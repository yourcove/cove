import type { MetadataServer, RatingSystemOptions, RelatedFilterCriterion, StringCriterion } from "../api/types";
import { formatFilterChipValue, formatRemoteIdFilterChipValue } from "./ActiveObjectFilterChips";
import { getRelatedCriteria } from "./filterCriteriaCatalogs";
import {
  getCriterionFilterValue,
  getExpressionConditionCriterion,
  isCriterionValueValid,
} from "./filterCriterionState";
import type { CriterionDefinition } from "./filterCriteriaTypes";

interface FilterExplanationNode {
  text: string;
  children?: FilterExplanationNode[];
}

function naturalList(values: string[], conjunction = "and") {
  if (values.length <= 1) return values[0] ?? "";
  if (values.length === 2) return `${values[0]} ${conjunction} ${values[1]}`;
  return `${values.slice(0, -1).join(", ")}, ${conjunction} ${values.at(-1)}`;
}

function explainCriterionValue(
  criterion: CriterionDefinition,
  value: unknown,
  ratingOptions: RatingSystemOptions,
  metadataServers: MetadataServer[],
): string {
  if (!value || typeof value !== "object") return `${criterion.label} is ${String(value ?? "not set")}`;
  if (criterion.type === "remoteId") {
    const remote = value as StringCriterion & { endpoint?: string };
    return `${criterion.label}: ${formatRemoteIdFilterChipValue(remote, { value: remote.endpoint, modifier: remote.modifier }, metadataServers)}`;
  }
  if (
    ["multiId", "rating", "duration", "careerLength", "resolution", "enum", "hash", "tagDuration"].includes(
      criterion.type,
    )
  ) {
    return `${criterion.label}: ${formatFilterChipValue(criterion, value, undefined, ratingOptions)}`;
  }
  const clause = value as {
    value?: unknown;
    value2?: unknown;
    modifier?: string;
    _names?: Record<string, string>;
    _selectedValues?: string[];
  };
  const displayScalar = (candidate: unknown) => {
    if (typeof candidate === "boolean") return candidate ? "yes" : "no";
    if (typeof candidate === "number") return clause._names?.[String(candidate)] ?? String(candidate);
    return String(candidate ?? "");
  };
  const rawValues = Array.isArray(clause.value)
    ? clause.value.map(displayScalar)
    : clause._selectedValues?.length
      ? clause._selectedValues
      : [displayScalar(clause.value)];
  const values = rawValues.filter(Boolean);
  const first = values[0] ?? formatFilterChipValue(criterion, value);
  const second = displayScalar(clause.value2);
  switch (clause.modifier) {
    case "EQUALS":
      return `${criterion.label} is ${first}`;
    case "NOT_EQUALS":
      return `${criterion.label} is not ${first}`;
    case "GREATER_THAN":
      return `${criterion.label} is greater than ${first}`;
    case "LESS_THAN":
      return `${criterion.label} is less than ${first}`;
    case "BETWEEN":
      return `${criterion.label} is between ${first} and ${second}`;
    case "NOT_BETWEEN":
      return `${criterion.label} is not between ${first} and ${second}`;
    case "INCLUDES":
      return `${criterion.label} includes ${naturalList(values, "or")}`;
    case "INCLUDES_ALL":
      return `${criterion.label} includes all of ${naturalList(values)}`;
    case "EXCLUDES":
      return `${criterion.label} excludes ${naturalList(values, "or")}`;
    case "EXCLUDES_ALL":
      return `${criterion.label} does not include all of ${naturalList(values)}`;
    case "MATCHES_REGEX":
      return `${criterion.label} matches the pattern ${first}`;
    case "NOT_MATCHES_REGEX":
      return `${criterion.label} does not match the pattern ${first}`;
    case "IS_NULL":
      return `${criterion.label} is not set`;
    case "NOT_NULL":
      return `${criterion.label} is set`;
    case "UNDER_PATH":
      return `${criterion.label} is under ${first}`;
    case "NOT_UNDER_PATH":
      return `${criterion.label} is not under ${first}`;
    default:
      return `${criterion.label} is ${formatFilterChipValue(criterion, value)}`;
  }
}

function explainExpressionLeaf(
  filter: Record<string, unknown>,
  criteria: CriterionDefinition[],
  ratingOptions: RatingSystemOptions,
  metadataServers: MetadataServer[],
): FilterExplanationNode {
  const selected = getExpressionConditionCriterion(filter, criteria);
  if (!selected) return { text: "Unknown filter condition" };
  const value = getCriterionFilterValue(filter, selected);
  if (selected.type !== "related")
    return { text: explainCriterionValue(selected, value, ratingOptions, metadataServers) };

  const related = value && typeof value === "object" ? (value as RelatedFilterCriterion) : {};
  const objectFilter =
    related.objectFilter && typeof related.objectFilter === "object"
      ? (related.objectFilter as Record<string, unknown>)
      : {};
  const contextCriteria = selected.relatedContextCriteria ?? [];
  const nestedCriteria = [
    ...contextCriteria,
    ...(selected.relatedCriteria?.() ?? getRelatedCriteria(selected.entityType!)),
  ];
  const children = nestedCriteria.flatMap((criterion): FilterExplanationNode[] => {
    const nestedValue = contextCriteria.some((candidate) => candidate.id === criterion.id)
      ? (related as Record<string, unknown>)[criterion.filterKey]
      : getCriterionFilterValue(objectFilter, criterion);
    return isCriterionValueValid(nestedValue, criterion)
      ? [{ text: explainCriterionValue(criterion, nestedValue, ratingOptions, metadataServers) }]
      : [];
  });
  const query = related.findFilter?.q?.trim();
  if (query) children.unshift({ text: `Text search contains “${query}”` });
  const singular =
    selected.entityType === "performers" ? "performer" : selected.entityType === "videos" ? "video" : "related item";
  const mode = related.mode ?? (related.exclude ? "none" : "atLeastOne");
  if (children.length === 0) {
    if (mode === "none") return { text: `No ${singular} matches` };
    return { text: `There is at least one ${singular}` };
  }
  const quantifier =
    mode === "every" ? `Every ${singular}` : mode === "none" ? `No ${singular}` : `At least one ${singular}`;
  const join = related.conditionOperator === "or" ? "any" : "all";
  const savedFilter = related._savedFilterName?.trim() ? ` from saved filter “${related._savedFilterName.trim()}”` : "";
  return { text: `${quantifier} matches ${join} of the following${savedFilter}`, children };
}

function formatExplanationNodeInline(node: FilterExplanationNode): string {
  if (!node.children?.length) return node.text;
  return `${node.text} — ${node.children.map(formatExplanationNodeInline).join("; ")}`;
}

export function describeFilterExpressionCondition(
  filter: Record<string, unknown>,
  criteria: CriterionDefinition[],
  ratingOptions: RatingSystemOptions,
  metadataServers: MetadataServer[],
): string {
  return formatExplanationNodeInline(explainExpressionLeaf(filter, criteria, ratingOptions, metadataServers));
}
