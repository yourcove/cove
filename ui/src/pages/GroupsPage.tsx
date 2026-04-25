import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { groups } from "../api/client";
import type { FindFilter, Group, GroupCreate, GroupFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { RatingBanner, RatingField } from "../components/Rating";
import { EditModal, Field, TextInput, TextArea, SaveButton } from "../components/EditModal";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { formatDate } from "../components/shared";
import { Layers, Film, Trash2, Loader2, Edit, Tag as TagIcon, FolderTree, FolderUp } from "lucide-react";
import { PopoverButton, ScenesPopoverContent } from "../components/EntityCards";
import { GROUP_CRITERIA } from "../components/FilterDialog";
import { BulkEditDialog, GROUP_BULK_FIELDS } from "../components/BulkEditDialog";
import { getDefaultFilter } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { ExtensionSlot } from "../router/RouteRegistry";
import { useRouteRegistry } from "../router/RouteRegistry";
import { createNestedRouteLinkProps } from "../components/cardNavigation";
import { CardSelectionToggle, RouteCardLinkOverlay } from "../components/RouteCardLinkOverlay";

const SORT_OPTIONS = [
  { value: "name", label: "Name" },
  { value: "date", label: "Date" },
  { value: "rating", label: "Rating" },
  { value: "random", label: "Random" },
  { value: "created_at", label: "Created At" },
];

interface Props {
  onNavigate: (r: any) => void;
}

export function GroupsPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("groups");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "name", direction: "asc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: "grid" as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "groups",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list"] as const,
  });
  const [showCreate, setShowCreate] = useState(false);
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const queryClient = useQueryClient();

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading } = useQuery({
    queryKey: ["groups", filter, objectFilter],
    queryFn: () =>
      hasObjectFilter
        ? groups.findFiltered({ findFilter: filter, objectFilter: objectFilter as GroupFilterCriteria })
        : groups.find(filter),
  });

  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(items);
  const selecting = selectedIds.size > 0;

  const bulkDeleteMut = useMutation({
    mutationFn: () => groups.bulkDelete([...selectedIds]),
    onSuccess: () => { selectNone(); queryClient.invalidateQueries({ queryKey: ["groups"] }); },
  });

  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) =>
      groups.bulkUpdate({ ids: [...selectedIds], ...values } as any),
    onSuccess: () => {
      setShowBulkEdit(false);
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["groups"] });
    },
  });

  return (
    <>
      <GroupCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "group", id })} />
      <ListPage
        title="Groups"
        pageKey="groups"
        filterMode="groups"
        filter={filter}
        onFilterChange={setFilter}
        totalCount={data?.totalCount ?? 0}
        isLoading={isLoading}
        sortOptions={SORT_OPTIONS}
        displayMode={displayMode}
        onDisplayModeChange={setDisplayMode}
        availableDisplayModes={["grid", "list"]}
        criteriaDefinitions={GROUP_CRITERIA}
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
            <button
              onClick={() => { if (confirm(`Delete ${selectedIds.size} group(s)?`)) bulkDeleteMut.mutate(); }}
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
        <div className="grid gap-3" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(var(--card-min-width, 160px), 1fr))" }}>
          {items.map((g) => (
            <GroupCard
              key={g.id}
              group={g}
              onClick={() => selecting ? toggle(g.id) : onNavigate({ page: "group", id: g.id })}
              onNavigate={onNavigate}
              selected={selectedIds.has(g.id)}
              onSelect={() => toggle(g.id)}
              selecting={selecting}
            />
          ))}
        </div>
      ) : (
        <GroupListTable groups={items} onNavigate={onNavigate} selectedIds={selectedIds} onToggle={toggle} selecting={selecting} />
      )}
      {items.length === 0 && (
        <div className="text-center text-secondary py-16">
          <Layers className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <p>No groups found</p>
        </div>
      )}
      </ListPage>
      <BulkEditDialog
        open={showBulkEdit}
        onClose={() => setShowBulkEdit(false)}
        title="Edit Groups"
        selectedCount={selectedIds.size}
        fields={GROUP_BULK_FIELDS}
        onApply={(values) => bulkEditMut.mutate(values)}
        isPending={bulkEditMut.isPending}
      />
    </>
  );
}

