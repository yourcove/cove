import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { tags, tagGroups } from "../api/client";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import type { Tag, TagCreate, TagFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { toggleOptionsFromEvent, useMultiSelect, type MultiSelectToggleHandler } from "../hooks/useMultiSelect";
import { CreateModalActions, EditModal, Field, NumberInput, SelectInput, TextInput, TextArea } from "../components/EditModal";
import { Merge, Layers, Tag as TagIcon } from "lucide-react";
import { MergeDialog } from "../components/MergeDialog";
import { TagTile } from "../components/EntityCards";
import { getDefaultFilter } from "../components/SavedFilterMenu";
import { TAG_CRITERIA } from "../components/FilterDialog";
import { useListUrlState } from "../hooks/useListUrlState";
import { useInfiniteListData } from "../hooks/useInfiniteListData";
import { ExtensionSlot } from "../router/RouteRegistry";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canWriteEntity } from "../auth/visibility";
import { StringListEditor } from "../components/StringListEditor";
import { TagGraphView } from "../components/TagGraphView";
import { TagGroupsManager } from "../components/TagGroupsManager";
import { TagTagger } from "../components/TagTagger";
import { CustomFieldsEditor } from "../components/shared";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { VirtualizedEntityGrid } from "../components/VirtualizedEntityLayouts";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";

const GRAPH_VIEW_LIMIT = 5000;

function clampOptionalPercent(value: number | undefined) {
  if (value == null || !Number.isFinite(value)) return undefined;
  return Math.min(100, Math.max(0, value));
}

const SORT_OPTIONS = [
  { value: "name", label: "Name" },
  { value: "rating", label: "Rating" },
  { value: "tag_group", label: "Tag Group" },
  { value: "video_count", label: "Video Count" },
  { value: "gallery_count", label: "Gallery Count" },
  { value: "group_count", label: "Group Count" },
  { value: "image_count", label: "Image Count" },
  { value: "performer_count", label: "Performer Count" },
  { value: "studio_count", label: "Studio Count" },
  { value: "latest_video_date", label: "Latest Video Date" },
  { value: "total_file_size", label: "Total File Size" },
  { value: "random", label: "Random" },
  { value: "created_at", label: "Created At" },
  { value: "updated_at", label: "Updated At" },
];

interface Props {
  onNavigate: (r: any) => void;
}

