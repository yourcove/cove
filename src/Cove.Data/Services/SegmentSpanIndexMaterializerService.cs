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
    private readonly SegmentSpanIndexIdleChecks _idleChecks = new();

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
                if (processed == 0)
                    await signal.WaitAsync(IdleDelay, stoppingToken);
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

        // Looking for stale videos compares every video's version with its stored state, so an idle pass
        // would repeat that for every stored profile. A profile found clean is skipped until its rules or
        // some video's version change (see SegmentSpanIndexIdleChecks).
        var now = DateTime.UtcNow;
        var versionChanges = await index.ReadVideoVersionChangeCounterAsync(ct);
        _idleChecks.ObserveVersionChanges(versionChanges, now);
        var profileVersions = await db.SegmentDisplayProfiles.AsNoTracking()
            .Where(profile => profileIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, profile => profile.Version, ct);

        var processed = 0;
        foreach (var profileId in profileIds)
        {
            if (!profileVersions.TryGetValue(profileId, out var profileVersion))
                continue;
            if (!_idleChecks.NeedsScan(profileId, profileVersion, now))
                continue;

            try
            {
                var reconciled = await index.ReconcileBatchAsync(profileId, SegmentSpanIndexService.BatchSize, ct, SettleDelay);
                processed += reconciled;
                if (reconciled == 0)
                    _idleChecks.RecordClean(profileId, profileVersion, now);
                else
                    _idleChecks.Forget(profileId);
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

/// <summary>
/// Decides when the background pass may skip a stored profile's search for stale videos. That search
/// grows with the library, and every profile ever opened stays stored, so repeating it every few seconds
/// for each one would keep a large library's database busy while nothing changes. A profile found clean
/// is skipped while its version and PostgreSQL's change counter for the video versions both stay put.
/// The counter is a statistic, updated a moment after a commit and not at all if statistics are off, so
/// a clean result only counts once the counter has been still for longer than the settle delay (a video
/// still settling would otherwise be missed), and every profile is searched again at least once a
/// minute regardless. Requests never depend on this: they bring their profile up to date themselves.
/// </summary>
internal sealed class SegmentSpanIndexIdleChecks
{
    internal static readonly TimeSpan MaxCleanAge = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan QuietBeforeTrusted = SegmentSpanIndexMaterializerService.SettleDelay + TimeSpan.FromSeconds(2);

    private readonly Dictionary<int, (int ProfileVersion, long? Changes, DateTime At)> _clean = new();
    private long? _changes;
    private DateTime _changesSeenAt = DateTime.MinValue;

    public void ObserveVersionChanges(long? changes, DateTime now)
    {
        if (changes == _changes && _changesSeenAt != DateTime.MinValue)
            return;

        _changes = changes;
        _changesSeenAt = now;
    }

    public bool NeedsScan(int profileId, int profileVersion, DateTime now)
        => _changes is null
           || !_clean.TryGetValue(profileId, out var clean)
           || clean.ProfileVersion != profileVersion
           || clean.Changes != _changes
           || now - clean.At >= MaxCleanAge;

    public void RecordClean(int profileId, int profileVersion, DateTime now)
    {
        // A counter that moved moments ago may belong to a video that is still settling.
        if (_changes is null || now - _changesSeenAt < QuietBeforeTrusted)
        {
            _clean.Remove(profileId);
            return;
        }

        _clean[profileId] = (profileVersion, _changes, now);
    }

    public void Forget(int profileId) => _clean.Remove(profileId);
}
