import { useCallback, useEffect, useRef, useState, type MouseEvent, type ReactNode } from "react";
import { entityImages, videos } from "../api/client";
import type { Video } from "../api/types";
import { EntityMedia, type EntityMediaFit, type EntityMediaSurface } from "./EntityMedia";
import { formatDuration } from "./shared";
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
  className = "",
  children,
}: {
  video: Video;
  fit: EntityMediaFit;
  surface?: EntityMediaSurface;
  coverWidth?: number;
  enableScrubbing?: boolean;
  className?: string;
  children?: ReactNode;
}) {
  const file = video.files[0];
  const clipDuration = typeof video.clipStartSec === "number" && typeof video.clipEndSec === "number"
    ? Math.max(0, video.clipEndSec - video.clipStartSec)
    : undefined;
  const duration = clipDuration ?? file?.duration ?? 0;
  const coverUrl = entityImages.videoCoverUrl(video.id, video.updatedAt, coverWidth);
  const previewUrl = videos.previewUrl(video.id);
  const coverAlt = video.imagePath ? video.title || "" : "";
  const [scrubSeconds, setScrubSeconds] = useState<number | null>(null);
  const scrubPercent = duration > 0 && scrubSeconds != null
    ? Math.min(100, Math.max(0, ((scrubSeconds - (video.clipStartSec ?? 0)) / duration) * 100))
    : 0;
  const scrubTimestamp = scrubSeconds != null ? formatDuration(scrubSeconds) : null;
  const scrubTimestampPercent = scrubSeconds != null ? Math.min(88, Math.max(12, scrubPercent)) : 0;
  const scrubImageUrl = scrubSeconds != null
    ? videos.screenshotUrl(video.id, video.updatedAt, scrubSeconds)
    : null;

  const updateScrubPreview = useCallback((event: MouseEvent<HTMLDivElement>) => {
    if (duration <= 0) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const percent = Math.min(1, Math.max(0, (event.clientX - rect.left) / Math.max(1, rect.width)));
    const nextSeconds = Math.round((video.clipStartSec ?? 0) + percent * duration);
    setScrubSeconds((current) => current === nextSeconds ? current : nextSeconds);
  }, [duration, video.clipStartSec]);

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
        renderDefault={() => <NativeVideoPreview coverUrl={coverUrl} coverAlt={coverAlt} previewUrl={previewUrl} fit={fit} />}
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
      {duration > 0 && enableScrubbing ? (
        <div
          className="absolute inset-x-0 bottom-0 z-[9] h-10 cursor-ew-resize"
          onMouseEnter={updateScrubPreview}
          onMouseMove={updateScrubPreview}
          onMouseLeave={() => setScrubSeconds(null)}
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
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
          <div className={`absolute inset-x-1 bottom-1 h-1 rounded-full bg-black/55 transition-opacity ${scrubSeconds != null ? "opacity-100" : "opacity-0"}`}>
            <div className="h-full rounded-full bg-accent" style={{ width: `${scrubPercent}%` }} />
          </div>
        </div>
      ) : null}
    </div>
  );
}
