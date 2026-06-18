import { useCallback, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { InfiniteData } from "@tanstack/react-query";
import type { BoolCriterion, CriterionModifier, CustomFieldCriterion, FindFilter, FaceSuggestion, FaceTopSuggestion, IntCriterion, MultiIdCriterion, PaginatedResponse, StringCriterion } from "../api/types";
import { faces } from "../api/client";
import type { Face } from "../api/types";
import { faceDisplayName } from "../utils/faceDisplay";
import { Fingerprint, Link2, Trash2 } from "lucide-react";
import { ListPage, type DisplayMode } from "../components/ListPage";
import type { CriterionDefinition } from "../components/FilterDialog";
import { CardSelectionToggle, RouteCardLinkOverlay } from "../components/RouteCardLinkOverlay";
import { createNestedRouteLinkProps } from "../components/cardNavigation";
import { FaceCompareDialog, readReferenceLinkInfo } from "../components/FaceCompareDialog";
import { buildFaceCarouselSampleImageUrls } from "../components/faceComparisonImages";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { FaceTile } from "../components/EntityCards";
import { useListPageCardSizeContext } from "../components/ListPageCardSizeContext";
import { formatDate } from "../components/shared";
import { getDefaultFilter } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { useInfiniteListData } from "../hooks/useInfiniteListData";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { VirtualizedEntityGrid } from "../components/VirtualizedEntityLayouts";

interface Props {
  onNavigate: (r: any) => void;
}

type FaceSort = string;

const defaultFaceSort: FaceSort = "suggestion_confidence";
const defaultFaceDirection = "desc";
const FACE_SORT_OPTIONS = [
  { value: "suggestion_confidence", label: "Suggested Match Confidence" },
  { value: "updated", label: "Updated At" },
  { value: "created", label: "Created At" },
  { value: "label", label: "Label" },
  { value: "performer_name", label: "Performer Name" },
  { value: "primary_source_key", label: "Source" },
  { value: "detection_count", label: "Detection Count" },
  { value: "appearance_count", label: "Appearance Count" },
  { value: "frame_sample_count", label: "Frame Sample Count" },
  { value: "video_count", label: "Video Count" },
  { value: "image_count", label: "Image Count" },
  { value: "cover_present", label: "Has Cover" },
];

const FACE_CRITERIA: CriterionDefinition[] = [
  { id: "linked", label: "Linked", type: "bool", filterKey: "linkedCriterion" },
  { id: "label", label: "Label", type: "string", filterKey: "labelCriterion" },
  { id: "performers", label: "Linked Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "topSuggestionPerformers", label: "Top Suggestion Performers", type: "multiId", entityType: "performers", filterKey: "topSuggestionPerformersCriterion" },
  { id: "detectionCount", label: "Detection Count", type: "number", filterKey: "detectionCountCriterion" },
  { id: "appearanceCount", label: "Appearance Count", type: "number", filterKey: "appearanceCountCriterion" },
  { id: "frameSampleCount", label: "Frame Sample Count", type: "number", filterKey: "frameSampleCountCriterion" },
  { id: "videoCount", label: "Video Count", type: "number", filterKey: "videoCountCriterion" },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion" },
  { id: "suggestionConfidence", label: "Suggestion Confidence", type: "number", filterKey: "suggestionConfidenceCriterion" },
];

function readFaceSort(value: unknown): FaceSort {
  return typeof value === "string" && FACE_SORT_OPTIONS.some((option) => option.value === value) ? value : defaultFaceSort;
}

function readMinSuggestionConfidence(value: unknown) {
  return typeof value === "number" && Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : undefined;
}

function readNumberCriterion(value: unknown): IntCriterion | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  const candidate = value as Partial<IntCriterion>;
  if (typeof candidate.value !== "number" || !Number.isFinite(candidate.value)) {
    return undefined;
  }

  const modifier = isCriterionModifier(candidate.modifier) ? candidate.modifier : "EQUALS";
  const criterion: IntCriterion = {
    modifier,
    value: Math.max(0, Math.min(100, candidate.value)),
  };
  if ((modifier === "BETWEEN" || modifier === "NOT_BETWEEN") && typeof candidate.value2 === "number" && Number.isFinite(candidate.value2)) {
    criterion.value2 = Math.max(0, Math.min(100, candidate.value2));
  }

  return criterion;
}

function readCountCriterion(value: unknown): IntCriterion | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  const candidate = value as Partial<IntCriterion>;
  if (typeof candidate.value !== "number" || !Number.isFinite(candidate.value)) {
    return undefined;
  }

  const modifier = isCriterionModifier(candidate.modifier) ? candidate.modifier : "EQUALS";
  const criterion: IntCriterion = { modifier, value: Math.max(0, Math.floor(candidate.value)) };
  if ((modifier === "BETWEEN" || modifier === "NOT_BETWEEN") && typeof candidate.value2 === "number" && Number.isFinite(candidate.value2)) {
    criterion.value2 = Math.max(0, Math.floor(candidate.value2));
  }
  return criterion;
}

function readStringCriterion(value: unknown): StringCriterion | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  const candidate = value as Partial<StringCriterion>;
  const modifier = isCriterionModifier(candidate.modifier) ? candidate.modifier : "INCLUDES";
  const rawValue = typeof candidate.value === "string" ? candidate.value.trim() : "";
  if ((modifier === "IS_NULL" || modifier === "NOT_NULL") || rawValue.length > 0) {
    return { modifier, value: rawValue };
  }

  return undefined;
}

function readBoolCriterion(value: unknown): BoolCriterion | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  const candidate = value as Partial<BoolCriterion>;
  return typeof candidate.value === "boolean" ? { value: candidate.value } : undefined;
}

function readMultiIdIncludes(value: unknown) {
  if (!value || typeof value !== "object") {
    return [] as number[];
  }

  const criterion = value as Partial<MultiIdCriterion>;
  return Array.isArray(criterion.value)
    ? criterion.value.filter((item): item is number => typeof item === "number" && Number.isFinite(item) && item > 0)
    : [];
}

