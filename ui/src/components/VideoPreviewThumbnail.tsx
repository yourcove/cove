import { useCallback, useEffect, useRef, useState, type MouseEvent, type ReactNode } from "react";
import { entityImages, videos } from "../api/client";
import type { Video } from "../api/types";
import { EntityMedia, type EntityMediaFit, type EntityMediaSurface } from "./EntityMedia";
import { formatDuration } from "./shared";
import { isPlainPrimaryClick } from "./cardNavigation";
import { findSpriteIndex, spriteCaptureTime, useVideoSprites } from "../hooks/useVideoSprites";
import { VideoCoverImage } from "./VideoCoverImage";

function NativeVideoPreview({
  coverUrl,
  coverAlt,
  previewUrl,
  fit,
}: {
  coverUrl: string;
  coverAlt: string;
  previewUrl: string;
  fit: EntityMediaFit;
}) {
  const videoRef = useRef<HTMLVideoElement>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    const observer = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (entry.intersectionRatio > 0) video.play().catch(() => {});
        else video.pause();
      });
    });
    observer.observe(video);
    return () => observer.disconnect();
  }, []);

  return (
    <>
      <VideoCoverImage
        src={coverUrl}
        alt={coverAlt}
        className="video-card-preview-image h-full w-full"
        fallbackClassName="video-card-cover-fallback"
        style={{ objectFit: fit }}
        loading="lazy"
      />
      <video
        ref={videoRef}
        disableRemotePlayback
        playsInline
        muted
        loop
        preload="none"
        src={previewUrl}
        className="video-card-preview-video"
        style={{ objectFit: fit }}
      />
    </>
  );
}

