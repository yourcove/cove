import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createElement, type ComponentType } from "react";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import type { ExtensionComponentOverride } from "../api/types";
import { VideoPlayer } from "../components/VideoPlayer";
import {
  VIDEO_ENGINE_TARGET,
  isVideoEngineComponent,
  playsDirectFormat,
  type VideoEngineCapabilities,
  type VideoEngineComponent,
  type VideoEngineProps,
} from "../components/VideoEngine";
import type { ResolvedComponentOverride } from "../extensions/ExtensionLoader";
import { resetServerAvailabilityForTests } from "../state/serverAvailability";

const { extensionState } = vi.hoisted(() => ({
  extensionState: {
    overrides: [] as ResolvedComponentOverride[],
    settled: true,
  },
}));

vi.mock("../extensions/ExtensionLoader", async () => {
  const actual = await vi.importActual<typeof import("../extensions/ExtensionLoader")>("../extensions/ExtensionLoader");
  return {
    ...actual,
    useResolvedComponentOverrides: (target: string) =>
      extensionState.overrides.filter((override) => override.contribution.targetComponent === target),
    useExtensionRuntimeSettled: () => extensionState.settled,
  };
});

vi.mock("../utils/interactionTracking", async () => {
  const actual = await vi.importActual<typeof import("../utils/interactionTracking")>("../utils/interactionTracking");
  return {
    ...actual,
    createPlaybackTracker: () => ({
      getSessionId: vi.fn(() => "session-1"),
      setTarget: vi.fn(() => Promise.resolve()),
      recordInterval: vi.fn(),
      flush: vi.fn(() => Promise.resolve()),
      dispose: vi.fn(() => Promise.resolve()),
    }),
  };
});

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: {} } }),
}));

const playMock = vi.fn(() => Promise.resolve());
const pauseMock = vi.fn();
const loadMock = vi.fn();
const fetchMock = vi.fn<typeof fetch>();

// Stands in for an engine's element: a custom element carrying the media surface as plain state,
// with play/pause/load routed to the same mocks as the browser's <video>.
class FakeVideoElement extends HTMLElement {
  currentTime = 0;
  duration = Number.NaN;
  paused = true;
  ended = false;
  seeking = false;
  readyState = 0;
  networkState = 0;
  error: { code: number; message: string } | null = null;
  buffered = { length: 0, start: () => 0, end: () => 0 };
  textTracks = { length: 0 };
  volume = 1;
  muted = false;
  playbackRate = 1;
  videoWidth = 0;
  videoHeight = 0;
  src = "";
  currentSrc = "";
  poster = "";
  play() {
    return playMock();
  }
  pause() {
    pauseMock();
  }
  load() {
    loadMock();
  }
}

class ResizeObserverMock {
  observe() {}
  unobserve() {}
  disconnect() {}
}

beforeAll(() => {
  customElements.define("fake-video", FakeVideoElement);
  vi.stubGlobal("ResizeObserver", ResizeObserverMock);
  vi.stubGlobal("MediaError", {
    MEDIA_ERR_ABORTED: 1,
    MEDIA_ERR_NETWORK: 2,
    MEDIA_ERR_DECODE: 3,
    MEDIA_ERR_SRC_NOT_SUPPORTED: 4,
  });
  vi.stubGlobal("fetch", fetchMock);
  for (const [name, value] of [
    ["play", playMock],
    ["pause", pauseMock],
    ["load", loadMock],
  ] as const) {
    Object.defineProperty(HTMLMediaElement.prototype, name, { configurable: true, writable: true, value });
  }
});

beforeEach(() => {
  extensionState.overrides = [];
  extensionState.settled = true;
  resetServerAvailabilityForTests();
  playMock.mockReset();
  playMock.mockResolvedValue(undefined);
  pauseMock.mockClear();
  loadMock.mockClear();
  fetchMock.mockReset();
  fetchMock.mockImplementation((input) =>
    String(input).includes("/resolutions")
      ? Promise.resolve(
          new Response(JSON.stringify(["360p", "720p"]), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          }),
        )
      : Promise.resolve(new Response(null, { status: 200 })),
  );
});

