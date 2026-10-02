import type { FindFilter } from "../api/types";
import { defaultSortDirection } from "./sortClauses";

export const RELEVANCE_SORT_VALUE = "relevance";
export const RELEVANCE_SORT_OPTION = { value: RELEVANCE_SORT_VALUE, label: "Relevance" } as const;

/**
 * Entities whose list queries rank a text query by score, so "Relevance" is a real ordering for them.
 *
 * This is narrower than "the backend can score this entity": a face's own `find` endpoint scores `q`,
 * but the face subviews (similar faces, appearances) filter and sort in memory and would silently
 * ignore `sort=relevance`, so faces stay out until every surface for them agrees.
 */
const RELEVANCE_SORT_ENTITY_TYPES = new Set([
  "video",
  "image",
  "audio",
  "text",
  "gallery",
  "performer",
  "tag",
  "group",
  "studio",
]);

const LIST_ENTITY_BY_FILTER_MODE: Record<string, string> = {
  videos: "video",
  audios: "audio",
  texts: "text",
  performers: "performer",
  tags: "tag",
  studios: "studio",
  galleries: "gallery",
  images: "image",
  groups: "group",
  faces: "face",
  segments: "segment",
};

export function normalizeListEntityType(entityType?: string) {
  const normalized = (entityType ?? "").trim().toLowerCase();
  const singular = normalized.endsWith("s") ? normalized.slice(0, -1) : normalized;
  return LIST_ENTITY_BY_FILTER_MODE[normalized] ?? singular;
}

/** Accepts either a normalized entity type or a raw list/filter mode; normalizing is idempotent. */
export function supportsRelevanceSort(listEntityType: string) {
  return RELEVANCE_SORT_ENTITY_TYPES.has(normalizeListEntityType(listEntityType));
}

export type PreviousSearchSort = Pick<FindFilter, "sort" | "direction" | "sorts" | "seed">;

export interface SearchQueryFilterResult {
  filter: FindFilter;
  /** The value the caller should store for its next call; `null` once nothing is held. */
  previousSearchSort: PreviousSearchSort | null;
}

/**
 * Shared search-box behavior for every list surface: a new text query switches the primary sort to
 * Relevance and remembers the sort it displaced, clearing the query puts that sort back. Top-level
 * pages and detail-page subviews must resolve this identically, so keep the logic here rather than
 * in either toolbar.
 *
 * `previousSearchSort` is the caller's memory of the displaced sort and lives only as long as the
 * toolbar does. A list whose filter outlives its toolbar — a detail-page panel unmounted by a tab
 * switch, say — comes back with nothing remembered and falls back to its first sort option on clear.
 */
export function resolveSearchQueryFilter({
  filter,
  query,
  listEntityType,
  sortOptions,
  previousSearchSort,
  switchToRelevance = true,
}: {
  filter: FindFilter;
  query: string | undefined;
  listEntityType: string;
  sortOptions: { value: string; label: string }[] | undefined;
  previousSearchSort: PreviousSearchSort | null;
  /** The library setting "Sort searches by relevance"; off keeps the list's sort when a query starts. */
  switchToRelevance?: boolean;
}): SearchQueryFilterResult {
  const currentQuery = filter.q?.trim();

  if (query && !currentQuery && switchToRelevance && supportsRelevanceSort(listEntityType)) {
    return {
      filter: {
        ...filter,
        q: query,
        page: 1,
        sort: RELEVANCE_SORT_VALUE,
        direction: "desc",
        sorts: undefined,
        seed: undefined,
      },
      previousSearchSort: {
        sort: filter.sort,
        direction: filter.direction,
        sorts: filter.sorts,
        seed: filter.seed,
      },
    };
  }

  if (!query && currentQuery && filter.sort === RELEVANCE_SORT_VALUE) {
    // A deep-linked relevance search has no remembered sort, so fall back to the first real option.
    const fallbackSort = sortOptions?.find((option) => option.value !== RELEVANCE_SORT_VALUE)?.value;
    const restoredSort = previousSearchSort ?? {
      sort: fallbackSort,
      direction: fallbackSort ? defaultSortDirection(fallbackSort) : undefined,
      sorts: undefined,
      seed: undefined,
    };
    return {
      filter: { ...filter, ...restoredSort, q: undefined, page: 1 },
      previousSearchSort: null,
    };
  }

  return { filter: { ...filter, q: query, page: 1 }, previousSearchSort };
}

/**
 * Prepends "Relevance" to a list's sort options while it is meaningful: whenever a text query is
 * present, and also whenever it is the active sort, so a list never renders a sort it cannot show.
 * Always returns a new array, so callers may sort the result without mutating the options they were
 * given.
 */
export function withRelevanceSortOption(
  sortOptions: { value: string; label: string }[],
  { listEntityType, filter }: { listEntityType: string; filter: FindFilter },
) {
  if (!supportsRelevanceSort(listEntityType)) return [...sortOptions];
  if (!filter.q?.trim() && filter.sort !== RELEVANCE_SORT_VALUE) return [...sortOptions];
  if (sortOptions.some((option) => option.value === RELEVANCE_SORT_VALUE)) return [...sortOptions];
  return [RELEVANCE_SORT_OPTION, ...sortOptions];
}

/**
 * The filter fields to change when a primary sort is chosen by hand. Relevance always ranks
 * best-first, so it pins `desc` rather than carrying the previous sort's direction into a toggle
 * the UI hides, and drops any multi-sort clauses the backend would otherwise prefer over it.
 */
export function filterPatchForSelectedSort(
  sort: string,
  filter: FindFilter,
): Pick<FindFilter, "sort" | "direction" | "sorts"> {
  if (sort !== RELEVANCE_SORT_VALUE) return { sort, direction: filter.direction, sorts: filter.sorts };
  return { sort, direction: "desc", sorts: undefined };
}
