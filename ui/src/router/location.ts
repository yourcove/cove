export interface Route {
  page: string;
  id?: number;
}

export interface RoutePointerEvent {
  button?: number;
  ctrlKey?: boolean;
  metaKey?: boolean;
  preventDefault(): void;
}

export const LOCATION_CHANGE_EVENT = "cove-locationchange";

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

export function openRouteInNewTab(route: Route) {
  window.open(buildRoutePath(route), "_blank", "noopener,noreferrer");
}

export function handleModifiedRouteNavigation(event: RoutePointerEvent, route: Route): boolean {
  const button = event.button ?? 0;
  if (button !== 1 && !event.ctrlKey && !event.metaKey) {
    return false;
  }

  event.preventDefault();
  openRouteInNewTab(route);
  return true;
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