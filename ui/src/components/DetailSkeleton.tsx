interface DetailSkeletonProps {
  lines?: number;
  showMedia?: boolean;
}

export function DetailSkeleton({ lines = 4, showMedia = true }: DetailSkeletonProps) {
  return (
    <div className="animate-pulse space-y-4" aria-hidden="true">
      {showMedia ? <div className="aspect-video rounded-3xl bg-surface" /> : null}
      <div className="h-8 w-2/3 rounded-full bg-surface" />
      <div className="space-y-2">
        {Array.from({ length: lines }, (_, index) => (
          <div
            key={index}
            className="h-4 rounded-full bg-surface"
            style={{ width: `${Math.max(45, 100 - index * 12)}%` }}
          />
        ))}
      </div>
    </div>
  );
}
