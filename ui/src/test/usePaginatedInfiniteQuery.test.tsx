import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { createElement, type ReactNode } from "react";
import { describe, expect, it } from "vitest";
import { UNCOUNTED_TOTAL, type PaginatedResponse } from "../api/types";
import { usePaginatedInfiniteQuery } from "../hooks/usePaginatedInfiniteQuery";

type Item = { id: number };

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return createElement(QueryClientProvider, { client }, children);
}

// Serves `total` items in pages, reporting the total only when `counted`.
function pagedItems(total: number, counted: boolean) {
  return (page: number, perPage: number): Promise<PaginatedResponse<Item>> => {
    const start = (page - 1) * perPage;
    const items = Array.from({ length: Math.max(0, Math.min(perPage, total - start)) }, (_, index) => ({
      id: start + index + 1,
    }));
    return Promise.resolve({ items, totalCount: counted ? total : UNCOUNTED_TOTAL, page, perPage });
  };
}

describe("usePaginatedInfiniteQuery", () => {
  it("stops at the counted total", async () => {
    const { result } = renderHook(
      () => usePaginatedInfiniteQuery<Item>({ queryKey: ["counted"], queryFn: pagedItems(4, true), chunkSize: 4 }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.totalCount).toBe(4);
    expect(result.current.hasNextPage).toBe(false);
  });

  it("uses the known total for uncounted pages", async () => {
    const { result } = renderHook(
      () =>
        usePaginatedInfiniteQuery<Item>({
          queryKey: ["known"],
          queryFn: pagedItems(10, false),
          chunkSize: 4,
          knownTotalCount: 10,
        }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.totalCount).toBe(10);
    expect(result.current.loadedThroughCount).toBe(4);
    expect(result.current.hasNextPage).toBe(true);
  });

  it("stops at a known total even when the last page was full", async () => {
    const { result } = renderHook(
      () =>
        usePaginatedInfiniteQuery<Item>({
          queryKey: ["known-exact"],
          queryFn: pagedItems(4, false),
          chunkSize: 4,
          knownTotalCount: 4,
        }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.hasNextPage).toBe(false);
  });

  it("applies a total that arrives after the pages loaded", async () => {
    const { result, rerender } = renderHook(
      ({ knownTotalCount }: { knownTotalCount?: number }) =>
        usePaginatedInfiniteQuery<Item>({
          queryKey: ["known-late"],
          queryFn: pagedItems(4, false),
          chunkSize: 4,
          knownTotalCount,
        }),
      { wrapper, initialProps: {} as { knownTotalCount?: number } },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.hasNextPage).toBe(true);

    rerender({ knownTotalCount: 4 });

    await waitFor(() => expect(result.current.hasNextPage).toBe(false));
    expect(result.current.totalCount).toBe(4);
  });

  it("keeps loading full uncounted pages until the total is known", async () => {
    const { result } = renderHook(
      () =>
        usePaginatedInfiniteQuery<Item>({ queryKey: ["unknown-full"], queryFn: pagedItems(10, false), chunkSize: 4 }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.totalCount).toBe(4);
    expect(result.current.hasNextPage).toBe(true);
  });

  it("stops after a short uncounted page", async () => {
    const { result } = renderHook(
      () =>
        usePaginatedInfiniteQuery<Item>({ queryKey: ["unknown-short"], queryFn: pagedItems(3, false), chunkSize: 4 }),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.totalCount).toBe(3);
    expect(result.current.hasNextPage).toBe(false);
  });
});
