import React from "react";
import { act, fireEvent, render, waitFor } from "@testing-library/react";
import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoPlayer, type VideoPlayerPlaybackControls } from "../components/VideoPlayer";

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
    playMock.mockReset();
    playMock.mockResolvedValue(undefined);
    pauseMock.mockClear();
    loadMock.mockClear();
    mockPlaybackTracker.setTarget.mockClear();
    mockPlaybackTracker.recordInterval.mockClear();
    mockPlaybackTracker.flush.mockClear();
  });

  it("allows mobile browsers to play video inline", () => {
    const { container } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );

    expect(container.querySelector("video")).toHaveAttribute("playsinline");
  });

  it("uses native video fullscreen when container fullscreen is unavailable", () => {
    const enterFullscreen = vi.fn();
    const { container } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement & {
      webkitEnterFullscreen?: () => void;
    };
    video.webkitEnterFullscreen = enterFullscreen;

    const fullscreenButton = container.querySelector(
      'button[aria-label="Enter fullscreen"]',
    );
    expect(fullscreenButton).toBeInstanceOf(HTMLButtonElement);
    fireEvent.click(fullscreenButton as HTMLButtonElement);

    expect(enterFullscreen).toHaveBeenCalledOnce();
  });

  it("keeps controls compact below the md breakpoint and exposes secondary playback options", () => {
    const { container, getByRole, getByText, queryByText } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );

    expect(container.querySelector('button[title="Back 10s"]')).toHaveClass("hidden", "md:inline-flex");
    expect(container.querySelector('button[title="Forward 10s"]')).toHaveClass("hidden", "md:inline-flex");
    expect(getByText("0:00 / 2:00")).toBeInTheDocument();

    const optionsButton = getByRole("button", { name: "Playback options" });
    expect(optionsButton.parentElement).toHaveClass("md:hidden");
    vi.spyOn(optionsButton, "getBoundingClientRect").mockReturnValue({
      x: 340,
      y: 700,
      top: 700,
      right: 364,
      bottom: 724,
      left: 340,
      width: 24,
      height: 24,
      toJSON: () => ({}),
    });
    const originalInnerHeight = window.innerHeight;
    Object.defineProperty(window, "innerHeight", { configurable: true, value: 740 });

    fireEvent.click(optionsButton);

    expect(getByRole("button", { name: "Direct" })).toBeInTheDocument();
    expect(getByText("Playback speed")).toBeInTheDocument();
    expect(getByText("Loop video")).toBeInTheDocument();
    expect(getByText("Picture-in-Picture")).toBeInTheDocument();
    const optionsMenu = getByRole("dialog", { name: "Playback options menu" });
    expect(optionsMenu).toHaveStyle({
      bottom: "48px",
      maxHeight: "684px",
    });

    fireEvent.scroll(optionsMenu);
    expect(getByText("Playback speed")).toBeInTheDocument();

    fireEvent.scroll(window);
    expect(queryByText("Playback speed")).not.toBeInTheDocument();

    fireEvent.click(optionsButton);
    fireEvent.pointerDown(document.body);

    expect(queryByText("Playback speed")).not.toBeInTheDocument();
    Object.defineProperty(window, "innerHeight", { configurable: true, value: originalInnerHeight });
  });

  it("lets registered seeks preserve pause state when requested", async () => {
    let seek: ((time: number, forcePlay?: boolean) => void) | undefined;
    const { container } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        onSeekRegister={(registered) => { seek = registered; }}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    await waitFor(() => expect(seek).toBeTypeOf("function"));

    act(() => seek?.(25, false));

    expect(video.currentTime).toBe(25);
    expect(playMock).not.toHaveBeenCalled();

    act(() => seek?.(30));
    expect(playMock).toHaveBeenCalledOnce();
  });

  it("registers playback controls for keyboard-driven extension editors", async () => {
    let controls: VideoPlayerPlaybackControls | undefined;
    const unregister = vi.fn();
    const { container, unmount } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        onPlaybackControlRegister={(registered) => {
          controls = registered;
          return unregister;
        }}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    await waitFor(() => expect(controls).toBeDefined());

    video.currentTime = 20;
    act(() => controls?.seekBy(5));
    expect(video.currentTime).toBe(25);
    expect(playMock).not.toHaveBeenCalled();
    act(() => video.dispatchEvent(new Event("timeupdate")));
    expect(unregister).not.toHaveBeenCalled();

    await act(async () => {
      await controls?.play();
    });
    expect(playMock).toHaveBeenCalledOnce();
    act(() => controls?.pause());
    expect(pauseMock).toHaveBeenCalledOnce();

    Object.defineProperty(video, "paused", { configurable: true, value: true });
    act(() => controls?.toggle());
    expect(playMock).toHaveBeenCalledTimes(2);

    playMock.mockRejectedValueOnce(new Error("Playback was blocked"));
    act(() => controls?.toggle());
    await act(async () => {
      await Promise.resolve();
    });

    unmount();
    expect(unregister).toHaveBeenCalledOnce();
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

  it("preserves pending autoplay when equivalent clip props are recreated before metadata loads", async () => {
    const renderPlayer = () => (
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        autostart
        clip={{ start: 5, end: 25, loop: false }}
      />
    );
    const { container, rerender } = render(renderPlayer());
    const video = container.querySelector("video") as HTMLVideoElement;

    rerender(renderPlayer());
    expect(loadMock).toHaveBeenCalledOnce();
    act(() => video.dispatchEvent(new Event("loadedmetadata")));

    await waitFor(() => expect(playMock).toHaveBeenCalledOnce());
  });

  it("cancels pending autoplay when it is disabled before metadata loads", () => {
    const renderPlayer = (autostart: boolean) => (
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        autostart={autostart}
        clip={{ start: 5, end: 25, loop: false }}
      />
    );
    const { container, rerender } = render(renderPlayer(true));
    const video = container.querySelector("video") as HTMLVideoElement;

    rerender(renderPlayer(false));
    act(() => video.dispatchEvent(new Event("loadedmetadata")));

    expect(playMock).not.toHaveBeenCalled();
  });

  it("starts pending autoplay once when it is enabled before metadata loads", async () => {
    const renderPlayer = (autostart: boolean) => (
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        autostart={autostart}
        clip={{ start: 5, end: 25, loop: false }}
      />
    );
    const { container, rerender } = render(renderPlayer(false));
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "readyState", { configurable: true, value: 1 });

    rerender(renderPlayer(true));
    act(() => video.dispatchEvent(new Event("loadedmetadata")));

    await waitFor(() => expect(playMock).toHaveBeenCalledOnce());
  });

  it("replaces pending autoplay metadata handling when the clip start changes", async () => {
    const renderPlayer = (clipStart: number) => (
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        autostart
        clip={{ start: clipStart, end: 25, loop: false }}
      />
    );
    const { container, rerender } = render(renderPlayer(5));
    const video = container.querySelector("video") as HTMLVideoElement;

    rerender(renderPlayer(20));
    act(() => video.dispatchEvent(new Event("loadedmetadata")));

    await waitFor(() => expect(playMock).toHaveBeenCalledOnce());
    expect(video.currentTime).toBe(20);
  });

  it("consumes pending metadata work when metadata is ready before its event is delivered", async () => {
    const renderPlayer = (clipStart: number) => (
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        autostart
        clip={{ start: clipStart, end: 25, loop: false }}
      />
    );
    const { container, rerender } = render(renderPlayer(5));
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "readyState", { configurable: true, value: 1 });

    rerender(renderPlayer(20));
    act(() => video.dispatchEvent(new Event("loadedmetadata")));

    await waitFor(() => expect(playMock).toHaveBeenCalledOnce());
    expect(video.currentTime).toBe(20);
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
