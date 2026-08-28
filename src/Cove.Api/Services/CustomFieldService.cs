using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;

namespace Cove.Api.Services;

public sealed class CustomFieldService(
    CoveContext db,
    CustomFieldJsonIndexJobService? jsonIndexJobs = null)
{
    private readonly CoveContext _db = db;
    private readonly CustomFieldJsonIndexJobService? _jsonIndexJobs = jsonIndexJobs;
    private sealed record NormalizedJsonPathInput(
        string Path,
        string Label,
        string Type,
        bool Filterable,
        bool Sortable,
        int DisplayOrder);

    private sealed record NormalizedDefinitionInput(
        int? Id,
        string Key,
        string Label,
        string Type,
        string[] EntityTypes,
        string[] Options,
        bool Filterable,
        bool Sortable,
        bool IsMultiValue,
        IReadOnlyList<NormalizedJsonPathInput> JsonPaths,
        int DisplayOrder);

    public async Task<List<CustomFieldDefinitionDto>> GetDefinitionsAsync(string? entityType = null, CancellationToken ct = default)
    {
        var normalizedEntityType = NormalizeEntityType(entityType);
        var definitions = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Include(definition => definition.JsonPaths)
            .OrderBy(definition => definition.DisplayOrder)
            .ThenBy(definition => definition.Label)
            .ToListAsync(ct);

        if (normalizedEntityType != null)
            definitions = definitions.Where(definition => definition.EntityTypes.Contains(normalizedEntityType)).ToList();

        return definitions.Select(MapDefinition).ToList();
    }

    public async Task<CustomFieldDefinitionDto> CreateDefinitionAsync(CustomFieldDefinitionCreateDto dto, CancellationToken ct = default)
    {
        var key = NormalizeKey(dto.Key, dto.Label);
        if (await KeyExistsAsync(key, null, ct))
            throw new ArgumentException("A custom field with that key already exists.");

        var entityTypes = NormalizeEntityTypes(dto.EntityTypes);
        var type = CustomFieldTypes.Normalize(dto.Type);
        var jsonPaths = NormalizeJsonPaths(type, dto.JsonPaths);
        var definition = new CustomFieldDefinition
        {
            Key = key,
            Label = NormalizeLabel(dto.Label, key),
            Type = type,
            EntityTypes = entityTypes,
            Options = NormalizeOptions(dto.Options),
            Filterable = NormalizeFilterable(type, dto.Filterable),
            Sortable = NormalizeSortable(type, dto.Sortable),
            IsMultiValue = NormalizeMultiValue(type, dto.IsMultiValue),
            JsonPaths = CreateJsonPathDefinitions(jsonPaths),
            DisplayOrder = dto.DisplayOrder ?? await NextDisplayOrderAsync(ct),
        };

        _db.CustomFieldDefinitions.Add(definition);
        await _db.SaveChangesAsync(ct);
        if (CustomFieldTypes.IsJson(definition.Type))
            _jsonIndexJobs?.RequestReconcile();
        return MapDefinition(definition);
    }

    public async Task<CustomFieldDefinitionDto?> UpdateDefinitionAsync(int id, CustomFieldDefinitionUpdateDto dto, CancellationToken ct = default)
    {
        var definition = await _db.CustomFieldDefinitions
            .Include(item => item.JsonPaths)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (definition == null)
            return null;
        var reconcileJsonIndexes = CustomFieldTypes.IsJson(definition.Type)
            || (dto.Type != null && CustomFieldTypes.IsJson(dto.Type))
            || dto.JsonPaths != null;

        if (dto.Key != null)
        {
            var key = NormalizeKey(dto.Key, definition.Label);
            if (await KeyExistsAsync(key, id, ct))
                throw new ArgumentException("A custom field with that key already exists.");
            definition.Key = key;
        }

        if (dto.Label != null) definition.Label = NormalizeLabel(dto.Label, definition.Key);
        if (dto.Type != null)
        {
            var nextType = CustomFieldTypes.Normalize(dto.Type);
            if (!string.Equals(nextType, CustomFieldTypes.Normalize(definition.Type), StringComparison.Ordinal)
                && await _db.CustomFieldValues.AnyAsync(value => value.DefinitionId == definition.Id, ct))
            {
                throw new ArgumentException("Remove existing custom field values before changing its type.");
            }

            definition.Type = nextType;
        }
        if (dto.EntityTypes != null) definition.EntityTypes = NormalizeEntityTypes(dto.EntityTypes);
        if (dto.Options != null) definition.Options = NormalizeOptions(dto.Options);
        if (dto.Filterable.HasValue) definition.Filterable = dto.Filterable.Value;
        if (dto.Sortable.HasValue) definition.Sortable = dto.Sortable.Value;
        if (dto.IsMultiValue.HasValue) definition.IsMultiValue = dto.IsMultiValue.Value;
        if (dto.JsonPaths != null)
            SyncJsonPathDefinitions(definition, NormalizeJsonPaths(definition.Type, dto.JsonPaths));
        else if (!CustomFieldTypes.IsJson(definition.Type) && definition.JsonPaths.Count > 0)
            SyncJsonPathDefinitions(definition, []);
        if (dto.DisplayOrder.HasValue) definition.DisplayOrder = dto.DisplayOrder.Value;
        NormalizeDefinitionCapabilities(definition);
        definition.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        if (reconcileJsonIndexes)
            _jsonIndexJobs?.RequestReconcile();
        return MapDefinition(definition);
    }

    public async Task<bool> DeleteDefinitionAsync(int id, CancellationToken ct = default)
    {
        var definition = await _db.CustomFieldDefinitions.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (definition == null)
            return false;

        _db.CustomFieldDefinitions.Remove(definition);
        await _db.SaveChangesAsync(ct);
        if (CustomFieldTypes.IsJson(definition.Type))
            _jsonIndexJobs?.RequestReconcile();
        return true;
    }

    public async Task<List<CustomFieldDefinitionDto>> ReplaceDefinitionsAsync(IReadOnlyCollection<CustomFieldDefinitionSyncDto> definitions, CancellationToken ct = default)
    {
        var incomingDefinitions = definitions.ToList();
        var duplicateId = incomingDefinitions
            .Where(definition => definition.Id.HasValue)
            .GroupBy(definition => definition.Id!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId != null)
            throw new ArgumentException("Duplicate custom field definition ids are not allowed.");

        var existingDefinitions = await _db.CustomFieldDefinitions
            .Include(definition => definition.JsonPaths)
            .ToListAsync(ct);
        var existingDefinitionsById = existingDefinitions.ToDictionary(definition => definition.Id);
        var normalizedDefinitions = incomingDefinitions
            .Select((definition, index) =>
            {
                if (definition.Id is int id && !existingDefinitionsById.ContainsKey(id))
                    throw new ArgumentException("A custom field definition no longer exists.");

                var key = NormalizeKey(definition.Key, definition.Label);
                var type = CustomFieldTypes.Normalize(definition.Type);
                return new NormalizedDefinitionInput(
                    definition.Id,
                    key,
                    NormalizeLabel(definition.Label, key),
                    type,
                    NormalizeEntityTypes(definition.EntityTypes),
                    NormalizeOptions(definition.Options),
                    NormalizeFilterable(type, definition.Filterable),
                    NormalizeSortable(type, definition.Sortable),
                    NormalizeMultiValue(type, definition.IsMultiValue),
                    NormalizeJsonPaths(type, definition.JsonPaths),
                    definition.DisplayOrder ?? (index * 10));
            })
            .ToList();
        var reconcileJsonIndexes = existingDefinitions.Any(definition => CustomFieldTypes.IsJson(definition.Type))
            || normalizedDefinitions.Any(definition => CustomFieldTypes.IsJson(definition.Type));

        var duplicateKey = normalizedDefinitions
            .GroupBy(definition => definition.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateKey != null)
            throw new ArgumentException("A custom field with that key already exists.");

        var typeChangedDefinitionIds = normalizedDefinitions
            .Where(definition => definition.Id is int id
                && !string.Equals(
                    definition.Type,
                    CustomFieldTypes.Normalize(existingDefinitionsById[id].Type),
                    StringComparison.Ordinal))
            .Select(definition => definition.Id!.Value)
            .ToArray();
        if (typeChangedDefinitionIds.Length > 0
            && await _db.CustomFieldValues.AnyAsync(value => typeChangedDefinitionIds.Contains(value.DefinitionId), ct))
        {
            throw new ArgumentException("Remove existing custom field values before changing its type.");
        }

        var retainedDefinitionIds = normalizedDefinitions
            .Where(definition => definition.Id.HasValue)
            .Select(definition => definition.Id!.Value)
            .ToHashSet();
        var definitionsToDelete = existingDefinitions
            .Where(definition => !retainedDefinitionIds.Contains(definition.Id))
            .ToList();
        if (definitionsToDelete.Count > 0)
            _db.CustomFieldDefinitions.RemoveRange(definitionsToDelete);

        foreach (var definitionInput in normalizedDefinitions)
        {
            var definition = definitionInput.Id is int id
                ? existingDefinitionsById[id]
                : new CustomFieldDefinition();

            if (definitionInput.Id == null)
                _db.CustomFieldDefinitions.Add(definition);

            definition.Key = definitionInput.Key;
            definition.Label = definitionInput.Label;
            definition.Type = definitionInput.Type;
            definition.EntityTypes = definitionInput.EntityTypes;
            definition.Options = definitionInput.Options;
            definition.Filterable = definitionInput.Filterable;
            definition.Sortable = definitionInput.Sortable;
            definition.IsMultiValue = definitionInput.IsMultiValue;
            SyncJsonPathDefinitions(definition, definitionInput.JsonPaths);
            definition.DisplayOrder = definitionInput.DisplayOrder;
            definition.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        if (reconcileJsonIndexes)
            _jsonIndexJobs?.RequestReconcile();
        return await GetDefinitionsAsync(null, ct);
    }

    public async Task<IReadOnlyDictionary<int, Dictionary<string, object>>> GetValuesAsync(string entityType, IEnumerable<int> entityIds, CancellationToken ct = default)
    {
        var normalizedEntityType = RequireEntityType(entityType);
        var ids = entityIds.Distinct().ToArray();
        var result = ids.ToDictionary(id => id, _ => new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));
        if (ids.Length == 0)
            return result;

        var rows = await _db.CustomFieldValues
            .AsNoTracking()
            .Include(value => value.Definition)
            .Where(value => value.EntityType == normalizedEntityType && ids.Contains(value.EntityId))
            .OrderBy(value => value.Definition!.DisplayOrder)
            .ThenBy(value => value.Position)
            .ToListAsync(ct);

        foreach (var entityGroup in rows.Where(value => value.Definition != null).GroupBy(value => value.EntityId))
        {
            if (!result.TryGetValue(entityGroup.Key, out var values))
                continue;

            foreach (var fieldGroup in entityGroup.GroupBy(value => value.DefinitionId))
            {
                var definition = fieldGroup.First().Definition!;
                var fieldValues = fieldGroup.OrderBy(value => value.Position).Select(value => ConvertValue(definition, value)).Where(value => value != null).ToList();
                if (fieldValues.Count == 0)
                    continue;

                values[definition.Key] = NormalizeMultiValue(definition.Type, definition.IsMultiValue) ? fieldValues : fieldValues[0]!;
            }
        }

        return result;
    }

    public async Task<Dictionary<string, object>> GetValuesAsync(string entityType, int entityId, CancellationToken ct = default)
    {
        var values = await GetValuesAsync(entityType, [entityId], ct);
        return values.TryGetValue(entityId, out var result) ? result : [];
    }

    public async Task<bool> SaveValuesAsync(string entityType, int entityId, IDictionary<string, object>? input, CancellationToken ct = default)
    {
        var normalizedEntityType = RequireEntityType(entityType);
        var existingValues = await _db.CustomFieldValues
            .Where(value => value.EntityType == normalizedEntityType && value.EntityId == entityId)
            .OrderBy(value => value.DefinitionId)
            .ThenBy(value => value.Position)
            .ToListAsync(ct);

        if (input == null || input.Count == 0)
        {
            if (existingValues.Count == 0)
                return false;
            _db.CustomFieldValues.RemoveRange(existingValues);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        var definitions = await _db.CustomFieldDefinitions
            .Where(definition => definition.EntityTypes.Contains(normalizedEntityType))
            .ToListAsync(ct);
        var definitionsByKey = definitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);
        var values = new List<CustomFieldValue>();

        foreach (var (key, rawValue) in input)
        {
            if (!definitionsByKey.TryGetValue(key, out var definition))
                continue;

            var normalizedValues = NormalizeInputValues(definition, rawValue).ToList();
            for (var index = 0; index < normalizedValues.Count; index++)
            {
                var value = normalizedValues[index];
                value.DefinitionId = definition.Id;
                value.EntityType = normalizedEntityType;
                value.EntityId = entityId;
                value.Position = index;
                values.Add(value);
            }
        }

        if (values.Count == 0)
        {
            if (existingValues.Count == 0)
                return false;
            _db.CustomFieldValues.RemoveRange(existingValues);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        values = values.OrderBy(value => value.DefinitionId).ThenBy(value => value.Position).ToList();
        if (existingValues.Count == values.Count && existingValues.Zip(values).All(pair => SameValue(pair.First, pair.Second)))
            return false;

        _db.CustomFieldValues.RemoveRange(existingValues);
        _db.CustomFieldValues.AddRange(values);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool SameValue(CustomFieldValue left, CustomFieldValue right) =>
        left.DefinitionId == right.DefinitionId
        && left.Position == right.Position
        && left.TextValue == right.TextValue
        && left.LongTextValue == right.LongTextValue
        && SameNumber(left.NumberValue, right.NumberValue)
        && left.BoolValue == right.BoolValue
        && left.DateValue == right.DateValue
        && SameTimestamp(left.TimestampValue, right.TimestampValue)
        && left.IntegerValue == right.IntegerValue
        && SameJson(left.JsonValue, right.JsonValue);

    private static bool SameNumber(decimal? left, decimal? right) =>
        left.HasValue == right.HasValue
        && (!left.HasValue || decimal.Round(left.Value, 6, MidpointRounding.AwayFromZero)
            == decimal.Round(right!.Value, 6, MidpointRounding.AwayFromZero));

    private static bool SameTimestamp(DateTime? left, DateTime? right) =>
        NormalizeTimestamp(left) == NormalizeTimestamp(right);

    private static DateTime? NormalizeTimestamp(DateTime? value) =>
        value.HasValue ? value.Value.AddTicks(-(value.Value.Ticks % 10)) : null;

    private static bool SameJson(JsonElement? left, JsonElement? right) =>
        left.HasValue == right.HasValue
        && (!left.HasValue || SameJsonValue(left.Value, right!.Value));

    private static bool SameJsonValue(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count()
                && left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out var other) && SameJsonValue(property.Value, other)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => SameJsonValue(pair.First, pair.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => NormalizeJsonNumber(left.GetRawText()) == NormalizeJsonNumber(right.GetRawText()),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => false,
        };
    }

    private static (BigInteger Coefficient, int Power) NormalizeJsonNumber(string raw)
    {
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex >= 0 ? raw[..exponentIndex] : raw;
        var exponent = exponentIndex >= 0
            ? int.Parse(raw[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : 0;
        var negative = mantissa.StartsWith("-", StringComparison.Ordinal);
        if (negative)
            mantissa = mantissa[1..];
        var decimalIndex = mantissa.IndexOf('.');
        var fractionalDigits = decimalIndex >= 0 ? mantissa.Length - decimalIndex - 1 : 0;
        var digits = decimalIndex >= 0 ? string.Concat(mantissa.AsSpan(0, decimalIndex), mantissa.AsSpan(decimalIndex + 1)) : mantissa;
        var coefficient = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
            coefficient = -coefficient;
        if (coefficient.IsZero)
            return (BigInteger.Zero, 0);

        var power = exponent - fractionalDigits;
        while (coefficient % 10 == 0)
        {
            coefficient /= 10;
            power++;
        }
        return (coefficient, power);
    }

    public async Task DeleteValuesForEntityAsync(string entityType, int entityId, CancellationToken ct = default)
    {
        if (await StageDeleteValuesForEntityAsync(entityType, entityId, ct))
            await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stages custom-field value removal without saving so a larger entity mutation can commit all of
    /// its relational changes atomically in one SaveChanges call.
    /// </summary>
    public async Task<bool> StageDeleteValuesForEntityAsync(string entityType, int entityId, CancellationToken ct = default)
    {
        var normalizedEntityType = RequireEntityType(entityType);
        var values = await _db.CustomFieldValues
            .Where(value => value.EntityType == normalizedEntityType && value.EntityId == entityId)
            .ToListAsync(ct);
        if (values.Count == 0)
            return false;

        _db.CustomFieldValues.RemoveRange(values);
        return true;
    }

    private async Task<bool> KeyExistsAsync(string key, int? exceptId, CancellationToken ct)
        => await _db.CustomFieldDefinitions.AnyAsync(definition => definition.Key == key && (!exceptId.HasValue || definition.Id != exceptId.Value), ct);

    private async Task<int> NextDisplayOrderAsync(CancellationToken ct)
        => (await _db.CustomFieldDefinitions.Select(definition => (int?)definition.DisplayOrder).MaxAsync(ct) ?? -10) + 10;

    public static CustomFieldDefinitionDto MapDefinition(CustomFieldDefinition definition)
    {
        var type = CustomFieldTypes.Normalize(definition.Type);
        return new()
        {
            Id = definition.Id,
            Key = definition.Key,
            Label = definition.Label,
            Type = type,
            EntityTypes = [.. definition.EntityTypes],
            Options = [.. definition.Options],
            Filterable = NormalizeFilterable(type, definition.Filterable),
            Sortable = NormalizeSortable(type, definition.Sortable),
            IsMultiValue = NormalizeMultiValue(type, definition.IsMultiValue),
            JsonPaths = definition.JsonPaths
                .OrderBy(path => path.DisplayOrder)
                .ThenBy(path => path.Label)
                .Select(MapJsonPathDefinition)
                .ToList(),
            DisplayOrder = definition.DisplayOrder,
            CreatedAt = definition.CreatedAt.ToString("o"),
            UpdatedAt = definition.UpdatedAt.ToString("o"),
        };
    }

    private static IEnumerable<CustomFieldValue> NormalizeInputValues(CustomFieldDefinition definition, object? rawValue)
    {
        if (CustomFieldTypes.IsJson(definition.Type) || CustomFieldTypes.IsLongText(definition.Type))
        {
            var converted = ConvertInputValue(definition, rawValue);
            if (converted != null)
                yield return converted;
            yield break;
        }

        if (rawValue is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var converted = ConvertInputValue(definition, item);
                if (converted != null)
                    yield return converted;
            }

            yield break;
        }

        if (rawValue is not string && rawValue is IEnumerable<object> items)
        {
            foreach (var item in items)
            {
                var converted = ConvertInputValue(definition, item);
                if (converted != null)
                    yield return converted;
            }

            yield break;
        }

        var value = ConvertInputValue(definition, rawValue);
        if (value != null)
            yield return value;
    }

    private static CustomFieldValue? ConvertInputValue(CustomFieldDefinition definition, object? rawValue)
    {
        if (rawValue == null)
            return null;

        var type = CustomFieldTypes.Normalize(definition.Type);
        if (CustomFieldTypes.IsJson(type))
            return ConvertInputJsonValue(rawValue);

        if (rawValue is JsonElement element)
            rawValue = ConvertJsonElement(element);

        if (rawValue == null)
            return null;

        var value = new CustomFieldValue();
        if (CustomFieldTypes.IsNumberLike(type))
        {
            if (!TryConvertDecimal(rawValue, out var number))
                return null;
            value.NumberValue = number;
            return value;
        }

        if (CustomFieldTypes.IsBoolean(type))
        {
            if (!TryConvertBool(rawValue, out var boolValue))
                return null;
            value.BoolValue = boolValue;
            return value;
        }

        if (CustomFieldTypes.IsDateLike(type))
        {
            if (!TryConvertDate(rawValue, out var dateValue))
                return null;
            value.DateValue = dateValue;
            return value;
        }

        if (CustomFieldTypes.IsTimestampLike(type))
        {
            if (!TryConvertTimestamp(rawValue, out var timestampValue))
                return null;
            value.TimestampValue = timestampValue;
            return value;
        }

        if (CustomFieldTypes.IsReference(type))
        {
            if (!TryConvertInteger(rawValue, out var integerValue))
                return null;
            value.IntegerValue = integerValue;
            return value;
        }

        var rawText = Convert.ToString(rawValue, CultureInfo.InvariantCulture);
        if (CustomFieldTypes.IsLongText(type))
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return null;
            value.LongTextValue = rawText;
            return value;
        }

        var text = rawText?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        value.TextValue = text;
        return value;
    }

    private static object? ConvertValue(CustomFieldDefinition definition, CustomFieldValue value)
    {
        var type = CustomFieldTypes.Normalize(definition.Type);
        if (CustomFieldTypes.IsJson(type)) return value.JsonValue?.Clone();
        if (CustomFieldTypes.IsLongText(type)) return value.LongTextValue;
        if (CustomFieldTypes.IsNumberLike(type)) return value.NumberValue;
        if (CustomFieldTypes.IsBoolean(type)) return value.BoolValue;
        if (CustomFieldTypes.IsDateLike(type)) return value.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (CustomFieldTypes.IsTimestampLike(type)) return value.TimestampValue?.ToString("o", CultureInfo.InvariantCulture);
        if (CustomFieldTypes.IsReference(type)) return value.IntegerValue;
        return value.TextValue;
    }

    private static CustomFieldValue? ConvertInputJsonValue(object rawValue)
    {
        string json;
        try
        {
            json = rawValue switch
            {
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => string.Empty,
                JsonElement element => element.GetRawText(),
                JsonDocument inputDocument => inputDocument.RootElement.GetRawText(),
                _ => JsonSerializer.Serialize(rawValue),
            };

            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var parsedDocument = JsonDocument.Parse(json);
            if (parsedDocument.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            return new CustomFieldValue { JsonValue = parsedDocument.RootElement.Clone() };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static object? ConvertJsonElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };

    private static bool NormalizeFilterable(string type, bool requested)
        => !CustomFieldTypes.IsJson(type) && !CustomFieldTypes.IsLongText(type) && requested;

    private static bool NormalizeSortable(string type, bool requested)
        => !CustomFieldTypes.IsJson(type) && !CustomFieldTypes.IsLongText(type) && requested;

    private static bool NormalizeMultiValue(string type, bool requested)
        => !CustomFieldTypes.IsJson(type) && !CustomFieldTypes.IsLongText(type) && requested;

    private static List<NormalizedJsonPathInput> NormalizeJsonPaths(
        string definitionType,
        IEnumerable<CustomFieldJsonPathDefinitionDto>? jsonPaths)
    {
        if (!CustomFieldTypes.IsJson(definitionType) || jsonPaths == null)
            return [];

        var normalized = jsonPaths.Select((jsonPath, index) =>
        {
            var path = NormalizeJsonPointer(jsonPath.Path);
            var type = NormalizeJsonPathType(jsonPath.Type);
            var label = string.IsNullOrWhiteSpace(jsonPath.Label)
                ? DecodeJsonPointerSegment(path[(path.LastIndexOf('/') + 1)..]).Trim()
                : jsonPath.Label.Trim();
            if (string.IsNullOrWhiteSpace(label))
                label = path;
            if (label.Length > 200)
                throw new ArgumentException("A queryable JSON path label cannot exceed 200 characters.");

            return new NormalizedJsonPathInput(
                path,
                label,
                type,
                jsonPath.Filterable,
                jsonPath.Sortable,
                index * 10);
        }).ToList();

        var duplicate = normalized
            .GroupBy(path => path.Path, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new ArgumentException($"Duplicate JSON Pointer '{duplicate.Key}' is not allowed.");

        return normalized;
    }

    private static string NormalizeJsonPointer(string? rawPath)
    {
        var path = rawPath ?? string.Empty;
        if (path.Length == 0 || path[0] != '/')
            throw new ArgumentException("A queryable JSON path must be a non-root JSON Pointer beginning with '/'.");
        if (path.Length > 500)
            throw new ArgumentException("A queryable JSON Pointer cannot exceed 500 characters.");
        if (path[1..].Split('/', StringSplitOptions.None).Length > 32)
            throw new ArgumentException("A queryable JSON Pointer cannot exceed 32 segments.");

        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] != '~')
                continue;
            if (index + 1 >= path.Length || path[index + 1] is not ('0' or '1'))
                throw new ArgumentException("A queryable JSON Pointer may only use '~0' and '~1' escape sequences.");
            index++;
        }

        return path;
    }

    private static string NormalizeJsonPathType(string? rawType)
    {
        var type = rawType?.Trim();
        if (string.Equals(type, CustomFieldTypes.Text, StringComparison.OrdinalIgnoreCase))
            return CustomFieldTypes.Text;
        if (string.Equals(type, CustomFieldTypes.Number, StringComparison.OrdinalIgnoreCase))
            return CustomFieldTypes.Number;
        if (string.Equals(type, CustomFieldTypes.Boolean, StringComparison.OrdinalIgnoreCase))
            return CustomFieldTypes.Boolean;
        throw new ArgumentException("Queryable JSON paths currently support text, number, and boolean scalar types.");
    }

    private static string DecodeJsonPointerSegment(string segment)
        => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static ICollection<CustomFieldJsonPathDefinition> CreateJsonPathDefinitions(
        IEnumerable<NormalizedJsonPathInput> jsonPaths)
        => jsonPaths.Select(CreateJsonPathDefinition).ToList();

    private static CustomFieldJsonPathDefinition CreateJsonPathDefinition(NormalizedJsonPathInput jsonPath)
        => new()
        {
            Path = jsonPath.Path,
            Label = jsonPath.Label,
            Type = jsonPath.Type,
            Filterable = jsonPath.Filterable,
            Sortable = jsonPath.Sortable,
            DisplayOrder = jsonPath.DisplayOrder,
        };

    private static CustomFieldJsonPathDefinitionDto MapJsonPathDefinition(CustomFieldJsonPathDefinition jsonPath)
        => new()
        {
            Path = jsonPath.Path,
            Label = jsonPath.Label,
            Type = jsonPath.Type,
            Filterable = jsonPath.Filterable,
            Sortable = jsonPath.Sortable,
        };

    private static void SyncJsonPathDefinitions(
        CustomFieldDefinition definition,
        IReadOnlyList<NormalizedJsonPathInput> jsonPaths)
    {
        var existingByPath = definition.JsonPaths.ToDictionary(path => path.Path, StringComparer.Ordinal);
        var retained = new HashSet<CustomFieldJsonPathDefinition>();

        foreach (var jsonPathInput in jsonPaths)
        {
            if (!existingByPath.TryGetValue(jsonPathInput.Path, out var jsonPath))
            {
                jsonPath = CreateJsonPathDefinition(jsonPathInput);
                definition.JsonPaths.Add(jsonPath);
            }

            jsonPath.Path = jsonPathInput.Path;
            jsonPath.Label = jsonPathInput.Label;
            jsonPath.Type = jsonPathInput.Type;
            jsonPath.Filterable = jsonPathInput.Filterable;
            jsonPath.Sortable = jsonPathInput.Sortable;
            jsonPath.DisplayOrder = jsonPathInput.DisplayOrder;
            jsonPath.UpdatedAt = DateTime.UtcNow;
            retained.Add(jsonPath);
        }

        foreach (var jsonPath in definition.JsonPaths.Where(path => !retained.Contains(path)).ToList())
            definition.JsonPaths.Remove(jsonPath);
    }

    private static void NormalizeDefinitionCapabilities(CustomFieldDefinition definition)
    {
        definition.Filterable = NormalizeFilterable(definition.Type, definition.Filterable);
        definition.Sortable = NormalizeSortable(definition.Type, definition.Sortable);
        definition.IsMultiValue = NormalizeMultiValue(definition.Type, definition.IsMultiValue);
    }

    private static bool TryConvertDecimal(object value, out decimal result)
    {
        result = 0;
        return value switch
        {
            decimal decimalValue => Set(decimalValue, out result),
            int intValue => Set(intValue, out result),
            long longValue => Set(longValue, out result),
            double doubleValue => Set((decimal)doubleValue, out result),
            float floatValue => Set((decimal)floatValue, out result),
            string text => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out result),
            _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out result),
        };
    }

    private static bool TryConvertInteger(object value, out int result)
    {
        result = 0;
        return value switch
        {
            int intValue => Set(intValue, out result),
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => Set((int)longValue, out result),
            decimal decimalValue when decimalValue == decimal.Truncate(decimalValue) && decimalValue is >= int.MinValue and <= int.MaxValue => Set((int)decimalValue, out result),
            string text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result),
            _ => int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result),
        };
    }

    private static bool TryConvertBool(object value, out bool result)
    {
        result = false;
        return value switch
        {
            bool boolValue => Set(boolValue, out result),
            string text => bool.TryParse(text, out result),
            _ => bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result),
        };
    }

    private static bool TryConvertDate(object value, out DateOnly result)
    {
        result = default;
        if (value is DateOnly dateOnly)
            return Set(dateOnly, out result);
        if (value is DateTime dateTime)
            return Set(DateOnly.FromDateTime(dateTime), out result);

        return DateOnly.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static bool TryConvertTimestamp(object value, out DateTime result)
    {
        result = default;
        if (value is DateTime dateTime)
            return Set(dateTime, out result);

        return DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
    }

    private static bool Set<T>(T value, out T target)
    {
        target = value;
        return true;
    }

    private static string NormalizeKey(string? key, string? label)
    {
        var raw = string.IsNullOrWhiteSpace(key) ? label : key;
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("A custom field label is required.");

        var result = new List<char>();
        var previousWasSeparator = false;
        foreach (var character in raw.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Add(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && result.Count > 0)
            {
                result.Add('_');
                previousWasSeparator = true;
            }
        }

        var normalized = new string([.. result]).Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("A custom field key must contain at least one letter or number.");
        return normalized;
    }

    private static string NormalizeLabel(string? label, string key)
        => string.IsNullOrWhiteSpace(label) ? key : label.Trim();

    private static string[] NormalizeEntityTypes(IEnumerable<string>? entityTypes)
    {
        var allowed = CustomFieldEntityTypes.All.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = (entityTypes ?? [])
            .Where(entityType => !string.IsNullOrWhiteSpace(entityType))
            .Select(entityType => entityType.Trim().ToLowerInvariant())
            .Where(allowed.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
            throw new ArgumentException("Select at least one entity type for the custom field.");
        return normalized;
    }

    private static string[] NormalizeOptions(IEnumerable<string>? options)
        => (options ?? [])
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Select(option => option.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? NormalizeEntityType(string? entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            return null;
        return RequireEntityType(entityType);
    }

    private static string RequireEntityType(string entityType)
    {
        var normalized = entityType.Trim().ToLowerInvariant();
        if (!CustomFieldEntityTypes.All.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown custom field entity type.");
        return normalized;
    }
}
