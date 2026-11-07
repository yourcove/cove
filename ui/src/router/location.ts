import type { FindFilter, SegmentDerivedQueryDescriptor } from "../api/types";
import { formatCutParam, parseCutParam, type CutRange } from "../utils/videoCut";

export interface Route {
  page: string;
  id?: number;
  /**
   * A single non-numeric child segment for an extension-contributed page.
   * Numeric children are represented by `id`; deeper child paths are not supported.
   */
  slug?: string;
  seekTo?: number;
  videoTab?: string;
  /** Removals to open a video's trim editor with, from the `cut` parameter. */
  cut?: CutRange[];
  detailTab?: string;
  spanKey?: string;
  profileId?: number;
  derivedQueryDescriptor?: SegmentDerivedQueryDescriptor;
  manualTopicId?: string;
  manualSlideId?: string;
  listFilter?: FindFilter;
  listObjectFilter?: Record<string, unknown>;
  listView?: string;
  segmentsView?: "spans" | "raw";
  compilationItemOrder?: string[];
}

interface RouteHistoryEntry {
  url: string;
  route: Route;
  /** The history entry's id, when it has one; matched before the URL, which entries can share. */
  id?: string;
}

function findRouteHistoryIndex(history: RouteHistoryEntry[], id: string | undefined, url: string): number {
  if (id) {
    for (let index = history.length - 1; index >= 0; index -= 1) {
      if (history[index].id === id) return index;
    }
  }
  for (let index = history.length - 1; index >= 0; index -= 1) {
    // An entry with a different id is another visit to the same URL, not this one.
    if (history[index].url === url && !(id && history[index].id)) return index;
  }
  return -1;
}

/**
 * A copy of the page a run of same-type pages started from, kept on every history entry of the run.
 * It lives in `history.state` beside the route, so it survives reloads and back/forward moves without
 * depending on how much of the session's route history is still recorded.
 */
export interface BackOrigin {
  url: string;
  route: Route;
  /** Window scroll offset of the origin page when it was left. */
  scrollY: number;
  /**
   * The origin page's own origin, restored when returning to it, so Back from there still reaches
   * the page its run started from. Nesting is capped at {@link MAX_BACK_ORIGIN_DEPTH}.
   */
  origin?: BackOrigin;
  /** The history entry the origin page had, telling it apart from other visits to the same URL. */
  entryId?: string;
}

const MAX_BACK_ORIGIN_DEPTH = 4;

const BACK_ORIGIN_STATE_KEY = "backOrigin";
/** Marks an entry opened by returning to a saved origin, as opposed to reached by browsing. */
const BACK_RETURNED_STATE_KEY = "backReturned";
/** Identifies a history entry, so entries with the same URL are told apart. */
const ENTRY_ID_STATE_KEY = "entryId";
const ENTRY_STATE_KEYS = [BACK_ORIGIN_STATE_KEY, BACK_RETURNED_STATE_KEY, ENTRY_ID_STATE_KEY] as const;

/** What the app keeps in `history.state` next to the route; each field is optional. */
interface EntryState {
  [BACK_ORIGIN_STATE_KEY]?: BackOrigin;
  [BACK_RETURNED_STATE_KEY]?: true;
  [ENTRY_ID_STATE_KEY]?: string;
}
type RouteHistoryState = Partial<Route> & EntryState;

