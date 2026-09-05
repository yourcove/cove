import { useMutation, useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { audios, entityEngagement, entityImages, groups, images, metadata, videos, segmentLibrary, texts } from "../api/client";
import type { AffinityHostType, Audio, BoolCriterion, DateCriterion, EntityEngagement, FindFilter, Group, GroupItem, GroupItemKind, Image, IntCriterion, MultiIdCriterion, Video, VideoFilterCriteria, SegmentDerivedQueryDescriptor, SegmentRecord, SegmentSpanDerivedQuery, StringCriterion, TextDocument, TimestampCriterion } from "../api/types";
import { formatDate, formatDuration, TagBadge, CustomFieldsDisplay, FieldProvenanceHover, resolveTagProvenance } from "../components/shared";
import { Building2, ExternalLink, FileText, Film, Fingerprint, FolderOpen, GripVertical, Headphones, Images, Layers, Link as LinkIcon, Loader2, Merge, MoreVertical, Pencil, Play, Plus, Tag, Trash2, Unlink, User, X } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { GroupEditModal } from "./GroupEditModal";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { NarrativeText } from "../components/NarrativeText";
import { ExtensionSlot } from "../router/RouteRegistry";
import { AudioTile, EntityTileFrame, GroupTile, ImageTile, VideoCard, SegmentTile, TextTile } from "../components/EntityCards";
import { CompilationPlayer } from "../components/CompilationPlayer";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { DetailListPagination, DetailListToolbar } from "../components/DetailListToolbar";
import { ListLoadError } from "../components/ListLoadError";
import { VIDEO_CRITERIA } from "../components/filterCriteriaCatalogs";
import type { CriterionDefinition } from "../components/filterCriteriaTypes";
import { EntityHeroLayout, HERO_ACTION_BUTTON_CLASS, HERO_PRIMARY_ACTION_BUTTON_CLASS } from "../components/EntityHeroLayout";
import { CoverImageDialog } from "../components/CoverImageDialog";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { EntityDetailTabs } from "../components/EntityDetailTabs";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { AspectRatingsPanel } from "../components/AspectRatingsPanel";
import { InteractiveRating } from "../components/Rating";
import { useExtensionTabs } from "../components/useExtensionTabs";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity, filterItemsByPermission } from "../auth/visibility";
import { SortableList } from "../components/SortableList";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useDetailListQuery } from "../hooks/useDetailListQuery";
import { useDetailListSelection } from "../hooks/useDetailListSelection";
import { withRequiredMultiId } from "../utils/detailRelationFilters";
import { VirtualizedEntityGrid } from "../components/VirtualizedEntityLayouts";
import { VirtualizedInfiniteList } from "../components/VirtualizedInfiniteList";
import { getEntityCardMinWidthPx } from "../hooks/useEntityCardSize";
import { RelatedEntityListView, useRelatedEntityDisplayMode } from "../components/RelatedEntityListView";
import { ContextualVideoListView } from "../components/ContextualMediaListViews";
import { isProtectedBuiltInGroup } from "../components/DynamicGroupFilterEditor";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { sortSeededRandom } from "../utils/seededRandomSort";
import { useDetailListUrlState } from "../hooks/useDetailListUrlState";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type TabKey = "items" | "subGroups" | (string & {});

const GROUP_ITEM_SORT_OPTIONS = [
  { value: "order", label: "Item #" },
  { value: "added_at", label: "Added to Group" },
  { value: "random", label: "Random" },
  { value: "title", label: "Title" },
  { value: "code", label: "Code" },
  { value: "date", label: "Date" },
  { value: "kind", label: "Type" },
  { value: "rating", label: "Rating" },
  { value: "organized", label: "Organized" },
  { value: "path", label: "Path" },
  { value: "url", label: "URL" },
  { value: "studio", label: "Studio" },
  { value: "tag_count", label: "Tag Count" },
  { value: "performer_count", label: "Performer Count" },
  { value: "file_count", label: "File Count" },
  { value: "duration", label: "Duration" },
  { value: "word_count", label: "Word Count" },
  { value: "page_count", label: "Page Count" },
  { value: "host_id", label: "Host ID" },
  { value: "range_start", label: "Range Start" },
  { value: "range_end", label: "Range End" },
  { value: "source_key", label: "Source Key" },
  { value: "confidence", label: "Confidence" },
  { value: "created_at", label: "Created At" },
  { value: "updated_at", label: "Updated At" },
];
const GROUP_ITEM_BUILT_IN_FILTER: FindFilter = { page: 1, perPage: 40, sort: "order", direction: "asc" };
const GROUP_ITEM_DISPLAY_MODES = ["grid", "list"] as const;

