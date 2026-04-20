import { useState } from "react";

interface Props {
  open: boolean;
  title: string;
  message: string;
  confirmLabel?: string;
  onConfirm: (options?: { deleteFile?: boolean }) => void;
  onCancel: () => void;
  destructive?: boolean;
  /** Show a "Also delete file from disk" checkbox */
  showDeleteFile?: boolean;
}

export function ConfirmDialog({ open, title, message, confirmLabel = "Delete", onConfirm, onCancel, destructive = true, showDeleteFile }: Props) {
  const [deleteFile, setDeleteFile] = useState(false);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/60" onClick={onCancel} />
      <div className="relative bg-surface rounded-lg border border-border shadow-xl p-6 max-w-sm w-full mx-4">
        <h3 className="text-lg font-semibold mb-2">{title}</h3>
        <p className="text-sm text-secondary mb-4">{message}</p>
        {showDeleteFile && (
          <label className="flex items-center gap-2 text-sm text-secondary cursor-pointer mb-4">
            <input type="checkbox" checked={deleteFile} onChange={(e) => setDeleteFile(e.target.checked)} className="rounded border-border bg-surface accent-accent" />
            Also delete file from disk
          </label>
        )}
        <div className="flex justify-end gap-3">
          <button
            onClick={() => { onCancel(); setDeleteFile(false); }}
            className="px-4 py-2 text-sm text-secondary hover:text-white transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={() => { onConfirm(showDeleteFile ? { deleteFile } : undefined); setDeleteFile(false); }}
            className={`px-4 py-2 text-sm rounded-md transition-colors ${
              destructive
                ? "bg-red-600 hover:bg-red-500 text-white"
                : "bg-accent hover:bg-accent-hover text-white"
            }`}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
