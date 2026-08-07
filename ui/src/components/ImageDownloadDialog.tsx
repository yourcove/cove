import { useEffect, useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Download, Link2, Loader2, Search, X } from "lucide-react";
import { system } from "../api/client";
import type { DownloaderMatch, Image } from "../api/types";

interface Props {
  open: boolean;
  onClose: () => void;
  onNavigate: (route: any) => void;
  image: Pick<Image, "id" | "title" | "urls" | "files">;
}

export function ImageDownloadDialog({ open, onClose, onNavigate, image }: Props) {
  const queryClient = useQueryClient();
  const [url, setUrl] = useState("");
  const [matches, setMatches] = useState<DownloaderMatch[]>([]);
  const [selectedDownloaderId, setSelectedDownloaderId] = useState("");
  const [qualityId, setQualityId] = useState("");
  const [allowDuplicateDownload, setAllowDuplicateDownload] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) {
      return;
    }

    setUrl(image?.urls[0] ?? "");
    setMatches([]);
    setSelectedDownloaderId("");
    setQualityId("");
    setAllowDuplicateDownload(false);
    setError(null);
  }, [open, image?.id, image?.urls]);

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
      if (!value) {
        throw new Error("Enter a URL to match.");
      }

      const results = await system.matchDownloaders({ url: value });
      return results.filter((match) => match.supportedEntity.toLowerCase() === "image");
    },
    onSuccess: (results) => {
      setMatches(results);
      setSelectedDownloaderId(results[0]?.downloaderId ?? "");
      setQualityId(results[0]?.qualityOptions[0]?.id ?? "");
      setError(results.length === 0 ? "No image downloader matched that URL." : null);
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
    mutationFn: async ({ queueDownload }: { queueDownload: boolean }) => {
      if (!selectedMatch) {
        throw new Error("Select a downloader match first.");
      }

      const imageId = image.id;
      const normalizedUrl = selectedMatch.normalizedUrl || url.trim();

      if (!allowDuplicateDownload) {
        const preflight = await system.preflightDownload({
          url: normalizedUrl,
          entity: "Image",
          entityId: imageId,
        });

        if (preflight.isDuplicate) {
          throw new Error(preflight.duplicateReason || "This URL is already downloaded.");
        }
      }

      if (queueDownload) {
        await system.startDownload({
          downloaderId: selectedMatch.downloaderId,
          url: normalizedUrl,
          entity: "Image",
          entityId: imageId,
          qualityId: qualityId || undefined,
          allowDuplicateDownload,
        });
      }

      return { imageId, queued: queueDownload };
    },
    onSuccess: ({ imageId, queued }) => {
      if (queued) {
        queryClient.invalidateQueries({ queryKey: ["jobs"] });
      }
      queryClient.invalidateQueries({ queryKey: ["images"] });
      queryClient.invalidateQueries({ queryKey: ["image", imageId] });
      onClose();
      onNavigate({ page: "image", id: imageId });
    },
    onError: (mutationError: Error) => {
      setError(mutationError.message || "Failed to queue the download.");
    },
  });

  if (!open) {
    return null;
  }

  const title = "Download Image Media";
  const subtitle = `Attach a downloader result to ${image.title || `Image ${image.id}`}.`;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4">
      <div className="w-full max-w-2xl overflow-hidden rounded-2xl border border-border bg-surface shadow-2xl">
        <div className="flex items-start justify-between border-b border-border px-5 py-4">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-foreground">
              <Download className="h-5 w-5 text-accent" />
              {title}
            </h2>
            <p className="mt-0.5 text-xs text-secondary">{subtitle}</p>
          </div>
          <button onClick={onClose} className="text-muted hover:text-foreground" aria-label="Close download dialog">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="space-y-5 px-5 py-4">
          <div className="space-y-2">
            <label className="block text-sm font-medium text-foreground">Source URL</label>
            <div className="flex gap-2">
              <div className="flex flex-1 items-center gap-2 rounded-xl border border-border bg-card px-3 py-2">
                <Link2 className="h-4 w-4 text-muted" />
                <input
                  value={url}
                  onChange={(event) => setUrl(event.target.value)}
                  placeholder="https://example.com/image/..."
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
                Match the URL to choose an image downloader.
              </div>
            ) : (
              <div className="space-y-2">
                {matches.map((match) => (
                  <label
                    key={`${match.downloaderId}:${match.normalizedUrl}`}
                    className={`flex cursor-pointer items-start gap-3 rounded-xl border px-3 py-3 transition-colors ${
                      selectedDownloaderId === match.downloaderId
                        ? "border-accent bg-accent/10"
                        : "border-border bg-card hover:border-accent/40"
                    }`}
                  >
                    <input
                      type="radio"
                      name="image-downloader-match"
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
              <select
                value={qualityId}
                onChange={(event) => setQualityId(event.target.value)}
                className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground outline-none"
              >
                {selectedMatch.qualityOptions.map((option) => (
                  <option key={option.id} value={option.id}>
                    {option.label}
                    {option.description ? ` — ${option.description}` : ""}
                  </option>
                ))}
              </select>
            </div>
          ) : null}

          {selectedMatch ? (
            <label className="flex items-start gap-3 rounded-xl border border-border bg-card/60 px-4 py-3 text-sm text-foreground">
              <input
                type="checkbox"
                checked={allowDuplicateDownload}
                onChange={(event) => setAllowDuplicateDownload(event.target.checked)}
                className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
              />
              <span>
                <span className="block font-medium">Allow duplicate download</span>
                <span className="mt-1 block text-xs text-secondary">
                  Leave this off to stop Cove from creating or queueing an image when this source URL already has downloaded files.
                </span>
              </span>
            </label>
          ) : null}

          {error ? (
            <div className="rounded-xl border border-red-800/60 bg-red-950/30 px-3 py-2 text-sm text-red-300">
              {error}
            </div>
          ) : null}
        </div>

        <div className="flex items-center justify-between border-t border-border px-5 py-4">
          <div className="text-xs text-muted">
            The image stays editable while the download job runs.
          </div>
          <div className="flex items-center gap-2">
            <button onClick={onClose} className="rounded-xl px-4 py-2 text-sm text-secondary hover:text-foreground">
              Cancel
            </button>
            <button
              onClick={() => startDownloadMutation.mutate({ queueDownload: true })}
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
