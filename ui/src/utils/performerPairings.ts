import type {
  FilterExpression,
  FilterExpressionNode,
  PerformerPairingCoStar,
  PerformerPairingVideo,
  PerformerPairings,
} from "../api/types";
import type { Route } from "../router/location";
import { FILTER_EXPRESSION_STATE_KEY } from "./filterExpressionTree";
import { compareNatural } from "./naturalCompare";

// Everything the Appears With tab shows is derived here from one /pairings response, so filters,
// sorting and lineups change instantly and every part of the tab agrees on the numbers.

/** Co-stars with at least this many videos together are frequent partners. */
export const FREQUENT_PAIRING_MIN = 5;

/** The URL parameters holding the tab's choices, so they survive going back from a video. */
export const PAIRING_URL_PARAMS = {
  tier: "awTier",
  gender: "awGender",
  sort: "awSort",
  duoOnly: "awDuo",
  favoritesOnly: "awFav",
  query: "awQ",
  lineup: "awLineup",
  page: "awPage",
  perPage: "awPerPage",
  videoFilter: "awFilters",
} as const;

/** Co-stars shown per page unless the viewer picks another page size. */
export const PAIRING_PER_PAGE_DEFAULT = 20;

/** The performer page's key for the Appears With tab. */
export const PAIRING_TAB_KEY = "appearsWith";

/** Dropped when the page switches to another tab, and remembered for when the tab is picked again. */
export const PAIRING_URL_KEYS: readonly string[] = Object.values(PAIRING_URL_PARAMS);

export type PairingSort = "together" | "recent" | "first" | "span" | "name";

export const PAIRING_SORT_OPTIONS: ReadonlyArray<{ value: PairingSort; label: string }> = [
  { value: "together", label: "Most videos together" },
  { value: "recent", label: "Most recent together" },
  { value: "first", label: "First together" },
  { value: "span", label: "Longest partnership" },
  { value: "name", label: "Name" },
];

export function isPairingSort(value: string | null | undefined): value is PairingSort {
  return PAIRING_SORT_OPTIONS.some((option) => option.value === value);
}

export type PairingTier = "frequent" | "recurring" | "once";

export const PAIRING_TIERS: ReadonlyArray<{
  value: PairingTier;
  label: string;
  rule: string;
}> = [
  {
    value: "frequent",
    label: "Frequent",
    rule: `${FREQUENT_PAIRING_MIN} or more videos together`,
  },
  {
    value: "recurring",
    label: "Recurring",
    rule: `2 to ${FREQUENT_PAIRING_MIN - 1} videos together`,
  },
  { value: "once", label: "One-time", rule: "1 video together" },
];

export function isPairingTier(value: string | null | undefined): value is PairingTier {
  return PAIRING_TIERS.some((tier) => tier.value === value);
}

/** The gender filter key for a co-star whose gender is not set. */
export const UNKNOWN_GENDER = "unknown";

export interface PairingFilters {
  favoritesOnly: boolean;
  query: string;
}

/** Filters offered as chips, each choice with a count. */
export interface PairingFacets {
  /** A gender value, UNKNOWN_GENDER, or null for every co-star. */
  gender: string | null;
  tier: PairingTier | null;
}

export interface PairingStudio {
  id: number | null;
  name: string;
  count: number;
}

export interface Pairing {
  coStar: PerformerPairingCoStar;
  /** The videos they share, newest first and undated last. In duo mode, only duos. */
  videos: PerformerPairingVideo[];
  count: number;
  duoCount: number;
  groupCount: number;
  firstYear: number | null;
  lastYear: number | null;
  firstDate: string | null;
  lastDate: string | null;
  yearCounts: Map<number, number>;
  /** Most videos first. */
  studios: PairingStudio[];
}

export interface PairingsSummary {
  videoCount: number;
  sharedVideoCount: number;
  /** The span of the performer's dated videos, solo ones included. */
  firstYear: number | null;
  lastYear: number | null;
}

