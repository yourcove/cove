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
        var local = await owner.CreateTagAsync(new TagBuilder().WithName(remote.Tag.Name).Build(), TestContext.Current.CancellationToken);

        var fallback = await owner.SearchTagMetadataServiceAsync(local, string.Empty, remote, TestContext.Current.CancellationToken);
        AssertMatch(fallback.Should().ContainSingle().Which, remote);
        var match = (await owner.SearchTagMetadataServiceAsync(local, remote.Tag.Name, remote, TestContext.Current.CancellationToken)).Should().ContainSingle().Which;
        AssertMatch(match, remote);

        var imported = await owner.ImportTagFromMetadataServiceAsync(local, match, TestContext.Current.CancellationToken);
        AssertImported(imported, remote);
        imported.ImportWarnings.Should().BeEmpty();
        var persisted = await owner.GetTagByIdAsync(local.Id, TestContext.Current.CancellationToken);
        AssertImported(persisted, remote);
        var existingRemote = await owner.SearchTagMetadataServiceAsync(persisted, string.Empty, remote, TestContext.Current.CancellationToken);
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
            .Build(), TestContext.Current.CancellationToken);
        var batch = await owner.CreateTagAsync(new TagBuilder()
            .WithName(batchRemote.Tag.Name)
            .WithDescription($"local batch {suffix}")
            .WithAlias($"preserved {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var unmatched = await owner.CreateTagAsync(new TagBuilder().WithName($"unmatched {suffix}").WithDescription($"unmatched details {suffix}").Build(), TestContext.Current.CancellationToken);
        var control = await owner.CreateTagAsync(new TagBuilder().WithName($"control {suffix}").WithDescription($"control details {suffix}").Build(), TestContext.Current.CancellationToken);
        var beforeLocal = await owner.GetTagByIdAsync(local.Id, TestContext.Current.CancellationToken);
        var beforeBatch = await owner.GetTagByIdAsync(batch.Id, TestContext.Current.CancellationToken);
        var beforeUnmatched = await owner.GetTagByIdAsync(unmatched.Id, TestContext.Current.CancellationToken);
        var beforeControl = await owner.GetTagByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var member = AsUser(ApiTestUsers.Eva);

        const string password = "Tag metadata permissions 123!";
        await owner.CreateUserAsync(new CreateUserRequest($"tag-no-role-{suffix}", password, Roles: []), TestContext.Current.CancellationToken);
        await owner.CreateUserAsync(new CreateUserRequest($"tag-viewer-{suffix}", password, Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var noRoleSession = await owner.CreateAuthSessionAsync($"tag-no-role-{suffix}", password, TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync($"tag-viewer-{suffix}", password, TestContext.Current.CancellationToken);
        var viewer = viewerSession.Client;
        Func<Task> forbiddenFind = () => noRoleSession.Client.FindTagMetadataServiceByIdsAsync(remote, [remote.Id]);
        await forbiddenFind.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var duplicateId = remote.Id.ToUpperInvariant();
        var viewerMatches = await viewer.FindTagMetadataServiceByIdsAsync(remote, [remote.Id, $"missing-{suffix}", duplicateId], TestContext.Current.CancellationToken);
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
        AssertUnchanged(await owner.GetTagByIdAsync(local.Id, TestContext.Current.CancellationToken), beforeLocal);
        AssertUnchanged(await owner.GetTagByIdAsync(batch.Id, TestContext.Current.CancellationToken), beforeBatch);
        AssertUnchanged(await owner.GetTagByIdAsync(unmatched.Id, TestContext.Current.CancellationToken), beforeUnmatched);

        var found = await member.FindTagMetadataServiceByIdsAsync(remote, [remote.Id, $"missing-{suffix}", duplicateId], TestContext.Current.CancellationToken);
        AssertMatch(found.Should().ContainSingle().Which, remote);
        (await owner.FindTagMetadataServiceByIdsAsync(remote, [], TestContext.Current.CancellationToken)).Should().BeEmpty();
        var imported = await member.ImportTagFromMetadataServiceAsync(local, found.Single(), TestContext.Current.CancellationToken);
        var draftId = await member.SubmitTagDraftToMetadataServiceAsync(imported, remote, TestContext.Current.CancellationToken);
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
        }, TestContext.Current.CancellationToken);
        batchStart.ItemCount.Should().Be(2);
        var job = await owner.WaitForTerminalJobAsync(batchStart.JobId, TestContext.Current.CancellationToken);
        job.Status.Should().Be(JobStatus.Completed);
        job.Type.Should().Be("metadata-server:tags");
        job.Error.Should().BeNull();
        var batchUpdated = await owner.GetTagByIdAsync(batch.Id, TestContext.Current.CancellationToken);
        batchUpdated.Description.Should().Be(batchRemote.Tag.Description);
        batchUpdated.Aliases.Should().Equal(beforeBatch.Aliases);
        batchUpdated.RemoteIds.Should().ContainSingle(id => id.Endpoint == batchRemote.Endpoint.AbsoluteUri && id.RemoteId == batchRemote.Id);
        AssertUnchanged(await owner.GetTagByIdAsync(unmatched.Id, TestContext.Current.CancellationToken), beforeUnmatched);
        AssertUnchanged(await owner.GetTagByIdAsync(control.Id, TestContext.Current.CancellationToken), beforeControl);

        await owner.UpdateTagAsync(batch.Id, new TagUpdateDto(null, null, $"drifted {suffix}", null, null, null, null, null), TestContext.Current.CancellationToken);
        var filteredRequest = new MetadataServerTagBatchTagRequestDto
        {
            Endpoint = batchRemote.Endpoint.AbsoluteUri,
            SelectAll = true,
            Filter = new TagFilter { Name = batchRemote.Tag.Name },
            RefreshAlreadyTagged = true,
            ExcludeFields = ["aliases"],
        };
        var memberRole = (await owner.GetRolesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var writeDeny = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Tag,
            batch.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "write"), TestContext.Current.CancellationToken);
        var beforeDeniedBatch = await owner.GetTagByIdAsync(batch.Id, TestContext.Current.CancellationToken);
        var beforeDeniedUnmatched = await owner.GetTagByIdAsync(unmatched.Id, TestContext.Current.CancellationToken);
        var beforeDeniedControl = await owner.GetTagByIdAsync(control.Id, TestContext.Current.CancellationToken);
        var jobIdsBeforeDeniedSelectAll = (await owner.ReadEndpointAsync(ReadEndpoint.Jobs, TestContext.Current.CancellationToken))
            .EnumerateArray()
            .Select(job => job.GetProperty("id").GetRawText())
            .ToArray();
        Func<Task> deniedSelectAll = () => member.StartTagMetadataBatchTagAsync(filteredRequest);
        await deniedSelectAll.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        var jobIdsAfterDeniedSelectAll = (await owner.ReadEndpointAsync(ReadEndpoint.Jobs, TestContext.Current.CancellationToken))
            .EnumerateArray()
            .Select(job => job.GetProperty("id").GetRawText())
            .ToArray();
        jobIdsAfterDeniedSelectAll.Should().Equal(jobIdsBeforeDeniedSelectAll);
        AssertUnchanged(await owner.GetTagByIdAsync(batch.Id, TestContext.Current.CancellationToken), beforeDeniedBatch);
        AssertUnchanged(await owner.GetTagByIdAsync(unmatched.Id, TestContext.Current.CancellationToken), beforeDeniedUnmatched);
        AssertUnchanged(await owner.GetTagByIdAsync(control.Id, TestContext.Current.CancellationToken), beforeDeniedControl);
        await owner.DeleteEntityOverrideAsync(writeDeny.Id, TestContext.Current.CancellationToken);

        var filtered = await member.StartTagMetadataBatchTagAsync(filteredRequest, TestContext.Current.CancellationToken);
        filtered.ItemCount.Should().Be(1);
        var filteredJob = await owner.WaitForTerminalJobAsync(filtered.JobId, TestContext.Current.CancellationToken);
        filteredJob.Status.Should().Be(JobStatus.Completed);
        filteredJob.Type.Should().Be("metadata-server:tags");
        filteredJob.Error.Should().BeNull();
        var restored = await owner.GetTagByIdAsync(batch.Id, TestContext.Current.CancellationToken);
        restored.Description.Should().Be(batchRemote.Tag.Description);
        restored.Aliases.Should().Equal(beforeBatch.Aliases);
        AssertUnchanged(await owner.GetTagByIdAsync(unmatched.Id, TestContext.Current.CancellationToken), beforeUnmatched);
        AssertUnchanged(await owner.GetTagByIdAsync(control.Id, TestContext.Current.CancellationToken), beforeControl);
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
