import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { performers, savedFilters, segmentLibrary } from "../api/client";
import { TagDetailPage } from "../pages/TagDetailPage";

const mocks = vi.hoisted(() => ({
  detailListOptions: [] as Array<Record<string, any>>,
}));

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
  performers: { findFiltered: vi.fn().mockResolvedValue({ items: [], totalCount: 0 }) },
  images: {},
  galleries: {},
  audios: {},
  texts: {},
  segmentLibrary: {
    distinctKinds: vi.fn().mockResolvedValue([]),
    distinctSourceKeys: vi.fn().mockResolvedValue([]),
    list: vi.fn().mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 }),
  },
  studios: {},
  groups: {},
}));

vi.mock("../hooks/useDetailListQuery", () => ({
  useDetailListQuery: (options: Record<string, any>) => {
    mocks.detailListOptions.push(options);
    return {
      data: { items: [], totalCount: 0, page: 1, perPage: 24 },
      isLoading: false,
      infinitePageSize: false,
      infiniteQuery: { hasNextPage: false, isFetchingNextPage: false },
      infiniteFilterKey: "test",
      fetchAllIds: vi.fn().mockResolvedValue([]),
      loadMore: vi.fn(),
    };
  },
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
  beforeEach(() => {
    mocks.detailListOptions.length = 0;
    localStorage.clear();
    window.history.replaceState(null, "", "/tag/1206?perPage=100&sort=rating");
  });

  it("persists include sub-tag content in the URL", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { unmount } = render(
      <QueryClientProvider client={queryClient}>
        <TagDetailPage id={1206} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    await user.click(await screen.findByRole("checkbox", { name: "Include sub-tag content" }));

    await waitFor(() => {
      const params = new URLSearchParams(window.location.search);
      expect(params.get("includeSubTags")).toBe("true");
      expect(params.get("perPage")).toBe("100");
      expect(params.get("sort")).toBe("rating");
    });

    unmount();
    render(
      <QueryClientProvider client={queryClient}>
        <TagDetailPage id={1206} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(await screen.findByRole("checkbox", { name: "Include sub-tag content" })).toBeChecked();
  });

  it("offers saved filters for every supported relation tab", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <TagDetailPage id={1206} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    const tabs = [
      ["Videos", "videos"],
      ["Performers", "performers"],
      ["Images", "images"],
      ["Galleries", "galleries"],
      ["Audios", "audios"],
      ["Texts", "texts"],
      ["Segments", "rawsegments"],
      ["Studios", "studios"],
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

  it("restores object criteria while retaining the current tag constraint", async () => {
    localStorage.setItem("cove-default-filter-performers", JSON.stringify({
      findFilter: { page: 3, perPage: 40, sort: "name", direction: "asc" },
      objectFilter: {
        favorite: true,
        tagsCriterion: { value: [999, 1000], modifier: "INCLUDES" },
      },
      uiOptions: { displayMode: "list" },
    }));
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <TagDetailPage id={1206} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );
    await user.click(await screen.findByRole("tab", { name: /^Performers/ }));

    await waitFor(() => expect(mocks.detailListOptions.some((options) =>
      options.queryKey?.[0] === "tag-performers" && options.queryKey?.[2]?.favorite === true,
    )).toBe(true));
    const options = [...mocks.detailListOptions].reverse().find((item) =>
      item.queryKey?.[0] === "tag-performers" && item.queryKey?.[2]?.favorite === true,
    );
    await options?.queryFn(options.filter);

    expect(performers.findFiltered).toHaveBeenCalledWith({
      findFilter: expect.objectContaining({ page: 1, perPage: 40, sort: "name", direction: "asc" }),
      objectFilter: expect.objectContaining({
        favorite: true,
        tagsCriterion: { value: [999, 1000], modifier: "INCLUDES", requiredIds: [1206] },
      }),
    });
    expect(screen.getByTitle("List")).toHaveClass("text-accent");
  });

  it("applies raw-segment object criteria while retaining the current tag constraint", async () => {
    localStorage.setItem("cove-default-filter-rawsegments", JSON.stringify({
      findFilter: { page: 1, perPage: 40, sort: "confidence", direction: "desc" },
      objectFilter: {
        rawKindCriterion: { value: "face", modifier: "EQUALS" },
        rawConfidenceCriterion: { value: 0.8, modifier: "GREATER_THAN" },
        rawPerformersCriterion: { value: [44], modifier: "INCLUDES" },
      },
    }));
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <TagDetailPage id={1206} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );
    await user.click(await screen.findByRole("tab", { name: /^Segments/ }));

    await waitFor(() => expect(mocks.detailListOptions.some((options) =>
      options.queryKey?.[0] === "tag-segments" && options.queryKey?.[2]?.rawKindCriterion?.value === "face",
    )).toBe(true));
    const options = [...mocks.detailListOptions].reverse().find((item) =>
      item.queryKey?.[0] === "tag-segments" && item.queryKey?.[2]?.rawKindCriterion?.value === "face",
    );
    await options?.queryFn(options.filter);

    expect(segmentLibrary.list).toHaveBeenCalledWith(expect.objectContaining({
      tagId: 1206,
      kind: "face",
      performerIds: "44",
      confidence: 0.8,
      confidenceModifier: "GREATER_THAN",
    }));
  });
});
