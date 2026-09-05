import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo, useState, type ReactNode } from "react";
import { Film, Image as ImageIcon, Sparkles } from "lucide-react";
import { useVisualSimilarityApi } from "../hooks/useVisualSimilarityApi";
import type { EntityEngagement, VisualSimilarImage, VisualSimilarVideo } from "../api/types";
import { formatDuration } from "./shared";
import { EntityCardGrid } from "./EntityCardGrid";
import { ImageTile, VideoCard } from "./EntityCards";
import { useManualContext } from "./ManualContext";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";

const DEFAULT_SIMILAR_PER_PAGE = 8;
const SIMILAR_PER_PAGE_OPTIONS = [8, 16, 24, 48];
const AVAILABILITY_PER_PAGE = 1;
const PER_PAGE_STORAGE_KEY = "visual-similarity:per-page";
const KIND_STORAGE_KEY = "visual-similarity:kind";

type SimilarKind = "videos" | "images";

interface PanelProps {
  onNavigate: (route: any) => void;
}

function readStorage(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStorage(key: string, value: string) {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Ignore storage failures (private mode, quota); the choice just won't persist.
  }
}

function usePersistedPerPage(): [number, (next: number) => void] {
  const [perPage, setPerPage] = useState(() => {
    const raw = Number(readStorage(PER_PAGE_STORAGE_KEY));
    return Number.isInteger(raw) && raw > 0 ? raw : DEFAULT_SIMILAR_PER_PAGE;
  });
  const update = useCallback((next: number) => {
    setPerPage(next);
    writeStorage(PER_PAGE_STORAGE_KEY, String(next));
  }, []);
  return [perPage, update];
}

function usePersistedKind(defaultKind: SimilarKind): [SimilarKind, (next: SimilarKind) => void] {
  const [kind, setKind] = useState<SimilarKind>(() => {
    const raw = readStorage(KIND_STORAGE_KEY);
    return raw === "videos" || raw === "images" ? raw : defaultKind;
  });
  const update = useCallback((next: SimilarKind) => {
    setKind(next);
    writeStorage(KIND_STORAGE_KEY, next);
  }, []);
  return [kind, update];
}

export function useVideoVisualSimilarityAvailability(videoId?: number) {
  const visualSimilarity = useVisualSimilarityApi();
  const enabled = visualSimilarity != null && typeof videoId === "number" && videoId > 0;
  const preview = useQuery({
    queryKey: ["visual-similarity", "video", videoId, "has-embeddings"],
    queryFn: () => visualSimilarity!.videoHasEmbeddings(videoId!),
    enabled,
    retry: false,
  });
  // Version-skew safety net: an older AI.Visual build won't have the has-embeddings endpoint (the call
  // 404s). Fall back to the previous 1-item similarity probe so the tab still appears.
  const legacy = useQuery({
    queryKey: ["visual-similarity", "video", videoId, "availability-fallback"],
    queryFn: () => visualSimilarity!.similarVideosForVideo(videoId!, { perPage: 1 }),
    enabled: enabled && preview.isError,
    retry: false,
  });

  if (!enabled) return { available: false, loading: false };
  if (preview.data) return { available: preview.data.hasEmbeddings, loading: false };
  if (preview.isError && legacy.data) return { available: legacy.data.items.length > 0, loading: false };
  if (preview.isError && legacy.isError) return { available: false, loading: false };
  return { available: false, loading: true };
}

export function useVideoVisualSimilarityAvailable(videoId?: number) {
  return useVideoVisualSimilarityAvailability(videoId).available;
}

export function useImageVisualSimilarityAvailable(imageId?: number) {
  const visualSimilarity = useVisualSimilarityApi();
  const enabled = visualSimilarity != null && typeof imageId === "number" && imageId > 0;
  const preview = useQuery({
    queryKey: ["visual-similarity", "image", imageId, "has-embeddings"],
    queryFn: () => visualSimilarity!.imageHasEmbeddings(imageId!),
    enabled,
    retry: false,
  });
  const legacy = useQuery({
    queryKey: ["visual-similarity", "image", imageId, "availability-fallback"],
    queryFn: () => visualSimilarity!.similarImagesForImage(imageId!, { perPage: 1 }),
    enabled: enabled && preview.isError,
    retry: false,
  });

  if (!enabled) return false;
  if (preview.data) return preview.data.hasEmbeddings;
  if (preview.isError) return (legacy.data?.items.length ?? 0) > 0;
  return false;
}

