namespace Cove.Core.Auth;

public enum PermissionMode
{
    /// <summary>The caller must hold every listed permission.</summary>
    All,
    /// <summary>The caller must hold at least one of the listed permissions.</summary>
    Any,
}

public enum EntityAccessDeniedBehavior
{
    Default,
    NotFound,
    Forbidden,
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresPermissionAttribute : Attribute
{
    public string[] Permissions { get; }
    public PermissionMode Mode { get; init; } = PermissionMode.All;

    public RequiresPermissionAttribute(params string[] permissions)
    {
        Permissions = permissions;
    }
}

/// <summary>
/// Marks a controller or action as exempt from the global default-deny filter.
/// The action still goes through standard authentication, but no permission check
/// is required (e.g. /api/auth/login, /api/system/status).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class AllowWithoutPermissionAttribute : Attribute
{
}

/// <summary>Allows a resolved share-link principal to invoke this viewing endpoint.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class AllowShareLinkAccessAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresEntityAccessAttribute : Attribute
{
    public string EntityKind { get; }
    public string Permission { get; }
    public string? RouteValueName { get; init; } = "id";
    public string? ActionArgumentName { get; init; }
    public string? PropertyName { get; init; }
    public EntityAccessDeniedBehavior DeniedBehavior { get; init; }

    public RequiresEntityAccessAttribute(string entityKind, string permission)
    {
        EntityKind = entityKind;
        Permission = permission;
    }
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresUnscopedEntityAccessAttribute(string appliesTo) : Attribute
{
    public string AppliesTo { get; } = appliesTo;
    public string? ActionArgumentName { get; init; }
    public string? PropertyName { get; init; }
}
