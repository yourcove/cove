using System.Text.Json;
using System.Text.Json.Nodes;
using Cove.Core.Entities;

namespace Cove.Data.Services;

/// <summary>
/// Rewrites tag identifiers stored inside Cove-owned JSON documents. This intentionally recognizes
/// only documented tag-bearing shapes; unrelated numeric identifiers must never be rewritten merely
/// because they happen to equal a merged tag id.
/// </summary>
internal static class TagReferenceJsonRewriter
{
    private static readonly HashSet<string> TagIdProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "tagId",
    };

    private static readonly HashSet<string> TagIdArrayProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "tagIds",
        "secondaryTagIds",
    };

    private static readonly HashSet<string> TagCriterionProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "tagsCriterion",
        "performerTagsCriterion",
        "videoTagsCriterion",
        "rawTagsCriterion",
    };

    private static readonly HashSet<string> CriterionIdArrayProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "value",
        "excludes",
        "requiredIds",
    };

    public static string? Rewrite(string? json, IReadOnlyDictionary<int, int> tagIdMap, bool isTagFilter = false)
    {
        if (string.IsNullOrWhiteSpace(json) || tagIdMap.Count == 0)
            return json;

        if (!FindTagIds(json, isTagFilter).Any(tagId => IsChangedMapping(tagId, tagIdMap)))
            return json;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root == null)
            return json;

        RewriteNode(root, tagIdMap, isTagFilter, false);
        return root.ToJsonString();
    }

    public static JsonDocument? Rewrite(JsonDocument? document, IReadOnlyDictionary<int, int> tagIdMap)
    {
        if (document == null)
            return null;

        var original = document.RootElement.GetRawText();
        var rewritten = Rewrite(original, tagIdMap);
        return rewritten == null
            ? null
            : string.Equals(original, rewritten, StringComparison.Ordinal)
                ? document
                : JsonDocument.Parse(rewritten);
    }

    public static string? RewriteRoleContentScope(
        string entityKind,
        string scopeKind,
        string? json,
        IReadOnlyDictionary<int, int> tagIdMap)
    {
        var genericallyRewritten = Rewrite(json, tagIdMap);
        if (!IsTagEntityName(entityKind)
            || string.IsNullOrWhiteSpace(genericallyRewritten)
            || tagIdMap.Count == 0)
            return genericallyRewritten;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(genericallyRewritten);
        }
        catch (JsonException)
        {
            return genericallyRewritten;
        }

        return root != null && RewriteTagEntityAttributeScope(root, scopeKind, tagIdMap)
            ? root.ToJsonString()
            : genericallyRewritten;
    }

    public static IReadOnlySet<int> FindTagIds(string? json, bool isTagFilter = false, bool rootIsTagIdArray = false)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        if (root == null)
            return result;

        if (rootIsTagIdArray)
            CollectArray(root, result);
        else
            CollectNode(root, result, isTagFilter, false);
        return result;
    }

    public static IReadOnlySet<int> FindRoleContentTagIds(string entityKind, string scopeKind, string? json)
    {
        var result = new HashSet<int>(FindTagIds(json));
        if (!IsTagEntityName(entityKind) || string.IsNullOrWhiteSpace(json))
            return result;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        if (root != null)
            CollectTagEntityAttributeScope(root, scopeKind, result);
        return result;
    }

    public static string? RewriteUserUiPreferences(
        string? json,
        IReadOnlyDictionary<int, int> tagIdMap)
    {
        if (string.IsNullOrWhiteSpace(json)
            || tagIdMap.Count == 0
            || ParseNode(json) is not JsonObject preferences
            || GetProperty(preferences, "defaultFilters", "default_filters") is not JsonObject defaultFilters)
            return json;

        var changed = false;
        foreach (var filter in defaultFilters.ToArray())
        {
            var serializedFilter = ReadString(filter.Value);
            if (serializedFilter == null)
                continue;

            var rewritten = Rewrite(serializedFilter, tagIdMap, IsTagEntityName(filter.Key));
            if (rewritten == null || string.Equals(rewritten, serializedFilter, StringComparison.Ordinal))
                continue;

            defaultFilters[filter.Key] = rewritten;
            changed = true;
        }

        return changed ? preferences.ToJsonString() : json;
    }

    public static IReadOnlySet<int> FindUserUiPreferenceTagIds(string? json)
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

            foreach (var tagId in FindTagIds(serializedFilter, IsTagEntityName(filter.Key)))
                result.Add(tagId);
        }

        return result;
    }

    public static string? RewriteFieldProvenanceValue(
        AffinityHostType hostType,
        string fieldKey,
        string? json,
        IReadOnlyDictionary<int, int> tagIdMap)
    {
        if (hostType != AffinityHostType.Segment)
            return json;

        if (fieldKey.Trim().Equals("tag_id", StringComparison.OrdinalIgnoreCase))
            return RewriteRootTagId(json, tagIdMap);

        return fieldKey.Trim().Equals("payload", StringComparison.OrdinalIgnoreCase)
            ? Rewrite(json, tagIdMap)
            : json;
    }

    public static IReadOnlySet<int> FindFieldProvenanceTagIds(
        AffinityHostType hostType,
        string fieldKey,
        string? json)
    {
        if (hostType != AffinityHostType.Segment)
            return new HashSet<int>();

        if (fieldKey.Trim().Equals("tag_id", StringComparison.OrdinalIgnoreCase))
        {
            var result = new HashSet<int>();
            if (ParseNode(json) is { } root)
                CollectScalar(root, result);
            return result;
        }

        return fieldKey.Trim().Equals("payload", StringComparison.OrdinalIgnoreCase)
            ? FindTagIds(json)
            : new HashSet<int>();
    }

    private static void RewriteNode(
        JsonNode node,
        IReadOnlyDictionary<int, int> tagIdMap,
        bool isTagFilter,
        bool isTagCriterion)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child != null).ToArray())
                RewriteNode(child!, tagIdMap, isTagFilter, isTagCriterion);
            return;
        }

        if (node is not JsonObject obj)
            return;

        var customFieldIsTag = obj.TryGetPropertyValue("type", out var typeNode)
            && string.Equals(ReadString(typeNode), "tag", StringComparison.OrdinalIgnoreCase);
        var objectFilterIsForTags = isTagFilter || ObjectDeclaresTagEntity(obj);

        foreach (var property in obj.ToArray())
        {
            if (property.Value == null)
                continue;

            if (TagIdProperties.Contains(property.Key))
            {
                obj[property.Key] = RewriteScalar(property.Value, tagIdMap);
                continue;
            }

            if (TagIdArrayProperties.Contains(property.Key))
            {
                obj[property.Key] = RewriteArray(property.Value, tagIdMap);
                continue;
            }

            var childIsTagCriterion = TagCriterionProperties.Contains(property.Key)
                || (isTagFilter && (property.Key.Equals("parentsCriterion", StringComparison.OrdinalIgnoreCase)
                    || property.Key.Equals("childrenCriterion", StringComparison.OrdinalIgnoreCase)));
            if (childIsTagCriterion)
            {
                RewriteNode(property.Value, tagIdMap, isTagFilter, true);
                continue;
            }

            if (isTagCriterion && CriterionIdArrayProperties.Contains(property.Key))
            {
                obj[property.Key] = RewriteArray(property.Value, tagIdMap);
                continue;
            }

            if (customFieldIsTag
                && (property.Key.Equals("value", StringComparison.OrdinalIgnoreCase)
                    || property.Key.Equals("value2", StringComparison.OrdinalIgnoreCase)))
            {
                obj[property.Key] = RewriteScalar(property.Value, tagIdMap);
                continue;
            }

            if (property.Key.Equals("objectFilters", StringComparison.OrdinalIgnoreCase)
                && property.Value is JsonObject objectFilters)
            {
                foreach (var objectFilter in objectFilters.ToArray())
                    if (objectFilter.Value != null)
                        RewriteNode(objectFilter.Value, tagIdMap, IsTagEntityName(objectFilter.Key), false);
                continue;
            }

            if (property.Key.Equals("objectFilter", StringComparison.OrdinalIgnoreCase))
            {
                RewriteNode(property.Value, tagIdMap, objectFilterIsForTags, false);
                continue;
            }

            RewriteNode(property.Value, tagIdMap, isTagFilter, isTagCriterion);
        }
    }

    private static void CollectNode(
        JsonNode node,
        ISet<int> result,
        bool isTagFilter,
        bool isTagCriterion)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array.Where(child => child != null))
                CollectNode(child!, result, isTagFilter, isTagCriterion);
            return;
        }

        if (node is not JsonObject obj)
            return;

        var customFieldIsTag = obj.TryGetPropertyValue("type", out var typeNode)
            && string.Equals(ReadString(typeNode), "tag", StringComparison.OrdinalIgnoreCase);
        var objectFilterIsForTags = isTagFilter || ObjectDeclaresTagEntity(obj);

        foreach (var property in obj)
        {
            if (property.Value == null)
                continue;

            if (TagIdProperties.Contains(property.Key))
            {
                CollectScalar(property.Value, result);
                continue;
            }

            if (TagIdArrayProperties.Contains(property.Key))
            {
                CollectArray(property.Value, result);
                continue;
            }

            var childIsTagCriterion = TagCriterionProperties.Contains(property.Key)
                || (isTagFilter && (property.Key.Equals("parentsCriterion", StringComparison.OrdinalIgnoreCase)
                    || property.Key.Equals("childrenCriterion", StringComparison.OrdinalIgnoreCase)));
            if (childIsTagCriterion)
            {
                CollectNode(property.Value, result, isTagFilter, true);
                continue;
            }

            if (isTagCriterion && CriterionIdArrayProperties.Contains(property.Key))
            {
                CollectArray(property.Value, result);
                continue;
            }

            if (customFieldIsTag
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
                        CollectNode(objectFilter.Value, result, IsTagEntityName(objectFilter.Key), false);
                continue;
            }

            if (property.Key.Equals("objectFilter", StringComparison.OrdinalIgnoreCase))
            {
                CollectNode(property.Value, result, objectFilterIsForTags, false);
                continue;
            }

            CollectNode(property.Value, result, isTagFilter, isTagCriterion);
        }
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

    private static JsonNode RewriteArray(JsonNode node, IReadOnlyDictionary<int, int> tagIdMap)
    {
        if (node is not JsonArray array)
            return RewriteScalar(node, tagIdMap);

        var rewritten = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item == null)
                continue;

            var mapped = RewriteScalar(item, tagIdMap);
            var identity = mapped.ToJsonString();
            if (seen.Add(identity))
                rewritten.Add(mapped);
        }

        return rewritten;
    }

    private static JsonNode RewriteScalar(JsonNode node, IReadOnlyDictionary<int, int> tagIdMap)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var numericId) && tagIdMap.TryGetValue(numericId, out var mappedNumericId))
                return JsonValue.Create(mappedNumericId)!;

            if (value.TryGetValue<string>(out var stringId)
                && int.TryParse(stringId, out var parsedId)
                && tagIdMap.TryGetValue(parsedId, out var mappedStringId))
                return JsonValue.Create(mappedStringId.ToString())!;
        }

        return node.DeepClone();
    }

    private static string? RewriteRootTagId(string? json, IReadOnlyDictionary<int, int> tagIdMap)
    {
        if (string.IsNullOrWhiteSpace(json) || tagIdMap.Count == 0 || ParseNode(json) is not { } root)
            return json;

        var ids = new HashSet<int>();
        CollectScalar(root, ids);
        return ids.Any(tagId => IsChangedMapping(tagId, tagIdMap))
            ? RewriteScalar(root, tagIdMap).ToJsonString()
            : json;
    }

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

    private static bool RewriteTagEntityAttributeScope(
        JsonNode node,
        string? scopeKind,
        IReadOnlyDictionary<int, int> tagIdMap)
    {
        if (node is not JsonObject scope)
            return false;

        if (string.Equals(scopeKind, "attribute", StringComparison.OrdinalIgnoreCase))
        {
            var path = ReadString(GetProperty(scope, "path", "field"));
            if (!string.Equals(path, "id", StringComparison.OrdinalIgnoreCase))
                return false;

            var changed = false;
            foreach (var propertyName in new[] { "equals", "notEquals", "in" })
            {
                var property = scope.FirstOrDefault(entry => entry.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
                if (property.Value == null
                    || !CollectIds(property.Value).Any(tagId => IsChangedMapping(tagId, tagIdMap)))
                    continue;

                scope[property.Key] = property.Value is JsonArray
                    ? RewriteArray(property.Value, tagIdMap)
                    : RewriteScalar(property.Value, tagIdMap);
                changed = true;
            }
            return changed;
        }

        if (!string.Equals(scopeKind, "expression", StringComparison.OrdinalIgnoreCase))
            return false;

        var changedExpression = false;
        if (GetProperty(scope, "rules") is JsonArray rules)
            foreach (var rule in rules.Where(rule => rule != null))
                changedExpression |= RewriteEmbeddedTagEntityRule(rule!, tagIdMap);
        if (GetProperty(scope, "rule") is { } singleRule)
            changedExpression |= RewriteEmbeddedTagEntityRule(singleRule, tagIdMap);
        return changedExpression;
    }

    private static bool RewriteEmbeddedTagEntityRule(JsonNode node, IReadOnlyDictionary<int, int> tagIdMap)
    {
        if (node is not JsonObject rule)
            return false;
        var scopeKind = ReadString(GetProperty(rule, "scopeKind", "scope_kind"));
        var scopeValue = GetProperty(rule, "scopeValue", "scope_value");
        return scopeValue != null && RewriteTagEntityAttributeScope(scopeValue, scopeKind, tagIdMap);
    }

    private static void CollectTagEntityAttributeScope(JsonNode node, string? scopeKind, ISet<int> result)
    {
        if (node is not JsonObject scope)
            return;

        if (string.Equals(scopeKind, "attribute", StringComparison.OrdinalIgnoreCase))
        {
            var path = ReadString(GetProperty(scope, "path", "field"));
            if (!string.Equals(path, "id", StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var propertyName in new[] { "equals", "notEquals", "in" })
                if (GetProperty(scope, propertyName) is { } value)
                    foreach (var tagId in CollectIds(value))
                        result.Add(tagId);
            return;
        }

        if (!string.Equals(scopeKind, "expression", StringComparison.OrdinalIgnoreCase))
            return;
        if (GetProperty(scope, "rules") is JsonArray rules)
            foreach (var rule in rules.Where(rule => rule != null))
                CollectEmbeddedTagEntityRule(rule!, result);
        if (GetProperty(scope, "rule") is { } singleRule)
            CollectEmbeddedTagEntityRule(singleRule, result);
    }

    private static void CollectEmbeddedTagEntityRule(JsonNode node, ISet<int> result)
    {
        if (node is not JsonObject rule)
            return;
        var scopeKind = ReadString(GetProperty(rule, "scopeKind", "scope_kind"));
        var scopeValue = GetProperty(rule, "scopeValue", "scope_value");
        if (scopeValue != null)
            CollectTagEntityAttributeScope(scopeValue, scopeKind, result);
    }

    private static IEnumerable<int> CollectIds(JsonNode node)
    {
        var result = new HashSet<int>();
        if (node is JsonArray)
            CollectArray(node, result);
        else
            CollectScalar(node, result);
        return result;
    }

    private static JsonNode? GetProperty(JsonObject obj, params string[] names)
    {
        foreach (var property in obj)
            if (names.Any(name => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return property.Value;
        return null;
    }

    private static bool ObjectDeclaresTagEntity(JsonObject obj)
        => IsTagEntityName(ReadString(GetProperty(obj, "entityType", "entity_type")));

    private static bool IsTagEntityName(string? value)
        => string.Equals(value, "tag", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "tags", StringComparison.OrdinalIgnoreCase);

    private static bool IsChangedMapping(int tagId, IReadOnlyDictionary<int, int> tagIdMap)
        => tagIdMap.TryGetValue(tagId, out var mappedTagId) && mappedTagId != tagId;

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
