import { useEffect, useMemo, useRef, useState } from "react";

function getWallColumnCount(width: number, maxColumns: number) {
  const responsiveLimit = Math.max(2, Math.floor(width / 180));
  return Math.min(maxColumns, responsiveLimit);
}

interface WallColumnOptions<T> {
  stable?: boolean;
  getKey?: (item: T) => string | number;
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
  const assignmentRef = useRef<{ columnCount: number; keys: Map<string | number, number> }>({
    columnCount,
    keys: new Map(),
  });

  useEffect(() => {
    const updateColumnCount = () => {
      setColumnCount(getWallColumnCount(window.innerWidth, maxColumns));
    };

    updateColumnCount();
    window.addEventListener("resize", updateColumnCount);
    return () => window.removeEventListener("resize", updateColumnCount);
  }, [maxColumns]);

  return useMemo(() => {
    if (!options?.stable || !options.getKey) {
      assignmentRef.current = { columnCount, keys: new Map() };
      const columns = Array.from({ length: columnCount }, () => [] as T[]);
      const columnHeights = Array.from({ length: columnCount }, () => 0);

      items.forEach((item) => {
        let shortestColumnIndex = 0;

        for (let index = 1; index < columnHeights.length; index += 1) {
          if (columnHeights[index] < columnHeights[shortestColumnIndex]) {
            shortestColumnIndex = index;
          }
        }

        columns[shortestColumnIndex].push(item);
        columnHeights[shortestColumnIndex] += Math.max(estimateHeight?.(item) ?? 1, 0.25);
      });

      return columns;
    }

    const getKey = options.getKey;

    if (assignmentRef.current.columnCount !== columnCount) {
      assignmentRef.current = { columnCount, keys: new Map() };
    }

    const assignments = assignmentRef.current.keys;
    const activeKeys = new Set<string | number>();
    const columnHeights = Array.from({ length: columnCount }, () => 0);

    for (const item of items) {
      const key = getKey(item);
      activeKeys.add(key);
      const assignedColumn = assignments.get(key);
      if (assignedColumn != null && assignedColumn >= 0 && assignedColumn < columnCount) {
        columnHeights[assignedColumn] += Math.max(estimateHeight?.(item) ?? 1, 0.25);
      }
    }

    for (const key of assignments.keys()) {
      if (!activeKeys.has(key)) {
        assignments.delete(key);
      }
    }

    for (const item of items) {
      const key = getKey(item);
      const assignedColumn = assignments.get(key);
      if (assignedColumn != null && assignedColumn >= 0 && assignedColumn < columnCount) {
        continue;
      }

      let shortestColumnIndex = 0;
      for (let index = 1; index < columnHeights.length; index += 1) {
        if (columnHeights[index] < columnHeights[shortestColumnIndex]) {
          shortestColumnIndex = index;
        }
      }

      assignments.set(key, shortestColumnIndex);
      columnHeights[shortestColumnIndex] += Math.max(estimateHeight?.(item) ?? 1, 0.25);
    }

    const columns = Array.from({ length: columnCount }, () => [] as T[]);
    items.forEach((item) => columns[assignments.get(getKey(item)) ?? 0].push(item));

    return columns;
  }, [columnCount, estimateHeight, items, options]);
}
