import { type ReactNode, useCallback, useMemo, useRef, useState } from "react";
import { keepPreviousData, useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { Clapperboard, Info, MoreVertical, Play, Sparkles, Tag as TagIcon } from "lucide-react";
import { faces, performers, videos, segmentDisplayProfiles, segmentLibrary, tags } from "../api/client";
import type {
  Face,
  ResolvedSpan,
  ResolvedSpanDetail,
  ResolvedSpanInterval,
  SegmentDerivedQueryDescriptor,
  SegmentRecord,
  SegmentSpanOperator,
  TagProvenance,
} from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canReadEntity, canWriteEntity } from "../auth/visibility";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { SegmentKindIcon, segmentKindIconKind } from "../components/EntityCards";
import { RawSegmentIcon, SegmentIcon } from "../components/segmentViewIcons";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { ListLoadError } from "../components/ListLoadError";
import { MediaDetailLayout } from "../components/MediaDetailLayout/MediaDetailLayout";
import { SegmentVisualSimilarityPanel, useSegmentVisualSimilarityAvailable } from "../components/VisualSimilarityPanel";
import { VideoPlayer, type VideoPlayerSeek } from "../components/VideoPlayer";
import { ProvenanceBadge, TagBadge } from "../components/shared";
import { useBackNavigation, type BackNavigationAlternative } from "../hooks/useBackNavigation";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { useAppConfig } from "../state/AppConfigContext";
import { faceDisplayName } from "../utils/faceDisplay";
import { formatSourceKeyLabel } from "../utils/sourceKeyLabel";
import { FaceSection, linkLabel } from "./segments/SegmentOverview";
import {
  DetailSection,
  EntityLink,
  formatConfidencePercent,
  type NearbyItem,
  LoadingNextMarker,
  NearbySection,
  SegmentHeaderLinks,
  SuggestedName,
  SummaryLine,
  TechnicalDetails,
  VideoSection,
} from "./segments/SegmentDetailSections";
import {
  buildRawSegmentTitle,
  formatDerivedQueryName,
  rawSegmentFaceId,
  rawSegmentFaceName,
  rawSegmentPlaceholderFaceId,
} from "./segments/segmentDisplayUtils";
import { useListedFaceNames } from "./segments/useListedFaceNames";
import { useSegmentFace } from "./segments/useSegmentFace";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { buildSubVideoCreate } from "../utils/subVideoCreation";

interface Props {
  videoId: number;
  spanKey: string;
  profileId?: number;
  derivedQueryDescriptor?: SegmentDerivedQueryDescriptor;
  onNavigate: (r: any) => void;
}

type ResolvedSpanTab = "overview" | "similar";

