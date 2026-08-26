import { useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Headphones, Mic2, MonitorPlay, PlayCircle } from "lucide-react";
import { audios, system } from "../api/client";
import { createFromUrlWithOptionalDownload, mergeUrlLists, NoDownloaderFoundError, type UrlDownloadMode } from "../utils/createFromUrlDownload";
import type { Audio, AudioCreate, AudioFilterCriteria, DownloaderMatch, EntityEngagement } from "../api/types";
import { BookmarkButton } from "../components/BookmarkButton";
import { CreateModalActions, EditModal, Field, TextArea, TextInput } from "../components/EditModal";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { CardSelectionToggle, RouteCardLinkOverlay } from "../components/RouteCardLinkOverlay";
import { CustomFieldsEditor, formatDuration } from "../components/shared";
import { IsoDateInput } from "../components/IsoDateInput";
import { AudioTile, EntityReferencePopovers } from "../components/EntityCards";
import { useAuth } from "../auth/AuthContext";
import { canWriteEntity } from "../auth/visibility";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { useListUrlState } from "../hooks/useListUrlState";
import { useInfiniteListData } from "../hooks/useInfiniteListData";
import { toggleOptionsFromEvent, useMultiSelect, type MultiSelectToggleHandler } from "../hooks/useMultiSelect";
import { getDefaultFilter, resolveSavedDisplayMode } from "../components/SavedFilterMenu";
import { getAudioDisplayTitle } from "../utils/audioTextDisplay";
import { FileBackedCreateSource, type CreateSourceMode } from "../components/FileBackedCreateSource";
import { useFileBackedCreatePreferences } from "../hooks/useFileBackedCreatePreferences";
import { StudioSelector } from "../components/StudioSelector";
import { StringListEditor } from "../components/StringListEditor";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { SourceDownloadDialog } from "../components/SourceDownloadDialog";
import { AUDIO_CRITERIA } from "../components/FilterDialog";
import { ScraperEntityTagger } from "../components/ScraperEntityTagger";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { VirtualizedEntityGrid } from "../components/VirtualizedEntityLayouts";
import { AUDIO_MULTI_SORT_KEYS, AUDIO_SORT_OPTIONS } from "../components/audioSortOptions";
import { MediaAggregateMetadata } from "../components/MediaAggregateMetadata";

const SORT_OPTIONS = AUDIO_SORT_OPTIONS;

interface Props {
  onNavigate: (route: any) => void;
}