export function TagsPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("tags");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "latest_video_date", direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: "grid" as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "tags",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "graph", "tagger"] as const,
    allowInfinitePageSize: true,
  });
  const [showCreate, setShowCreate] = useState(false);
  const [showTagGroups, setShowTagGroups] = useState(false);
  const [showMerge, setShowMerge] = useState(false);
  const [graphDeleteTarget, setGraphDeleteTarget] = useState<{ id: number; name: string } | null>(null);
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canWriteTag = canWriteEntity("tag", hasPermission);
  const canDeleteTag = canDeleteEntity("tag", hasPermission);

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const graphFindFilter = useMemo(
    () => ({ ...filter, page: 1, perPage: GRAPH_VIEW_LIMIT }),
    [filter],
  );
  const listData = useInfiniteListData<Tag>({
    queryKey: ["tags", filter, objectFilter],
    filter,
    chunkSize: defaultState.filter.perPage ?? 40,
    enabled: displayMode !== "graph",
    queryPage: (nextFilter) =>
      hasObjectFilter
        ? tags.findFiltered({ findFilter: nextFilter, objectFilter: objectFilter as TagFilterCriteria })
        : tags.find(nextFilter),
  });
  const { data: graphData, isLoading: isGraphLoading, error: graphError, refetch: refetchGraph } = useQuery({
    queryKey: ["tags", "graph", graphFindFilter, objectFilter],
    queryFn: () => tags.graph({ findFilter: graphFindFilter, objectFilter: objectFilter as TagFilterCriteria }),
    enabled: displayMode === "graph",
  });

  const items = listData.items;
  const totalCount = displayMode === "graph" ? graphData?.totalCount ?? 0 : listData.totalCount;
  const isLoading = displayMode === "graph" ? isGraphLoading : listData.isLoading;
  const { engagementById } = useEntityEngagementBatch("tag", items.map((item) => item.id));
  const selectionItems: Array<Pick<Tag, "id" | "name" | "imagePath">> = displayMode === "graph" ? graphData?.items ?? [] : items;
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: listData.infiniteFilterKey, objectFilter, displayMode }), [displayMode, listData.infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(selectionItems, { preserveOnItemsChange: displayMode !== "graph" && listData.infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await listData.fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };

  const deleteTagMut = useMutation({
    mutationFn: (id: number) => tags.delete(id),
    onSuccess: (_result, id) => {
      if (selectedIds.has(id)) {
        toggle(id);
      }
      queryClient.invalidateQueries({ queryKey: ["tags"] });
    },
  });

  return (
    <>
      <TagCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "tag", id })} />
      <TagGroupManagerDialog open={showTagGroups} onClose={() => setShowTagGroups(false)} />
      <ListPage
      title="Tags"
      pageKey="tags"
      filterMode="tags"
      filter={filter}
      onFilterChange={setFilter}
      totalCount={totalCount}
      isLoading={isLoading}
      error={displayMode === "graph" ? (!graphData && graphError instanceof Error ? graphError : null) : listData.loadError}
      onRetry={() => { void (displayMode === "graph" ? refetchGraph() : listData.refetch()); }}
      sortOptions={SORT_OPTIONS}
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={["grid", "list", "graph", "tagger"]}
      allowInfinitePageSize
      showPagingControls={displayMode === "graph" || !listData.infinitePageSize}
      selectAllPending={displayMode !== "graph" && listData.infinitePageSize ? selectAllMatchingPending : false}
      onSelectAllMatching={displayMode !== "graph" && listData.infinitePageSize ? selectAll : undefined}
      selectAllMatchingLabel="Select shown"
      infiniteScroll={displayMode !== "graph" ? listData.infiniteScroll : undefined}
      criteriaDefinitions={TAG_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      renderOperations={() => canWriteTag ? (
        <button
          type="button"
          onClick={() => setShowTagGroups(true)}
          className="inline-flex items-center gap-1.5 rounded-lg border border-border bg-card/70 px-3 py-1 text-xs text-secondary hover:border-accent hover:text-foreground"
        >
          <Layers className="h-3.5 w-3.5" />
          Groups
        </button>
      ) : null}
      onNew={canWriteTag ? () => setShowCreate(true) : undefined}
      selectedIds={selectedIds}
      onSelectAll={displayMode !== "graph" && listData.infinitePageSize ? handleSelectAllMatching : selectAll}
      onSelectNone={selectNone}
      onInvertSelection={invertSelection}
      selectionActions={(
        <>
          {canWriteTag && selectedIds.size >= 2 && (
            <button
              onClick={() => setShowMerge(true)}
              className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-yellow-400 hover:text-yellow-300 hover:bg-yellow-900/20"
            >
              <Merge className="w-3 h-3" />
              Merge
            </button>
          )}
          <BulkSelectionActions entityType="tags" selectedIds={selectedIds} onDone={selectNone} />
        </>
      )}
    >
      {displayMode === "tagger" ? (
        <TagTagger tags={items} selectedIds={selectedIds} selecting={selecting} onSelect={toggle} />
      ) : displayMode === "graph" ? (
        <TagGraphView
          nodes={graphData?.items ?? []}
          links={graphData?.links ?? []}
          totalCount={graphData?.totalCount ?? 0}
          onNavigate={onNavigate}
          isLoading={isGraphLoading}
          selectedIds={selectedIds}
          onToggleSelect={toggle}
          onDeleteNode={canDeleteTag ? (id) => {
            const tagName = selectionItems.find((item) => item.id === id)?.name ?? `#${id}`;
            setGraphDeleteTarget({ id, name: tagName });
          } : undefined}
        />
      ) : displayMode === "grid" ? (
        <VirtualizedEntityGrid
          items={items}
          getItemKey={(tag) => tag.id}
          minCardWidth="var(--card-min-width, 200px)"
          estimateRowHeight={300}
          infinitePageSize={listData.infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
          renderItem={(tag) => (
            <TagTile
              tag={tag}
              engagement={engagementById.get(tag.id)}
              onClick={(toggleOptions) => selecting ? toggle(tag.id, toggleOptions) : onNavigate({ page: "tag", id: tag.id })}
              onNavigate={onNavigate}
              selected={selectedIds.has(tag.id)}
              onSelect={(toggleOptions) => toggle(tag.id, toggleOptions)}
              selecting={selecting}
            >
              <ExtensionSlot slot="tag-card-footer" context={{ tag, onNavigate }} />
            </TagTile>
          )}
        />
      ) : (
        <RelatedEntityListView entityType="tags" items={items} displayMode="list" selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={listData.infinitePageSize} hasNextPage={listData.infiniteQuery.hasNextPage} isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage} loadMore={listData.loadMore} />
      )}
      {displayMode !== "graph" && items.length === 0 && (
        <div className="text-center text-secondary py-16">
          <TagIcon className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <p>No tags found</p>
        </div>
      )}
      </ListPage>
      <ConfirmDialog
        open={graphDeleteTarget != null}
        title="Delete Tag"
        message={graphDeleteTarget ? `Delete tag "${graphDeleteTarget.name}"? This cannot be undone.` : "Delete this tag?"}
        confirmLabel={deleteTagMut.isPending ? "Deleting..." : "Delete"}
        onConfirm={() => {
          if (graphDeleteTarget) {
            deleteTagMut.mutate(graphDeleteTarget.id, { onSuccess: () => setGraphDeleteTarget(null) });
          }
        }}
        onCancel={() => setGraphDeleteTarget(null)}
        isPending={deleteTagMut.isPending}
      />
      <MergeDialog
        open={showMerge}
        onClose={() => { setShowMerge(false); selectNone(); }}
        entityType="tag"
        items={selectionItems.filter((t) => selectedIds.has(t.id)).map((t) => ({ id: t.id, name: t.name, imagePath: t.imagePath }))}
        onMerge={tags.merge}
        queryKey="tags"
      />
    </>
  );
}

function TagGroupManagerDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <EditModal title="Tag Groups" open={open} onClose={onClose}>
      <TagGroupsManager framed={false} description="Create and edit the groups used by tag selectors and tag badges." />
      <div className="mt-4 flex justify-end">
        <button type="button" onClick={onClose} className="rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:text-foreground">Done</button>
      </div>
    </EditModal>
  );
}

/* ── Tag Create Modal ── */
export function TagCreateModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: number) => void }) {
  const qc = useQueryClient();
  const [form, setForm] = useState({ name: "", description: "", aliases: [] as string[], color: "", tagGroupId: undefined as number | undefined, minOccurrenceSec: undefined as number | undefined, minOccurrencePercent: undefined as number | undefined });
  const [selectedParentIds, setSelectedParentIds] = useState<number[]>([]);
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({});
  const [createAnother, setCreateAnother] = useState(false);
  const { data: groups = [] } = useQuery({ queryKey: ["tag-groups"], queryFn: tagGroups.list });
  const resetForm = () => {
    setForm({ name: "", description: "", aliases: [], color: "", tagGroupId: undefined, minOccurrenceSec: undefined, minOccurrencePercent: undefined });
    setSelectedParentIds([]);
    setCustomFields({});
  };
  const mutation = useMutation({
    mutationFn: (data: TagCreate) => tags.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["tags"] });
      resetForm();
      if (createAnother) return;
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });
  const handleClose = () => {
    mutation.reset();
    onClose();
  };
  return (
    <EditModal title="Create Tag" open={open} onClose={handleClose}>
      <Field label="Name">
        <TextInput value={form.name} onChange={(v) => setForm({ ...form, name: v })} />
      </Field>
      <Field label="Description">
        <TextArea value={form.description} onChange={(v) => setForm({ ...form, description: v })} rows={3} />
      </Field>
      <div className="grid gap-3 md:grid-cols-2">
        <Field label="Badge Color">
          <div className="flex items-center gap-2">
            <input
              type="color"
              value={/^#[0-9a-fA-F]{6}$/.test(form.color) ? form.color : "#6ee7b7"}
              onChange={(event) => setForm({ ...form, color: event.target.value })}
              className="h-9 w-11 rounded border border-border bg-card p-1"
            />
            <TextInput value={form.color} onChange={(v) => setForm({ ...form, color: v })} placeholder="#6ee7b7" />
          </div>
        </Field>
        <Field label="Tag Group">
          <SelectInput
            value={form.tagGroupId?.toString() ?? ""}
            onChange={(value) => setForm({ ...form, tagGroupId: value ? Number(value) : undefined })}
            options={groups.map((group) => ({ value: group.id.toString(), label: group.name }))}
          />
        </Field>
      </div>
      <div className="grid gap-3 md:grid-cols-2">
        <Field label="Min Seconds">
          <NumberInput value={form.minOccurrenceSec} onChange={(value) => setForm({ ...form, minOccurrenceSec: value })} min={0} />
        </Field>
        <Field label="Min Percent">
          <NumberInput value={form.minOccurrencePercent} onChange={(value) => setForm({ ...form, minOccurrencePercent: clampOptionalPercent(value) })} min={0} max={100} />
        </Field>
      </div>
      <Field label="Aliases">
        <StringListEditor
          values={form.aliases}
          onChange={(aliases) => setForm({ ...form, aliases })}
          placeholder="Alternate name"
          addLabel="Add Alias"
        />
      </Field>
      <Field label="Parent Tags">
        <EntityReferenceMultiSelector entityType="tag" values={selectedParentIds} onChange={setSelectedParentIds} placeholder="Search parent tags..." />
      </Field>
      <Field label="Custom Fields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} entityType="tag" />
      </Field>
      {mutation.error ? (
        <div role="alert" className="rounded-lg border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm text-red-200">
          {mutation.error.message}
        </div>
      ) : null}
      <CreateModalActions loading={mutation.isPending} createAnother={createAnother} onCreateAnotherChange={setCreateAnother} onSave={() => mutation.mutate({
          name: form.name,
          description: form.description || undefined,
          color: form.color.trim() || null,
          tagGroupId: form.tagGroupId ?? null,
          minOccurrenceSec: form.minOccurrenceSec ?? null,
          minOccurrencePercent: clampOptionalPercent(form.minOccurrencePercent) ?? null,
          aliases: form.aliases.map((alias) => alias.trim()).filter(Boolean),
          parentIds: selectedParentIds,
          customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
        })} />
    </EditModal>
  );
}

