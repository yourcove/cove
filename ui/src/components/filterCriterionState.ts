import type {
  BoolCriterion,
  CriterionModifier,
  CustomFieldCriterion,
  FilterExpression,
  MultiIdCriterion,
  RelatedFilterCriterion,
  StringCriterion,
  TagDurationClause,
  TagDurationCriterion,
} from "../api/types";
import {
  FILTER_EXPRESSION_STATE_KEY,
  type EditableFilterExpression,
} from "../utils/filterExpressionTree";
import { getRelatedCriteria } from "./filterCriteriaCatalogs";
import { MAX_DISTINCT_RELATED_CONDITIONS, type CriterionDefinition } from "./filterCriteriaTypes";

export const NULL_VALUE_MODIFIERS = new Set<CriterionModifier>(["IS_NULL", "NOT_NULL"]);
const RANGE_VALUE_MODIFIERS = new Set<CriterionModifier>(["BETWEEN", "NOT_BETWEEN"]);

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

export function isCriterionValueValid(value: unknown, criterion: CriterionDefinition) {
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
export function getCriterionFilterValue(filter: Record<string, unknown>, criterion: CriterionDefinition) {
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

export function removeCriterionFilterValue(filter: Record<string, unknown>, criterion: CriterionDefinition) {
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

export function setCriterionFilterValue(filter: Record<string, unknown>, criterion: CriterionDefinition, value: unknown) {
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

export function sanitizeFilterCriteria(filter: Record<string, unknown>, criteria: CriterionDefinition[], baseFilter: Record<string, unknown> = {}) {
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

export function sanitizeFilterExpression(expression: EditableFilterExpression | undefined, criteria: CriterionDefinition[]): FilterExpression<Record<string, unknown>> | undefined {
  if (!expression) return undefined;
  const children: FilterExpression<Record<string, unknown>>["children"] = [];
  for (const child of expression.children) {
    if (child.group) {
      const group = sanitizeFilterExpression(child.group as EditableFilterExpression, criteria);
      if (group && group.children.length > 0) children.push({ group });
      continue;
    }
    if (!child.filter) continue;
    const filter = sanitizeFilterCriteria(child.filter, criteria);
    if (Object.keys(filter).length > 0) children.push({ filter });
  }
  if (expression._semanticNone) {
    if (children.length === 0) return undefined;
    return {
      operator: "NOT",
      children: children.length === 1 ? children : [{ group: { operator: "OR", children } }],
    };
  }
  const operator = expression.operator === "OR" || expression.operator === "JUST_ONE" ? expression.operator : expression.operator === "NOT" ? "NOT" : "AND";
  if (operator === "NOT" && children.length !== 1) return undefined;
  const supportsDistinctPerformerMatches = criteria.some((criterion) => criterion.filterKey === "performerFilterCriterion"
    && criterion.supportsDistinctSiblingMatches);
  const distinctPerformerConditions = supportsDistinctPerformerMatches ? children.filter((child) => {
    const related = child.filter?.performerFilterCriterion;
    if (!related || typeof related !== "object") return false;
    const value = related as { mode?: string; exclude?: boolean };
    return value.exclude !== true && (value.mode === undefined || value.mode === "atLeastOne");
  }).length : 0;
  const keepDistinctMatches = operator === "AND"
    && expression.distinctRelatedMatches
    && distinctPerformerConditions >= 2
    && distinctPerformerConditions <= MAX_DISTINCT_RELATED_CONDITIONS;
  return children.length > 0 ? {
    operator,
    ...(keepDistinctMatches ? { distinctRelatedMatches: true } : {}),
    children,
  } : undefined;
}

export function filterToExpression(filter: Record<string, unknown>, criteria: CriterionDefinition[]): FilterExpression<Record<string, unknown>> {
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

export function expressionPassthroughFilter(filter: Record<string, unknown>, criteria: CriterionDefinition[]) {
  const expressionKeys = new Set(criteria.filter((criterion) => criterion.expressionSupported !== false).flatMap((criterion) => [criterion.filterKey, criterion.secondaryFilterKey, criterion.auxiliaryToggleKey].filter((key): key is string => Boolean(key))));
  return Object.fromEntries(Object.entries(filter).filter(([key]) => key !== FILTER_EXPRESSION_STATE_KEY && !expressionKeys.has(key)));
}

export function countValidFilterExpressionConditions(
  expression: FilterExpression<Record<string, unknown>> | undefined,
  criteria: CriterionDefinition[],
): number {
  if (!expression) return 0;
  return expression.children.reduce((count, child) => count + (child.group
    ? countValidFilterExpressionConditions(child.group, criteria)
    : child.filter && Object.keys(sanitizeFilterCriteria(child.filter, criteria)).length > 0 ? 1 : 0), 0);
}

export function mergeFilterExpressionWithSimpleCriteria(
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

export function getExpressionConditionCriterion(
  filter: Record<string, unknown>,
  criteria: CriterionDefinition[],
): CriterionDefinition | undefined {
  const selectedId = typeof filter._criterionId === "string" ? filter._criterionId : undefined;
  return criteria.find((criterion) => criterion.id === selectedId
    || getCriterionFilterValue(filter, criterion) !== undefined);
}

export interface FilterExpressionCriterionInstance {
  index: number;
  path: number[];
  filter: Record<string, unknown>;
}

export function getExpressionCriterionInstances(
  expression: FilterExpression<Record<string, unknown>> | undefined,
  criterion: CriterionDefinition,
  criteria: CriterionDefinition[],
  parentPath: number[] = [],
): FilterExpressionCriterionInstance[] {
  if (!expression) return [];
  return expression.children.flatMap((child, index) => {
    const path = [...parentPath, index];
    if (child.group) return getExpressionCriterionInstances(child.group, criterion, criteria, path);
    return child.filter && getExpressionConditionCriterion(child.filter, criteria)?.id === criterion.id
      ? [{ index, path, filter: child.filter }]
      : [];
  });
}

export function expressionHasActiveCriterion(
  expression: FilterExpression<Record<string, unknown>> | undefined,
  criterion: CriterionDefinition,
): boolean {
  return expression?.children.some((child) => child.filter
    ? isCriterionValueValid(getCriterionFilterValue(child.filter, criterion), criterion)
    : expressionHasActiveCriterion(child.group, criterion)) ?? false;
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
    && key !== "audioFilterCriterion"
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
