using Microsoft.Extensions.DependencyInjection;

namespace Cove.Plugins;

/// <summary>
/// Adapts entity-membership predicates onto the generic namespaced contribution runtime. Entity
/// authorization and candidate selection remain the responsibility of the Cove-owned caller.
/// </summary>
internal sealed class ExtensionEntityFilterRuntime(IExtensionContributionRuntime contributions)
    : IExtensionEntityFilterRuntime
{
    public async Task<IExtensionEntityFilterExecution?> OpenEntityFilterAsync(
        string extensionId,
        string entityType,
        string filterId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            return null;

        var normalizedEntityType = NormalizeEntityType(entityType);
        var execution = await contributions.OpenContributionAsync<
            UIListFilterContribution,
            ExtensionEntityFilterRequest,
            ExtensionEntityFilterResult>(
            extensionId,
            filterId,
            (extension, services, contributionId) => Bind(
                extension,
                services,
                normalizedEntityType,
                contributionId),
            ct);

        return execution is null ? null : new EntityFilterExecution(execution);
    }

    private static ExtensionContributionBinding<
        UIListFilterContribution,
        ExtensionEntityFilterRequest,
        ExtensionEntityFilterResult>? Bind(
        IExtension extension,
        IServiceProvider services,
        string entityType,
        string filterId)
    {
        if (extension is not IUIExtension uiExtension)
            return null;

        var declaration = uiExtension.GetUIManifest().ListFilters
            .Where(filter => !string.IsNullOrWhiteSpace(filter.FilterId))
            .FirstOrDefault(filter =>
                string.Equals(NormalizeEntityType(filter.EntityType), entityType, StringComparison.Ordinal)
                && string.Equals(filter.FilterId!.Trim(), filterId, StringComparison.OrdinalIgnoreCase));
        if (declaration is null)
            return null;

        var provider = services.GetKeyedServices<IExtensionEntityFilterProvider>(extension.Id)
            .SingleOrDefault(candidate => candidate.Filters.Any(definition =>
                string.Equals(definition.FilterId.Trim(), filterId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeEntityType(definition.EntityType), entityType, StringComparison.Ordinal)));
        if (provider is null)
            return null;

        var ownedDeclaration = declaration with
        {
            ExtensionId = extension.Id,
            EntityType = entityType,
            FilterId = filterId,
        };
        return new ExtensionContributionBinding<
            UIListFilterContribution,
            ExtensionEntityFilterRequest,
            ExtensionEntityFilterResult>(
            ownedDeclaration,
            provider.ResolveAsync);
    }

    private static string NormalizeEntityType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.EndsWith('s') ? normalized : normalized + "s";
    }

    private sealed class EntityFilterExecution(
        IExtensionContributionExecution<
            UIListFilterContribution,
            ExtensionEntityFilterRequest,
            ExtensionEntityFilterResult> inner) : IExtensionEntityFilterExecution
    {
        public UIListFilterContribution Declaration => inner.Declaration;

        public Task<ExtensionEntityFilterResult> ResolveAsync(
            ExtensionEntityFilterRequest request,
            CancellationToken ct)
            => inner.ExecuteAsync(request, ct);

        public void Dispose() => inner.Dispose();
    }
}
