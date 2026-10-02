import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { segmentLibrary, segmentSpans } from "../api/client";
import { SegmentsPage } from "../pages/SegmentsPage";

type InfiniteOptions = { queryKey: unknown[]; queryFn: (page: number, perPage: number) => Promise<unknown> };

const infiniteQueryOptions = vi.hoisted(() => [] as InfiniteOptions[]);

vi.mock("../api/client", () => ({
  faces: { list: vi.fn().mockResolvedValue({ items: [] }) },
  videos: { get: vi.fn(), segments: { delete: vi.fn() } },
  segmentDisplayProfiles: { list: vi.fn().mockResolvedValue([]) },
  segmentLibrary: {
    distinctKinds: vi.fn().mockResolvedValue([]),
    distinctSourceKeys: vi.fn().mockResolvedValue([]),
    list: vi.fn().mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 }),
  },
  segmentSpans: {
    search: vi.fn().mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24, hasMore: false }),
  },
}));

vi.mock("../components/ListPage", () => ({
  ListPage: (props: Record<string, any>) => (
    <div data-testid="list-page">
      <button type="button" onClick={() => props.onApplySavedFilterUIOptions?.({ profileId: 33 })}>
        Apply test profile
      </button>
      <button type="button" onClick={() => props.onFilterChange({ ...props.filter, seed: 2222 })}>
        Reshuffle
      </button>
    </div>
  ),
}));

vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock("../hooks/usePaginatedInfiniteQuery", () => ({
  usePaginatedInfiniteQuery: (options: InfiniteOptions) => {
    infiniteQueryOptions.push(options);
    return {
      items: [],
      totalCount: 0,
      hasNextPage: false,
      isFetchingNextPage: false,
      fetchNextPage: vi.fn(),
      isLoading: false,
    };
  },
}));
vi.mock("../pages/segments/useDerivedSpansQuery", () => ({
  useDerivedSpansQuery: () => ({ data: { items: [], totalCount: 0 }, isLoading: false }),
  useDerivedSpansCountQuery: () => ({ data: { totalCount: 0, duration: 0 }, isLoading: false }),
}));
vi.mock("../pages/segments/useRawSegmentsQuery", () => ({
  useRawSegmentsQuery: () => ({ data: { items: [], totalCount: 0, duration: 3600 }, isLoading: false }),
}));
vi.mock("../pages/segments/SegmentsPageList", () => ({ SegmentsPageList: () => null }));
vi.mock("../components/AddToGroupDialog", () => ({ AddToGroupDialog: () => null }));
vi.mock("../components/ConfirmDialog", () => ({ ConfirmDialog: () => null }));

function latestInfiniteQuery(kind: "raw" | "search") {
  const options = infiniteQueryOptions.filter((entry) => entry.queryKey[1] === kind).at(-1);
  if (!options) throw new Error(`No ${kind} infinite query`);
  return options;
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <SegmentsPage onNavigate={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("SegmentsPage infinite random seed", () => {
  beforeEach(() => {
    infiniteQueryOptions.length = 0;
    vi.mocked(segmentLibrary.list).mockClear();
    vi.mocked(segmentSpans.search).mockClear();
    localStorage.clear();
  });

  it("sends the random seed with raw infinite pages", async () => {
    window.history.replaceState({}, "", "/segments?segmentsView=raw&perPage=infinite&sort=random&seed=1357");
    renderPage();

    await latestInfiniteQuery("raw").queryFn(2, 40);

    expect(segmentLibrary.list).toHaveBeenLastCalledWith(
      expect.objectContaining({ sort: "random", seed: 1357, page: 2, perPage: 40 }),
    );
  });

  it("fetches derived infinite pages with the current seed after a reshuffle", async () => {
    window.history.replaceState({}, "", "/segments?perPage=infinite&sort=random&seed=1111");
    renderPage();
    await userEvent.click(screen.getByRole("button", { name: "Apply test profile" }));

    await userEvent.click(screen.getByRole("button", { name: "Reshuffle" }));
    await waitFor(() => expect(latestInfiniteQuery("search").queryKey).toContain(2222));

    await latestInfiniteQuery("search").queryFn(1, 40);

    expect(segmentSpans.search).toHaveBeenLastCalledWith(expect.objectContaining({ profile: 33, seed: 2222 }));
  });
});
