using System.Reflection;
using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.Entities;

namespace Cove.Tests;

public sealed class TagNameConflictAuthorizationTests
{
    [Fact]
    public void CleanupPermission_IsBackfilledOnlyForAdministrators()
    {
        var definition = Assert.Single(
            Permissions.CorePermissions,
            permission => permission.Key == Permissions.TagNameConflictsManage);

        Assert.True(definition.Dangerous);
        Assert.True(definition.GrantToAdminsByDefault);
        Assert.Contains(Permissions.TagNameConflictsManage, Permissions.AdminDefaults());
        Assert.DoesNotContain(Permissions.TagNameConflictsManage, Permissions.MemberDefaults);
        Assert.DoesNotContain(Permissions.TagNameConflictsManage, Permissions.ViewerDefaults);
        Assert.DoesNotContain(Permissions.TagNameConflictsManage, Permissions.GuestDefaults);

        var requirement = Assert.Single(
            typeof(TagNameConflictsController).GetCustomAttributes<RequiresPermissionAttribute>());
        Assert.Equal([Permissions.TagNameConflictsManage], requirement.Permissions);
    }

    [Fact]
    public void OrdinaryTagMerge_RequiresBothWriteAndDeletePermissions()
    {
        var method = typeof(TagsController).GetMethod(nameof(TagsController.MergeTags));
        var requirement = Assert.Single(method!.GetCustomAttributes<RequiresPermissionAttribute>());

        Assert.Equal([Permissions.TagsWrite, Permissions.TagsDelete], requirement.Permissions);

        var entityRequirements = method!.GetCustomAttributes<RequiresEntityAccessAttribute>().ToArray();
        Assert.Contains(entityRequirements, attribute =>
            attribute.EntityKind == EntityKinds.Tag
            && attribute.Permission == Permissions.TagsWrite
            && attribute.ActionArgumentName == "dto"
            && attribute.PropertyName == "TargetId");
        Assert.Contains(entityRequirements, attribute =>
            attribute.EntityKind == EntityKinds.Tag
            && attribute.Permission == Permissions.TagsDelete
            && attribute.ActionArgumentName == "dto"
            && attribute.PropertyName == "SourceIds");
    }
}
