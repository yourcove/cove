import { type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { Clapperboard, ExternalLink, Info, ListVideo, MoreVertical, Network, Sparkles } from "lucide-react";
import { faces, performers, videos, segmentDisplayProfiles, segmentLibrary, tags } from "../api/client";
import type { Face, ResolvedSpan, ResolvedSpanDetail, ResolvedSpanInterval, SegmentDerivedQueryDescriptor, SegmentSpanOperator, TagProvenance } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canWriteEntity } from "../auth/visibility";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { VideoCard } from "../components/EntityCards";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { ListLoadError } from "../components/ListLoadError";
import { MediaDetailLayout } from "../components/MediaDetailLayout/MediaDetailLayout";
import { SegmentVisualSimilarityPanel, useSegmentVisualSimilarityAvailable } from "../components/VisualSimilarityPanel";
import { VideoPlayer } from "../components/VideoPlayer";
import { ProvenanceBadge, TagBadge } from "../components/shared";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { useAppConfig } from "../state/AppConfigContext";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { buildSubVideoCreate } from "../utils/subVideoCreation";

interface Props {
  videoId: number;
  spanKey: string;
  profileId?: number;
  derivedQueryDescriptor?: SegmentDerivedQueryDescriptor;
  onNavigate: (r: any) => void;
}

type ResolvedSpanTab = "overview" | "context" | "intervals" | "similar";

