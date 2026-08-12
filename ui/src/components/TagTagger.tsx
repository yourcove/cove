import { useCallback, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { tags } from "../api/client";
import type { MetadataServer, MetadataServerTagImportRequest, MetadataServerTagMatch, Tag } from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";
import { DEFAULT_TAGGER_BLACKLIST, RemoteRefreshButtons, TaggerSettingsPanel, TaggerToolbar, cleanTaggerQueryString } from "./TaggerShared";
import { AlertCircle, Check, CloudDownload, CloudUpload, Eye, EyeOff, Loader2, Search, Tag as TagIcon, X } from "lucide-react";
import { toggleOptionsFromEvent, withOrderedToggle, type MultiSelectToggleOptions } from "../hooks/useMultiSelect";

interface TagTaggerProps {
  tags: Tag[];
  selectedIds?: Set<number>;
  selecting?: boolean;
  onSelect?: (tagId: number, options?: MultiSelectToggleOptions) => void;
  mode?: "bulk" | "detail";
}

interface TaggerConfig {
  selectedEndpoint: string;
  showTagged: boolean;
  blacklist: string[];
}

interface TagSearchState {
  loading: boolean;
  results?: MetadataServerTagMatch[];
  error?: string;
  selectedIndex?: number;
  saved?: boolean;
  warning?: string;
}

const CONCURRENCY_LIMIT = 5;

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

export function TagTagger({ tags: tagList, selectedIds, selecting = false, onSelect, mode = "bulk" }: TagTaggerProps) {
  const { config } = useAppConfig();
  const metadataServers = config?.scraping?.metadataServers ?? [];
  const [taggerConfig, setTaggerConfig] = useState<TaggerConfig>({
    selectedEndpoint: metadataServers[0]?.endpoint ?? "",
    showTagged: true,
    blacklist: [...DEFAULT_TAGGER_BLACKLIST],
  });
  const [searchStates, setSearchStates] = useState<Record<number, TagSearchState>>({});
  const [queryOverrides, setQueryOverrides] = useState<Record<number, string>>({});
  const [batchSearching, setBatchSearching] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  const updateSearchState = useCallback((tagId: number, update: Partial<TagSearchState>) => {
    setSearchStates((prev) => ({ ...prev, [tagId]: { ...prev[tagId], ...update } }));
  }, []);

  const searchTag = useCallback(async (tag: Tag) => {
    const query = queryOverrides[tag.id] ?? cleanTaggerQueryString(tag.name, taggerConfig.blacklist);
    updateSearchState(tag.id, { loading: true, error: undefined, warning: undefined, results: undefined, saved: false });
    try {
      const endpoint = taggerConfig.selectedEndpoint || undefined;
      const results = await tags.searchMetadataServer(tag.id, query, endpoint);
      updateSearchState(tag.id, { loading: false, results, selectedIndex: results.length > 0 ? 0 : undefined });
    } catch (err) {
      updateSearchState(tag.id, { loading: false, error: err instanceof Error ? err.message : "Search failed" });
    }
  }, [queryOverrides, taggerConfig.blacklist, taggerConfig.selectedEndpoint, updateSearchState]);

  const searchAll = useCallback(async () => {
    setBatchSearching(true);
    const controller = new AbortController();
    abortRef.current = controller;
    const toSearch = tagList.filter((tag) => !searchStates[tag.id]?.saved);
    await runWithConcurrency(toSearch, (tag) => searchTag(tag), CONCURRENCY_LIMIT, controller.signal);
    setBatchSearching(false);
    abortRef.current = null;
  }, [tagList, searchStates, searchTag]);

  const cancelBatchSearch = useCallback(() => {
    abortRef.current?.abort();
    setBatchSearching(false);
  }, []);

  if (metadataServers.length === 0) {
    return (
      <div className="px-4 py-12 text-center">
        <AlertCircle className="w-12 h-12 mx-auto mb-3 text-muted opacity-50" />
        <p className="text-secondary text-lg">No Metadata Server Sources Configured</p>
        <p className="text-muted text-sm mt-1">Add a metadata server endpoint in Settings &gt; Metadata Providers to use the tagger.</p>
      </div>
    );
  }

  // Detail mode was opened for this specific tag, so always show it (the bulk "hide tagged"
  // convenience filter would otherwise leave the dialog empty).
  const visibleTags = mode === "detail" || taggerConfig.showTagged
    ? tagList
    : tagList.filter((tag) => !searchStates[tag.id]?.saved);
  const visibleTagIds = visibleTags.map((tag) => tag.id);

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
          enabledLabel: "Hide Saved",
          disabledLabel: "Show Saved",
        } : undefined}
        batchSearching={batchSearching}
        onCancelBatch={cancelBatchSearch}
        onRunAll={searchAll}
        runAllLabel="Search All"
        showRunAll={mode === "bulk"}
        countLabel={`${visibleTags.length} tag${visibleTags.length !== 1 ? "s" : ""}`}
        settingsOpen={showSettings}
        onToggleSettings={() => setShowSettings((current) => !current)}
      />
      {showSettings && (
        <TaggerSettingsPanel
          blacklist={taggerConfig.blacklist}
          onBlacklistChange={(items) => setTaggerConfig((current) => ({ ...current, blacklist: items }))}
        />
      )}

      <div className="divide-y divide-border">
        {visibleTags.map((tag) => (
          <TagTaggerRow
            key={tag.id}
            tag={tag}
            state={searchStates[tag.id]}
            query={queryOverrides[tag.id] ?? cleanTaggerQueryString(tag.name, taggerConfig.blacklist)}
            onQueryChange={(query) => setQueryOverrides((prev) => ({ ...prev, [tag.id]: query }))}
            onSearch={() => searchTag(tag)}
            onUpdateState={(update) => updateSearchState(tag.id, update)}
            endpoint={taggerConfig.selectedEndpoint}
            metadataServers={metadataServers}
            detailMode={mode === "detail"}
            selected={selectedIds?.has(tag.id) ?? false}
            selecting={selecting}
            onSelect={onSelect ? withOrderedToggle(onSelect, visibleTagIds) : undefined}
          />
        ))}
      </div>
    </div>
  );
}

