import { useCallback, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { groups } from "../api/client";
import type { FindFilter, Group, GroupCreate, GroupFilterCriteria, PaginatedResponse } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { SortableList } from "../components/SortableList";
import { CreateModalActions, EditModal, Field, TextInput, TextArea } from "../components/EditModal";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { Layers, Trash2, Loader2, Edit } from "lucide-react";
import { GroupTile } from "../components/EntityCards";
import { GROUP_CRITERIA } from "../components/filterCriteriaCatalogs";
import { IsoDateInput } from "../components/IsoDateInput";
import { BulkEditDialog, GROUP_BULK_FIELDS } from "../components/BulkEditDialog";
import { getDefaultFilter, resolveSavedDisplayMode } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { useInfiniteListData } from "../hooks/useInfiniteListData";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canWriteEntity } from "../auth/visibility";
import { CustomFieldsEditor } from "../components/shared";
import { DynamicGroupFilterEditor, FILTER_DYNAMIC_SOURCE_KEY, defaultDynamicGroupFilterQueryJson, isProtectedBuiltInGroup } from "../components/DynamicGroupFilterEditor";
import { ScraperEntityTagger } from "../components/ScraperEntityTagger";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { VirtualizedEntityGrid } from "../components/VirtualizedEntityLayouts";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";
import { GROUP_SORT_OPTIONS } from "../components/groupSortOptions";

const SORT_OPTIONS = GROUP_SORT_OPTIONS;

interface Props {
  onNavigate: (r: any) => void;
}

