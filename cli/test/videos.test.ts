import { afterEach, describe, expect, test } from "bun:test";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { CoveClient } from "../src/client";
import { ConfigStore } from "../src/config";
import { CliError } from "../src/errors";
import type { Performer, Tag, Video } from "../src/types";
import { resolvePerformer, resolveTag, videosForCriteria } from "../src/videos";
import { json, page, startServer } from "./helpers";

const servers: Bun.Server<unknown>[] = [];
const directories: string[] = [];
afterEach(async () => {
  for (const server of servers.splice(0)) server.stop(true);
  await Promise.all(directories.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
});

function performer(id: number, name: string, aliases: string[] = []): Performer {
  return { id, name, aliases };
}

function tag(id: number, name: string, aliases: string[] = []): Tag {
  return { id, name, aliases };
}

async function clientWith(handler: (request: Request) => Response | Promise<Response>): Promise<CoveClient> {
  const running = startServer(handler);
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  return new CoveClient({ store: new ConfigStore(directory), profileName: "test", profile: { server: running.url } });
}

describe("performer resolution", () => {
  test("resolves a case-insensitive exact alias then loads performer detail", async () => {
    const client = await clientWith(request => {
      const url = new URL(request.url);
      if (url.pathname === "/api/performers" && url.searchParams.get("page") === "1") {
        return json(page([performer(8, "Canonical", ["Known Alias"])], 1));
      }
      if (url.pathname === "/api/performers/8") return json({ ...performer(8, "Canonical", ["Known Alias"]), details: "full" });
      return json({}, 404);
    });
    expect(await resolvePerformer(client, "known alias")).toMatchObject({ id: 8, name: "Canonical", details: "full" });
  });

  test("reports every exact match as an ambiguity", async () => {
    const client = await clientWith(() => json(page([performer(1, "Same"), performer(2, "Other", ["same"])], 2)));
    try {
      await resolvePerformer(client, "same");
      throw new Error("expected ambiguity");
    } catch (error) {
      expect(error).toBeInstanceOf(CliError);
      expect((error as CliError).code).toBe("PERFORMER_AMBIGUOUS");
      expect((error as CliError).details).toEqual({ candidates: [
        { id: 1, name: "Same", disambiguation: undefined },
        { id: 2, name: "Other", disambiguation: undefined },
      ] });
    }
  });
});

describe("tag resolution", () => {
  test("resolves a case-insensitive exact alias then loads tag detail", async () => {
    const client = await clientWith(request => {
      const url = new URL(request.url);
      if (url.pathname === "/api/tags" && url.searchParams.get("page") === "1") {
        expect(url.searchParams.get("includeCounts")).toBe("false");
        return json(page([tag(18, "Canonical", ["Known Alias"])], 1));
      }
      if (url.pathname === "/api/tags/18") return json({ ...tag(18, "Canonical", ["Known Alias"]), details: "full" });
      return json({}, 404);
    });
    expect(await resolveTag(client, "known alias")).toMatchObject({ id: 18, name: "Canonical", details: "full" });
  });
});

describe("video retrieval", () => {
  test("retrieves every page through the REST find endpoint and preserves unknown nested fields", async () => {
    const first = Array.from({ length: 250 }, (_, index): Video => ({ id: index + 1, performers: [], files: [], futureField: { retained: true } }));
    first.reverse();
    const second: Video[] = [{ id: 251, performers: [{ id: 4, name: "P", future: true }], files: [] }];
    const client = await clientWith(async request => {
      expect(new URL(request.url).pathname).toBe("/api/videos/find");
      const body = await request.json() as { findFilter: { page: number }; objectFilter: unknown };
      expect(body.findFilter).toMatchObject({ sort: "random", direction: "asc", seed: 0 });
      expect(body.objectFilter).toEqual({ performersCriterion: { value: [], modifier: "includes", requiredIds: [4], excludes: [5] } });
      return json(page(body.findFilter.page === 1 ? first : second, 251, body.findFilter.page));
    });
    const result = await videosForCriteria(client, { tagIds: [], excludedTagIds: [], performerIds: [4], excludedPerformerIds: [5] });
    expect(result.items).toHaveLength(251);
    expect(result.totalCount).toBe(251);
    expect(result.items.map(video => video.id)).toEqual(Array.from({ length: 251 }, (_, index) => index + 1));
    expect(result.items[0]?.futureField).toEqual({ retained: true });
    expect(result.items.at(-1)?.performers[0]).toEqual({ id: 4, name: "P", future: true });
  });

  test("preserves explicit REST sort order and stabilizes an unpaged single sort", async () => {
    const client = await clientWith(async request => {
      const body = await request.json() as { findFilter: unknown };
      expect(body.findFilter).toEqual({
        page: 1, perPage: 40, sort: "title", direction: "asc",
        sorts: [{ key: "title", direction: "asc" }, { key: "updated_at", direction: "asc" }],
      });
      return json(page([
        { id: 9, title: "A", performers: [], files: [] },
        { id: 3, title: "B", performers: [], files: [] },
      ], 2));
    });
    const result = await videosForCriteria(
      client,
      { tagIds: [], excludedTagIds: [], performerIds: [4], excludedPerformerIds: [] },
      { sorts: [{ key: "title", direction: "asc" }] },
    );
    expect(result.items.map(video => video.id)).toEqual([9, 3]);
  });

  test("sends a result limit as one server page", async () => {
    const requests: Array<{ page: number; perPage: number }> = [];
    const client = await clientWith(async request => {
      const body = await request.json() as { findFilter: { page: number; perPage: number } };
      requests.push({ page: body.findFilter.page, perPage: body.findFilter.perPage });
      const start = (body.findFilter.page - 1) * body.findFilter.perPage;
      const items = Array.from({ length: body.findFilter.perPage }, (_, index): Video => ({ id: start + index + 1, performers: [], files: [] }));
      return json(page(items, 600, body.findFilter.page, body.findFilter.perPage));
    });
    const result = await videosForCriteria(
      client,
      { tagIds: [], excludedTagIds: [], performerIds: [], excludedPerformerIds: [] },
      { limit: 300 },
    );
    expect(result.items).toHaveLength(300);
    expect(result.totalCount).toBe(600);
    expect(result.items.at(-1)?.id).toBe(300);
    expect(requests).toEqual([{ page: 1, perPage: 300 }]);
  });

  test("stabilizes paged single sorts and compares the fallback case-insensitively", async () => {
    const client = await clientWith(async request => {
      const body = await request.json() as { findFilter: unknown };
      expect(body.findFilter).toEqual({
        page: 2, perPage: 2, sort: "UPDATED_AT", direction: "desc",
        sorts: [{ key: "UPDATED_AT", direction: "desc" }, { key: "created_at", direction: "desc" }],
      });
      return json(page([{ id: 8, performers: [], files: [] }], 3, 2, 2));
    });
    const result = await videosForCriteria(
      client,
      { tagIds: [], excludedTagIds: [], performerIds: [4], excludedPerformerIds: [] },
      { page: 2, perPage: 2, sorts: [{ key: "UPDATED_AT", direction: "desc" }] },
    );
    expect(result.items.map(video => video.id)).toEqual([8]);
    expect(result.totalCount).toBe(3);
  });

  test("keeps single-only video sorts out of compound sorting", async () => {
    const client = await clientWith(async request => {
      const body = await request.json() as { findFilter: Record<string, unknown> };
      expect(body.findFilter).toEqual({ page: 1, perPage: 40, sort: "phash", direction: "asc" });
      return json(page([{ id: 8, performers: [], files: [] }], 1));
    });
    await videosForCriteria(
      client,
      { tagIds: [], excludedTagIds: [], performerIds: [], excludedPerformerIds: [] },
      { sorts: [{ key: "phash", direction: "asc" }] },
    );
  });
});
