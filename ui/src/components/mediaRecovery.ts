export type MediaRecoveryPhase =
  | "healthy"
  | "stalled"
  | "waiting-for-server"
  | "retrying"
  | "recovering"
  | "exhausted";

export type MediaPlayIntent = "play" | "pause";

export interface MediaRecoveryState {
  phase: MediaRecoveryPhase;
  resumeAt: number | null;
  playIntent: MediaPlayIntent;
  attempts: number;
}

export type MediaRecoveryEvent =
  | { type: "waiting"; position: number; playIntent: MediaPlayIntent }
  | { type: "stall-timeout"; serverAvailable: boolean }
  | { type: "recovery-timeout"; serverAvailable: boolean }
  | { type: "network-error"; position: number; playIntent: MediaPlayIntent; serverAvailable: boolean }
  | { type: "retry-timer" }
  | { type: "server-unavailable" }
  | { type: "server-available" }
  | { type: "metadata-loaded"; serverAvailable: boolean }
  | { type: "play-failed" }
  | { type: "can-play" }
  | { type: "playing" }
  | { type: "media-progress"; position: number }
  | { type: "ended" }
  | { type: "user-play" }
  | { type: "user-pause" }
  | { type: "user-seek"; position: number }
  | { type: "manual-retry" }
  | { type: "reset" };

export type MediaRecoveryCommand =
  | { type: "schedule-stall-watchdog" }
  | { type: "schedule-recovery-watchdog" }
  | { type: "schedule-retry"; delayMs: number }
  | { type: "cancel-timers" }
  | { type: "load" }
  | { type: "seek"; position: number }
  | { type: "play" };

export interface MediaRecoveryTransition {
  state: MediaRecoveryState;
  commands: MediaRecoveryCommand[];
}

const MAX_RETRY_ATTEMPTS = 3;
const MEDIA_PROGRESS_EPSILON_SECONDS = 0.05;

function retryOrWait(state: MediaRecoveryState, serverAvailable: boolean): MediaRecoveryTransition {
  if (!serverAvailable) {
    return transition({ ...state, phase: "waiting-for-server" }, [{ type: "cancel-timers" }]);
  }
  if (state.attempts >= MAX_RETRY_ATTEMPTS) {
    return transition({ ...state, phase: "exhausted" }, [{ type: "cancel-timers" }]);
  }
  const attempts = state.attempts + 1;
  return transition(
    { ...state, phase: "retrying", attempts },
    [{ type: "schedule-retry", delayMs: attempts * 500 }],
  );
}

export function createMediaRecoveryState(): MediaRecoveryState {
  return { phase: "healthy", resumeAt: null, playIntent: "pause", attempts: 0 };
}

function meaningfulPosition(next: number, previous: number | null): number | null {
  return Number.isFinite(next) && next >= 0 ? next : previous;
}

function transition(state: MediaRecoveryState, commands: MediaRecoveryCommand[] = []): MediaRecoveryTransition {
  return { state, commands };
}

export function reduceMediaRecovery(state: MediaRecoveryState, event: MediaRecoveryEvent): MediaRecoveryTransition {
  switch (event.type) {
    case "waiting":
      if (state.phase !== "healthy") return transition(state);
      return transition(
        { ...state, phase: "stalled", resumeAt: meaningfulPosition(event.position, state.resumeAt), playIntent: event.playIntent },
        [{ type: "schedule-stall-watchdog" }],
      );
    case "stall-timeout":
      if (state.phase !== "stalled") return transition(state);
      return event.serverAvailable
        ? transition({ ...state, phase: "recovering" }, [{ type: "load" }, { type: "schedule-recovery-watchdog" }])
        : transition({ ...state, phase: "waiting-for-server" }, [{ type: "cancel-timers" }]);
    case "recovery-timeout":
      return state.phase === "recovering" ? retryOrWait(state, event.serverAvailable) : transition(state);
    case "network-error": {
      const failed = {
        ...state,
        resumeAt: meaningfulPosition(event.position, state.resumeAt),
        playIntent: state.phase === "healthy" ? event.playIntent : state.playIntent,
      };
      return retryOrWait(failed, event.serverAvailable);
    }
    case "retry-timer":
      return state.phase === "retrying"
        ? transition({ ...state, phase: "recovering" }, [{ type: "load" }, { type: "schedule-recovery-watchdog" }])
        : transition(state);
    case "server-unavailable":
      return state.phase === "healthy"
        ? transition(state)
        : transition({ ...state, phase: "waiting-for-server" }, [{ type: "cancel-timers" }]);
    case "server-available":
      return state.phase === "waiting-for-server"
        ? transition(
          { ...state, phase: "recovering", attempts: 0 },
          [{ type: "load" }, { type: "schedule-recovery-watchdog" }],
        )
        : transition(state);
    case "metadata-loaded": {
      if (["healthy", "stalled", "waiting-for-server", "exhausted"].includes(state.phase)) return transition(state);
      const commands: MediaRecoveryCommand[] = [];
      if (state.resumeAt != null) commands.push({ type: "seek", position: state.resumeAt });
      if (state.playIntent === "play" && event.serverAvailable) commands.push({ type: "play" });
      return transition(
        { ...state, phase: "recovering" },
        [{ type: "cancel-timers" }, { type: "schedule-recovery-watchdog" }, ...commands],
      );
    }
    case "play-failed":
      return state.phase === "recovering"
        ? transition(createMediaRecoveryState(), [{ type: "cancel-timers" }])
        : transition(state);
    case "can-play":
      return state.phase === "stalled" || state.phase === "recovering"
        ? transition(createMediaRecoveryState(), [{ type: "cancel-timers" }])
        : transition(state);
    case "playing":
    case "ended":
      return transition(createMediaRecoveryState(), [{ type: "cancel-timers" }]);
    case "media-progress": {
      const position = meaningfulPosition(event.position, state.resumeAt);
      if (
        state.phase === "healthy"
        || state.playIntent !== "play"
        || position == null
        || state.resumeAt == null
        || position <= state.resumeAt + MEDIA_PROGRESS_EPSILON_SECONDS
      ) {
        return transition(state);
      }
      return transition(createMediaRecoveryState(), [{ type: "cancel-timers" }]);
    }
    case "user-play":
      return state.phase === "healthy" ? transition(state) : transition({ ...state, playIntent: "play" });
    case "user-pause":
      return state.phase === "healthy" ? transition(state) : transition({ ...state, playIntent: "pause" });
    case "user-seek":
      return state.phase === "healthy"
        ? transition(state)
        : transition({ ...state, resumeAt: meaningfulPosition(event.position, state.resumeAt) });
    case "manual-retry":
      return state.phase === "exhausted"
        ? transition(
          { ...state, phase: "recovering", attempts: 0, playIntent: "play" },
          [{ type: "load" }, { type: "schedule-recovery-watchdog" }],
        )
        : transition(state);
    case "reset":
      return transition(createMediaRecoveryState(), [{ type: "cancel-timers" }]);
  }
}
