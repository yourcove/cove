import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { galleries } from "../api/client";
import type { FindFilter, Gallery, GalleryCreate, GalleryFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { InteractiveRatingField, RatingBanner } from "../components/Rating";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { FolderOpen, Image, Users, Tag, Trash2, Loader2, Edit, Box, Film, Check } from "lucide-react";
import { PopoverButton, ScenesPopoverContent, ImagesPopoverContent } from "../components/EntityCards";
import { GALLERY_CRITERIA } from "../components/FilterDialog";
import { BulkEditDialog, GALLERY_BULK_FIELDS } from "../components/BulkEditDialog";
import { getDefaultFilter } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { createCardNavigationHandlers } from "../components/cardNavigation";
import { useWallColumns } from "../hooks/useWallColumns";
import { GALLERY_SORT_OPTIONS } from "../components/gallerySortOptions";
import { WallMediaCard } from "../components/WallMediaCard";

interface Props {
  onNavigate: (r: any) => void;
}

export function GalleriesPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("galleries");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: "grid" as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "galleries",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "wall"] as const,
  });
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [showCreate, setShowCreate] = useState(false);
  const [wallColumnCount, setWallColumnCount] = useState(5);
  const queryClient = useQueryClient();

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading } = useQuery({
    queryKey: ["galleries", filter, objectFilter],
    queryFn: () =>
      hasObjectFilter
        ? galleries.findFiltered({ findFilter: filter, objectFilter: objectFilter as GalleryFilterCriteria })
        : galleries.find(filter),
  });

  const items = data?.items ?? [];
  const wallColumns = useWallColumns(items, wallColumnCount);
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(items);
  const selecting = selectedIds.size > 0;

  const bulkDeleteMut = useMutation({
    mutationFn: () => galleries.bulkDelete([...selectedIds]),
    onSuccess: () => { selectNone(); queryClient.invalidateQueries({ queryKey: ["galleries"] }); },
  });

  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) =>
      galleries.bulkUpdate({ ids: [...selectedIds], ...values } as any),
    onSuccess: () => {
      setShowBulkEdit(false);
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["galleries"] });
    },
  });

  return (
    <>
    <GalleryCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "gallery", id })} />
    <ListPage
      title="Galleries"
      filterMode="galleries"
      filter={filter}
      onFilterChange={setFilter}
      totalCount={data?.totalCount ?? 0}
      isLoading={isLoading}
      sortOptions={GALLERY_SORT_OPTIONS}
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={["grid", "list", "wall"]}
      criteriaDefinitions={GALLERY_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      onNew={() => setShowCreate(true)}
      wallColumnCount={wallColumnCount}
      onWallColumnCountChange={setWallColumnCount}

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
            onClick={() => { if (confirm(`Delete ${selectedIds.size} gallery(s)?`)) bulkDeleteMut.mutate(); }}
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
        <div className="grid gap-3" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(var(--card-min-width, 200px), 1fr))" }}>
          {items.map((g) => (
            <GalleryCard key={g.id} gallery={g} onClick={() => selecting ? toggle(g.id) : onNavigate({ page: "gallery", id: g.id })} onNavigate={onNavigate} selected={selectedIds.has(g.id)} onSelect={() => toggle(g.id)} selecting={selecting} />
          ))}
        </div>
      ) : displayMode === "list" ? (
        <GalleryListTable galleries={items} onNavigate={onNavigate} selectedIds={selectedIds} onToggle={toggle} selecting={selecting} />
      ) : (
        <div className="flex gap-2 px-2">
          {wallColumns.map((column, columnIndex) => (
            <div key={columnIndex} className="flex min-w-0 flex-1 flex-col gap-2">
              {column.map((gallery) => (
                <GalleryWallCard
                  key={gallery.id}
                  gallery={gallery}
                  onClick={() => selecting ? toggle(gallery.id) : onNavigate({ page: "gallery", id: gallery.id })}
                  selected={selectedIds.has(gallery.id)}
                  onSelect={() => toggle(gallery.id)}
                  selecting={selecting}
                />
              ))}
            </div>
          ))}
        </div>
      )}
      {items.length === 0 && (
        <div className="text-center text-secondary py-16">
          <FolderOpen className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <p>No galleries found</p>
        </div>
      )}
    </ListPage>
    <BulkEditDialog
      open={showBulkEdit}
      onClose={() => setShowBulkEdit(false)}
      title="Edit Galleries"
      selectedCount={selectedIds.size}
      fields={GALLERY_BULK_FIELDS}
      onApply={(values) => bulkEditMut.mutate(values)}
      isPending={bulkEditMut.isPending}
    />
    </>
  );
}

