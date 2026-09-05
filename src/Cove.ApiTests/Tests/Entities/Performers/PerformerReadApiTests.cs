using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Performers;

public sealed class PerformerReadApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/performers/countries")]
    public async Task GivenCanonicalAndCustomCountries_WhenCountriesAreRead_ThenOptionsDescribeStoredValues()
    {
        await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithCountry("Monaco")
            .Build(), TestContext.Current.CancellationToken);
        await AsUser().CreatePerformerAsync(new PerformerBuilder()
            .WithCountry("Atlantis")
            .Build(), TestContext.Current.CancellationToken);

        var countries = await AsUser().GetPerformerCountriesAsync(TestContext.Current.CancellationToken);

        countries.Should().ContainSingle(country => country.Value == "MC")
            .Which.Should().BeEquivalentTo(new PerformerCountryOptionDto("MC", "MC", "Monaco", 1, false));
        countries.Should().ContainSingle(country => country.Value == "Atlantis")
            .Which.Should().BeEquivalentTo(new PerformerCountryOptionDto("Atlantis", null, "Atlantis", 1, true));
    }

    [Fact]
    public async Task GivenPerformer_WhenMemberReadsPerformers_ThenPerformerIsReturned()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        var performers = await AsUser(ApiTestUsers.Eva).GetPerformersAsync(TestContext.Current.CancellationToken);

        // Assert
        performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
    }

    [Fact]
    public async Task GivenMissingPerformer_WhenRead_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var action = () => AsUser().GetPerformerByIdAsync(missingId);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
