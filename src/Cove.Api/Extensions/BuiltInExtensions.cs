using Microsoft.Extensions.DependencyInjection;
using Cove.Plugins;

namespace Cove.Api.Extensions;

// ============================================================================
// POC 1: Theme Extension — multiple theme definitions + CSS variables
// Proves: theme system, theme selector, CSS variable injection
// ============================================================================
public class ThemeCollectionExtension : IExtension, IUIExtension
{
    public string Id => "com.cove.themes";
    public string Name => "Theme Collection";
    public string Version => "1.0.0";
    public string? Description => "Built-in theme collection with multiple dark and light themes";
    public string? Author => "Cove";
    public string? Url => null;
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.Theme, ExtensionCategories.ColorPalette, ExtensionCategories.Style, ExtensionCategories.Layout];

    public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }

    public UIManifest GetUIManifest() => new()
    {
        ComponentStyles =
        [
            new UIComponentStyleDef("default", "Default", "Rounded corners, raised cards, subtle transitions"),
            new UIComponentStyleDef("minimal", "Minimal", "Flat surfaces, no shadows, clean lines"),
            new UIComponentStyleDef("glass", "Glass", "Frosted glass surfaces with blur and transparency"),
            new UIComponentStyleDef("rounded", "Rounded", "Extra rounded corners, pill buttons"),
            new UIComponentStyleDef("gradient", "Gradient", "Animated gradient background using your active color palette"),
            new UIComponentStyleDef("animated", "Animated", "Breathing borders, hover glow, shimmer effects on cards and nav"),
        ],
        LayoutStyles =
        [
            new UILayoutStyleDef("default", "Default", "Standard layout with top navigation"),
            new UILayoutStyleDef("compact", "Compact", "Denser grid, smaller spacing"),
        ],
        Themes =
        [
            new UIThemeDefinition(
                Id: "default",
                Name: "Default",
                Description: "A clean, modern dark theme",
                CssVariables: new()
                {
                    ["--color-background"] = "#16181d",
                    ["--color-nav"] = "#111317",
                    ["--color-card"] = "#1e2028",
                    ["--color-card-hover"] = "#252830",
                    ["--color-surface"] = "#1a1c23",
                    ["--color-border"] = "#2a2d38",
                    ["--color-input"] = "rgba(0, 0, 0, 0.25)",
                    ["--color-accent"] = "#4f8ff7",
                    ["--color-accent-hover"] = "#6ea4ff",
                    ["--color-foreground"] = "#e8eaf0",
                    ["--color-secondary"] = "#9ea3b0",
                    ["--color-muted"] = "#6b7085",
                    ["--color-overlay"] = "rgba(0, 0, 0, 0.55)",
                    ["--color-nav-active"] = "#4f8ff7",
                }
            ),
            new UIThemeDefinition(
                Id: "legacy",
                Name: "Legacy",
                Description: "A nostalgic theme"
            ),
            new UIThemeDefinition(
                Id: "light",
                Name: "Light",
                Description: "Clean light theme with blue accents",
                ColorScheme: "light",
                CssVariables: new()
                {
                    ["--color-background"] = "#f0f2f5",
                    ["--color-nav"] = "#ffffff",
                    ["--color-card"] = "#ffffff",
                    ["--color-card-hover"] = "#f5f5f5",
                    ["--color-surface"] = "#ffffff",
                    ["--color-border"] = "#d1d5db",
                    ["--color-input"] = "rgba(0, 0, 0, 0.04)",
                    ["--color-accent"] = "#2563eb",
                    ["--color-accent-hover"] = "#1d4ed8",
                    ["--color-foreground"] = "#111827",
                    ["--color-secondary"] = "#6b7280",
                    ["--color-muted"] = "#9ca3af",
                    ["--color-overlay"] = "rgba(0, 0, 0, 0.3)",
                    ["--color-nav-active"] = "#2563eb",
                }
            ),
            new UIThemeDefinition(
                Id: "dark-midnight",
                Name: "Dark Midnight",
                Description: "Deep midnight blue with purple accents",
                CssVariables: new()
                {
                    ["--color-background"] = "#0e1320",
                    ["--color-nav"] = "#141a28",
                    ["--color-card"] = "#1a2230",
                    ["--color-surface"] = "#1e2838",
                    ["--color-border"] = "#2c3a4d",
                    ["--color-accent"] = "#8b5cf6",
                    ["--color-accent-hover"] = "#7c3aed",
                    ["--color-foreground"] = "#e6edf3",
                    ["--color-secondary"] = "#8b949e",
                    ["--color-muted"] = "#484f58",
                    ["--color-nav-active"] = "#06b6d4",
                }
            ),
            new UIThemeDefinition(
                Id: "dark-emerald",
                Name: "Dark Emerald",
                Description: "Dark theme with emerald green accents",
                CssVariables: new()
                {
                    ["--color-background"] = "#0c1a10",
                    ["--color-nav"] = "#122018",
                    ["--color-card"] = "#182a1e",
                    ["--color-surface"] = "#1e3225",
                    ["--color-border"] = "#2a4432",
                    ["--color-accent"] = "#10b981",
                    ["--color-accent-hover"] = "#059669",
                    ["--color-foreground"] = "#e6f0e8",
                    ["--color-secondary"] = "#7ca38a",
                    ["--color-muted"] = "#4a6350",
                    ["--color-nav-active"] = "#8b5cf6",
                }
            ),
            new UIThemeDefinition(
                Id: "dark-rose",
                Name: "Dark Rosé",
                Description: "Dark theme with warm rose accents",
                CssVariables: new()
                {
                    ["--color-background"] = "#1a0e0e",
                    ["--color-nav"] = "#221414",
                    ["--color-card"] = "#2c1a1a",
                    ["--color-surface"] = "#342020",
                    ["--color-border"] = "#482e2e",
                    ["--color-accent"] = "#f43f5e",
                    ["--color-accent-hover"] = "#e11d48",
                    ["--color-foreground"] = "#f0e6e6",
                    ["--color-secondary"] = "#a37c7c",
                    ["--color-muted"] = "#634a4a",
                    ["--color-nav-active"] = "#a855f7",
                }
            ),
            new UIThemeDefinition(
                Id: "dark-ocean",
                Name: "Dark Ocean",
                Description: "Deep ocean blue theme",
                CssVariables: new()
                {
                    ["--color-background"] = "#0a1628",
                    ["--color-nav"] = "#0f1d32",
                    ["--color-card"] = "#14253d",
                    ["--color-surface"] = "#192c47",
                    ["--color-border"] = "#243a5c",
                    ["--color-accent"] = "#0ea5e9",
                    ["--color-accent-hover"] = "#0284c7",
                    ["--color-foreground"] = "#e0f2fe",
                    ["--color-secondary"] = "#7cacca",
                    ["--color-muted"] = "#3b6685",
                    ["--color-nav-active"] = "#06d6a0",
                }
            ),
            new UIThemeDefinition(
                Id: "pornhub",
                Name: "Pornhub",
                Description: "Black and orange inspired by Pornhub",
                CssVariables: new()
                {
                    ["--color-background"] = "#000000",
                    ["--color-nav"] = "#1b1b1b",
                    ["--color-card"] = "#1b1b1b",
                    ["--color-card-hover"] = "#272727",
                    ["--color-surface"] = "#1b1b1b",
                    ["--color-border"] = "#333333",
                    ["--color-input"] = "#0d0d0d",
                    ["--color-accent"] = "#ff9000",
                    ["--color-accent-hover"] = "#ffb648",
                    ["--color-foreground"] = "#ffffff",
                    ["--color-secondary"] = "#b5b5b5",
                    ["--color-muted"] = "#6e6e6e",
                    ["--color-overlay"] = "rgba(0,0,0,0.85)",
                    ["--color-nav-active"] = "#e53935",
                }
            ),
            new UIThemeDefinition(
                Id: "plex",
                Name: "Plex",
                Description: "Dark theme with warm gold accents inspired by Plex",
                CssVariables: new()
                {
                    ["--color-background"] = "#1f1f1f",
                    ["--color-nav"] = "#191919",
                    ["--color-card"] = "#282828",
                    ["--color-card-hover"] = "#333333",
                    ["--color-surface"] = "#242424",
                    ["--color-border"] = "#3a3a3a",
                    ["--color-input"] = "#141414",
                    ["--color-accent"] = "#e5a00d",
                    ["--color-accent-hover"] = "#cc7b19",
                    ["--color-foreground"] = "#eaeaea",
                    ["--color-secondary"] = "#999999",
                    ["--color-muted"] = "#555555",
                    ["--color-overlay"] = "rgba(0,0,0,0.7)",
                    ["--color-nav-active"] = "#ff6b2b",
                }
            ),
            new UIThemeDefinition(
                Id: "reddit",
                Name: "Reddit Dark",
                Description: "Dark theme with blue-white tones inspired by Reddit",
                CssVariables: new()
                {
                    ["--color-background"] = "#030303",
                    ["--color-nav"] = "#1a1a1b",
                    ["--color-card"] = "#1a1a1b",
                    ["--color-card-hover"] = "#272729",
                    ["--color-surface"] = "#1a1a1b",
                    ["--color-border"] = "#343536",
                    ["--color-input"] = "#0f0f10",
                    ["--color-accent"] = "#ff4500",
                    ["--color-accent-hover"] = "#ff6733",
                    ["--color-foreground"] = "#d7dadc",
                    ["--color-secondary"] = "#818384",
                    ["--color-muted"] = "#545456",
                    ["--color-overlay"] = "rgba(0,0,0,0.75)",
                    ["--color-nav-active"] = "#0079d3",
                }
            ),
            new UIThemeDefinition(
                Id: "rainbow",
                Name: "Rainbow",
                Description: "Vivid multi-color rainbow gradient with deep dark base",
                CssVariables: new()
                {
                    ["--color-background"] = "#08080f",
                    ["--color-nav"] = "#0c0c16",
                    ["--color-card"] = "#12121e",
                    ["--color-card-hover"] = "#1a1a28",
                    ["--color-surface"] = "#0e0e18",
                    ["--color-border"] = "#1e1e2e",
                    ["--color-input"] = "#08080f",
                    ["--color-accent"] = "#a855f7",
                    ["--color-accent-hover"] = "#c084fc",
                    ["--color-foreground"] = "#f5f5f7",
                    ["--color-secondary"] = "#a1a1b0",
                    ["--color-muted"] = "#636370",
                    ["--color-overlay"] = "rgba(0,0,0,0.4)",
                    ["--color-nav-active"] = "#ec4899",
                },
                ComponentStyle: "glass gradient animated"
            ),
            new UIThemeDefinition(
                Id: "liquid-glass",
                Name: "Liquid Glass",
                Description: "Dramatic translucent glass with vivid gradient — inspired by Apple's Liquid Glass",
                CssVariables: new()
                {
                    ["--color-background"] = "#0a0a12",
                    ["--color-nav"] = "#0e0e18",
                    ["--color-card"] = "#14141e",
                    ["--color-card-hover"] = "#1c1c28",
                    ["--color-surface"] = "#10101a",
                    ["--color-border"] = "#22222e",
                    ["--color-input"] = "#0a0a12",
                    ["--color-accent"] = "#007aff",
                    ["--color-accent-hover"] = "#5ac8fa",
                    ["--color-foreground"] = "#f5f5f7",
                    ["--color-secondary"] = "#a1a1a6",
                    ["--color-muted"] = "#636366",
                    ["--color-overlay"] = "rgba(0,0,0,0.3)",
                    ["--color-nav-active"] = "#bf5af2",
                },
                BackgroundAnimation: "liquid-drift"
            ),
            // === Animated themes ===
            new UIThemeDefinition(
                Id: "neon-glow",
                Name: "Neon Glow",
                Description: "Animated purple-blue gradient with pulsing borders and frosted glass",
                CssVariables: new()
                {
                    ["--color-background"] = "#0c0b1e",
                    ["--color-nav"] = "#121026",
                    ["--color-card"] = "#1e1a38",
                    ["--color-card-hover"] = "#2a2648",
                    ["--color-surface"] = "#181430",
                    ["--color-border"] = "#302a58",
                    ["--color-input"] = "#0a0a14",
                    ["--color-accent"] = "#8b5cf6",
                    ["--color-accent-hover"] = "#a78bfa",
                    ["--color-foreground"] = "#f0eeff",
                    ["--color-secondary"] = "#9d8ec2",
                    ["--color-muted"] = "#5b4f7a",
                    ["--color-overlay"] = "rgba(0,0,0,0.6)",
                    ["--color-nav-active"] = "#06d6a0",
                },
                ComponentStyle: "glass gradient animated"
            ),
            new UIThemeDefinition(
                Id: "sunset-gradient",
                Name: "Sunset Gradient",
                Description: "Warm animated gradient from orange through rose to purple",
                CssVariables: new()
                {
                    ["--color-background"] = "#120c0e",
                    ["--color-nav"] = "#181012",
                    ["--color-card"] = "#281c20",
                    ["--color-card-hover"] = "#36262a",
                    ["--color-surface"] = "#20161a",
                    ["--color-border"] = "#3a2028",
                    ["--color-input"] = "#100a0c",
                    ["--color-accent"] = "#f97316",
                    ["--color-accent-hover"] = "#fb923c",
                    ["--color-foreground"] = "#fef2f2",
                    ["--color-secondary"] = "#c2918a",
                    ["--color-muted"] = "#785450",
                    ["--color-overlay"] = "rgba(0,0,0,0.6)",
                    ["--color-nav-active"] = "#e11d48",
                },
                ComponentStyle: "glass gradient animated"
            ),
            new UIThemeDefinition(
                Id: "aurora",
                Name: "Aurora",
                Description: "Shimmering northern lights with teal and purple hues",
                CssVariables: new()
                {
                    ["--color-background"] = "#080e0c",
                    ["--color-nav"] = "#0e1612",
                    ["--color-card"] = "#182822",
                    ["--color-card-hover"] = "#22342c",
                    ["--color-surface"] = "#14221c",
                    ["--color-border"] = "#1e3428",
                    ["--color-input"] = "#080e0c",
                    ["--color-accent"] = "#10b981",
                    ["--color-accent-hover"] = "#34d399",
                    ["--color-foreground"] = "#ecfdf5",
                    ["--color-secondary"] = "#86b8a0",
                    ["--color-muted"] = "#4a7562",
                    ["--color-overlay"] = "rgba(0,0,0,0.5)",
                    ["--color-nav-active"] = "#818cf8",
                },
                ComponentStyle: "glass gradient animated"
            ),
            // === Multi-color animated themes with background animations ===
            new UIThemeDefinition(
                Id: "cyberpunk",
                Name: "Cyberpunk",
                Description: "Neon pink and electric cyan on deep black — retro-futuristic vibes",
                CssVariables: new()
                {
                    ["--color-background"] = "#0a0a0f",
                    ["--color-nav"] = "#0e0a14",
                    ["--color-card"] = "#16101e",
                    ["--color-card-hover"] = "#201828",
                    ["--color-surface"] = "#120e1a",
                    ["--color-border"] = "#2a1e38",
                    ["--color-input"] = "#08060c",
                    ["--color-accent"] = "#ff2d95",
                    ["--color-accent-hover"] = "#00f0ff",
                    ["--color-foreground"] = "#f0e8ff",
                    ["--color-secondary"] = "#a090c0",
                    ["--color-muted"] = "#5a4878",
                    ["--color-overlay"] = "rgba(0,0,0,0.6)",
                    ["--color-nav-active"] = "#b44aff",
                },
                BackgroundAnimation: "liquid-drift"
            ),
            new UIThemeDefinition(
                Id: "deep-space",
                Name: "Deep Space",
                Description: "Cosmic nebula with blue, magenta, and violet — an interstellar journey",
                CssVariables: new()
                {
                    ["--color-background"] = "#060610",
                    ["--color-nav"] = "#0a0a18",
                    ["--color-card"] = "#12101e",
                    ["--color-card-hover"] = "#1c1828",
                    ["--color-surface"] = "#0e0c16",
                    ["--color-border"] = "#1e1830",
                    ["--color-input"] = "#060610",
                    ["--color-accent"] = "#6366f1",
                    ["--color-accent-hover"] = "#f472b6",
                    ["--color-foreground"] = "#eef0ff",
                    ["--color-secondary"] = "#9498c8",
                    ["--color-muted"] = "#4c4e72",
                    ["--color-overlay"] = "rgba(0,0,0,0.5)",
                    ["--color-nav-active"] = "#a78bfa",
                },
                BackgroundAnimation: "liquid-drift"
            ),
            // === Complex multi-color themes ===
            new UIThemeDefinition(
                Id: "synthwave",
                Name: "Synthwave",
                Description: "Retro-futuristic pink, magenta, and cyan on deep violet — 80s aesthetic",
                CssVariables: new()
                {
                    ["--color-background"] = "#0d0520",
                    ["--color-nav"] = "#120828",
                    ["--color-card"] = "#1a0e30",
                    ["--color-card-hover"] = "#24163a",
                    ["--color-surface"] = "#160a2c",
                    ["--color-border"] = "#2e1850",
                    ["--color-input"] = "#0a0418",
                    ["--color-accent"] = "#ff2d95",
                    ["--color-accent-hover"] = "#00e5ff",
                    ["--color-foreground"] = "#f8e8ff",
                    ["--color-secondary"] = "#b080d0",
                    ["--color-muted"] = "#6a4088",
                    ["--color-overlay"] = "rgba(0,0,0,0.6)",
                    ["--color-nav-active"] = "#bf5af2",
                },
                BackgroundAnimation: "liquid-drift"
            ),
            new UIThemeDefinition(
                Id: "ember",
                Name: "Ember",
                Description: "Warm embers — deep reds, burnt oranges, and golden highlights in darkness",
                CssVariables: new()
                {
                    ["--color-background"] = "#100804",
                    ["--color-nav"] = "#180c06",
                    ["--color-card"] = "#221208",
                    ["--color-card-hover"] = "#2e1a0e",
                    ["--color-surface"] = "#1c1008",
                    ["--color-border"] = "#3a2010",
                    ["--color-input"] = "#0e0604",
                    ["--color-accent"] = "#f97316",
                    ["--color-accent-hover"] = "#fbbf24",
                    ["--color-foreground"] = "#fff5e8",
                    ["--color-secondary"] = "#c8946a",
                    ["--color-muted"] = "#7a5238",
                    ["--color-overlay"] = "rgba(0,0,0,0.6)",
                    ["--color-nav-active"] = "#ef4444",
                },
                BackgroundAnimation: "liquid-drift"
            ),
        ]
    };
}

