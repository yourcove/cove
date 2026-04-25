import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { galleries, groups, images, metadata, performers, scenes, studios, entityImages } from "../api/client";
import type { FindFilter, Gallery, Group, Image, Performer, Scene, Studio } from "../api/types";
import { formatDate, formatDuration, getResolutionLabel, TagBadge, CustomFieldsDisplay } from "../components/shared";
import { ArrowLeft, Check, Building2, Film, FolderOpen, GitMerge, Heart, ImageIcon, Layers, Link as LinkIcon, Link2, Loader2, MoreVertical, Music, Pencil, Trash2, UserRound, Wand2 } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { StudioEditModal } from "./StudioEditModal";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { DetailMergeDialog } from "../components/DetailMergeDialog";
import { ExtensionSlot } from "../router/RouteRegistry";
import { SceneCard, PerformerTile, ImageTile, GalleryTile, StudioTile, GroupTile } from "../components/EntityCards";
import { InteractiveRating } from "../components/Rating";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { useAppConfig } from "../state/AppConfigContext";
import { DetailListToolbar } from "../components/DetailListToolbar";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { useExtensionTabs } from "../components/useExtensionTabs";
import { SCENE_SORT_OPTIONS } from "../components/sceneSortOptions";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { GALLERY_SORT_OPTIONS } from "../components/gallerySortOptions";

const PERFORMER_SORT = [
  { value: "name", label: "Name" },
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "rating", label: "Rating" },
  { value: "scene_count", label: "Scene Count" },
  { value: "random", label: "Random" },
];
const IMAGE_SORT = [
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "title", label: "Title" },
  { value: "rating", label: "Rating" },
  { value: "random", label: "Random" },
];
const GALLERY_SORT = GALLERY_SORT_OPTIONS;
const STUDIO_SORT = [
  { value: "name", label: "Name" },
  { value: "rating", label: "Rating" },
  { value: "scene_count", label: "Scene Count" },
  { value: "gallery_count", label: "Gallery Count" },
  { value: "image_count", label: "Image Count" },
  { value: "child_count", label: "Substudios Count" },
  { value: "tag_count", label: "Tag Count" },
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "random", label: "Random" },
];
const GROUP_SORT = [
  { value: "name", label: "Name" },
  { value: "updated_at", label: "Updated At" },
  { value: "created_at", label: "Created At" },
  { value: "random", label: "Random" },
];

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type TabKey = "scenes" | "performers" | "galleries" | "images" | "studios" | "groups" | (string & {});

