import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { FindFilter } from "../api/types";
import { getRelatedEntityDisplayModes, type RelatedEntityType } from "../components/relatedEntityDisplayModes";
import { LOCATION_CHANGE_EVENT, buildCurrentUrl, navigateToUrl } from "../router/location";
import { getDefaultFilter } from "../utils/defaultSavedFilter";
import { LIST_URL_MANAGED_KEYS, useListUrlState, type ListUrlState } from "./useListUrlState";

type CachedListState = ListUrlState<string>;

const DetailListStateCacheContext = createContext<Map<string, CachedListState> | null>(null);
// Each tab's own URL parameters (see useDetailTabUrlState), as a query string, while it is not shown.
const DetailTabUrlParamsCacheContext = createContext<Map<string, string> | null>(null);

export function DetailListStateCacheProvider({ children }: { children: ReactNode }) {
  const [cache] = useState(() => new Map<string, CachedListState>());
  const [tabUrlParams] = useState(() => new Map<string, string>());
  return (
    <DetailListStateCacheContext.Provider value={cache}>
      <DetailTabUrlParamsCacheContext.Provider value={tabUrlParams}>{children}</DetailTabUrlParamsCacheContext.Provider>
    </DetailListStateCacheContext.Provider>
  );
}

interface UseDetailListUrlStateOptions<TDisplayMode extends string> {
  stateKey: string;
  resetKey: string;
  builtInFilter: FindFilter;
  builtInObjectFilter?: Record<string, unknown>;
  defaultFilterKey?: string;
  defaultDisplayMode: TDisplayMode;
  allowedDisplayModes: readonly TDisplayMode[];
  defaultSearchMode?: string;
  allowedSearchModes?: readonly string[];
  allowInfinitePageSize?: boolean;
  enabled?: boolean;
}

function cloneObjectFilter(filter: Record<string, unknown> | undefined) {
  return filter && Object.keys(filter).length > 0
    ? (JSON.parse(JSON.stringify(filter)) as Record<string, unknown>)
    : {};
}

export function useDetailListUrlState<TDisplayMode extends string>(
  options: UseDetailListUrlStateOptions<TDisplayMode>,
) {
  const cache = useContext(DetailListStateCacheContext);
  // A cached value is the starting point for a remounted tab, not its serialization baseline.
  // Reading the mutable cache again after each update would make the current state look like the
  // default and cause explicit URL parameters (notably sort=random) to be removed on later renders.
  const [cached] = useState(() => cache?.get(options.stateKey));
  const saved = useMemo(
    () => (options.defaultFilterKey ? getDefaultFilter(options.defaultFilterKey) : null),
    [options.defaultFilterKey],
  );
  const savedDisplayMode =
    typeof saved?.uiOptions?.displayMode === "string" &&
    options.allowedDisplayModes.includes(saved.uiOptions.displayMode as TDisplayMode)
      ? (saved.uiOptions.displayMode as TDisplayMode)
      : undefined;

  // A saved default is a starting configuration, not a request to reopen the page where it was
  // captured. This also preserves the mount-time behavior used before URL-backed detail lists.
  const defaultFilter = saved?.findFilter ? { ...saved.findFilter, page: 1 } : options.builtInFilter;
  const defaultObjectFilter = saved?.objectFilter ?? options.builtInObjectFilter;
  const defaultDisplayMode = savedDisplayMode ?? options.defaultDisplayMode;
  const defaultSearchMode = options.defaultSearchMode;
  const initialState = cached
    ? {
        filter: cached.filter,
        objectFilter: cached.objectFilter,
        displayMode: cached.displayMode as TDisplayMode,
        searchMode: cached.searchMode,
      }
    : undefined;

  const state = useListUrlState({
    resetKey: options.resetKey,
    defaultFilter,
    defaultObjectFilter: cloneObjectFilter(defaultObjectFilter),
    defaultDisplayMode,
    allowedDisplayModes: options.allowedDisplayModes,
    defaultSearchMode,
    allowedSearchModes: options.allowedSearchModes,
    allowInfinitePageSize: options.allowInfinitePageSize,
    enabled: options.enabled,
    initialState,
  });

  useEffect(() => {
    cache?.set(options.stateKey, {
      filter: state.filter,
      objectFilter: state.objectFilter,
      displayMode: state.displayMode,
      searchMode: state.searchMode,
    });
  }, [cache, options.stateKey, state.displayMode, state.filter, state.objectFilter, state.searchMode]);

  return state;
}

interface UseRelatedDetailListUrlStateOptions {
  stateKey: string;
  resetKey: string;
  entityType: RelatedEntityType;
  builtInFilter: FindFilter;
  builtInObjectFilter?: Record<string, unknown>;
  defaultFilterKey?: string;
  enabled?: boolean;
}

export function useRelatedDetailListUrlState(options: UseRelatedDetailListUrlStateOptions) {
  const availableDisplayModes = getRelatedEntityDisplayModes(options.entityType);
  const state = useDetailListUrlState({
    stateKey: options.stateKey,
    resetKey: options.resetKey,
    builtInFilter: options.builtInFilter,
    builtInObjectFilter: options.builtInObjectFilter,
    defaultFilterKey: options.defaultFilterKey,
    defaultDisplayMode: availableDisplayModes[0],
    allowedDisplayModes: availableDisplayModes,
    allowInfinitePageSize: true,
    enabled: options.enabled,
  });
  return { ...state, availableDisplayModes };
}

function readTab(defaultTab: string) {
  return new URLSearchParams(window.location.search).get("tab") || defaultTab;
}

const NO_TAB_URL_KEYS: readonly string[] = [];

