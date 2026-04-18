using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Plugins;

// ============================================================================
// CORE EXTENSION INTERFACE
// ============================================================================

/// <summary>
/// Base interface for all Cove extensions. Every extension must implement this.
/// Extensions can optionally implement additional capability interfaces.
/// </summary>
public interface IExtension
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string? Description { get; }
    string? Author { get; }
    string? IconUrl { get; }

    void ConfigureServices(IServiceCollection services, ExtensionContext context);
    Task InitializeAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;
    Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Runtime context available to extensions during their lifecycle.
/// </summary>
public class ExtensionContext
{
    public required IConfiguration Configuration { get; init; }
    public required string DataDirectory { get; init; }
    public UIRegistry UI { get; } = new();
}

// ============================================================================
// CAPABILITY INTERFACES — Extensions opt into capabilities by implementing these
// ============================================================================

/// <summary>Register custom HTTP API endpoints.</summary>
public interface IApiExtension : IExtension
{
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

/// <summary>Contribute to the frontend UI via the manifest system.</summary>
public interface IUIExtension : IExtension
{
    UIManifest GetUIManifest();
}

/// <summary>
/// Extension with persistent key-value storage backed by the Cove database.
/// The ExtensionManager provides the IExtensionStore implementation.
/// </summary>
public interface IStatefulExtension : IExtension
{
    void SetStore(IExtensionStore store);
}

/// <summary>
/// Simple key-value store scoped to an extension. Backed by the extension_data table.
/// </summary>
public interface IExtensionStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default);
}

/// <summary>
/// Extension that can register and run background jobs/tasks.
/// </summary>
public interface IJobExtension : IExtension
{
    IReadOnlyList<ExtensionJobDefinition> Jobs { get; }
    Task RunJobAsync(string jobId, IReadOnlyDictionary<string, string>? parameters, IJobProgress progress, CancellationToken ct);
}

/// <summary>Progress reporter for extension jobs.</summary>
public interface IJobProgress
{
    void Report(double percent, string? message = null);
}

/// <summary>Metadata definition for an extension job.</summary>
public record ExtensionJobDefinition(
    string Id,
    string Name,
    string? Description = null,
    bool SupportsParameters = false
);

/// <summary>
/// Extension that subscribes to entity lifecycle events (pre/post CRUD).
/// </summary>
public interface IEventExtension : IExtension
{
    Task OnEventAsync(ExtensionEvent evt, CancellationToken ct = default);
}

/// <summary>An entity lifecycle event dispatched to extensions.</summary>
public record ExtensionEvent(
    string EventType,  // "scene.created", "performer.updated", "tag.deleted", etc.
    string EntityType, // "scene", "performer", "studio", "tag", "gallery", "image", "group"
    int EntityId,
    Dictionary<string, object?>? Data = null
);

// ============================================================================
// UI MANIFEST — Declares all frontend contributions from an extension
// ============================================================================

/// <summary>Complete UI manifest describing all frontend contributions.</summary>
public class UIManifest
{
    public List<UIPageDefinition> Pages { get; set; } = [];
    public List<UISlotContribution> Slots { get; set; } = [];
    public List<UITabContribution> Tabs { get; set; } = [];
    public List<UIThemeDefinition> Themes { get; set; } = [];
    public List<UIComponentStyleDef> ComponentStyles { get; set; } = [];
    public List<UILayoutStyleDef> LayoutStyles { get; set; } = [];
    public List<UISettingsPanel> SettingsPanels { get; set; } = [];
    public List<UIPageOverride> PageOverrides { get; set; } = [];
    public List<UIDialogOverride> DialogOverrides { get; set; } = [];

    /// <summary>URL of the extension's JS module (ESM) loaded by the frontend runtime.</summary>
    public string? JsBundleUrl { get; set; }
    /// <summary>URL of additional CSS to load.</summary>
    public string? CssBundleUrl { get; set; }
}

/// <summary>A new page contributed by an extension.</summary>
public record UIPageDefinition(
    string Route,
    string Label,
    string? Icon = null,
    string? DetailRoute = null,
    bool ShowInNav = true,
    int NavOrder = 100,
    string? RequiredPermission = null,
    /// <summary>The exported React component name from the JS bundle.</summary>
    string? ComponentName = null,
    /// <summary>The extension ID owning this page.</summary>
    string? ExtensionId = null
);

/// <summary>Inject content into a named slot on any page.</summary>
public record UISlotContribution(
    string Id,
    string Slot,
    string ExtensionId,
    /// <summary>"component" (React) or "html" (raw HTML).</summary>
    string ContentType = "component",
    string? ComponentName = null,
    string? Html = null,
    int Order = 100
);

/// <summary>Add a tab to an entity detail page.</summary>
public record UITabContribution(
    string Key,
    string Label,
    /// <summary>"scene", "performer", "studio", "tag", "gallery", "image", "group", "settings"</summary>
    string PageType,
    string ExtensionId,
    string ComponentName,
    int Order = 100
);

