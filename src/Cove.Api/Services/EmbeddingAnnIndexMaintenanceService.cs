using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Runs <see cref="EmbeddingAnnIndexReconciler"/> shortly after startup and then periodically, so a library
/// that starts producing embeddings gets its ANN indexes without a restart or a manual step.
/// </summary>
/// <remarks>
/// This is a hosted service rather than a job: the rows that make an index worth building are written
/// by analysis jobs that run for hours, and an exclusive job would wait behind them. A pass that finds
/// nothing to do reads one index entry per (Kind, Dim) pair.
/// </remarks>
internal sealed class EmbeddingAnnIndexMaintenanceService(
    IServiceScopeFactory scopeFactory,
    EmbeddingAnnIndexReconciler reconciler,
    MaintenanceState maintenance,
    ILogger<EmbeddingAnnIndexMaintenanceService> logger) : BackgroundService
{
    // Lets the rest of startup finish before the first pass.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The wait between passes: how soon a newly analysed family is picked up. Without its index a family's
    /// searches stay fast until it has grown for days, so a few hours' delay costs nothing users notice.
    /// It matches <see cref="EmbeddingAnnIndexReconciler.RetryFailedBuildAfter"/>.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(6);

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
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Embedding ANN index check stopped early; trying again in {Interval}", Interval);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the database may already be gone, so whatever was thrown is expected.
        }
    }

    private async Task<bool> IsDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        // A restore drops and rebuilds the schema; a build started now could collide with the dump's own indexes.
        if (maintenance.IsSchemaUnavailable)
            return false;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            if (!db.Database.IsNpgsql() || !await db.Database.CanConnectAsync(cancellationToken))
                return false;

            // Pending migrations exist only from startup until an administrator applies them, so a build
            // never starts while a migration might need the table.
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
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Embedding ANN index check is waiting for the database to become ready.");
            return false;
        }
    }
}
