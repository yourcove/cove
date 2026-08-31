using Cove.Core.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserService _users;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public UsersController(IUserService users, ICurrentPrincipalAccessor principalAccessor)
    {
        _users = users;
        _principalAccessor = principalAccessor;
    }

    [HttpGet]
    [RequiresPermission(Permissions.UsersRead)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _users.ListAsync(ct));

    [HttpGet("{id:int}")]
    [RequiresPermission(Permissions.UsersRead)]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var u = await _users.GetAsync(id, ct);
        return u is null ? NotFound() : Ok(u);
    }

    [HttpPost]
    [RequiresPermission(Permissions.UsersWrite)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req, CancellationToken ct) =>
        Ok(await _users.CreateAsync(req, _principalAccessor.Current, ct));

    [HttpPost("invite")]
    [RequiresPermission(Permissions.UsersInvite)]
    public async Task<IActionResult> CreateInvite([FromBody] CreateInviteRequest req, CancellationToken ct)
    {
        var baseUrl = ResolveInviteBaseUrl(Request);
        return Ok(await _users.CreatePendingInviteAsync(req, baseUrl, _principalAccessor.Current, ct));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.UsersWrite)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserRequest req, CancellationToken ct) =>
        Ok(await _users.UpdateAsync(id, req, _principalAccessor.Current, ct));

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.UsersDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _users.DeleteAsync(id, _principalAccessor.Current, ct);
        return NoContent();
    }

    public record SetRolesRequest(string[] Roles);

    [HttpPost("{id:int}/roles")]
    [RequiresPermission(Permissions.UsersWrite, Permissions.RolesWrite)]
    public async Task<IActionResult> SetRoles(int id, [FromBody] SetRolesRequest req, CancellationToken ct)
    {
        await _users.SetRolesAsync(id, req.Roles, _principalAccessor.Current, ct);
        return Ok(await _users.GetAsync(id, ct));
    }

    public record AdminPasswordRequest(string NewPassword);

    [HttpPost("{id:int}/password")]
    [RequiresPermission(Permissions.UsersWrite)]
    public async Task<IActionResult> AdminChangePassword(int id, [FromBody] AdminPasswordRequest req, CancellationToken ct)
    {
        await _users.ChangePasswordAsync(id, req.NewPassword, _principalAccessor.Current, ct);
        return Ok(new { message = "Password updated." });
    }

    [HttpPost("{id:int}/invite")]
    [RequiresPermission(Permissions.UsersInvite)]
    public async Task<IActionResult> Invite(int id, CancellationToken ct)
    {
        var baseUrl = ResolveInviteBaseUrl(Request);
        return Ok(await _users.CreateInviteAsync(id, baseUrl, _principalAccessor.Current, ct));
    }

    internal static string ResolveInviteBaseUrl(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            && (originUri.Scheme == Uri.UriSchemeHttp || originUri.Scheme == Uri.UriSchemeHttps)
            && originUri.AbsolutePath == "/"
            && string.IsNullOrEmpty(originUri.Query)
            && string.IsNullOrEmpty(originUri.Fragment)
            && string.IsNullOrEmpty(originUri.UserInfo))
        {
            return originUri.GetLeftPart(UriPartial.Authority);
        }

        return $"{request.Scheme}://{request.Host}";
    }

    [HttpPost("{id:int}/unlock")]
    [RequiresPermission(Permissions.UsersWrite)]
    public async Task<IActionResult> Unlock(int id, CancellationToken ct)
    {
        await _users.UnlockAsync(id, _principalAccessor.Current, ct);
        return Ok(new { message = "Unlocked." });
    }

    [HttpGet("{id:int}/external-links")]
    [RequiresPermission(Permissions.UsersRead)]
    public async Task<IActionResult> ExternalLinks(
        int id,
        [FromServices] IExternalIdentityService identities,
        CancellationToken ct)
    {
        if (await _users.GetAsync(id, ct) is null)
            return NotFound();
        return Ok(await identities.ListForUserAsync(id, ct));
    }

    [HttpDelete("{id:int}/external-links/{linkId:int}")]
    [RequiresPermission(Permissions.UsersWrite)]
    public async Task<IActionResult> RemoveExternalLink(
        int id,
        int linkId,
        [FromServices] IExternalIdentityService identities,
        CancellationToken ct)
    {
        try
        {
            await identities.RemoveLinkAsync(id, linkId, _principalAccessor.Current, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
