import { ReactNode, RefObject, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer, useWindowVirtualizer, Virtualizer } from "@tanstack/react-virtual";

/**
 * Shared Reddit-style virtualized infinite list.
 *
 * Single component, two layouts (single-column / grid), works against either
 * the browser window or an inner scroll container. All loaded items remain in
 * the data store; the DOM only renders the visible window + a small overscan
 * buffer. Spacers above/below preserve scrollbar accuracy.
 */

export interface VirtualizedRenderArg<TItem> {
  item: TItem;
  index: number;
  /** Pass to the *outer* element you render so dynamic height measurement works. */
  measureRef: (node: HTMLElement | null) => void;
  /** True when this item is inside the active near-viewport window. Useful for media warm-up. */
  isActive: boolean;
}

export interface VirtualizedInfiniteListBaseProps<TItem> {
  items: TItem[];
  getItemKey: (item: TItem, index: number) => string | number;
  /** Initial size estimate per item (for single-column) or per row (for grid). Real size is measured on mount. */
  estimateSize: number;
  hasNextPage: boolean;
  isFetchingNextPage: boolean;
  loadMore: () => void;
  /** Number of off-screen items to keep mounted on each side. Default 3. */
  overscan?: number;
  /** Class for the outer wrapper element. */
  className?: string;
  style?: React.CSSProperties;
  /** Optional render at the very end (after loaded items, before the next sentinel). */
  endContent?: ReactNode;
  /** Number of items ahead of the last rendered one at which to call loadMore. Default = overscan + 1. */
  loadMoreThreshold?: number;
  /** Called when the most visible item (or row for grid) changes. */
  onActiveIndexChange?: (index: number | null) => void;
  /** Disable virtualizer scroll offset corrections when dynamic item measurements change. */
  adjustScrollOnItemSizeChange?: boolean;
}

interface SingleColumnProps<TItem> extends VirtualizedInfiniteListBaseProps<TItem> {
  layout?: "single-column";
  renderItem: (arg: VirtualizedRenderArg<TItem>) => ReactNode;
  /** Optional class applied to each row wrapper. */
  itemClassName?: string;
}

interface GridProps<TItem> extends VirtualizedInfiniteListBaseProps<TItem> {
  layout: "grid";
  minColumnWidth: number;
  gap?: number;
  renderItem: (arg: VirtualizedRenderArg<TItem>) => ReactNode;
}

export type VirtualizedInfiniteListProps<TItem> =
  | (SingleColumnProps<TItem> & { scrollElementRef?: RefObject<HTMLElement | null> })
  | (GridProps<TItem> & { scrollElementRef?: RefObject<HTMLElement | null> });

export function VirtualizedInfiniteList<TItem>(props: VirtualizedInfiniteListProps<TItem>) {
  const useContainerScroll = Boolean(props.scrollElementRef);
  if (props.layout === "grid") {
    return useContainerScroll ? (
      <GridContainerScroll {...(props as GridProps<TItem> & { scrollElementRef: RefObject<HTMLElement | null> })} />
    ) : (
      <GridWindowScroll {...(props as GridProps<TItem>)} />
    );
  }
  return useContainerScroll ? (
    <SingleColumnContainerScroll
      {...(props as SingleColumnProps<TItem> & { scrollElementRef: RefObject<HTMLElement | null> })}
    />
  ) : (
    <SingleColumnWindowScroll {...(props as SingleColumnProps<TItem>)} />
  );
}

// ---------- Single-column, window scroll ----------

function SingleColumnWindowScroll<TItem>(props: SingleColumnProps<TItem>) {
  const parentRef = useRef<HTMLDivElement | null>(null);
  const [parentOffsetTop, setParentOffsetTop] = useState(0);

  useLayoutEffect(() => {
    if (parentRef.current) {
      setParentOffsetTop(parentRef.current.getBoundingClientRect().top + window.scrollY);
    }
  }, []);

  const virtualizer = useWindowVirtualizer({
    count: props.items.length,
    estimateSize: () => props.estimateSize,
    overscan: props.overscan ?? 3,
    scrollMargin: parentOffsetTop,
    getItemKey: (index) => props.getItemKey(props.items[index], index),
  });
  virtualizer.shouldAdjustScrollPositionOnItemSizeChange =
    props.adjustScrollOnItemSizeChange === false ? () => false : undefined;

  useInfiniteLoadTrigger({
    virtualItems: virtualizer.getVirtualItems(),
    itemCount: props.items.length,
    hasNextPage: props.hasNextPage,
    isFetchingNextPage: props.isFetchingNextPage,
    loadMore: props.loadMore,
    threshold: props.loadMoreThreshold ?? (props.overscan ?? 3) + 1,
  });

  return (
    <SingleColumnFrame
      parentRef={parentRef}
      virtualizer={virtualizer}
      offset={parentOffsetTop}
      items={props.items}
      renderItem={props.renderItem}
      itemClassName={props.itemClassName}
      className={props.className}
      style={props.style}
      endContent={props.endContent}
      isFetchingNextPage={props.isFetchingNextPage}
      hasNextPage={props.hasNextPage}
      onActiveIndexChange={props.onActiveIndexChange}
      windowScroll
    />
  );
}

