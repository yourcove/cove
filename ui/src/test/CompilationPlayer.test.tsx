import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
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
  VideoPlayer: (props: { autostart?: boolean; posterUrl?: string; videoId: number; clip?: { start: number; end?: number | null; loop?: boolean }; onPlay?: () => void; onPause?: () => void; onPlaybackStateChange?: (playing: boolean) => void }) => {
    videoPlayerMock(props);
    return (
      <div data-testid="compilation-video-player" data-autostart={String(props.autostart)} data-poster={props.posterUrl} data-video-id={props.videoId}>
      Video Player
        <button type="button" onClick={() => { props.onPlaybackStateChange?.(true); props.onPlay?.(); }}>Simulate play</button>
        <button type="button" onClick={() => { props.onPlaybackStateChange?.(false); props.onPause?.(); }}>Simulate pause</button>
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
    expect(videoPlayerMock).toHaveBeenCalledWith(expect.objectContaining({
      videoId: 14,
      extensionSurface: "compilation",
      interactionResetKey: "group:9:item:1",
    }));
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
    expect(videoPlayerMock).toHaveBeenCalledWith(expect.objectContaining({
      videoId: 14,
      clip: { start: 35, end: 45, loop: false },
    }));
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
          items={[{
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
          }]}
          onNavigate={vi.fn()}
        />
      </QueryClientProvider>,
    );

    expect(await screen.findByTestId("compilation-video-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(expect.objectContaining({
      clip: { start: 30, end: 60, loop: false },
    }));
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
    mockVideos.get.mockImplementation(async (id: number) => ({ id, files: [{ format: "mp4", duration: 120, captions: [] }] }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <CompilationPlayer
          groupId={9}
          groupName="Summer Compilation"
          items={[14, 15, 16].map((videoId, index) => ({
            groupItemId: index + 1, hostType: "segment", hostId: index + 101, videoId, audioId: null,
            title: `Clip ${index + 1}`, src: `/video-${videoId}.mp4`, startSec: 5, endSec: 15, durationSec: 10, hasVideoTrack: false,
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
});
