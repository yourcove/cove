import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { scrapeAttempts, system } from "../api/client";
import type { ApplyVideoScrapeAttemptRequest, ScrapeAttempt, ScraperSummary } from "../api/types";
import type { Route } from "../router/location";
import { createNestedRouteLinkProps } from "./cardNavigation";
import {
  buildMatchInfo,
  buildRelationActionMap,
  buildRelationSelectionPayload,
  relationKey,
  ScrapeRelationChoices,
  type ScrapeRelationActionMap,
} from "./ScrapeRelationChoices";
import {
  DEFAULT_COLLECTION_MODES,
  listsEqual,
  loadScrapeApplyPreferences,
  normalizeVideoDate,
  pickBestSourceUrl,
  saveScrapeApplyPreferences,
  type CollectionMode,
  type ScrapeApplyPreferences,
} from "./videoScrapeUtils";
import {
  CompactCollectionDecision,
  CompactListValue,
  CompactScalarDecision,
  DEFAULT_TAGGER_BLACKLIST,
  TaggerSettingsPanel,
  TaggerToolbar,
  cleanTaggerQueryString,
} from "./TaggerShared";
import { AlertCircle, Check, FileQuestion, Loader2, Search } from "lucide-react";

type SupportedScraperEntity = "image" | "audio" | "text" | "gallery" | "group";

interface ScraperEntityItem {
  id: number;
  name?: string;
  title?: string;
  aliases?: string;
  duration?: number;
  code?: string;
  date?: string;
  details?: string;
  photographer?: string;
  director?: string;
  synopsis?: string;
  studioName?: string;
  urls: string[];
  imagePath?: string | null;
  frontImagePath?: string | null;
  tags?: Array<{ name: string }>;
  performers?: Array<{ name: string }>;
  updatedAt?: string;
  files?: Array<{ basename?: string; path?: string }>;
}

interface ScraperEntityTaggerProps<T extends ScraperEntityItem> {
  entityType: SupportedScraperEntity;
  label: string;
  items: T[];
  selectedIds?: Set<number>;
  selecting?: boolean;
  onSelect?: (id: number) => void;
  getTitle: (item: T) => string;
  getImageUrl?: (item: T) => string | undefined;
  getRoute?: (item: T) => Route;
  queryKey: string;
}

interface SearchState {
  loading: boolean;
  results?: ScraperResultMatch[];
  error?: string;
  selectedIndex?: number;
  saved?: boolean;
}

interface ScraperResultMatch {
  id: string;
  attemptId: string;
  selectedCandidateIndex?: number;
  scraperName: string;
  title?: string;
  aliases: string[];
  duration?: string;
  code?: string;
  date?: string;
  details?: string;
  director?: string;
  rating?: string;
  studioName?: string;
  creator?: string;
  imageUrl?: string;
  urls: string[];
  performerNames: string[];
  tagNames: string[];
  rawResult: Record<string, unknown>;
}

interface ScraperReviewData {
  title?: string;
  aliases: string[];
  duration?: string;
  code?: string;
  details?: string;
  director?: string;
  rating?: string;
  creator?: string;
  date?: string;
  studio?: string;
  imageUrl?: string;
  urls: string[];
  tags: string[];
  performers: string[];
}

interface ScraperApplyPlan {
  currentData: ScraperReviewData;
  scrapedData: ScraperReviewData | null;
  replaceFields: string[];
  collectionModes: Record<string, CollectionMode>;
}

const CONCURRENCY_LIMIT = 5;
const SCRAPER_TAGGER_QUERY_STORAGE_KEY = "cove.scraperEntityTaggerQuerySettings";

function loadScraperBlacklist(): string[] {
  if (typeof window === "undefined") return [...DEFAULT_TAGGER_BLACKLIST];
  try {
    const raw = window.localStorage.getItem(SCRAPER_TAGGER_QUERY_STORAGE_KEY);
    if (!raw) return [...DEFAULT_TAGGER_BLACKLIST];
    const parsed = JSON.parse(raw) as { blacklist?: string[] };
    return Array.isArray(parsed.blacklist) ? parsed.blacklist : [...DEFAULT_TAGGER_BLACKLIST];
  } catch {
    return [...DEFAULT_TAGGER_BLACKLIST];
  }
}

function saveScraperBlacklist(blacklist: string[]) {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(SCRAPER_TAGGER_QUERY_STORAGE_KEY, JSON.stringify({ blacklist }));
  } catch {
    // Ignore localStorage failures.
  }
}

