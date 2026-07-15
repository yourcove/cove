import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SegmentDetailPage } from "../pages/SegmentDetailPage";

vi.stubGlobal("IntersectionObserver", vi.fn(function IntersectionObserver() {
  return {
    observe: vi.fn(),
    unobserve: vi.fn(),
    disconnect: vi.fn(),
  };
}));

const { mockEntityImages, mockVideos, mockSegmentLibrary, mockTags, mockGoBack } = vi.hoisted(() => ({
  mockEntityImages: {
    segmentCoverUrl: vi.fn(() => "/segment-cover.jpg"),
    videoCoverUrl: vi.fn(() => "/video-cover.jpg"),
    studioImageUrl: vi.fn(() => "/studio-image.jpg"),
    uploadSegmentCoverImage: vi.fn(),
    deleteSegmentCoverImage: vi.fn(),
    setSegmentCoverFromFrame: vi.fn(),
  },
  mockVideos: {
    get: vi.fn(),
    createSubVideo: vi.fn(),
    streamUrl: vi.fn(() => "/stream/video.mp4"),
    screenshotUrl: vi.fn(() => "/video.jpg"),
    previewUrl: vi.fn(() => "/video-preview.mp4"),
    previewStatusUrl: vi.fn(() => "/video-preview-status"),
    segments: {
      list: vi.fn(),
      spans: vi.fn(),
      update: vi.fn(),
      delete: vi.fn(),
    },
  },
  mockSegmentLibrary: {
    get: vi.fn(),
  },
  mockTags: {
    find: vi.fn(),
  },
  mockGoBack: vi.fn(),
}));

vi.mock("../api/client", () => ({
  entityImages: mockEntityImages,
  videos: mockVideos,
  segmentLibrary: mockSegmentLibrary,
  tags: mockTags,
}));

vi.mock("../components/VideoPlayer", () => ({
  VideoPlayer: () => <div data-testid="segment-video-player">Video Player</div>,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    hasPermission: () => true,
  }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({
    backLabel: "Back to segments",
    goBack: mockGoBack,
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: { keybindingOverrides: {} } } }),
  useOptionalAppConfig: () => ({ config: { ui: { keybindingOverrides: {}, videoObjectFit: "cover" } } }),
}));

function buildSegment(overrides: Record<string, unknown> = {}) {
  return {
    id: 7,
    hostId: 99,
    hostType: "video",
    hostTitle: "Video 99",
    title: "Episode Intro",
    kind: "intro",
    sourceKey: "detector.segment",
    sourceRunId: "run-1",
    colorHint: "#ffaa00",
    startSec: 12,
    endSec: 21,
    confidence: 0.91,
    tagId: 5,
    tagName: "Opening",
    refId: 55,
    payload: { score: 0.91 },
    createdAt: "2026-05-01T12:00:00Z",
    updatedAt: "2026-05-01T13:00:00Z",
    ...overrides,
  };
}

function buildVideo(overrides: Record<string, unknown> = {}) {
  return {
    id: 99,
    title: "Video 99",
    code: "SC-99",
    details: "Parent video details",
    director: "Director Example",
    date: "2026-05-01",
    organized: true,
    studioId: 12,
    studioName: "Studio 12",
    urls: ["https://example.test/video-99"],
    tags: [
      { id: 5, name: "Opening" },
      { id: 6, name: "Action" },
    ],
    performers: [{ id: 11, name: "Performer One" }],
    galleries: [{ id: 15, title: "Gallery Fifteen", date: "2026-04-15" }],
    groups: [{ id: 17, name: "Highlights", videoIndex: 3 }],
    customFields: { mood: "intense" },
    files: [{ format: "mp4", duration: 120, captions: [] }],
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
      <SegmentDetailPage id={7} onNavigate={onNavigate} />
    </QueryClientProvider>,
  );

  return { onNavigate };
}