function GroupCard({ group, onClick, onNavigate, selected, onSelect, selecting }: { group: Group; onClick: () => void; onNavigate?: (r: any) => void; selected?: boolean; onSelect?: () => void; selecting?: boolean }) {
  const { slots } = useRouteRegistry();
  const hasExtensionFooter = slots.some((slot) => slot.slot === "group-card-footer");
  return (
    <div onClick={selecting ? onClick : undefined} className={`entity-card bg-card rounded overflow-hidden border hover:border-accent/60 transition-all cursor-pointer group relative ${selected ? "border-accent ring-2 ring-accent" : "border-border"}`}>
      <RouteCardLinkOverlay route={{ page: "group", id: group.id }} onClick={onClick} label={`Open group ${group.name}`} disabled={selecting} selectionSafeZone={selected !== undefined || selecting} />
      {/* Movie poster style - 2:3 aspect ratio */}
      <div className="aspect-[2/3] bg-surface flex items-center justify-center relative overflow-hidden">
        <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
        {group.frontImagePath ? (
          <img src={group.frontImagePath} alt={group.name} className="w-full h-full object-cover" loading="lazy" />
        ) : (
          <Layers className="w-10 h-10 text-muted opacity-30" />
        )}
        <RatingBanner rating={group.rating} />
        {/* Studio overlay */}
        {group.studioName && (
          <div className="absolute top-1 right-1 text-xs bg-black/70 px-1.5 py-0.5 rounded text-white truncate max-w-[80%]">
            {group.studioName}
          </div>
        )}
      </div>
      <div className="card-body bg-card border-t border-border p-2 text-center">
        <h3 className="font-medium text-sm truncate text-foreground">{group.name}</h3>
        <div className="flex items-center justify-center gap-2 text-xs text-secondary mt-0.5">
          {group.date && <span>{group.date}</span>}
          {group.director && <span>Dir: {group.director}</span>}
        </div>
      </div>
      {(group.sceneCount > 0 || group.subGroupCount > 0 || group.containingGroupCount > 0 || group.tags.length > 0 || hasExtensionFooter) && (
        <div className="relative z-10 flex items-center justify-center gap-2 px-2 pb-2 border-t border-border pt-1.5">
          {group.sceneCount > 0 && (
            <PopoverButton icon={<Film className="w-3 h-3" />} count={group.sceneCount} title="Scenes" wide preferBelow>
              <ScenesPopoverContent filter={{ groupId: group.id }} />
            </PopoverButton>
          )}
          {group.tags.length > 0 && (
            <PopoverButton icon={<TagIcon className="w-3.5 h-3.5" />} count={group.tags.length} title="Tags" preferBelow>
              <div className="flex flex-wrap gap-1">
                {group.tags.map((t: any) => {
                  const linkProps = createNestedRouteLinkProps<HTMLAnchorElement>({ page: "tag", id: t.id }, () => onNavigate?.({ page: "tag", id: t.id }));

                  return <a key={t.id} {...linkProps}
                    className="text-[11px] text-accent hover:underline cursor-pointer px-1.5 py-0.5 rounded bg-card border border-border hover:border-accent/40 transition-colors whitespace-nowrap">
                    {t.name}
                  </a>;
                })}
              </div>
            </PopoverButton>
          )}
          {group.subGroupCount > 0 && (
            <span className="flex items-center gap-0.5 text-xs text-muted" title="Sub-groups">
              <FolderTree className="w-3 h-3" /> {group.subGroupCount}
            </span>
          )}
          {group.containingGroupCount > 0 && (
            <span className="flex items-center gap-0.5 text-xs text-muted" title="Containing groups">
              <FolderUp className="w-3 h-3" /> {group.containingGroupCount}
            </span>
          )}
          <ExtensionSlot slot="group-card-footer" context={{ group, onNavigate }} />
        </div>
      )}
    </div>
  );
}

