using System.Net;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Auth;

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
        await AsDbUser().CreateAuditEventAsync(auditAction, $"{{\"password\":\"private password\",\"message\":\"Bearer header.payload.signature {auditSecret}\",\"safe\":\"visible\"}}", auditSecret, TestContext.Current.CancellationToken);
        var scopedTag = await owner.CreateTagAsync($"Observability scope {suffix}", TestContext.Current.CancellationToken);
        const string password = "Observability permissions 123!";

        var denyRoleName = $"Deny-scoped observability {suffix}";
        var denyRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            denyRoleName,
            "Reads operational data without access to every library entity.",
            [Permissions.AuditRead, Permissions.VideosRead]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            denyRole.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{scopedTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        var denyUsername = $"deny-observability-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(denyUsername, password, Roles: [denyRoleName]), TestContext.Current.CancellationToken);
        using var denySession = await owner.CreateAuthSessionAsync(denyUsername, password, TestContext.Current.CancellationToken);

        var allowRoleName = $"Allow-scoped observability {suffix}";
        var allowRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            allowRoleName,
            "Exercises fail-closed global observability for an allow-only scope.",
            [Permissions.AuditRead]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            allowRole.Id, EntityKinds.Video, "allow", "tag", $"{{\"tagId\":{scopedTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        var allowUsername = $"allow-observability-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(allowUsername, password, Roles: [allowRoleName]), TestContext.Current.CancellationToken);
        using var allowSession = await owner.CreateAuthSessionAsync(allowUsername, password, TestContext.Current.CancellationToken);

        var unrestrictedRoleName = $"Unrestricted observability {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(
            unrestrictedRoleName,
            "Reads global operational data without content scopes.",
            [Permissions.AuditRead]), TestContext.Current.CancellationToken);
        var unrestrictedUsername = $"unrestricted-observability-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(unrestrictedUsername, password, Roles: [unrestrictedRoleName]), TestContext.Current.CancellationToken);
        using var unrestrictedSession = await owner.CreateAuthSessionAsync(unrestrictedUsername, password, TestContext.Current.CancellationToken);

        var noPermissionRoleName = $"No observability {suffix}";
        await owner.CreateRoleAsync(new CreateRoleRequest(noPermissionRoleName, "Has no audit permission.", []), TestContext.Current.CancellationToken);
        var noPermissionUsername = $"no-observability-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(noPermissionUsername, password, Roles: [noPermissionRoleName]), TestContext.Current.CancellationToken);
        using var noPermissionSession = await owner.CreateAuthSessionAsync(noPermissionUsername, password, TestContext.Current.CancellationToken);

        foreach (var client in new[] { denySession.Client, allowSession.Client })
        {
            await client.AssertResponseAsync("/api/audit", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
            await client.AssertResponseAsync("/api/audit?page=2&perPage=1&action=auth&outcome=deny", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
            await client.AssertResponseAsync("/api/logs", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
            await client.AssertResponseAsync("/api/logs?level=Warning&limit=1", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        }

        await noPermissionSession.Client.AssertResponseAsync("/api/audit", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await noPermissionSession.Client.AssertResponseAsync("/api/logs", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await AsAnonymous().AssertResponseAsync("/api/audit", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);
        await AsAnonymous().AssertResponseAsync("/api/logs", HttpStatusCode.Unauthorized, TestContext.Current.CancellationToken);

        await unrestrictedSession.Client.AssertResponseAsync("/api/audit", cancellationToken: TestContext.Current.CancellationToken);
        await unrestrictedSession.Client.AssertResponseAsync("/api/logs", cancellationToken: TestContext.Current.CancellationToken);
        await owner.AssertResponseAsync("/api/audit?page=1&perPage=1", cancellationToken: TestContext.Current.CancellationToken);
        await owner.AssertResponseAsync("/api/logs?limit=1", cancellationToken: TestContext.Current.CancellationToken);
        var audit = (await owner.GetAuditEventsAsync(auditAction, TestContext.Current.CancellationToken)).Items.Should().ContainSingle().Which;
        audit.Detail.Should().Contain("visible").And.Contain("[REDACTED]");
        audit.Detail.Should().NotContain("private password").And.NotContain(auditSecret).And.NotContain("header.payload.signature");
        audit.TargetId.Should().Be("[REDACTED]");
    }
}
