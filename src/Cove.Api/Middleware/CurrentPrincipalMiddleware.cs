using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data.Auth;
using Cove.Plugins;

namespace Cove.Api.Middleware;

/// <summary>
/// Resolves the bearer/API token from the incoming request and populates
/// <see cref="ICurrentPrincipalAccessor"/> for the duration of the request.
/// </summary>
public sealed class CurrentPrincipalMiddleware
{
    private const string AccessCookieName = "cove_access_token";
    private readonly RequestDelegate _next;

    public CurrentPrincipalMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ITokenService tokens,
        IExistingUserPrincipalResolver existingUsers,
        IExternalIdentityService externalIdentities,
        IShareLinkService shareLinks,
        ICurrentPrincipalAccessor accessor,
        CoveConfiguration config,
        AuthBypassPrincipalProvider authBypassPrincipalProvider,
        ConfigService configService,
        IAuditService audit,
        ILogger<CurrentPrincipalMiddleware> logger)
    {
        var ip = AuthDisabledRequestGuard.GetEffectiveRemoteAddress(context, config.Auth)?.ToString();
        var ua = context.Request.Headers.UserAgent.ToString();

        if (!config.Auth.Enabled)
        {
            if (AuthDisabledRequestGuard.IsTrustedLocalRequest(context, config.Auth))
            {
                accessor.Set(await authBypassPrincipalProvider.GetAsync(ip, ua, context.RequestAborted));
                await _next(context);
                return;
            }

            config.Auth.Enabled = true;

            try
            {
                await configService.SaveCurrentConfigAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist automatic auth lockdown after remote request from {RemoteIp}", ip ?? "unknown");
            }

            await audit.LogAsync(
                AuditActions.SettingsChange,
                AuditOutcomes.Deny,
                CovePrincipal.Anonymous(ip, ua),
                "auth",
                "enabled",
                new
                {
                    reason = "public_request_lockdown",
                    method = context.Request.Method,
                    path = context.Request.Path.Value,
                    remoteIp = ip,
                },
                context.RequestAborted);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "AUTH_LOCKDOWN_TRIGGERED",
                message = "Authentication was automatically enabled after a public remote request was detected while authentication was disabled.",
            }, context.RequestAborted);
            return;
        }

        string? shareToken = context.Request.Headers["X-Share-Token"].ToString();
        if (string.IsNullOrWhiteSpace(shareToken))
        {
            var qsShareToken = context.Request.Query["share_token"].ToString();
            if (!string.IsNullOrWhiteSpace(qsShareToken) && AllowsQueryToken(context.Request))
                shareToken = qsShareToken;
        }

        string? sharePassword = context.Request.Headers["X-Share-Password"].ToString();
        if (string.IsNullOrWhiteSpace(sharePassword))
        {
            var qsSharePassword = context.Request.Query["share_password"].ToString();
            if (!string.IsNullOrWhiteSpace(qsSharePassword) && AllowsQueryToken(context.Request))
                sharePassword = qsSharePassword;
        }

        // Allow SignalR / file-stream endpoints to pass the token via ?access_token=
        string? authHeader = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader))
        {
            var qsToken = context.Request.Query["access_token"].ToString();
            if (!string.IsNullOrEmpty(qsToken) && AllowsQueryToken(context.Request))
                authHeader = "Bearer " + qsToken;
        }

        if (string.IsNullOrEmpty(authHeader)
            && context.Request.Cookies.TryGetValue(AccessCookieName, out var cookieToken)
            && !string.IsNullOrEmpty(cookieToken))
        {
            authHeader = "Bearer " + cookieToken;
        }

        CovePrincipal? principal = null;
        if (!string.IsNullOrWhiteSpace(shareToken))
        {
            principal = await shareLinks.ResolveAsync(
                shareToken,
                string.IsNullOrWhiteSpace(sharePassword) ? null : sharePassword,
                ip,
                ua,
                context.RequestAborted);
        }
        else if (!string.IsNullOrEmpty(authHeader))
            principal = await tokens.ResolveAsync(authHeader, ip, ua, context.RequestAborted);

        if (context.TryGetExtensionIdentityAssertion(out var assertion)
            && (principal is null
                || assertion.IsAuthoritative && principal.Kind != PrincipalKind.ShareLink))
        {
            var userId = await externalIdentities.ResolveUserIdAsync(assertion, context.RequestAborted);
            CovePrincipal? assertedPrincipal = null;
            if (userId is int linkedUserId)
            {
                assertedPrincipal = await existingUsers.ResolveExistingUserAsync(
                    linkedUserId,
                    ip,
                    ua,
                    context.RequestAborted);
                if (assertedPrincipal is not null)
                    await externalIdentities.MarkUsedAsync(assertion, context.RequestAborted);
            }

            if (assertedPrincipal is not null)
            {
                if (principal is null || principal.UserId != assertedPrincipal.UserId)
                {
                    if (assertion.IsAuthoritative && principal is not null)
                    {
                        logger.LogDebug(
                            "Authoritative authentication assertion from extension {ExtensionId} replaced Cove principal {PreviousUserId} with {AssertedUserId}",
                            assertion.ExtensionId,
                            principal.UserId,
                            assertedPrincipal.UserId);
                    }
                    principal = assertedPrincipal;
                }
            }
            else
            {
                if (assertion.IsAuthoritative)
                    principal = null;
                logger.LogDebug(
                    "Authentication assertion from extension {ExtensionId} using {Method} did not resolve to a usable Cove user",
                    assertion.ExtensionId,
                    assertion.Method);
            }
        }

        if (principal is not null)
        {
            accessor.Set(principal);
        }
        else
        {
            accessor.Set(CovePrincipal.Anonymous(ip, ua));
        }
        await _next(context);
    }

    public static bool AllowsQueryToken(HttpRequest request)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/hubs"))
            return true;

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;

        var value = path.Value?.ToLowerInvariant() ?? string.Empty;
        if (value.StartsWith("/api/stream/", StringComparison.Ordinal))
            return true;

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || segments[0] != "api")
            return false;

        var resource = segments[1];
        var action = segments[3];
        if (resource == "audios" && action is "stream" or "image")
            return true;
        if (resource == "texts" && action is "file" or "image")
            return true;
        if (resource is "videos" or "segments" or "performers" or "studios" or "tags" or "galleries"
            && action is "image" or "cover")
            return true;
        if (resource == "groups" && action == "image")
            return true;

        return false;
    }
}
