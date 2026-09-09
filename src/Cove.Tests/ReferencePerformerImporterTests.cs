using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

public sealed class ReferencePerformerImporterTests
{
    [Fact]
    public async Task TryImportAsync_RecordsRemoteIdForExistingPerformerBeforeHydration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        services.AddDbContext<CoveContext>(options => options.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();

        int performerId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            var performer = new Performer { Name = "Existing Performer" };
            db.Performers.Add(performer);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            performerId = performer.Id;
        }

        var importer = new ReferencePerformerImporter(provider.GetRequiredService<IServiceScopeFactory>());

        var imported = await importer.TryImportAsync(performerId, "https://metadata.example", "remote-123", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(imported);
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CoveContext>();
        var performerWithRemoteIds = await verifyDb.Performers
            .Include(performer => performer.RemoteIds)
            .SingleAsync(performer => performer.Id == performerId, cancellationToken: TestContext.Current.CancellationToken);
        var remoteId = Assert.Single(performerWithRemoteIds.RemoteIds);
        Assert.Equal("https://metadata.example", remoteId.Endpoint);
        Assert.Equal("remote-123", remoteId.RemoteId);
    }
}
