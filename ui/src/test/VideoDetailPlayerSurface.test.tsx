import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { VideoDetailPage } from "../pages/VideoDetailPage";

const { mockVideos, videoPlayerMock, videoQueueMock, visualAvailabilityMock, coverDialogMock } = vi.hoisted(() => ({
  mockVideos: {
    get: vi.fn(),
    update: vi.fn(),
    screenshotUrl: vi.fn((id: number) => `/video-${id}.jpg`),
    streamUrl: vi.fn((id: number) => `/video-${id}.mp4`),
  },
  videoPlayerMock: vi.fn(),
  videoQueueMock: {
    queue: null as null | { videoIds: number[] },
    currentId: null as number | null,
    hasPrev: false,
    hasNext: false,
    goPrevious: vi.fn(),
    goNext: vi.fn(),
  },
  visualAvailabilityMock: { available: false, loading: false },
  coverDialogMock: vi.fn(),
}));

vi.mock("../api/client", () => ({
  entityImages: { studioImageUrl: vi.fn() },
  faces: { get: vi.fn() },
  galleries: {},
  metadata: {},
  fileOps: {},
  segmentDisplayProfiles: {},
  tagApplications: {},
  tags: {},
  videos: mockVideos,
}));

vi.mock("../components/VideoPlayer", () => ({
  VideoPlayer: (props: Record<string, unknown>) => {
    videoPlayerMock(props);
    return <div data-testid="video-detail-player">Video Player</div>;
  },
}));

vi.mock("../components/CoverImageDialog", () => ({
  CoverImageDialog: (props: Record<string, unknown>) => {
    coverDialogMock(props);
    return null;
  },
}));

vi.mock("../components/MediaDetailLayout/MediaDetailLayout", () => {
  const MockMediaDetailLayout = ({ media, tabs, activeTab, onTabChange, children }: { media: ReactElement<{ children?: ReactNode }>; tabs: { key: string; label: string }[]; activeTab: string; onTabChange: (key: string) => void; children?: ReactNode }) => {
    const mediaChildren = Array.isArray(media.props.children) ? media.props.children : [media.props.children];
    return <><div>{tabs.map((tab) => <button key={tab.key} role="tab" aria-selected={tab.key === activeTab} onClick={() => onTabChange(tab.key)}>{tab.label}</button>)}</div>{mediaChildren[0]}{activeTab === "edit" ? children : null}</>;
  };
  MockMediaDetailLayout.Content = ({ children }: { children: ReactNode }) => <>{children}</>;
  return { MediaDetailLayout: MockMediaDetailLayout };
});

vi.mock("../router/RouteRegistry", () => ({
  ExtensionSlot: () => null,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    hasPermission: (permission: string) => permission === "videos.write",
    user: { kind: "user", uiPreferences: { tracking: { enabled: false } } },
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: {} } }),
  useOptionalAppConfig: () => ({ config: { ui: {} } }),
}));

vi.mock("../components/ConfirmDialog", () => ({
  ConfirmDialog: () => null,
}));

vi.mock("../state/VideoQueueContext", () => ({
  useVideoQueue: () => ({
    ...videoQueueMock,
    prevId: null,
    nextId: null,
    currentPosition: 0,
    queueLength: 0,
    queueItems: [],
    goToIndex: vi.fn(),
    clearQueue: vi.fn(),
    autoplay: false,
    toggleAutoplay: vi.fn(),
  }),
}));

vi.mock("../extensions/ExtensionLoader", () => ({
  useExtensions: () => ({
    getTabsForPage: () => [],
    getExtensionRevision: () => 0,
    resolveComponent: () => undefined,
    getFeature: () => undefined,
  }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({ backLabel: "Back", goBack: vi.fn() }),
}));

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    engagement: undefined,
    favorite: false,
    rating: undefined,
    setFavorite: vi.fn(),
    setRating: vi.fn(),
    favoritePending: false,
  }),
}));

vi.mock("../hooks/useEntityEngagementBatch", () => ({
  useEntityEngagementBatch: () => ({ engagementById: {} }),
}));

vi.mock("../components/VisualSimilarityPanel", () => ({
  VideoVisualSimilarityPanel: () => null,
  useVideoVisualSimilarityAvailability: () => ({ ...visualAvailabilityMock }),
}));

vi.mock("../components/AudioSimilarityPanel", () => ({
  VideoAudioSimilarityPanel: () => null,
  useVideoAudioSimilarityAvailability: () => ({ available: false, loading: false }),
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => undefined,
}));

function renderVideoDetail(id = 14, initialSeekTo?: number) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  const onNavigate = vi.fn();
  const renderPage = (videoId: number) => (
    <QueryClientProvider client={queryClient}>
      <VideoDetailPage id={videoId} initialSeekTo={initialSeekTo} onNavigate={onNavigate} />
    </QueryClientProvider>
  );
  const result = render(renderPage(id));

  return {
    ...result,
    onNavigate,
    rerenderVideoDetail: (videoId: number) => result.rerender(renderPage(videoId)),
  };
}

