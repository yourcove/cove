using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Entities;

namespace Cove.Data.Services;

/// <summary>
/// Rewrites performer or studio identifiers in Cove-owned JSON. Only known identifier and criterion
/// shapes are recognized so unrelated numbers are never changed merely because they equal an entity ID.
/// </summary>
internal static class EntityReferenceJsonRewriter
{
    private static readonly HashSet<string> CriterionIdArrayProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "value",
        "excludes",
        "requiredIds",
    };

    public static string? Rewrite(
        string entityType,
        string? json,
        IReadOnlyDictionary<int, int> idMap,
        bool isEntityFilter = false)
    {
        if (string.IsNullOrWhiteSpace(json) || idMap.Count == 0)
            return json;

        if (!FindIds(entityType, json, isEntityFilter).Any(id => IsChangedMapping(id, idMap)))
            return json;

        var root = ParseNode(json);
        if (root == null)
            return json;

        RewriteNode(Describe(entityType), root, idMap, isEntityFilter, false, true);
        return root.ToJsonString();
    }

    public static JsonDocument? Rewrite(
        string entityType,
        JsonDocument? document,
        IReadOnlyDictionary<int, int> idMap)
    {
        if (document == null)
            return null;

        var original = document.RootElement.GetRawText();
        var rewritten = Rewrite(entityType, original, idMap);
        return rewritten == null
            ? null
            : string.Equals(original, rewritten, StringComparison.Ordinal)
                ? document
                : JsonDocument.Parse(rewritten);
    }

    public static IReadOnlySet<int> FindIds(
        string entityType,
        string? json,
        bool isEntityFilter = false,
        bool rootIsIdArray = false)
    {
        var result = new HashSet<int>();
        var root = ParseNode(json);
        if (root == null)
            return result;

        if (rootIsIdArray)
            CollectArray(root, result);
        else
            CollectNode(Describe(entityType), root, result, isEntityFilter, false, true);
        return result;
    }

    public static string? RewriteFieldProvenanceValue(
        string entityType,
        string fieldKey,
        string? json,
        IReadOnlyDictionary<int, int> idMap)
    {
        var normalizedField = fieldKey.Trim().Replace('-', '_').ToLowerInvariant();
        if (normalizedField == $"{entityType}_id")
            return RewriteRootId(json, idMap);
        return normalizedField is "payload" or "request" or "summary"
            ? Rewrite(entityType, json, idMap)
            : json;
    }

    public static IReadOnlySet<int> FindFieldProvenanceIds(
        string entityType,
        string fieldKey,
        string? json)
    {
        var normalizedField = fieldKey.Trim().Replace('-', '_').ToLowerInvariant();
        if (normalizedField == $"{entityType}_id")
        {
            var result = new HashSet<int>();
            if (ParseNode(json) is { } root)
                CollectScalar(root, result);
            return result;
        }

        return normalizedField is "payload" or "request" or "summary"
            ? FindIds(entityType, json)
            : new HashSet<int>();
    }

    public static string? RewriteUserUiPreferences(
        string entityType,
        string? json,
        IReadOnlyDictionary<int, int> idMap)
    {
        if (string.IsNullOrWhiteSpace(json)
            || idMap.Count == 0
            || ParseNode(json) is not JsonObject preferences
            || GetProperty(preferences, "defaultFilters", "default_filters") is not JsonObject defaultFilters)
            return json;

        var changed = false;
        foreach (var filter in defaultFilters.ToArray())
        {
            var serializedFilter = ReadString(filter.Value);
            if (serializedFilter == null)
                continue;

            var rewritten = Rewrite(entityType, serializedFilter, idMap, IsEntityName(filter.Key, entityType));
            if (rewritten == null || string.Equals(rewritten, serializedFilter, StringComparison.Ordinal))
                continue;

            defaultFilters[filter.Key] = rewritten;
            changed = true;
        }

        return changed ? preferences.ToJsonString() : json;
    }

    public static IReadOnlySet<int> FindUserUiPreferenceIds(string entityType, string? json)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(json)
            || ParseNode(json) is not JsonObject preferences
            || GetProperty(preferences, "defaultFilters", "default_filters") is not JsonObject defaultFilters)
            return result;

        foreach (var filter in defaultFilters)
        {
            var serializedFilter = ReadString(filter.Value);
            if (serializedFilter == null)
                continue;
            foreach (var id in FindIds(entityType, serializedFilter, IsEntityName(filter.Key, entityType)))
                result.Add(id);
        }

        return result;
    }

    public static string? RewriteRoleContentScope(
        string entityType,
        string entityKind,
        string scopeKind,
        string? json,
        IReadOnlyDictionary<int, int> idMap)
    {
        var genericallyRewritten = Rewrite(entityType, json, idMap);
        if (!IsEntityName(entityKind, entityType)
            || string.IsNullOrWhiteSpace(genericallyRewritten)
            || ParseNode(genericallyRewritten) is not { } root)
            return genericallyRewritten;

        return RewriteEntityAttributeScope(root, scopeKind, idMap)
            ? root.ToJsonString()
            : genericallyRewritten;
    }

    public static IReadOnlySet<int> FindRoleContentIds(
        string entityType,
        string entityKind,
        string scopeKind,
        string? json)
    {
        var result = new HashSet<int>(FindIds(entityType, json));
        if (!IsEntityName(entityKind, entityType) || ParseNode(json) is not { } root)
            return result;
        CollectEntityAttributeScope(root, scopeKind, result);
        return result;
    }

    private static void RewriteNode(
        Descriptor descriptor,
        JsonNode node,
        IReadOnlyDictionary<int, int> idMap,
        bool isEntityFilter,
        bool isEntityCriterion,
        bool isFilterObject)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child != null).ToArray())
                RewriteNode(descriptor, child!, idMap, isEntityFilter, isEntityCriterion, isFilterObject);
            return;
        }
        if (node is not JsonObject obj)
            return;

        var customFieldIsEntity = obj.TryGetPropertyValue("type", out var typeNode)
            && IsEntityName(ReadString(typeNode), descriptor.EntityType);
        var objectFilterIsForEntity = isEntityFilter || ObjectDeclaresEntity(obj, descriptor.EntityType);
        foreach (var property in obj.ToArray())
        {
            if (property.Value == null)
                continue;
            if (descriptor.ScalarProperties.Contains(property.Key))
            {
                obj[property.Key] = RewriteScalar(property.Value, idMap);
                continue;
            }
            if (descriptor.ArrayProperties.Contains(property.Key))
            {
                obj[property.Key] = RewriteArray(property.Value, idMap);
                continue;
            }
            if (descriptor.EntityType == NameConflictEntityTypes.Studio
                && isFilterObject
                && objectFilterIsForEntity
                && property.Key.Equals("parentId", StringComparison.OrdinalIgnoreCase))
            {
                obj[property.Key] = RewriteScalar(property.Value, idMap);
                continue;
            }

            var childIsEntityCriterion = descriptor.CriterionProperties.Contains(property.Key)
                || descriptor.EntityType == NameConflictEntityTypes.Studio
                    && objectFilterIsForEntity
                    && (property.Key.Equals("parentsCriterion", StringComparison.OrdinalIgnoreCase)
                        || property.Key.Equals("childrenCriterion", StringComparison.OrdinalIgnoreCase));
            if (childIsEntityCriterion)
            {
                RewriteNode(descriptor, property.Value, idMap, isEntityFilter, true, false);
                continue;
            }
            if (isEntityCriterion && CriterionIdArrayProperties.Contains(property.Key))
            {
                obj[property.Key] = RewriteArray(property.Value, idMap);
                continue;
            }
            if (customFieldIsEntity
                && (property.Key.Equals("value", StringComparison.OrdinalIgnoreCase)
                    || property.Key.Equals("value2", StringComparison.OrdinalIgnoreCase)))
            {
                obj[property.Key] = RewriteScalar(property.Value, idMap);
                continue;
            }
            if (property.Key.Equals("objectFilters", StringComparison.OrdinalIgnoreCase)
                && property.Value is JsonObject objectFilters)
            {
                foreach (var objectFilter in objectFilters.ToArray())
                    if (objectFilter.Value != null)
                        RewriteNode(descriptor, objectFilter.Value, idMap, IsEntityName(objectFilter.Key, descriptor.EntityType), false, true);
                continue;
            }
            if (property.Key.Equals("objectFilter", StringComparison.OrdinalIgnoreCase))
            {
                RewriteNode(descriptor, property.Value, idMap, objectFilterIsForEntity, false, true);
                continue;
            }
            RewriteNode(descriptor, property.Value, idMap, isEntityFilter, isEntityCriterion, false);
        }
    }

    private static void CollectNode(
        Descriptor descriptor,
        JsonNode node,
        ISet<int> result,
        bool isEntityFilter,
        bool isEntityCriterion,
        bool isFilterObject)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child != null))
                CollectNode(descriptor, child!, result, isEntityFilter, isEntityCriterion, isFilterObject);
            return;
        }
        if (node is not JsonObject obj)
            return;

        var customFieldIsEntity = obj.TryGetPropertyValue("type", out var typeNode)
            && IsEntityName(ReadString(typeNode), descriptor.EntityType);
        var objectFilterIsForEntity = isEntityFilter || ObjectDeclaresEntity(obj, descriptor.EntityType);
        foreach (var property in obj)
        {
            if (property.Value == null)
                continue;
            if (descriptor.ScalarProperties.Contains(property.Key))
            {
                CollectScalar(property.Value, result);
                continue;
            }
            if (descriptor.ArrayProperties.Contains(property.Key))
            {
                CollectArray(property.Value, result);
                continue;
            }
            if (descriptor.EntityType == NameConflictEntityTypes.Studio
                && isFilterObject
                && objectFilterIsForEntity
                && property.Key.Equals("parentId", StringComparison.OrdinalIgnoreCase))
            {
                CollectScalar(property.Value, result);
                continue;
            }
            var childIsEntityCriterion = descriptor.CriterionProperties.Contains(property.Key)
                || descriptor.EntityType == NameConflictEntityTypes.Studio
                    && objectFilterIsForEntity
                    && (property.Key.Equals("parentsCriterion", StringComparison.OrdinalIgnoreCase)
                        || property.Key.Equals("childrenCriterion", StringComparison.OrdinalIgnoreCase));
            if (childIsEntityCriterion)
            {
                CollectNode(descriptor, property.Value, result, isEntityFilter, true, false);
                continue;
            }
            if (isEntityCriterion && CriterionIdArrayProperties.Contains(property.Key))
            {
                CollectArray(property.Value, result);
                continue;
            }
            if (customFieldIsEntity
                && (property.Key.Equals("value", StringComparison.OrdinalIgnoreCase)
                    || property.Key.Equals("value2", StringComparison.OrdinalIgnoreCase)))
            {
                CollectScalar(property.Value, result);
                continue;
            }
            if (property.Key.Equals("objectFilters", StringComparison.OrdinalIgnoreCase)
                && property.Value is JsonObject objectFilters)
            {
                foreach (var objectFilter in objectFilters)
                    if (objectFilter.Value != null)
                        CollectNode(descriptor, objectFilter.Value, result, IsEntityName(objectFilter.Key, descriptor.EntityType), false, true);
                continue;
            }
            if (property.Key.Equals("objectFilter", StringComparison.OrdinalIgnoreCase))
            {
                CollectNode(descriptor, property.Value, result, objectFilterIsForEntity, false, true);
                continue;
            }
            CollectNode(descriptor, property.Value, result, isEntityFilter, isEntityCriterion, false);
        }
    }

    private static bool RewriteEntityAttributeScope(JsonNode node, string? scopeKind, IReadOnlyDictionary<int, int> idMap)
    {
        if (node is not JsonObject scope)
            return false;
        if (string.Equals(scopeKind, "attribute", StringComparison.OrdinalIgnoreCase))
            return RewriteAttribute(scope, idMap);
        if (!string.Equals(scopeKind, "expression", StringComparison.OrdinalIgnoreCase))
            return false;

        var changed = false;
        if (GetProperty(scope, "rules") is JsonArray rules)
            foreach (var rule in rules.Where(rule => rule != null))
                changed |= RewriteEmbeddedEntityRule(rule!, idMap);
        if (GetProperty(scope, "rule") is { } singleRule)
            changed |= RewriteEmbeddedEntityRule(singleRule, idMap);
        return changed;
    }

    private static bool RewriteEmbeddedEntityRule(JsonNode node, IReadOnlyDictionary<int, int> idMap)
    {
        if (node is not JsonObject rule)
            return false;
        var scopeKind = ReadString(GetProperty(rule, "scopeKind", "scope_kind"));
        var scopeValue = GetProperty(rule, "scopeValue", "scope_value");
        return scopeValue != null && RewriteEntityAttributeScope(scopeValue, scopeKind, idMap);
    }

    private static void CollectEntityAttributeScope(JsonNode node, string? scopeKind, ISet<int> result)
    {
        if (node is not JsonObject scope)
            return;
        if (string.Equals(scopeKind, "attribute", StringComparison.OrdinalIgnoreCase))
        {
            CollectAttribute(scope, result);
            return;
        }
        if (!string.Equals(scopeKind, "expression", StringComparison.OrdinalIgnoreCase))
            return;
        if (GetProperty(scope, "rules") is JsonArray rules)
            foreach (var rule in rules.Where(rule => rule != null))
                CollectEmbeddedEntityRule(rule!, result);
        if (GetProperty(scope, "rule") is { } singleRule)
            CollectEmbeddedEntityRule(singleRule, result);
    }

    private static void CollectEmbeddedEntityRule(JsonNode node, ISet<int> result)
    {
        if (node is not JsonObject rule)
            return;
        var scopeKind = ReadString(GetProperty(rule, "scopeKind", "scope_kind"));
        var scopeValue = GetProperty(rule, "scopeValue", "scope_value");
        if (scopeValue != null)
            CollectEntityAttributeScope(scopeValue, scopeKind, result);
    }

    private static bool RewriteAttribute(JsonObject scope, IReadOnlyDictionary<int, int> idMap)
    {
        var path = ReadString(GetProperty(scope, "path", "field"));
        if (!string.Equals(path, "id", StringComparison.OrdinalIgnoreCase))
            return false;
        var changed = false;
        foreach (var propertyName in new[] { "equals", "notEquals", "in" })
        {
            var property = scope.FirstOrDefault(entry => entry.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (property.Value == null || !CollectIds(property.Value).Any(id => IsChangedMapping(id, idMap)))
                continue;
            scope[property.Key] = property.Value is JsonArray
                ? RewriteArray(property.Value, idMap)
                : RewriteScalar(property.Value, idMap);
            changed = true;
        }
        return changed;
    }

    private static void CollectAttribute(JsonObject scope, ISet<int> result)
    {
        var path = ReadString(GetProperty(scope, "path", "field"));
        if (!string.Equals(path, "id", StringComparison.OrdinalIgnoreCase))
            return;
        foreach (var propertyName in new[] { "equals", "notEquals", "in" })
        {
            var value = scope.FirstOrDefault(entry => entry.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)).Value;
            if (value != null)
                CollectArray(value, result);
        }
    }

    private static JsonNode RewriteArray(JsonNode node, IReadOnlyDictionary<int, int> idMap)
    {
        if (node is not JsonArray array)
            return RewriteScalar(node, idMap);
        var rewritten = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.Where(item => item != null))
        {
            var mapped = RewriteScalar(item!, idMap);
            if (seen.Add(mapped.ToJsonString()))
                rewritten.Add(mapped);
        }
        return rewritten;
    }

    private static JsonNode RewriteScalar(JsonNode node, IReadOnlyDictionary<int, int> idMap)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var numericId) && idMap.TryGetValue(numericId, out var mappedNumericId))
                return JsonValue.Create(mappedNumericId)!;
            if (value.TryGetValue<string>(out var stringId)
                && int.TryParse(stringId, out var parsedId)
                && idMap.TryGetValue(parsedId, out var mappedStringId))
                return JsonValue.Create(mappedStringId.ToString())!;
        }
        return node.DeepClone();
    }

    private static string? RewriteRootId(string? json, IReadOnlyDictionary<int, int> idMap)
    {
        if (ParseNode(json) is not { } root)
            return json;
        var ids = new HashSet<int>();
        CollectScalar(root, ids);
        return ids.Any(id => IsChangedMapping(id, idMap))
            ? RewriteScalar(root, idMap).ToJsonString()
            : json;
    }

    private static void CollectArray(JsonNode node, ISet<int> result)
    {
        if (node is not JsonArray array)
        {
            CollectScalar(node, result);
            return;
        }
        foreach (var item in array.Where(item => item != null))
            CollectScalar(item!, result);
    }

    private static void CollectScalar(JsonNode node, ISet<int> result)
    {
        if (node is not JsonValue value)
            return;
        if (value.TryGetValue<int>(out var numericId))
            result.Add(numericId);
        else if (value.TryGetValue<string>(out var stringId) && int.TryParse(stringId, out var parsedId))
            result.Add(parsedId);
    }

    private static IEnumerable<int> CollectIds(JsonNode node)
    {
        var result = new HashSet<int>();
        CollectArray(node, result);
        return result;
    }

    private static bool ObjectDeclaresEntity(JsonObject obj, string entityType)
    {
        var entity = ReadString(GetProperty(obj, "entityType", "entity_type", "mode"));
        return IsEntityName(entity, entityType);
    }

    private static bool IsEntityName(string? value, string entityType)
    {
        var normalized = value?.Trim().TrimEnd('s');
        return string.Equals(normalized, entityType, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? GetProperty(JsonObject obj, params string[] names)
    {
        foreach (var property in obj)
            if (names.Any(name => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return property.Value;
        return null;
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsChangedMapping(int id, IReadOnlyDictionary<int, int> idMap)
        => idMap.TryGetValue(id, out var mapped) && mapped != id;

    private static Descriptor Describe(string entityType)
        => entityType switch
        {
            NameConflictEntityTypes.Performer => new(
                entityType,
                new HashSet<string>(["performerId", "localPerformerId"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["performerIds"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["performersCriterion", "rawPerformersCriterion", "topSuggestionPerformersCriterion"], StringComparer.OrdinalIgnoreCase)),
            NameConflictEntityTypes.Studio => new(
                entityType,
                new HashSet<string>(["studioId", "parentStudioId"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["studioIds"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(["studiosCriterion"], StringComparer.OrdinalIgnoreCase)),
            _ => throw new ArgumentException("The requested entity type does not have JSON-reference rules.", nameof(entityType)),
        };

    private sealed record Descriptor(
        string EntityType,
        HashSet<string> ScalarProperties,
        HashSet<string> ArrayProperties,
        HashSet<string> CriterionProperties);
}