export function ResolvedSpanPlayPage({ videoId, spanKey, profileId, derivedQueryDescriptor, onNavigate }: Props) {
  const { backLabel, goBack, backAlternative } = useBackNavigation({ page: "video", id: videoId }, onNavigate);
  const {
    data: detail,
    isLoading,
    isPlaceholderData: loadingNext,
    error: detailError,
    refetch: retryDetail,
  } = useQuery({
    queryKey: ["video", videoId, "span", spanKey, profileId],
    queryFn: () => videos.segments.spanDetail(videoId, spanKey, profileId),
    // Moving to another segment of the same video keeps this one on screen until the next loads, so the player,
    // the open tab and the sidebar scroll stay put instead of passing through a loading skeleton.
    placeholderData: (previous) => (previous?.videoId === videoId ? previous : undefined),
  });
  const detailLoadError = getLoadError(detail, detailError);

  if (isLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  if (isApiNotFoundError(detailLoadError)) {
    return <div className="py-16 text-center text-secondary">Segment not found</div>;
  }

  if (detailLoadError) {
    return (
      <ListLoadError
        error={detailLoadError}
        onRetry={() => {
          void retryDetail();
        }}
        title="Could not load segment"
        className="mx-0 mt-0"
      />
    );
  }

  if (!detail) {
    return <div className="py-16 text-center text-secondary">Segment not found</div>;
  }

  return (
    <ResolvedSpanPlayerCard
      detail={detail}
      loadingNext={loadingNext}
      derivedQueryDescriptor={derivedQueryDescriptor}
      onNavigate={onNavigate}
      backLabel={backLabel}
      onGoBack={goBack}
      backAlternative={backAlternative}
    />
  );
}

function ResolvedSpanPlayerCard({
  detail,
  loadingNext = false,
  derivedQueryDescriptor,
  backLabel,
  onGoBack,
  backAlternative,
  onNavigate,
}: {
  detail: ResolvedSpanDetail;
  /** The page still shows the previous segment while this one loads. */
  loadingNext?: boolean;
  derivedQueryDescriptor?: SegmentDerivedQueryDescriptor;
  backLabel: string;
  onGoBack: () => void;
  backAlternative?: BackNavigationAlternative;
  onNavigate: (r: any) => void;
}) {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const { config } = useAppConfig();
  const [activeIntervalIndex, setActiveIntervalIndex] = useState(0);
  const [resumeTime, setResumeTime] = useState(detail.span.startSec);
  const autoplayOnOpen = config?.ui.autostartVideo ?? false;
  const [autostart, setAutostart] = useState(autoplayOnOpen);
  const playerSeekRef = useRef<VideoPlayerSeek | null>(null);
  const [activeTab, setActiveTab] = useState<ResolvedSpanTab>("overview");
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const opsMenuRef = useRef<HTMLDivElement>(null);

  const intervals = useMemo(
    () =>
      detail.intervals.length > 0 ? detail.intervals : [{ startSec: detail.span.startSec, endSec: detail.span.endSec }],
    [detail.intervals, detail.span.endSec, detail.span.startSec],
  );
  const isDerivedQuery = useMemo(() => detail.span.spanKey.startsWith("dq-"), [detail.span.spanKey]);
  const derivedOperator = useMemo(
    () => derivedQueryDescriptor?.operator ?? parseDerivedOperator(detail.span.spanKey),
    [derivedQueryDescriptor?.operator, detail.span.spanKey],
  );
  const tagIds = useMemo(
    () => Array.from(new Set(derivedQueryDescriptor?.operands.flatMap((operand) => operand.tagIds ?? []) ?? [])),
    [derivedQueryDescriptor],
  );
  const performerIds = useMemo(
    () => Array.from(new Set(derivedQueryDescriptor?.operands.flatMap((operand) => operand.performerIds ?? []) ?? [])),
    [derivedQueryDescriptor],
  );
  const faceIds = useMemo(
    () => Array.from(new Set(derivedQueryDescriptor?.operands.flatMap((operand) => operand.faceIds ?? []) ?? [])),
    [derivedQueryDescriptor],
  );

  const profileQuery = useQuery({
    queryKey: ["segment-display-profile", detail.profileId],
    queryFn: () => segmentDisplayProfiles.get(detail.profileId),
    staleTime: 60_000,
  });

  const videoSpansQuery = useQuery({
    queryKey: ["video", detail.videoId, "segments", "spans", detail.profileId],
    queryFn: () => videos.segments.spans(detail.videoId, detail.profileId),
    staleTime: 60_000,
  });

  // A derived segment, or one without raw segments, never loads a list, so it must not keep the previous one either:
  // placeholder data is served even to a query that is turned off.
  const rawSegmentsEnabled = !isDerivedQuery && detail.span.segmentIds.length > 0;
  const rawSegmentsQuery = useQuery({
    queryKey: ["segments", "ids", detail.span.segmentIds.join(",")],
    queryFn: async () => {
      const response = await segmentLibrary.list({
        ids: detail.span.segmentIds.join(","),
        sort: "start_sec",
        direction: "asc",
        page: 1,
        perPage: Math.max(detail.span.segmentIds.length, 1),
      });
      return response.items;
    },
    enabled: rawSegmentsEnabled,
    // The previous segment's list stays until the next one's arrives, so the page does not jump while it loads. A
    // derived segment never loads a list, so it must not keep one either.
    placeholderData: rawSegmentsEnabled ? keepPreviousData : undefined,
    staleTime: 60_000,
  });
  // Only the raw segments of this segment: a list kept from the previous one shows nothing that is not part of it,
  // so its face, markers or name never stand in for this segment's.
  const rawSegments = useMemo(() => {
    const ids = new Set(detail.span.segmentIds);
    return isDerivedQuery ? [] : (rawSegmentsQuery.data ?? []).filter((segment) => ids.has(segment.id));
  }, [detail.span.segmentIds, isDerivedQuery, rawSegmentsQuery.data]);
  // The previous segment's list is still showing and none of this segment's raw segments have arrived yet.
  const loadingOwnRawSegments = rawSegmentsQuery.isPlaceholderData && rawSegments.length === 0;

  const tagQueries = useQueries({
    queries: tagIds.map((tagId) => ({
      queryKey: ["tag", tagId],
      queryFn: () => tags.get(tagId),
      staleTime: 60_000,
    })),
  });
  const performerQueries = useQueries({
    queries: performerIds.map((performerId) => ({
      queryKey: ["performer", performerId],
      queryFn: () => performers.get(performerId),
      staleTime: 60_000,
    })),
  });
  const faceQueries = useQueries({
    queries: faceIds.map((faceId) => ({
      queryKey: ["face", faceId],
      queryFn: () => faces.get(faceId),
      staleTime: 60_000,
    })),
  });

  const tagNamesById = useMemo(() => {
    const map = new Map<number, string>();
    tagIds.forEach((tagId, index) => {
      const tag = tagQueries[index]?.data;
      if (tag) {
        map.set(tagId, tag.name);
      }
    });
    return map;
  }, [tagIds, tagQueries]);
  const performerNamesById = useMemo(() => {
    const map = new Map<number, string>();
    performerIds.forEach((performerId, index) => {
      const performer = performerQueries[index]?.data;
      if (performer) {
        map.set(performerId, performer.name);
      }
    });
    return map;
  }, [performerIds, performerQueries]);
  const faceLabelsById = useMemo(() => {
    const map = new Map<number, string>();
    faceIds.forEach((faceId, index) => {
      const face = faceQueries[index]?.data;
      if (face) {
        map.set(faceId, faceDisplayName(face));
      }
    });
    return map;
  }, [faceIds, faceQueries]);
  const spanFaces = useMemo(
    () => faceQueries.map((query) => query.data).filter((face): face is Face => Boolean(face)),
    [faceQueries],
  );

  const canReadVideos = canReadEntity("video", hasPermission);
  const { data: currentVideo, isLoading: currentVideoLoading } = useQuery({
    queryKey: ["video", detail.videoId],
    queryFn: () => videos.get(detail.videoId),
    enabled: canReadVideos,
    staleTime: 60_000,
  });
  const currentFile = currentVideo?.files[0];
  const { engagementById: videoEngagement } = useEntityEngagementBatch(
    "video",
    detail.videoId != null ? [detail.videoId] : [],
  );
  const contextFollowTagId =
    detail.span.tagId ??
    derivedQueryDescriptor?.operands.find((operand) => (operand.tagIds?.length ?? 0) > 0)?.tagIds?.[0];
  const contextFollowTagName =
    detail.span.tagName ?? (contextFollowTagId != null ? tagNamesById.get(contextFollowTagId) : undefined);
  const spanContext = useMemo(
    () => buildSpanContext(videoSpansQuery.data?.spans ?? [], detail.span, contextFollowTagId, contextFollowTagName),
    [contextFollowTagId, contextFollowTagName, detail.span, videoSpansQuery.data?.spans],
  );

  const [prevAutoplayOnOpen, setPrevAutoplayOnOpen] = useState(autoplayOnOpen);
  if (autoplayOnOpen !== prevAutoplayOnOpen) {
    setPrevAutoplayOnOpen(autoplayOnOpen);
    setAutostart(autoplayOnOpen);
  }

  // Opening another span (or its intervals changing) restarts playback from the span start.
  const [prevSpan, setPrevSpan] = useState({
    spanKey: detail.span.spanKey,
    startSec: detail.span.startSec,
    intervals,
  });
  if (
    detail.span.spanKey !== prevSpan.spanKey ||
    detail.span.startSec !== prevSpan.startSec ||
    intervals !== prevSpan.intervals
  ) {
    setPrevSpan({ spanKey: detail.span.spanKey, startSec: detail.span.startSec, intervals });
    setResumeTime(detail.span.startSec);
    setAutostart(autoplayOnOpen);
    setActiveIntervalIndex(0);
    setShowOpsMenu(false);
  }

  const seekAbsolute = useCallback(
    (nextTime: number) => {
      const nextIndex = findIntervalIndex(nextTime, intervals);
      const bounded = clampNumber(nextTime, intervals[nextIndex].startSec, intervals[nextIndex].endSec);
      setActiveIntervalIndex(nextIndex);
      setResumeTime(bounded);
      playerSeekRef.current?.(bounded, false);
    },
    [intervals],
  );

  // The raw segment under the playhead, highlighted in the list of the segment's raw segments.
  const [playingRawSegmentId, setPlayingRawSegmentId] = useState<number | undefined>(undefined);
  const handlePlayerTimeUpdate = useCallback(
    (nextTime: number) => {
      const nextIndex = findIntervalIndex(nextTime, intervals);
      if (nextIndex !== activeIntervalIndex) {
        setActiveIntervalIndex(nextIndex);
      }
      const playing = rawSegments.find(
        (segment) => nextTime >= segment.startSec && nextTime < (segment.endSec ?? segment.startSec),
      );
      setPlayingRawSegmentId(playing?.id);
    },
    [activeIntervalIndex, intervals, rawSegments],
  );

  // A face span is named the way its cards and raw segments are: after the face's current name or suggested
  // performer rather than the placeholder key its segments were stored with.
  // Cards name a span after its first raw segment, so the page does too, falling back to any face segment in it.
  const spanFirstSegment = useMemo(
    () => rawSegments.find((segment) => segment.id === detail.span.segmentIds[0]),
    [detail.span.segmentIds, rawSegments],
  );
  const faceSegment = useMemo(
    () =>
      spanFirstSegment && rawSegmentFaceId(spanFirstSegment) != null
        ? spanFirstSegment
        : rawSegments.find((segment) => rawSegmentFaceId(segment) != null),
    [rawSegments, spanFirstSegment],
  );
  const canReadFaces = canReadEntity("face", hasPermission);
  // Nearby spans without a tag carry their segments' stored title, e.g. a face's placeholder key; their first raw
  // segment names them the way cards do.
  const nearbyUntaggedFirstIds = useMemo(() => {
    const nearby = [
      ...spanContext.previous,
      ...spanContext.next,
      ...spanContext.intersecting,
      ...(spanContext.nextSameTag ? [spanContext.nextSameTag] : []),
    ];
    return Array.from(
      new Set(
        nearby.filter((span) => span.tagId == null && span.segmentIds.length > 0).map((span) => span.segmentIds[0]),
      ),
    ).sort((left, right) => left - right);
  }, [spanContext]);
  const nearbySegmentsQuery = useQuery({
    queryKey: ["segments", "ids", nearbyUntaggedFirstIds.join(",")],
    queryFn: async () => {
      const response = await segmentLibrary.list({
        ids: nearbyUntaggedFirstIds.join(","),
        sort: "start_sec",
        direction: "asc",
        page: 1,
        perPage: nearbyUntaggedFirstIds.length,
      });
      return response.items;
    },
    enabled: !isDerivedQuery && nearbyUntaggedFirstIds.length > 0,
    placeholderData: keepPreviousData,
    staleTime: 60_000,
  });
  const nearbySegmentsById = useMemo(
    () => new Map((nearbySegmentsQuery.data ?? []).map((segment) => [segment.id, segment] as const)),
    [nearbySegmentsQuery.data],
  );
  const nearbyPlaceholderFaceIds = useMemo(
    () => (nearbySegmentsQuery.data ?? []).flatMap((segment) => rawSegmentPlaceholderFaceId(segment) ?? []),
    [nearbySegmentsQuery.data],
  );
  const nearbyFaceNames = useListedFaceNames(nearbyPlaceholderFaceIds, canReadFaces);
  const segmentFace = useSegmentFace(faceSegment, canReadFaces);
  const derivedName = isDerivedQuery
    ? formatDerivedQueryName(
        derivedQueryDescriptor,
        { tagNamesById, performerNamesById, faceLabelsById },
        { concise: true },
      )
    : undefined;
  const firstRawSegment = faceSegment ?? spanFirstSegment ?? rawSegments[0];
  // Only a span of a tag is named by it; any other span carries its segments' stored title in tagName.
  const hasTag = !isDerivedQuery && detail.span.tagId != null;
  const storedSpanName = detail.span.tagName || detail.span.kind || detail.videoTitle || "Segment";
  const spanTitle =
    derivedName ??
    (hasTag ? detail.span.tagName : undefined) ??
    segmentFace.shownName?.name ??
    (firstRawSegment ? buildRawSegmentTitle(firstRawSegment) : undefined) ??
    storedSpanName;
  const spanTitleIsSuggestion = !derivedName && !hasTag && !!segmentFace.shownName?.suggested;
  useDocumentTitle(spanTitleIsSuggestion ? `${spanTitle} (suggested)` : spanTitle);
  const spanKindIcon = isDerivedQuery
    ? null
    : segmentKindIconKind(detail.span.kind, !detail.span.tagName || detail.span.tagName === spanTitle);
  const isFaceSpan = detail.span.kind?.toLowerCase() === "face";
  const canCreateSubVideo = canWriteEntity("video", hasPermission) && !!currentVideo && !!currentFile;
  const createSubVideoMutation = useMutation({
    mutationFn: async () => {
      if (!currentVideo) {
        throw new Error("Video not loaded");
      }

      return videos.createSubVideo(
        detail.videoId,
        buildSubVideoCreate(
          currentVideo,
          {
            startSec: detail.span.startSec,
            endSec: detail.span.endSec,
          },
          {
            // A suggested name is only a guess, so the new video keeps the name the segment was stored with.
            title: spanTitleIsSuggestion ? storedSpanName : spanTitle,
          },
        ),
      );
    },
    onSuccess: (newVideo) => {
      queryClient.invalidateQueries({ queryKey: ["videos"] });
      queryClient.invalidateQueries({ queryKey: ["video", detail.videoId] });
      onNavigate({ page: "video", id: newVideo.id });
    },
  });

  const playerMedia = (
    <div className="flex min-h-0 min-w-0 max-w-full flex-1 flex-col overflow-hidden bg-black">
      {!canReadVideos ? (
        <div className="flex flex-1 items-center justify-center bg-black px-6 text-center text-sm text-secondary">
          The video player is unavailable because your permissions do not allow video playback.
        </div>
      ) : currentVideoLoading ? (
        <div className="flex flex-1 items-center justify-center bg-black text-sm text-secondary">
          Loading segment playback...
        </div>
      ) : currentFile ? (
        <div className="flex min-h-0 min-w-0 max-w-full flex-1 overflow-hidden bg-black">
          <VideoPlayer
            streamUrl={videos.streamUrl(detail.videoId)}
            posterUrl={videos.screenshotUrl(detail.videoId)}
            format={currentFile.format}
            duration={currentFile.duration}
            resumeTime={resumeTime}
            videoId={detail.videoId}
            detections={[]}
            segments={rawSegments}
            faces={segmentFace.playerFaces.length > 0 ? segmentFace.playerFaces : spanFaces}
            // A face span is about where its face is, so its box shows unless the viewer turned X-ray off.
            faceOverlayDefault={isFaceSpan}
            captions={currentFile.captions}
            onPlay={() => setAutostart(false)}
            onSeekRegister={(seek) => {
              playerSeekRef.current = seek;
            }}
            onTimeUpdate={handlePlayerTimeUpdate}
            autostart={autostart}
            playbackTracking={{
              hostType: "video",
              hostId: detail.videoId,
              surface: "resolvedSpan",
              scopeKey: `video:${detail.videoId}:span:${detail.span.spanKey}`,
              itemHostType: "video",
              itemHostId: detail.videoId,
              clipStartSec: detail.span.startSec,
              clipEndSec: detail.span.endSec,
              context: {
                spanKey: detail.span.spanKey,
              },
            }}
            clip={{ start: detail.span.startSec, end: detail.span.endSec, loop: false }}
          />
        </div>
      ) : (
        <div className="flex flex-1 items-center justify-center bg-black text-sm text-secondary">
          No playable video file is available for this segment.
        </div>
      )}
    </div>
  );

  const hasVisualSimilarity = useSegmentVisualSimilarityAvailable({
    videoId: detail.videoId,
    intervals: intervals.map((interval) => ({ startSec: interval.startSec, endSec: interval.endSec })),
  });

  const tabs = useMemo(
    () => [
      { key: "overview", label: "Overview", icon: <Info className="h-4 w-4" /> },
      ...(hasVisualSimilarity ? [{ key: "similar", label: "Similar", icon: <Sparkles className="h-4 w-4" /> }] : []),
    ],
    [hasVisualSimilarity],
  );

  const orderedVideoSpans = useMemo(
    () =>
      [...(videoSpansQuery.data?.spans ?? [])].sort(
        (left, right) => left.startSec - right.startSec || left.endSec - right.endSec,
      ),
    [videoSpansQuery.data?.spans],
  );
  const spanPosition = orderedVideoSpans.findIndex((span) => span.spanKey === detail.span.spanKey);
  const openVideo = canReadVideos
    ? () => onNavigate({ page: "video", id: detail.videoId, seekTo: detail.span.startSec })
    : undefined;
  const nearbySpan = (span: ResolvedSpan): NearbyItem => {
    const kindIcon = segmentKindIconKind(span.kind, true);
    const firstSegment = span.tagId == null ? nearbySegmentsById.get(span.segmentIds[0]) : undefined;
    const faceName = firstSegment ? rawSegmentFaceName(firstSegment, nearbyFaceNames) : undefined;
    return {
      key: span.spanKey,
      title: faceName?.name ?? (firstSegment ? buildRawSegmentTitle(firstSegment) : formatResolvedSpanTitle(span)),
      suggested: faceName?.suggested,
      range: `${formatTime(span.startSec)} - ${formatTime(span.endSec)}`,
      meta: span.segmentIds.length > 1 ? `${span.segmentIds.length} raw segments` : undefined,
      icon: kindIcon ? (
        <span aria-hidden className="inline-flex">
          <SegmentKindIcon kind={kindIcon} className="h-3.5 w-3.5 shrink-0 text-secondary" />
        </span>
      ) : undefined,
      onClick: () =>
        onNavigate({ page: "video-span", id: detail.videoId, spanKey: span.spanKey, profileId: detail.profileId }),
    };
  };
  const tagId = detail.span.tagId;
  const openTag =
    !isDerivedQuery && tagId != null && canReadEntity("tag", hasPermission)
      ? () => onNavigate({ page: "tag", id: tagId })
      : undefined;

  // Facts shown under the title: the tag when the heading does not name it, and the source.
  const summaryLine = (
    <SummaryLine
      items={[
        isDerivedQuery ? (
          <ResolvedSpanSourceDetails
            key="derived"
            detail={detail}
            derivedOperator={derivedOperator}
            derivedQueryDescriptor={derivedQueryDescriptor}
            profileName={profileQuery.data?.name}
            tagNamesById={tagNamesById}
            performerNamesById={performerNamesById}
            faceLabelsById={faceLabelsById}
            onNavigate={onNavigate}
          />
        ) : hasTag && detail.span.tagName && detail.span.tagName !== spanTitle ? (
          <EntityLink
            key="tag"
            label={linkLabel(detail.span.tagName, spanTitle, "tag", !!openTag)}
            icon={<TagIcon className="h-3.5 w-3.5" />}
            onClick={openTag}
          />
        ) : null,
        isDerivedQuery ? undefined : formatSourceKeyLabel(detail.span.sourceKey),
      ]}
    />
  );

  const overviewContent = (
    <div className="space-y-6">
      {faceSegment ? (
        <FaceSection
          faceSegment={faceSegment}
          heading={spanTitle}
          face={segmentFace.face}
          faceLoading={segmentFace.faceLoading}
          suggestions={segmentFace.suggestions}
          decisionPending={segmentFace.decisionPending}
          decisionError={segmentFace.decisionError}
          canReadFaces={canReadFaces}
          canWriteFaces={canWriteEntity("face", hasPermission)}
          canReadPerformers={canReadEntity("performer", hasPermission)}
          onAcceptSuggestion={(suggestion) => segmentFace.decide(suggestion, "accept")}
          onRejectSuggestion={(suggestion) => segmentFace.decide(suggestion, "reject")}
          onNavigate={onNavigate}
        />
      ) : null}
      <VideoSection
        video={currentVideo}
        videoTitle={detail.videoTitle || `Video #${detail.videoId}`}
        engagement={videoEngagement.get(detail.videoId)}
        position={
          spanPosition >= 0 && orderedVideoSpans.length > 1
            ? `${spanPosition + 1} of ${orderedVideoSpans.length} segments in this video`
            : undefined
        }
        onOpen={openVideo}
        onNavigate={onNavigate}
      />
      {!isDerivedQuery ? (
        <DetailSection title="Raw segments">
          {rawSegmentsQuery.isLoading || loadingOwnRawSegments ? (
            <p className="text-sm text-secondary">Loading raw segments...</p>
          ) : rawSegmentsQuery.error ? (
            <ListLoadError
              error={rawSegmentsQuery.error}
              onRetry={() => {
                void rawSegmentsQuery.refetch();
              }}
              title="Could not load raw segments"
            />
          ) : rawSegments.length > 0 ? (
            <ul className="divide-y divide-border overflow-hidden rounded-lg border border-border">
              {rawSegments.map((segment) => (
                <RawSegmentRow
                  key={segment.id}
                  segment={segment}
                  playing={segment.id === playingRawSegmentId}
                  onPlay={() => seekAbsolute(segment.startSec)}
                  onOpen={() => onNavigate({ page: "segment", id: segment.id })}
                />
              ))}
            </ul>
          ) : (
            <p className="text-sm text-secondary">No raw segments were returned for this segment.</p>
          )}
        </DetailSection>
      ) : null}
      <NearbySection
        groups={[
          { title: "Before", items: spanContext.previous.map(nearbySpan) },
          { title: "After", items: spanContext.next.map(nearbySpan) },
          { title: "Overlapping", items: spanContext.intersecting.map(nearbySpan) },
          {
            title: hasTag
              ? "Next with the same tag"
              : isFaceSpan
                ? "Next with the same face"
                : "Next with the same name",
            items: spanContext.nextSameTag ? [nearbySpan(spanContext.nextSameTag)] : [],
          },
        ]}
      />
      <TechnicalDetails
        entries={[
          ["Display profile", profileQuery.data?.name ?? `Profile ${detail.profileId}`],
          ["Derived", derivedOperator ? formatOperatorLabel(derivedOperator) : undefined],
          ["Segment key", detail.span.spanKey],
        ]}
      />
    </div>
  );

  const similarContent = (
    <SegmentVisualSimilarityPanel
      videoId={detail.videoId}
      intervals={intervals.map((interval) => ({ startSec: interval.startSec, endSec: interval.endSec }))}
      onNavigate={onNavigate}
    />
  );

  const activeContent = activeTab === "similar" ? similarContent : overviewContent;

  return (
    <MediaDetailLayout
      eyebrow={
        <span className="inline-flex items-center gap-1.5">
          <SegmentIcon className="h-3.5 w-3.5" />
          {!isDerivedQuery && detail.span.segmentIds.length > 1
            ? `Segment · ${detail.span.segmentIds.length} raw segments`
            : "Segment"}
          <LoadingNextMarker loading={loadingNext || loadingOwnRawSegments} />
        </span>
      }
      title={
        <span className="inline-flex items-center gap-2">
          {spanKindIcon ? (
            <span aria-hidden className="inline-flex">
              <SegmentKindIcon kind={spanKindIcon} className="h-5 w-5 shrink-0 text-secondary" />
            </span>
          ) : null}
          <SuggestedName name={spanTitle} suggested={spanTitleIsSuggestion} markerClassName="text-xs" />
        </span>
      }
      headerDetails={summaryLine}
      subtitle={`${formatTime(detail.span.startSec)} - ${formatTime(detail.span.endSec)} • ${formatTime(detail.span.endSec - detail.span.startSec)} long`}
      backLabel={backLabel}
      onGoBack={onGoBack}
      backAlternative={backAlternative}
      media={playerMedia}
      mediaAspectRatio="auto"
      mediaFullBleed
      mediaSticky={false}
      tabs={tabs}
      activeTab={activeTab}
      onTabChange={(key) => setActiveTab(key as ResolvedSpanTab)}
      actions={
        <>
          <SegmentHeaderLinks
            videoTitle={detail.videoTitle}
            onOpenVideo={openVideo}
            tagName={hasTag ? detail.span.tagName : undefined}
            onOpenTag={openTag}
          />
          {canCreateSubVideo ? (
            <div className="relative" ref={opsMenuRef}>
              <button
                type="button"
                onClick={() => setShowOpsMenu((current) => !current)}
                className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
                title="Operations"
              >
                <MoreVertical className="h-4 w-4" />
              </button>
              <FloatingActionMenu
                open={showOpsMenu}
                anchorRef={opsMenuRef}
                onClose={() => setShowOpsMenu(false)}
                className="min-w-[190px] py-1"
              >
                <button
                  type="button"
                  onClick={() => {
                    createSubVideoMutation.mutate();
                    setShowOpsMenu(false);
                  }}
                  disabled={createSubVideoMutation.isPending}
                  className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface disabled:opacity-60"
                >
                  <Clapperboard className="h-3.5 w-3.5" />{" "}
                  {createSubVideoMutation.isPending ? "Creating video" : "Make video"}
                </button>
              </FloatingActionMenu>
            </div>
          ) : null}
        </>
      }
    >
      <MediaDetailLayout.Content>{activeContent}</MediaDetailLayout.Content>
    </MediaDetailLayout>
  );
}

function ResolvedSpanSourceDetails({
  detail,
  derivedOperator,
  derivedQueryDescriptor,
  profileName,
  tagNamesById,
  performerNamesById,
  faceLabelsById,
  onNavigate,
}: {
  detail: ResolvedSpanDetail;
  derivedOperator?: SegmentSpanOperator;
  derivedQueryDescriptor?: SegmentDerivedQueryDescriptor;
  profileName?: string;
  tagNamesById: Map<number, string>;
  performerNamesById: Map<number, string>;
  faceLabelsById: Map<number, string>;
  onNavigate: (r: any) => void;
}) {
  const isDerivedQuery = detail.span.spanKey.startsWith("dq-");

  return (
    <div className="flex flex-wrap gap-2 text-xs">
      {isDerivedQuery && derivedQueryDescriptor ? (
        derivedQueryDescriptor.operands.map((operand, index) => {
          const chips: ReactNode[] = [];
          operand.tagIds?.forEach((tagId) =>
            chips.push(
              <TagBadge
                key={`tag-${index}-${tagId}`}
                name={tagNamesById.get(tagId) ?? `Tag #${tagId}`}
                provenance={buildOperandTagProvenance(
                  operand,
                  derivedOperator ?? derivedQueryDescriptor.operator,
                  profileName,
                )}
                onClick={() => onNavigate({ page: "tag", id: tagId })}
              />,
            ),
          );
          operand.performerIds?.forEach((performerId) =>
            chips.push(
              <ProvenanceBadge
                key={`performer-${index}-${performerId}`}
                name={performerNamesById.get(performerId) ?? `Performer #${performerId}`}
                sourceLabel="Performer"
                provenance={buildOperandTagProvenance(
                  operand,
                  derivedOperator ?? derivedQueryDescriptor.operator,
                  profileName,
                )}
                onClick={() => onNavigate({ page: "performer", id: performerId })}
              />,
            ),
          );
          operand.faceIds?.forEach((faceId) =>
            chips.push(
              <ProvenanceBadge
                key={`face-${index}-${faceId}`}
                name={faceLabelsById.get(faceId) ?? `Face #${faceId}`}
                sourceLabel="Face"
                provenance={buildOperandTagProvenance(
                  operand,
                  derivedOperator ?? derivedQueryDescriptor.operator,
                  profileName,
                )}
                onClick={() => onNavigate({ page: "face", id: faceId })}
              />,
            ),
          );

          return chips.length > 0 ? (
            chips
          ) : (
            <SourceChip key={`operand-${index}`}>
              {operand.kind || operand.sourceKey || `Operand ${index + 1}`}
            </SourceChip>
          );
        })
      ) : detail.span.tagId && detail.span.tagName ? (
        <TagBadge
          name={detail.span.tagName}
          provenance={buildSpanTagProvenance(detail, profileName)}
          onClick={() => onNavigate({ page: "tag", id: detail.span.tagId! })}
        />
      ) : (
        <SourceChip>{detail.span.tagName || detail.span.kind || detail.span.sourceKey || "Segment"}</SourceChip>
      )}
    </div>
  );
}

function buildSpanTagProvenance(detail: ResolvedSpanDetail, profileName?: string): TagProvenance[] {
  return [
    {
      sourceKey: detail.span.sourceKey || "resolved-span",
      appliedAt: "",
      contextType: profileName ? `profile:${profileName}` : "profile",
      contextId: detail.profileId,
      totalDurationSec: detail.span.endSec - detail.span.startSec,
    },
  ];
}

function buildOperandTagProvenance(
  operand: SegmentDerivedQueryDescriptor["operands"][number],
  operator: SegmentSpanOperator,
  profileName?: string,
): TagProvenance[] {
  return [
    {
      sourceKey: operand.sourceKey || `derived:${operator}`,
      confidence: operand.minConfidence,
      appliedAt: "",
      contextType: profileName ? `profile:${profileName}` : `derived:${operator}`,
    },
  ];
}

function SourceChip({ children }: { children: ReactNode }) {
  return (
    <span className="rounded-full border border-border bg-card px-2.5 py-1 text-xs text-foreground">{children}</span>
  );
}

function buildSpanContext(
  videoSpans: ResolvedSpan[],
  current: ResolvedSpan,
  followTagId?: number,
  followTagName?: string,
) {
  const currentEnd = current.endSec ?? current.startSec;
  const spans = [...videoSpans.filter((span) => span.spanKey !== current.spanKey), current].sort(
    (left, right) =>
      left.startSec - right.startSec || left.endSec - right.endSec || left.spanKey.localeCompare(right.spanKey),
  );
  const currentIndex = Math.max(
    0,
    spans.findIndex((span) => span.spanKey === current.spanKey),
  );
  const isCurrent = (span: ResolvedSpan) => span.spanKey === current.spanKey;
  const previous = spans.filter((span) => !isCurrent(span) && span.endSec <= current.startSec).slice(-3);
  const next = spans.filter((span) => !isCurrent(span) && span.startSec >= currentEnd).slice(0, 3);
  const intersecting = spans
    .filter((span) => !isCurrent(span) && span.startSec < currentEnd && span.endSec > current.startSec)
    .slice(0, 6);
  const nextSameTag =
    followTagId != null || followTagName
      ? spans
          .slice(currentIndex + 1)
          .find((span) => !isCurrent(span) && spanMatchesCurrentTag(span, followTagId, followTagName))
      : undefined;

  return { previous, next, intersecting, nextSameTag };
}

function spanMatchesCurrentTag(span: ResolvedSpan, followTagId?: number, followTagName?: string) {
  if (followTagId != null) {
    return span.tagId === followTagId;
  }

  return Boolean(followTagName && span.tagName === followTagName);
}

function formatResolvedSpanTitle(span: ResolvedSpan) {
  return span.tagName || span.kind || span.sourceKey || "Segment";
}

function findIntervalIndex(time: number, intervals: ResolvedSpanInterval[]) {
  const containingIndex = intervals.findIndex((interval) => time >= interval.startSec && time <= interval.endSec);
  if (containingIndex >= 0) {
    return containingIndex;
  }

  const nextIndex = intervals.findIndex((interval) => time < interval.startSec);
  if (nextIndex >= 0) {
    return nextIndex;
  }

  return Math.max(0, intervals.length - 1);
}

function clampNumber(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

function parseDerivedOperator(spanKey: string): SegmentSpanOperator | undefined {
  const parts = spanKey.split("-", 4);
  const operator = parts[1];
  return operator === "intersection" || operator === "union" || operator === "difference" ? operator : undefined;
}

function formatOperatorLabel(operator: SegmentSpanOperator) {
  switch (operator) {
    case "intersection":
      return "Intersection";
    case "union":
      return "Union";
    case "difference":
      return "Difference";
    default:
      return operator;
  }
}

function formatTime(value: number) {
  const totalHundredths = Math.max(0, Math.round(value * 100));
  const hours = Math.floor(totalHundredths / 360000);
  const minutes = Math.floor((totalHundredths % 360000) / 6000);
  const seconds = Math.floor((totalHundredths % 6000) / 100);
  const hundredths = totalHundredths % 100;

  if (hundredths === 0) {
    if (hours > 0) {
      return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
    }

    return `${minutes}:${String(seconds).padStart(2, "0")}`;
  }

  const fractional = hundredths % 10 === 0 ? String(Math.floor(hundredths / 10)) : String(hundredths).padStart(2, "0");

  if (hours > 0) {
    return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${fractional}`;
  }

  return `${minutes}:${String(seconds).padStart(2, "0")}.${fractional}`;
}

function RawSegmentRow({
  segment,
  playing,
  onPlay,
  onOpen,
}: {
  segment: SegmentRecord;
  playing: boolean;
  onPlay: () => void;
  onOpen: () => void;
}) {
  const range = `${formatTime(segment.startSec)} - ${formatTime(segment.endSec ?? segment.startSec)}`;
  return (
    <li
      aria-current={playing ? "true" : undefined}
      className={`flex items-center gap-2 px-3 py-2 text-sm ${playing ? "bg-accent/10" : ""}`}
    >
      <button
        type="button"
        onClick={onPlay}
        aria-label={`Play from ${formatTime(segment.startSec)}`}
        className="shrink-0 rounded p-1 text-secondary transition-colors hover:text-accent"
      >
        <Play className="h-3.5 w-3.5" />
      </button>
      <button
        type="button"
        onClick={onOpen}
        className="flex min-w-0 flex-1 items-center justify-between gap-3 text-left hover:text-accent"
      >
        <span className="inline-flex min-w-0 items-center gap-1.5 text-foreground">
          <RawSegmentIcon className="h-3.5 w-3.5 shrink-0 text-secondary" />
          <span className="font-mono text-xs">{range}</span>
        </span>
        <span className="shrink-0 text-xs text-secondary">
          {[formatSourceKeyLabel(segment.sourceKey), formatConfidencePercent(segment.confidence)]
            .filter(Boolean)
            .join(" · ")}
        </span>
      </button>
    </li>
  );
}
