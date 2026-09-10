using System.Runtime.CompilerServices;
using System.Text.Json;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Cove.Api.Services;

/// <summary>Attempt-local PostgreSQL staging, owned and cleaned up by the import transaction.</summary>
internal sealed class MetadataImportStage(NpgsqlConnection connection)
{
    private readonly NpgsqlConnection connection = connection;
    internal const int BatchSize = 256;

    internal static async Task<MetadataImportStage> CreateAsync(CoveContext db, Stream input, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Metadata staging requires the import transaction.");
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var stage = new MetadataImportStage(connection);
        await stage.ExecuteAsync("""
            CREATE TEMP TABLE cove_import_raw (
                sequence bigint PRIMARY KEY, section text NOT NULL, occurrence bigint NOT NULL, json text NOT NULL
            ) ON COMMIT DROP;
            CREATE TEMP TABLE cove_import_normalized (
                sequence bigint PRIMARY KEY, section text NOT NULL, identity text COLLATE "C",
                first_name text, first_disambiguation text, json text NOT NULL
            ) ON COMMIT DROP;
            CREATE TEMP TABLE cove_import_targets (section text NOT NULL, identity text COLLATE "C" NOT NULL, id integer NOT NULL) ON COMMIT DROP;
            CREATE INDEX ON cove_import_targets (section, md5(identity));
            CREATE TEMP TABLE cove_import_target_batch (LIKE cove_import_targets) ON COMMIT DROP;
            """, ct);

        // Only four section counters and invalid flags live in memory. Keep superseded
        // values raw: even an invalid entity must be ignored if a later section replaces it.
        var sections = new Dictionary<string, long>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        long sequence = 0;
        await using (var writer = await stage.connection.BeginBinaryImportAsync(
            "COPY pg_temp.cove_import_raw (sequence,section,occurrence,json) FROM STDIN (FORMAT BINARY)", ct))
        {
            await new MetadataImportReader().ReadAsync(input,
                (section, _) =>
                {
                    sections[section] = sections.GetValueOrDefault(section) + 1;
                    invalid.Remove(section);
                    return Task.CompletedTask;
                },
                async (section, value, token) =>
                {
                    await writer.StartRowAsync(token);
                    await writer.WriteAsync(++sequence, NpgsqlDbType.Bigint, token);
                    await writer.WriteAsync(section, NpgsqlDbType.Text, token);
                    await writer.WriteAsync(sections[section], NpgsqlDbType.Bigint, token);
                    await writer.WriteAsync(value.GetRawText(), NpgsqlDbType.Text, token);
                }, ct, (section, _) =>
                {
                    invalid.Add(section);
                    return Task.CompletedTask;
                });
            await writer.CompleteAsync(ct);
        }
        if (invalid.Count != 0)
            throw new JsonException("Supported metadata sections must be arrays or null.");
        foreach (var (section, occurrence) in sections)
        {
            await using var command = new NpgsqlCommand(
                "DELETE FROM pg_temp.cove_import_raw WHERE section=$1 AND occurrence<>$2", connection);
            command.Parameters.AddWithValue(section);
            command.Parameters.AddWithValue(occurrence);
            await command.ExecuteNonQueryAsync(ct);
        }

        long after = 0;
        while (true)
        {
            var rows = new List<(long Sequence, string Section, string Json)>(BatchSize);
            await using (var command = new NpgsqlCommand(
                "SELECT sequence,section,json FROM pg_temp.cove_import_raw WHERE sequence>$1 ORDER BY sequence LIMIT 256", connection))
            {
                command.Parameters.AddWithValue(after);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
            if (rows.Count == 0) break;
            after = rows[^1].Sequence;
            await using var writer = await connection.BeginBinaryImportAsync(
                "COPY pg_temp.cove_import_normalized (sequence,section,identity,first_name,first_disambiguation,json) FROM STDIN (FORMAT BINARY)", ct);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                string? identity = null, name = null, disambiguation = null;
                if (row.Section == "studios")
                {
                    var entity = JsonSerializer.Deserialize<Studio>(row.Json, CoveJson.Default)
                        ?? throw new JsonException("Null studio in metadata.");
                    identity = EntityNameRules.StudioIdentityKey(entity.Name);
                    name = entity.Name ?? EntityNameRules.NormalizeCanonicalName(null);
                }
                else if (row.Section == "performers")
                {
                    var entity = JsonSerializer.Deserialize<Performer>(row.Json, CoveJson.Default)
                        ?? throw new JsonException("Null performer in metadata.");
                    identity = EntityNameRules.PerformerIdentityKey(entity.Name, entity.Disambiguation);
                    name = entity.Name ?? EntityNameRules.NormalizeCanonicalName(null);
                    disambiguation = entity.Disambiguation;
                }
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(row.Sequence, NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(row.Section, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, identity, ct);
                await WriteNullableAsync(writer, name, ct);
                await WriteNullableAsync(writer, disambiguation, ct);
                await writer.WriteAsync(row.Json, NpgsqlDbType.Text, ct);
            }
            await writer.CompleteAsync(ct);
        }
        // Group on full ordinal identities without indexing their unrestricted text.
        // Tags/groups keep every source row; studios/performers keep first spelling and last metadata.
        await stage.ExecuteAsync("""
            CREATE TEMP TABLE cove_import_entities ON COMMIT DROP AS
            SELECT first.sequence, first.section, first.first_name, first.first_disambiguation, last.json
            FROM (
                SELECT min(sequence) AS first_sequence, max(sequence) AS last_sequence
                FROM pg_temp.cove_import_normalized
                GROUP BY section, identity, CASE WHEN identity IS NULL THEN sequence END
            ) grouped
            JOIN pg_temp.cove_import_normalized first ON first.sequence=grouped.first_sequence
            JOIN pg_temp.cove_import_normalized last ON last.sequence=grouped.last_sequence;
            CREATE INDEX ON cove_import_entities(section,sequence);
            DROP TABLE pg_temp.cove_import_raw, pg_temp.cove_import_normalized;
            ANALYZE pg_temp.cove_import_entities;
            """, ct);
        return stage;
    }

    internal async IAsyncEnumerable<List<TEntity>> ReadBatchesAsync<TEntity>(
        string section, [EnumeratorCancellation] CancellationToken ct) where TEntity : class
    {
        long after = 0;
        while (true)
        {
            var batch = new List<TEntity>(BatchSize);
            await using (var command = new NpgsqlCommand("""
                SELECT sequence,json,first_name,first_disambiguation FROM pg_temp.cove_import_entities
                WHERE section=$1 AND sequence>$2 ORDER BY sequence LIMIT 256
                """, connection))
            {
                command.Parameters.AddWithValue(section);
                command.Parameters.AddWithValue(after);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var entity = JsonSerializer.Deserialize<TEntity>(reader.GetString(1), CoveJson.Default)
                        ?? throw new JsonException("Null entity in metadata.");
                    if (entity is Studio studio)
                        studio.Name = reader.GetString(2);
                    else if (entity is Performer performer)
                    {
                        performer.Name = reader.GetString(2);
                        performer.Disambiguation = reader.IsDBNull(3) ? null : reader.GetString(3);
                    }
                    batch.Add(entity);
                    after = reader.GetInt64(0);
                }
            }
            if (batch.Count == 0) yield break;
            // The reader is closed before the caller saves or queries on this connection.
            yield return batch;
        }
    }