export function VideoVisualSimilarityPanel({ videoId, onNavigate }: PanelProps & { videoId: number }) {
  useManualContext(["panel:visual-similarity", "feature:visual-similarity"]);
  const visualSimilarity = useVisualSimilarityApi();
  const [kind, setKind] = usePersistedKind("videos");
  const [perPage, setPerPage] = usePersistedPerPage();

  const similarVideos = useQuery({
    queryKey: ["visual-similarity", "video", videoId, "similar-videos", perPage],
    queryFn: () => visualSimilarity!.similarVideosForVideo(videoId, { perPage }),
    enabled: visualSimilarity != null && kind === "videos",
    retry: false,
  });
  const similarImages = useQuery({
    queryKey: ["visual-similarity", "video", videoId, "similar-images", perPage],
    queryFn: () => visualSimilarity!.similarImagesForVideo(videoId, { perPage }),
    enabled: visualSimilarity != null && kind === "images",
    retry: false,
  });

  if (!visualSimilarity) {
    return <UnavailablePanel message="No visual embedding provider is available." />;
  }

  return (
    <div className="space-y-4">
      <SimilarityToolbar kind={kind} onKind={setKind} perPage={perPage} onPerPage={setPerPage} />
      {kind === "videos" ? (
        <SimilarVideoSection
          items={similarVideos.data?.items ?? []}
          loading={similarVideos.isLoading}
          error={similarVideos.isError}
          onNavigate={onNavigate}
        />
      ) : (
        <SimilarImageSection
          items={similarImages.data?.items ?? []}
          loading={similarImages.isLoading}
          error={similarImages.isError}
          onNavigate={onNavigate}
        />
      )}
    </div>
  );
}

export function ImageVisualSimilarityPanel({ imageId, onNavigate }: PanelProps & { imageId: number }) {
  useManualContext(["panel:visual-similarity", "feature:visual-similarity"]);
  const visualSimilarity = useVisualSimilarityApi();
  const [kind, setKind] = usePersistedKind("videos");
  const [perPage, setPerPage] = usePersistedPerPage();

  const similarVideos = useQuery({
    queryKey: ["visual-similarity", "image", imageId, "similar-videos", perPage],
    queryFn: () => visualSimilarity!.similarVideosForImage(imageId, { perPage }),
    enabled: visualSimilarity != null && kind === "videos",
    retry: false,
  });
  const similarImages = useQuery({
    queryKey: ["visual-similarity", "image", imageId, "similar-images", perPage],
    queryFn: () => visualSimilarity!.similarImagesForImage(imageId, { perPage }),
    enabled: visualSimilarity != null && kind === "images",
    retry: false,
  });

  if (!visualSimilarity) {
    return <UnavailablePanel message="No visual embedding provider is available." />;
  }

  return (
    <div className="space-y-4">
      <SimilarityToolbar kind={kind} onKind={setKind} perPage={perPage} onPerPage={setPerPage} />
      {kind === "videos" ? (
        <SimilarVideoSection
          items={similarVideos.data?.items ?? []}
          loading={similarVideos.isLoading}
          error={similarVideos.isError}
          onNavigate={onNavigate}
        />
      ) : (
        <SimilarImageSection
          items={similarImages.data?.items ?? []}
          loading={similarImages.isLoading}
          error={similarImages.isError}
          onNavigate={onNavigate}
        />
      )}
    </div>
  );
}

type SegmentSimilarityInterval = { startSec: number; endSec?: number };

export function useSegmentVisualSimilarityAvailable({
  videoId,
  startSec,
  endSec,
  intervals,
}: {
  videoId?: number;
  startSec?: number;
  endSec?: number;
  intervals?: SegmentSimilarityInterval[];
}) {
  const visualSimilarity = useVisualSimilarityApi();
  const queryIntervals = normalizeIntervals(intervals, startSec, endSec);
  const intervalKey = queryIntervals.map((interval) => `${interval.startSec}:${interval.endSec ?? ""}`).join("|");
  const preview = useQuery({
    queryKey: ["visual-similarity", "video", videoId, "segment-similar-videos", "preview", intervalKey],
    queryFn: () =>
      visualSimilarity!.similarVideosForVideoSegment(videoId!, {
        intervals: queryIntervals,
        perPage: AVAILABILITY_PER_PAGE,
      }),
    enabled: visualSimilarity != null && typeof videoId === "number" && videoId > 0 && queryIntervals.length > 0,
    retry: false,
  });

  return visualSimilarity != null && (preview.data?.items.length ?? 0) > 0;
}

