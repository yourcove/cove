using System.Threading.Channels;

namespace Cove.Tests.Postgres;

/// <summary>
/// Drops finished test databases in the background. PostgreSQL makes every <c>DROP DATABASE</c> wait for a
/// checkpoint, which flushes and syncs every dirty page on the server, so a test that dropped its own database
/// spent seconds waiting on writes made by the other tests. Here a few drops run at once, so they mostly wait on
/// the same checkpoints, and no test waits for them. The queue is bounded, so when dropping falls behind, tests
/// wait to queue instead of leaving databases to pile up on the server.
/// </summary>
internal static class DeferredDatabaseDrops
{
    private const int QueueCapacity = 32;
    private const int ConcurrentDrops = 8;

    private static readonly Channel<PostgreSqlTestDatabase> Queue = Channel.CreateBounded<PostgreSqlTestDatabase>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait });

    private static readonly Lock FailuresLock = new();
    private static readonly List<Exception> Failures = [];
    private static readonly Lazy<Task> Droppers = new(StartDroppers);

    public static async ValueTask EnqueueAsync(PostgreSqlTestDatabase database)
    {
        _ = Droppers.Value;
        try
        {
            await Queue.Writer.WriteAsync(database);
        }
        catch (ChannelClosedException)
        {
            // Queued after the run drained the queue: drop it here rather than leave it behind.
            await DropAsync(database);
        }
    }

    /// <summary>Drops everything queued and reports the drops that failed, since each leaves a database behind.</summary>
    public static async Task DrainAsync()
    {
        Queue.Writer.TryComplete();
        if (!Droppers.IsValueCreated)
            return;

        await Droppers.Value;
        lock (FailuresLock)
        {
            if (Failures.Count > 0)
                throw new AggregateException("Some test databases could not be dropped.", Failures);
        }
    }

    private static Task StartDroppers()
    {
        // The first drop is queued from some test's DisposeAsync; do not carry that test's async-local state
        // (its TestContext, current principal or ambient transaction) into the drops of every later test.
        using (ExecutionContext.SuppressFlow())
            return Task.WhenAll(Enumerable.Range(0, ConcurrentDrops).Select(_ => Task.Run(DropQueuedAsync)));
    }

    private static async Task DropQueuedAsync()
    {
        await foreach (var database in Queue.Reader.ReadAllAsync())
            await DropAsync(database);
    }

    private static async Task DropAsync(PostgreSqlTestDatabase database)
    {
        try
        {
            await database.DisposeAsync();
        }
        catch (Exception exception)
        {
            lock (FailuresLock)
                Failures.Add(new InvalidOperationException($"Could not drop test database {database.DatabaseName}.", exception));
        }
    }
}
