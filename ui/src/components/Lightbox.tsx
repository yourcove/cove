import { useState, useEffect, useEffectEvent, useCallback, useId, useLayoutEffect, useMemo, useRef } from "react";
import { createPortal } from "react-dom";
import { usePublishActiveMedia } from "./ActiveMedia";
import {
  X,
  ChevronLeft,
  ChevronRight,
  Play,
  Pause,
  ZoomIn,
  ZoomOut,
  RotateCcw,
  Maximize2,
  Minimize2,
  Settings,
  ThumbsUp,
} from "lucide-react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { images as imageApi, playback } from "../api/client";
import { createPlaybackSessionId, trackInteraction } from "../utils/interactionTracking";
import { pushOverlay } from "../utils/overlayState";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { InteractiveRating } from "./Rating";
import type { EntityEngagement } from "../api/types";
import { useKeySequence } from "../hooks/useKeySequence";
import { buildRouteUrl, navigateToUrl, resolveContextualDetailRoute, type Route } from "../router/location";
import { hasImageReferences, ImageReferencePopovers, type ImageReferences } from "./EntityCards";

// Movement (px) past which a pointer gesture counts as a pan rather than a click. Below it, releasing
// the pointer toggles zoom; above it the gesture is a drag and must not also toggle zoom on release.
const DRAG_CLICK_THRESHOLD_PX = 5;
const MAX_ZOOM = 5;

type Point = { x: number; y: number };

// A two-finger gesture, measured from the image's untransformed centre so the zoom anchors on the
// point between the fingers.
interface PinchStart {
  pointerIds: [number, number];
  origin: Point;
  distance: number;
  midpoint: Point;
  zoom: number;
  pan: Point;
}

const distanceBetween = (a: Point, b: Point) => Math.max(Math.hypot(b.x - a.x, b.y - a.y), 1);
const midpointOf = (a: Point, b: Point, origin: Point): Point => ({
  x: (a.x + b.x) / 2 - origin.x,
  y: (a.y + b.y) / 2 - origin.y,
});

// The image renders at `centre + zoom * offset + pan`, so the image point under the starting midpoint
// stays under the current midpoint when the pan moves by the midpoint shift minus its scaled offset.
function pinchView(start: PinchStart, distance: number, midpoint: Point): { zoom: number; pan: Point } {
  const zoom = Math.min(Math.max((start.zoom * distance) / start.distance, 1), MAX_ZOOM);
  if (zoom === 1) return { zoom, pan: { x: 0, y: 0 } };
  const ratio = zoom / start.zoom;
  return {
    zoom,
    pan: {
      x: midpoint.x - ratio * (start.midpoint.x - start.pan.x),
      y: midpoint.y - ratio * (start.midpoint.y - start.pan.y),
    },
  };
}

export interface LightboxImage {
  id: number;
  src: string;
  title?: string;
  interactionSource?: string;
  interactionMeta?: Record<string, unknown>;
  /** Tags, performers, galleries and groups shown as hover popovers, matching the image card footer. */
  references?: ImageReferences;
}

export interface LightboxProps {
  images: LightboxImage[];
  initialIndex: number;
  open: boolean;
  onClose: () => void;
  slideshowDelay?: number;
  autoPlay?: boolean;
  canEngage?: boolean;
  canLike?: boolean;
  loadPrevious?: () => Promise<LightboxImage[]>;
  loadNext?: () => Promise<LightboxImage[]>;
  hasPrevious?: boolean;
  hasNext?: boolean;
  wrap?: boolean;
  totalCount?: number;
  positionOffset?: number;
}

