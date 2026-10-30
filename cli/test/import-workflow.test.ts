import { describe, expect, test } from "bun:test";
import { join } from "node:path";
import { writeFile } from "node:fs/promises";
import { parseRemoteId, remoteIdObjectFilter } from "../src/remote-ids";
import { imageContentType } from "../src/uploads";
import { json, page, runCli, useTestResources } from "./helpers";

const resources = useTestResources();

interface Recorded {
  method: string;
  path: string;
  search: string;
  contentType: string;
  body?: unknown;
  form?: Record<string, { name: string; type: string; text: string } | string>;
}

/** Records every request and answers from `routes`, keyed by "METHOD /api/path"; anything else is a 404. */
function recordingServer(routes: Record<string, (recorded: Recorded) => Response | Promise<Response>>): { url: string; requests: Recorded[] } {
  const requests: Recorded[] = [];
  const running = resources.startServer(async request => {
    const url = new URL(request.url);
    const contentType = request.headers.get("content-type") ?? "";
    const recorded: Recorded = { method: request.method, path: url.pathname, search: url.search, contentType };
    if (contentType.startsWith("multipart/form-data")) {
      // Parsed by hand: Bun's formData() re-derives a file part's type from its name instead of its header.
      const boundary = /boundary=("?)([^";]+)\1/.exec(contentType)![2]!;
      recorded.form = {};
      for (const part of (await request.text()).split(`--${boundary}`).slice(1, -1)) {
        const [head, ...rest] = part.replace(/^\r\n/, "").split("\r\n\r\n");
        const text = rest.join("\r\n\r\n").replace(/\r\n$/, "");
        const field = /name="([^"]*)"/.exec(head!)![1]!;
        const name = /filename="([^"]*)"/.exec(head!)?.[1];
        recorded.form[field] = name === undefined ? text : { name, type: /content-type:\s*(\S+)/i.exec(head!)?.[1] ?? "", text };
      }
    } else {
      const text = await request.text();
      if (text) recorded.body = JSON.parse(text);
    }
    requests.push(recorded);
    const route = routes[`${request.method} ${url.pathname}`];
    return route ? route(recorded) : json({}, 404);
  });
  return { url: running.url, requests };
}

async function cli(url: string, args: string[]) {
  return runCli(args, { env: await resources.cliEnvironment({ server: url, token: "test-token" }) });
}

const PNG = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 0x0d, 0x49, 0x48, 0x44, 0x52]);

