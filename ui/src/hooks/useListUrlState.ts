import { useCallback, useEffect, useMemo, useState } from "react";
import type { FindFilter } from "../api/types";
import { LOCATION_CHANGE_EVENT, buildCurrentUrl, navigateToUrl } from "../router/location";
import { getSortClauses, parseSortClauses, serializeSortClauses } from "../utils/sortClauses";

export interface ListUrlState<TDisplayMode extends string> {
  filter: FindFilter;
  objectFilter: Record<string, unknown>;
  displayMode: TDisplayMode;
  searchMode: string;
}

export interface UseListUrlStateOptions<TDisplayMode extends string> {
  resetKey: string;
  defaultFilter: FindFilter;
  defaultObjectFilter?: Record<string, unknown>;
  defaultDisplayMode: TDisplayMode;
  allowedDisplayModes: readonly TDisplayMode[];
  defaultSearchMode?: string;
  allowedSearchModes?: readonly string[];
  allowInfinitePageSize?: boolean;
  enabled?: boolean;
  initialState?: ListUrlState<TDisplayMode>;
}

export const LIST_URL_MANAGED_KEYS = ["q", "page", "perPage", "sort", "direction", "sorts", "view", "viewMode", "filters", "seed", "searchMode"] as const;
const DEFAULT_SEARCH_MODE = "text";
const MAX_RANDOM_SORT_SEED = 2147483647;

function generateRandomSortSeed(): number {
  return Math.floor(Math.random() * MAX_RANDOM_SORT_SEED) || 1;
}

function cloneFilter(filter: FindFilter): FindFilter {
  return { ...filter, sorts: filter.sorts?.map((clause) => ({ ...clause })) };
}

function cloneObjectFilter(filter: Record<string, unknown> | undefined): Record<string, unknown> {
  if (!filter || Object.keys(filter).length === 0) {
    return {};
  }

  return JSON.parse(JSON.stringify(filter)) as Record<string, unknown>;
}

function normalizeInteger(value: string | null, fallback?: number): number | undefined {
  if (value == null || value.trim() === "") {
    return fallback;
  }

  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    return fallback;
  }

  return parsed;
}

function normalizePerPage(value: string | null, fallback: number | undefined, allowInfinite: boolean): number | undefined {
  if (allowInfinite && (value === "infinite" || value === "0")) {
    return 0;
  }

  return normalizeInteger(value, fallback);
}

function normalizeDirection(value: string | null, fallback?: "asc" | "desc"): "asc" | "desc" | undefined {
  if (value === "asc" || value === "desc") {
    return value;
  }

  return fallback;
}

function readObjectFilter(value: string | null, fallback: Record<string, unknown>): Record<string, unknown> {
  if (value == null) {
    // No "filters" param in URL at all means use defaults.
    return cloneObjectFilter(fallback);
  }

  // Explicit empty string or "{}" means the user cleared all filters.
  if (value === "" || value === "{}") {
    return {};
  }

  try {
    const parsed = JSON.parse(value);
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
  } catch {
    // Ignore invalid URL state and fall back to defaults.
  }

  return cloneObjectFilter(fallback);
}

