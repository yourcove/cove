import { useEffect, useMemo, useState } from "react";

function getWallColumnCount(width: number, maxColumns: number) {
  const responsiveLimit = Math.max(2, Math.floor(width / 180));
  return Math.min(maxColumns, responsiveLimit);
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