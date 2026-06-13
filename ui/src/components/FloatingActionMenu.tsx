import { type CSSProperties, type ReactNode, type RefObject, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

interface FloatingActionMenuProps {
  open: boolean;
  anchorRef: RefObject<HTMLElement | null>;
  onClose: () => void;
  children: ReactNode;
  className?: string;
  offset?: number;
  role?: string;
}

export function FloatingActionMenu({
  open,
  anchorRef,
  onClose,
  children,
  className = "min-w-[220px] py-1",
  offset = 4,
  role = "menu",
}: FloatingActionMenuProps) {
  const [position, setPosition] = useState<CSSProperties | null>(null);
  const backdropPointerDownRef = useRef(false);

  useEffect(() => {
    if (!open) {
      setPosition(null);
      return;
    }

    const updatePosition = () => {
      const anchor = anchorRef.current;
      if (!anchor) return;

      const rect = anchor.getBoundingClientRect();
      const viewportWidth = window.innerWidth;
      const gutter = 8;
      setPosition({
        top: rect.bottom + offset,
        right: Math.max(gutter, viewportWidth - rect.right),
        maxWidth: `calc(100vw - ${gutter * 2}px)`,
      });
    };

    updatePosition();
    window.addEventListener("resize", updatePosition);
    window.addEventListener("scroll", updatePosition, true);
    return () => {
      window.removeEventListener("resize", updatePosition);
      window.removeEventListener("scroll", updatePosition, true);
    };
  }, [anchorRef, offset, open]);

  if (!open || typeof document === "undefined") return null;

  return createPortal(
    <div
      className="fixed inset-0 z-50 flex items-end sm:items-center justify-center bg-black/60"
      onMouseDown={(event) => {
        backdropPointerDownRef.current = event.target === event.currentTarget;
      }}
      onClick={(event) => {
        if (event.target === event.currentTarget && backdropPointerDownRef.current) {
          onClose();
        }

        backdropPointerDownRef.current = false;
      }}
    >
      {position ? (
        <div
          className={["absolute z-[60] overflow-hidden rounded-lg border border-border bg-surface shadow-xl", className].filter(Boolean).join(" ")}
          style={position}
          role={role}
          // Stop pointerdown/mousedown from reaching document-level "click outside" handlers on the
          // host page. Some pages close their ops menu on `pointerdown`; since this menu is portaled
          // outside their ref, an un-stopped pointerdown would close (unmount) the menu before the
          // item's onClick fires — making the item appear to do nothing.
          onPointerDown={(event) => event.stopPropagation()}
          onMouseDown={(event) => event.stopPropagation()}
          onClick={(event) => event.stopPropagation()}
        >
          {children}
        </div>
      ) : null}
    </div>,
    document.body,
  );
}