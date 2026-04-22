import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { Search, ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, ArrowUpDown, LayoutGrid, List, Columns3, Grid3X3, ZoomIn, ZoomOut, SlidersHorizontal, X } from "lucide-react";
import type { FindFilter } from "../api/types";
import { tags as tagsApi, performers as performersApi, studios as studiosApi, groups as groupsApi } from "../api/client";
import { ExtensionSlot } from "../router/RouteRegistry";
import { SavedFilterMenu } from "./SavedFilterMenu";
import { FilterDialog, FilterButton, type CriterionDefinition } from "./FilterDialog";
import { useKeySequence } from "../hooks/useKeySequence";

export type DisplayMode = "grid" | "list" | "wall" | "tagger";

interface ListPageProps {
  title: string;
  pageKey?: string;
  filter: FindFilter;
  onFilterChange: (f: FindFilter) => void;
  totalCount: number;
  isLoading: boolean;
  children: ReactNode;
  sortOptions?: { value: string; label: string }[];
  displayMode?: DisplayMode;
  onDisplayModeChange?: (mode: DisplayMode) => void;
  availableDisplayModes?: DisplayMode[];
  selectedIds?: Set<number>;
  onSelectAll?: () => void;
  onSelectNone?: () => void;
  selectionActions?: ReactNode;
  metadataByline?: ReactNode;
  onNew?: () => void;
  renderOperations?: () => ReactNode;
  filterMode?: string;
  // Advanced filtering
  criteriaDefinitions?: CriterionDefinition[];
  objectFilter?: Record<string, unknown>;
  onObjectFilterChange?: (filter: Record<string, unknown>) => void;
  // Quick filter buttons (standard layout's criterion shortcut row)
  quickFilterIds?: string[];
  wallColumnCount?: number;
  onWallColumnCountChange?: (count: number) => void;
}

const PER_PAGE_OPTIONS = [20, 40, 60, 120, 250, 500, 1000];
const DEFAULT_ZOOM_LEVEL = 1;
const MIN_ZOOM_LEVEL = 0;
const MAX_ZOOM_LEVEL = 5;

function clampZoomLevel(value: number) {
  return Math.min(MAX_ZOOM_LEVEL, Math.max(MIN_ZOOM_LEVEL, value));
}

const CHIP_MODIFIER_LABELS: Record<string, string> = {
  EQUALS: "=",
  NOT_EQUALS: "≠",
  GREATER_THAN: ">",
  LESS_THAN: "<",
  INCLUDES: "Includes",
  EXCLUDES: "Excludes",
  INCLUDES_ALL: "Includes All",
  EXCLUDES_ALL: "Excludes All",
  IS_NULL: "Is Null",
  NOT_NULL: "Not Null",
  BETWEEN: "Between",
  NOT_BETWEEN: "Not Between",
  MATCHES_REGEX: "Regex",
  NOT_MATCHES_REGEX: "Not Regex",
};

function formatChipScalar(value: unknown): string {
  if (typeof value === "boolean") {
    return value ? "Yes" : "No";
  }

  if (value == null) {
    return "";
  }

  if (typeof value === "object") {
    const candidate = value as { label?: string; name?: string; title?: string; value?: string | number };
    return candidate.label ?? candidate.name ?? candidate.title ?? String(candidate.value ?? "");
  }

  return String(value);
}

function formatChipEntityId(value: unknown, nameMap?: Map<number, string>): string {
  if (typeof value === "number") {
    return nameMap?.get(value) ?? `#${value}`;
  }

  if (value && typeof value === "object") {
    const candidate = value as { id?: number | string; label?: string; name?: string; title?: string };
    if (candidate.label ?? candidate.name ?? candidate.title) {
      return (candidate.label ?? candidate.name ?? candidate.title)!;
    }
    if (candidate.id != null && typeof candidate.id === "number") {
      return nameMap?.get(candidate.id) ?? `#${candidate.id}`;
    }
    return candidate.id != null ? `#${candidate.id}` : "";
  }

  return String(value ?? "");
}

