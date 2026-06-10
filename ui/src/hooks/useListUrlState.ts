import { useCallback, useEffect, useMemo, useState } from "react";
import type { FindFilter } from "../api/types";
import { LOCATION_CHANGE_EVENT, buildCurrentUrl, navigateToUrl } from "../router/location";

interface ListUrlState<TDisplayMode extends string> {
  filter: FindFilter;
  objectFilter: Record<string, unknown>;
  displayMode: TDisplayMode;
  searchMode: string;
}

interface UseListUrlStateOptions<TDisplayMode extends string> {
  resetKey: string;
  defaultFilter: FindFilter;
  defaultObjectFilter?: Record<string, unknown>;
  defaultDisplayMode: TDisplayMode;
  allowedDisplayModes: readonly TDisplayMode[];
  defaultSearchMode?: string;
  allowedSearchModes?: readonly string[];
  allowInfinitePageSize?: boolean;
}

const MANAGED_KEYS = ["q", "page", "perPage", "sort", "direction", "view", "viewMode", "filters", "seed", "searchMode"];
const DEFAULT_SEARCH_MODE = "text";
const MAX_RANDOM_SORT_SEED = 2147483647;

function generateRandomSortSeed(): number {
  return Math.floor(Math.random() * MAX_RANDOM_SORT_SEED) || 1;
}

function cloneFilter(filter: FindFilter): FindFilter {
  return { ...filter };
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
  const sort = params.get("sort") ?? options.defaultFilter.sort;
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
    direction: normalizeDirection(params.get("direction"), options.defaultFilter.direction),
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

function writeStateToParams<TDisplayMode extends string>(
  params: URLSearchParams,
  state: ListUrlState<TDisplayMode>,
  options: UseListUrlStateOptions<TDisplayMode>,
) {
  for (const key of MANAGED_KEYS) {
    params.delete(key);
  }

  if (state.filter.q) {
    params.set("q", state.filter.q);
  }
  if (state.filter.page && state.filter.page !== options.defaultFilter.page) {
    params.set("page", String(state.filter.page));
  }
  if (state.filter.perPage != null && state.filter.perPage !== options.defaultFilter.perPage) {
    params.set("perPage", state.filter.perPage === 0 ? "infinite" : String(state.filter.perPage));
  }
  if (state.filter.sort && state.filter.sort !== options.defaultFilter.sort) {
    params.set("sort", state.filter.sort);
  }
  if (state.filter.direction && state.filter.direction !== options.defaultFilter.direction) {
    params.set("direction", state.filter.direction);
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
  const [state, setState] = useState<ListUrlState<TDisplayMode>>(() => readState());

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
    const params = new URLSearchParams(window.location.search);
    writeStateToParams(params, state, options);

    const nextUrl = buildCurrentUrl(window.location.pathname, params);
    navigateToUrl(nextUrl, { replace: true });
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

  return {
    filter: state.filter,
    objectFilter: state.objectFilter,
    displayMode: state.displayMode,
    searchMode: state.searchMode,
    setFilter,
    setObjectFilter,
    setDisplayMode,
    setSearchMode,
    reset,
  };
}
