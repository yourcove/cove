import type { FindFilter, SegmentDerivedQueryDescriptor } from "../api/types";

export interface Route {
  page: string;
  id?: number;
  seekTo?: number;
  spanKey?: string;
  profileId?: number;
  derivedQueryDescriptor?: SegmentDerivedQueryDescriptor;
  manualTopicId?: string;
  manualSlideId?: string;
  listFilter?: FindFilter;
  listObjectFilter?: Record<string, unknown>;
  listView?: string;
  compilationItemOrder?: string[];
}

interface RouteHistoryEntry {
  url: string;
  route: Route;
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
    return applyRouteSearch({
      page: "manual",
      manualTopicId: parts.length > 1 ? decodeURIComponent(parts[1]) : undefined,
      manualSlideId: parts.length > 2 ? decodeURIComponent(parts[2]) : undefined,
    }, search);
  }

  const page = parts[0];
  const id = parts.length > 1 ? Number(parts[1]) : undefined;
  if (id != null && Number.isInteger(id) && id > 0) {
    return applyRouteSearch({ page, id }, search);
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

function readCurrentStateRoute(): Route | undefined {
  return isRouteState(window.history.state) ? window.history.state : undefined;
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
  if (route.seekTo != null && Number.isFinite(route.seekTo) && route.seekTo >= 0) {
    params.set("t", String(route.seekTo));
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

export function navigateToUrl(url: string, options?: { replace?: boolean; state?: unknown }) {
  const currentUrl = `${window.location.pathname}${window.location.search}`;
  if (currentUrl === url) {
    return;
  }

  if (options?.replace) {
    window.history.replaceState(options?.state ?? null, "", url);
  } else {
    window.history.pushState(options?.state ?? null, "", url);
  }

  emitLocationChange({ replace: options?.replace });
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
      return entry != null && typeof entry.url === "string" && entry.route != null && typeof entry.route.page === "string";
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

export function readStoredRoute(url: string = buildCurrentUrl(window.location.pathname, window.location.search)): Route | undefined {
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
  const currentEntry: RouteHistoryEntry = {
    url: buildCurrentUrl(window.location.pathname, window.location.search),
    route: readCurrentStateRoute() ?? parseCurrentRoute(),
  };

  const history = readRouteHistory();
  if (mode === "replace" && history.length > 0)
  {
    history[history.length - 1] = currentEntry;
    writeRouteHistory(history);
    return;
  }

  if (mode === "history")
  {
    for (let index = history.length - 1; index >= 0; index -= 1)
    {
      if (history[index].url === currentEntry.url)
      {
        writeRouteHistory(history.slice(0, index + 1));
        return;
      }
    }
  }

  const lastEntry = history.length > 0 ? history[history.length - 1] : undefined;
  if (lastEntry?.url === currentEntry.url) {
    return;
  }

  history.push(currentEntry);
  writeRouteHistory(history);
}

function getRouteLabel(route: Route): string {
  switch (route.page) {
    case "home": return "Home";
    case "video": return "Video";
    case "audio": return "Audio";
    case "audios": return "Audios";
    case "text": return "Text";
    case "texts": return "Texts";
    case "video-span": return "Span";
    case "videos": return "Videos";
    case "segment": return "Segment";
    case "segments": return "Segments";
    case "faces": return "Faces";
    case "image": return "Image";
    case "images": return "Images";
    case "gallery": return "Gallery";
    case "galleries": return "Galleries";
    case "group": return "Group";
    case "groups": return "Groups";
    case "compilation": return "Compilation";
    case "performer": return "Performer";
    case "performers": return "Performers";
    case "studio": return "Studio";
    case "studios": return "Studios";
    case "tag": return "Tag";
    case "tags": return "Tags";
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
  let nextRoute = route;

  if (profileParam != null) {
    const profileId = Number(profileParam);
    if (Number.isInteger(profileId) && profileId > 0) {
      nextRoute = {
        ...nextRoute,
        profileId,
      };
    }
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

export function getPreviousInternalRoute(fallbackRoute: Route): { route: Route; label: string; hasHistory: boolean } {
  const history = readRouteHistory();
  const currentUrl = buildCurrentUrl(window.location.pathname, window.location.search);

  let currentIndex = -1;
  for (let index = history.length - 1; index >= 0; index -= 1) {
    if (history[index].url === currentUrl) {
      currentIndex = index;
      break;
    }
  }

  const previousEntry = currentIndex > 0 ? history[currentIndex - 1] : undefined;
  const route = previousEntry?.route ?? fallbackRoute;

  return {
    route,
    label: getRouteLabel(route),
    hasHistory: previousEntry != null,
  };
}
