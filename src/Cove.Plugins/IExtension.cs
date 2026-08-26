using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;
using System.Text.Json;
using Cove.Core.Auth;

namespace Cove.Plugins;

// ============================================================================
// CORE EXTENSION INTERFACE
// ============================================================================

/// <summary>
/// Base interface for all Cove extensions. Every extension must implement this.
/// Extensions can optionally implement additional capability interfaces.
/// Identity, version, category, dependency, and other descriptive properties must be immutable and
/// provider-independent because the host reads them before initialization and while disabled.
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
    /// Minimum Cove host version this extension requires (e.g. "1.1.0" or "1.1.1-dev.12").
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
/// Creates extension-owned service scopes that participate in provider-generation retirement.
/// Inject this contract into extension services instead of the container engine's
/// <see cref="IServiceScopeFactory"/>.
/// </summary>
public interface IExtensionServiceScopeFactory
{
    IServiceScope CreateScope();
    AsyncServiceScope CreateAsyncScope();
}

/// <summary>
/// Implemented by extension base classes that source their metadata (Id, Name, Version, Description,
/// Author, Url, Categories, dependencies, …) from the loaded <c>extension.json</c> manifest instead of
/// re-declaring it in code. The host calls <see cref="ApplyManifest"/> immediately after constructing
/// the extension instance and before any metadata property is read, so the manifest is the single
/// source of truth and nothing is duplicated between code and manifest.
/// </summary>
public interface IManifestAware
{
    /// <summary>Provide the parsed extension.json manifest for this extension instance.</summary>
    void ApplyManifest(ExtensionManifestFile manifest);
}

/// <summary>
/// Well-known extension categories. Extensions can also declare custom categories.
/// </summary>
public static class ExtensionCategories
{
    public const string AI = "ai";
    public const string Theme = "theme";
    public const string ColorPalette = "color-palette";
    public const string Style = "style";
    public const string Layout = "layout";
    public const string Analytics = "analytics";
    public const string Tools = "tools";
    public const string Library = "library";
    public const string Scraper = "scraper";
    public const string Metadata = "metadata";
    public const string MetadataConsumer = "metadata-consumer";
    public const string Integration = "integration";
    public const string Automation = "automation";
    public const string UI = "ui";
    public const string ContentManagement = "content-management";
    public const string Search = "search";
    public const string Import = "import";
    public const string Export = "export";
    public const string Downloader = "downloader";
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
    /// <summary>Return provider-independent UI declarations; this may be called while disabled.</summary>
    UIManifest GetUIManifest();
}

/// <summary>Marker for extension-owned provider services resolved by their real host-stamped owner.</summary>
public interface IExtensionContributionProvider { }

/// <summary>
/// Resolves extension-owned predicates against host-authorized entity candidates. Providers return
/// only membership; Cove retains ownership of authorization, query composition, sorting and paging.
/// Cove owner-keys contribution-provider registrations; extension services that also consume their
/// implementation should inject its concrete type rather than this discovery interface.
/// Resolve callbacks execute against the exact provider generation captured for the criterion and
/// must observe cancellation. Disable or replacement may retire that generation concurrently, but
/// the host keeps its provider scope alive until every in-flight callback has completed.
/// </summary>
public interface IExtensionEntityFilterProvider : IExtensionContributionProvider
{
    IReadOnlyCollection<ExtensionEntityFilterDefinition> Filters { get; }
    Task<ExtensionEntityFilterResult> ResolveAsync(ExtensionEntityFilterRequest request, CancellationToken ct);
}

public sealed record ExtensionEntityFilterDefinition(string FilterId, string EntityType);

public sealed record ExtensionFilterPrincipal(
    int? UserId,
    string Username,
    string Kind,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions);

public sealed record ExtensionEntityFilterRequest(
    string ExtensionId,
    string EntityType,
    string FilterId,
    string Modifier,
    System.Text.Json.JsonElement Value,
    IReadOnlyList<int> CandidateIds,
    ExtensionFilterPrincipal Principal);

public sealed record ExtensionEntityFilterResult(
    IReadOnlyCollection<int> MatchingEntityIds,
    string Revision);

/// <summary>A namespaced contribution owned by one extension.</summary>
internal sealed record ExtensionContributionKey(string ExtensionId, string ContributionId);

