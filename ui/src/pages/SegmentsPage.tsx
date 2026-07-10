import { useCallback, useEffect, useMemo, useState } from "react";
import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { FolderOpen, Loader2, Trash2 } from "lucide-react";
import { faces, videos, segmentDisplayProfiles, segmentLibrary, segmentSpans } from "../api/client";
import type { FindFilter, SegmentDisplayProfile } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { AddToGroupDialog, type AddToGroupEntry } from "../components/AddToGroupDialog";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { getDefaultFilter } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { usePaginatedInfiniteQuery } from "../hooks/usePaginatedInfiniteQuery";
import {
  buildAppliedDerivedQuery,
  buildDerivedQueryDescriptor,
  createDerivedSpanCustomFilterSection,
  formatOperatorLabel,
  isDerivedSpanQueryFilterActive,
  readDerivedSpanQueryFilter,
} from "./segments/derivedQueryCriterion";
import { readRawSegmentFilter } from "./segments/rawSegmentFilter";
import {
  readRawSegmentIdsFromUrl,
  readMultiIdCriterionIds,
  readNumberCriterion,
  readVideoSelectionCriterion,
  readSegmentsPageContentView,
  readStringCriterion,
  readStringCriterionValue,
  readTimestampCriterion,
  readBoolCriterion,
  createSegmentCriteria,
} from "./segments/segmentCriteriaDefinitions";
import { buildSpanTitle } from "./segments/segmentDisplayUtils";
import { SegmentsPageList } from "./segments/SegmentsPageList";
import { useDerivedSpansQuery, useDerivedSpansCountQuery } from "./segments/useDerivedSpansQuery";
import { useRawSegmentsQuery } from "./segments/useRawSegmentsQuery";
import type {
  AppliedDerivedQuery,
  DerivedSpanItem,
  DerivedSpanOperandFilterValue,
  DerivedSpanQueryFilterValue,
  RawSegmentItem,
  SegmentsPageContentView,
} from "./segments/types";
import { LOCATION_CHANGE_EVENT, buildCurrentUrl, navigateToUrl } from "../router/location";

interface Props {
  onNavigate: (r: any) => void;
}

const DERIVED_SPAN_SORT_OPTIONS = [
  { value: "updated_at", label: "Video Updated" },
  { value: "created_at", label: "Video Created" },
  { value: "title", label: "Video Title" },
  { value: "span_start", label: "Segment Start" },
  { value: "span_end", label: "Segment End" },
  { value: "span_duration", label: "Segment Duration" },
  { value: "segment_confidence", label: "Segment Confidence" },
  { value: "segment_kind", label: "Segment Kind" },
  { value: "segment_source_key", label: "Segment Source" },
  { value: "segment_source_run_id", label: "Segment Source Run" },
  { value: "segment_tag_name", label: "Segment Tag" },
  { value: "segment_performer", label: "Segment Performer" },
  { value: "segment_ref", label: "Segment Reference" },
  { value: "segment_created_at", label: "Segment Created" },
  { value: "segment_updated_at", label: "Segment Updated" },
  { value: "host_title", label: "Host Title" },
  { value: "host_type", label: "Host Type" },
  { value: "host_id", label: "Host ID" },
  { value: "segment_count", label: "Segment Count" },
];

const RAW_SEGMENT_SORT_OPTIONS = [
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "title", label: "Label" },
  { value: "video_title", label: "Video Title" },
  { value: "start_sec", label: "Start Time" },
  { value: "end_sec", label: "End Time" },
  { value: "duration", label: "Duration" },
  { value: "confidence", label: "Confidence" },
  { value: "kind", label: "Kind" },
  { value: "source_key", label: "Source" },
  { value: "tag_name", label: "Tag" },
  { value: "performer", label: "Performer" },
  { value: "ref", label: "Face/Reference" },
];

function dedupeSegmentDisplayProfiles(profiles: SegmentDisplayProfile[]): SegmentDisplayProfile[] {
  const byName = new Map<string, SegmentDisplayProfile>();
  for (const profile of profiles) {
    const key = profile.name.trim().toLocaleLowerCase();
    const current = byName.get(key);
    if (!current || isPreferredSegmentDisplayProfile(profile, current)) {
      byName.set(key, profile);
    }
  }

  return profiles.filter((profile) => byName.get(profile.name.trim().toLocaleLowerCase())?.id === profile.id);
}

function isPreferredSegmentDisplayProfile(candidate: SegmentDisplayProfile, current: SegmentDisplayProfile): boolean {
  if (candidate.userId != null && current.userId == null) return true;
  if (candidate.userId == null && current.userId != null) return false;
  if (candidate.isDefault !== current.isDefault) return candidate.isDefault;
  return candidate.id < current.id;
}

