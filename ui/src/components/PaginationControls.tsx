import { useState } from "react";
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from "lucide-react";

export function PaginationControls({ page, totalPages, goTo }: { page: number; totalPages: number; goTo: (page: number) => void }) {
  const [editing, setEditing] = useState(false);
  const [inputValue, setInputValue] = useState(String(page));

  const handleSubmit = () => {
    const nextPage = Number.parseInt(inputValue, 10);
    if (!Number.isNaN(nextPage) && nextPage >= 1 && nextPage <= totalPages) goTo(nextPage);
    setEditing(false);
  };

  return (
    <>
      <button type="button" aria-label="First page" title="First page" onClick={() => goTo(1)} disabled={page <= 1} className="inline-flex min-h-10 min-w-10 items-center justify-center rounded text-secondary hover:bg-card hover:text-foreground disabled:cursor-not-allowed disabled:opacity-30 sm:min-h-0 sm:min-w-0 sm:p-1">
        <ChevronsLeft className="w-3.5 h-3.5" />
      </button>
      <button type="button" aria-label="Previous page" title="Previous page" onClick={() => goTo(page - 1)} disabled={page <= 1} className="inline-flex min-h-10 min-w-10 items-center justify-center rounded text-secondary hover:bg-card hover:text-foreground disabled:cursor-not-allowed disabled:opacity-30 sm:min-h-0 sm:min-w-0 sm:p-1">
        <ChevronLeft className="w-3.5 h-3.5" />
      </button>
      {getPageNumbers(page, totalPages).map((pageNumber, index) =>
        pageNumber === -1 ? (
          <span key={`ellipsis-${index}`} aria-hidden="true" className="px-1 text-muted text-xs">…</span>
        ) : (
          <button
            type="button"
            key={pageNumber}
            aria-label={`Page ${pageNumber}`}
            aria-current={pageNumber === page ? "page" : undefined}
            onClick={() => goTo(pageNumber)}
            className={`h-10 min-w-10 rounded text-sm font-medium sm:h-7 sm:min-w-[28px] sm:text-xs ${
              pageNumber === page ? "bg-accent text-white" : "text-secondary hover:bg-card hover:text-foreground"
            }`}
          >
            {pageNumber}
          </button>
        )
      )}
      <button type="button" aria-label="Next page" title="Next page" onClick={() => goTo(page + 1)} disabled={page >= totalPages} className="inline-flex min-h-10 min-w-10 items-center justify-center rounded text-secondary hover:bg-card hover:text-foreground disabled:cursor-not-allowed disabled:opacity-30 sm:min-h-0 sm:min-w-0 sm:p-1">
        <ChevronRight className="w-3.5 h-3.5" />
      </button>
      <button type="button" aria-label="Last page" title="Last page" onClick={() => goTo(totalPages)} disabled={page >= totalPages} className="inline-flex min-h-10 min-w-10 items-center justify-center rounded text-secondary hover:bg-card hover:text-foreground disabled:cursor-not-allowed disabled:opacity-30 sm:min-h-0 sm:min-w-0 sm:p-1">
        <ChevronsRight className="w-3.5 h-3.5" />
      </button>
      {totalPages > 7 && (
        editing ? (
          <form onSubmit={(event) => { event.preventDefault(); handleSubmit(); }} className="ml-1 flex items-center gap-1">
            <input
              type="text"
              aria-label="Page number"
              autoFocus
              value={inputValue}
              onChange={(event) => setInputValue(event.target.value)}
              onBlur={handleSubmit}
              className="h-10 w-14 rounded border border-border bg-input text-center text-sm text-foreground focus:border-accent focus:outline-none sm:h-7 sm:w-12 sm:text-xs"
            />
          </form>
        ) : (
          <button type="button" aria-label="Go to page" onClick={() => { setInputValue(String(page)); setEditing(true); }} className="ml-1 min-h-10 rounded border border-border px-3 text-sm text-muted hover:bg-card hover:text-foreground sm:h-7 sm:min-h-0 sm:px-2 sm:text-xs" title="Go to page…">
            Go to…
          </button>
        )
      )}
    </>
  );
}

function getPageNumbers(current: number, total: number): number[] {
  if (total <= 7) return Array.from({ length: total }, (_, index) => index + 1);
  const pages: number[] = [1];
  if (current > 3) pages.push(-1);
  for (let page = Math.max(2, current - 1); page <= Math.min(total - 1, current + 1); page += 1) pages.push(page);
  if (current < total - 2) pages.push(-1);
  pages.push(total);
  return pages;
}
