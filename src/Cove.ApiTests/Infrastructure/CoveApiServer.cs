using System.Net;
using System.Net.Http.Json;
using Cove.Api.Services;
using Cove.Data.Auth;
using Cove.Data.Services;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.ApiTests.Infrastructure;

internal sealed class CoveApiServer : IAsyncDisposable
{
    private const string EnvironmentName = "IntegrationStartup";
    private static readonly SemaphoreSlim ProcessEnvironmentLock = new(1, 1);
    private static readonly string[] TestEnvironmentVariableNames =
    [
        "ASPNETCORE_ENVIRONMENT",
        "COVE_HOME",
        "COVE__Auth__Enabled",
        "COVE__Auth__JwtSecret",
        "COVE__BackupPath",
        "COVE__CachePath",
        "COVE__ExtensionPaths__0",
        "COVE__GeneratedPath",
        "COVE__Postgres__ConnectionString",
        "COVE__Postgres__Managed",
        "DOTNET_ENVIRONMENT",
    ];

    private readonly PostgreSqlTestDatabase _database;
    private readonly CoveApiWebApplicationFactory _factory;
    private readonly string _dataRoot;
    private readonly IReadOnlyDictionary<string, string?> _previousEnvironment;
    private bool _disposed;
    private bool _hasTestState;

    private CoveApiServer(
        PostgreSqlTestDatabase database,
        CoveApiWebApplicationFactory factory,
        Uri baseAddress,
        string dataRoot,
        IReadOnlyDictionary<string, string?> previousEnvironment)
    {
        _database = database;
        _factory = factory;
        BaseAddress = baseAddress;
        _dataRoot = dataRoot;
        _previousEnvironment = previousEnvironment;
    }

    public Uri BaseAddress { get; }

    public static async Task<CoveApiServer> StartAsync(CancellationToken cancellationToken = default)
    {
        await ProcessEnvironmentLock.WaitAsync(cancellationToken);

        PostgreSqlTestDatabase? database = null;
        CoveApiWebApplicationFactory? factory = null;
        var dataRoot = Path.Combine(Path.GetTempPath(), $"cove-api-tests-{Guid.NewGuid():N}");
        var previousEnvironment = CaptureEnvironment();

        try
        {
            Directory.CreateDirectory(dataRoot);
            database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
            ApplyTestEnvironment(dataRoot, database.ConnectionString);
            factory = new CoveApiWebApplicationFactory(database.ConnectionString, dataRoot);
            using var startupClient = factory.CreateClient();

            var baseAddress = startupClient.BaseAddress
                ?? throw new InvalidOperationException("The Cove API host did not publish a listening address.");
            if (baseAddress.Port is 0 or 80)
                throw new InvalidOperationException($"The Cove API host did not bind to a random Kestrel port: {baseAddress}.");

            await WaitUntilReadyAsync(startupClient, cancellationToken);

            return new CoveApiServer(
                database,
                factory,
                baseAddress,
                dataRoot,
                previousEnvironment);
        }
        catch (Exception startupError)
        {
            Exception? cleanupError = null;
            try
            {
                if (factory is not null)
                    await factory.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }

            try
            {
                if (database is not null)
                    await database.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupError = cleanupError is null
                    ? exception
                    : new AggregateException(cleanupError, exception);
            }
            finally
            {
                RestoreEnvironment(previousEnvironment);
                TryDeleteDataRoot(dataRoot);
                ProcessEnvironmentLock.Release();
            }

            if (cleanupError is not null)
                throw new AggregateException(startupError, cleanupError);
            throw;
        }
    }

    public async Task<ApiUser> CreateOwnerAsync(CancellationToken cancellationToken = default)
    {
        const string username = "api-test-owner";
        const string password = "api-test-password-4b93f6f2";

        using var client = new HttpClient { BaseAddress = BaseAddress };
        using var response = await client.PostAsJsonAsync(
            "/api/auth/bootstrap-owner",
            new { username, password },
            ApiJson.Options,
            cancellationToken);
        var login = await ApiResponse.ReadAsync<AuthenticationResponse>(
            response,
            "POST /api/auth/bootstrap-owner",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(login.Token))
            throw new InvalidOperationException("The owner bootstrap response did not contain an access token.");

        return new ApiUser(BaseAddress, login.Token);
    }

