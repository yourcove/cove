using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Cove.ApiTests.ExampleData;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Core.Auth;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Infrastructure;

internal sealed partial class CoveApiServer : IAsyncDisposable
{
    private const string EnvironmentName = "IntegrationStartup";
    private const string ResetTokenHeader = "X-Cove-Test-Reset-Token";
    private const string FaceSuggestionProviderDirectoryName = "com.cove.api-test-face-provider";
    private const string FaceSuggestionProviderBuildDirectoryName = "face-suggestion-provider";

    private readonly PostgreSqlTestDatabase _database;
    private readonly MetadataServiceSimulator _metadataService;
    private readonly DownloadSourceSimulator _downloadSource;
    private readonly ExtensionRegistrySimulator _extensionRegistry;
    private readonly ApiTestFileManagerRecorder _fileManagerRecorder;
    private readonly Process _process;
    private readonly string _dataRoot;
    private readonly string _faceSuggestionPlanPath;
    private readonly string _resetToken;
    private readonly ConcurrentQueue<string> _output;
    private bool _disposed;

    private CoveApiServer(
        PostgreSqlTestDatabase database,
        MetadataServiceSimulator metadataService,
        DownloadSourceSimulator downloadSource,
        ExtensionRegistrySimulator extensionRegistry,
        ApiTestFileManagerRecorder fileManagerRecorder,
        Process process,
        Uri baseAddress,
        string dataRoot,
        string libraryPath,
        string faceSuggestionPlanPath,
        string resetToken,
        ConcurrentQueue<string> output)
    {
        _database = database;
        _metadataService = metadataService;
        _downloadSource = downloadSource;
        _extensionRegistry = extensionRegistry;
        _fileManagerRecorder = fileManagerRecorder;
        _process = process;
        BaseAddress = baseAddress;
        _dataRoot = dataRoot;
        _faceSuggestionPlanPath = faceSuggestionPlanPath;
        FileSystem = new ApiTestFileSystem(libraryPath, Path.Combine(dataRoot, "generated"));
        _resetToken = resetToken;
        _output = output;
    }

    public Uri BaseAddress { get; }
    public MetadataServiceSimulator MetadataService => _metadataService;
    public DownloadSourceSimulator DownloadSource => _downloadSource;
    public ExtensionRegistrySimulator ExtensionRegistry => _extensionRegistry;
    public ApiTestFileManagerRecorder FileManagerRecorder => _fileManagerRecorder;
    public ApiTestFileSystem FileSystem { get; }
    public DatabaseClient DbUser => new(_database.ConnectionString);
    internal string DatabaseName => _database.DatabaseName;
    internal string DataRoot => _dataRoot;
    internal long ProcessStartedTimestamp { get; private init; }
    internal long ReadyTimestamp { get; private init; }