/// <summary>
/// A contribution binding captured from one exact extension/provider generation. The runtime owns
/// the provider scope and keeps it alive until all calls that were started through the execution
/// have completed, including calls that outlive a host timeout.
/// </summary>
internal sealed record ExtensionContributionBinding<TDeclaration, TRequest, TResult>(
    TDeclaration Declaration,
    Func<TRequest, CancellationToken, Task<TResult>> ExecuteAsync);

/// <summary>An exact-generation contribution execution.</summary>
internal interface IExtensionContributionExecution<TDeclaration, TRequest, TResult> : IDisposable
{
    ExtensionContributionKey Key { get; }
    TDeclaration Declaration { get; }
    Task<TResult> ExecuteAsync(TRequest request, CancellationToken ct);
}

/// <summary>
/// Host runtime for acquiring namespaced extension contributions. Implementations resolve and pin
/// the exact provider generation under the owner's lifecycle gate, then release that gate before
/// returning the execution.
/// </summary>
internal interface IExtensionContributionRuntime
{
    Task<IExtensionContributionExecution<TDeclaration, TRequest, TResult>?>
        OpenContributionAsync<TDeclaration, TRequest, TResult>(
        string extensionId,
        string contributionId,
        Func<IExtension, IServiceProvider, string, ExtensionContributionBinding<TDeclaration, TRequest, TResult>?> bind,
        CancellationToken ct);
}

/// <summary>
/// One owner-stamped filter declaration and exact provider generation captured for a criterion.
/// Dispose the execution after all batches have been scheduled; implementations retain their
/// provider scope until any timed-out in-flight work actually completes.
/// </summary>
public interface IExtensionEntityFilterExecution : IDisposable
{
    UIListFilterContribution Declaration { get; }
    Task<ExtensionEntityFilterResult> ResolveAsync(ExtensionEntityFilterRequest request, CancellationToken ct);
}

/// <summary>Host runtime used by the query orchestrator. Contributions are always owner-stamped.</summary>
public interface IExtensionEntityFilterRuntime
{
    Task<IExtensionEntityFilterExecution?> OpenEntityFilterAsync(
        string extensionId,
        string entityType,
        string filterId,
        CancellationToken ct);
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
    /// <summary>Provider-independent job declarations; the host may read them while disabled.</summary>
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
)
{
    public bool ShowInTaskList { get; init; }
}

/// <summary>
/// Extension that subscribes to post-persistence entity lifecycle and rating events.
/// </summary>
public interface IEventExtension : IExtension
{
    Task OnEventAsync(ExtensionEvent evt, CancellationToken ct = default);
}

/// <summary>An entity lifecycle event dispatched to extensions.</summary>
public record ExtensionEvent(
    string EventType,  // "video.created", "performer.updated", "tag.deleted", etc.
    string EntityType, // "video", "performer", "studio", "tag", "gallery", "image", "group", "audio", "text"
    int EntityId,
    Dictionary<string, object?>? Data = null
)
{
    /// <summary>
    /// Stable noun.verb name used by new handlers. <see cref="EventType"/> keeps the original wire name
    /// when changing it would break an existing extension.
    /// </summary>
    public string CanonicalEventType { get; init; } = EventType;
}

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
/// Extension that intercepts HTTP requests/responses (auth, logging, header/response transforms, …).
/// The host invokes <see cref="InvokeAsync"/> for every request, chaining all enabled middleware
/// extensions ahead of routing; call <paramref name="next"/> to continue the pipeline, or skip it to
/// short-circuit. Because the host drives this through a single persistent dispatcher, middleware from
/// an extension installed at runtime takes effect immediately — no host restart. Resolve the
/// extension's own services from <see cref="System.IServiceProvider"/> captured during initialization
/// (or the cross-extension exchange); <c>HttpContext.RequestServices</c> here is the host request scope.
/// </summary>
public interface IMiddlewareExtension : IExtension
{
    Task InvokeAsync(HttpContext context, RequestDelegate next);
}