const GROUP_ITEM_CRITERIA: CriterionDefinition[] = [
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "code", label: "Code", type: "string", filterKey: "codeCriterion" },
  { id: "details", label: "Details", type: "string", filterKey: "detailsCriterion" },
  { id: "kind", label: "Type", type: "enum", filterKey: "kindCriterion", modifiers: ["EQUALS", "NOT_EQUALS"], options: [
    { value: "video", label: "Videos" },
    { value: "image", label: "Images" },
    { value: "audio", label: "Audio" },
    { value: "text", label: "Texts" },
    { value: "segment", label: "Segments" },
  ] },
  { id: "rating", label: "Rating", type: "rating", filterKey: "ratingCriterion" },
  { id: "organized", label: "Organized", type: "bool", filterKey: "organizedCriterion" },
  { id: "path", label: "Path", type: "path", filterKey: "pathCriterion" },
  { id: "url", label: "URL", type: "string", filterKey: "urlCriterion" },
  { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "studios", label: "Studios", type: "multiId", entityType: "studios", filterKey: "studiosCriterion" },
  { id: "performerCount", label: "Performer Count", type: "number", filterKey: "performerCountCriterion" },
  { id: "tagCount", label: "Tag Count", type: "number", filterKey: "tagCountCriterion" },
  { id: "fileCount", label: "File Count", type: "number", filterKey: "fileCountCriterion" },
  { id: "duration", label: "Duration", type: "duration", filterKey: "durationCriterion" },
  { id: "wordCount", label: "Word Count", type: "number", filterKey: "wordCountCriterion" },
  { id: "pageCount", label: "Page Count", type: "number", filterKey: "pageCountCriterion" },
  { id: "itemNumber", label: "Item #", type: "number", filterKey: "itemNumberCriterion" },
  { id: "hostId", label: "Host ID", type: "number", filterKey: "hostIdCriterion" },
  { id: "rangeStart", label: "Range Start", type: "number", filterKey: "rangeStartCriterion" },
  { id: "rangeEnd", label: "Range End", type: "number", filterKey: "rangeEndCriterion" },
  { id: "sourceKey", label: "Source Key", type: "string", filterKey: "sourceKeyCriterion" },
  { id: "segmentKind", label: "Segment Kind", type: "string", filterKey: "segmentKindCriterion" },
  { id: "confidence", label: "Confidence", type: "number", filterKey: "confidenceCriterion" },
  { id: "added", label: "Added to Group", type: "timestamp", filterKey: "addedAtCriterion" },
  { id: "created", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updated", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

export function GroupDetailPage({ id, onNavigate }: Props) {
  const { data: group, isLoading, error: groupError, refetch: retryGroup } = useQuery({
    queryKey: ["group", id],
    queryFn: () => groups.get(id),
  });
  const groupLoadError = getLoadError(group, groupError);
  const { hasPermission, user } = useAuth();
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [coverOpen, setCoverOpen] = useState(false);
  const [coverFace, setCoverFace] = useState<"front" | "back">("front");
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [addSubGroupRequestId, setAddSubGroupRequestId] = useState(0);
  const opsMenuRef = useRef<HTMLDivElement | null>(null);
  const getCompilationItemOrderRef = useRef<() => Promise<string[]>>(async () => []);
  const [activeTab, setActiveTab] = useState<TabKey>("items");
  const { allTabs: groupTabs, renderExtensionTab } = useExtensionTabs("group", [
    { key: "items", label: "Items" },
    { key: "subGroups", label: "Sub-Groups" },
  ], id);
  const [videoFilter, setVideoFilter] = useState<FindFilter>({ page: 1, perPage: 24, direction: "asc", sort: "date" });
  const queryClient = useQueryClient();
  const { backLabel, goBack } = useBackNavigation({ page: "groups" }, onNavigate);
  const canReadGroups = canReadEntity("group", hasPermission);
  const canReadVideos = canReadEntity("video", hasPermission);
  const canWriteGroup = canWriteEntity("group", hasPermission);
  const canDeleteGroup = canDeleteEntity("group", hasPermission) && !isProtectedBuiltInGroup(group?.querySourceKey);
  const canReadStudios = canReadEntity("studio", hasPermission);
  const canReadTags = canReadEntity("tag", hasPermission);
  const canEngageGroup = canReadGroups && (user?.kind === "user" || user?.kind === "system");
  const { data: groupItemsData, isLoading: groupItemsLoading, error: groupItemsError, refetch: retryGroupItems } = useQuery({
    queryKey: ["group-items", id],
    queryFn: () => groups.items.list(id),
    enabled: canReadGroups && !!group && group.kind !== "dynamic",
  });
  const groupItemsLoadError = getLoadError(groupItemsData, groupItemsError);
  const groupItems = groupItemsData ?? [];
  const {
    favorite: groupFavorite,
    rating: groupRating,
    setFavorite: setGroupFavorite,
    setRating: setGroupRating,
    favoritePending: groupFavoritePending,
  } = useEntityEngagement("group", id, {
    enabled: !!group,
  });
  const { data: playbackManifest, isLoading: playbackManifestLoading } = useQuery({
    queryKey: ["group", id, "playback-manifest"],
    queryFn: () => groups.items.playbackManifest(id),
    enabled: canReadVideos,
  });
  const hasPlaybackItems = (playbackManifest?.items.length ?? 0) > 0;
  const hasCompilationItems = groupItems.some((item) => item.kind === "videoRange")
    || playbackManifest?.items.some((item) => item.startSec > 0 || item.endSec != null) === true;

  useDocumentTitle(group?.name);

  useEffect(() => {
    if (!showOpsMenu) return;
    const handlePointerDown = (event: PointerEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(event.target as Node)) {
        setShowOpsMenu(false);
      }
    };
    window.addEventListener("pointerdown", handlePointerDown);
    return () => window.removeEventListener("pointerdown", handlePointerDown);
  }, [showOpsMenu]);

  const deleteMut = useMutation({
    mutationFn: () => groups.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["groups"] });
      goBack();
    },
  });

  const tabs = useMemo(() => {
    const countedTabs = groupTabs.map((tab) => ({
      ...tab,
      count:
        tab.key === "items"
          ? group?.kind === "dynamic" ? group.itemCount ?? groupItems.length : groupItems.length + (group?.subGroupCount ?? 0)
          : tab.key === "subGroups"
            ? group?.subGroupCount
            : undefined,
    }));

    return filterItemsByPermission(countedTabs, {
      items: canReadVideos || canReadGroups ? "groups.read" : "__denied__",
      subGroups: "groups.read",
    }, hasPermission).filter((tab) => tab.key !== "items" || canReadVideos || canReadGroups);
  }, [canReadGroups, canReadVideos, group?.subGroupCount, groupItems.length, groupTabs, hasPermission]);

  useEffect(() => {
    if (tabs.length > 0 && !tabs.some((tab) => tab.key === activeTab)) {
      setActiveTab(tabs[0].key as TabKey);
    }
  }, [activeTab, tabs]);

  if (isLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  if (isApiNotFoundError(groupLoadError)) {
    return <div className="py-16 text-center text-secondary">Group not found</div>;
  }

  if (groupLoadError) {
    return <ListLoadError error={groupLoadError} onRetry={() => { void retryGroup(); }} title="Could not load group" className="mx-0 mt-0" />;
  }

  if (!group) {
    return <div className="py-16 text-center text-secondary">Group not found</div>;
  }

  const itemsContent = (
    <GroupItemsPanel
      key={group.id}
      group={group}
      filter={videoFilter}
      setFilter={setVideoFilter}
      onNavigate={onNavigate}
      groupItems={groupItems}
      groupItemsLoading={groupItemsLoading}
      groupItemsLoadError={groupItemsLoadError}
      retryGroupItems={() => { void retryGroupItems(); }}
      canReadVideos={canReadVideos}
      canReadGroups={canReadGroups}
      canWriteGroup={canWriteGroup}
      addSubGroupRequestId={addSubGroupRequestId}
      getCompilationItemOrderRef={getCompilationItemOrderRef}
    />
  );

  const subGroupsContent = (
    <section className="rounded-2xl border border-border bg-card/70 p-5">
      <div className="mb-4">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-muted">Sub-Groups</h2>
        <p className="mt-1 text-sm text-secondary">Groups contained within this group.</p>
      </div>
      <GroupSubGroupsPanel groupId={id} canWriteGroup={canWriteGroup && canReadGroups} onNavigate={onNavigate} />
    </section>
  );

  const activeContent =
    activeTab === "items"
      ? itemsContent
      : activeTab === "subGroups"
        ? subGroupsContent
        : renderExtensionTab(activeTab, id, onNavigate);

  const countMetrics = getGroupCountMetrics(group);
  const canAddSubGroup = canWriteGroup && canReadGroups && group.kind !== "dynamic";
  const showGroupOpsMenu = canAddSubGroup || canDeleteGroup;

  return (
    <div>
      <GroupEditModal group={group} open={editing} onClose={() => setEditing(false)} />
      <CoverImageDialog
        open={coverOpen}
        title={coverFace === "front" ? "Set Group Cover (Front)" : "Set Group Cover (Back)"}
        entityType="group"
        entityId={group.id}
        coverKey={coverFace}
        currentImageUrl={coverFace === "front" ? group.frontImagePath : group.backImagePath}
        onUpload={(file) => coverFace === "front" ? entityImages.uploadGroupFrontImage(group.id, file) : entityImages.uploadGroupBackImage(group.id, file)}
        onDelete={() => coverFace === "front" ? entityImages.deleteGroupFrontImage(group.id) : entityImages.deleteGroupBackImage(group.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={() => {
          queryClient.invalidateQueries({ queryKey: ["group", group.id] });
          queryClient.invalidateQueries({ queryKey: ["groups"] });
        }}
        aspectRatio="2/3"
        extraActions={
          <div className="flex items-center justify-center gap-2">
            <button
              type="button"
              onClick={() => setCoverFace("front")}
              className={`rounded-lg border px-3 py-1.5 text-sm transition-colors ${coverFace === "front" ? "border-accent bg-accent text-white" : "border-border bg-card text-secondary hover:text-foreground"}`}
            >
              Front
            </button>
            <button
              type="button"
              onClick={() => setCoverFace("back")}
              className={`rounded-lg border px-3 py-1.5 text-sm transition-colors ${coverFace === "back" ? "border-accent bg-accent text-white" : "border-border bg-card text-secondary hover:text-foreground"}`}
            >
              Back
            </button>
          </div>
        }
      />
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Group"
        message={`Delete "${group.name}"? This cannot be undone.`}
        onConfirm={() => deleteMut.mutate()}
        onCancel={() => setConfirmDelete(false)}
      />

      <EntityHeroLayout
        entityType="group"
        entityId={group.id}
        title={<FieldProvenanceHover fieldProvenance={group.fieldProvenance} fieldKey="name">{group.name}</FieldProvenanceHover>}
        description={group.description ? <FieldProvenanceHover fieldProvenance={group.fieldProvenance} fieldKey={["synopsis", "description", "details"]} block><NarrativeText>{group.description}</NarrativeText></FieldProvenanceHover> : undefined}
        favorite={groupFavorite}
        favoritePending={groupFavoritePending}
        onFavoriteToggle={canEngageGroup ? () => setGroupFavorite(!groupFavorite) : undefined}
        aliases={group.aliases ? <FieldProvenanceHover fieldProvenance={group.fieldProvenance} fieldKey="aliases">{group.aliases}</FieldProvenanceHover> : undefined}
        metaRow={
          <div className="flex flex-wrap items-center gap-3 text-sm text-secondary">
            {group.date ? <FieldProvenanceHover fieldProvenance={group.fieldProvenance} fieldKey="date"><span>{formatDate(group.date)}</span></FieldProvenanceHover> : null}
            {group.director ? <FieldProvenanceHover fieldProvenance={group.fieldProvenance} fieldKey="director"><span>Director: {group.director}</span></FieldProvenanceHover> : null}
            {group.studioName && group.studioId ? (
              canReadStudios ? (
                <FieldProvenanceHover fieldProvenance={group.fieldProvenance} fieldKey="studio">
                  <button onClick={() => onNavigate({ page: "studio", id: group.studioId })} className="text-accent hover:underline">
                    {group.studioName}
                  </button>
                </FieldProvenanceHover>
              ) : (
                <FieldProvenanceHover fieldProvenance={group.fieldProvenance} fieldKey="studio"><span>{group.studioName}</span></FieldProvenanceHover>
              )
            ) : null}
          </div>
        }
        backLabel={backLabel}
        onGoBack={goBack}
        imageUrl={group.frontImagePath}
        imageAlt={group.name}
        alternateImageUrl={group.backImagePath}
        alternateImageAlt={`${group.name} back cover`}
        primaryImageLabel="front cover"
        alternateImageLabel="back cover"
        imageFit="contain"
        imageContainerClassName="relative flex flex-shrink-0 items-center justify-center overflow-hidden rounded-xl border border-border bg-card shadow-xl shadow-black/35"
        imageClassName="h-auto w-auto max-h-72 max-w-[20rem] object-contain md:max-h-96 md:max-w-[24rem]"
        imageFallbackClassName="h-72 w-56 items-center justify-center bg-card text-muted md:h-96 md:w-72"
        onImageClick={canWriteGroup ? (imageSlot) => { setCoverFace(imageSlot === "alternate" ? "back" : "front"); setCoverOpen(true); } : undefined}
        imageFallback={<Layers className="h-14 w-14" />}
        counts={[
          ...countMetrics,
          { key: "containing", label: "Containing", value: group.containingGroupCount, icon: <LinkIcon className="h-4 w-4" /> },
        ]}
        actions={
          <>
            <ExtensionSlot slot="group-detail-actions" context={{ group, onNavigate }} />
            {hasPlaybackItems ? (
              <button
                type="button"
                onClick={async () => onNavigate({ page: "compilation", id, compilationItemOrder: await getCompilationItemOrderRef.current() })}
                className={`${HERO_ACTION_BUTTON_CLASS} text-secondary`}
                title={hasCompilationItems ? "Standalone Compilation" : "Standalone Player"}
              >
                <Play className="h-4 w-4" />
              </button>
            ) : null}
            {canWriteGroup ? (
              <button
                type="button"
                onClick={() => setEditing(true)}
                className={HERO_PRIMARY_ACTION_BUTTON_CLASS}
                title="Edit"
              >
                <Pencil className="h-3.5 w-3.5" /> Edit
              </button>
            ) : null}
            {showGroupOpsMenu ? (
              <div ref={opsMenuRef} className="relative">
                <button
                  type="button"
                  onClick={() => setShowOpsMenu((value) => !value)}
                  className={`${HERO_ACTION_BUTTON_CLASS} text-secondary`}
                  title="More actions"
                  aria-haspopup="menu"
                  aria-expanded={showOpsMenu}
                >
                  <MoreVertical className="h-4 w-4" />
                </button>
                <FloatingActionMenu open={showOpsMenu} anchorRef={opsMenuRef} onClose={() => setShowOpsMenu(false)} className="min-w-44 p-1">
                    {canAddSubGroup ? (
                      <button
                        type="button"
                        onClick={() => {
                          setShowOpsMenu(false);
                          setActiveTab("items");
                          setAddSubGroupRequestId((value) => value + 1);
                        }}
                        className="flex w-full items-center gap-2 rounded px-3 py-2 text-left text-sm text-foreground transition hover:bg-surface"
                        role="menuitem"
                      >
                        <Plus className="h-4 w-4" />
                        Add sub-group
                      </button>
                    ) : null}
                    {canAddSubGroup && canDeleteGroup ? <div className="my-1 border-t border-border" /> : null}
                    {canDeleteGroup ? (
                    <button
                      type="button"
                      onClick={() => { setShowOpsMenu(false); setConfirmDelete(true); }}
                      className="flex w-full items-center gap-2 rounded px-3 py-2 text-left text-sm text-red-200 transition hover:bg-red-500/10"
                      role="menuitem"
                    >
                      <Trash2 className="h-4 w-4" />
                      Delete group
                    </button>
                    ) : null}
                </FloatingActionMenu>
              </div>
            ) : null}
          </>
        }
        heroContent={(
          <>
            <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
              <div className="shrink-0">
                <InteractiveRating value={groupRating} onChange={(value) => setGroupRating(value)} readOnly={!canEngageGroup} />
              </div>
              <AspectRatingsPanel hostType="group" hostId={id} canRate={canEngageGroup} showHeading={false} variant="inline" className="min-w-0" />
            </div>

            <div className="mt-4 grid grid-cols-2 gap-3 md:grid-cols-4">
              <InfoItem icon={<Layers className="h-4 w-4" />} label="Kind" value={group.kind === "dynamic" ? "Dynamic" : "Static"} />
              <InfoItem icon={<Film className="h-4 w-4" />} label="Items" value={(group.itemCount ?? group.videoCount + group.subGroupCount).toLocaleString()} />
              <InfoItem label="Created" value={formatDate(group.createdAt)} />
              <InfoItem label="Updated" value={formatDate(group.updatedAt)} />
            </div>

            {group.urls.length > 0 ? (
              <FieldProvenanceHover fieldProvenance={group.fieldProvenance} fieldKey="urls" block className="mt-4">
                <div className="flex flex-wrap gap-2">
                  {group.urls.map((url, index) => (
                    <a key={index} href={url} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1.5 rounded-full border border-border bg-card px-3 py-1 text-xs text-accent hover:border-accent/60 hover:text-accent-hover">
                      <ExternalLink className="h-3 w-3" />
                      {(() => { try { return new URL(url).hostname.replace("www.", ""); } catch { return url; } })()}
                    </a>
                  ))}
                </div>
              </FieldProvenanceHover>
            ) : null}

            {canReadTags && group.tags.length > 0 ? (
              <div className="mt-4 flex flex-wrap gap-1.5">
                {group.tags.map((tag) => (
                  <TagBadge key={tag.id} name={tag.name} tag={tag} provenance={resolveTagProvenance(tag, group.fieldProvenance)} onClick={() => onNavigate({ page: "tag", id: tag.id })} />
                ))}
              </div>
            ) : null}

            <CustomFieldsDisplay customFields={group.customFields} entityType="group" />
            <ExtensionSlot slot="group-detail-sidebar-bottom" context={{ group, onNavigate }} />
          </>
        )}
      >
        <EntityDetailTabs tabs={tabs} activeTab={activeTab} onTabChange={(key) => setActiveTab(key as TabKey)} className="mt-0" />
        <div className="py-6">
          {activeContent}
          <ExtensionSlot slot="group-detail-main-bottom" context={{ group, onNavigate }} />
        </div>
      </EntityHeroLayout>

      <ExtensionSlot slot="group-detail-bottom" context={{ group, onNavigate }} />
    </div>
  );
}

function getGroupCountMetrics(group: Group) {
  const alwaysShow = new Set(["videos", "images", "audio", "texts", "segments"]);
  return [
    { key: "videos", label: "Videos", value: group.videoCount, icon: <Film className="h-4 w-4" /> },
    { key: "images", label: "Images", value: group.imageCount ?? 0, icon: <Images className="h-4 w-4" /> },
    { key: "audio", label: "Audio", value: group.audioCount ?? 0, icon: <Headphones className="h-4 w-4" /> },
    { key: "texts", label: "Texts", value: group.textCount ?? 0, icon: <FileText className="h-4 w-4" /> },
    { key: "galleries", label: "Galleries", value: group.galleryCount ?? 0, icon: <FolderOpen className="h-4 w-4" /> },
    { key: "subgroups", label: "Groups", value: group.subGroupCount, icon: <Layers className="h-4 w-4" /> },
    { key: "performers", label: "Performers", value: group.performerCount ?? 0, icon: <User className="h-4 w-4" /> },
    { key: "studios", label: "Studios", value: group.studioCount ?? 0, icon: <Building2 className="h-4 w-4" /> },
    { key: "tags", label: "Tag Items", value: group.tagItemCount ?? 0, icon: <Tag className="h-4 w-4" /> },
    { key: "faces", label: "Faces", value: group.faceCount ?? 0, icon: <Fingerprint className="h-4 w-4" /> },
    { key: "segments", label: "Segments", value: group.segmentCount ?? 0, icon: <Merge className="h-4 w-4" /> },
  ].filter((metric) => alwaysShow.has(metric.key) || metric.value > 0);
}

type MixedGroupItem =
  | { source: "item"; id: string; item: GroupItem; orderIndex: number; kind: GroupItemKind }
  | { source: "subgroup"; id: string; group: Group; orderIndex: number; kind: "group" };

function GroupItemsPanel({ group, filter, setFilter, onNavigate, groupItems, groupItemsLoading, groupItemsLoadError, retryGroupItems, canReadVideos, canReadGroups, canWriteGroup, addSubGroupRequestId, getCompilationItemOrderRef }: {
  group: Group;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
  groupItems?: GroupItem[];
  groupItemsLoading?: boolean;
  groupItemsLoadError: Error | null;
  retryGroupItems: () => void;
  canReadVideos: boolean;
  canReadGroups: boolean;
  canWriteGroup?: boolean;
  addSubGroupRequestId?: number;
  getCompilationItemOrderRef: React.MutableRefObject<() => Promise<string[]>>;
}) {
  const queryClient = useQueryClient();
  const {
    filter: mixedFilter,
    setFilter: setMixedFilter,
    objectFilter: itemObjectFilter,
    setObjectFilter: setItemObjectFilter,
    displayMode: viewMode,
    setDisplayMode: setGroupViewMode,
  } = useDetailListUrlState({
    stateKey: `group-items-${group.id}`,
    resetKey: `group-items-${group.id}`,
    builtInFilter: GROUP_ITEM_BUILT_IN_FILTER,
    defaultFilterKey: `groupitems-${group.id}`,
    defaultDisplayMode: "grid" as const,
    allowedDisplayModes: GROUP_ITEM_DISPLAY_MODES,
    allowInfinitePageSize: true,
  });
  const [zoomLevel, setZoomLevel] = useState(0);
  const [showAddDialog, setShowAddDialog] = useState(false);
  const [searchTerm, setSearchTerm] = useState("");
  const [confirmSelectionAction, setConfirmSelectionAction] = useState<"remove" | "delete" | null>(null);
  const { hasPermission } = useAuth();
  const { data: subGroupsData, isLoading: subGroupsLoading, error: subGroupsError, refetch: retrySubGroups } = useQuery({
    queryKey: ["group-subgroups", group.id],
    queryFn: () => groups.subGroups(group.id),
    enabled: canReadGroups,
  });
  const subGroupsLoadError = getLoadError(subGroupsData, subGroupsError);
  const subGroups = subGroupsData ?? [];
  const { data: searchResults } = useQuery({
    queryKey: ["groups-search-for-subgroup", group.id, searchTerm],
    queryFn: () => groups.find({ page: 1, perPage: 20, q: searchTerm }),
    enabled: showAddDialog && searchTerm.trim().length > 0,
  });

  const isDynamic = group.kind === "dynamic";
  const prerequisiteError = groupItemsLoadError ?? subGroupsLoadError;

  useEffect(() => {
    if (!addSubGroupRequestId || isDynamic || !canWriteGroup || !canReadGroups) return;
    setShowAddDialog(true);
  }, [addSubGroupRequestId, canReadGroups, canWriteGroup, isDynamic]);

  const staticMixedItems = useMemo(
    () => buildMixedGroupItems(groupItems ?? [], subGroups, isDynamic),
    [groupItems, isDynamic, subGroups]
  );
  const staticMixedItemsKey = useMemo(
    () => staticMixedItems.map((item) => `${item.id}:${item.orderIndex}:${item.source === "item" ? item.item.updatedAt : item.group.updatedAt}`).join("|"),
    [staticMixedItems]
  );
  const queryMixedItemsPage = useCallback(async (nextFilter: FindFilter) => {
    const pageItems = async (items: MixedGroupItem[]) => {
      const hostData = requiresHostMetadata(nextFilter, itemObjectFilter)
        ? await fetchMixedItemHostData(queryClient, items)
        : undefined;
      const engagementData = requiresEngagementMetadata(nextFilter, itemObjectFilter)
        ? await fetchMixedItemEngagementData(queryClient, items)
        : undefined;
      const pathCaseSensitive = hasPathContainmentCriterion(itemObjectFilter)
        ? (await queryClient.fetchQuery({ queryKey: ["filesystem-policy"], queryFn: metadata.filesystemPolicy })
            .catch(() => ({ caseSensitive: true }))).caseSensitive
        : true;
      return pageMixedGroupItems(items, nextFilter, itemObjectFilter, hostData, engagementData, pathCaseSensitive);
    };

    if (isDynamic) {
      if (!requiresFullDynamicItemResolution(nextFilter, itemObjectFilter)) {
        const page = await groups.items.page(group.id, normalizeGroupItemPageFilter(nextFilter));
        return { ...page, items: page.items.map(toMixedGroupItem) };
      }

      const allItemsPage = await queryClient.fetchQuery({
        queryKey: ["group-items-page-all", "unbounded-v2", group.id, group.updatedAt, group.queryJson, group.querySourceKey],
        queryFn: () => groups.items.page(group.id, { page: 1, perPage: 0, sort: "order", direction: "asc" }),
      });
      return pageItems(allItemsPage.items.map(toMixedGroupItem));
    }

    return pageItems(staticMixedItems);
  }, [group.id, group.queryJson, group.querySourceKey, group.updatedAt, isDynamic, itemObjectFilter, queryClient, staticMixedItems]);
  const {
    data: mixedData,
    isLoading: mixedItemsLoading,
    loadError: mixedItemsLoadError,
    retry: retryMixedItems,
    infinitePageSize,
    infiniteQuery,
    infiniteFilterKey,
    fetchAllIds,
    loadMore,
  } = useDetailListQuery<MixedGroupItem>({
    queryKey: ["group-mixed-items", group.id, group.updatedAt, group.queryJson, group.querySourceKey, staticMixedItemsKey, itemObjectFilter],
    filter: mixedFilter,
    queryFn: queryMixedItemsPage,
    enabled: canReadGroups && (isDynamic || (!groupItemsLoading && !subGroupsLoading)),
  });
  const displayedMixedItems = mixedData?.items ?? [];
  const getCompilationItemOrder = useCallback(async () => {
    const allItemsPage = await queryMixedItemsPage({ ...mixedFilter, page: 1, perPage: 0 });
    return allItemsPage.items
      .filter((item): item is Extract<MixedGroupItem, { source: "item" }> => item.source === "item")
      .map((item) => isDynamic
        ? `${(item.item.hostType || item.item.kind).toLowerCase()}:${getMixedItemHostIdValue(item)}`
        : `item:${item.item.id}`);
  }, [isDynamic, mixedFilter, queryMixedItemsPage]);
  useEffect(() => {
    getCompilationItemOrderRef.current = getCompilationItemOrder;
  }, [getCompilationItemOrder, getCompilationItemOrderRef]);
  const totalItemCount = mixedData?.totalCount ?? 0;
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items: displayedMixedItems, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [itemObjectFilter, group.id] });
  const selectedCount = selectedIds.size;
  const hydratedItems = useGroupItemEntities(displayedMixedItems);
  const canReorderMixedItems = !isDynamic
    && canWriteGroup
    && displayedMixedItems.length > 1
    && !mixedFilter.q
    && !infinitePageSize
    && Object.keys(itemObjectFilter).length === 0
    && (mixedFilter.sort ?? "order") === "order"
    && (mixedFilter.page ?? 1) === 1;
  const existingSubGroupIds = new Set(subGroups.map((subGroup) => subGroup.id));
  // Built-in groups (Save for Later, Watch History, Continue Watching) can't participate in
  // parent/child relations, so keep them out of the sub-group picker.
  const availableGroupResults = (searchResults?.items ?? []).filter((candidate) => candidate.id !== group.id && !existingSubGroupIds.has(candidate.id) && !isProtectedBuiltInGroup(candidate.querySourceKey));
  const loadedSelectedItems = useMemo(() => displayedMixedItems.filter((item) => selectedIds.has(item.id)), [displayedMixedItems, selectedIds]);
  const selectedDeletableKinds = useMemo(() => getSelectedDeletableKinds(loadedSelectedItems, hasPermission), [hasPermission, loadedSelectedItems]);
  const canDeleteSelectedItems = selectedCount > 0 && (selectedDeletableKinds.size > 0 || canDeleteAnyMixedHostType(hasPermission));

  useEffect(() => {
    if (infinitePageSize) return;

    const perPage = mixedFilter.perPage && mixedFilter.perPage > 0 ? mixedFilter.perPage : Math.max(totalItemCount, 1);
    const totalPages = Math.max(1, Math.ceil(totalItemCount / Math.max(perPage, 1)));
    const currentPage = mixedFilter.page ?? 1;
    if (currentPage <= totalPages) return;

    setMixedFilter({ ...mixedFilter, page: totalPages });
  }, [infinitePageSize, mixedFilter.page, mixedFilter.perPage, totalItemCount]);

  const getSelectedItemsByIds = useCallback(async (ids: Set<string>) => {
    const loadedItemsById = new Map(displayedMixedItems.map((item) => [item.id, item]));
    if ([...ids].every((id) => loadedItemsById.has(id))) {
      return [...ids].map((id) => loadedItemsById.get(id)).filter((item): item is MixedGroupItem => item != null);
    }

    const allItemsPage = await queryMixedItemsPage({ ...mixedFilter, page: 1, perPage: 0 });
    return allItemsPage.items.filter((item) => ids.has(item.id));
  }, [displayedMixedItems, mixedFilter, queryMixedItemsPage]);

  const invalidateGroupItems = () => {
    queryClient.invalidateQueries({ queryKey: ["group-items", group.id] });
    queryClient.invalidateQueries({ queryKey: ["group-mixed-items", group.id] });
    queryClient.invalidateQueries({ queryKey: ["group-items-page-all"] });
    queryClient.invalidateQueries({ queryKey: ["group-subgroups", group.id] });
    queryClient.invalidateQueries({ queryKey: ["group", group.id] });
    queryClient.invalidateQueries({ queryKey: ["groups"] });
  };

  const removeFromGroupMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (keys: string[]) => {
      const items = await getSelectedItemsByIds(new Set(keys));
      for (const item of items) {
        if (item.source === "item") {
          await groups.items.delete(group.id, item.item.id);
        } else {
          await groups.removeSubGroup(group.id, item.group.id);
        }
      }
    },
    onSuccess: () => { setConfirmSelectionAction(null); selectNone(); invalidateGroupItems(); },
  });
  const deleteSelectedHostsMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async (keys: string[]) => deleteSelectedGroupHosts(await getSelectedItemsByIds(new Set(keys)), hasPermission),
    onSuccess: () => { setConfirmSelectionAction(null); selectNone(); },
  });

  const reorderItemMutation = useMutation({
    mutationFn: (ids: number[]) => groups.items.reorder(group.id, { ids }),
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: ["group-items", group.id] });
      const previousItems = queryClient.getQueryData<GroupItem[]>(["group-items", group.id]) ?? [];
      const itemsById = new Map(previousItems.map((item) => [item.id, item]));
      const nextItems = ids
        .map((itemId, index) => {
          const item = itemsById.get(itemId);
          return item ? { ...item, orderIndex: index } : undefined;
        })
        .filter((item): item is GroupItem => item != null);
      queryClient.setQueryData(["group-items", group.id], nextItems);
      return { previousItems };
    },
    onError: (_error, _ids, context) => {
      if (context?.previousItems) queryClient.setQueryData(["group-items", group.id], context.previousItems);
    },
    onSettled: invalidateGroupItems,
  });
  const reorderMixedMutation = useMutation({
    mutationFn: async (nextItems: MixedGroupItem[]) => {
      const itemIds = nextItems.filter((item) => item.source === "item").map((item) => item.item.id);
      const subGroupIds = nextItems.filter((item) => item.source === "subgroup").map((item) => item.group.id);
      if (itemIds.length > 1) {
        await groups.items.reorder(group.id, { ids: itemIds });
      }
      if (subGroupIds.length > 1) {
        await groups.reorderSubGroups(group.id, subGroupIds);
      }
    },
    onMutate: async (nextItems) => {
      await Promise.all([
        queryClient.cancelQueries({ queryKey: ["group-items", group.id] }),
        queryClient.cancelQueries({ queryKey: ["group-subgroups", group.id] }),
      ]);
      const previousItems = queryClient.getQueryData<GroupItem[]>(["group-items", group.id]) ?? [];
      const previousSubGroups = queryClient.getQueryData<Group[]>(["group-subgroups", group.id]) ?? [];
      const itemsById = new Map(previousItems.map((item) => [item.id, item]));
      const groupsById = new Map(previousSubGroups.map((subGroup) => [subGroup.id, subGroup]));
      const nextDirectItems = nextItems
        .filter((item) => item.source === "item")
        .map((item, index) => {
          const existing = itemsById.get(item.item.id);
          return existing ? { ...existing, orderIndex: index } : undefined;
        })
        .filter((item): item is GroupItem => item != null);
      const nextSubGroups = nextItems
        .filter((item) => item.source === "subgroup")
        .map((item) => groupsById.get(item.group.id))
        .filter((subGroup): subGroup is Group => subGroup != null);

      queryClient.setQueryData(["group-items", group.id], nextDirectItems);
      queryClient.setQueryData(["group-subgroups", group.id], nextSubGroups);
      return { previousItems, previousSubGroups };
    },
    onError: (_error, _nextItems, context) => {
      if (context?.previousItems) queryClient.setQueryData(["group-items", group.id], context.previousItems);
      if (context?.previousSubGroups) queryClient.setQueryData(["group-subgroups", group.id], context.previousSubGroups);
    },
    onSettled: invalidateGroupItems,
  });
  const addSubGroupMutation = useMutation({
    mutationFn: (subGroupId: number) => groups.addSubGroup(group.id, subGroupId),
    onSuccess: () => {
      setSearchTerm("");
      setShowAddDialog(false);
      invalidateGroupItems();
    },
  });

  const selectionDialogs = (
    <>
      <ConfirmDialog
        open={confirmSelectionAction === "remove"}
        title="Remove from group"
        message={`Remove ${selectedCount} selected item${selectedCount === 1 ? "" : "s"} from ${group.name}?`}
        confirmLabel={removeFromGroupMutation.isPending ? "Removing..." : "Remove"}
        onConfirm={() => removeFromGroupMutation.mutate([...selectedIds])}
        onCancel={() => setConfirmSelectionAction(null)}
        isPending={removeFromGroupMutation.isPending}
        errorMessage={removeFromGroupMutation.error instanceof Error ? removeFromGroupMutation.error.message : undefined}
      />
      <ConfirmDialog
        open={confirmSelectionAction === "delete"}
        title="Delete selected items"
        message={`Delete ${selectedDeletableKinds.size > 1 ? "the supported" : ""} selected item${selectedCount === 1 ? "" : "s"}? This deletes the underlying entities, not just the group entries.`}
        confirmLabel={deleteSelectedHostsMutation.isPending ? "Queueing..." : "Queue deletion"}
        onConfirm={() => deleteSelectedHostsMutation.mutate([...selectedIds])}
        onCancel={() => setConfirmSelectionAction(null)}
        isPending={deleteSelectedHostsMutation.isPending}
        errorMessage={deleteSelectedHostsMutation.error instanceof Error ? deleteSelectedHostsMutation.error.message : undefined}
      />
    </>
  );

  if (groupItemsLoading || subGroupsLoading || mixedItemsLoading) {
    return <LoadingPanel icon={<Layers className="h-10 w-10" />} message="Loading group items..." />;
  }

  if (prerequisiteError) {
    return <ListLoadError error={prerequisiteError} onRetry={() => { retryGroupItems(); void retrySubGroups(); }} className="mt-3" />;
  }

  const toolbar = (
    <DetailListToolbar
      filter={mixedFilter}
      onFilterChange={setMixedFilter}
      totalCount={totalItemCount}
      sortOptions={GROUP_ITEM_SORT_OPTIONS}
      zoomLevel={zoomLevel}
      onZoomChange={setZoomLevel}
      cardSizeEntityType="groups"
      showSearch
      selectedCount={selectedCount}
      onSelectAll={selectAll}
      selectAllPending={selectAllPending}
      onSelectAllMatching={selectShown}
      selectAllMatchingLabel="Select shown"
      onSelectNone={selectNone}
      displayMode={viewMode}
      onDisplayModeChange={(mode) => {
        if (mode === "grid" || mode === "list") setGroupViewMode(mode);
      }}
      availableDisplayModes={["grid", "list"]}
      criteriaDefinitions={GROUP_ITEM_CRITERIA}
      objectFilter={itemObjectFilter}
      onObjectFilterChange={setItemObjectFilter}
      filterMode="groupitems"
      filterDefaultKey={`groupitems-${group.id}`}
      defaultFilterResolved
      allowInfinitePageSize
      selectionActions={(
        <>
          {canWriteGroup && !isDynamic ? (
            <button type="button" onClick={() => setConfirmSelectionAction("remove")} disabled={removeFromGroupMutation.isPending} className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-orange-400 transition hover:bg-orange-900/20 hover:text-orange-300 disabled:opacity-60">
              {removeFromGroupMutation.isPending ? <Loader2 className="h-3 w-3 animate-spin" /> : <Unlink className="h-3 w-3" />}
              Remove from group
            </button>
          ) : null}
          {canDeleteSelectedItems ? (
            <button type="button" onClick={() => setConfirmSelectionAction("delete")} disabled={deleteSelectedHostsMutation.isPending} className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-red-400 transition hover:bg-red-900/20 hover:text-red-300 disabled:opacity-60">
              {deleteSelectedHostsMutation.isPending ? <Loader2 className="h-3 w-3 animate-spin" /> : <Trash2 className="h-3 w-3" />}
              Delete
            </button>
          ) : null}
        </>
      )}
    />
  );

  const addSubGroupDialog = showAddDialog && canWriteGroup && !isDynamic && canReadGroups ? (
    <div className="mx-auto mb-4 max-w-7xl rounded-xl border border-border bg-card p-4">
      <div className="mb-3 flex items-center gap-2">
        <input
          type="text"
          value={searchTerm}
          onChange={(event) => setSearchTerm(event.target.value)}
          placeholder="Search groups to add..."
          className="flex-1 rounded border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
          autoFocus
        />
        <button type="button" onClick={() => { setShowAddDialog(false); setSearchTerm(""); }} className="rounded p-1.5 text-muted hover:bg-surface"><X className="h-4 w-4" /></button>
      </div>
      {availableGroupResults.length > 0 ? (
        <div className="max-h-48 space-y-1 overflow-y-auto">
          {availableGroupResults.map((candidate) => (
            <button key={candidate.id} type="button" onClick={() => addSubGroupMutation.mutate(candidate.id)} disabled={addSubGroupMutation.isPending} className="flex w-full items-center justify-between rounded px-3 py-2 text-left text-sm text-foreground hover:bg-surface disabled:opacity-50">
              <span>{candidate.name}</span>
              <Plus className="h-3.5 w-3.5 text-muted" />
            </button>
          ))}
        </div>
      ) : searchTerm.trim().length > 0 ? (
        <p className="py-4 text-center text-sm text-muted">No groups found</p>
      ) : (
        <p className="py-4 text-center text-sm text-muted">Type to search for groups</p>
      )}
    </div>
  ) : null;

  if (mixedItemsLoadError) {
    return <ListLoadError error={mixedItemsLoadError} onRetry={() => { void retryMixedItems(); }} className="mt-3" />;
  }

  if (staticMixedItems.length === 0 && !isDynamic && canReadVideos) {
    return <>{selectionDialogs}{addSubGroupDialog}<GroupVideosPanel groupId={group.id} filter={filter} setFilter={setFilter} onNavigate={onNavigate} groupItems={groupItems} groupItemsLoading={groupItemsLoading} canWriteGroup={canWriteGroup} /></>;
  }

  if (totalItemCount === 0) {
    return <>{selectionDialogs}{toolbar}{addSubGroupDialog}<EmptyPanel icon={<Layers className="h-12 w-12" />} message={isDynamic ? "No items currently resolve for this dynamic group" : "No items in this group"} /></>;
  }

  if (viewMode === "list") {
    return (
      <div>
        {selectionDialogs}
        {toolbar}
        {addSubGroupDialog}
        {infinitePageSize ? (
          <VirtualizedInfiniteList
            items={displayedMixedItems}
            getItemKey={(item) => item.id}
            estimateSize={76}
            hasNextPage={Boolean(infiniteQuery.hasNextPage)}
            isFetchingNextPage={Boolean(infiniteQuery.isFetchingNextPage)}
            loadMore={loadMore}
            className="space-y-2"
            renderItem={({ item, measureRef }) => (
              <div ref={measureRef}>
                <GroupItemRow item={item} onNavigate={onNavigate} selected={selectedIds.has(item.id)} onToggleSelect={() => toggle(item.id)} />
              </div>
            )}
          />
        ) : (
          <SortableList
            items={displayedMixedItems}
            getKey={mixedItemKey}
            onReorder={(nextItems) => reorderMixedMutation.mutate(nextItems)}
            disabled={!canReorderMixedItems || reorderMixedMutation.isPending || reorderItemMutation.isPending}
            className="space-y-2"
            renderItem={(item, { dragHandleProps, isDragging, isOver }) => (
              <GroupItemRow item={item} onNavigate={onNavigate} selected={selectedIds.has(item.id)} onToggleSelect={() => toggle(item.id)} dragHandleProps={canReorderMixedItems ? dragHandleProps : undefined} isDragging={isDragging} isOver={isOver} />
            )}
          />
        )}
        <DetailListPagination filter={mixedFilter} onFilterChange={setMixedFilter} totalCount={totalItemCount} allowInfinitePageSize />
      </div>
    );
  }

  const minCardWidth = getEntityCardMinWidthPx("groups", zoomLevel);

  return (
    <div>
      {selectionDialogs}
      {toolbar}
      {addSubGroupDialog}
      <VirtualizedEntityGrid
        items={displayedMixedItems}
        getItemKey={mixedItemKey}
        minCardWidth={`${minCardWidth}px`}
        virtualMinColumnWidth={minCardWidth}
        estimateRowHeight={210}
        gap={16}
        gapClassName="gap-4"
        infinitePageSize={infinitePageSize}
        hasNextPage={infiniteQuery.hasNextPage}
        isFetchingNextPage={infiniteQuery.isFetchingNextPage}
        loadMore={loadMore}
        renderItem={(item) => <GroupItemGridCard item={item} hydrated={hydratedItems.get(item.id)} onNavigate={onNavigate} selected={selectedIds.has(item.id)} onToggleSelect={() => toggle(item.id)} selecting={selectedCount > 0} />}
      />
      <DetailListPagination filter={mixedFilter} onFilterChange={setMixedFilter} totalCount={totalItemCount} allowInfinitePageSize />
    </div>
  );
}

