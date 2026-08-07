import { useCallback, useMemo, useState, useRef } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { videos, scrapeAttempts, system } from "../api/client";
import type { ApplyVideoScrapeAttemptRequest, Video, MetadataServer, MetadataServerVideoMatch, MetadataServerVideoImportRequest, ScrapeAttempt, ScraperSummary, ScrapeCollectionItemSelection } from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";
import { formatDuration, getResolutionLabel } from "./shared";
import { createNestedRouteLinkProps } from "./cardNavigation";
import { buildFragmentDraft, findDefaultKind, getVideoNameSearchInput, supportsScrapeKind, type CollectionMode, type InputKind } from "./videoScrapeUtils";
import { buildMatchInfo, buildRelationSelectionPayload, relationKey, ScrapeRelationChoices, type ScrapeRelationActionMap } from "./ScrapeRelationChoices";
import { invalidateVideoMetadataQueries } from "./videoMetadataQueryInvalidation";
import {
  CompactCollectionDecision,
  CompactImageDecision,
  CompactListValue,
  CompactScalarDecision,
  DEFAULT_TAGGER_BLACKLIST,
  RemoteRefreshButtons,
  TaggerSettingsPanel,
  TaggerToolbar,
  cleanTaggerQueryString,
  type TaggerQueryMode,
} from "./TaggerShared";
import {
  Search,
  Loader2,
  Check,
  X,
  Plus,
  Minus,
  AlertCircle,
  CloudDownload,
  Fingerprint,
  Settings2,
  EyeOff,
  Eye,
  Upload,
  CloudUpload,
} from "lucide-react";
import { toggleOptionsFromEvent, withOrderedToggle, type MultiSelectToggleOptions } from "../hooks/useMultiSelect";
import { VideoPreviewThumbnail } from "./VideoPreviewThumbnail";

interface VideoTaggerProps {
  videos: Video[];
  onNavigate?: (videoId: number) => void;
  selectedIds?: Set<number>;
  selecting?: boolean;
  onSelect?: (videoId: number, options?: MultiSelectToggleOptions) => void;
  mode?: "bulk" | "detail";
}

interface TaggerConfig {
  selectedEndpoint: string;
  showUnmatched: boolean;
  setCoverImage: boolean;
  setTags: boolean;
  setPerformers: boolean;
  setStudio: boolean;
  onlyExistingTags: boolean;
  onlyExistingPerformers: boolean;
  onlyExistingStudio: boolean;
  markOrganized: boolean;
  preferFingerprints: boolean;
  queryMode: TaggerQueryMode;
  defaultScraperInputKind: InputKind | "auto";
  blacklist: string[];
  createParentStudios: boolean;
  createParentTags: boolean;
  showMales: boolean;
  performerGenders: string[];
}

interface VideoSearchState {
  loading: boolean;
  results?: UnifiedVideoMatch[];
  error?: string;
  selectedIndex?: number;
  saved?: boolean;
  excludedPerformers?: Set<string>;
  excludedTags?: Set<string>;
  skipStudio?: boolean;
  forceIncludedPerformers?: Set<string>;
  forceIncludedTags?: Set<string>;
  forceIncludeStudio?: boolean;
  fieldStrategies?: Record<string, VideoFieldStrategy>;
  collectionModes?: Record<string, CollectionMode>;
}

type VideoFieldStrategy = "ignore" | "merge" | "overwrite";

type TaggerSource =
  | { kind: "metadata-server"; value: string; label: string; endpoint: string }
  | { kind: "scraper"; value: string; label: string; scraper: ScraperSummary };

interface UnifiedVideoMatch extends MetadataServerVideoMatch {
  sourceKind: "metadata-server" | "scraper";
  scrapeAttemptId?: string;
  selectedCandidateIndex?: number;
  rawResult?: Record<string, unknown>;
}

const sourceValue = (kind: "metadata-server" | "scraper", id: string) => `${kind}:${id}`;

function normalizeEndpoint(endpoint?: string | null): string {
  return (endpoint ?? "").trim().replace(/\/+$/, "").toLowerCase();
}

function resolveSource(value: string, sources: TaggerSource[]): TaggerSource | undefined {
  return sources.find((source) => source.value === value)
    ?? sources.find((source) => source.kind === "metadata-server" && source.endpoint === value)
    ?? sources[0];
}

function asString(value: unknown): string | undefined {
  if (typeof value === "string") return value.trim() || undefined;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return undefined;
}

function asStringList(value: unknown): string[] {
  if (Array.isArray(value)) {
    return value.flatMap(asStringList).filter(Boolean);
  }
  const text = asString(value);
  if (!text) return [];
  return text.split(",").map((item) => item.trim()).filter(Boolean);
}

function pickString(result: Record<string, unknown>, ...keys: string[]) {
  const entries = Object.entries(result);
  for (const key of keys) {
    const entry = entries.find(([entryKey]) => entryKey.toLowerCase() === key.toLowerCase());
    if (!entry) continue;
    const value = asString(entry[1]);
    if (value) return value;
  }
  return undefined;
}

function pickStringList(result: Record<string, unknown>, ...keys: string[]) {
  const entries = Object.entries(result);
  for (const key of keys) {
    const entry = entries.find(([entryKey]) => entryKey.toLowerCase() === key.toLowerCase());
    if (!entry) continue;
    const values = asStringList(entry[1]);
    if (values.length > 0) return [...new Set(values)];
  }
  return [];
}

function parseAttemptResults(attempt: ScrapeAttempt): Record<string, unknown>[] {
  try {
    if (attempt.candidateResultsJson) {
      const candidates = JSON.parse(attempt.candidateResultsJson);
      if (Array.isArray(candidates)) return candidates.filter((item): item is Record<string, unknown> => item && typeof item === "object" && !Array.isArray(item));
    }
    if (attempt.resultJson) {
      const result = JSON.parse(attempt.resultJson);
      if (result && typeof result === "object" && !Array.isArray(result)) return [result as Record<string, unknown>];
    }
  } catch {
    return [];
  }
  return [];
}

function toCandidates(names: string[]) {
  // existsLocally is a placeholder here; scraper results don't know local state at parse time. The
  // row enriches it from the backend resolve-relations matcher (see the resolvedRelations query).
  return names.map((name) => ({ remoteId: name, name, existsLocally: false }));
}

function toScraperVideoMatch(attempt: ScrapeAttempt, result: Record<string, unknown>, index: number, scraper: ScraperSummary): UnifiedVideoMatch {
  const title = pickString(result, "Title", "Name");
  const imageUrl = pickString(result, "Image", "ImageUrl", "ImageURL");
  const performerNames = pickStringList(result, "Performers", "Performer", "PerformerNames");
  const tagNames = pickStringList(result, "Tags", "Tag", "TagNames");
  const studioName = pickString(result, "Studio", "StudioName");
  return {
    sourceKind: "scraper",
    scrapeAttemptId: attempt.id,
    selectedCandidateIndex: index,
    rawResult: result,
    endpoint: scraper.id,
    serverName: scraper.name,
    id: `${attempt.id}:${index}`,
    title,
    code: pickString(result, "Code"),
    date: pickString(result, "Date", "ReleaseDate"),
    director: pickString(result, "Director"),
    details: pickString(result, "Details", "Description", "Synopsis"),
    studioName,
    imageUrl,
    duration: undefined,
    performerNames,
    tagNames,
    urls: pickStringList(result, "URLs", "Url", "URL"),
    fingerprintAlgorithms: [],
    matchCount: 0,
    fingerprints: [],
    studioCandidate: studioName ? { remoteId: studioName, name: studioName, existsLocally: false } : undefined,
    performerCandidates: toCandidates(performerNames),
    tagCandidates: toCandidates(tagNames),
  };
}

function getVideoTagNames(video: Video) {
  return video.tags.map((tag) => tag.name).filter(Boolean);
}

function getVideoPerformerNames(video: Video) {
  return video.performers.map((performer) => performer.name).filter(Boolean);
}

function normalizeDecisionValue(value?: string | null) {
  return value?.trim() ?? "";
}

function buildDefaultVideoFieldStrategies(video: Video, result: UnifiedVideoMatch): Record<string, VideoFieldStrategy> {
  const fields = [
    { key: "title", current: video.title, scraped: result.title },
    { key: "code", current: video.code, scraped: result.code },
    { key: "details", current: video.details, scraped: result.details },
    { key: "director", current: video.director, scraped: result.director },
    { key: "date", current: video.date, scraped: result.date },
  ];
  const strategies: Record<string, VideoFieldStrategy> = {};
  for (const field of fields) {
    if (!field.scraped) continue;
    strategies[field.key] = normalizeDecisionValue(field.current) === normalizeDecisionValue(field.scraped) ? "ignore" : "overwrite";
  }
  return strategies;
}

