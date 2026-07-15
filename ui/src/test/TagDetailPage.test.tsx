import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { TagDetailPage } from "../pages/TagDetailPage";

vi.mock("../api/client", () => ({
  tags: {
    get: vi.fn().mockResolvedValue({
      id: 1206,
      name: "Jerk Off Instruction",
      aliases: [],
      parents: [],
      children: [],
      videoCount: 1,
      performerCount: 0,
      imageCount: 0,
      galleryCount: 0,
      audioCount: 0,
      textCount: 0,
      segmentCount: 0,
      studioCount: 0,
      groupCount: 0,
      customFields: {},
    }),
  },
  entityImages: {
    tagImageUrl: vi.fn(() => "/tag-cover.jpg"),
  },
  savedFilters: {
    list: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    delete: vi.fn(),
  },
  videos: {},
  performers: {},
  images: {},
  galleries: {},
  audios: {},
  texts: {},
  segmentLibrary: {},
  studios: {},
  groups: {},
}));

vi.mock("../hooks/useDetailListQuery", () => ({
  useDetailListQuery: () => ({
    data: { items: [], totalCount: 0, page: 1, perPage: 24 },
    isLoading: false,
    infinitePageSize: false,
    infiniteQuery: { hasNextPage: false, isFetchingNextPage: false },
    infiniteFilterKey: "test",
    fetchAllIds: vi.fn().mockResolvedValue([]),
    loadMore: vi.fn(),
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: {} }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: { kind: "user" }, hasPermission: () => true }),
}));

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({ favorite: false, setFavorite: vi.fn(), rating: 0, setRating: vi.fn() }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({ backLabel: "Back to Tags", goBack: vi.fn() }),
}));

vi.mock("../components/useExtensionTabs", () => ({
  useExtensionTabs: (_entityType: string, tabs: unknown[]) => ({
    allTabs: tabs,
    renderExtensionTab: () => null,
    extensionCounts: [],
  }),
}));

vi.mock("../router/RouteRegistry", () => ({ ExtensionSlot: () => null }));
vi.mock("../hooks/useDocumentTitle", () => ({ useDocumentTitle: () => {} }));
vi.mock("../components/BulkSelectionActions", () => ({ BulkSelectionActions: () => null }));
vi.mock("../components/Rating", () => ({ InteractiveRating: () => null }));
vi.mock("../pages/TagEditModal", () => ({ TagEditModal: () => null }));
vi.mock("../components/ConfirmDialog", () => ({ ConfirmDialog: () => null }));
vi.mock("../components/DetailMergeDialog", () => ({ DetailMergeDialog: () => null }));
vi.mock("../components/MetadataTaggerDialog", () => ({ TagMetadataTaggerDialog: () => null }));
vi.mock("../components/CoverImageDialog", () => ({ CoverImageDialog: () => null }));

describe("TagDetailPage", () => {
  it("offers saved filters for the videos tab", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <TagDetailPage id={1206} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    await user.click(await screen.findByTitle("Saved filters"));

    expect(screen.getByText("Set current as default")).toBeInTheDocument();
    expect(screen.getByText("Save current filter")).toBeInTheDocument();
  });
});