export function VideoPreviewThumbnail({
  video,
  fit,
  surface = "card",
  coverWidth = 1280,
  enableScrubbing = true,
  onScrubClick,
  className = "",
  children,
}: {
  video: Video;
  fit: EntityMediaFit;
  surface?: EntityMediaSurface;
  coverWidth?: number;
  enableScrubbing?: boolean;
  /** Called with the media time under the pointer when the scrub bar is clicked. */
  onScrubClick?: (seconds: number) => void;
  className?: string;
  children?: ReactNode;
}) {
  const file = video.files.find((candidate) => candidate.id === video.primaryFileId);
  const clipDuration =
    typeof video.clipStartSec === "number" && typeof video.clipEndSec === "number"
      ? Math.max(0, video.clipEndSec - video.clipStartSec)
      : undefined;
  const duration = clipDuration ?? file?.duration ?? 0;
  // Touch screens have no hover to scrub with, and iOS Safari turns a tap on a hover-reactive strip into a
  // hover that never clicks. Without a precise hover pointer the strip is left out and taps open the card.
  const canScrub =
    enableScrubbing && duration > 0 && window.matchMedia?.("(hover: hover) and (pointer: fine)").matches !== false;
  const coverUrl = entityImages.videoCoverUrl(video.id, video.updatedAt, coverWidth);
  const previewUrl = videos.previewUrl(video.id);
  const coverAlt = video.imagePath ? video.title || "" : "";
  // The pointer's media time; the time shown and opened is derived from it during render.
  const [hoveredSeconds, setHoveredSeconds] = useState<number | null>(null);
  // Load the sprite list on a card's first hover only, so a grid does not fetch one per card.
  const [spritesRequestedFor, setSpritesRequestedFor] = useState<number | null>(null);
  const sprites = useVideoSprites(spritesRequestedFor === video.id ? video.id : null);
  const clipStart = video.clipStartSec ?? 0;

  // The frame shown while scrubbing is the sprite tile covering the hovered time, so open the video at the moment
  // that tile was captured. Keep the time inside a clip and short of its end so playback does not stop at once.
  // Some Stash sprite lists have cue times that do not match the video; their tiles do not cover the hovered time,
  // so fall back to it. Without sprites, round down so the far right edge stays inside the video.
  const shownSecondsFor = (hovered: number) => {
    if (sprites) {
      const entry = sprites.entries[findSpriteIndex(sprites.entries, hovered)];
      const tileLength = entry.end - entry.start;
      if (tileLength > 0 && hovered < entry.end + tileLength) {
        const captured = spriteCaptureTime(sprites, entry);
        return Math.min(Math.max(clipStart, captured), Math.max(clipStart, clipStart + duration - 1));
      }
    }
    return Math.floor(hovered);
  };

  const scrubSeconds = hoveredSeconds != null ? shownSecondsFor(hoveredSeconds) : null;
  const scrubPercent =
    duration > 0 && scrubSeconds != null
      ? Math.min(100, Math.max(0, ((scrubSeconds - clipStart) / duration) * 100))
      : 0;
  const scrubTimestamp = scrubSeconds != null ? formatDuration(scrubSeconds) : null;
  const scrubTimestampPercent = scrubSeconds != null ? Math.min(88, Math.max(12, scrubPercent)) : 0;
  const scrubImageUrl = scrubSeconds != null ? videos.screenshotUrl(video.id, video.updatedAt, scrubSeconds) : null;

  const hoveredSecondsAt = useCallback(
    (event: MouseEvent<HTMLDivElement>) => {
      const rect = event.currentTarget.getBoundingClientRect();
      const percent = Math.min(1, Math.max(0, (event.clientX - rect.left) / Math.max(1, rect.width)));
      return clipStart + percent * duration;
    },
    [clipStart, duration],
  );

  const updateScrubPreview = useCallback(
    (event: MouseEvent<HTMLDivElement>) => {
      if (duration <= 0) return;
      setHoveredSeconds(hoveredSecondsAt(event));
    },
    [duration, hoveredSecondsAt],
  );

  return (
    <div className={`video-card-preview card-media relative aspect-video overflow-hidden bg-black ${className}`.trim()}>
      <EntityMedia
        entityType="video"
        entityId={video.id}
        surface={surface}
        imageUrl={coverUrl}
        alt={coverAlt}
        fit={fit}
        loading="lazy"
        className="video-card-preview-image h-full w-full"
        renderDefault={() => (
          <NativeVideoPreview coverUrl={coverUrl} coverAlt={coverAlt} previewUrl={previewUrl} fit={fit} />
        )}
      />
      {scrubImageUrl ? (
        <img
          src={scrubImageUrl}
          alt=""
          className="absolute inset-0 z-[7] h-full w-full"
          style={{ objectFit: fit }}
          draggable={false}
        />
      ) : null}
      {children}
      {canScrub ? (
        <div
          className="absolute inset-x-0 bottom-0 z-[9] h-10 cursor-ew-resize"
          onMouseEnter={(event) => {
            setSpritesRequestedFor(video.id);
            updateScrubPreview(event);
          }}
          onMouseMove={updateScrubPreview}
          onMouseLeave={() => setHoveredSeconds(null)}
          onClick={(event) => {
            // Keep an enclosing link or card from also handling the click.
            event.preventDefault();
            event.stopPropagation();
            // Modifier clicks would replace the list in this tab instead of opening a new one, so ignore them.
            if (isPlainPrimaryClick(event)) onScrubClick?.(shownSecondsFor(hoveredSecondsAt(event)));
          }}
          aria-hidden="true"
        >
          {scrubTimestamp ? (
            <div
              className="pointer-events-none absolute bottom-4 -translate-x-1/2 whitespace-nowrap rounded bg-black/80 px-1.5 py-0.5 text-[10px] font-medium text-white shadow"
              style={{ left: `${scrubTimestampPercent}%` }}
            >
              {scrubTimestamp}
            </div>
          ) : null}
          <div
            className={`absolute inset-x-1 bottom-1 h-1 rounded-full bg-black/55 transition-opacity ${scrubSeconds != null ? "opacity-100" : "opacity-0"}`}
          >
            <div className="h-full rounded-full bg-accent" style={{ width: `${scrubPercent}%` }} />
          </div>
        </div>
      ) : null}
    </div>
  );
}
