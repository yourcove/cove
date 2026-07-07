import { useQueries, useQuery, useMutation, useQueryClient, keepPreviousData } from "@tanstack/react-query";
import { faces, videos, segmentDisplayProfiles, tagApplications, tags, entityImages, metadata, fileOps, galleries } from "../api/client";
import { formatDuration, formatFileSize, formatDate, TagBadge, getResolutionLabel, CustomFieldsDisplay, CustomFieldsEditor, FieldProvenanceHover, resolveTagProvenance } from "../components/shared";
import { 
  Plus, Trash2, Search, Eye, EyeOff, ArrowLeft, ThumbsUp,
  Check, ChevronLeft, ChevronRight, ChevronDown, MoreVertical,
  Gauge, Clapperboard, FolderOpen, Layers, Clock, List,
  RefreshCw, Camera, Image, Merge, ExternalLink, Download, X, Sparkles, Volume2, Filter,
  UserX, Loader2,
} from "lucide-react";
import { useState, useRef, useEffect, useCallback, Fragment, useMemo, lazy, Suspense } from "react";
import { ConfirmDialog } from "../components/ConfirmDialog";
import type { Detection, Face, PerformerSummary, ResolvedSpan, Video, VideoUpdate, Segment, TagApplication, TagProvenance } from "../api/types";
import { ExtensionSlot } from "../router/RouteRegistry";
import { AspectRatingsPanel } from "../components/AspectRatingsPanel";
import { InteractiveRating } from "../components/Rating";
import { VideoSegmentsPanel } from "../components/VideoSegmentsPanel";
import {
  type SegmentFilterState,
  type SegmentFilterContext,
  EMPTY_SEGMENT_FILTER,
  matchesSegmentFilter,
  isSegmentFilterActive,
} from "../components/segmentFilter";
import { useVideoQueue, type VideoQueueItem } from "../state/VideoQueueContext";
import { useAppConfig } from "../state/AppConfigContext";
import { useExtensions } from "../extensions/ExtensionLoader";
import { createRouteLinkProps } from "../components/cardNavigation";
import { StringListEditor } from "../components/StringListEditor";
import { StudioSelector } from "../components/StudioSelector";
import { ExtensionEntityActions } from "../components/ExtensionEntityActions";
import { ExtensionErrorBoundary } from "../components/ExtensionErrorBoundary";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { RemoteIdsEditor, normalizeRemoteIds, type RemoteIdValue } from "../components/RemoteIdsEditor";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity, filterItemsByPermission, hasAnyPermission } from "../auth/visibility";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { VideoPlayer } from "../components/VideoPlayer";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { MediaDetailLayout } from "../components/MediaDetailLayout/MediaDetailLayout";
import { CoverImageDialog } from "../components/CoverImageDialog";
import { PerformerTile, EntityRefBadge } from "../components/EntityCards";
import { trackInteraction } from "../utils/interactionTracking";
import { getEditableTagIds, getLockedTagIds, mergeTagIds } from "../utils/tags";
import { VideoVisualSimilarityPanel, useVideoVisualSimilarityAvailable } from "../components/VisualSimilarityPanel";
import { VideoAudioSimilarityPanel, useVideoAudioSimilarityAvailable } from "../components/AudioSimilarityPanel";
import { EntityReferenceMultiSelector, EntityReferenceSelector, EntityReferenceValue } from "../components/EntityReferenceSelector";
import { useDocumentTitle } from "../hooks/useDocumentTitle";

const GenerateDialog = lazy(() => import("../components/GenerateDialog").then((module) => ({ default: module.GenerateDialog })));
const DetailMergeDialog = lazy(() => import("../components/DetailMergeDialog").then((module) => ({ default: module.DetailMergeDialog })));
const IdentifyDialog = lazy(() => import("../components/IdentifyDialog").then((module) => ({ default: module.IdentifyDialog })));
const VideoDownloadDialog = lazy(() => import("../components/VideoDownloadDialog").then((module) => ({ default: module.VideoDownloadDialog })));
const VideoMetadataTaggerDialog = lazy(() => import("../components/MetadataTaggerDialog").then((module) => ({ default: module.VideoMetadataTaggerDialog })));

interface Props {
  id: number;
  initialSeekTo?: number;
  onNavigate: (r: any) => void;
}

// localStorage-backed boolean flag with safe SSR fallback.
function usePersistedFlag(key: string, defaultValue: boolean): [boolean, (next: boolean | ((prev: boolean) => boolean)) => void] {
  const [value, setValue] = useState<boolean>(() => {
    if (typeof window === "undefined") return defaultValue;
    try {
      const raw = window.localStorage.getItem(key);
      if (raw === "true") return true;
      if (raw === "false") return false;
    } catch { /* ignore */ }
    return defaultValue;
  });
  const set = useCallback((next: boolean | ((prev: boolean) => boolean)) => {
    setValue((prev) => {
      const resolved = typeof next === "function" ? (next as (p: boolean) => boolean)(prev) : next;
      try { window.localStorage.setItem(key, resolved ? "true" : "false"); } catch { /* ignore */ }
      return resolved;
    });
  }, [key]);
  return [value, set];
}

