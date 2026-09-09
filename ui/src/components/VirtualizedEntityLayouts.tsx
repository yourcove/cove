import { Fragment, type ReactNode } from "react";
import { EntityCardGrid } from "./EntityCardGrid";
import { VirtualizedInfiniteList } from "./VirtualizedInfiniteList";
import { useListPageCardMinWidthPx } from "./ListPageCardSizeContext";

export interface InfiniteEntityLoadingState {
  infinitePageSize: boolean;
  hasNextPage?: boolean;
  isFetchingNextPage?: boolean;
  loadMore?: () => void;
}

interface VirtualizedEntityGridProps<TItem> extends InfiniteEntityLoadingState {
  items: TItem[];
  getItemKey: (item: TItem, index: number) => string | number;
  renderItem: (item: TItem, index: number) => ReactNode;
  minCardWidth?: string;
  virtualMinColumnWidth?: number;
  estimateRowHeight?: number;
  gap?: number;
  overscan?: number;
  className?: string;
  gapClassName?: string;
}

export function VirtualizedEntityGrid<TItem>({
  items,
  getItemKey,
  renderItem,
  infinitePageSize,
  hasNextPage,
  isFetchingNextPage,
  loadMore,
  minCardWidth = "var(--card-min-width, 200px)",
  virtualMinColumnWidth,
  estimateRowHeight = 320,
  gap = 12,
  overscan = 3,
  className,
  gapClassName,
}: VirtualizedEntityGridProps<TItem>) {
  const sharedCardMinWidthPx = useListPageCardMinWidthPx();
  const inferredMinCardWidthPx = inferMinCardWidthPx(minCardWidth);
  const resolvedVirtualMinColumnWidth = virtualMinColumnWidth ?? sharedCardMinWidthPx ?? inferredMinCardWidthPx ?? 220;

  if (infinitePageSize && items.length > 0) {
    return (
      <VirtualizedInfiniteList
        layout="grid"
        items={items}
        getItemKey={getItemKey}
        minColumnWidth={resolvedVirtualMinColumnWidth}
        gap={gap}
        estimateSize={estimateRowHeight}
        overscan={overscan}
        hasNextPage={Boolean(hasNextPage)}
        isFetchingNextPage={Boolean(isFetchingNextPage)}
        loadMore={loadMore ?? noop}
        className={className}
        renderItem={({ item, index }) => renderItem(item, index)}
      />
    );
  }

  return (
    <EntityCardGrid minCardWidth={minCardWidth} gapClassName={gapClassName} className={className}>
      {items.map((item, index) => (
        <Fragment key={getItemKey(item, index)}>{renderItem(item, index)}</Fragment>
      ))}
    </EntityCardGrid>
  );
}

function inferMinCardWidthPx(minCardWidth: string | undefined) {
  if (!minCardWidth) {
    return undefined;
  }

  const pxMatch = /^\s*(\d+(?:\.\d+)?)px\s*$/.exec(minCardWidth);
  if (pxMatch) {
    return Math.round(Number(pxMatch[1]));
  }

  const cssVarFallbackMatch = /^\s*var\(\s*--[^,]+,\s*(\d+(?:\.\d+)?)px\s*\)\s*$/.exec(minCardWidth);
  if (cssVarFallbackMatch) {
    return Math.round(Number(cssVarFallbackMatch[1]));
  }

  return undefined;
}

interface VirtualizedWallColumnsProps<TItem> extends InfiniteEntityLoadingState {
  columns: TItem[][];
  getItemKey: (item: TItem, index: number, columnIndex: number) => string | number;
  renderItem: (item: TItem, index: number, columnIndex: number) => ReactNode;
  estimateItemHeight?: number;
  gap?: number;
  overscan?: number;
  className?: string;
  columnClassName?: string;
}

export function VirtualizedWallColumns<TItem>({
  columns,
  getItemKey,
  renderItem,
  infinitePageSize,
  hasNextPage,
  isFetchingNextPage,
  loadMore,
  estimateItemHeight = 280,
  gap = 8,
  overscan = 4,
  className = "flex gap-2 px-2",
  columnClassName = "flex min-w-0 flex-1 flex-col gap-2",
}: VirtualizedWallColumnsProps<TItem>) {
  if (infinitePageSize && columns.some((column) => column.length > 0)) {
    return (
      <div className={className}>
        {columns.map((column, columnIndex) => (
          <div key={columnIndex} className="min-w-0 flex-1">
            <VirtualizedInfiniteList
              items={column}
              getItemKey={(item, index) => getItemKey(item, index, columnIndex)}
              estimateSize={estimateItemHeight + gap}
              overscan={overscan}
              hasNextPage={Boolean(hasNextPage)}
              isFetchingNextPage={Boolean(isFetchingNextPage)}
              loadMore={loadMore ?? noop}
              renderItem={({ item, index }) => (
                <div style={{ paddingBottom: gap }}>{renderItem(item, index, columnIndex)}</div>
              )}
            />
          </div>
        ))}
      </div>
    );
  }

  return (
    <div className={className}>
      {columns.map((column, columnIndex) => (
        <div key={columnIndex} className={columnClassName}>
          {column.map((item, index) => (
            <Fragment key={getItemKey(item, index, columnIndex)}>{renderItem(item, index, columnIndex)}</Fragment>
          ))}
        </div>
      ))}
    </div>
  );
}

function noop() {}
