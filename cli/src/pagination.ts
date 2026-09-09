import { CliError } from "./errors";

export interface ResultWindowOptions {
  page?: number;
  perPage?: number;
  limit?: number;
  unlimited?: boolean;
}

export interface ResultWindow {
  mode: "unlimited" | "limit" | "page";
  firstPage: number;
  perPage: number;
  limit?: number;
}

export function resolveResultWindow(options: ResultWindowOptions, pageDefault = 25, batchSize = 40): ResultWindow {
  const paged = options.page !== undefined || options.perPage !== undefined;
  if (options.unlimited && (paged || options.limit !== undefined)) {
    throw new CliError("FILTER_CONFLICT", "--unlimited cannot be combined with --limit, --page, or --per-page.");
  }
  if (options.limit !== undefined && paged) {
    throw new CliError("FILTER_CONFLICT", "--limit cannot be combined with --page or --per-page.");
  }
  if (paged) return { mode: "page", firstPage: options.page ?? 1, perPage: options.perPage ?? pageDefault };
  if (options.limit !== undefined) return { mode: "limit", firstPage: 1, perPage: options.limit, limit: options.limit };
  return { mode: "unlimited", firstPage: 1, perPage: batchSize };
}

export function targetCount(window: ResultWindow, totalCount: number): number | undefined {
  return window.mode === "unlimited" ? totalCount : undefined;
}