function toMixedGroupItem(item: GroupItem): MixedGroupItem {
  return { source: "item", id: `item-${item.id}`, item, orderIndex: item.orderIndex, kind: item.kind };
}

function toMixedSubGroupItem(group: Group, orderIndex: number): MixedGroupItem {
  return { source: "subgroup", id: `subgroup-${group.id}`, group, orderIndex, kind: "group" };
}

function buildMixedGroupItems(groupItems: GroupItem[], subGroups: Group[], isDynamic: boolean) {
  const directItems = [...groupItems].sort((left, right) => left.orderIndex - right.orderIndex || left.id - right.id);
  const items: MixedGroupItem[] = directItems.map(toMixedGroupItem);
  if (!isDynamic) {
    items.push(...subGroups.map((subGroup, index) => toMixedSubGroupItem(subGroup, directItems.length + index)));
  }
  return items;
}

function pageMixedGroupItems(items: MixedGroupItem[], filter: FindFilter, objectFilter: Record<string, unknown>, hostData?: HydratedGroupItemMap, engagementData?: GroupItemEngagementMap, pathCaseSensitive = true) {
  const query = filter.q?.trim().toLowerCase();
  const searchedItems = query
    ? items.filter((item) => {
        const metadata = getMixedItemMetadata(item, hostData, engagementData);
        const hostId = metadata.hostId;
        return metadata.title.toLowerCase().includes(query)
          || (metadata.code?.toLowerCase().includes(query) ?? false)
          || (metadata.details?.toLowerCase().includes(query) ?? false)
          || metadata.paths.some((path) => path.toLowerCase().includes(query))
          || metadata.urls.some((url) => url.toLowerCase().includes(query))
          || labelForGroupItemKind(metadata.kind as GroupItemKind).toLowerCase().includes(query)
          || (hostId != null && String(hostId).includes(query));
      })
    : items;
  const filteredItems = searchedItems.filter((item) => matchesGroupItemObjectFilter(item, objectFilter, hostData, engagementData, pathCaseSensitive));
  const sortedItems = sortMixedGroupItems(filteredItems, filter.sort, filter.direction, filter.seed, hostData, engagementData);
  const page = Math.max(1, filter.page ?? 1);
  const infinitePageSize = (filter.perPage ?? 40) <= 0;
  const perPage = infinitePageSize ? Math.max(sortedItems.length, 1) : Math.max(1, filter.perPage ?? 40);
  const start = infinitePageSize ? 0 : (page - 1) * perPage;
  return {
    items: infinitePageSize ? sortedItems : sortedItems.slice(start, start + perPage),
    totalCount: sortedItems.length,
    page,
    perPage: infinitePageSize ? 0 : perPage,
  };
}

