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
        var cancellationToken = TestContext.Current.CancellationToken;
        var config = new CoveConfiguration
        {
            Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac" },
        };

        await using var setupDb = fixture.CreateContext();
        var userId = await setupDb.Users.AsNoTracking()
            .Where(user => user.Username == "perf-user")
            .Select(user => user.Id)
            .SingleAsync(cancellationToken);
        var setupTokens = CreateTokenService(setupDb, config);
        var rootPair = await setupTokens.IssueForUserAsync(userId, "127.0.0.1", "postgres-regression", cancellationToken);
        var activePair = await setupTokens.RefreshAsync(rootPair.RefreshToken, "127.0.0.1", "postgres-regression", cancellationToken);
        var rootId = await setupDb.RefreshTokens.AsNoTracking()
            .Where(token => token.TokenHash == TokenService.HashToken(rootPair.RefreshToken))
            .Select(token => token.Id)
            .SingleAsync(cancellationToken);
        var rootVersionBefore = await ReadRowVersionAsync(setupDb, rootId, cancellationToken);

        await using var firstDb = fixture.CreateContext();
        await using var secondDb = fixture.CreateContext();
        var firstTokens = CreateTokenService(firstDb, config);
        var secondTokens = CreateTokenService(secondDb, config);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = new[]
        {
            AttemptRefreshAsync(firstTokens, activePair.RefreshToken, start.Task, cancellationToken),
            AttemptRefreshAsync(secondTokens, activePair.RefreshToken, start.Task, cancellationToken),
        };
        start.SetResult();
        var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Single(results, result => result.Pair is not null);
        Assert.Single(results, result => result.Error is RefreshTokenConflictException);

        await using var verifyDb = fixture.CreateContext();
        var activeToken = await verifyDb.RefreshTokens.AsNoTracking()
            .Where(token => token.TokenHash == TokenService.HashToken(activePair.RefreshToken))
            .SingleAsync(cancellationToken);
        var children = await verifyDb.RefreshTokens.AsNoTracking()
            .Where(token => token.ParentId == activeToken.Id)
            .ToListAsync(cancellationToken);
        Assert.Single(children);
        Assert.Null(children[0].RevokedAt);
        Assert.Equal(rootVersionBefore, await ReadRowVersionAsync(verifyDb, rootId, cancellationToken));
    }

    [Fact]
    public async Task Older_ancestor_replay_waits_for_descendant_refresh_before_revoking_family()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var config = new CoveConfiguration
        {
            Auth = { JwtSecret = "test-secret-that-is-long-enough-for-hmac" },
        };

        await using var setupDb = fixture.CreateContext();
        var userId = await setupDb.Users.AsNoTracking()
            .Where(user => user.Username == "perf-user")
            .Select(user => user.Id)
            .SingleAsync(cancellationToken);
        var setupTokens = CreateTokenService(setupDb, config);
        var rootPair = await setupTokens.IssueForUserAsync(userId, "127.0.0.1", "postgres-regression", cancellationToken);
        var activePair = await setupTokens.RefreshAsync(rootPair.RefreshToken, "127.0.0.1", "postgres-regression", cancellationToken);
        var root = await setupDb.RefreshTokens
            .SingleAsync(token => token.TokenHash == TokenService.HashToken(rootPair.RefreshToken), cancellationToken);
        root.RevokedAt = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(1));
        await setupDb.SaveChangesAsync(cancellationToken);
        var activeId = await setupDb.RefreshTokens.AsNoTracking()
            .Where(token => token.TokenHash == TokenService.HashToken(activePair.RefreshToken))
            .Select(token => token.Id)
            .SingleAsync(cancellationToken);
        var rootVersionBefore = await ReadRowVersionAsync(setupDb, root.Id, cancellationToken);

        await using var blockerDb = fixture.CreateContext();
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync(cancellationToken);
        await blockerDb.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT "Id"
            FROM refresh_tokens
            WHERE "Id" = {activeId}
            FOR UPDATE
            """, cancellationToken);

        await using var refreshDb = fixture.CreateContext();
        await refreshDb.Database.OpenConnectionAsync(cancellationToken);
        var refreshBackendPid = await ReadBackendPidAsync(refreshDb, cancellationToken);
        var refreshTask = AttemptRefreshAsync(
            CreateTokenService(refreshDb, config), activePair.RefreshToken, Task.CompletedTask, cancellationToken);

        Task<RefreshAttempt>? replayTask = null;
        await using var replayDb = fixture.CreateContext();
        await replayDb.Database.OpenConnectionAsync(cancellationToken);
        var replayBackendPid = await ReadBackendPidAsync(replayDb, cancellationToken);
        try
        {
            await WaitForBlockedBackendAsync(setupDb, refreshBackendPid, cancellationToken);
            replayTask = AttemptRefreshAsync(
                CreateTokenService(replayDb, config), rootPair.RefreshToken, Task.CompletedTask, cancellationToken);
            await WaitForBlockedBackendAsync(setupDb, replayBackendPid, cancellationToken);
        }
        catch
        {
            await blockerTransaction.RollbackAsync(cancellationToken);
            await refreshTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (replayTask is not null)
                await replayTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            throw;
        }

        await blockerTransaction.RollbackAsync(cancellationToken);
        var refreshResult = await refreshTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var replayResult = await replayTask!.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        Assert.Null(refreshResult.Error);
        var replacementPair = Assert.IsType<TokenPair>(refreshResult.Pair);
        Assert.Null(replayResult.Pair);
        Assert.IsType<UnauthorizedException>(replayResult.Error);

        await using var verifyDb = fixture.CreateContext();
        var replacement = await verifyDb.RefreshTokens.AsNoTracking()
            .SingleAsync(token => token.TokenHash == TokenService.HashToken(replacementPair.RefreshToken), cancellationToken);
        Assert.Equal(activeId, replacement.ParentId);
        Assert.NotNull(replacement.RevokedAt);
        Assert.Null(await CreateTokenService(verifyDb, config)
            .ResolveAsync($"Bearer {replacementPair.AccessToken}", "127.0.0.1", "postgres-regression", cancellationToken));
        Assert.Equal(rootVersionBefore, await ReadRowVersionAsync(verifyDb, root.Id, cancellationToken));
    }

    private static TokenService CreateTokenService(CoveContext db, CoveConfiguration config)
        => new(db, config, new PermissionRegistry(), NullLogger<TokenService>.Instance);

    private static async Task<RefreshAttempt> AttemptRefreshAsync(
        TokenService tokens,
        string refreshToken,
        Task start,
        CancellationToken cancellationToken)
    {
        await start.WaitAsync(cancellationToken);
        try
        {
            return new RefreshAttempt(
                await tokens.RefreshAsync(refreshToken, "127.0.0.1", "postgres-regression", cancellationToken),
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RefreshAttempt(null, ex);
        }
    }

    private static Task<string> ReadRowVersionAsync(CoveContext db, Guid tokenId, CancellationToken cancellationToken)
        => db.Database.SqlQuery<string>($"""
                SELECT xmin::text AS "Value"
                FROM refresh_tokens
                WHERE "Id" = {tokenId}
                """)
            .SingleAsync(cancellationToken);

    private static Task<int> ReadBackendPidAsync(CoveContext db, CancellationToken cancellationToken)
        => db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"")
            .SingleAsync(cancellationToken);

    private static async Task WaitForBlockedBackendAsync(
        CoveContext observer,
        int backendPid,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var isBlocked = await observer.Database.SqlQuery<bool>($"""
                    SELECT cardinality(pg_blocking_pids({backendPid})) > 0 AS "Value"
                    """)
                .SingleAsync(cancellationToken);
            if (isBlocked) return;
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        throw new TimeoutException($"PostgreSQL backend {backendPid} did not block as expected.");
    }

    private sealed record RefreshAttempt(TokenPair? Pair, Exception? Error);
}
