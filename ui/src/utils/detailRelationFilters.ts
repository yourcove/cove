import type { MultiIdCriterion } from "../api/types";

function isMultiIdCriterion(value: unknown): value is MultiIdCriterion {
  if (!value || typeof value !== "object") return false;
  const criterion = value as Partial<MultiIdCriterion>;
  return Array.isArray(criterion.value) && typeof criterion.modifier === "string";
}

export function constrainMultiIdCriterion(criterion: unknown, requiredId: number): MultiIdCriterion {
  if (!isMultiIdCriterion(criterion)) {
    return { value: [], modifier: "INCLUDES", requiredIds: [requiredId] };
  }

  return { ...criterion, requiredIds: [requiredId] };
}

export function constrainSingleIdCriterion(criterion: unknown, requiredId: number): MultiIdCriterion {
  return constrainMultiIdCriterion(criterion, requiredId);
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
