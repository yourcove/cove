import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ImageDetailPage } from "../pages/ImageDetailPage";

const { mockImages, mockFaces, mockEntityImages, mockPlayback, mockSetFavorite, mockSetRating, mockGoBack } = vi.hoisted(() => ({
  mockImages: {
    get: vi.fn(),
    delete: vi.fn(),
    update: vi.fn(),
    incrementLike: vi.fn(),
    thumbnailUrl: vi.fn(() => "/thumb.jpg"),
    imageUrl: vi.fn(() => "/image.jpg"),
  },
  mockFaces: {
    imageFaces: vi.fn(),
  },
  mockEntityImages: {
    studioImageUrl: vi.fn((id: number) => `/studio-${id}.jpg`),
    tagImageUrl: vi.fn((id: number) => `/tag-${id}.jpg`),
  },
  mockPlayback: {
    recordIntervals: vi.fn(() => Promise.resolve()),
  },
  mockSetFavorite: vi.fn(),
  mockSetRating: vi.fn(),
  mockGoBack: vi.fn(),
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => {},
}));

vi.mock("../api/client", () => ({
  images: mockImages,
  faces: mockFaces,
  entityImages: mockEntityImages,
  playback: mockPlayback,
}));

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    engagement: { likeCount: 0, derivedLikeCount: 0, pageVisitCount: 0 },
    favorite: false,
    rating: 4,
    setFavorite: mockSetFavorite,
    setRating: mockSetRating,
    favoritePending: false,
  }),
}));

vi.mock("../components/ConfirmDialog", () => ({
  ConfirmDialog: () => null,
}));

vi.mock("../router/RouteRegistry", () => ({
  ExtensionSlot: () => null,
}));

vi.mock("../components/AspectRatingsPanel", () => ({
  AspectRatingsPanel: () => <div>Rating Breakdown</div>,
}));

vi.mock("../components/Rating", () => ({
  InteractiveRating: ({ value }: { value: number }) => <div>Rating {value}</div>,
}));

vi.mock("../components/ExtensionEntityActions", () => ({
  ExtensionEntityActions: () => <div>Extension Actions</div>,
}));

vi.mock("../pages/ImageEditModal", () => ({
  ImageEditPanel: () => <div>Edit Image Panel</div>,
}));

vi.mock("../components/cardNavigation", () => ({
  createRouteLinkProps: (_route: unknown, onClick: () => void) => ({ href: "#", onClick }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({
    backLabel: "Back to images",
    goBack: mockGoBack,
  }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    user: { kind: "user" },
    hasPermission: () => true,
  }),
}));

vi.mock("../utils/interactionTracking", () => ({
  createPlaybackSessionId: () => "test-session",
  trackInteraction: vi.fn(),
}));

function buildImage(overrides: Record<string, unknown> = {}) {
  return {
    id: 12,
    title: "Sunset Poster",
    date: "2026-05-01T00:00:00Z",
    studioId: 9,
    studioName: "Sunset Studio",
    photographer: "Riley Smith",
    likeCounter: 0,
    rating: 4,
    organized: false,
    details: "A beach sunset still.",
    performers: [{ id: 3, name: "Alex", imagePath: undefined }],
    tags: [{ id: 6, name: "Beach", provenance: undefined }],
    galleryCount: 0,
    galleryIds: [],
    galleries: [],
    groups: [],
    files: [{ id: 1, path: "C:/images/poster.jpg", width: 1920, height: 1080, format: "jpg", size: 1048576 }],
    urls: ["https://example.com/image/12"],
    customFields: {},
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
      <ImageDetailPage id={12} onNavigate={onNavigate} />
    </QueryClientProvider>,
  );

  return { onNavigate };
}

function getPrimaryDetailTabs() {
  const tablists = screen.getAllByRole("tablist", { name: /detail tabs/i });
  return tablists.find((tablist) => tablist.getAttribute("aria-orientation") === "vertical") ?? tablists[0];
}

describe("ImageDetailPage", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("renders the shared layout with faces and related content in details", async () => {
    mockImages.get.mockResolvedValue(buildImage());
    mockFaces.imageFaces.mockResolvedValue([
      {
      id: 33,
        label: "Beach Face",
        performerName: undefined,
        coverImageUrl: undefined,
        appearanceCount: 1,
        frameSampleCount: 1,
        videoCount: 1,
        imageCount: 1,
      },
    ]);

    renderPage();

    expect(await screen.findByRole("heading", { name: "Sunset Poster" })).toBeInTheDocument();
    expect(screen.getByTestId("media-detail-layout-media")).toBeInTheDocument();

    const tabs = getPrimaryDetailTabs();
    expect(within(tabs).queryByRole("tab", { name: /related/i })).not.toBeInTheDocument();

    fireEvent.click(within(tabs).getByRole("tab", { name: /faces/i }));
    await waitFor(() => expect(mockFaces.imageFaces).toHaveBeenCalledWith(12));
    await waitFor(() => expect(screen.getByText("Beach Face")).toBeInTheDocument());

    fireEvent.click(within(tabs).getByRole("tab", { name: /details/i }));
    expect(await screen.findByText("Alex")).toBeInTheDocument();
    expect(screen.getByText("Beach")).toBeInTheDocument();

    fireEvent.click(within(tabs).getByRole("tab", { name: /edit/i }));
    expect(await screen.findByText("Edit Image Panel")).toBeInTheDocument();
  });

  it("supports keyboard shortcuts for edit tab, lightbox, and likes count", async () => {
    mockImages.get.mockResolvedValue(buildImage());
    mockFaces.imageFaces.mockResolvedValue([]);
    mockImages.incrementLike.mockResolvedValue(undefined);

    renderPage();

    await screen.findByRole("heading", { name: "Sunset Poster" });

    fireEvent.keyDown(window, { key: "e" });
    expect(await screen.findByText("Edit Image Panel")).toBeInTheDocument();

    fireEvent.keyDown(window, { key: "f" });
    expect(await screen.findByRole("button", { name: "Close (Esc)" })).toBeInTheDocument();

    fireEvent.keyDown(window, { key: "l" });
    await waitFor(() => expect(mockImages.incrementLike).toHaveBeenCalledWith(12));
  });

  it("records a lightbox dwell interval when the shared lightbox closes", async () => {
    mockImages.get.mockResolvedValue(buildImage());
    mockFaces.imageFaces.mockResolvedValue([]);

    renderPage();

    await screen.findByRole("heading", { name: "Sunset Poster" });

    fireEvent.keyDown(window, { key: "f" });
    fireEvent.click(await screen.findByRole("button", { name: "Close (Esc)" }));

    await waitFor(() => expect(mockPlayback.recordIntervals).toHaveBeenCalledWith(expect.objectContaining({
      hostType: "image",
      hostId: 12,
      state: "ended",
      surface: "lightbox",
      scopeKey: "image:12:lightbox",
      intervals: [expect.objectContaining({ startSec: 0, endSec: expect.any(Number) })],
    })));
  });
});
