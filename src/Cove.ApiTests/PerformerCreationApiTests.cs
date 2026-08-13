using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane2Collection.Name)]
public sealed class PerformerCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenPerformer_WhenMemberReadsPerformers_ThenPerformerIsReturned()
    {
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());
        var member = AsUser(ApiTestUsers.Member);

        var performers = await member.GetPerformersAsync();

        member.Username.Should().Be(ApiTestUsers.Member);
        performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
    }

    [Fact]
    public async Task GivenPerformerDetails_WhenPerformerIsCreated_ThenPerformerCanBeRetrieved()
    {
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.VelvetThunder.Name)
                .Build());

        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.Name.Should().Be(performer.Name);
    }
}
