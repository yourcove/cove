import { useEffect, useState } from "react";
import { Download, Loader2, X } from "lucide-react";
import type { DownloadSelectionEntity, BatchDownloadOptions } from "../utils/batchDownloads";
import { normalizeBatchDownloadOptions } from "../utils/batchDownloads";
import { loadScrapeApplyPreferences, saveScrapeApplyPreferences } from "./videoScrapeUtils";

interface Props {
  open: boolean;
  entity: DownloadSelectionEntity;
  itemCount: number;
  initialOptions?: BatchDownloadOptions;
  isPending?: boolean;
  title?: string;
  description?: string;
  confirmLabel?: string;
  onClose: () => void;
  onConfirm: (options: BatchDownloadOptions) => void;
}

export function BatchDownloadOptionsDialog({
  open,
  entity,
  itemCount,
  initialOptions,
  isPending = false,
  title,
  description,
  confirmLabel = "Queue Download Job",
  onClose,
  onConfirm,
}: Props) {
  const [options, setOptions] = useState<BatchDownloadOptions>(() => normalizeBatchDownloadOptions(initialOptions));

  useEffect(() => {
    if (!open) {
      return;
    }

    setOptions(normalizeBatchDownloadOptions(initialOptions));
  }, [initialOptions, open]);

  if (!open) {
    return null;
  }

  const dialogTitle = title ?? `Batch Download ${itemCount} ${entity}${itemCount === 1 ? "" : "s"}`;
  const dialogDescription =
    description ?? "Queue one backend job for the selected items and choose any follow-up actions before it starts.";
  const generate = options.generate ?? {};
  const supportsMetadataScrape = entity !== "Gallery";

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4">
      <div className="w-full max-w-2xl overflow-hidden rounded-2xl border border-border bg-surface shadow-2xl">
        <div className="flex items-start justify-between border-b border-border px-5 py-4">
          <div>
            <h2 className="flex items-center gap-2 text-lg font-bold text-foreground">
              <Download className="h-5 w-5 text-accent" />
              {dialogTitle}
            </h2>
            <p className="mt-0.5 text-xs text-secondary">{dialogDescription}</p>
          </div>
          <button
            onClick={onClose}
            className="text-muted hover:text-foreground"
            aria-label="Close batch download options dialog"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="space-y-4 px-5 py-4">
          {supportsMetadataScrape ? (
            <label className="flex items-start gap-3 rounded-xl border border-border bg-card px-4 py-3 text-sm text-foreground">
              <input
                type="checkbox"
                checked={!!options.scrapeMetadata}
                onChange={(event) =>
                  setOptions((current) => ({
                    ...current,
                    scrapeMetadata: event.target.checked,
                    scrapeVideos: event.target.checked,
                  }))
                }
                className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
              />
              <span>
                <span className="block font-medium">Scrape metadata after download</span>
                <span className="mt-1 block text-xs text-secondary">
                  Apply the normal metadata scrape pass once each downloaded item is imported.
                </span>
              </span>
            </label>
          ) : null}

          {supportsMetadataScrape && options.scrapeMetadata ? (
            <div className="space-y-3 rounded-xl border border-border bg-card p-4">
              <div>
                <p className="text-sm font-medium text-foreground">Metadata creation</p>
                <p className="mt-1 text-xs text-secondary">
                  Reuse the same scrape apply preferences used by metadata review.
                </p>
              </div>
              <div className="grid gap-2 sm:grid-cols-2">
                <CheckboxOption
                  label="Create missing tags"
                  checked={!!options.createMissingTags}
                  onChange={(checked) => setOptions((current) => ({ ...current, createMissingTags: checked }))}
                />
                <CheckboxOption
                  label="Create missing performers"
                  checked={!!options.createMissingPerformers}
                  onChange={(checked) => setOptions((current) => ({ ...current, createMissingPerformers: checked }))}
                />
                <CheckboxOption
                  label="Create missing studio"
                  checked={!!options.createMissingStudio}
                  onChange={(checked) => setOptions((current) => ({ ...current, createMissingStudio: checked }))}
                />
                <CheckboxOption
                  label="Mark organized"
                  checked={!!options.markOrganized}
                  onChange={(checked) => setOptions((current) => ({ ...current, markOrganized: checked }))}
                />
              </div>
            </div>
          ) : null}

          <label className="flex items-start gap-3 rounded-xl border border-border bg-card px-4 py-3 text-sm text-foreground">
            <input
              type="checkbox"
              checked={!!options.allowDuplicateDownloads}
              onChange={(event) =>
                setOptions((current) => ({ ...current, allowDuplicateDownloads: event.target.checked }))
              }
              className="mt-0.5 h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
            />
            <span>
              <span className="block font-medium">Allow duplicate downloads</span>
              <span className="mt-1 block text-xs text-secondary">
                Leave this off to skip items whose URLs are already downloaded or already queued elsewhere in this
                batch.
              </span>
            </span>
          </label>

          <div className="space-y-3 rounded-xl border border-border bg-card p-4">
            <div>
              <p className="text-sm font-medium text-foreground">Generate after download</p>
              <p className="mt-1 text-xs text-secondary">
                Queue a follow-up generate pass after the download job completes.
              </p>
            </div>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxOption
                label="Covers"
                checked={!!generate.thumbnails}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, thumbnails: checked } }))
                }
              />
              <CheckboxOption
                label="Previews"
                checked={!!generate.previews}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, previews: checked } }))
                }
              />
              <CheckboxOption
                label="Sprites"
                checked={!!generate.sprites}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, sprites: checked } }))
                }
              />
              <CheckboxOption
                label="Video perceptual hashes"
                checked={!!generate.phashes}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, phashes: checked } }))
                }
              />
              <CheckboxOption
                label="MD5 checksums"
                checked={!!generate.md5}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, md5: checked } }))
                }
              />
              <CheckboxOption
                label="Image thumbnails"
                checked={!!generate.imageThumbnails}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, imageThumbnails: checked } }))
                }
              />
              <CheckboxOption
                label="Image perceptual hashes"
                checked={!!generate.imagePhashes}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, imagePhashes: checked } }))
                }
              />
              <CheckboxOption
                label="Audio perceptual hashes"
                checked={!!generate.audioPhashes}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, audioPhashes: checked } }))
                }
              />
              <CheckboxOption
                label="Text perceptual hashes"
                checked={!!generate.textPhashes}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, textPhashes: checked } }))
                }
              />
              <CheckboxOption
                label="Overwrite generated files"
                checked={!!generate.overwrite}
                onChange={(checked) =>
                  setOptions((current) => ({ ...current, generate: { ...current.generate, overwrite: checked } }))
                }
              />
            </div>
          </div>
        </div>

        <div className="flex items-center justify-end gap-2 border-t border-border px-5 py-4">
          <button onClick={onClose} className="rounded-xl px-4 py-2 text-sm text-secondary hover:text-foreground">
            Cancel
          </button>
          <button
            onClick={() => {
              const normalized = normalizeBatchDownloadOptions(options);
              const currentPreferences = loadScrapeApplyPreferences();
              saveScrapeApplyPreferences({
                ...currentPreferences,
                createMissingTags: !!normalized.createMissingTags,
                createMissingPerformers: !!normalized.createMissingPerformers,
                createMissingStudio: !!normalized.createMissingStudio,
                markOrganized: !!normalized.markOrganized,
              });
              onConfirm(normalized);
            }}
            disabled={isPending}
            className="inline-flex items-center gap-2 rounded-xl bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60"
          >
            {isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

function CheckboxOption({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
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