describe("remote ID references", () => {
  test("expand bare hosts the stash-box way and keep full endpoints as given", () => {
    expect(parseRemoteId("stashdb.org=abc")).toEqual({ endpoint: "https://stashdb.org/graphql", remoteId: "abc" });
    expect(parseRemoteId("https://example.test/custom=abc")).toEqual({ endpoint: "https://example.test/custom", remoteId: "abc" });
    expect(parseRemoteId("example.test/custom=abc")).toEqual({ endpoint: "https://example.test/custom", remoteId: "abc" });
    expect(parseRemoteId(" abc ")).toEqual({ remoteId: "abc" });
    expect(parseRemoteId("=abc")).toEqual({ remoteId: "abc" });
    expect(() => parseRemoteId("stashdb.org=")).toThrow("--remote-id must use endpoint=id or id.");
  });

  test("pair the endpoint and value criteria, with a blank endpoint for any-endpoint lookups", () => {
    expect(remoteIdObjectFilter({ endpoint: "https://stashdb.org/graphql", remoteId: "abc" })).toEqual({
      remoteIdCriterion: { value: "https://stashdb.org/graphql", modifier: "equals" },
      remoteIdValueCriterion: { value: "abc", modifier: "equals" },
    });
    expect(remoteIdObjectFilter({ remoteId: "abc" })).toEqual({
      remoteIdCriterion: { value: "", modifier: "equals" },
      remoteIdValueCriterion: { value: "abc", modifier: "equals" },
    });
  });

  test("list commands send the remote ID filter and skip the account default filter", async () => {
    for (const resource of ["videos", "performers", "studios", "tags"]) {
      const server = recordingServer({
        [`POST /api/${resource}/find`]: () => json(page([])),
        "GET /api/auth/me": () => { throw new Error("the default filter must not be consulted"); },
      });
      const result = await cli(server.url, [resource, "list", "--remote-id", "stashdb.org=abc", "--json"]);
      expect(result.stderr).toBe("");
      expect(result.exitCode).toBe(0);
      expect(server.requests.map(request => request.path)).toEqual([`/api/${resource}/find`]);
      expect((server.requests[0]!.body as { objectFilter: unknown }).objectFilter).toEqual({
        remoteIdCriterion: { value: "https://stashdb.org/graphql", modifier: "equals" },
        remoteIdValueCriterion: { value: "abc", modifier: "equals" },
      });
    }
  });

  test("a bare remote ID replaces the endpoint criterion of a saved filter", async () => {
    const saved = { id: 5, mode: "performers", name: "On StashDB", findFilter: "{}", objectFilter: JSON.stringify({ remoteIdCriterion: { value: "https://stashdb.org/graphql", modifier: "notEquals" }, favorite: true }) };
    const server = recordingServer({
      "GET /api/savedfilters/5": () => json(saved),
      "POST /api/performers/find": () => json(page([])),
    });
    const result = await cli(server.url, ["performers", "list", "--saved-filter", "5", "--remote-id", "abc", "--json"]);
    expect(result.stderr).toBe("");
    expect(result.exitCode).toBe(0);
    const find = server.requests.find(request => request.path === "/api/performers/find")!;
    expect((find.body as { objectFilter: unknown }).objectFilter).toEqual({
      favorite: true,
      remoteIdCriterion: { value: "", modifier: "equals" },
      remoteIdValueCriterion: { value: "abc", modifier: "equals" },
    });
  });

  test("show resolves a remote ID to the complete record", async () => {
    const performer = { id: 7, name: "Example Performer", aliases: [], remoteIds: [{ endpoint: "https://stashdb.org/graphql", remoteId: "abc" }], extra: true };
    const server = recordingServer({
      "POST /api/performers/find": () => json(page([{ id: 7, name: "Example Performer", aliases: [] }])),
      "GET /api/performers/7": () => json(performer),
    });
    const result = await cli(server.url, ["performers", "show", "--remote-id", "stashdb.org=abc", "--json"]);
    expect(result.exitCode).toBe(0);
    expect(JSON.parse(result.stdout)).toEqual(performer);
  });

  test("show reports a missing or shared remote ID with distinct codes", async () => {
    const missing = recordingServer({ "POST /api/studios/find": () => json(page([])) });
    const notFound = await cli(missing.url, ["studios", "show", "--remote-id", "stashdb.org=abc", "--json"]);
    expect(notFound.exitCode).toBe(1);
    expect(JSON.parse(notFound.stderr).error).toEqual({ code: "STUDIO_NOT_FOUND", message: "No studio has remote ID https://stashdb.org/graphql=abc." });

    const shared = recordingServer({ "POST /api/videos/find": () => json(page([{ id: 3 }, { id: 9 }])) });
    const ambiguous = await cli(shared.url, ["videos", "show", "--remote-id", "abc", "--json"]);
    expect(ambiguous.exitCode).toBe(1);
    const error = JSON.parse(ambiguous.stderr).error;
    expect(error.code).toBe("VIDEO_AMBIGUOUS");
    expect(error.details.candidates).toEqual([{ id: 3 }, { id: 9 }]);
  });

  test("show requires exactly one of a reference or --remote-id, even before a profile exists", async () => {
    const env = await resources.cliEnvironment();
    for (const [args, message] of [
      [["tags", "show"], "Pass exactly one of an ID or name or --remote-id."],
      [["studios", "show", "4", "--remote-id", "abc"], "Pass exactly one of an ID or --remote-id."],
    ] as const) {
      const result = await runCli([...args, "--json"], { env });
      expect(result.exitCode).toBe(2);
      expect(JSON.parse(result.stderr).error).toMatchObject({ code: "INVALID_ARGUMENT", message });
    }
  });

  test("show keeps plain ID lookups and leaves --remote-id off resources without remote IDs", async () => {
    const studio = { id: 4, name: "Example Studio", aliases: [] };
    const server = recordingServer({ "GET /api/studios/4": () => json(studio) });
    const byId = await cli(server.url, ["studios", "show", "4", "--json"]);
    expect(byId.exitCode).toBe(0);
    expect(JSON.parse(byId.stdout)).toEqual(studio);
    const gallery = await cli(server.url, ["galleries", "show", "--remote-id", "abc", "--json"]);
    expect(gallery.exitCode).toBe(2);
    expect(server.requests.map(request => request.path)).toEqual(["/api/studios/4"]);
  });

  test("--remote-id may only be given once", async () => {
    const server = recordingServer({});
    const result = await cli(server.url, ["videos", "list", "--remote-id", "a", "--remote-id", "b", "--json"]);
    expect(result.exitCode).toBe(2);
    expect(JSON.parse(result.stderr).error.message).toBe("--remote-id may only be specified once.");
  });
});

