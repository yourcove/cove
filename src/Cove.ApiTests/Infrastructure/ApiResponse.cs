using System.Text.Json;

namespace Cove.ApiTests.Infrastructure;

internal static class ApiResponse
{
    public static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        string requestDescription,
        CancellationToken cancellationToken = default)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{requestDescription} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {Truncate(body)}");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, ApiJson.Options)
                ?? throw new InvalidOperationException($"{requestDescription} returned an empty JSON value.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"{requestDescription} returned invalid JSON. Response: {Truncate(body)}",
                exception);
        }
    }

    private static string Truncate(string body)
        => body.Length <= 2_000 ? body : body[..2_000] + "...";
}