async function runWithConcurrency<T>(items: T[], fn: (item: T) => Promise<void>, limit: number, signal?: AbortSignal): Promise<void> {
  let index = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (index < items.length) {
      if (signal?.aborted) return;
      const itemIndex = index++;
      await fn(items[itemIndex]);
    }
  });
  await Promise.all(workers);
}

function asString(value: unknown): string | undefined {
  if (typeof value === "string") return value.trim() || undefined;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return undefined;
}

function asStringList(value: unknown): string[] {
  if (Array.isArray(value)) return value.flatMap(asStringList).filter(Boolean);
  const text = asString(value);
  if (!text) return [];
  return text.split(",").map((item) => item.trim()).filter(Boolean);
}

function splitAliases(value?: string) {
  if (!value) return [];
  return value.split(/[,;\n\r]/).map((item) => item.trim()).filter(Boolean);
}

function pickString(result: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const entry = Object.entries(result).find(([entryKey]) => entryKey.toLowerCase() === key.toLowerCase());
    if (!entry) continue;
    const value = asString(entry[1]);
    if (value) return value;
  }
  return undefined;
}

function pickStringList(result: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const entry = Object.entries(result).find(([entryKey]) => entryKey.toLowerCase() === key.toLowerCase());
    if (!entry) continue;
    const values = asStringList(entry[1]);
    if (values.length > 0) return [...new Set(values)];
  }
  return [];
}

function normalizeTagName(value: string) {
  const trimmed = value.trim();
  if (trimmed.startsWith("[") && trimmed.endsWith("]") && trimmed.length >= 2) {
    return trimmed.slice(1, -1).trim();
  }

  return trimmed;
}

function normalizeTagList(values: string[]) {
  return values
    .map((value) => normalizeTagName(value))
    .filter(Boolean)
    .filter((value, index, items) => items.findIndex((candidate) => candidate.toLowerCase() === value.toLowerCase()) === index);
}

