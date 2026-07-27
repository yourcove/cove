using System.Text.Json;
using System.Diagnostics;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;

namespace Cove.Api.Services;

/// <summary>
/// Validates and composes extension-owned membership predicates. The initial implementation is
/// deliberately bounded; request/result contracts operate on batches so providers remain compatible
/// with a future streaming query planner.
/// </summary>
public sealed class ExtensionEntityFilterService
{
    public const int DefaultBatchSize = ExtensionContributionBatchExecutor.DefaultBatchSize;
    public const int DefaultCriteriaLimit = 16;
    public const int DefaultIdentifierLengthLimit = 256;
    public const int DefaultModifierLengthLimit = 64;
    public const int DefaultValueLengthLimit = 4_096;
    public static readonly TimeSpan DefaultProviderTimeout = ExtensionContributionBatchExecutor.DefaultTimeout;

    private readonly IExtensionEntityFilterRuntime _runtime;
    private readonly ExtensionContributionBatchExecutor _batchExecutor;
    private readonly TimeSpan _providerTimeout;

    public ExtensionEntityFilterService(
        IExtensionEntityFilterRuntime runtime,
        int batchSize = DefaultBatchSize,
        TimeSpan? providerTimeout = null)
    {
        _runtime = runtime;
        _batchExecutor = new ExtensionContributionBatchExecutor(
            batchSize,
            DefaultIdentifierLengthLimit);
        _providerTimeout = providerTimeout ?? DefaultProviderTimeout;
    }

    public async Task<IReadOnlyList<int>> ApplyAsync(
        string entityType,
        IReadOnlyList<ExtensionFilterCriterion> criteria,
        IReadOnlyList<int> orderedCandidateIds,
        CovePrincipal principal,
        CancellationToken ct)
    {
        if (criteria.Count == 0)
            return orderedCandidateIds;
        if (criteria.Count > DefaultCriteriaLimit)
            throw new ExtensionEntityFilterLimitException($"Extension filtering supports at most {DefaultCriteriaLimit} criteria per query.");
        var candidates = orderedCandidateIds.Distinct().ToList();
        var principalContext = new ExtensionFilterPrincipal(
            principal.UserId,
            principal.Username,
            principal.Kind.ToString(),
            principal.Roles.Order(StringComparer.Ordinal).ToArray(),
            principal.Permissions.Order(StringComparer.Ordinal).ToArray());
        using var queryDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        queryDeadline.CancelAfter(_providerTimeout);
        var queryStartedAt = Stopwatch.GetTimestamp();

        foreach (var criterion in criteria)
        {
            ct.ThrowIfCancellationRequested();
            ValidateCriterionShape(criterion);
            IExtensionEntityFilterExecution? execution;
            try
            {
                execution = await _runtime.OpenEntityFilterAsync(
                    criterion.ExtensionId.Trim(),
                    NormalizeEntityType(entityType),
                    criterion.FilterId.Trim(),
                    queryDeadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ExtensionEntityFilterProviderException("The extension filter provider timed out.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ExtensionEntityFilterValidationException(
                    "The extension filter is unavailable, disabled, or undeclared.",
                    ex);
            }
            if (execution is null)
                throw new ExtensionEntityFilterValidationException("The extension filter is unavailable, disabled, or undeclared.");

            using (execution)
            {
                var declaration = ValidateDeclaration(entityType, criterion, execution.Declaration);
                candidates = await ApplyCriterionAsync(
                    declaration,
                    criterion,
                    candidates,
                    principalContext,
                    execution,
                    queryDeadline,
                    queryStartedAt,
                    ct);
            }
            if (candidates.Count == 0)
                break;
        }

        return candidates;
    }

    private async Task<List<int>> ApplyCriterionAsync(
        UIListFilterContribution declaration,
        ExtensionFilterCriterion criterion,
        List<int> candidates,
        ExtensionFilterPrincipal principalContext,
        IExtensionEntityFilterExecution execution,
        CancellationTokenSource queryDeadline,
        long queryStartedAt,
        CancellationToken ct)
    {
        var remaining = _providerTimeout - Stopwatch.GetElapsedTime(queryStartedAt);
        if (remaining <= TimeSpan.Zero)
            throw new ExtensionEntityFilterProviderException("The extension filter provider timed out.");

        IReadOnlyList<ExtensionEntityFilterResult> results;
        try
        {
            results = await _batchExecutor.ExecuteAsync<int, ExtensionEntityFilterRequest, ExtensionEntityFilterResult>(
                candidates,
                execution.ResolveAsync,
                batch => new ExtensionEntityFilterRequest(
                    declaration.ExtensionId,
                    NormalizeEntityType(declaration.EntityType),
                    declaration.FilterId!,
                    NormalizeModifier(criterion.Modifier),
                    criterion.Value,
                    batch,
                    principalContext),
                result => result?.Revision,
                static (batch, result) =>
                {
                    if (result?.MatchingEntityIds is null
                        || result.MatchingEntityIds.Count > batch.Count
                        || result.MatchingEntityIds.Any(id => !batch.Contains(id)))
                    {
                        throw new ExtensionEntityFilterProviderException(
                            "The extension filter provider returned an invalid or oversized membership result.");
                    }
                },
                remaining,
                queryDeadline.Token,
                ct);
        }
        catch (ExtensionContributionTimeoutException)
        {
            throw new ExtensionEntityFilterProviderException("The extension filter provider timed out.");
        }
        catch (ExtensionContributionProviderException ex)
        {
            throw new ExtensionEntityFilterProviderException("The extension filter provider failed.", ex.InnerException ?? ex);
        }
        catch (ExtensionContributionResultException ex)
        {
            throw new ExtensionEntityFilterProviderException(ex.Message, ex);
        }

        var nextMatches = results
            .SelectMany(result => result.MatchingEntityIds)
            .ToHashSet();
        return candidates.Where(nextMatches.Contains).ToList();
    }

    private static void ValidateCriterionShape(ExtensionFilterCriterion criterion)
    {
        if (criterion is null
            || string.IsNullOrWhiteSpace(criterion.ExtensionId)
            || string.IsNullOrWhiteSpace(criterion.FilterId)
            || string.IsNullOrWhiteSpace(criterion.Modifier))
        {
            throw new ExtensionEntityFilterValidationException("Extension filter owner, filter ID, and modifier are required.");
        }
        if (criterion.ExtensionId.Length > DefaultIdentifierLengthLimit
            || criterion.FilterId.Length > DefaultIdentifierLengthLimit
            || criterion.Modifier.Length > DefaultModifierLengthLimit)
        {
            throw new ExtensionEntityFilterValidationException("The extension filter owner, filter ID, or modifier is too long.");
        }
        if (criterion.Value.ValueKind == JsonValueKind.Undefined
            || criterion.Value.GetRawText().Length > DefaultValueLengthLimit)
        {
            throw new ExtensionEntityFilterValidationException("The extension filter value is missing or too large.");
        }
    }

    private static UIListFilterContribution ValidateDeclaration(
        string entityType,
        ExtensionFilterCriterion criterion,
        UIListFilterContribution declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration.FilterId)
            || string.IsNullOrWhiteSpace(declaration.CriterionType))
            throw new ExtensionEntityFilterValidationException("The extension filter is unavailable, disabled, or undeclared.");
        if (!string.Equals(NormalizeEntityType(declaration.EntityType), NormalizeEntityType(entityType), StringComparison.Ordinal))
            throw new ExtensionEntityFilterValidationException("The extension filter does not support this entity type.");