/// <summary>
/// Extension that runs a long-lived background worker managed by the host. The host starts
/// <see cref="RunAsync"/> after the extension initializes and cancels the token when the extension is
/// disabled, uninstalled, or the host shuts down. Implementations should loop until the token is
/// cancelled and use <see cref="IExtensionServiceScopeFactory"/> to create a tracked scope per unit
/// of work for scoped services. Persist durable state via the extension store — in-memory state is
/// reset if the extension's container is rebuilt.
/// <see cref="RunAsync"/> and any code it awaits must not invoke or await lifecycle operations,
/// worker-stop operations, or host-wide extension shutdown for the same extension: the host drains
/// the worker while holding that extension's lifecycle gate.
/// </summary>
public interface IBackgroundExtension : IExtension
{
    Task RunAsync(IServiceProvider services, CancellationToken ct);
}

/// <summary>
/// Extension that participates in the core library scan.
/// When a user triggers a scan, the core scan system calls all registered
/// scan participants as part of the same scan job, sharing progress tracking.
/// </summary>
public interface IScanParticipant : IExtension
{
    /// <summary>
    /// Called by the core scan system to let this extension scan for its file types.
    /// The extension receives the resolved scan paths and should process them
    /// within the provided progress range.
    /// </summary>
    Task ScanAsync(ScanContext context, CancellationToken ct = default);
}

/// <summary>
/// Context provided to extension scan participants during a core library scan.
/// </summary>
public record ScanContext(
    /// <summary>Resolved scan targets with their path and exclusion flags.</summary>
    IReadOnlyList<ScanPathInfo> Paths,
    /// <summary>Progress reporter scoped to this participant's allocation of the progress bar.</summary>
    IJobProgress Progress,
    /// <summary>Service provider for resolving dependencies.</summary>
    IServiceProvider Services,
    /// <summary>Whether this is a rescan (re-process existing files).</summary>
    bool Rescan = false
);

/// <summary>A resolved scan path with exclusion flags from the core configuration.</summary>
public record ScanPathInfo(
    string Path,
    bool ExcludeVideo,
    bool ExcludeImage,
    bool ExcludeAudio,
    bool IsFile,
    bool ExcludeText = false
);

/// <summary>
/// Extension that contributes toolbar actions, context menu items, and bulk actions.
/// These appear in the UI based on context (entity type, selection state, page).
/// </summary>
public interface IActionExtension : IExtension
{
    /// <summary>Return provider-independent action declarations; this may be called while disabled.</summary>
    IReadOnlyList<ExtensionAction> GetActions();
}

/// <summary>An action contributed by an extension (toolbar button, context menu item, bulk action).</summary>
public record ExtensionAction(
    string Id,
    string Label,
    string ExtensionId,
    /// <summary>"toolbar", "context-menu", "bulk"</summary>
    string ActionType,
    /// <summary>Entity types this action applies to (empty = all). E.g. ["video", "performer"]</summary>
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
    string[]? Pages = null,
    /// <summary>When true, the host should skip its default queued-action success alert.</summary>
    bool SuppressSuccessAlert = false
)
{
    /// <summary>Permission required to show and invoke this action.</summary>
    public string? RequiredPermission { get; init; }
}

/// <summary>A context in which an extension keyboard action can run.</summary>
public record UIKeyboardActionScope(
    string Surface,
    string? Page = null,
    string? EntityType = null,
    string? Tab = null);

/// <summary>A configurable keyboard action contributed by an extension.</summary>
public record UIKeyboardAction(
    string Id,
    string Label,
    string ExtensionId,
    string[] DefaultBindings,
    UIKeyboardActionScope[] Scopes,
    string? Description = null,
    string? Group = null,
    string? HandlerName = null,
    string? ApiEndpoint = null,
    int Order = 100,
    bool Repeatable = false,
    bool AllowInEditable = false,
    string? RequiredPermission = null);

