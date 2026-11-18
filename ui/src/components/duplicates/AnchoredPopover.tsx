import {
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type ReactNode,
  type RefObject,
} from "react";
import { createPortal } from "react-dom";

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select, textarea, [tabindex]:not([tabindex="-1"])';

interface PanelPosition {
  top?: number;
  bottom?: number;
  left: number;
  width: number;
  maxHeight: number;
}

/**
 * A menu panel rendered into document.body and pinned below its anchor, or above it where there is more room there.
 * Rendering outside the sticky toolbar matters for the glass theme: a backdrop-filtered panel nested inside another
 * backdrop-filtered element cannot blur the page behind it, so the content underneath would show straight through.
 *
 * Given a `label`, the panel is a dialog that sits in the tab order right after its anchor, although it is rendered at
 * the end of document.body: it takes focus when it opens, Tab past its last control or Shift+Tab before its first
 * hands focus back to the anchor and closes it, as does Escape, and it closes when focus moves anywhere else.
 */
export function AnchoredPopover({
  anchorRef,
  open,
  onClose,
  children,
  width = 320,
  align = "end",
  label,
}: {
  anchorRef: RefObject<HTMLElement | null>;
  open: boolean;
  onClose: () => void;
  children: ReactNode;
  width?: number;
  /** Which edge of the anchor the panel lines up with. */
  align?: "start" | "end";
  label?: string;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState<PanelPosition | null>(null);
  const isDialog = label != null;

  useLayoutEffect(() => {
    if (!open) return;
    const place = (event?: Event) => {
      // Scrolling the panel's own list leaves it where it is.
      if (event?.target instanceof Node && panelRef.current?.contains(event.target)) return;
      const rect = anchorRef.current?.getBoundingClientRect();
      if (!rect) return;
      // Never wider than the screen, less a margin on each side.
      const panelWidth = Math.min(width, window.innerWidth - 16);
      const preferred = align === "start" ? rect.left : rect.right - panelWidth;
      const left = Math.max(8, Math.min(window.innerWidth - panelWidth - 8, preferred));
      const below = window.innerHeight - rect.bottom - 12;
      const above = rect.top - 12;
      setPosition(
        below >= 240 || below >= above
          ? { top: rect.bottom + 4, left, width: panelWidth, maxHeight: below }
          : { bottom: window.innerHeight - rect.top + 4, left, width: panelWidth, maxHeight: above },
      );
    };
    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [align, anchorRef, open, width]);

  const placed = open && position !== null;
  useEffect(() => {
    if (placed && isDialog) panelRef.current?.focus({ preventScroll: true });
  }, [isDialog, placed]);

  useEffect(() => {
    if (!open) return;
    const inside = (target: EventTarget | null) =>
      target instanceof Node && (!!panelRef.current?.contains(target) || !!anchorRef.current?.contains(target));
    const handlePointer = (event: MouseEvent) => {
      if (!inside(event.target)) onClose();
    };
    const handleFocus = (event: FocusEvent) => {
      if (isDialog && !inside(event.target)) onClose();
    };
    const handleKey = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      if (isDialog) anchorRef.current?.focus();
      onClose();
    };
    document.addEventListener("mousedown", handlePointer);
    document.addEventListener("keydown", handleKey);
    document.addEventListener("focusin", handleFocus);
    return () => {
      document.removeEventListener("mousedown", handlePointer);
      document.removeEventListener("keydown", handleKey);
      document.removeEventListener("focusin", handleFocus);
    };
  }, [anchorRef, isDialog, onClose, open]);

  // Tab out of either end of the dialog returns to its anchor, so the next Tab carries on from there in the page.
  const handleTab = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "Tab" || !panelRef.current) return;
    const focusable = Array.from(panelRef.current.querySelectorAll<HTMLElement>(FOCUSABLE));
    const active = document.activeElement;
    const leaving = event.shiftKey
      ? active === panelRef.current || active === focusable[0]
      : focusable.length === 0 || active === focusable[focusable.length - 1];
    if (!leaving) return;
    event.preventDefault();
    anchorRef.current?.focus();
    onClose();
  };

  if (!open || !position) return null;
  return createPortal(
    <div
      ref={panelRef}
      role={isDialog ? "dialog" : undefined}
      aria-label={label}
      tabIndex={isDialog ? -1 : undefined}
      onKeyDown={isDialog ? handleTab : undefined}
      className="styled-dropdown-panel fixed z-50 rounded-lg border border-border bg-surface shadow-xl outline-none"
      style={{
        top: position.top,
        bottom: position.bottom,
        left: position.left,
        width: position.width,
        maxHeight: position.maxHeight,
        // Inline, because the panel class hides overflow outside the utility layer.
        overflowX: "hidden",
        overflowY: "auto",
      }}
    >
      {children}
    </div>,
    document.body,
  );
}
