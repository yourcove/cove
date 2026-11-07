import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { segmentDisplayProfiles } from "../api/client";
import { VideoDetailPage } from "../pages/VideoDetailPage";

const { mockVideos, videoPlayerMock, videoQueueMock, visualAvailabilityMock, coverDialogMock, appConfigMock } =
  vi.hoisted(() => ({
    appConfigMock: { config: { ui: {} } as { ui: Record<string, unknown>; scraping?: { metadataServers: unknown[] } } },
    mockVideos: {
      get: vi.fn(),
      update: vi.fn(),
      screenshotUrl: vi.fn((id: number) => `/video-${id}.jpg`),
      streamUrl: vi.fn((id: number) => `/video-${id}.mp4`),
      submitMetadataServerDraft: vi.fn(),
      getHistory: vi.fn(),
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

const { authMock, segmentsPanelMock, layoutShortcutsMock } = vi.hoisted(() => ({
  authMock: { extraPermissions: new Set<string>(), grantsVideoWrite: true },
  segmentsPanelMock: vi.fn(),
  layoutShortcutsMock: vi.fn((_shortcuts: { id?: string; handler: () => void }[]) => undefined),
}));

const { extensionTabsMock } = vi.hoisted(() => ({
  extensionTabsMock: { tabs: [] as Record<string, unknown>[] },
}));

const { videoSpritesMock, layoutMock } = vi.hoisted(() => ({
  videoSpritesMock: vi.fn((_videoId: number | null) => null as unknown),
  // Most tests only need the player row; timeline tests also render the scrubber below it.
  layoutMock: { renderWholeMediaColumn: false, renderDetailsTab: false },
}));

vi.mock("../hooks/useVideoSprites", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../hooks/useVideoSprites")>()),
  useVideoSprites: (videoId: number | null) => videoSpritesMock(videoId),
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
  const MockMediaDetailLayout = ({
    media,
    mediaSizesItselfOnMobile,
    actions,
    tabs,
    activeTab,
    onTabChange,
    keyboardShortcuts,
    children,
  }: {
    media: ReactElement<{ children?: ReactNode }>;
    mediaSizesItselfOnMobile?: boolean;
    actions?: ReactNode;
    tabs: { key: string; label: string; icon?: ReactNode }[];
    activeTab: string;
    onTabChange: (key: string) => void;
    keyboardShortcuts?: { id?: string; handler: () => void }[];
    children?: ReactNode;
  }) => {
    layoutShortcutsMock(keyboardShortcuts ?? []);
    const mediaChildren = Array.isArray(media.props.children) ? media.props.children : [media.props.children];
    return (
      <>
        <div>
          {tabs.map((tab) => (
            <button key={tab.key} role="tab" aria-selected={tab.key === activeTab} onClick={() => onTabChange(tab.key)}>
              {tab.icon}
              {tab.label}
            </button>
          ))}
        </div>
        {actions}
        <div data-testid="video-detail-media" data-sizes-itself-on-mobile={String(Boolean(mediaSizesItselfOnMobile))}>
          {layoutMock.renderWholeMediaColumn ? mediaChildren : mediaChildren[0]}
        </div>
        {activeTab === "edit" || activeTab === "file-info" || layoutMock.renderDetailsTab ? children : null}
      </>
    );
  };
  MockMediaDetailLayout.Content = ({ children }: { children: ReactNode }) => <>{children}</>;
  return { MediaDetailLayout: MockMediaDetailLayout };
});

vi.mock("../components/VideoSegmentsPanel", () => ({
  VideoSegmentsPanel: (props: Record<string, unknown>) => {
    segmentsPanelMock(props);
    return <div data-testid="video-segments-panel" />;
  },
}));

vi.mock("../router/RouteRegistry", () => ({
  ExtensionSlot: () => null,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    hasPermission: (permission: string) =>
      (permission === "videos.write" && authMock.grantsVideoWrite) ||
      permission === "files.read" ||
      authMock.extraPermissions.has(permission),
    user: { kind: "user", uiPreferences: { tracking: { enabled: false } } },
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => appConfigMock,
  useOptionalAppConfig: () => appConfigMock,
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
    getTabsForPage: (pageType: string) => (pageType === "video" ? extensionTabsMock.tabs : []),
    getActionsForContext: () => [],
    getExtensionRevision: () => 0,
    resolveComponent: () => undefined,
    getFeature: () => undefined,
  }),
  ExtensionComponentOverrideRenderer: ({ renderDefault }: { renderDefault: () => ReactNode }) => <>{renderDefault()}</>,
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
  useEntityEngagementBatch: () => ({ engagementById: new Map() }),
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
    queryClient,
    onNavigate,
    rerenderVideoDetail: (videoId: number) => result.rerender(renderPage(videoId)),
  };
}

describe("VideoDetailPage media-player extension surface", () => {
  it("does not offer file actions after the last file disappears", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Missing media",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });
    renderVideoDetail();
    fireEvent.click(await screen.findByRole("tab", { name: "File Info" }));
    expect(screen.queryByRole("button", { name: "Set as primary" })).not.toBeInTheDocument();
  });
  afterEach(() => {
    vi.clearAllMocks();
    extensionTabsMock.tabs = [];
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
    videoSpritesMock.mockReset();
    layoutMock.renderWholeMediaColumn = false;
    layoutMock.renderDetailsTab = false;
    authMock.extraPermissions.clear();
    authMock.grantsVideoWrite = true;
    videoSpritesMock.mockReturnValue(null);
    appConfigMock.config = { ui: {} };
  });

  it.each([
    { permissions: ["videos.read"], canEngage: true },
    { permissions: [], canEngage: false },
  ])(
    "gates History play and like controls on engagement rather than write access (canEngage: $canEngage)",
    async ({ permissions, canEngage }) => {
      authMock.grantsVideoWrite = false;
      layoutMock.renderDetailsTab = true;
      authMock.extraPermissions = new Set(permissions);
      mockVideos.get.mockResolvedValue({
        id: 14,
        title: "History video",
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
      });
      mockVideos.getHistory.mockResolvedValue({
        playHistory: ["2026-07-10T12:00:00Z"],
        likeHistory: ["2026-07-09T12:00:00Z"],
        sessions: [],
      });

      renderVideoDetail();
      fireEvent.click(await screen.findByRole("tab", { name: "History" }));
      await waitFor(() => expect(mockVideos.getHistory).toHaveBeenCalledWith(14));
      await screen.findByText(/2026-07-09/);

      const controls = {
        removeLastPlay: screen.queryByTitle("Remove last play"),
        resetPlayCount: screen.queryByTitle("Reset play count"),
        likeHistoryActions: screen.queryByRole("button", { name: "Like history actions" }),
        deleteLike: screen.queryByRole("button", { name: /^Delete like from/ }),
      };
      expect(Object.fromEntries(Object.entries(controls).map(([name, control]) => [name, control !== null]))).toEqual({
        removeLastPlay: canEngage,
        resetPlayCount: canEngage,
        likeHistoryActions: canEngage,
        deleteLike: canEngage,
      });
    },
  );

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
      files: [
        {
          format: "mp4",
          duration: 120,
          width: 1920,
          height: 1080,
          frameRate: 30,
          captions: [],
        },
      ],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail();

    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(
      expect.objectContaining({
        videoId: 14,
        extensionSurface: "detail",
      }),
    );
  });

  it("sizes the mobile player area to the playing file's aspect ratio", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Portrait video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [{ format: "mp4", duration: 120, width: 1080, height: 1920, frameRate: 30, captions: [] }],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail();

    const media = await screen.findByTestId("video-detail-media");
    expect(media).toHaveAttribute("data-sizes-itself-on-mobile", "true");
    const playerArea = await screen.findByTestId("video-detail-player-area");
    expect(playerArea.style.getPropertyValue("--video-aspect-ratio")).toBe("1080 / 1920");
    expect(playerArea).toHaveClass("max-lg:aspect-(--video-aspect-ratio)");
    expect(within(playerArea).getByTestId("video-detail-player").parentElement).toHaveClass("max-lg:absolute");
  });

  it("falls back to a 16:9 mobile player area when the file has no dimensions", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Unprobed video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [{ format: "mp4", duration: 120, width: 0, height: 0, frameRate: 30, captions: [] }],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail();

    const playerArea = await screen.findByTestId("video-detail-player-area");
    expect(playerArea.style.getPropertyValue("--video-aspect-ratio")).toBe("16 / 9");
  });

  it("does not size the mobile player area when there is no file", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Missing media",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail();

    const playerArea = await screen.findByTestId("video-detail-player-area");
    expect(playerArea).toHaveTextContent("No video file available");
    expect(playerArea).not.toHaveClass("max-lg:aspect-(--video-aspect-ratio)");
    expect(playerArea.style.getPropertyValue("--video-aspect-ratio")).toBe("");
  });

  const spriteSheet = {
    entries: [{ start: 0, end: 10, x: 0, y: 0, w: 160, h: 90 }],
    imageUrl: "/api/stream/video/14/sprite",
    sheetWidth: 160,
    sheetHeight: 90,
  };

  function mockSpriteVideo() {
    videoSpritesMock.mockImplementation((videoId) => (videoId === 14 ? spriteSheet : null));
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Sprite video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [{ format: "mp4", duration: 120, width: 1920, height: 1080, frameRate: 30, captions: [] }],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });
  }

  it("gives the player the video's sprites for the seek-bar preview", async () => {
    mockSpriteVideo();

    renderVideoDetail();

    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenLastCalledWith(expect.objectContaining({ seekPreviewSprites: spriteSheet }));
  });

  it("shows the sprite strip below the player by default", async () => {
    mockSpriteVideo();
    layoutMock.renderWholeMediaColumn = true;

    renderVideoDetail();

    expect(await screen.findByTestId("video-sprite-strip")).toBeInTheDocument();
  });

  it("hides the sprite strip but keeps the seek-bar preview when scrubber sprites are turned off", async () => {
    mockSpriteVideo();
    layoutMock.renderWholeMediaColumn = true;
    appConfigMock.config = { ui: { showScrubberSprites: false } };

    renderVideoDetail();

    // The strip renders in the same pass as the player, so its absence here is not a timing artifact.
    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();
    expect(screen.queryByTestId("video-sprite-strip")).not.toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenLastCalledWith(expect.objectContaining({ seekPreviewSprites: spriteSheet }));
  });

  it("does not load sprites for a video without files", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Missing media",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail();

    expect(await screen.findByTestId("video-detail-player-area")).toBeInTheDocument();
    expect(videoSpritesMock).toHaveBeenCalledWith(null);
    expect(videoSpritesMock).not.toHaveBeenCalledWith(14);
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
      files: [
        {
          format: "mp4",
          duration: 120,
          width: 1920,
          height: 1080,
          frameRate: 30,
          captions: [],
        },
      ],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail(15);

    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(
      expect.objectContaining({
        videoId: 15,
        clip: { start: 30, end: 60, loop: false },
      }),
    );
  });

  it("passes an explicit route timestamp separately from saved resume state", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Timestamped video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [
        {
          format: "mp4",
          duration: 120,
          width: 1920,
          height: 1080,
          frameRate: 30,
          captions: [],
        },
      ],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail(14, 42.5);

    expect(await screen.findByTestId("video-detail-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(
      expect.objectContaining({
        seekTo: 42.5,
        resumeTime: undefined,
      }),
    );
  });

  it("shows the icon each video extension tab declares", async () => {
    const extensionTab = { pageType: "video", extensionId: "ext.tabs", componentName: "Panel", order: 100 };
    extensionTabsMock.tabs = [
      { ...extensionTab, key: "cuts", label: "Cuts", icon: "scissors" },
      { ...extensionTab, key: "songs", label: "Songs", icon: "music" },
      { ...extensionTab, key: "plain", label: "Plain" },
    ];
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Video with extension tabs",
      files: [],
      performers: [],
      tags: [],
      galleries: [],
      groups: [],
      urls: [],
      remoteIds: [],
    });

    renderVideoDetail();

    const cuts = await screen.findByRole("tab", { name: "Cuts" });
    const songs = screen.getByRole("tab", { name: "Songs" });
    await waitFor(() => expect(cuts.querySelector(".lucide-scissors")).not.toBeNull());
    await waitFor(() => expect(songs.querySelector(".lucide-music")).not.toBeNull());
    // A tab without a declared icon is left to the layout's own default.
    expect(screen.getByRole("tab", { name: "Plain" }).querySelector("svg")).toBeNull();
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

  it("saves only the fields the user changed", async () => {
    const video = {
      id: 14,
      title: "Editable video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      date: "2026-07-01",
      files: [],
      performers: [],
      tags: [],
      galleries: [],
      groups: [],
      urls: ["https://example.com/video/14"],
      remoteIds: [],
      customFields: { mood: "calm" },
      contextTagApplications: [],
    };
    mockVideos.get.mockResolvedValue(video);
    mockVideos.update.mockResolvedValue(video);

    renderVideoDetail();

    fireEvent.click(await screen.findByRole("tab", { name: "Edit" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Title" }), { target: { value: "Renamed video" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockVideos.update).toHaveBeenCalledWith(14, { title: "Renamed video" }));
  });

  it("saves and clears the production date without touching the release date", async () => {
    const video = {
      id: 15,
      title: "Editable video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      date: "2026-07-01",
      productionDate: "2025-11",
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

    renderVideoDetail(15);

    fireEvent.click(await screen.findByRole("tab", { name: "Edit" }));
    const productionDate = screen.getByRole("textbox", { name: /^Production Date/ });
    expect(productionDate).toHaveValue("2025-11");
    fireEvent.change(productionDate, { target: { value: "2025-11-20" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(mockVideos.update).toHaveBeenCalledWith(15, { productionDate: "2025-11-20" }));

    fireEvent.change(screen.getByRole("textbox", { name: /^Production Date/ }), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(mockVideos.update).toHaveBeenLastCalledWith(15, { clearFields: ["productionDate"] }));
  });

  it.each([
    ["the production date", "2018-06-01", "18 years old"],
    ["the release date without a production date", undefined, "26 years old"],
  ])("shows performer ages on %s", async (_case, productionDate, expectedAge) => {
    layoutMock.renderDetailsTab = true;
    mockVideos.get.mockResolvedValue({
      id: 16,
      title: "Dated video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      date: "2026-07-01",
      productionDate,
      files: [],
      performers: [{ id: 3, name: "Dated Performer", birthdate: "2000-01-15", favorite: false, tags: [] }],
      tags: [],
      galleries: [],
      groups: [],
      urls: [],
      remoteIds: [],
      contextTagApplications: [],
    });

    renderVideoDetail(16);

    expect(await screen.findByText(expectedAge)).toBeInTheDocument();
  });

  it("keeps unsaved edits and follows other changes when the video refetches", async () => {
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

    const { queryClient } = renderVideoDetail();

    fireEvent.click(await screen.findByRole("tab", { name: "Edit" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Title" }), { target: { value: "Unsaved draft" } });
    // For example after Mark organized or a finished background job refreshes the video.
    await act(async () => {
      queryClient.setQueryData(["video", 14], { ...video, organized: true, director: "Scraped director" });
      // Query observers are notified on the next tick.
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    expect(screen.getByRole("textbox", { name: "Title" })).toHaveValue("Unsaved draft");
    // An untouched field shows the refetched value, and saving sends only what the user changed.
    expect(screen.getByRole("textbox", { name: "Director" })).toHaveValue("Scraped director");
    mockVideos.update.mockResolvedValue(video);
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(mockVideos.update).toHaveBeenCalledWith(14, { title: "Unsaved draft" }));
  });

  it("fills the edit form from the next video when the page moves on", async () => {
    mockVideos.get.mockImplementation(async (videoId: number) => ({
      id: videoId,
      title: `Queued video ${videoId}`,
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
    }));

    const { rerenderVideoDetail } = renderVideoDetail();
    fireEvent.click(await screen.findByRole("tab", { name: "Edit" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Title" }), { target: { value: "Unsaved draft" } });

    rerenderVideoDetail(15);

    await waitFor(() => expect(screen.getByRole("textbox", { name: "Title" })).toHaveValue("Queued video 15"));
  });

  it("continues from the saved video after saving", async () => {
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
    // The server stores what was sent in its own form, as it does with lists in display order.
    mockVideos.update.mockImplementation(async () => {
      mockVideos.get.mockResolvedValue({ ...video, title: "Renamed video (normalized)" });
      return { ...video, title: "Renamed video (normalized)" };
    });

    renderVideoDetail();

    fireEvent.click(await screen.findByRole("tab", { name: "Edit" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Title" }), { target: { value: "Renamed video" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(mockVideos.update).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(mockVideos.get).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getByRole("button", { name: "Save" })).toBeEnabled());
    await waitFor(() =>
      expect(screen.getByRole("textbox", { name: "Title" })).toHaveValue("Renamed video (normalized)"),
    );

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    // The second save compares against the saved video, so nothing is resent.
    await waitFor(() => expect(mockVideos.update).toHaveBeenCalledTimes(2));
    expect(mockVideos.update).toHaveBeenLastCalledWith(14, {});
  });

  it("refreshes a gallery the video was unlinked from", async () => {
    const video = {
      id: 14,
      title: "Editable video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [],
      performers: [],
      tags: [],
      galleries: [{ id: 3 }],
      groups: [],
      urls: [],
      remoteIds: [],
      contextTagApplications: [],
    };
    mockVideos.get.mockResolvedValue(video);
    mockVideos.update.mockResolvedValue(video);

    const { queryClient } = renderVideoDetail();
    const invalidateQueries = vi.spyOn(queryClient, "invalidateQueries");

    fireEvent.click(await screen.findByRole("tab", { name: "Edit" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove gallery" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(mockVideos.update).toHaveBeenCalledWith(14, { galleryIds: [] }));
    await waitFor(() => expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ["gallery", 3] }));
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ["gallery-videos", 3] });
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
    mockVideos.get.mockRejectedValueOnce(new Error("API Error 502: upstream API Error 404")).mockResolvedValueOnce({
      id: 14,
      title: "Recovered video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [
        {
          format: "mp4",
          duration: 120,
          width: 1920,
          height: 1080,
          frameRate: 30,
          captions: [],
        },
      ],
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
        files: [
          {
            format: "mp4",
            duration: 120,
            width: 1920,
            height: 1080,
            frameRate: 30,
            captions: [],
          },
        ],
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

  it("offers a draft submission to the configured metadata servers from the operations menu", async () => {
    appConfigMock.config = {
      ui: {},
      scraping: { metadataServers: [{ name: "First provider", endpoint: "https://first.example/graphql" }] },
    };
    mockVideos.get.mockImplementation((id: number) =>
      Promise.resolve({
        id,
        title: `Video ${id}`,
        organized: false,
        updatedAt: "2026-07-11T00:00:00Z",
        files: [],
        performers: [],
        tags: [],
        contextTagApplications: [],
      }),
    );

    const { rerenderVideoDetail } = renderVideoDetail();
    fireEvent.click(await screen.findByTitle("Operations"));
    fireEvent.click(screen.getByRole("button", { name: "Submit Draft…" }));

    const dialog = await screen.findByRole("dialog", { name: "Submit Draft" });
    expect(dialog).toHaveTextContent("First provider");

    // The dialog belongs to the video it was opened on; moving to the next one must not retarget it.
    rerenderVideoDetail(15);
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Submit Draft" })).not.toBeInTheDocument());
  });

  it("does not offer a draft submission when no metadata server is configured", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Detail video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [],
      performers: [],
      tags: [],
      contextTagApplications: [],
    });

    renderVideoDetail();
    fireEvent.click(await screen.findByTitle("Operations"));

    expect(screen.getByRole("button", { name: /Merge/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Submit Draft…" })).not.toBeInTheDocument();
  });

  it("opens the segments tab with the add form at the current time from the add-segment shortcut", async () => {
    authMock.extraPermissions = new Set(["segments.read", "segments.write"]);
    layoutMock.renderDetailsTab = true;
    Object.assign(mockVideos, {
      segments: { list: vi.fn().mockResolvedValue([]), spans: vi.fn().mockResolvedValue({ spans: [] }) },
      detections: { list: vi.fn().mockResolvedValue([]) },
    });
    Object.assign(segmentDisplayProfiles, { list: vi.fn().mockResolvedValue([]) });
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Segmented video",
      organized: false,
      updatedAt: "2026-07-11T00:00:00Z",
      files: [{ format: "mp4", duration: 120, width: 1920, height: 1080, frameRate: 30, captions: [] }],
      performers: [],
      tags: [],
      galleries: [],
      groups: [],
      urls: [],
      remoteIds: [],
      contextTagApplications: [],
    });

    renderVideoDetail();
    await waitFor(() => expect(videoPlayerMock).toHaveBeenCalled());
    const onTimeUpdate = videoPlayerMock.mock.calls.at(-1)?.[0].onTimeUpdate as (time: number) => void;
    act(() => onTimeUpdate(83.4));

    const shortcut = layoutShortcutsMock.mock.calls
      .at(-1)?.[0]
      .find((candidate) => candidate.id === "detail.video.addSegment");
    expect(shortcut).toBeDefined();
    act(() => shortcut!.handler());

    expect(await screen.findByRole("tab", { name: /^Segments/, selected: true })).toBeInTheDocument();
    await waitFor(() =>
      expect(segmentsPanelMock).toHaveBeenLastCalledWith(
        expect.objectContaining({ addRequested: true, currentTime: 83.4, canEdit: true }),
      ),
    );

    // The panel clears the request once it has opened the form, so revisiting the tab does not reopen it.
    const { onAddRequestHandled } = segmentsPanelMock.mock.calls.at(-1)![0] as { onAddRequestHandled: () => void };
    act(() => onAddRequestHandled());
    expect(segmentsPanelMock).toHaveBeenLastCalledWith(expect.objectContaining({ addRequested: false }));
  });
});
