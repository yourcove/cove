using Cove.Core.Interfaces;
using Cove.Core.Entities;
using Cove.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cove.Api.Services;

/// <summary>
/// Host-side <see cref="IReferencePerformerImporter"/>. Opens its own DI scope so it is safe to hold as
/// a singleton and to call from an extension container, then enriches the performer from the matching
/// metadata server via <see cref="MetadataServerService"/>. Any failure (no server configured for the
/// endpoint, network error, deleted remote performer) is swallowed and reported as <c>false</c> so the
/// caller keeps the performer with just its recorded remote id.
/// </summary>
public sealed class ReferencePerformerImporter(IServiceScopeFactory scopeFactory, ILogger<ReferencePerformerImporter>? logger = null)
    : IReferencePerformerImporter
{
    public async Task<bool> TryImportAsync(int performerId, string endpoint, string externalId, bool importMetadata = true, CancellationToken cancellationToken = default)
    {
        if (performerId <= 0 || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(externalId))
            return false;

        endpoint = endpoint.Trim();
        externalId = externalId.Trim();

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<CoveContext>();
            if (db is null)
                return false;

            var performer = await db.Performers
                .Include(p => p.RemoteIds)
                .Include(p => p.Aliases)
                .Include(p => p.Urls)
                .FirstOrDefaultAsync(p => p.Id == performerId, cancellationToken);
            if (performer is null)
                return false;

            var existingRemoteId = performer.RemoteIds.FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
            if (existingRemoteId is null)
            {
                performer.RemoteIds.Add(new PerformerRemoteId
                {
                    PerformerId = performer.Id,
                    Endpoint = endpoint,
                    RemoteId = externalId,
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (!string.Equals(existingRemoteId.RemoteId, externalId, StringComparison.OrdinalIgnoreCase))
            {
                existingRemoteId.RemoteId = externalId;
                await db.SaveChangesAsync(cancellationToken);
            }

            // The remote id is now recorded. When the caller opted out of scraping (the "Update existing
            // performers from metadata servers" setting is off), stop here without touching the server.
            if (!importMetadata)
                return false;

            var metadataServer = scope.ServiceProvider.GetService<MetadataServerService>();
            if (metadataServer is null)
                return false;

            var match = await metadataServer.GetPerformerMatchAsync(endpoint, externalId, cancellationToken);
            if (match is null)
                return false;

            var imported = await metadataServer.MergePerformerAsync(performer, endpoint, externalId, cancellationToken);
            if (!imported)
                return false;

            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Reference performer metadata import failed for performer {PerformerId} from {Endpoint}/{ExternalId}", performerId, endpoint, externalId);
            return false;
        }
    }
}
