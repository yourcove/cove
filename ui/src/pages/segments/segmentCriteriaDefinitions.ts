import type { CriterionDefinition } from "../../components/filterCriteriaTypes";
import type { BoolCriterion, CriterionModifier, IntCriterion, MultiIdCriterion, StringCriterion, TimestampCriterion } from "../../api/types";
import type { SegmentsPageContentView } from "./types";

const SEGMENT_NUMBER_MODIFIERS: CriterionModifier[] = ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"];
const SEGMENT_INCLUDE_ONLY_MODIFIERS: CriterionModifier[] = ["INCLUDES"];

export interface SegmentCriteriaOptions {
  kindOptions?: { value: string; label: string }[];
  sourceOptions?: { value: string; label: string }[];
}

export const SEGMENT_CRITERIA: CriterionDefinition[] = [
  { id: "videoTitle", label: "Video Title", type: "string", filterKey: "videoTitleCriterion" },
  { id: "videos", label: "Videos", type: "multiId", entityType: "videos", filterKey: "videosCriterion" },
  { id: "videoTags", label: "Video Tags", type: "multiId", entityType: "tags", filterKey: "videoTagsCriterion", modifiers: SEGMENT_INCLUDE_ONLY_MODIFIERS },
];

export function createSegmentCriteria(options: SegmentCriteriaOptions = {}): CriterionDefinition[] {
  return [
  ...SEGMENT_CRITERIA,
  { id: "title", label: "Segment Title", type: "string", filterKey: "rawTitleCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "rawCreatedAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "rawUpdatedAtCriterion" },
  { id: "startSec", label: "Start Time", type: "duration", filterKey: "rawStartSecCriterion", modifiers: SEGMENT_NUMBER_MODIFIERS },
  { id: "endSec", label: "End Time", type: "duration", filterKey: "rawEndSecCriterion", modifiers: SEGMENT_NUMBER_MODIFIERS },
  { id: "hostType", label: "Host Type", type: "enum", filterKey: "rawHostTypeCriterion", modifiers: ["EQUALS"], options: [{ value: "video", label: "Video" }] },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "rawTagsCriterion", modifiers: SEGMENT_INCLUDE_ONLY_MODIFIERS },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "rawPerformersCriterion", modifiers: SEGMENT_INCLUDE_ONLY_MODIFIERS },
  { id: "faces", label: "Faces", type: "multiId", entityType: "faces", filterKey: "rawFacesCriterion", modifiers: SEGMENT_INCLUDE_ONLY_MODIFIERS },
  { id: "kind", label: "Segment Type", type: "enum", filterKey: "rawKindCriterion", modifiers: ["EQUALS"], options: options.kindOptions ?? [] },
  { id: "source", label: "Source", type: "enum", filterKey: "rawSourceCriterion", modifiers: ["EQUALS"], options: options.sourceOptions ?? [] },
  { id: "sourceRun", label: "Source Run", type: "string", filterKey: "rawSourceRunCriterion" },
  { id: "colorHint", label: "Color Hint", type: "string", filterKey: "rawColorHintCriterion" },
  { id: "hasImage", label: "Has Image", type: "bool", filterKey: "rawHasImageCriterion" },
  { id: "hasPayload", label: "Has Payload", type: "bool", filterKey: "rawHasPayloadCriterion" },
  { id: "confidence", label: "Confidence", type: "number", filterKey: "rawConfidenceCriterion", modifiers: SEGMENT_NUMBER_MODIFIERS },
  { id: "duration", label: "Duration", type: "duration", filterKey: "rawDurationCriterion", modifiers: SEGMENT_NUMBER_MODIFIERS },
  ];
}

export const RAW_SEGMENT_CRITERIA: CriterionDefinition[] = createSegmentCriteria();

export interface VideoSelectionCriterion {
  includeIds: number[];
  excludeIds: number[];
}

interface VideoSelectionCriterionValue {
  value?: unknown;
  excludes?: unknown;
}

export function readStringCriterion(value: unknown) {
  if (!value || typeof value !== "object") {
    return "";
  }

  const candidate = (value as { value?: unknown }).value;
  return typeof candidate === "string" ? candidate.trim() : "";
}

