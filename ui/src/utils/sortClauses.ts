import type { FindFilter, SortClause } from "../api/types";

export const MAX_SORT_CLAUSES = 5;

const ASCENDING_DEFAULT_SORTS = new Set(["code", "path", "studio", "studio_code", "title"]);

export function defaultSortDirection(key: string): SortClause["direction"] {
  return ASCENDING_DEFAULT_SORTS.has(key) ? "asc" : "desc";
}

export function normalizeSortClauses(clauses: SortClause[] | undefined): SortClause[] {
  const seen = new Set<string>();
  const normalized: SortClause[] = [];

  for (const clause of clauses ?? []) {
    const key = clause?.key?.trim();
    if (!key || seen.has(key) || (clause.direction !== "asc" && clause.direction !== "desc")) {
      continue;
    }

    seen.add(key);
    normalized.push({ key, direction: clause.direction });
    if (normalized.length >= MAX_SORT_CLAUSES) break;
  }

  return normalized;
}

export function getSortClauses(filter: FindFilter): SortClause[] {
  const explicit = normalizeSortClauses(filter.sorts);
  if (explicit.length > 0) return explicit;
  if (!filter.sort) return [];
  return [{ key: filter.sort, direction: filter.direction ?? defaultSortDirection(filter.sort) }];
}

export function withSortClauses(filter: FindFilter, clauses: SortClause[]): FindFilter {
  const normalized = normalizeSortClauses(clauses);
  if (normalized.length === 0) {
    const { sort: _sort, direction: _direction, sorts: _sorts, ...rest } = filter;
    return rest;
  }

  const [primary] = normalized;
  if (normalized.length === 1) {
    const { sorts: _sorts, ...rest } = filter;
    return { ...rest, sort: primary.key, direction: primary.direction };
  }

  return {
    ...filter,
    sort: primary.key,
    direction: primary.direction,
    sorts: normalized,
  };
}

export function parseSortClauses(value: string | null | undefined): SortClause[] {
  if (!value) return [];

  return normalizeSortClauses(
    value.split(",").flatMap((part) => {
      const separator = part.lastIndexOf(":");
      if (separator <= 0) return [];
      const key = part.slice(0, separator).trim();
      const direction = part.slice(separator + 1).trim();
      if (direction !== "asc" && direction !== "desc") return [];
      return [{ key, direction }];
    }),
  );
}

export function serializeSortClauses(clauses: SortClause[]): string {
  return normalizeSortClauses(clauses)
    .map((clause) => `${clause.key}:${clause.direction}`)
    .join(",");
}
