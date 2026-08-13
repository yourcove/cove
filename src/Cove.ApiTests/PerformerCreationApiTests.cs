using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane2Collection.Name)]
public sealed class PerformerCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenNamedMember_WhenPerformersAreRead_ThenMemberUsesItsOwnClient()
    {
        var member = AsUser(ApiTestUsers.Member);

        var performers = await member.GetPerformersAsync();

        member.Username.Should().Be(ApiTestUsers.Member);
        performers.Should().BeEmpty();
    }

    [Fact]
    public async Task GivenPerformerDetails_WhenPerformerIsCreated_ThenPerformerCanBeRetrieved()
    {
        (await AsUser().GetPerformersAsync()).Should().BeEmpty();

        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName("Lane Two Performer")
                .Build());

        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.Name.Should().Be(performer.Name);
    }
}
