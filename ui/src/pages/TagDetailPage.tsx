import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { audios, galleries, groups, images, performers, videos, segmentLibrary, studios, tags, texts, entityImages } from "../api/client";
import type { Audio, AudioFilterCriteria, FindFilter, Gallery, GalleryFilterCriteria, Group, GroupFilterCriteria, Image, ImageFilterCriteria, Performer, PerformerFilterCriteria, Video, VideoFilterCriteria, SegmentRecord, Studio, StudioFilterCriteria, TagDetail as TagDetailModel, TextDocument, TextFilterCriteria } from "../api/types";
import { formatDate, formatDuration, getResolutionLabel, TagBadge, CustomFieldsDisplay, FieldProvenanceHover, resolveTagProvenance } from "../components/shared";
import { Building2, FileText, Film, FolderOpen, GitMerge, Headphones, Heart, ImageIcon, Layers, Loader2, MoreVertical, Music, Pencil, Search, Tag as TagIcon, Trash2, UserRound } from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { TagEditModal } from "./TagEditModal";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { DetailMergeDialog } from "../components/DetailMergeDialog";
import { ExtensionSlot } from "../router/RouteRegistry";
import { AudioTile, VideoCard, PerformerTile, ImageTile, GalleryTile, StudioTile, GroupTile, SegmentTile, TextTile } from "../components/EntityCards";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { DetailListPagination, DetailListToolbar } from "../components/DetailListToolbar";
import { ListLoadError } from "../components/ListLoadError";
import { ListQueryState } from "../components/ListQueryState";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { useExtensionTabs } from "../components/useExtensionTabs";
import { EntityDetailTabs } from "../components/EntityDetailTabs";
import { EntityHeroLayout, HERO_ACTION_BUTTON_CLASS, HERO_PRIMARY_ACTION_BUTTON_CLASS } from "../components/EntityHeroLayout";
import { MetadataServerLinks } from "../components/MetadataServerLinks";
import { useAppConfig } from "../state/AppConfigContext";
import { InteractiveRating } from "../components/Rating";
import { CoverImageDialog } from "../components/CoverImageDialog";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { TagMetadataTaggerDialog } from "../components/MetadataTaggerDialog";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { ContextualImageListView, ContextualVideoListView } from "../components/ContextualMediaListViews";
import { VIDEO_SORT_OPTIONS } from "../components/videoSortOptions";
import { AUDIO_CRITERIA, GALLERY_CRITERIA, GROUP_CRITERIA, IMAGE_CRITERIA, PERFORMER_CRITERIA, STUDIO_CRITERIA, TEXT_CRITERIA, VIDEO_CRITERIA } from "../components/FilterDialog";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { GALLERY_SORT_OPTIONS } from "../components/gallerySortOptions";
import { PERFORMER_SORT_OPTIONS } from "../components/performerSortOptions";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useDetailListQuery } from "../hooks/useDetailListQuery";
import { useDetailListSelection } from "../hooks/useDetailListSelection";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity, filterItemsByPermission } from "../auth/visibility";
import { withRequiredMultiId } from "../utils/detailRelationFilters";
import { HierarchyContentToggle } from "../components/HierarchyContentToggle";
import { getEntityCardMinWidthPx } from "../hooks/useEntityCardSize";
import { useDetailBooleanUrlState, useDetailTabUrlState, useRelatedDetailListUrlState } from "../hooks/useDetailListUrlState";
import { createSegmentCriteria } from "./segments/segmentCriteriaDefinitions";
import { readRawSegmentListFilter } from "./segments/rawSegmentFilter";
import { IMAGE_SORT_OPTIONS } from "../components/imageSortOptions";
import { AUDIO_SORT_OPTIONS } from "../components/audioSortOptions";
import { TEXT_SORT_OPTIONS } from "../components/textSortOptions";
import { STUDIO_SORT_OPTIONS } from "../components/studioSortOptions";
import { GROUP_SORT_OPTIONS } from "../components/groupSortOptions";
import { RAW_SEGMENT_SORT_OPTIONS } from "../components/segmentSortOptions";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";

const PERFORMER_SORT = PERFORMER_SORT_OPTIONS;
const IMAGE_SORT = IMAGE_SORT_OPTIONS;
const GALLERY_SORT = GALLERY_SORT_OPTIONS;
const STUDIO_SORT = STUDIO_SORT_OPTIONS;
const GROUP_SORT = GROUP_SORT_OPTIONS;
const AUDIO_SORT = AUDIO_SORT_OPTIONS;
const TEXT_SORT = TEXT_SORT_OPTIONS;
const SEGMENT_SORT = RAW_SEGMENT_SORT_OPTIONS;

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type TabKey = "videos" | "performers" | "images" | "galleries" | "audios" | "texts" | "segments" | "studios" | "groups" | (string & {});