function requiresFullDynamicItemResolution(filter: FindFilter, objectFilter: Record<string, unknown>) {
  return Boolean(filter.q?.trim()) || Object.keys(objectFilter).length > 0 || (filter.sort ?? "order") !== "order";
}

const HOST_METADATA_SORTS = new Set([
  "title",
  "code",
  "date",
  "rating",
  "organized",
  "path",
  "url",
  "studio",
  "tag_count",
  "performer_count",
  "file_count",
  "duration",
  "word_count",
  "page_count",
  "source_key",
  "confidence",
  "created_at",
  "updated_at",
]);

const HOST_METADATA_FILTER_KEYS = new Set([
  "titleCriterion",
  "codeCriterion",
  "detailsCriterion",
  "pathCriterion",
  "urlCriterion",
  "ratingCriterion",
  "organizedCriterion",
  "dateCriterion",
  "performersCriterion",
  "tagsCriterion",
  "studiosCriterion",
  "performerCountCriterion",
  "tagCountCriterion",
  "fileCountCriterion",
  "durationCriterion",
  "wordCountCriterion",
  "pageCountCriterion",
  "sourceKeyCriterion",
  "segmentKindCriterion",
  "confidenceCriterion",
  "createdAtCriterion",
  "updatedAtCriterion",
]);

function requiresHostMetadata(filter: FindFilter, objectFilter: Record<string, unknown>) {
  return Boolean(filter.q?.trim())
    || HOST_METADATA_SORTS.has(filter.sort ?? "")
    || Object.keys(objectFilter).some((key) => HOST_METADATA_FILTER_KEYS.has(key));
}

function requiresEngagementMetadata(filter: FindFilter, objectFilter: Record<string, unknown>) {
  return filter.sort === "rating" || objectFilter.ratingCriterion != null;
}

function normalizeGroupItemPageFilter(filter: FindFilter): FindFilter {
  return {
    ...filter,
    sort: "order",
    perPage: filter.perPage && filter.perPage > 0 ? filter.perPage : 0,
  };
}

function mixedItemKey(item: MixedGroupItem) {
  return item.id;
}

type HydratedGroupItemMap = Map<string, HydratedGroupItemData>;
type GroupItemEngagementMap = Map<string, EntityEngagement>;

interface MixedGroupItemMetadata {
  kind: string;
  hostId?: number;
  title: string;
  code?: string;
  details?: string;
  date?: string;
  addedAt?: string;
  createdAt?: string;
  updatedAt?: string;
  organized?: boolean;
  rating?: number;
  urls: string[];
  paths: string[];
  tagIds: number[];
  performerIds: number[];
  studioIds: number[];
  studioName?: string;
  tagCount?: number;
  performerCount?: number;
  fileCount?: number;
  duration?: number;
  wordCount?: number;
  pageCount?: number;
  startSec?: number;
  endSec?: number;
  sourceKey?: string;
  segmentKind?: string;
  confidence?: number;
}