function getVideoFieldStrategies(video: Video, result: UnifiedVideoMatch, state: VideoSearchState | undefined) {
  return { ...buildDefaultVideoFieldStrategies(video, result), ...(state?.fieldStrategies ?? {}) };
}

// Default cover decision: an auto-generated frame cover (no explicit imagePath) is treated as "not set",
// so it defaults to Replace; an explicitly set cover defaults to Keep. The global "Set video cover image"
// toggle, when off, keeps the cover regardless.
function defaultVideoImageStrategy(video: Video, taggerConfig: TaggerConfig): VideoFieldStrategy {
  if (!taggerConfig.setCoverImage) return "ignore";
  return video.imagePath ? "ignore" : "overwrite";
}

// Whether the scraped cover should replace the video's current cover. The per-result "image" decision
// (when the user toggled it) wins; otherwise the explicit-cover default applies.
function getVideoImageReplace(video: Video, result: UnifiedVideoMatch, state: VideoSearchState | undefined, taggerConfig: TaggerConfig) {
  return (getVideoFieldStrategies(video, result, state).image ?? defaultVideoImageStrategy(video, taggerConfig)) === "overwrite";
}

function buildDefaultVideoCollectionModes(result: UnifiedVideoMatch, state: VideoSearchState | undefined, taggerConfig: TaggerConfig): Record<string, CollectionMode> {
  return {
    urls: result.urls.length > 0 ? "merge" : "skip",
    tags: taggerConfig.setTags && result.tagNames.length > 0 ? "merge" : "skip",
    performers: taggerConfig.setPerformers && result.performerNames.length > 0 ? "merge" : "skip",
    studio: taggerConfig.setStudio && !state?.skipStudio && result.studioName ? "replace" : "skip",
  };
}

function getVideoCollectionModes(result: UnifiedVideoMatch, state: VideoSearchState | undefined, taggerConfig: TaggerConfig) {
  return { ...buildDefaultVideoCollectionModes(result, state, taggerConfig), ...(state?.collectionModes ?? {}) };
}

function collectionModeToFieldStrategy(mode: CollectionMode): VideoFieldStrategy {
  if (mode === "replace") return "overwrite";
  if (mode === "merge") return "merge";
  return "ignore";
}

function buildVideoFieldStrategies(video: Video, result: UnifiedVideoMatch, state: VideoSearchState | undefined, taggerConfig: TaggerConfig) {
  const scalarStrategies = getVideoFieldStrategies(video, result, state);
  const collectionModes = getVideoCollectionModes(result, state, taggerConfig);
  return {
    ...scalarStrategies,
    urls: collectionModeToFieldStrategy(collectionModes.urls),
    tags: collectionModeToFieldStrategy(collectionModes.tags),
    performers: collectionModeToFieldStrategy(collectionModes.performers),
    studio: collectionModeToFieldStrategy(collectionModes.studio),
  };
}

function buildVideoRelationActionMap(
  names: string[],
  currentNames: string[],
  existingNames: string[],
  excludedNames: Set<string> | undefined,
  forceCreateNames: Set<string> | undefined,
  createMissing: boolean,
): ScrapeRelationActionMap {
  const current = new Set(currentNames.map(relationKey));
  const existing = new Set(existingNames.map(relationKey));
  const excluded = new Set(Array.from(excludedNames ?? []).map(relationKey));
  const forced = new Set(Array.from(forceCreateNames ?? []).map(relationKey));
  const actions: ScrapeRelationActionMap = {};

  for (const name of names) {
    const key = relationKey(name);
    if (!key) continue;
    if (excluded.has(key)) actions[key] = "exclude";
    else if (forced.has(key)) actions[key] = "create";
    else if (current.has(key) || existing.has(key)) actions[key] = "include";
    else actions[key] = createMissing ? "create" : "exclude";
  }

  return actions;
}

function buildVideoRelationSelections(
  names: string[],
  currentNames: string[],
  existingNames: string[],
  excludedNames: Set<string> | undefined,
  forceCreateNames: Set<string> | undefined,
  createMissing: boolean,
): ScrapeCollectionItemSelection[] {
  return buildRelationSelectionPayload(
    names,
    buildVideoRelationActionMap(names, currentNames, existingNames, excludedNames, forceCreateNames, createMissing),
  );
}

function buildScraperVideoApplyRequest(result: UnifiedVideoMatch, video: Video, state: VideoSearchState | undefined, taggerConfig: TaggerConfig): ApplyVideoScrapeAttemptRequest {
  const fieldStrategies = buildVideoFieldStrategies(video, result, state, taggerConfig);
  const collectionModes = getVideoCollectionModes(result, state, taggerConfig);
  const replaceFields = Object.entries(fieldStrategies)
    .filter(([field, strategy]) => strategy === "overwrite" && !["urls", "tags", "performers", "studio", "image"].includes(field))
    .map(([field]) => field);
  const raw = result.rawResult ?? {};
  // Cover is driven by the per-result image decision (defaulting to the global toggle).
  if (getVideoImageReplace(video, result, state, taggerConfig) && pickString(raw, "Image", "ImageUrl", "ImageURL")) replaceFields.push("image");

  return {
    replaceFields,
    collectionModes,
    createMissingTags: !taggerConfig.onlyExistingTags,
    createMissingPerformers: !taggerConfig.onlyExistingPerformers,
    createMissingStudio: !taggerConfig.onlyExistingStudio,
    markOrganized: taggerConfig.markOrganized,
    hydratePerformers: taggerConfig.createParentTags,
    selectedCandidateIndex: result.selectedCandidateIndex,
    tagSelections: result.tagNames.length > 0 ? buildVideoRelationSelections(result.tagNames, getVideoTagNames(video), result.tagCandidates.filter((tag) => tag.existsLocally).map((tag) => tag.name), state?.excludedTags, state?.forceIncludedTags, !taggerConfig.onlyExistingTags) : undefined,
    performerSelections: result.performerNames.length > 0 ? buildVideoRelationSelections(result.performerNames, getVideoPerformerNames(video), result.performerCandidates.filter((performer) => performer.existsLocally).map((performer) => performer.name), state?.excludedPerformers, state?.forceIncludedPerformers, !taggerConfig.onlyExistingPerformers) : undefined,
  };
}

const CONCURRENCY_LIMIT = 5;

async function runWithConcurrency<T>(
  items: T[],
  fn: (item: T) => Promise<void>,
  limit: number,
  signal?: AbortSignal
): Promise<void> {
  let index = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (index < items.length) {
      if (signal?.aborted) return;
      const i = index++;
      await fn(items[i]);
    }
  });
  await Promise.all(workers);
}

