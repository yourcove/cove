import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
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
  DetailListToolbar: ({ sortOptions }: { sortOptions: Array<{ value: string; label: string }> }) => (
    <div data-testid="group-item-sort-options">
      {sortOptions.map((option) => <span key={option.value}>{option.label}</span>)}
    </div>
  ),
  DetailListPagination: () => null,
}));

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
        <div key={item.id}>{renderItem(item, { dragHandleProps: {}, index, isDragging: false, isOver: false })}</div>
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

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  const onNavigate = vi.fn();

  render(
    <QueryClientProvider client={queryClient}>
      <GroupDetailPage id={4} onNavigate={onNavigate} />
    </QueryClientProvider>,
  );

  return { onNavigate };
}

describe("GroupDetailPage", () => {
  afterEach(() => {
    vi.clearAllMocks();
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

  it("keeps random group-item sorting stable for a seed and reshuffles for a new seed", () => {
    const ids = [1, 2, 3, 4, 5].map((id) => `item-${id}`);
    const orderForSeed = (seed: number) => sortSeededRandom(ids, (id) => id, seed);
    const firstOrder = orderForSeed(1);
    const repeatedOrder = orderForSeed(1);
    const reshuffledOrder = orderForSeed(2);

    expect(repeatedOrder).toEqual(firstOrder);
    expect(reshuffledOrder).not.toEqual(firstOrder);
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
