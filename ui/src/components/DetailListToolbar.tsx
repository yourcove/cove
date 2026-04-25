import { ArrowUpDown, ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, Search, ZoomIn, ZoomOut } from "lucide-react";
import type { FindFilter } from "../api/types";
import { useMemo, useState } from "react";
import { withSeededRandomSort } from "../utils/seededRandomSort";

const PER_PAGE_OPTIONS = [20, 40, 60, 120, 250];

interface DetailListToolbarProps {
  filter: FindFilter;
  onFilterChange: (f: FindFilter) => void;
  totalCount: number;
  sortOptions: { value: string; label: string }[];
  zoomLevel?: number;
  onZoomChange?: (level: number) => void;
  showSearch?: boolean;
  selectedCount?: number;
  onSelectAll?: () => void;
  onSelectNone?: () => void;
  selectionActions?: React.ReactNode;
}

export function DetailListToolbar({ filter, onFilterChange, totalCount, sortOptions, zoomLevel, onZoomChange, showSearch, selectedCount, onSelectAll, onSelectNone, selectionActions }: DetailListToolbarProps) {
  const page = filter.page ?? 1;
  const perPage = filter.perPage ?? 24;
  const totalPages = Math.max(1, Math.ceil(totalCount / perPage));
  const start = (page - 1) * perPage + 1;
  const end = Math.min(page * perPage, totalCount);
  const [searchText, setSearchText] = useState(filter.q ?? "");
  const sortedSortOptions = useMemo(
    () => [...sortOptions].sort((left, right) => left.label.localeCompare(right.label)),
    [sortOptions]
  );

  return (
    <div className="mx-auto max-w-7xl flex flex-wrap items-center gap-2 mb-4 text-sm">
      {/* Count */}
      <span className="text-xs text-muted mr-auto">
        {totalCount > 0 ? `${start}–${end} of ${totalCount}` : "0 items"}
      </span>

      {/* Selection */}
      {selectedCount !== undefined && selectedCount > 0 && (
        <div className="flex items-center gap-2 bg-card/80 border border-border rounded-lg px-2 py-1">
          <span className="text-xs text-secondary">{selectedCount} selected</span>
          {onSelectAll && <button onClick={onSelectAll} className="text-xs text-accent hover:underline">Select all</button>}
          {onSelectNone && <button onClick={onSelectNone} className="text-xs text-secondary hover:text-foreground">Deselect</button>}
          {selectionActions}
        </div>
      )}

      {/* Search */}
      {showSearch && (
        <form onSubmit={(e) => { e.preventDefault(); onFilterChange({ ...filter, q: searchText || undefined, page: 1 }); }} className="flex items-center gap-1">
          <div className="relative">
            <Search className="absolute left-2 top-1/2 -translate-y-1/2 w-3 h-3 text-muted" />
            <input
              type="text"
              value={searchText}
              onChange={(e) => setSearchText(e.target.value)}
              onBlur={() => { if (searchText !== (filter.q ?? "")) onFilterChange({ ...filter, q: searchText || undefined, page: 1 }); }}
              placeholder="Search…"
              className="w-32 rounded border border-border bg-input pl-6 pr-2 py-1 text-xs text-foreground placeholder:text-muted focus:outline-none focus:ring-1 focus:ring-accent"
            />
          </div>
        </form>
      )}

      {/* Sort */}
      <select
        value={filter.sort ?? sortedSortOptions[0]?.value ?? ""}
        onChange={(e) => onFilterChange(withSeededRandomSort(filter, { ...filter, sort: e.target.value, page: 1 }))}
        className="rounded border border-border bg-input px-2 py-1 text-xs text-foreground"
      >
        {sortedSortOptions.map((opt) => (
          <option key={opt.value} value={opt.value}>{opt.label}</option>
        ))}
      </select>

      {/* Direction */}
      <button
        onClick={() => onFilterChange(withSeededRandomSort(filter, { ...filter, direction: filter.direction === "asc" ? "desc" : "asc", page: 1 }))}
        className="rounded border border-border bg-input p-1 text-secondary hover:text-foreground transition-colors"
        title={filter.direction === "asc" ? "Ascending" : "Descending"}
      >
        <ArrowUpDown className="w-3.5 h-3.5" />
      </button>

      {/* Per page */}
      <select
        value={perPage}
        onChange={(e) => onFilterChange({ ...filter, perPage: Number(e.target.value), page: 1 })}
        className="rounded border border-border bg-input px-2 py-1 text-xs text-foreground"
      >
        {PER_PAGE_OPTIONS.map((n) => (
          <option key={n} value={n}>{n}</option>
        ))}
      </select>

      {/* Zoom */}
      {zoomLevel !== undefined && onZoomChange && (
        <div className="hidden md:flex items-center gap-1">
          <ZoomOut className="w-3 h-3 text-muted" />
          <input
            type="range"
            min={0} max={5} step={0.25}
            value={zoomLevel}
            onChange={(e) => onZoomChange(Number(e.target.value))}
            className="w-16 h-1 accent-accent cursor-pointer"
            title={`Card size: ${Math.round(240 + zoomLevel * 60)}px`}
          />
          <ZoomIn className="w-3 h-3 text-muted" />
        </div>
      )}

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="flex items-center gap-0.5">
          <button disabled={page <= 1} onClick={() => onFilterChange({ ...filter, page: 1 })}
            className="p-1 rounded text-secondary hover:text-foreground disabled:opacity-30 disabled:cursor-not-allowed">
            <ChevronsLeft className="w-3.5 h-3.5" />
          </button>
          <button disabled={page <= 1} onClick={() => onFilterChange({ ...filter, page: page - 1 })}
            className="p-1 rounded text-secondary hover:text-foreground disabled:opacity-30 disabled:cursor-not-allowed">
            <ChevronLeft className="w-3.5 h-3.5" />
          </button>
          <span className="px-2 text-xs text-muted">{page} / {totalPages}</span>
          <button disabled={page >= totalPages} onClick={() => onFilterChange({ ...filter, page: page + 1 })}
            className="p-1 rounded text-secondary hover:text-foreground disabled:opacity-30 disabled:cursor-not-allowed">
            <ChevronRight className="w-3.5 h-3.5" />
          </button>
          <button disabled={page >= totalPages} onClick={() => onFilterChange({ ...filter, page: totalPages })}
            className="p-1 rounded text-secondary hover:text-foreground disabled:opacity-30 disabled:cursor-not-allowed">
            <ChevronsRight className="w-3.5 h-3.5" />
          </button>
        </div>
      )}
    </div>
  );
}
