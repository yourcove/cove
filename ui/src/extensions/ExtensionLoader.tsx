/**
 * Extension Runtime - Fetches the extension manifest and integrates all extension
 * contributions into the frontend: routes, slots, tabs, themes, page overrides,
 * and settings panels.
 *
 * The architecture:
 * - Backend extensions declare UI contributions via UIManifest (JSON)
 * - This loader fetches the manifest on mount and reconciles it after runtime changes
 * - Declarative contributions (pages, slots, tabs, themes, overrides) are registered
 *   into context-based registries consumed by the UI
 * - Component-based contributions reference built-in POC components (for built-in extensions)
 *   or would load from JS bundles (for external extensions)
 */
import {
  forwardRef,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  createContext,
  useContext,
  useCallback,
  useMemo,
  type ReactNode,
} from "react";
import { useRouteRegistry } from "../router/RouteRegistry";
import { useAppConfig } from "../state/AppConfigContext";
import { extensions } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { supportsServerBackedUiPreferences, updateAuthenticatedUserUiPreferences } from "../utils/userUiPreferences";
import { canAccessExtensionContribution } from "./extension-permissions";
import { Puzzle, type LucideIcon, type LucideProps } from "lucide-react";
import { DynamicIcon, iconNames, type IconName } from "lucide-react/dynamic.js";
import type {
  ExtensionManifest,
  ExtensionFeatureDef,
  ExtensionThemeDef,
  ExtensionTabContribution,
  ExtensionPageOverride,
  ExtensionSettingsTab,
  ExtensionSettingsPanel,
  ExtensionComponentStyleDef,
  ExtensionLayoutStyleDef,
  ExtensionAction,
  ExtensionComponentOverride,
  ExtensionListFilterContribution,
  ExtensionListSortContribution,
  UserThemePreferences,
} from "../api/types";
import {
  FALLBACK_DEFAULT_THEME,
  STYLE_OPTION_CSS_VARS,
  THEME_BOOT_VERSION,
  clearThemeBootArtifacts,
  clearThemeBootSnapshot,
  readThemeBootSnapshot,
  styleOptionDatasetKey,
  writeThemeBootSnapshot,
  type ThemeBootSnapshot,
} from "../theme/themeBoot";
import { ExtensionComponentRegistry, type ExtensionComponent } from "./ExtensionComponentRegistry";
import { ExtensionComponentOverrideHost } from "./ExtensionComponentOverrideHost";
import {
  createExtensionRuntimeReconciler,
  type ExtensionRuntimeOwner,
  type ExtensionRuntimeFailure,
} from "./ExtensionRuntimeReconciler";

// ============================================================================
// Icon resolver — maps manifest icon names to Lucide components
// ============================================================================
const validIconNames = new Set<string>(iconNames);
const iconComponents = new Map<IconName, LucideIcon>();
function resolveIcon(name?: string): LucideIcon {
  const normalizedName = name?.toLowerCase();
  if (!normalizedName || !validIconNames.has(normalizedName)) return Puzzle;

  const iconName = normalizedName as IconName;
  let Icon = iconComponents.get(iconName);
  if (!Icon) {
    // Keep the component type stable when manifest refreshes register routes again.
    Icon = forwardRef<SVGSVGElement, LucideProps>((props, ref) => <DynamicIcon {...props} name={iconName} ref={ref} />);
    Icon.displayName = `ExtensionIcon(${iconName})`;
    iconComponents.set(iconName, Icon);
  }
  return Icon;
}

type ExtensionActionHandler = (action: ExtensionAction, payload: Record<string, unknown>) => Promise<unknown> | unknown;
type BundleImporter = (url: string) => Promise<unknown>;

const LEGACY_BUNDLE_OWNER = Symbol("Cove aggregate extension bundle");
const LEGACY_BUNDLE_STYLE_OWNER = "legacy-aggregate";
const defaultImportBundle: BundleImporter = (url) => import(/* @vite-ignore */ url);
const ExtensionComponentRegistryContext = createContext<ExtensionComponentRegistry | null>(null);
let runtimeOwnerSequence = 0;

function createRuntimeOwnerId() {
  return globalThis.crypto?.randomUUID?.() ?? `cove-extension-runtime-${++runtimeOwnerSequence}`;
}

