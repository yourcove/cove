import { keepPreviousData, useInfiniteQuery, type QueryKey } from "@tanstack/react-query";
import type { PaginatedResponse } from "../api/types";

interface UsePaginatedInfiniteQueryOptions<TItem extends { id: string | number }> {
  queryKey: QueryKey;
  queryFn: (page: number, perPage: number) => Promise<PaginatedResponse<TItem>>;
  enabled?: boolean;
  chunkSize?: number;
}

function uniqueItemsById<TItem extends { id: string | number }>(items: TItem[]) {
  const seen = new Set<string>();
  const uniqueItems: TItem[] = [];
  for (const item of items) {
    const key = String(item.id);
    if (seen.has(key)) continue;
    seen.add(key);
    uniqueItems.push(item);
  }

  return uniqueItems;
}

export function usePaginatedInfiniteQuery<TItem extends { id: string | number }>({
  queryKey,
  queryFn,
  enabled = true,
  chunkSize = 24,
}: UsePaginatedInfiniteQueryOptions<TItem>) {
  const query = useInfiniteQuery({
    queryKey,
    enabled,
    initialPageParam: 1,
    queryFn: ({ pageParam }) => queryFn(pageParam, chunkSize),
    placeholderData: keepPreviousData,
    getNextPageParam: (lastPage) => {
      const loadedThrough = lastPage.page * lastPage.perPage;
      if (loadedThrough >= lastPage.totalCount || lastPage.items.length === 0) {
        return undefined;
      }

      return lastPage.page + 1;
    },
    getPreviousPageParam: (firstPage) => firstPage.page > 1 ? firstPage.page - 1 : undefined,
  });

  const pages = query.data?.pages ?? [];
  const totalCount = pages[0]?.totalCount ?? 0;
  const lastPage = pages[pages.length - 1];
  const loadedThroughCount = lastPage
    ? Math.min(totalCount, (lastPage.page - 1) * lastPage.perPage + lastPage.items.length)
    : 0;

  return {
    ...query,
    items: uniqueItemsById(pages.flatMap((page) => page.items)),
    firstLoadedIndex: pages[0] ? (pages[0].page - 1) * pages[0].perPage : 0,
    loadedThroughCount,
    totalCount,
  };
}
