import { useState, useMemo } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { images } from "../api/client";
import type { FindFilter, Image, ImageFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { RatingBanner } from "../components/Rating";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { ImageIcon, Users, Tag, Trash2, Loader2, Edit, Box, Heart, FolderOpen } from "lucide-react";
import { IMAGE_CRITERIA } from "../components/FilterDialog";
import { BulkEditDialog, IMAGE_BULK_FIELDS } from "../components/BulkEditDialog";
import { PopoverButton } from "../components/EntityCards";
import { Lightbox, type LightboxImage } from "../components/Lightbox";
import { ImageCreateModal } from "./ImageEditModal";
import { getDefaultFilter } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { createCardNavigationHandlers } from "../components/cardNavigation";

const SORT_OPTIONS = [
  { value: "updated_at", label: "Recently Updated" },
  { value: "created_at", label: "Recently Added" },
  { value: "title", label: "Title" },
  { value: "rating", label: "Rating" },
  { value: "o_counter", label: "Favorites" },
  { value: "random", label: "Random" },
];

interface Props {
  onNavigate: (r: any) => void;
}

export function ImagesPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("images");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: "grid" as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "images",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "wall"] as const,
  });
  const [showCreate, setShowCreate] = useState(false);
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [lightboxOpen, setLightboxOpen] = useState(false);
  const [lightboxIndex, setLightboxIndex] = useState(0);
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const queryClient = useQueryClient();

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading } = useQuery({
    queryKey: ["images", filter, objectFilter],
    queryFn: () =>
      hasObjectFilter
        ? images.findFiltered({ findFilter: filter, objectFilter: objectFilter as ImageFilterCriteria })
        : images.find(filter),
  });

  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(items);
  const selecting = selectedIds.size > 0;

  const lightboxImages: LightboxImage[] = useMemo(
    () => items.map((img) => ({ id: img.id, src: images.imageUrl(img.id), title: img.title })),
    [items],
  );

  const bulkDeleteMut = useMutation({
    mutationFn: () => images.bulkDelete([...selectedIds]),
    onSuccess: () => { selectNone(); queryClient.invalidateQueries({ queryKey: ["images"] }); },
  });

  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) =>
      images.bulkUpdate({ ids: [...selectedIds], ...values } as any),
    onSuccess: () => {
      setShowBulkEdit(false);
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["images"] });
    },
  });

  return (
    <>
    <ImageCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "image", id })} />
    <ListPage
      title="Images"
      filterMode="images"
      filter={filter}
      onFilterChange={setFilter}
      totalCount={data?.totalCount ?? 0}
      isLoading={isLoading}
      sortOptions={SORT_OPTIONS}
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={["grid", "wall"]}
      onNew={() => setShowCreate(true)}
      criteriaDefinitions={IMAGE_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}

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
            onClick={() => { if (confirm(`Delete ${selectedIds.size} image(s)?`)) bulkDeleteMut.mutate(); }}
            disabled={bulkDeleteMut.isPending}
            className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-red-400 hover:text-red-300 hover:bg-red-900/20"
          >
            {bulkDeleteMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
            Delete
          </button>
        </>
      }
    >
      {displayMode === "grid" ? (
        <div className="grid gap-3" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(var(--card-min-width, 140px), 1fr))" }}>
          {items.map((img, idx) => (
            <ImageCard
              key={img.id}
              image={img}
              onPreview={() => {
                if (selecting) { toggle(img.id); return; }
                setLightboxIndex(idx);
                setLightboxOpen(true);
              }}
              onDetails={() => {
                if (selecting) { toggle(img.id); return; }
                onNavigate({ page: "image", id: img.id });
              }}
              onNavigate={onNavigate}
              selected={selectedIds.has(img.id)}
              onSelect={() => toggle(img.id)}
              selecting={selecting}
              onQuickView={() => setQuickViewId(img.id)}
            />
          ))}
        </div>
      ) : (
        <div className="columns-2 sm:columns-3 md:columns-4 lg:columns-5 xl:columns-6 gap-2 space-y-2">
          {items.map((img) => (
            <ImageWallCard key={img.id} image={img} onClick={() => onNavigate({ page: "image", id: img.id })} />
          ))}
        </div>
      )}
      {items.length === 0 && (
        <div className="text-center text-secondary py-16">
          <ImageIcon className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <p>No images found</p>
        </div>
      )}
    </ListPage>
    <BulkEditDialog
      open={showBulkEdit}
      onClose={() => setShowBulkEdit(false)}
      title="Edit Images"
      selectedCount={selectedIds.size}
      fields={IMAGE_BULK_FIELDS}
      onApply={(values) => bulkEditMut.mutate(values)}
      isPending={bulkEditMut.isPending}
    />
    <Lightbox
      images={lightboxImages}
      initialIndex={lightboxIndex}
      open={lightboxOpen}
      onClose={() => setLightboxOpen(false)}
    />
    {quickViewId !== null && (
      <QuickViewDialog type="image" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
    )}
    </>
  );
}