export function StudioDetailPage({ id, onNavigate }: Props) {
  const { config } = useAppConfig();
  const { data: studio, isLoading } = useQuery({
    queryKey: ["studio", id],
    queryFn: () => studios.get(id),
  });
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [mergeOpen, setMergeOpen] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const [activeTab, setActiveTab] = useState<TabKey>("scenes");
  const { allTabs: studioTabs, renderExtensionTab, extensionCounts } = useExtensionTabs("studio", [
    { key: "scenes", label: "Scenes", count: studio?.sceneCount },
    { key: "performers", label: "Performers", count: studio?.performerCount },
    { key: "galleries", label: "Galleries", count: studio?.galleryCount },
    { key: "images", label: "Images", count: studio?.imageCount },
    { key: "studios", label: "Sub-studios", count: studio?.childStudioCount },
    { key: "groups", label: "Groups", count: studio?.groupCount },
  ], id);
  const [sceneFilter, setSceneFilter] = useState<FindFilter>({ page: 1, perPage: 24, direction: "desc" });
  const [galleryFilter, setGalleryFilter] = useState<FindFilter>({ page: 1, perPage: 18, direction: "desc" });
  const [imageFilter, setImageFilter] = useState<FindFilter>({ page: 1, perPage: 30, direction: "desc" });
  const [performerFilter, setPerformerFilter] = useState<FindFilter>({ page: 1, perPage: 18, direction: "asc" });
  const [childFilter, setChildFilter] = useState<FindFilter>({ page: 1, perPage: 18, direction: "asc" });
  const [groupFilter, setGroupFilter] = useState<FindFilter>({ page: 1, perPage: 18, direction: "asc" });
  const queryClient = useQueryClient();
  const { backLabel, goBack } = useBackNavigation({ page: "studios" }, onNavigate);

  useEffect(() => {
    if (studio) document.title = `${studio.name} | Cove`;
    return () => { document.title = "Cove"; };
  }, [studio]);

  // Close ops menu on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(e.target as Node)) {
        setShowOpsMenu(false);
      }
    };
    if (showOpsMenu) document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [showOpsMenu]);

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement).tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;
      switch (e.key) {
        case "e": setEditing((v) => !v); break;
        case "f": if (studio) updateMut.mutate({ favorite: !studio.favorite }); break;
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [studio]);

  const deleteMut = useMutation({
    mutationFn: () => studios.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["studios"] });
      goBack();
    },
  });

  const updateMut = useMutation({
    mutationFn: (data: { favorite?: boolean; rating?: number; organized?: boolean }) => studios.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["studio", id] });
      queryClient.invalidateQueries({ queryKey: ["studios"] });
    },
  });

  const autoTagMut = useMutation({
    mutationFn: () => {
      if (!studio) throw new Error("Studio not loaded");
      return metadata.autoTag({ studios: [studio.name] });
    },
  });

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
      </div>
    );
  }

  if (!studio) {
    return <div className="py-16 text-center text-secondary">Studio not found</div>;
  }

  const studioImageUrl = studio.imagePath || entityImages.studioImageUrl(studio.id, studio.updatedAt);

  return (
    <div className="min-h-screen">
      <div className="relative overflow-hidden border-b border-border detail-hero-gradient">
        {/* Background studio image */}
        <img
          src={entityImages.studioImageUrl(studio.id, studio.updatedAt, 1600)}
          alt=""
          className="absolute inset-0 h-full w-full object-cover opacity-10 blur-md scale-110"
          onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }}
        />
        <div className="absolute inset-0 bg-gradient-to-t from-background via-background/70 to-transparent" />
        <div className="relative mx-auto max-w-7xl px-4 py-8">
          <div className="mb-5 flex items-center justify-between gap-4">
            <button
              onClick={goBack}
              className="flex items-center gap-1 text-sm text-secondary hover:text-foreground"
            >
              <ArrowLeft className="h-4 w-4" /> {backLabel}
            </button>
            <div className="flex items-center gap-2">
              <ExtensionSlot slot="studio-detail-actions" context={{ studio, onNavigate }} />
              <div className="relative" ref={opsMenuRef}>
                <button
                  onClick={() => setShowOpsMenu(!showOpsMenu)}
                  className="rounded border border-border bg-card p-2 text-secondary hover:text-foreground"
                  title="Actions"
                >
                  <MoreVertical className="h-4 w-4" />
                </button>
                {showOpsMenu && (
                  <div className="absolute right-0 z-50 mt-1 min-w-[160px] rounded-lg border border-border bg-card py-1 shadow-xl">
                    <button onClick={() => { setEditing(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-foreground hover:bg-surface">
                      <Pencil className="h-3.5 w-3.5" /> Edit
                    </button>
                    <button onClick={() => { autoTagMut.mutate(); setShowOpsMenu(false); }} disabled={autoTagMut.isPending} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-foreground hover:bg-surface disabled:opacity-60">
                      {autoTagMut.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wand2 className="h-3.5 w-3.5" />} Auto Tag
                    </button>
                    <button onClick={() => { setMergeOpen(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-foreground hover:bg-surface">
                      <GitMerge className="h-3.5 w-3.5" /> Merge...
                    </button>
                    <div className="my-1 border-t border-border" />
                    <button onClick={() => { setConfirmDelete(true); setShowOpsMenu(false); }} className="flex w-full items-center gap-2 px-3 py-2 text-sm text-red-400 hover:bg-surface">
                      <Trash2 className="h-3.5 w-3.5" /> Delete
                    </button>
                  </div>
                )}
              </div>
            </div>
          </div>

          <div className="flex flex-col gap-6 md:flex-row md:items-start">
            <div className="flex h-32 w-32 flex-shrink-0 items-center justify-center overflow-hidden rounded-2xl border border-border bg-card shadow-xl shadow-black/35 md:h-36 md:w-36">
              <img
                src={studioImageUrl}
                alt={studio.name}
                className="h-full w-full object-contain p-3"
                onError={(e) => {
                  (e.target as HTMLImageElement).style.display = "none";
                  const fallback = (e.target as HTMLImageElement).nextElementSibling as HTMLElement | null;
                  if (fallback) fallback.style.display = "flex";
                }}
              />
              <div className="hidden h-full w-full items-center justify-center bg-card">
                <Building2 className="h-14 w-14 text-accent" />
              </div>
            </div>
            <div className="min-w-0 flex-1">
              <div className="mb-2 flex items-start gap-4">
                <div className="min-w-0 flex-1">
                  <h1 className="truncate text-2xl sm:text-3xl md:text-4xl font-bold text-foreground">{studio.name}</h1>
                  {studio.parentName && studio.parentId && (
                    <button
                      onClick={() => onNavigate({ page: "studio", id: studio.parentId })}
                      className="mt-1 text-sm text-accent hover:underline"
                    >
                      Part of {studio.parentName}
                    </button>
                  )}
                  {studio.aliases.length > 0 && (
                    <p className="mt-1 text-sm text-secondary">Also known as: {studio.aliases.join(", ")}</p>
                  )}
                </div>
                <button
                  onClick={() => updateMut.mutate({ favorite: !studio.favorite })}
                  className={`rounded-full p-2 transition-colors ${
                    studio.favorite
                      ? "bg-red-500/15 text-red-500"
                      : "bg-card text-muted hover:text-red-400"
                  }`}
                  title={studio.favorite ? "Remove from favorites" : "Add to favorites"}
                >
                  <Heart className={`h-6 w-6 ${studio.favorite ? "fill-current" : ""}`} />
                </button>
                <button
                  onClick={() => updateMut.mutate({ organized: !studio.organized })}
                  className={`rounded-full p-2 transition-colors ${
                    studio.organized
                      ? "bg-green-500/15 text-green-500"
                      : "bg-card text-muted hover:text-green-400"
                  }`}
                  title={studio.organized ? "Mark as unorganized" : "Mark as organized"}
                >
                  <Check className="h-6 w-6" />
                </button>
              </div>

              <InteractiveRating value={studio.rating} onChange={(value) => updateMut.mutate({ rating: value })} />

              <div className="mt-4 flex flex-wrap gap-3">
                <CountCard label="Scenes" value={studio.sceneCount} icon={<Film className="h-4 w-4" />} />
                <CountCard label="Performers" value={studio.performerCount} icon={<UserRound className="h-4 w-4" />} />
                <CountCard label="Images" value={studio.imageCount} icon={<ImageIcon className="h-4 w-4" />} />
                <CountCard label="Galleries" value={studio.galleryCount} icon={<FolderOpen className="h-4 w-4" />} />
                <CountCard label="Sub-studios" value={studio.childStudioCount} icon={<Building2 className="h-4 w-4" />} />
                <CountCard label="Groups" value={studio.groupCount} icon={<Layers className="h-4 w-4" />} />
                {extensionCounts.map((ec) => (
                  <CountCard key={ec.key} label={ec.label} value={ec.count} icon={ec.icon === "music" ? <Music className="h-4 w-4" /> : undefined} />
                ))}
              </div>

              {studio.details && (
                <p className="mt-3 max-w-4xl whitespace-pre-wrap text-sm leading-6 text-secondary">{studio.details}</p>
              )}
              {studio.tags.length > 0 && (
                <div className="mt-4 flex flex-wrap gap-1.5">
                  {studio.tags.map((tag) => (
                    <TagBadge key={tag.id} name={tag.name} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
                  ))}
                </div>
              )}
              {studio.urls.length > 0 && (
                <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-sm">
                  {studio.urls.map((url, index) => (
                    <a key={index} href={url} target="_blank" rel="noopener noreferrer" className="flex items-center gap-1 text-accent hover:underline truncate max-w-xs">
                      <LinkIcon className="h-3.5 w-3.5 flex-shrink-0" />{new URL(url).hostname}
                    </a>
                  ))}
                </div>
              )}
              <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-muted">
                {studio.ignoreAutoTag && <span className="rounded bg-yellow-500/15 px-1.5 py-0.5 text-yellow-400">Ignores Auto-Tag</span>}
                {studio.remoteIds && studio.remoteIds.length > 0 && studio.remoteIds.map((sid) => (
                  <span key={`${sid.endpoint}:${sid.remoteId}`} className="inline-flex items-center gap-1 rounded border border-border px-1.5 py-0.5 text-xs text-secondary">
                    <Link2 className="h-2.5 w-2.5 text-accent" />{sid.remoteId.slice(0, 12)}…
                  </span>
                ))}
                <span title={`Created ${formatDate(studio.createdAt)}`}>Updated {formatDate(studio.updatedAt)}</span>
              </div>
              <CustomFieldsDisplay customFields={studio.customFields} />
              {autoTagMut.isSuccess && (
                <p className="mt-3 text-sm text-emerald-300">Auto-tag job queued.</p>
              )}
            </div>
          </div>
        </div>
      </div>

      <StudioEditModal studio={studio} open={editing} onClose={() => setEditing(false)} />
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Studio"
        message={`Delete "${studio.name}"? This cannot be undone.`}
        onConfirm={() => deleteMut.mutate()}
        onCancel={() => setConfirmDelete(false)}
      />
      <DetailMergeDialog
        open={mergeOpen}
        onClose={() => setMergeOpen(false)}
        entityType="studio"
        targetItem={{ id: studio.id, name: studio.name, imagePath: studioImageUrl, subtitle: studio.parentName }}
        searchItems={async (term) => {
          const response = await studios.find({ page: 1, perPage: 20, sort: "name", direction: "asc", q: term || undefined });
          return response.items.map((item) => ({
            id: item.id,
            name: item.name,
            imagePath: item.imagePath,
            subtitle: item.parentName,
          }));
        }}
        onMerge={(targetId, sourceIds) => studios.merge(targetId, sourceIds)}
        invalidateQueryKeys={[["studio", id], ["studios"]]}
      />

      <div className="px-4 py-6">
            <ExtensionSlot slot="studio-detail-sidebar-bottom" context={{ studio, onNavigate }} />

        <div className="mx-auto max-w-7xl border-b border-border mt-6">
          <div className="flex gap-1 overflow-x-auto">
            {studioTabs.map((tab) => (
              <button
                key={tab.key}
                onClick={() => setActiveTab(tab.key as TabKey)}
                className={`border-b-2 px-4 py-3 text-sm font-medium transition-colors ${
                  activeTab === tab.key
                    ? "border-accent text-foreground"
                    : "border-transparent text-secondary hover:border-muted hover:text-foreground"
                }`}
              >
                {tab.label}
                {(tab.count ?? 0) > 0 && <span className="ml-1.5 text-xs text-muted bg-surface rounded-full px-1.5 py-0.5">{tab.count}</span>}
              </button>
            ))}
          </div>
        </div>

        <div className="py-6">
          {activeTab === "scenes" && (
            <StudioScenesPanel studioId={id} filter={sceneFilter} setFilter={setSceneFilter} onNavigate={onNavigate} />
          )}
          {activeTab === "performers" && (
            <StudioPerformersPanel studioId={id} filter={performerFilter} setFilter={setPerformerFilter} onNavigate={onNavigate} />
          )}
          {activeTab === "galleries" && (
            <StudioGalleriesPanel studioId={id} filter={galleryFilter} setFilter={setGalleryFilter} onNavigate={onNavigate} />
          )}
          {activeTab === "images" && (
            <StudioImagesPanel studioId={id} filter={imageFilter} setFilter={setImageFilter} onNavigate={onNavigate} />
          )}
          {activeTab === "studios" && (
            <ChildStudiosPanel studioId={id} filter={childFilter} setFilter={setChildFilter} onNavigate={onNavigate} />
          )}
          {activeTab === "groups" && (
            <StudioGroupsPanel studioId={id} filter={groupFilter} setFilter={setGroupFilter} onNavigate={onNavigate} />
          )}
          {renderExtensionTab(activeTab, id, onNavigate)}
        </div>

        <ExtensionSlot slot="studio-detail-bottom" context={{ studio, onNavigate }} />
      </div>
    </div>
  );
}

function CountCard({ label, value, icon }: { label: string; value: number; icon: React.ReactNode }) {
  return (
    <div className="flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-2">
      <span className="text-accent">{icon}</span>
      <div>
        <div className="text-lg font-semibold text-foreground">{value}</div>
        <div className="text-xs text-muted">{label}</div>
      </div>
    </div>
  );
}

function StudioScenesPanel({ studioId, filter, setFilter, onNavigate }: {
  studioId: number;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const { data, isLoading } = useQuery({
    queryKey: ["studio-scenes", studioId, filter],
    queryFn: () => scenes.find(filter, { studioId: String(studioId) }),
  });
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(data?.items ?? []);
  const selecting = selectedIds.size > 0;

  if (isLoading) return <LoadingPanel icon={<Film className="h-10 w-10" />} message="Loading scenes..." />;
  if (!data || data.items.length === 0) return <EmptyPanel icon={<Film className="h-12 w-12" />} message="No scenes from this studio" />;

  return (
    <>
      <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} sortOptions={SCENE_SORT_OPTIONS} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="scenes" selectedIds={selectedIds} onDone={selectNone} sceneItems={data.items} onNavigate={onNavigate} />} />
      <div className="grid gap-3" style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${220 + zoomLevel * 50}px, 1fr))` }}>
        {data.items.map((scene) => (
          <SceneCard key={scene.id} scene={scene} onClick={() => selecting ? toggle(scene.id) : onNavigate({ page: "scene", id: scene.id })} onNavigate={onNavigate} onQuickView={() => setQuickViewId(scene.id)} selected={selectedIds.has(scene.id)} onSelect={() => toggle(scene.id)} selecting={selecting} />
        ))}
      </div>
      <Pager filter={filter} setFilter={setFilter} totalCount={data.totalCount} />
      {quickViewId !== null && (
        <QuickViewDialog type="scene" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function StudioGalleriesPanel({ studioId, filter, setFilter, onNavigate }: {
  studioId: number;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { data, isLoading } = useQuery({
    queryKey: ["studio-galleries", studioId, filter],
    queryFn: () => galleries.find(filter, { studioId: String(studioId) }),
  });
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(data?.items ?? []);
  const selecting = selectedIds.size > 0;

  if (isLoading) return <LoadingPanel icon={<FolderOpen className="h-10 w-10" />} message="Loading galleries..." />;
  if (!data || data.items.length === 0) return <EmptyPanel icon={<FolderOpen className="h-12 w-12" />} message="No galleries from this studio" />;

  return (
    <>
      <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} sortOptions={GALLERY_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="galleries" selectedIds={selectedIds} onDone={selectNone} />} />
      <div className="grid gap-3" style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${220 + zoomLevel * 50}px, 1fr))` }}>
        {data.items.map((gallery) => (
          <GalleryTile key={gallery.id} gallery={gallery} onClick={() => selecting ? toggle(gallery.id) : onNavigate({ page: "gallery", id: gallery.id })} selected={selectedIds.has(gallery.id)} onSelect={() => toggle(gallery.id)} selecting={selecting} />
        ))}
      </div>
      <Pager filter={filter} setFilter={setFilter} totalCount={data.totalCount} />
    </>
  );
}

