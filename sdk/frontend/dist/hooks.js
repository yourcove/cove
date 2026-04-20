import { useState, useCallback, useEffect, useMemo } from "react";
import { request, createExtensionStore } from "./api";
/**
 * Hook for fetching data with loading and error states.
 * Lighter than react-query for simple extension use cases.
 */
export function useFetch(url, deps = []) {
    const [data, setData] = useState(null);
    const [isLoading, setIsLoading] = useState(!!url);
    const [error, setError] = useState(null);
    const refetch = useCallback(() => {
        if (!url)
            return;
        setIsLoading(true);
        setError(null);
        request(url)
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
export function useExtensionStore(extensionId) {
    const store = useMemo(() => createExtensionStore(extensionId), [extensionId]);
    return store;
}
/**
 * Hook for managing paginated/filterable entity lists.
 * Provides filter state, page navigation, and data fetching.
 */
export function useEntityList(basePath, defaultFilter = { page: 1, perPage: 40, sort: "name", direction: "asc" }) {
    const [filter, setFilter] = useState(defaultFilter);
    const queryParams = useMemo(() => {
        const params = new URLSearchParams();
        if (filter.page)
            params.set("page", String(filter.page));
        if (filter.perPage)
            params.set("perPage", String(filter.perPage));
        if (filter.sort)
            params.set("sort", filter.sort);
        if (filter.direction)
            params.set("direction", filter.direction);
        if (filter.query)
            params.set("q", filter.query);
        return params.toString();
    }, [filter]);
    const url = `${basePath}?${queryParams}`;
    const { data, isLoading, error, refetch } = useFetch(url, [queryParams]);
    const setPage = useCallback((page) => setFilter(f => ({ ...f, page })), []);
    const setSort = useCallback((sort, direction) => setFilter(f => ({ ...f, sort, direction: direction ?? f.direction, page: 1 })), []);
    const setQuery = useCallback((query) => setFilter(f => ({ ...f, query, page: 1 })), []);
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
