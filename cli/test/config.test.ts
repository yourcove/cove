import { afterEach, describe, expect, test } from "bun:test";
import { mkdtemp, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ConfigStore, normalizeServer, resolveProfile } from "../src/config";

const directories: string[] = [];
afterEach(async () => Promise.all(directories.splice(0).map(directory => rm(directory, { recursive: true, force: true }))));

describe("profile configuration", () => {
  test("writes versioned configuration atomically with private POSIX permissions", async () => {
    const directory = await mkdtemp(join(tmpdir(), "cove-cli-"));
    directories.push(directory);
    const store = new ConfigStore(directory);
    await store.update(config => {
      config.defaultProfile = "home";
      config.profiles.home = { server: "https://example.test", credential: { type: "apiToken", token: "secret" } };
    });

    expect(await store.load()).toEqual({
      version: 1,
      defaultProfile: "home",
      profiles: { home: { server: "https://example.test", credential: { type: "apiToken", token: "secret" } } },
    });
    if (process.platform !== "win32") expect((await stat(store.path)).mode & 0o777).toBe(0o600);
  });

  test("uses explicit server and token ahead of stored values", () => {
    const resolved = resolveProfile(
      { version: 1, defaultProfile: "home", profiles: { home: { server: "https://stored.test", credential: { type: "apiToken", token: "stored" } } } },
      { server: "https://override.test/", token: "transient" },
    );
    expect(resolved.profile).toEqual({ server: "https://override.test", credential: { type: "apiToken", token: "transient" } });
    expect(resolved.transientCredential).toBe(true);
  });

  test("rejects non-HTTP server URLs", () => {
    expect(() => normalizeServer("file:///tmp/cove")).toThrow("HTTP or HTTPS");
    expect(() => normalizeServer("https://user:secret@example.test")).toThrow("must not contain");
  });

  test("never sends a stored credential to a server override", () => {
    const resolved = resolveProfile(
      { version: 1, defaultProfile: "home", profiles: { home: { server: "https://stored.test", credential: { type: "apiToken", token: "stored-secret" } } } },
      { server: "https://other.test" },
    );
    expect(resolved.profile).toEqual({ server: "https://other.test", credential: undefined });
  });
});
