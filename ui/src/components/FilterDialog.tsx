import { useState, useMemo, useCallback, useEffect, useId, useRef, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from "react";
import { useQueries, useQuery } from "@tanstack/react-query";
import { X, Search, Pin, PinOff, Plus, Minus, Star, ArrowLeft, Film, Users, MoreHorizontal, Workflow } from "lucide-react";
import { tags as tagsApi, performers as performersApi, studios as studiosApi, groups as groupsApi, galleries as galleriesApi, videos as videosApi, tagGroups as tagGroupsApi, faces as facesApi, metadata, savedFilters as savedFiltersApi } from "../api/client";
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
  RelatedFilterCriterion,
  FilterExpression,
  RatingSystemOptions,
} from "../api/types";
import { RESOLUTION_FILTER_OPTIONS } from "../utils/resolutionBuckets";
import { rankByLabel } from "../utils/searchRanking";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import {
  ActiveObjectFilterChips,
  formatFilterChipValue,
  formatRemoteIdFilterChipValue,
  getFilterChipTargetKey,
  removeObjectFilterChipTarget,
  type FilterChipTarget,
  type RelatedFilterChipFacet,
} from "./ActiveObjectFilterChips";
import { ConfirmDialog } from "./ConfirmDialog";
import { getMultiIdModifierLabel } from "../utils/filterModifierLabels";
import { pushOverlay } from "../utils/overlayState";
import { LibraryFolderTree } from "./LibraryFolderTree";

// ===== Criterion definitions =====

export type CriterionType = "string" | "path" | "remoteId" | "number" | "bool" | "date" | "timestamp" | "duration" | "tagDuration" | "careerLength" | "rating" | "resolution" | "multiId" | "enum" | "hash" | "related";
export type EntityType = "tags" | "tagGroups" | "performers" | "studios" | "groups" | "galleries" | "videos" | "faces";

export interface CriterionDefinition<TFilterKey extends string = string> {
  id: string;
  label: string;
  type: CriterionType;
  entityType?: EntityType;
  filterKey: TFilterKey;
  category?: "related";
  /** Lazily resolves the criteria available inside a related-entity workspace. */
  relatedCriteria?: () => CriterionDefinition[];
  /** Criteria evaluated against the relationship host rather than the related entity itself. */
  relatedContextCriteria?: CriterionDefinition[];
  customFieldKey?: string;
  customFieldType?: string;
  modifiers?: CriterionModifier[];
  expressionSupported?: boolean;
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
  related: [],
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
    case "related":
      return sanitizeRelatedFilterCriterion(value, criterion) !== undefined;
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
    } else if (criterion.type === "related") {
      const related = sanitizeRelatedFilterCriterion(value, criterion);
      if (related) sanitized[criterion.filterKey] = related;
    } else {
      sanitized[criterion.filterKey] = value;
    }

    if (criterion.auxiliaryToggleKey && typeof filter[criterion.auxiliaryToggleKey] === "boolean") {
      sanitized[criterion.auxiliaryToggleKey] = filter[criterion.auxiliaryToggleKey];
    }
  }

  return sanitized;
}

export const FILTER_EXPRESSION_STATE_KEY = "_filterExpression";

function sanitizeFilterExpression(expression: FilterExpression<Record<string, unknown>> | undefined, criteria: CriterionDefinition[]): FilterExpression<Record<string, unknown>> | undefined {
  if (!expression) return undefined;
  const children: FilterExpression<Record<string, unknown>>["children"] = [];
  for (const child of expression.children) {
    if (child.group) {
      const group = sanitizeFilterExpression(child.group, criteria);
      if (group && group.children.length > 0) children.push({ group });
      continue;
    }
    if (!child.filter) continue;
    const filter = sanitizeFilterCriteria(child.filter, criteria);
    if (Object.keys(filter).length > 0) children.push({ filter });
  }
  const operator = expression.operator === "OR" ? "OR" : expression.operator === "NOT" ? "NOT" : "AND";
  if (operator === "NOT" && children.length !== 1) return undefined;
  return children.length > 0 ? { operator, children } : undefined;
}

function filterToExpression(filter: Record<string, unknown>, criteria: CriterionDefinition[]): FilterExpression<Record<string, unknown>> {
  const children: FilterExpression<Record<string, unknown>>["children"] = [];
  const consumed = new Set<string>([FILTER_EXPRESSION_STATE_KEY]);
  for (const criterion of criteria) {
    if (criterion.expressionSupported === false) continue;
    const value = getCriterionFilterValue(filter, criterion);
    if (!isCriterionValueValid(value, criterion)) continue;
    const leaf = setCriterionFilterValue({}, criterion, value);
    if (criterion.auxiliaryToggleKey && typeof filter[criterion.auxiliaryToggleKey] === "boolean") leaf[criterion.auxiliaryToggleKey] = filter[criterion.auxiliaryToggleKey];
    children.push({ filter: leaf });
    consumed.add(criterion.filterKey);
    if (criterion.secondaryFilterKey) consumed.add(criterion.secondaryFilterKey);
    if (criterion.auxiliaryToggleKey) consumed.add(criterion.auxiliaryToggleKey);
  }
  return { operator: "AND", children };
}

function expressionPassthroughFilter(filter: Record<string, unknown>, criteria: CriterionDefinition[]) {
  const expressionKeys = new Set(criteria.filter((criterion) => criterion.expressionSupported !== false).flatMap((criterion) => [criterion.filterKey, criterion.secondaryFilterKey, criterion.auxiliaryToggleKey].filter((key): key is string => Boolean(key))));
  return Object.fromEntries(Object.entries(filter).filter(([key]) => key !== FILTER_EXPRESSION_STATE_KEY && !expressionKeys.has(key)));
}

function countFilterExpressionConditions(expression: FilterExpression<Record<string, unknown>> | undefined): number {
  if (!expression) return 0;
  return expression.children.reduce((count, child) => count + (child.group ? countFilterExpressionConditions(child.group) : child.filter ? 1 : 0), 0);
}

export function isComplexFilterExpression(expression: FilterExpression<Record<string, unknown>> | undefined): boolean {
  return Boolean(expression && (expression.operator !== "AND" || expression.children.some((child) => child.group)));
}

