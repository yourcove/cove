using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data;

public partial class CoveContext
{
    private int _entityNameValidationSuppressionDepth;

    /// <summary>
    /// Allows merge services to pass through a pre-existing duplicate state while they remove it.
    /// Callers must validate the affected namespace before committing.
    /// </summary>
    public IDisposable SuppressEntityNameValidation()
    {
        _entityNameValidationSuppressionDepth++;
        return new EntityNameValidationScope(this);
    }

    private void NormalizeAndValidateEntityNames()
    {
        var performers = NormalizeChangedPerformerNames(normalizeValues: _entityNameValidationSuppressionDepth == 0);
        var studios = NormalizeChangedStudioNames(normalizeValues: _entityNameValidationSuppressionDepth == 0);
        if (_entityNameValidationSuppressionDepth > 0)
            return;
        ValidateTrackedIdentityCandidates(performers);
        if (performers.Count > 0)
        {
            var excludedIds = performers
                .Where(candidate => candidate.EntityId != null)
                .Select(candidate => candidate.EntityId!.Value)
                .Concat(DeletedIds<Performer>())
                .Distinct()
                .ToArray();
            var existing = Performers.IgnoreQueryFilters().AsNoTracking()
                .Where(performer => !excludedIds.Contains(performer.Id))
                .Select(performer => new { performer.Name, performer.Disambiguation })
                .ToList()
                .Select(performer => new EntityIdentityTarget(
                    NameConflictEntityTypes.Performer,
                    EntityNameRules.PerformerIdentityKey(performer.Name, performer.Disambiguation),
                    performer.Name,
                    performer.Disambiguation))
                .ToArray();
            ThrowForPersistedIdentityConflicts(performers, existing);
        }

        ValidateTrackedIdentityCandidates(studios);
        if (studios.Count > 0)
        {
            var excludedIds = studios
                .Where(candidate => candidate.EntityId != null)
                .Select(candidate => candidate.EntityId!.Value)
                .Concat(DeletedIds<Studio>())
                .Distinct()
                .ToArray();
            var existing = Studios.IgnoreQueryFilters().AsNoTracking()
                .Where(studio => !excludedIds.Contains(studio.Id))
                .Select(studio => studio.Name)
                .ToList()
                .Select(name => new EntityIdentityTarget(
                    NameConflictEntityTypes.Studio,
                    EntityNameRules.StudioIdentityKey(name),
                    name,
                    null))
                .ToArray();
            ThrowForPersistedIdentityConflicts(studios, existing);
        }
    }

    private async Task NormalizeAndValidateEntityNamesAsync(CancellationToken cancellationToken)
    {
        var performers = NormalizeChangedPerformerNames(normalizeValues: _entityNameValidationSuppressionDepth == 0);
        var studios = NormalizeChangedStudioNames(normalizeValues: _entityNameValidationSuppressionDepth == 0);
        if (_entityNameValidationSuppressionDepth > 0)
            return;
        ValidateTrackedIdentityCandidates(performers);
        if (performers.Count > 0)
        {
            var excludedIds = performers
                .Where(candidate => candidate.EntityId != null)
                .Select(candidate => candidate.EntityId!.Value)
                .Concat(DeletedIds<Performer>())
                .Distinct()
                .ToArray();
            var rows = await Performers.IgnoreQueryFilters().AsNoTracking()
                .Where(performer => !excludedIds.Contains(performer.Id))
                .Select(performer => new { performer.Name, performer.Disambiguation })
                .ToListAsync(cancellationToken);
            var existing = rows
                .Select(performer => new EntityIdentityTarget(
                    NameConflictEntityTypes.Performer,
                    EntityNameRules.PerformerIdentityKey(performer.Name, performer.Disambiguation),
                    performer.Name,
                    performer.Disambiguation))
                .ToArray();
            ThrowForPersistedIdentityConflicts(performers, existing);
        }

        ValidateTrackedIdentityCandidates(studios);
        if (studios.Count > 0)
        {
            var excludedIds = studios
                .Where(candidate => candidate.EntityId != null)
                .Select(candidate => candidate.EntityId!.Value)
                .Concat(DeletedIds<Studio>())
                .Distinct()
                .ToArray();
            var rows = await Studios.IgnoreQueryFilters().AsNoTracking()
                .Where(studio => !excludedIds.Contains(studio.Id))
                .Select(studio => studio.Name)
                .ToListAsync(cancellationToken);
            var existing = rows
                .Select(name => new EntityIdentityTarget(
                    NameConflictEntityTypes.Studio,
                    EntityNameRules.StudioIdentityKey(name),
                    name,
                    null))
                .ToArray();
            ThrowForPersistedIdentityConflicts(studios, existing);
        }
    }

