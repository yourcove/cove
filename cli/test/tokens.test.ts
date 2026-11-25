import { describe, expect, test } from "bun:test";
import { CoveClient } from "../src/client";
import { ConfigStore } from "../src/config";
import { grantsPermission, heldPermissions, parseScope, SCOPE_PRESETS, unheldScope } from "../src/tokens";
import { join } from "node:path";
import { renderIssuedApiToken } from "../src/output";
import type { IssuedApiToken } from "../src/types";
import { json, runCli, useTestResources } from "./helpers";

const resources = useTestResources();

/** Every command warns once when a profile talks to a plain-HTTP server. */
const HTTP_WARNING = "warning: credentials will be sent over plain HTTP.\n";

interface TokenServerOptions {
  permissions?: string[];
  readGrantedEntityKinds?: string[];
  tokens?: unknown[];
  createStatus?: number;
}

function tokenRecord(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: "6f1c2d9e-2c7e-4a51-9a2f-1d6b0f8a4c33",
    name: "agent",
    prefix: "a1b2",
    scope: null,
    createdAt: "2026-09-01T10:00:00Z",
    lastUsedAt: null,
    expiresAt: null,
    ...overrides,
  };
}

function tokenServer(options: TokenServerOptions = {}): { url: string; requests: Array<{ method: string; path: string; body?: unknown }> } {
  const requests: Array<{ method: string; path: string; body?: unknown }> = [];
  const running = resources.startServer(async request => {
    const path = new URL(request.url).pathname;
    const body: unknown = request.method === "POST" ? await request.json() : undefined;
    requests.push({ method: request.method, path, ...(body === undefined ? {} : { body }) });
    if (path === "/api/auth/me") {
      return json({
        user: { id: "1", username: "owner" },
        permissions: options.permissions ?? ["*"],
        readGrantedEntityKinds: options.readGrantedEntityKinds ?? [],
      });
    }
    if (path === "/api/apitokens" && request.method === "GET") return json(options.tokens ?? [tokenRecord()]);
    if (path === "/api/apitokens" && request.method === "POST") {
      const requested = body as { name: string; scope?: string[]; expiresAt?: string };
      if (options.createStatus) {
        // Mirrors the server: the 403 names the permissions that caused the rejection.
        const offending = (requested.scope ?? []).filter(key => !(options.permissions ?? ["*"]).includes(key));
        return json({
          code: "FORBIDDEN",
          message: `API token scopes must be known permissions already held by the requesting identity (not held by the requesting identity: ${offending.join(", ")}).`,
          missing: offending,
        }, options.createStatus);
      }
      return json(tokenRecord({
        name: requested.name,
        scope: requested.scope ?? null,
        expiresAt: requested.expiresAt ?? null,
        plaintextToken: "cove_pat_6f1c2d9e2c7e4a519a2f1d6b0f8a4c33_secret",
      }));
    }
    if (path.startsWith("/api/apitokens/") && request.method === "DELETE") return new Response(null, { status: 204 });
    return json({}, 404);
  });
  return { url: running.url, requests };
}

