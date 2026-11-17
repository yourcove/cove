using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cove.Data.Services;

/// <summary>
/// Builds and refreshes the stored span index off the request path. It registers the default display
/// profiles, then repeatedly re-resolves a batch of stale videos for every indexed profile and idles
/// when none are left. Requests refresh the few videos they need themselves
/// (<see cref="SegmentSpanIndexService.EnsureFreshAsync"/>); this service does the first build after an
/// upgrade, rebuilds after a profile's rules change and absorbs bulk imports.
/// </summary>
public sealed class SegmentSpanIndexMaterializerService(
    IServiceScopeFactory scopeFactory,
    SegmentSpanIndexSignal signal,
    ILogger<SegmentSpanIndexMaterializerService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest wait between passes that find nothing to do. Looking for stale videos compares every
    /// video's version with its stored state for each stored profile, so an idle server backs off from
    /// <see cref="IdleDelay"/> to this. A profile registered or saved, or a request that leaves work
    /// pending, wakes the service at once; a write made outside the app is picked up within this long,
    /// and any list request brings its own profile up to date regardless.
    /// </summary>
    internal static readonly TimeSpan MaxIdleDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A video whose segments changed this recently is left alone, so a detection run that writes a
    /// video's segments one statement at a time is resolved once at the end rather than once per write.
    /// </summary>
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Videos stored between refreshes of the planner's statistics during a build
    /// (<see cref="SegmentSpanIndexService.RefreshStatisticsAsync"/>). A build that goes idle with at
    /// least a batch stored since the last refresh refreshes them once more.
    /// </summary>
    internal const int StatisticsRefreshVideos = 5000;

    private bool _loggedWaitingForMigrations;
    private int _storedSinceStatisticsRefresh;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var idleDelay = IdleDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await IsDatabaseReadyAsync(stoppingToken))
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                var processed = await RunPassAsync(stoppingToken);
                if (processed > 0)
                {
                    idleDelay = IdleDelay;
                    continue;
                }

                // Nothing to do: wait longer each time, until woken or the next long check is due.
                idleDelay = await signal.WaitAsync(idleDelay, stoppingToken)
                    ? IdleDelay
                    : NextIdleDelay(idleDelay);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Segment span index pass failed; backing off.");
                try
                {
                    await Task.Delay(ErrorDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>The wait after another pass that found nothing: twice the last, up to <see cref="MaxIdleDelay"/>.</summary>
    internal static TimeSpan NextIdleDelay(TimeSpan current)
        => current * 2 < MaxIdleDelay ? current * 2 : MaxIdleDelay;

    private async Task<int> RunPassAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var index = scope.ServiceProvider.GetRequiredService<SegmentSpanIndexService>();
        var resolver = scope.ServiceProvider.GetRequiredService<SegmentSpanResolver>();

        // Creates the built-in profiles on a fresh database, so there is always a default to index.
        await resolver.EnsureDefaultProfileAsync(null, ct);
        var defaultProfileIds = await db.SegmentDisplayProfiles.AsNoTracking()
            // Only the shared default is built ahead of use; a user's own profile is indexed when listed.
            .Where(profile => profile.IsDefault && profile.UserId == null)
            .Select(profile => profile.Id)
            .ToListAsync(ct);
        foreach (var profileId in defaultProfileIds)
            await index.RegisterProfileAsync(profileId, ct);

        var profileIds = await db.SegmentSpanIndexedProfiles.AsNoTracking()
            .OrderBy(profile => profile.ProfileId)
            .Select(profile => profile.ProfileId)
            .ToListAsync(ct);

        var processed = 0;
        foreach (var profileId in profileIds)
        {
            try
            {
                processed += await index.ReconcileBatchAsync(profileId, SegmentSpanIndexService.BatchSize, ct, SettleDelay);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One profile's trouble must not keep the others from being indexed.
                logger.LogError(exception, "Storing spans for display profile {ProfileId} failed; continuing with the other profiles.", profileId);
            }
        }

        _storedSinceStatisticsRefresh += processed;
        if (_storedSinceStatisticsRefresh >= StatisticsRefreshVideos
            || (processed == 0 && _storedSinceStatisticsRefresh >= SegmentSpanIndexService.BatchSize))
        {
            _storedSinceStatisticsRefresh = 0;
            try
            {
                await index.RefreshStatisticsAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Only slower: autovacuum refreshes them in time.
                logger.LogWarning(exception, "Refreshing the planner statistics of the stored spans failed.");
            }
        }

        return processed;
    }

    private async Task<bool> IsDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            if (!await db.Database.CanConnectAsync(cancellationToken))
                return false;

            var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            if (pendingMigrations.Any())
            {
                if (!_loggedWaitingForMigrations)
                {
                    logger.LogInformation("Segment span index materializer is paused until pending database migrations are applied.");
                    _loggedWaitingForMigrations = true;
                }

                return false;
            }

            _loggedWaitingForMigrations = false;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Segment span index materializer is waiting for the database to become ready.");
            return false;
        }
    }
}
