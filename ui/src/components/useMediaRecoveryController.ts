import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type RefObject,
} from "react";
import {
  getServerAvailability,
  reportServerConnectionFailure,
  subscribeToServerAvailability,
} from "../state/serverAvailability";
import {
  createMediaRecoveryState,
  reduceMediaRecovery,
  type MediaPlayIntent,
  type MediaRecoveryCommand,
  type MediaRecoveryEvent,
  type MediaRecoveryPhase,
} from "./mediaRecovery";

const MEDIA_STALL_WATCHDOG_MS = 8_000;

interface UseMediaRecoveryControllerOptions {
  mediaRef: RefObject<HTMLVideoElement | null>;
  resetKey: string;
  toAbsoluteTime: (mediaTime: number) => number;
  toMediaTime: (absoluteTime: number) => number;
  setBuffering: (buffering: boolean) => void;
  onRecoveryPlayFailed?: () => void;
}

export interface MediaRecoveryController {
  phase: MediaRecoveryPhase;
  waiting: (media: HTMLVideoElement, playIntent: MediaPlayIntent) => void;
  networkError: (media: HTMLVideoElement, playIntent: MediaPlayIntent, probeServer: boolean) => void;
  metadataLoaded: () => void;
  canPlay: (media: HTMLVideoElement) => void;
  playing: () => void;
  mediaProgress: (media: HTMLVideoElement) => void;
  ended: () => void;
  userPlay: () => void;
  userPause: () => void;
  userSeek: (media: HTMLVideoElement) => void;
  retry: () => void;
}

interface RecoveryTimers {
  retry: ReturnType<typeof setTimeout> | null;
  watchdog: ReturnType<typeof setTimeout> | null;
}

