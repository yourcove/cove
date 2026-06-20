import { useEffect, useRef, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { savedFilters } from "../api/client";
import type { FindFilter } from "../api/types";
import { Bookmark, ChevronDown, Save, Trash2, Loader2, Star } from "lucide-react";
import { readAuthenticatedUserDefaultFilter, updateAuthenticatedUserUiPreferences } from "../utils/userUiPreferences";

const DEFAULT_SORT_BY_MODE: Record<string, string> = {
  videos: "date",
  images: "date",
  galleries: "date",
  groups: "date",
  audios: "date",
  texts: "date",
  performers: "latest_video_date",
  studios: "latest_video_date",
  tags: "latest_video_date",
};

// Random sort seeds are intentionally not persisted: a saved/default filter using random sort
// should re-shuffle on every load rather than reproduce the same order. Drop the seed on save.
function stripRandomSeed(findFilter: FindFilter): FindFilter {
  if (findFilter.sort === "random" && findFilter.seed != null) {
    const { seed: _seed, ...rest } = findFilter;
    return rest;
  }
  return findFilter;
}

function normalizeSavedFindFilter(mode: string, findFilter: FindFilter | undefined): FindFilter | undefined {
  if (!findFilter) return findFilter;
  const defaultSort = DEFAULT_SORT_BY_MODE[mode];
  if (!defaultSort) return findFilter;
  return {
    ...findFilter,
    sort: findFilter.sort ?? defaultSort,
    direction: findFilter.direction ?? "desc",
  };
}

/**
 * Get the default filter for a mode. Prefers the user's account-stored default (follows them across
 * browsers); falls back to the browser-local value for signed-out use and migration.
 */
export function getDefaultFilter(mode: string): { findFilter?: FindFilter; objectFilter?: Record<string, unknown>; uiOptions?: Record<string, unknown> } | null {
  try {
    const raw = readAuthenticatedUserDefaultFilter(mode) ?? localStorage.getItem(`cove-default-filter-${mode}`);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as { findFilter?: FindFilter; objectFilter?: Record<string, unknown>; uiOptions?: Record<string, unknown> };
    return { ...parsed, findFilter: normalizeSavedFindFilter(mode, parsed.findFilter) };
  } catch { return null; }
}

/**
 * Applies a mode's default saved filter exactly once on mount. Lets embedded list views inside
 * detail pages (a performer's videos, a studio's galleries, …) honor the user's default the same
 * way the top-level list pages do. `apply` receives the default's findFilter/objectFilter (if any).
 */
export function useDefaultSavedFilterOnMount(
  mode: string,
  apply: (findFilter: FindFilter | undefined, objectFilter: Record<string, unknown> | undefined) => void,
) {
  const appliedRef = useRef(false);
  useEffect(() => {
    if (appliedRef.current) return;
    appliedRef.current = true;
    const def = getDefaultFilter(mode);
    if (def) apply(def.findFilter, def.objectFilter);
    // Intentionally mount-only: the default is a starting point the user can then change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
}

/** Set the default filter for a mode (account-backed when signed in, plus a browser-local fallback). */
function setDefaultFilter(mode: string, findFilter: FindFilter, objectFilter?: Record<string, unknown>, uiOptions?: Record<string, unknown>) {
  const json = JSON.stringify({ findFilter: stripRandomSeed(findFilter), objectFilter, uiOptions });
  const key = mode.trim().toLowerCase();
  localStorage.setItem(`cove-default-filter-${mode}`, json);
  updateAuthenticatedUserUiPreferences((current) => ({
    ...(current ?? {}),
    defaultFilters: { ...(current?.defaultFilters ?? {}), [key]: json },
  }));
}

/** Clear the default filter for a mode (both account-backed and browser-local copies). */
function clearDefaultFilter(mode: string) {
  const key = mode.trim().toLowerCase();
  localStorage.removeItem(`cove-default-filter-${mode}`);
  updateAuthenticatedUserUiPreferences((current) => {
    if (!current?.defaultFilters || !(key in current.defaultFilters)) return current;
    const next = { ...current.defaultFilters };
    delete next[key];
    return { ...current, defaultFilters: Object.keys(next).length > 0 ? next : null };
  });
}

interface SavedFilterMenuProps {
  mode: string;
  /**
   * Storage key for the *default* (auto-applied) filter. Defaults to `mode`. Pass a distinct value to
   * give a view its own default that doesn't collide with another view sharing the same `mode` — e.g.
   * the images list inside a gallery shares the "images" named-filter library but wants its own default.
   */
  defaultFilterKey?: string;
  currentFilter: FindFilter;
  currentObjectFilter?: Record<string, unknown>;
  currentUIOptions?: Record<string, unknown>;
  onApplyFilter: (filter: FindFilter) => void;
  onApplyObjectFilter?: (filter: Record<string, unknown>) => void;
  onApplyUIOptions?: (options: Record<string, unknown>) => void;
}

export function SavedFilterMenu({
  mode,
  defaultFilterKey,
  currentFilter,
  currentObjectFilter,
  currentUIOptions,
  onApplyFilter,
  onApplyObjectFilter,
  onApplyUIOptions,
}: SavedFilterMenuProps) {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [saveName, setSaveName] = useState("");
  const [showSave, setShowSave] = useState(false);
  // Named saved filters are keyed by `mode` (server-side, enum-validated); the auto-applied default
  // is keyed separately so views sharing a `mode` can still keep independent defaults.
  const defaultKey = defaultFilterKey ?? mode;
  const hasDefault = !!getDefaultFilter(defaultKey);

  const { data: filters } = useQuery({
    queryKey: ["saved-filters", mode],
    queryFn: () => savedFilters.list(mode),
  });

  const createMut = useMutation({
    mutationFn: () =>
      savedFilters.create({
        mode,
        name: saveName,
        findFilter: JSON.stringify(stripRandomSeed(currentFilter)),
        objectFilter: currentObjectFilter && Object.keys(currentObjectFilter).length > 0 ? JSON.stringify(currentObjectFilter) : undefined,
        uiOptions: currentUIOptions && Object.keys(currentUIOptions).length > 0 ? JSON.stringify(currentUIOptions) : undefined,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["saved-filters", mode] });
      setSaveName("");
      setShowSave(false);
    },
  });

  const deleteMut = useMutation({
    mutationFn: (id: number) => savedFilters.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["saved-filters", mode] });
    },
  });

  const applyFilter = (findFilterJson: string | undefined, objectFilterJson?: string, uiOptionsJson?: string) => {
    if (!findFilterJson) return;
    try {
      const parsed = normalizeSavedFindFilter(mode, JSON.parse(findFilterJson) as FindFilter);
      if (!parsed) return;
      onApplyFilter(parsed);
    } catch {
      // ignore invalid JSON
    }

    if (onApplyObjectFilter) {
      try {
        onApplyObjectFilter(objectFilterJson ? JSON.parse(objectFilterJson) as Record<string, unknown> : {});
      } catch {
        onApplyObjectFilter({});
      }
    }

    if (onApplyUIOptions && uiOptionsJson) {
      try {
        onApplyUIOptions(JSON.parse(uiOptionsJson) as Record<string, unknown>);
      } catch {
        // ignore invalid JSON
      }
    }

    setOpen(false);
  };

  return (
    <div className="relative">
      <button
        onClick={() => setOpen(!open)}
        className="flex items-center gap-1 rounded-lg border border-border bg-card/70 px-2 py-1 text-xs text-secondary hover:border-accent hover:text-foreground"
        title="Saved filters"
      >
        <Bookmark className="w-3.5 h-3.5" />
        <ChevronDown className="w-3 h-3" />
      </button>

      {open && (
        <div className="styled-dropdown-panel absolute top-full right-0 z-50 mt-1 w-56 rounded-lg border border-border shadow-lg">
          <div className="p-2 border-b border-border">
            <p className="text-[10px] text-muted uppercase tracking-wider font-medium">
              Saved Filters
            </p>
          </div>

          {/* Existing filters */}
          <div className="max-h-48 overflow-y-auto">
            {(!filters || filters.length === 0) && (
              <p className="px-3 py-2 text-xs text-muted">No saved filters</p>
            )}
            {filters?.map((f) => (
              <div
                key={f.id}
                className="group flex cursor-pointer items-center justify-between px-3 py-1.5 hover:bg-card/80"
              >
                <button
                  onClick={() => applyFilter(f.findFilter, f.objectFilter, f.uiOptions)}
                  className="text-xs text-foreground hover:text-accent truncate flex-1 text-left"
                >
                  {f.name}
                </button>
                <button
                  onClick={(e) => {
                    e.stopPropagation();
                    deleteMut.mutate(f.id);
                  }}
                  className="p-0.5 text-muted hover:text-red-400 opacity-0 group-hover:opacity-100 transition-opacity"
                >
                  <Trash2 className="w-3 h-3" />
                </button>
              </div>
            ))}
          </div>

          {/* Save current */}
          <div className="border-t border-border p-2 space-y-1.5">
            {/* Set/clear default filter */}
            <button
              onClick={() => { setDefaultFilter(defaultKey, currentFilter, currentObjectFilter, currentUIOptions); setOpen(false); }}
              className="flex items-center gap-1.5 text-xs text-secondary hover:text-yellow-400 w-full"
              title="Apply the current filter state automatically when opening this page"
            >
              <Star className="w-3 h-3" />
              Set current as default
            </button>
            {hasDefault && (
              <button
                onClick={() => { clearDefaultFilter(defaultKey); setOpen(false); }}
                className="flex items-center gap-1.5 text-xs text-muted hover:text-red-400 w-full"
              >
                <Star className="w-3 h-3" />
                Clear default filter
              </button>
            )}
            {showSave ? (
              <div className="flex gap-1">
                <input
                  type="text"
                  value={saveName}
                  onChange={(e) => setSaveName(e.target.value)}
                  onKeyDown={(e) => e.key === "Enter" && saveName && createMut.mutate()}
                  placeholder="Filter name..."
                  className="flex-1 rounded border border-border bg-card/70 px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
                  autoFocus
                />
                <button
                  onClick={() => saveName && createMut.mutate()}
                  disabled={!saveName || createMut.isPending}
                  className="px-2 py-1 rounded text-xs bg-accent text-white hover:bg-accent-hover disabled:opacity-60"
                >
                  {createMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Save className="w-3 h-3" />}
                </button>
              </div>
            ) : (
              <button
                onClick={() => setShowSave(true)}
                className="flex items-center gap-1.5 text-xs text-secondary hover:text-foreground w-full"
              >
                <Save className="w-3 h-3" />
                Save current filter
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

