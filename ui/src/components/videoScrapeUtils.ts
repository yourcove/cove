import { videos } from "../api/client";
import type { DownloaderMatch, Video, ScrapeAttempt, ScraperSummary } from "../api/types";

export type InputKind = "url" | "name" | "fragment";
export type BatchInputKind = Exclude<InputKind, "fragment">;
export type CollectionMode = "skip" | "merge" | "replace";

export type VideoScrapeVideo = Pick<
  Video,
  | "id"
  | "title"
  | "code"
  | "details"
  | "director"
  | "date"
  | "organized"
  | "studioName"
  | "urls"
  | "tags"
  | "performers"
  | "files"
  | "updatedAt"
>;

export interface ScraperPreference {
  entityType?: string;
  site: string;
  scraperId: string;
}

export interface VideoReviewData {
  title?: string;
  code?: string;
  details?: string;
  director?: string;
  date?: string;
  image?: string;
  studio?: string;
  urls: string[];
  tags: string[];
  performers: string[];
  raw: Record<string, unknown> | null;
}

export interface ScrapeApplyPreferences {
  createMissingTags: boolean;
  createMissingPerformers: boolean;
  createMissingStudio: boolean;
  markOrganized: boolean;
  hydratePerformers: boolean;
}

export interface VideoApplyPlan {
  currentData: VideoReviewData;
  scrapedData: VideoReviewData | null;
  replaceFields: string[];
  collectionModes: Record<string, CollectionMode>;
}

export const DEFAULT_COLLECTION_MODES: Record<string, CollectionMode> = {
  studio: "skip",
  urls: "skip",
  tags: "skip",
  performers: "skip",
};

export const DEFAULT_SCRAPE_APPLY_PREFERENCES: ScrapeApplyPreferences = {
  createMissingStudio: false,
  createMissingTags: false,
  createMissingPerformers: false,
  markOrganized: false,
  hydratePerformers: false,
};

const SCRAPE_PREFERENCES_STORAGE_KEY = "cove.videoScrapePreferences";

export function resolveScrapeApplyDefaults(defaults?: Partial<ScrapeApplyPreferences> | null): ScrapeApplyPreferences {
  return {
    createMissingStudio: defaults?.createMissingStudio ?? DEFAULT_SCRAPE_APPLY_PREFERENCES.createMissingStudio,
    createMissingTags: defaults?.createMissingTags ?? DEFAULT_SCRAPE_APPLY_PREFERENCES.createMissingTags,
    createMissingPerformers:
      defaults?.createMissingPerformers ?? DEFAULT_SCRAPE_APPLY_PREFERENCES.createMissingPerformers,
    markOrganized: defaults?.markOrganized ?? DEFAULT_SCRAPE_APPLY_PREFERENCES.markOrganized,
    hydratePerformers: defaults?.hydratePerformers ?? DEFAULT_SCRAPE_APPLY_PREFERENCES.hydratePerformers,
  };
}

export function loadScrapeApplyPreferences(defaults?: Partial<ScrapeApplyPreferences> | null): ScrapeApplyPreferences {
  const fallback = resolveScrapeApplyDefaults(defaults);
  if (typeof window === "undefined") {
    return fallback;
  }

  try {
    const raw = window.localStorage.getItem(SCRAPE_PREFERENCES_STORAGE_KEY);
    if (!raw) {
      return fallback;
    }

    const parsed = JSON.parse(raw) as Partial<ScrapeApplyPreferences>;
    return {
      createMissingStudio: parsed.createMissingStudio ?? fallback.createMissingStudio,
      createMissingTags: parsed.createMissingTags ?? fallback.createMissingTags,
      createMissingPerformers: parsed.createMissingPerformers ?? fallback.createMissingPerformers,
      markOrganized: parsed.markOrganized ?? fallback.markOrganized,
      hydratePerformers: parsed.hydratePerformers ?? fallback.hydratePerformers,
    };
  } catch {
    return fallback;
  }
}

export function saveScrapeApplyPreferences(preferences: ScrapeApplyPreferences) {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(SCRAPE_PREFERENCES_STORAGE_KEY, JSON.stringify(preferences));
  } catch {
    // Ignore localStorage failures.
  }
}

