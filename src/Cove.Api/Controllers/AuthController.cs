using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Data.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private const string AccessCookieName = "cove_access_token";
    private readonly ITokenService _tokens;
    private readonly IUserService _users;
    private readonly IAuditService _audit;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly CoveConfiguration _config;

    public AuthController(ITokenService tokens, IUserService users, IAuditService audit, ICurrentPrincipalAccessor principalAccessor, CoveConfiguration config)
    {
        _tokens = tokens;
        _users = users;
        _audit = audit;
        _principalAccessor = principalAccessor;
        _config = config;
    }

    [HttpGet("bootstrap-status")]
    [AllowAnonymous]
    public async Task<IActionResult> BootstrapStatus(CancellationToken ct)
    {
        return Ok(new
        {
            ownerExists = await _users.OwnerExistsAsync(ct),
            authEnabled = _config.Auth.Enabled,
            hasSetupToken = await _users.HasSetupTokenAsync(ct),
        });
    }

    [HttpPost("bootstrap-owner")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth-strict")]
    public async Task<IActionResult> BootstrapOwner([FromBody] BootstrapOwnerRequest request, CancellationToken ct)
    {
        var ip = GetRequestIp();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();

        // Owner bootstrap is allowed from any address (including behind a reverse proxy) until an owner
        // exists, so first-run setup can be completed remotely without a token. The failsafe lockdown
        // only engages once an owner exists, and BootstrapOwnerAsync itself refuses once one does, so
        // this endpoint is only usable during the pre-owner setup window. This intentionally trades the
        // old "local-only owner claim" protection for usability in reverse-proxied deployments.

        // When the deployment is in token-first setup mode (an unconsumed setup token was explicitly
        // issued, e.g. via an admin invite), the owner account MUST be created by redeeming that token
        // via /auth/setup-token-redeem instead of this password-only path.
        if (await _users.HasSetupTokenAsync(ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "SETUP_TOKEN_REQUIRED", message = "A setup token is required to create the owner account." });

        try
        {
            var owner = await _users.BootstrapOwnerAsync(request.Username, request.Password, CovePrincipal.Anonymous(ip, ua), ct);
            var pair = await _tokens.IssueForUserAsync(owner.Id, ip, ua, ct);
            WriteAccessCookie(pair.AccessToken, pair.AccessExpires);
            return Ok(ToLoginResponse(pair));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { code = "OWNER_EXISTS", message = ex.Message });
        }
    }

    [HttpPost("setup-token-redeem")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth-strict")]
    public async Task<IActionResult> RedeemSetupToken([FromBody] SetupTokenRedeemRequest request, CancellationToken ct)
    {
        var ip = GetRequestIp();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();

        try
        {
            var user = await _users.RedeemSetupTokenAsync(request.Token, request.Password, request.Username, CovePrincipal.Anonymous(ip, ua), ct);
            var pair = await _tokens.IssueForUserAsync(user.Id, ip, ua, ct);
            WriteAccessCookie(pair.AccessToken, pair.AccessExpires);
            return Ok(ToLoginResponse(pair));
        }
        catch (InviteTokenException ex)
        {
            return StatusCode(StatusCodes.Status410Gone, new { code = "TOKEN_EXPIRED", message = ex.Message });
        }
    }

    [HttpPost("invite-redeem")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth-strict")]
    public async Task<IActionResult> RedeemInvite([FromBody] InviteRedeemRequest request, CancellationToken ct)
    {
        var ip = GetRequestIp();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();

        try
        {
            var user = await _users.RedeemInviteAsync(request.Token, request.Password, request.Username, CovePrincipal.Anonymous(ip, ua), ct);
            var pair = await _tokens.IssueForUserAsync(user.Id, ip, ua, ct);
            WriteAccessCookie(pair.AccessToken, pair.AccessExpires);
            return Ok(ToLoginResponse(pair));
        }
        catch (InviteTokenException ex)
        {
            return StatusCode(StatusCodes.Status410Gone, new { code = "TOKEN_EXPIRED", message = ex.Message });
        }
    }

    [HttpGet("invite-info")]
    [AllowAnonymous]
    public async Task<IActionResult> InviteInfo([FromQuery] string token, CancellationToken ct)
    {
        var info = await _users.GetInviteInfoAsync(token, ct);
        return info is null
            ? StatusCode(StatusCodes.Status410Gone, new { code = "TOKEN_EXPIRED", message = "Invite token is invalid or expired." })
            : Ok(info);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth-strict")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ip = GetRequestIp();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();

        IActionResult invalid = Unauthorized(new { code = "INVALID_CREDENTIALS", message = "Invalid credentials." });
        IActionResult disabled = Unauthorized(new { code = "ACCOUNT_DISABLED", message = "This account is disabled." });

        var user = await _users.FindByUsernameAsync(request.Username, ct);
        if (user is null)
        {
            PasswordHasher.VerifyDummy(request.Password);
            await _audit.LogAsync(AuditActions.LoginFail, AuditOutcomes.Fail,
                CovePrincipal.Anonymous(ip, ua), "user", request.Username, new { reason = "no_user" }, ct);
            return invalid;
        }
        if (!user.IsActive)
        {
            await _audit.LogAsync(AuditActions.LoginFail, AuditOutcomes.Fail,
                CovePrincipal.Anonymous(ip, ua), "user", user.Id.ToString(), new { reason = "inactive" }, ct);
            return disabled;
        }
        if (user.IsLocked)
        {
            await _audit.LogAsync(AuditActions.LoginFail, AuditOutcomes.Fail,
                CovePrincipal.Anonymous(ip, ua), "user", user.Id.ToString(), new { reason = "locked" }, ct);
            return invalid;
        }
        var ok = await _users.VerifyPasswordAsync(user.Id, request.Password, ct);
        if (!ok)
        {
            await _users.RecordLoginFailureAsync(user.Id, ct);
            await _audit.LogAsync(AuditActions.LoginFail, AuditOutcomes.Fail,
                CovePrincipal.Anonymous(ip, ua), "user", user.Id.ToString(), new { reason = "bad_password" }, ct);
            return invalid;
        }

        await _users.RecordLoginSuccessAsync(user.Id, ip, ct);
        var pair = await _tokens.IssueForUserAsync(user.Id, ip, ua, ct);
        WriteAccessCookie(pair.AccessToken, pair.AccessExpires);
        await _audit.LogAsync(AuditActions.LoginSuccess, AuditOutcomes.Success,
            CovePrincipal.Anonymous(ip, ua), "user", user.Id.ToString(), null, ct);

        return Ok(new
        {
            token = pair.AccessToken,
            refreshToken = pair.RefreshToken,
            accessExpires = pair.AccessExpires,
            refreshExpires = pair.RefreshExpires,
            user = pair.User,
            username = pair.User.Username,
        });
    }

    [HttpPost("refresh")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth-strict")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return Unauthorized(new { code = "INVALID_REFRESH" });
        var ip = GetRequestIp();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        try
        {
            var pair = await _tokens.RefreshAsync(request.RefreshToken, ip, ua, ct);
            WriteAccessCookie(pair.AccessToken, pair.AccessExpires);
            await _audit.LogAsync(AuditActions.TokenRefresh, AuditOutcomes.Success,
                CovePrincipal.Anonymous(ip, ua), "user", pair.User.Id.ToString(), null, ct);
            return Ok(new
            {
                token = pair.AccessToken,
                refreshToken = pair.RefreshToken,
                accessExpires = pair.AccessExpires,
                refreshExpires = pair.RefreshExpires,
                user = pair.User,
            });
        }
        catch (RefreshTokenConflictException ex)
        {
            await _audit.LogAsync(AuditActions.TokenRefreshConflict, AuditOutcomes.Deny,
                CovePrincipal.Anonymous(ip, ua), null, null, new { ex.Message }, ct);
            return Conflict(new { code = "REFRESH_TOKEN_ROTATED", message = ex.Message });
        }
        catch (UnauthorizedException ex)
        {
            await _audit.LogAsync(AuditActions.TokenRefreshReuse, AuditOutcomes.Deny,
                CovePrincipal.Anonymous(ip, ua), null, null, new { ex.Message }, ct);
            return Unauthorized(new { code = "INVALID_REFRESH", message = ex.Message });
        }
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request?.RefreshToken))
            await _tokens.RevokeChainAsync(request.RefreshToken, ct);
        ClearAccessCookie();
        var p = _principalAccessor.Current;
        await _audit.LogAsync(AuditActions.Logout, AuditOutcomes.Success, p, null, null, null, ct);
        return Ok(new { message = "Logged out" });
    }

    [HttpPost("revoke-sessions")]
    [AllowWithoutPermission]
    public async Task<IActionResult> RevokeSessions(CancellationToken ct)
    {
        var p = _principalAccessor.Current;
        if (p?.UserId is not int userId)
            return Unauthorized(new { code = "UNAUTHORIZED" });

        await _tokens.RevokeAllForUserAsync(userId, ct);
        ClearAccessCookie();
        await _audit.LogAsync(AuditActions.TokenRevoke, AuditOutcomes.Success, p, "user", userId.ToString(), new { allSessions = true }, ct);
        return Ok(new { message = "All sessions revoked." });
    }

    [HttpGet("me")]
    [AllowWithoutPermission]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var p = _principalAccessor.Current;
        if (p is null || p.Kind == PrincipalKind.Anonymous)
            return Unauthorized(new { code = "UNAUTHORIZED" });

        UserUiPreferencesDto? uiPreferences = null;
        var userId = p.UserId?.ToString(CultureInfo.InvariantCulture)
            ?? p.TokenId?.ToString("N", CultureInfo.InvariantCulture)
            ?? p.Username;
        var username = p.Kind == PrincipalKind.ShareLink ? "Share link" : p.Username;

        if (p.UserId is int currentUserId)
        {
            var user = await _users.GetAsync(currentUserId, ct);
            if (user is not null)
            {
                userId = user.Id.ToString(CultureInfo.InvariantCulture);
                username = user.Username;
                uiPreferences = user.UiPreferences;
            }
        }

        return Ok(new
        {
            user = new
            {
                id = userId,
                username,
                roles = p.Roles.ToArray(),
                kind = ToClientUserKind(p.Kind),
                uiPreferences,
            },
            permissions = p.Permissions.ToArray(),
            readGrantedEntityKinds = p.ReadGrantedEntityKinds.ToArray(),
        });
    }

    [HttpPut("me/ui-preferences")]
    [AllowWithoutPermission]
    public async Task<IActionResult> UpdateUiPreferences([FromBody] UserUiPreferencesDto preferences, CancellationToken ct)
    {
        var p = _principalAccessor.Current;
        if (p?.UserId is not int userId)
            return Unauthorized(new { code = "UNAUTHORIZED" });

        var updated = await _users.UpdateUiPreferencesAsync(userId, preferences, p, ct);
        return Ok(updated);
    }

    [HttpPost("change-password")]
    [AllowWithoutPermission]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        var p = _principalAccessor.Current;
        if (p?.UserId is not int userId)
            return Unauthorized(new { code = "UNAUTHORIZED" });
        var ok = await _users.VerifyPasswordAsync(userId, req.CurrentPassword, ct);
        if (!ok) return BadRequest(new { code = "INVALID_PASSWORD", message = "Current password is incorrect." });
        await _users.ChangePasswordAsync(userId, req.NewPassword, p, ct);
        await _tokens.RevokeAllForUserAsync(userId, ct);
        ClearAccessCookie();
        return Ok(new { message = "Password changed; please log in again." });
    }

    private void WriteAccessCookie(string token, DateTime expiresUtc)
    {
        Response.Cookies.Append(AccessCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = Request.IsHttps,
            Path = "/",
            Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc)),
        });
    }

    private void ClearAccessCookie()
    {
        Response.Cookies.Delete(AccessCookieName, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = Request.IsHttps,
            Path = "/",
        });
    }

    private string? GetRequestIp()
        => Cove.Api.Middleware.AuthDisabledRequestGuard.GetEffectiveRemoteAddress(HttpContext, _config.Auth)?.ToString();

    private static object ToLoginResponse(TokenPair pair) => new
    {
        token = pair.AccessToken,
        refreshToken = pair.RefreshToken,
        accessExpires = pair.AccessExpires,
        refreshExpires = pair.RefreshExpires,
        user = pair.User,
        username = pair.User.Username,
    };

    private static string ToClientUserKind(PrincipalKind kind) => kind switch
    {
        PrincipalKind.ShareLink => "shareLink",
        PrincipalKind.ApiToken => "apiToken",
        PrincipalKind.System => "system",
        PrincipalKind.User => "user",
        _ => "anonymous",
    };
}

public record RefreshRequest(string RefreshToken);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record BootstrapOwnerRequest(string Username, string Password);
public record SetupTokenRedeemRequest(string Token, string Password, string? Username = null);
public record InviteRedeemRequest(string Token, string Password, string? Username = null);
