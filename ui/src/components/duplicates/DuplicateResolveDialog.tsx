import { useEffect, useState } from "react";
import { AlertTriangle, GitMerge, Loader2, Trash2 } from "lucide-react";
import { formatFileSize } from "../shared";
import { DuplicateDialog } from "./DuplicateDialog";
import type { ResolutionPreferences } from "./duplicateModel";

export interface ResolveSummary {
  groupCount: number;
  videoCount: number;
  bytes: number;
}

/**
 * Confirms a resolution and lets the person adjust how it happens. The chosen options are remembered, so a
 * reviewer who resolves group by group is asked once rather than for every group.
 */
export function DuplicateResolveDialog({
  open,
  summary,
  preferences,
  canDeleteFiles,
  canMerge,
  isPending,
  error,
  scope,
  onChange,
  onConfirm,
  onClose,
}: {
  open: boolean;
  summary: ResolveSummary;
  preferences: ResolutionPreferences;
  canDeleteFiles: boolean;
  canMerge: boolean;
  isPending: boolean;
  error?: string | null;
  scope: "group" | "all";
  onChange: (next: ResolutionPreferences) => void;
  onConfirm: () => void;
  onClose: () => void;
}) {
  const [acknowledged, setAcknowledged] = useState(false);
  useEffect(() => {
    if (open) setAcknowledged(false);
  }, [open]);
  const update = (patch: Partial<ResolutionPreferences>) => onChange({ ...preferences, ...patch });
  const deleteFiles = preferences.deleteFiles && canDeleteFiles;
  const needsAcknowledgement = deleteFiles && scope === "all";
  const merge = preferences.action === "merge" && canMerge;
  const copies = `${summary.videoCount.toLocaleString()} ${summary.videoCount === 1 ? "copy" : "copies"}`;

  return (
    <DuplicateDialog
      open={open}
      onClose={onClose}
      dismissible={!isPending}
      size="sm"
      title={scope === "all" ? `Resolve ${summary.groupCount.toLocaleString()} groups` : "Resolve this group"}
      subtitle={`${copies} will be removed from Cove${deleteFiles ? " and deleted from disk" : ""}.`}
      footer={
        <>
          <button
            type="button"
            onClick={onClose}
            disabled={isPending}
            className="px-3 py-2 text-sm text-secondary hover:text-foreground disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="button"
            data-autofocus
            onClick={onConfirm}
            disabled={isPending || (needsAcknowledgement && !acknowledged) || summary.videoCount === 0}
            className={`inline-flex items-center gap-2 rounded-md px-4 py-2 text-sm font-semibold text-white disabled:cursor-not-allowed disabled:opacity-50 ${
              deleteFiles ? "bg-red-600 hover:bg-red-500" : "bg-accent hover:bg-accent-hover"
            }`}
          >
            {isPending ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : merge ? (
              <GitMerge className="h-4 w-4" />
            ) : (
              <Trash2 className="h-4 w-4" />
            )}
            {merge ? "Merge & remove" : "Remove"} {copies}
          </button>
        </>
      }
    >
      <div className="space-y-4 text-sm">
        <fieldset className="space-y-2">
          <legend className="mb-1 font-medium text-foreground">Before removing</legend>
          <OptionCard
            checked={merge}
            disabled={!canMerge}
            onSelect={() => update({ action: "merge" })}
            title="Merge metadata into the kept copy"
            description="Tags, performers, galleries, groups, links, remote IDs, ratings, favorites, play counts and your markers carry over. Empty fields are filled; nothing on the kept copy is overwritten."
            recommended
          />
          <OptionCard
            checked={!merge}
            onSelect={() => update({ action: "remove" })}
            title="Just remove the other copies"
            description="Their metadata and watch history are discarded."
          />
        </fieldset>

        <fieldset className="space-y-2">
          <legend className="mb-1 font-medium text-foreground">Files</legend>
          {canDeleteFiles ? (
            <label className="flex cursor-pointer items-start gap-2.5 rounded-lg border border-border bg-card/60 px-3 py-2.5">
              <input
                type="checkbox"
                checked={preferences.deleteFiles}
                onChange={(event) => update({ deleteFiles: event.target.checked })}
                className="mt-0.5 accent-red-500"
              />
              <span>
                <span className="block text-foreground">Delete the files from disk</span>
                <span className="block text-xs text-muted">
                  {deleteFiles
                    ? `Frees ${formatFileSize(summary.bytes)}. This cannot be undone.`
                    : "Files stay on disk. Unless they are moved or excluded, the next scan will add them back."}
                </span>
              </span>
            </label>
          ) : (
            <p className="text-xs text-muted">Files stay on disk. You don't have permission to delete video files.</p>
          )}
          <label className="flex cursor-pointer items-center gap-2.5 px-1 text-secondary">
            <input
              type="checkbox"
              checked={preferences.deleteGenerated}
              onChange={(event) => update({ deleteGenerated: event.target.checked })}
              className="accent-accent"
            />
            Delete generated previews, sprites and thumbnails
          </label>
        </fieldset>

        {scope === "group" ? (
          <label className="flex cursor-pointer items-center gap-2.5 px-1 text-secondary">
            <input
              type="checkbox"
              checked={!preferences.confirmEachGroup}
              onChange={(event) => update({ confirmEachGroup: !event.target.checked })}
              className="accent-accent"
            />
            Don't ask again — resolve groups with these options straight away
          </label>
        ) : null}

        {needsAcknowledgement ? (
          <label className="flex cursor-pointer items-start gap-2.5 rounded-lg border border-red-800 bg-red-950/40 px-3 py-2.5 text-red-100">
            <input
              type="checkbox"
              checked={acknowledged}
              onChange={(event) => setAcknowledged(event.target.checked)}
              className="mt-0.5 accent-red-500"
            />
            <span className="flex items-start gap-1.5">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />I have reviewed the keepers and understand {copies}{" "}
              will be permanently deleted from disk.
            </span>
          </label>
        ) : null}

        {error ? (
          <div className="rounded-md border border-red-800 bg-red-950/60 px-3 py-2 text-red-200">{error}</div>
        ) : null}
      </div>
    </DuplicateDialog>
  );
}

function OptionCard({
  checked,
  disabled,
  onSelect,
  title,
  description,
  recommended,
}: {
  checked: boolean;
  disabled?: boolean;
  onSelect: () => void;
  title: string;
  description: string;
  recommended?: boolean;
}) {
  return (
    <label
      className={`flex cursor-pointer items-start gap-2.5 rounded-lg border px-3 py-2.5 ${
        checked ? "border-accent bg-accent/10" : "border-border bg-card/60 hover:border-accent/50"
      } ${disabled ? "cursor-not-allowed opacity-50" : ""}`}
    >
      <input type="radio" checked={checked} disabled={disabled} onChange={onSelect} className="mt-0.5 accent-accent" />
      <span>
        <span className="flex items-center gap-2 text-foreground">
          {title}
          {recommended ? (
            <span className="rounded-full bg-accent/20 px-1.5 text-[10px] font-semibold uppercase text-accent">
              Recommended
            </span>
          ) : null}
        </span>
        <span className="block text-xs text-muted">{description}</span>
      </span>
    </label>
  );
}
