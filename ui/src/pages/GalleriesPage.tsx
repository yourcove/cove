import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { galleries, entityImages } from "../api/client";
import type { EntityEngagement, FindFilter, Gallery, GalleryCreate, GalleryFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { RatingBanner } from "../components/Rating";
import { CreateModalActions, EditModal, Field, TextInput, TextArea } from "../components/EditModal";
import { toggleOptionsFromEvent, useMultiSelect, type BoundMultiSelectToggleHandler, type MultiSelectToggleHandler } from "../hooks/useMultiSelect";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { FolderOpen, Images as ImagesIcon, Trash2, Loader2, Edit, Box, Film, Check, Search, Download } from "lucide-react";
import { GalleryTile, PopoverButton, VideosPopoverContent, ImagesPopoverContent, EntityReferencePopovers } from "../components/EntityCards";
import { GALLERY_CRITERIA } from "../components/filterCriteriaCatalogs";
import { IsoDateInput } from "../components/IsoDateInput";
import { BulkEditDialog, GALLERY_BULK_FIELDS } from "../components/BulkEditDialog";
import { getDefaultFilter, resolveSavedDisplayMode } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { useInfiniteListData } from "../hooks/useInfiniteListData";
import { CardSelectionToggle } from "../components/RouteCardLinkOverlay";
import { useWallColumns } from "../hooks/useWallColumns";
import { GALLERY_MULTI_SORT_KEYS, GALLERY_SORT_OPTIONS } from "../components/gallerySortOptions";
import { getGalleryDisplayTitle } from "../utils/galleryDisplay";
import { WallMediaCard } from "../components/WallMediaCard";
import { BatchDownloadOptionsDialog } from "../components/BatchDownloadOptionsDialog";
import { GalleryDownloadDialog } from "../components/GalleryDownloadDialog";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { MediaAggregateMetadata } from "../components/MediaAggregateMetadata";
import { ScraperEntityTagger } from "../components/ScraperEntityTagger";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canWriteEntity } from "../auth/visibility";
import { CustomFieldsEditor } from "../components/shared";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { VirtualizedEntityGrid, VirtualizedWallColumns } from "../components/VirtualizedEntityLayouts";
import {
  formatBatchDownloadSummary,
  getBatchDownloadOptionsStorageKey,
  getUndownloadedSelectionItems,
  loadStoredBatchDownloadOptions,
  queueBatchDownloads,
  saveStoredBatchDownloadOptions,
  type BatchDownloadOptions,
} from "../utils/batchDownloads";

interface Props {
  onNavigate: (r: any) => void;
}

