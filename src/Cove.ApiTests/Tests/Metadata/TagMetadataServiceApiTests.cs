using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using System.Globalization;

namespace Cove.ApiTests.Tests.Metadata;

[Collection(ApiTestLane1Collection.Name)]
public sealed class TagMetadataServiceApiTests(ITestOutputHelper output, CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/tags/{id:int}/metadata-server/search")]
    [CoversEndpoint("POST", "/api/tags/{id:int}/metadata-server/import")]
    public async Task GivenRemoteTag_WhenSearchedAndImported_ThenFallbackAndFreshPersistenceAreExact()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var remote = AsMetadataService().CreateTag(new MetadataServiceRemoteTag(
            $"tag-{suffix}",
            $"Metadata tag {suffix}",
            $"Remote description {suffix}",
            [$"Remote alias {suffix}"]));
        var local = await owner.CreateTagAsync(new TagBuilder().WithName(remote.Tag.Name).Build());

        var fallback = await owner.SearchTagMetadataServiceAsync(local, string.Empty, remote);
        AssertMatch(fallback.Should().ContainSingle().Which, remote);
        var match = (await owner.SearchTagMetadataServiceAsync(local, remote.Tag.Name, remote)).Should().ContainSingle().Which;
        AssertMatch(match, remote);

        var imported = await owner.ImportTagFromMetadataServiceAsync(local, match);
        AssertImported(imported, remote);
        imported.ImportWarnings.Should().BeEmpty();
        var persisted = await owner.GetTagByIdAsync(local.Id);
        AssertImported(persisted, remote);
        var existingRemote = await owner.SearchTagMetadataServiceAsync(persisted, string.Empty, remote);
        AssertMatch(existingRemote.Should().ContainSingle().Which, remote);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/tags/metadata-server/find-by-ids")]
    [CoversEndpoint("POST", "/api/tags/{id:int}/metadata-server/submit-draft")]
    [CoversEndpoint("POST", "/api/tags/metadata-server/batch-tag")]
    public async Task GivenRemoteTags_WhenFindingDraftingAndBatchTagging_ThenPermissionsIsolationAndRefreshAreExact()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var remote = AsMetadataService().CreateTag(new MetadataServiceRemoteTag(
            $"tag-{suffix}", $"Remote tag {suffix}", $"Remote details {suffix}", [$"Remote alias {suffix}"]));
        var batchRemote = AsMetadataService().CreateTag(new MetadataServiceRemoteTag(
            $"batch-{suffix}", $"Batch tag {suffix}", $"Batch remote details {suffix}", [$"Batch remote alias {suffix}"]));
        var local = await owner.CreateTagAsync(new TagBuilder()
            .WithName(remote.Tag.Name)
            .WithDescription($"local {suffix}")
            .Build());
        var batch = await owner.CreateTagAsync(new TagBuilder()
            .WithName(batchRemote.Tag.Name)
            .WithDescription($"local batch {suffix}")
            .WithAlias($"preserved {suffix}")
            .Build());
        var unmatched = await owner.CreateTagAsync(new TagBuilder().WithName($"unmatched {suffix}").WithDescription($"unmatched details {suffix}").Build());
        var control = await owner.CreateTagAsync(new TagBuilder().WithName($"control {suffix}").WithDescription($"control details {suffix}").Build());
        var beforeLocal = await owner.GetTagByIdAsync(local.Id);
        var beforeBatch = await owner.GetTagByIdAsync(batch.Id);
        var beforeUnmatched = await owner.GetTagByIdAsync(unmatched.Id);
        var beforeControl = await owner.GetTagByIdAsync(control.Id);
        var member = AsUser(ApiTestUsers.Eva);

        const string password = "Tag metadata permissions 123!";
        await owner.CreateUserAsync(new CreateUserRequest($"tag-no-role-{suffix}", password, Roles: []));
        await owner.CreateUserAsync(new CreateUserRequest($"tag-viewer-{suffix}", password, Roles: [BuiltinRoles.Viewer]));
        using var noRoleSession = await owner.CreateAuthSessionAsync($"tag-no-role-{suffix}", password);
        using var viewerSession = await owner.CreateAuthSessionAsync($"tag-viewer-{suffix}", password);
        var viewer = viewerSession.Client;
        Func<Task> forbiddenFind = () => noRoleSession.Client.FindTagMetadataServiceByIdsAsync(remote, [remote.Id]);
        await forbiddenFind.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var duplicateId = remote.Id.ToUpperInvariant();
        var viewerMatches = await viewer.FindTagMetadataServiceByIdsAsync(
            remote,
            [remote.Id, $"missing-{suffix}", duplicateId]);
        AssertMatch(viewerMatches.Should().ContainSingle().Which, remote);
        var viewerRequest = new MetadataServerTagBatchTagRequestDto
        {
            Endpoint = batchRemote.Endpoint.AbsoluteUri,
            Ids = [batch.Id],
        };
        foreach (var write in new Func<Task>[]
                 {
                     async () => _ = await viewer.ImportTagFromMetadataServiceAsync(local, viewerMatches.Single()),
                     async () => _ = await viewer.SubmitTagDraftToMetadataServiceAsync(local, remote),
                     async () => _ = await viewer.StartTagMetadataBatchTagAsync(viewerRequest),
                 })
            await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AsMetadataService().TagDraftSubmissions.Should().BeEmpty();
        AssertUnchanged(await owner.GetTagByIdAsync(local.Id), beforeLocal);
        AssertUnchanged(await owner.GetTagByIdAsync(batch.Id), beforeBatch);
        AssertUnchanged(await owner.GetTagByIdAsync(unmatched.Id), beforeUnmatched);

        var found = await member.FindTagMetadataServiceByIdsAsync(
            remote,
            [remote.Id, $"missing-{suffix}", duplicateId]);
        AssertMatch(found.Should().ContainSingle().Which, remote);
        (await owner.FindTagMetadataServiceByIdsAsync(remote, [])).Should().BeEmpty();
        var imported = await member.ImportTagFromMetadataServiceAsync(local, found.Single());
        var draftId = await member.SubmitTagDraftToMetadataServiceAsync(imported, remote);
        var draft = AsMetadataService().TagDraftSubmissions.Should().ContainSingle().Which;
        draft.DraftId.Should().Be(draftId);
        draft.Input.GetProperty("id").GetString().Should().Be(remote.Id);
        draft.Input.GetProperty("name").GetString().Should().Be(remote.Tag.Name);
        draft.Input.GetProperty("description").GetString().Should().Be(remote.Tag.Description);
        draft.Input.GetProperty("aliases").GetString().Should().Be(string.Join(", ", remote.Tag.Aliases));

        var batchStart = await member.StartTagMetadataBatchTagAsync(new MetadataServerTagBatchTagRequestDto
        {
            Endpoint = batchRemote.Endpoint.AbsoluteUri,
            Ids = [batch.Id, unmatched.Id],
            RefreshAlreadyTagged = true,
            ExcludeFields = ["aliases"],
        });
        batchStart.ItemCount.Should().Be(2);
        var job = await owner.WaitForTerminalJobAsync(batchStart.JobId);
        job.Status.Should().Be(JobStatus.Completed);
        job.Type.Should().Be("metadata-server:tags");
        job.Error.Should().BeNull();
        var batchUpdated = await owner.GetTagByIdAsync(batch.Id);
        batchUpdated.Description.Should().Be(batchRemote.Tag.Description);
        batchUpdated.Aliases.Should().Equal(beforeBatch.Aliases);
        batchUpdated.RemoteIds.Should().ContainSingle(id => id.Endpoint == batchRemote.Endpoint.AbsoluteUri && id.RemoteId == batchRemote.Id);
        AssertUnchanged(await owner.GetTagByIdAsync(unmatched.Id), beforeUnmatched);
        AssertUnchanged(await owner.GetTagByIdAsync(control.Id), beforeControl);

        await owner.UpdateTagAsync(
            batch.Id,
            new TagUpdateDto(null, null, $"drifted {suffix}", null, null, null, null, null));
        var filteredRequest = new MetadataServerTagBatchTagRequestDto
        {
            Endpoint = batchRemote.Endpoint.AbsoluteUri,
            SelectAll = true,
            Filter = new TagFilter { Name = batchRemote.Tag.Name },
            RefreshAlreadyTagged = true,
            ExcludeFields = ["aliases"],
        };
        var memberRole = (await owner.GetRolesAsync())
            .Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var writeDeny = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Tag,
            batch.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "write"));
        var beforeDeniedBatch = await owner.GetTagByIdAsync(batch.Id);
        var beforeDeniedUnmatched = await owner.GetTagByIdAsync(unmatched.Id);
        var beforeDeniedControl = await owner.GetTagByIdAsync(control.Id);
        var jobIdsBeforeDeniedSelectAll = (await owner.ReadEndpointAsync(ReadEndpoint.Jobs))
            .EnumerateArray()
            .Select(job => job.GetProperty("id").GetRawText())
            .ToArray();
        Func<Task> deniedSelectAll = () => member.StartTagMetadataBatchTagAsync(filteredRequest);
        await deniedSelectAll.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        var jobIdsAfterDeniedSelectAll = (await owner.ReadEndpointAsync(ReadEndpoint.Jobs))
            .EnumerateArray()
            .Select(job => job.GetProperty("id").GetRawText())
            .ToArray();
        jobIdsAfterDeniedSelectAll.Should().Equal(jobIdsBeforeDeniedSelectAll);
        AssertUnchanged(await owner.GetTagByIdAsync(batch.Id), beforeDeniedBatch);
        AssertUnchanged(await owner.GetTagByIdAsync(unmatched.Id), beforeDeniedUnmatched);
        AssertUnchanged(await owner.GetTagByIdAsync(control.Id), beforeDeniedControl);
        await owner.DeleteEntityOverrideAsync(writeDeny.Id);

        var filtered = await member.StartTagMetadataBatchTagAsync(filteredRequest);
        filtered.ItemCount.Should().Be(1);
        var filteredJob = await owner.WaitForTerminalJobAsync(filtered.JobId);
        filteredJob.Status.Should().Be(JobStatus.Completed);
        filteredJob.Type.Should().Be("metadata-server:tags");
        filteredJob.Error.Should().BeNull();
        var restored = await owner.GetTagByIdAsync(batch.Id);
        restored.Description.Should().Be(batchRemote.Tag.Description);
        restored.Aliases.Should().Equal(beforeBatch.Aliases);
        AssertUnchanged(await owner.GetTagByIdAsync(unmatched.Id), beforeUnmatched);
        AssertUnchanged(await owner.GetTagByIdAsync(control.Id), beforeControl);
    }

    private static void AssertImported(TagDetailDto actual, MetadataServiceTagHandle remote)
    {
        actual.Name.Should().Be(remote.Tag.Name);
        actual.Description.Should().Be(remote.Tag.Description);
        actual.Aliases.Should().Equal(remote.Tag.Aliases);
        actual.RemoteIds.Should().ContainSingle(
            id => id.Endpoint == remote.Endpoint.AbsoluteUri && id.RemoteId == remote.Id);
    }

    private static void AssertMatch(MetadataServerTagMatchDto actual, MetadataServiceTagHandle remote)
    {
        actual.Endpoint.Should().Be(remote.Endpoint.AbsoluteUri);
        actual.MetadataServerName.Should().Be(TestCatalog.MetadataServices.PulpMovieDb.Name);
        actual.Id.Should().Be(remote.Id);
        actual.Name.Should().Be(remote.Tag.Name);
        actual.Description.Should().Be(remote.Tag.Description);
        actual.Aliases.Should().Equal(remote.Tag.Aliases);
    }

    private static void AssertUnchanged(TagDetailDto actual, TagDetailDto before)
    {
        actual.Id.Should().Be(before.Id);
        actual.Name.Should().Be(before.Name);
        actual.Description.Should().Be(before.Description);
        actual.Aliases.Should().Equal(before.Aliases);
        actual.RemoteIds.Should().Equal(before.RemoteIds);
    }
}
