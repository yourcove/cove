import { useQuery } from "@tanstack/react-query";
import { segmentLibrary } from "../../api/client";
import { EntityMultiSelector } from "../../components/EntityMultiSelector";
import type { FilterDialogCustomSection } from "../../components/filterCriteriaTypes";
import {
  readBoolCriterion,
  readMultiIdCriterionIds,
  readNumberCriterion,
  readStringCriterion,
  readStringCriterionValue,
  readTimestampCriterion,
  readVideoSelectionCriterion,
  type SegmentNumberCriterionValue,
  type SegmentStringCriterionValue,
  type SegmentTimestampCriterionValue,
} from "./segmentCriteriaDefinitions";

export interface RawSegmentFilterValue {
  sourceCategory?: "user" | "extensions";
  sourceKey?: string;
  kind?: string;
  tagIds: number[];
  tagDepth?: -1;
  performerIds: number[];
  faceIds: number[];
  minConfidence?: number;
  minDurationSec?: number;
  titleCriterion?: SegmentStringCriterionValue;
  hostType?: string;
  sourceRunCriterion?: SegmentStringCriterionValue;
  colorHintCriterion?: SegmentStringCriterionValue;
  hasImage?: boolean;
  hasPayload?: boolean;
  createdAtCriterion?: SegmentTimestampCriterionValue;
  updatedAtCriterion?: SegmentTimestampCriterionValue;
  startSecCriterion?: SegmentNumberCriterionValue;
  endSecCriterion?: SegmentNumberCriterionValue;
  confidenceCriterion?: SegmentNumberCriterionValue;
  durationCriterion?: SegmentNumberCriterionValue;
}

export function createRawSegmentCustomFilterSection(): FilterDialogCustomSection {
  return {
    id: "rawSegmentFilters",
    label: "Raw Segments",
    filterKey: "rawSegmentFilters",
    defaultValue: createDefaultRawSegmentFilter(),
    isActive: isRawSegmentFilterActive,
    summarize: summarizeRawSegmentFilter,
    renderEditor: (value, onChange) => (
      <RawSegmentFilterEditor value={readRawSegmentFilter(value)} onChange={(nextValue) => onChange(nextValue)} />
    ),
  };
}

export function createDefaultRawSegmentFilter(): RawSegmentFilterValue {
  return {
    tagIds: [],
    performerIds: [],
    faceIds: [],
  };
}

export function readRawSegmentFilter(value: unknown): RawSegmentFilterValue {
  if (!value || typeof value !== "object") {
    return createDefaultRawSegmentFilter();
  }

  const candidate = value as {
    sourceCategory?: unknown;
    sourceKey?: unknown;
    kind?: unknown;
    tagIds?: unknown;
    performerIds?: unknown;
    faceIds?: unknown;
    minConfidence?: unknown;
    minDurationSec?: unknown;
  };

  return {
    sourceCategory:
      candidate.sourceCategory === "user" || candidate.sourceCategory === "extensions"
        ? candidate.sourceCategory
        : undefined,
    sourceKey: normalizeString(candidate.sourceKey),
    kind: normalizeString(candidate.kind),
    tagIds: normalizeIdArray(candidate.tagIds),
    performerIds: normalizeIdArray(candidate.performerIds),
    faceIds: normalizeIdArray(candidate.faceIds),
    minConfidence: normalizeFiniteNumber(candidate.minConfidence),
    minDurationSec: normalizeFiniteNumber(candidate.minDurationSec),
  };
}