    internal async Task BuildTargetIndexAsync(CoveContext db, CancellationToken ct)
    {
        await ExecuteAsync("TRUNCATE pg_temp.cove_import_targets", ct);
        await IndexAsync(db.Studios.AsNoTracking(), "studios", entity => EntityNameRules.StudioIdentityKey(entity.Name), ct);
        await IndexAsync(db.Performers.AsNoTracking(), "performers", entity => EntityNameRules.PerformerIdentityKey(entity.Name, entity.Disambiguation), ct);
        await IndexAsync(db.Groups.AsNoTracking(), "groups", entity => entity.Name, ct);
        await IndexAsync(db.Tags.AsNoTracking().Include(tag => tag.Aliases), "tags", entity => TagNameRules.NamespaceKey(TagNameRules.NormalizeCanonicalName(entity.Name)), ct);
        await ExecuteAsync("ANALYZE pg_temp.cove_import_targets", ct);
    }

    private async Task IndexAsync<TEntity>(IQueryable<TEntity> query, string section,
        Func<TEntity, string> identity, CancellationToken ct) where TEntity : BaseEntity
    {
        await using var selected = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM pg_temp.cove_import_entities WHERE section=$1)", connection);
        selected.Parameters.AddWithValue(section);
        if (!(bool)(await selected.ExecuteScalarAsync(ct))!) return;
        int? afterId = null;
        while (true)
        {
            var batch = await query.Where(entity => afterId == null || entity.Id > afterId)
                .OrderBy(entity => entity.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            afterId = batch[^1].Id;
            await AddTargetRowsAsync(batch.SelectMany(entity =>
                new[] { (section, identity(entity), entity.Id) }
                    .Concat(entity is Tag tag ? TagAliasRows(tag) : [])), ct);
        }
    }

