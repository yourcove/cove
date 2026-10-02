import { useCallback, useState, useRef } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { performers, system, tags } from "../api/client";
import type {
  Performer,
  MetadataServer,
  MetadataServerPerformerMatch,
  MetadataServerPerformerImportRequest,
  ScrapedPerformer,
  ScraperSummary,
} from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";
import { createNestedRouteLinkProps } from "./cardNavigation";
import { DEFAULT_COLLECTION_MODES, pickBestSourceUrl, type CollectionMode } from "./videoScrapeUtils";
import { buildRelationActionMap, relationKey, type ScrapeRelationActionMap } from "./ScrapeRelationChoices";
import {
  DEFAULT_TAGGER_DENYLIST,
  RemoteRefreshButtons,
  TaggerSettingsPanel,
  TaggerToolbar,
  cleanTaggerQueryString,
} from "./TaggerShared";
import { Search, Loader2, Check, AlertCircle, CloudUpload, Fingerprint } from "lucide-react";
import { toggleOptionsFromEvent, withOrderedToggle, type MultiSelectToggleOptions } from "../hooks/useMultiSelect";
import { MetadataDiff, scalarStatus, summarizeDiff, type DiffSelection } from "./MetadataDiff";
import { MetadataDiffSummary } from "./MetadataDiffSummary";
import { metadataServerLabel } from "./MetadataServerLinks";
import { ReviewCoverPanel } from "./ReviewCoverPanel";
import {
  applyPerformerSelectionChange,
  buildPerformerReview,
  type PerformerReviewInput,
} from "./PerformerTaggerReview";

interface PerformerTaggerProps {
  performers: Performer[];
  selectedIds?: Set<number>;
  selecting?: boolean;
  onSelect?: (performerId: number, options?: MultiSelectToggleOptions) => void;
  onNavigate?: (performerId: number) => void;
  mode?: "bulk" | "detail";
}

interface TaggerConfig {
  selectedEndpoint: string;
  showTagged: boolean;
  createMissingTags: boolean;
  denylist: string[];
}

interface PerformerSearchState {
  loading: boolean;
  results?: UnifiedPerformerMatch[];
  error?: string;
  selectedIndex?: number;
  saved?: boolean;
  fieldStrategies?: Record<string, PerformerFieldStrategy>;
  collectionModes?: Record<string, CollectionMode>;
  tagActions?: ScrapeRelationActionMap;
  /** Which of the source's images is on screen in the review; that one is applied. */
  imageIndex?: number;
}

/** Every per-match decision. A new search or another match starts from these, so nothing chosen for one match applies to the next. */
const CLEARED_DECISIONS = {
  fieldStrategies: undefined,
  collectionModes: undefined,
  tagActions: undefined,
  imageIndex: undefined,
} satisfies Partial<PerformerSearchState>;

type PerformerFieldStrategy = "ignore" | "merge" | "overwrite";
type PerformerInputKind = "url" | "name";

type PerformerSource =
  | { kind: "metadata-server"; value: string; label: string; endpoint: string }
  | { kind: "scraper"; value: string; label: string; scraper: ScraperSummary };

interface UnifiedPerformerMatch extends MetadataServerPerformerMatch {
  sourceKind: "metadata-server" | "scraper";
  scraped?: ScrapedPerformer;
}

const sourceValue = (kind: "metadata-server" | "scraper", id: string) => `${kind}:${id}`;

function resolveSource(value: string, sources: PerformerSource[]): PerformerSource | undefined {
  return (
    sources.find((source) => source.value === value) ??
    sources.find((source) => source.kind === "metadata-server" && source.endpoint === value) ??
    sources[0]
  );
}

function mapScrapedPerformer(scraped: ScrapedPerformer, scraper: ScraperSummary): UnifiedPerformerMatch {
  return {
    sourceKind: "scraper",
    scraped,
    endpoint: scraper.id,
    serverName: scraper.name,
    id: scraper.id,
    name: scraped.name || "Untitled performer",
    disambiguation: scraped.disambiguation,
    gender: scraped.gender,
    birthDate: scraped.birthdate,
    country: scraped.country,
    imageUrl: scraped.imageUrl,
    deleted: false,
    aliases: scraped.aliases ?? [],
    urls: scraped.urls ?? [],
  };
}

const CONCURRENCY_LIMIT = 5;
const PERFORMER_TAGGER_CONFIG_KEY = "cove.performerTaggerConfig";

function loadPerformerTaggerConfig(defaultEndpoint: string): TaggerConfig {
  const fallback: TaggerConfig = {
    selectedEndpoint: defaultEndpoint,
    showTagged: false,
    createMissingTags: true,
    denylist: [...DEFAULT_TAGGER_DENYLIST],
  };
  if (typeof window === "undefined") return fallback;
  try {
    const raw = window.localStorage.getItem(PERFORMER_TAGGER_CONFIG_KEY);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw) as Partial<TaggerConfig>;
    return {
      ...fallback,
      ...parsed,
      selectedEndpoint: parsed.selectedEndpoint ?? fallback.selectedEndpoint,
      denylist: parsed.denylist ?? fallback.denylist,
    };
  } catch {
    return fallback;
  }
}

function savePerformerTaggerConfig(config: TaggerConfig) {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(PERFORMER_TAGGER_CONFIG_KEY, JSON.stringify(config));
  } catch {
    // Ignore localStorage failures.
  }
}