function isCriterionModifier(value: unknown): value is CriterionModifier {
  return value === "EQUALS"
    || value === "NOT_EQUALS"
    || value === "GREATER_THAN"
    || value === "LESS_THAN"
    || value === "INCLUDES"
    || value === "EXCLUDES"
    || value === "INCLUDES_ALL"
    || value === "EXCLUDES_ALL"
    || value === "IS_NULL"
    || value === "NOT_NULL"
    || value === "BETWEEN"
    || value === "NOT_BETWEEN"
    || value === "MATCHES_REGEX"
    || value === "NOT_MATCHES_REGEX";
}

function hasMultiIdIncludes(value: unknown) {
  return readMultiIdIncludes(value).length > 0;
}

function readCustomFieldCriteria(value: unknown): CustomFieldCriterion[] {
  return Array.isArray(value) ? value.filter((item): item is CustomFieldCriterion => Boolean(item && typeof item === "object")) : [];
}

function readSuggestionConfidenceLowerBound(value: unknown) {
  const legacy = readMinSuggestionConfidence(value);
  if (legacy != null) {
    return legacy;
  }

  const criterion = readNumberCriterion(value);
  if (!criterion) {
    return undefined;
  }

  if (criterion.modifier === "GREATER_THAN" || criterion.modifier === "EQUALS") {
    return criterion.value;
  }

  if (criterion.modifier === "BETWEEN" || criterion.modifier === "NOT_BETWEEN") {
    return Math.min(criterion.value, criterion.value2 ?? criterion.value);
  }

  return undefined;
}

function sanitizeFaceFilters(filter: Record<string, unknown>) {
  const next: Record<string, unknown> = {};
  const linkedCriterion = readBoolCriterion(filter.linkedCriterion);
  const minSuggestionConfidence = readMinSuggestionConfidence(filter.minSuggestionConfidence);
  const suggestionConfidenceCriterion = readNumberCriterion(filter.suggestionConfidenceCriterion);
  const labelCriterion = readStringCriterion(filter.labelCriterion);
  const detectionCountCriterion = readCountCriterion(filter.detectionCountCriterion);
  const appearanceCountCriterion = readCountCriterion(filter.appearanceCountCriterion);
  const frameSampleCountCriterion = readCountCriterion(filter.frameSampleCountCriterion);
  const videoCountCriterion = readCountCriterion(filter.videoCountCriterion);
  const imageCountCriterion = readCountCriterion(filter.imageCountCriterion);
  const customFieldCriteria = readCustomFieldCriteria(filter.customFieldCriteria);

  if (linkedCriterion) next.linkedCriterion = linkedCriterion;

  if (minSuggestionConfidence != null) {
    next.minSuggestionConfidence = minSuggestionConfidence;
  }

  if (suggestionConfidenceCriterion) {
    next.suggestionConfidenceCriterion = suggestionConfidenceCriterion;
  }

  if (labelCriterion) next.labelCriterion = labelCriterion;
  if (detectionCountCriterion) next.detectionCountCriterion = detectionCountCriterion;
  if (appearanceCountCriterion) next.appearanceCountCriterion = appearanceCountCriterion;
  if (frameSampleCountCriterion) next.frameSampleCountCriterion = frameSampleCountCriterion;
  if (videoCountCriterion) next.videoCountCriterion = videoCountCriterion;
  if (imageCountCriterion) next.imageCountCriterion = imageCountCriterion;
  if (customFieldCriteria.length > 0) next.customFieldCriteria = customFieldCriteria;

  if (hasMultiIdIncludes(filter.performersCriterion)) {
    next.performersCriterion = filter.performersCriterion;
  }

  if (hasMultiIdIncludes(filter.topSuggestionPerformersCriterion)) {
    next.topSuggestionPerformersCriterion = filter.topSuggestionPerformersCriterion;
  }

  return next;
}

type FaceQueryPatchOptions = {
  linked?: boolean;
  performerIds?: string;
  topSuggestionPerformerIds?: string;
};

function faceMatchesPatchedQuery(face: Face, query: FaceQueryPatchOptions) {
  if (query.linked === true && !face.performerId) return false;
  if (query.linked === false && face.performerId) return false;

  const performerIds = parseIdSet(query.performerIds);
  if (performerIds && (!face.performerId || !performerIds.has(face.performerId))) return false;

  const topSuggestionPerformerIds = parseIdSet(query.topSuggestionPerformerIds);
  if (topSuggestionPerformerIds && (!face.topSuggestion || !topSuggestionPerformerIds.has(face.topSuggestion.performerId))) return false;

  return true;
}

function parseIdSet(value?: string) {
  if (!value) return null;
  const ids = value
    .split(",")
    .map((item) => Number(item))
    .filter((item) => Number.isFinite(item) && item > 0);
  return ids.length > 0 ? new Set(ids) : null;
}

function patchFacePage(page: PaginatedResponse<Face>, updatedFace: Face, keepFace: boolean): PaginatedResponse<Face> {
  const existing = page.items.some((item) => item.id === updatedFace.id);
  if (!existing) return page;

  return {
    ...page,
    items: keepFace
      ? page.items.map((item) => item.id === updatedFace.id ? updatedFace : item)
      : page.items.filter((item) => item.id !== updatedFace.id),
    totalCount: keepFace ? page.totalCount : Math.max(0, page.totalCount - 1),
  };
}

function patchFaceListCache(data: unknown, updatedFace: Face, keepFace: boolean) {
  if (!data || typeof data !== "object") return data;

  if ("pages" in data && Array.isArray((data as InfiniteData<PaginatedResponse<Face>>).pages)) {
    const infiniteData = data as InfiniteData<PaginatedResponse<Face>>;
    return {
      ...infiniteData,
      pages: infiniteData.pages.map((page) => patchFacePage(page, updatedFace, keepFace)),
    };
  }

  if ("items" in data && Array.isArray((data as PaginatedResponse<Face>).items)) {
    return patchFacePage(data as PaginatedResponse<Face>, updatedFace, keepFace);
  }

  return data;
}

