import { afterEach, describe, expect, test } from "bun:test";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { CoveClient } from "../src/client";
import { ConfigStore } from "../src/config";
import { CliError } from "../src/errors";
import { defaultSavedFilters, listSavedFilters, queryForSavedFilter, resolveSavedFilter, savedFilterSummary } from "../src/saved-filters";
import type { SavedFilter } from "../src/types";
import { json, startServer } from "./helpers";

const servers: Bun.Server<unknown>[] = [];
const directories: string[] = [];
afterEach(async () => {
  for (const server of servers.splice(0)) server.stop(true);
  await Promise.all(directories.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
});

async function clientWith(handler: (request: Request) => Response | Promise<Response>): Promise<CoveClient> {
  const running = startServer(handler);
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  return new CoveClient({ store: new ConfigStore(directory), profileName: "test", profile: { server: running.url } });
}

function savedFilter(overrides: Partial<SavedFilter> = {}): SavedFilter {
  return { id: 12, mode: "videos", name: "Recently Added", ...overrides };
}

describe("saved filter discovery", () => {
  test("reads account-backed defaults per view", async () => {
    const client = await clientWith(() => json({ user: { uiPreferences: { defaultFilters: {
      videos: JSON.stringify({ findFilter: { sort: "date", direction: "desc" }, objectFilter: { organizedCriterion: { value: true } }, uiOptions: { displayMode: "grid" } }),
      "gallery-images": JSON.stringify({ findFilter: { sort: "title", direction: "asc" } }),
    } } } }));
    expect(await defaultSavedFilters(client)).toEqual([
      { mode: "gallery-images", findFilter: JSON.stringify({ sort: "title", direction: "asc" }), objectFilter: undefined, uiOptions: undefined },
      { mode: "videos", findFilter: JSON.stringify({ sort: "date", direction: "desc" }), objectFilter: JSON.stringify({ organizedCriterion: { value: true } }), uiOptions: JSON.stringify({ displayMode: "grid" }) },
    ]);
  });

  test("ignores malformed account-backed defaults", async () => {
    const client = await clientWith(() => json({ user: { uiPreferences: { defaultFilters: { videos: "{", images: "[]", tags: "{}" } } } }));
    expect(await defaultSavedFilters(client)).toEqual([{ mode: "tags", findFilter: undefined, objectFilter: undefined, uiOptions: undefined }]);
  });

  test("direct default inspection surfaces authentication failures", async () => {
    const client = await clientWith(() => json({ error: "unauthorized" }, 401));
    await expect(defaultSavedFilters(client)).rejects.toMatchObject({ status: 401 });
  });
  test("lists and resolves filters across modes", async () => {
    const filters = [savedFilter(), savedFilter({ id: 14, mode: "images", name: "Unorganized" })];
    const client = await clientWith(() => json(filters));
    expect(await listSavedFilters(client)).toEqual(filters);
    expect(await resolveSavedFilter(client, "unorganized", "images")).toEqual(filters[1]!);
  });
  test("lists only the server's video-mode response and resolves exact names", async () => {
    const filters = [savedFilter(), savedFilter({ id: 13, name: "Favorites" })];
    const client = await clientWith(request => {
      const url = new URL(request.url);
      expect(url.pathname).toBe("/api/savedfilters");
      expect(url.searchParams.get("mode")).toBe("videos");
      return json(filters);
    });
    expect(await listSavedFilters(client, "videos")).toEqual(filters);
    expect(await resolveSavedFilter(client, "favorites", "videos")).toEqual(filters[1]!);
  });

  test("loads numeric IDs directly and rejects another filter mode", async () => {
    const client = await clientWith(() => json(savedFilter({ mode: "images" })));
    await expect(resolveSavedFilter(client, "12", "videos")).rejects.toMatchObject({ code: "SAVED_FILTER_MODE_MISMATCH" });
  });

  test("rejects ambiguous exact names with candidate IDs", async () => {
    const client = await clientWith(() => json([savedFilter(), savedFilter({ id: 14 })]));
    try {
      await resolveSavedFilter(client, "recently added", "videos");
      throw new Error("expected ambiguity");
    } catch (error) {
      expect(error).toBeInstanceOf(CliError);
      expect(error).toMatchObject({ code: "SAVED_FILTER_AMBIGUOUS", details: { candidates: [{ id: 12 }, { id: 14 }] } });
    }
  });
});

describe("composing saved filters", () => {
  test("summarizes entity criteria with the UI's natural-list wording", async () => {
    const names = new Map([[1, "Alexis Crystal"], [2, "Lexi Dona"], [3, "Paula Shy"]]);
    const client = await clientWith(request => {
      const id = Number(new URL(request.url).pathname.split("/").at(-1));
      return json({ id, name: names.get(id) });
    });
    const summary = await savedFilterSummary(client, {
      sorts: [{ key: "random", direction: "desc" }],
      objectFilter: { performersCriterion: { value: [1, 2, 3], modifier: "includesAll" } },
    });
    expect(summary).toBe("Default filter · Sort: Random descending · Performers: Alexis Crystal, Lexi Dona, and Paula Shy");
  });

  test("summarizes exclusions, hierarchy depth, and ranges with UI semantics", async () => {
    const client = await clientWith(() => json({}, 404));
    const summary = await savedFilterSummary(client, {
      sorts: [],
      objectFilter: {
        tagsCriterion: { value: [1, 2], excludes: [3], modifier: "excludesAll", depth: -1, _names: { 1: "First", 2: "Second", 3: "Third" } },
        durationCriterion: { value: 10, value2: 20, modifier: "between" },
      },
    });
    expect(summary).toBe("Default filter · Tags: not all of First, Second, and Third with sub-tags · Duration: Between 10 and 20");
  });

  test("extracts search, sorting, and normalized criteria for list queries", () => {
    const query = queryForSavedFilter(savedFilter({
      findFilter: JSON.stringify({ q: "term", page: 8, perPage: 10, sort: "date", direction: "asc" }),
      objectFilter: JSON.stringify({ titleCriterion: { value: "demo", modifier: "MATCHES_REGEX" }, includeCompilationGroups: { value: false } }),
    }), "videos");
    expect(query).toEqual({
      q: "term",
      sorts: [{ key: "date", direction: "asc" }],
      objectFilter: { titleCriterion: { value: "demo", modifier: "matchesRegex" } },
    });
  });

  test("preserves or generates a stable seed for random sorting", () => {
    expect(queryForSavedFilter(savedFilter({ findFilter: JSON.stringify({ sort: "random", seed: 42 }) }), "videos").seed).toBe(42);
    expect(queryForSavedFilter(savedFilter({ findFilter: JSON.stringify({ sort: "random" }) }), "videos").seed).toBeNumber();
  });

  test("treats nullable saved find-filter fields as absent", () => {
    expect(queryForSavedFilter(savedFilter({ findFilter: JSON.stringify({ q: null, sort: null, direction: null, sorts: null, seed: null }) }), "videos")).toEqual({
      sorts: [{ key: "date", direction: "desc" }],
      objectFilter: {},
    });
  });

  test("canonicalizes backend-valid direction casing", () => {
    expect(queryForSavedFilter(savedFilter({ findFilter: JSON.stringify({ sort: "date", direction: "DESC" }) }), "videos").sorts).toEqual([{ key: "date", direction: "desc" }]);
    expect(queryForSavedFilter(savedFilter({ findFilter: JSON.stringify({ sorts: [{ key: "title", direction: "ASC" }, { key: "date", direction: "DESC" }] }) }), "videos").sorts).toEqual([
      { key: "title", direction: "asc" },
      { key: "date", direction: "desc" },
    ]);
  });

  test("uses Cove's per-view built-in sort when a saved default omits sorting", () => {
    expect(queryForSavedFilter(savedFilter({ mode: "audios", findFilter: "{}" }), "audios").sorts).toEqual([{ key: "date", direction: "desc" }]);
    expect(queryForSavedFilter(savedFilter({ mode: "texts", findFilter: "{}" }), "texts").sorts).toEqual([{ key: "date", direction: "desc" }]);
    expect(queryForSavedFilter(savedFilter({ mode: "performers", findFilter: "{}" }), "performers").sorts).toEqual([{ key: "latest_video_date", direction: "desc" }]);
  });

  test("rejects malformed, mismatched, compilation-inclusive, and visual-only filters", () => {
    expect(() => queryForSavedFilter(savedFilter({ findFilter: "{" }), "videos")).toThrow("invalid findFilter JSON");
    expect(() => queryForSavedFilter(savedFilter({ mode: "images" }), "videos")).toThrow("belongs to");
    expect(() => queryForSavedFilter(savedFilter({ objectFilter: JSON.stringify({ includeCompilationGroups: { value: true } }) }), "videos")).toThrow("compilation groups");
    expect(() => queryForSavedFilter(savedFilter({ findFilter: JSON.stringify({ sort: "visual_match" }) }), "videos")).toThrow("Visual-similarity");
    expect(() => queryForSavedFilter(savedFilter({ findFilter: JSON.stringify({ sorts: {} }) }), "videos")).toThrow("compound sort");
    expect(() => queryForSavedFilter(savedFilter({ findFilter: JSON.stringify({ sorts: [null] }) }), "videos")).toThrow("compound sort");
  });
});
