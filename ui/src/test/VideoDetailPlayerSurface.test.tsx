import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { VideoDetailPage } from "../pages/VideoDetailPage";

const { mockVideos, videoPlayerMock } = vi.hoisted(() => ({
  mockVideos: {
    get: vi.fn(),
    update: vi.fn(),
    screenshotUrl: vi.fn((id: number) => `/video-${id}.jpg`),
    streamUrl: vi.fn((id: number) => `/video-${id}.mp4`),
  },
  videoPlayerMock: vi.fn(),
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
    queue: null,
    currentId: null,
    hasPrev: false,
    hasNext: false,
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
  useVideoVisualSimilarityAvailable: () => false,
}));

vi.mock("../components/AudioSimilarityPanel", () => ({
  VideoAudioSimilarityPanel: () => null,
  useVideoAudioSimilarityAvailable: () => false,
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

  const renderPage = (videoId: number) => (
    <QueryClientProvider client={queryClient}>
      <VideoDetailPage id={videoId} initialSeekTo={initialSeekTo} onNavigate={vi.fn()} />
    </QueryClientProvider>
  );
  const result = render(renderPage(id));

  return {
    ...result,
    rerenderVideoDetail: (videoId: number) => result.rerender(renderPage(videoId)),
  };
}

describe("VideoDetailPage media-player extension surface", () => {
  afterEach(() => {
    vi.clearAllMocks();
    mockVideos.get.mockReset();
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
