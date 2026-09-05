import { forwardRef, useCallback, useImperativeHandle, useRef, useState, type MouseEvent } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { FolderOpen } from "lucide-react";
import { galleries, images } from "../api/client";
import type { Gallery } from "../api/types";

export function getGalleryScrubImageIndex(pointerX: number, width: number, imageCount: number) {
  if (imageCount <= 1) return 0;
  const percent = Math.min(1, Math.max(0, pointerX / Math.max(1, width)));
  return Math.round(percent * (imageCount - 1));
}

interface GalleryScrubThumbnailProps {
  gallery: Gallery;
  coverUrl?: string | null;
  coverWidth?: number;
  fit?: "cover" | "contain";
  alt?: string;
  enableScrubbing?: boolean;
}

export interface GalleryScrubThumbnailHandle {
  updatePreview: (clientX: number, clientY: number) => void;
  resetPreview: () => void;
}

export const GalleryScrubThumbnail = forwardRef<GalleryScrubThumbnailHandle, GalleryScrubThumbnailProps>(
  function GalleryScrubThumbnail(
    { gallery, coverUrl, coverWidth = 640, fit = "cover", alt = "", enableScrubbing = true },
    ref,
  ) {
    const queryClient = useQueryClient();
    const containerRef = useRef<HTMLDivElement>(null);
    const resolvedCoverUrl =
      coverUrl === undefined
        ? (gallery.coverPath ?? galleries.coverUrl(gallery.id, gallery.updatedAt, coverWidth))
        : coverUrl;
    const [pendingUrl, setPendingUrl] = useState<string | null>(null);
    const [displayedUrl, setDisplayedUrl] = useState<string | null>(null);
    const [activeIndex, setActiveIndex] = useState<number | null>(null);
    const requestSequence = useRef(0);
    const requestedIndex = useRef<number | null>(null);
    const canScrub =
      enableScrubbing &&
      gallery.imageCount > 1 &&
      window.matchMedia?.("(hover: hover) and (pointer: fine)").matches !== false;
    const objectFitClass = fit === "contain" ? "object-contain" : "object-cover";

    const resetPreview = useCallback(() => {
      requestSequence.current += 1;
      requestedIndex.current = null;
      setPendingUrl(null);
      setDisplayedUrl(null);
      setActiveIndex(null);
    }, []);

    const updatePreviewAt = useCallback(
      (clientX: number, clientY: number) => {
        if (!canScrub) return;
        const rect = containerRef.current?.getBoundingClientRect();
        if (!rect) return;
        if (clientX < rect.left || clientX > rect.right || clientY < rect.top || clientY > rect.bottom) {
          resetPreview();
          return;
        }
        const imageIndex = getGalleryScrubImageIndex(clientX - rect.left, rect.width, gallery.imageCount);
        setActiveIndex(imageIndex);
        if (requestedIndex.current === imageIndex) return;
        requestedIndex.current = imageIndex;
        const sequence = ++requestSequence.current;
        setPendingUrl(null);

        void queryClient
          .fetchQuery({
            queryKey: ["gallery-scrub-image", gallery.id, gallery.updatedAt, imageIndex],
            queryFn: () =>
              images.find(
                { page: imageIndex + 1, perPage: 1, sort: "path", direction: "asc" },
                { galleryId: gallery.id },
              ),
            staleTime: 5 * 60 * 1000,
          })
          .then((result) => {
            if (requestSequence.current !== sequence) return;
            const image = result.items[0];
            setPendingUrl(image ? images.thumbnailUrl(image.id, 320) : null);
          })
          .catch(() => {
            if (requestSequence.current === sequence) {
              setPendingUrl(null);
            }
          });
      },
      [canScrub, gallery.id, gallery.imageCount, gallery.updatedAt, queryClient, resetPreview],
    );

    const updatePreview = useCallback(
      (event: MouseEvent<HTMLDivElement>) => {
        updatePreviewAt(event.clientX, event.clientY);
      },
      [updatePreviewAt],
    );

    useImperativeHandle(ref, () => ({ updatePreview: updatePreviewAt, resetPreview }), [resetPreview, updatePreviewAt]);

    return (
      <div
        ref={containerRef}
        className={`relative h-full w-full ${canScrub ? "cursor-ew-resize" : ""}`}
        onMouseMove={updatePreview}
        onMouseLeave={resetPreview}
        data-testid="gallery-scrub-thumbnail"
      >
        {!displayedUrl && resolvedCoverUrl ? (
          <>
            <img
              src={resolvedCoverUrl}
              alt={alt}
              className={`h-full w-full ${objectFitClass}`}
              loading="lazy"
              onError={(event) => {
                const image = event.currentTarget;
                image.style.display = "none";
                const fallback = image.nextElementSibling as HTMLElement | null;
                if (fallback) fallback.style.display = "flex";
              }}
            />
            <div className="hidden h-full w-full items-center justify-center text-muted">
              <FolderOpen className="h-7 w-7" />
            </div>
          </>
        ) : !displayedUrl ? (
          <div className="flex h-full w-full items-center justify-center text-muted">
            <FolderOpen className="h-7 w-7" />
          </div>
        ) : null}
        {displayedUrl ? (
          <img
            src={displayedUrl}
            alt=""
            className={`absolute inset-0 h-full w-full ${objectFitClass}`}
            draggable={false}
            onError={() => setDisplayedUrl(null)}
          />
        ) : null}
        {pendingUrl && pendingUrl !== displayedUrl ? (
          <img
            src={pendingUrl}
            alt=""
            className={`absolute inset-0 h-full w-full ${objectFitClass} opacity-0`}
            draggable={false}
            onLoad={() => {
              setDisplayedUrl(pendingUrl);
              setPendingUrl(null);
            }}
            onError={() => setPendingUrl(null)}
          />
        ) : null}
        {activeIndex != null ? (
          <div className="pointer-events-none absolute inset-x-0 bottom-0 z-[8] h-10">
            <div
              className="absolute bottom-4 -translate-x-1/2 whitespace-nowrap rounded bg-black/80 px-1.5 py-0.5 text-[10px] font-medium text-white shadow"
              style={{
                left: `${Math.min(88, Math.max(12, (activeIndex / Math.max(1, gallery.imageCount - 1)) * 100))}%`,
              }}
            >
              {activeIndex + 1} / {gallery.imageCount}
            </div>
            <div className="absolute inset-x-1 bottom-1 h-1 rounded-full bg-black/55">
              <div
                className="h-full rounded-full bg-accent"
                style={{ width: `${(activeIndex / Math.max(1, gallery.imageCount - 1)) * 100}%` }}
              />
            </div>
          </div>
        ) : null}
      </div>
    );
  },
);