export function ResolvedSpanPlayPage({ videoId, spanKey, profileId, derivedQueryDescriptor, onNavigate }: Props) {
  const { backLabel, goBack } = useBackNavigation({ page: "video", id: videoId }, onNavigate);
  const { data: detail, isLoading, error: detailError, refetch: retryDetail } = useQuery({
    queryKey: ["video", videoId, "span", spanKey, profileId],
    queryFn: () => videos.segments.spanDetail(videoId, spanKey, profileId),
  });
  const detailLoadError = getLoadError(detail, detailError);

  const title = detail?.span.tagName || detail?.span.kind || detail?.videoTitle || (detail ? `Span ${detail.span.spanKey}` : null);
  useDocumentTitle(title);

  if (isLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  if (isApiNotFoundError(detailLoadError)) {
    return <div className="py-16 text-center text-secondary">Resolved span not found</div>;
  }

  if (detailLoadError) {
    return <ListLoadError error={detailLoadError} onRetry={() => { void retryDetail(); }} title="Could not load segment" className="mx-0 mt-0" />;
  }

  if (!detail) {
    return <div className="py-16 text-center text-secondary">Resolved span not found</div>;
  }

  return (
    <ResolvedSpanPlayerCard
      detail={detail}
      derivedQueryDescriptor={derivedQueryDescriptor}
      onNavigate={onNavigate}
      backLabel={backLabel}
      onGoBack={goBack}
    />
  );
}

function ResolvedSpanPlayerCard({
  detail,
  derivedQueryDescriptor,
  backLabel,
  onGoBack,
  onNavigate,
}: {
  detail: ResolvedSpanDetail;
  derivedQueryDescriptor?: SegmentDerivedQueryDescriptor;
  backLabel: string;
  onGoBack: () => void;
  onNavigate: (r: any) => void;
}) {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const { config } = useAppConfig();
  const [, setCurrentAbsoluteTime] = useState(detail.intervals[0]?.startSec ?? detail.span.startSec);
  const [activeIntervalIndex, setActiveIntervalIndex] = useState(0);
  const [resumeTime, setResumeTime] = useState(detail.intervals[0]?.startSec ?? detail.span.startSec);
  const [autostart, setAutostart] = useState(config?.ui.autostartVideo ?? false);
  const autoplayOnOpenRef = useRef(config?.ui.autostartVideo ?? false);
  const [autostartToken, setAutostartToken] = useState(0);
  const [activeTab, setActiveTab] = useState<ResolvedSpanTab>("overview");
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const opsMenuRef = useRef<HTMLDivElement>(null);

  const intervals = useMemo(
    () => detail.intervals.length > 0 ? detail.intervals : [{ startSec: detail.span.startSec, endSec: detail.span.endSec }],
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
    enabled: !isDerivedQuery && detail.span.segmentIds.length > 0,
    staleTime: 60_000,
  });

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
        map.set(faceId, face.label?.trim() || face.performerName?.trim() || `Face #${faceId}`);
      }
    });
    return map;
  }, [faceIds, faceQueries]);
  const spanFaces = useMemo(
    () => faceQueries.map((query) => query.data).filter((face): face is Face => Boolean(face)),
    [faceQueries],
  );

  const { data: currentVideo, isLoading: currentVideoLoading } = useQuery({
    queryKey: ["video", detail.videoId],
    queryFn: () => videos.get(detail.videoId),
    staleTime: 60_000,
  });
  const currentFile = currentVideo?.files[0];
  const { engagementById: videoEngagement } = useEntityEngagementBatch("video", detail.videoId != null ? [detail.videoId] : []);
  const contextFollowTagId = detail.span.tagId ?? derivedQueryDescriptor?.operands.find((operand) => (operand.tagIds?.length ?? 0) > 0)?.tagIds?.[0];
  const contextFollowTagName = detail.span.tagName ?? (contextFollowTagId != null ? tagNamesById.get(contextFollowTagId) : undefined);
  const spanContext = useMemo(
    () => buildSpanContext(videoSpansQuery.data?.spans ?? [], detail.span, contextFollowTagId, contextFollowTagName),
    [contextFollowTagId, contextFollowTagName, detail.span, videoSpansQuery.data?.spans],
  );

  useEffect(() => {
    autoplayOnOpenRef.current = config?.ui.autostartVideo ?? false;
    setAutostart(autoplayOnOpenRef.current);
  }, [config?.ui.autostartVideo]);

  useEffect(() => {
    const initialStart = intervals[0]?.startSec ?? detail.span.startSec;
    setCurrentAbsoluteTime(initialStart);
    setResumeTime(initialStart);
    setAutostart(autoplayOnOpenRef.current);
    setAutostartToken(0);
    setActiveIntervalIndex(0);
  }, [detail.span.spanKey, detail.span.startSec, intervals]);

  const currentInterval = intervals[activeIntervalIndex] ?? intervals[0];

  const seekAbsolute = useCallback((nextTime: number) => {
    const nextIndex = findIntervalIndex(nextTime, intervals);
    const bounded = clampNumber(nextTime, intervals[nextIndex].startSec, intervals[nextIndex].endSec);
    setActiveIntervalIndex(nextIndex);
    setResumeTime(bounded);
    setCurrentAbsoluteTime(bounded);
  }, [intervals]);

  const advanceInterval = useCallback(() => {
    const nextIndex = activeIntervalIndex + 1;
    if (nextIndex < intervals.length) {
      const nextStart = intervals[nextIndex].startSec;
      setActiveIntervalIndex(nextIndex);
      setResumeTime(nextStart);
      setCurrentAbsoluteTime(nextStart);
      setAutostart(true);
      setAutostartToken((value) => value + 1);
      return;
    }

    setAutostart(false);
    const endTime = intervals[intervals.length - 1]?.endSec ?? detail.span.endSec;
    setResumeTime(endTime);
    setCurrentAbsoluteTime(endTime);
  }, [activeIntervalIndex, detail.span.endSec, intervals]);

  const handlePlayerTimeUpdate = useCallback((nextTime: number) => {
    setCurrentAbsoluteTime(nextTime);
    const nextIndex = findIntervalIndex(nextTime, intervals);
    if (nextIndex !== activeIntervalIndex) {
      setActiveIntervalIndex(nextIndex);
    }
  }, [activeIntervalIndex, intervals]);

  const spanTitle = detail.span.tagName || detail.span.kind || detail.videoTitle || `Span ${detail.span.spanKey}`;
  const canCreateSubVideo = canWriteEntity("video", hasPermission) && !!currentVideo && !!currentFile;
  const createSubVideoMutation = useMutation({
    mutationFn: async () => {
      if (!currentVideo) {
        throw new Error("Video not loaded");
      }

      return videos.createSubVideo(detail.videoId, buildSubVideoCreate(currentVideo, {
        startSec: detail.span.startSec,
        endSec: detail.span.endSec,
      }, {
        title: spanTitle,
      }));
    },
    onSuccess: (newVideo) => {
      queryClient.invalidateQueries({ queryKey: ["videos"] });
      queryClient.invalidateQueries({ queryKey: ["video", detail.videoId] });
      onNavigate({ page: "video", id: newVideo.id });
    },
  });

  const playerMedia = (
    <div className="flex min-h-0 min-w-0 max-w-full flex-1 flex-col overflow-hidden bg-black">
      {currentVideoLoading ? (
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
            segments={rawSegmentsQuery.data ?? []}
            faces={spanFaces}
            captions={currentFile.captions}
            onPlay={() => setAutostart(false)}
            onTimeUpdate={handlePlayerTimeUpdate}
            autostart={autostart}
            autostartToken={autostartToken}
            playbackTracking={{
              hostType: "video",
              hostId: detail.videoId,
              surface: "resolvedSpan",
              scopeKey: `video:${detail.videoId}:span:${detail.span.spanKey}`,
              itemHostType: "video",
              itemHostId: detail.videoId,
              clipStartSec: currentInterval.startSec,
              clipEndSec: currentInterval.endSec,
              context: {
                spanKey: detail.span.spanKey,
                intervalIndex: activeIntervalIndex,
              },
            }}
            onEnded={advanceInterval}
            clip={{ start: currentInterval.startSec, end: currentInterval.endSec, loop: false }}
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

  const tabs = useMemo(() => [
    { key: "overview", label: "Overview", icon: <Info className="h-4 w-4" /> },
    { key: "context", label: "Context", icon: <Network className="h-4 w-4" /> },
    ...(hasVisualSimilarity ? [{ key: "similar", label: "Similar", icon: <Sparkles className="h-4 w-4" /> }] : []),
    { key: "intervals", label: "Intervals", icon: <ListVideo className="h-4 w-4" />, count: intervals.length },
  ], [hasVisualSimilarity, intervals.length]);

  const intervalsContent = (
    <div className="space-y-2">
      <div className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted">Intervals</div>
      <div className="space-y-2">
        {intervals.map((interval, index) => (
          <button
            key={`${interval.startSec}-${interval.endSec}`}
            type="button"
            onClick={() => seekAbsolute(interval.startSec)}
            className={`flex w-full items-center justify-between rounded-xl border px-3 py-2 text-left text-sm transition-colors ${index === activeIntervalIndex ? "border-accent bg-accent/10 text-foreground" : "border-border bg-card/60 text-secondary hover:border-accent"}`}
          >
            <span>Interval {index + 1}</span>
            <span className="font-mono text-xs">{formatTime(interval.startSec)} - {formatTime(interval.endSec)}</span>
          </button>
        ))}
      </div>
    </div>
  );

  const sourceDetailsContent = (
    <ResolvedSpanSourceDetails
      detail={detail}
      derivedOperator={derivedOperator}
      derivedQueryDescriptor={derivedQueryDescriptor}
      profileName={profileQuery.data?.name}
      tagNamesById={tagNamesById}
      performerNamesById={performerNamesById}
      faceLabelsById={faceLabelsById}
      onNavigate={onNavigate}
    />
  );

  const overviewContent = (
    <div className="space-y-4">
      {sourceDetailsContent}
      <div className="grid grid-cols-2 gap-2">
        {buildResolvedSpanSummaryMetrics(detail.span, intervals.length, profileQuery.data?.name ?? `Profile ${detail.profileId}`, derivedOperator).map((metric) => (
          <SummaryMetric key={metric.label} label={metric.label} value={metric.value} />
        ))}
      </div>
      <dl className="mt-4 space-y-2 text-sm text-secondary">
        <div className="flex items-start justify-between gap-3">
          <dt className="text-muted">Range</dt>
          <dd className="text-right text-foreground">{formatTime(detail.span.startSec)} - {formatTime(detail.span.endSec)}</dd>
        </div>
        <div className="flex items-start justify-between gap-3">
          <dt className="text-muted">Profile</dt>
          <dd className="text-right text-foreground">{profileQuery.data?.name ?? `Profile ${detail.profileId}`}</dd>
        </div>
        <div className="flex items-start justify-between gap-3">
          <dt className="text-muted">Intervals</dt>
          <dd className="text-right text-foreground">{intervals.length}</dd>
        </div>
      </dl>
      <div className="mt-5 grid gap-2 sm:grid-cols-2">
        <button
          type="button"
          onClick={() => onNavigate({ page: "video", id: detail.videoId, seekTo: detail.span.startSec })}
          className="w-full rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
        >
          Open video at span start
        </button>
      </div>
    </div>
  );

  const contextContent = (
    <div className="space-y-4">
      {currentVideo ? (
        <div className="max-w-sm">
          <VideoCard video={currentVideo} engagement={videoEngagement.get(detail.videoId)} onClick={() => onNavigate({ page: "video", id: detail.videoId, seekTo: detail.span.startSec })} onNavigate={onNavigate} />
        </div>
      ) : (
        <div className="rounded-xl border border-border bg-card/70 px-3 py-3 text-sm text-secondary">Loading parent video...</div>
      )}
      {!isDerivedQuery ? (
        <div>
          <div className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted">Raw segments in this span</div>
          {rawSegmentsQuery.isLoading ? (
            <div className="rounded-xl border border-border bg-card/70 px-3 py-3">Loading raw segments...</div>
          ) : (rawSegmentsQuery.data ?? []).length > 0 ? (
            <div className="space-y-2">
              {(rawSegmentsQuery.data ?? []).map((segment) => (
                <button
                  key={segment.id}
                  type="button"
                  onClick={() => onNavigate({ page: "segment", id: segment.id })}
                  className="flex w-full items-center justify-between gap-3 rounded-xl border border-border bg-card/70 px-3 py-3 text-left transition-colors hover:border-accent"
                >
                  <div className="min-w-0">
                    <div className="truncate text-sm font-medium text-foreground">#{segment.id} {segment.title?.trim() || segment.tagName || segment.kind || segment.sourceKey}</div>
                    <div className="mt-1 flex flex-wrap gap-2 text-xs text-secondary">
                      {segment.sourceKey ? <span>{segment.sourceKey}</span> : null}
                      {segment.kind ? <span>{segment.kind}</span> : null}
                      {segment.confidence != null ? <span>{segment.confidence.toFixed(2)} conf</span> : null}
                    </div>
                  </div>
                  <div className="shrink-0 text-xs font-mono text-secondary">{formatTime(segment.startSec)} - {formatTime(segment.endSec ?? segment.startSec)}</div>
                </button>
              ))}
            </div>
          ) : (
            <div className="rounded-xl border border-border bg-card/70 px-3 py-3 text-sm text-secondary">No raw segments were returned for this resolved span.</div>
          )}
        </div>
      ) : null}
      <div className="space-y-4">
        {videoSpansQuery.isLoading ? (
          <div className="rounded-xl border border-border bg-card/70 px-3 py-3 text-sm text-secondary">Loading timeline context...</div>
        ) : (
          <>
            <ResolvedSpanContextSection title="Previous Segments" items={spanContext.previous} videoId={detail.videoId} profileId={detail.profileId} onNavigate={onNavigate} emptyMessage="This is the first segment in the video." />
            <ResolvedSpanContextSection title="Next Segments" items={spanContext.next} videoId={detail.videoId} profileId={detail.profileId} onNavigate={onNavigate} emptyMessage="This is the last segment in the video." />
            <ResolvedSpanContextSection title="Intersecting Segments" items={spanContext.intersecting} videoId={detail.videoId} profileId={detail.profileId} onNavigate={onNavigate} emptyMessage="No other segments overlap this time range." />
            <ResolvedSpanContextSection title="Next With Same Tag" items={spanContext.nextSameTag ? [spanContext.nextSameTag] : []} videoId={detail.videoId} profileId={detail.profileId} onNavigate={onNavigate} emptyMessage={contextFollowTagName ? `No later ${contextFollowTagName} segment is in this video.` : "This segment does not have a tag to follow."} compact />
          </>
        )}
      </div>
    </div>
  );

  const similarContent = (
    <SegmentVisualSimilarityPanel
      videoId={detail.videoId}
      intervals={intervals.map((interval) => ({ startSec: interval.startSec, endSec: interval.endSec }))}
      onNavigate={onNavigate}
    />
  );

  const activeContent = activeTab === "intervals"
    ? intervalsContent
    : activeTab === "similar"
      ? similarContent
      : activeTab === "context"
        ? contextContent
        : overviewContent;

  return (
    <MediaDetailLayout
      title={spanTitle}
      subtitle={`${formatTime(detail.span.startSec)} - ${formatTime(detail.span.endSec)} • ${formatTime(detail.span.endSec - detail.span.startSec)} long`}
      backLabel={backLabel}
      onGoBack={onGoBack}
      media={playerMedia}
      mediaAspectRatio="auto"
      mediaFullBleed
      mediaSticky={false}
      tabs={tabs}
      activeTab={activeTab}
      onTabChange={(key) => setActiveTab(key as ResolvedSpanTab)}
      actions={
        <>
          <button
            type="button"
            onClick={() => onNavigate({ page: "video", id: detail.videoId, seekTo: detail.span.startSec })}
            className="inline-flex items-center justify-center rounded p-1 text-secondary transition hover:bg-card hover:text-foreground"
            title="Open parent video"
          >
            <ExternalLink className="h-4 w-4" />
          </button>
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
              <FloatingActionMenu open={showOpsMenu} anchorRef={opsMenuRef} onClose={() => setShowOpsMenu(false)} className="min-w-[190px] py-1">
                  <button
                    type="button"
                    onClick={() => {
                      createSubVideoMutation.mutate();
                      setShowOpsMenu(false);
                    }}
                    disabled={createSubVideoMutation.isPending}
                    className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground transition-colors hover:bg-surface disabled:opacity-60"
                  >
                    <Clapperboard className="h-3.5 w-3.5" /> {createSubVideoMutation.isPending ? "Creating video" : "Make video"}
                  </button>
              </FloatingActionMenu>
            </div>
          ) : null}
        </>
      }
    >
      <MediaDetailLayout.Content>
        {activeContent}
      </MediaDetailLayout.Content>
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
      {isDerivedQuery && derivedQueryDescriptor ? derivedQueryDescriptor.operands.map((operand, index) => {
        const chips: ReactNode[] = [];
        operand.tagIds?.forEach((tagId) => chips.push(
          <TagBadge
            key={`tag-${index}-${tagId}`}
            name={tagNamesById.get(tagId) ?? `Tag #${tagId}`}
            provenance={buildOperandTagProvenance(operand, derivedOperator ?? derivedQueryDescriptor.operator, profileName)}
            onClick={() => onNavigate({ page: "tag", id: tagId })}
          />,
        ));
        operand.performerIds?.forEach((performerId) => chips.push(
          <ProvenanceBadge
            key={`performer-${index}-${performerId}`}
            name={performerNamesById.get(performerId) ?? `Performer #${performerId}`}
            sourceLabel="Performer"
            provenance={buildOperandTagProvenance(operand, derivedOperator ?? derivedQueryDescriptor.operator, profileName)}
            onClick={() => onNavigate({ page: "performer", id: performerId })}
          />,
        ));
        operand.faceIds?.forEach((faceId) => chips.push(
          <ProvenanceBadge
            key={`face-${index}-${faceId}`}
            name={faceLabelsById.get(faceId) ?? `Face #${faceId}`}
            sourceLabel="Face"
            provenance={buildOperandTagProvenance(operand, derivedOperator ?? derivedQueryDescriptor.operator, profileName)}
            onClick={() => onNavigate({ page: "face", id: faceId })}
          />,
        ));

        return chips.length > 0 ? chips : (
          <SourceChip key={`operand-${index}`}>{operand.kind || operand.sourceKey || `Operand ${index + 1}`}</SourceChip>
        );
      }) : (
        detail.span.tagId && detail.span.tagName ? (
          <TagBadge name={detail.span.tagName} provenance={buildSpanTagProvenance(detail, profileName)} onClick={() => onNavigate({ page: "tag", id: detail.span.tagId! })} />
        ) : (
          <SourceChip>{detail.span.tagName || detail.span.kind || detail.span.sourceKey || "Segment"}</SourceChip>
        )
      )}
    </div>
  );
}

