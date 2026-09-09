import { CliError } from "./errors";
import { mergeObjectFilters } from "./entity-list";
import { resolveResultWindow, targetCount } from "./pagination";
import { uniqueSorts } from "./sorts";
import type { CoveClient } from "./client";
import type { ListQueryOptions, ListResult, PaginatedResponse, Performer, Tag, Video } from "./types";

const PAGE_SIZE = 250;
const SINGLE_ONLY_SORTS = new Set(["random", "phash", "perceptual_similarity", "performer_age"]);

function assertPage<T>(value: unknown, resource: string): asserts value is PaginatedResponse<T> {
  const page = value as Partial<PaginatedResponse<T>> | undefined;
  if (!page || !Array.isArray(page.items) || typeof page.totalCount !== "number") {
    throw new CliError("INVALID_RESPONSE", `Cove returned an invalid ${resource} list response.`);
  }
}

async function allPages<T>(client: CoveClient, path: (page: number) => string, resource: string): Promise<T[]> {
  const result: T[] = [];
  for (let pageNumber = 1; ; pageNumber += 1) {
    const page = await client.get<PaginatedResponse<T>>(path(pageNumber));
    assertPage<T>(page, resource);
    result.push(...page.items);
    if (page.items.length === 0 || result.length >= page.totalCount) return result;
  }
}

function isPerformer(value: unknown): value is Performer {
  const candidate = value as Partial<Performer> | undefined;
  return !!candidate && typeof candidate.id === "number" && typeof candidate.name === "string" && Array.isArray(candidate.aliases);
}

function isTag(value: unknown): value is Tag {
  const candidate = value as Partial<Tag> | undefined;
  return !!candidate && typeof candidate.id === "number" && typeof candidate.name === "string" && Array.isArray(candidate.aliases);
}

export async function resolvePerformer(client: CoveClient, reference: string): Promise<Performer> {
  if (/^\d+$/.test(reference)) {
    const performer = await client.get<Performer>(`performers/${reference}`);
    if (!isPerformer(performer)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid performer response.");
    return performer;
  }

  const candidates = await allPages<Performer>(
    client,
    page => `performers?q=${encodeURIComponent(reference)}&page=${page}&perPage=${PAGE_SIZE}&sort=name&direction=asc`,
    "performer",
  );
  const normalized = reference.toLowerCase();
  const exact = candidates.filter(candidate =>
    candidate.name.toLowerCase() === normalized
      || candidate.aliases.some(alias => alias.toLowerCase() === normalized),
  );
  if (exact.length === 0) {
    throw new CliError("PERFORMER_NOT_FOUND", `No performer exactly matches “${reference}”.`, {
      details: { candidates: candidates.slice(0, 10).map(candidate => ({ id: candidate.id, name: candidate.name, disambiguation: candidate.disambiguation })) },
    });
  }
  if (exact.length > 1) {
    throw new CliError("PERFORMER_AMBIGUOUS", `More than one performer exactly matches “${reference}”. Use a performer ID.`, {
      details: { candidates: exact.map(candidate => ({ id: candidate.id, name: candidate.name, disambiguation: candidate.disambiguation })) },
    });
  }
  const performer = await client.get<Performer>(`performers/${exact[0]!.id}`);
  if (!isPerformer(performer)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid performer response.");
  return performer;
}

export async function resolveTag(client: CoveClient, reference: string): Promise<Tag> {
  if (/^\d+$/.test(reference)) {
    const tag = await client.get<Tag>(`tags/${reference}`);
    if (!isTag(tag)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid tag response.");
    return tag;
  }

  const candidates = await allPages<Tag>(
    client,
    page => `tags?q=${encodeURIComponent(reference)}&page=${page}&perPage=${PAGE_SIZE}&sort=name&direction=asc&includeCounts=false`,
    "tag",
  );
  const normalized = reference.toLowerCase();
  const exact = candidates.filter(candidate =>
    candidate.name.toLowerCase() === normalized
      || candidate.aliases.some(alias => alias.toLowerCase() === normalized),
  );
  if (exact.length === 0) {
    throw new CliError("TAG_NOT_FOUND", `No tag exactly matches “${reference}”.`, {
      details: { candidates: candidates.slice(0, 10).map(candidate => ({ id: candidate.id, name: candidate.name })) },
    });
  }
  if (exact.length > 1) {
    throw new CliError("TAG_AMBIGUOUS", `More than one tag exactly matches “${reference}”. Use a tag ID.`, {
      details: { candidates: exact.map(tag => ({ id: tag.id, name: tag.name })) },
    });
  }
  const tag = await client.get<Tag>(`tags/${exact[0]!.id}`);
  if (!isTag(tag)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid tag response.");
  return tag;
}

export interface VideoCriteria {
  tagIds: number[];
  excludedTagIds: number[];
  performerIds: number[];
  excludedPerformerIds: number[];
}

export async function videosForCriteria(client: CoveClient, criteria: VideoCriteria, options: ListQueryOptions = {}): Promise<ListResult<Video>> {
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
  const wireSorts = sorts.length === 1 && options.stabilizeSort !== false && !SINGLE_ONLY_SORTS.has(sorts[0]!.key.toLowerCase())
    ? [...sorts, { key: sorts[0]!.key.toLowerCase() === "updated_at" ? "created_at" : "updated_at", direction: sorts[0]!.direction }]
    : sorts;
  const primarySort = wireSorts[0];
  const searchFilter = options.q ? { q: options.q } : {};
  const baseFindFilter = primarySort
    ? { sort: primarySort.key, direction: primarySort.direction, ...(wireSorts.length > 1 ? { sorts: wireSorts } : {}), ...(primarySort.key.toLowerCase() === "random" ? { seed: options.seed ?? 0 } : {}) }
    : { sort: "random", direction: "asc" as const, seed: options.seed ?? 0 };
  const videos = new Map<number, Video>();
  let expectedTotal: number | undefined;
  let receivedCount = 0;
  for (let pageNumber = window.firstPage; ; pageNumber += 1) {
    const page = await client.post<PaginatedResponse<unknown>>("videos/find", {
      findFilter: { page: pageNumber, perPage: window.perPage, ...searchFilter, ...baseFindFilter },
      objectFilter,
    });
    assertPage(page, "video");
    expectedTotal ??= page.totalCount;
    if (page.totalCount !== expectedTotal) {
      throw new CliError("UNSTABLE_PAGINATION", "The matching video set changed while it was being retrieved. Run the command again.");
    }
    for (const item of page.items) {
      const video = item as Partial<Video> | undefined;
      if (!video || typeof video.id !== "number" || !Array.isArray(video.performers) || !Array.isArray(video.files)) {
        throw new CliError("INVALID_RESPONSE", "Cove returned an invalid video object.");
      }
      videos.set(video.id, video as Video);
    }
    receivedCount += page.items.length;
    const target = targetCount(window, expectedTotal);
    if (window.mode !== "unlimited" || page.items.length === 0 || target !== undefined && receivedCount >= target) break;
  }
  const expectedItems = targetCount(window, expectedTotal);
  if (expectedItems !== undefined && videos.size < expectedItems) {
    throw new CliError("UNSTABLE_PAGINATION", "Cove returned overlapping video pages. Run the command again.", {
      details: { expectedTotal, expectedItems, uniqueVideos: videos.size },
    });
  }
  const items = window.limit === undefined ? [...videos.values()] : [...videos.values()].slice(0, window.limit);
  if (!primarySort) items.sort((left, right) => left.id - right.id);
  return { items, totalCount: expectedTotal };
}