describe("scope parsing", () => {
  test("expands presets, splits repeated and comma-separated values, and deduplicates", () => {
    expect(parseScope([])).toEqual({ explicit: [], presets: [] });
    expect(parseScope(["viewer"])).toEqual({ keys: SCOPE_PRESETS.viewer!, explicit: [], presets: ["viewer"] });
    expect(SCOPE_PRESETS.viewer).toContain("files.read");
    expect(parseScope(["videos.read,images.read", " files.read "])).toEqual({
      keys: ["videos.read", "images.read", "files.read"],
      explicit: ["videos.read", "images.read", "files.read"],
      presets: [],
    });
    const mixed = parseScope(["viewer,videos.read", "system.settings.write"]);
    expect(mixed.presets).toEqual(["viewer"]);
    expect(mixed.explicit).toEqual(["videos.read", "system.settings.write"]);
    expect(mixed.keys).toEqual([...SCOPE_PRESETS.viewer!, "system.settings.write"]);
  });

  test("rejects wildcards, unknown presets, and empty values", () => {
    expect(() => parseScope(["videos.*"])).toThrow(/wildcard token scopes/);
    expect(() => parseScope(["*.read"])).toThrow(/wildcard token scopes/);
    expect(() => parseScope(["*"])).toThrow(/Omit --scope/);
    expect(() => parseScope(["readonly"])).toThrow(/is not a permission key or a scope preset/);
    expect(() => parseScope(["Videos.Read"])).toThrow(/Permission keys are lowercase/);
    // Inherited Object members must not resolve as presets.
    for (const inherited of ["toString", "constructor", "valueOf", "__proto__"]) {
      expect(() => parseScope([inherited])).toThrow(/is not a permission key or a scope preset/);
    }
    expect(() => parseScope([" , "])).toThrow(/at least one permission or preset/);
  });

  test("the viewer preset includes the read keys of the server's Viewer role defaults plus file access", async () => {
    // Read the server's list rather than trusting the comment, so adding a read key there is a
    // visible decision here. The preset stays read-only, so it leaves out the Viewer role's
    // non-read keys (managing its own saved filters), and adds read-only file metadata access.
    const permissions = await Bun.file(join(import.meta.dir, "../../src/Cove.Core/Auth/Permissions.cs")).text();
    const block = /ViewerDefaults\s*=\s*\[(?<keys>[^\]]*)\]/s.exec(permissions)?.groups?.keys;
    expect(block).toBeString();
    const constants = new Map([...permissions.matchAll(/public const string (?<name>\w+) = "(?<key>[^"]+)";/g)]
      .map(match => [match.groups!.name!, match.groups!.key!]));
    const viewerDefaults = block!.split(",").map(entry => entry.trim()).filter(Boolean).map(name => {
      expect(constants.has(name)).toBe(true);
      return constants.get(name)!;
    });

    expect(viewerDefaults.length).toBeGreaterThan(10);
    const viewerReadDefaults = viewerDefaults.filter(key => key.endsWith(".read"));
    expect([...SCOPE_PRESETS.viewer!].sort()).toEqual([...new Set([...viewerReadDefaults, "files.read"])].sort());
    for (const key of SCOPE_PRESETS.viewer!) expect(key).toMatch(/\.read$/);
  });
});

describe("permission checks", () => {
  test("mirrors the server's wildcard matching", () => {
    expect(grantsPermission(["*"], "videos.delete")).toBe(true);
    expect(grantsPermission(["videos.read"], "videos.read")).toBe(true);
    expect(grantsPermission(["videos.*"], "videos.delete.file")).toBe(true);
    expect(grantsPermission(["*.read"], "images.read")).toBe(true);
    expect(grantsPermission(["*.read"], "images.write")).toBe(false);
    expect(grantsPermission(["videos.read"], "images.read")).toBe(false);
    expect(grantsPermission(["videos.read"], "videos")).toBe(false);
  });

  test("scoped read grants satisfy the matching read permission", () => {
    const held = { permissions: ["system.read"], readGrantedEntityKinds: ["video", "file"] };
    expect(unheldScope(held, ["system.read", "videos.read", "files.read"])).toEqual([]);
    expect(unheldScope(held, ["images.read", "videos.write"])).toEqual(["images.read", "videos.write"]);
  });

  test("permissions are unknown when the identity endpoint rejects the credential", async () => {
    const running = resources.startServer(() => json({ code: "UNAUTHORIZED" }, 401));
    const directory = await resources.tempDirectory();
    const client = new CoveClient({ store: new ConfigStore(directory), profileName: "test", profile: { server: running.url } });
    expect(await heldPermissions(client)).toBeUndefined();
  });
});

