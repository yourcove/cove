import { useEffect, useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Download, Edit, Image as ImageIcon, Loader2, Trash2, Search, Play, Unlink } from "lucide-react";
import { videos as videosApi, images, galleries, performers, groups, studios, tags, audios, texts, entityImages } from "../api/client";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canWriteEntity } from "../auth/visibility";
import type {
  Audio,
  BulkAudioUpdate,
  BulkGalleryUpdate,
  BulkGroupUpdate,
  BulkImageUpdate,
  BulkPerformerUpdate,
  BulkVideoUpdate,
  BulkStudioUpdate,
  BulkTagUpdate,
  BulkTextUpdate,
  BulkDeletionJobStart,
  DeleteEntityOptions,
  Video,
} from "../api/types";
import type { TextDocument } from "../api/types";
import type { Route } from "../router/location";
import { BulkEditDialog, VIDEO_BULK_FIELDS, IMAGE_BULK_FIELDS, GALLERY_BULK_FIELDS, PERFORMER_BULK_FIELDS, GROUP_BULK_FIELDS, STUDIO_BULK_FIELDS, TAG_BULK_FIELDS, AUDIO_BULK_FIELDS, TEXT_BULK_FIELDS, type BulkEditField } from "./BulkEditDialog";
import { BatchDownloadOptionsDialog } from "./BatchDownloadOptionsDialog";
import { ConfirmDialog } from "./ConfirmDialog";
import { IdentifyDialog } from "./IdentifyDialog";
import { VideoQueue } from "./VideoQueue";
import { ExtensionSelectionActions } from "./ExtensionSelectionActions";
import { MediaScrapeDialog } from "./MediaScrapeDialog";
import {
  DEFAULT_BATCH_DOWNLOAD_OPTIONS,
  formatBatchDownloadSummary,
  getBatchDownloadOptionsStorageKey,
  getUndownloadedSelectionItems,
  loadStoredBatchDownloadOptions,
  queueBatchDownloads,
  saveStoredBatchDownloadOptions,
  type BatchDownloadOptions,
  type DownloadSelectionEntity,
  type DownloadSelectionItem,
} from "../utils/batchDownloads";

const FIELDS_MAP = {
  videos: VIDEO_BULK_FIELDS,
  images: IMAGE_BULK_FIELDS,
  galleries: GALLERY_BULK_FIELDS,
  performers: PERFORMER_BULK_FIELDS,
  groups: GROUP_BULK_FIELDS,
  studios: STUDIO_BULK_FIELDS,
  tags: TAG_BULK_FIELDS,
  audios: AUDIO_BULK_FIELDS,
  texts: TEXT_BULK_FIELDS,
} satisfies Record<string, BulkEditField[]>;

const API_MAP = { videos: videosApi, images, galleries, performers, groups, studios, tags, audios, texts } as const;

const ENTITY_RESOURCE_MAP = {
  videos: "video",
  images: "image",
  galleries: "gallery",
  performers: "performer",
  groups: "group",
  studios: "studio",
  tags: "tag",
  audios: "audio",
  texts: "text",
} as const;

type BulkSelectionEntityType = keyof typeof FIELDS_MAP;
type BulkUpdatePayloadByEntity = {
  videos: BulkVideoUpdate;
  images: BulkImageUpdate;
  galleries: BulkGalleryUpdate;
  performers: BulkPerformerUpdate;
  groups: BulkGroupUpdate;
  studios: BulkStudioUpdate;
  tags: BulkTagUpdate;
  audios: BulkAudioUpdate;
  texts: BulkTextUpdate;
};
type BulkUpdatePayload = BulkUpdatePayloadByEntity[BulkSelectionEntityType];

function runBulkUpdate(entityType: BulkSelectionEntityType, payload: BulkUpdatePayload) {
  switch (entityType) {
    case "videos": return videosApi.bulkUpdate(payload as BulkVideoUpdate);
    case "images": return images.bulkUpdate(payload as BulkImageUpdate);
    case "galleries": return galleries.bulkUpdate(payload as BulkGalleryUpdate);
    case "performers": return performers.bulkUpdate(payload as BulkPerformerUpdate);
    case "groups": return groups.bulkUpdate(payload as BulkGroupUpdate);
    case "studios": return studios.bulkUpdate(payload as BulkStudioUpdate);
    case "tags": return tags.bulkUpdate(payload as BulkTagUpdate);
    case "audios": return audios.bulkUpdate(payload as BulkAudioUpdate);
    case "texts": return texts.bulkUpdate(payload as BulkTextUpdate);
  }
}