async function fetchMixedItemHostData(queryClient: ReturnType<typeof useQueryClient>, items: MixedGroupItem[]) {
  const entries = await Promise.all(items.map(async (item) => {
    if (item.source !== "item" || !isHydratableGroupItemKind(item.kind)) return null;
    const query = createGroupItemEntityQuery(item);
    if (!query.enabled) return null;

    try {
      const data = await queryClient.fetchQuery({ queryKey: query.queryKey, queryFn: query.queryFn, staleTime: query.staleTime });
      return [item.id, data] as const;
    } catch {
      return null;
    }
  }));

  return new Map(entries.filter((entry): entry is readonly [string, HydratedGroupItemData] => entry != null));
}

async function fetchMixedItemEngagementData(queryClient: ReturnType<typeof useQueryClient>, items: MixedGroupItem[]) {
  const idsByType = new Map<AffinityHostType, Set<number>>();
  for (const item of items) {
    const host = getEngagementHost(item);
    if (!host) continue;
    const ids = idsByType.get(host.hostType) ?? new Set<number>();
    ids.add(host.hostId);
    idsByType.set(host.hostType, ids);
  }

  const batches = await Promise.all([...idsByType.entries()].map(async ([hostType, ids]) => {
    const hostIds = [...ids].sort((left, right) => left - right);
    try {
      return await queryClient.fetchQuery({
        queryKey: ["group-item-engagement", hostType, hostIds.join(",")],
        queryFn: () => entityEngagement.batch({ hostType, hostIds }),
        staleTime: 60000,
      });
    } catch {
      return [];
    }
  }));

  const map = new Map<string, EntityEngagement>();
  [...idsByType.keys()].forEach((hostType, index) => {
    for (const engagement of batches[index] ?? []) {
      map.set(engagementKey(hostType, engagement.hostId), engagement);
    }
  });
  return map;
}

function getMixedItemMetadata(item: MixedGroupItem, hostData?: HydratedGroupItemMap, engagementData?: GroupItemEngagementMap): MixedGroupItemMetadata {
  const host = groupItemHost(item);
  const hostId = getMixedItemHostIdValue(item);
  const data = hostData?.get(item.id);
  const engagement = getMixedItemEngagement(item, engagementData);
  const base: MixedGroupItemMetadata = {
    kind: getGroupItemFilterKind(item),
    hostId,
    title: host.title,
    addedAt: item.source === "subgroup" ? item.group.createdAt : item.item.createdAt,
    createdAt: item.source === "subgroup" ? item.group.createdAt : item.item.createdAt,
    updatedAt: item.source === "subgroup" ? item.group.updatedAt : item.item.updatedAt,
    rating: engagement?.rating,
    urls: [],
    paths: [],
    tagIds: [],
    performerIds: [],
    studioIds: [],
    startSec: item.source === "item" ? item.item.startSec ?? undefined : undefined,
    endSec: item.source === "item" ? item.item.endSec ?? undefined : undefined,
  };

  if (item.source === "subgroup") {
    return {
      ...base,
      title: item.group.name,
      date: item.group.date,
      urls: item.group.urls ?? [],
      tagIds: item.group.tags?.map((tag) => tag.id) ?? [],
      studioIds: item.group.studioId ? [item.group.studioId] : [],
      studioName: item.group.studioName,
      tagCount: item.group.tags?.length ?? item.group.tagItemCount ?? 0,
    };
  }

  switch (data?.type) {
    case "video": {
      const video = data.video;
      const duration = String(item.kind).toLowerCase() === "videorange" && (item.item.startSec != null || item.item.endSec != null)
        ? Math.max(0, (item.item.endSec ?? video.files[0]?.duration ?? 0) - (item.item.startSec ?? 0))
        : maxNumber(video.files.map((file) => file.duration));
      return {
        ...base,
        title: item.item.title || video.title || base.title,
        code: video.code,
        details: video.details,
        date: video.date,
        createdAt: video.createdAt,
        updatedAt: video.updatedAt,
        organized: video.organized,
        urls: video.urls ?? [],
        paths: video.files.map((file) => file.path).filter(Boolean),
        tagIds: video.tags.map((tag) => tag.id),
        performerIds: video.performers.map((performer) => performer.id),
        studioIds: video.studioId ? [video.studioId] : [],
        studioName: video.studioName,
        tagCount: video.tags.length,
        performerCount: video.performers.length,
        fileCount: video.files.length,
        duration,
      };
    }
    case "image": {
      const image = data.image;
      return {
        ...base,
        title: item.item.title || image.title || base.title,
        code: image.code,
        details: image.details,
        date: image.date,
        createdAt: image.createdAt,
        updatedAt: image.updatedAt,
        organized: image.organized,
        urls: image.urls ?? [],
        paths: image.files.map((file) => file.path).filter(Boolean),
        tagIds: image.tags.map((tag) => tag.id),
        performerIds: image.performers.map((performer) => performer.id),
        studioIds: image.studioId ? [image.studioId] : [],
        studioName: image.studioName,
        tagCount: image.tags.length,
        performerCount: image.performers.length,
        fileCount: image.files.length,
      };
    }
    case "audio": {
      const audio = data.audio;
      return {
        ...base,
        title: item.item.title || audio.title || base.title,
        code: audio.code,
        details: audio.details,
        date: audio.date,
        createdAt: audio.createdAt,
        updatedAt: audio.updatedAt,
        organized: audio.organized,
        urls: audio.urls ?? [],
        paths: audio.files.map((file) => file.path).filter(Boolean),
        tagIds: audio.tags.map((tag) => tag.id),
        performerIds: audio.performers.map((performer) => performer.id),
        studioIds: audio.studioId ? [audio.studioId] : [],
        studioName: audio.studioName,
        tagCount: audio.tags.length,
        performerCount: audio.performers.length,
        fileCount: audio.fileCount ?? audio.files.length,
        duration: audio.maxDuration,
      };
    }
    case "text": {
      const text = data.text;
      return {
        ...base,
        title: item.item.title || text.title || base.title,
        code: text.code,
        details: text.details,
        date: text.date,
        createdAt: text.createdAt,
        updatedAt: text.updatedAt,
        organized: text.organized,
        urls: text.urls ?? [],
        paths: text.files.map((file) => file.path).filter(Boolean),
        tagIds: text.tags.map((tag) => tag.id),
        performerIds: text.performers.map((performer) => performer.id),
        studioIds: text.studioId ? [text.studioId] : [],
        studioName: text.studioName,
        tagCount: text.tags.length,
        performerCount: text.performers.length,
        fileCount: text.fileCount ?? text.files.length,
        wordCount: text.maxWordCount ?? undefined,
        pageCount: text.maxPageCount ?? undefined,
      };
    }
    case "segment": {
      const segment = data.segment;
      return {
        ...base,
        title: item.item.title || segment.title || segment.tagName || segment.performerName || segment.refLabel || segment.kind || base.title,
        createdAt: segment.createdAt,
        updatedAt: segment.updatedAt,
        tagIds: segment.tagId ? [segment.tagId] : [],
        performerIds: segment.performerId ? [segment.performerId] : [],
        tagCount: segment.tagId ? 1 : 0,
        performerCount: segment.performerId ? 1 : 0,
        startSec: segment.startSec,
        endSec: segment.endSec,
        duration: segment.endSec == null ? undefined : Math.max(0, segment.endSec - segment.startSec),
        sourceKey: segment.sourceKey,
        segmentKind: segment.kind,
        confidence: segment.confidence,
      };
    }
    case "group": {
      const group = data.group;
      return {
        ...base,
        title: group.name,
        date: group.date,
        createdAt: group.createdAt,
        updatedAt: group.updatedAt,
        urls: group.urls ?? [],
        tagIds: group.tags?.map((tag) => tag.id) ?? [],
        studioIds: group.studioId ? [group.studioId] : [],
        studioName: group.studioName,
        tagCount: group.tags?.length ?? group.tagItemCount ?? 0,
      };
    }
    default:
      return base;
  }
}

function getGroupItemFilterKind(item: MixedGroupItem) {
  if (item.source === "subgroup") return "group";
  const normalized = String(item.kind).toLowerCase();
  return normalized === "videorange" ? "video" : normalized;
}

function isHydratableGroupItemKind(kind: GroupItemKind) {
  const normalized = String(kind).toLowerCase();
  return ["video", "videorange", "image", "audio", "text", "group", "segment"].includes(normalized);
}

function getEngagementHost(item: MixedGroupItem): { hostType: AffinityHostType; hostId: number } | null {
  const hostId = getMixedItemHostIdValue(item);
  if (!hostId) return null;
  const kind = getGroupItemFilterKind(item);
  if (["video", "image", "audio", "text", "group"].includes(kind)) {
    return { hostType: kind as AffinityHostType, hostId };
  }
  return null;
}

function getMixedItemEngagement(item: MixedGroupItem, engagementData?: GroupItemEngagementMap) {
  const host = getEngagementHost(item);
  return host ? engagementData?.get(engagementKey(host.hostType, host.hostId)) : undefined;
}

function engagementKey(hostType: AffinityHostType, hostId: number) {
  return `${hostType}-${hostId}`;
}

function maxNumber(values: Array<number | null | undefined>) {
  const finiteValues = values.filter((value): value is number => typeof value === "number" && Number.isFinite(value));
  return finiteValues.length > 0 ? Math.max(...finiteValues) : undefined;
}

function groupItemHost(item: MixedGroupItem) {
  if (item.source === "subgroup") {
    return { title: item.group.name, subtitle: `${item.group.videoCount} video${item.group.videoCount === 1 ? "" : "s"}`, kind: "group" as GroupItemKind, route: { page: "group", id: item.group.id } };
  }

  const groupItem = item.item;
  const hostType = (groupItem.hostType || groupItem.kind).toLowerCase();
  const hostId = groupItem.hostId || groupItem.videoId || groupItem.imageId || groupItem.childGroupId;
  const title = groupItem.title || groupItem.videoTitle || groupItem.imageTitle || groupItem.childGroupName || `${labelForGroupItemKind(groupItem.kind)} #${hostId ?? groupItem.id}`;
  const route = routeForGroupItem(groupItem, hostType, hostId ?? null);
  const subtitle = String(groupItem.kind).toLowerCase() === "videorange" ? formatDurationRange(groupItem.startSec, groupItem.endSec) : labelForGroupItemKind(groupItem.kind);
  return { title, subtitle, kind: groupItem.kind, route };
}

function sortMixedGroupItems(items: MixedGroupItem[], sort?: string, direction?: FindFilter["direction"], seed?: number, hostData?: HydratedGroupItemMap, engagementData?: GroupItemEngagementMap) {
  if (sort === "random") {
    return sortSeededRandom(items, (item) => item.id, seed, direction === "desc");
  }

  const sorted = [...items];
  sorted.sort((left, right) => {
    const leftMeta = getMixedItemMetadata(left, hostData, engagementData);
    const rightMeta = getMixedItemMetadata(right, hostData, engagementData);

    let comparison = 0;
    switch (sort ?? "order") {
      case "title":
        comparison = compareOptionalStrings(leftMeta.title, rightMeta.title);
        break;
      case "code":
        comparison = compareOptionalStrings(leftMeta.code, rightMeta.code);
        break;
      case "date":
        comparison = compareOptionalDates(leftMeta.date, rightMeta.date);
        break;
      case "kind":
        comparison = compareOptionalStrings(leftMeta.kind, rightMeta.kind);
        break;
      case "rating":
        comparison = compareOptionalNumbers(leftMeta.rating, rightMeta.rating);
        break;
      case "organized":
        comparison = compareOptionalNumbers(boolSortValue(leftMeta.organized), boolSortValue(rightMeta.organized));
        break;
      case "path":
        comparison = compareOptionalStrings(leftMeta.paths[0], rightMeta.paths[0]);
        break;
      case "url":
        comparison = compareOptionalStrings(leftMeta.urls[0], rightMeta.urls[0]);
        break;
      case "studio":
        comparison = compareOptionalStrings(leftMeta.studioName, rightMeta.studioName) || compareOptionalNumbers(leftMeta.studioIds[0], rightMeta.studioIds[0]);
        break;
      case "tag_count":
        comparison = compareOptionalNumbers(leftMeta.tagCount, rightMeta.tagCount);
        break;
      case "performer_count":
        comparison = compareOptionalNumbers(leftMeta.performerCount, rightMeta.performerCount);
        break;
      case "file_count":
        comparison = compareOptionalNumbers(leftMeta.fileCount, rightMeta.fileCount);
        break;
      case "duration":
        comparison = compareOptionalNumbers(leftMeta.duration, rightMeta.duration);
        break;
      case "word_count":
        comparison = compareOptionalNumbers(leftMeta.wordCount, rightMeta.wordCount);
        break;
      case "page_count":
        comparison = compareOptionalNumbers(leftMeta.pageCount, rightMeta.pageCount);
        break;
      case "host_id":
        comparison = compareOptionalNumbers(leftMeta.hostId, rightMeta.hostId);
        break;
      case "range_start":
      case "start_sec":
        comparison = compareOptionalNumbers(leftMeta.startSec, rightMeta.startSec);
        break;
      case "range_end":
      case "end_sec":
        comparison = compareOptionalNumbers(leftMeta.endSec, rightMeta.endSec);
        break;
      case "source_key":
        comparison = compareOptionalStrings(leftMeta.sourceKey, rightMeta.sourceKey);
        break;
      case "confidence":
        comparison = compareOptionalNumbers(leftMeta.confidence, rightMeta.confidence);
        break;
      case "added_at":
        comparison = compareOptionalDates(leftMeta.addedAt, rightMeta.addedAt);
        break;
      case "created_at":
        comparison = compareOptionalDates(leftMeta.createdAt, rightMeta.createdAt);
        break;
      case "updated_at":
        comparison = compareOptionalDates(leftMeta.updatedAt, rightMeta.updatedAt);
        break;
      default:
        comparison = left.orderIndex - right.orderIndex;
        break;
    }

    if (comparison === 0) {
      comparison = left.id.localeCompare(right.id, undefined, { numeric: true });
    }

    return direction === "desc" ? -comparison : comparison;
  });
  return sorted;
}