// A year outside this range is a placeholder or a typo (0001-01-01 and 1900-01-01 turn up in scraped
// data), and counting it would stretch every career bar across a century.
const EARLIEST_PLAUSIBLE_YEAR = 1920;

function isPlausibleYear(year: number) {
  return Number.isInteger(year) && year >= EARLIEST_PLAUSIBLE_YEAR && year <= new Date().getFullYear() + 5;
}

export function videoYear(video: Pick<PerformerPairingVideo, "date">): number | null {
  const year = video.date ? Number.parseInt(video.date.slice(0, 4), 10) : Number.NaN;
  return isPlausibleYear(year) ? year : null;
}

export function isDuoVideo(video: Pick<PerformerPairingVideo, "performerIds">) {
  return video.performerIds.length === 2;
}

export function coStarGenderKey(coStar: Pick<PerformerPairingCoStar, "gender">) {
  return coStar.gender || UNKNOWN_GENDER;
}

export function pairingTier(pairing: Pick<Pairing, "count">): PairingTier {
  if (pairing.count >= FREQUENT_PAIRING_MIN) return "frequent";
  return pairing.count >= 2 ? "recurring" : "once";
}

export function summarizePairings(data: PerformerPairings): PairingsSummary {
  let firstYear: number | null = null;
  let lastYear: number | null = null;
  const include = (year: number) => {
    if (firstYear == null || year < firstYear) firstYear = year;
    if (lastYear == null || year > lastYear) lastYear = year;
  };
  for (const video of data.videos) {
    const year = videoYear(video);
    if (year != null) include(year);
  }
  for (const [key, count] of Object.entries(data.soloVideoYears)) {
    const year = Number(key);
    if (count > 0 && isPlausibleYear(year)) include(year);
  }
  return { videoCount: data.videoCount, sharedVideoCount: data.videos.length, firstYear, lastYear };
}

function describePairing(coStar: PerformerPairingCoStar, videos: PerformerPairingVideo[]): Pairing {
  const yearCounts = new Map<number, number>();
  const studios = new Map<string, PairingStudio>();
  let duoCount = 0;
  let firstDate: string | null = null;
  let lastDate: string | null = null;
  let firstYear: number | null = null;
  let lastYear: number | null = null;
  for (const video of videos) {
    if (isDuoVideo(video)) duoCount += 1;
    const year = videoYear(video);
    if (year != null && video.date) {
      yearCounts.set(year, (yearCounts.get(year) ?? 0) + 1);
      if (firstYear == null || year < firstYear) firstYear = year;
      if (lastYear == null || year > lastYear) lastYear = year;
      // Partial dates compare correctly as strings: "2015" sorts before "2015-03-02".
      if (lastDate == null || video.date > lastDate) lastDate = video.date;
      if (firstDate == null || video.date < firstDate) firstDate = video.date;
    }
    if (video.studioName) {
      const key = video.studioId != null ? `id:${video.studioId}` : `name:${video.studioName}`;
      const studio = studios.get(key) ?? { id: video.studioId ?? null, name: video.studioName, count: 0 };
      studio.count += 1;
      studios.set(key, studio);
    }
  }
  return {
    coStar,
    videos,
    count: videos.length,
    duoCount,
    groupCount: videos.length - duoCount,
    firstYear,
    lastYear,
    firstDate,
    lastDate,
    yearCounts,
    studios: [...studios.values()].sort((a, b) => b.count - a.count || compareNatural(a.name, b.name)),
  };
}

/**
 * Every co-star who shares a video with the performer, unsorted. In duo mode only videos with just
 * the two of them count, and co-stars left with none drop out.
 */