describe("tokens commands", () => {
  test("lists tokens with their scope width", async () => {
    const server = tokenServer({ tokens: [
      tokenRecord({ name: "laptop" }),
      tokenRecord({ id: "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", name: "agent", scope: ["videos.read", "images.read"], lastUsedAt: "2026-09-18T09:30:00Z", expiresAt: "2026-12-31T00:00:00Z" }),
    ] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const human = await runCli(["tokens", "list"], { env });
    expect(human.exitCode).toBe(0);
    expect(human.stderr).toBe(HTTP_WARNING);
    expect(human.stdout).toContain("API tokens");
    expect(human.stdout).toContain("laptop");
    expect(human.stdout).toContain("full");
    expect(human.stdout).toContain("2 perms");

    const machine = await runCli(["tokens", "list", "--json"], { env });
    expect(machine.exitCode).toBe(0);
    expect(JSON.parse(machine.stdout).tokens).toHaveLength(2);
  });

  test("creates an unscoped token when no scope is requested", async () => {
    const server = tokenServer();
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "create", "automation", "--json"], { env });
    expect(result.exitCode).toBe(0);
    expect(result.stderr).toBe("");
    expect(JSON.parse(result.stdout).plaintextToken).toBe("cove_pat_6f1c2d9e2c7e4a519a2f1d6b0f8a4c33_secret");
    expect(server.requests).toEqual([{ method: "POST", path: "/api/apitokens", body: { name: "automation" } }]);
  });

  test("sends the viewer preset as explicit permission keys and shows the secret once", async () => {
    const server = tokenServer({ permissions: ["*"] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "create", "agent", "--scope", "viewer"], { env });
    expect(result.exitCode).toBe(0);
    expect(result.stderr).toBe(HTTP_WARNING);
    expect(result.stdout).toContain("API token issued");
    expect(result.stdout).toContain("cove_pat_6f1c2d9e2c7e4a519a2f1d6b0f8a4c33_secret");
    expect(result.stdout).toContain("will not show it again");
    const create = server.requests.find(entry => entry.method === "POST");
    expect((create?.body as { scope: string[] }).scope).toEqual(SCOPE_PRESETS.viewer!);
  });

  test("warns and keeps going when a named scope permission is not held", async () => {
    const server = tokenServer({ permissions: ["videos.read"], createStatus: 403 });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "create", "agent", "--scope", "videos.read,users.write"], { env });
    expect(result.stderr).toContain("warning:");
    expect(result.stderr).toContain("users.write was asked for by name");
    expect(result.stderr).not.toContain("videos.read");
    const create = server.requests.find(entry => entry.method === "POST");
    expect((create?.body as { scope: string[] }).scope).toEqual(["videos.read", "users.write"]);
    expect(result.exitCode).toBe(1);
    expect(result.stderr).toContain("not held by the requesting identity: users.write");
  });

  test("drops preset permissions the identity does not hold", async () => {
    const server = tokenServer({ permissions: ["videos.read", "images.read", "system.read"] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "create", "agent", "--scope", "viewer"], { env });
    expect(result.exitCode).toBe(0);
    expect(result.stderr).toContain("viewer scope preset includes 17 permissions the current identity does not hold");
    expect(result.stderr).toMatch(/leaving out audios\.read, .*, and 11 more\./);
    const create = server.requests.find(entry => entry.method === "POST");
    expect((create?.body as { scope: string[] }).scope).toEqual(["videos.read", "images.read", "system.read"]);
  });

  test("refuses a preset that shares no permission with the current identity", async () => {
    const server = tokenServer({ permissions: ["users.write"] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const machine = await runCli(["tokens", "create", "agent", "--scope", "viewer", "--json"], { env });
    expect(machine.exitCode).toBe(1);
    expect(JSON.parse(machine.stderr)).toMatchObject({ error: { code: "SCOPE_NOT_HELD" } });

    const human = await runCli(["tokens", "create", "agent", "--scope", "viewer"], { env });
    expect(human.exitCode).toBe(1);
    expect(human.stderr).toContain("holds none of the requested scope permissions");
    expect(server.requests.some(entry => entry.method === "POST")).toBe(false);
  });

  test("a permission named alongside a preset is sent even when the preset narrows", async () => {
    const server = tokenServer({ permissions: ["videos.read", "images.read", "system.read"], createStatus: 403 });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "create", "agent", "--scope", "viewer,users.write"], { env });
    expect(result.stderr).toContain("users.write was asked for by name");
    const create = server.requests.find(entry => entry.method === "POST");
    expect((create?.body as { scope: string[] }).scope).toEqual(["videos.read", "images.read", "system.read", "users.write"]);
    expect(result.exitCode).toBe(1);
  });

  test("a scope permission the identity holds is never dropped for being named twice", async () => {
    const server = tokenServer({ permissions: ["videos.read", "images.read", "system.read"] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "create", "agent", "--scope", "viewer,videos.read"], { env });
    expect(result.exitCode).toBe(0);
    const create = server.requests.find(entry => entry.method === "POST");
    expect((create?.body as { scope: string[] }).scope).toEqual(["videos.read", "images.read", "system.read"]);
  });

  test("rejects wildcard scopes and malformed expiry before contacting the server", async () => {
    const server = tokenServer();
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const wildcard = await runCli(["tokens", "create", "agent", "--scope", "videos.*", "--json"], { env });
    expect(wildcard.exitCode).toBe(2);
    expect(JSON.parse(wildcard.stderr).error.message).toContain("wildcard token scopes");

    const expiry = await runCli(["tokens", "create", "agent", "--expires", "next friday", "--json"], { env });
    expect(expiry.exitCode).toBe(2);
    expect(JSON.parse(expiry.stderr).error.message).toContain("ISO-8601");

    expect(server.requests).toEqual([]);
  });

  test("normalizes the expiry and warns when it has already passed", async () => {
    const server = tokenServer();
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "create", "ci", "--expires", "2020-01-01T00:00:00Z"], { env });
    expect(result.exitCode).toBe(0);
    expect(result.stderr).toContain("the token would be expired as soon as it is issued");
    const create = server.requests.find(entry => entry.method === "POST");
    expect(create?.body).toEqual({ name: "ci", expiresAt: "2020-01-01T00:00:00.000Z" });

    const machine = await runCli(["tokens", "create", "ci", "--expires", "2020-01-01T00:00:00Z", "--json"], { env });
    expect(machine.exitCode).toBe(2);
    expect(JSON.parse(machine.stderr).error.code).toBe("INVALID_ARGUMENT");

    // A value with no zone is read as UTC rather than as the machine's local time.
    const zoneless = await runCli(["tokens", "create", "ci", "--expires", "2099-06-01T12:00:00", "--json"], { env });
    expect(zoneless.exitCode).toBe(0);
    expect(server.requests.at(-1)?.body).toEqual({ name: "ci", expiresAt: "2099-06-01T12:00:00.000Z" });
  });

  test("revokes by ID and by unique name", async () => {
    const server = tokenServer({ tokens: [tokenRecord({ name: "agent" })] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const byId = await runCli(["tokens", "revoke", "6F1C2D9E-2C7E-4A51-9A2F-1D6B0F8A4C33"], { env });
    expect(byId.exitCode).toBe(0);
    expect(byId.stdout).toContain("API token revoked");
    expect(server.requests).toEqual([
      { method: "GET", path: "/api/apitokens" },
      { method: "DELETE", path: "/api/apitokens/6f1c2d9e-2c7e-4a51-9a2f-1d6b0f8a4c33" },
    ]);

    const byName = await runCli(["tokens", "revoke", "AGENT", "--json"], { env });
    expect(byName.exitCode).toBe(0);
    expect(JSON.parse(byName.stdout)).toEqual({ id: "6f1c2d9e-2c7e-4a51-9a2f-1d6b0f8a4c33", revoked: true });

    const missing = await runCli(["tokens", "revoke", "unknown", "--json"], { env });
    expect(missing.exitCode).toBe(1);
    expect(JSON.parse(missing.stderr)).toMatchObject({ error: { code: "TOKEN_NOT_FOUND" } });
  });

  test("refuses to guess between tokens that share a name", async () => {
    const server = tokenServer({ tokens: [
      tokenRecord({ name: "agent" }),
      tokenRecord({ id: "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", name: "agent" }),
    ] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "revoke", "agent", "--json"], { env });
    expect(result.exitCode).toBe(1);
    expect(JSON.parse(result.stderr)).toMatchObject({ error: { code: "AMBIGUOUS_TOKEN" } });
    expect(server.requests.some(entry => entry.method === "DELETE")).toBe(false);
  });
});

