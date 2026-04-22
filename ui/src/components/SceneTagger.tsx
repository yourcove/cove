import { useCallback, useState, useRef } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { scenes } from "../api/client";
import type { Scene, MetadataServerSceneMatch, MetadataServerSceneImportRequest } from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";
import { formatDuration, getResolutionLabel } from "./shared";
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
} from "lucide-react";

interface SceneTaggerProps {
  scenes: Scene[];
}

interface TaggerConfig {
  selectedEndpoint: string;
  showUnmatched: boolean;
  setCoverImage: boolean;
  setTags: boolean;
  tagOperation: "merge" | "overwrite";
  setPerformers: boolean;
  setStudio: boolean;
  onlyExistingTags: boolean;
  onlyExistingPerformers: boolean;
  onlyExistingStudio: boolean;
  markOrganized: boolean;
  preferFingerprints: boolean;
  queryMode: "auto" | "filename" | "dir" | "path" | "metadata";
  blacklist: string[];
  createParentStudios: boolean;
  createParentTags: boolean;
  showMales: boolean;
  performerGenders: string[];
}

interface SceneSearchState {
  loading: boolean;
  results?: MetadataServerSceneMatch[];
  error?: string;
  selectedIndex?: number;
  saved?: boolean;
  excludedPerformers?: Set<string>;
  excludedTags?: Set<string>;
  skipStudio?: boolean;
  forceIncludedPerformers?: Set<string>;
  forceIncludedTags?: Set<string>;
  forceIncludeStudio?: boolean;
}

const CONCURRENCY_LIMIT = 5;

// Date / JAV-code aware string cleaning (standard behavior)
const months = ["jan","feb","mar","apr","may","jun","jul","aug","sep","oct","nov","dec"];
const ddmmyyRegex = /\.(\d\d)\.(\d\d)\.(\d\d)\./;
const yyyymmddRegex = /(\d{4})[-.](\d{2})[-.](\d{2})/;
const mmddyyRegex = /(\d{2})[-.](\d{2})[-.](\d{4})/;
const ddMMyyRegex = new RegExp(`(\\d{1,2}).(${months.join("|")})\\.?.(\\d{4})`, "i");
const MMddyyRegex = new RegExp(`(${months.join("|")})\\.?.(\\d{1,2}),?.(\\d{4})`, "i");
const javcodeRegex = /([a-zA-Z|tT28|tT38]+-\d+[zZeE]?)/;

function handleSpecialStrings(input: string): string {
  let output = input;
  const ddmmyy = output.match(ddmmyyRegex);
  if (ddmmyy) output = output.replace(ddmmyy[0], ` 20${ddmmyy[1]}-${ddmmyy[2]}-${ddmmyy[3]} `);
  const mmddyy = output.match(mmddyyRegex);
  if (mmddyy) output = output.replace(mmddyy[0], ` ${mmddyy[1]}-${mmddyy[2]}-${mmddyy[3]} `);
  const ddMMyy = output.match(ddMMyyRegex);
  if (ddMMyy) {
    const month = (months.indexOf(ddMMyy[2].toLowerCase()) + 1).toString().padStart(2, "0");
    output = output.replace(ddMMyy[0], ` ${ddMMyy[3]}-${month}-${ddMMyy[1].padStart(2, "0")} `);
  }
  const MMddyy = output.match(MMddyyRegex);
  if (MMddyy) {
    const month = (months.indexOf(MMddyy[1].toLowerCase()) + 1).toString().padStart(2, "0");
    output = output.replace(MMddyy[0], ` ${MMddyy[3]}-${month}-${MMddyy[2].padStart(2, "0")} `);
  }
  const yyyymmdd = output.search(yyyymmddRegex);
  if (yyyymmdd !== -1)
    return output.slice(0, yyyymmdd).replace(/-/g, " ") + output.slice(yyyymmdd, yyyymmdd + 10).replace(/\./g, "-") + output.slice(yyyymmdd + 10).replace(/-/g, " ");
  const javcodeIndex = output.search(javcodeRegex);
  if (javcodeIndex !== -1) {
    const javcodeLength = output.match(javcodeRegex)![1].length;
    return output.slice(0, javcodeIndex).replace(/-/g, " ") + output.slice(javcodeIndex, javcodeIndex + javcodeLength) + output.slice(javcodeIndex + javcodeLength).replace(/-/g, " ");
  }
  return output.replace(/-/g, " ");
}