function compareOptionalStrings(left?: string | null, right?: string | null) {
  const normalizedLeft = left?.trim();
  const normalizedRight = right?.trim();
  if (!normalizedLeft && !normalizedRight) return 0;
  if (!normalizedLeft) return 1;
  if (!normalizedRight) return -1;
  return normalizedLeft.localeCompare(normalizedRight, undefined, { numeric: true, sensitivity: "base" });
}

function compareOptionalNumbers(left?: number | null, right?: number | null) {
  if (left == null && right == null) return 0;
  if (left == null) return 1;
  if (right == null) return -1;
  return left - right;
}

function compareOptionalDates(left?: string | null, right?: string | null) {
  const leftTime = left ? Date.parse(left) : Number.NaN;
  const rightTime = right ? Date.parse(right) : Number.NaN;
  if (!Number.isFinite(leftTime) && !Number.isFinite(rightTime)) return 0;
  if (!Number.isFinite(leftTime)) return 1;
  if (!Number.isFinite(rightTime)) return -1;
  return leftTime - rightTime;
}

function boolSortValue(value?: boolean) {
  return value == null ? undefined : value ? 1 : 0;
}

function matchesGroupItemObjectFilter(item: MixedGroupItem, objectFilter: Record<string, unknown>, hostData?: HydratedGroupItemMap, engagementData?: GroupItemEngagementMap, pathCaseSensitive = true) {
  if (Object.keys(objectFilter).length === 0) return true;

  const metadata = getMixedItemMetadata(item, hostData, engagementData);
  return matchesStringCriterion(metadata.title, objectFilter.titleCriterion as StringCriterion | undefined)
    && matchesStringCriterion(metadata.code, objectFilter.codeCriterion as StringCriterion | undefined)
    && matchesStringCriterion(metadata.details, objectFilter.detailsCriterion as StringCriterion | undefined)
    && matchesStringCriterion(metadata.kind, objectFilter.kindCriterion as StringCriterion | undefined)
    && matchesNumberCriterion(metadata.rating, objectFilter.ratingCriterion as IntCriterion | undefined)
    && matchesBoolCriterion(metadata.organized, objectFilter.organizedCriterion as BoolCriterion | undefined)
    && matchesStringCollectionCriterion(metadata.paths, objectFilter.pathCriterion as StringCriterion | undefined, pathCaseSensitive)
    && matchesStringCollectionCriterion(metadata.urls, objectFilter.urlCriterion as StringCriterion | undefined)
    && matchesDateCriterion(metadata.date, objectFilter.dateCriterion as DateCriterion | undefined)
    && matchesMultiIdCriterion(metadata.performerIds, objectFilter.performersCriterion as MultiIdCriterion | undefined)
    && matchesMultiIdCriterion(metadata.tagIds, objectFilter.tagsCriterion as MultiIdCriterion | undefined)
    && matchesMultiIdCriterion(metadata.studioIds, objectFilter.studiosCriterion as MultiIdCriterion | undefined)
    && matchesNumberCriterion(metadata.performerCount, objectFilter.performerCountCriterion as IntCriterion | undefined)
    && matchesNumberCriterion(metadata.tagCount, objectFilter.tagCountCriterion as IntCriterion | undefined)
    && matchesNumberCriterion(metadata.fileCount, objectFilter.fileCountCriterion as IntCriterion | undefined)
    && matchesNumberCriterion(metadata.duration, objectFilter.durationCriterion as IntCriterion | undefined)
    && matchesNumberCriterion(metadata.wordCount, objectFilter.wordCountCriterion as IntCriterion | undefined)
    && matchesNumberCriterion(metadata.pageCount, objectFilter.pageCountCriterion as IntCriterion | undefined)
    && matchesNumberCriterion(item.orderIndex + 1, objectFilter.itemNumberCriterion as IntCriterion | undefined)
    && matchesNumberCriterion(metadata.hostId, objectFilter.hostIdCriterion as IntCriterion | undefined)
    && matchesNumberCriterion(metadata.startSec, objectFilter.rangeStartCriterion as IntCriterion | undefined)
    && matchesNumberCriterion(metadata.endSec, objectFilter.rangeEndCriterion as IntCriterion | undefined)
    && matchesStringCriterion(metadata.sourceKey, objectFilter.sourceKeyCriterion as StringCriterion | undefined)
    && matchesStringCriterion(metadata.segmentKind, objectFilter.segmentKindCriterion as StringCriterion | undefined)
    && matchesNumberCriterion(metadata.confidence, objectFilter.confidenceCriterion as IntCriterion | undefined)
    && matchesTimestampCriterion(metadata.addedAt, objectFilter.addedAtCriterion as TimestampCriterion | undefined)
    && matchesTimestampCriterion(metadata.createdAt, objectFilter.createdAtCriterion as TimestampCriterion | undefined)
    && matchesTimestampCriterion(metadata.updatedAt, objectFilter.updatedAtCriterion as TimestampCriterion | undefined);
}

function matchesStringCriterion(value: string | undefined, criterion?: StringCriterion, pathCaseSensitive = true) {
  if (!criterion) return true;
  const rawValue = value?.trim() ?? "";
  const rawExpected = criterion.value?.trim() ?? "";
  const normalized = rawValue.toLowerCase();
  const expected = rawExpected.toLowerCase();
  const modifier = criterion.modifier ?? "EQUALS";
  const normalizedPath = normalizeFolderPath(pathCaseSensitive ? rawValue : normalized);
  const expectedPath = normalizeFolderPath(pathCaseSensitive ? rawExpected : expected);
  const expectedPrefix = expectedPath.endsWith("/") ? expectedPath : `${expectedPath}/`;

  switch (modifier) {
    case "IS_NULL": return normalized.length === 0;
    case "NOT_NULL": return normalized.length > 0;
    case "NOT_EQUALS": return normalized !== expected;
    case "INCLUDES": return normalized.includes(expected);
    case "EXCLUDES": return !normalized.includes(expected);
    case "MATCHES_REGEX": return matchesRegex(value ?? "", criterion.value ?? "");
    case "NOT_MATCHES_REGEX": return !matchesRegex(value ?? "", criterion.value ?? "");
    case "UNDER_PATH": return normalizedPath === expectedPath || normalizedPath.startsWith(expectedPrefix);
    case "NOT_UNDER_PATH": return normalizedPath !== expectedPath && !normalizedPath.startsWith(expectedPrefix);
    default: return normalized === expected;
  }
}

function normalizeFolderPath(value: string) {
  let normalized = value.trim().replaceAll("\\", "/");
  while (normalized.length > 1 && normalized.endsWith("/") && !(normalized.length === 3 && normalized[1] === ":")) {
    normalized = normalized.slice(0, -1);
  }
  return normalized;
}

function matchesStringCollectionCriterion(values: string[], criterion?: StringCriterion, pathCaseSensitive = true) {
  if (!criterion) return true;
  const modifier = criterion.modifier ?? "EQUALS";
  if (modifier === "IS_NULL") return values.length === 0 || values.every((value) => !value.trim());
  if (modifier === "NOT_NULL") return values.some((value) => value.trim().length > 0);
  if (modifier === "NOT_EQUALS" || modifier === "EXCLUDES" || modifier === "NOT_MATCHES_REGEX" || modifier === "NOT_UNDER_PATH") {
    return values.length === 0 || values.every((value) => matchesStringCriterion(value, criterion, pathCaseSensitive));
  }
  return values.some((value) => matchesStringCriterion(value, criterion, pathCaseSensitive));
}

function hasPathContainmentCriterion(objectFilter: Record<string, unknown>) {
  const modifier = (objectFilter.pathCriterion as StringCriterion | undefined)?.modifier;
  return modifier === "UNDER_PATH" || modifier === "NOT_UNDER_PATH";
}

function matchesBoolCriterion(value: boolean | undefined, criterion?: BoolCriterion) {
  if (!criterion) return true;
  return value === criterion.value;
}

function matchesNumberCriterion(value: number | undefined, criterion?: IntCriterion) {
  if (!criterion) return true;
  const modifier = criterion.modifier ?? "EQUALS";
  const expected = criterion.value;
  const expected2 = criterion.value2;

  switch (modifier) {
    case "IS_NULL": return value == null;
    case "NOT_NULL": return value != null;
    case "NOT_EQUALS": return value !== expected;
    case "GREATER_THAN": return value != null && expected != null && value > expected;
    case "LESS_THAN": return value != null && expected != null && value < expected;
    case "BETWEEN": return value != null && expected != null && expected2 != null && value >= Math.min(expected, expected2) && value <= Math.max(expected, expected2);
    case "NOT_BETWEEN": return value == null || expected == null || expected2 == null || value < Math.min(expected, expected2) || value > Math.max(expected, expected2);
    default: return value === expected;
  }
}

function matchesDateCriterion(value: string | undefined, criterion?: DateCriterion) {
  if (!criterion) return true;
  return matchesTimestampCriterion(value, criterion);
}

function matchesMultiIdCriterion(values: number[], criterion?: MultiIdCriterion) {
  if (!criterion) return true;
  const selected = criterion.value ?? [];
  const valueSet = new Set(values);
  const modifier = criterion.modifier ?? "INCLUDES";

  switch (modifier) {
    case "IS_NULL": return values.length === 0;
    case "NOT_NULL": return values.length > 0;
    case "EXCLUDES":
    case "NOT_EQUALS": return selected.every((id) => !valueSet.has(id));
    case "INCLUDES_ALL": return selected.every((id) => valueSet.has(id));
    case "EXCLUDES_ALL": return !selected.every((id) => valueSet.has(id));
    default: return selected.length === 0 || selected.some((id) => valueSet.has(id));
  }
}

function matchesRegex(value: string, pattern: string) {
  try {
    return new RegExp(pattern, "i").test(value);
  } catch {
    return false;
  }
}

function getSelectedDeletableKinds(items: MixedGroupItem[], hasPermission: (permission: string) => boolean) {
  const kinds = new Set<GroupItemKind | "group">();
  for (const item of items) {
    const host = getMixedItemHost(item);
    if (!host) continue;
    if (canDeleteEntity(host.permissionKind, hasPermission)) kinds.add(host.kind);
  }
  return kinds;
}

function canDeleteAnyMixedHostType(hasPermission: (permission: string) => boolean) {
  return canDeleteEntity("video", hasPermission)
    || canDeleteEntity("image", hasPermission)
    || canDeleteEntity("audio", hasPermission)
    || canDeleteEntity("text", hasPermission)
    || canDeleteEntity("group", hasPermission);
}

function getMixedItemHostIdValue(item: MixedGroupItem) {
  if (item.source === "subgroup") return item.group.id;
  return item.item.hostId || item.item.videoId || item.item.imageId || item.item.childGroupId || item.item.id;
}

async function deleteSelectedGroupHosts(items: MixedGroupItem[], hasPermission: (permission: string) => boolean) {
  const videoIds = new Set<number>();
  const imageIds = new Set<number>();
  const audioIds = new Set<number>();
  const textIds = new Set<number>();
  const groupIds = new Set<number>();

  for (const item of items) {
    const host = getMixedItemHost(item);
    if (!host || !canDeleteEntity(host.permissionKind, hasPermission)) continue;

    switch (host.kind) {
      case "video":
      case "videoRange":
        videoIds.add(host.hostId);
        break;
      case "image":
        imageIds.add(host.hostId);
        break;
      case "audio":
        audioIds.add(host.hostId);
        break;
      case "text":
        textIds.add(host.hostId);
        break;
      case "group":
        groupIds.add(host.hostId);
        break;
    }
  }

  await Promise.all([
    videoIds.size > 0 ? videos.bulkDelete([...videoIds]) : Promise.resolve(),
    imageIds.size > 0 ? images.bulkDelete([...imageIds]) : Promise.resolve(),
    audioIds.size > 0 ? audios.bulkDelete([...audioIds]) : Promise.resolve(),
    textIds.size > 0 ? texts.bulkDelete([...textIds]) : Promise.resolve(),
    groupIds.size > 0 ? groups.bulkDelete([...groupIds]) : Promise.resolve(),
  ]);
}

function getMixedItemHost(item: MixedGroupItem): { kind: GroupItemKind; hostId: number; permissionKind: "video" | "image" | "audio" | "text" | "group" } | null {
  if (item.source === "subgroup") {
    return { kind: "group", hostId: item.group.id, permissionKind: "group" };
  }

  switch (item.kind) {
    case "video":
    case "videoRange": {
      const hostId = item.item.videoId ?? item.item.hostId;
      return hostId ? { kind: item.kind, hostId, permissionKind: "video" } : null;
    }
    case "image": {
      const hostId = item.item.imageId ?? item.item.hostId;
      return hostId ? { kind: item.kind, hostId, permissionKind: "image" } : null;
    }
    case "audio":
      return item.item.hostId ? { kind: item.kind, hostId: item.item.hostId, permissionKind: "audio" } : null;
    case "text":
      return item.item.hostId ? { kind: item.kind, hostId: item.item.hostId, permissionKind: "text" } : null;
    case "group": {
      const hostId = item.item.childGroupId ?? item.item.hostId;
      return hostId ? { kind: item.kind, hostId, permissionKind: "group" } : null;
    }
    default:
      return null;
  }
}

