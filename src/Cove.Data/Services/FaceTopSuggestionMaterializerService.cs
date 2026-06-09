using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cove.Data.Services;

/// <summary>
/// Drains the face top-suggestion backlog off the request path. It repeatedly picks up a batch of
/// unlinked faces whose projection is missing or has been invalidated
/// (<c>TopSuggestionComputedAt == null</c>), recomputes them via <see cref="FaceTopSuggestionService"/>,
/// and idles when there is nothing to do. Invalidation triggers (link/accept/reject, reference-pack
/// import, new faces) simply null the stamp; this service does the heavy compute that the faces list
/// no longer does on the request thread.
/// </summary>
public sealed class FaceTopSuggestionMaterializerService(
    IServiceScopeFactory scopeFactory,
    ILogger<FaceTopSuggestionMaterializerService> logger) : BackgroundService
{
    private const int BatchSize = 200;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);
    private bool _loggedWaitingForMigrations;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish booting (extensions publish their suggesters during startup) before the
        // first pass, so we don't stamp every face "computed, no suggestion" before the AI.Faces
        // suggester is available.
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

                int processed;
                using (var scope = scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                    var service = scope.ServiceProvider.GetRequiredService<FaceTopSuggestionService>();

                    var batch = await db.Faces
                        .AsNoTracking()
                        .Where(face => face.PerformerId == null
                            && face.MergedIntoFaceId == null
                            && face.TopSuggestionComputedAt == null)
                        .OrderBy(face => face.Id)
                        .Select(face => face.Id)
                        .Take(BatchSize)
                        .ToListAsync(stoppingToken);

                    processed = batch.Count == 0 ? 0 : await service.MaterializeAsync(batch, stoppingToken);
                }

                if (processed == 0)
                    await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Face top-suggestion materializer batch failed; backing off.");
                await Task.Delay(ErrorDelay, stoppingToken);
            }
        }
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
                    logger.LogInformation("Face top-suggestion materializer is paused until pending database migrations are applied.");
                    _loggedWaitingForMigrations = true;
                }

                return false;
            }

            _loggedWaitingForMigrations = false;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Face top-suggestion materializer is waiting for the database to become ready.");
            return false;
        }
    }
}
