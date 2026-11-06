import { type ReactNode, type RefObject, useRef } from "react";
import { createPortal } from "react-dom";
import { type PopoverAlign, usePopoverPosition } from "../hooks/usePopoverPosition";

interface FloatingActionMenuProps {
  open: boolean;
  anchorRef: RefObject<HTMLElement | null>;
  onClose: () => void;
  children: ReactNode;
  className?: string;
  offset?: number;
  /** Anchor edge the menu lines up with first; it switches edges, then clamps, when that would leave the viewport. */
  align?: PopoverAlign;
  role?: string;
}

export function FloatingActionMenu({
  open,
  anchorRef,
  onClose,
  children,
  className = "min-w-[220px] py-1",
  offset = 4,
  align = "end",
  role = "menu",
}: FloatingActionMenuProps) {
  const menuRef = useRef<HTMLDivElement>(null);
  const backdropPointerDownRef = useRef(false);
  const { style } = usePopoverPosition(open, anchorRef, menuRef, { align, offset });

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
      <div
        ref={menuRef}
        className={[
          "absolute z-[60] overflow-x-hidden overflow-y-auto rounded-lg border border-border bg-surface shadow-xl",
          className,
        ]
          .filter(Boolean)
          .join(" ")}
        style={style}
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
    </div>,
    document.body,
  );
}