// ---------- Single-column, inner container scroll ----------

function SingleColumnContainerScroll<TItem>(
  props: SingleColumnProps<TItem> & { scrollElementRef: RefObject<HTMLElement | null> },
) {
  const virtualizer = useVirtualizer({
    count: props.items.length,
    getScrollElement: () => props.scrollElementRef.current,
    estimateSize: () => props.estimateSize,
    overscan: props.overscan ?? 3,
    getItemKey: (index) => props.getItemKey(props.items[index], index),
  });
  virtualizer.shouldAdjustScrollPositionOnItemSizeChange =
    props.adjustScrollOnItemSizeChange === false ? () => false : undefined;

  useInfiniteLoadTrigger({
    virtualItems: virtualizer.getVirtualItems(),
    itemCount: props.items.length,
    hasNextPage: props.hasNextPage,
    isFetchingNextPage: props.isFetchingNextPage,
    loadMore: props.loadMore,
    threshold: props.loadMoreThreshold ?? (props.overscan ?? 3) + 1,
  });

  return (
    <SingleColumnFrame
      parentRef={undefined}
      virtualizer={virtualizer}
      offset={0}
      items={props.items}
      renderItem={props.renderItem}
      itemClassName={props.itemClassName}
      className={props.className}
      style={props.style}
      endContent={props.endContent}
      isFetchingNextPage={props.isFetchingNextPage}
      hasNextPage={props.hasNextPage}
      onActiveIndexChange={props.onActiveIndexChange}
      scrollElement={props.scrollElementRef.current}
    />
  );
}

interface SingleColumnFrameProps<TItem> {
  parentRef: React.MutableRefObject<HTMLDivElement | null> | undefined;
  virtualizer: Virtualizer<Window, Element> | Virtualizer<HTMLElement, Element>;
  offset: number;
  items: TItem[];
  renderItem: (arg: VirtualizedRenderArg<TItem>) => ReactNode;
  itemClassName?: string;
  className?: string;
  style?: React.CSSProperties;
  endContent?: ReactNode;
  isFetchingNextPage: boolean;
  hasNextPage: boolean;
  onActiveIndexChange?: (index: number | null) => void;
  windowScroll?: boolean;
  scrollElement?: HTMLElement | null;
}

function SingleColumnFrame<TItem>({
  parentRef,
  virtualizer,
  offset,
  items,
  renderItem,
  itemClassName,
  className,
  style,
  endContent,
  isFetchingNextPage,
  hasNextPage,
  onActiveIndexChange,
  windowScroll,
  scrollElement,
}: SingleColumnFrameProps<TItem>) {
  const virtualItems = virtualizer.getVirtualItems();
  const totalSize = virtualizer.getTotalSize();
  const activeRange = useMemo(() => {
    if (virtualItems.length === 0) return { first: -1, last: -1 };
    return { first: virtualItems[0].index, last: virtualItems[virtualItems.length - 1].index };
  }, [virtualItems]);

  useActiveIndexNotifier({
    virtualItems,
    onActiveIndexChange,
    getScrollOffset: () => (windowScroll ? window.scrollY - offset : (scrollElement?.scrollTop ?? 0)),
    getViewportSize: () => (windowScroll ? window.innerHeight : (scrollElement?.clientHeight ?? window.innerHeight)),
    windowScroll,
    scrollElement,
  });

  return (
    <div ref={parentRef} className={className} style={style}>
      <div style={{ position: "relative", width: "100%", height: totalSize }}>
        {virtualItems.map((virtualRow) => {
          const item = items[virtualRow.index];
          if (item == null) return null;
          return (
            <div
              key={virtualRow.key}
              data-index={virtualRow.index}
              ref={virtualizer.measureElement}
              className={itemClassName}
              style={{
                position: "absolute",
                top: 0,
                left: 0,
                width: "100%",
                transform: `translateY(${virtualRow.start - offset}px)`,
              }}
            >
              {renderItem({
                item,
                index: virtualRow.index,
                measureRef: () => {
                  /* measured via the outer ref */
                },
                isActive: virtualRow.index >= activeRange.first && virtualRow.index <= activeRange.last,
              })}
            </div>
          );
        })}
      </div>
      {(isFetchingNextPage || hasNextPage || endContent) && (
        <div className="py-3 text-center text-xs text-muted">{isFetchingNextPage ? "Loading…" : endContent}</div>
      )}
    </div>
  );
}