function StudioImagesPanel({ studioId, filter, setFilter, onNavigate }: {
  studioId: number;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const { data, isLoading } = useQuery({
    queryKey: ["studio-images", studioId, filter],
    queryFn: () => images.find(filter, { studioId: String(studioId) }),
  });
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(data?.items ?? []);
  const selecting = selectedIds.size > 0;

  if (isLoading) return <LoadingPanel icon={<ImageIcon className="h-10 w-10" />} message="Loading images..." />;
  if (!data || data.items.length === 0) return <EmptyPanel icon={<ImageIcon className="h-12 w-12" />} message="No images from this studio" />;

  return (
    <>
      <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} sortOptions={IMAGE_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="images" selectedIds={selectedIds} onDone={selectNone} />} />
      <div className="grid gap-3" style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${160 + zoomLevel * 50}px, 1fr))` }}>
        {data.items.map((image) => (
          <ImageTile key={image.id} image={image} onClick={() => selecting ? toggle(image.id) : onNavigate({ page: "image", id: image.id })} onNavigate={onNavigate} onQuickView={() => setQuickViewId(image.id)} selected={selectedIds.has(image.id)} onSelect={() => toggle(image.id)} selecting={selecting} />
        ))}
      </div>
      <Pager filter={filter} setFilter={setFilter} totalCount={data.totalCount} />
      {quickViewId !== null && (
        <QuickViewDialog type="image" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function ChildStudiosPanel({ studioId, filter, setFilter, onNavigate }: {
  studioId: number;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { data, isLoading } = useQuery({
    queryKey: ["child-studios", studioId, filter],
    queryFn: () => studios.find(filter, { parentId: String(studioId) }),
  });
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(data?.items ?? []);
  const selecting = selectedIds.size > 0;

  if (isLoading) return <LoadingPanel icon={<Building2 className="h-10 w-10" />} message="Loading sub-studios..." />;
  if (!data || data.items.length === 0) return <EmptyPanel icon={<Building2 className="h-12 w-12" />} message="No sub-studios" />;

  return (
    <>
      <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} sortOptions={STUDIO_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="studios" selectedIds={selectedIds} onDone={selectNone} />} />
      <div className="grid gap-3" style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${200 + zoomLevel * 50}px, 1fr))` }}>
        {data.items.map((childStudio) => (
          <StudioTile key={childStudio.id} studio={childStudio} onClick={() => selecting ? toggle(childStudio.id) : onNavigate({ page: "studio", id: childStudio.id })} selected={selectedIds.has(childStudio.id)} onSelect={() => toggle(childStudio.id)} selecting={selecting} />
        ))}
      </div>
      <Pager filter={filter} setFilter={setFilter} totalCount={data.totalCount} />
    </>
  );
}

