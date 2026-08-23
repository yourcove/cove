import { useCallback, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Edit, Loader2, Play, Trash2 } from "lucide-react";
import type { DeleteEntityOptions } from "../api/types";
import { images } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canWriteEntity } from "../auth/visibility";
import { BulkEditDialog, IMAGE_BULK_FIELDS } from "./BulkEditDialog";
import { ConfirmDialog } from "./ConfirmDialog";
import { ExtensionSelectionActions } from "./ExtensionSelectionActions";
import { runSequentialBatches } from "../utils/sequentialBatches";

const actionClass = "flex items-center gap-1 px-2 py-0.5 rounded text-xs";
const IMAGE_DELETE_BATCH_SIZE = 50;

interface ImageDeleteRun {
  ids: number[];
  options?: DeleteEntityOptions;
}

export interface ImageSelectionActionsProps {
  selectedIds: Set<number>;
  /** Clear the selection — called after any action that consumes it. */
  onSelectNone: () => void;
  /**
   * Open the selection in a viewer. Optional because the viewer is the owning list's — it is scoped to that
   * list's items and paging — so a list without one simply doesn't offer Play.
   */
  onPlay?: () => void;
  /** React Query key to invalidate after a bulk mutation, so the owning list refetches. */
  queryKey?: string;
}

/**
 * The bulk actions offered for a multi-selection of images: play, edit, whatever extensions contribute, and
 * delete — dialogs included. Shared so the native images page and any extension list (the recommendations feed)
 * offer the same actions and keep doing so as actions are added.
 */
export function ImageSelectionActions({ selectedIds, onSelectNone, onPlay, queryKey = "images" }: ImageSelectionActionsProps) {
  const { hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const canWrite = canWriteEntity("image", hasPermission);
  const canDelete = canDeleteEntity("image", hasPermission);

  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [deleteRun, setDeleteRun] = useState<ImageDeleteRun | null>(null);
  const [deleteProgress, setDeleteProgress] = useState(0);
  const deleteProgressRef = useRef(0);

  const invalidate = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: [queryKey] });
  }, [queryClient, queryKey]);

  const bulkDeleteMut = useMutation<void, Error, ImageDeleteRun>({
    meta: { suppressGlobalError: true },
    mutationFn: async (run) => {
      // Give React a browser task to paint the initial 0/total state before a fast local deletion starts.
      await new Promise<void>((resolve) => window.setTimeout(resolve, 0));
      await runSequentialBatches(
        run.ids,
        (batch) => images.bulkDelete(batch, run.options),
        {
          batchSize: IMAGE_DELETE_BATCH_SIZE,
          startIndex: deleteProgressRef.current,
          onProgress: ({ completed }) => {
            deleteProgressRef.current = completed;
            setDeleteProgress(completed);
          },
        },
      );
    },
    onSuccess: () => {
      setShowDeleteConfirm(false);
      setDeleteRun(null);
      setDeleteProgress(0);
      deleteProgressRef.current = 0;
      onSelectNone();
      invalidate();
    },
    onError: invalidate,
  });

  const startOrRetryBulkDelete = useCallback((options?: DeleteEntityOptions) => {
    const run = deleteRun ?? { ids: [...selectedIds], options };
    if (!deleteRun) {
      setDeleteRun(run);
      setDeleteProgress(0);
      deleteProgressRef.current = 0;
    }
    bulkDeleteMut.mutate(run);
  }, [bulkDeleteMut, deleteRun, selectedIds]);

  const cancelBulkDelete = useCallback(() => {
    const partiallyCompleted = deleteProgressRef.current > 0;
    bulkDeleteMut.reset();
    setDeleteRun(null);
    setDeleteProgress(0);
    deleteProgressRef.current = 0;
    setShowDeleteConfirm(false);
    if (partiallyCompleted) {
      onSelectNone();
      invalidate();
    }
  }, [bulkDeleteMut, invalidate, onSelectNone]);

  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) => images.bulkUpdate({ ids: [...selectedIds], ...values } as any),
    onSuccess: () => { setShowBulkEdit(false); onSelectNone(); invalidate(); },
  });

  return (
    <>
      {onPlay && selectedIds.size > 1 ? (
        <button onClick={onPlay} className={`${actionClass} text-accent hover:text-accent-hover hover:bg-accent/10`}>
          <Play className="w-3 h-3" />
          Play
        </button>
      ) : null}
      {canWrite && (
        <button onClick={() => setShowBulkEdit(true)} className={`${actionClass} text-accent hover:text-accent-hover hover:bg-accent/10`}>
          <Edit className="w-3 h-3" />
          Edit
        </button>
      )}
      <ExtensionSelectionActions entityType="image" selectedIds={selectedIds} />
      {canDelete && (
        <button
          onClick={() => setShowDeleteConfirm(true)}
          disabled={bulkDeleteMut.isPending}
          className={`${actionClass} text-red-400 hover:text-red-300 hover:bg-red-900/20`}
        >
          {bulkDeleteMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
          Delete
        </button>
      )}

      <ConfirmDialog
        open={showDeleteConfirm}
        title={`Delete ${deleteRun?.ids.length ?? selectedIds.size} image${(deleteRun?.ids.length ?? selectedIds.size) === 1 ? "" : "s"}`}
        message={deleteRun
          ? "Deletion is running in batches."
          : `Delete ${selectedIds.size} selected image${selectedIds.size === 1 ? "" : "s"}? This cannot be undone.`}
        confirmLabel={bulkDeleteMut.isPending ? "Deleting..." : bulkDeleteMut.isError ? "Retry remaining" : "Delete"}
        onConfirm={startOrRetryBulkDelete}
        onCancel={cancelBulkDelete}
        isPending={bulkDeleteMut.isPending}
        errorMessage={bulkDeleteMut.error?.message ?? null}
        progress={deleteRun ? { completed: deleteProgress, total: deleteRun.ids.length } : null}
        lockOptions={deleteRun !== null}
        showDeleteFile
        showDeleteGenerated
      />
      <BulkEditDialog
        open={showBulkEdit}
        onClose={() => setShowBulkEdit(false)}
        title="Edit Images"
        selectedCount={selectedIds.size}
        fields={IMAGE_BULK_FIELDS}
        onApply={(values) => bulkEditMut.mutate(values)}
        isPending={bulkEditMut.isPending}
      />
    </>
  );
}