export function VideoTagger({ videos: videoList, onNavigate, selectedIds, selecting = false, onSelect, mode = "bulk" }: VideoTaggerProps) {
  const { config } = useAppConfig();
  const metadataServers = config?.scraping?.metadataServers ?? [];
  const { data: scraperList = [] } = useQuery({ queryKey: ["scrapers"], queryFn: system.listScrapers });
  const videoScrapers = scraperList.filter((scraper) => scraper.entityType.toLowerCase() === "video");
  const taggerSources: TaggerSource[] = [
    ...metadataServers.map((server) => ({
      kind: "metadata-server" as const,
      value: sourceValue("metadata-server", server.endpoint),
      label: server.name || server.endpoint,
      endpoint: server.endpoint,
    })),
    ...videoScrapers.map((scraper) => ({
      kind: "scraper" as const,
      value: sourceValue("scraper", scraper.id),
      label: `${scraper.name} (Scraper)`,
      scraper,
    })),
  ];

  const TAGGER_CONFIG_KEY = "cove-tagger-config";

  const DEFAULT_TAGGER_CONFIG: TaggerConfig = {
    selectedEndpoint: metadataServers[0] ? sourceValue("metadata-server", metadataServers[0].endpoint) : "",
    showUnmatched: true,
    setCoverImage: true,
    setTags: true,
    setPerformers: true,
    setStudio: true,
    onlyExistingTags: false,
    onlyExistingPerformers: false,
    onlyExistingStudio: false,
    markOrganized: false,
    preferFingerprints: true,
    queryMode: "auto",
    defaultScraperInputKind: "auto",
    blacklist: [...DEFAULT_TAGGER_BLACKLIST],
    createParentStudios: true,
    createParentTags: true,
    showMales: true,
    performerGenders: ["Female", "Male", "Transgender Female", "Transgender Male", "Intersex", "Non-Binary"],
  };

  const [taggerConfig, _setTaggerConfig] = useState<TaggerConfig>(() => {
    try {
      const saved = localStorage.getItem(TAGGER_CONFIG_KEY);
      if (saved) {
        const parsed = JSON.parse(saved) as Partial<TaggerConfig>;
        return {
          ...DEFAULT_TAGGER_CONFIG,
          ...parsed,
          selectedEndpoint: parsed.selectedEndpoint ?? DEFAULT_TAGGER_CONFIG.selectedEndpoint,
          blacklist: parsed.blacklist ?? DEFAULT_TAGGER_CONFIG.blacklist,
          performerGenders: parsed.performerGenders ?? DEFAULT_TAGGER_CONFIG.performerGenders,
        };
      }
    } catch { /* ignore */ }
    return DEFAULT_TAGGER_CONFIG;
  });

  const setTaggerConfig = useCallback((updater: TaggerConfig | ((prev: TaggerConfig) => TaggerConfig)) => {
    _setTaggerConfig((prev) => {
      const next = typeof updater === "function" ? updater(prev) : updater;
      try { localStorage.setItem(TAGGER_CONFIG_KEY, JSON.stringify(next)); } catch { /* ignore */ }
      return next;
    });
  }, []);
  const [showConfig, setShowConfig] = useState(false);
  const [searchStates, setSearchStates] = useState<Record<number, VideoSearchState>>({});
  const [queryOverrides, setQueryOverrides] = useState<Record<number, string>>({});
  const [scraperInputKinds, setScraperInputKinds] = useState<Record<number, InputKind>>({});
  const selectedSource = resolveSource(taggerConfig.selectedEndpoint, taggerSources);

  const updateSearchState = useCallback(
    (videoId: number, update: Partial<VideoSearchState>) => {
      setSearchStates((prev) => ({
        ...prev,
        [videoId]: { ...prev[videoId], ...update },
      }));
    },
    []
  );

  // Derive search query from video (standard prepareQueryString logic)
  const getSearchQuery = useCallback(
    (video: Video): string => {
      if (queryOverrides[video.id] !== undefined) return queryOverrides[video.id];
      const file = video.files[0];
      const mode = taggerConfig.queryMode;

      // metadata mode, or auto mode when video has date+studio — build compound query
      if (mode === "metadata" || (mode === "auto" && video.date && video.studioName)) {
        let str = [
          video.date || "",
          video.studioName || "",
          (video.performers || []).map((p: any) => p.name).join(" "),
          video.title ? video.title.replace(/[^a-zA-Z0-9 ]+/g, "") : "",
        ].filter((s) => s !== "").join(" ");
        str = cleanTaggerQueryString(str, taggerConfig.blacklist);
        return str;
      }

      // filename/dir/path modes: derive from file path
      if (mode === "filename" && file?.basename) {
        return cleanTaggerQueryString(file.basename.replace(/\.\w{2,4}$/, ""), taggerConfig.blacklist);
      }
      if (mode === "dir" && file?.path) {
        const parts = file.path.replace(/\\/g, "/").split("/");
        return parts.length > 1 ? cleanTaggerQueryString(parts[parts.length - 2], taggerConfig.blacklist) : "";
      }
      if (mode === "path" && file?.path) {
        return cleanTaggerQueryString(file.path, taggerConfig.blacklist);
      }

      // auto mode: try title first, then filename — always apply blacklist
      if (video.title) return cleanTaggerQueryString(video.title, taggerConfig.blacklist);
      if (file?.basename) {
        return cleanTaggerQueryString(file.basename.replace(/\.\w{2,4}$/, ""), taggerConfig.blacklist);
      }
      return "";
    },
    [queryOverrides, taggerConfig.queryMode, taggerConfig.blacklist]
  );

  const getScraperInputKind = useCallback((video: Video, source: TaggerSource | undefined): InputKind => {
    if (source?.kind !== "scraper") {
      return "name";
    }

    const override = scraperInputKinds[video.id];
    if (override) {
      return override;
    }
    const configured = taggerConfig.defaultScraperInputKind;
    const preferred: InputKind = configured !== "auto"
      ? configured
      : video.urls?.some((url) => url.trim()) ? "url" : "name";
    return findDefaultKind(source.scraper, preferred);
  }, [scraperInputKinds, taggerConfig.defaultScraperInputKind]);

  const getSourceQuery = useCallback(
    (video: Video, source: TaggerSource | undefined): string => {
      if (source?.kind === "scraper") {
        const inputKind = getScraperInputKind(video, source);
        if (queryOverrides[video.id] !== undefined) {
          return queryOverrides[video.id];
        }

        if (inputKind === "url") {
          return video.urls?.find((url) => url.trim()) ?? "";
        }

        if (inputKind === "fragment") {
          return buildFragmentDraft(video);
        }

        return getVideoNameSearchInput(video) || getSearchQuery(video);
      }
      return getSearchQuery(video);
    },
    [getScraperInputKind, getSearchQuery, queryOverrides]
  );

  const handleScraperInputKindChange = useCallback((video: Video, source: TaggerSource | undefined, inputKind: InputKind) => {
    setScraperInputKinds((prev) => ({ ...prev, [video.id]: inputKind }));
    setQueryOverrides((prev) => {
      const nextQuery = inputKind === "url"
        ? video.urls?.find((url) => url.trim()) ?? ""
        : inputKind === "fragment"
          ? buildFragmentDraft(video)
          : getVideoNameSearchInput(video) || getSearchQuery(video);
      return { ...prev, [video.id]: nextQuery };
    });
    if (source?.kind === "scraper" && !supportsScrapeKind(source.scraper, inputKind)) {
      updateSearchState(video.id, { error: `The selected scraper does not support ${inputKind} input.` });
    }
  }, [getSearchQuery, updateSearchState]);

  const searchVideo = useCallback(
    async (video: Video) => {
      const source = selectedSource;
      const query = getSourceQuery(video, source);
      updateSearchState(video.id, { loading: true, error: undefined, results: undefined, saved: false });
      try {
        let results: UnifiedVideoMatch[] = [];
        if (source?.kind === "scraper") {
          const inputKind = getScraperInputKind(video, source);
          if (!supportsScrapeKind(source.scraper, inputKind)) throw new Error(`This scraper does not support ${inputKind} input.`);
          if (inputKind === "url" && !query.trim()) throw new Error("Enter a URL to scrape.");
          if (inputKind === "name" && !query.trim()) throw new Error("Enter a title or name to scrape.");
          let fragment: Record<string, unknown> | undefined;
          if (inputKind === "fragment") {
            const parsed = JSON.parse(query);
            if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
              throw new Error("Fragment input must be a JSON object.");
            }
            fragment = parsed as Record<string, unknown>;
          }
          const attempt = await scrapeAttempts.create({
            scraperId: source.scraper.id,
            entityType: "video",
            entityId: video.id,
            inputKind,
            url: inputKind === "url" ? query : undefined,
            name: inputKind === "name" ? query : undefined,
            fragment,
          });
          if (attempt.status.toLowerCase() === "failure") throw new Error(attempt.error || "Scrape returned no results.");
          results = parseAttemptResults(attempt).map((result, index) => toScraperVideoMatch(attempt, result, index, source.scraper));
        } else {
          const endpoint = source?.endpoint || undefined;
          const shouldTryFingerprints = taggerConfig.preferFingerprints || !query;

          if (shouldTryFingerprints) {
            results = (await videos.searchMetadataServer(video.id, undefined, endpoint)).map((match) => ({ ...match, sourceKind: "metadata-server" as const }));
          }

          if (results.length === 0 && query) {
            results = (await videos.searchMetadataServer(video.id, query, endpoint)).map((match) => ({ ...match, sourceKind: "metadata-server" as const }));
          }
        }

        updateSearchState(video.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
        });
      } catch (err) {
        updateSearchState(video.id, {
          loading: false,
          error: err instanceof Error ? err.message : "Search failed",
        });
      }
    },
    [getScraperInputKind, getSourceQuery, selectedSource, taggerConfig.preferFingerprints, updateSearchState]
  );

  // Fingerprint-only search
  const searchVideoFingerprints = useCallback(
    async (video: Video) => {
      updateSearchState(video.id, { loading: true, error: undefined, results: undefined, saved: false });
      try {
        if (selectedSource?.kind !== "metadata-server") throw new Error("Fingerprint search is only available for metadata-server sources.");
        const results = (await videos.searchMetadataServer(video.id, undefined, selectedSource.endpoint || undefined)).map((match) => ({ ...match, sourceKind: "metadata-server" as const }));
        updateSearchState(video.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
        });
      } catch (err) {
        updateSearchState(video.id, {
          loading: false,
          error: err instanceof Error ? err.message : "Search failed",
        });
      }
    },
    [selectedSource, updateSearchState]
  );

  // Refresh/rescrape directly from an existing remote id (no name search needed).
  const refreshVideoFromRemote = useCallback(
    async (video: Video, endpoint: string, remoteId: string) => {
      updateSearchState(video.id, { loading: true, error: undefined, results: undefined, saved: false });
      try {
        const results = (await videos.findMetadataServerByIds({ endpoint, ids: [remoteId] })).map((match) => ({ ...match, sourceKind: "metadata-server" as const }));
        updateSearchState(video.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
          error: results.length === 0 ? "No metadata-server entry found for this remote id." : undefined,
        });
      } catch (err) {
        updateSearchState(video.id, { loading: false, error: err instanceof Error ? err.message : "Refresh failed" });
      }
    },
    [updateSearchState]
  );

  // Batch scrape all (concurrent)
  const [batchSearching, setBatchSearching] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const searchAll = useCallback(async () => {
    setBatchSearching(true);
    const controller = new AbortController();
    abortRef.current = controller;
    const toSearch = videoList.filter((s) => !searchStates[s.id]?.saved);
    await runWithConcurrency(toSearch, (video) => searchVideo(video), CONCURRENCY_LIMIT, controller.signal);
    setBatchSearching(false);
    abortRef.current = null;
  }, [videoList, searchStates, searchVideo]);

  const cancelBatchSearch = useCallback(() => {
    abortRef.current?.abort();
    setBatchSearching(false);
  }, []);

  if (taggerSources.length === 0) {
    return (
      <div className="px-4 py-12 text-center">
        <AlertCircle className="w-12 h-12 mx-auto mb-3 text-muted opacity-50" />
        <p className="text-secondary text-lg">No Metadata Sources Configured</p>
        <p className="text-muted text-sm mt-1">
          Add a metadata server or install a video scraper to use the tagger.
        </p>
      </div>
    );
  }

  // Detail mode was opened for this specific video, so always show it (the bulk "hide matched"
  // convenience filter would otherwise leave the dialog empty).
  const visibleVideos = mode === "detail" || taggerConfig.showUnmatched
    ? videoList
    : videoList.filter((s) => {
        const state = searchStates[s.id];
        return !state || !state.results || state.results.length > 0;
      });
  const visibleVideoIds = visibleVideos.map((video) => video.id);

  return (
    <div className="space-y-0">
      <TaggerToolbar
        sources={taggerSources.map((source) => ({ value: source.value, label: source.label }))}
        selectedSource={selectedSource?.value ?? taggerConfig.selectedEndpoint}
        onSourceChange={(value) => {
          setTaggerConfig((c) => ({ ...c, selectedEndpoint: value }));
          setSearchStates({});
          setQueryOverrides({});
          setScraperInputKinds({});
        }}
        showToggle={mode === "bulk" ? {
          value: taggerConfig.showUnmatched,
          onChange: (value) => setTaggerConfig((c) => ({ ...c, showUnmatched: value })),
          enabledLabel: "Hide Unmatched",
          disabledLabel: "Show Unmatched",
        } : undefined}
        batchSearching={batchSearching}
        onCancelBatch={cancelBatchSearch}
        onRunAll={searchAll}
        showRunAll={mode === "bulk"}
        countLabel={`${visibleVideos.length} video${visibleVideos.length !== 1 ? "s" : ""}`}
        settingsOpen={showConfig}
        onToggleSettings={() => setShowConfig((current) => !current)}
      />

      {showConfig && (
        <TaggerSettingsPanel
          blacklist={taggerConfig.blacklist}
          onBlacklistChange={(items) => setTaggerConfig((c) => ({ ...c, blacklist: items }))}
        >

              {/* Performer genders */}
              <div>
                <p className="text-xs text-muted mb-1.5">Performer genders</p>
                <div className="space-y-1">
                  {["Female", "Male", "Transgender Female", "Transgender Male", "Intersex", "Non-Binary"].map((g) => (
                    <label key={g} className="flex items-center gap-2 text-xs text-foreground">
                      <input type="checkbox" checked={taggerConfig.performerGenders.includes(g)} onChange={(e) => setTaggerConfig((c) => ({ ...c, performerGenders: e.target.checked ? [...c.performerGenders, g] : c.performerGenders.filter((x) => x !== g) }))} className="rounded border-border" />
                      {g}
                    </label>
                  ))}
                </div>
                <p className="text-[10px] text-muted mt-1">Performers with these genders will be shown when tagging videos.</p>
              </div>

              {/* Set video cover image */}
              <div>
                <label className="flex items-center gap-2 text-xs text-foreground">
                  <input type="checkbox" checked={taggerConfig.setCoverImage} onChange={(e) => setTaggerConfig((c) => ({ ...c, setCoverImage: e.target.checked }))} className="rounded border-border" />
                  Set video cover image
                </label>
                <p className="text-[10px] text-muted mt-0.5 ml-5">Replace the video cover if one is found.</p>
              </div>

              {/* Set performers */}
              <div>
                <label className="flex items-center gap-2 text-xs text-foreground">
                  <input type="checkbox" checked={taggerConfig.setPerformers} onChange={(e) => setTaggerConfig((c) => ({ ...c, setPerformers: e.target.checked }))} className="rounded border-border" />
                  Set performers
                </label>
                {taggerConfig.setPerformers && (
                  <label className="flex items-center gap-2 text-xs text-foreground ml-5 mt-1">
                    <input type="checkbox" checked={!taggerConfig.onlyExistingPerformers} onChange={(e) => setTaggerConfig((c) => ({ ...c, onlyExistingPerformers: !e.target.checked }))} className="rounded border-border" />
                    Create missing performers
                  </label>
                )}
                <p className="text-[10px] text-muted mt-0.5 ml-5">Attach performers to video. Uncheck "Create missing" to only use performers that already exist.</p>
              </div>

              {/* Set studio */}
              <div>
                <label className="flex items-center gap-2 text-xs text-foreground">
                  <input type="checkbox" checked={taggerConfig.setStudio} onChange={(e) => setTaggerConfig((c) => ({ ...c, setStudio: e.target.checked }))} className="rounded border-border" />
                  Set studio
                </label>
                {taggerConfig.setStudio && (
                  <label className="flex items-center gap-2 text-xs text-foreground ml-5 mt-1">
                    <input type="checkbox" checked={!taggerConfig.onlyExistingStudio} onChange={(e) => setTaggerConfig((c) => ({ ...c, onlyExistingStudio: !e.target.checked }))} className="rounded border-border" />
                    Create missing studios
                  </label>
                )}
                <p className="text-[10px] text-muted mt-0.5 ml-5">Set the video studio. Uncheck "Create missing" to only use studios that already exist.</p>
              </div>

              {/* Set tags + operation */}
              <div>
                <div className="flex items-center gap-3">
                  <label className="flex items-center gap-2 text-xs text-foreground">
                    <input type="checkbox" checked={taggerConfig.setTags} onChange={(e) => setTaggerConfig((c) => ({ ...c, setTags: e.target.checked }))} className="rounded border-border" />
                    Set tags
                  </label>
                </div>
                {taggerConfig.setTags && (
                  <label className="flex items-center gap-2 text-xs text-foreground ml-5 mt-1">
                    <input type="checkbox" checked={!taggerConfig.onlyExistingTags} onChange={(e) => setTaggerConfig((c) => ({ ...c, onlyExistingTags: !e.target.checked }))} className="rounded border-border" />
                    Create missing tags
                  </label>
                )}
                <p className="text-[10px] text-muted mt-0.5 ml-5">Attach tags to video. Uncheck "Create missing" to only set tags that already exist.</p>
              </div>

              {/* Query mode */}
              <div>
                <div className="flex items-center gap-2">
                  <span className="text-xs text-muted">Query Mode:</span>
                  <select value={taggerConfig.queryMode} onChange={(e) => setTaggerConfig((c) => ({ ...c, queryMode: e.target.value as TaggerConfig["queryMode"] }))} className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent">
                    <option value="auto">Auto</option>
                    <option value="filename">Filename</option>
                    <option value="dir">Directory</option>
                    <option value="path">Full Path</option>
                    <option value="metadata">Metadata</option>
                  </select>
                </div>
                <p className="text-[10px] text-muted mt-0.5">Uses metadata if present, or filename</p>
              </div>

              {/* Default scraper input (only relevant when the source is a scraper) */}
              {selectedSource?.kind === "scraper" && (
                <div>
                  <div className="flex items-center gap-2">
                    <span className="text-xs text-muted">Scraper Input:</span>
                    <select
                      value={taggerConfig.defaultScraperInputKind}
                      onChange={(e) => {
                        setTaggerConfig((c) => ({ ...c, defaultScraperInputKind: e.target.value as TaggerConfig["defaultScraperInputKind"] }));
                        setScraperInputKinds({});
                        setQueryOverrides({});
                      }}
                      className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
                    >
                      <option value="auto">Auto</option>
                      <option value="url">URL</option>
                      <option value="name">Title</option>
                      <option value="fragment">Fragment</option>
                    </select>
                  </div>
                  <p className="text-[10px] text-muted mt-0.5">Default scrape input for scraper sources. Auto uses the URL when present, otherwise the title. Falls back to a supported mode if the scraper lacks the chosen one, and can be overridden per video.</p>
                </div>
              )}

              {/* Mark organized */}
              <div>
                <label className="flex items-center gap-2 text-xs text-foreground">
                  <input type="checkbox" checked={taggerConfig.markOrganized} onChange={(e) => setTaggerConfig((c) => ({ ...c, markOrganized: e.target.checked }))} className="rounded border-border" />
                  Mark as Organized on save
                </label>
                <p className="text-[10px] text-muted mt-0.5 ml-5">Immediately mark the video as Organized after the Save button is clicked.</p>
              </div>
        </TaggerSettingsPanel>
      )}

      {/* Video list */}
      <div className="divide-y divide-border">
        {visibleVideos.map((video) => (
          <TaggerVideoRow
            key={video.id}
            video={video}
            state={searchStates[video.id]}
            query={getSourceQuery(video, selectedSource)}
            onQueryChange={(q) => setQueryOverrides((prev) => ({ ...prev, [video.id]: q }))}
            scraperInputKind={getScraperInputKind(video, selectedSource)}
            onScraperInputKindChange={(inputKind) => handleScraperInputKindChange(video, selectedSource, inputKind)}
            onSearch={() => searchVideo(video)}
            onSearchFingerprints={() => searchVideoFingerprints(video)}
            onRefreshFromRemote={(endpoint, remoteId) => refreshVideoFromRemote(video, endpoint, remoteId)}
            onUpdateState={(update) => updateSearchState(video.id, update)}
            source={selectedSource}
            metadataServers={metadataServers}
            taggerConfig={taggerConfig}
            onNavigate={onNavigate}
            selected={selectedIds?.has(video.id) ?? false}
            selecting={selecting}
            onSelect={onSelect ? withOrderedToggle(onSelect, visibleVideoIds) : undefined}
            detailMode={mode === "detail"}
          />
        ))}
      </div>
    </div>
  );
}

