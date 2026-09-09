using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cove.Api.Services;

public static partial class ObservabilityRedactor
{
    private const string Redacted = "[REDACTED]";

    public static string? RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var redacted = CoveCredentialRegex().Replace(value, Redacted);
        redacted = BearerCredentialRegex().Replace(redacted, "$1" + Redacted);
        return SensitiveAssignmentRegex().Replace(redacted, "$1" + Redacted);
    }

    public static string? RedactJson(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        try
        {
            var node = RedactNode(JsonNode.Parse(value));
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch (JsonException)
        {
            return RedactText(value);
        }
    }

    private static JsonNode? RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (IsSensitiveName(property.Key))
                    obj[property.Key] = Redacted;
                else
                    obj[property.Key] = RedactNode(property.Value);
            }
            return obj;
        }

        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                array[index] = RedactNode(array[index]);
            return array;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return JsonValue.Create(RedactText(text));

        return node;
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized is "password" or "token" or "accesstoken" or "refreshtoken"
            or "sharetoken" or "sharepassword" or "apitoken" or "apikey" or "secret" or "authorization";
    }

    [GeneratedRegex(@"cove_(?:pat|share)_[A-Fa-f0-9]{32}_[A-Za-z0-9_-]+", RegexOptions.IgnoreCase)]
    private static partial Regex CoveCredentialRegex();

    [GeneratedRegex(@"(?i)(\bBearer\s+)[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerCredentialRegex();

    [GeneratedRegex("(?i)(\\b(?:access[_-]?token|refresh[_-]?token|share[_-]?token|share[_-]?password|api[_-]?token|api[_-]?key|password|token|secret|authorization)\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;&]+)")]
    private static partial Regex SensitiveAssignmentRegex();
}
