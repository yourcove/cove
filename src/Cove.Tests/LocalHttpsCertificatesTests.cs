using System.Security.Cryptography.X509Certificates;
using Cove.Api.Services;

namespace Cove.Tests;

/// <summary>
/// The HTTPS listener exists so a headset on the LAN gets a secure context. It only works if the server
/// certificate chains to the CA the headset was told to trust and names the address the headset uses.
/// </summary>
public sealed class LocalHttpsCertificatesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cove-https-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ServerCertificateChainsToTheLocalAuthorityAndNamesConfiguredHosts()
    {
        var certificates = LocalHttpsCertificates.LoadOrCreate(_directory, ["cove.lan", "192.168.1.50"], Now);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificates.Authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = Now.UtcDateTime.AddDays(1);
        Assert.True(chain.Build(certificates.Server), string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));

        var san = certificates.Server.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("localhost", san.EnumerateDnsNames());
        Assert.Contains("cove.lan", san.EnumerateDnsNames());
        Assert.Contains(san.EnumerateIPAddresses(), ip => ip.ToString() == "192.168.1.50");
        Assert.True(certificates.Server.HasPrivateKey);
        Assert.True(certificates.Server.NotAfter - certificates.Server.NotBefore <= TimeSpan.FromDays(398));
    }

    [Fact]
    public void ReusesCertificatesWhileTheyStillFit()
    {
        var first = LocalHttpsCertificates.LoadOrCreate(_directory, ["cove.lan"], Now);
        var second = LocalHttpsCertificates.LoadOrCreate(_directory, ["cove.lan"], Now.AddDays(10));

        Assert.Equal(first.Authority.Thumbprint, second.Authority.Thumbprint);
        Assert.Equal(first.Server.Thumbprint, second.Server.Thumbprint);
    }

    [Fact]
    public void ReissuesTheServerCertificateButKeepsTheAuthorityWhenAnAddressIsNew()
    {
        var first = LocalHttpsCertificates.LoadOrCreate(_directory, [], Now);
        var second = LocalHttpsCertificates.LoadOrCreate(_directory, ["10.0.0.9"], Now);

        Assert.Equal(first.Authority.Thumbprint, second.Authority.Thumbprint);
        Assert.NotEqual(first.Server.Thumbprint, second.Server.Thumbprint);
    }

    [Fact]
    public void ReissuesTheServerCertificateBeforeItExpires()
    {
        var first = LocalHttpsCertificates.LoadOrCreate(_directory, [], Now);
        var renewed = LocalHttpsCertificates.LoadOrCreate(_directory, [], Now.AddDays(380));

        Assert.Equal(first.Authority.Thumbprint, renewed.Authority.Thumbprint);
        Assert.NotEqual(first.Server.Thumbprint, renewed.Server.Thumbprint);
    }

    [Fact]
    public void ExportsOnlyThePublicAuthorityCertificate()
    {
        var certificates = LocalHttpsCertificates.LoadOrCreate(_directory, [], Now);

        var exported = X509CertificateLoader.LoadCertificate(certificates.ExportAuthorityCertificate());

        Assert.Equal(certificates.Authority.Thumbprint, exported.Thumbprint);
        Assert.False(exported.HasPrivateKey);
    }
}
