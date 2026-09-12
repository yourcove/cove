import { useEffect, useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import { createPortal } from "react-dom";

/**
 * A menu panel rendered into document.body and pinned below its anchor. Rendering outside the sticky
 * toolbar matters for the glass theme: a backdrop-filtered panel nested inside another backdrop-filtered
 * element cannot blur the page behind it, so the content underneath would show straight through.
 */
export function AnchoredPopover({
  anchorRef,
  open,
  onClose,
  children,
  width = 320,
}: {
  anchorRef: RefObject<HTMLElement | null>;
  open: boolean;
  onClose: () => void;
  children: ReactNode;
  width?: number;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState<{ top: number; left: number } | null>(null);

  useLayoutEffect(() => {
    if (!open) return;
    const place = () => {
      const rect = anchorRef.current?.getBoundingClientRect();
      if (!rect) return;
      const left = Math.max(8, Math.min(window.innerWidth - width - 8, rect.right - width));
      setPosition({ top: rect.bottom + 4, left });
    };
    place();
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [anchorRef, open, width]);

  useEffect(() => {
    if (!open) return;
    const handlePointer = (event: MouseEvent) => {
      const target = event.target as Node;
      if (panelRef.current?.contains(target) || anchorRef.current?.contains(target)) return;
      onClose();
    };
    const handleKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    document.addEventListener("mousedown", handlePointer);
    document.addEventListener("keydown", handleKey);
    return () => {
      document.removeEventListener("mousedown", handlePointer);
      document.removeEventListener("keydown", handleKey);
    };
  }, [anchorRef, onClose, open]);

  if (!open || !position) return null;
  return createPortal(
    <div
      ref={panelRef}
      className="styled-dropdown-panel fixed z-50 overflow-hidden rounded-lg border border-border bg-surface shadow-xl"
      style={{ top: position.top, left: position.left, width }}
    >
      {children}
    </div>,
    document.body,
  );
}
