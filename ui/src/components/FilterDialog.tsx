import { useState, useMemo, useCallback, useEffect, useRef, type ReactNode } from "react";
import { useQueries, useQuery } from "@tanstack/react-query";
import { X, ChevronDown, ChevronRight, Search, Pin, PinOff, Plus, Minus, Star } from "lucide-react";
import { tags as tagsApi, performers as performersApi, studios as studiosApi, groups as groupsApi, galleries as galleriesApi, videos as videosApi, tagGroups as tagGroupsApi, faces as facesApi } from "../api/client";
import { GroupedTagOptionList } from "./TagSelector";
import { EntityReferenceSelector } from "./EntityReferenceSelector";
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
} from "../api/types";
import { RESOLUTION_FILTER_OPTIONS } from "../utils/resolutionBuckets";
import { rankByLabel } from "../utils/searchRanking";

// ===== Criterion definitions =====

export type CriterionType = "string" | "number" | "bool" | "date" | "timestamp" | "duration" | "tagDuration" | "careerLength" | "rating" | "resolution" | "multiId" | "enum" | "hash";
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
  options?: { value: string; label: string }[];
  multiSelectOptions?: boolean;
  hierarchyToggleLabel?: string;
  auxiliaryToggleKey?: TFilterKey;
  auxiliaryToggleLabel?: string;
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
};

