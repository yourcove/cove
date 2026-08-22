using System.Globalization;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane2Collection.Name)]
public sealed class RoleAndContentRuleAdministrationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/roles/permissions")]
    [CoversEndpoint("GET", "/api/roles/{id:int}")]
    [CoversEndpoint("POST", "/api/roles")]
    [CoversEndpoint("PUT", "/api/roles/{id:int}")]
    [CoversEndpoint("DELETE", "/api/roles/{id:int}")]
    [CoversEndpoint("GET", "/api/content-rules/overrides")]
    [CoversEndpoint("DELETE", "/api/content-rules/overrides/{id:int}")]
    [CoversEndpoint("POST", "/api/content-rules")]
    [CoversEndpoint("PUT", "/api/content-rules/{id:int}")]
    [CoversEndpoint("DELETE", "/api/content-rules/{id:int}")]
    public async Task GivenOwnerManagedRoleAndContentRule_WhenLifecycleAndAuthorizationRun_ThenPersistenceVisibilityAndDeniedAdminMutationsAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var roleName = $"Scoped studio role {suffix}";
        var createdDescription = "Scoped studio role created by the administration lifecycle.";
        var updatedDescription = "Scoped studio role updated by the administration lifecycle.";

        var permissions = await owner.GetRolePermissionsAsync();
        permissions.Select(permission => permission.Key).Should().OnlyHaveUniqueItems();
        permissions.Select(permission => (permission.Category, permission.Key)).Should().Equal(
            permissions.OrderBy(permission => permission.Category).ThenBy(permission => permission.Key).Select(permission => (permission.Category, permission.Key)));
        var rolesWrite = permissions.Should().ContainSingle(permission => permission.Key == Permissions.RolesWrite).Which;
        rolesWrite.Category.Should().Be("Roles");
        rolesWrite.Description.Should().Be("Create or edit roles and permission assignments.");
        rolesWrite.Dangerous.Should().BeTrue();
        rolesWrite.Implies.Should().Equal(Permissions.RolesRead);
        rolesWrite.Source.Should().Be("core");
        var forbiddenMemberPermissionList = () => member.GetRolePermissionsAsync();
        await forbiddenMemberPermissionList.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");

        var created = await owner.CreateRoleAsync(new CreateRoleRequest(roleName, createdDescription, [Permissions.StudiosRead]));
        AssertRole(created, roleName, createdDescription, [Permissions.StudiosRead], expectedUserCount: 0);
        AssertRole(await owner.GetRoleAsync(created.Id), roleName, createdDescription, [Permissions.StudiosRead], expectedUserCount: 0);
        var updated = await owner.UpdateRoleAsync(created.Id, new UpdateRoleRequest(updatedDescription, [Permissions.StudiosRead, Permissions.TagsRead]));
        AssertRole(updated, roleName, updatedDescription, [Permissions.StudiosRead, Permissions.TagsRead], expectedUserCount: 0);
        AssertRole(await owner.GetRoleAsync(created.Id), roleName, updatedDescription, [Permissions.StudiosRead, Permissions.TagsRead], expectedUserCount: 0);

        var viewerUsername = $"roles-viewer-{suffix}";
        const string viewerPassword = "Roles viewer 123!";
        await owner.CreateUserAsync(new CreateUserRequest(viewerUsername, viewerPassword, Roles: [BuiltinRoles.Viewer]));
        using var viewerSession = await owner.CreateAuthSessionAsync(viewerUsername, viewerPassword);
        var viewer = viewerSession.Client;
        var forbiddenViewerGet = () => viewer.GetRoleAsync(created.Id);
        await forbiddenViewerGet.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var forbiddenMemberCreate = () => member.CreateRoleAsync(new CreateRoleRequest($"Forbidden role {suffix}", "must not persist", [Permissions.StudiosRead]));
        await forbiddenMemberCreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertRole((await owner.GetRolesAsync()).Should().ContainSingle(role => role.Id == created.Id).Which, roleName, updatedDescription, [Permissions.StudiosRead, Permissions.TagsRead], expectedUserCount: 0);
        (await owner.GetRolesAsync()).Should().NotContain(role => role.Name == $"Forbidden role {suffix}");
        var forbiddenViewerUpdate = () => viewer.UpdateRoleAsync(created.Id, new UpdateRoleRequest("forbidden update", [Permissions.StudiosWrite]));
        await forbiddenViewerUpdate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertRole(await owner.GetRoleAsync(created.Id), roleName, updatedDescription, [Permissions.StudiosRead, Permissions.TagsRead], expectedUserCount: 0);

        var studio = await owner.CreateStudioAsync($"Scoped studio {suffix}");
        var scopedUsername = $"scoped-role-user-{suffix}";
        const string scopedPassword = "Scoped role user 123!";
        var scopedUser = await owner.CreateUserAsync(new CreateUserRequest(scopedUsername, scopedPassword, Roles: [roleName]));
        using (var unrestrictedSession = await owner.CreateAuthSessionAsync(scopedUsername, scopedPassword))
            (await unrestrictedSession.Client.GetStudiosAsync()).Select(item => item.Id).Should().Equal(studio.Id);

        var createdRule = await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            created.Id,
            EntityKinds.Studio,
            "allow",
            "all",
            "{}",
            "read"));
        AssertRule(createdRule, created.Id, roleName, EntityKinds.Studio, "allow", "all", "{}", "read");
        AssertRule((await owner.GetContentRulesAsync(created.Id)).Should().ContainSingle().Which, created.Id, roleName, EntityKinds.Studio, "allow", "all", "{}", "read");
        var updatedRule = await owner.UpdateContentRuleAsync(createdRule.Id, new UpdateContentRuleRequest(
            Effect: "deny",
            ScopeKind: "all",
            ScopeValue: "{\"phase\": \"deny\"}",
            AppliesTo: "read"));
        AssertRule(updatedRule, created.Id, roleName, EntityKinds.Studio, "deny", "all", "{\"phase\": \"deny\"}", "read");
        AssertRule((await owner.GetContentRulesAsync(created.Id)).Should().ContainSingle().Which, created.Id, roleName, EntityKinds.Studio, "deny", "all", "{\"phase\": \"deny\"}", "read");

        using (var restrictedSession = await owner.CreateAuthSessionAsync(scopedUsername, scopedPassword))
            (await restrictedSession.Client.GetStudiosAsync()).Should().BeEmpty();

        var createdOverride = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            created.Id,
            EntityKinds.Studio,
            studio.Id.ToString(CultureInfo.InvariantCulture),
            "allow",
            "read"));
        createdOverride.RoleId.Should().Be(created.Id);
        createdOverride.RoleName.Should().Be(roleName);
        createdOverride.EntityKind.Should().Be(EntityKinds.Studio);
        createdOverride.EntityId.Should().Be(studio.Id.ToString(CultureInfo.InvariantCulture));
        createdOverride.Effect.Should().Be("allow");
        createdOverride.AppliesTo.Should().Be("read");
        AssertOverride((await owner.GetEntityOverridesAsync(created.Id, EntityKinds.Studio)).Should().ContainSingle().Which, created.Id, roleName, EntityKinds.Studio, studio.Id.ToString(CultureInfo.InvariantCulture), "allow", "read");
        using (var overrideSession = await owner.CreateAuthSessionAsync(scopedUsername, scopedPassword))
            (await overrideSession.Client.GetStudiosAsync()).Select(item => item.Id).Should().Equal(studio.Id);

        var forbiddenMemberOverrideDelete = () => member.DeleteEntityOverrideAsync(createdOverride.Id);
        await forbiddenMemberOverrideDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertOverride((await owner.GetEntityOverridesAsync(created.Id, EntityKinds.Studio)).Should().ContainSingle().Which, created.Id, roleName, EntityKinds.Studio, studio.Id.ToString(CultureInfo.InvariantCulture), "allow", "read");
        await owner.DeleteEntityOverrideAsync(createdOverride.Id);
        (await owner.GetEntityOverridesAsync(created.Id, EntityKinds.Studio)).Should().BeEmpty();
        using (var overrideDeletedSession = await owner.CreateAuthSessionAsync(scopedUsername, scopedPassword))
            (await overrideDeletedSession.Client.GetStudiosAsync()).Should().BeEmpty();

        var forbiddenMemberRuleCreate = () => member.CreateContentRuleAsync(new CreateContentRuleRequest(created.Id, EntityKinds.Studio, "deny", "all", "{}", "read"));
        await forbiddenMemberRuleCreate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertRule((await owner.GetContentRulesAsync(created.Id)).Should().ContainSingle().Which, created.Id, roleName, EntityKinds.Studio, "deny", "all", "{\"phase\": \"deny\"}", "read");
        var forbiddenViewerRuleDelete = () => viewer.DeleteContentRuleAsync(updatedRule.Id);
        await forbiddenViewerRuleDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertRule((await owner.GetContentRulesAsync(created.Id)).Should().ContainSingle().Which, created.Id, roleName, EntityKinds.Studio, "deny", "all", "{\"phase\": \"deny\"}", "read");

        await owner.DeleteContentRuleAsync(updatedRule.Id);
        (await owner.GetContentRulesAsync(created.Id)).Should().BeEmpty();
        using (var restoredSession = await owner.CreateAuthSessionAsync(scopedUsername, scopedPassword))
            (await restoredSession.Client.GetStudiosAsync()).Select(item => item.Id).Should().Equal(studio.Id);

        var forbiddenViewerRoleDelete = () => viewer.DeleteRoleAsync(created.Id);
        await forbiddenViewerRoleDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        AssertRole(await owner.GetRoleAsync(created.Id), roleName, updatedDescription, [Permissions.StudiosRead, Permissions.TagsRead], expectedUserCount: 1);
        await owner.DeleteRoleAsync(created.Id);
        var deletedRole = () => owner.GetRoleAsync(created.Id);
        await deletedRole.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        (await owner.GetRolesAsync()).Should().NotContain(role => role.Id == created.Id);
        var survivingScopedUser = await owner.GetUserAsync(scopedUser.Id);
        survivingScopedUser.Id.Should().Be(scopedUser.Id);
        survivingScopedUser.Username.Should().Be(scopedUsername);
        survivingScopedUser.Roles.Should().BeEmpty();
    }

    private static void AssertRole(RoleDto actual, string name, string description, IReadOnlyList<string> permissions, int expectedUserCount)
    {
        actual.Name.Should().Be(name);
        actual.Description.Should().Be(description);
        actual.IsBuiltin.Should().BeFalse();
        actual.IsSystem.Should().BeFalse();
        actual.Source.Should().Be("core");
        actual.Permissions.Should().Equal(permissions);
        actual.UserCount.Should().Be(expectedUserCount);
    }

    private static void AssertRule(ContentRuleDto actual, int roleId, string roleName, string entityKind, string effect, string scopeKind, string scopeValue, string appliesTo)
    {
        actual.RoleId.Should().Be(roleId);
        actual.RoleName.Should().Be(roleName);
        actual.EntityKind.Should().Be(entityKind);
        actual.Effect.Should().Be(effect);
        actual.ScopeKind.Should().Be(scopeKind);
        actual.ScopeValue.Should().Be(scopeValue);
        actual.AppliesTo.Should().Be(appliesTo);
    }

    private static void AssertOverride(EntityOverrideDto actual, int roleId, string roleName, string entityKind, string entityId, string effect, string appliesTo)
    {
        actual.RoleId.Should().Be(roleId);
        actual.RoleName.Should().Be(roleName);
        actual.EntityKind.Should().Be(entityKind);
        actual.EntityId.Should().Be(entityId);
        actual.Effect.Should().Be(effect);
        actual.AppliesTo.Should().Be(appliesTo);
        actual.CreatedAt.Should().BeOnOrBefore(DateTime.UtcNow);
    }
}
