import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { SegmentsPage } from "../pages/SegmentsPage";

const useRawSegmentsQueryMock = vi.hoisted(() => vi.fn(() => ({ data: { items: [], totalCount: 0, duration: 3600 }, isLoading: false })));
const useDerivedSpansQueryMock = vi.hoisted(() => vi.fn(() => ({ data: { items: [], totalCount: 0 }, isLoading: false })));

vi.mock("../api/client", () => ({
  faces: { list: vi.fn().mockResolvedValue({ items: [] }) },
  videos: { get: vi.fn(), segments: { delete: vi.fn() } },
  segmentDisplayProfiles: { list: vi.fn().mockResolvedValue([]) },
  segmentLibrary: {
    distinctKinds: vi.fn().mockResolvedValue([]),
    distinctSourceKeys: vi.fn().mockResolvedValue([]),
    list: vi.fn().mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 }),
  },
  segmentSpans: { search: vi.fn().mockResolvedValue({ items: [], totalCount: 0 }) },
}));

vi.mock("../components/ListPage", () => ({
  ListPage: (props: Record<string, any>) => (
    <div
      data-testid="list-page"
      data-filter-mode={props.filterMode}
      data-filter={JSON.stringify(props.filter)}
      data-object-filter={JSON.stringify(props.objectFilter)}
      data-display-mode={props.displayMode}
      data-profile-id={String(props.savedFilterUIOptions?.profileId ?? "")}
    >
      <button type="button" onClick={() => props.onApplySavedFilterUIOptions?.({ profileId: 33 })}>
        Apply test profile
      </button>
      {props.renderOperations?.()}
    </div>
  ),
}));

vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock("../hooks/useMultiSelect", () => ({
  useMultiSelect: () => ({
    selectedIds: new Set(),
    toggle: vi.fn(),
    selectAll: vi.fn(),
    selectIds: vi.fn(),
    selectNone: vi.fn(),
    invertSelection: vi.fn(),
  }),
}));
vi.mock("../hooks/usePaginatedInfiniteQuery", () => ({
  usePaginatedInfiniteQuery: () => ({
    items: [], totalCount: 0, hasNextPage: false, isFetchingNextPage: false,
    fetchNextPage: vi.fn(), isLoading: false,
  }),
}));
vi.mock("../pages/segments/useDerivedSpansQuery", () => ({
  useDerivedSpansQuery: useDerivedSpansQueryMock,
  useDerivedSpansCountQuery: () => ({ data: { totalCount: 0, duration: 0 }, isLoading: false }),
}));
vi.mock("../pages/segments/useRawSegmentsQuery", () => ({
  useRawSegmentsQuery: useRawSegmentsQueryMock,
}));
vi.mock("../pages/segments/SegmentsPageList", () => ({ SegmentsPageList: () => null }));
vi.mock("../components/AddToGroupDialog", () => ({ AddToGroupDialog: () => null }));
vi.mock("../components/ConfirmDialog", () => ({ ConfirmDialog: () => null }));

