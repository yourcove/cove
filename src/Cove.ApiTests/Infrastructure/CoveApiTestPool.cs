using System.Threading.Channels;

namespace Cove.ApiTests.Infrastructure;

public sealed class CoveApiTestPool : IAsyncLifetime
{
    public const int MaxParallelThreads = 4;

    private readonly Lock _stateLock = new();
    private readonly int _capacity;
    private readonly HashSet<CoveApiServer> _leasedServers = [];
    private Channel<CoveApiServer>? _availableServers;
    private IReadOnlyList<CoveApiServer> _servers = [];
    private Exception? _failure;
    private bool _initializing;
    private bool _disposed;

    public CoveApiTestPool()
        : this(MaxParallelThreads)
    {
    }

    internal CoveApiTestPool(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    internal int ConfiguredCapacity
        => _capacity;

    internal bool IsInitialized
    {
        get
        {
            lock (_stateLock)
                return _availableServers is not null && _failure is null && !_disposed;
        }
    }

    public async ValueTask InitializeAsync()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initializing || _availableServers is not null)
                throw new InvalidOperationException("The API test pool has already been initialized.");
            _initializing = true;
        }

        var starts = Enumerable.Range(0, _capacity)
            .Select(_ => CoveApiServer.StartAsync(TestContext.Current.CancellationToken))
            .ToArray();
        CoveApiServer[] servers;
        try
        {
            servers = await Task.WhenAll(starts);
        }
        catch (Exception startupError)
        {
            lock (_stateLock)
                _initializing = false;
            var startedServers = starts
                .Where(start => start.IsCompletedSuccessfully)
                .Select(start => start.Result)
                .ToArray();
            try
            {
                await DisposeServersAsync(startedServers);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(startupError, cleanupError);
            }
            throw;
        }

        var availableServers = Channel.CreateBounded<CoveApiServer>(new BoundedChannelOptions(_capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        foreach (var server in servers)
        {
            if (!availableServers.Writer.TryWrite(server))
                throw new InvalidOperationException("The API test pool could not register a started server.");
        }

        lock (_stateLock)
        {
            _initializing = false;
            if (!_disposed)
            {
                _servers = servers;
                _availableServers = availableServers;
                return;
            }
        }

        await DisposeServersAsync(servers);
        throw new ObjectDisposedException(nameof(CoveApiTestPool), "The API test pool was disposed during initialization.");
    }

    internal async ValueTask<CoveApiServer> RentAsync(
        CancellationToken cancellationToken = default)
    {
        Channel<CoveApiServer> availableServers;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFailed();
            availableServers = _availableServers
                ?? throw new InvalidOperationException("The API test pool has not been initialized.");
        }

        CoveApiServer server;
        try
        {
            server = await availableServers.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException exception)
        {
            lock (_stateLock)
                throw new InvalidOperationException("The API test pool is unavailable.", _failure ?? exception);
        }
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFailed();
            if (!_leasedServers.Add(server))
                throw new InvalidOperationException("The API test pool leased the same server more than once.");
        }
        return server;
    }

    internal void Return(CoveApiServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Channel<CoveApiServer>? availableServers;
        lock (_stateLock)
        {
            if (!_leasedServers.Remove(server))
            {
                if (_disposed)
                    return;
                throw new InvalidOperationException("The API test pool cannot return a server that it did not lease.");
            }
            if (_disposed || _failure is not null)
                return;
            availableServers = _availableServers;
        }

        if (availableServers is null || !availableServers.Writer.TryWrite(server))
            throw new InvalidOperationException("The API test pool could not return a leased server.");
    }

    internal async ValueTask RetireAsync(CoveApiServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (_stateLock)
        {
            if (!_leasedServers.Remove(server))
            {
                if (_disposed)
                    return;
                throw new InvalidOperationException("The API test pool cannot retire a server that it did not lease.");
            }
            if (_disposed || _failure is not null)
                return;
            if (!_servers.Contains(server))
                throw new InvalidOperationException("The API test pool cannot retire an unregistered server.");
        }

        try
        {
            await server.DisposeAsync();
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }

        lock (_stateLock)
        {
            _servers = _servers.Where(candidate => candidate != server).ToArray();
            if (_disposed || _failure is not null)
                return;
        }

        CoveApiServer replacement;
        try
        {
            replacement = await CoveApiServer.StartAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }

        Channel<CoveApiServer>? availableServers;
        lock (_stateLock)
        {
            if (_disposed || _failure is not null)
            {
                availableServers = null;
            }
            else
            {
                _servers = [.. _servers, replacement];
                availableServers = _availableServers;
            }
        }

        if (availableServers is null)
        {
            await replacement.DisposeAsync();
            return;
        }
        if (!availableServers.Writer.TryWrite(replacement))
        {
            lock (_stateLock)
                _servers = _servers.Where(candidate => candidate != replacement).ToArray();
            await replacement.DisposeAsync();
            var registrationError = new InvalidOperationException("The API test pool could not register a replacement server.");
            Fail(registrationError);
            throw registrationError;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IReadOnlyList<CoveApiServer> servers;
        int outstandingLeaseCount;
        lock (_stateLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _availableServers?.Writer.TryComplete();
            _availableServers = null;
            servers = _servers;
            _servers = [];
            outstandingLeaseCount = _leasedServers.Count;
            _leasedServers.Clear();
        }

        Exception? cleanupError = null;
        try
        {
            await DisposeServersAsync(servers);
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }

        if (outstandingLeaseCount > 0)
        {
            var leaseError = new InvalidOperationException(
                $"The API test pool was disposed with {outstandingLeaseCount} outstanding server lease(s).");
            cleanupError = cleanupError is null
                ? leaseError
                : new AggregateException(cleanupError, leaseError);
        }

        if (_failure is not null)
        {
            cleanupError = cleanupError is null
                ? _failure
                : new AggregateException(cleanupError, _failure);
        }

        if (cleanupError is not null)
            throw cleanupError;
    }

    private static Task DisposeServersAsync(IEnumerable<CoveApiServer> servers)
        => Task.WhenAll(servers.Select(server => server.DisposeAsync().AsTask()));

    private void Fail(Exception exception)
    {
        Channel<CoveApiServer>? availableServers;
        lock (_stateLock)
        {
            if (_disposed || _failure is not null)
                return;
            _failure = exception;
            availableServers = _availableServers;
        }
        availableServers?.Writer.TryComplete(exception);
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null)
            throw new InvalidOperationException("The API test pool is unavailable after a server retirement failure.", _failure);
    }
}
