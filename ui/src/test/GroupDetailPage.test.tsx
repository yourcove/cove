import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useState } from "react";
import { GroupDetailPage } from "../pages/GroupDetailPage";
import { sortSeededRandom } from "../utils/seededRandomSort";

const { mockGroups, mockVideos, mockGoBack } = vi.hoisted(() => ({
  mockGroups: {
    get: vi.fn(),
    find: vi.fn(),
    delete: vi.fn(),
    subGroups: vi.fn(),
    addSubGroup: vi.fn(),
    removeSubGroup: vi.fn(),
    reorderSubGroups: vi.fn(),
    containingGroups: vi.fn(),
    items: {
      page: vi.fn(),
      list: vi.fn(),
      delete: vi.fn(),
      reorder: vi.fn(),
      playbackManifest: vi.fn(),
    },
  },
  mockVideos: {
    find: vi.fn(),
  },
  mockGoBack: vi.fn(),
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => {},
}));

vi.mock("../api/client", () => ({
  groups: mockGroups,
  videos: mockVideos,
  entityImages: {
    groupFrontImageUrl: vi.fn(() => "/front.jpg"),
    uploadGroupFrontImage: vi.fn(),
    deleteGroupFrontImage: vi.fn(),
    groupBackImageUrl: vi.fn(() => "/back.jpg"),
    uploadGroupBackImage: vi.fn(),
    deleteGroupBackImage: vi.fn(),
  },
}));

vi.mock("../components/CompilationPlayer", () => ({
  CompilationPlayer: () => <div data-testid="compilation-player">Compilation Player</div>,
}));

vi.mock("../components/ConfirmDialog", () => ({
  ConfirmDialog: () => null,
}));

vi.mock("../pages/GroupEditModal", () => ({
  GroupEditModal: ({ open }: { open: boolean }) => open ? <div role="dialog" aria-label="Edit Group Modal" /> : null,
}));

vi.mock("../router/RouteRegistry", () => ({
  ExtensionSlot: () => null,
}));

vi.mock("../components/EntityCards", () => ({
  EntityTileFrame: ({ label, onClick, body, media }: { label: string; onClick: () => void; body: React.ReactNode; media: React.ReactNode }) => (
    <button type="button" aria-label={label} onClick={onClick}>{media}{body}</button>
  ),
  GroupTile: ({ group, onClick }: { group: { name: string }; onClick: () => void }) => (
    <button type="button" onClick={onClick}>{group.name}</button>
  ),
  VideoCard: ({ video, onClick }: { video: { title?: string; id: number }; onClick: () => void }) => (
    <button type="button" onClick={onClick}>{video.title || `Video #${video.id}`}</button>
  ),
}));

vi.mock("../components/QuickViewDialog", () => ({
  QuickViewDialog: () => null,
}));

vi.mock("../components/DetailListToolbar", () => ({
  DetailListToolbar: ({ sortOptions, filterMode, filterDefaultKey, filter, onFilterChange }: { sortOptions: Array<{ value: string; label: string }>; filterMode?: string; filterDefaultKey?: string; filter: Record<string, unknown>; onFilterChange: (filter: Record<string, unknown>) => void }) => (
    <MockDetailListToolbar sortOptions={sortOptions} filterMode={filterMode} filterDefaultKey={filterDefaultKey} filter={filter} onFilterChange={onFilterChange} />
  ),
  DetailListPagination: () => null,
}));

function MockDetailListToolbar({ sortOptions, filterMode, filterDefaultKey, filter, onFilterChange }: { sortOptions: Array<{ value: string; label: string }>; filterMode?: string; filterDefaultKey?: string; filter: Record<string, unknown>; onFilterChange: (filter: Record<string, unknown>) => void }) {
  const [mountedDefaultKey] = useState(filterDefaultKey);
  return (
    <div data-testid="group-item-sort-options" data-filter-mode={filterMode} data-filter-default-key={filterDefaultKey} data-mounted-default-key={mountedDefaultKey} data-sort={filter.sort} data-seed={filter.seed}>
      {sortOptions.map((option) => <span key={option.value}>{option.label}</span>)}
      {sortOptions.some((option) => option.value === "order") ? (
        <button type="button" onClick={() => onFilterChange({ ...filter, sort: "random", seed: 2468 })}>Use random group sort</button>
      ) : null}
    </div>
  );
}

vi.mock("../components/AspectRatingsPanel", () => ({
  AspectRatingsPanel: () => <div>Aspect ratings</div>,
}));