export function parseJsonObject(json?: string | null): Record<string, unknown> | null {
  if (!json) {
    return null;
  }

  try {
    const parsed = JSON.parse(json);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

export function parseJsonObjectArray(json?: string | null): Record<string, unknown>[] {
  if (!json) {
    return [];
  }

  try {
    const parsed = JSON.parse(json);
    return Array.isArray(parsed)
      ? parsed.filter((item): item is Record<string, unknown> =>
          Boolean(item && typeof item === "object" && !Array.isArray(item)),
        )
      : [];
  } catch {
    return [];
  }
}

export function getValue(object: Record<string, unknown> | null, ...names: string[]) {
  if (!object) {
    return undefined;
  }

  const normalized = names.map((name) => name.toLowerCase());
  for (const [key, value] of Object.entries(object)) {
    if (normalized.includes(key.toLowerCase())) {
      return value;
    }
  }

  return undefined;
}

export function getString(object: Record<string, unknown> | null, ...names: string[]) {
  const value = getValue(object, ...names);
  if (typeof value === "string") {
    const trimmed = value.trim();
    return trimmed ? trimmed : undefined;
  }
  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }
  return undefined;
}

export function getStringList(object: Record<string, unknown> | null, ...names: string[]) {
  const value = getValue(object, ...names);
  if (Array.isArray(value)) {
    return value
      .map((item) =>
        typeof item === "string"
          ? item
          : typeof item === "number" || typeof item === "boolean"
            ? String(item)
            : undefined,
      )
      .filter((item): item is string => Boolean(item?.trim()))
      .map((item) => item.trim())
      .filter(
        (item, index, items) =>
          items.findIndex((candidate) => candidate.toLowerCase() === item.toLowerCase()) === index,
      );
  }
  if (typeof value === "string") {
    return value
      .split(",")
      .map((item) => item.trim())
      .filter(Boolean)
      .filter(
        (item, index, items) =>
          items.findIndex((candidate) => candidate.toLowerCase() === item.toLowerCase()) === index,
      );
  }
  return [];
}

export function getNamedList(object: Record<string, unknown> | null, ...names: string[]) {
  const value = getValue(object, ...names);
  if (typeof value === "string") {
    return value
      .split(",")
      .map((item) => item.trim())
      .filter(Boolean)
      .filter(
        (item, index, items) =>
          items.findIndex((candidate) => candidate.toLowerCase() === item.toLowerCase()) === index,
      );
  }
  if (!Array.isArray(value)) {
    return [];
  }
  const items = value
    .map((item) => {
      if (typeof item === "string") {
        return item.trim();
      }
      if (item && typeof item === "object") {
        const candidate = getString(item as Record<string, unknown>, "Name", "name", "Title", "title");
        return candidate?.trim();
      }
      return undefined;
    })
    .filter((item): item is string => Boolean(item));
  return items.filter(
    (item, index) => items.findIndex((candidate) => candidate.toLowerCase() === item.toLowerCase()) === index,
  );
}

export function normalizeVideoDate(value?: string | null) {
  const trimmed = value?.trim();
  if (!trimmed) {
    return undefined;
  }

  const compact = trimmed.match(/^(\d{4})(\d{2})(\d{2})$/);
  if (compact) {
    return `${compact[1]}-${compact[2]}-${compact[3]}`;
  }

  const iso = trimmed.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (iso) {
    return `${iso[1]}-${iso[2]}-${iso[3]}`;
  }

  const parsed = new Date(trimmed);
  if (Number.isNaN(parsed.getTime())) {
    return trimmed;
  }

  const year = parsed.getFullYear();
  const month = String(parsed.getMonth() + 1).padStart(2, "0");
  const day = String(parsed.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

export function normalizeAttemptData(
  attempt?: ScrapeAttempt | null,
  rawOverride?: Record<string, unknown> | null,
): VideoReviewData | null {
  const raw = rawOverride ?? parseJsonObject(attempt?.resultJson);
  if (!raw) {
    return null;
  }
  return {
    title: getString(raw, "Title", "Name"),
    code: getString(raw, "Code"),
    details: getString(raw, "Details", "Description", "Synopsis"),
    director: getString(raw, "Director"),
    date: normalizeVideoDate(getString(raw, "Date", "ReleaseDate")),
    image: getString(raw, "Image", "ImageUrl", "ImageURL"),
    studio: getNamedList(raw, "Studio", "StudioName")[0] ?? getString(raw, "Studio", "StudioName"),
    urls: getStringList(raw, "URLs", "Url", "URL"),
    tags: getNamedList(raw, "Tags", "Tag", "TagNames"),
    performers: getNamedList(raw, "Performers", "Performer", "PerformerNames"),
    raw,
  };
}

export function getAttemptCandidates(attempt?: ScrapeAttempt | null): VideoReviewData[] {
  const candidatePayloads = parseJsonObjectArray(attempt?.candidateResultsJson);
  if (candidatePayloads.length > 0) {
    return candidatePayloads
      .map((payload) => normalizeAttemptData(undefined, payload))
      .filter((candidate): candidate is VideoReviewData => candidate !== null);
  }

  const single = normalizeAttemptData(attempt);
  return single ? [single] : [];
}

export function normalizeVideoSnapshot(video: VideoScrapeVideo, attempt?: ScrapeAttempt | null): VideoReviewData {
  const snapshot = parseJsonObject(attempt?.entitySnapshotJson);
  return {
    title: getString(snapshot, "title") ?? video.title,
    code: getString(snapshot, "code") ?? video.code,
    details: getString(snapshot, "details") ?? video.details,
    director: getString(snapshot, "director") ?? video.director,
    date: normalizeVideoDate(getString(snapshot, "date") ?? video.date),
    image: getString(snapshot, "image", "imageUrl", "imageURL") ?? videos.screenshotUrl(video.id, video.updatedAt),
    studio: getString(snapshot, "studio") ?? video.studioName,
    urls: getStringList(snapshot, "urls").length > 0 ? getStringList(snapshot, "urls") : video.urls,
    tags:
      getNamedList(snapshot, "tags").length > 0 ? getNamedList(snapshot, "tags") : video.tags.map((tag) => tag.name),
    performers:
      getNamedList(snapshot, "performers").length > 0
        ? getNamedList(snapshot, "performers")
        : video.performers.map((performer) => performer.name),
    raw: snapshot,
  };
}

export function buildFragmentDraft(video: VideoScrapeVideo) {
  return JSON.stringify(
    {
      title: video.title ?? "",
      name: video.title ?? video.files[0]?.basename ?? "",
      filename: video.files[0]?.basename ?? "",
      path: video.files[0]?.path ?? "",
      code: video.code ?? "",
      details: video.details ?? "",
      director: video.director ?? "",
      date: video.date ?? "",
      url: video.urls[0] ?? "",
      urls: video.urls,
      studio: video.studioName ?? "",
    },
    null,
    2,
  );
}

export function supportsScrapeKind(scraper: ScraperSummary | undefined, kind: InputKind) {
  if (!scraper) {
    return false;
  }
  const required = kind === "url" ? "url" : kind === "name" ? "name" : "fragment";
  return scraper.supportedScrapes.some((value) => value.toLowerCase() === required);
}

export function matchesUrlPattern(scraper: ScraperSummary, url: string) {
  const normalizedUrl = url.trim().toLowerCase();
  if (!normalizedUrl) {
    return false;
  }

  return scraper.urls.some((pattern) => {
    const normalizedPattern = pattern.trim().toLowerCase();
    if (!normalizedPattern) {
      return false;
    }

    const fragments = normalizedPattern.split("*").filter(Boolean);
    return fragments.length > 0 && fragments.every((fragment) => normalizedUrl.includes(fragment));
  });
}

const DIRECT_ASSET_EXTENSIONS = new Set([
  "jpg",
  "jpeg",
  "png",
  "gif",
  "webp",
  "bmp",
  "svg",
  "mp4",
  "webm",
  "m4v",
  "mov",
  "avi",
  "mkv",
  "mp3",
  "m4a",
  "wav",
  "flac",
  "ogg",
  "opus",
  "pdf",
  "txt",
  "epub",
]);

const PAGE_LIKE_PATH_HINTS = [
  "/comments/",
  "/comment/",
  "/post/",
  "/posts/",
  "/watch",
  "/video/",
  "/videos/",
  "/gallery/",
  "/galleries/",
  "/album/",
  "/story/",
  "/read/",
  "/performer/",
  "/model/",
  "/s/",
];

const CDN_HOST_HINTS = ["preview.", "cdn.", "static.", "media.", "img.", "images.", "i."];

export function normalizeSourceUrls(urls: string[]) {
  return urls
    .map((value) => value.trim())
    .filter(Boolean)
    .filter(
      (value, index, items) =>
        items.findIndex((candidate) => candidate.toLowerCase() === value.toLowerCase()) === index,
    );
}

export function getScraperUrlMatchScore(scraper: ScraperSummary | undefined, url: string) {
  const normalizedUrl = url.trim().toLowerCase();
  if (!scraper || !normalizedUrl) {
    return 0;
  }

  return scraper.urls.reduce((bestScore, pattern) => {
    const normalizedPattern = pattern.trim().toLowerCase();
    if (!normalizedPattern) {
      return bestScore;
    }

    const fragments = normalizedPattern.split("*").filter(Boolean);
    if (fragments.length === 0 || !fragments.every((fragment) => normalizedUrl.includes(fragment))) {
      return bestScore;
    }

    const score = fragments.length * 1000 + fragments.reduce((sum, fragment) => sum + fragment.length, 0);
    return Math.max(bestScore, score);
  }, 0);
}

function getSourceUrlPreferenceScore(url: string) {
  const normalizedUrl = url.trim();
  if (!normalizedUrl) {
    return 0;
  }

  try {
    const parsed = new URL(normalizedUrl);
    const host = parsed.hostname.toLowerCase();
    const path = parsed.pathname.toLowerCase().replace(/\/+$/, "");
    const segments = path.split("/").filter(Boolean);
    const extensionMatch = path.match(/\.([a-z0-9]{2,5})$/i);
    const extension = extensionMatch?.[1]?.toLowerCase();

    let score = 0;

    if (PAGE_LIKE_PATH_HINTS.some((hint) => path.includes(hint))) {
      score += 40;
    }

    if (segments.length >= 2) {
      score += 8;
    }

    if (parsed.search.length > 0) {
      score += 2;
    }

    if (extension && DIRECT_ASSET_EXTENSIONS.has(extension)) {
      score -= 30;
    }

    if (
      CDN_HOST_HINTS.some((hint) => host.startsWith(hint)) ||
      host.includes(".cdn.") ||
      host.includes(".static.") ||
      host.includes(".media.")
    ) {
      score -= 20;
    }

    return score;
  } catch {
    return 0;
  }
}

export function pickBestSourceUrl(urls: string[], scraper?: ScraperSummary) {
  const normalizedUrls = normalizeSourceUrls(urls);
  if (normalizedUrls.length === 0) {
    return undefined;
  }

  return [...normalizedUrls].sort((left, right) => {
    const matchScoreDelta = getScraperUrlMatchScore(scraper, right) - getScraperUrlMatchScore(scraper, left);
    if (matchScoreDelta !== 0) {
      return matchScoreDelta;
    }

    const preferenceDelta = getSourceUrlPreferenceScore(right) - getSourceUrlPreferenceScore(left);
    if (preferenceDelta !== 0) {
      return preferenceDelta;
    }

    return normalizedUrls.indexOf(left) - normalizedUrls.indexOf(right);
  })[0];
}

export function getScraperSiteKey(value: string | undefined) {
  const trimmed = value?.trim().toLowerCase();
  if (!trimmed) {
    return "";
  }

  try {
    const parsed = new URL(
      trimmed.startsWith("http://") || trimmed.startsWith("https://") ? trimmed : `https://${trimmed}`,
    );
    return parsed.hostname.replace(/^www\./, "");
  } catch {
    return (
      trimmed
        .replace(/^https?:\/\//, "")
        .replace(/^www\./, "")
        .split(/[/?#*]/)[0] ?? ""
    );
  }
}

export function listsEqual(left: string[], right: string[]) {
  if (left.length !== right.length) {
    return false;
  }
  const normalizedLeft = [...left].map((item) => item.toLowerCase()).sort();
  const normalizedRight = [...right].map((item) => item.toLowerCase()).sort();
  return normalizedLeft.every((item, index) => item === normalizedRight[index]);
}

export function buildDefaultVideoApplyPlan(
  video: VideoScrapeVideo,
  attempt?: ScrapeAttempt | null,
  selectedPayload?: Record<string, unknown> | null,
): VideoApplyPlan {
  const currentData = normalizeVideoSnapshot(video, attempt);
  const scrapedData = normalizeAttemptData(attempt, selectedPayload);

  if (!scrapedData) {
    return {
      currentData,
      scrapedData: null,
      replaceFields: [],
      collectionModes: { ...DEFAULT_COLLECTION_MODES },
    };
  }

  const replaceFields: string[] = [];
  if (scrapedData.title && scrapedData.title !== currentData.title) replaceFields.push("title");
  if (scrapedData.code && scrapedData.code !== currentData.code) replaceFields.push("code");
  if (scrapedData.details && scrapedData.details !== currentData.details) replaceFields.push("details");
  if (scrapedData.director && scrapedData.director !== currentData.director) replaceFields.push("director");
  if (scrapedData.date && scrapedData.date !== currentData.date) replaceFields.push("date");
  if (scrapedData.image) replaceFields.push("image");

  return {
    currentData,
    scrapedData,
    replaceFields,
    collectionModes: {
      studio: scrapedData.studio && scrapedData.studio !== currentData.studio ? "replace" : "skip",
      urls: scrapedData.urls.length > 0 && !listsEqual(scrapedData.urls, currentData.urls) ? "merge" : "skip",
      tags: scrapedData.tags.length > 0 && !listsEqual(scrapedData.tags, currentData.tags) ? "merge" : "skip",
      performers:
        scrapedData.performers.length > 0 && !listsEqual(scrapedData.performers, currentData.performers)
          ? "merge"
          : "skip",
    },
  };
}

function getScraperSpecificity(scraper: ScraperSummary, videoUrl?: string) {
  const normalizedUrl = videoUrl?.trim().toLowerCase();
  if (!normalizedUrl) {
    return 0;
  }

  return scraper.urls.reduce((bestScore, pattern) => {
    const normalizedPattern = pattern.trim().toLowerCase();
    if (!normalizedPattern) {
      return bestScore;
    }

    const fragments = normalizedPattern.split("*").filter(Boolean);
    if (fragments.length === 0 || !fragments.every((fragment) => normalizedUrl.includes(fragment))) {
      return bestScore;
    }

    const score = fragments.length * 1000 + fragments.reduce((sum, fragment) => sum + fragment.length, 0);
    return Math.max(bestScore, score);
  }, 0);
}

function getConfiguredScraperId(
  scrapers: ScraperSummary[],
  videoUrl: string | undefined,
  scraperPreferences: ScraperPreference[],
) {
  const site = getScraperSiteKey(videoUrl);
  if (!site) {
    return "";
  }

  const entityType = scrapers[0]?.entityType?.toLowerCase() ?? "";
  const configuredScraperId =
    scraperPreferences.find(
      (preference) => preference.site === site && (preference.entityType?.toLowerCase() ?? "") === entityType,
    )?.scraperId ??
    scraperPreferences.find((preference) => preference.site === site && !preference.entityType)?.scraperId;
  return configuredScraperId && scrapers.some((scraper) => scraper.id === configuredScraperId)
    ? configuredScraperId
    : "";
}

export function sortScrapersForVideo(
  scrapers: ScraperSummary[],
  videoUrl: string | undefined,
  scraperPreferences: ScraperPreference[] = [],
) {
  const configuredScraperId = getConfiguredScraperId(scrapers, videoUrl, scraperPreferences);

  return [...scrapers].sort((left, right) => {
    const leftConfigured = configuredScraperId !== "" && left.id === configuredScraperId;
    const rightConfigured = configuredScraperId !== "" && right.id === configuredScraperId;
    if (leftConfigured !== rightConfigured) {
      return leftConfigured ? -1 : 1;
    }

    const specificityDelta = getScraperSpecificity(right, videoUrl) - getScraperSpecificity(left, videoUrl);
    if (specificityDelta !== 0) {
      return specificityDelta;
    }

    return left.name.localeCompare(right.name);
  });
}

export function findPreferredScraperId(
  scrapers: ScraperSummary[],
  videoUrl: string | undefined,
  scraperPreferences: ScraperPreference[] = [],
) {
  if (scrapers.length === 0) {
    return "";
  }

  return sortScrapersForVideo(scrapers, videoUrl, scraperPreferences)[0]?.id ?? "";
}

export function findDefaultKind(scraper: ScraperSummary | undefined, preferred: InputKind): InputKind {
  if (!scraper) {
    return preferred;
  }
  if (supportsScrapeKind(scraper, preferred)) {
    return preferred;
  }
  if (supportsScrapeKind(scraper, "url")) {
    return "url";
  }
  if (supportsScrapeKind(scraper, "name")) {
    return "name";
  }
  return "fragment";
}

export function sortDownloaderMatches(matches: DownloaderMatch[]) {
  return [...matches].sort((left, right) => {
    const leftLabel = (left.label || left.downloaderName).toLowerCase();
    const rightLabel = (right.label || right.downloaderName).toLowerCase();
    const labelDelta = leftLabel.localeCompare(rightLabel);
    if (labelDelta !== 0) {
      return labelDelta;
    }

    return left.downloaderId.localeCompare(right.downloaderId);
  });
}

export function getVideoScrapeInput(video: VideoScrapeVideo, inputKind: BatchInputKind) {
  if (inputKind === "url") {
    return video.urls[0]?.trim() ?? "";
  }

  return getVideoNameSearchInput(video);
}

export function getVideoNameSearchInput(video: VideoScrapeVideo) {
  const raw = video.title?.trim() || video.files[0]?.basename?.trim() || "";
  if (!raw) {
    return "";
  }

  const sanitized = raw
    .replace(/[\\/_:|]+/g, " ")
    .replace(/\s+/g, " ")
    .trim();

  return sanitized || raw;
}

export function getVideoLabel(video: VideoScrapeVideo) {
  return video.title || video.files[0]?.basename || `Video ${video.id}`;
}
