using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private static string ReadStashCustomFieldText(object value)
        => value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : Convert.ToString(value, CultureInfo.InvariantCulture)!;

    private static readonly Regex StashCustomFieldNumberPattern = new(
        @"^[+-]?(?<whole>[0-9]*)(?:\.(?<fraction>[0-9]*))?(?:[eE](?<exponent>[+-]?[0-9]+))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static bool CanStoreStashJsonValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return !value.GetString()!.Contains('\0');
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                    if (property.Name.Contains('\0') || !names.Add(property.Name) || !CanStoreStashJsonValue(property.Value))
                        return false;
                return true;
            case JsonValueKind.Array:
                return value.EnumerateArray().All(CanStoreStashJsonValue);
            case JsonValueKind.Number:
                // jsonb numbers use unconstrained PostgreSQL numeric, which has wider bounds
                // than NumberValue. Preserve raw fractional scale, including trailing zeros.
                var number = StashCustomFieldNumberPattern.Match(value.GetRawText());
                var exponentText = number.Groups["exponent"].Value;
                var exponent = 0;
                if (exponentText.Length > 0 && !int.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                    return false;
                // Conservatively keep extreme exponent notation as text, even for zero:
                // PostgreSQL's exponent parser can overflow before normalizing the value.
                if (exponent > 131_072 || exponent < -16_383)
                    return false;
                var digits = (number.Groups["whole"].Value + number.Groups["fraction"].Value).TrimStart('0');
                var scale = (long)number.Groups["fraction"].Length - exponent;
                return scale <= 16_383 && (digits.Length == 0 || digits.Length - scale <= 131_072);
            default:
                return true;
        }
    }

    private static (bool Number, bool Json) DetectStashCustomFieldValueType(string text)
    {
        // Numbers take precedence over JSON. Check significant digits before parsing, since
        // decimal.TryParse can silently round inputs beyond decimal's precision.
        var isNumber = false;
        var numeric = StashCustomFieldNumberPattern.Match(text.Trim());
        if (numeric.Success && numeric.Groups["whole"].Length + numeric.Groups["fraction"].Length > 0)
        {
            var digits = numeric.Groups["whole"].Value + numeric.Groups["fraction"].Value;
            var significant = digits.TrimStart('0').TrimEnd('0');
            var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
            var exponentText = numeric.Groups["exponent"].Value;
            var exponent = 0;
            var hasExponent = exponentText.Length == 0 || int.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent);
            var scale = (long)numeric.Groups["fraction"].Length - exponent - trailingZeros;
            // NumberValue is PostgreSQL numeric(18,6): at most 12 integer and 6 fractional digits.
            if (hasExponent && (significant.Length == 0 || (scale <= 6 && significant.Length - scale <= 12))
                && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                isNumber = true;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return (isNumber, CanStoreStashJsonValue(document.RootElement));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Decoding a lone surrogate escape can fail even after JsonDocument.Parse succeeds.
            return (isNumber, false);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> DetectStashCustomFieldTypesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var types = new Dictionary<string, (bool Number, bool Json)>(StringComparer.Ordinal);
        // Definitions are shared across entity types. Inspect every source value before writing
        // any definitions, including rows outside an individual entity batch or target ID map.
        foreach (var sourceType in new[] { "studio", "tag", "performer", "group", "scene", "image", "gallery" })
        {
            var table = $"{sourceType}_custom_fields";
            if (!await TableExistsAsync(conn, table, ct))
                continue;
            await using var command = conn.CreateCommand();
            command.CommandText = $"SELECT field, value FROM {table}";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(1))
                    continue;
                var name = reader.GetString(0);
                var previous = types.GetValueOrDefault(name, (Number: true, Json: true));
                if (!previous.Number && !previous.Json)
                    continue;
                var current = DetectStashCustomFieldValueType(ReadStashCustomFieldText(reader.GetValue(1)));
                types[name] = (previous.Number && current.Number, previous.Json && current.Json);
            }
        }
        return types.ToDictionary(pair => pair.Key, pair => pair.Value.Number ? CustomFieldTypes.Number
            : pair.Value.Json ? CustomFieldTypes.Json : CustomFieldTypes.LongText, StringComparer.Ordinal);
    }

    private async Task<int> ImportCustomFieldsAsync(
        SqliteConnection conn,
        string sourceType,
        string entityType,
        IReadOnlyDictionary<int, int> idMap,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? fieldTypes = null)
    {
        // These identifiers come only from the fixed entity mappings in ImportCoreAsync.
        var table = $"{sourceType}_custom_fields";
        if (idMap.Count == 0 || !await TableExistsAsync(conn, table, ct))
            return 0;

        fieldTypes ??= await DetectStashCustomFieldTypesAsync(conn, ct);
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
                var fieldType = fieldTypes[name];
                // Search the whole suffix family first: deleting a conflicting definition can
                // leave a free base key while the original imported field still exists at _2.
                var definition = definitionsByKey.Values
                    .Where(candidate => candidate.Label == name && candidate.Type == fieldType
                        && !candidate.IsMultiValue
                        && (fieldType == CustomFieldTypes.Number || (!candidate.Filterable && !candidate.Sortable))
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
                        Type = fieldType,
                        EntityTypes = [entityType],
                        Filterable = fieldType == CustomFieldTypes.Number,
                        Sortable = fieldType == CustomFieldTypes.Number,
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
                var value = new CustomFieldValue
                {
                    DefinitionId = definitionId,
                    EntityType = entityType,
                    EntityId = row.EntityId,
                };
                switch (definitionsByName[row.Name].Type)
                {
                    case CustomFieldTypes.Number:
                        value.NumberValue = decimal.Parse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                        break;
                    case CustomFieldTypes.Json:
                        using (var document = JsonDocument.Parse(row.Value))
                            value.JsonValue = document.RootElement.Clone();
                        break;
                    default:
                        value.LongTextValue = row.Value;
                        break;
                }
                _db.CustomFieldValues.Add(value);
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
            var text = ReadStashCustomFieldText(value);
            pending.Add((entityId, reader.GetString(1), text));
            if (pending.Count >= 1000)
                await FlushAsync();
        }
        await FlushAsync();
        return imported;
    }
}