function getPerformerSearchErrorMessage(error: unknown) {
  const rawMessage = error instanceof Error ? error.message : error ? String(error) : "Search failed";
  if (/scrape returned no performer metadata/i.test(rawMessage) || /API Error 404/i.test(rawMessage)) {
    return "No performer metadata was found for this search.";
  }
  return rawMessage;
}

function normalizeDecisionValue(value?: string | number | null) {
  return value === undefined || value === null ? "" : String(value).trim();
}

/** The source's images in its own order; a source with a single image has one candidate. */
function getPerformerImageCandidates(result: UnifiedPerformerMatch): string[] {
  const urls = (result.imageUrls ?? []).filter((url) => url.trim());
  return urls.length > 0 ? urls : result.imageUrl ? [result.imageUrl] : [];
}

function getChosenPerformerImage(result: UnifiedPerformerMatch, state?: PerformerSearchState) {
  const candidates = getPerformerImageCandidates(result);
  return candidates[Math.min(Math.max(state?.imageIndex ?? 0, 0), Math.max(candidates.length - 1, 0))];
}

function getPerformerTagNames(performer: Performer) {
  return performer.tags.map((tag) => tag.name).filter(Boolean);
}

function getPerformerScrapedValue(result: UnifiedPerformerMatch, key: string): string | number | undefined {
  const scraped = result.scraped;
  switch (key) {
    case "name":
      return result.name;
    case "disambiguation":
      return result.disambiguation;
    case "gender":
      return result.gender;
    case "birthdate":
      return result.birthDate ?? scraped?.birthdate;
    case "country":
      return result.country;
    case "ethnicity":
      return scraped?.ethnicity;
    case "eyeColor":
      return scraped?.eyeColor;
    case "hairColor":
      return scraped?.hairColor;
    case "heightCm":
      return scraped?.heightCm;
    case "weight":
      return scraped?.weight;
    case "measurements":
      return scraped?.measurements;
    case "tattoos":
      return scraped?.tattoos;
    case "piercings":
      return scraped?.piercings;
    case "details":
      return scraped?.details;
    default:
      return undefined;
  }
}

function getPerformerCurrentValue(performer: Performer, key: string): string | number | undefined {
  switch (key) {
    case "name":
      return performer.name;
    case "disambiguation":
      return performer.disambiguation;
    case "gender":
      return performer.gender;
    case "birthdate":
      return performer.birthdate;
    case "country":
      return performer.country;
    case "ethnicity":
      return performer.ethnicity;
    case "eyeColor":
      return performer.eyeColor;
    case "hairColor":
      return performer.hairColor;
    case "heightCm":
      return performer.heightCm;
    case "weight":
      return performer.weight;
    case "measurements":
      return performer.measurements;
    case "tattoos":
      return performer.tattoos;
    case "piercings":
      return performer.piercings;
    case "details":
      return performer.details;
    default:
      return undefined;
  }
}

const performerScalarFields = [
  { key: "name", label: "Name" },
  { key: "disambiguation", label: "Disambiguation" },
  { key: "gender", label: "Gender" },
  { key: "birthdate", label: "Birthdate" },
  { key: "country", label: "Country" },
  { key: "ethnicity", label: "Ethnicity" },
  { key: "eyeColor", label: "Eye Color" },
  { key: "hairColor", label: "Hair Color" },
  { key: "heightCm", label: "Height" },
  { key: "weight", label: "Weight" },
  { key: "measurements", label: "Measurements" },
  { key: "tattoos", label: "Tattoos", multiline: true },
  { key: "piercings", label: "Piercings", multiline: true },
  { key: "details", label: "Details", multiline: true },
];

function buildDefaultPerformerFieldStrategies(
  performer: Performer,
  result: UnifiedPerformerMatch,
): Record<string, PerformerFieldStrategy> {
  const strategies: Record<string, PerformerFieldStrategy> = {};
  for (const field of performerScalarFields) {
    const scraped = getPerformerScrapedValue(result, field.key);
    if (scraped === undefined || scraped === null || scraped === "") continue;
    const current = getPerformerCurrentValue(performer, field.key);
    strategies[field.key] =
      normalizeDecisionValue(current) === normalizeDecisionValue(scraped) ? "ignore" : "overwrite";
  }
  // Cover image: replace-if-empty, keep-if-exists. "overwrite" replaces the cover; "ignore" keeps it.
  if (getPerformerImageCandidates(result).length > 0) {
    strategies.image = performer.imagePath ? "ignore" : "overwrite";
  }
  return strategies;
}

function getPerformerFieldStrategies(
  performer: Performer,
  result: UnifiedPerformerMatch,
  state?: PerformerSearchState,
) {
  return { ...buildDefaultPerformerFieldStrategies(performer, result), ...state?.fieldStrategies };
}

function buildDefaultPerformerCollectionModes(result: UnifiedPerformerMatch): Record<string, CollectionMode> {
  return {
    ...DEFAULT_COLLECTION_MODES,
    urls: result.urls.length > 0 ? "merge" : "skip",
    aliases: result.aliases.length > 0 ? "merge" : "skip",
    tags: (result.scraped?.tagNames?.length ?? 0) > 0 ? "merge" : "skip",
  };
}

function getPerformerCollectionModes(result: UnifiedPerformerMatch, state?: PerformerSearchState) {
  return { ...buildDefaultPerformerCollectionModes(result), ...state?.collectionModes };
}

function collectionModeToFieldStrategy(mode: CollectionMode): PerformerFieldStrategy {
  if (mode === "replace") return "overwrite";
  if (mode === "merge") return "merge";
  return "ignore";
}

