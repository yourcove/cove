import { fireEvent, render, waitFor } from "@testing-library/react";
import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoPlayer } from "../components/VideoPlayer";

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: {} } }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: { kind: "user", uiPreferences: {} } }),
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

describe("Compilation player autoplay token wiring", () => {
  beforeEach(() => {
    playMock.mockClear();
    pauseMock.mockClear();
    loadMock.mockClear();
  });

  it("plays again after a source swap when autostartToken increments", async () => {
    const onPlay = vi.fn();
    const { container, rerender } = render(
      <VideoPlayer
        streamUrl="/api/videos/1/stream"
        format="mp4"
        duration={120}
        resumeTime={5}
        videoId={1}
        detections={[]}
        onPlay={onPlay}
        autostart
        autostartToken={0}
        trackingEnabled={false}
        clip={{ start: 5, end: 15, loop: false }}
      />,
    );

    const firstVideo = container.querySelector("video");
    expect(firstVideo).toBeInstanceOf(HTMLVideoElement);
    fireEvent(firstVideo as HTMLVideoElement, new Event("loadedmetadata"));

    await waitFor(() => {
      expect(playMock).toHaveBeenCalledTimes(1);
    });

    rerender(
      <VideoPlayer
        streamUrl="/api/videos/2/stream"
        format="mp4"
        duration={180}
        resumeTime={20}
        videoId={2}
        detections={[]}
        onPlay={onPlay}
        autostart
        autostartToken={1}
        trackingEnabled={false}
        clip={{ start: 20, end: 30, loop: false }}
      />,
    );

    const secondVideo = container.querySelector("video");
    expect(secondVideo).toBeInstanceOf(HTMLVideoElement);
    fireEvent(secondVideo as HTMLVideoElement, new Event("loadedmetadata"));

    await waitFor(() => {
      expect(playMock).toHaveBeenCalledTimes(2);
    });
  });

  it("restarts a non-looping clip at the clip start when replayed after the clip end", () => {
    const { container } = render(
      <VideoPlayer
        streamUrl="/api/videos/1/stream"
        format="mp4"
        duration={120}
        resumeTime={5}
        videoId={1}
        detections={[]}
        trackingEnabled={false}
        clip={{ start: 5, end: 15, loop: false }}
      />,
    );

    const video = container.querySelector("video") as HTMLVideoElement;
    expect(video).toBeInstanceOf(HTMLVideoElement);

    video.currentTime = 15;
    fireEvent(video, new Event("timeupdate"));

    expect(pauseMock).toHaveBeenCalledTimes(1);
    expect(video.currentTime).toBe(15);

    fireEvent.click(video);

    expect(video.currentTime).toBe(5);
    expect(playMock).toHaveBeenCalledTimes(1);
  });

  it("does not declare a misleading MIME type for direct video streams", () => {
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
    expect(source?.getAttribute("type")).toBeNull();
  });
});
