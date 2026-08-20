using System.Data;
using System.Security.Cryptography;
using System.Text;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Cove.Data.Services;

public interface IEntityExternalReferenceInspector
{
    Task<IReadOnlyList<EntityExternalReferenceDto>> InspectAsync(
        string entityType,
        IReadOnlyCollection<int> entityIds,
        CancellationToken ct = default);

    Task ApplyResolutionsAsync(
        string entityType,
        int targetEntityId,
        IReadOnlyCollection<EntityExternalReferenceResolutionDto> resolutions,
        CancellationToken ct = default);
}

public sealed class EntityExternalReferenceRepairException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Catalog-backed safeguard for foreign keys owned outside Cove core. Disabled extensions still leave
/// their tables and constraints in PostgreSQL, so inspecting the catalog is more reliable than the
/// currently loaded EF model. Locations hidden by RLS or permissions fail closed.
/// </summary>
public sealed class PostgresEntityExternalReferenceInspector(CoveContext db) : IEntityExternalReferenceInspector
{
    private sealed record PrincipalDescriptor(
        string EntityType,
        Type ClrType,
        string Noun,
        CoreHandledReference[] CoreHandledReferences);

    private sealed record CoreHandledReference(Type DependentType, string PropertyName);

    private readonly record struct ReferenceLocation(
        string Schema,
        string Table,
        string Column,
        string DeleteBehavior)
    {
        public string Key => CreateReferenceKey(Schema, Table, Column);
    }