function formatFilterChipValue(def: CriterionDefinition | undefined, value: unknown, nameMap?: Map<number, string>): string {
  if (Array.isArray(value)) {
    return value.map((item) => formatChipScalar(item)).join(", ");
  }

  if (!value || typeof value !== "object") {
    return String(value ?? "");
  }

  const criterion = value as {
    value?: unknown;
    value2?: unknown;
    excludes?: unknown[];
    modifier?: string;
    depth?: number;
    _names?: Record<string, string>;
  };

  const modifier = criterion.modifier ? CHIP_MODIFIER_LABELS[criterion.modifier] ?? criterion.modifier : "";

  // Merge _names (embedded by filter editor) with nameMap (from entity queries) for best coverage
  const embeddedNames = criterion._names;
  const resolveEntityName = (id: unknown): string => {
    if (typeof id === "number") {
      // First check embedded names (always available), then nameMap (from queries)
      const name = embeddedNames?.[String(id)] ?? nameMap?.get(id);
      return name ?? `#${id}`;
    }
    return formatChipEntityId(id, nameMap);
  };

  if (def?.type === "multiId") {
    const included = Array.isArray(criterion.value)
      ? criterion.value.map((item) => resolveEntityName(item)).filter(Boolean).join(", ")
      : "";
    const excluded = Array.isArray(criterion.excludes)
      ? criterion.excludes.map((item) => resolveEntityName(item)).filter(Boolean).join(", ")
      : "";

    const parts = [
      included ? `${modifier} ${included}`.trim() : modifier,
      excluded ? `Except ${excluded}` : "",
      criterion.depth === -1 ? "with sub-tags" : "",
    ].filter(Boolean);

    return parts.join(" · ");
  }

  if (criterion.modifier === "IS_NULL" || criterion.modifier === "NOT_NULL") {
    return modifier;
  }

  const valueText = formatChipScalar(criterion.value);
  const value2Text = formatChipScalar(criterion.value2);

  if (criterion.modifier === "BETWEEN" || criterion.modifier === "NOT_BETWEEN") {
    return `${modifier} ${valueText} and ${value2Text}`.trim();
  }

  if (valueText) {
    return `${modifier} ${valueText}`.trim();
  }

  return JSON.stringify(value);
}