export function GalleriesPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("galleries");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "date", direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: resolveSavedDisplayMode(savedFilter?.uiOptions, ["grid", "list", "wall", "tagger"] as const, "grid") as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "galleries",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "wall", "tagger"] as const,
    allowInfinitePageSize: true,
  });
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [showCreate, setShowCreate] = useState(false);
  const [wallColumnCount, setWallColumnCount] = useState(5);
  const [downloadTarget, setDownloadTarget] = useState<Gallery | null>(null);
  const [showBatchDownloadOptions, setShowBatchDownloadOptions] = useState(false);
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canWriteGallery = canWriteEntity("gallery", hasPermission);
  const canDeleteGallery = canDeleteEntity("gallery", hasPermission);
  const canDownloadGallery = hasPermission("jobs.run") && canWriteGallery;

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const listData = useInfiniteListData<Gallery>({
    queryKey: ["galleries", filter, objectFilter],
    filter,
    chunkSize: defaultState.filter.perPage ?? 40,
    queryPage: (nextFilter) =>
      hasObjectFilter
        ? galleries.findFiltered({ findFilter: nextFilter, objectFilter: objectFilter as GalleryFilterCriteria })
        : galleries.find(nextFilter),
  });

  const items = listData.items;
  const totalCount = listData.totalCount;
  const isLoading = listData.isLoading;
  const { engagementById } = useEntityEngagementBatch("gallery", items.map((item) => item.id));
  const wallColumns = useWallColumns(items, wallColumnCount);
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: listData.infiniteFilterKey, objectFilter }), [listData.infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(items, { preserveOnItemsChange: listData.infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const aggregateFilter = useMemo(() => ({ q: filter.q, page: 1, perPage: 0 }), [filter.q]);
  const { data: filteredAggregate, isLoading: filteredAggregateLoading } = useQuery({
    queryKey: ["galleries", "aggregate", aggregateFilter, objectFilter],
    queryFn: () => galleries.aggregate({ findFilter: aggregateFilter, objectFilter: hasObjectFilter ? objectFilter as GalleryFilterCriteria : undefined }),
  });
  const selectedIdList = useMemo(() => [...selectedIds].map(Number).sort((left, right) => left - right), [selectedIds]);
  const { data: selectedAggregate, isLoading: selectedAggregateLoading } = useQuery({
    queryKey: ["galleries", "aggregate", "selection", selectedIdList],
    queryFn: () => galleries.aggregate({ ids: selectedIdList }),
    enabled: selectedIdList.length > 0,
  });
  const selectedGallery = selectedIds.size === 1 ? items.find((gallery) => selectedIds.has(gallery.id)) : undefined;
  const selectedDownloadTargets = useMemo(() => getUndownloadedSelectionItems(items, selectedIds), [items, selectedIds]);
  const canDownloadSelectedGallery = canDownloadGallery && selectedDownloadTargets.length > 0;
  const batchDownloadStorageKey = getBatchDownloadOptionsStorageKey("page-galleries");
  const [batchDownloadOptions, setBatchDownloadOptions] = useState<BatchDownloadOptions>(() => loadStoredBatchDownloadOptions(batchDownloadStorageKey));
  const handleSelectAllMatching = async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await listData.fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  };

  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) =>
      galleries.bulkUpdate({ ids: [...selectedIds], ...values } as any),
    onSuccess: () => {
      setShowBulkEdit(false);
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["galleries"] });
    },
  });

  const batchDownloadMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (options: BatchDownloadOptions) => queueBatchDownloads("Gallery", selectedDownloadTargets, options),
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: ["jobs"] });
      queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
      queryClient.invalidateQueries({ queryKey: ["jobs-history"] });
      queryClient.invalidateQueries({ queryKey: ["galleries"] });
      window.alert(formatBatchDownloadSummary("gallery", result));
      selectNone();
    },
    onError: (error: Error) => {
      window.alert(error.message || "Failed to queue the selected downloads.");
    },
  });

  useEffect(() => {
    if (displayMode !== "list" || !listData.infinitePageSize || !listData.infiniteQuery.hasNextPage || listData.infiniteQuery.isFetchingNextPage) {
      return;
    }

    listData.loadMore();
  }, [displayMode, listData]);

  return (
    <>
    <GalleryCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "gallery", id })} />
    {downloadTarget ? (
      <GalleryDownloadDialog
        open
        gallery={downloadTarget}
        onClose={() => setDownloadTarget(null)}
        onNavigate={onNavigate}
      />
    ) : null}
    <BatchDownloadOptionsDialog
      open={showBatchDownloadOptions}
      entity="Gallery"
      itemCount={selectedDownloadTargets.length}
      initialOptions={batchDownloadOptions}
      isPending={batchDownloadMut.isPending}
      onClose={() => setShowBatchDownloadOptions(false)}
      onConfirm={(options) => {
        setBatchDownloadOptions(options);
        saveStoredBatchDownloadOptions(batchDownloadStorageKey, options);
        setShowBatchDownloadOptions(false);
        batchDownloadMut.mutate(options);
      }}
    />
    <ListPage
      title="Galleries"
      pageKey="galleries"
      filterMode="galleries"
      filter={filter}
      onFilterChange={setFilter}
      totalCount={totalCount}
      isLoading={isLoading}
      error={listData.loadError}
      onRetry={() => { void listData.refetch(); }}
      sortOptions={GALLERY_SORT_OPTIONS}
      multiSortKeys={GALLERY_MULTI_SORT_KEYS}
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={["grid", "list", "wall", "tagger"]}
      allowInfinitePageSize
      showPagingControls={!listData.infinitePageSize}
      selectAllPending={listData.infinitePageSize ? selectAllMatchingPending : false}
      onSelectAllMatching={listData.infinitePageSize ? selectAll : undefined}
      selectAllMatchingLabel="Select shown"
      infiniteScroll={listData.infiniteScroll}
      criteriaDefinitions={GALLERY_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      onNew={canWriteGallery ? () => setShowCreate(true) : undefined}
      metadataByline={<MediaAggregateMetadata fileSize={filteredAggregate?.fileSize} loading={filteredAggregateLoading} />}
      wallColumnCount={wallColumnCount}
      onWallColumnCountChange={setWallColumnCount}

      selectedIds={selectedIds}
      onSelectAll={listData.infinitePageSize ? handleSelectAllMatching : selectAll}
      onSelectNone={selectNone}
      onInvertSelection={invertSelection}
      selectionActions={<BulkSelectionActions entityType="galleries" selectedIds={selectedIds} onDone={selectNone} downloadItems={items} />}
      selectionMetadata={<MediaAggregateMetadata fileSize={selectedAggregate?.fileSize} loading={selectedAggregateLoading} />}
    >
      {displayMode === "tagger" ? (
        <ScraperEntityTagger
          entityType="gallery"
          label="Gallery"
          items={items}
          selectedIds={selectedIds}
          selecting={selecting}
          onSelect={toggle}
          getTitle={(gallery) => getGalleryDisplayTitle(gallery)}
          getImageUrl={(gallery) => gallery.coverPath ?? galleries.coverUrl(gallery.id, gallery.updatedAt, 640)}
          getRoute={(gallery) => ({ page: "gallery", id: gallery.id })}
          queryKey="galleries"
        />
      ) : displayMode === "grid" ? (
        <VirtualizedEntityGrid
          items={items}
          getItemKey={(gallery) => gallery.id}
          minCardWidth="var(--card-min-width, 140px)"
          estimateRowHeight={260}
          infinitePageSize={listData.infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
          renderItem={(g) => (
            <GalleryTile gallery={g} engagement={engagementById.get(g.id)} onClick={(toggleOptions) => selecting ? toggle(g.id, toggleOptions) : onNavigate({ page: "gallery", id: g.id })} onNavigate={onNavigate} selected={selectedIds.has(g.id)} onSelect={(toggleOptions) => toggle(g.id, toggleOptions)} selecting={selecting} />
          )}
        />
      ) : displayMode === "list" ? (
        <RelatedEntityListView entityType="galleries" items={items} displayMode="list" selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={listData.infinitePageSize} hasNextPage={listData.infiniteQuery.hasNextPage} isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage} loadMore={listData.loadMore} />
      ) : (
        <VirtualizedWallColumns
          columns={wallColumns}
          getItemKey={(gallery) => gallery.id}
          infinitePageSize={listData.infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
          estimateItemHeight={320}
          gap={8}
          renderItem={(gallery) => (
                <GalleryWallCard
                  gallery={gallery}
                  engagement={engagementById.get(gallery.id)}
                  onClick={(toggleOptions) => selecting ? toggle(gallery.id, toggleOptions) : onNavigate({ page: "gallery", id: gallery.id })}
                  onNavigate={onNavigate}
                  selected={selectedIds.has(gallery.id)}
                  onSelect={(toggleOptions) => toggle(gallery.id, toggleOptions)}
                  selecting={selecting}
                />
          )}
        />
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

function GalleryWallCard({ gallery, engagement, onClick, onNavigate, selected, onSelect, selecting }: { gallery: Gallery; engagement?: EntityEngagement; onClick: BoundMultiSelectToggleHandler; onNavigate: (route: any) => void; selected?: boolean; onSelect?: BoundMultiSelectToggleHandler; selecting?: boolean }) {
  const rating = engagement?.rating;
  const galleryCoverSrc = gallery.coverPath ?? galleries.coverUrl(gallery.id, gallery.updatedAt, 960);

  return (
    <WallMediaCard
      onClick={(event) => onClick(toggleOptionsFromEvent(event))}
      title={getGalleryDisplayTitle(gallery)}
      imageSrc={galleryCoverSrc}
      aspectRatio="1 / 1"
      fallback={<FolderOpen className="w-10 h-10 text-muted opacity-30" />}
      className={`${selected ? "border-accent ring-2 ring-accent" : ""} group`.trim()}
    >
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <RatingBanner rating={rating} />
      <div className="absolute bottom-1 left-1 right-1 z-10 flex items-end justify-between gap-2">
        <div className="flex flex-wrap items-center gap-1 rounded-md bg-black/55 px-1 py-0.5 backdrop-blur-sm">
          {gallery.imageCount > 0 ? (
            <PopoverButton icon={<ImagesIcon className="w-3.5 h-3.5" />} count={gallery.imageCount} title="Images" wide preferBelow>
              <ImagesPopoverContent filter={{ galleryId: gallery.id }} />
            </PopoverButton>
          ) : null}
          {gallery.videoCount > 0 ? (
            <PopoverButton icon={<Film className="w-3.5 h-3.5" />} count={gallery.videoCount} title="Videos" wide preferBelow>
              <VideosPopoverContent filter={{ galleryId: gallery.id }} />
            </PopoverButton>
          ) : null}
          <EntityReferencePopovers
            performers={gallery.performers}
            tags={gallery.tags}
            studio={gallery.studioName ? { id: gallery.studioId, name: gallery.studioName } : null}
            onNavigate={onNavigate}
            className="gap-0.5"
          />
        </div>
        {gallery.organized ? (
          <span className="inline-flex items-center gap-1 rounded-full bg-green-600/90 px-1.5 py-0.5 text-[10px] text-white" title="Organized">
            <Box className="h-3.5 w-3.5" />
          </span>
        ) : null}
      </div>
      {gallery.studioName && gallery.studioId && !selecting ? (
        <div className="absolute top-0 right-0 p-1 z-[5]">
          <img
            src={entityImages.studioImageUrl(gallery.studioId)}
            alt={gallery.studioName}
            className="h-8 w-auto max-w-[120px] object-contain drop-shadow-md"
            onError={(e) => {
              const el = e.target as HTMLImageElement;
              el.style.display = "none";
              if (el.nextElementSibling) (el.nextElementSibling as HTMLElement).style.display = "";
            }}
          />
          <span className="text-xs font-medium text-white bg-black/60 px-1.5 py-0.5 rounded" style={{ display: "none" }}>{gallery.studioName}</span>
        </div>
      ) : null}
    </WallMediaCard>
  );
}

function GalleryListTable({ galleries: items, engagementById, onNavigate, selectedIds, onToggle, selecting }: { galleries: Gallery[]; engagementById: ReadonlyMap<number, EntityEngagement>; onNavigate: (r: any) => void; selectedIds?: Set<number>; onToggle?: MultiSelectToggleHandler; selecting?: boolean }) {
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
          <tr key={g.id} onClick={(event) => selecting ? onToggle?.(g.id, toggleOptionsFromEvent(event)) : onNavigate({ page: "gallery", id: g.id })} className={`border-b border-border hover:bg-card cursor-pointer ${selectedIds?.has(g.id) ? "bg-accent/10" : ""}`}>
            {selectedIds && <td className="py-2 px-3"><input type="checkbox" checked={selectedIds.has(g.id)} onChange={() => {}} onClick={(event) => { event.stopPropagation(); onToggle?.(g.id, toggleOptionsFromEvent(event)); }} className="w-3.5 h-3.5 rounded border-border cursor-pointer accent-accent" /></td>}
            <td className="py-2 px-3 text-foreground">{getGalleryDisplayTitle(g)}</td>
            <td className="py-2 px-3 text-secondary">{g.studioName ?? ""}</td>
            <td className="py-2 px-3 text-secondary">{g.date ?? ""}</td>
            <td className="py-2 px-3 text-secondary text-right">{g.imageCount}</td>
            <td className="py-2 px-3 text-secondary text-right">{engagementById.get(g.id)?.rating ?? ""}</td>
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
  });
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({});
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const [createAnother, setCreateAnother] = useState(false);

  const resetForm = () => {
    setForm({ title: "", code: "", date: "", details: "", photographer: "" });
    setCustomFields({});
  };

  const mutation = useMutation({
    mutationFn: (data: GalleryCreate) => galleries.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["galleries"] });
      resetForm();
      if (createAnother) return;
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
      customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
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
        <IsoDateInput value={form.date} onChange={(event) => setForm({ ...form, date: event.target.value })} className="w-full rounded border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none" />
      </Field>
      <Field label="Photographer">
        <TextInput value={form.photographer} onChange={(v) => setForm({ ...form, photographer: v })} />
      </Field>
      <Field label="Details">
        <TextArea value={form.details} onChange={(v) => setForm({ ...form, details: v })} rows={3} />
      </Field>
      <Field label="Custom Fields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} onValidityChange={setCustomFieldsValid} entityType="gallery" />
      </Field>
      <CreateModalActions loading={mutation.isPending} disabled={!customFieldsValid} onSave={save} createAnother={createAnother} onCreateAnotherChange={setCreateAnother} />
    </EditModal>
  );
}