    internal Task AddTagsAsync(IEnumerable<Tag> tags, CancellationToken ct)
        => AddTargetRowsAsync(tags.SelectMany(tag =>
            new[] { ("tags", TagNameRules.NamespaceKey(TagNameRules.NormalizeCanonicalName(tag.Name)), tag.Id) }
                .Concat(TagAliasRows(tag))), ct);

    private static IEnumerable<(string Section, string Identity, int Id)> TagAliasRows(Tag tag)
        => tag.Aliases.Select(alias => TagNameRules.NormalizeAlias(alias.Alias)).OfType<string>()
            .Select(alias => ("tag-aliases", TagNameRules.NamespaceKey(alias), tag.Id));

    private async Task AddTargetRowsAsync(IEnumerable<(string Section, string Identity, int Id)> rows, CancellationToken ct)
    {
        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY pg_temp.cove_import_target_batch (section,identity,id) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var row in rows)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(row.Section, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(row.Identity, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(row.Id, NpgsqlDbType.Integer, ct);
            }
            await writer.CompleteAsync(ct);
        }
        await ExecuteAsync("""
            INSERT INTO pg_temp.cove_import_targets
            SELECT DISTINCT batch.section,batch.identity,batch.id FROM pg_temp.cove_import_target_batch batch
            WHERE NOT EXISTS (
                SELECT 1 FROM pg_temp.cove_import_targets target WHERE target.section=batch.section
                AND md5(target.identity)=md5(batch.identity) AND target.identity=batch.identity AND target.id=batch.id
            );
            TRUNCATE pg_temp.cove_import_target_batch;
            """, ct);
    }

    internal async Task<Dictionary<string, List<int>>> FindTargetIdsAsync(string section, IEnumerable<string> identities, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT requested.identity, matched.id FROM unnest($1::text[]) requested(identity)
            CROSS JOIN LATERAL (
                SELECT DISTINCT id FROM pg_temp.cove_import_targets target
                WHERE target.section=$2 AND md5(target.identity)=md5(requested.identity)
                    AND target.identity=requested.identity COLLATE "C"
                ORDER BY id LIMIT 2
            ) matched
            """, connection);
        command.Parameters.AddWithValue(identities.Distinct(StringComparer.Ordinal).ToArray());
        command.Parameters.AddWithValue(section);
        var matches = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetString(0);
            if (!matches.TryGetValue(key, out var ids)) matches[key] = ids = new List<int>(2);
            ids.Add(reader.GetInt32(1));
        }
        return matches;
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
        => value == null ? writer.WriteNullAsync(ct) : writer.WriteAsync(value, NpgsqlDbType.Text, ct);
}
