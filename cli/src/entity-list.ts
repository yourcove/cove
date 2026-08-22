import type { CoveClient } from "./client";
import { CliError } from "./errors";
import { resolveResultWindow, targetCount } from "./pagination";
import { uniqueSorts } from "./sorts";
import type { ListQueryOptions, ListResult, PaginatedResponse } from "./types";

export function mergeObjectFilters(base: Record<string, unknown>, explicit: Record<string, unknown>): Record<string, unknown> {
  const explicitKeys = new Set(Object.keys(explicit).map(key => key.toLowerCase()));
  return { ...Object.fromEntries(Object.entries(base).filter(([key]) => !explicitKeys.has(key.toLowerCase()))), ...explicit };
}

export async function listEntities<T extends { id: number }>(
  client: CoveClient,
  path: string,
  objectFilter: Record<string, unknown>,
  options: ListQueryOptions,
  resource: string,
  validate: (value: unknown) => value is T,
): Promise<ListResult<T>> {
  const window = resolveResultWindow(options, 25);
  const sorts = uniqueSorts(options.sorts);
  const wireSorts = sorts.length === 1 && options.stabilizeSort !== false && sorts[0]!.key.toLowerCase() !== "random"
    ? [...sorts, { key: sorts[0]!.key.toLowerCase() === "updated_at" ? "created_at" : "updated_at", direction: sorts[0]!.direction }]
    : sorts;
  const primarySort = wireSorts[0];
  const baseFindFilter = primarySort
    ? { sort: primarySort.key, direction: primarySort.direction, ...(wireSorts.length > 1 ? { sorts: wireSorts } : {}), ...(primarySort.key.toLowerCase() === "random" ? { seed: options.seed ?? 0 } : {}) }
    : { sort: "random", direction: "asc" as const, seed: options.seed ?? 0 };
  const searchFilter = options.q ? { q: options.q } : {};
  const items = new Map<number, T>();
  let expectedTotal: number | undefined;
  let receivedCount = 0;
  for (let pageNumber = window.firstPage; ; pageNumber += 1) {
    const page = await client.post<PaginatedResponse<unknown>>(`${path}/find`, {
      findFilter: { page: pageNumber, perPage: window.perPage, ...searchFilter, ...baseFindFilter },
      objectFilter: mergeObjectFilters(options.objectFilter ?? {}, objectFilter),
    });
    if (!page || !Array.isArray(page.items) || typeof page.totalCount !== "number") {
      throw new CliError("INVALID_RESPONSE", `Cove returned an invalid ${resource} list response.`);
    }
    expectedTotal ??= page.totalCount;
    if (page.totalCount !== expectedTotal) {
      throw new CliError("UNSTABLE_PAGINATION", `The matching ${resource} set changed while it was being retrieved. Run the command again.`);
    }
    for (const item of page.items) {
      if (!validate(item)) throw new CliError("INVALID_RESPONSE", `Cove returned an invalid ${resource} object.`);
      items.set(item.id, item);
    }
    receivedCount += page.items.length;
    const target = targetCount(window, expectedTotal);
    if (window.mode !== "unlimited" || page.items.length === 0 || target !== undefined && receivedCount >= target) break;
  }
  const expectedItems = targetCount(window, expectedTotal);
  if (expectedItems !== undefined && items.size < expectedItems) {
    throw new CliError("UNSTABLE_PAGINATION", `Cove returned overlapping ${resource} pages. Run the command again.`, {
      details: { expectedTotal, expectedItems, uniqueItems: items.size },
    });
  }
  const result = window.limit === undefined ? [...items.values()] : [...items.values()].slice(0, window.limit);
  if (!primarySort) result.sort((left, right) => left.id - right.id);
  return { items: result, totalCount: expectedTotal };
}