    private List<EntityIdentityCandidate> NormalizeChangedPerformerNames(bool normalizeValues)
    {
        var candidates = new List<EntityIdentityCandidate>();
        foreach (var entry in ChangeTracker.Entries<Performer>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            var originalIdentityKey = entry.State == EntityState.Modified
                ? EntityNameRules.PerformerIdentityKey(
                    entry.Property(performer => performer.Name).OriginalValue,
                    entry.Property(performer => performer.Disambiguation).OriginalValue)
                : null;
            var normalizedName = EntityNameRules.NormalizeCanonicalName(entry.Entity.Name);
            var normalizedDisambiguation = EntityNameRules.NormalizeDisambiguation(entry.Entity.Disambiguation);
            if (normalizeValues)
            {
                entry.Entity.Name = normalizedName;
                entry.Entity.Disambiguation = normalizedDisambiguation;
            }
            var identityKey = EntityNameRules.PerformerIdentityKey(normalizedName, normalizedDisambiguation);
            entry.Entity.IdentityKey = identityKey;
            if (string.Equals(identityKey, originalIdentityKey, StringComparison.Ordinal))
                continue;
            candidates.Add(new EntityIdentityCandidate(
                NameConflictEntityTypes.Performer,
                identityKey,
                normalizedName,
                normalizedDisambiguation,
                entry.Entity.Id > 0 ? entry.Entity.Id : null));
        }

        return candidates;
    }

    private List<EntityIdentityCandidate> NormalizeChangedStudioNames(bool normalizeValues)
    {
        var candidates = new List<EntityIdentityCandidate>();
        foreach (var entry in ChangeTracker.Entries<Studio>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            var originalIdentityKey = entry.State == EntityState.Modified
                ? EntityNameRules.StudioIdentityKey(entry.Property(studio => studio.Name).OriginalValue)
                : null;
            var normalizedName = EntityNameRules.NormalizeCanonicalName(entry.Entity.Name);
            if (normalizeValues)
                entry.Entity.Name = normalizedName;
            var identityKey = EntityNameRules.StudioIdentityKey(normalizedName);
            entry.Entity.NameKey = identityKey;
            if (string.Equals(identityKey, originalIdentityKey, StringComparison.Ordinal))
                continue;
            candidates.Add(new EntityIdentityCandidate(
                NameConflictEntityTypes.Studio,
                identityKey,
                normalizedName,
                null,
                entry.Entity.Id > 0 ? entry.Entity.Id : null));
        }

        return candidates;
    }

    private static void ValidateTrackedIdentityCandidates(IReadOnlyCollection<EntityIdentityCandidate> candidates)
    {
        var duplicate = candidates
            .GroupBy(candidate => candidate.IdentityKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            var existing = duplicate.First();
            throw EntityNameConflictException.ForExistingIdentity(
                existing.EntityType,
                existing.Name,
                existing.Disambiguation);
        }
    }

    private static void ThrowForPersistedIdentityConflicts(
        IReadOnlyCollection<EntityIdentityCandidate> candidates,
        IReadOnlyCollection<EntityIdentityTarget> existingIdentities)
    {
        var existingByKey = existingIdentities
            .GroupBy(identity => identity.IdentityKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (existingByKey.TryGetValue(candidate.IdentityKey, out var existing))
            {
                throw EntityNameConflictException.ForExistingIdentity(
                    existing.EntityType,
                    existing.Name,
                    existing.Disambiguation);
            }
        }
    }

    private record EntityIdentityTarget(
        string EntityType,
        string IdentityKey,
        string Name,
        string? Disambiguation);

    private sealed record EntityIdentityCandidate(
        string EntityType,
        string IdentityKey,
        string Name,
        string? Disambiguation,
        int? EntityId);

    private sealed class EntityNameValidationScope(CoveContext context) : IDisposable
    {
        private CoveContext? _context = context;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _context, null);
            if (owner != null)
                owner._entityNameValidationSuppressionDepth--;
        }
    }
}
