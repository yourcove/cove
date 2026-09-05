import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { videos, images, playback } from "../api/client";
import { formatDuration, formatFileSize, formatDate, getResolutionLabel, TagBadge } from "./shared";
import { X, ExternalLink, Star, User, Tag, Building2, Calendar, Film, Clock, HardDrive, Monitor } from "lucide-react";
import { RatingBadge } from "./Rating";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { VideoPlayer } from "./VideoPlayer";
import { createPlaybackSessionId, trackInteraction } from "../utils/interactionTracking";

interface VideoQuickViewProps {
  type: "video";
  id: number;
  onClose: () => void;
  onNavigate: (r: any) => void;
}

interface ImageQuickViewProps {
  type: "image";
  id: number;
  onClose: () => void;
  onNavigate: (r: any) => void;
}

type QuickViewProps = VideoQuickViewProps | ImageQuickViewProps;

export function QuickViewDialog(props: QuickViewProps) {
  if (props.type === "video") return <VideoQuickView {...props} />;
  return <ImageQuickView {...props} />;
}

function VideoQuickView({ id, onClose, onNavigate }: Omit<VideoQuickViewProps, "type">) {
  const { engagement } = useEntityEngagement("video", id);
  const { data: video, isLoading } = useQuery({
    queryKey: ["video", id],
    queryFn: () => videos.get(id),
  });

  useEffect(() => {
    if (!video) {
      return;
    }

    trackInteraction({
      hostType: "video",
      hostId: video.id,
      kind: "openDetail",
      meta: { surface: "quickView" },
    });
  }, [video?.id]);

  if (isLoading || !video) {
    return (
      <Overlay onClose={onClose}>
        <div className="flex items-center justify-center h-64">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
        </div>
      </Overlay>
    );
  }

  const file = video.files?.[0];
  const duration = file?.duration ?? 0;
  const resLabel = file ? getResolutionLabel(file.width, file.height) : null;

  return (
    <Overlay onClose={onClose}>
      {/* Header */}
      <div className="flex items-center justify-between px-5 py-3 border-b border-border">
        <h2 className="text-lg font-semibold text-foreground truncate pr-4">
          {video.title || file?.basename || "Untitled"}
        </h2>
        <div className="flex items-center gap-2 flex-shrink-0">
          <button
            onClick={() => {
              onClose();
              onNavigate({ page: "video", id });
            }}
            className="flex items-center gap-1 px-2.5 py-1 text-xs bg-accent text-white rounded hover:bg-accent-hover"
          >
            <ExternalLink className="w-3 h-3" /> Open
          </button>
          <button onClick={onClose} className="p-1 rounded hover:bg-card-hover text-muted hover:text-foreground">
            <X className="w-4 h-4" />
          </button>
        </div>
      </div>

      {/* Preview */}
      <div className="aspect-video bg-black">
        {file ? (
          <VideoPlayer
            streamUrl={videos.streamUrl(video.id)}
            posterUrl={videos.screenshotUrl(video.id, video.updatedAt)}
            format={file.format}
            audioCodec={file.audioCodec}
            duration={duration}
            clip={
              video.parentVideoId != null
                ? { start: video.clipStartSec ?? 0, end: video.clipEndSec, loop: false }
                : undefined
            }
            videoId={video.id}
            extensionSurface="quick-view"
            playbackTracking={{
              hostType: "video",
              hostId: video.id,
              surface: "quickView",
              scopeKey: `video:${video.id}:quickView`,
            }}
            showAbLoop={false}
          />
        ) : (
          <img src={videos.screenshotUrl(video.id, video.updatedAt)} alt="" className="h-full w-full object-contain" />
        )}
      </div>

      {/* Details */}
      <div className="p-5 space-y-4 max-h-[50vh] overflow-y-auto">
        {/* Rating */}
        {engagement?.rating != null && (
          <div className="flex items-center gap-2">
            <Star className="w-4 h-4 text-yellow-400" />
            <RatingBadge rating={engagement.rating} />
          </div>
        )}

        {/* Meta row */}
        <div className="flex flex-wrap gap-3 text-xs text-secondary">
          {video.date && (
            <span className="flex items-center gap-1">
              <Calendar className="w-3 h-3" /> {video.date}
            </span>
          )}
          {resLabel && (
            <span className="flex items-center gap-1">
              <Monitor className="w-3 h-3" /> {resLabel}
            </span>
          )}
          {file && (
            <span className="flex items-center gap-1">
              <HardDrive className="w-3 h-3" /> {formatFileSize(file.size)}
            </span>
          )}
          {duration > 0 && (
            <span className="flex items-center gap-1">
              <Clock className="w-3 h-3" /> {formatDuration(duration)}
            </span>
          )}
        </div>

        {/* Studio */}
        {video.studioName && (
          <button
            onClick={() => {
              onClose();
              onNavigate({ page: "studio", id: video.studioId });
            }}
            className="flex items-center gap-2 text-sm text-foreground hover:text-accent"
          >
            <Building2 className="w-4 h-4 text-muted" />
            {video.studioName}
          </button>
        )}

        {/* Performers */}
        {video.performers?.length > 0 && (
          <div>
            <div className="flex items-center gap-1 text-xs text-muted mb-1.5">
              <User className="w-3 h-3" /> Performers
            </div>
            <div className="flex flex-wrap gap-1.5">
              {video.performers.map((p: any) => (
                <button
                  key={p.id}
                  onClick={() => {
                    onClose();
                    onNavigate({ page: "performer", id: p.id });
                  }}
                  className="flex items-center gap-1.5 rounded-full border border-border bg-surface px-2 py-1 hover:border-accent/50 transition-colors"
                >
                  {p.imagePath ? (
                    <img src={p.imagePath} alt="" className="h-5 w-5 rounded-full object-cover" />
                  ) : (
                    <User className="h-4 w-4 text-muted" />
                  )}
                  <span className="text-xs text-secondary hover:text-accent">{p.name}</span>
                </button>
              ))}
            </div>
          </div>
        )}

        {/* Tags */}
        {video.tags?.length > 0 && (
          <div>
            <div className="flex items-center gap-1 text-xs text-muted mb-1.5">
              <Tag className="w-3 h-3" /> Tags
            </div>
            <div className="flex flex-wrap gap-1">
              {video.tags.map((t: any) => (
                <TagBadge
                  key={t.id}
                  name={t.name}
                  tag={t}
                  onClick={() => {
                    onClose();
                    onNavigate({ page: "tag", id: t.id });
                  }}
                />
              ))}
            </div>
          </div>
        )}

        {/* Details text */}
        {video.details && <p className="text-sm text-secondary leading-relaxed">{video.details}</p>}

        {/* File path */}
        {file && (
          <div className="text-xs text-muted truncate" title={file.path || file.basename}>
            {file.path || file.basename}
          </div>
        )}
      </div>
    </Overlay>
  );
}