export type NestedListParent = {
  type: "tag" | "performer" | "studio" | "group" | "gallery";
  id: number;
  label?: string;
};

type RemoveFromParentAction = {
  label: string;
  parentLabel: string;
  permissionTarget: "child" | "parent";
  run: (ids: number[]) => Promise<unknown>;
};

type CoverFromSelectionAction = {
  label: string;
  parentType: "tag" | "performer" | "group" | "gallery";
  run: (id: number) => Promise<unknown>;
};

function getEntityLabel(entityType: BulkSelectionEntityType, count: number) {
  const singular = ENTITY_RESOURCE_MAP[entityType];
  return count === 1 ? singular : entityType;
}

function getParentLabel(parent: NestedListParent) {
  return parent.label?.trim() || `this ${parent.type}`;
}

function getRemoveFromParentAction(entityType: BulkSelectionEntityType, parent?: NestedListParent): RemoveFromParentAction | null {
  if (!parent) return null;
  const parentLabel = getParentLabel(parent);

  if (parent.type === "tag") {
    const run = (ids: number[]) => {
      switch (entityType) {
        case "videos": return videosApi.bulkUpdate({ ids, tagIds: [parent.id], tagMode: "REMOVE" });
        case "images": return images.bulkUpdate({ ids, tagIds: [parent.id], tagMode: "REMOVE" });
        case "galleries": return galleries.bulkUpdate({ ids, tagIds: [parent.id], tagMode: "REMOVE" });
        case "performers": return performers.bulkUpdate({ ids, tagIds: [parent.id], tagMode: "REMOVE" });
        case "groups": return groups.bulkUpdate({ ids, tagIds: [parent.id], tagMode: "REMOVE" });
        case "studios": return studios.bulkUpdate({ ids, tagIds: [parent.id], tagMode: "REMOVE" });
        case "audios": return audios.bulkUpdate({ ids, tagIds: [parent.id], tagMode: "REMOVE" });
        case "texts": return texts.bulkUpdate({ ids, tagIds: [parent.id], tagMode: "REMOVE" });
        default: return Promise.reject(new Error("This nested removal is not supported."));
      }
    };
    return { label: `Remove from ${parentLabel}`, parentLabel, permissionTarget: "child", run };
  }

  if (parent.type === "performer") {
    const run = (ids: number[]) => {
      switch (entityType) {
        case "videos": return videosApi.bulkUpdate({ ids, performerIds: [parent.id], performerMode: "REMOVE" });
        case "images": return images.bulkUpdate({ ids, performerIds: [parent.id], performerMode: "REMOVE" });
        case "galleries": return galleries.bulkUpdate({ ids, performerIds: [parent.id], performerMode: "REMOVE" });
        case "audios": return audios.bulkUpdate({ ids, performerIds: [parent.id], performerMode: "REMOVE" });
        case "texts": return texts.bulkUpdate({ ids, performerIds: [parent.id], performerMode: "REMOVE" });
        default: return Promise.reject(new Error("This nested removal is not supported."));
      }
    };
    return { label: `Remove from ${parentLabel}`, parentLabel, permissionTarget: "child", run };
  }

  if (parent.type === "studio") {
    const run = (ids: number[]) => {
      switch (entityType) {
        case "videos": return videosApi.bulkUpdate({ ids, clearFields: ["studioId"] });
        case "images": return images.bulkUpdate({ ids, clearFields: ["studioId"] });
        case "galleries": return galleries.bulkUpdate({ ids, clearFields: ["studioId"] });
        case "groups": return groups.bulkUpdate({ ids, clearFields: ["studioId"] });
        case "studios": return studios.bulkUpdate({ ids, clearFields: ["parentId"] });
        case "audios": return audios.bulkUpdate({ ids, clearFields: ["studioId"] });
        case "texts": return texts.bulkUpdate({ ids, clearFields: ["studioId"] });
        default: return Promise.reject(new Error("This nested removal is not supported."));
      }
    };
    return { label: `Remove from ${parentLabel}`, parentLabel, permissionTarget: "child", run };
  }

  if (parent.type === "gallery") {
    const run = (ids: number[]) => {
      switch (entityType) {
        case "images": return galleries.removeImages(parent.id, ids);
        case "videos": return videosApi.bulkUpdate({ ids, galleryIds: [parent.id], galleryMode: "REMOVE" });
        default: return Promise.reject(new Error("This nested removal is not supported."));
      }
    };
    return { label: `Remove from ${parentLabel}`, parentLabel, permissionTarget: entityType === "images" ? "parent" : "child", run };
  }

  if (parent.type === "group") {
    const run = (ids: number[]) => {
      switch (entityType) {
        case "videos": return videosApi.bulkUpdate({ ids, groupIds: [{ groupId: parent.id, videoIndex: 0 }], groupMode: "REMOVE" });
        case "images": return groups.items.removeHosts(parent.id, { kind: "image", hostIds: ids });
        case "galleries": return groups.items.removeHosts(parent.id, { kind: "gallery", hostIds: ids });
        case "audios": return groups.items.removeHosts(parent.id, { kind: "audio", hostIds: ids });
        case "texts": return groups.items.removeHosts(parent.id, { kind: "text", hostIds: ids });
        case "groups": return Promise.all(ids.map((id) => groups.removeSubGroup(parent.id, id)));
        default: return Promise.reject(new Error("This nested removal is not supported."));
      }
    };
    return { label: `Remove from ${parentLabel}`, parentLabel, permissionTarget: entityType === "videos" ? "child" : "parent", run };
  }

  return null;
}

