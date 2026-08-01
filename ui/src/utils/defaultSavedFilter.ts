import type { FindFilter, SavedFilterUIOptions } from "../api/types";
import { readAuthenticatedUserDefaultFilter } from "./userUiPreferences";

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

export function normalizeSavedFindFilter(mode: string, findFilter: FindFilter | undefined): FindFilter | undefined {
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
export function getDefaultFilter(mode: string): { findFilter?: FindFilter; objectFilter?: Record<string, unknown>; uiOptions?: SavedFilterUIOptions } | null {
  try {
    const raw = readAuthenticatedUserDefaultFilter(mode) ?? localStorage.getItem(`cove-default-filter-${mode}`);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as { findFilter?: FindFilter; objectFilter?: Record<string, unknown>; uiOptions?: SavedFilterUIOptions };
    return { ...parsed, findFilter: normalizeSavedFindFilter(mode, parsed.findFilter) };
  } catch { return null; }
}

export function resolveSavedDisplayMode<TDisplayMode extends string>(
  uiOptions: SavedFilterUIOptions | undefined,
  allowedDisplayModes: readonly TDisplayMode[],
  fallback: TDisplayMode,
): TDisplayMode {
  const displayMode = uiOptions?.displayMode;
  return typeof displayMode === "string" && allowedDisplayModes.includes(displayMode as TDisplayMode)
    ? displayMode as TDisplayMode
    : fallback;
}
