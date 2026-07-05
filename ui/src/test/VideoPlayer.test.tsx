import React from "react";
import { render, waitFor } from "@testing-library/react";
import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoPlayer } from "../components/VideoPlayer";

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
});
