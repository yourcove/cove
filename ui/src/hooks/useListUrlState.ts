import { useCallback, useEffect, useMemo, useState } from "react";
import type { FindFilter } from "../api/types";
import { LOCATION_CHANGE_EVENT, buildCurrentUrl, navigateToUrl } from "../router/location";

interface ListUrlState<TDisplayMode extends string> {
  filter: FindFilter;
  objectFilter: Record<string, unknown>;
  displayMode: TDisplayMode;
}

interface UseListUrlStateOptions<TDisplayMode extends string> {
  resetKey: string;
  defaultFilter: FindFilter;
  defaultObjectFilter?: Record<string, unknown>;
  defaultDisplayMode: TDisplayMode;
  allowedDisplayModes: readonly TDisplayMode[];
}

const MANAGED_KEYS = ["q", "page", "perPage", "sort", "direction", "view", "filters"];

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

function normalizeDirection(value: string | null, fallback?: "asc" | "desc"): "asc" | "desc" | undefined {
  if (value === "asc" || value === "desc") {
    return value;
  }

  return fallback;
}

function readObjectFilter(value: string | null, fallback: Record<string, unknown>): Record<string, unknown> {
  if (value == null) {
    // No "filters" param in URL at all → use defaults
    return cloneObjectFilter(fallback);
  }

  // Explicit empty string or "{}" means "user cleared all filters"
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
  const filter: FindFilter = {
    q: params.get("q") ?? options.defaultFilter.q,
    page: normalizeInteger(params.get("page"), options.defaultFilter.page),
    perPage: normalizeInteger(params.get("perPage"), options.defaultFilter.perPage),
    sort: params.get("sort") ?? options.defaultFilter.sort,
    direction: normalizeDirection(params.get("direction"), options.defaultFilter.direction),
  };

  const view = params.get("view");
  const displayMode = options.allowedDisplayModes.includes(view as TDisplayMode)
    ? (view as TDisplayMode)
    : options.defaultDisplayMode;

  return {
    filter,
    objectFilter: readObjectFilter(params.get("filters"), options.defaultObjectFilter ?? {}),
    displayMode,
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
  if (state.filter.perPage && state.filter.perPage !== options.defaultFilter.perPage) {
    params.set("perPage", String(state.filter.perPage));
  }
  if (state.filter.sort && state.filter.sort !== options.defaultFilter.sort) {
    params.set("sort", state.filter.sort);
  }
  if (state.filter.direction && state.filter.direction !== options.defaultFilter.direction) {
    params.set("direction", state.filter.direction);
  }
  if (state.displayMode !== options.defaultDisplayMode) {
    params.set("view", state.displayMode);
  }
  if (Object.keys(state.objectFilter).length > 0) {
    params.set("filters", JSON.stringify(state.objectFilter));
  } else if (options.defaultObjectFilter && Object.keys(options.defaultObjectFilter).length > 0) {
    // Explicitly write empty filters to distinguish "user cleared filters" from "use defaults"
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
    });
  }, [options.defaultDisplayMode, options.defaultFilter, options.defaultObjectFilter]);

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

  return {
    filter: state.filter,
    objectFilter: state.objectFilter,
    displayMode: state.displayMode,
    setFilter,
    setObjectFilter,
    setDisplayMode,
    reset,
  };
}