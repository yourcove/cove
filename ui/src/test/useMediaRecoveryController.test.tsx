import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useMediaRecoveryController } from "../components/useMediaRecoveryController";
import { reportServerResponse, resetServerAvailabilityForTests } from "../state/serverAvailability";

function createMedia(play = vi.fn(() => Promise.resolve())) {
  return {
    currentTime: 0,
    load: vi.fn(),
    play,
    readyState: HTMLMediaElement.HAVE_FUTURE_DATA,
  } as unknown as HTMLVideoElement;
}

describe("useMediaRecoveryController", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    resetServerAvailabilityForTests();
    vi.stubGlobal(
      "fetch",
      vi.fn(() => Promise.resolve(new Response(null, { status: 200 }))),
    );
  });

  afterEach(() => {
    vi.useRealTimers();
    resetServerAvailabilityForTests();
    vi.unstubAllGlobals();
  });

  it("keeps callbacks stable while using the latest transcode conversion", async () => {
    let rejectPlay!: (error: Error) => void;
    const play = vi.fn(
      () =>
        new Promise<void>((_resolve, reject) => {
          rejectPlay = reject;
        }),
    );
    const media = createMedia(play);
    media.currentTime = 2;
    const mediaRef = { current: media };
    const setBuffering = vi.fn();
    const initialPlayFailed = vi.fn();
    const currentPlayFailed = vi.fn();
    const { result, rerender } = renderHook(
      ({ offset, onRecoveryPlayFailed }) =>
        useMediaRecoveryController({
          mediaRef,
          resetKey: "video-1|source-a",
          toAbsoluteTime: (time) => offset + time,
          toMediaTime: (time) => time - offset,
          setBuffering,
          onRecoveryPlayFailed,
        }),
      { initialProps: { offset: 40, onRecoveryPlayFailed: initialPlayFailed } },
    );
    const initialCallbacks = {
      waiting: result.current.waiting,
      networkError: result.current.networkError,
      metadataLoaded: result.current.metadataLoaded,
      userSeek: result.current.userSeek,
    };

    act(() => result.current.userPlay());
    expect(setBuffering).not.toHaveBeenCalled();

    act(() => result.current.networkError(media, "play", false));
    expect(result.current.phase).toBe("retrying");
    act(() => vi.advanceTimersByTime(500));
    expect(media.load).toHaveBeenCalledOnce();

    rerender({ offset: 30, onRecoveryPlayFailed: currentPlayFailed });
    expect(result.current).toMatchObject(initialCallbacks);
    act(() => result.current.metadataLoaded());

    expect(media.currentTime).toBe(12);
    expect(play).toHaveBeenCalledOnce();
    act(() => result.current.canPlay(media));
    expect(result.current.phase).toBe("healthy");
    await act(async () => {
      rejectPlay(new Error("autoplay rejected"));
      await Promise.resolve();
    });
    expect(result.current.phase).toBe("healthy");
    expect(setBuffering).toHaveBeenLastCalledWith(false);
    expect(initialPlayFailed).not.toHaveBeenCalled();
    expect(currentPlayFailed).toHaveBeenCalledOnce();
  });

  it("cancels watchdogs when the video or source identity changes", () => {
    const media = createMedia();
    media.currentTime = 24;
    const mediaRef = { current: media };
    const setBuffering = vi.fn();
    const { result, rerender } = renderHook(
      ({ resetKey }) =>
        useMediaRecoveryController({
          mediaRef,
          resetKey,
          toAbsoluteTime: (time) => time,
          toMediaTime: (time) => time,
          setBuffering,
        }),
      { initialProps: { resetKey: "video-1|source-a" } },
    );

    act(() => result.current.waiting(media, "play"));
    expect(result.current.phase).toBe("stalled");
    rerender({ resetKey: "video-2|source-b" });
    expect(result.current.phase).toBe("healthy");

    act(() => vi.advanceTimersByTime(8_000));
    expect(media.load).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
  });

  it("finishes a stall only after playback makes meaningful progress", () => {
    const media = createMedia();
    media.currentTime = 24;
    const mediaRef = { current: media };
    const setBuffering = vi.fn();
    const { result } = renderHook(() =>
      useMediaRecoveryController({
        mediaRef,
        resetKey: "video-1|source-a",
        toAbsoluteTime: (time) => time,
        toMediaTime: (time) => time,
        setBuffering,
      }),
    );

    act(() => result.current.waiting(media, "play"));
    expect(result.current.phase).toBe("stalled");
    setBuffering.mockClear();
    media.currentTime = 24.01;

    act(() => result.current.mediaProgress(media));

    expect(result.current.phase).toBe("stalled");
    expect(setBuffering).not.toHaveBeenCalled();
    media.currentTime = 25;

    act(() => result.current.mediaProgress(media));

    expect(result.current.phase).toBe("healthy");
    expect(setBuffering).toHaveBeenCalledWith(false);
    act(() => vi.advanceTimersByTime(8_000));
    expect(media.load).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
  });

  it("ignores a late recovery play rejection from an old source", async () => {
    let rejectPlay!: (error: Error) => void;
    const media = createMedia(
      vi.fn(
        () =>
          new Promise<void>((_resolve, reject) => {
            rejectPlay = reject;
          }),
      ),
    );
    media.currentTime = 18;
    const mediaRef = { current: media };
    const { result, rerender } = renderHook(
      ({ resetKey }) =>
        useMediaRecoveryController({
          mediaRef,
          resetKey,
          toAbsoluteTime: (time) => time,
          toMediaTime: (time) => time,
          setBuffering: vi.fn(),
        }),
      { initialProps: { resetKey: "video-1|source-a" } },
    );

    act(() => result.current.networkError(media, "play", false));
    act(() => vi.advanceTimersByTime(500));
    act(() => result.current.metadataLoaded());
    expect(result.current.phase).toBe("recovering");

    rerender({ resetKey: "video-2|source-b" });
    act(() => result.current.waiting(media, "play"));
    expect(result.current.phase).toBe("stalled");
    await act(async () => {
      rejectPlay(new Error("late rejection"));
      await Promise.resolve();
    });

    expect(result.current.phase).toBe("stalled");
  });

  it("ignores an older play rejection after a newer same-source recovery attempt starts", async () => {
    const rejectPlays: Array<(error: Error) => void> = [];
    const media = createMedia(
      vi.fn(
        () =>
          new Promise<void>((_resolve, reject) => {
            rejectPlays.push(reject);
          }),
      ),
    );
    media.currentTime = 22;
    const mediaRef = { current: media };
    const onRecoveryPlayFailed = vi.fn();
    const { result } = renderHook(() =>
      useMediaRecoveryController({
        mediaRef,
        resetKey: "video-1|source-a",
        toAbsoluteTime: (time) => time,
        toMediaTime: (time) => time,
        setBuffering: vi.fn(),
        onRecoveryPlayFailed,
      }),
    );

    act(() => result.current.networkError(media, "play", false));
    act(() => vi.advanceTimersByTime(500));
    act(() => result.current.metadataLoaded());
    expect(rejectPlays).toHaveLength(1);

    act(() => result.current.networkError(media, "play", false));
    act(() => vi.advanceTimersByTime(1_000));
    act(() => result.current.metadataLoaded());
    expect(rejectPlays).toHaveLength(2);
    expect(result.current.phase).toBe("recovering");

    await act(async () => {
      rejectPlays[0](new Error("older rejection"));
      await Promise.resolve();
    });
    expect(result.current.phase).toBe("recovering");
    expect(onRecoveryPlayFailed).not.toHaveBeenCalled();

    await act(async () => {
      rejectPlays[1](new Error("current rejection"));
      await Promise.resolve();
    });
    expect(result.current.phase).toBe("healthy");
    expect(onRecoveryPlayFailed).toHaveBeenCalledOnce();
  });

  it("ignores an old recovery rejection after a newer user play succeeds", async () => {
    let rejectRecoveryPlay!: (error: Error) => void;
    const media = createMedia(
      vi.fn(
        () =>
          new Promise<void>((_resolve, reject) => {
            rejectRecoveryPlay = reject;
          }),
      ),
    );
    media.currentTime = 28;
    const mediaRef = { current: media };
    const onRecoveryPlayFailed = vi.fn();
    const { result } = renderHook(() =>
      useMediaRecoveryController({
        mediaRef,
        resetKey: "video-1|source-a",
        toAbsoluteTime: (time) => time,
        toMediaTime: (time) => time,
        setBuffering: vi.fn(),
        onRecoveryPlayFailed,
      }),
    );

    act(() => result.current.networkError(media, "play", false));
    act(() => vi.advanceTimersByTime(500));
    act(() => result.current.metadataLoaded());
    expect(result.current.phase).toBe("recovering");

    act(() => result.current.userPlay());
    act(() => result.current.playing());
    expect(result.current.phase).toBe("healthy");
    await act(async () => {
      rejectRecoveryPlay(new Error("old recovery rejection"));
      await Promise.resolve();
    });

    expect(result.current.phase).toBe("healthy");
    expect(onRecoveryPlayFailed).not.toHaveBeenCalled();
  });

  it("does not hide buffering when stale can-play arrives during an outage", () => {
    const media = createMedia();
    media.currentTime = 17;
    const mediaRef = { current: media };
    const setBuffering = vi.fn();
    const { result } = renderHook(() =>
      useMediaRecoveryController({
        mediaRef,
        resetKey: "video-1|source-a",
        toAbsoluteTime: (time) => time,
        toMediaTime: (time) => time,
        setBuffering,
      }),
    );

    act(() => reportServerResponse(new Response(null, { status: 502 })));
    act(() => result.current.networkError(media, "play", false));
    expect(result.current.phase).toBe("waiting-for-server");
    setBuffering.mockClear();

    act(() => result.current.canPlay(media));

    expect(result.current.phase).toBe("waiting-for-server");
    expect(setBuffering).not.toHaveBeenCalled();
  });

  it("exhausts silent recovery loads and lets a manual retry start fresh", async () => {
    const media = createMedia();
    media.currentTime = 36;
    const mediaRef = { current: media };
    const { result } = renderHook(() =>
      useMediaRecoveryController({
        mediaRef,
        resetKey: "video-1|source-a",
        toAbsoluteTime: (time) => time,
        toMediaTime: (time) => time,
        setBuffering: vi.fn(),
      }),
    );

    act(() => result.current.networkError(media, "play", false));
    await act(() => vi.advanceTimersByTimeAsync(500));
    expect(media.load).toHaveBeenCalledTimes(1);

    for (const retryDelay of [1_000, 1_500]) {
      await act(() => vi.advanceTimersByTimeAsync(8_000));
      await act(() => vi.advanceTimersByTimeAsync(retryDelay));
    }
    expect(media.load).toHaveBeenCalledTimes(3);

    await act(() => vi.advanceTimersByTimeAsync(8_000));
    expect(result.current.phase).toBe("exhausted");

    act(() => result.current.retry());
    expect(result.current.phase).toBe("recovering");
    expect(media.load).toHaveBeenCalledTimes(4);
  });

  it("waits through a confirmed outage and reloads once when the server returns", () => {
    const media = createMedia();
    media.currentTime = 17;
    const mediaRef = { current: media };
    const { result } = renderHook(() =>
      useMediaRecoveryController({
        mediaRef,
        resetKey: "video-1|source-a",
        toAbsoluteTime: (time) => time,
        toMediaTime: (time) => time,
        setBuffering: vi.fn(),
      }),
    );

    act(() => reportServerResponse(new Response(null, { status: 502 })));
    act(() => result.current.networkError(media, "play", false));
    expect(result.current.phase).toBe("waiting-for-server");
    expect(media.load).not.toHaveBeenCalled();

    act(() => reportServerResponse(new Response(null, { status: 200 })));
    expect(result.current.phase).toBe("recovering");
    expect(media.load).toHaveBeenCalledOnce();
  });
});