export function TagDetailPage({ id, onNavigate }: Props) {
  const { config } = useAppConfig();
  const { hasPermission, user } = useAuth();
  const { data: tag, isLoading, error: tagError, refetch: retryTag } = useQuery({
    queryKey: ["tag", id],
    queryFn: () => tags.get(id),
  });
  const tagLoadError = getLoadError(tag, tagError);
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [mergeOpen, setMergeOpen] = useState(false);
  const [metadataTaggerOpen, setMetadataTaggerOpen] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [coverOpen, setCoverOpen] = useState(false);
  const { activeTab, setActiveTab } = useDetailTabUrlState<TabKey>("videos");
  const [includeSubTags, setIncludeSubTags] = useDetailBooleanUrlState("includeSubTags");
  const { data: recursiveTag } = useQuery({
    queryKey: ["tag", id, "depth", -1],
    queryFn: () => tags.get(id, -1),
    enabled: includeSubTags,
  });
  const opsMenuRef = useRef<HTMLDivElement | null>(null);
  const { allTabs: tagTabs, renderExtensionTab, extensionCounts } = useExtensionTabs("tag", [
    { key: "videos", label: "Videos", count: includeSubTags ? recursiveTag?.videoCount : tag?.videoCount },
    { key: "performers", label: "Performers", count: includeSubTags ? recursiveTag?.performerCount : tag?.performerCount },
    { key: "images", label: "Images", count: includeSubTags ? recursiveTag?.imageCount : tag?.imageCount },
    { key: "galleries", label: "Galleries", count: includeSubTags ? recursiveTag?.galleryCount : tag?.galleryCount },
    { key: "audios", label: "Audios", count: includeSubTags ? recursiveTag?.audioCount : tag?.audioCount },
    { key: "texts", label: "Texts", count: includeSubTags ? recursiveTag?.textCount : tag?.textCount },
    { key: "segments", label: "Segments", count: includeSubTags ? recursiveTag?.segmentCount : tag?.segmentCount },
    { key: "studios", label: "Studios", count: includeSubTags ? recursiveTag?.studioCount : tag?.studioCount },
    { key: "groups", label: "Groups", count: includeSubTags ? recursiveTag?.groupCount : tag?.groupCount },
  ], id);
  const queryClient = useQueryClient();
  const { backLabel, goBack } = useBackNavigation({ page: "tags" }, onNavigate);
  const canWriteTag = canWriteEntity("tag", hasPermission);
  const canEngageTag = canReadEntity("tag", hasPermission) && (user?.kind === "user" || user?.kind === "system");
  const canDeleteTag = canDeleteEntity("tag", hasPermission);
  const showTagOpsMenu = canWriteTag || canDeleteTag;
  const visibleTagTabs = filterItemsByPermission(tagTabs, {
    videos: "videos.read",
    performers: "performers.read",
    images: "images.read",
    galleries: "galleries.read",
    audios: "audios.read",
    texts: "texts.read",
    segments: "videos.read",
    studios: "studios.read",
    groups: "groups.read",
  }, hasPermission);

  const { favorite: tagFavorite, setFavorite: setTagFavorite, rating: tagRating, setRating: setTagRating } = useEntityEngagement("tag", id, {
    fallbackFavorite: tag?.favorite,
  });

  useDocumentTitle(tag?.name);

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const el = (e.target as HTMLElement).tagName;
      if (el === "INPUT" || el === "TEXTAREA" || el === "SELECT") return;
      switch (e.key) {
        case "e": if (canWriteTag) setEditing((v) => !v); break;
        case "f": if (tag && canEngageTag) setTagFavorite(!tagFavorite); break;
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [canEngageTag, canWriteTag, tag, tagFavorite, setTagFavorite]);

  useEffect(() => {
    if (visibleTagTabs.length > 0 && !visibleTagTabs.some((tab) => tab.key === activeTab)) {
      setActiveTab(visibleTagTabs[0].key as TabKey);
    }
  }, [activeTab, visibleTagTabs]);

  useEffect(() => {
    if (!showOpsMenu) return;
    const handlePointerDown = (event: PointerEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(event.target as Node)) {
        setShowOpsMenu(false);
      }
    };
    document.addEventListener("pointerdown", handlePointerDown);
    return () => document.removeEventListener("pointerdown", handlePointerDown);
  }, [showOpsMenu]);

  const deleteMut = useMutation({
    mutationFn: () => tags.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["tags"] });
      goBack();
    },
  });

  const updateMut = useMutation({
    mutationFn: (data: { organized?: boolean }) => tags.update(id, data),
    onMutate: async (data) => {
      if (data.organized === undefined) return undefined;
      await queryClient.cancelQueries({ queryKey: ["tag", id] });
      const previous = queryClient.getQueryData<TagDetailModel>(["tag", id]);
      queryClient.setQueryData<TagDetailModel>(["tag", id], (current) => current ? { ...current, organized: data.organized! } : current);
      return { previous };
    },
    onError: (_error, _data, context) => {
      if (context?.previous) queryClient.setQueryData(["tag", id], context.previous);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["tag", id] });
      queryClient.invalidateQueries({ queryKey: ["tags"] });
    },
  });

  if (isLoading) {
    return (
      <div className="flex h-64 items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
      </div>
    );
  }

  if (isApiNotFoundError(tagLoadError)) {
    return <div className="py-16 text-center text-secondary">Tag not found</div>;
  }

  if (tagLoadError) {
    return <ListLoadError error={tagLoadError} onRetry={() => { void retryTag(); }} title="Could not load tag" className="mx-0 mt-0" />;
  }

  if (!tag) {
    return <div className="py-16 text-center text-secondary">Tag not found</div>;
  }

  const tagImageUrl = tag.imagePath || entityImages.tagImageUrl(tag.id, tag.updatedAt);
  const handleCoverChanged = () => {
    queryClient.invalidateQueries({ queryKey: ["tag", tag.id] });
    queryClient.invalidateQueries({ queryKey: ["tags"] });
  };

  return (
    <>
      <EntityHeroLayout
        entityType="tag"
        entityId={tag.id}
        backLabel={backLabel}
        onGoBack={goBack}
        imageUrl={tagImageUrl}
        imageAlt={tag.name}
        imageFit="contain"
        imageClassName="h-full w-full object-contain p-3"
        onImageClick={canWriteTag ? () => setCoverOpen(true) : undefined}
        imageFallback={<TagIcon className="h-14 w-14 text-accent" />}
        title={<FieldProvenanceHover fieldProvenance={tag.fieldProvenance} fieldKey="name">{tag.name}</FieldProvenanceHover>}
        sortName={tag.sortName && tag.sortName !== tag.name ? <FieldProvenanceHover fieldProvenance={tag.fieldProvenance} fieldKey="sortName">{tag.sortName}</FieldProvenanceHover> : undefined}
        aliases={tag.aliases.length > 0 ? <FieldProvenanceHover fieldProvenance={tag.fieldProvenance} fieldKey="aliases">{tag.aliases.join(", ")}</FieldProvenanceHover> : undefined}
        description={tag.description ? <FieldProvenanceHover fieldProvenance={tag.fieldProvenance} fieldKey="description" block>{tag.description}</FieldProvenanceHover> : undefined}
        favorite={tagFavorite}
        onFavoriteToggle={canEngageTag ? () => setTagFavorite(!tagFavorite) : undefined}
        organized={tag.organized}
        organizedPending={updateMut.isPending}
        onOrganizedToggle={canWriteTag ? (organized) => updateMut.mutate({ organized }) : undefined}
        counts={[
          { key: "videos", label: "Videos", value: tag.videoCount, icon: <Film className="h-4 w-4" /> },
          { key: "performers", label: "Performers", value: tag.performerCount, icon: <UserRound className="h-4 w-4" /> },
          { key: "images", label: "Images", value: tag.imageCount, icon: <ImageIcon className="h-4 w-4" /> },
          { key: "galleries", label: "Galleries", value: tag.galleryCount, icon: <FolderOpen className="h-4 w-4" /> },
          { key: "audios", label: "Audios", value: tag.audioCount, icon: <Headphones className="h-4 w-4" /> },
          { key: "texts", label: "Texts", value: tag.textCount, icon: <FileText className="h-4 w-4" /> },
          { key: "segments", label: "Segments", value: tag.segmentCount, icon: <Layers className="h-4 w-4" /> },
          { key: "studios", label: "Studios", value: tag.studioCount, icon: <Building2 className="h-4 w-4" /> },
          { key: "groups", label: "Groups", value: tag.groupCount, icon: <Layers className="h-4 w-4" /> },
          ...extensionCounts.map((ec) => ({
            key: ec.key,
            label: ec.label,
            value: ec.count,
            icon: ec.icon === "music" ? <Music className="h-4 w-4" /> : undefined,
          })),
        ]}
        metaRow={(
          <>
            <span title={`Created ${formatDate(tag.createdAt)}`}>Updated {formatDate(tag.updatedAt)}</span>
          </>
        )}
        heroContent={(
          <>
            <div className="mb-3 shrink-0">
              <InteractiveRating value={tagRating} onChange={(value) => setTagRating(value)} readOnly={!canEngageTag} />
            </div>
            <MetadataServerLinks className="mb-3 flex flex-wrap gap-2" remoteIds={tag.remoteIds} entityType="tags" metadataServers={config?.scraping?.metadataServers} />
            <CustomFieldsDisplay customFields={tag.customFields} entityType="tag" />
          </>
        )}
        actions={(
          <>
            <ExtensionSlot slot="tag-detail-actions" context={{ tag, onNavigate }} />
            {canWriteTag ? <button onClick={() => setEditing(true)} className={HERO_PRIMARY_ACTION_BUTTON_CLASS}><Pencil className="h-3.5 w-3.5" /> Edit</button> : null}
            {showTagOpsMenu ? (
              <div className="relative" ref={opsMenuRef}>
                <button onClick={() => setShowOpsMenu(!showOpsMenu)} className={`${HERO_ACTION_BUTTON_CLASS} text-secondary`} title="Actions">
                  <MoreVertical className="h-4 w-4" />
                </button>
                <FloatingActionMenu open={showOpsMenu} anchorRef={opsMenuRef} onClose={() => setShowOpsMenu(false)} className="w-44 py-1">
                    {canWriteTag ? (
                      <button type="button" onClick={() => { setShowOpsMenu(false); setMetadataTaggerOpen(true); }} className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-secondary hover:bg-card hover:text-foreground">
                        <Search className="h-3.5 w-3.5" /> Metadata...
                      </button>
                    ) : null}
                    {canWriteTag ? (
                      <button type="button" onClick={() => { setShowOpsMenu(false); setMergeOpen(true); }} className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-secondary hover:bg-card hover:text-foreground">
                        <GitMerge className="h-3.5 w-3.5" /> Merge...
                      </button>
                    ) : null}
                    {canDeleteTag ? (
                      <button type="button" onClick={() => { setShowOpsMenu(false); setConfirmDelete(true); }} className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-red-300 hover:bg-red-950/30">
                        <Trash2 className="h-3.5 w-3.5" /> Delete
                      </button>
                    ) : null}
                </FloatingActionMenu>
              </div>
            ) : null}
          </>
        )}
      >
        <ExtensionSlot slot="tag-detail-sidebar-bottom" context={{ tag, onNavigate }} />

        <TagHierarchyLinks tag={tag} onNavigate={onNavigate} className="mx-auto mb-4 max-w-7xl" />
        <EntityDetailTabs tabs={visibleTagTabs} activeTab={activeTab} onTabChange={(key) => setActiveTab(key as TabKey)} className="mx-auto max-w-7xl" />
        <div className="mx-auto mt-4 max-w-7xl px-4">
          <HierarchyContentToggle checked={includeSubTags} label="Include sub-tag content" onChange={setIncludeSubTags} />
        </div>

        <div className="py-6">
          {activeTab === "videos" && (
            <TagVideosPanel tagId={id} includeSubTags={includeSubTags} onNavigate={onNavigate} />
          )}
          {activeTab === "performers" && (
            <TagPerformersPanel tagId={id} includeSubTags={includeSubTags} onNavigate={onNavigate} />
          )}
          {activeTab === "images" && (
            <TagImagesPanel tagId={id} includeSubTags={includeSubTags} onNavigate={onNavigate} />
          )}
          {activeTab === "galleries" && (
            <TagGalleriesPanel tagId={id} includeSubTags={includeSubTags} onNavigate={onNavigate} />
          )}
          {activeTab === "audios" && (
            <TagAudiosPanel tagId={id} includeSubTags={includeSubTags} onNavigate={onNavigate} />
          )}
          {activeTab === "texts" && (
            <TagTextsPanel tagId={id} includeSubTags={includeSubTags} onNavigate={onNavigate} />
          )}
          {activeTab === "segments" && (
            <TagSegmentsPanel tagId={id} includeSubTags={includeSubTags} onNavigate={onNavigate} />
          )}
          {activeTab === "studios" && (
            <TagStudiosPanel tagId={id} includeSubTags={includeSubTags} onNavigate={onNavigate} />
          )}
          {activeTab === "groups" && (
            <TagGroupsPanel tagId={id} includeSubTags={includeSubTags} onNavigate={onNavigate} />
          )}
          {renderExtensionTab(activeTab, id, onNavigate)}
        </div>

        <ExtensionSlot slot="tag-detail-bottom" context={{ tag, onNavigate }} />
      </EntityHeroLayout>

      <CoverImageDialog
        open={coverOpen}
        title="Set Tag Cover"
        entityType="tag"
        entityId={tag.id}
        currentImageUrl={tagImageUrl}
        onUpload={(file) => entityImages.uploadTagImage(tag.id, file)}
        onDelete={() => entityImages.deleteTagImage(tag.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={handleCoverChanged}
        aspectRatio="16/9"
        objectFit="contain"
      />

      <TagEditModal tag={tag} open={editing} onClose={() => setEditing(false)} />
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Tag"
        message={`Delete "${tag.name}"? This cannot be undone.`}
        onConfirm={() => deleteMut.mutate()}
        onCancel={() => setConfirmDelete(false)}
      />
      <DetailMergeDialog
        open={mergeOpen}
        onClose={() => setMergeOpen(false)}
        entityType="tag"
        targetItem={{ id: tag.id, name: tag.name, imagePath: tagImageUrl, subtitle: tag.sortName && tag.sortName !== tag.name ? tag.sortName : undefined }}
        searchItems={async (term) => {
          const response = await tags.find({ page: 1, perPage: 20, sort: "name", direction: "asc", q: term || undefined });
          return response.items.map((item) => ({
            id: item.id,
            name: item.name,
            imagePath: item.imagePath,
          }));
        }}
        onMerge={(targetId, sourceIds) => tags.merge(targetId, sourceIds)}
        invalidateQueryKeys={[["tag", id], ["tags"]]}
      />
      <TagMetadataTaggerDialog open={metadataTaggerOpen} onClose={() => setMetadataTaggerOpen(false)} tag={tag} />

    </>
  );
}

function TagHierarchyLinks({
  tag,
  onNavigate,
  className = "",
}: {
  tag: TagDetailModel;
  onNavigate: (r: any) => void;
  className?: string;
}) {
  if (tag.parents.length === 0 && tag.children.length === 0) {
    return null;
  }

  return (
    <section className={["rounded-xl border border-border bg-card/70 px-4 py-3", className].filter(Boolean).join(" ")}>
      <div className="flex flex-wrap gap-x-6 gap-y-3 text-sm text-secondary">
        {tag.parents.length > 0 ? (
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            <span className="inline-flex items-center gap-1 text-[11px] font-semibold uppercase tracking-wide text-muted">
              <TagIcon className="h-3 w-3" /> Parents
            </span>
            {tag.parents.map((parent) => (
              <TagBadge key={parent.id} name={parent.name} tag={parent} provenance={resolveTagProvenance(parent, tag.fieldProvenance, "parents")} onClick={() => onNavigate({ page: "tag", id: parent.id })} />
            ))}
          </div>
        ) : null}
        {tag.children.length > 0 ? (
          <div className="flex min-w-0 flex-wrap items-center gap-1.5">
            <span className="inline-flex items-center gap-1 text-[11px] font-semibold uppercase tracking-wide text-muted">
              <TagIcon className="h-3 w-3" /> Sub Tags
            </span>
            {tag.children.map((child) => (
              <TagBadge key={child.id} name={child.name} tag={child} provenance={resolveTagProvenance(child, tag.fieldProvenance, "children")} onClick={() => onNavigate({ page: "tag", id: child.id })} />
            ))}
          </div>
        ) : null}
      </div>
    </section>
  );
}

function TagVideosPanel({ tagId, includeSubTags, onNavigate }: {
  tagId: number;
  includeSubTags: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "videos", resetKey: "tag-videos", entityType: "videos", builtInFilter: { page: 1, perPage: 24, direction: "desc" }, defaultFilterKey: "videos" });
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const queryPage = useCallback((nextFilter: FindFilter) => hasObjectFilter || includeSubTags
    ? videos.findFiltered({
        findFilter: nextFilter,
        objectFilter: withRequiredMultiId(objectFilter as VideoFilterCriteria, "tagsCriterion", tagId, includeSubTags ? -1 : undefined),
      })
    : videos.find(nextFilter, { tagIds: String(tagId) }), [hasObjectFilter, includeSubTags, objectFilter, tagId]);
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Video>({
    queryKey: ["tag-videos", tagId, objectFilter, includeSubTags],
    filter,
    queryFn: queryPage,
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={VIDEO_SORT_OPTIONS} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="videos" selectedIds={selectedIds} onDone={selectNone} videoItems={items} onNavigate={onNavigate} removeFromParent={{ type: "tag", id: tagId }} />} criteriaDefinitions={VIDEO_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="videos" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  return (
    <ListQueryState header={toolbar} isLoading={isLoading} loadError={loadError} isEmpty={items.length === 0} onRetry={() => { void retry(); }} loading={<LoadingPanel icon={<Film className="h-10 w-10" />} message="Loading videos..." />} empty={<EmptyPanel icon={<Film className="h-12 w-12" />} message="No videos with this tag" />}>
      <>
        <ContextualVideoListView items={items} filter={filter} totalCount={data.totalCount} queryPage={queryPage} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} onVideoQuickView={setQuickViewId} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
        <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} allowInfinitePageSize />
        {quickViewId !== null && (
          <QuickViewDialog type="video" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
        )}
      </>
    </ListQueryState>
  );
}

