import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { SegmentsPage } from "../pages/SegmentsPage";

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
  useDerivedSpansQuery: () => ({ data: { items: [], totalCount: 0 }, isLoading: false }),
  useDerivedSpansCountQuery: () => ({ data: 0 }),
}));
vi.mock("../pages/segments/useRawSegmentsQuery", () => ({
  useRawSegmentsQuery: () => ({ data: { items: [], totalCount: 0 }, isLoading: false }),
}));
vi.mock("../pages/segments/SegmentsPageList", () => ({ SegmentsPageList: () => null }));
vi.mock("../components/AddToGroupDialog", () => ({ AddToGroupDialog: () => null }));
vi.mock("../components/ConfirmDialog", () => ({ ConfirmDialog: () => null }));

describe("SegmentsPage saved-filter modes", () => {
  beforeEach(() => {
    localStorage.clear();
    window.history.replaceState({}, "", "/segments");
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
});
