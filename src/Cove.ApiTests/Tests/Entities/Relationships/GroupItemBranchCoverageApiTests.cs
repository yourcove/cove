using System.Globalization;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Relationships;

[Collection(ApiTestLane2Collection.Name)]
public sealed class GroupItemBranchCoverageApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenMemberReadDenialsForGroupItemHosts_WhenMemberReadsParent_ThenScopedHostsAreHiddenButFaceRemainsPermissionOnly()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var group = await owner.CreateGroupAsync($"Scoped group item parent {suffix}");
        var studio = await owner.CreateStudioAsync($"Scoped group item studio {suffix}");
        var tag = await owner.CreateTagAsync($"Scoped group item tag {suffix}");
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Scoped group item gallery {suffix}")
            .Build());
        var face = await owner.CreateFaceAsync(new FaceCreateDto($"Scoped group item face {suffix}", null, false, "api-test"));
        var video = await owner.CreateVideoAsync($"Scoped group item video {suffix}");
        var segment = await owner.CreateVideoSegmentAsync(video, $"Scoped group item segment {suffix}");
        var created = new[]
        {
            await owner.CreateGroupItemAsync(group.Id, CreateItem(0, GroupItemKind.Studio, "studio", studio.Id)),
            await owner.CreateGroupItemAsync(group.Id, CreateItem(1, GroupItemKind.Tag, "tag", tag.Id)),
            await owner.CreateGroupItemAsync(group.Id, CreateItem(2, GroupItemKind.Gallery, "gallery", gallery.Id)),
            await owner.CreateGroupItemAsync(group.Id, CreateItem(3, GroupItemKind.Face, "face", face.Id)),
            await owner.CreateGroupItemAsync(group.Id, CreateItem(4, GroupItemKind.Segment, "segment", segment.Id)),
        };
        var memberRole = (await owner.GetRolesAsync()).Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;

        (await member.GetGroupItemsPageAsync(group.Id, perPage: 10)).Items.Select(item => item.Id).Should().Equal(created.Select(item => item.Id));

        await CreateReadDenyAsync(owner, memberRole.Id, EntityKinds.Studio, studio.Id);
        await CreateReadDenyAsync(owner, memberRole.Id, EntityKinds.Tag, tag.Id);
        await CreateReadDenyAsync(owner, memberRole.Id, EntityKinds.Gallery, gallery.Id);
        await CreateReadDenyAsync(owner, memberRole.Id, EntityKinds.Video, video.Id);

        var page = await member.GetGroupItemsPageAsync(group.Id, perPage: 10);
        var deniedStudio = () => member.GetStudioByIdAsync(studio.Id);
        var deniedTag = () => member.GetTagByIdAsync(tag.Id);
        var deniedGallery = () => member.GetGalleryByIdAsync(gallery.Id);
        var deniedVideo = () => member.GetVideoByIdAsync(video.Id);
        var deniedSegment = () => member.GetSegmentByIdAsync(segment.Id);
        var retainedFace = await member.GetFaceByIdAsync(face.Id);

        await deniedStudio.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await deniedTag.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await deniedGallery.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await deniedVideo.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await deniedSegment.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(created[3].Id);
        page.Items.Single().HostType.Should().Be("face");
        page.Items.Single().HostId.Should().Be(face.Id);
        retainedFace.Id.Should().Be(face.Id);
    }

    private static async Task CreateReadDenyAsync(CoveClient owner, int roleId, string entityKind, int entityId)
    {
        var created = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            roleId,
            entityKind,
            entityId.ToString(CultureInfo.InvariantCulture),
            "deny",
            "read"));

        created.RoleId.Should().Be(roleId);
        created.EntityKind.Should().Be(entityKind);
        created.EntityId.Should().Be(entityId.ToString(CultureInfo.InvariantCulture));
        created.Effect.Should().Be("deny");
        created.AppliesTo.Should().Be("read");
    }

    private static GroupItemCreateDto CreateItem(
        int orderIndex,
        GroupItemKind kind,
        string hostType,
        int? hostId)
        => new(
            OrderIndex: orderIndex,
            Kind: kind,
            VideoId: string.Equals(hostType, "video", StringComparison.OrdinalIgnoreCase) ? hostId : null,
            HostType: hostType,
            HostId: hostId,
            StartSec: null,
            EndSec: null,
            Title: null,
            Notes: null,
            SourceSpanKey: null,
            SourceProfileId: null);
}