function TagPerformersPanel({ tagId, includeSubTags, onNavigate }: {
  tagId: number;
  includeSubTags: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "performers", resetKey: "tag-performers", entityType: "performers", builtInFilter: { page: 1, perPage: 18, direction: "asc" }, defaultFilterKey: "performers" });
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Performer>({
    queryKey: ["tag-performers", tagId, objectFilter, includeSubTags],
    filter,
    queryFn: (nextFilter) => hasObjectFilter || includeSubTags
      ? performers.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredMultiId(objectFilter as PerformerFilterCriteria, "tagsCriterion", tagId, includeSubTags ? -1 : undefined),
        })
      : performers.find(nextFilter, { tagIds: String(tagId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={PERFORMER_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="performers" selectedIds={selectedIds} onDone={selectNone} removeFromParent={{ type: "tag", id: tagId }} />} criteriaDefinitions={PERFORMER_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="performers" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  return (
    <ListQueryState header={toolbar} isLoading={isLoading} loadError={loadError} isEmpty={items.length === 0} onRetry={() => { void retry(); }} loading={<LoadingPanel icon={<UserRound className="h-10 w-10" />} message="Loading performers..." />} empty={<EmptyPanel icon={<UserRound className="h-12 w-12" />} message="No performers with this tag" />}>
      <><RelatedEntityListView entityType="performers" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} /><DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} allowInfinitePageSize /></>
    </ListQueryState>
  );
}

function TagImagesPanel({ tagId, includeSubTags, onNavigate }: {
  tagId: number;
  includeSubTags: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "images", resetKey: "tag-images", entityType: "images", builtInFilter: { page: 1, perPage: 30, direction: "desc" }, defaultFilterKey: "images" });
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const queryPage = useCallback((nextFilter: FindFilter) => hasObjectFilter || includeSubTags
    ? images.findFiltered({
        findFilter: nextFilter,
        objectFilter: withRequiredMultiId(objectFilter as ImageFilterCriteria, "tagsCriterion", tagId, includeSubTags ? -1 : undefined),
      })
    : images.find(nextFilter, { tagIds: String(tagId) }), [hasObjectFilter, includeSubTags, objectFilter, tagId]);
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Image>({
    queryKey: ["tag-images", tagId, objectFilter, includeSubTags],
    filter,
    queryFn: queryPage,
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={IMAGE_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="images" selectedIds={selectedIds} onDone={selectNone} downloadItems={items} removeFromParent={{ type: "tag", id: tagId }} />} criteriaDefinitions={IMAGE_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="images" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  return (
    <ListQueryState header={toolbar} isLoading={isLoading} loadError={loadError} isEmpty={items.length === 0} onRetry={() => { void retry(); }} loading={<LoadingPanel icon={<ImageIcon className="h-10 w-10" />} message="Loading images..." />} empty={<EmptyPanel icon={<ImageIcon className="h-12 w-12" />} message="No images with this tag" />}>
      <>
        <ContextualImageListView items={items} filter={filter} totalCount={data.totalCount} queryPage={queryPage} interactionSource="tagDetailPage" interactionMeta={{ tagId }} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} onImageQuickView={setQuickViewId} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} />
        <DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} allowInfinitePageSize />
        {quickViewId !== null && <QuickViewDialog type="image" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />}
      </>
    </ListQueryState>
  );
}

