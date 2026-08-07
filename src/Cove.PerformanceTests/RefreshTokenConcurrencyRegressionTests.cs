using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Auth;
using Cove.PerformanceTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.PerformanceTests;

[Collection("performance")]
public sealed class RefreshTokenConcurrencyRegressionTests(PostgresPerformanceFixture fixture)
{
    [Fact]
    public async Task Concurrent_descendant_refreshes_serialize_without_rewriting_family_root()
    {
        var config = new CoveConfiguration
        {
            Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac" },
        };

        await using var setupDb = fixture.CreateContext();
        var userId = await setupDb.Users.AsNoTracking()
            .Where(user => user.Username == "perf-user")
            .Select(user => user.Id)
            .SingleAsync();
        var setupTokens = CreateTokenService(setupDb, config);
        var rootPair = await setupTokens.IssueForUserAsync(userId, "127.0.0.1", "postgres-regression");
        var activePair = await setupTokens.RefreshAsync(rootPair.RefreshToken, "127.0.0.1", "postgres-regression");
        var rootId = await setupDb.RefreshTokens.AsNoTracking()
            .Where(token => token.TokenHash == TokenService.HashToken(rootPair.RefreshToken))
            .Select(token => token.Id)
            .SingleAsync();
        var rootVersionBefore = await ReadRowVersionAsync(setupDb, rootId);

        await using var firstDb = fixture.CreateContext();
        await using var secondDb = fixture.CreateContext();
        var firstTokens = CreateTokenService(firstDb, config);
        var secondTokens = CreateTokenService(secondDb, config);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = new[]
        {
            AttemptRefreshAsync(firstTokens, activePair.RefreshToken, start.Task),
            AttemptRefreshAsync(secondTokens, activePair.RefreshToken, start.Task),
        };
        start.SetResult();
        var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(results, result => result.Pair is not null);
        Assert.Single(results, result => result.Error is RefreshTokenConflictException);

        await using var verifyDb = fixture.CreateContext();
        var activeToken = await verifyDb.RefreshTokens.AsNoTracking()
            .Where(token => token.TokenHash == TokenService.HashToken(activePair.RefreshToken))
            .SingleAsync();
        var children = await verifyDb.RefreshTokens.AsNoTracking()
            .Where(token => token.ParentId == activeToken.Id)
            .ToListAsync();
        Assert.Single(children);
        Assert.Null(children[0].RevokedAt);
        Assert.Equal(rootVersionBefore, await ReadRowVersionAsync(verifyDb, rootId));
    }

    [Fact]
    public async Task Older_ancestor_replay_waits_for_descendant_refresh_before_revoking_family()
    {
        var config = new CoveConfiguration
        {
            Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac" },
        };

        await using var setupDb = fixture.CreateContext();
        var userId = await setupDb.Users.AsNoTracking()
            .Where(user => user.Username == "perf-user")
            .Select(user => user.Id)
            .SingleAsync();
        var setupTokens = CreateTokenService(setupDb, config);
        var rootPair = await setupTokens.IssueForUserAsync(userId, "127.0.0.1", "postgres-regression");
        var activePair = await setupTokens.RefreshAsync(rootPair.RefreshToken, "127.0.0.1", "postgres-regression");
        var root = await setupDb.RefreshTokens
            .SingleAsync(token => token.TokenHash == TokenService.HashToken(rootPair.RefreshToken));
        root.RevokedAt = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(1));
        await setupDb.SaveChangesAsync();
        var activeId = await setupDb.RefreshTokens.AsNoTracking()
            .Where(token => token.TokenHash == TokenService.HashToken(activePair.RefreshToken))
            .Select(token => token.Id)
            .SingleAsync();
        var rootVersionBefore = await ReadRowVersionAsync(setupDb, root.Id);

        await using var blockerDb = fixture.CreateContext();
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync();
        await blockerDb.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT "Id"
            FROM refresh_tokens
            WHERE "Id" = {activeId}
            FOR UPDATE
            """);

        await using var refreshDb = fixture.CreateContext();
        await refreshDb.Database.OpenConnectionAsync();
        var refreshBackendPid = await ReadBackendPidAsync(refreshDb);
        var refreshTask = AttemptRefreshAsync(
            CreateTokenService(refreshDb, config), activePair.RefreshToken, Task.CompletedTask);

        Task<RefreshAttempt>? replayTask = null;
        await using var replayDb = fixture.CreateContext();
        await replayDb.Database.OpenConnectionAsync();
        var replayBackendPid = await ReadBackendPidAsync(replayDb);
        try
        {
            await WaitForBlockedBackendAsync(setupDb, refreshBackendPid);
            replayTask = AttemptRefreshAsync(
                CreateTokenService(replayDb, config), rootPair.RefreshToken, Task.CompletedTask);
            await WaitForBlockedBackendAsync(setupDb, replayBackendPid);
        }
        catch
        {
            await blockerTransaction.RollbackAsync();
            await refreshTask.WaitAsync(TimeSpan.FromSeconds(10));
            if (replayTask is not null)
                await replayTask.WaitAsync(TimeSpan.FromSeconds(10));
            throw;
        }

        await blockerTransaction.RollbackAsync();
        var refreshResult = await refreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        var replayResult = await replayTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(refreshResult.Error);
        var replacementPair = Assert.IsType<TokenPair>(refreshResult.Pair);
        Assert.Null(replayResult.Pair);
        Assert.IsType<UnauthorizedException>(replayResult.Error);

        await using var verifyDb = fixture.CreateContext();
        var replacement = await verifyDb.RefreshTokens.AsNoTracking()
            .SingleAsync(token => token.TokenHash == TokenService.HashToken(replacementPair.RefreshToken));
        Assert.Equal(activeId, replacement.ParentId);
        Assert.NotNull(replacement.RevokedAt);
        Assert.Null(await CreateTokenService(verifyDb, config)
            .ResolveAsync($"Bearer {replacementPair.AccessToken}", "127.0.0.1", "postgres-regression"));
        Assert.Equal(rootVersionBefore, await ReadRowVersionAsync(verifyDb, root.Id));
    }

    private static TokenService CreateTokenService(CoveContext db, CoveConfiguration config)
        => new(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);

    private static async Task<RefreshAttempt> AttemptRefreshAsync(
        TokenService tokens,
        string refreshToken,
        Task start)
    {
        await start;
        try
        {
            return new RefreshAttempt(
                await tokens.RefreshAsync(refreshToken, "127.0.0.1", "postgres-regression"),
                null);
        }
        catch (Exception ex)
        {
            return new RefreshAttempt(null, ex);
        }
    }

    private static Task<string> ReadRowVersionAsync(CoveContext db, Guid tokenId)
        => db.Database.SqlQuery<string>($"""
                SELECT xmin::text AS "Value"
                FROM refresh_tokens
                WHERE "Id" = {tokenId}
                """)
            .SingleAsync();

    private static Task<int> ReadBackendPidAsync(CoveContext db)
        => db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync();

    private static async Task WaitForBlockedBackendAsync(CoveContext observer, int backendPid)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var isBlocked = await observer.Database.SqlQuery<bool>($"""
                    SELECT cardinality(pg_blocking_pids({backendPid})) > 0 AS "Value"
                    """)
                .SingleAsync();
            if (isBlocked) return;
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        throw new TimeoutException($"PostgreSQL backend {backendPid} did not block as expected.");
    }

    private sealed record RefreshAttempt(TokenPair? Pair, Exception? Error);
}
