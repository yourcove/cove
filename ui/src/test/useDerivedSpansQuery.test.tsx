import type { PropsWithChildren } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { segmentSpans } from "../api/client";
import { useDerivedSpansQuery } from "../pages/segments/useDerivedSpansQuery";

vi.mock("../api/client", () => ({
  segmentSpans: { search: vi.fn() },
}));

describe("useDerivedSpansQuery", () => {
  beforeEach(() => {
    vi.mocked(segmentSpans.search).mockReset().mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
  });

  it("refetches derived spans when the random seed changes", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: PropsWithChildren) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
    const options = {
      activeProfileId: 1,
      pageNumber: 1,
      perPage: 24,
      q: "",
      videoTitle: "",
      sort: "random",
      direction: "asc" as const,
      seed: 111,
      includeVideoIds: [],
      excludeVideoIds: [],
      appliedQuery: null,
      rawFilter: { tagIds: [], performerIds: [], faceIds: [] },
      enabled: true,
    };

    const { rerender } = renderHook(({ seed }) => useDerivedSpansQuery({ ...options, seed }), {
      initialProps: { seed: 111 },
      wrapper,
    });
    await waitFor(() => expect(segmentSpans.search).toHaveBeenCalledTimes(1));

    rerender({ seed: 222 });
    await waitFor(() => expect(segmentSpans.search).toHaveBeenCalledTimes(2));
    expect(segmentSpans.search).toHaveBeenLastCalledWith(expect.objectContaining({ sort: "random", seed: 222 }));
  });
});
