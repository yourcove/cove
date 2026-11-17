import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { CompilationPlayer } from "../components/CompilationPlayer";

const { mockVideos, mockUiConfig, videoPlayerMock } = vi.hoisted(() => ({
  mockVideos: {
    get: vi.fn(),
    streamUrl: vi.fn((id: number) => `/video-${id}.mp4`),
    screenshotUrl: vi.fn((id: number) => `/video-${id}.jpg`),
  },
  mockUiConfig: { autostartVideo: true },
  videoPlayerMock: vi.fn(),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: mockUiConfig }, configLoading: false }),
}));

vi.mock("../api/client", () => ({
  audios: {
    get: vi.fn(),
    streamUrl: vi.fn((id: number) => `/audio-${id}.mp3`),
  },
  images: {
    imageUrl: vi.fn((id: number) => `/image-${id}.jpg`),
  },
  videos: mockVideos,
  texts: {
    content: vi.fn(),
    fileUrl: vi.fn((id: number) => `/text-${id}`),
  },
}));

vi.mock("../components/VideoPlayer", () => ({
  VideoPlayer: (props: {
    autostart?: boolean;
    posterUrl?: string;
    videoId: number;
    clip?: { start: number; end?: number | null; loop?: boolean };
    onPlay?: () => void;
    onPause?: () => void;
    onPlaybackStateChange?: (playing: boolean) => void;
  }) => {
    videoPlayerMock(props);
    return (
      <div
        data-testid="compilation-video-player"
        data-autostart={String(props.autostart)}
        data-poster={props.posterUrl}
        data-video-id={props.videoId}
      >
        Video Player
        <button
          type="button"
          onClick={() => {
            props.onPlaybackStateChange?.(true);
            props.onPlay?.();
          }}
        >
          Simulate play
        </button>
        <button
          type="button"
          onClick={() => {
            props.onPlaybackStateChange?.(false);
            props.onPause?.();
          }}
        >
          Simulate pause
        </button>
      </div>
    );
  },
}));

function renderPlayer() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  render(
    <QueryClientProvider client={queryClient}>
      <CompilationPlayer
        groupId={9}
        groupName="Summer Compilation"
        items={[
          {
            groupItemId: 1,
            hostType: "video",
            hostId: 14,
            videoId: 14,
            audioId: null,
            title: "Clip One",
            src: "/video-14.mp4",
            startSec: 5,
            endSec: 15,
            durationSec: 10,
            hasVideoTrack: false,
          },
        ]}
        onNavigate={vi.fn()}
        backLabel="Back to group"
        onGoBack={vi.fn()}
      />
    </QueryClientProvider>,
  );
}