function buildSpanTagProvenance(detail: ResolvedSpanDetail, profileName?: string): TagProvenance[] {
  return [{
    sourceKey: detail.span.sourceKey || "resolved-span",
    appliedAt: "",
    contextType: profileName ? `profile:${profileName}` : "profile",
    contextId: detail.profileId,
    totalDurationSec: detail.span.endSec - detail.span.startSec,
  }];
}

function buildOperandTagProvenance(operand: SegmentDerivedQueryDescriptor["operands"][number], operator: SegmentSpanOperator, profileName?: string): TagProvenance[] {
  return [{
    sourceKey: operand.sourceKey || `derived:${operator}`,
    confidence: operand.minConfidence,
    appliedAt: "",
    contextType: profileName ? `profile:${profileName}` : `derived:${operator}`,
  }];
}

function SourceChip({ children }: { children: ReactNode }) {
  return (
    <span className="rounded-full border border-border bg-card px-2.5 py-1 text-xs text-foreground">
      {children}
    </span>
  );
}

function ResolvedSpanContextSection({
  title,
  items,
  videoId,
  profileId,
  onNavigate,
  emptyMessage,
  compact = false,
}: {
  title: string;
  items: ResolvedSpan[];
  videoId: number;
  profileId: number;
  onNavigate: (r: any) => void;
  emptyMessage: string;
  compact?: boolean;
}) {
  return (
    <section className="rounded-xl border border-border bg-card/70 p-3">
      <div className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted">{title}</div>
      {items.length === 0 ? (
        <div className="text-sm text-secondary">{emptyMessage}</div>
      ) : (
        <div className={compact ? "space-y-2" : "grid gap-2 sm:grid-cols-2 xl:grid-cols-3"}>
          {items.map((item) => (
            <button
              key={item.spanKey}
              type="button"
              onClick={() => onNavigate({ page: "video-span", id: videoId, spanKey: item.spanKey, profileId })}
              className="min-w-0 rounded-lg border border-border bg-surface/70 px-3 py-2 text-left transition-colors hover:border-accent/70 hover:bg-surface"
            >
              <div className="truncate text-sm font-medium text-foreground">{formatResolvedSpanTitle(item)}</div>
              <div className="mt-1 text-xs text-secondary">{formatTime(item.startSec)}-{formatTime(item.endSec)}</div>
            </button>
          ))}
        </div>
      )}
    </section>
  );
}

