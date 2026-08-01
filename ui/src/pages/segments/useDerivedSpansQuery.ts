import { useQuery } from "@tanstack/react-query";
import { segmentSpans } from "../../api/client";
import type { SegmentDerivedQueryDescriptor, SegmentSpanSearchRequest } from "../../api/types";
import type { RawSegmentFilterValue } from "./rawSegmentFilter";
import type { AppliedDerivedQuery, DerivedSpanItem } from "./types";

interface UseDerivedSpansQueryOptions {
  activeProfileId?: number;
  pageNumber: number;
  perPage: number;
  q: string;
  videoTitle: string;
  videoTagIds: number[];
  videoTagDepth?: -1;
  sort: string;
  direction: "asc" | "desc";
  seed?: number;
  includeVideoIds: number[];
  excludeVideoIds: number[];
  appliedQuery: AppliedDerivedQuery | null;
  derivedQueryDescriptor?: SegmentDerivedQueryDescriptor;
  rawFilter: RawSegmentFilterValue;
  enabled: boolean;
}

/**
 * Builds the spans search/count request body. Shared by the paged query, the infinite query, and the
 * count query so they always send identical filters (only page/sort/direction differ, which the count
 * endpoint ignores).
 */
export function buildSpanSearchRequest(opts: {
  activeProfileId: number;
  pageNumber: number;
  perPage: number;
  q: string;
  videoTitle: string;
  videoTagIds: number[];
  videoTagDepth?: -1;
  sort: string;
  direction: "asc" | "desc";
  seed?: number;
  includeVideoIds: number[];
  excludeVideoIds: number[];
  appliedQuery: AppliedDerivedQuery | null;
  rawFilter: RawSegmentFilterValue;
}): SegmentSpanSearchRequest {
  const { activeProfileId, pageNumber, perPage, q, videoTitle, videoTagIds, videoTagDepth, sort, direction, seed, includeVideoIds, excludeVideoIds, appliedQuery, rawFilter } = opts;
  return {
    profile: activeProfileId,
    derivedQuery: appliedQuery != null ? {
      operator: appliedQuery.operator,
      operands: appliedQuery.operands,
      mergeGapSec: appliedQuery.mergeGapSec,
      minDurationSec: appliedQuery.minDurationSec,
    } : undefined,
    page: pageNumber,
    perPage,
    sort,
    direction,
    seed,
    q: q || undefined,
    videoTitle: videoTitle || undefined,
    videoTagIds: videoTagIds.length > 0 ? videoTagIds : undefined,
    videoTagDepth,
    videoIds: includeVideoIds.length > 0 ? includeVideoIds : undefined,
    excludeVideoIds: excludeVideoIds.length > 0 ? excludeVideoIds : undefined,
    tagIds: rawFilter.tagIds.length > 0 ? rawFilter.tagIds : undefined,
    tagDepth: rawFilter.tagDepth,
    kind: rawFilter.kind,
    sourceKey: rawFilter.sourceKey,
    sourceCategory: rawFilter.sourceCategory,
    title: rawFilter.titleCriterion?.value,
    titleModifier: rawFilter.titleCriterion?.modifier,
    hostType: rawFilter.hostType,
    sourceRunId: rawFilter.sourceRunCriterion?.value,
    sourceRunIdModifier: rawFilter.sourceRunCriterion?.modifier,
    colorHint: rawFilter.colorHintCriterion?.value,
    colorHintModifier: rawFilter.colorHintCriterion?.modifier,
    hasImage: rawFilter.hasImage,
    hasPayload: rawFilter.hasPayload,
    startSec: rawFilter.startSecCriterion?.value,
    startSec2: rawFilter.startSecCriterion?.value2,
    startSecModifier: rawFilter.startSecCriterion?.modifier,
    endSec: rawFilter.endSecCriterion?.value,
    endSec2: rawFilter.endSecCriterion?.value2,
    endSecModifier: rawFilter.endSecCriterion?.modifier,
    createdAt: rawFilter.createdAtCriterion?.value,
    createdAt2: rawFilter.createdAtCriterion?.value2,
    createdAtModifier: rawFilter.createdAtCriterion?.modifier,
    updatedAt: rawFilter.updatedAtCriterion?.value,
    updatedAt2: rawFilter.updatedAtCriterion?.value2,
    updatedAtModifier: rawFilter.updatedAtCriterion?.modifier,
    refIds: rawFilter.faceIds.length > 0 ? rawFilter.faceIds : undefined,
    performerIds: rawFilter.performerIds.length > 0 ? rawFilter.performerIds : undefined,
    confidence: rawFilter.confidenceCriterion?.value,
    confidence2: rawFilter.confidenceCriterion?.value2,
    confidenceModifier: rawFilter.confidenceCriterion?.modifier,
    durationSec: rawFilter.durationCriterion?.value,
    durationSec2: rawFilter.durationCriterion?.value2,
    durationModifier: rawFilter.durationCriterion?.modifier,
  };
}

