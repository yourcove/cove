import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import { useInfiniteListData } from "../hooks/useInfiniteListData";

function createWrapper(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  };
}

describe("useInfiniteListData", () => {
  it("uses a positive chunk size when the saved default page size is Infinite", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const queryPage = vi.fn().mockImplementation(async (filter) => ({
      items: [{ id: 1 }],
      totalCount: 1,
      page: filter.page,
      perPage: filter.perPage,
    }));

    const { result } = renderHook(
      () =>
        useInfiniteListData({
          queryKey: ["studios"],
          filter: { page: 1, perPage: 0 },
          chunkSize: 0,
          queryPage,
        }),
      { wrapper: createWrapper(queryClient) },
    );

    await waitFor(() => expect(result.current.items).toEqual([{ id: 1 }]));
    expect(queryPage).toHaveBeenCalledWith(expect.objectContaining({ page: 1, perPage: 40 }));
  });
});