/// <summary>A data-only keyboard shortcut preset contributed by an extension.</summary>
public record UIKeyboardShortcutPreset(
    int SchemaVersion,
    string Id,
    string Name,
    string ExtensionId,
    string UnmappedActions,
    Dictionary<string, string[]> Bindings,
    string? Description = null,
    string? Author = null,
    string? Version = null,
    string? BasePresetId = null,
    int Order = 100);

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
    /// <summary>Manifest type: "extension" or metadata-only kinds such as "bundle" and "scraper-pack".</summary>
    public string Kind { get; set; } = "extension";
    /// <summary>Minimum Cove version required (semver).</summary>
    public string? MinCoveVersion { get; set; }
    /// <summary>Extension dependencies: ID → semver range (e.g. ">=1.0.0").</summary>
    public Dictionary<string, string> Dependencies { get; set; } = [];
    /// <summary>External tools, runtimes, or services this extension can use.</summary>
    public List<ExtensionExternalDependency> ExternalDependencies { get; set; } = [];
    /// <summary>Generic key/value settings surfaced in the Extensions settings UI.</summary>
    public List<ExtensionSettingManifest> Settings { get; set; } = [];
    /// <summary>In-app Cove Manual topics contributed by this extension.</summary>
    public List<UITutorialTopic> TutorialTopics { get; set; } = [];
    /// <summary>Configurable keyboard actions contributed by a manifest-only extension.</summary>
    public List<UIKeyboardAction> KeyboardActions { get; set; } = [];
    /// <summary>Shareable keyboard presets contributed by a manifest-only extension.</summary>
    public List<UIKeyboardShortcutPreset> KeyboardShortcutPresets { get; set; } = [];
    /// <summary>The DLL filename containing the IExtension implementation.</summary>
    public string? EntryDll { get; set; }
    /// <summary>Assembly names that must be unified across extension load contexts.</summary>
    public List<string> SharedAssemblies { get; set; } = [];
    /// <summary>Relative path to the frontend JS bundle (ESM module).</summary>
    public string? JsBundle { get; set; }
    /// <summary>Relative path to the frontend CSS file.</summary>
    public string? CssBundle { get; set; }
    /// <summary>Categories/labels for filtering and sorting.</summary>
    public List<string> Categories { get; set; } = [];
    /// <summary>Runtime permissions requested by the extension.</summary>
    public ExtensionPermissionManifest Permissions { get; set; } = new();
    /// <summary>SHA-256 checksum of the extension package (set by registry).</summary>
    public string? Checksum { get; set; }
    /// <summary>Registry source URL (set when installed from remote registry).</summary>
    public string? RegistryUrl { get; set; }
}

public class ExtensionExternalDependency
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Kind { get; set; } = "tool";
    public bool Required { get; set; } = true;
    public string? Description { get; set; }
    public string? VersionRequirement { get; set; }
    public List<string> Executables { get; set; } = [];
    public List<string> EnvironmentVariables { get; set; } = [];
    public List<string> ConfigurationKeys { get; set; } = [];
    public string? InstallHint { get; set; }
    public string? NativeHint { get; set; }
    public string? DockerHint { get; set; }
    public string? Url { get; set; }
    public List<string> ExtensionIds { get; set; } = [];

    [JsonPropertyName("optional")]
    public bool? LegacyOptional
    {
        get => null;
        set
        {
            if (value.HasValue)
                Required = !value.Value;
        }
    }

    [JsonPropertyName("settingsKey")]
    public string? LegacySettingsKey
    {
        get => null;
        set
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            if (!ConfigurationKeys.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                ConfigurationKeys.Add(trimmed);
        }
    }
}

