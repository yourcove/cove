import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { HomePage } from "../pages/HomePage";

const MAX_RANDOM_SORT_SEED = 2147483647;

const { mockHomePageContent, mocks } = vi.hoisted(() => {
  const emptyPage: { items: Record<string, unknown>[]; totalCount: number; page?: number; perPage?: number } = {
    items: [],
    totalCount: 0,
  };
  return {
    mockHomePageContent: {
      value: JSON.stringify([
        { type: "custom", mode: "videos", sortBy: "random", direction: "asc", header: "Random Videos" },
      ]),
    },
    mocks: {
      videosFind: vi.fn(async () => emptyPage),
      videosFindFiltered: vi.fn(async () => emptyPage),
      performersFind: vi.fn(async () => emptyPage),
      performersFindFiltered: vi.fn(async () => emptyPage),
      studiosFind: vi.fn(async () => emptyPage),
      studiosFindFiltered: vi.fn(async () => emptyPage),
      tagsFind: vi.fn(async () => emptyPage),
      tagsFindFiltered: vi.fn(async () => emptyPage),
      galleriesFind: vi.fn(async () => emptyPage),
      galleriesFindFiltered: vi.fn(async () => emptyPage),
      groupsFind: vi.fn(async () => emptyPage),
      groupsFindFiltered: vi.fn(async () => emptyPage),
      groupItemsList: vi.fn(async () => []),
      groupItemsPage: vi.fn(async () => ({ ...emptyPage, page: 1, perPage: 12 })),
      savedFiltersGet: vi.fn(),
    },
  };
});

vi.mock("../api/client", () => ({
  videos: {
    find: mocks.videosFind,
    findFiltered: mocks.videosFindFiltered,
    screenshotUrl: (id: number) => `/api/stream/video/${id}/screenshot`,
  },
  performers: { find: mocks.performersFind, findFiltered: mocks.performersFindFiltered },
  studios: { find: mocks.studiosFind, findFiltered: mocks.studiosFindFiltered },
  tags: { find: mocks.tagsFind, findFiltered: mocks.tagsFindFiltered },
  galleries: { find: mocks.galleriesFind, findFiltered: mocks.galleriesFindFiltered },
  groups: {
    find: mocks.groupsFind,
    findFiltered: mocks.groupsFindFiltered,
    items: { list: mocks.groupItemsList, page: mocks.groupItemsPage },
  },
  savedFilters: { get: mocks.savedFiltersGet },
}));

vi.mock("../hooks/useEntityEngagementBatch", () => ({
  useEntityEngagementBatch: () => ({ engagementById: new Map() }),
}));

vi.mock("../utils/userUiPreferences", () => ({
  readAuthenticatedUserHomePageContent: () => mockHomePageContent.value,
  updateAuthenticatedUserUiPreferences: vi.fn(),
}));

function renderHomePage(onNavigate = vi.fn()) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <HomePage onNavigate={onNavigate} />
    </QueryClientProvider>,
  );
}

function randomValueForSeed(seed: number) {
  return (seed + 0.5) / MAX_RANDOM_SORT_SEED;
}

