using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Auth;

public sealed class ShareLinkService : IShareLinkService
{
    private static readonly HashSet<string> ValidEntityKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        EntityKinds.Video,
        EntityKinds.Performer,
        EntityKinds.Tag,
        EntityKinds.Studio,
        EntityKinds.Gallery,
        EntityKinds.Image,
        EntityKinds.Group,
        EntityKinds.Segment,
    };

    private readonly CoveContext _db;
    private readonly IAuditService _audit;
    private readonly IPermissionRegistry _registry;
    private readonly CoveConfiguration _config;

    public ShareLinkService(CoveContext db, IAuditService audit, IPermissionRegistry registry, CoveConfiguration config)
    {
        _db = db;
        _audit = audit;
        _registry = registry;
        _config = config;
    }

    public async Task<IReadOnlyList<ShareLinkDto>> ListAsync(int? createdByUserId = null, CancellationToken ct = default)
    {
        var query = _db.ShareLinks.AsNoTracking().Include(link => link.CreatedBy).AsQueryable();
        if (createdByUserId is { } selectedUserId)
            query = query.Where(link => link.CreatedByUserId == selectedUserId);

        var rows = await query.OrderByDescending(link => link.CreatedAt).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ShareLinkIssued> CreateAsync(CreateShareLinkRequest req, CovePrincipal? actor, CancellationToken ct = default)
    {
        var entityKind = NormalizeEntityKind(req.EntityKind);
        if (!ValidEntityKinds.Contains(entityKind))
            throw new InvalidOperationException("Invalid entity kind.");

        var entityIds = req.EntityIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (entityIds.Length == 0)
            throw new InvalidOperationException("At least one entity id is required.");

        await EnsureActorCanReadEntitiesAsync(entityKind, entityIds, ct);

        var id = Guid.NewGuid();
        var (plainToken, tokenHash) = NewOpaqueToken();
        var rawToken = $"cove_share_{id:N}_{plainToken}";

        var entity = new ShareLink
        {
            Id = id,
            CreatedByUserId = actor?.UserId,
            TokenHash = tokenHash,
            EntityKind = entityKind,
            EntityIds = JsonSerializer.Serialize(entityIds),
            ExpiresAt = req.ExpiresAt,
            PasswordHash = string.IsNullOrEmpty(req.Password)
                ? null
                : BCrypt.Net.BCrypt.HashPassword(req.Password, workFactor: 12),
            CreatedAt = DateTime.UtcNow,
        };

        _db.ShareLinks.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            AuditActions.ShareLinkCreate,
            AuditOutcomes.Success,
            actor,
            "share_link",
            id.ToString(),
            new { entity.EntityKind, ids = entityIds, req.ExpiresAt, hasPassword = entity.PasswordHash is not null },
            ct);

        return new ShareLinkIssued(id, rawToken, ToClientEntityKind(entity.EntityKind), entityIds, entity.CreatedAt, entity.ExpiresAt, entity.PasswordHash is not null);
    }

    public async Task RevokeAsync(Guid id, CovePrincipal? actor, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var affectedRows = await _db.ShareLinks
            .Where(link => link.Id == id && link.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(link => link.RevokedAt, now), ct);

        if (affectedRows > 0)
        {
            await _audit.LogAsync(AuditActions.ShareLinkRevoke, AuditOutcomes.Success, actor, "share_link", id.ToString(), null, ct);
        }
    }

    public async Task<CovePrincipal?> ResolveAsync(string token, string? password, string? ip, string? userAgent, CancellationToken ct = default)
    {
        if (!_config.Auth.AllowAnonymousShareLinks)
            return null;

        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("cove_share_", StringComparison.Ordinal))
            return null;

        var rest = token["cove_share_".Length..];
        var separatorIndex = rest.IndexOf('_');
        if (separatorIndex < 0)
            return null;

        var idPart = rest[..separatorIndex];
        var secret = rest[(separatorIndex + 1)..];
        if (!Guid.TryParseExact(idPart, "N", out var linkId))
            return null;

        var link = await _db.ShareLinks.AsNoTracking().FirstOrDefaultAsync(item => item.Id == linkId, ct);
        if (link is null || link.RevokedAt is not null)
            return null;
        if (link.ExpiresAt is { } expiresAt && expiresAt < DateTime.UtcNow)
            return null;

        var secretHash = HashToken(secret);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secretHash), Encoding.UTF8.GetBytes(link.TokenHash)))
            return null;

        if (link.PasswordHash is not null)
        {
            if (string.IsNullOrEmpty(password) || !BCrypt.Net.BCrypt.Verify(password, link.PasswordHash))
                return null;
        }

        try
        {
            await _db.ShareLinks
                .Where(item => item.Id == linkId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ViewCount, item => item.ViewCount + 1), ct);
        }
        catch
        {
        }

        await _audit.LogAsync(AuditActions.ShareLinkAccess, AuditOutcomes.Success, null, "share_link", linkId.ToString(), new { ip, userAgent }, ct);

        var guestRole = await _db.Roles
            .Where(role => role.Name == "Guest")
            .Include(role => role.Permissions)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        var permissionKeys = guestRole?.Permissions.Select(permission => permission.PermissionKey).ToList() ?? new List<string>();
        var permissions = _registry.Expand(permissionKeys);
        permissions.RemoveWhere(permission => permission.EndsWith(".write", StringComparison.Ordinal)
            || permission.EndsWith(".delete", StringComparison.Ordinal)
            || permission.EndsWith(".delete.file", StringComparison.Ordinal)
            || permission == "*");

        return new CovePrincipal
        {
            UserId = null,
            Username = $"share:{linkId:N}",
            Kind = PrincipalKind.ShareLink,
            Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Guest" },
            Permissions = permissions,
            ReadGrantedEntityKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { link.EntityKind },
            TokenId = linkId,
            Ip = ip,
            UserAgent = userAgent,
        };
    }

    public static string HashToken(string raw)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw)));
    }

    public static (string plain, string sha256Hash) NewOpaqueToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plain = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (plain, HashToken(plain));
    }

    private async Task EnsureActorCanReadEntitiesAsync(string entityKind, IReadOnlyCollection<string> entityIds, CancellationToken ct)
    {
        var parsedIds = new List<int>(entityIds.Count);
        foreach (var entityId in entityIds)
        {
            if (!int.TryParse(entityId, out var parsedId))
                throw new ForbiddenException("Share links can only include content the current user can already access.");

            parsedIds.Add(parsedId);
        }

        var readableCount = entityKind switch
        {
            EntityKinds.Video => await _db.ReadSet<Video>().CountAsync(video => parsedIds.Contains(video.Id), ct),
            EntityKinds.Performer => await _db.ReadSet<Performer>().CountAsync(performer => parsedIds.Contains(performer.Id), ct),
            EntityKinds.Tag => await _db.ReadSet<Tag>().CountAsync(tag => parsedIds.Contains(tag.Id), ct),
            EntityKinds.Studio => await _db.ReadSet<Studio>().CountAsync(studio => parsedIds.Contains(studio.Id), ct),
            EntityKinds.Gallery => await _db.ReadSet<Gallery>().CountAsync(gallery => parsedIds.Contains(gallery.Id), ct),
            EntityKinds.Image => await _db.ReadSet<Image>().CountAsync(image => parsedIds.Contains(image.Id), ct),
            EntityKinds.Group => await _db.ReadSet<Group>().CountAsync(group => parsedIds.Contains(group.Id), ct),
            EntityKinds.Segment => await _db.ReadSet<Segment>().CountAsync(segment => parsedIds.Contains(segment.Id), ct),
            _ => 0,
        };

        if (readableCount != parsedIds.Count)
            throw new ForbiddenException("Share links can only include content the current user can already access.");
    }

    private static ShareLinkDto ToDto(ShareLink shareLink)
    {
        List<string> ids;
        try
        {
            ids = JsonSerializer.Deserialize<List<string>>(shareLink.EntityIds) ?? new List<string>();
        }
        catch
        {
            ids = new List<string>();
        }

        return new ShareLinkDto(
            shareLink.Id,
            shareLink.CreatedByUserId,
            shareLink.CreatedBy?.Username,
            ToClientEntityKind(shareLink.EntityKind),
            ids,
            shareLink.CreatedAt,
            shareLink.ExpiresAt,
            shareLink.ViewCount,
            shareLink.PasswordHash is not null,
            shareLink.RevokedAt is not null);
    }

    private static string NormalizeEntityKind(string entityKind)
    {
        return entityKind.Trim().ToLowerInvariant();
    }

    private static string ToClientEntityKind(string entityKind) => entityKind;
}
