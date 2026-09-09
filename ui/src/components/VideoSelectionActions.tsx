import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Download, Edit, Loader2, Merge, Play, Search, Trash2 } from "lucide-react";
import type { BulkDeletionJobStart, Video } from "../api/types";
import { videos } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canWriteEntity } from "../auth/visibility";
import { useAppConfig } from "../state/AppConfigContext";
import { useVideoQueue } from "../state/VideoQueueContext";
import { BulkEditDialog, VIDEO_BULK_FIELDS } from "./BulkEditDialog";
import { ConfirmDialog } from "./ConfirmDialog";
import { ExtensionSelectionActions } from "./ExtensionSelectionActions";
import {
  formatBatchDownloadSummary,
  getBatchDownloadOptionsStorageKey,
  getUndownloadedSelectionItems,
  loadStoredBatchDownloadOptions,
  queueBatchDownloads,
  saveStoredBatchDownloadOptions,
  type BatchDownloadOptions,
} from "../utils/batchDownloads";

const VideoDownloadDialog = lazy(() =>
  import("./VideoDownloadDialog").then((module) => ({ default: module.VideoDownloadDialog })),
);
const BatchDownloadOptionsDialog = lazy(() =>
  import("./BatchDownloadOptionsDialog").then((module) => ({ default: module.BatchDownloadOptionsDialog })),
);
const MergeDialog = lazy(() => import("./MergeDialog").then((module) => ({ default: module.MergeDialog })));
const IdentifyDialog = lazy(() => import("./IdentifyDialog").then((module) => ({ default: module.IdentifyDialog })));

const actionClass = "flex items-center gap-1 px-2 py-0.5 rounded text-xs";

export interface VideoSelectionActionsProps {
  /** The videos currently rendered, used to resolve the selection into full entities. */
  items: Video[];
  selectedIds: Set<number>;
  /** Clear the selection — called after any action that consumes it. */
  onSelectNone: () => void;
  onNavigate: (route: { page: string; id?: number }) => void;
  /**
   * Distinguishes this list's remembered batch-download options from another's (e.g. "page-videos").
   * Lists that shouldn't share those defaults pass their own key.
   */
  storageKey?: string;
  /** React Query key to invalidate after a bulk mutation, so the owning list refetches. */
  queryKey?: string;
}

/**
 * The bulk actions offered for a multi-selection of videos: download, edit, identify, merge, play, whatever
 * extensions contribute, and delete — plus every dialog they open and the permission checks that gate them.
 *
 * This is the single definition of "what you can do with selected videos", so the native videos page and any
 * extension list (the recommendations feed, for one) offer exactly the same actions and keep doing so as actions
 * are added. Drop it straight into `ListPage`'s `selectionActions`; it renders its own dialogs inline.
 */
