using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.ApiTestFaceProvider;

/// <summary>
/// A deliberately small runtime extension used only by API tests. Its plan is owned by the
/// corresponding test host, rather than static state, so the two API-test lanes cannot affect each other.
/// </summary>
public sealed class ApiTestFaceSuggestionExtension : CoveExtensionBase, IStatefulExtension, IJobExtension
{
    public const string RecordParametersJobId = "record-parameters";
    public const string JobParametersStoreKey = "api-test.job.parameters";
    public const string JobProgressStoreKey = "api-test.job.progress";
    public const string FailInitializationStoreKey = "api-test.initialize.fail";
    public const string CaptureInstallCountParameter = "capture-install-count";
    public const string ExpectedInstallCountParameter = "expected-install-count";
    public const string InstallCountStoreKey = "api-test.install-count";

    private static readonly IReadOnlyList<ExtensionJobDefinition> JobDefinitions =
    [
        new(
            RecordParametersJobId,
            "Record API test parameters",
            "Records deterministic API-test parameters and progress in extension state.",
            SupportsParameters: true)
        {
            ShowInTaskList = true,
        },
    ];

    private IExtensionStore? _store;
    private int _installCount;

    public override IReadOnlyDictionary<string, string> Dependencies { get; } =
        new Dictionary<string, string>
        {
            [ApiTestDependencyExtension.ExtensionId] = ">=1.0.0",
        };

    public IReadOnlyList<ExtensionJobDefinition> Jobs => JobDefinitions;

    public override void ConfigureServices(IServiceCollection services, Cove.Plugins.ExtensionContext context)
        => services.AddSingleton<IFaceSuggester, PlannedFaceSuggester>();

    public override async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        if (_store != null
            && string.Equals(
                await _store.GetAsync(FailInitializationStoreKey, ct),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("API-test extension initialization failure requested.");
        }

        PublishContributions<IFaceSuggester>(services);
    }

    public override Task OnInstallAsync(IServiceProvider services, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _installCount);
        return Task.CompletedTask;
    }

    public override Task ShutdownAsync(CancellationToken ct = default)
    {
        _store = null;
        return Task.CompletedTask;
    }

    public void SetStore(IExtensionStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task RunJobAsync(
        string jobId,
        IReadOnlyDictionary<string, string>? parameters,
        Cove.Plugins.IJobProgress progress,
        CancellationToken ct)
    {
        if (!string.Equals(jobId, RecordParametersJobId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown API-test extension job '{jobId}'.");

        if (parameters?.TryGetValue(ExpectedInstallCountParameter, out var expectedInstallCountText) == true
            && int.TryParse(expectedInstallCountText, out var expectedInstallCount)
            && Volatile.Read(ref _installCount) != expectedInstallCount)
        {
            throw new InvalidOperationException(
                $"Expected install count {expectedInstallCount}, but found {Volatile.Read(ref _installCount)}.");
        }

        var store = _store ?? throw new InvalidOperationException("Extension store has not been initialized.");
        var orderedParameters = (parameters ?? new Dictionary<string, string>())
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        progress.Report(0.25, "Recording API test parameters");
        await store.SetAsync(JobParametersStoreKey, JsonSerializer.Serialize(orderedParameters), ct);
        await store.SetAsync(JobProgressStoreKey, "0.25|Recording API test parameters", ct);
        if (parameters?.ContainsKey(CaptureInstallCountParameter) == true)
            await store.SetAsync(InstallCountStoreKey, Volatile.Read(ref _installCount).ToString(), ct);

        progress.Report(1, "API test parameters recorded");
        await store.SetAsync(JobProgressStoreKey, "1|API test parameters recorded", ct);
    }
}

public sealed class ApiTestDependencyExtension : CoveExtensionBase
{
    public const string ExtensionId = "com.cove.api-test-dependency";

    public override string Id => ExtensionId;
    public override string Name => "API Test Dependency";
    public override string Version => "1.0.0";

    public override void ConfigureServices(IServiceCollection services, Cove.Plugins.ExtensionContext context)
    {
    }
}

internal sealed class PlannedFaceSuggester(IConfiguration configuration) : IFaceSuggester
{
    private static readonly JsonSerializerOptions PlanJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _planPath = configuration["Cove:ApiTestFaceSuggestions:PlanPath"];

    public Task<IReadOnlyList<FaceSuggestionDto>> SuggestForAsync(
        int faceId,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = ReadPlan();
        return Task.FromResult<IReadOnlyList<FaceSuggestionDto>>(
            plan.TryGetValue(faceId, out var suggestions) ? suggestions : []);
    }

    public Task<IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>>> SuggestForBatchAsync(
        IReadOnlyCollection<int> faceIds,
        int maxResults,
        FaceSuggestionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = ReadPlan();
        IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>> result = faceIds
            .Where(static faceId => faceId > 0)
            .Distinct()
            .ToDictionary(
                faceId => faceId,
                faceId => (IReadOnlyList<FaceSuggestionDto>)(plan.TryGetValue(faceId, out var suggestions) ? suggestions : []));
        return Task.FromResult(result);
    }

    private IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>> ReadPlan()
    {
        if (string.IsNullOrWhiteSpace(_planPath))
            return EmptyPlan;

        try
        {
            using var stream = File.OpenRead(_planPath);
            return JsonSerializer.Deserialize<Dictionary<int, List<FaceSuggestionDto>>>(stream, PlanJsonOptions)?
                .ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<FaceSuggestionDto>)pair.Value)
                ?? EmptyPlan;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // The test harness atomically replaces this file. Treat an absent/invalid plan as empty
            // so a transient cleanup race cannot turn a face API request into a server error.
            return EmptyPlan;
        }
    }

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>> EmptyPlan =
        new Dictionary<int, IReadOnlyList<FaceSuggestionDto>>();
}