public class ExtensionSettingManifest
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "STRING";
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public List<string> ExtensionIds { get; set; } = [];

    [JsonPropertyName("key")]
    public string? LegacyKey
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(Name))
                return;

            Name = value?.Trim() ?? string.Empty;
        }
    }

    [JsonPropertyName("label")]
    public string? LegacyLabel
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
                DisplayName = value?.Trim();
        }
    }

    [JsonPropertyName("defaultValue")]
    public string? LegacyDefaultValue
    {
        get => null;
        set { }
    }

    [JsonPropertyName("scope")]
    public string? LegacyScope
    {
        get => null;
        set { }
    }
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
    public List<UIExtensionBundle> ExtensionBundles { get; set; } = [];
    public List<ExtensionLoginMethod> LoginMethods { get; set; } = [];
    public List<UIPageDefinition> Pages { get; set; } = [];
    public List<UISlotContribution> Slots { get; set; } = [];
    public List<UITabContribution> Tabs { get; set; } = [];
    public List<UIPaneContribution> Panes { get; set; } = [];
    public List<UIFeatureDefinition> Features { get; set; } = [];
    public List<UIComponentOverride> ComponentOverrides { get; set; } = [];
    public List<UISelectorOverride> SelectorOverrides { get; set; } = [];
    public List<UIThemeDefinition> Themes { get; set; } = [];
    public List<UIComponentStyleDef> ComponentStyles { get; set; } = [];
    public List<UILayoutStyleDef> LayoutStyles { get; set; } = [];
    public List<UISettingsTab> SettingsTabs { get; set; } = [];
    public List<UISettingsPanel> SettingsPanels { get; set; } = [];
    public List<UIPageOverride> PageOverrides { get; set; } = [];
    public List<UIDialogOverride> DialogOverrides { get; set; } = [];
    public List<ExtensionAction> Actions { get; set; } = [];
    public List<UIKeyboardAction> KeyboardActions { get; set; } = [];
    public List<UIKeyboardShortcutPreset> KeyboardShortcutPresets { get; set; } = [];
    public List<UITutorialTopic> TutorialTopics { get; set; } = [];
    public List<UIListFilterContribution> ListFilters { get; set; } = [];
    public List<UIListSortContribution> ListSorts { get; set; } = [];
    public List<UIDashboardWidgetContribution> DashboardWidgets { get; set; } = [];

    /// <summary>Version of the frontend runtime contract used to load extension bundles.</summary>
    public string? FrontendRuntimeVersion { get; set; }
    /// <summary>URL of the extension's JS module (ESM) loaded by the frontend runtime.</summary>
    public string? JsBundleUrl { get; set; }
    /// <summary>URL of additional CSS to load.</summary>
    public string? CssBundleUrl { get; set; }
}

/// <summary>
/// An interactive login method advertised by an extension without loading its frontend bundle on
/// Cove's unauthenticated login page. The start URL must be local to this Cove origin.
/// </summary>
public record ExtensionLoginMethod(
    string Id,
    string Label,
    string StartUrl,
    int Order = 100,
    string? ExtensionId = null,
    string? LinkStartUrl = null)
{
    /// <summary>Binary-compatible constructor for extensions built before account linking.</summary>
    public ExtensionLoginMethod(
        string Id,
        string Label,
        string StartUrl,
        int Order,
        string? ExtensionId)
        : this(Id, Label, StartUrl, Order, ExtensionId, null)
    {
    }

    /// <summary>
    /// Whether this method appears on the unauthenticated login page. Link-only methods remain
    /// available in account settings even when transparent middleware performs their login step.
    /// </summary>
    public bool ShowOnLoginPage { get; init; } = true;
}

/// <summary>
/// Versioned frontend assets owned by one extension. Keeping bundle ownership in the
/// manifest lets the browser runtime load, replace, and unload extensions independently.
/// </summary>
public record UIExtensionBundle(
    string ExtensionId,
    string Version,
    string? JsBundleUrl = null,
    string? CssBundleUrl = null
);

/// <summary>In-app tutorial/manual topic contributed by Cove or an extension.</summary>
public record UITutorialTopic(
    string Id,
    string Title,
    string? Description = null,
    string[]? Pages = null,
    string[]? Contexts = null,
    string? ExtensionId = null,
    int Order = 100,
    List<UITutorialSlide>? Slides = null,
    string? ParentTopicId = null,
    string? Kind = null
);

/// <summary>Single tutorial slide shown inside a tutorial topic.</summary>
public record UITutorialSlide(
    string Id,
    string Title,
    string? Caption = null,
    string[]? Points = null,
    string? ImageSrc = null,
    string? ImageAlt = null,
    string? MockKind = null,
    List<UITutorialLink>? Links = null,
    string? BodyMarkdown = null
);

/// <summary>External link rendered from an in-app manual slide.</summary>
public record UITutorialLink(string Label, string Url);

/// <summary>
/// A new top-level page contributed by an extension.
/// </summary>
/// <remarks>
/// Cove also routes one optional child path segment to the same component. A non-numeric child is
/// supplied to the component as <c>slug</c>; a positive numeric child uses the existing <c>id</c>
/// detail-page contract. Deeper child paths and numeric slugs are not supported. Extensions that
/// need a deeper route hierarchy should keep that state inside their page or use query parameters.
/// </remarks>
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
)
{
    /// <summary>Permissions evaluated together to show and render this page.</summary>
    public string[]? RequiredPermissions { get; init; }
    /// <summary>How <see cref="RequiredPermissions"/> are evaluated. Defaults to requiring all.</summary>
    public PermissionMode RequiredPermissionMode { get; init; } = PermissionMode.All;
}

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

