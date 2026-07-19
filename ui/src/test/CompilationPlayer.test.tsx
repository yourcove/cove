import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { CompilationPlayer } from "../components/CompilationPlayer";

const { mockVideos, mockUiConfig } = vi.hoisted(() => ({
  mockVideos: {
    get: vi.fn(),
    streamUrl: vi.fn((id: number) => `/video-${id}.mp4`),
    screenshotUrl: vi.fn((id: number) => `/video-${id}.jpg`),
  },
  mockUiConfig: { autostartVideo: true },
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
  VideoPlayer: ({ autostart }: { autostart?: boolean }) => <div data-testid="compilation-video-player" data-autostart={String(autostart)}>Video Player</div>,
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

  it("leaves compilation playback paused when automatic playback is disabled", async () => {
    mockUiConfig.autostartVideo = false;
    mockVideos.get.mockResolvedValue({
      id: 14,
      files: [{ format: "mp4", duration: 120, captions: [] }],
    });

    renderPlayer();

    expect(await screen.findByTestId("compilation-video-player")).toHaveAttribute("data-autostart", "false");
  });
});
