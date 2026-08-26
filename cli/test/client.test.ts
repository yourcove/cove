import { afterEach, expect, test } from "bun:test";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { CoveClient } from "../src/client";
import { ConfigStore } from "../src/config";
import { json, startServer } from "./helpers";

const servers: Bun.Server<unknown>[] = [];
const directories: string[] = [];
afterEach(async () => {
  for (const server of servers.splice(0)) server.stop(true);
  await Promise.all(directories.splice(0).map(directory => rm(directory, { recursive: true, force: true })));
});

test("refreshes a rejected session once and persists the rotated pair", async () => {
  let resourceCalls = 0;
  const running = startServer(async request => {
    const url = new URL(request.url);
    if (url.pathname === "/api/resource") {
      resourceCalls += 1;
      return request.headers.get("Authorization") === "Bearer new-access" ? json({ ok: true }) : json({ code: "UNAUTHORIZED" }, 401);
    }
    if (url.pathname === "/api/auth/refresh") {
      expect(await request.json()).toEqual({ refreshToken: "old-refresh" });
      return json({ token: "new-access", refreshToken: "new-refresh" });
    }
    return json({}, 404);
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const store = new ConfigStore(directory);
  await store.save({ version: 1, defaultProfile: "test", profiles: { test: { server: running.url, credential: { type: "session", accessToken: "old-access", refreshToken: "old-refresh" } } } });
  const client = new CoveClient({ store, profileName: "test", profile: (await store.load()).profiles.test! });

  expect(await client.get<{ ok: boolean }>("resource")).toEqual({ ok: true });
  expect(resourceCalls).toBe(2);
  expect((await store.load()).profiles.test?.credential).toMatchObject({ accessToken: "new-access", refreshToken: "new-refresh" });
});

test("supports a shorter timeout for best-effort requests", async () => {
  const running = startServer(async () => {
    await Bun.sleep(250);
    return json({ ok: true });
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const store = new ConfigStore(directory);
  const client = new CoveClient({ store, profileName: "test", profile: { server: running.url }, timeoutMs: 5_000 });

  await expect(client.get("slow", 25)).rejects.toMatchObject({ code: "REQUEST_TIMEOUT" });
});

test("downloads authenticated non-JSON responses", async () => {
  const running = startServer(request => {
    expect(request.headers.get("Authorization")).toBe("Bearer api-token");
    return new Response(new Uint8Array([0, 1, 2, 255]), { headers: { "Content-Type": "application/octet-stream" } });
  });
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const client = new CoveClient({ store: new ConfigStore(directory), profileName: "test", profile: { server: running.url, credential: { type: "apiToken", token: "api-token" } } });

  const result = await client.download("stream/image/12");
  expect([...new Uint8Array(await new Response(result.body).arrayBuffer())]).toEqual([0, 1, 2, 255]);
  expect(result.contentType).toBe("application/octet-stream");
});

test("rejects API paths that escape the server API base", async () => {
  let requested = false;
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const client = new CoveClient({
    store: new ConfigStore(directory),
    profileName: "test",
    profile: { server: "https://cove.example" },
    fetch: (() => { requested = true; return Promise.resolve(json({})); }) as unknown as typeof fetch,
  });

  await expect(client.get("../health")).rejects.toMatchObject({ code: "INVALID_ARGUMENT" });
  await expect(client.get("%2e%2e/health")).rejects.toMatchObject({ code: "INVALID_ARGUMENT" });
  expect(requested).toBe(false);
});

test("surfaces RFC 9457 problem details from failed requests", async () => {
  const running = startServer(() => json({ title: "Unsupported compound sort.", detail: "Compound sort key 'random' is not supported.", status: 400 }, 400));
  servers.push(running.server);
  const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
  directories.push(directory);
  const client = new CoveClient({ store: new ConfigStore(directory), profileName: "test", profile: { server: running.url } });

  await expect(client.get("videos/find")).rejects.toMatchObject({
    code: "HTTP_400",
    message: "Compound sort key 'random' is not supported.",
    status: 400,
    details: { title: "Unsupported compound sort." },
  });
});
