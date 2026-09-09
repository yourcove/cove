using System.Reflection;
using Cove.ApiTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Cove.ApiTests.Tests.Contracts;

public sealed class EndpointCoverageTests(ITestOutputHelper output)
{
    [Fact]
    public void GivenCurrentControllers_WhenEndpointInventoryIsBuilt_ThenEveryAttributedActionIsUniquelyIdentified()
    {
        // Arrange
        var attributedActions = typeof(Program).Assembly
            .GetTypes()
            .Where(IsApiController)
            .SelectMany(controller => controller.GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method
                .GetCustomAttributes(inherit: true)
                .OfType<IActionHttpMethodProvider>()
                .Any())
            .Distinct()
            .OrderBy(FormatAction, StringComparer.Ordinal)
            .ToArray();
        var inventoriedActions = ApiEndpointInventory.All
            .Select(definition => definition.ActionMethod)
            .Distinct()
            .OrderBy(FormatAction, StringComparer.Ordinal)
            .ToArray();
        var duplicateEndpointIds = ApiEndpointInventory.All
            .GroupBy(definition => definition.Endpoint)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(definition => FormatAction(definition.ActionMethod)))}")
            .ToArray();

        // Act & Assert
        inventoriedActions.Should().BeEquivalentTo(attributedActions);
        duplicateEndpointIds.Should().BeEmpty(
            "each endpoint must have one unambiguous action, but found:{0}{1}",
            Environment.NewLine,
            string.Join(Environment.NewLine, duplicateEndpointIds));
    }

    [Fact]
    public void GivenCurrentCoverage_WhenEndpointDispositionsAreInspected_ThenProgressDoesNotRegress()
    {
        // Arrange
        var inventory = ApiEndpointInventory.All
            .Select(definition => definition.Endpoint)
            .ToHashSet();
        var mapped = GetMappedEndpoints();
        var temporarilyUnmapped = EndpointCoverageProgress.TemporarilyUnmapped;
        var exceptions = EndpointCoverageProgress.Exceptions;
        var exceptionEndpoints = exceptions
            .Select(exception => exception.Endpoint)
            .ToHashSet();

        output.WriteLine(
            "Endpoint coverage: {0} mapped, {1} temporarily unmapped, {2} explicit exceptions, {3} total.",
            mapped.Count,
            temporarilyUnmapped.Count,
            exceptionEndpoints.Count,
            inventory.Count);

        // Act
        var problems = new List<string>();
        AddEndpointProblem(
            problems,
            "Unclassified endpoints (add an executable exact mapping, temporary backlog entry, or reviewed exception)",
            inventory.Except(mapped).Except(temporarilyUnmapped).Except(exceptionEndpoints));
        AddEndpointProblem(
            problems,
            "Mappings that do not match a current endpoint",
            mapped.Except(inventory));
        AddEndpointProblem(
            problems,
            "Temporary backlog entries that do not match a current endpoint",
            temporarilyUnmapped.Except(inventory));
        AddEndpointProblem(
            problems,
            "Exception entries that do not match a current endpoint",
            exceptionEndpoints.Except(inventory));
        AddEndpointProblem(
            problems,
            "Endpoints with both mapped and temporary dispositions",
            mapped.Intersect(temporarilyUnmapped));
        AddEndpointProblem(
            problems,
            "Endpoints with both mapped and exception dispositions",
            mapped.Intersect(exceptionEndpoints));
        AddEndpointProblem(
            problems,
            "Endpoints with both temporary and exception dispositions",
            temporarilyUnmapped.Intersect(exceptionEndpoints));

        var duplicateExceptions = exceptions
            .GroupBy(exception => exception.Endpoint)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        AddEndpointProblem(problems, "Endpoints with duplicate exception entries", duplicateExceptions);
        var reasonlessExceptions = exceptions
            .Where(exception => string.IsNullOrWhiteSpace(exception.Reason))
            .Select(exception => exception.Endpoint)
            .ToArray();
        AddEndpointProblem(problems, "Exception entries without a technical reason", reasonlessExceptions);

        if (mapped.Count != EndpointCoverageProgress.ExpectedMappedEndpoints)
        {
            problems.Add(
                $"Mapped endpoint count is {mapped.Count}; update the checked-in expectation of {EndpointCoverageProgress.ExpectedMappedEndpoints} with this slice.");
        }
        if (temporarilyUnmapped.Count != EndpointCoverageProgress.ExpectedTemporarilyUnmappedEndpoints)
        {
            problems.Add(
                $"Temporary backlog count is {temporarilyUnmapped.Count}; update the checked-in expectation of {EndpointCoverageProgress.ExpectedTemporarilyUnmappedEndpoints} with this slice.");
        }

        // Assert
        Assert.True(
            problems.Count == 0,
            $"Endpoint coverage disposition failed:{Environment.NewLine}{string.Join(Environment.NewLine + Environment.NewLine, problems)}");

        ReadEndpointCatalog.All
            .Select(definition => definition.Endpoint)
            .Should().OnlyHaveUniqueItems();
        ReadEndpointCatalog.All
            .Select(definition => definition.Endpoint)
            .Should().BeEquivalentTo(Enum.GetValues<ReadEndpoint>());
    }

    private static HashSet<ApiEndpointId> GetMappedEndpoints()
    {
        var runnableTests = typeof(EndpointCoverageTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(IsRunnableTest)
            .ToArray();
        var attributedMappings = runnableTests
            .SelectMany(method => method.GetCustomAttributes<CoversEndpointAttribute>())
            .Select(attribute => attribute.Endpoint);
        var readCatalogMappings = runnableTests
            .Any(method => method.GetCustomAttribute<CoversReadEndpointCatalogAttribute>() is not null)
                ? ReadEndpointCatalog.All.Select(definition => definition.CoveredEndpoint)
                : [];

        return readCatalogMappings
            .Concat(attributedMappings)
            .ToHashSet();
    }

    private static bool IsApiController(Type type)
        => type is { IsAbstract: false, IsPublic: true }
            && typeof(ControllerBase).IsAssignableFrom(type)
            && type.Assembly == typeof(Program).Assembly;

    private static bool IsRunnableTest(MethodInfo method)
        => method.GetCustomAttributes<FactAttribute>()
            .Any(attribute => string.IsNullOrWhiteSpace(attribute.Skip));

    private static string FormatAction(MethodInfo method)
        => $"{method.DeclaringType?.FullName}.{method.Name}";

    private static void AddEndpointProblem(
        ICollection<string> problems,
        string heading,
        IEnumerable<ApiEndpointId> endpoints)
    {
        var endpointList = endpoints
            .OrderBy(endpoint => endpoint.ToString(), StringComparer.Ordinal)
            .Select(endpoint => $"  {endpoint}")
            .ToArray();
        if (endpointList.Length > 0)
            problems.Add($"{heading}:{Environment.NewLine}{string.Join(Environment.NewLine, endpointList)}");
    }
}
