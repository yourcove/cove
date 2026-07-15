import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, Tags, FolderTree, Grid3X3, LayoutGrid, List, MonitorPlay, Rows3, Search, Share2, Shuffle, ZoomIn, ZoomOut } from "lucide-react";
import type { FindFilter } from "../api/types";
import { isValidElement, useEffect, useMemo, useState, type CSSProperties } from "react";
import { clampEntityCardSizeLevel, getEntityCardMaxLevel, getEntityCardMinWidthPx, useEntityCardSize } from "../hooks/useEntityCardSize";
import { reshuffleRandomSort, withSeededRandomSort } from "../utils/seededRandomSort";
import { toolbarIconButtonClass, toolbarSegmentClass, toolbarSelectClass } from "./listToolbarStyles";
import { FilterButton, FilterDialog, type CriterionDefinition } from "./FilterDialog";
import { PageSizeSelect } from "./PageSizeSelect";
import { SavedFilterMenu, useDefaultSavedFilterOnMount } from "./SavedFilterMenu";
import { ActiveObjectFilterChips } from "./ActiveObjectFilterChips";

export type DetailListDisplayMode = "grid" | "list" | "wall" | "tagger" | "graph" | "byGroup" | "feed" | "vertical";

const DISPLAY_MODE_BUTTONS: Array<{ mode: DetailListDisplayMode; title: string; icon: React.ReactNode }> = [
  { mode: "grid", title: "Grid", icon: <LayoutGrid className="h-3.5 w-3.5" /> },
  { mode: "list", title: "List", icon: <List className="h-3.5 w-3.5" /> },
  { mode: "wall", title: "Wall", icon: <Grid3X3 className="h-3.5 w-3.5" /> },
  { mode: "tagger", title: "Tagger", icon: <Tags className="h-3.5 w-3.5" /> },
  { mode: "graph", title: "Graph/Tree", icon: <Share2 className="h-3.5 w-3.5" /> },
  { mode: "byGroup", title: "By Group", icon: <FolderTree className="h-3.5 w-3.5" /> },
  { mode: "feed", title: "Feed", icon: <Rows3 className="h-3.5 w-3.5" /> },
  { mode: "vertical", title: "Vertical Viewer", icon: <MonitorPlay className="h-3.5 w-3.5" /> },
];

interface DetailListToolbarProps {
  filter: FindFilter;
  onFilterChange: (f: FindFilter) => void;
  totalCount: number;
  sortOptions: { value: string; label: string }[];
  zoomLevel?: number;
  onZoomChange?: (level: number) => void;
  cardSizeEntityType?: string;
  showSearch?: boolean;
  showSort?: boolean;
  selectedCount?: number;
  onSelectAll?: () => void;
  onSelectAllMatching?: () => void;
  onSelectNone?: () => void;
  selectAllLabel?: string;
  selectAllPending?: boolean;
  selectAllMatchingLabel?: string;
  selectAllMatchingPending?: boolean;
  selectionActions?: React.ReactNode;
  displayMode?: DetailListDisplayMode;
  onDisplayModeChange?: (mode: DetailListDisplayMode) => void;
  availableDisplayModes?: DetailListDisplayMode[];
  criteriaDefinitions?: CriterionDefinition[];
  objectFilter?: Record<string, unknown>;
  onObjectFilterChange?: (filter: Record<string, unknown>) => void;
  allowInfinitePageSize?: boolean;
  infinitePageSizeOnly?: boolean;
  showPagingControls?: boolean;
  // When set (e.g. "videos"), shows the saved-filter menu so embedded lists inside detail pages
  // (a performer's videos, a studio's galleries, …) can save, apply and default-pin filters too.
  filterMode?: string;
  // Optional separate storage key for the auto-applied default filter (defaults to filterMode). Lets an
  // embedded list keep its own default while still sharing filterMode's named-filter library.
  filterDefaultKey?: string;
  // URL-backed detail lists resolve their saved default before the first query and must not apply it again after mount.
  defaultFilterResolved?: boolean;
}