describe("videos import-file", () => {
  const video = (id: number) => ({ id, title: null, performers: [], files: [{ id: id * 10 }] });

  test("registers server paths in order and prints the created videos", async () => {
    let next = 41;
    const server = recordingServer({ "POST /api/videos/from-file": () => json(video(++next), 201) });
    const result = await cli(server.url, ["videos", "import-file", "/media/a.mp4", "C:\\media\\b.mkv", "--json"]);
    expect(result.exitCode).toBe(0);
    expect(server.requests.map(request => request.body)).toEqual([{ filePath: "/media/a.mp4" }, { filePath: "C:\\media\\b.mkv" }]);
    expect(JSON.parse(result.stdout).videos.map((item: { id: number }) => item.id)).toEqual([42, 43]);
  });

  test("stops at the first failure and names the videos already imported", async () => {
    const server = recordingServer({
      "POST /api/videos/from-file": recorded => (recorded.body as { filePath: string }).filePath === "/media/a.mp4"
        ? json(video(42), 201)
        : json({ error: "A valid file path is required." }, 400),
    });
    const result = await cli(server.url, ["videos", "import-file", "/media/a.mp4", "/media/missing.mp4", "/media/c.mp4", "--json"]);
    expect(result.exitCode).toBe(1);
    expect(server.requests).toHaveLength(2);
    const error = JSON.parse(result.stderr).error;
    expect(error.message).toBe("/media/missing.mp4: A valid file path is required. Already imported: 42.");
    expect(error.details).toEqual({ imported: [{ path: "/media/a.mp4", id: 42 }], failedPath: "/media/missing.mp4", failure: { error: "A valid file path is required." } });
  });

  test("rejects relative paths before sending anything", async () => {
    const server = recordingServer({});
    const result = await cli(server.url, ["videos", "import-file", "/media/a.mp4", "incoming/b.mp4", "--json"]);
    expect(result.exitCode).toBe(2);
    expect(JSON.parse(result.stderr).error.message).toContain("“incoming/b.mp4” is not an absolute path");
    expect(server.requests).toEqual([]);
  });
});

