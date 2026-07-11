import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { GalleryDetailPage } from "../pages/GalleryDetailPage";

const {
  mockGalleries,
  mockImages,
  mockVideos,
  mockEntityImages,
  mockSetRating,
  mockGoBack,
} = vi.hoisted(() => ({
  mockGalleries: {
    get: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
    addImages: vi.fn(),
    removeImages: vi.fn(),
    coverUrl: vi.fn((id: number) => `/gallery-cover-${id}.jpg`),
    chapters: vi.fn(),
    createChapter: vi.fn(),
    updateChapter: vi.fn(),
    deleteChapter: vi.fn(),
  },
  mockImages: {
    find: vi.fn(),
    imageUrl: vi.fn((id: number) => `/image-${id}.jpg`),
    thumbnailUrl: vi.fn((id: number) => `/thumb-${id}.jpg`),
  },
  mockVideos: {
    find: vi.fn(),
  },
  mockEntityImages: {
    performerImageUrl: vi.fn((id: number) => `/performer-${id}.jpg`),
  },
  mockSetRating: vi.fn(),
  mockGoBack: vi.fn(),
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => {},
}));

vi.mock("../api/client", () => ({
  galleries: mockGalleries,
  images: mockImages,
  videos: mockVideos,
  entityImages: mockEntityImages,
  savedFilters: {
    list: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    delete: vi.fn(),
  },
}));

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    rating: 4,
    setRating: mockSetRating,
  }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({
    backLabel: "Back to galleries",
    goBack: mockGoBack,
  }),
}));

vi.mock("../components/useExtensionTabs", () => ({
  useExtensionTabs: (_entityType: string, tabs: unknown[]) => ({
    allTabs: tabs,
    renderExtensionTab: () => null,
  }),
}));

vi.mock("../router/RouteRegistry", () => ({
  ExtensionSlot: () => null,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    user: { kind: "user" },
    hasPermission: () => true,
  }),
}));

vi.mock("../components/ConfirmDialog", () => ({
  ConfirmDialog: () => null,
}));

vi.mock("../pages/GalleryEditModal", () => ({
  GalleryEditModal: ({ open }: { open: boolean }) => (open ? <div>Edit Gallery Modal</div> : null),
}));

vi.mock("../components/GalleryDownloadDialog", () => ({
  GalleryDownloadDialog: () => null,
}));

vi.mock("../components/Rating", () => ({
  InteractiveRating: ({ value }: { value: number }) => <div>Rating {value}</div>,
}));

vi.mock("../components/EntityCards", () => ({
  VideoCard: ({ video, onClick }: { video: { title: string }; onClick: () => void }) => (
    <button onClick={onClick}>{video.title}</button>
  ),
  ImageTile: ({ image, onClick }: { image: { title: string }; onClick: () => void }) => (
    <button onClick={onClick}>{image.title}</button>
  ),
  PerformerTile: ({ performer, onClick }: { performer: { name: string }; onClick: () => void }) => (
    <button onClick={onClick}>{performer.name}</button>
  ),
  PerformerBadgeRow: ({ performers }: { performers: Array<{ name: string }> }) => (
    <div>{performers.map((performer) => performer.name).join(", ")}</div>
  ),
}));

vi.mock("../components/QuickViewDialog", () => ({
  QuickViewDialog: () => null,
}));

vi.mock("../components/BulkSelectionActions", () => ({
  BulkSelectionActions: () => <div>Bulk Actions</div>,
}));

vi.mock("../hooks/useMultiSelect", () => ({
  useMultiSelect: () => ({
    selectedIds: new Set<number>(),
    toggle: vi.fn(),
    selectAll: vi.fn(),
    selectNone: vi.fn(),
  }),
}));

vi.mock("../components/cardNavigation", () => ({
  createRouteLinkProps: (_route: unknown, onClick: () => void) => ({ href: "#", onClick }),
}));

vi.mock("../components/Lightbox", () => ({
  Lightbox: ({ open }: { open: boolean }) => (open ? <div>Lightbox</div> : null),
}));

