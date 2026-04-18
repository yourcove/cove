import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { groups, scenes } from "../api/client";
import type { FindFilter, Group, Scene } from "../api/types";
import { formatDate, formatDuration, getResolutionLabel, TagBadge, CustomFieldsDisplay } from "../components/shared";
import { ArrowLeft, ChevronDown, ChevronUp, Clapperboard, Film, Layers, Link as LinkIcon, Loader2, Pencil, Plus, Trash2, X } from "lucide-react";
import { useEffect, useState } from "react";
import { GroupEditModal } from "./GroupEditModal";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { ExtensionSlot } from "../router/RouteRegistry";
import { GroupTile, SceneCard } from "../components/EntityCards";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { DetailListToolbar } from "../components/DetailListToolbar";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { BulkSelectionActions } from "../components/BulkSelectionActions";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type TabKey = "scenes" | "subGroups" | "containingGroups";

export function GroupDetailPage({ id, onNavigate }: Props) {
  const { data: group, isLoading } = useQuery({
    queryKey: ["group", id],
    queryFn: () => groups.get(id),
  });
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [activeTab, setActiveTab] = useState<TabKey>("scenes");
  const [sceneFilter, setSceneFilter] = useState<FindFilter>({ page: 1, perPage: 24, direction: "asc", sort: "date" });
  const queryClient = useQueryClient();

  useEffect(() => {
    if (group) document.title = `${group.name} | Cove`;
    return () => { document.title = "Cove"; };
  }, [group]);

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const el = (e.target as HTMLElement).tagName;
      if (el === "INPUT" || el === "TEXTAREA" || el === "SELECT") return;
      switch (e.key) {
        case "e": setEditing((v) => !v); break;
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);

  const deleteMut = useMutation({
    mutationFn: () => groups.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["groups"] });
      onNavigate({ page: "groups" });
    },
  });

  const tabs: { key: TabKey; label: string; count?: number }[] = [
    { key: "scenes", label: "Scenes", count: group?.sceneCount },
    { key: "subGroups", label: "Sub-Groups", count: group?.subGroupCount },
    { key: "containingGroups", label: "Containing Groups", count: group?.containingGroupCount },
  ];

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
      </div>
    );
  }

  if (!group) {
    return <div className="py-16 text-center text-secondary">Group not found</div>;
  }

  return (
    <div className="min-h-screen">
      <div className="relative overflow-hidden border-b border-border detail-hero-gradient">
        <div className="mx-auto max-w-7xl px-4 py-8">
          <div className="mb-5 flex items-center justify-between gap-4">
            <button
              onClick={() => onNavigate({ page: "groups" })}
              className="flex items-center gap-1 text-sm text-secondary hover:text-foreground"
            >
              <ArrowLeft className="h-4 w-4" /> Back to groups
            </button>
            <div className="flex items-center gap-2">
              <ExtensionSlot slot="group-detail-actions" context={{ group, onNavigate }} />
              <button
                onClick={() => setEditing(true)}
                className="flex items-center gap-1.5 rounded bg-accent px-3 py-1.5 text-sm text-white hover:bg-accent-hover"
              >
                <Pencil className="h-3.5 w-3.5" /> Edit
              </button>
              <button
                onClick={() => setConfirmDelete(true)}
                className="flex items-center gap-1.5 rounded border border-border bg-card px-3 py-1.5 text-sm text-secondary hover:border-red-500 hover:text-red-300"
              >
                <Trash2 className="h-3.5 w-3.5" /> Delete
              </button>
            </div>
          </div>

          <div className="flex flex-col gap-6 md:flex-row md:items-end">
            <div className="flex flex-shrink-0 gap-3">
              {group.frontImagePath ? (
                <img
                  src={group.frontImagePath}
                  alt={`${group.name} front cover`}
                  className="h-40 w-auto max-w-[200px] rounded-xl border border-border object-cover shadow-xl shadow-black/35"
                />
              ) : (
                <div className="flex h-40 w-28 items-center justify-center rounded-xl border border-border bg-card shadow-xl shadow-black/35">
                  <Layers className="h-14 w-14 text-accent" />
                </div>
              )}
              {group.backImagePath && (
                <img
                  src={group.backImagePath}
                  alt={`${group.name} back cover`}
                  className="h-40 w-auto max-w-[200px] rounded-xl border border-border object-cover shadow-xl shadow-black/35"
                />
              )}
            </div>
            <div className="min-w-0 flex-1">
              <h1 className="truncate text-2xl sm:text-3xl md:text-4xl font-bold text-foreground">{group.name}</h1>
              <div className="mt-2 flex flex-wrap items-center gap-3 text-sm text-secondary">
                {group.aliases && <span>Aliases: {group.aliases}</span>}
                {group.date && <span>{formatDate(group.date)}</span>}
                {group.director && <span>Director: {group.director}</span>}
                {group.duration && <span className="flex items-center gap-1"><Clapperboard className="h-4 w-4" /> {formatDuration(group.duration)}</span>}
                {group.studioName && group.studioId && (
                  <button onClick={() => onNavigate({ page: "studio", id: group.studioId })} className="text-accent hover:underline">
                    {group.studioName}
                  </button>
                )}
              </div>
              {group.synopsis && (
                <p className="mt-3 max-w-4xl whitespace-pre-wrap text-sm leading-6 text-secondary">{group.synopsis}</p>
              )}
              {group.tags.length > 0 && (
                <div className="mt-4 flex flex-wrap gap-1.5">
                  {group.tags.map((tag) => (
                    <TagBadge key={tag.id} name={tag.name} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Tabs */}
          <div className="mx-auto max-w-7xl mt-6 flex gap-1 border-b border-border">
            {tabs.map((tab) => (
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

      <GroupEditModal group={group} open={editing} onClose={() => setEditing(false)} />
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Group"
        message={`Delete "${group.name}"? This cannot be undone.`}
        onConfirm={() => deleteMut.mutate()}
        onCancel={() => setConfirmDelete(false)}
      />

      <div className="px-4 py-6">
        <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_320px]">
          <div className="min-w-0">
            {activeTab === "scenes" && (
              <GroupScenesPanel groupId={id} filter={sceneFilter} setFilter={setSceneFilter} onNavigate={onNavigate} />
            )}
            {activeTab === "subGroups" && (
              <GroupSubGroupsPanel groupId={id} onNavigate={onNavigate} />
            )}
            {activeTab === "containingGroups" && (
              <GroupContainingGroupsPanel groupId={id} onNavigate={onNavigate} />
            )}

            <ExtensionSlot slot="group-detail-main-bottom" context={{ group, onNavigate }} />
          </div>

          <aside className="space-y-4">
            <div className="rounded-xl border border-border bg-card p-4">
              <h2 className="mb-3 text-sm font-semibold uppercase tracking-wide text-muted">Metadata</h2>
              <dl className="space-y-2 text-sm">
                <div>
                  <dt className="text-muted">Scene Count</dt>
                  <dd className="text-foreground">{group.sceneCount}</dd>
                </div>
                <div>
                  <dt className="text-muted">Created</dt>
                  <dd className="text-foreground">{formatDate(group.createdAt)}</dd>
                </div>
                <div>
                  <dt className="text-muted">Updated</dt>
                  <dd className="text-foreground">{formatDate(group.updatedAt)}</dd>
                </div>
              </dl>
            </div>

            {group.urls.length > 0 && (
              <div className="rounded-xl border border-border bg-card p-4">
                <h2 className="mb-3 flex items-center gap-1.5 text-sm font-semibold uppercase tracking-wide text-muted">
                  <LinkIcon className="h-4 w-4" /> URLs
                </h2>
                <div className="space-y-1 text-sm">
                  {group.urls.map((url, index) => (
                    <a key={index} href={url} target="_blank" rel="noopener noreferrer" className="block truncate text-accent hover:underline">
                      {url}
                    </a>
                  ))}
                </div>
              </div>
            )}

            <CustomFieldsDisplay customFields={group.customFields} />
            <ExtensionSlot slot="group-detail-sidebar-bottom" context={{ group, onNavigate }} />
          </aside>
        </div>

        <ExtensionSlot slot="group-detail-bottom" context={{ group, onNavigate }} />
      </div>
    </div>
  );
}

function GroupScenesPanel({ groupId, filter, setFilter, onNavigate }: {
  groupId: number;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const { data: groupScenes, isLoading } = useQuery({
    queryKey: ["group-scenes", groupId, filter],
    queryFn: () => scenes.find(filter, { groupId: String(groupId) }),
  });
  const { selectedIds, toggle, selectAll, selectNone } = useMultiSelect(groupScenes?.items ?? []);
  const selecting = selectedIds.size > 0;

  if (isLoading) return <LoadingPanel icon={<Film className="h-10 w-10" />} message="Loading scenes..." />;
  if (!groupScenes || groupScenes.items.length === 0) return <EmptyPanel icon={<Film className="h-12 w-12" />} message="No scenes in this group" />;

  return (
    <>
      <DetailListToolbar
        filter={filter}
        onFilterChange={setFilter}
        totalCount={groupScenes.totalCount}
        sortOptions={[
          { value: "title", label: "Title" },
          { value: "date", label: "Date" },
          { value: "rating", label: "Rating" },
          { value: "created_at", label: "Created" },
        ]}
        zoomLevel={zoomLevel}
        onZoomChange={setZoomLevel}
        showSearch
        selectedCount={selectedIds.size}
        onSelectAll={selectAll}
        onSelectNone={selectNone}
        selectionActions={<BulkSelectionActions entityType="scenes" selectedIds={selectedIds} onDone={selectNone} sceneItems={groupScenes.items} onNavigate={onNavigate} />}
      />
      <div className="grid gap-4" style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${220 + zoomLevel * 50}px, 1fr))` }}>
        {groupScenes.items.map((scene) => (
          <SceneCard key={scene.id} scene={scene} onClick={() => selecting ? toggle(scene.id) : onNavigate({ page: "scene", id: scene.id })} onNavigate={onNavigate} onQuickView={() => setQuickViewId(scene.id)} selected={selectedIds.has(scene.id)} onSelect={() => toggle(scene.id)} selecting={selecting} />
        ))}
      </div>
      {quickViewId !== null && (
        <QuickViewDialog type="scene" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function GroupSubGroupsPanel({ groupId, onNavigate }: { groupId: number; onNavigate: (r: any) => void }) {
  const queryClient = useQueryClient();
  const { data: subGroups, isLoading } = useQuery({
    queryKey: ["group-subgroups", groupId],
    queryFn: () => groups.subGroups(groupId),
  });
  const [showAddDialog, setShowAddDialog] = useState(false);
  const [searchTerm, setSearchTerm] = useState("");

  const { data: searchResults } = useQuery({
    queryKey: ["groups-search-for-subgroup", searchTerm],
    queryFn: () => groups.find({ page: 1, perPage: 20, q: searchTerm }),
    enabled: showAddDialog && searchTerm.length > 0,
  });

  const addMut = useMutation({
    mutationFn: (subGroupId: number) => groups.addSubGroup(groupId, subGroupId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["group-subgroups", groupId] }),
  });

  const removeMut = useMutation({
    mutationFn: (subGroupId: number) => groups.removeSubGroup(groupId, subGroupId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["group-subgroups", groupId] }),
  });

  const reorderMut = useMutation({
    mutationFn: (ids: number[]) => groups.reorderSubGroups(groupId, ids),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["group-subgroups", groupId] }),
  });

  const moveUp = (idx: number) => {
    if (!subGroups || idx <= 0) return;
    const ids = subGroups.map((g) => g.id);
    [ids[idx - 1], ids[idx]] = [ids[idx], ids[idx - 1]];
    reorderMut.mutate(ids);
  };

  const moveDown = (idx: number) => {
    if (!subGroups || idx >= subGroups.length - 1) return;
    const ids = subGroups.map((g) => g.id);
    [ids[idx], ids[idx + 1]] = [ids[idx + 1], ids[idx]];
    reorderMut.mutate(ids);
  };

  const existingIds = new Set(subGroups?.map((g) => g.id) ?? []);
  const availableResults = (searchResults?.items ?? []).filter((g) => g.id !== groupId && !existingIds.has(g.id));

  if (isLoading) return <LoadingPanel icon={<Layers className="h-10 w-10" />} message="Loading sub-groups..." />;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold text-muted uppercase tracking-wider">Sub-Groups</h3>
        <button
          onClick={() => setShowAddDialog(!showAddDialog)}
          className="flex items-center gap-1 px-2 py-1 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10 border border-border"
        >
          <Plus className="w-3 h-3" />
          Add Sub-Group
        </button>
      </div>

      {/* Add sub-group search */}
      {showAddDialog && (
        <div className="rounded-xl border border-border bg-card p-4">
          <div className="flex items-center gap-2 mb-3">
            <input
              type="text"
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              placeholder="Search groups to add..."
              className="flex-1 bg-input border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
              autoFocus
            />
            <button onClick={() => { setShowAddDialog(false); setSearchTerm(""); }} className="p-1.5 rounded hover:bg-surface text-muted"><X className="w-4 h-4" /></button>
          </div>
          {availableResults.length > 0 ? (
            <div className="space-y-1 max-h-48 overflow-y-auto">
              {availableResults.map((g) => (
                <button
                  key={g.id}
                  onClick={() => addMut.mutate(g.id)}
                  disabled={addMut.isPending}
                  className="w-full flex items-center justify-between px-3 py-2 rounded text-left text-sm hover:bg-surface text-foreground"
                >
                  <span>{g.name}</span>
                  <Plus className="w-3.5 h-3.5 text-muted" />
                </button>
              ))}
            </div>
          ) : searchTerm.length > 0 ? (
            <p className="text-sm text-muted text-center py-4">No groups found</p>
          ) : (
            <p className="text-sm text-muted text-center py-4">Type to search for groups</p>
          )}
        </div>
      )}

      {subGroups && subGroups.length > 0 ? (
        <div className="space-y-2">
          {subGroups.map((g, idx) => (
            <div key={g.id} className="flex items-center gap-3 rounded-xl border border-border bg-card px-4 py-3 group">
              <div className="flex flex-col gap-0.5">
                <button onClick={() => moveUp(idx)} disabled={idx === 0} className="p-0.5 rounded hover:bg-surface disabled:opacity-20 text-muted"><ChevronUp className="w-3.5 h-3.5" /></button>
                <button onClick={() => moveDown(idx)} disabled={idx === subGroups.length - 1} className="p-0.5 rounded hover:bg-surface disabled:opacity-20 text-muted"><ChevronDown className="w-3.5 h-3.5" /></button>
              </div>
              <span className="text-xs text-muted w-6 text-center">{idx + 1}</span>
              <button onClick={() => onNavigate({ page: "group", id: g.id })} className="flex-1 text-left text-sm font-medium text-foreground hover:text-accent">{g.name}</button>
              <span className="text-xs text-muted">{g.sceneCount} scenes</span>
              <button
                onClick={() => { if (confirm(`Remove "${g.name}" from sub-groups?`)) removeMut.mutate(g.id); }}
                className="opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-red-900/20 text-muted hover:text-red-400"
              >
                <X className="w-3.5 h-3.5" />
              </button>
            </div>
          ))}
        </div>
      ) : (
        <EmptyPanel icon={<Layers className="h-12 w-12" />} message="No sub-groups" />
      )}
    </div>
  );
}

function GroupContainingGroupsPanel({ groupId, onNavigate }: { groupId: number; onNavigate: (r: any) => void }) {
  const { data: containingGroups, isLoading } = useQuery({
    queryKey: ["group-containinggroups", groupId],
    queryFn: () => groups.containingGroups(groupId),
  });

  if (isLoading) return <LoadingPanel icon={<Layers className="h-10 w-10" />} message="Loading containing groups..." />;
  if (!containingGroups || containingGroups.length === 0) return <EmptyPanel icon={<Layers className="h-12 w-12" />} message="No containing groups" />;

  return (
    <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5">
      {containingGroups.map((g) => (
        <GroupTile key={g.id} group={g} onClick={() => onNavigate({ page: "group", id: g.id })} />
      ))}
    </div>
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
    <div className="rounded-xl border border-dashed border-border bg-card/40 py-12 text-center text-muted">
      <div className="mx-auto mb-3 flex justify-center opacity-60">{icon}</div>
      <p>{message}</p>
    </div>
  );
}