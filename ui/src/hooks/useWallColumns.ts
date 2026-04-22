import { useEffect, useMemo, useState } from "react";

function getWallColumnCount(width: number, maxColumns: number) {
  if (width >= 1280) {
    return Math.min(maxColumns, 6);
  }
  if (width >= 1024) {
    return Math.min(maxColumns, 5);
  }
  if (width >= 768) {
    return Math.min(maxColumns, 4);
  }
  if (width >= 640) {
    return Math.min(maxColumns, 3);
  }
  return Math.min(maxColumns, 2);
}

export function useWallColumns<T>(items: T[], maxColumns: number) {
  const [columnCount, setColumnCount] = useState(() => getWallColumnCount(typeof window === "undefined" ? 1280 : window.innerWidth, maxColumns));

  useEffect(() => {
    const updateColumnCount = () => {
      setColumnCount(getWallColumnCount(window.innerWidth, maxColumns));
    };

    updateColumnCount();
    window.addEventListener("resize", updateColumnCount);
    return () => window.removeEventListener("resize", updateColumnCount);
  }, [maxColumns]);

  return useMemo(() => {
    const columns = Array.from({ length: columnCount }, () => [] as T[]);
    items.forEach((item, index) => {
      columns[index % columnCount].push(item);
    });
    return columns;
  }, [columnCount, items]);
}