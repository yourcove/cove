import { useState, useMemo, useCallback, useEffect, useId, useRef, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from "react";
import { useQueries, useQuery } from "@tanstack/react-query";
import { X, Search, Pin, PinOff, Plus, Minus, Star, ArrowLeft } from "lucide-react";
import { tags as tagsApi, performers as performersApi, studios as studiosApi, groups as groupsApi, galleries as galleriesApi, videos as videosApi, tagGroups as tagGroupsApi, faces as facesApi, metadata } from "../api/client";
import { GroupedTagOptionList, groupTagsForSelector } from "./TagSelector";
import { IsoDateInput } from "./IsoDateInput";
import { EntityReferenceSelector } from "./EntityReferenceSelector";
import { formatHumanDuration } from "../utils/durationFormat";
import {
  convertToRatingFormat,
  convertFromRatingFormat,
  getRatingMax,
  getRatingStep,
  getRatingPrecision,
  useRatingOptions,
} from "./Rating";
import type {
  CriterionModifier,
  IntCriterion,
  StringCriterion,
  BoolCriterion,
  MultiIdCriterion,
  DateCriterion,
  TimestampCriterion,
  FingerprintCriterion,
  CustomFieldCriterion,
  TagDurationClause,
  TagDurationCriterion,
  VideoFilterCriteria,
  PerformerFilterCriteria,
  TagFilterCriteria,
  StudioFilterCriteria,
  GalleryFilterCriteria,
  ImageFilterCriteria,
  AudioFilterCriteria,
  TextFilterCriteria,
  GroupFilterCriteria,
  MetadataServer,
} from "../api/types";
import { RESOLUTION_FILTER_OPTIONS } from "../utils/resolutionBuckets";
import { rankByLabel } from "../utils/searchRanking";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { ActiveObjectFilterChips } from "./ActiveObjectFilterChips";
import { ConfirmDialog } from "./ConfirmDialog";
import { getMultiIdModifierLabel } from "../utils/filterModifierLabels";
import { pushOverlay } from "../utils/overlayState";
import { LibraryFolderTree } from "./LibraryFolderTree";

// ===== Criterion definitions =====

export type CriterionType = "string" | "path" | "remoteId" | "number" | "bool" | "date" | "timestamp" | "duration" | "tagDuration" | "careerLength" | "rating" | "resolution" | "multiId" | "enum" | "hash";
export type EntityType = "tags" | "tagGroups" | "performers" | "studios" | "groups" | "galleries" | "videos" | "faces";

export interface CriterionDefinition<TFilterKey extends string = string> {
  id: string;
  label: string;
  type: CriterionType;
  entityType?: EntityType;
  filterKey: TFilterKey;
  customFieldKey?: string;
  customFieldType?: string;
  modifiers?: CriterionModifier[];
  /**
   * Modifier to start on when the criterion has no value yet. Needed whenever `modifiers` omits the editor's
   * built-in default, which would otherwise leave the Match control with nothing selected and let the user save a
   * criterion whose modifier isn't even offered. Honored by the number editor.
   */
  defaultModifier?: CriterionModifier;
  /**
   * Bounds and granularity for a numeric criterion. `step` sizes the input's increments; declaring BOTH `min` and
   * `max` additionally marks the value as living on a known range, which the number editor shows as a slider —
   * the useful question about a bounded value being "where on its range" rather than "which exact number".
   */
  min?: number;
  max?: number;
  step?: number;
  /** Short note under a numeric editor explaining what its scale means (e.g. where the neutral point is). */
  hint?: string;
  options?: { value: string; label: string }[];
  multiSelectOptions?: boolean;
  hierarchyToggleLabel?: string;
  auxiliaryToggleKey?: TFilterKey;
  auxiliaryToggleLabel?: string;
  secondaryFilterKey?: TFilterKey;
  supported?: boolean;
  unsupportedReason?: string;
}

type CriteriaDefinitionList<TFilterCriteria> = CriterionDefinition<Extract<keyof TFilterCriteria, string>>[];

// Modifier labels
const MODIFIER_LABELS: Record<CriterionModifier, string> = {
  EQUALS: "=",
  NOT_EQUALS: "≠",
  GREATER_THAN: ">",
  LESS_THAN: "<",
  INCLUDES: "Includes",
  EXCLUDES: "Excludes",
  INCLUDES_ALL: "Includes All",
  EXCLUDES_ALL: "Excludes All",
  IS_NULL: "Is Null",
  NOT_NULL: "Not Null",
  BETWEEN: "Between",
  NOT_BETWEEN: "Not Between",
  MATCHES_REGEX: "Regex",
  NOT_MATCHES_REGEX: "Not Regex",
  UNDER_PATH: "Under",
  NOT_UNDER_PATH: "Not Under",
};

// Which modifiers each type supports
const TYPE_MODIFIERS: Record<CriterionType, CriterionModifier[]> = {
  string: ["EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
  path: ["UNDER_PATH", "NOT_UNDER_PATH", "EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
  remoteId: ["EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
  hash: ["EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
  number: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  bool: ["EQUALS"],
  date: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  timestamp: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  duration: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"],
  tagDuration: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"],
  careerLength: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"],
  rating: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"],
  resolution: ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN"],
  multiId: ["INCLUDES", "INCLUDES_ALL", "EXCLUDES", "EXCLUDES_ALL", "IS_NULL", "NOT_NULL"],
  enum: ["EQUALS", "NOT_EQUALS", "IS_NULL", "NOT_NULL"],
};

const NON_NULL_NUMBER_MODIFIERS: CriterionModifier[] = ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"];
const NON_NULL_TIMESTAMP_MODIFIERS: CriterionModifier[] = ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"];
const VALUE_ONLY_ENUM_MODIFIERS: CriterionModifier[] = ["EQUALS", "NOT_EQUALS"];
const NULL_VALUE_MODIFIERS = new Set<CriterionModifier>(["IS_NULL", "NOT_NULL"]);
const RANGE_VALUE_MODIFIERS = new Set<CriterionModifier>(["BETWEEN", "NOT_BETWEEN"]);
const VIDEO_HASH_OPTIONS = [
  { value: "oshash", label: "OSHash" },
  { value: "md5", label: "MD5" },
  { value: "phash", label: "pHash" },
] as const;
const VISUAL_HASH_OPTIONS = [
  { value: "md5", label: "MD5" },
  { value: "phash", label: "pHash" },
] as const;

function hasStringCriterionValue(criterion: { modifier?: CriterionModifier; value?: string; value2?: string }) {
  const modifier = criterion.modifier ?? "EQUALS";
  if (NULL_VALUE_MODIFIERS.has(modifier)) {
    return true;
  }

  const value = criterion.value?.trim() ?? "";
  if (value === "") {
    return false;
  }

  if (RANGE_VALUE_MODIFIERS.has(modifier)) {
    return (criterion.value2?.trim() ?? "") !== "";
  }

  return true;
}

function hasNumericCriterionValue(criterion: { modifier?: CriterionModifier; value?: number; value2?: number }) {
  const modifier = criterion.modifier ?? "EQUALS";
  if (NULL_VALUE_MODIFIERS.has(modifier)) {
    return true;
  }

  if (typeof criterion.value !== "number" || Number.isNaN(criterion.value)) {
    return false;
  }

  if (RANGE_VALUE_MODIFIERS.has(modifier)) {
    return typeof criterion.value2 === "number" && !Number.isNaN(criterion.value2);
  }

  return true;
}

function hasFingerprintCriterionValue(criterion: { modifier?: CriterionModifier; value?: string; type?: string }) {
  if ((criterion.type?.trim() ?? "") === "") {
    return false;
  }

  return hasStringCriterionValue(criterion);
}

function isTagDurationClauseValid(clause: TagDurationClause | undefined) {
  return Boolean(clause?.tagId && clause.tagId > 0 && hasNumericCriterionValue(clause));
}

function getTagDurationClauses(value: TagDurationCriterion | undefined) {
  if (!value) {
    return [];
  }

  if (Array.isArray(value.clauses) && value.clauses.length > 0) {
    return value.clauses;
  }

  return [value];
}

function isCriterionValueValid(value: unknown, criterion: CriterionDefinition) {
  if (value == null) {
    return false;
  }

  switch (criterion.type) {
    case "bool":
      return typeof (value as BoolCriterion).value === "boolean";
    case "multiId": {
      const criterionValue = value as MultiIdCriterion;
      if (NULL_VALUE_MODIFIERS.has(criterionValue.modifier ?? "INCLUDES")) {
        return true;
      }
      const ids = criterionValue.value;
      const excludes = criterionValue.excludes;
      return (Array.isArray(ids) && ids.length > 0) || (Array.isArray(excludes) && excludes.length > 0);
    }
    case "tagDuration": {
      const criterionValue = value as TagDurationCriterion;
      return getTagDurationClauses(criterionValue).some((clause) => isTagDurationClauseValid(clause));
    }
    case "string":
    case "path":
    case "remoteId":
    case "hash":
    case "date":
    case "timestamp":
    case "enum":
      return criterion.type === "remoteId"
        ? Boolean((value as { _legacyEndpointCriterion?: StringCriterion })._legacyEndpointCriterion
          ? (
            (value as { endpoint?: string }).endpoint?.trim()
            || NULL_VALUE_MODIFIERS.has((value as StringCriterion).modifier ?? "EQUALS")
          )
          : (
            NULL_VALUE_MODIFIERS.has((value as StringCriterion).modifier ?? "EQUALS")
            || (value as StringCriterion).value?.trim()
          ))
        : criterion.type === "hash"
        ? hasFingerprintCriterionValue(value as { modifier?: CriterionModifier; value?: string; type?: string })
        : hasStringCriterionValue(value as { modifier?: CriterionModifier; value?: string; value2?: string });
    case "number":
    case "duration":
    case "careerLength":
    case "rating":
    case "resolution":
      return hasNumericCriterionValue(value as { modifier?: CriterionModifier; value?: number; value2?: number });
    default:
      return true;
  }
}

function getCustomFieldCriteria(filter: Record<string, unknown>) {
  return Array.isArray(filter.customFieldCriteria)
    ? filter.customFieldCriteria.filter((item): item is CustomFieldCriterion => Boolean(item && typeof item === "object"))
    : [];
}

function findCustomFieldCriterion(filter: Record<string, unknown>, criterion: CriterionDefinition) {
  if (!criterion.customFieldKey) return undefined;
  return getCustomFieldCriteria(filter).find((item) => item.key === criterion.customFieldKey);
}

function coerceCustomFieldCriterionForEditor(value: CustomFieldCriterion | undefined, criterion: CriterionDefinition) {
  if (!value) return undefined;
  const next: Record<string, unknown> = { ...value };
  const coerceNumber = (rawValue: unknown) => {
    if (rawValue == null || rawValue === "") return undefined;
    const numericValue = Number(rawValue);
    return Number.isFinite(numericValue) ? numericValue : undefined;
  };

  switch (criterion.type) {
    case "number":
    case "duration":
    case "careerLength":
    case "rating":
    case "resolution":
      next.value = coerceNumber(value.value);
      next.value2 = coerceNumber(value.value2);
      break;
    case "bool":
      next.value = String(value.value).toLowerCase() === "true";
      break;
  }

  return next;
}

function getCriterionFilterValue(filter: Record<string, unknown>, criterion: CriterionDefinition) {
  if (criterion.type === "remoteId" && criterion.secondaryFilterKey) {
    const valueCriterion = filter[criterion.filterKey] as StringCriterion | undefined;
    const endpointCriterion = filter[criterion.secondaryFilterKey] as StringCriterion | undefined;
    if (!valueCriterion && !endpointCriterion) return undefined;
    return {
      ...(valueCriterion ?? { value: "", modifier: endpointCriterion?.modifier ?? "EQUALS" }),
      endpoint: endpointCriterion?.value ?? "",
      _legacyEndpointCriterion: valueCriterion ? undefined : endpointCriterion,
    };
  }
  return criterion.customFieldKey ? coerceCustomFieldCriterionForEditor(findCustomFieldCriterion(filter, criterion), criterion) : filter[criterion.filterKey];
}

function normalizeCustomFieldCriterion(value: unknown, criterion: CriterionDefinition): CustomFieldCriterion | undefined {
  if (!criterion.customFieldKey || !value || typeof value !== "object") return undefined;

  const raw = value as Record<string, unknown>;
  const normalized: CustomFieldCriterion = {
    ...(raw as Partial<CustomFieldCriterion>),
    key: criterion.customFieldKey,
    type: (criterion.customFieldType ?? "text") as CustomFieldCriterion["type"],
    modifier: (raw.modifier as CriterionModifier | undefined) ?? "EQUALS",
    value: raw.value == null ? "" : String(raw.value),
  };

  if (raw.value2 != null) {
    normalized.value2 = String(raw.value2);
  } else {
    delete normalized.value2;
  }

  return normalized;
}

function removeCriterionFilterValue(filter: Record<string, unknown>, criterion: CriterionDefinition) {
  const next = { ...filter };
  if (criterion.customFieldKey) {
    const remaining = getCustomFieldCriteria(next).filter((item) => item.key !== criterion.customFieldKey);
    if (remaining.length > 0) next.customFieldCriteria = remaining;
    else delete next.customFieldCriteria;
    return next;
  }

  delete next[criterion.filterKey];
  if (criterion.secondaryFilterKey) delete next[criterion.secondaryFilterKey];
  if (criterion.auxiliaryToggleKey) {
    delete next[criterion.auxiliaryToggleKey];
  }
  return next;
}

function setCriterionFilterValue(filter: Record<string, unknown>, criterion: CriterionDefinition, value: unknown) {
  if (value === undefined) {
    return removeCriterionFilterValue(filter, criterion);
  }

  if (criterion.customFieldKey) {
    const customFieldCriterion = normalizeCustomFieldCriterion(value, criterion);
    if (!customFieldCriterion) return removeCriterionFilterValue(filter, criterion);

    const remaining = getCustomFieldCriteria(filter).filter((item) => item.key !== criterion.customFieldKey);
    return { ...filter, customFieldCriteria: [...remaining, customFieldCriterion] };
  }

  if (criterion.type === "remoteId" && criterion.secondaryFilterKey) {
    const raw = value as StringCriterion & { endpoint?: string; _legacyEndpointCriterion?: StringCriterion };
    const next = removeCriterionFilterValue(filter, criterion);
    if (raw._legacyEndpointCriterion && !(raw.value?.trim())) {
      next[criterion.secondaryFilterKey] = raw._legacyEndpointCriterion;
      return next;
    }
    const endpoint = raw.endpoint?.trim() ?? "";
    if (endpoint) next[criterion.secondaryFilterKey] = { value: endpoint, modifier: "EQUALS" };
    if (NULL_VALUE_MODIFIERS.has(raw.modifier ?? "EQUALS") || raw.value?.trim()) {
      next[criterion.filterKey] = { value: raw.value ?? "", modifier: raw.modifier ?? "EQUALS" };
    }
    return next;
  }

  return { ...filter, [criterion.filterKey]: value };
}

function sanitizeFilterCriteria(filter: Record<string, unknown>, criteria: CriterionDefinition[], baseFilter: Record<string, unknown> = {}) {
  let sanitized: Record<string, unknown> = { ...baseFilter };

  for (const criterion of criteria) {
    const value = getCriterionFilterValue(filter, criterion);
    if (!isCriterionValueValid(value, criterion)) {
      continue;
    }

    if (criterion.customFieldKey || criterion.type === "remoteId") {
      sanitized = setCriterionFilterValue(sanitized, criterion, value);
    } else {
      sanitized[criterion.filterKey] = value;
    }

    if (criterion.auxiliaryToggleKey && typeof filter[criterion.auxiliaryToggleKey] === "boolean") {
      sanitized[criterion.auxiliaryToggleKey] = filter[criterion.auxiliaryToggleKey];
    }
  }

  return sanitized;
}

// Video criterion definitions
export const VIDEO_CRITERIA: CriteriaDefinitionList<VideoFilterCriteria> = [
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "code", label: "Studio Code", type: "string", filterKey: "codeCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "director", label: "Director", type: "string", filterKey: "directorCriterion" },
  { id: "path", label: "Path", type: "path", filterKey: "pathCriterion" },
  { id: "hash", label: "Hash", type: "hash", filterKey: "fingerprintCriterion", options: [...VIDEO_HASH_OPTIONS] },
  { id: "duplicatedPhash", label: "Duplicated (pHash)", type: "bool", filterKey: "duplicatedPhashCriterion" },
  { id: "duplicatedTitle", label: "Duplicated Title", type: "bool", filterKey: "duplicatedTitleCriterion" },
  { id: "duplicatedRemoteId", label: "Duplicated Remote ID", type: "bool", filterKey: "duplicatedRemoteIdCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "likeCounter", label: "Likes", type: "number", filterKey: "likeCounterCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "isVr", label: "VR", type: "bool", filterKey: "isVrCriterion" },
  { id: "hasSegments", label: "Has Segments", type: "bool", filterKey: "hasSegmentsCriterion" },
  { id: "duration", label: "Duration", type: "duration", filterKey: "durationCriterion" },
  { id: "tagDuration", label: "Tag Duration", type: "tagDuration", entityType: "tags", filterKey: "tagDurationCriterion" },
  { id: "resolution", label: "Resolution", type: "resolution", filterKey: "resolutionCriterion" },
    { id: "playCount", label: "Play Count", type: "number", filterKey: "playCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "groups", label: "Groups", type: "multiId", entityType: "groups", filterKey: "groupsCriterion" },
  { id: "galleries", label: "Galleries", type: "multiId", entityType: "galleries", filterKey: "galleriesCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "remoteId", label: "Remote ID", type: "remoteId", filterKey: "remoteIdValueCriterion", secondaryFilterKey: "remoteIdCriterion" },
  { id: "remoteIdCount", label: "Remote ID Count", type: "number", filterKey: "remoteIdCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "videoCodec", label: "Video Codec", type: "string", filterKey: "videoCodecCriterion" },
  { id: "audioCodec", label: "Audio Codec", type: "string", filterKey: "audioCodecCriterion" },
  { id: "frameRate", label: "Frame Rate", type: "number", filterKey: "frameRateCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "bitrate", label: "Bitrate (kbps)", type: "number", filterKey: "bitrateInterval" },
  { id: "fileCount", label: "File Count", type: "number", filterKey: "fileCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerFavorite", label: "Performer Favorite", type: "bool", filterKey: "performerFavoriteCriterion" },
  { id: "resumeTime", label: "Resume Time", type: "number", filterKey: "resumeTimeCriterion" },
  { id: "playDuration", label: "Play Duration", type: "duration", filterKey: "playDurationCriterion" },
  { id: "lastPlayedAt", label: "Last Played", type: "timestamp", filterKey: "lastPlayedAtCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
  { id: "performerTags", label: "Performer Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "performerAge", label: "Performer Age", type: "number", filterKey: "performerAgeCriterion" },
  { id: "captions", label: "Captions", type: "string", filterKey: "captionsCriterion" },
  { id: "orientation", label: "Orientation", type: "enum", filterKey: "orientationCriterion", modifiers: VALUE_ONLY_ENUM_MODIFIERS, options: [
    { value: "landscape", label: "Landscape" },
    { value: "portrait", label: "Portrait" },
    { value: "square", label: "Square" },
  ] },
];

export const PERFORMER_CRITERIA: CriteriaDefinitionList<PerformerFilterCriteria> = [
  { id: "name", label: "Name", type: "string", filterKey: "nameCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
  { id: "age", label: "Age", type: "number", filterKey: "ageCriterion" },
  { id: "gender", label: "Gender", type: "enum", filterKey: "genderCriterion", multiSelectOptions: true, options: [
    { value: "Male", label: "Male" },
    { value: "Female", label: "Female" },
    { value: "TransgenderMale", label: "Transgender Male" },
    { value: "TransgenderFemale", label: "Transgender Female" },
    { value: "Intersex", label: "Intersex" },
    { value: "NonBinary", label: "Non-Binary" },
  ] },
  { id: "ethnicity", label: "Ethnicity", type: "string", filterKey: "ethnicityCriterion" },
  { id: "country", label: "Country", type: "string", filterKey: "countryCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "videoCount", label: "Video Count", type: "number", filterKey: "videoCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "studioCount", label: "Studio Count", type: "number", filterKey: "studioCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "galleryCount", label: "Gallery Count", type: "number", filterKey: "galleryCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "birthdate", label: "Birthdate", type: "date", filterKey: "birthdateCriterion" },
  { id: "height", label: "Height (cm)", type: "number", filterKey: "heightCriterion" },
  { id: "weight", label: "Weight", type: "number", filterKey: "weightCriterion" },
  { id: "remoteId", label: "Remote ID", type: "remoteId", filterKey: "remoteIdValueCriterion", secondaryFilterKey: "remoteIdCriterion" },
  { id: "remoteIdCount", label: "Remote ID Count", type: "number", filterKey: "remoteIdCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion", modifiers: NON_NULL_TIMESTAMP_MODIFIERS },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion", modifiers: NON_NULL_TIMESTAMP_MODIFIERS },
  { id: "disambiguation", label: "Disambiguation", type: "string", filterKey: "disambiguationCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "eyeColor", label: "Eye Color", type: "string", filterKey: "eyeColorCriterion" },
  { id: "hairColor", label: "Hair Color", type: "string", filterKey: "hairColorCriterion" },
  { id: "measurements", label: "Measurements", type: "string", filterKey: "measurementsCriterion" },
  { id: "fakeTits", label: "Fake Tits", type: "string", filterKey: "fakeTitsCriterion" },
  { id: "penisLength", label: "Penis Length", type: "number", filterKey: "penisLengthCriterion" },
  { id: "circumcised", label: "Circumcised", type: "enum", filterKey: "circumcisedCriterion", options: [
    { value: "Cut", label: "Cut" },
    { value: "Uncut", label: "Uncut" },
  ] },
  { id: "careerStart", label: "Career Start", type: "date", filterKey: "careerStartCriterion" },
  { id: "careerEnd", label: "Career End", type: "date", filterKey: "careerEndCriterion" },
  { id: "careerLength", label: "Career Length", type: "careerLength", filterKey: "careerLengthCriterion" },
  { id: "tattoos", label: "Tattoos", type: "string", filterKey: "tattooCriterion" },
  { id: "piercings", label: "Piercings", type: "string", filterKey: "piercingsCriterion" },
  { id: "aliases", label: "Aliases", type: "string", filterKey: "aliasesCriterion" },
  { id: "deathDate", label: "Death Date", type: "date", filterKey: "deathDateCriterion" },
  { id: "playCount", label: "Play Count", type: "number", filterKey: "playCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "likeCounter", label: "Likes", type: "number", filterKey: "likeCounterCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "groups", label: "Groups", type: "multiId", entityType: "groups", filterKey: "groupsCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
];

export const TAG_CRITERIA: CriteriaDefinitionList<TagFilterCriteria> = [
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
  { id: "videoCount", label: "Video Count", type: "number", filterKey: "videoCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS, auxiliaryToggleKey: "videoCountIncludesChildren", auxiliaryToggleLabel: "Count videos from child tags" },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS, auxiliaryToggleKey: "performerCountIncludesChildren", auxiliaryToggleLabel: "Count performers from child tags" },
  { id: "parents", label: "Parent Tags", type: "multiId", entityType: "tags", filterKey: "parentsCriterion" },
  { id: "children", label: "Sub-Tags", type: "multiId", entityType: "tags", filterKey: "childrenCriterion" },
  { id: "tagGroup", label: "Tag Group", type: "multiId", entityType: "tagGroups", filterKey: "tagGroupsCriterion", modifiers: ["INCLUDES"] },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
  { id: "name", label: "Name", type: "string", filterKey: "nameCriterion" },
  { id: "sortName", label: "Sort Name", type: "string", filterKey: "sortNameCriterion" },
  { id: "remoteId", label: "Remote ID", type: "remoteId", filterKey: "remoteIdValueCriterion", secondaryFilterKey: "remoteIdCriterion" },
  { id: "remoteIdCount", label: "Remote ID Count", type: "number", filterKey: "remoteIdCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "aliases", label: "Aliases", type: "string", filterKey: "aliasesCriterion" },
  { id: "description", label: "Description", type: "string", filterKey: "descriptionCriterion" },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS, auxiliaryToggleKey: "imageCountIncludesChildren", auxiliaryToggleLabel: "Count images from child tags" },
  { id: "galleryCount", label: "Gallery Count", type: "number", filterKey: "galleryCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS, auxiliaryToggleKey: "galleryCountIncludesChildren", auxiliaryToggleLabel: "Count galleries from child tags" },
  { id: "studioCount", label: "Studio Count", type: "number", filterKey: "studioCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS, auxiliaryToggleKey: "studioCountIncludesChildren", auxiliaryToggleLabel: "Count studios from child tags" },
  { id: "groupCount", label: "Group Count", type: "number", filterKey: "groupCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS, auxiliaryToggleKey: "groupCountIncludesChildren", auxiliaryToggleLabel: "Count groups from child tags" },
  { id: "parentCount", label: "Parent Count", type: "number", filterKey: "parentCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "childCount", label: "Sub-Tag Count", type: "number", filterKey: "childCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
];

export const STUDIO_CRITERIA: CriteriaDefinitionList<StudioFilterCriteria> = [
  { id: "name", label: "Name", type: "string", filterKey: "nameCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "videoCount", label: "Video Count", type: "number", filterKey: "videoCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "remoteId", label: "Remote ID", type: "remoteId", filterKey: "remoteIdValueCriterion", secondaryFilterKey: "remoteIdCriterion" },
  { id: "remoteIdCount", label: "Remote ID Count", type: "number", filterKey: "remoteIdCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "aliases", label: "Aliases", type: "string", filterKey: "aliasesCriterion" },
  { id: "parents", label: "Parent Studios", type: "multiId", entityType: "studios", filterKey: "parentsCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "parentCount", label: "Parent Studio Count", type: "number", filterKey: "parentCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "childCount", label: "Substudios Count", type: "number", filterKey: "childCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "groupCount", label: "Group Count", type: "number", filterKey: "groupCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "galleryCount", label: "Gallery Count", type: "number", filterKey: "galleryCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
];

export const GALLERY_CRITERIA: CriteriaDefinitionList<GalleryFilterCriteria> = [
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "code", label: "Studio Code", type: "string", filterKey: "codeCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "photographer", label: "Photographer", type: "string", filterKey: "photographerCriterion" },
  { id: "path", label: "Path", type: "path", filterKey: "pathCriterion" },
  { id: "hash", label: "Hash", type: "hash", filterKey: "fingerprintCriterion", options: [...VISUAL_HASH_OPTIONS] },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "videos", label: "Videos", type: "multiId", entityType: "videos", filterKey: "videosCriterion" },
  { id: "performerTags", label: "Performer Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "performerFavorite", label: "Performer Favorite", type: "bool", filterKey: "performerFavoriteCriterion" },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "likes", label: "Likes", type: "number", filterKey: "likeCounterCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "lastLikedAt", label: "Last Liked Date", type: "timestamp", filterKey: "lastLikedAtCriterion" },
  { id: "fileCount", label: "File Count", type: "number", filterKey: "fileCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerAge", label: "Performer Age", type: "number", filterKey: "performerAgeCriterion" },
  { id: "typicalResolution", label: "Typical Resolution", type: "resolution", filterKey: "typicalResolutionCriterion" },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

export const IMAGE_CRITERIA: CriteriaDefinitionList<ImageFilterCriteria> = [
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "code", label: "Studio Code", type: "string", filterKey: "codeCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "photographer", label: "Photographer", type: "string", filterKey: "photographerCriterion" },
  { id: "path", label: "Path", type: "path", filterKey: "pathCriterion" },
  { id: "hash", label: "Hash", type: "hash", filterKey: "fingerprintCriterion" as Extract<keyof ImageFilterCriteria, string>, options: [...VISUAL_HASH_OPTIONS] },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "likeCounter", label: "Likes", type: "number", filterKey: "likeCounterCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "resolution", label: "Resolution", type: "resolution", filterKey: "resolutionCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "galleries", label: "Galleries", type: "multiId", entityType: "galleries", filterKey: "galleriesCriterion" },
  { id: "performerTags", label: "Performer Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "performerFavorite", label: "Performer Favorite", type: "bool", filterKey: "performerFavoriteCriterion" },
  { id: "fileCount", label: "File Count", type: "number", filterKey: "fileCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerAge", label: "Performer Age", type: "number", filterKey: "performerAgeCriterion" as Extract<keyof ImageFilterCriteria, string> },
  { id: "orientation", label: "Orientation", type: "enum", filterKey: "orientationCriterion" as Extract<keyof ImageFilterCriteria, string>, modifiers: VALUE_ONLY_ENUM_MODIFIERS, options: [
    { value: "landscape", label: "Landscape" },
    { value: "portrait", label: "Portrait" },
    { value: "square", label: "Square" },
  ] },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

export const AUDIO_CRITERIA: CriteriaDefinitionList<AudioFilterCriteria> = [
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "code", label: "Code", type: "string", filterKey: "codeCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "path", label: "Path", type: "path", filterKey: "pathCriterion" },
  { id: "format", label: "File Format", type: "string", filterKey: "formatCriterion" },
  { id: "audioCodec", label: "Audio Codec", type: "string", filterKey: "audioCodecCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "hasVideoFiles", label: "Has Video Track", type: "bool", filterKey: "hasVideoFilesCriterion" },
  { id: "hasCover", label: "Has Cover", type: "bool", filterKey: "hasCoverCriterion" },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "duration", label: "Duration", type: "number", filterKey: "durationCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "bitRate", label: "Bitrate", type: "number", filterKey: "bitRateCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "fileSize", label: "File Size", type: "number", filterKey: "fileSizeCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "fileModTime", label: "File Modified", type: "timestamp", filterKey: "fileModTimeCriterion" },
  { id: "fileCount", label: "File Count", type: "number", filterKey: "fileCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "trackCount", label: "Track Count", type: "number", filterKey: "trackCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "trackTitle", label: "Track Title", type: "string", filterKey: "trackTitleCriterion" },
  { id: "sampleRate", label: "Sample Rate", type: "number", filterKey: "sampleRateCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "channels", label: "Channels", type: "number", filterKey: "channelsCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "playCount", label: "Play Count", type: "number", filterKey: "playCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "likeCounter", label: "Likes", type: "number", filterKey: "likeCounterCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "playDuration", label: "Play Duration", type: "number", filterKey: "playDurationCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "lastPlayedAt", label: "Last Played", type: "timestamp", filterKey: "lastPlayedAtCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerTags", label: "Performer Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "groups", label: "Groups", type: "multiId", entityType: "groups", filterKey: "groupsCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

export const TEXT_CRITERIA: CriteriaDefinitionList<TextFilterCriteria> = [
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "code", label: "Code", type: "string", filterKey: "codeCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "content", label: "Content", type: "string", filterKey: "contentCriterion" },
  { id: "path", label: "Path", type: "path", filterKey: "pathCriterion" },
  { id: "format", label: "File Format", type: "string", filterKey: "formatCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "hasCover", label: "Has Cover", type: "bool", filterKey: "hasCoverCriterion" },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "wordCount", label: "Word Count", type: "number", filterKey: "wordCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "pageCount", label: "Page Count", type: "number", filterKey: "pageCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "fileSize", label: "File Size", type: "number", filterKey: "fileSizeCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "fileModTime", label: "File Modified", type: "timestamp", filterKey: "fileModTimeCriterion" },
  { id: "fileCount", label: "File Count", type: "number", filterKey: "fileCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "readCount", label: "Read Count", type: "number", filterKey: "playCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "likeCounter", label: "Likes", type: "number", filterKey: "likeCounterCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "readDuration", label: "Read Duration", type: "number", filterKey: "playDurationCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "lastReadAt", label: "Last Read", type: "timestamp", filterKey: "lastReadAtCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerTags", label: "Performer Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "groups", label: "Groups", type: "multiId", entityType: "groups", filterKey: "groupsCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

export const GROUP_CRITERIA: CriteriaDefinitionList<GroupFilterCriteria> = [
  { id: "name", label: "Name", type: "string", filterKey: "nameCriterion" },
  { id: "aliases", label: "Aliases", type: "string", filterKey: "aliasesCriterion" },
  { id: "kind", label: "Kind", type: "enum", filterKey: "kindCriterion", modifiers: VALUE_ONLY_ENUM_MODIFIERS, options: [{ value: "static", label: "Static" }, { value: "dynamic", label: "Dynamic" }] },
  { id: "querySourceKey", label: "Query Source", type: "string", filterKey: "querySourceKeyCriterion" },
  { id: "hasQuery", label: "Has Dynamic Query", type: "bool", filterKey: "hasQueryCriterion" },
  { id: "isBuiltIn", label: "Built-In Group", type: "bool", filterKey: "isBuiltInCriterion" },
  { id: "lastResolvedAt", label: "Last Resolved", type: "timestamp", filterKey: "lastResolvedAtCriterion" },
  { id: "cachedItemCount", label: "Cached Item Count", type: "number", filterKey: "cachedItemCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "showInVideoLists", label: "Show In Video Lists", type: "bool", filterKey: "showInVideoListsCriterion" },
  { id: "allowedHostTypes", label: "Allowed Host Type", type: "string", filterKey: "allowedHostTypesCriterion" },
  { id: "sortOrder", label: "Manual Sort Order", type: "number", filterKey: "sortOrderCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "director", label: "Director", type: "string", filterKey: "directorCriterion" },
  { id: "description", label: "Description", type: "string", filterKey: "synopsisCriterion" },
  { id: "duration", label: "Duration", type: "duration", filterKey: "durationCriterion" },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "itemCount", label: "Item Count", type: "number", filterKey: "itemCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "videoCount", label: "Video Count", type: "number", filterKey: "videoCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "imageCount", label: "Image Count", type: "number", filterKey: "imageCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "audioCount", label: "Audio Count", type: "number", filterKey: "audioCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "textCount", label: "Text Count", type: "number", filterKey: "textCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "galleryCount", label: "Gallery Count", type: "number", filterKey: "galleryCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "performerItemCount", label: "Performer Item Count", type: "number", filterKey: "performerItemCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "studioItemCount", label: "Studio Item Count", type: "number", filterKey: "studioItemCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "tagItemCount", label: "Tag Item Count", type: "number", filterKey: "tagItemCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "faceCount", label: "Face Count", type: "number", filterKey: "faceCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "segmentCount", label: "Segment Count", type: "number", filterKey: "segmentCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "subGroupCount", label: "Subgroup Count", type: "number", filterKey: "subGroupCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "containingGroupCount", label: "Containing Group Count", type: "number", filterKey: "containingGroupCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

// ===== Filter Dialog =====

interface FilterDialogProps {
  open: boolean;
  onClose: () => void;
  criteria: CriterionDefinition[];
  activeFilter: Record<string, unknown>;
  onApply: (filter: Record<string, unknown>) => void;
  preselectCriterion?: string;
  customSections?: FilterDialogCustomSection[];
  showCustomSectionDivider?: boolean;
}

export interface FilterDialogCustomSection {
  id: string;
  label: string;
  filterKey: string;
  defaultValue: unknown;
  isActive: (value: unknown) => boolean;
  shouldKeepDraft?: (value: unknown) => boolean;
  sanitize?: (value: unknown) => unknown;
  renderEditor: (value: unknown, onChange: (value: unknown) => void) => ReactNode;
  summarize?: (value: unknown) => string;
}

export function FilterDialog({ open, onClose, criteria, activeFilter, onApply, preselectCriterion, customSections, showCustomSectionDivider = true }: FilterDialogProps) {
  const [editFilter, setEditFilter] = useState<Record<string, unknown>>({ ...activeFilter });
  const backdropPointerDownRef = useRef(false);
  const [search, setSearch] = useState("");
  const [expandedCriterion, setExpandedCriterion] = useState<string | null>(null);
  const [navigatorFocusId, setNavigatorFocusId] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const criterionButtonRefs = useRef(new Map<string, HTMLButtonElement>());
  const pinButtonRefs = useRef(new Map<string, HTMLButtonElement>());
  const pendingPinFocusRef = useRef<string | null>(null);
  const wasOpenRef = useRef(false);
  const activeFilterSignature = useMemo(() => JSON.stringify(activeFilter ?? {}), [activeFilter]);
  const lastActiveFilterSignatureRef = useRef(activeFilterSignature);
  const [pinnedIds, setPinnedIds] = useState<Set<string>>(() => {
    try {
      const stored = localStorage.getItem("filter-pinned");
      return stored ? new Set(JSON.parse(stored)) : new Set<string>();
    } catch {
      return new Set<string>();
    }
  });

  const togglePin = useCallback(
    (id: string) => {
      pendingPinFocusRef.current = id;
      setPinnedIds((prev) => {
        const next = new Set(prev);
        if (next.has(id)) next.delete(id);
        else next.add(id);
        localStorage.setItem("filter-pinned", JSON.stringify([...next]));
        return next;
      });
    },
    []
  );

  useEffect(() => {
    const id = pendingPinFocusRef.current;
    if (!id) return;
    pendingPinFocusRef.current = null;
    criterionButtonRefs.current.get(id)?.focus();
  }, [pinnedIds]);

  const filteredCriteria = useMemo(() => {
    const q = search.trim().toLowerCase();
    return (q ? criteria.filter((c) => c.label.toLowerCase().includes(q)) : criteria)
      .slice()
      .sort((a, b) => {
        const aExact = Boolean(q) && a.label.toLowerCase() === q;
        const bExact = Boolean(q) && b.label.toLowerCase() === q;
        if (aExact !== bExact) return aExact ? -1 : 1;
        return a.label.localeCompare(b.label);
      });
  }, [criteria, search]);

  const activeCriterionCount = useMemo(() => {
    const criteriaCount = criteria.filter((criterion) => isCriterionValueValid(getCriterionFilterValue(editFilter, criterion), criterion)).length;
    const customCount = (customSections ?? []).filter((section) => section.isActive(editFilter[section.filterKey])).length;
    return criteriaCount + customCount;
  }, [criteria, customSections, editFilter]);

  const activeEditFilter = useMemo(() => {
    const sectionFilter: Record<string, unknown> = {};
    for (const section of customSections ?? []) {
      const value = section.sanitize ? section.sanitize(editFilter[section.filterKey]) : editFilter[section.filterKey];
      if (section.isActive(value)) sectionFilter[section.filterKey] = value;
    }
    return sanitizeFilterCriteria(editFilter, criteria, sectionFilter);
  }, [criteria, customSections, editFilter]);

  type NavigatorItem =
    | { kind: "criterion"; id: string; label: string; active: boolean; pinned: boolean; criterion: CriterionDefinition }
    | { kind: "custom"; id: string; label: string; active: boolean; pinned: false; section: FilterDialogCustomSection };

  const navigatorGroups = useMemo(() => {
    const q = search.trim().toLowerCase();
    const sortItems = (a: NavigatorItem, b: NavigatorItem) => {
      const aExact = Boolean(q) && a.label.toLowerCase() === q;
      const bExact = Boolean(q) && b.label.toLowerCase() === q;
      if (aExact !== bExact) return aExact ? -1 : 1;
      return a.label.localeCompare(b.label);
    };
    const criterionItems: NavigatorItem[] = filteredCriteria.map((criterion) => ({
      kind: "criterion",
      id: criterion.id,
      label: criterion.label,
      active: isCriterionValueValid(getCriterionFilterValue(editFilter, criterion), criterion),
      pinned: pinnedIds.has(criterion.id),
      criterion,
    }));
    const customItems: NavigatorItem[] = (customSections ?? [])
      .filter((section) => !q || section.label.toLowerCase().includes(q))
      .map((section) => ({
        kind: "custom",
        id: section.id,
        label: section.label,
        active: section.isActive(editFilter[section.filterKey]),
        pinned: false,
        section,
      }));
    const items = [...customItems, ...criterionItems];
    const active = items.filter((item) => item.active).sort(sortItems);
    const pinned = items.filter((item) => !item.active && item.pinned).sort(sortItems);
    const remaining = items.filter((item) => !item.active && !item.pinned).sort(sortItems);
    return [
      { label: "Active", items: active },
      { label: "Pinned", items: pinned },
      { label: "All filters", items: remaining },
    ].filter((group) => group.items.length > 0);
  }, [customSections, editFilter, filteredCriteria, pinnedIds, search]);

  const visibleNavigatorItems = useMemo(() => navigatorGroups.flatMap((group) => group.items), [navigatorGroups]);
  const rovingNavigatorId = navigatorFocusId && visibleNavigatorItems.some((item) => item.id === navigatorFocusId)
    ? navigatorFocusId
    : expandedCriterion && visibleNavigatorItems.some((item) => item.id === expandedCriterion)
    ? expandedCriterion
    : visibleNavigatorItems[0]?.id;
  const selectedItem = useMemo(() => {
    if (!expandedCriterion) return undefined;
    const section = (customSections ?? []).find((item) => item.id === expandedCriterion);
    if (section) {
      return {
        kind: "custom" as const,
        id: section.id,
        label: section.label,
        active: section.isActive(editFilter[section.filterKey]),
        pinned: false as const,
        section,
      };
    }
    const criterion = criteria.find((item) => item.id === expandedCriterion);
    return criterion ? {
      kind: "criterion" as const,
      id: criterion.id,
      label: criterion.label,
      active: isCriterionValueValid(getCriterionFilterValue(editFilter, criterion), criterion),
      pinned: pinnedIds.has(criterion.id),
      criterion,
    } : undefined;
  }, [criteria, customSections, editFilter, expandedCriterion, pinnedIds]);

  const cloneActiveFilter = useCallback(() => JSON.parse(activeFilterSignature) as Record<string, unknown>, [activeFilterSignature]);

  const focusFirstEditorControl = useCallback(() => {
    window.setTimeout(() => {
      const panel = dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
      (panel?.querySelector<HTMLElement>("[data-filter-primary-control]")
        ?? panel?.querySelector<HTMLElement>("input:not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled])"))?.focus();
    }, 0);
  }, []);

  const selectNavigatorItem = useCallback((id: string) => {
    setExpandedCriterion(id);
    focusFirstEditorControl();
  }, [focusFirstEditorControl]);

  useEffect(() => {
    if (lastActiveFilterSignatureRef.current !== activeFilterSignature) {
      lastActiveFilterSignatureRef.current = activeFilterSignature;
      setEditFilter(cloneActiveFilter());
    }
  }, [activeFilterSignature, cloneActiveFilter]);

  useEffect(() => {
    if (!open) return;
    return pushOverlay();
  }, [open]);

  useEffect(() => {
    if (!open) {
      if (wasOpenRef.current) {
        setEditFilter(cloneActiveFilter());
        setSearch("");
        setExpandedCriterion(null);
        setNavigatorFocusId(null);
        previousFocusRef.current?.focus();
      }
      wasOpenRef.current = false;
      return;
    }

    if (!wasOpenRef.current) {
      previousFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      setEditFilter(cloneActiveFilter());
      setSearch("");
      setNavigatorFocusId(null);
      const firstActive = criteria.find((criterion) => isCriterionValueValid(getCriterionFilterValue(activeFilter, criterion), criterion))?.id
        ?? (customSections ?? []).find((section) => section.isActive(activeFilter[section.filterKey]))?.id;
      const nextSelected = preselectCriterion ?? firstActive ?? null;
      setExpandedCriterion(nextSelected);
      window.setTimeout(() => {
        if (nextSelected && preselectCriterion) focusFirstEditorControl();
        else searchRef.current?.focus();
        if (nextSelected) criterionButtonRefs.current.get(nextSelected)?.scrollIntoView?.({ block: "center", inline: "nearest" });
      }, 0);
    }
    wasOpenRef.current = true;
  }, [activeFilter, cloneActiveFilter, criteria, customSections, focusFirstEditorControl, open, preselectCriterion]);

  const dismiss = useCallback(() => {
    setEditFilter(cloneActiveFilter());
    setSearch("");
    setExpandedCriterion(null);
    setNavigatorFocusId(null);
    onClose();
  }, [cloneActiveFilter, onClose]);

  useEffect(() => {
    if (!open) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        dismiss();
        return;
      }
      if (event.key !== "Tab" || !dialogRef.current) return;
      const focusable = Array.from(dialogRef.current.querySelectorAll<HTMLElement>(
        "button:not([disabled]):not([tabindex='-1']), input:not([disabled]):not([tabindex='-1']), select:not([disabled]):not([tabindex='-1']), textarea:not([disabled]):not([tabindex='-1']), [tabindex]:not([tabindex='-1'])",
      )).filter((element) => !element.closest("[hidden]") && element.offsetParent !== null);
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [dismiss, open]);

  const handleRemoveCriterion = useCallback((criterion: CriterionDefinition, criterionId?: string) => {
    setEditFilter((prev) => removeCriterionFilterValue(prev, criterion));

    if (criterionId && expandedCriterion === criterionId) {
      setExpandedCriterion(null);
    }
  }, [expandedCriterion]);

  const handleSetCriterion = useCallback((criterion: CriterionDefinition, value: unknown) => {
    setEditFilter((prev) => setCriterionFilterValue(prev, criterion, value));
  }, []);

  const handleSetAuxiliaryToggle = useCallback((criterion: CriterionDefinition, checked: boolean) => {
    const auxiliaryToggleKey = criterion.auxiliaryToggleKey;
    if (!auxiliaryToggleKey) {
      return;
    }

    setEditFilter((prev) => {
      const next = { ...prev };
      if (checked) {
        next[auxiliaryToggleKey] = true;
      } else {
        delete next[auxiliaryToggleKey];
      }
      return next;
    });
  }, []);

  const handleEditChip = useCallback((key: string) => {
    const customSection = (customSections ?? []).find((section) => section.filterKey === key);
    const criterion = criteria.find((item) => item.id === key
      || item.filterKey === key
      || item.secondaryFilterKey === key
      || item.auxiliaryToggleKey === key);
    const nextId = customSection?.id ?? criterion?.id;
    if (nextId) {
      setSearch("");
      selectNavigatorItem(nextId);
    }
  }, [criteria, customSections, selectNavigatorItem]);

  const handleRemoveChip = useCallback((key: string) => {
    const customSection = (customSections ?? []).find((section) => section.filterKey === key);
    if (customSection) {
      setEditFilter((current) => {
        const next = { ...current };
        delete next[customSection.filterKey];
        return next;
      });
      return;
    }

    const criterion = criteria.find((item) => item.id === key
      || item.filterKey === key
      || item.secondaryFilterKey === key
      || item.auxiliaryToggleKey === key);
    if (criterion) {
      if (criterion.auxiliaryToggleKey === key) {
        setEditFilter((current) => {
          const next = { ...current };
          delete next[key];
          return next;
        });
      } else {
        handleRemoveCriterion(criterion);
      }
      return;
    }

    setEditFilter((current) => {
      const next = { ...current };
      delete next[key];
      return next;
    });
  }, [criteria, customSections, handleRemoveCriterion]);

  const handleApply = () => {
    onApply(activeEditFilter);
    onClose();
  };

  const handleClear = () => {
    setEditFilter({});
    setExpandedCriterion(null);
    window.setTimeout(() => searchRef.current?.focus(), 0);
  };

  if (!open) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 md:p-4"
      onMouseDown={(event) => {
        backdropPointerDownRef.current = event.target === event.currentTarget;
      }}
      onClick={(event) => {
        if (event.target === event.currentTarget && backdropPointerDownRef.current) {
          dismiss();
        }

        backdropPointerDownRef.current = false;
      }}
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="filter-dialog-title"
        className="filter-dialog flex h-[100dvh] w-full flex-col overflow-hidden border-border bg-surface shadow-2xl md:h-[min(88dvh,52rem)] md:w-[min(94vw,72rem)] md:rounded-2xl md:border"
        onKeyDown={(event) => {
          if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
            event.preventDefault();
            handleApply();
          }
        }}
        onClick={(e) => e.stopPropagation()}
        onMouseDown={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex min-h-16 items-center justify-between border-b border-border px-4 pt-[env(safe-area-inset-top)] md:px-6 md:pt-0">
          <div className="flex min-w-0 items-center gap-2">
            {selectedItem ? (
              <button type="button" onClick={() => { setExpandedCriterion(null); window.setTimeout(() => searchRef.current?.focus(), 0); }} className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground md:hidden" aria-label="Back to filter criteria">
                <ArrowLeft className="h-5 w-5" />
              </button>
            ) : null}
            <h2 id="filter-dialog-title" className="truncate text-lg font-semibold text-foreground">Filters</h2>
            {selectedItem ? <span className="truncate text-sm text-secondary md:hidden">{selectedItem.label}</span> : null}
            {activeCriterionCount > 0 && (
              <span className="rounded-full bg-accent px-2 py-0.5 text-xs font-bold text-white" aria-label={`${activeCriterionCount} active filters`}>
                {activeCriterionCount}
              </span>
            )}
          </div>
          <button type="button" onClick={dismiss} className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-muted hover:bg-card hover:text-foreground" aria-label="Close filters">
            <X className="h-5 w-5" />
          </button>
        </div>

        <ActiveObjectFilterChips
          criteriaDefinitions={criteria}
          objectFilter={activeEditFilter}
          customFilterSections={customSections}
          onEdit={handleEditChip}
          onRemove={handleRemoveChip}
          onClearAll={handleClear}
          rovingKeyboardAccess
          onFocusFallback={() => {
            const criterionButton = expandedCriterion ? criterionButtonRefs.current.get(expandedCriterion) : undefined;
            if (criterionButton) criterionButton.focus();
            else searchRef.current?.focus();
          }}
          onFocusKey={(key) => {
            const buttons = dialogRef.current?.querySelectorAll<HTMLButtonElement>("button[data-active-filter-key]");
            Array.from(buttons ?? []).find((button) => button.dataset.activeFilterKey === key)?.focus();
          }}
          ariaLabel="Selected filters"
          className="!mx-0 !mt-0 max-h-[min(12rem,35dvh)] shrink-0 overflow-y-auto !rounded-none !border-x-0 !border-t-0 px-3 py-2 md:px-4"
        />

        <div className="grid min-h-0 flex-1 overflow-hidden md:grid-cols-[20rem_minmax(0,1fr)]">
          <aside className={`${selectedItem ? "hidden md:flex" : "flex"} min-h-0 flex-col border-border md:border-r`} aria-label="Filter criteria">
            <div className="border-b border-border p-3 md:p-4">
              <label className="relative block">
                <span className="sr-only">Search filter criteria</span>
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
                <input
                  ref={searchRef}
                  type="search"
                  aria-label="Search filter criteria"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  onKeyDown={(event) => {
                    if (event.key !== "ArrowDown" || visibleNavigatorItems.length === 0) return;
                    event.preventDefault();
                    criterionButtonRefs.current.get(visibleNavigatorItems[0].id)?.focus();
                  }}
                  placeholder="Search filters"
                  className="min-h-11 w-full rounded-lg border border-border bg-input py-2 pl-10 pr-3 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
                />
              </label>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto p-2 md:p-3" role="tablist" aria-label="Available filter criteria" aria-orientation="vertical">
              {navigatorGroups.map((group) => (
                <section key={group.label} className="mb-4" aria-label={group.label}>
                  <h3 className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-muted">{group.label}</h3>
                  <div className="space-y-1">
                    {group.items.map((item) => {
                      const selected = item.id === expandedCriterion;
                      const supported = item.kind === "custom" || item.criterion.supported !== false;
                      const rowStateClass = selected
                        ? "border-accent bg-accent/15 text-foreground"
                        : item.active
                        ? "border-accent/30 bg-accent/5 text-foreground hover:bg-card"
                        : "border-transparent text-secondary hover:border-border hover:bg-card hover:text-foreground";
                      return (
                        <div key={item.id} role="presentation" className={`flex min-h-11 w-full items-stretch overflow-hidden rounded-lg border text-sm transition ${rowStateClass} ${supported ? "" : "cursor-not-allowed opacity-60"}`}>
                          <button
                            ref={(element) => { if (element) criterionButtonRefs.current.set(item.id, element); else criterionButtonRefs.current.delete(item.id); }}
                            type="button"
                            role="tab"
                            id={`filter-tab-${item.id}`}
                            aria-selected={selected}
                            aria-controls="filter-editor-panel"
                            aria-disabled={!supported || undefined}
                            tabIndex={item.id === rovingNavigatorId ? 0 : -1}
                            onClick={() => { if (supported) selectNavigatorItem(item.id); }}
                            onFocus={() => setNavigatorFocusId(item.id)}
                            onKeyDown={(event) => {
                              const index = visibleNavigatorItems.findIndex((candidate) => candidate.id === item.id);
                              if (event.key === "ArrowRight" && item.kind === "criterion" && supported) {
                                event.preventDefault();
                                pinButtonRefs.current.get(item.id)?.focus();
                                return;
                              }
                              if (event.key === "ArrowUp" && index === 0) {
                                event.preventDefault();
                                searchRef.current?.focus();
                                return;
                              }
                              let nextIndex: number | undefined;
                              if (event.key === "ArrowDown") nextIndex = Math.min(visibleNavigatorItems.length - 1, index + 1);
                              if (event.key === "ArrowUp") nextIndex = Math.max(0, index - 1);
                              if (event.key === "Home") nextIndex = 0;
                              if (event.key === "End") nextIndex = visibleNavigatorItems.length - 1;
                              if (nextIndex !== undefined) {
                                event.preventDefault();
                                criterionButtonRefs.current.get(visibleNavigatorItems[nextIndex].id)?.focus();
                              }
                            }}
                            className="flex min-w-0 flex-1 items-center gap-3 px-3 py-2 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent"
                            title={supported ? undefined : item.kind === "criterion" ? item.criterion.unsupportedReason : undefined}
                          >
                            <span className="min-w-0 flex-1 truncate font-medium">{item.label}</span>
                            {!supported ? <span className="text-[10px] uppercase tracking-wide text-muted">Unavailable</span> : null}
                          </button>
                          {item.kind === "criterion" && supported ? (
                            <button
                              ref={(element) => { if (element) pinButtonRefs.current.set(item.id, element); else pinButtonRefs.current.delete(item.id); }}
                              type="button"
                              onClick={() => togglePin(item.id)}
                              onKeyDown={(event) => {
                                if (event.key === "ArrowLeft" || event.key === "Escape") {
                                  event.preventDefault();
                                  event.stopPropagation();
                                  criterionButtonRefs.current.get(item.id)?.focus();
                                }
                              }}
                              tabIndex={-1}
                              aria-label={`${item.pinned ? "Unpin" : "Pin"} ${item.label}`}
                              aria-pressed={item.pinned}
                              title={`${item.pinned ? "Unpin" : "Pin"} ${item.label}`}
                              className={`group flex w-10 shrink-0 items-center justify-center border-l text-muted transition hover:bg-background/40 hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent ${selected ? "border-accent/40" : "border-border/60"} ${item.pinned ? "opacity-100" : "opacity-100 md:opacity-0 md:hover:opacity-100 md:focus-visible:opacity-100"}`}
                            >
                              {item.pinned ? (
                                <>
                                  <Pin className="h-4 w-4 group-hover:hidden group-focus-visible:hidden" />
                                  <PinOff className="hidden h-4 w-4 group-hover:block group-focus-visible:block" />
                                </>
                              ) : <Pin className="h-4 w-4" />}
                            </button>
                          ) : null}
                        </div>
                      );
                    })}
                  </div>
                </section>
              ))}
              {visibleNavigatorItems.length === 0 ? <div className="px-4 py-10 text-center text-sm text-muted">No filters match “{search}”.</div> : null}
            </div>
          </aside>

          <main className={`${selectedItem ? "flex" : "hidden md:flex"} min-h-0 min-w-0 flex-col`}>
            {selectedItem ? (
              <div
                id="filter-editor-panel"
                role="tabpanel"
                aria-labelledby={`filter-tab-${selectedItem.id}`}
                aria-label={selectedItem.label}
                className="flex min-h-0 flex-1 flex-col overflow-y-auto p-4 md:p-6"
              >
                {selectedItem.kind === "custom" ? selectedItem.section.renderEditor(
                  editFilter[selectedItem.section.filterKey] ?? selectedItem.section.defaultValue,
                  (nextValue) => {
                    setEditFilter((current) => {
                      const next = { ...current };
                      const shouldKeepDraft = selectedItem.section.shouldKeepDraft ?? selectedItem.section.isActive;
                      if (shouldKeepDraft(nextValue)) next[selectedItem.section.filterKey] = nextValue;
                      else delete next[selectedItem.section.filterKey];
                      return next;
                    });
                  },
                ) : selectedItem.criterion.supported === false ? (
                  <div className="rounded-xl border border-border bg-card p-4 text-sm text-secondary">{selectedItem.criterion.unsupportedReason ?? "This filter is not currently available."}</div>
                ) : (
                  <CriterionEditor
                    criterion={selectedItem.criterion}
                    value={getCriterionFilterValue(editFilter, selectedItem.criterion)}
                    auxiliaryToggleChecked={selectedItem.criterion.auxiliaryToggleKey ? Boolean(editFilter[selectedItem.criterion.auxiliaryToggleKey]) : undefined}
                    onAuxiliaryToggleChange={(checked) => handleSetAuxiliaryToggle(selectedItem.criterion, checked)}
                    onChange={(value) => handleSetCriterion(selectedItem.criterion, value)}
                  />
                )}
              </div>
            ) : (
              <div className="flex flex-1 items-center justify-center p-8 text-center">
                <div className="max-w-sm">
                  <Search className="mx-auto mb-3 h-8 w-8 text-muted" />
                  <h3 className="text-lg font-semibold text-foreground">Choose a filter</h3>
                  <p className="mt-1 text-sm text-secondary">Search or select a criterion to configure it. Changes are applied only when you choose Apply.</p>
                </div>
              </div>
            )}
          </main>
        </div>

        {/* Footer */}
        <div className="flex min-h-16 items-center justify-end gap-3 border-t border-border px-4 pb-[env(safe-area-inset-bottom)] md:px-6 md:pb-0">
          <div className="flex items-center gap-2">
            <button type="button" onClick={dismiss} className="min-h-11 rounded-lg border border-border px-4 text-sm text-secondary hover:bg-card hover:text-foreground">
              Cancel
            </button>
            <button type="button" onClick={handleApply} className="min-h-11 rounded-lg bg-accent px-5 text-sm font-semibold text-white hover:bg-accent-hover">
              Apply
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

// ===== Criterion Editor =====

function CriterionEditor({
  criterion,
  value,
  auxiliaryToggleChecked,
  onAuxiliaryToggleChange,
  onChange,
}: {
  criterion: CriterionDefinition;
  value: unknown;
  auxiliaryToggleChecked?: boolean;
  onAuxiliaryToggleChange?: (checked: boolean) => void;
  onChange: (v: unknown) => void;
}) {
  const { type, entityType } = criterion;
  const modifiers = criterion.modifiers ?? TYPE_MODIFIERS[type];

  switch (type) {
    case "bool":
      return <BoolEditor value={value as BoolCriterion | undefined} onChange={onChange} />;
    case "rating":
      return <RatingFilterEditor value={value as IntCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "number":
    case "duration":
    case "careerLength":
    case "resolution":
      return (
        <NumberEditor
          value={value as IntCriterion | undefined}
          onChange={onChange}
          type={type}
          modifiers={modifiers}
          defaultModifier={criterion.defaultModifier}
          min={criterion.min}
          max={criterion.max}
          step={criterion.step}
          hint={criterion.hint}
          auxiliaryToggleLabel={criterion.auxiliaryToggleLabel}
          auxiliaryToggleChecked={auxiliaryToggleChecked}
          onAuxiliaryToggleChange={onAuxiliaryToggleChange}
        />
      );
    case "tagDuration":
      return <TagDurationEditor value={value as TagDurationCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "hash":
      return <HashEditor value={value as FingerprintCriterion | undefined} onChange={onChange} modifiers={modifiers} options={criterion.options ?? []} />;
    case "string":
      return <StringEditor value={value as StringCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "path":
      return <PathEditor value={value as StringCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "remoteId":
      return <RemoteIdFilterEditor value={value as (StringCriterion & { endpoint?: string }) | undefined} onChange={onChange} modifiers={modifiers} />;
    case "enum":
      return criterion.multiSelectOptions
        ? <MultiEnumEditor value={value as StringCriterion | undefined} onChange={onChange} options={criterion.options ?? []} />
        : <EnumEditor value={value as StringCriterion | undefined} onChange={onChange} options={criterion.options ?? []} modifiers={modifiers} />;
    case "date":
      return <DateEditor value={value as DateCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "timestamp":
      return <TimestampEditor value={value as TimestampCriterion | undefined} onChange={onChange} modifiers={modifiers} />;
    case "multiId":
      return <MultiIdEditor value={value as MultiIdCriterion | undefined} onChange={onChange} entityType={entityType!} modifiers={modifiers} hierarchyToggleLabel={criterion.hierarchyToggleLabel} />;
    default:
      return null;
  }
}

// ===== Bool Editor =====

function BoolEditor({ value, onChange }: { value?: BoolCriterion; onChange: (v: unknown) => void }) {
  return (
    <div className="space-y-2" role="group" aria-label="Value">
      <div className="text-sm font-medium text-secondary">Value</div>
      <div className="flex flex-wrap items-center gap-2">
      <button
        type="button"
        aria-pressed={value?.value === true}
        onClick={() => onChange({ value: true })}
        className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${value?.value === true ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
      >
        True
      </button>
      <button
        type="button"
        aria-pressed={value?.value === false}
        onClick={() => onChange({ value: false })}
        className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${value?.value === false ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
      >
        False
      </button>
      </div>
    </div>
  );
}

// ===== Number Editor =====

export function NumberEditor({
  value,
  onChange,
  type,
  modifiers,
  defaultModifier,
  min,
  max,
  step,
  hint,
  auxiliaryToggleLabel,
  auxiliaryToggleChecked,
  onAuxiliaryToggleChange,
}: {
  value?: IntCriterion;
  onChange: (v: unknown) => void;
  type: CriterionType;
  modifiers: CriterionModifier[];
  defaultModifier?: CriterionModifier;
  min?: number;
  max?: number;
  step?: number;
  hint?: string;
  auxiliaryToggleLabel?: string;
  auxiliaryToggleChecked?: boolean;
  onAuxiliaryToggleChange?: (checked: boolean) => void;
}) {
  // A criterion that narrows `modifiers` must be able to start on one it actually offers — otherwise the Match
  // control shows nothing selected and the saved criterion carries a modifier that isn't in the list.
  const modifier = value?.modifier ?? defaultModifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";
  // Both bounds known ⇒ the value lives on a range, so offer a slider alongside the box.
  const bounded = min != null && max != null && max > min;
  const sliderStep = step ?? (bounded ? Math.max((max! - min!) / 100, 0.001) : undefined);
  const fallback = bounded ? (min! + max!) / 2 : 0;

  const update = (patch: Partial<IntCriterion>) => {
    onChange({ modifier, ...(bounded ? { value: value?.value ?? fallback } : {}), ...value, ...patch });
  };

  const numberInput = (current: number | undefined, onPick: (v: number | undefined) => void, label: string) => (
    <div className={bounded ? "flex items-center gap-3" : undefined}>
      {bounded && (
        <input
          aria-label={`${label} slider`}
          type="range"
          min={min}
          max={max}
          step={sliderStep}
          value={current ?? fallback}
          onChange={(e) => onPick(Number(e.target.value))}
          className="h-2 flex-1 accent-accent"
        />
      )}
      <input
        aria-label={label}
        type="number"
        min={min}
        max={max}
        step={sliderStep}
        value={current ?? ""}
        onChange={(e) => onPick(e.target.value === "" ? undefined : Number(e.target.value))}
        className={`min-h-11 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm ${bounded ? "w-24 tabular-nums" : "w-full"}`}
      />
    </div>
  );

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      {!isNull && (
        <div className="grid gap-3 sm:grid-cols-2">
          {type === "duration" ? (
            <LabeledControl label={isBetween ? "Minimum" : "Value"}><DurationInput value={value?.value} onChange={(v) => update({ value: v })} ariaLabel={isBetween ? "Minimum" : "Value"} /></LabeledControl>
          ) : type === "resolution" ? (
            <LabeledControl label="Value"><ResolutionSelect value={value?.value ?? 0} onChange={(v) => update({ value: v })} /></LabeledControl>
          ) : type === "careerLength" ? (
            <LabeledControl label={isBetween ? "Minimum" : "Value"}><CareerLengthInput value={value?.value ?? 0} onChange={(v) => update({ value: v })} /></LabeledControl>
          ) : (
            <LabeledControl label={isBetween ? "Minimum" : "Value"}>
              {numberInput(value?.value, (v) => update({ value: v }), isBetween ? "Minimum" : "Value")}
            </LabeledControl>
          )}
          {isBetween && (
            <div>
              {type === "duration" ? (
                <LabeledControl label="Maximum"><DurationInput value={value?.value2} onChange={(v) => update({ value2: v })} ariaLabel="Maximum" /></LabeledControl>
              ) : type === "careerLength" ? (
                <LabeledControl label="Maximum"><CareerLengthInput value={value?.value2 ?? 0} onChange={(v) => update({ value2: v })} /></LabeledControl>
              ) : (
                <LabeledControl label="Maximum">
                  {numberInput(value?.value2, (v) => update({ value2: v }), "Maximum")}
                </LabeledControl>
              )}
            </div>
          )}
        </div>
      )}
      {hint && <div className="text-xs text-muted">{hint}</div>}
      {auxiliaryToggleLabel && onAuxiliaryToggleChange && (
        <label className="flex min-h-9 items-center gap-2 text-sm text-secondary">
          <input
            type="checkbox"
            checked={Boolean(auxiliaryToggleChecked)}
            onChange={(event) => onAuxiliaryToggleChange(event.target.checked)}
            className="h-5 w-5 rounded border-border bg-input text-accent focus:ring-accent"
          />
          <span>{auxiliaryToggleLabel}</span>
        </label>
      )}
    </div>
  );
}

function createDraftTagDurationClause(): TagDurationClause {
  return { modifier: "GREATER_THAN", unit: "seconds" };
}

function getEditableTagDurationClauses(value?: TagDurationCriterion): TagDurationClause[] {
  if (value?.clauses && value.clauses.length > 0) {
    return value.clauses;
  }

  if (value && (value.tagId || value.value != null || value.value2 != null)) {
    return [{ tagId: value.tagId, value: value.value, value2: value.value2, modifier: value.modifier ?? "GREATER_THAN", unit: value.unit ?? "seconds" }];
  }

  return [createDraftTagDurationClause()];
}

function TagDurationEditor({ value, onChange, modifiers }: { value?: TagDurationCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const clauses = getEditableTagDurationClauses(value);
  const existingNames: Record<string, string> = value?._names ?? {};

  const commit = (nextClauses: TagDurationClause[], nextNames: Record<string, string> = existingNames) => {
    const cleanedClauses = nextClauses.map((clause) => ({
      tagId: clause.tagId,
      value: clause.value,
      value2: clause.value2,
      modifier: clause.modifier ?? "GREATER_THAN",
      unit: clause.unit ?? "seconds",
    }));

    onChange({
      clauses: cleanedClauses,
      _names: Object.keys(nextNames).length > 0 ? nextNames : undefined,
    });
  };

  const updateClause = (index: number, patch: Partial<TagDurationClause>, namesPatch?: Record<string, string>) => {
    const nextNames = namesPatch ? { ...existingNames, ...namesPatch } : existingNames;
    commit(clauses.map((clause, clauseIndex) => clauseIndex === index ? { ...clause, ...patch } : clause), nextNames);
  };

  const removeClause = (index: number) => {
    const nextClauses = clauses.filter((_, clauseIndex) => clauseIndex !== index);
    if (nextClauses.length === 0) {
      onChange(undefined);
      return;
    }

    commit(nextClauses);
  };

  const selectedTagIds = clauses.map((clause) => clause.tagId).filter((tagId): tagId is number => typeof tagId === "number" && tagId > 0);

  return (
    <div className="space-y-2">
      <div className="space-y-2">
        {clauses.map((clause, index) => (
          <TagDurationClauseEditor
            key={`${clause.tagId ?? "draft"}-${index}`}
            clause={clause}
            modifiers={modifiers}
            excludedTagIds={selectedTagIds.filter((tagId) => tagId !== clause.tagId)}
            onChange={(patch, namesPatch) => updateClause(index, patch, namesPatch)}
            onRemove={clauses.length > 1 || value != null ? () => removeClause(index) : undefined}
          />
        ))}
      </div>
      <button
        type="button"
        onClick={() => commit([...clauses, createDraftTagDurationClause()])}
        className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-xs text-secondary hover:border-accent/60 hover:text-foreground"
      >
        <Plus className="h-3 w-3" />
        Add tag duration
      </button>
    </div>
  );
}

function TagDurationClauseEditor({
  clause,
  modifiers,
  excludedTagIds,
  onChange,
  onRemove,
}: {
  clause: TagDurationClause;
  modifiers: CriterionModifier[];
  excludedTagIds: number[];
  onChange: (patch: Partial<TagDurationClause>, namesPatch?: Record<string, string>) => void;
  onRemove?: () => void;
}) {
  const modifier = clause.modifier ?? "GREATER_THAN";
  const unit = clause.unit ?? "seconds";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";

  const update = (patch: Partial<TagDurationClause>, namesPatch?: Record<string, string>) => {
    onChange({ modifier, unit, ...patch }, namesPatch);
  };

  const setUnit = (nextUnit: TagDurationClause["unit"]) => {
    update({ unit: nextUnit, value: undefined, value2: undefined });
  };

  const renderValueInput = (field: "value" | "value2", label: string) => {
    const currentValue = field === "value" ? clause.value : clause.value2;
    return unit === "percent" ? (
      <PercentInput value={currentValue} onChange={(nextValue) => update({ [field]: nextValue })} ariaLabel={label} />
    ) : (
      <DurationInput value={currentValue} onChange={(nextValue) => update({ [field]: nextValue })} ariaLabel={label} />
    );
  };

  return (
    <div className="space-y-2 rounded border border-border/70 bg-input/30 p-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      <div className="grid gap-2 sm:grid-cols-[minmax(0,1fr)_auto]">
        <div className="relative">
          <EntityReferenceSelector
            entityType="tag"
            value={clause.tagId}
            onChange={(tagId, option) => update({ tagId }, tagId && option ? { [String(tagId)]: option.label } : undefined)}
            placeholder="Search tags"
            inputClassName="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground outline-none focus:border-accent md:text-sm"
            excludeIds={excludedTagIds}
          />
        </div>
        <select
          value={unit}
          onChange={(event) => setUnit(event.target.value as TagDurationClause["unit"])}
          className="min-h-11 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground outline-none focus:border-accent md:text-sm"
          aria-label="Tag duration unit"
        >
          <option value="seconds">Seconds</option>
          <option value="percent">Percent</option>
        </select>
      </div>
      <div className="flex items-center gap-2">
        {renderValueInput("value", unit === "percent" ? "Tag duration percent" : "Tag duration time")}
        {isBetween ? (
          <>
            <span className="text-xs text-muted">and</span>
            {renderValueInput("value2", unit === "percent" ? "Tag duration end percent" : "Tag duration end time")}
          </>
        ) : null}
        {onRemove ? (
          <button
            type="button"
            onClick={onRemove}
            aria-label="Remove tag duration clause"
            className="ml-auto rounded p-1 text-muted hover:bg-red-900/20 hover:text-red-300"
          >
            <X className="h-3.5 w-3.5" />
          </button>
        ) : null}
      </div>
    </div>
  );
}

// ===== Rating Editor — uses the user's configured rating system =====

function RatingStarInput({
  displayValue,
  onChangeDisplay,
  step,
}: {
  displayValue: number;
  onChangeDisplay: (v: number) => void;
  step: number;
}) {
  const [hoverValue, setHoverValue] = useState<number | null>(null);
  const activeValue = hoverValue ?? displayValue;

  return (
    <div className="flex items-center gap-0.5" onMouseLeave={() => setHoverValue(null)}>
      {[1, 2, 3, 4, 5].map((star) => (
        <button
          key={star}
          type="button"
          aria-label={`Set rating to ${star}`}
          onMouseMove={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
            const segments = Math.max(1, Math.ceil(ratio / step));
            const frac = Math.min(1, Number((segments * step).toFixed(2)));
            setHoverValue(star - 1 + frac);
          }}
          onMouseLeave={() => setHoverValue(null)}
          onClick={(e) => {
            const next = e.detail === 0
              ? star
              : (() => {
                const rect = e.currentTarget.getBoundingClientRect();
                const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
                const segments = Math.max(1, Math.ceil(ratio / step));
                const frac = Math.min(1, Number((segments * step).toFixed(2)));
                return star - 1 + frac;
              })();
            onChangeDisplay(next === displayValue ? 0 : next);
          }}
          className="relative inline-flex h-9 w-9 items-center justify-center rounded-lg text-accent transition-transform hover:scale-105 focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          <Star className="h-7 w-7 text-muted" />
          <span
            className="absolute left-1 top-1 h-7 overflow-hidden"
            style={{ width: `${Math.max(0, Math.min(1, activeValue - (star - 1))) * 1.75}rem` }}
          >
            <Star className="h-7 w-7 fill-current text-accent" />
          </span>
        </button>
      ))}
      {hoverValue != null && (
        <span className="text-xs text-secondary ml-1">{hoverValue.toFixed(step < 1 ? 1 : 0)}</span>
      )}
    </div>
  );
}

function RatingFilterInput({
  rawValue,
  onChangeRaw,
}: {
  rawValue: number;
  onChangeRaw: (v: number) => void;
}) {
  const options = useRatingOptions();
  const displayValue = convertToRatingFormat(rawValue || undefined, options) ?? 0;
  const max = getRatingMax(options);
  const step = getRatingStep(options);

  const setDisplay = (v: number) => {
    const clamped = Math.min(max, Math.max(0, Number(v.toFixed(2))));
    onChangeRaw(convertFromRatingFormat(clamped, options));
  };

  if (options.type === "stars") {
    return (
      <div className="flex items-center gap-2">
        <RatingStarInput
          displayValue={displayValue}
          onChangeDisplay={setDisplay}
          step={getRatingPrecision(options.starPrecision)}
        />
      </div>
    );
  }

  // Decimal mode
  return (
    <input
      type="number"
      value={displayValue || ""}
      min={0}
      max={max}
      step={step}
      onChange={(e) => {
        const v = Number(e.target.value);
        if (Number.isFinite(v)) setDisplay(v);
      }}
      className="min-h-11 w-28 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
    />
  );
}

function RatingFilterEditor({ value, onChange, modifiers }: { value?: IntCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  const update = (patch: Partial<IntCriterion>) => {
    onChange({ value: value?.value ?? 0, modifier, ...value, ...patch });
  };

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      {!isNull && (
        <div className="space-y-2">
          <RatingFilterInput rawValue={value?.value ?? 0} onChangeRaw={(v) => update({ value: v })} />
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              <RatingFilterInput rawValue={value?.value2 ?? 0} onChangeRaw={(v) => update({ value2: v })} />
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ===== String Editor =====

function StringEditor({ value, onChange, modifiers }: { value?: StringCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <LabeledControl label="Value">
          <input
            aria-label="Value"
            type="text"
            value={value?.value ?? ""}
            onChange={(e) => onChange({ value: e.target.value, modifier })}
            className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            placeholder="Enter a value"
          />
        </LabeledControl>
      )}
    </div>
  );
}

function PathEditor({ value, onChange, modifiers }: { value?: StringCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "UNDER_PATH";
  const isNull = NULL_VALUE_MODIFIERS.has(modifier);
  const rootsQuery = useQuery({
    queryKey: ["library-folders", "roots", false],
    queryFn: () => metadata.libraryFolders(undefined, false),
    retry: false,
  });

  const updateModifier = (nextModifier: CriterionModifier) => {
    onChange({ value: value?.value ?? "", modifier: nextModifier });
  };

  const selectFolder = (path: string, checked: boolean) => {
    if (!checked) return;
    onChange({
      value: path,
      modifier: modifier === "NOT_UNDER_PATH" ? "NOT_UNDER_PATH" : "UNDER_PATH",
    });
  };

  return (
    <div className="space-y-4">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={updateModifier} />
      {!isNull ? (
        <>
          <div className="space-y-2">
            <div>
              <div className="text-sm font-medium text-secondary">Browse library folders</div>
              <p className="text-xs text-muted">Choose a folder to match it and all of its descendants.</p>
            </div>
            {rootsQuery.isLoading || (rootsQuery.isFetching && rootsQuery.isError) ? (
              <p className="text-xs text-muted">Loading library folders…</p>
            ) : rootsQuery.isError ? (
              <p className="text-xs text-muted">Folder browsing is unavailable. You can still enter a path manually.</p>
            ) : (
              <LibraryFolderTree
                roots={rootsQuery.data ?? []}
                selected={value?.value ? [value.value] : []}
                onToggle={selectFolder}
                selectionMode="single"
                probeChildren={false}
                emptyHint="No library folders are configured."
              />
            )}
          </div>
          <LabeledControl label="Path">
            <input
              aria-label="Path"
              type="text"
              value={value?.value ?? ""}
              onChange={(event) => onChange({ value: event.target.value, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              placeholder="Enter a file or folder path"
            />
          </LabeledControl>
        </>
      ) : null}
    </div>
  );
}

export function RemoteIdFilterEditor({
  value,
  onChange,
  modifiers,
  metadataServers,
}: {
  value?: StringCriterion & { endpoint?: string };
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
  metadataServers?: MetadataServer[];
}) {
  const appConfig = useOptionalAppConfig();
  const modifier = value?.modifier ?? "EQUALS";
  const selectedEndpoint = value?.endpoint?.trim() ?? "";
  const isNull = NULL_VALUE_MODIFIERS.has(modifier);
  const configuredServers = metadataServers ?? appConfig?.config?.scraping?.metadataServers ?? [];
  const options = useMemo(() => {
    const endpoints = new Set<string>();
    const configured = configuredServers.flatMap((server) => {
      const endpoint = server.endpoint.trim();
      const normalizedEndpoint = endpoint.toLowerCase();
      if (!endpoint || endpoints.has(normalizedEndpoint)) return [];
      endpoints.add(normalizedEndpoint);
      const optionValue = selectedEndpoint.toLowerCase() === normalizedEndpoint ? selectedEndpoint : endpoint;
      return [{ value: optionValue, label: server.name?.trim() || endpoint }];
    });

    if (selectedEndpoint && !endpoints.has(selectedEndpoint.toLowerCase())) {
      configured.push({ value: selectedEndpoint, label: `${selectedEndpoint} (unconfigured)` });
    }

    return configured;
  }, [configuredServers, selectedEndpoint]);

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(nextModifier) => onChange({ value: value?.value ?? "", endpoint: selectedEndpoint, modifier: nextModifier })}
      />
      <select
        aria-label="Metadata Service"
        value={selectedEndpoint}
        onChange={(event) => onChange({ value: value?.value ?? "", endpoint: event.target.value, modifier })}
        className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none disabled:opacity-60 md:text-sm"
      >
        <option value="">Any metadata service</option>
        {options.map((option) => (
          <option key={option.value} value={option.value}>{option.label}</option>
        ))}
      </select>
      {!isNull && (
        <input
          type="text"
          aria-label="Remote ID value"
          value={value?.value ?? ""}
          onChange={(event) => onChange({ value: event.target.value, endpoint: selectedEndpoint, modifier })}
          className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
          placeholder="Value..."
        />
      )}
    </div>
  );
}

function HashEditor({
  value,
  onChange,
  modifiers,
  options,
}: {
  value?: FingerprintCriterion;
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
  options: { value: string; label: string }[];
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const hashType = value?.type ?? options[0]?.value ?? "md5";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <select
        value={hashType}
        onChange={(event) => onChange({ type: event.target.value, value: value?.value ?? "", modifier })}
        className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>{option.label}</option>
        ))}
      </select>
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(nextModifier) => onChange({ type: hashType, value: value?.value ?? "", modifier: nextModifier })} />
      {!isNull && (
        <LabeledControl label="Value">
          <input
            type="text"
            aria-label="Value"
            value={value?.value ?? ""}
            onChange={(event) => onChange({ type: hashType, value: event.target.value, modifier })}
            className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
            placeholder="Hash value..."
          />
        </LabeledControl>
      )}
    </div>
  );
}

// ===== Enum Editor =====

function EnumEditor({ value, onChange, options, modifiers }: { value?: StringCriterion; onChange: (v: unknown) => void; options: { value: string; label: string }[]; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <select
          value={value?.value ?? ""}
          onChange={(e) => onChange({ value: e.target.value, modifier })}
          className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
        >
          <option value="">Select...</option>
          {options.map((opt) => (
            <option key={opt.value} value={opt.value}>{opt.label}</option>
          ))}
        </select>
      )}
    </div>
  );
}

function MultiEnumEditor({ value, onChange, options }: { value?: StringCriterion; onChange: (v: unknown) => void; options: { value: string; label: string }[] }) {
  const selectionMode = value?.modifier === "NOT_MATCHES_REGEX"
    ? "exclude"
    : value?.modifier === "IS_NULL"
    ? "isNull"
    : value?.modifier === "NOT_NULL"
    ? "notNull"
    : "include";
  const selectedValues = useMemo(() => {
    const storedValues = (value as { _selectedValues?: string[] } | undefined)?._selectedValues;
    if (Array.isArray(storedValues) && storedValues.length > 0) {
      return options.filter((option) => storedValues.includes(option.value)).map((option) => option.value);
    }

    if (!value?.value) {
      return [];
    }

    if (value.modifier === "MATCHES_REGEX" || value.modifier === "NOT_MATCHES_REGEX") {
      try {
        const regex = new RegExp(value.value, "i");
        return options.filter((option) => regex.test(option.value)).map((option) => option.value);
      } catch {
        return [];
      }
    }

    return options.some((option) => option.value === value.value) ? [value.value] : [];
  }, [options, value]);

  const buildCriterion = (nextSelectedValues: string[], nextMode: "include" | "exclude" | "isNull" | "notNull") => {
    if (nextMode === "isNull") {
      onChange({ value: "", modifier: "IS_NULL", _selectedValues: nextSelectedValues });
      return;
    }

    if (nextMode === "notNull") {
      onChange({ value: "", modifier: "NOT_NULL", _selectedValues: nextSelectedValues });
      return;
    }

    const escapedValues = nextSelectedValues.map((selectedValue) => selectedValue.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
    onChange({
      value: escapedValues.length > 0 ? `^(?:${escapedValues.join("|")})$` : "",
      modifier: nextMode === "exclude" ? "NOT_MATCHES_REGEX" : "MATCHES_REGEX",
      _selectedValues: nextSelectedValues,
    });
  };

  const toggleValue = (optionValue: string) => {
    const nextSelectedValues = selectedValues.includes(optionValue)
      ? selectedValues.filter((selectedValue) => selectedValue !== optionValue)
      : [...selectedValues, optionValue];
    buildCriterion(nextSelectedValues, selectionMode);
  };

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2" role="group" aria-label="Match mode">
        {([
          ["include", "Any Of"],
          ["exclude", "None Of"],
          ["isNull", "No Value"],
          ["notNull", "Has Value"],
        ] as const).map(([mode, label]) => (
          <button
            key={mode}
            onClick={() => buildCriterion(selectedValues, mode)}
            className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${
              selectionMode === mode
                ? "bg-accent text-white border-accent"
                : "border-border text-secondary hover:text-foreground hover:border-accent/50"
            }`}
          >
            {label}
          </button>
        ))}
      </div>
      {(selectionMode === "include" || selectionMode === "exclude") && (
        <div className="grid gap-1 sm:grid-cols-2">
          {options.map((option) => {
            const checked = selectedValues.includes(option.value);

            return (
              <label key={option.value} className="flex min-h-9 items-center gap-2 rounded-lg border border-border bg-input px-3 py-1.5 text-sm text-foreground">
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => toggleValue(option.value)}
                  className="h-5 w-5 accent-accent"
                />
                <span>{option.label}</span>
              </label>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ===== Date Editor =====

function DateEditor({ value, onChange, modifiers }: { value?: DateCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })} />
      {!isNull && (
        <div className={`grid gap-3 ${isBetween ? "sm:grid-cols-2" : ""}`}>
          <LabeledControl label={isBetween ? "Minimum" : "Value"}>
            <IsoDateInput
              aria-label={isBetween ? "Minimum" : "Value"}
              value={value?.value ?? ""}
              onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            />
          </LabeledControl>
          {isBetween && (
            <LabeledControl label="Maximum">
              <IsoDateInput
                aria-label="Maximum"
                value={value?.value2 ?? ""}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              />
            </LabeledControl>
          )}
        </div>
      )}
    </div>
  );
}

// ===== Timestamp Editor =====

function getDefaultLocalTimestampValue() {
  const date = new Date();
  date.setHours(12, 0, 0, 0);

  const pad = (part: number) => String(part).padStart(2, "0");

  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function TimestampEditor({ value, onChange, modifiers }: { value?: TimestampCriterion; onChange: (v: unknown) => void; modifiers: CriterionModifier[] }) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";
  const ensureTimestampValue = (current?: string) => (current && current.length > 0 ? current : getDefaultLocalTimestampValue());

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(m) => {
          const nextIsNull = m === "IS_NULL" || m === "NOT_NULL";
          const nextIsBetween = m === "BETWEEN" || m === "NOT_BETWEEN";
          onChange({
            value: nextIsNull ? (value?.value ?? "") : ensureTimestampValue(value?.value),
            value2: nextIsBetween ? ensureTimestampValue(value?.value2) : undefined,
            modifier: m,
          });
        }}
      />
      {!isNull && (
        <div className={`grid gap-3 ${isBetween ? "sm:grid-cols-2" : ""}`}>
          <LabeledControl label={isBetween ? "Minimum" : "Value"}>
            <IsoDateInput
              aria-label={isBetween ? "Minimum" : "Value"}
              pickerType="datetime-local"
              value={value?.value ?? ensureTimestampValue(value?.value)}
              onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            />
          </LabeledControl>
          {isBetween && (
            <LabeledControl label="Maximum">
              <IsoDateInput
                aria-label="Maximum"
                pickerType="datetime-local"
                value={value?.value2 ?? ensureTimestampValue(value?.value2)}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              />
            </LabeledControl>
          )}
        </div>
      )}
    </div>
  );
}

// ===== MultiId Editor =====

function MultiIdEditor({ value, onChange, entityType, modifiers, hierarchyToggleLabel }: { value?: MultiIdCriterion; onChange: (v: unknown) => void; entityType: EntityType; modifiers: CriterionModifier[]; hierarchyToggleLabel?: string }) {
  const includeModifiers = modifiers.filter((modifier) => modifier === "INCLUDES" || modifier === "INCLUDES_ALL");
  const supportsExclude = modifiers.some((modifier) => modifier === "EXCLUDES" || modifier === "EXCLUDES_ALL");
  const modifier = value?.modifier ?? (includeModifiers.includes("INCLUDES_ALL") ? "INCLUDES_ALL" : "INCLUDES");
  const nullModifiers = modifiers.filter((item) => NULL_VALUE_MODIFIERS.has(item));
  const isNullModifier = NULL_VALUE_MODIFIERS.has(modifier);
  const includedIds = value?.value ?? [];
  const excludedIds = supportsExclude ? value?.excludes ?? [] : [];
  const includeHierarchy = (value as any)?.depth === -1;
  const existingNames: Record<string, string> = (value as any)?._names ?? {};
  const [searchText, setSearchText] = useState("");
  const [activeResultIndex, setActiveResultIndex] = useState(-1);
  const [pendingNullModifier, setPendingNullModifier] = useState<CriterionModifier | null>(null);
  const resultsRef = useRef<HTMLDivElement>(null);
  const searchInputRef = useRef<HTMLInputElement>(null);
  const selectedButtonRefs = useRef(new Map<number, HTMLButtonElement>());
  const pendingSelectedRemovalRef = useRef<{ id: number | null; label: string } | null>(null);
  const [selectedValueFocusId, setSelectedValueFocusId] = useState<number | null>(() => includedIds[0] ?? excludedIds[0] ?? null);
  const [selectionAnnouncement, setSelectionAnnouncement] = useState("");
  const selectedValueInstructionsId = useId();
  const trimmedSearchText = searchText.trim();

  const { data: entities, isPlaceholderData, isFetching } = useQuery({
    queryKey: ["multi-id-selector", entityType, trimmedSearchText],
    queryFn: async () => {
      switch (entityType) {
        case "tags": return (await tagsApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "name", direction: "asc" }, { includeCounts: false })).items;
        case "tagGroups": return await tagGroupsApi.list();
        case "performers": return (await performersApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "name", direction: "asc" })).items;
        case "studios": return (await studiosApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "name", direction: "asc" })).items;
        case "groups": return (await groupsApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "name", direction: "asc" })).items;
        case "galleries": return (await galleriesApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "title", direction: "asc" })).items;
        case "videos": return (await videosApi.find({ q: trimmedSearchText || undefined, perPage: 50, sort: "title", direction: "asc" })).items;
        case "faces": return (await facesApi.list({ q: trimmedSearchText || undefined, merged: false, page: 1, perPage: 50 })).items;
        default: return [];
      }
    },
    // Video libraries make a remote title search noticeably slower; clear their unrelated initial
    // page while searching so a successful autocomplete does not look broken.
    placeholderData: entityType === "videos" && trimmedSearchText ? undefined : (previousData) => previousData,
    staleTime: 60000,
  });

  const selectedIds = useMemo(() => Array.from(new Set([...includedIds, ...excludedIds])), [excludedIds, includedIds]);
  const selectedIdsSignature = selectedIds.join(",");
  useEffect(() => {
    if (selectedValueFocusId != null && selectedIds.includes(selectedValueFocusId)) return;
    setSelectedValueFocusId(selectedIds[0] ?? null);
  }, [selectedIdsSignature, selectedValueFocusId]);
  useEffect(() => {
    const pending = pendingSelectedRemovalRef.current;
    if (!pending) return;
    pendingSelectedRemovalRef.current = null;
    setSelectionAnnouncement(`Removed ${pending.label}. ${selectedIds.length} selected.`);
    if (pending.id != null) selectedButtonRefs.current.get(pending.id)?.focus();
    else searchInputRef.current?.focus();
  }, [selectedIdsSignature]);
  const missingSelectedIds = useMemo(() => {
    const availableIds = new Set((entities as any[] | undefined)?.map((entity) => entity.id) ?? []);
    return selectedIds.filter((id) => !existingNames[String(id)] && !availableIds.has(id));
  }, [entities, existingNames, selectedIds]);
  const selectedEntityQueries = useQueries({
    queries: missingSelectedIds.map((id) => ({
      queryKey: ["multi-id-selector", entityType, "selected", id],
      queryFn: () => getMultiIdEntityLabel(entityType, id),
      staleTime: 60000,
    })),
  });

  // Build a name lookup from available entities
  const nameMap = useMemo(() => {
    const map: Record<string, string> = { ...existingNames };
    if (entities) for (const e of entities as any[]) map[String(e.id)] = e.name || e.title || getMultiIdEntityLabels(entityType).singular;
    for (const query of selectedEntityQueries) {
      if (query.data) map[String(query.data.id)] = query.data.label;
    }
    return map;
  }, [entities, existingNames, selectedEntityQueries]);

  const buildCriterion = (inc: number[], exc: number[], mod: string, includeChildren: boolean) => {
    // Include _names so filter chips can display entity names without waiting for queries
    const names: Record<string, string> = {};
    for (const id of [...inc, ...exc]) {
      if (nameMap[String(id)]) names[String(id)] = nameMap[String(id)];
    }
    return {
      value: inc,
      modifier: mod,
      excludes: exc.length > 0 ? exc : undefined,
      ...(includeChildren ? { depth: -1 } : {}),
      _names: Object.keys(names).length > 0 ? names : undefined,
    };
  };

  const filteredEntities = useMemo(() => {
    if (!entities) return [];
    const q = searchText.trim().toLowerCase();
    const available = entityType === "tagGroups" && q
      ? entities.filter((entity: any) => (entity.name || entity.title || "").toLowerCase().includes(q))
      : entities;
    return q ? rankByLabel(available as any[], searchText, (entity) => entity.name || entity.title || entity.label || entity.performerName || "") : available;
  }, [entities, entityType, searchText]);

  const navigableEntities = useMemo(() => {
    const visible = filteredEntities.slice(0, 50);
    if (entityType === "tags" && !trimmedSearchText) {
      return groupTagsForSelector(visible as any[]).flatMap((group) => group.tags);
    }
    return visible;
  }, [entityType, filteredEntities, trimmedSearchText]);

  useEffect(() => {
    setActiveResultIndex((current) => current >= navigableEntities.length ? -1 : current);
  }, [navigableEntities.length]);

  useEffect(() => {
    if (activeResultIndex < 0) return;
    const activeEntity = navigableEntities[activeResultIndex] as any;
    if (!activeEntity) return;
    resultsRef.current
      ?.querySelector<HTMLElement>(`#multi-id-result-${entityType}-${activeEntity.id}`)
      ?.scrollIntoView({ block: "nearest" });
  }, [activeResultIndex, entityType, navigableEntities]);

  const addInclude = (id: number) => {
    const nextInc = includedIds.includes(id) ? includedIds : [...includedIds, id];
    const nextExc = excludedIds.filter((i) => i !== id);
    onChange(buildCriterion(nextInc, nextExc, modifier, includeHierarchy));
    setSearchText("");
    setActiveResultIndex(-1);
  };

  const addExclude = (id: number) => {
    if (!supportsExclude) {
      return;
    }

    const nextInc = includedIds.filter((i) => i !== id);
    const nextExc = excludedIds.includes(id) ? excludedIds : [...excludedIds, id];
    onChange(buildCriterion(nextInc, nextExc, modifier, includeHierarchy));
    setSearchText("");
    setActiveResultIndex(-1);
  };

  const removeId = (id: number) => {
    const nextInc = includedIds.filter((i) => i !== id);
    const nextExc = excludedIds.filter((i) => i !== id);
    onChange(buildCriterion(nextInc, nextExc, modifier, includeHierarchy));
  };

  const labels = getMultiIdEntityLabels(entityType);
  const selectedCount = includedIds.length + excludedIds.length;
  const selectNullModifier = (nextModifier: CriterionModifier) => {
    if (selectedCount > 0) {
      setPendingNullModifier(nextModifier);
      return;
    }

    onChange({ modifier: nextModifier });
  };
  const matchModifiers = [
    ...(includeModifiers.length > 0
      ? [...includeModifiers].sort((a, b) => (a === "INCLUDES_ALL" ? -1 : b === "INCLUDES_ALL" ? 1 : 0))
      : (["INCLUDES"] as CriterionModifier[])),
    ...nullModifiers,
  ];
  const selectMatchModifier = (nextModifier: CriterionModifier) => {
    if (NULL_VALUE_MODIFIERS.has(nextModifier)) {
      selectNullModifier(nextModifier);
      return;
    }

    onChange(buildCriterion(includedIds, excludedIds, nextModifier, includeHierarchy));
  };
  const handleMatchModeKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    const currentButton = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-match-modifier]");
    if (!currentButton) return;
    const currentModifier = currentButton.dataset.matchModifier as CriterionModifier;
    const currentIndex = matchModifiers.indexOf(currentModifier);
    if (currentIndex < 0) return;

    event.preventDefault();
    event.stopPropagation();
    const direction = event.key === "ArrowRight" ? 1 : -1;
    const nextModifier = matchModifiers[(currentIndex + direction + matchModifiers.length) % matchModifiers.length];
    event.currentTarget
      .querySelector<HTMLButtonElement>(`button[data-match-modifier="${nextModifier}"]`)
      ?.focus();
    selectMatchModifier(nextModifier);
  };
  const getName = (e: any) => e.name || e.title || e.label || e.performerName || labels.singular;
  const getSelectedName = (id: number, entity?: any) => {
    if (entity) return getName(entity);
    const hydratedName = nameMap[String(id)];
    if (hydratedName) return hydratedName;
    const missingIndex = missingSelectedIds.indexOf(id);
    if (missingIndex >= 0 && selectedEntityQueries[missingIndex]?.isError) return `Unavailable ${labels.singular}`;
    return `Loading ${labels.singular}...`;
  };
  const focusSelectedValue = (id: number) => {
    setSelectedValueFocusId(id);
    selectedButtonRefs.current.get(id)?.focus();
  };
  const removeSelectedValue = (id: number) => {
    const index = selectedIds.indexOf(id);
    const nextId = selectedIds[index + 1] ?? selectedIds[index - 1] ?? null;
    pendingSelectedRemovalRef.current = { id: nextId, label: getSelectedName(id) };
    setSelectedValueFocusId(nextId);
    removeId(id);
  };
  const handleSelectedValueKeyDown = (event: ReactKeyboardEvent<HTMLButtonElement>, id: number) => {
    const currentIndex = selectedIds.indexOf(id);
    if (currentIndex < 0) return;
    if (event.key === "ArrowRight" || event.key === "ArrowLeft") {
      event.preventDefault();
      const direction = event.key === "ArrowRight" ? 1 : -1;
      focusSelectedValue(selectedIds[(currentIndex + direction + selectedIds.length) % selectedIds.length]);
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      focusSelectedValue(event.key === "Home" ? selectedIds[0] : selectedIds[selectedIds.length - 1]);
    } else if (event.key === "Delete" || event.key === "Backspace") {
      event.preventDefault();
      removeSelectedValue(id);
    }
  };

  return (
    <div className="flex min-h-full flex-1 flex-col gap-2">
      <div className="space-y-2">
        <div className="text-sm font-medium text-secondary">Match</div>
        <div className="flex flex-col gap-2 md:flex-row md:items-center">
          <div className="flex flex-wrap gap-2" role="group" aria-label="Match mode" onKeyDown={handleMatchModeKeyDown}>
            {matchModifiers.map((m) => (
              <button
                type="button"
                key={m}
                data-match-modifier={m}
                onClick={() => selectMatchModifier(m)}
                aria-pressed={m === modifier}
                tabIndex={m === modifier ? 0 : -1}
                className={`min-h-9 flex-1 whitespace-nowrap rounded-lg border px-3 py-1.5 text-sm md:flex-none ${
                  m === modifier
                    ? "border-accent bg-accent text-white"
                    : "border-border text-secondary hover:border-accent/50 hover:text-foreground"
                }`}
              >
                {getMultiIdModifierLabel(m, entityType, MODIFIER_LABELS[m])}
              </button>
            ))}
          </div>
          {!isNullModifier && (entityType === "tags" || hierarchyToggleLabel) && (
            <label className="flex min-h-9 cursor-pointer select-none items-center gap-2 text-sm text-secondary md:ml-auto">
              <input
                type="checkbox"
                checked={includeHierarchy}
                onChange={(e) => {
                  onChange(buildCriterion(includedIds, excludedIds, modifier, e.target.checked));
                }}
                className="h-5 w-5 accent-accent"
              />
              {hierarchyToggleLabel ?? "Include sub-tags"}
            </label>
          )}
        </div>
      </div>
      {isNullModifier ? (
        <div className="rounded border border-border/70 bg-input px-2 py-2 text-xs text-muted">
          This criterion will match entities with {modifier === "IS_NULL" ? "no" : "at least one"} linked {entityType} item.
        </div>
      ) : (
        <>
      {/* Search + add */}
      <div className="relative">
        <input
          ref={searchInputRef}
          type="text"
          role="combobox"
          aria-label={`Search ${entityType}`}
          aria-autocomplete="list"
          aria-controls={`multi-id-results-${entityType}`}
          aria-expanded={!isNullModifier}
          aria-activedescendant={activeResultIndex >= 0 ? `multi-id-result-${entityType}-${navigableEntities[activeResultIndex]?.id}` : undefined}
          data-filter-primary-control
          value={searchText}
          onChange={(e) => {
            setSearchText(e.target.value);
            setActiveResultIndex(-1);
          }}
          onKeyDown={(event) => {
            if (isPlaceholderData && (event.key === "ArrowDown" || event.key === "ArrowUp" || (event.key === "Enter" && !event.ctrlKey && !event.metaKey))) {
              event.preventDefault();
              return;
            }
            if (event.key === "ArrowDown" && navigableEntities.length > 0) {
              event.preventDefault();
              setActiveResultIndex((current) => Math.min(navigableEntities.length - 1, current + 1));
            } else if (event.key === "ArrowUp" && navigableEntities.length > 0) {
              event.preventDefault();
              setActiveResultIndex((current) => current <= 0 ? 0 : current - 1);
            } else if (event.key === "Enter" && !event.ctrlKey && !event.metaKey && navigableEntities.length > 0) {
              event.preventDefault();
              const entity = navigableEntities[activeResultIndex >= 0 ? activeResultIndex : 0] as any;
              if (event.shiftKey && supportsExclude) addExclude(entity.id);
              else addInclude(entity.id);
            }
          }}
          placeholder={`Search ${entityType}...`}
          className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
        />
      </div>
      <p className="hidden text-xs text-muted md:block">Use ↑/↓ to choose, Enter to include, Shift+Enter to exclude. Ctrl/⌘+Enter applies.</p>
      <div ref={resultsRef} id={`multi-id-results-${entityType}`} role="listbox" aria-label={`${labels.plural} results`} tabIndex={-1} className="max-h-64 shrink-0 overflow-y-auto rounded-lg border border-border bg-input" aria-busy={(isPlaceholderData || isFetching) || undefined}>
        {!entities && trimmedSearchText && (
          <div className="px-2 py-2 text-xs text-muted text-center">Searching…</div>
        )}
        {entityType === "tags" ? (
          <GroupedTagOptionList
            tags={navigableEntities as any[]}
            maxItems={50}
            className="border-0 bg-transparent"
            preserveOrder={Boolean(searchText.trim())}
            groupToggleTabIndex={-1}
            groupHeadersInteractive={false}
            renderTag={(entity: any) => {
              const isIncluded = includedIds.includes(entity.id);
              const isExcluded = excludedIds.includes(entity.id);
              return (
                <div
                  id={`multi-id-result-${entityType}-${entity.id}`}
                  role="option"
                  aria-label={getName(entity)}
                  aria-selected={isIncluded || isExcluded}
                  aria-disabled={isPlaceholderData || undefined}
                  onClick={() => { if (!isPlaceholderData) isIncluded ? removeId(entity.id) : addInclude(entity.id); }}
                  className={`flex min-h-11 w-full items-center gap-1 px-1 text-sm ${isPlaceholderData ? "cursor-wait" : "cursor-pointer"} ${activeResultIndex >= 0 && navigableEntities[activeResultIndex]?.id === entity.id ? "bg-accent/15 ring-1 ring-inset ring-accent" : ""} ${isIncluded ? "text-green-300" : isExcluded ? "text-red-300" : "text-foreground"}`}
                >
                  <button
                    type="button"
                    tabIndex={-1}
                    onClick={(event) => { event.stopPropagation(); isIncluded ? removeId(entity.id) : addInclude(entity.id); }}
                    className={`inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg hover:bg-green-500/10 hover:text-green-400 disabled:cursor-wait ${isIncluded ? "text-green-400" : "text-muted"}`}
                    title="Include"
                    aria-label={`Include ${getName(entity)}`}
                    disabled={isPlaceholderData}
                  >
                    <Plus className="w-3 h-3" />
                  </button>
                  <span className="min-w-0 flex-1 truncate">{getName(entity)}</span>
                  {supportsExclude && (
                    <button
                      type="button"
                      tabIndex={-1}
                      onClick={(event) => { event.stopPropagation(); isExcluded ? removeId(entity.id) : addExclude(entity.id); }}
                      className={`inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg hover:bg-red-500/10 hover:text-red-400 disabled:cursor-wait ${isExcluded ? "text-red-400" : "text-muted"}`}
                      title="Exclude"
                      aria-label={`Exclude ${getName(entity)}`}
                      disabled={isPlaceholderData}
                    >
                      <Minus className="w-3 h-3" />
                    </button>
                  )}
                </div>
              );
            }}
          />
        ) : navigableEntities.map((entity: any) => {
          const isIncluded = includedIds.includes(entity.id);
          const isExcluded = excludedIds.includes(entity.id);
          return (
            <div
              key={entity.id}
              id={`multi-id-result-${entityType}-${entity.id}`}
              role="option"
              aria-label={getName(entity)}
              aria-selected={isIncluded || isExcluded}
              aria-disabled={isPlaceholderData || undefined}
              onClick={() => { if (!isPlaceholderData) isIncluded ? removeId(entity.id) : addInclude(entity.id); }}
              className={`flex min-h-11 w-full items-center gap-1 px-1 text-sm ${isPlaceholderData ? "cursor-wait" : "cursor-pointer"} ${activeResultIndex >= 0 && navigableEntities[activeResultIndex]?.id === entity.id ? "bg-accent/15 ring-1 ring-inset ring-accent" : ""} ${isIncluded ? "text-green-300" : isExcluded ? "text-red-300" : "text-foreground"}`}
            >
              <button
                type="button"
                tabIndex={-1}
                onClick={(event) => { event.stopPropagation(); isIncluded ? removeId(entity.id) : addInclude(entity.id); }}
                className={`inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg hover:bg-green-500/10 hover:text-green-400 disabled:cursor-wait ${isIncluded ? "text-green-400" : "text-muted"}`}
                title="Include"
                aria-label={`Include ${getName(entity)}`}
                disabled={isPlaceholderData}
              >
                <Plus className="w-3 h-3" />
              </button>
              <span className="min-w-0 flex-1 truncate">{getName(entity)}</span>
              {supportsExclude && (
                <button
                  type="button"
                  tabIndex={-1}
                  onClick={(event) => { event.stopPropagation(); isExcluded ? removeId(entity.id) : addExclude(entity.id); }}
                  className={`inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg hover:bg-red-500/10 hover:text-red-400 disabled:cursor-wait ${isExcluded ? "text-red-400" : "text-muted"}`}
                  title="Exclude"
                  aria-label={`Exclude ${getName(entity)}`}
                  disabled={isPlaceholderData}
                >
                  <Minus className="w-3 h-3" />
                </button>
              )}
            </div>
          );
        })}
        {entities && filteredEntities.length === 0 && (
          <div className="px-2 py-2 text-xs text-muted text-center">No results</div>
        )}
      </div>
      {/* Selected items: included */}
      {includedIds.length > 0 && (
        <div className="flex flex-wrap gap-1" role="group" aria-label={`Included ${labels.plural}`}>
          {includedIds.map((id) => {
            const entity = entities?.find((e: any) => e.id === id);
            return (
              <span key={id} className="inline-flex h-8 items-center overflow-hidden rounded-md border border-green-700 bg-green-900/50 pl-2 text-sm text-green-300 focus-within:ring-2 focus-within:ring-accent">
                <span className="max-w-56 truncate">{getSelectedName(id, entity)}</span>
                <button
                  type="button"
                  ref={(element) => {
                    if (element) selectedButtonRefs.current.set(id, element);
                    else selectedButtonRefs.current.delete(id);
                  }}
                  onClick={() => removeSelectedValue(id)}
                  onFocus={() => setSelectedValueFocusId(id)}
                  onKeyDown={(event) => handleSelectedValueKeyDown(event, id)}
                  tabIndex={selectedValueFocusId === id || (selectedValueFocusId == null && id === selectedIds[0]) ? 0 : -1}
                  className="ml-2 inline-flex h-8 w-8 items-center justify-center border-l border-green-700 hover:bg-red-500/10 hover:text-red-300 focus:outline-none"
                  aria-label={`Remove ${getSelectedName(id, entity)}`}
                  aria-describedby={selectedValueInstructionsId}
                  aria-keyshortcuts="ArrowLeft ArrowRight Home End Delete Backspace"
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </span>
            );
          })}
        </div>
      )}
      {/* Selected items: excluded */}
      {supportsExclude && excludedIds.length > 0 && (
        <div className="flex flex-wrap gap-1" role="group" aria-label={`Excluded ${labels.plural}`}>
          {excludedIds.map((id) => {
            const entity = entities?.find((e: any) => e.id === id);
            return (
              <span key={id} className="inline-flex h-8 items-center overflow-hidden rounded-md border border-red-700 bg-red-900/50 pl-2 text-sm text-red-300 focus-within:ring-2 focus-within:ring-accent">
                <span className="max-w-56 truncate">{getSelectedName(id, entity)}</span>
                <button
                  type="button"
                  ref={(element) => {
                    if (element) selectedButtonRefs.current.set(id, element);
                    else selectedButtonRefs.current.delete(id);
                  }}
                  onClick={() => removeSelectedValue(id)}
                  onFocus={() => setSelectedValueFocusId(id)}
                  onKeyDown={(event) => handleSelectedValueKeyDown(event, id)}
                  tabIndex={selectedValueFocusId === id || (selectedValueFocusId == null && id === selectedIds[0]) ? 0 : -1}
                  className="ml-2 inline-flex h-8 w-8 items-center justify-center border-l border-red-700 hover:bg-red-500/10 hover:text-red-200 focus:outline-none"
                  aria-label={`Remove ${getSelectedName(id, entity)}`}
                  aria-describedby={selectedValueInstructionsId}
                  aria-keyshortcuts="ArrowLeft ArrowRight Home End Delete Backspace"
                >
                  <X className="h-3.5 w-3.5" />
                </button>
              </span>
            );
          })}
        </div>
      )}
      {selectedIds.length > 0 ? (
        <p id={selectedValueInstructionsId} className="text-xs text-muted">Selected {labels.plural}: use ←/→ to review; Delete removes.</p>
      ) : null}
      <span className="sr-only" role="status" aria-live="polite">{selectionAnnouncement}</span>
        </>
      )}
      <ConfirmDialog
        open={pendingNullModifier !== null}
        title={`Clear selected ${labels.plural}?`}
        message={`Switching to ${pendingNullModifier ? getMultiIdModifierLabel(pendingNullModifier, entityType, MODIFIER_LABELS[pendingNullModifier]) : "this match mode"} will clear ${selectedCount} selected ${selectedCount === 1 ? labels.singular : labels.plural}.`}
        confirmLabel="Clear selection"
        onConfirm={() => {
          if (pendingNullModifier) {
            onChange({ modifier: pendingNullModifier });
          }
          setPendingNullModifier(null);
        }}
        onCancel={() => setPendingNullModifier(null)}
      />
    </div>
  );
}

const MULTI_ID_ENTITY_LABELS: Record<EntityType, { singular: string; plural: string }> = {
  tags: { singular: "tag", plural: "tags" },
  tagGroups: { singular: "tag group", plural: "tag groups" },
  performers: { singular: "performer", plural: "performers" },
  studios: { singular: "studio", plural: "studios" },
  groups: { singular: "group", plural: "groups" },
  galleries: { singular: "gallery", plural: "galleries" },
  videos: { singular: "video", plural: "videos" },
  faces: { singular: "face", plural: "faces" },
};

function getMultiIdEntityLabels(entityType: EntityType) {
  return MULTI_ID_ENTITY_LABELS[entityType];
}

async function getMultiIdEntityLabel(entityType: EntityType, id: number): Promise<{ id: number; label: string }> {
  switch (entityType) {
    case "tags": {
      const tag = await tagsApi.get(id);
      return { id, label: tag.name };
    }
    case "tagGroups": {
      const tagGroup = await tagGroupsApi.get(id);
      return { id, label: tagGroup.name };
    }
    case "performers": {
      const performer = await performersApi.get(id);
      return { id, label: performer.name };
    }
    case "studios": {
      const studio = await studiosApi.get(id);
      return { id, label: studio.name };
    }
    case "groups": {
      const group = await groupsApi.get(id);
      return { id, label: group.name };
    }
    case "galleries": {
      const gallery = await galleriesApi.get(id);
      return { id, label: gallery.title?.trim() || "Untitled gallery" };
    }
    case "videos": {
      const video = await videosApi.get(id);
      return { id, label: video.title?.trim() || video.code?.trim() || video.files?.[0]?.basename || "Untitled video" };
    }
    case "faces": {
      const face = await facesApi.get(id);
      return { id, label: face.label?.trim() || face.performerName?.trim() || `Face #${id}` };
    }
  }
}

// ===== Shared Components =====

function ModifierSelector({ modifiers, selected, onSelect }: { modifiers: CriterionModifier[]; selected: CriterionModifier; onSelect: (m: CriterionModifier) => void }) {
  const handleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    const currentButton = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-modifier]");
    if (!currentButton) return;
    const currentModifier = currentButton.dataset.modifier as CriterionModifier;
    const currentIndex = modifiers.indexOf(currentModifier);
    if (currentIndex < 0) return;

    event.preventDefault();
    event.stopPropagation();
    const direction = event.key === "ArrowRight" ? 1 : -1;
    const nextModifier = modifiers[(currentIndex + direction + modifiers.length) % modifiers.length];
    event.currentTarget.querySelector<HTMLButtonElement>(`button[data-modifier="${nextModifier}"]`)?.focus();
    onSelect(nextModifier);
  };

  return (
    <div className="space-y-2" role="group" aria-label="Match">
      <div className="text-sm font-medium text-secondary">Match</div>
      <div className="flex flex-wrap gap-2" onKeyDown={handleKeyDown}>
      {modifiers.map((m) => (
        <button
          type="button"
          key={m}
          data-modifier={m}
          aria-pressed={m === selected}
          aria-keyshortcuts="ArrowLeft ArrowRight"
          tabIndex={m === selected ? 0 : -1}
          onClick={() => onSelect(m)}
          className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${
            m === selected
              ? "bg-accent text-white border-accent"
              : "border-border text-secondary hover:text-foreground hover:border-accent/50"
          }`}
        >
          {MODIFIER_LABELS[m]}
        </button>
      ))}
      </div>
    </div>
  );
}

function LabeledControl({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block space-y-1.5 text-sm font-medium text-secondary">
      <span>{label}</span>
      {children}
    </label>
  );
}

function formatDurationInputValue(value?: number) {
  if (value == null) {
    return "";
  }

  const h = Math.floor((value ?? 0) / 3600);
  const m = Math.floor(((value ?? 0) % 3600) / 60);
  const s = (value ?? 0) % 60;
  return h > 0 ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}` : `${m}:${String(s).padStart(2, "0")}`;
}

function parseDurationInputValue(value: string) {
  const trimmed = value.trim();
  if (trimmed === "") return undefined;
  const parts = trimmed.split(":").map(Number);
  if (parts.some((part) => !Number.isFinite(part))) return undefined;
  const seconds = parts.length === 3
    ? parts[0] * 3600 + parts[1] * 60 + parts[2]
    : parts.length === 2
      ? parts[0] * 60 + parts[1]
      : parts[0];
  return seconds >= 0 ? seconds : undefined;
}

function DurationInput({ value, onChange, ariaLabel }: { value?: number; onChange: (v: number | undefined) => void; ariaLabel?: string }) {
  const [inputText, setInputText] = useState(() => formatDurationInputValue(value));
  const descriptionId = useId();

  useEffect(() => {
    setInputText(formatDurationInputValue(value));
  }, [value]);

  const commit = (rawValue: string) => {
    const parsed = parseDurationInputValue(rawValue);
    setInputText(formatDurationInputValue(parsed));
    onChange(parsed);
  };

  const humanValue = formatHumanDuration(parseDurationInputValue(inputText));

  return (
    <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
      <input
        type="text"
        value={inputText}
        onChange={(event) => setInputText(event.target.value)}
        onBlur={(event) => commit(event.target.value)}
        placeholder="H:MM:SS"
        aria-label={ariaLabel}
        aria-describedby={humanValue ? descriptionId : undefined}
        className="min-h-11 w-28 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
      />
      {humanValue ? <span id={descriptionId} aria-live="polite" className="text-xs font-normal text-muted">{humanValue}</span> : null}
    </span>
  );
}

function PercentInput({ value, onChange, ariaLabel }: { value?: number; onChange: (v: number | undefined) => void; ariaLabel?: string }) {
  return (
    <label className="relative inline-flex w-24 items-center">
      <input
        type="number"
        min={0}
        max={100}
        step={0.1}
        value={value ?? ""}
        onChange={(event) => onChange(event.target.value === "" ? undefined : Number(event.target.value))}
        aria-label={ariaLabel}
        className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 pr-8 text-base text-foreground outline-none focus:border-accent md:text-sm"
      />
      <span className="pointer-events-none absolute right-2 text-xs text-muted">%</span>
    </label>
  );
}

// CareerLengthInput stores its value as integer years (the backend's unit). The
// user can optionally enter a value in months and it will be converted to years
// (rounded to the nearest year, minimum 1 if any months were entered).
function CareerLengthInput({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  const [unit, setUnit] = useState<"years" | "months">("years");
  const display = unit === "years" ? value : value * 12;

  const handleAmountChange = (amount: number) => {
    if (unit === "years") {
      onChange(amount);
    } else {
      // Convert months to years: round to nearest, but if any months entered round up to at least 1.
      const years = Math.round(amount / 12);
      onChange(amount > 0 && years === 0 ? 1 : years);
    }
  };

  return (
    <div className="flex items-center gap-1">
      <input
        type="number"
        min={0}
        value={display === 0 ? "" : display}
        onChange={(e) => handleAmountChange(e.target.value === "" ? 0 : Number(e.target.value))}
        className="min-h-11 w-24 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
      />
      <select
        value={unit}
        onChange={(e) => setUnit(e.target.value as "years" | "months")}
        aria-label="Career length unit"
        className="min-h-11 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
      >
        <option value="years">Years</option>
        <option value="months">Months</option>
      </select>
    </div>
  );
}

function ResolutionSelect({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(Number(e.target.value))}
      className="min-h-11 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
    >
      {RESOLUTION_FILTER_OPTIONS.map((o) => (
        <option key={o.value} value={o.value}>{o.label}</option>
      ))}
    </select>
  );
}

// ===== Filter Button for ListPage =====

export function FilterButton({
  activeCount,
  onClick,
}: {
  activeCount: number;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={activeCount > 0 ? `Filters, ${activeCount} active` : "Filters"}
      className={`flex items-center gap-1 rounded border px-2 py-1 text-xs ${
        activeCount > 0
          ? "border-accent bg-accent/10 text-accent"
          : "border-border bg-card/70 text-secondary hover:border-accent hover:text-foreground"
      }`}
    >
      <svg className="h-3.5 w-3.5" aria-hidden="true" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
        <path strokeLinecap="round" strokeLinejoin="round" d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z" />
      </svg>
      Filters
      {activeCount > 0 && (
        <span className="min-w-[16px] rounded-full bg-accent px-1 py-0 text-center text-[10px] font-bold text-white" aria-hidden="true">
          {activeCount}
        </span>
      )}
    </button>
  );
}
