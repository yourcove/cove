import type { ListQueryOptions } from "./types";

export function uniqueSorts(sorts: ListQueryOptions["sorts"] = []): NonNullable<ListQueryOptions["sorts"]> {
  const seen = new Set<string>();
  return sorts.filter(sort => seen.add(sort.key.toLowerCase()));
}