        var modifier = NormalizeModifier(criterion.Modifier);
        var modifiers = declaration.Modifiers?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeModifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(["equals"], StringComparer.OrdinalIgnoreCase);
        if (!modifiers.Contains(modifier))
            throw new ExtensionEntityFilterValidationException("The extension filter modifier is invalid.");

        var criterionType = declaration.CriterionType.Trim().ToLowerInvariant();
        var validValue = criterionType switch
        {
            "bool" or "boolean" => criterion.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => criterion.Value.ValueKind == JsonValueKind.Number && criterion.Value.TryGetInt64(out _),
            "number" => criterion.Value.ValueKind == JsonValueKind.Number,
            "string" or "enum" => criterion.Value.ValueKind == JsonValueKind.String,
            _ => false,
        };
        if (!validValue)
            throw new ExtensionEntityFilterValidationException("The extension filter value does not match its declared type.");

        return declaration;
    }

    private static string NormalizeEntityType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.EndsWith('s') ? normalized : normalized + "s";
    }

    private static string NormalizeModifier(string value)
        => value.Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant() switch
        {
            "equals" => "equals",
            "notequals" => "notEquals",
            "includes" => "includes",
            "excludes" => "excludes",
            var modifier => modifier,
        };

}

public sealed class ExtensionEntityFilterValidationException : Exception
{
    public ExtensionEntityFilterValidationException(string message) : base(message) { }
    public ExtensionEntityFilterValidationException(string message, Exception inner) : base(message, inner) { }
}
public sealed class ExtensionEntityFilterLimitException(string message) : Exception(message);
public sealed class ExtensionEntityFilterProviderException : Exception
{
    public ExtensionEntityFilterProviderException(string message) : base(message) { }
    public ExtensionEntityFilterProviderException(string message, Exception inner) : base(message, inner) { }
}
