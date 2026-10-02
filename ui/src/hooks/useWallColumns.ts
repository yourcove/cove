import { useEffect, useMemo, useState } from "react";

function getWallColumnCount(width: number, maxColumns: number) {
  const responsiveLimit = Math.max(2, Math.floor(width / 180));
  return Math.min(maxColumns, responsiveLimit);
}

interface WallColumnOptions<T> {
  stable?: boolean;
  getKey?: (item: T) => string | number;
}

type ColumnKey = string | number;

interface StableColumnAssignment {
  columnCount: number;
  columns: ReadonlyMap<ColumnKey, number>;
}

function emptyAssignment(columnCount: number): StableColumnAssignment {
  return { columnCount, columns: new Map() };
}

function shortestColumn(columnHeights: number[]) {
  let shortestColumnIndex = 0;
  for (let index = 1; index < columnHeights.length; index += 1) {
    if (columnHeights[index] < columnHeights[shortestColumnIndex]) {
      shortestColumnIndex = index;
    }
  }
  return shortestColumnIndex;
}

// Keeps every still-present item in the column it was given before and places new items in the
// shortest column. Returns `previous` itself when nothing changed, so it is safe to store the result
// in state during render.
function assignStableColumns<T>(
  previous: StableColumnAssignment,
  items: T[],
  columnCount: number,
  getKey: (item: T) => ColumnKey,
  estimateHeight?: (item: T) => number,
): StableColumnAssignment {
  const base = previous.columnCount === columnCount ? previous : emptyAssignment(columnCount);
  const columns = new Map<ColumnKey, number>();
  const columnHeights = Array.from({ length: columnCount }, () => 0);
  const unassigned: T[] = [];

  for (const item of items) {
    const key = getKey(item);
    const assignedColumn = base.columns.get(key);
    if (assignedColumn != null && assignedColumn >= 0 && assignedColumn < columnCount) {
      columns.set(key, assignedColumn);
      columnHeights[assignedColumn] += Math.max(estimateHeight?.(item) ?? 1, 0.25);
    } else {
      unassigned.push(item);
    }
  }

  for (const item of unassigned) {
    const key = getKey(item);
    if (columns.has(key)) continue;
    const shortestColumnIndex = shortestColumn(columnHeights);
    columns.set(key, shortestColumnIndex);
    columnHeights[shortestColumnIndex] += Math.max(estimateHeight?.(item) ?? 1, 0.25);
  }

  const unchanged =
    base === previous &&
    columns.size === previous.columns.size &&
    [...columns].every(([key, column]) => previous.columns.get(key) === column);
  return unchanged ? previous : { columnCount, columns };
}

export function useWallColumns<T>(
  items: T[],
  maxColumns: number,
  estimateHeight?: (item: T) => number,
  options?: WallColumnOptions<T>,
) {
  const [columnCount, setColumnCount] = useState(() =>
    getWallColumnCount(typeof window === "undefined" ? 1280 : window.innerWidth, maxColumns),
  );
  const [stableAssignment, setStableAssignment] = useState(() => emptyAssignment(columnCount));

  useEffect(() => {
    const updateColumnCount = () => {
      setColumnCount(getWallColumnCount(window.innerWidth, maxColumns));
    };

    updateColumnCount();
    window.addEventListener("resize", updateColumnCount);
    return () => window.removeEventListener("resize", updateColumnCount);
  }, [maxColumns]);

  const stableGetKey = options?.stable ? options.getKey : undefined;
  const nextStableAssignment = useMemo(
    () =>
      stableGetKey
        ? assignStableColumns(stableAssignment, items, columnCount, stableGetKey, estimateHeight)
        : emptyAssignment(columnCount),
    [columnCount, estimateHeight, items, stableAssignment, stableGetKey],
  );
  // Remember the assignment for the next render. A non-stable render stores an empty assignment, so
  // stable columns start fresh when they are turned back on.
  if (
    nextStableAssignment !== stableAssignment &&
    (stableGetKey || stableAssignment.columns.size > 0 || stableAssignment.columnCount !== columnCount)
  ) {
    setStableAssignment(nextStableAssignment);
  }

  return useMemo(() => {
    const columns = Array.from({ length: columnCount }, () => [] as T[]);

    if (stableGetKey) {
      items.forEach((item) => columns[nextStableAssignment.columns.get(stableGetKey(item)) ?? 0].push(item));
      return columns;
    }

    const columnHeights = Array.from({ length: columnCount }, () => 0);
    items.forEach((item) => {
      const shortestColumnIndex = shortestColumn(columnHeights);
      columns[shortestColumnIndex].push(item);
      columnHeights[shortestColumnIndex] += Math.max(estimateHeight?.(item) ?? 1, 0.25);
    });
    return columns;
  }, [columnCount, estimateHeight, items, nextStableAssignment, stableGetKey]);
}
