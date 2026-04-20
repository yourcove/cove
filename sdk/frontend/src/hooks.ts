import { useState, useCallback, useEffect, useMemo } from "react";
import { request, createExtensionStore } from "./api";
import type { FindFilter } from "./types";

/**
 * Hook for fetching data with loading and error states.
 * Lighter than react-query for simple extension use cases.
 */
export function useFetch<T>(url: string | null, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [isLoading, setIsLoading] = useState(!!url);
  const [error, setError] = useState<Error | null>(null);

  const refetch = useCallback(() => {
    if (!url) return;
    setIsLoading(true);
    setError(null);
    request<T>(url)
      .then(setData)
      .catch(setError)
      .finally(() => setIsLoading(false));
  }, [url, ...deps]);

  useEffect(() => {
    refetch();
  }, [refetch]);

  return { data, isLoading, error, refetch };
}

/**
 * Hook for extension key-value store operations.
 */
export function useExtensionStore(extensionId: string) {
  const store = useMemo(() => createExtensionStore(extensionId), [extensionId]);
  return store;
}

/**
 * Hook for managing paginated/filterable entity lists.
 * Provides filter state, page navigation, and data fetching.
 */
export function useEntityList<T>(
  basePath: string,
  defaultFilter: FindFilter = { page: 1, perPage: 40, sort: "name", direction: "asc" }
) {
  const [filter, setFilter] = useState<FindFilter>(defaultFilter);

  const queryParams = useMemo(() => {
    const params = new URLSearchParams();
    if (filter.page) params.set("page", String(filter.page));
    if (filter.perPage) params.set("perPage", String(filter.perPage));
    if (filter.sort) params.set("sort", filter.sort);
    if (filter.direction) params.set("direction", filter.direction);
    if (filter.query) params.set("q", filter.query);
    return params.toString();
  }, [filter]);

  const url = `${basePath}?${queryParams}`;
  const { data, isLoading, error, refetch } = useFetch<{ items: T[]; totalCount: number }>(url, [queryParams]);

  const setPage = useCallback((page: number) => setFilter(f => ({ ...f, page })), []);
  const setSort = useCallback((sort: string, direction?: "asc" | "desc") =>
    setFilter(f => ({ ...f, sort, direction: direction ?? f.direction, page: 1 })), []);
  const setQuery = useCallback((query: string) =>
    setFilter(f => ({ ...f, query, page: 1 })), []);

  return {
    items: data?.items ?? [],
    totalCount: data?.totalCount ?? 0,
    isLoading,
    error,
    filter,
    setFilter,
    setPage,
    setSort,
    setQuery,
    refetch,
  };
}