function cleanQueryString(input: string, blacklist: string[]): string {
  // Convert dots/underscores to spaces so tokens are properly separated
  let cleaned = input.replace(/[._]/g, " ");
  // Apply each blacklist item as a regex — every match is removed from the string
  for (const pattern of blacklist) {
    try {
      cleaned = cleaned.replace(new RegExp(pattern, "gi"), "");
    } catch { /* invalid regex — skip */ }
  }
  cleaned = handleSpecialStrings(cleaned);
  return cleaned.replace(/ +/g, " ").trim();
}

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

export function SceneTagger({ scenes: sceneList }: SceneTaggerProps) {
  const { config } = useAppConfig();
  const metadataServers = config?.scraping?.metadataServers ?? [];

  const TAGGER_CONFIG_KEY = "cove-tagger-config";

  const DEFAULT_TAGGER_CONFIG: TaggerConfig = {
    selectedEndpoint: metadataServers[0]?.endpoint ?? "",
    showUnmatched: true,
    setCoverImage: true,
    setTags: true,
    tagOperation: "merge",
    setPerformers: true,
    setStudio: true,
    onlyExistingTags: false,
    onlyExistingPerformers: false,
    onlyExistingStudio: false,
    markOrganized: false,
    preferFingerprints: true,
    queryMode: "auto",
    blacklist: ["\\sXXX\\s", "1080p", "720p", "2160p", "4K", "KTR", "RARBG", "\\smp4\\s"],
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
  const [searchStates, setSearchStates] = useState<Record<number, SceneSearchState>>({});
  const [queryOverrides, setQueryOverrides] = useState<Record<number, string>>({});

  const updateSearchState = useCallback(
    (sceneId: number, update: Partial<SceneSearchState>) => {
      setSearchStates((prev) => ({
        ...prev,
        [sceneId]: { ...prev[sceneId], ...update },
      }));
    },
    []
  );

  // Derive search query from scene (standard prepareQueryString logic)
  const getSearchQuery = useCallback(
    (scene: Scene): string => {
      if (queryOverrides[scene.id] !== undefined) return queryOverrides[scene.id];
      const file = scene.files[0];
      const mode = taggerConfig.queryMode;

      // metadata mode, or auto mode when scene has date+studio — build compound query
      if (mode === "metadata" || (mode === "auto" && scene.date && scene.studioName)) {
        let str = [
          scene.date || "",
          scene.studioName || "",
          (scene.performers || []).map((p: any) => p.name).join(" "),
          scene.title ? scene.title.replace(/[^a-zA-Z0-9 ]+/g, "") : "",
        ].filter((s) => s !== "").join(" ");
        str = cleanQueryString(str, taggerConfig.blacklist);
        return str;
      }

      // filename/dir/path modes: derive from file path
      if (mode === "filename" && file?.basename) {
        return cleanQueryString(file.basename.replace(/\.\w{2,4}$/, ""), taggerConfig.blacklist);
      }
      if (mode === "dir" && file?.path) {
        const parts = file.path.replace(/\\/g, "/").split("/");
        return parts.length > 1 ? cleanQueryString(parts[parts.length - 2], taggerConfig.blacklist) : "";
      }
      if (mode === "path" && file?.path) {
        return cleanQueryString(file.path, taggerConfig.blacklist);
      }

      // auto mode: try title first, then filename — always apply blacklist
      if (scene.title) return cleanQueryString(scene.title, taggerConfig.blacklist);
      if (file?.basename) {
        return cleanQueryString(file.basename.replace(/\.\w{2,4}$/, ""), taggerConfig.blacklist);
      }
      return "";
    },
    [queryOverrides, taggerConfig.queryMode, taggerConfig.blacklist]
  );

  const searchScene = useCallback(
    async (scene: Scene) => {
      const query = getSearchQuery(scene);
      updateSearchState(scene.id, { loading: true, error: undefined, results: undefined, saved: false });
      try {
        let results: MetadataServerSceneMatch[] = [];
        const endpoint = taggerConfig.selectedEndpoint || undefined;
        const shouldTryFingerprints = taggerConfig.preferFingerprints || !query;

        if (shouldTryFingerprints) {
          results = await scenes.searchMetadataServer(scene.id, undefined, endpoint);
        }

        if (results.length === 0 && query) {
          results = await scenes.searchMetadataServer(scene.id, query, endpoint);
        }

        updateSearchState(scene.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
        });
      } catch (err) {
        updateSearchState(scene.id, {
          loading: false,
          error: err instanceof Error ? err.message : "Search failed",
        });
      }
    },
    [getSearchQuery, taggerConfig.preferFingerprints, taggerConfig.selectedEndpoint, updateSearchState]
  );

  // Fingerprint-only search
  const searchSceneFingerprints = useCallback(
    async (scene: Scene) => {
      updateSearchState(scene.id, { loading: true, error: undefined, results: undefined, saved: false });
      try {
        const endpoint = taggerConfig.selectedEndpoint || undefined;
        const results = await scenes.searchMetadataServer(scene.id, undefined, endpoint);
        updateSearchState(scene.id, {
          loading: false,
          results,
          selectedIndex: results.length > 0 ? 0 : undefined,
        });
      } catch (err) {
        updateSearchState(scene.id, {
          loading: false,
          error: err instanceof Error ? err.message : "Search failed",
        });
      }
    },
    [taggerConfig.selectedEndpoint, updateSearchState]
  );

  // Batch scrape all (concurrent)
  const [batchSearching, setBatchSearching] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const searchAll = useCallback(async () => {
    setBatchSearching(true);
    const controller = new AbortController();
    abortRef.current = controller;
    const toSearch = sceneList.filter((s) => !searchStates[s.id]?.saved);
    await runWithConcurrency(toSearch, (scene) => searchScene(scene), CONCURRENCY_LIMIT, controller.signal);
    setBatchSearching(false);
    abortRef.current = null;
  }, [sceneList, searchStates, searchScene]);

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

  const visibleScenes = taggerConfig.showUnmatched
    ? sceneList
    : sceneList.filter((s) => {
        const state = searchStates[s.id];
        return !state || !state.results || state.results.length > 0;
      });

  return (
    <div className="space-y-0">
      {/* Tagger Toolbar — clean like V1: Source / Hide Unmatched / Scrape All / Config gear */}
      <div className="flex flex-wrap items-center gap-2 bg-surface border-b border-border px-4 py-2">
        {/* Source selector */}
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

        {/* Show/Hide unmatched */}
        <button
          onClick={() => setTaggerConfig((c) => ({ ...c, showUnmatched: !c.showUnmatched }))}
          className="flex items-center gap-1 px-2 py-1 rounded text-xs border border-border bg-input text-secondary hover:text-foreground"
        >
          {taggerConfig.showUnmatched ? <Eye className="w-3.5 h-3.5" /> : <EyeOff className="w-3.5 h-3.5" />}
          {taggerConfig.showUnmatched ? "Hide Unmatched" : "Show Unmatched"}
        </button>

        {/* Scrape All / Cancel */}
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

        {/* Config toggle */}
        <button
          onClick={() => setShowConfig(!showConfig)}
          className={`flex items-center gap-1 px-2 py-1 rounded text-xs border bg-input ml-auto ${showConfig ? "border-accent text-accent" : "border-border text-secondary hover:text-foreground"}`}
        >
          <Settings2 className="w-3.5 h-3.5" />
        </button>
      </div>

      {/* Config panel — standard layout: Configuration (left) / Blacklist (right) */}
      {showConfig && (
        <div className="bg-card border-b border-border px-4 py-3 space-y-4">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {/* Left: Configuration */}
            <div className="space-y-3">
              <h3 className="text-sm font-bold text-foreground italic">Configuration</h3>

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
                <p className="text-[10px] text-muted mt-1">Performers with these genders will be shown when tagging scenes.</p>
              </div>

              {/* Set scene cover image */}
              <div>
                <label className="flex items-center gap-2 text-xs text-foreground">
                  <input type="checkbox" checked={taggerConfig.setCoverImage} onChange={(e) => setTaggerConfig((c) => ({ ...c, setCoverImage: e.target.checked }))} className="rounded border-border" />
                  Set scene cover image
                </label>
                <p className="text-[10px] text-muted mt-0.5 ml-5">Replace the scene cover if one is found.</p>
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
                <p className="text-[10px] text-muted mt-0.5 ml-5">Attach performers to scene. Uncheck "Create missing" to only use performers that already exist.</p>
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
                <p className="text-[10px] text-muted mt-0.5 ml-5">Set the scene studio. Uncheck "Create missing" to only use studios that already exist.</p>
              </div>

              {/* Set tags + operation */}
              <div>
                <div className="flex items-center gap-3">
                  <label className="flex items-center gap-2 text-xs text-foreground">
                    <input type="checkbox" checked={taggerConfig.setTags} onChange={(e) => setTaggerConfig((c) => ({ ...c, setTags: e.target.checked }))} className="rounded border-border" />
                    Set tags
                  </label>
                  {taggerConfig.setTags && (
                    <select value={taggerConfig.tagOperation} onChange={(e) => setTaggerConfig((c) => ({ ...c, tagOperation: e.target.value as "merge" | "overwrite" }))} className="bg-input border border-border rounded px-2 py-0.5 text-xs text-foreground focus:outline-none focus:border-accent">
                      <option value="merge">Merge</option>
                      <option value="overwrite">Overwrite</option>
                    </select>
                  )}
                </div>
                {taggerConfig.setTags && (
                  <label className="flex items-center gap-2 text-xs text-foreground ml-5 mt-1">
                    <input type="checkbox" checked={!taggerConfig.onlyExistingTags} onChange={(e) => setTaggerConfig((c) => ({ ...c, onlyExistingTags: !e.target.checked }))} className="rounded border-border" />
                    Create missing tags
                  </label>
                )}
                <p className="text-[10px] text-muted mt-0.5 ml-5">Attach tags to scene. Uncheck "Create missing" to only set tags that already exist.</p>
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

              {/* Mark organized */}
              <div>
                <label className="flex items-center gap-2 text-xs text-foreground">
                  <input type="checkbox" checked={taggerConfig.markOrganized} onChange={(e) => setTaggerConfig((c) => ({ ...c, markOrganized: e.target.checked }))} className="rounded border-border" />
                  Mark as Organized on save
                </label>
                <p className="text-[10px] text-muted mt-0.5 ml-5">Immediately mark the scene as Organized after the Save button is clicked.</p>
              </div>
            </div>

            {/* Right: Blacklist */}
            <div className="space-y-2">
              <h3 className="text-sm font-bold text-foreground italic">Blacklist</h3>
              <BlacklistEditor
                items={taggerConfig.blacklist}
                onChange={(items) => setTaggerConfig((c) => ({ ...c, blacklist: items }))}
              />
              <p className="text-[10px] text-muted">
                Blacklist items are excluded from queries. Note that they are regular expressions and also case-insensitive. Certain characters must be escaped with a backslash: <code className="text-pink-400">{`[\\.^$.|?*+()`}</code>
              </p>
            </div>
          </div>
        </div>
      )}

      {/* Scene list */}
      <div className="divide-y divide-border">
        {visibleScenes.map((scene) => (
          <TaggerSceneRow
            key={scene.id}
            scene={scene}
            state={searchStates[scene.id]}
            query={getSearchQuery(scene)}
            onQueryChange={(q) => setQueryOverrides((prev) => ({ ...prev, [scene.id]: q }))}
            onSearch={() => searchScene(scene)}
            onSearchFingerprints={() => searchSceneFingerprints(scene)}
            onUpdateState={(update) => updateSearchState(scene.id, update)}
            endpoint={taggerConfig.selectedEndpoint}
            taggerConfig={taggerConfig}
          />
        ))}
      </div>
    </div>
  );
}

