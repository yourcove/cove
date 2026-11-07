import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthContext } from "../auth/optionalAuth";
import type { AuthContextValue } from "../auth/AuthContext";
import type { BackNavigationTarget } from "../api/types";
import { useBackNavigation } from "../hooks/useBackNavigation";
import {
  emitLocationChange,
  navigateToUrl,
  readBackOrigin,
  resolveCurrentRoute,
  syncRouteHistory,
  type Route,
} from "../router/location";
import { restoreWindowScroll } from "../utils/restoreWindowScroll";

const sessionEntries = new Map<string, string>();
let scrollY = 0;

/** Navigate the way App does: push the entry, then record it in the session route history. */
function visit(url: string, route: Route, options?: { replace?: boolean }) {
  navigateToUrl(url, { state: route, replace: options?.replace });
  syncRouteHistory(options?.replace ? "replace" : "push");
}

function wrapperFor(target: BackNavigationTarget | undefined) {
  const auth = {
    user: { uiPreferences: target ? { backNavigationTarget: target } : null },
  } as unknown as AuthContextValue;
  return ({ children }: { children: ReactNode }) => (
    <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>
  );
}

beforeEach(() => {
  sessionEntries.clear();
  Object.defineProperty(window, "sessionStorage", {
    configurable: true,
    value: {
      getItem: (key: string) => sessionEntries.get(key) ?? null,
      setItem: (key: string, value: string) => sessionEntries.set(key, value),
      removeItem: (key: string) => sessionEntries.delete(key),
    },
  });
  scrollY = 0;
  Object.defineProperty(window, "scrollY", { configurable: true, get: () => scrollY });
  vi.spyOn(window, "scrollTo").mockImplementation(((x: number, y: number) => {
    scrollY = y;
  }) as typeof window.scrollTo);
  window.history.replaceState(null, "", "/");
});

