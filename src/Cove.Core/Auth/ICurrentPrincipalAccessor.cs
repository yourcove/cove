using System.Security.Claims;
using Cove.Core.Entities;
using PermissionKeys = Cove.Core.Auth.Permissions;

namespace Cove.Core.Auth;

/// <summary>
/// Scoped per-request accessor for the resolved Cove principal (user + roles + permissions).
/// Populated by middleware after token validation.
/// </summary>
public interface ICurrentPrincipalAccessor
{
    CovePrincipal? Current { get; }
    void Set(CovePrincipal? principal);
}

public sealed class CurrentPrincipalAccessor : ICurrentPrincipalAccessor
{
    private static readonly AsyncLocal<CovePrincipal?> CurrentHolder = new();

    public CovePrincipal? Current => CurrentHolder.Value;

    public void Set(CovePrincipal? principal) => CurrentHolder.Value = principal;
}

public sealed class CovePrincipal
{
    public required int? UserId { get; init; }
    public required string Username { get; init; }
    public required PrincipalKind Kind { get; init; }
    public required IReadOnlySet<string> Roles { get; init; }
    /// <summary>Resolved permission set with wildcards expanded.</summary>
    public required IReadOnlySet<string> Permissions { get; init; }
    /// <summary>
    /// Entity kinds that need content-rule evaluation for read access.
    /// Empty means standard permission checks are sufficient for normal user requests.
    /// </summary>
    public IReadOnlySet<string> ReadRestrictedEntityKinds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Entity kinds that can be read only through explicit read-scoped allow rules or entity overrides.
    /// These do not imply unrestricted read access; SQL scope evaluation must still run per entity.
    /// </summary>
    public IReadOnlySet<string> ReadGrantedEntityKinds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public ClaimsPrincipal? ClaimsPrincipal { get; init; }
    /// <summary>For api_token / share_link principals: the originating token id.</summary>
    public Guid? TokenId { get; init; }
    public string? Ip { get; init; }
    public string? UserAgent { get; init; }

    public static CovePrincipal Anonymous(string? ip = null, string? userAgent = null) => new()
    {
        UserId = null,
        Username = "anonymous",
        Kind = PrincipalKind.Anonymous,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string>(),
        Ip = ip,
        UserAgent = userAgent,
    };

    public static CovePrincipal System() => new()
    {
        UserId = null,
        Username = "system",
        Kind = PrincipalKind.System,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<string> { Permissions_All },
    };

    private const string Permissions_All = "*";

    public bool Has(string permission)
    {
        if (Permissions.Contains("*")) return true;
        if (Permissions.Contains(permission)) return true;
        // wildcard "<resource>.*" or "*.read" support
        var dot = permission.IndexOf('.');
        if (dot < 0) return false;
        var resource = permission[..dot];
        var verb = permission[(dot + 1)..];
        if (Permissions.Contains(resource + ".*")) return true;
        if (Permissions.Contains("*." + verb)) return true;
        return false;
    }

    public bool HasReadGrant(string permission)
    {
        return TryGetReadGrantEntityKind(permission, out var entityKind)
            && ReadGrantedEntityKinds.Contains(entityKind);
    }

    /// <summary>
    /// Resolves the entity kind whose scoped read grants may satisfy the supplied read permission.
    /// Callers must still authorize the concrete entity before treating the grant as allowed.
    /// </summary>
    public static bool TryGetReadGrantEntityKind(string permission, out string entityKind)
    {
        switch (permission)
        {
            case PermissionKeys.VideosRead:
                entityKind = EntityKinds.Video;
                return true;
            case PermissionKeys.AudiosRead:
                entityKind = EntityKinds.Audio;
                return true;
            case PermissionKeys.TextsRead:
                entityKind = EntityKinds.Text;
                return true;
            case PermissionKeys.PerformersRead:
                entityKind = EntityKinds.Performer;
                return true;
            case PermissionKeys.FacesRead:
                entityKind = EntityKinds.Face;
                return true;
            case PermissionKeys.TagsRead:
                entityKind = EntityKinds.Tag;
                return true;
            case PermissionKeys.StudiosRead:
                entityKind = EntityKinds.Studio;
                return true;
            case PermissionKeys.GalleriesRead:
                entityKind = EntityKinds.Gallery;
                return true;
            case PermissionKeys.ImagesRead:
                entityKind = EntityKinds.Image;
                return true;
            case PermissionKeys.GroupsRead:
                entityKind = EntityKinds.Group;
                return true;
            case PermissionKeys.SegmentsRead:
                entityKind = EntityKinds.Segment;
                return true;
            case PermissionKeys.FilesRead:
                entityKind = EntityKinds.File;
                return true;
            default:
                entityKind = string.Empty;
                return false;
        }
    }

    public static bool TryGetReadGrantPermission(string entityKind, out string permission)
    {
        switch (entityKind)
        {
            case EntityKinds.Video:
                permission = PermissionKeys.VideosRead;
                return true;
            case EntityKinds.Audio:
                permission = PermissionKeys.AudiosRead;
                return true;
            case EntityKinds.Text:
                permission = PermissionKeys.TextsRead;
                return true;
            case EntityKinds.Performer:
                permission = PermissionKeys.PerformersRead;
                return true;
            case EntityKinds.Face:
                permission = PermissionKeys.FacesRead;
                return true;
            case EntityKinds.Tag:
                permission = PermissionKeys.TagsRead;
                return true;
            case EntityKinds.Studio:
                permission = PermissionKeys.StudiosRead;
                return true;
            case EntityKinds.Gallery:
                permission = PermissionKeys.GalleriesRead;
                return true;
            case EntityKinds.Image:
                permission = PermissionKeys.ImagesRead;
                return true;
            case EntityKinds.Group:
                permission = PermissionKeys.GroupsRead;
                return true;
            case EntityKinds.Segment:
                permission = PermissionKeys.SegmentsRead;
                return true;
            default:
                permission = string.Empty;
                return false;
        }
    }
}

public enum PrincipalKind
{
    Anonymous,
    User,
    ApiToken,
    ShareLink,
    System,
}
