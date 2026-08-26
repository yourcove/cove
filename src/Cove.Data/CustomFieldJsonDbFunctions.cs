using System.Text.Json;

namespace Cove.Data;

/// <summary>
/// Database-only JSON pointer extractors shared by custom-field queries and their managed indexes.
/// </summary>
public static class CustomFieldJsonDbFunctions
{
    public const int TextIndexKeyByteLength = 1024;

    public static string? Text(JsonElement? document, string pointer)
        => throw new InvalidOperationException("This method can only be evaluated by PostgreSQL.");

    public static byte[]? TextIndexKey(JsonElement? document, string pointer)
        => throw new InvalidOperationException("This method can only be evaluated by PostgreSQL.");

    public static decimal? Number(JsonElement? document, string pointer)
        => throw new InvalidOperationException("This method can only be evaluated by PostgreSQL.");

    public static bool? Boolean(JsonElement? document, string pointer)
        => throw new InvalidOperationException("This method can only be evaluated by PostgreSQL.");
}