function StudioPerformersPanel({ studioId, filter, setFilter, onNavigate }: {
  studioId: number;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { data, isLoading } = useQuery({
    queryKey: ["studio-performers", studioId, filter],
    queryFn: () => performers.find(filter, { studioId: String(studioId) }),
  });
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(data?.items ?? []);
  const selecting = selectedIds.size > 0;

  if (isLoading) return <LoadingPanel icon={<UserRound className="h-10 w-10" />} message="Loading performers..." />;
  if (!data || data.items.length === 0) return <EmptyPanel icon={<UserRound className="h-12 w-12" />} message="No performers from this studio" />;

  return (
    <>
      <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} sortOptions={PERFORMER_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="performers" selectedIds={selectedIds} onDone={selectNone} />} />
      <div className="grid gap-3" style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${180 + zoomLevel * 50}px, 1fr))` }}>
        {data.items.map((performer) => (
          <PerformerTile key={performer.id} performer={performer} onClick={() => selecting ? toggle(performer.id) : onNavigate({ page: "performer", id: performer.id })} selected={selectedIds.has(performer.id)} onSelect={() => toggle(performer.id)} selecting={selecting} />
        ))}
      </div>
      <Pager filter={filter} setFilter={setFilter} totalCount={data.totalCount} />
    </>
  );
}

function StudioGroupsPanel({ studioId, filter, setFilter, onNavigate }: {
  studioId: number;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { data, isLoading } = useQuery({
    queryKey: ["studio-groups", studioId, filter],
    queryFn: () => groups.find(filter, { studioId: String(studioId) }),
  });
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(data?.items ?? []);
  const selecting = selectedIds.size > 0;

  if (isLoading) return <LoadingPanel icon={<Layers className="h-10 w-10" />} message="Loading groups..." />;
  if (!data || data.items.length === 0) return <EmptyPanel icon={<Layers className="h-12 w-12" />} message="No groups from this studio" />;

  return (
    <>
      <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data.totalCount} sortOptions={GROUP_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="groups" selectedIds={selectedIds} onDone={selectNone} />} />
      <div className="grid gap-3" style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${200 + zoomLevel * 50}px, 1fr))` }}>
        {data.items.map((group) => (
          <GroupTile key={group.id} group={group} onClick={() => selecting ? toggle(group.id) : onNavigate({ page: "group", id: group.id })} selected={selectedIds.has(group.id)} onSelect={() => toggle(group.id)} selecting={selecting} />
        ))}
      </div>
      <Pager filter={filter} setFilter={setFilter} totalCount={data.totalCount} />
    </>
  );
}

