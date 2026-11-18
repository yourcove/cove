import { createContext, useCallback, useContext, useState, useSyncExternalStore, type ReactNode } from "react";

/**
 * What a reviewer decided for each similar face: true links it, false leaves it out. A skip is always kept;
 * a decision to link is kept only where it differs from the face's default.
 */
export type SimilarReviewChoices = ReadonlyMap<number, boolean>;

/** One review of a face's similar faces for one performer. */
export interface SimilarReviewRecord {
  choices: SimilarReviewChoices;
}

export const EMPTY_SIMILAR_REVIEW: SimilarReviewRecord = Object.freeze({ choices: new Map<number, boolean>() });

/** A review belongs to the face it was made on and to the performer it links to. */
export function similarReviewKey(sourceFaceId: number, performerId: number) {
  return `${sourceFaceId}:${performerId}`;
}

export interface SimilarReviewStore {
  get: (key: string) => SimilarReviewRecord;
  update: (key: string, change: (record: SimilarReviewRecord) => SimilarReviewRecord) => void;
  subscribe: (listener: () => void) => () => void;
}

function createSimilarReviewStore(): SimilarReviewStore {
  const records = new Map<string, SimilarReviewRecord>();
  const listeners = new Set<() => void>();
  return {
    get: (key) => records.get(key) ?? EMPTY_SIMILAR_REVIEW,
    update: (key, change) => {
      const next = change(records.get(key) ?? EMPTY_SIMILAR_REVIEW);
      if (next.choices.size === 0) {
        records.delete(key);
      } else {
        records.set(key, next);
      }
      listeners.forEach((listener) => listener());
    },
    subscribe: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

const SimilarReviewStoreContext = createContext<SimilarReviewStore | null>(null);

/**
 * Keeps similar-faces reviews for the session, above the page that App mounts anew for every face, so
 * leaving a face mid-review or mid-batch and coming back (Back included) finds the skips as they were. The
 * batch links every candidate that is not skipped, so a lost skip would send someone else to be linked; the
 * server still refuses a face the user marked as someone else unless the review overrides that mark.
 */
export function SimilarReviewChoicesProvider({ children }: { children: ReactNode }) {
  const [store] = useState(createSimilarReviewStore);
  return <SimilarReviewStoreContext.Provider value={store}>{children}</SimilarReviewStoreContext.Provider>;
}

/**
 * The review stored under `key` (or none while there is no key) and the store to change it in. The store is
 * the only copy, so every mounted page sees the same choices. Outside a provider a store lives as long as the
 * component that asks for it.
 */
export function useSimilarReview(key: string | null) {
  const sessionStore = useContext(SimilarReviewStoreContext);
  const [ownStore] = useState(createSimilarReviewStore);
  const store = sessionStore ?? ownStore;
  const getSnapshot = useCallback(() => (key ? store.get(key) : EMPTY_SIMILAR_REVIEW), [key, store]);
  const record = useSyncExternalStore(store.subscribe, getSnapshot, getSnapshot);
  return { record, store };
}
