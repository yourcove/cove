namespace Cove.Api.Services;

public sealed class VideoGeneratedAssetCoordinator
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 257).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> versions = new();

    public async Task<IAsyncDisposable> AcquireAsync(int videoId, CancellationToken ct)
    {
        var gate = gates[(int)((uint)videoId % (uint)gates.Length)];
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }

    public async Task<bool> RunAsync(int videoId, Func<Task<bool>> operation, CancellationToken ct)
    {
        var expectedVersion = versions.GetValueOrDefault(videoId);
        await using var lease = await AcquireAsync(videoId, ct);
        if (versions.GetValueOrDefault(videoId) != expectedVersion) return false;
        return await operation();
    }

    public void Advance(int videoId) => versions.AddOrUpdate(videoId, 1, static (_, value) => value + 1);

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
