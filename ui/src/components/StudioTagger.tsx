import { useCallback, useState, useRef } from "react";
import { useMutation } from "@tanstack/react-query";
import { studios } from "../api/client";
import type {
  Studio,
  MetadataServer,
  MetadataServerStudioMatch,
  MetadataServerStudioImportRequest,
} from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";
import { DEFAULT_COLLECTION_MODES, type CollectionMode } from "./videoScrapeUtils";
import { MetadataDiff, scalarStatus, summarizeDiff, type DiffSelection } from "./MetadataDiff";
import { metadataServerLabel } from "./MetadataServerLinks";
import { MetadataDiffSummary } from "./MetadataDiffSummary";
import { ReviewCoverPanel } from "./ReviewCoverPanel";
import {
  applyStudioSelectionChange,
  buildStudioReview,
  type StudioFieldStrategy,
  type StudioReviewInput,
} from "./StudioTaggerReview";
import {
  DEFAULT_TAGGER_DENYLIST,
  RemoteRefreshButtons,
  TaggerSettingsPanel,
  TaggerToolbar,
  cleanTaggerQueryString,
} from "./TaggerShared";
import { Search, Loader2, Check, AlertCircle, Fingerprint, CloudUpload } from "lucide-react";
import { toggleOptionsFromEvent, withOrderedToggle, type MultiSelectToggleOptions } from "../hooks/useMultiSelect";

interface StudioTaggerProps {
  studios: Studio[];
  selectedIds?: Set<number>;
  selecting?: boolean;
  onSelect?: (studioId: number, options?: MultiSelectToggleOptions) => void;
  mode?: "bulk" | "detail";
}

