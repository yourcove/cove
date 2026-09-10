using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

internal static class MetadataImportRunner
{
    internal static async Task RunAsync(IServiceScopeFactory scopeFactory, Stream input,
        Func<CoveContext, MetadataImportStage, CancellationToken, Task> apply, CancellationToken ct)
    {
        if (!input.CanSeek)
            throw new ArgumentException("Metadata import requires replayable input.", nameof(input));
        using var strategyScope = scopeFactory.CreateScope();
        var strategy = strategyScope.ServiceProvider.GetRequiredService<CoveContext>().Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            ct.ThrowIfCancellationRequested();
            input.Position = 0;
            // A failed connection loses its temporary tables. Each attempt owns a fresh
            // context, connection and transaction, and rebuilds everything from the same file.
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await db.Database.OpenConnectionAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var stage = await MetadataImportStage.CreateAsync(db, input, token);
            await stage.BuildTargetIndexAsync(db, token);
            await apply(db, stage, token);
            await transaction.CommitAsync(token);
        }, ct);
    }
}
