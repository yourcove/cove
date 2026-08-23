using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane1Collection.Name)]
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
            [Permissions.SystemSettingsWrite, Permissions.AiDataClear]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            readRole.Id, EntityKinds.Video, "deny", "all", "{}", "read"));
        var readUsername = $"read-maintenance-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(readUsername, password, Roles: [readRoleName]));
        using var readSession = await owner.CreateAuthSessionAsync(readUsername, password);

        var writeRoleName = $"Write-scoped maintenance {suffix}";
        var writeRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            writeRoleName,
            "Recomputes global derived data while write-scoped.",
            [Permissions.SystemSettingsWrite]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            writeRole.Id, EntityKinds.Video, "deny", "all", "{}", "write"));
        var writeUsername = $"write-maintenance-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(writeUsername, password, Roles: [writeRoleName]));
        using var writeSession = await owner.CreateAuthSessionAsync(writeUsername, password);

        var deleteRoleName = $"Delete-scoped maintenance {suffix}";
        var deleteRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            deleteRoleName,
            "Deletes global derived data while delete-scoped.",
            [Permissions.SystemSettingsWrite, Permissions.AiDataClear]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            deleteRole.Id, EntityKinds.Video, "deny", "all", "{}", "delete"));
        var deleteUsername = $"delete-maintenance-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(deleteUsername, password, Roles: [deleteRoleName]));
        using var deleteSession = await owner.CreateAuthSessionAsync(deleteUsername, password);

        var unrestrictedRoleName = $"Unrestricted maintenance {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(
            unrestrictedRoleName,
            "Maintains global derived data without content scopes.",
            [Permissions.SystemSettingsWrite, Permissions.AiDataClear]));
        var unrestrictedUsername = $"unrestricted-maintenance-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(unrestrictedUsername, password, Roles: [unrestrictedRoleName]));
        using var unrestrictedSession = await owner.CreateAuthSessionAsync(unrestrictedUsername, password);

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

        await readSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/system/maintenance/recompute-derived-counts", HttpStatusCode.Forbidden);
        await readSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/metadata/clean-generated", HttpStatusCode.Forbidden);
        await readSession.Client.AssertResponseAsync(HttpMethod.Delete, "/api/embeddings", HttpStatusCode.Forbidden, selector);
        await readSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", HttpStatusCode.Forbidden, purgeRequest);

        await writeSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/system/maintenance/recompute-derived-counts", HttpStatusCode.Forbidden);

        await deleteSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/metadata/clean-generated", HttpStatusCode.Forbidden);
        await deleteSession.Client.AssertResponseAsync(HttpMethod.Delete, "/api/embeddings", HttpStatusCode.Forbidden, selector);
        await deleteSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", payload: purgeRequest);
        await deleteSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", HttpStatusCode.Forbidden, destructivePurgeRequest);

        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/system/maintenance/recompute-derived-counts");
        var cleanGeneratedJobId = await unrestrictedSession.Client.StartMetadataCleanGeneratedAsync();
        await owner.WaitForTerminalJobAsync(cleanGeneratedJobId);
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Delete, "/api/embeddings", payload: selector);
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", payload: purgeRequest);
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/ai-data/purge", payload: destructivePurgeRequest);
    }
}