function TagGalleriesPanel({ tagId, includeSubTags, onNavigate }: {
  tagId: number;
  includeSubTags: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "galleries", resetKey: "tag-galleries", entityType: "galleries", builtInFilter: { page: 1, perPage: 18, direction: "desc" }, defaultFilterKey: "galleries" });
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Gallery>({
    queryKey: ["tag-galleries", tagId, objectFilter, includeSubTags],
    filter,
    queryFn: (nextFilter) => hasObjectFilter || includeSubTags
      ? galleries.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredMultiId(objectFilter as GalleryFilterCriteria, "tagsCriterion", tagId, includeSubTags ? -1 : undefined),
        })
      : galleries.find(nextFilter, { tagIds: String(tagId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={GALLERY_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="galleries" selectedIds={selectedIds} onDone={selectNone} downloadItems={items} removeFromParent={{ type: "tag", id: tagId }} />} criteriaDefinitions={GALLERY_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="galleries" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  return (
    <ListQueryState header={toolbar} isLoading={isLoading} loadError={loadError} isEmpty={items.length === 0} onRetry={() => { void retry(); }} loading={<LoadingPanel icon={<FolderOpen className="h-10 w-10" />} message="Loading galleries..." />} empty={<EmptyPanel icon={<FolderOpen className="h-12 w-12" />} message="No galleries with this tag" />}>
      <><RelatedEntityListView entityType="galleries" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} /><DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} allowInfinitePageSize /></>
    </ListQueryState>
  );
}

function TagAudiosPanel({ tagId, includeSubTags, onNavigate }: {
  tagId: number;
  includeSubTags: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "audios", resetKey: "tag-audios", entityType: "audios", builtInFilter: { page: 1, perPage: 18, direction: "desc" }, defaultFilterKey: "audios" });
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Audio>({
    queryKey: ["tag-audios", tagId, objectFilter, includeSubTags],
    filter,
    queryFn: (nextFilter) => audios.findFiltered({
      findFilter: nextFilter,
      objectFilter: withRequiredMultiId(objectFilter as AudioFilterCriteria, "tagsCriterion", tagId, includeSubTags ? -1 : undefined),
    }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={AUDIO_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="audios" selectedIds={selectedIds} onDone={selectNone} audioItems={items} downloadItems={items} onNavigate={onNavigate} removeFromParent={{ type: "tag", id: tagId }} />} criteriaDefinitions={AUDIO_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="audios" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  return (
    <ListQueryState header={toolbar} isLoading={isLoading} loadError={loadError} isEmpty={items.length === 0} onRetry={() => { void retry(); }} loading={<LoadingPanel icon={<Headphones className="h-10 w-10" />} message="Loading audios..." />} empty={<EmptyPanel icon={<Headphones className="h-12 w-12" />} message="No audios with this tag" />}>
      <><RelatedEntityListView entityType="audios" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} /><DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} allowInfinitePageSize /></>
    </ListQueryState>
  );
}

function TagTextsPanel({ tagId, includeSubTags, onNavigate }: {
  tagId: number;
  includeSubTags: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "texts", resetKey: "tag-texts", entityType: "texts", builtInFilter: { page: 1, perPage: 18, direction: "desc" }, defaultFilterKey: "texts" });
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<TextDocument>({
    queryKey: ["tag-texts", tagId, objectFilter, includeSubTags],
    filter,
    queryFn: (nextFilter) => texts.findFiltered({
      findFilter: nextFilter,
      objectFilter: withRequiredMultiId(objectFilter as TextFilterCriteria, "tagsCriterion", tagId, includeSubTags ? -1 : undefined),
    }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={TEXT_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="texts" selectedIds={selectedIds} onDone={selectNone} textItems={items} downloadItems={items} removeFromParent={{ type: "tag", id: tagId }} />} criteriaDefinitions={TEXT_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="texts" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  return (
    <ListQueryState header={toolbar} isLoading={isLoading} loadError={loadError} isEmpty={items.length === 0} onRetry={() => { void retry(); }} loading={<LoadingPanel icon={<FileText className="h-10 w-10" />} message="Loading texts..." />} empty={<EmptyPanel icon={<FileText className="h-12 w-12" />} message="No texts with this tag" />}>
      <><RelatedEntityListView entityType="texts" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} /><DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} allowInfinitePageSize /></>
    </ListQueryState>
  );
}

function TagSegmentsPanel({ tagId, includeSubTags, onNavigate }: { tagId: number; includeSubTags: boolean; onNavigate: (r: any) => void }) {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canEditSegments = hasPermission("segments.write");
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "segments", resetKey: "tag-segments", entityType: "segments", builtInFilter: { page: 1, perPage: 24, direction: "desc", sort: "updated_at" }, defaultFilterKey: "rawsegments" });
  const rawFilter = useMemo(() => readRawSegmentListFilter(objectFilter), [objectFilter]);
  const sourceOptionsQuery = useQuery({
    queryKey: ["segments-page", "filter", "source-keys"],
    queryFn: () => segmentLibrary.distinctSourceKeys(),
    staleTime: 60_000,
  });
  const kindOptionsQuery = useQuery({
    queryKey: ["segments-page", "filter", "kinds"],
    queryFn: () => segmentLibrary.distinctKinds(),
    staleTime: 60_000,
  });
  const segmentCriteria = useMemo(() => createSegmentCriteria({
    sourceOptions: (sourceOptionsQuery.data ?? []).map((option) => ({ value: option.value, label: `${option.value} (${option.count})` })),
    kindOptions: (kindOptionsQuery.data ?? []).map((option) => ({ value: option.value, label: `${option.value} (${option.count})` })),
  }), [kindOptionsQuery.data, sourceOptionsQuery.data]);
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<SegmentRecord>({
    queryKey: ["tag-segments", tagId, objectFilter, includeSubTags],
    filter,
    queryFn: (nextFilter) => segmentLibrary.list({
      q: nextFilter.q,
      tagId,
      tagDepth: includeSubTags ? -1 : undefined,
      videoIds: rawFilter.videoIds.length > 0 ? rawFilter.videoIds.join(",") : undefined,
      excludeVideoIds: rawFilter.excludeVideoIds.length > 0 ? rawFilter.excludeVideoIds.join(",") : undefined,
      videoTitle: rawFilter.videoTitle,
      tagIds: rawFilter.tagIds.length > 0 ? rawFilter.tagIds.join(",") : undefined,
      performerIds: rawFilter.performerIds.length > 0 ? rawFilter.performerIds.join(",") : undefined,
      refIds: rawFilter.faceIds.length > 0 ? rawFilter.faceIds.join(",") : undefined,
      kind: rawFilter.kind,
      sourceKey: rawFilter.sourceKey,
      sourceCategory: rawFilter.sourceCategory,
      title: rawFilter.titleCriterion?.value,
      titleModifier: rawFilter.titleCriterion?.modifier,
      hostType: rawFilter.hostType,
      sourceRunId: rawFilter.sourceRunCriterion?.value,
      sourceRunIdModifier: rawFilter.sourceRunCriterion?.modifier,
      colorHint: rawFilter.colorHintCriterion?.value,
      colorHintModifier: rawFilter.colorHintCriterion?.modifier,
      hasImage: rawFilter.hasImage,
      hasPayload: rawFilter.hasPayload,
      startSec: rawFilter.startSecCriterion?.value,
      startSec2: rawFilter.startSecCriterion?.value2,
      startSecModifier: rawFilter.startSecCriterion?.modifier,
      endSec: rawFilter.endSecCriterion?.value,
      endSec2: rawFilter.endSecCriterion?.value2,
      endSecModifier: rawFilter.endSecCriterion?.modifier,
      createdAt: rawFilter.createdAtCriterion?.value,
      createdAt2: rawFilter.createdAtCriterion?.value2,
      createdAtModifier: rawFilter.createdAtCriterion?.modifier,
      updatedAt: rawFilter.updatedAtCriterion?.value,
      updatedAt2: rawFilter.updatedAtCriterion?.value2,
      updatedAtModifier: rawFilter.updatedAtCriterion?.modifier,
      confidence: rawFilter.confidenceCriterion?.value,
      confidence2: rawFilter.confidenceCriterion?.value2,
      confidenceModifier: rawFilter.confidenceCriterion?.modifier,
      durationSec: rawFilter.durationCriterion?.value,
      durationSec2: rawFilter.durationCriterion?.value2,
      durationModifier: rawFilter.durationCriterion?.modifier,
      minConfidence: rawFilter.minConfidence,
      minDurationSec: rawFilter.minDurationSec,
      sort: nextFilter.sort,
      direction: nextFilter.direction as "asc" | "desc" | undefined,
      seed: nextFilter.seed,
      page: nextFilter.page,
      perPage: nextFilter.perPage,
    }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const removeTagMut = useMutation({
    mutationFn: () => segmentLibrary.removeTag({ tagId, ids: [...selectedIds] }),
    onSuccess: () => {
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["tag-segments", tagId] });
      queryClient.invalidateQueries({ queryKey: ["tag", tagId] });
      queryClient.invalidateQueries({ queryKey: ["tags"] });
    },
  });
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={SEGMENT_SORT} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={canEditSegments ? (
    <button type="button" onClick={() => removeTagMut.mutate()} disabled={selectedIds.size === 0 || removeTagMut.isPending} className="flex items-center gap-1 rounded px-2 py-0.5 text-xs text-red-300 hover:bg-red-900/20 hover:text-red-200 disabled:cursor-not-allowed disabled:opacity-50">
      {removeTagMut.isPending ? <Loader2 className="h-3 w-3 animate-spin" /> : <Trash2 className="h-3 w-3" />}
      Remove from Tag
    </button>
  ) : undefined} criteriaDefinitions={segmentCriteria} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="rawsegments" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  return (
    <ListQueryState header={toolbar} isLoading={isLoading} loadError={loadError} isEmpty={items.length === 0} onRetry={() => { void retry(); }} loading={<LoadingPanel icon={<Layers className="h-10 w-10" />} message="Loading segments..." />} empty={<EmptyPanel icon={<Layers className="h-12 w-12" />} message="No segments with this tag" />}>
      <><RelatedEntityListView entityType="segments" items={items} displayMode={displayMode} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} /><DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} allowInfinitePageSize /></>
    </ListQueryState>
  );
}

