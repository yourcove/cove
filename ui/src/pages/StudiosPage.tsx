import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { studios } from "../api/client";
import type { EntityEngagement, Studio, StudioCreate, StudioFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { CreateModalActions, EditModal, Field, TextInput, TextArea } from "../components/EditModal";
import { EntityReferenceSelector } from "../components/EntityReferenceSelector";
import { useMultiSelect } from "../hooks/useMultiSelect";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { Building2, Merge } from "lucide-react";
import { STUDIO_CRITERIA } from "../components/FilterDialog";
import { MergeDialog } from "../components/MergeDialog";
import { StudioTagger } from "../components/StudioTagger";
import { StudioTile } from "../components/EntityCards";
import { getDefaultFilter } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { useInfiniteListData } from "../hooks/useInfiniteListData";
import { ExtensionSlot } from "../router/RouteRegistry";
import { useAuth } from "../auth/AuthContext";
import { canWriteEntity } from "../auth/visibility";
import { CustomFieldsEditor } from "../components/shared";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { VirtualizedEntityGrid } from "../components/VirtualizedEntityLayouts";

const SORT_OPTIONS = [
  { value: "name", label: "Name" },
  { value: "rating", label: "Rating" },
  { value: "video_count", label: "Video Count" },
  { value: "gallery_count", label: "Gallery Count" },
  { value: "image_count", label: "Image Count" },
  { value: "latest_video_date", label: "Latest Video Date" },
  { value: "total_file_size", label: "Total File Size" },
  { value: "parent_count", label: "Parent Studio Count" },
  { value: "child_count", label: "Substudios Count" },
  { value: "tag_count", label: "Tag Count" },
  { value: "updated_at", label: "Updated At" },
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
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "latest_video_date", direction: "desc" },
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
    allowInfinitePageSize: true,
  });
  const [showCreate, setShowCreate] = useState(false);
  const [showMerge, setShowMerge] = useState(false);
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const { hasPermission } = useAuth();
  const canWriteStudio = canWriteEntity("studio", hasPermission);

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const listData = useInfiniteListData<Studio>({
    queryKey: ["studios", filter, objectFilter],
    filter,
    chunkSize: defaultState.filter.perPage ?? 40,
    queryPage: (nextFilter) =>
      hasObjectFilter
        ? studios.findFiltered({ findFilter: nextFilter, objectFilter: objectFilter as StudioFilterCriteria })
        : studios.find(nextFilter),
  });

  const items = listData.items;
  const totalCount = listData.totalCount;
  const isLoading = listData.isLoading;
  const { engagementById } = useEntityEngagementBatch("studio", items.map((item) => item.id));
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: listData.infiniteFilterKey, objectFilter }), [listData.infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(items, { preserveOnAppend: listData.infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await listData.fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };

  return (
    <>
      <StudioCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "studio", id })} />
      <ListPage
        title="Studios"
        pageKey="studios"
        filterMode="studios"
        filter={filter}
        onFilterChange={setFilter}
        totalCount={totalCount}
        isLoading={isLoading}
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
        criteriaDefinitions={STUDIO_CRITERIA}
        objectFilter={objectFilter}
        onObjectFilterChange={setObjectFilter}
        onNew={canWriteStudio ? () => setShowCreate(true) : undefined}
        selectedIds={selectedIds}
        onSelectAll={listData.infinitePageSize ? handleSelectAllMatching : selectAll}
        onSelectNone={selectNone}
        onInvertSelection={invertSelection}
        selectionActions={
          <>
            {canWriteStudio && selectedIds.size >= 2 && (
              <button
                onClick={() => setShowMerge(true)}
                className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-yellow-400 hover:text-yellow-300 hover:bg-yellow-900/20"
              >
                <Merge className="w-3 h-3" />
                Merge
              </button>
            )}
            <BulkSelectionActions entityType="studios" selectedIds={selectedIds} onDone={selectNone} />
          </>
        }
      >
      {displayMode === "tagger" ? (
        <StudioTagger studios={items} selectedIds={selectedIds} selecting={selecting} onSelect={toggle} />
      ) : displayMode === "grid" ? (
        <VirtualizedEntityGrid
          items={items}
          getItemKey={(studio) => studio.id}
          minCardWidth="var(--card-min-width, 200px)"
          estimateRowHeight={300}
          infinitePageSize={listData.infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
          renderItem={(s) => (
            <StudioTile
              studio={s}
              engagement={engagementById.get(s.id)}
              onClick={() => selecting ? toggle(s.id) : onNavigate({ page: "studio", id: s.id })}
              onNavigate={onNavigate}
              selected={selectedIds.has(s.id)}
              onSelect={() => toggle(s.id)}
              selecting={selecting}
            >
              <ExtensionSlot slot="studio-card-footer" context={{ studio: s, onNavigate }} />
            </StudioTile>
          )}
        />
      ) : (
        <RelatedEntityListView entityType="studios" items={items} displayMode="list" selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={listData.infinitePageSize} hasNextPage={listData.infiniteQuery.hasNextPage} isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage} loadMore={listData.loadMore} />
      )}
      {items.length === 0 && (
        <div className="text-center text-secondary py-16">
          <Building2 className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <p>No studios found</p>
        </div>
      )}
      </ListPage>
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

function StudioListTable({ studios: items, engagementById, onNavigate, selectedIds, onToggle, selecting }: { studios: Studio[]; engagementById: ReadonlyMap<number, EntityEngagement>; onNavigate: (r: any) => void; selectedIds?: Set<number>; onToggle?: (id: number) => void; selecting?: boolean }) {
  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="border-b border-border text-left text-muted text-xs">
          {selectedIds && <th className="w-8 py-2 px-3"></th>}
          <th className="py-2 px-3">Name</th>
          <th className="py-2 px-3">Parent</th>
          <th className="py-2 px-3 text-right">Videos</th>
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
            <td className="py-2 px-3 text-secondary text-right">{s.videoCount}</td>
            <td className="py-2 px-3 text-secondary text-right">{engagementById.get(s.id)?.rating ?? ""}</td>
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
  });
  const [parentId, setParentId] = useState<number | undefined>(undefined);
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({});
  const [createAnother, setCreateAnother] = useState(false);

  const resetForm = () => {
    setForm({ name: "", details: "" });
    setParentId(undefined);
    setCustomFields({});
  };

  const mutation = useMutation({
    mutationFn: (data: StudioCreate) => studios.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["studios"] });
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
      name,
      details: form.details || undefined,
      parentId,
      customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
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
      <Field label="Parent Studio">
        <EntityReferenceSelector entityType="studio" value={parentId} onChange={setParentId} placeholder="Search parent studios..." />
      </Field>
      <Field label="Custom Fields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} entityType="studio" />
      </Field>
      <CreateModalActions loading={mutation.isPending} onSave={save} createAnother={createAnother} onCreateAnotherChange={setCreateAnother} />
    </EditModal>
  );
}

