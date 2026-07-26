namespace Cove.Api.Http;

/// <summary>
/// Preserves query-string credentials across same-origin API redirects. Image elements cannot send
/// an Authorization header, so the UI, share-link viewers, and API clients authenticate media URLs
/// with <c>?access_token=</c> / <c>?share_token=</c> / <c>?share_password=</c>. Endpoints that
/// <c>Redirect()</c> to another API URL (e.g. a group cover falling back to a member video's
/// screenshot) must carry those credentials forward — the browser follows the redirect verbatim,
/// so a dropped token turns into a 401 for every client that has no session cookie.
/// </summary>
internal static class QueryCredentials
{
    private static readonly string[] Names = ["access_token", "share_token", "share_password"];

    /// <summary>Appends any credential query parameters present on <paramref name="request"/> to
    /// <paramref name="url"/>. URLs without incoming credentials are returned unchanged.</summary>
    public static string Preserve(HttpRequest request, string url)
    {
        List<string>? creds = null;
        foreach (var name in Names)
        {
            var value = request.Query[name].ToString();
            if (string.IsNullOrEmpty(value)) continue;
            (creds ??= []).Add($"{name}={Uri.EscapeDataString(value)}");
        }
        if (creds == null) return url;
        return url + (url.Contains('?') ? '&' : '?') + string.Join('&', creds);
    }
}
