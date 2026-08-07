import { afterEach, describe, expect, it, vi } from "vitest";
import { extensions, globalSearch, groups } from "../api/client";

describe("api client", () => {
  afterEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it("coordinates token refresh across independent browser pages", async () => {
    localStorage.setItem("cove_access_token", "expired-access-token");
    localStorage.setItem("cove_refresh_token", "refresh-token");

    let lockTail = Promise.resolve();
    const lockRequest = vi.fn(<T>(_name: string, callback: () => Promise<T>): Promise<T> => {
      const result = lockTail.then(callback);
      lockTail = result.then(() => undefined, () => undefined);
      return result;
    });
    vi.stubGlobal("navigator", { locks: { request: lockRequest } });

    let refreshRequests = 0;
    const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
      const url = String(input);
      if (url === "/api/auth/refresh") {
        refreshRequests += 1;
        return new Response(JSON.stringify({
          token: "new-access-token",
          refreshToken: "new-refresh-token",
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }

      const authorization = new Headers(init?.headers).get("Authorization");
      return new Response(null, {
        status: authorization === "Bearer new-access-token" ? 204 : 401,
      });
    });
    vi.stubGlobal("fetch", fetchMock);

    vi.resetModules();
    const firstPageClient = await import("../api/client");
    vi.resetModules();
    const secondPageClient = await import("../api/client");

    const responses = await Promise.all([
      firstPageClient.authedFetch("/api/first"),
      secondPageClient.authedFetch("/api/second"),
    ]);

    expect(responses.map(response => response.status)).toEqual([204, 204]);
    expect(refreshRequests).toBe(1);
    expect(lockRequest).toHaveBeenCalledTimes(2);
  });

  it("recovers when another page wins a refresh race before its tokens arrive", async () => {
    localStorage.setItem("cove_access_token", "expired-access-token");
    localStorage.setItem("cove_refresh_token", "stale-refresh-token");
    vi.stubGlobal("navigator", {});

    const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
      const url = String(input);
      if (url === "/api/auth/refresh") {
        setTimeout(() => {
          localStorage.setItem("cove_access_token", "new-access-token");
          localStorage.setItem("cove_refresh_token", "new-refresh-token");
        }, 0);
        return new Response(JSON.stringify({ code: "REFRESH_TOKEN_ROTATED" }), {
          status: 409,
          headers: { "Content-Type": "application/json" },
        });
      }

      const authorization = new Headers(init?.headers).get("Authorization");
      return new Response(null, {
        status: authorization === "Bearer new-access-token" ? 204 : 401,
      });
    });
    vi.stubGlobal("fetch", fetchMock);

    vi.resetModules();
    const { authedFetch } = await import("../api/client");

    const response = await authedFetch("/api/resource");

    expect(response.status).toBe(204);
    expect(localStorage.getItem("cove_refresh_token")).toBe("new-refresh-token");
  });

  it("falls back when the browser refresh lock fails", async () => {
    localStorage.setItem("cove_access_token", "expired-access-token");
    localStorage.setItem("cove_refresh_token", "refresh-token");
    vi.stubGlobal("navigator", {
      locks: { request: vi.fn().mockRejectedValue(new Error("lock unavailable")) },
    });

    const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
      if (String(input) === "/api/auth/refresh") {
        return new Response(JSON.stringify({
          token: "new-access-token",
          refreshToken: "new-refresh-token",
        }), { status: 200, headers: { "Content-Type": "application/json" } });
      }

      const authorization = new Headers(init?.headers).get("Authorization");
      return new Response(null, {
        status: authorization === "Bearer new-access-token" ? 204 : 401,
      });
    });
    vi.stubGlobal("fetch", fetchMock);

    vi.resetModules();
    const { authedFetch } = await import("../api/client");

    expect((await authedFetch("/api/resource")).status).toBe(204);
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

  it("forwards global-search limits and cancellation", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ groups: [], failedTypes: [] }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    const controller = new AbortController();

    await globalSearch.find("quick search", 8, controller.signal);

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/search/global?q=quick+search&perType=8",
      expect.objectContaining({ signal: controller.signal }),
    );
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
