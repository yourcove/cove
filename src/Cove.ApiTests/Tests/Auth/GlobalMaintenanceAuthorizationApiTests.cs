using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Auth;

public sealed class GlobalMaintenanceAuthorizationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenContentScopedMaintainers_WhenGlobalDerivedDataIsMutated_ThenAccessFailsClosed()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        const string password = "Global maintenance 123!";

        var readRoleName = $"Read-scoped maintenance {suffix}";
        var readRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            readRoleName,
            "Maintains global derived data while read-scoped.",
            [Permissions.SystemSettingsWrite, Permissions.AiDataClear]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            readRole.Id, EntityKinds.Video, "deny", "all", "{}", "read"), TestContext.Current.CancellationToken);
        var readUsername = $"read-maintenance-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(readUsername, password, Roles: [readRoleName]), TestContext.Current.CancellationToken);
        using var readSession = await owner.CreateAuthSessionAsync(readUsername, password, TestContext.Current.CancellationToken);

        var writeRoleName = $"Write-scoped maintenance {suffix}";
        var writeRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            writeRoleName,
            "Recomputes global derived data while write-scoped.",
            [Permissions.SystemSettingsWrite]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            writeRole.Id, EntityKinds.Video, "deny", "all", "{}", "write"), TestContext.Current.CancellationToken);
        var writeUsername = $"write-maintenance-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(writeUsername, password, Roles: [writeRoleName]), TestContext.Current.CancellationToken);
        using var writeSession = await owner.CreateAuthSessionAsync(writeUsername, password, TestContext.Current.CancellationToken);

        var deleteRoleName = $"Delete-scoped maintenance {suffix}";
        var deleteRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            deleteRoleName,
            "Deletes global derived data while delete-scoped.",
            [Permissions.SystemSettingsWrite, Permissions.AiDataClear]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            deleteRole.Id, EntityKinds.Video, "deny", "all", "{}", "delete"), TestContext.Current.CancellationToken);
        var deleteUsername = $"delete-maintenance-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(deleteUsername, password, Roles: [deleteRoleName]), TestContext.Current.CancellationToken);
        using var deleteSession = await owner.CreateAuthSessionAsync(deleteUsername, password, TestContext.Current.CancellationToken);

        var unrestrictedRoleName = $"Unrestricted maintenance {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(
            unrestrictedRoleName,
            "Maintains global derived data without content scopes.",
            [Permissions.SystemSettingsWrite, Permissions.AiDataClear]), TestContext.Current.CancellationToken);
        var unrestrictedUsername = $"unrestricted-maintenance-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(unrestrictedUsername, password, Roles: [unrestrictedRoleName]), TestContext.Current.CancellationToken);
        using var unrestrictedSession = await owner.CreateAuthSessionAsync(unrestrictedUsername, password, TestContext.Current.CancellationToken);

        var selector = new { sourceKey = $"api-test-{suffix}" };
        var purgeRequest = new
        {
            sourceKey = $"api-test-{suffix}",
            dryRun = true,
        };
        var destructivePurgeRequest = new
        {
            sourceKey = $"api-test-{suffix}",
            dryRun = false,
        };

        await readSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/system/maintenance/recompute-derived-counts", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await readSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/metadata/clean-generated", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await readSession.Client.AssertResponseAsync(HttpMethod.Delete, "/api/embeddings", HttpStatusCode.Forbidden, selector, TestContext.Current.CancellationToken);
        await readSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", HttpStatusCode.Forbidden, purgeRequest, TestContext.Current.CancellationToken);

        await writeSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/system/maintenance/recompute-derived-counts", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);

        await deleteSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/metadata/clean-generated", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await deleteSession.Client.AssertResponseAsync(HttpMethod.Delete, "/api/embeddings", HttpStatusCode.Forbidden, selector, TestContext.Current.CancellationToken);
        await deleteSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", payload: purgeRequest, cancellationToken: TestContext.Current.CancellationToken);
        await deleteSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", HttpStatusCode.Forbidden, destructivePurgeRequest, TestContext.Current.CancellationToken);

        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/system/maintenance/recompute-derived-counts", cancellationToken: TestContext.Current.CancellationToken);
        var cleanGeneratedJobId = await unrestrictedSession.Client.StartMetadataCleanGeneratedAsync(TestContext.Current.CancellationToken);
        await owner.WaitForTerminalJobAsync(cleanGeneratedJobId, TestContext.Current.CancellationToken);
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Delete, "/api/embeddings", payload: selector, cancellationToken: TestContext.Current.CancellationToken);
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", payload: purgeRequest, cancellationToken: TestContext.Current.CancellationToken);
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", payload: destructivePurgeRequest, cancellationToken: TestContext.Current.CancellationToken);
    }
}