function ImageQuickView({ id, onClose, onNavigate }: Omit<ImageQuickViewProps, "type">) {
  const { engagement } = useEntityEngagement("image", id);
  const { data: image, isLoading } = useQuery({
    queryKey: ["image", id],
    queryFn: () => images.get(id),
  });

  useEffect(() => {
    if (!image) {
      return;
    }

    const imageId = image.id;
    const startedAt = typeof performance === "undefined" ? Date.now() : performance.now();
    const sessionId = createPlaybackSessionId();
    const elapsedSeconds = () => {
      const now = typeof performance === "undefined" ? Date.now() : performance.now();
      return Math.max(0.001, (now - startedAt) / 1000);
    };

    trackInteraction({
      hostType: "image",
      hostId: imageId,
      kind: "openDetail",
      meta: { surface: "quickView" },
    });

    let flushed = false;
    const flushDwell = (state: "ended" | "abandoned") => {
      if (flushed) return;
      flushed = true;
      const durationSec = elapsedSeconds();
      void playback
        .recordIntervals({
          hostType: "image",
          hostId: imageId,
          sessionId,
          mediaDurationSec: durationSec,
          currentPositionSec: durationSec,
          state,
          surface: "quickView",
          scopeKey: `image:${imageId}:quickView`,
          intervals: [{ startSec: 0, endSec: durationSec }],
        })
        .catch(() => {});
    };

    const handlePageHide = () => flushDwell("abandoned");
    window.addEventListener("pagehide", handlePageHide);
    return () => {
      window.removeEventListener("pagehide", handlePageHide);
      flushDwell("ended");
    };
  }, [image?.id]);

  if (isLoading || !image) {
    return (
      <Overlay onClose={onClose}>
        <div className="flex items-center justify-center h-64">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
        </div>
      </Overlay>
    );
  }

  const fileUrl = image.urls?.[0];
  const displayTitle = getImageDisplayTitle(image);

  return (
    <Overlay onClose={onClose}>
      {/* Header */}
      <div className="flex items-center justify-between px-5 py-3 border-b border-border">
        <h2 className="text-lg font-semibold text-foreground truncate pr-4">{displayTitle}</h2>
        <div className="flex items-center gap-2 flex-shrink-0">
          <button
            onClick={() => {
              onClose();
              onNavigate({ page: "image", id });
            }}
            className="flex items-center gap-1 px-2.5 py-1 text-xs bg-accent text-white rounded hover:bg-accent-hover"
          >
            <ExternalLink className="w-3 h-3" /> Open
          </button>
          <button onClick={onClose} className="p-1 rounded hover:bg-card-hover text-muted hover:text-foreground">
            <X className="w-4 h-4" />
          </button>
        </div>
      </div>

      {/* Preview */}
      <div
        className="bg-black flex items-center justify-center h-[50vh]"
        onClick={() => {
          onClose();
          onNavigate({ page: "image", id });
        }}
      >
        <img
          src={images.thumbnailUrl(image.id, 1600)}
          alt=""
          className="w-full h-[50vh] object-contain cursor-pointer"
        />
      </div>

      {/* Details */}
      <div className="p-5 space-y-4 max-h-[40vh] overflow-y-auto">
        {/* Rating */}
        {engagement?.rating != null && (
          <div className="flex items-center gap-2">
            <Star className="w-4 h-4 text-yellow-400" />
            <RatingBadge rating={engagement.rating} />
          </div>
        )}

        {/* Meta */}
        <div className="flex flex-wrap gap-3 text-xs text-secondary">
          {image.createdAt && (
            <span className="flex items-center gap-1">
              <Calendar className="w-3 h-3" /> {formatDate(image.createdAt)}
            </span>
          )}
        </div>

        {/* Studio */}
        {image.studioName && (
          <button
            onClick={() => {
              onClose();
              onNavigate({ page: "studio", id: image.studioId });
            }}
            className="flex items-center gap-2 text-sm text-foreground hover:text-accent"
          >
            <Building2 className="w-4 h-4 text-muted" />
            {image.studioName}
          </button>
        )}

        {/* Performers */}
        {image.performers?.length > 0 && (
          <div>
            <div className="flex items-center gap-1 text-xs text-muted mb-1.5">
              <User className="w-3 h-3" /> Performers
            </div>
            <div className="flex flex-wrap gap-1.5">
              {image.performers.map((p: any) => (
                <button
                  key={p.id}
                  onClick={() => {
                    onClose();
                    onNavigate({ page: "performer", id: p.id });
                  }}
                  className="flex items-center gap-1.5 rounded-full border border-border bg-surface px-2 py-1 hover:border-accent/50 transition-colors"
                >
                  <User className="h-4 w-4 text-muted" />
                  <span className="text-xs text-secondary hover:text-accent">{p.name}</span>
                </button>
              ))}
            </div>
          </div>
        )}

        {/* Tags */}
        {image.tags?.length > 0 && (
          <div>
            <div className="flex items-center gap-1 text-xs text-muted mb-1.5">
              <Tag className="w-3 h-3" /> Tags
            </div>
            <div className="flex flex-wrap gap-1">
              {image.tags.map((t: any) => (
                <TagBadge
                  key={t.id}
                  name={t.name}
                  tag={t}
                  onClick={() => {
                    onClose();
                    onNavigate({ page: "tag", id: t.id });
                  }}
                />
              ))}
            </div>
          </div>
        )}

        {/* URL */}
        {fileUrl && (
          <div className="text-xs text-muted truncate" title={fileUrl}>
            {fileUrl}
          </div>
        )}
      </div>
    </Overlay>
  );
}

function Overlay({ onClose, children }: { onClose: () => void; children: React.ReactNode }) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70" onClick={onClose}>
      <div
        className="bg-surface border border-border rounded-xl shadow-2xl w-full max-w-2xl max-h-[90vh] overflow-hidden flex flex-col"
        onClick={(e) => e.stopPropagation()}
      >
        {children}
      </div>
    </div>
  );
}
