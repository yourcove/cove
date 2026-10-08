using System.Net;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Entities.Tags;

public sealed class TagAliasApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/tags/{id:int}/aliases")]
    public async Task GivenTagWithAlias_WhenAnotherAliasIsAdded_ThenBothAliasesAreKept()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var tag = await owner.CreateTagAsync($"Big Breasts {suffix}");
        await owner.AddTagAliasAsync(tag.Id, $"Busty {suffix}", TestContext.Current.CancellationToken);

        var updated = await owner.AddTagAliasAsync(tag.Id, $" Big Tits {suffix} ", TestContext.Current.CancellationToken);
        var read = await owner.GetTagByIdAsync(tag.Id, TestContext.Current.CancellationToken);

        updated.Aliases.Should().BeEquivalentTo($"Busty {suffix}", $"Big Tits {suffix}");
        read.Aliases.Should().BeEquivalentTo($"Busty {suffix}", $"Big Tits {suffix}");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/tags/{id:int}/aliases")]
    public async Task GivenAliasOwnedByAnotherTag_WhenAddedToTag_ThenConflictIsReturned()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var tag = await owner.CreateTagAsync($"Big Breasts {suffix}");
        await owner.CreateTagAsync($"Big Tits {suffix}");

        var status = await owner.TryAddTagAliasAsync(tag.Id, $"big tits {suffix}", TestContext.Current.CancellationToken);
        var read = await owner.GetTagByIdAsync(tag.Id, TestContext.Current.CancellationToken);

        status.Should().Be(HttpStatusCode.Conflict);
        read.Aliases.Should().BeEmpty();
    }

    [Fact]
    [CoversEndpoint("POST", "/api/tags/{id:int}/aliases")]
    public async Task GivenMissingTag_WhenAliasIsAdded_ThenNotFoundIsReturned()
    {
        var status = await AsUser().TryAddTagAliasAsync(int.MaxValue, "Orphan alias", TestContext.Current.CancellationToken);

        status.Should().Be(HttpStatusCode.NotFound);
    }
}