export function AudiosPage({ onNavigate }: Props) {
  const [showCreate, setShowCreate] = useState(false);
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("audios");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "date", direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: resolveSavedDisplayMode(savedFilter?.uiOptions, ["grid", "list", "tagger"] as const, "grid") as DisplayMode,
    };
  }, []);

  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "audios",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "tagger"] as const,
    allowInfinitePageSize: true,
  });
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const listData = useInfiniteListData<Audio>({
    queryKey: ["audios", filter, objectFilter],
    filter,
    chunkSize: defaultState.filter.perPage ?? 40,
    queryPage: (nextFilter) => hasObjectFilter
      ? audios.findFiltered({ findFilter: nextFilter, objectFilter: objectFilter as AudioFilterCriteria })
      : audios.find(nextFilter),
  });

  const items = listData.items;
  const totalCount = listData.totalCount;
  const isLoading = listData.isLoading;
  const { engagementById } = useEntityEngagementBatch("audio", items.map((item) => item.id));
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: listData.infiniteFilterKey, objectFilter }), [listData.infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(items, { preserveOnItemsChange: listData.infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const aggregateFilter = useMemo(() => ({ q: filter.q, page: 1, perPage: 0 }), [filter.q]);
  const { data: filteredAggregate, isLoading: filteredAggregateLoading } = useQuery({
    queryKey: ["audios", "aggregate", aggregateFilter, objectFilter],
    queryFn: () => audios.aggregate({ findFilter: aggregateFilter, objectFilter: hasObjectFilter ? objectFilter as AudioFilterCriteria : undefined }),
  });
  const selectedIdList = useMemo(() => [...selectedIds].map(Number).sort((left, right) => left - right), [selectedIds]);
  const { data: selectedAggregate, isLoading: selectedAggregateLoading } = useQuery({
    queryKey: ["audios", "aggregate", "selection", selectedIdList],
    queryFn: () => audios.aggregate({ ids: selectedIdList }),
    enabled: selectedIdList.length > 0,
  });
  const { hasPermission } = useAuth();
  const canWriteAudio = canWriteEntity("audio", hasPermission);
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
    {showCreate ? <AudioCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "audio", id })} /> : null}
    <ListPage
      title="Audios"
      metadataByline={<MediaAggregateMetadata duration={filteredAggregate?.duration} fileSize={filteredAggregate?.fileSize} loading={filteredAggregateLoading} />}
      pageKey="audios"
      filterMode="audios"
      filter={filter}
      onFilterChange={setFilter}
      totalCount={totalCount}
      isLoading={isLoading}
      error={listData.loadError}
      onRetry={() => { void listData.refetch(); }}
      searchPlaceholder="Search audio, tags, performers..."
      sortOptions={SORT_OPTIONS}
      multiSortKeys={AUDIO_MULTI_SORT_KEYS}
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={["grid", "list", "tagger"]}
      allowInfinitePageSize
      showPagingControls={!listData.infinitePageSize}
      selectAllPending={listData.infinitePageSize ? selectAllMatchingPending : false}
      onSelectAllMatching={listData.infinitePageSize ? selectAll : undefined}
      selectAllMatchingLabel="Select shown"
      infiniteScroll={listData.infiniteScroll}
      onNew={canWriteAudio ? () => setShowCreate(true) : undefined}
      criteriaDefinitions={AUDIO_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      selectedIds={selectedIds}
      selectionMetadata={<MediaAggregateMetadata duration={selectedAggregate?.duration} fileSize={selectedAggregate?.fileSize} loading={selectedAggregateLoading} />}
      onSelectAll={listData.infinitePageSize ? handleSelectAllMatching : selectAll}
      onSelectNone={selectNone}
      onInvertSelection={invertSelection}
      selectionActions={<BulkSelectionActions entityType="audios" selectedIds={selectedIds} onDone={selectNone} audioItems={items} downloadItems={items} onNavigate={onNavigate} />}
    >
      {items.length === 0 && !isLoading ? (
        <div className="rounded-lg border border-dashed border-border bg-card/70 px-6 py-10 text-sm text-muted">
          No audio items matched the current filter.
        </div>
      ) : (
        displayMode === "tagger" ? (
          <ScraperEntityTagger
            entityType="audio"
            label="Audio"
            items={items}
            selectedIds={selectedIds}
            selecting={selecting}
            onSelect={toggle}
            getTitle={getAudioDisplayTitle}
            getImageUrl={(audio) => audio.imagePath ?? undefined}
            getRoute={(audio) => ({ page: "audio", id: audio.id })}
            queryKey="audios"
          />
        ) : displayMode === "list" ? (
          <RelatedEntityListView entityType="audios" items={items} displayMode="list" selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={listData.infinitePageSize} hasNextPage={listData.infiniteQuery.hasNextPage} isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage} loadMore={listData.loadMore} />
        ) : (
        <VirtualizedEntityGrid
          items={items}
          getItemKey={(audio) => audio.id}
          minCardWidth="var(--card-min-width, 280px)"
          estimateRowHeight={220}
          infinitePageSize={listData.infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
          renderItem={(audio) => (
            <AudioTile
              audio={audio}
              engagement={engagementById.get(audio.id)}
              selected={selectedIds.has(audio.id)}
              selecting={selecting}
              onSelect={(toggleOptions) => toggle(audio.id, toggleOptions)}
              onClick={(toggleOptions) => selecting ? toggle(audio.id, toggleOptions) : onNavigate({ page: "audio", id: audio.id })}
              onNavigate={onNavigate}
            />
          )}
        />
        )
      )}
    </ListPage>
    </>
  );
}

function AudioCreateModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: number) => void }) {
  const queryClient = useQueryClient();
  const [sourceMode, setSourceMode] = useState<CreateSourceMode>("metadata");
  const [filePath, setFilePath] = useState("");
  const [url, setUrl] = useState("");
  const { urlDownloadMode, setUrlDownloadMode, scrapeMetadata, setScrapeMetadata } = useFileBackedCreatePreferences("Audio");
  const [noDownloaderFound, setNoDownloaderFound] = useState(false);
  const [sourceDownload, setSourceDownload] = useState<{ sourceUrl: string; data: AudioCreate; matches: DownloaderMatch[]; autoApplyMetadata: boolean } | null>(null);
  const [title, setTitle] = useState("");
  const [code, setCode] = useState("");
  const [date, setDate] = useState("");
  const [details, setDetails] = useState("");
  const [studioId, setStudioId] = useState<number | undefined>(undefined);
  const [urls, setUrls] = useState<string[]>([""]);
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({});
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const [createAnother, setCreateAnother] = useState(false);

  const resetForm = () => {
    setSourceMode("metadata");
    setFilePath("");
    setUrl("");
    setNoDownloaderFound(false);
    setTitle("");
    setCode("");
    setDate("");
    setDetails("");
    setStudioId(undefined);
    setUrls([""]);
    setCustomFields({});
  };

  const buildPayload = (extraUrls: string[] = []): AudioCreate => ({
    title: title.trim() || undefined,
    code: code.trim() || undefined,
    date: date || undefined,
    details: details.trim() || undefined,
    studioId,
    urls: mergeUrlLists(urls, extraUrls),
    customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
  });

  const handleCreated = (created?: Audio) => {
    queryClient.invalidateQueries({ queryKey: ["audios"] });
    resetForm();
    if (createAnother) return;
    onClose();
    if (created?.id) onCreated(created.id);
  };

  const createMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (data: AudioCreate) => audios.create(data),
    onSuccess: handleCreated,
  });

  const fileMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({ path, data }: { path: string; data: AudioCreate }) => {
      const created = await audios.createFromFile({ filePath: path });
      return created?.id ? audios.update(created.id, data) : created;
    },
    onSuccess: handleCreated,
  });

  const downloadMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({ requestedUrl, data, downloadMode, scrapeMetadata }: { requestedUrl: string; data: AudioCreate; downloadMode: UrlDownloadMode; scrapeMetadata: boolean }) => {
      if (downloadMode === "now") {
        const matches = (await system.matchDownloaders({ url: requestedUrl }))
          .filter((match) => match.supportedEntity.toLowerCase() === "audio");

        if (matches.length > 1) {
          setSourceDownload({ sourceUrl: requestedUrl, data, matches, autoApplyMetadata: scrapeMetadata });
          return null;
        }

        if (matches.length === 0) {
          throw new NoDownloaderFoundError(requestedUrl);
        }
      }

      return createFromUrlWithOptionalDownload({ requestedUrl, data, entity: "Audio", downloadMode, scrapeMetadata, create: audios.create });
    },
    onSuccess: (created) => {
      if (!created) return;
      queryClient.invalidateQueries({ queryKey: ["jobs"] });
      handleCreated(created);
    },
    onError: (err) => {
      if (err instanceof NoDownloaderFoundError) setNoDownloaderFound(true);
    },
  });

  const handleSourceModeChange = (mode: CreateSourceMode) => {
    setSourceMode(mode);
    setNoDownloaderFound(false);
  };

  const handleUrlChange = (value: string) => {
    setUrl(value);
    setNoDownloaderFound(false);
  };

  const handleCreateWithoutDownload = () => {
    const requestedUrl = url.trim();
    if (requestedUrl) createMutation.mutate(buildPayload([requestedUrl]));
  };

  const handleSave = () => {
    if (sourceMode === "file") {
      const path = filePath.trim();
      if (path) fileMutation.mutate({ path, data: buildPayload() });
      return;
    }
    if (sourceMode === "url") {
      const requestedUrl = url.trim();
      if (requestedUrl) downloadMutation.mutate({ requestedUrl, data: buildPayload(), downloadMode: urlDownloadMode, scrapeMetadata });
      return;
    }
    createMutation.mutate(buildPayload());
  };

  const pending = createMutation.isPending || fileMutation.isPending || downloadMutation.isPending;
  const error = (createMutation.error ?? fileMutation.error ?? downloadMutation.error) as Error | null;
  const visibleError = error instanceof NoDownloaderFoundError ? null : error;
  return (
    <>
    <EditModal title="Create Audio" open={open} onClose={onClose}>
      <FileBackedCreateSource mode={sourceMode} onModeChange={handleSourceModeChange} filePath={filePath} onFilePathChange={setFilePath} url={url} onUrlChange={handleUrlChange} urlDownloadMode={urlDownloadMode} onUrlDownloadModeChange={setUrlDownloadMode} scrapeMetadata={scrapeMetadata} onScrapeMetadataChange={setScrapeMetadata} noDownloaderFound={noDownloaderFound} onCreateWithoutDownload={handleCreateWithoutDownload} onDismissNoDownloader={() => setNoDownloaderFound(false)} modes={["metadata", "file", "url"]} filePlaceholder="C:\\Media\\audio.mp3" urlPlaceholder="https://example.com/audio.mp3" />

      <div className="grid grid-cols-2 gap-4">
        <Field label="Title"><TextInput value={title} onChange={setTitle} placeholder="Audio title" /></Field>
        <Field label="Date"><IsoDateInput value={date} onChange={(event) => setDate(event.target.value)} className="w-full rounded border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none" /></Field>
      </div>
      <Field label="Studio Code"><TextInput value={code} onChange={setCode} placeholder="Audio code" /></Field>
      <Field label="Details"><TextArea value={details} onChange={setDetails} placeholder="Audio notes" rows={3} /></Field>
      <Field label="Studio"><StudioSelector value={studioId} onChange={setStudioId} /></Field>
      <Field label="URLs"><StringListEditor values={urls} onChange={setUrls} placeholder="https://..." addLabel="Add URL" inputType="url" /></Field>
      <Field label="Custom Fields"><CustomFieldsEditor value={customFields} onChange={setCustomFields} onValidityChange={setCustomFieldsValid} entityType="audio" /></Field>
      {visibleError ? (
        <div className="mb-4 rounded border border-red-700 bg-red-900/50 p-2 text-sm text-red-300">
          {visibleError.message}
        </div>
      ) : null}
      <CreateModalActions loading={pending} disabled={!customFieldsValid} onCancel={onClose} onSave={handleSave} createAnother={createAnother} onCreateAnotherChange={setCreateAnother} />
    </EditModal>
    {sourceDownload ? (
      <SourceDownloadDialog
        open
        entity="Audio"
        sourceUrl={sourceDownload.sourceUrl}
        matches={sourceDownload.matches}
        baseTitle={sourceDownload.data.title}
        metadata={sourceDownload.data}
        autoApplyMetadata={sourceDownload.autoApplyMetadata}
        onClose={() => setSourceDownload(null)}
        onQueued={() => {
          queryClient.invalidateQueries({ queryKey: ["jobs"] });
          queryClient.invalidateQueries({ queryKey: ["audios"] });
          setSourceDownload(null);
          resetForm();
          if (!createAnother) onClose();
        }}
      />
    ) : null}
    </>
  );
}