export function VideoSelectionActions({
  items,
  selectedIds,
  onSelectNone,
  onNavigate,
  storageKey = "selection-actions",
  queryKey = "videos",
}: VideoSelectionActionsProps) {
  const { hasPermission } = useAuth();
  const { config } = useAppConfig();
  const { setQueue } = useVideoQueue();
  const queryClient = useQueryClient();

  const canWrite = canWriteEntity("video", hasPermission);
  const canDelete = canDeleteEntity("video", hasPermission);
  const canDeleteFiles = hasPermission("videos.delete.file");
  const canIdentify = hasPermission("library.identify") && canWrite;
  const canDownload = hasPermission("jobs.run") && canWrite;
  const continuePlaylistDefault = config?.ui.continuePlaylistDefault ?? false;

  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [showMerge, setShowMerge] = useState(false);
  const [showIdentify, setShowIdentify] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [showBatchDownloadOptions, setShowBatchDownloadOptions] = useState(false);
  const [downloadTarget, setDownloadTarget] = useState<Video | null>(null);

  const selectedVideos = useMemo(() => items.filter((video) => selectedIds.has(video.id)), [items, selectedIds]);
  const selectedVideo = selectedIds.size === 1 ? selectedVideos[0] : undefined;
  const selectedDownloadTargets = useMemo(
    () => getUndownloadedSelectionItems(items, selectedIds),
    [items, selectedIds],
  );
  const canDownloadSelection = canDownload && selectedDownloadTargets.length > 0;

  const batchDownloadStorageKey = getBatchDownloadOptionsStorageKey(storageKey);
  const [batchDownloadOptions, setBatchDownloadOptions] = useState<BatchDownloadOptions>(() =>
    loadStoredBatchDownloadOptions(batchDownloadStorageKey),
  );
  useEffect(() => {
    setBatchDownloadOptions(loadStoredBatchDownloadOptions(batchDownloadStorageKey));
  }, [batchDownloadStorageKey]);

  const invalidate = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: [queryKey] });
  }, [queryClient, queryKey]);

  const bulkDeleteMut = useMutation<
    BulkDeletionJobStart,
    Error,
    { deleteFile?: boolean; deleteGenerated?: boolean } | undefined
  >({
    mutationFn: (options?: { deleteFile?: boolean; deleteGenerated?: boolean }) =>
      videos.bulkDelete([...selectedIds], options),
    onSuccess: () => {
      setShowDeleteConfirm(false);
      onSelectNone();
    },
  });

  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) => videos.bulkUpdate({ ids: [...selectedIds], ...values } as any),
    onSuccess: () => {
      setShowBulkEdit(false);
      onSelectNone();
      invalidate();
    },
  });

  const batchDownloadMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (options: BatchDownloadOptions) => queueBatchDownloads("Video", selectedDownloadTargets, options),
    onSuccess: (result) => {
      for (const key of ["jobs", "jobs-active", "jobs-history"]) queryClient.invalidateQueries({ queryKey: [key] });
      invalidate();
      window.alert(formatBatchDownloadSummary("video", result));
      onSelectNone();
    },
    onError: (error: Error) => {
      window.alert(error.message || "Failed to queue the selected downloads.");
    },
  });

  const handlePlaySelected = useCallback(() => {
    const ids = selectedVideos.map((video) => video.id);
    if (ids.length === 0) return;

    setQueue(
      ids,
      ids[0],
      selectedVideos.map((video) => ({
        id: video.id,
        title: video.title || video.files[0]?.basename || `Video ${video.id}`,
        subtitle: video.studioName || video.date || undefined,
        imagePath: videos.screenshotUrl(video.id, video.updatedAt),
      })),
      { autoplay: continuePlaylistDefault },
    );
    onSelectNone();
    onNavigate({ page: "video", id: ids[0] });
  }, [continuePlaylistDefault, onNavigate, onSelectNone, selectedVideos, setQueue]);

  return (
    <>
      {canDownloadSelection && (
        <button
          onClick={() => {
            if (selectedDownloadTargets.length > 1 || !selectedVideo) {
              setShowBatchDownloadOptions(true);
              return;
            }
            setDownloadTarget(selectedVideo);
          }}
          disabled={batchDownloadMut.isPending}
          className={`${actionClass} text-cyan-400 hover:text-cyan-300 hover:bg-cyan-900/20 disabled:opacity-60`}
        >
          {batchDownloadMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Download className="w-3 h-3" />}
          Download
        </button>
      )}
      {canWrite && (
        <button
          onClick={() => setShowBulkEdit(true)}
          className={`${actionClass} text-accent hover:text-accent-hover hover:bg-accent/10`}
        >
          <Edit className="w-3 h-3" />
          Edit
        </button>
      )}
      {canIdentify && (
        <button
          onClick={() => setShowIdentify(true)}
          className={`${actionClass} text-accent hover:text-accent-hover hover:bg-accent/10`}
        >
          <Search className="w-3 h-3" />
          Identify
        </button>
      )}
      {canWrite && selectedIds.size >= 2 && (
        <button
          onClick={() => setShowMerge(true)}
          className={`${actionClass} text-yellow-400 hover:text-yellow-300 hover:bg-yellow-900/20`}
        >
          <Merge className="w-3 h-3" />
          Merge
        </button>
      )}
      <button
        onClick={handlePlaySelected}
        className={`${actionClass} text-green-400 hover:text-green-300 hover:bg-green-900/20`}
      >
        <Play className="w-3 h-3" />
        Play
      </button>
      <ExtensionSelectionActions entityType="video" selectedIds={selectedIds} />
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
        title={`Delete ${selectedIds.size} video${selectedIds.size === 1 ? "" : "s"}`}
        message={`Delete ${selectedIds.size} selected video${selectedIds.size === 1 ? "" : "s"}? This cannot be undone.`}
        confirmLabel={bulkDeleteMut.isPending ? "Queueing..." : "Queue deletion"}
        onConfirm={(options) => bulkDeleteMut.mutate(options)}
        onCancel={() => setShowDeleteConfirm(false)}
        isPending={bulkDeleteMut.isPending}
        errorMessage={bulkDeleteMut.error?.message ?? null}
        showDeleteFile={canDeleteFiles}
        showDeleteGenerated
      />
      <BulkEditDialog
        open={showBulkEdit}
        onClose={() => setShowBulkEdit(false)}
        title="Edit Videos"
        selectedCount={selectedIds.size}
        fields={VIDEO_BULK_FIELDS}
        onApply={(values) => bulkEditMut.mutate(values)}
        isPending={bulkEditMut.isPending}
      />
      <Suspense fallback={null}>
        {downloadTarget !== null ? (
          <VideoDownloadDialog
            open
            video={downloadTarget}
            onClose={() => setDownloadTarget(null)}
            onNavigate={onNavigate}
          />
        ) : null}
        {showBatchDownloadOptions ? (
          <BatchDownloadOptionsDialog
            open
            entity="Video"
            itemCount={selectedDownloadTargets.length}
            initialOptions={batchDownloadOptions}
            isPending={batchDownloadMut.isPending}
            onClose={() => setShowBatchDownloadOptions(false)}
            onConfirm={(options) => {
              setBatchDownloadOptions(options);
              saveStoredBatchDownloadOptions(batchDownloadStorageKey, options);
              setShowBatchDownloadOptions(false);
              batchDownloadMut.mutate(options);
            }}
          />
        ) : null}
        {showMerge ? (
          <MergeDialog
            open
            onClose={() => {
              setShowMerge(false);
              onSelectNone();
            }}
            entityType="video"
            items={selectedVideos.map((video) => ({
              id: video.id,
              name: video.title || video.files[0]?.basename || `Video ${video.id}`,
            }))}
            onMerge={videos.merge}
            queryKey={queryKey}
          />
        ) : null}
        {showIdentify ? (
          <IdentifyDialog
            open
            onClose={() => {
              setShowIdentify(false);
              onSelectNone();
            }}
            videoIds={[...selectedIds]}
          />
        ) : null}
      </Suspense>
    </>
  );
}
