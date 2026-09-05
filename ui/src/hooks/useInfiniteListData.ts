import { useCallback, useMemo } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import type { FindFilter, PaginatedResponse } from "../api/types";
import { fetchAllMatchingIds } from "../utils/selectAllMatching";
import { getLoadError } from "../utils/queryLoadState";
import { usePaginatedInfiniteQuery } from "./usePaginatedInfiniteQuery";

interface UseInfiniteListDataOptions<TItem extends { id: string | number }> {
  queryKey: readonly unknown[];
  filter: FindFilter;
  queryPage: (filter: FindFilter) => Promise<PaginatedResponse<TItem>>;
  enabled?: boolean;
  chunkSize?: number;
}

export function useInfiniteListData<TItem extends { id: string | number }>({
  queryKey,
  filter,
  queryPage,
  enabled = true,
  chunkSize,
}: UseInfiniteListDataOptions<TItem>) {
  const infinitePageSize = filter.perPage === 0;
  const infiniteChunkSize = chunkSize && chunkSize > 0 ? chunkSize : 40;
  const infiniteFilterKey = useMemo(
    () => ({ ...filter, page: 1, perPage: infiniteChunkSize }),
    [filter, infiniteChunkSize],
  );

  const pageQuery = useQuery({
    queryKey: [...queryKey, "page", filter],
    queryFn: () => queryPage(filter),
    enabled: enabled && !infinitePageSize,
    placeholderData: keepPreviousData,
  });

  const infiniteQuery = usePaginatedInfiniteQuery<TItem>({
    queryKey: [...queryKey, "infinite", infiniteFilterKey],
    enabled: enabled && infinitePageSize,
    chunkSize: infiniteChunkSize,
    queryFn: (page, perPage) => queryPage({ ...filter, page, perPage }),
  });

  const loadMore = useCallback(() => {
    if (!infiniteQuery.isPlaceholderData && infiniteQuery.hasNextPage && !infiniteQuery.isFetchingNextPage) {
      void infiniteQuery.fetchNextPage();
    }
  }, [infiniteQuery.fetchNextPage, infiniteQuery.hasNextPage, infiniteQuery.isFetchingNextPage, infiniteQuery.isPlaceholderData]);

  const fetchAllIds = useCallback(
    () => fetchAllMatchingIds(filter, queryPage),
    [filter, queryPage],
  );

  const items = infinitePageSize ? infiniteQuery.items : (pageQuery.data?.items ?? []);
  const totalCount = infinitePageSize ? infiniteQuery.totalCount : (pageQuery.data?.totalCount ?? 0);
  const isLoading = infinitePageSize ? infiniteQuery.isPending : pageQuery.isLoading;
  const loadError = infinitePageSize
    ? getLoadError(infiniteQuery.data, infiniteQuery.error)
    : getLoadError(pageQuery.data, pageQuery.error);
  const refetch = infinitePageSize ? infiniteQuery.refetch : pageQuery.refetch;

  const infiniteScroll = infinitePageSize ? {
    hasNextPage: !infiniteQuery.isPlaceholderData && infiniteQuery.hasNextPage,
    isFetchingNextPage: infiniteQuery.isFetchingNextPage,
    onLoadMore: loadMore,
    loadedCount: infiniteQuery.loadedThroughCount,
    totalCount,
  } : undefined;

  return {
    infinitePageSize,
    items,
    totalCount,
    isLoading,
    loadError,
    refetch,
    infiniteQuery,
    isPlaceholderData: infinitePageSize && infiniteQuery.isPlaceholderData,
    infiniteFilterKey,
    loadMore,
    infiniteScroll,
    fetchAllIds,
  };
}