function ImageCard({ image, onPreview, onDetails, onNavigate, selected, onSelect, selecting, onQuickView }: { image: Image; onPreview: () => void; onDetails: () => void; onNavigate?: (r: any) => void; selected?: boolean; onSelect?: () => void; selecting?: boolean; onQuickView?: () => void }) {
  const thumbnailUrl = images.thumbnailUrl(image.id);

  return (
    <div
      className={`entity-card bg-card rounded overflow-hidden cursor-pointer border hover:border-accent/60 transition-colors group relative ${selected ? "border-accent ring-2 ring-accent" : "border-border"}`}
    >
      <div className="aspect-square bg-surface relative overflow-hidden" onClick={onPreview}>
        <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
          <input type="checkbox" checked={selected} onChange={(e) => { e.stopPropagation(); onSelect?.(); }} onClick={(e) => e.stopPropagation()} className="w-4 h-4 rounded border-border cursor-pointer accent-accent" />
        </div>
        <img
          src={thumbnailUrl}
          alt={image.title || "Image"}
          className="w-full h-full object-cover group-hover:scale-105 transition-transform duration-300"
          loading="lazy"
          onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }}
        />
        <RatingBanner rating={image.rating} />
        {onQuickView && (
          <button
            onClick={(e) => { e.stopPropagation(); onQuickView(); }}
            className="absolute bottom-1 left-1 z-10 opacity-0 group-hover:opacity-100 transition-opacity p-1 rounded bg-black/60 text-white hover:bg-black/80"
            title="Quick View"
          >
            <ImageIcon className="w-3.5 h-3.5" />
          </button>
        )}
        {image.studioName && (
          <div className="absolute top-1 right-1 text-xs bg-black/70 px-1 py-0.5 rounded text-white truncate max-w-[80%]">
            {image.studioName}
          </div>
        )}
      </div>
      <div className="card-body border-t border-border/50 p-1.5" onClick={onDetails}>
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">
          {image.title || "Untitled"}
        </p>
      </div>
      {(image.performers.length > 0 || image.tags.length > 0 || image.oCounter > 0 || image.galleryCount > 0 || image.organized) && (
        <div className="flex items-center justify-center gap-1 px-1.5 pb-1.5 border-t border-border/50 pt-1">
          {image.tags.length > 0 && (
            <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={image.tags.length} title="Tags" preferBelow>
              <div className="flex flex-wrap gap-1">
                {image.tags.map((t: any) => (
                  <button key={t.id} onClick={(e) => { e.stopPropagation(); onNavigate?.({ page: "tag", id: t.id }); }}
                    className="text-[11px] text-accent hover:underline cursor-pointer px-1.5 py-0.5 rounded bg-card border border-border hover:border-accent/40 transition-colors whitespace-nowrap">
                    {t.name}
                  </button>
                ))}
              </div>
            </PopoverButton>
          )}
          {image.performers.length > 0 && (
            <PopoverButton icon={<Users className="w-3.5 h-3.5" />} count={image.performers.length} title="Performers" wide preferBelow>
              <div className="grid grid-cols-2 gap-2">
                {image.performers.map((p: any) => (
                  <button key={p.id} onClick={(e) => { e.stopPropagation(); onNavigate?.({ page: "performer", id: p.id }); }}
                    className="flex flex-col items-center gap-1.5 text-center cursor-pointer rounded hover:bg-card-hover p-1.5 group/perf transition-colors">
                    <span className="text-xs text-accent group-hover/perf:underline truncate w-full font-medium">{p.name}</span>
                  </button>
                ))}
              </div>
            </PopoverButton>
          )}
          {image.oCounter > 0 && (
            <span className="flex items-center gap-0.5 text-xs text-muted" title="Favorites">
              <Heart className="w-3 h-3" /> {image.oCounter}
            </span>
          )}
          {image.galleryCount > 0 && (
            <span className="flex items-center gap-0.5 text-xs text-muted" title="Galleries">
              <FolderOpen className="w-3 h-3" /> {image.galleryCount}
            </span>
          )}
          {image.organized && (
            <span className="text-muted" title="Organized">
              <Box className="w-2.5 h-2.5" />
            </span>
          )}
        </div>
      )}
    </div>
  );
}

function ImageWallCard({ image, onClick }: { image: Image; onClick: () => void }) {
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "image", id: image.id }, onClick);

  return (
    <div
      {...navigationHandlers}
      className="break-inside-avoid cursor-pointer rounded overflow-hidden border border-border hover:border-accent/60 transition-all"
    >
      <img
        src={images.thumbnailUrl(image.id)}
        alt={image.title || "Image"}
        className="w-full object-cover"
        loading="lazy"
      />
    </div>
  );
}
