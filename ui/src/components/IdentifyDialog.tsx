import { useEffect, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { metadata } from "../api/client";
import type { MetadataServer } from "../api/types";
import { useAppConfig } from "../state/AppConfigContext";
import {
  Search,
  X,
  Loader2,
  Check,
  GripVertical,
  ChevronDown,
  ChevronUp,
  Info,
} from "lucide-react";

interface Props {
  open: boolean;
  onClose: () => void;
  sceneIds?: number[];
}

interface IdentifySource {
  id: string;
  name: string;
  type: "metadata-server" | "scraper" | "auto-tag";
  enabled: boolean;
}

const DEFAULT_IDENTIFY_DEFAULTS = {
  createTags: true,
  createPerformers: true,
  createStudios: true,
};

function buildIdentifySources(metadataServers: MetadataServer[]): IdentifySource[] {
  const sources: IdentifySource[] = [];
  metadataServers.forEach((box, i) => {
    sources.push({
      id: `metadata-server-${i}`,
      name: box.name || box.endpoint,
      type: "metadata-server",
      enabled: true,
    });
  });
  sources.push({
    id: "auto-tag",
    name: "Auto Tag (built-in)",
    type: "auto-tag",
    enabled: true,
  });
  return sources;
}

export function IdentifyDialog({ open, onClose, sceneIds }: Props) {
  const queryClient = useQueryClient();
  const { config } = useAppConfig();

  const metadataServers = config?.scraping?.metadataServers ?? [];
  const identifyDefaults = config?.scraping?.identifyDefaults ?? DEFAULT_IDENTIFY_DEFAULTS;

  const [sources, setSources] = useState<IdentifySource[]>(() => buildIdentifySources(metadataServers));

  const [showOptions, setShowOptions] = useState(false);
  const [setCoverImage, setSetCoverImage] = useState(true);
  const [setOrganized, setSetOrganized] = useState(false);
  const [skipMultipleMatches, setSkipMultipleMatches] = useState(true);
  const [skipSingleNamePerformers, setSkipSingleNamePerformers] = useState(true);
  const [createTags, setCreateTags] = useState(identifyDefaults.createTags);
  const [createPerformers, setCreatePerformers] = useState(identifyDefaults.createPerformers);
  const [createStudios, setCreateStudios] = useState(identifyDefaults.createStudios);

  useEffect(() => {
    if (!open) {
      return;
    }

    setSources(buildIdentifySources(metadataServers));
    setCreateTags(identifyDefaults.createTags);
    setCreatePerformers(identifyDefaults.createPerformers);
    setCreateStudios(identifyDefaults.createStudios);
  }, [open, metadataServers, identifyDefaults.createTags, identifyDefaults.createPerformers, identifyDefaults.createStudios]);

  const identifyMut = useMutation({
    mutationFn: () => {
      const enabledSources = sources.filter((s) => s.enabled).map((s) => s.name);
      return metadata.identify({
        sources: enabledSources.length > 0 ? enabledSources : undefined,
        sceneIds,
        setCoverImage,
        markOrganized: setOrganized,
        skipMultipleMatches,
        skipSingleNamePerformers,
        createTags,
        createPerformers,
        createStudios,
      });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["jobs"] });
      onClose();
    },
  });

  const toggleSource = (id: string) => {
    setSources(sources.map((s) => (s.id === id ? { ...s, enabled: !s.enabled } : s)));
  };

  const moveSource = (index: number, direction: "up" | "down") => {
    const newSources = [...sources];
    const target = direction === "up" ? index - 1 : index + 1;
    if (target < 0 || target >= newSources.length) return;
    [newSources[index], newSources[target]] = [newSources[target], newSources[index]];
    setSources(newSources);
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70">
      <div className="bg-surface border border-border rounded-2xl shadow-2xl w-full max-w-lg max-h-[85vh] overflow-hidden flex flex-col">
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-4 border-b border-border">
          <div>
            <h2 className="text-lg font-bold text-foreground flex items-center gap-2">
              <Search className="w-5 h-5 text-accent" />
              Identify
            </h2>
            <p className="text-xs text-secondary mt-0.5">
              {sceneIds
                ? `Identifying ${sceneIds.length} scene${sceneIds.length !== 1 ? "s" : ""}`
                : "Identifying all scenes"}
            </p>
          </div>
          <button onClick={onClose} className="text-muted hover:text-foreground">
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Body */}
        <div className="flex-1 overflow-y-auto px-5 py-4 space-y-5">
          {/* Sources */}
          <div>
            <h3 className="text-sm font-medium text-foreground mb-2">Sources (first match wins)</h3>
            <div className="space-y-1.5">
              {sources.map((source, i) => (
                <div
                  key={source.id}
                  className={`flex items-center gap-3 px-3 py-2.5 rounded-lg border transition-colors ${
                    source.enabled
                      ? "bg-card border-border"
                      : "bg-card/50 border-border/50 opacity-60"
                  }`}
                >
                  <div className="flex flex-col gap-0.5 flex-shrink-0">
                    <button
                      onClick={() => moveSource(i, "up")}
                      disabled={i === 0}
                      className="text-muted hover:text-foreground disabled:opacity-30"
                    >
                      <ChevronUp className="w-3 h-3" />
                    </button>
                    <button
                      onClick={() => moveSource(i, "down")}
                      disabled={i === sources.length - 1}
                      className="text-muted hover:text-foreground disabled:opacity-30"
                    >
                      <ChevronDown className="w-3 h-3" />
                    </button>
                  </div>
                  <label className="flex items-center gap-2.5 flex-1 cursor-pointer min-w-0">
                    <input
                      type="checkbox"
                      checked={source.enabled}
                      onChange={() => toggleSource(source.id)}
                      className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                    />
                    <div className="min-w-0">
                      <div className="text-sm font-medium text-foreground truncate">{source.name}</div>
                      <div className="text-xs text-muted capitalize">{source.type.replace("-", " ")}</div>
                    </div>
                  </label>
                </div>
              ))}
            </div>
            {sources.length === 0 && (
              <div className="text-sm text-muted text-center py-4">
                No sources available. Configure MetadataServer endpoints in Settings &gt; Metadata Providers.
              </div>
            )}
          </div>

          {/* Options */}
          <div>
            <button
              onClick={() => setShowOptions(!showOptions)}
              className="flex items-center gap-1.5 text-sm font-medium text-secondary hover:text-foreground"
            >
              {showOptions ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
              Options
            </button>
            {showOptions && (
              <div className="mt-3 space-y-2 pl-1">
                <label className="flex items-center gap-2 text-sm text-secondary">
                  <input
                    type="checkbox"
                    checked={setCoverImage}
                    onChange={(e) => setSetCoverImage(e.target.checked)}
                    className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                  />
                  Set cover image from scraper
                </label>
                <label className="flex items-center gap-2 text-sm text-secondary">
                  <input
                    type="checkbox"
                    checked={setOrganized}
                    onChange={(e) => setSetOrganized(e.target.checked)}
                    className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                  />
                  Mark identified scenes as organized
                </label>
                <label className="flex items-center gap-2 text-sm text-secondary">
                  <input
                    type="checkbox"
                    checked={skipMultipleMatches}
                    onChange={(e) => setSkipMultipleMatches(e.target.checked)}
                    className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                  />
                  Skip scenes with multiple matches
                </label>
                <label className="flex items-center gap-2 text-sm text-secondary">
                  <input
                    type="checkbox"
                    checked={skipSingleNamePerformers}
                    onChange={(e) => setSkipSingleNamePerformers(e.target.checked)}
                    className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                  />
                  Skip single-name performers
                </label>

                <div className="border-t border-border my-2 pt-2">
                  <span className="text-xs font-medium text-muted uppercase tracking-wide">Entity Creation</span>
                </div>
                <label className="flex items-center gap-2 text-sm text-secondary">
                  <input
                    type="checkbox"
                    checked={createPerformers}
                    onChange={(e) => setCreatePerformers(e.target.checked)}
                    className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                  />
                  Create new performers
                </label>
                <label className="flex items-center gap-2 text-sm text-secondary">
                  <input
                    type="checkbox"
                    checked={createStudios}
                    onChange={(e) => setCreateStudios(e.target.checked)}
                    className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                  />
                  Create new studios
                </label>
                <label className="flex items-center gap-2 text-sm text-secondary">
                  <input
                    type="checkbox"
                    checked={createTags}
                    onChange={(e) => setCreateTags(e.target.checked)}
                    className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                  />
                  Create new tags
                </label>
                <div className="flex items-start gap-2 mt-2 p-2 bg-accent/10 border border-accent/20 rounded-lg">
                  <Info className="w-4 h-4 text-accent mt-0.5 flex-shrink-0" />
                  <div className="text-xs text-secondary space-y-1">
                    <p>
                    Identified data will be merged with existing data by default. Fields that
                    already have values won't be overwritten.
                    </p>
                    <p>
                      Auto-apply duration and pHash thresholds are configured in Settings &gt; Metadata Providers &gt; Identify Defaults.
                    </p>
                  </div>
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Footer */}
        <div className="flex justify-end gap-2 px-5 py-4 border-t border-border">
          <button
            onClick={onClose}
            className="px-4 py-2 text-sm text-secondary hover:text-foreground transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={() => identifyMut.mutate()}
            disabled={identifyMut.isPending || sources.filter((s) => s.enabled).length === 0}
            className="inline-flex items-center gap-2 px-5 py-2 bg-accent hover:bg-accent-hover text-white rounded-lg font-medium disabled:opacity-50 transition-colors"
          >
            {identifyMut.isPending ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <Search className="w-4 h-4" />
            )}
            Identify
          </button>
        </div>
      </div>
    </div>
  );
}