    public static async Task<CoveApiServer> StartAsync(CancellationToken cancellationToken = default)
    {
        PostgreSqlTestDatabase? database = null;
        MetadataServiceSimulator? metadataService = null;
        DownloadSourceSimulator? downloadSource = null;
        ExtensionRegistrySimulator? extensionRegistry = null;
        Process? process = null;
        var dataRoot = Path.Combine(Path.GetTempPath(), $"cove-api-tests-{Guid.NewGuid():N}");
        var libraryPath = Path.Combine(dataRoot, "library");
        var faceSuggestionPlanPath = Path.Combine(dataRoot, "face-suggestion-plan.json");
        ApiTestFileManagerRecorder? fileManagerRecorder = null;
        var resetToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var output = new ConcurrentQueue<string>();

        try
        {
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(libraryPath);
            fileManagerRecorder = ApiTestFileManagerRecorder.Create(dataRoot);
            InstallFaceSuggestionProvider(dataRoot);
            await WriteFaceSuggestionPlanAsync(
                faceSuggestionPlanPath,
                new Dictionary<int, IReadOnlyList<FaceSuggestionDto>>(),
                cancellationToken);
            metadataService = await MetadataServiceSimulator.StartAsync(cancellationToken);
            downloadSource = await DownloadSourceSimulator.StartAsync(cancellationToken);
            extensionRegistry = await ExtensionRegistrySimulator.StartAsync(cancellationToken);
            database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
            process = StartApiProcess(
                dataRoot,
                database.ConnectionString,
                metadataService.Endpoint,
                extensionRegistry.Endpoint,
                fileManagerRecorder,
                libraryPath,
                faceSuggestionPlanPath,
                resetToken,
                output);
            var processStartedTimestamp = Stopwatch.GetTimestamp();
            var baseAddress = await WaitForListeningAddressAsync(process, output, cancellationToken);

            using var startupClient = new HttpClient { BaseAddress = baseAddress };
            await WaitUntilReadyAsync(startupClient, process, output, cancellationToken);

            return new CoveApiServer(
                database,
                metadataService,
                downloadSource,
                extensionRegistry,
                fileManagerRecorder,
                process,
                baseAddress,
                dataRoot,
                libraryPath,
                faceSuggestionPlanPath,
                resetToken,
                output)
            {
                ProcessStartedTimestamp = processStartedTimestamp,
                ReadyTimestamp = Stopwatch.GetTimestamp(),
            };
        }
        catch (Exception startupError)
        {
            Exception? cleanupError = null;
            try
            {
                if (process is { HasExited: false })
                    await KillAndWaitAsync(process);
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }
            finally
            {
                process?.Dispose();
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

            try
            {
                if (metadataService is not null)
                    await metadataService.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupError = cleanupError is null
                    ? exception
                    : new AggregateException(cleanupError, exception);
            }

            try
            {
                if (downloadSource is not null)
                    await downloadSource.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupError = cleanupError is null
                    ? exception
                    : new AggregateException(cleanupError, exception);
            }

            try
            {
                if (extensionRegistry is not null)
                    await extensionRegistry.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupError = cleanupError is null
                    ? exception
                    : new AggregateException(cleanupError, exception);
            }
            finally
            {
                TryDeleteDataRoot(dataRoot);
            }

            if (cleanupError is not null)
                throw new AggregateException(startupError, cleanupError);
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, CoveClient>> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        _metadataService.ReleaseBlockedRequests();
        await ConfigureFaceSuggestionPlanAsync(
            new Dictionary<int, IReadOnlyList<FaceSuggestionDto>>(),
            cancellationToken);
        using var client = CreateLifecycleClient();
        using var response = await client.PostAsync("/health/test-reset", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"POST /health/test-reset returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}{Environment.NewLine}"
                + $"API process output:{Environment.NewLine}{FormatOutput(_output)}");
        }
        _metadataService.Reset();
        _downloadSource.Reset();
        _extensionRegistry.Reset();
        _fileManagerRecorder.Reset();
        FileSystem.Reset();
        var users = new Dictionary<string, CoveClient>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var owner = await CreateOwnerAsync(cancellationToken);
            users.Add(owner.Username, owner);
            foreach (var (username, displayName) in new[]
            {
                (ApiTestUsers.Eva, "Eva"),
                (ApiTestUsers.Anthony, "Anthony"),
            })
            {
                await owner.CreateUserAsync(new CreateUserRequest(
                    username,
                    ApiTestUsers.Password,
                    DisplayName: displayName,
                    Roles: [BuiltinRoles.Member]), cancellationToken);
                var member = await CreateTestSessionAsync(username, cancellationToken);
                users.Add(member.Username, member);
            }
            return users;
        }
        catch
        {
            foreach (var user in users.Values)
                user.Dispose();
            throw;
        }
    }

    internal Task ConfigureFaceSuggestionPlanAsync(
        IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>> plan,
        CancellationToken cancellationToken = default)
        => WriteFaceSuggestionPlanAsync(_faceSuggestionPlanPath, plan, cancellationToken);

    internal Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _process.WaitForExitAsync(cancellationToken);

