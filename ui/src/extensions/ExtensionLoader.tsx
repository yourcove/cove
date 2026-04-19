/**
 * Extension Runtime - Fetches the extension manifest and integrates all extension
 * contributions into the frontend: routes, slots, tabs, themes, page overrides,
 * dialog overrides, and settings panels.
 *
 * The architecture:
 * - Backend extensions declare UI contributions via UIManifest (JSON)
 * - This loader fetches the manifest once on mount
 * - Declarative contributions (pages, slots, tabs, themes, overrides) are registered
 *   into context-based registries consumed by the UI
 * - Component-based contributions reference built-in POC components (for built-in extensions)
 *   or would load from JS bundles (for external extensions)
 */
import { useEffect, useState, createContext, useContext, useCallback, type ReactNode, type FC } from "react";
import { useRouteRegistry } from "../router/RouteRegistry";
import { extensions } from "../api/client";
import type {
  ExtensionManifest,
  ExtensionThemeDef,
  ExtensionTabContribution,
  ExtensionPageOverride,
  ExtensionDialogOverride,
  ExtensionSettingsPanel,
  ExtensionComponentStyleDef,
  ExtensionLayoutStyleDef,
  ExtensionAction,
} from "../api/types";

// ============================================================================
// Built-in POC extension components (registered by component name)
// These prove that the component resolution system works without external JS bundles.
// External extensions would register components via their JS bundle entry point.
// ============================================================================
import { SceneAnalyticsTab } from "./poc/SceneAnalyticsTab";
import { CustomHomeDashboard } from "./poc/CustomHomeDashboard";
import { SystemToolsPage } from "./poc/SystemToolsPage";
import { NotificationSettingsPanel } from "./poc/NotificationSettingsPanel";
import { EnhancedDeleteDialog } from "./poc/EnhancedDeleteDialog";

/** Global registry mapping componentName → React component. */
const componentRegistry = new Map<string, FC<any>>([
  ["SceneAnalyticsTab", SceneAnalyticsTab],
  ["CustomHomeDashboard", CustomHomeDashboard],
  ["SystemToolsPage", SystemToolsPage],
  ["NotificationSettingsPanel", NotificationSettingsPanel],
  ["EnhancedDeleteDialog", EnhancedDeleteDialog],
]);

/** Resolve a component by name from the registry. */
export function resolveComponent(name: string): FC<any> | undefined {
  return componentRegistry.get(name);
}

/** Register a component into the global registry (for external extensions). */
export function registerComponent(name: string, component: FC<any>) {
  componentRegistry.set(name, component);
}

// ============================================================================
// Extension context — everything the UI needs from the extension system
// ============================================================================
interface ExtensionState {
  manifest: ExtensionManifest | null;
  loaded: boolean;
  error?: string;
  activeThemeId: string | null;
  setActiveTheme: (id: string | null) => void;
  availableThemes: ExtensionThemeDef[];
  activeComponentStyles: Set<string>;
  toggleComponentStyle: (id: string) => void;
  availableComponentStyles: ExtensionComponentStyleDef[];
  activeLayoutStyle: string;
  setActiveLayoutStyle: (id: string) => void;
  availableLayoutStyles: ExtensionLayoutStyleDef[];
  /** Custom color theme variables (user-defined) */
  customThemeColors: Record<string, string>;
  setCustomThemeColors: (colors: Record<string, string>) => void;
  /** Tab contributions for a specific page type */
  getTabsForPage: (pageType: string) => ExtensionTabContribution[];
  /** Page override for a specific built-in page (highest priority wins) */
  getPageOverride: (targetPage: string) => ExtensionPageOverride | undefined;
  /** Dialog override for a specific dialog ID (highest priority wins) */
  getDialogOverride: (dialogId: string) => ExtensionDialogOverride | undefined;
  /** Settings panels contributed by extensions */
  settingsPanels: ExtensionSettingsPanel[];
  /** Actions contributed by extensions (toolbar, context menu, bulk) */
  actions: ExtensionAction[];
  /** Get actions applicable to a given context */
  getActionsForContext: (entityType?: string, page?: string, actionType?: string) => ExtensionAction[];
  /** Resolve a React component by name */
  resolveComponent: (name: string) => FC<any> | undefined;
}

