using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane1Collection.Name)]
public sealed class AdministrativeObservabilityAuthorizationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenContentScopedAuditReaders_WhenGlobalObservabilityIsRead_ThenAccessFailsClosed()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var auditAction = $"api_test.observability.{suffix}";
        var auditSecret = $"cove_pat_{Guid.NewGuid():N}_private-token";
        await AsDbUser().CreateAuditEventAsync(
            auditAction,
            $"{{\"password\":\"private password\",\"message\":\"Bearer header.payload.signature {auditSecret}\",\"safe\":\"visible\"}}",
            auditSecret);
        var scopedTag = await owner.CreateTagAsync($"Observability scope {suffix}");
        const string password = "Observability permissions 123!";

        var denyRoleName = $"Deny-scoped observability {suffix}";
        var denyRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            denyRoleName,
            "Reads operational data without access to every library entity.",
            [Permissions.AuditRead, Permissions.VideosRead]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            denyRole.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{scopedTag.Id}}}", "read"));
        var denyUsername = $"deny-observability-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(denyUsername, password, Roles: [denyRoleName]));
        using var denySession = await owner.CreateAuthSessionAsync(denyUsername, password);

        var allowRoleName = $"Allow-scoped observability {suffix}";
        var allowRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            allowRoleName,
            "Exercises fail-closed global observability for an allow-only scope.",
            [Permissions.AuditRead]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            allowRole.Id, EntityKinds.Video, "allow", "tag", $"{{\"tagId\":{scopedTag.Id}}}", "read"));
        var allowUsername = $"allow-observability-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(allowUsername, password, Roles: [allowRoleName]));
        using var allowSession = await owner.CreateAuthSessionAsync(allowUsername, password);

        var unrestrictedRoleName = $"Unrestricted observability {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(
            unrestrictedRoleName,
            "Reads global operational data without content scopes.",
            [Permissions.AuditRead]));
        var unrestrictedUsername = $"unrestricted-observability-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(unrestrictedUsername, password, Roles: [unrestrictedRoleName]));
        using var unrestrictedSession = await owner.CreateAuthSessionAsync(unrestrictedUsername, password);

        var noPermissionRoleName = $"No observability {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(noPermissionRoleName, "Has no audit permission.", []));
        var noPermissionUsername = $"no-observability-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(noPermissionUsername, password, Roles: [noPermissionRoleName]));
        using var noPermissionSession = await owner.CreateAuthSessionAsync(noPermissionUsername, password);

        foreach (var client in new[] { denySession.Client, allowSession.Client })
        {
            await client.AssertResponseAsync("/api/audit", HttpStatusCode.Forbidden);
            await client.AssertResponseAsync("/api/audit?page=2&perPage=1&action=auth&outcome=deny", HttpStatusCode.Forbidden);
            await client.AssertResponseAsync("/api/logs", HttpStatusCode.Forbidden);
            await client.AssertResponseAsync("/api/logs?level=Warning&limit=1", HttpStatusCode.Forbidden);
        }

        await noPermissionSession.Client.AssertResponseAsync("/api/audit", HttpStatusCode.Forbidden);
        await noPermissionSession.Client.AssertResponseAsync("/api/logs", HttpStatusCode.Forbidden);
        await AsAnonymous().AssertResponseAsync("/api/audit", HttpStatusCode.Unauthorized);
        await AsAnonymous().AssertResponseAsync("/api/logs", HttpStatusCode.Unauthorized);

        await unrestrictedSession.Client.AssertResponseAsync("/api/audit");
        await unrestrictedSession.Client.AssertResponseAsync("/api/logs");
        await owner.AssertResponseAsync("/api/audit?page=1&perPage=1");
        await owner.AssertResponseAsync("/api/logs?limit=1");
        var audit = (await owner.GetAuditEventsAsync(auditAction)).Items.Should().ContainSingle().Which;
        audit.Detail.Should().Contain("visible").And.Contain("[REDACTED]");
        audit.Detail.Should().NotContain("private password").And.NotContain(auditSecret).And.NotContain("header.payload.signature");
        audit.TargetId.Should().Be("[REDACTED]");
    }
}