// ============================================================================
// POC 4: New Page Extension — adds entirely new pages to navigation
// Proves: new page contribution, nav integration, multi-page extension
// ============================================================================
public class SystemToolsExtension : IExtension, IApiExtension, IUIExtension
{
    public string Id => "com.cove.system-tools";
    public string Name => "System Tools";
    public string Version => "1.0.0";
    public string? Description => "Adds a System Tools page with diagnostics and utilities";
    public string? Author => "Cove";
    public string? Url => null;
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.Tools, ExtensionCategories.UI];

    public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ext/system-tools");

        group.MapGet("/info", () => Results.Ok(new
        {
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            cpuCount = Environment.ProcessorCount,
            workingSet = Environment.WorkingSet,
            uptime = Environment.TickCount64,
            gcMemory = GC.GetTotalMemory(false),
        }));

        group.MapGet("/extensions", (ExtensionManager mgr) =>
        {
            return Results.Ok(mgr.Extensions.Select(e => new
            {
                e.Id,
                e.Name,
                e.Version,
                e.Description,
                enabled = mgr.IsEnabled(e.Id),
                capabilities = new
                {
                    ui = e is IUIExtension,
                    api = e is IApiExtension,
                    stateful = e is IStatefulExtension,
                    jobs = e is IJobExtension,
                    events = e is IEventExtension,
                }
            }));
        });
    }

    public UIManifest GetUIManifest() => new()
    {
        Pages =
        [
            new UIPageDefinition(
                Route: "system-tools",
                Label: "System Tools",
                Icon: "wrench",
                ShowInNav: true,
                NavOrder: 80,
                ComponentName: "SystemToolsPage",
                ExtensionId: Id
            ),
        ],
    };
}

