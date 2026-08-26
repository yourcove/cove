import { afterEach, expect, test } from "bun:test";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { audiosForCriteria } from "../src/audios";
import { CoveClient } from "../src/client";
import { ConfigStore } from "../src/config";
import type { Audio } from "../src/types";
import { json, page, startServer } from "./helpers";

const servers: Bun.Server<unknown>[] = [];
const directories: string[] = [];
afterEach(async () => {
  for (const server of servers.splice(0)) server.stop(true);
  await Promise.all(directories.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
});

test("audio retrieval fetches every stable page and returns numeric ID order", async () => {
  const first = Array.from({ length: 250 }, (_, index): Audio => ({ id: index + 1, performers: [], tracks: [], files: [], futureField: { retained: true } })).reverse();
  const second: Audio[] = [{ id: 251, performers: [{ id: 4, name: "Performer", future: true }], tracks: [], files: [] }];
  const running = startServer(async request => {
    expect(new URL(request.url).pathname).toBe("/api/audios/find");
    const body = await request.json() as { findFilter: { page: number; sort: string; direction: string; seed: number }; objectFilter: unknown };
    expect(body.findFilter).toMatchObject({ sort: "random", direction: "asc", seed: 0 });
    expect(body.objectFilter).toEqual({ performersCriterion: { value: [], modifier: "includes", requiredIds: [4], excludes: [5] } });
    return json(page(body.findFilter.page === 1 ? first : second, 251, body.findFilter.page));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const client = new CoveClient({ store: new ConfigStore(directory), profileName: "test", profile: { server: running.url } });

  const result = await audiosForCriteria(client, { tagIds: [], excludedTagIds: [], performerIds: [4], excludedPerformerIds: [5] });

  expect(result.items).toHaveLength(251);
  expect(result.totalCount).toBe(251);
  expect(result.items.map(audio => audio.id)).toEqual(Array.from({ length: 251 }, (_, index) => index + 1));
  expect(result.items[0]?.futureField).toEqual({ retained: true });
  expect(result.items.at(-1)?.performers[0]).toEqual({ id: 4, name: "Performer", future: true });
});

test("audio retrieval preserves explicit REST sort order", async () => {
  const running = startServer(async request => {
    const body = await request.json() as { findFilter: unknown };
    expect(body.findFilter).toEqual({ page: 2, perPage: 2, sort: "title", direction: "desc" });
    return json(page([
      { id: 9, title: "Z", performers: [], tracks: [], files: [] },
      { id: 3, title: "A", performers: [], tracks: [], files: [] },
    ], 8, 2, 2));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const client = new CoveClient({ store: new ConfigStore(directory), profileName: "test", profile: { server: running.url } });
  const result = await audiosForCriteria(
    client,
    { tagIds: [], excludedTagIds: [], performerIds: [4], excludedPerformerIds: [] },
    { page: 2, perPage: 2, sorts: [{ key: "title", direction: "desc" }] },
  );
  expect(result.items.map(audio => audio.id)).toEqual([9, 3]);
  expect(result.totalCount).toBe(8);
});

test("audio retrieval accepts a server-capped single-page limit", async () => {
  const requests: Array<{ page: number; perPage: number }> = [];
  const running = startServer(async request => {
    const body = await request.json() as { findFilter: { page: number; perPage: number } };
    requests.push({ page: body.findFilter.page, perPage: body.findFilter.perPage });
    return json(page(Array.from({ length: 250 }, (_, index): Audio => ({ id: index + 1, performers: [], tracks: [], files: [] })), 600, 1, 250));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const client = new CoveClient({ store: new ConfigStore(directory), profileName: "test", profile: { server: running.url } });

  const result = await audiosForCriteria(
    client,
    { tagIds: [], excludedTagIds: [], performerIds: [], excludedPerformerIds: [] },
    { limit: 500 },
  );

  expect(requests).toEqual([{ page: 1, perPage: 500 }]);
  expect(result.items).toHaveLength(250);
  expect(result.totalCount).toBe(600);
});