function getCoverFromSelectionAction(entityType: BulkSelectionEntityType, parent?: NestedListParent): CoverFromSelectionAction | null {
  if (!parent || (entityType !== "images" && entityType !== "videos")) return null;
  if (parent.type !== "tag" && parent.type !== "performer" && parent.type !== "group" && parent.type !== "gallery") return null;

  const sourceLabel = entityType === "images" ? "Image" : "Video";
  const parentLabel = parent.type.charAt(0).toUpperCase() + parent.type.slice(1);
  const sourceFor = (id: number) => entityType === "images" ? { imageId: id } : { videoId: id };
  const run = (id: number) => {
    switch (parent.type) {
      case "gallery":
        return entityImages.setGalleryImageFromSource(parent.id, sourceFor(id));
      case "performer": return entityImages.setPerformerImageFromSource(parent.id, sourceFor(id));
      case "tag": return entityImages.setTagImageFromSource(parent.id, sourceFor(id));
      case "group": return entityImages.setGroupFrontImageFromSource(parent.id, sourceFor(id));
      default: return Promise.reject(new Error("This cover action is not supported."));
    }
  };

  return { label: `Use ${sourceLabel} for ${parentLabel} Cover`, parentType: parent.type, run };
}

function getMutationErrorMessage(error: unknown) {
  return error instanceof Error ? error.message : error ? String(error) : null;
}

interface Props {
  entityType: keyof typeof FIELDS_MAP;
  selectedIds: Set<number>;
  onDone: () => void;
  /** Raw video items for Play/Identify (only needed when entityType is "videos") */
  videoItems?: Pick<Video, "id" | "title" | "updatedAt" | "urls" | "files">[];
  audioItems?: Audio[];
  textItems?: TextDocument[];
  downloadItems?: DownloadSelectionItem[];
  /** Navigate callback for the video queue player */
  onNavigate?: (route: Route) => void;
  removeFromParent?: NestedListParent;
}