afterEach(() => {
  vi.restoreAllMocks();
});

function makeEngine(name: string, capabilities: VideoEngineCapabilities): VideoEngineComponent {
  const Engine = ({ children, ...props }: VideoEngineProps) =>
    createElement("fake-video", { ...props, "data-engine": name }, children);
  return Object.assign(Engine, { videoEngine: capabilities });
}

function installEngines(...engines: Array<{ name: string; Component: ComponentType<VideoEngineProps> }>) {
  extensionState.overrides = engines.map(({ name, Component }, index) => ({
    contribution: {
      targetComponent: VIDEO_ENGINE_TARGET,
      extensionId: `extension.${name}`,
      componentName: name,
      priority: 100 - index,
    } satisfies ExtensionComponentOverride,
    Component,
    revision: 1,
  }));
}

function player(props: Partial<Parameters<typeof VideoPlayer>[0]> = {}) {
  return (
    <VideoPlayer
      streamUrl="/api/stream/video/7"
      format="mp4"
      duration={120}
      videoId={7}
      detections={[]}
      trackingEnabled={false}
      {...props}
    />
  );
}

const engineElement = (container: HTMLElement, name?: string) =>
  container.querySelector<FakeVideoElement>(name ? `fake-video[data-engine="${name}"]` : "fake-video");

async function resolutionsLoaded() {
  await waitFor(() =>
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes("/resolutions"))).toBe(true),
  );
}

function failWith(element: Element, code: number) {
  Object.defineProperty(element, "error", { configurable: true, value: { code, message: "failed" } });
  act(() => {
    element.dispatchEvent(new Event("error"));
  });
}

describe("video engine capabilities", () => {
  it("recognises only components that declare valid stream kinds", () => {
    const valid = makeEngine("valid", { streams: ["direct", "hls"] });
    expect(isVideoEngineComponent(valid)).toBe(true);
    expect(isVideoEngineComponent(() => null)).toBe(false);
    expect(isVideoEngineComponent(Object.assign(() => null, { videoEngine: { streams: ["dash"] } }))).toBe(false);
  });

  it("matches direct formats case-insensitively and only for engines that take direct streams", () => {
    expect(playsDirectFormat({ streams: ["direct"], directFormats: "any" }, "wmv")).toBe(true);
    expect(playsDirectFormat({ streams: ["direct"], directFormats: ["AVI"] }, " avi ")).toBe(true);
    expect(playsDirectFormat({ streams: ["direct"], directFormats: ["avi"] }, "wmv")).toBe(false);
    expect(playsDirectFormat({ streams: ["direct"] }, "avi")).toBe(false);
    expect(playsDirectFormat({ streams: ["hls"], directFormats: "any" }, "avi")).toBe(false);
  });
});