function getPerformerNamesForEntity(entityType: SupportedScraperEntity, rawResult: Record<string, unknown>) {
  const explicit = pickStringList(rawResult, "Performers", "Performer", "PerformerNames");
  const legacyValues =
    entityType === "audio"
      ? pickStringList(rawResult, "Artist", "artist", "Creator", "creator", "Author", "author")
      : entityType === "text"
        ? pickStringList(rawResult, "Author", "author", "Creator", "creator", "Artist", "artist")
          : [];

        return entityType === "group" ? [] : [...new Set([...explicit, ...legacyValues])];
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

function mapScraperResult(
  attempt: ScrapeAttempt,
  result: Record<string, unknown>,
  index: number,
  scraper: ScraperSummary,
  entityType: SupportedScraperEntity,
): ScraperResultMatch {
  return {
    id: `${attempt.id}:${index}`,
    attemptId: attempt.id,
    selectedCandidateIndex: index,
    scraperName: scraper.name,
    title: pickString(result, "Title", "Name"),
    aliases: pickStringList(result, "Aliases", "Alias"),
    duration: pickString(result, "Duration", "DurationSeconds"),
    code: pickString(result, "Code"),
    date: normalizeVideoDate(pickString(result, "Date", "ReleaseDate")),
    details: pickString(result, "Details", "Description", "Synopsis"),
    director: pickString(result, "Director"),
    rating: pickString(result, "Rating"),
    studioName: pickString(result, "Studio", "StudioName"),
    creator: entityType === "image" || entityType === "gallery" ? pickString(result, "Photographer") : undefined,
    imageUrl: pickString(result, "Image", "ImageUrl", "ImageURL", "FrontImage", "FrontImageUrl", "FrontImageURL"),
    urls: pickStringList(result, "URLs", "Url", "URL"),
    performerNames: getPerformerNamesForEntity(entityType, result),
    tagNames: normalizeTagList(pickStringList(result, "Tags", "Tag", "TagNames")),
    rawResult: result,
  };
}

function buildCurrentReviewData(item: ScraperEntityItem): ScraperReviewData {
  return {
    title: item.title ?? item.name,
    aliases: normalizeTagList(splitAliases(item.aliases)),
    duration: item.duration == null ? undefined : String(item.duration),
    code: item.code,
    details: item.details ?? item.synopsis,
    director: item.director,
    date: normalizeVideoDate(item.date),
    studio: item.studioName,
    imageUrl: item.frontImagePath ?? item.imagePath ?? undefined,
    urls: item.urls ?? [],
    tags: normalizeTagList((item.tags ?? []).map((tag) => tag.name)),
    performers: (item.performers ?? []).map((performer) => performer.name).filter(Boolean),
    creator: item.photographer,
  };
}

function buildScrapedReviewData(result?: ScraperResultMatch): ScraperReviewData | null {
  if (!result) {
    return null;
  }

  return {
    title: result.title,
    aliases: result.aliases,
    duration: result.duration,
    code: result.code,
    details: result.details,
    director: result.director,
    rating: result.rating,
    creator: result.creator,
    date: result.date,
    studio: result.studioName,
    imageUrl: result.imageUrl,
    urls: result.urls,
    tags: result.tagNames,
    performers: result.performerNames,
  };
}

function buildDefaultApplyPlan(
  entityType: SupportedScraperEntity,
  item: ScraperEntityItem,
  result?: ScraperResultMatch,
): ScraperApplyPlan {
  const currentData = buildCurrentReviewData(item);
  const scrapedData = buildScrapedReviewData(result);

  if (!scrapedData) {
    return {
      currentData,
      scrapedData: null,
      replaceFields: [],
      collectionModes: { ...DEFAULT_COLLECTION_MODES },
    };
  }

  const replaceFields: string[] = [];
  if (scrapedData.title && scrapedData.title !== currentData.title) replaceFields.push(entityType === "group" ? "name" : "title");
  if (scrapedData.code && scrapedData.code !== currentData.code) replaceFields.push("code");
  if (scrapedData.details && scrapedData.details !== currentData.details) replaceFields.push("details");
  if (entityType === "group" && scrapedData.director && scrapedData.director !== currentData.director) replaceFields.push("director");
  if (entityType === "group" && scrapedData.duration && scrapedData.duration !== currentData.duration) replaceFields.push("duration");
  if (entityType === "group" && scrapedData.rating && scrapedData.rating !== currentData.rating) replaceFields.push("rating");
  if (entityType === "group" && scrapedData.imageUrl && scrapedData.imageUrl !== currentData.imageUrl) replaceFields.push("image");
  if ((entityType === "image" || entityType === "gallery") && scrapedData.creator && scrapedData.creator !== currentData.creator) replaceFields.push("photographer");
  if (scrapedData.date && scrapedData.date !== currentData.date) replaceFields.push("date");

  return {
    currentData,
    scrapedData,
    replaceFields,
    collectionModes: {
      studio: scrapedData.studio && scrapedData.studio !== currentData.studio ? "replace" : "skip",
      aliases: entityType === "group" && scrapedData.aliases.length > 0 && !listsEqual(scrapedData.aliases, currentData.aliases) ? "merge" : "skip",
      urls: scrapedData.urls.length > 0 && !listsEqual(scrapedData.urls, currentData.urls) ? "merge" : "skip",
      tags: scrapedData.tags.length > 0 && !listsEqual(scrapedData.tags, currentData.tags) ? "merge" : "skip",
      performers: entityType !== "group" && scrapedData.performers.length > 0 && !listsEqual(scrapedData.performers, currentData.performers) ? "merge" : "skip",
    },
  };
}

function buildApplyRequest(
  result: ScraperResultMatch,
  replaceFields: string[],
  collectionModes: Record<string, CollectionMode>,
  tagActions: ScrapeRelationActionMap,
  performerActions: ScrapeRelationActionMap,
  preferences: ScrapeApplyPreferences,
): ApplyVideoScrapeAttemptRequest {
  return {
    replaceFields,
    collectionModes,
    createMissingTags: preferences.createMissingTags,
    createMissingPerformers: preferences.createMissingPerformers,
    createMissingStudio: preferences.createMissingStudio,
    markOrganized: preferences.markOrganized,
    selectedCandidateIndex: result.selectedCandidateIndex,
    tagSelections: result.tagNames.length > 0 ? buildRelationSelectionPayload(result.tagNames, tagActions) : undefined,
    performerSelections: result.performerNames.length > 0 ? buildRelationSelectionPayload(result.performerNames, performerActions) : undefined,
  };
}

export function ScraperEntityTagger<T extends ScraperEntityItem>({ entityType, label, items, selectedIds, selecting = false, onSelect, getTitle, getImageUrl, getRoute, queryKey }: ScraperEntityTaggerProps<T>) {
  const queryClient = useQueryClient();
  const { data: scraperList = [] } = useQuery({ queryKey: ["scrapers"], queryFn: system.listScrapers });
  const scrapers = scraperList.filter((scraper) => scraper.entityType.toLowerCase() === entityType);
  const [selectedScraperId, setSelectedScraperId] = useState("");
  const selectedScraper = scrapers.find((scraper) => scraper.id === selectedScraperId) ?? scrapers[0];
  const [searchStates, setSearchStates] = useState<Record<number, SearchState>>({});
  const [queryOverrides, setQueryOverrides] = useState<Record<number, string>>({});
  const [batchSearching, setBatchSearching] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [preferences, setPreferences] = useState<ScrapeApplyPreferences>(() => loadScrapeApplyPreferences());
  const [blacklist, setBlacklist] = useState<string[]>(() => loadScraperBlacklist());
  const abortRef = useRef<AbortController | null>(null);
  const batchItems = useMemo(
    () => selectedIds && selectedIds.size > 0 ? items.filter((item) => selectedIds.has(item.id)) : items,
    [items, selectedIds],
  );

  const updatePreferences = useCallback((update: Partial<ScrapeApplyPreferences>) => {
    setPreferences((current) => {
      const next = { ...current, ...update };
      saveScrapeApplyPreferences(next);
      return next;
    });
  }, []);

  const updateBlacklist = useCallback((items: string[]) => {
    setBlacklist(items);
    saveScraperBlacklist(items);
  }, []);

  const updateSearchState = useCallback((id: number, update: Partial<SearchState>) => {
    setSearchStates((prev) => ({ ...prev, [id]: { ...prev[id], ...update } }));
  }, []);

  const getQuery = useCallback((item: T) => {
    if (queryOverrides[item.id] !== undefined) return queryOverrides[item.id];
    if (selectedScraper?.supportedScrapes.some((kind) => kind.toLowerCase() === "url")) {
      return pickBestSourceUrl(item.urls, selectedScraper) ?? cleanTaggerQueryString(getTitle(item), blacklist);
    }
    return cleanTaggerQueryString(getTitle(item), blacklist);
  }, [blacklist, getTitle, queryOverrides, selectedScraper]);

  const searchItem = useCallback(async (item: T) => {
    if (!selectedScraper) return;
    const query = getQuery(item);
    updateSearchState(item.id, { loading: true, error: undefined, results: undefined, saved: false });
    try {
      const supportsUrl = selectedScraper.supportedScrapes.some((kind) => kind.toLowerCase() === "url");
      const supportsName = selectedScraper.supportedScrapes.some((kind) => kind.toLowerCase() === "name");
      const looksLikeUrl = /^https?:\/\//i.test(query.trim());
      const inputKind = supportsUrl && looksLikeUrl ? "url" : supportsName ? "name" : supportsUrl ? "url" : undefined;
      if (!inputKind) throw new Error("This scraper cannot search this row from the available data.");
      const attempt = await scrapeAttempts.create({
        scraperId: selectedScraper.id,
        entityType,
        entityId: item.id,
        inputKind,
        url: inputKind === "url" ? query : undefined,
        name: inputKind === "name" ? query : undefined,
      });
      if (attempt.status.toLowerCase() === "failure") throw new Error(attempt.error || "Scrape returned no results.");
      const results = parseAttemptResults(attempt).map((result, index) => mapScraperResult(attempt, result, index, selectedScraper, entityType));
      updateSearchState(item.id, { loading: false, results, selectedIndex: results.length > 0 ? 0 : undefined });
    } catch (err) {
      updateSearchState(item.id, { loading: false, error: err instanceof Error ? err.message : "Search failed" });
    }
  }, [entityType, getQuery, selectedScraper, updateSearchState]);

  const searchAll = useCallback(async () => {
    setBatchSearching(true);
    const controller = new AbortController();
    abortRef.current = controller;
    const toSearch = batchItems.filter((item) => !searchStates[item.id]?.saved);
    await runWithConcurrency(toSearch, (item) => searchItem(item), CONCURRENCY_LIMIT, controller.signal);
    setBatchSearching(false);
    abortRef.current = null;
  }, [batchItems, searchItem, searchStates]);

  const cancelBatchSearch = useCallback(() => {
    abortRef.current?.abort();
    setBatchSearching(false);
  }, []);

  if (scrapers.length === 0) {
    return (
      <div className="px-4 py-12 text-center">
        <AlertCircle className="w-12 h-12 mx-auto mb-3 text-muted opacity-50" />
        <p className="text-secondary text-lg">No {label} Scrapers Configured</p>
        <p className="text-muted text-sm mt-1">Install or enable a scraper that supports {label.toLowerCase()} metadata.</p>
      </div>
    );
  }

  return (
    <div className="space-y-0">
      <TaggerToolbar
        sources={scrapers.map((scraper) => ({ value: scraper.id, label: scraper.name }))}
        selectedSource={selectedScraper?.id ?? ""}
        onSourceChange={setSelectedScraperId}
        batchSearching={batchSearching}
        onCancelBatch={cancelBatchSearch}
        onRunAll={searchAll}
        countLabel={selectedIds && selectedIds.size > 0 ? `${batchItems.length} selected` : `${items.length} ${label.toLowerCase()}${items.length !== 1 ? "s" : ""}`}
        settingsOpen={showSettings}
        onToggleSettings={() => setShowSettings((current) => !current)}
      />
      {showSettings && (
        <TaggerSettingsPanel blacklist={blacklist} onBlacklistChange={updateBlacklist}>
          <label className="flex items-center gap-2 text-xs text-foreground">
            <input type="checkbox" checked={preferences.createMissingTags} onChange={(event) => updatePreferences({ createMissingTags: event.target.checked })} className="rounded border-border" />
            Create missing tags
          </label>
          {entityType !== "group" && (
            <label className="flex items-center gap-2 text-xs text-foreground">
              <input type="checkbox" checked={preferences.createMissingPerformers} onChange={(event) => updatePreferences({ createMissingPerformers: event.target.checked })} className="rounded border-border" />
              Create missing performers
            </label>
          )}
          <label className="flex items-center gap-2 text-xs text-foreground">
            <input type="checkbox" checked={preferences.createMissingStudio} onChange={(event) => updatePreferences({ createMissingStudio: event.target.checked })} className="rounded border-border" />
            Create missing studios
          </label>
        </TaggerSettingsPanel>
      )}
      <div className="divide-y divide-border">
        {items.map((item) => (
          <ScraperEntityTaggerRow
            key={item.id}
            entityType={entityType}
            item={item}
            title={getTitle(item)}
            imageUrl={getImageUrl?.(item) ?? item.imagePath ?? item.frontImagePath ?? undefined}
            route={getRoute?.(item)}
            state={searchStates[item.id]}
            query={getQuery(item)}
            preferences={preferences}
            onQueryChange={(rowQuery) => setQueryOverrides((prev) => ({ ...prev, [item.id]: rowQuery }))}
            onSearch={() => searchItem(item)}
            onUpdateState={(update) => updateSearchState(item.id, update)}
            selected={selectedIds?.has(item.id) ?? false}
            selecting={selecting}
            onSelect={onSelect}
            onApplied={() => queryClient.invalidateQueries({ queryKey: [queryKey] })}
          />
        ))}
      </div>
    </div>
  );
}

function ScraperEntityTaggerRow({
  entityType,
  item,
  title,
  imageUrl,
  route,
  state,
  query,
  preferences,
  onQueryChange,
  onSearch,
  onUpdateState,
  selected,
  selecting,
  onSelect,
  onApplied,
}: {
  entityType: SupportedScraperEntity;
  item: ScraperEntityItem;
  title: string;
  imageUrl?: string;
  route?: Route;
  state?: SearchState;
  query: string;
  preferences: ScrapeApplyPreferences;
  onQueryChange: (query: string) => void;
  onSearch: () => void;
  onUpdateState: (update: Partial<SearchState>) => void;
  selected: boolean;
  selecting: boolean;
  onSelect?: (id: number) => void;
  onApplied: () => void;
}) {
  const selectedResult = state?.results?.[state.selectedIndex ?? 0];
  const applyPlan = useMemo(() => buildDefaultApplyPlan(entityType, item, selectedResult), [entityType, item, selectedResult]);
  // Resolve which scraped names already exist locally via the same backend matcher the apply path uses
  // (alias-aware for performers), instead of a client-side snapshot of every tag/performer.
  const scrapedRelationNames = useMemo(
    () => ({ tags: applyPlan.scrapedData?.tags ?? [], performers: applyPlan.scrapedData?.performers ?? [] }),
    [applyPlan.scrapedData?.tags, applyPlan.scrapedData?.performers],
  );
  const { data: resolvedRelations } = useQuery({
    queryKey: ["scraper-tagger-resolve-relations", scrapedRelationNames],
    queryFn: () => scrapeAttempts.resolveRelations(scrapedRelationNames),
    enabled: scrapedRelationNames.tags.length > 0 || scrapedRelationNames.performers.length > 0,
    staleTime: 30_000,
  });
  const existingTagNames = useMemo(() => (resolvedRelations?.tags ?? []).map((match) => match.input), [resolvedRelations]);
  const existingPerformerNames = useMemo(() => (resolvedRelations?.performers ?? []).map((match) => match.input), [resolvedRelations]);
  const tagMatchInfo = useMemo(() => buildMatchInfo(resolvedRelations?.tags), [resolvedRelations]);
  const performerMatchInfo = useMemo(() => buildMatchInfo(resolvedRelations?.performers), [resolvedRelations]);
  const [replaceFields, setReplaceFields] = useState<string[]>([]);
  const [collectionModes, setCollectionModes] = useState<Record<string, CollectionMode>>({ ...DEFAULT_COLLECTION_MODES });
  const [tagActions, setTagActions] = useState<ScrapeRelationActionMap>({});
  const [performerActions, setPerformerActions] = useState<ScrapeRelationActionMap>({});
  const itemLinkProps = route ? createNestedRouteLinkProps<HTMLAnchorElement>(route) : undefined;

  useEffect(() => {
    if (!selectedResult || !applyPlan.scrapedData) {
      setReplaceFields([]);
      setCollectionModes({ ...DEFAULT_COLLECTION_MODES });
      setTagActions({});
      setPerformerActions({});
      return;
    }

    setReplaceFields([...applyPlan.replaceFields]);
    setCollectionModes({ ...applyPlan.collectionModes });
    setTagActions(buildRelationActionMap(applyPlan.scrapedData.tags, applyPlan.currentData.tags, existingTagNames, preferences.createMissingTags));
    setPerformerActions(buildRelationActionMap(applyPlan.scrapedData.performers, applyPlan.currentData.performers, existingPerformerNames, preferences.createMissingPerformers));
  }, [applyPlan, existingPerformerNames, existingTagNames, preferences.createMissingPerformers, preferences.createMissingTags, selectedResult]);

  const importMut = useMutation({
    mutationFn: () => {
      if (!selectedResult) throw new Error("No result selected");
      return scrapeAttempts.apply(
        selectedResult.attemptId,
        buildApplyRequest(selectedResult, replaceFields, collectionModes, tagActions, performerActions, preferences),
      );
    },
    onSuccess: () => {
      onUpdateState({ saved: true });
      onApplied();
    },
  });

  const preview = (
    <>
      <div className="relative aspect-video bg-card rounded overflow-hidden flex items-center justify-center">
        {imageUrl ? <img src={imageUrl} alt="" className="w-full h-full object-cover" loading="lazy" /> : <FileQuestion className="w-8 h-8 text-muted" />}
      </div>
      <p className={`mt-0.5 truncate text-[11px] font-medium leading-snug ${itemLinkProps ? "text-accent group-hover/entity:underline" : "text-foreground"}`}>{title}</p>
      <p className="truncate text-[10px] text-muted">{item.studioName || item.files?.[0]?.basename || item.date || ""}</p>
    </>
  );

  return (
    <div className={`px-3 py-2 ${selected ? "bg-accent/5" : ""}`}>
      <div className="flex gap-3">
        {onSelect && (
          <button type="button" onClick={() => onSelect(item.id)} className={`mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded border text-[10px] ${selected ? "border-accent bg-accent text-white" : selecting ? "border-accent/60 text-accent" : "border-border text-transparent hover:border-accent hover:text-accent"}`} aria-label={selected ? "Deselect" : "Select"} title={selected ? "Deselect" : "Select"}>
            <Check className="h-3 w-3" />
          </button>
        )}
        {itemLinkProps ? (
          <a {...itemLinkProps} className="group/entity block w-32 flex-shrink-0" title={`Open ${title}`}>
            {preview}
          </a>
        ) : (
          <div className="w-32 flex-shrink-0">
            {preview}
          </div>
        )}
        <div className="flex-1 min-w-0">
          <div className="flex gap-2 mb-2">
            <input value={query} onChange={(event) => onQueryChange(event.target.value)} onKeyDown={(event) => event.key === "Enter" && onSearch()} placeholder="Search URL or query..." className="flex-1 bg-input border border-border rounded px-3 py-1.5 text-xs text-foreground focus:outline-none focus:border-accent" />
            <button onClick={onSearch} disabled={state?.loading} className="flex items-center gap-1.5 px-3 py-1.5 rounded text-xs font-medium bg-accent text-white hover:bg-accent-hover disabled:opacity-60">
              {state?.loading ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Search className="w-3.5 h-3.5" />}
              Search
            </button>
          </div>
          {state?.error && <p className="text-xs text-red-400 mb-2"><AlertCircle className="w-3 h-3 inline mr-1" />{state.error}</p>}
          {state?.results && state.results.length === 0 && <p className="text-xs text-muted">No matches found.</p>}
          {state?.results && state.results.length > 0 && (
            <div className="space-y-1">
              {state.results.map((result, index) => (
                <ScraperResultRow
                  key={result.id}
                  entityType={entityType}
                  result={result}
                  currentData={applyPlan.currentData}
                  isSelected={index === (state.selectedIndex ?? 0)}
                  replaceFields={replaceFields}
                  collectionModes={collectionModes}
                  tagActions={tagActions}
                  performerActions={performerActions}
                  existingTagNames={existingTagNames}
                  existingPerformerNames={existingPerformerNames}
                  tagMatchInfo={tagMatchInfo}
                  performerMatchInfo={performerMatchInfo}
                  onReplaceFieldChange={(field, shouldReplace) => setReplaceFields((current) => {
                    const isReplacing = current.includes(field);
                    if (isReplacing === shouldReplace) return current;
                    return shouldReplace ? [...current, field] : current.filter((value) => value !== field);
                  })}
                  onCollectionModeChange={(field, mode) => setCollectionModes((current) => ({ ...current, [field]: mode }))}
                  onTagActionChange={(name, action) => setTagActions((current) => ({ ...current, [relationKey(name)]: action }))}
                  onPerformerActionChange={(name, action) => setPerformerActions((current) => ({ ...current, [relationKey(name)]: action }))}
                  onClick={() => onUpdateState({ selectedIndex: index })}
                  onSave={index === (state.selectedIndex ?? 0) ? () => importMut.mutate() : undefined}
                  saving={index === (state.selectedIndex ?? 0) ? importMut.isPending : false}
                  saved={state.saved}
                />
              ))}
            </div>
          )}
          {state?.saved && <div className="flex items-center gap-1 mt-2 text-xs text-green-400"><Check className="w-3.5 h-3.5" />Saved successfully</div>}
        </div>
      </div>
    </div>
  );
}

function ScraperResultRow({
  entityType,
  result,
  currentData,
  isSelected,
  replaceFields,
  collectionModes,
  tagActions,
  performerActions,
  existingTagNames,
  existingPerformerNames,
  tagMatchInfo,
  performerMatchInfo,
  onReplaceFieldChange,
  onCollectionModeChange,
  onTagActionChange,
  onPerformerActionChange,
  onClick,
  onSave,
  saving,
  saved,
}: {
  entityType: SupportedScraperEntity;
  result: ScraperResultMatch;
  currentData: ScraperReviewData;
  isSelected: boolean;
  replaceFields: string[];
  collectionModes: Record<string, CollectionMode>;
  tagActions: ScrapeRelationActionMap;
  performerActions: ScrapeRelationActionMap;
  existingTagNames: string[];
  existingPerformerNames: string[];
  tagMatchInfo?: Record<string, string>;
  performerMatchInfo?: Record<string, string>;
  onReplaceFieldChange: (field: string, shouldReplace: boolean) => void;
  onCollectionModeChange: (field: string, mode: CollectionMode) => void;
  onTagActionChange: (name: string, action: "include" | "create" | "exclude") => void;
  onPerformerActionChange: (name: string, action: "include" | "create" | "exclude") => void;
  onClick: () => void;
  onSave?: () => void;
  saving?: boolean;
  saved?: boolean;
}) {
  const scalarRows = [
    { key: entityType === "group" ? "name" : "title", label: entityType === "group" ? "Name" : "Title", current: currentData.title, scraped: result.title },
    ...(entityType === "group" ? [] : [{ key: "code", label: "Code", current: currentData.code, scraped: result.code }]),
    { key: "details", label: "Details", current: currentData.details, scraped: result.details, multiline: true },
    ...(entityType === "group" ? [
      { key: "director", label: "Director", current: currentData.director, scraped: result.director },
      { key: "duration", label: "Duration", current: currentData.duration, scraped: result.duration },
      { key: "rating", label: "Rating", current: currentData.rating, scraped: result.rating },
      { key: "image", label: "Front image", current: currentData.imageUrl ? "Current front image" : undefined, scraped: result.imageUrl ? "Scraped front image" : undefined },
    ] : []),
    ...(entityType === "image" || entityType === "gallery" ? [{ key: "photographer", label: "Photographer", current: currentData.creator, scraped: result.creator }] : []),
    { key: "date", label: "Date", current: currentData.date, scraped: result.date },
  ].filter((row) => Boolean(row.scraped));

  const collectionRows = [
    ...(entityType === "group" ? [{ key: "aliases", label: "Aliases", current: currentData.aliases, scraped: result.aliases }] : []),
    { key: "urls", label: "URLs", current: currentData.urls, scraped: result.urls },
    { key: "tags", label: "Tags", current: currentData.tags, scraped: result.tagNames },
    ...(entityType === "group" ? [] : [{ key: "performers", label: "Performers", current: currentData.performers, scraped: result.performerNames }]),
  ].filter((row) => row.scraped.length > 0);

  return (
    <div onClick={onClick} className={`rounded border cursor-pointer transition-colors ${isSelected ? "border-accent bg-card" : "border-border bg-surface hover:border-accent/50"}`}>
      <div className="flex items-center gap-3 p-2">
        <div className="flex-shrink-0">
          <div className={`w-4 h-4 rounded-full border-2 flex items-center justify-center ${isSelected ? "border-accent" : "border-border"}`}>
            {isSelected && <div className="w-2 h-2 rounded-full bg-accent" />}
          </div>
        </div>
        {result.imageUrl && <img src={result.imageUrl} alt="" className="w-16 h-10 object-cover rounded flex-shrink-0" loading="lazy" />}
        <div className="flex-1 min-w-0">
          <p className="text-xs font-medium text-foreground truncate">{result.title || "Untitled"}{result.code && <span className="text-muted ml-1">({result.code})</span>}</p>
          {result.details && <p className="mt-1 text-[11px] leading-relaxed text-secondary line-clamp-2">{result.details}</p>}
          <div className="flex items-center gap-2 text-[10px] text-muted mt-0.5">
            {result.date && <span>{result.date}</span>}
            {result.studioName && <span>{result.studioName}</span>}
            {result.tagNames.length > 0 && <span>{result.tagNames.length} tag(s)</span>}
            {entityType !== "group" && result.performerNames.length > 0 && <span>{result.performerNames.length} performer(s)</span>}
          </div>
        </div>
        {isSelected && onSave && !saved && (
          <button onClick={(event) => { event.stopPropagation(); onSave(); }} disabled={saving} className="flex items-center gap-1.5 px-4 py-1.5 rounded text-xs font-medium bg-green-600 text-white hover:bg-green-500 disabled:opacity-60">
            {saving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Check className="w-3.5 h-3.5" />}
            Save
          </button>
        )}
      </div>
      {isSelected && !saved && (
        <div className="border-t border-border px-3 py-2.5 space-y-2">
          {scalarRows.map((row) => (
            <CompactScalarDecision
              key={row.key}
              label={row.label}
              current={row.current}
              scraped={row.scraped}
              multiline={row.multiline}
              replacing={replaceFields.includes(row.key)}
              onChange={(shouldReplace) => onReplaceFieldChange(row.key, shouldReplace)}
            />
          ))}

          {result.studioName && (
            <CompactScalarDecision
              label="Studio"
              current={currentData.studio}
              scraped={result.studioName}
              replacing={collectionModes.studio === "replace"}
              onChange={(shouldReplace) => onCollectionModeChange("studio", shouldReplace ? "replace" : "skip")}
            />
          )}

          {collectionRows.map((row) => {
            const isTags = row.key === "tags";
            const isPerformers = row.key === "performers";
            const relationActions = isTags ? tagActions : performerActions;
            const existingNames = isTags ? existingTagNames : existingPerformerNames;
            const matchInfo = isTags ? tagMatchInfo : performerMatchInfo;

            return (
              <CompactCollectionDecision
                key={row.key}
                label={row.label}
                current={row.current}
                mode={collectionModes[row.key]}
                onModeChange={(mode) => onCollectionModeChange(row.key, mode)}
                scraped={isTags || isPerformers ? (
                  <div onClick={(event) => event.stopPropagation()}>
                    <ScrapeRelationChoices
                      names={row.scraped}
                      currentNames={row.current}
                      existingNames={existingNames}
                      matchInfo={matchInfo}
                      actions={relationActions}
                      disabled={collectionModes[row.key] === "skip"}
                      onActionChange={isTags ? onTagActionChange : onPerformerActionChange}
                    />
                  </div>
                ) : (
                  <CompactListValue values={row.scraped} breakAll />
                )}
              />
            );
          })}
        </div>
      )}
    </div>
  );
}