// ---------- Grid, window scroll ----------

function GridWindowScroll<TItem>(props: GridProps<TItem>) {
  const parentRef = useRef<HTMLDivElement | null>(null);
  const [parentOffsetTop, setParentOffsetTop] = useState(0);
  const [containerWidth, setContainerWidth] = useState(0);

  useLayoutEffect(() => {
    if (!parentRef.current) return;
    setParentOffsetTop(parentRef.current.getBoundingClientRect().top + window.scrollY);

    const ro = new ResizeObserver(() => {
      if (!parentRef.current) return;
      setContainerWidth(parentRef.current.clientWidth);
      setParentOffsetTop(parentRef.current.getBoundingClientRect().top + window.scrollY);
    });
    ro.observe(parentRef.current);
    setContainerWidth(parentRef.current.clientWidth);
    return () => ro.disconnect();
  }, []);

  const gap = props.gap ?? 12;
  const columns = Math.max(1, Math.floor((containerWidth + gap) / (props.minColumnWidth + gap)));
  const rowCount = Math.ceil(props.items.length / columns);

  const virtualizer = useWindowVirtualizer({
    count: rowCount,
    estimateSize: () => props.estimateSize,
    overscan: props.overscan ?? 3,
    scrollMargin: parentOffsetTop,
    getItemKey: (rowIndex) => {
      const first = props.items[rowIndex * columns];
      return first ? `row-${props.getItemKey(first, rowIndex * columns)}` : `row-${rowIndex}`;
    },
  });
  virtualizer.shouldAdjustScrollPositionOnItemSizeChange =
    props.adjustScrollOnItemSizeChange === false ? () => false : undefined;

  useInfiniteLoadTriggerRows({
    virtualItems: virtualizer.getVirtualItems(),
    rowCount,
    hasNextPage: props.hasNextPage,
    isFetchingNextPage: props.isFetchingNextPage,
    loadMore: props.loadMore,
    threshold: props.loadMoreThreshold ?? (props.overscan ?? 3) + 1,
  });

  return (
    <GridFrame
      parentRef={parentRef}
      virtualizer={virtualizer}
      offset={parentOffsetTop}
      items={props.items}
      columns={columns}
      gap={gap}
      renderItem={props.renderItem}
      className={props.className}
      style={props.style}
      endContent={props.endContent}
      isFetchingNextPage={props.isFetchingNextPage}
      hasNextPage={props.hasNextPage}
      onActiveIndexChange={props.onActiveIndexChange}
      windowScroll
    />
  );
}

// ---------- Grid, inner container scroll ----------

function GridContainerScroll<TItem>(props: GridProps<TItem> & { scrollElementRef: RefObject<HTMLElement | null> }) {
  const wrapperRef = useRef<HTMLDivElement | null>(null);
  const [containerWidth, setContainerWidth] = useState(0);

  useLayoutEffect(() => {
    if (!wrapperRef.current) return;
    const ro = new ResizeObserver(() => {
      if (wrapperRef.current) setContainerWidth(wrapperRef.current.clientWidth);
    });
    ro.observe(wrapperRef.current);
    setContainerWidth(wrapperRef.current.clientWidth);
    return () => ro.disconnect();
  }, []);

  const gap = props.gap ?? 12;
  const columns = Math.max(1, Math.floor((containerWidth + gap) / (props.minColumnWidth + gap)));
  const rowCount = Math.ceil(props.items.length / columns);

  const virtualizer = useVirtualizer({
    count: rowCount,
    getScrollElement: () => props.scrollElementRef.current,
    estimateSize: () => props.estimateSize,
    overscan: props.overscan ?? 3,
    getItemKey: (rowIndex) => {
      const first = props.items[rowIndex * columns];
      return first ? `row-${props.getItemKey(first, rowIndex * columns)}` : `row-${rowIndex}`;
    },
  });
  virtualizer.shouldAdjustScrollPositionOnItemSizeChange =
    props.adjustScrollOnItemSizeChange === false ? () => false : undefined;

  useInfiniteLoadTriggerRows({
    virtualItems: virtualizer.getVirtualItems(),
    rowCount,
    hasNextPage: props.hasNextPage,
    isFetchingNextPage: props.isFetchingNextPage,
    loadMore: props.loadMore,
    threshold: props.loadMoreThreshold ?? (props.overscan ?? 3) + 1,
  });

  return (
    <GridFrame
      parentRef={wrapperRef}
      virtualizer={virtualizer}
      offset={0}
      items={props.items}
      columns={columns}
      gap={gap}
      renderItem={props.renderItem}
      className={props.className}
      style={props.style}
      endContent={props.endContent}
      isFetchingNextPage={props.isFetchingNextPage}
      hasNextPage={props.hasNextPage}
      onActiveIndexChange={props.onActiveIndexChange}
      scrollElement={props.scrollElementRef.current}
    />
  );
}