describe("VideoPlayer with video engines", () => {
  it("plays with the browser's <video> when no engine is registered", async () => {
    const { container } = render(player());
    await resolutionsLoaded();

    expect(container.querySelector("video source")).toHaveAttribute("src", "/api/stream/video/7");
    expect(engineElement(container)).toBeNull();
  });

  it("ignores an override that does not declare engine capabilities", async () => {
    installEngines({ name: "plain", Component: () => createElement("fake-video") });
    const { container } = render(player());
    await resolutionsLoaded();

    expect(container.querySelector("video source")).toHaveAttribute("src", "/api/stream/video/7");
    expect(engineElement(container)).toBeNull();
  });

  it("renders the engine with the same attributes, <source> and <track> children as <video>", async () => {
    installEngines({ name: "pup", Component: makeEngine("pup", { streams: ["direct"] }) });
    const { container } = render(
      player({
        posterUrl: "/poster.jpg",
        captions: [{ id: 3, languageCode: "en", captionType: "vtt", filename: "a.vtt" }],
      }),
    );
    await resolutionsLoaded();

    const engine = engineElement(container, "pup")!;
    expect(container.querySelector("video")).toBeNull();
    expect(engine.querySelector("source")).toHaveAttribute("src", "/api/stream/video/7");
    expect(engine.querySelector("track")).toHaveAttribute("src", "/api/stream/video/7/caption/3");
    expect(engine.poster).toBe("/poster.jpg");
    expect(engine.className).toContain("object-contain");
    expect(engine).not.toHaveAttribute("x-webkit-airplay");
  });

  it("plays formats the browser cannot directly when the engine declares them", async () => {
    installEngines({ name: "pup", Component: makeEngine("pup", { streams: ["direct"], directFormats: "any" }) });
    const { container } = render(player({ format: "avi", audioCodec: "ac3" }));
    await resolutionsLoaded();

    await waitFor(() =>
      expect(engineElement(container, "pup")?.querySelector("source")).toHaveAttribute("src", "/api/stream/video/7"),
    );
    expect(screen.queryByText(/Using transcoded stream for/)).not.toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes("/transcode"))).toBe(false);
  });

  it("keeps the transcode for formats an engine limited to other containers does not declare", async () => {
    installEngines({ name: "mkv", Component: makeEngine("mkv", { streams: ["direct"], directFormats: ["mkv"] }) });
    const { container } = render(player({ format: "wmv" }));

    await waitFor(() =>
      expect(container.querySelector("video source")).toHaveAttribute(
        "src",
        "/api/stream/video/7/transcode?resolution=720p",
      ),
    );
    expect(engineElement(container)).toBeNull();
  });

  it("waits for the extension runtime before moving an unplayable format to a transcode", async () => {
    extensionState.settled = false;
    const { container, rerender } = render(player({ format: "avi" }));
    await act(async () => {
      await Promise.resolve();
    });
    expect(container.querySelector("source")).not.toHaveAttribute("src");

    extensionState.settled = true;
    installEngines({ name: "pup", Component: makeEngine("pup", { streams: ["direct"], directFormats: "any" }) });
    rerender(player({ format: "avi" }));

    await waitFor(() =>
      expect(engineElement(container, "pup")?.querySelector("source")).toHaveAttribute("src", "/api/stream/video/7"),
    );
  });

  it("hands a stream kind the engine does not take to <video>", async () => {
    installEngines({ name: "pup", Component: makeEngine("pup", { streams: ["direct"] }) });
    const { container } = render(player());
    await resolutionsLoaded();
    expect(engineElement(container, "pup")).not.toBeNull();

    fireEvent.click(screen.getByTitle("Video quality"));
    fireEvent.click(screen.getAllByRole("button", { name: "720p" })[0]);

    await waitFor(() =>
      expect(container.querySelector("video source")).toHaveAttribute(
        "src",
        "/api/stream/video/7/transcode?resolution=720p",
      ),
    );
    expect(engineElement(container)).toBeNull();
  });

  it("uses the highest-priority engine that takes the current stream kind", async () => {
    installEngines(
      { name: "direct-only", Component: makeEngine("direct-only", { streams: ["direct"] }) },
      { name: "transcodes", Component: makeEngine("transcodes", { streams: ["direct", "transcode-mp4"] }) },
    );
    const { container } = render(player());
    await resolutionsLoaded();
    expect(engineElement(container, "direct-only")).not.toBeNull();

    fireEvent.click(screen.getByTitle("Video quality"));
    fireEvent.click(screen.getAllByRole("button", { name: "720p" })[0]);

    await waitFor(() =>
      expect(engineElement(container, "transcodes")?.querySelector("source")).toHaveAttribute(
        "src",
        "/api/stream/video/7/transcode?resolution=720p",
      ),
    );
  });

  it("falls back from an engine that cannot decode to <video>, then to a transcode", async () => {
    installEngines({ name: "pup", Component: makeEngine("pup", { streams: ["direct"] }) });
    const { container } = render(player());
    await resolutionsLoaded();

    failWith(engineElement(container, "pup")!, 3);
    await waitFor(() => expect(container.querySelector("video source")).toHaveAttribute("src", "/api/stream/video/7"));
    expect(engineElement(container)).toBeNull();

    failWith(container.querySelector("video")!, 3);
    await waitFor(() =>
      expect(container.querySelector("video source")).toHaveAttribute(
        "src",
        "/api/stream/video/7/transcode?resolution=720p",
      ),
    );
  });

  it("falls back to <video> when the engine rejects a reachable source twice", async () => {
    installEngines({ name: "pup", Component: makeEngine("pup", { streams: ["direct"] }) });
    const { container } = render(player());
    await resolutionsLoaded();
    const engine = engineElement(container, "pup")!;
    engine.networkState = HTMLMediaElement.NETWORK_NO_SOURCE;

    const rejectSource = () =>
      act(() => {
        engine.querySelector("source")!.dispatchEvent(new Event("error"));
      });

    // The first rejection of a reachable source is retried in place on the same engine.
    loadMock.mockClear();
    rejectSource();
    await waitFor(() => expect(loadMock).toHaveBeenCalled(), { timeout: 5000 });
    expect(engineElement(container, "pup")).toBe(engine);

    rejectSource();

    await waitFor(() => expect(container.querySelector("video source")).toHaveAttribute("src", "/api/stream/video/7"), {
      timeout: 5000,
    });
    expect(engineElement(container)).toBeNull();
  });

  it("falls back to <video> when the engine component throws", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const Broken = Object.assign(
      () => {
        throw new Error("engine failed to render");
      },
      { videoEngine: { streams: ["direct"] } satisfies VideoEngineCapabilities },
    );
    installEngines({ name: "broken", Component: Broken });
    const { container } = render(player());

    await waitFor(() => expect(container.querySelector("video source")).toHaveAttribute("src", "/api/stream/video/7"));
  });

  it("resumes on <video> from the engine's position and play state", async () => {
    installEngines({ name: "pup", Component: makeEngine("pup", { streams: ["direct"] }) });
    const { container } = render(player());
    await resolutionsLoaded();
    const engine = engineElement(container, "pup")!;
    engine.currentTime = 30;
    engine.paused = false;
    playMock.mockClear();

    failWith(engine, 4);
    const video = await waitFor(() => container.querySelector("video")!);
    fireEvent.loadedMetadata(video);

    expect(video.currentTime).toBe(30);
    expect(playMock).toHaveBeenCalled();
  });

  it("reports the engine's playback through the player's callbacks", async () => {
    installEngines({ name: "pup", Component: makeEngine("pup", { streams: ["direct"] }) });
    const onTimeUpdate = vi.fn();
    const onPlaybackStateChange = vi.fn();
    const { container } = render(player({ onTimeUpdate, onPlaybackStateChange }));
    await resolutionsLoaded();
    const engine = engineElement(container, "pup")!;

    engine.currentTime = 12.5;
    act(() => {
      engine.dispatchEvent(new Event("play"));
      engine.dispatchEvent(new Event("timeupdate"));
    });

    expect(onPlaybackStateChange).toHaveBeenLastCalledWith(true);
    expect(onTimeUpdate).toHaveBeenLastCalledWith(12.5);
  });

  it("offers Picture-in-Picture only when the engine declares it", async () => {
    installEngines({ name: "pup", Component: makeEngine("pup", { streams: ["direct"] }) });
    const { unmount } = render(player());
    await resolutionsLoaded();
    expect(screen.queryByTitle("Picture-in-Picture")).not.toBeInTheDocument();
    unmount();

    installEngines({
      name: "pip",
      Component: makeEngine("pip", { streams: ["direct"], features: { pictureInPicture: true, airplay: true } }),
    });
    const { container } = render(player());
    await resolutionsLoaded();
    expect(screen.getByTitle("Picture-in-Picture")).toBeInTheDocument();
    expect(engineElement(container, "pip")).toHaveAttribute("x-webkit-airplay", "allow");
  });
});
