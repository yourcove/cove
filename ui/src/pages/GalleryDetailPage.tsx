import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { galleries, images, scenes, entityImages } from "../api/client";
import type { FindFilter, GalleryChapter } from "../api/types";
import { formatDate, formatDuration, formatFileSize, getResolutionLabel, TagBadge, CustomFieldsDisplay } from "../components/shared";
import { ArrowLeft, BookOpen, Film, FolderOpen, HardDrive, ImageIcon, Link as LinkIcon, Pencil, Plus, Trash2, UserRound, Check, Loader2, MoreVertical, RefreshCw, Star } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { GalleryEditModal } from "./GalleryEditModal";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ExtensionSlot } from "../router/RouteRegistry";
import { Lightbox, type LightboxImage } from "../components/Lightbox";
import { InteractiveRating } from "../components/Rating";
import { DetailListToolbar } from "../components/DetailListToolbar";
import { SceneCard, ImageTile } from "../components/EntityCards";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { useExtensionTabs } from "../components/useExtensionTabs";
import { createCardNavigationHandlers } from "../components/cardNavigation";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import { useBackNavigation } from "../hooks/useBackNavigation";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type TabKey = "images" | "scenes" | "chapters" | "fileinfo" | (string & {});

export function GalleryDetailPage({ id, onNavigate }: Props) {
  const [imageFilter, setImageFilter] = useState<FindFilter>({ page: 1, perPage: 60, direction: "desc" });
  const { data: gallery, isLoading } = useQuery({
    queryKey: ["gallery", id],
    queryFn: () => galleries.get(id),
  });
  const { data: galleryImages } = useQuery({
    queryKey: ["gallery-images", id, imageFilter],
    queryFn: () => images.find(imageFilter, { galleryId: id }),
    enabled: !!gallery,
  });
  const { data: chaptersData } = useQuery({
    queryKey: ["gallery-chapters", id],
    queryFn: () => galleries.chapters(id),
    enabled: !!gallery,
  });
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [activeTab, setActiveTab] = useState<TabKey>("images");
  const { allTabs: galleryTabs, renderExtensionTab } = useExtensionTabs("gallery", [
    { key: "images", label: "Images", count: gallery?.imageCount },
    { key: "scenes", label: "Scenes" },
    { key: "chapters", label: "Chapters", count: chaptersData?.length ?? 0 },
    { key: "fileinfo", label: "File Info" },
  ]);
  const [lightboxOpen, setLightboxOpen] = useState(false);
  const [lightboxIndex, setLightboxIndex] = useState(0);
  const [imageZoom, setImageZoom] = useState(0);
  const [sceneFilter, setSceneFilter] = useState<FindFilter>({ page: 1, perPage: 24, direction: "desc" });
  const [showAddImages, setShowAddImages] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const queryClient = useQueryClient();
  const { backLabel, goBack } = useBackNavigation({ page: "galleries" }, onNavigate);

  useEffect(() => {
    if (gallery) document.title = `${gallery.title || `Gallery ${id}`} | Cove`;
    return () => { document.title = "Cove"; };
  }, [gallery, id]);

  // Close ops menu on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(e.target as Node)) setShowOpsMenu(false);
    };
    if (showOpsMenu) document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [showOpsMenu]);

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const el = (e.target as HTMLElement).tagName;
      if (el === "INPUT" || el === "TEXTAREA" || el === "SELECT") return;
      switch (e.key) {
        case "e": setEditing((v) => !v); break;
        case "a": setActiveTab("images"); break;
        case "c": setActiveTab("chapters"); break;
        case "f": setActiveTab("fileinfo"); break;
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);

  const deleteMut = useMutation({
    mutationFn: () => galleries.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["galleries"] });
      goBack();
    },
  });

  const galleryUpdateMut = useMutation({
    mutationFn: (data: { rating?: number; organized?: boolean }) => galleries.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery", id] });
      queryClient.invalidateQueries({ queryKey: ["galleries"] });
    },
  });

  const removeImagesMut = useMutation({
    mutationFn: (imageIds: number[]) => galleries.removeImages(id, imageIds),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery-images", id] });
      queryClient.invalidateQueries({ queryKey: ["gallery", id] });
    },
  });

  const addImagesMut = useMutation({
    mutationFn: (imageIds: number[]) => galleries.addImages(id, imageIds),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery-images", id] });
      queryClient.invalidateQueries({ queryKey: ["gallery", id] });
      setShowAddImages(false);
    },
  });

  const setCoverMut = useMutation({
    mutationFn: (imageId: number) => galleries.setCover(id, imageId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery", id] });
    },
  });

  const resetCoverMut = useMutation({
    mutationFn: () => galleries.resetCover(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery", id] });
    },
  });

  const coverImageUrl = useMemo(() => {
    if (gallery?.coverPath) return gallery.coverPath;
    const firstImage = galleryImages?.items[0];
    return firstImage ? images.imageUrl(firstImage.id) : null;
  }, [gallery, galleryImages]);

  const coverImage = useMemo(() => galleryImages?.items[0], [galleryImages]);

  const lightboxImages: LightboxImage[] = useMemo(
    () => (galleryImages?.items ?? []).map((img) => ({ id: img.id, src: images.imageUrl(img.id), title: img.title })),
    [galleryImages],
  );

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
      </div>
    );
  }

  if (!gallery) {
    return <div className="py-16 text-center text-secondary">Gallery not found</div>;
  }

  return (
    <div className="min-h-screen">
      <div className="relative overflow-hidden border-b border-border bg-surface">
        {coverImageUrl ? (
          <>
            <img src={coverImageUrl} alt="" className="absolute inset-0 h-full w-full object-cover opacity-20 blur-sm" />
            <div className="absolute inset-0 bg-gradient-to-t from-background via-background/70 to-background/30" />
          </>
        ) : (
          <div className="absolute inset-0 detail-hero-gradient" />
        )}

        <div className="relative mx-auto max-w-7xl px-4 py-8">
          <div className="mb-5 flex items-center justify-between gap-4">
            <button
              onClick={goBack}
              className="flex items-center gap-1 text-sm text-secondary hover:text-foreground"
            >
              <ArrowLeft className="h-4 w-4" /> {backLabel}
            </button>
            <div className="flex items-center gap-2">
              <ExtensionSlot slot="gallery-detail-actions" context={{ gallery, onNavigate }} />
              <button
                onClick={() => setEditing(true)}
                className="flex items-center gap-1.5 rounded bg-accent px-3 py-1.5 text-sm text-white hover:bg-accent-hover"
              >
                <Pencil className="h-3.5 w-3.5" /> Edit
              </button>
              <div className="relative" ref={opsMenuRef}>
                <button
                  onClick={() => setShowOpsMenu(!showOpsMenu)}
                  className="p-1.5 rounded text-secondary hover:text-foreground hover:bg-card"
                  title="Operations"
                >
                  <MoreVertical className="w-4 h-4" />
                </button>
                {showOpsMenu && (
                  <div className="absolute right-0 top-full mt-1 z-50 min-w-[180px] bg-card border border-border rounded shadow-lg py-1">
                    <button onClick={() => { setEditing(true); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><Pencil className="w-3.5 h-3.5" /> Edit</button>
                    <button onClick={() => { setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface flex items-center gap-2"><RefreshCw className="w-3.5 h-3.5" /> Rescan</button>
                    <div className="border-t border-border my-1" />
                    <button onClick={() => { setConfirmDelete(true); setShowOpsMenu(false); }} className="w-full px-3 py-1.5 text-left text-sm text-red-400 hover:bg-surface flex items-center gap-2"><Trash2 className="w-3.5 h-3.5" /> Delete</button>
                  </div>
                )}
              </div>
            </div>
          </div>

          <div className="flex flex-col gap-6 md:flex-row md:items-end">
            <div className="flex h-40 w-32 flex-shrink-0 items-center justify-center overflow-hidden rounded-2xl border border-border bg-card shadow-xl shadow-black/35 sm:h-52 sm:w-40">
              {coverImageUrl ? (
                <img src={coverImageUrl} alt={gallery.title || ""} className="h-full w-full object-cover" />
              ) : (
                <FolderOpen className="h-14 w-14 text-muted" />
              )}
            </div>
            <div className="min-w-0 flex-1">
              <h1 className="truncate text-2xl sm:text-3xl md:text-4xl font-bold text-foreground">{gallery.title || "Untitled Gallery"}</h1>
              <div className="mt-2 flex flex-wrap items-center gap-3 text-sm text-secondary">
                {gallery.date && <span>{formatDate(gallery.date)}</span>}
                {gallery.studioName && gallery.studioId && (
                  <button onClick={() => onNavigate({ page: "studio", id: gallery.studioId })} className="text-accent hover:underline">
                    {gallery.studioName}
                  </button>
                )}
                {gallery.photographer && <span>Photographer: {gallery.photographer}</span>}
                {gallery.code && <span>Code: {gallery.code}</span>}
                <span className="flex items-center gap-1"><ImageIcon className="h-4 w-4" /> {gallery.imageCount} images</span>
                <span title={`Created ${formatDate(gallery.createdAt)}`}>Updated {formatDate(gallery.updatedAt)}</span>
              </div>
              <div className="mt-4 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-border bg-card/60 px-4 py-3">
                <InteractiveRating value={gallery.rating} onChange={(value) => galleryUpdateMut.mutate({ rating: value })} />
                <div className="flex items-center gap-2">
                  <button
                    onClick={() => galleryUpdateMut.mutate({ organized: !gallery.organized })}
                    className={`rounded px-2 py-1 text-xs font-medium transition-colors ${gallery.organized ? "bg-green-600 text-white" : "border border-border bg-card text-secondary hover:text-foreground"}`}
                  >
                    {gallery.organized ? "Organized" : "Mark Organized"}
                  </button>
                </div>
              </div>
              {gallery.details && (
                <p className="mt-3 max-w-4xl whitespace-pre-wrap text-sm leading-6 text-secondary">{gallery.details}</p>
              )}
              {gallery.tags.length > 0 && (
                <div className="mt-4 flex flex-wrap gap-1.5">
                  {gallery.tags.map((tag) => (
                    <TagBadge key={tag.id} name={tag.name} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
                  ))}
                </div>
              )}
              {gallery.urls.length > 0 && (
                <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-sm">
                  {gallery.urls.map((url, index) => (
                    <a key={index} href={url} target="_blank" rel="noopener noreferrer" className="flex items-center gap-1 text-accent hover:underline truncate max-w-xs">
                      <LinkIcon className="h-3.5 w-3.5 flex-shrink-0" />{new URL(url).hostname}
                    </a>
                  ))}
                </div>
              )}
              <CustomFieldsDisplay customFields={gallery.customFields} />
            </div>
          </div>

          {/* Tabs */}
          <div className="mt-6 flex gap-1 border-b border-border">
            {galleryTabs.map((tab) => (
              <button
                key={tab.key}
                onClick={() => setActiveTab(tab.key)}
                className={`flex items-center gap-2 px-4 py-2.5 text-sm font-medium transition-colors ${
                  activeTab === tab.key
                    ? "border-b-2 border-accent text-accent"
                    : "text-secondary hover:text-foreground"
                }`}
              >
                {tab.label}
                {tab.count !== undefined && (
                  <span className={`min-w-[20px] rounded-full px-1.5 py-0.5 text-center text-xs ${
                    activeTab === tab.key ? "bg-accent/20 text-accent" : "bg-surface text-muted"
                  }`}>{tab.count}</span>
                )}
              </button>
            ))}
          </div>
        </div>
      </div>

      <GalleryEditModal gallery={gallery} open={editing} onClose={() => setEditing(false)} />
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Gallery"
        message={`Delete "${gallery.title || "Untitled"}"? This cannot be undone.`}
        onConfirm={() => deleteMut.mutate()}
        onCancel={() => setConfirmDelete(false)}
      />

      <div className="px-4 py-6">
          {gallery.performers.length > 0 && (
              <div className="mb-6 rounded-xl border border-border bg-card p-4">
                <h2 className="mb-3 text-sm font-semibold uppercase tracking-wide text-muted">Performers</h2>
                <div className="flex flex-wrap justify-center gap-3">
                  {gallery.performers.map((performer) => (
                    <GalleryPerformerCard key={performer.id} performer={performer} onClick={() => onNavigate({ page: "performer", id: performer.id })} />
                  ))}
                </div>
              </div>
            )}

            {activeTab === "images" && (
              <GalleryImagesPanel
                galleryId={id}
                filter={imageFilter}
                setFilter={setImageFilter}
                onNavigate={onNavigate}
                galleryImages={galleryImages}
                onShowAddImages={() => setShowAddImages(true)}
                onLightbox={(idx) => { setLightboxIndex(idx); setLightboxOpen(true); }}
                removeImagesMut={removeImagesMut}
                imageZoom={imageZoom}
                setImageZoom={setImageZoom}
              />
            )}

            {activeTab === "scenes" && (
              <GalleryScenesPanel galleryId={id} filter={sceneFilter} setFilter={setSceneFilter} onNavigate={onNavigate} />
            )}

            {activeTab === "chapters" && (
              <GalleryChaptersPanel galleryId={id} chapters={chaptersData ?? []} />
            )}

            {activeTab === "fileinfo" && (
              <GalleryFileInfo gallery={gallery} />
            )}

            {renderExtensionTab(activeTab, id, onNavigate)}

            <ExtensionSlot slot="gallery-detail-main-bottom" context={{ gallery, onNavigate }} />

        <ExtensionSlot slot="gallery-detail-bottom" context={{ gallery, onNavigate }} />
      </div>

      {/* Add Images Dialog */}
      {showAddImages && (
        <AddImagesDialog
          galleryId={id}
          existingImageIds={new Set(galleryImages?.items.map((i) => i.id) ?? [])}
          onAdd={(ids) => addImagesMut.mutate(ids)}
          onClose={() => setShowAddImages(false)}
          isPending={addImagesMut.isPending}
        />
      )}

      <Lightbox
        images={lightboxImages}
        initialIndex={lightboxIndex}
        open={lightboxOpen}
        onClose={() => setLightboxOpen(false)}
      />
    </div>
  );
}

function GalleryPerformerCard({ performer, onClick }: { performer: { id: number; name: string; disambiguation?: string; imagePath?: string }; onClick: () => void }) {
  const imageUrl = performer.imagePath || entityImages.performerImageUrl(performer.id);
  const navigationHandlers = createCardNavigationHandlers<HTMLButtonElement>({ page: "performer", id: performer.id }, onClick);

  return (
    <button
      type="button"
      {...navigationHandlers}
      className="w-[180px] overflow-hidden rounded-lg border border-border bg-surface text-left transition-colors hover:border-accent/60"
    >
      <div className="aspect-[2/3] overflow-hidden bg-card">
        <img
          src={imageUrl}
          alt={performer.name}
          className="h-full w-full object-cover"
          onError={(e) => {
            (e.target as HTMLImageElement).style.display = "none";
            const fallback = (e.target as HTMLImageElement).nextElementSibling as HTMLElement | null;
            if (fallback) fallback.style.display = "flex";
          }}
        />
        <div className="hidden h-full w-full items-center justify-center bg-gradient-to-b from-card to-surface">
          <UserRound className="h-12 w-12 text-muted/50" />
        </div>
      </div>
      <div className="p-2 text-center">
        <p className="truncate text-sm font-medium text-foreground">{performer.name}</p>
        {performer.disambiguation && <p className="truncate text-xs text-muted">{performer.disambiguation}</p>}
      </div>
    </button>
  );
}

function GalleryScenesPanel({ galleryId, filter, setFilter, onNavigate }: {
  galleryId: number;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const { data, isLoading } = useQuery({
    queryKey: ["gallery-scenes", galleryId, filter],
    queryFn: () => scenes.find(filter, { galleryId: String(galleryId) }),
  });
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(data?.items ?? []);
  const selecting = selectedIds.size > 0;

  if (isLoading) return <LoadingPanel icon={<Film className="h-10 w-10" />} message="Loading scenes..." />;
  if (!data || data.items.length === 0) return <EmptyPanel icon={<Film className="h-12 w-12" />} message="No scenes for this gallery" />;

  return (
    <>
      <DetailListToolbar
        filter={filter}
        onFilterChange={setFilter}
        totalCount={data.totalCount}
        sortOptions={[
          { value: "title", label: "Title" },
          { value: "date", label: "Date" },
          { value: "rating", label: "Rating" },
          { value: "created_at", label: "Created At" },
        ]}
        zoomLevel={zoomLevel}
        onZoomChange={setZoomLevel}
        showSearch
        selectedCount={selectedIds.size}
        onSelectAll={selectAll}
        onSelectNone={selectNone}
        selectionActions={<BulkSelectionActions entityType="scenes" selectedIds={selectedIds} onDone={selectNone} sceneItems={data.items} onNavigate={onNavigate} />}
      />
      <div className="grid gap-4" style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${220 + zoomLevel * 50}px, 1fr))` }}>
        {data.items.map((scene) => (
          <SceneCard key={scene.id} scene={scene} onClick={() => selecting ? toggle(scene.id) : onNavigate({ page: "scene", id: scene.id })} onNavigate={onNavigate} onQuickView={() => setQuickViewId(scene.id)} selected={selectedIds.has(scene.id)} onSelect={() => toggle(scene.id)} selecting={selecting} />
        ))}
      </div>
      {quickViewId !== null && (
        <QuickViewDialog type="scene" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

const IMAGE_SORT = [
  { label: "Title", value: "title" },
  { label: "Rating", value: "rating" },
  { label: "Created At", value: "created_at" },
];

function GalleryImagesPanel({ galleryId, filter, setFilter, onNavigate, galleryImages, onShowAddImages, onLightbox, removeImagesMut, imageZoom, setImageZoom }: {
  galleryId: number;
  filter: FindFilter;
  setFilter: (f: FindFilter) => void;
  onNavigate: (r: any) => void;
  galleryImages: { items: any[]; totalCount: number } | undefined;
  onShowAddImages: () => void;
  onLightbox: (idx: number) => void;
  removeImagesMut: any;
  imageZoom: number;
  setImageZoom: (z: number) => void;
}) {
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(galleryImages?.items ?? []);
  const selecting = selectedIds.size > 0;

  if (!galleryImages) return <EmptyPanel icon={<ImageIcon className="h-12 w-12" />} message="No images in this gallery" />;
  if (galleryImages.items.length === 0) return (
    <>
      <div className="flex justify-end mb-3">
        <button onClick={onShowAddImages} className="flex items-center gap-1 px-2 py-1 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10 border border-border">
          <Plus className="w-3 h-3" /> Add Images
        </button>
      </div>
      <EmptyPanel icon={<ImageIcon className="h-12 w-12" />} message="No images in this gallery" />
    </>
  );

  return (
    <>
      <DetailListToolbar
        filter={filter}
        onFilterChange={setFilter}
        totalCount={galleryImages.totalCount}
        sortOptions={IMAGE_SORT}
        zoomLevel={imageZoom}
        onZoomChange={setImageZoom}
        showSearch
        selectedCount={selectedIds.size}
        onSelectAll={selectAll}
        onSelectNone={selectNone}
        selectionActions={
          <>
            <BulkSelectionActions entityType="images" selectedIds={selectedIds} onDone={selectNone} />
            <button
              onClick={() => { if (confirm(`Remove ${selectedIds.size} image(s) from gallery?`)) removeImagesMut.mutate([...selectedIds]); }}
              disabled={removeImagesMut.isPending}
              className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-orange-400 hover:text-orange-300 hover:bg-orange-900/20"
            >
              {removeImagesMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
              Remove from Gallery
            </button>
          </>
        }
      />
      <div className="flex justify-end mb-2">
        <button onClick={onShowAddImages} className="flex items-center gap-1 px-2 py-1 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10 border border-border">
          <Plus className="w-3 h-3" /> Add Images
        </button>
      </div>
      <div className="grid gap-3" style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${160 + imageZoom * 50}px, 1fr))` }}>
        {galleryImages.items.map((image, idx) => (
          <ImageTile
            key={image.id}
            image={image}
            onClick={() => selecting ? toggle(image.id) : onLightbox(idx)}
            onNavigate={onNavigate}
            onQuickView={() => setQuickViewId(image.id)}
            selected={selectedIds.has(image.id)}
            onSelect={() => toggle(image.id)}
            selecting={selecting}
          />
        ))}
      </div>
      {quickViewId !== null && (
        <QuickViewDialog type="image" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function GalleryChaptersPanel({ galleryId, chapters }: { galleryId: number; chapters: GalleryChapter[] }) {
  const queryClient = useQueryClient();
  const [adding, setAdding] = useState(false);
  const [newTitle, setNewTitle] = useState("");
  const [newIndex, setNewIndex] = useState(0);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editTitle, setEditTitle] = useState("");
  const [editIndex, setEditIndex] = useState(0);

  const createMut = useMutation({
    mutationFn: () => galleries.createChapter(galleryId, { title: newTitle, imageIndex: newIndex }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery-chapters", galleryId] });
      setAdding(false);
      setNewTitle("");
      setNewIndex(0);
    },
  });

  const updateMut = useMutation({
    mutationFn: (chapterId: number) => galleries.updateChapter(galleryId, chapterId, { title: editTitle, imageIndex: editIndex }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery-chapters", galleryId] });
      setEditingId(null);
    },
  });

  const deleteMut = useMutation({
    mutationFn: (chapterId: number) => galleries.deleteChapter(galleryId, chapterId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery-chapters", galleryId] });
    },
  });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">Chapters</h3>
        <button
          onClick={() => setAdding(true)}
          className="flex items-center gap-1.5 rounded bg-accent px-3 py-1.5 text-sm text-white hover:bg-accent-hover"
        >
          <Plus className="h-3.5 w-3.5" /> Add Chapter
        </button>
      </div>

      {adding && (
        <div className="rounded-xl border border-accent/40 bg-card p-4">
          <div className="grid gap-3 sm:grid-cols-[1fr_auto_auto_auto]">
            <input
              type="text"
              placeholder="Chapter title"
              value={newTitle}
              onChange={(e) => setNewTitle(e.target.value)}
              className="rounded border border-border bg-surface px-3 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
            />
            <input
              type="number"
              placeholder="Image index"
              value={newIndex}
              onChange={(e) => setNewIndex(parseInt(e.target.value) || 0)}
              className="w-24 rounded border border-border bg-surface px-3 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
            />
            <button onClick={() => createMut.mutate()} disabled={!newTitle} className="rounded bg-accent px-3 py-1.5 text-sm text-white hover:bg-accent-hover disabled:opacity-50">Save</button>
            <button onClick={() => setAdding(false)} className="rounded border border-border px-3 py-1.5 text-sm text-secondary hover:text-foreground">Cancel</button>
          </div>
        </div>
      )}

      {chapters.length === 0 ? (
        <EmptyPanel icon={<BookOpen className="h-12 w-12" />} message="No chapters" />
      ) : (
        <div className="divide-y divide-border rounded-xl border border-border bg-card">
          {chapters.map((ch) => (
            <div key={ch.id} className="flex items-center justify-between px-4 py-3">
              {editingId === ch.id ? (
                <div className="flex flex-1 items-center gap-3">
                  <input
                    type="text"
                    value={editTitle}
                    onChange={(e) => setEditTitle(e.target.value)}
                    className="flex-1 rounded border border-border bg-surface px-3 py-1 text-sm text-foreground focus:border-accent focus:outline-none"
                  />
                  <input
                    type="number"
                    value={editIndex}
                    onChange={(e) => setEditIndex(parseInt(e.target.value) || 0)}
                    className="w-20 rounded border border-border bg-surface px-3 py-1 text-sm text-foreground focus:border-accent focus:outline-none"
                  />
                  <button onClick={() => updateMut.mutate(ch.id)} className="text-sm text-accent hover:underline">Save</button>
                  <button onClick={() => setEditingId(null)} className="text-sm text-secondary hover:text-foreground">Cancel</button>
                </div>
              ) : (
                <>
                  <div>
                    <p className="text-sm font-medium text-foreground">{ch.title}</p>
                    <p className="text-xs text-secondary">Image #{ch.imageIndex}</p>
                  </div>
                  <div className="flex items-center gap-2">
                    <button
                      onClick={() => {
                        setEditingId(ch.id);
                        setEditTitle(ch.title);
                        setEditIndex(ch.imageIndex);
                      }}
                      className="rounded p-1 text-muted hover:text-accent"
                    >
                      <Pencil className="h-3.5 w-3.5" />
                    </button>
                    <button
                      onClick={() => deleteMut.mutate(ch.id)}
                      className="rounded p-1 text-muted hover:text-red-400"
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </button>
                  </div>
                </>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function AddImagesDialog({ galleryId, existingImageIds, onAdd, onClose, isPending }: {
  galleryId: number;
  existingImageIds: Set<number>;
  onAdd: (ids: number[]) => void;
  onClose: () => void;
  isPending: boolean;
}) {
  const [searchFilter, setSearchFilter] = useState<FindFilter>({ page: 1, perPage: 30, direction: "desc" });
  const [selected, setSelected] = useState<Set<number>>(new Set());

  const { data } = useQuery({
    queryKey: ["images-for-gallery", searchFilter],
    queryFn: () => images.find(searchFilter),
  });

  const allImages = data?.items ?? [];
  const available = allImages.filter((i) => !existingImageIds.has(i.id));

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70" onClick={onClose}>
      <div className="bg-card border border-border rounded-xl shadow-2xl w-full max-w-4xl max-h-[80vh] flex flex-col" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-border">
          <h2 className="text-lg font-semibold text-foreground">Add Images to Gallery</h2>
          <div className="flex items-center gap-3">
            <span className="text-xs text-muted">{selected.size} selected</span>
            <button
              onClick={() => onAdd([...selected])}
              disabled={selected.size === 0 || isPending}
              className="px-3 py-1.5 rounded text-sm font-medium bg-accent hover:bg-accent-hover text-white disabled:opacity-50 flex items-center gap-2"
            >
              {isPending && <Loader2 className="w-3.5 h-3.5 animate-spin" />}
              Add {selected.size > 0 ? selected.size : ""}
            </button>
          </div>
        </div>

        <div className="px-5 py-3 border-b border-border">
          <input
            type="text"
            placeholder="Search images..."
            value={searchFilter.q ?? ""}
            onChange={(e) => setSearchFilter((f) => ({ ...f, q: e.target.value || undefined, page: 1 }))}
            className="w-full bg-input border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </div>

        <div className="flex-1 overflow-y-auto p-5">
          {available.length > 0 ? (
            <div className="grid grid-cols-4 sm:grid-cols-5 lg:grid-cols-6 gap-3">
              {available.map((image) => (
                <button
                  key={image.id}
                  onClick={() => setSelected((prev) => { const n = new Set(prev); n.has(image.id) ? n.delete(image.id) : n.add(image.id); return n; })}
                  className={`group overflow-hidden rounded-lg border text-left relative ${selected.has(image.id) ? "border-accent ring-2 ring-accent" : "border-border"}`}
                >
                  {selected.has(image.id) && (
                    <div className="absolute top-1 left-1 z-10">
                      <div className="w-5 h-5 rounded bg-accent flex items-center justify-center">
                        <Check className="w-3 h-3 text-white" />
                      </div>
                    </div>
                  )}
                  <div className="aspect-square overflow-hidden bg-surface">
                    <img src={images.thumbnailUrl(image.id)} alt={getImageDisplayTitle(image)} className="h-full w-full object-cover" loading="lazy" />
                  </div>
                  <div className="p-1.5">
                    <p className="truncate text-xs text-foreground">{getImageDisplayTitle(image)}</p>
                  </div>
                </button>
              ))}
            </div>
          ) : (
            <div className="text-center py-12 text-muted">No images available to add</div>
          )}
        </div>

        <div className="flex items-center justify-between px-5 py-3 border-t border-border">
          <div className="flex items-center gap-2">
            <button onClick={() => setSearchFilter((f) => ({ ...f, page: Math.max(1, (f.page ?? 1) - 1) }))} disabled={(searchFilter.page ?? 1) <= 1} className="px-2 py-1 rounded text-xs text-secondary hover:text-foreground disabled:opacity-30">Prev</button>
            <span className="text-xs text-muted">Page {searchFilter.page ?? 1}</span>
            <button onClick={() => setSearchFilter((f) => ({ ...f, page: (f.page ?? 1) + 1 }))} className="px-2 py-1 rounded text-xs text-secondary hover:text-foreground">Next</button>
          </div>
          <button onClick={onClose} className="px-3 py-1.5 rounded text-sm text-secondary hover:text-foreground">Cancel</button>
        </div>
      </div>
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
    <div className="rounded-xl border border-dashed border-border bg-card/40 py-12 text-center text-muted">
      <div className="mx-auto mb-3 flex justify-center opacity-60">{icon}</div>
      <p>{message}</p>
    </div>
  );
}

function GalleryFileInfo({ gallery }: { gallery: { folderPath?: string; files: { id: number; path: string; size: number; modTime: string; fingerprints: { type: string; value: string }[] }[] } }) {
  const hasFolder = !!gallery.folderPath;
  const hasFiles = gallery.files.length > 0;

  if (!hasFolder && !hasFiles) {
    return <EmptyPanel icon={<HardDrive className="h-8 w-8" />} message="No file information available" />;
  }

  return (
    <div className="space-y-4">
      {hasFolder && (
        <div className="rounded-xl border border-border bg-card p-4">
          <h3 className="mb-3 text-sm font-semibold uppercase tracking-wide text-muted">Folder</h3>
          <dl className="space-y-2 text-sm">
            <div>
              <dt className="text-muted">Path</dt>
              <dd className="font-mono text-xs text-foreground break-all">{gallery.folderPath}</dd>
            </div>
          </dl>
        </div>
      )}
      {gallery.files.map((file) => (
        <div key={file.id} className="rounded-xl border border-border bg-card p-4">
          <h3 className="mb-3 text-sm font-semibold uppercase tracking-wide text-muted">File</h3>
          <dl className="space-y-2 text-sm">
            <div>
              <dt className="text-muted">Path</dt>
              <dd className="font-mono text-xs text-foreground break-all">{file.path}</dd>
            </div>
            <div>
              <dt className="text-muted">Size</dt>
              <dd className="text-foreground">{formatFileSize(file.size)}</dd>
            </div>
            <div>
              <dt className="text-muted">Modified</dt>
              <dd className="text-foreground">{formatDate(file.modTime)}</dd>
            </div>
            {file.fingerprints.length > 0 && (
              <div>
                <dt className="text-muted mb-1">Fingerprints</dt>
                {file.fingerprints.map((fp, i) => (
                  <dd key={i} className="text-foreground">
                    <span className="text-muted text-xs uppercase">{fp.type}:</span>{" "}
                    <span className="font-mono text-xs break-all">{fp.value}</span>
                  </dd>
                ))}
              </div>
            )}
          </dl>
        </div>
      ))}
    </div>
  );
}