describe("library image", () => {
  test("detects image types from their leading bytes", () => {
    expect(imageContentType(Uint8Array.from([0xff, 0xd8, 0xff, 0xe0]))).toBe("image/jpeg");
    expect(imageContentType(PNG)).toBe("image/png");
    expect(imageContentType(new TextEncoder().encode("RIFF\0\0\0\0WEBPVP8 "))).toBe("image/webp");
    expect(imageContentType(new TextEncoder().encode("\0\0\0\x1cftypavif"))).toBe("image/avif");
    expect(imageContentType(new TextEncoder().encode("GIF89a"))).toBe("image/gif");
    expect(imageContentType(new TextEncoder().encode("\0\0\0\x18ftypmp42"))).toBeUndefined();
  });

  test("uploads the file as multipart form data with its detected type", async () => {
    const directory = await resources.tempDirectory();
    const file = join(directory, "cover.bin");
    await writeFile(file, PNG);
    const server = recordingServer({ "POST /api/performers/7/image": () => json({ blobId: "blob-1" }) });
    const result = await cli(server.url, ["library", "image", "performers", "7", "--file", file, "--json"]);
    expect(result.exitCode).toBe(0);
    expect(server.requests[0]!.contentType).toStartWith("multipart/form-data");
    expect(server.requests[0]!.form!.file).toMatchObject({ name: "cover.bin", type: "image/png" });
    expect(JSON.parse(result.stdout)).toEqual({ resource: "performers", id: 7, file, contentType: "image/png", blobId: "blob-1" });
  });

  test("rejects resources without images and files that are not images before sending anything", async () => {
    const directory = await resources.tempDirectory();
    const image = join(directory, "cover.png");
    const text = join(directory, "notes.txt");
    await writeFile(image, PNG);
    await writeFile(text, "not an image");
    const server = recordingServer({});
    const group = await cli(server.url, ["library", "image", "groups", "7", "--file", image, "--json"]);
    expect(group.exitCode).toBe(2);
    expect(JSON.parse(group.stderr).error.message).toBe("groups do not have an uploadable image. Supported resources: videos, performers, studios, tags, galleries, audios, texts.");
    const notImage = await cli(server.url, ["library", "image", "videos", "7", "--file", text, "--json"]);
    expect(notImage.exitCode).toBe(2);
    expect(JSON.parse(notImage.stderr).error.message).toContain("is not a JPEG, PNG, GIF, WebP, AVIF, or BMP image");
    const missing = await cli(server.url, ["library", "image", "videos", "7", "--file", join(directory, "absent.png"), "--json"]);
    expect(missing.exitCode).toBe(2);
    expect(JSON.parse(missing.stderr).error.message).toBe(`No file exists at “${join(directory, "absent.png")}”.`);
    expect(server.requests).toEqual([]);
  });
});

describe("maintenance scan", () => {
  test("targets the given paths through the selective scan endpoint", async () => {
    const server = recordingServer({ "POST /api/metadata/scan": () => json({ jobId: "job-1" }) });
    const result = await cli(server.url, ["maintenance", "scan", "--path", "/media/a", "--path", "/media/b", "--generate-previews", "--json"]);
    expect(result.exitCode).toBe(0);
    expect(server.requests[0]!.body).toEqual({ paths: ["/media/a", "/media/b"], scanGeneratePreviews: true });
    expect(JSON.parse(result.stdout)).toEqual({ jobId: "job-1" });
  });

  test("keeps the full-library scan when no path is given", async () => {
    const server = recordingServer({ "POST /api/jobs/scan": () => json({ jobId: "job-2" }, 202) });
    const result = await cli(server.url, ["maintenance", "scan", "--json"]);
    expect(result.exitCode).toBe(0);
    expect(server.requests[0]!.search).toBe("?generatePreviews=false");
  });

  test("rejects relative paths, which Cove would skip silently", async () => {
    const server = recordingServer({});
    const result = await cli(server.url, ["maintenance", "scan", "--path", "incoming", "--json"]);
    expect(result.exitCode).toBe(2);
    expect(JSON.parse(result.stderr).error.message).toContain("--path “incoming” is not an absolute path");
    expect(server.requests).toEqual([]);
  });

  test("names the permission a token lacks", async () => {
    const server = recordingServer({ "POST /api/metadata/scan": () => json({ code: "FORBIDDEN", missing: ["library.scan"] }, 403) });
    const result = await cli(server.url, ["maintenance", "scan", "--path", "/media/a", "--json"]);
    expect(result.exitCode).toBe(1);
    expect(JSON.parse(result.stderr).error.message).toBe("Not permitted (missing: library.scan).");
  });
});