function TagTaggerRow({ tag, state, query, onQueryChange, onSearch, onUpdateState, endpoint, metadataServers, detailMode = false, selected, selecting, onSelect }: {
  tag: Tag;
  state?: TagSearchState;
  query: string;
  onQueryChange: (query: string) => void;
  onSearch: () => void;
  onUpdateState: (update: Partial<TagSearchState>) => void;
  endpoint: string;
  metadataServers: MetadataServer[];
  detailMode?: boolean;
  selected: boolean;
  selecting: boolean;
  onSelect?: (tagId: number, options?: MultiSelectToggleOptions) => void;
}) {
  const [refreshBusyEndpoint, setRefreshBusyEndpoint] = useState<string | null>(null);

  const refreshFromRemote = useCallback(async (refreshEndpoint: string, remoteId: string) => {
    setRefreshBusyEndpoint(refreshEndpoint);
    onUpdateState({ loading: true, error: undefined, warning: undefined, results: undefined, saved: false });
    try {
      const results = await tags.findMetadataServerByIds({ endpoint: refreshEndpoint, ids: [remoteId] });
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
      const request: MetadataServerTagImportRequest = { endpoint: selectedResult.endpoint, tagId: selectedResult.id };
      return tags.importFromMetadataServer(tag.id, request);
    },
    onSuccess: (result) => onUpdateState({
      saved: true,
      warning: result.importWarnings && result.importWarnings.length > 0 ? result.importWarnings.join(" ") : undefined,
    }),
  });

  const submitDraftMut = useMutation<{ draftId: string | null }, Error>({
    meta: { suppressGlobalError: true },
    mutationFn: () => {
      if (!endpoint) throw new Error("Select a metadata-server source first.");
      return tags.submitMetadataServerDraft(tag.id, endpoint);
    },
  });

  return (
    <div className={`px-4 py-3 ${selected ? "bg-accent/5" : ""}`}>
      <div className="flex gap-4">
        {onSelect && (
          <button
            type="button"
            onClick={(event) => onSelect(tag.id, toggleOptionsFromEvent(event))}
            className={`mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded border text-[10px] ${selected ? "border-accent bg-accent text-white" : selecting ? "border-accent/60 text-accent" : "border-border text-transparent hover:border-accent hover:text-accent"}`}
            aria-label={selected ? "Deselect tag" : "Select tag"}
            title={selected ? "Deselect" : "Select"}
          >
            <Check className="h-3 w-3" />
          </button>
        )}
        <div className="flex-shrink-0 w-24">
          <div className="relative aspect-video bg-card rounded overflow-hidden flex items-center justify-center">
            {tag.imagePath ? <img src={tag.imagePath} alt="" className="w-full h-full object-cover" loading="lazy" /> : <TagIcon className="w-8 h-8 text-muted" />}
          </div>
          <p className="text-xs text-foreground mt-1 truncate font-medium">{tag.name}</p>
          {tag.tagGroupName && <p className="text-[10px] text-muted truncate">{tag.tagGroupName}</p>}
        </div>
        <div className="flex-1 min-w-0">
          {detailMode && (
            <RemoteRefreshButtons
              remoteIds={(tag as { remoteIds?: { endpoint: string; remoteId: string }[] }).remoteIds}
              servers={metadataServers}
              busyEndpoint={refreshBusyEndpoint}
              onRefresh={refreshFromRemote}
            />
          )}
          <div className="flex gap-2 mb-2">
            <input
              type="text"
              value={query}
              onChange={(event) => onQueryChange(event.target.value)}
              onKeyDown={(event) => event.key === "Enter" && onSearch()}
              placeholder="Search query..."
              className="flex-1 bg-input border border-border rounded px-3 py-1.5 text-xs text-foreground focus:outline-none focus:border-accent"
            />
            <button onClick={onSearch} disabled={state?.loading} className="flex items-center gap-1.5 px-3 py-1.5 rounded text-xs font-medium bg-accent text-white hover:bg-accent-hover disabled:opacity-60">
              {state?.loading ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Search className="w-3.5 h-3.5" />}
              Search
            </button>
            <button
              onClick={() => submitDraftMut.mutate()}
              disabled={submitDraftMut.isPending}
              className="flex items-center gap-1 px-2 py-1.5 rounded text-xs bg-surface border border-border text-muted hover:text-foreground disabled:opacity-60"
              title="Submit this tag as a draft entry to the metadata server"
            >
              {submitDraftMut.isPending ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <CloudUpload className="w-3.5 h-3.5" />}
            </button>
          </div>

          {submitDraftMut.isError && <p className="text-xs text-red-400 mb-2"><AlertCircle className="w-3 h-3 inline mr-1" />{submitDraftMut.error.message}</p>}
          {submitDraftMut.isSuccess && <p className="text-xs text-green-400 mb-2"><Check className="w-3 h-3 inline mr-1" />Tag draft submitted{submitDraftMut.data.draftId ? ` (${submitDraftMut.data.draftId})` : ""}.</p>}

          {state?.error && <p className="text-xs text-red-400 mb-2"><AlertCircle className="w-3 h-3 inline mr-1" />{state.error}</p>}
          {state?.warning && <p className="text-xs text-amber-300 mb-2"><AlertCircle className="w-3 h-3 inline mr-1" />Saved with warnings: {state.warning}</p>}
          {state?.results && state.results.length === 0 && <p className="text-xs text-muted">No matches found.</p>}
          {state?.results && state.results.length > 0 && (
            <div className="space-y-1">
              {state.results.map((result, index) => (
                <TagResultRow
                  key={`${result.endpoint}-${result.id}`}
                  result={result}
                  isSelected={index === (state.selectedIndex ?? 0)}
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

function TagResultRow({ result, isSelected, onClick, onSave, saving, saved }: {
  result: MetadataServerTagMatch;
  isSelected: boolean;
  onClick: () => void;
  onSave?: () => void;
  saving?: boolean;
  saved?: boolean;
}) {
  return (
    <div onClick={onClick} className={`rounded border cursor-pointer transition-colors ${isSelected ? "border-accent bg-card" : "border-border bg-surface hover:border-accent/50"}`}>
      <div className="flex items-center gap-3 p-2">
        <TagIcon className="h-4 w-4 text-muted flex-shrink-0" />
        <div className="flex-1 min-w-0">
          <p className="text-xs font-medium text-foreground truncate">{result.name}</p>
          <div className="flex items-center gap-2 text-[10px] text-muted">
            {result.description && <span className="truncate">{result.description}</span>}
            {result.aliases.length > 0 && <span>{result.aliases.length} alias(es)</span>}
          </div>
        </div>
      </div>
      {isSelected && !saved && (
        <div className="border-t border-border p-3">
          {result.description && <p className="text-xs text-secondary mb-2 line-clamp-3">{result.description}</p>}
          {result.aliases.length > 0 && <FieldRow label="Aliases" value={result.aliases.join(", ")} />}
          {onSave && !saved && (
            <div className="flex justify-end mt-3">
              <button onClick={(event) => { event.stopPropagation(); onSave(); }} disabled={saving} className="flex items-center gap-1.5 px-4 py-1.5 rounded text-xs font-medium bg-green-600 text-white hover:bg-green-500 disabled:opacity-60">
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

function FieldRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex gap-2 text-xs">
      <span className="text-muted w-24 flex-shrink-0 text-right">{label}:</span>
      <span className="text-foreground truncate">{value}</span>
    </div>
  );
}
