import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Gauge, Headphones, MonitorPlay, Pause, Play, SkipBack, SkipForward, SlidersHorizontal, Volume2, VolumeX } from "lucide-react";
import { createPlaybackTracker, trackInteraction, type PlaybackTrackingTarget } from "../utils/interactionTracking";
import { usePlaybackPreferences } from "../utils/playbackPreferences";

const VOLUME_KEY = "cove-audio-player-volume";
const MUTED_KEY = "cove-audio-player-muted";
const RATE_KEY = "cove-audio-player-rate";
const PITCH_KEY = "cove-audio-player-pitch";

type PitchAwareAudio = HTMLAudioElement & {
  preservesPitch?: boolean;
  webkitPreservesPitch?: boolean;
  mozPreservesPitch?: boolean;
  webkitShowPlaybackTargetPicker?: () => void;
};

function roundTime(value: number) {
  return Math.round(value * 1000) / 1000;
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

function formatTime(value: number) {
  if (!Number.isFinite(value) || value <= 0) {
    return "0:00";
  }

  const hours = Math.floor(value / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  const seconds = Math.floor(value % 60);
  return hours > 0
    ? `${hours}:${minutes.toString().padStart(2, "0")}:${seconds.toString().padStart(2, "0")}`
    : `${minutes}:${seconds.toString().padStart(2, "0")}`;
}

function roundRate(value: number) {
  return Math.round(value * 10) / 10;
}

function formatPitch(value: number) {
  if (!Number.isFinite(value) || value === 0) {
    return "0 st";
  }

  return `${value > 0 ? "+" : ""}${value} st`;
}

function rateToSliderValue(value: number) {
  if (value <= 0.25) {
    return 0;
  }

  return clamp(Math.round((roundRate(value) - 0.2) * 10), 0, 28);
}

function sliderValueToRate(value: number) {
  if (value <= 0) {
    return 0.25;
  }

  return roundRate(clamp(0.2 + value / 10, 0.25, 3));
}

function setPreservePitch(audio: PitchAwareAudio, value: boolean) {
  audio.preservesPitch = value;
  audio.webkitPreservesPitch = value;
  audio.mozPreservesPitch = value;
}

type WebkitAudioWindow = Window & { webkitAudioContext?: typeof AudioContext };

function shouldUseNativeAudioOutput() {
  if (typeof navigator === "undefined") {
    return false;
  }

  const userAgent = navigator.userAgent || "";
  const platform = navigator.platform || "";
  const maxTouchPoints = navigator.maxTouchPoints || 0;
  return /iPad|iPhone|iPod/.test(userAgent) || (platform === "MacIntel" && maxTouchPoints > 1);
}

class GranularPitchShifter {
  readonly input: GainNode;
  readonly output: GainNode;

  private readonly directGain: GainNode;
  private readonly wetGain: GainNode;
  private readonly delayA: DelayNode;
  private readonly delayB: DelayNode;
  private readonly gainA: GainNode;
  private readonly gainB: GainNode;
  private readonly maxDelay = 0.12;
  private readonly modulationDepth = 0.1;
  private modulationSources: AudioBufferSourceNode[] = [];

  constructor(private readonly context: AudioContext) {
    this.input = context.createGain();
    this.output = context.createGain();
    this.directGain = context.createGain();
    this.wetGain = context.createGain();
    this.delayA = context.createDelay(this.maxDelay + 0.02);
    this.delayB = context.createDelay(this.maxDelay + 0.02);
    this.gainA = context.createGain();
    this.gainB = context.createGain();

    this.wetGain.gain.value = 0;
    this.delayA.delayTime.value = 0;
    this.delayB.delayTime.value = 0;
    this.gainA.gain.value = 0;
    this.gainB.gain.value = 0;

    this.input.connect(this.directGain).connect(this.output);
    this.input.connect(this.delayA).connect(this.gainA).connect(this.wetGain).connect(this.output);
    this.input.connect(this.delayB).connect(this.gainB).connect(this.wetGain);
  }

  setPitch(semitones: number) {
    const normalized = clamp(Math.round(semitones), -12, 12);
    this.stopModulation();

    if (normalized === 0) {
      this.directGain.gain.setTargetAtTime(1, this.context.currentTime, 0.015);
      this.wetGain.gain.setTargetAtTime(0, this.context.currentTime, 0.015);
      return;
    }

    this.directGain.gain.setTargetAtTime(0, this.context.currentTime, 0.015);
    this.wetGain.gain.setTargetAtTime(1, this.context.currentTime, 0.015);

    const ratio = Math.pow(2, normalized / 12);
    const scanDuration = clamp(this.modulationDepth / Math.abs(ratio - 1), 0.06, 1.4);
    const now = this.context.currentTime;

    const delayBuffer = this.createDelayRampBuffer(scanDuration, ratio > 1);
    const fadeBuffer = this.createFadeBuffer(scanDuration);

    this.modulationSources = [
      this.startLoopingBuffer(delayBuffer, this.delayA.delayTime, now, 0),
      this.startLoopingBuffer(fadeBuffer, this.gainA.gain, now, 0),
      this.startLoopingBuffer(delayBuffer, this.delayB.delayTime, now, scanDuration / 2),
      this.startLoopingBuffer(fadeBuffer, this.gainB.gain, now, scanDuration / 2),
    ];
  }

  disconnect() {
    this.stopModulation();
    this.input.disconnect();
    this.output.disconnect();
    this.directGain.disconnect();
    this.wetGain.disconnect();
    this.delayA.disconnect();
    this.delayB.disconnect();
    this.gainA.disconnect();
    this.gainB.disconnect();
  }

  private stopModulation() {
    for (const source of this.modulationSources) {
      try {
        source.stop();
      } catch {
        // Source may already have been stopped by the browser.
      }
      source.disconnect();
    }
    this.modulationSources = [];

    const now = this.context.currentTime;
    for (const param of [this.delayA.delayTime, this.delayB.delayTime, this.gainA.gain, this.gainB.gain]) {
      param.cancelScheduledValues(now);
      param.setValueAtTime(0, now);
    }
  }

  private createDelayRampBuffer(duration: number, shiftUp: boolean) {
    const sampleRate = this.context.sampleRate;
    const length = Math.max(2, Math.round(duration * sampleRate));
    const buffer = this.context.createBuffer(1, length, sampleRate);
    const data = buffer.getChannelData(0);
    for (let index = 0; index < length; index += 1) {
      const progress = index / (length - 1);
      data[index] = shiftUp
        ? this.modulationDepth * (1 - progress)
        : this.modulationDepth * progress;
    }

    return buffer;
  }

  private createFadeBuffer(duration: number) {
    const sampleRate = this.context.sampleRate;
    const length = Math.max(2, Math.round(duration * sampleRate));
    const buffer = this.context.createBuffer(1, length, sampleRate);
    const data = buffer.getChannelData(0);
    for (let index = 0; index < length; index += 1) {
      const progress = index / (length - 1);
      data[index] = Math.sin(Math.PI * progress);
    }

    return buffer;
  }

  private startLoopingBuffer(buffer: AudioBuffer, target: AudioParam, when: number, offset: number) {
    const source = this.context.createBufferSource();
    source.buffer = buffer;
    source.loop = true;
    source.connect(target);
    source.start(when, offset % buffer.duration);
    return source;
  }
}

function ControlFlyout({
  label,
  value,
  icon,
  children,
}: {
  label: string;
  value: string;
  icon: ReactNode;
  children: ReactNode;
}) {
  const rootRef = useRef<HTMLDivElement>(null);
  const [pointerInside, setPointerInside] = useState(false);
  const [focusInside, setFocusInside] = useState(false);
  const [pinned, setPinned] = useState(false);
  const [dismissedUntilLeave, setDismissedUntilLeave] = useState(false);
  const open = !dismissedUntilLeave && (pointerInside || focusInside || pinned);

  useEffect(() => {
    if (!pinned) {
      return;
    }

    const handlePointerDown = (event: PointerEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) {
        setPinned(false);
        setDismissedUntilLeave(false);
      }
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setPinned(false);
        setDismissedUntilLeave(true);
      }
    };

    document.addEventListener("pointerdown", handlePointerDown);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("pointerdown", handlePointerDown);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [pinned]);

  const toggleOpen = () => {
    setPinned((current) => (pointerInside || focusInside ? true : !current));
    setDismissedUntilLeave(false);
  };

  return (
    <div
      ref={rootRef}
      className="relative"
      data-audio-control={label.toLowerCase()}
      onPointerEnter={(event) => {
        if (event.pointerType === "touch") {
          return;
        }
        setPointerInside(true);
        setDismissedUntilLeave(false);
      }}
      onPointerLeave={(event) => {
        if (event.pointerType === "touch") {
          return;
        }
        setPointerInside(false);
        setDismissedUntilLeave(false);
      }}
      onFocusCapture={() => setFocusInside(true)}
      onBlurCapture={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) {
          setFocusInside(false);
          setDismissedUntilLeave(false);
        }
      }}
    >
      <button
        type="button"
        className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-border bg-background/80 text-secondary transition hover:border-accent hover:text-accent focus-visible:border-accent focus-visible:text-accent focus-visible:outline-none"
        title={`${label}: ${value}`}
        aria-label={`${label}: ${value}`}
        aria-expanded={open}
        onPointerDown={(event) => {
          event.preventDefault();
          toggleOpen();
        }}
        onKeyDown={(event) => {
          if (event.key !== "Enter" && event.key !== " ") {
            return;
          }

          event.preventDefault();
          toggleOpen();
        }}
      >
        {icon}
      </button>
      <div className={["fixed inset-x-3 bottom-[calc(env(safe-area-inset-bottom)+1rem)] z-50 w-auto max-w-[calc(100vw-1.5rem)] pb-0 transition sm:absolute sm:bottom-full sm:left-auto sm:right-0 sm:w-64 sm:max-w-none sm:pb-2", open ? "pointer-events-auto translate-y-0 opacity-100" : "pointer-events-none translate-y-1 opacity-0"].join(" ")}>
        <div className="rounded-lg border border-border bg-surface p-3 text-foreground shadow-lg">
          <div className="mb-2 flex items-center justify-between gap-3 text-xs font-medium text-secondary">
            <span>{label}</span>
            <span className="text-foreground">{value}</span>
          </div>
          {children}
        </div>
      </div>
    </div>
  );
}

