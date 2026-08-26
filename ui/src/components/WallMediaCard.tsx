import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { HTMLAttributes, ReactNode } from "react";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { createPlaybackTracker, type PlaybackTrackingTarget } from "../utils/interactionTracking";
import { serverAwareFetch } from "../state/serverAvailability";

export interface WallMediaVideoControlsState {
  currentTime: number;
  duration: number;
  progressPercent: number;
  isPlaying: boolean;
  seekToPercent: (percent: number) => void;
  togglePlayback: () => void;
  toggleFullscreen: () => void;
  isFullscreen: boolean;
}

interface WallMediaCardProps extends HTMLAttributes<HTMLDivElement> {
  title: string;
  imageSrc?: string | null;
  videoSrc?: string | null;
  videoStatusSrc?: string | null;
  useVideo?: boolean;
  muted?: boolean;
  videoStartTimeSec?: number;
  videoEndTimeSec?: number;
  videoLoadRootMargin?: string;
  videoPlayThreshold?: number;
  aspectRatio?: string;
  fillMedia?: boolean;
  fallback?: ReactNode;
  imageAlt?: string;
  imageClassName?: string;
  videoClassName?: string;
  chromeless?: boolean;
  videoControls?: (state: WallMediaVideoControlsState) => ReactNode;
  onVideoPlayEligibilityChange?: (eligible: boolean) => void;
  playbackTracking?: PlaybackTrackingTarget;
  trackingEnabled?: boolean;
}

function roundPlaybackTime(value: number) {
  return Math.round(value * 1000) / 1000;
}