export function buildPairings(data: PerformerPairings, duoOnly: boolean): Pairing[] {
  const videosByCoStar = new Map<number, PerformerPairingVideo[]>();
  for (const video of data.videos) {
    if (duoOnly && !isDuoVideo(video)) continue;
    for (const performerId of video.performerIds) {
      if (performerId === data.performerId) continue;
      const shared = videosByCoStar.get(performerId);
      if (shared) shared.push(video);
      else videosByCoStar.set(performerId, [video]);
    }
  }
  const pairings: Pairing[] = [];
  for (const coStar of data.coStars) {
    const shared = videosByCoStar.get(coStar.id);
    if (shared && shared.length > 0) pairings.push(describePairing(coStar, shared));
  }
  return pairings;
}

/**
 * Keeps favorites only, or co-stars whose name or disambiguation matches the search. The pairings
 * themselves are reused, so rows that stay on screen do not re-render while typing.
 */
export function filterPairings(pairings: readonly Pairing[], filters: PairingFilters): Pairing[] {
  const query = filters.query.trim().toLocaleLowerCase();
  if (!filters.favoritesOnly && !query) return [...pairings];
  return pairings.filter(
    ({ coStar }) =>
      (!filters.favoritesOnly || coStar.favorite) &&
      (!query || `${coStar.name} ${coStar.disambiguation ?? ""}`.toLocaleLowerCase().includes(query)),
  );
}