const ExtensionContext = createContext<ExtensionState>({
  manifest: null,
  loaded: false,
  activeThemeId: null,
  setActiveTheme: () => {},
  availableThemes: [],
  activeComponentStyles: new Set(["default"]),
  toggleComponentStyle: () => {},
  availableComponentStyles: [],
  activeLayoutStyle: "default",
  setActiveLayoutStyle: () => {},
  availableLayoutStyles: [],
  customThemeColors: {},
  setCustomThemeColors: () => {},
  getTabsForPage: () => [],
  getPageOverride: () => undefined,
  getDialogOverride: () => undefined,
  settingsPanels: [],
  actions: [],
  getActionsForContext: () => [],
  resolveComponent: () => undefined,
});

export function useExtensions() {
  return useContext(ExtensionContext);
}

const THEME_STORAGE_KEY = "cove-active-theme";
const COMPONENT_STYLE_STORAGE_KEY = "cove-component-style";
const LAYOUT_STYLE_STORAGE_KEY = "cove-layout-style";
const CUSTOM_THEME_STORAGE_KEY = "cove-custom-theme-colors";

function parseStyleSet(raw: string | null): Set<string> {
  if (!raw) return new Set(["default"]);
  const items = raw.split(" ").filter(Boolean);
  return items.length > 0 ? new Set(items) : new Set(["default"]);
}

