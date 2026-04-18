import { useCallback, useState, useRef } from "react";
import { useMutation } from "@tanstack/react-query";
import { performers } from "../api/client";
import type { Performer, MetadataServerPerformerMatch, MetadataServerPerformerImportRequest } from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";
import {
  Search, Loader2, Check, X, ChevronDown, ChevronUp, AlertCircle,
  CloudDownload, Fingerprint, Settings2, EyeOff, Eye,
} from "lucide-react";

interface PerformerTaggerProps {
  performers: Performer[];
}

interface TaggerConfig {
  selectedEndpoint: string;
  showTagged: boolean;
}

interface PerformerSearchState {
  loading: boolean;
  results?: MetadataServerPerformerMatch[];
  error?: string;
  selectedIndex?: number;
  saved?: boolean;
}

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

export function PerformerTagger({ performers: performerList }: PerformerTaggerProps) {
  const { config } = useAppConfig();
  const metadataServers = config?.scraping?.metadataServers ?? [];

  const [taggerConfig, setTaggerConfig] = useState<TaggerConfig>({
    selectedEndpoint: metadataServers[0]?.endpoint ?? "",
    showTagged: false,
  });

  const [searchStates, setSearchStates] = useState<Record<number, PerformerSearchState>>({});
  const [queryOverrides, setQueryOverrides] = useState<Record<number, string>>({});

  const updateSearchState = useCallback(
    (performerId: number, update: Partial<PerformerSearchState>) => {
      setSearchStates((prev) => ({ ...prev, [performerId]: { ...prev[performerId], ...update } }));
    },
    []
  );

  const searchPerformer = useCallback(
    async (performer: Performer) => {
      const query = queryOverrides[performer.id] ?? performer.name;
      updateSearchState(performer.id, { loading: true, error: undefined, results: undefined, saved: false });
      try {
        const endpoint = taggerConfig.selectedEndpoint || undefined;
        const results = await performers.searchMetadataServer(performer.id, query, endpoint);
        updateSearchState(performer.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
        });
      } catch (err) {
        updateSearchState(performer.id, {
          loading: false,
          error: err instanceof Error ? err.message : "Search failed",
        });
      }
    },
    [queryOverrides, taggerConfig.selectedEndpoint, updateSearchState]
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

  const visiblePerformers = taggerConfig.showTagged
    ? performerList
    : performerList.filter((p) => !p.remoteIds || p.remoteIds.length === 0);

  return (
    <div className="space-y-0">
      {/* Toolbar */}
      <div className="flex flex-wrap items-center gap-2 bg-surface border-b border-border px-4 py-2">
        <div className="flex items-center gap-2">
          <label className="text-xs text-muted whitespace-nowrap">Source:</label>
          <select
            value={taggerConfig.selectedEndpoint}
            onChange={(e) => setTaggerConfig((c) => ({ ...c, selectedEndpoint: e.target.value }))}
            className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
          >
            {metadataServers.map((sb) => (
              <option key={sb.endpoint} value={sb.endpoint}>
                {sb.name || sb.endpoint}
              </option>
            ))}
          </select>
        </div>

        <button
          onClick={() => setTaggerConfig((c) => ({ ...c, showTagged: !c.showTagged }))}
          className="flex items-center gap-1 px-2 py-1 rounded text-xs border border-border bg-input text-secondary hover:text-foreground"
        >
          {taggerConfig.showTagged ? <Eye className="w-3.5 h-3.5" /> : <EyeOff className="w-3.5 h-3.5" />}
          {taggerConfig.showTagged ? "Hide Already Tagged" : "Show Already Tagged"}
        </button>

        {batchSearching ? (
          <button
            onClick={cancelBatchSearch}
            className="flex items-center gap-1.5 px-3 py-1 rounded text-xs font-medium bg-red-600 text-white hover:bg-red-500"
          >
            <X className="w-3.5 h-3.5" />
            Cancel
          </button>
        ) : (
          <button
            onClick={searchAll}
            className="flex items-center gap-1.5 px-3 py-1 rounded text-xs font-medium bg-accent text-white hover:bg-accent-hover"
          >
            <CloudDownload className="w-3.5 h-3.5" />
            Scrape All
          </button>
        )}

        <span className="text-xs text-muted ml-auto">
          {visiblePerformers.length} performer{visiblePerformers.length !== 1 ? "s" : ""}
        </span>
      </div>

      {/* Performer list */}
      <div className="divide-y divide-border">
        {visiblePerformers.map((performer) => (
          <PerformerTaggerRow
            key={performer.id}
            performer={performer}
            state={searchStates[performer.id]}
            query={queryOverrides[performer.id] ?? performer.name}
            onQueryChange={(q) => setQueryOverrides((prev) => ({ ...prev, [performer.id]: q }))}
            onSearch={() => searchPerformer(performer)}
            onUpdateState={(update) => updateSearchState(performer.id, update)}
            endpoint={taggerConfig.selectedEndpoint}
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
  onSearch,
  onUpdateState,
  endpoint,
}: {
  performer: Performer;
  state?: PerformerSearchState;
  query: string;
  onQueryChange: (q: string) => void;
  onSearch: () => void;
  onUpdateState: (update: Partial<PerformerSearchState>) => void;
  endpoint: string;
}) {
  const imageUrl = performer.imagePath;

  const importMut = useMutation({
    mutationFn: () => {
      const selectedResult = state?.results?.[state.selectedIndex ?? 0];
      if (!selectedResult) throw new Error("No result selected");
      const importReq: MetadataServerPerformerImportRequest = {
        endpoint,
        performerId: selectedResult.id,
      };
      return performers.importFromMetadataServer(performer.id, importReq);
    },
    onSuccess: () => {
      onUpdateState({ saved: true });
    },
  });

  return (
    <div className={`px-4 py-3 ${state?.saved ? "opacity-50" : ""}`}>
      <div className="flex gap-4">
        {/* Performer image */}
        <div className="flex-shrink-0 w-24">
          <div className="relative aspect-[2/3] bg-card rounded overflow-hidden">
            {imageUrl ? (
              <img src={imageUrl} alt="" className="w-full h-full object-cover" loading="lazy" />
            ) : (
              <div className="w-full h-full flex items-center justify-center text-muted text-xs">No Image</div>
            )}
          </div>
          <p className="text-xs text-foreground mt-1 truncate font-medium">{performer.name}</p>
          {performer.remoteIds && performer.remoteIds.length > 0 && (
            <div className="flex flex-wrap gap-1 mt-1">
              {performer.remoteIds.map((sid) => (
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
          </div>

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
                <PerformerResultRow
                  key={`${result.endpoint}-${result.id}`}
                  result={result}
                  isSelected={i === (state.selectedIndex ?? 0)}
                  onClick={() => onUpdateState({ selectedIndex: i })}
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

function PerformerResultRow({
  result,
  isSelected,
  onClick,
  onSave,
  saving,
  saved,
}: {
  result: MetadataServerPerformerMatch;
  isSelected: boolean;
  onClick: () => void;
  onSave?: () => void;
  saving?: boolean;
  saved?: boolean;
}) {
  return (
    <div
      onClick={onClick}
      className={`rounded border cursor-pointer transition-colors ${
        isSelected ? "border-accent bg-card" : "border-border bg-surface hover:border-accent/50"
      }`}
    >
      <div className="flex items-center gap-3 p-2">
        {result.imageUrl && (
          <img src={result.imageUrl} alt="" className="w-10 h-14 object-cover rounded flex-shrink-0" loading="lazy" />
        )}
        <div className="flex-1 min-w-0">
          <p className="text-xs font-medium text-foreground truncate">{result.name}</p>
          <div className="flex items-center gap-2 text-[10px] text-muted">
            {result.disambiguation && <span>({result.disambiguation})</span>}
            {result.gender && <span>{result.gender}</span>}
            {result.country && <span>{result.country}</span>}
            {result.birthDate && <span>{result.birthDate}</span>}
          </div>
        </div>
      </div>

      {isSelected && (
        <div className="border-t border-border p-3">
          <div className="grid grid-cols-2 gap-2 text-xs mb-3">
            {result.disambiguation && <FieldRow label="Disambiguation" value={result.disambiguation} />}
            {result.gender && <FieldRow label="Gender" value={result.gender} />}
            {result.birthDate && <FieldRow label="Birthdate" value={result.birthDate} />}
            {result.country && <FieldRow label="Country" value={result.country} />}
            {result.aliases && result.aliases.length > 0 && <FieldRow label="Aliases" value={result.aliases.join(", ")} />}
          </div>

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

function FieldRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex gap-2">
      <span className="text-muted w-24 flex-shrink-0 text-right">{label}:</span>
      <span className="text-foreground truncate">{value}</span>
    </div>
  );
}
