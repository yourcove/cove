using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.ApiTestFaceProvider;

/// <summary>
/// A deliberately small runtime extension used only by API tests. Its plan is owned by the
/// corresponding test host, rather than static state, so the two API-test lanes cannot affect each other.
/// </summary>
public sealed class ApiTestFaceSuggestionExtension : CoveExtensionBase
{
    public override void ConfigureServices(IServiceCollection services, Cove.Plugins.ExtensionContext context)
        => services.AddSingleton<IFaceSuggester, PlannedFaceSuggester>();

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        PublishContributions<IFaceSuggester>(services);
        return Task.CompletedTask;
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
