import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { studios, entityImages } from "../api/client";
import type { FindFilter, Studio, StudioCreate, StudioFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { RatingBanner, RatingField } from "../components/Rating";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { Building2, Film, Image, LayoutGrid, Trash2, Loader2, Edit, Merge, Heart, Box, Users, Layers, Tag as TagIcon } from "lucide-react";
import { STUDIO_CRITERIA } from "../components/FilterDialog";
import { BulkEditDialog } from "../components/BulkEditDialog";
import { MergeDialog } from "../components/MergeDialog";
import { StudioTagger } from "../components/StudioTagger";
import { PopoverButton, ScenesPopoverContent, ImagesPopoverContent, PerformersPopoverContent, GalleriesPopoverContent, GroupsPopoverContent } from "../components/EntityCards";
import { getDefaultFilter } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { ExtensionSlot } from "../router/RouteRegistry";
import { useRouteRegistry } from "../router/RouteRegistry";
import { createCardNavigationHandlers } from "../components/cardNavigation";

const SORT_OPTIONS = [
  { value: "name", label: "Name" },
  { value: "scene_count", label: "Scene Count" },
  { value: "random", label: "Random" },
  { value: "created_at", label: "Created At" },
];

interface Props {
  onNavigate: (r: any) => void;
}

export function StudiosPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("studios");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "name", direction: "asc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: "grid" as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "studios",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "tagger"] as const,
  });
  const [showCreate, setShowCreate] = useState(false);
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [showMerge, setShowMerge] = useState(false);
  const queryClient = useQueryClient();

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading } = useQuery({
    queryKey: ["studios", filter, objectFilter],
    queryFn: () =>
      hasObjectFilter
        ? studios.findFiltered({ findFilter: filter, objectFilter: objectFilter as StudioFilterCriteria })
        : studios.find(filter),
  });

  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(items);
  const selecting = selectedIds.size > 0;

  const bulkDeleteMut = useMutation({
    mutationFn: () => studios.bulkDelete([...selectedIds]),
    onSuccess: () => { selectNone(); queryClient.invalidateQueries({ queryKey: ["studios"] }); },
  });

  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) =>
      studios.bulkUpdate({ ids: [...selectedIds], ...values } as any),
    onSuccess: () => {
      setShowBulkEdit(false);
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["studios"] });
    },
  });

  return (
    <>
      <StudioCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "studio", id })} />
      <ListPage
        title="Studios"
        filterMode="studios"
        filter={filter}
        onFilterChange={setFilter}
        totalCount={data?.totalCount ?? 0}
        isLoading={isLoading}
        sortOptions={SORT_OPTIONS}
        displayMode={displayMode}
        onDisplayModeChange={setDisplayMode}
        availableDisplayModes={["grid", "list", "tagger"]}
        criteriaDefinitions={STUDIO_CRITERIA}
        objectFilter={objectFilter}
        onObjectFilterChange={setObjectFilter}
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
              onClick={() => { if (confirm(`Delete ${selectedIds.size} studio(s)?`)) bulkDeleteMut.mutate(); }}
              disabled={bulkDeleteMut.isPending}
              className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-red-400 hover:text-red-300 hover:bg-red-900/20"
            >
              {bulkDeleteMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
              Delete
            </button>
          </>
        }
      >
      {displayMode === "tagger" ? (
        <StudioTagger studios={items} />
      ) : displayMode === "grid" ? (
        <div className="grid gap-3" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(var(--card-min-width, 200px), 1fr))" }}>
          {items.map((s) => (
            <StudioCard
              key={s.id}
              studio={s}
              onClick={() => selecting ? toggle(s.id) : onNavigate({ page: "studio", id: s.id })}
              onNavigate={onNavigate}
              selected={selectedIds.has(s.id)}
              onSelect={() => toggle(s.id)}
              selecting={selecting}
            />
          ))}
        </div>
      ) : (
        <StudioListTable studios={items} onNavigate={onNavigate} selectedIds={selectedIds} onToggle={toggle} selecting={selecting} />
      )}
      {items.length === 0 && (
        <div className="text-center text-secondary py-16">
          <Building2 className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <p>No studios found</p>
        </div>
      )}
      </ListPage>
      <BulkEditDialog
        open={showBulkEdit}
        onClose={() => setShowBulkEdit(false)}
        title="Edit Studios"
        selectedCount={selectedIds.size}
        fields={[{ key: "rating", label: "Rating", type: "number" }, { key: "favorite", label: "Favorite", type: "bool" }]}
        onApply={(values) => bulkEditMut.mutate(values)}
        isPending={bulkEditMut.isPending}
      />
      <MergeDialog
        open={showMerge}
        onClose={() => { setShowMerge(false); selectNone(); }}
        entityType="studio"
        items={items.filter((s) => selectedIds.has(s.id)).map((s) => ({ id: s.id, name: s.name }))}
        onMerge={studios.merge}
        queryKey="studios"
      />
    </>
  );
}