export function Lightbox({
  images,
  initialIndex,
  open,
  onClose,
  slideshowDelay = 5000,
  autoPlay = false,
  canEngage = false,
  canLike = false,
  loadPrevious,
  loadNext,
  hasPrevious = false,
  hasNext = false,
  wrap = true,
  totalCount,
  positionOffset = 0,
}: LightboxProps) {
  const [queuedImages, setQueuedImages] = useState(images);
  const [index, setIndex] = useState(initialIndex);
  const [displayed, setDisplayed] = useState<LightboxImage | null>(null);
  const [failedSrc, setFailedSrc] = useState<string | null>(null);
  const [playing, setPlaying] = useState(open && autoPlay);
  const [currentSlideshowDelay, setCurrentSlideshowDelay] = useState(slideshowDelay);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const [dragging, setDragging] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const settingsButtonRef = useRef<HTMLButtonElement>(null);
  const settingsPanelId = useId();
  // iPhone Safari only lets video elements go full screen, so the button would do nothing there.
  const fullscreenSupported = typeof document !== "undefined" && document.fullscreenEnabled === true;
  const [boundaryLoading, setBoundaryLoading] = useState(false);

  const dragStart = useRef({ x: 0, y: 0 });
  const panStart = useRef({ x: 0, y: 0 });
  // Whether the current pointer gesture moved far enough to count as a pan (vs. a click). Read by the
  // image's onClick to decide if a release should toggle zoom. A ref, not state, so onClick sees the
  // up-to-date value even though the click fires after pointerup has already reset `dragging`.
  const pointerMoved = useRef(false);
  // Every pointer down on the image area, so a second finger can turn a tap or pan into a pinch.
  const activePointers = useRef(new Map<number, Point>());
  const pinchStart = useRef<PinchStart | null>(null);
  // The latest zoom and pan, which pointer handlers read so a gesture never starts from a view React has
  // not rendered yet.
  const view = useRef<{ zoom: number; pan: Point }>({ zoom: 1, pan: { x: 0, y: 0 } });
  useLayoutEffect(() => {
    view.current = { zoom, pan };
  }, [zoom, pan]);
  const stageRef = useRef<HTMLDivElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const slideshowTimer = useRef<ReturnType<typeof setInterval>>(undefined);
  const trackedOpen = useRef(false);
  const lastTrackedIndex = useRef<number | null>(null);

  const count = queuedImages.length;
  // The panel lives behind the gear, which only shows for a multi-image queue; a queue replaced by a
  // single image must not leave its invisible dismiss layer behind.
  const settingsShown = settingsOpen && count > 1;
  // A stale or absent total must never read as fewer results than the queue already holds.
  const displayCount = Math.max(totalCount ?? 0, count);
  const displayPosition = positionOffset + index + 1;
  const current = queuedImages[index];
  const currentSrc = useRef<string | undefined>(current?.src);
  const openSrc = open ? current?.src : undefined;
  // Image loads resolve asynchronously, so they compare against the source shown after the latest commit.
  useLayoutEffect(() => {
    currentSrc.current = openSrc;
  }, [openSrc]);
  const loading = Boolean(current && displayed?.src !== current.src);
  usePublishActiveMedia(
    open && !loading && current ? { kind: "image", id: current.id, surface: "lightbox" } : null,
    30,
    open,
  );
  const failed = failedSrc === current?.src;
  // References describe the image on screen, so they keep showing the previous one until the next has loaded.
  const referenceImage = loading ? displayed : current;
  const visibleImages =
    displayed && displayed.src !== current?.src ? [displayed, ...(current ? [current] : [])] : current ? [current] : [];
  const queryClient = useQueryClient();
  const { engagement, rating, setRating, ratingPending } = useEntityEngagement("image", current?.id ?? 0, {
    enabled: open && Boolean(current),
  });
  const likeMutation = useMutation({
    mutationFn: (imageId: number) => imageApi.incrementLike(imageId),
    onSuccess: (likeCount, imageId) => {
      queryClient.setQueryData(["engagement", "image", imageId], (existing: EntityEngagement | undefined) =>
        existing ? { ...existing, likeCount } : existing,
      );
      queryClient.invalidateQueries({ queryKey: ["engagement", "image", imageId] });
      queryClient.invalidateQueries({ queryKey: ["engagement", "image", "batch"] });
      queryClient.invalidateQueries({ queryKey: ["image", imageId] });
      queryClient.invalidateQueries({ queryKey: ["image", imageId, "history"] });
      queryClient.invalidateQueries({ queryKey: ["gallery-like-count"] });
    },
  });
  const likeCount = likeMutation.data ?? engagement?.likeCount ?? 0;

  const resetLikeMutation = useEffectEvent(() => likeMutation.reset());
  // A like result belongs to the image it was recorded for; clear it when the viewer moves on.
  useEffect(() => {
    resetLikeMutation();
  }, [current?.id]);

  const trackCurrentImageInteraction = useCallback(
    (kind: string, extraMeta?: Record<string, unknown>) => {
      if (!current) {
        return;
      }

      trackInteraction({
        hostType: "image",
        hostId: current.id,
        kind,
        meta: {
          source: current.interactionSource ?? "lightbox",
          ...current.interactionMeta,
          ...extraMeta,
        },
      });
    },
    [current],
  );

  // Sync the queue and index when initialIndex or open changes, and clear the shown image on close.
  // Only these two props trigger the reset, so a late-resolving images/autoPlay/slideshowDelay config
  // does not discard pages the viewer loaded by navigating past the end of the queue. The delay is
  // applied separately below, so a config that lands mid-session still takes effect.
  const [prevOpen, setPrevOpen] = useState(open);
  const [prevInitialIndex, setPrevInitialIndex] = useState(initialIndex);
  if (open !== prevOpen || initialIndex !== prevInitialIndex) {
    setPrevOpen(open);
    setPrevInitialIndex(initialIndex);
    if (open) {
      setQueuedImages(images);
      setIndex(initialIndex);
      setZoom(1);
      setPan({ x: 0, y: 0 });
      setPlaying(autoPlay);
      setCurrentSlideshowDelay(slideshowDelay);
    } else if (open !== prevOpen) {
      setDisplayed(null);
      setFailedSrc(null);
    }
  }

  const [prevSlideshowDelay, setPrevSlideshowDelay] = useState(slideshowDelay);
  if (slideshowDelay !== prevSlideshowDelay) {
    setPrevSlideshowDelay(slideshowDelay);
    setCurrentSlideshowDelay(slideshowDelay);
  }

  const [prevCurrentSrc, setPrevCurrentSrc] = useState(current?.src);
  if (current?.src !== prevCurrentSrc) {
    setPrevCurrentSrc(current?.src);
    setFailedSrc(null);
  }

  // Fullscreen is only tracked while open; the listener below re-reads it on the next open.
  if (!open && fullscreen) {
    setFullscreen(false);
  }
  // Reopening starts with the settings panel closed.
  if (!open && settingsOpen) {
    setSettingsOpen(false);
  }

  useEffect(() => {
    if (open) {
      trackedOpen.current = false;
      lastTrackedIndex.current = null;
    }
  }, [open, initialIndex]);

  useEffect(() => {
    if (!open || !current) {
      return;
    }

    if (!trackedOpen.current) {
      trackedOpen.current = true;
      lastTrackedIndex.current = index;
      trackCurrentImageInteraction("openLightbox", { index: displayPosition, count: displayCount });
      return;
    }

    if (lastTrackedIndex.current !== null && lastTrackedIndex.current !== index) {
      trackCurrentImageInteraction("navigate", {
        fromIndex: positionOffset + lastTrackedIndex.current + 1,
        toIndex: displayPosition,
        count: displayCount,
      });
      lastTrackedIndex.current = index;
    }
  }, [current, displayCount, displayPosition, index, open, positionOffset, trackCurrentImageInteraction]);

  const currentId = current?.id;
  // Read when a dwell session starts, so its report describes the image and position it began on.
  const getDwellContext = useEffectEvent(() => ({
    index: positionOffset + index + 1,
    count: displayCount,
    source: current?.interactionSource ?? "lightbox",
    ...current?.interactionMeta,
  }));
  useEffect(() => {
    if (!open || currentId === undefined) {
      return;
    }

    const imageId = currentId;
    const context = getDwellContext();
    const startedAt = typeof performance === "undefined" ? Date.now() : performance.now();
    const sessionId = createPlaybackSessionId();
    const elapsedSeconds = () => {
      const now = typeof performance === "undefined" ? Date.now() : performance.now();
      return Math.max(0.001, (now - startedAt) / 1000);
    };
    let flushed = false;
    const flushDwell = (state: "ended" | "abandoned") => {
      if (flushed) return;
      flushed = true;
      const durationSec = elapsedSeconds();
      void playback
        .recordIntervals({
          hostType: "image",
          hostId: imageId,
          sessionId,
          mediaDurationSec: durationSec,
          currentPositionSec: durationSec,
          state,
          surface: "lightbox",
          scopeKey: `image:${imageId}:lightbox`,
          context,
          intervals: [{ startSec: 0, endSec: durationSec }],
        })
        .catch(() => {});
    };

    const handlePageHide = () => flushDwell("abandoned");
    window.addEventListener("pagehide", handlePageHide);
    return () => {
      window.removeEventListener("pagehide", handlePageHide);
      flushDwell("ended");
    };
  }, [count, currentId, index, open]);

  // Lock body scroll + claim keyboard ownership so background list/app shortcuts pause while open.
  useEffect(() => {
    if (!open) return;
    const releaseOverlay = pushOverlay();
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      releaseOverlay();
      document.body.style.overflow = prev;
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;

    const handleFullscreenChange = () => {
      setFullscreen(document.fullscreenElement === containerRef.current);
    };

    handleFullscreenChange();
    document.addEventListener("fullscreenchange", handleFullscreenChange);
    return () => document.removeEventListener("fullscreenchange", handleFullscreenChange);
  }, [open]);

  const resetView = useCallback(() => {
    setZoom(1);
    setPan({ x: 0, y: 0 });
    // A gesture in progress belongs to the image it started on.
    pinchStart.current = null;
    setDragging(false);
  }, []);

  const goTo = useCallback(
    (next: number) => {
      setIndex(((next % count) + count) % count);
      resetView();
    },
    [count, resetView],
  );

  const goPrev = useCallback(async () => {
    if (index > 0 || !hasPrevious || !loadPrevious) {
      if (index === 0 && !wrap) return;
      goTo(index - 1);
      return;
    }
    setBoundaryLoading(true);
    try {
      const loaded = await loadPrevious();
      if (loaded.length === 0) return;
      setQueuedImages((currentImages) => [...loaded, ...currentImages]);
      // Prepending shifts every existing position. Follow the new page only if the viewer is still
      // where the load started; otherwise keep them on the image they navigated to meanwhile.
      setIndex((current) => (current === index ? loaded.length - 1 : current + loaded.length));
      resetView();
    } catch {
      // Keep the current image visible when an adjacent page cannot be loaded.
    } finally {
      setBoundaryLoading(false);
    }
  }, [goTo, hasPrevious, index, loadPrevious, resetView, wrap]);
  const goNext = useCallback(async () => {
    if (index < count - 1 || !hasNext || !loadNext) {
      if (index === count - 1 && !wrap) return;
      goTo(index + 1);
      return;
    }
    setBoundaryLoading(true);
    try {
      const loaded = await loadNext();
      if (loaded.length === 0) return;
      setQueuedImages((currentImages) => [...currentImages, ...loaded]);
      setIndex((current) => (current === index ? current + 1 : current));
      resetView();
    } catch {
      // Keep the current image visible when an adjacent page cannot be loaded.
    } finally {
      setBoundaryLoading(false);
    }
  }, [count, goTo, hasNext, index, loadNext, resetView, wrap]);

  const toggleSlideshow = useCallback(() => setPlaying((p) => !p), []);

  const toggleZoom = useCallback(() => {
    if (zoom > 1) {
      resetView();
      trackCurrentImageInteraction("zoom", { action: "toggle", zoom: 1 });
      return;
    }

    setZoom(2);
    trackCurrentImageInteraction("zoom", { action: "toggle", zoom: 2 });
  }, [resetView, trackCurrentImageInteraction, zoom]);

  const handleZoomIn = useCallback(() => {
    const nextZoom = Math.min(zoom + 0.5, MAX_ZOOM);
    setZoom(nextZoom);
    if (nextZoom !== zoom) {
      trackCurrentImageInteraction("zoom", { action: "in", zoom: nextZoom });
    }
  }, [trackCurrentImageInteraction, zoom]);

  const handleZoomOut = useCallback(() => {
    const nextZoom = Math.max(zoom - 0.5, 1);
    setZoom(nextZoom);
    if (nextZoom === 1) {
      setPan({ x: 0, y: 0 });
    }
    if (nextZoom !== zoom) {
      trackCurrentImageInteraction("zoom", { action: "out", zoom: nextZoom });
    }
  }, [trackCurrentImageInteraction, zoom]);

  const toggleFullscreen = useCallback(async () => {
    if (document.fullscreenElement === containerRef.current) {
      await document.exitFullscreen();
      trackCurrentImageInteraction("fullscreen", { action: "exit" });
      return;
    }

    if (containerRef.current) {
      await containerRef.current.requestFullscreen();
      trackCurrentImageInteraction("fullscreen", { action: "enter" });
    }
  }, [trackCurrentImageInteraction]);

  const changeSlideshowDelay = useCallback(
    (deltaMs: number) => {
      setCurrentSlideshowDelay((current) => {
        const next = Math.min(30000, Math.max(1000, current + deltaMs));
        if (next !== current) {
          trackCurrentImageInteraction("slideshowDelay", { milliseconds: next });
        }
        return next;
      });
    },
    [trackCurrentImageInteraction],
  );

  const handleClose = useCallback(() => {
    if (open) {
      trackCurrentImageInteraction("closeLightbox", { index: displayPosition, count: displayCount });
    }

    if (document.fullscreenElement === containerRef.current) {
      void document.exitFullscreen();
    }

    onClose();
  }, [displayCount, displayPosition, onClose, open, trackCurrentImageInteraction]);

  // Following a reference leaves the viewer, so close it first; otherwise a page that stays mounted
  // across the navigation would keep the lightbox open over the destination.
  const navigateToReference = useCallback(
    (route: Route) => {
      handleClose();
      const contextualRoute = resolveContextualDetailRoute(route);
      navigateToUrl(buildRouteUrl(contextualRoute), { state: contextualRoute });
    },
    [handleClose],
  );

  // Slideshow
  useEffect(() => {
    if (playing && open) {
      slideshowTimer.current = setInterval(goNext, currentSlideshowDelay);
    }
    return () => {
      if (slideshowTimer.current) clearInterval(slideshowTimer.current);
    };
  }, [playing, open, goNext, currentSlideshowDelay]);

  // Keyboard
  useKeySequence(
    useMemo(
      () =>
        open
          ? [
              { id: "viewer.previous", keys: "ArrowLeft", surface: "viewer" as const, action: goPrev },
              { id: "viewer.next", keys: "ArrowRight", surface: "viewer" as const, action: goNext },
              { id: "viewer.slideshow", keys: "Space", surface: "viewer" as const, action: toggleSlideshow },
              {
                id: "viewer.fullscreen",
                keys: "f",
                surface: "viewer" as const,
                action: () => {
                  if (fullscreenSupported) void toggleFullscreen();
                },
              },
            ]
          : [],
      [fullscreenSupported, goNext, goPrev, open, toggleFullscreen, toggleSlideshow],
    ),
  );
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      switch (e.key) {
        case "Escape":
          e.preventDefault();
          // Escape dismisses the settings panel before the lightbox itself, returning focus to its button
          // so keyboard users are not dropped onto the page body.
          if (settingsShown) {
            setSettingsOpen(false);
            settingsButtonRef.current?.focus();
          } else handleClose();
          break;
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [handleClose, open, settingsShown]);

  // Scroll wheel zoom
  const handleWheel = useCallback((e: React.WheelEvent) => {
    e.preventDefault();
    const delta = e.deltaY > 0 ? -0.25 : 0.25;
    setZoom((z) => {
      const next = Math.min(Math.max(z + delta, 1), MAX_ZOOM);
      if (next === 1) setPan({ x: 0, y: 0 });
      return next;
    });
  }, []);

  // The shown image's centre before its zoom transform; offsets ignore transforms.
  const imageOrigin = useCallback((): Point => {
    const stage = stageRef.current;
    const image = stage?.querySelector<HTMLImageElement>("img[data-lightbox-shown]");
    if (!stage || !image) return { x: 0, y: 0 };
    const rect = stage.getBoundingClientRect();
    return {
      x: rect.left + stage.clientLeft + image.offsetLeft + image.offsetWidth / 2,
      y: rect.top + stage.clientTop + image.offsetTop + image.offsetHeight / 2,
    };
  }, []);

  const startPan = useCallback((point: Point) => {
    setDragging(true);
    dragStart.current = point;
    panStart.current = { ...view.current.pan };
  }, []);

  // Pan and pinch handlers
  const handlePointerDown = useCallback(
    (e: React.PointerEvent) => {
      const pointers = activePointers.current;
      // A primary pointer starts a new gesture, so drop any pointer whose release was never seen.
      if (e.isPrimary) {
        pointers.clear();
        pinchStart.current = null;
      }
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      (e.target as HTMLElement).setPointerCapture?.(e.pointerId);

      if (pointers.size === 2) {
        const [[firstId, first], [secondId, second]] = Array.from(pointers);
        // A pinch is never a tap, so its release must not toggle zoom.
        pointerMoved.current = true;
        setDragging(true);
        // The image centre does not move while zooming, so it is measured once per pinch.
        const origin = imageOrigin();
        pinchStart.current = {
          pointerIds: [firstId, secondId],
          origin,
          distance: distanceBetween(first, second),
          midpoint: midpointOf(first, second, origin),
          zoom: view.current.zoom,
          pan: { ...view.current.pan },
        };
        return;
      }
      if (pointers.size > 2) return;

      pointerMoved.current = false;
      if (view.current.zoom <= 1) return;
      startPan({ x: e.clientX, y: e.clientY });
    },
    [imageOrigin, startPan],
  );

  const handlePointerMove = useCallback(
    (e: React.PointerEvent) => {
      const pointers = activePointers.current;
      if (pointers.has(e.pointerId)) pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });

      const pinch = pinchStart.current;
      if (pinch) {
        const first = pointers.get(pinch.pointerIds[0]);
        const second = pointers.get(pinch.pointerIds[1]);
        if (!first || !second) return;
        const next = pinchView(pinch, distanceBetween(first, second), midpointOf(first, second, pinch.origin));
        view.current = next;
        setZoom(next.zoom);
        setPan(next.pan);
        return;
      }

      if (!dragging) return;
      const dx = e.clientX - dragStart.current.x;
      const dy = e.clientY - dragStart.current.y;
      if (!pointerMoved.current && Math.hypot(dx, dy) > DRAG_CLICK_THRESHOLD_PX) {
        pointerMoved.current = true;
      }
      const nextPan = { x: panStart.current.x + dx, y: panStart.current.y + dy };
      view.current = { ...view.current, pan: nextPan };
      setPan(nextPan);
    },
    [dragging],
  );

  const handlePointerUp = useCallback(
    (e: React.PointerEvent) => {
      const pointers = activePointers.current;
      pointers.delete(e.pointerId);
      const pinch = pinchStart.current;
      if (pinch?.pointerIds.includes(e.pointerId)) {
        pinchStart.current = null;
        const { zoom: pinchedZoom } = view.current;
        if (pinchedZoom !== pinch.zoom) {
          trackCurrentImageInteraction("zoom", { action: "pinch", zoom: pinchedZoom });
        }
        // The finger still down keeps panning the zoomed image instead of stalling until it lifts.
        const remaining = pointers.size === 1 ? pointers.values().next().value : undefined;
        if (remaining && pinchedZoom > 1) {
          startPan(remaining);
          return;
        }
      }
      if (pointers.size === 0 || !pinchStart.current) setDragging(false);
    },
    [startPan, trackCurrentImageInteraction],
  );

  // Preload adjacent images
  useEffect(() => {
    if (!open || count <= 1) return;
    const preload = (i: number) => {
      // Only the wrapping queue has a neighbour past either end; a partial one would fetch a slide
      // the disabled control cannot reach.
      if (!wrap && (i < 0 || i >= count)) return;
      const img = new Image();
      img.src = queuedImages[((i % count) + count) % count]?.src ?? "";
    };
    preload(index + 1);
    preload(index - 1);
  }, [open, index, queuedImages, count, wrap]);

  if (!open) return <></>;

  return createPortal(
    <div
      ref={containerRef}
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/90"
      onClick={(e) => {
        if (e.target === containerRef.current) handleClose();
      }}
    >
      {/* While the settings panel is open, a transparent layer under the top bar takes the next tap
          anywhere else, so dismissing the panel never also closes the viewer or zooms the image. */}
      {settingsShown ? (
        <div
          data-lightbox-settings-dismiss=""
          className="absolute inset-0 z-30"
          style={{ touchAction: "none" }}
          onClick={(e) => {
            // Marks the tap handled, so iOS does not treat a quick second tap as double-tap zoom.
            e.preventDefault();
            setSettingsOpen(false);
          }}
        />
      ) : null}

      {/* Top bar */}
      <div
        className={`absolute top-0 left-0 right-0 ${settingsShown ? "z-40" : "z-10"} flex items-center justify-between gap-3 p-4 pt-[max(1rem,env(safe-area-inset-top))] bg-gradient-to-b from-black/80 via-black/40 to-transparent`}
      >
        {/* The counter gives way on very narrow screens, so the controls and close button stay reachable.
            The image name sits at the bottom left. */}
        <span className="min-w-0 truncate text-white text-sm font-medium tabular-nums select-none">
          {displayPosition} / {displayCount}
        </span>
        <div className="flex shrink-0 items-center gap-2">
          {count > 1 ? (
            <div>
              {/* Not positioned: the panel anchors to the top bar so it stays on screen beside a narrow toolbar. */}
              <button
                ref={settingsButtonRef}
                onClick={() => setSettingsOpen(!settingsShown)}
                className="p-2 text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
                aria-label="Lightbox settings"
                aria-expanded={settingsShown}
                aria-controls={settingsShown ? settingsPanelId : undefined}
                title="Lightbox settings"
              >
                <Settings size={20} />
              </button>
              {settingsShown ? (
                <div
                  id={settingsPanelId}
                  role="group"
                  aria-label="Lightbox settings"
                  className="absolute right-4 top-full -mt-1 w-max max-w-[calc(100vw-2rem)] rounded-lg border border-white/15 bg-black/85 p-3 text-sm text-white shadow-lg backdrop-blur-sm"
                >
                  <div className="flex items-center justify-between gap-3">
                    <span className="whitespace-nowrap text-white/80">Slideshow delay</span>
                    <div className="flex items-center gap-1 rounded-lg border border-white/10 bg-white/5 px-1 py-0.5">
                      <button
                        onClick={() => changeSlideshowDelay(-1000)}
                        className="inline-flex h-8 w-8 items-center justify-center rounded text-white/80 transition-colors hover:bg-white/10 hover:text-white"
                        aria-label="Decrease slideshow delay"
                        title="Decrease slideshow delay"
                      >
                        -
                      </button>
                      <span className="min-w-[2.5rem] text-center tabular-nums" aria-live="polite">
                        {(currentSlideshowDelay / 1000).toFixed(0)}s
                      </span>
                      <button
                        onClick={() => changeSlideshowDelay(1000)}
                        className="inline-flex h-8 w-8 items-center justify-center rounded text-white/80 transition-colors hover:bg-white/10 hover:text-white"
                        aria-label="Increase slideshow delay"
                        title="Increase slideshow delay"
                      >
                        +
                      </button>
                    </div>
                  </div>
                </div>
              ) : null}
            </div>
          ) : null}
          <button
            onClick={handleZoomOut}
            className="p-2 max-sm:hidden text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
            aria-label="Zoom out"
            title="Zoom out"
          >
            <ZoomOut size={20} />
          </button>
          <button
            onClick={handleZoomIn}
            className="p-2 max-sm:hidden text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
            aria-label="Zoom in"
            title="Zoom in"
          >
            <ZoomIn size={20} />
          </button>
          <button
            onClick={() => resetView()}
            className="p-2 max-sm:hidden text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
            aria-label="Reset zoom"
            title="Reset zoom"
          >
            <RotateCcw size={20} />
          </button>
          {fullscreenSupported ? (
            <button
              onClick={() => void toggleFullscreen()}
              className="p-2 text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
              aria-label={fullscreen ? "Exit full screen" : "Enter full screen"}
              title={fullscreen ? "Exit full screen" : "Enter full screen"}
            >
              {fullscreen ? <Minimize2 size={20} /> : <Maximize2 size={20} />}
            </button>
          ) : null}
          <button
            onClick={toggleSlideshow}
            className="p-2 text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
            aria-label={playing ? "Pause slideshow" : "Play slideshow"}
          >
            {playing ? <Pause size={20} /> : <Play size={20} />}
          </button>
          <button
            onClick={handleClose}
            className="p-2 text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
            aria-label="Close (Esc)"
            title="Close (Esc)"
          >
            <X size={20} />
          </button>
        </div>
      </div>

      {/* Previous button */}
      {(count > 1 || hasPrevious) && (
        <button
          onClick={goPrev}
          disabled={boundaryLoading || (!wrap && index === 0 && !hasPrevious)}
          className="absolute left-4 top-1/2 -translate-y-1/2 z-10 p-2 text-white/80 hover:text-white rounded-full hover:bg-white/10 transition-colors"
          aria-label="Previous image"
        >
          <ChevronLeft size={32} />
        </button>
      )}

      {/* Next button */}
      {(count > 1 || hasNext) && (
        <button
          onClick={goNext}
          disabled={boundaryLoading || (!wrap && index === count - 1 && !hasNext)}
          className="absolute right-4 top-1/2 -translate-y-1/2 z-10 p-2 text-white/80 hover:text-white rounded-full hover:bg-white/10 transition-colors"
          aria-label="Next image"
        >
          <ChevronRight size={32} />
        </button>
      )}

      {/* Image container */}
      <div
        ref={stageRef}
        className="relative flex h-[100dvh] w-screen items-center justify-center overflow-hidden select-none pt-[calc(4rem+env(safe-area-inset-top))] pb-[env(safe-area-inset-bottom)] sm:h-[94dvh] sm:w-[96vw] sm:pt-0 sm:pb-0"
        onClick={(e) => {
          // A pan that started on the backdrop releases there too, and must not close the viewer.
          if (e.target === e.currentTarget && !pointerMoved.current) {
            handleClose();
          }
        }}
        onWheel={handleWheel}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        onPointerCancel={handlePointerUp}
        // Cove blocks pinch zoom on the page; here the image takes the gesture instead.
        style={{ touchAction: "none" }}
      >
        {loading && !failed && !displayed && (
          <div className="absolute inset-0 flex items-center justify-center">
            <div className="w-10 h-10 border-4 border-white/30 border-t-white rounded-full animate-spin" />
          </div>
        )}
        {failed && (
          <div role="alert" className="absolute z-10 rounded bg-black/80 p-3 text-white">
            Unable to load image. Try another image.
          </div>
        )}
        {visibleImages.map((image) => (
          <img
            key={image.src}
            src={image.src}
            alt={image.title ?? ""}
            aria-hidden={image.src !== current?.src}
            data-lightbox-shown={displayed?.src === image.src ? "" : undefined}
            draggable={false}
            onClick={(e) => {
              // A pan (drag past the threshold) must not also toggle zoom when the pointer is released.
              if (pointerMoved.current) return;
              e.stopPropagation();
              if (loading) return;
              toggleZoom();
            }}
            onLoad={async (event) => {
              const element = event.currentTarget;
              try {
                await element.decode?.();
                // Ignore loads completed after another navigation or closing the viewer.
                if (element.isConnected && currentSrc.current === image.src) {
                  setDisplayed(image);
                  setFailedSrc(null);
                }
              } catch {
                if (element.isConnected && currentSrc.current === image.src) setFailedSrc(image.src);
              }
            }}
            onError={() => {
              if (currentSrc.current === image.src) setFailedSrc(image.src);
            }}
            className="max-h-full max-w-full object-contain transition-transform duration-200 ease-out"
            style={{
              transform: `scale(${zoom}) translate(${pan.x / zoom}px, ${pan.y / zoom}px)`,
              cursor: zoom > 1 ? (dragging ? "grabbing" : "grab") : "zoom-in",
              // Follow the fingers directly; the eased transition is for button and tap zoom.
              transition: dragging ? "none" : undefined,
              touchAction: "none",
              opacity: displayed?.src === image.src ? 1 : 0,
              position: displayed?.src === image.src ? "relative" : "absolute",
            }}
          />
        ))}
      </div>

      {/* The image name takes the bottom-left; rating, like and references sit on the right,
          straight on the image with a shadow for contrast instead of a panel. Phones stack like and references above compact stars so the name keeps room beside them. */}
      <div className="pointer-events-none absolute inset-x-4 bottom-[max(1rem,env(safe-area-inset-bottom))] z-20 flex items-end gap-3">
        {current?.title ? (
          <span className="pointer-events-auto min-w-0 truncate pb-1 text-sm text-white/80 [text-shadow:0_1px_2px_rgb(0_0_0/0.8)]">
            {current.title}
          </span>
        ) : null}

        {current ? (
          <div
            data-lightbox-engagement=""
            className="pointer-events-auto ml-auto flex shrink-0 flex-col items-end gap-1 text-white [filter:drop-shadow(0_1px_2px_rgb(0_0_0/0.9))] sm:flex-row sm:items-center sm:gap-2"
          >
            {/* First in the DOM so keyboard and screen-reader order matches the row on wider screens. A fixed
                height on phones stops the row above from jumping while the read-only stars show. */}
            <div data-lightbox-rating="" className="flex items-center max-sm:min-h-7">
              <InteractiveRating
                value={rating}
                onChange={setRating}
                readOnly={(loading && Boolean(displayed)) || !canEngage || ratingPending}
                compact
              />
            </div>

            <div className="flex items-center gap-1 max-sm:order-first">
              <button
                type="button"
                disabled={(loading && Boolean(displayed)) || !canLike || likeMutation.isPending}
                onClick={() => likeMutation.mutate(current.id)}
                className="inline-flex min-h-8 items-center gap-1.5 rounded-md px-2 text-white/80 transition-colors hover:bg-white/10 hover:text-white disabled:cursor-not-allowed disabled:opacity-50"
                aria-label={`Like image (${likeCount} likes)`}
                title={likeMutation.isPending ? "Saving like" : "Like image"}
              >
                <ThumbsUp className={likeCount > 0 ? "h-4 w-4 fill-current text-accent" : "h-4 w-4"} />
                <span className="text-sm tabular-nums">{likeCount}</span>
              </button>

              {referenceImage?.references && hasImageReferences(referenceImage.references) ? (
                <div data-lightbox-references="" className="flex items-center gap-1">
                  <ImageReferencePopovers
                    key={referenceImage.id}
                    imageId={referenceImage.id}
                    references={referenceImage.references}
                    onNavigate={navigateToReference}
                    buttonClassName="text-white/80 hover:bg-white/10 hover:text-white"
                  />
                </div>
              ) : null}
            </div>
          </div>
        ) : null}
      </div>
    </div>,
    document.body,
  );
}
