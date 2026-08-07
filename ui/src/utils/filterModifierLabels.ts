import type { CriterionModifier } from "../api/types";

const ANY_NONE_ENTITY_TYPES = new Set(["tags", "performers", "studios"]);

export function getMultiIdModifierLabel(
  modifier: CriterionModifier,
  entityType: string | undefined,
  fallback: string,
): string {
  if (!entityType || !ANY_NONE_ENTITY_TYPES.has(entityType)) return fallback;
  if (modifier === "IS_NULL") return "None";
  if (modifier === "NOT_NULL") return "Any";
  return fallback;
}

export function formatMultiIdIncludedValues(
  values: string[],
  modifier: string | undefined,
  entityType: string | undefined,
): string | undefined {
  if (!entityType || !ANY_NONE_ENTITY_TYPES.has(entityType)) return undefined;

  const conjunction = modifier === "INCLUDES_ALL" ? "and" : modifier === "INCLUDES" ? "or" : undefined;
  if (!conjunction || values.length === 0) return undefined;
  if (values.length === 1) return values[0];
  if (values.length === 2) return `${values[0]} ${conjunction} ${values[1]}`;
  return `${values.slice(0, -1).join(", ")}, ${conjunction} ${values.at(-1)}`;
}
