import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from "react";
import {
  Eye,
  EyeOff,
  Maximize,
  Minimize,
  Pause,
  PictureInPicture2,
  Play,
  Repeat,
  Repeat1,
  SkipBack,
  SkipForward,
  Subtitles,
  Volume2,
  VolumeX,
} from "lucide-react";
import { videos } from "../api/client";
import type { Detection, Face, Segment } from "../api/types";
import { createPlaybackTracker, trackInteraction, type PlaybackTrackingTarget } from "../utils/interactionTracking";
import { useAppConfig } from "../state/AppConfigContext";

type FaceOverlayInfo = Pick<Face, "id" | "label" | "performerName" | "performerId">;
type DetectionOverlay = Detection & { overlayKey?: string };

function generateUuid() {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }

  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (character) => {
    const random = Math.random() * 16 | 0;
    const value = character === "x" ? random : (random & 0x3) | 0x8;
    return value.toString(16);
  });
}

const VOLUME_KEY = "cove-video-player-volume";
const MUTED_KEY = "cove-video-player-muted";
const FACE_OVERLAY_KEY = "cove.player.faceOverlay";
const PLAYBACK_RATES = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2] as const;

function getVideoSourceMimeType(format?: string) {
  switch (format?.trim().toLowerCase()) {
    case "mp4":
      return "video/mp4";
    case "webm":
      return "video/webm";
    case "ogg":
    case "ogv":
      return "video/ogg";
    case "mpeg":
    case "mpg":
      return "video/mpeg";
    case "mov":
      return "video/quicktime";
    default:
      return undefined;
  }
}

// Sentinel quality meaning "transcode at the source resolution" — used as a fallback when no
// smaller transcode-ladder entries are available (e.g. a sub-360p source).
const SOURCE_TRANSCODE_QUALITY = "Source";

// Audio codecs that browsers generally cannot decode in a direct <video> playback. Files with
// these codecs play with video but NO audio in Direct mode, and because playback does not error
// the onError->transcode fallback never fires — so we proactively avoid defaulting to Direct.
const INCOMPATIBLE_AUDIO_CODECS = new Set(["ac3", "eac3", "ec-3", "dts", "dts-hd", "truehd", "mlp"]);

function isBrowserCompatibleAudio(codec?: string) {
  const normalized = codec?.trim().toLowerCase();
  if (!normalized) {
    // Unknown codec — assume compatible and let the existing onError fallback handle real failures.
    return true;
  }

  return !INCOMPATIBLE_AUDIO_CODECS.has(normalized);
}

function usePersistedFlag(key: string, defaultValue: boolean): [boolean, (next: boolean | ((prev: boolean) => boolean)) => void] {
  const [value, setValue] = useState<boolean>(() => {
    if (typeof window === "undefined") return defaultValue;
    try {
      const raw = window.localStorage.getItem(key);
      if (raw === "true") return true;
      if (raw === "false") return false;
    } catch {
      // Ignore storage access failures.
    }
    return defaultValue;
  });

  const setPersistedValue = useCallback((next: boolean | ((prev: boolean) => boolean)) => {
    setValue((previous) => {
      const resolved = typeof next === "function" ? (next as (prev: boolean) => boolean)(previous) : next;
      try {
        window.localStorage.setItem(key, resolved ? "true" : "false");
      } catch {
        // Ignore storage access failures.
      }
      return resolved;
    });
  }, [key]);

  return [value, setPersistedValue];
}

function roundPlaybackTime(value: number) {
  return Math.round(value * 1000) / 1000;
}

function getConfiguredPlaybackStartTime(duration: number, startPercent: number, minDuration: number) {
  if (!Number.isFinite(duration) || duration <= 0 || startPercent <= 0 || duration < minDuration) {
    return undefined;
  }

  return roundPlaybackTime(duration * Math.min(95, Math.max(0, startPercent)) / 100);
}

