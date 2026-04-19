using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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
    /// <summary>Unique extension identifier (reverse-domain recommended: "com.author.name").</summary>
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string? Description { get; }
    string? Author { get; }
    string? Url { get; }
    string? IconUrl { get; }

    /// <summary>
    /// Categories/labels describing what this extension does.
    /// Use well-known categories from <see cref="ExtensionCategories"/> when applicable,
    /// plus any custom labels (e.g. "recommendation-system", "ai").
    /// Used for filtering/sorting in the UI and registry.
    /// </summary>
    IReadOnlyList<string> Categories => [];

    /// <summary>
    /// Minimum Cove core version this extension requires (semver, e.g. "1.0.0").
    /// Null means compatible with any version.
    /// </summary>
    string? MinCoveVersion => null;

    /// <summary>
    /// Extensions this extension depends on. Key = extension ID, Value = semver range (e.g. ">=1.0.0").
    /// </summary>
    IReadOnlyDictionary<string, string> Dependencies => new Dictionary<string, string>();

    void ConfigureServices(IServiceCollection services, ExtensionContext context);
    Task InitializeAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;
    Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Called once after first install. Extensions can seed data, create config, etc.</summary>
    Task OnInstallAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Called when the extension is being uninstalled. Clean up non-DB resources.</summary>
    Task OnUninstallAsync(IServiceProvider services, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Well-known extension categories. Extensions can also declare custom categories.
/// </summary>
public static class ExtensionCategories
{
    public const string Theme = "theme";
    public const string ColorPalette = "color-palette";
    public const string Style = "style";
    public const string Layout = "layout";
    public const string Analytics = "analytics";
    public const string Tools = "tools";
    public const string Library = "library";
    public const string Scraper = "scraper";
    public const string Metadata = "metadata";
    public const string Integration = "integration";
    public const string Automation = "automation";
    public const string UI = "ui";
    public const string ContentManagement = "content-management";
    public const string Search = "search";
    public const string Import = "import";
    public const string Export = "export";
    public const string Notification = "notification";
    public const string Security = "security";
    public const string MediaPlayer = "media-player";
}

/// <summary>
/// Runtime context available to extensions during their lifecycle.
/// </summary>
public class ExtensionContext
{
    public required IConfiguration Configuration { get; init; }
    public required string DataDirectory { get; init; }
    public required string CoveVersion { get; init; }
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

/// <summary>
/// Extension that contributes its own database tables via EF Core migrations.
/// Extensions MUST NOT drop core tables or remove core columns.
/// Extensions CAN add new tables and add columns to core tables (via raw SQL migrations).
/// </summary>
public interface IDataExtension : IExtension
{
    /// <summary>
    /// Configure the extension's entity model. Called during OnModelCreating.
    /// Use this to define new tables owned by the extension.
    /// </summary>
    void ConfigureModel(ModelBuilder modelBuilder);

    /// <summary>
    /// Return SQL migration scripts to apply, keyed by a unique migration name.
    /// These are tracked in extension_migrations and only applied once.
    /// Use this for adding columns to core tables or complex schema changes.
    /// Order matters — migrations are applied in dictionary order.
    /// </summary>
    IReadOnlyList<ExtensionMigration> GetMigrations() => [];
}

/// <summary>A named SQL migration for an extension.</summary>
public record ExtensionMigration(
    /// <summary>Unique name for this migration (e.g. "001_add_recommendation_scores").</summary>
    string Name,
    /// <summary>SQL to apply this migration (forward only).</summary>
    string UpSql
);

/// <summary>
/// Extension that provides middleware to intercept and modify HTTP requests/responses.
/// Useful for adding auth, logging, response transformation, etc.
/// </summary>
public interface IMiddlewareExtension : IExtension
{
    /// <summary>
    /// Configure middleware in the pipeline. Called during app.UseRouting() phase.
    /// Extensions should call next() to continue the pipeline.
    /// </summary>
    void ConfigureMiddleware(IApplicationBuilder app);
}

/// <summary>
/// Extension that contributes toolbar actions, context menu items, and bulk actions.
/// These appear in the UI based on context (entity type, selection state, page).
/// </summary>
public interface IActionExtension : IExtension
{
    IReadOnlyList<ExtensionAction> GetActions();
}

/// <summary>An action contributed by an extension (toolbar button, context menu item, bulk action).</summary>
public record ExtensionAction(
    string Id,
    string Label,
    string ExtensionId,
    /// <summary>"toolbar", "context-menu", "bulk"</summary>
    string ActionType,
    /// <summary>Entity types this action applies to (empty = all). E.g. ["scene", "performer"]</summary>
    string[] EntityTypes,
    /// <summary>Icon name (lucide icon).</summary>
    string? Icon = null,
    /// <summary>
    /// If set, clicking invokes the extension's API endpoint. Otherwise triggers a JS handler.
    /// </summary>
    string? ApiEndpoint = null,
    /// <summary>JS handler function name exported from the extension bundle.</summary>
    string? HandlerName = null,
    int Order = 100,
    /// <summary>Only show in these pages (empty = show everywhere applicable).</summary>
    string[]? Pages = null
);

// ============================================================================
// EXTENSION MANIFEST FILE — extension.json schema
// ============================================================================

/// <summary>
/// Deserialized from extension.json in each extension directory.
/// Provides metadata, dependencies, and asset paths before the DLL is loaded.
/// </summary>
public class ExtensionManifestFile
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Url { get; set; }
    public string? IconUrl { get; set; }
    /// <summary>Minimum Cove version required (semver).</summary>
    public string? MinCoveVersion { get; set; }
    /// <summary>Extension dependencies: ID → semver range (e.g. ">=1.0.0").</summary>
    public Dictionary<string, string> Dependencies { get; set; } = [];
    /// <summary>The DLL filename containing the IExtension implementation.</summary>
    public string? EntryDll { get; set; }
    /// <summary>Relative path to the frontend JS bundle (ESM module).</summary>
    public string? JsBundle { get; set; }
    /// <summary>Relative path to the frontend CSS file.</summary>
    public string? CssBundle { get; set; }
    /// <summary>Categories/labels for filtering and sorting.</summary>
    public List<string> Categories { get; set; } = [];
    /// <summary>SHA-256 checksum of the extension package (set by registry).</summary>
    public string? Checksum { get; set; }
    /// <summary>Registry source URL (set when installed from remote registry).</summary>
    public string? RegistryUrl { get; set; }
}

// ============================================================================
// INSTALLATION STATE — tracked in the database
// ============================================================================

/// <summary>
/// Tracks the installation state of an extension in the database.
/// </summary>
public class ExtensionInstallation
{
    public required string ExtensionId { get; set; }
    public required string Version { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>JSON blob of the extension.json manifest for reference.</summary>
    public string? ManifestJson { get; set; }
    /// <summary>Source: "local", "registry", "builtin"</summary>
    public string Source { get; set; } = "local";
    /// <summary>Comma-separated categories/labels for filtering.</summary>
    public string? Categories { get; set; }
}

/// <summary>
/// Tracks which extension migrations have been applied.
/// </summary>
public class ExtensionMigrationRecord
{
    public required string ExtensionId { get; set; }
    public required string MigrationName { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}

// ============================================================================
// UI MANIFEST — Declares all frontend contributions from an extension
// ============================================================================

/// <summary>Complete UI manifest describing all frontend contributions.</summary>
public class UIManifest
{
    public List<UIPageDefinition> Pages { get; set; } = [];
    public List<UISlotContribution> Slots { get; set; } = [];
    public List<UITabContribution> Tabs { get; set; } = [];
    public List<UIPaneContribution> Panes { get; set; } = [];
    public List<UIComponentOverride> ComponentOverrides { get; set; } = [];
    public List<UISelectorOverride> SelectorOverrides { get; set; } = [];
    public List<UIThemeDefinition> Themes { get; set; } = [];
    public List<UIComponentStyleDef> ComponentStyles { get; set; } = [];
    public List<UILayoutStyleDef> LayoutStyles { get; set; } = [];
    public List<UISettingsPanel> SettingsPanels { get; set; } = [];
    public List<UIPageOverride> PageOverrides { get; set; } = [];
    public List<UIDialogOverride> DialogOverrides { get; set; } = [];
    public List<ExtensionAction> Actions { get; set; } = [];

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

/// <summary>Add a panel/pane region contribution to a page layout.</summary>
public record UIPaneContribution(
    string Id,
    /// <summary>"scene", "performer", "studio", "tag", "gallery", "image", "group", "settings", "home"</summary>
    string PageType,
    /// <summary>Host-defined zone key where this pane should render (e.g. "sidebar-right", "hero", "details").</summary>
    string Zone,
    string ExtensionId,
    string ComponentName,
    string? Label = null,
    int Order = 100
);

/// <summary>Override a host component by stable key (selectors, cards, toolbars, list rows, etc.).</summary>
public record UIComponentOverride(
    /// <summary>Host-defined component key (e.g. "scene.selector", "performer.card", "search.bar").</summary>
    string TargetComponent,
    string ExtensionId,
    string ComponentName,
    int Priority = 100
);

/// <summary>Replace a host selector implementation with an extension selector component.</summary>
public record UISelectorOverride(
    /// <summary>Host-defined selector key (e.g. "tag-selector", "performer-selector").</summary>
    string SelectorKey,
    string ExtensionId,
    string ComponentName,
    int Priority = 100
);

/// <summary>Theme definition with CSS variable overrides and optional style/layout layers.</summary>
public record UIThemeDefinition(
    string Id,
    string Name,
    string? Description = null,
    Dictionary<string, string>? CssVariables = null,
    string? CssUrl = null,
    string? ComponentStyle = null,
    string? LayoutStyle = null,
    string? BackgroundAnimation = null,
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
/// This is how extensions achieve full page replacement or complete UI overhaul.
/// Use TargetPage = "*" to override the entire app shell.
/// </summary>
public record UIPageOverride(
    /// <summary>The built-in page key to replace (e.g. "scenes", "home", "settings", or "*" for full shell).</summary>
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
    private readonly List<UIPaneContribution> _panes = [];
    private readonly List<UIComponentOverride> _componentOverrides = [];
    private readonly List<UISelectorOverride> _selectorOverrides = [];
    private readonly List<UIThemeDefinition> _themes = [];
    private readonly List<UIComponentStyleDef> _componentStyles = [];
    private readonly List<UILayoutStyleDef> _layoutStyles = [];
    private readonly List<UISettingsPanel> _settingsPanels = [];
    private readonly List<UIPageOverride> _pageOverrides = [];
    private readonly List<UIDialogOverride> _dialogOverrides = [];
    private readonly List<ExtensionAction> _actions = [];

    public IReadOnlyList<UIPageDefinition> Pages => _pages;
    public IReadOnlyList<UISlotContribution> Slots => _slots;
    public IReadOnlyList<UITabContribution> Tabs => _tabs;
    public IReadOnlyList<UIPaneContribution> Panes => _panes;
    public IReadOnlyList<UIComponentOverride> ComponentOverrides => _componentOverrides;
    public IReadOnlyList<UISelectorOverride> SelectorOverrides => _selectorOverrides;
    public IReadOnlyList<UIThemeDefinition> Themes => _themes;
    public IReadOnlyList<UIComponentStyleDef> ComponentStyles => _componentStyles;
    public IReadOnlyList<UILayoutStyleDef> LayoutStyles => _layoutStyles;
    public IReadOnlyList<UISettingsPanel> SettingsPanels => _settingsPanels;
    public IReadOnlyList<UIPageOverride> PageOverrides => _pageOverrides;
    public IReadOnlyList<UIDialogOverride> DialogOverrides => _dialogOverrides;
    public IReadOnlyList<ExtensionAction> Actions => _actions;

    public void RegisterPage(UIPageDefinition page) => _pages.Add(page);
    public void RegisterSlot(UISlotContribution slot) => _slots.Add(slot);
    public void RegisterTab(UITabContribution tab) => _tabs.Add(tab);
    public void RegisterPane(UIPaneContribution pane) => _panes.Add(pane);
    public void RegisterComponentOverride(UIComponentOverride ov) => _componentOverrides.Add(ov);
    public void RegisterSelectorOverride(UISelectorOverride ov) => _selectorOverrides.Add(ov);
    public void RegisterTheme(UIThemeDefinition theme) => _themes.Add(theme);
    public void RegisterComponentStyle(UIComponentStyleDef style) => _componentStyles.Add(style);
    public void RegisterLayoutStyle(UILayoutStyleDef layout) => _layoutStyles.Add(layout);
    public void RegisterSettingsPanel(UISettingsPanel panel) => _settingsPanels.Add(panel);
    public void RegisterPageOverride(UIPageOverride ov) => _pageOverrides.Add(ov);
    public void RegisterDialogOverride(UIDialogOverride ov) => _dialogOverrides.Add(ov);
    public void RegisterAction(ExtensionAction action) => _actions.Add(action);

    public UIManifest ToManifest() => new()
    {
        Pages = [.. _pages],
        Slots = [.. _slots],
        Tabs = [.. _tabs],
        Panes = [.. _panes],
        ComponentOverrides = [.. _componentOverrides],
        SelectorOverrides = [.. _selectorOverrides],
        Themes = [.. _themes],
        ComponentStyles = [.. _componentStyles],
        LayoutStyles = [.. _layoutStyles],
        SettingsPanels = [.. _settingsPanels],
        PageOverrides = [.. _pageOverrides],
        DialogOverrides = [.. _dialogOverrides],
        Actions = [.. _actions],
    };
}
