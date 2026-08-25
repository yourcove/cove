using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cove.Tests;

internal sealed class TestRetryingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
    : IExecutionStrategyFactory
{
    public IExecutionStrategy Create() => new TestRetryingExecutionStrategy(dependencies);
}

internal sealed class TestRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception)
    {
        if (exception is not TestTransientException transient)
            return false;
        transient.BeforeRetry();
        return true;
    }
}

internal sealed class TestTransientException(Action? beforeRetry = null) : Exception
{
    private Action? _beforeRetry = beforeRetry;

    public void BeforeRetry() => Interlocked.Exchange(ref _beforeRetry, null)?.Invoke();
}

internal sealed class CommitAmbiguityInterceptor : DbTransactionInterceptor
{
    private int _remainingFailures;
    private Func<Task>? _afterNextCommit;

    public int FailuresRaised { get; private set; }

    public void Arm(Func<Task>? afterNextCommit = null, int failureCount = 1)
    {
        _afterNextCommit = afterNextCommit;
        _remainingFailures = failureCount;
    }

    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (_remainingFailures <= 0)
            return;

        _remainingFailures--;
        FailuresRaised++;
        var callback = Interlocked.Exchange(ref _afterNextCommit, null);
        if (callback is not null)
            await callback();
        throw new TestTransientException();
    }
}

internal sealed class PreCommitFailureInterceptor : DbTransactionInterceptor
{
    private Func<Task>? _afterRollback;

    public void Arm(Func<Task> afterRollback) => _afterRollback = afterRollback;

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        var callback = Interlocked.Exchange(ref _afterRollback, null);
        if (callback is null)
            return ValueTask.FromResult(result);

        return ValueTask.FromException<InterceptionResult>(new TestTransientException(
            () => callback().GetAwaiter().GetResult()));
    }
}