describe("HomePage random rows", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    vi.clearAllMocks();
    localStorage.clear();
    mockHomePageContent.value = JSON.stringify([
      { type: "custom", mode: "videos", sortBy: "random", direction: "asc", header: "Random Videos" },
    ]);
    class ResizeObserverMock {
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    vi.stubGlobal("ResizeObserver", ResizeObserverMock);
  });

  it("adds a fresh seed to custom random front-page rows", async () => {
    const expectedSeed = 101;
    vi.spyOn(Math, "random").mockReturnValue(randomValueForSeed(expectedSeed));

    renderHomePage();

    await waitFor(() => {
      expect(mocks.videosFind).toHaveBeenCalledWith(expect.objectContaining({
        perPage: 25,
        sort: "random",
        direction: "asc",
        seed: expectedSeed,
      }));
    });
  });

  it("opens a premade row with its configured sort", async () => {
    const onNavigate = vi.fn();
    mockHomePageContent.value = JSON.stringify([
      { type: "custom", mode: "videos", sortBy: "created_at", direction: "desc", header: "Recently Added Videos" },
    ]);
    mocks.videosFind.mockResolvedValueOnce({ items: [{ id: 101, title: "Recent result" }], totalCount: 1 });

    renderHomePage(onNavigate);

    fireEvent.click(await screen.findByRole("button", { name: "View All" }));

    expect(onNavigate).toHaveBeenCalledWith({
      page: "videos",
      listFilter: { q: "", page: 1, sort: "created_at", direction: "desc" },
      listObjectFilter: {},
    });
  });

  it("requests only the Continue Watching items shown on the home page", async () => {
    const onNavigate = vi.fn();
    mockHomePageContent.value = JSON.stringify([{ type: "continueWatching" }]);
    mocks.groupsFind.mockResolvedValueOnce({
      items: [{ id: 3, querySourceKey: "continue-watching" }],
      totalCount: 1,
      page: 1,
      perPage: 100,
    });
    mocks.groupItemsPage.mockResolvedValueOnce({
      items: [{ id: 1, groupId: 3, hostType: "video", hostId: 10, videoId: 10, title: "Resume item" }],
      totalCount: 30,
      page: 1,
      perPage: 12,
    });

    renderHomePage(onNavigate);

    await waitFor(() => {
      expect(mocks.groupItemsPage).toHaveBeenCalledWith(3, { page: 1, perPage: 12 });
    });
    expect(mocks.groupItemsList).not.toHaveBeenCalled();
    expect(await screen.findByText("Resume item")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "View All" }));
    expect(onNavigate).toHaveBeenCalledWith({ page: "group", id: 3 });
  });

  it("generates a new custom row seed for each front-page mount", async () => {
    const firstSeed = 201;
    const secondSeed = 202;
    vi.spyOn(Math, "random")
      .mockReturnValueOnce(randomValueForSeed(firstSeed))
      .mockReturnValueOnce(randomValueForSeed(secondSeed));

    const first = renderHomePage();
    await waitFor(() => expect(mocks.videosFind).toHaveBeenCalled());
    first.unmount();

    renderHomePage();
    await waitFor(() => expect(mocks.videosFind).toHaveBeenCalledTimes(2));

    expect(mocks.videosFind).toHaveBeenNthCalledWith(1, expect.objectContaining({ seed: firstSeed }));
    expect(mocks.videosFind).toHaveBeenNthCalledWith(2, expect.objectContaining({ seed: secondSeed }));
  });

  it("adds a fresh seed to saved random front-page rows", async () => {
    const expectedSeed = 301;
    vi.spyOn(Math, "random").mockReturnValue(randomValueForSeed(expectedSeed));
    mockHomePageContent.value = JSON.stringify([{ type: "saved", savedFilterId: 7 }]);
    mocks.savedFiltersGet.mockResolvedValue({
      id: 7,
      mode: "videos",
      name: "Saved Random Videos",
      findFilter: JSON.stringify({ sort: "random", direction: "desc" }),
    });

    renderHomePage();

    await waitFor(() => {
      expect(mocks.videosFind).toHaveBeenCalledWith(expect.objectContaining({
        page: 1,
        perPage: 25,
        sort: "random",
        direction: "desc",
        seed: expectedSeed,
      }));
    });
  });

  it("preserves the saved random row seed when opening View All", async () => {
    const onNavigate = vi.fn();
    const expectedSeed = 302;
    vi.spyOn(Math, "random").mockReturnValue(randomValueForSeed(expectedSeed));
    mockHomePageContent.value = JSON.stringify([{ type: "saved", savedFilterId: 7 }]);
    mocks.savedFiltersGet.mockResolvedValue({
      id: 7,
      mode: "videos",
      name: "Saved Random Videos",
      findFilter: JSON.stringify({ sort: "random", direction: "desc" }),
    });
    mocks.videosFind.mockResolvedValueOnce({ items: [{ id: 101, title: "Random result" }], totalCount: 1 });

    renderHomePage(onNavigate);

    fireEvent.click(await screen.findByRole("button", { name: "View All" }));

    expect(onNavigate).toHaveBeenCalledWith({
      page: "videos",
      listFilter: { q: "", page: 1, sort: "random", direction: "desc", seed: expectedSeed },
      listObjectFilter: {},
    });
  });

  it("keeps object filters when saved random tag rows are loaded", async () => {
    const expectedSeed = 401;
    vi.spyOn(Math, "random").mockReturnValue(randomValueForSeed(expectedSeed));
    mockHomePageContent.value = JSON.stringify([{ type: "saved", savedFilterId: 9 }]);
    mocks.savedFiltersGet.mockResolvedValue({
      id: 9,
      mode: "tags",
      name: "Saved Random Tags",
      findFilter: JSON.stringify({ sort: "random", direction: "asc" }),
      objectFilter: JSON.stringify({ aliasesCriterion: { modifier: "includes", value: "sample" } }),
    });

    renderHomePage();

    await waitFor(() => {
      expect(mocks.tagsFindFiltered).toHaveBeenCalledWith({
        findFilter: expect.objectContaining({
          page: 1,
          perPage: 25,
          sort: "random",
          direction: "asc",
          seed: expectedSeed,
        }),
        objectFilter: { aliasesCriterion: { modifier: "includes", value: "sample" } },
      });
    });
    expect(mocks.tagsFind).not.toHaveBeenCalled();
  });

  it("opens a saved-filter row with the saved filter state", async () => {
    const onNavigate = vi.fn();
    mockHomePageContent.value = JSON.stringify([{ type: "saved", savedFilterId: 7 }]);
    mocks.savedFiltersGet.mockResolvedValue({
      id: 7,
      mode: "videos",
      name: "Saved Videos",
      findFilter: JSON.stringify({ q: "favorite", page: 4, perPage: 60, sort: "rating", direction: "desc" }),
      objectFilter: JSON.stringify({ ratingCriterion: { modifier: "greater_than", value: 80 } }),
      uiOptions: JSON.stringify({ displayMode: "list" }),
    });
    mocks.videosFindFiltered.mockResolvedValueOnce({ items: [{ id: 101, title: "Filtered result" }], totalCount: 1 });

    renderHomePage(onNavigate);

    fireEvent.click(await screen.findByRole("button", { name: "View All" }));

    expect(onNavigate).toHaveBeenCalledWith({
      page: "videos",
      listFilter: { q: "favorite", page: 1, perPage: 60, sort: "rating", direction: "desc" },
      listObjectFilter: { ratingCriterion: { modifier: "greater_than", value: 80 } },
      listView: "list",
    });
  });

  it("preserves empty saved-filter state so destination defaults are cleared", async () => {
    const onNavigate = vi.fn();
    mockHomePageContent.value = JSON.stringify([{ type: "saved", savedFilterId: 8 }]);
    mocks.savedFiltersGet.mockResolvedValue({
      id: 8,
      mode: "videos",
      name: "Unfiltered Videos",
      findFilter: JSON.stringify({ perPage: 40 }),
    });
    mocks.videosFind.mockResolvedValueOnce({ items: [{ id: 102, title: "Unfiltered result" }], totalCount: 1 });

    renderHomePage(onNavigate);

    fireEvent.click(await screen.findByRole("button", { name: "View All" }));

    expect(onNavigate).toHaveBeenCalledWith({
      page: "videos",
      listFilter: { q: "", page: 1, perPage: 40, sort: "date", direction: "desc" },
      listObjectFilter: {},
    });
  });
});