export function WallMediaCard({
  title,
  imageSrc,
  videoSrc,
  videoStatusSrc,
  useVideo = false,
  muted = true,
  videoStartTimeSec = 0,
  videoEndTimeSec,
  videoLoadRootMargin = "320px 0px",
  videoPlayThreshold = 0.6,
  aspectRatio = "1 / 1",
  fillMedia = false,
  fallback,
  imageAlt,
  imageClassName,
  videoClassName,
  chromeless = false,
  videoControls,
  onVideoPlayEligibilityChange,
  playbackTracking,
  trackingEnabled = true,
  className,
  children,
  ...props
}: WallMediaCardProps) {
  const appConfig = useOptionalAppConfig();
  const mediaRef = useRef<HTMLDivElement>(null);
  const videoRef = useRef<HTMLVideoElement>(null);
  const [videoFailed, setVideoFailed] = useState(false);
  const [videoAvailable, setVideoAvailable] = useState(false);
  const [shouldLoadVideo, setShouldLoadVideo] = useState(false);
  const [shouldPlayVideo, setShouldPlayVideo] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [videoDuration, setVideoDuration] = useState(0);
  const [isPlaying, setIsPlaying] = useState(false);
  const [isFullscreen, setIsFullscreen] = useState(false);
  const playbackTracker = useRef(createPlaybackTracker());
  const intervalStart = useRef<number | null>(null);
  const lastSeenTime = useRef(0);
  const lastKeepaliveSentAt = useRef(0);
  const onVideoPlayEligibilityChangeRef = useRef(onVideoPlayEligibilityChange);
  const lastReportedVideoPlayEligibilityRef = useRef<boolean | null>(null);

  useEffect(() => {
    onVideoPlayEligibilityChangeRef.current = onVideoPlayEligibilityChange;
  }, [onVideoPlayEligibilityChange]);

  const setVideoPlayEligibility = useCallback((eligible: boolean) => {
    setShouldPlayVideo(eligible);
    if (lastReportedVideoPlayEligibilityRef.current !== eligible) {
      lastReportedVideoPlayEligibilityRef.current = eligible;
      onVideoPlayEligibilityChangeRef.current?.(eligible);
    }
  }, []);

  const playbackTrackingTarget = useMemo<PlaybackTrackingTarget | null>(() => {
    if (!trackingEnabled) {
      return null;
    }

    if (!playbackTracking) {
      return null;
    }

    return {
      ...playbackTracking,
      muted,
      autoplay: shouldPlayVideo,
      fullscreen: isFullscreen,
      route: typeof window === "undefined" ? playbackTracking.route : playbackTracking.route ?? `${window.location.pathname}${window.location.search}${window.location.hash}`,
    };
  }, [isFullscreen, muted, playbackTracking, shouldPlayVideo, trackingEnabled]);
  const playbackTrackingSignature = useMemo(() => JSON.stringify(playbackTrackingTarget), [playbackTrackingTarget]);

  useEffect(() => {
    setVideoFailed(false);
    setCurrentTime(0);
    setVideoDuration(0);
    setIsPlaying(false);
    intervalStart.current = null;
    lastSeenTime.current = 0;
    lastKeepaliveSentAt.current = 0;
  }, [videoSrc]);

  useEffect(() => {
    void playbackTracker.current.setTarget(playbackTrackingTarget);
  }, [playbackTrackingSignature]);

  useEffect(() => () => {
    void playbackTracker.current.dispose();
  }, []);

  useEffect(() => {
    const handleFullscreenChange = () => setIsFullscreen(document.fullscreenElement === mediaRef.current);
    document.addEventListener("fullscreenchange", handleFullscreenChange);
    return () => document.removeEventListener("fullscreenchange", handleFullscreenChange);
  }, []);

  useEffect(() => {
    if (!useVideo || !videoSrc) {
      setShouldLoadVideo(false);
      setVideoPlayEligibility(false);
      return;
    }

    const element = mediaRef.current;
    if (!element) return;

    if (typeof IntersectionObserver === "undefined") {
      setShouldLoadVideo(true);
      setVideoPlayEligibility(true);
      return;
    }

    const loadObserver = new IntersectionObserver(([entry]) => {
      setShouldLoadVideo(entry.isIntersecting);
    }, { rootMargin: videoLoadRootMargin, threshold: 0 });
    const playObserver = new IntersectionObserver(([entry]) => {
      const intersectionRatio = typeof entry.intersectionRatio === "number"
        ? entry.intersectionRatio
        : (entry.isIntersecting ? 1 : 0);
      setVideoPlayEligibility(entry.isIntersecting && intersectionRatio >= videoPlayThreshold);
    }, { threshold: [0, Math.min(1, Math.max(0.01, videoPlayThreshold)), 1] });

    loadObserver.observe(element);
    playObserver.observe(element);
    return () => {
      loadObserver.disconnect();
      playObserver.disconnect();
      setVideoPlayEligibility(false);
    };
  }, [setVideoPlayEligibility, useVideo, videoLoadRootMargin, videoPlayThreshold, videoSrc]);

  useEffect(() => {
    if (!useVideo || !videoSrc || !shouldLoadVideo) {
      setVideoAvailable(false);
      return;
    }

    if (!videoStatusSrc) {
      setVideoAvailable(true);
      return;
    }

    const controller = new AbortController();
    setVideoAvailable(false);
    serverAwareFetch(videoStatusSrc, { method: "GET", signal: controller.signal })
      .then((response) => {
        return response.ok ? response.json() as Promise<{ available?: boolean }> : { available: false };
      })
      .then((status) => {
        if (!controller.signal.aborted) setVideoAvailable(status.available === true);
      })
      .catch(() => {
        if (!controller.signal.aborted) setVideoAvailable(false);
      });

    return () => controller.abort();
  }, [shouldLoadVideo, useVideo, videoSrc, videoStatusSrc]);

  const seekToStartTime = () => {
    const video = videoRef.current;
    if (!video || videoStartTimeSec <= 0 || !Number.isFinite(video.duration)) return;
    if (video.duration > videoStartTimeSec + 1) {
      video.currentTime = videoStartTimeSec;
    }
  };

  const restartBoundedVideo = (video: HTMLVideoElement, nextTime: number) => {
    if (videoEndTimeSec == null || !Number.isFinite(videoEndTimeSec) || nextTime < videoEndTimeSec) return false;

    lastSeenTime.current = roundPlaybackTime(videoEndTimeSec);
    flushInterval("active");
    intervalStart.current = null;
    const restartTime = Number.isFinite(videoStartTimeSec) && videoStartTimeSec >= 0
      ? videoStartTimeSec
      : 0;
    video.currentTime = restartTime;
    setCurrentTime(restartTime);
    lastSeenTime.current = roundPlaybackTime(restartTime);
    if (restartTime >= videoEndTimeSec) {
      video.pause();
      setIsPlaying(false);
    }
    return true;
  };

  const syncVideoMetrics = () => {
    const video = videoRef.current;
    if (!video) return;
    const nextTime = Number.isFinite(video.currentTime) ? video.currentTime : 0;
    setCurrentTime(nextTime);
    setVideoDuration(Number.isFinite(video.duration) ? video.duration : 0);
    lastSeenTime.current = roundPlaybackTime(nextTime);
  };

  const flushInterval = useCallback((state: string, mode: "default" | "keepalive" = "default") => {
    const video = videoRef.current;
    if (!playbackTrackingTarget || !video || intervalStart.current === null) return;
    const startSec = intervalStart.current;
    const endSec = roundPlaybackTime(lastSeenTime.current);
    if (endSec <= startSec) return;
    const mediaDurationSec = Number.isFinite(video.duration) && video.duration > 0
      ? video.duration
      : Math.max(videoDuration, endSec, 0);
    playbackTracker.current.recordInterval({
      startSec,
      endSec,
      mediaDurationSec,
      currentPositionSec: endSec,
      state,
      mode,
    });
  }, [playbackTrackingTarget, videoDuration]);

  const startTrackedInterval = useCallback((time: number) => {
    intervalStart.current = roundPlaybackTime(time);
    lastSeenTime.current = roundPlaybackTime(time);
    lastKeepaliveSentAt.current = Date.now();
  }, []);

  const seekToPercent = (percent: number) => {
    const video = videoRef.current;
    const duration = videoDuration || video?.duration || 0;
    if (!video || duration <= 0 || !Number.isFinite(duration)) return;
    video.currentTime = Math.min(duration, Math.max(0, duration * Math.min(1, Math.max(0, percent))));
    syncVideoMetrics();
  };

  const togglePlayback = () => {
    const video = videoRef.current;
    if (!video) return;
    if (video.paused) {
      const playResult = video.play();
      if (playResult && typeof playResult.catch === "function") {
        playResult.catch(() => {});
      }
      return;
    }

    video.pause();
  };

  const toggleFullscreen = () => {
    const element = mediaRef.current;
    if (!element) return;
    if (document.fullscreenElement === element) {
      void document.exitFullscreen?.();
      return;
    }
    void element.requestFullscreen?.();
  };

  useEffect(() => {
    seekToStartTime();
  }, [videoSrc, videoStartTimeSec, videoEndTimeSec, videoAvailable, shouldLoadVideo]);

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !useVideo || !videoSrc || !videoAvailable || videoFailed) return;

    if (shouldPlayVideo) {
      const playResult = video.play();
      if (playResult && typeof playResult.catch === "function") {
        playResult.catch(() => {});
      }
    } else {
      video.pause();
    }
  }, [shouldPlayVideo, useVideo, videoSrc, videoAvailable, videoFailed]);

  useEffect(() => {
    if (!playbackTrackingTarget) {
      return;
    }

    const flushPausedKeepalive = () => flushInterval("paused", "keepalive");
    const handleVisibilityChange = () => {
      if (document.visibilityState === "hidden") {
        flushPausedKeepalive();
      }
    };

    window.addEventListener("pagehide", flushPausedKeepalive);
    document.addEventListener("visibilitychange", handleVisibilityChange);
    return () => {
      window.removeEventListener("pagehide", flushPausedKeepalive);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
      flushPausedKeepalive();
    };
  }, [flushInterval, playbackTrackingTarget]);

  const imageFitClass = appConfig?.config?.ui.imageObjectFit === "contain" ? "object-contain" : "object-cover";
  const videoFitClass = appConfig?.config?.ui.videoObjectFit === "contain" ? "object-contain" : "object-cover";
  const resolvedImageClassName = imageClassName ?? imageFitClass;
  const resolvedVideoClassName = videoClassName ?? videoFitClass;
  const wrapperClassName = chromeless
    ? `cursor-pointer overflow-hidden ${className ?? ""}`.trim()
    : `cursor-pointer rounded overflow-hidden border border-border hover:border-accent/60 transition-all ${className ?? ""}`.trim();
  const mediaContainerClassName = chromeless ? "bg-transparent" : "bg-surface";

  return (
    <div
      {...props}
      className={wrapperClassName}
      title={title}
    >
      <div ref={mediaRef} className={`relative w-full ${mediaContainerClassName} ${fillMedia ? "h-full" : ""}`.trim()} style={fillMedia ? undefined : { aspectRatio }}>
        {useVideo && videoSrc && shouldLoadVideo && videoAvailable && !videoFailed ? (
          <video
            ref={videoRef}
            src={videoSrc}
            poster={imageSrc ?? undefined}
            className={`absolute inset-0 h-full w-full ${resolvedVideoClassName}`}
            muted={muted}
            playsInline
            loop
            preload={shouldPlayVideo ? "auto" : "metadata"}
            onLoadedMetadata={() => { seekToStartTime(); syncVideoMetrics(); }}
            onDurationChange={syncVideoMetrics}
            onPlay={() => {
              const video = videoRef.current;
              setIsPlaying(true);
              startTrackedInterval(roundPlaybackTime(video?.currentTime ?? currentTime));
            }}
            onPause={() => {
              setIsPlaying(false);
              flushInterval("paused");
              intervalStart.current = null;
            }}
            onSeeking={() => {
              if (intervalStart.current !== null) {
                flushInterval("active");
                intervalStart.current = null;
              }
            }}
            onSeeked={() => {
              const video = videoRef.current;
              if (video && !video.paused) {
                startTrackedInterval(roundPlaybackTime(video.currentTime));
              }
            }}
            onTimeUpdate={() => {
              const video = videoRef.current;
              const nextTime = roundPlaybackTime(video && Number.isFinite(video.currentTime) ? video.currentTime : 0);
              if (video && restartBoundedVideo(video, nextTime)) return;
              const previousTime = lastSeenTime.current;
              if (intervalStart.current !== null && nextTime + 0.25 < previousTime) {
                flushInterval("active");
                intervalStart.current = nextTime;
                lastKeepaliveSentAt.current = Date.now();
              }
              syncVideoMetrics();
              if (intervalStart.current !== null) {
                const now = Date.now();
                if (now - lastKeepaliveSentAt.current >= 10000) {
                  lastKeepaliveSentAt.current = now;
                  flushInterval("active");
                  intervalStart.current = roundPlaybackTime(videoRef.current?.currentTime ?? 0);
                }
              }
            }}
            onError={() => {
              flushInterval("paused", "keepalive");
              intervalStart.current = null;
              setIsPlaying(false);
              setVideoFailed(true);
            }}
          />
        ) : imageSrc ? (
          <img
            src={imageSrc}
            alt={imageAlt ?? title}
            className={`absolute inset-0 h-full w-full ${resolvedImageClassName}`}
            loading="lazy"
          />
        ) : (
          <div className="absolute inset-0 flex items-center justify-center">
            {fallback}
          </div>
        )}
        {children}
        {videoControls && useVideo && videoSrc && shouldLoadVideo && videoAvailable && !videoFailed ? videoControls({
          currentTime,
          duration: videoDuration,
          progressPercent: videoDuration > 0 ? Math.min(100, Math.max(0, (currentTime / videoDuration) * 100)) : 0,
          isPlaying,
          seekToPercent,
          togglePlayback,
          toggleFullscreen,
          isFullscreen,
        }) : null}
      </div>
    </div>
  );
}