export function readVideoSelectionCriterion(value: unknown): VideoSelectionCriterion {
  if (!value || typeof value !== "object") {
    return { includeIds: [], excludeIds: [] };
  }

  const criterion = value as VideoSelectionCriterionValue;
  const included = Array.isArray(criterion.value)
    ? criterion.value.filter((item): item is number => typeof item === "number" && Number.isFinite(item))
    : [];
  const excluded = Array.isArray(criterion.excludes)
    ? criterion.excludes.filter((item): item is number => typeof item === "number" && Number.isFinite(item))
    : [];

  return {
    includeIds: included,
    excludeIds: excluded,
  };
}

export function readSegmentsPageContentView(): SegmentsPageContentView {
  const params = new URLSearchParams(window.location.search);
  return params.get("segmentsView") === "raw" ? "raw" : "spans";
}

export function readRawSegmentIdsFromUrl() {
  const params = new URLSearchParams(window.location.search);
  const raw = params.get("rawIds");
  if (!raw) {
    return [] as number[];
  }

  return raw
    .split(",")
    .map((value) => Number(value))
    .filter((value) => Number.isInteger(value) && value > 0);
}

export function readMultiIdCriterionIds(value: unknown) {
  if (!value || typeof value !== "object") {
    return [] as number[];
  }

  const criterion = value as Partial<MultiIdCriterion>;
  return Array.isArray(criterion.value)
    ? criterion.value.filter((item): item is number => typeof item === "number" && Number.isFinite(item) && item > 0)
    : [];
}

export function readMultiIdCriterionDepth(value: unknown): -1 | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  return (value as Partial<MultiIdCriterion>).depth === -1 ? -1 : undefined;
}

export function readMinimumNumberCriterion(value: unknown) {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  const criterion = value as Partial<IntCriterion>;
  if (typeof criterion.value !== "number" || !Number.isFinite(criterion.value)) {
    return undefined;
  }

  if (criterion.modifier === "BETWEEN" && typeof criterion.value2 === "number" && Number.isFinite(criterion.value2)) {
    return Math.min(criterion.value, criterion.value2);
  }

  return criterion.value;
}

export interface SegmentNumberCriterionValue {
  modifier?: CriterionModifier;
  value?: number;
  value2?: number;
}

export interface SegmentStringCriterionValue {
  modifier?: CriterionModifier;
  value?: string;
}

export interface SegmentTimestampCriterionValue {
  modifier?: CriterionModifier;
  value?: string;
  value2?: string;
}

export function readNumberCriterion(value: unknown): SegmentNumberCriterionValue | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  const criterion = value as Partial<IntCriterion>;
  if (typeof criterion.value !== "number" || !Number.isFinite(criterion.value)) {
    return undefined;
  }

  return {
    modifier: criterion.modifier,
    value: criterion.value,
    value2: typeof criterion.value2 === "number" && Number.isFinite(criterion.value2) ? criterion.value2 : undefined,
  };
}

export function readStringCriterionValue(value: unknown): SegmentStringCriterionValue | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  const criterion = value as Partial<StringCriterion>;
  const modifier = criterion.modifier;
  const text = typeof criterion.value === "string" ? criterion.value.trim() : "";
  if ((modifier === "IS_NULL" || modifier === "NOT_NULL") || text.length > 0) {
    return { modifier, value: text };
  }

  return undefined;
}

export function readTimestampCriterion(value: unknown): SegmentTimestampCriterionValue | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  const criterion = value as Partial<TimestampCriterion>;
  const text = typeof criterion.value === "string" ? criterion.value.trim() : "";
  if (text.length === 0) {
    return undefined;
  }

  return {
    modifier: criterion.modifier,
    value: text,
    value2: typeof criterion.value2 === "string" && criterion.value2.trim().length > 0 ? criterion.value2.trim() : undefined,
  };
}

export function readBoolCriterion(value: unknown): BoolCriterion | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }

  const criterion = value as Partial<BoolCriterion>;
  return typeof criterion.value === "boolean" ? { value: criterion.value } : undefined;
}