function readStateFromUrl<TDisplayMode extends string>(options: UseListUrlStateOptions<TDisplayMode>): ListUrlState<TDisplayMode> {
  const params = new URLSearchParams(window.location.search);
  const defaultSearchMode = options.defaultSearchMode ?? DEFAULT_SEARCH_MODE;
  const allowedSearchModes = options.allowedSearchModes ?? [defaultSearchMode];
  const searchModeParam = params.get("searchMode");
  const urlSorts = parseSortClauses(params.get("sorts"));
  const defaultSorts = getSortClauses(options.defaultFilter);
  const legacySort = params.get("sort");
  const legacyDirection = normalizeDirection(params.get("direction"), options.defaultFilter.direction);
  const activeSorts = urlSorts.length > 0
    ? urlSorts
    : legacySort
      ? [{ key: legacySort, direction: legacyDirection ?? "asc" }]
      : defaultSorts;
  const sort = activeSorts[0]?.key ?? options.defaultFilter.sort;
  const direction = activeSorts[0]?.direction
    ?? legacyDirection;
  let seed = normalizeInteger(params.get("seed"), options.defaultFilter.seed);
  // Random sort with no seed (e.g. a saved/default filter that intentionally omits one) would
  // otherwise fall back to the backend's fixed default seed and produce the *same* "random" order
  // on every load. Mint a fresh seed per mount so results actually re-shuffle; it's written back
  // to the URL so pagination stays consistent within this view.
  if (sort === "random" && seed == null) {
    seed = generateRandomSortSeed();
  }
  const filter: FindFilter = {
    q: params.get("q") ?? options.defaultFilter.q,
    page: normalizeInteger(params.get("page"), options.defaultFilter.page),
    perPage: normalizePerPage(params.get("perPage"), options.defaultFilter.perPage, options.allowInfinitePageSize === true),
    sort,
    direction,
    sorts: activeSorts.length > 1 ? activeSorts : undefined,
    seed,
  };

  const rawView = params.get("view");
  const view = rawView && options.allowedDisplayModes.includes(rawView as TDisplayMode)
    ? rawView
    : null;
  const displayMode = view
    ? (view as TDisplayMode)
    : options.defaultDisplayMode;

  return {
    filter,
    objectFilter: readObjectFilter(params.get("filters"), options.defaultObjectFilter ?? {}),
    displayMode,
    searchMode: searchModeParam && allowedSearchModes.includes(searchModeParam) ? searchModeParam : defaultSearchMode,
  };
}

function readDefaultState<TDisplayMode extends string>(options: UseListUrlStateOptions<TDisplayMode>): ListUrlState<TDisplayMode> {
  const filter = cloneFilter(options.defaultFilter);
  if (filter.sort === "random" && filter.seed == null) filter.seed = generateRandomSortSeed();
  return {
    filter,
    objectFilter: cloneObjectFilter(options.defaultObjectFilter),
    displayMode: options.defaultDisplayMode,
    searchMode: options.defaultSearchMode ?? DEFAULT_SEARCH_MODE,
  };
}

function writeStateToParams<TDisplayMode extends string>(
  params: URLSearchParams,
  state: ListUrlState<TDisplayMode>,
  options: UseListUrlStateOptions<TDisplayMode>,
) {
  for (const key of LIST_URL_MANAGED_KEYS) {
    params.delete(key);
  }

  if (state.filter.q) {
    params.set("q", state.filter.q);
  } else if (options.defaultFilter.q) {
    params.set("q", "");
  }
  if (state.filter.page && state.filter.page !== options.defaultFilter.page) {
    params.set("page", String(state.filter.page));
  }
  if (state.filter.perPage != null && state.filter.perPage !== options.defaultFilter.perPage) {
    params.set("perPage", state.filter.perPage === 0 ? "infinite" : String(state.filter.perPage));
  }
  const sortClauses = getSortClauses(state.filter);
  const defaultSortClauses = getSortClauses(options.defaultFilter);
  if (sortClauses.length > 1) {
    const serializedSorts = serializeSortClauses(sortClauses);
    if (serializedSorts !== serializeSortClauses(defaultSortClauses)) {
      params.set("sorts", serializedSorts);
    }
  } else {
    if (state.filter.sort && state.filter.sort !== options.defaultFilter.sort) {
      params.set("sort", state.filter.sort);
    }
    if (state.filter.direction && state.filter.direction !== options.defaultFilter.direction) {
      params.set("direction", state.filter.direction);
    }
  }
  if (state.filter.seed != null) {
    params.set("seed", String(state.filter.seed));
  }
  if (state.displayMode !== options.defaultDisplayMode) {
    params.set("view", state.displayMode);
  }
  if (state.searchMode !== (options.defaultSearchMode ?? DEFAULT_SEARCH_MODE)) {
    params.set("searchMode", state.searchMode);
  }
  if (Object.keys(state.objectFilter).length > 0) {
    params.set("filters", JSON.stringify(state.objectFilter));
  } else if (options.defaultObjectFilter && Object.keys(options.defaultObjectFilter).length > 0) {
    params.set("filters", "{}");
  }
}

