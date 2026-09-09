using Cove.Core.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ApiTokensController : ControllerBase
{
    private readonly ITokenService _tokens;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly IAuditService _audit;

    public ApiTokensController(ITokenService tokens, ICurrentPrincipalAccessor principalAccessor, IAuditService audit)
    {
        _tokens = tokens;
        _principalAccessor = principalAccessor;
        _audit = audit;
    }

    public record CreateApiTokenRequest(string Name, string[]? Scope = null, DateTime? ExpiresAt = null);

    [HttpGet]
    [RequiresPermission(Permissions.ApiTokensWrite)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var p = _principalAccessor.Current;
        if (p?.UserId is not int uid) return Unauthorized();
        return Ok(await _tokens.ListApiTokensAsync(uid, ct));
    }

    [HttpPost]
    [RequiresPermission(Permissions.ApiTokensWrite)]
    public async Task<IActionResult> Create([FromBody] CreateApiTokenRequest req, CancellationToken ct)
    {
        var p = _principalAccessor.Current;
        if (p?.UserId is not int uid) return Unauthorized();
        var issued = await _tokens.CreateApiTokenAsync(uid, req.Name, req.Scope, req.ExpiresAt, p, ct);
        await _audit.LogAsync(AuditActions.ApiTokenCreate, AuditOutcomes.Success, p,
            "api_token", issued.Id.ToString(), new { req.Name }, ct);
        return Ok(issued);
    }

    [HttpDelete("{id:guid}")]
    [RequiresPermission(Permissions.ApiTokensWrite)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var p = _principalAccessor.Current;
        if (p?.UserId is not int) return Unauthorized();
        await _tokens.RevokeApiTokenAsync(id, p, ct);
        return NoContent();
    }
}
