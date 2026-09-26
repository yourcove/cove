using Cove.Api.Services;
using Cove.Core.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cove.Api.Controllers;

/// <summary>What the HTTPS listener serves, so clients that need a secure context can be sent to it.</summary>
public record HttpsStatusDto(bool Enabled, int? Port, IReadOnlyList<string> HostNames, string? CertificateAuthorityUrl);

[ApiController]
[Route("api/https")]
public class HttpsController : ControllerBase
{
    private readonly LocalHttpsStatus _status;

    public HttpsController(LocalHttpsStatus status) => _status = status;

    [HttpGet]
    [RequiresPermission(Permissions.StreamRead)]
    public ActionResult<HttpsStatusDto> Get()
        => _status.Certificates is { } certificates
            ? new HttpsStatusDto(true, _status.Port, certificates.HostNames, "/api/https/ca.crt")
            : new HttpsStatusDto(false, null, [], null);

    /// <summary>
    /// The local CA certificate (public part only). Anonymous, because a headset needs it installed
    /// before it can open the HTTPS origin to sign in, and it holds nothing secret.
    /// </summary>
    [HttpGet("ca.crt")]
    [AllowAnonymous]
    public IActionResult DownloadAuthority()
        => _status.Certificates is { } certificates
            ? File(certificates.ExportAuthorityCertificate(), "application/x-x509-ca-cert", LocalHttpsCertificates.CaDownloadName)
            : NotFound();
}