describe("VideoDetailPage media-player extension surface", () => {
  afterEach(() => {
    vi.clearAllMocks();
    mockVideos.get.mockReset();
    videoQueueMock.queue = null;
    videoQueueMock.currentId = null;
    videoQueueMock.hasPrev = false;
    videoQueueMock.hasNext = false;
    videoQueueMock.goPrevious.mockReset();
    videoQueueMock.goNext.mockReset();
    visualAvailabilityMock.available = false;
    visualAvailabilityMock.loading = false;
    coverDialogMock.mockReset();
  });

  it("does not offer removal for a generated-only video cover", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Generated cover video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [{ format: "mp4", duration: 120, width: 1920, height: 1080, frameRate: 30, captions: [] }],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail();

    await waitFor(() => expect(coverDialogMock).toHaveBeenCalled());
    expect(coverDialogMock.mock.calls.at(-1)?.[0]).toEqual(expect.objectContaining({ onDelete: undefined }));
  });

  it("opts the primary player into the detail extension surface", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Detail video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [{
        format: "mp4",
        duration: 120,
        width: 1920,
        height: 1080,
        frameRate: 30,
        captions: [],
      }],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail();

    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(expect.objectContaining({
      videoId: 14,
      extensionSurface: "detail",
    }));
  });

  it("constrains sub-video playback to its parent clip range", async () => {
    mockVideos.get.mockResolvedValue({
      id: 15,
      title: "Sub-video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      parentVideoId: 14,
      clipStartSec: 30,
      clipEndSec: 60,
      files: [{
        format: "mp4",
        duration: 120,
        width: 1920,
        height: 1080,
        frameRate: 30,
        captions: [],
      }],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail(15);

    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(expect.objectContaining({
      videoId: 15,
      clip: { start: 30, end: 60, loop: false },
    }));
  });

  it("passes an explicit route timestamp separately from saved resume state", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Timestamped video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [{
        format: "mp4",
        duration: 120,
        width: 1920,
        height: 1080,
        frameRate: 30,
        captions: [],
      }],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail(14, 42.5);

    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(expect.objectContaining({
      seekTo: 42.5,
      resumeTime: undefined,
    }));
  });

  it("stays on the edit tab after saving", async () => {
    const video = {
      id: 14,
      title: "Editable video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [],
      performers: [],
      tags: [],
      galleries: [],
      groups: [],
      urls: [],
      remoteIds: [],
      contextTagApplications: [],
    };
    mockVideos.get.mockResolvedValue(video);
    mockVideos.update.mockResolvedValue(video);

    renderVideoDetail();

    fireEvent.click(await screen.findByRole("tab", { name: "Edit" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockVideos.update).toHaveBeenCalled());
    expect(screen.getByRole("tab", { name: "Edit" })).toHaveAttribute("aria-selected", "true");
  });

  it("carries the selected tab when opening the next video", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Queued video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [{ format: "mp4", duration: 120, width: 1920, height: 1080, frameRate: 30, captions: [] }],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });
    videoQueueMock.queue = { videoIds: [14, 15] };
    videoQueueMock.currentId = 14;
    videoQueueMock.hasNext = true;
    videoQueueMock.goNext.mockResolvedValue(15);

    const { onNavigate } = renderVideoDetail();
    fireEvent.click(await screen.findByRole("tab", { name: "History" }));

    const playerProps = videoPlayerMock.mock.calls.at(-1)?.[0] as { onNext?: () => void };
    playerProps.onNext?.();

    await waitFor(() => expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 15, videoTab: "history" }));
  });

  it("keeps the Similar tab selected while the next video's availability loads", async () => {
    mockVideos.get.mockImplementation(async (videoId: number) => ({
      id: videoId,
      title: `Queued video ${videoId}`,
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [],
      performers: [],
      tags: [],
      contextTagApplications: [],
    }));
    visualAvailabilityMock.available = true;

    const { rerenderVideoDetail } = renderVideoDetail();
    fireEvent.click(await screen.findByRole("tab", { name: "Similar" }));

    visualAvailabilityMock.available = false;
    visualAvailabilityMock.loading = true;
    rerenderVideoDetail(15);
    await waitFor(() => expect(mockVideos.get).toHaveBeenCalledWith(15));
    visualAvailabilityMock.available = true;
    visualAvailabilityMock.loading = false;
    rerenderVideoDetail(15);

    expect(await screen.findByRole("tab", { name: "Similar" })).toHaveAttribute("aria-selected", "true");
  });

  it("shows a retryable load error when the video request fails", async () => {
    mockVideos.get
      .mockRejectedValueOnce(new Error("API Error 502: upstream API Error 404"))
      .mockResolvedValueOnce({
        id: 14,
        title: "Recovered video",
        organized: false,
        updatedAt: "2026-07-11T00:00:00Z",
        files: [{
          format: "mp4",
          duration: 120,
          width: 1920,
          height: 1080,
          frameRate: 30,
          captions: [],
        }],
        performers: [],
        tags: [],
        contextTagApplications: [],
      });

    renderVideoDetail();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load video");
    expect(screen.queryByText("Video not found")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();
  });

  it("keeps the not-found state for a genuine missing video", async () => {
    mockVideos.get.mockRejectedValue(new Error("API Error 404: Not Found"));

    renderVideoDetail();

    expect(await screen.findByText("Video not found")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("does not let the previous video's placeholder hide a load failure", async () => {
    mockVideos.get
      .mockResolvedValueOnce({
        id: 14,
        title: "First video",
        organized: false,
        updatedAt: "2026-07-11T00:00:00Z",
        files: [{
          format: "mp4",
          duration: 120,
          width: 1920,
          height: 1080,
          frameRate: 30,
          captions: [],
        }],
        performers: [],
        tags: [],
        contextTagApplications: [],
      })
      .mockRejectedValueOnce(new Error("API Error 502: Bad Gateway"));

    const { rerenderVideoDetail } = renderVideoDetail();
    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();

    rerenderVideoDetail(15);

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load video");
    expect(screen.queryByTestId("video-detail-player")).not.toBeInTheDocument();
  });
});
