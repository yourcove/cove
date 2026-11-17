import { useMemo } from "react";
import { useQueries, useQuery } from "@tanstack/react-query";
import { faces, segmentDisplayProfiles, videos } from "../../api/client";
import type { FindFilter } from "../../api/types";
import {
  buildAppliedDerivedQuery,
  buildDerivedQueryDescriptor,
  isDerivedSpanQueryFilterActive,
  readDerivedSpanQueryFilter,
} from "./derivedQueryCriterion";
import { readRawSegmentFilter } from "./rawSegmentFilter";
import {
  readBoolCriterion,
  readMultiIdCriterionDepth,
  readMultiIdCriterionIds,
  readNumberCriterion,
  readStringCriterion,
  readStringCriterionValue,
  readTimestampCriterion,
  readVideoSelectionCriterion,
} from "./segmentCriteriaDefinitions";
import { dedupeSegmentDisplayProfiles } from "./segmentDisplayProfiles";

/**
 * What the Segments page's list filter means as request inputs: the criteria read from the URL's object
 * filter, the derived query with its performers resolved to their faces, and whether the inputs are ready
 * to query. The Segments page lists with these and segment playback plays with them, so a playback opened
 * from the page's URL parameters plays exactly what the page lists.
 */
export function useSegmentsQueryInputs({
  filter,
  objectFilter,
  isRawView,
  activeProfileId,
}: {
  filter: FindFilter;
  objectFilter: Record<string, unknown>;
  isRawView: boolean;
  activeProfileId: number | undefined;
}) {
  const videoTitle = readStringCriterion(objectFilter.videoTitleCriterion);
  const videoSelection = readVideoSelectionCriterion(objectFilter.videosCriterion);
  const videoTagIds = useMemo(
    () => readMultiIdCriterionIds(objectFilter.videoTagsCriterion),
    [objectFilter.videoTagsCriterion],
  );
  const videoTagDepth = useMemo(
    () => readMultiIdCriterionDepth(objectFilter.videoTagsCriterion),
    [objectFilter.videoTagsCriterion],
  );
  const derivedSpanQueryFilter = useMemo(
    () => readDerivedSpanQueryFilter(objectFilter.derivedSpanQuery),
    [objectFilter.derivedSpanQuery],
  );
  const rawSegmentFilter = useMemo(
    () => readRawSegmentFilter(objectFilter.rawSegmentFilters),
    [objectFilter.rawSegmentFilters],
  );
  const rawTagIds = useMemo(
    () => readMultiIdCriterionIds(objectFilter.rawTagsCriterion),
    [objectFilter.rawTagsCriterion],
  );
  const rawTagDepth = useMemo(
    () => readMultiIdCriterionDepth(objectFilter.rawTagsCriterion),
    [objectFilter.rawTagsCriterion],
  );
  const rawPerformerIds = useMemo(
    () => readMultiIdCriterionIds(objectFilter.rawPerformersCriterion),
    [objectFilter.rawPerformersCriterion],
  );
  const rawFaceIds = useMemo(
    () => readMultiIdCriterionIds(objectFilter.rawFacesCriterion),
    [objectFilter.rawFacesCriterion],
  );
  const rawKind = readStringCriterion(objectFilter.rawKindCriterion);
  const rawSourceKey = readStringCriterion(objectFilter.rawSourceCriterion);
  const rawTitle = readStringCriterionValue(objectFilter.rawTitleCriterion);
  const rawHostType = readStringCriterion(objectFilter.rawHostTypeCriterion);
  const rawSourceRun = readStringCriterionValue(objectFilter.rawSourceRunCriterion);
  const rawColorHint = readStringCriterionValue(objectFilter.rawColorHintCriterion);
  const rawHasImage = readBoolCriterion(objectFilter.rawHasImageCriterion);
  const rawHasPayload = readBoolCriterion(objectFilter.rawHasPayloadCriterion);
  const rawCreatedAt = readTimestampCriterion(objectFilter.rawCreatedAtCriterion);
  const rawUpdatedAt = readTimestampCriterion(objectFilter.rawUpdatedAtCriterion);
  const rawStartSec = readNumberCriterion(objectFilter.rawStartSecCriterion);
  const rawEndSec = readNumberCriterion(objectFilter.rawEndSecCriterion);
  const rawConfidence = readNumberCriterion(objectFilter.rawConfidenceCriterion);
  const rawDuration = readNumberCriterion(objectFilter.rawDurationCriterion);
  const combinedRawSegmentFilter = useMemo(
    () => ({
      ...rawSegmentFilter,
      titleCriterion: rawTitle,
      hostType: rawHostType || undefined,
      sourceRunCriterion: rawSourceRun,
      colorHintCriterion: rawColorHint,
      hasImage: rawHasImage?.value,
      hasPayload: rawHasPayload?.value,
      createdAtCriterion: rawCreatedAt,
      updatedAtCriterion: rawUpdatedAt,
      startSecCriterion: rawStartSec,
      endSecCriterion: rawEndSec,
      tagIds: Array.from(new Set([...rawSegmentFilter.tagIds, ...rawTagIds])),
      tagDepth: rawTagDepth,
      performerIds: Array.from(new Set([...rawSegmentFilter.performerIds, ...rawPerformerIds])),
      faceIds: Array.from(new Set([...rawSegmentFilter.faceIds, ...rawFaceIds])),
      kind: rawSegmentFilter.kind ?? (rawKind || undefined),
      sourceKey: rawSegmentFilter.sourceKey ?? (rawSourceKey || undefined),
      confidenceCriterion: rawConfidence ?? rawSegmentFilter.confidenceCriterion,
      durationCriterion: rawDuration ?? rawSegmentFilter.durationCriterion,
      minConfidence: rawSegmentFilter.minConfidence,
      minDurationSec: rawSegmentFilter.minDurationSec,
    }),
    [
      rawColorHint,
      rawConfidence,
      rawCreatedAt,
      rawDuration,
      rawEndSec,
      rawFaceIds,
      rawHasImage?.value,
      rawHasPayload?.value,
      rawHostType,
      rawKind,
      rawPerformerIds,
      rawSegmentFilter,
      rawSourceKey,
      rawSourceRun,
      rawStartSec,
      rawTagDepth,
      rawTagIds,
      rawTitle,
      rawUpdatedAt,
    ],
  );
  const derivedSpanQueryActive = isDerivedSpanQueryFilterActive(objectFilter.derivedSpanQuery);
  const q = filter.q?.trim() ?? "";
  const sort = filter.sort ?? "updated_at";
  const direction = filter.direction ?? "desc";
  const seed = filter.seed;

  const selectedVideoQueries = useQueries({
    queries: !isRawView
      ? videoSelection.includeIds.map((videoId) => ({
          queryKey: ["video", videoId],
          queryFn: () => videos.get(videoId),
          staleTime: 60_000,
        }))
      : [],
  });

  const selectedVideosLoading = selectedVideoQueries.some((query) => query.isLoading);

  const selectedPerformerIds = useMemo(
    () => Array.from(new Set(derivedSpanQueryFilter.operands.flatMap((operand) => operand.performerIds))),
    [derivedSpanQueryFilter.operands],
  );

  const performerFaceQueries = useQueries({
    queries: !isRawView
      ? selectedPerformerIds.map((performerId) => ({
          queryKey: ["segments-page", "performer-faces", performerId],
          queryFn: () => faces.list({ performerId, merged: false, page: 1, perPage: 200 }),
          staleTime: 60_000,
        }))
      : [],
  });

  const performerFaceIdsByPerformer = useMemo(() => {
    const map = new Map<number, number[]>();
    selectedPerformerIds.forEach((performerId, index) => {
      map.set(performerId, performerFaceQueries[index]?.data?.items.map((face) => face.id) ?? []);
    });
    return map;
  }, [performerFaceQueries, selectedPerformerIds]);

  const appliedQuery = useMemo(
    () => buildAppliedDerivedQuery(derivedSpanQueryFilter, performerFaceIdsByPerformer),
    [derivedSpanQueryFilter, performerFaceIdsByPerformer],
  );
  const derivedQueryDescriptor = useMemo(
    () => buildDerivedQueryDescriptor(derivedSpanQueryFilter),
    [derivedSpanQueryFilter],
  );
  const performerFaceQueriesLoading = performerFaceQueries.some((query) => query.isLoading);
  const derivedQueryEnabled =
    !isRawView &&
    activeProfileId != null &&
    (!derivedSpanQueryActive || !performerFaceQueriesLoading) &&
    (videoSelection.includeIds.length === 0 || !selectedVideosLoading);

  return {
    videoTitle,
    videoSelection,
    videoTagIds,
    videoTagDepth,
    derivedSpanQueryFilter,
    combinedRawSegmentFilter,
    q,
    sort,
    direction,
    seed,
    selectedVideoQueries,
    performerFaceQueries,
    performerFaceQueriesLoading,
    selectedVideosLoading,
    appliedQuery,
    derivedQueryDescriptor,
    derivedQueryEnabled,
  };
}

/** The display profiles the Segments page offers, one per name. */
export function useSegmentDisplayProfiles(enabled: boolean) {
  const profilesQuery = useQuery({
    queryKey: ["segment-display-profiles"],
    queryFn: () => segmentDisplayProfiles.list(),
    enabled,
  });

  const availableProfiles = useMemo(() => {
    const profiles = dedupeSegmentDisplayProfiles(profilesQuery.data ?? []);
    const nonRawProfiles = profiles.filter((profile) => !(profile.isSystem && profile.name === "Raw"));
    return nonRawProfiles.length > 0 ? nonRawProfiles : profiles;
  }, [profilesQuery.data]);

  return { profilesQuery, availableProfiles };
}