/// <summary>Theme definition with CSS variable overrides and optional style/layout layers.</summary>
public record UIThemeDefinition(
    string Id,
    string Name,
    string? Description = null,
    Dictionary<string, string>? CssVariables = null,
    string? CssUrl = null,
    /// <summary>Layer 2: Component style preset id (e.g. "default", "glass", "minimal", "rounded").</summary>
    string? ComponentStyle = null,
    /// <summary>Layer 3: Layout preset id (e.g. "default", "compact").</summary>
    string? LayoutStyle = null,
    /// <summary>CSS animation name for the background effect (e.g. "liquid-drift"). Applied via data-theme-bg-animation attribute.</summary>
    string? BackgroundAnimation = null,
    /// <summary>Color scheme hint: "light" or "dark" (default). Applied via data-color-scheme attribute for light/dark mode CSS.</summary>
    string? ColorScheme = null
);

/// <summary>Built-in component style presets (Layer 2).</summary>
public record UIComponentStyleDef(
    string Id,
    string Name,
    string? Description = null
);

/// <summary>Built-in layout presets (Layer 3).</summary>
public record UILayoutStyleDef(
    string Id,
    string Name,
    string? Description = null
);

/// <summary>A settings panel contributed by an extension, shown in the Extensions settings tab.</summary>
public record UISettingsPanel(
    string Id,
    string Label,
    string ExtensionId,
    string ComponentName,
    int Order = 100
);

/// <summary>
/// Replace an existing built-in page with an extension's component.
/// This is how extensions achieve full page replacement.
/// </summary>
public record UIPageOverride(
    /// <summary>The built-in page key to replace (e.g. "scenes", "home", "settings").</summary>
    string TargetPage,
    string ExtensionId,
    string ComponentName,
    /// <summary>Priority — highest priority wins if multiple extensions override the same page.</summary>
    int Priority = 100
);

/// <summary>
/// Override or wrap a dialog/modal component.
/// </summary>
public record UIDialogOverride(
    /// <summary>Dialog identifier (e.g. "scene-edit", "performer-edit", "confirm-delete").</summary>
    string DialogId,
    string ExtensionId,
    string ComponentName,
    int Priority = 100
);

// ============================================================================
// UI REGISTRY — Aggregates contributions before serialization
// ============================================================================

/// <summary>Central registry for UI contributions aggregated from all extensions.</summary>
public class UIRegistry
{
    private readonly List<UIPageDefinition> _pages = [];
    private readonly List<UISlotContribution> _slots = [];
    private readonly List<UITabContribution> _tabs = [];
    private readonly List<UIThemeDefinition> _themes = [];
    private readonly List<UIComponentStyleDef> _componentStyles = [];
    private readonly List<UILayoutStyleDef> _layoutStyles = [];
    private readonly List<UISettingsPanel> _settingsPanels = [];
    private readonly List<UIPageOverride> _pageOverrides = [];
    private readonly List<UIDialogOverride> _dialogOverrides = [];

    public IReadOnlyList<UIPageDefinition> Pages => _pages;
    public IReadOnlyList<UISlotContribution> Slots => _slots;
    public IReadOnlyList<UITabContribution> Tabs => _tabs;
    public IReadOnlyList<UIThemeDefinition> Themes => _themes;
    public IReadOnlyList<UIComponentStyleDef> ComponentStyles => _componentStyles;
    public IReadOnlyList<UILayoutStyleDef> LayoutStyles => _layoutStyles;
    public IReadOnlyList<UISettingsPanel> SettingsPanels => _settingsPanels;
    public IReadOnlyList<UIPageOverride> PageOverrides => _pageOverrides;
    public IReadOnlyList<UIDialogOverride> DialogOverrides => _dialogOverrides;

    public void RegisterPage(UIPageDefinition page) => _pages.Add(page);
    public void RegisterSlot(UISlotContribution slot) => _slots.Add(slot);
    public void RegisterTab(UITabContribution tab) => _tabs.Add(tab);
    public void RegisterTheme(UIThemeDefinition theme) => _themes.Add(theme);
    public void RegisterComponentStyle(UIComponentStyleDef style) => _componentStyles.Add(style);
    public void RegisterLayoutStyle(UILayoutStyleDef layout) => _layoutStyles.Add(layout);
    public void RegisterSettingsPanel(UISettingsPanel panel) => _settingsPanels.Add(panel);
    public void RegisterPageOverride(UIPageOverride ov) => _pageOverrides.Add(ov);
    public void RegisterDialogOverride(UIDialogOverride ov) => _dialogOverrides.Add(ov);

    public UIManifest ToManifest() => new()
    {
        Pages = [.. _pages],
        Slots = [.. _slots],
        Tabs = [.. _tabs],
        Themes = [.. _themes],
        ComponentStyles = [.. _componentStyles],
        LayoutStyles = [.. _layoutStyles],
        SettingsPanels = [.. _settingsPanels],
        PageOverrides = [.. _pageOverrides],
        DialogOverrides = [.. _dialogOverrides],
    };
}