export function SegmentVisualSimilarityPanel({
  videoId,
  startSec,
  endSec,
  intervals,
  onNavigate,
}: PanelProps & { videoId: number; startSec?: number; endSec?: number; intervals?: SegmentSimilarityInterval[] }) {
  useManualContext(["panel:visual-similarity", "feature:visual-similarity"]);
  const visualSimilarity = useVisualSimilarityApi();
  const [perPage, setPerPage] = usePersistedPerPage();
  const queryIntervals = normalizeIntervals(intervals, startSec, endSec);
  const intervalKey = queryIntervals.map((interval) => `${interval.startSec}:${interval.endSec ?? ""}`).join("|");
  const similarVideos = useQuery({
    queryKey: ["visual-similarity", "video", videoId, "segment-similar-videos", intervalKey, perPage],
    queryFn: () => visualSimilarity!.similarVideosForVideoSegment(videoId, { intervals: queryIntervals, perPage }),
    retry: false,
    enabled: visualSimilarity != null && queryIntervals.length > 0,
  });

  if (!visualSimilarity) {
    return <UnavailablePanel message="No visual embedding provider is available." />;
  }

  if (similarVideos.isError) {
    return <UnavailablePanel message="Visual similarity could not be loaded." />;
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <SimilarityHeader />
        <PerPageSelect perPage={perPage} onPerPage={setPerPage} />
      </div>
      <SimilarVideoSection
        items={similarVideos.data?.items ?? []}
        loading={similarVideos.isLoading}
        error={similarVideos.isError}
        onNavigate={onNavigate}
      />
    </div>
  );
}

function SimilarityToolbar({
  kind,
  onKind,
  perPage,
  onPerPage,
}: {
  kind: SimilarKind;
  onKind: (kind: SimilarKind) => void;
  perPage: number;
  onPerPage: (perPage: number) => void;
}) {
  return (
    <div className="flex items-center justify-between gap-3">
      <SimilarityHeader />
      <div className="flex items-center gap-2">
        <div className="inline-flex overflow-hidden rounded-md border border-border text-xs">
          <KindToggleButton
            active={kind === "videos"}
            onClick={() => onKind("videos")}
            icon={<Film className="h-3.5 w-3.5" />}
            label="Videos"
          />
          <KindToggleButton
            active={kind === "images"}
            onClick={() => onKind("images")}
            icon={<ImageIcon className="h-3.5 w-3.5" />}
            label="Images"
          />
        </div>
        <PerPageSelect perPage={perPage} onPerPage={onPerPage} />
      </div>
    </div>
  );
}

