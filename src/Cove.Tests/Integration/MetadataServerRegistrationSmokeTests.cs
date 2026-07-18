using Cove.Api.Services;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests.Integration;

/// <summary>
/// Guards the extension-facing DI contract for the metadata-server client. Extensions can bind only
/// Cove.Core types, so they resolve <see cref="IMetadataServerService"/> rather than the concrete
/// <c>Cove.Api</c> client. Nothing at compile time ties the registration to the interface, so dropping it
/// would silently return null to every extension that resolves it — this asserts it stays wired.
/// </summary>
public sealed class MetadataServerRegistrationSmokeTests
{
    [Fact]
    public void IMetadataServerService_ResolvesToTheHostMetadataServerClient()
    {
        using var factory = new CoveWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        var service = scope.ServiceProvider.GetService<IMetadataServerService>();

        Assert.IsType<MetadataServerService>(service);
    }
}
