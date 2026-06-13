import { useCallback, useState, useRef } from "react";
import { useMutation } from "@tanstack/react-query";
import { studios } from "../api/client";
import type { Studio, MetadataServer, MetadataServerStudioMatch, MetadataServerStudioImportRequest } from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";
import { DEFAULT_COLLECTION_MODES, type CollectionMode } from "./videoScrapeUtils";
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
} from "./TaggerShared";
import {
  Search, Loader2, Check, AlertCircle, Fingerprint, CloudUpload,
} from "lucide-react";

interface StudioTaggerProps {
  studios: Studio[];
  selectedIds?: Set<number>;
  selecting?: boolean;
  onSelect?: (studioId: number) => void;
  mode?: "bulk" | "detail";
}

interface TaggerConfig {
  selectedEndpoint: string;
  showTagged: boolean;
  blacklist: string[];
}

interface StudioSearchState {
  loading: boolean;
  results?: MetadataServerStudioMatch[];
  error?: string;
  selectedIndex?: number;
  saved?: boolean;
  fieldStrategies?: Record<string, StudioFieldStrategy>;
  collectionModes?: Record<string, CollectionMode>;
}

type StudioFieldStrategy = "ignore" | "merge" | "overwrite";

const CONCURRENCY_LIMIT = 5;
async function runWithConcurrency<T>(items: T[], fn: (item: T) => Promise<void>, limit: number, signal?: AbortSignal): Promise<void> {
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

// Cover image is handled separately as a thumbnail comparison (CompactImageDecision), not a text scalar.
const studioScalarFields = [
  { key: "name", label: "Name" },
  { key: "parent", label: "Parent" },
];

function normalizeDecisionValue(value?: string | number | null) {
  return value == null ? "" : String(value).trim().toLowerCase();
}

function getStudioCurrentValue(studio: Studio, field: string) {
  switch (field) {
    case "name": return studio.name;
    case "parent": return studio.parentName;
    case "image": return studio.imagePath ? "Current logo" : undefined;
    default: return undefined;
  }
}

function getStudioScrapedValue(result: MetadataServerStudioMatch, field: string) {
  switch (field) {
    case "name": return result.name;
    case "parent": return result.parentName;
    case "image": return result.imageUrl ? "MetadataServer logo" : undefined;
    default: return undefined;
  }
}

function buildDefaultStudioFieldStrategies(studio: Studio, result: MetadataServerStudioMatch): Record<string, StudioFieldStrategy> {
  const strategies: Record<string, StudioFieldStrategy> = {};
  for (const field of studioScalarFields) {
    const scraped = getStudioScrapedValue(result, field.key);
    if (scraped === undefined || scraped === null || scraped === "") continue;
    const current = getStudioCurrentValue(studio, field.key);
    strategies[field.key] = normalizeDecisionValue(current) === normalizeDecisionValue(scraped) ? "ignore" : "overwrite";
  }
  // Cover logo: replace-if-empty, keep-if-exists. "overwrite" replaces the logo; "ignore" keeps it.
  if (result.imageUrl) {
    strategies.image = studio.imagePath ? "ignore" : "overwrite";
  }
  return strategies;
}

function getStudioFieldStrategies(studio: Studio, result: MetadataServerStudioMatch, state?: StudioSearchState) {
  return { ...buildDefaultStudioFieldStrategies(studio, result), ...(state?.fieldStrategies ?? {}) };
}

function buildDefaultStudioCollectionModes(result: MetadataServerStudioMatch): Record<string, CollectionMode> {
  return {
    ...DEFAULT_COLLECTION_MODES,
    urls: result.urls.length > 0 ? "merge" : "skip",
    aliases: result.aliases.length > 0 ? "merge" : "skip",
  };
}

function getStudioCollectionModes(result: MetadataServerStudioMatch, state?: StudioSearchState) {
  return { ...buildDefaultStudioCollectionModes(result), ...(state?.collectionModes ?? {}) };
}

function collectionModeToStudioStrategy(mode: CollectionMode): StudioFieldStrategy {
  if (mode === "replace") return "overwrite";
  if (mode === "merge") return "merge";
  return "ignore";
}

function buildStudioFieldStrategies(studio: Studio, result: MetadataServerStudioMatch, state?: StudioSearchState) {
  const scalarStrategies = getStudioFieldStrategies(studio, result, state);
  const collectionModes = getStudioCollectionModes(result, state);
  return {
    ...scalarStrategies,
    urls: collectionModeToStudioStrategy(collectionModes.urls),
    aliases: collectionModeToStudioStrategy(collectionModes.aliases),
  };
}

export function StudioTagger({ studios: studioList, selectedIds, selecting = false, onSelect, mode = "bulk" }: StudioTaggerProps) {
  const { config } = useAppConfig();
  const metadataServers = config?.scraping?.metadataServers ?? [];

  const [taggerConfig, setTaggerConfig] = useState<TaggerConfig>({
    selectedEndpoint: metadataServers[0]?.endpoint ?? "",
    showTagged: true,
    blacklist: [...DEFAULT_TAGGER_BLACKLIST],
  });

  const [searchStates, setSearchStates] = useState<Record<number, StudioSearchState>>({});
  const [queryOverrides, setQueryOverrides] = useState<Record<number, string>>({});
  const [showSettings, setShowSettings] = useState(false);

  const updateSearchState = useCallback(
    (studioId: number, update: Partial<StudioSearchState>) => {
      setSearchStates((prev) => ({ ...prev, [studioId]: { ...prev[studioId], ...update } }));
    },
    []
  );

  const searchStudio = useCallback(
    async (studio: Studio) => {
      const query = queryOverrides[studio.id] ?? cleanTaggerQueryString(studio.name, taggerConfig.blacklist);
      updateSearchState(studio.id, { loading: true, error: undefined, results: undefined, saved: false });
      try {
        const endpoint = taggerConfig.selectedEndpoint || undefined;
        const results = await studios.searchMetadataServer(studio.id, query, endpoint);
        updateSearchState(studio.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
        });
      } catch (err) {
        updateSearchState(studio.id, {
          loading: false,
          error: err instanceof Error ? err.message : "Search failed",
        });
      }
    },
    [queryOverrides, taggerConfig.blacklist, taggerConfig.selectedEndpoint, updateSearchState]
  );

  const [batchSearching, setBatchSearching] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const searchAll = useCallback(async () => {
    setBatchSearching(true);
    const controller = new AbortController();
    abortRef.current = controller;
    const toSearch = studioList.filter((s) => !searchStates[s.id]?.saved);
    await runWithConcurrency(toSearch, (s) => searchStudio(s), CONCURRENCY_LIMIT, controller.signal);
    setBatchSearching(false);
    abortRef.current = null;
  }, [studioList, searchStates, searchStudio]);

  const cancelBatchSearch = useCallback(() => {
    abortRef.current?.abort();
    setBatchSearching(false);
  }, []);

  if (metadataServers.length === 0) {
    return (
      <div className="px-4 py-12 text-center">
        <AlertCircle className="w-12 h-12 mx-auto mb-3 text-muted opacity-50" />
        <p className="text-secondary text-lg">No Metadata Server Sources Configured</p>
        <p className="text-muted text-sm mt-1">
          Add a metadata server endpoint in Settings &gt; Metadata Providers to use the tagger.
        </p>
      </div>
    );
  }

  // Detail mode was opened for this specific studio, so always show it (the bulk "hide tagged"
  // convenience filter would otherwise leave the dialog empty for an already-tagged studio).
  const visibleStudios = mode === "detail" || taggerConfig.showTagged
    ? studioList
    : studioList.filter((s) => !s.remoteIds || s.remoteIds.length === 0);

  return (
    <div className="space-y-0">
      <TaggerToolbar
        sources={metadataServers.map((server) => ({ value: server.endpoint, label: server.name || server.endpoint }))}
        selectedSource={taggerConfig.selectedEndpoint}
        onSourceChange={(value) => {
          setTaggerConfig((current) => ({ ...current, selectedEndpoint: value }));
          setQueryOverrides({});
        }}
        showToggle={mode === "bulk" ? {
          value: taggerConfig.showTagged,
          onChange: (value) => setTaggerConfig((current) => ({ ...current, showTagged: value })),
          enabledLabel: "Hide Already Tagged",
          disabledLabel: "Show All Studios",
        } : undefined}
        batchSearching={batchSearching}
        onCancelBatch={cancelBatchSearch}
        onRunAll={searchAll}
        showRunAll={mode === "bulk"}
        countLabel={`${visibleStudios.length} studio${visibleStudios.length !== 1 ? "s" : ""}`}
        settingsOpen={showSettings}
        onToggleSettings={() => setShowSettings((current) => !current)}
      />
      {showSettings && (
        <TaggerSettingsPanel
          blacklist={taggerConfig.blacklist}
          onBlacklistChange={(items) => setTaggerConfig((current) => ({ ...current, blacklist: items }))}
        />
      )}

      {/* Studio list */}
      <div className="divide-y divide-border">
        {visibleStudios.length === 0 && !taggerConfig.showTagged && (
          <div className="px-4 py-10 text-center text-sm text-secondary">
            All visible studios already have Remote IDs. Use "Show All Studios" to tag or re-check matched studios.
          </div>
        )}
        {visibleStudios.map((studio) => (
          <StudioTaggerRow
            key={studio.id}
            studio={studio}
            state={searchStates[studio.id]}
            query={queryOverrides[studio.id] ?? cleanTaggerQueryString(studio.name, taggerConfig.blacklist)}
            onQueryChange={(q) => setQueryOverrides((prev) => ({ ...prev, [studio.id]: q }))}
            onSearch={() => searchStudio(studio)}
            onUpdateState={(update) => updateSearchState(studio.id, update)}
            endpoint={taggerConfig.selectedEndpoint}
            metadataServers={metadataServers}
            detailMode={mode === "detail"}
            selected={selectedIds?.has(studio.id) ?? false}
            selecting={selecting}
            onSelect={onSelect}
          />
        ))}
      </div>
    </div>
  );
}

