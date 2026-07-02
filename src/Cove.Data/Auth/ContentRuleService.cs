using Cove.Core.Auth;
using Cove.Core.Entities.Auth;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Auth;

public sealed class ContentRuleService : IContentRuleService
{
    private static readonly HashSet<string> ValidEntityKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "video", "performer", "tag", "studio", "gallery", "image", "group", "segment", "marker", "file",
    };

    private static readonly HashSet<string> ValidEffects = new(StringComparer.OrdinalIgnoreCase) { "allow", "deny" };
    private static readonly HashSet<string> ValidScopeKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "tag", "studio", "attribute", "expression",
    };

    private static readonly HashSet<string> ValidAppliesTo = new(StringComparer.OrdinalIgnoreCase)
    {
        "read", "write", "delete", "all",
    };

    private readonly CoveContext _db;
    private readonly IAuditService _audit;

    public ContentRuleService(CoveContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IReadOnlyList<ContentRuleDto>> ListAsync(int? roleId = null, CancellationToken ct = default)
    {
        var query = _db.RoleContentRules.AsNoTracking().Include(rule => rule.Role).AsQueryable();
        if (roleId is { } selectedRoleId)
            query = query.Where(rule => rule.RoleId == selectedRoleId);

        var rows = await query.OrderBy(rule => rule.RoleId).ThenBy(rule => rule.EntityKind).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ContentRuleDto> CreateAsync(CreateContentRuleRequest req, CovePrincipal? actor, CancellationToken ct = default)
    {
        Validate(req.EntityKind, req.Effect, req.ScopeKind, req.AppliesTo);

        var role = await _db.Roles.FirstOrDefaultAsync(item => item.Id == req.RoleId, ct)
            ?? throw new InvalidOperationException($"Role {req.RoleId} not found.");

        var entity = new RoleContentRule
        {
            RoleId = req.RoleId,
            EntityKind = NormalizeEntityKind(req.EntityKind),
            Effect = req.Effect.ToLowerInvariant(),
            ScopeKind = req.ScopeKind.ToLowerInvariant(),
            ScopeValue = string.IsNullOrWhiteSpace(req.ScopeValue) ? "{}" : req.ScopeValue,
            AppliesTo = req.AppliesTo.ToLowerInvariant(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.RoleContentRules.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            AuditActions.SettingsChange,
            AuditOutcomes.Success,
            actor,
            "content_rule",
            entity.Id.ToString(),
            new { action = "create", role = role.Name, entity.EntityKind, entity.Effect, entity.ScopeKind, entity.AppliesTo },
            ct);

        entity.Role = role;
        return ToDto(entity);
    }

    public async Task<ContentRuleDto> UpdateAsync(int id, UpdateContentRuleRequest req, CovePrincipal? actor, CancellationToken ct = default)
    {
        var entity = await _db.RoleContentRules.Include(rule => rule.Role).FirstOrDefaultAsync(rule => rule.Id == id, ct)
            ?? throw new InvalidOperationException($"Content rule {id} not found.");

        if (req.Effect is not null)
        {
            if (!ValidEffects.Contains(req.Effect))
                throw new InvalidOperationException("Invalid effect.");
            entity.Effect = req.Effect.ToLowerInvariant();
        }

        if (req.ScopeKind is not null)
        {
            if (!ValidScopeKinds.Contains(req.ScopeKind))
                throw new InvalidOperationException("Invalid scope kind.");
            entity.ScopeKind = req.ScopeKind.ToLowerInvariant();
        }

        if (req.ScopeValue is not null)
            entity.ScopeValue = req.ScopeValue;

        if (req.AppliesTo is not null)
        {
            if (!ValidAppliesTo.Contains(req.AppliesTo))
                throw new InvalidOperationException("Invalid appliesTo.");
            entity.AppliesTo = req.AppliesTo.ToLowerInvariant();
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            AuditActions.SettingsChange,
            AuditOutcomes.Success,
            actor,
            "content_rule",
            entity.Id.ToString(),
            new { action = "update", entity.Effect, entity.ScopeKind, entity.AppliesTo },
            ct);

        return ToDto(entity);
    }

    public async Task DeleteAsync(int id, CovePrincipal? actor, CancellationToken ct = default)
    {
        var entity = await _db.RoleContentRules.FirstOrDefaultAsync(rule => rule.Id == id, ct);
        if (entity is null)
            return;

        _db.RoleContentRules.Remove(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            AuditActions.SettingsChange,
            AuditOutcomes.Success,
            actor,
            "content_rule",
            id.ToString(),
            new { action = "delete" },
            ct);
    }

    public async Task<IReadOnlyList<EntityOverrideDto>> ListOverridesAsync(int? roleId = null, string? entityKind = null, CancellationToken ct = default)
    {
        var query = _db.RoleEntityOverrides.AsNoTracking().Include(overrideItem => overrideItem.Role).AsQueryable();
        if (roleId is { } selectedRoleId)
            query = query.Where(overrideItem => overrideItem.RoleId == selectedRoleId);

        if (!string.IsNullOrEmpty(entityKind))
        {
            var normalizedEntityKind = NormalizeEntityKind(entityKind);
            query = query.Where(overrideItem => overrideItem.EntityKind == normalizedEntityKind);
        }

        var rows = await query.OrderBy(overrideItem => overrideItem.RoleId).ThenBy(overrideItem => overrideItem.EntityKind).ToListAsync(ct);
        return rows.Select(overrideItem => new EntityOverrideDto(
            overrideItem.Id,
            overrideItem.RoleId,
            overrideItem.Role?.Name ?? string.Empty,
            ToClientEntityKind(overrideItem.EntityKind),
            overrideItem.EntityId,
            overrideItem.Effect,
            overrideItem.AppliesTo,
            overrideItem.CreatedAt)).ToList();
    }

    public async Task<EntityOverrideDto> CreateOverrideAsync(CreateEntityOverrideRequest req, CovePrincipal? actor, CancellationToken ct = default)
    {
        if (!ValidEntityKinds.Contains(req.EntityKind))
            throw new InvalidOperationException("Invalid entity kind.");
        if (!ValidEffects.Contains(req.Effect))
            throw new InvalidOperationException("Invalid effect.");
        if (!ValidAppliesTo.Contains(req.AppliesTo))
            throw new InvalidOperationException("Invalid appliesTo.");

        var role = await _db.Roles.FirstOrDefaultAsync(item => item.Id == req.RoleId, ct)
            ?? throw new InvalidOperationException($"Role {req.RoleId} not found.");

        var entity = new RoleEntityOverride
        {
            RoleId = req.RoleId,
            EntityKind = NormalizeEntityKind(req.EntityKind),
            EntityId = req.EntityId,
            Effect = req.Effect.ToLowerInvariant(),
            AppliesTo = req.AppliesTo.ToLowerInvariant(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.RoleEntityOverrides.Add(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            AuditActions.SettingsChange,
            AuditOutcomes.Success,
            actor,
            "entity_override",
            entity.Id.ToString(),
            new { action = "create", role = role.Name, entity.EntityKind, entity.EntityId, entity.Effect, entity.AppliesTo },
            ct);

        return new EntityOverrideDto(
            entity.Id,
            entity.RoleId,
            role.Name,
            ToClientEntityKind(entity.EntityKind),
            entity.EntityId,
            entity.Effect,
            entity.AppliesTo,
            entity.CreatedAt);
    }

    public async Task DeleteOverrideAsync(int id, CovePrincipal? actor, CancellationToken ct = default)
    {
        var entity = await _db.RoleEntityOverrides.FirstOrDefaultAsync(overrideItem => overrideItem.Id == id, ct);
        if (entity is null)
            return;

        _db.RoleEntityOverrides.Remove(entity);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            AuditActions.SettingsChange,
            AuditOutcomes.Success,
            actor,
            "entity_override",
            id.ToString(),
            new { action = "delete" },
            ct);
    }

    private static void Validate(string entityKind, string effect, string scopeKind, string appliesTo)
    {
        if (!ValidEntityKinds.Contains(entityKind))
            throw new InvalidOperationException("Invalid entity kind.");
        if (!ValidEffects.Contains(effect))
            throw new InvalidOperationException("Invalid effect.");
        if (!ValidScopeKinds.Contains(scopeKind))
            throw new InvalidOperationException("Invalid scope kind.");
        if (!ValidAppliesTo.Contains(appliesTo))
            throw new InvalidOperationException("Invalid appliesTo.");
    }

    private static ContentRuleDto ToDto(RoleContentRule rule) => new(
        rule.Id,
        rule.RoleId,
        rule.Role?.Name ?? string.Empty,
        ToClientEntityKind(rule.EntityKind),
        rule.Effect,
        rule.ScopeKind,
        rule.ScopeValue,
        rule.AppliesTo,
        rule.CreatedAt,
        rule.UpdatedAt);

    private static string NormalizeEntityKind(string entityKind)
    {
        var normalized = entityKind.ToLowerInvariant();
        return normalized == "segment" ? "marker" : normalized;
    }

    private static string ToClientEntityKind(string entityKind) =>
        entityKind.Equals("marker", StringComparison.OrdinalIgnoreCase) ? "segment" : entityKind;
}
