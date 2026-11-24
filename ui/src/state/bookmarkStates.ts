import { useEffect, useRef } from "react";
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
 * Bookmarks belong to a user account. A signed-in user, the owner an auth-disabled install acts
 * as, and an API token's owner all report a numeric user id; share-link guests and an
 * auth-disabled install without an owner do not, and the server refuses them, so they do not ask.
 */
export function useCanBookmark() {
  const auth = useOptionalAuth();
  if (!auth) return true;
  const user = auth.user;
  return user != null && user.kind !== "shareLink" && /^\d+$/.test(user.id);
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

export interface BookmarkHostRef {
  hostType: AffinityHostType;
  hostId: number;
}

/**
 * Loads the states of a page of results in one request per host type. Virtualized lists mount
 * their cards a few at a time, and mixed group views mount each card when its item loads; with
 * the page prefetched, those cards read the cache instead of asking for their own state.
 */
export function usePrefetchBookmarkStates(hostType: AffinityHostType | undefined, hostIds: readonly number[]) {
  usePrefetchBookmarkStateRefs(hostType ? hostIds.map((hostId) => ({ hostType, hostId })) : []);
}

/** The mixed-type form of usePrefetchBookmarkStates, for views such as group items. */
export function usePrefetchBookmarkStateRefs(refs: readonly BookmarkHostRef[]) {
  const queryClient = useQueryClient();
  const canBookmark = useCanBookmark();
  const refsKey = refs
    .filter((ref) => ref.hostId > 0 && CARD_BOOKMARK_HOST_TYPES.has(ref.hostType))
    .map((ref) => `${ref.hostType}:${ref.hostId}`)
    .join(",");
  // Items already requested while this view is mounted. When an infinite list grows, only the new
  // page is asked for; earlier cards keep their cached state, which toggles and invalidation keep
  // current.
  const requested = useRef(new Set<string>());
  useEffect(() => {
    if (!canBookmark || !refsKey) return;
    for (const key of refsKey.split(",")) {
      if (requested.current.has(key)) continue;
      requested.current.add(key);
      const [hostType, hostId] = key.split(":");
      void queryClient.prefetchQuery(bookmarkStateQueryOptions(hostType as AffinityHostType, Number(hostId)));
    }
  }, [canBookmark, refsKey, queryClient]);
}

/**
 * Bookmarks belong to one account, but their cache key does not name it. Drops every cached state
 * when the signed-in user signs out or changes, so the next account never sees the previous one's.
 */
export function useResetBookmarkStatesOnUserChange(userId: string | null | undefined) {
  const queryClient = useQueryClient();
  const previous = useRef(userId);
  useEffect(() => {
    const changed = previous.current != null && previous.current !== userId;
    previous.current = userId;
    if (changed) queryClient.removeQueries({ queryKey: bookmarkStatesQueryKey() });
  }, [queryClient, userId]);
}
