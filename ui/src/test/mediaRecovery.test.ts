import { describe, expect, it } from "vitest";
import {
  createMediaRecoveryState,
  reduceMediaRecovery,
  type MediaRecoveryEvent,
  type MediaRecoveryState,
} from "../components/mediaRecovery";

function apply(state: MediaRecoveryState, event: MediaRecoveryEvent) {
  return reduceMediaRecovery(state, event);
}

describe("media recovery state machine", () => {
  it("turns a prolonged stall without a media error into a reload", () => {
    const stalled = apply(createMediaRecoveryState(), { type: "waiting", position: 24, playIntent: "play" });
    expect(stalled.state.phase).toBe("stalled");
    expect(stalled.commands).toEqual([{ type: "schedule-stall-watchdog" }]);

    const timedOut = apply(stalled.state, { type: "stall-timeout", serverAvailable: true });
    expect(timedOut.state).toMatchObject({ phase: "recovering", resumeAt: 24, playIntent: "play" });
    expect(timedOut.commands).toEqual([{ type: "load" }, { type: "schedule-recovery-watchdog" }]);
  });

  it("returns to healthy when a stall resolves before its watchdog", () => {
    const stalled = apply(createMediaRecoveryState(), { type: "waiting", position: 24, playIntent: "play" });
    const recovered = apply(stalled.state, { type: "can-play" });

    expect(recovered.state).toEqual(createMediaRecoveryState());
    expect(recovered.commands).toEqual([{ type: "cancel-timers" }]);
  });

  it("returns to healthy when playback advances without another readiness event", () => {
    const stalled = apply(createMediaRecoveryState(), { type: "waiting", position: 24, playIntent: "play" });
    const progressed = apply(stalled.state, { type: "media-progress", position: 25 });

    expect(progressed.state).toEqual(createMediaRecoveryState());
    expect(progressed.commands).toEqual([{ type: "cancel-timers" }]);
  });

  it("finishes recovery when playback advances without another readiness event", () => {
    let result = apply(createMediaRecoveryState(), {
      type: "network-error",
      position: 42,
      playIntent: "play",
      serverAvailable: true,
    });
    result = apply(result.state, { type: "retry-timer" });
    result = apply(result.state, { type: "metadata-loaded", serverAvailable: true });
    result = apply(result.state, { type: "media-progress", position: 43 });

    expect(result.state).toEqual(createMediaRecoveryState());
    expect(result.commands).toEqual([{ type: "cancel-timers" }]);
  });

  it("exposes retry exhaustion and lets an explicit retry start a fresh load", () => {
    let state = createMediaRecoveryState();
    for (let attempt = 1; attempt <= 4; attempt += 1) {
      const result = apply(state, { type: "network-error", position: 42, playIntent: "play", serverAvailable: true });
      state = result.state;
      if (attempt <= 3) expect(result.commands).toEqual([{ type: "schedule-retry", delayMs: attempt * 500 }]);
    }
    expect(state.phase).toBe("exhausted");

    const retry = apply(state, { type: "manual-retry" });
    expect(retry.state).toMatchObject({ phase: "recovering", attempts: 0, playIntent: "play" });
    expect(retry.commands).toEqual([{ type: "load" }, { type: "schedule-recovery-watchdog" }]);
  });

  it("keeps retrying when a recovery load produces no further media event", () => {
    let result = apply(createMediaRecoveryState(), {
      type: "network-error",
      position: 42,
      playIntent: "play",
      serverAvailable: true,
    });
    result = apply(result.state, { type: "retry-timer" });
    expect(result.commands).toEqual([{ type: "load" }, { type: "schedule-recovery-watchdog" }]);

    result = apply(result.state, { type: "recovery-timeout", serverAvailable: true });
    expect(result.state).toMatchObject({ phase: "retrying", attempts: 2 });
    expect(result.commands).toEqual([{ type: "schedule-retry", delayMs: 1_000 }]);
  });

  it("keeps a watchdog armed while recovered metadata is still waiting to play", () => {
    let result = apply(createMediaRecoveryState(), {
      type: "network-error",
      position: 42,
      playIntent: "play",
      serverAvailable: true,
    });
    result = apply(result.state, { type: "retry-timer" });
    result = apply(result.state, { type: "metadata-loaded", serverAvailable: true });

    expect(result.state.phase).toBe("recovering");
    expect(result.commands).toEqual([
      { type: "cancel-timers" },
      { type: "schedule-recovery-watchdog" },
      { type: "seek", position: 42 },
      { type: "play" },
    ]);

    result = apply(result.state, { type: "recovery-timeout", serverAvailable: true });
    expect(result.state).toMatchObject({ phase: "retrying", attempts: 2 });
  });

  it("returns to healthy on can-play even while recovery play intent is pending", () => {
    let result = apply(createMediaRecoveryState(), {
      type: "network-error",
      position: 42,
      playIntent: "play",
      serverAvailable: true,
    });
    result = apply(result.state, { type: "retry-timer" });
    result = apply(result.state, { type: "metadata-loaded", serverAvailable: true });
    result = apply(result.state, { type: "can-play" });

    expect(result.state).toEqual(createMediaRecoveryState());
    expect(result.commands).toEqual([{ type: "cancel-timers" }]);
  });

  it("ignores stale can-play events while waiting for the server", () => {
    const waiting = apply(createMediaRecoveryState(), {
      type: "network-error",
      position: 42,
      playIntent: "play",
      serverAvailable: false,
    });

    const staleCanPlay = apply(waiting.state, { type: "can-play" });

    expect(staleCanPlay.state).toBe(waiting.state);
    expect(staleCanPlay.commands).toEqual([]);
  });

  it("ignores stale metadata while waiting for the server", () => {
    const waiting = apply(createMediaRecoveryState(), {
      type: "network-error",
      position: 42,
      playIntent: "play",
      serverAvailable: false,
    });

    const staleMetadata = apply(waiting.state, { type: "metadata-loaded", serverAvailable: false });

    expect(staleMetadata.state).toBe(waiting.state);
    expect(staleMetadata.commands).toEqual([]);
  });

  it("returns to a healthy paused state when recovery playback is rejected", () => {
    let result = apply(createMediaRecoveryState(), {
      type: "network-error",
      position: 42,
      playIntent: "play",
      serverAvailable: true,
    });
    result = apply(result.state, { type: "retry-timer" });
    result = apply(result.state, { type: "metadata-loaded", serverAvailable: true });
    expect(result.commands).toContainEqual({ type: "play" });

    result = apply(result.state, { type: "play-failed" });

    expect(result.state).toEqual(createMediaRecoveryState());
    expect(result.commands).toEqual([{ type: "cancel-timers" }]);
  });

  it("preserves the latest seek and explicit pause through server recovery", () => {
    let result = apply(createMediaRecoveryState(), {
      type: "network-error",
      position: 40,
      playIntent: "play",
      serverAvailable: false,
    });
    result = apply(result.state, { type: "user-seek", position: 118 });
    result = apply(result.state, { type: "user-pause" });
    result = apply(result.state, { type: "server-available" });
    expect(result.commands).toEqual([{ type: "load" }, { type: "schedule-recovery-watchdog" }]);

    result = apply(result.state, { type: "metadata-loaded", serverAvailable: true });
    expect(result.commands).toEqual([
      { type: "cancel-timers" },
      { type: "schedule-recovery-watchdog" },
      { type: "seek", position: 118 },
    ]);
    expect(result.state.playIntent).toBe("pause");
  });

  it("waits for the server instead of spending retries during a confirmed outage", () => {
    const failed = apply(createMediaRecoveryState(), {
      type: "network-error",
      position: 17,
      playIntent: "play",
      serverAvailable: false,
    });
    expect(failed.state).toMatchObject({ phase: "waiting-for-server", attempts: 0 });
    expect(failed.commands).toEqual([{ type: "cancel-timers" }]);

    const recovered = apply(failed.state, { type: "server-available" });
    expect(recovered.state.phase).toBe("recovering");
    expect(recovered.commands).toEqual([{ type: "load" }, { type: "schedule-recovery-watchdog" }]);
  });
});