function removeFacesFromListCache(data: unknown, deletedFaceIds: Set<number>) {
  if (!data || typeof data !== "object" || deletedFaceIds.size === 0) return data;

  if ("pages" in data && Array.isArray((data as InfiniteData<PaginatedResponse<Face>>).pages)) {
    const infiniteData = data as InfiniteData<PaginatedResponse<Face>>;
    return {
      ...infiniteData,
      pages: infiniteData.pages.map((page) => ({
        ...page,
        items: page.items.filter((item) => !deletedFaceIds.has(item.id)),
        totalCount: Math.max(0, page.totalCount - deletedFaceIds.size),
      })),
    };
  }

  if ("items" in data && Array.isArray((data as PaginatedResponse<Face>).items)) {
    const page = data as PaginatedResponse<Face>;
    return {
      ...page,
      items: page.items.filter((item) => !deletedFaceIds.has(item.id)),
      totalCount: Math.max(0, page.totalCount - deletedFaceIds.size),
    };
  }

  return data;
}

export function FacesPage({ onNavigate }: Props) {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canReadPerformers = canReadEntity("performer", hasPermission);
  const canWriteFaces = canWriteEntity("face", hasPermission);
  const canDeleteFaces = canDeleteEntity("face", hasPermission);
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("faces");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: defaultFaceSort, direction: defaultFaceDirection } as FindFilter,
      objectFilter: savedFilter?.objectFilter ?? { linkedCriterion: { value: false } },
      displayMode: "grid" as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "faces",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list"] as const,
    allowInfinitePageSize: true,
  });
  const linkedCriterion = readBoolCriterion(objectFilter.linkedCriterion);
  const minSuggestionConfidence = readMinSuggestionConfidence(objectFilter.minSuggestionConfidence);
  const suggestionConfidenceCriterion = readNumberCriterion(objectFilter.suggestionConfidenceCriterion);
  const labelCriterion = readStringCriterion(objectFilter.labelCriterion);
  const detectionCountCriterion = readCountCriterion(objectFilter.detectionCountCriterion);
  const appearanceCountCriterion = readCountCriterion(objectFilter.appearanceCountCriterion);
  const frameSampleCountCriterion = readCountCriterion(objectFilter.frameSampleCountCriterion);
  const videoCountCriterion = readCountCriterion(objectFilter.videoCountCriterion);
  const imageCountCriterion = readCountCriterion(objectFilter.imageCountCriterion);
  const linkedPerformerIds = useMemo(() => readMultiIdIncludes(objectFilter.performersCriterion), [objectFilter.performersCriterion]);
  const topSuggestionPerformerIds = useMemo(() => readMultiIdIncludes(objectFilter.topSuggestionPerformersCriterion), [objectFilter.topSuggestionPerformersCriterion]);
  const customFieldCriteria = useMemo(() => readCustomFieldCriteria(objectFilter.customFieldCriteria), [objectFilter.customFieldCriteria]);
  const sort = readFaceSort(filter.sort);
  const [comparison, setComparison] = useState<{ face: Face; suggestion: FaceTopSuggestion } | null>(null);
  const [batchCompareFaceIds, setBatchCompareFaceIds] = useState<number[]>([]);
  const [batchCompareIndex, setBatchCompareIndex] = useState(0);
  const [confirmBatchDelete, setConfirmBatchDelete] = useState(false);
  const [confirmBatchLink, setConfirmBatchLink] = useState(false);
  const [createFromReference, setCreateFromReference] = useState(true);
  const [linkConflicting, setLinkConflicting] = useState(false);
  const [mergeConflicting, setMergeConflicting] = useState(false);
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const query = useMemo(() => ({
    q: filter.q?.trim() || undefined,
    linked: linkedCriterion?.value,
    label: labelCriterion?.value,
    labelModifier: labelCriterion?.modifier,
    detectionCount: detectionCountCriterion?.value,
    detectionCount2: detectionCountCriterion?.value2,
    detectionCountModifier: detectionCountCriterion?.modifier,
    appearanceCount: appearanceCountCriterion?.value,
    appearanceCount2: appearanceCountCriterion?.value2,
    appearanceCountModifier: appearanceCountCriterion?.modifier,
    frameSampleCount: frameSampleCountCriterion?.value,
    frameSampleCount2: frameSampleCountCriterion?.value2,
    frameSampleCountModifier: frameSampleCountCriterion?.modifier,
    videoCount: videoCountCriterion?.value,
    videoCount2: videoCountCriterion?.value2,
    videoCountModifier: videoCountCriterion?.modifier,
    imageCount: imageCountCriterion?.value,
    imageCount2: imageCountCriterion?.value2,
    imageCountModifier: imageCountCriterion?.modifier,
    minSuggestionConfidence,
    suggestionConfidence: suggestionConfidenceCriterion?.value,
    suggestionConfidence2: suggestionConfidenceCriterion?.value2,
    suggestionConfidenceModifier: suggestionConfidenceCriterion?.modifier,
    performerIds: linkedPerformerIds.length > 0 ? linkedPerformerIds.join(",") : undefined,
    topSuggestionPerformerIds: topSuggestionPerformerIds.length > 0 ? topSuggestionPerformerIds.join(",") : undefined,
    sort,
    direction: filter.direction,
    customFieldCriteria,
    page: filter.page ?? 1,
    perPage: filter.perPage ?? 40,
  }), [appearanceCountCriterion?.modifier, appearanceCountCriterion?.value, appearanceCountCriterion?.value2, customFieldCriteria, detectionCountCriterion?.modifier, detectionCountCriterion?.value, detectionCountCriterion?.value2, filter.direction, filter.page, filter.perPage, filter.q, frameSampleCountCriterion?.modifier, frameSampleCountCriterion?.value, frameSampleCountCriterion?.value2, imageCountCriterion?.modifier, imageCountCriterion?.value, imageCountCriterion?.value2, labelCriterion?.modifier, labelCriterion?.value, linkedCriterion?.value, linkedPerformerIds, minSuggestionConfidence, videoCountCriterion?.modifier, videoCountCriterion?.value, videoCountCriterion?.value2, sort, suggestionConfidenceCriterion?.modifier, suggestionConfidenceCriterion?.value, suggestionConfidenceCriterion?.value2, topSuggestionPerformerIds]);
  const listData = useInfiniteListData<Face>({
    queryKey: ["faces", query],
    filter,
    chunkSize: defaultState.filter.perPage ?? 40,
    queryPage: (nextFilter) => faces.list({
      ...query,
      page: nextFilter.page ?? 1,
      perPage: nextFilter.perPage ?? defaultState.filter.perPage ?? 40,
    }),
  });

  const items = listData.items;
  const totalCount = listData.totalCount;
  const isLoading = listData.isLoading;
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: listData.infiniteFilterKey, objectFilter }), [listData.infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(items, { preserveOnAppend: listData.infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const selectedFaceIds = useMemo(() => Array.from(selectedIds).map((value) => Number(value)), [selectedIds]);
  const batchComparableFaces = useMemo(() => items.filter((face) => selectedIds.has(face.id) && !face.performerId && face.topSuggestion), [items, selectedIds]);
  const batchComparisonFace = batchCompareFaceIds.length > 0 ? items.find((face) => face.id === batchCompareFaceIds[batchCompareIndex]) : undefined;
  const activeComparison = batchComparisonFace?.topSuggestion ? { face: batchComparisonFace, suggestion: batchComparisonFace.topSuggestion } : comparison;
  const comparisonFaceId = activeComparison?.face.id ?? null;
  const { data: comparisonFaceDetections = [] } = useQuery({
    queryKey: ["face", comparisonFaceId, "detections"],
    queryFn: () => faces.detections(comparisonFaceId!),
    enabled: comparisonFaceId != null,
  });
  const comparisonFaceImageUrls = useMemo(
    () => buildFaceCarouselSampleImageUrls(activeComparison?.face, comparisonFaceDetections, faces.detectionCropUrl),
    [activeComparison?.face, comparisonFaceDetections],
  );
  // Pull the full ranked suggestions for the compared face so the dialog can show the same evidence and
  // conflict/merge chooser as the face detail page (the cached topSuggestion projection lacks why,
  // evidence, and conflict grouping).
  const { data: comparisonSuggestions = [] } = useQuery({
    queryKey: ["face", comparisonFaceId, "suggestions"],
    queryFn: () => faces.suggestions(comparisonFaceId!),
    enabled: comparisonFaceId != null,
  });
  const comparisonSuggestion = useMemo(() => {
    if (!activeComparison) return null;
    return comparisonSuggestions.find((item) => item.performerId === activeComparison.suggestion.performerId)
      ?? activeComparison.suggestion;
  }, [activeComparison, comparisonSuggestions]);
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await listData.fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };

  const invalidateFace = useCallback((faceId: number) => {
    queryClient.invalidateQueries({ queryKey: ["faces"] });
    queryClient.invalidateQueries({ queryKey: ["face", faceId] });
    queryClient.invalidateQueries({ queryKey: ["face", faceId, "suggestions"] });
  }, [queryClient]);

  const suggestionDecisionMutation = useMutation({
    mutationFn: (data: { faceId: number; performerId: number; decision: "accept" | "reject" | "merge"; setPerformerImage?: boolean; secondaryPerformerIds?: number[]; referenceEndpoint?: string; referenceExternalId?: string; referenceUpdateMetadata?: boolean }) =>
      faces.recordSuggestionDecision(data.faceId, { performerId: data.performerId, decision: data.decision, setPerformerImage: data.setPerformerImage, secondaryPerformerIds: data.secondaryPerformerIds, referenceEndpoint: data.referenceEndpoint, referenceExternalId: data.referenceExternalId, referenceUpdateMetadata: data.referenceUpdateMetadata }),
    onSuccess: (updatedFace, variables) => {
      queryClient.setQueryData(["face", variables.faceId], updatedFace);
      if (variables.decision === "accept") {
        const keepFace = faceMatchesPatchedQuery(updatedFace, query);
        queryClient.setQueriesData({ queryKey: ["faces", query] }, (data) => patchFaceListCache(data, updatedFace, keepFace));
      }
      invalidateFace(variables.faceId);
    },
  });

  const batchLinkTopSuggestionMutation = useMutation({
    mutationFn: (options: { createFromReference: boolean; linkConflicting: boolean; mergeConflicting: boolean }) =>
      faces.batchLinkTopSuggestion({ faceIds: selectedFaceIds, ...options }),
    onSuccess: () => {
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["faces"] });
    },
  });

  const batchDeleteMutation = useMutation({
    mutationFn: () => faces.batchDelete({ faceIds: selectedFaceIds }),
    onSuccess: (result) => {
      const deletedFaceIds = new Set(result.succeeded);
      queryClient.setQueriesData({ queryKey: ["faces", query] }, (data) => removeFacesFromListCache(data, deletedFaceIds));
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["faces"] });
    },
  });

  const handleFilterChange = useCallback((next: FindFilter) => {
    setFilter({ ...next, sort: readFaceSort(next.sort) });
  }, [setFilter]);

  const handleObjectFilterChange = useCallback((next: Record<string, unknown>) => {
    setObjectFilter(sanitizeFaceFilters(next));
  }, [setObjectFilter]);

  const compareBusy = suggestionDecisionMutation.isPending;

  const finishBatchCompare = useCallback(() => {
    setBatchCompareFaceIds([]);
    setBatchCompareIndex(0);
    selectNone();
  }, [selectNone]);

  const advanceBatchCompare = useCallback(() => {
    if (batchCompareIndex + 1 >= batchCompareFaceIds.length) {
      finishBatchCompare();
      return;
    }

    setBatchCompareIndex((current) => current + 1);
  }, [batchCompareFaceIds.length, batchCompareIndex, finishBatchCompare]);

  const handleConfirmSuggestion = useCallback((face: Face, suggestion: FaceTopSuggestion, options?: { setPerformerImage?: boolean }) => {
    suggestionDecisionMutation.mutate({ faceId: face.id, performerId: suggestion.performerId, decision: "accept", setPerformerImage: options?.setPerformerImage, ...readReferenceLinkInfo(suggestion) });
    setComparison(null);
  }, [suggestionDecisionMutation]);

  const handleStartBatchCompare = useCallback(() => {
    setBatchCompareFaceIds(batchComparableFaces.map((face) => face.id));
    setBatchCompareIndex(0);
    setComparison(null);
  }, [batchComparableFaces]);

  return (
    <>
      <ListPage
        title="Faces"
        pageKey="faces"
        filterMode="faces"
        filter={filter}
        onFilterChange={handleFilterChange}
        totalCount={totalCount}
        isLoading={isLoading}
        sortOptions={FACE_SORT_OPTIONS}
        displayMode={displayMode}
        onDisplayModeChange={setDisplayMode}
        showClearAllObjectFilters={false}
        availableDisplayModes={["grid", "list"]}
        allowInfinitePageSize
        showPagingControls={!listData.infinitePageSize}
        selectAllPending={listData.infinitePageSize ? selectAllMatchingPending : false}
        onSelectAllMatching={listData.infinitePageSize ? selectAll : undefined}
        selectAllMatchingLabel="Select shown"
        infiniteScroll={listData.infiniteScroll}
        criteriaDefinitions={FACE_CRITERIA}
        objectFilter={objectFilter}
        onObjectFilterChange={handleObjectFilterChange}
        selectedIds={selectedIds}
        onSelectAll={listData.infinitePageSize ? handleSelectAllMatching : selectAll}
        onSelectNone={selectNone}
        onInvertSelection={invertSelection}
        selectionActions={(
          <div className="flex flex-wrap items-center gap-2">
            {canWriteFaces ? (
              <button
                type="button"
                onClick={handleStartBatchCompare}
                disabled={batchComparableFaces.length === 0 || batchLinkTopSuggestionMutation.isPending || batchDeleteMutation.isPending || compareBusy}
                className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-accent hover:bg-accent/10 hover:text-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
                title="Compare selected unlinked faces that have top suggestions"
              >
                <Fingerprint className="h-3.5 w-3.5" />
                Compare
              </button>
            ) : null}
            {canWriteFaces ? (
              <button
                type="button"
                onClick={() => setConfirmBatchLink(true)}
                disabled={selectedFaceIds.length === 0 || batchLinkTopSuggestionMutation.isPending || batchDeleteMutation.isPending}
                className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-accent hover:bg-accent/10 hover:text-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Link2 className="h-3.5 w-3.5" />
                {batchLinkTopSuggestionMutation.isPending ? "Linking..." : "Link suggested"}
              </button>
            ) : null}
            {canDeleteFaces ? (
              <button
                type="button"
                onClick={() => setConfirmBatchDelete(true)}
                disabled={selectedFaceIds.length === 0 || batchLinkTopSuggestionMutation.isPending || batchDeleteMutation.isPending}
                className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-red-400 hover:bg-red-900/20 hover:text-red-300 disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Trash2 className="h-3.5 w-3.5" />
                {batchDeleteMutation.isPending ? "Deleting..." : "Delete"}
              </button>
            ) : null}
          </div>
        )}
        metadataByline={<span className="hidden text-xs text-muted lg:inline">Review face clusters with suggestions, links, and sample counts.</span>}
      >
        {displayMode === "grid" ? (
          <VirtualizedEntityGrid
            items={items}
            getItemKey={(face) => face.id}
            minCardWidth="var(--card-min-width, 200px)"
            estimateRowHeight={340}
            gap={16}
            gapClassName="gap-4"
            infinitePageSize={listData.infinitePageSize}
            hasNextPage={listData.infiniteQuery.hasNextPage}
            isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
            loadMore={listData.loadMore}
            renderItem={(face) => (
              <FaceTile
                face={face}
                onClick={() => selecting ? toggle(face.id) : onNavigate({ page: "face", id: face.id })}
                selected={selectedIds.has(face.id)}
                onSelect={() => toggle(face.id)}
                selecting={selecting}
              >
                {face.performerId ? (
                  <LinkedPerformerSummary face={face} onNavigate={onNavigate} canReadPerformers={canReadPerformers} />
                ) : (
                  <TopSuggestionFooter
                    face={face}
                    suggestion={face.topSuggestion}
                    onNavigate={onNavigate}
                    canReadPerformers={canReadPerformers}
                    canWriteFaces={canWriteFaces}
                    onOpenCompare={(suggestion) => setComparison({ face, suggestion })}
                    onLinkSuggestion={(suggestion) => handleConfirmSuggestion(face, suggestion)}
                    actionDisabled={compareBusy}
                  />
                )}
              </FaceTile>
            )}
          />
        ) : (
          <FaceListTable
            faces={items}
            onNavigate={onNavigate}
            canReadPerformers={canReadPerformers}
            canWriteFaces={canWriteFaces}
            onOpenCompare={(face, suggestion) => setComparison({ face, suggestion })}
            onLinkSuggestion={(face, suggestion) => handleConfirmSuggestion(face, suggestion)}
            actionDisabled={compareBusy}
            selectedIds={selectedIds}
            onToggle={toggle}
            selecting={selecting}
          />
        )}
        {items.length === 0 && !isLoading ? (
          <div className="rounded-xl border border-dashed border-border bg-card/40 px-4 py-12 text-center text-sm text-secondary">
            <Fingerprint className="mx-auto mb-3 h-8 w-8 text-muted" />
            No faces matched the current filters.
          </div>
        ) : null}
      </ListPage>

      <ConfirmDialog
        open={confirmBatchDelete}
        title="Delete selected faces?"
        message={`Delete ${selectedFaceIds.length} selected face${selectedFaceIds.length === 1 ? "" : "s"} and their AI artifacts. This cannot be undone.`}
        confirmLabel="Delete faces"
        onCancel={() => setConfirmBatchDelete(false)}
        onConfirm={() => {
          setConfirmBatchDelete(false);
          batchDeleteMutation.mutate();
        }}
      />

      {confirmBatchLink ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center">
          <div className="fixed inset-0 bg-black/60" onClick={() => setConfirmBatchLink(false)} />
          <div className="relative mx-4 w-full max-w-sm rounded-lg border border-border bg-surface p-6 shadow-xl">
            <h3 className="mb-2 text-lg font-semibold">Link suggested performers?</h3>
            <p className="mb-4 text-sm text-secondary">
              {`Link the top suggestion for ${selectedFaceIds.length} selected face${selectedFaceIds.length === 1 ? "" : "s"} that meet the confidence threshold.`}
            </p>
            <label className="mb-4 flex cursor-pointer items-start gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={createFromReference}
                onChange={(e) => setCreateFromReference(e.target.checked)}
                className="mt-0.5 rounded border-border bg-surface accent-accent"
              />
              <span>
                Create new performers from metadata servers for reference matches.
                <span className="mt-0.5 block text-xs text-muted">
                  When unchecked, faces whose only match is a reference pack identity without a local performer are skipped.
                </span>
              </span>
            </label>
            <label className="mb-4 flex cursor-pointer items-start gap-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={linkConflicting}
                onChange={(e) => setLinkConflicting(e.target.checked)}
                className="mt-0.5 rounded border-border bg-surface accent-accent"
              />
              <span>
                Also link faces with conflicting matches.
                <span className="mt-0.5 block text-xs text-muted">
                  When unchecked, faces that matched more than one performer are skipped so you can review them.
                </span>
              </span>
            </label>
            {linkConflicting ? (
              <div className="mb-4 ml-6 space-y-2 border-l border-border pl-3 text-sm text-secondary">
                <label className="flex cursor-pointer items-start gap-2">
                  <input
                    type="radio"
                    name="conflict-resolution"
                    checked={!mergeConflicting}
                    onChange={() => setMergeConflicting(false)}
                    className="mt-0.5 accent-accent"
                  />
                  <span>Link the top match directly.</span>
                </label>
                <label className="flex cursor-pointer items-start gap-2">
                  <input
                    type="radio"
                    name="conflict-resolution"
                    checked={mergeConflicting}
                    onChange={() => setMergeConflicting(true)}
                    className="mt-0.5 accent-accent"
                  />
                  <span>
                    Merge the matches into the top one.
                    <span className="mt-0.5 block text-xs text-muted">
                      The highest-confidence match becomes the primary; the others are folded in as aliases and links.
                    </span>
                  </span>
                </label>
              </div>
            ) : null}
            <div className="flex justify-end gap-3">
              <button
                type="button"
                onClick={() => setConfirmBatchLink(false)}
                className="px-4 py-2 text-sm text-secondary transition-colors hover:text-white"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => {
                  setConfirmBatchLink(false);
                  batchLinkTopSuggestionMutation.mutate({ createFromReference, linkConflicting, mergeConflicting });
                }}
                className="rounded-md bg-accent px-4 py-2 text-sm text-white transition-colors hover:bg-accent-hover"
              >
                Link suggested
              </button>
            </div>
          </div>
        </div>
      ) : null}

      <FaceCompareDialog
        open={activeComparison != null}
        face={activeComparison?.face ?? null}
        suggestion={comparisonSuggestion}
        siblingSuggestions={comparisonSuggestions}
        faceImageUrls={comparisonFaceImageUrls}
        disabled={compareBusy}
        canReadPerformers={canReadPerformers}
        batchLabel={batchCompareFaceIds.length > 0 ? `${batchCompareIndex + 1} of ${batchCompareFaceIds.length}` : undefined}
        onSkip={batchCompareFaceIds.length > 0 ? advanceBatchCompare : undefined}
        onClose={() => {
          setComparison(null);
          setBatchCompareFaceIds([]);
          setBatchCompareIndex(0);
        }}
        onConfirm={(suggestion, options) => {
          if (activeComparison) {
            suggestionDecisionMutation.mutate({ faceId: activeComparison.face.id, performerId: suggestion.performerId, decision: "accept", setPerformerImage: options?.setPerformerImage, ...readReferenceLinkInfo(suggestion) });
            if (batchCompareFaceIds.length > 0) {
              advanceBatchCompare();
            } else {
              setComparison(null);
            }
          }
        }}
        onReject={(suggestion) => {
          if (activeComparison) {
            suggestionDecisionMutation.mutate({ faceId: activeComparison.face.id, performerId: suggestion.performerId, decision: "reject" });
            if (batchCompareFaceIds.length > 0) {
              advanceBatchCompare();
            } else {
              setComparison(null);
            }
          }
        }}
        onMerge={(primaryPerformerId, secondaryPerformerIds) => {
          if (activeComparison) {
            suggestionDecisionMutation.mutate({ faceId: activeComparison.face.id, performerId: primaryPerformerId, decision: "merge", secondaryPerformerIds });
            if (batchCompareFaceIds.length > 0) {
              advanceBatchCompare();
            } else {
              setComparison(null);
            }
          }
        }}
        onNavigate={onNavigate}
      />
    </>
  );
}

