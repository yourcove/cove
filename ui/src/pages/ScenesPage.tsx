import { useMemo, useState, useCallback, useEffect } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { scenes, tags, performers, galleries } from "../api/client";
import type { FindFilter, Scene, SceneCreate, SceneFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { useListUrlState } from "../hooks/useListUrlState";
import { RatingField } from "../components/Rating";
import { SceneTagger } from "../components/SceneTagger";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { formatDuration, formatFileSize, getResolutionLabel, RatingBadge } from "../components/shared";
import { SCENE_CRITERIA } from "../components/FilterDialog";
import { BulkEditDialog, SCENE_BULK_FIELDS } from "../components/BulkEditDialog";
import { EditModal, Field, TextArea, TextInput, SaveButton } from "../components/EditModal";
import { Film, Eye, Trash2, Loader2, Edit, Merge, Search, Play } from "lucide-react";
import { MergeDialog } from "../components/MergeDialog";
import { useSceneQueue } from "../state/SceneQueueContext";
import { IdentifyDialog } from "../components/IdentifyDialog";
import { SceneQueue } from "../components/SceneQueue";
import { SceneCard } from "../components/EntityCards";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { RouteCardLinkOverlay } from "../components/RouteCardLinkOverlay";
import { StringListEditor } from "../components/StringListEditor";
import { SCENE_SORT_OPTIONS } from "../components/sceneSortOptions";
import { useWallColumns } from "../hooks/useWallColumns";
import { StudioSelector } from "../components/StudioSelector";
import { withSeededRandomSort } from "../utils/seededRandomSort";
import { WallMediaCard } from "../components/WallMediaCard";

import { getDefaultFilter } from "../components/SavedFilterMenu";

interface Props {
  onNavigate: (r: any) => void;
}

export function ScenesPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("scenes");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: "grid" as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "scenes",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "wall", "tagger"] as const,
  });
  const [showCreate, setShowCreate] = useState(false);
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [showMerge, setShowMerge] = useState(false);
  const [showIdentify, setShowIdentify] = useState(false);
  const [showQueue, setShowQueue] = useState(false);
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const [wallColumnCount, setWallColumnCount] = useState(5);
  const queryClient = useQueryClient();
  const { setQueue } = useSceneQueue();

  const hasObjectFilter = Object.keys(objectFilter).length > 0;

  const { data, isLoading } = useQuery({
    queryKey: ["scenes", filter, objectFilter],
    queryFn: () =>
      hasObjectFilter
        ? scenes.findFiltered({ findFilter: filter, objectFilter: objectFilter as SceneFilterCriteria })
        : scenes.find(filter),
  });

  const items = data?.items ?? [];
  const wallColumns = useWallColumns(items, wallColumnCount);
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(items);
  const selecting = selectedIds.size > 0;

  const navigateToScene = useCallback((sceneId: number) => {
    const ids = items.map((s) => s.id);
    if (ids.length > 0) setQueue(ids, sceneId);
    onNavigate({ page: "scene", id: sceneId });
  }, [items, setQueue, onNavigate]);

  // When sort changes to random, generate a new seed for reproducibility
  const handleFilterChange = useCallback((next: typeof filter) => {
    setFilter(withSeededRandomSort(filter, next));
  }, [filter, setFilter]);

  // Bulk delete
  const bulkDeleteMut = useMutation({
    mutationFn: () => scenes.bulkDelete([...selectedIds]),
    onSuccess: () => {
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["scenes"] });
    },
  });

  // Bulk edit
  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) =>
      scenes.bulkUpdate({
        ids: [...selectedIds],
        ...values,
      } as any),
    onSuccess: () => {
      setShowBulkEdit(false);
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["scenes"] });
    },
  });

  // Metadata byline standard layout: (1:23:45 - 2.5 GB)
  const byline = useMemo(() => {
    const items = data?.items ?? [];
    const totalDur = items.reduce((sum, s) => sum + (s.files[0]?.duration ?? 0), 0);
    const totalSize = items.reduce((sum, s) => sum + (s.files[0]?.size ?? 0), 0);
    if (!totalDur && !totalSize) return null;
    const parts: string[] = [];
    if (totalDur) parts.push(formatDuration(totalDur));
    if (totalSize) parts.push(formatFileSize(totalSize));
    return <span className="text-xs text-muted">({parts.join(" — ")})</span>;
  }, [data?.items]);

  return (
    <>
    <SceneCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "scene", id })} />
    <ListPage
      title="Scenes"
      pageKey="scenes"
      filterMode="scenes"
      filter={filter}
      onFilterChange={handleFilterChange}
      totalCount={data?.totalCount ?? 0}
      isLoading={isLoading}
      sortOptions={SCENE_SORT_OPTIONS}
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={["grid", "list", "wall", "tagger"]}
      metadataByline={byline}
      criteriaDefinitions={SCENE_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      wallColumnCount={wallColumnCount}
      onWallColumnCountChange={setWallColumnCount}
      onNew={() => setShowCreate(true)}
      selectedIds={selectedIds}
      onSelectAll={selectAll}
      onSelectNone={selectNone}
      selectionActions={
        <>
          <button
            onClick={() => setShowBulkEdit(true)}
            className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10"
          >
            <Edit className="w-3 h-3" />
            Edit
          </button>
          <button
            onClick={() => setShowIdentify(true)}
            className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10"
          >
            <Search className="w-3 h-3" />
            Identify
          </button>
          {selectedIds.size >= 2 && (
            <button
              onClick={() => setShowMerge(true)}
              className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-yellow-400 hover:text-yellow-300 hover:bg-yellow-900/20"
            >
              <Merge className="w-3 h-3" />
              Merge
            </button>
          )}
          <button
            onClick={() => setShowQueue(true)}
            className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-green-400 hover:text-green-300 hover:bg-green-900/20"
          >
            <Play className="w-3 h-3" />
            Play
          </button>
          <button
            onClick={() => { if (confirm(`Delete ${selectedIds.size} scene(s)?`)) bulkDeleteMut.mutate(); }}
            disabled={bulkDeleteMut.isPending}
            className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-red-400 hover:text-red-300 hover:bg-red-900/20"
          >
            {bulkDeleteMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
            Delete
          </button>
        </>
      }
    >
      {displayMode === "grid" && (
        <div className="grid gap-3" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(var(--card-min-width, 200px), 1fr))" }}>
          {items.map((scene) => (
            <SceneCard
              key={scene.id}
              scene={scene}
              onClick={() => selecting ? toggle(scene.id) : navigateToScene(scene.id)}
              onNavigate={onNavigate}
              selected={selectedIds.has(scene.id)}
              onSelect={() => toggle(scene.id)}
              selecting={selecting}
              onQuickView={() => setQuickViewId(scene.id)}
            />
          ))}
        </div>
      )}
      {displayMode === "list" && (
        <SceneListTable scenes={items} onNavigate={onNavigate} selectedIds={selectedIds} onToggle={toggle} selecting={selecting} />
      )}
      {displayMode === "wall" && (
        <div className="flex gap-1 px-2">
          {wallColumns.map((column, columnIndex) => (
            <div key={columnIndex} className="flex-1 flex flex-col gap-1 min-w-0">
              {column.map((scene) => (
                <SceneWallCard key={scene.id} scene={scene} onClick={() => navigateToScene(scene.id)} />
              ))}
            </div>
          ))}
        </div>
      )}
      {displayMode === "tagger" && (
        <SceneTagger scenes={items} onNavigate={navigateToScene} />
      )}
      {items.length === 0 && !isLoading && (
        <div className="text-center py-20">
          <Film className="w-16 h-16 mx-auto mb-4 text-muted opacity-50" />
          <p className="text-secondary text-lg">No scenes found</p>
          <p className="text-muted text-sm mt-1">Try scanning your library to discover content</p>
        </div>
      )}
    </ListPage>

    {/* Bulk Edit Dialog */}
    <BulkEditDialog
      open={showBulkEdit}
      onClose={() => setShowBulkEdit(false)}
      title="Edit Scenes"
      selectedCount={selectedIds.size}
      fields={SCENE_BULK_FIELDS}
      onApply={(values) => bulkEditMut.mutate(values)}
      isPending={bulkEditMut.isPending}
    />
    <MergeDialog
      open={showMerge}
      onClose={() => { setShowMerge(false); selectNone(); }}
      entityType="scene"
      items={items.filter((s) => selectedIds.has(s.id)).map((s) => ({ id: s.id, name: s.title || s.files[0]?.basename || `Scene ${s.id}` }))}
      onMerge={scenes.merge}
      queryKey="scenes"
    />
    <IdentifyDialog
      open={showIdentify}
      onClose={() => { setShowIdentify(false); selectNone(); }}
      sceneIds={[...selectedIds]}
    />
    {showQueue && (
      <SceneQueue
        scenes={items.filter((s) => selectedIds.has(s.id)).map((s) => ({
          id: s.id,
          title: s.title || s.files[0]?.basename,
          duration: s.files[0]?.duration,
          screenshotUrl: scenes.screenshotUrl(s.id, s.updatedAt),
        }))}
        onClose={() => { setShowQueue(false); selectNone(); }}
        onNavigate={onNavigate}
      />
    )}
    {quickViewId !== null && (
      <QuickViewDialog type="scene" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
    )}
    </>
  );
}

function SceneCreateModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: number) => void }) {
  const qc = useQueryClient();
  const [title, setTitle] = useState("");
  const [code, setCode] = useState("");
  const [date, setDate] = useState("");
  const [details, setDetails] = useState("");
  const [director, setDirector] = useState("");
  const [rating, setRating] = useState<number | undefined>(undefined);
  const [organized, setOrganized] = useState(false);
  const [urls, setUrls] = useState<string[]>([""]);
  const [studioId, setStudioId] = useState<number | undefined>(undefined);

  const [tagSearch, setTagSearch] = useState("");
  const [selectedTags, setSelectedTags] = useState<{ id: number; name: string }[]>([]);
  const [performerSearch, setPerformerSearch] = useState("");
  const [selectedPerformers, setSelectedPerformers] = useState<{ id: number; name: string }[]>([]);
  const [gallerySearch, setGallerySearch] = useState("");
  const [selectedGalleries, setSelectedGalleries] = useState<{ id: number; title: string }[]>([]);

  const { data: tagResults } = useQuery({
    queryKey: ["tags-search", tagSearch],
    queryFn: () => tags.find({ q: tagSearch, perPage: 20, sort: "name", direction: "asc" }),
    enabled: tagSearch.length > 0,
  });

  const { data: performerResults } = useQuery({
    queryKey: ["performers-search", performerSearch],
    queryFn: () => performers.find({ q: performerSearch, perPage: 20, sort: "name", direction: "asc" }),
    enabled: performerSearch.length > 0,
  });

  const { data: galleryResults } = useQuery({
    queryKey: ["galleries-search", gallerySearch],
    queryFn: () => galleries.find({ q: gallerySearch, perPage: 20, sort: "title", direction: "asc" }),
    enabled: gallerySearch.length > 0,
  });

  const createMut = useMutation({
    mutationFn: (data: SceneCreate) => scenes.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["scenes"] });
      setTitle("");
      setCode("");
      setDate("");
      setDetails("");
      setDirector("");
      setRating(undefined);
      setOrganized(false);
      setUrls([""]);
      setStudioId(undefined);
      setSelectedTags([]);
      setSelectedPerformers([]);
      setSelectedGalleries([]);
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });

  const handleSave = () => {
    const urlList = urls.map((url) => url.trim()).filter(Boolean);
    createMut.mutate({
      title: title || undefined,
      code: code || undefined,
      date: date || undefined,
      details: details || undefined,
      director: director || undefined,
      rating,
      organized,
      studioId,
      urls: urlList,
      tagIds: selectedTags.map((t) => t.id),
      performerIds: selectedPerformers.map((p) => p.id),
      galleryIds: selectedGalleries.map((g) => g.id),
    });
  };

  return (
    <EditModal title="Create Scene" open={open} onClose={onClose}>
      <div className="grid grid-cols-2 gap-4">
        <Field label="Title">
          <TextInput value={title} onChange={setTitle} placeholder="Scene title" />
        </Field>
        <Field label="Date">
          <input
            type="date"
            value={date}
            onChange={(e) => setDate(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Field label="Studio Code">
          <TextInput value={code} onChange={setCode} placeholder="Studio code" />
        </Field>
        <Field label="Director">
          <TextInput value={director} onChange={setDirector} placeholder="Director" />
        </Field>
      </div>

      <Field label="Details">
        <TextArea value={details} onChange={setDetails} placeholder="Scene description" rows={3} />
      </Field>

      <div className="grid grid-cols-2 gap-4">
        <RatingField value={rating} onChange={setRating} />
        <Field label="Studio">
          <StudioSelector value={studioId} onChange={setStudioId} />
        </Field>
      </div>

      <Field label="URLs">
        <StringListEditor values={urls} onChange={setUrls} placeholder="https://..." addLabel="Add URL" inputType="url" />
      </Field>

      <label className="flex items-center gap-2 text-sm mb-2">
        <input
          type="checkbox"
          checked={organized}
          onChange={(e) => setOrganized(e.target.checked)}
          className="rounded bg-card border-border"
        />
        Organized
      </label>

      <Field label="Tags">
        <div className="flex flex-wrap gap-1.5 mb-2">
          {selectedTags.map((t) => (
            <span key={t.id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-accent/20 text-accent">
              {t.name}
              <button onClick={() => setSelectedTags(selectedTags.filter((x) => x.id !== t.id))} className="hover:text-white">×</button>
            </span>
          ))}
        </div>
        <input
          type="text"
          value={tagSearch}
          onChange={(e) => setTagSearch(e.target.value)}
          placeholder="Search tags..."
          className="w-full bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent mb-1"
        />
        {tagSearch && (tagResults?.items?.length ?? 0) > 0 && (
          <div className="max-h-32 overflow-y-auto bg-card rounded border border-border">
            {(tagResults?.items ?? []).filter((t) => !selectedTags.some((x) => x.id === t.id)).slice(0, 10).map((t) => (
              <button
                key={t.id}
                onClick={() => { setSelectedTags([...selectedTags, { id: t.id, name: t.name }]); setTagSearch(""); }}
                className="block w-full text-left px-3 py-1.5 text-sm text-secondary hover:bg-card-hover"
              >
                {t.name}
              </button>
            ))}
          </div>
        )}
      </Field>

      <Field label="Performers">
        <div className="flex flex-wrap gap-1.5 mb-2">
          {selectedPerformers.map((p) => (
            <span key={p.id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-accent/10 text-accent-hover">
              {p.name}
              <button onClick={() => setSelectedPerformers(selectedPerformers.filter((x) => x.id !== p.id))} className="hover:text-white">×</button>
            </span>
          ))}
        </div>
        <input
          type="text"
          value={performerSearch}
          onChange={(e) => setPerformerSearch(e.target.value)}
          placeholder="Search performers..."
          className="w-full bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent mb-1"
        />
        {performerSearch && (performerResults?.items?.length ?? 0) > 0 && (
          <div className="max-h-32 overflow-y-auto bg-card rounded border border-border">
            {(performerResults?.items ?? []).filter((p) => !selectedPerformers.some((x) => x.id === p.id)).slice(0, 10).map((p) => (
              <button
                key={p.id}
                onClick={() => { setSelectedPerformers([...selectedPerformers, { id: p.id, name: p.name }]); setPerformerSearch(""); }}
                className="block w-full text-left px-3 py-1.5 text-sm text-secondary hover:bg-card-hover"
              >
                {p.name}
              </button>
            ))}
          </div>
        )}
      </Field>

      <Field label="Galleries">
        <div className="flex flex-wrap gap-1.5 mb-2">
          {selectedGalleries.map((g) => (
            <span key={g.id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium bg-emerald-900 text-emerald-300">
              {g.title}
              <button onClick={() => setSelectedGalleries(selectedGalleries.filter((x) => x.id !== g.id))} className="hover:text-white">×</button>
            </span>
          ))}
        </div>
        <input
          type="text"
          value={gallerySearch}
          onChange={(e) => setGallerySearch(e.target.value)}
          placeholder="Search galleries..."
          className="w-full bg-card border border-border rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent mb-1"
        />
        {gallerySearch && (galleryResults?.items?.length ?? 0) > 0 && (
          <div className="max-h-32 overflow-y-auto bg-card rounded border border-border">
            {(galleryResults?.items ?? []).filter((g) => !selectedGalleries.some((x) => x.id === g.id)).slice(0, 10).map((g) => (
              <button
                key={g.id}
                onClick={() => { setSelectedGalleries([...selectedGalleries, { id: g.id, title: g.title || "Untitled" }]); setGallerySearch(""); }}
                className="block w-full text-left px-3 py-1.5 text-sm text-secondary hover:bg-card-hover"
              >
                {g.title || "Untitled"}
              </button>
            ))}
          </div>
        )}
      </Field>

      <div className="flex justify-end gap-3 mt-4">
        <button onClick={onClose} className="px-4 py-2 text-sm text-secondary hover:text-white">Cancel</button>
        <SaveButton loading={createMut.isPending} onClick={handleSave} />
      </div>
    </EditModal>
  );
}

/* ── Scene List Table ── */

function SceneListTable({ scenes, onNavigate, selectedIds, onToggle }: { scenes: Scene[]; onNavigate: (r: any) => void; selectedIds?: Set<number>; onToggle?: (id: number) => void; selecting?: boolean }) {
  return (
    <div className="overflow-x-auto px-2">
      <table className="w-full text-xs text-foreground">
        <thead>
          <tr className="border-b border-border text-muted">
            {selectedIds && <th className="w-8 py-2 px-2"></th>}
            <th className="text-left py-2 px-2 font-medium">Title</th>
            <th className="text-left py-2 px-2 font-medium">Date</th>
            <th className="text-left py-2 px-2 font-medium">Rating</th>
            <th className="text-left py-2 px-2 font-medium">Duration</th>
            <th className="text-left py-2 px-2 font-medium">Size</th>
            <th className="text-left py-2 px-2 font-medium">Resolution</th>
            <th className="text-right py-2 px-2 font-medium">Plays</th>
          </tr>
        </thead>
        <tbody>
          {scenes.map((scene) => {
            const file = scene.files[0];
            return (
              <tr
                key={scene.id}
                onClick={() => onNavigate({ page: "scene", id: scene.id })}
                className={`border-b border-border hover:bg-card cursor-pointer ${selectedIds?.has(scene.id) ? "bg-accent/10" : ""}`}
              >
                {selectedIds && (
                  <td className="py-1.5 px-2">
                    <input
                      type="checkbox"
                      checked={selectedIds.has(scene.id)}
                      onChange={() => onToggle?.(scene.id)}
                      onClick={(e) => e.stopPropagation()}
                      className="w-3.5 h-3.5 rounded border-border cursor-pointer accent-accent"
                    />
                  </td>
                )}
                <td className="py-1.5 px-2">
                  <span className="text-foreground hover:text-accent">
                    {scene.title || file?.basename || "Untitled"}
                  </span>
                  {scene.studioName && (
                    <span className="text-muted ml-2">— {scene.studioName}</span>
                  )}
                </td>
                <td className="py-1.5 px-2 text-muted">{scene.date || ""}</td>
                <td className="py-1.5 px-2"><RatingBadge rating={scene.rating} /></td>
                <td className="py-1.5 px-2 text-muted">{file ? formatDuration(file.duration) : ""}</td>
                <td className="py-1.5 px-2 text-muted">{file ? formatFileSize(file.size) : ""}</td>
                <td className="py-1.5 px-2 text-muted">{file ? getResolutionLabel(file.width, file.height) : ""}</td>
                <td className="py-1.5 px-2 text-right text-muted">{scene.playCount || ""}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/* ── Scene Wall Card ── */

function SceneWallCard({ scene, onClick }: { scene: Scene; onClick: () => void }) {
  const file = scene.files[0];
  const screenshotUrl = scenes.screenshotUrl(scene.id, scene.updatedAt);
  const aspectRatio = file?.width && file.height ? `${file.width} / ${file.height}` : "16 / 9";
  const title = scene.title || file?.basename || "Untitled";

  return (
    <WallMediaCard
      title={title}
      imageSrc={screenshotUrl}
      aspectRatio={aspectRatio}
      className="group"
    >
      <RouteCardLinkOverlay route={{ page: "scene", id: scene.id }} onClick={onClick} label={`Open scene ${title}`} />
      <div className="absolute inset-0 bg-gradient-to-t from-black/60 via-transparent to-transparent opacity-0 group-hover:opacity-100 transition-opacity" />
      <div className="absolute bottom-0 left-0 right-0 p-1.5 opacity-0 group-hover:opacity-100 transition-opacity">
          <p className="text-xs text-white font-medium truncate">
            {title}
          </p>
      </div>
      {file && file.duration > 0 && (
        <span className="absolute top-1 right-1 text-xs text-white bg-black/70 px-1 rounded">
          {formatDuration(file.duration)}
        </span>
      )}
    </WallMediaCard>
  );
}
