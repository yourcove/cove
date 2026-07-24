import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import { useDetailListQuery } from "../hooks/useDetailListQuery";

function createWrapper(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  };
}

describe("useDetailListQuery", () => {
  it("keeps the current page visible while a changed filter loads", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    let resolveSearch!: (value: { items: { id: number }[]; totalCount: number; page: number; perPage: number }) => void;
    const searchResult = new Promise<{ items: { id: number }[]; totalCount: number; page: number; perPage: number }>((resolve) => {
      resolveSearch = resolve;
    });
    const queryFn = vi.fn()
      .mockResolvedValueOnce({ items: [{ id: 1 }], totalCount: 1, page: 1, perPage: 24 })
      .mockReturnValueOnce(searchResult);
    const { result, rerender } = renderHook(({ q }) => useDetailListQuery({
      queryKey: ["related-items-search"],
      filter: { page: 1, perPage: 24, q },
      queryFn,
    }), {
      initialProps: { q: "" },
      wrapper: createWrapper(queryClient),
    });

    await waitFor(() => expect(result.current.data.items).toEqual([{ id: 1 }]));

    rerender({ q: "friend" });

    expect(result.current.isLoading).toBe(false);
    expect(result.current.data.items).toEqual([{ id: 1 }]);

    resolveSearch({ items: [{ id: 2 }], totalCount: 1, page: 1, perPage: 24 });
    await waitFor(() => expect(result.current.data.items).toEqual([{ id: 2 }]));
  });

  it("exposes an initial load failure and can retry it", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const queryFn = vi.fn()
      .mockRejectedValueOnce(new Error("API Error 502: Bad Gateway"))
      .mockResolvedValue({ items: [{ id: 1 }], totalCount: 1, page: 1, perPage: 24 });

    const { result } = renderHook(() => useDetailListQuery({
      queryKey: ["related-items"],
      filter: { page: 1, perPage: 24 },
      queryFn,
    }), { wrapper: createWrapper(queryClient) });

    await waitFor(() => expect(result.current.loadError?.message).toBe("API Error 502: Bad Gateway"));
    expect(result.current.data.items).toEqual([]);

    await act(async () => { await result.current.retry(); });

    await waitFor(() => expect(result.current.loadError).toBeNull());
    expect(result.current.data.items).toEqual([{ id: 1 }]);
  });

  it("keeps retained data visible when a background refetch fails", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const queryFn = vi.fn()
      .mockResolvedValueOnce({ items: [{ id: 1 }], totalCount: 1, page: 1, perPage: 24 })
      .mockRejectedValueOnce(new Error("API Error 502: Bad Gateway"));

    const { result } = renderHook(() => useDetailListQuery({
      queryKey: ["related-items-retained"],
      filter: { page: 1, perPage: 24 },
      queryFn,
    }), { wrapper: createWrapper(queryClient) });

    await waitFor(() => expect(result.current.data.items).toEqual([{ id: 1 }]));
    await act(async () => { await result.current.retry(); });

    expect(result.current.loadError).toBeNull();
    expect(result.current.data.items).toEqual([{ id: 1 }]);
  });

  it("retains a successful empty page when a background refetch fails", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const queryFn = vi.fn()
      .mockResolvedValueOnce({ items: [], totalCount: 0, page: 1, perPage: 24 })
      .mockRejectedValueOnce(new Error("API Error 502: Bad Gateway"));

    const { result } = renderHook(() => useDetailListQuery({
      queryKey: ["related-items-retained-empty"],
      filter: { page: 1, perPage: 24 },
      queryFn,
    }), { wrapper: createWrapper(queryClient) });

    await waitFor(() => expect(queryFn).toHaveBeenCalledOnce());
    await act(async () => { await result.current.retry(); });

    expect(result.current.loadError).toBeNull();
    expect(result.current.data.items).toEqual([]);
  });

  it("exposes and retries an initial infinite-list failure", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const queryFn = vi.fn()
      .mockRejectedValueOnce(new Error("API Error 502: Bad Gateway"))
      .mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 60 });

    const { result } = renderHook(() => useDetailListQuery({
      queryKey: ["related-items-infinite"],
      filter: { page: 1, perPage: 0 },
      queryFn,
    }), { wrapper: createWrapper(queryClient) });

    await waitFor(() => expect(result.current.loadError?.message).toBe("API Error 502: Bad Gateway"));
    await act(async () => { await result.current.retry(); });
    await waitFor(() => expect(result.current.loadError).toBeNull());
    expect(result.current.data.items).toEqual([]);
  });

  it("retains a successful empty infinite result when a refetch fails", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const queryFn = vi.fn()
      .mockResolvedValueOnce({ items: [], totalCount: 0, page: 1, perPage: 60 })
      .mockRejectedValueOnce(new Error("API Error 502: Bad Gateway"));

    const { result } = renderHook(() => useDetailListQuery({
      queryKey: ["related-items-infinite-empty"],
      filter: { page: 1, perPage: 0 },
      queryFn,
    }), { wrapper: createWrapper(queryClient) });

    await waitFor(() => expect(queryFn).toHaveBeenCalledOnce());
    await act(async () => { await result.current.retry(); });

    expect(result.current.loadError).toBeNull();
    expect(result.current.data.items).toEqual([]);
  });
});