function FaceListTable({
  faces,
  onNavigate,
  canReadPerformers,
  canWriteFaces,
  onOpenCompare,
  onLinkSuggestion,
  actionDisabled,
  selectedIds,
  onToggle,
  selecting,
}: {
  faces: Face[];
  onNavigate: (r: any) => void;
  canReadPerformers: boolean;
  canWriteFaces: boolean;
  onOpenCompare: (face: Face, suggestion: FaceTopSuggestion) => void;
  onLinkSuggestion: (face: Face, suggestion: FaceTopSuggestion) => void;
  actionDisabled?: boolean;
  selectedIds: Set<number>;
  onToggle: (id: number) => void;
  selecting: boolean;
}) {
  const density = useFaceListDensity();

  return (
    <div className="overflow-hidden rounded-xl border border-border bg-card">
      <div className="hidden grid-cols-[minmax(0,1.1fr)_110px_130px_minmax(0,1fr)_120px] gap-3 border-b border-border bg-surface/70 px-4 py-2 text-[11px] font-medium uppercase tracking-wide text-muted lg:grid">
        <span>Face</span>
        <span>Detections</span>
        <span>Videos / Images</span>
        <span>Top suggestion</span>
        <span>Updated</span>
      </div>
      <div className="divide-y divide-border">
        {faces.map((face) => (
          <FaceListRow
            key={face.id}
            face={face}
            onNavigate={onNavigate}
            canReadPerformers={canReadPerformers}
            canWriteFaces={canWriteFaces}
            onOpenCompare={(suggestion) => onOpenCompare(face, suggestion)}
            onLinkSuggestion={(suggestion) => onLinkSuggestion(face, suggestion)}
            actionDisabled={actionDisabled}
            selected={selectedIds.has(face.id)}
            onToggle={() => onToggle(face.id)}
            selecting={selecting}
            density={density}
          />
        ))}
      </div>
    </div>
  );
}

