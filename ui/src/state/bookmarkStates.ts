import { useEffect } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import { bookmarks } from "../api/client";
import type { AffinityHostType, BookmarkState } from "../api/types";
import { useOptionalAuth } from "../auth/optionalAuth";
import { createBatchedLoader } from "../utils/batchRequests";

/**
 * Save for Later state is cached per item under `["bookmark-state", hostType, hostId]`.
 * Lookups made in the same tick are combined into one `POST /me/bookmarks/batch` per host
 * type, so a page of cards costs one request per host type instead of one per card.
 */
export function bookmarkStateQueryKey(hostType: AffinityHostType, hostId: number) {
  return ["bookmark-state", hostType, hostId] as const;
}

/** Prefix that matches every cached state, or every state of one host type. */
export function bookmarkStatesQueryKey(hostType?: AffinityHostType) {
  return hostType === undefined ? (["bookmark-state"] as const) : (["bookmark-state", hostType] as const);
}

/** Refetches cached states; the refetches are batched like any other lookup. */
export function invalidateBookmarkStates(queryClient: QueryClient, hostType?: AffinityHostType) {
  return queryClient.invalidateQueries({ queryKey: bookmarkStatesQueryKey(hostType) });
}

// Large infinite pages are split so one request body stays a reasonable size.
const MAX_IDS_PER_REQUEST = 500;

const loaders = new Map<AffinityHostType, (ids: number[]) => Promise<BookmarkState[]>>();

export async function fetchBookmarkState(hostType: AffinityHostType, hostId: number): Promise<BookmarkState> {
  let loader = loaders.get(hostType);
  if (!loader) {
    loader = createBatchedLoader(
      (hostIds) => bookmarks.batch({ hostType, hostIds }),
      (state) => state.hostId,
      MAX_IDS_PER_REQUEST,
    );
    loaders.set(hostType, loader);
  }
  const [state] = await loader([hostId]);
  return state ?? { hostType, hostId, saved: false };
}

/** Options for the per-item query, shared by the button and the page-level prefetch. */
export function bookmarkStateQueryOptions(hostType: AffinityHostType, hostId: number) {
  return {
    queryKey: bookmarkStateQueryKey(hostType, hostId),
    queryFn: () => fetchBookmarkState(hostType, hostId),
  };
}

/**
 * Only signed-in users have bookmarks; share-link guests and sessions without an owner are
 * refused by the server, so they do not ask.
 */
export function useCanBookmark() {
  const auth = useOptionalAuth();
  if (!auth) return true;
  return auth.user != null && (auth.user.kind ?? "user") === "user";
}

// Host types whose cards carry a Save for Later button; prefetching any other type would be wasted.
const CARD_BOOKMARK_HOST_TYPES: ReadonlySet<AffinityHostType> = new Set([
  "video",
  "image",
  "gallery",
  "group",
  "audio",
  "text",
]);

/**
 * Loads the states of a page of results in one request per host type. Virtualized lists mount
 * their cards a few at a time; with the page prefetched, those cards read the cache instead of
 * asking for their own state.
 */
export function usePrefetchBookmarkStates(hostType: AffinityHostType | undefined, hostIds: readonly number[]) {
  const queryClient = useQueryClient();
  const canBookmark = useCanBookmark();
  const idsKey = hostIds.join(",");
  useEffect(() => {
    if (!canBookmark || !hostType || !CARD_BOOKMARK_HOST_TYPES.has(hostType) || !idsKey) return;
    for (const hostId of idsKey.split(",").map(Number)) {
      if (hostId > 0) void queryClient.prefetchQuery(bookmarkStateQueryOptions(hostType, hostId));
    }
  }, [canBookmark, hostType, idsKey, queryClient]);
}