describe("token secret and machine output", () => {
  test("the issued secret is never reflowed, whatever the terminal width", async () => {
    // A wrapped secret cannot be pasted back, and Cove keeps only its hash. A real token is
    // 85 characters, so anything narrower than that is where hard-wrapping would show up.
    const secret = `cove_pat_${"a".repeat(32)}_${"b".repeat(43)}`;
    expect(secret).toHaveLength(85);
    const issued = { ...tokenRecord(), plaintextToken: secret } as IssuedApiToken;
    for (const terminalWidth of [30, 40, 80, 86, 200]) {
      expect(renderIssuedApiToken(issued, { color: false, hyperlinks: false, terminalWidth }).split("\n")).toContain(secret);
    }

    const server = tokenServer();
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });
    const result = await runCli(["tokens", "create", "agent"], { env });
    expect(result.exitCode).toBe(0);
    expect(result.stdout.split("\n")).toContain("cove_pat_6f1c2d9e2c7e4a519a2f1d6b0f8a4c33_secret");
  });

  test("the secret is not written to the profile store", async () => {
    const server = tokenServer();
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    await runCli(["tokens", "create", "agent", "--json"], { env });
    const stored = await Bun.file(join(env.COVE_CONFIG_DIR!, "cli.json")).text().catch(() => "");
    expect(stored).not.toContain("cove_pat_6f1c2d9e2c7e4a519a2f1d6b0f8a4c33_secret");
  });

  test("machine output refuses to quietly narrow a preset", async () => {
    const server = tokenServer({ permissions: ["videos.read", "images.read", "system.read"] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "create", "agent", "--scope", "viewer", "--json"], { env });
    expect(result.exitCode).toBe(1);
    const error = JSON.parse(result.stderr).error;
    expect(error.code).toBe("SCOPE_NOT_HELD");
    expect(error.message).toContain("audios.read");
    expect(error.message).toContain("Name the permissions to include explicitly");
    expect(server.requests.some(entry => entry.method === "POST")).toBe(false);
  });

  test("lists tokens as JSON Lines", async () => {
    const server = tokenServer({ tokens: [
      tokenRecord({ name: "laptop" }),
      tokenRecord({ id: "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", name: "agent", scope: ["videos.read"] }),
    ] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "list", "--output", "jsonl"], { env });
    expect(result.exitCode).toBe(0);
    const rows = result.stdout.trimEnd().split("\n").map(line => JSON.parse(line));
    expect(rows.map(row => row.name)).toEqual(["laptop", "agent"]);
  });

  test("an identity endpoint failure other than 401 stops the create", async () => {
    const requests: string[] = [];
    const running = resources.startServer(request => {
      const path = new URL(request.url).pathname;
      requests.push(`${request.method} ${path}`);
      if (path === "/api/auth/me") return json({ code: "SERVER_ERROR", message: "identity unavailable" }, 500);
      return json({}, 404);
    });
    const env = await resources.cliEnvironment({ server: running.url, token: "test-token" });

    const result = await runCli(["tokens", "create", "agent", "--scope", "videos.read", "--json"], { env });
    expect(result.exitCode).toBe(1);
    expect(JSON.parse(result.stderr).error.message).toContain("identity unavailable");
    expect(requests).toEqual(["GET /api/auth/me"]);
  });
});