interface FaceListDensity {
  rowPaddingClassName: string;
  previewSize: number;
  showPreview: boolean;
  showMeta: boolean;
}

function useFaceListDensity(): FaceListDensity {
  const cardSize = useListPageCardSizeContext();
  const level = Math.max(0, Math.min(8, cardSize?.zoomLevel ?? 1));

  if (level <= 0.25) {
    return { rowPaddingClassName: "py-1.5", previewSize: 0, showPreview: false, showMeta: false };
  }

  if (level <= 0.75) {
    return { rowPaddingClassName: "py-2", previewSize: 0, showPreview: false, showMeta: true };
  }

  return {
    rowPaddingClassName: level >= 3 ? "py-4" : "py-3",
    previewSize: Math.round(Math.min(104, 48 + level * 8)),
    showPreview: true,
    showMeta: true,
  };
}

function FaceListRow({
  face,
  onNavigate,
  canReadPerformers,
  canWriteFaces,
  onOpenCompare,
  onLinkSuggestion,
  actionDisabled,
  selected,
  onToggle,
  selecting,
  density,
}: {
  face: Face;
  onNavigate: (r: any) => void;
  canReadPerformers: boolean;
  canWriteFaces: boolean;
  onOpenCompare: (suggestion: FaceTopSuggestion) => void;
  onLinkSuggestion: (suggestion: FaceTopSuggestion) => void;
  actionDisabled?: boolean;
  selected: boolean;
  onToggle: () => void;
  selecting: boolean;
  density: FaceListDensity;
}) {
  const title = faceDisplayName(face);

  return (
    <div
      onClick={selecting ? onToggle : undefined}
      className={`group relative cursor-pointer px-4 ${density.rowPaddingClassName} transition-colors ${selected ? "bg-accent/10" : "hover:bg-surface/40"}`}
    >
      <RouteCardLinkOverlay
        route={{ page: "face", id: face.id }}
        onClick={() => onNavigate({ page: "face", id: face.id })}
        label={`Open face ${title}`}
        disabled={selecting}
        selectionSafeZone
      />
      <div className="relative z-10 flex items-start gap-3 lg:grid lg:grid-cols-[minmax(0,1.1fr)_110px_130px_minmax(0,1fr)_120px] lg:items-center">
        <div className="relative min-w-0 pl-8">
          <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onToggle} />
          <div className="flex items-start gap-3">
            {density.showPreview ? <div className="hidden shrink-0 overflow-hidden rounded-full bg-surface sm:block" style={{ height: density.previewSize, width: density.previewSize }}>
              {face.coverImageUrl ? (
                      <img src={face.coverImageUrl} alt={title} className="h-full w-full bg-surface/85 object-contain p-1" loading="lazy" />
              ) : (
                <div className="flex h-full w-full items-center justify-center text-muted">
                  <Fingerprint className="h-6 w-6" />
                </div>
              )}
            </div> : null}
            <div className="min-w-0">
              <div className="truncate text-sm font-medium text-foreground">{title}</div>
              {density.showMeta ? <div className="mt-1 flex flex-wrap items-center gap-1.5 text-[11px] text-secondary">
                {face.performerId ? <Badge icon={<Link2 className="h-3 w-3" />} label={face.performerName || `Performer #${face.performerId}`} /> : null}
              </div> : null}
            </div>
          </div>
        </div>
        <div className="hidden text-xs text-secondary lg:block">{face.detectionCount}</div>
        <div className="hidden text-xs text-secondary lg:block">{face.videoCount} / {face.imageCount}</div>
        <div className="hidden lg:block">
          {face.performerId ? (
            <LinkedPerformerSummary face={face} onNavigate={onNavigate} canReadPerformers={canReadPerformers} compact />
          ) : (
            <TopSuggestionFooter
              face={face}
              suggestion={face.topSuggestion}
              onNavigate={onNavigate}
              canReadPerformers={canReadPerformers}
              canWriteFaces={canWriteFaces}
              onOpenCompare={onOpenCompare}
              onLinkSuggestion={onLinkSuggestion}
              actionDisabled={actionDisabled}
              compact
            />
          )}
        </div>
        <div className="hidden text-xs text-secondary lg:block">{formatDate(face.updatedAt)}</div>
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-3 pl-8 text-[11px] text-secondary lg:hidden">
        <span>{face.detectionCount} detections</span>
        <span>{face.videoCount} videos</span>
        <span>{face.imageCount} images</span>
        {face.topSuggestion ? <span>Top match: {face.topSuggestion.performerName}</span> : null}
      </div>
    </div>
  );
}

