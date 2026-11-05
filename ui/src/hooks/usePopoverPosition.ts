import { type CSSProperties, type RefObject, useCallback, useLayoutEffect, useState } from "react";

export type PopoverAlign = "start" | "end";

export interface PopoverRect {
  top: number;
  left: number;
  right: number;
  bottom: number;
}

export interface PopoverPositionInput {
  anchor: PopoverRect;
  /** The popover's natural size, before any height cap is applied. */
  popover: { width: number; height: number };
  viewport: { width: number; height: number };
  /** Which anchor edge the popover lines up with first: "end" grows leftward from the anchor's right edge. */
  align?: PopoverAlign;
  /** Gap between the anchor and the popover. */
  offset?: number;
  /** Minimum distance kept from every viewport edge. */
  padding?: number;
}

export interface PopoverPosition {
  top: number;
  left: number;
  maxHeight: number;
  maxWidth: number;
  side: "below" | "above";
  align: PopoverAlign | "clamped";
}

/**
 * Places a popover next to its anchor so it stays inside the viewport: below the anchor unless only
 * the space above fits it, lined up with the preferred anchor edge unless only the other edge fits,
 * and otherwise slid inward until it clears the edge.
 */
export function computePopoverPosition({
  anchor,
  popover,
  viewport,
  align = "end",
  offset = 4,
  padding = 8,
}: PopoverPositionInput): PopoverPosition {
  const maxWidth = Math.max(0, viewport.width - padding * 2);
  const width = Math.min(popover.width, maxWidth);

  const spaceBelow = viewport.height - padding - (anchor.bottom + offset);
  const spaceAbove = anchor.top - offset - padding;
  const side = popover.height <= spaceBelow || spaceBelow >= spaceAbove ? "below" : "above";
  const maxHeight = Math.max(0, side === "below" ? spaceBelow : spaceAbove);
  const height = Math.min(popover.height, maxHeight);
  const top = side === "below" ? anchor.bottom + offset : anchor.top - offset - height;

  const endLeft = anchor.right - width;
  const startLeft = anchor.left;
  const fitsEnd = endLeft >= padding && endLeft + width <= viewport.width - padding;
  const fitsStart = startLeft >= padding && startLeft + width <= viewport.width - padding;
  const [preferredLeft, fitsPreferred, otherLeft, fitsOther, otherAlign] =
    align === "end"
      ? [endLeft, fitsEnd, startLeft, fitsStart, "start" as const]
      : [startLeft, fitsStart, endLeft, fitsEnd, "end" as const];

  if (fitsPreferred) return { top, left: preferredLeft, maxHeight, maxWidth, side, align };
  if (fitsOther) return { top, left: otherLeft, maxHeight, maxWidth, side, align: otherAlign };

  const clampedLeft = Math.min(Math.max(preferredLeft, padding), Math.max(padding, viewport.width - padding - width));
  return { top, left: clampedLeft, maxHeight, maxWidth, side, align: "clamped" };
}

function readViewport() {
  // clientWidth excludes a classic scrollbar, which a fixed popover cannot cover anyway; jsdom reports 0.
  const root = document.documentElement;
  return {
    width: root.clientWidth || window.innerWidth,
    height: root.clientHeight || window.innerHeight,
  };
}

/** Full rendered size, including content currently hidden behind a max-height cap. */
function readNaturalSize(element: HTMLElement) {
  const borderY = element.offsetHeight - element.clientHeight;
  return {
    width: element.offsetWidth,
    height: Math.max(element.offsetHeight, element.scrollHeight + borderY),
  };
}

interface UsePopoverPositionOptions {
  align?: PopoverAlign;
  offset?: number;
  padding?: number;
}

/**
 * Positions a `position: fixed` (or viewport-sized-container) popover against its anchor while it is
 * open. The popover stays invisible until it has been measured once, so it never paints in the wrong place,
 * and it re-places itself on scroll, resize, and changes to its own or the anchor's size.
 */
export function usePopoverPosition(
  open: boolean,
  anchorRef: RefObject<HTMLElement | null>,
  popoverRef: RefObject<HTMLElement | null>,
  { align = "end", offset = 4, padding = 8 }: UsePopoverPositionOptions = {},
): { style: CSSProperties } {
  const [position, setPosition] = useState<PopoverPosition | null>(null);
  // Forget the last placement on close so a reopened popover is measured again before it shows.
  const [prevOpen, setPrevOpen] = useState(open);
  if (prevOpen !== open) {
    setPrevOpen(open);
    if (!open) setPosition(null);
  }

  const update = useCallback(() => {
    const anchor = anchorRef.current;
    const popover = popoverRef.current;
    if (!anchor || !popover) return;
    const next = computePopoverPosition({
      anchor: anchor.getBoundingClientRect(),
      popover: readNaturalSize(popover),
      viewport: readViewport(),
      align,
      offset,
      padding,
    });
    setPosition((current) =>
      current &&
      current.top === next.top &&
      current.left === next.left &&
      current.maxHeight === next.maxHeight &&
      current.maxWidth === next.maxWidth
        ? current
        : next,
    );
  }, [anchorRef, popoverRef, align, offset, padding]);

  useLayoutEffect(() => {
    if (!open) return;

    update();
    window.addEventListener("resize", update);
    window.addEventListener("scroll", update, true);
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(() => update());
    if (popoverRef.current) observer?.observe(popoverRef.current);
    if (anchorRef.current) observer?.observe(anchorRef.current);
    return () => {
      window.removeEventListener("resize", update);
      window.removeEventListener("scroll", update, true);
      observer?.disconnect();
    };
  }, [open, update, anchorRef, popoverRef]);

  const style: CSSProperties = position
    ? { top: position.top, left: position.left, maxHeight: position.maxHeight, maxWidth: position.maxWidth }
    : // Unmeasured: invisible but still focusable (a visibility:hidden ancestor would swallow focus() from a
      // child's mount effect), and width-capped so the first measurement matches the placed layout.
      { top: 0, left: 0, maxWidth: `calc(100vw - ${padding * 2}px)`, opacity: 0, pointerEvents: "none" };

  return { style };
}
