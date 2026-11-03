import { render, renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { useExtensionTabs } from "../components/useExtensionTabs";

const { fetchCount, extensionTabs } = vi.hoisted(() => ({
  fetchCount: vi.fn(),
  extensionTabs: {
    current: [{ key: "missing-videos", label: "Missing Videos", countEndpoint: "/counts/{entityId}" }] as Record<
      string,
      unknown
    >[],
  },
}));
vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({
    getTabsForPage: () => extensionTabs.current,
    getExtensionRevision: () => 0,
    resolveComponent: () => null,
  }),
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

describe("extension tab icons", () => {
  it("gives each tab the icon it declares, Puzzle for an unknown name, and none when undeclared", async () => {
    const original = extensionTabs.current;
    extensionTabs.current = [
      { key: "cuts", label: "Cuts", icon: "scissors" },
      { key: "plain", label: "Plain" },
      { key: "typo", label: "Typo", icon: "not-an-icon" },
    ];
    try {
      const { result } = renderHook(() => useExtensionTabs("performer", []));
      const [cuts, plain, typo] = result.current.allTabs;
      expect(plain.icon).toBeUndefined();
      const { container } = render(
        <>
          <span data-testid="cuts">{cuts.icon}</span>
          <span data-testid="typo">{typo.icon}</span>
        </>,
      );
      // An unknown name still marks the tab as an extension one, as it does in the top navigation.
      expect(container.querySelector("[data-testid=typo] .lucide-puzzle")).not.toBeNull();
      await waitFor(() => expect(container.querySelector("[data-testid=cuts] .lucide-scissors")).not.toBeNull());
    } finally {
      extensionTabs.current = original;
    }
  });
});
