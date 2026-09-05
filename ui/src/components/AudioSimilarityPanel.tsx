import { useQuery } from "@tanstack/react-query";
import { useMemo, type ReactNode } from "react";
import { Film, Volume2 } from "lucide-react";
import { useAudioSimilarityApi } from "../hooks/useAudioSimilarityApi";
import type { AudioSimilarVideo, EntityEngagement } from "../api/types";
import { formatDuration } from "./shared";
import { EntityCardGrid } from "./EntityCardGrid";
import { VideoCard } from "./EntityCards";
import { useManualContext } from "./ManualContext";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";

const SIMILAR_PER_PAGE = 8;
const AVAILABILITY_PER_PAGE = 1;

interface PanelProps {
  onNavigate: (route: any) => void;
}

export function useVideoAudioSimilarityAvailability(videoId?: number) {
  const audioSimilarity = useAudioSimilarityApi();
  const enabled = audioSimilarity != null && typeof videoId === "number" && videoId > 0;
  const preview = useQuery({
    queryKey: ["audio-similarity", "video", videoId, "has-embeddings"],
    queryFn: () => audioSimilarity!.videoHasEmbeddings(videoId!),
    enabled,
    retry: false,
  });
  // Version-skew safety net: an older AI.Audio build won't have the has-embeddings endpoint (the call
  // 404s). Fall back to the previous 1-item similarity probe so the tab still appears.
  const legacy = useQuery({
    queryKey: ["audio-similarity", "video", videoId, "availability-fallback"],
    queryFn: () => audioSimilarity!.similarVideosForVideo(videoId!, { perPage: AVAILABILITY_PER_PAGE }),
    enabled: enabled && preview.isError,
    retry: false,
  });

  if (!enabled) return { available: false, loading: false };
  if (preview.data) return { available: preview.data.hasEmbeddings, loading: false };
  if (preview.isError && legacy.data) return { available: legacy.data.items.length > 0, loading: false };
  if (preview.isError && legacy.isError) return { available: false, loading: false };
  return { available: false, loading: true };
}

export function useVideoAudioSimilarityAvailable(videoId?: number) {
  return useVideoAudioSimilarityAvailability(videoId).available;
}

export function VideoAudioSimilarityPanel({ videoId, onNavigate }: PanelProps & { videoId: number }) {
  useManualContext(["panel:audio-similarity", "feature:audio-similarity"]);
  const audioSimilarity = useAudioSimilarityApi();
  const similarVideos = useQuery({
    queryKey: ["audio-similarity", "video", videoId, "similar-videos"],
    queryFn: () => audioSimilarity!.similarVideosForVideo(videoId, { perPage: SIMILAR_PER_PAGE }),
    enabled: audioSimilarity != null,
    retry: false,
  });

  if (!audioSimilarity) {
    return <UnavailablePanel message="No audio embedding provider is available." />;
  }

  if (similarVideos.isError) {
    return <UnavailablePanel message="Audio similarity could not be loaded." />;
  }

  return (
    <div className="space-y-6">
      <SimilarityHeader />
      <SimilarVideoSection
        title="Similar Videos"
        items={similarVideos.data?.items ?? []}
        loading={similarVideos.isLoading}
        error={similarVideos.isError}
        onNavigate={onNavigate}
      />
    </div>
  );
}

function SimilarityHeader() {
  return (
    <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-muted">
      <Volume2 className="h-3.5 w-3.5" />
      Audio Similarity
    </div>
  );
}

function SimilarVideoSection({
  title,
  items,
  loading,
  error,
  onNavigate,
}: {
  title: string;
  items: AudioSimilarVideo[];
  loading: boolean;
  error: boolean;
  onNavigate: (route: any) => void;
}) {
  const videoIds = useMemo(() => items.map((item) => item.video.id), [items]);
  const { engagementById: videoEngagement } = useEntityEngagementBatch("video", videoIds);

  if (error) {
    return null;
  }

  return (
    <section>
      <SectionTitle title={title} count={items.length} />
      {loading ? (
        <LoadingPanel />
      ) : items.length === 0 ? (
        <EmptyPanel icon={<Film className="h-10 w-10" />} message="No audio matches yet." />
      ) : (
        <EntityCardGrid minCardWidth="240px" gapClassName="gap-4" className="mt-3">
          {items.map((item) => (
            <SimilarVideoCard
              key={item.video.id}
              item={item}
              engagement={videoEngagement.get(item.video.id)}
              onNavigate={onNavigate}
            />
          ))}
        </EntityCardGrid>
      )}
    </section>
  );
}

function SectionTitle({ title, count }: { title: string; count: number }) {
  return (
    <div className="flex items-center justify-between gap-3">
      <h2 className="text-sm font-semibold text-foreground">{title}</h2>
      {count > 0 ? <span className="text-xs text-muted">{count}</span> : null}
    </div>
  );
}

function SimilarVideoCard({
  item,
  engagement,
  onNavigate,
}: {
  item: AudioSimilarVideo;
  engagement?: EntityEngagement;
  onNavigate: (route: any) => void;
}) {
  const video = item.video;
  const matchStart = item.sectionIndex > 0 ? item.startSec : undefined;

  return (
    <div className="relative h-full">
      <VideoCard
        video={video}
        engagement={engagement}
        onClick={() =>
          onNavigate(
            matchStart != null ? { page: "video", id: video.id, seekTo: matchStart } : { page: "video", id: video.id },
          )
        }
        onNavigate={onNavigate}
      />
      <SimilarityOverlay distance={item.distance} label={getVideoMeta(item)} />
    </div>
  );
}

function SimilarityOverlay({ distance, label }: { distance: number; label?: string }) {
  const match = Math.max(0, Math.min(100, Math.round((1 - distance) * 100)));
  return (
    <div className="pointer-events-none absolute left-2 right-2 top-2 z-20 flex items-start justify-between gap-2">
      {label ? (
        <span className="max-w-[70%] truncate rounded bg-black/75 px-2 py-1 text-[11px] font-medium text-white shadow-sm">
          {label}
        </span>
      ) : (
        <span />
      )}
      <span className="shrink-0 rounded bg-black/75 px-2 py-1 text-[11px] font-medium text-white shadow-sm">
        {match}%
      </span>
    </div>
  );
}

function LoadingPanel() {
  return (
    <div className="mt-3 rounded-xl border border-border bg-surface/40 px-4 py-8 text-center text-sm text-secondary">
      Loading...
    </div>
  );
}

function EmptyPanel({ icon, message }: { icon: ReactNode; message: string }) {
  return (
    <div className="mt-3 flex flex-col items-center justify-center rounded-xl border border-dashed border-border bg-surface/40 px-4 py-8 text-center text-sm text-secondary">
      <div className="mb-3 text-muted opacity-60">{icon}</div>
      <p>{message}</p>
    </div>
  );
}

function UnavailablePanel({ message }: { message: string }) {
  return <EmptyPanel icon={<Volume2 className="h-10 w-10" />} message={message} />;
}

function getVideoMeta(item: AudioSimilarVideo) {
  if (item.sectionIndex > 0 && item.startSec != null) {
    return item.endSec != null
      ? `${formatDuration(item.startSec)} - ${formatDuration(item.endSec)}`
      : formatDuration(item.startSec);
  }

  return undefined;
}