export function readRawSegmentListFilter(objectFilter: Record<string, unknown>) {
  const legacy = readRawSegmentFilter(objectFilter.rawSegmentFilters);
  const videoSelection = readVideoSelectionCriterion(objectFilter.videosCriterion);
  const rawTagIds = readMultiIdCriterionIds(objectFilter.rawTagsCriterion);
  const rawPerformerIds = readMultiIdCriterionIds(objectFilter.rawPerformersCriterion);
  const rawFaceIds = readMultiIdCriterionIds(objectFilter.rawFacesCriterion);
  const rawKind = readStringCriterion(objectFilter.rawKindCriterion);
  const rawSourceKey = readStringCriterion(objectFilter.rawSourceCriterion);

  return {
    videoTitle: readStringCriterion(objectFilter.videoTitleCriterion) || undefined,
    videoIds: videoSelection.includeIds,
    excludeVideoIds: videoSelection.excludeIds,
    tagIds: Array.from(new Set([...legacy.tagIds, ...rawTagIds])),
    performerIds: Array.from(new Set([...legacy.performerIds, ...rawPerformerIds])),
    faceIds: Array.from(new Set([...legacy.faceIds, ...rawFaceIds])),
    kind: (legacy.kind ?? rawKind) || undefined,
    sourceKey: (legacy.sourceKey ?? rawSourceKey) || undefined,
    sourceCategory: legacy.sourceCategory,
    titleCriterion: readStringCriterionValue(objectFilter.rawTitleCriterion),
    hostType: readStringCriterion(objectFilter.rawHostTypeCriterion) || undefined,
    sourceRunCriterion: readStringCriterionValue(objectFilter.rawSourceRunCriterion),
    colorHintCriterion: readStringCriterionValue(objectFilter.rawColorHintCriterion),
    hasImage: readBoolCriterion(objectFilter.rawHasImageCriterion)?.value,
    hasPayload: readBoolCriterion(objectFilter.rawHasPayloadCriterion)?.value,
    createdAtCriterion: readTimestampCriterion(objectFilter.rawCreatedAtCriterion),
    updatedAtCriterion: readTimestampCriterion(objectFilter.rawUpdatedAtCriterion),
    startSecCriterion: readNumberCriterion(objectFilter.rawStartSecCriterion),
    endSecCriterion: readNumberCriterion(objectFilter.rawEndSecCriterion),
    confidenceCriterion: readNumberCriterion(objectFilter.rawConfidenceCriterion) ?? legacy.confidenceCriterion,
    durationCriterion: readNumberCriterion(objectFilter.rawDurationCriterion) ?? legacy.durationCriterion,
    minConfidence: legacy.minConfidence,
    minDurationSec: legacy.minDurationSec,
  };
}

export function isRawSegmentFilterActive(value: unknown) {
  const filter = readRawSegmentFilter(value);
  return Boolean(
    filter.sourceCategory ||
    filter.sourceKey ||
    filter.kind ||
    filter.tagIds.length > 0 ||
    filter.performerIds.length > 0 ||
    filter.faceIds.length > 0 ||
    filter.minConfidence != null ||
    filter.minDurationSec != null,
  );
}

function summarizeRawSegmentFilter(value: unknown) {
  const filter = readRawSegmentFilter(value);
  const parts: string[] = [];
  if (filter.sourceCategory === "extensions") parts.push("Extensions");
  if (filter.sourceCategory === "user") parts.push("User");
  if (filter.sourceKey) parts.push(filter.sourceKey);
  if (filter.kind) parts.push(filter.kind);
  if (filter.tagIds.length > 0) parts.push(`${filter.tagIds.length} tag${filter.tagIds.length === 1 ? "" : "s"}`);
  if (filter.performerIds.length > 0)
    parts.push(`${filter.performerIds.length} performer${filter.performerIds.length === 1 ? "" : "s"}`);
  if (filter.faceIds.length > 0) parts.push(`${filter.faceIds.length} face${filter.faceIds.length === 1 ? "" : "s"}`);
  if (filter.minConfidence != null) parts.push(`${Math.round(filter.minConfidence * 100)}%+ confidence`);
  if (filter.minDurationSec != null) parts.push(`${filter.minDurationSec}s+`);

  return parts.length > 0 ? parts.join(" · ") : "Raw segments";
}