function TopSuggestionFooter({
  face,
  suggestion,
  onNavigate,
  canReadPerformers,
  canWriteFaces,
  onOpenCompare,
  onLinkSuggestion,
  actionDisabled,
  compact = false,
}: {
  face: Face;
  suggestion?: FaceTopSuggestion;
  onNavigate: (r: any) => void;
  canReadPerformers: boolean;
  canWriteFaces: boolean;
  onOpenCompare: (suggestion: FaceTopSuggestion) => void;
  onLinkSuggestion: (suggestion: FaceTopSuggestion) => void;
  actionDisabled?: boolean;
  compact?: boolean;
}) {
  if (!suggestion) {
    return <div className="text-xs text-secondary">No top suggestion yet.</div>;
  }

  const localPerformerId = suggestion.localPerformerId ?? (suggestion.performerId > 0 ? suggestion.performerId : undefined);
  const performerLinkProps = localPerformerId != null && canReadPerformers
    ? createNestedRouteLinkProps<HTMLAnchorElement>({ page: "performer", id: localPerformerId }, () => onNavigate({ page: "performer", id: localPerformerId }))
    : null;

  return (
      <div className={`relative z-20 flex items-center gap-3 ${compact ? "min-w-0" : ""}`}>
      <div className={`${compact ? "h-10 w-10" : "h-12 w-12"} shrink-0 overflow-hidden rounded-xl bg-surface/80`}>
        {suggestion.coverImageUrl ? (
          <img src={suggestion.coverImageUrl} alt={suggestion.performerName} className="h-full w-full object-cover object-top" loading="lazy" />
        ) : (
          <div className="flex h-full w-full items-center justify-center text-muted">
            <Fingerprint className="h-5 w-5" />
          </div>
        )}
      </div>
        <div className="min-w-0 flex-1 space-y-1">
          <div className="text-[11px] font-semibold uppercase tracking-wide text-muted">Top suggestion</div>
        {performerLinkProps ? (
          <a {...performerLinkProps} className="block truncate text-sm font-medium text-accent hover:underline">
            {suggestion.performerName}
          </a>
        ) : suggestion.externalUrl ? (
          <a href={suggestion.externalUrl} target="_blank" rel="noopener noreferrer" className="block truncate text-sm font-medium text-accent hover:underline">
            {suggestion.performerName}
          </a>
        ) : (
          <div className="truncate text-sm font-medium text-foreground">{suggestion.performerName}</div>
        )}
          <div className={canWriteFaces ? "pr-20 text-xs text-secondary" : "text-xs text-secondary"}>{formatPercent(suggestion.confidence)}% confidence</div>
        </div>
      {canWriteFaces ? (
        <div className="absolute bottom-0 right-0 z-20 flex shrink-0 items-center gap-1.5">
          <button
            type="button"
            onClick={(event) => {
              event.preventDefault();
              event.stopPropagation();
              onLinkSuggestion(suggestion);
            }}
            disabled={actionDisabled}
            title="Link"
            aria-label="Link top suggestion"
            className="inline-flex items-center gap-1 rounded-lg border border-accent bg-accent/10 px-1.5 py-1 text-xs font-medium text-accent transition-colors hover:bg-accent/15 disabled:cursor-not-allowed disabled:opacity-60"
          >
            <Link2 className="h-3.5 w-3.5" />
            <span className="sr-only">Link</span>
          </button>
          <button
            type="button"
            onClick={(event) => {
              event.preventDefault();
              event.stopPropagation();
              onOpenCompare(suggestion);
            }}
            disabled={actionDisabled}
            title="Compare"
            aria-label="Compare top suggestion"
            className="inline-flex items-center gap-1 rounded-lg border border-border px-1.5 py-1 text-xs font-medium text-foreground transition-colors hover:border-accent hover:text-accent disabled:cursor-not-allowed disabled:opacity-60"
          >
            <Fingerprint className="h-3.5 w-3.5" />
            <span className="sr-only">Compare</span>
          </button>
        </div>
      ) : null}
    </div>
  );
}