export function useMediaRecoveryController({
  mediaRef,
  resetKey,
  toAbsoluteTime,
  toMediaTime,
  setBuffering,
  onRecoveryPlayFailed,
}: UseMediaRecoveryControllerOptions): MediaRecoveryController {
  const serverAvailability = useSyncExternalStore(
    subscribeToServerAvailability,
    getServerAvailability,
    getServerAvailability,
  );
  const previousServerAvailabilityRef = useRef(serverAvailability);
  const stateRef = useRef(createMediaRecoveryState());
  const timersRef = useRef<RecoveryTimers>({ retry: null, watchdog: null });
  const generationRef = useRef(0);
  const recoveryPlayAttemptRef = useRef(0);
  const mountedRef = useRef(false);
  const optionsRef = useRef({ mediaRef, toAbsoluteTime, toMediaTime, setBuffering, onRecoveryPlayFailed });
  const [phase, setPhase] = useState<MediaRecoveryPhase>("healthy");

  useLayoutEffect(() => {
    optionsRef.current = { mediaRef, toAbsoluteTime, toMediaTime, setBuffering, onRecoveryPlayFailed };
  }, [mediaRef, onRecoveryPlayFailed, setBuffering, toAbsoluteTime, toMediaTime]);

  const cancelTimers = useCallback(() => {
    const timers = timersRef.current;
    if (timers.retry) clearTimeout(timers.retry);
    if (timers.watchdog) clearTimeout(timers.watchdog);
    timersRef.current = { retry: null, watchdog: null };
  }, []);

  const dispatch = useCallback((event: MediaRecoveryEvent) => {
    if (!mountedRef.current) return;

    const previousState = stateRef.current;
    const next = reduceMediaRecovery(previousState, event);
    stateRef.current = next.state;
    setPhase(next.state.phase);
    if (
      next.state.phase === "exhausted"
      || (previousState.phase !== "healthy" && next.state.phase === "healthy")
      || ((event.type === "can-play" || event.type === "playing") && next.state.phase === "healthy")
    ) {
      optionsRef.current.setBuffering(false);
    }

    const execute = (command: MediaRecoveryCommand) => {
      switch (command.type) {
        case "cancel-timers":
          cancelTimers();
          break;
        case "schedule-stall-watchdog":
        case "schedule-recovery-watchdog": {
          const timers = timersRef.current;
          if (timers.watchdog) clearTimeout(timers.watchdog);
          const generation = generationRef.current;
          timers.watchdog = setTimeout(() => {
            if (!mountedRef.current || generationRef.current !== generation) return;
            timersRef.current.watchdog = null;
            if (command.type === "schedule-stall-watchdog" && stateRef.current.phase === "stalled") {
              optionsRef.current.setBuffering(true);
            }
            reportServerConnectionFailure();
            dispatch({
              type: command.type === "schedule-stall-watchdog" ? "stall-timeout" : "recovery-timeout",
              serverAvailable: getServerAvailability() === "available",
            });
          }, MEDIA_STALL_WATCHDOG_MS);
          break;
        }
        case "schedule-retry": {
          const timers = timersRef.current;
          if (timers.retry) clearTimeout(timers.retry);
          const generation = generationRef.current;
          timers.retry = setTimeout(() => {
            if (!mountedRef.current || generationRef.current !== generation) return;
            timersRef.current.retry = null;
            dispatch({ type: "retry-timer" });
          }, command.delayMs);
          break;
        }
        case "load":
          recoveryPlayAttemptRef.current += 1;
          optionsRef.current.setBuffering(true);
          optionsRef.current.mediaRef.current?.load();
          break;
        case "seek": {
          const media = optionsRef.current.mediaRef.current;
          if (media) media.currentTime = optionsRef.current.toMediaTime(command.position);
          break;
        }
        case "play": {
          const media = optionsRef.current.mediaRef.current;
          if (!media) break;
          const generation = generationRef.current;
          const playAttempt = ++recoveryPlayAttemptRef.current;
          void media.play().catch(() => {
            if (
              !mountedRef.current
              || generationRef.current !== generation
              || recoveryPlayAttemptRef.current !== playAttempt
            ) return;
            dispatch({ type: "play-failed" });
            optionsRef.current.onRecoveryPlayFailed?.();
          });
          break;
        }
      }
    };

    next.commands.forEach(execute);
  }, [cancelTimers]);

  useLayoutEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      generationRef.current += 1;
      cancelTimers();
    };
  }, [cancelTimers]);

  useLayoutEffect(() => {
    generationRef.current += 1;
    dispatch({ type: "reset" });
  }, [dispatch, resetKey]);

  useEffect(() => {
    const previousAvailability = previousServerAvailabilityRef.current;
    previousServerAvailabilityRef.current = serverAvailability;
    if (previousAvailability === serverAvailability) return;
    dispatch({ type: serverAvailability === "available" ? "server-available" : "server-unavailable" });
  }, [dispatch, serverAvailability]);

  const waiting = useCallback((media: HTMLVideoElement, playIntent: MediaPlayIntent) => {
    optionsRef.current.setBuffering(true);
    dispatch({ type: "waiting", position: optionsRef.current.toAbsoluteTime(media.currentTime), playIntent });
  }, [dispatch]);

  const networkError = useCallback((media: HTMLVideoElement, playIntent: MediaPlayIntent, probeServer: boolean) => {
    if (probeServer) reportServerConnectionFailure();
    optionsRef.current.setBuffering(true);
    dispatch({
      type: "network-error",
      position: optionsRef.current.toAbsoluteTime(media.currentTime),
      playIntent,
      serverAvailable: getServerAvailability() === "available",
    });
  }, [dispatch]);

  const metadataLoaded = useCallback(() => {
    dispatch({ type: "metadata-loaded", serverAvailable: getServerAvailability() === "available" });
  }, [dispatch]);

  const canPlay = useCallback((media: HTMLVideoElement) => {
    if (media.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA) dispatch({ type: "can-play" });
  }, [dispatch]);

  const playing = useCallback(() => {
    dispatch({ type: "playing" });
  }, [dispatch]);

  const mediaProgress = useCallback((media: HTMLVideoElement) => {
    const state = stateRef.current;
    if (state.phase === "healthy" || state.playIntent !== "play") return;
    dispatch({ type: "media-progress", position: optionsRef.current.toAbsoluteTime(media.currentTime) });
  }, [dispatch]);

  const ended = useCallback(() => dispatch({ type: "ended" }), [dispatch]);
  const userPlay = useCallback(() => {
    recoveryPlayAttemptRef.current += 1;
    dispatch({ type: "user-play" });
  }, [dispatch]);
  const userPause = useCallback(() => dispatch({ type: "user-pause" }), [dispatch]);
  const userSeek = useCallback((media: HTMLVideoElement) => {
    dispatch({ type: "user-seek", position: optionsRef.current.toAbsoluteTime(media.currentTime) });
  }, [dispatch]);
  const retry = useCallback(() => dispatch({ type: "manual-retry" }), [dispatch]);

  return useMemo(() => ({
    phase,
    waiting,
    networkError,
    metadataLoaded,
    canPlay,
    playing,
    mediaProgress,
    ended,
    userPlay,
    userPause,
    userSeek,
    retry,
  }), [canPlay, ended, mediaProgress, metadataLoaded, networkError, phase, playing, retry, userPause, userPlay, userSeek, waiting]);
}
