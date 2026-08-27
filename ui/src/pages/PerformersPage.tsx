import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { performers } from "../api/client";
import type { EntityEngagement, FindFilter, Performer, PerformerCreate, PerformerFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { CreateModalActions, EditModal, Field, TextInput, TextArea } from "../components/EditModal";
import { StringListEditor } from "../components/StringListEditor";
import { GENDER_OPTIONS } from "./PerformerEditModal";
import { toggleOptionsFromEvent, useMultiSelect, type BoundMultiSelectToggleHandler, type MultiSelectToggleHandler } from "../hooks/useMultiSelect";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { PERFORMER_CRITERIA } from "../components/FilterDialog";
import { IsoDateInput } from "../components/IsoDateInput";
import { Users, Heart, Merge, User } from "lucide-react";
import { MergeDialog } from "../components/MergeDialog";
import { PerformerTagger } from "../components/PerformerTagger";
import { PerformerTile, CardExtensionSlot } from "../components/EntityCards";
import { getDefaultFilter, resolveSavedDisplayMode } from "../components/SavedFilterMenu";
import { useListUrlState } from "../hooks/useListUrlState";
import { useInfiniteListData } from "../hooks/useInfiniteListData";
import { useAuth } from "../auth/AuthContext";
import { canWriteEntity } from "../auth/visibility";
import { createNestedRouteLinkProps } from "../components/cardNavigation";
import { CardSelectionToggle, RouteCardLinkOverlay } from "../components/RouteCardLinkOverlay";
import { PERFORMER_MULTI_SORT_KEYS, PERFORMER_SORT_OPTIONS } from "../components/performerSortOptions";
import { CustomFieldsEditor } from "../components/shared";
import { useWallColumns } from "../hooks/useWallColumns";
import { WallMediaCard } from "../components/WallMediaCard";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { VirtualizedEntityGrid, VirtualizedWallColumns } from "../components/VirtualizedEntityLayouts";
import { getApiValidationFailureDetail } from "../utils/requestFailure";

/** Convert 2-letter ISO country code to flag emoji */
function countryToFlag(code: string): string {
  const upper = code.toUpperCase();
  if (upper.length !== 2) return code;
  return String.fromCodePoint(...[...upper].map(c => 0x1F1E6 + c.charCodeAt(0) - 65));
}

const SORT_OPTIONS = PERFORMER_SORT_OPTIONS;

interface Props {
  onNavigate: (r: any) => void;
}

export function PerformersPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("performers");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "latest_video_date", direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: resolveSavedDisplayMode(savedFilter?.uiOptions, ["grid", "list", "wall", "tagger"] as const, "grid") as DisplayMode,
    };
  }, []);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "performers",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "wall", "tagger"] as const,
    allowInfinitePageSize: true,
  });
  const [wallColumnCount, setWallColumnCount] = useState(6);
  const [showCreate, setShowCreate] = useState(false);
  const [showMerge, setShowMerge] = useState(false);
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const { hasPermission } = useAuth();
  const canWritePerformer = canWriteEntity("performer", hasPermission);

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const listData = useInfiniteListData<Performer>({
    queryKey: ["performers", filter, objectFilter],
    filter,
    chunkSize: defaultState.filter.perPage ?? 40,
    queryPage: (nextFilter) =>
      hasObjectFilter
        ? performers.findFiltered({ findFilter: nextFilter, objectFilter: objectFilter as PerformerFilterCriteria })
        : performers.find(nextFilter),
  });

  const items = listData.items;
  const totalCount = listData.totalCount;
  const isLoading = listData.isLoading;
  const wallColumns = useWallColumns(items, wallColumnCount);
  const { engagementById } = useEntityEngagementBatch("performer", items.map((item) => item.id));
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: listData.infiniteFilterKey, objectFilter }), [listData.infiniteFilterKey, objectFilter]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(items, { preserveOnItemsChange: listData.infinitePageSize, resetKey: selectionResetKey });
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
      <PerformerCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "performer", id })} />
      <ListPage
        title="Performers"
        pageKey="performers"
        filterMode="performers"
        filter={filter}
        onFilterChange={setFilter}
        totalCount={totalCount}
        isLoading={isLoading}
        error={listData.loadError}
        onRetry={() => { void listData.refetch(); }}
        sortOptions={SORT_OPTIONS}
        multiSortKeys={PERFORMER_MULTI_SORT_KEYS}
        displayMode={displayMode}
        onDisplayModeChange={setDisplayMode}
        availableDisplayModes={["grid", "list", "wall", "tagger"]}
        allowInfinitePageSize
        showPagingControls={!listData.infinitePageSize}
        selectAllPending={listData.infinitePageSize ? selectAllMatchingPending : false}
        onSelectAllMatching={listData.infinitePageSize ? selectAll : undefined}
        selectAllMatchingLabel="Select shown"
        infiniteScroll={listData.infiniteScroll}
        wallColumnCount={wallColumnCount}
        onWallColumnCountChange={setWallColumnCount}
        onNew={canWritePerformer ? () => setShowCreate(true) : undefined}
        criteriaDefinitions={PERFORMER_CRITERIA}
        objectFilter={objectFilter}
        onObjectFilterChange={setObjectFilter}
        selectedIds={selectedIds}
        onSelectAll={listData.infinitePageSize ? handleSelectAllMatching : selectAll}
        onSelectNone={selectNone}
        onInvertSelection={invertSelection}
        selectionActions={
          <>
            {canWritePerformer && selectedIds.size >= 2 && (
              <button
                onClick={() => setShowMerge(true)}
                className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-yellow-400 hover:text-yellow-300 hover:bg-yellow-900/20"
              >
                <Merge className="w-3 h-3" />
                Merge
              </button>
            )}
            <BulkSelectionActions entityType="performers" selectedIds={selectedIds} onDone={selectNone} />
          </>
        }
      >
      {displayMode === "tagger" ? (
        <PerformerTagger performers={items} selectedIds={selectedIds} selecting={selecting} onSelect={toggle} onNavigate={(performerId) => onNavigate({ page: "performer", id: performerId })} />
      ) : displayMode === "wall" ? (
        <VirtualizedWallColumns
          columns={wallColumns}
          getItemKey={(performer) => performer.id}
          infinitePageSize={listData.infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
          estimateItemHeight={280}
          gap={4}
          className="flex gap-1 px-2"
          columnClassName="flex min-w-0 flex-1 flex-col gap-1"
          renderItem={(performer) => (
                <EntityWallCard
                  title={performer.name}
                  imageSrc={performer.imagePath}
                  route={{ page: "performer", id: performer.id }}
                  selected={selectedIds.has(performer.id)}
                  selecting={selecting}
                  onSelect={(toggleOptions) => toggle(performer.id, toggleOptions)}
                  onClick={(toggleOptions) => selecting ? toggle(performer.id, toggleOptions) : onNavigate({ page: "performer", id: performer.id })}
                />
          )}
        />
      ) : displayMode === "grid" ? (
        <VirtualizedEntityGrid
          items={items}
          getItemKey={(p) => p.id}
          minCardWidth="var(--card-min-width, 160px)"
          estimateRowHeight={340}
          infinitePageSize={listData.infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
          renderItem={(p) => (
            <PerformerTile
              performer={p}
              engagement={engagementById.get(p.id)}
              onClick={(toggleOptions) => selecting ? toggle(p.id, toggleOptions) : onNavigate({ page: "performer", id: p.id })}
              onNavigate={onNavigate}
              selected={selectedIds.has(p.id)}
              onSelect={(toggleOptions) => toggle(p.id, toggleOptions)}
              selecting={selecting}
            >
              <CardExtensionSlot slot="performer-card-footer" context={{ performer: p, onNavigate }} />
            </PerformerTile>
          )}
        />
      ) : (
        <RelatedEntityListView entityType="performers" items={items} displayMode="list" selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={listData.infinitePageSize} hasNextPage={listData.infiniteQuery.hasNextPage} isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage} loadMore={listData.loadMore} />
      )}
      {items.length === 0 && (
        <div className="text-center text-secondary py-16">
          <Users className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <p>No performers found</p>
        </div>
      )}
      </ListPage>

      <MergeDialog
        open={showMerge}
        onClose={() => { setShowMerge(false); selectNone(); }}
        entityType="performer"
        items={items.filter((p) => selectedIds.has(p.id)).map((p) => ({ id: p.id, name: p.name, imagePath: p.imagePath }))}
        onMerge={performers.merge}
        queryKey="performers"
      />
    </>
  );
}

