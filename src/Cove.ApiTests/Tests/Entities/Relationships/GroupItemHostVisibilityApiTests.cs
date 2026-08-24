using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Entities.Relationships;

public sealed class GroupItemHostVisibilityApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenAllPublicGroupItemHosts_WhenMemberReadsFreshPage_ThenEveryHostIsVisibleInOrder()
    {
        var eva = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var parent = await eva.CreateGroupAsync($"Host visibility parent {suffix}", TestContext.Current.CancellationToken);
        var childGroup = await eva.CreateGroupAsync($"Host visibility child {suffix}", TestContext.Current.CancellationToken);
        var controlGroup = await eva.CreateGroupAsync($"Host visibility control {suffix}", TestContext.Current.CancellationToken);
        var video = await eva.CreateVideoAsync($"Host visibility video {suffix}", TestContext.Current.CancellationToken);
        var audio = await eva.CreateAudioAsync($"Host visibility audio {suffix}", TestContext.Current.CancellationToken);
        var text = await eva.CreateTextAsync($"Host visibility text {suffix}", TestContext.Current.CancellationToken);
        var image = await eva.CreateImageAsync($"Host visibility image {suffix}", TestContext.Current.CancellationToken);
        var performer = await eva.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Host visibility performer {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var studio = await eva.CreateStudioAsync($"Host visibility studio {suffix}", TestContext.Current.CancellationToken);
        var tag = await eva.CreateTagAsync($"Host visibility tag {suffix}", TestContext.Current.CancellationToken);
        var gallery = await eva.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Host visibility gallery {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var face = await eva.CreateFaceAsync(new FaceCreateDto($"Host visibility face {suffix}", null, false, "api-test"), TestContext.Current.CancellationToken);
        var segment = await eva.CreateVideoSegmentAsync(video, $"Host visibility segment {suffix}", TestContext.Current.CancellationToken);

        parent.Kind.Should().Be(GroupKind.Static);
        var expected = new (GroupItemKind Kind, string HostType, int HostId)[]
        {
            (GroupItemKind.Video, "video", video.Id),
            (GroupItemKind.Audio, "audio", audio.Id),
            (GroupItemKind.Text, "text", text.Id),
            (GroupItemKind.Image, "image", image.Id),
            (GroupItemKind.Performer, "performer", performer.Id),
            (GroupItemKind.Group, "group", childGroup.Id),
            (GroupItemKind.Studio, "studio", studio.Id),
            (GroupItemKind.Tag, "tag", tag.Id),
            (GroupItemKind.Gallery, "gallery", gallery.Id),
            (GroupItemKind.Face, "face", face.Id),
            (GroupItemKind.Segment, "segment", segment.Id),
        };

        var created = new List<GroupItemDto>();
        for (var orderIndex = 0; orderIndex < expected.Length; orderIndex++)
        {
            var host = expected[orderIndex];
            created.Add(await eva.CreateGroupItemAsync(parent.Id, CreateItem(orderIndex, host.Kind, host.HostType, host.HostId), TestContext.Current.CancellationToken));
        }

        var controlItem = await eva.CreateGroupItemAsync(controlGroup.Id, CreateItem(0, GroupItemKind.Video, "video", video.Id), TestContext.Current.CancellationToken);

        var page = await eva.GetGroupItemsPageAsync(parent.Id, page: 1, perPage: 25, cancellationToken: TestContext.Current.CancellationToken);
        var freshParent = await eva.GetGroupByIdAsync(parent.Id, TestContext.Current.CancellationToken);
        var freshControl = await eva.GetGroupByIdAsync(controlGroup.Id, TestContext.Current.CancellationToken);

        page.TotalCount.Should().Be(expected.Length);
        page.Page.Should().Be(1);
        page.PerPage.Should().Be(25);
        page.Items.Select(item => item.Id).Should().Equal(created.Select(item => item.Id));
        page.Items.Select(item => item.GroupId).Should().AllBeEquivalentTo(parent.Id);
        page.Items.Select(item => item.Kind).Should().Equal(expected.Select(host => host.Kind));
        page.Items.Select(item => item.HostType).Should().Equal(expected.Select(host => host.HostType));
        page.Items.Select(item => item.HostId).Should().Equal(expected.Select(host => host.HostId));
        page.Items.Select(item => item.OrderIndex).Should().Equal(Enumerable.Range(0, expected.Length));
        page.Items.Should().NotContain(item => item.Id == controlItem.Id);
        freshParent.ItemCount.Should().Be(expected.Length);
        freshControl.ItemCount.Should().Be(1);
    }

    private static GroupItemCreateDto CreateItem(
        int orderIndex,
        GroupItemKind kind,
        string hostType,
        int hostId)
        => new(
            OrderIndex: orderIndex,
            Kind: kind,
            VideoId: kind == GroupItemKind.Video ? hostId : null,
            HostType: hostType,
            HostId: hostId,
            StartSec: null,
            EndSec: null,
            Title: null,
            Notes: null,
            SourceSpanKey: null,
            SourceProfileId: null);
}
