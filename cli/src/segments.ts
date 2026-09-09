import type { CoveClient } from "./client";
import { CliError } from "./errors";
import { resolveResultWindow, targetCount } from "./pagination";
import type { ListResult, PaginatedResponse, SegmentRecord } from "./types";

export interface SegmentListOptions {
  query?: string;
  videoId?: number;
  tagId?: number;
  kind?: string;
  sourceKey?: string;
  page?: number;
  perPage?: number;
  limit?: number;
  unlimited?: boolean;
  sort?: { key: string; direction: "asc" | "desc" };
}

export async function listSegments(client: CoveClient, options: SegmentListOptions): Promise<ListResult<SegmentRecord>> {
  const window = resolveResultWindow(options, 48);
  const items = new Map<number, SegmentRecord>();
  let totalCount: number | undefined;
  let received = 0;
  for (let pageNumber = window.firstPage; ; pageNumber += 1) {
    const parameters = new URLSearchParams({ page: String(pageNumber), perPage: String(window.perPage), sort: options.sort?.key ?? "updated_at", direction: options.sort?.direction ?? "desc" });
    if (options.query) parameters.set("q", options.query);
    if (options.videoId !== undefined) parameters.set("videoId", String(options.videoId));
    if (options.tagId !== undefined) parameters.set("tagId", String(options.tagId));
    if (options.kind) parameters.set("kind", options.kind);
    if (options.sourceKey) parameters.set("sourceKey", options.sourceKey);
    const response = await client.get<PaginatedResponse<unknown>>(`segments?${parameters}`);
    if (!response || !Array.isArray(response.items) || typeof response.totalCount !== "number") throw new CliError("INVALID_RESPONSE", "Cove returned an invalid segment list response.");
    totalCount ??= response.totalCount;
    if (response.totalCount !== totalCount) throw new CliError("UNSTABLE_PAGINATION", "The matching segment set changed while it was being retrieved. Run the command again.");
    for (const value of response.items) {
      if (!isSegmentRecord(value)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid segment object.");
      items.set(value.id, value);
    }
    received += response.items.length;
    const target = targetCount(window, totalCount);
    if (window.mode !== "unlimited" || response.items.length === 0 || target !== undefined && received >= target) break;
  }
  const expectedItems = targetCount(window, totalCount);
  if (expectedItems !== undefined && items.size < expectedItems) throw new CliError("UNSTABLE_PAGINATION", "Cove returned overlapping segment pages. Run the command again.");
  const result = window.limit === undefined ? [...items.values()] : [...items.values()].slice(0, window.limit);
  return { items: result, totalCount };
}

export function isSegmentRecord(value: unknown): value is SegmentRecord {
  const item = value as Partial<SegmentRecord> | undefined;
  return !!item && typeof item.id === "number" && (typeof item.hostType === "string" || typeof item.hostType === "number")
    && typeof item.hostId === "number" && typeof item.startSec === "number" && Number.isFinite(item.startSec) && typeof item.sourceKey === "string";
}
