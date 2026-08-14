using System.Reflection;
using Cove.ApiTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Cove.ApiTests.Tests.Contracts;

public sealed class EndpointCoverageTests
{
    [Fact]
    public void GivenCurrentControllers_WhenEndpointCoverageIsInspected_ThenEveryControllerHasHappyPathTest()
    {
        var currentControllers = typeof(Program).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsPublic: true }
                && typeof(ControllerBase).IsAssignableFrom(type)
                && type.Namespace == "Cove.Api.Controllers")
            .Select(type => type.FullName)
            .Order()
            .ToArray();
        var focusedControllers = typeof(EndpointCoverageTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.GetCustomAttributes<FactAttribute>()
                .Any(attribute => string.IsNullOrWhiteSpace(attribute.Skip)))
            .SelectMany(method => method.GetCustomAttributes<CoversEndpointsAttribute>())
            .SelectMany(attribute => attribute.ControllerTypes)
            .ToArray();
        var coveredControllers = ReadEndpointCatalog.All
            .Select(definition => definition.ControllerType)
            .Concat(focusedControllers)
            .Select(type => type.FullName)
            .Distinct()
            .Order()
            .ToArray();

        ReadEndpointCatalog.All
            .Select(definition => definition.Endpoint)
            .Should().OnlyHaveUniqueItems();
        ReadEndpointCatalog.All
            .Select(definition => definition.Endpoint)
            .Should().BeEquivalentTo(Enum.GetValues<ReadEndpoint>());
        coveredControllers.Should().BeEquivalentTo(currentControllers);
    }
}
