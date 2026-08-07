import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { PerformerDetailPage } from "../pages/PerformerDetailPage";

const { mockPerformers } = vi.hoisted(() => ({
  mockPerformers: {
    get: vi.fn(),
  },
}));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    performers: { ...actual.performers, ...mockPerformers },
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
  useDetailTabUrlState: () => ({ activeTab: "extension-test", setActiveTab: vi.fn() }),
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

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    favorite: false,
    rating: undefined,
    setFavorite: vi.fn(),
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
});
