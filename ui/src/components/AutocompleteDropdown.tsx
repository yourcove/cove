import { useLayoutEffect, useState, type HTMLAttributes, type ReactNode, type RefObject } from "react";
import { createPortal } from "react-dom";

interface Props extends Omit<HTMLAttributes<HTMLDivElement>, "children" | "className"> {
  anchorRef: RefObject<HTMLElement | null>;
  containerRef?: RefObject<HTMLDivElement | null>;
  children: ReactNode;
  className?: string;
  maxHeight?: number;
  /** Optional portal root. Defaults to the anchor's active fullscreen ancestor, then document.body. */
  portalContainer?: HTMLElement | null;
}

interface Position {
  left: number;
  top: number;
  width: number;
  maxHeight: number;
  placement: "above" | "below";
}

interface Layout {
  portalContainer: HTMLElement;
  position: Position;
}

interface ContainingBlockLease {
  count: number;
  previousInlinePosition: string;
}

const containingBlockLeases = new WeakMap<HTMLElement, ContainingBlockLease>();

function acquireContainingBlock(target: HTMLElement) {
  if (target === document.body || target === document.documentElement) return () => {};

  const existing = containingBlockLeases.get(target);
  if (existing) {
    existing.count += 1;
  } else {
    if (window.getComputedStyle(target).position !== "static") return () => {};
    containingBlockLeases.set(target, {
      count: 1,
      previousInlinePosition: target.style.position,
    });
    target.style.position = "relative";
  }

  let released = false;
  return () => {
    if (released) return;
    released = true;
    const lease = containingBlockLeases.get(target);
    if (!lease) return;
    lease.count -= 1;
    if (lease.count > 0) return;
    containingBlockLeases.delete(target);
    if (target.style.position === "relative") {
      target.style.position = lease.previousInlinePosition;
    }
  };
}

export function AutocompleteDropdown({
  anchorRef,
  containerRef,
  children,
  className,
  maxHeight = 160,
  portalContainer,
  ...containerProps
}: Props) {
  const [layout, setLayout] = useState<Layout | null>(null);

  useLayoutEffect(() => {
    let positionedTarget: HTMLElement | null = null;
    let releaseContainingBlock = () => {};

    const restoreTargetPosition = () => {
      releaseContainingBlock();
      releaseContainingBlock = () => {};
      positionedTarget = null;
    };

    const establishContainingBlock = (target: HTMLElement) => {
      if (positionedTarget === target) return;
      restoreTargetPosition();
      positionedTarget = target;
      releaseContainingBlock = acquireContainingBlock(target);
    };

    const place = () => {
      const anchor = anchorRef.current;
      if (!anchor) {
        setLayout(null);
        return;
      }

      const rect = anchor.getBoundingClientRect();
      const fullscreenElement = document.fullscreenElement;
      const automaticFullscreenContainer =
        fullscreenElement instanceof HTMLElement && fullscreenElement.contains(anchor) ? fullscreenElement : null;
      const target = portalContainer ?? automaticFullscreenContainer ?? document.body;
      establishContainingBlock(target);
      const gap = 4;
      const useDocumentCoordinates = target === document.body || target === document.documentElement;
      const viewport = window.visualViewport;
      const targetRect = useDocumentCoordinates ? null : target.getBoundingClientRect();
      const boundaryTop = targetRect ? targetRect.top + target.clientTop : 0;
      const targetClientHeight = targetRect
        ? target.clientHeight || Math.max(0, targetRect.height - target.clientTop)
        : 0;
      const boundaryBottom = targetRect ? boundaryTop + targetClientHeight : (viewport?.height ?? window.innerHeight);
      const coordinateOffsetLeft = useDocumentCoordinates
        ? window.scrollX
        : target.scrollLeft - (targetRect?.left ?? 0) - target.clientLeft;
      const coordinateOffsetTop = useDocumentCoordinates
        ? window.scrollY
        : target.scrollTop - (targetRect?.top ?? 0) - target.clientTop;
      const visualTop = useDocumentCoordinates ? (viewport?.pageTop ?? window.scrollY) : boundaryTop;
      const visualBottom = useDocumentCoordinates
        ? visualTop + (viewport?.height ?? window.innerHeight)
        : boundaryBottom;
      const anchorLeft = rect.left + coordinateOffsetLeft;
      const anchorTop = rect.top + coordinateOffsetTop;
      const anchorBottom = rect.bottom + coordinateOffsetTop;
      const spaceBelow = visualBottom - (useDocumentCoordinates ? anchorBottom : rect.bottom) - gap;
      const spaceAbove = (useDocumentCoordinates ? anchorTop : rect.top) - visualTop - gap;
      const openAbove = spaceBelow < maxHeight && spaceAbove > spaceBelow;
      const availableHeight = Math.min(maxHeight, Math.max(0, openAbove ? spaceAbove : spaceBelow));

      setLayout({
        portalContainer: target,
        position: {
          left: anchorLeft,
          top: openAbove ? anchorTop - gap : anchorBottom + gap,
          width: rect.width,
          maxHeight: availableHeight,
          placement: openAbove ? "above" : "below",
        },
      });
    };

    place();
    const viewport = window.visualViewport;
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    document.addEventListener("fullscreenchange", place);
    viewport?.addEventListener("resize", place);
    viewport?.addEventListener("scroll", place);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
      document.removeEventListener("fullscreenchange", place);
      viewport?.removeEventListener("resize", place);
      viewport?.removeEventListener("scroll", place);
      restoreTargetPosition();
    };
  }, [anchorRef, maxHeight, portalContainer]);

  if (typeof document === "undefined" || !layout) return null;

  const { portalContainer: target, position } = layout;

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
    target,
  );
}