function VideoQueuePanel({
  items,
  currentId,
  autoplay,
  onNavigate,
  onClose,
  onClear,
  onToggleAutoplay,
}: {
  items: VideoQueueItem[];
  currentId: number;
  autoplay: boolean;
  onNavigate: (videoId: number, index: number) => void;
  onClose: () => void;
  onClear: () => void;
  onToggleAutoplay: () => void;
}) {
  return (
    <div className="max-h-72 flex-shrink-0 overflow-hidden border-t border-border bg-[#161616] text-white shadow-[0_-8px_24px_rgba(0,0,0,0.28)]">
      <div className="flex items-center justify-between border-b border-white/10 px-3 py-2">
        <div>
          <div className="text-sm font-semibold">Play Selected Queue</div>
          <div className="text-xs text-white/50">{items.length} video{items.length === 1 ? "" : "s"}</div>
        </div>
        <div className="flex items-center gap-1">
          <button
            type="button"
            onClick={onToggleAutoplay}
            className={["rounded px-2 py-1 text-xs", autoplay ? "bg-accent/20 text-accent" : "text-white/60 hover:bg-white/10 hover:text-white"].join(" ")}
          >
            Auto
          </button>
          <button type="button" onClick={onClear} className="rounded px-2 py-1 text-xs text-white/60 hover:bg-white/10 hover:text-white">
            Clear
          </button>
          <button type="button" onClick={onClose} className="rounded p-1 text-white/60 hover:bg-white/10 hover:text-white" aria-label="Close queue panel">
            <X className="h-4 w-4" />
          </button>
        </div>
      </div>
      <div className="max-h-56 overflow-y-auto p-2">
        <div className="grid gap-1 sm:grid-cols-2 xl:grid-cols-3">
          {items.map((item, index) => {
            const active = item.id === currentId;
            return (
              <button
                key={`${item.id}-${index}`}
                type="button"
                onClick={() => onNavigate(item.id, index)}
                className={["flex min-w-0 items-center gap-2 rounded border p-1.5 text-left transition", active ? "border-accent bg-accent/15" : "border-white/10 bg-white/[0.03] hover:border-accent/50 hover:bg-white/[0.06]"].join(" ")}
              >
                {item.imagePath ? (
                  <img src={item.imagePath} alt="" className="h-10 w-16 shrink-0 rounded object-cover bg-black" loading="lazy" />
                ) : (
                  <div className="flex h-10 w-16 shrink-0 items-center justify-center rounded bg-black/40 text-white/35">
                    <Clapperboard className="h-4 w-4" />
                  </div>
                )}
                <div className="min-w-0 flex-1">
                  <div className="truncate text-xs font-medium text-white">{item.title || `Video ${item.id}`}</div>
                  <div className="mt-0.5 truncate text-[10px] text-white/45">
                    {index + 1}{active ? " · Now playing" : item.subtitle ? ` · ${item.subtitle}` : ""}
                  </div>
                </div>
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}

type TabKey = "details" | "segments" | "filters" | "file-info" | "edit" | "history" | string;

export function VideoDetailPage({ id, initialSeekTo, onNavigate }: Props) {
  const { data: video, isLoading } = useQuery({
    queryKey: ["video", id],
    queryFn: () => videos.get(id),
    // Keep the previous video's data on screen while the next one loads. Advancing in a queue
    // otherwise drops to the page-level loading skeleton, which unmounts the whole player subtree —
    // exiting fullscreen on every "next" and flashing a blank skeleton between items. With the player
    // kept mounted it handles the id change in place (it already resets per-video state and reloads
    // the source on videoId/streamUrl change). The player block below reads id/stream/poster from the
    // loaded `video` so the (id, file, stream) triple stays self-consistent during the swap.
    placeholderData: keepPreviousData,
  });
  const { hasPermission, user } = useAuth();
  const { config } = useAppConfig();
  const { queue, currentId: queueCurrentId, hasPrev, hasNext, prevId, nextId, currentPosition, queueLength, queueItems, goToIndex, clearQueue, autoplay: queueAutoplay, toggleAutoplay } = useVideoQueue();
  const { getTabsForPage, resolveComponent: resolveExtComponent, getFeature } = useExtensions();
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [showGenerate, setShowGenerate] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [showQueuePanel, setShowQueuePanel] = useState(false);
  const [showMerge, setShowMerge] = useState(false);
  const [showIdentify, setShowIdentify] = useState(false);
  const [showScrapeDialog, setShowScrapeDialog] = useState(false);
  const [showDownloadDialog, setShowDownloadDialog] = useState(false);
  const [activeTab, setActiveTab] = useState<TabKey>("details");
  const [selectedProfileId, setSelectedProfileId] = useState<number | undefined>(undefined);
  const [segmentFilter, setSegmentFilter] = useState<SegmentFilterState>(EMPTY_SEGMENT_FILTER);
  const queryClient = useQueryClient();
  const { backLabel, goBack } = useBackNavigation({ page: "videos" }, onNavigate);
  const canWriteVideo = canWriteEntity("video", hasPermission);
  const canReadVideo = canReadEntity("video", hasPermission);
  const canDeleteVideo = canDeleteEntity("video", hasPermission);
  const canReadGroups = canReadEntity("group", hasPermission);
  const canReadGalleries = canReadEntity("gallery", hasPermission);
  const canReadFaces = canReadEntity("face", hasPermission);
  const canWriteFaces = canWriteEntity("face", hasPermission);
  const canReadSegments = canReadEntity("segment", hasPermission);
  const canWriteSegments = hasPermission("segments.write");
  const canWriteTags = hasPermission("tags.write");
  const canReadFiles = hasPermission("files.read");
  const canRunJobs = hasPermission("jobs.run");
  const canLibraryScan = hasPermission("library.scan");
  const canIdentify = hasPermission("library.identify");
  const canScrapeVideo = hasAnyPermission(hasPermission, ["videos.scrape", "videos.write"]);
  const canEngageVideo = canReadVideo && (user?.kind === "user" || user?.kind === "system");
  const trackingEnabled = user?.uiPreferences?.tracking?.enabled ?? true;
  const trackPlaybackActivity = canEngageVideo && trackingEnabled;
  const canGenerateVideo = canRunJobs && canWriteVideo;
  const canIdentifyVideo = canIdentify && canWriteVideo;
  const canDownloadVideo = canRunJobs && canWriteVideo;
  const seekRef = useRef<((time: number) => void) | null>(null);
  const trackedPageVisitVideoIdRef = useRef<number | null>(null);
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const [videoTime, setVideoTime] = useState(0);
  const [coverOpen, setCoverOpen] = useState(false);
  const [videoFilters, setVideoFilters] = useState({ brightness: 100, contrast: 100, gamma: 100, saturation: 100, hue: 0 });
  const {
    engagement: videoEngagement,
    favorite: videoFavorite,
    rating: videoRating,
    setFavorite: setVideoFavorite,
    setRating: setVideoRating,
    favoritePending: videoFavoritePending,
  } = useEntityEngagement("video", id, {
    enabled: !!video && canReadVideo,
    fallbackFavorite: false,
    fallbackRating: undefined,
  });
  const videoPlayCount = videoEngagement?.playCount ?? 0;
  const videoPlayDuration = videoEngagement?.playDuration ?? 0;
  const videoResumeTime = videoEngagement?.resumeTime;
  const videoLikeCount = videoEngagement?.likeCount ?? 0;
  const videoDerivedLikeCount = videoEngagement?.derivedLikeCount ?? 0;
  const videoPageVisitCount = videoEngagement?.pageVisitCount ?? 0;
  const effectiveVideoResumeTime = typeof videoResumeTime === "number" && Number.isFinite(videoResumeTime) && videoResumeTime > 0
    ? videoResumeTime
    : undefined;
  const effectiveResumeTime = initialSeekTo ?? effectiveVideoResumeTime;

  useEffect(() => {
    const videoId = video?.id;
    if (!videoId || !trackPlaybackActivity) return;
    if (trackedPageVisitVideoIdRef.current === videoId) return;
    trackedPageVisitVideoIdRef.current = videoId;
    trackInteraction({ hostType: "video", hostId: videoId, kind: "pageVisit" });
    queryClient.invalidateQueries({ queryKey: ["engagement", "video", videoId] });
  }, [queryClient, video?.id, trackPlaybackActivity]);

  useDocumentTitle(video ? video.title || video.files?.[0]?.basename || `Video ${id}` : null);

  // Disable background animations on video player pages for GPU performance
  // Controlled by gradient > "Pause on Video Player" setting (default: on)
  useEffect(() => {
    try {
      const opts = JSON.parse(localStorage.getItem("cove-style-options") ?? "{}");
      if (opts.gradient?.videopause === "off") return;
    } catch { /* default to pausing */ }
    document.body.classList.add("has-video-player");
    return () => document.body.classList.remove("has-video-player");
  }, []);

  // Close ops menu on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(e.target as Node)) {
        setShowOpsMenu(false);
      }
    };
    if (showOpsMenu) document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [showOpsMenu]);

  const videoStyle = useMemo(() => {
    const { brightness, contrast, saturation, hue } = videoFilters;
    return { filter: `brightness(${brightness}%) contrast(${contrast}%) saturate(${saturation}%) hue-rotate(${hue}deg)` };
  }, [videoFilters]);

  const deleteMut = useMutation({
    mutationFn: (deleteFile?: boolean) => videos.delete(id, deleteFile),
    onSuccess: () => { 
      queryClient.invalidateQueries({ queryKey: ["videos"] }); 
      goBack(); 
    },
  });

  const incrementLikeMut = useMutation({
    mutationFn: () => videos.incrementLike(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["video", id] });
      queryClient.invalidateQueries({ queryKey: ["engagement", "video", id] });
    },
  });

  const updateMut = useMutation({
    mutationFn: (data: { organized?: boolean; rating?: number }) => videos.update(id, data),
    onSuccess: (updatedVideo) => {
      queryClient.setQueryData<Video>(["video", id], updatedVideo);
    },
  });

  const invalidateVideoCover = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: ["video", id] });
    queryClient.invalidateQueries({ queryKey: ["videos"] });
  }, [id, queryClient]);

  const setCoverFromCurrentFrameMut = useMutation({
    mutationFn: (atSeconds?: number) => videos.setCoverFromFrame(id, atSeconds),
    onSuccess: invalidateVideoCover,
  });

  const coverActionPending = setCoverFromCurrentFrameMut.isPending;

  const handleSetCoverFromCurrentFrame = () => {
    setCoverFromCurrentFrameMut.mutate(videoTime);
  };

  const { data: segments = [], isLoading: segmentsLoading } = useQuery({
    queryKey: ["video", id, "segments"],
    queryFn: () => videos.segments.list(id),
    enabled: canReadSegments,
  });

  const { data: displayProfiles = [] } = useQuery({
    queryKey: ["segment-display-profiles"],
    queryFn: () => segmentDisplayProfiles.list(),
    enabled: canReadSegments,
  });

  const { data: resolvedSpansResponse, isLoading: resolvedSpansLoading } = useQuery({
    queryKey: ["video", id, "resolved-spans", selectedProfileId],
    queryFn: () => videos.segments.spans(id, selectedProfileId),
    enabled: canReadSegments,
  });

  const { data: detections = [], isLoading: detectionsLoading } = useQuery({
    queryKey: ["video", id, "detections"],
    queryFn: () => videos.detections.list(id),
    enabled: canReadSegments,
  });

  const videoFaceIds = useMemo(() => {
    const ids = new Set<number>();
    for (const detection of detections) {
      if (detection.refId != null && detection.refKind?.toLowerCase() === "face") {
        ids.add(detection.refId);
      }
    }

    for (const segment of segments) {
      if (segment.refId != null && isFaceTimelineSegment(segment)) {
        ids.add(Number(segment.refId));
      }
    }

    return Array.from(ids);
  }, [detections, segments]);

  const videoFaceQueries = useQueries({
    queries: videoFaceIds.map((faceId) => ({
      queryKey: ["face", faceId],
      queryFn: () => faces.get(faceId),
      enabled: canReadFaces && canReadSegments,
    })),
  });

  const videoFaces = useMemo(() => {
    const countsByFaceId = new Map<number, number>();
    for (const detection of detections) {
      if (detection.refId != null && detection.refKind?.toLowerCase() === "face") {
        countsByFaceId.set(detection.refId, (countsByFaceId.get(detection.refId) ?? 0) + 1);
      }
    }

    return videoFaceQueries
      .map((query) => query.data)
      .filter((face): face is Face => face != null)
      .map((face) => ({ face, detectionCount: countsByFaceId.get(face.id) ?? 0 }))
      .sort((left, right) => right.detectionCount - left.detectionCount || left.face.id - right.face.id);
  }, [detections, videoFaceQueries]);

  const rescanMut = useMutation({
    mutationFn: () => videos.rescan(id),
  });

  // Marking a face not-present re-homes the wrong-person occurrences off this face (see the AI.Faces
  // extension). Refresh this video's faces/detections/segments and any cached face data afterward.
  const markFaceNotPresentMut = useMutation({
    mutationFn: (faceId: number) => faces.markNotPresent(faceId, { hostType: "video", hostId: Number(id) }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["video", id, "detections"] });
      queryClient.invalidateQueries({ queryKey: ["video", id, "segments"] });
      queryClient.invalidateQueries({ queryKey: ["video", id] });
      queryClient.invalidateQueries({ queryKey: ["face"] });
    },
  });

  // "This detection is wrong": drop the AI's host-level applications for one tag so the wrongly-derived
  // chip falls off this video. Refresh the video (effective tags) and tag lists/counts.
  const reportIncorrectTagMut = useMutation({
    mutationFn: (tagId: number) => tagApplications.reportIncorrect("video", Number(id), tagId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["video", id] });
      queryClient.invalidateQueries({ queryKey: ["tag"] });
    },
  });
  // The derived tag the user is reporting as wrong, if any. Owned here (not per-tab) so the Details
  // chip and the Edit-tab selector share one confirm dialog and one mutation.
  const [reportTag, setReportTag] = useState<any | null>(null);
  const requestReportTag = canWriteTags ? (tag: any) => setReportTag(tag) : undefined;

  const resolvedSpans = resolvedSpansResponse?.spans ?? [];
  const activeProfileId = selectedProfileId ?? resolvedSpansResponse?.profileId;
  const activeProfileName = displayProfiles.find((profile) => profile.id === activeProfileId)?.name ?? "Resolved";

  // Shared segment filter context — drives both the segments sidebar and the player swimlanes.
  const segmentRawById = useMemo(() => new Map(segments.map((segment) => [segment.id, segment])), [segments]);
  // When a tag-group filter is active, fetch that group's member tags so segments tagged within
  // the group resolve to it (segments carry a tag id, not a group id).
  const selectedTagGroupIds = segmentFilter.tagGroupIds;
  const { data: tagGroupMemberTags } = useQuery({
    queryKey: ["segment-filter-group-tags", selectedTagGroupIds],
    queryFn: () => tags.findFiltered({
      findFilter: { page: 1, perPage: 1000, sort: "name", direction: "asc" },
      objectFilter: { tagGroupsCriterion: { value: selectedTagGroupIds, modifier: "INCLUDES" } },
    }),
    enabled: selectedTagGroupIds.length > 0,
  });
  const tagIdToGroupId = useMemo(() => {
    const map = new Map<number, number>();
    for (const tag of video?.tags ?? []) {
      if (tag.tagGroupId != null) map.set(tag.id, tag.tagGroupId);
    }
    for (const tag of tagGroupMemberTags?.items ?? []) {
      if (tag.tagGroupId != null) map.set(tag.id, tag.tagGroupId);
    }
    return map;
  }, [video?.tags, tagGroupMemberTags]);
  const segmentFilterContext = useMemo<SegmentFilterContext>(() => ({ rawSegmentsById: segmentRawById, tagIdToGroupId }), [segmentRawById, tagIdToGroupId]);

  useEffect(() => { setSegmentFilter(EMPTY_SEGMENT_FILTER); }, [id]);
  const hasVisualSimilarity = useVideoVisualSimilarityAvailable(id);
  const hasAudioSimilarity = useVideoAudioSimilarityAvailable(id);
  const videoExtTabs = useMemo(() => getTabsForPage("video"), [getTabsForPage]);

  const tabs = filterItemsByPermission([
    { key: "details", label: "Details" },
    { key: "segments", label: `Segments${segments.length ? ` (${segments.length})` : ""}` },
    ...(hasVisualSimilarity ? [{ key: "similar", label: "Similar", icon: <Sparkles className="h-4 w-4" /> }] : []),
    ...(hasAudioSimilarity ? [{ key: "audio-similar", label: "Audio Similar", icon: <Volume2 className="h-4 w-4" /> }] : []),
    { key: "filters", label: "Filters" },
    { key: "file-info", label: `File Info${video?.files.length && video.files.length > 1 ? ` (${video.files.length})` : ""}` },
    { key: "history", label: "History" },
    ...videoExtTabs.map((t) => ({ key: `ext:${t.key}` as TabKey, label: t.label, manualContexts: t.manualContexts })),
    { key: "edit", label: "Edit" },
  ], {
    segments: "segments.read",
    "file-info": "files.read",
    edit: "videos.write",
  }, hasPermission);

  useEffect(() => {
    if (!tabs.some((tab) => tab.key === activeTab)) {
      setActiveTab("details");
    }
  }, [activeTab, tabs]);

  useEffect(() => {
    if (!queue || queueCurrentId === id) {
      return;
    }

    const nextIndex = queue.videoIds.indexOf(id);
    if (nextIndex >= 0) {
      goToIndex(nextIndex);
    }
  }, [goToIndex, id, queue, queueCurrentId]);

  const queueSyncedToVideo = queueCurrentId === id;

  const videoKeyboardShortcuts = useMemo(() => [
    { key: "a", description: "Open details tab", handler: () => setActiveTab("details") },
    { key: "e", description: "Open edit tab", handler: () => canWriteVideo && setActiveTab("edit") },
    { key: "s", description: "Open segments tab", handler: () => canReadSegments && setActiveTab("segments") },
    { key: "i", description: "Open file info tab", handler: () => canReadFiles && setActiveTab("file-info") },
    { key: "h", description: "Open history tab", handler: () => setActiveTab("history") },
    { key: "o", description: "Toggle favorite", handler: () => video && canEngageVideo && setVideoFavorite(!videoFavorite) },
    { key: "[", description: "Open previous video", handler: () => queueSyncedToVideo && hasPrev && prevId != null && onNavigate({ page: "video", id: prevId }) },
    { key: "]", description: "Open next video", handler: () => queueSyncedToVideo && hasNext && nextId != null && onNavigate({ page: "video", id: nextId }) },
  ], [canEngageVideo, canReadFiles, canReadSegments, canWriteVideo, hasNext, hasPrev, nextId, onNavigate, prevId, queueSyncedToVideo, video, videoFavorite, setVideoFavorite]);

  if (isLoading) {
    return (
      <div className="-mx-6 -mt-5 -mb-5 px-6 py-6">
        <DetailSkeleton />
      </div>
    );
  }

  if (!video) return <div className="text-center text-secondary py-16">Video not found</div>;

  const file = video.files[0];
  // Use video.id (the loaded record) rather than the route id so the stream URL stays consistent with
  // `file` during a keepPreviousData swap, where the route id is already the next video but the data
  // (and format) is still the previous one for a frame.
  const streamUrl = videos.streamUrl(video.id);
  const resLabel = file ? getResolutionLabel(file.width, file.height) : null;

  const studioImageUrl = video.studioId ? entityImages.studioImageUrl(video.studioId) : null;
  const videoTitle = video.title || file?.basename || `Video ${video.id}`;

  const videoHeaderImage = studioImageUrl && video.studioId ? (
    <button
      type="button"
      onClick={() => onNavigate({ page: "studio", id: video.studioId })}
      className="block"
      title={video.studioName || "Studio"}
    >
      <img
        src={studioImageUrl}
        alt={video.studioName || "Studio"}
        className="h-20 w-auto max-w-full object-contain"
        onError={(event) => { (event.target as HTMLImageElement).style.display = "none"; }}
      />
    </button>
  ) : null;

  const videoSubtitle = (
    <div className="flex flex-wrap items-start gap-4 text-sm text-secondary">
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        {video.date ? (
          <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="date">
            <span>
              {new Date(`${video.date}T00:00:00`).toLocaleDateString(undefined, {
                year: "numeric",
                month: "long",
                day: "numeric",
              })}
            </span>
          </FieldProvenanceHover>
        ) : null}

        <div className="flex flex-wrap items-center gap-2">
          {video.studioName && video.studioId ? (
            <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="studio">
              <button
                type="button"
                onClick={() => onNavigate({ page: "studio", id: video.studioId })}
                className="font-medium text-accent hover:underline"
              >
                {video.studioName}
              </button>
            </FieldProvenanceHover>
          ) : null}
          {file && file.frameRate > 0 ? <span>{file.frameRate.toFixed(0)} fps</span> : null}
          {file && resLabel ? <span className="font-semibold text-accent">{resLabel}</span> : null}
          {video.code ? <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="code"><span>Code {video.code}</span></FieldProvenanceHover> : null}
          {video.director ? (
            <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="director">
              <button
                type="button"
                onClick={() => onNavigate({ page: "videos", query: video.director })}
                className="hover:text-foreground"
              >
                Director {video.director}
              </button>
            </FieldProvenanceHover>
          ) : null}
        </div>
      </div>
    </div>
  );

  const videoActions = (
    <>
      {canWriteVideo ? (
        <button
          type="button"
          onClick={() => { if (!updateMut.isPending) updateMut.mutate({ organized: !video.organized }); }}
          disabled={updateMut.isPending}
          className={`inline-flex items-center justify-center rounded p-1 transition ${video.organized ? "bg-green-600 text-white" : "bg-card text-muted hover:text-foreground"} ${updateMut.isPending ? "cursor-not-allowed opacity-60" : ""}`}
          title={video.organized ? "Organized" : "Mark organized"}
        >
          <Check className="h-4 w-4" />
        </button>
      ) : video.organized ? (
        <span className="inline-flex items-center justify-center rounded bg-green-600 p-1 text-white" title="Organized">
          <Check className="h-4 w-4" />
        </span>
      ) : null}

      {file ? (
        <a
          href={streamUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
          title="Open in external player"
        >
          <ExternalLink className="h-4 w-4" />
        </a>
      ) : null}

      {queueLength > 1 ? (
        <button
          type="button"
          onClick={() => setShowQueuePanel((value) => !value)}
          className={["inline-flex items-center gap-1 rounded px-1.5 py-1 text-xs transition", showQueuePanel ? "bg-accent/15 text-accent" : "text-secondary hover:bg-card hover:text-foreground"].join(" ")}
          title="Show selected queue"
          aria-pressed={showQueuePanel}
        >
          <List className="h-4 w-4" />
          <span>{currentPosition}/{queueLength}</span>
        </button>
      ) : null}

      <div className="relative" ref={opsMenuRef}>
        <button
          type="button"
          onClick={() => setShowOpsMenu(!showOpsMenu)}
          className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
          title="Operations"
        >
          <MoreVertical className="h-4 w-4" />
        </button>
        <FloatingActionMenu open={showOpsMenu} anchorRef={opsMenuRef} onClose={() => setShowOpsMenu(false)} className="min-w-[220px] py-1">
            {!file && canDownloadVideo ? (
              <button onClick={() => { setShowDownloadDialog(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface"><Download className="h-3.5 w-3.5" /> Download Media…</button>
            ) : null}
            {file && canLibraryScan ? (
              <button onClick={() => { rescanMut.mutate(); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface"><RefreshCw className="h-3.5 w-3.5" /> Rescan</button>
            ) : null}
            {canScrapeVideo ? <button onClick={() => { setShowScrapeDialog(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface"><Search className="h-3.5 w-3.5" /> Scrape / Metadata…</button> : null}
            {canIdentifyVideo ? <button onClick={() => { setShowIdentify(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface"><Search className="h-3.5 w-3.5" /> Identify…</button> : null}
            {canGenerateVideo || canWriteVideo ? <div className="my-1 border-t border-border" /> : null}
            <ExtensionEntityActions entityType="video" entityId={video.id} renderMode="menu" onInvoked={() => setShowOpsMenu(false)} />
            {canGenerateVideo ? <button onClick={() => { setShowGenerate(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface"><Clapperboard className="h-3.5 w-3.5" /> Generate…</button> : null}
            {canWriteVideo ? <button onClick={() => { setCoverOpen(true); setShowOpsMenu(false); }} disabled={coverActionPending} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface disabled:opacity-60"><Image className="h-3.5 w-3.5" /> Set Cover…</button> : null}
            {canWriteVideo ? <div className="my-1 border-t border-border" /> : null}
            {canWriteVideo ? <button onClick={() => { setShowMerge(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface"><Merge className="h-3.5 w-3.5" /> Merge…</button> : null}
            {canDeleteVideo ? <div className="my-1 border-t border-border" /> : null}
            {canDeleteVideo ? <button onClick={() => { setConfirmDelete(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-red-400 hover:bg-surface"><Trash2 className="h-3.5 w-3.5" /> Delete</button> : null}
        </FloatingActionMenu>
      </div>

      <ExtensionSlot slot="video-detail-actions" context={{ video, onNavigate }} />
    </>
  );

  const activeTabContent = activeTab === "details" ? (
    <DetailsTab
      video={video}
      onNavigate={onNavigate}
      videoFaces={videoFaces}
      onMarkFaceNotPresent={canWriteFaces ? (faceId) => markFaceNotPresentMut.mutate(faceId) : undefined}
      markingFaceId={markFaceNotPresentMut.isPending ? (markFaceNotPresentMut.variables as number) : undefined}
      onRequestReportTag={requestReportTag}
    />
  ) : activeTab === "segments" ? (
    <VideoSegmentsPanel
      videoId={video.id}
      spans={resolvedSpans}
      rawSegments={segments}
      loading={resolvedSpansLoading || segmentsLoading}
      profiles={displayProfiles}
      currentProfileId={activeProfileId}
      onProfileChange={setSelectedProfileId}
      filter={segmentFilter}
      onFilterChange={setSegmentFilter}
      tagIdToGroupId={tagIdToGroupId}
      canEdit={canWriteSegments}
      onSeek={(time) => seekRef.current?.(time)}
      currentTime={videoTime}
      onNavigate={onNavigate}
    />
  ) : activeTab === "similar" ? (
    <VideoVisualSimilarityPanel videoId={video.id} onNavigate={onNavigate} />
  ) : activeTab === "audio-similar" ? (
    <VideoAudioSimilarityPanel videoId={video.id} onNavigate={onNavigate} />
  ) : activeTab === "filters" ? (
    <VideoFiltersTab filters={videoFilters} onChange={setVideoFilters} />
  ) : activeTab === "file-info" && video.files.length > 0 ? (
    <FileInfoTab files={video.files} />
  ) : activeTab === "history" ? (
    <HistoryTab
      video={video}
      playCount={videoPlayCount}
      playDuration={videoPlayDuration}
    />
  ) : activeTab === "edit" ? (
    <VideoEditPanel video={video} onSaved={() => setActiveTab("details")} onNavigate={onNavigate} onRequestReportTag={requestReportTag} />
  ) : activeTab.startsWith("ext:") ? (() => {
    const extTabKey = activeTab.replace("ext:", "");
    const extTab = videoExtTabs.find((tab) => tab.key === extTabKey);
    if (!extTab) return null;
    const Component = resolveExtComponent(extTab.componentName);
    if (!Component) return <div className="p-4 text-muted">Extension component not found: {extTab.componentName}</div>;
    return (
      <ExtensionErrorBoundary extensionId={extTab.extensionId}>
        <Component entityId={id} />
      </ExtensionErrorBoundary>
    );
  })() : null;

  const videoMedia = (
    <div className="flex min-h-0 min-w-0 max-w-full flex-1 flex-col overflow-hidden bg-black">
      <div className="flex min-h-0 min-w-0 max-w-full flex-1 overflow-hidden bg-black">
        {file ? (
          <VideoPlayer
            // Intentionally NOT keyed by id: the player stays mounted across queue advances so the
            // fullscreen container (containerRef lives inside VideoPlayer) survives, keeping fullscreen
            // when you hit next. The player resets its per-video state on the videoId change and the
            // source-change effect calls video.load() (releasing the old stream); on unmount the cleanup
            // effect fully tears the connection down.
            streamUrl={streamUrl}
            posterUrl={videos.screenshotUrl(video.id, video.updatedAt)}
            format={file.format}
            duration={file.duration}
            audioCodec={file.audioCodec}
            resumeTime={effectiveResumeTime}
            videoId={video.id}
            detections={detections}
            segments={segments}
            faces={videoFaces.map(({ face }) => face)}
            captions={file.captions}
            videoStyle={videoStyle}
            onSeekRegister={(fn) => { seekRef.current = fn; }}
            onTimeUpdate={setVideoTime}
            autostart={config?.ui.autostartVideo}
            showAbLoop={config?.ui.showAbLoopControls}
            trackingEnabled={trackPlaybackActivity}
            onEnded={() => { if (queueAutoplay && queueSyncedToVideo && hasNext && nextId != null) onNavigate({ page: "video", id: nextId }); }}
            onPrev={queueSyncedToVideo && hasPrev && prevId != null ? () => onNavigate({ page: "video", id: prevId }) : undefined}
            onNext={queueSyncedToVideo && hasNext && nextId != null ? () => onNavigate({ page: "video", id: nextId }) : undefined}
          />
        ) : (
          <div className="flex h-48 items-center justify-center text-muted">No video file available</div>
        )}
      </div>
      {file ? (
        <VideoScrubber
          videoId={video.id}
          duration={file.duration}
          spans={resolvedSpans}
          rawSegments={segments}
          detections={detections}
          faces={videoFaces.map(({ face }) => face)}
          performers={video.performers}
          onSeek={(time) => seekRef.current?.(time)}
          currentTime={videoTime}
          profileName={activeProfileName}
          filter={segmentFilter}
          filterContext={segmentFilterContext}
          onClearFilter={() => setSegmentFilter(EMPTY_SEGMENT_FILTER)}
        />
      ) : null}
      {showQueuePanel && queueLength > 0 ? (
        <VideoQueuePanel
          items={queueItems}
          currentId={id}
          autoplay={queueAutoplay}
          onClose={() => setShowQueuePanel(false)}
          onClear={() => { clearQueue(); setShowQueuePanel(false); }}
          onToggleAutoplay={toggleAutoplay}
          onNavigate={(videoId, index) => {
            goToIndex(index);
            onNavigate({ page: "video", id: videoId });
          }}
        />
      ) : null}
    </div>
  );

  return (
    <>
      <CoverImageDialog
        open={coverOpen}
        title="Set Video Cover"
        currentImageUrl={videos.screenshotUrl(video.id, video.updatedAt)}
        onUpload={(file) => entityImages.uploadVideoCoverImage(video.id, file)}
        onDelete={() => entityImages.deleteVideoCoverImage(video.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={invalidateVideoCover}
        aspectRatio="16/9"
        extraActions={file ? (
          <button
            type="button"
            onClick={() => { handleSetCoverFromCurrentFrame(); setCoverOpen(false); }}
            disabled={coverActionPending}
            className="inline-flex w-full items-center justify-center gap-2 rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground hover:border-accent hover:text-accent disabled:opacity-60"
          >
            {coverActionPending ? <span className="h-3.5 w-3.5 animate-spin rounded-full border-b-2 border-accent" /> : <Camera className="h-3.5 w-3.5" />}
            From Current Frame
          </button>
        ) : null}
      />
      <Suspense fallback={null}>
        {showGenerate ? (
          <GenerateDialog
            open={showGenerate}
            onClose={() => setShowGenerate(false)}
            videoIds={[id]}
            title={`Generate for "${video.title || "Untitled"}"`}
          />
        ) : null}
        {showDownloadDialog ? (
          <VideoDownloadDialog
            open={showDownloadDialog}
            video={video}
            onClose={() => setShowDownloadDialog(false)}
            onNavigate={onNavigate}
          />
        ) : null}
        {showScrapeDialog ? (
          <VideoMetadataTaggerDialog
            open={showScrapeDialog}
            video={video}
            onClose={() => setShowScrapeDialog(false)}
            onNavigate={onNavigate}
          />
        ) : null}
        {showMerge ? (
          <DetailMergeDialog
            open={showMerge}
            onClose={() => setShowMerge(false)}
            entityType="video"
            targetItem={{ id: video.id, name: video.title || file?.basename || `Video ${video.id}`, imagePath: videos.screenshotUrl(video.id, video.updatedAt), subtitle: video.studioName }}
            searchItems={async (term) => {
              const response = await videos.find({ page: 1, perPage: 20, direction: "desc", q: term || undefined });
              return response.items.map((item) => ({
                id: item.id,
                name: item.title || item.files[0]?.basename || `Video ${item.id}`,
                imagePath: videos.screenshotUrl(item.id, item.updatedAt),
                subtitle: item.studioName,
              }));
            }}
            onMerge={(targetId, sourceIds) => videos.merge(targetId, sourceIds)}
            invalidateQueryKeys={[["video", id], ["videos"]]}
          />
        ) : null}
        {showIdentify ? (
          <IdentifyDialog
            open={showIdentify}
            onClose={() => setShowIdentify(false)}
            videoIds={[id]}
          />
        ) : null}
      </Suspense>
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Video"
        message={`Are you sure you want to delete "${video.title || "Untitled"}"? This cannot be undone.`}
        onConfirm={(opts) => deleteMut.mutate(opts?.deleteFile)}
        onCancel={() => setConfirmDelete(false)}
        showDeleteFile
      />
      <ConfirmDialog
        open={reportTag != null}
        title="Is this detection wrong?"
        message={reportTag
          ? `"${reportTag.name}" was detected${describeTagEvidence(reportTag)} in this video. Removing it deletes the AI's detection from this video — use this only when the AI is mistaken. If it's correct but just too minor, adjust the tag's threshold instead.`
          : ""}
        confirmLabel="Remove detection"
        isPending={reportTag != null && reportIncorrectTagMut.isPending}
        onCancel={() => setReportTag(null)}
        onConfirm={async () => {
          if (!reportTag) return;
          await reportIncorrectTagMut.mutateAsync(reportTag.id);
          setReportTag(null);
        }}
      />
      <MediaDetailLayout
        title={<FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="title">{videoTitle}</FieldProvenanceHover>}
        headerImage={videoHeaderImage}
        subtitle={videoSubtitle}
        backLabel={backLabel}
        onGoBack={goBack}
        media={videoMedia}
        mediaAspectRatio="auto"
        mediaFullBleed
        mediaSticky={false}
        tabs={tabs}
        activeTab={activeTab}
        onTabChange={(key) => setActiveTab(key as TabKey)}
        engagement={{
          primaryContent: <InteractiveRating value={videoRating} onChange={(value) => setVideoRating(value)} readOnly={!canEngageVideo} />,
          favorite: videoFavorite,
          favoritePending: videoFavoritePending,
          onFavoriteChange: canEngageVideo ? setVideoFavorite : undefined,
          additionalMetrics: [
            {
              label: "Likes",
              value: videoLikeCount,
              icon: <ThumbsUp className={["h-4 w-4", videoLikeCount > 0 ? "fill-accent text-accent" : ""].join(" ")} />,
              title: "Likes",
              onClick: canEngageVideo ? () => incrementLikeMut.mutate() : undefined,
              active: videoLikeCount > 0,
            },
            {
              label: "Page Visits",
              value: videoPageVisitCount,
              icon: <Eye className="h-4 w-4" />,
              title: "Page visits",
            },
          ],
        }}
        keyboardShortcuts={videoKeyboardShortcuts}
        actions={videoActions}
      >
        <MediaDetailLayout.Content>
          {activeTab === "details" ? (
            <div className="mb-4">
              <AspectRatingsPanel hostType="video" hostId={id} canRate={canEngageVideo} />
            </div>
          ) : null}
          {activeTabContent}
        </MediaDetailLayout.Content>
        <ExtensionSlot slot="video-detail-main-bottom" context={{ video, onNavigate }} />
      </MediaDetailLayout>
    </>
  );
}

function buildVideoEditPerformerContextTagIds(video: Video): Record<number, number[]> {
  const result: Record<number, number[]> = {};
  for (const application of video.contextTagApplications ?? []) {
    if (application.contextType !== "performer" || application.contextId == null) {
      continue;
    }

    result[application.contextId] = [...(result[application.contextId] ?? []), application.tag.id];
  }

  return result;
}

async function syncVideoEditPerformerContextTags(videoId: number, existingApplications: TagApplication[], desiredByPerformer: Record<number, number[]>, selectedPerformerIds: number[]) {
  const selectedPerformers = new Set(selectedPerformerIds);
  const desiredKeys = new Set<string>();

  for (const [performerIdText, tagIds] of Object.entries(desiredByPerformer)) {
    const performerId = Number(performerIdText);
    if (!selectedPerformers.has(performerId)) {
      continue;
    }

    for (const tagId of tagIds) {
      desiredKeys.add(`${performerId}:${tagId}`);
    }
  }

  const existingContextApplications = existingApplications.filter((application) => application.contextType === "performer" && application.contextId != null);

  for (const application of existingContextApplications) {
    const key = `${application.contextId}:${application.tag.id}`;
    if (!desiredKeys.has(key)) {
      await tagApplications.delete(application.id);
    }
  }

  const existingKeys = new Set(existingContextApplications.map((application) => `${application.contextId}:${application.tag.id}`));
  for (const [performerIdText, tagIds] of Object.entries(desiredByPerformer)) {
    const performerId = Number(performerIdText);
    if (!selectedPerformers.has(performerId)) {
      continue;
    }

    for (const tagId of tagIds) {
      const key = `${performerId}:${tagId}`;
      if (existingKeys.has(key)) {
        continue;
      }

      await tagApplications.create({
        hostType: "video",
        hostId: videoId,
        contextType: "performer",
        contextId: performerId,
        tagId,
        sourceKey: "user",
      });
    }
  }
}

// Renders the AI's evidence for a derived tag (" for 14s (3% of this video)") so the user can judge
// whether it's a genuine mistake or just a minor-but-real detection before deciding to remove it.
function describeTagEvidence(tag: { effectiveDurationSec?: number | null; effectiveDurationPercent?: number | null }): string {
  const parts: string[] = [];
  if (typeof tag.effectiveDurationSec === "number" && tag.effectiveDurationSec > 0) {
    parts.push(formatDuration(tag.effectiveDurationSec));
  }
  if (typeof tag.effectiveDurationPercent === "number" && tag.effectiveDurationPercent > 0) {
    parts.push(`${tag.effectiveDurationPercent.toFixed(tag.effectiveDurationPercent < 10 ? 1 : 0)}% of this video`);
  }
  return parts.length > 0 ? ` for ${parts.join(" — ")}` : "";
}

// Details Tab Content
export function DetailsTab({ video, onNavigate, videoFaces = [], onMarkFaceNotPresent, markingFaceId, onRequestReportTag }: { video: Video; onNavigate: (r: any) => void; videoFaces?: Array<{ face: Face; detectionCount: number }>; onMarkFaceNotPresent?: (faceId: number) => void; markingFaceId?: number; onRequestReportTag?: (tag: any) => void }) {
  const { engagementById: performerEngagement } = useEntityEngagementBatch("performer", video?.performers?.map((p) => p.id) ?? []);
  return (
    <div className="space-y-4">
      {/* Created/Updated + Code/Director at top like original */}
      <dl className="grid gap-y-1.5 text-sm" style={{ gridTemplateColumns: "auto 1fr" }}>
        <dt className="text-muted pr-3">Created</dt>
        <dd className="text-foreground">{formatDate(video.createdAt)}</dd>
        <dt className="text-muted pr-3">Updated</dt>
        <dd className="text-foreground">{formatDate(video.updatedAt)}</dd>
        {video.code && (
          <>
            <dt className="text-muted pr-3">Studio Code</dt>
            <dd className="text-foreground"><FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="code">{video.code}</FieldProvenanceHover></dd>
          </>
        )}
        {video.director && (
          <>
            <dt className="text-muted pr-3">Director</dt>
            <dd>
              <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="director">
                <button onClick={() => onNavigate({ page: "videos", query: video.director })} className="text-accent hover:underline">
                  {video.director}
                </button>
              </FieldProvenanceHover>
            </dd>
          </>
        )}
      </dl>

      {/* Details / Description */}
      {video.details && (
        <div>
          <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="details" block>
            <p className="text-sm text-foreground whitespace-pre-wrap">{video.details}</p>
          </FieldProvenanceHover>
        </div>
      )}

      {/* Tags */}
      {video.tags.length > 0 && (
        <div>
          <h6 className="text-sm text-muted mb-2">Tags</h6>
          <div className="flex flex-wrap gap-1.5">
            {video.tags.map((tag: any) => (
              <TagBadge
                key={tag.id}
                name={tag.name}
                tag={tag}
                provenance={resolveTagProvenance(tag, video.fieldProvenance)}
                onClick={() => onNavigate({ page: "tag", id: tag.id })}
                reportable={Boolean(tag.canReportIncorrect && onRequestReportTag)}
                onAdjustThreshold={() => onNavigate({ page: "tag", id: tag.id })}
                onReportIncorrect={() => onRequestReportTag?.(tag)}
              />
            ))}
          </div>
        </div>
      )}

      {/* Performers */}
      {video.performers.length > 0 && (
        <div>
          <h6 className="text-sm text-muted mb-2">Performer{video.performers.length > 1 ? "s" : ""}</h6>
          <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="performers" block>
            <div className={video.performers.length > 1 ? "grid grid-cols-2 gap-3" : "grid max-w-[220px] gap-3"}>
              {video.performers.map((performer: any) => {
                const contextTags = (video.contextTagApplications ?? []).filter((application) => application.contextType === "performer" && application.contextId === performer.id);
                const ageAtVideo = getAgeAtDate(video.date, performer.birthdate);
                const footer = ageAtVideo || contextTags.length > 0
                  ? <VideoPerformerTileFooter ageAtVideo={ageAtVideo} contextTags={contextTags} />
                  : null;

                return (
                  <PerformerTile
                    key={performer.id}
                    performer={performer}
                    engagement={performerEngagement.get(performer.id)}
                    onClick={() => onNavigate({ page: "performer", id: performer.id })}
                    onNavigate={onNavigate}
                  >
                    {footer}
                  </PerformerTile>
                );
              })}
            </div>
          </FieldProvenanceHover>
        </div>
      )}

      {video.groups.length > 0 && (
        <div>
          <h6 className="mb-2 text-sm text-muted">Groups</h6>
          <div className="flex flex-wrap gap-2">
            {video.groups.map((group) => (
              <EntityRefBadge
                key={group.id}
                route={{ page: "group", id: group.id }}
                onNavigate={onNavigate}
                imageUrl={entityImages.groupFrontImageUrl(group.id)}
                icon={<Layers className="h-5 w-5" />}
                label={group.name}
              />
            ))}
          </div>
        </div>
      )}

      {video.galleries.length > 0 && (
        <div>
          <h6 className="mb-2 text-sm text-muted">Galleries</h6>
          <div className="flex flex-wrap gap-2">
            {video.galleries.map((gallery) => (
              <EntityRefBadge
                key={gallery.id}
                route={{ page: "gallery", id: gallery.id }}
                onNavigate={onNavigate}
                imageUrl={galleries.coverUrl(gallery.id)}
                icon={<FolderOpen className="h-5 w-5" />}
                label={gallery.title || "Untitled"}
              />
            ))}
          </div>
        </div>
      )}

      {/* Faces */}
      {videoFaces.length > 0 && (
        <div>
          <h6 className="mb-2 text-sm text-muted">Faces in this video</h6>
          <div className="flex flex-wrap gap-2">
            {videoFaces.map(({ face, detectionCount }) => {
              const title = face.label?.trim() || face.performerName || `Face #${face.id}`;
              const isMarking = markingFaceId === face.id;
              return (
                <div
                  key={face.id}
                  className="group relative flex min-w-[180px] flex-1 items-stretch sm:flex-none sm:basis-[calc(50%-0.25rem)]"
                >
                  <button
                    type="button"
                    onClick={() => onNavigate({ page: "face", id: face.id })}
                    className="flex w-full items-center gap-3 rounded-xl border border-border bg-card/70 px-3 py-2 text-left transition-colors hover:border-accent"
                  >
                    <div className="h-14 w-14 overflow-hidden rounded-lg bg-surface/80">
                      {face.coverImageUrl ? (
                        <img src={face.coverImageUrl} alt={title} className="h-full w-full object-cover" loading="lazy" />
                      ) : (
                        <div className="flex h-full w-full items-center justify-center text-muted">
                          <Image className="h-5 w-5" />
                        </div>
                      )}
                    </div>
                    <div className="min-w-0 flex-1">
                      <div className="truncate text-sm font-medium text-foreground">{title}</div>
                      <div className="mt-1 text-xs text-secondary">
                        {detectionCount} detection{detectionCount === 1 ? "" : "s"}
                      </div>
                    </div>
                  </button>
                  {onMarkFaceNotPresent ? (
                    <button
                      type="button"
                      title="This face is not actually present in this video"
                      aria-label="Mark face not present in this video"
                      disabled={isMarking}
                      onClick={() => {
                        if (window.confirm(`Mark "${title}" as NOT present in this video?\n\nIts occurrences here (and other videos that match them) will be split off into the correct face.`)) {
                          onMarkFaceNotPresent(face.id);
                        }
                      }}
                      className="absolute right-1 top-1 rounded-md bg-surface/80 p-1 text-muted opacity-0 transition-opacity hover:text-red-300 disabled:cursor-not-allowed disabled:opacity-100 group-hover:opacity-100"
                    >
                      {isMarking ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <UserX className="h-3.5 w-3.5" />}
                    </button>
                  ) : null}
                </div>
              );
            })}
          </div>
        </div>
      )}

      {/* URLs */}
      {video.urls && video.urls.length > 0 && (
        <div>
          <h6 className="text-sm text-muted mb-2">URLs</h6>
          <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="urls" block>
            <div className="space-y-1">
              {video.urls.map((url: string, i: number) => (
                <a
                  key={i}
                  href={url}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="text-accent hover:underline text-sm block truncate"
                >
                  {url}
                </a>
              ))}
            </div>
          </FieldProvenanceHover>
        </div>
      )}

      <CustomFieldsDisplay customFields={video.customFields} entityType="video" />
    </div>
  );
}

function VideoPerformerTileFooter({ ageAtVideo, contextTags = [] }: { ageAtVideo: number | null; contextTags?: TagApplication[] }) {
  return <div className="space-y-2 text-xs text-secondary">
    {ageAtVideo ? <div className="text-center">{ageAtVideo} yrs old</div> : null}
    <PerformerContextTagList contextTags={contextTags} />
  </div>;
}

function getAgeAtDate(videoDate?: string, birthdate?: string) {
  if (!videoDate || !birthdate) return null;

  const video = new Date(videoDate);
  const birth = new Date(birthdate);
  let age = video.getFullYear() - birth.getFullYear();
  const monthDelta = video.getMonth() - birth.getMonth();
  if (monthDelta < 0 || (monthDelta === 0 && video.getDate() < birth.getDate())) age--;
  return age > 0 ? age : null;
}

function PerformerContextTagList({ contextTags }: { contextTags: TagApplication[] }) {
  return contextTags.length > 0 ? (
    <div className="flex flex-wrap gap-1.5">
      {contextTags.map((application) => (
        <TagBadge key={application.id} name={application.tag.name} tag={application.tag} provenance={[toTagProvenance(application)]} />
      ))}
    </div>
  ) : null;
}

function toTagProvenance(application: TagApplication) {
  return {
    sourceKey: application.sourceKey,
    sourceRunId: application.sourceRunId ?? undefined,
    modelKey: application.modelKey ?? undefined,
    confidence: application.confidence ?? undefined,
    appliedAt: application.appliedAt,
    contextType: application.contextType ?? undefined,
    contextId: application.contextId ?? undefined,
    totalDurationSec: application.totalDurationSec ?? undefined,
    hostDurationSec: application.hostDurationSec ?? undefined,
  };
}

// File Info Tab — show every underlying video file rather than only the first one.
export function FileInfoTab({ files }: { files: Video["files"] }) {
  const revealMutation = useMutation({ mutationFn: (fileId: number) => fileOps.reveal(fileId) });
  const canReveal = typeof window !== "undefined" && ["localhost", "127.0.0.1", "::1"].includes(window.location.hostname);

  return (
    <div className="space-y-4 text-sm">
      {files.map((file, index) => {
        const sectionLabel = file.basename || file.path.split(/[\\/]/).pop() || `File ${index + 1}`;

        return (
          <section key={file.id ?? `${file.path}-${index}`} className="rounded-xl border border-border bg-card p-4 space-y-3">
            {files.length > 1 && (
              <div className="flex items-start justify-between gap-3">
                <div>
                  <h6 className="text-sm font-semibold text-foreground">{sectionLabel}</h6>
                  <p className="text-xs text-muted">File {index + 1} of {files.length}</p>
                </div>
                {canReveal && file.id ? (
                  <button
                    type="button"
                    onClick={() => revealMutation.mutate(file.id)}
                    className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-xs text-secondary hover:border-accent hover:text-foreground"
                  >
                    <FolderOpen className="h-3.5 w-3.5" />
                    Reveal
                  </button>
                ) : null}
              </div>
            )}

            {files.length <= 1 && canReveal && file.id ? (
              <div className="flex justify-end">
                <button
                  type="button"
                  onClick={() => revealMutation.mutate(file.id)}
                  className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-xs text-secondary hover:border-accent hover:text-foreground"
                >
                  <FolderOpen className="h-3.5 w-3.5" />
                  Reveal
                </button>
              </div>
            ) : null}

            <dl className="grid gap-y-1.5" style={{ gridTemplateColumns: "minmax(100px, auto) 1fr" }}>
              <dt className="text-muted">Path</dt>
              <dd className="text-foreground break-all font-mono text-xs">{file.path}</dd>

              <dt className="text-muted">File Size</dt>
              <dd className="text-foreground">{formatFileSize(file.size)}</dd>

              <dt className="text-muted">Duration</dt>
              <dd className="text-foreground">{formatDuration(file.duration)}</dd>

              <dt className="text-muted">Dimensions</dt>
              <dd className="text-foreground">{file.width}×{file.height}</dd>

              <dt className="text-muted">Frame Rate</dt>
              <dd className="text-foreground">{file.frameRate.toFixed(2)} fps</dd>

              <dt className="text-muted">Bitrate</dt>
              <dd className="text-foreground">{Math.round(file.bitRate / 1000)} kbps</dd>

              <dt className="text-muted">Video Codec</dt>
              <dd className="text-foreground">{file.videoCodec}</dd>

              <dt className="text-muted">Audio Codec</dt>
              <dd className="text-foreground">{file.audioCodec}</dd>
            </dl>

            {file.fingerprints && file.fingerprints.length > 0 && (
              <div>
                <h6 className="text-sm text-muted mb-1 font-medium">Fingerprints</h6>
                <dl className="grid gap-y-1" style={{ gridTemplateColumns: "auto 1fr" }}>
                  {file.fingerprints.map((fp: any) => (
                    <Fragment key={`${file.id ?? index}-${fp.type}`}>
                      <dt className="text-muted text-xs pr-3">{fp.type}</dt>
                      <dd className="text-foreground font-mono text-xs break-all">{fp.value}</dd>
                    </Fragment>
                  ))}
                </dl>
              </div>
            )}
          </section>
        );
      })}
    </div>
  );
}

// History Tab
function HistoryTab({
  video,
  playCount,
  playDuration,
}: {
  video: Video;
  playCount: number;
  playDuration: number;
}) {
  const queryClient = useQueryClient();
  const { data: history } = useQuery({
    queryKey: ["video-history", video.id],
    queryFn: () => videos.getHistory(video.id),
  });
  const resetPlayMut = useMutation({
    mutationFn: () => videos.resetPlay(video.id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["video", video.id] });
      queryClient.invalidateQueries({ queryKey: ["engagement", "video", video.id] });
      queryClient.invalidateQueries({ queryKey: ["video-history", video.id] });
    },
  });
  const deletePlayMut = useMutation({
    mutationFn: () => videos.deletePlay(video.id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["video", video.id] });
      queryClient.invalidateQueries({ queryKey: ["engagement", "video", video.id] });
      queryClient.invalidateQueries({ queryKey: ["video-history", video.id] });
    },
  });

  const btnCls = "rounded border border-border bg-card px-2 py-0.5 text-xs text-secondary hover:text-foreground hover:bg-card-hover";
  const recentSessions = history?.sessions?.slice(0, 10) ?? [];

  return (
    <div className="space-y-6 text-sm">
      {/* Play History */}
      <section>
        <div className="flex items-center justify-between mb-2">
          <h3 className="text-sm font-semibold text-muted uppercase tracking-wide">Play History</h3>
          <div className="flex gap-1">
            <button onClick={() => deletePlayMut.mutate()} className={btnCls} title="Remove last play">-1</button>
            <button onClick={() => resetPlayMut.mutate()} className={btnCls} title="Reset play count">Reset</button>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-2 mb-2">
          <div><span className="text-muted">Play Count:</span> <span className="text-foreground">{playCount}</span></div>
          <div><span className="text-muted">Duration:</span> <span className="text-foreground">{formatDuration(playDuration)}</span></div>
        </div>
        {history?.playHistory && history.playHistory.length > 0 && (
          <div className="max-h-40 overflow-y-auto space-y-0.5 border-t border-border pt-2">
            {history.playHistory.map((date, i) => (
              <div key={i} className="text-xs text-secondary">{new Date(date).toLocaleString()}</div>
            ))}
          </div>
        )}
      </section>

      {recentSessions.length > 0 && (
        <section>
          <div className="mb-2 flex items-center justify-between">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">Playback Sessions</h3>
            <span className="text-xs text-secondary">{recentSessions.length}{(history?.sessions?.length ?? 0) > recentSessions.length ? ` of ${history?.sessions?.length ?? 0}` : ""} sessions</span>
          </div>
          <div className="space-y-3 border-t border-border pt-3">
            {recentSessions.map((session) => (
              <div key={session.sessionId} className="rounded-lg border border-border/70 bg-surface/35 px-3 py-2">
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <div className="text-xs font-medium uppercase tracking-wide text-foreground">
                      {session.isCompleted ? "Completed session" : "Playback session"}
                    </div>
                    <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 text-xs text-secondary">
                      <span>Watched {formatDuration(session.totalWatchedSec)}</span>
                      {session.lastPositionSec != null ? <span>Last position {formatDuration(session.lastPositionSec)}</span> : null}
                      <span>{session.intervals.length} intervals</span>
                    </div>
                  </div>
                  <div className="shrink-0 text-xs text-secondary">{new Date(session.startedAt).toLocaleString()}</div>
                </div>
                {session.intervals.length > 0 ? (
                  <div className="mt-2 flex flex-wrap gap-1.5">
                    {session.intervals.map((range, index) => (
                      <span key={`${session.sessionId}-${range.startSec}-${range.endSec}-${index}`} className="rounded-full border border-border bg-card px-2 py-0.5 text-[11px] text-secondary">
                        {formatDuration(range.startSec)}-{formatDuration(range.endSec)}
                      </span>
                    ))}
                  </div>
                ) : null}
              </div>
            ))}
          </div>
        </section>
      )}

      {/* Timestamps */}
      <div className="grid grid-cols-2 gap-2">
        <div><span className="text-muted">Created:</span> <span className="text-foreground">{formatDate(video.createdAt)}</span></div>
        <div><span className="text-muted">Updated:</span> <span className="text-foreground">{formatDate(video.updatedAt)}</span></div>
      </div>
    </div>
  );
}

// Video Filters Tab — matches standard's brightness/contrast/gamma/saturation/hue
interface VideoFilters {
  brightness: number;
  contrast: number;
  gamma: number;
  saturation: number;
  hue: number;
}

function VideoFiltersTab({ filters, onChange }: { filters: VideoFilters; onChange: (f: VideoFilters) => void }) {
  const sliders: { key: keyof VideoFilters; label: string; min: number; max: number; default: number; unit: string; formatValue?: (v: number) => string }[] = [
    { key: "brightness", label: "Brightness", min: 0, max: 200, default: 100, unit: "%" },
    { key: "contrast", label: "Contrast", min: 0, max: 200, default: 100, unit: "%" },
    { key: "gamma", label: "Gamma", min: 0, max: 200, default: 100, unit: "", formatValue: (v) => String(v - 100) },
    { key: "saturation", label: "Saturation", min: 0, max: 200, default: 100, unit: "%" },
    { key: "hue", label: "Hue", min: -180, max: 180, default: 0, unit: "°" },
  ];

  const handleReset = () => onChange({ brightness: 100, contrast: 100, gamma: 100, saturation: 100, hue: 0 });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h5 className="text-sm font-medium text-foreground">Filters</h5>
        <button onClick={handleReset} className="text-xs text-accent hover:underline">Reset All</button>
      </div>
      {sliders.map(({ key, label, min, max, default: def, unit, formatValue }) => (
        <div key={key} className="flex items-center gap-3">
          <span className="text-sm text-muted w-24 flex-shrink-0">{label}</span>
          <input
            type="range"
            min={min}
            max={max}
            value={filters[key]}
            onChange={(e) => onChange({ ...filters, [key]: Number(e.target.value) })}
            className="flex-1 h-1 accent-accent cursor-pointer"
          />
          <button
            onClick={() => onChange({ ...filters, [key]: def })}
            className="text-xs text-secondary hover:text-foreground w-12 text-right cursor-pointer"
            title="Click to reset"
          >
            {formatValue ? formatValue(filters[key]) : `${filters[key]}${unit}`}
          </button>
        </div>
      ))}
    </div>
  );
}

type TimelineOverlayItem = {
  key: string;
  startSec: number;
  endSec: number;
  label: string;
  colorHint?: string | null;
  colorSeed?: string;
};

const SEGMENT_TIMELINE_COLORS = ["#a87a2d", "#4c6faa", "#7963a1", "#a05f7b", "#3f7f6e", "#4b7f8e", "#748a37", "#a35d4e"];
const FACE_TIMELINE_COLORS = ["#4d8569", "#4a807b", "#5a7ca5", "#7d8842", "#a07a3f", "#93658a"];

function timelineHash(value: string) {
  let hash = 0;
  for (let index = 0; index < value.length; index++) {
    hash = ((hash << 5) - hash + value.charCodeAt(index)) | 0;
  }
  return Math.abs(hash);
}

function getTimelineOverlayColor(item: TimelineOverlayItem, palette: string[]) {
  const hint = item.colorHint?.trim();
  if (hint) return hint;
  const seed = item.colorSeed || item.label || item.key;
  return palette[timelineHash(seed) % palette.length];
}

function timelineLabelFits(widthPercent: number, label: string) {
  return widthPercent >= Math.min(10, Math.max(2.4, label.length * 0.28));
}

function getSegmentTimelineLabel(
  span: Pick<ResolvedSpan, "spanKey" | "tagName" | "kind" | "sourceKey" | "segmentIds">,
  rawSegmentsById: Map<number, Pick<Segment, "id" | "title" | "kind" | "sourceKey" | "refId">>,
  performersById: Map<number, Pick<PerformerSummary, "id" | "name">>,
) {
  const tagName = span.tagName?.trim();
  if (tagName) return tagName;

  for (const segmentId of span.segmentIds ?? []) {
    const segment = rawSegmentsById.get(segmentId);
    if (!segment) continue;

    const kind = segment.kind?.trim().toLowerCase();
    if (segment.refId != null && kind === "performer") {
      const performerName = performersById.get(Number(segment.refId))?.name?.trim();
      if (performerName) return performerName;
    }

    const title = segment.title?.trim();
    if (title && title.toLowerCase() !== "performer" && !isRawDataLabel(title)) return title;
  }

  const kind = span.kind?.trim();
  if (kind && kind !== "tag") return kind;

  const sourceKey = span.sourceKey?.trim();
  if (sourceKey) return sourceKey.replace(/^ext:ai\./, "").replace(/^ext:/, "");

  return "Segment";
}

function isRawDataLabel(value: string) {
  return value.startsWith("{") || value.startsWith("[") || value.includes('"probabilit');
}

// Video Scrubber / Timeline Component
function VideoScrubber({ 
  videoId, 
  duration, 
  spans,
  rawSegments,
  detections,
  faces,
  performers,
  onSeek,
  currentTime,
  profileName,
  filter,
  filterContext,
  onClearFilter,
}: {
  videoId: number;
  duration: number;
  spans: Pick<ResolvedSpan, "spanKey" | "startSec" | "endSec" | "tagId" | "tagName" | "kind" | "colorHint" | "sourceKey" | "lane" | "segmentIds">[];
  rawSegments: Pick<Segment, "id" | "startSec" | "endSec" | "title" | "kind" | "sourceKey" | "refId">[];
  detections: Pick<Detection, "id" | "observedAtSec" | "class" | "score" | "refKind" | "refId">[];
  faces?: Pick<Face, "id" | "label" | "performerName" | "performerId">[];
  performers?: Pick<PerformerSummary, "id" | "name">[];
  onSeek?: (time: number) => void;
  currentTime?: number;
  profileName?: string;
  filter: SegmentFilterState;
  filterContext: SegmentFilterContext;
  onClearFilter: () => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const [spriteData, setSpriteData] = useState<{ entries: { start: number; end: number; x: number; y: number; w: number; h: number }[]; imageUrl: string } | null>(null);
  const [spriteError, setSpriteError] = useState(false);
  const [spriteLoadSettled, setSpriteLoadSettled] = useState(false);
  
  const spriteVttUrl = `/api/stream/video/${videoId}/vtt/thumbs`;
  const spriteImageUrl = `/api/stream/video/${videoId}/sprite`;
  const [showAllResolvedLanes, setShowAllResolvedLanes] = useState(false);
  const [showAllFaceLanes, setShowAllFaceLanes] = useState(false);
  const [overlaysCollapsed, setOverlaysCollapsed] = usePersistedFlag("cove.timeline.overlaysCollapsed", false);
  const [facesEnabled, setFacesEnabled] = usePersistedFlag("cove.timeline.facesEnabled", false);
  
  const formatTime = (s: number) => {
    const m = Math.floor(s / 60);
    const sec = Math.floor(s % 60);
    return `${m}:${sec.toString().padStart(2, "0")}`;
  };

  // Load and parse VTT sprite data
  useEffect(() => {
    let cancelled = false;

    setSpriteData(null);
    setSpriteError(false);
    setSpriteLoadSettled(false);

    fetch(spriteVttUrl)
      .then(r => { if (!r.ok) throw new Error("VTT not found"); return r.text(); })
      .then(text => {
        if (cancelled) return;
        const entries: typeof spriteData extends null ? never : NonNullable<typeof spriteData>["entries"] = [];
        const blocks = text.split(/\n\n+/);
        for (const block of blocks) {
          const lines = block.trim().split("\n");
          for (let i = 0; i < lines.length; i++) {
            const timeMatch = lines[i].match(/(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})/);
            if (timeMatch && lines[i + 1]) {
              const xywhMatch = lines[i + 1].match(/#xywh=(\d+),(\d+),(\d+),(\d+)/);
              if (xywhMatch) {
                entries.push({
                  start: parseVttTime(timeMatch[1]),
                  end: parseVttTime(timeMatch[2]),
                  x: parseInt(xywhMatch[1]),
                  y: parseInt(xywhMatch[2]),
                  w: parseInt(xywhMatch[3]),
                  h: parseInt(xywhMatch[4]),
                });
              }
            }
          }
        }
        if (entries.length > 0) {
          setSpriteData({ entries, imageUrl: spriteImageUrl });
        } else {
          setSpriteError(true);
        }
        setSpriteLoadSettled(true);
      })
      .catch(() => {
        if (cancelled) return;
        setSpriteError(true);
        setSpriteLoadSettled(true);
      });

    return () => {
      cancelled = true;
    };
  }, [videoId, spriteVttUrl, spriteImageUrl]);

  const thumbCount = spriteData ? spriteData.entries.length : 0;
  const thumbWidth = 160;
  const thumbHeight = spriteData?.entries[0] ? Math.round(thumbWidth * (spriteData.entries[0].h / spriteData.entries[0].w)) : 0;
  const rawSegmentsById = useMemo(() => new Map(rawSegments.map((segment) => [segment.id, segment])), [rawSegments]);
  const performersById = useMemo(() => new Map((performers ?? []).map((performer) => [performer.id, performer])), [performers]);
  const filterActive = isSegmentFilterActive(filter);
  const nonFaceSpans = useMemo(
    () => spans.filter((span) => !isFaceResolvedSpan(span, rawSegmentsById) && matchesSegmentFilter(span, filter, filterContext)),
    [rawSegmentsById, spans, filter, filterContext],
  );
  // Faces carry no tags, so a tag/tag-group filter hides them; otherwise they pass when the
  // filter targets faces (kind "face", or a specific face/performer).
  const faceFilterPredicate = useMemo(() => {
    if (!filterActive) return () => true;
    const hasTagFilter = filter.tagIds.length > 0 || filter.tagGroupIds.length > 0;
    const kinds = filter.kinds.map((kind) => kind.toLowerCase());
    const faceIds = new Set(filter.faceIds);
    const performerIds = new Set(filter.performerIds);
    return (faceId: number, performerId?: number | null) => {
      if (hasTagFilter) return false;
      if (kinds.length > 0 && !kinds.includes("face")) return false;
      if (faceIds.size > 0 && !faceIds.has(faceId)) return false;
      if (performerIds.size > 0 && !(performerId != null && performerIds.has(performerId))) return false;
      return true;
    };
  }, [filterActive, filter.tagIds, filter.tagGroupIds, filter.kinds, filter.faceIds, filter.performerIds]);
  // A face-targeting filter auto-reveals the faces lane even when the manual toggle is off.
  const faceFilterTargetsFaces = filterActive
    && filter.tagIds.length === 0 && filter.tagGroupIds.length === 0
    && (filter.kinds.map((kind) => kind.toLowerCase()).includes("face") || filter.faceIds.length > 0 || filter.performerIds.length > 0);
  const effectiveFacesEnabled = facesEnabled || faceFilterTargetsFaces;
  const segmentLanes = useMemo(() => buildTimelineLanes<TimelineOverlayItem>(
    nonFaceSpans.map((span) => ({
      key: span.spanKey,
      startSec: span.startSec,
      endSec: span.endSec,
      label: getSegmentTimelineLabel(span, rawSegmentsById, performersById),
      colorHint: span.colorHint,
      colorSeed: `${span.kind ?? "span"}:${span.tagName ?? ""}:${span.sourceKey ?? ""}`,
    })),
  ), [nonFaceSpans, performersById, rawSegmentsById]);
  const faceLanes = useMemo(() => {
    if (!effectiveFacesEnabled) return [] as ReturnType<typeof buildTimelineLanes<TimelineOverlayItem>>;
    const facesById = new Map<number, Pick<Face, "id" | "label" | "performerName" | "performerId">>();
    for (const face of faces ?? []) facesById.set(face.id, face);

    const items: TimelineOverlayItem[] = [];
    const segmentFaceIds = new Set<number>();

    for (const segment of rawSegments) {
      if (!isFaceTimelineSegment(segment)) continue;
      const faceId = segment.refId != null ? Number(segment.refId) : -Math.abs(segment.id);
      const face = facesById.get(faceId);
      if (!faceFilterPredicate(faceId, face?.performerId)) continue;
      if (faceId > 0) segmentFaceIds.add(faceId);
      const label = face?.performerName?.trim() || face?.label?.trim() || segment.title?.trim() || (faceId > 0 ? `Face #${faceId}` : "Face");
      items.push({
        key: `face-segment-${segment.id}`,
        startSec: segment.startSec,
        endSec: Math.max(segment.endSec ?? segment.startSec, segment.startSec + 0.4),
        label,
        colorSeed: String(faceId),
      });
    }

    const buckets = new Map<number, number[]>();
    for (const det of detections) {
      if (det.refId == null || det.refKind?.toLowerCase() !== "face" || segmentFaceIds.has(det.refId)) continue;
      if (det.observedAtSec == null) continue;
      if (!faceFilterPredicate(det.refId, facesById.get(det.refId)?.performerId)) continue;
      const arr = buckets.get(det.refId) ?? [];
      arr.push(det.observedAtSec);
      buckets.set(det.refId, arr);
    }

    const MERGE_GAP_SEC = 2.5;
    for (const [faceId, times] of buckets.entries()) {
      times.sort((left, right) => left - right);
      let windowStart = times[0];
      let windowEnd = times[0];
      let runIndex = 0;
      const flush = () => {
        const face = facesById.get(faceId);
        const label = face?.performerName?.trim() || face?.label?.trim() || (faceId > 0 ? `Face #${faceId}` : "Face");
        items.push({
          key: `face-${faceId}-${runIndex++}`,
          startSec: windowStart,
          endSec: Math.max(windowEnd, windowStart + 0.4),
          label,
          colorSeed: String(faceId),
        });
      };
      for (let i = 1; i < times.length; i++) {
        const t = times[i];
        if (t - windowEnd <= MERGE_GAP_SEC) {
          windowEnd = t;
        } else {
          flush();
          windowStart = t;
          windowEnd = t;
        }
      }
      flush();
    }

    return buildTimelineLanes(items);
  }, [detections, rawSegments, faces, effectiveFacesEnabled, faceFilterPredicate]);
  // Detection dots respect the face/performer filter; when a non-face filter is active they are hidden.
  const visibleDetections = useMemo(
    () => detections.filter((det) => det.refId == null || det.refKind?.toLowerCase() !== "face"
      ? !filterActive
      : faceFilterPredicate(det.refId, null)),
    [detections, filterActive, faceFilterPredicate],
  );
  const visibleResolvedLanes = showAllResolvedLanes ? segmentLanes : segmentLanes.slice(0, 4);
  const visibleFaceLanes = showAllFaceLanes ? faceLanes : faceLanes.slice(0, 2);
  const hiddenResolvedLaneCount = Math.max(0, segmentLanes.length - visibleResolvedLanes.length);
  const hiddenFaceLaneCount = Math.max(0, faceLanes.length - visibleFaceLanes.length);
  const hasFaceDetections = useMemo(
    () => detections.some((det) => det.refKind?.toLowerCase() === "face" && det.refId != null)
      || rawSegments.some((segment) => isFaceTimelineSegment(segment)),
    [detections, rawSegments],
  );

  // Determine which thumbnail index is active based on current video time
  const activeIndex = useMemo(() => {
    if (currentTime == null || currentTime <= 0) return -1;
    if (spriteData) {
      for (let i = spriteData.entries.length - 1; i >= 0; i--) {
        if (currentTime >= spriteData.entries[i].start) return i;
      }
      return 0;
    }
    return -1;
  }, [currentTime, spriteData, duration, thumbCount]);

  // Auto-scroll to active thumbnail
  useEffect(() => {
    if (activeIndex >= 0 && scrollRef.current) {
      const targetLeft = activeIndex * thumbWidth;
      const { scrollLeft, clientWidth } = scrollRef.current;
      if (targetLeft < scrollLeft || targetLeft + thumbWidth > scrollLeft + clientWidth) {
        scrollRef.current.scrollTo({ left: Math.max(0, targetLeft - clientWidth / 2 + thumbWidth / 2), behavior: "smooth" });
      }
    }
  }, [activeIndex, thumbWidth]);

  const scroll = (dir: number) => {
    if (scrollRef.current) scrollRef.current.scrollBy({ left: dir * thumbWidth * 4, behavior: "smooth" });
  };
  const clampPercent = (value: number) => Math.min(100, Math.max(0, value));
  const timelineDuration = Math.max(0.001, duration || 0);
  
  return (
    <div className="flex-shrink-0 bg-[#1a1a1a] border-t border-border">
      {(spans.length > 0 || hasFaceDetections) && (
        <div className="border-b border-black/20 bg-[#181a20]">
          <div className="flex flex-wrap items-center justify-between gap-2 border-b border-white/10 bg-[#20222a] px-2 py-1.5 pr-8 text-[10px] text-white/65">
            <div className="flex min-w-0 flex-wrap items-center gap-1.5">
              <span className="font-semibold uppercase tracking-[0.16em] text-white/70">Timeline overlays</span>
              {nonFaceSpans.length > 0 ? <span className="rounded border border-white/10 bg-white/[0.04] px-1.5 py-0.5">{nonFaceSpans.length} segment{nonFaceSpans.length === 1 ? "" : "s"}</span> : null}
              {filterActive ? (
                <button
                  type="button"
                  onClick={onClearFilter}
                  className="inline-flex items-center gap-1 rounded border border-accent/50 bg-accent/15 px-1.5 py-0.5 text-accent transition-colors hover:bg-accent/25"
                  title="Filtered by the segments panel — click to clear"
                >
                  <Filter className="h-3 w-3" /> Filtered
                  <X className="h-3 w-3" />
                </button>
              ) : null}
            </div>
            <div className="flex shrink-0 flex-wrap items-center gap-1">
              <button
                type="button"
                onClick={() => setOverlaysCollapsed((value) => !value)}
                className="inline-flex items-center gap-1 rounded border border-white/10 px-2 py-0.5 text-[9px] text-white/70 transition-colors hover:border-white/30 hover:text-white"
                title={overlaysCollapsed ? "Show timeline overlays" : "Collapse timeline overlays"}
              >
                <ChevronDown className={`h-3 w-3 transition-transform ${overlaysCollapsed ? "-rotate-90" : ""}`} />
                {overlaysCollapsed ? "Show" : "Collapse"}
              </button>
              {!overlaysCollapsed && segmentLanes.length > 4 ? (
                <button
                  type="button"
                  onClick={() => setShowAllResolvedLanes((value) => !value)}
                  className="rounded border border-white/10 px-2 py-0.5 text-[9px] text-white/70 transition-colors hover:border-white/30 hover:text-white"
                >
                  {showAllResolvedLanes ? "Fewer segments" : `All ${segmentLanes.length} segment lanes`}
                </button>
              ) : null}
              {!overlaysCollapsed && hasFaceDetections && !faceFilterTargetsFaces ? (
                <button
                  type="button"
                  onClick={() => setFacesEnabled((value) => !value)}
                  className="inline-flex items-center gap-1 rounded border border-white/10 px-2 py-0.5 text-[9px] text-white/70 transition-colors hover:border-white/30 hover:text-white"
                  title={facesEnabled ? "Hide face appearance bars" : "Show face appearance bars"}
                >
                  {facesEnabled ? <Eye className="h-3 w-3" /> : <EyeOff className="h-3 w-3" />}
                  {facesEnabled ? "Hide faces" : "Show faces"}
                </button>
              ) : null}
              {!overlaysCollapsed && effectiveFacesEnabled && faceLanes.length > 2 ? (
                <button
                  type="button"
                  onClick={() => setShowAllFaceLanes((value) => !value)}
                  className="rounded border border-white/10 px-2 py-0.5 text-[9px] text-white/70 transition-colors hover:border-white/30 hover:text-white"
                >
                  {showAllFaceLanes ? "Fewer faces" : `All ${faceLanes.length} face lanes`}
                </button>
              ) : null}
            </div>
          </div>
          {!overlaysCollapsed ? <div className="space-y-2 px-2 py-2">
            {nonFaceSpans.length > 0 ? (
              <div className="space-y-1">
                <div className="flex items-center justify-between text-[10px] uppercase tracking-[0.14em] text-white/45">
                  <span>Segments{profileName ? ` · ${profileName}` : ""}</span>
                  {hiddenResolvedLaneCount > 0 ? <span>{hiddenResolvedLaneCount} hidden</span> : null}
                </div>
                <div className="relative overflow-hidden rounded border border-white/10 bg-black/25" style={{ height: `${Math.max(28, visibleResolvedLanes.length * 24 + 6)}px` }}>
                  {visibleResolvedLanes.map((lane, laneIndex) => lane.map(({ item, endSec }) => {
                    const start = clampPercent((item.startSec / timelineDuration) * 100);
                    const end = clampPercent(((endSec + 0.001) / timelineDuration) * 100);
                    const width = Math.max(0.45, end - start);
                    const color = getTimelineOverlayColor(item, SEGMENT_TIMELINE_COLORS);

                    return (
                      <button
                        key={item.key}
                        className="absolute h-5 overflow-hidden rounded-sm px-1.5 text-left text-[10px] font-semibold leading-5 text-white shadow-sm transition hover:brightness-110 focus:outline-none focus:ring-1 focus:ring-white/70"
                        style={{
                          left: `${start}%`,
                          top: `${laneIndex * 24 + 4}px`,
                          width: `${width}%`,
                          backgroundColor: color,
                          boxShadow: "inset 0 0 0 1px rgba(255,255,255,0.2)",
                        }}
                        title={`${item.label} (${formatTimelineTime(item.startSec)} - ${formatTimelineTime(endSec)})`}
                        onClick={() => onSeek?.(item.startSec)}
                      >
                        {timelineLabelFits(width, item.label) ? <span className="block truncate">{item.label}</span> : null}
                      </button>
                    );
                  }))}
                </div>
              </div>
            ) : null}
            {hasFaceDetections && effectiveFacesEnabled && faceLanes.length > 0 ? (
              <div className="space-y-1">
                <div className="flex items-center justify-between text-[10px] uppercase tracking-[0.14em] text-white/45">
                  <span>Faces</span>
                  {hiddenFaceLaneCount > 0 ? <span>{hiddenFaceLaneCount} hidden</span> : null}
                </div>
                <div className="relative overflow-hidden rounded border border-white/10 bg-black/25" style={{ height: `${Math.max(28, visibleFaceLanes.length * 24 + 6)}px` }}>
                  {visibleFaceLanes.map((lane, laneIndex) => lane.map(({ item, endSec }) => {
                    const start = clampPercent((item.startSec / timelineDuration) * 100);
                    const end = clampPercent(((endSec + 0.001) / timelineDuration) * 100);
                    const width = Math.max(0.45, end - start);
                    const color = getTimelineOverlayColor(item, FACE_TIMELINE_COLORS);

                    return (
                      <button
                        key={item.key}
                        className="absolute h-5 overflow-hidden rounded-sm px-1.5 text-left text-[10px] font-semibold leading-5 text-white shadow-sm transition hover:brightness-110 focus:outline-none focus:ring-1 focus:ring-white/70"
                        style={{
                          left: `${start}%`,
                          top: `${laneIndex * 24 + 4}px`,
                          width: `${width}%`,
                          backgroundColor: color,
                          boxShadow: "inset 0 0 0 1px rgba(255,255,255,0.2)",
                        }}
                        title={`${item.label} (${formatTimelineTime(item.startSec)} - ${formatTimelineTime(endSec)})`}
                        onClick={() => onSeek?.(item.startSec)}
                      >
                        {timelineLabelFits(width, item.label) ? <span className="block truncate">{item.label}</span> : null}
                      </button>
                    );
                  }))}
                </div>
              </div>
            ) : null}
          </div> : null}
        </div>
      )}
      {visibleDetections.length > 0 && (
        <div className="relative h-5 border-b border-black/20 bg-[#1f2c35]">
          {visibleDetections.map((detection) => {
            const time = detection.observedAtSec ?? 0;
            return (
              <button
                key={detection.id}
                className="absolute top-1/2 h-3 w-3 -translate-x-1/2 -translate-y-1/2 rounded-full border border-white/30 bg-sky-400/80 hover:bg-sky-300"
                style={{ left: `${clampPercent((time / timelineDuration) * 100)}%` }}
                title={`${detection.class} (${Math.round(detection.score * 100)}%) at ${formatTimelineTime(time)}${detection.refKind && detection.refId != null ? ` • ${detection.refKind} #${detection.refId}` : ""}`}
                onClick={() => onSeek?.(time)}
              />
            );
          })}
        </div>
      )}

      {spriteData && spriteLoadSettled && !spriteError ? (
      <div className="relative flex overflow-hidden" ref={containerRef}>
        <button onClick={() => scroll(-1)} className="flex-shrink-0 w-7 bg-[#222] hover:bg-[#333] text-muted border-r border-border z-10">
          <ChevronLeft className="w-4 h-4 mx-auto" />
        </button>
        
        <div ref={scrollRef} className="flex-1 flex overflow-x-auto scrollbar-thin scrollbar-thumb-border">
          {Array.from({ length: thumbCount }).map((_, i) => {
            const entry = spriteData.entries[i];
            const time = entry?.start ?? 0;
            const isActive = i === activeIndex;
            return (
              <div 
                key={i} 
                className={`flex-shrink-0 relative cursor-pointer hover:ring-2 hover:ring-accent hover:z-10 ${isActive ? "ring-2 ring-accent z-10" : ""}`}
                style={{ width: thumbWidth }}
                onClick={() => onSeek?.(time)}
              >
                <div className="bg-surface" style={{ width: thumbWidth, height: thumbHeight }}>
                  {entry ? (
                    <div
                      style={{
                        width: thumbWidth,
                        height: thumbHeight,
                        backgroundImage: `url(${spriteData!.imageUrl})`,
                        backgroundPosition: `-${entry.x * (thumbWidth / entry.w)}px -${entry.y * (thumbHeight / entry.h)}px`,
                        backgroundSize: `${(spriteData!.entries[0].w * Math.ceil(Math.sqrt(thumbCount))) * (thumbWidth / entry.w)}px auto`,
                      }}
                    />
                  ) : null}
                </div>
                <div className="absolute bottom-0 left-0 right-0 text-center text-[10px] text-white bg-black/70 py-0.5">
                  {formatTime(time)}
                </div>
              </div>
            );
          })}
        </div>
        
        <button onClick={() => scroll(1)} className="flex-shrink-0 w-7 bg-[#222] hover:bg-[#333] text-muted border-l border-border z-10">
          <ChevronRight className="w-4 h-4 mx-auto" />
        </button>
      </div>
      ) : null}
    </div>
  );
}

function buildTimelineLanes<T extends { key: string; startSec: number; endSec: number }>(items: T[]) {
  const ordered = [...items].sort((left, right) => left.startSec - right.startSec || left.endSec - right.endSec || left.key.localeCompare(right.key));
  const lanes: Array<Array<{ item: T; endSec: number }>> = [];
  const laneEnds: number[] = [];

  ordered.forEach((item) => {
    const effectiveEnd = Math.max(item.endSec, item.startSec + 0.05);
    let laneIndex = laneEnds.findIndex((laneEnd) => laneEnd <= item.startSec);
    if (laneIndex === -1) {
      laneIndex = lanes.length;
      lanes.push([]);
      laneEnds.push(effectiveEnd);
    } else {
      laneEnds[laneIndex] = effectiveEnd;
    }

    lanes[laneIndex].push({ item: { ...item, endSec: effectiveEnd }, endSec: effectiveEnd });
  });

  return lanes;
}

function isFaceTimelineSegment(segment: Pick<Segment, "title" | "kind" | "sourceKey">) {
  const normalizedKind = segment.kind?.trim().toLowerCase() ?? "";
  const normalizedSource = segment.sourceKey?.trim().toLowerCase() ?? "";
  const normalizedTitle = segment.title?.trim().toLowerCase() ?? "";
  return normalizedKind === "face" || normalizedSource.includes("face") || normalizedTitle.startsWith("face-");
}

function isFaceResolvedSpan(
  span: Pick<ResolvedSpan, "kind" | "sourceKey" | "tagName" | "segmentIds">,
  rawSegmentsById: Map<number, Pick<Segment, "id" | "title" | "kind" | "sourceKey" | "refId">>,
) {
  if (isFaceTimelineSegment({ title: span.tagName ?? undefined, kind: span.kind, sourceKey: span.sourceKey ?? "" })) {
    return true;
  }

  const segmentIds = span.segmentIds ?? [];
  return segmentIds.length > 0 && segmentIds.every((segmentId) => {
    const segment = rawSegmentsById.get(segmentId);
    return segment ? isFaceTimelineSegment(segment) : false;
  });
}

function parseVttTime(timeStr: string): number {
  const parts = timeStr.split(":");
  return parseInt(parts[0]) * 3600 + parseInt(parts[1]) * 60 + parseFloat(parts[2]);
}

function formatTimelineTime(seconds: number) {
  const mins = Math.floor(seconds / 60);
  const secs = Math.floor(seconds % 60);
  const fractional = seconds % 1;

  if (fractional > 0) {
    return `${mins}:${secs.toString().padStart(2, "0")}.${Math.round(fractional * 10)}`;
  }

  return `${mins}:${secs.toString().padStart(2, "0")}`;
}

function DetectionsPanel({
  detections,
  loading,
  onSeek,
}: {
  detections: Detection[];
  loading: boolean;
  onSeek?: (time: number) => void;
}) {
  const classCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const detection of detections) {
      counts.set(detection.class, (counts.get(detection.class) ?? 0) + 1);
    }
    return Array.from(counts.entries()).sort((a, b) => b[1] - a[1]).slice(0, 6);
  }, [detections]);

  if (loading) {
    return <div className="text-sm text-secondary">Loading detections...</div>;
  }

  if (detections.length === 0) {
    return <div className="text-sm text-muted">No detections recorded for this video.</div>;
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2 text-xs text-secondary">
        <span>{detections.length} detection{detections.length !== 1 ? "s" : ""}</span>
        {classCounts.map(([name, count]) => (
          <span key={name} className="rounded-full border border-border bg-surface px-2 py-1">
            {name} · {count}
          </span>
        ))}
      </div>
      <div className="space-y-1">
        {detections.map((detection) => (
          <div key={detection.id} className="rounded border border-border bg-card px-3 py-2 text-sm">
            <div className="flex items-center justify-between gap-3">
              <button className="flex items-center gap-3 text-left hover:text-accent" onClick={() => onSeek?.(detection.observedAtSec ?? 0)}>
                <span className="w-20 font-mono text-xs text-accent">{formatTimelineTime(detection.observedAtSec ?? 0)}</span>
                <span className="text-foreground">{detection.class}</span>
                <span className="rounded bg-surface px-1.5 py-0.5 text-xs text-secondary">{Math.round(detection.score * 100)}%</span>
              </button>
              <div className="text-xs text-secondary">{detection.frameWidth}×{detection.frameHeight}</div>
            </div>
            <div className="mt-2 flex flex-wrap gap-2 text-xs text-secondary">
              <span className="rounded bg-surface px-1.5 py-0.5">x {detection.x.toFixed(3)}</span>
              <span className="rounded bg-surface px-1.5 py-0.5">y {detection.y.toFixed(3)}</span>
              <span className="rounded bg-surface px-1.5 py-0.5">w {detection.w.toFixed(3)}</span>
              <span className="rounded bg-surface px-1.5 py-0.5">h {detection.h.toFixed(3)}</span>
              {detection.refKind && detection.refId != null && (
                <span className="rounded bg-surface px-1.5 py-0.5">{detection.refKind} #{detection.refId}</span>
              )}
              {detection.groupKey && <span className="rounded bg-surface px-1.5 py-0.5">group {detection.groupKey}</span>}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ===== Inline Video Edit Panel =====
function VideoEditPanel({ video, onSaved, onNavigate, onRequestReportTag }: { video: Video; onSaved: () => void; onNavigate?: (r: any) => void; onRequestReportTag?: (tag: any) => void }) {
  const queryClient = useQueryClient();
  const { config } = useAppConfig();
  const [title, setTitle] = useState(video.title || "");
  const [code, setCode] = useState(video.code || "");
  const [details, setDetails] = useState(video.details || "");
  const [director, setDirector] = useState(video.director || "");
  const [date, setDate] = useState(video.date || "");
  const [isVr, setIsVr] = useState(video.isVr ?? false);
  const [rating, setRating] = useState<number | undefined>(undefined);
  const [urls, setUrls] = useState(video.urls.length > 0 ? video.urls : [""]);
  const [remoteIds, setRemoteIds] = useState<RemoteIdValue[]>(video.remoteIds?.length ? video.remoteIds : []);
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({ ...(video.customFields ?? {}) });
  const [studioId, setStudioId] = useState<number | undefined>(video.studioId ?? undefined);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>(getEditableTagIds(video.tags));
  const [selectedPerformerIds, setSelectedPerformerIds] = useState<number[]>(video.performers.map((p) => p.id));
  const [selectedGalleryIds, setSelectedGalleryIds] = useState<number[]>(video.galleries.map((g) => g.id));
  const [selectedGroups, setSelectedGroups] = useState<{ groupId: number; videoIndex: number }[]>(
    video.groups.map((g) => ({ groupId: g.id, videoIndex: g.videoIndex }))
  );
  const [contextTagIdsByPerformer, setContextTagIdsByPerformer] = useState<Record<number, number[]>>(() => buildVideoEditPerformerContextTagIds(video));
  const [performerOccurrenceTagsOpen, setPerformerOccurrenceTagsOpen] = useState(false);
  useEffect(() => {
    setTitle(video.title || ""); setCode(video.code || ""); setDetails(video.details || "");
    setDirector(video.director || ""); setDate(video.date || ""); setIsVr(video.isVr ?? false); setRating(undefined);
    setUrls(video.urls.length > 0 ? video.urls : [""]); setStudioId(video.studioId ?? undefined);
    setRemoteIds(video.remoteIds?.length ? video.remoteIds : []);
    setCustomFields({ ...(video.customFields ?? {}) });
    setSelectedTagIds(getEditableTagIds(video.tags)); setSelectedPerformerIds(video.performers.map((p) => p.id));
    setSelectedGalleryIds(video.galleries.map((g) => g.id));
    setSelectedGroups(video.groups.map((g) => ({ groupId: g.id, videoIndex: g.videoIndex })));
    setContextTagIdsByPerformer(buildVideoEditPerformerContextTagIds(video));
  }, [video]);

  const mutation = useMutation({
    mutationFn: async (data: VideoUpdate) => {
      const updated = await videos.update(video.id, data);
      await syncVideoEditPerformerContextTags(video.id, video.contextTagApplications ?? [], contextTagIdsByPerformer, selectedPerformerIds);
      return updated;
    },
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["video", video.id] }); queryClient.invalidateQueries({ queryKey: ["tagapplications"] }); queryClient.invalidateQueries({ queryKey: ["videos"] }); onSaved(); },
  });

  const handleSave = () => {
    const urlList = urls.map((url) => url.trim()).filter(Boolean);
    mutation.mutate({ title: title, code: code, details: details,
      director: director, date: date || undefined, isVr, rating, studioId,
      urls: urlList, remoteIds: normalizeRemoteIds(remoteIds), customFields,
      tagIds: selectedTagIds, performerIds: selectedPerformerIds, galleryIds: selectedGalleryIds, groups: selectedGroups });
  };

  const setPerformerContextTagIds = (performerId: number, tagIds: number[]) => {
    setContextTagIdsByPerformer((current) => ({ ...current, [performerId]: Array.from(new Set(tagIds)) }));
  };
  const setSelectedGroupIds = (groupIds: number[]) => {
    setSelectedGroups(groupIds.map((groupId) => selectedGroups.find((group) => group.groupId === groupId) ?? { groupId, videoIndex: 0 }));
  };

  const lockedTagIds = getLockedTagIds(video.tags);
  const reportableTagIds = useMemo(() => video.tags.filter((tag: any) => tag.canReportIncorrect).map((tag) => tag.id), [video.tags]);
  const displayedTagIds = mergeTagIds(lockedTagIds, selectedTagIds);
  const tagProvenanceById = useMemo(() => {
    const lookup: Record<number, TagProvenance[] | undefined> = {};
    for (const tag of video.tags) {
      lookup[tag.id] = resolveTagProvenance(tag, video.fieldProvenance);
    }
    return lookup;
  }, [video.fieldProvenance, video.tags]);
  const updateSelectedTagIds = (tagIds: number[]) => {
    const locked = new Set(lockedTagIds);
    setSelectedTagIds(tagIds.filter((tagId) => !locked.has(tagId)));
  };

  const inputCls = "w-full bg-input border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent";

  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-3">
        <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="title" block>
          <label className="space-y-1"><span className="text-xs text-secondary">Title</span><input value={title} onChange={(e) => setTitle(e.target.value)} className={inputCls} /></label>
        </FieldProvenanceHover>
        <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="date" block>
          <label className="space-y-1"><span className="text-xs text-secondary">Date</span><input type="date" value={date} onChange={(e) => setDate(e.target.value)} className={inputCls} /></label>
        </FieldProvenanceHover>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="code" block>
          <label className="space-y-1"><span className="text-xs text-secondary">Studio Code</span><input value={code} onChange={(e) => setCode(e.target.value)} className={inputCls} /></label>
        </FieldProvenanceHover>
        <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="director" block>
          <label className="space-y-1"><span className="text-xs text-secondary">Director</span><input value={director} onChange={(e) => setDirector(e.target.value)} className={inputCls} /></label>
        </FieldProvenanceHover>
      </div>
      <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="details" block>
        <label className="block space-y-1"><span className="text-xs text-secondary">Details</span><textarea value={details} onChange={(e) => setDetails(e.target.value)} rows={3} className={inputCls} /></label>
      </FieldProvenanceHover>
      <label className="inline-flex items-center gap-2 text-sm text-secondary">
        <input type="checkbox" checked={isVr} onChange={(e) => setIsVr(e.target.checked)} className="rounded border-border bg-card" />
        VR
      </label>
      <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="studio" block>
        <div className="space-y-1">
          <span className="text-xs text-secondary">Studio</span>
          <StudioSelector value={studioId} onChange={setStudioId} placeholder="Search studios..." />
        </div>
      </FieldProvenanceHover>
      <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="urls" block>
        <div className="space-y-1"><span className="text-xs text-secondary">URLs</span><StringListEditor values={urls} onChange={setUrls} placeholder="https://..." addLabel="Add URL" inputType="url" /></div>
      </FieldProvenanceHover>
      {/* Tags */}
      <div className="space-y-1">
        <span className="text-xs text-secondary">Tags</span>
        <EntityReferenceMultiSelector
          entityType="tag"
          values={displayedTagIds}
          lockedIds={lockedTagIds}
          onChange={updateSelectedTagIds}
          placeholder="Search tags..."
          inputClassName={inputCls}
          selectedProvenanceById={tagProvenanceById}
          reportableIds={onRequestReportTag ? reportableTagIds : undefined}
          onReportIncorrect={onRequestReportTag ? (tagId) => onRequestReportTag(video.tags.find((tag) => tag.id === tagId)) : undefined}
          onAdjustThreshold={onNavigate ? (tagId) => onNavigate({ page: "tag", id: tagId }) : undefined}
        />
      </div>

      {/* Performers */}
      <FieldProvenanceHover fieldProvenance={video.fieldProvenance} fieldKey="performers" block>
        <div className="space-y-1">
          <span className="text-xs text-secondary">Performers</span>
          <EntityReferenceMultiSelector entityType="performer" values={selectedPerformerIds} onChange={setSelectedPerformerIds} placeholder="Search performers..." inputClassName={inputCls} />
        </div>
      </FieldProvenanceHover>

      {selectedPerformerIds.length > 0 ? (
        <div className="space-y-2 rounded-lg border border-border bg-surface/40 p-3">
          <button
            type="button"
            onClick={() => setPerformerOccurrenceTagsOpen((open) => !open)}
            className="flex w-full items-center justify-between gap-3 text-left text-xs font-medium uppercase tracking-wide text-secondary hover:text-foreground"
          >
            <span>Performer Occurrence Tags</span>
            <span className="inline-flex items-center gap-2 normal-case tracking-normal text-muted">
              {selectedPerformerIds.reduce((sum, performerId) => sum + (contextTagIdsByPerformer[performerId]?.length ?? 0), 0)} tag assignments
              {performerOccurrenceTagsOpen ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
            </span>
          </button>
          {performerOccurrenceTagsOpen ? selectedPerformerIds.map((performerId) => {
            const tagIds = contextTagIdsByPerformer[performerId] ?? [];

            return (
              <div key={performerId} className="rounded-lg border border-border bg-card/70 p-3">
                <div className="mb-2 flex items-center justify-between gap-3">
                  <div className="min-w-0 text-sm font-medium text-foreground"><EntityReferenceValue entityType="performer" value={performerId} /></div>
                  <div className="text-xs text-muted">{tagIds.length} tag{tagIds.length === 1 ? "" : "s"}</div>
                </div>
                <EntityReferenceMultiSelector
                  entityType="tag"
                  values={tagIds}
                  onChange={(nextTagIds) => setPerformerContextTagIds(performerId, nextTagIds)}
                  placeholder="Search tags for this occurrence..."
                  emptyMessage="No tags found"
                  inputClassName={inputCls}
                />
              </div>
            );
          }) : null}
        </div>
      ) : null}

      {/* Galleries */}
      <div className="space-y-1">
        <span className="text-xs text-secondary">Galleries</span>
        <EntityReferenceMultiSelector entityType="gallery" values={selectedGalleryIds} onChange={setSelectedGalleryIds} placeholder="Search galleries..." inputClassName={inputCls} />
      </div>

      {/* Groups */}
      <div className="space-y-1">
        <span className="text-xs text-secondary">Groups</span>
        <div className="space-y-1 mb-1">
          {selectedGroups.map((sg) => {
            return (
              <div key={sg.groupId} className="flex items-center gap-2">
                <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs bg-orange-900 text-orange-300">
                  <EntityReferenceValue entityType="group" value={sg.groupId} />
                  <button onClick={() => setSelectedGroups(selectedGroups.filter((g) => g.groupId !== sg.groupId))} className="hover:text-white">×</button>
                </span>
                <label className="flex items-center gap-1 text-xs text-muted">
                  Video #
                  <input type="number" min={0} value={sg.videoIndex}
                    onChange={(e) => setSelectedGroups(selectedGroups.map((g) => g.groupId === sg.groupId ? { ...g, videoIndex: Number(e.target.value) || 0 } : g))}
                    className="w-16 bg-surface border border-border rounded px-2 py-0.5 text-xs text-foreground focus:outline-none focus:border-accent" />
                </label>
              </div>
            );
          })}
        </div>
        <EntityReferenceMultiSelector entityType="group" values={selectedGroups.map((group) => group.groupId)} onChange={setSelectedGroupIds} placeholder="Search groups..." inputClassName={inputCls} />
      </div>

      <div className="space-y-1"><span className="text-xs text-secondary">Remote IDs</span><RemoteIdsEditor value={remoteIds} onChange={setRemoteIds} metadataServers={config?.scraping?.metadataServers} /></div>
      <div className="space-y-1"><span className="text-xs text-secondary">Custom Fields</span><CustomFieldsEditor value={customFields} onChange={setCustomFields} entityType="video" /></div>

      {mutation.error && <div className="bg-red-900/50 border border-red-700 text-red-300 rounded p-2 text-sm">{(mutation.error as Error).message}</div>}

      <div className="flex justify-end gap-3 pt-2">
        <button onClick={onSaved} className="px-4 py-2 text-sm text-secondary hover:text-foreground">Cancel</button>
        <button onClick={handleSave} disabled={mutation.isPending} className="px-4 py-2 text-sm bg-accent hover:bg-accent-hover text-white rounded disabled:opacity-50">
          {mutation.isPending ? "Saving…" : "Save"}
        </button>
      </div>
    </div>
  );
}

