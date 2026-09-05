import { useEffect, useId, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { Loader2 } from "lucide-react";
import type { DeleteEntityOptions } from "../api/types";
import { useOptionalAppConfig } from "../state/AppConfigContext";

interface Props {
  open: boolean;
  title: string;
  message: string;
  confirmLabel?: string;
  onConfirm: (options?: DeleteEntityOptions) => void | Promise<void>;
  onCancel: () => void;
  destructive?: boolean;
  isPending?: boolean;
  errorMessage?: string | null;
  /** Show a "Also delete file from disk" checkbox */
  showDeleteFile?: boolean;
  showDeleteGenerated?: boolean;
}

export function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel = "Delete",
  onConfirm,
  onCancel,
  destructive = true,
  isPending = false,
  errorMessage = null,
  showDeleteFile,
  showDeleteGenerated,
}: Props) {
  const appConfig = useOptionalAppConfig();
  const [deleteFile, setDeleteFile] = useState(false);
  const [deleteGenerated, setDeleteGenerated] = useState(false);
  const dialogRef = useRef<HTMLDivElement>(null);
  const cancelButtonRef = useRef<HTMLButtonElement>(null);
  const titleId = useId();

  useEffect(() => {
    if (!open) return;

    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const focusTimer = window.setTimeout(() => cancelButtonRef.current?.focus(), 0);
    return () => {
      window.clearTimeout(focusTimer);
      previousFocus?.focus();
    };
  }, [open]);

  useEffect(() => {
    if (!open) {
      return;
    }

    setDeleteFile(showDeleteFile ? (appConfig?.config?.ui.deleteFileDefault ?? false) : false);
    setDeleteGenerated(showDeleteGenerated ? (appConfig?.config?.deleteGeneratedDefault ?? false) : false);
  }, [
    appConfig?.config?.deleteGeneratedDefault,
    appConfig?.config?.ui.deleteFileDefault,
    open,
    showDeleteFile,
    showDeleteGenerated,
  ]);

  if (!open) return null;

  const resetOptions = () => {
    setDeleteFile(false);
    setDeleteGenerated(false);
  };

  const cancel = () => {
    onCancel();
    resetOptions();
  };

  const handleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    event.stopPropagation();
    if (event.key === "Escape") {
      event.preventDefault();
      if (!isPending) cancel();
      return;
    }

    if (event.key !== "Tab" || !dialogRef.current) return;
    const focusable = Array.from(
      dialogRef.current.querySelectorAll<HTMLElement>(
        "button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])",
      ),
    );
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div
        className="fixed inset-0 bg-black/60"
        onClick={() => {
          if (!isPending) cancel();
        }}
      />
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onKeyDown={handleKeyDown}
        className="relative bg-surface rounded-lg border border-border shadow-xl p-6 max-w-sm w-full mx-4"
      >
        <h3 id={titleId} className="text-lg font-semibold mb-2">
          {title}
        </h3>
        <p className="text-sm text-secondary mb-4">{message}</p>
        {showDeleteFile && (
          <label className="flex items-center gap-2 text-sm text-secondary cursor-pointer mb-4">
            <input
              type="checkbox"
              checked={deleteFile}
              onChange={(e) => setDeleteFile(e.target.checked)}
              className="rounded border-border bg-surface accent-accent"
            />
            Also delete file from disk
          </label>
        )}
        {showDeleteGenerated && (
          <label className="flex items-center gap-2 text-sm text-secondary cursor-pointer mb-4">
            <input
              type="checkbox"
              checked={deleteGenerated}
              onChange={(e) => setDeleteGenerated(e.target.checked)}
              className="rounded border-border bg-surface accent-accent"
            />
            Also delete generated files
          </label>
        )}
        {errorMessage ? (
          <div className="mb-4 rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200">
            {errorMessage}
          </div>
        ) : null}
        <div className="flex justify-end gap-3">
          <button
            ref={cancelButtonRef}
            onClick={cancel}
            disabled={isPending}
            className="px-4 py-2 text-sm text-secondary hover:text-white transition-colors disabled:cursor-not-allowed disabled:opacity-60"
          >
            Cancel
          </button>
          <button
            onClick={() => {
              const options =
                showDeleteFile || showDeleteGenerated
                  ? {
                      deleteFile: showDeleteFile ? deleteFile : false,
                      deleteGenerated: showDeleteGenerated ? deleteGenerated : false,
                    }
                  : undefined;
              void onConfirm(options);
            }}
            disabled={isPending}
            className={`px-4 py-2 text-sm rounded-md transition-colors ${
              destructive ? "bg-red-600 hover:bg-red-500 text-white" : "bg-accent hover:bg-accent-hover text-white"
            } disabled:cursor-not-allowed disabled:opacity-60`}
          >
            <span className="inline-flex items-center gap-2">
              {isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              {confirmLabel}
            </span>
          </button>
        </div>
      </div>
    </div>
  );
}