// ============================================================================
// POC 6: Dialog Override Extension — overrides the delete confirmation dialog
// Proves: dialog override capability
// ============================================================================
public class EnhancedDeleteDialogExtension : IExtension, IUIExtension
{
    public string Id => "com.cove.enhanced-delete-dialog";
    public string Name => "Enhanced Delete Dialog";
    public string Version => "1.0.0";
    public string? Description => "Replaces the default delete confirmation with an enhanced version showing what will be affected";
    public string? Author => "Cove";
    public string? Url => null;
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.UI];

    public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }

    public UIManifest GetUIManifest() => new()
    {
        DialogOverrides =
        [
            new UIDialogOverride(
                DialogId: "confirm-delete",
                ExtensionId: Id,
                ComponentName: "EnhancedDeleteDialog",
                Priority: 100
            ),
        ],
    };
}

// ============================================================================
// POC 7: Event Extension — demonstrates entity event subscription
// Proves: event hooks, stateful tracking
// ============================================================================
public class AuditLogExtension : IExtension, IEventExtension, IApiExtension, IStatefulExtension
{
    public string Id => "com.cove.audit-log";
    public string Name => "Audit Log";
    public string Version => "1.0.0";
    public string? Description => "Logs entity changes (create/update/delete) to an audit trail";
    public string? Author => "Cove";
    public string? Url => null;
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.Security, ExtensionCategories.Tools];
    private IExtensionStore? _store;

    public void SetStore(IExtensionStore store) => _store = store;
    public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }

    public async Task OnEventAsync(ExtensionEvent evt, CancellationToken ct = default)
    {
        if (_store == null) return;
        var timestamp = DateTime.UtcNow.ToString("O");
        var key = $"log:{timestamp}:{evt.EventType}:{evt.EntityId}";
        await _store.SetAsync(key, System.Text.Json.JsonSerializer.Serialize(new
        {
            evt.EventType,
            evt.EntityType,
            evt.EntityId,
            Timestamp = timestamp,
        }), ct);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/ext/audit");

        group.MapGet("/log", async () =>
        {
            if (_store == null) return Results.StatusCode(500);
            var all = await _store.GetAllAsync();
            var logs = all.Where(kv => kv.Key.StartsWith("log:"))
                .OrderByDescending(kv => kv.Key)
                .Take(100)
                .Select(kv => System.Text.Json.JsonSerializer.Deserialize<object>(kv.Value))
                .ToList();
            return Results.Ok(logs);
        });
    }
}