export function GroupsPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("groups");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "date", direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: resolveSavedDisplayMode(savedFilter?.uiOptions, ["grid", "list", "tagger"] as const, "grid") as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "groups",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "tagger"] as const,
    allowInfinitePageSize: true,
  });
  const [showCreate, setShowCreate] = useState(false);
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canWriteGroup = canWriteEntity("group", hasPermission);
  const canDeleteGroup = canDeleteEntity("group", hasPermission);

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const queryGroupsPage = useCallback((nextFilter: FindFilter) =>
    hasObjectFilter
      ? groups.findFiltered({ findFilter: nextFilter, objectFilter: objectFilter as GroupFilterCriteria })
      : groups.find(nextFilter),
  [hasObjectFilter, objectFilter]);
  const listData = useInfiniteListData<Group>({
    queryKey: ["groups", filter, objectFilter],
    filter,
    chunkSize: defaultState.filter.perPage ?? 40,
    queryPage: queryGroupsPage,
  });

  const items = listData.items;
  const totalCount = listData.totalCount;
  const isLoading = listData.isLoading;
  const manualOrderingEnabled = !listData.infinitePageSize && displayMode === "grid" && !hasObjectFilter && !filter.q && (filter.sort ?? "sort_order") === "sort_order" && (filter.direction ?? "asc") !== "desc";
  const { engagementById } = useEntityEngagementBatch("group", items.map((item) => item.id));
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: listData.infiniteFilterKey, objectFilter }), [listData.infiniteFilterKey, objectFilter]);
  // Built-in/system groups (Save for Later, Watch History, Continue Watching) can't be deleted,
  // so they must not be selectable for bulk actions.
  const protectedBuiltInGroupIds = useMemo(
    () => new Set(items.filter((group) => isProtectedBuiltInGroup(group.querySourceKey)).map((group) => group.id)),
    [items],
  );
  const isSelectableGroupId = useCallback((id: number) => !protectedBuiltInGroupIds.has(id), [protectedBuiltInGroupIds]);
  const isSelectableGroupItem = useCallback((group: Group) => !isProtectedBuiltInGroup(group.querySourceKey), []);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(items, { preserveOnItemsChange: listData.infinitePageSize, resetKey: selectionResetKey, isSelectable: isSelectableGroupItem, isSelectableId: isSelectableGroupId });
  const selecting = selectedIds.size > 0;
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      const ids: number[] = [];
      const seen = new Set<number>();
      let page = 1;
      let totalCount = Number.POSITIVE_INFINITY;
      while (ids.length < totalCount) {
        const response = await queryGroupsPage({ ...filter, page, perPage: 1000 });
        totalCount = response.totalCount;
        for (const group of response.items) {
          if (seen.has(group.id)) continue;
          seen.add(group.id);
          if (!isProtectedBuiltInGroup(group.querySourceKey)) {
            ids.push(group.id);
          }
        }
        if (response.items.length === 0 || response.page * response.perPage >= response.totalCount) {
          break;
        }
        page += 1;
      }
      selectIds(ids);
    } finally {
      setSelectAllMatchingPending(false);
    }
  };

  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) =>
      groups.bulkUpdate({ ids: [...selectedIds], ...values } as any),
    onSuccess: () => {
      setShowBulkEdit(false);
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["groups"] });
    },
  });

  const reorderMut = useMutation({
    mutationFn: (nextItems: Group[]) => groups.reorder({ ids: nextItems.map((item) => item.id), startIndex: ((filter.page ?? 1) - 1) * (filter.perPage ?? 40) }),
    onMutate: async (nextItems) => {
      await queryClient.cancelQueries({ queryKey: ["groups", filter, objectFilter] });
      const previousData = queryClient.getQueryData<PaginatedResponse<Group>>(["groups", filter, objectFilter]);
      if (previousData) {
        queryClient.setQueryData<PaginatedResponse<Group>>(["groups", filter, objectFilter], { ...previousData, items: nextItems });
      }
      return { previousData };
    },
    onError: (_error, _nextItems, context) => {
      if (context?.previousData) {
        queryClient.setQueryData(["groups", filter, objectFilter], context.previousData);
      }
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: ["groups"] }),
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
        totalCount={totalCount}
        isLoading={isLoading}
        error={listData.loadError}
        onRetry={() => { void listData.refetch(); }}
        sortOptions={SORT_OPTIONS}
        displayMode={displayMode}
        onDisplayModeChange={setDisplayMode}
        availableDisplayModes={["grid", "list", "tagger"]}
        allowInfinitePageSize
        showPagingControls={!listData.infinitePageSize}
        selectAllPending={listData.infinitePageSize ? selectAllMatchingPending : false}
        onSelectAllMatching={listData.infinitePageSize ? selectAll : undefined}
        selectAllMatchingLabel="Select shown"
        infiniteScroll={listData.infiniteScroll}
        criteriaDefinitions={GROUP_CRITERIA}
        objectFilter={objectFilter}
        onObjectFilterChange={setObjectFilter}
        onNew={canWriteGroup ? () => setShowCreate(true) : undefined}
        selectedIds={selectedIds}
        onSelectAll={listData.infinitePageSize ? handleSelectAllMatching : selectAll}
        onSelectNone={selectNone}
        onInvertSelection={invertSelection}
        selectionActions={<BulkSelectionActions entityType="groups" selectedIds={selectedIds} onDone={selectNone} />}
      >
      {displayMode === "tagger" ? (
        <ScraperEntityTagger
          entityType="group"
          label="Group"
          items={items}
          selectedIds={selectedIds}
          selecting={selecting}
          onSelect={toggle}
          getTitle={(group) => group.name}
          getImageUrl={(group) => group.frontImagePath}
          getRoute={(group) => ({ page: "group", id: group.id })}
          queryKey="groups"
        />
      ) : displayMode === "grid" ? (
        manualOrderingEnabled ? (
          <SortableList
            items={items}
            getKey={(group) => group.id}
            onReorder={(nextItems) => reorderMut.mutate(nextItems)}
            disabled={!canWriteGroup || selecting || reorderMut.isPending}
            className="grid gap-3"
            style={{ gridTemplateColumns: "repeat(auto-fill, minmax(var(--card-min-width, 160px), 1fr))" }}
            renderItem={(g, { dragHandleProps, isDragging, isOver }) => (
              <GroupTile
                group={g}
                engagement={engagementById.get(g.id)}
                onClick={(toggleOptions) => selecting && isSelectableGroupItem(g) ? toggle(g.id, toggleOptions) : onNavigate({ page: "group", id: g.id })}
                onNavigate={onNavigate}
                selected={selectedIds.has(g.id)}
                onSelect={(toggleOptions) => toggle(g.id, toggleOptions)}
                selecting={selecting}
                selectable={isSelectableGroupItem(g)}
                dragHandleProps={canWriteGroup ? dragHandleProps : undefined}
                isDragging={isDragging}
                isOver={isOver}
              />
            )}
          />
        ) : (
          <VirtualizedEntityGrid
            items={items}
            getItemKey={(group) => group.id}
            minCardWidth="var(--card-min-width, 160px)"
            estimateRowHeight={280}
            infinitePageSize={listData.infinitePageSize}
            hasNextPage={listData.infiniteQuery.hasNextPage}
            isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
            loadMore={listData.loadMore}
            renderItem={(g) => (
              <GroupTile
                group={g}
                engagement={engagementById.get(g.id)}
                onClick={(toggleOptions) => selecting && isSelectableGroupItem(g) ? toggle(g.id, toggleOptions) : onNavigate({ page: "group", id: g.id })}
                onNavigate={onNavigate}
                selected={selectedIds.has(g.id)}
                onSelect={(toggleOptions) => toggle(g.id, toggleOptions)}
                selecting={selecting}
                selectable={isSelectableGroupItem(g)}
              />
            )}
          />
        )
      ) : (
        <RelatedEntityListView entityType="groups" items={items} displayMode="list" selectedIds={selectedIds} selecting={selecting} onToggle={toggle} isSelectable={isSelectableGroupItem} onNavigate={onNavigate} infinitePageSize={listData.infinitePageSize} hasNextPage={listData.infiniteQuery.hasNextPage} isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage} loadMore={listData.loadMore} />
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

/* â”€â”€ Group Create Modal â”€â”€ */
function GroupCreateModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: number) => void }) {
  const qc = useQueryClient();
  const { data: dynamicSources = [] } = useQuery({
    queryKey: ["group-dynamic-sources"],
    queryFn: () => groups.dynamicSources(),
    enabled: open,
  });
  const dynamicSourceOptions = useMemo(() => {
    const filterSource = dynamicSources.find((source) => source.key === FILTER_DYNAMIC_SOURCE_KEY);
    return filterSource ? [filterSource] : dynamicSources;
  }, [dynamicSources]);
  const defaultDynamicSourceKey = dynamicSourceOptions[0]?.key ?? FILTER_DYNAMIC_SOURCE_KEY;
  const [form, setForm] = useState({
    name: "",
    date: "",
    director: "",
    description: "",
    kind: "static" as "static" | "dynamic",
    querySourceKey: FILTER_DYNAMIC_SOURCE_KEY,
    queryJson: defaultDynamicGroupFilterQueryJson(),
  });
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({});
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const [parentGroupIds, setParentGroupIds] = useState<number[]>([]);
  const [createAnother, setCreateAnother] = useState(false);

  const resetForm = () => {
    setForm({ name: "", date: "", director: "", description: "", kind: "static", querySourceKey: defaultDynamicSourceKey, queryJson: defaultDynamicGroupFilterQueryJson() });
    setParentGroupIds([]);
    setCustomFields({});
  };

  const mutation = useMutation({
    mutationFn: async ({ data, parentIds }: { data: GroupCreate; parentIds: number[] }) => {
      const created = await groups.create(data);
      if (created?.id) {
        for (const parentId of parentIds) {
          await groups.addSubGroup(parentId, created.id);
        }
      }
      return created;
    },
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["groups"] });
      for (const parentId of parentGroupIds) {
        qc.invalidateQueries({ queryKey: ["group-subgroups", parentId] });
      }
      if (created?.id) qc.invalidateQueries({ queryKey: ["group-containinggroups", created.id] });
      resetForm();
      if (createAnother) return;
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });

  const save = () => {
    const name = form.name.trim();
    if (!name) return;
    mutation.mutate({
      data: {
        name,
        date: form.date || undefined,
        director: form.director || undefined,
        description: form.description || undefined,
        customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
        kind: form.kind,
        querySourceKey: form.kind === "dynamic" ? form.querySourceKey : undefined,
        queryJson: form.kind === "dynamic" && form.querySourceKey === FILTER_DYNAMIC_SOURCE_KEY ? form.queryJson : undefined,
      },
      parentIds: parentGroupIds,
    });
  };

  return (
    <EditModal title="Create Group" open={open} onClose={onClose}>
      <Field label="Name">
        <TextInput value={form.name} onChange={(v) => setForm({ ...form, name: v })} />
      </Field>
      <Field label="Kind">
        <div className="inline-flex rounded-lg border border-border bg-card p-1">
          {(["static", "dynamic"] as const).map((kind) => (
            <button
              key={kind}
              type="button"
              onClick={() => setForm({ ...form, kind, querySourceKey: kind === "dynamic" ? (form.querySourceKey || defaultDynamicSourceKey) : form.querySourceKey, queryJson: form.queryJson || defaultDynamicGroupFilterQueryJson() })}
              className={`rounded-md px-3 py-1.5 text-sm capitalize transition-colors ${form.kind === kind ? "bg-accent text-white" : "text-secondary hover:text-foreground"}`}
            >
              {kind}
            </button>
          ))}
        </div>
      </Field>
      {form.kind === "dynamic" ? (
        <div className="grid grid-cols-1 gap-4">
          <Field label="Source">
            <select
              value={form.querySourceKey}
              onChange={(event) => setForm({ ...form, querySourceKey: event.target.value, queryJson: event.target.value === FILTER_DYNAMIC_SOURCE_KEY ? (form.queryJson || defaultDynamicGroupFilterQueryJson()) : form.queryJson })}
              className="w-full rounded border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
            >
              {dynamicSourceOptions.map((source) => (
                <option key={source.key} value={source.key}>{source.displayName}</option>
              ))}
            </select>
          </Field>
        </div>
      ) : null}
      {form.kind === "dynamic" && form.querySourceKey === FILTER_DYNAMIC_SOURCE_KEY ? (
        <DynamicGroupFilterEditor queryJson={form.queryJson} onChange={(queryJson) => setForm({ ...form, queryJson })} />
      ) : null}
      <Field label="Date">
        <IsoDateInput value={form.date} onChange={(event) => setForm({ ...form, date: event.target.value })} className="w-full rounded border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none" />
      </Field>
      <Field label="Director">
        <TextInput value={form.director} onChange={(v) => setForm({ ...form, director: v })} />
      </Field>
      <Field label="Description">
        <TextArea value={form.description} onChange={(v) => setForm({ ...form, description: v })} rows={3} />
      </Field>
      <Field label="Parent Groups">
        <EntityReferenceMultiSelector entityType="group" values={parentGroupIds} onChange={setParentGroupIds} placeholder="Search parent groups..." />
      </Field>
      <Field label="Custom Fields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} onValidityChange={setCustomFieldsValid} entityType="group" />
      </Field>
      <CreateModalActions loading={mutation.isPending} disabled={!customFieldsValid} onSave={save} createAnother={createAnother} onCreateAnotherChange={setCreateAnother} />
    </EditModal>
  );
}
