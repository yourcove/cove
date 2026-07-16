import React from "react";
import { act, render, waitFor } from "@testing-library/react";
import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoPlayer } from "../components/VideoPlayer";

const { mockPlaybackTracker } = vi.hoisted(() => ({
  mockPlaybackTracker: {
    getSessionId: vi.fn(() => "session-1"),
    setTarget: vi.fn(() => Promise.resolve()),
    recordInterval: vi.fn(),
    flush: vi.fn(() => Promise.resolve()),
    dispose: vi.fn(() => Promise.resolve()),
  },
}));

vi.mock("../utils/interactionTracking", async () => {
  const actual = await vi.importActual<typeof import("../utils/interactionTracking")>("../utils/interactionTracking");
  return {
    ...actual,
    createPlaybackTracker: () => mockPlaybackTracker,
  };
});

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: {} } }),
}));

const playMock = vi.fn(() => Promise.resolve());
const pauseMock = vi.fn();
const loadMock = vi.fn();
const localStorageMock = {
  getItem: vi.fn(() => null),
  setItem: vi.fn(),
  removeItem: vi.fn(),
};

class ResizeObserverMock {
  observe() {}
  unobserve() {}
  disconnect() {}
}

beforeAll(() => {
  vi.stubGlobal("ResizeObserver", ResizeObserverMock);
  vi.stubGlobal("localStorage", localStorageMock);

  Object.defineProperty(HTMLMediaElement.prototype, "play", {
    configurable: true,
    writable: true,
    value: playMock,
  });
  Object.defineProperty(HTMLMediaElement.prototype, "pause", {
    configurable: true,
    writable: true,
    value: pauseMock,
  });
  Object.defineProperty(HTMLMediaElement.prototype, "load", {
    configurable: true,
    writable: true,
    value: loadMock,
  });
});