export function VideoPlayer({
  streamUrl,
  posterUrl,
  format,
  audioCodec,
  duration,
  resumeTime,
  videoId,
  detections = [],
  segments = [],
  faces = [],
  captions,
  onPlay,
  onSeekRegister,
  onTimeUpdate: onTimeUpdateProp,
  autostart,
  autostartToken,
  showAbLoop,
  trackingEnabled = true,
  playbackTracking,
  onEnded: onEndedProp,
  clip,
  videoStyle,
  onPrev,
  onNext,
}: {
  streamUrl: string;
  posterUrl?: string;
  format: string;
  audioCodec?: string;
  duration: number;
  resumeTime?: number;
  videoId: number;
  detections?: Detection[];
  segments?: Segment[];
  faces?: FaceOverlayInfo[];
  captions?: { id: number; languageCode: string; captionType: string; filename: string }[];
  onPlay?: () => void;
  onSeekRegister?: (fn: (time: number) => void) => void;
  onTimeUpdate?: (time: number) => void;
  autostart?: boolean;
  autostartToken?: number;
  showAbLoop?: boolean;
  trackingEnabled?: boolean;
  playbackTracking?: PlaybackTrackingTarget;
  onEnded?: () => void;
  clip?: { start: number; end?: number | null; loop?: boolean };
  videoStyle?: CSSProperties;
  onPrev?: () => void;
  onNext?: () => void;
}) {
  const { config } = useAppConfig();
  const maxLoopDuration = config?.ui.maxLoopDuration ?? 0;
  const playerVideoStartPercent = config?.ui.playerVideoStartPercent ?? 0;
  const playerVideoStartMinDuration = config?.ui.playerVideoStartMinDuration ?? 0;
  const effectiveShowAbLoop = showAbLoop ?? config?.ui.showAbLoopControls ?? true;
  const effectiveResumeTime = config?.ui.alwaysResumeOnPlayback === false ? undefined : resumeTime;
  const videoRef = useRef<HTMLVideoElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const volumeTrackRef = useRef<HTMLDivElement>(null);
  const [playing, setPlaying] = useState(false);
  const [currentTime, setCurTime] = useState(0);
  const [buffered, setBuffered] = useState(0);
  const [vol, setVol] = useState(() => {
    const saved = localStorage.getItem(VOLUME_KEY);
    return saved ? Number(saved) : 1;
  });
  const [muted, setMuted] = useState(() => localStorage.getItem(MUTED_KEY) === "true");
  const [fullscreen, setFullscreen] = useState(false);
  const [showControls, setShowControls] = useState(true);
  const [showCursor, setShowCursor] = useState(true);
  const [isBuffering, setIsBuffering] = useState(false);
  const [showSpeed, setShowSpeed] = useState(false);
  const [rate, setRate] = useState(1);
  const [pip, setPip] = useState(false);
  const [loop, setLoop] = useState(false);
  const [abLoop, setAbLoop] = useState<{ a: number | null; b: number | null }>({ a: null, b: null });
  const [showCaptions, setShowCaptions] = useState(false);
  const [showQuality, setShowQuality] = useState(false);
  const [selectedQuality, setSelectedQuality] = useState<string>("Direct");
  const [transcodeStartSec, setTranscodeStartSec] = useState(0);
  const [availableQualities, setAvailableQualities] = useState<string[]>([]);
  // Set when we avoid Direct play because the audio codec is browser-incompatible, so we can surface
  // a subtle note explaining why a transcoded stream was chosen. Reset per video.
  const [audioFallbackActive, setAudioFallbackActive] = useState(false);
  // Guards the one-shot automatic transcode fallback (on direct-play error). Reset per video.
  const autoTranscodeTriedRef = useRef(false);
  // Guards the one-shot incompatible-audio default selection so a user can still pick Direct later.
  const audioFallbackAppliedRef = useRef(false);
  const [faceOverlayEnabled, setFaceOverlayEnabled] = usePersistedFlag(FACE_OVERLAY_KEY, false);
  const playbackTracker = useRef(createPlaybackTracker());
  const hideTimer = useRef<ReturnType<typeof setTimeout>>(null);
  const playTriggered = useRef(false);
  const sourceRestoreRef = useRef<{ time: number; shouldPlay: boolean } | null>(null);
  const lastLoadedSourceRef = useRef<string | null>(null);
  const pendingAutostartRef = useRef(false);
  // Bounded in-place recovery from transient network stalls (MEDIA_ERR_NETWORK/ABORTED). Tracks how
  // many reloads we've attempted since playback last succeeded plus the pending backoff timer, so a
  // dead source can't spin us in a tight reload loop. Reset to 0 whenever playback resumes.
  const networkRecoveryRef = useRef<{ attempts: number; timer: ReturnType<typeof setTimeout> | null }>({ attempts: 0, timer: null });
  // Resume-seek bookkeeping: which source we last applied the resume/initial seek for, and whether a real
  // resume target has been applied for it. Lets us ignore later resumeTime changes for the SAME source (the
  // engagement cache being rewritten when you rate/favorite mid-playback) so they can't yank playback.
  const resumeSourceKeyRef = useRef<string | null>(null);
  const resumeSettledRef = useRef(false);
  const intervalStart = useRef<number | null>(null);
  const lastSeenTime = useRef<number>(0);
  const lastKeepaliveSentAt = useRef<number>(0);
  // Wall-clock of the last timeupdate tick, used to tell contiguous playback from a seek.
  const lastTickAt = useRef<number>(0);
  const journalFlushed = useRef(false);
  const lastHideInteractionAt = useRef(0);
  const clipEndedHandled = useRef(false);
  const [videoBox, setVideoBox] = useState({ left: 0, top: 0, width: 0, height: 0 });
  const clipStart = clip?.start ?? 0;
  const clipEnd = Math.max(clipStart, clip?.end ?? duration);
  const timelineStart = clip ? clipStart : 0;
  const timelineDuration = clip ? Math.max(clipEnd - clipStart, 0.001) : Math.max(duration, 0.001);
  const visibleCurrentTime = clip ? Math.max(0, currentTime - clipStart) : currentTime;
  const visibleBuffered = clip ? Math.max(0, Math.min(buffered, clipEnd) - clipStart) : buffered;
  const defaultPlaybackStartTime = useMemo(
    () => getConfiguredPlaybackStartTime(duration, playerVideoStartPercent, playerVideoStartMinDuration),
    [duration, playerVideoStartMinDuration, playerVideoStartPercent],
  );
  const playbackTrackingTarget = useMemo<PlaybackTrackingTarget | null>(() => {
    if (!trackingEnabled) {
      return null;
    }

    const baseTarget = playbackTracking ?? { hostType: "video", hostId: videoId, scopeKey: `video:${videoId}`, surface: "detail" };
    return {
      ...baseTarget,
      clipStartSec: clip?.start ?? baseTarget.clipStartSec,
      clipEndSec: clip?.end ?? baseTarget.clipEndSec,
      autoplay: autostart ?? baseTarget.autoplay,
      muted,
      fullscreen,
      playbackRate: rate,
      route: typeof window === "undefined" ? baseTarget.route : baseTarget.route ?? `${window.location.pathname}${window.location.search}${window.location.hash}`,
    };
  }, [autostart, clip?.end, clip?.start, fullscreen, muted, playbackTracking, rate, videoId, trackingEnabled]);
  const playbackTrackingSignature = useMemo(() => JSON.stringify(playbackTrackingTarget), [playbackTrackingTarget]);

  useEffect(() => {
    intervalStart.current = null;
    lastSeenTime.current = 0;
    lastKeepaliveSentAt.current = 0;
    lastHideInteractionAt.current = 0;
    playTriggered.current = false;
    pendingAutostartRef.current = false;
    autoTranscodeTriedRef.current = false;
    audioFallbackAppliedRef.current = false;
    if (networkRecoveryRef.current.timer) clearTimeout(networkRecoveryRef.current.timer);
    networkRecoveryRef.current = { attempts: 0, timer: null };
    setAudioFallbackActive(false);
    setSelectedQuality("Direct");
    setTranscodeStartSec(0);
  }, [videoId]);

  useEffect(() => {
    void playbackTracker.current.setTarget(playbackTrackingTarget);
  }, [playbackTrackingSignature]);

  const trackPlayerInteraction = useCallback((kind: "pause" | "seek" | "fullscreen", meta: Record<string, unknown> = {}) => {
    if (!playbackTrackingTarget) {
      return;
    }

    trackInteraction({
      hostType: playbackTrackingTarget.hostType as never,
      hostId: playbackTrackingTarget.hostId,
      kind,
      meta: {
        surface: playbackTrackingTarget.surface,
        scopeKey: playbackTrackingTarget.scopeKey,
        groupItemId: playbackTrackingTarget.groupItemId,
        parentHostType: playbackTrackingTarget.parentHostType,
        parentHostId: playbackTrackingTarget.parentHostId,
        itemHostType: playbackTrackingTarget.itemHostType,
        itemHostId: playbackTrackingTarget.itemHostId,
        segmentId: playbackTrackingTarget.segmentId,
        clipStartSec: playbackTrackingTarget.clipStartSec,
        clipEndSec: playbackTrackingTarget.clipEndSec,
        playbackRate: rate,
        muted,
        fullscreen,
        ...meta,
      },
    });
  }, [fullscreen, muted, playbackTrackingTarget, rate]);

  useEffect(() => {
    clipEndedHandled.current = false;
    if (clip) {
      setLoop(!!clip.loop);
    }
  }, [clip?.end, clip?.loop, clip?.start, videoId, streamUrl]);

  useEffect(() => {
    const v = videoRef.current;
    if (!v) return;
    v.volume = vol;
    v.muted = muted;
  }, []);

  const toAbsoluteTime = useCallback((mediaTime: number) => (
    selectedQuality === "Direct" ? mediaTime : transcodeStartSec + mediaTime
  ), [selectedQuality, transcodeStartSec]);

  const seekToAbsoluteTime = useCallback((targetTime: number, forcePlay = false) => {
    const video = videoRef.current;
    // A seek must not fold the skipped span into watched time: flush the open interval up to the real
    // last-watched position and close it. onSeeked re-opens a fresh interval at the destination. (We can't
    // call flushInterval here — it's declared below — so record directly.)
    if (playbackTrackingTarget && intervalStart.current !== null) {
      const s = intervalStart.current;
      const e = roundPlaybackTime(lastSeenTime.current);
      if (e > s)
        playbackTracker.current.recordInterval({ startSec: s, endSec: e, mediaDurationSec: duration || video?.duration || 0, currentPositionSec: e, state: "active", mode: "default" });
      intervalStart.current = null;
    }
    const maxTarget = Number.isFinite(duration) && duration > 0 ? duration : targetTime;
    const target = Math.min(Math.max(0, targetTime), Math.max(0, maxTarget));
    const rounded = roundPlaybackTime(target);

    if (selectedQuality === "Direct") {
      if (video) {
        video.currentTime = target;
        if (forcePlay) video.play().catch(() => {});
      }
      setCurTime(rounded);
      onTimeUpdateProp?.(rounded);
      lastSeenTime.current = rounded;
      return;
    }

    const shouldPlay = forcePlay || Boolean(video && !video.paused);
    sourceRestoreRef.current = { time: target, shouldPlay };
    setCurTime(rounded);
    onTimeUpdateProp?.(rounded);
    lastSeenTime.current = rounded;
    setTranscodeStartSec(target);
  }, [duration, onTimeUpdateProp, selectedQuality, playbackTrackingTarget]);

  useEffect(() => {
    if (onSeekRegister) {
      onSeekRegister((time: number) => {
        seekToAbsoluteTime(time, true);
      });
    }
  }, [onSeekRegister, seekToAbsoluteTime]);

  const updateVideoBox = useCallback(() => {
    const video = videoRef.current;
    const container = containerRef.current;
    if (!video || !container) {
      return;
    }

    const intrinsicWidth = video.videoWidth || video.clientWidth;
    const intrinsicHeight = video.videoHeight || video.clientHeight;
    const containerWidth = container.clientWidth;
    const containerHeight = container.clientHeight;

    if (!intrinsicWidth || !intrinsicHeight || !containerWidth || !containerHeight) {
      return;
    }

    const scale = Math.min(containerWidth / intrinsicWidth, containerHeight / intrinsicHeight);
    const width = intrinsicWidth * scale;
    const height = intrinsicHeight * scale;
    const left = (containerWidth - width) / 2;
    const top = (containerHeight - height) / 2;

    setVideoBox((current) => {
      if (
        Math.abs(current.left - left) < 0.5
        && Math.abs(current.top - top) < 0.5
        && Math.abs(current.width - width) < 0.5
        && Math.abs(current.height - height) < 0.5
      ) {
        return current;
      }

      return { left, top, width, height };
    });
  }, []);

  useEffect(() => {
    const container = containerRef.current;
    const video = videoRef.current;
    if (!container || !video) {
      return;
    }

    updateVideoBox();
    const resizeObserver = new ResizeObserver(() => updateVideoBox());
    resizeObserver.observe(container);
    resizeObserver.observe(video);
    window.addEventListener("resize", updateVideoBox);
    return () => {
      resizeObserver.disconnect();
      window.removeEventListener("resize", updateVideoBox);
    };
  }, [videoId, selectedQuality, streamUrl, updateVideoBox]);

  const faceLabelsById = useMemo(() => {
    const labels = new Map<number, FaceOverlayInfo>();
    for (const face of faces) {
      labels.set(face.id, face);
    }

    return labels;
  }, [faces]);

  const activeDetections = useMemo<DetectionOverlay[]>(() => {
    const faceSegments = segments.filter(isFaceTimelineSegment);
    if (!detections.length && (!faceOverlayEnabled || !faceSegments.some(hasSegmentFaceKeyframes))) {
      return [];
    }

    const toleranceSec = 0.5;
    const byKey = new Map<string, DetectionOverlay>();
    const faceDetections: Detection[] = [];

    for (const detection of detections) {
      if (isLinkedFaceDetection(detection)) {
        faceDetections.push(detection);
        continue;
      }

      if (isFaceDetection(detection)) {
        continue;
      }

      const observedAt = detection.observedAtSec;
      if (observedAt != null && Math.abs(observedAt - currentTime) > toleranceSec) {
        continue;
      }

      const key = detection.groupKey
        ?? `${detection.refKind ?? detection.class}:${detection.refId ?? detection.id}:${detection.class}`;
      const existing = byKey.get(key);
      if (!existing) {
        byKey.set(key, detection);
        continue;
      }

      const existingDelta = Math.abs((existing.observedAtSec ?? currentTime) - currentTime);
      const candidateDelta = Math.abs((detection.observedAtSec ?? currentTime) - currentTime);
      if (candidateDelta < existingDelta) {
        byKey.set(key, detection);
      }
    }

    if (faceOverlayEnabled && (faceDetections.length > 0 || faceSegments.some(hasSegmentFaceKeyframes))) {
      const faceGroups = groupFaceDetections(faceDetections);
      const consumedGroups = new Set<string>();

      for (const segment of faceSegments) {
        if (!isFaceTimelineSegment(segment) || !isTimeWithinSegment(currentTime, segment, toleranceSec)) {
          continue;
        }

        const trackKey = getSegmentTrackKey(segment);
        let segmentCandidates = trackKey && faceGroups.has(trackKey)
          ? faceGroups.get(trackKey) ?? []
          : faceDetections.filter((detection) => detection.refId != null
              && segment.refId != null
              && detection.refId === segment.refId
              && isDetectionWithinSegment(detection, segment, toleranceSec));

        if (segmentCandidates.length === 0) {
          segmentCandidates = getSegmentFaceKeyframes(segment);
        }

        if (segmentCandidates.length === 0) {
          continue;
        }

        const overlay = interpolateDetection(segmentCandidates, currentTime);
        const key = getFaceOverlayKey(overlay, trackKey);
        const candidate = { ...overlay, overlayKey: key };
        const existing = byKey.get(key);
        byKey.set(key, existing ? chooseCurrentFaceOverlay(existing, candidate, currentTime) : candidate);
        if (trackKey) {
          consumedGroups.add(trackKey);
        }
      }

      for (const [groupKey, group] of faceGroups) {
        if (consumedGroups.has(groupKey) || group.length === 0) {
          continue;
        }

        const timed = group.filter((detection) => detection.observedAtSec != null);
        if (timed.length === 0) {
          const fallback = group[0];
          const key = getFaceOverlayKey(fallback, groupKey);
          const candidate = { ...fallback, overlayKey: key };
          const existing = byKey.get(key);
          byKey.set(key, existing ? chooseCurrentFaceOverlay(existing, candidate, currentTime) : candidate);
          continue;
        }

        const start = Math.min(...timed.map((detection) => detection.observedAtSec!));
        const end = Math.max(...timed.map((detection) => detection.observedAtSec!));
        const singleInstantWindow = timed.length === 1 ? toleranceSec : 0;
        if (currentTime < start - toleranceSec || currentTime > end + Math.max(toleranceSec, singleInstantWindow)) {
          continue;
        }

        const overlay = interpolateDetection(group, currentTime);
        const key = getFaceOverlayKey(overlay, groupKey);
        const candidate = { ...overlay, overlayKey: key };
        const existing = byKey.get(key);
        byKey.set(key, existing ? chooseCurrentFaceOverlay(existing, candidate, currentTime) : candidate);
      }
    }

    return Array.from(byKey.values());
  }, [currentTime, detections, faceOverlayEnabled, segments]);

  const hasFaceDetections = useMemo(
    () => detections.some(isLinkedFaceDetection) || segments.some(hasSegmentFaceKeyframes),
    [detections, segments],
  );

  // The source sentinel transcodes at the original resolution (no `resolution` query param).
  const transcodeResolution = selectedQuality === SOURCE_TRANSCODE_QUALITY ? undefined : selectedQuality;
  const effectiveStreamUrl = selectedQuality === "Direct" ? streamUrl : videos.transcodeUrl(videoId, transcodeResolution, transcodeStartSec > 0 ? transcodeStartSec : undefined);
  const effectiveSourceType = selectedQuality === "Direct" ? getVideoSourceMimeType(format) : "video/mp4";

  useEffect(() => {
    const v = videoRef.current;

    // A change in this key is a legitimate moment to (re)apply the resume/initial seek: a new video, a quality
    // switch, a new stream, or a clip change. Apply on a new source, or the FIRST time a resume target arrives
    // for the current source (engagement loads async). A later resumeTime change for the SAME source — e.g.
    // the engagement cache being rewritten when you rate/favorite mid-playback — must NOT re-seek.
    const sourceKey = `${videoId}|${selectedQuality}|${streamUrl}|${clip?.start ?? ""}|${clip?.end ?? ""}|${clip?.loop ?? ""}`;
    const isNewSource = sourceKey !== resumeSourceKeyRef.current;
    const shouldSeek = isNewSource || (!resumeSettledRef.current && effectiveResumeTime != null);
    if (isNewSource) {
      resumeSourceKeyRef.current = sourceKey;
      resumeSettledRef.current = effectiveResumeTime != null;
    } else if (shouldSeek) {
      resumeSettledRef.current = true;
    }

    if (shouldSeek) {
      const nextTime = clip
        ? Math.min(Math.max(effectiveResumeTime ?? clip.start, clip.start), clip.end ?? duration)
        : effectiveResumeTime ?? defaultPlaybackStartTime;
      if (v && nextTime != null) {
        if (selectedQuality === "Direct") {
          v.currentTime = nextTime;
        } else {
          setTranscodeStartSec(nextTime);
        }
        setCurTime(roundPlaybackTime(nextTime));
      }
    }

    if (clip?.loop && clip.end != null) {
      setAbLoop({ a: clip.start, b: clip.end });
    } else if (clip) {
      setAbLoop({ a: null, b: null });
    }
  }, [clip?.end, clip?.loop, clip?.start, defaultPlaybackStartTime, duration, effectiveResumeTime, videoId, selectedQuality, streamUrl]);

  useEffect(() => {
    if (!autostart) {
      return;
    }

    pendingAutostartRef.current = true;
    const video = videoRef.current;
    const sourceSignature = `${effectiveStreamUrl}|${format || "mp4"}`;
    if (!video || lastLoadedSourceRef.current !== sourceSignature) {
      return;
    }

    video.play().catch(() => {});
  }, [autostart, autostartToken, effectiveStreamUrl, format]);

  useEffect(() => {
    const handler = () => setPip(document.pictureInPictureElement === videoRef.current);
    document.addEventListener("enterpictureinpicture", handler);
    document.addEventListener("leavepictureinpicture", handler);
    return () => {
      document.removeEventListener("enterpictureinpicture", handler);
      document.removeEventListener("leavepictureinpicture", handler);
    };
  }, []);

  useEffect(() => {
    const v = videoRef.current as (HTMLVideoElement & { webkitShowPlaybackTargetPicker?: () => void }) | null;
    if (!v) return;
    const onTargetChanged = () => {
      const savedTime = v.currentTime;
      setTimeout(() => {
        if (v.currentTime < savedTime - 1) v.currentTime = savedTime;
      }, 500);
    };
    v.addEventListener("webkitcurrentplaybacktargetchanged" as never, onTargetChanged as EventListener);
    return () => v.removeEventListener("webkitcurrentplaybacktargetchanged" as never, onTargetChanged as EventListener);
  }, []);

  useEffect(() => {
    if (abLoop.a == null || abLoop.b == null) return;
    const v = videoRef.current;
    if (!v) return;
    const handler = () => {
      if (toAbsoluteTime(v.currentTime) >= abLoop.b!) {
        seekToAbsoluteTime(abLoop.a!, false);
      }
    };
    v.addEventListener("timeupdate", handler);
    return () => v.removeEventListener("timeupdate", handler);
  }, [abLoop, seekToAbsoluteTime, toAbsoluteTime]);

  useEffect(() => {
    if (journalFlushed.current) {
      return;
    }

    journalFlushed.current = true;
    window.localStorage.removeItem("cove-video-activity-journal");
  }, []);

  const flushInterval = useCallback((state: string, mode: "default" | "keepalive" = "default") => {
    const video = videoRef.current;
    if (!playbackTrackingTarget || !video || intervalStart.current === null) return;
    const startSec = intervalStart.current;
    const endSec = roundPlaybackTime(lastSeenTime.current);
    if (endSec <= startSec) return;
    playbackTracker.current.recordInterval({
      startSec,
      endSec,
      mediaDurationSec: duration || video.duration || 0,
      currentPositionSec: endSec,
      state,
      mode,
    });
  }, [duration, playbackTrackingTarget]);

  const flushIntervalKeepalive = useCallback((state: string) => {
    flushInterval(state, "keepalive");
  }, [flushInterval]);

  const startTrackedInterval = useCallback((time: number) => {
    intervalStart.current = time;
    lastSeenTime.current = time;
    lastKeepaliveSentAt.current = Date.now();
    lastTickAt.current = Date.now();
  }, []);

  useEffect(() => {
    if (!clip) {
      return;
    }

    const video = videoRef.current;
    if (!video) {
      return;
    }

    const handleClipBoundary = () => {
      const absoluteTime = toAbsoluteTime(video.currentTime);
      if (absoluteTime < clipStart) {
        seekToAbsoluteTime(clipStart, false);
        setCurTime(roundPlaybackTime(clipStart));
        return;
      }

      if (absoluteTime < clipEnd - 0.05) {
        clipEndedHandled.current = false;
        return;
      }

      if (loop) {
        if (intervalStart.current !== null) {
          flushInterval("active");
        }
        seekToAbsoluteTime(clipStart, false);
        setCurTime(roundPlaybackTime(clipStart));
        lastSeenTime.current = roundPlaybackTime(clipStart);
        if (intervalStart.current !== null) {
          startTrackedInterval(clipStart);
        }
        return;
      }

      if (clipEndedHandled.current) {
        return;
      }

      clipEndedHandled.current = true;
      video.pause();
      seekToAbsoluteTime(clipEnd, false);
      lastSeenTime.current = roundPlaybackTime(clipEnd);
      setCurTime(roundPlaybackTime(clipEnd));
      flushInterval("ended");
      intervalStart.current = null;
      setPlaying(false);
      onEndedProp?.();
    };

    video.addEventListener("timeupdate", handleClipBoundary);
    return () => {
      video.removeEventListener("timeupdate", handleClipBoundary);
    };
  }, [clip, clipEnd, clipStart, flushInterval, loop, onEndedProp, seekToAbsoluteTime, startTrackedInterval, toAbsoluteTime]);

  useEffect(() => {
    if (!playbackTrackingTarget) {
      return;
    }

    // Flush the OPEN interval AND any already-queued intervals (e.g. one a pause put on the 5s batch
    // timer) via keepalive, so a refresh/close/navigation never drops the last watched span.
    const flushAllKeepalive = () => {
      flushIntervalKeepalive("paused");
      void playbackTracker.current.flush("paused", "keepalive");
    };

    const handleVisibilityChange = () => {
      if (document.visibilityState === "hidden") {
        // Flush what was watched up to now, then CLOSE the interval so the hidden span isn't
        // bridged back in when the tab returns to the foreground.
        flushAllKeepalive();
        intervalStart.current = null;
      } else if (document.visibilityState === "visible") {
        // Reopen a fresh interval from the current position if playback is still running.
        const video = videoRef.current;
        if (video && !video.paused) {
          startTrackedInterval(roundPlaybackTime(toAbsoluteTime(video.currentTime)));
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
  }, [flushIntervalKeepalive, playbackTrackingTarget, startTrackedInterval, toAbsoluteTime]);

  useEffect(() => {
    const handler = () => {
      const isFullscreen = !!document.fullscreenElement;
      setFullscreen(isFullscreen);
      // Leaving fullscreen always restores the cursor (windowed mode never hides it).
      if (!isFullscreen) setShowCursor(true);
    };
    document.addEventListener("fullscreenchange", handler);
    return () => document.removeEventListener("fullscreenchange", handler);
  }, []);

  const resetHideTimer = useCallback(() => {
    setShowControls(true);
    setShowCursor(true);
    if (hideTimer.current) clearTimeout(hideTimer.current);
    hideTimer.current = setTimeout(() => {
      const video = videoRef.current;
      if (video && !video.paused) {
        setShowControls(false);
        // Only hide the cursor in fullscreen; windowed mode keeps the pointer visible.
        if (document.fullscreenElement) setShowCursor(false);
      }
    }, 3000);
  }, []);

  useEffect(() => {
    const v = videoRef.current;
    if (!v) return;
    for (let i = 0; i < v.textTracks.length; i++) {
      v.textTracks[i].mode = showCaptions ? "showing" : "hidden";
    }
  }, [showCaptions]);

  useEffect(() => {
    videos.getResolutions(videoId).then((res) => {
      const resolutions = res ?? [];
      setAvailableQualities(resolutions);

      // If the source audio codec can't be decoded by the browser, Direct play would yield video
      // with no audio (and no error to trigger the onError fallback). Proactively default to the
      // best available transcode instead. Only applied once per video so the user can still
      // explicitly choose Direct afterward.
      if (!audioFallbackAppliedRef.current && !isBrowserCompatibleAudio(audioCodec)) {
        audioFallbackAppliedRef.current = true;
        const target = resolutions.length > 0
          ? resolutions[resolutions.length - 1]
          : SOURCE_TRANSCODE_QUALITY;
        setSelectedQuality((prev) => (prev === "Direct" ? target : prev));
        setAudioFallbackActive(true);
      }
    }).catch(() => {});
  }, [audioCodec, videoId]);

  const prepareClipForPlayback = useCallback(() => {
    const video = videoRef.current;
    const currentPosition = roundPlaybackTime(video ? toAbsoluteTime(video.currentTime) : currentTime);

    if (!video || !clip || loop) {
      return currentPosition;
    }

    if (clipEndedHandled.current || currentPosition >= clipEnd - 0.05 || currentPosition < clipStart) {
      const startPosition = roundPlaybackTime(clipStart);
      clipEndedHandled.current = false;
      seekToAbsoluteTime(clipStart, false);
      lastSeenTime.current = startPosition;
      setCurTime(startPosition);
      return startPosition;
    }

    return currentPosition;
  }, [clip, clipEnd, clipStart, currentTime, loop, seekToAbsoluteTime, toAbsoluteTime]);

  const playVideo = useCallback(() => {
    const video = videoRef.current;
    if (!video) return;
    prepareClipForPlayback();
    video.play().catch(() => {});
  }, [prepareClipForPlayback]);

  useEffect(() => {
    const handler = (event: KeyboardEvent) => {
      const v = videoRef.current;
      if (!v) return;
      const tag = (event.target as HTMLElement).tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;

      switch (event.key) {
        case " ":
        case "k":
          event.preventDefault();
          v.paused ? playVideo() : v.pause();
          break;
        case "ArrowLeft":
          event.preventDefault();
          seekToAbsoluteTime(currentTime - (event.shiftKey ? 10 : 5));
          break;
        case "ArrowRight":
          event.preventDefault();
          seekToAbsoluteTime(currentTime + (event.shiftKey ? 10 : 5));
          break;
        case "ArrowUp":
          event.preventDefault();
          v.volume = Math.min(1, v.volume + 0.1);
          setVol(v.volume);
          localStorage.setItem(VOLUME_KEY, String(v.volume));
          break;
        case "ArrowDown":
          event.preventDefault();
          v.volume = Math.max(0, v.volume - 0.1);
          setVol(v.volume);
          localStorage.setItem(VOLUME_KEY, String(v.volume));
          break;
        case "m":
          v.muted = !v.muted;
          setMuted(v.muted);
          localStorage.setItem(MUTED_KEY, String(v.muted));
          break;
        case "f":
          toggleFullscreen();
          break;
        case "0": case "1": case "2": case "3": case "4":
        case "5": case "6": case "7": case "8": case "9":
          event.preventDefault();
          seekToAbsoluteTime(timelineStart + timelineDuration * (Number(event.key) / 10));
          break;
      }
      resetHideTimer();
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [currentTime, playVideo, resetHideTimer, seekToAbsoluteTime, timelineDuration, timelineStart]);

  const togglePlay = () => {
    const v = videoRef.current;
    if (!v) return;
    v.paused ? playVideo() : v.pause();
  };

  const seekTo = (event: React.MouseEvent<HTMLDivElement>) => {
    const v = videoRef.current;
    if (!v) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const pct = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
    seekToAbsoluteTime(timelineStart + pct * timelineDuration);
  };

  const setVolumeFromClientX = useCallback((clientX: number) => {
    const v = videoRef.current;
    const track = volumeTrackRef.current;
    if (!v || !track) return;
    const rect = track.getBoundingClientRect();
    const pct = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    v.volume = pct;
    v.muted = false;
    setVol(pct);
    setMuted(false);
    localStorage.setItem(VOLUME_KEY, String(pct));
    localStorage.setItem(MUTED_KEY, "false");
  }, []);

  const changeVolume = (event: React.MouseEvent<HTMLDivElement>) => {
    setVolumeFromClientX(event.clientX);
  };

  const startVolumeDrag = (event: React.PointerEvent<HTMLDivElement>) => {
    event.preventDefault();
    setVolumeFromClientX(event.clientX);

    const handlePointerMove = (moveEvent: PointerEvent) => setVolumeFromClientX(moveEvent.clientX);
    const handlePointerUp = () => {
      window.removeEventListener("pointermove", handlePointerMove);
      window.removeEventListener("pointerup", handlePointerUp);
      window.removeEventListener("pointercancel", handlePointerUp);
    };

    window.addEventListener("pointermove", handlePointerMove);
    window.addEventListener("pointerup", handlePointerUp, { once: true });
    window.addEventListener("pointercancel", handlePointerUp, { once: true });
  };

  const toggleFullscreen = () => {
    const nextFullscreen = !document.fullscreenElement;
    trackPlayerInteraction("fullscreen", { active: nextFullscreen, positionSec: currentTime });
    if (document.fullscreenElement) document.exitFullscreen();
    else containerRef.current?.requestFullscreen();
  };

  const changeRate = (nextRate: number) => {
    const v = videoRef.current;
    if (v) v.playbackRate = nextRate;
    setRate(nextRate);
    setShowSpeed(false);
  };

  const changeQuality = (quality: string) => {
    const v = videoRef.current;
    const curTime = v ? toAbsoluteTime(v.currentTime) : currentTime;
    const wasPlaying = v ? !v.paused : false;
    sourceRestoreRef.current = { time: curTime, shouldPlay: wasPlaying };
    if (quality === "Direct") {
      setTranscodeStartSec(0);
      // User explicitly chose Direct — dismiss the incompatible-audio note.
      setAudioFallbackActive(false);
    } else {
      setTranscodeStartSec(curTime);
    }
    setSelectedQuality(quality);
    setShowQuality(false);
  };

  // Fall back to server-side transcoding when the source can't be played directly in the
  // browser — a non-native container (avi, wmv, …) or a native container with an unsupported
  // codec that fails to load. Triggered from the <video> onError handler. Picks the highest
  // available ladder rung, or a source-resolution transcode when no ladder entries exist.
  const fallbackToTranscode = () => {
    if (autoTranscodeTriedRef.current) return;
    autoTranscodeTriedRef.current = true;
    const target = availableQualities.length > 0
      ? availableQualities[availableQualities.length - 1]
      : SOURCE_TRANSCODE_QUALITY;
    changeQuality(target);
  };

  // Recover in place from a transient network stall (MEDIA_ERR_NETWORK / MEDIA_ERR_ABORTED). A hard
  // network error leaves the <video> element in an error state that will not resume on its own, so we
  // must reload — but we reload the SAME source and seek back to where we were, showing the buffering
  // spinner meanwhile, instead of falling back to a transcode (which reloads from 0 and is what made
  // the player jump to the beginning). Bounded + backed-off so a genuinely dead source stops retrying.
  const recoverFromNetworkStall = () => {
    const video = videoRef.current;
    if (!video) return;
    if (networkRecoveryRef.current.attempts >= 3) return;
    networkRecoveryRef.current.attempts += 1;
    setIsBuffering(true);

    const resumeAt = video.currentTime > 0.01
      ? (selectedQuality === "Direct" ? video.currentTime : transcodeStartSec + video.currentTime)
      : undefined;
    const wasPlaying = !video.paused;

    const onMeta = () => {
      if (resumeAt != null && Number.isFinite(resumeAt)) {
        video.currentTime = selectedQuality === "Direct" ? resumeAt : Math.max(0, resumeAt - transcodeStartSec);
      }
      if (wasPlaying) video.play().catch(() => {});
    };
    video.addEventListener("loadedmetadata", onMeta, { once: true });

    if (networkRecoveryRef.current.timer) clearTimeout(networkRecoveryRef.current.timer);
    // Short, increasing backoff so a briefly-flapping connection isn't hammered.
    networkRecoveryRef.current.timer = setTimeout(() => {
      const v = videoRef.current;
      if (v) v.load();
    }, 500 * networkRecoveryRef.current.attempts);
  };

  useEffect(() => {
    const video = videoRef.current;
    if (!video) {
      return;
    }

    const sourceSignature = `${effectiveStreamUrl}|${format || "mp4"}`;
    if (lastLoadedSourceRef.current === sourceSignature) {
      return;
    }
    lastLoadedSourceRef.current = sourceSignature;

    const pendingRestore = sourceRestoreRef.current;
    sourceRestoreRef.current = null;
    const shouldAutoplayAfterLoad = pendingRestore?.shouldPlay || pendingAutostartRef.current;

    // Capture the current absolute playback position BEFORE video.load() resets the element. If a
    // source reload happens mid-playback (e.g. a transient src refresh) with no explicit restore /
    // clip / resume target, we restore to this position instead of letting the element seek to 0.
    // Only treat it as a real position when the video had already started playing.
    const positionBeforeLoad = video.currentTime > 0.01
      ? (selectedQuality === "Direct" ? video.currentTime : transcodeStartSec + video.currentTime)
      : undefined;

    const handleLoadedMetadata = () => {
      const mediaDuration = selectedQuality === "Direct" && Number.isFinite(video.duration) && video.duration > 0 ? video.duration : duration;
      const configuredStartTime = getConfiguredPlaybackStartTime(mediaDuration, playerVideoStartPercent, playerVideoStartMinDuration);
      const targetTime = pendingRestore?.time
        ?? (clip ? clip.start : effectiveResumeTime ?? configuredStartTime)
        ?? positionBeforeLoad;
      if (targetTime != null && Number.isFinite(targetTime)) {
        video.currentTime = selectedQuality === "Direct" ? targetTime : Math.max(0, targetTime - transcodeStartSec);
        setCurTime(roundPlaybackTime(targetTime));
      }

      if (shouldAutoplayAfterLoad) {
        pendingAutostartRef.current = false;
        video.play().catch(() => {});
      }
    };

    video.addEventListener("loadedmetadata", handleLoadedMetadata, { once: true });
    video.load();
    return () => {
      video.removeEventListener("loadedmetadata", handleLoadedMetadata);
    };
  }, [clip, duration, effectiveResumeTime, effectiveStreamUrl, format, playerVideoStartMinDuration, playerVideoStartPercent, selectedQuality, transcodeStartSec]);

  // Release the media element's network connection when the player unmounts. Without this, leaving a
  // video (back to the list, or advancing to the next item in a queue when the player is keyed by id)
  // leaves the browser holding the open stream/transcode connection until GC. That pins a server-side
  // transcode slot and, after several videos, can exhaust the browser's per-host connection pool so
  // subsequent requests — including the next video's metadata fetch — stall, leaving a blank page.
  useEffect(() => {
    const video = videoRef.current;
    return () => {
      if (networkRecoveryRef.current.timer) clearTimeout(networkRecoveryRef.current.timer);
      if (!video) return;
      try {
        video.pause();
        // Clear the <source> URLs and reload so the element aborts any in-flight download instead of
        // re-selecting the same source. Removing the attribute (not the node) avoids fighting React's
        // own unmount removal.
        video.querySelectorAll("source").forEach((s) => s.removeAttribute("src"));
        video.removeAttribute("src");
        video.load();
      } catch {
        // Ignore — element may already be detached.
      }
    };
  }, []);

  const togglePip = async () => {
    const v = videoRef.current;
    if (!v) return;
    try {
      if (document.pictureInPictureElement) {
        await document.exitPictureInPicture();
      } else {
        await v.requestPictureInPicture();
      }
    } catch {
      // PiP not supported or denied.
    }
  };

  const cycleAbLoop = () => {
    const v = videoRef.current;
    if (!v) return;
    if (abLoop.a == null) {
      setAbLoop({ a: currentTime, b: null });
    } else if (abLoop.b == null) {
      const rawEnd = currentTime;
      const cappedEnd = maxLoopDuration > 0 && rawEnd > abLoop.a
        ? Math.min(rawEnd, abLoop.a + maxLoopDuration)
        : rawEnd;
      setAbLoop({ a: abLoop.a, b: cappedEnd });
    } else {
      setAbLoop({ a: null, b: null });
    }
  };

  const fmtTime = (value: number) => {
    if (!isFinite(value)) return "0:00";
    const h = Math.floor(value / 3600);
    const m = Math.floor((value % 3600) / 60);
    const sec = Math.floor(value % 60);
    return h > 0 ? `${h}:${m.toString().padStart(2, "0")}:${sec.toString().padStart(2, "0")}` : `${m}:${sec.toString().padStart(2, "0")}`;
  };

  return (
    <div
      ref={containerRef}
      className="relative group w-full h-full flex items-center justify-center bg-black"
      style={{ cursor: showCursor ? undefined : "none" }}
      onMouseMove={resetHideTimer}
      onMouseLeave={() => playing && setShowControls(false)}
    >
      <video
        ref={videoRef}
        className="w-full h-full object-contain cursor-pointer"
        style={showCursor ? videoStyle : { ...videoStyle, cursor: "none" }}
        preload="metadata"
        poster={posterUrl}
        {...({ "x-webkit-airplay": "allow" } as Record<string, string>)}
        onLoadedMetadata={updateVideoBox}
        onLoadedData={updateVideoBox}
        onError={(e) => {
          const code = e.currentTarget.error?.code;
          // Only a genuine container/codec failure (DECODE / SRC_NOT_SUPPORTED) warrants swapping to a
          // server transcode. MEDIA_ERR_NETWORK (2) / MEDIA_ERR_ABORTED (1) are transient buffering
          // stalls — recover in place at the same position rather than reloading from 0.
          if (selectedQuality === "Direct" && (code === MediaError.MEDIA_ERR_DECODE || code === MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED)) {
            fallbackToTranscode();
          } else if (code === MediaError.MEDIA_ERR_NETWORK || code === MediaError.MEDIA_ERR_ABORTED) {
            recoverFromNetworkStall();
          }
        }}
        onClick={togglePlay}
        onDoubleClick={toggleFullscreen}
        onWaiting={() => setIsBuffering(true)}
        onStalled={() => setIsBuffering(true)}
        onCanPlay={() => setIsBuffering(false)}
        onPlaying={() => {
          setIsBuffering(false);
          // Playback is healthy again — clear the transient-stall retry budget.
          networkRecoveryRef.current.attempts = 0;
        }}
        onPlay={() => {
          setPlaying(true);
          pendingAutostartRef.current = false;
          const currentPos = prepareClipForPlayback();
          startTrackedInterval(currentPos);
          if (!playTriggered.current) { playTriggered.current = true; onPlay?.(); }
        }}
        onPause={() => {
          setPlaying(false);
          flushInterval("paused");
          intervalStart.current = null;
          trackPlayerInteraction("pause", { positionSec: lastSeenTime.current });
        }}
        onSeeking={() => {
          if (intervalStart.current !== null) {
            flushInterval("active");
            intervalStart.current = null;
          }
          const video = videoRef.current;
          trackPlayerInteraction("seek", {
            fromSec: lastSeenTime.current,
            toSec: video ? roundPlaybackTime(toAbsoluteTime(video.currentTime)) : undefined,
          });
        }}
        onSeeked={() => {
          setIsBuffering(false);
          const video = videoRef.current;
          if (video && !video.paused) {
            const time = roundPlaybackTime(toAbsoluteTime(video.currentTime));
            startTrackedInterval(time);
          }
        }}
        onTimeUpdate={() => {
          const v = videoRef.current;
          const time = roundPlaybackTime(v ? toAbsoluteTime(v.currentTime) : 0);
          setCurTime(time);
          onTimeUpdateProp?.(time);
          // Don't accrue watch time while the tab is backgrounded: a <video> can keep playing and
          // firing timeupdate in a hidden tab, and counting that pollutes engagement/watch data.
          if (document.hidden) return;
          const now = Date.now();
          if (trackingEnabled && intervalStart.current !== null) {
            const wallDt = lastTickAt.current > 0 ? (now - lastTickAt.current) / 1000 : 0;
            const rate = v?.playbackRate ?? 1;
            // Max contiguous media-time advance since the last tick (+ tolerance for jitter/buffering).
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
              // Discontinuity (a seek): close the interval at the real last-watched position and reopen at
              // the new one, so the skipped span is never counted as watched.
              flushInterval("active");
              startTrackedInterval(time);
            }
          } else {
            lastSeenTime.current = time;
          }
          lastTickAt.current = now;
        }}
        onProgress={() => {
          const v = videoRef.current;
          if (v && v.buffered.length > 0) setBuffered(Math.min(duration, toAbsoluteTime(v.buffered.end(v.buffered.length - 1))));
        }}
        onEnded={() => {
          if (loop) {
            flushInterval("active");
            intervalStart.current = null;
            seekToAbsoluteTime(timelineStart, true);
            return;
          }
          setPlaying(false);
          flushInterval("ended");
          intervalStart.current = null;
          onEndedProp?.();
        }}
      >
        <source src={effectiveStreamUrl} type={effectiveSourceType} />
        {captions?.map((cap, idx) => (
          <track
            key={cap.id}
            kind="captions"
            src={videos.captionUrl(videoId, cap.id)}
            srcLang={cap.languageCode === "00" ? "en" : cap.languageCode}
            label={cap.languageCode === "00" ? cap.filename : cap.languageCode.toUpperCase()}
            default={idx === 0 && showCaptions}
          />
        ))}
      </video>

      {activeDetections.length > 0 && videoBox.width > 0 && videoBox.height > 0 ? (
        <div className="pointer-events-none absolute inset-0 z-[2]">
          {activeDetections.map((detection) => {
            const left = videoBox.left + (detection.x / Math.max(detection.frameWidth, 1)) * videoBox.width;
            const top = videoBox.top + (detection.y / Math.max(detection.frameHeight, 1)) * videoBox.height;
            const width = (detection.w / Math.max(detection.frameWidth, 1)) * videoBox.width;
            const height = (detection.h / Math.max(detection.frameHeight, 1)) * videoBox.height;
            const color = detectionColor(detection.class);

            return (
              <div
                key={detection.overlayKey ?? detection.id}
                className="absolute rounded-md border shadow-[0_0_0_1px_rgba(0,0,0,0.25)]"
                style={{
                  left,
                  top,
                  width,
                  height,
                  borderColor: color,
                  boxShadow: `0 0 0 1px ${color}55 inset`,
                  background: `${color}14`,
                }}
              >
                <span
                  className="absolute left-0 top-0 -translate-y-full rounded-sm px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wide text-white"
                  style={{ backgroundColor: color }}
                >
                  {formatDetectionBadge(detection, faceLabelsById)}
                </span>
              </div>
            );
          })}
        </div>
      ) : null}

      <div
        className={`absolute bottom-0 left-0 right-0 bg-gradient-to-t from-black/90 via-black/50 to-transparent transition-opacity ${
          showControls ? "opacity-100" : "opacity-0 pointer-events-none"
        }`}
        style={{ padding: "40px 0 0 0" }}
      >
        <div className="px-3">
          <div className="relative h-4 flex items-center cursor-pointer group/seek" onClick={seekTo}>
            <div className="w-full h-1 bg-white/20 rounded-full group-hover/seek:h-1.5 transition-all relative">
              <div className="absolute top-0 left-0 h-full bg-white/30 rounded-full" style={{ width: `${(visibleBuffered / timelineDuration) * 100}%` }} />
              <div className="absolute top-0 left-0 h-full bg-accent rounded-full" style={{ width: `${(visibleCurrentTime / timelineDuration) * 100}%` }} />
              {abLoop.a != null && (
                <div
                  className="absolute top-0 h-full bg-accent/25 pointer-events-none"
                  style={{
                    left: `${((abLoop.a - timelineStart) / timelineDuration) * 100}%`,
                    width: abLoop.b != null ? `${((abLoop.b - abLoop.a) / timelineDuration) * 100}%` : "2px",
                  }}
                />
              )}
            </div>
            <div
              className="absolute top-1/2 -translate-y-1/2 w-3 h-3 bg-accent rounded-full opacity-0 group-hover/seek:opacity-100 transition-opacity"
              style={{ left: `${(visibleCurrentTime / timelineDuration) * 100}%`, transform: "translate(-50%, -50%)" }}
            />
          </div>
        </div>

        <div className="flex items-center gap-2 px-3 py-2 text-white">
          {onPrev && (
            <button onClick={onPrev} className="hover:text-accent p-1" title="Previous video">
              <SkipBack className="w-4 h-4 fill-current" />
            </button>
          )}

          <button onClick={togglePlay} className="hover:text-accent p-1">
            {playing ? <Pause className="w-5 h-5" /> : <Play className="w-5 h-5" />}
          </button>

          {onNext && (
            <button onClick={onNext} className="hover:text-accent p-1" title="Next video">
              <SkipForward className="w-4 h-4 fill-current" />
            </button>
          )}

          <button onClick={() => seekToAbsoluteTime(currentTime - 10)} className="hover:text-accent p-1" title="Back 10s">
            <SkipBack className="w-4 h-4" />
          </button>
          <button onClick={() => seekToAbsoluteTime(currentTime + 10)} className="hover:text-accent p-1" title="Forward 10s">
            <SkipForward className="w-4 h-4" />
          </button>

          <button onClick={() => {
            const v = videoRef.current;
            if (!v) return;
            v.muted = !v.muted;
            setMuted(v.muted);
            localStorage.setItem(MUTED_KEY, String(v.muted));
          }} className="hover:text-accent p-1">
            {muted || vol === 0 ? <VolumeX className="w-4 h-4" /> : <Volume2 className="w-4 h-4" />}
          </button>
          <div ref={volumeTrackRef} className="w-20 h-3 flex items-center cursor-pointer group/vol touch-none" onClick={changeVolume} onPointerDown={startVolumeDrag}>
            <div className="w-full h-1 bg-white/20 rounded-full relative">
              <div className="absolute top-0 left-0 h-full bg-white rounded-full" style={{ width: `${(muted ? 0 : vol) * 100}%` }} />
            </div>
          </div>

          <span className="text-xs text-white/70 ml-1 select-none tabular-nums">
            {fmtTime(visibleCurrentTime)} / {fmtTime(clip ? clipEnd - clipStart : duration)}
          </span>

          <div className="ml-auto flex items-center gap-2">
            <div className="relative">
              <button
                onClick={() => setShowSpeed(!showSpeed)}
                className={`hover:text-accent p-1 text-xs font-medium flex items-center gap-1 ${rate !== 1 ? "text-accent" : ""}`}
              >
                {rate}x
              </button>
              {showSpeed && (
                <div className="absolute bottom-full right-0 mb-2 bg-surface border border-border rounded shadow-lg py-1 z-10">
                  {PLAYBACK_RATES.map((playbackRate) => (
                    <button
                      key={playbackRate}
                      onClick={() => changeRate(playbackRate)}
                      className={`block w-full text-left px-4 py-1 text-sm hover:bg-card ${playbackRate === rate ? "text-accent" : "text-white"}`}
                    >
                      {playbackRate}x
                    </button>
                  ))}
                </div>
              )}
            </div>

            {effectiveShowAbLoop && (
              <button
                onClick={cycleAbLoop}
                className={`hover:text-accent p-1 text-xs font-medium flex items-center gap-1 ${abLoop.a != null ? "text-accent" : ""}`}
                title={abLoop.a == null ? "Set loop start (A)" : abLoop.b == null ? "Set loop end (B)" : "Clear A-B loop"}
              >
                <Repeat className="w-4 h-4" />
                {abLoop.a != null && abLoop.b == null && "A"}
                {abLoop.a != null && abLoop.b != null && "A-B"}
              </button>
            )}

            {availableQualities.length > 0 && (
              <div className="relative">
                <button
                  onClick={() => setShowQuality(!showQuality)}
                  className={`hover:text-accent p-1 text-xs font-medium ${selectedQuality !== "Direct" ? "text-accent" : ""}`}
                  title="Video quality"
                >
                  {selectedQuality === "Direct" ? "Direct" : selectedQuality}
                </button>
                {showQuality && (
                  <div className="absolute bottom-full right-0 mb-2 bg-surface border border-border rounded shadow-lg py-1 z-10">
                    <button
                      onClick={() => changeQuality("Direct")}
                      className={`block w-full text-left px-4 py-1 text-sm hover:bg-card ${selectedQuality === "Direct" ? "text-accent" : "text-white"}`}
                    >
                      Direct
                    </button>
                    {availableQualities.map((quality) => (
                      <button
                        key={quality}
                        onClick={() => changeQuality(quality)}
                        className={`block w-full text-left px-4 py-1 text-sm hover:bg-card ${quality === selectedQuality ? "text-accent" : "text-white"}`}
                      >
                        {quality}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            )}

            <button
              onClick={() => setLoop(!loop)}
              className={`hover:text-accent p-1 ${loop ? "text-accent" : ""}`}
              title={loop ? "Disable loop" : "Loop video"}
            >
              <Repeat1 className="w-4 h-4" />
            </button>

            <button onClick={togglePip} className={`hover:text-accent p-1 ${pip ? "text-accent" : ""}`} title="Picture-in-Picture">
              <PictureInPicture2 className="w-4 h-4" />
            </button>

            {captions && captions.length > 0 && (
              <button
                onClick={() => setShowCaptions((prev) => !prev)}
                className={`hover:text-accent p-1 ${showCaptions ? "text-accent" : ""}`}
                title={showCaptions ? "Hide captions" : "Show captions"}
              >
                <Subtitles className="w-4 h-4" />
              </button>
            )}

            {hasFaceDetections ? (
              <button
                onClick={() => setFaceOverlayEnabled((previous) => !previous)}
                className={`hover:text-accent p-1 ${faceOverlayEnabled ? "text-accent" : ""}`}
                title="X-ray"
                aria-label="X-ray"
                aria-pressed={faceOverlayEnabled}
              >
                {faceOverlayEnabled ? <Eye className="w-4 h-4" /> : <EyeOff className="w-4 h-4" />}
              </button>
            ) : null}

            <button onClick={toggleFullscreen} className="hover:text-accent p-1">
              {fullscreen ? <Minimize className="w-4 h-4" /> : <Maximize className="w-4 h-4" />}
            </button>
          </div>
        </div>
      </div>

      {isBuffering && (
        <div className="absolute inset-0 flex items-center justify-center pointer-events-none z-[3]">
          <div className="h-12 w-12 rounded-full border-4 border-white/30 border-t-white animate-spin" />
        </div>
      )}

      {audioFallbackActive && (
        <div className="absolute top-2 left-1/2 -translate-x-1/2 z-[3] pointer-events-none rounded bg-black/70 px-3 py-1 text-xs text-white/90">
          Direct play unavailable: unsupported audio codec — using transcoded stream
        </div>
      )}

      {!playing && !isBuffering && (
        <div className="absolute inset-0 flex items-center justify-center pointer-events-none">
          <div className="bg-black/40 rounded-full p-4">
            <Play className="w-12 h-12 text-white" />
          </div>
        </div>
      )}
    </div>
  );
}

function detectionColor(className: string) {
  const normalized = className.trim().toLowerCase();
  if (normalized === "face") return "#22c55e";
  if (normalized === "person" || normalized === "body") return "#38bdf8";
  if (normalized === "hand") return "#f59e0b";
  if (normalized === "text") return "#a855f7";

  let hash = 0;
  for (let index = 0; index < normalized.length; index += 1) {
    hash = ((hash << 5) - hash) + normalized.charCodeAt(index);
    hash |= 0;
  }

  const hue = Math.abs(hash) % 360;
  return `hsl(${hue} 80% 55%)`;
}

function isFaceDetection(detection: Detection) {
  return (detection.refKind ?? detection.class ?? "").toLowerCase() === "face";
}

function isLinkedFaceDetection(detection: Detection) {
  return isFaceDetection(detection) && detection.refKind?.toLowerCase() === "face" && detection.refId != null;
}

function getPayloadValue(payload: unknown, key: string): unknown {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    return undefined;
  }

  return (payload as Record<string, unknown>)[key];
}

function getPayloadString(payload: unknown, key: string): string | undefined {
  const value = getPayloadValue(payload, key);
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function getPayloadJsonValue(payload: unknown, key: string): unknown {
  const value = getPayloadValue(payload, key);
  if (typeof value !== "string") {
    return value;
  }

  try {
    return JSON.parse(value);
  } catch {
    return undefined;
  }
}

function readFiniteNumber(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }

  return undefined;
}

function readNumberArray(value: unknown): number[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map(readFiniteNumber)
    .filter((item): item is number => item != null);
}

function getSegmentKeyframeItems(segment: Segment): unknown[] {
  const keyframes = getPayloadJsonValue(segment.payload, "keyframes");
  return Array.isArray(keyframes) ? keyframes : [];
}

function hasSegmentFaceKeyframes(segment: Segment) {
  if (!isFaceTimelineSegment(segment)) {
    return false;
  }

  return getSegmentKeyframeItems(segment).length > 0
    || readNumberArray(getPayloadJsonValue(segment.payload, "bestBbox")).length >= 4;
}

function getSegmentFaceKeyframes(segment: Segment): Detection[] {
  const keyframes = getSegmentKeyframeItems(segment);
  const detections = keyframes
    .map((keyframe, index) => createSegmentKeyframeDetection(segment, keyframe, index))
    .filter((detection): detection is Detection => detection != null);

  if (detections.length > 0) {
    return detections;
  }

  const bestBbox = readNumberArray(getPayloadJsonValue(segment.payload, "bestBbox"));
  if (bestBbox.length < 4) {
    return [];
  }

  return [createSegmentFaceDetection(
    segment,
    0,
    readFiniteNumber(getPayloadValue(segment.payload, "bestTimeSec")) ?? segment.startSec,
    bestBbox,
    readFiniteNumber(getPayloadValue(segment.payload, "bestScore")) ?? segment.confidence ?? 1,
  )];
}

function createSegmentKeyframeDetection(segment: Segment, keyframe: unknown, index: number): Detection | null {
  if (!keyframe || typeof keyframe !== "object" || Array.isArray(keyframe)) {
    return null;
  }

  const record = keyframe as Record<string, unknown>;
  const bbox = readNumberArray(record.bbox);
  if (bbox.length < 4) {
    return null;
  }

  return createSegmentFaceDetection(
    segment,
    index,
    readFiniteNumber(record.t) ?? readFiniteNumber(record.timeSec) ?? readFiniteNumber(record.time) ?? segment.startSec,
    bbox,
    readFiniteNumber(record.score) ?? segment.confidence ?? 1,
  );
}

function createSegmentFaceDetection(segment: Segment, index: number, observedAtSec: number, bbox: number[], score: number): Detection {
  const x = bbox[0];
  const y = bbox[1];
  const width = bbox[2] > x ? bbox[2] - x : bbox[2];
  const height = bbox[3] > y ? bbox[3] - y : bbox[3];
  const trackKey = getSegmentTrackKey(segment) ?? `segment:${segment.id}`;

  return {
    id: -(segment.id * 1000 + index + 1),
    hostType: "video",
    hostId: segment.hostId,
    observedAtSec,
    frameWidth: 1,
    frameHeight: 1,
    class: "face",
    score,
    x,
    y,
    w: Math.max(width, 0),
    h: Math.max(height, 0),
    extra: segment.payload,
    refKind: "face",
    refId: segment.refId,
    groupKey: trackKey,
    sourceKey: segment.sourceKey,
    sourceRunId: segment.sourceRunId,
    createdAt: segment.createdAt,
    updatedAt: segment.updatedAt,
  };
}

function isFaceTimelineSegment(segment: Segment) {
  return (segment.kind ?? "").toLowerCase() === "face"
    || getPayloadString(segment.payload, "refKind")?.toLowerCase() === "face";
}

function getSegmentTrackKey(segment: Segment) {
  return getPayloadString(segment.payload, "trackKey") || undefined;
}

function isTimeWithinSegment(currentTime: number, segment: Segment, toleranceSec: number) {
  const start = segment.startSec;
  const end = Math.max(segment.endSec ?? segment.startSec, segment.startSec + 0.4);
  return currentTime >= start - toleranceSec && currentTime <= end + toleranceSec;
}

function isDetectionWithinSegment(detection: Detection, segment: Segment, toleranceSec: number) {
  if (detection.observedAtSec == null) {
    return false;
  }

  const start = segment.startSec;
  const end = Math.max(segment.endSec ?? segment.startSec, segment.startSec + 0.4);
  return detection.observedAtSec >= start - toleranceSec && detection.observedAtSec <= end + toleranceSec;
}

function getFaceDetectionGroupKey(detection: Detection) {
  return detection.groupKey
    ?? (detection.refId != null ? `face:${detection.refId}` : `detection:${detection.id}`);
}

function groupFaceDetections(detections: Detection[]) {
  const groups = new Map<string, Detection[]>();
  for (const detection of detections) {
    const key = getFaceDetectionGroupKey(detection);
    const group = groups.get(key) ?? [];
    group.push(detection);
    groups.set(key, group);
  }

  return groups;
}

function getFaceOverlayKey(detection: Detection, trackKey?: string) {
  if (detection.refId != null) {
    return `face:${detection.refId}`;
  }

  return `face-track:${trackKey ?? detection.groupKey ?? detection.id}`;
}

function chooseCurrentFaceOverlay(existing: DetectionOverlay, candidate: DetectionOverlay, currentTime: number): DetectionOverlay {
  const existingDelta = Math.abs((existing.observedAtSec ?? currentTime) - currentTime);
  const candidateDelta = Math.abs((candidate.observedAtSec ?? currentTime) - currentTime);
  if (candidateDelta < existingDelta - 0.001) {
    return candidate;
  }

  if (Math.abs(candidateDelta - existingDelta) <= 0.001 && candidate.score > existing.score) {
    return candidate;
  }

  return existing;
}

function interpolateDetection(detections: Detection[], currentTime: number): Detection {
  const timed = detections
    .filter((detection) => detection.observedAtSec != null)
    .sort((left, right) => (left.observedAtSec ?? 0) - (right.observedAtSec ?? 0));

  if (timed.length === 0) {
    return detections[0];
  }

  if (timed.length === 1 || currentTime <= timed[0].observedAtSec!) {
    return timed[0];
  }

  const last = timed[timed.length - 1];
  if (currentTime >= last.observedAtSec!) {
    return last;
  }

  for (let index = 1; index < timed.length; index += 1) {
    const previous = timed[index - 1];
    const next = timed[index];
    const previousTime = previous.observedAtSec ?? currentTime;
    const nextTime = next.observedAtSec ?? previousTime;
    if (currentTime > nextTime) {
      continue;
    }

    const span = Math.max(nextTime - previousTime, 0.001);
    const ratio = Math.min(1, Math.max(0, (currentTime - previousTime) / span));
    const lerp = (left: number, right: number) => left + ((right - left) * ratio);
    return {
      ...previous,
      observedAtSec: currentTime,
      score: Math.max(previous.score, next.score),
      x: lerp(previous.x, next.x),
      y: lerp(previous.y, next.y),
      w: lerp(previous.w, next.w),
      h: lerp(previous.h, next.h),
    };
  }

  return last;
}

function formatDetectionBadge(detection: Detection, faceLabelsById?: Map<number, FaceOverlayInfo>) {
  const confidence = Math.round(detection.score * 100);
  const face = detection.refId != null && isFaceDetection(detection)
    ? faceLabelsById?.get(detection.refId)
    : undefined;
  if (face?.performerName?.trim()) {
    return `${detection.class} ${confidence}% · ${face.performerName.trim()}`;
  }

  const refText = detection.refKind && detection.refId != null
    ? ` · ${detection.refKind} #${detection.refId}`
    : "";
  return `${detection.class} ${confidence}%${refText}`;
}