function Pager({ filter, setFilter, totalCount }: {
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  totalCount: number;
}) {
  const perPage = filter.perPage ?? 1;
  const page = filter.page ?? 1;
  const totalPages = Math.max(1, Math.ceil(totalCount / perPage));

  if (totalPages <= 1) return null;

  return (
    <div className="mx-auto max-w-7xl mt-6 flex items-center justify-center gap-4">
      <button
        disabled={page <= 1}
        onClick={() => setFilter({ ...filter, page: page - 1 })}
        className="rounded border border-border bg-card px-4 py-2 text-sm text-secondary hover:bg-card-hover disabled:cursor-not-allowed disabled:opacity-50"
      >
        Previous
      </button>
      <span className="text-sm text-secondary">Page {page} of {totalPages}</span>
      <button
        disabled={page >= totalPages}
        onClick={() => setFilter({ ...filter, page: page + 1 })}
        className="rounded border border-border bg-card px-4 py-2 text-sm text-secondary hover:bg-card-hover disabled:cursor-not-allowed disabled:opacity-50"
      >
        Next
      </button>
    </div>
  );
}

function LoadingPanel({ icon, message }: { icon: React.ReactNode; message: string }) {
  return (
    <div className="flex flex-col items-center justify-center py-12 text-muted">
      <div className="mb-3 animate-pulse">{icon}</div>
      <p>{message}</p>
    </div>
  );
}

function EmptyPanel({ icon, message }: { icon: React.ReactNode; message: string }) {
  return (
    <div className="flex flex-col items-center justify-center rounded-xl border border-dashed border-border bg-card/40 py-12 text-muted">
      <div className="mb-3 opacity-60">{icon}</div>
      <p>{message}</p>
    </div>
  );
}