describe("SegmentsPage saved-filter modes", () => {
  beforeEach(() => {
    useRawSegmentsQueryMock.mockClear();
    useDerivedSpansQueryMock.mockClear();
    localStorage.clear();
    window.history.replaceState({}, "", "/segments");
  });

  it("forwards the active sort to the finite derived query", async () => {
    window.history.replaceState({}, "", "/segments?sort=span_duration&direction=asc&seed=2468&perPage=24");
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <SegmentsPage onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(useDerivedSpansQueryMock).toHaveBeenCalledWith(expect.objectContaining({
      sort: "span_duration",
      direction: "asc",
      seed: 2468,
    })));
  });

  it("requests a dedicated aggregate for a direct raw infinite view", async () => {
    window.history.replaceState({}, "", "/segments?segmentsView=raw&perPage=infinite");
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <SegmentsPage onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(useRawSegmentsQueryMock).toHaveBeenCalledWith(expect.objectContaining({
      enabled: true,
      includeAggregate: true,
      pageNumber: 1,
      perPage: 1,
    })));
  });

  it("keeps segment and raw-segment defaults in separate modes", async () => {
    localStorage.setItem("cove-default-filter-segments", JSON.stringify({
      findFilter: { page: 2, perPage: 40, sort: "title", direction: "asc", q: "spans" },
      objectFilter: { spanOnly: true },
      uiOptions: { displayMode: "list", profileId: 11 },
    }));
    localStorage.setItem("cove-default-filter-rawsegments", JSON.stringify({
      findFilter: { page: 4, perPage: 60, sort: "start_sec", direction: "desc", q: "raw" },
      objectFilter: { rawOnly: true },
      uiOptions: { displayMode: "grid", profileId: 22 },
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const user = userEvent.setup();

    render(
      <QueryClientProvider client={queryClient}>
        <SegmentsPage onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    const listPage = screen.getByTestId("list-page");
    expect(listPage).toHaveAttribute("data-filter-mode", "segments");
    expect(listPage).toHaveAttribute("data-filter", expect.stringContaining('"q":"spans"'));
    expect(listPage).toHaveAttribute("data-object-filter", '{"spanOnly":true}');
    expect(listPage).toHaveAttribute("data-display-mode", "list");
    expect(listPage).toHaveAttribute("data-profile-id", "11");

    await user.click(screen.getByRole("button", { name: "Raw segments" }));

    await waitFor(() => expect(listPage).toHaveAttribute("data-filter-mode", "rawsegments"));
    expect(listPage).toHaveAttribute("data-filter", expect.stringContaining('"q":"raw"'));
    expect(listPage).toHaveAttribute("data-object-filter", '{"rawOnly":true}');
    expect(listPage).toHaveAttribute("data-display-mode", "grid");
    expect(listPage).toHaveAttribute("data-profile-id", "22");
    expect(window.location.search).toContain("segmentsView=raw");
    expect(window.location.search).toContain("q=raw");
    expect(decodeURIComponent(window.location.search)).toContain('"rawOnly":true');

    await user.click(screen.getByRole("button", { name: "Segments" }));

    await waitFor(() => expect(listPage).toHaveAttribute("data-filter-mode", "segments"));
    expect(listPage).toHaveAttribute("data-filter", expect.stringContaining('"q":"spans"'));
    expect(listPage).toHaveAttribute("data-object-filter", '{"spanOnly":true}');
    expect(listPage).toHaveAttribute("data-display-mode", "list");
    expect(listPage).toHaveAttribute("data-profile-id", "11");
    expect(window.location.search).not.toContain("segmentsView=raw");
    expect(window.location.search).toContain("q=spans");
    expect(decodeURIComponent(window.location.search)).not.toContain("rawOnly");
  });

  it.each([
    { mode: "Segments", filterMode: "segments", segmentsView: undefined },
    { mode: "Raw segments", filterMode: "rawsegments", segmentsView: "raw" },
  ])("does not reset filters when clicking the active $mode mode", async ({ mode, filterMode, segmentsView }) => {
    localStorage.setItem(`cove-default-filter-${filterMode}`, JSON.stringify({
      findFilter: { page: 1, perPage: 24, sort: "updated_at", direction: "desc", q: "saved-default" },
      objectFilter: { savedDefault: true },
      uiOptions: { displayMode: "grid", profileId: 11 },
    }));
    const params = new URLSearchParams({
      q: "active-filter",
      perPage: "40",
      view: "list",
      filters: JSON.stringify({ currentCriterion: true }),
    });
    if (segmentsView) params.set("segmentsView", segmentsView);
    window.history.replaceState({}, "", `/segments?${params.toString()}`);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const user = userEvent.setup();

    render(
      <QueryClientProvider client={queryClient}>
        <SegmentsPage onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    const listPage = screen.getByTestId("list-page");
    await waitFor(() => expect(listPage).toHaveAttribute("data-filter", expect.stringContaining('"q":"active-filter"')));
    expect(listPage).toHaveAttribute("data-filter-mode", filterMode);
    expect(listPage).toHaveAttribute("data-filter", expect.stringContaining('"perPage":40'));
    expect(listPage).toHaveAttribute("data-object-filter", '{"currentCriterion":true}');
    expect(listPage).toHaveAttribute("data-display-mode", "list");
    await user.click(screen.getByRole("button", { name: "Apply test profile" }));
    expect(listPage).toHaveAttribute("data-profile-id", "33");
    const searchBeforeClick = window.location.search;

    await user.click(screen.getByRole("button", { name: mode }));

    expect(listPage).toHaveAttribute("data-filter", expect.stringContaining('"q":"active-filter"'));
    expect(listPage).toHaveAttribute("data-filter", expect.stringContaining('"perPage":40'));
    expect(listPage).toHaveAttribute("data-object-filter", '{"currentCriterion":true}');
    expect(listPage).toHaveAttribute("data-display-mode", "list");
    expect(listPage).toHaveAttribute("data-profile-id", "33");
    expect(window.location.search).toBe(searchBeforeClick);
  });
});
