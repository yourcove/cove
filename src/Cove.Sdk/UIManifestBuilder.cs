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
        string? icon = null)
    {
        _manifest.Tabs.Add(new UITabContribution(key, label, pageType, _extensionId, componentName, order, countEndpoint, icon));
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
        int order = 100)
    {
        _manifest.Actions.Add(new ExtensionAction(id, label, _extensionId, actionType, entityTypes, icon, apiEndpoint, handlerName, order));
        return this;
    }

    /// <summary>Add a settings panel.</summary>
    public UIManifestBuilder AddSettingsPanel(UISettingsPanel panel)
    {
        _manifest.SettingsPanels.Add(panel);
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

    /// <summary>Build the final manifest.</summary>
    public UIManifest Build() => _manifest;
}