function StudioTaggerRow({
  studio,
  state,
  query,
  onQueryChange,
  onSearch,
  onUpdateState,
  endpoint,
  metadataServers,
  detailMode = false,
  selected,
  selecting,
  onSelect,
}: {
  studio: Studio;
  state?: StudioSearchState;
  query: string;
  onQueryChange: (q: string) => void;
  onSearch: () => void;
  onUpdateState: (update: Partial<StudioSearchState>) => void;
  endpoint: string;
  metadataServers: MetadataServer[];
  detailMode?: boolean;
  selected: boolean;
  selecting: boolean;
  onSelect?: (studioId: number) => void;
}) {
  const imageUrl = studio.imagePath;
  const [refreshBusyEndpoint, setRefreshBusyEndpoint] = useState<string | null>(null);

  const refreshFromRemote = useCallback(async (refreshEndpoint: string, remoteId: string) => {
    setRefreshBusyEndpoint(refreshEndpoint);
    onUpdateState({ loading: true, error: undefined, results: undefined, saved: false });
    try {
      const results = await studios.findMetadataServerByIds({ endpoint: refreshEndpoint, ids: [remoteId] });
      onUpdateState({
        loading: false,
        results,
        selectedIndex: results.length > 0 ? 0 : undefined,
        error: results.length === 0 ? "No metadata-server entry found for this remote id." : undefined,
      });
    } catch (err) {
      onUpdateState({ loading: false, error: err instanceof Error ? err.message : "Refresh failed" });
    } finally {
      setRefreshBusyEndpoint(null);
    }
  }, [onUpdateState]);

  const importMut = useMutation({
    mutationFn: () => {
      const selectedResult = state?.results?.[state.selectedIndex ?? 0];
      if (!selectedResult) throw new Error("No result selected");
      const importReq: MetadataServerStudioImportRequest = {
        endpoint,
        studioId: selectedResult.id,
        fieldStrategies: buildStudioFieldStrategies(studio, selectedResult, state),
      };
      return studios.importFromMetadataServer(studio.id, importReq);
    },
    onSuccess: () => {
      onUpdateState({ saved: true });
    },
  });

  const submitDraftMut = useMutation<{ draftId: string | null }, Error>({
    mutationFn: () => {
      if (!endpoint) throw new Error("Select a metadata-server source first.");
      return studios.submitMetadataServerDraft(studio.id, endpoint);
    },
  });

  return (
    <div className={`px-4 py-3 ${selected ? "bg-accent/5" : ""}`}>
      <div className="flex gap-4">
        {onSelect && (
          <button
            type="button"
            onClick={() => onSelect(studio.id)}
            className={`mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded border text-[10px] ${selected ? "border-accent bg-accent text-white" : selecting ? "border-accent/60 text-accent" : "border-border text-transparent hover:border-accent hover:text-accent"}`}
            aria-label={selected ? "Deselect studio" : "Select studio"}
            title={selected ? "Deselect" : "Select"}
          >
            <Check className="h-3 w-3" />
          </button>
        )}
        {/* Studio image */}
        <div className="flex-shrink-0 w-24">
          <div className="relative aspect-video bg-card rounded overflow-hidden">
            {imageUrl ? (
              <img src={imageUrl} alt="" className="w-full h-full object-contain" loading="lazy" />
            ) : (
              <div className="w-full h-full flex items-center justify-center text-muted text-xs">No Image</div>
            )}
          </div>
          <p className="text-xs text-foreground mt-1 truncate font-medium">{studio.name}</p>
          {studio.remoteIds && studio.remoteIds.length > 0 && (
            <div className="flex flex-wrap gap-1 mt-1">
              {studio.remoteIds.map((sid) => (
                <span key={`${sid.endpoint}-${sid.remoteId}`} className="text-[9px] px-1.5 py-0.5 rounded bg-green-600/20 text-green-300" title={sid.endpoint}>
                  <Fingerprint className="w-2.5 h-2.5 inline mr-0.5" />
                  {sid.remoteId.substring(0, 8)}…
                </span>
              ))}
            </div>
          )}
        </div>

        {/* Search + Results */}
        <div className="flex-1 min-w-0">
          {detailMode && (
            <RemoteRefreshButtons
              remoteIds={studio.remoteIds}
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
              placeholder="Search query..."
              className="flex-1 bg-input border border-border rounded px-3 py-1.5 text-xs text-foreground focus:outline-none focus:border-accent"
            />
            <button
              onClick={onSearch}
              disabled={state?.loading}
              className="flex items-center gap-1.5 px-3 py-1.5 rounded text-xs font-medium bg-accent text-white hover:bg-accent-hover disabled:opacity-60"
            >
              {state?.loading ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Search className="w-3.5 h-3.5" />}
              Search
            </button>
            <button
              onClick={() => submitDraftMut.mutate()}
              disabled={submitDraftMut.isPending}
              className="flex items-center gap-1 px-2 py-1.5 rounded text-xs bg-surface border border-border text-muted hover:text-foreground disabled:opacity-60"
              title="Submit this studio as a draft entry to the metadata server"
            >
              {submitDraftMut.isPending ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <CloudUpload className="w-3.5 h-3.5" />}
            </button>
          </div>

          {submitDraftMut.isError && (
            <p className="text-xs text-red-400 mb-2"><AlertCircle className="w-3 h-3 inline mr-1" />{submitDraftMut.error.message}</p>
          )}
          {submitDraftMut.isSuccess && (
            <p className="text-xs text-green-400 mb-2"><Check className="w-3 h-3 inline mr-1" />Studio draft submitted{submitDraftMut.data.draftId ? ` (${submitDraftMut.data.draftId})` : ""}.</p>
          )}

          {state?.error && (
            <p className="text-xs text-red-400 mb-2">
              <AlertCircle className="w-3 h-3 inline mr-1" />{state.error}
            </p>
          )}

          {state?.results && state.results.length === 0 && (
            <p className="text-xs text-muted">No matches found.</p>
          )}

          {state?.results && state.results.length > 0 && (
            <div className="space-y-1">
              {state.results.map((result, i) => (
                <StudioResultRow
                  key={`${result.endpoint}-${result.id}`}
                  studio={studio}
                  result={result}
                  isSelected={i === (state.selectedIndex ?? 0)}
                  fieldStrategies={getStudioFieldStrategies(studio, result, state)}
                  collectionModes={getStudioCollectionModes(result, state)}
                  onFieldStrategyChange={(field, strategy) => onUpdateState({ fieldStrategies: { ...getStudioFieldStrategies(studio, result, state), [field]: strategy } })}
                  onCollectionModeChange={(field, mode) => onUpdateState({ collectionModes: { ...getStudioCollectionModes(result, state), [field]: mode } })}
                  onClick={() => onUpdateState(i === (state.selectedIndex ?? 0) ? { selectedIndex: i } : { selectedIndex: i, fieldStrategies: undefined, collectionModes: undefined })}
                  onSave={i === (state.selectedIndex ?? 0) ? () => importMut.mutate() : undefined}
                  saving={i === (state.selectedIndex ?? 0) ? importMut.isPending : false}
                  saved={state.saved}
                />
              ))}
            </div>
          )}

          {state?.saved && (
            <div className="flex items-center gap-1 mt-2 text-xs text-green-400">
              <Check className="w-3.5 h-3.5" />Saved successfully
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function StudioResultRow({
  studio,
  result,
  isSelected,
  fieldStrategies,
  collectionModes,
  onFieldStrategyChange,
  onCollectionModeChange,
  onClick,
  onSave,
  saving,
  saved,
}: {
  studio: Studio;
  result: MetadataServerStudioMatch;
  isSelected: boolean;
  fieldStrategies: Record<string, StudioFieldStrategy>;
  collectionModes: Record<string, CollectionMode>;
  onFieldStrategyChange: (field: string, strategy: StudioFieldStrategy) => void;
  onCollectionModeChange: (field: string, mode: CollectionMode) => void;
  onClick: () => void;
  onSave?: () => void;
  saving?: boolean;
  saved?: boolean;
}) {
  const scalarRows = studioScalarFields
    .map((field) => ({
      ...field,
      current: getStudioCurrentValue(studio, field.key),
      scraped: getStudioScrapedValue(result, field.key),
    }))
    .filter((field) => field.scraped !== undefined && field.scraped !== null && field.scraped !== "");

  return (
    <div
      onClick={onClick}
      className={`rounded border cursor-pointer transition-colors ${
        isSelected ? "border-accent bg-card" : "border-border bg-surface hover:border-accent/50"
      }`}
    >
      <div className="flex items-center gap-3 p-2">
        {result.imageUrl && (
          <img src={result.imageUrl} alt="" className="h-8 w-16 object-contain rounded flex-shrink-0" loading="lazy" />
        )}
        <div className="flex-1 min-w-0">
          <p className="text-xs font-medium text-foreground truncate">{result.name}</p>
          <div className="flex items-center gap-2 text-[10px] text-muted">
            {result.parentName && <span>Parent: {result.parentName}</span>}
            {result.aliases && result.aliases.length > 0 && <span>{result.aliases.length} alias(es)</span>}
          </div>
        </div>
      </div>

      {isSelected && !saved && (
        <div className="border-t border-border px-3 py-3 space-y-3">
          {scalarRows.map((row) => (
            <CompactScalarDecision
              key={row.key}
              label={row.label}
              current={row.current}
              scraped={row.scraped}
              replacing={fieldStrategies[row.key] === "overwrite"}
              onChange={(shouldReplace) => onFieldStrategyChange(row.key, shouldReplace ? "overwrite" : "ignore")}
            />
          ))}

          {result.imageUrl && (
            <CompactImageDecision
              label="Logo"
              currentImageUrl={studio.imagePath}
              scrapedImageUrl={result.imageUrl}
              replacing={fieldStrategies.image === "overwrite"}
              onChange={(shouldReplace) => onFieldStrategyChange("image", shouldReplace ? "overwrite" : "ignore")}
            />
          )}

          {result.urls.length > 0 && (
            <CompactCollectionDecision
              label="URLs"
              current={studio.urls}
              mode={collectionModes.urls}
              onModeChange={(mode) => onCollectionModeChange("urls", mode)}
              scraped={<CompactListValue values={result.urls} breakAll />}
            />
          )}

          {result.aliases.length > 0 && (
            <CompactCollectionDecision
              label="Aliases"
              current={studio.aliases}
              mode={collectionModes.aliases}
              onModeChange={(mode) => onCollectionModeChange("aliases", mode)}
              scraped={<CompactListValue values={result.aliases} />}
            />
          )}

          {onSave && !saved && (
            <div className="flex justify-end">
              <button
                onClick={(e) => { e.stopPropagation(); onSave(); }}
                disabled={saving}
                className="flex items-center gap-1.5 px-4 py-1.5 rounded text-xs font-medium bg-green-600 text-white hover:bg-green-500 disabled:opacity-60"
              >
                {saving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Check className="w-3.5 h-3.5" />}
                Save
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