afterEach(() => {
  // Leaving the page ends any scroll restore a test started.
  emitLocationChange();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("back origin", () => {
  it("keeps the list a run of videos started from on every video in the run", () => {
    visit("/videos?page=3", { page: "videos" });
    scrollY = 1200;
    visit("/video/1", { page: "video", id: 1 });
    scrollY = 0;
    visit("/video/2", { page: "video", id: 2 });
    visit("/video/3", { page: "video", id: 3 });

    expect(readBackOrigin()).toMatchObject({ url: "/videos?page=3", route: { page: "videos" }, scrollY: 1200 });
    // The list remembers where its own run started, the home page this test begins on.
    expect(readBackOrigin()?.origin?.url).toBe("/");
  });

  it("starts a new run when the page type changes", () => {
    visit("/videos", { page: "videos" });
    visit("/video/1", { page: "video", id: 1 });
    visit("/tag/5", { page: "tag", id: 5 });
    visit("/tag/6", { page: "tag", id: 6 });

    expect(readBackOrigin()?.url).toBe("/video/1");
  });

  it("keeps the origin when the current entry is replaced", () => {
    visit("/performers", { page: "performers" });
    visit("/performer/1", { page: "performer", id: 1 });
    visit("/performer/1?tab=videos", { page: "performer", id: 1, detailTab: "videos" }, { replace: true });

    expect(readBackOrigin()?.url).toBe("/performers");
  });

  it("keeps the origin when a tab change replaces the entry without a route state", () => {
    visit("/performers", { page: "performers" });
    visit("/performer/1", { page: "performer", id: 1 });
    visit("/performer/2", { page: "performer", id: 2 });
    navigateToUrl("/performer/2?tab=galleries", { replace: true });
    syncRouteHistory("replace");

    expect(readBackOrigin()?.url).toBe("/performers");
    // No stale route is stored, so the tab still comes from the URL.
    expect(resolveCurrentRoute()).toMatchObject({ page: "performer", id: 2, detailTab: "galleries" });

    visit("/performer/3", { page: "performer", id: 3 });
    expect(readBackOrigin()?.url).toBe("/performers");
  });

  it("records an origin for a push without a route state", () => {
    visit("/videos", { page: "videos" });
    navigateToUrl("/video/1");
    syncRouteHistory("push");

    expect(readBackOrigin()?.url).toBe("/videos");
  });

  it("leaves state written by other code untouched", () => {
    visit("/videos", { page: "videos" });
    navigateToUrl("/video/1", { state: { editor: true } });

    expect(window.history.state).toEqual({ editor: true });
  });

  it("keeps the origin out of the route read back from history state", () => {
    visit("/videos", { page: "videos" });
    visit("/video/1", { page: "video", id: 1 });

    expect(resolveCurrentRoute()).toEqual({ page: "video", id: 1 });
  });
});

describe("useBackNavigation", () => {
  function startRun() {
    visit("/videos?page=3", { page: "videos" });
    scrollY = 900;
    visit("/video/1", { page: "video", id: 1 });
    visit("/video/2", { page: "video", id: 2 });
    visit("/video/3", { page: "video", id: 3 });
  }

  it("returns to where the run started by default and offers the previous page beside it", () => {
    startRun();
    const historyBack = vi.spyOn(window.history, "back").mockImplementation(() => {});
    const { result } = renderHook(() => useBackNavigation({ page: "videos" }, vi.fn()), {
      wrapper: wrapperFor(undefined),
    });

    expect(result.current.backLabel).toBe("Back to Videos");
    expect(result.current.backAlternative?.label).toBe("Back to previous video");

    act(() => result.current.backAlternative?.onSelect());
    expect(historyBack).toHaveBeenCalledTimes(1);

    scrollY = 0;
    act(() => result.current.goBack());
    expect(`${window.location.pathname}${window.location.search}`).toBe("/videos?page=3");
    expect(window.history.state).toMatchObject({ page: "videos" });
    expect(window.scrollTo).toHaveBeenCalledWith(0, 900);
  });

  it("steps back one page when the preference is the previous page", () => {
    startRun();
    const historyBack = vi.spyOn(window.history, "back").mockImplementation(() => {});
    const { result } = renderHook(() => useBackNavigation({ page: "videos" }, vi.fn()), {
      wrapper: wrapperFor("previous"),
    });

    expect(result.current.backLabel).toBe("Back to previous video");
    expect(result.current.backAlternative?.label).toBe("Back to Videos");

    act(() => result.current.goBack());
    expect(historyBack).toHaveBeenCalledTimes(1);
  });

  it("leaves for the origin after a delete even when Back steps one page", () => {
    startRun();
    const { result } = renderHook(() => useBackNavigation({ page: "videos" }, vi.fn()), {
      wrapper: wrapperFor("previous"),
    });

    const entries = window.history.length;
    act(() => result.current.goToOrigin());
    expect(`${window.location.pathname}${window.location.search}`).toBe("/videos?page=3");
    // The deleted item's entry is replaced, so browser Back cannot return to it.
    expect(window.history.length).toBe(entries);
  });

  it("returns to an origin that is itself in a run with that run's origin", () => {
    visit("/videos", { page: "videos" });
    visit("/video/1", { page: "video", id: 1 });
    visit("/video/2", { page: "video", id: 2 });
    visit("/performer/3", { page: "performer", id: 3 });
    visit("/performer/4", { page: "performer", id: 4 });
    const first = renderHook(() => useBackNavigation({ page: "performers" }, vi.fn()), {
      wrapper: wrapperFor(undefined),
    });
    expect(first.result.current.backLabel).toBe("Back to Video");

    act(() => first.result.current.goBack());
    syncRouteHistory("push");
    expect(window.location.pathname).toBe("/video/2");

    const second = renderHook(() => useBackNavigation({ page: "videos" }, vi.fn()), {
      wrapper: wrapperFor(undefined),
    });
    expect(second.result.current.backLabel).toBe("Back to Videos");
    expect(second.result.current.backAlternative?.label).toBe("Back to Performer");
  });

  it("uses the origin as the only target when no previous page was recorded", () => {
    startRun();
    sessionEntries.clear();
    const onNavigate = vi.fn();
    const { result } = renderHook(() => useBackNavigation({ page: "videos" }, onNavigate), {
      wrapper: wrapperFor("previous"),
    });

    expect(result.current.backLabel).toBe("Back to Videos");
    expect(result.current.backAlternative).toBeUndefined();
    act(() => result.current.goBack());
    expect(`${window.location.pathname}${window.location.search}`).toBe("/videos?page=3");
    expect(onNavigate).not.toHaveBeenCalled();
  });

  it("offers a single target when the origin is the previous page", () => {
    visit("/videos", { page: "videos" });
    visit("/video/1", { page: "video", id: 1 });
    const historyBack = vi.spyOn(window.history, "back").mockImplementation(() => {});
    const { result } = renderHook(() => useBackNavigation({ page: "videos" }, vi.fn()), {
      wrapper: wrapperFor(undefined),
    });

    expect(result.current.backLabel).toBe("Back to Videos");
    expect(result.current.backAlternative).toBeUndefined();

    act(() => result.current.goToOrigin());
    expect(historyBack).toHaveBeenCalledTimes(1);
  });

  it("falls back to the page's list without any recorded history", () => {
    window.history.replaceState({ page: "video", id: 9 }, "", "/video/9");
    const onNavigate = vi.fn();
    const { result } = renderHook(() => useBackNavigation({ page: "videos" }, onNavigate), {
      wrapper: wrapperFor(undefined),
    });

    expect(result.current.backAlternative).toBeUndefined();
    act(() => result.current.goBack());
    expect(onNavigate).toHaveBeenCalledWith({ page: "videos" });
  });
});

describe("returning to an origin and browser history", () => {
  /** Move through the browser's history the way its buttons do, then sync like App's popstate handler. */
  async function browserGo(delta: number) {
    await new Promise<void>((resolve) => {
      window.addEventListener("popstate", () => resolve(), { once: true });
      window.history.go(delta);
    });
    syncRouteHistory("history");
  }

  function backNavigation(fallback: Route, onNavigate = vi.fn()) {
    return renderHook(() => useBackNavigation(fallback, onNavigate), { wrapper: wrapperFor(undefined) }).result.current;
  }

  function goBackAndSync(fallback: Route) {
    const back = backNavigation(fallback);
    act(() => back.goBack());
    syncRouteHistory("push");
  }

  it("does not loop when the page returned to has no origin of its own", () => {
    // Opened directly, as from a bookmark: nothing came before it.
    window.history.replaceState(null, "", "/video/2");
    syncRouteHistory("push");
    visit("/performer/3", { page: "performer", id: 3 });
    visit("/performer/4", { page: "performer", id: 4 });

    expect(backNavigation({ page: "performers" }).backLabel).toBe("Back to Video");
    goBackAndSync({ page: "performers" });
    expect(window.location.pathname).toBe("/video/2");

    const onNavigate = vi.fn();
    const back = backNavigation({ page: "videos" }, onNavigate);
    expect(back.backLabel).toBe("Back to Videos");
    expect(back.backAlternative?.label).toBe("Back to Performer");
    act(() => back.goBack());
    expect(onNavigate).toHaveBeenCalledWith({ page: "videos" });
  });

  it("keeps a returned page's origin when its previous page is of the same kind as that origin", () => {
    visit("/videos", { page: "videos" });
    visit("/video/1", { page: "video", id: 1 });
    visit("/performer/2", { page: "performer", id: 2 });
    visit("/performer/3", { page: "performer", id: 3 });
    visit("/video/4", { page: "video", id: 4 });
    visit("/video/5", { page: "video", id: 5 });
    goBackAndSync({ page: "videos" });
    expect(window.location.pathname).toBe("/performer/3");

    const back = backNavigation({ page: "performers" });
    expect(back.backLabel).toBe("Back to Video");
    expect(back.backAlternative?.label).toBe("Back to previous video");
    act(() => back.goBack());
    expect(window.location.pathname).toBe("/video/1");
  });

  it("treats an origin whose page later rewrote its URL silently as the previous page", () => {
    visit("/duplicates", { page: "duplicates" });
    // The page records its own sort in the URL without telling the app, as DuplicateFinderPage does.
    window.history.replaceState(window.history.state, "", "/duplicates?sort=size");
    visit("/video/1", { page: "video", id: 1 });

    const back = backNavigation({ page: "videos" });
    expect(back.backLabel).toBe("Back to Duplicates");
    expect(back.backAlternative).toBeUndefined();
  });

  it("matches a session recorded before entries had ids by URL", () => {
    sessionEntries.set(
      "cove-route-history",
      JSON.stringify([
        { url: "/videos", route: { page: "videos" } },
        { url: "/video/1", route: { page: "video", id: 1 } },
      ]),
    );
    window.history.replaceState({ page: "video", id: 1 }, "", "/video/1");
    syncRouteHistory("push");

    const back = backNavigation({ page: "videos" });
    expect(back.backLabel).toBe("Back to Videos");
    expect(JSON.parse(sessionEntries.get("cove-route-history") ?? "[]")).toHaveLength(2);
  });

  it("keeps the previous page right after browser Back and Forward across a returned page", async () => {
    visit("/videos", { page: "videos" });
    visit("/video/1", { page: "video", id: 1 });
    visit("/video/2", { page: "video", id: 2 });
    visit("/performer/3", { page: "performer", id: 3 });
    visit("/performer/4", { page: "performer", id: 4 });
    goBackAndSync({ page: "performers" });
    expect(window.location.pathname).toBe("/video/2");

    await browserGo(-1);
    expect(window.location.pathname).toBe("/performer/4");
    await browserGo(1);
    expect(window.location.pathname).toBe("/video/2");

    const back = backNavigation({ page: "videos" });
    expect(back.backLabel).toBe("Back to Videos");
    expect(back.backAlternative?.label).toBe("Back to Performer");
  });

  it("still offers both targets after browser Back, Forward and Back around a returned list", async () => {
    visit("/videos", { page: "videos" });
    visit("/video/1", { page: "video", id: 1 });
    visit("/video/2", { page: "video", id: 2 });
    visit("/video/3", { page: "video", id: 3 });
    goBackAndSync({ page: "videos" });
    expect(window.location.pathname).toBe("/videos");

    await browserGo(-1);
    await browserGo(1);
    await browserGo(-1);
    expect(window.location.pathname).toBe("/video/3");

    const back = backNavigation({ page: "videos" });
    expect(back.backLabel).toBe("Back to Videos");
    expect(back.backAlternative?.label).toBe("Back to previous video");
  });
});

describe("restoreWindowScroll", () => {
  it("re-applies the offset while the page grows until it holds", () => {
    vi.useFakeTimers();
    let maxScroll = 300;
    vi.mocked(window.scrollTo).mockImplementation(((x: number, y: number) => {
      scrollY = Math.min(y, maxScroll);
    }) as typeof window.scrollTo);

    restoreWindowScroll(1000);
    expect(scrollY).toBe(300);

    maxScroll = 2000; // the next infinite-scroll page arrived
    vi.advanceTimersByTime(100);
    expect(scrollY).toBe(1000);

    vi.advanceTimersByTime(1000);
    const calls = vi.mocked(window.scrollTo).mock.calls.length;
    scrollY = 0; // a later reset no longer gets undone once restoring has settled
    vi.advanceTimersByTime(1000);
    expect(vi.mocked(window.scrollTo).mock.calls.length).toBe(calls);
  });

  it("stops as soon as the user scrolls", () => {
    vi.useFakeTimers();
    vi.mocked(window.scrollTo).mockImplementation(((x: number, y: number) => {
      scrollY = Math.min(y, 300);
    }) as typeof window.scrollTo);

    restoreWindowScroll(1000);
    window.dispatchEvent(new Event("wheel"));
    const calls = vi.mocked(window.scrollTo).mock.calls.length;
    vi.advanceTimersByTime(1000);

    expect(vi.mocked(window.scrollTo).mock.calls.length).toBe(calls);
  });

  it("keeps going while the page replaces its own URL and stops once another page opens", () => {
    vi.useFakeTimers();
    vi.mocked(window.scrollTo).mockImplementation(((x: number, y: number) => {
      scrollY = Math.min(y, 300);
    }) as typeof window.scrollTo);

    restoreWindowScroll(1000);
    emitLocationChange({ replace: true });
    let calls = vi.mocked(window.scrollTo).mock.calls.length;
    vi.advanceTimersByTime(200);
    expect(vi.mocked(window.scrollTo).mock.calls.length).toBeGreaterThan(calls);

    emitLocationChange();
    calls = vi.mocked(window.scrollTo).mock.calls.length;
    vi.advanceTimersByTime(1000);
    expect(vi.mocked(window.scrollTo).mock.calls.length).toBe(calls);
  });

  it("gives up after the timeout", () => {
    vi.useFakeTimers();
    vi.mocked(window.scrollTo).mockImplementation(((x: number, y: number) => {
      scrollY = Math.min(y, 300);
    }) as typeof window.scrollTo);

    restoreWindowScroll(1000);
    vi.advanceTimersByTime(8000);
    const calls = vi.mocked(window.scrollTo).mock.calls.length;
    vi.advanceTimersByTime(1000);

    expect(vi.mocked(window.scrollTo).mock.calls.length).toBe(calls);
  });
});