function AudioListTable({ audios: items, engagementById, selectedIds, selecting, onToggle, onNavigate }: { audios: Audio[]; engagementById: ReadonlyMap<number, EntityEngagement>; selectedIds: Set<number>; selecting: boolean; onToggle: MultiSelectToggleHandler; onNavigate: (route: any) => void }) {
  return (
    <div className="overflow-x-auto rounded-lg border border-border bg-card">
      <table className="min-w-full divide-y divide-border text-sm">
        <thead className="bg-surface text-left text-xs uppercase text-muted">
          <tr>
            <th className="w-10 px-3 py-2" />
            <th className="px-3 py-2">Title</th>
            <th className="px-3 py-2">Studio</th>
            <th className="px-3 py-2">Duration</th>
            <th className="px-3 py-2">Files</th>
            <th className="px-3 py-2">Entities</th>
            <th className="px-3 py-2">Listened</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {items.map((audio) => {
            const title = getAudioDisplayTitle(audio);
            const duration = audio.maxDuration > 0 ? formatDuration(audio.maxDuration) : "";
            const engagement = engagementById.get(audio.id);
            return (
              <tr key={audio.id} onClick={(event) => selecting ? onToggle(audio.id, toggleOptionsFromEvent(event)) : onNavigate({ page: "audio", id: audio.id })} className={`cursor-pointer hover:bg-surface/70 ${selectedIds.has(audio.id) ? "bg-accent/10" : ""}`}>
                <td className="px-3 py-2">
                  <input
                    type="checkbox"
                    checked={selectedIds.has(audio.id)}
	                    onChange={() => {}}
	                    onClick={(event) => { event.stopPropagation(); onToggle(audio.id, toggleOptionsFromEvent(event)); }}
                    className="rounded border-border bg-card"
                    aria-label={`Select ${title}`}
                  />
                </td>
                <td className="min-w-[18rem] px-3 py-2">
                  <div className="font-medium text-foreground">{title}</div>
                  {audio.details ? <div className="mt-0.5 line-clamp-1 max-w-xl text-xs text-secondary">{audio.details}</div> : null}
                  {audio.files.length === 0 && audio.urls.length > 0 ? <div className="mt-1 text-xs text-cyan-300">Download available</div> : null}
                </td>
                <td className="px-3 py-2 text-secondary">
                  <EntityReferencePopovers studio={{ id: audio.studioId, name: audio.studioName }} onNavigate={onNavigate} />
                </td>
                <td className="px-3 py-2 text-secondary">{duration}</td>
                <td className="px-3 py-2 text-secondary">{audio.fileCount}</td>
                <td className="px-3 py-2 text-secondary">
                  <EntityReferencePopovers performers={audio.performers} tags={audio.tags} groups={audio.groups} onNavigate={onNavigate} />
                </td>
                <td className="px-3 py-2 text-secondary">{engagement?.playDuration ? formatDuration(engagement.playDuration) : ""}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