function TagStudiosPanel({ tagId, includeSubTags, onNavigate }: {
  tagId: number;
  includeSubTags: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "studios", resetKey: "tag-studios", entityType: "studios", builtInFilter: { page: 1, perPage: 18, direction: "asc" }, defaultFilterKey: "studios" });
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Studio>({
    queryKey: ["tag-studios", tagId, objectFilter, includeSubTags],
    filter,
    queryFn: (nextFilter) => hasObjectFilter || includeSubTags
      ? studios.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredMultiId(objectFilter as StudioFilterCriteria, "tagsCriterion", tagId, includeSubTags ? -1 : undefined),
        })
      : studios.find(nextFilter, { tagIds: String(tagId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={STUDIO_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="studios" selectedIds={selectedIds} onDone={selectNone} removeFromParent={{ type: "tag", id: tagId }} />} criteriaDefinitions={STUDIO_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="studios" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  return (
    <ListQueryState header={toolbar} isLoading={isLoading} loadError={loadError} isEmpty={items.length === 0} onRetry={() => { void retry(); }} loading={<LoadingPanel icon={<Building2 className="h-10 w-10" />} message="Loading studios..." />} empty={<EmptyPanel icon={<Building2 className="h-12 w-12" />} message="No studios with this tag" />}>
      <><RelatedEntityListView entityType="studios" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} /><DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} allowInfinitePageSize /></>
    </ListQueryState>
  );
}

function TagGroupsPanel({ tagId, includeSubTags, onNavigate }: {
  tagId: number;
  includeSubTags: boolean;
  onNavigate: (r: any) => void;
}) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } = useRelatedDetailListUrlState({ stateKey: "groups", resetKey: "tag-groups", entityType: "groups", builtInFilter: { page: 1, perPage: 18, direction: "asc" }, defaultFilterKey: "groups" });
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const { data, isLoading, loadError, retry, infinitePageSize, infiniteQuery, infiniteFilterKey, fetchAllIds, loadMore } = useDetailListQuery<Group>({
    queryKey: ["tag-groups", tagId, objectFilter, includeSubTags],
    filter,
    queryFn: (nextFilter) => hasObjectFilter || includeSubTags
      ? groups.findFiltered({
          findFilter: nextFilter,
          objectFilter: withRequiredMultiId(objectFilter as GroupFilterCriteria, "tagsCriterion", tagId, includeSubTags ? -1 : undefined),
        })
      : groups.find(nextFilter, { tagIds: String(tagId) }),
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({ items, infinitePageSize, infiniteFilterKey, fetchAllIds, resetKeyParts: [objectFilter] });
  const selecting = selectedIds.size > 0;
  const toolbar = <DetailListToolbar filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} sortOptions={GROUP_SORT} zoomLevel={zoomLevel} onZoomChange={setZoomLevel} showSearch selectedCount={selectedIds.size} onSelectAll={selectAll} selectAllPending={selectAllPending} onSelectAllMatching={selectShown} selectAllMatchingLabel="Select shown" onSelectNone={selectNone} selectionActions={<BulkSelectionActions entityType="groups" selectedIds={selectedIds} onDone={selectNone} removeFromParent={{ type: "tag", id: tagId }} />} criteriaDefinitions={GROUP_CRITERIA} objectFilter={objectFilter} onObjectFilterChange={setObjectFilter} filterMode="groups" defaultFilterResolved allowInfinitePageSize displayMode={displayMode} onDisplayModeChange={setDisplayMode} availableDisplayModes={availableDisplayModes} />;

  return (
    <ListQueryState header={toolbar} isLoading={isLoading} loadError={loadError} isEmpty={items.length === 0} onRetry={() => { void retry(); }} loading={<LoadingPanel icon={<Layers className="h-10 w-10" />} message="Loading groups..." />} empty={<EmptyPanel icon={<Layers className="h-12 w-12" />} message="No groups with this tag" />}>
      <><RelatedEntityListView entityType="groups" items={items} displayMode={displayMode} zoomLevel={zoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={toggle} onNavigate={onNavigate} infinitePageSize={infinitePageSize} hasNextPage={infiniteQuery.hasNextPage} isFetchingNextPage={infiniteQuery.isFetchingNextPage} loadMore={loadMore} /><DetailListPagination filter={filter} onFilterChange={setFilter} totalCount={data?.totalCount ?? 0} allowInfinitePageSize /></>
    </ListQueryState>
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
    <div className="flex flex-col items-center justify-center rounded-xl border border-dashed border-border bg-card/40 py-12 text-muted">
      <div className="mb-3 opacity-60">{icon}</div>
      <p>{message}</p>
    </div>
  );
}