function KindToggleButton({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean;
  onClick: () => void;
  icon: ReactNode;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex items-center gap-1.5 px-2.5 py-1 font-medium transition-colors ${active ? "bg-accent text-white" : "bg-surface/40 text-secondary hover:text-foreground"}`}
      aria-pressed={active}
    >
      {icon}
      {label}
    </button>
  );
}

function PerPageSelect({ perPage, onPerPage }: { perPage: number; onPerPage: (perPage: number) => void }) {
  const options = SIMILAR_PER_PAGE_OPTIONS.includes(perPage)
    ? SIMILAR_PER_PAGE_OPTIONS
    : [...SIMILAR_PER_PAGE_OPTIONS, perPage].sort((left, right) => left - right);
  return (
    <select
      value={perPage}
      onChange={(event) => onPerPage(Number(event.target.value))}
      className="rounded-md border border-border bg-surface/40 px-2 py-1 text-xs text-foreground"
      title="Number of items to show"
      aria-label="Number of items to show"
    >
      {options.map((option) => (
        <option key={option} value={option}>
          {option}
        </option>
      ))}
    </select>
  );
}

function SimilarityHeader() {
  return (
    <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-muted">
      <Sparkles className="h-3.5 w-3.5" />
      Visual Similarity
    </div>
  );
}

function SimilarVideoSection({
  items,
  loading,
  error,
  onNavigate,
}: {
  items: VisualSimilarVideo[];
  loading: boolean;
  error: boolean;
  onNavigate: (route: any) => void;
}) {
  const videoIds = useMemo(() => items.map((item) => item.video.id), [items]);
  const { engagementById: videoEngagement } = useEntityEngagementBatch("video", videoIds);

  if (error) {
    return <UnavailablePanel message="Visual similarity could not be loaded." />;
  }

  return (
    <section>
      {loading ? (
        <LoadingPanel />
      ) : items.length === 0 ? (
        <EmptyPanel icon={<Film className="h-10 w-10" />} message="No visual matches yet." />
      ) : (
        <EntityCardGrid minCardWidth="240px" gapClassName="gap-4" className="mt-1">
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

function SimilarImageSection({
  items,
  loading,
  error,
  onNavigate,
}: {
  items: VisualSimilarImage[];
  loading: boolean;
  error: boolean;
  onNavigate: (route: any) => void;
}) {
  const imageIds = useMemo(() => items.map((item) => item.image.id), [items]);
  const { engagementById: imageEngagement } = useEntityEngagementBatch("image", imageIds);

  if (error) {
    return <UnavailablePanel message="Visual similarity could not be loaded." />;
  }

  return (
    <section>
      {loading ? (
        <LoadingPanel />
      ) : items.length === 0 ? (
        <EmptyPanel icon={<ImageIcon className="h-10 w-10" />} message="No visual matches yet." />
      ) : (
        <EntityCardGrid minCardWidth="190px" gapClassName="gap-4" className="mt-1">
          {items.map((item) => (
            <SimilarImageCard
              key={item.image.id}
              item={item}
              engagement={imageEngagement.get(item.image.id)}
              onNavigate={onNavigate}
            />
          ))}
        </EntityCardGrid>
      )}
    </section>
  );
}

function SimilarVideoCard({
  item,
  engagement,
  onNavigate,
}: {
  item: VisualSimilarVideo;
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

function SimilarImageCard({
  item,
  engagement,
  onNavigate,
}: {
  item: VisualSimilarImage;
  engagement?: EntityEngagement;
  onNavigate: (route: any) => void;
}) {
  const image = item.image;

  return (
    <div className="relative h-full">
      <ImageTile
        image={image}
        engagement={engagement}
        onClick={() => onNavigate({ page: "image", id: image.id })}
        onNavigate={onNavigate}
      />
      <SimilarityOverlay distance={item.distance} />
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
    <div className="mt-1 rounded-xl border border-border bg-surface/40 px-4 py-8 text-center text-sm text-secondary">
      Loading...
    </div>
  );
}

function EmptyPanel({ icon, message }: { icon: ReactNode; message: string }) {
  return (
    <div className="mt-1 flex flex-col items-center justify-center rounded-xl border border-dashed border-border bg-surface/40 px-4 py-8 text-center text-sm text-secondary">
      <div className="mb-3 text-muted opacity-60">{icon}</div>
      <p>{message}</p>
    </div>
  );
}

function UnavailablePanel({ message }: { message: string }) {
  return <EmptyPanel icon={<Sparkles className="h-10 w-10" />} message={message} />;
}

function getVideoMeta(item: VisualSimilarVideo) {
  if (item.sectionIndex > 0 && item.startSec != null) {
    return item.endSec != null
      ? `${formatDuration(item.startSec)} - ${formatDuration(item.endSec)}`
      : formatDuration(item.startSec);
  }

  return undefined;
}

function normalizeIntervals(
  intervals: SegmentSimilarityInterval[] | undefined,
  startSec: number | undefined,
  endSec: number | undefined,
) {
  const source = intervals && intervals.length > 0 ? intervals : startSec != null ? [{ startSec, endSec }] : [];
  return source
    .filter((interval) => Number.isFinite(interval.startSec))
    .map((interval) => ({
      startSec: interval.startSec,
      endSec: typeof interval.endSec === "number" && Number.isFinite(interval.endSec) ? interval.endSec : undefined,
    }));
}
