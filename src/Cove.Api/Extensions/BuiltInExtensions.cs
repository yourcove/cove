using Microsoft.Extensions.DependencyInjection;
using Cove.Plugins;

namespace Cove.Api.Extensions;

// ============================================================================
// Built-in theme collection.
// ============================================================================
public class ThemeCollectionExtension : IExtension, IUIExtension
{
    public string Id => "com.cove.themes";
    public string Name => "Theme Collection";
    public string Version => "1.0.0";
    public string? Description => "Built-in theme, style, and layout collection";
    public string? Author => "Cove";
    public string? Url => null;
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.Theme, ExtensionCategories.ColorPalette, ExtensionCategories.Style, ExtensionCategories.Layout];

    public void ConfigureServices(IServiceCollection services, ExtensionContext context) { }

    public UIManifest GetUIManifest() => new()
    {
        ComponentStyles =
        [
            new UIComponentStyleDef("default", "Default", "Balanced corners, defined cards, restrained motion"),
            new UIComponentStyleDef("glass", "Glass", "Frosted glass surfaces with blur and transparency"),
            new UIComponentStyleDef("rounded", "Rounded", "Softer corners and friendlier panel geometry"),
            new UIComponentStyleDef("gradient", "Gradient", "Accent-driven gradients across cards, surfaces, and the background"),
            new UIComponentStyleDef("animated", "Animated", "Lift, shimmer, and accent trails on interactive surfaces"),
            new UIComponentStyleDef("floating", "Floating", "Frameless media-first cards that sit directly on the page"),
        ],
        LayoutStyles =
        [
            new UILayoutStyleDef("default", "Default", "Standard layout with top navigation"),
            new UILayoutStyleDef("detail-theater", "Theater Detail", "Places detail metadata in a right rail so media leads the page"),
            new UILayoutStyleDef("detail-tabs", "Detail Tabs", "Uses a horizontal tab strip on media detail pages instead of the side icon rail"),
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
                Description: "A nostalgic theme",
                CssVariables: new()
                {
                    ["--color-background"] = "#202b33",
                    ["--color-nav"] = "#1a2329",
                    ["--color-card"] = "#30404d",
                    ["--color-card-hover"] = "#394b59",
                    ["--color-surface"] = "#283540",
                    ["--color-border"] = "#394b59",
                    ["--color-input"] = "rgba(16, 22, 26, 0.3)",
                    ["--color-accent"] = "#137cbd",
                    ["--color-accent-hover"] = "#48aff0",
                    ["--color-foreground"] = "#f5f8fa",
                    ["--color-secondary"] = "#bfccd6",
                    ["--color-muted"] = "#8a9ba8",
                    ["--color-overlay"] = "rgba(0, 0, 0, 0.6)",
                    ["--color-nav-active"] = "#137cbd",
                }
            ),
            new UIThemeDefinition(
                Id: "light",
                Name: "Light",
                Description: "Clean light theme with blue accents",
                ColorScheme: "light",
                CssVariables: new()
                {
                    ["--color-background"] = "#c5cad4",
                    ["--color-nav"] = "#b8bdc7",
                    ["--color-card"] = "#e0e5ec",
                    ["--color-card-hover"] = "#d6dbe4",
                    ["--color-surface"] = "#d8dde5",
                    ["--color-border"] = "#a8aeb8",
                    ["--color-input"] = "rgba(0, 0, 0, 0.08)",
                    ["--color-accent"] = "#2563eb",
                    ["--color-accent-hover"] = "#1d4ed8",
                    ["--color-foreground"] = "#111827",
                    ["--color-secondary"] = "#4a5060",
                    ["--color-muted"] = "#7a8194",
                    ["--color-overlay"] = "rgba(0, 0, 0, 0.3)",
                    ["--color-nav-active"] = "#2563eb",
                    ["--color-shell-bg"] = "#c5cad4",
                    ["--color-shell-nav"] = "#b8bdc7",
                    ["--color-shell-surface"] = "#d8dde5",
                    ["--color-shell-card"] = "#e0e5ec",
                    ["--color-shell-card-hover"] = "#d6dbe4",
                    ["--color-shell-border"] = "#a8aeb8",
                    ["--color-shell-input"] = "rgba(0, 0, 0, 0.08)",
                    ["--color-shell-text"] = "#111827",
                    ["--color-shell-text-secondary"] = "#4a5060",
                    ["--color-shell-text-muted"] = "#7a8194",
                    ["--color-shell-accent"] = "#2563eb",
                    ["--color-shell-accent-hover"] = "#1d4ed8",
                    ["--color-shell-overlay"] = "rgba(0, 0, 0, 0.3)",
                    ["--color-shell-nav-active"] = "#2563eb",
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
                Description: "Dark slate with restrained emerald accents and softer contrast",
                CssVariables: new()
                {
                    ["--color-background"] = "#0d1412",
                    ["--color-nav"] = "#111916",
                    ["--color-card"] = "#18211d",
                    ["--color-card-hover"] = "#1f2a25",
                    ["--color-surface"] = "#151d1a",
                    ["--color-border"] = "#24332d",
                    ["--color-input"] = "rgba(0, 0, 0, 0.24)",
                    ["--color-accent"] = "#3bbd83",
                    ["--color-accent-hover"] = "#5fd6a0",
                    ["--color-foreground"] = "#eef8f2",
                    ["--color-secondary"] = "#9db9ad",
                    ["--color-muted"] = "#648378",
                    ["--color-overlay"] = "rgba(0, 0, 0, 0.55)",
                    ["--color-nav-active"] = "#2f9f74",
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
                Id: "copper-noir",
                Name: "Copper Noir",
                Description: "High-contrast dark theme with vivid orange accents",
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
                Id: "golden-hour",
                Name: "Golden Hour",
                Description: "Dark theme with warm gold accents",
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
                Id: "signal-dark",
                Name: "Signal Dark",
                Description: "Dark theme with cool neutrals and bright accent contrast",
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
                Description: "Dramatic translucent glass with vivid gradient highlights",
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
                Description: "Electric cyan, acid gold, and sharp noir surfaces with hard neon contrast",
                CssVariables: new()
                {
                    ["--color-background"] = "#08090d",
                    ["--color-nav"] = "#0b0f14",
                    ["--color-card"] = "#101722",
                    ["--color-card-hover"] = "#17212e",
                    ["--color-surface"] = "#0e141f",
                    ["--color-border"] = "#1f3141",
                    ["--color-input"] = "#06090d",
                    ["--color-accent"] = "#00eaff",
                    ["--color-accent-hover"] = "#ff4fd8",
                    ["--color-foreground"] = "#f4fbff",
                    ["--color-secondary"] = "#9fc5d4",
                    ["--color-muted"] = "#547184",
                    ["--color-overlay"] = "rgba(0,0,0,0.65)",
                    ["--color-nav-active"] = "#ffe45c",
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
                Description: "Violet dusk, sunset orange, and pop magenta for a warmer 80s neon feel",
                CssVariables: new()
                {
                    ["--color-background"] = "#14071f",
                    ["--color-nav"] = "#1a0a28",
                    ["--color-card"] = "#24103a",
                    ["--color-card-hover"] = "#2f1748",
                    ["--color-surface"] = "#1d0d31",
                    ["--color-border"] = "#47205f",
                    ["--color-input"] = "#0d0418",
                    ["--color-accent"] = "#ff5cab",
                    ["--color-accent-hover"] = "#7c6dff",
                    ["--color-foreground"] = "#fbeeff",
                    ["--color-secondary"] = "#c09bd8",
                    ["--color-muted"] = "#7e5a97",
                    ["--color-overlay"] = "rgba(0,0,0,0.62)",
                    ["--color-nav-active"] = "#ffb86b",
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
            new UIThemeDefinition(
                Id: "cinema-dark",
                Name: "Cinema Dark",
                Description: "Dark video-first browsing with red accents and floating media tiles",
                CssVariables: new()
                {
                    ["--color-background"] = "#0f0f0f",
                    ["--color-nav"] = "#111111",
                    ["--color-card"] = "#181818",
                    ["--color-card-hover"] = "#242424",
                    ["--color-surface"] = "#1a1a1a",
                    ["--color-border"] = "#303030",
                    ["--color-input"] = "#121212",
                    ["--color-accent"] = "#ff0033",
                    ["--color-accent-hover"] = "#ff4e45",
                    ["--color-foreground"] = "#f1f1f1",
                    ["--color-secondary"] = "#c0c0c0",
                    ["--color-muted"] = "#8b8b8b",
                    ["--color-overlay"] = "rgba(0,0,0,0.7)",
                    ["--color-nav-active"] = "#ffffff",
                },
                ComponentStyle: "floating",
                LayoutStyle: "detail-theater detail-tabs"
            ),
        ]
    };
}