    public async Task<IReadOnlyList<EntityExternalReferenceDto>> InspectAsync(
        string entityType,
        IReadOnlyCollection<int> entityIds,
        CancellationToken ct = default)
    {
        var descriptor = Describe(entityType);
        var ids = entityIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length == 0
            || !db.Database.IsRelational()
            || db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
            return [];

        var principal = GetPrincipalStore(descriptor);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await db.Database.OpenConnectionAsync(ct);

        NpgsqlTransaction? ownedInspectionTransaction = null;
        try
        {
            var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            if (transaction == null)
            {
                ownedInspectionTransaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    ct);
                transaction = ownedInspectionTransaction;
            }

            var resetInspectionStateSavepoint = ownedInspectionTransaction == null
                ? $"cove_entity_reference_inspection_{Guid.NewGuid():N}"
                : null;
            if (resetInspectionStateSavepoint != null)
                await transaction.SaveAsync(resetInspectionStateSavepoint, ct);

            EntityExternalReferenceDto[] orderedResult;
            try
            {
                // An unprivileged role cannot actually bypass RLS with this setting. PostgreSQL raises
                // 42501 instead of returning an incomplete count, which lets Cove block safely.
                await using (var rowSecurityCommand = new NpgsqlCommand(
                    "SET LOCAL row_security = off",
                    connection,
                    transaction))
                {
                    await rowSecurityCommand.ExecuteNonQueryAsync(ct);
                }

                var locations = await LoadReferenceLocationsAsync(
                    connection,
                    transaction,
                    principal.Schema,
                    principal.Table,
                    principal.IdColumn,
                    ct);
                var handledLocations = LoadCoreHandledLocations(descriptor);
                var externalLocations = locations
                    .Where(location => !handledLocations.Contains((location.Schema, location.Table, location.Column)))
                    .ToArray();
                var result = new List<EntityExternalReferenceDto>();

                for (var locationIndex = 0; locationIndex < externalLocations.Length; locationIndex++)
                {
                    var location = externalLocations[locationIndex];
                    var savepoint = $"cove_entity_reference_probe_{locationIndex}";
                    await transaction.SaveAsync(savepoint, ct);
                    var sql = $"""
                        SELECT {QuoteIdentifier(location.Column)}, COUNT(*)::integer
                        FROM {QuoteIdentifier(location.Schema)}.{QuoteIdentifier(location.Table)}
                        WHERE {QuoteIdentifier(location.Column)} = ANY (@entity_ids)
                        GROUP BY {QuoteIdentifier(location.Column)}
                        """;
                    try
                    {
                        await using var command = new NpgsqlCommand(sql, connection, transaction);
                        command.Parameters.Add(new NpgsqlParameter<int[]>("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                        {
                            TypedValue = ids,
                        });
                        await using (var reader = await command.ExecuteReaderAsync(ct))
                        {
                            while (await reader.ReadAsync(ct))
                            {
                                var entityId = reader.GetInt32(0);
                                if (Array.BinarySearch(ids, entityId) < 0)
                                    continue;
                                result.Add(new EntityExternalReferenceDto(
                                    entityId,
                                    location.Key,
                                    location.Schema,
                                    location.Table,
                                    location.Column,
                                    location.DeleteBehavior,
                                    reader.GetInt32(1)));
                            }
                        }

                        await transaction.ReleaseAsync(savepoint, ct);
                    }
                    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
                    {
                        await transaction.RollbackAsync(savepoint, CancellationToken.None);
                        await transaction.ReleaseAsync(savepoint, CancellationToken.None);
                        var limitation = exception.MessageText.Contains(
                            "row-level security",
                            StringComparison.OrdinalIgnoreCase)
                            ? EntityExternalReferenceAccessLimitations.RowLevelSecurity
                            : EntityExternalReferenceAccessLimitations.DatabasePermission;
                        foreach (var entityId in ids)
                        {
                            result.Add(new EntityExternalReferenceDto(
                                entityId,
                                location.Key,
                                location.Schema,
                                location.Table,
                                location.Column,
                                location.DeleteBehavior,
                                null,
                                limitation));
                        }
                    }
                }

                orderedResult = result
                    .OrderBy(reference => reference.EntityId)
                    .ThenBy(reference => reference.SchemaName, StringComparer.Ordinal)
                    .ThenBy(reference => reference.TableName, StringComparer.Ordinal)
                    .ThenBy(reference => reference.ColumnName, StringComparer.Ordinal)
                    .ToArray();
            }
            finally
            {
                if (resetInspectionStateSavepoint != null)
                {
                    await transaction.RollbackAsync(resetInspectionStateSavepoint, CancellationToken.None);
                    await transaction.ReleaseAsync(resetInspectionStateSavepoint, CancellationToken.None);
                }
            }

            if (ownedInspectionTransaction != null)
                await ownedInspectionTransaction.CommitAsync(ct);
            return orderedResult;
        }
        finally
        {
            if (ownedInspectionTransaction != null)
                await ownedInspectionTransaction.DisposeAsync();
            if (openedHere)
                await db.Database.CloseConnectionAsync();
        }
    }

    public async Task ApplyResolutionsAsync(
        string entityType,
        int targetEntityId,
        IReadOnlyCollection<EntityExternalReferenceResolutionDto> resolutions,
        CancellationToken ct = default)
    {
        var descriptor = Describe(entityType);
        if (resolutions.Count == 0)
            return;
        if (targetEntityId <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetEntityId));
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Non-core reference repairs require the cleanup transaction.");
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Non-core reference repairs require PostgreSQL.");

        var requested = resolutions
            .OrderBy(resolution => resolution.EntityId)
            .ThenBy(resolution => resolution.ReferenceKey, StringComparer.Ordinal)
            .ToArray();
        if (requested.Any(resolution => resolution.EntityId <= 0 || resolution.EntityId == targetEntityId))
            throw new ArgumentException("Only source-entity references can be repaired.", nameof(resolutions));
        if (requested.GroupBy(resolution => (resolution.EntityId, resolution.ReferenceKey)).Any(group => group.Count() > 1))
            throw new ArgumentException("A non-core reference location can have only one repair action per source entity.", nameof(resolutions));
        if (requested.Any(resolution => resolution.Action is not EntityExternalReferenceActions.UpdateToSurvivor
            and not EntityExternalReferenceActions.DeleteRows))
            throw new ArgumentException("The requested non-core reference action is not valid.", nameof(resolutions));

        var sourceIds = requested.Select(resolution => resolution.EntityId).Distinct().Order().ToArray();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction();
        var principal = GetPrincipalStore(descriptor);
        var requestedEntityIds = sourceIds.Append(targetEntityId).Distinct().Order().ToArray();
        var lockEntitiesSql = $"SELECT {QuoteIdentifier(principal.IdColumn)} FROM {QuoteIdentifier(principal.Schema)}.{QuoteIdentifier(principal.Table)} WHERE {QuoteIdentifier(principal.IdColumn)} = ANY (@entity_ids) ORDER BY {QuoteIdentifier(principal.IdColumn)} FOR UPDATE";
        await using (var lockEntitiesCommand = new NpgsqlCommand(lockEntitiesSql, connection, transaction))
        {
            lockEntitiesCommand.Parameters.Add(new NpgsqlParameter<int[]>("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                TypedValue = requestedEntityIds,
            });
            var lockedEntityIds = new List<int>();
            await using var reader = await lockEntitiesCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                lockedEntityIds.Add(reader.GetInt32(0));
            if (!lockedEntityIds.SequenceEqual(requestedEntityIds))
                throw new EntityExternalReferenceRepairException(
                    $"The selected survivor or a source {descriptor.Noun} changed. Refresh the scan and try again.");
        }

        var current = await InspectAsync(entityType, sourceIds, ct);
        if (current.Any(reference => reference.AccessLimitation != null || reference.RowCount == null))
            throw new EntityExternalReferenceRepairException(
                $"A non-core table cannot be inspected or repaired safely because of row-level security or database permissions. Use the owning extension or a database administrator before merging this {descriptor.Noun}.");
        var currentByIdentity = current.ToDictionary(
            reference => (reference.EntityId, reference.ReferenceKey));
        if (current.Count != requested.Length
            || requested.Any(resolution => !currentByIdentity.ContainsKey((resolution.EntityId, resolution.ReferenceKey))))
            throw new EntityExternalReferenceRepairException(
                $"The non-core {descriptor.Noun} references changed. Refresh the scan and review every table before trying again.");

        var locations = requested
            .Select(resolution => currentByIdentity[(resolution.EntityId, resolution.ReferenceKey)])
            .DistinctBy(reference => (reference.SchemaName, reference.TableName))
            .OrderBy(reference => reference.SchemaName, StringComparer.Ordinal)
            .ThenBy(reference => reference.TableName, StringComparer.Ordinal)
            .ToArray();

        try
        {
            foreach (var location in locations)
            {
                var lockSql = $"LOCK TABLE {QuoteIdentifier(location.SchemaName)}.{QuoteIdentifier(location.TableName)} IN SHARE ROW EXCLUSIVE MODE";
                await using var lockCommand = new NpgsqlCommand(lockSql, connection, transaction);
                await lockCommand.ExecuteNonQueryAsync(ct);
            }

            var tablesWithPriorDelete = new HashSet<(string Schema, string Table)>();
            var executionOrder = requested
                .OrderBy(resolution => resolution.Action == EntityExternalReferenceActions.DeleteRows ? 1 : 0)
                .ThenBy(resolution => resolution.EntityId)
                .ThenBy(resolution => resolution.ReferenceKey, StringComparer.Ordinal);
            foreach (var resolution in executionOrder)
            {
                var reference = currentByIdentity[(resolution.EntityId, resolution.ReferenceKey)];
                var tableIdentity = (reference.SchemaName, reference.TableName);
                var qualifiedTable = $"{QuoteIdentifier(reference.SchemaName)}.{QuoteIdentifier(reference.TableName)}";
                var column = QuoteIdentifier(reference.ColumnName);
                var sql = resolution.Action == EntityExternalReferenceActions.UpdateToSurvivor
                    ? $"UPDATE {qualifiedTable} SET {column} = @target_entity_id WHERE {column} = @source_entity_id"
                    : $"DELETE FROM {qualifiedTable} WHERE {column} = @source_entity_id";
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                if (resolution.Action == EntityExternalReferenceActions.UpdateToSurvivor)
                    command.Parameters.AddWithValue("target_entity_id", NpgsqlDbType.Integer, targetEntityId);
                command.Parameters.AddWithValue("source_entity_id", NpgsqlDbType.Integer, resolution.EntityId);
                var affected = await command.ExecuteNonQueryAsync(ct);
                var expectedRowCount = reference.RowCount!.Value;
                var priorDeleteCanExplainMissingRows = tablesWithPriorDelete.Contains(tableIdentity);
                if (affected > expectedRowCount
                    || (affected < expectedRowCount && !priorDeleteCanExplainMissingRows))
                    throw new EntityExternalReferenceRepairException(
                        $"The non-core {descriptor.Noun} references changed while the repair was running. No cleanup changes were committed.");
                if (resolution.Action == EntityExternalReferenceActions.DeleteRows)
                    tablesWithPriorDelete.Add(tableIdentity);
            }

            var remaining = await InspectAsync(entityType, sourceIds, ct);
            if (remaining.Count > 0)
                throw new EntityExternalReferenceRepairException(
                    $"Some non-core {descriptor.Noun} references remain after the selected repairs. No cleanup changes were committed.");
        }
        catch (PostgresException exception)
        {
            throw new EntityExternalReferenceRepairException(
                $"The database rejected a selected non-core {descriptor.Noun}-reference repair. No cleanup changes were committed. Choose a different action or use the owning extension.",
                exception);
        }
    }

    private async Task<IReadOnlyList<ReferenceLocation>> LoadReferenceLocationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string principalSchema,
        string principalTable,
        string principalIdColumn,
        CancellationToken ct)
    {
        const string sql = """
            SELECT dependent_schema.nspname,
                   dependent_table.relname,
                   dependent_column.attname,
                   foreign_key.confdeltype
            FROM pg_constraint AS foreign_key
            JOIN pg_class AS dependent_table ON dependent_table.oid = foreign_key.conrelid
            JOIN pg_namespace AS dependent_schema ON dependent_schema.oid = dependent_table.relnamespace
            JOIN pg_class AS principal_table ON principal_table.oid = foreign_key.confrelid
            JOIN pg_namespace AS principal_schema ON principal_schema.oid = principal_table.relnamespace
            JOIN LATERAL unnest(foreign_key.conkey) WITH ORDINALITY AS dependent_key(attnum, ordinal) ON TRUE
            JOIN LATERAL unnest(foreign_key.confkey) WITH ORDINALITY AS principal_key(attnum, ordinal)
              ON principal_key.ordinal = dependent_key.ordinal
            JOIN pg_attribute AS dependent_column
              ON dependent_column.attrelid = dependent_table.oid
             AND dependent_column.attnum = dependent_key.attnum
            JOIN pg_attribute AS principal_column
              ON principal_column.attrelid = principal_table.oid
             AND principal_column.attnum = principal_key.attnum
            WHERE foreign_key.contype = 'f'
              AND foreign_key.conparentid = 0
              AND principal_schema.nspname = @principal_schema
              AND principal_table.relname = @principal_table
              AND principal_column.attname = @principal_id_column
            ORDER BY dependent_schema.nspname, dependent_table.relname, dependent_column.attname, foreign_key.oid
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("principal_schema", principalSchema);
        command.Parameters.AddWithValue("principal_table", principalTable);
        command.Parameters.AddWithValue("principal_id_column", principalIdColumn);
        var result = new List<ReferenceLocation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ReferenceLocation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DescribeDeleteBehavior(reader.GetChar(3))));
        return result
            .GroupBy(location => (location.Schema, location.Table, location.Column))
            .Select(group =>
            {
                var behaviors = group.Select(location => location.DeleteBehavior).Distinct(StringComparer.Ordinal).ToArray();
                var first = group.First();
                return first with { DeleteBehavior = behaviors.Length == 1 ? behaviors[0] : "mixed" };
            })
            .OrderBy(location => location.Schema, StringComparer.Ordinal)
            .ThenBy(location => location.Table, StringComparer.Ordinal)
            .ThenBy(location => location.Column, StringComparer.Ordinal)
            .ToArray();
    }

    private HashSet<(string Schema, string Table, string Column)> LoadCoreHandledLocations(
        PrincipalDescriptor descriptor)
    {
        var locations = new HashSet<(string Schema, string Table, string Column)>();
        foreach (var reference in descriptor.CoreHandledReferences)
        {
            var entityType = db.Model.FindEntityType(reference.DependentType);
            if (entityType == null)
                continue;

            var table = entityType.GetTableName();
            if (table == null)
                continue;
            var schema = entityType.GetSchema() ?? "public";
            var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            var property = entityType.FindProperty(reference.PropertyName);
            if (property == null
                || !property.GetContainingForeignKeys().Any(key => key.PrincipalEntityType.ClrType == descriptor.ClrType))
                continue;
            var column = property.GetColumnName(store);
            if (column != null)
                locations.Add((schema, table, column));
        }

        return locations;
    }

    private (string Schema, string Table, string IdColumn) GetPrincipalStore(PrincipalDescriptor descriptor)
    {
        var entity = db.Model.FindEntityType(descriptor.ClrType)
            ?? throw new InvalidOperationException($"The {descriptor.Noun} entity is missing from the database model.");
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"The {descriptor.Noun} table is missing from the database model.");
        var schema = entity.GetSchema() ?? "public";
        var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
        var idColumn = entity.FindProperty(nameof(BaseEntity.Id))?.GetColumnName(store)
            ?? throw new InvalidOperationException($"The {descriptor.Noun} identifier column is missing from the database model.");
        return (schema, table, idColumn);
    }

    private static PrincipalDescriptor Describe(string entityType)
        => entityType switch
        {
            NameConflictEntityTypes.Performer => new(
                entityType,
                typeof(Performer),
                "performer",
                [
                    new(typeof(AudioPerformer), nameof(AudioPerformer.PerformerId)),
                    new(typeof(Face), nameof(Face.PerformerId)),
                    new(typeof(FaceSuggestionDecision), nameof(FaceSuggestionDecision.PerformerId)),
                    new(typeof(GalleryPerformer), nameof(GalleryPerformer.PerformerId)),
                    new(typeof(ImagePerformer), nameof(ImagePerformer.PerformerId)),
                    new(typeof(PerformerAlias), nameof(PerformerAlias.PerformerId)),
                    new(typeof(PerformerRemoteId), nameof(PerformerRemoteId.PerformerId)),
                    new(typeof(PerformerTag), nameof(PerformerTag.PerformerId)),
                    new(typeof(PerformerUrl), nameof(PerformerUrl.PerformerId)),
                    new(typeof(TextPerformer), nameof(TextPerformer.PerformerId)),
                    new(typeof(VideoPerformer), nameof(VideoPerformer.PerformerId)),
                ]),
            NameConflictEntityTypes.Studio => new(
                entityType,
                typeof(Studio),
                "studio",
                [
                    new(typeof(Audio), nameof(Audio.StudioId)),
                    new(typeof(Gallery), nameof(Gallery.StudioId)),
                    new(typeof(Group), nameof(Group.StudioId)),
                    new(typeof(Image), nameof(Image.StudioId)),
                    new(typeof(Studio), nameof(Studio.ParentId)),
                    new(typeof(StudioAlias), nameof(StudioAlias.StudioId)),
                    new(typeof(StudioRemoteId), nameof(StudioRemoteId.StudioId)),
                    new(typeof(StudioTag), nameof(StudioTag.StudioId)),
                    new(typeof(StudioUrl), nameof(StudioUrl.StudioId)),
                    new(typeof(TextDocument), nameof(TextDocument.StudioId)),
                    new(typeof(Video), nameof(Video.StudioId)),
                ]),
            _ => throw new ArgumentException(
                "The requested entity type does not support external-reference inspection.",
                nameof(entityType)),
        };

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string CreateReferenceKey(string schema, string table, string column)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{schema}\0{table}\0{column}"));
        return $"foreign-key-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string DescribeDeleteBehavior(char behavior)
        => behavior switch
        {
            'a' => "no action",
            'r' => "restrict",
            'c' => "cascade",
            'n' => "set null",
            'd' => "set default",
            _ => "unknown",
        };
}
