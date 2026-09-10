using Cove.Api.Services;

namespace Cove.Tests;

public sealed class GenerationWorkSetTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("error")]
    public async Task BatchesIdsAndRemovesTemporaryStateOnEveryExit(string outcome)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cove-generation-stage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var work = await GenerationWorkSet.CreateAsync(TestContext.Current.CancellationToken, directory))
            {
                await work.AddAsync(Enumerable.Range(1, 601), TestContext.Current.CancellationToken);
                await work.AddAsync([1, 2], TestContext.Current.CancellationToken);
                Assert.Equal(601, work.Count);
                var read = 0;
                await foreach (var batch in work.ReadAsync(TestContext.Current.CancellationToken))
                {
                    Assert.InRange(batch.Length, 1, GenerationSelection.BatchSize);
                    foreach (var id in batch)
                        Assert.Equal(++read, id);
                }
                Assert.Equal(601, read);
                Assert.NotEmpty(Directory.EnumerateFiles(directory));
                if (outcome == "cancel")
                {
                    using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                    canceled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.AddAsync([602], canceled.Token));
                }
                else if (outcome == "error")
                    await Assert.ThrowsAsync<InvalidOperationException>(() => work.AddAsync(FailingIds(), TestContext.Current.CancellationToken));
                Assert.Equal(601, work.Count);
            }
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<int> FailingIds()
    {
        yield return 602;
        throw new InvalidOperationException("Injected selection failure");
    }
}
