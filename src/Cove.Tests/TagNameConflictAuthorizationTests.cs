using System.Reflection;
using Cove.Api.Controllers;
using Cove.Core.Auth;
using Cove.Core.Entities;

namespace Cove.Tests;

public sealed class TagNameConflictAuthorizationTests
{
    [Fact]
    public void CleanupPermissions_AreBackfilledOnlyForAdministrators_WithoutEscalatingLegacyTagRoles()
    {
        var tagDefinition = Assert.Single(
            Permissions.CorePermissions,
            permission => permission.Key == Permissions.TagNameConflictsManage);
        var entityDefinition = Assert.Single(
            Permissions.CorePermissions,
            permission => permission.Key == Permissions.EntityNameConflictsManage);

        Assert.True(tagDefinition.Dangerous);
        Assert.True(tagDefinition.GrantToAdminsByDefault);
        var tagImplications = Assert.IsType<string[]>(tagDefinition.Implies);
        Assert.Equal([Permissions.TagsRead, Permissions.TagsWrite, Permissions.TagsDelete], tagImplications);
        Assert.DoesNotContain(Permissions.PerformersDelete, tagImplications);
        Assert.DoesNotContain(Permissions.StudiosDelete, tagImplications);

        Assert.True(entityDefinition.Dangerous);
        Assert.True(entityDefinition.GrantToAdminsByDefault);
        Assert.Contains(Permissions.TagNameConflictsManage, entityDefinition.Implies!);
        Assert.Contains(Permissions.TagNameConflictsManage, Permissions.AdminDefaults());
        Assert.Contains(Permissions.EntityNameConflictsManage, Permissions.AdminDefaults());
        Assert.DoesNotContain(Permissions.TagNameConflictsManage, Permissions.MemberDefaults);
        Assert.DoesNotContain(Permissions.EntityNameConflictsManage, Permissions.MemberDefaults);
        Assert.DoesNotContain(Permissions.TagNameConflictsManage, Permissions.ViewerDefaults);
        Assert.DoesNotContain(Permissions.EntityNameConflictsManage, Permissions.ViewerDefaults);
        Assert.DoesNotContain(Permissions.TagNameConflictsManage, Permissions.GuestDefaults);
        Assert.DoesNotContain(Permissions.EntityNameConflictsManage, Permissions.GuestDefaults);

        var requirement = Assert.Single(
            typeof(TagNameConflictsController).GetCustomAttributes<RequiresPermissionAttribute>());
        Assert.Equal([Permissions.TagNameConflictsManage], requirement.Permissions);
        var entityRequirement = Assert.Single(
            typeof(EntityNameConflictsController).GetCustomAttributes<RequiresPermissionAttribute>());
        Assert.Equal([Permissions.EntityNameConflictsManage], entityRequirement.Permissions);
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

    [Theory]
    [InlineData(typeof(PerformersController), nameof(PerformersController.MergePerformers), EntityKinds.Performer, Permissions.PerformersWrite, Permissions.PerformersDelete)]
    [InlineData(typeof(StudiosController), nameof(StudiosController.MergeStudios), EntityKinds.Studio, Permissions.StudiosWrite, Permissions.StudiosDelete)]
    public void OrdinaryEntityMerge_RequiresWriteAndDeleteAccessToTheSelectedIds(
        Type controllerType,
        string methodName,
        string entityKind,
        string writePermission,
        string deletePermission)
    {
        var method = controllerType.GetMethod(methodName)!;
        var requirement = Assert.Single(method.GetCustomAttributes<RequiresPermissionAttribute>());
        Assert.Equal([writePermission, deletePermission], requirement.Permissions);

        var entityRequirements = method.GetCustomAttributes<RequiresEntityAccessAttribute>().ToArray();
        Assert.Contains(entityRequirements, attribute =>
            attribute.EntityKind == entityKind
            && attribute.Permission == writePermission
            && attribute.ActionArgumentName == "dto"
            && attribute.PropertyName == "TargetId");
        Assert.Contains(entityRequirements, attribute =>
            attribute.EntityKind == entityKind
            && attribute.Permission == deletePermission
            && attribute.ActionArgumentName == "dto"
            && attribute.PropertyName == "SourceIds");
    }
}
