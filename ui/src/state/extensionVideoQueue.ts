import { useCallback, useEffect, useMemo, useRef } from "react";
import { useOptionalAppConfig } from "./AppConfigContext";
import { useOptionalVideoQueue } from "./VideoQueueContext";

/**
 * The video queue as extensions see it through `@cove/runtime/components`. Everything here is a
 * public contract with extensions that are already built: add members, never rename or remove them.
 * The types are declared here, apart from the provider's own, so a field the provider gains does
 * not become part of the contract by accident.
 */
export interface VideoQueueItem {
  id: number;
  title?: string | null;
  subtitle?: string | null;
  /** The URL of the image the queue panel shows for the video. */
  imagePath?: string | null;
}

export interface VideoQueueOptions {
  /** Whether the next video starts when one ends. Left out, the queue starts with the user's auto-advance setting. */
  autoplay?: boolean;
  /** The opener's own label for the queue, readable later as `queue.source`. */
  source?: string;
}

export interface VideoQueueSummary {
  /** The videos the queue has loaded, in play order. */
  videoIds: readonly number[];
  /** The label the queue was opened with, if any. */
  source?: string;
}

export interface VideoQueueHandle {
  /** The current queue, or null when there is none. */
  queue: VideoQueueSummary | null;
  /** The queue's current video, or null without a queue. */
  currentId: number | null;
  /** Whether the next video starts when one ends; false without a queue. */
  autoplay: boolean;
  /** Replaces the queue. `currentId` falls back to the first video when it is not in `ids`; no `ids` clears the queue. */
  setQueue: (
    ids: readonly number[],
    currentId: number,
    items?: readonly VideoQueueItem[],
    options?: VideoQueueOptions,
  ) => void;
  /**
   * Changes how videos already in the queue are shown, without changing the queue, its position or its
   * order. A field left out of an item keeps its value; null clears it.
   */
  updateItems: (items: readonly VideoQueueItem[]) => void;
  clearQueue: () => void;
}

function text(value: unknown): string | null | undefined {
  return typeof value === "string" || value === null ? value : undefined;
}

/**
 * Extensions are not type-checked against the host, and the queue is rendered and persisted as it is
 * stored, so only well-formed fields of well-formed items are taken, and arrays are copied.
 */
function toItems(items: readonly VideoQueueItem[] | undefined) {
  if (!Array.isArray(items)) return undefined;
  return (items as readonly VideoQueueItem[])
    .filter((item) => item != null && Number.isFinite(item.id))
    .map((item) => ({
      id: item.id,
      title: text(item.title),
      subtitle: text(item.subtitle),
      imagePath: text(item.imagePath),
    }));
}

/**
 * Returns the video queue, or null where no queue is available. `setQueue`, `updateItems` and
 * `clearQueue` keep their identity across queue changes, so they are safe in effect dependencies.
 */
export function useExtensionVideoQueue(): VideoQueueHandle | null {
  const context = useOptionalVideoQueue();
  const defaultAutoplay = useOptionalAppConfig()?.config?.ui.continuePlaylistDefault ?? false;
  // Read when a queue is opened, so the setting loading or changing does not give setQueue a new identity.
  const defaultAutoplayRef = useRef(defaultAutoplay);
  useEffect(() => {
    defaultAutoplayRef.current = defaultAutoplay;
  }, [defaultAutoplay]);

  const videoIds = context?.queue?.videoIds;
  const source = context?.queue?.source;
  const currentId = context?.currentId ?? null;
  const autoplay = context?.autoplay ?? false;
  const setHostQueue = context?.setQueue;
  const updateHostItems = context?.updateItems;
  const clearQueue = context?.clearQueue;

  const queue = useMemo<VideoQueueSummary | null>(() => (videoIds ? { videoIds, source } : null), [videoIds, source]);

  const setQueue = useCallback<VideoQueueHandle["setQueue"]>(
    (ids, startId, items, options) => {
      const queued = Array.isArray(ids) ? (ids as readonly number[]).filter((id) => Number.isFinite(id)) : [];
      if (queued.length === 0) {
        clearQueue?.();
        return;
      }
      setHostQueue?.(queued, startId, toItems(items), {
        autoplay: typeof options?.autoplay === "boolean" ? options.autoplay : defaultAutoplayRef.current,
        source: typeof options?.source === "string" ? options.source : undefined,
      });
    },
    [clearQueue, setHostQueue],
  );

  const updateItems = useCallback<VideoQueueHandle["updateItems"]>(
    (items) => {
      const queued = toItems(items);
      if (queued?.length) updateHostItems?.(queued);
    },
    [updateHostItems],
  );

  return useMemo(() => {
    if (!setHostQueue || !updateHostItems || !clearQueue) return null;
    return { queue, currentId, autoplay, setQueue, updateItems, clearQueue };
  }, [autoplay, clearQueue, currentId, queue, setHostQueue, setQueue, updateHostItems, updateItems]);
}
