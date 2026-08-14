using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Contracts;

[Collection(ApiTestLane1Collection.Name)]
public sealed class EndpointReadApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    public static TheoryData<ReadEndpoint> Endpoints
    {
        get
        {
            var endpoints = new TheoryData<ReadEndpoint>();
            foreach (var definition in ReadEndpointCatalog.All)
                endpoints.Add(definition.Endpoint);
            return endpoints;
        }
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task GivenFreshLibrary_WhenEndpointIsRead_ThenResponseHasExpectedShape(
        ReadEndpoint endpoint)
    {
        var expectedShape = ReadEndpointCatalog.Get(endpoint).ExpectedShape;

        var response = await AsUser().ReadEndpointAsync(endpoint);

        response.ValueKind.Should().Be(
            expectedShape == JsonResponseShape.Array
                ? JsonValueKind.Array
                : JsonValueKind.Object);
        if (expectedShape != JsonResponseShape.Paginated)
            return;

        response.TryGetProperty("items", out var items).Should().BeTrue();
        items.ValueKind.Should().Be(JsonValueKind.Array);
        response.TryGetProperty("totalCount", out var totalCount).Should().BeTrue();
        totalCount.ValueKind.Should().Be(JsonValueKind.Number);
    }
}