/** A page number or page size from the URL; anything but a positive whole number reads as null. */
export function parsePositiveInt(value: string | null | undefined): number | null {
  const parsed = Number(value);
  return value && Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

/** A comma-separated list of performer ids from the URL, in order and without repeats. */
export function parseIdList(value: string | null | undefined): number[] {
  const ids: number[] = [];
  for (const part of (value ?? "").split(",")) {
    const id = Number(part);
    if (Number.isInteger(id) && id > 0 && !ids.includes(id)) ids.push(id);
  }
  return ids;
}

/**
 * Applies the gender and tier chips. Each chip's count applies the other chip row but not its own,
 * so a count always says what picking that chip would show.
 */
export function facetPairings(pairings: readonly Pairing[], facets: PairingFacets) {
  const genderCounts = new Map<string, number>();
  const tierCounts: Record<PairingTier, number> = { frequent: 0, recurring: 0, once: 0 };
  const visible: Pairing[] = [];
  for (const pairing of pairings) {
    const gender = coStarGenderKey(pairing.coStar);
    const tier = pairingTier(pairing);
    const genderMatches = facets.gender == null || gender === facets.gender;
    const tierMatches = facets.tier == null || tier === facets.tier;
    if (tierMatches) genderCounts.set(gender, (genderCounts.get(gender) ?? 0) + 1);
    if (genderMatches) tierCounts[tier] += 1;
    if (genderMatches && tierMatches) visible.push(pairing);
  }
  return {
    visible,
    tierCounts,
    genderCounts: [...genderCounts.entries()]
      .map(([gender, count]) => ({ gender, count }))
      .sort((a, b) => b.count - a.count || compareNatural(a.gender, b.gender)),
  };
}

function compareByName(a: Pairing, b: Pairing) {
  return compareNatural(a.coStar.name, b.coStar.name) || a.coStar.id - b.coStar.id;
}

// Missing dates sort last whichever way the dates run.
function compareDates(a: string | null, b: string | null, direction: 1 | -1) {
  if (a === b) return 0;
  if (a == null) return 1;
  if (b == null) return -1;
  return a < b ? -direction : direction;
}

function yearsTogether(pairing: Pairing) {
  return pairing.firstYear != null && pairing.lastYear != null ? pairing.lastYear - pairing.firstYear : -1;
}

export function sortPairings(pairings: readonly Pairing[], sort: PairingSort): Pairing[] {
  const sorted = [...pairings];
  switch (sort) {
    case "name":
      return sorted.sort(compareByName);
    case "recent":
      return sorted.sort(
        (a, b) => compareDates(a.lastDate, b.lastDate, -1) || b.count - a.count || compareByName(a, b),
      );
    case "first":
      return sorted.sort(
        (a, b) => compareDates(a.firstDate, b.firstDate, 1) || b.count - a.count || compareByName(a, b),
      );
    case "span":
      return sorted.sort((a, b) => yearsTogether(b) - yearsTogether(a) || b.count - a.count || compareByName(a, b));
    default:
      return sorted.sort(
        (a, b) => b.count - a.count || compareDates(a.lastDate, b.lastDate, -1) || compareByName(a, b),
      );
  }
}

/**
 * The performer's videos with every chosen co-star in them, and those with at least one. In duo mode
 * only duos count, so a lineup of two or more co-stars has no video with all of them.
 */
export function lineupVideos(data: PerformerPairings, coStarIds: readonly number[], duoOnly = false) {
  const chosen = new Set(coStarIds);
  const all: PerformerPairingVideo[] = [];
  const any: PerformerPairingVideo[] = [];
  if (chosen.size === 0) return { all, any };
  for (const video of data.videos) {
    if (duoOnly && !isDuoVideo(video)) continue;
    let matches = 0;
    for (const performerId of video.performerIds) if (chosen.has(performerId)) matches += 1;
    if (matches > 0) any.push(video);
    if (matches === chosen.size) all.push(video);
  }
  return { all, any };
}

/**
 * The performer's Videos tab filtered to videos with the given co-stars. One co-star uses the same
 * filter a person would pick by hand; several need all of them ("all") or at least one ("any"). The
 * search and page are reset so the tab's remembered state cannot hide any of those videos.
 */
export function sharedVideosRoute(
  performerId: number,
  coStarIds: readonly number[],
  options: { match?: "all" | "any"; duoOnly?: boolean; videoFilter?: Record<string, unknown> } = {},
): Route {
  const modifier = coStarIds.length > 1 && options.match !== "any" ? "INCLUDES_ALL" : "INCLUDES";
  let listObjectFilter = withCondition(options.videoFilter ?? {}, "performersCriterion", {
    value: [...coStarIds],
    modifier,
  });
  if (options.duoOnly) {
    listObjectFilter = withCondition(listObjectFilter, "performerCountCriterion", { value: 2, modifier: "EQUALS" });
  }
  return { page: "performer", id: performerId, detailTab: "videos", listFilter: { q: "", page: 1 }, listObjectFilter };
}

/**
 * Adds a condition every video must also meet. It takes its own filter key when that is free, so it
 * shows as the chip a person would pick by hand; when the filter already uses the key, the condition
 * joins the filter expression instead, which applies together with the plain conditions.
 */
function withCondition(filter: Record<string, unknown>, key: string, criterion: unknown): Record<string, unknown> {
  if (!(key in filter)) return { ...filter, [key]: criterion };
  const condition: FilterExpressionNode = { filter: { [key]: criterion } };
  const existing = filter[FILTER_EXPRESSION_STATE_KEY] as FilterExpression | undefined;
  const expression: FilterExpression = !existing
    ? { operator: "AND", children: [condition] }
    : existing.operator === "AND" && !existing.relatedScope
      ? { ...existing, children: [...existing.children, condition] }
      : { operator: "AND", children: [{ group: existing }, condition] };
  return { ...filter, [FILTER_EXPRESSION_STATE_KEY]: expression };
}

/**
 * The Videos-list filter the tab counts shared videos by, as the URL keeps it. Anything but a JSON
 * object reads as no filter.
 */
export function parseVideoFilter(value: string | null | undefined): Record<string, unknown> {
  if (!value) return {};
  try {
    const parsed: unknown = JSON.parse(value);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

/** The response with only the shared videos the Videos-list filter matched. */
export function scopePairingsToVideos(
  data: PerformerPairings,
  matchingVideoIds: ReadonlySet<number>,
): PerformerPairings {
  return { ...data, videos: data.videos.filter((video) => matchingVideoIds.has(video.id)) };
}

export function pairingVideoTitle(video: Pick<PerformerPairingVideo, "id" | "title">) {
  return video.title?.trim() || `Video ${video.id}`;
}
