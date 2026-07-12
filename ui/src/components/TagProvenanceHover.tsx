import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import type { TagProvenance } from "../api/types";
import { formatDateTime } from "../utils/dateFormat";

// Hover-intent timings: the popup opens only after a deliberate pause (sweeping the cursor across a
// tag list must not flash popups) and closes shortly after the pointer settles outside both the chip
// and the popup.
const OPEN_DELAY_MS = 400;
const CLOSE_GRACE_MS = 150;

// At most one provenance popup is visible at a time; opening one hides the previous immediately.
let activePopupHide: { current: () => void } | null = null;

export function TagProvenanceHover({ provenance, sourceLabel = "Tag", children, className }: { provenance?: TagProvenance[]; sourceLabel?: string; children: ReactNode; className?: string }) {
  const wrapperRef = useRef<HTMLSpanElement>(null);
  const popupRef = useRef<HTMLSpanElement>(null);
  const [showProvenance, setShowProvenance] = useState(false);
  const [popupPosition, setPopupPosition] = useState<{ left: number; top: number }>({ left: 0, top: 0 });
  const openTimer = useRef<number | null>(null);
  const closeTimer = useRef<number | null>(null);
  const hideRef = useRef<() => void>(() => {});
  // Set for the synchronous mousedown→focus window so pointer clicks (e.g. on the "⋯" trigger) don't
  // re-open the popup via the focus handler; focus-opens are for keyboard navigation only.
  const suppressFocusOpen = useRef(false);

  const cancelOpenTimer = () => {
    if (openTimer.current != null) {
      window.clearTimeout(openTimer.current);
      openTimer.current = null;
    }
  };
  const cancelCloseTimer = () => {
    if (closeTimer.current != null) {
      window.clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
  };

  const hidePopup = () => {
    cancelOpenTimer();
    cancelCloseTimer();
    setShowProvenance(false);
    if (activePopupHide === hideRef) activePopupHide = null;
  };
  hideRef.current = hidePopup;

  const showPopup = () => {
    cancelOpenTimer();
    cancelCloseTimer();
    if (activePopupHide && activePopupHide !== hideRef) activePopupHide.current();
    activePopupHide = hideRef;
    setShowProvenance(true);
  };

  const scheduleOpen = () => {
    if (showProvenance) {
      cancelCloseTimer();
      return;
    }
    if (openTimer.current == null) {
      openTimer.current = window.setTimeout(() => {
        openTimer.current = null;
        showPopup();
      }, OPEN_DELAY_MS);
    }
  };

  // While visible, track the pointer globally instead of relying on enter/leave pairs: anywhere
  // inside the chip or the popup keeps it open (native mouseleave misfires when the pointer moves
  // onto the popup's scrollbar), anywhere else schedules the close.
  useEffect(() => {
    if (!showProvenance) return;

    const isInside = (x: number, y: number) => {
      for (const el of [wrapperRef.current, popupRef.current]) {
        const rect = el?.getBoundingClientRect();
        if (rect && x >= rect.left - 4 && x <= rect.right + 4 && y >= rect.top - 4 && y <= rect.bottom + 4) return true;
      }
      return false;
    };
    const onMove = (event: MouseEvent) => {
      if (isInside(event.clientX, event.clientY)) {
        cancelCloseTimer();
        return;
      }
      if (closeTimer.current == null) {
        closeTimer.current = window.setTimeout(() => {
          closeTimer.current = null;
          hideRef.current();
        }, CLOSE_GRACE_MS);
      }
    };

    window.addEventListener("mousemove", onMove, true);
    return () => window.removeEventListener("mousemove", onMove, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [showProvenance]);

  useEffect(() => () => {
    if (openTimer.current != null) window.clearTimeout(openTimer.current);
    if (closeTimer.current != null) window.clearTimeout(closeTimer.current);
    if (activePopupHide === hideRef) activePopupHide = null;
  }, []);

  useLayoutEffect(() => {
    if (!showProvenance || !provenance?.length) {
      return;
    }

    const updatePosition = () => {
      const rect = wrapperRef.current?.getBoundingClientRect();
      if (!rect) {
        return;
      }

      const width = 288;
      const margin = 8;
      const left = Math.min(Math.max(margin, rect.right - width), window.innerWidth - width - margin);
      const preferredTop = rect.bottom + margin;
      const top = preferredTop < window.innerHeight - margin ? preferredTop : Math.max(margin, rect.top - margin);
      setPopupPosition({ left, top });
    };

    updatePosition();
    window.addEventListener("resize", updatePosition);
    window.addEventListener("scroll", updatePosition, true);
    return () => {
      window.removeEventListener("resize", updatePosition);
      window.removeEventListener("scroll", updatePosition, true);
    };
  }, [provenance?.length, showProvenance]);

  if (!provenance?.length) {
    return <>{children}</>;
  }

  return (
    <span
      ref={wrapperRef}
      className={["relative inline-flex cursor-help", className ?? ""].filter(Boolean).join(" ")}
      onMouseEnter={scheduleOpen}
      onMouseLeave={cancelOpenTimer}
      // Any press on the chip (navigating, removing, opening the "⋯" menu) dismisses the popup so it
      // never sits on top of whatever the click opens. The pressed element takes focus right after
      // mousedown, so suppress the focus-open for that synchronous window.
      onMouseDown={() => {
        suppressFocusOpen.current = true;
        window.setTimeout(() => {
          suppressFocusOpen.current = false;
        }, 0);
        hidePopup();
      }}
      onFocus={() => {
        if (suppressFocusOpen.current) return;
        showPopup();
      }}
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          hidePopup();
        }
      }}
    >
      {children}
      <span className="sr-only"><TagProvenancePopupContent provenance={provenance} title={`${sourceLabel} Sources`} /></span>
      {showProvenance && typeof document !== "undefined" ? createPortal(
        <span
          ref={popupRef}
          className="fixed z-[200] max-h-[min(70vh,24rem)] w-72 overflow-y-auto rounded-xl border border-border bg-surface/95 p-3 text-left shadow-2xl backdrop-blur"
          style={{ left: popupPosition.left, top: popupPosition.top }}
          // Portal events bubble through the React tree to the chip wrapper, so without this a press
          // anywhere on the popup (its scrollbar included) would hit the wrapper's dismiss handler.
          onMouseDown={(event) => event.stopPropagation()}
        >
          <TagProvenancePopupContent provenance={provenance} title={`${sourceLabel} Sources`} />
        </span>,
        document.body,
      ) : null}
    </span>
  );
}