    public async Task<ApiUser> ResetAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasTestState)
        {
            var owner = await CreateOwnerAsync(cancellationToken);
            _hasTestState = true;
            return owner;
        }

        await CancelAndDrainJobsAsync(cancellationToken);
        await _factory.Services.GetRequiredService<AuditService>().FlushAsync(cancellationToken);
        await _database.ResetAsync(cancellationToken);

        _factory.Services.GetRequiredService<SegmentSpanCacheRegistry>().InvalidateAll();
        if (_factory.Services.GetRequiredService<IMemoryCache>() is MemoryCache memoryCache)
            memoryCache.Compact(1);
        await _factory.Services
            .GetRequiredService<IOutputCacheStore>()
            .EvictByTagAsync("api-test-state", cancellationToken);

        await _factory.Services
            .GetRequiredService<BootstrapAuthService>()
            .RefreshPermissionCatalogAsync(cancellationToken);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<DynamicGroupResolver>()
                .EnsureBuiltInGroupsAsync(cancellationToken);
        }

        return await CreateOwnerAsync(cancellationToken);
    }

    private async Task CancelAndDrainJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = _factory.Services.GetRequiredService<JobService>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await jobs.CancelAllAndWaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Active API jobs did not stop before the test database reset.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        Exception? cleanupError = null;

        try
        {
            await _factory.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupError = exception;
        }

        try
        {
            await _database.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupError = cleanupError is null
                ? exception
                : new AggregateException(cleanupError, exception);
        }
        finally
        {
            RestoreEnvironment(_previousEnvironment);
            TryDeleteDataRoot(_dataRoot);
            ProcessEnvironmentLock.Release();
        }

        if (cleanupError is not null)
            throw cleanupError;
    }

    private static async Task WaitUntilReadyAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string? lastResponse = null;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await client.GetAsync("/health/startup", cancellationToken);
                lastResponse = $"{(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}";
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(
            $"The Cove API did not become ready in time. Last response: {lastResponse ?? "none"}.",
            lastError);
    }

    private static IReadOnlyDictionary<string, string?> CaptureEnvironment()
        => TestEnvironmentVariableNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

    private static void ApplyTestEnvironment(string dataRoot, string connectionString)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", EnvironmentName);
        Environment.SetEnvironmentVariable("COVE_HOME", dataRoot);
        Environment.SetEnvironmentVariable("COVE__Auth__Enabled", "true");
        Environment.SetEnvironmentVariable("COVE__Auth__JwtSecret", "cove-fluent-api-tests-only-jwt-secret-4b93f6f2");
        Environment.SetEnvironmentVariable("COVE__BackupPath", Path.Combine(dataRoot, "backups"));
        Environment.SetEnvironmentVariable("COVE__CachePath", Path.Combine(dataRoot, "cache"));
        Environment.SetEnvironmentVariable("COVE__ExtensionPaths__0", Path.Combine(dataRoot, "plugins"));
        Environment.SetEnvironmentVariable("COVE__GeneratedPath", Path.Combine(dataRoot, "generated"));
        Environment.SetEnvironmentVariable("COVE__Postgres__ConnectionString", connectionString);
        Environment.SetEnvironmentVariable("COVE__Postgres__Managed", "false");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", EnvironmentName);
    }

    private static void RestoreEnvironment(IReadOnlyDictionary<string, string?> previousEnvironment)
    {
        foreach (var (name, value) in previousEnvironment)
            Environment.SetEnvironmentVariable(name, value);
    }

    private static void TryDeleteDataRoot(string dataRoot)
    {
        try
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
        catch
        {
            // A failed temporary-directory cleanup should not hide a test or database failure.
        }
    }

    private sealed record AuthenticationResponse(string Token);
}
