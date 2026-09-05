import { system, type GenerateOptions } from "../api/client";
import { loadScrapeApplyPreferences } from "../components/videoScrapeUtils";

export type DownloadSelectionEntity = "Video" | "Image" | "Gallery" | "Audio" | "Text";

export interface BatchDownloadOptions {
  scrapeVideos?: boolean;
  scrapeMetadata?: boolean;
  createMissingTags?: boolean;
  createMissingPerformers?: boolean;
  createMissingStudio?: boolean;
  markOrganized?: boolean;
  allowDuplicateDownloads?: boolean;
  generate?: GenerateOptions;
}

export interface DownloadSelectionItem {
  id: number;
  title?: string | null;
  urls: string[];
  files: Array<unknown>;
}

export interface BatchDownloadIssue {
  kind: "skipped" | "failed";
  label: string;
  reason: string;
}

export interface BatchDownloadResult {
  queuedCount: number;
  issues: BatchDownloadIssue[];
  jobId?: string;
}

export interface QueueImportedUrlDownloadsOptions extends BatchDownloadOptions {
  autoApplyMetadata?: boolean;
}

export const DEFAULT_BATCH_DOWNLOAD_GENERATE_OPTIONS: GenerateOptions = {
  thumbnails: false,
  previews: false,
  sprites: false,
  segments: false,
  segmentThumbnails: false,
  segmentPreviews: false,
  phashes: false,
  md5: false,
  imageThumbnails: false,
  imagePhashes: false,
  audioPhashes: false,
  textPhashes: false,
  overwrite: false,
};

export const DEFAULT_BATCH_DOWNLOAD_OPTIONS: BatchDownloadOptions = {
  scrapeVideos: false,
  allowDuplicateDownloads: false,
  generate: DEFAULT_BATCH_DOWNLOAD_GENERATE_OPTIONS,
};

export function getUndownloadedSelectionItems<T extends DownloadSelectionItem>(items: T[], selectedIds: Set<number>) {
  return items.filter((item) => selectedIds.has(item.id) && item.files.length === 0);
}

export function getBatchDownloadOptionsStorageKey(scope: string) {
  return `cove-batch-download-options:${scope}`;
}

export function loadStoredBatchDownloadOptions(
  storageKey: string,
  fallback: BatchDownloadOptions = DEFAULT_BATCH_DOWNLOAD_OPTIONS,
): BatchDownloadOptions {
  try {
    const raw = localStorage.getItem(storageKey);
    if (!raw) {
      return normalizeBatchDownloadOptions(fallback);
    }

    return normalizeBatchDownloadOptions({
      ...fallback,
      ...JSON.parse(raw),
    });
  } catch {
    return normalizeBatchDownloadOptions(fallback);
  }
}

export function saveStoredBatchDownloadOptions(storageKey: string, options: BatchDownloadOptions) {
  localStorage.setItem(storageKey, JSON.stringify(normalizeBatchDownloadOptions(options)));
}

export function normalizeBatchDownloadOptions(
  options: BatchDownloadOptions = DEFAULT_BATCH_DOWNLOAD_OPTIONS,
): BatchDownloadOptions {
  const preferences = loadScrapeApplyPreferences();
  const scrapeMetadata = !!(options.scrapeMetadata ?? options.scrapeVideos);

  return {
    scrapeVideos: scrapeMetadata,
    scrapeMetadata,
    createMissingTags: options.createMissingTags ?? preferences.createMissingTags,
    createMissingPerformers: options.createMissingPerformers ?? preferences.createMissingPerformers,
    createMissingStudio: options.createMissingStudio ?? preferences.createMissingStudio,
    markOrganized: options.markOrganized ?? preferences.markOrganized,
    allowDuplicateDownloads: !!options.allowDuplicateDownloads,
    generate: {
      ...DEFAULT_BATCH_DOWNLOAD_GENERATE_OPTIONS,
      ...(options.generate ?? {}),
    },
  };
}

export async function queueBatchDownloads(
  entity: DownloadSelectionEntity,
  items: DownloadSelectionItem[],
  options: BatchDownloadOptions = {},
): Promise<BatchDownloadResult> {
  const issues: BatchDownloadIssue[] = [];
  const batchItems: Array<{
    url: string;
    sourceUrl?: string;
    entity: DownloadSelectionEntity;
    entityId: number;
    label: string;
  }> = [];

  for (const item of items) {
    const label = getItemLabel(item, entity);
    const candidateUrls = getItemDownloadUrls(item.urls);
    if (candidateUrls.length === 0) {
      issues.push({ kind: "skipped", label, reason: "No source URL is stored for this item." });
      continue;
    }

    const sourceUrl = candidateUrls[0];
    const primaryDownloadUrl = candidateUrls.find((url) => !areUrlsEqual(url, sourceUrl)) ?? sourceUrl;
    batchItems.push({
      url: primaryDownloadUrl,
      sourceUrl: areUrlsEqual(primaryDownloadUrl, sourceUrl) ? undefined : sourceUrl,
      entity,
      entityId: item.id,
      label,
    });
  }

  if (batchItems.length === 0) {
    return { queuedCount: 0, issues };
  }

  const normalizedOptions = normalizeBatchDownloadOptions(options);
  const response = await system.startBatchDownload({
    items: batchItems,
    followUp: buildBatchFollowUp(entity, normalizedOptions),
  });

  return {
    queuedCount: response.queuedCount,
    issues: [...issues, ...normalizeResponseIssues(response.issues)],
    jobId: response.jobId ?? undefined,
  };
}

