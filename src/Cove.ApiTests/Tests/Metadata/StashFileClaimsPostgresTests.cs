using System.Data;
using System.Reflection;
using Cove.Api.Services;
using Cove.ApiTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cove.ApiTests.Tests.Metadata;

public sealed class StashFileClaimsPostgresTests(MetadataImportPostgresFixture fixture)
    : IClassFixture<MetadataImportPostgresFixture>
{
    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("error")]
    public async Task ClaimsUsePostgresTemporaryTablesAndCleanUp(string outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateContext();
        NpgsqlConnection connection;
        await using (var claims = await StashFileClaims.CreateAsync(db, StringComparer.Ordinal, ct))
        {
            connection = GetConnection(claims);
            Assert.True(await claims.AddAsync("folder/file", ct));
            await using var command = new NpgsqlCommand(
                "SELECT relpersistence::text FROM pg_class WHERE oid = 'pg_temp.cove_stash_file_claims'::regclass", connection);
            Assert.Equal("t", await command.ExecuteScalarAsync(ct));
            if (outcome == "cancel")
            {
                using var canceled = CancellationTokenSource.CreateLinkedTokenSource(ct);
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => claims.AddAsync("cancel", canceled.Token));
                Assert.True(await claims.AddAsync("cancel", ct));
            }
            if (outcome == "error")
                await Assert.ThrowsAsync<InvalidOperationException>(() => claims.SeedAsync(FailingValues(), ct));
        }
        Assert.Equal(ConnectionState.Closed, connection.State);
        // The dedicated session is not pooled: its temp relations must be removed too.
        await db.Database.OpenConnectionAsync(ct);
        await using var check = db.Database.GetDbConnection().CreateCommand();
        check.CommandText = "SELECT count(*) FROM pg_class WHERE relname = 'cove_stash_file_claims' AND relpersistence = 't'";
        Assert.Equal(0L, Convert.ToInt64(await check.ExecuteScalarAsync(ct)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSeedAndClaimsPreserveManagedEqualityAcrossHashCollisions(bool ignoreCase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateContext();
        var comparer = new CollidingComparer(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        await using var claims = await StashFileClaims.CreateAsync(db, comparer, ct);
        await claims.SeedAsync(Enumerable.Range(0, 601).Select(i => $"folder/seed{i}"), ct);
        await claims.SeedAsync(["folder/Café", "folder/Café", new string('x', 10000)], ct);
        Assert.False(await claims.AddAsync("folder/seed600", ct));
        Assert.False(await claims.AddAsync("folder/Café", ct));
        Assert.Equal(!ignoreCase, await claims.AddAsync("FOLDER/CAFÉ", ct));
        Assert.True(await claims.AddAsync("folder/other", ct));
        Assert.False(await claims.AddAsync("folder/other", ct));
        Assert.False(await claims.AddAsync(new string('x', 10000), ct));
    }

    [Fact]
    public async Task ClaimsSurviveDestinationRollbackAndAreIsolatedBetweenImports()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateContext();
        await db.Database.OpenConnectionAsync(ct);
        await using var claims = await StashFileClaims.CreateAsync(db, StringComparer.Ordinal, ct);
        await using var other = await StashFileClaims.CreateAsync(db, StringComparer.Ordinal, ct);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            Assert.True(await claims.AddAsync("failed-scene", ct));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT 1 / 0", ct));
            await transaction.RollbackAsync(ct);
        }
        Assert.False(await claims.AddAsync("failed-scene", ct));
        Assert.True(await other.AddAsync("failed-scene", ct));
    }

    [Fact]
    public async Task ClaimsConnectionLossDoesNotSilentlyForgetEarlierClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = fixture.CreateContext();
        await using var claims = await StashFileClaims.CreateAsync(db, StringComparer.Ordinal, ct);
        Assert.True(await claims.AddAsync("claimed", ct));
        var pid = GetConnection(claims).ProcessID;
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT pg_terminate_backend({pid})";
        await command.ExecuteScalarAsync(ct);
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => claims.AddAsync("claimed", ct));
    }

    private static NpgsqlConnection GetConnection(IStashFileClaims claims)
        => (NpgsqlConnection)typeof(StashFileClaims).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(claims)!;

    private static IEnumerable<string> FailingValues()
    {
        yield return "before-error";
        throw new InvalidOperationException("Injected seed failure");
    }

    private sealed class CollidingComparer(StringComparer inner) : StringComparer
    {
        public override int Compare(string? x, string? y) => inner.Compare(x, y);
        public override bool Equals(string? x, string? y) => inner.Equals(x, y);
        public override int GetHashCode(string obj) => 1;
    }
}
