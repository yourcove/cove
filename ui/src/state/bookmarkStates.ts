import type { QueryClient } from "@tanstack/react-query";
import { bookmarks } from "../api/client";
import type { AffinityHostType, BookmarkState } from "../api/types";

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

interface Waiter {
  resolve: (state: BookmarkState) => void;
  reject: (error: unknown) => void;
}

const pending = new Map<AffinityHostType, Map<number, Waiter[]>>();
let flushScheduled = false;

export function fetchBookmarkState(hostType: AffinityHostType, hostId: number): Promise<BookmarkState> {
  return new Promise((resolve, reject) => {
    let byId = pending.get(hostType);
    if (!byId) {
      byId = new Map();
      pending.set(hostType, byId);
    }
    const waiters = byId.get(hostId);
    if (waiters) waiters.push({ resolve, reject });
    else byId.set(hostId, [{ resolve, reject }]);

    if (!flushScheduled) {
      flushScheduled = true;
      // A macrotask lets every card mounted by the same render register before the request.
      setTimeout(flushPending, 0);
    }
  });
}

function flushPending() {
  flushScheduled = false;
  const batches = [...pending.entries()];
  pending.clear();
  for (const [hostType, byId] of batches) {
    const ids = [...byId.keys()];
    for (let start = 0; start < ids.length; start += MAX_IDS_PER_REQUEST) {
      void requestChunk(hostType, ids.slice(start, start + MAX_IDS_PER_REQUEST), byId);
    }
  }
}

async function requestChunk(hostType: AffinityHostType, ids: number[], byId: Map<number, Waiter[]>) {
  try {
    const states = await bookmarks.batch({ hostType, hostIds: ids });
    const byHostId = new Map(states.map((state) => [state.hostId, state]));
    for (const id of ids) {
      const state = byHostId.get(id) ?? { hostType, hostId: id, saved: false };
      for (const waiter of byId.get(id) ?? []) waiter.resolve(state);
    }
  } catch (error) {
    for (const id of ids) {
      for (const waiter of byId.get(id) ?? []) waiter.reject(error);
    }
  }
}