/* ── Scene Tagger Row ── */

interface TaggerSceneRowProps {
  scene: Scene;
  state?: SceneSearchState;
  query: string;
  onQueryChange: (q: string) => void;
  onSearch: () => void;
  onSearchFingerprints: () => void;
  onUpdateState: (update: Partial<SceneSearchState>) => void;
  endpoint: string;
  taggerConfig: TaggerConfig;
}

function TaggerSceneRow({
  scene,
  state,
  query,
  onQueryChange,
  onSearch,
  onSearchFingerprints,
  onUpdateState,
  endpoint,
  taggerConfig,
}: TaggerSceneRowProps) {
  const file = scene.files[0];
  const screenshotUrl = scenes.screenshotUrl(scene.id, scene.updatedAt);
  const selectedResult = state?.results?.[state.selectedIndex ?? 0];
  const queryClient = useQueryClient();

  const importMut = useMutation({
    mutationFn: () => {
      const excludedTags = state?.excludedTags ? Array.from(state.excludedTags) : undefined;
      const excludedPerformers = state?.excludedPerformers ? Array.from(state.excludedPerformers) : undefined;

      // Build overrides for force-included entities (entities that would normally be skipped
      // by onlyExisting* flags but the user explicitly opted to create)
      const performerOverrides = state?.forceIncludedPerformers?.size
        ? selectedResult?.performerCandidates
            .filter(p => state.forceIncludedPerformers!.has(p.name))
            .map(p => ({ remoteId: p.remoteId, name: p.name, action: "create" }))
        : undefined;
      const tagOverrides = state?.forceIncludedTags?.size
        ? selectedResult?.tagCandidates
            .filter(t => state.forceIncludedTags!.has(t.name))
            .map(t => ({ remoteId: t.remoteId, name: t.name, action: "create" }))
        : undefined;
      const studioOverride = state?.forceIncludeStudio && selectedResult?.studioCandidate
        ? { remoteId: selectedResult.studioCandidate.remoteId, name: selectedResult.studioCandidate.name, action: "create" }
        : undefined;

      const importReq: MetadataServerSceneImportRequest = {
        endpoint,
        sceneId: selectedResult?.id ?? "",
        setCoverImage: taggerConfig.setCoverImage,
        setTags: taggerConfig.setTags,
        setPerformers: taggerConfig.setPerformers,
        setStudio: taggerConfig.setStudio && !state?.skipStudio,
        onlyExistingTags: taggerConfig.onlyExistingTags,
        onlyExistingPerformers: taggerConfig.onlyExistingPerformers,
        onlyExistingStudio: taggerConfig.onlyExistingStudio,
        markOrganized: taggerConfig.markOrganized,
        excludedTagNames: excludedTags,
        excludedPerformerNames: excludedPerformers,
        performerOverrides,
        tagOverrides,
        studioOverride,
      };
      return scenes.importFromMetadataServer(scene.id, importReq);
    },
    onSuccess: () => {
      onUpdateState({ saved: true });
      queryClient.invalidateQueries({ queryKey: ["scene", scene.id] });
      queryClient.invalidateQueries({ queryKey: ["scenes"] });
    },
  });

  return (
    <div className={`px-3 py-2 ${state?.saved ? "opacity-50" : ""}`}>
      <div className="flex gap-3">
        {/* Scene preview — compact */}
        <div className="flex-shrink-0 w-32">
          <div className="relative aspect-video bg-card rounded overflow-hidden">
            <img
              src={screenshotUrl}
              alt=""
              className="w-full h-full object-cover"
              loading="lazy"
              onError={(e) => {
                (e.target as HTMLImageElement).style.display = "none";
              }}
            />
            {file && file.duration > 0 && (
              <span className="absolute bottom-0.5 right-0.5 text-[8px] text-white bg-black/70 px-0.5 rounded">
                {formatDuration(file.duration)}
              </span>
            )}
          </div>
          <p className="text-[11px] text-foreground mt-0.5 truncate font-medium leading-snug">
            {scene.title || file?.basename || "Untitled"}
          </p>
          <p className="text-[9px] text-muted truncate leading-snug">
            {[scene.studioName, file && getResolutionLabel(file.width, file.height)].filter(Boolean).join(" · ")}
          </p>
        </div>

        {/* Search + Results */}
        <div className="flex-1 min-w-0">
          {/* Search input — inline and compact */}
          <div className="flex gap-1.5 mb-1.5">
            <input
              type="text"
              value={query}
              onChange={(e) => onQueryChange(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && onSearch()}
              placeholder="Search query..."
              className="flex-1 min-w-0 bg-input border border-border rounded pl-2 pr-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
            />
            <button
              onClick={onSearch}
              disabled={state?.loading}
              className="flex items-center gap-1 px-2 py-1 rounded text-xs font-medium bg-accent text-white hover:bg-accent-hover disabled:opacity-60"
            >
              {state?.loading ? <Loader2 className="w-3 h-3 animate-spin" /> : <Search className="w-3 h-3" />}
            </button>
            <button
              onClick={onSearchFingerprints}
              disabled={state?.loading}
              className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-surface border border-border text-muted hover:text-foreground disabled:opacity-60"
              title="Search by fingerprint only"
            >
              <Fingerprint className="w-3 h-3" />
            </button>
          </div>

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
              results={state.results}
              selectedIndex={state.selectedIndex ?? 0}
              onSelect={(i) => onUpdateState({ selectedIndex: i })}
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
  results: MetadataServerSceneMatch[];
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
  onTogglePerformer: (name: string) => void;
  onToggleTag: (name: string) => void;
  onToggleStudio: () => void;
  taggerConfig: TaggerConfig;
}

function TaggerResults({ results, selectedIndex, onSelect, onSave, saving, saved, localDuration, excludedPerformers, excludedTags, skipStudio, forceIncludedPerformers, forceIncludedTags, forceIncludeStudio, onTogglePerformer, onToggleTag, onToggleStudio, taggerConfig }: TaggerResultsProps) {
  return (
    <div className="space-y-1">
      {results.map((result, i) => (
        <TaggerResultRow
          key={`${result.endpoint}-${result.id}`}
          result={result}
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
  result,
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
  onTogglePerformer,
  onToggleTag,
  onToggleStudio,
  taggerConfig,
}: {
  result: MetadataServerSceneMatch;
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
  onTogglePerformer?: (name: string) => void;
  onToggleTag?: (name: string) => void;
  onToggleStudio?: () => void;
  taggerConfig: TaggerConfig;
}) {
  const durationDiff = localDuration != null && result.duration != null
    ? Math.abs(localDuration - result.duration)
    : undefined;
  const durationMatch = durationDiff != null && durationDiff < 5;

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

        {/* Fingerprint indicators — shows which algorithms the remote scene has, with match status */}
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
      {isSelected && (
        <div className="border-t border-border px-3 py-3 space-y-3">
          {/* Description */}
          {result.details && (
            <p className="text-xs text-secondary leading-relaxed line-clamp-3">{result.details}</p>
          )}

          {/* Studio */}
          {result.studioCandidate && taggerConfig.setStudio && (
            <div className="flex items-center gap-2">
              <span className="text-[10px] text-muted uppercase tracking-wider w-20 shrink-0">Studio</span>
              {(() => {
                const willSkipByDefault = taggerConfig.onlyExistingStudio && !result.studioCandidate.existsLocally;
                const isForceIncluded = forceIncludeStudio && willSkipByDefault;
                return (
                  <span
                    onClick={(e) => { e.stopPropagation(); onToggleStudio?.(); }}
                    className={`inline-flex items-center gap-1 text-xs px-2 py-1 rounded cursor-pointer border transition-colors ${
                      skipStudio
                        ? "bg-surface text-muted border-border line-through opacity-60"
                        : isForceIncluded
                          ? "bg-amber-600/10 text-amber-300 border-amber-600/20"
                          : willSkipByDefault
                            ? "bg-surface text-muted border-border opacity-60"
                            : result.studioCandidate.existsLocally
                              ? "bg-green-600/10 text-green-300 border-green-600/20"
                              : "bg-amber-600/10 text-amber-300 border-amber-600/20"
                    }`}
                    title={
                      skipStudio ? "Excluded — click to include"
                      : isForceIncluded ? "Force include — click to skip"
                      : willSkipByDefault ? "Won't be added (doesn't exist locally) — click to force include"
                      : result.studioCandidate.existsLocally ? "✓ Matched locally — click to exclude"
                      : "+ Will create — click to exclude"
                    }
                  >
                    {skipStudio ? <Minus className="w-3 h-3" /> : isForceIncluded ? <Check className="w-3 h-3" /> : willSkipByDefault ? <Plus className="w-3 h-3" /> : result.studioCandidate.existsLocally ? <Check className="w-3 h-3" /> : <Check className="w-3 h-3" />}
                    {result.studioCandidate.name}
                  </span>
                );
              })()}
            </div>
          )}

          {/* Performers */}
          {result.performerCandidates.length > 0 && taggerConfig.setPerformers && (
            <div className="flex items-start gap-2">
              <span className="text-[10px] text-muted uppercase tracking-wider w-20 shrink-0 pt-1">Performers</span>
              <div className="flex flex-wrap gap-1">
                {result.performerCandidates.map((perf) => {
                  const excluded = excludedPerformers.has(perf.name);
                  const willSkipByDefault = taggerConfig.onlyExistingPerformers && !perf.existsLocally;
                  const isForceIncluded = forceIncludedPerformers.has(perf.name) && willSkipByDefault;
                  return (
                    <span
                      key={perf.remoteId}
                      onClick={(e) => { e.stopPropagation(); onTogglePerformer?.(perf.name); }}
                      className={`inline-flex items-center gap-1 text-xs px-2 py-1 rounded cursor-pointer border transition-colors ${
                        excluded
                          ? "bg-surface text-muted border-border line-through opacity-60"
                          : isForceIncluded
                            ? "bg-amber-600/10 text-amber-300 border-amber-600/20"
                            : willSkipByDefault
                              ? "bg-surface text-muted border-border opacity-60"
                              : perf.existsLocally
                                ? "bg-green-600/10 text-green-300 border-green-600/20"
                                : "bg-amber-600/10 text-amber-300 border-amber-600/20"
                      }`}
                      title={
                        excluded ? "Excluded — click to include"
                        : isForceIncluded ? "Force include — click to skip"
                        : willSkipByDefault ? `Won't be added (doesn't exist locally) — click to force include`
                        : perf.existsLocally ? `✓ Matched locally — click to exclude`
                        : `+ Will create — click to exclude`
                      }
                    >
                      {excluded ? <Minus className="w-3 h-3" /> : isForceIncluded ? <Check className="w-3 h-3" /> : willSkipByDefault ? <Plus className="w-3 h-3" /> : perf.existsLocally ? <Check className="w-3 h-3" /> : <Check className="w-3 h-3" />}
                      {perf.name}
                    </span>
                  );
                })}
              </div>
            </div>
          )}

          {/* Tags */}
          {result.tagCandidates.length > 0 && taggerConfig.setTags && (
            <div className="flex items-start gap-2">
              <span className="text-[10px] text-muted uppercase tracking-wider w-20 shrink-0 pt-1">Tags</span>
              <div className="flex flex-wrap gap-1">
                {result.tagCandidates.map((tag) => {
                  const excluded = excludedTags.has(tag.name);
                  const willSkipByDefault = taggerConfig.onlyExistingTags && !tag.existsLocally;
                  const isForceIncluded = forceIncludedTags.has(tag.name) && willSkipByDefault;
                  return (
                    <span
                      key={tag.remoteId}
                      onClick={(e) => { e.stopPropagation(); onToggleTag?.(tag.name); }}
                      className={`inline-flex items-center gap-1 text-[11px] px-1.5 py-0.5 rounded cursor-pointer border transition-colors ${
                        excluded
                          ? "bg-surface text-muted border-border line-through opacity-60"
                          : isForceIncluded
                            ? "bg-amber-600/10 text-amber-300 border-amber-600/20"
                            : willSkipByDefault
                              ? "bg-surface text-muted border-border opacity-60"
                              : tag.existsLocally
                                ? "bg-green-600/10 text-green-300 border-green-600/20"
                                : "bg-amber-600/10 text-amber-300 border-amber-600/20"
                      }`}
                      title={
                        excluded ? "Excluded — click to include"
                        : isForceIncluded ? "Force include — click to skip"
                        : willSkipByDefault ? `Won't be added (doesn't exist locally) — click to force include`
                        : tag.existsLocally ? `✓ Matched locally — click to exclude`
                        : `+ Will create — click to exclude`
                      }
                    >
                      {excluded ? <Minus className="w-2.5 h-2.5" /> : isForceIncluded ? <Check className="w-2.5 h-2.5" /> : willSkipByDefault ? <Plus className="w-2.5 h-2.5" /> : tag.existsLocally ? <Check className="w-2.5 h-2.5" /> : <Check className="w-2.5 h-2.5" />}
                      {tag.name}
                    </span>
                  );
                })}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/* ── Blacklist Editor (tag-style pills) ── */

function BlacklistEditor({ items, onChange }: { items: string[]; onChange: (items: string[]) => void }) {
  const [input, setInput] = useState("");

  const addItem = () => {
    const trimmed = input.trim();
    if (trimmed && !items.includes(trimmed)) {
      onChange([...items, trimmed]);
      setInput("");
    }
  };

  const removeItem = (index: number) => {
    onChange(items.filter((_, i) => i !== index));
  };

  return (
    <div className="space-y-2">
      <div className="flex gap-1.5">
        <input
          type="text"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addItem(); } }}
          placeholder=""
          className="flex-1 bg-input border border-border rounded px-2 py-1.5 text-xs text-foreground outline-none focus:border-accent font-mono"
        />
        <button
          onClick={addItem}
          disabled={!input.trim()}
          className="px-3 py-1.5 text-xs rounded border border-border bg-surface text-foreground hover:bg-card disabled:opacity-40"
        >
          Add
        </button>
      </div>
      <div className="flex flex-wrap gap-1.5">
        {items.map((item, i) => (
          <span
            key={i}
            className="inline-flex items-center gap-1 bg-surface text-foreground text-xs px-2 py-1 rounded border border-border font-mono"
          >
            {item}
            <button onClick={() => removeItem(i)} className="text-muted hover:text-red-400 ml-0.5">
              <X className="w-3 h-3" />
            </button>
          </span>
        ))}
      </div>
    </div>
  );
}