export async function queueImportedUrlDownloads(
  entity: DownloadSelectionEntity,
  urls: string[],
  options: QueueImportedUrlDownloadsOptions = {},
): Promise<BatchDownloadResult> {
  const issues: BatchDownloadIssue[] = [];
  const batchItems: Array<{
    url: string;
    entity: DownloadSelectionEntity;
    label: string;
    title: string;
    createEntityIfMissing: boolean;
  }> = [];

  const normalizedOptions = normalizeBatchDownloadOptions({
    ...options,
    scrapeMetadata: options.scrapeMetadata ?? options.scrapeVideos ?? options.autoApplyMetadata,
  });

  for (const sourceUrl of normalizeUrlLines(urls)) {
    const label = deriveImportedItemTitle(sourceUrl);

    batchItems.push({
      url: sourceUrl,
      entity,
      label,
      title: label,
      createEntityIfMissing: true,
    });
  }

  if (batchItems.length === 0) {
    return { queuedCount: 0, issues };
  }

  const response = await system.startBatchDownload({
    items: batchItems,
    followUp: buildBatchFollowUp(entity, normalizedOptions),
    preflightBeforeQueue: false,
  });

  return {
    queuedCount: response.queuedCount,
    issues: [...issues, ...normalizeResponseIssues(response.issues)],
    jobId: response.jobId ?? undefined,
  };
}

export function formatBatchDownloadSummary(entityLabel: string, result: BatchDownloadResult) {
  const skippedCount = result.issues.filter((issue) => issue.kind === "skipped").length;
  const failedCount = result.issues.filter((issue) => issue.kind === "failed").length;
  const parts =
    result.queuedCount > 0
      ? [`Queued ${result.queuedCount} ${entityLabel}${result.queuedCount === 1 ? "" : "s"}.`]
      : [`No ${entityLabel} downloads queued.`];

  if (result.jobId) {
    parts.push(`Batch job ${result.jobId} is running.`);
  }

  if (skippedCount > 0) {
    parts.push(`Skipped ${skippedCount} ${entityLabel}${skippedCount === 1 ? "" : "s"}.`);
  }

  if (failedCount > 0) {
    parts.push(`Failed ${failedCount} ${entityLabel}${failedCount === 1 ? "" : "s"}.`);
  }

  if (result.issues.length > 0) {
    parts.push("");
    parts.push(...result.issues.slice(0, 5).map((issue) => `${issue.label}: ${issue.reason}`));
  }

  return parts.join("\n");
}

function normalizeResponseIssues(issues?: BatchDownloadIssue[] | null): BatchDownloadIssue[] {
  if (!Array.isArray(issues)) {
    return [];
  }

  return issues
    .filter((issue) => issue.kind === "skipped" || issue.kind === "failed")
    .map((issue) => ({
      kind: issue.kind,
      label: issue.label?.trim() || "Batch item",
      reason: issue.reason?.trim() || "No details were returned.",
    }));
}

function getItemLabel(item: DownloadSelectionItem, entity: DownloadSelectionEntity) {
  const trimmedTitle = item.title?.trim();
  if (trimmedTitle) {
    return trimmedTitle;
  }

  return `${entity} ${item.id}`;
}

function normalizeUrlLines(urls: string[]) {
  return urls.map((value) => value.trim()).filter(Boolean);
}

function getItemDownloadUrls(urls: string[]) {
  return [...new Set(normalizeUrlLines(urls))];
}

function areUrlsEqual(left: string, right: string) {
  return left.trim() === right.trim();
}

function deriveImportedItemTitle(url: string) {
  try {
    const parsed = new URL(url);
    const fileName = parsed.pathname.split("/").filter(Boolean).at(-1);
    if (fileName) {
      return decodeURIComponent(fileName)
        .replace(/[._-]+/g, " ")
        .trim();
    }

    return parsed.hostname;
  } catch {
    return url;
  }
}

function buildBatchFollowUp(entity: DownloadSelectionEntity, options: BatchDownloadOptions) {
  const applyMetadata = entity !== "Gallery" && !!options.scrapeMetadata;
  return {
    scrapeVideos: entity === "Video" ? applyMetadata : false,
    autoApplyMetadata: applyMetadata,
    createMissingTags: !!options.createMissingTags,
    createMissingPerformers: !!options.createMissingPerformers,
    createMissingStudio: !!options.createMissingStudio,
    markOrganized: !!options.markOrganized,
    allowDuplicateDownloads: !!options.allowDuplicateDownloads,
    generate: options.generate,
  };
}
