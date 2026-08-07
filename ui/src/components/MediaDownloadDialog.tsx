import { useEffect, useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Download, Link2, Loader2, Search, X } from "lucide-react";
import { system } from "../api/client";
import type { DownloaderMatch } from "../api/types";
import { loadScrapeApplyPreferences, saveScrapeApplyPreferences } from "./videoScrapeUtils";
import { useFileBackedCreatePreferences } from "../hooks/useFileBackedCreatePreferences";
import type { DownloadEntityName } from "../utils/createFromUrlDownload";

type DownloadableMediaEntity = Extract<DownloadEntityName, "Audio" | "Image" | "Text">;

interface Props {
  open: boolean;
  onClose: () => void;
  onNavigate: (route: any) => void;
  entity: DownloadableMediaEntity;
  item: { id: number; title?: string; urls: string[]; files: unknown[] };
  listQueryKey: string;
  detailQueryKey: string;
  routePage: string;
}

export function MediaDownloadDialog({ open, onClose, onNavigate, entity, item, listQueryKey, detailQueryKey, routePage }: Props) {
  const queryClient = useQueryClient();
  const { scrapeMetadata, setScrapeMetadata } = useFileBackedCreatePreferences(entity);
  const [url, setUrl] = useState("");
  const [matches, setMatches] = useState<DownloaderMatch[]>([]);
  const [selectedDownloaderId, setSelectedDownloaderId] = useState("");
  const [qualityId, setQualityId] = useState("");
  const [allowDuplicateDownload, setAllowDuplicateDownload] = useState(false);
  const [createMissingTags, setCreateMissingTags] = useState(false);
  const [createMissingPerformers, setCreateMissingPerformers] = useState(false);
  const [createMissingStudio, setCreateMissingStudio] = useState(false);
  const [markOrganized, setMarkOrganized] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setUrl(item.urls[0] ?? "");
    setMatches([]);
    setSelectedDownloaderId("");
    setQualityId("");
    setAllowDuplicateDownload(false);
    const preferences = loadScrapeApplyPreferences();
    setCreateMissingTags(!!preferences.createMissingTags);
    setCreateMissingPerformers(!!preferences.createMissingPerformers);
    setCreateMissingStudio(!!preferences.createMissingStudio);
    setMarkOrganized(!!preferences.markOrganized);
    setError(null);
  }, [item.id, item.urls, open]);

  const selectedMatch = useMemo(
    () => matches.find((match) => match.downloaderId === selectedDownloaderId) ?? null,
    [matches, selectedDownloaderId],
  );

  useEffect(() => {
    if (!selectedMatch || selectedMatch.qualityOptions.length === 0) {
      setQualityId("");
      return;
    }
    if (!selectedMatch.qualityOptions.some((option) => option.id === qualityId)) {
      setQualityId(selectedMatch.qualityOptions[0]?.id ?? "");
    }
  }, [qualityId, selectedMatch]);

  const matchMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async () => {
      const value = url.trim();
      if (!value) throw new Error("Enter a URL to match.");
      const results = await system.matchDownloaders({ url: value });
      return results.filter((match) => match.supportedEntity.toLowerCase() === entity.toLowerCase());
    },
    onSuccess: (results) => {
      setMatches(results);
      setSelectedDownloaderId(results[0]?.downloaderId ?? "");
      setQualityId(results[0]?.qualityOptions[0]?.id ?? "");
      setError(results.length === 0 ? `No ${entity.toLowerCase()} downloader matched that URL.` : null);
    },
    onError: (mutationError: Error) => {
      setMatches([]);
      setSelectedDownloaderId("");
      setQualityId("");
      setError(mutationError.message || "Failed to match downloaders.");
    },
  });

  const startDownloadMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async () => {
      if (!selectedMatch) throw new Error("Select a downloader match first.");
      const normalizedUrl = selectedMatch.normalizedUrl || url.trim();
      if (!allowDuplicateDownload) {
        const preflight = await system.preflightDownload({
          url: normalizedUrl,
          entity,
          entityId: item.id,
        });
        if (preflight.isDuplicate) {
          throw new Error(preflight.duplicateReason || "This URL is already downloaded.");
        }
      }

      if (scrapeMetadata) {
        const preferences = loadScrapeApplyPreferences();
        saveScrapeApplyPreferences({
          ...preferences,
          createMissingTags,
          createMissingPerformers,
          createMissingStudio,
          markOrganized,
        });
      }

      await system.startDownload({
        downloaderId: selectedMatch.downloaderId,
        url: normalizedUrl,
        entity,
        entityId: item.id,
        qualityId: qualityId || undefined,
        allowDuplicateDownload,
        autoApplyMetadata: scrapeMetadata,
        createMissingTags: scrapeMetadata ? createMissingTags : false,
        createMissingPerformers: scrapeMetadata ? createMissingPerformers : false,
        createMissingStudio: scrapeMetadata ? createMissingStudio : false,
        markOrganized: scrapeMetadata ? markOrganized : false,
      });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["jobs"] });
      queryClient.invalidateQueries({ queryKey: [listQueryKey] });
      queryClient.invalidateQueries({ queryKey: [detailQueryKey, item.id] });
      onClose();
      onNavigate({ page: routePage, id: item.id });
    },
    onError: (mutationError: Error) => {
      setError(mutationError.message || "Failed to queue the download.");
    },
  });

  if (!open) return null;

  const label = item.title || `${entity} ${item.id}`;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4">
      <div className="flex max-h-[90vh] w-full max-w-2xl flex-col overflow-hidden rounded-2xl border border-border bg-surface shadow-2xl">
        <div className="flex items-start justify-between border-b border-border px-5 py-4">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-foreground">
              <Download className="h-5 w-5 text-accent" />
              Download {entity} Media
            </h2>
            <p className="mt-0.5 text-xs text-secondary">Attach a downloader result to {label}.</p>
          </div>
          <button onClick={onClose} className="text-muted hover:text-foreground" aria-label="Close download dialog">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="space-y-5 overflow-y-auto px-5 py-4">
          <div className="space-y-2">
            <label className="block text-sm font-medium text-foreground">Source URL</label>
            <div className="flex gap-2">
              <div className="flex flex-1 items-center gap-2 rounded-xl border border-border bg-card px-3 py-2">
                <Link2 className="h-4 w-4 text-muted" />
                <input
                  value={url}
                  onChange={(event) => setUrl(event.target.value)}
                  placeholder="https://example.com/..."
                  className="min-w-0 flex-1 bg-transparent text-sm text-foreground outline-none"
                />
              </div>
              <button
                onClick={() => {
                  setError(null);
                  matchMutation.mutate();
                }}
                disabled={matchMutation.isPending || startDownloadMutation.isPending}
                className="inline-flex items-center gap-2 rounded-xl border border-border bg-card px-4 py-2 text-sm font-medium text-foreground hover:border-accent disabled:opacity-60"
              >
                {matchMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
                Find Downloaders
              </button>
            </div>
          </div>

          <div className="space-y-2">
            <label className="block text-sm font-medium text-foreground">Matches</label>
            {matches.length === 0 ? (
              <div className="rounded-xl border border-dashed border-border bg-card/50 px-4 py-5 text-sm text-muted">
                Match the URL to choose a downloader.
              </div>
            ) : (
              <div className="space-y-2">
                {matches.map((match) => (
                  <label
                    key={`${match.downloaderId}:${match.normalizedUrl}`}
                    className={`flex cursor-pointer items-start gap-3 rounded-xl border px-3 py-3 transition-colors ${selectedDownloaderId === match.downloaderId ? "border-accent bg-accent/10" : "border-border bg-card hover:border-accent/40"}`}
                  >
                    <input
                      type="radio"
                      name="media-downloader-match"
                      checked={selectedDownloaderId === match.downloaderId}
                      onChange={() => setSelectedDownloaderId(match.downloaderId)}
                      className="mt-0.5 h-4 w-4 border-border bg-card text-accent focus:ring-0"
                    />
                    <div className="min-w-0 flex-1">
                      <div className="text-sm font-medium text-foreground">{match.label || match.downloaderName}</div>
                      <div className="text-xs text-secondary">{match.downloaderName}</div>
                      <div className="mt-1 truncate text-xs text-muted">{match.normalizedUrl}</div>
                    </div>
                  </label>
                ))}
              </div>
            )}
          </div>

          {selectedMatch?.qualityOptions.length ? (
            <div className="space-y-2">
              <label className="block text-sm font-medium text-foreground">Quality</label>
              <select value={qualityId} onChange={(event) => setQualityId(event.target.value)} className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground outline-none">
                {selectedMatch.qualityOptions.map((option) => (
                  <option key={option.id} value={option.id}>{option.label}{option.description ? ` - ${option.description}` : ""}</option>
                ))}
              </select>
            </div>
          ) : null}

          {selectedMatch ? (
            <div className="space-y-2">
              <label className="flex items-start gap-3 rounded-xl border border-border bg-card/60 px-4 py-3 text-sm text-foreground">
                <input type="checkbox" checked={scrapeMetadata} onChange={(event) => setScrapeMetadata(event.target.checked)} className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0" />
                <span className="block font-medium">Scrape metadata after download</span>
              </label>
              {scrapeMetadata ? (
                <div className="space-y-3 rounded-xl border border-border bg-card p-4">
                  <div>
                    <p className="text-sm font-medium text-foreground">Metadata creation</p>
                    <p className="mt-1 text-xs text-secondary">Reuse the same scrape apply preferences used by metadata review.</p>
                  </div>
                  <div className="grid gap-2 sm:grid-cols-2">
                    <CheckboxOption label="Create missing tags" checked={createMissingTags} onChange={setCreateMissingTags} />
                    <CheckboxOption label="Create missing performers" checked={createMissingPerformers} onChange={setCreateMissingPerformers} />
                    <CheckboxOption label="Create missing studio" checked={createMissingStudio} onChange={setCreateMissingStudio} />
                    <CheckboxOption label="Mark organized" checked={markOrganized} onChange={setMarkOrganized} />
                  </div>
                </div>
              ) : null}
              <label className="flex items-start gap-3 rounded-xl border border-border bg-card/60 px-4 py-3 text-sm text-foreground">
                <input type="checkbox" checked={allowDuplicateDownload} onChange={(event) => setAllowDuplicateDownload(event.target.checked)} className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0" />
                <span className="block font-medium">Allow duplicate download</span>
              </label>
            </div>
          ) : null}

          {error ? <div className="rounded-xl border border-red-800/60 bg-red-950/30 px-3 py-2 text-sm text-red-300">{error}</div> : null}
        </div>

        <div className="flex items-center justify-between border-t border-border px-5 py-4">
          <div className="text-xs text-muted">The item stays editable while the download job runs.</div>
          <div className="flex items-center gap-2">
            <button onClick={onClose} className="rounded-xl px-4 py-2 text-sm text-secondary hover:text-foreground">Cancel</button>
            <button
              onClick={() => startDownloadMutation.mutate()}
              disabled={!selectedMatch || startDownloadMutation.isPending || matchMutation.isPending}
              className="inline-flex items-center gap-2 rounded-xl bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
            >
              {startDownloadMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
              Queue Download
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function CheckboxOption({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <label className="flex items-start gap-3 rounded-xl border border-border/60 bg-surface/60 px-3 py-2 text-sm text-foreground">
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
      />
      <span>{label}</span>
    </label>
  );
}