function buildPerformerFieldStrategies(
  performer: Performer,
  result: UnifiedPerformerMatch,
  state?: PerformerSearchState,
) {
  const scalarStrategies = getPerformerFieldStrategies(performer, result, state);
  const collectionModes = getPerformerCollectionModes(result, state);
  return {
    ...scalarStrategies,
    urls: collectionModeToFieldStrategy(collectionModes.urls),
    aliases: collectionModeToFieldStrategy(collectionModes.aliases),
    tags: collectionModeToFieldStrategy(collectionModes.tags),
  };
}

function buildFilteredScrapedPerformer(
  performer: Performer,
  result: UnifiedPerformerMatch,
  state: PerformerSearchState | undefined,
  createMissingTags: boolean,
  existingTagNames: string[],
): ScrapedPerformer {
  const scraped = result.scraped;
  if (!scraped) throw new Error("No scraped performer selected");
  const fieldStrategies = getPerformerFieldStrategies(performer, result, state);
  const collectionModes = getPerformerCollectionModes(result, state);
  const currentTagNames = getPerformerTagNames(performer);
  const tagActions =
    state?.tagActions ??
    buildRelationActionMap(scraped.tagNames ?? [], currentTagNames, existingTagNames, createMissingTags);
  const selectedTags = (scraped.tagNames ?? []).filter((name) => tagActions[relationKey(name)] !== "exclude");

  const filtered: ScrapedPerformer = {
    urls: collectionModes.urls === "skip" ? [] : (scraped.urls ?? []),
    aliases: collectionModes.aliases === "skip" ? [] : (scraped.aliases ?? []),
    tagNames: collectionModes.tags === "skip" ? [] : selectedTags,
  };
  for (const field of performerScalarFields) {
    if (fieldStrategies[field.key] !== "overwrite") continue;
    const value = getPerformerScrapedValue(result, field.key);
    if (value === undefined || value === null || value === "") continue;
    (filtered as unknown as Record<string, unknown>)[field.key] = value;
  }
  const chosenImage = getChosenPerformerImage(result, state);
  if (chosenImage) filtered.imageUrl = chosenImage;
  return filtered;
}