export function useListUrlState<TDisplayMode extends string>(options: UseListUrlStateOptions<TDisplayMode>) {
  const readState = useCallback(() => readStateFromUrl(options), [options]);
  const [state, setState] = useState<ListUrlState<TDisplayMode>>(() => {
    if (!options.initialState) {
      return options.enabled === false ? readDefaultState(options) : readState();
    }

    const initialOptions = {
      ...options,
      defaultFilter: options.initialState.filter,
      defaultObjectFilter: options.initialState.objectFilter,
      defaultDisplayMode: options.initialState.displayMode,
      defaultSearchMode: options.initialState.searchMode,
    };
    return options.enabled === false ? readDefaultState(initialOptions) : readStateFromUrl(initialOptions);
  });

  const serializedState = useMemo(() => JSON.stringify(state), [state]);

  const reset = useCallback(() => {
    setState({
      filter: cloneFilter(options.defaultFilter),
      objectFilter: cloneObjectFilter(options.defaultObjectFilter),
      displayMode: options.defaultDisplayMode,
      searchMode: options.defaultSearchMode ?? DEFAULT_SEARCH_MODE,
    });
  }, [options.defaultDisplayMode, options.defaultFilter, options.defaultObjectFilter, options.defaultSearchMode]);

  useEffect(() => {
    if (options.enabled === false) return;
    const applyUrlState = () => {
      const nextState = readState();
      setState((current) => {
        const currentSerialized = JSON.stringify(current);
        const nextSerialized = JSON.stringify(nextState);
        return currentSerialized === nextSerialized ? current : nextState;
      });
    };

    window.addEventListener("popstate", applyUrlState);
    window.addEventListener(LOCATION_CHANGE_EVENT, applyUrlState);

    return () => {
      window.removeEventListener("popstate", applyUrlState);
      window.removeEventListener(LOCATION_CHANGE_EVENT, applyUrlState);
    };
  }, [readState]);

  useEffect(() => {
    const handleReset = (event: Event) => {
      if ((event as CustomEvent).detail === options.resetKey) {
        reset();
      }
    };

    window.addEventListener("cove-page-reset", handleReset);
    return () => window.removeEventListener("cove-page-reset", handleReset);
  }, [options.resetKey, reset]);

  useEffect(() => {
    if (options.enabled === false) return;
    const params = new URLSearchParams(window.location.search);
    writeStateToParams(params, state, options);

    const nextUrl = buildCurrentUrl(window.location.pathname, params);
    const currentUrl = `${window.location.pathname}${window.location.search}`;
    if (nextUrl !== currentUrl) {
      // This hook already owns the state represented by these query parameters. Emitting a
      // global location-change event here can make another listener re-read the previous render
      // and overwrite a just-applied filter change. Native replaceState is intentionally silent;
      // popstate and explicit app navigation continue to flow through the listeners above.
      window.history.replaceState(window.history.state, "", nextUrl);
    }
  }, [options, serializedState, state]);

  const setFilter = useCallback((filter: FindFilter) => {
    setState((current) => ({ ...current, filter }));
  }, []);

  const setObjectFilter = useCallback((objectFilter: Record<string, unknown>) => {
    setState((current) => ({ ...current, objectFilter }));
  }, []);

  const setDisplayMode = useCallback((displayMode: TDisplayMode) => {
    setState((current) => ({ ...current, displayMode }));
  }, []);

  const setSearchMode = useCallback((searchMode: string) => {
    setState((current) => ({ ...current, searchMode }));
  }, []);

  const replaceState = useCallback((nextState: Omit<ListUrlState<TDisplayMode>, "searchMode"> & { searchMode?: string }) => {
    const normalizedState: ListUrlState<TDisplayMode> = {
      ...nextState,
      searchMode: nextState.searchMode ?? options.defaultSearchMode ?? DEFAULT_SEARCH_MODE,
    };
    setState(normalizedState);

    // Write the complete state before emitting a location change. Consumers that also update
    // page-specific URL parameters can then navigate without the URL listener restoring stale
    // list state from the previous mode.
    const params = new URLSearchParams(window.location.search);
    writeStateToParams(params, normalizedState, options);
    navigateToUrl(buildCurrentUrl(window.location.pathname, params), { replace: true });
  }, [options]);

  return {
    filter: state.filter,
    objectFilter: state.objectFilter,
    displayMode: state.displayMode,
    searchMode: state.searchMode,
    setFilter,
    setObjectFilter,
    setDisplayMode,
    setSearchMode,
    replaceState,
    reset,
  };
}
