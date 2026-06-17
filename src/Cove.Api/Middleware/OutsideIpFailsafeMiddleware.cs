using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Interfaces;

namespace Cove.Api.Middleware;

public sealed class OutsideIpFailsafeMiddleware
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private readonly RequestDelegate _next;

    public OutsideIpFailsafeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        CoveConfiguration config,
        ConfigService configService,
        IUserService users,
        IAuditService audit,
        ILogger<OutsideIpFailsafeMiddleware> logger)
    {
        var remoteAddress = AuthDisabledRequestGuard.GetEffectiveRemoteAddress(context, config.Auth);
        if (config.Auth.Enabled || AuthDisabledRequestGuard.IsTrustedLocalRequest(context, config.Auth))
        {
            await _next(context);
            return;
        }

        // Auth is disabled and the request is from an untrusted (public/remote) address. Hold off the
        // failsafe lockdown until initial setup is complete — i.e. until an owner account exists. Until
        // then, a first-run visitor (e.g. reaching Cove through a reverse proxy) must be able to load
        // the setup wizard and create the owner password without needing a token, so we let the request
        // through. Once an owner exists, a public request while auth is disabled trips the lockdown.
        bool ownerExists;
        try
        {
            ownerExists = await users.OwnerExistsAsync(context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not determine owner account status during auth failsafe; allowing request to proceed to setup.");
            await _next(context);
            return;
        }

        if (!ownerExists)
        {
            await _next(context);
            return;
        }

        var ip = remoteAddress?.ToString();
        var ua = context.Request.Headers.UserAgent.ToString();

        await Lock.WaitAsync(context.RequestAborted);
        try
        {
            if (!config.Auth.Enabled)
            {
                config.Auth.Enabled = true;
                try
                {
                    await configService.SaveCurrentConfigAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to persist auth failsafe enablement after request from {RemoteIp}", ip ?? "unknown");
                }
            }
        }
        finally
        {
            Lock.Release();
        }

        await audit.LogAsync(
            AuditActions.AuthFailsafeEnabled,
            AuditOutcomes.Deny,
            CovePrincipal.Anonymous(ip, ua),
            "auth",
            "enabled",
            new
            {
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
    }
}