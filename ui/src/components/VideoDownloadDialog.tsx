import { useEffect, useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Download, Link2, Loader2, Search, X } from "lucide-react";
import { videos, system } from "../api/client";
import type { DownloaderMatch, Video } from "../api/types";
import { sortDownloaderMatches } from "./videoScrapeUtils";

interface Props {
  open: boolean;
  onClose: () => void;
  onNavigate: (route: any) => void;
  video?: Pick<Video, "id" | "title" | "urls" | "files">;
}

function deriveVideoTitle(url: string, fallback?: string) {
  if (fallback?.trim()) {
    return fallback.trim();
  }

  try {
    const parsed = new URL(url);
    const lastSegment = parsed.pathname.split("/").filter(Boolean).at(-1);
    if (lastSegment) {
      return decodeURIComponent(lastSegment)
        .replace(/[._-]+/g, " ")
        .trim();
    }

    return parsed.hostname;
  } catch {
    return url.trim();
  }
}

export function VideoDownloadDialog({ open, onClose, onNavigate, video }: Props) {
  const queryClient = useQueryClient();
  const [url, setUrl] = useState("");
  const [matches, setMatches] = useState<DownloaderMatch[]>([]);
  const [selectedDownloaderId, setSelectedDownloaderId] = useState("");
  const [qualityId, setQualityId] = useState("");
  const [autoApplyMetadata, setAutoApplyMetadata] = useState(false);
  const [allowDuplicateDownload, setAllowDuplicateDownload] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const orderedMatches = useMemo(() => sortDownloaderMatches(matches), [matches]);

  useEffect(() => {
    if (!open) {
      return;
    }

    setUrl(video?.urls[0] ?? "");
    setMatches([]);
    setSelectedDownloaderId("");
    setQualityId("");
    setAutoApplyMetadata(!video);
    setAllowDuplicateDownload(false);
    setError(null);
  }, [open, video?.id, video?.urls]);

  const selectedMatch = useMemo(
    () => orderedMatches.find((match) => match.downloaderId === selectedDownloaderId) ?? null,
    [orderedMatches, selectedDownloaderId],
  );

  useEffect(() => {
    if (orderedMatches.length === 0) {
      return;
    }

    if (!selectedDownloaderId || !orderedMatches.some((match) => match.downloaderId === selectedDownloaderId)) {
      setSelectedDownloaderId(orderedMatches[0]?.downloaderId ?? "");
    }
  }, [orderedMatches, selectedDownloaderId]);

  useEffect(() => {
    if (!selectedMatch) {
      setQualityId("");
      return;
    }

    if (selectedMatch.qualityOptions.length === 0) {
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
      return results.filter((match) => match.supportedEntity.toLowerCase() === "video");
    },
    onSuccess: (results) => {
      const ordered = sortDownloaderMatches(results);
      setMatches(results);
      setSelectedDownloaderId(ordered[0]?.downloaderId ?? "");
      setQualityId(ordered[0]?.qualityOptions[0]?.id ?? "");
      setError(results.length === 0 ? "No video downloader matched that URL." : null);
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

      let videoId = video?.id;
      const normalizedUrl = selectedMatch.normalizedUrl || url.trim();

      if (!allowDuplicateDownload) {
        const preflight = await system.preflightDownload({
          url: normalizedUrl,
          entity: "Video",
          entityId: videoId,
        });

        if (preflight.isDuplicate) {
          throw new Error(preflight.duplicateReason || "This URL is already downloaded.");
        }
      }

      if (!videoId) {
        const createdVideo = await videos.create({
          title: deriveVideoTitle(normalizedUrl, selectedMatch.label),
          organized: false,
          urls: [normalizedUrl],
        });
        videoId = createdVideo.id;
      }

      if (queueDownload) {
        await system.startDownload({
          downloaderId: selectedMatch.downloaderId,
          url: normalizedUrl,
          entity: "Video",
          entityId: videoId,
          qualityId: qualityId || undefined,
          autoApplyMetadata,
          allowDuplicateDownload,
        });
      }

      return { videoId, queued: queueDownload };
    },
    onSuccess: ({ videoId, queued }) => {
      if (queued) {
        queryClient.invalidateQueries({ queryKey: ["jobs"] });
      }
      queryClient.invalidateQueries({ queryKey: ["videos"] });
      queryClient.invalidateQueries({ queryKey: ["video", videoId] });
      onClose();
      onNavigate({ page: "video", id: videoId });
    },
    onError: (mutationError: Error) => {
      setError(mutationError.message || "Failed to queue the download.");
    },
  });

  if (!open) {
    return null;
  }

  const title = video ? "Download Video Media" : "New Video From URL";
  const subtitle = video
    ? `Attach a downloader result to ${video.title || `Video ${video.id}`}.`
    : "Create a video from a source URL now and choose whether to queue the media download immediately or later.";

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
                  placeholder="https://example.com/watch/..."
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
                {matchMutation.isPending ? (
                  <Loader2 className="h-4 w-4 animate-spin" />
                ) : (
                  <Search className="h-4 w-4" />
                )}
                Find Downloaders
              </button>
            </div>
          </div>

          <div className="space-y-2">
            <label className="block text-sm font-medium text-foreground">Matches</label>
            {orderedMatches.length === 0 ? (
              <div className="rounded-xl border border-dashed border-border bg-card/50 px-4 py-5 text-sm text-muted">
                Match the URL to choose a video downloader.
              </div>
            ) : (
              <div className="space-y-2">
                {orderedMatches.map((match) => (
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
                      name="video-downloader-match"
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
            <div className="space-y-3">
              <label className="flex items-start gap-3 rounded-xl border border-border bg-card/60 px-4 py-3 text-sm text-foreground">
                <input
                  type="checkbox"
                  checked={autoApplyMetadata}
                  onChange={(event) => setAutoApplyMetadata(event.target.checked)}
                  className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
                />
                <span>
                  <span className="block font-medium">Auto-apply metadata after download</span>
                  <span className="mt-1 block text-xs text-secondary">
                    When the downloader exposes metadata, Cove will merge it into the video after the media import
                    finishes.
                  </span>
                </span>
              </label>
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
                    Leave this off to stop Cove from creating or queueing a video when this source URL already has
                    downloaded files.
                  </span>
                </span>
              </label>
            </div>
          ) : null}

          {error ? (
            <div className="rounded-xl border border-red-800/60 bg-red-950/30 px-3 py-2 text-sm text-red-300">
              {error}
            </div>
          ) : null}
        </div>

        <div className="flex items-center justify-between border-t border-border px-5 py-4">
          <div className="text-xs text-muted">
            {video
              ? "The video stays editable while the download job runs. Downloads are queued separately from exclusive sync jobs."
              : "Cove checks for duplicate downloaded URLs before it creates a new video or queues the download."}
          </div>
          <div className="flex items-center gap-2">
            <button onClick={onClose} className="rounded-xl px-4 py-2 text-sm text-secondary hover:text-foreground">
              Cancel
            </button>
            {!video ? (
              <button
                onClick={() => startDownloadMutation.mutate({ queueDownload: false })}
                disabled={!selectedMatch || startDownloadMutation.isPending || matchMutation.isPending}
                className="inline-flex items-center gap-2 rounded-xl border border-border bg-card px-4 py-2 text-sm font-medium text-foreground hover:border-accent disabled:opacity-60"
              >
                {startDownloadMutation.isPending ? (
                  <Loader2 className="h-4 w-4 animate-spin" />
                ) : (
                  <Link2 className="h-4 w-4" />
                )}
                Create Video Only
              </button>
            ) : null}
            <button
              onClick={() => startDownloadMutation.mutate({ queueDownload: true })}
              disabled={!selectedMatch || startDownloadMutation.isPending || matchMutation.isPending}
              className="inline-flex items-center gap-2 rounded-xl bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
            >
              {startDownloadMutation.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <Download className="h-4 w-4" />
              )}
              Queue Download
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
