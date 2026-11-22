using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cove.Data.Services;

/// <summary>
/// Runs <see cref="EmbeddingAnnIndexReconciler"/> shortly after startup and then periodically, so a library
/// that starts producing embeddings gets its ANN indexes without a restart or a manual step.
/// </summary>
public sealed class EmbeddingAnnIndexMaintenanceService(
    IServiceScopeFactory scopeFactory,
    EmbeddingAnnIndexReconciler reconciler,
    ILogger<EmbeddingAnnIndexMaintenanceService> logger) : BackgroundService
{
    // Starts after the other startup work so the first pass does not compete with it.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The wait between passes. A pass that finds nothing new reads one index entry per (Kind, Dim) pair,
    /// so this only bounds how soon a newly analysed family is picked up.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private bool _loggedWaitingForMigrations;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await IsDatabaseReadyAsync(stoppingToken))
                        await reconciler.ReconcileAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Embedding ANN index check failed; trying again in {Interval}", Interval);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> IsDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            if (!db.Database.IsNpgsql() || !await db.Database.CanConnectAsync(cancellationToken))
                return false;

            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                if (!_loggedWaitingForMigrations)
                {
                    logger.LogInformation("Embedding ANN index check is paused until pending database migrations are applied.");
                    _loggedWaitingForMigrations = true;
                }
                return false;
            }

            _loggedWaitingForMigrations = false;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Embedding ANN index check is waiting for the database to become ready.");
            return false;
        }
    }
}