function StudioCard({ studio, onClick, onNavigate, selected, onSelect, selecting }: { studio: Studio; onClick: () => void; onNavigate?: (r: any) => void; selected?: boolean; onSelect?: () => void; selecting?: boolean }) {
  const { slots } = useRouteRegistry();
  const queryClient = useQueryClient();
  const hasExtensionFooter = slots.some((slot) => slot.slot === "studio-card-footer");
  const navigationHandlers = createCardNavigationHandlers<HTMLDivElement>({ page: "studio", id: studio.id }, onClick);
  const favMut = useMutation({
    mutationFn: () => studios.update(studio.id, { favorite: !studio.favorite }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["studios"] }),
  });

  return (
    <div {...navigationHandlers} className={`entity-card bg-card rounded overflow-hidden border hover:border-accent/60 transition-all cursor-pointer relative group ${selected ? "border-accent ring-2 ring-accent" : "border-border"}`}>
      <div className="aspect-video bg-surface flex items-center justify-center text-muted relative overflow-hidden">
        <div className={`absolute top-1 left-1 z-10 ${selected || selecting ? "opacity-100" : "opacity-0 group-hover:opacity-100"} transition-opacity`}>
          <input type="checkbox" checked={selected} onChange={(e) => { e.stopPropagation(); onSelect?.(); }} onClick={(e) => e.stopPropagation()} className="w-4 h-4 rounded border-border cursor-pointer accent-accent" />
        </div>
        {/* Favorite heart overlay */}
        <button
          onClick={(e) => { e.stopPropagation(); favMut.mutate(); }}
          className={`absolute top-1 right-1 p-1 z-10 transition-opacity ${studio.favorite ? "opacity-100" : "opacity-0 group-hover:opacity-70"}`}
          title={studio.favorite ? "Unfavorite" : "Favorite"}
        >
          <Heart className={`w-5 h-5 ${studio.favorite ? "fill-red-500 text-red-500" : "text-white drop-shadow-md"}`} />
        </button>
        {studio.imagePath ? (
          <img
            src={entityImages.studioImageUrl(studio.id, studio.updatedAt)}
            alt={studio.name}
            className="w-full h-full object-contain p-4"
            loading="lazy"
          />
        ) : (
          <Building2 className="w-10 h-10 opacity-30" />
        )}
        <RatingBanner rating={studio.rating} />
      </div>
      <div className="card-body border-t border-border/50 p-2 text-center">
        <h3 className="font-medium text-sm truncate text-foreground">{studio.name}</h3>
        {studio.parentName && (
          <div className="text-xs text-muted truncate">↑ {studio.parentName}</div>
        )}
      </div>
      {(studio.sceneCount > 0 || studio.imageCount > 0 || studio.galleryCount > 0 || studio.groupCount > 0 || studio.performerCount > 0 || studio.tags.length > 0 || studio.childStudioCount > 0 || studio.organized || hasExtensionFooter) && (
        <div className="flex items-center justify-center gap-1 px-2 pb-2 border-t border-border/50 pt-1.5 flex-wrap">
          {studio.sceneCount > 0 && (
            <PopoverButton icon={<Film className="w-3 h-3" />} count={studio.sceneCount} title="Scenes" wide preferBelow>
              <ScenesPopoverContent filter={{ studioId: studio.id }} />
            </PopoverButton>
          )}
          {studio.groupCount > 0 && (
            <PopoverButton icon={<Layers className="w-3 h-3" />} count={studio.groupCount} title="Groups" wide preferBelow>
              <GroupsPopoverContent filter={{ studioId: studio.id }} />
            </PopoverButton>
          )}
          {studio.imageCount > 0 && (
            <PopoverButton icon={<Image className="w-3 h-3" />} count={studio.imageCount} title="Images" wide preferBelow>
              <ImagesPopoverContent filter={{ studioId: studio.id }} />
            </PopoverButton>
          )}
          {studio.galleryCount > 0 && (
            <PopoverButton icon={<LayoutGrid className="w-3 h-3" />} count={studio.galleryCount} title="Galleries" wide preferBelow>
              <GalleriesPopoverContent filter={{ studioId: studio.id }} />
            </PopoverButton>
          )}
          {studio.performerCount > 0 && (
            <PopoverButton icon={<Users className="w-3 h-3" />} count={studio.performerCount} title="Performers" wide preferBelow>
              <PerformersPopoverContent filter={{ studioId: studio.id }} />
            </PopoverButton>
          )}
          {studio.tags.length > 0 && (
            <PopoverButton icon={<TagIcon className="w-3.5 h-3.5" />} count={studio.tags.length} title="Tags" preferBelow>
              <div className="flex flex-wrap gap-1">
                {studio.tags.map((t: any) => (
                  <button key={t.id} onClick={(e) => { e.stopPropagation(); onNavigate?.({ page: "tag", id: t.id }); }}
                    className="text-[11px] text-accent hover:underline cursor-pointer px-1.5 py-0.5 rounded bg-card border border-border hover:border-accent/40 transition-colors whitespace-nowrap">
                    {t.name}
                  </button>
                ))}
              </div>
            </PopoverButton>
          )}
          {studio.childStudioCount > 0 && (
            <span className="flex items-center gap-0.5 text-xs text-muted" title="Sub-Studios">
              <Building2 className="w-3 h-3" /> {studio.childStudioCount}
            </span>
          )}
          {studio.organized && (
            <span className="text-muted" title="Organized">
              <Box className="w-3 h-3" />
            </span>
          )}
          <ExtensionSlot slot="studio-card-footer" context={{ studio, onNavigate }} />
        </div>
      )}
    </div>
  );
}