function TagListTable({ tags: items, onNavigate, selectedIds, onToggle, selecting }: { tags: Tag[]; onNavigate: (r: any) => void; selectedIds?: Set<number>; onToggle?: MultiSelectToggleHandler; selecting?: boolean }) {
  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="border-b border-border text-left text-muted text-xs">
          {selectedIds && <th className="w-8 py-2 px-3"></th>}
          <th className="py-2 px-3">Name</th>
          <th className="py-2 px-3">Group</th>
          <th className="py-2 px-3">Description</th>
          <th className="py-2 px-3">Aliases</th>
          <th className="py-2 px-3 text-right">Videos</th>
        </tr>
      </thead>
      <tbody>
        {items.map((t) => (
          <tr
            key={t.id}
            onClick={(event) => selecting ? onToggle?.(t.id, toggleOptionsFromEvent(event)) : onNavigate({ page: "tag", id: t.id })}
            className={`border-b border-border hover:bg-card cursor-pointer ${selectedIds?.has(t.id) ? "bg-accent/10" : ""}`}
          >
            {selectedIds && <td className="py-2 px-3"><input type="checkbox" checked={selectedIds.has(t.id)} onChange={() => {}} onClick={(event) => { event.stopPropagation(); onToggle?.(t.id, toggleOptionsFromEvent(event)); }} className="w-3.5 h-3.5 rounded border-border cursor-pointer accent-accent" /></td>}
            <td className="py-2 px-3 text-foreground">{t.name}</td>
            <td className="py-2 px-3 text-secondary">
              {t.tagGroupName ? (
                <span className="inline-flex max-w-[12rem] items-center gap-1.5 rounded-full border border-border bg-card px-2 py-0.5 text-xs">
                  <span className="h-2 w-2 rounded-full border border-border" style={{ backgroundColor: t.tagGroupColor ?? "transparent" }} />
                  <span className="truncate">{t.tagGroupName}</span>
                </span>
              ) : <span className="text-muted">Ungrouped</span>}
            </td>
            <td className="py-2 px-3 text-secondary truncate max-w-xs">{t.description ?? ""}</td>
            <td className="py-2 px-3 text-muted truncate max-w-xs">{t.aliases.join(", ")}</td>
            <td className="py-2 px-3 text-secondary text-right">{t.videoCount ?? ""}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