function GalleryWallCard({ gallery, onClick, selected, onSelect, selecting }: { gallery: Gallery; onClick: () => void; selected?: boolean; onSelect?: () => void; selecting?: boolean }) {
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "gallery", id: gallery.id }, onClick);

  return (
    <WallMediaCard
      {...navigationHandlers}
      title={gallery.title || "Untitled"}
      imageSrc={gallery.coverPath}
      aspectRatio="16 / 9"
      fallback={<FolderOpen className="w-10 h-10 text-muted opacity-30" />}
      className={`${selected ? "border-accent ring-2 ring-accent" : ""} group`.trim()}
    >
      <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
        <input
          type="checkbox"
          checked={selected}
          onChange={(event) => {
            event.stopPropagation();
            onSelect?.();
          }}
          onClick={(event) => event.stopPropagation()}
          className="w-4 h-4 rounded border-border cursor-pointer accent-accent"
        />
      </div>
      <RatingBanner rating={gallery.rating} />
      {gallery.studioName && (
        <div className="absolute top-1 right-1 text-xs bg-black/70 px-1.5 py-0.5 rounded text-white truncate max-w-[80%]">
          {gallery.studioName}
        </div>
      )}
    </WallMediaCard>
  );
}

function GalleryCard({ gallery, onClick, onNavigate, selected, onSelect, selecting }: { gallery: Gallery; onClick: () => void; onNavigate?: (r: any) => void; selected?: boolean; onSelect?: () => void; selecting?: boolean }) {
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "gallery", id: gallery.id }, onClick);

  return (
    <div {...navigationHandlers} className={`entity-card bg-card rounded overflow-hidden border hover:border-accent/60 transition-all cursor-pointer group relative ${selected ? "border-accent ring-2 ring-accent" : "border-border"}`}>
      <div className="aspect-video bg-surface flex items-center justify-center relative overflow-hidden">
        <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
          <input type="checkbox" checked={selected} onChange={(e) => { e.stopPropagation(); onSelect?.(); }} onClick={(e) => e.stopPropagation()} className="w-4 h-4 rounded border-border cursor-pointer accent-accent" />
        </div>
        {gallery.coverPath ? (
          <img src={gallery.coverPath} alt={gallery.title || ""} className="w-full h-full object-cover" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} />
        ) : (
          <FolderOpen className="w-10 h-10 text-muted opacity-30" />
        )}
        <RatingBanner rating={gallery.rating} />
        {gallery.studioName && (
          <div className="absolute top-1 right-1 text-xs bg-black/70 px-1.5 py-0.5 rounded text-white truncate max-w-[80%]">
            {gallery.studioName}
          </div>
        )}
      </div>
      <div className="card-body border-t border-border/50 p-2">
        <h3 className="font-medium text-sm truncate text-foreground">{gallery.title || "Untitled"}</h3>
        {gallery.date && <div className="text-xs text-secondary">{gallery.date}</div>}
      </div>
      <GalleryCardPopovers gallery={gallery} onNavigate={onNavigate} />
    </div>
  );
}

function GalleryCardPopovers({ gallery, onNavigate }: { gallery: Gallery; onNavigate?: (r: any) => void }) {
  const hasAny = gallery.imageCount > 0 || gallery.performers.length > 0 || gallery.tags.length > 0 || gallery.sceneCount > 0 || gallery.organized;
  if (!hasAny) return null;

  return (
    <div className="flex items-center justify-center gap-1 px-2 pb-2 border-t border-border/50 pt-1.5">
      {gallery.imageCount > 0 && (
        <PopoverButton icon={<Image className="w-3 h-3" />} count={gallery.imageCount} title="Images" wide preferBelow>
          <ImagesPopoverContent filter={{ galleryId: gallery.id }} />
        </PopoverButton>
      )}
      {gallery.tags.length > 0 && (
        <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={gallery.tags.length} title="Tags" preferBelow>
          <div className="flex flex-wrap gap-1">
            {gallery.tags.map((t: any) => (
              <button key={t.id} onClick={(e) => { e.stopPropagation(); onNavigate?.({ page: "tag", id: t.id }); }}
                className="text-[11px] text-accent hover:underline cursor-pointer px-1.5 py-0.5 rounded bg-card border border-border hover:border-accent/40 transition-colors whitespace-nowrap">
                {t.name}
              </button>
            ))}
          </div>
        </PopoverButton>
      )}
      {gallery.performers.length > 0 && (
        <PopoverButton icon={<Users className="w-3.5 h-3.5" />} count={gallery.performers.length} title="Performers" wide preferBelow>
          <div className="grid grid-cols-2 gap-2">
            {gallery.performers.map((p: any) => (
              <button key={p.id} onClick={(e) => { e.stopPropagation(); onNavigate?.({ page: "performer", id: p.id }); }}
                className="flex flex-col items-center gap-1 text-center cursor-pointer rounded hover:bg-card-hover p-1.5 transition-colors">
                <span className="text-xs text-accent hover:underline truncate w-full">{p.name}</span>
              </button>
            ))}
          </div>
        </PopoverButton>
      )}
      {gallery.sceneCount > 0 && (
        <PopoverButton icon={<Film className="w-3 h-3" />} count={gallery.sceneCount} title="Scenes" wide preferBelow>
          <ScenesPopoverContent filter={{ galleryId: gallery.id }} />
        </PopoverButton>
      )}
      {gallery.organized && (
        <span className="text-muted" title="Organized">
          <Box className="w-3 h-3" />
        </span>
      )}
    </div>
  );
}

