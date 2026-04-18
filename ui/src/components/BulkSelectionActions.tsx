import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Edit, Loader2, Trash2, Search, Merge, Play } from "lucide-react";
import { scenes as scenesApi, images, galleries, performers, groups, studios, tags } from "../api/client";
import { BulkEditDialog, SCENE_BULK_FIELDS, IMAGE_BULK_FIELDS, GALLERY_BULK_FIELDS, PERFORMER_BULK_FIELDS } from "./BulkEditDialog";
import { IdentifyDialog } from "./IdentifyDialog";
import { SceneQueue } from "./SceneQueue";

const FIELDS_MAP = {
  scenes: SCENE_BULK_FIELDS,
  images: IMAGE_BULK_FIELDS,
  galleries: GALLERY_BULK_FIELDS,
  performers: PERFORMER_BULK_FIELDS,
  groups: [] as typeof SCENE_BULK_FIELDS,
  studios: [] as typeof SCENE_BULK_FIELDS,
  tags: [] as typeof SCENE_BULK_FIELDS,
} as const;

const API_MAP = { scenes: scenesApi, images, galleries, performers, groups, studios, tags } as const;

interface Props {
  entityType: keyof typeof FIELDS_MAP;
  selectedIds: Set<number>;
  onDone: () => void;
  /** Raw scene items for Play/Identify (only needed when entityType is "scenes") */
  sceneItems?: { id: number; title?: string; updatedAt?: string; files: { basename?: string; duration?: number }[] }[];
  /** Navigate callback for the scene queue player */
  onNavigate?: (route: any) => void;
}

export function BulkSelectionActions({ entityType, selectedIds, onDone, sceneItems, onNavigate }: Props) {
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [showIdentify, setShowIdentify] = useState(false);
  const [showQueue, setShowQueue] = useState(false);
  const queryClient = useQueryClient();
  const api = API_MAP[entityType];
  const fields = FIELDS_MAP[entityType];

  const bulkDeleteMut = useMutation<void, Error, void>({
    mutationFn: async () => {
      await api.bulkDelete([...selectedIds]);
    },
    onSuccess: () => { queryClient.invalidateQueries(); onDone(); },
  });

  const bulkEditMut = useMutation<void, Error, Record<string, unknown>>({
    mutationFn: async (values) => {
      await api.bulkUpdate({ ids: [...selectedIds], ...values } as any);
    },
    onSuccess: () => { queryClient.invalidateQueries(); setShowBulkEdit(false); onDone(); },
  });

  const isScenes = entityType === "scenes";

  return (
    <>
      {fields.length > 0 && (
        <button
          onClick={() => setShowBulkEdit(true)}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10"
        >
          <Edit className="w-3 h-3" />
          Edit
        </button>
      )}
      {isScenes && (
        <button
          onClick={() => setShowIdentify(true)}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10"
        >
          <Search className="w-3 h-3" />
          Identify
        </button>
      )}
      {isScenes && selectedIds.size >= 2 && (
        <button
          onClick={() => {/* TODO: merge dialog */}}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-yellow-400 hover:text-yellow-300 hover:bg-yellow-900/20"
        >
          <Merge className="w-3 h-3" />
          Merge
        </button>
      )}
      {isScenes && sceneItems && onNavigate && (
        <button
          onClick={() => setShowQueue(true)}
          className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-green-400 hover:text-green-300 hover:bg-green-900/20"
        >
          <Play className="w-3 h-3" />
          Play
        </button>
      )}
      <button
        onClick={() => { if (confirm(`Delete ${selectedIds.size} item(s)?`)) bulkDeleteMut.mutate(); }}
        disabled={bulkDeleteMut.isPending}
        className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-red-400 hover:text-red-300 hover:bg-red-900/20"
      >
        {bulkDeleteMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
        Delete
      </button>
      {showBulkEdit && (
        <BulkEditDialog
          open
          onClose={() => setShowBulkEdit(false)}
          title={`Bulk Edit ${selectedIds.size} ${entityType}`}
          selectedCount={selectedIds.size}
          fields={fields as any}
          onApply={(values) => bulkEditMut.mutate(values)}
          isPending={bulkEditMut.isPending}
        />
      )}
      {showIdentify && isScenes && (
        <IdentifyDialog open onClose={() => setShowIdentify(false)} sceneIds={[...selectedIds]} />
      )}
      {showQueue && isScenes && sceneItems && onNavigate && (
        <SceneQueue
          scenes={sceneItems.filter(s => selectedIds.has(s.id)).map(s => ({
            id: s.id,
            title: s.title || s.files[0]?.basename,
            duration: s.files[0]?.duration,
            screenshotUrl: scenesApi.screenshotUrl(s.id, s.updatedAt),
          }))}
          onClose={() => setShowQueue(false)}
          onNavigate={onNavigate}
        />
      )}
    </>
  );
}