public record UITabContribution(
    string Key,
    string Label,
    /// <summary>"video", "performer", "studio", "tag", "gallery", "image", "group", "settings"</summary>
    string PageType,
    string ExtensionId,
    string ComponentName,
    int Order = 100,
    /// <summary>API endpoint template for fetching a count badge. Use {entityId} as placeholder.</summary>
    string? CountEndpoint = null,
    string? Icon = null,
    /// <summary>Manual contexts to publish while this tab is active.</summary>
    string[]? ManualContexts = null
)
{
    /// <summary>Permission required to show and render this tab.</summary>
    public string? RequiredPermission { get; init; }
    /// <summary>Permissions evaluated together to show and render this tab.</summary>
    public string[]? RequiredPermissions { get; init; }
    /// <summary>How <see cref="RequiredPermissions"/> are evaluated. Defaults to requiring all.</summary>
    public PermissionMode RequiredPermissionMode { get; init; } = PermissionMode.All;
}

/// <summary>Add a panel/pane region contribution to a page layout.</summary>
public record UIPaneContribution(
    string Id,
    /// <summary>"video", "performer", "studio", "tag", "gallery", "image", "group", "settings", "home"</summary>
    string PageType,
    /// <summary>Host-defined zone key where this pane should render (e.g. "sidebar-right", "hero", "details").</summary>
    string Zone,
    string ExtensionId,
    string ComponentName,
    string? Label = null,
    int Order = 100
);

/// <summary>Host-owned dashboard presentation modes supported by a widget contribution.</summary>
public enum DashboardWidgetPresentation
{
    /// <summary>Content-driven widget rendered in the dashboard's ordered vertical stack.</summary>
    Flow,
    /// <summary>Exclusive widget rendered as the dashboard's document-scrolling content canvas.</summary>
    Canvas,
}

/// <summary>A widget that users may place on personal dashboards.</summary>
public record UIDashboardWidgetContribution(
    string Id,
    string Label,
    string ExtensionId,
    string ComponentName,
    string? EditorComponentName = null,
    string? Description = null,
    string? Icon = null,
    JsonElement? DefaultConfiguration = null,
    bool AllowMultiple = true,
    int Order = 100)
{
    public string? RequiredPermission { get; init; }
    public string[]? RequiredPermissions { get; init; }
    public PermissionMode RequiredPermissionMode { get; init; } = PermissionMode.All;
    public DashboardWidgetPresentation[] SupportedPresentations { get; init; } = [DashboardWidgetPresentation.Flow];
    public DashboardWidgetPresentation DefaultPresentation { get; init; } = DashboardWidgetPresentation.Flow;
}

/// <summary>Feature capability metadata exposed to the host UI.</summary>
public record UIFeatureDefinition(
    string Key,
    string ExtensionId,
    Dictionary<string, string>? Options = null
);