function GalleryListTable({ galleries: items, onNavigate, selectedIds, onToggle, selecting }: { galleries: Gallery[]; onNavigate: (r: any) => void; selectedIds?: Set<number>; onToggle?: (id: number) => void; selecting?: boolean }) {
  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="border-b border-border text-left text-muted text-xs">
          {selectedIds && <th className="w-8 py-2 px-3"></th>}
          <th className="py-2 px-3">Title</th>
          <th className="py-2 px-3">Studio</th>
          <th className="py-2 px-3">Date</th>
          <th className="py-2 px-3 text-right">Images</th>
          <th className="py-2 px-3 text-right">Rating</th>
        </tr>
      </thead>
      <tbody>
        {items.map((g) => (
          <tr key={g.id} onClick={() => selecting ? onToggle?.(g.id) : onNavigate({ page: "gallery", id: g.id })} className={`border-b border-border hover:bg-card cursor-pointer ${selectedIds?.has(g.id) ? "bg-accent/10" : ""}`}>
            {selectedIds && <td className="py-2 px-3"><input type="checkbox" checked={selectedIds.has(g.id)} onChange={() => onToggle?.(g.id)} onClick={(e) => e.stopPropagation()} className="w-3.5 h-3.5 rounded border-border cursor-pointer accent-accent" /></td>}
            <td className="py-2 px-3 text-foreground">{g.title || "Untitled"}</td>
            <td className="py-2 px-3 text-secondary">{g.studioName ?? ""}</td>
            <td className="py-2 px-3 text-secondary">{g.date ?? ""}</td>
            <td className="py-2 px-3 text-secondary text-right">{g.imageCount}</td>
            <td className="py-2 px-3 text-secondary text-right">{g.rating ?? ""}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/* ── Gallery Create Modal ── */
function GalleryCreateModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: number) => void }) {
  const qc = useQueryClient();
  const [form, setForm] = useState({
    title: "",
    code: "",
    date: "",
    details: "",
    photographer: "",
    rating: undefined as number | undefined,
  });

  const mutation = useMutation({
    mutationFn: (data: GalleryCreate) => galleries.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["galleries"] });
      setForm({ title: "", code: "", date: "", details: "", photographer: "", rating: undefined });
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });

  const save = () => {
    const title = form.title.trim();
    if (!title) return;
    mutation.mutate({
      title,
      code: form.code || undefined,
      date: form.date || undefined,
      details: form.details || undefined,
      photographer: form.photographer || undefined,
      rating: form.rating,
    });
  };

  return (
    <EditModal title="Create Gallery" open={open} onClose={onClose}>
      <Field label="Title">
        <TextInput value={form.title} onChange={(v) => setForm({ ...form, title: v })} />
      </Field>
      <Field label="Studio Code">
        <TextInput value={form.code} onChange={(v) => setForm({ ...form, code: v })} />
      </Field>
      <Field label="Date">
        <TextInput value={form.date} onChange={(v) => setForm({ ...form, date: v })} placeholder="YYYY-MM-DD" />
      </Field>
      <Field label="Photographer">
        <TextInput value={form.photographer} onChange={(v) => setForm({ ...form, photographer: v })} />
      </Field>
      <Field label="Details">
        <TextArea value={form.details} onChange={(v) => setForm({ ...form, details: v })} rows={3} />
      </Field>
      <InteractiveRatingField value={form.rating} onChange={(value) => setForm({ ...form, rating: value })} />
      <div className="flex justify-end mt-4">
        <SaveButton loading={mutation.isPending} onClick={save} />
      </div>
    </EditModal>
  );
}
