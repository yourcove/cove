using System.Globalization;
using System.Text;
using Cove.Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public partial class StashMigrationService
{
    private static string NormalizeStashCustomFieldKey(string name)
    {
        var key = new StringBuilder();
        foreach (var character in name)
        {
            if (character == '.')
                key.Append("__");
            else
                key.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_');
        }

        var normalized = key.ToString().Trim('_');
        if (normalized.Length == 0)
            normalized = "field";
        // Dots can expand Stash's 64-byte names. Leave room for the prefix and collision suffix.
        if (normalized.Length > 80)
            normalized = normalized[..80].TrimEnd('_');
        return "stash__" + normalized;
    }

    private async Task<int> ImportCustomFieldsAsync(
        SqliteConnection conn,
        string sourceType,
        string entityType,
        IReadOnlyDictionary<int, int> idMap,
        CancellationToken ct)
    {
        // These identifiers come only from the fixed entity mappings in ImportCoreAsync.
        var table = $"{sourceType}_custom_fields";
        if (idMap.Count == 0 || !await TableExistsAsync(conn, table, ct))
            return 0;

        var definitions = await _db.CustomFieldDefinitions.ToListAsync(ct);
        var definitionsByKey = definitions.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        var definitionsByName = new Dictionary<string, CustomFieldDefinition>(StringComparer.Ordinal);
        var pending = new List<(int EntityId, string Name, string Value)>(1000);
        var imported = 0;

        async Task FlushAsync()
        {
            if (pending.Count == 0)
                return;

            foreach (var name in pending.Select(row => row.Name).Distinct(StringComparer.Ordinal))
            {
                if (definitionsByName.ContainsKey(name))
                    continue;

                // Keep keys readable, and distinguish names that normalize to the same key
                // by checking the original label before reusing an existing definition.
                var baseKey = NormalizeStashCustomFieldKey(name);
                // Search the whole suffix family first: deleting a conflicting definition can
                // leave a free base key while the original imported field still exists at _2.
                var definition = definitionsByKey.Values
                    .Where(candidate => candidate.Label == name && candidate.Type == CustomFieldTypes.LongText
                        && !candidate.IsMultiValue && !candidate.Filterable && !candidate.Sortable
                        && (string.Equals(candidate.Key, baseKey, StringComparison.OrdinalIgnoreCase)
                            || (candidate.Key.StartsWith(baseKey + "_", StringComparison.OrdinalIgnoreCase)
                                && int.TryParse(candidate.Key.AsSpan(baseKey.Length + 1), out var number) && number >= 2)))
                    .OrderBy(candidate => candidate.Id)
                    .FirstOrDefault();

                if (definition == null)
                {
                    var key = baseKey;
                    var suffix = 2;
                    while (definitionsByKey.ContainsKey(key))
                        key = $"{baseKey}_{suffix++}";
                    definition = new CustomFieldDefinition
                    {
                        Key = key,
                        Label = name,
                        Type = CustomFieldTypes.LongText,
                        EntityTypes = [entityType],
                        Filterable = false,
                        Sortable = false,
                        IsMultiValue = false,
                    };
                    _db.CustomFieldDefinitions.Add(definition);
                    definitionsByKey.Add(key, definition);
                }
                else if (!definition.EntityTypes.Contains(entityType))
                {
                    if (_db.Entry(definition).State == EntityState.Detached)
                        _db.CustomFieldDefinitions.Attach(definition);
                    definition.EntityTypes = [.. definition.EntityTypes, entityType];
                }
                definitionsByName.Add(name, definition);
            }

            _db.ChangeTracker.DetectChanges();
            await _db.SaveChangesAsync(ct);

            var entityIds = pending.Select(row => row.EntityId).Distinct().ToArray();
            var definitionIds = pending.Select(row => definitionsByName[row.Name].Id).Distinct().ToArray();
            var existing = await _db.CustomFieldValues.AsNoTracking()
                .Where(value => value.EntityType == entityType && entityIds.Contains(value.EntityId)
                    && definitionIds.Contains(value.DefinitionId))
                .Select(value => new { value.EntityId, value.DefinitionId })
                .ToListAsync(ct);
            var occupied = existing.Select(value => (value.EntityId, value.DefinitionId)).ToHashSet();
            foreach (var row in pending)
            {
                var definitionId = definitionsByName[row.Name].Id;
                // Match the migration's fill-missing policy, including multiple source entities
                // resolving to the same Cove entity. Do not replace manually edited values.
                if (!occupied.Add((row.EntityId, definitionId)))
                    continue;
                _db.CustomFieldValues.Add(new CustomFieldValue
                {
                    DefinitionId = definitionId,
                    EntityType = entityType,
                    EntityId = row.EntityId,
                    LongTextValue = row.Value,
                });
                imported++;
            }
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
            pending.Clear();
        }

        await using var command = conn.CreateCommand();
        command.CommandText = $"SELECT {sourceType}_id, field, value FROM {table} ORDER BY {sourceType}_id, field";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!idMap.TryGetValue(reader.GetInt32(0), out var entityId) || reader.IsDBNull(2))
                continue;

            // Despite the BLOB column declaration, Stash writes native SQLite scalars, not JSON.
            // Strings must remain verbatim; numbers use a culture-independent representation.
            var value = reader.GetValue(2);
            var text = value is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : Convert.ToString(value, CultureInfo.InvariantCulture)!;
            pending.Add((entityId, reader.GetString(1), text));
            if (pending.Count >= 1000)
                await FlushAsync();
        }
        await FlushAsync();
        return imported;
    }
}