function StudioListTable({ studios: items, onNavigate, selectedIds, onToggle, selecting }: { studios: Studio[]; onNavigate: (r: any) => void; selectedIds?: Set<number>; onToggle?: (id: number) => void; selecting?: boolean }) {
  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="border-b border-border text-left text-muted text-xs">
          {selectedIds && <th className="w-8 py-2 px-3"></th>}
          <th className="py-2 px-3">Name</th>
          <th className="py-2 px-3">Parent</th>
          <th className="py-2 px-3 text-right">Scenes</th>
          <th className="py-2 px-3 text-right">Rating</th>
        </tr>
      </thead>
      <tbody>
        {items.map((s) => (
          <tr
            key={s.id}
            onClick={() => selecting ? onToggle?.(s.id) : onNavigate({ page: "studio", id: s.id })}
            className={`border-b border-border hover:bg-card cursor-pointer ${selectedIds?.has(s.id) ? "bg-accent/10" : ""}`}
          >
            {selectedIds && <td className="py-2 px-3"><input type="checkbox" checked={selectedIds.has(s.id)} onChange={() => onToggle?.(s.id)} onClick={(e) => e.stopPropagation()} className="w-3.5 h-3.5 rounded border-border cursor-pointer accent-accent" /></td>}
            <td className="py-2 px-3 text-foreground">{s.name}</td>
            <td className="py-2 px-3 text-secondary">{s.parentName ?? ""}</td>
            <td className="py-2 px-3 text-secondary text-right">{s.sceneCount}</td>
            <td className="py-2 px-3 text-secondary text-right">{s.rating ?? ""}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/* ── Studio Create Modal ── */
function StudioCreateModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: number) => void }) {
  const qc = useQueryClient();
  const [form, setForm] = useState({
    name: "",
    details: "",
    rating: undefined as number | undefined,
    favorite: false,
    ignoreAutoTag: false,
    organized: false,
  });

  const mutation = useMutation({
    mutationFn: (data: StudioCreate) => studios.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["studios"] });
      setForm({ name: "", details: "", rating: undefined, favorite: false, ignoreAutoTag: false, organized: false });
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });

  const save = () => {
    const name = form.name.trim();
    if (!name) return;
    mutation.mutate({
      name,
      details: form.details || undefined,
      rating: form.rating,
      favorite: form.favorite || undefined,
      ignoreAutoTag: form.ignoreAutoTag || undefined,
      organized: form.organized || undefined,
    });
  };

  return (
    <EditModal title="Create Studio" open={open} onClose={onClose}>
      <Field label="Name">
        <TextInput value={form.name} onChange={(v) => setForm({ ...form, name: v })} />
      </Field>
      <Field label="Details">
        <TextArea value={form.details} onChange={(v) => setForm({ ...form, details: v })} rows={3} />
      </Field>
      <RatingField value={form.rating} onChange={(value) => setForm({ ...form, rating: value })} />
      <div className="flex items-center gap-4 mb-4">
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={form.favorite}
            onChange={(e) => setForm({ ...form, favorite: e.target.checked })}
            className="rounded bg-card border-border"
          />
          Favorite
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={form.ignoreAutoTag}
            onChange={(e) => setForm({ ...form, ignoreAutoTag: e.target.checked })}
            className="rounded bg-card border-border"
          />
          Ignore Auto Tag
        </label>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={form.organized}
            onChange={(e) => setForm({ ...form, organized: e.target.checked })}
            className="rounded bg-card border-border"
          />
          Organized
        </label>
      </div>
      <div className="flex justify-end mt-4">
        <SaveButton loading={mutation.isPending} onClick={save} />
      </div>
    </EditModal>
  );
}