vi.mock("../components/Rating", () => ({
  InteractiveRating: () => <div>Rating</div>,
}));

vi.mock("../components/BulkSelectionActions", () => ({
  BulkSelectionActions: () => null,
}));

vi.mock("../components/useExtensionTabs", () => ({
  useExtensionTabs: (_pageType: string, tabs: Array<{ key: string; label: string }>) => ({
    allTabs: tabs,
    renderExtensionTab: () => null,
  }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({
    backLabel: "Back to groups",
    goBack: mockGoBack,
  }),
}));

vi.mock("../hooks/useMultiSelect", () => ({
  useMultiSelect: () => ({
    selectedIds: new Set<number>(),
    toggle: vi.fn(),
    selectAll: vi.fn(),
    selectNone: vi.fn(),
  }),
}));

vi.mock("../components/SortableList", () => ({
  SortableList: ({ items, renderItem }: { items: any[]; renderItem: (item: any, state: any) => React.ReactNode }) => (
    <div>
      {items.map((item, index) => (
        <div key={item.id}>{renderItem(item, { dragHandleProps: { role: "button", "aria-label": "Pick up item to reorder" }, index, isDragging: false, isOver: false })}</div>
      ))}
    </div>
  ),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    hasPermission: () => true,
  }),
}));

function buildGroup(overrides: Record<string, unknown> = {}) {
  return {
    id: 4,
    name: "Summer Compilation",
    aliases: "Summer Mix",
    date: "2026-05-01T00:00:00Z",
    director: "Alex Doe",
    duration: 3600,
    studioId: 11,
    studioName: "Cove Studio",
    description: "A curated compilation.",
    tags: [],
    urls: ["https://example.com/group/4"],
    customFields: {},
    frontImagePath: undefined,
    backImagePath: undefined,
    videoCount: 2,
    subGroupCount: 1,
    containingGroupCount: 3,
    createdAt: "2026-05-01T12:00:00Z",
    updatedAt: "2026-05-01T13:00:00Z",
    ...overrides,
  };
}

function renderPage(id = 4) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  const onNavigate = vi.fn();

  const rendered = render(
    <QueryClientProvider client={queryClient}>
      <GroupDetailPage id={id} onNavigate={onNavigate} />
    </QueryClientProvider>,
  );

  return {
    onNavigate,
    unmountPage: rendered.unmount,
    rerenderPage: (nextId: number) => rendered.rerender(
      <QueryClientProvider client={queryClient}>
        <GroupDetailPage id={nextId} onNavigate={onNavigate} />
      </QueryClientProvider>,
    ),
  };
}

