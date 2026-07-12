import { useLayoutEffect, useState, type HTMLAttributes, type ReactNode, type RefObject } from "react";
import { createPortal } from "react-dom";

interface Props extends Omit<HTMLAttributes<HTMLDivElement>, "children" | "className"> {
  anchorRef: RefObject<HTMLElement | null>;
  containerRef?: RefObject<HTMLDivElement | null>;
  children: ReactNode;
  className?: string;
  maxHeight?: number;
}

interface Position {
  left: number;
  top: number;
  width: number;
  maxHeight: number;
  placement: "above" | "below";
}

export function AutocompleteDropdown({ anchorRef, containerRef, children, className, maxHeight = 160, ...containerProps }: Props) {
  const [position, setPosition] = useState<Position | null>(null);

  useLayoutEffect(() => {
    const place = () => {
      const anchor = anchorRef.current;
      if (!anchor) return;

      const rect = anchor.getBoundingClientRect();
      const viewport = window.visualViewport;
      const visualTop = viewport?.pageTop ?? window.scrollY;
      const viewportHeight = viewport?.height ?? window.innerHeight;
      const anchorLeft = rect.left + window.scrollX;
      const anchorTop = rect.top + window.scrollY;
      const anchorBottom = rect.bottom + window.scrollY;
      const gap = 4;
      const spaceBelow = visualTop + viewportHeight - anchorBottom - gap;
      const spaceAbove = anchorTop - visualTop - gap;
      const openAbove = spaceBelow < maxHeight && spaceAbove > spaceBelow;
      const availableHeight = Math.min(maxHeight, Math.max(0, openAbove ? spaceAbove : spaceBelow));

      setPosition({
        left: anchorLeft,
        top: openAbove ? anchorTop - gap : anchorBottom + gap,
        width: rect.width,
        maxHeight: availableHeight,
        placement: openAbove ? "above" : "below",
      });
    };

    place();
    const viewport = window.visualViewport;
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    viewport?.addEventListener("resize", place);
    viewport?.addEventListener("scroll", place);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
      viewport?.removeEventListener("resize", place);
      viewport?.removeEventListener("scroll", place);
    };
  }, [anchorRef, maxHeight]);

  if (typeof document === "undefined" || !position) return null;

  return createPortal(
    <div
      ref={containerRef}
      {...containerProps}
      className={`absolute z-[200] overflow-x-hidden overflow-y-auto shadow-lg ${className ?? ""}`}
      style={{
        left: position.left,
        top: position.top,
        width: position.width,
        maxHeight: position.maxHeight,
        transform: position.placement === "above" ? "translateY(-100%)" : undefined,
      }}
    >
      {children}
    </div>,
    document.body,
  );
}
