using Cove.Plugins;

namespace Cove.Sdk;

/// <summary>
/// Fluent builder for constructing a <see cref="UIManifest"/>.
/// Use this in your extension's <see cref="IUIExtension.GetUIManifest"/> implementation
/// for readable, chainable manifest definitions.
/// </summary>
public class UIManifestBuilder
{
    private readonly UIManifest _manifest = new();
    private readonly string _extensionId;

    public UIManifestBuilder(string extensionId)
    {
        _extensionId = extensionId;
    }

    /// <summary>Set the JS bundle URL for this extension's frontend module.</summary>
    public UIManifestBuilder WithJsBundle(string url)
    {
        _manifest.JsBundleUrl = url;
        return this;
    }

    /// <summary>Set the CSS bundle URL.</summary>
    public UIManifestBuilder WithCssBundle(string url)
    {
        _manifest.CssBundleUrl = url;
        return this;
    }

    /// <summary>Set the frontend runtime version (e.g. "v1").</summary>
    public UIManifestBuilder WithRuntimeVersion(string version)
    {
        _manifest.FrontendRuntimeVersion = version;
        return this;
    }

    /// <summary>Register a full page route.</summary>
    public UIManifestBuilder AddPage(
        string route,
        string label,
        string componentName,
        string? icon = null,
        string? detailRoute = null,
        bool showInNav = true,
        int navOrder = 100)
    {
        _manifest.Pages.Add(new UIPageDefinition(
            route, label, icon, detailRoute, showInNav, navOrder,
            ComponentName: componentName, ExtensionId: _extensionId));
        return this;
    }

    /// <summary>Add a page definition while binding ownership to this extension.</summary>
    public UIManifestBuilder AddPage(UIPageDefinition page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _manifest.Pages.Add(page with { ExtensionId = _extensionId });
        return this;
    }

    /// <summary>Inject a component or HTML into a named slot.</summary>
    public UIManifestBuilder AddSlot(
        string slot,
        string componentName,
        string? id = null,
        int order = 100)
    {
        _manifest.Slots.Add(new UISlotContribution(
            id ?? $"{_extensionId}:{slot}",
            slot,
            _extensionId,
            "component",
            componentName,
            Order: order));
        return this;
    }

    /// <summary>Add a tab to an entity detail page.</summary>
    public UIManifestBuilder AddTab(
        string pageType,
        string key,
        string label,
        string componentName,
        int order = 100,
        string? countEndpoint = null,
        string? icon = null,
        string[]? manualContexts = null)
    {
        _manifest.Tabs.Add(new UITabContribution(key, label, pageType, _extensionId, componentName, order, countEndpoint, icon, manualContexts));
        return this;
    }

    /// <summary>Add a tab definition while binding ownership to this extension.</summary>
    public UIManifestBuilder AddTab(UITabContribution tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _manifest.Tabs.Add(tab with { ExtensionId = _extensionId });
        return this;
    }

    /// <summary>Add a pane to a page zone.</summary>
    public UIManifestBuilder AddPane(
        string pageType,
        string zone,
        string componentName,
        string? label = null,
        int order = 100)
    {
        _manifest.Panes.Add(new UIPaneContribution(
            $"{_extensionId}:{zone}", pageType, zone, _extensionId, componentName, label, order));
        return this;
    }

    /// <summary>Expose a UI-consumable feature capability.</summary>
    public UIManifestBuilder AddFeature(string key, Dictionary<string, string>? options = null)
    {
        _manifest.Features.Add(new UIFeatureDefinition(key, _extensionId, options));
        return this;
    }

    /// <summary>Override a host component.</summary>
    public UIManifestBuilder OverrideComponent(
        string targetComponent,
        string componentName,
        int priority = 100)
    {
        _manifest.ComponentOverrides.Add(new UIComponentOverride(targetComponent, _extensionId, componentName, priority));
        return this;
    }

    /// <summary>Add an action (toolbar, context menu, or bulk).</summary>
    public UIManifestBuilder AddAction(
        string id,
        string label,
        string actionType,
        string[] entityTypes,
        string? icon = null,
        string? apiEndpoint = null,
        string? handlerName = null,
        int order = 100,
        bool suppressSuccessAlert = false)
    {
        _manifest.Actions.Add(new ExtensionAction(id, label, _extensionId, actionType, entityTypes, icon, apiEndpoint, handlerName, order, SuppressSuccessAlert: suppressSuccessAlert));
        return this;
    }

    /// <summary>Add an action that is visible only when the current principal has the required permission.</summary>
    public UIManifestBuilder AddAction(
        string id,
        string label,
        string actionType,
        string[] entityTypes,
        string? icon,
        string? apiEndpoint,
        string? handlerName,
        int order,
        string requiredPermission,
        bool suppressSuccessAlert = false)
    {
        _manifest.Actions.Add(new ExtensionAction(id, label, _extensionId, actionType, entityTypes, icon, apiEndpoint, handlerName, order, SuppressSuccessAlert: suppressSuccessAlert)
        {
            RequiredPermission = requiredPermission,
        });
        return this;
    }

    /// <summary>Add a settings panel.</summary>
    public UIManifestBuilder AddSettingsPanel(UISettingsPanel panel)
    {
        _manifest.SettingsPanels.Add(panel);
        return this;
    }

    /// <summary>Add a dedicated tab within the Extensions settings group.</summary>
    public UIManifestBuilder AddSettingsTab(UISettingsTab tab)
    {
        _manifest.SettingsTabs.Add(tab);
        return this;
    }