function mergeFilterExpressionWithSimpleCriteria(
  filter: Record<string, unknown>,
  criteria: CriterionDefinition[],
): FilterExpression<Record<string, unknown>> | undefined {
  const expression = filter[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
  const simpleExpression = filterToExpression(filter, criteria);
  if (!expression) return simpleExpression.children.length > 0 ? simpleExpression : undefined;
  if (simpleExpression.children.length === 0) return expression;
  return expression.operator === "AND"
    ? { ...expression, children: [...expression.children, ...simpleExpression.children] }
    : { operator: "AND", children: [{ group: expression }, ...simpleExpression.children] };
}

function replaceExpressionGroup(
  root: FilterExpression<Record<string, unknown>>,
  groupPath: number[],
  update: (group: FilterExpression<Record<string, unknown>>) => FilterExpression<Record<string, unknown>>,
): FilterExpression<Record<string, unknown>> {
  if (groupPath.length === 0) return update(root);
  const [index, ...rest] = groupPath;
  const child = root.children[index];
  if (!child?.group) return root;
  const children = root.children.slice();
  children[index] = { group: replaceExpressionGroup(child.group, rest, update) };
  return { ...root, children };
}

function removeExpressionGroup(
  root: FilterExpression<Record<string, unknown>>,
  groupPath: number[],
): FilterExpression<Record<string, unknown>> | undefined {
  if (groupPath.length === 0) return undefined;
  const parentPath = groupPath.slice(0, -1);
  const groupIndex = groupPath.at(-1);
  if (groupIndex === undefined) return root;
  return replaceExpressionGroup(root, parentPath, (parent) => ({
    ...parent,
    children: parent.children.filter((_, index) => index !== groupIndex),
  }));
}

function getExpressionLeaf(
  root: FilterExpression<Record<string, unknown>>,
  path: number[],
): Record<string, unknown> | undefined {
  if (path.length === 0) return undefined;
  const [index, ...rest] = path;
  const child = root.children[index];
  if (!child) return undefined;
  if (rest.length === 0) return child.filter;
  return child.group ? getExpressionLeaf(child.group, rest) : undefined;
}

function getExpressionGroup(
  root: FilterExpression<Record<string, unknown>>,
  path: number[],
): FilterExpression<Record<string, unknown>> | undefined {
  if (path.length === 0) return root;
  const [index, ...rest] = path;
  const group = root.children[index]?.group;
  return group ? getExpressionGroup(group, rest) : undefined;
}

function getExpressionConditionCriterion(
  filter: Record<string, unknown>,
  criteria: CriterionDefinition[],
): CriterionDefinition | undefined {
  const selectedId = typeof filter._criterionId === "string" ? filter._criterionId : undefined;
  return criteria.find((criterion) => criterion.id === selectedId
    || getCriterionFilterValue(filter, criterion) !== undefined);
}

function updateExpressionLeaf(
  root: FilterExpression<Record<string, unknown>>,
  path: number[],
  filter: Record<string, unknown>,
): FilterExpression<Record<string, unknown>> {
  const parentPath = path.slice(0, -1);
  const index = path.at(-1);
  if (index === undefined) return root;
  return replaceExpressionGroup(root, parentPath, (group) => {
    const children = group.children.slice();
    children[index] = { filter };
    return { ...group, children };
  });
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
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
  { id: "likeCounter", label: "Likes", type: "number", filterKey: "likeCounterCriterion" },
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
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
  { id: "relatedPerformers", label: "Related Performers", type: "related", entityType: "performers", filterKey: "performerFilterCriterion", category: "related", relatedCriteria: () => getRelatedCriteria("performers"), relatedContextCriteria: [
    { id: "ageAtVideoDate", label: "Age (then)", type: "number", filterKey: "ageAtHostDateCriterion" },
  ] },
  { id: "resumeTime", label: "Resume Time", type: "number", filterKey: "resumeTimeCriterion" },
  { id: "playDuration", label: "Play Duration", type: "duration", filterKey: "playDurationCriterion" },
  { id: "lastPlayedAt", label: "Last Played", type: "timestamp", filterKey: "lastPlayedAtCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
  { id: "performerTags", label: "Performer Occurrence Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
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
  { id: "relatedVideos", label: "Related Videos", type: "related", entityType: "videos", filterKey: "videoFilterCriterion", category: "related", relatedCriteria: () => getRelatedCriteria("videos") },
  { id: "age", label: "Age (now)", type: "number", filterKey: "ageCriterion" },
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
  { id: "audioCount", label: "Audio Count", type: "number", filterKey: "audioCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "textCount", label: "Text Count", type: "number", filterKey: "textCountCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
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
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "videos", label: "Videos", type: "multiId", entityType: "videos", filterKey: "videosCriterion" },
  { id: "performerTags", label: "Performer Occurrence Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "relatedPerformers", label: "Related Performers", type: "related", entityType: "performers", filterKey: "performerFilterCriterion", category: "related", relatedCriteria: () => getRelatedCriteria("performers") },
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
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "likeCounter", label: "Likes", type: "number", filterKey: "likeCounterCriterion", modifiers: NON_NULL_NUMBER_MODIFIERS },
  { id: "resolution", label: "Resolution", type: "resolution", filterKey: "resolutionCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "galleries", label: "Galleries", type: "multiId", entityType: "galleries", filterKey: "galleriesCriterion" },
  { id: "performerTags", label: "Performer Occurrence Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "relatedPerformers", label: "Related Performers", type: "related", entityType: "performers", filterKey: "performerFilterCriterion", category: "related", relatedCriteria: () => getRelatedCriteria("performers") },
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
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
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
  { id: "performerTags", label: "Performer Occurrence Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "relatedPerformers", label: "Related Performers", type: "related", entityType: "performers", filterKey: "performerFilterCriterion", category: "related", relatedCriteria: () => getRelatedCriteria("performers") },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion", hierarchyToggleLabel: "Include sub-studios" },
  { id: "groups", label: "Groups", type: "multiId", entityType: "groups", filterKey: "groupsCriterion" },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

export const TEXT_CRITERIA: CriteriaDefinitionList<TextFilterCriteria> = [
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
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
  { id: "performerTags", label: "Performer Occurrence Tags", type: "multiId", entityType: "tags", filterKey: "performerTagsCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "relatedPerformers", label: "Related Performers", type: "related", entityType: "performers", filterKey: "performerFilterCriterion", category: "related", relatedCriteria: () => getRelatedCriteria("performers") },
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
  { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
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

function getRelatedCriteria(entityType: EntityType | undefined): CriterionDefinition[] {
  const criteria = entityType === "performers"
    ? PERFORMER_CRITERIA
    : entityType === "videos"
    ? VIDEO_CRITERIA
    : [];
  return criteria.filter((criterion) => criterion.type !== "related");
}

function hasMeaningfulRelatedValue(value: unknown): boolean {
  if (value == null || value === "") return false;
  if (Array.isArray(value)) return value.length > 0;
  if (typeof value === "object") return Object.keys(value as Record<string, unknown>).length > 0;
  return true;
}

function sanitizeRelatedFilterCriterion(value: unknown, criterion: CriterionDefinition): RelatedFilterCriterion | undefined {
  if (!value || typeof value !== "object") return undefined;
  const raw = value as RelatedFilterCriterion;
  const nestedCriteria = getRelatedCriteria(criterion.entityType);
  const rawObjectFilter = raw.objectFilter && typeof raw.objectFilter === "object"
    ? raw.objectFilter as Record<string, unknown>
    : {};
  const knownKeys = new Set(nestedCriteria.flatMap((item) => [item.filterKey, item.secondaryFilterKey, item.auxiliaryToggleKey].filter(Boolean) as string[]));
  const unknownValues = Object.fromEntries(Object.entries(rawObjectFilter).filter(([key, item]) =>
    !knownKeys.has(key)
    && key !== "performerFilterCriterion"
    && key !== "videoFilterCriterion"
    && !key.startsWith("_")
    && hasMeaningfulRelatedValue(item)));
  const objectFilter = sanitizeFilterCriteria(rawObjectFilter, nestedCriteria, unknownValues);
  const q = raw.findFilter?.q?.trim();
  const matchAll = raw._matchAll === true;
  const mode = raw.mode === "every" || raw.mode === "none" ? raw.mode : undefined;
  const conditionOperator = raw.conditionOperator === "or" ? "or" : undefined;
  const contextValues = (criterion.relatedContextCriteria ?? []).reduce<Record<string, unknown>>((result, contextCriterion) => {
    const contextValue = (raw as Record<string, unknown>)[contextCriterion.filterKey];
    if (isCriterionValueValid(contextValue, contextCriterion)) result[contextCriterion.filterKey] = contextValue;
    return result;
  }, {});
  if (!q && Object.keys(objectFilter).length === 0 && !matchAll && Object.keys(contextValues).length === 0) return undefined;

  return {
    ...(q ? { findFilter: { q } } : {}),
    ...(Object.keys(objectFilter).length > 0 ? { objectFilter } : {}),
    ...(mode ? { mode } : {}),
    ...(conditionOperator ? { conditionOperator } : {}),
    ...(raw.exclude ? { exclude: true } : {}),
    ...(raw._savedFilterName?.trim() ? { _savedFilterName: raw._savedFilterName.trim() } : {}),
    ...(matchAll ? { _matchAll: true } : {}),
    ...contextValues,
  };
}

export function migrateLegacyPerformerFavoriteCriterion(
  filter: Record<string, unknown>,
  criteria: CriterionDefinition[],
): Record<string, unknown> {
  const supportsRelatedPerformers = criteria.some((criterion) => criterion.filterKey === "performerFilterCriterion");
  const legacy = filter.performerFavoriteCriterion;
  if (!supportsRelatedPerformers || !legacy || typeof legacy !== "object" || typeof (legacy as { value?: unknown }).value !== "boolean") {
    return filter;
  }

  const next = { ...filter };
  delete next.performerFavoriteCriterion;
  if (next.performerFilterCriterion !== undefined) return next;

  const requiresFavorite = (legacy as { value: boolean }).value;
  next.performerFilterCriterion = {
    objectFilter: { favoriteCriterion: { value: true } },
    ...(!requiresFavorite ? { exclude: true } : {}),
  } satisfies RelatedFilterCriterion;
  return next;
}

// ===== Filter Dialog =====

export type FilterDialogPreselection = string | {
  criterionId: string;
  relatedFacet?: RelatedFilterChipFacet;
  nestedCriterionId?: string;
};

interface FilterDialogProps {
  open: boolean;
  onClose: () => void;
  criteria: CriterionDefinition[];
  activeFilter: Record<string, unknown>;
  onApply: (filter: Record<string, unknown>) => void;
  preselectCriterion?: FilterDialogPreselection;
  customSections?: FilterDialogCustomSection[];
  showCustomSectionDivider?: boolean;
  supportsFilterExpressions?: boolean;
  initialView?: "simple" | "advanced";
  initialExpressionPath?: number[];
  subjectLabel?: string;
  openAtRoot?: boolean;
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

function getFirstEditorControl(panel: HTMLElement | null | undefined): HTMLElement | null {
  return panel?.querySelector<HTMLElement>("[data-filter-primary-control]")
    ?? panel?.querySelector<HTMLElement>("input:not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]):not([data-mobile-only-control])")
    ?? panel?.querySelector<HTMLElement>("button:not([disabled])")
    ?? null;
}

function getFirstInlineEditorControl(panel: HTMLElement | null | undefined): HTMLElement | null {
  return panel?.querySelector<HTMLElement>("button[data-modifier][aria-pressed='true'], button[data-match-modifier][aria-pressed='true']")
    ?? getFirstEditorControl(panel);
}

type FilterDialogView = "simple" | "expression";

interface ExpressionConditionDraft {
  filter: Record<string, unknown>;
  path?: number[];
  parentPath: number[];
  isNew: boolean;
  returnView: "simple" | "expression";
  implicitRootAnd?: boolean;
}

export function FilterDialog({ open, onClose, criteria, activeFilter, onApply, preselectCriterion, customSections, showCustomSectionDivider = true, supportsFilterExpressions = false, initialView = "simple", initialExpressionPath, subjectLabel = "items", openAtRoot = false }: FilterDialogProps) {
  const supportsExpressions = supportsFilterExpressions;
  const [editFilter, setEditFilter] = useState<Record<string, unknown>>({ ...activeFilter });
  const [dialogView, setDialogView] = useState<FilterDialogView>(() => initialView === "advanced" && isComplexFilterExpression(activeFilter[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined) ? "expression" : "simple");
  const [conditionDraft, setConditionDraft] = useState<ExpressionConditionDraft | null>(null);
  const [simpleExpressionGroupPath, setSimpleExpressionGroupPath] = useState<number[]>(() => initialExpressionPath?.slice(0, -1) ?? []);
  const [inlineStackReturnsToExpression, setInlineStackReturnsToExpression] = useState(false);
  const backdropPointerDownRef = useRef(false);
  const [search, setSearch] = useState("");
  const [expandedCriterion, setExpandedCriterion] = useState<string | null>(null);
  const [relatedWorkspaceSelection, setRelatedWorkspaceSelection] = useState<{ facet: RelatedFilterChipFacet; nestedCriterionId?: string } | null>(null);
  const [navigatorFocusId, setNavigatorFocusId] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const selectedFiltersToolbarRef = useRef<HTMLDivElement>(null);
  const selectedFiltersLastFocusedRef = useRef<HTMLButtonElement | null>(null);
  const selectedFiltersLastFocusedIndexRef = useRef(0);
  const selectedFiltersInstructionsId = useId();
  const previousFocusRef = useRef<HTMLElement | null>(null);
  const viewReturnFocusRef = useRef<HTMLElement | null>(null);
  const simpleReturnFocusKeyRef = useRef<string | null>(null);
  const expressionReturnFocusKeyRef = useRef<string | null>(null);
  const backButtonRef = useRef<HTMLButtonElement>(null);
  const criterionButtonRefs = useRef(new Map<string, HTMLButtonElement>());
  const pinButtonRefs = useRef(new Map<string, HTMLButtonElement>());
  const pendingPinFocusRef = useRef<string | null>(null);
  const wasOpenRef = useRef(false);
  const activeFilterSignature = useMemo(() => JSON.stringify(activeFilter ?? {}), [activeFilter]);
  const normalizedActiveFilter = useMemo(
    () => migrateLegacyPerformerFavoriteCriterion(JSON.parse(activeFilterSignature) as Record<string, unknown>, criteria),
    [activeFilterSignature, criteria],
  );
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
      .filter((criterion) => !conditionDraft || (criterion.supported !== false && criterion.expressionSupported !== false))
      .slice()
      .sort((a, b) => {
        const aExact = Boolean(q) && a.label.toLowerCase() === q;
        const bExact = Boolean(q) && b.label.toLowerCase() === q;
        if (aExact !== bExact) return aExact ? -1 : 1;
        return a.label.localeCompare(b.label);
      });
  }, [conditionDraft, criteria, search]);

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
  const expression = editFilter[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
  const expressionConditionCount = countFilterExpressionConditions(expression);
  const hasComplexExpression = isComplexFilterExpression(expression);
  const simpleExpressionChildren = expression && !hasComplexExpression ? expression.children : [];
  const directlyEditableExpressionGroup = expression ? getExpressionGroup(expression, simpleExpressionGroupPath) : undefined;
  const directlyEditableExpressionChildren = directlyEditableExpressionGroup?.children ?? [];
  const validSimpleExpressionEntries = simpleExpressionChildren.flatMap((child, index) => child.filter
    && Object.keys(sanitizeFilterCriteria(child.filter, criteria)).length > 0 ? [{ child, index }] : []);
  const displayedActiveCount = activeCriterionCount + (hasComplexExpression ? 1 : validSimpleExpressionEntries.length);

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
      active: isCriterionValueValid(getCriterionFilterValue(editFilter, criterion), criterion)
        || directlyEditableExpressionChildren.some((child) => child.filter && getExpressionConditionCriterion(child.filter, criteria)?.id === criterion.id
          && isCriterionValueValid(getCriterionFilterValue(child.filter, criterion), criterion)),
      pinned: pinnedIds.has(criterion.id),
      criterion,
    }));
    const customItems: NavigatorItem[] = (conditionDraft ? [] : customSections ?? [])
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
    const related = items.filter((item) => !item.active && !item.pinned && item.kind === "criterion" && item.criterion.category === "related").sort(sortItems);
    const remaining = items.filter((item) => !item.active && !item.pinned && !(item.kind === "criterion" && item.criterion.category === "related")).sort(sortItems);
    return [
      { label: "Active", items: active },
      { label: "Pinned", items: pinned },
      { label: "Related items", items: related },
      { label: "All filters", items: remaining },
    ].filter((group) => group.items.length > 0);
  }, [criteria, customSections, directlyEditableExpressionChildren, editFilter, filteredCriteria, pinnedIds, search]);

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
      active: isCriterionValueValid(getCriterionFilterValue(editFilter, criterion), criterion)
        || directlyEditableExpressionChildren.some((child) => child.filter && getExpressionConditionCriterion(child.filter, criteria)?.id === criterion.id
          && isCriterionValueValid(getCriterionFilterValue(child.filter, criterion), criterion)),
      pinned: pinnedIds.has(criterion.id),
      criterion,
    } : undefined;
  }, [criteria, customSections, directlyEditableExpressionChildren, editFilter, expandedCriterion, pinnedIds]);
  const relatedWorkspaceCriterion = selectedItem?.kind === "criterion" && selectedItem.criterion.type === "related"
    ? selectedItem.criterion
    : undefined;
  const relatedExpressionInstances = relatedWorkspaceCriterion ? directlyEditableExpressionChildren.flatMap((child, index) =>
    child.filter && getExpressionConditionCriterion(child.filter, criteria)?.id === relatedWorkspaceCriterion.id
      ? [{ index, path: [...simpleExpressionGroupPath, index], filter: child.filter }]
      : []) : [];
  const relatedWorkspaceObjectFilter = conditionDraft && relatedWorkspaceCriterion
    ? conditionDraft.filter
    : relatedExpressionInstances.length === 1 ? relatedExpressionInstances[0].filter : editFilter;
  const selectedCompactCriterion = selectedItem?.kind === "criterion" && selectedItem.criterion.type !== "related"
    ? selectedItem.criterion
    : undefined;
  const selectedExpressionInstances = selectedCompactCriterion ? directlyEditableExpressionChildren.flatMap((child, index) =>
    child.filter && getExpressionConditionCriterion(child.filter, criteria)?.id === selectedCompactCriterion.id
      ? [{ index, path: [...simpleExpressionGroupPath, index], filter: child.filter }]
      : []) : [];
  const selectedSingleExpressionInstance = selectedExpressionInstances.length === 1 ? selectedExpressionInstances[0] : undefined;
  const showInlineConditionStack = Boolean(!conditionDraft && selectedCompactCriterion && selectedExpressionInstances.length > 1);

  const cloneActiveFilter = useCallback(
    () => JSON.parse(JSON.stringify(normalizedActiveFilter)) as Record<string, unknown>,
    [normalizedActiveFilter],
  );

  const focusFirstEditorControl = useCallback(() => {
    window.setTimeout(() => {
      const panel = dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
      getFirstEditorControl(panel)?.focus();
    }, 0);
  }, []);

  const focusFirstConditionEditorControl = useCallback(() => {
    window.setTimeout(() => window.setTimeout(() => {
      const panel = dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
      getFirstInlineEditorControl(panel)?.focus();
    }, 0), 0);
  }, []);

  const selectNavigatorItem = useCallback((id: string) => {
    setRelatedWorkspaceSelection(null);
    setExpandedCriterion(id);
    setConditionDraft((current) => current
      ? getExpressionConditionCriterion(current.filter, criteria)?.id === id
        ? current
        : { ...current, filter: { _criterionId: id } }
      : current);
    if (conditionDraft) focusFirstConditionEditorControl();
    else focusFirstEditorControl();
  }, [conditionDraft, criteria, focusFirstConditionEditorControl, focusFirstEditorControl]);

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
        setRelatedWorkspaceSelection(null);
        setDialogView("simple");
        setConditionDraft(null);
        setSimpleExpressionGroupPath([]);
        setInlineStackReturnsToExpression(false);
        setNavigatorFocusId(null);
        previousFocusRef.current?.focus();
      }
      wasOpenRef.current = false;
      return;
    }

    if (!wasOpenRef.current) {
      previousFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      const openingFilter = cloneActiveFilter();
      const openingExpression = normalizedActiveFilter[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
      const openingLeaf = initialExpressionPath && openingExpression ? getExpressionLeaf(openingExpression, initialExpressionPath) : undefined;
      const openingLeafCriterion = openingLeaf ? getExpressionConditionCriterion(openingLeaf, criteria) : undefined;
      const openLeafInline = Boolean(openingLeafCriterion && openingLeafCriterion.type !== "related" && openingExpression);
      const openingView = openingLeaf ? "simple" : initialView === "advanced" && isComplexFilterExpression(openingExpression) ? "expression" : "simple";
      if (openingView === "expression") {
        const merged = mergeFilterExpressionWithSimpleCriteria(openingFilter, criteria);
        setEditFilter({ ...expressionPassthroughFilter(openingFilter, criteria), ...(merged ? { [FILTER_EXPRESSION_STATE_KEY]: merged } : {}) });
      } else {
        setEditFilter(openingFilter);
      }
      setDialogView(openingView);
      setSimpleExpressionGroupPath(initialExpressionPath?.slice(0, -1) ?? []);
      setConditionDraft(openingLeaf && !openLeafInline ? { filter: openingLeaf, path: initialExpressionPath, parentPath: initialExpressionPath!.slice(0, -1), isNew: false, returnView: "simple" } : null);
      setInlineStackReturnsToExpression(false);
      setSearch("");
      setNavigatorFocusId(null);
      const firstActive = criteria.find((criterion) => isCriterionValueValid(getCriterionFilterValue(normalizedActiveFilter, criterion), criterion))?.id
        ?? (customSections ?? []).find((section) => section.isActive(normalizedActiveFilter[section.filterKey]))?.id;
      const nextSelected = openingLeafCriterion?.id ?? (typeof preselectCriterion === "string"
        ? preselectCriterion
        : preselectCriterion?.criterionId ?? (openAtRoot ? null : firstActive) ?? null);
      setRelatedWorkspaceSelection(typeof preselectCriterion === "object"
        ? { facet: preselectCriterion.relatedFacet ?? "mode", nestedCriterionId: preselectCriterion.nestedCriterionId }
        : null);
      setExpandedCriterion(nextSelected);
      window.setTimeout(() => {
        if (openingView === "expression") backButtonRef.current?.focus();
        else if (openLeafInline && initialExpressionPath) {
          const condition = dialogRef.current?.querySelector<HTMLElement>(`[data-inline-condition-index="${initialExpressionPath.at(-1)}"]`);
          getFirstInlineEditorControl(condition?.querySelector<HTMLElement>("[data-inline-condition-editor]"))?.focus();
        }
        else if (nextSelected && preselectCriterion) focusFirstEditorControl();
        else searchRef.current?.focus();
        if (nextSelected) criterionButtonRefs.current.get(nextSelected)?.scrollIntoView?.({ block: "center", inline: "nearest" });
      }, 0);
    }
    wasOpenRef.current = true;
  }, [cloneActiveFilter, criteria, customSections, focusFirstEditorControl, initialExpressionPath, initialView, normalizedActiveFilter, open, openAtRoot, preselectCriterion]);

  const dismiss = useCallback(() => {
    setEditFilter(cloneActiveFilter());
    setSearch("");
    setExpandedCriterion(null);
    setRelatedWorkspaceSelection(null);
    setDialogView("simple");
    setConditionDraft(null);
    setInlineStackReturnsToExpression(false);
    setNavigatorFocusId(null);
    onClose();
  }, [cloneActiveFilter, onClose]);

  const returnToSimpleFilters = useCallback(() => {
    setDialogView("simple");
    setConditionDraft(null);
    setInlineStackReturnsToExpression(false);
    window.setTimeout(() => window.setTimeout(() => {
        const keyedTarget = simpleReturnFocusKeyRef.current
          ? dialogRef.current?.querySelector<HTMLElement>(`[data-simple-return-focus="${simpleReturnFocusKeyRef.current}"]`)
          : null;
        if (keyedTarget) keyedTarget.focus();
        else if (viewReturnFocusRef.current?.isConnected) viewReturnFocusRef.current.focus();
        else searchRef.current?.focus();
      }, 0), 0);
  }, []);

  const returnToExpression = useCallback(() => {
    setEditFilter((current) => {
      const merged = mergeFilterExpressionWithSimpleCriteria(current, criteria);
      return { ...expressionPassthroughFilter(current, criteria), ...(merged ? { [FILTER_EXPRESSION_STATE_KEY]: merged } : {}) };
    });
    setDialogView("expression");
    setConditionDraft(null);
    setInlineStackReturnsToExpression(false);
    window.setTimeout(() => window.setTimeout(() => {
        const keyedTarget = expressionReturnFocusKeyRef.current
          ? dialogRef.current?.querySelector<HTMLElement>(`[data-expression-return-focus="${expressionReturnFocusKeyRef.current}"]`)
          : null;
        if (keyedTarget) keyedTarget.focus();
        else if (viewReturnFocusRef.current?.isConnected) viewReturnFocusRef.current.focus();
        else backButtonRef.current?.focus();
      }, 0), 0);
  }, [criteria]);

  const enterExpression = useCallback((returnFocusKey?: string, groupPath?: number[]) => {
    viewReturnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    simpleReturnFocusKeyRef.current = returnFocusKey ?? viewReturnFocusRef.current?.dataset.simpleReturnFocus ?? null;
    setEditFilter((current) => {
      const merged = mergeFilterExpressionWithSimpleCriteria(current, criteria);
      return { ...expressionPassthroughFilter(current, criteria), ...(merged ? { [FILTER_EXPRESSION_STATE_KEY]: merged } : {}) };
    });
    setExpandedCriterion(null);
    setRelatedWorkspaceSelection(null);
    setInlineStackReturnsToExpression(false);
    setDialogView("expression");
    window.setTimeout(() => {
      const pathKey = groupPath?.join(".");
      const groupControl = groupPath
        ? dialogRef.current?.querySelector<HTMLElement>(`[data-expression-group-control="${pathKey}"]`)
        : null;
      (groupControl ?? backButtonRef.current)?.focus();
    }, 0);
  }, [criteria]);

  const cancelExpressionCondition = useCallback(() => {
    if (conditionDraft?.returnView === "simple") returnToSimpleFilters();
    else returnToExpression();
  }, [conditionDraft?.returnView, returnToExpression, returnToSimpleFilters]);

  useEffect(() => {
    if (!open) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.preventDefault();
        if (relatedWorkspaceSelection) {
          setRelatedWorkspaceSelection(null);
          window.setTimeout(() => dialogRef.current?.querySelector<HTMLElement>("[aria-label='Saved performer filter'], [aria-label='Saved video filter']")?.focus(), 0);
        } else if (conditionDraft) cancelExpressionCondition();
        else if (inlineStackReturnsToExpression) returnToExpression();
        else if (dialogView === "expression") returnToSimpleFilters();
        else if (relatedWorkspaceCriterion) {
          if (relatedWorkspaceSelection) {
            setRelatedWorkspaceSelection(null);
            window.setTimeout(() => dialogRef.current?.querySelector<HTMLElement>("[aria-label='Saved performer filter'], [aria-label='Saved video filter']")?.focus(), 0);
          } else {
            setExpandedCriterion(null);
            window.setTimeout(() => searchRef.current?.focus(), 0);
          }
        } else dismiss();
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
  }, [cancelExpressionCondition, conditionDraft, dialogView, dismiss, inlineStackReturnsToExpression, open, relatedWorkspaceCriterion, relatedWorkspaceSelection, returnToExpression, returnToSimpleFilters]);

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

  const handleEditChip = useCallback((target: FilterChipTarget) => {
    const key = getFilterChipTargetKey(target);
    if (key === FILTER_EXPRESSION_STATE_KEY) {
      enterExpression("advanced");
      return;
    }
    const customSection = (customSections ?? []).find((section) => section.filterKey === key);
    const criterion = criteria.find((item) => item.id === key
      || item.filterKey === key
      || item.secondaryFilterKey === key
      || item.auxiliaryToggleKey === key);
    const nextId = customSection?.id ?? criterion?.id;
    if (nextId) {
      setSearch("");
      if (target.kind === "related") {
        setExpandedCriterion(nextId);
        setRelatedWorkspaceSelection({ facet: target.facet, nestedCriterionId: target.nestedCriterionId });
        focusFirstEditorControl();
      } else {
        selectNavigatorItem(nextId);
      }
    }
  }, [criteria, customSections, enterExpression, focusFirstEditorControl, selectNavigatorItem]);

  const handleRemoveChip = useCallback((target: FilterChipTarget) => {
    const key = getFilterChipTargetKey(target);
    const customSection = (customSections ?? []).find((section) => section.filterKey === key);
    if (target.kind === "root" && customSection) {
      setEditFilter((current) => {
        const next = { ...current };
        delete next[customSection.filterKey];
        return next;
      });
      return;
    }
    setEditFilter((current) => removeObjectFilterChipTarget(current, criteria, target));
  }, [criteria, customSections]);

  const handleRemoveRelatedWorkspaceChip = useCallback((target: FilterChipTarget) => {
    if (conditionDraft && relatedWorkspaceCriterion && getExpressionConditionCriterion(conditionDraft.filter, criteria)?.id === relatedWorkspaceCriterion.id) {
      setConditionDraft((current) => current ? { ...current, filter: removeObjectFilterChipTarget(current.filter, criteria, target) } : current);
      return;
    }
    if (relatedExpressionInstances.length === 1) {
      const [{ path }] = relatedExpressionInstances;
      setEditFilter((current) => {
        const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
        const filter = currentExpression ? getExpressionLeaf(currentExpression, path) : undefined;
        if (!currentExpression || !filter) return current;
        return { ...current, [FILTER_EXPRESSION_STATE_KEY]: updateExpressionLeaf(currentExpression, path, removeObjectFilterChipTarget(filter, criteria, target)) };
      });
      return;
    }
    handleRemoveChip(target);
  }, [conditionDraft, criteria, handleRemoveChip, relatedExpressionInstances, relatedWorkspaceCriterion]);

  const removeRelatedExpressionChip = (path: number[], target: FilterChipTarget) => {
    setEditFilter((current) => {
      const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
      const filter = currentExpression ? getExpressionLeaf(currentExpression, path) : undefined;
      if (!currentExpression || !filter) return current;
      return { ...current, [FILTER_EXPRESSION_STATE_KEY]: updateExpressionLeaf(currentExpression, path, removeObjectFilterChipTarget(filter, criteria, target)) };
    });
  };

  const handleApply = () => {
    const hasExpression = Boolean(editFilter[FILTER_EXPRESSION_STATE_KEY]);
    const mergedExpression = hasExpression ? sanitizeFilterExpression(mergeFilterExpressionWithSimpleCriteria(editFilter, criteria), criteria) : undefined;
    onApply(hasExpression
      ? { ...expressionPassthroughFilter(editFilter, criteria), ...(mergedExpression ? { [FILTER_EXPRESSION_STATE_KEY]: mergedExpression } : {}) }
      : activeEditFilter);
    onClose();
  };

  const finishRelatedWorkspace = () => {
    setRelatedWorkspaceSelection(null);
    setExpandedCriterion(null);
    window.setTimeout(() => searchRef.current?.focus(), 0);
  };

  const handleClear = () => {
    setEditFilter({});
    setExpandedCriterion(null);
    setRelatedWorkspaceSelection(null);
    setDialogView("simple");
    setConditionDraft(null);
    setInlineStackReturnsToExpression(false);
    window.setTimeout(() => searchRef.current?.focus(), 0);
  };

  const focusInlineCondition = (index: number) => {
    window.setTimeout(() => window.setTimeout(() => {
      const condition = dialogRef.current?.querySelector<HTMLElement>(`[data-inline-condition-index="${index}"]`);
      getFirstInlineEditorControl(condition?.querySelector<HTMLElement>("[data-inline-condition-editor]"))?.focus();
    }, 0), 0);
  };

  const updateInlineCondition = (path: number[], criterion: CriterionDefinition, value: unknown) => {
    setEditFilter((current) => {
      const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
      const existingFilter = currentExpression ? getExpressionLeaf(currentExpression, path) : undefined;
      if (!currentExpression || !existingFilter) return current;
      const nextFilter = { ...existingFilter };
      delete nextFilter[criterion.filterKey];
      if (criterion.secondaryFilterKey) delete nextFilter[criterion.secondaryFilterKey];
      return {
        ...current,
        [FILTER_EXPRESSION_STATE_KEY]: updateExpressionLeaf(currentExpression, path, {
          ...nextFilter,
          ...setCriterionFilterValue({}, criterion, value),
          _criterionId: criterion.id,
        }),
      };
    });
  };

  const updateInlineAuxiliaryToggle = (path: number[], criterion: CriterionDefinition, checked: boolean) => {
    const auxiliaryToggleKey = criterion.auxiliaryToggleKey;
    if (!auxiliaryToggleKey) return;
    setEditFilter((current) => {
      const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
      const filter = currentExpression ? getExpressionLeaf(currentExpression, path) : undefined;
      if (!currentExpression || !filter) return current;
      const nextFilter = { ...filter };
      if (checked) nextFilter[auxiliaryToggleKey] = true;
      else delete nextFilter[auxiliaryToggleKey];
      return { ...current, [FILTER_EXPRESSION_STATE_KEY]: updateExpressionLeaf(currentExpression, path, nextFilter) };
    });
  };

  const openNewExpressionCondition = (criterionId?: string, parentPath: number[] = []) => {
    if (expression && getExpressionGroup(expression, parentPath)?.operator === "NOT") return;
    viewReturnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (dialogView === "simple") simpleReturnFocusKeyRef.current = criterionId ? `repeat-${criterionId}` : null;
    else expressionReturnFocusKeyRef.current = `add-${parentPath.join(".")}`;
    setConditionDraft({ filter: criterionId ? { _criterionId: criterionId } : {}, parentPath, isNew: true, returnView: dialogView === "simple" ? "simple" : "expression" });
    setExpandedCriterion(criterionId ?? null);
    setDialogView("simple");
    window.setTimeout(() => criterionId ? focusFirstConditionEditorControl() : searchRef.current?.focus(), 0);
  };

  const openExpressionCondition = (path: number[], returnView: "simple" | "expression" = "expression") => {
    if (!expression) return;
    const filter = getExpressionLeaf(expression, path);
    if (!filter) return;
    const criterion = getExpressionConditionCriterion(filter, criteria);
    if (returnView === "expression" && !expression.children.some((child) => child.group) && criterion?.type !== "related") {
      expressionReturnFocusKeyRef.current = `edit-${path.join(".")}`;
      setConditionDraft(null);
      setInlineStackReturnsToExpression(true);
      setExpandedCriterion(criterion?.id ?? null);
      setDialogView("simple");
      focusInlineCondition(path[0]);
      return;
    }
    viewReturnFocusRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (returnView === "simple") simpleReturnFocusKeyRef.current = `expression-${path.join(".")}`;
    else expressionReturnFocusKeyRef.current = `edit-${path.join(".")}`;
    setConditionDraft({ filter, path, parentPath: path.slice(0, -1), isNew: false, returnView: returnView === "simple" ? "simple" : "expression" });
    setExpandedCriterion(criterion?.id ?? null);
    setDialogView("simple");
    window.setTimeout(() => criterion ? focusFirstConditionEditorControl() : searchRef.current?.focus(), 0);
  };

  const openSimpleExpressionCondition = (path: number[]) => {
    if (!expression) return;
    const filter = getExpressionLeaf(expression, path);
    const criterion = filter ? getExpressionConditionCriterion(filter, criteria) : undefined;
    if (!criterion || criterion.type === "related") {
      openExpressionCondition(path, "simple");
      return;
    }
    setSearch("");
    setRelatedWorkspaceSelection(null);
    setSimpleExpressionGroupPath(path.slice(0, -1));
    setExpandedCriterion(criterion.id);
    const siblingCount = getExpressionGroup(expression, path.slice(0, -1))?.children.filter((child) => child.filter
      && getExpressionConditionCriterion(child.filter, criteria)?.id === criterion.id).length ?? 0;
    if (siblingCount > 1) focusInlineCondition(path.at(-1) ?? 0);
    else focusFirstEditorControl();
  };

  const saveExpressionCondition = () => {
    if (!conditionDraft) return;
    const sanitized = sanitizeFilterCriteria(conditionDraft.filter, criteria);
    if (Object.keys(sanitized).length === 0) return;
    setEditFilter((current) => {
      const base = mergeFilterExpressionWithSimpleCriteria(current, criteria) ?? { operator: "AND" as const, children: [] };
      if (conditionDraft.isNew && countFilterExpressionConditions(base) >= MAX_FILTER_EXPRESSION_CONDITIONS) return current;
      if (conditionDraft.isNew && !conditionDraft.implicitRootAnd && getExpressionGroup(base, conditionDraft.parentPath)?.operator === "NOT") return current;
      const next = conditionDraft.isNew && conditionDraft.implicitRootAnd
        ? base.operator === "AND"
          ? { ...base, children: [...base.children, { filter: sanitized }] }
          : { operator: "AND" as const, children: [{ group: base }, { filter: sanitized }] }
        : conditionDraft.isNew
        ? replaceExpressionGroup(base, conditionDraft.parentPath, (group) => ({ ...group, children: [...group.children, { filter: sanitized }] }))
        : updateExpressionLeaf(base, conditionDraft.path ?? [], sanitized);
      return { ...expressionPassthroughFilter(current, criteria), [FILTER_EXPRESSION_STATE_KEY]: next };
    });
    if (conditionDraft.returnView === "simple") returnToSimpleFilters();
    else returnToExpression();
  };

  const addRelatedCondition = () => {
    if (!relatedWorkspaceCriterion) return;
    const merged = mergeFilterExpressionWithSimpleCriteria(editFilter, criteria);
    if (!merged || countFilterExpressionConditions(merged) >= MAX_FILTER_EXPRESSION_CONDITIONS) return;
    simpleReturnFocusKeyRef.current = `repeat-${relatedWorkspaceCriterion.id}`;
    setConditionDraft({
      filter: { _criterionId: relatedWorkspaceCriterion.id },
      parentPath: [],
      isNew: true,
      returnView: "simple",
      implicitRootAnd: true,
    });
    setRelatedWorkspaceSelection(null);
    focusFirstConditionEditorControl();
  };

  const addImplicitAndCondition = (criterion: CriterionDefinition) => {
    const merged = mergeFilterExpressionWithSimpleCriteria(editFilter, criteria);
    if (!merged || countFilterExpressionConditions(merged) >= MAX_FILTER_EXPRESSION_CONDITIONS) return;
    simpleReturnFocusKeyRef.current = `repeat-${criterion.id}`;
    setConditionDraft({
      filter: { _criterionId: criterion.id },
      parentPath: [],
      isNew: true,
      returnView: "simple",
      implicitRootAnd: true,
    });
    setExpandedCriterion(criterion.id);
    setRelatedWorkspaceSelection(null);
    focusFirstConditionEditorControl();
  };

  const removeSimpleExpressionCondition = (index: number, inlinePosition?: number, parentPath: number[] = simpleExpressionGroupPath) => {
    setEditFilter((current) => {
      const currentExpression = current[FILTER_EXPRESSION_STATE_KEY] as FilterExpression<Record<string, unknown>> | undefined;
      if (!currentExpression) return current;
      const group = getExpressionGroup(currentExpression, parentPath);
      if (!group) return current;
      if (group.operator === "NOT" && group.children.length === 1) {
        const nextExpression = removeExpressionGroup(currentExpression, parentPath);
        const next = { ...current };
        if (nextExpression) next[FILTER_EXPRESSION_STATE_KEY] = nextExpression;
        else delete next[FILTER_EXPRESSION_STATE_KEY];
        return next;
      }
      const children = group.children.filter((_, candidate) => candidate !== index);
      const next = { ...current };
      if (parentPath.length === 0 && children.length === 0) delete next[FILTER_EXPRESSION_STATE_KEY];
      else next[FILTER_EXPRESSION_STATE_KEY] = replaceExpressionGroup(currentExpression, parentPath, (target) => ({ ...target, children }));
      return next;
    });
    window.setTimeout(() => {
      if (inlinePosition !== undefined) {
        window.setTimeout(() => {
          const conditions = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>("[data-inline-condition-index]") ?? []);
          const target = conditions[Math.min(inlinePosition, conditions.length - 1)];
          if (target) getFirstInlineEditorControl(target.querySelector<HTMLElement>("[data-inline-condition-editor]"))?.focus();
          else {
            const panel = dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
            getFirstEditorControl(panel)?.focus();
          }
        }, 0);
        return;
      }
      const nextIndex = Math.min(index, directlyEditableExpressionChildren.length - 2);
      const target = dialogRef.current?.querySelector<HTMLElement>(`[data-simple-return-focus="expression-${Math.max(0, nextIndex)}"]`);
      const toolbarButtons = Array.from(selectedFiltersToolbarRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
      (target ?? toolbarButtons[Math.min(selectedFiltersLastFocusedIndexRef.current, toolbarButtons.length - 1)] ?? searchRef.current)?.focus();
    }, 0);
  };

  const conditionCriterionId = conditionDraft
    ? typeof conditionDraft.filter._criterionId === "string"
      ? conditionDraft.filter._criterionId
      : criteria.find((criterion) => getCriterionFilterValue(conditionDraft.filter, criterion) !== undefined)?.id
    : undefined;
  const conditionCriterion = criteria.find((criterion) => criterion.id === conditionCriterionId);
  const conditionTitle = `${conditionDraft?.isNew ? "Add" : "Edit"} ${conditionCriterion?.label ?? "filter"} condition`;
  const conditionCanSave = Boolean(conditionCriterion && isCriterionValueValid(getCriterionFilterValue(conditionDraft?.filter ?? {}, conditionCriterion), conditionCriterion));
  const selectedCriterionEditorFilter = conditionDraft && conditionCriterion?.id === selectedCompactCriterion?.id
    ? conditionDraft.filter
    : selectedSingleExpressionInstance?.filter ?? editFilter;
  const relatedWorkspaceValue = relatedWorkspaceCriterion
    ? getCriterionFilterValue(relatedWorkspaceObjectFilter, relatedWorkspaceCriterion)
    : undefined;
  const relatedConditionLimitReached = countFilterExpressionConditions(mergeFilterExpressionWithSimpleCriteria(editFilter, criteria)) >= MAX_FILTER_EXPRESSION_CONDITIONS;
  const mergedExpressionConditionCount = countFilterExpressionConditions(mergeFilterExpressionWithSimpleCriteria(editFilter, criteria));
  const canCombineFilters = Boolean(dialogView === "simple" && !conditionDraft && supportsExpressions && mergedExpressionConditionCount >= 2);
  const canAddRelatedCondition = Boolean(
    !conditionDraft
      && supportsExpressions
      && relatedWorkspaceCriterion
      && relatedWorkspaceCriterion.expressionSupported !== false
      && (relatedExpressionInstances.length > 0 || isCriterionValueValid(relatedWorkspaceValue, relatedWorkspaceCriterion)),
  );

  useEffect(() => {
    const toolbar = selectedFiltersToolbarRef.current;
    if (!toolbar) return;
    const buttons = Array.from(toolbar.querySelectorAll<HTMLButtonElement>("button:not(:disabled)"));
    if (buttons.length === 0) return;
    const activeButton = document.activeElement instanceof HTMLButtonElement && toolbar.contains(document.activeElement)
      ? document.activeElement
      : undefined;
    const rememberedWasRemoved = Boolean(selectedFiltersLastFocusedRef.current && !toolbar.contains(selectedFiltersLastFocusedRef.current));
    const rememberedButton = selectedFiltersLastFocusedRef.current && toolbar.contains(selectedFiltersLastFocusedRef.current)
      ? selectedFiltersLastFocusedRef.current
      : undefined;
    const indexedFallback = buttons[Math.min(selectedFiltersLastFocusedIndexRef.current, buttons.length - 1)];
    const tabStop = activeButton ?? rememberedButton ?? indexedFallback ?? buttons[0];
    buttons.forEach((button) => { button.tabIndex = button === tabStop ? 0 : -1; });
    if (rememberedWasRemoved && document.activeElement === document.body) tabStop.focus();
  });

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
            if (conditionDraft) saveExpressionCondition();
            else if (relatedWorkspaceCriterion) finishRelatedWorkspace();
            else handleApply();
          }
        }}
        onClick={(e) => e.stopPropagation()}
        onMouseDown={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex min-h-16 items-center justify-between border-b border-border px-4 pt-[env(safe-area-inset-top)] md:px-6 md:pt-0">
          <div className="flex min-w-0 items-center gap-2">
            {conditionDraft || inlineStackReturnsToExpression || dialogView !== "simple" || relatedWorkspaceCriterion ? (
              <button
                ref={backButtonRef}
                type="button"
                onClick={() => {
                  if (conditionDraft && relatedWorkspaceSelection) {
                    setRelatedWorkspaceSelection(null);
                    window.setTimeout(() => dialogRef.current?.querySelector<HTMLElement>("[aria-label='Saved performer filter'], [aria-label='Saved video filter']")?.focus(), 0);
                  } else if (conditionDraft) cancelExpressionCondition();
                  else if (inlineStackReturnsToExpression) returnToExpression();
                  else if (dialogView === "expression") returnToSimpleFilters();
                  else {
                    setExpandedCriterion(null);
                    setRelatedWorkspaceSelection(null);
                    window.setTimeout(() => searchRef.current?.focus(), 0);
                  }
                }}
                className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground"
                aria-label={conditionDraft
                  ? conditionDraft?.returnView === "simple" ? "Back to filters" : "Back to Combine Filters"
                  : inlineStackReturnsToExpression ? "Back to Combine Filters"
                  : dialogView === "expression" ? "Back to simple filters" : "Back to filters"}
              >
                <ArrowLeft className="h-5 w-5" />
              </button>
            ) : selectedItem ? (
              <button type="button" data-mobile-only-control onClick={() => { setExpandedCriterion(null); window.setTimeout(() => searchRef.current?.focus(), 0); }} className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground md:hidden" aria-label="Back to filter criteria">
                <ArrowLeft className="h-5 w-5" />
              </button>
            ) : null}
            {relatedWorkspaceCriterion ? (
              <span
                aria-label={relatedWorkspaceCriterion.entityType === "performers" ? "Performers" : "Videos"}
                className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-accent/15 text-accent"
              >
                {relatedWorkspaceCriterion.entityType === "performers"
                  ? <Users className="h-4 w-4" />
                  : <Film className="h-4 w-4" />}
              </span>
            ) : null}
            <h2 id="filter-dialog-title" className="truncate text-lg font-semibold text-foreground">
              {conditionDraft ? conditionTitle : dialogView === "expression" ? "Combine Filters" : relatedWorkspaceCriterion ? `Filters / ${relatedWorkspaceCriterion.label}` : "Filters"}
            </h2>
            {dialogView === "simple" && !relatedWorkspaceCriterion && selectedItem ? <span className="truncate text-sm text-secondary md:hidden">{selectedItem.label}</span> : null}
            {dialogView === "simple" && displayedActiveCount > 0 && (
              <span className="rounded-full bg-accent px-2 py-0.5 text-xs font-bold text-white" aria-label={`${displayedActiveCount} active filters`}>
                {displayedActiveCount}
              </span>
            )}
          </div>
          <div className="flex items-center gap-2">
            <button type="button" onClick={dismiss} className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-muted hover:bg-card hover:text-foreground" aria-label="Close filters">
              <X className="h-5 w-5" />
            </button>
          </div>
        </div>

        {dialogView === "simple" && !conditionDraft && !relatedWorkspaceCriterion && (expressionConditionCount > 0 || Object.keys(activeEditFilter).length > 0) ? (
          <div
            ref={selectedFiltersToolbarRef}
            className="flex max-h-[min(12rem,35dvh)] shrink-0 flex-wrap items-center gap-2 overflow-y-auto border-b border-border px-3 py-2 [&_button:focus-visible]:relative [&_button:focus-visible]:z-10 [&_button:focus-visible]:bg-accent/25 [&_button:focus-visible]:outline-none [&_button:focus-visible]:ring-2 [&_button:focus-visible]:ring-inset [&_button:focus-visible]:ring-accent md:px-4"
            role="toolbar"
            aria-label="Selected filters"
            aria-orientation="horizontal"
            aria-describedby={selectedFiltersInstructionsId}
            onFocusCapture={(event) => {
              const target = event.target;
              if (!(target instanceof HTMLButtonElement)) return;
              selectedFiltersLastFocusedRef.current = target;
              const buttons = Array.from(selectedFiltersToolbarRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
              selectedFiltersLastFocusedIndexRef.current = Math.max(0, buttons.indexOf(target));
              buttons.forEach((button) => { button.tabIndex = button === target ? 0 : -1; });
            }}
            onKeyDownCapture={(event) => {
              if (event.key === "ArrowDown" && event.target instanceof HTMLButtonElement) {
                event.preventDefault();
                event.stopPropagation();
                searchRef.current?.focus();
                return;
              }
              if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
              const buttons = Array.from(selectedFiltersToolbarRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
              const current = event.target instanceof HTMLButtonElement ? event.target : undefined;
              const index = current ? buttons.indexOf(current) : -1;
              if (index < 0 || buttons.length === 0) return;
              const nextIndex = event.key === "Home"
                ? 0
                : event.key === "End"
                ? buttons.length - 1
                : event.key === "ArrowRight"
                ? (index + 1) % buttons.length
                : (index - 1 + buttons.length) % buttons.length;
              event.preventDefault();
              event.stopPropagation();
              buttons[nextIndex].focus();
            }}
          >
            <span id={selectedFiltersInstructionsId} className="sr-only">Use Left and Right Arrow to move between selected filter parts and Clear all. Press Down Arrow to move to filter search. Press Enter to activate the focused control.</span>
            {validSimpleExpressionEntries.map(({ child, index }) => (
              <div key={index} className="flex min-h-9 max-w-full items-stretch overflow-hidden rounded-lg border border-border bg-card text-sm">
                <button type="button" onClick={() => openSimpleExpressionCondition([index])} data-simple-return-focus={`expression-${index}`} className="min-w-0 px-3 text-left hover:bg-background/40" aria-label={`Edit filter: ${summarizeExpressionCondition(child.filter ?? {}, criteria)}`}>
                  <span className="truncate">{summarizeExpressionCondition(child.filter ?? {}, criteria)}</span>
                </button>
                <button type="button" onClick={() => removeSimpleExpressionCondition(index)} className="flex w-9 items-center justify-center border-l border-border text-muted hover:bg-red-500/10 hover:text-red-300" aria-label={`Remove filter: ${summarizeExpressionCondition(child.filter ?? {}, criteria)}`}>
                  <X className="h-4 w-4" />
                </button>
              </div>
            ))}
            {hasComplexExpression ? (
              <ActiveObjectFilterChips
                criteriaDefinitions={criteria}
                objectFilter={{ [FILTER_EXPRESSION_STATE_KEY]: expression }}
                onEdit={(target) => {
                  if (target.kind === "expression") openSimpleExpressionCondition(target.path);
                  else if (target.kind === "root" && target.key === FILTER_EXPRESSION_STATE_KEY) {
                    const path = target.path ?? [];
                    enterExpression(`expression-group-${path.join(".") || "root"}`, path);
                  } else handleEditChip(target);
                }}
                onRemove={handleRemoveChip}
                expressionReturnFocusKeys
                hideRootAndOperator
                embeddedInToolbar
                ariaLabel="Selected expression filters"
                className="!m-0 !border-0 !bg-transparent !p-0"
              />
            ) : null}
            {Object.keys(activeEditFilter).length > 0 ? <ActiveObjectFilterChips
              criteriaDefinitions={criteria}
              objectFilter={activeEditFilter}
              customFilterSections={customSections}
              onEdit={handleEditChip}
              onRemove={handleRemoveChip}
              onClearAll={handleClear}
              rovingKeyboardAccess
              embeddedInToolbar
              onFocusFallback={() => {
                const toolbarButtons = Array.from(selectedFiltersToolbarRef.current?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
                const toolbarFallback = toolbarButtons[Math.min(selectedFiltersLastFocusedIndexRef.current, toolbarButtons.length - 1)];
                if (toolbarFallback) {
                  toolbarFallback.focus();
                  return;
                }
                const mobileLayout = typeof window.matchMedia === "function"
                  ? window.matchMedia("(max-width: 767px)").matches
                  : window.innerWidth < 768;
                if (mobileLayout && expandedCriterion) {
                  const editorControl = getFirstEditorControl(dialogRef.current?.querySelector<HTMLElement>("[role='tabpanel']"));
                  if (editorControl) {
                    editorControl.focus();
                    return;
                  }
                }
                const criterionButton = expandedCriterion ? criterionButtonRefs.current.get(expandedCriterion) : undefined;
                if (criterionButton) criterionButton.focus();
                else searchRef.current?.focus();
              }}
              onFocusKey={(key) => {
                const buttons = dialogRef.current?.querySelectorAll<HTMLButtonElement>("button[data-active-filter-key]");
                Array.from(buttons ?? []).find((button) => button.dataset.activeFilterKey === key)?.focus();
              }}
              ariaLabel="Selected filters"
              className="!m-0 max-h-[min(12rem,35dvh)] overflow-y-auto !border-0 !bg-transparent !p-0"
            /> : null}
            {Object.keys(activeEditFilter).length === 0 && hasComplexExpression ? (
              <button type="button" onClick={handleClear} className="min-h-9 rounded-lg px-3 text-sm text-secondary hover:bg-red-500/10 hover:text-red-300">Clear all</button>
            ) : null}
          </div>
        ) : null}

        {dialogView === "expression" ? (
          <FilterExpressionEditor
            criteria={criteria}
            value={expression ?? { operator: "AND", children: [] }}
            onChange={(value) => setEditFilter((current) => ({ ...expressionPassthroughFilter(current, criteria), [FILTER_EXPRESSION_STATE_KEY]: value }))}
            onAddCondition={openNewExpressionCondition}
            onEditCondition={openExpressionCondition}
            subjectLabel={subjectLabel}
          />
        ) : relatedWorkspaceCriterion ? (
          <div className="flex min-h-0 flex-1 flex-col">
            {canAddRelatedCondition ? (
              <div className="flex justify-end px-3 pt-3 md:px-4">
                <button
                  type="button"
                  onClick={addRelatedCondition}
                  disabled={relatedConditionLimitReached}
                  data-simple-return-focus={`repeat-${relatedWorkspaceCriterion.id}`}
                  className="inline-flex h-11 w-11 items-center justify-center rounded-lg border border-border text-secondary hover:bg-card hover:text-foreground disabled:opacity-40"
                  title={`Add another ${relatedWorkspaceCriterion.entityType === "performers" ? "performer" : "video"} condition`}
                  aria-label={`Add another ${relatedWorkspaceCriterion.entityType === "performers" ? "performer" : "video"} condition`}
                >
                  <Plus className="h-4 w-4" />
                </button>
              </div>
            ) : null}
            {!conditionDraft && relatedExpressionInstances.length > 1 ? (
              <div className="min-h-0 flex-1 space-y-3 overflow-y-auto px-3 pb-3 md:px-4 md:pb-4">
                {relatedExpressionInstances.map((instance, position) => (
                  <fieldset key={instance.index} data-inline-condition-index={instance.index} aria-label={`${relatedWorkspaceCriterion.label} condition ${position + 1}`} className="rounded-xl border border-border bg-card/30 p-3">
                    <div className="mb-2 flex min-h-9 flex-wrap items-center justify-between gap-2">
                      <legend className="px-1 text-sm font-semibold text-foreground">{relatedWorkspaceCriterion.label} condition {position + 1}</legend>
                      <div data-inline-condition-editor className="flex items-center gap-1">
                        <button type="button" data-simple-return-focus={`expression-${instance.path.join(".")}`} onClick={() => openExpressionCondition(instance.path, "simple")} className="min-h-9 rounded-lg px-3 text-sm text-secondary hover:bg-card hover:text-foreground">Edit</button>
                        <button type="button" onClick={() => removeSimpleExpressionCondition(instance.index, position, instance.path.slice(0, -1))} className="inline-flex min-h-9 items-center gap-2 rounded-lg px-3 text-sm text-muted hover:bg-red-500/10 hover:text-red-300" aria-label={`Remove ${relatedWorkspaceCriterion.label} condition ${position + 1}`}><X className="h-4 w-4" /> Remove</button>
                      </div>
                    </div>
                    <ActiveObjectFilterChips
                      criteriaDefinitions={criteria}
                      objectFilter={instance.filter}
                      onEdit={(target) => {
                        openExpressionCondition(instance.path, "simple");
                        if (target.kind === "related") setRelatedWorkspaceSelection({ facet: target.facet, nestedCriterionId: target.nestedCriterionId });
                      }}
                      onRemove={(target) => removeRelatedExpressionChip(instance.path, target)}
                      rovingKeyboardAccess
                      onFocusFallback={() => window.setTimeout(() => {
                        const searchLabel = relatedWorkspaceCriterion.entityType === "performers"
                          ? "Search performer filter criteria"
                          : "Search video filter criteria";
                        const searchInput = dialogRef.current?.querySelector<HTMLElement>(`[aria-label="${searchLabel}"]`);
                        const survivingCard = dialogRef.current?.querySelector<HTMLElement>("[data-inline-condition-editor]");
                        (searchInput ?? getFirstInlineEditorControl(survivingCard) ?? dialogRef.current?.querySelector<HTMLElement>(`[data-simple-return-focus="repeat-${relatedWorkspaceCriterion.id}"]`))?.focus();
                      }, 0)}
                      ariaLabel={`${relatedWorkspaceCriterion.label} condition ${position + 1} filters`}
                      className="!m-0 !border-0 !bg-transparent !p-0"
                    />
                  </fieldset>
                ))}
              </div>
            ) : (
              <>
                {isCriterionValueValid(getCriterionFilterValue(relatedWorkspaceObjectFilter, relatedWorkspaceCriterion), relatedWorkspaceCriterion) ? (
                  <ActiveObjectFilterChips
                    criteriaDefinitions={criteria}
                    objectFilter={setCriterionFilterValue({}, relatedWorkspaceCriterion, getCriterionFilterValue(relatedWorkspaceObjectFilter, relatedWorkspaceCriterion))}
                    onEdit={handleEditChip}
                    onRemove={handleRemoveRelatedWorkspaceChip}
                    rovingKeyboardAccess
                    onFocusFallback={() => {
                      const searchLabel = relatedWorkspaceCriterion.entityType === "performers"
                        ? "Search performer filter criteria"
                        : "Search video filter criteria";
                      window.setTimeout(() => dialogRef.current?.querySelector<HTMLElement>(`[aria-label="${searchLabel}"]`)?.focus(), 0);
                    }}
                    ariaLabel={`${relatedWorkspaceCriterion.label} selected filters`}
                    className="mx-3 mt-3 max-h-[min(10rem,25dvh)] shrink-0 overflow-y-auto md:mx-4"
                  />
                ) : null}
                <RelatedFilterWorkspace
                  criterion={relatedWorkspaceCriterion}
                  value={getCriterionFilterValue(relatedWorkspaceObjectFilter, relatedWorkspaceCriterion) as RelatedFilterCriterion | undefined}
                  onChange={(value) => conditionCriterion?.id === relatedWorkspaceCriterion.id && conditionDraft
                    ? setConditionDraft((current) => current ? { ...current, filter: { _criterionId: relatedWorkspaceCriterion.id, ...setCriterionFilterValue({}, relatedWorkspaceCriterion, value) } } : current)
                    : relatedExpressionInstances.length === 1
                      ? updateInlineCondition(relatedExpressionInstances[0].path, relatedWorkspaceCriterion, value)
                    : handleSetCriterion(relatedWorkspaceCriterion, value)}
                  selection={relatedWorkspaceSelection}
                  onSelectionChange={setRelatedWorkspaceSelection}
                />
              </>
            )}
          </div>
        ) : <div className="grid min-h-0 flex-1 overflow-hidden md:grid-cols-[20rem_minmax(0,1fr)]">
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
                    if (event.key === "ArrowUp") {
                      const toolbar = selectedFiltersToolbarRef.current;
                      const buttons = Array.from(toolbar?.querySelectorAll<HTMLButtonElement>("button:not(:disabled)") ?? []);
                      const rememberedButton = selectedFiltersLastFocusedRef.current && toolbar?.contains(selectedFiltersLastFocusedRef.current)
                        ? selectedFiltersLastFocusedRef.current
                        : undefined;
                      const toolbarTarget = rememberedButton ?? buttons.find((button) => button.tabIndex === 0) ?? buttons[0];
                      if (toolbarTarget) {
                        event.preventDefault();
                        toolbarTarget.focus();
                      }
                      return;
                    }
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
                {selectedItem.kind === "criterion"
                  && !conditionDraft
                  && supportsExpressions
                  && selectedItem.active
                  && selectedItem.criterion.expressionSupported !== false ? (
                    <div className="mb-2 flex justify-end">
                      <button
                        type="button"
                        onClick={() => addImplicitAndCondition(selectedItem.criterion)}
                        disabled={mergedExpressionConditionCount >= MAX_FILTER_EXPRESSION_CONDITIONS}
                        data-simple-return-focus={`repeat-${selectedItem.criterion.id}`}
                        className="inline-flex h-11 w-11 items-center justify-center rounded-lg border border-border text-secondary hover:bg-card hover:text-foreground disabled:opacity-40"
                        title={`Add another ${selectedItem.criterion.label}`}
                        aria-label={`Add another ${selectedItem.criterion.label}`}
                      >
                        <Plus className="h-4 w-4" />
                      </button>
                    </div>
                  ) : null}
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
                ) : showInlineConditionStack ? (
                  <div className="space-y-4">
                    {selectedExpressionInstances.map(({ index, path, filter }, position) => (
                      <fieldset
                        key={index}
                        data-inline-condition-index={index}
                        aria-label={`${selectedItem.criterion.label} condition ${position + 1}`}
                        className="rounded-xl border border-border bg-card/30 p-4"
                      >
                        <div className="mb-4 flex min-h-9 items-center justify-between gap-3">
                          <legend className="px-1 text-sm font-semibold text-foreground">{selectedItem.criterion.label} condition {position + 1}</legend>
                          <button
                            type="button"
                            onClick={() => removeSimpleExpressionCondition(index, position, path.slice(0, -1))}
                            className="inline-flex min-h-9 items-center gap-2 rounded-lg px-3 text-sm text-muted hover:bg-red-500/10 hover:text-red-300"
                            aria-label={`Remove ${selectedItem.criterion.label} condition ${position + 1}`}
                          >
                            <X className="h-4 w-4" /> Remove
                          </button>
                        </div>
                        <div data-inline-condition-editor>
                          <CriterionEditor
                            criterion={selectedItem.criterion}
                            value={getCriterionFilterValue(filter, selectedItem.criterion)}
                            auxiliaryToggleChecked={selectedItem.criterion.auxiliaryToggleKey ? Boolean(filter[selectedItem.criterion.auxiliaryToggleKey]) : undefined}
                            onAuxiliaryToggleChange={(checked) => updateInlineAuxiliaryToggle(path, selectedItem.criterion, checked)}
                            onChange={(value) => updateInlineCondition(path, selectedItem.criterion, value)}
                          />
                        </div>
                      </fieldset>
                    ))}
                  </div>
                ) : (
                  <>
                    <CriterionEditor
                      criterion={selectedItem.criterion}
                      value={getCriterionFilterValue(selectedCriterionEditorFilter, selectedItem.criterion)}
                      auxiliaryToggleChecked={selectedItem.criterion.auxiliaryToggleKey ? Boolean(selectedCriterionEditorFilter[selectedItem.criterion.auxiliaryToggleKey]) : undefined}
                      onAuxiliaryToggleChange={(checked) => {
                        if (conditionCriterion?.id === selectedItem.criterion.id && conditionDraft) {
                          setConditionDraft((current) => {
                            if (!current || !selectedItem.criterion.auxiliaryToggleKey) return current;
                            const filter = { ...current.filter };
                            if (checked) filter[selectedItem.criterion.auxiliaryToggleKey] = true;
                            else delete filter[selectedItem.criterion.auxiliaryToggleKey];
                            return { ...current, filter };
                          });
                        } else if (selectedSingleExpressionInstance) {
                          updateInlineAuxiliaryToggle(selectedSingleExpressionInstance.path, selectedItem.criterion, checked);
                        } else handleSetAuxiliaryToggle(selectedItem.criterion, checked);
                      }}
                      onChange={(value) => conditionCriterion?.id === selectedItem.criterion.id && conditionDraft
                        ? setConditionDraft((current) => current ? { ...current, filter: { _criterionId: selectedItem.criterion.id, ...setCriterionFilterValue({}, selectedItem.criterion, value) } } : current)
                        : selectedSingleExpressionInstance
                        ? updateInlineCondition(selectedSingleExpressionInstance.path, selectedItem.criterion, value)
                        : handleSetCriterion(selectedItem.criterion, value)}
                    />
                  </>
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
        </div>}

        {/* Footer */}
        <div className="flex min-h-16 flex-wrap items-center justify-between gap-2 border-t border-border px-4 py-2 pb-[max(0.5rem,env(safe-area-inset-bottom))] md:px-6 md:py-2">
          {canCombineFilters ? (
            <button
              type="button"
              onClick={() => enterExpression("combine")}
              data-simple-return-focus="combine"
              title="Combine Filters"
              aria-label="Combine Filters"
              className="inline-flex h-11 w-11 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground"
            >
              <Workflow className="h-4 w-4" />
            </button>
          ) : <span />}
          <div className="flex flex-wrap items-center justify-end gap-2">
            {conditionDraft ? (
              <>
                <button type="button" onClick={cancelExpressionCondition} className="min-h-11 rounded-lg border border-border px-4 text-sm text-secondary hover:bg-card hover:text-foreground">Cancel condition</button>
                <button type="button" onClick={saveExpressionCondition} disabled={!conditionCanSave} className="min-h-11 rounded-lg bg-accent px-5 text-sm font-semibold text-white hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50">Save condition</button>
              </>
            ) : (
              <>
                <button type="button" onClick={dismiss} className="min-h-11 rounded-lg border border-border px-4 text-sm text-secondary hover:bg-card hover:text-foreground">Cancel</button>
                {relatedWorkspaceCriterion ? (
                  <>
                    <button type="button" onClick={finishRelatedWorkspace} aria-keyshortcuts="Control+Enter Meta+Enter" className="min-h-11 rounded-lg border border-border px-4 text-sm font-semibold text-secondary hover:bg-card hover:text-foreground">Done</button>
                    <button type="button" onClick={handleApply} className="min-h-11 rounded-lg bg-accent px-5 text-sm font-semibold text-white hover:bg-accent-hover">Apply</button>
                  </>
                ) : (
                  <button type="button" onClick={handleApply} aria-keyshortcuts="Control+Enter Meta+Enter" className="min-h-11 rounded-lg bg-accent px-5 text-sm font-semibold text-white hover:bg-accent-hover">Apply</button>
                )}
              </>
            )}
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
    case "related":
      return null;
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

function FilterExpressionEditor({
  criteria,
  value,
  onChange,
  onAddCondition,
  onEditCondition,
  subjectLabel,
}: {
  criteria: CriterionDefinition[];
  value: FilterExpression<Record<string, unknown>>;
  onChange: (value: FilterExpression<Record<string, unknown>>) => void;
  onAddCondition: (criterionId?: string, parentPath?: number[]) => void;
  onEditCondition: (path: number[]) => void;
  subjectLabel: string;
}) {
  const conditionCount = countFilterExpressionConditions(value);
  const ratingOptions = useRatingOptions();
  const appConfig = useOptionalAppConfig();
  const metadataServers = appConfig?.config?.scraping?.metadataServers ?? [];
  const describeCondition = (filter: Record<string, unknown>) => formatExplanationNodeInline(explainExpressionLeaf(filter, criteria, ratingOptions, metadataServers));
  return (
    <div className="min-h-0 flex-1 overflow-y-auto px-3 py-5 md:px-8 md:py-7">
      <div className="mx-auto max-w-3xl space-y-4">
        <p className="text-sm font-medium text-secondary">Find {subjectLabel} where</p>
        <div data-expression-tree>
          <ExpressionGroupEditor group={value} groupPath={[]} criteria={criteria} root conditionCount={conditionCount} onChange={onChange} onAddCondition={onAddCondition} onEditCondition={onEditCondition} describeCondition={describeCondition} />
        </div>
      </div>
    </div>
  );
}

const MAX_FILTER_EXPRESSION_DEPTH = 8;
const MAX_FILTER_EXPRESSION_CONDITIONS = 100;

function ExpressionGroupEditor({
  group,
  groupPath,
  criteria,
  root = false,
  conditionCount,
  onChange,
  onAddCondition,
  onEditCondition,
  parentOperator,
  ungroupChildFromParent,
  removeGroupFromParent,
  describeCondition,
}: {
  group: FilterExpression<Record<string, unknown>>;
  groupPath: number[];
  criteria: CriterionDefinition[];
  root?: boolean;
  conditionCount: number;
  onChange: (value: FilterExpression<Record<string, unknown>>) => void;
  onAddCondition: (criterionId?: string, parentPath?: number[]) => void;
  onEditCondition: (path: number[]) => void;
  parentOperator?: "AND" | "OR" | "NOT";
  ungroupChildFromParent?: () => void;
  removeGroupFromParent?: () => void;
  describeCondition: (filter: Record<string, unknown>) => string;
}) {
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [groupingMode, setGroupingMode] = useState(false);
  const [openMenu, setOpenMenu] = useState<"group" | number | null>(null);
  const [announcement, setAnnouncement] = useState("");
  const childFocusRefs = useRef(new Map<number, HTMLElement>());
  const conditionMenuButtonRefs = useRef(new Map<number, HTMLButtonElement>());
  const groupMenuButtonRef = useRef<HTMLButtonElement>(null);
  const addConditionRef = useRef<HTMLButtonElement>(null);
  const groupPathKey = groupPath.join(".");
  const canCreateNestedGroup = groupPath.length + 2 <= MAX_FILTER_EXPRESSION_DEPTH;
  const hasGroupActions = group.operator === "NOT" || group.children.length === 1 || !root;
  useEffect(() => {
    setSelected(new Set());
    setGroupingMode(false);
    setOpenMenu(null);
  }, [group]);
  const focusChildAfterChange = (index: number) => {
    window.setTimeout(() => window.setTimeout(() => {
      const pathKey = [...groupPath, index].join(".");
      (childFocusRefs.current.get(index)
        ?? document.querySelector<HTMLElement>(`[data-expression-node-path="${pathKey}"]`)
        ?? addConditionRef.current)?.focus();
    }, 0), 0);
  };
  const updateChild = (index: number, child: FilterExpression<Record<string, unknown>>["children"][number]) => {
    const children = group.children.slice();
    children[index] = child;
    onChange({ ...group, children });
  };
  const removeChild = (index: number) => {
    setSelected(new Set());
    setOpenMenu(null);
    onChange({ ...group, children: group.children.filter((_, candidate) => candidate !== index) });
    focusChildAfterChange(Math.min(index, group.children.length - 2));
  };
  const ungroupChild = (index: number) => {
    const child = group.children[index];
    if (!child?.group) return;
    if (group.operator === "NOT" && child.group.children.length !== 1) {
      setAnnouncement("A NOT group must contain exactly one item.");
      return;
    }
    setSelected(new Set());
    setOpenMenu(null);
    onChange({ ...group, children: [...group.children.slice(0, index), ...child.group.children, ...group.children.slice(index + 1)] });
    focusChildAfterChange(index);
  };
  const groupSelected = (operator: "AND" | "OR" | "NOT") => {
    const indexes = [...selected].sort((a, b) => a - b);
    if (operator === "NOT" ? indexes.length !== 1 : indexes.length < 2) return;
    const selectedChildren = indexes.map((index) => group.children[index]);
    const first = indexes[0];
    const selectedSet = new Set(indexes);
    const children = group.children.flatMap((child, index) => index === first
      ? [{ group: { operator, children: selectedChildren } }]
      : selectedSet.has(index) ? [] : [child]);
    setSelected(new Set());
    setGroupingMode(false);
    onChange({ ...group, children });
    focusChildAfterChange(first);
  };
  const startGrouping = () => {
    if (!canCreateNestedGroup) {
      setAnnouncement(`Groups may not be nested more than ${MAX_FILTER_EXPRESSION_DEPTH} levels.`);
      return;
    }
    setSelected(new Set());
    setGroupingMode(true);
    window.setTimeout(() => childFocusRefs.current.get(0)?.focus(), 0);
  };
  const setOperator = (operator: "AND" | "OR" | "NOT") => {
    setOpenMenu(null);
    onChange({ ...group, operator });
    window.setTimeout(() => document.querySelector<HTMLElement>(`[data-expression-group-control="${groupPathKey}"]`)?.focus(), 0);
  };
  const toggleSelection = (index: number) => setSelected((current) => {
    const next = new Set(current);
    if (next.has(index)) next.delete(index); else next.add(index);
    return next;
  });
  const closeMenuAndRestoreFocus = () => {
    const trigger = openMenu === "group" ? groupMenuButtonRef.current : openMenu === null ? null : conditionMenuButtonRefs.current.get(openMenu);
    setOpenMenu(null);
    window.setTimeout(() => trigger?.focus(), 0);
  };

  return (
    <section
      className={root ? "space-y-3" : "space-y-2 rounded-xl border-l-2 border-accent/30 bg-card/25 px-3 py-3 md:px-4"}
      aria-label={`${group.operator} group`}
      data-expression-node-path={groupPathKey || undefined}
      tabIndex={root ? undefined : -1}
      onKeyDown={(event) => {
        if (event.key !== "Escape" || openMenu === null) return;
        event.preventDefault();
        event.stopPropagation();
        closeMenuAndRestoreFocus();
      }}
    >
      <div className="flex min-h-10 flex-wrap items-center gap-2">
        {group.operator === "NOT" ? (
          <span className="text-sm font-semibold text-foreground">Exclude this</span>
        ) : (
          <>
            <span className="text-sm font-medium text-secondary">Match</span>
            <div className="inline-flex rounded-lg bg-card p-1" role="group" aria-label="How conditions are combined">
              <button type="button" data-expression-group-control={group.operator === "AND" ? groupPathKey : undefined} aria-label="Match all" aria-pressed={group.operator === "AND"} onClick={() => setOperator("AND")} className={`min-h-8 rounded-md px-3 text-sm ${group.operator === "AND" ? "bg-accent text-white shadow-sm" : "text-secondary hover:text-foreground"}`}>All</button>
              <button type="button" data-expression-group-control={group.operator === "OR" ? groupPathKey : undefined} aria-label="Match any" aria-pressed={group.operator === "OR"} onClick={() => setOperator("OR")} className={`min-h-8 rounded-md px-3 text-sm ${group.operator === "OR" ? "bg-accent text-white shadow-sm" : "text-secondary hover:text-foreground"}`}>Any</button>
            </div>
            <span className="text-sm text-secondary">of these</span>
          </>
        )}
        {hasGroupActions ? <div className="relative ml-auto">
          <button ref={groupMenuButtonRef} type="button" data-expression-group-control={group.operator === "NOT" ? groupPathKey : undefined} aria-label={`More actions for ${root ? "root " : ""}group`} aria-expanded={openMenu === "group"} onClick={() => setOpenMenu((current) => current === "group" ? null : "group")} className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-muted hover:bg-card hover:text-foreground"><MoreHorizontal className="h-4 w-4" /></button>
          {openMenu === "group" ? (
            <div role="group" aria-label="Group actions" className="absolute right-0 top-full z-20 mt-1 min-w-44 rounded-lg border border-border bg-surface p-1 shadow-xl">
              {group.operator === "NOT" ? <>
                <button type="button" onClick={() => setOperator("AND")} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card">Match all</button>
                <button type="button" onClick={() => setOperator("OR")} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card">Match any</button>
              </> : group.children.length === 1 ? <button type="button" onClick={() => setOperator("NOT")} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card">Exclude this</button> : null}
              {!root ? <>
                <button type="button" onClick={() => { setOpenMenu(null); ungroupChildFromParent?.(); }} disabled={!ungroupChildFromParent || (parentOperator === "NOT" && group.children.length !== 1)} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card disabled:opacity-40">Dissolve group</button>
                <button type="button" onClick={() => { setOpenMenu(null); removeGroupFromParent?.(); }} disabled={!removeGroupFromParent || parentOperator === "NOT"} className="block min-h-10 w-full rounded px-3 text-left text-sm text-red-300 hover:bg-red-500/10 disabled:opacity-40">Remove group</button>
              </> : null}
            </div>
          ) : null}
        </div> : null}
      </div>
      <div className="space-y-2">
        {group.children.map((child, index) => (
          <div key={index} className="space-y-2">
            {groupingMode ? (
              <button
                ref={(element) => { if (element) childFocusRefs.current.set(index, element); else childFocusRefs.current.delete(index); }}
                type="button"
                aria-pressed={selected.has(index)}
                aria-label={`Select ${child.group ? "group" : "condition"} ${index + 1} for grouping`}
                onClick={() => toggleSelection(index)}
                className={`flex min-h-12 w-full items-center gap-3 rounded-xl border px-4 text-left text-sm transition-colors ${selected.has(index) ? "border-accent bg-accent/15 text-foreground" : "border-border bg-surface text-secondary hover:border-accent/50 hover:text-foreground"}`}
              >
                <span aria-hidden="true" className={`flex h-5 w-5 shrink-0 items-center justify-center rounded-full border ${selected.has(index) ? "border-accent bg-accent text-white" : "border-muted"}`}>{selected.has(index) ? "✓" : ""}</span>
                <span>{child.group ? `${child.group.operator === "OR" ? "Match any" : child.group.operator === "NOT" ? "Exclude" : "Match all"} group` : describeCondition(child.filter ?? {})}</span>
              </button>
            ) : child.group ? (
              <ExpressionGroupEditor
                group={child.group}
                groupPath={[...groupPath, index]}
                criteria={criteria}
                conditionCount={conditionCount}
                onChange={(next) => updateChild(index, { group: next })}
                onAddCondition={onAddCondition}
                onEditCondition={onEditCondition}
                describeCondition={describeCondition}
                parentOperator={group.operator}
                ungroupChildFromParent={() => ungroupChild(index)}
                removeGroupFromParent={() => removeChild(index)}
              />
            ) : (
              <div className="group/condition relative flex min-h-12 items-stretch overflow-visible rounded-xl border border-border bg-surface transition-colors hover:border-accent/40">
                <button ref={(element) => { if (element) childFocusRefs.current.set(index, element); else childFocusRefs.current.delete(index); }} type="button" onClick={() => onEditCondition([...groupPath, index])} data-expression-node-path={[...groupPath, index].join(".")} data-expression-return-focus={`edit-${[...groupPath, index].join(".")}`} className="min-w-0 flex-1 rounded-l-xl px-4 py-3 text-left text-sm leading-5 text-foreground hover:bg-card/60" aria-label={`Edit condition ${index + 1}: ${describeCondition(child.filter ?? {})}`}>
                  {describeCondition(child.filter ?? {})}
                </button>
                <div className="relative flex items-center pr-1">
                  <button ref={(element) => { if (element) conditionMenuButtonRefs.current.set(index, element); else conditionMenuButtonRefs.current.delete(index); }} type="button" aria-label={`More actions for condition ${index + 1}`} aria-expanded={openMenu === index} onClick={() => setOpenMenu((current) => current === index ? null : index)} className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-muted opacity-100 hover:bg-card hover:text-foreground md:opacity-0 md:group-hover/condition:opacity-100 md:group-focus-within/condition:opacity-100"><MoreHorizontal className="h-4 w-4" /></button>
                  {openMenu === index ? (
                    <div role="group" aria-label={`Condition ${index + 1} actions`} className="absolute right-0 top-full z-20 mt-1 min-w-36 rounded-lg border border-border bg-surface p-1 shadow-xl">
                      <button type="button" onClick={() => { setOpenMenu(null); onEditCondition([...groupPath, index]); }} className="block min-h-10 w-full rounded px-3 text-left text-sm hover:bg-card">Edit</button>
                      <button type="button" onClick={() => removeChild(index)} disabled={group.operator === "NOT" && group.children.length === 1} className="block min-h-10 w-full rounded px-3 text-left text-sm text-red-300 hover:bg-red-500/10 disabled:opacity-40">Remove</button>
                    </div>
                  ) : null}
                </div>
              </div>
            )}
          </div>
        ))}
        {group.children.length === 0 ? <p className="px-3 py-5 text-center text-sm text-muted">No conditions in this group.</p> : null}
      </div>
      <div className="flex flex-wrap items-center gap-2 pt-1">
        {groupingMode ? <div className="w-full space-y-2">
          <div className="flex min-h-10 items-center justify-between gap-3">
            <span className="text-sm text-secondary">{selected.size} selected</span>
            <button type="button" onClick={() => { setGroupingMode(false); setSelected(new Set()); }} className="min-h-10 rounded-lg px-3 text-sm text-secondary hover:bg-card hover:text-foreground">Cancel grouping</button>
          </div>
          <div className="grid grid-cols-3 gap-2">
            <button type="button" aria-label="Group selected as match all" disabled={selected.size < 2} onClick={() => groupSelected("AND")} className="min-h-10 rounded-lg border border-border px-2 text-sm text-secondary hover:bg-card hover:text-foreground disabled:opacity-40">Match all</button>
            <button type="button" aria-label="Group selected as match any" disabled={selected.size < 2} onClick={() => groupSelected("OR")} className="min-h-10 rounded-lg border border-border px-2 text-sm text-secondary hover:bg-card hover:text-foreground disabled:opacity-40">Match any</button>
            <button type="button" aria-label="Exclude selected" disabled={selected.size !== 1} onClick={() => groupSelected("NOT")} className="min-h-10 rounded-lg border border-border px-2 text-sm text-secondary hover:bg-card hover:text-foreground disabled:opacity-40">Exclude</button>
          </div>
        </div> : <>
          <button ref={addConditionRef} type="button" disabled={group.operator === "NOT" || conditionCount >= MAX_FILTER_EXPRESSION_CONDITIONS} onClick={() => onAddCondition(undefined, groupPath)} data-expression-return-focus={`add-${groupPath.join(".")}`} className="inline-flex min-h-10 items-center gap-2 rounded-lg px-3 text-sm text-secondary hover:bg-card hover:text-foreground disabled:opacity-40"><Plus className="h-4 w-4" /> Add condition</button>
          {group.operator !== "NOT" && group.children.length > 0 ? <button type="button" disabled={!canCreateNestedGroup} onClick={startGrouping} className="min-h-10 rounded-lg px-3 text-sm text-secondary hover:bg-card hover:text-foreground disabled:opacity-40">Group conditions</button> : null}
        </>}
        {conditionCount >= MAX_FILTER_EXPRESSION_CONDITIONS ? <span className="text-xs text-muted">Maximum of {MAX_FILTER_EXPRESSION_CONDITIONS} conditions reached.</span> : null}
        <span className="sr-only" role="status">{announcement}</span>
      </div>
    </section>
  );
}

function summarizeExpressionCondition(filter: Record<string, unknown>, criteria: CriterionDefinition[]): string {
  const selectedId = typeof filter._criterionId === "string"
    ? filter._criterionId
    : criteria.find((criterion) => getCriterionFilterValue(filter, criterion) !== undefined)?.id;
  const selected = criteria.find((criterion) => criterion.id === selectedId);
  if (!selected) return "Choose a filter";
  const value = getCriterionFilterValue(filter, selected);
  if (selected.type !== "related") return `${selected.label}: ${formatFilterChipValue(selected, value)}`;

  const related = value && typeof value === "object" ? value as RelatedFilterCriterion : {};
  const objectFilter = related.objectFilter && typeof related.objectFilter === "object" ? related.objectFilter as Record<string, unknown> : {};
  const nestedCriteria = [...(selected.relatedContextCriteria ?? []), ...(selected.relatedCriteria?.() ?? getRelatedCriteria(selected.entityType!))];
  const details = nestedCriteria.flatMap((criterion) => {
    const nestedValue = selected.relatedContextCriteria?.some((candidate) => candidate.id === criterion.id)
      ? (related as Record<string, unknown>)[criterion.filterKey]
      : getCriterionFilterValue(objectFilter, criterion);
    return isCriterionValueValid(nestedValue, criterion) ? [`${criterion.label}: ${formatFilterChipValue(criterion, nestedValue)}`] : [];
  });
  const query = related.findFilter?.q?.trim();
  if (query) details.unshift(`Search: ${query}`);
  if (related._savedFilterName) details.unshift(`Saved filter: ${related._savedFilterName}`);
  if (related._matchAll) details.unshift("Any related item");
  const singular = selected.entityType === "performers" ? "performer" : selected.entityType === "videos" ? "video" : "related item";
  const mode = related.mode ?? (related.exclude ? "none" : "atLeastOne");
  const relationship = mode === "every" ? `Every ${singular}` : mode === "none" ? `No ${singular}` : `At least one ${singular}`;
  const separator = related.conditionOperator === "or" ? " OR " : " AND ";
  return `${relationship}${details.length > 0 ? ` — ${details.join(separator)}` : ""}`;
}

interface FilterExplanationNode {
  text: string;
  children?: FilterExplanationNode[];
}

function naturalList(values: string[], conjunction = "and") {
  if (values.length <= 1) return values[0] ?? "";
  if (values.length === 2) return `${values[0]} ${conjunction} ${values[1]}`;
  return `${values.slice(0, -1).join(", ")}, ${conjunction} ${values.at(-1)}`;
}

function explainCriterionValue(criterion: CriterionDefinition, value: unknown, ratingOptions: RatingSystemOptions, metadataServers: MetadataServer[]): string {
  if (!value || typeof value !== "object") return `${criterion.label} is ${String(value ?? "not set")}`;
  if (criterion.type === "remoteId") {
    const remote = value as StringCriterion & { endpoint?: string };
    return `${criterion.label}: ${formatRemoteIdFilterChipValue(remote, { value: remote.endpoint, modifier: remote.modifier }, metadataServers)}`;
  }
  if (["multiId", "rating", "duration", "careerLength", "resolution", "enum", "hash", "tagDuration"].includes(criterion.type)) {
    return `${criterion.label}: ${formatFilterChipValue(criterion, value, undefined, ratingOptions)}`;
  }
  const clause = value as { value?: unknown; value2?: unknown; modifier?: string; _names?: Record<string, string>; _selectedValues?: string[] };
  const displayScalar = (candidate: unknown) => {
    if (typeof candidate === "boolean") return candidate ? "yes" : "no";
    if (typeof candidate === "number") return clause._names?.[String(candidate)] ?? String(candidate);
    return String(candidate ?? "");
  };
  const rawValues = Array.isArray(clause.value)
    ? clause.value.map(displayScalar)
    : clause._selectedValues?.length ? clause._selectedValues : [displayScalar(clause.value)];
  const values = rawValues.filter(Boolean);
  const first = values[0] ?? formatFilterChipValue(criterion, value);
  const second = displayScalar(clause.value2);
  switch (clause.modifier) {
    case "EQUALS": return `${criterion.label} is ${first}`;
    case "NOT_EQUALS": return `${criterion.label} is not ${first}`;
    case "GREATER_THAN": return `${criterion.label} is greater than ${first}`;
    case "LESS_THAN": return `${criterion.label} is less than ${first}`;
    case "BETWEEN": return `${criterion.label} is between ${first} and ${second}`;
    case "NOT_BETWEEN": return `${criterion.label} is not between ${first} and ${second}`;
    case "INCLUDES": return `${criterion.label} includes ${naturalList(values, "or")}`;
    case "INCLUDES_ALL": return `${criterion.label} includes all of ${naturalList(values)}`;
    case "EXCLUDES": return `${criterion.label} excludes ${naturalList(values, "or")}`;
    case "EXCLUDES_ALL": return `${criterion.label} does not include all of ${naturalList(values)}`;
    case "MATCHES_REGEX": return `${criterion.label} matches the pattern ${first}`;
    case "NOT_MATCHES_REGEX": return `${criterion.label} does not match the pattern ${first}`;
    case "IS_NULL": return `${criterion.label} is not set`;
    case "NOT_NULL": return `${criterion.label} is set`;
    case "UNDER_PATH": return `${criterion.label} is under ${first}`;
    case "NOT_UNDER_PATH": return `${criterion.label} is not under ${first}`;
    default: return `${criterion.label} is ${formatFilterChipValue(criterion, value)}`;
  }
}

function explainExpressionLeaf(filter: Record<string, unknown>, criteria: CriterionDefinition[], ratingOptions: RatingSystemOptions, metadataServers: MetadataServer[]): FilterExplanationNode {
  const selected = getExpressionConditionCriterion(filter, criteria);
  if (!selected) return { text: "Unknown filter condition" };
  const value = getCriterionFilterValue(filter, selected);
  if (selected.type !== "related") return { text: explainCriterionValue(selected, value, ratingOptions, metadataServers) };

  const related = value && typeof value === "object" ? value as RelatedFilterCriterion : {};
  const objectFilter = related.objectFilter && typeof related.objectFilter === "object" ? related.objectFilter as Record<string, unknown> : {};
  const contextCriteria = selected.relatedContextCriteria ?? [];
  const nestedCriteria = [...contextCriteria, ...(selected.relatedCriteria?.() ?? getRelatedCriteria(selected.entityType!))];
  const children = nestedCriteria.flatMap((criterion): FilterExplanationNode[] => {
    const nestedValue = contextCriteria.some((candidate) => candidate.id === criterion.id)
      ? (related as Record<string, unknown>)[criterion.filterKey]
      : getCriterionFilterValue(objectFilter, criterion);
    return isCriterionValueValid(nestedValue, criterion) ? [{ text: explainCriterionValue(criterion, nestedValue, ratingOptions, metadataServers) }] : [];
  });
  const query = related.findFilter?.q?.trim();
  if (query) children.unshift({ text: `Text search contains “${query}”` });
  const singular = selected.entityType === "performers" ? "performer" : selected.entityType === "videos" ? "video" : "related item";
  const mode = related.mode ?? (related.exclude ? "none" : "atLeastOne");
  if (children.length === 0) {
    if (mode === "none") return { text: `No ${singular} matches` };
    return { text: `There is at least one ${singular}` };
  }
  const quantifier = mode === "every" ? `Every ${singular}` : mode === "none" ? `No ${singular}` : `At least one ${singular}`;
  const join = related.conditionOperator === "or" ? "any" : "all";
  const savedFilter = related._savedFilterName?.trim() ? ` from saved filter “${related._savedFilterName.trim()}”` : "";
  return { text: `${quantifier} matches ${join} of the following${savedFilter}`, children };
}

function formatExplanationNodeInline(node: FilterExplanationNode): string {
  if (!node.children?.length) return node.text;
  return `${node.text} — ${node.children.map(formatExplanationNodeInline).join("; ")}`;
}

// ===== Related-entity Editor =====

function parseSavedFilterObject(value: string | undefined): Record<string, unknown> {
  if (!value) return {};
  try {
    const parsed = JSON.parse(value);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
  } catch {
    return {};
  }
}

function RelatedFilterWorkspace({
  criterion,
  value,
  onChange,
  selection,
  onSelectionChange,
}: {
  criterion: CriterionDefinition;
  value?: RelatedFilterCriterion;
  onChange: (v: unknown) => void;
  selection: { facet: RelatedFilterChipFacet; nestedCriterionId?: string } | null;
  onSelectionChange: (selection: { facet: RelatedFilterChipFacet; nestedCriterionId?: string } | null) => void;
}) {
  const entityType = criterion.entityType!;
  const singular = entityType === "performers" ? "performer" : "video";
  const plural = entityType === "performers" ? "performers" : "videos";
  const resultPlural = entityType === "performers" ? "videos" : "performers";
  const relativePronoun = entityType === "performers" ? "who" : "that";
  const EntityIcon = entityType === "performers" ? Users : Film;
  const nestedCriteria = useMemo(() => [
    ...(criterion.relatedContextCriteria ?? []),
    ...(criterion.relatedCriteria?.() ?? getRelatedCriteria(entityType)),
  ], [criterion, entityType]);
  const [criteriaSearch, setCriteriaSearch] = useState("");
  const [navigatorFocusKey, setNavigatorFocusKey] = useState<string | null>(null);
  const workspaceRef = useRef<HTMLDivElement>(null);
  const criteriaSearchRef = useRef<HTMLInputElement>(null);
  const criterionButtonRefs = useRef(new Map<string, HTMLButtonElement>());
  const relationshipModeRef = useRef<HTMLSelectElement>(null);
  const matchAnyRef = useRef<HTMLButtonElement>(null);
  const initialSelectionRef = useRef(selection);
  const related = value ?? {};
  const relationshipMode = related.mode ?? (related.exclude ? "none" : "atLeastOne");
  const conditionOperator = related.conditionOperator === "or" ? "or" : "and";
  const objectFilter = related.objectFilter && typeof related.objectFilter === "object"
    ? related.objectFilter as Record<string, unknown>
    : {};
  const selectedCriterion = selection?.facet === "criterion"
    ? nestedCriteria.find((candidate) => candidate.id === selection.nestedCriterionId)
    : undefined;
  const editingSearch = selection?.facet === "search";
  const editingExistence = selection?.facet === "existence";
  const hasEditor = Boolean(selectedCriterion || editingSearch || editingExistence);
  const { data: savedFilters = [], isPending: savedFiltersPending, isError: savedFiltersError } = useQuery({
    queryKey: ["saved-filters", entityType],
    queryFn: () => savedFiltersApi.list(entityType),
  });
  const selectedSavedFilterId = savedFilters.find((savedFilter) => savedFilter.name === related._savedFilterName)?.id ?? "";

  useEffect(() => {
    const initialSelection = initialSelectionRef.current;
    if (initialSelection?.facet === "criterion" || initialSelection?.facet === "search") return;
    const timeout = window.setTimeout(() => {
      if (initialSelection?.facet === "mode") relationshipModeRef.current?.focus();
      else if (initialSelection?.facet === "existence") matchAnyRef.current?.focus();
      else criteriaSearchRef.current?.focus();
    }, 0);
    return () => window.clearTimeout(timeout);
  }, []);

  const update = (patch: Partial<RelatedFilterCriterion>, clearSavedFilterName = false) => {
    const next: Record<string, unknown> = { ...related, ...patch };
    if (clearSavedFilterName) delete next._savedFilterName;
    for (const [key, item] of Object.entries(next)) {
      if (item === undefined) delete next[key];
    }
    onChange(next);
  };

  const select = (nextSelection: { facet: RelatedFilterChipFacet; nestedCriterionId?: string }) => {
    onSelectionChange(nextSelection);
    window.setTimeout(() => {
      const panel = workspaceRef.current?.querySelector<HTMLElement>("[role='tabpanel']");
      getFirstEditorControl(panel)?.focus();
    }, 0);
  };

  const updateNestedCriterion = (nestedCriterion: CriterionDefinition, nextValue: unknown) => {
    if (criterion.relatedContextCriteria?.some((candidate) => candidate.id === nestedCriterion.id)) {
      update({ [nestedCriterion.filterKey]: nextValue, _matchAll: undefined } as Partial<RelatedFilterCriterion>, true);
      return;
    }
    update({
      objectFilter: setCriterionFilterValue(objectFilter, nestedCriterion, nextValue),
      _matchAll: undefined,
    }, true);
  };

  const removeNestedCriterion = (nestedCriterion: CriterionDefinition) => {
    if (criterion.relatedContextCriteria?.some((candidate) => candidate.id === nestedCriterion.id)) {
      update({ [nestedCriterion.filterKey]: undefined } as Partial<RelatedFilterCriterion>, true);
      return;
    }
    update({ objectFilter: removeCriterionFilterValue(objectFilter, nestedCriterion) }, true);
  };

  const getNestedValue = (nestedCriterion: CriterionDefinition) => criterion.relatedContextCriteria?.some((candidate) => candidate.id === nestedCriterion.id)
    ? (related as Record<string, unknown>)[nestedCriterion.filterKey]
    : getCriterionFilterValue(objectFilter, nestedCriterion);

  const chooseSavedFilter = (id: string) => {
    const savedFilter = savedFilters.find((candidate) => String(candidate.id) === id);
    if (!savedFilter) return;
    const findFilter = parseSavedFilterObject(savedFilter.findFilter);
    const savedObjectFilter = parseSavedFilterObject(savedFilter.objectFilter);
    const q = typeof findFilter.q === "string" ? findFilter.q.trim() : "";
    const hasObjectFilter = Object.keys(savedObjectFilter).length > 0;
    onChange({
      ...(q ? { findFilter: { q } } : {}),
      ...(hasObjectFilter ? { objectFilter: savedObjectFilter } : {}),
      ...(related.mode ? { mode: related.mode } : {}),
      ...(related.conditionOperator ? { conditionOperator: related.conditionOperator } : {}),
      ...(related.exclude ? { exclude: true } : {}),
      _savedFilterName: savedFilter.name,
      ...(!q && !hasObjectFilter ? { _matchAll: true } : {}),
    });
    onSelectionChange(null);
  };

  const toggleMatchAll = () => {
    if (related._matchAll) update({ _matchAll: undefined });
    else onChange({
      ...(related.mode ? { mode: related.mode } : {}),
      ...(related.conditionOperator ? { conditionOperator: related.conditionOperator } : {}),
      ...(related.exclude ? { exclude: true } : {}),
      _matchAll: true,
    });
  };

  const filteredCriteria = useMemo(() => {
    const query = criteriaSearch.trim().toLowerCase();
    return nestedCriteria
      .filter((candidate) => !query || candidate.label.toLowerCase().includes(query))
      .slice()
      .sort((left, right) => left.label.localeCompare(right.label));
  }, [criteriaSearch, nestedCriteria]);
  const activeCriteria = filteredCriteria.filter((candidate) => isCriterionValueValid(getNestedValue(candidate), candidate));
  const inactiveCriteria = filteredCriteria.filter((candidate) => !activeCriteria.includes(candidate));
  const activeConditionCount = nestedCriteria.filter((candidate) => isCriterionValueValid(getNestedValue(candidate), candidate)).length
    + (related.findFilter?.q?.trim() ? 1 : 0);
  const showTextSearch = !criteriaSearch.trim() || "text search".includes(criteriaSearch.trim().toLowerCase());
  const visibleNavigatorKeys = [
    ...(showTextSearch ? ["search"] : []),
    ...activeCriteria.map((candidate) => candidate.id),
    ...inactiveCriteria.map((candidate) => candidate.id),
  ];
  const selectedNavigatorKey = editingSearch ? "search" : selectedCriterion?.id;
  const rovingNavigatorKey = navigatorFocusKey && visibleNavigatorKeys.includes(navigatorFocusKey)
    ? navigatorFocusKey
    : selectedNavigatorKey && visibleNavigatorKeys.includes(selectedNavigatorKey)
    ? selectedNavigatorKey
    : visibleNavigatorKeys[0];
  const handleNavigatorKeyDown = (event: ReactKeyboardEvent<HTMLButtonElement>, key: string) => {
    const index = visibleNavigatorKeys.indexOf(key);
    if (event.key === "ArrowUp" && index === 0) {
      event.preventDefault();
      criteriaSearchRef.current?.focus();
      return;
    }
    let nextIndex: number | undefined;
    if (event.key === "ArrowDown") nextIndex = Math.min(visibleNavigatorKeys.length - 1, index + 1);
    if (event.key === "ArrowUp") nextIndex = Math.max(0, index - 1);
    if (event.key === "Home") nextIndex = 0;
    if (event.key === "End") nextIndex = visibleNavigatorKeys.length - 1;
    if (nextIndex === undefined || nextIndex < 0) return;
    event.preventDefault();
    criterionButtonRefs.current.get(visibleNavigatorKeys[nextIndex])?.focus();
  };

  const renderCriterionRow = (nestedCriterion: CriterionDefinition) => {
    const active = isCriterionValueValid(getNestedValue(nestedCriterion), nestedCriterion);
    const selected = selectedCriterion?.id === nestedCriterion.id;
    return (
      <button
        ref={(element) => { if (element) criterionButtonRefs.current.set(nestedCriterion.id, element); else criterionButtonRefs.current.delete(nestedCriterion.id); }}
        key={nestedCriterion.id}
        type="button"
        role="tab"
        aria-selected={selected}
        data-active={active ? "true" : "false"}
        tabIndex={nestedCriterion.id === rovingNavigatorKey ? 0 : -1}
        onClick={() => select({ facet: "criterion", nestedCriterionId: nestedCriterion.id })}
        onFocus={() => setNavigatorFocusKey(nestedCriterion.id)}
        onKeyDown={(event) => handleNavigatorKeyDown(event, nestedCriterion.id)}
        className={`flex min-h-11 w-full items-center gap-3 rounded-lg border px-3 py-2 text-left text-sm transition ${selected ? "border-accent bg-accent/15 text-foreground" : active ? "border-accent/30 bg-accent/5 text-foreground hover:bg-card" : "border-transparent text-secondary hover:border-border hover:bg-card hover:text-foreground"}`}
      >
        <span className="min-w-0 flex-1 truncate font-medium">{nestedCriterion.label}</span>
        {active ? <span className="h-2 w-2 shrink-0 rounded-full bg-accent" aria-hidden="true" /> : null}
      </button>
    );
  };

  return (
    <div ref={workspaceRef} className="flex min-h-0 flex-1 flex-col overflow-hidden">
      <div className="flex flex-col gap-3 border-b border-border px-4 py-3 md:flex-row md:items-center md:justify-between md:px-6">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-foreground">Relationship</h3>
          <p className="text-xs text-muted">{conditionOperator === "or" ? "At least one condition below" : "All conditions below"} must match the same related {singular}.</p>
        </div>
        <div className="flex flex-col gap-2 sm:flex-row">
          <select
            ref={relationshipModeRef}
            aria-label="Relationship match mode"
            value={relationshipMode}
            onChange={(event) => {
              const mode = event.target.value as RelatedFilterCriterion["mode"];
              update({ mode: mode === "atLeastOne" ? undefined : mode, exclude: undefined });
            }}
            className="min-h-11 shrink-0 rounded-lg border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
          >
            <option value="atLeastOne">At least one matching {singular}</option>
            <option value="every">Every {singular} matches</option>
            <option value="none">No {singular} matches</option>
          </select>
          {activeConditionCount >= 2 || conditionOperator === "or" ? (
            <select
              aria-label="Related condition operator"
              value={conditionOperator}
              onChange={(event) => update({ conditionOperator: event.target.value === "or" ? "or" : undefined }, true)}
              className="min-h-11 shrink-0 rounded-lg border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
            >
              <option value="and">Match all conditions</option>
              <option value="or">Match any condition</option>
            </select>
          ) : null}
        </div>
      </div>

      <div className="grid min-h-0 flex-1 overflow-hidden md:grid-cols-[20rem_minmax(0,1fr)]">
        <aside className={`${hasEditor ? "hidden md:flex" : "flex"} min-h-0 flex-col border-border md:border-r`} aria-label={`${criterion.label} criteria`}>
          <div className="space-y-3 border-b border-border p-3 md:p-4">
            <LabeledControl label={`Saved ${singular} filter`}>
              <select
                data-filter-primary-control
                aria-label={`Saved ${singular} filter`}
                value={selectedSavedFilterId}
                onChange={(event) => chooseSavedFilter(event.target.value)}
                className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              >
                <option value="">Choose a saved filter…</option>
                {savedFilters.map((savedFilter) => <option key={savedFilter.id} value={savedFilter.id}>{savedFilter.name}</option>)}
              </select>
            </LabeledControl>
            {savedFiltersPending ? <p className="text-xs text-muted">Loading saved filters…</p> : null}
            {savedFiltersError ? <p className="text-xs text-red-300">Saved filters are unavailable. You can still build the filter here.</p> : null}
            <label className="relative block">
              <span className="sr-only">Search {singular} filter criteria</span>
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
              <input
                ref={criteriaSearchRef}
                type="search"
                aria-label={`Search ${singular} filter criteria`}
                value={criteriaSearch}
                onChange={(event) => setCriteriaSearch(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key !== "ArrowDown" || visibleNavigatorKeys.length === 0) return;
                  event.preventDefault();
                  criterionButtonRefs.current.get(visibleNavigatorKeys[0])?.focus();
                }}
                placeholder={`Search ${singular} filters`}
                className="min-h-11 w-full rounded-lg border border-border bg-input py-2 pl-10 pr-3 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
              />
            </label>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto p-2 md:p-3" role="tablist" aria-label={`Available ${singular} filters`} aria-orientation="vertical">
            {showTextSearch ? (
              <section className="mb-4" aria-label="Quick">
                <h4 className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-muted">Quick</h4>
                <button
                  ref={(element) => { if (element) criterionButtonRefs.current.set("search", element); else criterionButtonRefs.current.delete("search"); }}
                  type="button"
                  role="tab"
                  aria-selected={editingSearch}
                  data-active={related.findFilter?.q?.trim() ? "true" : "false"}
                  tabIndex={rovingNavigatorKey === "search" ? 0 : -1}
                  onClick={() => select({ facet: "search" })}
                  onFocus={() => setNavigatorFocusKey("search")}
                  onKeyDown={(event) => handleNavigatorKeyDown(event, "search")}
                  className={`flex min-h-11 w-full items-center gap-3 rounded-lg border px-3 py-2 text-left text-sm transition ${editingSearch ? "border-accent bg-accent/15 text-foreground" : related.findFilter?.q?.trim() ? "border-accent/30 bg-accent/5 text-foreground hover:bg-card" : "border-transparent text-secondary hover:border-border hover:bg-card hover:text-foreground"}`}
                >
                  <Search className="h-4 w-4 shrink-0" />
                  <span className="font-medium">Text search</span>
                </button>
              </section>
            ) : null}
            {activeCriteria.length > 0 ? (
              <section className="mb-4" aria-label="Active">
                <h4 className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-muted">Active</h4>
                <div className="space-y-1">{activeCriteria.map(renderCriterionRow)}</div>
              </section>
            ) : null}
            {inactiveCriteria.length > 0 ? (
              <section className="mb-4" aria-label={`All ${singular} filters`}>
                <h4 className="px-3 pb-1 text-xs font-semibold uppercase tracking-wide text-muted">All {singular} filters</h4>
                <div className="space-y-1">{inactiveCriteria.map(renderCriterionRow)}</div>
              </section>
            ) : null}
            {!showTextSearch && filteredCriteria.length === 0 ? <div className="px-4 py-10 text-center text-sm text-muted">No filters match “{criteriaSearch}”.</div> : null}
          </div>
        </aside>

        <main className={`${hasEditor ? "flex" : "hidden md:flex"} min-h-0 min-w-0 flex-col`}>
          {hasEditor ? (
            <div role="tabpanel" aria-label={editingSearch ? "Text search" : editingExistence ? `Any ${singular}` : selectedCriterion?.label} className="flex min-h-0 flex-1 flex-col overflow-y-auto p-4 md:p-6">
              <div className="mb-4 flex items-center gap-2 md:hidden">
                <button
                  type="button"
                  data-mobile-only-control
                  onClick={() => { onSelectionChange(null); window.setTimeout(() => criteriaSearchRef.current?.focus(), 0); }}
                  className="inline-flex h-10 w-10 items-center justify-center rounded-lg text-secondary hover:bg-card hover:text-foreground"
                  aria-label="Back to related filter criteria"
                >
                  <ArrowLeft className="h-5 w-5" />
                </button>
                <span className="text-sm font-semibold text-foreground">{editingSearch ? "Text search" : editingExistence ? `Any ${singular}` : selectedCriterion?.label}</span>
              </div>
              {editingSearch ? (
                <div className="max-w-2xl space-y-4">
                  <div>
                    <h4 className="text-base font-semibold text-foreground">Search related {plural}</h4>
                    <p className="mt-1 text-sm text-secondary">Match the related {singular}'s name, title, tags, or other searchable text.</p>
                  </div>
                  <LabeledControl label={`Search related ${plural}`}>
                    <input
                      data-filter-primary-control
                      type="search"
                      aria-label={`Search related ${plural}`}
                      value={related.findFilter?.q ?? ""}
                      onChange={(event) => update({
                        findFilter: event.target.value ? { q: event.target.value } : undefined,
                        _matchAll: undefined,
                      }, true)}
                      placeholder={`Search ${plural}`}
                      className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
                    />
                  </LabeledControl>
                </div>
              ) : editingExistence ? (
                <div className="max-w-2xl space-y-4">
                  <div>
                    <h4 className="text-base font-semibold text-foreground">Any related {singular}</h4>
                    <p className="mt-1 text-sm text-secondary">Require at least one related {singular} without adding another condition.</p>
                  </div>
                  <button
                    ref={matchAnyRef}
                    data-filter-primary-control
                    type="button"
                    aria-pressed={related._matchAll === true}
                    onClick={toggleMatchAll}
                    className={`min-h-11 w-fit rounded-lg border px-4 py-2 text-sm ${related._matchAll ? "border-accent bg-accent/15 text-foreground" : "border-border text-secondary hover:bg-card hover:text-foreground"}`}
                  >
                    Match any related {singular}
                  </button>
                </div>
              ) : selectedCriterion ? (
                <div className="max-w-2xl space-y-4">
                  <div className="flex items-center justify-between gap-3">
                    <h4 className="text-base font-semibold text-foreground">{selectedCriterion.label}</h4>
                    {isCriterionValueValid(getNestedValue(selectedCriterion), selectedCriterion) ? (
                      <button type="button" onClick={() => removeNestedCriterion(selectedCriterion)} className="text-sm text-muted hover:text-foreground">Remove</button>
                    ) : null}
                  </div>
                  <CriterionEditor
                    criterion={selectedCriterion}
                    value={getNestedValue(selectedCriterion)}
                    auxiliaryToggleChecked={selectedCriterion.auxiliaryToggleKey ? Boolean(objectFilter[selectedCriterion.auxiliaryToggleKey]) : undefined}
                    onAuxiliaryToggleChange={(checked) => {
                      if (!selectedCriterion.auxiliaryToggleKey) return;
                      const nextObjectFilter = { ...objectFilter };
                      if (checked) nextObjectFilter[selectedCriterion.auxiliaryToggleKey] = true;
                      else delete nextObjectFilter[selectedCriterion.auxiliaryToggleKey];
                      update({ objectFilter: nextObjectFilter, _matchAll: undefined }, true);
                    }}
                    onChange={(nextValue) => updateNestedCriterion(selectedCriterion, nextValue)}
                  />
                </div>
              ) : null}
            </div>
          ) : (
            <div className="flex flex-1 items-center justify-center p-8 text-center">
              <div className="max-w-sm">
                <EntityIcon className="mx-auto mb-3 h-8 w-8 text-muted" />
                <h4 className="text-lg font-semibold text-foreground">
                  {relationshipMode === "every" ? `Find ${resultPlural} where every ${singular} matches` : relationshipMode === "none" ? `Find ${resultPlural} where no ${singular} matches` : `Find ${resultPlural} by ${singular}`}
                </h4>
                <p className="mt-1 text-sm text-secondary">
                  {relationshipMode === "every"
                    ? `Show ${resultPlural} with at least one ${singular}, where every related ${singular} matches all filters.`
                    : relationshipMode === "none"
                      ? `Show ${resultPlural} with at least one ${singular}, where no related ${singular} matches all filters.`
                      : `Show ${resultPlural} with a ${singular} ${relativePronoun} matches a saved filter, the filters you add here, or both. All filters must match the same ${singular}.`}
                </p>
                {value ? (
                  <button type="button" onClick={() => onChange(undefined)} className="mt-5 text-sm text-muted hover:text-foreground">Clear related filter</button>
                ) : null}
              </div>
            </div>
          )}
        </main>
      </div>
    </div>
  );
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
