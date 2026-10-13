import { type CSSProperties, useState } from "react";
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from "lucide-react";

const VISIBLE_SLOTS = 7;
const ELLIPSIS = -1;

// Every page-number slot (numbers and ellipses) shares one width derived from the widest page
// number in the list, so the Next/Last arrows stay put while paging (#620). The width uses ch,
// which resolves against each slot's own font, so the font classes are shared as well.
const SLOT_WIDTH_PROPERTY = "--page-slot";
const SLOT_CLASSES =
  "min-w-[max(2.5rem,var(--page-slot))] text-sm font-medium tabular-nums sm:min-w-[max(28px,var(--page-slot))] sm:text-xs";

// Below the sm breakpoint the callers' wrapping flex rows show the page numbers on their own first
// row and the arrows with the Go-to control between them on a second row. Flex order puts the
// controls in that sequence on narrow screens and restores DOM order (« ‹ numbers › » Go to…) above it.
const ARROW_CLASSES =
  "inline-flex min-h-10 min-w-10 items-center justify-center rounded text-secondary hover:bg-card hover:text-foreground disabled:cursor-not-allowed disabled:opacity-30 sm:min-h-0 sm:min-w-0 sm:p-1";
const MOBILE_ORDER = {
  first: "order-1 sm:order-none",
  previous: "order-2 sm:order-none",
  goTo: "order-3 sm:order-none",
  next: "order-4 sm:order-none",
  last: "order-5 sm:order-none",
} as const;

export function PaginationControls({
  page,
  totalPages,
  totalPagesKnown = true,
  goTo,
}: {
  page: number;
  totalPages: number;
  totalPagesKnown?: boolean;
  goTo: (page: number) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [inputValue, setInputValue] = useState(String(page));
  // ch is the width of "0", which tabular-nums makes the width of every digit; 1rem covers padding.
  const slotStyle = { [SLOT_WIDTH_PROPERTY]: `calc(${String(totalPages).length}ch + 1rem)` } as CSSProperties;

  const handleSubmit = () => {
    const nextPage = Number.parseInt(inputValue, 10);
    if (!Number.isNaN(nextPage) && nextPage >= 1 && nextPage <= totalPages) goTo(nextPage);
    setEditing(false);
  };

  return (
    <>
      <button
        type="button"
        aria-label="First page"
        title="First page"
        onClick={() => goTo(1)}
        disabled={page <= 1}
        className={`${ARROW_CLASSES} ${MOBILE_ORDER.first}`}
      >
        <ChevronsLeft className="w-3.5 h-3.5" />
      </button>
      <button
        type="button"
        aria-label="Previous page"
        title="Previous page"
        onClick={() => goTo(page - 1)}
        disabled={page <= 1}
        className={`${ARROW_CLASSES} ${MOBILE_ORDER.previous}`}
      >
        <ChevronLeft className="w-3.5 h-3.5" />
      </button>
      <div
        data-testid="page-numbers"
        className="order-first flex basis-full flex-wrap items-center justify-center gap-1 sm:order-none sm:basis-auto"
      >
        {getPageNumbers(page, totalPages).map((pageNumber, index) =>
          pageNumber === ELLIPSIS ? (
            <span
              key={`ellipsis-${index}`}
              aria-hidden="true"
              style={slotStyle}
              className={`inline-flex h-10 ${SLOT_CLASSES} items-center justify-center text-muted sm:h-7`}
            >
              …
            </span>
          ) : (
            <button
              type="button"
              key={pageNumber}
              aria-label={`Page ${pageNumber}`}
              aria-current={pageNumber === page ? "page" : undefined}
              onClick={() => goTo(pageNumber)}
              style={slotStyle}
              className={`h-10 ${SLOT_CLASSES} rounded sm:h-7 ${
                pageNumber === page ? "bg-accent text-white" : "text-secondary hover:bg-card hover:text-foreground"
              }`}
            >
              {pageNumber}
            </button>
          ),
        )}
      </div>
      <button
        type="button"
        aria-label="Next page"
        title="Next page"
        onClick={() => goTo(page + 1)}
        disabled={page >= totalPages}
        className={`${ARROW_CLASSES} ${MOBILE_ORDER.next}`}
      >
        <ChevronRight className="w-3.5 h-3.5" />
      </button>
      {totalPagesKnown && (
        <button
          type="button"
          aria-label="Last page"
          title="Last page"
          onClick={() => goTo(totalPages)}
          disabled={page >= totalPages}
          className={`${ARROW_CLASSES} ${MOBILE_ORDER.last}`}
        >
          <ChevronsRight className="w-3.5 h-3.5" />
        </button>
      )}
      {totalPages > VISIBLE_SLOTS &&
        (editing ? (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              handleSubmit();
            }}
            className={`mx-1 flex items-center gap-1 sm:mr-0 ${MOBILE_ORDER.goTo}`}
          >
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
          <button
            type="button"
            aria-label="Go to page"
            onClick={() => {
              setInputValue(String(page));
              setEditing(true);
            }}
            className={`mx-1 min-h-10 rounded border border-border px-3 text-sm text-muted hover:bg-card hover:text-foreground sm:mr-0 sm:h-7 sm:min-h-0 sm:px-2 sm:text-xs ${MOBILE_ORDER.goTo}`}
            title="Go to page…"
          >
            Go to…
          </button>
        ))}
    </>
  );
}

// Always returns exactly VISIBLE_SLOTS entries when the list has more pages than that, so the
// number block keeps a constant width as the current page changes. ELLIPSIS marks a gap.
function getPageNumbers(current: number, total: number): number[] {
  if (total <= VISIBLE_SLOTS) return Array.from({ length: total }, (_, index) => index + 1);
  if (current <= 4) return [1, 2, 3, 4, 5, ELLIPSIS, total];
  if (current >= total - 3) return [1, ELLIPSIS, total - 4, total - 3, total - 2, total - 1, total];
  return [1, ELLIPSIS, current - 1, current, current + 1, ELLIPSIS, total];
}
