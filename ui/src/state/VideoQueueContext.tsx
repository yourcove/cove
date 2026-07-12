import { createContext, useContext, useState, useCallback, useEffect, useRef, type ReactNode } from "react";

interface VideoQueueState {
  videoIds: number[];
  currentIndex: number;
  autoplay: boolean;
  items?: Record<number, VideoQueueItem>;
  startIndex?: number;
  totalCount?: number;
  hasRemotePrevious?: boolean;
  hasRemoteNext?: boolean;
}

// Persist the queue per-tab so a refresh or back/forward navigation doesn't lose it. sessionStorage
// (not localStorage) matches the route-history store and keeps the queue scoped to the tab that
// started playing — it survives reloads and in-tab history navigation but doesn't bleed across tabs.
const QUEUE_STORAGE_KEY = "cove-video-queue";

function readStoredQueue(): VideoQueueState | null {
  try {
    const raw = sessionStorage.getItem(QUEUE_STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed || !Array.isArray(parsed.videoIds) || typeof parsed.currentIndex !== "number") return null;
    // Page loaders are functions and intentionally cannot be persisted. A restored queue remains
    // usable for its already-loaded items without exposing boundary controls that cannot load.
    return {
      ...parsed,
      startIndex: 0,
      totalCount: parsed.videoIds.length,
      hasRemotePrevious: false,
      hasRemoteNext: false,
    } as VideoQueueState;
  } catch {
    return null;
  }
}

export interface VideoQueueItem {
  id: number;
  title?: string | null;
  subtitle?: string | null;
  imagePath?: string | null;
}

interface VideoQueuePageResult { items: VideoQueueItem[]; hasMore: boolean }
interface VideoQueueOptions {
  startIndex: number;
  totalCount: number;
  loadPrevious?: () => Promise<VideoQueuePageResult>;
  loadNext?: () => Promise<VideoQueuePageResult>;
}

interface VideoQueueContextValue {
  queue: VideoQueueState | null;
  setQueue: (ids: number[], currentId: number, items?: VideoQueueItem[], options?: VideoQueueOptions) => void;
  clearQueue: () => void;
  currentId: number | null;
  prevId: number | null;
  nextId: number | null;
  hasPrev: boolean;
  hasNext: boolean;
  goToIndex: (index: number) => number | null;
  goPrevious: () => Promise<number | null>;
  goNext: () => Promise<number | null>;
  toggleAutoplay: () => void;
  autoplay: boolean;
  queueLength: number;
  currentPosition: number;
  queueItems: VideoQueueItem[];
}

const VideoQueueContext = createContext<VideoQueueContextValue | null>(null);

