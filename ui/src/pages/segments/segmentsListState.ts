import type { FindFilter } from "../../api/types";
import type { DisplayMode } from "../../components/ListPage";
import { getDefaultFilter } from "../../components/SavedFilterMenu";
import { useListUrlState } from "../../hooks/useListUrlState";
import type { SegmentsPageContentView } from "./types";

export const DEFAULT_SEGMENT_FILTER: FindFilter = { page: 1, perPage: 24, sort: "updated_at", direction: "desc" };

export function segmentFilterMode(view: SegmentsPageContentView) {
  return view === "raw" ? "rawsegments" : "segments";
}

export interface SegmentsListDefaults {
  filter: FindFilter;
  objectFilter: Record<string, unknown>;
  displayMode: DisplayMode;
  profileId?: number;
}

/**
 * The Segments list state a URL leaves out: the view's saved default filter, which the URL only overrides,
 * and the display profile, from the URL's `profile` parameter or the saved filter. Read the same way by the
 * Segments page and by segment playback, so a URL means the same list on both.
 */
export function readSegmentsListDefaults(view: SegmentsPageContentView): SegmentsListDefaults {
  const saved = getDefaultFilter(segmentFilterMode(view));
  const savedDisplayMode = saved?.uiOptions?.displayMode;
  const routeProfileId = Number(new URLSearchParams(window.location.search).get("profile"));
  return {
    filter: saved?.findFilter ?? DEFAULT_SEGMENT_FILTER,
    objectFilter: saved?.objectFilter ?? {},
    displayMode: savedDisplayMode === "list" ? "list" : "grid",
    profileId:
      Number.isInteger(routeProfileId) && routeProfileId > 0
        ? routeProfileId
        : typeof saved?.uiOptions?.profileId === "number"
          ? saved.uiOptions.profileId
          : undefined,
  };
}

/** The Segments list's filter, object filter and display mode, kept in the URL. */
export function useSegmentsListUrlState(resetKey: string, defaults: SegmentsListDefaults) {
  return useListUrlState({
    resetKey,
    defaultFilter: defaults.filter,
    defaultObjectFilter: defaults.objectFilter,
    defaultDisplayMode: defaults.displayMode,
    allowedDisplayModes: ["grid", "list"] as const,
    allowInfinitePageSize: true,
  });
}