export function useDerivedSpansQuery(options: UseDerivedSpansQueryOptions) {
  const {
    activeProfileId, pageNumber, perPage, q, videoTitle, videoTagIds, videoTagDepth, sort, direction, seed,
    includeVideoIds, excludeVideoIds, appliedQuery, derivedQueryDescriptor, rawFilter, enabled,
  } = options;
  return useQuery({
    queryKey: [
      "segments-page", "search", activeProfileId, pageNumber, perPage, q, videoTitle, videoTagIds.join(","), videoTagDepth, sort, direction, seed,
      includeVideoIds.join(","), excludeVideoIds.join(","), appliedQuery ?? null, rawFilter,
    ],
    queryFn: async (): Promise<{ items: DerivedSpanItem[]; totalCount: number; hasMore: boolean }> => {
      if (activeProfileId == null) {
        return { items: [], totalCount: 0, hasMore: false };
      }

      const response = await segmentSpans.search(buildSpanSearchRequest({
        activeProfileId, pageNumber, perPage, q, videoTitle, videoTagIds, videoTagDepth, sort, direction, seed,
        includeVideoIds, excludeVideoIds, appliedQuery, rawFilter,
      }));

      return {
        items: response.items.map((item) => ({
          id: `${item.videoId}:${item.span.spanKey}`,
          key: `${item.videoId}:${item.span.spanKey}`,
          kind: derivedQueryDescriptor ? "derivedQuery" : "profile",
          videoId: item.videoId,
          videoTitle: item.videoTitle ?? `Video #${item.videoId}`,
          videoUpdatedAt: item.videoUpdatedAt,
          span: item.span,
          profileId: item.profileId,
          derivedQuery: appliedQuery != null ? {
            operator: appliedQuery.operator,
            operands: appliedQuery.operands,
            mergeGapSec: appliedQuery.mergeGapSec,
            minDurationSec: appliedQuery.minDurationSec,
          } : undefined,
          derivedQueryDescriptor,
        })),
        totalCount: response.totalCount,
        hasMore: response.hasMore ?? false,
      };
    },
    enabled,
    staleTime: 15_000,
  });
}

/**
 * Exact span total for the current filter set, served by the cached spans/count endpoint. Keyed by the
 * filters only (not page/sort/direction), so it's fetched once per filter set and reused across paging.
 */
export function useDerivedSpansCountQuery(options: UseDerivedSpansQueryOptions) {
  const { activeProfileId, q, videoTitle, videoTagIds, videoTagDepth, includeVideoIds, excludeVideoIds, appliedQuery, rawFilter, enabled } = options;
  return useQuery({
    queryKey: [
      "segments-page", "count", activeProfileId, q, videoTitle, videoTagIds.join(","), videoTagDepth,
      includeVideoIds.join(","), excludeVideoIds.join(","), appliedQuery ?? null, rawFilter,
    ],
    queryFn: async (): Promise<number> => {
      if (activeProfileId == null) return 0;
      const response = await segmentSpans.count(buildSpanSearchRequest({
        activeProfileId, pageNumber: 1, perPage: options.perPage, q, videoTitle, videoTagIds, videoTagDepth,
        sort: options.sort, direction: options.direction, seed: options.seed, includeVideoIds, excludeVideoIds, appliedQuery, rawFilter,
      }));
      return response.totalCount;
    },
    enabled: enabled && activeProfileId != null,
    staleTime: 30_000,
  });
}
