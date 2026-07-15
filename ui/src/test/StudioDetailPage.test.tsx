import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { savedFilters } from "../api/client";
import { StudioDetailPage } from "../pages/StudioDetailPage";

vi.mock("../api/client", () => ({
  studios: {
    get: vi.fn().mockResolvedValue({
      id: 25,
      name: "Example Studio",
      aliases: [],
      urls: [],
      tags: [],
      remoteIds: [],
      videoCount: 1,
      performerCount: 0,
      galleryCount: 0,
      imageCount: 0,
      audioCount: 0,
      textCount: 0,
      childStudioCount: 0,
      groupCount: 0,
      customFields: {},
    }),
  },
  entityImages: { studioImageUrl: vi.fn(() => "/studio-cover.jpg") },
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

vi.mock("../state/AppConfigContext", () => ({ useAppConfig: () => ({ config: {} }) }));
vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: { kind: "user" }, hasPermission: () => true }),
}));
vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({ favorite: false, setFavorite: vi.fn(), rating: 0, setRating: vi.fn() }),
}));
vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({ backLabel: "Back to Studios", goBack: vi.fn() }),
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
vi.mock("../pages/StudioEditModal", () => ({ StudioEditModal: () => null }));
vi.mock("../components/ConfirmDialog", () => ({ ConfirmDialog: () => null }));
vi.mock("../components/DetailMergeDialog", () => ({ DetailMergeDialog: () => null }));
vi.mock("../components/MetadataTaggerDialog", () => ({ StudioMetadataTaggerDialog: () => null }));
vi.mock("../components/CoverImageDialog", () => ({ CoverImageDialog: () => null }));

describe("StudioDetailPage", () => {
  beforeEach(() => localStorage.clear());

  it("offers the matching saved-filter library on every relation tab", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <StudioDetailPage id={25} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    const tabs = [
      ["Videos", "videos"],
      ["Performers", "performers"],
      ["Galleries", "galleries"],
      ["Images", "images"],
      ["Audios", "audios"],
      ["Texts", "texts"],
      ["Sub-studios", "studios"],
      ["Groups", "groups"],
    ] as const;

    for (const [tabName, mode] of tabs) {
      await user.click(await screen.findByRole("tab", { name: new RegExp(`^${tabName}`) }));
      await user.click(await screen.findByTitle("Saved filters"));

      expect(savedFilters.list).toHaveBeenCalledWith(mode);
      expect(screen.getByText("Set current as default")).toBeInTheDocument();
      expect(screen.getByText("Save current filter")).toBeInTheDocument();
    }
  });
});
