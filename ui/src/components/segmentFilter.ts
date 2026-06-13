import type { ResolvedSpan, Segment } from "../api/types";

// Shared filter model for the video segments view. The same filter narrows both the
// segments sidebar list and the player timeline swimlanes, so a user can isolate
// "when does tag X appear" instead of seeing every lane at once.
export interface SegmentFilterState {
  tagIds: number[];
  tagGroupIds: number[];
  // Faces and performers are kept separate so each entity selector renders its own
  // chips, but they match against the same underlying ref ids.
  faceIds: number[];
  performerIds: number[];
  kinds: string[];
  sourceKeys: string[];
}

export const EMPTY_SEGMENT_FILTER: SegmentFilterState = {
  tagIds: [],
  tagGroupIds: [],
  faceIds: [],
  performerIds: [],
  kinds: [],
  sourceKeys: [],
};

export function isSegmentFilterActive(filter: SegmentFilterState): boolean {
  return countActiveFilters(filter) > 0;
}

export function countActiveFilters(filter: SegmentFilterState): number {
  return (
    filter.tagIds.length +
    filter.tagGroupIds.length +
    filter.faceIds.length +
    filter.performerIds.length +
    filter.kinds.length +
    filter.sourceKeys.length
  );
}

type SpanLike = Pick<ResolvedSpan, "kind" | "sourceKey" | "tagId" | "segmentIds">;
type SegmentLike = Pick<Segment, "id" | "kind" | "sourceKey" | "tagId" | "refId" | "performerId">;

export interface SegmentFilterContext {
  rawSegmentsById: Map<number, SegmentLike>;
  // Maps a tag id to the tag group it belongs to (built from the video's tags).
  tagIdToGroupId: Map<number, number>;
}

// Collects the distinct facet values a span carries, pulling from the span itself and
// its underlying raw segments so resolved spans (which may not carry a tagId/refId
// directly) still match correctly.
function collectSpanFacets(span: SpanLike, ctx: SegmentFilterContext) {
  const tagIds = new Set<number>();
  const refIds = new Set<number>();
  const kinds = new Set<string>();
  const sourceKeys = new Set<string>();

  if (span.tagId != null) tagIds.add(span.tagId);
  if (span.kind) kinds.add(span.kind.toLowerCase());
  if (span.sourceKey) sourceKeys.add(span.sourceKey);

  for (const segmentId of span.segmentIds ?? []) {
    const segment = ctx.rawSegmentsById.get(segmentId);
    if (!segment) continue;
    if (segment.tagId != null) tagIds.add(segment.tagId);
    if (segment.refId != null) refIds.add(Number(segment.refId));
    if (segment.performerId != null) refIds.add(Number(segment.performerId));
    if (segment.kind) kinds.add(segment.kind.toLowerCase());
    if (segment.sourceKey) sourceKeys.add(segment.sourceKey);
  }

  return { tagIds, refIds, kinds, sourceKeys };
}

function hasIntersection<T>(selected: T[], present: Set<T>): boolean {
  return selected.some((value) => present.has(value));
}

// A span passes the filter when it satisfies every active category (AND across
// categories), and within a category any one of the selected values matches (OR).
export function matchesSegmentFilter(
  span: SpanLike,
  filter: SegmentFilterState,
  ctx: SegmentFilterContext,
): boolean {
  if (!isSegmentFilterActive(filter)) return true;

  const facets = collectSpanFacets(span, ctx);

  if (filter.tagIds.length > 0 && !hasIntersection(filter.tagIds, facets.tagIds)) {
    return false;
  }

  if (filter.tagGroupIds.length > 0) {
    const groupIds = new Set<number>();
    for (const tagId of facets.tagIds) {
      const groupId = ctx.tagIdToGroupId.get(tagId);
      if (groupId != null) groupIds.add(groupId);
    }
    if (!hasIntersection(filter.tagGroupIds, groupIds)) return false;
  }

  const selectedRefIds = [...filter.faceIds, ...filter.performerIds];
  if (selectedRefIds.length > 0 && !hasIntersection(selectedRefIds, facets.refIds)) {
    return false;
  }

  if (filter.kinds.length > 0) {
    const normalized = filter.kinds.map((kind) => kind.toLowerCase());
    if (!hasIntersection(normalized, facets.kinds)) return false;
  }

  if (filter.sourceKeys.length > 0 && !hasIntersection(filter.sourceKeys, facets.sourceKeys)) {
    return false;
  }

  return true;
}

export interface SegmentFacets {
  kinds: string[];
  sourceKeys: string[];
  tagIds: number[];
  refIds: number[];
}

// Derives the distinct kind / source / tag / ref values actually present across the
// supplied spans so the filter bar only offers values that exist on this video.
export function buildSegmentFacets(
  spans: SpanLike[],
  ctx: SegmentFilterContext,
): SegmentFacets {
  const kinds = new Set<string>();
  const sourceKeys = new Set<string>();
  const tagIds = new Set<number>();
  const refIds = new Set<number>();

  for (const span of spans) {
    const facets = collectSpanFacets(span, ctx);
    facets.kinds.forEach((kind) => kinds.add(kind));
    facets.sourceKeys.forEach((source) => sourceKeys.add(source));
    facets.tagIds.forEach((tagId) => tagIds.add(tagId));
    facets.refIds.forEach((refId) => refIds.add(refId));
  }

  return {
    kinds: Array.from(kinds).sort(),
    sourceKeys: Array.from(sourceKeys).sort(),
    tagIds: Array.from(tagIds),
    refIds: Array.from(refIds),
  };
}
