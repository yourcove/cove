import { afterEach, describe, expect, it, vi } from "vitest";
import { sharedModuleSpecifiers } from "../generated/extensions/runtime/v1/contract";
import { extensionFetch } from "../extensions/extension-api";

describe("extension API runtime", () => {
  afterEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    document.querySelectorAll("base").forEach((element) => element.remove());
    vi.unstubAllGlobals();
  });

  it("publishes the authenticated API module to extensions", () => {
    expect(sharedModuleSpecifiers).toContain("@cove/runtime/api");
  });

  it("authenticates extension endpoint requests with the active Cove token", async () => {
    localStorage.setItem("cove_access_token", "test-access-token");
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await extensionFetch("/api/plugins/example/status");

    const headers = new Headers(fetchMock.mock.calls[0][1]?.headers);
    expect(headers.get("Authorization")).toBe("Bearer test-access-token");
  });

  it("rejects cross-origin URLs before credentials can be sent", async () => {
    localStorage.setItem("cove_access_token", "test-access-token");
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await expect(extensionFetch("https://example.invalid/collect"))
      .rejects.toThrow("same-origin Cove API URL");

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("rejects same-origin URLs outside the Cove API", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await expect(extensionFetch("/settings"))
      .rejects.toThrow("same-origin Cove API URL");

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("normalizes relative API URLs independently of the document base", async () => {
    const base = document.createElement("base");
    base.href = "https://example.invalid/";
    document.head.append(base);
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await extensionFetch("api/plugins/example/status");

    expect(fetchMock.mock.calls[0][0]).toBe(`${window.location.origin}/api/plugins/example/status`);
  });

  it("rejects redirects even when the caller requests redirect following", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await extensionFetch("/api/plugins/example/status", { redirect: "follow" });

    expect(fetchMock.mock.calls[0][1]?.redirect).toBe("error");
  });

  it("forwards active share credentials to same-origin API requests", async () => {
    sessionStorage.setItem("cove_share_token", "share-token");
    sessionStorage.setItem("cove_share_password", "share-password");
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await extensionFetch("/api/plugins/example/status");

    const headers = new Headers(fetchMock.mock.calls[0][1]?.headers);
    expect(headers.get("X-Share-Token")).toBe("share-token");
    expect(headers.get("X-Share-Password")).toBe("share-password");
  });

  it("retries a same-origin API request with a refreshed bearer token", async () => {
    localStorage.setItem("cove_access_token", "expired-access-token");
    localStorage.setItem("cove_refresh_token", "refresh-token");
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        token: "new-access-token",
        refreshToken: "new-refresh-token",
      }), { status: 200, headers: { "Content-Type": "application/json" } }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    await extensionFetch("/api/plugins/example/status");

    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      `${window.location.origin}/api/plugins/example/status`,
      "/api/auth/refresh",
      `${window.location.origin}/api/plugins/example/status`,
    ]);
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).get("Authorization"))
      .toBe("Bearer expired-access-token");
    expect(new Headers(fetchMock.mock.calls[2][1]?.headers).get("Authorization"))
      .toBe("Bearer new-access-token");
  });
});
