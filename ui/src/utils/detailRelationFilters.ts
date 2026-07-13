import type { MultiIdCriterion } from "../api/types";

const IMPOSSIBLE_RELATION_ID = 2147483647;

function isMultiIdCriterion(value: unknown): value is MultiIdCriterion {
  if (!value || typeof value !== "object") return false;
  const criterion = value as Partial<MultiIdCriterion>;
  return Array.isArray(criterion.value) && typeof criterion.modifier === "string";
}

function uniqueIds(ids: number[]) {
  return [...new Set(ids.filter((id) => Number.isFinite(id) && id > 0))];
}

function impossibleCriterion(): MultiIdCriterion {
  return { value: [IMPOSSIBLE_RELATION_ID], modifier: "INCLUDES" };
}

export function constrainMultiIdCriterion(criterion: unknown, requiredId: number): MultiIdCriterion {
  if (!isMultiIdCriterion(criterion)) {
    return { value: [requiredId], modifier: "INCLUDES" };
  }

  const values = uniqueIds(criterion.value);
  const excludes = uniqueIds(criterion.excludes ?? []);

  switch (criterion.modifier) {
    case "IS_NULL":
      return impossibleCriterion();
    case "EXCLUDES":
      if (values.includes(requiredId) || excludes.includes(requiredId)) return impossibleCriterion();
      return { value: [requiredId], modifier: "INCLUDES", excludes: values };
    case "EXCLUDES_ALL":
      return { value: [requiredId], modifier: "INCLUDES", excludes };
    case "NOT_NULL":
      return { value: [requiredId], modifier: "INCLUDES", excludes };
    case "INCLUDES":
    case "INCLUDES_ALL":
    default:
      return { ...criterion, value: uniqueIds([requiredId, ...values]), modifier: "INCLUDES_ALL", excludes };
  }
}

export function constrainSingleIdCriterion(criterion: unknown, requiredId: number): MultiIdCriterion {
  if (!isMultiIdCriterion(criterion)) {
    return { value: [requiredId], modifier: "INCLUDES" };
  }

  const values = uniqueIds(criterion.value);
  const excludes = uniqueIds(criterion.excludes ?? []);

  switch (criterion.modifier) {
    case "IS_NULL":
      return impossibleCriterion();
    case "EXCLUDES":
    case "EXCLUDES_ALL":
      return values.includes(requiredId) || excludes.includes(requiredId)
        ? impossibleCriterion()
        : { value: [requiredId], modifier: "INCLUDES" };
    case "NOT_NULL":
      return excludes.includes(requiredId) ? impossibleCriterion() : { value: [requiredId], modifier: "INCLUDES" };
    case "INCLUDES":
    case "INCLUDES_ALL":
    default:
      return values.length === 0 || values.includes(requiredId)
        ? { value: [requiredId], modifier: "INCLUDES" }
        : impossibleCriterion();
  }
}

export function withRequiredMultiId<TFilter extends object>(filter: TFilter, key: keyof TFilter & string, requiredId: number, depth?: number): TFilter {
  const source = filter as Record<string, unknown>;
  const criterion = constrainMultiIdCriterion(source[key], requiredId);
  return {
    ...source,
    [key]: depth === undefined ? criterion : { ...criterion, depth },
  } as TFilter;
}

export function withRequiredSingleId<TFilter extends object>(filter: TFilter, key: keyof TFilter & string, requiredId: number, depth?: number): TFilter {
  const source = filter as Record<string, unknown>;
  const criterion = constrainSingleIdCriterion(source[key], requiredId);
  return {
    ...source,
    [key]: depth === undefined ? criterion : { ...criterion, depth },
  } as TFilter;
}