let entryIdCounter = 0;
function newEntryId(): string {
  entryIdCounter += 1;
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}-${entryIdCounter}`;
}

function readEntryState(): EntryState {
  const state: unknown = window.history.state;
  return state != null && typeof state === "object" ? (state as EntryState) : {};
}

function readEntryId(): string | undefined {
  const id = readEntryState()[ENTRY_ID_STATE_KEY];
  return typeof id === "string" && id ? id : undefined;
}

/**
 * A copy of a saved history state to push as a new entry. The entry id is left out, because two
 * entries must not share one; the next route-history sync gives the new entry its own.
 */
export function historyStateForNewEntry(state: unknown): unknown {
  if (state == null || typeof state !== "object" || !(ENTRY_ID_STATE_KEY in state)) return state;
  const { [ENTRY_ID_STATE_KEY]: _id, ...rest } = state as EntryState & Record<string, unknown>;
  return rest;
}

/** Whether the current entry was opened by returning to a saved origin. */
export function readBackReturned(): boolean {
  return readEntryState()[BACK_RETURNED_STATE_KEY] === true;
}

export const LOCATION_CHANGE_EVENT = "cove-locationchange";
const ROUTE_HISTORY_KEY = "cove-route-history";
type RouteHistoryMode = "push" | "replace" | "history";

function isRouteState(value: unknown): value is Route {
  return value != null && typeof value === "object" && typeof (value as Route).page === "string";
}

function parsePath(pathname: string, search?: string): Route {
  const parts = pathname.split("/").filter(Boolean);
  if (parts.length === 0 || parts[0] === "home") {
    return applyRouteSearch({ page: "home" }, search);
  }

  if (parts[0] === "video" && parts.length > 3 && parts[2] === "span") {
    const id = Number(parts[1]);
    if (Number.isInteger(id) && id > 0) {
      return applyRouteSearch({ page: "video-span", id, spanKey: decodeURIComponent(parts[3]) }, search);
    }
  }

  if (parts[0] === "compilation" && parts.length > 2 && parts[2] === "play") {
    const id = Number(parts[1]);
    if (Number.isInteger(id) && id > 0) {
      return applyRouteSearch({ page: "compilation", id }, search);
    }
  }

  if (parts[0] === "manual") {
    return applyRouteSearch(
      {
        page: "manual",
        manualTopicId: parts.length > 1 ? decodeURIComponent(parts[1]) : undefined,
        manualSlideId: parts.length > 2 ? decodeURIComponent(parts[2]) : undefined,
      },
      search,
    );
  }

  const page = parts[0];
  const id = parts.length > 1 ? Number(parts[1]) : undefined;
  if (id != null && Number.isInteger(id) && id > 0) {
    return applyRouteSearch({ page, id }, search);
  }

  if (parts.length === 2 && parts[1]) {
    const child = decodeURIComponent(parts[1]);
    if (Number.isNaN(Number(child))) {
      return applyRouteSearch({ page, slug: child }, search);
    }
  }

  return applyRouteSearch({ page }, search);
}

export function parseLegacyHashRoute(hash: string): Route | null {
  if (!hash.startsWith("#/")) {
    return null;
  }

  const [pathname, search = ""] = hash.slice(1).split("?");
  return parsePath(pathname, search ? `?${search}` : undefined);
}

export function parseCurrentRoute(): Route {
  return parsePath(window.location.pathname, window.location.search);
}

const DETAIL_TAB_BY_SOURCE_PAGE: Record<string, string> = {
  video: "videos",
  videos: "videos",
  gallery: "galleries",
  galleries: "galleries",
  image: "images",
  images: "images",
  audio: "audios",
  audios: "audios",
  text: "texts",
  texts: "texts",
};

const DETAIL_TABS_BY_TARGET_PAGE: Record<string, ReadonlySet<string>> = {
  performer: new Set(["videos", "galleries", "images", "audios", "texts"]),
  studio: new Set(["videos", "galleries", "images", "audios", "texts"]),
  tag: new Set(["videos", "galleries", "images", "audios", "texts"]),
  gallery: new Set(["videos", "images"]),
};

// Sources whose tab differs once the target is taken into account. A gallery reached from gallery
// browsing has no "galleries" tab to land on, and its images are what the viewer was looking at, so
// it opens on Images rather than falling through to the target's own default.
const DETAIL_TAB_BY_SOURCE_PAGE_FOR_TARGET: Record<string, Record<string, string>> = {
  gallery: { gallery: "images", galleries: "images" },
};

export function resolveContextualDetailRoute(route: Route, sourcePage: string = parseCurrentRoute().page): Route {
  if (route.detailTab) return route;

  const detailTab =
    DETAIL_TAB_BY_SOURCE_PAGE_FOR_TARGET[route.page]?.[sourcePage] ?? DETAIL_TAB_BY_SOURCE_PAGE[sourcePage];
  if (!detailTab || !DETAIL_TABS_BY_TARGET_PAGE[route.page]?.has(detailTab)) return route;

  return { ...route, detailTab };
}

/** The route stored in a history state, without the entry details that travel with it. */
export function routeFromHistoryState(state: unknown): Route | undefined {
  if (!isRouteState(state)) return undefined;
  if (!ENTRY_STATE_KEYS.some((key) => key in state)) return state;
  const route = { ...state } as Record<string, unknown>;
  for (const key of ENTRY_STATE_KEYS) delete route[key];
  return route as unknown as Route;
}

/** Whether a history state is one the app writes: a route, an entry-details-only object, or none. */
function isAppHistoryState(state: unknown): boolean {
  if (state == null || isRouteState(state)) return true;
  if (typeof state !== "object") return false;
  const keys = Object.keys(state);
  return keys.length > 0 && keys.every((key) => (ENTRY_STATE_KEYS as readonly string[]).includes(key));
}

function readCurrentStateRoute(): Route | undefined {
  return routeFromHistoryState(window.history.state);
}

function isBackOrigin(value: unknown): value is BackOrigin {
  if (value == null || typeof value !== "object") return false;
  const origin = value as BackOrigin;
  return typeof origin.url === "string" && isRouteState(origin.route) && typeof origin.scrollY === "number";
}

/** The page the current run of same-type pages started from, when the app recorded one. */
export function readBackOrigin(): BackOrigin | undefined {
  const state: unknown = window.history.state;
  if (state == null || typeof state !== "object") return undefined;
  return sanitizeBackOrigin((state as RouteHistoryState)[BACK_ORIGIN_STATE_KEY], MAX_BACK_ORIGIN_DEPTH);
}

function sanitizeBackOrigin(value: unknown, depth: number): BackOrigin | undefined {
  if (depth <= 0 || !isBackOrigin(value)) return undefined;
  const { url, route, scrollY } = value;
  const entryId = typeof value.entryId === "string" && value.entryId ? value.entryId : undefined;
  const parent = sanitizeBackOrigin(value.origin, depth - 1);
  return { url, route, scrollY, ...(entryId ? { entryId } : {}), ...(parent ? { origin: parent } : {}) };
}

/**
 * The origin to store with a new entry for `nextRoute`. Moving between pages of the same type
 * (video to video, performer to performer) keeps the current run's origin; any other move starts a
 * new run whose origin is the page being left.
 */
function nextBackOrigin(nextRoute: Route): BackOrigin | undefined {
  const currentRoute = readCurrentStateRoute() ?? parseCurrentRoute();
  if (currentRoute.page === nextRoute.page) return readBackOrigin();

  const parentOrigin = sanitizeBackOrigin(readBackOrigin(), MAX_BACK_ORIGIN_DEPTH - 1);
  const entryId = ensureCurrentEntryId();
  return {
    url: buildCurrentUrl(window.location.pathname, window.location.search),
    route: currentRoute,
    scrollY: Math.max(0, Math.round(window.scrollY)),
    ...(entryId ? { entryId } : {}),
    ...(parentOrigin ? { origin: parentOrigin } : {}),
  };
}

function parseUrlRoute(url: string): Route {
  const parsed = new URL(url, window.location.origin);
  return parsePath(parsed.pathname, parsed.search);
}

/**
 * Attach the entry details (id, back origin, returned marker) to the state of the entry being
 * written. `explicitOrigin` marks a return to a saved origin and stores that page's own origin
 * (`null` stores none).
 */
function withEntryState(
  state: unknown,
  url: string,
  replace: boolean | undefined,
  explicitOrigin: BackOrigin | null | undefined,
): unknown {
  const route = routeFromHistoryState(state);
  // Leave state written by someone else alone; only route states and empty states carry details.
  if (state != null && !route) return state;

  const current = readEntryState();
  let origin: BackOrigin | undefined;
  let returned = false;
  if (explicitOrigin !== undefined) {
    origin = explicitOrigin ?? undefined;
    returned = true;
  } else if (replace) {
    // A replaced entry is the same step, so it keeps what it had. Most tab and filter changes
    // replace without a state, which must not lose the origin either.
    origin = readBackOrigin();
    returned = current[BACK_RETURNED_STATE_KEY] === true;
  } else {
    origin = nextBackOrigin(route ?? parseUrlRoute(url));
  }

  const details: EntryState = {
    [ENTRY_ID_STATE_KEY]: (replace ? readEntryId() : undefined) ?? newEntryId(),
    ...(origin ? { [BACK_ORIGIN_STATE_KEY]: origin } : {}),
    ...(returned ? { [BACK_RETURNED_STATE_KEY]: true as const } : {}),
  };
  // Without a route, store the details alone: a stale route here would override what the URL says.
  return route ? { ...route, ...details } : details;
}

/** Give the current entry an id if the app owns its state and it has none yet, e.g. a fresh tab. */
function ensureCurrentEntryId(): string | undefined {
  const existing = readEntryId();
  if (existing) return existing;
  const state: unknown = window.history.state;
  if (!isAppHistoryState(state)) return undefined;

  const id = newEntryId();
  const nextState = state == null ? { [ENTRY_ID_STATE_KEY]: id } : { ...(state as object), [ENTRY_ID_STATE_KEY]: id };
  window.history.replaceState(
    nextState,
    "",
    `${window.location.pathname}${window.location.search}${window.location.hash}`,
  );
  return id;
}

export function buildRoutePath(route: Route): string {
  if (!route.page || route.page === "home") {
    return "/";
  }

  if (route.page === "video-span" && route.id != null && route.spanKey) {
    return `/video/${route.id}/span/${encodeURIComponent(route.spanKey)}`;
  }

  if (route.page === "compilation" && route.id != null) {
    return `/compilation/${route.id}/play`;
  }

  if (route.page === "manual") {
    const segments = ["manual"];
    if (route.manualTopicId) {
      segments.push(encodeURIComponent(route.manualTopicId));
      if (route.manualSlideId) segments.push(encodeURIComponent(route.manualSlideId));
    }
    return `/${segments.join("/")}`;
  }

  if (route.id != null) {
    return `/${route.page}/${route.id}`;
  }

  if (route.slug) {
    return `/${route.page}/${encodeURIComponent(route.slug)}`;
  }

  return `/${route.page}`;
}

export function buildRouteUrl(route: Route): string {
  const params = new URLSearchParams();
  const listFilterEntries: [keyof FindFilter, string][] = [
    ["q", "q"],
    ["page", "page"],
    ["perPage", "perPage"],
    ["sort", "sort"],
    ["direction", "direction"],
    ["seed", "seed"],
  ];
  for (const [filterKey, paramKey] of listFilterEntries) {
    const value = route.listFilter?.[filterKey];
    if (value != null && (value !== "" || filterKey === "q")) {
      params.set(paramKey, filterKey === "perPage" && value === 0 ? "infinite" : String(value));
    }
  }
  if (route.listObjectFilter !== undefined) {
    params.set("filters", JSON.stringify(route.listObjectFilter));
  }
  if (route.listView) {
    params.set("view", route.listView);
  }
  if (route.segmentsView === "raw") {
    params.set("segmentsView", "raw");
  }
  if (route.detailTab) {
    params.set("tab", route.detailTab);
  }
  if (route.seekTo != null && Number.isFinite(route.seekTo) && route.seekTo >= 0) {
    params.set("t", String(route.seekTo));
  }
  if (route.cut && route.cut.length > 0) {
    params.set("cut", formatCutParam(route.cut));
  }
  if (route.profileId != null && Number.isInteger(route.profileId) && route.profileId > 0) {
    params.set("profile", String(route.profileId));
  }
  if (route.derivedQueryDescriptor) {
    const encoded = encodeDerivedQueryDescriptor(route.derivedQueryDescriptor);
    if (encoded) {
      params.set("dq", encoded);
    }
  }

  return buildCurrentUrl(buildRoutePath(route), params);
}

function encodeDerivedQueryDescriptor(descriptor: SegmentDerivedQueryDescriptor): string | undefined {
  try {
    const json = JSON.stringify(descriptor);
    if (typeof window === "undefined") {
      return undefined;
    }
    return window.btoa(unescape(encodeURIComponent(json)));
  } catch {
    return undefined;
  }
}

function decodeDerivedQueryDescriptor(encoded: string): SegmentDerivedQueryDescriptor | undefined {
  try {
    if (typeof window === "undefined") {
      return undefined;
    }
    const json = decodeURIComponent(escape(window.atob(encoded)));
    const parsed = JSON.parse(json);
    if (!parsed || typeof parsed !== "object" || !Array.isArray(parsed.operands)) {
      return undefined;
    }
    return parsed as SegmentDerivedQueryDescriptor;
  } catch {
    return undefined;
  }
}

export function buildCurrentUrl(pathname: string, search?: URLSearchParams | string | null): string {
  if (search == null) {
    return pathname;
  }

  const searchString = search instanceof URLSearchParams ? search.toString() : search.replace(/^\?/, "");
  return searchString ? `${pathname}?${searchString}` : pathname;
}

export function emitLocationChange(options?: { replace?: boolean }) {
  window.dispatchEvent(new CustomEvent(LOCATION_CHANGE_EVENT, { detail: options }));
}

type NavigationBlocker = () => boolean;
const navigationBlockers = new Set<NavigationBlocker>();

/** Register a synchronous guard for in-app history mutations. Return false to keep the current URL. */
export function registerNavigationBlocker(blocker: NavigationBlocker): () => void {
  navigationBlockers.add(blocker);
  return () => navigationBlockers.delete(blocker);
}

export function navigateToUrl(
  url: string,
  options?: {
    replace?: boolean;
    state?: unknown;
    bypassBlockers?: boolean;
    /** The back origin to store with the entry instead of the computed one; `null` stores none. */
    backOrigin?: BackOrigin | null;
  },
): boolean {
  const currentUrl = `${window.location.pathname}${window.location.search}`;
  if (currentUrl === url) {
    return true;
  }

  if (!options?.bypassBlockers && [...navigationBlockers].some((blocker) => !blocker())) return false;

  const state = withEntryState(options?.state ?? null, url, options?.replace, options?.backOrigin);
  if (options?.replace) {
    window.history.replaceState(state, "", url);
  } else {
    window.history.pushState(state, "", url);
  }

  emitLocationChange({ replace: options?.replace });
  return true;
}

function readRouteHistory(): RouteHistoryEntry[] {
  try {
    const raw = sessionStorage.getItem(ROUTE_HISTORY_KEY);
    if (!raw) {
      return [];
    }

    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed.filter((entry): entry is RouteHistoryEntry => {
      return (
        entry != null && typeof entry.url === "string" && entry.route != null && typeof entry.route.page === "string"
      );
    });
  } catch {
    return [];
  }
}

function writeRouteHistory(entries: RouteHistoryEntry[]) {
  try {
    sessionStorage.setItem(ROUTE_HISTORY_KEY, JSON.stringify(entries.slice(-30)));
  } catch {
    // Ignore session storage failures.
  }
}

export function readStoredRoute(
  url: string = buildCurrentUrl(window.location.pathname, window.location.search),
): Route | undefined {
  const history = readRouteHistory();
  for (let index = history.length - 1; index >= 0; index -= 1) {
    if (history[index].url === url && isRouteState(history[index].route)) {
      return history[index].route;
    }
  }

  return undefined;
}

export function resolveCurrentRoute(): Route {
  return readCurrentStateRoute() ?? readStoredRoute() ?? parseCurrentRoute();
}

export function syncRouteHistory(mode: RouteHistoryMode = "push") {
  // An id given only now cannot identify an earlier record, so matching then goes by URL.
  const knownId = readEntryId();
  const id = knownId ?? ensureCurrentEntryId();
  const currentEntry: RouteHistoryEntry = {
    url: buildCurrentUrl(window.location.pathname, window.location.search),
    route: readCurrentStateRoute() ?? parseCurrentRoute(),
    ...(id ? { id } : {}),
  };

  const history = readRouteHistory();
  if (mode === "replace" && history.length > 0) {
    // Replace this entry's record; an entry without an id falls back to the latest record.
    let index = -1;
    for (let candidate = history.length - 1; knownId && candidate >= 0; candidate -= 1) {
      if (history[candidate].id === knownId) {
        index = candidate;
        break;
      }
    }
    history[index >= 0 ? index : history.length - 1] = currentEntry;
    writeRouteHistory(history);
    return;
  }

  if (mode === "history") {
    const index = findRouteHistoryIndex(history, knownId, currentEntry.url);
    if (index >= 0) {
      history[index] = currentEntry;
      writeRouteHistory(history.slice(0, index + 1));
      return;
    }
  }

  const lastEntry = history.length > 0 ? history[history.length - 1] : undefined;
  if (lastEntry && (knownId && lastEntry.id ? lastEntry.id === knownId : lastEntry.url === currentEntry.url)) {
    history[history.length - 1] = currentEntry;
    writeRouteHistory(history);
    return;
  }

  history.push(currentEntry);
  writeRouteHistory(history);
}

export function getRouteLabel(route: Route): string {
  switch (route.page) {
    case "home":
      return "Home";
    case "video":
      return "Video";
    case "audio":
      return "Audio";
    case "audios":
      return "Audios";
    case "text":
      return "Text";
    case "texts":
      return "Texts";
    case "video-span":
      return "Span";
    case "videos":
      return "Videos";
    case "segment":
      return "Segment";
    case "segments":
      return "Segments";
    case "faces":
      return "Faces";
    case "image":
      return "Image";
    case "images":
      return "Images";
    case "gallery":
      return "Gallery";
    case "galleries":
      return "Galleries";
    case "group":
      return "Group";
    case "groups":
      return "Groups";
    case "compilation":
      return "Compilation";
    case "performer":
      return "Performer";
    case "performers":
      return "Performers";
    case "studio":
      return "Studio";
    case "studios":
      return "Studios";
    case "tag":
      return "Tag";
    case "tags":
      return "Tags";
    default:
      return route.page ? route.page.charAt(0).toUpperCase() + route.page.slice(1) : "Previous Page";
  }
}

function applyRouteSearch(route: Route, search?: string): Route {
  if (!search) {
    return route;
  }

  const params = new URLSearchParams(search);
  const profileParam = params.get("profile");
  const seekParam = params.get("t");
  const detailTab = params.get("tab");
  let nextRoute = route;

  if (detailTab) {
    nextRoute = { ...nextRoute, detailTab };
  }

  const cut = parseCutParam(params.get("cut"));
  if (cut) {
    nextRoute = { ...nextRoute, cut };
  }

  if (profileParam != null) {
    const profileId = Number(profileParam);
    if (Number.isInteger(profileId) && profileId > 0) {
      nextRoute = {
        ...nextRoute,
        profileId,
      };
    }
  }
  if (params.get("segmentsView") === "raw") {
    nextRoute = { ...nextRoute, segmentsView: "raw" };
  }

  const dqParam = params.get("dq");
  if (dqParam) {
    const descriptor = decodeDerivedQueryDescriptor(dqParam);
    if (descriptor) {
      nextRoute = {
        ...nextRoute,
        derivedQueryDescriptor: descriptor,
      };
    }
  }

  if (seekParam == null) {
    return nextRoute;
  }

  const seekTo = Number(seekParam);
  if (!Number.isFinite(seekTo) || seekTo < 0) {
    return nextRoute;
  }

  return {
    ...nextRoute,
    seekTo,
  };
}

export function getPreviousInternalRoute(fallbackRoute: Route): {
  route: Route;
  label: string;
  hasHistory: boolean;
  url?: string;
  entryId?: string;
} {
  const history = readRouteHistory();
  const currentUrl = buildCurrentUrl(window.location.pathname, window.location.search);

  const currentIndex = findRouteHistoryIndex(history, readEntryId(), currentUrl);
  const previousEntry = currentIndex > 0 ? history[currentIndex - 1] : undefined;
  const route = previousEntry?.route ?? fallbackRoute;

  return {
    route,
    label: getRouteLabel(route),
    hasHistory: previousEntry != null,
    url: previousEntry?.url,
    entryId: previousEntry?.id,
  };
}