function routeForGroupItem(item: GroupItem, hostType: string, hostId: number | null) {
  if (item.childGroupId) return { page: "group", id: item.childGroupId };
  // Only seek when the item carries a real position (spans/ranges). A plain video item has no
  // startSec, so omit seekTo and let the video page resume from engagement rather than restart at 0.
  if (item.videoId) return item.startSec && item.startSec > 0
    ? { page: "video", id: item.videoId, seekTo: item.startSec }
    : { page: "video", id: item.videoId };
  if (item.imageId) return { page: "image", id: item.imageId };
  if (!hostId) return null;
  if (["audio", "text", "gallery", "performer", "studio", "tag", "face", "group", "image", "video", "segment"].includes(hostType)) {
    return { page: hostType === "text" ? "text" : hostType, id: hostId };
  }
  return null;
}

function labelForGroupItemKind(kind: GroupItemKind) {
  switch (String(kind).toLowerCase()) {
    case "videorange": return "Video Range";
    case "image": return "Image";
    case "audio": return "Audio";
    case "text": return "Text";
    case "group": return "Group";
    case "performer": return "Performer";
    case "studio": return "Studio";
    case "tag": return "Tag";
    case "gallery": return "Gallery";
    case "face": return "Face";
    case "segment": return "Segment";
    default: return "Video";
  }
}

function GroupItemKindIcon({ kind, className = "h-4 w-4" }: { kind: GroupItemKind; className?: string }) {
  switch (String(kind).toLowerCase()) {
    case "image": return <Images className={className} />;
    case "audio": return <Headphones className={className} />;
    case "text": return <FileText className={className} />;
    case "group": return <Layers className={className} />;
    case "performer": return <User className={className} />;
    case "studio": return <Building2 className={className} />;
    case "tag": return <Tag className={className} />;
    case "gallery": return <FolderOpen className={className} />;
    case "face": return <Fingerprint className={className} />;
    case "segment": return <Merge className={className} />;
    default: return <Film className={className} />;
  }
}

type HydratedGroupItemData =
  | { type: "video"; video: Video }
  | { type: "image"; image: Image }
  | { type: "audio"; audio: Audio }
  | { type: "text"; text: TextDocument }
  | { type: "segment"; segment: SegmentRecord }
  | { type: "group"; group: Group };

type HydratedGroupItemState =
  | { status: "loading" }
  | { status: "error" }
  | { status: "ready"; data: HydratedGroupItemData };

function useGroupItemEntities(items: MixedGroupItem[]) {
  const queryItems = useMemo(
    () => items.filter((item): item is Extract<MixedGroupItem, { source: "item" }> => item.source === "item" && isHydratableGroupItemKind(item.kind)),
    [items]
  );
  const queries = useQueries({
    queries: queryItems.map((item) => createGroupItemEntityQuery(item)),
  });

  return useMemo(() => {
    const map = new Map<string, HydratedGroupItemState>();
    queryItems.forEach((item, index) => {
      const query = queries[index];
      if (!query) return;
      if (query.isError) {
        map.set(mixedItemKey(item), { status: "error" });
        return;
      }
      if (!query.data) {
        map.set(mixedItemKey(item), { status: "loading" });
        return;
      }
      map.set(mixedItemKey(item), { status: "ready", data: query.data });
    });
    return map;
  }, [queries, queryItems]);
}

function createGroupItemEntityQuery(item: Extract<MixedGroupItem, { source: "item" }>) {
  const normalizedKind = String(item.kind).toLowerCase();
  const hostId = normalizedKind === "video" || normalizedKind === "videorange"
    ? item.item.videoId ?? item.item.hostId
    : normalizedKind === "image"
      ? item.item.imageId ?? item.item.hostId
      : normalizedKind === "group"
        ? item.item.childGroupId ?? item.item.hostId
        : item.item.hostId;

  return {
    queryKey: ["group-item-host", item.kind, hostId],
    enabled: !!hostId,
    staleTime: 60000,
    queryFn: async (): Promise<HydratedGroupItemData> => {
      switch (normalizedKind) {
        case "video":
        case "videorange":
          return { type: "video", video: await videos.get(hostId!) };
        case "image":
          return { type: "image", image: await images.get(hostId!) };
        case "audio":
          return { type: "audio", audio: await audios.get(hostId!) };
        case "text":
          return { type: "text", text: await texts.get(hostId!) };
        case "segment": {
          const segment = await segmentLibrary.get(hostId!);
          if (!segment) throw new Error("Segment not found");
          return { type: "segment", segment };
        }
        case "group":
          return { type: "group", group: await groups.get(hostId!) };
        default:
          throw new Error(`Unsupported group item kind: ${item.kind}`);
      }
    },
  };
}

function applyVideoItemOverrides(video: Video, item: Extract<MixedGroupItem, { source: "item" }>) {
  return {
    ...video,
    title: item.item.title || video.title,
    clipStartSec: item.kind === "videoRange" ? item.item.startSec ?? video.clipStartSec : video.clipStartSec,
    clipEndSec: item.kind === "videoRange" ? item.item.endSec ?? video.clipEndSec : video.clipEndSec,
  } satisfies Video;
}

function applyNamedItemOverride<T extends { title?: string }>(entity: T, title?: string) {
  return title ? { ...entity, title } : entity;
}

function GroupItemCardShell({ children }: { children: React.ReactNode }) {
  return <div className="group relative h-full">{children}</div>;
}

function GroupItemRow({ item, onNavigate, selected, onToggleSelect, dragHandleProps, isDragging, isOver }: {
  item: MixedGroupItem;
  onNavigate: (r: any) => void;
  selected: boolean;
  onToggleSelect: () => void;
  dragHandleProps?: any;
  isDragging?: boolean;
  isOver?: boolean;
}) {
  const host = groupItemHost(item);
  return (
    <div className={`flex items-center gap-3 rounded-xl border bg-card/80 px-4 py-3 transition-colors ${selected ? "border-accent ring-1 ring-accent" : isDragging ? "border-accent opacity-40" : isOver ? "border-accent bg-accent/5" : "border-border"}`}>
      {dragHandleProps ? <span {...dragHandleProps} className="inline-flex shrink-0 cursor-grab items-center text-muted active:cursor-grabbing"><GripVertical className="h-4 w-4" /></span> : null}
      <button type="button" onClick={onToggleSelect} className={`h-5 w-5 rounded border ${selected ? "border-accent bg-accent" : "border-border"}`} aria-label={selected ? "Deselect item" : "Select item"} />
      <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded bg-surface text-muted"><GroupItemKindIcon kind={host.kind} /></div>
      <div className="min-w-0 flex-1">
        <div className="truncate text-sm font-medium text-foreground">{host.title}</div>
        <div className="mt-1 flex flex-wrap gap-2 text-xs text-secondary"><span>#{item.orderIndex + 1}</span><span>{host.subtitle}</span></div>
      </div>
      {host.route ? <button type="button" onClick={() => onNavigate(host.route)} className="inline-flex items-center gap-1.5 rounded border border-border px-2 py-1.5 text-sm text-foreground transition hover:border-accent"><ExternalLink className="h-4 w-4" />Open</button> : null}
    </div>
  );
}

function GroupItemGridCard({ item, hydrated, onNavigate, selected, onToggleSelect, selecting }: { item: MixedGroupItem; hydrated?: HydratedGroupItemState; onNavigate: (r: any) => void; selected: boolean; onToggleSelect: () => void; selecting?: boolean }) {
  const host = groupItemHost(item);
  const route = host.route ?? { page: "group", id: item.source === "subgroup" ? item.group.id : item.item.groupId };
  // Per-item engagement so each card shows its rating banner. getEngagementHost resolves the correct
  // host type/id from the raw item (null for non-rateable kinds like segments, which disables it).
  const engagementHost = getEngagementHost(item);
  const { engagement: engagementRaw } = useEntityEngagement(engagementHost?.hostType ?? "video", engagementHost?.hostId ?? 0, { enabled: !!engagementHost });
  const engagement = engagementRaw ?? undefined;

  if (item.source === "subgroup") {
    return (
      <GroupTile
        group={item.group}
        engagement={engagement}
        onClick={() => selecting ? onToggleSelect() : onNavigate(route)}
        onNavigate={onNavigate}
        selected={selected}
        onSelect={onToggleSelect}
        selecting={selecting}
      />
    );
  }

  if (hydrated?.status === "ready") {
    switch (hydrated.data.type) {
      case "video": {
        const video = applyVideoItemOverrides(hydrated.data.video, item);
        return (
          <GroupItemCardShell>
            <VideoCard
              video={video}
              engagement={engagement}
              onClick={() => selecting ? onToggleSelect() : onNavigate(route)}
              onNavigate={onNavigate}
              selected={selected}
              onSelect={onToggleSelect}
              selecting={selecting}
            />
          </GroupItemCardShell>
        );
      }
      case "image": {
        const image = applyNamedItemOverride(hydrated.data.image, item.item.title);
        return (
          <GroupItemCardShell>
            <ImageTile
              image={image}
              engagement={engagement}
              onClick={() => selecting ? onToggleSelect() : onNavigate(route)}
              onNavigate={onNavigate}
              selected={selected}
              onSelect={onToggleSelect}
              selecting={selecting}
            />
          </GroupItemCardShell>
        );
      }
      case "audio": {
        const audio = applyNamedItemOverride(hydrated.data.audio, item.item.title);
        return (
          <GroupItemCardShell>
            <AudioTile
              audio={audio}
              engagement={engagement}
              onClick={() => selecting ? onToggleSelect() : onNavigate(route)}
              onNavigate={onNavigate}
              selected={selected}
              onSelect={onToggleSelect}
              selecting={selecting}
            />
          </GroupItemCardShell>
        );
      }
      case "text": {
        const text = applyNamedItemOverride(hydrated.data.text, item.item.title);
        return (
          <GroupItemCardShell>
            <TextTile
              text={text}
              engagement={engagement}
              onClick={() => selecting ? onToggleSelect() : onNavigate(route)}
              onNavigate={onNavigate}
              selected={selected}
              onSelect={onToggleSelect}
              selecting={selecting}
            />
          </GroupItemCardShell>
        );
      }
      case "segment": {
        const segment = item.item.title ? { ...hydrated.data.segment, title: item.item.title } : hydrated.data.segment;
        return (
          <GroupItemCardShell>
            <SegmentTile
              segment={segment}
              onClick={() => selecting ? onToggleSelect() : onNavigate(route)}
              selected={selected}
              onSelect={onToggleSelect}
              selecting={selecting}
            />
          </GroupItemCardShell>
        );
      }
      case "group":
        return (
          <GroupTile
            group={hydrated.data.group}
            engagement={engagement}
            onClick={() => selecting ? onToggleSelect() : onNavigate(route)}
            onNavigate={onNavigate}
            selected={selected}
            onSelect={onToggleSelect}
            selecting={selecting}
          />
        );
    }
  }

  return (
    <EntityTileFrame
      route={route}
      label={`Open ${host.title}`}
      onClick={() => {
        if (selecting) {
          onToggleSelect();
          return;
        }
        if (host.route) onNavigate(host.route);
      }}
      selected={selected}
      onSelect={onToggleSelect}
      selecting={selecting || selected}
      mediaClassName="aspect-[4/3] bg-surface/70"
      media={<GroupItemKindIcon kind={host.kind} className="h-10 w-10 text-muted" />}
      body={(
        <>
          <p className="card-title line-clamp-2 font-semibold text-foreground group-hover:text-accent">{host.title}</p>
          <p className="truncate text-xs text-secondary">{host.subtitle}</p>
        </>
      )}
    />
  );
}

function InfoItem({ icon, label, value }: { icon?: React.ReactNode; label: string; value: React.ReactNode }) {
  return (
    <div className="flex items-center gap-2 text-sm">
      {icon ? <span className="text-muted">{icon}</span> : null}
      <div>
        <div className="text-xs text-muted">{label}</div>
        <div className="text-foreground">{value}</div>
      </div>
    </div>
  );
}

