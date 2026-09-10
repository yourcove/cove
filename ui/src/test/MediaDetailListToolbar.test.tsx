import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { MediaDetailListToolbar } from "../components/MediaDetailListToolbar";
import { audios, galleries, images, texts, videos } from "../api/client";

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    videos: { aggregate: vi.fn() },
    images: { aggregate: vi.fn() },
    galleries: { aggregate: vi.fn() },
    audios: { aggregate: vi.fn() },
    texts: { aggregate: vi.fn() },
    savedFilters: { list: vi.fn().mockResolvedValue([]) },
  };
});

vi.mock("../hooks/useRegisterKeyboardActionHandler", () => ({ useRegisterKeyboardActionHandler: vi.fn() }));

function setup(mediaType: "videos" | "images" | "galleries" | "audios" | "texts", selectedIds = new Set<number>()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  const props = {
    mediaType,
    selectedIds,
    selectedCount: selectedIds.size,
    aggregateObjectFilter: { performersCriterion: { requiredIds: [7], value: [], modifier: "INCLUDES" } },
    filter: { q: "example", page: 3, perPage: 20, sort: "title" },
    totalCount: 100,
    sortOptions: [],
    onFilterChange: vi.fn(),
    showSort: false,
  };
  const view = (next: typeof props) => (
    <QueryClientProvider client={client}>
      <MediaDetailListToolbar {...next} />
    </QueryClientProvider>
  );
  return { ...render(view(props)), view, props };
}

beforeEach(() => vi.resetAllMocks());

describe("MediaDetailListToolbar", () => {
  it.each([
    ["videos", videos, true],
    ["images", images, false],
    ["galleries", galleries, false],
    ["audios", audios, true],
    ["texts", texts, false],
  ] as const)(
    "shows filtered and selected %s totals in their respective toolbar rows",
    async (mediaType, api, hasDuration) => {
      vi.mocked(api.aggregate)
        .mockResolvedValueOnce({
          count: 100,
          fileSize: 1073741824,
          ...(hasDuration ? { duration: 3600 } : {}),
        } as never)
        .mockResolvedValueOnce({ count: 2, fileSize: 1048576, ...(hasDuration ? { duration: 60 } : {}) } as never);
      const { props } = setup(mediaType, new Set([99, 1]));
      const total = await screen.findByText(hasDuration ? "1h 0m · 1 GB" : "1 GB");
      expect(total.parentElement).toHaveTextContent("41–60 of 100");
      const selected = await screen.findByText(hasDuration ? "1m 0s · 1 MB" : "1 MB");
      expect(selected.parentElement).toHaveTextContent("2 selected");
      expect(api.aggregate).toHaveBeenNthCalledWith(1, {
        findFilter: { q: "example", page: 1, perPage: 0 },
        objectFilter: props.aggregateObjectFilter,
      });
      expect(api.aggregate).toHaveBeenNthCalledWith(
        2,
        mediaType === "videos" ? { objectFilter: { ids: [1, 99] } } : { ids: [1, 99] },
      );
    },
  );

  it("updates totals for search, parent filters and selection, without recalculating for pagination or sorting", async () => {
    vi.mocked(images.aggregate).mockResolvedValue({ count: 100, fileSize: 1024 });
    const { props, view, rerender } = setup("images");
    await screen.findByText("1 KB");
    expect(images.aggregate).toHaveBeenCalledTimes(1);
    rerender(view({ ...props, filter: { ...props.filter, page: 4, sort: "date" } }));
    expect(images.aggregate).toHaveBeenCalledTimes(1);
    rerender(view({ ...props, filter: { ...props.filter, q: "changed" } }));
    await waitFor(() => expect(images.aggregate).toHaveBeenCalledTimes(2));
    const next = {
      ...props,
      aggregateObjectFilter: { performersCriterion: { requiredIds: [8], value: [], modifier: "INCLUDES" } },
    };
    rerender(view(next));
    await waitFor(() => expect(images.aggregate).toHaveBeenCalledTimes(3));
    rerender(view({ ...next, selectedIds: new Set([150]), selectedCount: 1 }));
    await waitFor(() => expect(images.aggregate).toHaveBeenCalledWith({ ids: [150] }));
    rerender(view(next));
    expect(screen.queryByText("1 selected")).not.toBeInTheDocument();
    expect(images.aggregate).toHaveBeenCalledTimes(4);
  });

  it("shows zero totals and allows retry after an aggregate error", async () => {
    vi.mocked(images.aggregate)
      .mockRejectedValueOnce(new Error("unavailable"))
      .mockResolvedValueOnce({ count: 0, fileSize: 0 });
    setup("images");
    fireEvent.click(await screen.findByRole("button", { name: "Retry totals" }));
    expect(await screen.findByText("0 B")).toBeInTheDocument();
  });
});