export function ListPage({
  title,
  pageKey,
  filter,
  onFilterChange,
  totalCount,
  isLoading,
  children,
  sortOptions,
  displayMode,
  onDisplayModeChange,
  availableDisplayModes,
  selectedIds,
  onSelectAll,
  onSelectNone,
  selectionActions,
  metadataByline,
  onNew,
  renderOperations,
  filterMode,
  criteriaDefinitions,
  objectFilter,
  onObjectFilterChange,
  quickFilterIds,
  wallColumnCount,
  onWallColumnCountChange,
}: ListPageProps) {
  const [searchText, setSearchText] = useState(filter.q ?? "");
  const [filterDialogOpen, setFilterDialogOpen] = useState(false);
  const [filterDialogPreselect, setFilterDialogPreselect] = useState<string | undefined>();
  const [zoomLevel, setZoomLevel] = useState(DEFAULT_ZOOM_LEVEL); // 0-5 range: 0=smallest (240px), 5=largest (540px)
  const restoredPrefsRef = useRef(false);

  // Determine which entity types are used in active filters for name resolution
  const activeEntityTypes = useMemo(() => {
    if (!objectFilter || !criteriaDefinitions) return new Set<string>();
    const types = new Set<string>();
    for (const key of Object.keys(objectFilter)) {
      const def = criteriaDefinitions.find((d) => d.id === key || d.filterKey === key);
      if (def?.type === "multiId" && def.entityType) types.add(def.entityType);
    }
    return types;
  }, [objectFilter, criteriaDefinitions]);

  // Fetch entity names for active multiId filters (uses same cache key as FilterDialog)
  const { data: tagEntities } = useQuery({
    queryKey: ["tags", "all"],
    queryFn: async () => (await tagsApi.find({ perPage: 5000, sort: "name", direction: "asc" })).items,
    staleTime: 60000,
    enabled: activeEntityTypes.has("tags"),
  });
  const { data: performerEntities } = useQuery({
    queryKey: ["performers", "all"],
    queryFn: async () => (await performersApi.find({ perPage: 5000, sort: "name", direction: "asc" })).items,
    staleTime: 60000,
    enabled: activeEntityTypes.has("performers"),
  });
  const { data: studioEntities } = useQuery({
    queryKey: ["studios", "all"],
    queryFn: async () => (await studiosApi.find({ perPage: 5000, sort: "name", direction: "asc" })).items,
    staleTime: 60000,
    enabled: activeEntityTypes.has("studios"),
  });
  const { data: groupEntities } = useQuery({
    queryKey: ["groups", "all"],
    queryFn: async () => (await groupsApi.find({ perPage: 5000, sort: "name", direction: "asc" })).items,
    staleTime: 60000,
    enabled: activeEntityTypes.has("groups"),
  });

  // Build name maps per entity type
  const entityNameMaps = useMemo(() => {
    const maps: Record<string, Map<number, string>> = {};
    const buildMap = (entities: any[] | undefined) => {
      const m = new Map<number, string>();
      if (entities) for (const e of entities) m.set(e.id, e.name || e.title || `#${e.id}`);
      return m;
    };
    if (tagEntities) maps.tags = buildMap(tagEntities);
    if (performerEntities) maps.performers = buildMap(performerEntities);
    if (studioEntities) maps.studios = buildMap(studioEntities);
    if (groupEntities) maps.groups = buildMap(groupEntities);
    return maps;
  }, [tagEntities, performerEntities, studioEntities, groupEntities]);
  const perPage = filter.perPage ?? 25;
  const page = filter.page ?? 1;
  const totalPages = Math.max(1, Math.ceil(totalCount / perPage));
  const start = (page - 1) * perPage + 1;
  const end = Math.min(page * perPage, totalCount);
  const sortedSortOptions = useMemo(
    () => (sortOptions ? [...sortOptions].sort((left, right) => left.label.localeCompare(right.label)) : undefined),
    [sortOptions]
  );
  const slotContext = { pageKey, title, filter, onFilterChange, totalCount, isLoading };
  const selecting = selectedIds && selectedIds.size > 0;
  const toolbarSegmentClass = "flex items-center gap-1 rounded-lg border border-border bg-card/70 px-1.5 py-1 shadow-sm";
  const toolbarSelectClass = "min-h-[30px] rounded-md border border-border/60 bg-input px-2 py-1 text-xs text-foreground shadow-inner focus:outline-none focus:border-accent";
  const toolbarIconButtonClass = "rounded-md border border-transparent p-1.5 text-secondary hover:bg-card/80 hover:text-foreground focus:outline-none focus:border-accent";

  useEffect(() => {
    if (!pageKey || restoredPrefsRef.current) {
      return;
    }

    restoredPrefsRef.current = true;

    try {
      const raw = localStorage.getItem(`cove-list-prefs-${pageKey}`);
      if (!raw) {
        return;
      }

      const parsed = JSON.parse(raw) as { perPage?: number; zoomLevel?: number; wallColumnCount?: number };
      if (typeof parsed.zoomLevel === "number") {
        setZoomLevel(clampZoomLevel(parsed.zoomLevel));
      }

      if (typeof parsed.wallColumnCount === "number" && onWallColumnCountChange) {
        onWallColumnCountChange(Math.min(12, Math.max(2, parsed.wallColumnCount)));
      }

      const hasPerPageOverride = new URLSearchParams(window.location.search).has("perPage");
      if (!hasPerPageOverride && typeof parsed.perPage === "number" && parsed.perPage > 0 && parsed.perPage !== perPage) {
        onFilterChange({ ...filter, perPage: parsed.perPage, page: 1 });
      }
    } catch {
      // Ignore invalid persisted list preferences.
    }
  }, [filter, onFilterChange, onWallColumnCountChange, pageKey, perPage]);

  useEffect(() => {
    if (!pageKey) {
      return;
    }

    localStorage.setItem(
      `cove-list-prefs-${pageKey}`,
      JSON.stringify({ perPage, zoomLevel: clampZoomLevel(zoomLevel), wallColumnCount })
    );
  }, [pageKey, perPage, wallColumnCount, zoomLevel]);

  useEffect(() => {
    setSearchText(filter.q ?? "");
  }, [filter.q]);

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault();
    onFilterChange({ ...filter, q: searchText || undefined, page: 1 });
  };

  const goTo = useCallback(
    (p: number) => onFilterChange({ ...filter, page: Math.max(1, Math.min(totalPages, p)) }),
    [filter, onFilterChange, totalPages]
  );

  // List-page keyboard shortcuts
  const listBindings = useMemo(() => [
    // "/" focuses search
    { keys: "/", action: () => { document.querySelector<HTMLInputElement>("input[placeholder='Filter...']")?.focus(); } },
    // View switching
    ...(onDisplayModeChange && availableDisplayModes ? [
      ...(availableDisplayModes.includes("grid") ? [{ keys: "v g", action: () => onDisplayModeChange("grid") }] : []),
      ...(availableDisplayModes.includes("list") ? [{ keys: "v l", action: () => onDisplayModeChange("list") }] : []),
      ...(availableDisplayModes.includes("wall") ? [{ keys: "v w", action: () => onDisplayModeChange("wall") }] : []),
      ...(availableDisplayModes.includes("tagger") ? [{ keys: "v t", action: () => onDisplayModeChange("tagger") }] : []),
    ] : []),
    // Selection
    ...(onSelectAll ? [{ keys: "s a", action: onSelectAll }] : []),
    ...(onSelectNone ? [{ keys: "s n", action: onSelectNone }] : []),
    // Pagination
    { keys: "ArrowLeft", action: () => goTo(page - 1) },
    { keys: "ArrowRight", action: () => goTo(page + 1) },
    { keys: "Shift+ArrowLeft", action: () => goTo(page - 10) },
    { keys: "Shift+ArrowRight", action: () => goTo(page + 10) },
    { keys: "Ctrl+Home", action: () => goTo(1) },
    { keys: "Ctrl+End", action: () => goTo(totalPages) },
    // Filter dialog
    ...(criteriaDefinitions && onObjectFilterChange ? [{ keys: "f", action: () => setFilterDialogOpen(true) }] : []),
    // Zoom
    { keys: "+", action: () => setZoomLevel((v) => Math.min(5, v + 0.25)) },
    { keys: "-", action: () => setZoomLevel((v) => Math.max(0, v - 0.25)) },
  ], [onDisplayModeChange, availableDisplayModes, onSelectAll, onSelectNone, goTo, page, totalPages, criteriaDefinitions, onObjectFilterChange]);

  useKeySequence(listBindings);

  // Set page title (e.g., "Scenes | Cove")
  useEffect(() => {
    document.title = `${title} | Cove`;
    return () => { document.title = "Cove"; };
  }, [title]);

  return (
    <div className="space-y-0">
      {/* Toolbar - matches standard FilteredListToolbar */}
      <div className="sticky top-0 z-30 mx-1 mt-1 flex flex-wrap items-center gap-2 rounded-xl border border-border bg-surface/90 px-2.5 py-2 shadow-sm shadow-black/20">
        {/* Title + count + byline */}
        <div className="mr-auto flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 pr-2">
          <h1 className="text-sm font-semibold text-foreground whitespace-nowrap">{title}</h1>
          <span className="text-xs text-muted hidden sm:inline">
            {totalCount > 0 ? `${start}-${end} of ${totalCount.toLocaleString()}` : "0 items"}
          </span>
          <span className="text-xs text-muted sm:hidden">
            {totalCount > 0 ? totalCount.toLocaleString() : "0"}
          </span>
          {metadataByline}
        </div>

        {/* Search */}
        <form onSubmit={handleSearch} className="relative shrink-0" style={{ width: "13rem", maxWidth: "100%" }}>
          <Search className="absolute left-2 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-muted" />
          <input
            type="text"
            value={searchText}
            onChange={(e) => setSearchText(e.target.value)}
            placeholder="Filter..."
            aria-label="Filter by title"
            className="w-full rounded-lg border border-border bg-card/70 pl-7 pr-3 py-1.5 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
          />
        </form>

        {/* Sort */}
        {sortedSortOptions && (
          <div className={toolbarSegmentClass}>
            <select
              value={filter.sort ?? ""}
              onChange={(e) => onFilterChange({ ...filter, sort: e.target.value || undefined, page: 1 })}
              className={`${toolbarSelectClass} min-w-[8.5rem] max-w-[10rem]`}
            >
              {sortedSortOptions.map((o) => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </select>

            {/* Direction toggle */}
            <button
              onClick={() => onFilterChange({ ...filter, direction: filter.direction === "desc" ? "asc" : "desc" })}
              className={toolbarIconButtonClass}
              title={filter.direction === "desc" ? "Sort descending" : "Sort ascending"}
            >
              <ArrowUpDown className="w-3.5 h-3.5" />
            </button>
          </div>
        )}

        {/* Saved filters */}
        {filterMode && (
          <SavedFilterMenu
            mode={filterMode}
            currentFilter={filter}
            currentObjectFilter={objectFilter}
            currentUIOptions={displayMode ? { displayMode } : undefined}
            onApplyFilter={onFilterChange}
            onApplyObjectFilter={onObjectFilterChange}
            onApplyUIOptions={(options) => {
              const mode = typeof options.displayMode === "string" ? options.displayMode : undefined;
              if (mode && onDisplayModeChange) onDisplayModeChange(mode as DisplayMode);
            }}
          />
        )}

        {/* Advanced filter button */}
        {criteriaDefinitions && onObjectFilterChange && (
          <FilterButton
            activeCount={Object.keys(objectFilter ?? {}).length}
            onClick={() => setFilterDialogOpen(true)}
          />
        )}

        {/* Display mode */}
        {onDisplayModeChange && availableDisplayModes && (
          <div className={`${toolbarSegmentClass} gap-0.5`}>
            {availableDisplayModes.includes("grid") && (
              <button
                onClick={() => onDisplayModeChange("grid")}
                className={`rounded-md p-1.5 ${displayMode === "grid" ? "bg-background/60 text-accent shadow-sm" : "text-secondary hover:bg-card/80 hover:text-foreground"}`}
                title="Grid"
              >
                <LayoutGrid className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("list") && (
              <button
                onClick={() => onDisplayModeChange("list")}
                className={`rounded-md p-1.5 ${displayMode === "list" ? "bg-background/60 text-accent shadow-sm" : "text-secondary hover:bg-card/80 hover:text-foreground"}`}
                title="List"
              >
                <List className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("wall") && (
              <button
                onClick={() => onDisplayModeChange("wall")}
                className={`rounded-md p-1.5 ${displayMode === "wall" ? "bg-background/60 text-accent shadow-sm" : "text-secondary hover:bg-card/80 hover:text-foreground"}`}
                title="Wall"
              >
                <Grid3X3 className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("tagger") && (
              <button
                onClick={() => onDisplayModeChange("tagger")}
                className={`rounded-md p-1.5 ${displayMode === "tagger" ? "bg-background/60 text-accent shadow-sm" : "text-secondary hover:bg-card/80 hover:text-foreground"}`}
                title="Tagger"
              >
                <Columns3 className="w-3.5 h-3.5" />
              </button>
            )}
          </div>
        )}

        {/* Per page */}
        <div className={toolbarSegmentClass}>
          <select
            value={perPage}
            onChange={(e) => onFilterChange({ ...filter, perPage: Number(e.target.value), page: 1 })}
            className={`${toolbarSelectClass} min-w-[4.75rem]`}
            title="Items per page"
          >
            {PER_PAGE_OPTIONS.map((n) => (
              <option key={n} value={n}>{n}</option>
            ))}
          </select>

          {/* Zoom slider (standard card size slider) */}
          {displayMode === "grid" && (
            <div className="flex items-center gap-1 pl-1">
              <ZoomOut className="w-3 h-3 text-muted" />
              <input
                type="range"
                min={0}
                max={5}
                step={0.25}
                value={zoomLevel}
                onChange={(e) => setZoomLevel(clampZoomLevel(Number(e.target.value)))}
                className="w-16 sm:w-20 h-1 accent-accent cursor-pointer"
                title={`Card size: ${Math.round(240 + zoomLevel * 60)}px`}
              />
              <ZoomIn className="w-3 h-3 text-muted" />
            </div>
          )}

          {displayMode === "wall" && wallColumnCount != null && onWallColumnCountChange && (
            <div className="flex items-center gap-1 pl-1">
              <input
                type="range"
                min={2}
                max={8}
                step={1}
                value={wallColumnCount}
                onChange={(e) => onWallColumnCountChange(Number(e.target.value))}
                className="w-16 sm:w-20 h-1 accent-accent cursor-pointer"
                title={`Wall columns: ${wallColumnCount}`}
              />
              <span className="min-w-[1rem] text-[10px] text-muted">{wallColumnCount}</span>
            </div>
          )}
        </div>

        {/* Operations */}
        <div className="ml-auto flex flex-wrap items-center justify-end gap-2">
          {renderOperations?.()}
          <ExtensionSlot slot="list-page-toolbar-end" context={slotContext} />
          {pageKey && <ExtensionSlot slot={`${pageKey}-list-toolbar-end`} context={slotContext} />}
          {onNew && (
            <button
              onClick={onNew}
              className="rounded-lg bg-accent px-3 py-1 text-xs font-medium text-white hover:bg-accent-hover"
            >
              + New
            </button>
          )}
        </div>
      </div>

      {/* Active filter tags (criterion badges) */}
      {objectFilter && onObjectFilterChange && criteriaDefinitions && Object.keys(objectFilter).length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 bg-surface/50 border border-border rounded-lg px-3 py-1.5 mx-1 mt-1">
          {Object.entries(objectFilter).map(([key, value]) => {
            const def = criteriaDefinitions.find((d) => d.id === key || d.filterKey === key);
            const label = def?.label ?? key;
            const nameMap = def?.entityType ? entityNameMaps[def.entityType] : undefined;
            const displayValue = formatFilterChipValue(def, value, nameMap);
            return (
              <button
                key={key}
                onClick={() => {
                  const next = { ...objectFilter };
                  delete next[key];
                  onObjectFilterChange(next);
                  onFilterChange({ ...filter, page: 1 });
                }}
                className="group flex items-center gap-1 rounded-full bg-card border border-border px-2.5 py-0.5 text-xs text-foreground hover:border-red-400 hover:text-red-300 transition-colors"
                title={`Remove filter: ${label}`}
              >
                <span className="text-muted">{label}:</span>
                <span className="max-w-[200px] truncate">{displayValue}</span>
                <X className="w-3 h-3 opacity-50 group-hover:opacity-100" />
              </button>
            );
          })}
          <button
            onClick={() => { onObjectFilterChange({}); onFilterChange({ ...filter, page: 1 }); }}
            className="text-xs text-muted hover:text-red-300"
          >
            Clear all
          </button>
        </div>
      )}

      {/* Selection bar */}
      {selecting && (
        <div className="flex items-center gap-3 bg-card/80 border border-border rounded-lg px-3 py-1.5 mx-1 mt-1">
          <span className="text-xs text-secondary">
            {selectedIds!.size} selected
          </span>
          <button onClick={onSelectAll} className="text-xs text-accent hover:underline">Select all</button>
          <button onClick={onSelectNone} className="text-xs text-secondary hover:text-foreground">Deselect all</button>
          {selectionActions}
        </div>
      )}

      {/* Pagination top */}
      {totalPages > 1 && (
        <div className="flex items-center justify-center gap-1 py-1 mx-1 mt-1">
          <PaginationControls page={page} totalPages={totalPages} goTo={goTo} />
        </div>
      )}

      {/* Content */}
      {isLoading ? (
        <div className="flex items-center justify-center h-64">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
        </div>
      ) : (
        <div className="pt-3" style={{ "--card-min-width": `${Math.round(240 + zoomLevel * 60)}px` } as React.CSSProperties}>
          {children}
        </div>
      )}

      {/* Pagination bottom */}
      {totalPages > 1 && (
        <div className="flex items-center justify-center gap-1 py-4">
          <PaginationControls page={page} totalPages={totalPages} goTo={goTo} />
        </div>
      )}

      {/* Filter Dialog */}
      {criteriaDefinitions && onObjectFilterChange && (
        <FilterDialog
          open={filterDialogOpen}
          onClose={() => { setFilterDialogOpen(false); setFilterDialogPreselect(undefined); }}
          criteria={criteriaDefinitions}
          activeFilter={objectFilter ?? {}}
          onApply={(f) => {
            onObjectFilterChange(f);
            onFilterChange({ ...filter, page: 1 });
          }}
          preselectCriterion={filterDialogPreselect}
        />
      )}
    </div>
  );
}

function PaginationControls({ page, totalPages, goTo }: { page: number; totalPages: number; goTo: (p: number) => void }) {
  const [editing, setEditing] = useState(false);
  const [inputValue, setInputValue] = useState(String(page));

  const handleSubmit = () => {
    const p = parseInt(inputValue, 10);
    if (!isNaN(p) && p >= 1 && p <= totalPages) goTo(p);
    setEditing(false);
  };

  return (
    <>
      <button onClick={() => goTo(1)} disabled={page <= 1} className="p-1 rounded hover:bg-card disabled:opacity-30 disabled:cursor-not-allowed text-secondary hover:text-foreground">
        <ChevronsLeft className="w-3.5 h-3.5" />
      </button>
      <button onClick={() => goTo(page - 1)} disabled={page <= 1} className="p-1 rounded hover:bg-card disabled:opacity-30 disabled:cursor-not-allowed text-secondary hover:text-foreground">
        <ChevronLeft className="w-3.5 h-3.5" />
      </button>
      {getPageNumbers(page, totalPages).map((p, i) =>
        p === -1 ? (
          <span key={`ellipsis-${i}`} className="px-1 text-muted text-xs">…</span>
        ) : (
          <button
            key={p}
            onClick={() => goTo(p)}
            className={`min-w-[28px] h-7 rounded text-xs font-medium ${
              p === page ? "bg-accent text-white" : "text-secondary hover:bg-card hover:text-foreground"
            }`}
          >
            {p}
          </button>
        )
      )}
      <button onClick={() => goTo(page + 1)} disabled={page >= totalPages} className="p-1 rounded hover:bg-card disabled:opacity-30 disabled:cursor-not-allowed text-secondary hover:text-foreground">
        <ChevronRight className="w-3.5 h-3.5" />
      </button>
      <button onClick={() => goTo(totalPages)} disabled={page >= totalPages} className="p-1 rounded hover:bg-card disabled:opacity-30 disabled:cursor-not-allowed text-secondary hover:text-foreground">
        <ChevronsRight className="w-3.5 h-3.5" />
      </button>
      {totalPages > 7 && (
        editing ? (
          <form onSubmit={(e) => { e.preventDefault(); handleSubmit(); }} className="ml-1 flex items-center gap-1">
            <input
              type="text"
              autoFocus
              value={inputValue}
              onChange={(e) => setInputValue(e.target.value)}
              onBlur={handleSubmit}
              className="w-12 h-7 rounded border border-border bg-input text-center text-xs text-foreground focus:outline-none focus:border-accent"
            />
          </form>
        ) : (
          <button onClick={() => { setInputValue(String(page)); setEditing(true); }} className="ml-1 h-7 px-2 rounded text-xs text-muted hover:text-foreground hover:bg-card border border-border" title="Go to page…">
            Go to…
          </button>
        )
      )}
    </>
  );
}

function getPageNumbers(current: number, total: number): number[] {
  if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1);
  const pages: number[] = [1];
  if (current > 3) pages.push(-1);
  for (let i = Math.max(2, current - 1); i <= Math.min(total - 1, current + 1); i++) pages.push(i);
  if (current < total - 2) pages.push(-1);
  pages.push(total);
  return pages;
}
