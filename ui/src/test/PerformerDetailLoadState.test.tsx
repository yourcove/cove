import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { PerformerDetailPage } from "../pages/PerformerDetailPage";

const { mockPerformers, mockPageState } = vi.hoisted(() => ({
  mockPerformers: {
    get: vi.fn(),
  },
  mockPageState: {
    activeTab: "extension-test",
    setFavorite: vi.fn(),
  },
}));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    performers: { ...actual.performers, ...mockPerformers },
    savedFilters: {
      list: vi.fn().mockResolvedValue([]),
      create: vi.fn(),
      delete: vi.fn(),
    },
  };
});

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    user: { kind: "user" },
    hasPermission: () => true,
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: {} } }),
  useOptionalAppConfig: () => ({ config: { ui: {} } }),
}));

vi.mock("../components/useExtensionTabs", () => ({
  useExtensionTabs: (_entityType: string, tabs: unknown[]) => ({
    allTabs: tabs,
    renderExtensionTab: () => null,
    extensionCounts: [],
  }),
}));

vi.mock("../hooks/useDetailListUrlState", () => ({
  useDetailTabUrlState: () => ({ activeTab: mockPageState.activeTab, setActiveTab: vi.fn() }),
  useRelatedDetailListUrlState: () => ({
    filter: {},
    setFilter: vi.fn(),
    objectFilter: {},
    setObjectFilter: vi.fn(),
    displayMode: "grid",
    setDisplayMode: vi.fn(),
    availableDisplayModes: ["grid"],
  }),
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

vi.mock("../hooks/useResolvedKeybindingOverrides", () => ({
  useResolvedKeybindingOverrides: () => ({}),
}));

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    favorite: false,
    rating: undefined,
    setFavorite: mockPageState.setFavorite,
    setRating: vi.fn(),
  }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({ backLabel: "Back", goBack: vi.fn() }),
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => undefined,
}));

vi.mock("../router/RouteRegistry", () => ({
  ExtensionSlot: () => null,
}));

function buildPerformer() {
  return {
    id: 17,
    name: "Recovered performer",
    aliases: [],
    urls: [],
    remoteIds: [],
    tags: [],
    customFields: {},
    fieldProvenance: [],
    videoCount: 0,
    galleryCount: 0,
    imageCount: 0,
    audioCount: 0,
    textCount: 0,
    groupCount: 0,
    faceCount: 0,
    updatedAt: "2026-08-03T00:00:00Z",
  };
}

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  render(
    <QueryClientProvider client={queryClient}>
      <PerformerDetailPage id={17} onNavigate={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("PerformerDetailPage load state", () => {
  afterEach(() => {
    vi.clearAllMocks();
    mockPerformers.get.mockReset();
    mockPageState.activeTab = "extension-test";
  });

  it("shows a retryable load error and recovers", async () => {
    mockPerformers.get
      .mockRejectedValueOnce(new Error("API Error 502: upstream API Error 404"))
      .mockResolvedValueOnce(buildPerformer());

    renderPage();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load performer");
    expect(screen.queryByText("Performer not found")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("heading", { name: "Recovered performer" })).toBeInTheDocument();
  });

  it("keeps the not-found state for a genuine missing performer", async () => {
    mockPerformers.get.mockRejectedValue(new Error("API Error 404: Not Found"));

    renderPage();

    expect(await screen.findByText("Performer not found")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("shows a deceased performer's age beside the death date instead of the birth date", async () => {
    mockPerformers.get.mockResolvedValue({
      ...buildPerformer(),
      birthdate: "1994-08-23",
      deathDate: "2017-12-05",
    });

    renderPage();

    expect(await screen.findByText("1994-08-23")).toBeInTheDocument();
    expect(screen.getByText("2017-12-05 (age 23)")).toBeInTheDocument();
  });
});
