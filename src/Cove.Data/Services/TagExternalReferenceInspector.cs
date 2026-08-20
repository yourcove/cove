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

public interface ITagExternalReferenceInspector
{
    Task<IReadOnlyList<TagExternalReferenceDto>> InspectAsync(
        IReadOnlyCollection<int> tagIds,
        CancellationToken ct = default);

    Task ApplyResolutionsAsync(
        int targetTagId,
        IReadOnlyCollection<TagExternalReferenceResolutionDto> resolutions,
        CancellationToken ct = default);
}

public sealed class TagExternalReferenceRepairException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Finds database foreign keys to core tags that are not transferred by <see cref="TagMergeService"/>.
/// This is deliberately catalog-based so disabled extensions remain visible: their tables and foreign
/// keys outlive their loaded EF model. A false positive blocks a merge; a false negative could delete
/// extension data through a cascading foreign key.
/// </summary>
public sealed class PostgresTagExternalReferenceInspector(CoveContext db) : ITagExternalReferenceInspector
{
    private readonly record struct ReferenceLocation(
        string Schema,
        string Table,
        string Column,
        string DeleteBehavior)
    {
        public string Key => CreateReferenceKey(Schema, Table, Column);
    }

    public async Task<IReadOnlyList<TagExternalReferenceDto>> InspectAsync(
        IReadOnlyCollection<int> tagIds,
        CancellationToken ct = default)
    {
        var ids = tagIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length == 0
            || !db.Database.IsRelational()
            || db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
            return [];

        var tagEntity = db.Model.FindEntityType(typeof(Tag))
            ?? throw new InvalidOperationException("The tag entity is missing from the database model.");
        var tagTable = tagEntity.GetTableName()
            ?? throw new InvalidOperationException("The tag table is missing from the database model.");
        var tagSchema = tagEntity.GetSchema() ?? "public";
        var tagStore = StoreObjectIdentifier.Table(tagTable, tagEntity.GetSchema());
        var tagIdColumn = tagEntity.FindProperty(nameof(Tag.Id))?.GetColumnName(tagStore)
            ?? throw new InvalidOperationException("The tag identifier column is missing from the database model.");

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
                ? $"cove_tag_reference_inspection_{Guid.NewGuid():N}"
                : null;
            if (resetInspectionStateSavepoint != null)
                await transaction.SaveAsync(resetInspectionStateSavepoint, ct);

            TagExternalReferenceDto[] orderedResult;
            try
            {
                // PostgreSQL's row_security=off mode does not bypass policies for an unprivileged role.
                // It raises 42501 instead of returning a filtered result, which lets Cove represent the
                // location as uninspectable and block deletion rather than mistake hidden rows for zero.
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
                    tagSchema,
                    tagTable,
                    tagIdColumn,
                    ct);
                var handledLocations = LoadCoreHandledLocations();
                var externalLocations = locations
                    .Where(location => !handledLocations.Contains((location.Schema, location.Table, location.Column)))
                    .ToArray();
                var result = new List<TagExternalReferenceDto>();

                for (var locationIndex = 0; locationIndex < externalLocations.Length; locationIndex++)
                {
                    var location = externalLocations[locationIndex];
                    var savepoint = $"cove_tag_reference_probe_{locationIndex}";
                    await transaction.SaveAsync(savepoint, ct);
                    var sql = $"""
                        SELECT {QuoteIdentifier(location.Column)}, COUNT(*)::integer
                        FROM {QuoteIdentifier(location.Schema)}.{QuoteIdentifier(location.Table)}
                        WHERE {QuoteIdentifier(location.Column)} = ANY (@tag_ids)
                        GROUP BY {QuoteIdentifier(location.Column)}
                        """;
                    try
                    {
                        await using var command = new NpgsqlCommand(sql, connection, transaction);
                        command.Parameters.Add(new NpgsqlParameter<int[]>("tag_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                        {
                            TypedValue = ids,
                        });
                        await using (var reader = await command.ExecuteReaderAsync(ct))
                        {
                            while (await reader.ReadAsync(ct))
                            {
                                var tagId = reader.GetInt32(0);
                                if (Array.BinarySearch(ids, tagId) < 0)
                                    continue;
                                result.Add(new TagExternalReferenceDto(
                                    tagId,
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
                            ? TagExternalReferenceAccessLimitations.RowLevelSecurity
                            : TagExternalReferenceAccessLimitations.DatabasePermission;
                        foreach (var tagId in ids)
                        {
                            result.Add(new TagExternalReferenceDto(
                                tagId,
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
                    .OrderBy(reference => reference.TagId)
                    .ThenBy(reference => reference.SchemaName, StringComparer.Ordinal)
                    .ThenBy(reference => reference.TableName, StringComparer.Ordinal)
                    .ThenBy(reference => reference.ColumnName, StringComparer.Ordinal)
                    .ToArray();
            }
            finally
            {
                if (resetInspectionStateSavepoint != null)
                {
                    // The caller owns this transaction. Roll back the inspection scope so the
                    // row_security setting cannot alter later merge/cleanup queries.
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
        int targetTagId,
        IReadOnlyCollection<TagExternalReferenceResolutionDto> resolutions,
        CancellationToken ct = default)
    {
        if (resolutions.Count == 0)
            return;
        if (targetTagId <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetTagId));
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Non-core tag-reference repairs require the cleanup transaction.");
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Non-core tag-reference repairs require PostgreSQL.");

        var requested = resolutions
            .OrderBy(resolution => resolution.TagId)
            .ThenBy(resolution => resolution.ReferenceKey, StringComparer.Ordinal)
            .ToArray();
        if (requested.Any(resolution => resolution.TagId <= 0 || resolution.TagId == targetTagId))
            throw new ArgumentException("Only source-tag references can be repaired.", nameof(resolutions));
        if (requested.GroupBy(resolution => (resolution.TagId, resolution.ReferenceKey)).Any(group => group.Count() > 1))
            throw new ArgumentException("A non-core reference location can have only one repair action per source tag.", nameof(resolutions));
        if (requested.Any(resolution => resolution.Action is not TagExternalReferenceActions.UpdateToSurvivor
            and not TagExternalReferenceActions.DeleteRows))
            throw new ArgumentException("The requested non-core reference action is not valid.", nameof(resolutions));

        var sourceIds = requested.Select(resolution => resolution.TagId).Distinct().Order().ToArray();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction();
        var tagEntity = db.Model.FindEntityType(typeof(Tag))
            ?? throw new InvalidOperationException("The tag entity is missing from the database model.");
        var tagTable = tagEntity.GetTableName()
            ?? throw new InvalidOperationException("The tag table is missing from the database model.");
        var tagSchema = tagEntity.GetSchema() ?? "public";
        var tagStore = StoreObjectIdentifier.Table(tagTable, tagEntity.GetSchema());
        var tagIdColumn = tagEntity.FindProperty(nameof(Tag.Id))?.GetColumnName(tagStore)
            ?? throw new InvalidOperationException("The tag identifier column is missing from the database model.");
        var requestedTagIds = sourceIds.Append(targetTagId).Distinct().Order().ToArray();
        var lockTagsSql = $"SELECT {QuoteIdentifier(tagIdColumn)} FROM {QuoteIdentifier(tagSchema)}.{QuoteIdentifier(tagTable)} WHERE {QuoteIdentifier(tagIdColumn)} = ANY (@tag_ids) ORDER BY {QuoteIdentifier(tagIdColumn)} FOR UPDATE";
        await using (var lockTagsCommand = new NpgsqlCommand(lockTagsSql, connection, transaction))
        {
            lockTagsCommand.Parameters.Add(new NpgsqlParameter<int[]>("tag_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                TypedValue = requestedTagIds,
            });
            var lockedTagIds = new List<int>();
            await using var reader = await lockTagsCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                lockedTagIds.Add(reader.GetInt32(0));
            if (!lockedTagIds.SequenceEqual(requestedTagIds))
                throw new TagExternalReferenceRepairException(
                    "The selected survivor or a source tag changed. Refresh the scan and try again.");
        }

        var current = await InspectAsync(sourceIds, ct);
        if (current.Any(reference => reference.AccessLimitation != null || reference.RowCount == null))
            throw new TagExternalReferenceRepairException(
                "A non-core table cannot be inspected or repaired safely because of row-level security or database permissions. Use the owning extension or a database administrator before merging this tag.");
        var currentByIdentity = current.ToDictionary(
            reference => (reference.TagId, reference.ReferenceKey));
        if (current.Count != requested.Length
            || requested.Any(resolution => !currentByIdentity.ContainsKey((resolution.TagId, resolution.ReferenceKey))))
            throw new TagExternalReferenceRepairException(
                "The non-core tag references changed. Refresh the scan and review every table before trying again.");

        var locations = requested
            .Select(resolution => currentByIdentity[(resolution.TagId, resolution.ReferenceKey)])
            .DistinctBy(reference => (reference.SchemaName, reference.TableName))
            .OrderBy(reference => reference.SchemaName, StringComparer.Ordinal)
            .ThenBy(reference => reference.TableName, StringComparer.Ordinal)
            .ToArray();

        try
        {
            // Hold writers out of every selected extension table until the core merge commits. This
            // prevents a newly inserted source reference from slipping between repair and deletion.
            foreach (var location in locations)
            {
                var lockSql = $"LOCK TABLE {QuoteIdentifier(location.SchemaName)}.{QuoteIdentifier(location.TableName)} IN SHARE ROW EXCLUSIVE MODE";
                await using var lockCommand = new NpgsqlCommand(lockSql, connection, transaction);
                await lockCommand.ExecuteNonQueryAsync(ct);
            }

            var tablesWithPriorDelete = new HashSet<(string Schema, string Table)>();
            var executionOrder = requested
                // Update every selected column before deleting any row. If one row references a
                // source through multiple columns, an explicit delete still wins, while updates in
                // its other columns are not accidentally skipped just because catalog order varied.
                .OrderBy(resolution => resolution.Action == TagExternalReferenceActions.DeleteRows ? 1 : 0)
                .ThenBy(resolution => resolution.TagId)
                .ThenBy(resolution => resolution.ReferenceKey, StringComparer.Ordinal);
            foreach (var resolution in executionOrder)
            {
                var reference = currentByIdentity[(resolution.TagId, resolution.ReferenceKey)];
                var tableIdentity = (reference.SchemaName, reference.TableName);
                var qualifiedTable = $"{QuoteIdentifier(reference.SchemaName)}.{QuoteIdentifier(reference.TableName)}";
                var column = QuoteIdentifier(reference.ColumnName);
                var sql = resolution.Action == TagExternalReferenceActions.UpdateToSurvivor
                    ? $"UPDATE {qualifiedTable} SET {column} = @target_tag_id WHERE {column} = @source_tag_id"
                    : $"DELETE FROM {qualifiedTable} WHERE {column} = @source_tag_id";
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                if (resolution.Action == TagExternalReferenceActions.UpdateToSurvivor)
                    command.Parameters.AddWithValue("target_tag_id", NpgsqlDbType.Integer, targetTagId);
                command.Parameters.AddWithValue("source_tag_id", NpgsqlDbType.Integer, resolution.TagId);
                var affected = await command.ExecuteNonQueryAsync(ct);
                var expectedRowCount = reference.RowCount!.Value;
                var priorDeleteCanExplainMissingRows = tablesWithPriorDelete.Contains(tableIdentity);
                if (affected > expectedRowCount
                    || (affected < expectedRowCount && !priorDeleteCanExplainMissingRows))
                    throw new TagExternalReferenceRepairException(
                        "The non-core tag references changed while the repair was running. No cleanup changes were committed.");
                if (resolution.Action == TagExternalReferenceActions.DeleteRows)
                    tablesWithPriorDelete.Add(tableIdentity);
            }

            var remaining = await InspectAsync(sourceIds, ct);
            if (remaining.Count > 0)
                throw new TagExternalReferenceRepairException(
                    "Some non-core tag references remain after the selected repairs. No cleanup changes were committed.");
        }
        catch (PostgresException exception)
        {
            throw new TagExternalReferenceRepairException(
                "The database rejected a selected non-core tag-reference repair. No cleanup changes were committed. Choose a different action or use the owning extension.",
                exception);
        }
    }

    private async Task<IReadOnlyList<ReferenceLocation>> LoadReferenceLocationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tagSchema,
        string tagTable,
        string tagIdColumn,
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
              AND principal_schema.nspname = @tag_schema
              AND principal_table.relname = @tag_table
              AND principal_column.attname = @tag_id_column
            ORDER BY dependent_schema.nspname, dependent_table.relname, dependent_column.attname, foreign_key.oid
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tag_schema", tagSchema);
        command.Parameters.AddWithValue("tag_table", tagTable);
        command.Parameters.AddWithValue("tag_id_column", tagIdColumn);
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

    private HashSet<(string Schema, string Table, string Column)> LoadCoreHandledLocations()
    {
        (Type DependentType, string PropertyName)[] handledReferences =
        [
            (typeof(AudioTag), nameof(AudioTag.TagId)),
            (typeof(GalleryTag), nameof(GalleryTag.TagId)),
            (typeof(GroupTag), nameof(GroupTag.TagId)),
            (typeof(ImageTag), nameof(ImageTag.TagId)),
            (typeof(PerformerTag), nameof(PerformerTag.TagId)),
            (typeof(SegmentDisplayRule), nameof(SegmentDisplayRule.TagId)),
            (typeof(Segment), nameof(Segment.TagId)),
            (typeof(StudioTag), nameof(StudioTag.TagId)),
            (typeof(TagAlias), nameof(TagAlias.TagId)),
            (typeof(TagApplication), nameof(TagApplication.TagId)),
            (typeof(TagParent), nameof(TagParent.ParentId)),
            (typeof(TagParent), nameof(TagParent.ChildId)),
            (typeof(TagRemoteId), nameof(TagRemoteId.TagId)),
            (typeof(TextTag), nameof(TextTag.TagId)),
            (typeof(VideoTag), nameof(VideoTag.TagId)),
        ];
        var locations = new HashSet<(string Schema, string Table, string Column)>();
        foreach (var reference in handledReferences)
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
                || !property.GetContainingForeignKeys().Any(key => key.PrincipalEntityType.ClrType == typeof(Tag)))
                continue;
            var column = property.GetColumnName(store);
            if (column != null)
                locations.Add((schema, table, column));
        }

        return locations;
    }

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