// ============================================================================
// Extension context — everything the UI needs from the extension system
// ============================================================================
interface ExtensionState {
  manifest: ExtensionManifest | null;
  loaded: boolean;
  error?: string;
  loadFailures: ExtensionRuntimeFailure[];
  retryFailedExtensions: () => Promise<void>;
  /** Refetch and fully reconcile the extension manifest. Returns the applied manifest. */
  refreshManifest: () => Promise<ExtensionManifest | null>;
  activeThemeId: string | null;
  setActiveTheme: (id: string | null) => void;
  availableThemes: ExtensionThemeDef[];
  activeComponentStyles: Set<string>;
  toggleComponentStyle: (id: string) => void;
  availableComponentStyles: ExtensionComponentStyleDef[];
  activeLayoutStyles: Set<string>;
  toggleLayoutStyle: (id: string) => void;
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
  /** Component overrides for a stable host component key */
  getComponentOverrides: (targetComponent: string) => ExtensionComponentOverride[];
  /** Feature capabilities contributed by extensions */
  features: ExtensionFeatureDef[];
  /** Resolve a feature capability by stable key */
  getFeature: (key: string) => ExtensionFeatureDef | undefined;
  /** Settings tabs contributed by extensions */
  settingsTabs: ExtensionSettingsTab[];
  /** Settings panels contributed by extensions */
  settingsPanels: ExtensionSettingsPanel[];
  /** Get settings panels for a specific settings tab (e.g. "library", "interface") */
  getSettingsPanelsForTab: (tab: string, section?: string) => ExtensionSettingsPanel[];
  /** Actions contributed by extensions (toolbar, context menu, bulk) */
  actions: ExtensionAction[];
  /** Get actions applicable to a given context */
  getActionsForContext: (entityType?: string, page?: string, actionType?: string) => ExtensionAction[];
  /** List filters contributed by extensions */
  getListFiltersForEntity: (entityType: string) => ExtensionListFilterContribution[];
  /** List sorts contributed by extensions */
  getListSortsForEntity: (entityType: string) => ExtensionListSortContribution[];
  /** Resolve a React component by extension owner and export name */
  resolveComponent: (extensionId: string, name: string) => ExtensionComponent | undefined;
  /** Resolve a runtime action handler by extension owner and export name */
  resolveActionHandler: (extensionId: string, name: string) => ExtensionActionHandler | undefined;
  /** Revision of the active component exports for one extension owner */
  getExtensionRevision: (extensionId: string) => number;
}

const ExtensionContext = createContext<ExtensionState>({
  manifest: null,
  loaded: false,
  loadFailures: [],
  retryFailedExtensions: async () => {},
  refreshManifest: async () => null,
  activeThemeId: null,
  setActiveTheme: () => {},
  availableThemes: [],
  activeComponentStyles: new Set(["default"]),
  toggleComponentStyle: () => {},
  availableComponentStyles: [],
  activeLayoutStyles: new Set(["default"]),
  toggleLayoutStyle: () => {},
  activeLayoutStyle: "default",
  setActiveLayoutStyle: () => {},
  availableLayoutStyles: [],
  customThemeColors: {},
  setCustomThemeColors: () => {},
  getTabsForPage: () => [],
  getPageOverride: () => undefined,
  getComponentOverrides: () => [],
  features: [],
  getFeature: () => undefined,
  settingsTabs: [],
  settingsPanels: [],
  getSettingsPanelsForTab: () => [],
  actions: [],
  getActionsForContext: () => [],
  getListFiltersForEntity: () => [],
  getListSortsForEntity: () => [],
  resolveComponent: () => undefined,
  resolveActionHandler: () => undefined,
  getExtensionRevision: () => 0,
});

export function useExtensions() {
  return useContext(ExtensionContext);
}

interface ExtensionComponentOverrideRendererProps<TProps extends object> {
  targetComponent: string;
  componentProps: TProps;
  renderDefault: () => ReactNode;
  resetKey?: unknown;
}

/** Connects the generic override middleware host to the active extension runtime. */
export function ExtensionComponentOverrideRenderer<TProps extends object>({
  targetComponent,
  componentProps,
  renderDefault,
  resetKey,
}: ExtensionComponentOverrideRendererProps<TProps>) {
  const registry = useContext(ExtensionComponentRegistryContext);
  const { getComponentOverrides } = useExtensions();
  if (!registry) return <>{renderDefault()}</>;

  return (
    <ExtensionComponentOverrideHost
      targetComponent={targetComponent}
      contributions={getComponentOverrides(targetComponent)}
      registry={registry}
      componentProps={componentProps}
      renderDefault={renderDefault}
      resetKey={resetKey}
    />
  );
}

const THEME_STORAGE_KEY = "cove-active-theme";
const COMPONENT_STYLE_STORAGE_KEY = "cove-component-style";
const LAYOUT_STYLE_STORAGE_KEY = "cove-layout-style";
const CUSTOM_THEME_STORAGE_KEY = "cove-custom-theme-colors";
const STYLE_OPTIONS_STORAGE_KEY = "cove-style-options";

function normalizeListEntityType(entityType: string) {
  const normalized = entityType.trim().toLowerCase();
  return normalized.endsWith("s") ? normalized.slice(0, -1) : normalized;
}

function compareOrdinal(a: string, b: string) {
  return a < b ? -1 : a > b ? 1 : 0;
}

function parseStyleSet(raw: string | null): Set<string> {
  if (!raw) return new Set(["default"]);
  const items = raw.split(" ").filter(Boolean);
  return items.length > 0 ? new Set(items) : new Set(["default"]);
}

function parseLayoutSet(raw: string | null): Set<string> {
  if (!raw) return new Set(["default"]);
  const removedBuiltInLayouts = new Set(["compact", "wide", "control-rail"]);
  const items = raw.split(" ").filter((item) => item && !removedBuiltInLayouts.has(item));
  return items.length > 0 ? new Set(items) : new Set(["default"]);
}

function readStoredThemeColors(): Record<string, string> {
  try {
    return JSON.parse(localStorage.getItem(CUSTOM_THEME_STORAGE_KEY) ?? "{}");
  } catch {
    return {};
  }
}

function readStoredStyleOptions(): Record<string, Record<string, string>> {
  try {
    const parsed = JSON.parse(localStorage.getItem(STYLE_OPTIONS_STORAGE_KEY) ?? "{}");
    return parsed && typeof parsed === "object" ? (parsed as Record<string, Record<string, string>>) : {};
  } catch {
    return {};
  }
}