function LinkedPerformerSummary({
  face,
  onNavigate,
  canReadPerformers,
  compact = false,
}: {
  face: Face;
  onNavigate: (r: any) => void;
  canReadPerformers: boolean;
  compact?: boolean;
}) {
  const performerId = face.performerId;
  if (performerId == null) {
    return null;
  }

  const performerLinkProps = canReadPerformers
    ? createNestedRouteLinkProps<HTMLAnchorElement>({ page: "performer", id: performerId }, () => onNavigate({ page: "performer", id: performerId }))
    : null;

  return (
    <div className={`relative z-10 ${compact ? "text-xs" : "space-y-1"}`}>
      <div className="text-[11px] font-semibold uppercase tracking-wide text-muted">Linked performer</div>
      {performerLinkProps ? (
        <a {...performerLinkProps} className="truncate text-sm font-medium text-accent hover:underline">
          {face.performerName || `Performer #${performerId}`}
        </a>
      ) : (
        <div className="truncate text-sm font-medium text-foreground">{face.performerName || `Performer #${performerId}`}</div>
      )}
    </div>
  );
}

function Badge({ icon, label }: { icon: React.ReactNode; label: string }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-full border border-white/15 bg-black/35 px-2 py-0.5 text-[10px] font-medium text-white">
      {icon}
      {label}
    </span>
  );
}

function Metric({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-lg border border-border bg-surface/50 px-2 py-2">
      <div className="text-sm font-semibold text-foreground">{value}</div>
      <div className="mt-1 text-[10px] uppercase tracking-wide text-muted">{label}</div>
    </div>
  );
}

function formatPercent(value: number) {
  const scaled = value <= 1 ? value * 100 : value;
  return Math.max(0, Math.min(100, Math.round(scaled)));
}