function EntityWallCard({ title, imageSrc, route, selected, selecting, onSelect, onClick }: { title: string; imageSrc?: string | null; route: any; selected: boolean; selecting: boolean; onSelect: BoundMultiSelectToggleHandler; onClick: BoundMultiSelectToggleHandler }) {
  return (
    <WallMediaCard title={title} imageSrc={imageSrc} aspectRatio="2 / 3" onClick={(event) => onClick(toggleOptionsFromEvent(event))} className={selected ? "ring-2 ring-accent" : ""} fallback={<User className="h-12 w-12 text-muted" />}>
      <RouteCardLinkOverlay route={route} onClick={onClick} label={`Open ${title}`} disabled={selecting} selectionSafeZone />
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <div className="selection-safe-zone absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/85 to-transparent p-2 text-xs font-medium text-white">
        {title}
      </div>
    </WallMediaCard>
  );
}

function PerformerListTable({ performers: items, engagementById, onNavigate, selectedIds, onToggle, selecting }: { performers: Performer[]; engagementById: ReadonlyMap<number, EntityEngagement>; onNavigate: (r: any) => void; selectedIds?: Set<number>; onToggle?: MultiSelectToggleHandler; selecting?: boolean }) {
  return (
    <table className="w-full text-sm">
      <thead>
        <tr className="border-b border-border text-left text-muted text-xs">
          {selectedIds && <th className="w-8 py-2 px-3"></th>}
          <th className="py-2 px-3">Name</th>
          <th className="py-2 px-3">Gender</th>
          <th className="py-2 px-3">Age</th>
          <th className="py-2 px-3">Country</th>
          <th className="py-2 px-3 text-right">Videos</th>
          <th className="py-2 px-3 text-right">Rating</th>
          <th className="py-2 px-3">Favorite</th>
        </tr>
      </thead>
      <tbody>
        {items.map((p) => {
          const age = p.birthdate
            ? Math.floor((Date.now() - new Date(p.birthdate).getTime()) / 31557600000)
            : null;
          const engagement = engagementById.get(p.id);
          const favorite = engagement?.isFavorite ?? p.favorite;
          const rating = engagement?.rating;
          return (
            <tr 
              key={p.id} 
              onClick={(event) => selecting ? onToggle?.(p.id, toggleOptionsFromEvent(event)) : onNavigate({ page: "performer", id: p.id })}
              className={`border-b border-border hover:bg-card cursor-pointer ${selectedIds?.has(p.id) ? "bg-accent/10" : ""}`}
            >
              {selectedIds && (
                <td className="py-2 px-3">
                  <input type="checkbox" checked={selectedIds.has(p.id)} onChange={() => {}} onClick={(event) => { event.stopPropagation(); onToggle?.(p.id, toggleOptionsFromEvent(event)); }} className="w-3.5 h-3.5 rounded border-border cursor-pointer accent-accent" />
                </td>
              )}
              <td className="py-2 px-3 text-foreground">
                {p.name}
                {p.disambiguation && <span className="text-muted ml-1">({p.disambiguation})</span>}
              </td>
              <td className="py-2 px-3 text-secondary capitalize">{p.gender?.toLowerCase()}</td>
              <td className="py-2 px-3 text-secondary">{age ?? ""}</td>
              <td className="py-2 px-3 text-secondary">{p.country ?? ""}</td>
              <td className="py-2 px-3 text-secondary text-right">{p.videoCount}</td>
              <td className="py-2 px-3 text-secondary text-right">{rating ?? ""}</td>
              <td className="py-2 px-3">
                {favorite && <Heart className="w-4 h-4 fill-red-500 text-red-500" />}
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

/* ── Performer Create Modal ── */
const SELECT_CLASS = "w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent";

export function PerformerCreateModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: number) => void }) {
  const qc = useQueryClient();
  const [name, setName] = useState("");
  const [disambiguation, setDisambiguation] = useState("");
  const [gender, setGender] = useState("");
  const [birthdate, setBirthdate] = useState("");
  const [deathDate, setDeathDate] = useState("");
  const [country, setCountry] = useState("");
  const [ethnicity, setEthnicity] = useState("");
  const [eyeColor, setEyeColor] = useState("");
  const [hairColor, setHairColor] = useState("");
  const [tattoos, setTattoos] = useState("");
  const [piercings, setPiercings] = useState("");
  const [details, setDetails] = useState("");
  const [aliases, setAliases] = useState<string[]>([""]);
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({});
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const [createAnother, setCreateAnother] = useState(false);

  const resetForm = () => {
    setName("");
    setDisambiguation("");
    setGender("");
    setBirthdate("");
    setDeathDate("");
    setCountry("");
    setEthnicity("");
    setEyeColor("");
    setHairColor("");
    setTattoos("");
    setPiercings("");
    setDetails("");
    setAliases([""]);
    setCustomFields({});
  };

  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (data: PerformerCreate) => performers.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["performers"] });
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

  const save = () => {
    const trimmedName = name.trim();
    if (!trimmedName) return;
    const aliasList = aliases.map((alias) => alias.trim()).filter(Boolean);
    mutation.mutate({
      name: trimmedName,
      disambiguation: disambiguation.trim() || undefined,
      gender: gender || undefined,
      birthdate: birthdate || undefined,
      deathDate: deathDate || undefined,
      country: country || undefined,
      ethnicity: ethnicity || undefined,
      eyeColor: eyeColor || undefined,
      hairColor: hairColor || undefined,
      tattoos: tattoos || undefined,
      piercings: piercings || undefined,
      details: details || undefined,
      aliases: aliasList,
      customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
    });
  };

  return (
    <EditModal title="Create Performer" open={open} onClose={handleClose}>
      <div className="space-y-4">
        <Field label="Name *">
          <TextInput value={name} onChange={setName} placeholder="Performer name" />
        </Field>

        <Field label="Disambiguation">
          <TextInput value={disambiguation} onChange={setDisambiguation} placeholder="Optional identity qualifier" />
        </Field>

        <div className="grid grid-cols-4 gap-4">
          <Field label="Gender">
            <select value={gender} onChange={(e) => setGender(e.target.value)} className={SELECT_CLASS}>
              <option value="">—</option>
              {GENDER_OPTIONS.map((o) => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </select>
          </Field>
          <Field label="Birthdate">
            <IsoDateInput value={birthdate} onChange={(e) => setBirthdate(e.target.value)} className={SELECT_CLASS} />
          </Field>
          <Field label="Death Date">
            <IsoDateInput value={deathDate} onChange={(e) => setDeathDate(e.target.value)} className={SELECT_CLASS} />
          </Field>
          <Field label="Country">
            <TextInput value={country} onChange={setCountry} placeholder="e.g. US" />
          </Field>
        </div>

        <div className="grid grid-cols-3 gap-4">
          <Field label="Ethnicity">
            <TextInput value={ethnicity} onChange={setEthnicity} />
          </Field>
          <Field label="Eye Color">
            <TextInput value={eyeColor} onChange={setEyeColor} />
          </Field>
          <Field label="Hair Color">
            <TextInput value={hairColor} onChange={setHairColor} />
          </Field>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <Field label="Tattoos">
            <TextInput value={tattoos} onChange={setTattoos} />
          </Field>
          <Field label="Piercings">
            <TextInput value={piercings} onChange={setPiercings} />
          </Field>
        </div>

        <Field label="Details">
          <TextArea value={details} onChange={setDetails} placeholder="Bio / notes" rows={2} />
        </Field>

        <Field label="Aliases">
          <StringListEditor values={aliases} onChange={setAliases} placeholder="Alias" addLabel="Add Alias" />
        </Field>

        <Field label="Custom Fields">
          <CustomFieldsEditor value={customFields} onChange={setCustomFields} onValidityChange={setCustomFieldsValid} entityType="performer" />
        </Field>
        {mutation.error ? <div role="alert" className="rounded border border-red-700 bg-red-900/50 p-2 text-sm text-red-300">{getApiValidationFailureDetail(mutation.error)}</div> : null}
      </div>
      <CreateModalActions loading={mutation.isPending} disabled={!customFieldsValid} onSave={save} createAnother={createAnother} onCreateAnotherChange={setCreateAnother} />
    </EditModal>
  );
}