export function VideoQueueProvider({ children }: { children: ReactNode }) {
  const [queue, setQueueState] = useState<VideoQueueState | null>(() => readStoredQueue());
  const loadersRef = useRef<Pick<VideoQueueOptions, "loadPrevious" | "loadNext">>({});
  const loadingBoundaryRef = useRef(false);
  const queueGenerationRef = useRef(0);

  useEffect(() => {
    try {
      if (queue) sessionStorage.setItem(QUEUE_STORAGE_KEY, JSON.stringify(queue));
      else sessionStorage.removeItem(QUEUE_STORAGE_KEY);
    } catch {
      // Ignore storage failures (private mode / quota).
    }
  }, [queue]);

  const setQueue = useCallback((ids: number[], currentId: number, items?: VideoQueueItem[], options?: VideoQueueOptions) => {
    queueGenerationRef.current += 1;
    const idx = ids.indexOf(currentId);
    const itemMap = items?.reduce<Record<number, VideoQueueItem>>((map, item) => {
      map[item.id] = item;
      return map;
    }, {});
    loadersRef.current = { loadPrevious: options?.loadPrevious, loadNext: options?.loadNext };
    setQueueState({
      videoIds: ids,
      currentIndex: idx >= 0 ? idx : 0,
      autoplay: false,
      items: itemMap,
      startIndex: options?.startIndex ?? 0,
      totalCount: options?.totalCount ?? ids.length,
      hasRemotePrevious: Boolean(options?.loadPrevious),
      hasRemoteNext: Boolean(options?.loadNext),
    });
  }, []);

  const clearQueue = useCallback(() => {
    queueGenerationRef.current += 1;
    loadersRef.current = {};
    setQueueState(null);
  }, []);

  const currentId = queue ? queue.videoIds[queue.currentIndex] ?? null : null;
  const prevId = queue && queue.currentIndex > 0 ? queue.videoIds[queue.currentIndex - 1] : null;
  const nextId = queue && queue.currentIndex < queue.videoIds.length - 1 ? queue.videoIds[queue.currentIndex + 1] : null;
  const queueItems = queue
    ? queue.videoIds.map((id) => queue.items?.[id] ?? { id })
    : [];

  const goToIndex = useCallback((index: number) => {
    if (!queue || index < 0 || index >= queue.videoIds.length) return null;
    queueGenerationRef.current += 1;
    const id = queue.videoIds[index];
    setQueueState({ ...queue, currentIndex: index });
    return id;
  }, [queue]);

  const loadBoundary = useCallback(async (direction: "previous" | "next") => {
    if (loadingBoundaryRef.current) return null;
    const loader = direction === "previous" ? loadersRef.current.loadPrevious : loadersRef.current.loadNext;
    if (!loader) return null;
    const generation = queueGenerationRef.current;
    loadingBoundaryRef.current = true;
    try {
      const result = await loader();
      if (queueGenerationRef.current !== generation) return null;
      if (result.items.length === 0) return null;
      const ids = result.items.map((item) => item.id);
      const itemMap = Object.fromEntries(result.items.map((item) => [item.id, item]));
      const targetId = direction === "previous" ? ids.at(-1) ?? null : ids[0] ?? null;
      setQueueState((current) => {
        if (!current) return current;
        if (direction === "previous") {
          if (!result.hasMore) loadersRef.current.loadPrevious = undefined;
          return { ...current, videoIds: [...ids, ...current.videoIds], currentIndex: ids.length - 1, startIndex: Math.max(0, (current.startIndex ?? 0) - ids.length), items: { ...current.items, ...itemMap }, hasRemotePrevious: result.hasMore };
        }
        if (!result.hasMore) loadersRef.current.loadNext = undefined;
        return { ...current, videoIds: [...current.videoIds, ...ids], currentIndex: current.videoIds.length, items: { ...current.items, ...itemMap }, hasRemoteNext: result.hasMore };
      });
      return targetId;
    } catch {
      return null;
    } finally {
      loadingBoundaryRef.current = false;
    }
  }, []);

  const goPrevious = useCallback(async () => {
    if (prevId != null && queue) return goToIndex(queue.currentIndex - 1);
    return loadBoundary("previous");
  }, [goToIndex, loadBoundary, prevId, queue]);
  const goNext = useCallback(async () => {
    if (nextId != null && queue) return goToIndex(queue.currentIndex + 1);
    return loadBoundary("next");
  }, [goToIndex, loadBoundary, nextId, queue]);

  const toggleAutoplay = useCallback(() => {
    setQueueState((prev) => prev ? { ...prev, autoplay: !prev.autoplay } : null);
  }, []);

  return (
    <VideoQueueContext.Provider
      value={{
        queue,
        setQueue,
        clearQueue,
        currentId,
        prevId,
        nextId,
        hasPrev: prevId !== null || Boolean(queue?.hasRemotePrevious),
        hasNext: nextId !== null || Boolean(queue?.hasRemoteNext),
        goToIndex,
        goPrevious,
        goNext,
        toggleAutoplay,
        autoplay: queue?.autoplay ?? false,
        queueLength: queue?.totalCount ?? queue?.videoIds.length ?? 0,
        currentPosition: queue ? (queue.startIndex ?? 0) + queue.currentIndex + 1 : 0,
        queueItems,
      }}
    >
      {children}
    </VideoQueueContext.Provider>
  );
}

export function useVideoQueue() {
  const ctx = useContext(VideoQueueContext);
  if (!ctx) throw new Error("useVideoQueue must be used within VideoQueueProvider");
  return ctx;
}