describe("CompilationPlayer", () => {
  afterEach(() => {
    vi.clearAllMocks();
    mockUiConfig.autostartVideo = true;
  });

  it("uses the shared full-bleed media surface without the framed video wrapper", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    });

    renderPlayer();

    expect(await screen.findByRole("heading", { name: "Summer Compilation" })).toBeInTheDocument();
    expect(await screen.findByTestId("compilation-video-player")).toBeInTheDocument();
    expect(screen.queryByTestId("media-detail-layout-media-frame")).not.toBeInTheDocument();
  });

  it("honors the automatic playback preference when compilation playback starts", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    });

    renderPlayer();

    expect(await screen.findByTestId("compilation-video-player")).toHaveAttribute("data-autostart", "true");
  });

  it("opts video playback into the compilation extension surface", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    });

    renderPlayer();

    expect(await screen.findByTestId("compilation-video-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(
      expect.objectContaining({
        videoId: 14,
        extensionSurface: "compilation",
        interactionResetKey: "group:9:item:1",
      }),
    );
  });

  it("offsets compilation ranges into a sub-video's parent clip", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      parentVideoId: 10,
      clipStartSec: 30,
      clipEndSec: 60,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    });

    renderPlayer();

    expect(await screen.findByTestId("compilation-video-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(
      expect.objectContaining({
        videoId: 14,
        clip: { start: 35, end: 45, loop: false },
      }),
    );
  });

  it("shows the bounded duration for a whole sub-video compilation item", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      parentVideoId: 10,
      clipStartSec: 30,
      clipEndSec: 60,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <CompilationPlayer
          groupId={9}
          groupName="Summer Compilation"
          items={[
            {
              groupItemId: 1,
              hostType: "video",
              hostId: 14,
              videoId: 14,
              audioId: null,
              title: "Whole sub-video",
              src: "/video-14.mp4",
              startSec: 0,
              endSec: null,
              durationSec: 120,
              hasVideoTrack: false,
            },
          ]}
          onNavigate={vi.fn()}
        />
      </QueryClientProvider>,
    );

    expect(await screen.findByTestId("compilation-video-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(
      expect.objectContaining({
        clip: { start: 30, end: 60, loop: false },
      }),
    );
    expect(screen.getByText("0:30")).toBeInTheDocument();
  });

  it("leaves compilation playback paused when automatic playback is disabled", async () => {
    mockUiConfig.autostartVideo = false;
    mockVideos.get.mockResolvedValue({
      id: 14,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    });

    renderPlayer();

    expect(await screen.findByTestId("compilation-video-player")).toHaveAttribute("data-autostart", "false");
  });

  it("does not flash the next item's poster during a video transition", async () => {
    mockVideos.get.mockImplementation(async (id: number) => ({
      id,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <CompilationPlayer
          groupId={9}
          groupName="Summer Compilation"
          items={[14, 15].map((videoId, index) => ({
            groupItemId: index + 1,
            hostType: "segment",
            hostId: index + 101,
            videoId,
            audioId: null,
            title: `Clip ${index + 1}`,
            src: `/video-${videoId}.mp4`,
            startSec: 5,
            endSec: 15,
            durationSec: 10,
            hasVideoTrack: false,
          }))}
          onNavigate={vi.fn()}
        />
      </QueryClientProvider>,
    );

    expect(await screen.findByTestId("compilation-video-player")).toHaveAttribute("data-poster", "/video-14.jpg");
    fireEvent.click(screen.getByRole("button", { name: "Next item" }));

    expect(await screen.findByTestId("compilation-video-player")).not.toHaveAttribute("data-poster");
  });

  it("shows the destination poster when a playlist item is selected without autoplay", async () => {
    mockVideos.get.mockImplementation(async (id: number) => ({
      id,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <CompilationPlayer
          groupId={9}
          groupName="Summer Compilation"
          items={[14, 15].map((videoId, index) => ({
            groupItemId: index + 1,
            hostType: "segment",
            hostId: index + 101,
            videoId,
            audioId: null,
            title: `Clip ${index + 1}`,
            src: `/video-${videoId}.mp4`,
            startSec: 5,
            endSec: 15,
            durationSec: 10,
            hasVideoTrack: false,
          }))}
          onNavigate={vi.fn()}
        />
      </QueryClientProvider>,
    );

    await screen.findByTestId("compilation-video-player");
    fireEvent.click(screen.getByRole("button", { name: "Simulate play" }));
    fireEvent.click(screen.getByRole("button", { name: "Simulate pause" }));
    fireEvent.click(screen.getByRole("button", { name: /2\. Clip 2Segment/ }));

    expect(await screen.findByTestId("compilation-video-player")).toHaveAttribute("data-poster", "/video-15.jpg");
  });

  it("keeps playback active when a playing user selects another playlist item", async () => {
    mockVideos.get.mockImplementation(async (id: number) => ({
      id,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <CompilationPlayer
          groupId={9}
          groupName="Summer Compilation"
          items={[14, 15, 16].map((videoId, index) => ({
            groupItemId: index + 1,
            hostType: "segment",
            hostId: index + 101,
            videoId,
            audioId: null,
            title: `Clip ${index + 1}`,
            src: `/video-${videoId}.mp4`,
            startSec: 5,
            endSec: 15,
            durationSec: 10,
            hasVideoTrack: false,
          }))}
          onNavigate={vi.fn()}
        />
      </QueryClientProvider>,
    );

    await screen.findByTestId("compilation-video-player");
    fireEvent.click(screen.getByRole("button", { name: "Simulate play" }));
    fireEvent.click(screen.getByRole("button", { name: /2\. Clip 2Segment/ }));

    const player = await screen.findByTestId("compilation-video-player");
    expect(player).toHaveAttribute("data-video-id", "15");
    expect(player).toHaveAttribute("data-autostart", "true");
    expect(player).not.toHaveAttribute("data-poster");

    fireEvent.click(screen.getByRole("button", { name: "Simulate pause" }));
    fireEvent.click(screen.getByRole("button", { name: /3\. Clip 3Segment/ }));

    const nextPlayer = await screen.findByTestId("compilation-video-player");
    expect(nextPlayer).toHaveAttribute("data-video-id", "16");
    expect(nextPlayer).toHaveAttribute("data-autostart", "true");
    expect(nextPlayer).not.toHaveAttribute("data-poster");
  });

  it("links each segment row to its segment and to its video at the segment start", async () => {
    mockVideos.get.mockImplementation(async (id: number) => ({
      id,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    }));
    const onNavigate = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <CompilationPlayer
          groupId={9}
          groupName="Summer Compilation"
          items={[
            {
              groupItemId: 1,
              hostType: "segment",
              hostId: 101,
              segmentId: 101,
              videoId: 14,
              videoTitle: "Source Video",
              title: "Facial",
              src: "/video-14.mp4",
              startSec: 65,
              endSec: 75,
              durationSec: 10,
              hasVideoTrack: false,
            },
            {
              groupItemId: 2,
              hostType: "video",
              hostId: 15,
              videoId: 15,
              title: "Whole Video",
              src: "/video-15.mp4",
              startSec: 0,
              durationSec: 120,
              hasVideoTrack: false,
            },
            {
              groupItemId: 3,
              hostType: "segment",
              hostId: 102,
              segmentId: 102,
              audioId: 30,
              title: "Audio Moment",
              src: "",
              startSec: 4,
              endSec: 8,
              hasVideoTrack: false,
            },
          ]}
          onNavigate={onNavigate}
        />
      </QueryClientProvider>,
    );

    await screen.findByTestId("compilation-video-player");
    expect(
      screen.getByRole("button", { name: /^1\. Facial\s*Segment\s*Source Video · 1:05 - 1:15$/ }),
    ).toBeInTheDocument();

    const segmentLink = screen.getByRole("link", { name: "Open segment Facial" });
    expect(segmentLink).toHaveAttribute("href", "/segment/101");
    const videoLink = screen.getByRole("link", { name: "Open video Source Video at 1:05" });
    expect(videoLink).toHaveAttribute("href", expect.stringMatching(/^\/video\/14\?t=65/));

    // Only video-hosted segment rows get the links: the segment page cannot show an audio-hosted segment,
    // and other media keep the plain playlist row.
    expect(screen.getAllByRole("link", { name: /^Open (segment|video) / })).toHaveLength(2);
    expect(screen.getByRole("button", { name: /^1\. Facial/ })).toHaveAttribute("aria-current", "true");
    expect(screen.getByRole("button", { name: /^2\. Whole Video/ })).not.toHaveAttribute("aria-current");

    fireEvent.click(videoLink);
    expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 14, seekTo: 65 });

    // The Current tab opens the playing segment's video too, not only the segment.
    onNavigate.mockClear();
    fireEvent.click(screen.getAllByRole("tab", { name: "Current" })[0]);
    fireEvent.click(screen.getByRole("button", { name: "Open video" }));
    expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 14, seekTo: 65 });
  });

  it("plays an ad hoc list without a group, linking items to their own detail page", async () => {
    mockVideos.get.mockImplementation(async (id: number) => ({
      id,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    }));
    const onNavigate = vi.fn();
    const onNearEnd = vi.fn();
    const spanRoute = { page: "video-span", id: 14, spanKey: "span-1", profileId: 2 };
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <CompilationPlayer
          groupName="Segments"
          sourceKey="segments:test"
          returnTarget={{ route: { page: "segments" }, label: "Back to Segments" }}
          totalCount={250}
          onNearEnd={onNearEnd}
          items={[
            {
              groupItemId: 1,
              hostType: "segment",
              hostId: 14,
              videoId: 14,
              videoTitle: "Source Video",
              title: "Facial",
              src: "/video-14.mp4",
              startSec: 65,
              endSec: 75,
              hasVideoTrack: false,
              detailRoute: spanRoute,
            },
          ]}
          onNavigate={onNavigate}
        />
      </QueryClientProvider>,
    );

    await screen.findByTestId("compilation-video-player");
    expect(screen.getByText("1/250 items")).toBeInTheDocument();
    // Plays of an ad hoc list are not attributed to any group.
    expect(videoPlayerMock).toHaveBeenLastCalledWith(expect.objectContaining({ playbackTracking: undefined }));
    // Within a few items of the loaded end, the player asks for more.
    expect(onNearEnd).toHaveBeenCalled();

    expect(screen.getByRole("link", { name: "Open segment Facial" })).toHaveAttribute(
      "href",
      expect.stringMatching(/^\/video\/14\/span\/span-1/),
    );

    fireEvent.click(screen.getAllByRole("tab", { name: "Current" })[0]);
    fireEvent.click(screen.getByRole("button", { name: "Open item" }));
    expect(onNavigate).toHaveBeenLastCalledWith(spanRoute);
    fireEvent.click(screen.getByRole("button", { name: "Back to Segments" }));
    expect(onNavigate).toHaveBeenLastCalledWith({ page: "segments" });
    expect(screen.queryByRole("button", { name: "Back to group" })).not.toBeInTheDocument();
  });

  it("waits at the end of the loaded items for more instead of stopping or looping back", async () => {
    mockVideos.get.mockImplementation(async (id: number) => ({
      id,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    }));
    const segment = (index: number) => ({
      groupItemId: index,
      hostType: "segment",
      hostId: 100 + index,
      segmentId: 100 + index,
      videoId: 20 + index,
      title: `Clip ${index}`,
      src: `/video-${20 + index}.mp4`,
      startSec: 5,
      endSec: 15,
      hasVideoTrack: false,
    });
    const onNearEnd = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const renderList = (items: ReturnType<typeof segment>[], hasMore: boolean) => (
      <QueryClientProvider client={queryClient}>
        <CompilationPlayer
          groupName="Segments"
          sourceKey="segments:test"
          items={items}
          hasMore={hasMore}
          onNearEnd={onNearEnd}
          onNavigate={vi.fn()}
        />
      </QueryClientProvider>
    );
    const { rerender } = render(renderList([segment(1)], true));

    expect(await screen.findByTestId("compilation-video-player")).toHaveAttribute("data-video-id", "21");
    // The total is unknown, so the counts say there is more rather than claiming the list is complete.
    expect(screen.getByText("1/1+ items")).toBeInTheDocument();
    expect(screen.getByText("Now playing 1/1+")).toBeInTheDocument();

    // Looping is on, but the end of the loaded items is not the end of the list.
    fireEvent.click(screen.getByRole("button", { name: "Loop compilation" }));
    const next = screen.getByRole("button", { name: "Next item" });
    expect(next).toBeEnabled();
    fireEvent.click(next);
    expect(onNearEnd).toHaveBeenCalled();
    expect(screen.getByTestId("compilation-video-player")).toHaveAttribute("data-video-id", "21");

    rerender(renderList([segment(1), segment(2)], false));
    await waitFor(() => expect(screen.getByTestId("compilation-video-player")).toHaveAttribute("data-video-id", "22"));
    expect(screen.getByText("2/2 items")).toBeInTheDocument();
  });
});
