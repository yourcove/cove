export interface Route {
  page: string;
  id?: number;
}

interface RouteHistoryEntry {
  url: string;
  route: Route;
}

export const LOCATION_CHANGE_EVENT = "cove-locationchange";
const ROUTE_HISTORY_KEY = "cove-route-history";

function parsePath(pathname: string): Route {
  const parts = pathname.split("/").filter(Boolean);
  if (parts.length === 0 || parts[0] === "home") {
    return { page: "home" };
  }

  const page = parts[0];
  const id = parts.length > 1 ? Number(parts[1]) : undefined;
  if (id != null && Number.isInteger(id) && id > 0) {
    return { page, id };
  }

  return { page };
}

export function parseLegacyHashRoute(hash: string): Route | null {
  if (!hash.startsWith("#/")) {
    return null;
  }

  return parsePath(hash.slice(1));
}

export function parseCurrentRoute(): Route {
  return parsePath(window.location.pathname);
}

export function buildRoutePath(route: Route): string {
  if (!route.page || route.page === "home") {
    return "/";
  }

  if (route.id != null) {
    return `/${route.page}/${route.id}`;
  }

  return `/${route.page}`;
}

export function buildCurrentUrl(pathname: string, search?: URLSearchParams | string | null): string {
  if (search == null) {
    return pathname;
  }

  const searchString = search instanceof URLSearchParams ? search.toString() : search.replace(/^\?/, "");
  return searchString ? `${pathname}?${searchString}` : pathname;
}

export function emitLocationChange() {
  window.dispatchEvent(new Event(LOCATION_CHANGE_EVENT));
}

export function navigateToUrl(url: string, options?: { replace?: boolean }) {
  const currentUrl = `${window.location.pathname}${window.location.search}`;
  if (currentUrl === url) {
    return;
  }

  if (options?.replace) {
    window.history.replaceState(null, "", url);
  } else {
    window.history.pushState(null, "", url);
  }

  emitLocationChange();
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

export function syncRouteHistory() {
  const currentEntry: RouteHistoryEntry = {
    url: buildCurrentUrl(window.location.pathname, window.location.search),
    route: parseCurrentRoute(),
  };

  const history = readRouteHistory();
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
    case "scene": return "Scene";
    case "scenes": return "Scenes";
    case "image": return "Image";
    case "images": return "Images";
    case "gallery": return "Gallery";
    case "galleries": return "Galleries";
    case "group": return "Group";
    case "groups": return "Groups";
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