function runtimeBundleDescriptors(manifest: ExtensionManifest) {
  const ownedBundles = (manifest.extensionBundles ?? []).flatMap((bundle) =>
    bundle.jsBundleUrl
      ? [
          {
            extensionId: bundle.extensionId,
            version: bundle.version,
            jsBundleUrl: bundle.jsBundleUrl,
            dependencies: bundle.dependencies,
          },
        ]
      : [],
  );

  if (ownedBundles.length > 0) return ownedBundles;
  return manifest.jsBundleUrl ? [{ extensionId: LEGACY_BUNDLE_OWNER, jsBundleUrl: manifest.jsBundleUrl }] : [];
}

function usesLegacyRuntimeBundle(manifest: ExtensionManifest) {
  return (
    !(manifest.extensionBundles ?? []).some((bundle) => Boolean(bundle.jsBundleUrl)) && Boolean(manifest.jsBundleUrl)
  );
}

// All owned contribution arrays use extensionId. Keep declarative-only extensions,
// but withdraw every surface (including CSS) belonging to a failed browser bundle.
function withoutFailedContributions(
  manifest: ExtensionManifest,
  failures: ExtensionRuntimeFailure[],
): ExtensionManifest {
  const failedOwners = new Set(failures.map((failure) => failure.extensionId));
  const legacyFailed = failedOwners.has(LEGACY_BUNDLE_OWNER);
  return Object.fromEntries(
    Object.entries(manifest).map(([key, value]) => [
      key,
      Array.isArray(value)
        ? value.filter((item) => !legacyFailed && !failedOwners.has(item.extensionId))
        : (key === "jsBundleUrl" || key === "cssBundleUrl") && failures.length > 0
          ? undefined
          : value,
    ]),
  ) as unknown as ExtensionManifest;
}

function removeExtensionBundleStyles(runtimeOwnerId: string) {
  document
    .querySelectorAll(`link[data-cove-extension-runtime-owner="${runtimeOwnerId}"]`)
    .forEach((element) => element.remove());
}

function reconcileExtensionBundleStyles(manifest: ExtensionManifest, runtimeOwnerId: string) {
  removeExtensionBundleStyles(runtimeOwnerId);
  document.getElementById("cove-extension-css-bundle")?.remove();

  const ownedStyles = (manifest.extensionBundles ?? []).flatMap((bundle) =>
    bundle.cssBundleUrl ? [{ extensionId: bundle.extensionId, cssBundleUrl: bundle.cssBundleUrl }] : [],
  );
  const styles =
    ownedStyles.length > 0
      ? ownedStyles
      : manifest.cssBundleUrl
        ? [{ extensionId: LEGACY_BUNDLE_STYLE_OWNER, cssBundleUrl: manifest.cssBundleUrl }]
        : [];

  for (const style of styles) {
    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = style.cssBundleUrl;
    link.dataset.coveExtensionBundle = "true";
    link.dataset.extensionId = style.extensionId;
    link.dataset.coveExtensionRuntimeOwner = runtimeOwnerId;
    document.head.appendChild(link);
  }
}

interface ExtensionLoaderProviderProps {
  children: ReactNode;
  importBundle?: BundleImporter;
}

