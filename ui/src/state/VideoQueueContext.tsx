import { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from "react";

interface VideoQueueState {
  videoIds: number[];
  currentIndex: number;
  autoplay: boolean;
  items?: Record<number, VideoQueueItem>;
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
    return parsed as VideoQueueState;
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

interface VideoQueueContextValue {
  queue: VideoQueueState | null;
  setQueue: (ids: number[], currentId: number, items?: VideoQueueItem[]) => void;
  clearQueue: () => void;
  currentId: number | null;
  prevId: number | null;
  nextId: number | null;
  hasPrev: boolean;
  hasNext: boolean;
  goToIndex: (index: number) => number | null;
  toggleAutoplay: () => void;
  autoplay: boolean;
  queueLength: number;
  currentPosition: number;
  queueItems: VideoQueueItem[];
}

const VideoQueueContext = createContext<VideoQueueContextValue | null>(null);

export function VideoQueueProvider({ children }: { children: ReactNode }) {
  const [queue, setQueueState] = useState<VideoQueueState | null>(() => readStoredQueue());

  useEffect(() => {
    try {
      if (queue) sessionStorage.setItem(QUEUE_STORAGE_KEY, JSON.stringify(queue));
      else sessionStorage.removeItem(QUEUE_STORAGE_KEY);
    } catch {
      // Ignore storage failures (private mode / quota).
    }
  }, [queue]);

  const setQueue = useCallback((ids: number[], currentId: number, items?: VideoQueueItem[]) => {
    const idx = ids.indexOf(currentId);
    const itemMap = items?.reduce<Record<number, VideoQueueItem>>((map, item) => {
      map[item.id] = item;
      return map;
    }, {});
    setQueueState({ videoIds: ids, currentIndex: idx >= 0 ? idx : 0, autoplay: false, items: itemMap });
  }, []);

  const clearQueue = useCallback(() => setQueueState(null), []);

  const currentId = queue ? queue.videoIds[queue.currentIndex] ?? null : null;
  const prevId = queue && queue.currentIndex > 0 ? queue.videoIds[queue.currentIndex - 1] : null;
  const nextId = queue && queue.currentIndex < queue.videoIds.length - 1 ? queue.videoIds[queue.currentIndex + 1] : null;
  const queueItems = queue
    ? queue.videoIds.map((id) => queue.items?.[id] ?? { id })
    : [];

  const goToIndex = useCallback((index: number) => {
    if (!queue || index < 0 || index >= queue.videoIds.length) return null;
    const id = queue.videoIds[index];
    setQueueState({ ...queue, currentIndex: index });
    return id;
  }, [queue]);

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
        hasPrev: prevId !== null,
        hasNext: nextId !== null,
        goToIndex,
        toggleAutoplay,
        autoplay: queue?.autoplay ?? false,
        queueLength: queue?.videoIds.length ?? 0,
        currentPosition: queue ? queue.currentIndex + 1 : 0,
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

