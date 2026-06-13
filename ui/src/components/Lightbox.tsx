import { useState, useEffect, useCallback, useRef } from "react";
import { createPortal } from "react-dom";
import {
  X,
  ChevronLeft,
  ChevronRight,
  Play,
  Pause,
  ZoomIn,
  ZoomOut,
  Maximize2,
  Minimize2,
} from "lucide-react";
import { playback } from "../api/client";
import { createPlaybackSessionId, trackInteraction } from "../utils/interactionTracking";

export interface LightboxImage {
  id: number;
  src: string;
  title?: string;
  interactionSource?: string;
  interactionMeta?: Record<string, unknown>;
}

export interface LightboxProps {
  images: LightboxImage[];
  initialIndex: number;
  open: boolean;
  onClose: () => void;
  slideshowDelay?: number;
  autoPlay?: boolean;
}

export function Lightbox({
  images,
  initialIndex,
  open,
  onClose,
  slideshowDelay = 5000,
  autoPlay = false,
}: LightboxProps) {
  const [index, setIndex] = useState(initialIndex);
  const [loading, setLoading] = useState(true);
  const [playing, setPlaying] = useState(false);
  const [currentSlideshowDelay, setCurrentSlideshowDelay] = useState(slideshowDelay);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const [dragging, setDragging] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);

  const dragStart = useRef({ x: 0, y: 0 });
  const panStart = useRef({ x: 0, y: 0 });
  const containerRef = useRef<HTMLDivElement>(null);
  const slideshowTimer = useRef<ReturnType<typeof setInterval>>(undefined);
  const trackedOpen = useRef(false);
  const lastTrackedIndex = useRef<number | null>(null);

  const count = images.length;
  const current = images[index];

  const trackCurrentImageInteraction = useCallback((kind: string, extraMeta?: Record<string, unknown>) => {
    if (!current) {
      return;
    }

    trackInteraction({
      hostType: "image",
      hostId: current.id,
      kind,
      meta: {
        source: current.interactionSource ?? "lightbox",
        ...(current.interactionMeta ?? {}),
        ...(extraMeta ?? {}),
      },
    });
  }, [current]);

  // Sync index when initialIndex or open changes
  useEffect(() => {
    if (open) {
      setIndex(initialIndex);
      setZoom(1);
      setPan({ x: 0, y: 0 });
      setPlaying(autoPlay);
      setCurrentSlideshowDelay(slideshowDelay);
      trackedOpen.current = false;
      lastTrackedIndex.current = null;
    }
  }, [autoPlay, open, initialIndex, slideshowDelay]);

  useEffect(() => {
    if (!open || !current) {
      return;
    }

    if (!trackedOpen.current) {
      trackedOpen.current = true;
      lastTrackedIndex.current = index;
      trackCurrentImageInteraction("openLightbox", { index: index + 1, count });
      return;
    }

    if (lastTrackedIndex.current !== null && lastTrackedIndex.current !== index) {
      trackCurrentImageInteraction("navigate", {
        fromIndex: lastTrackedIndex.current + 1,
        toIndex: index + 1,
        count,
      });
      lastTrackedIndex.current = index;
    }
  }, [count, current, index, open, trackCurrentImageInteraction]);

  useEffect(() => {
    if (!open || !current) {
      return;
    }

    const imageId = current.id;
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
      void playback.recordIntervals({
        hostType: "image",
        hostId: imageId,
        sessionId,
        mediaDurationSec: durationSec,
        currentPositionSec: durationSec,
        state,
        surface: "lightbox",
        scopeKey: `image:${imageId}:lightbox`,
        context: {
          index: index + 1,
          count,
          source: current.interactionSource ?? "lightbox",
          ...(current.interactionMeta ?? {}),
        },
        intervals: [{ startSec: 0, endSec: durationSec }],
      }).catch(() => {});
    };

    const handlePageHide = () => flushDwell("abandoned");
    window.addEventListener("pagehide", handlePageHide);
    return () => {
      window.removeEventListener("pagehide", handlePageHide);
      flushDwell("ended");
    };
  }, [count, current?.id, index, open]);

  // Lock body scroll
  useEffect(() => {
    if (!open) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = prev;
    };
  }, [open]);

  useEffect(() => {
    if (!open) {
      setFullscreen(false);
      return;
    }

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
  }, []);

  const goTo = useCallback(
    (next: number) => {
      setIndex(((next % count) + count) % count);
      setLoading(true);
      resetView();
    },
    [count, resetView],
  );

  const goPrev = useCallback(() => goTo(index - 1), [goTo, index]);
  const goNext = useCallback(() => goTo(index + 1), [goTo, index]);

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
    const nextZoom = Math.min(zoom + 0.5, 5);
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

  const changeSlideshowDelay = useCallback((deltaMs: number) => {
    setCurrentSlideshowDelay((current) => {
      const next = Math.min(30000, Math.max(1000, current + deltaMs));
      if (next !== current) {
        trackCurrentImageInteraction("slideshowDelay", { milliseconds: next });
      }
      return next;
    });
  }, [trackCurrentImageInteraction]);

  const handleClose = useCallback(() => {
    if (open) {
      trackCurrentImageInteraction("closeLightbox", { index: index + 1, count });
    }

    if (document.fullscreenElement === containerRef.current) {
      void document.exitFullscreen();
    }

    onClose();
  }, [count, index, onClose, open, trackCurrentImageInteraction]);

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
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      switch (e.key) {
        case "ArrowLeft":
          e.preventDefault();
          goPrev();
          break;
        case "ArrowRight":
          e.preventDefault();
          goNext();
          break;
        case "Escape":
          e.preventDefault();
          handleClose();
          break;
        case " ":
          e.preventDefault();
          toggleSlideshow();
          break;
        case "f":
        case "F":
          e.preventDefault();
          void toggleFullscreen();
          break;
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [goNext, goPrev, handleClose, open, toggleFullscreen, toggleSlideshow]);

  // Scroll wheel zoom
  const handleWheel = useCallback(
    (e: React.WheelEvent) => {
      e.preventDefault();
      const delta = e.deltaY > 0 ? -0.25 : 0.25;
      setZoom((z) => {
        const next = Math.min(Math.max(z + delta, 1), 5);
        if (next === 1) setPan({ x: 0, y: 0 });
        return next;
      });
    },
    [],
  );

  // Pan handlers
  const handlePointerDown = useCallback(
    (e: React.PointerEvent) => {
      if (zoom <= 1) return;
      setDragging(true);
      dragStart.current = { x: e.clientX, y: e.clientY };
      panStart.current = { ...pan };
      (e.target as HTMLElement).setPointerCapture(e.pointerId);
    },
    [zoom, pan],
  );

  const handlePointerMove = useCallback(
    (e: React.PointerEvent) => {
      if (!dragging) return;
      setPan({
        x: panStart.current.x + (e.clientX - dragStart.current.x),
        y: panStart.current.y + (e.clientY - dragStart.current.y),
      });
    },
    [dragging],
  );

  const handlePointerUp = useCallback(() => {
    setDragging(false);
  }, []);

  // Preload adjacent images
  useEffect(() => {
    if (!open || count <= 1) return;
    const preload = (i: number) => {
      const img = new Image();
      img.src = images[((i % count) + count) % count].src;
    };
    preload(index + 1);
    preload(index - 1);
  }, [open, index, images, count]);

  if (!open) return <></>;

  return createPortal(
    <div
      ref={containerRef}
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/90"
      onClick={(e) => {
        if (e.target === containerRef.current) handleClose();
      }}
    >
      {/* Top bar */}
      <div className="absolute top-0 left-0 right-0 z-10 flex items-center justify-between p-4 pt-[max(1rem,env(safe-area-inset-top))] bg-gradient-to-b from-black/80 via-black/40 to-transparent">
        <span className="text-white text-sm font-medium select-none">
          {index + 1} / {count}
          {current?.title && (
            <span className="ml-3 text-white/70">{current.title}</span>
          )}
        </span>
        <div className="flex items-center gap-2">
          {count > 1 ? (
            <div className="mr-1 flex items-center gap-1 rounded-lg border border-white/10 bg-white/5 px-2 py-1 text-xs text-white/80">
              <button
                onClick={() => changeSlideshowDelay(-1000)}
                className="rounded px-1 py-0.5 text-white/80 transition-colors hover:bg-white/10 hover:text-white"
                aria-label="Decrease slideshow delay"
                title="Decrease slideshow delay"
              >
                -
              </button>
              <span className="min-w-[3.5rem] text-center tabular-nums">{(currentSlideshowDelay / 1000).toFixed(0)}s</span>
              <button
                onClick={() => changeSlideshowDelay(1000)}
                className="rounded px-1 py-0.5 text-white/80 transition-colors hover:bg-white/10 hover:text-white"
                aria-label="Increase slideshow delay"
                title="Increase slideshow delay"
              >
                +
              </button>
            </div>
          ) : null}
          <button
            onClick={handleZoomOut}
            className="p-2 text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
            aria-label="Zoom out"
          >
            <ZoomOut size={20} />
          </button>
          <button
            onClick={handleZoomIn}
            className="p-2 text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
            aria-label="Zoom in"
          >
            <ZoomIn size={20} />
          </button>
          <button
            onClick={() => resetView()}
            className="p-2 text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
            aria-label="Reset zoom"
            title="Reset zoom"
          >
            <ZoomOut size={20} />
          </button>
          <button
            onClick={() => void toggleFullscreen()}
            className="p-2 text-white/80 hover:text-white rounded-lg hover:bg-white/10 transition-colors"
            aria-label={fullscreen ? "Exit full screen" : "Enter full screen"}
            title={fullscreen ? "Exit full screen" : "Enter full screen"}
          >
            {fullscreen ? <Minimize2 size={20} /> : <Maximize2 size={20} />}
          </button>
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
      {count > 1 && (
        <button
          onClick={goPrev}
          className="absolute left-4 top-1/2 -translate-y-1/2 z-10 p-2 text-white/80 hover:text-white rounded-full hover:bg-white/10 transition-colors"
          aria-label="Previous image"
        >
          <ChevronLeft size={32} />
        </button>
      )}

      {/* Next button */}
      {count > 1 && (
        <button
          onClick={goNext}
          className="absolute right-4 top-1/2 -translate-y-1/2 z-10 p-2 text-white/80 hover:text-white rounded-full hover:bg-white/10 transition-colors"
          aria-label="Next image"
        >
          <ChevronRight size={32} />
        </button>
      )}

      {/* Image container */}
      <div
        className="relative flex h-[100dvh] w-screen items-center justify-center overflow-hidden select-none pt-[calc(4rem+env(safe-area-inset-top))] pb-[env(safe-area-inset-bottom)] sm:h-[94dvh] sm:w-[96vw] sm:pt-0 sm:pb-0"
        onClick={(e) => {
          if (e.target === e.currentTarget) {
            handleClose();
          }
        }}
        onWheel={handleWheel}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
      >
        {loading && (
          <div className="absolute inset-0 flex items-center justify-center">
            <div className="w-10 h-10 border-4 border-white/30 border-t-white rounded-full animate-spin" />
          </div>
        )}
        <img
          key={current?.id}
          src={current?.src}
          alt={current?.title ?? ""}
          draggable={false}
          onClick={(e) => {
            if (!dragging) {
              e.stopPropagation();
              toggleZoom();
            }
          }}
          onLoad={() => setLoading(false)}
          className="max-h-full max-w-full object-contain transition-transform duration-200 ease-out"
          style={{
            transform: `scale(${zoom}) translate(${pan.x / zoom}px, ${pan.y / zoom}px)`,
            cursor: zoom > 1 ? (dragging ? "grabbing" : "grab") : "zoom-in",
            opacity: loading ? 0 : 1,
          }}
        />
      </div>
    </div>,
    document.body,
  );
}
