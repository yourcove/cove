import { afterEach, expect, test } from "bun:test";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ConfigStore } from "../src/config";
import { json, page, startServer } from "./helpers";

const servers: Bun.Server<unknown>[] = [];
const directories: string[] = [];
afterEach(async () => {
  for (const server of servers.splice(0)) server.stop(true);
  await Promise.all(directories.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
});

test("global search preserves grouped results and passes the per-type limit", async () => {
  const response = { groups: [{ type: "video", items: [{ id: 4, title: "Example", subtitle: "Studio", future: true }] }, { type: "tag", items: [{ id: 9, title: "Example Tag", subtitle: null }] }], failedTypes: ["text"], future: true };
  const running = startServer(request => {
    const url = new URL(request.url);
    expect(url.pathname).toBe("/api/search/global");
    expect(url.searchParams.get("q")).toBe("Example & more");
    expect(url.searchParams.get("perType")).toBe("12");
    return json(response);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "search", "  Example & more  ", "--per-type", "12", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual(response);
});

test("global search rejects queries shorter than two characters before making a request", async () => {
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "search", " x ", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: "https://unused.example", COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(2);
  expect(stdout).toBe("");
  expect(JSON.parse(stderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT" } });
});

test("similar discovers visual and audio providers and preserves ranked results", async () => {
  const requests: string[] = [];
  const running = startServer(request => {
    const url = new URL(request.url);
    requests.push(`${request.method} ${url.pathname}${url.search}`);
    if (url.pathname === "/api/extensions/manifest") {
      return json({ features: [
        { key: "visual-similarity", options: { apiBasePath: "/api/ext/visual" } },
        { key: "audio-similarity", options: { apiBasePath: "/api/ext/audio" } },
      ] });
    }
    if (url.pathname === "/api/ext/visual/videos/42/similar-images") {
      return json({ items: [{ image: { id: 7, title: "Visual match", performers: [], files: [] }, distance: 0.12 }] });
    }
    if (url.pathname === "/api/ext/visual/images/42/similar-images") {
      return json({ items: [{ image: { id: 9, title: "Image-source match", performers: [], files: [] }, distance: 0.08 }] });
    }
    if (url.pathname === "/api/ext/audio/videos/42/similar-videos") {
      return json({ items: [{ video: { id: 8, title: "Audio match", performers: [], files: [] }, distance: 0.2, sectionIndex: 1, startSec: 65, endSec: 80 }] });
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const environment = { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };

  const visual = Bun.spawn([process.execPath, "src/index.ts", "similar", "video", "42", "--type", "images", "--limit", "7", "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
  const [visualStdout, visualStderr, visualExit] = await Promise.all([new Response(visual.stdout).text(), new Response(visual.stderr).text(), visual.exited]);
  expect(visualExit).toBe(0);
  expect(visualStderr).toBe("");
  expect(JSON.parse(visualStdout)).toEqual({ items: [{ image: { id: 7, title: "Visual match", performers: [], files: [] }, distance: 0.12 }] });

  const audio = Bun.spawn([process.execPath, "src/index.ts", "similar", "video", "42", "--by", "audio", "--limit", "3", "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
  const [audioStdout, audioStderr, audioExit] = await Promise.all([new Response(audio.stdout).text(), new Response(audio.stderr).text(), audio.exited]);
  expect(audioExit).toBe(0);
  expect(audioStderr).toBe("");
  expect(JSON.parse(audioStdout)).toEqual({ items: [{ video: { id: 8, title: "Audio match", performers: [], files: [] }, distance: 0.2, sectionIndex: 1, startSec: 65, endSec: 80 }] });

  const imageSource = Bun.spawn([process.execPath, "src/index.ts", "similar", "image", "42", "--output", "jsonl"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
  const [imageStdout, imageStderr, imageExit] = await Promise.all([new Response(imageSource.stdout).text(), new Response(imageSource.stderr).text(), imageSource.exited]);
  expect(imageExit).toBe(0);
  expect(imageStderr).toBe("");
  expect(imageStdout.trim().split("\n").map(line => JSON.parse(line))).toEqual([{ image: { id: 9, title: "Image-source match", performers: [], files: [] }, distance: 0.08 }]);
  expect(requests).toEqual([
    "GET /api/extensions/manifest",
    "GET /api/ext/visual/videos/42/similar-images?perPage=7",
    "GET /api/extensions/manifest",
    "GET /api/ext/audio/videos/42/similar-videos?perPage=3",
    "GET /api/extensions/manifest",
    "GET /api/ext/visual/images/42/similar-images?perPage=20",
  ]);
});

test("similar rejects unsupported pairings and unavailable providers", async () => {
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const invalid = Bun.spawn([process.execPath, "src/index.ts", "similar", "image", "7", "--by", "audio", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: "https://unused.example", COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [invalidStdout, invalidStderr, invalidExit] = await Promise.all([new Response(invalid.stdout).text(), new Response(invalid.stderr).text(), invalid.exited]);
  expect(invalidExit).toBe(2);
  expect(invalidStdout).toBe("");
  expect(JSON.parse(invalidStderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT", message: "Audio similarity supports video sources and video results only." } });

  const running = startServer(request => {
    expect(new URL(request.url).pathname).toBe("/api/extensions/manifest");
    return json({ features: [] });
  });
  servers.push(running.server);
  const unavailable = Bun.spawn([process.execPath, "src/index.ts", "similar", "video", "7", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [unavailableStdout, unavailableStderr, unavailableExit] = await Promise.all([new Response(unavailable.stdout).text(), new Response(unavailable.stderr).text(), unavailable.exited]);
  expect(unavailableExit).toBe(1);
  expect(unavailableStdout).toBe("");
  expect(JSON.parse(unavailableStderr)).toMatchObject({ error: { code: "FEATURE_UNAVAILABLE", message: "No visual similarity provider is available." } });

  const invalidPaths = ["https://outside.example/api/ext/visual", "/api/ext/%2e%2e/visual", "/api/ext/visual?mode=unsafe"];
  let invalidPathRequests = 0;
  const unsafe = startServer(request => {
    expect(new URL(request.url).pathname).toBe("/api/extensions/manifest");
    return json({ features: [{ key: "visual-similarity", options: { apiBasePath: invalidPaths[invalidPathRequests++] } }] });
  });
  servers.push(unsafe.server);
  for (const _path of invalidPaths) {
    const attempt = Bun.spawn([process.execPath, "src/index.ts", "similar", "video", "7", "--json"], {
      cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: unsafe.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
    });
    const [attemptStdout, attemptStderr, attemptExit] = await Promise.all([new Response(attempt.stdout).text(), new Response(attempt.stderr).text(), attempt.exited]);
    expect(attemptExit).toBe(1);
    expect(attemptStdout).toBe("");
    expect(JSON.parse(attemptStderr)).toMatchObject({ error: { code: "INVALID_RESPONSE", message: "The visual similarity provider advertised an invalid API path." } });
  }
  expect(invalidPathRequests).toBe(invalidPaths.length);
});

test("videos list combines repeated positive and negative tag and performer filters", async () => {
  const running = startServer(async request => {
    expect(request.headers.get("Authorization")).toBe("Bearer test-token");
    const url = new URL(request.url);
    if (url.pathname === "/api/performers/7") return json({ id: 7, name: "Selected", aliases: [], privateDetail: "not top-level" });
    if (url.pathname === "/api/performers/8") return json({ id: 8, name: "Required", aliases: [] });
    if (url.pathname === "/api/performers/9") return json({ id: 9, name: "Excluded", aliases: [] });
    if (url.pathname === "/api/tags/10") return json({ id: 10, name: "First", aliases: [] });
    if (url.pathname === "/api/tags/11") return json({ id: 11, name: "Second", aliases: [] });
    if (url.pathname === "/api/tags/12") return json({ id: 12, name: "Rejected", aliases: [] });
    if (url.pathname === "/api/videos/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body).toEqual({
        findFilter: { page: 2, perPage: 50, sort: "title", direction: "asc", sorts: [{ key: "title", direction: "asc" }, { key: "date", direction: "desc" }] },
        objectFilter: {
          tagsCriterion: { value: [], modifier: "includes", requiredIds: [10, 11], excludes: [12] },
          performersCriterion: { value: [], modifier: "includes", requiredIds: [7, 8], excludes: [9] },
        },
      });
      return json(page([{ id: 3, title: "Video", performers: [{ id: 7, name: "Selected", extra: "kept" }], files: [], unknown: 42 }], 101, 2, 50));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([
    process.execPath,
    "src/index.ts",
    "videos",
    "list",
    "--performer",
    "7",
    "--performer",
    "8",
    "--exclude-performer",
    "9",
    "--tag",
    "10",
    "--tag",
    "11",
    "--exclude-tag",
    "12",
    "--page", "2",
    "--per-page", "50",
    "--sort-by", "title:asc",
    "--sort-by", "date:desc",
    "--json",
  ], {
    cwd: join(import.meta.dir, ".."),
    env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory },
    stdout: "pipe",
    stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([
    new Response(processResult.stdout).text(),
    new Response(processResult.stderr).text(),
    processResult.exited,
  ]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ videos: [{ id: 3, title: "Video", performers: [{ id: 7, name: "Selected", extra: "kept" }], files: [], unknown: 42 }], totalCount: 101 });
  expect(JSON.parse(stdout)).not.toHaveProperty("performer");
});

test("videos list accepts an advanced filter without relation flags", async () => {
  const running = startServer(async request => {
    expect(new URL(request.url).pathname).toBe("/api/videos/find");
    expect(await request.json()).toMatchObject({ objectFilter: { pathCriterion: { value: "/library", modifier: "underPath" } } });
    return json(page([], 0));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--filter", '{"pathCriterion":{"value":"/library","modifier":"underPath"}}', "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ videos: [], totalCount: 0 });
});

test("videos list translates repeatable filter-by expressions before requesting results", async () => {
  const running = startServer(async request => {
    expect(new URL(request.url).pathname).toBe("/api/videos/find");
    const body = await request.json() as { objectFilter: unknown };
    expect(body.objectFilter).toEqual({
      pathCriterion: { value: "/library", modifier: "underPath" },
      titleCriterion: { value: "[cyoa]", modifier: "excludes" },
      directorCriterion: { value: "example", modifier: "includes" },
    });
    return json(page([], 0));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([
    process.execPath,
    "src/index.ts",
    "videos",
    "list",
    "--filter", '{"pathCriterion":{"value":"/library","modifier":"underPath"},"TitleCriterion":{"value":"old","modifier":"equals"}}',
    "--filter-by", "title:excludes=[cyoa]",
    "--filter-by", "director:includes=example",
    "--json",
  ], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ videos: [], totalCount: 0 });
});

test("videos list accepts filter-by as its only filter", async () => {
  const running = startServer(async request => {
    expect(new URL(request.url).pathname).toBe("/api/videos/find");
    expect(await request.json()).toMatchObject({ objectFilter: { titleCriterion: { value: "[cyoa]", modifier: "excludes" } } });
    return json(page([], 0));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--filter-by", "title:excludes=[cyoa]", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ videos: [], totalCount: 0 });
});

test("media download streams an authenticated binary response to a protected file", async () => {
  const running = startServer(request => {
    expect(new URL(request.url).pathname).toBe("/api/stream/image/7");
    expect(request.headers.get("Authorization")).toBe("Bearer test-token");
    return new Response(new Uint8Array([1, 2, 3, 255]), { headers: { "Content-Length": "4" } });
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const destination = join(directory, "image.bin");
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "media", "download", "image", "7", "--file", destination], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("warning: credentials will be sent over plain HTTP.\n");
  expect(stdout).toBe(`Downloaded 4 bytes to ${destination}\n`);
  expect([...await readFile(destination)]).toEqual([1, 2, 3, 255]);

  const repeated = Bun.spawn([process.execPath, "src/index.ts", "media", "download", "image", "7", "--file", destination], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [repeatedStdout, repeatedStderr, repeatedExit] = await Promise.all([new Response(repeated.stdout).text(), new Response(repeated.stderr).text(), repeated.exited]);
  expect(repeatedExit).toBe(1);
  expect(repeatedStdout).toBe("");
  expect(repeatedStderr).toContain("already exists");
  expect([...await readFile(destination)]).toEqual([1, 2, 3, 255]);
});

test("library video bulk deletion uses destroy and ratings enforce the API scale", async () => {
  let requests = 0;
  const running = startServer(async request => {
    requests += 1;
    expect(request.method).toBe("POST");
    expect(new URL(request.url).pathname).toBe("/api/videos/destroy");
    expect(await request.json()).toEqual({ ids: [4, 5] });
    return json({ deletedIds: [4, 5] });
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const environment = { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };
  const deletion = Bun.spawn([process.execPath, "src/index.ts", "library", "bulk-delete", "videos", "--ids", "4,5", "--yes", "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
  const [deletionStdout, deletionStderr, deletionExit] = await Promise.all([new Response(deletion.stdout).text(), new Response(deletion.stderr).text(), deletion.exited]);
  expect(deletionExit).toBe(0);
  expect(deletionStderr).toBe("");
  expect(JSON.parse(deletionStdout)).toEqual({ deletedIds: [4, 5] });

  const rating = Bun.spawn([process.execPath, "src/index.ts", "library", "rate", "videos", "4", "--value", "4.5", "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
  const [ratingStdout, ratingStderr, ratingExit] = await Promise.all([new Response(rating.stdout).text(), new Response(rating.stderr).text(), rating.exited]);
  expect(ratingExit).toBe(2);
  expect(ratingStdout).toBe("");
  expect(JSON.parse(ratingStderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT", message: "--value must be an integer from 0 to 100." } });
  expect(requests).toBe(1);
});

test("audios list combines repeated positive and negative tag and performer filters", async () => {
  const running = startServer(async request => {
    expect(request.headers.get("Authorization")).toBe("Bearer test-token");
    const url = new URL(request.url);
    if (url.pathname === "/api/performers/7") return json({ id: 7, name: "Required", aliases: [] });
    if (url.pathname === "/api/performers/9") return json({ id: 9, name: "Excluded", aliases: [] });
    if (url.pathname === "/api/tags/10") return json({ id: 10, name: "Required", aliases: [] });
    if (url.pathname === "/api/tags/12") return json({ id: 12, name: "Excluded", aliases: [] });
    if (url.pathname === "/api/audios/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body).toEqual({
        findFilter: { page: 3, perPage: 20, sort: "date", direction: "desc" },
        objectFilter: {
          tagsCriterion: { value: [], modifier: "includes", requiredIds: [10], excludes: [12] },
          performersCriterion: { value: [], modifier: "includes", requiredIds: [7], excludes: [9] },
        },
      });
      return json(page([{ id: 4, title: "Audio", performers: [{ id: 7, name: "Required" }], files: [], tracks: [], unknown: 42 }], 61, 3, 20));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([
    process.execPath, "src/index.ts", "audios", "list",
    "--performer", "7", "--exclude-performer", "9",
    "--tag", "10", "--exclude-tag", "12", "--json",
    "--page", "3", "--per-page", "20", "--sort-by", "date:desc",
  ], {
    cwd: join(import.meta.dir, ".."),
    env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory },
    stdout: "pipe",
    stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([
    new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited,
  ]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ audios: [{ id: 4, title: "Audio", performers: [{ id: 7, name: "Required" }], files: [], tracks: [], unknown: 42 }], totalCount: 61 });
});

test("images list uses image REST criteria, paging, and compound sorts", async () => {
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/tags/10") return json({ id: 10, name: "Required", aliases: [] });
    if (url.pathname === "/api/performers/9") return json({ id: 9, name: "Excluded", aliases: [] });
    if (url.pathname === "/api/images/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body).toEqual({
        findFilter: { page: 2, perPage: 50, sort: "date", direction: "desc", sorts: [{ key: "date", direction: "desc" }, { key: "title", direction: "asc" }] },
        objectFilter: {
          tagsCriterion: { value: [], modifier: "includes", requiredIds: [10], excludes: [] },
          performersCriterion: { value: [], modifier: "includes", requiredIds: [], excludes: [9] },
        },
      });
      return json(page([{ id: 4, title: "Image", performers: [], files: [], unknown: 42 }], 61, 2, 50));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "images", "list", "--tag", "10", "--exclude-performer", "9", "--page", "2", "--per-page", "50", "--sort-by", "date:desc", "--sort-by", "title:asc", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ images: [{ id: 4, title: "Image", performers: [], files: [], unknown: 42 }], totalCount: 61 });
});

test("galleries list preserves full gallery REST records", async () => {
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/galleries/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body).toEqual({ findFilter: { page: 1, perPage: 25, sort: "title", direction: "asc", sorts: [{ key: "title", direction: "asc" }, { key: "updated_at", direction: "asc" }] }, objectFilter: {} });
      return json(page([{ id: 8, title: "Gallery", performers: [], files: [], imageCount: 3, unknown: 42 }], 12, 1, 25));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "galleries", "list", "--per-page", "25", "--sort-by", "title:asc", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ galleries: [{ id: 8, title: "Gallery", performers: [], files: [], imageCount: 3, unknown: 42 }], totalCount: 12 });
});

test("tags list searches and uses tag-specific sorts", async () => {
  const running = startServer(async request => {
    if (new URL(request.url).pathname !== "/api/tags/find") return json({}, 404);
    const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
    expect(body).toEqual({ findFilter: { page: 1, perPage: 10, q: "Example", sort: "name", direction: "asc", sorts: [{ key: "name", direction: "asc" }, { key: "updated_at", direction: "asc" }] }, objectFilter: {} });
    return json(page([{ id: 5, name: "Example", aliases: [], videoCount: 7, unknown: 42 }], 1, 1, 10));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "tags", "list", "--query", "Example", "--per-page", "10", "--sort-by", "name:asc", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ tags: [{ id: 5, name: "Example", aliases: [], videoCount: 7, unknown: 42 }], totalCount: 1 });
});

test("performers list applies positive and negative tag criteria", async () => {
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/tags/10") return json({ id: 10, name: "Required", aliases: [] });
    if (url.pathname === "/api/tags/12") return json({ id: 12, name: "Excluded", aliases: [] });
    if (url.pathname === "/api/performers/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body).toEqual({ findFilter: { page: 1, perPage: 25, q: "Example", sort: "name", direction: "asc", sorts: [{ key: "name", direction: "asc" }, { key: "updated_at", direction: "asc" }] }, objectFilter: { tagsCriterion: { value: [], modifier: "includes", requiredIds: [10], excludes: [12] } } });
      return json(page([{ id: 7, name: "Example", aliases: [], videoCount: 4, unknown: 42 }], 1, 1, 25));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "performers", "list", "--query", "Example", "--tag", "10", "--exclude-tag", "12", "--per-page", "25", "--sort-by", "name:asc", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ performers: [{ id: 7, name: "Example", aliases: [], videoCount: 4, unknown: 42 }], totalCount: 1 });
});

test("performers show returns the complete REST performer object", async () => {
  const performer = { id: 7, name: "Example Performer", disambiguation: "Example", aliases: ["Alias"], tags: [{ id: 2, name: "Featured" }], urls: ["https://example.test/profile"], videoCount: 4, imageCount: 3, customFields: { future: true }, unknown: 42 };
  const running = startServer(request => new URL(request.url).pathname === "/api/performers/7" ? json(performer) : json({}, 404));
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "performers", "show", "7", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual(performer);
});

test("human performer details use configured metadata service names without exposing config secrets", async () => {
  const running = startServer(request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/performers/7") return json({
      id: 7,
      name: "Example Performer",
      aliases: [],
      remoteIds: [{ endpoint: "https://metadata.example/graphql", remoteId: "remote-123" }],
    });
    if (path === "/api/system/config") return json({
      scraping: { metadataServers: [{ endpoint: "https://metadata.example/graphql/", name: "Friendly Catalog", apiKey: "never-render-this" }] },
    });
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "performers", "show", "7"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).not.toContain("never-render-this");
  expect(stdout).toContain("Friendly Catalog · remote-123");
  expect(stdout).not.toContain('"endpoint"');
  expect(stdout).not.toContain("never-render-this");
});

test("human performer details tolerate malformed optional remote IDs without metadata discovery", async () => {
  let configRequests = 0;
  const running = startServer(request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/performers/7") return json({ id: 7, name: "Example Performer", aliases: [], remoteIds: "invalid" });
    if (path === "/api/system/config") {
      configRequests += 1;
      return json({ scraping: { metadataServers: [] } });
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "performers", "show", "7"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(configRequests).toBe(0);
  expect(stdout).toContain("Remote IDs  —");
  expect(stderr).not.toContain("TypeError");
});

test("videos and audios show return complete REST detail objects", async () => {
  const video = { id: 4, title: "Video", tags: [{ id: 2, name: "Featured" }], performers: [], files: [{ basename: "video.mp4", duration: 65 }], customFields: { future: true }, unknown: 42 };
  const audio = { id: 5, title: "Audio", tags: [{ id: 3, name: "Favorite" }], performers: [], tracks: [], files: [{ basename: "audio.flac", duration: 90 }], customFields: { future: true }, unknown: 43 };
  const running = startServer(request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/videos/4") return json(video);
    if (path === "/api/audios/5") return json(audio);
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const environment = { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };
  for (const testCase of [{ command: ["videos", "show", "4"], expected: video }, { command: ["audios", "show", "5"], expected: audio }]) {
    const processResult = Bun.spawn([process.execPath, "src/index.ts", ...testCase.command, "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
    expect(exitCode).toBe(0);
    expect(stderr).toBe("");
    expect(JSON.parse(stdout)).toEqual(testCase.expected);
  }
});

test("human video details use configured metadata service names without exposing config secrets", async () => {
  const running = startServer(request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/videos/4") return json({
      id: 4,
      title: "Example video",
      performers: [],
      files: [],
      remoteIds: [{ endpoint: "https://metadata.example/graphql", remoteId: "remote-123" }],
    });
    if (path === "/api/system/config") return json({
      scraping: { metadataServers: [{ endpoint: "https://metadata.example/graphql/", name: "Friendly Catalog", apiKey: "never-render-this" }] },
    });
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "show", "4"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toContain("credentials will be sent over plain HTTP");
  expect(stderr).not.toContain("never-render-this");
  expect(stdout).toContain("Friendly Catalog · remote-123");
  expect(stdout).not.toContain('"endpoint"');
  expect(stdout).not.toContain("never-render-this");
  expect(stdout).not.toContain("\u001b]8;;");
});

test("optional metadata service naming cannot stall human video details", async () => {
  const running = startServer(async request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/videos/4") return json({
      id: 4,
      title: "Example video",
      performers: [],
      files: [],
      remoteIds: [{ endpoint: "https://www.metadata.example/graphql", remoteId: "remote-123" }],
    });
    if (path === "/api/system/config") {
      await Bun.sleep(3_000);
      return json({ scraping: { metadataServers: [] } });
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const started = performance.now();
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "show", "4"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, exitCode] = await Promise.all([new Response(processResult.stdout).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(performance.now() - started).toBeLessThan(1_800);
  expect(stdout).toContain("metadata.example · remote-123");
});

test("show commands reject IDs outside Cove's integer range before resolving a server", async () => {
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "show", "2147483648", "--json"], { cwd: join(import.meta.dir, ".."), stdout: "pipe", stderr: "pipe" });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(2);
  expect(stdout).toBe("");
  expect(JSON.parse(stderr).error.message).toContain("ID must be between 1 and 2147483647");
});

test("show commands reject malformed nested REST detail records", async () => {
  const running = startServer(() => json({ id: 4, performers: [], files: [null] }));
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "show", "4", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(1);
  expect(stdout).toBe("");
  expect(JSON.parse(stderr).error.code).toBe("INVALID_RESPONSE");
});

test("studios list applies tag criteria and studio-specific sorts", async () => {
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/tags/10") return json({ id: 10, name: "Required", aliases: [] });
    if (url.pathname === "/api/studios/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body).toEqual({ findFilter: { page: 1, perPage: 25, sort: "video_count", direction: "desc", sorts: [{ key: "video_count", direction: "desc" }, { key: "updated_at", direction: "desc" }] }, objectFilter: { tagsCriterion: { value: [], modifier: "includes", requiredIds: [10], excludes: [] } } });
      return json(page([{ id: 3, name: "Studio", aliases: [], videoCount: 9, unknown: 42 }], 1, 1, 25));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "studios", "list", "--tag", "10", "--per-page", "25", "--sort-by", "video_count:desc", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ studios: [{ id: 3, name: "Studio", aliases: [], videoCount: 9, unknown: 42 }], totalCount: 1 });
});

test("read-only detail commands preserve complete entity responses", async () => {
  const records: Record<string, unknown> = {
    "/api/images/1": { id: 1, title: "Image", performers: [], tags: [], galleries: [], groups: [], files: [], unknown: 42 },
    "/api/galleries/2": { id: 2, title: "Gallery", performers: [], tags: [], files: [], unknown: 42 },
    "/api/tags/7": { id: 7, name: "Tag", aliases: [], parents: [], children: [], unknown: 42 },
    "/api/studios/3": { id: 3, name: "Studio", aliases: [], tags: [], unknown: 42 },
    "/api/groups/4": { id: 4, name: "Group", tags: [], unknown: 42 },
    "/api/texts/5": { id: 5, title: "Text", performers: [], tags: [], groups: [], files: [], unknown: 42 },
    "/api/segments/6": { id: 6, hostType: "video", hostId: 7, startSec: 1, sourceKey: "user", unknown: 42 },
  };
  const running = startServer(request => json(records[new URL(request.url).pathname] ?? {}, records[new URL(request.url).pathname] ? 200 : 404));
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  for (const [resource, id] of [["images", 1], ["galleries", 2], ["tags", 7], ["studios", 3], ["groups", 4], ["texts", 5], ["segments", 6]] as const) {
    const processResult = Bun.spawn([process.execPath, "src/index.ts", resource, "show", String(id), "--json"], {
      cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
    });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
    expect(exitCode).toBe(0);
    expect(stderr).toBe("");
    expect(JSON.parse(stdout)).toEqual(records[`/api/${resource}/${id}`]);
  }
});

test("images show includes the configured server path in its human stream URL", async () => {
  const running = startServer(request => new URL(request.url).pathname === "/base/api/images/1" ? json({ id: 1, title: "Image", performers: [], files: [] }) : json({}, 404));
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "images", "show", "1", "--server", `${running.url}/base`, "--no-color"], { cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe" });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toContain("credentials will be sent over plain HTTP");
  expect(stdout).toContain(`${running.url}/base/api/stream/image/1`);
});

test("read-only detail commands reject malformed nested records", async () => {
  const running = startServer(request => {
    const path = new URL(request.url).pathname;
    if (path === "/api/images/1") return json({ id: 1, performers: [], files: [], tags: [null] });
    if (path === "/api/studios/2") return json({ id: 2, name: "Studio", aliases: [null], tags: [null] });
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  for (const [resource, id] of [["images", "1"], ["studios", "2"]]) {
    const processResult = Bun.spawn([process.execPath, "src/index.ts", resource!, "show", id!, "--json"], { cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe" });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
    expect(exitCode).toBe(1);
    expect(stdout).toBe("");
    expect(JSON.parse(stderr)).toMatchObject({ error: { code: "INVALID_RESPONSE" } });
  }
});

test("segments list uses the server page-size default and singular sort help", async () => {
  const running = startServer(request => {
    const url = new URL(request.url);
    expect(url.searchParams.get("perPage")).toBe("48");
    return json(page([], 0, 2, 48));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const listResult = Bun.spawn([process.execPath, "src/index.ts", "segments", "list", "--page", "2", "--json"], { cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe" });
  expect(await listResult.exited).toBe(0);
  const helpResult = Bun.spawn([process.execPath, "src/index.ts", "segments", "list", "--help"], { cwd: join(import.meta.dir, ".."), stdout: "pipe", stderr: "pipe" });
  const help = await new Response(helpResult.stdout).text();
  expect(await helpResult.exited).toBe(0);
  expect(help).toContain("default: 48");
  expect(help).toContain("Sort:");
  expect(help).not.toContain("(repeatable)");
  expect(help).toContain("cove-cli help segments list");
  expect(help).not.toContain("updated_at, created_at");
});

test("groups and texts list use stable paginated REST queries", async () => {
  const running = startServer(async request => {
    const url = new URL(request.url);
    const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
    if (url.pathname === "/api/groups/find") {
      expect(body).toEqual({ findFilter: { page: 2, perPage: 10, q: "Example", sort: "random", direction: "asc", seed: 0 }, objectFilter: {} });
      return json(page([{ id: 4, name: "Group", tags: [], itemCount: 2 }], 11, 2, 10));
    }
    if (url.pathname === "/api/texts/find") {
      expect(body).toEqual({ findFilter: { page: 1, perPage: 5, sort: "words", direction: "desc", sorts: [{ key: "words", direction: "desc" }, { key: "updated_at", direction: "desc" }] }, objectFilter: {} });
      return json(page([{ id: 5, title: "Text", performers: [], tags: [], groups: [], files: [], maxWordCount: 20 }], 1, 1, 5));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  for (const args of [["groups", "list", "--query", "Example", "--page", "2", "--per-page", "10", "--json"], ["texts", "list", "--per-page", "5", "--sort-by", "words:desc", "--json"]]) {
    const processResult = Bun.spawn([process.execPath, "src/index.ts", ...args], { cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe" });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
    expect(exitCode).toBe(0);
    expect(stderr).toBe("");
    expect(JSON.parse(stdout).totalCount).toBeGreaterThan(0);
  }
});

test("groups list keeps random sorting singular", async () => {
  const running = startServer(async request => {
    expect(new URL(request.url).pathname).toBe("/api/groups/find");
    const body = await request.json() as { findFilter: Record<string, unknown> };
    const seed = body.findFilter.seed;
    expect(typeof seed).toBe("number");
    expect(Number(seed)).toBeGreaterThan(0);
    expect(body.findFilter).toEqual({ page: 1, perPage: 25, sort: "random", direction: "asc", seed });
    return json(page([{ id: 4, name: "Group", tags: [], itemCount: 2 }], 1));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "groups", "list", "--sort-by", "random", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout).totalCount).toBe(1);
});

test("group items list preserves membership identities in machine output", async () => {
  const items = [
    { id: 103, groupId: 42, orderIndex: 0, kind: "video", videoId: 7, videoTitle: "First", hostType: "video", hostId: 7, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" },
    { id: 107, groupId: 42, orderIndex: 1, kind: "image", imageId: 8, imageTitle: "Second", hostType: "image", hostId: 8, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" },
  ];
  const running = startServer(request => {
    expect(new URL(request.url).pathname).toBe("/api/groups/42/items");
    return json(items);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "list", "42", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ groupId: 42, items });
});

test("group items move sends one ordered block and renders the refreshed order", async () => {
  const initial = [
    { id: 101, groupId: 42, orderIndex: 0, kind: "video", videoTitle: "One", hostType: "video", hostId: 1, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" },
    { id: 103, groupId: 42, orderIndex: 1, kind: "video", videoTitle: "Three", hostType: "video", hostId: 3, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" },
    { id: 107, groupId: 42, orderIndex: 2, kind: "video", videoTitle: "Seven", hostType: "video", hostId: 7, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" },
    { id: 120, groupId: 42, orderIndex: 3, kind: "video", videoTitle: "Anchor", hostType: "video", hostId: 20, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" },
  ];
  const refreshed = [initial[1], initial[2], initial[0], initial[3]].map((item, orderIndex) => ({ ...item, orderIndex }));
  let getCount = 0;
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/groups/42") return json({ id: 42, name: "Static", kind: "static", tags: [] });
    if (request.method === "GET") {
      expect(url.pathname).toBe("/api/groups/42/items");
      return json(getCount++ === 0 ? initial : refreshed);
    }
    expect(request.method).toBe("PUT");
    expect(url.pathname).toBe("/api/groups/42/items/reorder");
    expect(await request.json()).toEqual({ ids: [103, 107], startIndex: 0 });
    return json(null);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", "103", "107", "--first", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(getCount).toBe(2);
  expect(JSON.parse(stdout)).toEqual({ groupId: 42, items: refreshed });
});

test("group items move calculates first, last, and 1-based absolute destinations", async () => {
  const item = (id: number, orderIndex: number) => ({ id, groupId: 42, orderIndex, kind: "video", videoTitle: `Video ${id}`, hostType: "video", hostId: id, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" });
  const initial = [item(1, 0), item(2, 1), item(3, 2), item(4, 3)];
  const cases = [
    { args: ["3", "--first"], body: { ids: [3], startIndex: 0 }, final: [3, 1, 2, 4] },
    { args: ["2", "--last"], body: { ids: [2], startIndex: 2_147_483_647 }, final: [1, 3, 4, 2] },
    { args: ["4", "--to-position", "2"], body: { ids: [4], startIndex: 1 }, final: [1, 4, 2, 3] },
    { args: ["4", "--to-position", "99"], body: { ids: [4], startIndex: 98 }, final: [1, 2, 3, 4] },
  ];
  for (const testCase of cases) {
    let getCount = 0;
    const refreshed = testCase.final.map((id, orderIndex) => item(id, orderIndex));
    const running = startServer(async request => {
      const url = new URL(request.url);
      if (url.pathname === "/api/groups/42") return json({ id: 42, name: "Static", kind: "static", tags: [] });
      if (request.method === "GET") return json(getCount++ === 0 ? initial : refreshed);
      expect(url.pathname).toBe("/api/groups/42/items/reorder");
      expect(await request.json()).toEqual(testCase.body);
      return json(null);
    });
    servers.push(running.server);
    const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
    directories.push(directory);
    const processResult = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", ...testCase.args, "--json"], {
      cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
    });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
    expect(exitCode).toBe(0);
    expect(stderr).toBe("");
    expect(JSON.parse(stdout).items.map((entry: { id: number }) => entry.id)).toEqual(testCase.final);
  }
});

test("group items move rejects ambiguous destinations before contacting Cove", async () => {
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  for (const args of [["--first", "--last"], []]) {
    const processResult = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", "103", ...args, "--json"], {
      cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: "https://unused.example", COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
    });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
    expect(exitCode).toBe(2);
    expect(stdout).toBe("");
    expect(JSON.parse(stderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT", message: "Choose exactly one destination: --first, --last, or --to-position." } });
  }

  const unsupported = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", "103", "--before", "120", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: "https://unused.example", COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [unsupportedStdout, unsupportedStderr, unsupportedExitCode] = await Promise.all([new Response(unsupported.stdout).text(), new Response(unsupported.stderr).text(), unsupported.exited]);
  expect(unsupportedExitCode).toBe(2);
  expect(unsupportedStdout).toBe("");
  expect(JSON.parse(unsupportedStderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT" } });

  const outOfRange = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", "103", "--to-position", "2147483648", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: "https://unused.example", COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [rangeStdout, rangeStderr, rangeExitCode] = await Promise.all([new Response(outOfRange.stdout).text(), new Response(outOfRange.stderr).text(), outOfRange.exited]);
  expect(rangeExitCode).toBe(2);
  expect(rangeStdout).toBe("");
  expect(JSON.parse(rangeStderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT", message: "--to-position must be between 1 and 2147483647." } });

  for (const option of ["--first", "--last"]) {
    const repeated = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", "103", option, "107", option, "--json"], {
      cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: "https://unused.example", COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
    });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(repeated.stdout).text(), new Response(repeated.stderr).text(), repeated.exited]);
    expect(exitCode).toBe(2);
    expect(stdout).toBe("");
    expect(JSON.parse(stderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT", message: `${option} may only be specified once.` } });
  }
});

test("group items move submits visible no-ops and validates membership IDs", async () => {
  const items = [1, 2, 3].map((id, orderIndex) => ({ id, groupId: 42, orderIndex, kind: "video", videoTitle: `Video ${id}`, hostType: "video", hostId: id, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" }));
  let requestCount = 0;
  let itemGetCount = 0;
  const running = startServer(async request => {
    requestCount += 1;
    const url = new URL(request.url);
    if (url.pathname === "/api/groups/42") return json({ id: 42, name: "Static", kind: "static", tags: [] });
    if (url.pathname === "/api/groups/42/items/reorder") {
      expect(request.method).toBe("PUT");
      expect(await request.json()).toEqual({ ids: [1], startIndex: 0 });
      return json(null);
    }
    expect(url.pathname).toBe("/api/groups/42/items");
    itemGetCount += 1;
    return json(items);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const environment = { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };

  const noOp = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", "1", "--first", "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
  const [noOpStdout, noOpStderr, noOpExit] = await Promise.all([new Response(noOp.stdout).text(), new Response(noOp.stderr).text(), noOp.exited]);
  expect(noOpExit).toBe(0);
  expect(noOpStderr).toBe("");
  expect(JSON.parse(noOpStdout)).toEqual({ groupId: 42, items });
  expect(requestCount).toBe(4);
  expect(itemGetCount).toBe(2);

  const invalidCases = [
    { args: ["9", "--last"], message: "Group item ID 9 is not in this group." },
    { args: ["1", "1", "--last"], message: "Each group item ID may only be moved once." },
  ];
  for (const testCase of invalidCases) {
    const attempt = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", ...testCase.args, "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(attempt.stdout).text(), new Response(attempt.stderr).text(), attempt.exited]);
    expect(exitCode).toBe(2);
    expect(stdout).toBe("");
    expect(JSON.parse(stderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT", message: testCase.message } });
  }
  expect(requestCount).toBe(7);
});

test("group items move uses the existing reorder contract without capability negotiation", async () => {
  let putCount = 0;
  const item = { id: 1, groupId: 42, orderIndex: 0, kind: "video", hostType: "video", hostId: 1, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" };
  const running = startServer(request => {
    const url = new URL(request.url);
    if (request.method === "PUT") putCount += 1;
    if (url.pathname === "/api/groups/42") return json({ id: 42, name: "Static", kind: "static", tags: [] });
    if (url.pathname === "/api/groups/42/items/reorder") return json(null);
    return json([item]);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", "1", "--last", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(putCount).toBe(1);
  expect(JSON.parse(stdout)).toEqual({ groupId: 42, items: [item] });
});

test("group items move clearly rejects an empty dynamic group", async () => {
  let putCount = 0;
  const running = startServer(request => {
    const url = new URL(request.url);
    if (request.method === "PUT") putCount += 1;
    if (url.pathname === "/api/groups/42") return json({ id: 42, name: "Dynamic", kind: "dynamic", tags: [] });
    return json([]);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "groups", "items", "move", "42", "1", "--last", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(1);
  expect(stdout).toBe("");
  expect(putCount).toBe(0);
  expect(JSON.parse(stderr)).toMatchObject({ error: { code: "FEATURE_UNAVAILABLE", message: "Dynamic groups cannot be reordered." } });
});

test("segments list sends filters and preserves the JSON envelope", async () => {
  const running = startServer(request => {
    const url = new URL(request.url);
    expect(url.pathname).toBe("/api/segments");
    expect(Object.fromEntries(url.searchParams)).toEqual({ page: "3", perPage: "20", sort: "confidence", direction: "asc", q: "Example", videoId: "7", tagId: "8", kind: "face", sourceKey: "extension" });
    return json(page([{ id: 6, hostType: "video", hostId: 7, startSec: 1, sourceKey: "extension", unknown: 42 }], 41, 3, 20));
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "segments", "list", "--query", "Example", "--video", "7", "--tag", "8", "--kind", "face", "--source-key", "extension", "--page", "3", "--per-page", "20", "--sort-by", "confidence", "--json"], { cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe" });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ segments: [{ id: 6, hostType: "video", hostId: 7, startSec: 1, sourceKey: "extension", unknown: 42 }], totalCount: 41 });
});

test("list pagination and sorting options reject invalid values", async () => {
  const cases = [
    { args: ["--page", "0"], message: "--page must be a positive integer" },
    { args: ["--per-page", "251"], message: "--per-page must be between 1 and 250" },
    { args: ["--limit", "2147483648"], message: "--limit must be between 1 and 2147483647" },
    { args: ["--sort-by", "date:sideways"], message: "--sort-by must use field, field:asc, or field:desc" },
    { args: ["--sort-by", " :asc"], message: "--sort-by must use field, field:asc, or field:desc" },
    { args: ["--sort-by", "date:asc", "--sort-by", "DATE:desc"], message: "may only be used once" },
    { args: ["date", "title", "rating", "duration", "path", "file_size"].flatMap(field => ["--sort-by", `${field}:desc`]), message: "at most 5 --sort-by options" },
  ];
  for (const testCase of cases) {
    const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--performer", "7", ...testCase.args, "--json"], {
      cwd: join(import.meta.dir, ".."), stdout: "pipe", stderr: "pipe",
    });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
    expect(exitCode).toBe(2);
    expect(stdout).toBe("");
    expect(JSON.parse(stderr).error.message).toContain(testCase.message);
  }
});

test("videos list composes a saved filter with explicit field criteria", async () => {
  const filter = { id: 12, mode: "videos", name: "Generic Filter", findFilter: JSON.stringify({ q: "saved search", sort: "date", direction: "desc" }), objectFilter: JSON.stringify({ organizedCriterion: { value: true }, TitleCriterion: { value: "old", modifier: "equals" } }) };
  const running = startServer(async request => {
    expect(request.headers.get("Authorization")).toBe("Bearer test-token");
    const url = new URL(request.url);
    if (url.pathname === "/api/savedfilters") return json([filter]);
    if (url.pathname === "/api/videos/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body.findFilter).toEqual({ page: 1, perPage: 25, q: "saved search", sort: "date", direction: "desc" });
      expect(body.objectFilter).toEqual({ organizedCriterion: { value: true }, titleCriterion: { value: "[cyoa]", modifier: "excludes" } });
      return json(page([{ id: 3, title: "Video", performers: [], files: [], unknown: 42 }], 1));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const environment = { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };

  const listResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "filters", "list", "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
  const [listStdout, listStderr, listExit] = await Promise.all([new Response(listResult.stdout).text(), new Response(listResult.stderr).text(), listResult.exited]);
  expect(listExit).toBe(0);
  expect(listStderr).toBe("");
  expect(JSON.parse(listStdout)).toEqual({ savedFilters: [filter], totalCount: 1 });

  const runResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--saved-filter", "Generic Filter", "--filter-by", "title:excludes=[cyoa]", "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
  const [runStdout, runStderr, runExit] = await Promise.all([new Response(runResult.stdout).text(), new Response(runResult.stderr).text(), runResult.exited]);
  expect(runExit).toBe(0);
  expect(runStderr).toBe("");
  expect(JSON.parse(runStdout)).toEqual({ videos: [{ id: 3, title: "Video", performers: [], files: [], unknown: 42 }], totalCount: 1 });
});

test("videos list keeps singleton saved sort arrays out of compound sorting", async () => {
  const filter = { id: 13, mode: "videos", name: "Random Filter", findFilter: JSON.stringify({ sorts: [{ key: "random", direction: "desc" }] }), objectFilter: "{}" };
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/savedfilters/13") return json(filter);
    if (url.pathname === "/api/videos/find") {
      const body = await request.json() as { findFilter: Record<string, unknown> };
      expect(body.findFilter).toMatchObject({ page: 1, perPage: 25, sort: "random", direction: "desc", seed: expect.any(Number) });
      expect(body.findFilter.sorts).toBeUndefined();
      return json(page([{ id: 3, title: "Video", performers: [], files: [] }], 1));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--saved-filter", "13", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout).totalCount).toBe(1);
});

test("videos list defaults a directionless explicit sort to ascending", async () => {
  const filter = { id: 15, mode: "videos", name: "Import Queue", findFilter: JSON.stringify({ sort: "date", direction: "desc" }), objectFilter: "{}" };
  const seeds: number[] = [];
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/savedfilters") return json([filter]);
    if (url.pathname === "/api/videos/find") {
      const body = await request.json() as { findFilter: Record<string, unknown> };
      const seed = body.findFilter.seed;
      expect(typeof seed).toBe("number");
      expect(Number(seed)).toBeGreaterThan(0);
      const pageNumber = Number(body.findFilter.page);
      seeds.push(Number(seed));
      expect(body.findFilter).toEqual({ page: pageNumber, perPage: 40, sort: "random", direction: "asc", seed });
      const firstId = (pageNumber - 1) * 40 + 1;
      const count = Math.min(40, 251 - firstId + 1);
      return json(page(Array.from({ length: count }, (_, index) => ({ id: firstId + index, title: "Video", performers: [], files: [] })), 251, pageNumber, 40));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--saved-filter", "Import Queue", "--sort-by", "random", "--unlimited", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toMatchObject({ totalCount: 251, videos: expect.any(Array) });
  expect(JSON.parse(stdout).videos).toHaveLength(251);
  expect(seeds).toHaveLength(7);
  expect(new Set(seeds).size).toBe(1);
});

test("non-video lists use matching saved-filter queries", async () => {
  const filter = { id: 14, mode: "images", name: "Unorganized", findFilter: JSON.stringify({ q: "saved search", sort: "created_at", direction: "asc" }), objectFilter: JSON.stringify({ organizedCriterion: { value: false } }) };
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/savedfilters/14") return json(filter);
    if (url.pathname === "/api/images/find") {
      expect(await request.json()).toEqual({
        findFilter: { page: 1, perPage: 25, q: "saved search", sort: "updated_at", direction: "desc", sorts: [{ key: "updated_at", direction: "desc" }, { key: "created_at", direction: "desc" }] },
        objectFilter: { organizedCriterion: { value: false } },
      });
      return json(page([{ id: 4, title: "Image", performers: [], tags: [], galleries: [], files: [] }], 1));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "images", "list", "--saved-filter", "14", "--sort-by", "updated_at:desc", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout).totalCount).toBe(1);
});

test("saved filters are applied only through entity list commands", async () => {
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "filters", "run", "12", "--json"], { cwd: join(import.meta.dir, ".."), stdout: "pipe", stderr: "pipe" });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(2);
  expect(stdout).toBe("");
  expect(JSON.parse(stderr)).toMatchObject({ error: { code: "INVALID_ARGUMENT", message: expect.stringContaining("unknown command 'run'") } });
});

test("entity-scoped saved-filter management infers and enforces the parent mode", async () => {
  const requests: Array<{ method: string; path: string; search: string; body?: unknown }> = [];
  const filter = { id: 42, mode: "images", name: "Unorganized", findFilter: "{}", objectFilter: "{}" };
  const running = startServer(async request => {
    const url = new URL(request.url);
    const body = request.method === "POST" || request.method === "PUT" ? await request.json() : undefined;
    requests.push({ method: request.method, path: url.pathname, search: url.search, body });
    if (request.method === "GET" && url.pathname === "/api/savedfilters") return json([filter]);
    if (request.method === "GET" && url.pathname === "/api/savedfilters/42") return json(filter);
    if (request.method === "GET" && url.pathname === "/api/savedfilters/99") return json({ ...filter, id: 99, mode: "videos" });
    if (request.method === "POST") return json({ ...filter, ...(body as object) }, 201);
    if (request.method === "PUT") return json({ ...filter, ...(body as object) });
    if (request.method === "DELETE") return new Response(null, { status: 204 });
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const environment = { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };
  const run = async (args: string[]) => {
    const child = Bun.spawn([process.execPath, "src/index.ts", "images", "filters", ...args, "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(child.stdout).text(), new Response(child.stderr).text(), child.exited]);
    expect(exitCode).toBe(0);
    expect(stderr).toBe("");
    return JSON.parse(stdout);
  };

  const humanList = Bun.spawn([process.execPath, "src/index.ts", "images", "filters", "list"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
  const [humanStdout, humanStderr, humanExitCode] = await Promise.all([new Response(humanList.stdout).text(), new Response(humanList.stderr).text(), humanList.exited]);
  expect(humanExitCode).toBe(0);
  expect(humanStderr).toBe("warning: credentials will be sent over plain HTTP.\n");
  expect(humanStdout).toStartWith("Saved image filters");
  expect(humanStdout).not.toContain("Saved video filters");

  expect(await run(["show", "Unorganized"])).toMatchObject({ id: 42, mode: "images" });
  expect(await run(["create", "--name", "Recent", "--find-filter", '{"sort":"created_at"}'])).toMatchObject({ mode: "images", name: "Recent" });
  expect(await run(["update", "42", "--name", "Renamed"])).toMatchObject({ id: 42, mode: "images", name: "Renamed" });
  expect(await run(["delete", "42"])).toEqual({ id: 42, deleted: true });
  expect(requests).toContainEqual({ method: "GET", path: "/api/savedfilters", search: "?mode=images" });
  expect(requests).toContainEqual({ method: "POST", path: "/api/savedfilters", search: "", body: { mode: "images", name: "Recent", findFilter: '{"sort":"created_at"}' } });
  expect(requests).toContainEqual({ method: "PUT", path: "/api/savedfilters/42", search: "", body: { name: "Renamed" } });
  expect(requests.filter(request => request.method === "GET" && request.path === "/api/savedfilters/42")).toHaveLength(2);

  for (const args of [["update", "99", "--name", "Wrong mode"], ["delete", "99"]]) {
    const child = Bun.spawn([process.execPath, "src/index.ts", "images", "filters", ...args, "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(child.stdout).text(), new Response(child.stderr).text(), child.exited]);
    expect(exitCode).toBe(1);
    expect(stdout).toBe("");
    expect(JSON.parse(stderr)).toMatchObject({ error: { code: "SAVED_FILTER_MODE_MISMATCH" } });
  }
  expect(requests.some(request => request.path === "/api/savedfilters/99" && (request.method === "PUT" || request.method === "DELETE"))).toBe(false);
});

test("every standard entity list exposes saved-filter composition", async () => {
  for (const resource of ["videos", "audios", "images", "galleries", "tags", "performers", "studios", "groups", "texts"]) {
    const processResult = Bun.spawn([process.execPath, "src/index.ts", resource, "list", "--help"], { cwd: join(import.meta.dir, ".."), stdout: "pipe", stderr: "pipe" });
    const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
    expect(exitCode).toBe(0);
    expect(stderr).toBe("");
    expect(stdout).toContain("--saved-filter <id-or-name>");
  }
});

test("videos list preserves video-specific saved-filter safety checks", async () => {
  const filter = { id: 12, mode: "videos", name: "Compilation Filter", objectFilter: JSON.stringify({ includeCompilationGroups: { value: true } }) };
  const running = startServer(request => {
    expect(new URL(request.url).pathname).toBe("/api/savedfilters/12");
    return json(filter);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--saved-filter", "12", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(1);
  expect(stdout).toBe("");
  expect(JSON.parse(stderr)).toMatchObject({ error: { code: "UNSUPPORTED_SAVED_FILTER" } });
});

test("machine-readable commands serialize empty responses as null", async () => {
  const running = startServer(request => {
    expect(request.method).toBe("GET");
    expect(new URL(request.url).pathname).toBe("/api/no-content");
    return new Response(null, { status: 204 });
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "api", "get", "no-content", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(stdout).toBe("null\n");
});

test("videos list applies the account-backed default filter", async () => {
  const storedDefault = JSON.stringify({ findFilter: { q: "default search", sort: "updated_at", direction: "desc" }, objectFilter: { organizedCriterion: { value: true } }, uiOptions: { displayMode: "list" } });
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/auth/me") return json({ user: { uiPreferences: { defaultFilters: { videos: storedDefault } } } });
    if (url.pathname === "/api/videos/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body.findFilter).toEqual({ page: 1, perPage: 25, q: "default search", sort: "updated_at", direction: "desc" });
      expect(body.objectFilter).toEqual({ organizedCriterion: { value: true } });
      return json(page([{ id: 3, title: "Video", performers: [], files: [] }], 1));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory },
    stdout: "pipe",
    stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([
    new Response(processResult.stdout).text(),
    new Response(processResult.stderr).text(),
    processResult.exited,
  ]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ videos: [{ id: 3, title: "Video", performers: [], files: [] }], totalCount: 1 });
});

test("human lists show UI-style default criteria above separated pagination", async () => {
  const storedDefault = JSON.stringify({
    findFilter: { sort: "random", direction: "desc" },
    objectFilter: { performersCriterion: { value: [1, 2, 3], modifier: "includesAll", _names: { 1: "Alexis Crystal", 2: "Lexi Dona", 3: "Paula Shy" } } },
  });
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/auth/me") return json({ user: { uiPreferences: { defaultFilters: { videos: storedDefault } } } });
    if (url.pathname === "/api/videos/find") return json(page([{ id: 9, title: "Example", performers: [], files: [] }], 124, 2, 1));
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--page", "2", "--per-page", "1"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode, stderr).toBe(0);
  expect(stdout).toStartWith("Videos\nDefault filter · Sort: Random descending · Performers: Alexis Crystal, Lexi Dona, and Paula Shy\n\n2-2 of 124 · Page 2/124");
  expect(stdout).toEndWith("2-2 of 124 · Page 2/124\n");
});

test("human saved-filter summaries reflect explicit query and sort overrides", async () => {
  const filter = { id: 12, mode: "performers", name: "Recent", findFilter: JSON.stringify({ q: "saved", sort: "latest_video_date", direction: "desc" }), objectFilter: "{}" };
  const running = startServer(request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/savedfilters/12") return json(filter);
    if (url.pathname === "/api/performers/find") return json(page([], 0));
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "performers", "list", "--saved-filter", "12", "--query", "override", "--sort-by", "name:asc"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode, stderr).toBe(0);
  expect(stdout).toStartWith("Performers\nSaved filter “Recent” · Search: “override” · Sort: Name ascending");
  expect(stdout).not.toContain("Latest Video Date descending");
});

test("human saved-filter summaries reflect explicit relation overrides", async () => {
  const filter = { id: 12, mode: "videos", name: "Tagged", findFilter: "{}", objectFilter: JSON.stringify({ tagsCriterion: { value: [4], modifier: "includes", _names: { 4: "Saved Tag" } } }) };
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/savedfilters/12") return json(filter);
    if (url.pathname === "/api/tags/9") return json({ id: 9, name: "Explicit Tag", aliases: [] });
    if (url.pathname === "/api/tags/10") return json({ id: 10, name: "Second Tag", aliases: [] });
    if (url.pathname === "/api/videos/find") {
      const body = await request.json() as { objectFilter: unknown };
      expect(body.objectFilter).toEqual({ tagsCriterion: { value: [], modifier: "includes", requiredIds: [9, 10], excludes: [] } });
      return json(page([], 0));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--saved-filter", "12", "--tag", "9", "--tag", "10"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode, stderr).toBe(0);
  expect(stdout).toStartWith("Videos\nSaved filter “Tagged” · Sort: Date descending · Tags: Explicit Tag and Second Tag");
  expect(stdout).not.toContain("Saved Tag");
});

test("videos list falls back to Cove's built-in view sort when no default is saved", async () => {
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/auth/me") return json({ user: { uiPreferences: { defaultFilters: {} } } });
    if (url.pathname === "/api/videos/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body.findFilter).toEqual({ page: 1, perPage: 25, sort: "date", direction: "desc" });
      expect(body.objectFilter).toEqual({});
      return json(page([], 0));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ videos: [], totalCount: 0 });
});

test("no-default-filter skips account defaults and uses the built-in view sort", async () => {
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/auth/me") throw new Error("default discovery must be skipped");
    if (url.pathname === "/api/performers/find") {
      const body = await request.json() as { findFilter: unknown; objectFilter: unknown };
      expect(body).toEqual({ findFilter: { page: 1, perPage: 1, sort: "latest_video_date", direction: "desc" }, objectFilter: {} });
      return json(page([{ id: 1, name: "Example", aliases: [], tags: [] }], 5910, 1, 1));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "performers", "list", "--no-default-filter", "--limit", "1", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode, stderr).toBe(0);
  expect(JSON.parse(stdout)).toEqual({ performers: [{ id: 1, name: "Example", aliases: [], tags: [] }], totalCount: 5910 });
});

test("videos list does not broaden results when default lookup fails", async () => {
  let findRequested = false;
  const running = startServer(request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/auth/me") return json({ error: "unavailable" }, 503);
    if (url.pathname === "/api/videos/find") findRequested = true;
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  expect(await processResult.exited).toBe(1);
  expect(findRequested).toBe(false);
});

test("audio and text lists apply their account-backed defaults", async () => {
  const requests: Array<{ path: string; body: unknown }> = [];
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/auth/me") return json({ user: { uiPreferences: { defaultFilters: {
      audios: JSON.stringify({ objectFilter: { organizedCriterion: { value: true } } }),
      texts: JSON.stringify({ findFilter: { q: "saved text search" }, objectFilter: { organizedCriterion: { value: false } } }),
    } } } });
    if (url.pathname === "/api/audios/find" || url.pathname === "/api/texts/find") {
      requests.push({ path: url.pathname, body: await request.json() });
      return json(page([], 0));
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const environment = { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory };
  for (const resource of ["audios", "texts"]) {
    const processResult = Bun.spawn([process.execPath, "src/index.ts", resource, "list", "--json"], { cwd: join(import.meta.dir, ".."), env: environment, stdout: "pipe", stderr: "pipe" });
    expect(await processResult.exited).toBe(0);
  }
  expect(requests).toEqual([
    { path: "/api/audios/find", body: { findFilter: { page: 1, perPage: 25, sort: "date", direction: "desc" }, objectFilter: { organizedCriterion: { value: true } } } },
    { path: "/api/texts/find", body: { findFilter: { page: 1, perPage: 25, q: "saved text search", sort: "date", direction: "desc" }, objectFilter: { organizedCriterion: { value: false } } } },
  ]);
});

test("explicit relation filters do not inherit an account default", async () => {
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/tags/9") return json({ id: 9, name: "Featured", aliases: [] });
    if (url.pathname === "/api/videos/find") {
      const body = await request.json() as { objectFilter: unknown };
      expect(body.objectFilter).toEqual({ tagsCriterion: { value: [], modifier: "includes", requiredIds: [9], excludes: [] } });
      return json(page([], 0));
    }
    if (url.pathname === "/api/auth/me") throw new Error("explicit filters must skip default discovery");
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "videos", "list", "--tag", "9", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  expect(await processResult.exited).toBe(0);
});

test("entity filters default returns only that view's account-backed default", async () => {
  const running = startServer(request => new URL(request.url).pathname === "/api/auth/me"
    ? json({ user: { uiPreferences: { defaultFilters: { audios: JSON.stringify({ findFilter: { sort: "date" } }), texts: JSON.stringify({ objectFilter: { organizedCriterion: { value: false } } }) } } } })
    : json({}, 404));
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "audios", "filters", "default", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ defaultFilter: { mode: "audios", findFilter: JSON.stringify({ sort: "date" }) } });

  const missing = Bun.spawn([process.execPath, "src/index.ts", "groups", "filters", "default", "--json"], {
    cwd: join(import.meta.dir, ".."), env: { ...process.env, COVE_SERVER: running.url, COVE_TOKEN: "test-token", COVE_CONFIG_DIR: directory }, stdout: "pipe", stderr: "pipe",
  });
  const [missingStdout, missingStderr, missingExitCode] = await Promise.all([new Response(missing.stdout).text(), new Response(missing.stderr).text(), missing.exited]);
  expect(missingExitCode).toBe(0);
  expect(missingStderr).toBe("");
  expect(JSON.parse(missingStdout)).toEqual({ defaultFilter: null });
});

test("every actionable command shows inherited options and generic examples", async () => {
  const cases = [
    { command: ["auth", "login"], example: "cove-cli auth login --server https://cove.example --profile personal" },
    { command: ["auth", "status"], example: "cove-cli auth status --profile personal" },
    { command: ["auth", "logout"], example: "cove-cli auth logout --profile personal" },
    { command: ["profiles", "list"], example: "cove-cli profiles list" },
    { command: ["profiles", "use"], example: "cove-cli profiles use personal" },
    { command: ["profiles", "remove"], example: "cove-cli profiles remove old-server" },
    { command: ["videos", "list"], example: "cove-cli videos list --performer 42 --profile personal" },
    { command: ["videos", "show"], example: "cove-cli videos show 42 --profile personal" },
    { command: ["videos", "filters", "list"], example: "cove-cli videos filters list --profile personal" },
    { command: ["videos", "filters", "default"], example: "cove-cli videos filters default --profile personal" },
    { command: ["audios", "list"], example: "cove-cli audios list --performer 42 --profile personal" },
    { command: ["audios", "show"], example: "cove-cli audios show 42 --profile personal" },
    { command: ["images", "list"], example: "cove-cli images list --profile personal" },
    { command: ["images", "show"], example: "cove-cli images show 42 --profile personal" },
    { command: ["galleries", "list"], example: "cove-cli galleries list --profile personal" },
    { command: ["galleries", "show"], example: "cove-cli galleries show 42 --profile personal" },
    { command: ["tags", "list"], example: "cove-cli tags list --profile personal" },
    { command: ["tags", "show"], example: "cove-cli tags show 42 --profile personal" },
    { command: ["performers", "list"], example: "cove-cli performers list --profile personal" },
    { command: ["performers", "show"], example: "cove-cli performers show 42 --profile personal" },
    { command: ["studios", "list"], example: "cove-cli studios list --profile personal" },
    { command: ["studios", "show"], example: "cove-cli studios show 42 --profile personal" },
    { command: ["groups", "list"], example: "cove-cli groups list --query \"Example\"" },
    { command: ["groups", "show"], example: "cove-cli groups show 42 --profile personal" },
    { command: ["groups", "items", "list"], example: "cove-cli groups items list 42 --profile personal" },
    { command: ["groups", "items", "move"], example: "cove-cli groups items move 42 103 --first" },
    { command: ["texts", "list"], example: "cove-cli texts list --query \"Example\"" },
    { command: ["texts", "show"], example: "cove-cli texts show 42 --profile personal" },
    { command: ["segments", "list"], example: "cove-cli segments list --video 42" },
    { command: ["segments", "show"], example: "cove-cli segments show 42 --profile personal" },
  ];
  for (const testCase of cases) {
    const processResult = Bun.spawn([process.execPath, "src/index.ts", ...testCase.command, "--help"], {
      cwd: join(import.meta.dir, ".."),
      stdout: "pipe",
      stderr: "pipe",
    });
    const [stdout, stderr, exitCode] = await Promise.all([
      new Response(processResult.stdout).text(),
      new Response(processResult.stderr).text(),
      processResult.exited,
    ]);
    expect(exitCode).toBe(0);
    expect(stderr).toBe("");
    expect(stdout).toContain("Global Options:");
    expect(stdout).toContain("--server <url>");
    expect(stdout).toContain("--profile <name>");
    expect(stdout).toContain("Examples:");
    expect(stdout).toContain(testCase.example);
    if (testCase.command.length === 2 && testCase.command[1] === "list" && (testCase.command[0] === "videos" || testCase.command[0] === "audios")) {
      expect(stdout).toContain("Sort:");
      expect(stdout).toContain(`cove-cli help ${testCase.command.join(" ")}`);
      expect(stdout).not.toContain("title, rating, play_count");
    }
  }
});

test("logout with a transient environment token leaves stored credentials untouched", async () => {
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const store = new ConfigStore(directory);
  await store.save({
    version: 1,
    defaultProfile: "default",
    profiles: { default: { server: "https://stored.test", credential: { type: "apiToken", token: "stored-token" } } },
  });
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "auth", "logout", "--json"], {
    cwd: join(import.meta.dir, ".."),
    env: { ...process.env, COVE_TOKEN: "transient-token", COVE_CONFIG_DIR: directory },
    stdout: "pipe",
    stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([
    new Response(processResult.stdout).text(),
    new Response(processResult.stderr).text(),
    processResult.exited,
  ]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ profile: "default", loggedOut: true });
  expect((await store.load()).profiles.default?.credential).toEqual({ type: "apiToken", token: "stored-token" });
});

test("API-token login validates and persists a secret-free JSON result", async () => {
  const running = startServer(request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/system/status") return json({ version: "1.2.3", authEnabled: true });
    if (url.pathname === "/api/auth/me") {
      expect(request.headers.get("Authorization")).toBe("Bearer agent-token");
      return json({ user: { username: "agent", kind: "apiToken" }, permissions: ["videos.read"] });
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "auth", "login", "--server", running.url, "--json"], {
    cwd: join(import.meta.dir, ".."),
    env: { ...process.env, COVE_TOKEN: "agent-token", COVE_CONFIG_DIR: directory },
    stdout: "pipe",
    stderr: "pipe",
  });
  const [stdout, stderr, exitCode] = await Promise.all([new Response(processResult.stdout).text(), new Response(processResult.stderr).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stderr).toBe("");
  expect(JSON.parse(stdout)).toEqual({ profile: "default", server: running.url, authentication: { kind: "apiToken", username: "agent" } });
  expect(JSON.stringify(JSON.parse(stdout))).not.toContain("agent-token");
  expect((await new ConfigStore(directory).load()).profiles.default?.credential).toEqual({ type: "apiToken", token: "agent-token" });
});

test("status distinguishes an auth-enabled logged-out server", async () => {
  const running = startServer(request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/system/status") return json({ version: "1.2.3", authEnabled: true });
    if (url.pathname === "/api/auth/me") return json({ code: "UNAUTHORIZED" }, 401);
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  await new ConfigStore(directory).save({ version: 1, defaultProfile: "default", profiles: { default: { server: running.url } } });
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "auth", "status"], {
    cwd: join(import.meta.dir, ".."),
    env: { ...process.env, COVE_CONFIG_DIR: directory },
    stdout: "pipe",
    stderr: "pipe",
  });
  const [stdout, exitCode] = await Promise.all([new Response(processResult.stdout).text(), processResult.exited]);
  expect(exitCode).toBe(0);
  expect(stdout).toContain("not authenticated");
  expect(stdout).not.toContain("authentication disabled");
});

test("logout does not erase a newer credential saved concurrently", async () => {
  let releaseLogout!: () => void;
  const release = new Promise<void>(resolve => { releaseLogout = resolve; });
  let observeRequest!: () => void;
  const requested = new Promise<void>(resolve => { observeRequest = resolve; });
  const running = startServer(async request => {
    if (new URL(request.url).pathname === "/api/auth/logout") {
      observeRequest();
      await release;
      return json({ message: "Logged out" });
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const store = new ConfigStore(directory);
  await store.save({ version: 1, defaultProfile: "default", profiles: { default: { server: running.url, credential: { type: "session", accessToken: "old-access", refreshToken: "old-refresh" } } } });
  const processResult = Bun.spawn([process.execPath, "src/index.ts", "auth", "logout", "--json"], {
    cwd: join(import.meta.dir, ".."),
    env: { ...process.env, COVE_CONFIG_DIR: directory },
    stdout: "pipe",
    stderr: "pipe",
  });
  await requested;
  await store.update(config => {
    config.profiles.default!.credential = { type: "session", accessToken: "new-access", refreshToken: "new-refresh" };
  });
  releaseLogout();
  expect(await processResult.exited).toBe(0);
  expect((await store.load()).profiles.default?.credential).toMatchObject({ accessToken: "new-access", refreshToken: "new-refresh" });
});
