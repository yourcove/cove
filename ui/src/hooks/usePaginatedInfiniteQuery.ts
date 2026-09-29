import { useCallback } from "react";
import { keepPreviousData, useInfiniteQuery, type QueryKey } from "@tanstack/react-query";
import { UNCOUNTED_TOTAL, type PaginatedResponse } from "../api/types";

interface UsePaginatedInfiniteQueryOptions<TItem extends { id: string | number }> {
  queryKey: QueryKey;
  queryFn: (page: number, perPage: number) => Promise<PaginatedResponse<TItem>>;
  enabled?: boolean;
  chunkSize?: number;
  /**
   * The total when pages are fetched with `skipCount` and report `UNCOUNTED_TOTAL`. Until it is known,
   * another page is loaded whenever the last one came back full.
   */
  knownTotalCount?: number;
}

function resolveTotalCount(pageTotal: number, knownTotalCount: number | undefined) {
  return pageTotal === UNCOUNTED_TOTAL ? knownTotalCount : pageTotal;
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
  knownTotalCount,
}: UsePaginatedInfiniteQueryOptions<TItem>) {
  const query = useInfiniteQuery({
    queryKey,
    enabled,
    initialPageParam: 1,
    queryFn: ({ pageParam }) => queryFn(pageParam, chunkSize),
    placeholderData: keepPreviousData,
    getNextPageParam: (lastPage) => {
      const loadedThrough = lastPage.page * lastPage.perPage;
      const total = resolveTotalCount(lastPage.totalCount, knownTotalCount);
      const exhausted = total === undefined ? lastPage.items.length < lastPage.perPage : loadedThrough >= total;
      if (exhausted || lastPage.items.length === 0) {
        return undefined;
      }

      return lastPage.page + 1;
    },
    getPreviousPageParam: (firstPage) => (firstPage.page > 1 ? firstPage.page - 1 : undefined),
  });

  // Loads one more page and resolves with every item the query then knows about, newest page last.
  // Callers that keep their own copy of the queue (the lightbox) diff against what they already hold,
  // so they pick up the new page even if the list advanced independently while they were detached.
  const { fetchNextPage, isPlaceholderData } = query;
  const fetchMoreItems = useCallback(async () => {
    // While placeholder data stands in for a key that has not loaded, the pages on hand belong to the
    // previous filter and `fetchNextPage` would fetch page one of the new one. Report no results instead
    // of handing the caller items from a different query.
    if (isPlaceholderData) return [];
    // Do not cancel and restart a page the list is already fetching for itself.
    const result = await fetchNextPage({ cancelRefetch: false });
    return (result.data?.pages ?? []).flatMap((page) => page.items);
  }, [fetchNextPage, isPlaceholderData]);

  const pages = query.data?.pages ?? [];
  const lastPage = pages[pages.length - 1];
  const loadedThrough = lastPage ? (lastPage.page - 1) * lastPage.perPage + lastPage.items.length : 0;
  // An uncounted list whose total is not known yet reports what it has loaded so far.
  const totalCount = pages[0] ? (resolveTotalCount(pages[0].totalCount, knownTotalCount) ?? loadedThrough) : 0;
  const loadedThroughCount = Math.min(totalCount, loadedThrough);

  return {
    ...query,
    fetchMoreItems,
    items: uniqueItemsById(pages.flatMap((page) => page.items)),
    firstLoadedIndex: pages[0] ? (pages[0].page - 1) * pages[0].perPage : 0,
    loadedThroughCount,
    totalCount,
  };
}
