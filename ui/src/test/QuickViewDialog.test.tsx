import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { QuickViewDialog } from "../components/QuickViewDialog";

const { mockVideos, videoPlayerMock } = vi.hoisted(() => ({
  mockVideos: {
    get: vi.fn(),
    streamUrl: vi.fn((id: number) => `/video-${id}.mp4`),
    screenshotUrl: vi.fn((id: number) => `/video-${id}.jpg`),
  },
  videoPlayerMock: vi.fn(),
}));

vi.mock("../api/client", () => ({
  entityEngagement: { recordInteraction: vi.fn() },
  images: {
    get: vi.fn(),
    thumbnailUrl: vi.fn((id: number) => `/image-${id}.jpg`),
  },
  playback: { recordIntervals: vi.fn() },
  videos: mockVideos,
}));

vi.mock("../components/VideoPlayer", () => ({
  VideoPlayer: (props: Record<string, unknown>) => {
    videoPlayerMock(props);
    return <div data-testid="quick-view-video-player">Video Player</div>;
  },
}));

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({ engagement: undefined }),
}));

function renderQuickView() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  render(
    <QueryClientProvider client={queryClient}>
      <QuickViewDialog type="video" id={14} onClose={vi.fn()} onNavigate={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("QuickViewDialog media-player extension surface", () => {
  afterEach(() => {
    vi.clearAllMocks();
  });

  it("opts video playback into the quick-view extension surface", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Quick view video",
      updatedAt: "2026-07-11T00:00:00Z",
      files: [{ format: "mp4", duration: 120, captions: [] }],
      performers: [],
      tags: [],
    });

    renderQuickView();

    expect(await screen.findByTestId("quick-view-video-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(
      expect.objectContaining({
        videoId: 14,
        extensionSurface: "quick-view",
      }),
    );
  });

  it("constrains sub-video playback to its parent clip range", async () => {
    mockVideos.get.mockResolvedValue({
      id: 14,
      title: "Quick view sub-video",
      updatedAt: "2026-07-11T00:00:00Z",
      parentVideoId: 10,
      clipStartSec: 30,
      clipEndSec: 60,
      files: [{ format: "mp4", duration: 120, captions: [] }],
      performers: [],
      tags: [],
    });

    renderQuickView();

    expect(await screen.findByTestId("quick-view-video-player")).toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(
      expect.objectContaining({
        videoId: 14,
        clip: { start: 30, end: 60, loop: false },
      }),
    );
  });
});
