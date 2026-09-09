import React from "react";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoPlayer, type VideoPlayerPlaybackControls } from "../components/VideoPlayer";
import {
  getServerAvailability,
  reportServerResponse,
  resetServerAvailabilityForTests,
} from "../state/serverAvailability";

const { mockPlaybackTracker, mockUiConfig } = vi.hoisted(() => ({
  mockPlaybackTracker: {
    getSessionId: vi.fn(() => "session-1"),
    setTarget: vi.fn(() => Promise.resolve()),
    recordInterval: vi.fn(),
    flush: vi.fn(() => Promise.resolve()),
    dispose: vi.fn(() => Promise.resolve()),
  },
  mockUiConfig: {} as { alwaysResumeOnPlayback?: boolean },
}));

vi.mock("../utils/interactionTracking", async () => {
  const actual = await vi.importActual<typeof import("../utils/interactionTracking")>("../utils/interactionTracking");
  return {
    ...actual,
    createPlaybackTracker: () => mockPlaybackTracker,
  };
});

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: mockUiConfig } }),
}));

const playMock = vi.fn(() => Promise.resolve());
const pauseMock = vi.fn();
const loadMock = vi.fn();
const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(new Response(null, { status: 200 })));
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
  vi.stubGlobal("MediaError", {
    MEDIA_ERR_ABORTED: 1,
    MEDIA_ERR_NETWORK: 2,
    MEDIA_ERR_DECODE: 3,
    MEDIA_ERR_SRC_NOT_SUPPORTED: 4,
  });
  vi.stubGlobal("fetch", fetchMock);

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
    mockUiConfig.alwaysResumeOnPlayback = undefined;
    resetServerAvailabilityForTests();
    playMock.mockReset();
    playMock.mockResolvedValue(undefined);
    pauseMock.mockClear();
    loadMock.mockClear();
    fetchMock.mockReset();
    fetchMock.mockResolvedValue(new Response(null, { status: 200 }));
    mockPlaybackTracker.setTarget.mockClear();
    mockPlaybackTracker.recordInterval.mockClear();
    mockPlaybackTracker.flush.mockClear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("seeks to an explicit timestamp without playing when automatic resume is disabled", () => {
    mockUiConfig.alwaysResumeOnPlayback = false;
    const { container } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        resumeTime={20}
        seekTo={42.5}
        autostart
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    video.currentTime = 0;
    playMock.mockClear();

    fireEvent.loadedMetadata(video);

    expect(video.currentTime).toBe(42.5);
    expect(playMock).not.toHaveBeenCalled();
  });

  it("seeks to and plays an explicit timestamp when automatic resume is enabled", () => {
    mockUiConfig.alwaysResumeOnPlayback = true;
    const { container } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        resumeTime={20}
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    video.currentTime = 0;
    playMock.mockClear();

    fireEvent.loadedMetadata(video);

    expect(video.currentTime).toBe(42.5);
    expect(playMock).toHaveBeenCalledOnce();
  });

  it("uses saved resume time only when automatic resume is enabled", () => {
    mockUiConfig.alwaysResumeOnPlayback = true;
    const { container, rerender } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        resumeTime={20}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    video.currentTime = 0;
    fireEvent.loadedMetadata(video);
    expect(video.currentTime).toBe(20);

    mockUiConfig.alwaysResumeOnPlayback = false;
    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/2"
        format="mp4"
        duration={120}
        resumeTime={30}
        videoId={2}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    video.currentTime = 0;
    fireEvent.loadedMetadata(video);
    expect(video.currentTime).toBe(0);
  });

  it("applies a new timestamp intent to an already-loaded player", () => {
    mockUiConfig.alwaysResumeOnPlayback = true;
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
    Object.defineProperty(video, "readyState", { configurable: true, value: HTMLMediaElement.HAVE_METADATA });
    playMock.mockClear();

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );

    expect(video.currentTime).toBe(42.5);
    expect(playMock).toHaveBeenCalledOnce();

    mockUiConfig.alwaysResumeOnPlayback = false;
    playMock.mockClear();
    pauseMock.mockClear();
    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        seekTo={64}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );

    expect(video.currentTime).toBe(64);
    expect(playMock).not.toHaveBeenCalled();
    expect(pauseMock).toHaveBeenCalledOnce();
  });

  it("does not reapply timestamp autoplay after the user pauses and the source reloads", () => {
    mockUiConfig.alwaysResumeOnPlayback = true;
    const { container, rerender } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1?token=first"
        format="mp4"
        duration={120}
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    fireEvent.loadedMetadata(video);
    fireEvent.pause(video);
    video.currentTime = 60;
    playMock.mockClear();

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1?token=second"
        format="mp4"
        duration={120}
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    video.currentTime = 0;
    fireEvent.loadedMetadata(video);

    expect(video.currentTime).toBe(60);
    expect(playMock).not.toHaveBeenCalled();
  });

  it("cancels a pending timestamp intent when the timestamp is removed before metadata loads", () => {
    mockUiConfig.alwaysResumeOnPlayback = true;
    const { container, rerender } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        resumeTime={20}
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        resumeTime={20}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    video.currentTime = 0;
    playMock.mockClear();
    fireEvent.loadedMetadata(video);

    expect(video.currentTime).toBe(20);
    expect(playMock).not.toHaveBeenCalled();
  });

  it("keeps a timestamp pending when preexisting metadata belongs to an unrecorded source", () => {
    const readyStateDescriptor = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "readyState");
    Object.defineProperty(HTMLMediaElement.prototype, "readyState", {
      configurable: true,
      get: () => HTMLMediaElement.HAVE_METADATA,
    });
    mockUiConfig.alwaysResumeOnPlayback = false;
    try {
      const { container } = render(
        <VideoPlayer
          streamUrl="/api/stream/video/1"
          format="mp4"
          duration={120}
          seekTo={42.5}
          videoId={1}
          detections={[]}
          trackingEnabled={false}
        />,
      );
      const video = container.querySelector("video") as HTMLVideoElement;
      video.currentTime = 0;
      pauseMock.mockClear();

      fireEvent.loadedMetadata(video);

      expect(video.currentTime).toBe(42.5);
      expect(pauseMock).toHaveBeenCalledOnce();
    } finally {
      if (readyStateDescriptor) {
        Object.defineProperty(HTMLMediaElement.prototype, "readyState", readyStateDescriptor);
      }
    }
  });

  it("clears pending autostart when a paused timestamp arrives before metadata", () => {
    mockUiConfig.alwaysResumeOnPlayback = false;
    const { container, rerender } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1?token=first"
        format="mp4"
        duration={120}
        autostart
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1?token=first"
        format="mp4"
        duration={120}
        autostart
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    video.currentTime = 0;
    playMock.mockClear();
    fireEvent.loadedMetadata(video);
    expect(video.currentTime).toBe(42.5);
    expect(playMock).not.toHaveBeenCalled();

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1?token=second"
        format="mp4"
        duration={120}
        autostart
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    video.currentTime = 0;
    fireEvent.loadedMetadata(video);

    expect(playMock).not.toHaveBeenCalled();
  });

  it("lets a paused timestamp supersede an in-flight same-source recovery", () => {
    vi.useFakeTimers();
    mockUiConfig.alwaysResumeOnPlayback = false;
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
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    Object.defineProperty(video, "error", { configurable: true, value: { code: 2 } });
    video.currentTime = 20;
    fireEvent.error(video);
    act(() => vi.advanceTimersByTime(500));
    playMock.mockClear();

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    video.currentTime = 0;
    fireEvent.loadedMetadata(video);

    expect(video.currentTime).toBe(42.5);
    expect(playMock).not.toHaveBeenCalled();

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1?token=refreshed"
        format="mp4"
        duration={120}
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    video.currentTime = 0;
    fireEvent.loadedMetadata(video);
    expect(playMock).not.toHaveBeenCalled();
  });

  it("applies a paused timestamp immediately to a metadata-ready stalled player", () => {
    mockUiConfig.alwaysResumeOnPlayback = false;
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
    Object.defineProperty(video, "readyState", { configurable: true, value: HTMLMediaElement.HAVE_METADATA });
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    fireEvent.loadedMetadata(video);
    fireEvent.waiting(video);
    pauseMock.mockClear();

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );

    expect(video.currentTime).toBe(42.5);
    expect(pauseMock).toHaveBeenCalledOnce();
  });

  it("keeps a stalled transcoded timestamp pending for the new source metadata", async () => {
    mockUiConfig.alwaysResumeOnPlayback = false;
    fetchMock.mockImplementation((input) =>
      String(input).includes("/resolutions")
        ? Promise.resolve(
            new Response(JSON.stringify(["720p"]), { status: 200, headers: { "Content-Type": "application/json" } }),
          )
        : Promise.resolve(new Response(null, { status: 200 })),
    );
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
    fireEvent.loadedMetadata(video);

    const qualityButton = await screen.findByTitle("Video quality");
    fireEvent.click(qualityButton);
    fireEvent.click(screen.getByRole("button", { name: "720p" }));
    fireEvent.loadedMetadata(video);
    fireEvent.waiting(video);
    playMock.mockClear();
    pauseMock.mockClear();

    rerender(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        seekTo={42.5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
      />,
    );
    video.currentTime = 0;
    fireEvent.loadedMetadata(video);

    expect(container).toHaveTextContent("0:42 / 2:00");
    expect(playMock).not.toHaveBeenCalled();
    expect(pauseMock).toHaveBeenCalled();
  });

  it("reloads network-failed media after the server recovers", () => {
    vi.useFakeTimers();
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
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    Object.defineProperty(video, "error", { configurable: true, value: { code: 2 } });
    video.currentTime = 42;
    loadMock.mockClear();
    playMock.mockClear();

    act(() => reportServerResponse(new Response(null, { status: 502 })));
    fireEvent.error(video);
    loadMock.mockClear();

    act(() => reportServerResponse(new Response(null, { status: 200 })));

    expect(loadMock).toHaveBeenCalledOnce();
    video.currentTime = 0;
    fireEvent.loadedMetadata(video);
    expect(video.currentTime).toBe(42);
    expect(playMock).toHaveBeenCalledOnce();
    vi.useRealTimers();
  });

  it("keeps the buffering indicator visible when a seek completes during an outage", () => {
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
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    Object.defineProperty(video, "error", { configurable: true, value: { code: 1 } });
    video.currentTime = 42;

    act(() => reportServerResponse(new Response(null, { status: 502 })));
    fireEvent.error(video);
    expect(container.querySelector(".animate-spin")).toBeInTheDocument();

    video.currentTime = 60;
    fireEvent.seeking(video);
    fireEvent.seeked(video);

    expect(container.querySelector(".animate-spin")).toBeInTheDocument();
  });

  it("discovers a full outage from a native network error without other API traffic", async () => {
    fetchMock.mockImplementation((input) =>
      input === "/api/system/status"
        ? Promise.reject(new TypeError("Failed to fetch"))
        : new Promise<Response>(() => {}),
    );
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
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    Object.defineProperty(video, "error", { configurable: true, value: { code: 2 } });
    video.currentTime = 42;
    loadMock.mockClear();

    fireEvent.error(video);

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        "/api/system/status",
        expect.objectContaining({ cache: "no-store", signal: expect.any(AbortSignal) }),
      ),
    );
    await waitFor(() => expect(getServerAvailability()).toBe("unavailable"));

    act(() => reportServerResponse(new Response(null, { status: 200 })));

    expect(loadMock).toHaveBeenCalledOnce();
  });

  it("synchronizes playback UI when recovery play is rejected", async () => {
    vi.useFakeTimers();
    const onPause = vi.fn();
    const onPlaybackStateChange = vi.fn();
    const { container } = render(
      <VideoPlayer
        streamUrl="/api/stream/video/1"
        format="mp4"
        duration={120}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        onPause={onPause}
        onPlaybackStateChange={onPlaybackStateChange}
      />,
    );
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    Object.defineProperty(video, "error", { configurable: true, value: { code: 2 } });
    video.currentTime = 42;

    fireEvent.play(video);
    expect(container.querySelector(".lucide-pause")).toBeInTheDocument();
    playMock.mockRejectedValueOnce(new Error("Recovery playback was blocked"));

    fireEvent.error(video);
    act(() => vi.advanceTimersByTime(500));
    fireEvent.loadedMetadata(video);
    await act(async () => {
      await Promise.resolve();
    });

    expect(container.querySelector(".lucide-play")).toBeInTheDocument();
    expect(onPlaybackStateChange.mock.calls).toEqual([[true], [false]]);
    expect(onPause).not.toHaveBeenCalled();
  });

  it("retains play intent and the latest seek across failed reload attempts", () => {
    vi.useFakeTimers();
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
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "error", { configurable: true, value: { code: 2 } });
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    video.currentTime = 42;
    loadMock.mockClear();
    playMock.mockClear();

    fireEvent.play(video);
    fireEvent.error(video);
    act(() => vi.advanceTimersByTime(500));
    expect(loadMock).toHaveBeenCalledOnce();

    video.currentTime = 0;
    fireEvent.loadedMetadata(video);
    expect(video.currentTime).toBe(42);

    Object.defineProperty(video, "paused", { configurable: true, value: true });
    video.currentTime = 0;
    act(() => reportServerResponse(new Response(null, { status: 502 })));
    fireEvent.error(video);
    video.currentTime = 120;
    fireEvent.seeking(video);
    loadMock.mockClear();
    playMock.mockClear();

    act(() => reportServerResponse(new Response(null, { status: 200 })));
    expect(loadMock).toHaveBeenCalledOnce();
    fireEvent.loadedMetadata(video);

    expect(video.currentTime).toBe(120);
    expect(playMock).toHaveBeenCalledOnce();
  });

  it("reloads playback when waiting does not resolve before the stall watchdog", () => {
    vi.useFakeTimers();
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
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    video.currentTime = 24;
    loadMock.mockClear();

    fireEvent.waiting(video);
    act(() => vi.advanceTimersByTime(7_999));
    expect(loadMock).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(1));
    expect(loadMock).toHaveBeenCalledOnce();
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/system/status",
      expect.objectContaining({ cache: "no-store", signal: expect.any(AbortSignal) }),
    );
  });

  it("cancels the stall watchdog when playback becomes ready again", () => {
    vi.useFakeTimers();
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
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    Object.defineProperty(video, "readyState", { configurable: true, value: HTMLMediaElement.HAVE_FUTURE_DATA });
    loadMock.mockClear();

    fireEvent.waiting(video);
    fireEvent.canPlay(video);
    act(() => vi.advanceTimersByTime(8_000));

    expect(loadMock).not.toHaveBeenCalled();
  });

  it("treats buffered progress as recovery and reloads only after playback stalls again", () => {
    vi.useFakeTimers();
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
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    video.currentTime = 24;
    loadMock.mockClear();

    act(() => reportServerResponse(new Response(null, { status: 502 })));
    fireEvent.waiting(video);
    expect(container.querySelector(".animate-spin")).toBeInTheDocument();

    for (let currentTime = 25; currentTime <= 33; currentTime += 1) {
      video.currentTime = currentTime;
      fireEvent.timeUpdate(video);
      expect(container.querySelector(".animate-spin")).not.toBeInTheDocument();
      act(() => vi.advanceTimersByTime(1_000));
    }
    expect(loadMock).not.toHaveBeenCalled();

    act(() => vi.advanceTimersByTime(8_000));
    expect(loadMock).not.toHaveBeenCalled();

    fireEvent.waiting(video);
    act(() => vi.advanceTimersByTime(8_000));
    act(() => reportServerResponse(new Response(null, { status: 200 })));
    expect(loadMock).toHaveBeenCalledOnce();

    video.currentTime = 0;
    fireEvent.loadedMetadata(video);
    expect(video.currentTime).toBe(33);
  });

  it("does not repeat a recovered segment when playback advances without readiness events", () => {
    vi.useFakeTimers();
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
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    Object.defineProperty(video, "error", { configurable: true, value: { code: 2 } });
    video.currentTime = 42;
    loadMock.mockClear();

    act(() => reportServerResponse(new Response(null, { status: 502 })));
    fireEvent.error(video);
    act(() => reportServerResponse(new Response(null, { status: 200 })));
    expect(loadMock).toHaveBeenCalledOnce();

    video.currentTime = 0;
    fireEvent.loadedMetadata(video);
    expect(video.currentTime).toBe(42);

    video.currentTime = 43;
    fireEvent.timeUpdate(video);
    expect(container.querySelector(".animate-spin")).not.toBeInTheDocument();

    act(() => vi.advanceTimersByTime(9_000));
    expect(loadMock).toHaveBeenCalledOnce();
    expect(video.currentTime).toBe(43);
  });

  it("offers an explicit playback retry after isolated media failures exhaust automatic retries", () => {
    vi.useFakeTimers();
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
    const video = container.querySelector("video") as HTMLVideoElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    Object.defineProperty(video, "error", { configurable: true, value: { code: 2 } });
    video.currentTime = 36;
    loadMock.mockClear();

    for (const delay of [500, 1_000, 1_500]) {
      fireEvent.error(video);
      act(() => vi.advanceTimersByTime(delay));
    }
    fireEvent.error(video);

    expect(screen.getByRole("alert")).toHaveTextContent("Playback could not reconnect.");
    loadMock.mockClear();
    fireEvent.click(screen.getByRole("button", { name: "Retry playback" }));
    expect(loadMock).toHaveBeenCalledOnce();
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

    const fullscreenButton = container.querySelector('button[aria-label="Enter fullscreen"]');
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
        onSeekRegister={(registered) => {
          seek = registered;
        }}
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
    expect(mockPlaybackTracker.recordInterval).toHaveBeenNthCalledWith(
      1,
      expect.objectContaining({
        startSec: 5,
        endSec: 6,
      }),
    );

    act(() => {
      video.currentTime = 21;
      video.dispatchEvent(new Event("timeupdate"));
      paused = true;
      video.dispatchEvent(new Event("pause"));
    });

    expect(mockPlaybackTracker.recordInterval).toHaveBeenCalledTimes(2);
    expect(mockPlaybackTracker.recordInterval).toHaveBeenNthCalledWith(
      2,
      expect.objectContaining({
        startSec: 20,
        endSec: 21,
      }),
    );
  });

  describe("initial compatibility fallback", () => {
    const mockResolutions = (resolutions: string[]) => {
      fetchMock.mockImplementation((input) =>
        String(input).includes("/resolutions")
          ? Promise.resolve(
              new Response(JSON.stringify(resolutions), {
                status: 200,
                headers: { "Content-Type": "application/json" },
              }),
            )
          : Promise.resolve(new Response(null, { status: 200 })),
      );
    };

    const player = (videoId: number, format: string, audioCodec = "aac") => (
      <VideoPlayer
        streamUrl={`/api/stream/video/${videoId}`}
        format={format}
        audioCodec={audioCodec}
        duration={120}
        videoId={videoId}
        detections={[]}
        trackingEnabled={false}
      />
    );

    it.each(["wmv", " ASF ", "AvI"])("selects the highest transcode for %s sources", async (format) => {
      mockResolutions(["360p", "720p", "1080p"]);
      const { container } = render(player(41, format));

      await waitFor(() =>
        expect(container.querySelector("source")).toHaveAttribute(
          "src",
          "/api/stream/video/41/transcode?resolution=1080p",
        ),
      );
      expect(screen.getByText("Using transcoded stream for video format compatibility")).toBeInTheDocument();
    });

    it("keeps a compatible MP4 source on Direct", async () => {
      mockResolutions(["360p", "720p"]);
      const { container } = render(player(42, "mp4"));

      await waitFor(() =>
        expect(fetchMock.mock.calls.some(([input]) => String(input).includes("/resolutions"))).toBe(true),
      );
      expect(container.querySelector("source")).toHaveAttribute("src", "/api/stream/video/42");
      expect(screen.queryByText(/Using transcoded stream for/)).not.toBeInTheDocument();
    });

    it("selects a transcode and shows an audio-specific notice for incompatible MP4 audio", async () => {
      mockResolutions(["360p", "720p"]);
      const { container } = render(player(43, "mp4", " AC3 "));

      await waitFor(() =>
        expect(container.querySelector("source")).toHaveAttribute(
          "src",
          "/api/stream/video/43/transcode?resolution=720p",
        ),
      );
      expect(screen.getByText("Using transcoded stream for audio codec compatibility")).toBeInTheDocument();
    });

    it("lets the user override automatic video-format fallback with Direct", async () => {
      mockResolutions(["360p", "720p"]);
      const { container } = render(player(44, "wmv"));
      const qualityButton = await screen.findByTitle("Video quality");
      await waitFor(() => expect(qualityButton).toHaveTextContent("720p"));

      fireEvent.click(qualityButton);
      fireEvent.click(screen.getByRole("button", { name: "Direct" }));

      await waitFor(() => expect(container.querySelector("source")).toHaveAttribute("src", "/api/stream/video/44"));
      expect(screen.queryByText(/Using transcoded stream for/)).not.toBeInTheDocument();
      await act(async () => Promise.resolve());
      expect(container.querySelector("source")).toHaveAttribute("src", "/api/stream/video/44");

      const video = container.querySelector("video") as HTMLVideoElement;
      Object.defineProperty(video, "error", {
        configurable: true,
        value: { code: MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED },
      });
      fireEvent.error(video);
      await act(async () => Promise.resolve());
      expect(container.querySelector("source")).toHaveAttribute("src", "/api/stream/video/44");
    });

    it("does not attach Direct while compatibility lookup is pending", async () => {
      let resolveResolutions: ((response: Response) => void) | undefined;
      fetchMock.mockImplementation((input) =>
        String(input).includes("/resolutions")
          ? new Promise<Response>((resolve) => {
              resolveResolutions = resolve;
            })
          : Promise.resolve(new Response(null, { status: 200 })),
      );
      const { container } = render(player(48, "wmv"));
      expect(container.querySelector("source")).not.toHaveAttribute("src");

      await act(async () => {
        resolveResolutions?.(
          new Response(JSON.stringify(["360p"]), { status: 200, headers: { "Content-Type": "application/json" } }),
        );
      });

      await waitFor(() =>
        expect(container.querySelector("source")).toHaveAttribute(
          "src",
          "/api/stream/video/48/transcode?resolution=360p",
        ),
      );
    });

    it("keeps Direct when selected from compact controls during compatibility lookup", async () => {
      const originalMatchMedia = window.matchMedia;
      const mediaQuery = {
        matches: true,
        media: "(max-width: 767px)",
        onchange: null,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        dispatchEvent: vi.fn(),
      };
      Object.defineProperty(window, "matchMedia", {
        configurable: true,
        value: vi.fn(() => mediaQuery),
      });

      let resolveResolutions: ((response: Response) => void) | undefined;
      fetchMock.mockImplementation((input) =>
        String(input).includes("/resolutions")
          ? new Promise<Response>((resolve) => {
              resolveResolutions = resolve;
            })
          : Promise.resolve(new Response(null, { status: 200 })),
      );
      const rendered = render(player(52, "wmv"));
      try {
        fireEvent.click(screen.getByRole("button", { name: "Playback options" }));
        fireEvent.click(screen.getByRole("button", { name: "Direct" }));

        await act(async () => {
          resolveResolutions?.(
            new Response(JSON.stringify(["360p"]), { status: 200, headers: { "Content-Type": "application/json" } }),
          );
        });

        await waitFor(() =>
          expect(rendered.container.querySelector("source")).toHaveAttribute("src", "/api/stream/video/52"),
        );
        expect(screen.queryByText(/Using transcoded stream for/)).not.toBeInTheDocument();
      } finally {
        rendered.unmount();
        Object.defineProperty(window, "matchMedia", {
          configurable: true,
          value: originalMatchMedia,
        });
      }
    });

    it("unloads the previous MP4 while denied-format compatibility lookup is pending", async () => {
      mockResolutions(["360p"]);
      const { container, rerender } = render(player(49, "mp4"));
      await waitFor(() => expect(container.querySelector("source")).toHaveAttribute("src", "/api/stream/video/49"));
      pauseMock.mockClear();
      loadMock.mockClear();

      let resolveResolutions: ((response: Response) => void) | undefined;
      fetchMock.mockImplementation((input) =>
        String(input).includes("/resolutions")
          ? new Promise<Response>((resolve) => {
              resolveResolutions = resolve;
            })
          : Promise.resolve(new Response(null, { status: 200 })),
      );
      rerender(player(50, "avi"));

      expect(container.querySelector("source")).not.toHaveAttribute("src");
      expect(pauseMock).toHaveBeenCalledOnce();
      expect(loadMock).toHaveBeenCalledOnce();
      await act(async () => {
        resolveResolutions?.(
          new Response(JSON.stringify(["360p"]), { status: 200, headers: { "Content-Type": "application/json" } }),
        );
      });
      await waitFor(() =>
        expect(container.querySelector("source")).toHaveAttribute(
          "src",
          "/api/stream/video/50/transcode?resolution=360p",
        ),
      );
    });

    it("uses Direct without a notice when compatibility lookup fails", async () => {
      fetchMock.mockImplementation((input) =>
        String(input).includes("/resolutions")
          ? Promise.reject(new Error("lookup failed"))
          : Promise.resolve(new Response(null, { status: 200 })),
      );
      const { container } = render(player(51, "wmv"));

      await waitFor(() => expect(container.querySelector("source")).toHaveAttribute("src", "/api/stream/video/51"));
      expect(screen.queryByText(/Using transcoded stream for/)).not.toBeInTheDocument();
    });

    it("uses source-resolution transcoding when no ladder resolutions are available", async () => {
      mockResolutions([]);
      const { container } = render(player(45, "asf"));

      await waitFor(() =>
        expect(container.querySelector("source")).toHaveAttribute("src", "/api/stream/video/45/transcode"),
      );
      expect(screen.getByTitle("Video quality")).toHaveTextContent("Source");
    });

    it("resets automatic selection and notice when the video changes", async () => {
      mockResolutions(["360p", "720p"]);
      const { container, rerender } = render(player(46, "avi"));
      await screen.findByText("Using transcoded stream for video format compatibility");

      rerender(player(47, "mp4"));

      await waitFor(() => expect(container.querySelector("source")).toHaveAttribute("src", "/api/stream/video/47"));
      expect(screen.queryByText(/Using transcoded stream for/)).not.toBeInTheDocument();
      expect(screen.getByTitle("Video quality")).toHaveTextContent("Direct");
    });
  });
});
