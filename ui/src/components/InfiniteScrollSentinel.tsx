import { useEffect, useRef } from "react";
import { Loader2 } from "lucide-react";

interface InfiniteScrollSentinelProps {
  hasMore: boolean;
  isLoading: boolean;
  onLoadMore: () => void;
  loadedCount: number;
  totalCount?: number;
  direction?: "next" | "previous";
  className?: string;
  rootMargin?: string;
}

export function InfiniteScrollSentinel({
  hasMore,
  isLoading,
  onLoadMore,
  loadedCount,
  totalCount,
  direction = "next",
  className,
  rootMargin,
}: InfiniteScrollSentinelProps) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const node = ref.current;
    if (!node || !hasMore || isLoading) {
      return;
    }

    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          onLoadMore();
        }
      },
      { rootMargin: rootMargin ?? (direction === "previous" ? "120px 0px 0px 0px" : "800px 0px") },
    );

    observer.observe(node);
    return () => observer.disconnect();
  }, [direction, hasMore, isLoading, onLoadMore, rootMargin]);

  return (
    <div
      ref={ref}
      className={`flex items-center justify-center px-4 py-6 text-sm text-muted ${className ?? ""}`.trim()}
    >
      {isLoading ? (
        <span className="inline-flex items-center gap-2">
          <Loader2 className="h-4 w-4 animate-spin" />
          {direction === "previous" ? "Restoring earlier items..." : "Loading more..."}
        </span>
      ) : hasMore ? (
        <span>
          {direction === "previous"
            ? `Earlier items hidden. Scroll up to restore them.`
            : `Loaded ${loadedCount}${totalCount ? ` of ${totalCount}` : ""}. Scroll to load more.`}
        </span>
      ) : (
        <span>
          {direction === "previous"
            ? "At the start of the results."
            : `Loaded all ${loadedCount}${totalCount && totalCount !== loadedCount ? ` of ${totalCount}` : ""} items.`}
        </span>
      )}
    </div>
  );
}
