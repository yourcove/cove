using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests.Postgres;

[RequiresPostgres]
public sealed class CovePostgresDatabaseTests
{
    [Fact]
    public async Task NewDatabase_HasEveryMigrationAndTheSqlFunctionsTheyCreate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = database.CreateContext();

        Assert.True(db.Database.IsNpgsql());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
        Assert.Equal(
            db.Database.GetMigrations(),
            await db.Database.GetAppliedMigrationsAsync(ct));
        var functions = await db.Database
            .SqlQuery<string>($"SELECT proname AS \"Value\" FROM pg_proc WHERE proname = 'cove_authz_can_read'")
            .ToListAsync(ct);
        Assert.Single(functions);
    }

    [Fact]
    public async Task RetryOnFailure_KeepsTheVectorMapping()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var db = database.CreateContext(options =>
            options.UseNpgsql(database.DataSource, npgsql => npgsql.EnableRetryOnFailure()));

        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);
        // The model maps pgvector columns, so it only builds while the vector plugin is still configured.
        Assert.Equal(0, await db.Tags.CountAsync(ct));
    }

    [Fact]
    public async Task Databases_DoNotShareRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var first = await CovePostgresDatabase.CreateAsync(ct);
        await using var second = await CovePostgresDatabase.CreateAsync(ct);

        await using (var db = first.CreateContext())
        {
            db.Tags.Add(new Tag { Name = "Only in the first database" });
            await db.SaveChangesAsync(ct);
        }

        await using var firstDb = first.CreateContext();
        await using var secondDb = second.CreateContext();
        Assert.Equal(1, await firstDb.Tags.CountAsync(ct));
        Assert.Equal(0, await secondDb.Tags.CountAsync(ct));
    }

    [Fact]
    public async Task DisposedDatabase_IsDroppedInTheBackground()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await CovePostgresDatabase.CreateAsync(ct);
        string name;
        try
        {
            await using var db = database.CreateContext();
            await db.Database.OpenConnectionAsync(ct);
            name = db.Database.GetDbConnection().Database;
        }
        finally
        {
            await database.DisposeAsync();
        }

        // DeferredDatabaseDrops drops it in the background, possibly behind other tests' databases.
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (await PostgreSqlTestDatabase.ExistsAsync(name, ct) && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);

        Assert.False(await PostgreSqlTestDatabase.ExistsAsync(name, ct));
    }
}

[RequiresPostgres]
public sealed class CovePostgresClassFixtureTests(CovePostgresDatabase database) : IClassFixture<CovePostgresDatabase>
{
    [Fact]
    public async Task ClassFixture_IsInitializedBeforeTheTestRuns()
    {
        await using var db = database.CreateContext();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }
}
