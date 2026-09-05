import { renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { useExtensionTabs } from "../components/useExtensionTabs";

const { fetchCount, getTabsForPage } = vi.hoisted(() => ({
  fetchCount: vi.fn(),
  getTabsForPage: () => [{ key: "missing-videos", label: "Missing Videos", countEndpoint: "/counts/{entityId}" }],
}));
vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({ getTabsForPage, getExtensionRevision: () => 0, resolveComponent: () => null }),
}));
vi.mock("../state/serverAvailability", () => ({ serverAwareFetch: fetchCount }));

describe("extension counts", () => {
  it("removes a previous count when the next record has no measurement", async () => {
    fetchCount.mockResolvedValueOnce({ ok: true, json: async () => ({ count: 7 }) });
    const { result, rerender } = renderHook(({ id }) => useExtensionTabs("performer", [], id), {
      initialProps: { id: 1 },
    });
    await waitFor(() => expect(result.current.allTabs[0].count).toBe(7));
    fetchCount.mockResolvedValueOnce({ ok: true, json: async () => ({ count: null }) });
    rerender({ id: 2 });
    await waitFor(() => expect(result.current.allTabs[0].count).toBeUndefined());
    expect(result.current.extensionCounts).toEqual([]);
  });

  it("preserves a measured zero in the tab and summary", async () => {
    fetchCount.mockResolvedValueOnce({ ok: true, json: async () => ({ count: 0 }) });
    const { result } = renderHook(() => useExtensionTabs("performer", [], 1));
    await waitFor(() => expect(result.current.allTabs[0].count).toBe(0));
    expect(result.current.extensionCounts[0].count).toBe(0);
  });
});