export function DetailListToolbar({ filter, onFilterChange, totalCount, sortOptions, zoomLevel, onZoomChange, cardSizeEntityType, showSearch, showSort = true, selectedCount, onSelectAll, onSelectAllMatching, onSelectNone, selectAllLabel = "Select all", selectAllPending = false, selectAllMatchingLabel = "Select all matching", selectAllMatchingPending, selectionActions, displayMode, onDisplayModeChange, availableDisplayModes, criteriaDefinitions, objectFilter, onObjectFilterChange, allowInfinitePageSize = false, infinitePageSizeOnly = false, showPagingControls = true, filterMode, filterDefaultKey, defaultFilterResolved = false }: DetailListToolbarProps) {
  const page = filter.page ?? 1;
  const perPage = filter.perPage ?? 24;
  // Random sort with no seed (e.g. a default saved filter, or a re-mounted detail-page list) would
  // otherwise hit the backend's fixed fallback seed and return the *same* "random" order every time.
  // Mint a seed once so embedded lists re-shuffle on mount, matching the top-level list pages.
  useEffect(() => {
    if (filter.sort === "random" && filter.seed == null) {
      onFilterChange(reshuffleRandomSort(filter));
    }
    // Only react to the sort/seed pair; reshuffle sets a seed which clears this condition.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filter.sort, filter.seed]);
  const infinitePageSize = allowInfinitePageSize && (perPage === 0 || infinitePageSizeOnly);
  const effectivePerPage = infinitePageSize ? Math.max(totalCount, 1) : perPage;
  const totalPages = Math.max(1, Math.ceil(totalCount / effectivePerPage));
  // A persisted page (URL/state) can outlive the list it belongs to — e.g. the list shrinks after a
  // filter change, or a remembered page is restored for a shorter list. Clamp it for all rendering so
  // the range label stays sane ("25–15 of 15") and the pager never disappears while stranded out of range.
  const clampedPage = Math.min(Math.max(1, page), totalPages);
  const start = totalCount > 0 ? (infinitePageSize ? 1 : (clampedPage - 1) * effectivePerPage + 1) : 0;
  const end = infinitePageSize ? totalCount : Math.min(clampedPage * effectivePerPage, totalCount);
  const [searchText, setSearchText] = useState(filter.q ?? "");
  const [filterDialogOpen, setFilterDialogOpen] = useState(false);
  const sortedSortOptions = useMemo(
    () => [...sortOptions].sort((left, right) => left.label.localeCompare(right.label)),
    [sortOptions]
  );
  const selectionActionEntityType = useMemo(() => {
    if (!isValidElement<{ entityType?: string }>(selectionActions)) return undefined;
    return selectionActions.props.entityType;
  }, [selectionActions]);
  const inferredCardSizeEntityType = useMemo(() => cardSizeEntityType ?? selectionActionEntityType ?? inferCardSizeEntityType(sortOptions), [cardSizeEntityType, selectionActionEntityType, sortOptions]);
  const maxZoomLevel = getEntityCardMaxLevel(inferredCardSizeEntityType);
  const [storedZoomLevel, setStoredZoomLevel] = useEntityCardSize(inferredCardSizeEntityType);
  const effectiveZoomLevel = inferredCardSizeEntityType ? storedZoomLevel : zoomLevel;
  const displayModes = availableDisplayModes ?? (displayMode && onDisplayModeChange ? ["grid", "list"] as DetailListDisplayMode[] : []);

  useEffect(() => {
    if (!inferredCardSizeEntityType || zoomLevel == null || !onZoomChange) return;
    if (Math.abs(storedZoomLevel - zoomLevel) > 0.001) onZoomChange(storedZoomLevel);
  }, [inferredCardSizeEntityType, onZoomChange, storedZoomLevel, zoomLevel]);

  // Correct an out-of-range page back into the filter so the stale value doesn't persist (e.g. in the
  // URL) and re-strand the user next time. Guarded on a loaded count so a transient 0 during fetch
  // doesn't reset a deep-linked page before its data arrives.
  useEffect(() => {
    if (totalCount > 0 && clampedPage !== page) {
      onFilterChange({ ...filter, page: clampedPage });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clampedPage, page, totalCount]);

  const handleZoomChange = (level: number) => {
    const nextLevel = clampEntityCardSizeLevel(inferredCardSizeEntityType, level);
    if (inferredCardSizeEntityType) setStoredZoomLevel(nextLevel);
    onZoomChange?.(nextLevel);
  };

  const handleDisplayModeChange = (mode: DetailListDisplayMode) => {
    onDisplayModeChange?.(mode);
    if (allowInfinitePageSize && (mode === "feed" || mode === "vertical") && !infinitePageSize) {
      onFilterChange({ ...filter, perPage: 0, page: 1 });
    }
  };

  const goTo = (nextPage: number) => onFilterChange({ ...filter, page: Math.max(1, Math.min(totalPages, nextPage)) });
  const activeObjectFilter = objectFilter ?? {};

  // Any embedded list that exposes the saved-filter menu must also honor that mode's default.
  // Keep the surrounding entity constraint outside FindFilter and always start on the first page.
  useDefaultSavedFilterOnMount(filterDefaultKey ?? filterMode ?? "", (findFilter, defaultObjectFilter, defaultUIOptions) => {
    if (!filterMode || defaultFilterResolved) return;
    if (findFilter) onFilterChange({ ...filter, ...findFilter, page: 1 });
    if (defaultObjectFilter && Object.keys(defaultObjectFilter).length > 0) onObjectFilterChange?.(defaultObjectFilter);
    const defaultDisplayMode = typeof defaultUIOptions?.displayMode === "string" ? defaultUIOptions.displayMode : undefined;
    if (defaultDisplayMode) onDisplayModeChange?.(defaultDisplayMode as DetailListDisplayMode);
  });

  return (
    <>
      <div className="mx-auto mb-2 flex max-w-7xl flex-wrap items-center gap-2 rounded-xl border border-border bg-surface/90 px-3 py-3 text-sm shadow-sm shadow-black/20 sm:px-2.5 sm:py-2">
        <div className="mr-auto flex min-w-0 items-center gap-2 pr-2">
          <span className="text-xs text-muted">
            {totalCount > 0 ? `${start}–${end} of ${totalCount.toLocaleString()}` : "0 items"}
          </span>
        </div>

        {showSearch && (
          <form onSubmit={(event) => { event.preventDefault(); onFilterChange({ ...filter, q: searchText || undefined, page: 1 }); }} className="flex w-full shrink-0 items-center gap-1 sm:max-w-[18rem]">
            <div className="relative min-w-0 flex-1">
              <Search className="absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted" />
              <input
                type="text"
                value={searchText}
                onChange={(e) => setSearchText(e.target.value)}
                onBlur={() => { if (searchText !== (filter.q ?? "")) onFilterChange({ ...filter, q: searchText || undefined, page: 1 }); }}
                placeholder="Search…"
                className="min-h-10 w-full rounded-lg border border-border bg-card/70 py-2 pl-8 pr-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none sm:min-h-[30px] sm:py-1.5 sm:pl-7 sm:text-xs"
              />
            </div>
          </form>
        )}

        {showSort && (
          <div className={toolbarSegmentClass}>
            <select
              value={filter.sort ?? sortedSortOptions[0]?.value ?? ""}
              onChange={(e) => onFilterChange(withSeededRandomSort(filter, { ...filter, sort: e.target.value, page: 1 }))}
              className={`${toolbarSelectClass} min-w-[8.5rem] max-w-[10rem]`}
            >
              {sortedSortOptions.map((opt) => (
                <option key={opt.value} value={opt.value}>{opt.label}</option>
              ))}
            </select>
            {filter.sort === "random" ? (
              <button
                type="button"
                onClick={() => onFilterChange(reshuffleRandomSort(filter))}
                className={toolbarIconButtonClass}
                title="Shuffle"
                aria-label="Shuffle"
              >
                <Shuffle className="w-3.5 h-3.5" />
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => onFilterChange(withSeededRandomSort(filter, { ...filter, direction: filter.direction === "asc" ? "desc" : "asc", page: 1 }))}
              className={toolbarIconButtonClass}
              title={filter.direction === "asc" ? "Ascending" : "Descending"}
            >
              {filter.direction === "desc" ? <ArrowDown className="w-3.5 h-3.5" /> : <ArrowUp className="w-3.5 h-3.5" />}
            </button>
          </div>
        )}

        {criteriaDefinitions && onObjectFilterChange ? (
          <FilterButton activeCount={Object.keys(activeObjectFilter).length} onClick={() => setFilterDialogOpen(true)} />
        ) : null}

        {filterMode ? (
          <SavedFilterMenu
            mode={filterMode}
            defaultFilterKey={filterDefaultKey}
            currentFilter={filter}
            currentObjectFilter={activeObjectFilter}
            currentUIOptions={displayMode ? { displayMode } : undefined}
            onApplyFilter={(nextFilter) => onFilterChange(withSeededRandomSort(filter, { ...nextFilter, page: 1 }))}
            onApplyObjectFilter={onObjectFilterChange}
            onApplyUIOptions={(options) => {
              const nextDisplayMode = typeof options.displayMode === "string" ? options.displayMode : undefined;
              if (nextDisplayMode) onDisplayModeChange?.(nextDisplayMode as DetailListDisplayMode);
            }}
          />
        ) : null}

        {displayMode && onDisplayModeChange ? (
          <div className={`${toolbarSegmentClass} gap-0.5`}>
            {DISPLAY_MODE_BUTTONS.filter((button) => displayModes.includes(button.mode)).map((button) => (
              <button
                key={button.mode}
                type="button"
                onClick={() => handleDisplayModeChange(button.mode)}
                className={`${toolbarIconButtonClass} ${displayMode === button.mode ? "bg-background/60 text-accent shadow-sm" : ""}`}
                title={button.title}
                aria-label={button.title}
              >
                {button.icon}
              </button>
            ))}
          </div>
        ) : null}

        <div className={toolbarSegmentClass}>
          <PageSizeSelect
            perPage={perPage}
            allowInfinite={allowInfinitePageSize}
            infinitePageSize={infinitePageSize}
            infinitePageSizeOnly={infinitePageSizeOnly}
            onChange={(nextPerPage) => onFilterChange({ ...filter, perPage: nextPerPage, page: 1 })}
          />

          {effectiveZoomLevel !== undefined && onZoomChange && (
            <div className="hidden items-center gap-1 pl-1 md:flex">
              <ZoomOut className="w-3 h-3 text-muted" />
              <input
                type="range"
                min={0} max={maxZoomLevel} step={0.25}
                value={effectiveZoomLevel}
                onChange={(e) => handleZoomChange(Number(e.target.value))}
                style={{ "--range-fill": `${(effectiveZoomLevel / Math.max(0.25, maxZoomLevel)) * 100}%` } as CSSProperties}
                className="themed-range-input h-1 w-16 cursor-pointer sm:w-20"
                title={`Card size: ${getEntityCardMinWidthPx(inferredCardSizeEntityType, effectiveZoomLevel)}px`}
              />
              <ZoomIn className="w-3 h-3 text-muted" />
            </div>
          )}
        </div>
      </div>

      {criteriaDefinitions && onObjectFilterChange && Object.keys(activeObjectFilter).length > 0 ? (
        <ActiveObjectFilterChips
          criteriaDefinitions={criteriaDefinitions}
          objectFilter={activeObjectFilter}
          className="mb-2"
          onRemove={(key) => {
            const next = { ...activeObjectFilter };
            delete next[key];
            onObjectFilterChange(next);
            onFilterChange({ ...filter, page: 1 });
          }}
          onClearAll={() => {
            onObjectFilterChange({});
            onFilterChange({ ...filter, page: 1 });
          }}
        />
      ) : null}

      {selectedCount !== undefined && selectedCount > 0 && (
        <div className="mx-auto mb-2 flex max-w-7xl flex-wrap items-center gap-3 rounded-lg border border-border bg-card/80 px-3 py-1.5">
          <span className="text-xs text-secondary">{selectedCount} selected</span>
          {onSelectAll && <button onClick={onSelectAll} disabled={selectAllPending} className="text-xs text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-60">{selectAllPending ? "Selecting..." : selectAllLabel}</button>}
          {onSelectAllMatching && (
            <button onClick={onSelectAllMatching} disabled={selectAllMatchingPending} className="text-xs text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-60">
              {selectAllMatchingPending ? "Selecting..." : selectAllMatchingLabel}
            </button>
          )}
          {onSelectNone && <button onClick={onSelectNone} className="text-xs text-secondary hover:text-foreground">Deselect all</button>}
          {selectionActions}
        </div>
      )}

      {showPagingControls && !infinitePageSize && totalPages > 1 && (
        <div className="mx-auto mb-4 flex max-w-7xl flex-wrap items-center justify-center gap-1 py-1">
          <button disabled={clampedPage <= 1} onClick={() => goTo(1)}
            className={`${toolbarIconButtonClass} disabled:cursor-not-allowed disabled:opacity-30`}>
            <ChevronsLeft className="w-3.5 h-3.5" />
          </button>
          <button disabled={clampedPage <= 1} onClick={() => goTo(clampedPage - 1)}
            className={`${toolbarIconButtonClass} disabled:cursor-not-allowed disabled:opacity-30`}>
            <ChevronLeft className="w-3.5 h-3.5" />
          </button>
          <span className="px-2 text-xs text-muted">{clampedPage} / {totalPages}</span>
          <button disabled={clampedPage >= totalPages} onClick={() => goTo(clampedPage + 1)}
            className={`${toolbarIconButtonClass} disabled:cursor-not-allowed disabled:opacity-30`}>
            <ChevronRight className="w-3.5 h-3.5" />
          </button>
          <button disabled={clampedPage >= totalPages} onClick={() => goTo(totalPages)}
            className={`${toolbarIconButtonClass} disabled:cursor-not-allowed disabled:opacity-30`}>
            <ChevronsRight className="w-3.5 h-3.5" />
          </button>
        </div>
      )}
      {criteriaDefinitions && onObjectFilterChange ? (
        <FilterDialog
          open={filterDialogOpen}
          onClose={() => setFilterDialogOpen(false)}
          criteria={criteriaDefinitions}
          activeFilter={activeObjectFilter}
          onApply={(nextFilter) => {
            onObjectFilterChange(nextFilter);
            onFilterChange({ ...filter, page: 1 });
          }}
        />
      ) : null}
    </>
  );
}

function inferCardSizeEntityType(sortOptions?: { value: string; label: string }[]) {
  const values = new Set((sortOptions ?? []).map((option) => option.value));
  if (values.has("framerate") || values.has("bitrate") || values.has("play_duration") || values.has("performer_age")) return "videos";
  if (values.has("measurements") || values.has("birthdate") || values.has("career_length")) return "performers";
  if (values.has("image_count") && values.has("path")) return "galleries";
  return undefined;
}
