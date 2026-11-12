import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Face } from "../api/types";
import { faces } from "../api/client";
import { useListedFaceNames } from "../pages/segments/useListedFaceNames";

vi.mock("../api/client", () => ({ faces: { findByIds: vi.fn() } }));

const findByIds = vi.mocked(faces.findByIds);

function face(id: number): Face {
  return {
    id,
    label: `face-${id}`,
    primarySourceKey: `face-${id}`,
    topSuggestion: { performerId: id, performerName: `Performer ${id}`, confidence: 90 },
  } as Face;
}

function wrapper(queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  return ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}

describe("useListedFaceNames", () => {
  beforeEach(() => {
    findByIds.mockReset();
    findByIds.mockImplementation(async (ids) => ({ items: ids.map(face), totalCount: ids.length }) as never);
  });

  it("looks faces up in batches of 48 and names them after their suggestions", async () => {
    const ids = Array.from({ length: 50 }, (_, index) => index + 1);
    const { result } = renderHook(() => useListedFaceNames([...ids, 1], true), { wrapper: wrapper() });

    await waitFor(() => expect(result.current.size).toBe(50));
    expect(result.current.get(50)).toEqual({ name: "Performer 50", suggested: true });
    expect(findByIds.mock.calls.map(([batch]) => batch.length)).toEqual([48, 2]);
  });

  it("keeps the names it showed while more listed faces load", async () => {
    let release: () => void = () => {};
    const { result, rerender } = renderHook(({ ids }) => useListedFaceNames(ids, true), {
      wrapper: wrapper(),
      initialProps: { ids: [1, 2] },
    });
    await waitFor(() => expect(result.current.size).toBe(2));

    findByIds.mockImplementationOnce(
      (ids) =>
        new Promise((resolve) => {
          release = () => resolve({ items: ids.map(face), totalCount: ids.length } as never);
        }),
    );
    rerender({ ids: [1, 2, 3] });
    expect(result.current.get(1)?.name).toBe("Performer 1");
    expect(result.current.has(3)).toBe(false);

    release();
    await waitFor(() => expect(result.current.get(3)?.name).toBe("Performer 3"));
  });

  it("asks for nothing without access to faces", () => {
    const { result } = renderHook(() => useListedFaceNames([1], false), { wrapper: wrapper() });

    expect(result.current.size).toBe(0);
    expect(findByIds).not.toHaveBeenCalled();
  });
});

describe("useListedFaceNames without fresh data", () => {
  beforeEach(() => {
    findByIds.mockReset();
    findByIds.mockImplementation(async (ids) => ({ items: ids.map(face), totalCount: ids.length }) as never);
  });

  it("hides names cached earlier once faces may no longer be read", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result, rerender } = renderHook(({ enabled }) => useListedFaceNames([1], enabled), {
      wrapper: wrapper(queryClient),
      initialProps: { enabled: true },
    });
    await waitFor(() => expect(result.current.size).toBe(1));

    rerender({ enabled: false });
    expect(result.current.size).toBe(0);
  });

  it("shows placeholders for a batch whose lookup failed", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { result, rerender } = renderHook(({ ids }) => useListedFaceNames(ids, true), {
      wrapper: wrapper(queryClient),
      initialProps: { ids: [1] },
    });
    await waitFor(() => expect(result.current.size).toBe(1));

    findByIds.mockRejectedValueOnce(new Error("lookup failed"));
    rerender({ ids: [1, 2] });
    await waitFor(() => expect(result.current.size).toBe(0));
  });
});