export function ExtensionLoaderProvider({
  children,
  importBundle = defaultImportBundle,
}: ExtensionLoaderProviderProps) {
  const { register, registerSlot } = useRouteRegistry();
  const { config } = useAppConfig();
  const { user, hasPermission } = useAuth();
  const troubleshootingMode = config?.ui.troubleshootingModeEnabled === true;
  const hasServerBackedUiPreferences = supportsServerBackedUiPreferences(user);
  const userThemePreferencesJson = JSON.stringify(
    hasServerBackedUiPreferences ? (user.uiPreferences?.theme ?? null) : null,
  );
  // Auth-store updates for unrelated preferences (saved filters, playback, etc.) replace the user
  // object. Keep the semantic theme value referentially stable so those updates cannot tear down and
  // reapply component/layout styles for a frame.
  const userThemePreferences = useMemo(
    () => JSON.parse(userThemePreferencesJson) as UserThemePreferences | null,
    [userThemePreferencesJson],
  );
  const [manifest, setManifest] = useState<ExtensionManifest | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [loadFailures, setLoadFailures] = useState<ExtensionRuntimeFailure[]>([]);
  const sourceManifest = useRef<ExtensionManifest | null>(null);
  const retryUrls = useRef(new Map<string, string>());
  const manifestRequestGeneration = useRef(0);
  const unmountDisposal = useRef<Promise<void> | null>(null);
  const legacyBundleActive = useRef(false);
  const runtimeOwnerId = useMemo(createRuntimeOwnerId, []);
  const [componentRegistry] = useState(() => new ExtensionComponentRegistry());
  const [actionHandlerRegistry] = useState(() => new Map<ExtensionRuntimeOwner, Map<string, ExtensionActionHandler>>());
  const runtimeReconciler = useMemo(
    () =>
      createExtensionRuntimeReconciler<ExtensionComponent, ExtensionActionHandler>({
        importBundle: (url) => importBundle(retryUrls.current.get(url) ?? url),
        registrations: {
          register(extensionId, registration) {
            componentRegistry.register(extensionId, registration.components);
            actionHandlerRegistry.set(extensionId, new Map(Object.entries(registration.actionHandlers)));

            let unregistered = false;
            return () => {
              if (unregistered) return;
              unregistered = true;
              componentRegistry.unregister(extensionId);
              actionHandlerRegistry.delete(extensionId);
            };
          },
        },
      }),
    [actionHandlerRegistry, componentRegistry, importBundle],
  );
  const resolveComponent = useCallback(
    (extensionId: string, name: string) =>
      componentRegistry.resolve(extensionId, name) ??
      (legacyBundleActive.current ? componentRegistry.resolve(LEGACY_BUNDLE_OWNER, name) : undefined),
    [componentRegistry],
  );
  const resolveActionHandler = useCallback(
    (extensionId: string, name: string) =>
      actionHandlerRegistry.get(extensionId)?.get(name) ??
      (legacyBundleActive.current ? actionHandlerRegistry.get(LEGACY_BUNDLE_OWNER)?.get(name) : undefined),
    [actionHandlerRegistry],
  );
  const getExtensionRevision = useCallback(
    (extensionId: string) =>
      componentRegistry.getRevision(extensionId) ||
      (legacyBundleActive.current ? componentRegistry.getRevision(LEGACY_BUNDLE_OWNER) : 0),
    [componentRegistry],
  );
  const [activeThemeId, setActiveThemeIdState] = useState<string | null>(
    () => userThemePreferences?.activeThemeId ?? localStorage.getItem(THEME_STORAGE_KEY) ?? "default",
  );
  const [activeComponentStyles, setActiveComponentStylesState] = useState<Set<string>>(() =>
    parseStyleSet(
      userThemePreferences?.activeComponentStyles?.join(" ") ?? localStorage.getItem(COMPONENT_STYLE_STORAGE_KEY),
    ),
  );
  const [activeLayoutStyles, setActiveLayoutStylesState] = useState<Set<string>>(() =>
    parseLayoutSet(userThemePreferences?.activeLayoutStyle ?? localStorage.getItem(LAYOUT_STYLE_STORAGE_KEY)),
  );
  const activeLayoutStyle = useMemo(() => [...activeLayoutStyles].join(" "), [activeLayoutStyles]);
  const [customThemeColors, setCustomThemeColorsState] = useState<Record<string, string>>(
    () => userThemePreferences?.customThemeColors ?? readStoredThemeColors(),
  );
  const availableThemes = useMemo(() => {
    const manifestThemes = manifest?.themes ?? [];
    return manifestThemes.some((theme) => theme.id === FALLBACK_DEFAULT_THEME.id)
      ? manifestThemes
      : [FALLBACK_DEFAULT_THEME, ...manifestThemes];
  }, [manifest]);
  const selectedTheme = useMemo(
    () =>
      activeThemeId && activeThemeId !== "custom"
        ? (availableThemes.find((theme) => theme.id === activeThemeId) ??
          (activeThemeId === FALLBACK_DEFAULT_THEME.id ? FALLBACK_DEFAULT_THEME : null))
        : null,
    [activeThemeId, availableThemes],
  );
  const hasUserComponentStyleOverride = useMemo(
    () =>
      hasServerBackedUiPreferences
        ? (userThemePreferences?.activeComponentStyles?.length ?? 0) > 0
        : Boolean(localStorage.getItem(COMPONENT_STYLE_STORAGE_KEY)),
    [hasServerBackedUiPreferences, userThemePreferences],
  );
  const hasUserLayoutStyleOverride = useMemo(
    () =>
      hasServerBackedUiPreferences
        ? Boolean(userThemePreferences?.activeLayoutStyle?.trim())
        : Boolean(localStorage.getItem(LAYOUT_STYLE_STORAGE_KEY)),
    [hasServerBackedUiPreferences, userThemePreferences],
  );

  const setActiveTheme = useCallback((id: string | null) => {
    setActiveThemeIdState(id);
    if (id) {
      localStorage.setItem(THEME_STORAGE_KEY, id);
    } else {
      localStorage.removeItem(THEME_STORAGE_KEY);
    }
    updateAuthenticatedUserUiPreferences((current) => ({
      ...current,
      theme: {
        ...current?.theme,
        activeThemeId: id,
      },
    }));
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
      updateAuthenticatedUserUiPreferences((current) => ({
        ...current,
        theme: {
          ...current?.theme,
          activeComponentStyles: [...next],
        },
      }));
      return next;
    });
  }, []);

  const persistLayoutStyles = useCallback((next: Set<string>) => {
    const value = [...next].join(" ");
    localStorage.setItem(LAYOUT_STYLE_STORAGE_KEY, value);
    updateAuthenticatedUserUiPreferences((current) => ({
      ...current,
      theme: {
        ...current?.theme,
        activeLayoutStyle: value,
      },
    }));
  }, []);

  const setActiveLayoutStyle = useCallback(
    (id: string) => {
      const next = parseLayoutSet(id);
      setActiveLayoutStylesState(next);
      persistLayoutStyles(next);
    },
    [persistLayoutStyles],
  );

  const toggleLayoutStyle = useCallback(
    (id: string) => {
      setActiveLayoutStylesState((prev) => {
        const next = new Set(prev);
        if (id === "default") {
          const onlyDefault = new Set(["default"]);
          persistLayoutStyles(onlyDefault);
          return onlyDefault;
        }
        next.delete("default");
        if (next.has(id)) next.delete(id);
        else next.add(id);
        if (next.size === 0) next.add("default");
        persistLayoutStyles(next);
        return next;
      });
    },
    [persistLayoutStyles],
  );

  const setCustomThemeColors = useCallback((colors: Record<string, string>) => {
    setCustomThemeColorsState(colors);
    localStorage.setItem(CUSTOM_THEME_STORAGE_KEY, JSON.stringify(colors));
    updateAuthenticatedUserUiPreferences((current) => ({
      ...current,
      theme: {
        ...current?.theme,
        customThemeColors: colors,
      },
    }));
  }, []);

  // Adopt server-backed theme preferences whenever they change. Null until the first render so
  // preferences present at mount are applied too, replacing any localStorage fallback.
  const [prevServerTheme, setPrevServerTheme] = useState<{
    hasServerBackedUiPreferences: boolean;
    userThemePreferences: UserThemePreferences | null;
  } | null>(null);
  if (
    prevServerTheme === null ||
    prevServerTheme.hasServerBackedUiPreferences !== hasServerBackedUiPreferences ||
    prevServerTheme.userThemePreferences !== userThemePreferences
  ) {
    setPrevServerTheme({ hasServerBackedUiPreferences, userThemePreferences });
    const nextTheme = hasServerBackedUiPreferences ? userThemePreferences : null;
    if (nextTheme) {
      setActiveThemeIdState(nextTheme.activeThemeId ?? "default");
      setActiveComponentStylesState(parseStyleSet(nextTheme.activeComponentStyles?.join(" ") ?? null));
      setActiveLayoutStylesState(parseLayoutSet(nextTheme.activeLayoutStyle ?? null));
      setCustomThemeColorsState(nextTheme.customThemeColors ?? {});
    }
  }

  // A selected theme that is missing from `availableThemes` is deliberately NOT rewritten to the
  // default here. A theme is absent for two reasons this side cannot tell apart — it was
  // uninstalled, or it is transiently missing because the manifest request failed, troubleshooting
  // mode withdrew every extension, a refresh superseded the initial load, or its bundle failed to
  // import (see `withoutFailedContributions`) — and writing the fallback back to localStorage and
  // the server turned every transient case into permanent loss of the user's choice.
  //
  // Nothing needs to replace it: the stored selection is kept, reapplied the moment the theme is
  // offered again, and the UI already shows the default look meanwhile. `selectedTheme` resolves to
  // null, and `data-theme` is not left stale — on a failed first load it was never set, and when a
  // loaded manifest goes away (troubleshooting mode) the theme effect's own cleanup removes it.

  const applyManifest = useCallback(
    async (nextManifest: ExtensionManifest, requestGeneration: number) => {
      if (requestGeneration !== manifestRequestGeneration.current) return null;
      const reconciled = await runtimeReconciler.reconcile(runtimeBundleDescriptors(nextManifest), {
        isCurrent: () => requestGeneration === manifestRequestGeneration.current,
      });
      if (!reconciled || requestGeneration !== manifestRequestGeneration.current) return null;
      const failures = runtimeReconciler.getFailures();
      const availableManifest = withoutFailedContributions(nextManifest, failures);
      sourceManifest.current = nextManifest;
      setLoadFailures(failures);
      legacyBundleActive.current = usesLegacyRuntimeBundle(availableManifest);
      reconcileExtensionBundleStyles(availableManifest, runtimeOwnerId);
      setManifest(availableManifest);
      setError(undefined);
      return availableManifest;
    },
    [runtimeOwnerId, runtimeReconciler],
  );

  // Fetch and register the initial manifest. Troubleshooting mode reconciles to an
  // empty runtime immediately, withdrawing all declarative and module contributions.
  useEffect(() => {
    let cancelled = false;

    if (troubleshootingMode) {
      manifestRequestGeneration.current += 1;
      // oxlint-disable-next-line react/set-state-in-effect -- marks the runtime as loading while this effect unloads every extension bundle
      setLoaded(false);
      void runtimeReconciler
        .reconcile([])
        .then(() => {
          if (cancelled) return;
          legacyBundleActive.current = false;
          removeExtensionBundleStyles(runtimeOwnerId);
          setManifest(null);
          sourceManifest.current = null;
          setLoadFailures([]);
          setError(undefined);
          setLoaded(true);
        })
        .catch((err) => {
          if (cancelled) return;
          console.warn("[ExtensionLoader] Failed to unload extension bundles:", err);
          setError(err instanceof Error ? err.message : "Failed to unload extensions");
          setLoaded(true);
        });
      return () => {
        cancelled = true;
      };
    }

    setLoaded(false);
    const requestGeneration = ++manifestRequestGeneration.current;
    void (async () => {
      try {
        const nextManifest = await extensions.getManifest();
        if (cancelled) return;
        await applyManifest(nextManifest, requestGeneration);
        if (!cancelled) setLoaded(true);
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : "Failed to load extensions");
          setLoaded(true);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [applyManifest, runtimeOwnerId, runtimeReconciler, troubleshootingMode]);

  // Layout cleanup runs before the declarative route/slot cleanup below. On
  // provider unmount this lets lifecycle hooks finish before their registrations
  // are withdrawn; on a reconciler replacement the next setup clears the marker.
  useLayoutEffect(() => {
    unmountDisposal.current = null;
    return () => {
      manifestRequestGeneration.current += 1;
      const disposal = runtimeReconciler.dispose();
      unmountDisposal.current = disposal;
      void disposal
        .catch((err) => {
          console.warn("[ExtensionLoader] Failed to dispose extension runtime:", err);
        })
        .finally(() => {
          if (unmountDisposal.current === disposal) {
            removeExtensionBundleStyles(runtimeOwnerId);
          }
        });
    };
  }, [runtimeOwnerId, runtimeReconciler]);

  // Declarative routes and slots follow manifest state, so refreshes cleanly remove
  // the previous extension set before registering the next one.
  useLayoutEffect(() => {
    if (!manifest || troubleshootingMode) return;

    const unregisterRoutes: Array<() => void> = [];
    const unregisterSlots: Array<() => void> = [];

    for (const page of manifest.pages ?? []) {
      if (page.showInNav && canAccessExtensionContribution(page, hasPermission)) {
        unregisterRoutes.push(
          register({
            page: page.route,
            navItem: {
              page: page.route,
              label: page.label,
              icon: resolveIcon(page.icon),
              order: page.navOrder,
            },
          }),
        );
      }
    }

    for (const slot of manifest.slots ?? []) {
      if (slot.contentType === "html" && slot.html) {
        unregisterSlots.push(
          registerSlot({
            id: slot.id,
            extensionId: slot.extensionId,
            slot: slot.slot,
            render: () => <div dangerouslySetInnerHTML={{ __html: slot.html! }} />,
            order: slot.order,
          }),
        );
      } else if (slot.contentType === "component" && slot.componentName) {
        const Component = resolveComponent(slot.extensionId, slot.componentName);
        if (Component) {
          unregisterSlots.push(
            registerSlot({
              id: slot.id,
              extensionId: slot.extensionId,
              slot: slot.slot,
              render: (props) => <Component {...props} />,
              order: slot.order,
              resetKey: getExtensionRevision(slot.extensionId),
            }),
          );
        }
      }
    }

    const unregisterContributions = () => {
      unregisterRoutes.forEach((unregisterRoute) => unregisterRoute());
      unregisterSlots.forEach((unregisterSlot) => unregisterSlot());
    };
    return () => {
      const disposal = unmountDisposal.current;
      if (disposal) {
        void disposal.then(unregisterContributions, unregisterContributions);
      } else {
        unregisterContributions();
      }
    };
  }, [getExtensionRevision, hasPermission, manifest, register, registerSlot, resolveComponent, troubleshootingMode]);

  // Withdraw the theme the inline boot script painted, where React must not simply replace it.
  //
  // Replacement is mostly free: the theme effect below removes #cove-theme-override and
  // #cove-theme-css by id before applying, so whichever branch of it runs already takes them over.
  // Only two cases never reach it -- troubleshooting mode, and a snapshot cached for another user.
  //
  // A failed manifest request is deliberately not one of them. It says nothing about what the user
  // chose, so they keep the look they had rather than being repainted into the default theme; this
  // is the runtime twin of keeping a theme that is missing from the manifest.
  useEffect(() => {
    if (troubleshootingMode) {
      clearThemeBootArtifacts(document);
      clearThemeBootSnapshot();
      return;
    }
    // /me can resolve to a different account than the one cached when the page was parsed.
    const snapshot = readThemeBootSnapshot();
    if (snapshot && snapshot.u !== (user?.id ?? null)) {
      clearThemeBootArtifacts(document);
      clearThemeBootSnapshot();
    }
  }, [troubleshootingMode, user?.id]);

  // If the theme bundles styles/layouts, auto-apply them unless the user explicitly overrode them.
  // Re-evaluated on the same changes that re-apply the theme below; null until the first render.
  const [prevThemeInputs, setPrevThemeInputs] = useState<{
    activeThemeId: string | null;
    customThemeColors: Record<string, string>;
    hasUserComponentStyleOverride: boolean;
    hasUserLayoutStyleOverride: boolean;
    manifest: ExtensionManifest | null;
    selectedTheme: typeof selectedTheme;
    troubleshootingMode: boolean;
  } | null>(null);
  if (
    prevThemeInputs === null ||
    prevThemeInputs.activeThemeId !== activeThemeId ||
    prevThemeInputs.customThemeColors !== customThemeColors ||
    prevThemeInputs.hasUserComponentStyleOverride !== hasUserComponentStyleOverride ||
    prevThemeInputs.hasUserLayoutStyleOverride !== hasUserLayoutStyleOverride ||
    prevThemeInputs.manifest !== manifest ||
    prevThemeInputs.selectedTheme !== selectedTheme ||
    prevThemeInputs.troubleshootingMode !== troubleshootingMode
  ) {
    setPrevThemeInputs({
      activeThemeId,
      customThemeColors,
      hasUserComponentStyleOverride,
      hasUserLayoutStyleOverride,
      manifest,
      selectedTheme,
      troubleshootingMode,
    });
    if (manifest && !troubleshootingMode && activeThemeId && activeThemeId !== "custom" && selectedTheme) {
      if (!hasUserComponentStyleOverride) {
        setActiveComponentStylesState(parseStyleSet(selectedTheme.componentStyle ?? "default"));
      }
      if (!hasUserLayoutStyleOverride) {
        setActiveLayoutStylesState(parseLayoutSet(selectedTheme.layoutStyle ?? "default"));
      }
    }
  }

  // Apply active theme CSS variables
  useEffect(() => {
    if (!manifest || troubleshootingMode) return;

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
    const theme = selectedTheme;
    if (!theme) {
      document.documentElement.removeAttribute("data-theme");
      document.documentElement.removeAttribute("data-theme-bg-animation");
      document.documentElement.removeAttribute("data-color-scheme");
      return;
    }

    document.documentElement.setAttribute("data-theme", theme.id);

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
  }, [
    activeThemeId,
    customThemeColors,
    hasUserComponentStyleOverride,
    hasUserLayoutStyleOverride,
    manifest,
    selectedTheme,
    troubleshootingMode,
  ]);

  // Cache what was just painted, for the boot script to replay on the next load. Built from state,
  // not read back off the DOM, so it does not depend on effect ordering. Nothing is cached before
  // the manifest resolves, or the next load would pre-paint the default theme we are avoiding.
  useEffect(() => {
    if (!manifest || troubleshootingMode) return;

    if (!activeThemeId) {
      clearThemeBootSnapshot();
      return;
    }

    const theme = activeThemeId === "custom" ? null : selectedTheme;
    if (activeThemeId !== "custom" && !theme) {
      // The selection stays: the theme may just be initialising, upgrading or disabled. Only the
      // cached appearance goes, so the next load starts from the default, as this one does.
      clearThemeBootSnapshot();
      return;
    }

    const snapshot: ThemeBootSnapshot = {
      v: THEME_BOOT_VERSION,
      u: user?.id ?? null,
      themeId: activeThemeId,
      // The custom palette carries no colour scheme.
      colorScheme: theme?.colorScheme ?? null,
      bgAnimation: theme?.backgroundAnimation ?? null,
      vars: (activeThemeId === "custom" ? customThemeColors : theme?.cssVariables) ?? {},
      cssUrl: theme?.cssUrl ?? null,
      componentStyle: [...activeComponentStyles].join(" "),
      layoutStyle: activeLayoutStyle,
      styleOptions: userThemePreferences?.styleOptions ?? readStoredStyleOptions(),
    };
    writeThemeBootSnapshot(snapshot);
  }, [
    activeComponentStyles,
    activeLayoutStyle,
    activeThemeId,
    customThemeColors,
    manifest,
    selectedTheme,
    troubleshootingMode,
    user?.id,
    userThemePreferences,
  ]);

  // Apply component style data attribute (space-separated for composability)
  useEffect(() => {
    if (troubleshootingMode) {
      document.documentElement.removeAttribute("data-component-style");
      return;
    }
    const styleStr = [...activeComponentStyles].join(" ");
    document.documentElement.setAttribute("data-component-style", styleStr);
    return () => {
      document.documentElement.removeAttribute("data-component-style");
    };
  }, [activeComponentStyles, troubleshootingMode]);

  // Apply style options (data attributes + CSS custom properties) for the current session
  useEffect(() => {
    if (troubleshootingMode) {
      for (const key of Object.keys(document.documentElement.dataset)) {
        if (key.startsWith("style")) delete document.documentElement.dataset[key];
      }
      return;
    }
    try {
      const raw = userThemePreferences?.styleOptions ?? readStoredStyleOptions();
      delete document.documentElement.dataset.styleGradientSpeed;
      delete document.documentElement.dataset.styleGradientCardstrength;
      delete document.documentElement.dataset.styleGradientBgstrength;
      for (const key of Object.keys(document.documentElement.dataset)) {
        if (key.startsWith("style")) {
          delete document.documentElement.dataset[key];
        }
      }
      for (const [styleId, opts] of Object.entries(raw)) {
        for (const [key, val] of Object.entries(opts as Record<string, string>)) {
          document.documentElement.dataset[styleOptionDatasetKey(styleId, key)] = val;
          const cssVar = STYLE_OPTION_CSS_VARS[styleId]?.[key];
          if (cssVar) {
            document.documentElement.style.setProperty(cssVar, val);
          }
        }
      }
    } catch {
      /* ignore parse errors */
    }
  }, [userThemePreferences, troubleshootingMode]);

  // Apply layout style data attribute
  useEffect(() => {
    if (troubleshootingMode) {
      document.documentElement.removeAttribute("data-layout");
      return;
    }
    document.documentElement.setAttribute("data-layout", activeLayoutStyle);
    return () => {
      document.documentElement.removeAttribute("data-layout");
    };
  }, [activeLayoutStyle, troubleshootingMode]);

  // Derived lookups
  const getTabsForPage = useCallback(
    (pageType: string) =>
      manifest?.tabs.filter((t) => t.pageType === pageType && canAccessExtensionContribution(t, hasPermission)) ?? [],
    [hasPermission, manifest],
  );

  const getPageOverride = useCallback(
    (targetPage: string) => {
      const overrides = manifest?.pageOverrides.filter((o) => o.targetPage === targetPage) ?? [];
      return overrides.sort((a, b) => b.priority - a.priority)[0];
    },
    [manifest],
  );

  const getComponentOverrides = useCallback(
    (targetComponent: string) =>
      [...(manifest?.componentOverrides ?? [])]
        .filter((componentOverride) => componentOverride.targetComponent === targetComponent)
        .sort(
          (a, b) =>
            b.priority - a.priority ||
            compareOrdinal(a.extensionId, b.extensionId) ||
            compareOrdinal(a.componentName, b.componentName),
        ),
    [manifest],
  );

  const availableComponentStyles = manifest?.componentStyles ?? [];
  const availableLayoutStyles = manifest?.layoutStyles ?? [];
  const features = useMemo(() => manifest?.features ?? [], [manifest]);
  const settingsTabs = [...(manifest?.settingsTabs ?? [])].sort((a, b) => a.order - b.order);
  const settingsPanels = useMemo(() => manifest?.settingsPanels ?? [], [manifest]);
  const actions = useMemo(() => manifest?.actions ?? [], [manifest]);
  const listFilters = useMemo(() => manifest?.listFilters ?? [], [manifest]);
  const listSorts = useMemo(() => manifest?.listSorts ?? [], [manifest]);

  const getFeature = useCallback(
    (key: string) => {
      const normalizedKey = key.toLowerCase();
      return features.find((feature) => feature.key.toLowerCase() === normalizedKey);
    },
    [features],
  );

  const getSettingsPanelsForTab = useCallback(
    (tab: string, section?: string) => {
      const normalizedTab = tab.toLowerCase();
      return settingsPanels
        .filter((p) => {
          const resolvedTargetTab = p.targetTab ?? "extensions";
          if (resolvedTargetTab.toLowerCase() !== normalizedTab) return false;
          if (section == null) return !p.targetSection;
          return p.targetSection === section;
        })
        .sort((a, b) => a.order - b.order);
    },
    [settingsPanels],
  );

  const getActionsForContext = useCallback(
    (entityType?: string, page?: string, actionType?: string) => {
      return actions
        .filter((a) => {
          if (actionType && a.actionType !== actionType) return false;
          if (entityType && a.entityTypes.length > 0 && !a.entityTypes.includes(entityType)) return false;
          if (page && a.pages && a.pages.length > 0 && !a.pages.includes(page)) return false;
          if (a.requiredPermission && !hasPermission(a.requiredPermission)) return false;
          return true;
        })
        .sort((a, b) => a.order - b.order);
    },
    [actions, hasPermission],
  );

  const getListFiltersForEntity = useCallback(
    (entityType: string) => {
      const normalized = normalizeListEntityType(entityType);
      return listFilters
        .filter((filter) => normalizeListEntityType(filter.entityType) === normalized)
        .sort((a, b) => a.order - b.order);
    },
    [listFilters],
  );

  const getListSortsForEntity = useCallback(
    (entityType: string) => {
      const normalized = normalizeListEntityType(entityType);
      return listSorts
        .filter((sort) => normalizeListEntityType(sort.entityType) === normalized)
        .sort((a, b) => a.order - b.order);
    },
    [listSorts],
  );

  const refreshManifest = useCallback(async () => {
    if (troubleshootingMode) return null;
    const requestGeneration = ++manifestRequestGeneration.current;
    try {
      const nextManifest = await extensions.getManifest();
      const appliedManifest = await applyManifest(nextManifest, requestGeneration);
      if (appliedManifest) setLoaded(true);
      return appliedManifest;
    } catch (err) {
      console.warn("[ExtensionLoader] Failed to refresh manifest:", err);
      setError(err instanceof Error ? err.message : "Failed to refresh extensions");
      return null;
    }
  }, [applyManifest, troubleshootingMode]);

  const retryFailedExtensions = useCallback(async () => {
    if (!sourceManifest.current || troubleshootingMode) return;
    const failedOwners = new Set(loadFailures.map((failure) => failure.extensionId));
    for (const descriptor of runtimeBundleDescriptors(sourceManifest.current)) {
      if (!failedOwners.has(descriptor.extensionId)) continue;
      // Browsers cache rejected module imports. A fresh URL makes Retry actually
      // fetch the repaired bundle while healthy modules keep their current identity.
      const url = new URL(descriptor.jsBundleUrl, window.location.href);
      url.searchParams.set("coveRetry", createRuntimeOwnerId());
      retryUrls.current.set(descriptor.jsBundleUrl, url.href);
    }
    if (!(await refreshManifest())) throw new Error("Couldn’t refresh extension status.");
  }, [loadFailures, refreshManifest, troubleshootingMode]);

  return (
    <ExtensionComponentRegistryContext.Provider value={componentRegistry}>
      <ExtensionContext.Provider
        value={{
          manifest,
          loaded,
          refreshManifest,
          error,
          loadFailures,
          retryFailedExtensions,
          activeThemeId,
          setActiveTheme,
          availableThemes,
          activeComponentStyles,
          toggleComponentStyle,
          availableComponentStyles,
          activeLayoutStyles,
          toggleLayoutStyle,
          activeLayoutStyle,
          setActiveLayoutStyle,
          availableLayoutStyles,
          customThemeColors,
          setCustomThemeColors,
          getTabsForPage,
          getPageOverride,
          getComponentOverrides,
          features,
          getFeature,
          settingsTabs,
          settingsPanels,
          getSettingsPanelsForTab,
          actions,
          getActionsForContext,
          getListFiltersForEntity,
          getListSortsForEntity,
          resolveComponent,
          resolveActionHandler,
          getExtensionRevision,
        }}
      >
        {children}
      </ExtensionContext.Provider>
    </ExtensionComponentRegistryContext.Provider>
  );
}
