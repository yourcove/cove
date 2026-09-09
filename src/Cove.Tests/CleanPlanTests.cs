using Cove.Api.Services;

namespace Cove.Tests;

public sealed class CleanPlanTests
{
    [Fact]
    public async Task BatchesAndDeduplicatesCandidatesWithoutMixingKinds()
    {
        await using var plan = await CleanPlan.CreateAsync(TestContext.Current.CancellationToken);
        await plan.AddAsync("files", Enumerable.Range(1, 601), TestContext.Current.CancellationToken);
        await plan.AddAsync("files", [1, 2], TestContext.Current.CancellationToken);
        await plan.AddAsync("video", [1], TestContext.Current.CancellationToken);
        Assert.Equal(601, await plan.CountAsync("files", TestContext.Current.CancellationToken));
        Assert.Equal(1, await plan.CountAsync("video", TestContext.Current.CancellationToken));
        var count = 0;
        await foreach (var batch in plan.ReadAsync("files", TestContext.Current.CancellationToken))
        {
            Assert.InRange(batch.Length, 1, CleanPlan.BatchSize);
            foreach (var id in batch) Assert.Equal(++count, id);
        }
        Assert.Equal(601, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledStagingRollsBackAndRemovesTemporaryFiles(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cove-clean-plan-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var plan = await CleanPlan.CreateAsync(TestContext.Current.CancellationToken, directory))
            {
                if (cancel)
                {
                    using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                    canceled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plan.AddAsync("files", [1], canceled.Token));
                }
                else
                    await Assert.ThrowsAsync<InvalidOperationException>(() => plan.AddAsync("files", FailingIds(), TestContext.Current.CancellationToken));
                Assert.Equal(0, await plan.CountAsync("files", TestContext.Current.CancellationToken));
                await foreach (var batch in plan.ReadAsync("files", TestContext.Current.CancellationToken))
                    Assert.Fail("A failed staging transaction retained candidate IDs.");
            }
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static IEnumerable<int> FailingIds()
    {
        yield return 1;
        throw new InvalidOperationException("Injected detection failure");
    }
}
