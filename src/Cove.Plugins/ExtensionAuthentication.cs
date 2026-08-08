using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;

namespace Cove.Plugins;

/// <summary>
/// Legacy username-only assertion retained for binary compatibility. Cove no longer accepts this
/// ambiguous identity shape; authentication extensions must use <see cref="ExtensionIdentityAssertion"/>.
/// </summary>
[Obsolete("Username-only external authentication is no longer accepted. Use ExtensionIdentityAssertion.")]
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
    IdentityUnlinked,
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
    /// Convert an extension-verified, already-linked identity into a short-lived, browser-bound Cove
    /// login code. Cove accepts only an existing active, unlocked user.
    /// </summary>
    Task<ExtensionLoginCompletion> CompleteAsync(
        HttpContext context,
        string browserBinding,
        ExtensionIdentityAssertion assertion,
        CancellationToken ct = default);

    /// <summary>
    /// Legacy username completion retained so an older extension fails closed instead of crashing
    /// after a Cove upgrade. Username-only identities cannot create a login ticket.
    /// </summary>
    [Obsolete("Username-only external authentication is no longer accepted. Use the identity assertion overload.")]
    Task<ExtensionLoginCompletion> CompleteAsync(
        HttpContext context,
        string browserBinding,
        string extensionId,
        string username,
        CancellationToken ct = default) => Task.FromResult(new ExtensionLoginCompletion(
            null,
            ExtensionLoginCompletionFailure.InvalidRequest));

    /// <summary>
    /// Atomically redeem a code from the same browser that began the flow and issue the Cove
    /// session only after that redemption succeeds.
    /// </summary>
    Task<ExtensionLoginRedemption?> RedeemAsync(
        HttpContext context,
        string code,
        CancellationToken ct = default);
}

public sealed record ExtensionIdentityLinkIntent(
    string Token,
    string BrowserBinding);

public enum ExtensionIdentityLinkPreparationFailure
{
    None,
    InvalidRequest,
    BrowserMismatch,
    IdentityConflict,
}

public sealed record ExtensionIdentityLinkPreparation(
    string? Code,
    ExtensionIdentityLinkPreparationFailure Failure);

/// <summary>
/// Host-owned bridge for explicitly linking an extension-validated identity to the Cove user who
/// starts and later confirms the flow. A provider callback only prepares a candidate; it cannot
/// persist a link by itself.
/// </summary>
public interface IExtensionIdentityLinkService
{
    ExtensionIdentityLinkIntent? BeginLink(
        HttpContext context,
        string extensionId,
        string providerId);

    Task<ExtensionIdentityLinkPreparation> PrepareLinkAsync(
        HttpContext context,
        string intentToken,
        string browserBinding,
        ExtensionIdentityAssertion assertion,
        CancellationToken ct = default);

    /// <summary>
    /// Prepare a link for an identity that the extension can validate on the authenticated start
    /// request itself (for example, a trusted reverse-proxy header). Cove creates a fresh browser
    /// binding and still requires the user to confirm the returned one-time code.
    /// </summary>
    Task<ExtensionIdentityLinkPreparation> PrepareDirectLinkAsync(
        HttpContext context,
        ExtensionIdentityAssertion assertion,
        CancellationToken ct = default);
}

/// <summary>
/// Request-local bridge between extension middleware and Cove's principal resolver.
/// </summary>
public static class ExtensionAuthenticationHttpContextExtensions
{
    private static readonly object IdentityAssertionItemKey = new();

    /// <summary>
    /// Submit a stable external identity assertion for the current request. The first valid assertion
    /// wins; later middleware cannot replace it.
    /// </summary>
    public static bool TrySetExtensionIdentityAssertion(
        this HttpContext context,
        ExtensionIdentityAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assertion);

        if (!TryNormalize(assertion, out var normalized)
            || context.Items.ContainsKey(IdentityAssertionItemKey))
        {
            return false;
        }

        context.Items[IdentityAssertionItemKey] = normalized;
        return true;
    }

    /// <summary>Read the external identity assertion, if one was submitted.</summary>
    public static bool TryGetExtensionIdentityAssertion(
        this HttpContext context,
        out ExtensionIdentityAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(IdentityAssertionItemKey, out var raw)
            && raw is ExtensionIdentityAssertion value)
        {
            assertion = value;
            return true;
        }

        assertion = null!;
        return false;
    }

    /// <summary>Legacy username assertions deliberately fail closed.</summary>
    [Obsolete("Username-only external authentication is no longer accepted. Use TrySetExtensionIdentityAssertion.")]
    public static bool TrySetExtensionUserAssertion(
        this HttpContext context,
        ExtensionUserAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assertion);
        return false;
    }

    /// <summary>Legacy username assertions are never present.</summary>
    [Obsolete("Username-only external authentication is no longer accepted. Use TryGetExtensionIdentityAssertion.")]
    public static bool TryGetExtensionUserAssertion(
        this HttpContext context,
        out ExtensionUserAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(context);
        assertion = null!;
        return false;
    }

    private static bool TryNormalize(
        ExtensionIdentityAssertion assertion,
        out ExtensionIdentityAssertion normalized)
    {
        var extensionId = assertion.ExtensionId?.Trim();
        var providerId = assertion.ProviderId?.Trim();
        var subject = assertion.Subject;
        var method = assertion.Method?.Trim();
        var providerLabel = assertion.ProviderLabel?.Trim();
        var accountLabel = string.IsNullOrWhiteSpace(assertion.AccountLabel)
            ? null
            : assertion.AccountLabel.Trim();
        if (!IsValid(extensionId, 256)
            || !IsValid(providerId, 512)
            || !IsValid(subject, 512)
            || !IsValid(method, 128)
            || !IsValid(providerLabel, 128)
            || (accountLabel is not null && !IsValid(accountLabel, 256)))
        {
            normalized = null!;
            return false;
        }

        normalized = new ExtensionIdentityAssertion(
            extensionId!,
            providerId!,
            subject!,
            method!,
            providerLabel!,
            accountLabel);
        return true;
    }

    private static bool IsValid(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);
}