function GroupVideosPanel({ groupId, filter, setFilter, onNavigate, groupItems, groupItemsLoading, canWriteGroup }: {
  groupId: number;
  filter: FindFilter;
  setFilter: (filter: FindFilter) => void;
  onNavigate: (r: any) => void;
  groupItems?: GroupItem[];
  groupItemsLoading?: boolean;
  canWriteGroup?: boolean;
}) {
  const queryClient = useQueryClient();
  const [zoomLevel, setZoomLevel] = useState(0);
  const { displayMode, setDisplayMode, availableDisplayModes } = useRelatedEntityDisplayMode("videos");
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const [objectFilter, setObjectFilter] = useState<Record<string, unknown>>({});
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const queryPage = useCallback((nextFilter: FindFilter) => hasObjectFilter
    ? videos.findFiltered({
        findFilter: nextFilter,
        objectFilter: withRequiredMultiId(objectFilter as VideoFilterCriteria, "groupsCriterion", groupId),
      })
    : videos.find(nextFilter, { groupId: String(groupId) }), [groupId, hasObjectFilter, objectFilter]);
  const { data: groupVideos, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Video>({
    queryKey: ["group-videos", groupId, objectFilter],
    filter,
    queryFn: queryPage,
  });
  const deleteItemMutation = useMutation({
    mutationFn: (itemId: number) => groups.items.delete(groupId, itemId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["group-items", groupId] });
      queryClient.invalidateQueries({ queryKey: ["group", groupId] });
      queryClient.invalidateQueries({ queryKey: ["groups"] });
    },
  });
  const reorderItemMutation = useMutation({
    mutationFn: (ids: number[]) => groups.items.reorder(groupId, { ids }),
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: ["group-items", groupId] });
      const previousItems = queryClient.getQueryData<GroupItem[]>(["group-items", groupId]) ?? [];
      const itemsById = new Map(previousItems.map((item) => [item.id, item]));
      const nextItems = ids
        .map((itemId, index) => {
          const item = itemsById.get(itemId);
          return item ? { ...item, orderIndex: index } : undefined;
        })
        .filter((item): item is GroupItem => item != null);

      queryClient.setQueryData(["group-items", groupId], nextItems);
      return { previousItems };
    },
    onError: (_error, _ids, context) => {
      if (context?.previousItems) {
        queryClient.setQueryData(["group-items", groupId], context.previousItems);
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ["group-items", groupId] });
    },
  });
  const items = groupVideos?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar
      filter={filter}
      onFilterChange={setFilter}
      totalCount={groupVideos?.totalCount ?? 0}
      sortOptions={[
        { value: "title", label: "Title" },
        { value: "date", label: "Date" },
        { value: "rating", label: "Rating" },
        { value: "created_at", label: "Created At" },
      ]}
      zoomLevel={zoomLevel}
      onZoomChange={setZoomLevel}
      showSearch
      selectedCount={selectedIds.size}
      onSelectAll={selectAll}
      selectAllPending={selectAllPending}
      onSelectAllMatching={selectShown}
      selectAllMatchingLabel="Select shown"
      onSelectNone={selectNone}
      selectionActions={<BulkSelectionActions entityType="videos" selectedIds={selectedIds} onDone={selectNone} videoItems={items} onNavigate={onNavigate} removeFromParent={{ type: "group", id: groupId }} />}
      criteriaDefinitions={VIDEO_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      allowInfinitePageSize
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={availableDisplayModes}
    />
  );

  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void retry(); }} className="mt-3" />;

  if (groupItemsLoading) {
    return <LoadingPanel icon={<Film className="h-10 w-10" />} message="Loading group items..." />;
  }

  if (groupItems && groupItems.length > 0) {
    const orderedItems = [...groupItems].sort((left, right) => left.orderIndex - right.orderIndex || left.id - right.id);

    return (
      <div className="space-y-4">
        <div className="flex items-center justify-between rounded-xl border border-border bg-card p-4">
          <div>
            <div className="text-sm font-semibold text-foreground">Group Items</div>
            <div className="mt-1 text-sm text-secondary">This tab now reads the ordered playback items directly from the new group item API.</div>
          </div>
          <div className="text-xs text-muted">{orderedItems.length} item{orderedItems.length === 1 ? "" : "s"}</div>
        </div>

        <SortableList
          items={orderedItems}
          getKey={(item) => item.id}
          onReorder={(nextItems) => reorderItemMutation.mutate(nextItems.map((item) => item.id))}
          disabled={!canWriteGroup || reorderItemMutation.isPending}
          className="space-y-2"
          renderItem={(item, { dragHandleProps, isDragging, isOver }) => {
            const label = item.title || item.videoTitle || `Video #${item.videoId}`;
            return (
              <div className={`rounded-xl border bg-card/80 p-4 transition-colors ${isDragging ? "border-accent opacity-40" : isOver ? "border-accent bg-accent/5" : "border-border"}`}>
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="flex items-start gap-3">
                    {canWriteGroup ? (
                      <span {...dragHandleProps} className="mt-0.5 inline-flex shrink-0 cursor-grab items-center text-muted active:cursor-grabbing">
                        <GripVertical className="h-4 w-4" />
                      </span>
                    ) : null}
                    <div>
                      <div className="text-sm font-medium text-foreground">{label}</div>
                      <div className="mt-1 flex flex-wrap gap-2 text-xs text-secondary">
                        <span>#{item.orderIndex + 1}</span>
                        <span>{item.kind === "videoRange" ? formatDurationRange(item.startSec, item.endSec) : "Full video"}</span>
                        {item.sourceSpanKey ? <span>Span snapshot</span> : null}
                      </div>
                    </div>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <button
                      type="button"
                      onClick={() => onNavigate(item.startSec && item.startSec > 0
                        ? { page: "video", id: item.videoId, seekTo: item.startSec }
                        : { page: "video", id: item.videoId })}
                      className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
                    >
                      <ExternalLink className="h-4 w-4" />
                      Open video
                    </button>
                    {item.sourceSpanKey ? (
                      <button
                        type="button"
                        onClick={() => onNavigate({
                          page: "video-span",
                          id: item.videoId,
                          spanKey: item.sourceSpanKey,
                          profileId: item.sourceProfileId,
                          derivedQueryDescriptor: parseGroupItemDerivedQueryDescriptor(item.sourceQueryJson),
                        })}
                        className="rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
                      >
                        Open segment
                      </button>
                    ) : null}
                    {canWriteGroup ? (
                      <>
                        <button
                          type="button"
                          onClick={() => deleteItemMutation.mutate(item.id)}
                          disabled={deleteItemMutation.isPending}
                          className="inline-flex items-center gap-1.5 rounded px-3 py-2 text-sm text-orange-400 transition-colors hover:bg-orange-900/20 hover:text-orange-300 disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          <Unlink className="h-4 w-4" />
                          Remove from group
                        </button>
                      </>
                    ) : null}
                  </div>
                </div>
              </div>
            );
          }}
        />
      </div>
    );
  }

  if (isLoading) return <LoadingPanel icon={<Film className="h-10 w-10" />} message="Loading videos..." />;
  if (!groupVideos || items.length === 0) return <>{toolbar}<EmptyPanel icon={<Film className="h-12 w-12" />} message="No videos in this group" /></>;

  return (
    <>
      {toolbar}
      <ContextualVideoListView items={items} filter={filter} totalCount={groupVideos.totalCount} queryPage={queryPage} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} onVideoQuickView={setQuickViewId} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
      <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={groupVideos.totalCount} allowInfinitePageSize />
      {quickViewId !== null && (
        <QuickViewDialog type="video" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function parseGroupItemDerivedQueryDescriptor(sourceQueryJson?: string): SegmentDerivedQueryDescriptor | undefined {
  if (!sourceQueryJson) {
    return undefined;
  }

  try {
    const parsed = JSON.parse(sourceQueryJson) as SegmentSpanDerivedQuery;
    if (!parsed || typeof parsed !== "object" || !Array.isArray(parsed.operands) || typeof parsed.operator !== "string") {
      return undefined;
    }

    return {
      operator: parsed.operator,
      mergeGapSec: typeof parsed.mergeGapSec === "number" ? parsed.mergeGapSec : undefined,
      minDurationSec: typeof parsed.minDurationSec === "number" ? parsed.minDurationSec : undefined,
      operands: parsed.operands
        .filter((operand) => operand != null && typeof operand === "object")
        .map((operand) => ({
          sourceKey: operand.sourceKey,
          kind: operand.kind,
          tagIds: Array.isArray(operand.tagIds) ? operand.tagIds.filter((value): value is number => Number.isInteger(value) && value > 0) : undefined,
          faceIds: Array.isArray(operand.refIds) ? operand.refIds.filter((value): value is number => Number.isInteger(value) && value > 0) : undefined,
          minConfidence: typeof operand.minConfidence === "number" ? operand.minConfidence : undefined,
        })),
    };
  } catch {
    return undefined;
  }
}
function GroupSubGroupsPanel({ groupId, onNavigate, canWriteGroup }: { groupId: number; onNavigate: (r: any) => void; canWriteGroup: boolean }) {
  const queryClient = useQueryClient();
  const { data: subGroups, isLoading, error, refetch } = useQuery({
    queryKey: ["group-subgroups", groupId],
    queryFn: () => groups.subGroups(groupId),
  });
  const loadError = getLoadError(subGroups, error);
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
    onMutate: async (ids) => {
      await queryClient.cancelQueries({ queryKey: ["group-subgroups", groupId] });
      const previousGroups = queryClient.getQueryData<Group[]>(["group-subgroups", groupId]) ?? [];
      const groupsById = new Map(previousGroups.map((group) => [group.id, group]));
      const nextGroups = ids
        .map((groupIdToMove) => groupsById.get(groupIdToMove))
        .filter((group): group is Group => group != null);

      queryClient.setQueryData(["group-subgroups", groupId], nextGroups);
      return { previousGroups };
    },
    onError: (_error, _ids, context) => {
      if (context?.previousGroups) {
        queryClient.setQueryData(["group-subgroups", groupId], context.previousGroups);
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ["group-subgroups", groupId] });
    },
  });

  const existingIds = new Set(subGroups?.map((g) => g.id) ?? []);
  const availableResults = (searchResults?.items ?? []).filter((g) => g.id !== groupId && !existingIds.has(g.id));

  if (isLoading) return <LoadingPanel icon={<Layers className="h-10 w-10" />} message="Loading sub-groups..." />;
  if (loadError) return <ListLoadError error={loadError} onRetry={() => { void refetch(); }} className="mt-3" />;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold text-muted uppercase tracking-wider">Sub-Groups</h3>
        {canWriteGroup ? <button
          onClick={() => setShowAddDialog(!showAddDialog)}
          className="flex items-center gap-1 px-2 py-1 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10 border border-border"
        >
          <Plus className="w-3 h-3" />
          Add Sub-Group
        </button> : null}
      </div>

      {/* Add sub-group search */}
      {showAddDialog && canWriteGroup && (
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
        <SortableList
          items={subGroups}
          getKey={(item) => item.id}
          onReorder={(nextGroups) => reorderMut.mutate(nextGroups.map((item) => item.id))}
          disabled={!canWriteGroup || reorderMut.isPending}
          className="space-y-2"
          renderItem={(g, { dragHandleProps, index, isDragging, isOver }) => (
            <div className={`group flex items-center gap-3 rounded-xl border bg-card px-4 py-3 transition-colors ${isDragging ? "border-accent opacity-40" : isOver ? "border-accent bg-accent/5" : "border-border"}`}>
              {canWriteGroup ? (
                <span {...dragHandleProps} className="inline-flex shrink-0 cursor-grab items-center text-muted active:cursor-grabbing">
                  <GripVertical className="h-4 w-4" />
                </span>
              ) : null}
              <span className="w-6 text-center text-xs text-muted">{index + 1}</span>
              <button onClick={() => onNavigate({ page: "group", id: g.id })} className="flex-1 text-left text-sm font-medium text-foreground hover:text-accent">{g.name}</button>
              <span className="text-xs text-muted">{g.videoCount} videos</span>
              {canWriteGroup ? <button
                onClick={() => { if (confirm(`Remove "${g.name}" from sub-groups?`)) removeMut.mutate(g.id); }}
                className="opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-red-900/20 text-muted hover:text-red-400"
              >
                <X className="w-3.5 h-3.5" />
              </button> : null}
            </div>
          )}
        />
      ) : (
        <EmptyPanel icon={<Layers className="h-12 w-12" />} message="No sub-groups" />
      )}
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

function formatDurationRange(startSec?: number, endSec?: number) {
  if (startSec == null || endSec == null) {
    return "Range unavailable";
  }

  return `${formatDurationValue(startSec)} - ${formatDurationValue(endSec)}`;
}

function formatDurationValue(value: number) {
  const totalSeconds = Math.max(0, Math.floor(value));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${minutes}:${String(seconds).padStart(2, "0")}`;
}

function matchesTimestampCriterion(value: string | undefined, criterion?: TimestampCriterion) {
  if (!criterion) return true;
  const timestamp = value ? Date.parse(value) : Number.NaN;
  const expected = criterion.value ? Date.parse(criterion.value) : Number.NaN;
  const expected2 = criterion.value2 ? Date.parse(criterion.value2) : Number.NaN;
  const modifier = criterion.modifier ?? "EQUALS";

  switch (modifier) {
    case "IS_NULL": return !Number.isFinite(timestamp);
    case "NOT_NULL": return Number.isFinite(timestamp);
    case "NOT_EQUALS": return timestamp !== expected;
    case "GREATER_THAN": return Number.isFinite(timestamp) && Number.isFinite(expected) && timestamp > expected;
    case "LESS_THAN": return Number.isFinite(timestamp) && Number.isFinite(expected) && timestamp < expected;
    case "BETWEEN": return Number.isFinite(timestamp) && Number.isFinite(expected) && Number.isFinite(expected2) && timestamp >= Math.min(expected, expected2) && timestamp <= Math.max(expected, expected2);
    case "NOT_BETWEEN": return !Number.isFinite(timestamp) || !Number.isFinite(expected) || !Number.isFinite(expected2) || timestamp < Math.min(expected, expected2) || timestamp > Math.max(expected, expected2);
    default: return timestamp === expected;
  }
}