function buildSpanContext(videoSpans: ResolvedSpan[], current: ResolvedSpan, followTagId?: number, followTagName?: string) {
  const currentEnd = current.endSec ?? current.startSec;
  const spans = [...videoSpans.filter((span) => span.spanKey !== current.spanKey), current]
    .sort((left, right) => left.startSec - right.startSec || left.endSec - right.endSec || left.spanKey.localeCompare(right.spanKey));
  const currentIndex = Math.max(0, spans.findIndex((span) => span.spanKey === current.spanKey));
  const isCurrent = (span: ResolvedSpan) => span.spanKey === current.spanKey;
  const previous = spans
    .filter((span) => !isCurrent(span) && span.endSec <= current.startSec)
    .slice(-3);
  const next = spans
    .filter((span) => !isCurrent(span) && span.startSec >= currentEnd)
    .slice(0, 3);
  const intersecting = spans
    .filter((span) => !isCurrent(span) && span.startSec < currentEnd && span.endSec > current.startSec)
    .slice(0, 6);
  const nextSameTag = followTagId != null || followTagName
    ? spans.slice(currentIndex + 1).find((span) => !isCurrent(span) && spanMatchesCurrentTag(span, followTagId, followTagName))
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

function buildResolvedSpanSummaryMetrics(span: ResolvedSpan, intervalCount: number, profileName: string, derivedOperator?: SegmentSpanOperator) {
  const metrics: Array<{ label: string; value: string }> = [
    { label: "Duration", value: formatTime(span.endSec - span.startSec) },
    { label: "Intervals", value: `${intervalCount}` },
  ];

  if (span.tagName) {
    metrics.push({ label: "Tag", value: span.tagName });
  }

  if (span.kind) {
    metrics.push({ label: "Type", value: span.kind });
  }

  if (derivedOperator) {
    metrics.push({ label: "Derived", value: formatOperatorLabel(derivedOperator) });
  }

  metrics.push({ label: "Profile", value: profileName });
  return metrics;
}

function SummaryMetric({ label, value }: { label: string; value: string }) {
  return (
    <div className="border-t border-border/70 py-3 first:border-t-0 sm:border-l sm:border-t-0 sm:px-3 sm:first:border-l-0">
      <div className="text-[11px] font-semibold uppercase tracking-wide text-muted">{label}</div>
      <div className="mt-1 text-sm font-medium text-foreground">{value}</div>
    </div>
  );
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
  return operator === "intersection" || operator === "union" || operator === "difference"
    ? operator
    : undefined;
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

  const fractional = hundredths % 10 === 0
    ? String(Math.floor(hundredths / 10))
    : String(hundredths).padStart(2, "0");

  if (hours > 0) {
    return `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${fractional}`;
  }

  return `${minutes}:${String(seconds).padStart(2, "0")}.${fractional}`;
}