export function AudioPlayer({
  streamUrl,
  format,
  title,
  subtitle,
  coverUrl,
  duration,
  resumeTime,
  hasVideoTrack = false,
  trackingEnabled = true,
  playbackTracking,
  autostart = false,
  autostartToken = 0,
  onPlay,
  onPause,
  onPlaybackStateChange,
  onSeekRegister,
  onEnded,
  clip,
}: {
  streamUrl: string;
  format: string;
  title: string;
  subtitle?: string;
  coverUrl?: string | null;
  duration: number;
  resumeTime?: number;
  hasVideoTrack?: boolean;
  trackingEnabled?: boolean;
  playbackTracking?: PlaybackTrackingTarget;
  autostart?: boolean;
  autostartToken?: number;
  onPlay?: () => void;
  onPause?: () => void;
  onPlaybackStateChange?: (playing: boolean) => void;
  onSeekRegister?: (fn: (time: number) => void) => void;
  onEnded?: () => void;
  clip?: { start: number; end?: number | null; loop?: boolean };
}) {
  const audioRef = useRef<HTMLAudioElement>(null);
  const playbackPreferences = usePlaybackPreferences();
  const playbackTracker = useRef(createPlaybackTracker());
  const intervalStart = useRef<number | null>(null);
  const lastSeenTime = useRef(0);
  const lastKeepaliveSentAt = useRef(0);
  const lastTickAt = useRef(0);
  const resumeAppliedRef = useRef<string | null>(null);
  const pendingAutostartRef = useRef(false);
  const lastLoadedSourceRef = useRef<string | null>(null);
  const clipEndedHandled = useRef(false);
  const pitchGraphRef = useRef<{ context: AudioContext; source: MediaElementAudioSourceNode; shifter: GranularPitchShifter } | null>(null);
  const [playing, setPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [buffered, setBuffered] = useState(0);
  const [measuredDuration, setMeasuredDuration] = useState(duration);
  const [volume, setVolume] = useState(() => {
    if (typeof window === "undefined") {
      return 1;
    }

    const saved = window.localStorage.getItem(VOLUME_KEY);
    const parsed = saved == null ? 1 : Number(saved);
    return Number.isFinite(parsed) ? clamp(parsed, 0, 1) : 1;
  });
  const [muted, setMuted] = useState(() => {
    if (typeof window === "undefined") {
      return false;
    }

    return window.localStorage.getItem(MUTED_KEY) === "true";
  });
  const [rate, setRate] = useState(() => {
    if (typeof window === "undefined") {
      return 1;
    }

    const saved = window.localStorage.getItem(RATE_KEY);
    const parsed = saved == null ? 1 : Number(saved);
    return Number.isFinite(parsed) ? roundRate(clamp(parsed, 0.25, 3)) : 1;
  });
  const [pitchSemitones, setPitchSemitones] = useState(() => {
    if (typeof window === "undefined") {
      return 0;
    }

    const saved = window.localStorage.getItem(PITCH_KEY);
    const parsed = saved == null ? 0 : Number(saved);
    return Number.isFinite(parsed) ? clamp(Math.round(parsed), -12, 12) : 0;
  });
  const [remotePlaybackAvailable, setRemotePlaybackAvailable] = useState(false);
  const nativeAudioOutput = useMemo(() => shouldUseNativeAudioOutput(), []);

  const trackingTarget = useMemo<PlaybackTrackingTarget | null>(() => {
    if (!trackingEnabled) {
      return null;
    }

    if (!playbackTracking) {
      return null;
    }

    return {
      ...playbackTracking,
      clipStartSec: clip?.start ?? playbackTracking.clipStartSec,
      clipEndSec: clip?.end ?? playbackTracking.clipEndSec,
      autoplay: autostart ?? playbackTracking.autoplay,
      muted,
      playbackRate: rate,
      route: typeof window === "undefined" ? playbackTracking.route : playbackTracking.route ?? `${window.location.pathname}${window.location.search}${window.location.hash}`,
    };
  }, [autostart, clip?.end, clip?.start, muted, playbackTracking, rate, trackingEnabled]);
  const trackingTargetSignature = useMemo(() => JSON.stringify(trackingTarget), [trackingTarget]);

  const trackAudioInteraction = useCallback((kind: "pause" | "seek", meta: Record<string, unknown> = {}) => {
    if (!trackingTarget) {
      return;
    }

    trackInteraction({
      hostType: trackingTarget.hostType as never,
      hostId: trackingTarget.hostId,
      kind,
      meta: {
        surface: trackingTarget.surface,
        scopeKey: trackingTarget.scopeKey,
        groupItemId: trackingTarget.groupItemId,
        parentHostType: trackingTarget.parentHostType,
        parentHostId: trackingTarget.parentHostId,
        itemHostType: trackingTarget.itemHostType,
        itemHostId: trackingTarget.itemHostId,
        segmentId: trackingTarget.segmentId,
        clipStartSec: trackingTarget.clipStartSec,
        clipEndSec: trackingTarget.clipEndSec,
        playbackRate: rate,
        muted,
        ...meta,
      },
    });
  }, [muted, rate, trackingTarget]);

  const disposePitchGraph = useCallback(() => {
    const graph = pitchGraphRef.current;
    if (!graph) {
      return;
    }

    graph.source.disconnect();
    graph.shifter.disconnect();
    pitchGraphRef.current = null;
    if (graph.context.state !== "closed") {
      void graph.context.close().catch(() => {});
    }
  }, []);

  const ensurePitchGraph = useCallback(() => {
    const audio = audioRef.current;
    if (!audio || nativeAudioOutput || typeof window === "undefined") {
      return null;
    }

    if (pitchGraphRef.current) {
      return pitchGraphRef.current;
    }

    const AudioContextConstructor = window.AudioContext ?? (window as WebkitAudioWindow).webkitAudioContext;
    if (!AudioContextConstructor) {
      return null;
    }

    try {
      const context = new AudioContextConstructor();
      const source = context.createMediaElementSource(audio);
      const shifter = new GranularPitchShifter(context);
      source.connect(shifter.input);
      shifter.output.connect(context.destination);
      pitchGraphRef.current = { context, source, shifter };
      return pitchGraphRef.current;
    } catch {
      return null;
    }
  }, [nativeAudioOutput]);

  useEffect(() => () => {
    disposePitchGraph();
  }, [disposePitchGraph]);

  const flushInterval = useCallback((state: string, mode: "default" | "keepalive" = "default") => {
    const audio = audioRef.current;
    if (!trackingTarget || !audio || intervalStart.current == null) {
      return;
    }

    const startSec = intervalStart.current;
    const endSec = roundTime(lastSeenTime.current);
    if (endSec <= startSec) {
      return;
    }

    playbackTracker.current.recordInterval({
      startSec,
      endSec,
      mediaDurationSec: Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : Math.max(duration, endSec),
      currentPositionSec: endSec,
      state,
      mode,
    });
  }, [duration, trackingTarget]);

  const startTrackedInterval = useCallback((time: number) => {
    intervalStart.current = time;
    lastSeenTime.current = time;
    lastKeepaliveSentAt.current = Date.now();
    lastTickAt.current = Date.now();
  }, []);

  useEffect(() => {
    void playbackTracker.current.setTarget(trackingTarget);
    return () => {
      void playbackTracker.current.dispose();
    };
  }, [trackingTargetSignature]);

  useEffect(() => {
    // Flush the OPEN interval AND any already-queued intervals (e.g. one a pause put on the batch timer)
    // via keepalive, so a refresh/close/navigation never drops the last watched span.
    const flushAllKeepalive = () => {
      flushInterval("paused", "keepalive");
      void playbackTracker.current.flush("paused", "keepalive");
    };

    const handleVisibilityChange = () => {
      if (document.visibilityState === "hidden") {
        flushAllKeepalive();
        intervalStart.current = null;
      } else if (document.visibilityState === "visible") {
        const audio = audioRef.current;
        if (audio && !audio.paused) {
          startTrackedInterval(roundTime(audio.currentTime));
        }
      }
    };
    const handlePageHide = () => flushAllKeepalive();

    window.addEventListener("pagehide", handlePageHide);
    document.addEventListener("visibilitychange", handleVisibilityChange);
    return () => {
      window.removeEventListener("pagehide", handlePageHide);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
      flushAllKeepalive();
    };
  }, [flushInterval, startTrackedInterval]);

  useEffect(() => {
    const audio = audioRef.current;
    if (!audio) {
      return;
    }

    audio.volume = volume;
    audio.muted = muted;
  }, [muted, volume]);

  useEffect(() => {
    const audio = audioRef.current as PitchAwareAudio | null;
    if (!audio) {
      return;
    }

    audio.defaultPlaybackRate = rate;
    audio.playbackRate = rate;
    setPreservePitch(audio, true);
    if (nativeAudioOutput) {
      disposePitchGraph();
      audio.dataset.pitchShiftSemitones = String(pitchSemitones);
      audio.dataset.pitchShiftActive = "false";
      return;
    }

    const graph = pitchSemitones !== 0 ? ensurePitchGraph() : pitchGraphRef.current;
    graph?.shifter.setPitch(pitchSemitones);
    audio.dataset.pitchShiftSemitones = String(pitchSemitones);
    audio.dataset.pitchShiftActive = graph && pitchSemitones !== 0 ? "true" : "false";
  }, [disposePitchGraph, ensurePitchGraph, nativeAudioOutput, pitchSemitones, rate]);

  useEffect(() => {
    clipEndedHandled.current = false;
  }, [clip?.end, clip?.start, streamUrl]);

  useEffect(() => {
    const audio = audioRef.current as PitchAwareAudio | null;
    if (!audio) {
      return;
    }

    const resumeKey = `${streamUrl}:${resumeTime ?? "none"}:${clip?.start ?? "none"}:${clip?.end ?? "none"}`;
    const handleLoadedMetadata = () => {
      const nextDuration = Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : duration;
      const clipStart = clip?.start ?? 0;
      const clipEnd = clip?.end != null ? Math.max(clipStart, clip.end) : nextDuration;
      const nextTime = clip
        ? clamp(resumeTime ?? clipStart, clipStart, Math.max(clipEnd - 0.25, clipStart))
        : resumeTime;
      audio.defaultPlaybackRate = rate;
      audio.playbackRate = rate;
      setPreservePitch(audio, true);
      if (nativeAudioOutput) {
        disposePitchGraph();
        audio.dataset.pitchShiftSemitones = String(pitchSemitones);
        audio.dataset.pitchShiftActive = "false";
      } else {
        const graph = pitchSemitones !== 0 ? ensurePitchGraph() : pitchGraphRef.current;
        graph?.shifter.setPitch(pitchSemitones);
        audio.dataset.pitchShiftSemitones = String(pitchSemitones);
        audio.dataset.pitchShiftActive = graph && pitchSemitones !== 0 ? "true" : "false";
      }
      setMeasuredDuration(nextDuration);
      lastLoadedSourceRef.current = `${streamUrl}|${format || "audio"}`;
      if (nextTime != null && nextTime >= 0 && resumeAppliedRef.current !== resumeKey) {
        audio.currentTime = clamp(nextTime, 0, Math.max(nextDuration - 0.25, 0));
        setCurrentTime(roundTime(audio.currentTime));
        lastSeenTime.current = roundTime(audio.currentTime);
        resumeAppliedRef.current = resumeKey;
      }
      if (pendingAutostartRef.current) {
        audio.play().catch(() => {});
      }
    };

    audio.addEventListener("loadedmetadata", handleLoadedMetadata);
    return () => {
      audio.removeEventListener("loadedmetadata", handleLoadedMetadata);
    };
  }, [clip?.end, clip?.start, disposePitchGraph, duration, ensurePitchGraph, format, nativeAudioOutput, pitchSemitones, rate, resumeTime, streamUrl]);

  useEffect(() => {
    if (!autostart) {
      pendingAutostartRef.current = false;
      return;
    }

    pendingAutostartRef.current = true;
    const audio = audioRef.current;
    const sourceSignature = `${streamUrl}|${format || "audio"}`;
    if (!audio || lastLoadedSourceRef.current !== sourceSignature) {
      return;
    }

    audio.play().catch(() => {});
  }, [autostart, autostartToken, format, streamUrl]);

  useEffect(() => {
    const audio = audioRef.current as PitchAwareAudio | null;
    if (!audio) {
      return;
    }

    setRemotePlaybackAvailable(typeof audio.webkitShowPlaybackTargetPicker === "function");
    const onTargetChanged = () => {
      const savedTime = audio.currentTime;
      window.setTimeout(() => {
        if (audio.currentTime < savedTime - 1) {
          audio.currentTime = savedTime;
        }
      }, 500);
    };

    audio.addEventListener("webkitcurrentplaybacktargetchanged" as never, onTargetChanged as EventListener);
    return () => {
      audio.removeEventListener("webkitcurrentplaybacktargetchanged" as never, onTargetChanged as EventListener);
    };
  }, []);

  const togglePlayback = useCallback(() => {
    const audio = audioRef.current;
    if (!audio) {
      return;
    }

    if (audio.paused) {
      audio.play().catch(() => {});
      return;
    }

    pendingAutostartRef.current = false;
    audio.pause();
    setPlaying(false);
    flushInterval("paused");
    intervalStart.current = null;
  }, [flushInterval]);

  const seekBy = useCallback((delta: number) => {
    const audio = audioRef.current;
    if (!audio) {
      return;
    }

    audio.currentTime = clamp(audio.currentTime + delta, 0, Number.isFinite(audio.duration) ? audio.duration : Math.max(measuredDuration, 0));
  }, [measuredDuration]);

  const setPlaybackTime = useCallback((nextTime: number) => {
    const audio = audioRef.current;
    if (!audio) {
      return;
    }

    const clampedTime = clamp(nextTime, 0, Number.isFinite(audio.duration) ? audio.duration : Math.max(measuredDuration, 0));
    audio.currentTime = clampedTime;
    if (clip?.end == null || clampedTime < clip.end) {
      clipEndedHandled.current = false;
    }
  }, [clip?.end, measuredDuration]);

  useEffect(() => {
    onSeekRegister?.((time: number) => {
      setPlaybackTime(time);
      audioRef.current?.play().catch(() => {});
    });
  }, [onSeekRegister, setPlaybackTime]);

  const commitVolume = useCallback((nextVolume: number, nextMuted = false) => {
    const normalized = clamp(nextVolume, 0, 1);
    setVolume(normalized);
    setMuted(nextMuted);
    window.localStorage.setItem(VOLUME_KEY, String(normalized));
    window.localStorage.setItem(MUTED_KEY, nextMuted ? "true" : "false");
  }, []);

  const commitRate = useCallback((nextRate: number) => {
    const normalized = roundRate(clamp(nextRate, 0.25, 3));
    setRate(normalized);
    window.localStorage.setItem(RATE_KEY, String(normalized));
  }, []);

  const commitPitch = useCallback((nextPitch: number) => {
    const normalized = clamp(Math.round(nextPitch), -12, 12);
    setPitchSemitones(normalized);
    window.localStorage.setItem(PITCH_KEY, String(normalized));
  }, []);

  const showRemotePlaybackPicker = useCallback(() => {
    const audio = audioRef.current as PitchAwareAudio | null;
    audio?.webkitShowPlaybackTargetPicker?.();
  }, []);

  const effectiveDuration = Number.isFinite(measuredDuration) && measuredDuration > 0 ? measuredDuration : Math.max(duration, currentTime, 0);
  const clipStart = clip?.start ?? 0;
  const clipEnd = clip?.end != null ? Math.max(clipStart, clip.end) : effectiveDuration;
  const seekProgress = effectiveDuration > 0 ? Math.min(100, (currentTime / effectiveDuration) * 100) : 0;
  const bufferedProgress = effectiveDuration > 0 ? Math.min(100, (buffered / effectiveDuration) * 100) : 0;
  const currentVolume = muted ? 0 : volume;
  const skipSeconds = playbackPreferences.skipSeconds;

  return (
    <div className="mt-auto w-full overflow-visible border-y border-border bg-surface text-foreground shadow-sm sm:rounded-lg sm:border">
      <audio
        ref={audioRef}
        preload="metadata"
        src={streamUrl}
        playsInline
        {...({ "x-webkit-airplay": "allow" } as Record<string, string>)}
        onPlay={() => {
          const audio = audioRef.current;
          const graph = pitchGraphRef.current ?? (pitchSemitones !== 0 && !nativeAudioOutput ? ensurePitchGraph() : null);
          if (graph?.context.state === "suspended") {
            void graph.context.resume().catch(() => {});
          }
          pendingAutostartRef.current = false;
          setPlaying(true);
          onPlaybackStateChange?.(true);
          onPlay?.();
          startTrackedInterval(roundTime(audio?.currentTime ?? currentTime));
        }}
        onPause={() => {
          setPlaying(false);
          onPlaybackStateChange?.(false);
          onPause?.();
          flushInterval("paused");
          intervalStart.current = null;
          trackAudioInteraction("pause", { positionSec: lastSeenTime.current });
        }}
        onSeeking={() => {
          if (intervalStart.current != null) {
            flushInterval("active");
            intervalStart.current = null;
          }
          trackAudioInteraction("seek", {
            fromSec: lastSeenTime.current,
            toSec: roundTime(audioRef.current?.currentTime ?? currentTime),
          });
        }}
        onSeeked={() => {
          const audio = audioRef.current;
          if (audio && !audio.paused) {
            startTrackedInterval(roundTime(audio.currentTime));
          }
        }}
        onTimeUpdate={() => {
          const audio = audioRef.current;
          const time = roundTime(audio?.currentTime ?? 0);
          setCurrentTime(time);
          // Don't accrue watch time while the tab is backgrounded (see VideoPlayer for rationale).
          if (document.hidden) return;
          const now = Date.now();
          if (trackingEnabled && intervalStart.current != null) {
            const wallDt = lastTickAt.current > 0 ? (now - lastTickAt.current) / 1000 : 0;
            const rate = audio?.playbackRate ?? 1;
            const maxAdvance = Math.max(1, wallDt * rate + 1);
            const advance = time - lastSeenTime.current;
            if (advance >= -0.5 && advance <= maxAdvance) {
              // Contiguous playback → extend the watched interval.
              lastSeenTime.current = time;
              if (now - lastKeepaliveSentAt.current >= 10000) {
                lastKeepaliveSentAt.current = now;
                flushInterval("active");
                intervalStart.current = time;
              }
            } else {
              // Discontinuity (a seek): close at the real last-watched position, reopen at the new one.
              flushInterval("active");
              startTrackedInterval(time);
            }
          } else {
            lastSeenTime.current = time;
          }
          lastTickAt.current = now;
          if (audio && clip?.end != null && time >= clipEnd && !clipEndedHandled.current) {
            clipEndedHandled.current = true;
            audio.pause();
            audio.currentTime = clipEnd;
            setCurrentTime(roundTime(clipEnd));
            lastSeenTime.current = roundTime(clipEnd);
            flushInterval("ended");
            intervalStart.current = null;
            setPlaying(false);
            onEnded?.();
          }
        }}
        onProgress={() => {
          const audio = audioRef.current;
          if (audio && audio.buffered.length > 0) {
            setBuffered(audio.buffered.end(audio.buffered.length - 1));
          }
        }}
        onEnded={() => {
          setPlaying(false);
          flushInterval("ended");
          intervalStart.current = null;
          onEnded?.();
        }}
      />

      <div className="flex flex-col gap-3 p-3 sm:p-4">
        <div className="flex flex-col gap-3 xl:grid xl:grid-cols-[minmax(220px,1fr)_auto_minmax(220px,1fr)] xl:items-center">
          <div className="flex min-w-0 items-center gap-3">
            <div className="flex h-14 w-14 shrink-0 items-center justify-center rounded-md border border-border bg-card shadow-sm">
              {coverUrl ? (
                <img src={coverUrl} alt="" className="h-full w-full rounded-md object-cover" />
              ) : (
                <Headphones className="h-7 w-7 text-accent" />
              )}
            </div>
            <div className="min-w-0">
              <div className="flex min-w-0 items-center gap-2 text-[11px] uppercase text-secondary">
                <span className="truncate">{format || "audio"}</span>
                {hasVideoTrack ? <MonitorPlay className="h-3.5 w-3.5 shrink-0 text-accent" /> : null}
              </div>
              <h2 className="mt-0.5 truncate text-base font-semibold leading-snug text-foreground sm:text-lg">{title}</h2>
              {subtitle ? <p className="truncate text-xs text-secondary">{subtitle}</p> : null}
            </div>
          </div>

          <div className="flex items-center justify-center gap-2">
            <button
              type="button"
              onClick={() => seekBy(-skipSeconds)}
              className="inline-flex h-9 w-9 items-center justify-center rounded-full border border-border bg-background/80 text-foreground transition hover:border-accent hover:text-accent"
              title={`Back ${skipSeconds} seconds`}
              aria-label={`Back ${skipSeconds} seconds`}
            >
              <SkipBack className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={togglePlayback}
              className="inline-flex h-11 w-11 items-center justify-center rounded-full bg-accent text-white shadow-sm transition hover:scale-[1.02] hover:bg-accent-hover"
              title={playing ? "Pause" : "Play"}
              aria-label={playing ? "Pause" : "Play"}
            >
              {playing ? <Pause className="h-5 w-5" /> : <Play className="h-5 w-5 translate-x-[1px]" />}
            </button>
            <button
              type="button"
              onClick={() => seekBy(skipSeconds)}
              className="inline-flex h-9 w-9 items-center justify-center rounded-full border border-border bg-background/80 text-foreground transition hover:border-accent hover:text-accent"
              title={`Forward ${skipSeconds} seconds`}
              aria-label={`Forward ${skipSeconds} seconds`}
            >
              <SkipForward className="h-4 w-4" />
            </button>
          </div>

          <div className="flex items-center justify-center gap-2 xl:justify-end">
            <ControlFlyout
              label="Volume"
              value={`${Math.round(currentVolume * 100)}%`}
              icon={currentVolume === 0 ? <VolumeX className="h-4 w-4" /> : <Volume2 className="h-4 w-4" />}
            >
              <input
                type="range"
                min={0}
                max={1}
                step={0.01}
                value={currentVolume}
                onChange={(event) => commitVolume(Number(event.currentTarget.value), false)}
                className="w-full accent-accent"
                aria-label="Volume"
              />
              <div className="mt-2 flex items-center justify-between text-[11px] text-secondary">
                <button type="button" onClick={() => commitVolume(volume, !muted)} className="text-accent transition hover:text-accent-hover">
                  {muted || volume === 0 ? "Unmute" : "Mute"}
                </button>
                <span>100%</span>
              </div>
            </ControlFlyout>

            <ControlFlyout label="Speed" value={`${rate.toFixed(1)}x`} icon={<Gauge className="h-4 w-4" />}>
              <input
                type="range"
                min={0}
                max={28}
                step={1}
                value={rateToSliderValue(rate)}
                onChange={(event) => commitRate(sliderValueToRate(Number(event.currentTarget.value)))}
                className="w-full accent-accent"
                aria-label="Playback speed"
                aria-valuetext={`${rate.toFixed(1)}x`}
              />
              <div className="mt-2 flex items-center justify-between text-[11px] text-secondary">
                <span>0.25x</span>
                <button type="button" onClick={() => commitRate(1)} className="text-accent transition hover:text-accent-hover">1.0x</button>
                <span>3.0x</span>
              </div>
            </ControlFlyout>

            {!nativeAudioOutput ? (
              <ControlFlyout label="Pitch" value={formatPitch(pitchSemitones)} icon={<SlidersHorizontal className="h-4 w-4" />}>
                <input
                  type="range"
                  min={-12}
                  max={12}
                  step={1}
                  value={pitchSemitones}
                  onChange={(event) => commitPitch(Number(event.currentTarget.value))}
                  className="w-full accent-accent"
                  aria-label="Pitch adjustment"
                />
                <div className="mt-2 flex items-center justify-between text-[11px] text-secondary">
                  <span>-12 st</span>
                  <button type="button" onClick={() => commitPitch(0)} className="text-accent transition hover:text-accent-hover">0 st</button>
                  <span>+12 st</span>
                </div>
              </ControlFlyout>
            ) : null}

            {remotePlaybackAvailable ? (
              <button
                type="button"
                onClick={showRemotePlaybackPicker}
                className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-border bg-background/80 text-secondary transition hover:border-accent hover:text-accent"
                title="Choose playback device"
                aria-label="Choose playback device"
              >
                <MonitorPlay className="h-4 w-4" />
              </button>
            ) : null}
          </div>
        </div>

        <div className="grid grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-3 text-xs text-secondary">
          <span className="tabular-nums">{formatTime(currentTime)}</span>
          <input
            type="range"
            min={0}
            max={Math.max(effectiveDuration, 0.001)}
            step={0.1}
            value={Math.min(currentTime, effectiveDuration)}
            onChange={(event) => setPlaybackTime(Number(event.currentTarget.value))}
            className="h-3 w-full cursor-pointer accent-accent"
            style={{
              background: `linear-gradient(to right, var(--color-accent, #3b82f6) 0%, var(--color-accent, #3b82f6) ${seekProgress}%, rgba(255,255,255,0.22) ${seekProgress}%, rgba(255,255,255,0.22) ${bufferedProgress}%, rgba(255,255,255,0.08) ${bufferedProgress}%, rgba(255,255,255,0.08) 100%)`,
            }}
            aria-label="Seek audio"
          />
          <span className="tabular-nums">{formatTime(effectiveDuration)}</span>
        </div>
      </div>
    </div>
  );
}
