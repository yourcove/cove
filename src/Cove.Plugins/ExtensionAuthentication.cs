using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;

namespace Cove.Plugins;

/// <summary>
/// A username assertion produced by an enabled authentication extension. Cove still owns user
/// lookup, account-state checks, role expansion, and the principal placed on the request.
/// </summary>
public sealed record ExtensionUserAssertion(
    string ExtensionId,
    string Username,
    string Method);

/// <summary>Why Cove did not create a login ticket for a completed extension flow.</summary>
public enum ExtensionLoginCompletionFailure
{
    None,
    InvalidRequest,
    BrowserMismatch,
    UserRejected,
}

/// <summary>Result returned to an extension after it verifies an external identity.</summary>
public sealed record ExtensionLoginCompletion(
    string? Code,
    ExtensionLoginCompletionFailure Failure);

/// <summary>A one-time Cove session redeemed by the browser that began the external flow.</summary>
public sealed record ExtensionLoginRedemption(
    string ExtensionId,
    TokenPair TokenPair);

/// <summary>
/// Host-owned bridge for interactive authentication extensions. The extension verifies the external
/// protocol; Cove binds the flow to a browser, validates the local account, issues Cove tokens, and
/// exposes only a short-lived one-time code to the browser redirect. Extensions should carry that
/// code in the URL fragment so it is not sent to reverse proxies or access logs.
/// </summary>
public interface IExtensionLoginSessionService
{
    /// <summary>Rotate the browser binding cookie and return the value to bind into extension state.</summary>
    string BeginBrowserSession(HttpContext context);

    /// <summary>Check the callback browser before exchanging or accepting an external credential.</summary>
    bool IsBrowserSession(HttpContext context, string browserBinding);

    /// <summary>
    /// Convert an extension-verified username into a short-lived, browser-bound Cove login code.
    /// Cove accepts only an existing active, unlocked user.
    /// </summary>
    Task<ExtensionLoginCompletion> CompleteAsync(
        HttpContext context,
        string browserBinding,
        string extensionId,
        string username,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically redeem a code from the same browser that began the flow and issue the Cove
    /// session only after that redemption succeeds.
    /// </summary>
    Task<ExtensionLoginRedemption?> RedeemAsync(
        HttpContext context,
        string code,
        CancellationToken ct = default);
}

/// <summary>
/// Request-local bridge between extension middleware and Cove's principal resolver.
/// </summary>
public static class ExtensionAuthenticationHttpContextExtensions
{
    private static readonly object UserAssertionItemKey = new();

    /// <summary>
    /// Submit an external username assertion for the current request. The first valid assertion wins;
    /// later middleware cannot replace it.
    /// </summary>
    public static bool TrySetExtensionUserAssertion(
        this HttpContext context,
        ExtensionUserAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assertion);

        if (!TryNormalize(assertion, out var normalized)
            || context.Items.ContainsKey(UserAssertionItemKey))
        {
            return false;
        }

        context.Items[UserAssertionItemKey] = normalized;
        return true;
    }

    /// <summary>Read the external username assertion, if one was submitted.</summary>
    public static bool TryGetExtensionUserAssertion(
        this HttpContext context,
        out ExtensionUserAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(UserAssertionItemKey, out var raw)
            && raw is ExtensionUserAssertion value)
        {
            assertion = value;
            return true;
        }

        assertion = null!;
        return false;
    }

    private static bool TryNormalize(
        ExtensionUserAssertion assertion,
        out ExtensionUserAssertion normalized)
    {
        var extensionId = assertion.ExtensionId?.Trim();
        var username = assertion.Username?.Trim();
        var method = assertion.Method?.Trim();
        if (!IsValid(extensionId, 256)
            || !IsValid(username, 256)
            || !IsValid(method, 128))
        {
            normalized = null!;
            return false;
        }

        normalized = new ExtensionUserAssertion(extensionId!, username!, method!);
        return true;
    }

    private static bool IsValid(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);
}