    private async Task<CoveClient> CreateOwnerAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress };
        using var response = await client.PostAsJsonAsync(
            "/api/auth/bootstrap-owner",
            new { username = ApiTestUsers.Owner, password = ApiTestUsers.Password },
            ApiJson.Options,
            cancellationToken);
        var login = await ApiResponse.ReadAsync<AuthenticationResponse>(
            response,
            "POST /api/auth/bootstrap-owner",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(login.Token))
            throw new InvalidOperationException("The owner bootstrap response did not contain an access token.");

        return new CoveClient(ApiTestUsers.Owner, BaseAddress, login.Token);
    }

    private async Task<CoveClient> CreateTestSessionAsync(
        string username,
        CancellationToken cancellationToken)
    {
        using var client = CreateLifecycleClient();
        using var response = await client.PostAsync(
            $"/health/test-session/{Uri.EscapeDataString(username)}",
            content: null,
            cancellationToken);
        var login = await ApiResponse.ReadAsync<AuthenticationResponse>(
            response,
            "POST /health/test-session/{username}",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(login.Token))
            throw new InvalidOperationException($"The test session response for '{username}' did not contain an access token.");
        return new CoveClient(username, BaseAddress, login.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        Exception? cleanupError = null;

        try
        {
            if (!_process.HasExited)
            {
                using var client = CreateLifecycleClient();
                using var response = await client.PostAsync("/health/test-shutdown", content: null);
                if (response.StatusCode is not HttpStatusCode.Accepted)
                {
                    throw new InvalidOperationException(
                        $"The API test host rejected graceful shutdown with {(int)response.StatusCode}.{Environment.NewLine}"
                        + $"API process output:{Environment.NewLine}{FormatOutput(_output)}");
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception exception)
        {
            cleanupError = exception;
            if (!_process.HasExited)
            {
                try
                {
                    await KillAndWaitAsync(_process);
                }
                catch (Exception killError)
                {
                    cleanupError = new AggregateException(cleanupError, killError);
                }
            }
        }
        finally
        {
            _process.Dispose();
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

        try
        {
            await _metadataService.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupError = cleanupError is null
                ? exception
                : new AggregateException(cleanupError, exception);
        }

        try
        {
            await _downloadSource.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupError = cleanupError is null
                ? exception
                : new AggregateException(cleanupError, exception);
        }

        try
        {
            await _extensionRegistry.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupError = cleanupError is null
                ? exception
                : new AggregateException(cleanupError, exception);
        }
        finally
        {
            TryDeleteDataRoot(_dataRoot);
        }

        if (cleanupError is not null)
            throw cleanupError;
    }

    private HttpClient CreateLifecycleClient()
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Add(ResetTokenHeader, _resetToken);
        return client;
    }

    private static Process StartApiProcess(
        string dataRoot,
        string connectionString,
        Uri metadataServiceEndpoint,
        Uri extensionRegistryEndpoint,
        ApiTestFileManagerRecorder fileManagerRecorder,
        string libraryPath,
        string faceSuggestionPlanPath,
        string resetToken,
        ConcurrentQueue<string> output)
    {
        var assemblyPath = typeof(Program).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = EnvironmentName;
        startInfo.Environment["DOTNET_ENVIRONMENT"] = EnvironmentName;
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        startInfo.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
        startInfo.Environment["COVE_HOME"] = dataRoot;
        startInfo.Environment["COVE__Auth__Enabled"] = "true";
        startInfo.Environment["COVE__Auth__JwtSecret"] = "cove-fluent-api-tests-only-jwt-secret-4b93f6f2";
        startInfo.Environment["COVE__BackupPath"] = Path.Combine(dataRoot, "backups");
        startInfo.Environment["COVE__CachePath"] = Path.Combine(dataRoot, "cache");
        startInfo.Environment["COVE__CovePaths__0__Path"] = libraryPath;
        startInfo.Environment["COVE__ExtensionPaths__0"] = Path.Combine(dataRoot, "plugins");
        startInfo.Environment["COVE__ExtensionRegistryBaseUrl"] = extensionRegistryEndpoint.AbsoluteUri;
        fileManagerRecorder.Configure(startInfo);
        startInfo.Environment["COVE__ApiTestFaceSuggestions__PlanPath"] = faceSuggestionPlanPath;
        startInfo.Environment[
            "COVE__PluginConfigurations__com.cove.api-test-face-provider__apiTestBaseline"] = "preserved";
        startInfo.Environment["COVE__GeneratedPath"] = Path.Combine(dataRoot, "generated");
        startInfo.Environment["COVE__IntegrationTestResetToken"] = resetToken;
        startInfo.Environment["COVE__Postgres__ConnectionString"] = connectionString;
        startInfo.Environment["COVE__Postgres__Managed"] = "false";
        startInfo.Environment["COVE__Scraping__MetadataServers__0__ApiKey"] = MetadataServiceSimulator.ApiKey;
        startInfo.Environment["COVE__Scraping__MetadataServers__0__Endpoint"] = metadataServiceEndpoint.AbsoluteUri;
        startInfo.Environment["COVE__Scraping__MetadataServers__0__Name"] = TestCatalog.MetadataServices.PulpMovieDb.Name;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Cove API test process could not be started.");
        process.OutputDataReceived += (_, args) => CaptureOutput(output, args.Data);
        process.ErrorDataReceived += (_, args) => CaptureOutput(output, args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void InstallFaceSuggestionProvider(string dataRoot)
    {
        var sourceDirectory = Path.Combine(
            Path.GetDirectoryName(typeof(CoveApiServer).Assembly.Location)
                ?? throw new InvalidOperationException("The API-test assembly has no directory."),
            FaceSuggestionProviderBuildDirectoryName);
        var manifestPath = Path.Combine(sourceDirectory, "extension.json");
        var assemblyPath = Path.Combine(sourceDirectory, "Cove.ApiTestFaceProvider.dll");
        if (!File.Exists(manifestPath) || !File.Exists(assemblyPath))
        {
            throw new InvalidOperationException(
                "The API-test face suggestion provider is missing from the test output. Build Cove.ApiTests before starting the API test host.");
        }

        var targetDirectory = Path.Combine(dataRoot, "extensions", FaceSuggestionProviderDirectoryName);
        Directory.CreateDirectory(targetDirectory);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            File.Copy(sourcePath, Path.Combine(targetDirectory, Path.GetFileName(sourcePath)), overwrite: true);
    }

    private static async Task WriteFaceSuggestionPlanAsync(
        string path,
        IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>> plan,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(plan, ApiJson.Options),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<Uri> WaitForListeningAddressAsync(
        Process process,
        ConcurrentQueue<string> output,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, output);

            foreach (var line in output)
            {
                var match = ListeningAddressRegex().Match(line);
                if (match.Success && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var address))
                    return address;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException($"The Cove API process did not publish a listening address. Output:{Environment.NewLine}{FormatOutput(output)}");
    }

    private static async Task WaitUntilReadyAsync(
        HttpClient client,
        Process process,
        ConcurrentQueue<string> output,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, output);

            try
            {
                using var response = await client.GetAsync("/health/startup", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
                // Kestrel can publish its address just before it accepts the first request.
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"The Cove API process did not become ready. Output:{Environment.NewLine}{FormatOutput(output)}");
    }

    private static void ThrowIfExited(Process process, ConcurrentQueue<string> output)
    {
        if (process.HasExited)
            throw new InvalidOperationException($"The Cove API process exited with code {process.ExitCode}. Output:{Environment.NewLine}{FormatOutput(output)}");
    }

    private static void CaptureOutput(ConcurrentQueue<string> output, string? line)
    {
        if (line is null)
            return;
        output.Enqueue(line);
        while (output.Count > 500 && output.TryDequeue(out _))
        {
        }
    }

    private static string FormatOutput(ConcurrentQueue<string> output)
        => string.Join(Environment.NewLine, output);

    private static async Task KillAndWaitAsync(Process process)
    {
        process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
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

    [GeneratedRegex(@"Now listening on:\s+(http://\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ListeningAddressRegex();

    private sealed record AuthenticationResponse(string Token);
}
