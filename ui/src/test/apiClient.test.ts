import { afterEach, describe, expect, it, vi } from "vitest";
import { extensions, groups } from "../api/client";

describe("api client", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("treats empty successful responses as void", async () => {
    const fetchMock = vi.fn(async () => new Response("", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    await expect(groups.addSubGroup(1, 2)).resolves.toBeUndefined();
    expect(fetchMock).toHaveBeenCalledWith("/api/groups/1/subgroups", expect.objectContaining({ method: "POST" }));
  });

  it("preserves zero page size for group item pages", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ items: [], totalCount: 0, page: 1, perPage: 0 }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    await groups.items.page(4, { page: 1, perPage: 0 });

    expect(fetchMock).toHaveBeenCalledWith("/api/groups/4/items/page?page=1&perPage=0", expect.objectContaining({ headers: expect.any(Headers) }));
  });

  it("uploads extension ZIPs as authenticated multipart form data without setting a boundary header", async () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({
      message: "installed",
      extensionId: "com.example.upload",
      version: "1.0.0",
      path: "/extensions/com.example.upload",
    }), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    const file = new File(["zip contents"], "extension.zip", { type: "application/zip" });

    await extensions.installFromZip(file, true);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/extensions/install-from-zip");
    expect(init?.method).toBe("POST");
    expect(new Headers(init?.headers).has("Content-Type")).toBe(false);
    const body = init?.body as FormData;
    expect(body.get("file")).toBe(file);
    expect(body.get("trustUnverified")).toBe("true");
  });
});
