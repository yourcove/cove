import type { QueryKey } from "@tanstack/react-query";
import type { FindFilter, PaginatedResponse } from "../api/types";
import { useInfiniteListData } from "./useInfiniteListData";

interface UseDetailListQueryOptions<TItem extends { id: string | number }> {
  queryKey: QueryKey;
  filter: FindFilter;
  queryFn: (filter: FindFilter) => Promise<PaginatedResponse<TItem>>;
  enabled?: boolean;
  chunkSize?: number;
}

export function useDetailListQuery<TItem extends { id: string | number }>({
  queryKey,
  filter,
  queryFn,
  enabled = true,
  chunkSize = 60,
}: UseDetailListQueryOptions<TItem>) {
  const baseQueryKey = Array.isArray(queryKey) ? queryKey : [queryKey];
  const listData = useInfiniteListData({
    queryKey: baseQueryKey,
    filter,
    queryPage: queryFn,
    enabled,
    chunkSize,
  });

  return {
    data: {
      items: listData.items,
      totalCount: listData.totalCount,
      page: filter.perPage === 0 ? 1 : (filter.page ?? 1),
      perPage: filter.perPage === 0 ? chunkSize : (filter.perPage ?? chunkSize),
    } satisfies PaginatedResponse<TItem>,
    isLoading: listData.isLoading,
    loadError: listData.loadError,
    retry: listData.refetch,
    infinitePageSize: listData.infinitePageSize,
    infiniteQuery: listData.infiniteQuery,
    infiniteFilterKey: listData.infiniteFilterKey,
    loadMore: listData.loadMore,
    infiniteScroll: listData.infiniteScroll,
    fetchAllIds: listData.fetchAllIds,
  };
}
