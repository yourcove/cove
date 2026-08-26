import type { CoveClient } from "./client";
import { CliError } from "./errors";
import { mergeObjectFilters } from "./entity-list";
import { resolveResultWindow, targetCount } from "./pagination";
import { uniqueSorts } from "./sorts";
import type { Audio, ListQueryOptions, ListResult, PaginatedResponse } from "./types";

export interface AudioCriteria {
  tagIds: number[];
  excludedTagIds: number[];
  performerIds: number[];
  excludedPerformerIds: number[];
}

export async function audiosForCriteria(client: CoveClient, criteria: AudioCriteria, options: ListQueryOptions = {}): Promise<ListResult<Audio>> {
  const relationFilter: Record<string, unknown> = {};
  if (criteria.tagIds.length || criteria.excludedTagIds.length) {
    relationFilter.tagsCriterion = { value: [], modifier: "includes", requiredIds: criteria.tagIds, excludes: criteria.excludedTagIds };
  }
  if (criteria.performerIds.length || criteria.excludedPerformerIds.length) {
    relationFilter.performersCriterion = { value: [], modifier: "includes", requiredIds: criteria.performerIds, excludes: criteria.excludedPerformerIds };
  }
  const objectFilter = mergeObjectFilters(options.objectFilter ?? {}, relationFilter);

  const window = resolveResultWindow(options, 25);
  const sorts = uniqueSorts(options.sorts);
  const primarySort = sorts[0];
  const searchFilter = options.q ? { q: options.q } : {};
  const baseFindFilter = primarySort
    ? { sort: primarySort.key, direction: primarySort.direction, ...(sorts.length > 1 ? { sorts } : {}), ...(primarySort.key.toLowerCase() === "random" ? { seed: options.seed ?? 0 } : {}) }
    : { sort: "random", direction: "asc" as const, seed: options.seed ?? 0 };
  const audios = new Map<number, Audio>();
  let expectedTotal: number | undefined;
  let receivedCount = 0;
  for (let pageNumber = window.firstPage; ; pageNumber += 1) {
    const page = await client.post<PaginatedResponse<unknown>>("audios/find", {
      findFilter: { page: pageNumber, perPage: window.perPage, ...searchFilter, ...baseFindFilter },
      objectFilter,
    });
    if (!page || !Array.isArray(page.items) || typeof page.totalCount !== "number") {
      throw new CliError("INVALID_RESPONSE", "Cove returned an invalid audio list response.");
    }
    expectedTotal ??= page.totalCount;
    if (page.totalCount !== expectedTotal) {
      throw new CliError("UNSTABLE_PAGINATION", "The matching audio set changed while it was being retrieved. Run the command again.");
    }
    for (const item of page.items) {
      const audio = item as Partial<Audio> | undefined;
      if (!audio || typeof audio.id !== "number" || !Array.isArray(audio.performers) || !Array.isArray(audio.files) || !Array.isArray(audio.tracks)) {
        throw new CliError("INVALID_RESPONSE", "Cove returned an invalid audio object.");
      }
      audios.set(audio.id, audio as Audio);
    }
    receivedCount += page.items.length;
    const target = targetCount(window, expectedTotal);
    if (window.mode !== "unlimited" || page.items.length === 0 || target !== undefined && receivedCount >= target) break;
  }
  const expectedItems = targetCount(window, expectedTotal);
  if (expectedItems !== undefined && audios.size < expectedItems) {
    throw new CliError("UNSTABLE_PAGINATION", "Cove returned overlapping audio pages. Run the command again.", {
      details: { expectedTotal, expectedItems, uniqueAudios: audios.size },
    });
  }
  const items = window.limit === undefined ? [...audios.values()] : [...audios.values()].slice(0, window.limit);
  if (!primarySort) items.sort((left, right) => left.id - right.id);
  return { items, totalCount: expectedTotal };
}