/// <summary>Override a host component by stable key (selectors, cards, toolbars, list rows, etc.).</summary>
public record UIComponentOverride(
    /// <summary>Host-defined component key (e.g. "video.selector", "performer.card", "search.bar").</summary>
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

/// <summary>How a settings tab's content is laid out.</summary>
public enum SettingsTabLayout
{
    /// <summary>Host stacks the tab's contributed panels, each in its own card (default).</summary>
    Panels,

    /// <summary>Extension owns the tab canvas: host renders the tab's panels full-width, stacked without card chrome.</summary>
    Page,
}

/// <summary>
/// A dedicated tab rendered in the host's Extensions settings group.
/// The key also acts as the route segment after /settings/.
/// </summary>
public record UISettingsTab(
    string Key,
    string Label,
    string ExtensionId,
    int Order = 100,
    string? Icon = null,
    string? ParentTabKey = null,
    string? Description = null,
    string[]? SearchKeywords = null,
    string[]? Aliases = null)
{
    /// <summary>
    /// How the host renders this tab's contributed panels; defaults to <see cref="SettingsTabLayout.Panels"/>.
    /// Declared as an init property, not a primary-constructor parameter, so adding it does not change the
    /// record's constructor signature — extensions compiled against the prior version stay binary-compatible.
    /// </summary>
    public SettingsTabLayout Layout { get; init; } = SettingsTabLayout.Panels;
}

/// <summary>A settings panel contributed by an extension. Appears in the Extensions settings tab by default, or in a specific tab or tab section when targeted.</summary>
public record UISettingsPanel(
    string Id,
    string Label,
    string ExtensionId,
    string ComponentName,
    int Order = 100,
    /// <summary>Target settings tab key (e.g. "library", "interface", "security"). Null = Extensions tab.</summary>
    string? TargetTab = null,
    /// <summary>Optional target section key within the target tab (e.g. "extensions" under the Library tab).</summary>
    string? TargetSection = null
);

/// <summary>
/// Replace an existing built-in page with an extension's component.
/// This is how extensions achieve full page replacement or complete UI overhaul.
/// Use TargetPage = "*" to override the entire app shell.
/// </summary>
public record UIPageOverride(
    /// <summary>The built-in page key to replace (e.g. "videos", "home", "settings", or "*" for full shell).</summary>
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
    /// <summary>Dialog identifier (e.g. "video-edit", "performer-edit", "confirm-delete").</summary>
    string DialogId,
    string ExtensionId,
    string ComponentName,
    int Priority = 100
);

/// <summary>Declare a first-class advanced filter row for a built-in entity list.</summary>
public record UIListFilterContribution(
    string Id,
    string EntityType,
    string Label,
    string CriterionType,
    string ExtensionId,
    string? FilterKey = null,
    string? CustomFieldKey = null,
    string? CustomFieldType = null,
    string? EntityReferenceType = null,
    List<string>? Modifiers = null,
    List<UIListFilterOption>? Options = null,
    int Order = 100)
{
    /// <summary>
    /// Stable provider-owned filter identifier. Null means presentation-only/core-backed. This is
    /// an init property so extensions compiled against the original positional record keep the
    /// same constructor and deconstructor ABI.
    /// </summary>
    public string? FilterId { get; init; }
}

/// <summary>Static option for an extension-contributed enum/multi-select list filter.</summary>
public record UIListFilterOption(string Value, string Label);

/// <summary>Declare a first-class sort option for a built-in entity list.</summary>
public record UIListSortContribution(
    string Id,
    string EntityType,
    string Label,
    string ExtensionId,
    string? SortKey = null,
    string? CustomFieldKey = null,
    string? CustomFieldType = null,
    int Order = 100
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
    private readonly List<UIFeatureDefinition> _features = [];
    private readonly List<UIComponentOverride> _componentOverrides = [];
    private readonly List<UISelectorOverride> _selectorOverrides = [];
    private readonly List<UIThemeDefinition> _themes = [];
    private readonly List<UIComponentStyleDef> _componentStyles = [];
    private readonly List<UILayoutStyleDef> _layoutStyles = [];
    private readonly List<UISettingsTab> _settingsTabs = [];
    private readonly List<UISettingsPanel> _settingsPanels = [];
    private readonly List<UIPageOverride> _pageOverrides = [];
    private readonly List<UIDialogOverride> _dialogOverrides = [];
    private readonly List<ExtensionAction> _actions = [];
    private readonly List<UIKeyboardAction> _keyboardActions = [];
    private readonly List<UIKeyboardShortcutPreset> _keyboardShortcutPresets = [];
    private readonly List<UITutorialTopic> _tutorialTopics = [];
    private readonly List<UIListFilterContribution> _listFilters = [];
    private readonly List<UIListSortContribution> _listSorts = [];

    public IReadOnlyList<UIPageDefinition> Pages => _pages;
    public IReadOnlyList<UISlotContribution> Slots => _slots;
    public IReadOnlyList<UITabContribution> Tabs => _tabs;
    public IReadOnlyList<UIPaneContribution> Panes => _panes;
    public IReadOnlyList<UIFeatureDefinition> Features => _features;
    public IReadOnlyList<UIComponentOverride> ComponentOverrides => _componentOverrides;
    public IReadOnlyList<UISelectorOverride> SelectorOverrides => _selectorOverrides;
    public IReadOnlyList<UIThemeDefinition> Themes => _themes;
    public IReadOnlyList<UIComponentStyleDef> ComponentStyles => _componentStyles;
    public IReadOnlyList<UILayoutStyleDef> LayoutStyles => _layoutStyles;
    public IReadOnlyList<UISettingsTab> SettingsTabs => _settingsTabs;
    public IReadOnlyList<UISettingsPanel> SettingsPanels => _settingsPanels;
    public IReadOnlyList<UIPageOverride> PageOverrides => _pageOverrides;
    public IReadOnlyList<UIDialogOverride> DialogOverrides => _dialogOverrides;
    public IReadOnlyList<ExtensionAction> Actions => _actions;
    public IReadOnlyList<UIKeyboardAction> KeyboardActions => _keyboardActions;
    public IReadOnlyList<UIKeyboardShortcutPreset> KeyboardShortcutPresets => _keyboardShortcutPresets;
    public IReadOnlyList<UITutorialTopic> TutorialTopics => _tutorialTopics;
    public IReadOnlyList<UIListFilterContribution> ListFilters => _listFilters;
    public IReadOnlyList<UIListSortContribution> ListSorts => _listSorts;

    public void RegisterPage(UIPageDefinition page) => _pages.Add(page);
    public void RegisterSlot(UISlotContribution slot) => _slots.Add(slot);
    public void RegisterTab(UITabContribution tab) => _tabs.Add(tab);
    public void RegisterPane(UIPaneContribution pane) => _panes.Add(pane);
    public void RegisterFeature(UIFeatureDefinition feature) => _features.Add(feature);
    public void RegisterComponentOverride(UIComponentOverride ov) => _componentOverrides.Add(ov);
    public void RegisterSelectorOverride(UISelectorOverride ov) => _selectorOverrides.Add(ov);
    public void RegisterTheme(UIThemeDefinition theme) => _themes.Add(theme);
    public void RegisterComponentStyle(UIComponentStyleDef style) => _componentStyles.Add(style);
    public void RegisterLayoutStyle(UILayoutStyleDef layout) => _layoutStyles.Add(layout);
    public void RegisterSettingsTab(UISettingsTab tab) => _settingsTabs.Add(tab);
    public void RegisterSettingsPanel(UISettingsPanel panel) => _settingsPanels.Add(panel);
    public void RegisterPageOverride(UIPageOverride ov) => _pageOverrides.Add(ov);
    public void RegisterDialogOverride(UIDialogOverride ov) => _dialogOverrides.Add(ov);
    public void RegisterAction(ExtensionAction action) => _actions.Add(action);
    public void RegisterKeyboardAction(UIKeyboardAction action) => _keyboardActions.Add(action);
    public void RegisterKeyboardShortcutPreset(UIKeyboardShortcutPreset preset) => _keyboardShortcutPresets.Add(preset);
    public void RegisterTutorialTopic(UITutorialTopic topic) => _tutorialTopics.Add(topic);
    public void RegisterListFilter(UIListFilterContribution filter) => _listFilters.Add(filter);
    public void RegisterListSort(UIListSortContribution sort) => _listSorts.Add(sort);

    public UIManifest ToManifest() => new()
    {
        Pages = [.. _pages],
        Slots = [.. _slots],
        Tabs = [.. _tabs],
        Panes = [.. _panes],
        Features = [.. _features],
        ComponentOverrides = [.. _componentOverrides],
        SelectorOverrides = [.. _selectorOverrides],
        Themes = [.. _themes],
        ComponentStyles = [.. _componentStyles],
        LayoutStyles = [.. _layoutStyles],
        SettingsTabs = [.. _settingsTabs],
        SettingsPanels = [.. _settingsPanels],
        PageOverrides = [.. _pageOverrides],
        DialogOverrides = [.. _dialogOverrides],
        Actions = [.. _actions],
        KeyboardActions = [.. _keyboardActions],
        KeyboardShortcutPresets = [.. _keyboardShortcutPresets],
        TutorialTopics = [.. _tutorialTopics],
        ListFilters = [.. _listFilters],
        ListSorts = [.. _listSorts],
    };
}