export function BulkSelectionActions({ entityType, selectedIds, onDone, videoItems, audioItems, textItems, downloadItems, onNavigate, removeFromParent }: Props) {
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [showIdentify, setShowIdentify] = useState(false);
  const [showQueue, setShowQueue] = useState(false);
  const [showScrape, setShowScrape] = useState(false);
  const [showBatchDownloadOptions, setShowBatchDownloadOptions] = useState(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [showRemoveFromParentConfirm, setShowRemoveFromParentConfirm] = useState(false);
  const { hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const api = API_MAP[entityType];
  const fields = FIELDS_MAP[entityType];
  const resource = ENTITY_RESOURCE_MAP[entityType];
  const canWrite = canWriteEntity(resource, hasPermission);
  const canDelete = canDeleteEntity(resource, hasPermission);
  const removeFromParentAction = useMemo(
    () => getRemoveFromParentAction(entityType, removeFromParent),
    [entityType, removeFromParent?.id, removeFromParent?.label, removeFromParent?.type],
  );
  const coverFromSelectionAction = useMemo(
    () => getCoverFromSelectionAction(entityType, removeFromParent),
    [entityType, removeFromParent?.id, removeFromParent?.label, removeFromParent?.type],
  );
  const canRemoveFromParent = !!removeFromParentAction
    && (removeFromParentAction.permissionTarget === "parent"
      ? canWriteEntity(removeFromParent!.type, hasPermission)
      : canWrite);
  const selectedSourceId = selectedIds.size === 1 ? [...selectedIds][0] : undefined;
  const canSetParentCover = !!coverFromSelectionAction
    && selectedSourceId != null
    && canWriteEntity(coverFromSelectionAction.parentType, hasPermission);
  const supportsDeleteOptions = entityType === "videos" || entityType === "images" || entityType === "audios" || entityType === "texts";
  const canDeleteFiles = entityType === "images"
    ? hasPermission("images.delete.file")
    : entityType === "videos"
      ? hasPermission("videos.delete.file")
      : entityType === "audios" || entityType === "texts"
        ? hasPermission("files.delete")
        : false;

  const bulkDeleteMut = useMutation<BulkDeletionJobStart, Error, DeleteEntityOptions | undefined>({
    meta: { suppressGlobalError: true },
    mutationFn: async (options) => api.bulkDelete([...selectedIds], options),
    onSuccess: () => { setShowDeleteConfirm(false); onDone(); },
  });

  const bulkEditMut = useMutation<void, Error, Record<string, unknown>>({
    mutationFn: async (values) => {
      await runBulkUpdate(entityType, { ids: [...selectedIds], ...values } as BulkUpdatePayload);
    },
    onSuccess: () => { queryClient.invalidateQueries(); setShowBulkEdit(false); onDone(); },
  });

  const removeFromParentMut = useMutation<void, Error>({
    meta: { suppressGlobalError: true },
    mutationFn: async () => {
      if (!removeFromParentAction) return;
      await removeFromParentAction.run([...selectedIds]);
    },
    onSuccess: () => {
      queryClient.invalidateQueries();
      setShowRemoveFromParentConfirm(false);
      onDone();
    },
  });

  const setParentCoverMut = useMutation<void, Error>({
    mutationFn: async () => {
      if (!coverFromSelectionAction || selectedSourceId == null) return;
      await coverFromSelectionAction.run(selectedSourceId);
    },
    onSuccess: () => {
      queryClient.invalidateQueries();
      onDone();
    },
  });

  const isVideos = entityType === "videos";
  const isAudios = entityType === "audios";
  const isTexts = entityType === "texts";
  const canIdentify = isVideos && hasPermission("library.identify") && canWrite;
  const downloadEntity: DownloadSelectionEntity | null = entityType === "videos"
    ? "Video"
    : entityType === "images"
      ? "Image"
      : entityType === "galleries"
        ? "Gallery"
        : entityType === "audios"
          ? "Audio"
          : entityType === "texts"
            ? "Text"
            : null;
  const resolvedDownloadItems = useMemo(
    () => downloadItems ?? (downloadEntity === "Video" ? videoItems ?? [] : []),
    [downloadEntity, downloadItems, videoItems],
  );
  const selectedDownloadItems = useMemo(
    () => (downloadEntity ? getUndownloadedSelectionItems(resolvedDownloadItems, selectedIds) : []),
    [downloadEntity, resolvedDownloadItems, selectedIds],
  );
  const batchDownloadStorageKey = useMemo(
    () => (downloadEntity ? getBatchDownloadOptionsStorageKey(`bulk-${downloadEntity.toLowerCase()}`) : null),
    [downloadEntity],
  );
  const [batchDownloadOptions, setBatchDownloadOptions] = useState<BatchDownloadOptions>(() =>
    batchDownloadStorageKey ? loadStoredBatchDownloadOptions(batchDownloadStorageKey) : DEFAULT_BATCH_DOWNLOAD_OPTIONS,
  );
  const canDownload = !!downloadEntity && hasPermission("jobs.run") && canWrite;
  const selectedMediaItem = useMemo(() => {
    if (selectedIds.size !== 1) return undefined;
    const [selectedId] = [...selectedIds];
    return isAudios
      ? audioItems?.find((item) => item.id === selectedId)
      : isTexts
        ? textItems?.find((item) => item.id === selectedId)
        : undefined;
  }, [audioItems, isAudios, isTexts, selectedIds, textItems]);
  const mediaScrapeType = isAudios ? "audio" : isTexts ? "text" : null;
  const canScrapeMedia = canWrite && !!mediaScrapeType && !!selectedMediaItem;

  useEffect(() => {
    if (batchDownloadStorageKey) {
      setBatchDownloadOptions(loadStoredBatchDownloadOptions(batchDownloadStorageKey));
    }
  }, [batchDownloadStorageKey]);

  const batchDownloadMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (options: BatchDownloadOptions) => {
      if (!downloadEntity) {
        throw new Error("Bulk download is not available for this entity type.");
      }

      return queueBatchDownloads(downloadEntity, selectedDownloadItems, options);
    },
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: ["jobs"] });
      queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
      queryClient.invalidateQueries({ queryKey: ["jobs-history"] });
      queryClient.invalidateQueries();
      window.alert(formatBatchDownloadSummary(downloadEntity!.toLowerCase(), result));
      onDone();
    },
    onError: (error: Error) => {
      window.alert(error.message || "Failed to queue the selected downloads.");
    },
  });

  return (
    <>
      {canDownload && downloadEntity && selectedDownloadItems.length > 0 && (
        <button
          onClick={() => setShowBatchDownloadOptions(true)}
          disabled={batchDownloadMut.isPending}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-cyan-400 hover:text-cyan-300 hover:bg-cyan-900/20 disabled:opacity-60"
        >
          {batchDownloadMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Download className="w-3 h-3" />}
          Download
        </button>
      )}
      {canWrite && fields.length > 0 && (
        <button
          onClick={() => setShowBulkEdit(true)}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10"
        >
          <Edit className="w-3 h-3" />
          Edit
        </button>
      )}
      {canIdentify && (
        <button
          onClick={() => setShowIdentify(true)}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10"
        >
          <Search className="w-3 h-3" />
          Identify
        </button>
      )}
      {canScrapeMedia && mediaScrapeType && selectedMediaItem && (
        <button
          onClick={() => setShowScrape(true)}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10"
        >
          <Search className="w-3 h-3" />
          Scrape
        </button>
      )}
      {isVideos && videoItems && onNavigate && (
        <button
          onClick={() => setShowQueue(true)}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-green-400 hover:text-green-300 hover:bg-green-900/20"
        >
          <Play className="w-3 h-3" />
          Play
        </button>
      )}
      {isAudios && audioItems && onNavigate && (
        <button
          onClick={() => {
            const selectedAudio = audioItems.find((item) => selectedIds.has(item.id));
            if (selectedAudio) {
              onNavigate({ page: "audio", id: selectedAudio.id });
              onDone();
            }
          }}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-green-400 hover:text-green-300 hover:bg-green-900/20"
        >
          <Play className="w-3 h-3" />
          Play
        </button>
      )}
      {canSetParentCover && coverFromSelectionAction && (
        <button
          onClick={() => setParentCoverMut.mutate()}
          disabled={setParentCoverMut.isPending}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10 disabled:opacity-60"
        >
          {setParentCoverMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <ImageIcon className="w-3 h-3" />}
          {coverFromSelectionAction.label}
        </button>
      )}
      {canRemoveFromParent && removeFromParentAction && (
        <button
          onClick={() => setShowRemoveFromParentConfirm(true)}
          disabled={removeFromParentMut.isPending}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-orange-400 hover:text-orange-300 hover:bg-orange-900/20 disabled:opacity-60"
        >
          {removeFromParentMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Unlink className="w-3 h-3" />}
          {removeFromParentAction.label}
        </button>
      )}
      <ExtensionSelectionActions entityType={entityType} selectedIds={selectedIds} />
      {canDelete && (
        <button
          onClick={() => setShowDeleteConfirm(true)}
          disabled={bulkDeleteMut.isPending}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-red-400 hover:text-red-300 hover:bg-red-900/20"
        >
          {bulkDeleteMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
          Delete
        </button>
      )}
      {showBulkEdit && (
        <BulkEditDialog
          open
          onClose={() => setShowBulkEdit(false)}
          title={`Bulk Edit ${selectedIds.size} ${entityType}`}
          selectedCount={selectedIds.size}
          fields={fields}
          onApply={(values) => bulkEditMut.mutate(values)}
          isPending={bulkEditMut.isPending}
        />
      )}
      {showIdentify && canIdentify && (
        <IdentifyDialog open onClose={() => setShowIdentify(false)} videoIds={[...selectedIds]} />
      )}
      {showQueue && isVideos && videoItems && onNavigate && (
        <VideoQueue
          videos={videoItems.filter(s => selectedIds.has(s.id)).map(s => ({
            id: s.id,
            title: s.title || s.files[0]?.basename,
            duration: s.files[0]?.duration,
            screenshotUrl: videosApi.screenshotUrl(s.id, s.updatedAt),
          }))}
          onClose={() => setShowQueue(false)}
          onNavigate={onNavigate}
        />
      )}
      {showScrape && mediaScrapeType && selectedMediaItem ? (
        <MediaScrapeDialog
          open
          entityType={mediaScrapeType}
          entity={selectedMediaItem}
          onClose={() => setShowScrape(false)}
        />
      ) : null}
      {canDownload && downloadEntity && (
        <BatchDownloadOptionsDialog
          open={showBatchDownloadOptions}
          entity={downloadEntity}
          itemCount={selectedDownloadItems.length}
          initialOptions={batchDownloadOptions}
          isPending={batchDownloadMut.isPending}
          onClose={() => setShowBatchDownloadOptions(false)}
          onConfirm={(options) => {
            setBatchDownloadOptions(options);
            if (batchDownloadStorageKey) {
              saveStoredBatchDownloadOptions(batchDownloadStorageKey, options);
            }
            setShowBatchDownloadOptions(false);
            batchDownloadMut.mutate(options);
          }}
        />
      )}
      <ConfirmDialog
        open={showRemoveFromParentConfirm}
        title={removeFromParentAction?.label ?? "Remove from parent"}
        message={`Remove ${selectedIds.size} selected ${getEntityLabel(entityType, selectedIds.size)} from ${removeFromParentAction?.parentLabel ?? "this parent"}?`}
        confirmLabel={removeFromParentMut.isPending ? "Removing..." : "Remove"}
        onConfirm={() => removeFromParentMut.mutate()}
        onCancel={() => { removeFromParentMut.reset(); setShowRemoveFromParentConfirm(false); }}
        isPending={removeFromParentMut.isPending}
        errorMessage={getMutationErrorMessage(removeFromParentMut.error)}
      />
      <ConfirmDialog
        open={showDeleteConfirm}
        title={`Delete ${selectedIds.size} ${resource}${selectedIds.size === 1 ? "" : "s"}`}
        message={`Delete ${selectedIds.size} selected ${resource}${selectedIds.size === 1 ? "" : "s"}? This cannot be undone.`}
        confirmLabel={bulkDeleteMut.isPending ? "Queueing..." : "Queue deletion"}
        onConfirm={(options) => bulkDeleteMut.mutate(supportsDeleteOptions ? options : undefined)}
        onCancel={() => { bulkDeleteMut.reset(); setShowDeleteConfirm(false); }}
        isPending={bulkDeleteMut.isPending}
        errorMessage={getMutationErrorMessage(bulkDeleteMut.error)}
        showDeleteFile={supportsDeleteOptions && canDeleteFiles}
        showDeleteGenerated={supportsDeleteOptions}
      />
    </>
  );
}