function GroupListTable({ groups: items, onNavigate, selectedIds, onToggle, selecting }: { groups: Group[]; onNavigate: (r: any) => void; selectedIds?: Set<number>; onToggle?: (id: number) => void; selecting?: boolean }) {
  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="border-b border-border text-left text-muted text-xs">
          {selectedIds && <th className="w-8 py-2 px-3"></th>}
          <th className="py-2 px-3">Name</th>
          <th className="py-2 px-3">Studio</th>
          <th className="py-2 px-3">Director</th>
          <th className="py-2 px-3">Date</th>
          <th className="py-2 px-3 text-right">Scenes</th>
          <th className="py-2 px-3 text-right">Rating</th>
        </tr>
      </thead>
      <tbody>
        {items.map((g) => (
          <tr
            key={g.id}
            onClick={() => selecting ? onToggle?.(g.id) : onNavigate({ page: "group", id: g.id })}
            className={`border-b border-border hover:bg-card cursor-pointer ${selectedIds?.has(g.id) ? "bg-accent/10" : ""}`}
          >
            {selectedIds && <td className="py-2 px-3"><input type="checkbox" checked={selectedIds.has(g.id)} onChange={() => onToggle?.(g.id)} onClick={(e) => e.stopPropagation()} className="w-3.5 h-3.5 rounded border-border cursor-pointer accent-accent" /></td>}
            <td className="py-2 px-3 text-foreground">{g.name}</td>
            <td className="py-2 px-3 text-secondary">{g.studioName ?? ""}</td>
            <td className="py-2 px-3 text-secondary">{g.director ?? ""}</td>
            <td className="py-2 px-3 text-secondary">{g.date ? formatDate(g.date) : ""}</td>
            <td className="py-2 px-3 text-secondary text-right">{g.sceneCount}</td>
            <td className="py-2 px-3 text-secondary text-right">{g.rating ?? ""}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/* ── Group Create Modal ── */
function GroupCreateModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: number) => void }) {
  const qc = useQueryClient();
  const [form, setForm] = useState({
    name: "",
    date: "",
    director: "",
    synopsis: "",
    rating: undefined as number | undefined,
  });

  const mutation = useMutation({
    mutationFn: (data: GroupCreate) => groups.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["groups"] });
      setForm({ name: "", date: "", director: "", synopsis: "", rating: undefined });
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });

  const save = () => {
    const name = form.name.trim();
    if (!name) return;
    mutation.mutate({
      name,
      date: form.date || undefined,
      director: form.director || undefined,
      synopsis: form.synopsis || undefined,
      rating: form.rating,
    });
  };

  return (
    <EditModal title="Create Group" open={open} onClose={onClose}>
      <Field label="Name">
        <TextInput value={form.name} onChange={(v) => setForm({ ...form, name: v })} />
      </Field>
      <Field label="Date">
        <TextInput value={form.date} onChange={(v) => setForm({ ...form, date: v })} placeholder="YYYY-MM-DD" />
      </Field>
      <Field label="Director">
        <TextInput value={form.director} onChange={(v) => setForm({ ...form, director: v })} />
      </Field>
      <Field label="Synopsis">
        <TextArea value={form.synopsis} onChange={(v) => setForm({ ...form, synopsis: v })} rows={3} />
      </Field>
      <RatingField value={form.rating} onChange={(value) => setForm({ ...form, rating: value })} />
      <div className="flex justify-end mt-4">
        <SaveButton loading={mutation.isPending} onClick={save} />
      </div>
    </EditModal>
  );
}