describe("revoke safety", () => {
  test("an ID that is not the caller's own is reported instead of claimed as revoked", async () => {
    // Cove answers 204 for an unknown, already-revoked, or someone else's token.
    const server = tokenServer({ tokens: [tokenRecord({ name: "agent" })] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "revoke", "6f1c2d9e-2c7e-4a51-9a2f-1d6b0f8a4c34", "--json"], { env });
    expect(result.exitCode).toBe(1);
    expect(JSON.parse(result.stderr)).toMatchObject({ error: { code: "TOKEN_NOT_FOUND" } });
    expect(server.requests.some(entry => entry.method === "DELETE")).toBe(false);
  });

  test("accepts the dashless ID form carried inside the token string", async () => {
    const server = tokenServer({ tokens: [tokenRecord({ name: "agent" })] });
    const env = await resources.cliEnvironment({ server: server.url, token: "test-token" });

    const result = await runCli(["tokens", "revoke", "6f1c2d9e2c7e4a519a2f1d6b0f8a4c33", "--json"], { env });
    expect(result.exitCode).toBe(0);
    expect(JSON.parse(result.stdout)).toEqual({ id: "6f1c2d9e-2c7e-4a51-9a2f-1d6b0f8a4c33", revoked: true });
    expect(server.requests.at(-1)).toEqual({ method: "DELETE", path: "/api/apitokens/6f1c2d9e-2c7e-4a51-9a2f-1d6b0f8a4c33" });
  });
});

test("a scope is sent unchanged when the server will not report the identity's permissions", async () => {
  const requests: Array<{ method: string; path: string; body?: unknown }> = [];
  const running = resources.startServer(async request => {
    const path = new URL(request.url).pathname;
    const body: unknown = request.method === "POST" ? await request.json() : undefined;
    requests.push({ method: request.method, path, ...(body === undefined ? {} : { body }) });
    if (path === "/api/auth/me") return json({ code: "UNAUTHORIZED" }, 401);
    return json({ ...tokenRecord({ scope: ["videos.read"] }), plaintextToken: "cove_pat_x_y" });
  });
  const env = await resources.cliEnvironment({ server: running.url, token: "test-token" });

  const result = await runCli(["tokens", "create", "agent", "--scope", "videos.read,users.write", "--json"], { env });
  expect(result.exitCode).toBe(0);
  expect(requests.at(-1)).toEqual({
    method: "POST",
    path: "/api/apitokens",
    body: { name: "agent", scope: ["videos.read", "users.write"] },
  });
});
