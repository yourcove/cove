import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useLayoutEffect, useRef } from "react";
import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoPlayer } from "../components/VideoPlayer";
import { RouteRegistryProvider, useRouteRegistry } from "../router/RouteRegistry";

const playbackTrackerMock = vi.hoisted(() => ({
  setTarget: vi.fn(() => Promise.resolve()),
  recordInterval: vi.fn(),
  flush: vi.fn(() => Promise.resolve()),
}));

vi.mock("../utils/interactionTracking", async (importOriginal) => {
  const original = await importOriginal<typeof import("../utils/interactionTracking")>();
  return {
    ...original,
    createPlaybackTracker: () => playbackTrackerMock,
  };
});

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: {} } }),
}));

const playMock = vi.fn(() => Promise.resolve());
const pauseMock = vi.fn();
const loadMock = vi.fn();

interface ExpectedPlayerContext {
  hostType: "video";
  hostId: number;
  surface: "detail" | "quick-view" | "compilation";
  currentTime: number;
  duration: number;
  playing: boolean;
  playbackRate?: number;
  intrinsicWidth: number;
  intrinsicHeight: number;
  contentRect: { left: number; top: number; width: number; height: number };
  play(): Promise<void>;
  pause(): void;
  seek(seconds: number): void;
  setPlaybackRate(rate: number): void;
  acquireInteractionMode(options?: {
    hideNativeControls?: boolean;
    pauseTracking?: boolean;
    pausePlayback?: boolean;
  }): () => void;
}

let actionContext: ExpectedPlayerContext | undefined;
let overlayContext: ExpectedPlayerContext | undefined;
const actionClickMock = vi.fn();

class ResizeObserverMock {
  observe() {}
  unobserve() {}
  disconnect() {}
}

