import type { PaginatedResponse, Video } from "../api/types";

/**
 * A video list the headset can show: the same items, page and page size the browser's list shows.
 * Pages that render a video list hand one to their toolbar's "View in VR" button.
 */
export interface VrListSource {
  /** Shown above the wall, e.g. "Videos" or the tag's name. */
  label: string;
  /**
   * Identifies the list: which page it is on and its filters and sort, page number aside. When it
   * changes the wall reloads from the start; see {@link listKey}.
   */
  key: string;
  /** The list's current page and page size, so the wall starts near where the browser is. */
  page: number;
  perPage: number;
  /** Fetches a page exactly as the browser's list would, or restricted to VR videos when asked. */
  fetchPage: (page: number, perPage: number, vrOnly: boolean) => Promise<PaginatedResponse<Video>>;
  /** Moves the browser's list to the page the headset moved to. */
  setPage?: (page: number) => void;
}

interface RegisteredList {
  source: VrListSource;
  /** Path and query of the page that showed the list, so the headset can bring the browser back. */
  url: string;
}

// The list most recently shown to the user. It survives the page unmounting, so a video opened from
// it (in the browser or the headset) knows where "back" leads.
let lastList: RegisteredList | null = null;
// A live immersive session waiting for the list page to mount and show the wall in it.
let pendingWallSession: { session: object; url: string } | null = null;

/** Called by a list page while it is on screen; the newest registration wins. */
export function registerVrList(source: VrListSource): void {
  lastList = { source, url: `${window.location.pathname}${window.location.search}` };
}

/** The list the user last had on screen, if any. */
export function lastVrList(): RegisteredList | null {
  return lastList;
}

/**
 * Keeps a session alive for the list page at `url`, which claims it on mount with
 * {@link claimWallSession} and shows the wall in it. Unclaimed sessions are ended by the caller's timeout.
 */
export function offerWallSession(session: object, url: string): void {
  pendingWallSession = { session, url };
}

/** Takes the session waiting for this page, if the page is the one it was left for. */
export function claimWallSession(url: string): object | null {
  if (!pendingWallSession || pendingWallSession.url !== url) return null;
  const { session } = pendingWallSession;
  pendingWallSession = null;
  return session;
}

/** Whether a session is waiting for some list page. */
export function hasPendingWallSession(): boolean {
  return pendingWallSession != null;
}

/** Forgets a session nobody claimed. True when it was still waiting. */
export function dropWallSession(session: object): boolean {
  if (pendingWallSession?.session !== session) return false;
  pendingWallSession = null;
  return true;
}

/** A {@link VrListSource.key}: the current path plus whatever defines the list, with paging left out. */
export function listKey(
  filter: { page?: number; perPage?: number; [key: string]: unknown },
  ...extra: unknown[]
): string {
  const { page: _page, perPage: _perPage, ...rest } = filter as Record<string, unknown>;
  return JSON.stringify([window.location.pathname, rest, ...extra]);
}

/** The page's own title, as a label for the wall: Cove sets document.title to "<page> | <app>". */
export function pageLabel(fallback = "Videos"): string {
  const title = document.title.split(" | ")[0]?.trim();
  return title || fallback;
}

/** A video object filter restricted to VR videos, as the wall's "VR only" asks for. */
export function vrOnlyFilter<T extends Record<string, unknown>>(
  objectFilter: T,
): T & { isVrCriterion: { value: boolean } } {
  return { ...objectFilter, isVrCriterion: { value: true } };
}
