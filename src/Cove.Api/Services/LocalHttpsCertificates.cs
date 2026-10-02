using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cove.Api.Services;

/// <summary>
/// A private certificate authority and a server certificate it signs, kept in <c>&lt;data&gt;/certs</c>, so Cove
/// can serve HTTPS on a LAN with no domain name. Browsers only expose APIs such as WebXR to secure contexts,
/// and a headset reaching Cove by LAN IP over plain HTTP is not one.
/// <para>
/// The CA is created once and kept, so a device that trusts it keeps trusting Cove. The server certificate is
/// reissued whenever it nears expiry or no longer names every address this machine answers on, which covers
/// DHCP moving the machine to a new IP.
/// </para>
/// </summary>
public sealed class LocalHttpsCertificates
{
    public const string CaFileName = "cove-local-ca.pfx";
    public const string ServerFileName = "cove-https.pfx";
    public const string CaDownloadName = "cove-local-ca.crt";

    // Chromium rejects server certificates valid for longer than 398 days, including privately trusted ones on some platforms.
    private static readonly TimeSpan ServerLifetime = TimeSpan.FromDays(397);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public required X509Certificate2 Authority { get; init; }
    public required X509Certificate2 Server { get; init; }
    public required IReadOnlyList<string> HostNames { get; init; }

    /// <summary>The CA certificate without its key, DER encoded, for installing on client devices.</summary>
    public byte[] ExportAuthorityCertificate() => Authority.Export(X509ContentType.Cert);

    public static LocalHttpsCertificates LoadOrCreate(string directory, IEnumerable<string> extraHostNames, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        var hostNames = CollectHostNames(extraHostNames);

        var caPath = Path.Combine(directory, CaFileName);
        var authority = TryLoad(caPath);
        if (authority == null || authority.NotAfter <= now.UtcDateTime + RenewBefore)
        {
            authority = CreateAuthority(now);
            File.WriteAllBytes(caPath, authority.Export(X509ContentType.Pkcs12));
            File.WriteAllBytes(Path.Combine(directory, CaDownloadName), authority.Export(X509ContentType.Cert));
            File.Delete(Path.Combine(directory, ServerFileName));
        }

        var serverPath = Path.Combine(directory, ServerFileName);
        var server = TryLoad(serverPath);
        if (server == null
            || server.NotAfter <= now.UtcDateTime + RenewBefore
            || server.Issuer != authority.Subject
            || !CoversAll(server, hostNames))
        {
            server = CreateServer(authority, hostNames, now);
            File.WriteAllBytes(serverPath, server.Export(X509ContentType.Pkcs12));
            // Reload so the key lives in a form SslStream accepts on every platform.
            server = X509CertificateLoader.LoadPkcs12FromFile(serverPath, null);
        }

        return new LocalHttpsCertificates { Authority = authority, Server = server, HostNames = hostNames };
    }

    internal static IReadOnlyList<string> CollectHostNames(IEnumerable<string> extraHostNames)
    {
        var names = new List<string> { "localhost", "127.0.0.1", "::1" };
        var machine = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(machine))
        {
            names.Add(machine);
            names.Add(machine + ".local");
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                {
                    // IPv4 only: IPv6 privacy addresses rotate daily and would reissue the certificate on every start.
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                        names.Add(address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Sandboxed hosts may refuse interface enumeration; localhost and configured names still work.
        }

        names.AddRange(extraHostNames.Select(name => name.Trim()).Where(name => name.Length > 0));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static X509Certificate2? TryLoad(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static X509Certificate2 CreateAuthority(DateTimeOffset now)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN=Cove Local CA ({Environment.MachineName}), O=Cove", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }

    private static X509Certificate2 CreateServer(X509Certificate2 authority, IReadOnlyList<string> hostNames, DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Cove", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in hostNames)
        {
            if (IPAddress.TryParse(name, out var ip))
                san.AddIpAddress(ip);
            else
                san.AddDnsName(name);
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notAfter = now + ServerLifetime;
        using var signed = request.Create(authority, now.AddDays(-1), notAfter < authority.NotAfter ? notAfter : authority.NotAfter, serial);
        return signed.CopyWithPrivateKey(key);
    }

    private static bool CoversAll(X509Certificate2 certificate, IReadOnlyList<string> hostNames)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san == null)
            return false;
        var covered = new HashSet<string>(san.EnumerateDnsNames(), StringComparer.OrdinalIgnoreCase);
        foreach (var ip in san.EnumerateIPAddresses())
            covered.Add(ip.ToString());
        return hostNames.All(covered.Contains);
    }
}

/// <summary>Whether the optional HTTPS listener is running, and the certificates it uses.</summary>
public sealed record LocalHttpsStatus(LocalHttpsCertificates? Certificates, int? Port);
