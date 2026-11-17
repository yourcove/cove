import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { SegmentsPage } from "../pages/SegmentsPage";

const listState = vi.hoisted(() => ({ rawTotal: 5 }));

vi.mock("../api/client", () => ({
  faces: { list: vi.fn().mockResolvedValue({ items: [] }) },
  videos: { get: vi.fn(), segments: { delete: vi.fn() } },
  segmentDisplayProfiles: { list: vi.fn().mockResolvedValue([]) },
  segmentLibrary: {
    distinctKinds: vi.fn().mockResolvedValue([]),
    distinctSourceKeys: vi.fn().mockResolvedValue([]),
    list: vi.fn().mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 }),
    findFiltered: vi.fn().mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 }),
    aggregate: vi.fn().mockResolvedValue({ count: 0, duration: 0 }),
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
      {props.renderOperations?.()}
    </div>
  ),
}));

vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock("../hooks/usePaginatedInfiniteQuery", () => ({
  usePaginatedInfiniteQuery: () => ({
    items: [],
    totalCount: 0,
    hasNextPage: false,
    isFetchingNextPage: false,
    fetchNextPage: vi.fn(),
    isLoading: false,
  }),
}));
vi.mock("../pages/segments/useDerivedSpansQuery", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../pages/segments/useDerivedSpansQuery")>()),
  useDerivedSpansQuery: () => ({ data: { items: [], totalCount: 7, hasMore: false }, isLoading: false }),
  useDerivedSpansCountQuery: () => ({ data: { totalCount: 7, duration: 0 }, isLoading: false }),
}));
vi.mock("../pages/segments/useRawSegmentsQuery", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../pages/segments/useRawSegmentsQuery")>()),
  useRawSegmentsQuery: () => ({
    data: { items: [], totalCount: listState.rawTotal, duration: 0 },
    isLoading: false,
  }),
}));
vi.mock("../pages/segments/SegmentsPageList", () => ({ SegmentsPageList: () => null }));
vi.mock("../components/AddToGroupDialog", () => ({ AddToGroupDialog: () => null }));
vi.mock("../components/ConfirmDialog", () => ({ ConfirmDialog: () => null }));

function renderPage() {
  const onNavigate = vi.fn();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <SegmentsPage onNavigate={onNavigate} />
    </QueryClientProvider>,
  );
  return onNavigate;
}

describe("SegmentsPage Play", () => {
  beforeEach(() => {
    listState.rawTotal = 5;
    localStorage.clear();
  });

  it("plays the raw results with the request the page sends, seed included", async () => {
    window.history.replaceState({}, "", "/segments?segmentsView=raw&sort=random&seed=1357&q=kiss");
    const onNavigate = renderPage();

    await userEvent.click(screen.getByRole("button", { name: "Play" }));

    expect(onNavigate).toHaveBeenCalledWith({
      page: "segments-play",
      segmentsPlayback: {
        view: "raw",
        request: expect.objectContaining({
          findFilter: expect.objectContaining({ q: "kiss", sort: "random", seed: 1357 }),
        }),
        returnRoute: expect.objectContaining({ page: "segments", segmentsView: "raw" }),
      },
    });
  });

  it("plays the resolved results of the active profile", async () => {
    window.history.replaceState({}, "", "/segments?sort=random&seed=2468");
    const onNavigate = renderPage();
    await userEvent.click(screen.getByRole("button", { name: "Apply test profile" }));

    await userEvent.click(screen.getByRole("button", { name: "Play" }));

    expect(onNavigate).toHaveBeenCalledWith({
      page: "segments-play",
      segmentsPlayback: expect.objectContaining({
        view: "spans",
        request: expect.objectContaining({ profile: 33, sort: "random", seed: 2468 }),
      }),
    });
  });

  it("offers nothing to play when the results are empty", () => {
    listState.rawTotal = 0;
    window.history.replaceState({}, "", "/segments?segmentsView=raw");
    renderPage();

    expect(screen.getByRole("button", { name: "Play" })).toBeDisabled();
  });
});