beforeAll(() => {
  vi.stubGlobal("ResizeObserver", ResizeObserverMock);

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

beforeEach(() => {
  actionContext = undefined;
  overlayContext = undefined;
  actionClickMock.mockClear();
  playMock.mockClear();
  pauseMock.mockClear();
  loadMock.mockClear();
  playbackTrackerMock.setTarget.mockClear();
  playbackTrackerMock.recordInterval.mockClear();
  playbackTrackerMock.flush.mockClear();
  window.localStorage.clear();
});

function RegisteredPlayer({
  videoId = 17,
  surface = "detail",
  registerContributions = true,
  interactionResetKey,
  trackingEnabled = false,
  crashAction = false,
}: {
  videoId?: number;
  surface?: ExpectedPlayerContext["surface"];
  registerContributions?: boolean;
  interactionResetKey?: unknown;
  trackingEnabled?: boolean;
  crashAction?: boolean;
}) {
  const { registerSlot } = useRouteRegistry();
  const crashActionRef = useRef(crashAction);
  crashActionRef.current = crashAction;

  useLayoutEffect(() => {
    if (!registerContributions) return;

    const unregisterAction = registerSlot({
      id: "media-player-test-action",
      slot: "media-player-actions",
      render: (context) => {
        if (crashActionRef.current) throw new Error("player action crashed");
        actionContext = context as ExpectedPlayerContext;
        return (
          <button data-testid="media-player-test-action" onClick={actionClickMock}>
            Extension action
          </button>
        );
      },
    });
    const unregisterOverlay = registerSlot({
      id: "media-player-test-overlay",
      slot: "media-player-overlay",
      render: (context) => {
        overlayContext = context as ExpectedPlayerContext;
        return <button data-testid="media-player-test-overlay">Extension overlay</button>;
      },
    });

    return () => {
      unregisterAction();
      unregisterOverlay();
    };
  }, [registerContributions, registerSlot]);

  return (
    <VideoPlayer
      streamUrl={`/api/videos/${videoId}/stream`}
      format="mp4"
      duration={120}
      videoId={videoId}
      detections={[]}
      trackingEnabled={trackingEnabled}
      extensionSurface={surface}
      interactionResetKey={interactionResetKey}
    />
  );
}

function renderPlayer(props: Parameters<typeof RegisteredPlayer>[0] = {}) {
  return render(
    <RouteRegistryProvider>
      <RegisteredPlayer {...props} />
    </RouteRegistryProvider>,
  );
}

function setVideoGeometry(container: HTMLElement) {
  const video = container.querySelector("video") as HTMLVideoElement;
  const player = video.parentElement as HTMLDivElement;
  Object.defineProperties(player, {
    clientWidth: { configurable: true, value: 1000 },
    clientHeight: { configurable: true, value: 1000 },
  });
  Object.defineProperties(video, {
    videoWidth: { configurable: true, value: 1920 },
    videoHeight: { configurable: true, value: 1080 },
  });
  fireEvent.loadedMetadata(video);
  return video;
}

function findNativeControls() {
  let element = document.querySelector<HTMLElement>("span.tabular-nums");
  while (element && !element.className.includes("transition-opacity")) {
    element = element.parentElement;
  }
  if (!element) throw new Error("Could not find native player controls");
  return element;
}

describe("VideoPlayer extension slots", () => {
  it("provides action and overlay slots with live playback state and the exact displayed content rectangle", async () => {
    const { container } = renderPlayer({ surface: "quick-view" });
    const video = setVideoGeometry(container);

    Object.defineProperty(video, "currentTime", { configurable: true, writable: true, value: 42.125 });
    fireEvent.play(video);
    fireEvent.timeUpdate(video);

    await waitFor(() => {
      expect(screen.getByTestId("media-player-test-action")).toBeInTheDocument();
      expect(screen.getByTestId("media-player-test-overlay")).toBeInTheDocument();
      expect(screen.getByTestId("media-player-test-overlay").parentElement).toHaveClass("pointer-events-auto");
      expect(actionContext).toMatchObject({
        hostType: "video",
        hostId: 17,
        surface: "quick-view",
        currentTime: 42.125,
        duration: 120,
        playing: true,
        intrinsicWidth: 1920,
        intrinsicHeight: 1080,
        contentRect: { left: 0, top: 218.75, width: 1000, height: 562.5 },
      });
      expect(overlayContext).toMatchObject({
        hostType: "video",
        hostId: 17,
        surface: "quick-view",
        currentTime: 42.125,
        duration: 120,
        playing: true,
        intrinsicWidth: 1920,
        intrinsicHeight: 1080,
        contentRect: { left: 0, top: 218.75, width: 1000, height: 562.5 },
      });
    });
  });

  it("moves player actions into the compact playback menu without mounting duplicates", async () => {
    const originalMatchMedia = window.matchMedia;
    const addListener = vi.fn();
    const removeListener = vi.fn();
    const mediaQuery = {
      matches: true,
      media: "(max-width: 767px)",
      onchange: null,
      addListener,
      removeListener,
      dispatchEvent: vi.fn(),
    };
    const matchMedia = vi.fn((query: string) => {
      mediaQuery.media = query;
      return mediaQuery;
    });
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      value: matchMedia,
    });

    const rendered = renderPlayer();
    try {
      expect(matchMedia).toHaveBeenCalledWith("(max-width: 767px)");
      expect(screen.queryByTestId("media-player-test-action")).not.toBeInTheDocument();
      expect(addListener).toHaveBeenCalledOnce();

      fireEvent.click(screen.getByRole("button", { name: "Playback options" }));

      await waitFor(() => expect(screen.getByTestId("media-player-test-action")).toBeInTheDocument());
      expect(screen.getAllByTestId("media-player-test-action")).toHaveLength(1);
      expect(screen.getByText("Extension actions")).toBeInTheDocument();
      expect(screen.getByTestId("media-player-test-action").closest('[role="dialog"]')).not.toBeNull();

      fireEvent.click(screen.getByTestId("media-player-test-action"));

      expect(actionClickMock).toHaveBeenCalledOnce();
      expect(screen.queryByText("Extension actions")).not.toBeInTheDocument();

      act(() => {
        mediaQuery.matches = false;
        const sync = addListener.mock.calls[0]?.[0] as (() => void) | undefined;
        sync?.();
      });

      await waitFor(() => expect(screen.getByTestId("media-player-test-action")).toBeInTheDocument());
      expect(screen.getAllByTestId("media-player-test-action")).toHaveLength(1);
      expect(screen.getByTestId("media-player-test-action").closest('[role="dialog"]')).toBeNull();
    } finally {
      rendered.unmount();
      Object.defineProperty(window, "matchMedia", {
        configurable: true,
        value: originalMatchMedia,
      });
    }
  });

  it("lets extension controllers play, pause, and seek without exposing the video element", async () => {
    const { container } = renderPlayer();
    const video = setVideoGeometry(container);
    await waitFor(() => expect(actionContext).toBeDefined());
    await act(async () => {
      await actionContext!.play();
    });
    expect(playMock).toHaveBeenCalledTimes(1);

    act(() => actionContext!.pause());
    expect(pauseMock).toHaveBeenCalledTimes(1);

    act(() => actionContext!.seek(37.5));
    expect(video.currentTime).toBe(37.5);
    await waitFor(() => expect(actionContext?.currentTime).toBe(37.5));
  });

  it("bounds extension seeks and playback rates before touching the media element", async () => {
    const { container } = renderPlayer();
    const video = setVideoGeometry(container);
    video.currentTime = 12;
    await waitFor(() => expect(actionContext).toBeDefined());

    act(() => {
      actionContext!.seek(Number.NaN);
      actionContext!.seek(Number.POSITIVE_INFINITY);
      actionContext!.setPlaybackRate(Number.NaN);
    });
    expect(video.currentTime).toBe(12);
    expect(video.playbackRate).toBe(1);

    act(() => actionContext!.setPlaybackRate(100));
    expect(video.playbackRate).toBe(2);
    await waitFor(() => expect(actionContext?.playbackRate).toBe(2));

    act(() => actionContext!.setPlaybackRate(-100));
    expect(video.playbackRate).toBe(0.25);
    await waitFor(() => expect(actionContext?.playbackRate).toBe(0.25));
  });

  it("uses an idempotent interaction lease to pause once, hide native controls, and suppress conflicting input until release", async () => {
    const { container } = renderPlayer();
    const video = setVideoGeometry(container);
    await waitFor(() => expect(actionContext).toBeDefined());
    expect(screen.getByTestId("video-player-paused-affordance")).toBeInTheDocument();

    let release!: () => void;
    act(() => {
      release = actionContext!.acquireInteractionMode({
        hideNativeControls: true,
        pausePlayback: true,
      });
    });

    expect(pauseMock).toHaveBeenCalledTimes(1);
    await waitFor(() => {
      expect(findNativeControls()).toHaveClass("opacity-0", "pointer-events-none");
      expect(screen.queryByTestId("video-player-paused-affordance")).not.toBeInTheDocument();
    });

    playMock.mockClear();
    video.currentTime = 20;
    fireEvent.timeUpdate(video);
    fireEvent.click(video);
    fireEvent.keyDown(window, { key: "ArrowRight" });
    expect(playMock).not.toHaveBeenCalled();
    expect(video.currentTime).toBe(20);

    act(() => {
      release();
      release();
    });
    await waitFor(() => expect(findNativeControls()).toHaveClass("opacity-100"));
    expect(screen.getByTestId("video-player-paused-affordance")).toBeInTheDocument();

    fireEvent.click(video);
    expect(playMock).toHaveBeenCalledTimes(1);
    fireEvent.keyDown(window, { key: "ArrowRight" });
    expect(video.currentTime).toBe(25);
  });

  it("automatically releases an owning contribution's lease when that contribution is unregistered", async () => {
    const rendered = renderPlayer();
    const video = setVideoGeometry(rendered.container);
    await waitFor(() => expect(actionContext).toBeDefined());

    const staleContext = actionContext!;
    act(() => {
      staleContext.acquireInteractionMode({ hideNativeControls: true });
    });
    await waitFor(() => expect(findNativeControls()).toHaveClass("opacity-0"));

    rendered.rerender(
      <RouteRegistryProvider>
        <RegisteredPlayer registerContributions={false} />
      </RouteRegistryProvider>,
    );
    await waitFor(() => expect(screen.queryByTestId("media-player-test-action")).not.toBeInTheDocument());

    playMock.mockClear();
    fireEvent.click(video);
    expect(playMock).toHaveBeenCalledTimes(1);
    expect(findNativeControls()).toHaveClass("opacity-100");

    act(() => {
      staleContext.acquireInteractionMode({ hideNativeControls: true, pausePlayback: true });
    });
    expect(findNativeControls()).toHaveClass("opacity-100");
    expect(pauseMock).not.toHaveBeenCalled();
  });

  it("composes independent owners and aggregates their requested interaction options", async () => {
    const { container } = renderPlayer();
    const video = setVideoGeometry(container);
    await waitFor(() => expect(overlayContext).toBeDefined());

    let releaseAction!: () => void;
    let releaseOverlay!: () => void;
    act(() => {
      releaseAction = actionContext!.acquireInteractionMode({ hideNativeControls: true });
      releaseOverlay = overlayContext!.acquireInteractionMode({ pauseTracking: true });
    });
    await waitFor(() => expect(findNativeControls()).toHaveClass("opacity-0"));

    act(() => releaseAction());
    await waitFor(() => expect(findNativeControls()).toHaveClass("opacity-100"));
    playMock.mockClear();
    fireEvent.click(video);
    expect(playMock).not.toHaveBeenCalled();

    act(() => releaseOverlay());
    fireEvent.click(video);
    expect(playMock).toHaveBeenCalledTimes(1);
  });

  it("honors pausePlayback once for every successful acquisition even when another lease remains active", async () => {
    const { container } = renderPlayer();
    setVideoGeometry(container);
    await waitFor(() => expect(overlayContext).toBeDefined());

    act(() => {
      actionContext!.acquireInteractionMode({ pausePlayback: true });
    });
    expect(pauseMock).toHaveBeenCalledTimes(1);

    await act(async () => {
      await overlayContext!.play();
    });
    act(() => {
      overlayContext!.acquireInteractionMode({ pausePlayback: true });
    });
    expect(pauseMock).toHaveBeenCalledTimes(2);
  });

  it("keeps the cursor and ordinary controls available while an interaction lease is active in fullscreen", async () => {
    vi.useFakeTimers();
    try {
      const { container } = renderPlayer();
      const video = setVideoGeometry(container);
      const player = video.parentElement as HTMLDivElement;
      Object.defineProperty(video, "paused", { configurable: true, value: false });
      Object.defineProperty(document, "fullscreenElement", { configurable: true, value: player });
      act(() => document.dispatchEvent(new Event("fullscreenchange")));
      await act(async () => {});

      fireEvent.mouseMove(player);
      act(() => vi.advanceTimersByTime(3000));
      expect(player).toHaveStyle({ cursor: "none" });

      act(() => {
        actionContext!.acquireInteractionMode();
      });
      expect(player).not.toHaveStyle({ cursor: "none" });
      expect(findNativeControls()).toHaveClass("opacity-100");

      fireEvent.mouseLeave(player);
      act(() => vi.advanceTimersByTime(3000));
      expect(player).not.toHaveStyle({ cursor: "none" });
      expect(findNativeControls()).toHaveClass("opacity-100");
    } finally {
      vi.useRealTimers();
      Object.defineProperty(document, "fullscreenElement", { configurable: true, value: null });
    }
  });

  it("cuts playback tracking for the full pauseTracking lease and restores it only after the final ordinary release", async () => {
    const { container } = renderPlayer({ trackingEnabled: true });
    const video = setVideoGeometry(container);
    await waitFor(() => expect(actionContext).toBeDefined());
    fireEvent.play(video);

    let releaseAction!: () => void;
    let releaseOverlay!: () => void;
    act(() => {
      releaseAction = actionContext!.acquireInteractionMode({ pauseTracking: true });
      releaseOverlay = overlayContext!.acquireInteractionMode({ pauseTracking: true });
    });
    await waitFor(() => expect(playbackTrackerMock.setTarget).toHaveBeenLastCalledWith(null));

    act(() => releaseAction());
    expect(playbackTrackerMock.setTarget).toHaveBeenLastCalledWith(null);

    act(() => releaseOverlay());
    await waitFor(() => {
      expect(playbackTrackerMock.setTarget).toHaveBeenLastCalledWith(
        expect.objectContaining({
          hostType: "video",
          hostId: 17,
        }),
      );
    });
  });

  it("releases leases and clears stale geometry when the active video changes", async () => {
    const rendered = renderPlayer({ videoId: 17 });
    const firstVideo = setVideoGeometry(rendered.container);
    await waitFor(() => expect(actionContext?.intrinsicWidth).toBe(1920));
    firstVideo.currentTime = 24;
    fireEvent.play(firstVideo);
    fireEvent.timeUpdate(firstVideo);
    expect(actionContext).toMatchObject({ hostId: 17, currentTime: 24, playing: true });

    act(() => {
      actionContext!.acquireInteractionMode({ hideNativeControls: true });
    });
    await waitFor(() => expect(findNativeControls()).toHaveClass("opacity-0"));

    rendered.rerender(
      <RouteRegistryProvider>
        <RegisteredPlayer videoId={18} />
      </RouteRegistryProvider>,
    );

    await waitFor(() => {
      expect(actionContext).toMatchObject({
        hostId: 18,
        currentTime: 0,
        playing: false,
        intrinsicWidth: 0,
        intrinsicHeight: 0,
        contentRect: { left: 0, top: 0, width: 0, height: 0 },
      });
      expect(findNativeControls()).toHaveClass("opacity-100");
    });

    playMock.mockClear();
    fireEvent.click(firstVideo);
    expect(playMock).toHaveBeenCalledTimes(1);
  });

  it("releases outstanding interaction leases and resumes tracking when the player leaves fullscreen", async () => {
    const { container } = renderPlayer({ surface: "compilation", trackingEnabled: true });
    const video = setVideoGeometry(container);
    const player = video.parentElement;
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    await waitFor(() => expect(actionContext).toBeDefined());
    fireEvent.play(video);

    act(() => {
      actionContext!.acquireInteractionMode({ hideNativeControls: true, pauseTracking: true });
    });
    await waitFor(() => expect(findNativeControls()).toHaveClass("opacity-0"));

    let fullscreenElement: Element | null = player;
    Object.defineProperty(document, "fullscreenElement", {
      configurable: true,
      get: () => fullscreenElement,
    });
    act(() => document.dispatchEvent(new Event("fullscreenchange")));
    expect(findNativeControls()).toHaveClass("opacity-0");

    fullscreenElement = null;
    act(() => document.dispatchEvent(new Event("fullscreenchange")));
    await waitFor(() => expect(findNativeControls()).toHaveClass("opacity-100"));

    playbackTrackerMock.recordInterval.mockClear();
    video.currentTime = 0.5;
    fireEvent.timeUpdate(video);
    fireEvent.pause(video);
    expect(playbackTrackerMock.recordInterval).toHaveBeenCalledWith(
      expect.objectContaining({
        startSec: 0,
        endSec: 0.5,
      }),
    );

    Object.defineProperty(video, "paused", { configurable: true, value: true });
    playMock.mockClear();
    fireEvent.click(video);
    expect(playMock).toHaveBeenCalledTimes(1);
  });

  it("releases interaction leases and resumes tracking when a same-video queue item changes its reset key", async () => {
    const rendered = renderPlayer({ interactionResetKey: "item-a", trackingEnabled: true });
    const video = setVideoGeometry(rendered.container);
    Object.defineProperty(video, "paused", { configurable: true, value: false });
    await waitFor(() => expect(actionContext).toBeDefined());
    fireEvent.play(video);
    act(() => {
      actionContext!.acquireInteractionMode({ hideNativeControls: true, pauseTracking: true });
    });
    await waitFor(() => expect(findNativeControls()).toHaveClass("opacity-0"));

    rendered.rerender(
      <RouteRegistryProvider>
        <RegisteredPlayer interactionResetKey="item-b" trackingEnabled />
      </RouteRegistryProvider>,
    );
    await waitFor(() => {
      expect(findNativeControls()).toHaveClass("opacity-100");
      expect(actionContext).toMatchObject({
        intrinsicWidth: 1920,
        intrinsicHeight: 1080,
        contentRect: { left: 0, top: 218.75, width: 1000, height: 562.5 },
      });
    });

    playbackTrackerMock.recordInterval.mockClear();
    video.currentTime = 0.75;
    fireEvent.timeUpdate(video);
    fireEvent.pause(video);
    expect(playbackTrackerMock.recordInterval).toHaveBeenCalledWith(
      expect.objectContaining({
        startSec: 0,
        endSec: 0.75,
      }),
    );

    Object.defineProperty(video, "paused", { configurable: true, value: true });
    playMock.mockClear();
    fireEvent.click(video);
    expect(playMock).toHaveBeenCalledTimes(1);
  });

  it("recovers a failed player contribution when the same video advances to a new item context", async () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const rendered = renderPlayer({ interactionResetKey: "item-a", crashAction: true });

    expect(await screen.findByText("Extension error (media-player-test-action)")).toBeInTheDocument();
    expect(screen.queryByTestId("media-player-test-action")).not.toBeInTheDocument();

    rendered.rerender(
      <RouteRegistryProvider>
        <RegisteredPlayer interactionResetKey="item-b" crashAction={false} />
      </RouteRegistryProvider>,
    );

    expect(await screen.findByTestId("media-player-test-action")).toBeInTheDocument();
    expect(screen.queryByText("Extension error (media-player-test-action)")).not.toBeInTheDocument();
    consoleError.mockRestore();
  });
});
