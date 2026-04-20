import type { FindFilter } from "./types";
/**
 * Hook for fetching data with loading and error states.
 * Lighter than react-query for simple extension use cases.
 */
export declare function useFetch<T>(url: string | null, deps?: unknown[]): {
    data: T | null;
    isLoading: boolean;
    error: Error | null;
    refetch: () => void;
};
/**
 * Hook for extension key-value store operations.
 */
export declare function useExtensionStore(extensionId: string): {
    get: (key: string) => Promise<string | null>;
    set: (key: string, value: string) => Promise<void>;
    delete: (key: string) => Promise<void>;
    getAll: () => Promise<Record<string, string>>;
};
/**
 * Hook for managing paginated/filterable entity lists.
 * Provides filter state, page navigation, and data fetching.
 */
export declare function useEntityList<T>(basePath: string, defaultFilter?: FindFilter): {
    items: T[];
    totalCount: number;
    isLoading: boolean;
    error: Error | null;
    filter: FindFilter;
    setFilter: import("react").Dispatch<import("react").SetStateAction<FindFilter>>;
    setPage: (page: number) => void;
    setSort: (sort: string, direction?: "asc" | "desc") => void;
    setQuery: (query: string) => void;
    refetch: () => void;
};
//# sourceMappingURL=hooks.d.ts.map