async function fetchAllSegmentPages<TItem>(queryPage: (page: number, perPage: number) => Promise<{ items: TItem[]; totalCount: number }>, chunkSize = 1000) {
  const items: TItem[] = [];
  let page = 1;
  let totalCount: number | undefined;

  while (totalCount == null || items.length < totalCount) {
    const response = await queryPage(page, chunkSize);
    totalCount = response.totalCount;
    items.push(...response.items);
    if (response.items.length === 0) break;
    page += 1;
  }

  return items;
}

export function SegmentsPage({ onNavigate }: Props) {
  const queryClient = useQueryClient();
  const defaultState = useMemo(() => ({
    filter: getDefaultFilter("segments")?.findFilter ?? { page: 1, perPage: 24, sort: "updated_at", direction: "desc" } as FindFilter,
    objectFilter: getDefaultFilter("segments")?.objectFilter ?? {},
    displayMode: "grid" as DisplayMode,
  }), []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "segments",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list"] as const,
    allowInfinitePageSize: true,
  });
  const { hasPermission } = useAuth();
  const canReadVideos = canReadEntity("video", hasPermission);
  const canWriteGroups = canWriteEntity("group", hasPermission);
  const canDeleteSegments = canDeleteEntity("segment", hasPermission);
  const [showAddToGroup, setShowAddToGroup] = useState(false);
  const [confirmRawDelete, setConfirmRawDelete] = useState(false);
  const [activeProfileId, setActiveProfileId] = useState<number>();
  const [contentView, setContentView] = useState<SegmentsPageContentView>(() => readSegmentsPageContentView());
  const [rawSegmentIds, setRawSegmentIds] = useState<number[]>(() => readRawSegmentIdsFromUrl());
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const [selectedMatchingItems, setSelectedMatchingItems] = useState<{ view: SegmentsPageContentView; spans: DerivedSpanItem[]; raw: RawSegmentItem[] } | null>(null);

  const videoTitle = readStringCriterion(objectFilter.videoTitleCriterion);
  const videoSelection = readVideoSelectionCriterion(objectFilter.videosCriterion);
  const derivedSpanQueryFilter = useMemo(
    () => readDerivedSpanQueryFilter(objectFilter.derivedSpanQuery),
    [objectFilter.derivedSpanQuery],
  );
  const rawSegmentFilter = useMemo(
    () => readRawSegmentFilter(objectFilter.rawSegmentFilters),
    [objectFilter.rawSegmentFilters],
  );
  const rawTagIds = useMemo(() => readMultiIdCriterionIds(objectFilter.rawTagsCriterion), [objectFilter.rawTagsCriterion]);
  const rawPerformerIds = useMemo(() => readMultiIdCriterionIds(objectFilter.rawPerformersCriterion), [objectFilter.rawPerformersCriterion]);
  const rawFaceIds = useMemo(() => readMultiIdCriterionIds(objectFilter.rawFacesCriterion), [objectFilter.rawFacesCriterion]);
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
  const combinedRawSegmentFilter = useMemo(() => ({
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
    performerIds: Array.from(new Set([...rawSegmentFilter.performerIds, ...rawPerformerIds])),
    faceIds: Array.from(new Set([...rawSegmentFilter.faceIds, ...rawFaceIds])),
    kind: rawSegmentFilter.kind ?? (rawKind || undefined),
    sourceKey: rawSegmentFilter.sourceKey ?? (rawSourceKey || undefined),
    confidenceCriterion: rawConfidence ?? rawSegmentFilter.confidenceCriterion,
    durationCriterion: rawDuration ?? rawSegmentFilter.durationCriterion,
    minConfidence: rawSegmentFilter.minConfidence,
    minDurationSec: rawSegmentFilter.minDurationSec,
  }), [rawColorHint, rawConfidence, rawCreatedAt, rawDuration, rawEndSec, rawFaceIds, rawHasImage?.value, rawHasPayload?.value, rawHostType, rawKind, rawPerformerIds, rawSegmentFilter, rawSourceKey, rawSourceRun, rawStartSec, rawTagIds, rawTitle, rawUpdatedAt]);
  const derivedSpanQueryActive = isDerivedSpanQueryFilterActive(objectFilter.derivedSpanQuery);
  const q = filter.q?.trim() ?? "";
  const infinitePageSize = filter.perPage === 0;
  const defaultPerPage = defaultState.filter.perPage ?? 24;
  const perPage = infinitePageSize ? defaultPerPage : filter.perPage ?? defaultPerPage;
  const pageNumber = filter.page ?? 1;
  const sort = filter.sort ?? "updated_at";
  const direction = filter.direction ?? "desc";
  const isRawView = contentView === "raw";
  const sortOptions = isRawView ? RAW_SEGMENT_SORT_OPTIONS : DERIVED_SPAN_SORT_OPTIONS;

  const sourceOptionsQuery = useQuery({
    queryKey: ["segments-page", "filter", "source-keys"],
    queryFn: () => segmentLibrary.distinctSourceKeys(),
    staleTime: 60_000,
  });
  const kindOptionsQuery = useQuery({
    queryKey: ["segments-page", "filter", "kinds"],
    queryFn: () => segmentLibrary.distinctKinds(),
    staleTime: 60_000,
  });
  const segmentCriteria = useMemo(() => createSegmentCriteria({
    sourceOptions: [
      { value: "derived", label: "Derived" },
      ...(sourceOptionsQuery.data ?? []).map((option) => ({ value: option.value, label: `${option.value} (${option.count})` })),
    ],
    kindOptions: [
      { value: "union", label: "Union" },
      { value: "intersection", label: "Intersection" },
      { value: "difference", label: "Difference" },
      ...(kindOptionsQuery.data ?? []).map((option) => ({ value: option.value, label: `${option.value} (${option.count})` })),
    ],
  }), [kindOptionsQuery.data, sourceOptionsQuery.data]);

  useEffect(() => {
    const syncContentView = () => {
      setContentView(readSegmentsPageContentView());
      setRawSegmentIds(readRawSegmentIdsFromUrl());
    };

    window.addEventListener("popstate", syncContentView);
    window.addEventListener(LOCATION_CHANGE_EVENT, syncContentView);

    return () => {
      window.removeEventListener("popstate", syncContentView);
      window.removeEventListener(LOCATION_CHANGE_EVENT, syncContentView);
    };
  }, []);

  const updateContentView = useCallback((nextView: SegmentsPageContentView, nextRawSegmentIds?: number[]) => {
    const params = new URLSearchParams(window.location.search);
    if (nextView === "raw") {
      params.set("segmentsView", "raw");
    } else {
      params.delete("segmentsView");
    }

    const normalizedRawIds = Array.from(new Set((nextRawSegmentIds ?? []).filter((value) => Number.isInteger(value) && value > 0)));
    if (normalizedRawIds.length > 0) {
      params.set("rawIds", normalizedRawIds.join(","));
    } else {
      params.delete("rawIds");
    }

    navigateToUrl(buildCurrentUrl(window.location.pathname, params), { replace: true });
  }, []);

  const switchContentView = useCallback((nextView: SegmentsPageContentView, nextRawSegmentIds?: number[]) => {
    setFilter({
      ...filter,
      page: 1,
    });
    updateContentView(nextView, nextRawSegmentIds);
  }, [filter, setFilter, updateContentView]);

  const profilesQuery = useQuery({
    queryKey: ["segment-display-profiles"],
    queryFn: () => segmentDisplayProfiles.list(),
    enabled: !isRawView,
  });

  const availableProfiles = useMemo(() => {
    const profiles = dedupeSegmentDisplayProfiles(profilesQuery.data ?? []);
    const nonRawProfiles = profiles.filter((profile) => !(profile.isSystem && profile.name === "Raw"));
    return nonRawProfiles.length > 0 ? nonRawProfiles : profiles;
  }, [profilesQuery.data]);

  useEffect(() => {
    if (availableProfiles.length === 0) {
      return;
    }

    if (activeProfileId != null && availableProfiles.some((profile) => profile.id === activeProfileId)) {
      return;
    }

    setActiveProfileId(availableProfiles.find((profile) => profile.isDefault)?.id ?? availableProfiles[0].id);
  }, [activeProfileId, availableProfiles]);

  const selectedVideoQueries = useQueries({
    queries: !isRawView ? videoSelection.includeIds.map((videoId) => ({
      queryKey: ["video", videoId],
      queryFn: () => videos.get(videoId),
      staleTime: 60_000,
    })) : [],
  });

  const selectedVideosLoading = selectedVideoQueries.some((query) => query.isLoading);

  const selectedPerformerIds = useMemo(
    () => Array.from(new Set(derivedSpanQueryFilter.operands.flatMap((operand) => operand.performerIds))),
    [derivedSpanQueryFilter.operands],
  );

  const performerFaceQueries = useQueries({
    queries: !isRawView ? selectedPerformerIds.map((performerId) => ({
      queryKey: ["segments-page", "performer-faces", performerId],
      queryFn: () => faces.list({ performerId, merged: false, page: 1, perPage: 200 }),
      staleTime: 60_000,
    })) : [],
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
  const derivedQueryEnabled = !isRawView && activeProfileId != null && (!derivedSpanQueryActive || !performerFaceQueriesLoading) && (videoSelection.includeIds.length === 0 || !selectedVideosLoading);
  const rawQueryEnabled = isRawView;

  const queryDerivedSpansPage = useCallback(async (page: number, pageSize: number) => {
    if (activeProfileId == null) {
      return { items: [], totalCount: 0, page, perPage: pageSize };
    }

    const response = await segmentSpans.search({
      profile: activeProfileId,
      derivedQuery: appliedQuery != null ? {
        operator: appliedQuery.operator,
        operands: appliedQuery.operands,
        mergeGapSec: appliedQuery.mergeGapSec,
        minDurationSec: appliedQuery.minDurationSec,
      } : undefined,
      page,
      perPage: pageSize,
      sort,
      direction,
      q: q || undefined,
      videoTitle: videoTitle || undefined,
      videoIds: videoSelection.includeIds.length > 0 ? videoSelection.includeIds : undefined,
      excludeVideoIds: videoSelection.excludeIds.length > 0 ? videoSelection.excludeIds : undefined,
      tagIds: combinedRawSegmentFilter.tagIds.length > 0 ? combinedRawSegmentFilter.tagIds : undefined,
      kind: combinedRawSegmentFilter.kind,
      sourceKey: combinedRawSegmentFilter.sourceKey,
      sourceCategory: combinedRawSegmentFilter.sourceCategory,
      title: combinedRawSegmentFilter.titleCriterion?.value,
      titleModifier: combinedRawSegmentFilter.titleCriterion?.modifier,
      hostType: combinedRawSegmentFilter.hostType,
      sourceRunId: combinedRawSegmentFilter.sourceRunCriterion?.value,
      sourceRunIdModifier: combinedRawSegmentFilter.sourceRunCriterion?.modifier,
      colorHint: combinedRawSegmentFilter.colorHintCriterion?.value,
      colorHintModifier: combinedRawSegmentFilter.colorHintCriterion?.modifier,
      hasImage: combinedRawSegmentFilter.hasImage,
      hasPayload: combinedRawSegmentFilter.hasPayload,
      startSec: combinedRawSegmentFilter.startSecCriterion?.value,
      startSec2: combinedRawSegmentFilter.startSecCriterion?.value2,
      startSecModifier: combinedRawSegmentFilter.startSecCriterion?.modifier,
      endSec: combinedRawSegmentFilter.endSecCriterion?.value,
      endSec2: combinedRawSegmentFilter.endSecCriterion?.value2,
      endSecModifier: combinedRawSegmentFilter.endSecCriterion?.modifier,
      createdAt: combinedRawSegmentFilter.createdAtCriterion?.value,
      createdAt2: combinedRawSegmentFilter.createdAtCriterion?.value2,
      createdAtModifier: combinedRawSegmentFilter.createdAtCriterion?.modifier,
      updatedAt: combinedRawSegmentFilter.updatedAtCriterion?.value,
      updatedAt2: combinedRawSegmentFilter.updatedAtCriterion?.value2,
      updatedAtModifier: combinedRawSegmentFilter.updatedAtCriterion?.modifier,
      refIds: combinedRawSegmentFilter.faceIds.length > 0 ? combinedRawSegmentFilter.faceIds : undefined,
      performerIds: combinedRawSegmentFilter.performerIds.length > 0 ? combinedRawSegmentFilter.performerIds : undefined,
      confidence: combinedRawSegmentFilter.confidenceCriterion?.value,
      confidence2: combinedRawSegmentFilter.confidenceCriterion?.value2,
      confidenceModifier: combinedRawSegmentFilter.confidenceCriterion?.modifier,
      durationSec: combinedRawSegmentFilter.durationCriterion?.value,
      durationSec2: combinedRawSegmentFilter.durationCriterion?.value2,
      durationModifier: combinedRawSegmentFilter.durationCriterion?.modifier,
    });

    return {
      items: response.items.map<DerivedSpanItem>((item) => ({
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
      // The search endpoint reports totalCount=-1 when it served the page via early termination. Map it
      // to a value that lets the infinite loader's getNextPageParam continue iff there's more (the exact
      // total for display comes from the separate cached count query).
      totalCount: response.totalCount >= 0
        ? response.totalCount
        : (response.hasMore ? page * pageSize + 1 : (page - 1) * pageSize + response.items.length),
      page: response.page,
      perPage: response.perPage,
    };
  }, [activeProfileId, appliedQuery, combinedRawSegmentFilter, derivedQueryDescriptor, direction, q, videoSelection.excludeIds, videoSelection.includeIds, videoTitle, sort]);

  const queryRawSegmentsPage = useCallback(async (page: number, pageSize: number) => {
    const response = await segmentLibrary.list({
      q: q || undefined,
      ids: rawSegmentIds.length > 0 ? rawSegmentIds.join(",") : undefined,
      videoIds: videoSelection.includeIds.length > 0 ? videoSelection.includeIds.join(",") : undefined,
      excludeVideoIds: videoSelection.excludeIds.length > 0 ? videoSelection.excludeIds.join(",") : undefined,
      videoTitle: videoTitle || undefined,
      tagIds: combinedRawSegmentFilter.tagIds.length > 0 ? combinedRawSegmentFilter.tagIds.join(",") : undefined,
      kind: combinedRawSegmentFilter.kind,
      sourceKey: combinedRawSegmentFilter.sourceKey,
      sourceCategory: combinedRawSegmentFilter.sourceCategory,
      title: combinedRawSegmentFilter.titleCriterion?.value,
      titleModifier: combinedRawSegmentFilter.titleCriterion?.modifier,
      hostType: combinedRawSegmentFilter.hostType,
      sourceRunId: combinedRawSegmentFilter.sourceRunCriterion?.value,
      sourceRunIdModifier: combinedRawSegmentFilter.sourceRunCriterion?.modifier,
      colorHint: combinedRawSegmentFilter.colorHintCriterion?.value,
      colorHintModifier: combinedRawSegmentFilter.colorHintCriterion?.modifier,
      hasImage: combinedRawSegmentFilter.hasImage,
      hasPayload: combinedRawSegmentFilter.hasPayload,
      startSec: combinedRawSegmentFilter.startSecCriterion?.value,
      startSec2: combinedRawSegmentFilter.startSecCriterion?.value2,
      startSecModifier: combinedRawSegmentFilter.startSecCriterion?.modifier,
      endSec: combinedRawSegmentFilter.endSecCriterion?.value,
      endSec2: combinedRawSegmentFilter.endSecCriterion?.value2,
      endSecModifier: combinedRawSegmentFilter.endSecCriterion?.modifier,
      createdAt: combinedRawSegmentFilter.createdAtCriterion?.value,
      createdAt2: combinedRawSegmentFilter.createdAtCriterion?.value2,
      createdAtModifier: combinedRawSegmentFilter.createdAtCriterion?.modifier,
      updatedAt: combinedRawSegmentFilter.updatedAtCriterion?.value,
      updatedAt2: combinedRawSegmentFilter.updatedAtCriterion?.value2,
      updatedAtModifier: combinedRawSegmentFilter.updatedAtCriterion?.modifier,
      refIds: combinedRawSegmentFilter.faceIds.length > 0 ? combinedRawSegmentFilter.faceIds.join(",") : undefined,
      performerIds: combinedRawSegmentFilter.performerIds.length > 0 ? combinedRawSegmentFilter.performerIds.join(",") : undefined,
      minConfidence: combinedRawSegmentFilter.minConfidence,
      minDurationSec: combinedRawSegmentFilter.minDurationSec,
      confidence: combinedRawSegmentFilter.confidenceCriterion?.value,
      confidence2: combinedRawSegmentFilter.confidenceCriterion?.value2,
      confidenceModifier: combinedRawSegmentFilter.confidenceCriterion?.modifier,
      durationSec: combinedRawSegmentFilter.durationCriterion?.value,
      durationSec2: combinedRawSegmentFilter.durationCriterion?.value2,
      durationModifier: combinedRawSegmentFilter.durationCriterion?.modifier,
      sort,
      direction,
      page,
      perPage: pageSize,
    });

    return {
      items: response.items.map((item) => ({
        ...item,
        key: `segment:${item.id}`,
        videoId: item.hostId,
        videoTitle: item.hostTitle?.trim() || `Video #${item.hostId}`,
      })),
      totalCount: response.totalCount,
      page: response.page,
      perPage: response.perPage,
    };
  }, [combinedRawSegmentFilter, direction, q, rawSegmentIds, videoSelection.excludeIds, videoSelection.includeIds, videoTitle, sort]);

  const segmentsWindowQuery = useDerivedSpansQuery({
    activeProfileId,
    pageNumber,
    perPage,
    q,
    videoTitle,
    sort,
    direction,
    includeVideoIds: videoSelection.includeIds,
    excludeVideoIds: videoSelection.excludeIds,
    appliedQuery,
    derivedQueryDescriptor: appliedQuery != null ? derivedQueryDescriptor : undefined,
    rawFilter: combinedRawSegmentFilter,
    enabled: derivedQueryEnabled && !infinitePageSize,
  });

  // Exact span total (cached, async). Kept independent of page/sort/direction so it's fetched once per
  // filter set and reused while paging — the search response itself only carries -1 on the fast path.
  const spansCountQuery = useDerivedSpansCountQuery({
    activeProfileId,
    pageNumber,
    perPage,
    q,
    videoTitle,
    sort,
    direction,
    includeVideoIds: videoSelection.includeIds,
    excludeVideoIds: videoSelection.excludeIds,
    appliedQuery,
    derivedQueryDescriptor: appliedQuery != null ? derivedQueryDescriptor : undefined,
    rawFilter: combinedRawSegmentFilter,
    enabled: derivedQueryEnabled,
  });

  const rawSegmentsQuery = useRawSegmentsQuery({
    pageNumber,
    perPage,
    q,
    videoTitle,
    sort,
    direction,
    includeVideoIds: videoSelection.includeIds,
    excludeVideoIds: videoSelection.excludeIds,
    rawSegmentIds,
    rawFilter: combinedRawSegmentFilter,
    enabled: rawQueryEnabled && !infinitePageSize,
  });

  const derivedInfiniteQuery = usePaginatedInfiniteQuery<DerivedSpanItem>({
    queryKey: ["segments-page", "search", "infinite", activeProfileId, q, videoTitle, sort, direction, videoSelection.includeIds.join(","), videoSelection.excludeIds.join(","), appliedQuery ?? null, combinedRawSegmentFilter],
    queryFn: queryDerivedSpansPage,
    enabled: derivedQueryEnabled && infinitePageSize,
    chunkSize: defaultPerPage,
  });

  const rawInfiniteQuery = usePaginatedInfiniteQuery<RawSegmentItem>({
    queryKey: ["segments-page", "raw", "infinite", q, videoTitle, sort, direction, videoSelection.includeIds.join(","), videoSelection.excludeIds.join(","), rawSegmentIds.join(","), combinedRawSegmentFilter],
    queryFn: queryRawSegmentsPage,
    enabled: rawQueryEnabled && infinitePageSize,
    chunkSize: defaultPerPage,
  });

  const loadMoreSegments = useCallback(() => {
    if (isRawView) {
      if (rawInfiniteQuery.hasNextPage && !rawInfiniteQuery.isFetchingNextPage) {
        void rawInfiniteQuery.fetchNextPage();
      }
      return;
    }

    if (derivedInfiniteQuery.hasNextPage && !derivedInfiniteQuery.isFetchingNextPage) {
      void derivedInfiniteQuery.fetchNextPage();
    }
  }, [derivedInfiniteQuery.fetchNextPage, derivedInfiniteQuery.hasNextPage, derivedInfiniteQuery.isFetchingNextPage, isRawView, rawInfiniteQuery.fetchNextPage, rawInfiniteQuery.hasNextPage, rawInfiniteQuery.isFetchingNextPage]);

  const spanItems = infinitePageSize ? derivedInfiniteQuery.items : segmentsWindowQuery.data?.items ?? [];
  const rawItems = infinitePageSize ? rawInfiniteQuery.items : rawSegmentsQuery.data?.items ?? [];
  const items = isRawView ? rawItems : spanItems;
  // Spans: prefer the exact cached count; until it arrives, fall back to the page response's own total
  // (exact when the whole scope was resolved, -1 on the fast path) and finally to a provisional total
  // that keeps the pager's "next" enabled while there's more. The exact value replaces it on arrival.
  const spansPageTotal = infinitePageSize ? derivedInfiniteQuery.totalCount : (segmentsWindowQuery.data?.totalCount ?? 0);
  const spansHasMore = !infinitePageSize && (segmentsWindowQuery.data?.hasMore ?? false);
  const spansTotalCount = spansCountQuery.data
    ?? (spansPageTotal >= 0 ? spansPageTotal : pageNumber * perPage + (spansHasMore ? perPage : 0));
  const totalCount = isRawView
    ? (infinitePageSize ? rawInfiniteQuery.totalCount : rawSegmentsQuery.data?.totalCount ?? 0)
    : spansTotalCount;
  const selectionItems: Array<{ id: string | number }> = items;

  const isLoading = (!isRawView && profilesQuery.isLoading)
    || (!isRawView && videoSelection.includeIds.length > 0
      ? selectedVideosLoading
      : false)
    || (!isRawView && performerFaceQueriesLoading)
    || (isRawView
      ? (infinitePageSize ? rawInfiniteQuery.isLoading : rawSegmentsQuery.isLoading)
      : (infinitePageSize ? derivedInfiniteQuery.isLoading : segmentsWindowQuery.isLoading));
  const firstQueryError = isRawView
    ? ((infinitePageSize ? rawInfiniteQuery.error : rawSegmentsQuery.error) instanceof Error ? (infinitePageSize ? rawInfiniteQuery.error : rawSegmentsQuery.error) as Error : undefined)
    : ((infinitePageSize ? derivedInfiniteQuery.error : segmentsWindowQuery.error) instanceof Error ? (infinitePageSize ? derivedInfiniteQuery.error : segmentsWindowQuery.error) as Error : undefined);

  useEffect(() => {
    if (infinitePageSize) {
      return;
    }

    if (totalCount === 0) {
      return;
    }

    const totalPages = Math.max(1, Math.ceil(totalCount / perPage));
    if (pageNumber <= totalPages) {
      return;
    }

    setFilter({
      ...filter,
      page: totalPages,
    });
  }, [filter, infinitePageSize, pageNumber, perPage, setFilter, totalCount]);

  const selectionResetKey = useMemo(() => JSON.stringify({ contentView, filter: { ...filter, page: undefined }, objectFilter, activeProfileId, appliedQuery, rawSegmentIds }), [activeProfileId, appliedQuery, contentView, filter, objectFilter, rawSegmentIds]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(selectionItems, { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const spanSelectionItems = selectedMatchingItems?.view === "spans" ? selectedMatchingItems.spans : spanItems;
  const rawSelectionItems = selectedMatchingItems?.view === "raw" ? selectedMatchingItems.raw : rawItems;
  const selectedEntries = useMemo<AddToGroupEntry[]>(() => spanSelectionItems
    .filter((item) => selectedIds.has(item.id))
    .map((item) => ({
      key: item.key,
      videoId: item.videoId,
      spanKey: item.span.spanKey,
      title: buildSpanTitle(item.span, item.videoTitle),
      profileId: item.profileId,
      derivedQuery: item.derivedQuery,
    })), [selectedIds, spanSelectionItems]);
  const selectedRawSegments = useMemo(() => rawSelectionItems.filter((item) => selectedIds.has(item.id)), [rawSelectionItems, selectedIds]);
  const handleSelectNone = useCallback(() => {
    setSelectedMatchingItems(null);
    selectNone();
  }, [selectNone]);
  const handleSelectAllMatching = useCallback(async () => {
    setSelectAllMatchingPending(true);
    try {
      if (isRawView) {
        const allRawItems = await fetchAllSegmentPages(queryRawSegmentsPage);
        setSelectedMatchingItems({ view: "raw", spans: [], raw: allRawItems });
        selectIds(allRawItems.map((item) => item.id));
      } else {
        const allSpanItems = await fetchAllSegmentPages(queryDerivedSpansPage);
        setSelectedMatchingItems({ view: "spans", spans: allSpanItems, raw: [] });
        selectIds(allSpanItems.map((item) => item.id));
      }
    } finally {
      setSelectAllMatchingPending(false);
    }
  }, [isRawView, queryDerivedSpansPage, queryRawSegmentsPage, selectIds]);

  const rawDeleteMutation = useMutation({
    mutationFn: async (segmentsToDelete: RawSegmentItem[]) => {
      for (const segment of segmentsToDelete) {
        await videos.segments.delete(segment.hostId, segment.id);
      }
    },
    onSuccess: async (_result, segmentsToDelete) => {
      await queryClient.invalidateQueries({ queryKey: ["segments-page"] });
      for (const segment of segmentsToDelete) {
        await queryClient.invalidateQueries({ queryKey: ["segment", segment.id] });
        await queryClient.invalidateQueries({ queryKey: ["video", segment.hostId, "segments"] });
        await queryClient.invalidateQueries({ queryKey: ["video", segment.hostId, "resolved-spans"] });
        await queryClient.invalidateQueries({ queryKey: ["video", segment.hostId] });
      }
      setConfirmRawDelete(false);
      handleSelectNone();
    },
  });

  const customFilterSections = useMemo(
    () => (isRawView ? [] : [createDerivedSpanCustomFilterSection(videoSelection.includeIds)]),
    [isRawView, videoSelection.includeIds],
  );

  useEffect(() => {
    handleSelectNone();
  }, [handleSelectNone, selectionResetKey]);

  useEffect(() => {
    const currentSort = filter.sort ?? "updated_at";
    if (!sortOptions.some((option) => option.value === currentSort)) {
      setFilter({ ...filter, sort: "updated_at", page: 1 });
    }
  }, [filter, setFilter, sortOptions]);

  return (
    <>
      <AddToGroupDialog open={showAddToGroup} onClose={() => setShowAddToGroup(false)} items={selectedEntries} onAdded={handleSelectNone} />
      <ListPage
        title="Segments"
        pageKey="segments"
        filter={filter}
        onFilterChange={setFilter}
        totalCount={totalCount}
        isLoading={isLoading}
        sortOptions={sortOptions}
        displayMode={displayMode}
        onDisplayModeChange={setDisplayMode}
        availableDisplayModes={["grid", "list"]}
        allowInfinitePageSize
        showPagingControls={!infinitePageSize}
        selectAllPending={infinitePageSize ? selectAllMatchingPending : false}
        onSelectAllMatching={infinitePageSize ? selectAll : undefined}
        selectAllMatchingLabel="Select shown"
        infiniteScroll={infinitePageSize ? {
          hasNextPage: isRawView ? rawInfiniteQuery.hasNextPage : derivedInfiniteQuery.hasNextPage,
          isFetchingNextPage: isRawView ? rawInfiniteQuery.isFetchingNextPage : derivedInfiniteQuery.isFetchingNextPage,
          onLoadMore: loadMoreSegments,
          loadedCount: isRawView ? rawInfiniteQuery.loadedThroughCount : derivedInfiniteQuery.loadedThroughCount,
          totalCount,
        } : undefined}
        criteriaDefinitions={segmentCriteria}
        objectFilter={objectFilter}
        onObjectFilterChange={setObjectFilter}
        customFilterSections={customFilterSections}
        metadataByline={null}
        renderOperations={() => (
          <div className="flex flex-wrap items-center gap-2">
            <div className="inline-flex rounded-lg border border-border bg-card/70 p-1 text-xs">
              <button
                type="button"
                onClick={() => switchContentView("spans")}
                className={`rounded-md px-2.5 py-1.5 transition-colors ${!isRawView ? "bg-accent text-white" : "text-muted hover:text-foreground"}`}
              >
                Segments
              </button>
              <button
                type="button"
                onClick={() => switchContentView("raw")}
                className={`rounded-md px-2.5 py-1.5 transition-colors ${isRawView ? "bg-accent text-white" : "text-muted hover:text-foreground"}`}
              >
                Raw segments
              </button>
            </div>
            <label
              title={isRawView ? "Raw view shows segments as observed; profiles and derived queries do not apply." : undefined}
              className="flex items-center gap-2 rounded-lg border border-border bg-card/70 px-2.5 py-1.5 text-xs text-muted"
            >
              <span className="font-semibold uppercase tracking-wide">Profile</span>
              <select
                value={activeProfileId ?? ""}
                onChange={(event) => setActiveProfileId(Number(event.target.value))}
                className="min-w-[10rem] bg-transparent text-xs text-foreground focus:outline-none disabled:cursor-not-allowed disabled:opacity-60"
                disabled={isRawView || availableProfiles.length === 0}
              >
                {availableProfiles.map((profile) => (
                  <option key={profile.id} value={profile.id}>
                    {profile.name}{profile.isDefault ? " (Default)" : ""}
                  </option>
                ))}
              </select>
            </label>
          </div>
        )}
        selectedIds={selectedIds}
        onSelectAll={infinitePageSize ? handleSelectAllMatching : selectAll}
        onSelectNone={handleSelectNone}
        onInvertSelection={invertSelection}
        selectionActions={
          <>
            {isRawView && canDeleteSegments && selectedRawSegments.length > 0 ? (
              <button
                type="button"
                onClick={() => setConfirmRawDelete(true)}
                disabled={rawDeleteMutation.isPending}
                className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-red-400 hover:bg-red-900/20 hover:text-red-300 disabled:opacity-60"
              >
                {rawDeleteMutation.isPending ? <Loader2 className="h-3 w-3 animate-spin" /> : <Trash2 className="h-3 w-3" />}
                Delete
              </button>
            ) : null}
            {!isRawView && canWriteGroups && selectedEntries.length > 0 ? (
              <button
                type="button"
                onClick={() => setShowAddToGroup(true)}
                className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-accent hover:bg-accent/10 hover:text-accent-hover"
              >
                <FolderOpen className="h-3 w-3" />
                Add to group
              </button>
            ) : null}
          </>
        }
      >
        {firstQueryError ? (
          <div className="mb-4 rounded-lg border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">
            {isRawView ? `Raw segments failed to load: ${firstQueryError.message}` : `Derived query failed: ${firstQueryError.message}`}
          </div>
        ) : null}
        <SegmentsPageList
          displayMode={displayMode}
          isRawView={isRawView}
          rawItems={rawItems}
          spanItems={spanItems}
          rawSegmentIds={rawSegmentIds}
          appliedQuery={appliedQuery}
          isLoading={isLoading}
          canReadVideos={canReadVideos}
          onNavigate={onNavigate}
          onViewRawSegments={(segmentIds) => switchContentView("raw", segmentIds)}
          selectedIds={selectedIds}
          onToggle={toggle}
          selecting={selecting}
          infinitePageSize={infinitePageSize}
          hasNextPage={isRawView ? rawInfiniteQuery.hasNextPage : derivedInfiniteQuery.hasNextPage}
          isFetchingNextPage={isRawView ? rawInfiniteQuery.isFetchingNextPage : derivedInfiniteQuery.isFetchingNextPage}
          loadMore={loadMoreSegments}
        />
      </ListPage>
      <ConfirmDialog
        open={confirmRawDelete}
        title="Delete Raw Segments"
        message={`Delete ${selectedRawSegments.length} selected raw segment${selectedRawSegments.length === 1 ? "" : "s"}? This cannot be undone.`}
        confirmLabel={rawDeleteMutation.isPending ? "Deleting..." : "Delete"}
        onConfirm={() => rawDeleteMutation.mutate(selectedRawSegments)}
        onCancel={() => setConfirmRawDelete(false)}
        isPending={rawDeleteMutation.isPending}
      />
    </>
  );
}

