import { useEffect, useMemo, useRef, useState } from "react";
import { ChevronDown, ChevronLeft, ChevronRight, Eye, EyeOff, Filter, X } from "lucide-react";
import type { Detection, Face, ResolvedSpan, Segment } from "../../api/types";
import {
  type SegmentFilterContext,
  type SegmentFilterState,
  EMPTY_SEGMENT_FILTER,
  isSegmentFilterActive,
  matchesSegmentFilter,
} from "../../components/segmentFilter";
import { usePersistedFlag } from "../../hooks/usePersistedFlag";
import { findSpriteIndex, spriteTileStyle, type VideoSprites } from "../../hooks/useVideoSprites";
import { formatShownTitle, resolveSpanName, segmentFaceName } from "../segments/segmentDisplayUtils";
import {
  buildFaceTimelineItems,
  isFaceResolvedSpan,
  isFaceTimelineSegment,
  timelineName,
  type TimelineOverlayItem,
} from "./timelineFaces";

const SEGMENT_TIMELINE_COLORS = [
  "#a87a2d",
  "#4c6faa",
  "#7963a1",
  "#a05f7b",
  "#3f7f6e",
  "#4b7f8e",
  "#748a37",
  "#a35d4e",
];
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

// Video Scrubber / Timeline Component
export function VideoScrubber({
  sprites,
  duration,
  spans,
  rawSegments,
  detections,
  facesById,
  personColors,
  onSeek,
  currentTime,
  profileName,
  filter = EMPTY_SEGMENT_FILTER,
  filterContext,
  onClearFilter,
  highlight,
  activeSpanKey,
  onOpenSpan,
}: {
  sprites: VideoSprites | null;
  duration: number;
  spans: Pick<
    ResolvedSpan,
    "spanKey" | "startSec" | "endSec" | "tagId" | "tagName" | "kind" | "colorHint" | "sourceKey" | "lane" | "segmentIds"
  >[];
  rawSegments: Segment[];
  detections: Pick<Detection, "id" | "observedAtSec" | "class" | "score" | "refKind" | "refId">[];
  /** The video's faces by id, which name face segments and detections. */
  facesById: ReadonlyMap<number, Face>;
  /** Each person's colour, shared with the Segments tab's groups. */
  personColors: ReadonlyMap<string, string>;
  onSeek?: (time: number) => void;
  currentTime?: number;
  profileName?: string;
  filter?: SegmentFilterState;
  filterContext?: SegmentFilterContext;
  onClearFilter?: () => void;
  /** A stretch of the video to mark on every lane, such as the segment a page is about. */
  highlight?: { startSec: number; endSec: number };
  /** The segment a page is about, outlined among the bars. */
  activeSpanKey?: string;
  /** Opens a segment from its bar; without it a bar seeks to the segment's start. */
  onOpenSpan?: (spanKey: string) => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const [showAllResolvedLanes, setShowAllResolvedLanes] = useState(false);
  const [showAllFaceLanes, setShowAllFaceLanes] = useState(false);
  const [overlaysCollapsed, setOverlaysCollapsed] = usePersistedFlag("cove.timeline.overlaysCollapsed", false);
  // Faces start out shown when the video has faces with a name or a suggested match, until the viewer chooses.
  const hasNamedFaces = useMemo(
    () => Array.from(facesById.values()).some((face) => segmentFaceName(face) != null),
    [facesById],
  );
  const [facesEnabled, setFacesEnabled] = usePersistedFlag("cove.timeline.facesEnabled", hasNamedFaces);

  const formatTime = (s: number) => {
    const m = Math.floor(s / 60);
    const sec = Math.floor(s % 60);
    return `${m}:${sec.toString().padStart(2, "0")}`;
  };

  const thumbWidth = 160;
  const thumbHeight = sprites ? Math.round(thumbWidth * (sprites.entries[0].h / sprites.entries[0].w)) : 0;
  const rawSegmentsById = useMemo(() => new Map(rawSegments.map((segment) => [segment.id, segment])), [rawSegments]);
  const filterActive = isSegmentFilterActive(filter);
  const effectiveFilterContext = useMemo(
    () => filterContext ?? { rawSegmentsById, tagIdToGroupId: new Map<number, number>() },
    [filterContext, rawSegmentsById],
  );
  const nonFaceSpans = useMemo(
    () =>
      spans.filter(
        (span) =>
          !isFaceResolvedSpan(span, rawSegmentsById) && matchesSegmentFilter(span, filter, effectiveFilterContext),
      ),
    [rawSegmentsById, spans, filter, effectiveFilterContext],
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
  const faceFilterTargetsFaces =
    filterActive &&
    filter.tagIds.length === 0 &&
    filter.tagGroupIds.length === 0 &&
    (filter.kinds.map((kind) => kind.toLowerCase()).includes("face") ||
      filter.faceIds.length > 0 ||
      filter.performerIds.length > 0);
  const effectiveFacesEnabled = facesEnabled || faceFilterTargetsFaces;
  const segmentLanes = useMemo(
    () =>
      buildTimelineLanes<TimelineOverlayItem>(
        nonFaceSpans.map((span) => ({
          key: span.spanKey,
          spanKey: span.spanKey,
          startSec: span.startSec,
          endSec: span.endSec,
          ...timelineName(resolveSpanName(span, rawSegmentsById, facesById)),
          colorHint: span.colorHint,
          colorSeed: `${span.kind ?? "span"}:${span.tagName ?? ""}:${span.sourceKey ?? ""}`,
        })),
      ),
    [facesById, nonFaceSpans, rawSegmentsById],
  );
  const faceLanes = useMemo(() => {
    if (!effectiveFacesEnabled) return [] as ReturnType<typeof buildTimelineLanes<TimelineOverlayItem>>;
    const items = buildFaceTimelineItems({
      spans,
      rawSegments,
      rawSegmentsById,
      detections,
      facesById,
      includeFace: faceFilterPredicate,
      filterActive,
      personColors,
    });
    return buildTimelineLanes(items);
  }, [
    detections,
    spans,
    rawSegments,
    rawSegmentsById,
    facesById,
    effectiveFacesEnabled,
    faceFilterPredicate,
    filterActive,
    personColors,
  ]);
  // Detection dots respect the face/performer filter; when a non-face filter is active they are hidden.
  const visibleDetections = useMemo(
    () =>
      detections.filter((det) =>
        det.refId == null || det.refKind?.toLowerCase() !== "face"
          ? !filterActive
          : faceFilterPredicate(det.refId, null),
      ),
    [detections, filterActive, faceFilterPredicate],
  );
  const visibleResolvedLanes = showAllResolvedLanes ? segmentLanes : segmentLanes.slice(0, 4);
  const visibleFaceLanes = showAllFaceLanes ? faceLanes : faceLanes.slice(0, 2);
  const hiddenResolvedLaneCount = Math.max(0, segmentLanes.length - visibleResolvedLanes.length);
  const hiddenFaceLaneCount = Math.max(0, faceLanes.length - visibleFaceLanes.length);
  const hasFaceDetections = useMemo(
    () =>
      detections.some((det) => det.refKind?.toLowerCase() === "face" && det.refId != null) ||
      rawSegments.some((segment) => isFaceTimelineSegment(segment)),
    [detections, rawSegments],
  );

  // Determine which thumbnail index is active based on current video time
  const activeIndex = useMemo(() => {
    if (currentTime == null || currentTime <= 0 || !sprites) return -1;
    return findSpriteIndex(sprites.entries, currentTime);
  }, [currentTime, sprites]);

  // Auto-scroll to active thumbnail
  useEffect(() => {
    if (activeIndex >= 0 && scrollRef.current) {
      const targetLeft = activeIndex * thumbWidth;
      const { scrollLeft, clientWidth } = scrollRef.current;
      if (targetLeft < scrollLeft || targetLeft + thumbWidth > scrollLeft + clientWidth) {
        scrollRef.current.scrollTo({
          left: Math.max(0, targetLeft - clientWidth / 2 + thumbWidth / 2),
          behavior: "smooth",
        });
      }
    }
  }, [activeIndex, thumbWidth]);

  const scroll = (dir: number) => {
    if (scrollRef.current) scrollRef.current.scrollBy({ left: dir * thumbWidth * 4, behavior: "smooth" });
  };
  const timelineDuration = Math.max(0.001, duration || 0);

  return (
    <div className="flex-shrink-0 bg-[#1a1a1a] border-t border-border">
      {(spans.length > 0 || hasFaceDetections) && (
        <div className="border-b border-black/20 bg-[#181a20]">
          <div className="flex flex-wrap items-center justify-between gap-2 border-b border-white/10 bg-[#20222a] px-2 py-1.5 pr-8 text-[10px] text-white/65">
            <div className="flex min-w-0 flex-wrap items-center gap-1.5">
              <span className="font-semibold uppercase tracking-[0.16em] text-white/70">Timeline overlays</span>
              {nonFaceSpans.length > 0 ? (
                <span className="rounded border border-white/10 bg-white/[0.04] px-1.5 py-0.5">
                  {nonFaceSpans.length} segment{nonFaceSpans.length === 1 ? "" : "s"}
                </span>
              ) : null}
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
          {!overlaysCollapsed ? (
            <div className="space-y-2 px-2 py-2">
              {/* People come first, as in the Segments tab. */}
              {hasFaceDetections && effectiveFacesEnabled && faceLanes.length > 0 ? (
                <div className="space-y-1">
                  <div className="flex items-center justify-between text-[10px] uppercase tracking-[0.14em] text-white/45">
                    <span>Faces</span>
                    {hiddenFaceLaneCount > 0 ? <span>{hiddenFaceLaneCount} hidden</span> : null}
                  </div>
                  <TimelineLaneBox
                    lanes={visibleFaceLanes}
                    palette={FACE_TIMELINE_COLORS}
                    duration={timelineDuration}
                    highlight={highlight}
                    activeSpanKey={activeSpanKey}
                    onSeek={onSeek}
                    onOpenSpan={onOpenSpan}
                  />
                </div>
              ) : null}
              {nonFaceSpans.length > 0 ? (
                <div className="space-y-1">
                  <div className="flex items-center justify-between text-[10px] uppercase tracking-[0.14em] text-white/45">
                    <span>Segments{profileName ? ` · ${profileName}` : ""}</span>
                    {hiddenResolvedLaneCount > 0 ? <span>{hiddenResolvedLaneCount} hidden</span> : null}
                  </div>
                  <TimelineLaneBox
                    lanes={visibleResolvedLanes}
                    palette={SEGMENT_TIMELINE_COLORS}
                    duration={timelineDuration}
                    highlight={highlight}
                    activeSpanKey={activeSpanKey}
                    onSeek={onSeek}
                    onOpenSpan={onOpenSpan}
                  />
                </div>
              ) : null}
            </div>
          ) : null}
        </div>
      )}
      {visibleDetections.length > 0 && (
        <div className="relative h-5 border-b border-black/20 bg-[#1f2c35]">
          {highlight ? <TimelineHighlight highlight={highlight} duration={timelineDuration} /> : null}
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

      {sprites ? (
        <div className="relative flex overflow-hidden" ref={containerRef} data-testid="video-sprite-strip">
          <button
            onClick={() => scroll(-1)}
            className="flex-shrink-0 w-7 bg-[#222] hover:bg-[#333] text-muted border-r border-border z-10"
          >
            <ChevronLeft className="w-4 h-4 mx-auto" />
          </button>

          <div ref={scrollRef} className="flex-1 flex overflow-x-auto scrollbar-thin scrollbar-thumb-border">
            {sprites.entries.map((entry, i) => {
              const time = entry.start;
              const isActive = i === activeIndex;
              return (
                <div
                  key={i}
                  className={`flex-shrink-0 relative cursor-pointer hover:ring-2 hover:ring-accent hover:z-10 ${isActive ? "ring-2 ring-accent z-10" : ""}`}
                  style={{ width: thumbWidth }}
                  onClick={() => onSeek?.(time)}
                >
                  <div className="bg-surface" style={{ width: thumbWidth, height: thumbHeight }}>
                    <div style={spriteTileStyle(sprites, entry, thumbWidth)} />
                  </div>
                  <div className="absolute bottom-0 left-0 right-0 text-center text-[10px] text-white bg-black/70 py-0.5">
                    {formatTime(time)}
                  </div>
                </div>
              );
            })}
          </div>

          <button
            onClick={() => scroll(1)}
            className="flex-shrink-0 w-7 bg-[#222] hover:bg-[#333] text-muted border-l border-border z-10"
          >
            <ChevronRight className="w-4 h-4 mx-auto" />
          </button>
        </div>
      ) : null}
    </div>
  );
}

function buildTimelineLanes<T extends { key: string; startSec: number; endSec: number }>(items: T[]) {
  const ordered = [...items].sort(
    (left, right) => left.startSec - right.startSec || left.endSec - right.endSec || left.key.localeCompare(right.key),
  );
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

function formatTimelineTime(seconds: number) {
  const mins = Math.floor(seconds / 60);
  const secs = Math.floor(seconds % 60);
  const fractional = seconds % 1;

  if (fractional > 0) {
    return `${mins}:${secs.toString().padStart(2, "0")}.${Math.round(fractional * 10)}`;
  }

  return `${mins}:${secs.toString().padStart(2, "0")}`;
}

const clampPercent = (value: number) => Math.min(100, Math.max(0, value));

// The marked stretch of the video, drawn behind a lane's bars.
function TimelineHighlight({
  highlight,
  duration,
}: {
  highlight: { startSec: number; endSec: number };
  duration: number;
}) {
  const start = clampPercent((highlight.startSec / duration) * 100);
  const end = clampPercent((highlight.endSec / duration) * 100);
  return (
    <div
      aria-hidden
      data-testid="timeline-highlight"
      className="pointer-events-none absolute inset-y-0 border-x-2 border-accent bg-accent/25"
      style={{ left: `${start}%`, width: `${Math.max(0.3, end - start)}%` }}
    />
  );
}

function TimelineLaneBox({
  lanes,
  palette,
  duration,
  highlight,
  activeSpanKey,
  onSeek,
  onOpenSpan,
}: {
  lanes: Array<Array<{ item: TimelineOverlayItem; endSec: number }>>;
  palette: string[];
  duration: number;
  highlight?: { startSec: number; endSec: number };
  activeSpanKey?: string;
  onSeek?: (time: number) => void;
  onOpenSpan?: (spanKey: string) => void;
}) {
  return (
    <div
      className="relative overflow-hidden rounded border border-white/10 bg-black/25"
      style={{ height: `${Math.max(28, lanes.length * 24 + 6)}px` }}
    >
      {highlight ? <TimelineHighlight highlight={highlight} duration={duration} /> : null}
      {lanes.map((lane, laneIndex) =>
        lane.map(({ item, endSec }) => {
          const start = clampPercent((item.startSec / duration) * 100);
          const end = clampPercent(((endSec + 0.001) / duration) * 100);
          const width = Math.max(0.45, end - start);
          const color = getTimelineOverlayColor(item, palette);
          const active = activeSpanKey != null && item.spanKey === activeSpanKey;
          const opens = onOpenSpan != null && item.spanKey != null && !active;

          return (
            <button
              key={item.key}
              type="button"
              aria-current={active ? "true" : undefined}
              className={`absolute h-5 overflow-hidden rounded-sm px-1.5 text-left text-[10px] font-semibold leading-5 text-white shadow-sm transition hover:brightness-110 focus:outline-none focus:ring-1 focus:ring-white/70`}
              style={{
                left: `${start}%`,
                top: `${laneIndex * 24 + 4}px`,
                width: `${width}%`,
                backgroundColor: color,
                // The open segment's bar is outlined. The outline is part of the inline shadow, which a ring class
                // would not override.
                boxShadow: active
                  ? "inset 0 0 0 1px rgba(255,255,255,0.2), 0 0 0 2px #fff"
                  : "inset 0 0 0 1px rgba(255,255,255,0.2)",
                zIndex: active ? 1 : undefined,
              }}
              title={`${formatShownTitle(item.label, item.suggested)} (${formatTimelineTime(item.startSec)} - ${formatTimelineTime(endSec)})${opens ? " — open segment" : ""}`}
              onClick={() => (opens ? onOpenSpan(item.spanKey!) : onSeek?.(item.startSec))}
            >
              {timelineLabelFits(width, item.label) ? (
                <span className={`block truncate ${item.suggested ? "italic" : ""}`}>{item.label}</span>
              ) : null}
            </button>
          );
        }),
      )}
    </div>
  );
}
