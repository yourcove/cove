import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { VideoDetailPage } from "../pages/VideoDetailPage";

const { mockVideos, videoPlayerMock } = vi.hoisted(() => ({
  mockVideos: {
    get: vi.fn(),
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
  const MockMediaDetailLayout = ({ media }: { media: ReactElement<{ children?: ReactNode }> }) => {
    const children = Array.isArray(media.props.children) ? media.props.children : [media.props.children];
    return <>{children[0]}</>;
  };
  MockMediaDetailLayout.Content = ({ children }: { children: ReactNode }) => <>{children}</>;
  return { MediaDetailLayout: MockMediaDetailLayout };
});

vi.mock("../router/RouteRegistry", () => ({
  ExtensionSlot: () => null,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    hasPermission: () => false,
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

function renderVideoDetail() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  render(
    <QueryClientProvider client={queryClient}>
      <VideoDetailPage id={14} onNavigate={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("VideoDetailPage media-player extension surface", () => {
  afterEach(() => {
    vi.clearAllMocks();
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
});