interface GridFrameProps<TItem> {
  parentRef: React.MutableRefObject<HTMLDivElement | null>;
  virtualizer: Virtualizer<Window, Element> | Virtualizer<HTMLElement, Element>;
  offset: number;
  items: TItem[];
  columns: number;
  gap: number;
  renderItem: (arg: VirtualizedRenderArg<TItem>) => ReactNode;
  className?: string;
  style?: React.CSSProperties;
  endContent?: ReactNode;
  isFetchingNextPage: boolean;
  hasNextPage: boolean;
  onActiveIndexChange?: (index: number | null) => void;
  windowScroll?: boolean;
  scrollElement?: HTMLElement | null;
}

function GridFrame<TItem>({
  parentRef,
  virtualizer,
  offset,
  items,
  columns,
  gap,
  renderItem,
  className,
  style,
  endContent,
  isFetchingNextPage,
  hasNextPage,
  onActiveIndexChange,
  windowScroll,
  scrollElement,
}: GridFrameProps<TItem>) {
  const virtualRows = virtualizer.getVirtualItems();
  const totalSize = virtualizer.getTotalSize();
  const activeRange = useMemo(() => {
    if (virtualRows.length === 0) return { first: -1, last: -1 };
    return {
      first: virtualRows[0].index * columns,
      last: (virtualRows[virtualRows.length - 1].index + 1) * columns - 1,
    };
  }, [virtualRows, columns]);

  useActiveIndexNotifier({
    virtualItems: virtualRows,
    onActiveIndexChange: onActiveIndexChange
      ? (rowIndex) => onActiveIndexChange(rowIndex == null ? null : rowIndex * columns)
      : undefined,
    getScrollOffset: () => (windowScroll ? window.scrollY - offset : (scrollElement?.scrollTop ?? 0)),
    getViewportSize: () => (windowScroll ? window.innerHeight : (scrollElement?.clientHeight ?? window.innerHeight)),
    windowScroll,
    scrollElement,
  });

  return (
    <div ref={parentRef} className={className} style={style}>
      <div style={{ position: "relative", width: "100%", height: totalSize }}>
        {virtualRows.map((virtualRow) => {
          const rowStartIndex = virtualRow.index * columns;
          const rowItems = items.slice(rowStartIndex, rowStartIndex + columns);
          if (rowItems.length === 0) return null;
          return (
            <div
              key={virtualRow.key}
              data-index={virtualRow.index}
              ref={virtualizer.measureElement}
              style={{
                position: "absolute",
                top: 0,
                left: 0,
                width: "100%",
                transform: `translateY(${virtualRow.start - offset}px)`,
                display: "grid",
                gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`,
                gap,
                paddingBottom: gap,
              }}
            >
              {rowItems.map((item, columnIndex) => {
                const itemIndex = rowStartIndex + columnIndex;
                return (
                  <div key={virtualRow.key + "-" + columnIndex} style={{ minWidth: 0 }}>
                    {renderItem({
                      item,
                      index: itemIndex,
                      measureRef: () => {
                        /* row-level measurement */
                      },
                      isActive: itemIndex >= activeRange.first && itemIndex <= activeRange.last,
                    })}
                  </div>
                );
              })}
            </div>
          );
        })}
      </div>
      {(isFetchingNextPage || hasNextPage || endContent) && (
        <div className="py-3 text-center text-xs text-muted">{isFetchingNextPage ? "Loading…" : endContent}</div>
      )}
    </div>
  );
}

// ---------- Active index notifier ----------

function useActiveIndexNotifier({
  virtualItems,
  onActiveIndexChange,
  getScrollOffset,
  getViewportSize,
  windowScroll,
  scrollElement,
}: {
  virtualItems: { index: number; start: number; size: number }[];
  onActiveIndexChange?: (index: number | null) => void;
  getScrollOffset: () => number;
  getViewportSize: () => number;
  windowScroll?: boolean;
  scrollElement?: HTMLElement | null;
}) {
  const cb = onActiveIndexChange;
  const lastReportedRef = useRef<number | null>(null);
  const virtualItemsRef = useRef(virtualItems);
  const getScrollOffsetRef = useRef(getScrollOffset);
  const getViewportSizeRef = useRef(getViewportSize);

  virtualItemsRef.current = virtualItems;
  getScrollOffsetRef.current = getScrollOffset;
  getViewportSizeRef.current = getViewportSize;

  const reportActiveIndex = useCallback(() => {
    if (!cb) return;
    const currentVirtualItems = virtualItemsRef.current;
    if (currentVirtualItems.length === 0) {
      if (lastReportedRef.current !== null) {
        lastReportedRef.current = null;
        cb(null);
      }
      return;
    }

    const viewportStart = getScrollOffsetRef.current();
    const viewportEnd = viewportStart + getViewportSizeRef.current();
    const viewportMidpoint = viewportStart + (viewportEnd - viewportStart) / 2;
    let bestIndex: number | null = null;
    let bestVisible = 0;
    let bestDistance = Infinity;
    for (const v of currentVirtualItems) {
      const itemStart = v.start;
      const itemEnd = v.start + v.size;
      const visible = Math.max(0, Math.min(itemEnd, viewportEnd) - Math.max(itemStart, viewportStart));
      if (visible <= 0) continue;
      const itemMidpoint = itemStart + v.size / 2;
      const distance = Math.abs(itemMidpoint - viewportMidpoint);
      if (visible > bestVisible || (visible === bestVisible && distance < bestDistance)) {
        bestVisible = visible;
        bestDistance = distance;
        bestIndex = v.index;
      }
    }
    if (bestIndex == null && currentVirtualItems.length > 0) {
      bestIndex = currentVirtualItems[0].index;
    }
    if (lastReportedRef.current !== bestIndex) {
      lastReportedRef.current = bestIndex;
      cb(bestIndex);
    }
  }, [cb]);

  useEffect(() => {
    reportActiveIndex();
  });

  useEffect(() => {
    if (!cb) return;
    const target = windowScroll ? window : scrollElement;
    if (!target) {
      reportActiveIndex();
      return;
    }

    let frameId = 0;
    const scheduleReport = () => {
      if (frameId !== 0) return;
      frameId = window.requestAnimationFrame(() => {
        frameId = 0;
        reportActiveIndex();
      });
    };

    target.addEventListener("scroll", scheduleReport, { passive: true });
    window.addEventListener("resize", scheduleReport);
    reportActiveIndex();

    return () => {
      if (frameId !== 0) window.cancelAnimationFrame(frameId);
      target.removeEventListener("scroll", scheduleReport);
      window.removeEventListener("resize", scheduleReport);
    };
  }, [cb, reportActiveIndex, scrollElement, windowScroll]);
}

// ---------- Infinite load triggers ----------

function useInfiniteLoadTrigger({
  virtualItems,
  itemCount,
  hasNextPage,
  isFetchingNextPage,
  loadMore,
  threshold,
}: {
  virtualItems: { index: number }[];
  itemCount: number;
  hasNextPage: boolean;
  isFetchingNextPage: boolean;
  loadMore: () => void;
  threshold: number;
}) {
  const loadMoreRef = useRef(loadMore);
  loadMoreRef.current = loadMore;

  const lastIndex = virtualItems.length > 0 ? virtualItems[virtualItems.length - 1].index : -1;
  const shouldLoad = hasNextPage && !isFetchingNextPage && lastIndex >= itemCount - 1 - threshold && itemCount > 0;

  useEffect(() => {
    if (shouldLoad) loadMoreRef.current();
  }, [shouldLoad]);
}

function useInfiniteLoadTriggerRows({
  virtualItems,
  rowCount,
  hasNextPage,
  isFetchingNextPage,
  loadMore,
  threshold,
}: {
  virtualItems: { index: number }[];
  rowCount: number;
  hasNextPage: boolean;
  isFetchingNextPage: boolean;
  loadMore: () => void;
  threshold: number;
}) {
  const loadMoreRef = useRef(loadMore);
  loadMoreRef.current = loadMore;

  const lastRow = virtualItems.length > 0 ? virtualItems[virtualItems.length - 1].index : -1;
  const shouldLoad = hasNextPage && !isFetchingNextPage && lastRow >= rowCount - 1 - threshold && rowCount > 0;

  useEffect(() => {
    if (shouldLoad) loadMoreRef.current();
  }, [shouldLoad]);
}

// ---------- Helper to enable infinite mode regardless of perPage ----------

export const VIRTUAL_INFINITE_CHUNK_SIZE = 40;
