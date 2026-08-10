using System.Data;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Cove.Data.Services;

public interface ITagExternalReferenceInspector
{
    Task<IReadOnlyDictionary<int, int>> CountAsync(
        IReadOnlyCollection<int> tagIds,
        CancellationToken ct = default);
}

/// <summary>
/// Finds database foreign keys to core tags that are not transferred by <see cref="TagMergeService"/>.
/// This is deliberately catalog-based so disabled extensions remain visible: their tables and foreign
/// keys outlive their loaded EF model. A false positive blocks a merge; a false negative could delete
/// extension data through a cascading foreign key.
/// </summary>
public sealed class PostgresTagExternalReferenceInspector(CoveContext db) : ITagExternalReferenceInspector
{
    private readonly record struct ReferenceLocation(string Schema, string Table, string Column);

    public async Task<IReadOnlyDictionary<int, int>> CountAsync(
        IReadOnlyCollection<int> tagIds,
        CancellationToken ct = default)
    {
        var ids = tagIds.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length == 0
            || !db.Database.IsRelational()
            || db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
            return new Dictionary<int, int>();

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

        try
        {
            var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            var locations = await LoadReferenceLocationsAsync(
                connection,
                transaction,
                tagSchema,
                tagTable,
                tagIdColumn,
                ct);
            var handledLocations = LoadCoreHandledLocations();
            var externalLocations = locations.Where(location => !handledLocations.Contains(location)).ToArray();
            var counts = ids.ToDictionary(id => id, _ => 0);

            foreach (var location in externalLocations)
            {
                var sql = $"""
                    SELECT {QuoteIdentifier(location.Column)}, COUNT(*)::integer
                    FROM {QuoteIdentifier(location.Schema)}.{QuoteIdentifier(location.Table)}
                    WHERE {QuoteIdentifier(location.Column)} = ANY (@tag_ids)
                    GROUP BY {QuoteIdentifier(location.Column)}
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.Add(new NpgsqlParameter<int[]>("tag_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer)
                {
                    TypedValue = ids,
                });
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var tagId = reader.GetInt32(0);
                    if (counts.ContainsKey(tagId))
                        counts[tagId] = checked(counts[tagId] + reader.GetInt32(1));
                }
            }

            return counts;
        }
        finally
        {
            if (openedHere)
                await db.Database.CloseConnectionAsync();
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
            SELECT DISTINCT dependent_schema.nspname, dependent_table.relname, dependent_column.attname
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
              AND principal_schema.nspname = @tag_schema
              AND principal_table.relname = @tag_table
              AND principal_column.attname = @tag_id_column
            ORDER BY dependent_schema.nspname, dependent_table.relname, dependent_column.attname
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tag_schema", tagSchema);
        command.Parameters.AddWithValue("tag_table", tagTable);
        command.Parameters.AddWithValue("tag_id_column", tagIdColumn);
        var result = new List<ReferenceLocation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ReferenceLocation(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    private HashSet<ReferenceLocation> LoadCoreHandledLocations()
    {
        Type[] handledDependentTypes =
        [
            typeof(AudioTag),
            typeof(GalleryTag),
            typeof(GroupTag),
            typeof(ImageTag),
            typeof(PerformerTag),
            typeof(SegmentDisplayRule),
            typeof(Segment),
            typeof(StudioTag),
            typeof(TagAlias),
            typeof(TagApplication),
            typeof(TagParent),
            typeof(TagRemoteId),
            typeof(TextTag),
            typeof(VideoTag),
        ];
        var locations = new HashSet<ReferenceLocation>();
        foreach (var dependentType in handledDependentTypes)
        {
            var entityType = db.Model.FindEntityType(dependentType);
            if (entityType == null)
                continue;

            var table = entityType.GetTableName();
            if (table == null)
                continue;
            var schema = entityType.GetSchema() ?? "public";
            var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            foreach (var foreignKey in entityType.GetForeignKeys().Where(key => key.PrincipalEntityType.ClrType == typeof(Tag)))
            foreach (var property in foreignKey.Properties)
            {
                var column = property.GetColumnName(store);
                if (column != null)
                    locations.Add(new ReferenceLocation(schema, table, column));
            }
        }

        return locations;
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
