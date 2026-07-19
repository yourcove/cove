using System.Diagnostics;

namespace Cove.Plugins;

/// <summary>
/// Executes a namespaced contribution over bounded batches without imposing a global candidate
/// ceiling. One deadline and one stable provider revision apply to the complete execution.
/// </summary>
internal sealed class ExtensionContributionBatchExecutor
{
    public const int DefaultBatchSize = 256;
    public const int DefaultRevisionLengthLimit = 256;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly int _batchSize;
    private readonly int _revisionLengthLimit;

    public ExtensionContributionBatchExecutor(
        int batchSize = DefaultBatchSize,
        int revisionLengthLimit = DefaultRevisionLengthLimit)
    {
        _batchSize = Math.Clamp(batchSize, 1, 4_096);
        _revisionLengthLimit = Math.Clamp(revisionLengthLimit, 1, 4_096);
    }

    public async Task<IReadOnlyList<TResult>> ExecuteAsync<TCandidate, TRequest, TResult>(
        IReadOnlyList<TCandidate> candidates,
        Func<TRequest, CancellationToken, Task<TResult>> execute,
        Func<IReadOnlyList<TCandidate>, TRequest> createRequest,
        Func<TResult, string?> getRevision,
        Action<IReadOnlyList<TCandidate>, TResult> validateResult,
        TimeSpan timeout,
        CancellationToken providerCancellation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(createRequest);
        ArgumentNullException.ThrowIfNull(getRevision);
        ArgumentNullException.ThrowIfNull(validateResult);

        if (timeout <= TimeSpan.Zero)
            throw new ExtensionContributionTimeoutException();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(providerCancellation, ct);
        deadline.CancelAfter(timeout);
        var results = new List<TResult>();
        var startedAt = Stopwatch.GetTimestamp();
        string? stableRevision = null;

        foreach (var batch in candidates.Chunk(_batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
                throw new ExtensionContributionTimeoutException();

            var request = createRequest(batch);
            Task<TResult>? providerTask = null;
            TResult result;
            try
            {
                providerTask = execute(request, deadline.Token);
                result = await providerTask.WaitAsync(remaining, ct);
            }
            catch (TimeoutException ex)
            {
                deadline.Cancel();
                ObserveLateFailure(providerTask);
                throw new ExtensionContributionTimeoutException(ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                deadline.Cancel();
                ObserveLateFailure(providerTask);
                throw new ExtensionContributionTimeoutException();
            }
            catch (OperationCanceledException)
            {
                ObserveLateFailure(providerTask);
                throw;
            }
            catch (Exception ex)
            {
                throw new ExtensionContributionProviderException(ex);
            }

            var revision = getRevision(result);
            if (string.IsNullOrWhiteSpace(revision) || revision.Length > _revisionLengthLimit)
                throw new ExtensionContributionResultException("The extension contribution returned a missing or oversized revision.");
            if (stableRevision != null && !string.Equals(stableRevision, revision, StringComparison.Ordinal))
                throw new ExtensionContributionResultException("The extension contribution revision changed during execution.");

            validateResult(batch, result);
            stableRevision = revision;
            results.Add(result);
        }

        return results;
    }

    private static void ObserveLateFailure(Task? providerTask)
    {
        if (providerTask is null)
            return;

        _ = providerTask.ContinueWith(
            task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed class ExtensionContributionTimeoutException : Exception
{
    public ExtensionContributionTimeoutException() : base("The extension contribution timed out.") { }
    public ExtensionContributionTimeoutException(Exception inner) : base("The extension contribution timed out.", inner) { }
}

internal sealed class ExtensionContributionProviderException(Exception inner)
    : Exception("The extension contribution provider failed.", inner);

internal sealed class ExtensionContributionResultException(string message) : Exception(message);