function RawSegmentFilterEditor({
  value,
  onChange,
}: {
  value: RawSegmentFilterValue;
  onChange: (value: RawSegmentFilterValue) => void;
}) {
  const sourceOptionsQuery = useQuery({
    queryKey: ["segments-page", "raw-filter", "source-keys"],
    queryFn: () => segmentLibrary.distinctSourceKeys(),
    staleTime: 60_000,
  });
  const kindOptionsQuery = useQuery({
    queryKey: ["segments-page", "raw-filter", "kinds"],
    queryFn: () => segmentLibrary.distinctKinds(),
    staleTime: 60_000,
  });
  const sourceOptions = sourceOptionsQuery.data ?? [];
  const kindOptions = kindOptionsQuery.data ?? [];

  return (
    <div className="space-y-4">
      <div className="grid gap-3 md:grid-cols-2 lg:grid-cols-4">
        <label className="space-y-1 text-xs text-secondary">
          <span className="font-semibold uppercase tracking-wide text-muted">Provider</span>
          <select
            value={value.sourceCategory ?? ""}
            onChange={(event) =>
              onChange({
                ...value,
                sourceCategory:
                  event.target.value === "user" || event.target.value === "extensions" ? event.target.value : undefined,
              })
            }
            className="w-full rounded border border-border bg-input px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
          >
            <option value="">Any provider</option>
            <option value="extensions">Extensions</option>
            <option value="user">User-created</option>
          </select>
        </label>
        <label className="space-y-1 text-xs text-secondary">
          <span className="font-semibold uppercase tracking-wide text-muted">Source</span>
          <select
            value={value.sourceKey ?? ""}
            onChange={(event) => onChange({ ...value, sourceKey: event.target.value || undefined })}
            className="w-full rounded border border-border bg-input px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
          >
            <option value="">Any source</option>
            {sourceOptionsQuery.isLoading && sourceOptions.length === 0 ? (
              <option value="" disabled>
                Loading sources...
              </option>
            ) : null}
            {sourceOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.value} ({option.count})
              </option>
            ))}
          </select>
        </label>
        <label className="space-y-1 text-xs text-secondary">
          <span className="font-semibold uppercase tracking-wide text-muted">Kind</span>
          <select
            value={value.kind ?? ""}
            onChange={(event) => onChange({ ...value, kind: event.target.value || undefined })}
            className="w-full rounded border border-border bg-input px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
          >
            <option value="">Any kind</option>
            {kindOptionsQuery.isLoading && kindOptions.length === 0 ? (
              <option value="" disabled>
                Loading kinds...
              </option>
            ) : null}
            {kindOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.value} ({option.count})
              </option>
            ))}
          </select>
        </label>
        <label className="space-y-1 text-xs text-secondary">
          <span className="font-semibold uppercase tracking-wide text-muted">Minimum confidence</span>
          <input
            type="number"
            min="0"
            max="1"
            step="0.01"
            value={value.minConfidence ?? ""}
            onChange={(event) => onChange({ ...value, minConfidence: parseOptionalNumber(event.target.value) })}
            className="w-full rounded border border-border bg-input px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
            placeholder="Optional"
          />
        </label>
      </div>

      <div className="grid gap-3 lg:grid-cols-4">
        <label className="space-y-1 text-xs text-secondary">
          <span className="font-semibold uppercase tracking-wide text-muted">Minimum duration (sec)</span>
          <input
            type="number"
            min="0"
            step="0.1"
            value={value.minDurationSec ?? ""}
            onChange={(event) => onChange({ ...value, minDurationSec: parseOptionalNumber(event.target.value) })}
            className="w-full rounded border border-border bg-input px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
            placeholder="Optional"
          />
        </label>
        <div className="space-y-1">
          <div className="text-xs font-semibold uppercase tracking-wide text-muted">Tags</div>
          <EntityMultiSelector
            entityType="tags"
            values={value.tagIds}
            onChange={(tagIds) => onChange({ ...value, tagIds })}
            placeholder="Search tags..."
            emptyMessage="No tags found"
          />
        </div>
        <div className="space-y-1">
          <div className="text-xs font-semibold uppercase tracking-wide text-muted">Performers</div>
          <EntityMultiSelector
            entityType="performers"
            values={value.performerIds}
            onChange={(performerIds) => onChange({ ...value, performerIds })}
            placeholder="Search performers..."
            emptyMessage="No performers found"
          />
        </div>
        <div className="space-y-1">
          <div className="text-xs font-semibold uppercase tracking-wide text-muted">Faces</div>
          <EntityMultiSelector
            entityType="faces"
            values={value.faceIds}
            onChange={(faceIds) => onChange({ ...value, faceIds })}
            placeholder="Search faces..."
            emptyMessage="No faces found"
          />
        </div>
      </div>
    </div>
  );
}

function normalizeString(value: unknown) {
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : undefined;
}

function normalizeIdArray(value: unknown) {
  return Array.isArray(value)
    ? value.filter((item): item is number => typeof item === "number" && Number.isFinite(item) && item > 0)
    : [];
}

function normalizeFiniteNumber(value: unknown) {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function parseOptionalNumber(value: string) {
  const trimmed = value.trim();
  if (!trimmed) {
    return undefined;
  }

  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : undefined;
}
