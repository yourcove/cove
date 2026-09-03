export function FilterButton({
  activeCount,
  onClick,
}: {
  activeCount: number;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={activeCount > 0 ? `Filters, ${activeCount} active` : "Filters"}
      className={`flex items-center gap-1 rounded border px-2 py-1 text-xs ${
        activeCount > 0
          ? "border-accent bg-accent/10 text-accent"
          : "border-border bg-card/70 text-secondary hover:border-accent hover:text-foreground"
      }`}
    >
      <svg className="h-3.5 w-3.5" aria-hidden="true" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
        <path strokeLinecap="round" strokeLinejoin="round" d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z" />
      </svg>
      Filters
      {activeCount > 0 && (
        <span className="min-w-[16px] rounded-full bg-accent px-1 py-0 text-center text-[10px] font-bold text-white" aria-hidden="true">
          {activeCount}
        </span>
      )}
    </button>
  );
}