async function runWithConcurrency<T>(
  items: T[],
  fn: (item: T) => Promise<void>,
  limit: number,
  signal?: AbortSignal,
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

export function PerformerTagger({
  performers: performerList,
  selectedIds,
  selecting = false,
  onSelect,
  onNavigate,
  mode = "bulk",
}: PerformerTaggerProps) {
  const { config } = useAppConfig();
  const metadataServers = config?.scraping?.metadataServers ?? [];
  const { data: scraperList = [] } = useQuery({ queryKey: ["scrapers"], queryFn: system.listScrapers });
  const { data: tagPage } = useQuery({
    queryKey: ["performer-tagger-tags"],
    queryFn: () => tags.find({ page: 1, perPage: 10000, sort: "name", direction: "asc" }),
    enabled: performerList.length > 0,
    staleTime: 60_000,
  });
  const performerScrapers = scraperList.filter((scraper) => scraper.entityType.toLowerCase() === "performer");
  const sources: PerformerSource[] = [
    ...metadataServers.map((server) => ({
      kind: "metadata-server" as const,
      value: sourceValue("metadata-server", server.endpoint),
      label: server.name || server.endpoint,
      endpoint: server.endpoint,
    })),
    ...performerScrapers.map((scraper) => ({
      kind: "scraper" as const,
      value: sourceValue("scraper", scraper.id),
      label: `${scraper.name} (Scraper)`,
      scraper,
    })),
  ];

  const [taggerConfig, _setTaggerConfig] = useState<TaggerConfig>(() =>
    loadPerformerTaggerConfig(metadataServers[0] ? sourceValue("metadata-server", metadataServers[0].endpoint) : ""),
  );
  const setTaggerConfig = useCallback((updater: TaggerConfig | ((current: TaggerConfig) => TaggerConfig)) => {
    _setTaggerConfig((current) => {
      const next = typeof updater === "function" ? updater(current) : updater;
      savePerformerTaggerConfig(next);
      return next;
    });
  }, []);

  const [searchStates, setSearchStates] = useState<Record<number, PerformerSearchState>>({});
  const [queryOverrides, setQueryOverrides] = useState<Record<number, string>>({});
  const [scraperInputKinds, setScraperInputKinds] = useState<Record<number, PerformerInputKind>>({});
  const [showSettings, setShowSettings] = useState(false);
  const selectedSource = resolveSource(taggerConfig.selectedEndpoint, sources);
  const existingTagNames = (tagPage?.items ?? []).map((tag) => tag.name);

  const getScraperInputKind = useCallback(
    (performer: Performer, source: PerformerSource | undefined): PerformerInputKind => {
      if (source?.kind !== "scraper") {
        return "name";
      }

      if (scraperInputKinds[performer.id]) {
        return scraperInputKinds[performer.id];
      }

      const supportsUrl = source.scraper.supportedScrapes.some((kind) => kind.toLowerCase() === "url");
      return supportsUrl && performer.urls.some((url) => url.trim()) ? "url" : "name";
    },
    [scraperInputKinds],
  );

  const getQuery = useCallback(
    (performer: Performer) => {
      if (queryOverrides[performer.id] !== undefined) {
        return queryOverrides[performer.id];
      }

      if (selectedSource?.kind === "scraper" && getScraperInputKind(performer, selectedSource) === "url") {
        return pickBestSourceUrl(performer.urls, selectedSource.scraper) ?? "";
      }

      return cleanTaggerQueryString(performer.name, taggerConfig.denylist);
    },
    [getScraperInputKind, queryOverrides, selectedSource, taggerConfig.denylist],
  );

  const updateSearchState = useCallback((performerId: number, update: Partial<PerformerSearchState>) => {
    setSearchStates((prev) => ({ ...prev, [performerId]: { ...prev[performerId], ...update } }));
  }, []);

  const handleScraperInputKindChange = useCallback(
    (performer: Performer, source: PerformerSource | undefined, inputKind: PerformerInputKind) => {
      setScraperInputKinds((prev) => ({ ...prev, [performer.id]: inputKind }));
      setQueryOverrides((prev) => ({
        ...prev,
        [performer.id]:
          inputKind === "url"
            ? ((source?.kind === "scraper"
                ? pickBestSourceUrl(performer.urls, source.scraper)
                : performer.urls.find((url) => url.trim())) ?? "")
            : cleanTaggerQueryString(performer.name, taggerConfig.denylist),
      }));
    },
    [taggerConfig.denylist],
  );

  const searchPerformer = useCallback(
    async (performer: Performer) => {
      const source = selectedSource;
      const query = getQuery(performer);
      updateSearchState(performer.id, {
        loading: true,
        error: undefined,
        results: undefined,
        saved: false,
        ...CLEARED_DECISIONS,
      });
      try {
        let results: UnifiedPerformerMatch[];
        if (source?.kind === "scraper") {
          const inputKind = getScraperInputKind(performer, source);
          const supportsUrl = source.scraper.supportedScrapes.some((kind) => kind.toLowerCase() === "url");
          const supportsName = source.scraper.supportedScrapes.some((kind) => kind.toLowerCase() === "name");
          if (inputKind === "url" && !supportsUrl) throw new Error("This scraper does not support URL input.");
          if (inputKind === "name" && !supportsName) throw new Error("This scraper does not support name input.");
          if (!query.trim())
            throw new Error(
              inputKind === "url" ? "Enter a performer URL to scrape." : "Enter a performer name to scrape.",
            );
          const preview = await performers.previewScrape(performer.id, {
            scraperId: source.scraper.id,
            inputKind,
            url: inputKind === "url" ? query : undefined,
            name: inputKind === "name" ? query : undefined,
          });
          results = [mapScrapedPerformer(preview.scraped, source.scraper)];
        } else {
          const endpoint = source?.endpoint || undefined;
          results = (await performers.searchMetadataServer(performer.id, query, endpoint)).map((match) => ({
            ...match,
            sourceKind: "metadata-server" as const,
          }));
        }
        updateSearchState(performer.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
        });
      } catch (err) {
        updateSearchState(performer.id, {
          loading: false,
          error: getPerformerSearchErrorMessage(err),
        });
      }
    },
    [getQuery, getScraperInputKind, selectedSource, updateSearchState],
  );

  const [batchSearching, setBatchSearching] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const searchAll = useCallback(async () => {
    setBatchSearching(true);
    const controller = new AbortController();
    abortRef.current = controller;
    const toSearch = performerList.filter((p) => !searchStates[p.id]?.saved);
    await runWithConcurrency(toSearch, (p) => searchPerformer(p), CONCURRENCY_LIMIT, controller.signal);
    setBatchSearching(false);
    abortRef.current = null;
  }, [performerList, searchStates, searchPerformer]);

  const cancelBatchSearch = useCallback(() => {
    abortRef.current?.abort();
    setBatchSearching(false);
  }, []);

  if (sources.length === 0) {
    return (
      <div className="px-4 py-12 text-center">
        <AlertCircle className="w-12 h-12 mx-auto mb-3 text-muted opacity-50" />
        <p className="text-secondary text-lg">No Metadata Sources Configured</p>
        <p className="text-muted text-sm mt-1">
          Add a metadata server or install a performer scraper to use the tagger.
        </p>
      </div>
    );
  }

  // The "hide already tagged" filter is a bulk-mode convenience for skipping done performers in a
  // batch. In detail mode the dialog was opened for this specific performer (and the toggle is
  // hidden), so always show it — otherwise an already-tagged performer yields an empty dialog.
  const visiblePerformers =
    mode === "detail" || taggerConfig.showTagged
      ? performerList
      : performerList.filter((p) => !p.remoteIds || p.remoteIds.length === 0);
  const visiblePerformerIds = visiblePerformers.map((performer) => performer.id);

  return (
    <div className="space-y-0">
      <TaggerToolbar
        sources={sources.map((source) => ({ value: source.value, label: source.label }))}
        selectedSource={selectedSource?.value ?? taggerConfig.selectedEndpoint}
        onSourceChange={(value) => {
          setTaggerConfig((current) => ({ ...current, selectedEndpoint: value }));
          setQueryOverrides({});
          setScraperInputKinds({});
        }}
        showToggle={
          mode === "bulk"
            ? {
                value: taggerConfig.showTagged,
                onChange: (value) => setTaggerConfig((current) => ({ ...current, showTagged: value })),
                enabledLabel: "Hide Already Tagged",
                disabledLabel: "Show Already Tagged",
              }
            : undefined
        }
        batchSearching={batchSearching}
        onCancelBatch={cancelBatchSearch}
        onRunAll={searchAll}
        showRunAll={mode === "bulk"}
        countLabel={`${visiblePerformers.length} performer${visiblePerformers.length !== 1 ? "s" : ""}`}
        settingsOpen={showSettings}
        onToggleSettings={() => setShowSettings((current) => !current)}
      />
      {showSettings && (
        <TaggerSettingsPanel
          denylist={taggerConfig.denylist}
          onDenylistChange={(items) => setTaggerConfig((current) => ({ ...current, denylist: items }))}
        >
          <label className="flex items-center gap-2 text-xs text-foreground">
            <input
              type="checkbox"
              checked={taggerConfig.createMissingTags}
              onChange={(event) =>
                setTaggerConfig((current) => ({ ...current, createMissingTags: event.target.checked }))
              }
              className="rounded border-border"
            />
            Create missing tags
          </label>
        </TaggerSettingsPanel>
      )}

      {/* Performer list */}
      <div className="divide-y divide-border">
        {visiblePerformers.map((performer) => (
          <PerformerTaggerRow
            key={performer.id}
            performer={performer}
            state={searchStates[performer.id]}
            query={getQuery(performer)}
            onQueryChange={(q) => setQueryOverrides((prev) => ({ ...prev, [performer.id]: q }))}
            scraperInputKind={getScraperInputKind(performer, selectedSource)}
            onScraperInputKindChange={(inputKind) => handleScraperInputKindChange(performer, selectedSource, inputKind)}
            onSearch={() => searchPerformer(performer)}
            onUpdateState={(update) => updateSearchState(performer.id, update)}
            source={selectedSource}
            selected={selectedIds?.has(performer.id) ?? false}
            selecting={selecting}
            onSelect={onSelect ? withOrderedToggle(onSelect, visiblePerformerIds) : undefined}
            onNavigate={onNavigate}
            metadataServers={metadataServers}
            taggerConfig={taggerConfig}
            existingTagNames={existingTagNames}
            detailMode={mode === "detail"}
          />
        ))}
      </div>
    </div>
  );
}

