using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane1Collection.Name)]
public sealed class AdministrativeDataTransferAuthorizationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenContentScopedSystemOperators_WhenAdministrativeDataIsTransferred_ThenAccessFailsClosed()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var scopeTag = await owner.CreateTagAsync($"Transfer scope {suffix}");
        const string password = "Administrative transfer 123!";

        var denyRoleName = $"Deny-scoped transfer {suffix}";
        var denyRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            denyRoleName,
            "Exercises administrative transfer denial for a read deny scope.",
            [Permissions.SystemBackup, Permissions.SystemRestore, Permissions.SystemSettingsWrite, Permissions.SystemWipe, Permissions.ImportStash, Permissions.VideosRead]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            denyRole.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{scopeTag.Id}}}", "read"));
        var denyUsername = $"deny-transfer-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(denyUsername, password, Roles: [denyRoleName]));
        using var denySession = await owner.CreateAuthSessionAsync(denyUsername, password);

        var allowRoleName = $"Allow-scoped transfer {suffix}";
        var allowRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            allowRoleName,
            "Exercises administrative transfer denial for an allow-only read scope.",
            [Permissions.SystemBackup, Permissions.SystemRestore, Permissions.SystemSettingsWrite, Permissions.SystemWipe, Permissions.ImportStash]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            allowRole.Id, EntityKinds.Video, "allow", "tag", $"{{\"tagId\":{scopeTag.Id}}}", "read"));
        var allowUsername = $"allow-transfer-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(allowUsername, password, Roles: [allowRoleName]));
        using var allowSession = await owner.CreateAuthSessionAsync(allowUsername, password);

        var writeRoleName = $"Write-scoped transfer {suffix}";
        var writeRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            writeRoleName,
            "Exercises administrative transfer denial for a write-only scope.",
            [Permissions.SystemRestore, Permissions.SystemSettingsWrite, Permissions.ImportStash]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            writeRole.Id, EntityKinds.Video, "deny", "all", "{}", "write"));
        var writeUsername = $"write-transfer-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(writeUsername, password, Roles: [writeRoleName]));
        using var writeSession = await owner.CreateAuthSessionAsync(writeUsername, password);

        var deleteRoleName = $"Delete-scoped transfer {suffix}";
        var deleteRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            deleteRoleName,
            "Exercises whole-library restore denial for a delete-only scope.",
            [Permissions.SystemRestore, Permissions.SystemSettingsWrite, Permissions.SystemWipe, Permissions.ImportStash]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            deleteRole.Id, EntityKinds.Video, "deny", "all", "{}", "delete"));
        var deleteUsername = $"delete-transfer-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(deleteUsername, password, Roles: [deleteRoleName]));
        using var deleteSession = await owner.CreateAuthSessionAsync(deleteUsername, password);

        var unrestrictedRoleName = $"Unrestricted transfer {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(
            unrestrictedRoleName,
            "Exercises administrative transfer validation without content scopes.",
            [Permissions.SystemBackup, Permissions.SystemRestore, Permissions.ImportStash]));
        var unrestrictedUsername = $"unrestricted-transfer-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(unrestrictedUsername, password, Roles: [unrestrictedRoleName]));
        using var unrestrictedSession = await owner.CreateAuthSessionAsync(unrestrictedUsername, password);

        var systemReadRoleName = $"System reader {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(
            systemReadRoleName,
            "Reads system status without permission to discover backup paths.",
            [Permissions.SystemRead]));
        var systemReadUsername = $"system-reader-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(systemReadUsername, password, Roles: [systemReadRoleName]));
        using var systemReadSession = await owner.CreateAuthSessionAsync(systemReadUsername, password);

        foreach (var client in new[] { denySession.Client, allowSession.Client })
        {
            await client.AssertResponseAsync(HttpMethod.Post, "/api/metadata/export", HttpStatusCode.Forbidden, new ExportOptionsDto());
            await client.AssertResponseAsync(HttpMethod.Post, "/api/jobs/backup", HttpStatusCode.Forbidden);
            await client.AssertResponseAsync("/api/jobs/backup/latest", HttpStatusCode.Forbidden);
            await client.AssertResponseAsync(HttpMethod.Post, "/api/database/backup", HttpStatusCode.Forbidden);
            await client.AssertResponseAsync(HttpMethod.Post, "/api/database/migrate", HttpStatusCode.Forbidden);
            await client.AssertResponseAsync(HttpMethod.Post, "/api/database/wipe", HttpStatusCode.Forbidden);

            await client.AssertResponseAsync(HttpMethod.Post, "/api/metadata/import", HttpStatusCode.Forbidden, new ImportOptionsDto());
            await client.AssertResponseAsync(HttpMethod.Post, "/api/database/restore", HttpStatusCode.Forbidden, new RestoreBackupRequestDto(""));
            await client.AssertResponseAsync(HttpMethod.Post, "/api/stash-migration/import", HttpStatusCode.Forbidden, new { stashDbPath = "missing.sqlite", generatedPath = (string?)null });
            await client.AssertResponseAsync(HttpMethod.Post, "/api/database/config/restore", HttpStatusCode.BadRequest, new RestoreBackupRequestDto(""));
            await client.AssertResponseAsync(HttpMethod.Post, "/api/stash-migration/preview", HttpStatusCode.BadRequest, new { stashDbPath = "missing.sqlite" });
            await client.AssertResponseAsync("/api/stash-migration/import/missing-job", HttpStatusCode.NotFound);
        }

        await writeSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/metadata/import", HttpStatusCode.Forbidden, new ImportOptionsDto());
        await writeSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/database/restore", HttpStatusCode.Forbidden, new RestoreBackupRequestDto(""));
        await writeSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/database/config/restore", HttpStatusCode.BadRequest, new RestoreBackupRequestDto(""));
        await writeSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/database/migrate", HttpStatusCode.Forbidden);
        await writeSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/stash-migration/import", HttpStatusCode.Forbidden, new { stashDbPath = "missing.sqlite", generatedPath = (string?)null });
        await deleteSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/database/restore", HttpStatusCode.Forbidden, new RestoreBackupRequestDto(""));
        await deleteSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/database/migrate", HttpStatusCode.Forbidden);
        await deleteSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/database/wipe", HttpStatusCode.Forbidden);
        await systemReadSession.Client.AssertResponseAsync("/api/database/config/latest-backup", HttpStatusCode.Forbidden);

        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/metadata/export", payload: new ExportOptionsDto
        {
            IncludeVideos = false,
            IncludePerformers = false,
            IncludeStudios = false,
            IncludeTags = false,
            IncludeGalleries = false,
            IncludeGroups = false,
        });
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/metadata/import", HttpStatusCode.BadRequest, new ImportOptionsDto());
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/database/restore", HttpStatusCode.BadRequest, new RestoreBackupRequestDto(""));
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/database/config/restore", HttpStatusCode.BadRequest, new RestoreBackupRequestDto(""));
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/stash-migration/preview", HttpStatusCode.BadRequest, new { stashDbPath = "missing.sqlite" });
        await unrestrictedSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/stash-migration/import", HttpStatusCode.Accepted, new { stashDbPath = "missing.sqlite", generatedPath = (string?)null });
        await unrestrictedSession.Client.AssertResponseAsync("/api/stash-migration/import/missing-job", HttpStatusCode.NotFound);
        await unrestrictedSession.Client.AssertResponseAsync("/api/database/config/latest-backup");
    }
}
