import type { Detection, Face, ResolvedSpan, Segment } from "../../api/types";
import type { ShownName } from "../segments/segmentDisplayUtils";
import { facePerson, spanPerson } from "./segmentPeople";

/** A bar on the video page's timeline overlays. */
export type TimelineOverlayItem = {
  key: string;
  startSec: number;
  endSec: number;
  label: string;
  /** The segment the bar draws, when it draws one rather than a run of detections. */
  spanKey?: string;
  /** The label is a face's suggested performer, not one it is linked to; shown in italics and said in its title. */
  suggested?: boolean;
  colorHint?: string | null;
  colorSeed?: string;
};

/** A timeline bar's label from the shared segment naming. */
export function timelineName(shown: ShownName) {
  return { label: shown.name, suggested: shown.suggested };
}

export function isFaceTimelineSegment(segment: Pick<Segment, "title" | "kind" | "sourceKey">) {
  const normalizedKind = segment.kind?.trim().toLowerCase() ?? "";
  const normalizedSource = segment.sourceKey?.trim().toLowerCase() ?? "";
  const normalizedTitle = segment.title?.trim().toLowerCase() ?? "";
  return normalizedKind === "face" || normalizedSource.includes("face") || normalizedTitle.startsWith("face-");
}

export function isFaceResolvedSpan(
  span: Pick<ResolvedSpan, "kind" | "sourceKey" | "tagName" | "segmentIds">,
  rawSegmentsById: ReadonlyMap<number, Pick<Segment, "id" | "title" | "kind" | "sourceKey" | "refId">>,
) {
  if (isFaceTimelineSegment({ title: span.tagName ?? undefined, kind: span.kind, sourceKey: span.sourceKey ?? "" })) {
    return true;
  }

  const segmentIds = span.segmentIds ?? [];
  return (
    segmentIds.length > 0 &&
    segmentIds.every((segmentId) => {
      const segment = rawSegmentsById.get(segmentId);
      return segment ? isFaceTimelineSegment(segment) : false;
    })
  );
}

// Detections of one face closer together than this read as one appearance when a face has no segments.
const DETECTION_MERGE_GAP_SEC = 2.5;

/**
 * The bars of the timeline's face lanes. A face with face segments is drawn from the segments of the display profile
 * chosen for the video, like every other lane: joined segments by default, one bar per raw segment with the Raw
 * profile. Runs of detections only stand in for faces without any face segments, even when the profile hides all of
 * a face's segments.
 */
export function buildFaceTimelineItems({
  spans,
  rawSegments,
  rawSegmentsById,
  detections,
  facesById,
  includeFace,
  filterActive,
  personColors,
}: {
  spans: Pick<
    ResolvedSpan,
    "spanKey" | "startSec" | "endSec" | "tagId" | "tagName" | "kind" | "sourceKey" | "segmentIds"
  >[];
  rawSegments: Segment[];
  rawSegmentsById: ReadonlyMap<number, Segment>;
  detections: Pick<Detection, "observedAtSec" | "refKind" | "refId">[];
  facesById: ReadonlyMap<number, Face>;
  /** Whether the segment filter lets this face through. */
  includeFace: (faceId: number, performerId?: number | null) => boolean;
  /** A filter is applied, so face segments whose face is unknown are left out. */
  filterActive: boolean;
  /** Each person's colour, shared with the Segments tab's groups. */
  personColors?: ReadonlyMap<string, string>;
}): TimelineOverlayItem[] {
  const items: TimelineOverlayItem[] = [];
  const segmentFaceIds = new Set<number>();
  for (const segment of rawSegments) {
    if (isFaceTimelineSegment(segment) && segment.refId != null) segmentFaceIds.add(Number(segment.refId));
  }

  for (const span of spans) {
    if (!isFaceResolvedSpan(span, rawSegmentsById)) continue;
    const first = (span.segmentIds ?? []).map((id) => rawSegmentsById.get(id)).find((segment) => segment != null);
    const faceId = first?.refId != null ? Number(first.refId) : undefined;
    const face = faceId == null ? undefined : facesById.get(faceId);
    if (faceId != null && !includeFace(faceId, face?.performerId)) continue;
    if (faceId == null && filterActive) continue;
    // A face bar carries the person it shows, in the colour of the person's group in the Segments tab.
    const person = spanPerson(span, rawSegmentsById, facesById);
    items.push({
      key: `face-span-${span.spanKey}`,
      spanKey: span.spanKey,
      startSec: span.startSec,
      endSec: Math.max(span.endSec, span.startSec + 0.4),
      ...timelineName(person),
      colorHint: personColors?.get(person.key),
    });
  }

  const buckets = new Map<number, number[]>();
  for (const detection of detections) {
    const faceId = detection.refId;
    if (faceId == null || detection.refKind?.toLowerCase() !== "face" || segmentFaceIds.has(faceId)) continue;
    if (detection.observedAtSec == null) continue;
    if (!includeFace(faceId, facesById.get(faceId)?.performerId)) continue;
    const times = buckets.get(faceId) ?? [];
    times.push(detection.observedAtSec);
    buckets.set(faceId, times);
  }

  for (const [faceId, times] of buckets.entries()) {
    times.sort((left, right) => left - right);
    const person = facePerson(faceId, facesById.get(faceId));
    let windowStart = times[0];
    let windowEnd = times[0];
    let runIndex = 0;
    const flush = () => {
      items.push({
        key: `face-${faceId}-${runIndex++}`,
        startSec: windowStart,
        endSec: Math.max(windowEnd, windowStart + 0.4),
        ...timelineName(person),
        colorHint: personColors?.get(person.key),
      });
    };
    for (let index = 1; index < times.length; index++) {
      const time = times[index];
      if (time - windowEnd <= DETECTION_MERGE_GAP_SEC) {
        windowEnd = time;
      } else {
        flush();
        windowStart = time;
        windowEnd = time;
      }
    }
    flush();
  }

  return items;
}
