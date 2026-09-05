import { Clock3, Download, FilePlus2, Link2, Pencil } from "lucide-react";
import { Field } from "./EditModal";
import type { UrlDownloadMode } from "../utils/createFromUrlDownload";

export type CreateSourceMode = "metadata" | "file" | "url";

export function FileBackedCreateSource({
  mode,
  onModeChange,
  filePath,
  onFilePathChange,
  url,
  onUrlChange,
  urlDownloadMode,
  onUrlDownloadModeChange,
  scrapeMetadata = false,
  onScrapeMetadataChange,
  noDownloaderFound = false,
  onCreateWithoutDownload,
  onDismissNoDownloader,
  modes = ["metadata", "file"],
  filePlaceholder = "C:\\Media\\item.mp4",
  urlPlaceholder = "https://example.com/media",
}: {
  mode: CreateSourceMode;
  onModeChange: (mode: CreateSourceMode) => void;
  filePath: string;
  onFilePathChange: (value: string) => void;
  url?: string;
  onUrlChange?: (value: string) => void;
  urlDownloadMode?: UrlDownloadMode;
  onUrlDownloadModeChange?: (value: UrlDownloadMode) => void;
  scrapeMetadata?: boolean;
  onScrapeMetadataChange?: (value: boolean) => void;
  noDownloaderFound?: boolean;
  onCreateWithoutDownload?: () => void;
  onDismissNoDownloader?: () => void;
  modes?: CreateSourceMode[];
  filePlaceholder?: string;
  urlPlaceholder?: string;
}) {
  return (
    <div className="mb-5 rounded-2xl border border-border bg-card/50 p-4 sm:p-5">
      {modes.length > 1 ? (
        <div>
          <div className="text-[11px] font-semibold uppercase tracking-[0.18em] text-muted">Create From</div>
          <div
            className="mt-2 inline-flex rounded-lg border border-border bg-surface p-1"
            role="group"
            aria-label="Create source"
          >
            {modes.includes("metadata") ? (
              <button
                type="button"
                onClick={() => onModeChange("metadata")}
                className={`inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium ${mode === "metadata" ? "bg-accent text-white" : "text-secondary hover:text-foreground"}`}
              >
                <Pencil className="h-3.5 w-3.5" />
                Metadata
              </button>
            ) : null}
            {modes.includes("file") ? (
              <button
                type="button"
                onClick={() => onModeChange("file")}
                className={`inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium ${mode === "file" ? "bg-accent text-white" : "text-secondary hover:text-foreground"}`}
              >
                <FilePlus2 className="h-3.5 w-3.5" />
                File
              </button>
            ) : null}
            {modes.includes("url") ? (
              <button
                type="button"
                onClick={() => onModeChange("url")}
                className={`inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium ${mode === "url" ? "bg-accent text-white" : "text-secondary hover:text-foreground"}`}
              >
                <Link2 className="h-3.5 w-3.5" />
                URL
              </button>
            ) : null}
          </div>
        </div>
      ) : null}

      {mode === "file" ? (
        <div className={modes.length > 1 ? "mt-4" : ""}>
          <Field label="File path">
            <input
              type="text"
              value={filePath}
              onChange={(event) => onFilePathChange(event.target.value)}
              placeholder={filePlaceholder}
              className="w-full rounded border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
            />
          </Field>
        </div>
      ) : mode === "url" ? (
        <div className={modes.length > 1 ? "mt-4" : ""}>
          <Field label="URL">
            <input
              type="url"
              value={url ?? ""}
              onChange={(event) => onUrlChange?.(event.target.value)}
              placeholder={urlPlaceholder}
              className="w-full rounded border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
            />
          </Field>
          {onUrlDownloadModeChange ? (
            <Field label="Download">
              <div
                className="inline-flex rounded-lg border border-border bg-surface p-1"
                role="group"
                aria-label="Download timing"
              >
                <button
                  type="button"
                  onClick={() => onUrlDownloadModeChange("now")}
                  className={`inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium ${urlDownloadMode !== "later" ? "bg-accent text-white" : "text-secondary hover:text-foreground"}`}
                >
                  <Download className="h-3.5 w-3.5" />
                  Now
                </button>
                <button
                  type="button"
                  onClick={() => onUrlDownloadModeChange("later")}
                  className={`inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-xs font-medium ${urlDownloadMode === "later" ? "bg-accent text-white" : "text-secondary hover:text-foreground"}`}
                >
                  <Clock3 className="h-3.5 w-3.5" />
                  Later
                </button>
              </div>
              {urlDownloadMode !== "later" && onScrapeMetadataChange ? (
                <label className="ml-3 inline-flex items-center gap-2 text-xs text-secondary">
                  <input
                    type="checkbox"
                    checked={scrapeMetadata}
                    onChange={(event) => onScrapeMetadataChange(event.target.checked)}
                    className="rounded border-border bg-card"
                  />
                  Scrape/store metadata
                </label>
              ) : null}
            </Field>
          ) : null}
          {noDownloaderFound ? (
            <div className="rounded-md border border-amber-500/50 bg-amber-500/10 p-3 text-sm text-amber-100">
              <div className="font-medium">No downloader found for this URL.</div>
              <div className="mt-3 flex flex-wrap gap-2">
                {onCreateWithoutDownload ? (
                  <button
                    type="button"
                    onClick={onCreateWithoutDownload}
                    className="rounded bg-amber-500 px-3 py-1.5 text-xs font-medium text-black hover:bg-amber-400"
                  >
                    Create Without Download
                  </button>
                ) : null}
                {onDismissNoDownloader ? (
                  <button
                    type="button"
                    onClick={onDismissNoDownloader}
                    className="rounded border border-amber-300/50 px-3 py-1.5 text-xs font-medium text-amber-100 hover:bg-amber-500/20"
                  >
                    Edit URL
                  </button>
                ) : null}
              </div>
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
