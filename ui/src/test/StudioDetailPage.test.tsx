import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { savedFilters } from "../api/client";
import { StudioDetailPage } from "../pages/StudioDetailPage";

const mocks = vi.hoisted(() => ({
  studioGet: vi.fn(),
  setFavorite: vi.fn(),
}));

vi.mock("../api/client", () => ({
  studios: {
    get: mocks.studioGet,
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

function buildStudio() {
  return {
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
    };
}

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
vi.mock("../hooks/useResolvedKeybindingOverrides", () => ({ useResolvedKeybindingOverrides: () => ({}) }));
vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: { kind: "user" }, hasPermission: () => true }),
}));
vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({ favorite: false, setFavorite: mocks.setFavorite, rating: 0, setRating: vi.fn() }),
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
vi.mock("../components/Rating", () => ({
  InteractiveRating: () => null,
  useRatingOptions: () => ({ type: "stars", starPrecision: "full" }),
}));
vi.mock("../pages/StudioEditModal", () => ({ StudioEditModal: () => null }));
vi.mock("../components/ConfirmDialog", () => ({ ConfirmDialog: () => null }));
vi.mock("../components/DetailMergeDialog", () => ({ DetailMergeDialog: () => null }));
vi.mock("../components/MetadataTaggerDialog", () => ({ StudioMetadataTaggerDialog: () => null }));
vi.mock("../components/CoverImageDialog", () => ({ CoverImageDialog: () => null }));

describe("StudioDetailPage", () => {
  beforeEach(() => {
    mocks.studioGet.mockReset().mockResolvedValue(buildStudio());
    mocks.setFavorite.mockReset();
    localStorage.clear();
    window.history.replaceState(null, "", "/studio/25?perPage=100&sort=rating&filters=%7B%22favorite%22%3Atrue%7D");
  });

  it("shows a retryable load error and recovers", async () => {
    mocks.studioGet
      .mockRejectedValueOnce(new Error("API Error 502: upstream API Error 404"))
      .mockResolvedValueOnce(buildStudio());
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <StudioDetailPage id={25} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load studio");
    expect(screen.queryByText("Studio not found")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("heading", { name: "Example Studio" })).toBeInTheDocument();
  });

  it("keeps the not-found state for a genuine missing studio", async () => {
    mocks.studioGet.mockRejectedValue(new Error("API Error 404: Not Found"));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <StudioDetailPage id={25} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(await screen.findByText("Studio not found")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("places the tabs first so the hero layout prevents doubled leading spacing", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <StudioDetailPage id={25} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    const tablist = await screen.findByRole("tablist", { name: "Detail tabs" });
    const tabs = tablist.parentElement?.parentElement;
    expect(tabs?.parentElement?.firstElementChild).toBe(tabs);
    expect(tabs).toHaveAttribute("data-entity-detail-tabs");
  });

  it("persists include sub-studio content in the URL", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { unmount } = render(
      <QueryClientProvider client={queryClient}>
        <StudioDetailPage id={25} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    await user.click(await screen.findByRole("checkbox", { name: "Include sub-studio content" }));

    await waitFor(() => {
      const params = new URLSearchParams(window.location.search);
      expect(params.get("includeSubStudios")).toBe("true");
      expect(params.get("perPage")).toBe("100");
      expect(params.get("sort")).toBe("rating");
      expect(params.get("filters")).toBe('{"favorite":true}');
    });

    unmount();
    render(
      <QueryClientProvider client={queryClient}>
        <StudioDetailPage id={25} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(await screen.findByRole("checkbox", { name: "Include sub-studio content" })).toBeChecked();
    expect(new URLSearchParams(window.location.search).get("includeSubStudios")).toBe("true");
  });

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