/**
 * The active detail tab, kept in the "tab" URL parameter. `tabUrlKeys` names parameters that tabs keep
 * outside the list state. Switching tabs takes them out of the URL, so no tab carries another's choices,
 * and puts back the ones the chosen tab had when it was last left, as the list state cache does for
 * list filters. Pass a stable (module-level) array.
 */
export function useDetailTabUrlState<TTab extends string>(
  defaultTab: TTab,
  tabUrlKeys: readonly string[] = NO_TAB_URL_KEYS,
) {
  const [activeTab, setActiveTabState] = useState<TTab>(() => readTab(defaultTab) as TTab);
  const tabUrlParams = useContext(DetailTabUrlParamsCacheContext);
  // Selecting the tab that happens to be the default leaves no "tab" parameter behind, so the URL
  // alone cannot tell a deliberate choice from an untouched page. Remember the choice here instead:
  // the app config resolves after mount and can move which tab ranks first, and a default arriving
  // that late must not overrule the viewer.
  const tabChosen = useRef(false);

  // Adopt the default whenever it resolves or changes, but only while the viewer has not chosen.
  useEffect(() => {
    if (tabChosen.current) return;
    setActiveTabState(readTab(defaultTab) as TTab);
  }, [defaultTab]);

  // The URL stays authoritative for real navigation, including back and forward.
  useEffect(() => {
    const applyUrlTab = () => setActiveTabState(readTab(defaultTab) as TTab);
    window.addEventListener("popstate", applyUrlTab);
    window.addEventListener(LOCATION_CHANGE_EVENT, applyUrlTab);
    return () => {
      window.removeEventListener("popstate", applyUrlTab);
      window.removeEventListener(LOCATION_CHANGE_EVENT, applyUrlTab);
    };
  }, [defaultTab]);

  const setActiveTab = useCallback(
    (nextTab: TTab) => {
      const params = new URLSearchParams(window.location.search);
      const leavingTab = readTab(defaultTab);
      // Picking the tab that is already open keeps its own parameters.
      const switching = nextTab !== leavingTab && tabUrlKeys.length > 0;
      const leaving = new URLSearchParams();
      for (const key of LIST_URL_MANAGED_KEYS) params.delete(key);
      if (switching) {
        for (const key of tabUrlKeys) {
          const value = params.get(key);
          if (value != null) leaving.set(key, value);
          params.delete(key);
        }
        for (const [key, value] of new URLSearchParams(tabUrlParams?.get(nextTab) ?? "")) params.set(key, value);
      }
      if (nextTab === defaultTab) params.delete("tab");
      else params.set("tab", nextTab);
      // A navigation blocker can keep the page as it is; then the tab stays too.
      if (!navigateToUrl(buildCurrentUrl(window.location.pathname, params), { replace: true })) return;
      if (switching) tabUrlParams?.set(leavingTab, leaving.toString());
      tabChosen.current = true;
      setActiveTabState(nextTab);
    },
    [defaultTab, tabUrlKeys, tabUrlParams],
  );

  return { activeTab, setActiveTab };
}

/**
 * Keeps a tab's own URL parameters for when it is picked again, however it is left: another tab, a
 * link or Back. `params` is the query string the tab would restore; the last one rendered is kept.
 */
export function useRememberDetailTabUrlParams(tabKey: string, params: string) {
  const tabUrlParams = useContext(DetailTabUrlParamsCacheContext);
  const latest = useRef(params);
  useEffect(() => {
    latest.current = params;
  }, [params]);
  useEffect(() => {
    if (!tabUrlParams) return;
    return () => {
      tabUrlParams.set(tabKey, latest.current);
    };
  }, [tabKey, tabUrlParams]);
}

function readUrlParam(paramKey: string) {
  return new URLSearchParams(window.location.search).get(paramKey);
}

// A URL parameter outside the list keys that useListUrlState owns. `parse` and `serialize` must be
// module-level functions so the listeners stay attached; a serialized null or empty string removes
// the parameter.
function useDetailUrlParam<T>(
  paramKey: string,
  parse: (raw: string | null) => T,
  serialize: (value: T) => string | null,
) {
  const [value, setValueState] = useState(() => parse(readUrlParam(paramKey)));

  useEffect(() => {
    const applyUrlValue = () => setValueState(parse(readUrlParam(paramKey)));
    window.addEventListener("popstate", applyUrlValue);
    window.addEventListener(LOCATION_CHANGE_EVENT, applyUrlValue);
    return () => {
      window.removeEventListener("popstate", applyUrlValue);
      window.removeEventListener(LOCATION_CHANGE_EVENT, applyUrlValue);
    };
  }, [paramKey, parse]);

  const setValue = useCallback(
    (nextValue: T) => {
      const params = new URLSearchParams(window.location.search);
      const raw = serialize(nextValue);
      if (raw) params.set(paramKey, raw);
      else params.delete(paramKey);
      navigateToUrl(buildCurrentUrl(window.location.pathname, params), { replace: true });
      // Follow what the URL now holds, so a navigation a blocker refused leaves the value as it was.
      setValueState(parse(readUrlParam(paramKey)));
    },
    [paramKey, parse, serialize],
  );

  return [value, setValue] as const;
}

const parseBooleanParam = (raw: string | null) => raw === "true";
const serializeBooleanParam = (value: boolean) => (value ? "true" : null);
const parseStringParam = (raw: string | null) => raw || null;
const serializeStringParam = (value: string | null) => value || null;

export function useDetailBooleanUrlState(paramKey: string) {
  return useDetailUrlParam(paramKey, parseBooleanParam, serializeBooleanParam);
}

/** A string choice such as a sort or filter; null or an empty string removes it from the URL. */
export function useDetailStringUrlState(paramKey: string) {
  return useDetailUrlParam(paramKey, parseStringParam, serializeStringParam);
}