function TagProvenancePopupContent({ provenance, title }: { provenance: TagProvenance[]; title: string }) {
  return (
    <>
      <span className="mb-2 block text-[11px] font-semibold uppercase tracking-[0.16em] text-muted">{title}</span>
      <span className="flex flex-col gap-2">
        {provenance
          .slice()
          .sort((left, right) => Date.parse(right.appliedAt) - Date.parse(left.appliedAt))
          .map((entry, index) => (
            <span key={`${entry.sourceKey}-${entry.sourceRunId ?? ""}-${entry.modelKey ?? ""}-${index}`} className="block rounded-lg border border-border/70 bg-card/70 px-2.5 py-2">
              <span className="flex items-center justify-between gap-2 text-xs text-foreground">
                <span className="font-medium">{formatTagProvenanceSource(entry.sourceKey)}</span>
                {entry.confidence != null ? <span className="text-emerald-300">{formatTagConfidence(entry.confidence)}</span> : null}
              </span>
              {entry.modelKey ? <span className="mt-1 block break-all text-[11px] text-secondary">Model {entry.modelKey}</span> : null}
              {entry.sourceRunId ? <span className="mt-1 block break-all text-[11px] text-muted">Run {entry.sourceRunId}</span> : null}
              {entry.contextType && entry.contextId ? <span className="mt-1 block text-[11px] text-muted">Context {formatTagProvenanceSource(entry.contextType)} #{entry.contextId}</span> : null}
              {entry.totalDurationSec != null ? <span className="mt-1 block text-[11px] text-muted">Duration {formatTagDurationProvenance(entry)}</span> : null}
              <span className="mt-1 block text-[11px] text-muted">Applied {formatTagProvenanceDate(entry.appliedAt)}</span>
            </span>
          ))}
      </span>
    </>
  );
}

function formatTagProvenanceSource(sourceKey: string) {
  const normalized = sourceKey.trim();
  if (!normalized) {
    return "Unknown";
  }

  if (normalized.toLowerCase() === "user") {
    return "Manual";
  }

  if (normalized.startsWith("ext:")) {
    return normalized.slice(4).split(".").map(capitalizeWord).join(".");
  }

  if (normalized.startsWith("scraper:")) {
    return `Scraper: ${formatProviderIdentifier(normalized.slice("scraper:".length))}`;
  }

  if (normalized.startsWith("metadata:")) {
    return `Metadata: ${formatProviderIdentifier(normalized.slice("metadata:".length))}`;
  }

  return normalized.split(/[:._-]+/).map(capitalizeWord).join(" ");
}

function formatProviderIdentifier(value: string) {
  const trimmed = value.trim();
  if (!trimmed) {
    return "Default";
  }

  try {
    const url = new URL(trimmed);
    return url.host || trimmed;
  } catch {
    return trimmed;
  }
}

function capitalizeWord(value: string) {
  if (!value) {
    return value;
  }

  return value[0].toUpperCase() + value.slice(1);
}

function formatTagConfidence(confidence: number) {
  return `${Math.round(confidence * 100)}%`;
}

function formatTagProvenanceDate(value?: string) {
  if (!value) {
    return "Unknown";
  }

  try {
    return formatDateTime(value);
  } catch {
    return value;
  }
}

function formatTagDurationProvenance(entry: TagProvenance) {
  const duration = formatDuration(entry.totalDurationSec ?? 0);
  if (entry.hostDurationSec && entry.hostDurationSec > 0) {
    const percent = Math.round(((entry.totalDurationSec ?? 0) / entry.hostDurationSec) * 100);
    return `${duration} (${percent}%)`;
  }

  return duration;
}

function formatDuration(seconds: number): string {
  if (!seconds || seconds <= 0) return "0:00";
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  if (h > 0) return `${h}:${m.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}`;
  return `${m}:${s.toString().padStart(2, "0")}`;
}