    /// <summary>Add a dedicated tab within the Extensions settings group.</summary>
    public UIManifestBuilder AddSettingsTab(
        string key,
        string label,
        int order = 100,
        string? icon = null,
        string? parentTabKey = null,
        string? description = null,
        string[]? searchKeywords = null,
        string[]? aliases = null)
    {
        _manifest.SettingsTabs.Add(new UISettingsTab(
            key,
            label,
            _extensionId,
            order,
            icon,
            parentTabKey,
            description,
            searchKeywords,
            aliases));
        return this;
    }

    /// <summary>
    /// Add a dedicated settings tab with an explicit layout. The tab's content comes from the panels
    /// that target it (see <see cref="AddSettingsSection"/>) exactly like the default overload;
    /// <paramref name="layout"/> only controls how the host renders them — stacked in cards
    /// (<see cref="SettingsTabLayout.Panels"/>) or full-width with no card chrome
    /// (<see cref="SettingsTabLayout.Page"/>).
    /// </summary>
    /// <remarks>
    /// A separate overload rather than an added parameter on the overload above: appending an optional
    /// parameter to an existing public method is source- but not binary-compatible, so it would break
    /// extensions already compiled against the prior signature. <paramref name="layout"/> is required
    /// here so the two overloads never resolve ambiguously.
    /// </remarks>
    public UIManifestBuilder AddSettingsTab(
        string key,
        string label,
        SettingsTabLayout layout,
        int order = 100,
        string? icon = null,
        string? parentTabKey = null,
        string? description = null,
        string[]? searchKeywords = null,
        string[]? aliases = null)
    {
        _manifest.SettingsTabs.Add(new UISettingsTab(
            key,
            label,
            _extensionId,
            order,
            icon,
            parentTabKey,
            description,
            searchKeywords,
            aliases)
        {
            Layout = layout,
        });
        return this;
    }

    /// <summary>Add a settings panel to a specific settings tab (e.g. "library", "interface").</summary>
    public UIManifestBuilder AddSettingsSection(
        string targetTab,
        string label,
        string componentName,
        string? id = null,
        int order = 100,
        string? targetSection = null)
    {
        _manifest.SettingsPanels.Add(new UISettingsPanel(
            id ?? $"{_extensionId}:{targetTab}",
            label,
            _extensionId,
            componentName,
            order,
            targetTab,
            targetSection));
        return this;
    }

    /// <summary>Add an in-app tutorial/manual topic with one or more slides.</summary>
    public UIManifestBuilder AddTutorialTopic(
        string id,
        string title,
        string? description = null,
        string[]? pages = null,
        string[]? contexts = null,
        IEnumerable<UITutorialSlide>? slides = null,
        int order = 100,
        string? parentTopicId = null)
    {
        _manifest.TutorialTopics.Add(new UITutorialTopic(
            Id: id,
            Title: title,
            Description: description,
            Pages: pages,
            Contexts: contexts,
            ExtensionId: _extensionId,
            Order: order,
            Slides: slides?.ToList() ?? [],
            ParentTopicId: parentTopicId));
        return this;
    }

    /// <summary>Add a first-class advanced filter row backed by an existing object-filter key.</summary>
    public UIManifestBuilder AddListFilter(
        string entityType,
        string id,
        string label,
        string criterionType,
        string filterKey,
        int order = 100,
        string? entityReferenceType = null,
        IEnumerable<string>? modifiers = null,
        IEnumerable<UIListFilterOption>? options = null)
    {
        _manifest.ListFilters.Add(new UIListFilterContribution(
            id,
            entityType,
            label,
            criterionType,
            _extensionId,
            FilterKey: filterKey,
            EntityReferenceType: entityReferenceType,
            Modifiers: modifiers?.ToList(),
            Options: options?.ToList(),
            Order: order));
        return this;
    }

    /// <summary>Add a first-class advanced filter row backed by a persisted custom field.</summary>
    public UIManifestBuilder AddCustomFieldListFilter(
        string entityType,
        string id,
        string label,
        string customFieldKey,
        string customFieldType,
        string? criterionType = null,
        int order = 100,
        string? entityReferenceType = null,
        IEnumerable<string>? modifiers = null,
        IEnumerable<UIListFilterOption>? options = null)
    {
        _manifest.ListFilters.Add(new UIListFilterContribution(
            id,
            entityType,
            label,
            criterionType ?? customFieldType,
            _extensionId,
            CustomFieldKey: customFieldKey,
            CustomFieldType: customFieldType,
            EntityReferenceType: entityReferenceType,
            Modifiers: modifiers?.ToList(),
            Options: options?.ToList(),
            Order: order));
        return this;
    }

    /// <summary>Add a first-class sort option backed by an existing backend sort key.</summary>
    public UIManifestBuilder AddListSort(
        string entityType,
        string id,
        string label,
        string sortKey,
        int order = 100)
    {
        _manifest.ListSorts.Add(new UIListSortContribution(id, entityType, label, _extensionId, SortKey: sortKey, Order: order));
        return this;
    }

    /// <summary>Add a first-class sort option backed by a persisted custom field.</summary>
    public UIManifestBuilder AddCustomFieldListSort(
        string entityType,
        string id,
        string label,
        string customFieldKey,
        string customFieldType,
        int order = 100)
    {
        _manifest.ListSorts.Add(new UIListSortContribution(
            id,
            entityType,
            label,
            _extensionId,
            CustomFieldKey: customFieldKey,
            CustomFieldType: customFieldType,
            Order: order));
        return this;
    }

    /// <summary>Build the final manifest.</summary>
    public UIManifest Build() => _manifest;
}