// Which modifiers each type supports
const TYPE_MODIFIERS: Record<CriterionType, CriterionModifier[]> = {
  string: ["EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "MATCHES_REGEX", "NOT_MATCHES_REGEX", "IS_NULL", "NOT_NULL"],
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
    case "hash":
    case "date":
    case "timestamp":
    case "enum":
      return criterion.type === "hash"
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

  return { ...filter, [criterion.filterKey]: value };
}

function sanitizeFilterCriteria(filter: Record<string, unknown>, criteria: CriterionDefinition[], baseFilter: Record<string, unknown> = {}) {
  let sanitized: Record<string, unknown> = { ...baseFilter };

  for (const criterion of criteria) {
    const value = getCriterionFilterValue(filter, criterion);
    if (!isCriterionValueValid(value, criterion)) {
      continue;
    }

    if (criterion.customFieldKey) {
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
  { id: "path", label: "Path", type: "string", filterKey: "pathCriterion" },
  { id: "hash", label: "Hash", type: "hash", filterKey: "fingerprintCriterion", options: [...VIDEO_HASH_OPTIONS] },
  { id: "duplicatedPhash", label: "Duplicated (pHash)", type: "bool", filterKey: "duplicatedPhashCriterion" },
  { id: "duplicatedTitle", label: "Duplicated Title", type: "bool", filterKey: "duplicatedTitleCriterion" },
  { id: "duplicatedRemoteId", label: "Duplicated Remote ID", type: "bool", filterKey: "duplicatedRemoteIdCriterion" },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "likeCounter", label: "Likes", type: "number", filterKey: "likeCounterCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "isVr", label: "VR", type: "bool", filterKey: "isVrCriterion" },
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
  { id: "remoteId", label: "Remote ID", type: "string", filterKey: "remoteIdCriterion" },
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
  { id: "remoteId", label: "Remote ID", type: "string", filterKey: "remoteIdValueCriterion" },
  { id: "remoteIdProvider", label: "Remote ID Provider", type: "string", filterKey: "remoteIdCriterion" },
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
  { id: "remoteId", label: "Remote ID", type: "string", filterKey: "remoteIdValueCriterion" },
  { id: "remoteIdProvider", label: "Remote ID Provider", type: "string", filterKey: "remoteIdCriterion" },
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
  { id: "remoteId", label: "Remote ID", type: "string", filterKey: "remoteIdCriterion" },
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
  { id: "path", label: "Path", type: "string", filterKey: "pathCriterion" },
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
  { id: "path", label: "Path", type: "string", filterKey: "pathCriterion" },
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
  { id: "path", label: "Path", type: "string", filterKey: "pathCriterion" },
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
  { id: "path", label: "Path", type: "string", filterKey: "pathCriterion" },
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
  const activeFilterSignature = useMemo(() => JSON.stringify(activeFilter ?? {}), [activeFilter]);
  const [lastSyncedFilterSignature, setLastSyncedFilterSignature] = useState(activeFilterSignature);
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

  const filteredCriteria = useMemo(() => {
    const q = search.toLowerCase();
    const filtered = q ? criteria.filter((c) => c.label.toLowerCase().includes(q)) : criteria;
    // Sort: pinned first, then alphabetical
    return [...filtered].sort((a, b) => {
      const ap = pinnedIds.has(a.id) ? 0 : 1;
      const bp = pinnedIds.has(b.id) ? 0 : 1;
      if (ap !== bp) return ap - bp;
      return a.label.localeCompare(b.label);
    });
  }, [criteria, search, pinnedIds]);

  // Auto-expand preselected criterion when dialog opens
  useEffect(() => {
    if (open && preselectCriterion) {
      setExpandedCriterion(preselectCriterion);
    }
  }, [open, preselectCriterion]);

  useEffect(() => {
    if (open) {
      if (lastSyncedFilterSignature !== activeFilterSignature) {
        setEditFilter(JSON.parse(activeFilterSignature) as Record<string, unknown>);
        setLastSyncedFilterSignature(activeFilterSignature);
      }
      return;
    }

    if (lastSyncedFilterSignature !== activeFilterSignature) {
      setEditFilter(JSON.parse(activeFilterSignature) as Record<string, unknown>);
    }

    setLastSyncedFilterSignature(activeFilterSignature);
  }, [activeFilterSignature, lastSyncedFilterSignature, open]);

  const activeCriterionCount = useMemo(() => {
    const criteriaCount = criteria.filter((criterion) => isCriterionValueValid(getCriterionFilterValue(editFilter, criterion), criterion)).length;
    const customCount = (customSections ?? []).filter((section) => section.isActive(editFilter[section.filterKey])).length;
    return criteriaCount + customCount;
  }, [criteria, customSections, editFilter]);

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

  const handleApply = () => {
    const sectionFilter: Record<string, unknown> = {};
    for (const section of customSections ?? []) {
      const value = section.sanitize ? section.sanitize(editFilter[section.filterKey]) : editFilter[section.filterKey];
      if (section.isActive(value)) {
        sectionFilter[section.filterKey] = value;
      }
    }

    const nextFilter = sanitizeFilterCriteria(editFilter, criteria, sectionFilter);

    onApply(nextFilter);
    onClose();
  };

  const handleClear = () => {
    setEditFilter({});
  };

  const hasWideCustomFieldLayout = (customSections ?? []).some((section) => section.id === "custom-fields");

  if (!open) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-end sm:items-center justify-center bg-black/60"
      onMouseDown={(event) => {
        backdropPointerDownRef.current = event.target === event.currentTarget;
      }}
      onClick={(event) => {
        if (event.target === event.currentTarget && backdropPointerDownRef.current) {
          onClose();
        }

        backdropPointerDownRef.current = false;
      }}
    >
      <div
        className={`bg-surface border border-border sm:rounded-lg shadow-xl w-full ${hasWideCustomFieldLayout ? "sm:w-[min(92vw,56rem)] sm:max-w-none" : "sm:max-w-lg"} h-[85vh] sm:h-auto sm:max-h-[80vh] flex flex-col rounded-t-lg`}
        onClick={(e) => e.stopPropagation()}
        onMouseDown={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-border">
          <div className="flex items-center gap-2">
            <h2 className="text-sm font-semibold text-foreground">Edit Filter</h2>
            {activeCriterionCount > 0 && (
              <span className="px-1.5 py-0.5 rounded-full bg-accent text-white text-[10px] font-bold">
                {activeCriterionCount}
              </span>
            )}
          </div>
          <button onClick={onClose} className="p-1 hover:bg-card rounded text-muted hover:text-foreground">
            <X className="w-4 h-4" />
          </button>
        </div>

        {/* Search criteria */}
        <div className="px-4 py-2 border-b border-border">
          <div className="relative">
            <Search className="absolute left-2 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-muted" />
            <input
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search criteria..."
              className="w-full bg-input border border-border rounded pl-7 pr-3 py-1.5 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
            />
          </div>
        </div>

        {/* Active filter tags */}
        {activeCriterionCount > 0 && (
          <div className="px-4 py-2 border-b border-border flex flex-wrap gap-1">
            {(customSections ?? [])
              .filter((section) => section.isActive(editFilter[section.filterKey]))
              .map((section) => (
                <span
                  key={section.id}
                  className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] bg-accent/20 text-accent border border-accent/30"
                >
                  {section.label}
                  <button
                    onClick={() => setEditFilter((current) => {
                      const next = { ...current };
                      delete next[section.filterKey];
                      return next;
                    })}
                    aria-label={`Remove ${section.label} filter chip`}
                    className="hover:text-white"
                  >
                    <X className="w-3 h-3" />
                  </button>
                </span>
              ))}
            {criteria
              .filter((c) => isCriterionValueValid(getCriterionFilterValue(editFilter, c), c))
              .map((c) => (
                <span
                  key={c.id}
                  className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] bg-accent/20 text-accent border border-accent/30"
                >
                  {c.label}
                  <button
                    onClick={() => handleRemoveCriterion(c, c.id)}
                    aria-label={`Remove ${c.label} filter chip`}
                    className="hover:text-white"
                  >
                    <X className="w-3 h-3" />
                  </button>
                </span>
              ))}
          </div>
        )}

        {/* Criterion list */}
        <div className="flex-1 overflow-y-auto px-2 py-1">
          {(customSections ?? []).map((section) => {
            const value = editFilter[section.filterKey] ?? section.defaultValue;
            const isActive = section.isActive(editFilter[section.filterKey]);
            const isExpanded = expandedCriterion === section.id;

            return (
              <div key={section.id} className={`rounded mb-0.5 ${isActive ? "bg-accent/5 border border-accent/20" : ""}`}>
                <div
                  className="flex items-center gap-1 px-2 py-1.5 cursor-pointer hover:bg-card/50 rounded"
                  onClick={() => setExpandedCriterion(isExpanded ? null : section.id)}
                >
                  {isExpanded ? (
                    <ChevronDown className="w-3 h-3 text-muted flex-shrink-0" />
                  ) : (
                    <ChevronRight className="w-3 h-3 text-muted flex-shrink-0" />
                  )}
                  <span className={`text-xs flex-1 ${isActive ? "text-accent font-medium" : "text-foreground"}`}>
                    {section.label}
                  </span>
                  {isActive ? (
                    <button
                      onClick={(event) => {
                        event.stopPropagation();
                        setEditFilter((current) => {
                          const next = { ...current };
                          delete next[section.filterKey];
                          return next;
                        });
                      }}
                      aria-label={`Remove ${section.label} filter row`}
                      className="p-0.5 rounded hover:bg-red-900/20 text-muted hover:text-red-400"
                    >
                      <X className="w-3 h-3" />
                    </button>
                  ) : null}
                </div>
                {isExpanded ? (
                  <div className="px-3 pb-2">
                    {section.renderEditor(value, (nextValue) => {
                      setEditFilter((current) => {
                        const next = { ...current };
                        const shouldKeepDraft = section.shouldKeepDraft ?? section.isActive;
                        if (shouldKeepDraft(nextValue)) {
                          next[section.filterKey] = nextValue;
                        } else {
                          delete next[section.filterKey];
                        }
                        return next;
                      });
                    })}
                  </div>
                ) : null}
              </div>
            );
          })}

          {showCustomSectionDivider && customSections && customSections.length > 0 ? <div className="border-t border-border my-1" /> : null}

          {/* Pinned divider */}
          {filteredCriteria.some((c) => pinnedIds.has(c.id)) && filteredCriteria.some((c) => !pinnedIds.has(c.id)) && (
            <>
              {filteredCriteria
                .filter((c) => pinnedIds.has(c.id))
                .map((criterion) => (
                  <CriterionRow
                    key={criterion.id}
                    criterion={criterion}
                    value={getCriterionFilterValue(editFilter, criterion)}
                    auxiliaryToggleChecked={criterion.auxiliaryToggleKey ? Boolean(editFilter[criterion.auxiliaryToggleKey]) : undefined}
                    onAuxiliaryToggleChange={(checked) => handleSetAuxiliaryToggle(criterion, checked)}
                    onChange={(v) => handleSetCriterion(criterion, v)}
                    onRemove={() => handleRemoveCriterion(criterion, criterion.id)}
                    expanded={expandedCriterion === criterion.id}
                    onToggleExpand={() => setExpandedCriterion(expandedCriterion === criterion.id ? null : criterion.id)}
                    pinned
                    onTogglePin={() => togglePin(criterion.id)}
                  />
                ))}
              <div className="border-t border-border my-1" />
            </>
          )}
          {filteredCriteria
            .filter((c) => !(pinnedIds.has(c.id) && filteredCriteria.some((c2) => pinnedIds.has(c2.id)) && filteredCriteria.some((c2) => !pinnedIds.has(c2.id))))
            .map((criterion) => (
              <CriterionRow
                key={criterion.id}
                criterion={criterion}
                value={getCriterionFilterValue(editFilter, criterion)}
                auxiliaryToggleChecked={criterion.auxiliaryToggleKey ? Boolean(editFilter[criterion.auxiliaryToggleKey]) : undefined}
                onAuxiliaryToggleChange={(checked) => handleSetAuxiliaryToggle(criterion, checked)}
                onChange={(v) => handleSetCriterion(criterion, v)}
                onRemove={() => handleRemoveCriterion(criterion, criterion.id)}
                expanded={expandedCriterion === criterion.id}
                onToggleExpand={() => setExpandedCriterion(expandedCriterion === criterion.id ? null : criterion.id)}
                pinned={pinnedIds.has(criterion.id)}
                onTogglePin={() => togglePin(criterion.id)}
              />
            ))}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between px-4 py-3 border-t border-border">
          <button
            onClick={handleClear}
            className="px-3 py-1 rounded text-xs text-secondary hover:text-foreground hover:bg-card"
          >
            Clear All
          </button>
          <div className="flex items-center gap-2">
            <button onClick={onClose} className="px-3 py-1 rounded text-xs text-secondary hover:text-foreground border border-border">
              Cancel
            </button>
            <button onClick={handleApply} className="px-4 py-1 rounded text-xs font-medium bg-accent hover:bg-accent-hover text-white">
              Apply
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

// ===== Criterion Row =====

function CriterionRow({
  criterion,
  value,
  auxiliaryToggleChecked,
  onAuxiliaryToggleChange,
  onChange,
  onRemove,
  expanded,
  onToggleExpand,
  pinned,
  onTogglePin,
}: {
  criterion: CriterionDefinition;
  value: unknown;
  auxiliaryToggleChecked?: boolean;
  onAuxiliaryToggleChange?: (checked: boolean) => void;
  onChange: (v: unknown) => void;
  onRemove: () => void;
  expanded: boolean;
  onToggleExpand: () => void;
  pinned: boolean;
  onTogglePin: () => void;
}) {
  const hasDraftValue = value !== undefined;
  const isSupported = criterion.supported !== false;
  const isActive = isCriterionValueValid(value, criterion);

  return (
    <div className={`rounded mb-0.5 ${isActive ? "bg-accent/5 border border-accent/20" : ""}`}>
      <div
        className={`flex items-center gap-1 px-2 py-1.5 rounded ${isSupported ? "cursor-pointer hover:bg-card/50" : "cursor-not-allowed opacity-60"}`}
        onClick={isSupported ? onToggleExpand : undefined}
        title={isSupported ? undefined : criterion.unsupportedReason ?? "This criterion is not supported yet"}
      >
        {expanded ? (
          <ChevronDown className="w-3 h-3 text-muted flex-shrink-0" />
        ) : (
          <ChevronRight className="w-3 h-3 text-muted flex-shrink-0" />
        )}
        <span className={`text-xs flex-1 ${isActive ? "text-accent font-medium" : "text-foreground"}`}>
          {criterion.label}
        </span>
        {!isSupported && <span className="rounded border border-border px-1 py-0.5 text-[10px] uppercase tracking-wide text-muted">Unsupported</span>}
        <button
          onClick={(e) => { e.stopPropagation(); onTogglePin(); }}
          className={`p-0.5 rounded hover:bg-card ${pinned ? "text-accent" : "text-muted opacity-0 group-hover:opacity-100"}`}
          title={pinned ? "Unpin" : "Pin"}
          style={{ opacity: pinned ? 1 : undefined }}
        >
          {pinned ? <Pin className="w-3 h-3" /> : <PinOff className="w-3 h-3" />}
        </button>
        {hasDraftValue && (
          <button
            onClick={(e) => { e.stopPropagation(); onRemove(); }}
            aria-label={`Remove ${criterion.label} filter row`}
            className="p-0.5 rounded hover:bg-red-900/20 text-muted hover:text-red-400"
          >
            <X className="w-3 h-3" />
          </button>
        )}
      </div>
      {expanded && isSupported && (
        <div className="px-3 pb-2">
          <CriterionEditor
            criterion={criterion}
            value={value}
            auxiliaryToggleChecked={auxiliaryToggleChecked}
            onAuxiliaryToggleChange={onAuxiliaryToggleChange}
            onChange={onChange}
          />
        </div>
      )}
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
    <div className="flex items-center gap-2">
      <button
        onClick={() => onChange({ value: true })}
        className={`px-3 py-1 rounded text-xs border ${value?.value === true ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
      >
        True
      </button>
      <button
        onClick={() => onChange({ value: false })}
        className={`px-3 py-1 rounded text-xs border ${value?.value === false ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
      >
        False
      </button>
    </div>
  );
}

// ===== Number Editor =====

export function NumberEditor({
  value,
  onChange,
  type,
  modifiers,
  auxiliaryToggleLabel,
  auxiliaryToggleChecked,
  onAuxiliaryToggleChange,
}: {
  value?: IntCriterion;
  onChange: (v: unknown) => void;
  type: CriterionType;
  modifiers: CriterionModifier[];
  auxiliaryToggleLabel?: string;
  auxiliaryToggleChecked?: boolean;
  onAuxiliaryToggleChange?: (checked: boolean) => void;
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  const update = (patch: Partial<IntCriterion>) => {
    onChange({ modifier, ...value, ...patch });
  };

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      {!isNull && (
        <div className="flex items-center gap-2">
          {type === "duration" ? (
            <DurationInput value={value?.value ?? 0} onChange={(v) => update({ value: v })} />
          ) : type === "resolution" ? (
            <ResolutionSelect value={value?.value ?? 0} onChange={(v) => update({ value: v })} />
          ) : type === "careerLength" ? (
            <CareerLengthInput value={value?.value ?? 0} onChange={(v) => update({ value: v })} />
          ) : (
            <input
              type="number"
              value={value?.value ?? ""}
              onChange={(e) => update({ value: e.target.value === "" ? undefined : Number(e.target.value) })}
              className="w-24 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
            />
          )}
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              {type === "duration" ? (
                <DurationInput value={value?.value2 ?? 0} onChange={(v) => update({ value2: v })} />
              ) : type === "careerLength" ? (
                <CareerLengthInput value={value?.value2 ?? 0} onChange={(v) => update({ value2: v })} />
              ) : (
                <input
                  type="number"
                  value={value?.value2 ?? ""}
                  onChange={(e) => update({ value2: e.target.value === "" ? undefined : Number(e.target.value) })}
                  className="w-24 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
                />
              )}
            </>
          )}
        </div>
      )}
      {auxiliaryToggleLabel && onAuxiliaryToggleChange && (
        <label className="flex items-center gap-2 text-xs text-secondary">
          <input
            type="checkbox"
            checked={Boolean(auxiliaryToggleChecked)}
            onChange={(event) => onAuxiliaryToggleChange(event.target.checked)}
            className="h-3.5 w-3.5 rounded border-border bg-input text-accent focus:ring-accent"
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
            inputClassName="w-full rounded border border-border bg-input px-2 py-1 text-xs text-foreground outline-none focus:border-accent"
            excludeIds={excludedTagIds}
          />
        </div>
        <select
          value={unit}
          onChange={(event) => setUnit(event.target.value as TagDurationClause["unit"])}
          className="rounded border border-border bg-input px-2 py-1 text-xs text-foreground outline-none focus:border-accent"
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
  sizeClass,
}: {
  displayValue: number;
  onChangeDisplay: (v: number) => void;
  step: number;
  sizeClass?: string;
}) {
  const [hoverValue, setHoverValue] = useState<number | null>(null);
  const activeValue = hoverValue ?? displayValue;
  const cls = sizeClass ?? "h-4 w-4";

  return (
    <div className="flex items-center gap-0.5" onMouseLeave={() => setHoverValue(null)}>
      {[1, 2, 3, 4, 5].map((star) => (
        <button
          key={star}
          type="button"
          onMouseMove={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
            const segments = Math.max(1, Math.ceil(ratio / step));
            const frac = Math.min(1, Number((segments * step).toFixed(2)));
            setHoverValue(star - 1 + frac);
          }}
          onMouseLeave={() => setHoverValue(null)}
          onClick={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
            const segments = Math.max(1, Math.ceil(ratio / step));
            const frac = Math.min(1, Number((segments * step).toFixed(2)));
            const next = star - 1 + frac;
            onChangeDisplay(next === displayValue ? 0 : next);
          }}
          className="relative text-accent transition-transform hover:scale-110"
        >
          <Star className={`${cls} text-muted`} />
          <span
            className="absolute inset-y-0 left-0 overflow-hidden"
            style={{ width: `${Math.max(0, Math.min(1, activeValue - (star - 1))) * 100}%` }}
          >
            <Star className={`${cls} fill-current text-accent`} />
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
      className="w-24 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
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
        <input
          type="text"
          value={value?.value ?? ""}
          onChange={(e) => onChange({ value: e.target.value, modifier })}
          className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
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
        className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>{option.label}</option>
        ))}
      </select>
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(nextModifier) => onChange({ type: hashType, value: value?.value ?? "", modifier: nextModifier })} />
      {!isNull && (
        <input
          type="text"
          value={value?.value ?? ""}
          onChange={(event) => onChange({ type: hashType, value: event.target.value, modifier })}
          className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
          placeholder="Hash value..."
        />
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
          className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
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
      <div className="flex flex-wrap gap-1">
        {([
          ["include", "Any Of"],
          ["exclude", "None Of"],
          ["isNull", "No Value"],
          ["notNull", "Has Value"],
        ] as const).map(([mode, label]) => (
          <button
            key={mode}
            onClick={() => buildCriterion(selectedValues, mode)}
            className={`px-2 py-0.5 rounded text-[10px] border ${
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
              <label key={option.value} className="flex items-center gap-2 rounded border border-border bg-input px-2 py-1 text-xs text-foreground">
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => toggleValue(option.value)}
                  className="accent-accent h-3.5 w-3.5"
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
        <div className="flex items-center gap-2">
          <input
            type="date"
            value={value?.value ?? ""}
            onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
            className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
          />
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              <input
                type="date"
                value={value?.value2 ?? ""}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
              />
            </>
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
        <div className="flex items-center gap-2">
          <input
            type="datetime-local"
            value={value?.value ?? ensureTimestampValue(value?.value)}
            onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
            className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
          />
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              <input
                type="datetime-local"
                value={value?.value2 ?? ensureTimestampValue(value?.value2)}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
              />
            </>
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
  const trimmedSearchText = searchText.trim();

  const { data: entities } = useQuery({
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
    staleTime: 60000,
  });

  const selectedIds = useMemo(() => Array.from(new Set([...includedIds, ...excludedIds])), [excludedIds, includedIds]);
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

  const addInclude = (id: number) => {
    const nextInc = includedIds.includes(id) ? includedIds : [...includedIds, id];
    const nextExc = excludedIds.filter((i) => i !== id);
    onChange(buildCriterion(nextInc, nextExc, modifier, includeHierarchy));
  };

  const addExclude = (id: number) => {
    if (!supportsExclude) {
      return;
    }

    const nextInc = includedIds.filter((i) => i !== id);
    const nextExc = excludedIds.includes(id) ? excludedIds : [...excludedIds, id];
    onChange(buildCriterion(nextInc, nextExc, modifier, includeHierarchy));
  };

  const removeId = (id: number) => {
    const nextInc = includedIds.filter((i) => i !== id);
    const nextExc = excludedIds.filter((i) => i !== id);
    onChange(buildCriterion(nextInc, nextExc, modifier, includeHierarchy));
  };

  const labels = getMultiIdEntityLabels(entityType);
  const getName = (e: any) => e.name || e.title || e.label || e.performerName || labels.singular;
  const getSelectedName = (id: number, entity?: any) => {
    if (entity) return getName(entity);
    const hydratedName = nameMap[String(id)];
    if (hydratedName) return hydratedName;
    const missingIndex = missingSelectedIds.indexOf(id);
    if (missingIndex >= 0 && selectedEntityQueries[missingIndex]?.isError) return `Unavailable ${labels.singular}`;
    return `Loading ${labels.singular}...`;
  };

  return (
    <div className="space-y-2">
      {/* Include/Exclude mode toggle */}
      <div className="flex flex-wrap gap-1">
        {(includeModifiers.length > 0 ? includeModifiers : (["INCLUDES"] as CriterionModifier[])).map((m) => (
          <button
            key={m}
            onClick={() => onChange(buildCriterion(includedIds, excludedIds, m, includeHierarchy))}
            className={`px-2 py-0.5 rounded text-[10px] border ${
              m === modifier
                ? "bg-accent text-white border-accent"
                : "border-border text-secondary hover:text-foreground hover:border-accent/50"
            }`}
          >
            {MODIFIER_LABELS[m]}
          </button>
        ))}
        {nullModifiers.map((m) => (
          <button
            key={m}
            onClick={() => onChange({ modifier: m })}
            className={`px-2 py-0.5 rounded text-[10px] border ${
              m === modifier
                ? "bg-accent text-white border-accent"
                : "border-border text-secondary hover:text-foreground hover:border-accent/50"
            }`}
          >
            {MODIFIER_LABELS[m]}
          </button>
        ))}
      </div>
      {isNullModifier ? (
        <div className="rounded border border-border/70 bg-input px-2 py-2 text-xs text-muted">
          This criterion will match entities with {modifier === "IS_NULL" ? "no" : "at least one"} linked {entityType} item.
        </div>
      ) : (
        <>
      {/* Sub-tag checkbox (only for tags) */}
      {(entityType === "tags" || hierarchyToggleLabel) && (
        <label className="flex items-center gap-1.5 text-xs text-secondary cursor-pointer select-none">
          <input
            type="checkbox"
            checked={includeHierarchy}
            onChange={(e) => {
              onChange(buildCriterion(includedIds, excludedIds, modifier, e.target.checked));
            }}
            className="accent-accent w-3.5 h-3.5"
          />
          {hierarchyToggleLabel ?? "Include sub-tags (child tags)"}
        </label>
      )}
      {/* Selected items: included */}
      {includedIds.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {includedIds.map((id) => {
            const entity = entities?.find((e: any) => e.id === id);
            return (
              <span key={id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] bg-green-900/50 text-green-300 border border-green-700">
                {getSelectedName(id, entity)}
                <button onClick={() => removeId(id)} className="hover:text-red-400">
                  <X className="w-2.5 h-2.5" />
                </button>
              </span>
            );
          })}
        </div>
      )}
      {/* Selected items: excluded */}
      {supportsExclude && excludedIds.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {excludedIds.map((id) => {
            const entity = entities?.find((e: any) => e.id === id);
            return (
              <span key={id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] bg-red-900/50 text-red-300 border border-red-700">
                {getSelectedName(id, entity)}
                <button onClick={() => removeId(id)} className="hover:text-red-400">
                  <X className="w-2.5 h-2.5" />
                </button>
              </span>
            );
          })}
        </div>
      )}
      {/* Search + add */}
      <div className="relative">
        <input
          type="text"
          value={searchText}
          onChange={(e) => setSearchText(e.target.value)}
          placeholder={`Search ${entityType}...`}
          className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
        />
      </div>
      <div className="max-h-32 overflow-y-auto border border-border rounded bg-input">
        {entityType === "tags" ? (
          <GroupedTagOptionList
            tags={filteredEntities as any[]}
            maxItems={50}
            className="border-0 bg-transparent"
            preserveOrder={Boolean(searchText.trim())}
            renderTag={(entity: any) => {
              const isIncluded = includedIds.includes(entity.id);
              const isExcluded = excludedIds.includes(entity.id);
              return (
                <div className={`w-full px-2 py-1 text-xs flex items-center gap-1 ${isIncluded ? "text-green-300" : isExcluded ? "text-red-300" : "text-foreground"}`}>
                  <button
                    onClick={() => isIncluded ? removeId(entity.id) : addInclude(entity.id)}
                    className={`hover:text-green-400 ${isIncluded ? "text-green-400" : "text-muted"}`}
                    title="Include"
                  >
                    <Plus className="w-3 h-3" />
                  </button>
                  {supportsExclude && (
                    <button
                      onClick={() => isExcluded ? removeId(entity.id) : addExclude(entity.id)}
                      className={`hover:text-red-400 ${isExcluded ? "text-red-400" : "text-muted"}`}
                      title="Exclude"
                    >
                      <Minus className="w-3 h-3" />
                    </button>
                  )}
                  <span className="min-w-0 flex-1 truncate">{getName(entity)}</span>
                </div>
              );
            }}
          />
        ) : filteredEntities.slice(0, 50).map((entity: any) => {
          const isIncluded = includedIds.includes(entity.id);
          const isExcluded = excludedIds.includes(entity.id);
          return (
            <div
              key={entity.id}
              className={`w-full px-2 py-1 text-xs flex items-center gap-1 ${isIncluded ? "text-green-300" : isExcluded ? "text-red-300" : "text-foreground"}`}
            >
              <button
                onClick={() => isIncluded ? removeId(entity.id) : addInclude(entity.id)}
                className={`hover:text-green-400 ${isIncluded ? "text-green-400" : "text-muted"}`}
                title="Include"
              >
                <Plus className="w-3 h-3" />
              </button>
              {supportsExclude && (
                <button
                  onClick={() => isExcluded ? removeId(entity.id) : addExclude(entity.id)}
                  className={`hover:text-red-400 ${isExcluded ? "text-red-400" : "text-muted"}`}
                  title="Exclude"
                >
                  <Minus className="w-3 h-3" />
                </button>
              )}
              <span className="flex-1">{getName(entity)}</span>
            </div>
          );
        })}
        {filteredEntities.length === 0 && (
          <div className="px-2 py-2 text-xs text-muted text-center">No results</div>
        )}
      </div>
        </>
      )}
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
  return (
    <div className="flex flex-wrap gap-1">
      {modifiers.map((m) => (
        <button
          key={m}
          onClick={() => onSelect(m)}
          className={`px-2 py-0.5 rounded text-[10px] border ${
            m === selected
              ? "bg-accent text-white border-accent"
              : "border-border text-secondary hover:text-foreground hover:border-accent/50"
          }`}
        >
          {MODIFIER_LABELS[m]}
        </button>
      ))}
    </div>
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

function DurationInput({ value, onChange, ariaLabel }: { value?: number; onChange: (v: number | undefined) => void; ariaLabel?: string }) {
  const [inputText, setInputText] = useState(() => formatDurationInputValue(value));

  useEffect(() => {
    setInputText(formatDurationInputValue(value));
  }, [value]);

  const parse = (str: string) => {
    const trimmed = str.trim();
    if (trimmed === "") return undefined;
    const parts = trimmed.split(":").map(Number);
    if (parts.some((part) => !Number.isFinite(part))) return undefined;
    if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
    if (parts.length === 2) return parts[0] * 60 + parts[1];
    return parts[0];
  };

  const commit = (rawValue: string) => {
    const parsed = parse(rawValue);
    setInputText(formatDurationInputValue(parsed));
    onChange(parsed);
  };

  return (
    <input
      type="text"
      value={inputText}
      onChange={(event) => setInputText(event.target.value)}
      onBlur={(event) => commit(event.target.value)}
      placeholder="H:MM:SS"
      aria-label={ariaLabel}
      className="w-24 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
    />
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
        className="w-full rounded border border-border bg-input px-2 py-1 pr-6 text-xs text-foreground outline-none focus:border-accent"
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
        className="w-20 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
      />
      <select
        value={unit}
        onChange={(e) => setUnit(e.target.value as "years" | "months")}
        aria-label="Career length unit"
        className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
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
      className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
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
      onClick={onClick}
      className={`flex items-center gap-1 px-2 py-1 rounded text-xs border ${
        activeCount > 0
          ? "border-accent bg-accent/10 text-accent"
          : "border-border bg-card/70 text-secondary hover:border-accent hover:text-foreground"
      }`}
    >
      <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
        <path strokeLinecap="round" strokeLinejoin="round" d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z" />
      </svg>
      Filter
      {activeCount > 0 && (
        <span className="px-1 py-0 rounded-full bg-accent text-white text-[10px] font-bold min-w-[16px] text-center">
          {activeCount}
        </span>
      )}
    </button>
  );
}
