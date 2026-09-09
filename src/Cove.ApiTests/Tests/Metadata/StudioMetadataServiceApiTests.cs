using System.Globalization;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Metadata;

public sealed class StudioMetadataServiceApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/studios/{id:int}/metadata-server/search")]
    [CoversEndpoint("POST", "/api/studios/{id:int}/metadata-server/import")]
    public async Task GivenRemoteStudio_WhenSearchedAndImported_ThenFallbackParentAndFreshPersistenceAreExact()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var remote = AsMetadataService().CreateStudio(new MetadataServiceRemoteStudio(
            $"studio-{suffix}",
            $"Metadata studio {suffix}",
            [$"Alias {suffix}"],
            [$"https://metadata.example/studios/{suffix}"],
            new MetadataServiceStudioParent($"parent-{suffix}", $"Parent studio {suffix}")));
        var local = await owner.CreateStudioAsync(new StudioBuilder().WithName(remote.Studio.Name).Build(), TestContext.Current.CancellationToken);

        var fallback = await owner.SearchStudioMetadataServiceAsync(local, string.Empty, remote, TestContext.Current.CancellationToken);
        AssertMatch(fallback.Should().ContainSingle().Which, remote);
        var matches = await owner.SearchStudioMetadataServiceAsync(local, remote.Studio.Name, remote, TestContext.Current.CancellationToken);
        var match = matches.Should().ContainSingle().Which;
        AssertMatch(match, remote);
        var imported = await owner.ImportStudioFromMetadataServiceAsync(local, match, TestContext.Current.CancellationToken);
        AssertImported(imported, remote);
        var persisted = await owner.GetStudioByIdAsync(local.Id, TestContext.Current.CancellationToken);
        AssertImported(persisted, remote);
        persisted.ParentId.Should().Be(imported.ParentId);
        var parent = await owner.GetStudioByIdAsync(persisted.ParentId!.Value, TestContext.Current.CancellationToken);
        parent.Name.Should().Be(remote.Studio.Parent!.Name);
        parent.RemoteIds.Should().ContainSingle(id =>
            id.Endpoint == remote.Endpoint.AbsoluteUri && id.RemoteId == remote.Studio.Parent.Id);
        var existingRemoteMatches = await owner.SearchStudioMetadataServiceAsync(persisted, string.Empty, remote, TestContext.Current.CancellationToken);
        AssertMatch(existingRemoteMatches.Should().ContainSingle().Which, remote);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/studios/metadata-server/find-by-ids")]
    [CoversEndpoint("POST", "/api/studios/{id:int}/metadata-server/submit-draft")]
    [CoversEndpoint("POST", "/api/studios/metadata-server/batch-tag")]
    public async Task GivenRemoteStudios_WhenFindingDraftingAndBatching_ThenAuthorizationIsolationAndRefreshAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var remote = AsMetadataService().CreateStudio(new MetadataServiceRemoteStudio(
            $"studio-{suffix}",
            $"Remote studio {suffix}",
            [$"Remote alias {suffix}"],
            [$"https://metadata.example/{suffix}"],
            new MetadataServiceStudioParent($"parent-{suffix}", $"Draft parent {suffix}")));
        var batchRemote = AsMetadataService().CreateStudio(new MetadataServiceRemoteStudio(
            $"batch-{suffix}",
            $"Batch studio {suffix}",
            [$"Batch alias {suffix}"],
            [$"https://metadata.example/batch-{suffix}"],
            new MetadataServiceStudioParent($"batch-parent-{suffix}", $"Batch parent {suffix}")));
        var local = await owner.CreateStudioAsync(new StudioBuilder().WithName(remote.Studio.Name).Build(), TestContext.Current.CancellationToken);
        var batch = await owner.CreateStudioAsync(new StudioBuilder()
            .WithName(batchRemote.Studio.Name)
            .WithAlias($"preserved {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var unmatched = await owner.CreateStudioAsync(new StudioBuilder()
            .WithName($"unmatched {suffix}")
            .WithDetails("unmatched")
            .Build(), TestContext.Current.CancellationToken);
        var control = await owner.CreateStudioAsync(new StudioBuilder()
            .WithName($"control {suffix}")
            .WithDetails("control")
            .Build(), TestContext.Current.CancellationToken);
        var beforeLocal = await owner.GetStudioByIdAsync(local.Id, TestContext.Current.CancellationToken);
        var beforeBatch = await owner.GetStudioByIdAsync(batch.Id, TestContext.Current.CancellationToken);
        var beforeUnmatched = await owner.GetStudioByIdAsync(unmatched.Id, TestContext.Current.CancellationToken);
        var beforeControl = await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken);
        const string password = "Studio metadata permissions 123!";
        await owner.CreateUserAsync(new CreateUserRequest($"studio-none-{suffix}", password, Roles: []), TestContext.Current.CancellationToken);
        await owner.CreateUserAsync(new CreateUserRequest(
            $"studio-viewer-{suffix}",
            password,
            Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var none = await owner.CreateAuthSessionAsync($"studio-none-{suffix}", password, TestContext.Current.CancellationToken);
        using var viewerSession = await owner.CreateAuthSessionAsync($"studio-viewer-{suffix}", password, TestContext.Current.CancellationToken);
        Func<Task> forbiddenFind = () => none.Client.FindStudioMetadataServiceByIdsAsync(remote, [remote.Id]);
        await forbiddenFind.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var duplicate = remote.Id.ToUpperInvariant();
        var viewerMatches = await viewerSession.Client.FindStudioMetadataServiceByIdsAsync(remote, [remote.Id, duplicate, $"missing-{suffix}"], TestContext.Current.CancellationToken);
        AssertMatch(viewerMatches.Should().ContainSingle().Which, remote);
        var viewerBatch = new MetadataServerStudioBatchTagRequestDto
        {
            Endpoint = batchRemote.Endpoint.AbsoluteUri,
            Ids = [batch.Id],
        };
        var forbiddenWrites = new Func<Task>[]
        {
            async () => _ = await viewerSession.Client.ImportStudioFromMetadataServiceAsync(
                local,
                viewerMatches.Single()),
            async () => _ = await viewerSession.Client.SubmitStudioDraftToMetadataServiceAsync(local, remote),
            async () => _ = await viewerSession.Client.StartStudioMetadataBatchTagAsync(viewerBatch),
        };
        foreach (var write in forbiddenWrites)
            await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AsMetadataService().StudioDraftSubmissions.Should().BeEmpty();
        AssertUnchanged(await owner.GetStudioByIdAsync(local.Id, TestContext.Current.CancellationToken), beforeLocal);
        AssertUnchanged(await owner.GetStudioByIdAsync(batch.Id, TestContext.Current.CancellationToken), beforeBatch);
        AssertUnchanged(await owner.GetStudioByIdAsync(unmatched.Id, TestContext.Current.CancellationToken), beforeUnmatched);

        var found = await member.FindStudioMetadataServiceByIdsAsync(remote, [remote.Id, duplicate, $"missing-{suffix}"], TestContext.Current.CancellationToken);
        AssertMatch(found.Should().ContainSingle().Which, remote);
        (await member.FindStudioMetadataServiceByIdsAsync(remote, [], TestContext.Current.CancellationToken)).Should().BeEmpty();
        var imported = await member.ImportStudioFromMetadataServiceAsync(local, found.Single(), TestContext.Current.CancellationToken);
        AssertImported(imported, remote);
        var draftId = await member.SubmitStudioDraftToMetadataServiceAsync(imported, remote, TestContext.Current.CancellationToken);
        var draft = AsMetadataService().StudioDraftSubmissions.Should().ContainSingle().Which;
        draft.DraftId.Should().Be(draftId);
        draft.Input.GetProperty("id").GetString().Should().Be(remote.Id);
        draft.Input.GetProperty("name").GetString().Should().Be(remote.Studio.Name);
        draft.Input.GetProperty("aliases").GetString().Should().Be(string.Join(", ", remote.Studio.Aliases));
        draft.Input.GetProperty("urls")
            .EnumerateArray()
            .Select(url => url.GetString())
            .Should().Equal(remote.Studio.Urls);
        draft.Input.GetProperty("parent").GetProperty("name").GetString().Should().Be(remote.Studio.Parent!.Name);
        draft.Input.GetProperty("parent").GetProperty("id").GetString().Should().Be(remote.Studio.Parent.Id);

        var explicitJob = await member.StartStudioMetadataBatchTagAsync(new MetadataServerStudioBatchTagRequestDto
            {
                Endpoint = batchRemote.Endpoint.AbsoluteUri,
                Ids = [batch.Id, unmatched.Id],
                RefreshAlreadyTagged = true,
                ExcludeFields = ["aliases"],
                CreateParentStudios = false,
            }, TestContext.Current.CancellationToken);
        explicitJob.ItemCount.Should().Be(2);
        var completed = await owner.WaitForTerminalJobAsync(explicitJob.JobId, TestContext.Current.CancellationToken);
        completed.Status.Should().Be(JobStatus.Completed);
        completed.Type.Should().Be("metadata-server:studios");
        completed.Error.Should().BeNull();
        var batchUpdated = await owner.GetStudioByIdAsync(batch.Id, TestContext.Current.CancellationToken);
        batchUpdated.Aliases.Should().Equal(beforeBatch.Aliases);
        batchUpdated.Urls.Should().Equal(batchRemote.Studio.Urls);
        batchUpdated.RemoteIds.Should().ContainSingle(id =>
            id.Endpoint == batchRemote.Endpoint.AbsoluteUri && id.RemoteId == batchRemote.Id);
        batchUpdated.ParentId.Should().BeNull();
        (await owner.GetStudiosAsync(TestContext.Current.CancellationToken)).Should().NotContain(
            studio => studio.Name == batchRemote.Studio.Parent!.Name);
        AssertUnchanged(await owner.GetStudioByIdAsync(unmatched.Id, TestContext.Current.CancellationToken), beforeUnmatched);
        AssertUnchanged(await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken), beforeControl);
        var driftedName = $"Drifted batch studio {suffix}";
        await owner.UpdateStudioAsync(batch.Id, new { name = driftedName }, TestContext.Current.CancellationToken);
        var request = new MetadataServerStudioBatchTagRequestDto
        {
            Endpoint = batchRemote.Endpoint.AbsoluteUri,
            SelectAll = true,
            Filter = new StudioFilter { Name = driftedName },
            RefreshAlreadyTagged = true,
            ExcludeFields = ["aliases"],
            CreateParentStudios = true,
        };
        var memberRole = (await owner.GetRolesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var deny = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.Studio,
            batch.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "write"), TestContext.Current.CancellationToken);
        var jobIds = (await owner.ReadEndpointAsync(ReadEndpoint.Jobs, TestContext.Current.CancellationToken))
            .EnumerateArray()
            .Select(job => job.GetProperty("id").GetRawText())
            .ToArray();
        var beforeDeniedBatch = await owner.GetStudioByIdAsync(batch.Id, TestContext.Current.CancellationToken);
        var beforeDeniedUnmatched = await owner.GetStudioByIdAsync(unmatched.Id, TestContext.Current.CancellationToken);
        var beforeDeniedControl = await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken);
        Func<Task> denied = () => member.StartStudioMetadataBatchTagAsync(request);
        await denied.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await owner.ReadEndpointAsync(ReadEndpoint.Jobs, TestContext.Current.CancellationToken))
            .EnumerateArray()
            .Select(job => job.GetProperty("id").GetRawText())
            .Should().Equal(jobIds);
        AssertUnchanged(await owner.GetStudioByIdAsync(batch.Id, TestContext.Current.CancellationToken), beforeDeniedBatch);
        AssertUnchanged(await owner.GetStudioByIdAsync(unmatched.Id, TestContext.Current.CancellationToken), beforeDeniedUnmatched);
        AssertUnchanged(await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken), beforeDeniedControl);
        (await owner.GetStudiosAsync(TestContext.Current.CancellationToken)).Should().NotContain(
            studio => studio.Name == batchRemote.Studio.Parent!.Name);
        await owner.DeleteEntityOverrideAsync(deny.Id, TestContext.Current.CancellationToken);
        var filtered = await member.StartStudioMetadataBatchTagAsync(request, TestContext.Current.CancellationToken);
        filtered.ItemCount.Should().Be(1);
        var refreshed = await owner.WaitForTerminalJobAsync(filtered.JobId, TestContext.Current.CancellationToken);
        refreshed.Status.Should().Be(JobStatus.Completed);
        refreshed.Type.Should().Be("metadata-server:studios");
        refreshed.Error.Should().BeNull();
        var restored = await owner.GetStudioByIdAsync(batch.Id, TestContext.Current.CancellationToken);
        restored.Name.Should().Be(batchRemote.Studio.Name);
        restored.Urls.Should().Equal(batchRemote.Studio.Urls);
        restored.Aliases.Should().Equal(beforeBatch.Aliases);
        var restoredParent = await owner.GetStudioByIdAsync(restored.ParentId!.Value, TestContext.Current.CancellationToken);
        restoredParent.Name.Should().Be(batchRemote.Studio.Parent!.Name);
        restoredParent.RemoteIds.Should().ContainSingle(id =>
            id.Endpoint == batchRemote.Endpoint.AbsoluteUri && id.RemoteId == batchRemote.Studio.Parent.Id);
        AssertUnchanged(await owner.GetStudioByIdAsync(unmatched.Id, TestContext.Current.CancellationToken), beforeUnmatched);
        AssertUnchanged(await owner.GetStudioByIdAsync(control.Id, TestContext.Current.CancellationToken), beforeControl);
    }

    private static void AssertMatch(
        MetadataServerStudioMatchDto actual,
        MetadataServiceStudioHandle remote)
    {
        actual.Endpoint.Should().Be(remote.Endpoint.AbsoluteUri);
        actual.MetadataServerName.Should().Be(TestCatalog.MetadataServices.PulpMovieDb.Name);
        actual.Id.Should().Be(remote.Id);
        actual.Name.Should().Be(remote.Studio.Name);
        actual.ImageUrl.Should().BeNull();
        actual.Aliases.Should().Equal(remote.Studio.Aliases);
        actual.Urls.Should().Equal(remote.Studio.Urls);
        actual.ParentName.Should().Be(remote.Studio.Parent?.Name);
    }

    private static void AssertImported(StudioDto actual, MetadataServiceStudioHandle remote)
    {
        actual.Name.Should().Be(remote.Studio.Name);
        actual.ParentId.Should().NotBeNull();
        actual.ParentName.Should().Be(remote.Studio.Parent?.Name);
        actual.Aliases.Should().Equal(remote.Studio.Aliases);
        actual.Urls.Should().Equal(remote.Studio.Urls);
        actual.RemoteIds.Should().ContainSingle(
            id => id.Endpoint == remote.Endpoint.AbsoluteUri && id.RemoteId == remote.Id);
    }

    private static void AssertUnchanged(StudioDto actual, StudioDto before)
    {
        actual.Id.Should().Be(before.Id);
        actual.Name.Should().Be(before.Name);
        actual.ParentId.Should().Be(before.ParentId);
        actual.ParentName.Should().Be(before.ParentName);
        actual.Details.Should().Be(before.Details);
        actual.Aliases.Should().Equal(before.Aliases);
        actual.Urls.Should().Equal(before.Urls);
        actual.RemoteIds.Should().Equal(before.RemoteIds);
    }
}