function PerformerTaggerRow({
  performer,
  state,
  query,
  onQueryChange,
  scraperInputKind,
  onScraperInputKindChange,
  onSearch,
  onUpdateState,
  source,
  selected,
  selecting,
  onSelect,
  onNavigate,
  metadataServers,
  taggerConfig,
  existingTagNames,
  detailMode = false,
}: {
  performer: Performer;
  state?: PerformerSearchState;
  query: string;
  onQueryChange: (q: string) => void;
  scraperInputKind: PerformerInputKind;
  onScraperInputKindChange: (inputKind: PerformerInputKind) => void;
  onSearch: () => void;
  onUpdateState: (update: Partial<PerformerSearchState>) => void;
  source?: PerformerSource;
  selected: boolean;
  selecting: boolean;
  onSelect?: (performerId: number, options?: MultiSelectToggleOptions) => void;
  onNavigate?: (performerId: number) => void;
  metadataServers: MetadataServer[];
  taggerConfig: TaggerConfig;
  existingTagNames: string[];
  detailMode?: boolean;
}) {
  const imageUrl = performer.imagePath;
  const queryClient = useQueryClient();
  const [refreshBusyEndpoint, setRefreshBusyEndpoint] = useState<string | null>(null);

  const refreshFromRemote = useCallback(
    async (endpoint: string, remoteId: string) => {
      setRefreshBusyEndpoint(endpoint);
      onUpdateState({ loading: true, error: undefined, results: undefined, saved: false, ...CLEARED_DECISIONS });
      try {
        const matches = await performers.findMetadataServerByIds({ endpoint, ids: [remoteId] });
        onUpdateState({
          loading: false,
          results: matches.map((match) => ({ ...match, sourceKind: "metadata-server" as const })),
          selectedIndex: matches.length > 0 ? 0 : undefined,
          error: matches.length === 0 ? "No metadata-server entry found for this remote id." : undefined,
        });
      } catch (err) {
        onUpdateState({ loading: false, error: getPerformerSearchErrorMessage(err) });
      } finally {
        setRefreshBusyEndpoint(null);
      }
    },
    [onUpdateState],
  );
  const performerLinkProps = createNestedRouteLinkProps<HTMLAnchorElement>(
    { page: "performer", id: performer.id },
    () => onNavigate?.(performer.id),
  );
  const isScraperSource = source?.kind === "scraper";
  const performerUrls = (performer.urls ?? []).filter((url) => url.trim());
  const selectedUrlOption = performerUrls.includes(query) ? query : "__custom";
  const searchPlaceholder = isScraperSource
    ? scraperInputKind === "url"
      ? "Performer URL..."
      : "Performer name..."
    : "Search query...";

  const importMut = useMutation({
    mutationFn: () => {
      const selectedResult = state?.results?.[state.selectedIndex ?? 0];
      if (!selectedResult) throw new Error("No result selected");
      if (selectedResult.sourceKind === "scraper") {
        if (!selectedResult.scraped) throw new Error("No scraped performer selected");
        const tagActions =
          state?.tagActions ??
          buildRelationActionMap(
            selectedResult.scraped.tagNames ?? [],
            getPerformerTagNames(performer),
            existingTagNames,
            taggerConfig.createMissingTags,
          );
        const forceCreateTags = Object.values(tagActions).some((action) => action === "create");
        return performers.applyScraped(performer.id, {
          scraped: buildFilteredScrapedPerformer(
            performer,
            selectedResult,
            state,
            taggerConfig.createMissingTags,
            existingTagNames,
          ),
          createMissingTags: taggerConfig.createMissingTags || forceCreateTags,
          // "image" is included only when its decision is "overwrite" (it lives in fieldStrategies now),
          // so the cover is replaced only when the user chose Replace.
          replaceFields: Object.entries(getPerformerFieldStrategies(performer, selectedResult, state))
            .filter(([, strategy]) => strategy === "overwrite")
            .map(([field]) => field),
          collectionModes: getPerformerCollectionModes(selectedResult, state),
        });
      }
      const importReq: MetadataServerPerformerImportRequest = {
        endpoint: selectedResult.endpoint,
        performerId: selectedResult.id,
        fieldStrategies: buildPerformerFieldStrategies(performer, selectedResult, state),
        // The image on screen in the review, so the import stores that one instead of the source's first.
        imageUrl: getChosenPerformerImage(selectedResult, state),
      };
      return performers.importFromMetadataServer(performer.id, importReq);
    },
    onSuccess: () => {
      onUpdateState({ saved: true });
      queryClient.invalidateQueries({ queryKey: ["performer", performer.id] });
      queryClient.invalidateQueries({ queryKey: ["performers"] });
    },
  });

  const submitDraftMut = useMutation<{ draftId: string | null }, Error>({
    meta: { suppressGlobalError: true },
    mutationFn: () => {
      if (source?.kind !== "metadata-server") throw new Error("Select a metadata-server source first.");
      return performers.submitMetadataServerDraft(performer.id, source.endpoint);
    },
  });

  const preview = (
    <>
      <div className="relative aspect-[2/3] bg-card rounded overflow-hidden">
        {imageUrl ? (
          <img src={imageUrl} alt="" className="w-full h-full object-cover" loading="lazy" />
        ) : (
          <div className="w-full h-full flex items-center justify-center text-muted text-xs">No Image</div>
        )}
      </div>
      <p className="mt-0.5 truncate text-[11px] font-medium leading-snug text-accent group-hover/performer:underline">
        {performer.name}
      </p>
      <p className="truncate text-[10px] text-muted">
        {performer.disambiguation || performer.country || performer.birthdate || ""}
      </p>
      {performer.remoteIds && performer.remoteIds.length > 0 && (
        <div className="mt-1 flex flex-wrap gap-1">
          {performer.remoteIds.map((sid) => (
            <span
              key={`${sid.endpoint}-${sid.remoteId}`}
              className="text-[9px] px-1.5 py-0.5 rounded bg-green-600/20 text-green-300"
              title={sid.endpoint}
            >
              <Fingerprint className="w-2.5 h-2.5 inline mr-0.5" />
              {sid.remoteId.substring(0, 8)}…
            </span>
          ))}
        </div>
      )}
    </>
  );

  return (
    <div className={`px-3 py-2 ${selected ? "bg-accent/5" : ""}`}>
      {/* Wraps on a phone: the performer on its own line, then the query and the review at full width. */}
      <div className="flex flex-wrap gap-3 sm:flex-nowrap">
        {onSelect && (
          <button
            type="button"
            onClick={(event) => onSelect(performer.id, toggleOptionsFromEvent(event))}
            className={`mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded border text-[10px] ${selected ? "border-accent bg-accent text-white" : selecting ? "border-accent/60 text-accent" : "border-border text-transparent hover:border-accent hover:text-accent"}`}
            aria-label={selected ? "Deselect performer" : "Select performer"}
            title={selected ? "Deselect" : "Select"}
          >
            <Check className="h-3 w-3" />
          </button>
        )}
        <a
          {...performerLinkProps}
          className="group/performer block w-20 flex-shrink-0 sm:w-28"
          title={`Open performer ${performer.name}`}
        >
          {preview}
        </a>

        {/* Search + Results */}
        <div className="min-w-0 flex-[1_1_100%] sm:flex-1">
          {detailMode && isScraperSource && (
            <div className="mb-2 flex flex-wrap items-center gap-2">
              <select
                value={scraperInputKind}
                onChange={(event) => onScraperInputKindChange(event.target.value as PerformerInputKind)}
                className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
              >
                <option
                  value="url"
                  disabled={!source.scraper.supportedScrapes.some((kind) => kind.toLowerCase() === "url")}
                >
                  URL
                </option>
                <option
                  value="name"
                  disabled={!source.scraper.supportedScrapes.some((kind) => kind.toLowerCase() === "name")}
                >
                  Name
                </option>
              </select>
              {scraperInputKind === "url" && performerUrls.length > 0 ? (
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
                  {performerUrls.map((url) => (
                    <option key={url} value={url}>
                      {url}
                    </option>
                  ))}
                </select>
              ) : null}
            </div>
          )}
          {detailMode && (
            <RemoteRefreshButtons
              remoteIds={performer.remoteIds}
              servers={metadataServers}
              busyEndpoint={refreshBusyEndpoint}
              onRefresh={refreshFromRemote}
            />
          )}
          <div className="flex gap-2 mb-2">
            <input
              type="text"
              value={query}
              onChange={(e) => onQueryChange(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && onSearch()}
              placeholder={searchPlaceholder}
              className="flex-1 min-w-0 bg-input border border-border rounded px-3 py-1.5 text-xs text-foreground focus:outline-none focus:border-accent"
            />
            <button
              onClick={onSearch}
              disabled={state?.loading}
              aria-label="Search"
              className="flex shrink-0 items-center gap-1.5 px-3 py-1.5 rounded text-xs font-medium bg-accent text-white hover:bg-accent-hover disabled:opacity-60"
            >
              {state?.loading ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Search className="w-3.5 h-3.5" />}
              <span className="hidden sm:inline">Search</span>
            </button>
            {source?.kind === "metadata-server" && (
              <button
                onClick={() => submitDraftMut.mutate()}
                disabled={submitDraftMut.isPending}
                className="flex shrink-0 items-center gap-1 px-2 py-1.5 rounded text-xs bg-surface border border-border text-muted hover:text-foreground disabled:opacity-60"
                title="Submit this performer as a draft entry to the metadata server"
              >
                {submitDraftMut.isPending ? (
                  <Loader2 className="w-3.5 h-3.5 animate-spin" />
                ) : (
                  <CloudUpload className="w-3.5 h-3.5" />
                )}
              </button>
            )}
          </div>

          {submitDraftMut.isError && (
            <p className="text-xs text-red-400 mb-2">
              <AlertCircle className="w-3 h-3 inline mr-1" />
              {submitDraftMut.error.message}
            </p>
          )}
          {submitDraftMut.isSuccess && (
            <p className="text-xs text-green-400 mb-2">
              <Check className="w-3 h-3 inline mr-1" />
              Performer draft submitted{submitDraftMut.data.draftId ? ` (${submitDraftMut.data.draftId})` : ""}.
            </p>
          )}

          {state?.error && (
            <p className="text-xs text-red-400 mb-2">
              <AlertCircle className="w-3 h-3 inline mr-1" />
              {state.error}
            </p>
          )}

          {state?.results && state.results.length === 0 && <p className="text-xs text-muted">No matches found.</p>}

          {state?.results && state.results.length > 0 && (
            <div className="space-y-1">
              {state.results.map((result, i) => (
                <PerformerResultRow
                  key={`${result.endpoint}-${result.id}`}
                  performer={performer}
                  result={result}
                  isSelected={i === (state.selectedIndex ?? 0)}
                  fieldStrategies={getPerformerFieldStrategies(performer, result, state)}
                  collectionModes={getPerformerCollectionModes(result, state)}
                  tagActions={
                    state.tagActions ??
                    buildRelationActionMap(
                      result.scraped?.tagNames ?? [],
                      getPerformerTagNames(performer),
                      existingTagNames,
                      taggerConfig.createMissingTags,
                    )
                  }
                  existingTagNames={existingTagNames}
                  createMissingTags={taggerConfig.createMissingTags}
                  showSelector={state.results!.length > 1}
                  sourceName={
                    result.sourceKind === "scraper"
                      ? (source?.label ?? "the scraper").replace(/ \(Scraper\)$/, "")
                      : result.serverName || metadataServerLabel(result.endpoint, metadataServers)
                  }
                  imageIndex={i === (state.selectedIndex ?? 0) ? (state.imageIndex ?? 0) : 0}
                  onImageIndexChange={(imageIndex) => onUpdateState({ imageIndex })}
                  onFieldStrategyChange={(field, strategy) =>
                    onUpdateState({
                      fieldStrategies: { ...getPerformerFieldStrategies(performer, result, state), [field]: strategy },
                    })
                  }
                  onCollectionModeChange={(field, mode) =>
                    onUpdateState({ collectionModes: { ...getPerformerCollectionModes(result, state), [field]: mode } })
                  }
                  onTagActionsChange={(actions) =>
                    onUpdateState({
                      tagActions: {
                        ...(state.tagActions ??
                          buildRelationActionMap(
                            result.scraped?.tagNames ?? [],
                            getPerformerTagNames(performer),
                            existingTagNames,
                            taggerConfig.createMissingTags,
                          )),
                        ...actions,
                      },
                    })
                  }
                  onClick={() =>
                    onUpdateState(
                      i === (state.selectedIndex ?? 0)
                        ? { selectedIndex: i }
                        : {
                            selectedIndex: i,
                            ...CLEARED_DECISIONS,
                          },
                    )
                  }
                  onSave={i === (state.selectedIndex ?? 0) ? () => importMut.mutate() : undefined}
                  saving={i === (state.selectedIndex ?? 0) ? importMut.isPending : false}
                  saved={state.saved}
                />
              ))}
            </div>
          )}

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

function PerformerResultRow({
  performer,
  result,
  isSelected,
  showSelector,
  sourceName,
  fieldStrategies,
  collectionModes,
  tagActions,
  existingTagNames,
  imageIndex,
  onImageIndexChange,
  onFieldStrategyChange,
  onCollectionModeChange,
  onTagActionsChange,
  onClick,
  onSave,
  saving,
  saved,
}: {
  performer: Performer;
  result: UnifiedPerformerMatch;
  isSelected: boolean;
  showSelector: boolean;
  sourceName: string;
  fieldStrategies: Record<string, PerformerFieldStrategy>;
  collectionModes: Record<string, CollectionMode>;
  tagActions: ScrapeRelationActionMap;
  existingTagNames: string[];
  createMissingTags: boolean;
  imageIndex: number;
  onImageIndexChange: (index: number) => void;
  onFieldStrategyChange: (field: string, strategy: PerformerFieldStrategy) => void;
  onCollectionModeChange: (field: string, mode: CollectionMode) => void;
  onTagActionsChange: (actions: Record<string, "include" | "create" | "exclude">) => void;
  onClick: () => void;
  onSave?: () => void;
  saving?: boolean;
  saved?: boolean;
}) {
  // Accept-all is the common case, so the review opens as a list of facts; the full side-by-side
  // rows are one click away for per-item chips.
  const [adjusting, setAdjusting] = useState(false);
  const currentTagNames = getPerformerTagNames(performer);
  const scrapedTagNames = result.scraped?.tagNames ?? [];
  const candidates = getPerformerImageCandidates(result);
  const activeImage = candidates[Math.min(Math.max(imageIndex, 0), Math.max(candidates.length - 1, 0))];
  const reviewInput: PerformerReviewInput = {
    sourceName,
    scalars: performerScalarFields
      .map((field) => ({
        key: field.key,
        label: field.label,
        current: getPerformerCurrentValue(performer, field.key),
        scraped: getPerformerScrapedValue(result, field.key),
      }))
      .filter(
        (field): field is typeof field & { scraped: string | number } =>
          field.scraped !== undefined && field.scraped !== null && field.scraped !== "",
      ),
    fieldStrategies,
    collectionModes,
    currentImageUrl: performer.imagePath,
    incomingImageUrl: activeImage,
    urls: { current: performer.urls ?? [], incoming: result.urls },
    aliases: { current: performer.aliases ?? [], incoming: result.aliases },
    tags: {
      current: currentTagNames,
      incoming: scrapedTagNames,
      existing: existingTagNames,
      actions: tagActions,
    },
  };
  const review = isSelected ? buildPerformerReview(reviewInput) : null;
  const summary = review ? summarizeDiff(review.fields, review.source, review.target, review.selection) : null;
  const imageField = review?.fields.find((field) => field.key === "image");
  const handleSelectionChange = (next: DiffSelection) => {
    if (!review) return;
    applyPerformerSelectionChange(reviewInput, review.selection, next, {
      onFieldStrategyChange,
      onCollectionModeChange,
      onTagActionsChange,
    });
  };
  const identity = [result.disambiguation ? `(${result.disambiguation})` : null, sourceName]
    .filter(Boolean)
    .join(" · ");
  const facts = [result.gender, result.country, result.birthDate].filter(Boolean).join(" · ");

  return (
    <div
      onClick={onClick}
      className={`rounded-lg border transition-colors ${
        isSelected ? "border-accent/70 bg-card" : "cursor-pointer border-border bg-surface hover:border-accent/50"
      }`}
    >
      <div
        role={showSelector && !isSelected ? "button" : undefined}
        tabIndex={showSelector && !isSelected ? 0 : undefined}
        aria-label={showSelector && !isSelected ? `Use ${result.name}` : undefined}
        onKeyDown={(event) => {
          if (showSelector && !isSelected && (event.key === "Enter" || event.key === " ")) {
            event.preventDefault();
            onClick();
          }
        }}
        className="flex flex-wrap items-center gap-2.5 px-3 py-2 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-accent"
      >
        {showSelector && (
          <div
            aria-hidden="true"
            className={`flex h-4 w-4 shrink-0 items-center justify-center rounded-full border-2 ${isSelected ? "border-accent" : "border-border"}`}
          >
            {isSelected && <div className="h-2 w-2 rounded-full bg-accent" />}
          </div>
        )}
        {!isSelected && result.imageUrl && (
          <img src={result.imageUrl} alt="" className="h-14 w-10 shrink-0 rounded object-cover" loading="lazy" />
        )}
        {review && imageField ? (
          <div onClick={(event) => event.stopPropagation()} className="w-full sm:w-auto">
            <ReviewCoverPanel
              status={scalarStatus(imageField, review.source, review.target)}
              chosen={review.selection.image === "source" ? "source" : "target"}
              currentUrl={performer.imagePath}
              candidates={candidates}
              activeIndex={imageIndex}
              onActiveIndexChange={onImageIndexChange}
              incomingLabel={sourceName}
              onChoose={(side) => handleSelectionChange({ ...review.selection, image: side })}
              disabled={saving}
              aspect="portrait"
              subject="Image"
            />
          </div>
        ) : null}
        <div className="min-w-0 flex-1 self-start">
          <p
            className={`text-foreground ${isSelected ? "text-base font-semibold leading-snug" : "truncate text-[13px] font-semibold"}`}
          >
            {result.name}
          </p>
          {identity ? (
            <p className={isSelected ? "text-sm text-secondary" : "text-[11px] text-secondary"}>{identity}</p>
          ) : null}
          {!isSelected && facts ? <p className="truncate text-[11px] text-muted">{facts}</p> : null}
        </div>
      </div>

      {review && !saved && (
        <div className="border-t border-border" onClick={(event) => event.stopPropagation()}>
          {adjusting ? (
            <div className="px-3 py-3">
              <MetadataDiff
                fields={review.fields}
                source={review.source}
                target={review.target}
                value={review.selection}
                onChange={handleSelectionChange}
                disabled={saving}
              />
            </div>
          ) : (
            <div className="py-1">
              <MetadataDiffSummary
                fields={review.fields.filter((field) => field.key !== "image")}
                source={review.source}
                target={review.target}
                value={review.selection}
                onChange={handleSelectionChange}
                disabled={saving}
              />
            </div>
          )}
          <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-t border-border bg-surface/60 px-3 py-2">
            {onSave && (
              <button
                onClick={onSave}
                disabled={saving}
                className="flex items-center gap-1.5 rounded px-4 py-1.5 text-xs font-medium bg-green-600 text-white hover:bg-green-500 disabled:opacity-60"
              >
                {saving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Check className="w-3.5 h-3.5" />}
                {summary?.changeCount
                  ? `Apply ${summary.changeCount} ${summary.changeCount === 1 ? "change" : "changes"}`
                  : "Apply"}
              </button>
            )}
            {summary ? (
              <span className="hidden min-w-0 flex-1 truncate text-[11px] text-muted sm:inline">
                {summary.changes.map((change) => change.text).join(" · ")}
              </span>
            ) : null}
            <button
              type="button"
              aria-expanded={adjusting}
              onClick={() => setAdjusting((current) => !current)}
              className="ml-auto text-xs text-accent hover:underline"
            >
              {adjusting ? "Done adjusting" : "Adjust…"}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