describe("api mutations", () => {
  test("send JSON bodies and query parameters", async () => {
    const server = recordingServer({ "PUT /api/videos/4": () => json({ id: 4, title: "Example" }) });
    const result = await cli(server.url, ["api", "put", "videos/4", "--data", "{\"title\":\"Example\"}", "--param", "force=true", "--json"]);
    expect(result.exitCode).toBe(0);
    expect(server.requests[0]).toMatchObject({ method: "PUT", search: "?force=true", contentType: "application/json", body: { title: "Example" } });
    expect(JSON.parse(result.stdout)).toEqual({ id: 4, title: "Example" });
  });

  test("upload a file under the chosen form field", async () => {
    const directory = await resources.tempDirectory();
    const file = join(directory, "archive.zip");
    await writeFile(file, "PK\x03\x04");
    const server = recordingServer({ "POST /api/extensions/install-from-zip": () => new Response(null, { status: 204 }) });
    const result = await cli(server.url, ["api", "post", "extensions/install-from-zip", "--file", file, "--field", "package", "--json"]);
    expect(result.exitCode).toBe(0);
    expect(server.requests[0]!.form!.package).toMatchObject({ name: "archive.zip", type: "application/zip", text: "PK\x03\x04" });
    expect(JSON.parse(result.stdout)).toBeNull();
  });

  test("keep an error reason sent as a bare JSON string, as ASP.NET does for BadRequest(\"reason\")", async () => {
    const server = recordingServer({ "POST /api/videos/4/image": () => json("File must be an image.", 400) });
    const result = await cli(server.url, ["api", "post", "videos/4/image", "--data", "{}", "--json"]);
    expect(result.exitCode).toBe(1);
    expect(JSON.parse(result.stderr).error).toEqual({ code: "HTTP_400", message: "File must be an image.", status: 400, details: "File must be an image." });
  });

  test("do not turn a proxy's HTML error page into the message", async () => {
    const server = recordingServer({ "POST /api/videos/4": () => new Response("<html><body><h1>502 Bad Gateway</h1></body></html>", { status: 502, headers: { "content-type": "text/html" } }) });
    const result = await cli(server.url, ["api", "post", "videos/4", "--data", "{}", "--json"]);
    expect(JSON.parse(result.stderr).error).toEqual({ code: "INVALID_RESPONSE", message: "Cove returned a non-JSON response (502).", status: 502 });
  });

  test("keep a plain-text error reason", async () => {
    const server = recordingServer({ "POST /api/videos/4/image": () => new Response("File must be an image.", { status: 400, headers: { "content-type": "text/plain" } }) });
    const result = await cli(server.url, ["api", "post", "videos/4/image", "--data", "{}", "--json"]);
    expect(result.exitCode).toBe(1);
    expect(JSON.parse(result.stderr).error).toEqual({ code: "HTTP_400", message: "File must be an image.", status: 400 });
  });

  test("require confirmation for DELETE and reject conflicting bodies before sending anything", async () => {
    const server = recordingServer({});
    const unconfirmed = await cli(server.url, ["api", "delete", "videos/4/image", "--json"]);
    expect(unconfirmed.exitCode).toBe(2);
    expect(JSON.parse(unconfirmed.stderr).error.message).toContain("--yes");
    const conflicting = await cli(server.url, ["api", "post", "videos/4", "--data", "{}", "--file", "x", "--json"]);
    expect(JSON.parse(conflicting.stderr).error.message).toBe("Pass --data or --file, not both.");
    const invalid = await cli(server.url, ["api", "post", "videos/4", "--data", "{", "--json"]);
    expect(JSON.parse(invalid.stderr).error.message).toBe("--data must be valid JSON.");
    expect(server.requests).toEqual([]);
  });
});
