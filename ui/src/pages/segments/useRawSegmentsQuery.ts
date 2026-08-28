import { useQuery } from "@tanstack/react-query";
import { segmentLibrary } from "../../api/client";
import type { RawSegmentItem } from "./types";
import type { RawSegmentFilterValue } from "./rawSegmentFilter";

interface UseRawSegmentsQueryOptions {
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
  rawSegmentIds: number[];
  rawFilter: RawSegmentFilterValue;
  enabled: boolean;
  includeAggregate?: boolean;
}

type RawSegmentListOptions = NonNullable<Parameters<typeof segmentLibrary.list>[0]>;

export function buildRawSegmentListOptions({
  pageNumber,
  perPage,
  q,
  videoTitle,
  videoTagIds,
  videoTagDepth,
  sort,
  direction,
  seed,
  includeVideoIds,
  excludeVideoIds,
  rawSegmentIds,
  rawFilter,
  includeAggregate = false,
}: Omit<UseRawSegmentsQueryOptions, "enabled">): RawSegmentListOptions {
  return {
    q: q || undefined,
    ids: rawSegmentIds.length > 0 ? rawSegmentIds.join(",") : undefined,
    videoIds: includeVideoIds.length > 0 ? includeVideoIds.join(",") : undefined,
    excludeVideoIds: excludeVideoIds.length > 0 ? excludeVideoIds.join(",") : undefined,
    videoTitle: videoTitle || undefined,
    videoTagIds: videoTagIds.length > 0 ? videoTagIds.join(",") : undefined,
    videoTagDepth,
    tagIds: rawFilter.tagIds.length > 0 ? rawFilter.tagIds.join(",") : undefined,
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
    refIds: rawFilter.faceIds.length > 0 ? rawFilter.faceIds.join(",") : undefined,
    performerIds: rawFilter.performerIds.length > 0 ? rawFilter.performerIds.join(",") : undefined,
    minConfidence: rawFilter.minConfidence,
    minDurationSec: rawFilter.minDurationSec,
    confidence: rawFilter.confidenceCriterion?.value,
    confidence2: rawFilter.confidenceCriterion?.value2,
    confidenceModifier: rawFilter.confidenceCriterion?.modifier,
    durationSec: rawFilter.durationCriterion?.value,
    durationSec2: rawFilter.durationCriterion?.value2,
    durationModifier: rawFilter.durationCriterion?.modifier,
    sort,
    direction,
    seed,
    page: pageNumber,
    perPage,
    includeAggregate,
  };
}

export function useRawSegmentsQuery({
  pageNumber,
  perPage,
  q,
  videoTitle,
  videoTagIds,
  videoTagDepth,
  sort,
  direction,
  seed,
  includeVideoIds,
  excludeVideoIds,
  rawSegmentIds,
  rawFilter,
  enabled,
  includeAggregate = false,
}: UseRawSegmentsQueryOptions) {
  return useQuery({
    queryKey: [
      "segments-page",
      "raw",
      pageNumber,
      perPage,
      q,
      videoTitle,
      videoTagIds.join(","),
      videoTagDepth,
      sort,
      direction,
      seed,
      includeVideoIds.join(","),
      excludeVideoIds.join(","),
      rawSegmentIds.join(","),
      rawFilter,
      includeAggregate,
    ],
    queryFn: async (): Promise<{ items: RawSegmentItem[]; totalCount: number; duration: number }> => {
      const response = await segmentLibrary.list(buildRawSegmentListOptions({
        pageNumber,
        perPage,
        q,
        videoTitle,
        videoTagIds,
        videoTagDepth,
        sort,
        direction,
        seed,
        includeVideoIds,
        excludeVideoIds,
        rawSegmentIds,
        rawFilter,
        includeAggregate,
      }));

      return {
        items: response.items.map((item) => ({
          ...item,
          key: `segment:${item.id}`,
          videoId: item.hostId,
          videoTitle: item.hostTitle?.trim() || `Video #${item.hostId}`,
        })),
        totalCount: response.totalCount,
        duration: response.aggregateDuration ?? 0,
      };
    },
    enabled,
    staleTime: 15_000,
  });
}
