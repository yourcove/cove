import { beforeEach, describe, expect, it } from "vitest";
import {
  buildRouteUrl,
  getPreviousInternalRoute,
  navigateToUrl,
  parseCurrentRoute,
  registerNavigationBlocker,
  resolveContextualDetailRoute,
  syncRouteHistory,
} from "../router/location";

const sessionEntries = new Map<string, string>();

beforeEach(() => {
  sessionEntries.clear();
  Object.defineProperty(window, "sessionStorage", {
    configurable: true,
    value: {
      getItem: (key: string) => sessionEntries.get(key) ?? null,
      setItem: (key: string, value: string) => {
        sessionEntries.set(key, value);
      },
      removeItem: (key: string) => {
        sessionEntries.delete(key);
      },
    },
  });

  window.history.replaceState(null, "", "/");
});

describe("route history", () => {
  it("lets an active editor block in-app navigation", () => {
    const unregister = registerNavigationBlocker(() => false);

    expect(navigateToUrl("/videos")).toBe(false);
    expect(window.location.pathname).toBe("/");

    unregister();
    expect(navigateToUrl("/videos")).toBe(true);
    expect(window.location.pathname).toBe("/videos");
  });

  it("parses and rebuilds video seek timestamps", () => {
    window.history.replaceState(null, "", "/video/42?t=91.5");

    expect(parseCurrentRoute()).toEqual({
      page: "video",
      id: 42,
      seekTo: 91.5,
    });

    expect(buildRouteUrl({ page: "video", id: 42, seekTo: 91.5 })).toBe("/video/42?t=91.5");
  });

  it.each([
    ["video", "videos"],
    ["videos", "videos"],
    ["gallery", "galleries"],
    ["galleries", "galleries"],
    ["image", "images"],
    ["images", "images"],
    ["audio", "audios"],
    ["audios", "audios"],
    ["text", "texts"],
    ["texts", "texts"],
  ])("opens related entity detail pages on the %s source tab", (sourcePage, detailTab) => {
    expect(resolveContextualDetailRoute({ page: "performer", id: 7 }, sourcePage)).toEqual({
      page: "performer",
      id: 7,
      detailTab,
    });
  });

  it("preserves an explicit detail tab over the source context", () => {
    expect(resolveContextualDetailRoute({ page: "performer", id: 7, detailTab: "faces" }, "gallery")).toEqual({
      page: "performer",
      id: 7,
      detailTab: "faces",
    });
  });

  it("serializes and parses an entity detail tab", () => {
    const url = buildRouteUrl({ page: "performer", id: 7, detailTab: "galleries" });

    expect(url).toBe("/performer/7?tab=galleries");
    window.history.replaceState(null, "", url);
    expect(parseCurrentRoute()).toEqual({ page: "performer", id: 7, detailTab: "galleries" });
  });

  it("parses and rebuilds parameterized extension page routes", () => {
    window.history.replaceState(null, "", "/reports/42");

    expect(parseCurrentRoute()).toEqual({
      page: "reports",
      id: 42,
    });

    expect(buildRouteUrl({ page: "reports", id: 42 })).toBe("/reports/42");
  });

  it("parses and rebuilds static child extension page routes", () => {
    window.history.replaceState(null, "", "/reports/settings");

    expect(parseCurrentRoute()).toEqual({
      page: "reports",
      slug: "settings",
    });

    expect(buildRouteUrl({ page: "reports", slug: "settings" })).toBe("/reports/settings");
  });

  it("keeps numeric children in the existing detail-id contract", () => {
    window.history.replaceState(null, "", "/reports/7");

    expect(parseCurrentRoute()).toEqual({
      page: "reports",
      id: 7,
    });
  });

  it.each(["0", "-1", "1.5", "%34%32"])("does not expose unsupported numeric child %s as a slug", (child) => {
    window.history.replaceState(null, "", `/reports/${child}`);

    expect(parseCurrentRoute()).toEqual({ page: "reports" });
  });

  it("does not treat deeper paths as a supported static child route", () => {
    window.history.replaceState(null, "", "/reports/settings/advanced");

    expect(parseCurrentRoute()).toEqual({ page: "reports" });
  });

  it("does not decode an unsupported deeper child path", () => {
    window.history.replaceState(null, "", "/reports/%E0%A4%A/advanced");

    expect(parseCurrentRoute()).toEqual({ page: "reports" });
  });

  it("includes saved list state in list route URLs", () => {
    expect(
      buildRouteUrl({
        page: "videos",
        listFilter: { q: "favorite", page: 1, perPage: 60, sort: "rating", direction: "desc" },
        listObjectFilter: { ratingCriterion: { modifier: "greater_than", value: 80 } },
        listView: "list",
      }),
    ).toBe(
      "/videos?q=favorite&page=1&perPage=60&sort=rating&direction=desc&filters=%7B%22ratingCriterion%22%3A%7B%22modifier%22%3A%22greater_than%22%2C%22value%22%3A80%7D%7D&view=list",
    );
  });

  it("preserves explicitly empty saved list state", () => {
    expect(
      buildRouteUrl({
        page: "videos",
        listFilter: { q: "" },
        listObjectFilter: {},
      }),
    ).toBe("/videos?q=&filters=%7B%7D");
  });

  it("preserves raw-segment view and display profile state", () => {
    const url = buildRouteUrl({ page: "segments", segmentsView: "raw", profileId: 7, listView: "list" });

    expect(url).toBe("/segments?view=list&segmentsView=raw&profile=7");
    window.history.replaceState(null, "", url);
    expect(parseCurrentRoute()).toEqual({ page: "segments", segmentsView: "raw", profileId: 7 });
  });

  it("keeps back labels aligned with browser back navigation after a popstate-style move", () => {
    window.history.replaceState(null, "", "/performers");
    syncRouteHistory("push");

    window.history.pushState(null, "", "/performer/1");
    syncRouteHistory("push");

    window.history.pushState(null, "", "/video/2");
    syncRouteHistory("push");

    window.history.replaceState(null, "", "/performer/1");
    syncRouteHistory("history");

    expect(getPreviousInternalRoute({ page: "performers" })).toEqual(
      expect.objectContaining({
        route: { page: "performers" },
        label: "Performers",
        hasHistory: true,
      }),
    );
  });

  it("replaces the current internal route when only URL state is replaced", () => {
    window.history.replaceState(null, "", "/studio/46?sort=title");
    syncRouteHistory("push");

    window.history.replaceState(null, "", "/studio/46?includeSubStudios=true&sort=title");
    syncRouteHistory("replace");

    expect(getPreviousInternalRoute({ page: "studios" })).toEqual({
      route: { page: "studios" },
      label: "Studios",
      hasHistory: false,
    });
  });
});
