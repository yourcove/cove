import type { PropsWithChildren } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { segmentSpans } from "../api/client";
import { buildSpanSearchRequest, useDerivedSpansQuery } from "../pages/segments/useDerivedSpansQuery";
import { readMultiIdCriterionDepth } from "../pages/segments/segmentCriteriaDefinitions";

vi.mock("../api/client", () => ({
  segmentSpans: { search: vi.fn() },
}));

describe("useDerivedSpansQuery", () => {
  beforeEach(() => {
    vi.mocked(segmentSpans.search).mockReset().mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
  });

  it("preserves the sub-tag depth in derived span requests", () => {
    const tagDepth = readMultiIdCriterionDepth({ value: [94], modifier: "INCLUDES", depth: -1 });

    expect(
      buildSpanSearchRequest({
        activeProfileId: 1,
        pageNumber: 1,
        perPage: 24,
        q: "",
        videoTitle: "",
        videoTagIds: [95],
        videoTagDepth: -1,
        sort: "updated_at",
        direction: "desc",
        includeVideoIds: [],
        excludeVideoIds: [],
        appliedQuery: null,
        rawFilter: { tagIds: [94], tagDepth, performerIds: [], faceIds: [] },
      }),
    ).toEqual(expect.objectContaining({ tagIds: [94], tagDepth: -1, videoTagIds: [95], videoTagDepth: -1 }));
  });

  it("refetches derived spans when the random seed changes", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: PropsWithChildren) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );
    const options = {
      activeProfileId: 1,
      pageNumber: 1,
      perPage: 24,
      q: "",
      videoTitle: "",
      videoTagIds: [],
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