describe("GroupDetailPage", () => {
  afterEach(() => {
    vi.clearAllMocks();
    window.history.replaceState(null, "", "/");
  });

  it("shows a retryable load error and recovers", async () => {
    mockGroups.get
      .mockRejectedValueOnce(new Error("API Error 502: upstream API Error 404"))
      .mockResolvedValueOnce(buildGroup());
    mockGroups.items.list.mockResolvedValue([]);
    mockGroups.items.page.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 40 });
    mockGroups.items.playbackManifest.mockResolvedValue({ items: [] });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);

    renderPage();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load group");
    expect(screen.queryByText("Group not found")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("heading", { name: "Summer Compilation" })).toBeInTheDocument();
  });

  it("keeps the not-found state for a genuine missing group", async () => {
    mockGroups.get.mockRejectedValue(new Error("API Error 404: Not Found"));
    mockGroups.items.playbackManifest.mockResolvedValue({ items: [] });

    renderPage();

    expect(await screen.findByText("Group not found")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("renders the shared hero layout with metadata above the tabs", async () => {
    mockGroups.get.mockResolvedValue(buildGroup());
    mockGroups.items.list.mockResolvedValue([
      { id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 },
    ]);
    mockGroups.items.page.mockResolvedValue({
      items: [{ id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 }],
      totalCount: 1,
      page: 1,
      perPage: 40,
    });
    mockGroups.items.playbackManifest.mockResolvedValue({
      items: [{ groupItemId: 21, videoId: 10, title: "Clip One", startSec: 1, endSec: 5, durationSec: 4 }],
    });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);

    renderPage();

    expect(await screen.findByRole("heading", { name: "Summer Compilation" })).toBeInTheDocument();
    const tablist = screen.getByRole("tablist", { name: "Detail tabs" });
    expect(tablist.parentElement?.parentElement?.parentElement).not.toHaveClass("max-w-7xl");
    expect(screen.getByRole("tab", { name: /^items$/i })).toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: /metadata/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: /^edit$/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId("compilation-player")).not.toBeInTheDocument();
    expect(screen.getByTitle("Standalone Compilation")).toBeInTheDocument();
    expect(screen.getByText("example.com")).toBeInTheDocument();
    expect(screen.getByText("Kind")).toBeInTheDocument();
    expect(screen.getByText("Static")).toBeInTheDocument();
  });

  it("offers random sorting for group items", async () => {
    mockGroups.get.mockResolvedValue(buildGroup());
    mockGroups.items.list.mockResolvedValue([
      { id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 },
    ]);
    mockGroups.items.page.mockResolvedValue({
      items: [{ id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 }],
      totalCount: 1,
      page: 1,
      perPage: 40,
    });
    mockGroups.items.playbackManifest.mockResolvedValue({ items: [] });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      const sortOptionLists = screen.getAllByTestId("group-item-sort-options");
      expect(sortOptionLists.some((options) => options.textContent?.includes("Item #") && options.textContent.includes("Random"))).toBe(true);
    });
  });

  it("only offers group item drag handles in list view", async () => {
    mockGroups.get.mockResolvedValue(buildGroup());
    mockGroups.items.list.mockResolvedValue([
      { id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 },
      { id: 22, orderIndex: 1, videoId: 11, title: "Clip Two", kind: "videoRange", startSec: 2, endSec: 6 },
    ]);
    mockGroups.items.page.mockResolvedValue({
      items: [
        { id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 },
        { id: 22, orderIndex: 1, videoId: 11, title: "Clip Two", kind: "videoRange", startSec: 2, endSec: 6 },
      ],
      totalCount: 2,
      page: 1,
      perPage: 40,
    });
    mockGroups.items.playbackManifest.mockResolvedValue({ items: [] });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);

    window.history.replaceState(null, "", "/group/4?view=grid");
    const grid = renderPage();
    await screen.findByRole("heading", { name: "Summer Compilation" });
    await screen.findByText("Clip One");
    expect(screen.queryByRole("button", { name: "Pick up item to reorder" })).not.toBeInTheDocument();
    grid.unmountPage();

    window.history.replaceState(null, "", "/group/4?view=list");
    renderPage();
    expect(await screen.findAllByRole("button", { name: "Pick up item to reorder" })).toHaveLength(2);
  });

  it("offers saved filters for group items", async () => {
    mockGroups.get.mockResolvedValue(buildGroup());
    mockGroups.items.list.mockResolvedValue([
      { id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 },
    ]);
    mockGroups.items.page.mockResolvedValue({
      items: [{ id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 }],
      totalCount: 1,
      page: 1,
      perPage: 40,
    });
    mockGroups.items.playbackManifest.mockResolvedValue({ items: [] });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      const toolbars = screen.getAllByTestId("group-item-sort-options");
      expect(toolbars.some((toolbar) => toolbar.textContent?.includes("Item #")
        && toolbar.dataset.filterMode === "groupitems"
        && toolbar.dataset.filterDefaultKey === "groupitems-4")).toBe(true);
    });
  });

  it("remounts group item filters so each group can apply its own default", async () => {
    mockGroups.get.mockImplementation(async (id: number) => buildGroup({ id }));
    mockGroups.items.list.mockResolvedValue([
      { id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 },
    ]);
    mockGroups.items.page.mockResolvedValue({
      items: [{ id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 }],
      totalCount: 1,
      page: 1,
      perPage: 40,
    });
    mockGroups.items.playbackManifest.mockResolvedValue({ items: [] });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);

    const { rerenderPage } = renderPage(4);
    await waitFor(() => expect(screen.getAllByTestId("group-item-sort-options").some((toolbar) => toolbar.dataset.mountedDefaultKey === "groupitems-4")).toBe(true));

    rerenderPage(5);

    await waitFor(() => expect(screen.getAllByTestId("group-item-sort-options").some((toolbar) => toolbar.dataset.mountedDefaultKey === "groupitems-5")).toBe(true));
  });

  it("keeps random group-item sorting stable for a seed and reshuffles for a new seed", () => {
    const ids = [1, 2, 3, 4, 5].map((id) => `item-${id}`);
    const orderForSeed = (seed: number) => sortSeededRandom(ids, (id) => id, seed);
    const firstOrder = orderForSeed(1);
    const repeatedOrder = orderForSeed(1);
    const reshuffledOrder = orderForSeed(2);

    expect(repeatedOrder).toEqual(firstOrder);
    expect(reshuffledOrder).not.toEqual(firstOrder);
  });

  it("opens standalone compilation in the full group-item list order", async () => {
    const items = [
      { id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 },
      { id: 22, orderIndex: 1, videoId: 11, title: "Clip Two", kind: "videoRange", startSec: 1, endSec: 5 },
      { id: 23, orderIndex: 2, videoId: 12, title: "Clip Three", kind: "videoRange", startSec: 1, endSec: 5 },
    ];
    mockGroups.get.mockResolvedValue(buildGroup());
    mockGroups.items.list.mockResolvedValue(items);
    mockGroups.items.page.mockResolvedValue({ items, totalCount: items.length, page: 1, perPage: 40 });
    mockGroups.items.playbackManifest.mockResolvedValue({
      items: items.map((item) => ({ groupItemId: item.id, videoId: item.videoId, title: item.title, startSec: 1, endSec: 5, durationSec: 4 })),
    });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);

    const { onNavigate } = renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Use random group sort" }));
    fireEvent.click(screen.getByTitle("Standalone Compilation"));

    const expectedOrder = sortSeededRandom(items, (item) => `item-${item.id}`, 2468).map((item) => `item:${item.id}`);
    await waitFor(() => expect(onNavigate).toHaveBeenCalledWith({
      page: "compilation",
      id: 4,
      compilationItemOrder: expectedOrder,
    }));
  });

  it("persists the group item random sort and seed in the URL", async () => {
    window.history.replaceState(null, "", "/group/4");
    mockGroups.get.mockResolvedValue(buildGroup());
    mockGroups.items.list.mockResolvedValue([
      { id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 },
    ]);
    mockGroups.items.page.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 40 });
    mockGroups.items.playbackManifest.mockResolvedValue({ items: [] });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);

    const { unmountPage } = renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Use random group sort" }));

    await waitFor(() => {
      expect(window.location.search).toContain("sort=random");
      expect(window.location.search).toContain("seed=2468");
    });

    unmountPage();
    renderPage();

    await waitFor(() => {
      const toolbars = screen.getAllByTestId("group-item-sort-options");
      expect(toolbars.some((toolbar) => toolbar.textContent?.includes("Item #")
        && toolbar.dataset.sort === "random"
        && toolbar.dataset.seed === "2468")).toBe(true);
    });
  });

  it("opens the group editor from the hero action", async () => {
    mockGroups.get.mockResolvedValue(buildGroup());
    mockGroups.items.list.mockResolvedValue([
      { id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 },
    ]);
    mockGroups.items.page.mockResolvedValue({
      items: [{ id: 21, orderIndex: 0, videoId: 10, title: "Clip One", kind: "videoRange", startSec: 1, endSec: 5 }],
      totalCount: 1,
      page: 1,
      perPage: 40,
    });
    mockGroups.items.playbackManifest.mockResolvedValue({
      items: [{ groupItemId: 21, videoId: 10, title: "Clip One", startSec: 1, endSec: 5, durationSec: 4 }],
    });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);

    renderPage();

    await screen.findByRole("heading", { name: "Summer Compilation" });
    fireEvent.click(screen.getByTitle("Edit"));
    expect(await screen.findByRole("dialog", { name: "Edit Group Modal" })).toBeInTheDocument();
  });

  it("adds a subgroup from the search results", async () => {
    mockGroups.get.mockResolvedValue(buildGroup({ subGroupCount: 0 }));
    mockGroups.items.list.mockResolvedValue([]);
    mockGroups.items.page.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 40 });
    mockGroups.items.playbackManifest.mockResolvedValue({ items: [] });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });
    mockGroups.subGroups.mockResolvedValue([]);
    mockGroups.containingGroups.mockResolvedValue([]);
    mockGroups.find.mockResolvedValue({ items: [buildGroup({ id: 8, name: "Nested Group" })], totalCount: 1, page: 1, perPage: 20 });
    mockGroups.addSubGroup.mockResolvedValue(undefined);

    renderPage();

    fireEvent.click(await screen.findByTitle("More actions"));
    fireEvent.click(await screen.findByRole("menuitem", { name: /add sub-group/i }));
    fireEvent.change(screen.getByPlaceholderText("Search groups to add..."), { target: { value: "Nested" } });
    fireEvent.click(await screen.findByRole("button", { name: /nested group/i }));

    await waitFor(() => expect(mockGroups.addSubGroup).toHaveBeenCalledWith(4, 8));
  });
});
