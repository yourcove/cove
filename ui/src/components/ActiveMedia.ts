import { useEffectEvent, useLayoutEffect, useRef, useSyncExternalStore } from "react";

/** Observed content, not a grid selection. No tokens or URLs are exposed. */
export interface ActiveMediaContext {
  kind: "image" | "video";
  id: number;
  surface: "detail" | "quick-view" | "compilation" | "lightbox";
  positionSeconds?: number;
}

const sources = new Map<symbol, { value: ActiveMediaContext | null; priority: number }>();
const listeners = new Set<() => void>();
let snapshot: ActiveMediaContext | null = null;

function publish() {
  const ordered = [...sources.values()].sort((a, b) => b.priority - a.priority);
  // Multiple equally authoritative players are ambiguous; do not silently pick one.
  const first = ordered[0];
  const next =
    first?.value &&
    !ordered.some(
      (x) => x.priority === first.priority && (x.value?.kind !== first.value?.kind || x.value?.id !== first.value?.id),
    )
      ? first.value
      : null;
  if (JSON.stringify(next) === JSON.stringify(snapshot)) return;
  snapshot = next;
  listeners.forEach((listener) => listener());
}

/** Host viewers register and clean up their actual displayed item. */
export function registerActiveMedia(owner: symbol, value: ActiveMediaContext | null, priority: number) {
  sources.set(owner, { value, priority });
  publish();
  return () => {
    sources.delete(owner);
    publish();
  };
}

export function usePublishActiveMedia(value: ActiveMediaContext | null, priority: number, enabled = value !== null) {
  const owner = useRef(Symbol("media-viewer"));
  // Re-register only when a published field changes; callers may pass a fresh
  // object with identical contents on every render.
  const register = useEffectEvent(() => registerActiveMedia(owner.current, value, priority));
  useLayoutEffect(() => {
    if (!enabled) return;
    return register();
  }, [value?.kind, value?.id, value?.surface, value?.positionSeconds, priority, enabled]);
}

export const getActiveMedia = () => snapshot;
export function useActiveMedia() {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    getActiveMedia,
    () => null,
  );
}
