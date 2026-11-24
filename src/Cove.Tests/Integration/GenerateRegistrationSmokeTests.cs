using Cove.Api.Services;
using Cove.Core.Interfaces;
using Cove.Tests.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests.Integration;

/// <summary>
/// Guards the extension-facing DI contract for generate. Extensions can bind only Cove.Core types, so they
/// resolve <see cref="IGenerateService"/> rather than the concrete <c>Cove.Api</c> job service. The
/// forwarding registration is what ties the two together and nothing at compile time requires it, so
/// dropping it would silently return null to every extension that resolves the interface.
/// </summary>
[RequiresPostgres]
public sealed class GenerateRegistrationSmokeTests
{
    [Fact]
    public void IGenerateService_ResolvesToTheHostGenerateJobService()
    {
        using var factory = new CoveWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        var service = scope.ServiceProvider.GetService<IGenerateService>();

        Assert.IsType<GenerateJobService>(service);
    }

    /// <summary>
    /// The forward must hand back the host's own singleton. A second instance would run generate with its
    /// own collaborators while the rest of the host tracked the first, and the resolve above would still
    /// pass.
    /// </summary>
    [Fact]
    public void IGenerateService_IsTheSameInstanceAsTheConcreteRegistration()
    {
        using var factory = new CoveWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        var viaInterface = scope.ServiceProvider.GetRequiredService<IGenerateService>();
        var viaConcrete = scope.ServiceProvider.GetRequiredService<GenerateJobService>();

        Assert.Same(viaConcrete, viaInterface);
    }
}
