import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FaceDetailPage } from "../pages/FaceDetailPage";

const { mockFaces, mockPerformers, mockEntityEngagement, mockGoBack } = vi.hoisted(() => ({
  mockFaces: {
    get: vi.fn(),
    similar: vi.fn(),
    appearances: vi.fn(),
    detections: vi.fn(),
    deleteImpact: vi.fn(),
    update: vi.fn(),
    link: vi.fn(),
    recordSuggestionDecision: vi.fn(),
    setIgnored: vi.fn(),
    mergeInto: vi.fn(),
    delete: vi.fn(),
    list: vi.fn(),
  },
  mockPerformers: {
    find: vi.fn(),
  },
  mockEntityEngagement: {
    get: vi.fn(),
    setFavorite: vi.fn(),
    setRating: vi.fn(),
  },
  mockGoBack: vi.fn(),
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => {},
}));

vi.mock("../api/client", () => ({
  faces: mockFaces,
  performers: mockPerformers,
  entityEngagement: mockEntityEngagement,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    user: { kind: "user" },
    hasPermission: (permission: string) => permission.endsWith(".read"),
  }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({
    backLabel: "Back to faces",
    goBack: mockGoBack,
  }),
}));

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  const onNavigate = vi.fn();

  mockFaces.list.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 500 });

  render(
    <QueryClientProvider client={queryClient}>
      <FaceDetailPage id={7} onNavigate={onNavigate} />
    </QueryClientProvider>,
  );

  return { onNavigate };
}

describe("FaceDetailPage", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows a retryable load error and recovers", async () => {
    mockFaces.get
      .mockRejectedValueOnce(new Error("API Error 502: upstream API Error 404"))
      .mockResolvedValueOnce({
        id: 7,
        label: "Recovered face",
        ignored: false,
        detectionCount: 0,
        videoCount: 0,
        appearanceCount: 0,
        frameSampleCount: 0,
        imageCount: 0,
        createdAt: "2026-04-01T12:00:00Z",
        updatedAt: "2026-04-02T12:00:00Z",
      });
    mockFaces.similar.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 18 });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load face");
    expect(screen.queryByText("Face not found")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("heading", { name: "Recovered face" })).toBeInTheDocument();
  });

  it("keeps the not-found state for a genuine missing face", async () => {
    mockFaces.get.mockRejectedValue(new Error("API Error 404: Not Found"));
    mockFaces.similar.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 18 });
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();

    expect(await screen.findByText("Face not found")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("renders face metadata and similar faces", async () => {
    mockFaces.get.mockResolvedValue({
      id: 7,
      label: "Jane Cluster",
      performerId: 12,
      performerName: "Jane Doe",
      coverImageUrl: "/img/faces/7.jpg",
      ignored: false,
      mergedIntoFaceId: undefined,
      detectionCount: 18,
      videoCount: 4,
      appearanceCount: 6,
      frameSampleCount: 11,
      imageCount: 2,
      topSuggestion: undefined,
      primarySourceKey: "detector.facebox",
      createdAt: "2026-04-01T12:00:00Z",
      updatedAt: "2026-04-02T12:00:00Z",
    });
    mockFaces.similar.mockResolvedValue({
      items: [
        {
          id: 17,
          label: "Similar Jane",
          performerId: undefined,
          performerName: undefined,
          coverImageUrl: "/img/faces/17.jpg",
          ignored: false,
          mergedIntoFaceId: undefined,
          detectionCount: 9,
          videoCount: 3,
          imageCount: 1,
          primarySourceKey: "detector.facebox",
          createdAt: "2026-03-30T12:00:00Z",
          updatedAt: "2026-04-03T12:00:00Z",
          appearanceCount: 4,
          frameSampleCount: 7,
          distance: 0.1234,
        },
      ],
      totalCount: 20,
      page: 1,
      perPage: 18,
    });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    const { onNavigate } = renderPage();

    // A linked face shows the performer's name (faceDisplayName prefers performerName over label).
    expect(await screen.findByRole("heading", { name: "Jane Doe" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));

    expect(await screen.findByText("Nearest neighbors from the face embedding index.")).toBeInTheDocument();
    expect(await screen.findByText("Similar Jane")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Page 2" })).toHaveLength(2);
    expect(screen.getByRole("navigation", { name: "Pagination above results" })).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Pagination" })).toBeInTheDocument();
    expect(mockFaces.similar).toHaveBeenCalledWith(7, expect.objectContaining({ k: 250 }));
    expect(screen.getByRole("option", { name: "Random" })).toBeInTheDocument();
    expect(screen.getAllByRole("combobox").length).toBeGreaterThanOrEqual(2);

    fireEvent.click(screen.getByRole("link", { name: "Open face Similar Jane" }));
    expect(onNavigate).toHaveBeenCalledWith({ page: "face", id: 17 });
  });

  it("uses the back navigation hook", async () => {
    mockFaces.get.mockResolvedValue({
      id: 7,
      label: "Jane Cluster",
      performerId: undefined,
      performerName: undefined,
      coverImageUrl: undefined,
      ignored: false,
      mergedIntoFaceId: undefined,
      detectionCount: 3,
      videoCount: 1,
      appearanceCount: 1,
      frameSampleCount: 1,
      imageCount: 0,
      topSuggestion: undefined,
      primarySourceKey: undefined,
      createdAt: "2026-04-01T12:00:00Z",
      updatedAt: "2026-04-02T12:00:00Z",
    });
    mockFaces.similar.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 18 });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();
    await screen.findByRole("heading", { name: "Jane Cluster" });

    fireEvent.click(screen.getByRole("button", { name: "Back to faces" }));
    expect(mockGoBack).toHaveBeenCalled();
  });
});