function buildGallery(overrides: Record<string, unknown> = {}) {
  return {
    id: 21,
    title: "Summer Set",
    date: "2026-05-01T00:00:00Z",
    studioId: 9,
    studioName: "Sunset Studio",
    photographer: "Riley Smith",
    code: "SUM-21",
    imageCount: 2,
    rating: 4,
    organized: true,
    details: "A bright summer gallery.",
    performers: [{ id: 5, name: "Alex", imagePath: undefined }],
    tags: [{ id: 8, name: "Beach", provenance: undefined }],
    urls: ["https://example.com/gallery/21"],
    customFields: {},
    createdAt: "2026-05-01T12:00:00Z",
    updatedAt: "2026-05-01T13:00:00Z",
    coverImageId: 91,
    folderPath: "C:/galleries/summer-set",
    files: [
      {
        id: 1,
        path: "C:/galleries/summer-set/gallery.zip",
        size: 2048,
        modTime: "2026-05-01T14:00:00Z",
        fingerprints: [{ type: "sha256", value: "abc123" }],
      },
    ],
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

  render(
    <QueryClientProvider client={queryClient}>
      <GalleryDetailPage id={21} onNavigate={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("GalleryDetailPage", () => {
  afterEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it("applies the saved gallery image page size", async () => {
    localStorage.setItem("cove-default-filter-gallery-images", JSON.stringify({
      findFilter: { sort: "path", direction: "asc", perPage: 20 },
    }));
    mockGalleries.get.mockResolvedValue(buildGallery());
    mockImages.find.mockResolvedValue({ items: [{ id: 91, title: "Cover Frame" }], totalCount: 78 });
    mockVideos.find.mockResolvedValue({ items: [], totalCount: 0 });

    renderPage();

    await waitFor(() => expect(screen.getByTitle("Items per page")).toHaveValue("20"));
  });

  it("applies the saved gallery video page size", async () => {
    localStorage.setItem("cove-default-filter-videos", JSON.stringify({
      findFilter: { sort: "date", direction: "desc", perPage: 20 },
    }));
    localStorage.setItem("cove-default-filter-gallery-videos", JSON.stringify({
      findFilter: { sort: "date", direction: "desc", perPage: 40 },
    }));
    mockGalleries.get.mockResolvedValue(buildGallery({ videoCount: 1 }));
    mockImages.find.mockResolvedValue({ items: [{ id: 91, title: "Cover Frame" }], totalCount: 1 });
    mockVideos.find.mockResolvedValue({ items: [{ id: 4, title: "Video One" }], totalCount: 1 });

    renderPage();
    fireEvent.click(await screen.findByRole("tab", { name: /videos/i }));

    await waitFor(() => expect(screen.getByTitle("Items per page")).toHaveValue("40"));
  });

  it("renders the shared layout with images, videos, and file info tabs", async () => {
    mockGalleries.get.mockResolvedValue(buildGallery());
    mockImages.find.mockResolvedValue({
      items: [{ id: 91, title: "Cover Frame" }],
      totalCount: 1,
    });
    mockVideos.find.mockResolvedValue({ items: [{ id: 4, title: "Video One" }], totalCount: 1 });

    renderPage();

    expect((await screen.findAllByRole("heading", { name: "Summer Set" })).length).toBeGreaterThan(0);
    expect(screen.getByText("Alex")).toBeInTheDocument();
    expect(await screen.findByText("Cover Frame")).toBeInTheDocument();

    const tabs = screen.getByRole("tablist", { name: /detail tabs/i });
    expect(tabs).not.toHaveClass("mx-auto");

    fireEvent.click(within(tabs).getByRole("tab", { name: /videos/i }));
    expect(await screen.findByText("Video One")).toBeInTheDocument();

    fireEvent.click(within(tabs).getByRole("tab", { name: /file info/i }));
    expect(await screen.findByText("C:/galleries/summer-set")).toBeInTheDocument();
  });

  it("supports keyboard shortcuts for images, videos, file info, and edit", async () => {
    mockGalleries.get.mockResolvedValue(buildGallery());
    mockImages.find.mockResolvedValue({
      items: [{ id: 91, title: "Cover Frame" }],
      totalCount: 1,
    });
    mockVideos.find.mockResolvedValue({ items: [{ id: 4, title: "Video One" }], totalCount: 1 });

    renderPage();

    expect((await screen.findAllByRole("heading", { name: "Summer Set" })).length).toBeGreaterThan(0);

    fireEvent.keyDown(window, { key: "s" });
    expect(await screen.findByText("Video One")).toBeInTheDocument();

    fireEvent.keyDown(window, { key: "f" });
    expect(await screen.findByText("C:/galleries/summer-set")).toBeInTheDocument();

    fireEvent.keyDown(window, { key: "a" });
    expect(await screen.findByText("Cover Frame")).toBeInTheDocument();

    fireEvent.keyDown(window, { key: "e" });
    expect(await screen.findByText("Edit Gallery Modal")).toBeInTheDocument();
  });
});