/* ── Video Tagger Row ── */

interface TaggerVideoRowProps {
  video: Video;
  state?: VideoSearchState;
  query: string;
  onQueryChange: (q: string) => void;
  scraperInputKind: InputKind;
  onScraperInputKindChange: (inputKind: InputKind) => void;
  onSearch: () => void;
  onSearchFingerprints: () => void;
  onRefreshFromRemote: (endpoint: string, remoteId: string) => void | Promise<void>;
  onUpdateState: (update: Partial<VideoSearchState>) => void;
  source?: TaggerSource;
  metadataServers: MetadataServer[];
  taggerConfig: TaggerConfig;
  onNavigate?: (videoId: number) => void;
  selected?: boolean;
  selecting?: boolean;
  onSelect?: (videoId: number, options?: MultiSelectToggleOptions) => void;
  detailMode?: boolean;
}

function TaggerVideoRow({
  video,
  state,
  query,
  onQueryChange,
  scraperInputKind,
  onScraperInputKindChange,
  onSearch,
  onSearchFingerprints,
  onRefreshFromRemote,
  onUpdateState,
  source,
  metadataServers,
  taggerConfig,
  onNavigate,
  selected = false,
  selecting = false,
  onSelect,
  detailMode = false,
}: TaggerVideoRowProps) {
  const file = video.files[0];
  const [refreshBusyEndpoint, setRefreshBusyEndpoint] = useState<string | null>(null);
  const handleRefreshFromRemote = async (endpoint: string, remoteId: string) => {
    setRefreshBusyEndpoint(endpoint);
    try {
      await onRefreshFromRemote(endpoint, remoteId);
    } finally {
      setRefreshBusyEndpoint(null);
    }
  };
  const queryClient = useQueryClient();
  // Resolve which scraper-returned tag/performer names already exist locally, using the same backend
  // matcher the apply path uses (alias-aware for performers). Metadata-server results already carry
  // correct existsLocally from their own search, so only scraper candidates are enriched below.
  const scraperResultNames = useMemo(() => {
    const tags = new Set<string>();
    const performers = new Set<string>();
    for (const r of state?.results ?? []) {
      if (r.sourceKind !== "scraper") continue;
      r.tagNames.forEach((name) => tags.add(name));
      r.performerNames.forEach((name) => performers.add(name));
    }
    return { tags: [...tags], performers: [...performers] };
  }, [state?.results]);
  const { data: resolvedRelations } = useQuery({
    queryKey: ["tagger-resolve-relations", scraperResultNames],
    queryFn: () => scrapeAttempts.resolveRelations({ tags: scraperResultNames.tags, performers: scraperResultNames.performers }),
    enabled: scraperResultNames.tags.length > 0 || scraperResultNames.performers.length > 0,
    staleTime: 30_000,
  });
  const existingTagKeys = useMemo(() => new Set((resolvedRelations?.tags ?? []).map((m) => relationKey(m.input))), [resolvedRelations]);
  const existingPerformerKeys = useMemo(() => new Set((resolvedRelations?.performers ?? []).map((m) => relationKey(m.input))), [resolvedRelations]);
  const tagMatchInfo = useMemo(() => buildMatchInfo(resolvedRelations?.tags), [resolvedRelations]);
  const performerMatchInfo = useMemo(() => buildMatchInfo(resolvedRelations?.performers), [resolvedRelations]);
  const enrichedResults = useMemo(() => {
    const results = state?.results;
    if (!results) return results;
    return results.map((r) =>
      r.sourceKind !== "scraper"
        ? r
        : {
            ...r,
            tagCandidates: r.tagCandidates.map((c) => ({ ...c, existsLocally: existingTagKeys.has(relationKey(c.name)) })),
            performerCandidates: r.performerCandidates.map((c) => ({ ...c, existsLocally: existingPerformerKeys.has(relationKey(c.name)) })),
          },
    );
  }, [state?.results, existingTagKeys, existingPerformerKeys]);
  const selectedResult = enrichedResults?.[state?.selectedIndex ?? 0];
  const videoLinkProps = createNestedRouteLinkProps<HTMLAnchorElement>({ page: "video", id: video.id }, () => onNavigate?.(video.id));
  const isScraperSource = source?.kind === "scraper";
  const videoUrls = (video.urls ?? []).filter((url) => url.trim());
  const selectedUrlOption = videoUrls.includes(query) ? query : "__custom";
  const searchPlaceholder = isScraperSource
    ? scraperInputKind === "url"
      ? "Video URL..."
      : scraperInputKind === "fragment"
        ? "Fragment JSON..."
        : "Title or name..."
    : "Search query...";

  const importMut = useMutation<Video | ScrapeAttempt, Error>({
    mutationFn: () => {
      if (!selectedResult) throw new Error("No result selected");
      const collectionModes = getVideoCollectionModes(selectedResult, state, taggerConfig);
      const tagActions = buildVideoRelationActionMap(selectedResult.tagNames, getVideoTagNames(video), selectedResult.tagCandidates.filter((tag) => tag.existsLocally).map((tag) => tag.name), state?.excludedTags, state?.forceIncludedTags, !taggerConfig.onlyExistingTags);
      const performerActions = buildVideoRelationActionMap(selectedResult.performerNames, getVideoPerformerNames(video), selectedResult.performerCandidates.filter((performer) => performer.existsLocally).map((performer) => performer.name), state?.excludedPerformers, state?.forceIncludedPerformers, !taggerConfig.onlyExistingPerformers);
      const excludedTags = collectionModes.tags === "skip" ? selectedResult.tagNames : selectedResult.tagNames.filter((name) => tagActions[relationKey(name)] === "exclude");
      const excludedPerformers = collectionModes.performers === "skip" ? selectedResult.performerNames : selectedResult.performerNames.filter((name) => performerActions[relationKey(name)] === "exclude");
      if (selectedResult?.sourceKind === "scraper") {
        if (!selectedResult.scrapeAttemptId) throw new Error("No scraper attempt selected");
        return scrapeAttempts.apply(selectedResult.scrapeAttemptId, buildScraperVideoApplyRequest(selectedResult, video, state, taggerConfig));
      }

      // Build overrides for force-included entities (entities that would normally be skipped
      // by onlyExisting* flags but the user explicitly opted to create)
      const performerOverrides = selectedResult.performerCandidates.some((performer) => performerActions[relationKey(performer.name)] === "create")
        ? selectedResult.performerCandidates
            .filter(p => performerActions[relationKey(p.name)] === "create")
            .map(p => ({ remoteId: p.remoteId, name: p.name, action: "create" }))
        : undefined;
      const tagOverrides = selectedResult.tagCandidates.some((tag) => tagActions[relationKey(tag.name)] === "create")
        ? selectedResult.tagCandidates
            .filter(t => tagActions[relationKey(t.name)] === "create")
            .map(t => ({ remoteId: t.remoteId, name: t.name, action: "create" }))
        : undefined;
      const studioOverride = state?.forceIncludeStudio && selectedResult.studioCandidate
        ? { remoteId: selectedResult.studioCandidate.remoteId, name: selectedResult.studioCandidate.name, action: "create" }
        : undefined;

      const importReq: MetadataServerVideoImportRequest = {
        endpoint: selectedResult.endpoint,
        videoId: selectedResult?.id ?? "",
        setCoverImage: getVideoImageReplace(video, selectedResult, state, taggerConfig),
        // When the user explicitly chose Replace, overwrite even an explicitly set cover.
        overwriteExplicitCover: getVideoImageReplace(video, selectedResult, state, taggerConfig),
        setTags: taggerConfig.setTags && collectionModes.tags !== "skip",
        setPerformers: taggerConfig.setPerformers && collectionModes.performers !== "skip",
        setStudio: taggerConfig.setStudio && collectionModes.studio !== "skip",
        onlyExistingTags: taggerConfig.onlyExistingTags,
        onlyExistingPerformers: taggerConfig.onlyExistingPerformers,
        onlyExistingStudio: taggerConfig.onlyExistingStudio,
        markOrganized: taggerConfig.markOrganized,
        excludedTagNames: excludedTags.length > 0 ? excludedTags : undefined,
        excludedPerformerNames: excludedPerformers.length > 0 ? excludedPerformers : undefined,
        performerOverrides,
        tagOverrides,
        studioOverride,
        fieldStrategies: buildVideoFieldStrategies(video, selectedResult, state, taggerConfig),
      };
      return videos.importFromMetadataServer(video.id, importReq);
    },
    onSuccess: async () => {
      onUpdateState({ saved: true });
      await invalidateVideoMetadataQueries(queryClient, video.id);
    },
  });

  const submitEndpoint = source?.kind === "metadata-server" ? source.endpoint : undefined;
  const normalizedSubmitEndpoint = normalizeEndpoint(submitEndpoint);
  const hasRemoteIdForEndpoint =
    Boolean(normalizedSubmitEndpoint) &&
    video.remoteIds.some((remote) => normalizeEndpoint(remote.endpoint) === normalizedSubmitEndpoint);
  const hasSavedMetadataServerMatchForEndpoint =
    Boolean(normalizedSubmitEndpoint) &&
    Boolean(state?.saved) &&
    selectedResult?.sourceKind === "metadata-server" &&
    normalizeEndpoint(selectedResult.endpoint) === normalizedSubmitEndpoint;
  const canSubmitFingerprints = source?.kind === "metadata-server" && (hasRemoteIdForEndpoint || hasSavedMetadataServerMatchForEndpoint);
  const shouldHighlightFingerprintSubmit = canSubmitFingerprints;

  const submitDraftMut = useMutation<{ draftId: string | null }, Error>({
    mutationFn: () => {
      if (!submitEndpoint) throw new Error("Select a metadata-server source first.");
      return videos.submitMetadataServerDraft(video.id, submitEndpoint);
    },
  });

  const submitFingerprintsMut = useMutation<void, Error>({
    mutationFn: () => {
      if (!submitEndpoint) throw new Error("Select a metadata-server source first.");
      return videos.submitFingerprints(video.id, submitEndpoint);
    },
  });

  return (
    <div className={`px-3 py-2 ${selected ? "bg-accent/5" : ""}`}>
      <div className="flex gap-3">
        {onSelect && (
          <button
            type="button"
            onClick={(event) => onSelect(video.id, toggleOptionsFromEvent(event))}
            className={`mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded border text-[10px] ${selected ? "border-accent bg-accent text-white" : selecting ? "border-accent/60 text-accent" : "border-border text-transparent hover:border-accent hover:text-accent"}`}
            aria-label={selected ? "Deselect video" : "Select video"}
            title={selected ? "Deselect" : "Select"}
          >
            <Check className="h-3 w-3" />
          </button>
        )}
        {/* Video preview */}
        <a
          {...videoLinkProps}
          className="video-card-preview-trigger block w-[10.5rem] flex-shrink-0 group/video"
          title={`Open video ${video.title || file?.basename || "Untitled"}`}
        >
          <VideoPreviewThumbnail video={video} fit="cover" surface="list" coverWidth={640} className="rounded bg-card">
            {file && file.duration > 0 && (
              <span className="video-specs-overlay absolute bottom-0.5 right-0.5 z-[5] rounded bg-black/70 px-0.5 text-[8px] text-white transition-opacity">
                {formatDuration(file.duration)}
              </span>
            )}
          </VideoPreviewThumbnail>
          <p className="text-[11px] text-accent mt-0.5 truncate font-medium leading-snug group-hover/video:underline">
            {video.title || file?.basename || "Untitled"}
          </p>
          <p className="text-[9px] text-muted truncate leading-snug">
            {[video.studioName, file && getResolutionLabel(file.width, file.height)].filter(Boolean).join(" · ")}
          </p>
        </a>

        {/* Search + Results */}
        <div className="flex-1 min-w-0">
          {detailMode && (
            <RemoteRefreshButtons
              remoteIds={video.remoteIds}
              servers={metadataServers}
              busyEndpoint={refreshBusyEndpoint}
              onRefresh={handleRefreshFromRemote}
            />
          )}
          {isScraperSource && (
            <div className="mb-1.5 flex flex-wrap items-center gap-1.5">
              <select
                value={scraperInputKind}
                onChange={(event) => onScraperInputKindChange(event.target.value as InputKind)}
                className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
              >
                <option value="url" disabled={!supportsScrapeKind(source.scraper, "url")}>URL</option>
                <option value="name" disabled={!supportsScrapeKind(source.scraper, "name")}>Title</option>
                <option value="fragment" disabled={!supportsScrapeKind(source.scraper, "fragment")}>Fragment</option>
              </select>
              {scraperInputKind === "url" && videoUrls.length > 0 ? (
                <select
                  value={selectedUrlOption}
                  onChange={(event) => {
                    if (event.target.value !== "__custom") {
                      onQueryChange(event.target.value);
                    }
                  }}
                  className="min-w-0 max-w-full flex-1 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
                >
                  <option value="__custom">Custom URL</option>
                  {videoUrls.map((url) => (
                    <option key={url} value={url}>{url}</option>
                  ))}
                </select>
              ) : null}
            </div>
          )}
          {/* Search input — inline and compact */}
          <div className="flex gap-1.5 mb-1.5">
            {isScraperSource && scraperInputKind === "fragment" ? (
              <textarea
                value={query}
                onChange={(e) => onQueryChange(e.target.value)}
                rows={detailMode ? 8 : 3}
                placeholder={searchPlaceholder}
                className="flex-1 min-w-0 bg-input border border-border rounded pl-2 pr-2 py-1 font-mono text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
              />
            ) : (
              <input
                type="text"
                value={query}
                onChange={(e) => onQueryChange(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && onSearch()}
                placeholder={searchPlaceholder}
                className="flex-1 min-w-0 bg-input border border-border rounded pl-2 pr-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
              />
            )}
            <button
              onClick={onSearch}
              disabled={state?.loading}
              className="flex h-fit items-center gap-1 px-2 py-1 rounded text-xs font-medium bg-accent text-white hover:bg-accent-hover disabled:opacity-60"
            >
              {state?.loading ? <Loader2 className="w-3 h-3 animate-spin" /> : <Search className="w-3 h-3" />}
            </button>
            {source?.kind === "metadata-server" && (
              <button
                onClick={onSearchFingerprints}
                disabled={state?.loading}
                className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-surface border border-border text-muted hover:text-foreground disabled:opacity-60"
                title="Search by fingerprint only"
              >
                <Fingerprint className="w-3 h-3" />
              </button>
            )}
            {source?.kind === "metadata-server" && (
              <button
                onClick={() => submitFingerprintsMut.mutate()}
                disabled={submitFingerprintsMut.isPending || !canSubmitFingerprints}
                className={`flex items-center gap-1 px-2 py-1 rounded text-xs border transition-colors disabled:opacity-60 ${
                  shouldHighlightFingerprintSubmit
                    ? "border-accent/30 bg-accent/10 text-accent hover:border-accent/50 hover:bg-accent/15 hover:text-accent"
                    : "bg-surface border-border text-muted hover:text-foreground"
                }`}
                title={canSubmitFingerprints ? "Submit your fingerprints for this video to the metadata server" : "Link this video to a metadata-server entry before submitting fingerprints"}
              >
                {submitFingerprintsMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Upload className="w-3 h-3" />}
              </button>
            )}
            {source?.kind === "metadata-server" && (
              <button
                onClick={() => submitDraftMut.mutate()}
                disabled={submitDraftMut.isPending}
                className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-surface border border-border text-muted hover:text-foreground disabled:opacity-60"
                title="Submit this video as a draft entry to the metadata server"
              >
                {submitDraftMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <CloudUpload className="w-3 h-3" />}
              </button>
            )}
          </div>

          {submitFingerprintsMut.isError && (
            <p className="text-xs text-red-400 mb-2"><AlertCircle className="w-3 h-3 inline mr-1" />{submitFingerprintsMut.error.message}</p>
          )}
          {submitFingerprintsMut.isSuccess && (
            <p className="text-xs text-green-400 mb-2"><Check className="w-3 h-3 inline mr-1" />Fingerprints submitted to the metadata server.</p>
          )}
          {submitDraftMut.isError && (
            <p className="text-xs text-red-400 mb-2"><AlertCircle className="w-3 h-3 inline mr-1" />{submitDraftMut.error.message}</p>
          )}
          {submitDraftMut.isSuccess && (
            <p className="text-xs text-green-400 mb-2"><Check className="w-3 h-3 inline mr-1" />Video draft submitted{submitDraftMut.data.draftId ? ` (${submitDraftMut.data.draftId})` : ""}.</p>
          )}

          {/* Error */}
          {state?.error && (
            <p className="text-xs text-red-400 mb-2">
              <AlertCircle className="w-3 h-3 inline mr-1" />
              {state.error}
            </p>
          )}

          {/* No results */}
          {state?.results && state.results.length === 0 && (
            <p className="text-xs text-muted">No matches found.</p>
          )}

          {/* Results */}
          {state?.results && state.results.length > 0 && (
            <TaggerResults
              video={video}
              results={enrichedResults ?? state.results}
              tagMatchInfo={tagMatchInfo}
              performerMatchInfo={performerMatchInfo}
              selectedIndex={state.selectedIndex ?? 0}
              onSelect={(i) => onUpdateState(i === (state.selectedIndex ?? 0) ? { selectedIndex: i } : {
                selectedIndex: i,
                fieldStrategies: undefined,
                collectionModes: undefined,
                excludedPerformers: undefined,
                excludedTags: undefined,
                skipStudio: undefined,
                forceIncludedPerformers: undefined,
                forceIncludedTags: undefined,
                forceIncludeStudio: undefined,
              })}
              onSave={() => importMut.mutate()}
              saving={importMut.isPending}
              saved={state.saved}
              localDuration={file?.duration}
              excludedPerformers={state.excludedPerformers ?? new Set()}
              excludedTags={state.excludedTags ?? new Set()}
              skipStudio={state.skipStudio ?? false}
              forceIncludedPerformers={state.forceIncludedPerformers ?? new Set()}
              forceIncludedTags={state.forceIncludedTags ?? new Set()}
              forceIncludeStudio={state.forceIncludeStudio ?? false}
              fieldStrategies={selectedResult ? getVideoFieldStrategies(video, selectedResult, state) : {}}
              collectionModes={selectedResult ? getVideoCollectionModes(selectedResult, state, taggerConfig) : {}}
              onFieldStrategyChange={(field, strategy) => {
                if (!selectedResult) return;
                onUpdateState({ fieldStrategies: { ...getVideoFieldStrategies(video, selectedResult, state), [field]: strategy } });
              }}
              onCollectionModeChange={(field, mode) => {
                if (!selectedResult) return;
                onUpdateState({ collectionModes: { ...getVideoCollectionModes(selectedResult, state, taggerConfig), [field]: mode } });
              }}
              onTogglePerformer={(name) => {
                const perf = selectedResult?.performerCandidates.find(p => p.name === name);
                const willSkipByDefault = taggerConfig.onlyExistingPerformers && perf && !perf.existsLocally;
                if (willSkipByDefault) {
                  const current = new Set(state.forceIncludedPerformers ?? []);
                  if (current.has(name)) current.delete(name);
                  else current.add(name);
                  onUpdateState({ forceIncludedPerformers: current });
                } else {
                  const current = new Set(state.excludedPerformers ?? []);
                  if (current.has(name)) current.delete(name);
                  else current.add(name);
                  onUpdateState({ excludedPerformers: current });
                }
              }}
              onToggleTag={(name) => {
                const tag = selectedResult?.tagCandidates.find(t => t.name === name);
                const willSkipByDefault = taggerConfig.onlyExistingTags && tag && !tag.existsLocally;
                if (willSkipByDefault) {
                  const current = new Set(state.forceIncludedTags ?? []);
                  if (current.has(name)) current.delete(name);
                  else current.add(name);
                  onUpdateState({ forceIncludedTags: current });
                } else {
                  const current = new Set(state.excludedTags ?? []);
                  if (current.has(name)) current.delete(name);
                  else current.add(name);
                  onUpdateState({ excludedTags: current });
                }
              }}
              onToggleStudio={() => {
                const willSkipByDefault = taggerConfig.onlyExistingStudio && selectedResult?.studioCandidate && !selectedResult.studioCandidate.existsLocally;
                if (willSkipByDefault) {
                  onUpdateState({ forceIncludeStudio: !state.forceIncludeStudio });
                } else {
                  onUpdateState({ skipStudio: !state.skipStudio });
                }
              }}
              taggerConfig={taggerConfig}
            />
          )}

          {/* Saved indicator */}
          {state?.saved && (
            <div className="flex items-center gap-1 mt-2 text-xs text-green-400">
              <Check className="w-3.5 h-3.5" />
              Saved successfully
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

/* ── Tagger Results ── */

interface TaggerResultsProps {
  video: Video;
  results: UnifiedVideoMatch[];
  tagMatchInfo?: Record<string, string>;
  performerMatchInfo?: Record<string, string>;
  selectedIndex: number;
  onSelect: (index: number) => void;
  onSave: () => void;
  saving?: boolean;
  saved?: boolean;
  localDuration?: number;
  excludedPerformers: Set<string>;
  excludedTags: Set<string>;
  skipStudio: boolean;
  forceIncludedPerformers: Set<string>;
  forceIncludedTags: Set<string>;
  forceIncludeStudio: boolean;
  fieldStrategies: Record<string, VideoFieldStrategy>;
  collectionModes: Record<string, CollectionMode>;
  onFieldStrategyChange: (field: string, strategy: VideoFieldStrategy) => void;
  onCollectionModeChange: (field: string, mode: CollectionMode) => void;
  onTogglePerformer: (name: string) => void;
  onToggleTag: (name: string) => void;
  onToggleStudio: () => void;
  taggerConfig: TaggerConfig;
}

function TaggerResults({ video, results, tagMatchInfo, performerMatchInfo, selectedIndex, onSelect, onSave, saving, saved, localDuration, excludedPerformers, excludedTags, skipStudio, forceIncludedPerformers, forceIncludedTags, forceIncludeStudio, fieldStrategies, collectionModes, onFieldStrategyChange, onCollectionModeChange, onTogglePerformer, onToggleTag, onToggleStudio, taggerConfig }: TaggerResultsProps) {
  return (
    <div className="space-y-1">
      {results.map((result, i) => (
        <TaggerResultRow
          key={`${result.endpoint}-${result.id}`}
          video={video}
          result={result}
          tagMatchInfo={tagMatchInfo}
          performerMatchInfo={performerMatchInfo}
          isSelected={i === selectedIndex}
          onClick={() => onSelect(i)}
          onSave={i === selectedIndex ? onSave : undefined}
          saving={i === selectedIndex ? saving : false}
          saved={saved}
          localDuration={localDuration}
          excludedPerformers={excludedPerformers}
          excludedTags={excludedTags}
          skipStudio={skipStudio}
          forceIncludedPerformers={forceIncludedPerformers}
          forceIncludedTags={forceIncludedTags}
          forceIncludeStudio={forceIncludeStudio}
          fieldStrategies={fieldStrategies}
          collectionModes={collectionModes}
          onFieldStrategyChange={i === selectedIndex ? onFieldStrategyChange : undefined}
          onCollectionModeChange={i === selectedIndex ? onCollectionModeChange : undefined}
          onTogglePerformer={i === selectedIndex ? onTogglePerformer : undefined}
          onToggleTag={i === selectedIndex ? onToggleTag : undefined}
          onToggleStudio={i === selectedIndex ? onToggleStudio : undefined}
          taggerConfig={taggerConfig}
        />
      ))}
    </div>
  );
}

function TaggerResultRow({
  video,
  result,
  tagMatchInfo,
  performerMatchInfo,
  isSelected,
  onClick,
  onSave,
  saving,
  saved,
  localDuration,
  excludedPerformers,
  excludedTags,
  skipStudio,
  forceIncludedPerformers,
  forceIncludedTags,
  forceIncludeStudio,
  fieldStrategies,
  collectionModes,
  onFieldStrategyChange,
  onCollectionModeChange,
  onTogglePerformer,
  onToggleTag,
  onToggleStudio,
  taggerConfig,
}: {
  video: Video;
  result: MetadataServerVideoMatch;
  tagMatchInfo?: Record<string, string>;
  performerMatchInfo?: Record<string, string>;
  isSelected: boolean;
  onClick: () => void;
  onSave?: () => void;
  saving?: boolean;
  saved?: boolean;
  localDuration?: number;
  excludedPerformers: Set<string>;
  excludedTags: Set<string>;
  skipStudio: boolean;
  forceIncludedPerformers: Set<string>;
  forceIncludedTags: Set<string>;
  forceIncludeStudio: boolean;
  fieldStrategies: Record<string, VideoFieldStrategy>;
  collectionModes: Record<string, CollectionMode>;
  onFieldStrategyChange?: (field: string, strategy: VideoFieldStrategy) => void;
  onCollectionModeChange?: (field: string, mode: CollectionMode) => void;
  onTogglePerformer?: (name: string) => void;
  onToggleTag?: (name: string) => void;
  onToggleStudio?: () => void;
  taggerConfig: TaggerConfig;
}) {
  const durationDiff = localDuration != null && result.duration != null
    ? Math.abs(localDuration - result.duration)
    : undefined;
  const durationMatch = durationDiff != null && durationDiff < 5;
  const scalarRows = [
    { key: "title", label: "Title", current: video.title, scraped: result.title },
    { key: "code", label: "Code", current: video.code, scraped: result.code },
    { key: "details", label: "Details", current: video.details, scraped: result.details, multiline: true },
    { key: "director", label: "Director", current: video.director, scraped: result.director },
    { key: "date", label: "Date", current: video.date, scraped: result.date },
  ].filter((row) => Boolean(row.scraped));
  const currentTagNames = getVideoTagNames(video);
  const currentPerformerNames = getVideoPerformerNames(video);
  const existingTagNames = result.tagCandidates.filter((tag) => tag.existsLocally).map((tag) => tag.name);
  const existingPerformerNames = result.performerCandidates.filter((performer) => performer.existsLocally).map((performer) => performer.name);
  const tagActions = buildVideoRelationActionMap(result.tagNames, currentTagNames, existingTagNames, excludedTags, forceIncludedTags, !taggerConfig.onlyExistingTags);
  const performerActions = buildVideoRelationActionMap(result.performerNames, currentPerformerNames, existingPerformerNames, excludedPerformers, forceIncludedPerformers, !taggerConfig.onlyExistingPerformers);

  return (
    <div
      onClick={onClick}
      className={`rounded border cursor-pointer transition-colors ${
        isSelected
          ? "border-accent bg-card"
          : "border-border bg-surface hover:border-accent/50"
      }`}
    >
      {/* Header row — always visible for all results */}
      <div className="flex items-center gap-3 p-2">
        {/* Radio selector for multiple results */}
        <div className="flex-shrink-0">
          <div className={`w-4 h-4 rounded-full border-2 flex items-center justify-center ${isSelected ? "border-accent" : "border-border"}`}>
            {isSelected && <div className="w-2 h-2 rounded-full bg-accent" />}
          </div>
        </div>

        {/* Cover thumbnail */}
        {result.imageUrl && (
          <img src={result.imageUrl} alt="" className="w-20 h-12 object-cover rounded flex-shrink-0" loading="lazy" />
        )}

        <div className="flex-1 min-w-0">
          <p className="text-xs font-medium text-foreground truncate">
            {result.title || "Untitled"}
            {result.code && <span className="text-muted ml-1">({result.code})</span>}
          </p>
          {result.details && (
            <p className="mt-1 text-[11px] leading-relaxed text-secondary line-clamp-2">
              {result.details}
            </p>
          )}
          <div className="flex items-center gap-3 text-[10px] text-muted mt-0.5">
            {result.date && <span>Date: <span className="text-foreground">{result.date}</span></span>}
            {result.director && <span>Director: <span className="text-foreground">{result.director}</span></span>}
            {result.duration != null && (
              <span>
                Duration: <span className="text-foreground">{formatDuration(result.duration)}</span>
                {durationDiff != null && (
                  <span className={durationMatch ? " text-green-400" : durationDiff < 30 ? " text-yellow-400" : " text-red-400"}>
                    {" "}({durationDiff < 1 ? "exact" : `${Math.round(durationDiff)}s diff`})
                  </span>
                )}
              </span>
            )}
            {result.performerNames.length > 0 && (
              <span className="truncate">{result.performerNames.join(", ")}</span>
            )}
          </div>
        </div>

        {/* Fingerprint indicators — shows which algorithms the remote video has, with match status */}
        {result.fingerprints.length > 0 && (() => {
          const remoteAlgos = [...new Set(result.fingerprints.map(fp => fp.algorithm.toUpperCase()))];
          const matchedSet = new Set(result.fingerprintAlgorithms.map(a => a.toUpperCase()));
          return (
            <span className="flex items-center gap-1 text-[9px] px-2 py-0.5 rounded bg-surface flex-shrink-0" title={result.matchCount > 0 ? `${result.matchCount} fingerprint match${result.matchCount !== 1 ? "es" : ""}` : "No fingerprint matches"}>
              <Fingerprint className={`w-3 h-3 ${result.matchCount > 0 ? "text-green-400" : "text-muted"}`} />
              {remoteAlgos.map((alg, i) => (
                <span key={alg} className={`font-semibold ${matchedSet.has(alg) ? "text-green-300" : "text-muted"}`}>{i > 0 && " · "}{alg}</span>
              ))}
              {result.matchCount > 0 && (
                <span className="text-green-300 opacity-70 ml-0.5">({result.matchCount})</span>
              )}
            </span>
          );
        })()}

        {/* Save button (inline for selected) */}
        {isSelected && onSave && !saved && (
          <button
            onClick={(e) => { e.stopPropagation(); onSave(); }}
            disabled={saving}
            className="flex items-center gap-1.5 px-4 py-1.5 rounded text-xs font-medium bg-green-600 text-white hover:bg-green-500 disabled:opacity-60 flex-shrink-0"
          >
            {saving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Check className="w-3.5 h-3.5" />}
            Save
          </button>
        )}
      </div>

      {/* Expanded details — only for selected result */}
      {isSelected && !saved && (
        <div className="border-t border-border px-3 py-3 space-y-3">
          {scalarRows.map((row) => (
            <CompactScalarDecision
              key={row.key}
              label={row.label}
              current={row.current}
              scraped={row.scraped}
              multiline={row.multiline}
              replacing={fieldStrategies[row.key] === "overwrite"}
              onChange={(shouldReplace) => onFieldStrategyChange?.(row.key, shouldReplace ? "overwrite" : "ignore")}
            />
          ))}

          {result.imageUrl && (
            <CompactImageDecision
              currentImageUrl={video.imagePath || videos.screenshotUrl(video.id, video.updatedAt)}
              scrapedImageUrl={result.imageUrl}
              replacing={(fieldStrategies.image ?? defaultVideoImageStrategy(video, taggerConfig)) === "overwrite"}
              onChange={(shouldReplace) => onFieldStrategyChange?.("image", shouldReplace ? "overwrite" : "ignore")}
            />
          )}

          {result.studioName && taggerConfig.setStudio && (
            <CompactScalarDecision
              label="Studio"
              current={video.studioName}
              scraped={result.studioName}
              replacing={collectionModes.studio === "replace"}
              onChange={(shouldReplace) => onCollectionModeChange?.("studio", shouldReplace ? "replace" : "skip")}
            />
          )}

          {result.urls.length > 0 && (
            <CompactCollectionDecision
              label="URLs"
              current={video.urls}
              mode={collectionModes.urls}
              onModeChange={(mode) => onCollectionModeChange?.("urls", mode)}
              scraped={<CompactListValue values={result.urls} breakAll />}
            />
          )}

          {result.performerNames.length > 0 && taggerConfig.setPerformers && (
            <CompactCollectionDecision
              label="Performers"
              current={currentPerformerNames}
              mode={collectionModes.performers}
              onModeChange={(mode) => onCollectionModeChange?.("performers", mode)}
              scraped={(
                <div onClick={(event) => event.stopPropagation()}>
                  <ScrapeRelationChoices
                    names={result.performerNames}
                    currentNames={currentPerformerNames}
                    existingNames={existingPerformerNames}
                    matchInfo={performerMatchInfo}
                    actions={performerActions}
                    disabled={collectionModes.performers === "skip"}
                    onActionChange={(name) => onTogglePerformer?.(name)}
                  />
                </div>
              )}
            />
          )}

          {result.tagNames.length > 0 && taggerConfig.setTags && (
            <CompactCollectionDecision
              label="Tags"
              current={currentTagNames}
              mode={collectionModes.tags}
              onModeChange={(mode) => onCollectionModeChange?.("tags", mode)}
              scraped={(
                <div onClick={(event) => event.stopPropagation()}>
                  <ScrapeRelationChoices
                    names={result.tagNames}
                    currentNames={currentTagNames}
                    existingNames={existingTagNames}
                    matchInfo={tagMatchInfo}
                    actions={tagActions}
                    disabled={collectionModes.tags === "skip"}
                    onActionChange={(name) => onToggleTag?.(name)}
                  />
                </div>
              )}
            />
          )}
        </div>
      )}
    </div>
  );
}
