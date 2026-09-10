using Cove.ApiTests.Infrastructure;
using System.Text;
using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cove.ApiTests.Tests.Metadata;

public sealed class MetadataImportRunnerTests(MetadataImportPostgresFixture fixture) : IClassFixture<MetadataImportPostgresFixture>
{
    [Fact]
    public async Task ConnectionLossReplaysInputAndRollsBackSavedRows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var services = new ServiceCollection().AddScoped(_ => fixture.CreateContext(retry: true)).BuildServiceProvider();
        var names = Enumerable.Range(0, 300).Select(i => $"retry {Guid.NewGuid():N} {i}").ToArray();
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { tags = names.Select(name => new { name }) }));
        var attempts = 0;
        var sessions = new List<int>();
        await MetadataImportRunner.RunAsync(services.GetRequiredService<IServiceScopeFactory>(), input, async (db, stage, token) =>
        {
            attempts++;
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            sessions.Add(connection.ProcessID);
            Assert.Empty(await stage.FindTargetIdsAsync("tags", names, token));
            var count = 0;
            await foreach (var batch in stage.ReadBatchesAsync<Tag>("tags", token))
            {
                db.Tags.AddRange(batch.Select(tag => new Tag { Name = tag.Name }));
                await db.SaveChangesAsync(token);
                await stage.AddTagsAsync(db.ChangeTracker.Entries<Tag>().Select(entry => entry.Entity), token);
                count += batch.Count;
                db.ChangeTracker.Clear();
                if (attempts == 1)
                {
                    // Terminate the real backend after a batch has been saved, before commit.
                    await using var killer = new NpgsqlConnection(fixture.ConnectionString);
                    await killer.OpenAsync(token);
                    await using var command = new NpgsqlCommand("SELECT pg_terminate_backend($1)", killer);
                    command.Parameters.AddWithValue(connection.ProcessID);
                    Assert.True((bool)(await command.ExecuteScalarAsync(token))!);
                    await db.Database.ExecuteSqlRawAsync("SELECT 1", token);
                    Assert.Fail("The terminated connection must fail.");
                }
            }
            Assert.Equal(names.Length, count);
        }, ct);
        Assert.Equal(2, attempts);
        Assert.Equal(2, sessions.Distinct().Count());
        await using var verify = fixture.CreateContext();
        Assert.Equal(names.Length, await verify.Tags.CountAsync(tag => names.Contains(tag.Name), ct));
    }

    [Fact]
    public async Task CancellationRollsBackSavedBatch()
    {
        using var services = new ServiceCollection().AddScoped(_ => fixture.CreateContext(retry: true)).BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var name = $"cancel {Guid.NewGuid():N}";
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { tags = new[] { new { name } } }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MetadataImportRunner.RunAsync(
            services.GetRequiredService<IServiceScopeFactory>(), input, async (db, stage, token) =>
            {
                await foreach (var batch in stage.ReadBatchesAsync<Tag>("tags", token))
                {
                    db.Tags.AddRange(batch);
                    await db.SaveChangesAsync(token);
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
            }, cancellation.Token));
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Tags.AnyAsync(tag => tag.Name == name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentAttemptsHaveIsolatedTemporaryTables()
    {
        using var services = new ServiceCollection().AddScoped(_ => fixture.CreateContext(retry: true)).BuildServiceProvider();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        async Task Run(string name)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"tags\":[{{\"name\":\"{name}\"}}]}}"));
            await MetadataImportRunner.RunAsync(services.GetRequiredService<IServiceScopeFactory>(), input, async (db, stage, token) =>
            {
                if (Interlocked.Increment(ref count) == 2) ready.SetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                await foreach (var batch in stage.ReadBatchesAsync<Tag>("tags", token))
                    Assert.Equal(name, Assert.Single(batch).Name);
            }, TestContext.Current.CancellationToken);
        }
        await Task.WhenAll(Run("first session"), Run("second session"));
    }
}