describe("SegmentDetailPage", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("renders media layout tabs and timeline context", async () => {
    mockSegmentLibrary.get.mockResolvedValue(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([
      buildSegment({ id: 6, title: "Cold Open", startSec: 5, endSec: 10, tagName: "Teaser" }),
      buildSegment(),
      buildSegment({ id: 8, title: "Action Beat", startSec: 22, endSec: 34, tagName: "Action" }),
    ]);
    mockVideos.segments.spans.mockResolvedValue({
      profileId: 3,
      spans: [
        {
          spanKey: "focus",
          startSec: 10,
          endSec: 28,
          tagName: "Opening Stretch",
          kind: "highlight",
          segmentIds: [7, 8],
        },
      ],
    });
    mockTags.find.mockResolvedValue({ items: [] });

    renderPage();

    expect(await screen.findByRole("heading", { name: "Episode Intro" })).toBeInTheDocument();
    expect(screen.getByTestId("media-detail-layout-media")).toBeInTheDocument();
    expect(screen.getByTestId("segment-video-player")).toBeInTheDocument();
    expect(screen.queryByTestId("media-detail-layout-media-frame")).not.toBeInTheDocument();

    const tabs = screen.getByRole("tablist", { name: /detail tabs/i });

    fireEvent.click(within(tabs).getByRole("tab", { name: /context/i }));
    expect(await screen.findByText("Previous Segments")).toBeInTheDocument();
    expect(screen.getAllByText("Cold Open").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Action Beat").length).toBeGreaterThan(0);

    expect(within(tabs).queryByRole("tab", { name: /resolved spans/i })).not.toBeInTheDocument();
  });

  it("does not list intersecting segments as adjacent segments", async () => {
    mockSegmentLibrary.get.mockResolvedValue(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([
      buildSegment({ id: 5, title: "Earlier Segment", startSec: 5, endSec: 10, tagName: "Teaser" }),
      buildSegment({ id: 6, title: "Earlier Overlap", startSec: 10, endSec: 15, tagName: "Transition" }),
      buildSegment(),
      buildSegment({ id: 8, title: "Same Range", tagName: "Action" }),
      buildSegment({ id: 9, title: "Later Segment", startSec: 22, endSec: 34, tagName: "Closing" }),
    ]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockTags.find.mockResolvedValue({ items: [] });

    renderPage();

    const tabs = await screen.findByRole("tablist", { name: /detail tabs/i });
    fireEvent.click(within(tabs).getByRole("tab", { name: /context/i }));

    const previousSection = screen.getByText("Previous Segments").parentElement!;
    const nextSection = screen.getByText("Next Segments").parentElement!;
    const intersectingSection = screen.getByText("Intersecting Segments").parentElement!;
    expect(within(previousSection).getByText("Earlier Segment")).toBeInTheDocument();
    expect(within(previousSection).queryByText("Earlier Overlap")).not.toBeInTheDocument();
    expect(within(nextSection).queryByText("Same Range")).not.toBeInTheDocument();
    expect(within(nextSection).getByText("Later Segment")).toBeInTheDocument();
    expect(within(intersectingSection).getByText("Earlier Overlap")).toBeInTheDocument();
    expect(within(intersectingSection).getByText("Same Range")).toBeInTheDocument();
  });

  it("supports keyboard shortcuts for edit and adjacent navigation", async () => {
    mockSegmentLibrary.get.mockResolvedValue(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([
      buildSegment({ id: 6, title: "Cold Open", startSec: 5, endSec: 10 }),
      buildSegment(),
      buildSegment({ id: 8, title: "Action Beat", startSec: 22, endSec: 34 }),
    ]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockTags.find.mockResolvedValue({ items: [] });

    const { onNavigate } = renderPage();

    await screen.findByRole("heading", { name: "Episode Intro" });

    fireEvent.keyDown(window, { key: "e" });
    expect(await screen.findByRole("heading", { name: "Edit Segment" })).toBeInTheDocument();
    expect(screen.getByLabelText("Title")).toHaveFocus();

    fireEvent.keyDown(window, { key: "]" });
    expect(onNavigate).toHaveBeenCalledWith({ page: "segment", id: 8 });

    fireEvent.keyDown(window, { key: "s" });
    expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 99, seekTo: 12 });
  });

  it("creates a metadata-preserving sub-video from the current segment", async () => {
    mockSegmentLibrary.get.mockResolvedValue(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.createSubVideo.mockResolvedValue({ id: 1234 });
    mockVideos.segments.list.mockResolvedValue([buildSegment()]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockTags.find.mockResolvedValue({ items: [] });

    const { onNavigate } = renderPage();

    fireEvent.click(await screen.findByTitle("Operations"));
    const makeVideoButton = await screen.findByRole("button", { name: /make video/i });
    await waitFor(() => {
      expect(makeVideoButton).toBeEnabled();
    });
    fireEvent.click(makeVideoButton);

    await waitFor(() => {
      expect(mockVideos.createSubVideo).toHaveBeenCalledWith(99, expect.objectContaining({
        title: "Episode Intro",
        code: "SC-99",
        details: "Parent video details",
        director: "Director Example",
        date: "2026-05-01",
        organized: true,
        studioId: 12,
        urls: ["https://example.test/video-99"],
        tagIds: [5, 6],
        performerIds: [11],
        galleryIds: [15],
        groups: [{ groupId: 17, videoIndex: 3 }],
        customFields: { mood: "intense" },
        parentVideoId: 99,
        clipStartSec: 12,
        clipEndSec: 21,
      }));
    });
    await waitFor(() => {
      expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 1234 });
    });
  });
});
