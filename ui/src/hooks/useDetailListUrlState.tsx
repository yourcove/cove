import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { FindFilter } from "../api/types";
import { getRelatedEntityDisplayModes, type RelatedEntityType } from "../components/relatedEntityDisplayModes";
import { LOCATION_CHANGE_EVENT, buildCurrentUrl, navigateToUrl } from "../router/location";
import { getDefaultFilter } from "../utils/defaultSavedFilter";
import {
  LIST_URL_MANAGED_KEYS,
  useListUrlState,
  type ListUrlState,
} from "./useListUrlState";

type CachedListState = ListUrlState<string>;

const DetailListStateCacheContext = createContext<Map<string, CachedListState> | null>(null);

export function DetailListStateCacheProvider({ children }: { children: ReactNode }) {
  const cacheRef = useRef(new Map<string, CachedListState>());
  return (
    <DetailListStateCacheContext.Provider value={cacheRef.current}>
      {children}
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
    ? JSON.parse(JSON.stringify(filter)) as Record<string, unknown>
    : {};
}

export function useDetailListUrlState<TDisplayMode extends string>(options: UseDetailListUrlStateOptions<TDisplayMode>) {
  const cache = useContext(DetailListStateCacheContext);
  // A cached value is the starting point for a remounted tab, not its serialization baseline.
  // Reading the mutable cache again after each update would make the current state look like the
  // default and cause explicit URL parameters (notably sort=random) to be removed on later renders.
  const initialCachedRef = useRef(cache?.get(options.stateKey));
  const cached = initialCachedRef.current;
  const saved = useMemo(
    () => options.defaultFilterKey ? getDefaultFilter(options.defaultFilterKey) : null,
    [options.defaultFilterKey],
  );
  const savedDisplayMode = typeof saved?.uiOptions?.displayMode === "string"
    && options.allowedDisplayModes.includes(saved.uiOptions.displayMode as TDisplayMode)
    ? saved.uiOptions.displayMode as TDisplayMode
    : undefined;

  const defaultFilter = saved?.findFilter ?? options.builtInFilter;
  const defaultObjectFilter = saved?.objectFilter ?? options.builtInObjectFilter;
  const defaultDisplayMode = savedDisplayMode
    ?? options.defaultDisplayMode;
  const defaultSearchMode = options.defaultSearchMode;
  const initialState = cached ? {
    filter: cached.filter,
    objectFilter: cached.objectFilter,
    displayMode: cached.displayMode as TDisplayMode,
    searchMode: cached.searchMode,
  } : undefined;

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

export function useDetailTabUrlState<TTab extends string>(defaultTab: TTab) {
  const [activeTab, setActiveTabState] = useState<TTab>(() => readTab(defaultTab) as TTab);

  useEffect(() => {
    const applyUrlTab = () => setActiveTabState(readTab(defaultTab) as TTab);
    window.addEventListener("popstate", applyUrlTab);
    window.addEventListener(LOCATION_CHANGE_EVENT, applyUrlTab);
    return () => {
      window.removeEventListener("popstate", applyUrlTab);
      window.removeEventListener(LOCATION_CHANGE_EVENT, applyUrlTab);
    };
  }, [defaultTab]);

  const setActiveTab = useCallback((nextTab: TTab) => {
    const params = new URLSearchParams(window.location.search);
    for (const key of LIST_URL_MANAGED_KEYS) params.delete(key);
    if (nextTab === defaultTab) params.delete("tab");
    else params.set("tab", nextTab);
    navigateToUrl(buildCurrentUrl(window.location.pathname, params), { replace: true });
    setActiveTabState(nextTab);
  }, [defaultTab]);

  return { activeTab, setActiveTab };
}