export function ExtensionLoaderProvider({ children }: { children: ReactNode }) {
  const { register, registerSlot } = useRouteRegistry();
  const [manifest, setManifest] = useState<ExtensionManifest | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [activeThemeId, setActiveThemeIdState] = useState<string | null>(
    () => localStorage.getItem(THEME_STORAGE_KEY) ?? "default"
  );
  const [activeComponentStyles, setActiveComponentStylesState] = useState<Set<string>>(
    () => parseStyleSet(localStorage.getItem(COMPONENT_STYLE_STORAGE_KEY))
  );
  const [activeLayoutStyle, setActiveLayoutStyleState] = useState<string>(
    () => localStorage.getItem(LAYOUT_STYLE_STORAGE_KEY) ?? "default"
  );
  const [customThemeColors, setCustomThemeColorsState] = useState<Record<string, string>>(
    () => {
      try { return JSON.parse(localStorage.getItem(CUSTOM_THEME_STORAGE_KEY) ?? "{}"); } catch { return {}; }
    }
  );

  const setActiveTheme = useCallback((id: string | null) => {
    setActiveThemeIdState(id);
    if (id) {
      localStorage.setItem(THEME_STORAGE_KEY, id);
    } else {
      localStorage.removeItem(THEME_STORAGE_KEY);
    }
  }, []);

  const toggleComponentStyle = useCallback((id: string) => {
    setActiveComponentStylesState((prev) => {
      const next = new Set(prev);
      if (id === "default") {
        // "default" clears all others
        // Clean up all style-specific data attributes
        for (const key of Object.keys(document.documentElement.dataset)) {
          if (key.startsWith("style")) delete document.documentElement.dataset[key];
        }
        return new Set(["default"]);
      }
      next.delete("default"); // remove default when adding a specific style
      if (next.has(id)) {
        next.delete(id);
        // Clean up data attributes for this deactivated style
        const prefix = `style${id.charAt(0).toUpperCase()}${id.slice(1)}`;
        for (const key of Object.keys(document.documentElement.dataset)) {
          if (key.startsWith(prefix)) delete document.documentElement.dataset[key];
        }
        if (next.size === 0) next.add("default");
      } else {
        next.add(id);
      }
      localStorage.setItem(COMPONENT_STYLE_STORAGE_KEY, [...next].join(" "));
      return next;
    });
  }, []);

  const setActiveLayoutStyle = useCallback((id: string) => {
    setActiveLayoutStyleState(id);
    localStorage.setItem(LAYOUT_STYLE_STORAGE_KEY, id);
  }, []);

  const setCustomThemeColors = useCallback((colors: Record<string, string>) => {
    setCustomThemeColorsState(colors);
    localStorage.setItem(CUSTOM_THEME_STORAGE_KEY, JSON.stringify(colors));
  }, []);

  // Fetch manifest on mount
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const m = await extensions.getManifest();
        if (cancelled) return;
        setManifest(m);

        // Register extension pages as routes
        for (const page of m.pages) {
          if (page.showInNav) {
            register({
              page: page.route,
              navItem: {
                page: page.route,
                label: page.label,
                icon: undefined,
                order: page.navOrder,
              },
            });
          }
        }

        // Register slot contributions
        for (const slot of m.slots) {
          if (slot.contentType === "html" && slot.html) {
            registerSlot({
              id: slot.id,
              slot: slot.slot,
              // eslint-disable-next-line react/no-danger
              render: () => <div dangerouslySetInnerHTML={{ __html: slot.html! }} />,
              order: slot.order,
            });
          } else if (slot.contentType === "component" && slot.componentName) {
            const Component = resolveComponent(slot.componentName);
            if (Component) {
              registerSlot({
                id: slot.id,
                slot: slot.slot,
                render: (props) => <Component {...props} />,
                order: slot.order,
              });
            }
          }
        }

        // Dynamically load external JS bundles (ESM modules from third-party extensions)
        if (m.jsBundleUrl) {
          try {
            const mod = await import(/* @vite-ignore */ m.jsBundleUrl);
            if (mod.default?.components) {
              for (const [name, component] of Object.entries(mod.default.components)) {
                if (typeof component === "function") {
                  registerComponent(name, component as FC<any>);
                }
              }
            }
          } catch (err) {
            console.warn("[ExtensionLoader] Failed to load JS bundle:", m.jsBundleUrl, err);
          }
        }

        setLoaded(true);
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : "Failed to load extensions");
          setLoaded(true);
        }
      }
    })();
    return () => { cancelled = true; };
  }, [register, registerSlot]);

  // Apply active theme CSS variables and bundled component style
  useEffect(() => {
    if (!manifest) return;

    const existingStyle = document.getElementById("cove-theme-override");
    if (existingStyle) existingStyle.remove();
    const existingLink = document.getElementById("cove-theme-css");
    if (existingLink) existingLink.remove();

    // Handle custom theme
    if (activeThemeId === "custom") {
      document.documentElement.setAttribute("data-theme", "custom");
      document.documentElement.removeAttribute("data-theme-bg-animation");
      document.documentElement.removeAttribute("data-color-scheme");
      if (Object.keys(customThemeColors).length > 0) {
        const style = document.createElement("style");
        style.id = "cove-theme-override";
        const vars = Object.entries(customThemeColors)
          .map(([key, val]) => `  ${key}: ${val};`)
          .join("\n");
        style.textContent = `:root {\n${vars}\n}`;
        document.head.appendChild(style);
      }
      return () => {
        document.getElementById("cove-theme-override")?.remove();
        document.documentElement.removeAttribute("data-theme");
      };
    }

    // Set data-theme attribute for theme-specific CSS selectors
    if (!activeThemeId) {
      document.documentElement.removeAttribute("data-theme");
      document.documentElement.removeAttribute("data-theme-bg-animation");
      document.documentElement.removeAttribute("data-color-scheme");
      return;
    }
    document.documentElement.setAttribute("data-theme", activeThemeId);

    const theme = manifest.themes.find((t) => t.id === activeThemeId);
    if (!theme) return;

    // If the theme bundles component styles, auto-apply them (unless user overrode)
    if (theme.componentStyle) {
      const userOverride = localStorage.getItem(COMPONENT_STYLE_STORAGE_KEY);
      if (!userOverride) {
        setActiveComponentStylesState(parseStyleSet(theme.componentStyle));
      }
    }

    if (theme.cssVariables && Object.keys(theme.cssVariables).length > 0) {
      const style = document.createElement("style");
      style.id = "cove-theme-override";
      const vars = Object.entries(theme.cssVariables)
        .map(([key, val]) => `  ${key}: ${val};`)
        .join("\n");
      style.textContent = `:root {\n${vars}\n}`;
      document.head.appendChild(style);
    }

    if (theme.cssUrl) {
      const link = document.createElement("link");
      link.id = "cove-theme-css";
      link.rel = "stylesheet";
      link.href = theme.cssUrl;
      document.head.appendChild(link);
    }

    // Apply background animation attribute for themes with custom bg effects
    if (theme.backgroundAnimation) {
      document.documentElement.setAttribute("data-theme-bg-animation", theme.backgroundAnimation);
    } else {
      document.documentElement.removeAttribute("data-theme-bg-animation");
    }

    // Apply color scheme attribute for light/dark mode CSS
    if (theme.colorScheme === "light") {
      document.documentElement.setAttribute("data-color-scheme", "light");
    } else {
      document.documentElement.removeAttribute("data-color-scheme");
    }

    return () => {
      document.getElementById("cove-theme-override")?.remove();
      document.getElementById("cove-theme-css")?.remove();
      document.documentElement.removeAttribute("data-theme");
      document.documentElement.removeAttribute("data-theme-bg-animation");
      document.documentElement.removeAttribute("data-color-scheme");
    };
  }, [activeThemeId, manifest, customThemeColors]);

  // Apply component style data attribute (space-separated for composability)
  useEffect(() => {
    const styleStr = [...activeComponentStyles].join(" ");
    document.documentElement.setAttribute("data-component-style", styleStr);
    return () => { document.documentElement.removeAttribute("data-component-style"); };
  }, [activeComponentStyles]);

  // Apply style options (data attributes + CSS custom properties) at startup
  useEffect(() => {
    try {
      const raw = JSON.parse(localStorage.getItem("cove-style-options") ?? "{}");
      // CSS custom property mapping for range-type style configs
      const cssVarMap: Record<string, Record<string, string>> = {
        gradient: { animated: "--sv-anim-speed", background: "--sv-bg-intensity", cards: "--sv-card-gradient" },
        glass: { cardblur: "--sv-card-blur", surfaceblur: "--sv-surface-blur", opacity: "--sv-surface-opacity" },
        animated: { hover: "--sv-hover-glow" },
      };
      for (const [styleId, opts] of Object.entries(raw)) {
        for (const [key, val] of Object.entries(opts as Record<string, string>)) {
          document.documentElement.dataset[`style${styleId.charAt(0).toUpperCase()}${styleId.slice(1)}${key.charAt(0).toUpperCase()}${key.slice(1)}`] = val;
          const cssVar = cssVarMap[styleId]?.[key];
          if (cssVar) {
            document.documentElement.style.setProperty(cssVar, val);
          }
        }
      }
    } catch { /* ignore parse errors */ }
  }, []);

  // Apply layout style data attribute
  useEffect(() => {
    document.documentElement.setAttribute("data-layout", activeLayoutStyle);
    return () => { document.documentElement.removeAttribute("data-layout"); };
  }, [activeLayoutStyle]);

  // Derived lookups
  const getTabsForPage = useCallback(
    (pageType: string) =>
      manifest?.tabs.filter((t) => t.pageType === pageType) ?? [],
    [manifest]
  );

  const getPageOverride = useCallback(
    (targetPage: string) => {
      const overrides = manifest?.pageOverrides.filter((o) => o.targetPage === targetPage) ?? [];
      return overrides.sort((a, b) => b.priority - a.priority)[0];
    },
    [manifest]
  );

  const getDialogOverride = useCallback(
    (dialogId: string) => {
      const overrides = manifest?.dialogOverrides.filter((o) => o.dialogId === dialogId) ?? [];
      return overrides.sort((a, b) => b.priority - a.priority)[0];
    },
    [manifest]
  );

  const availableThemes = manifest?.themes ?? [];
  const availableComponentStyles = manifest?.componentStyles ?? [];
  const availableLayoutStyles = manifest?.layoutStyles ?? [];
  const settingsPanels = manifest?.settingsPanels ?? [];
  const actions = manifest?.actions ?? [];

  const getActionsForContext = useCallback(
    (entityType?: string, page?: string, actionType?: string) => {
      return actions.filter((a) => {
        if (actionType && a.actionType !== actionType) return false;
        if (entityType && a.entityTypes.length > 0 && !a.entityTypes.includes(entityType)) return false;
        if (page && a.pages && a.pages.length > 0 && !a.pages.includes(page)) return false;
        return true;
      }).sort((a, b) => a.order - b.order);
    },
    [actions]
  );

  return (
    <ExtensionContext.Provider
      value={{
        manifest,
        loaded,
        error,
        activeThemeId,
        setActiveTheme,
        availableThemes,
        activeComponentStyles,
        toggleComponentStyle,
        availableComponentStyles,
        activeLayoutStyle,
        setActiveLayoutStyle,
        availableLayoutStyles,
        customThemeColors,
        setCustomThemeColors,
        getTabsForPage,
        getPageOverride,
        getDialogOverride,
        settingsPanels,
        actions,
        getActionsForContext,
        resolveComponent,
      }}
    >
      {children}
    </ExtensionContext.Provider>
  );
}