describe("VideoPlayer source lifecycle", () => {
  beforeEach(() => {
    playMock.mockClear();
    pauseMock.mockClear();
    loadMock.mockClear();
    mockPlaybackTracker.setTarget.mockClear();
    mockPlaybackTracker.recordInterval.mockClear();
    mockPlaybackTracker.flush.mockClear();
  });

  it("restores the rendered source after StrictMode replays mount effects", async () => {
    const { container } = render(
      <React.StrictMode>
        <VideoPlayer
          streamUrl="/api/stream/video/1"
          format="mp4"
          duration={120}
          videoId={1}
          detections={[]}
          trackingEnabled={false}
        />
      </React.StrictMode>,
    );

    const source = container.querySelector("source");
    expect(source).toBeInstanceOf(HTMLSourceElement);

    await waitFor(() => {
      expect(source).toHaveAttribute("src", "/api/stream/video/1");
      expect(source).toHaveAttribute("type", "video/mp4");
    });
  });

  it("does not declare a misleading MIME type for unknown direct video streams", () => {
    const { container } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/8912"
        format="mpegts"
        duration={120}
        videoId={8912}
        detections={[]}
        trackingEnabled={false}
      />,
    );

    const source = container.querySelector("source");
    expect(source).toBeInstanceOf(HTMLSourceElement);
    expect(source).not.toHaveAttribute("type");
  });

  it("does not carry the previous video's end position into a different video", async () => {
    const { container, rerender } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    video.currentTime = 119.75;

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/2"
        format="mp4"
        duration={90}
        videoId={2}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    act(() => {
      video.currentTime = 0;
      video.dispatchEvent(new Event("loadedmetadata"));
    });

    await waitFor(() => expect(video.currentTime).toBe(0));
  });

  it("clears stale playing state when loading a different video", async () => {
    const { container, rerender } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;

    act(() => video.dispatchEvent(new Event("play")));
    expect(container.querySelector(".lucide-pause")).toBeInTheDocument();
    playMock.mockClear();

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/2"
        format="mp4"
        duration={90}
        videoId={2}
        detections={[]}
        trackingEnabled={false}
        autostart
      />,
    );

    await waitFor(() => expect(container.querySelector(".lucide-play")).toBeInTheDocument());
    act(() => video.dispatchEvent(new Event("loadedmetadata")));
    await waitFor(() => expect(playMock).toHaveBeenCalledTimes(1));
  });

  it("preserves position when the same video's source reloads", async () => {
    const { container, rerender } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1?token=first"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    video.currentTime = 47;

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1?token=second"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    act(() => {
      video.currentTime = 0;
      video.dispatchEvent(new Event("loadedmetadata"));
    });

    await waitFor(() => expect(video.currentTime).toBe(47));
  });

  it("does not re-seek when playback is within clip-start precision tolerance", () => {
    const { container } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={600}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        clip={{ start: 260.28, end: 424.454, loop: false }}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    let currentTime = 260.279999;
    const currentTimeSet = vi.fn((nextTime: number) => {
      currentTime = nextTime;
    });
    Object.defineProperty(video, "currentTime", {
      configurable: true,
      get: () => currentTime,
      set: currentTimeSet,
    });

    act(() => video.dispatchEvent(new Event("timeupdate")));

    expect(currentTimeSet).not.toHaveBeenCalled();

    currentTime = 260.229;
    act(() => video.dispatchEvent(new Event("timeupdate")));

    expect(currentTimeSet).toHaveBeenCalledOnce();
    expect(currentTimeSet).toHaveBeenCalledWith(260.28);
  });

  it("does not flush playback when equivalent tracking props are recreated", async () => {
    Object.defineProperty(document, "hidden", { configurable: true, value: false });
    const renderPlayer = () => (
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        playbackTracking={{
          hostType: "video",
          hostId: 1,
          surface: "resolvedSpan",
          scopeKey: "video:1:span:test",
          clipStartSec: 5,
          clipEndSec: 25,
          context: { intervalIndex: 0 },
        }}
        clip={{ start: 5, end: 25, loop: false }}
      />
    );
    const { container, rerender } = render(renderPlayer());
    const video = container.querySelector("video") as HTMLVideoElement;

    act(() => {
      video.currentTime = 5;
      video.dispatchEvent(new Event("play"));
      video.currentTime = 6;
      video.dispatchEvent(new Event("timeupdate"));
    });

    rerender(renderPlayer());
    await act(async () => Promise.resolve());

    expect(mockPlaybackTracker.recordInterval).not.toHaveBeenCalled();
    expect(mockPlaybackTracker.flush).not.toHaveBeenCalled();
  });

  it("flushes playback when tracking context meaningfully changes", async () => {
    Object.defineProperty(document, "hidden", { configurable: true, value: false });
    const renderPlayer = (intervalIndex: number) => (
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        playbackTracking={{
          hostType: "video",
          hostId: 1,
          surface: "resolvedSpan",
          scopeKey: "video:1:span:test",
          clipStartSec: intervalIndex === 0 ? 5 : 20,
          clipEndSec: intervalIndex === 0 ? 10 : 25,
          context: { intervalIndex },
        }}
        clip={{ start: intervalIndex === 0 ? 5 : 20, end: intervalIndex === 0 ? 10 : 25, loop: false }}
      />
    );
    const { container, rerender } = render(renderPlayer(0));
    const video = container.querySelector("video") as HTMLVideoElement;
    let paused = false;
    Object.defineProperty(video, "paused", { configurable: true, get: () => paused });

    act(() => {
      video.currentTime = 5;
      video.dispatchEvent(new Event("play"));
      video.currentTime = 6;
      video.dispatchEvent(new Event("timeupdate"));
    });

    rerender(renderPlayer(1));
    await act(async () => Promise.resolve());

    expect(mockPlaybackTracker.recordInterval).toHaveBeenCalledTimes(1);
    expect(mockPlaybackTracker.flush).toHaveBeenCalledTimes(1);
    expect(mockPlaybackTracker.recordInterval).toHaveBeenNthCalledWith(1, expect.objectContaining({
      startSec: 5,
      endSec: 6,
    }));

    act(() => {
      video.currentTime = 21;
      video.dispatchEvent(new Event("timeupdate"));
      paused = true;
      video.dispatchEvent(new Event("pause"));
    });

    expect(mockPlaybackTracker.recordInterval).toHaveBeenCalledTimes(2);
    expect(mockPlaybackTracker.recordInterval).toHaveBeenNthCalledWith(2, expect.objectContaining({
      startSec: 20,
      endSec: 21,
    }));
  });
});