interface TaggerConfig {
  selectedEndpoint: string;
  showTagged: boolean;
  denylist: string[];
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

const CONCURRENCY_LIMIT = 5;
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

// The logo is handled separately as a cover panel, not a text scalar.
const studioScalarFields = [
  { key: "name", label: "Name" },
  { key: "parent", label: "Parent" },
];

function normalizeDecisionValue(value?: string | number | null) {
  return value == null ? "" : String(value).trim().toLowerCase();
}

function getStudioCurrentValue(studio: Studio, field: string) {
  switch (field) {
    case "name":
      return studio.name;
    case "parent":
      return studio.parentName;
    default:
      return undefined;
  }
}

function getStudioScrapedValue(result: MetadataServerStudioMatch, field: string) {
  switch (field) {
    case "name":
      return result.name;
    case "parent":
      return result.parentName;
    default:
      return undefined;
  }
}

function buildDefaultStudioFieldStrategies(
  studio: Studio,
  result: MetadataServerStudioMatch,
): Record<string, StudioFieldStrategy> {
  const strategies: Record<string, StudioFieldStrategy> = {};
  for (const field of studioScalarFields) {
    const scraped = getStudioScrapedValue(result, field.key);
    if (scraped === undefined || scraped === null || scraped === "") continue;
    const current = getStudioCurrentValue(studio, field.key);
    strategies[field.key] =
      normalizeDecisionValue(current) === normalizeDecisionValue(scraped) ? "ignore" : "overwrite";
  }
  // Cover logo: replace-if-empty, keep-if-exists. "overwrite" replaces the logo; "ignore" keeps it.
  if (result.imageUrl) {
    strategies.image = studio.imagePath ? "ignore" : "overwrite";
  }
  return strategies;
}

function getStudioFieldStrategies(studio: Studio, result: MetadataServerStudioMatch, state?: StudioSearchState) {
  return { ...buildDefaultStudioFieldStrategies(studio, result), ...state?.fieldStrategies };
}

function buildDefaultStudioCollectionModes(result: MetadataServerStudioMatch): Record<string, CollectionMode> {
  return {
    ...DEFAULT_COLLECTION_MODES,
    urls: result.urls.length > 0 ? "merge" : "skip",
    aliases: result.aliases.length > 0 ? "merge" : "skip",
  };
}

function getStudioCollectionModes(result: MetadataServerStudioMatch, state?: StudioSearchState) {
  return { ...buildDefaultStudioCollectionModes(result), ...state?.collectionModes };
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

export function StudioTagger({
  studios: studioList,
  selectedIds,
  selecting = false,
  onSelect,
  mode = "bulk",
}: StudioTaggerProps) {
  const { config } = useAppConfig();
  const metadataServers = config?.scraping?.metadataServers ?? [];

  const [taggerConfig, setTaggerConfig] = useState<TaggerConfig>({
    selectedEndpoint: metadataServers[0]?.endpoint ?? "",
    showTagged: true,
    denylist: [...DEFAULT_TAGGER_DENYLIST],
  });

  const [searchStates, setSearchStates] = useState<Record<number, StudioSearchState>>({});
  const [queryOverrides, setQueryOverrides] = useState<Record<number, string>>({});
  const [showSettings, setShowSettings] = useState(false);

  const updateSearchState = useCallback((studioId: number, update: Partial<StudioSearchState>) => {
    setSearchStates((prev) => ({ ...prev, [studioId]: { ...prev[studioId], ...update } }));
  }, []);

  const searchStudio = useCallback(
    async (studio: Studio) => {
      const query = queryOverrides[studio.id] ?? cleanTaggerQueryString(studio.name, taggerConfig.denylist);
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
    [queryOverrides, taggerConfig.denylist, taggerConfig.selectedEndpoint, updateSearchState],
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
  const visibleStudios =
    mode === "detail" || taggerConfig.showTagged
      ? studioList
      : studioList.filter((s) => !s.remoteIds || s.remoteIds.length === 0);
  const visibleStudioIds = visibleStudios.map((studio) => studio.id);

  return (
    <div className="space-y-0">
      <TaggerToolbar
        sources={metadataServers.map((server) => ({ value: server.endpoint, label: server.name || server.endpoint }))}
        selectedSource={taggerConfig.selectedEndpoint}
        onSourceChange={(value) => {
          setTaggerConfig((current) => ({ ...current, selectedEndpoint: value }));
          setQueryOverrides({});
        }}
        showToggle={
          mode === "bulk"
            ? {
                value: taggerConfig.showTagged,
                onChange: (value) => setTaggerConfig((current) => ({ ...current, showTagged: value })),
                enabledLabel: "Hide Already Tagged",
                disabledLabel: "Show All Studios",
              }
            : undefined
        }
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
          denylist={taggerConfig.denylist}
          onDenylistChange={(items) => setTaggerConfig((current) => ({ ...current, denylist: items }))}
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
            query={queryOverrides[studio.id] ?? cleanTaggerQueryString(studio.name, taggerConfig.denylist)}
            onQueryChange={(q) => setQueryOverrides((prev) => ({ ...prev, [studio.id]: q }))}
            onSearch={() => searchStudio(studio)}
            onUpdateState={(update) => updateSearchState(studio.id, update)}
            endpoint={taggerConfig.selectedEndpoint}
            metadataServers={metadataServers}
            detailMode={mode === "detail"}
            selected={selectedIds?.has(studio.id) ?? false}
            selecting={selecting}
            onSelect={onSelect ? withOrderedToggle(onSelect, visibleStudioIds) : undefined}
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
  onSelect?: (studioId: number, options?: MultiSelectToggleOptions) => void;
}) {
  const imageUrl = studio.imagePath;
  const [refreshBusyEndpoint, setRefreshBusyEndpoint] = useState<string | null>(null);

  const refreshFromRemote = useCallback(
    async (refreshEndpoint: string, remoteId: string) => {
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
    },
    [onUpdateState],
  );

  const importMut = useMutation({
    mutationFn: () => {
      const selectedResult = state?.results?.[state.selectedIndex ?? 0];
      if (!selectedResult) throw new Error("No result selected");
      const importReq: MetadataServerStudioImportRequest = {
        endpoint: selectedResult.endpoint,
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
    meta: { suppressGlobalError: true },
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
            onClick={(event) => onSelect(studio.id, toggleOptionsFromEvent(event))}
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
            <button
              onClick={() => submitDraftMut.mutate()}
              disabled={submitDraftMut.isPending}
              className="flex shrink-0 items-center gap-1 px-2 py-1.5 rounded text-xs bg-surface border border-border text-muted hover:text-foreground disabled:opacity-60"
              title="Submit this studio as a draft entry to the metadata server"
            >
              {submitDraftMut.isPending ? (
                <Loader2 className="w-3.5 h-3.5 animate-spin" />
              ) : (
                <CloudUpload className="w-3.5 h-3.5" />
              )}
            </button>
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
              Studio draft submitted{submitDraftMut.data.draftId ? ` (${submitDraftMut.data.draftId})` : ""}.
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
                <StudioResultRow
                  key={`${result.endpoint}-${result.id}`}
                  studio={studio}
                  result={result}
                  metadataServers={metadataServers}
                  isSelected={i === (state.selectedIndex ?? 0)}
                  showSelector={state.results!.length > 1}
                  fieldStrategies={getStudioFieldStrategies(studio, result, state)}
                  collectionModes={getStudioCollectionModes(result, state)}
                  onFieldStrategyChange={(field, strategy) =>
                    onUpdateState({
                      fieldStrategies: { ...getStudioFieldStrategies(studio, result, state), [field]: strategy },
                    })
                  }
                  onCollectionModeChange={(field, mode) =>
                    onUpdateState({ collectionModes: { ...getStudioCollectionModes(result, state), [field]: mode } })
                  }
                  onClick={() =>
                    onUpdateState(
                      i === (state.selectedIndex ?? 0)
                        ? { selectedIndex: i }
                        : { selectedIndex: i, fieldStrategies: undefined, collectionModes: undefined },
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

function StudioResultRow({
  studio,
  result,
  metadataServers,
  isSelected,
  showSelector,
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
  metadataServers: MetadataServer[];
  isSelected: boolean;
  showSelector: boolean;
  fieldStrategies: Record<string, StudioFieldStrategy>;
  collectionModes: Record<string, CollectionMode>;
  onFieldStrategyChange: (field: string, strategy: StudioFieldStrategy) => void;
  onCollectionModeChange: (field: string, mode: CollectionMode) => void;
  onClick: () => void;
  onSave?: () => void;
  saving?: boolean;
  saved?: boolean;
}) {
  // Accept-all is the common case, so the review opens as a list of facts; the full side-by-side
  // rows are one click away.
  const [adjusting, setAdjusting] = useState(false);
  const sourceName = result.serverName || metadataServerLabel(result.endpoint, metadataServers);
  const reviewInput: StudioReviewInput = {
    sourceName,
    scalars: studioScalarFields
      .map((field) => ({
        key: field.key,
        label: field.label,
        current: getStudioCurrentValue(studio, field.key),
        scraped: getStudioScrapedValue(result, field.key),
      }))
      .filter(
        (field): field is typeof field & { scraped: string | number } =>
          field.scraped !== undefined && field.scraped !== null && field.scraped !== "",
      ),
    fieldStrategies,
    collectionModes,
    currentImageUrl: studio.imagePath,
    incomingImageUrl: result.imageUrl || undefined,
    urls: { current: studio.urls ?? [], incoming: result.urls },
    aliases: { current: studio.aliases ?? [], incoming: result.aliases },
  };
  const review = isSelected ? buildStudioReview(reviewInput) : null;
  const summary = review ? summarizeDiff(review.fields, review.source, review.target, review.selection) : null;
  const imageField = review?.fields.find((field) => field.key === "image");
  const handleSelectionChange = (next: DiffSelection) => {
    if (!review) return;
    applyStudioSelectionChange(reviewInput, review.selection, next, {
      onFieldStrategyChange,
      onCollectionModeChange,
    });
  };
  const facts = [
    result.parentName ? `Parent: ${result.parentName}` : null,
    result.aliases.length > 0 ? `${result.aliases.length} alias${result.aliases.length === 1 ? "" : "es"}` : null,
  ]
    .filter(Boolean)
    .join(" · ");

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
          <img src={result.imageUrl} alt="" className="h-8 w-16 shrink-0 rounded object-contain" loading="lazy" />
        )}
        {review && imageField ? (
          <div onClick={(event) => event.stopPropagation()} className="w-full sm:w-auto">
            <ReviewCoverPanel
              status={scalarStatus(imageField, review.source, review.target)}
              chosen={review.selection.image === "source" ? "source" : "target"}
              currentUrl={studio.imagePath}
              candidates={result.imageUrl ? [result.imageUrl] : []}
              incomingLabel={sourceName}
              onChoose={(side) => handleSelectionChange({ ...review.selection, image: side })}
              disabled={saving}
              subject="Logo"
            />
          </div>
        ) : null}
        <div className="min-w-0 flex-1 self-start">
          <p
            className={`text-foreground ${isSelected ? "text-base font-semibold leading-snug" : "truncate text-[13px] font-semibold"}`}
          >
            {result.name}
          </p>
          {/* Expanded, the rows below say what happens to each fact, so the header names the source instead. */}
          {isSelected ? (
            <p className="text-sm text-secondary">{sourceName}</p>
          ) : facts ? (
            <p className="truncate text-[11px] text-muted">{facts}</p>
          ) : null}
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
