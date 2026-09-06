import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { galleries, images, videos, fileOps } from "../api/client";
import type { FindFilter, Gallery, Image, ImageFilterCriteria, Video, VideoFilterCriteria } from "../api/types";
import {
  formatDate,
  formatDuration,
  formatFileSize,
  getResolutionLabel,
  TagBadge,
  CustomFieldsDisplay,
  FieldProvenanceHover,
  resolveTagProvenance,
} from "../components/shared";
import {
  Film,
  FolderOpen,
  HardDrive,
  ImageIcon,
  Link as LinkIcon,
  Pencil,
  Plus,
  Trash2,
  Loader2,
  MoreVertical,
  RefreshCw,
  Star,
  ThumbsUp,
} from "lucide-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { GalleryEditModal } from "./GalleryEditModal";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { NarrativeText } from "../components/NarrativeText";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { ExtensionSlot } from "../router/RouteRegistry";
import { Lightbox, type LightboxImage } from "../components/Lightbox";
import { InteractiveRating } from "../components/Rating";
import { DetailListPagination, DetailListToolbar } from "../components/DetailListToolbar";
import { ListLoadError } from "../components/ListLoadError";
import { IMAGE_CRITERIA, VIDEO_CRITERIA } from "../components/filterCriteriaCatalogs";
import { PerformerTile } from "../components/EntityCards";
import {
  EntityHeroLayout,
  HERO_PRIMARY_ACTION_BUTTON_CLASS,
  HERO_ACTION_BUTTON_CLASS,
} from "../components/EntityHeroLayout";
import { CoverImageDialog } from "../components/CoverImageDialog";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { EntityDetailTabs } from "../components/EntityDetailTabs";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { BulkSelectionActions } from "../components/BulkSelectionActions";
import { useExtensionTabs } from "../components/useExtensionTabs";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import { getGalleryDisplayTitle } from "../utils/galleryDisplay";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useKeySequence } from "../hooks/useKeySequence";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity, filterItemsByPermission } from "../auth/visibility";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useDetailListQuery } from "../hooks/useDetailListQuery";
import { useDetailListSelection } from "../hooks/useDetailListSelection";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { withRequiredMultiId } from "../utils/detailRelationFilters";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { ContextualVideoListView } from "../components/ContextualMediaListViews";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";
import { IMAGE_SORT_OPTIONS } from "../components/imageSortOptions";
import { VIDEO_SORT_OPTIONS } from "../components/videoSortOptions";
import { useDetailTabUrlState, useRelatedDetailListUrlState } from "../hooks/useDetailListUrlState";
import { usePaginatedImageLightbox } from "../hooks/usePaginatedImageLightbox";
import { getLoadError, isApiNotFoundError } from "../utils/queryLoadState";
import { useAppConfig } from "../state/AppConfigContext";
import { getFirstDetailTabByMenuItems, orderDetailTabsByMenuItems } from "../utils/detailTabOrder";

interface Props {
  id: number;
  onNavigate: (r: any) => void;
}

type TabKey = "images" | "videos" | "fileinfo" | (string & {});

// Gallery contents are bounded collections (typically no more than hundreds of items), so users can
// safely default them to Infinite without also making the global Images/Videos pages attempt to load
// potentially millions of records. Filters created with "Save current filter" are available in both
// gallery and global lists, while "Set current as default" is stored separately for gallery contents.
const GALLERY_IMAGES_DEFAULT_FILTER_KEY = "gallery-images";
const GALLERY_VIDEOS_DEFAULT_FILTER_KEY = "gallery-videos";

export function GalleryDetailPage({ id, onNavigate }: Props) {
  const { config } = useAppConfig();
  const { hasPermission, user } = useAuth();
  const { activeTab, setActiveTab } = useDetailTabUrlState<TabKey>(
    getFirstDetailTabByMenuItems(["images", "videos", "fileinfo"], config?.interface?.menuItems) ?? "images",
  );
  const {
    filter: imageFilter,
    setFilter: setImageFilter,
    objectFilter: imageObjectFilter,
    setObjectFilter: setImageObjectFilter,
    displayMode: imageDisplayMode,
    setDisplayMode: setImageDisplayMode,
    availableDisplayModes: imageDisplayModes,
  } = useRelatedDetailListUrlState({
    stateKey: "images",
    resetKey: "gallery-images",
    entityType: "images",
    builtInFilter: { page: 1, perPage: 60, direction: "desc" },
    defaultFilterKey: GALLERY_IMAGES_DEFAULT_FILTER_KEY,
    enabled: activeTab === "images",
  });
  const hasImageObjectFilter = Object.keys(imageObjectFilter).length > 0;
  const {
    data: gallery,
    isLoading,
    error: galleryError,
    refetch: retryGallery,
  } = useQuery({
    queryKey: ["gallery", id],
    queryFn: () => galleries.get(id),
  });
  const { data: galleryLikeCount = 0 } = useQuery({
    queryKey: ["gallery-like-count", id],
    queryFn: () => galleries.getLikeCount(id),
    enabled: !!gallery,
  });
  const galleryLoadError = getLoadError(gallery, galleryError);
  const queryGalleryImages = useCallback(
    (nextFilter: FindFilter) =>
      hasImageObjectFilter
        ? images.findFiltered({
            findFilter: nextFilter,
            objectFilter: withRequiredMultiId(imageObjectFilter as ImageFilterCriteria, "galleriesCriterion", id),
          })
        : images.find(nextFilter, { galleryId: id }),
    [hasImageObjectFilter, id, imageObjectFilter],
  );
  const {
    data: galleryImages,
    loadError: galleryImagesLoadError,
    retry: retryGalleryImages,
    infinitePageSize: imageInfinitePageSize,
    infiniteQuery: imageInfiniteQuery,
    infiniteFilterKey: imageInfiniteFilterKey,
    fetchAllIds: fetchAllImageIds,
    loadMore: loadMoreImages,
  } = useDetailListQuery<Image>({
    queryKey: ["gallery-images", id, imageObjectFilter],
    filter: imageFilter,
    queryFn: queryGalleryImages,
    enabled: !!gallery,
  });
  const effectiveImageCount = galleryImages?.totalCount ?? gallery?.imageCount ?? 0;
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const { allTabs: galleryTabs, renderExtensionTab } = useExtensionTabs(
    "gallery",
    orderDetailTabsByMenuItems(
      [
        { key: "images", label: "Images", count: effectiveImageCount },
        { key: "videos", label: "Videos", count: gallery?.videoCount ?? 0 },
        { key: "fileinfo", label: "File Info" },
      ],
      config?.interface?.menuItems,
    ),
  );
  const [imageZoom, setImageZoom] = useState(0);
  const [showAddImages, setShowAddImages] = useState(false);
  const [showOpsMenu, setShowOpsMenu] = useState(false);
  const [coverOpen, setCoverOpen] = useState(false);
  const opsMenuRef = useRef<HTMLDivElement>(null);
  const queryClient = useQueryClient();
  const { backLabel, goBack } = useBackNavigation({ page: "galleries" }, onNavigate);
  const canWriteGallery = canWriteEntity("gallery", hasPermission);
  const canEngageGallery =
    canReadEntity("gallery", hasPermission) && (user?.kind === "user" || user?.kind === "system");
  const canDeleteGallery = canDeleteEntity("gallery", hasPermission);
  const canReadGalleryImages = canReadEntity("image", hasPermission);
  const canWriteImages = canWriteEntity("image", hasPermission);
  const canEngageImages = canReadGalleryImages && (user?.kind === "user" || user?.kind === "system");
  const canReadPerformers = canReadEntity("performer", hasPermission);
  const canReadStudios = canReadEntity("studio", hasPermission);
  const canReadTags = canReadEntity("tag", hasPermission);
  const canLibraryScan = hasPermission("library.scan");
  const {
    favorite: galleryFavorite,
    rating: galleryRating,
    setFavorite: setGalleryFavorite,
    setRating: setGalleryRating,
    favoritePending: galleryFavoritePending,
  } = useEntityEngagement("gallery", id, {
    enabled: !!gallery,
    fallbackRating: undefined,
  });
  const visibleGalleryTabs = filterItemsByPermission(
    galleryTabs,
    {
      images: "images.read",
      videos: "videos.read",
      fileinfo: "galleries.read",
    },
    hasPermission,
  );

  useDocumentTitle(gallery ? getGalleryDisplayTitle(gallery) : null);

  // Close ops menu on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (opsMenuRef.current && !opsMenuRef.current.contains(e.target as Node)) setShowOpsMenu(false);
    };
    if (showOpsMenu) document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [showOpsMenu]);

  const galleryKeyboardShortcuts = useMemo(
    () => [
      {
        id: "detail.edit",
        key: "e",
        keys: "e",
        surface: "detail" as const,
        description: "Edit gallery",
        handler: () => {
          if (canWriteGallery) {
            setEditing(true);
          }
        },
      },
      {
        id: "detail.gallery.images",
        key: "a",
        keys: "a",
        surface: "detail" as const,
        description: "Open images tab",
        handler: () => {
          if (canReadGalleryImages) {
            setActiveTab("images");
          }
        },
      },
      {
        id: "detail.gallery.videos",
        key: "s",
        keys: "s",
        surface: "detail" as const,
        description: "Open videos tab",
        handler: () => setActiveTab("videos"),
      },
      {
        id: "detail.fileInfo",
        key: "i",
        keys: "i",
        surface: "detail" as const,
        description: "Open file info tab",
        handler: () => setActiveTab("fileinfo"),
      },
    ],
    [canReadGalleryImages, canWriteGallery],
  );
  useKeySequence(
    galleryKeyboardShortcuts.map((shortcut) => ({
      id: shortcut.id,
      keys: shortcut.keys,
      surface: shortcut.surface,
      action: shortcut.handler,
    })),
  );

  useEffect(() => {
    if (visibleGalleryTabs.length > 0 && !visibleGalleryTabs.some((tab) => tab.key === activeTab)) {
      setActiveTab(visibleGalleryTabs[0].key);
    }
  }, [activeTab, visibleGalleryTabs]);

  const deleteMut = useMutation({
    mutationFn: () => galleries.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["galleries"] });
      goBack();
    },
  });

  const rescanMut = useMutation({ mutationFn: () => galleries.rescan(id) });

  const galleryUpdateMut = useMutation({
    mutationFn: (data: { organized?: boolean }) => galleries.update(id, data),
    onMutate: async (data) => {
      if (data.organized === undefined) return undefined;
      await queryClient.cancelQueries({ queryKey: ["gallery", id] });
      const previous = queryClient.getQueryData<Gallery>(["gallery", id]);
      queryClient.setQueryData<Gallery>(["gallery", id], (current) =>
        current ? { ...current, organized: data.organized! } : current,
      );
      return { previous };
    },
    onError: (_error, _data, context) => {
      if (context?.previous) queryClient.setQueryData(["gallery", id], context.previous);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery", id] });
      queryClient.invalidateQueries({ queryKey: ["galleries"] });
    },
  });

  const addImagesMut = useMutation({
    mutationFn: (imageIds: number[]) => galleries.addImages(id, imageIds),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["gallery-images", id] });
      queryClient.invalidateQueries({ queryKey: ["gallery", id] });
      queryClient.invalidateQueries({ queryKey: ["gallery-like-count", id] });
      setShowAddImages(false);
    },
  });

  const toLightboxImage = useCallback(
    (img: Image): LightboxImage => ({
      id: img.id,
      src: images.imageUrl(img.id),
      title: img.title,
      interactionSource: "galleryDetailPage",
      interactionMeta: { galleryId: id },
    }),
    [id],
  );
  const galleryLightbox = usePaginatedImageLightbox({
    items: galleryImages?.items ?? [],
    filter: imageFilter,
    totalCount: galleryImages?.totalCount ?? 0,
    infinitePageSize: imageInfinitePageSize,
    queryPage: queryGalleryImages,
    toLightboxImage,
  });

  if (isLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  if (isApiNotFoundError(galleryLoadError)) {
    return <div className="py-16 text-center text-secondary">Gallery not found</div>;
  }

  if (galleryLoadError) {
    return (
      <ListLoadError
        error={galleryLoadError}
        onRetry={() => {
          void retryGallery();
        }}
        title="Could not load gallery"
        className="mx-0 mt-0"
      />
    );
  }

  if (!gallery) {
    return <div className="py-16 text-center text-secondary">Gallery not found</div>;
  }

  const galleryTitle = getGalleryDisplayTitle(gallery);

  const activeContent =
    activeTab === "images" ? (
      <GalleryImagesPanel
        galleryId={id}
        filter={imageFilter}
        setFilter={setImageFilter}
        objectFilter={imageObjectFilter}
        setObjectFilter={setImageObjectFilter}
        displayMode={imageDisplayMode}
        setDisplayMode={setImageDisplayMode}
        availableDisplayModes={imageDisplayModes}
        onNavigate={onNavigate}
        galleryImages={galleryImages}
        loadError={galleryImagesLoadError}
        onRetry={() => {
          void retryGalleryImages();
        }}
        infinitePageSize={imageInfinitePageSize}
        infiniteQuery={imageInfiniteQuery}
        infiniteFilterKey={imageInfiniteFilterKey}
        fetchAllIds={fetchAllImageIds}
        loadMore={loadMoreImages}
        onShowAddImages={() => setShowAddImages(true)}
        onLightbox={galleryLightbox.openImage}
        imageZoom={imageZoom}
        setImageZoom={setImageZoom}
        canWriteGallery={canWriteGallery}
      />
    ) : activeTab === "videos" ? (
      <GalleryVideosPanel galleryId={id} onNavigate={onNavigate} />
    ) : activeTab === "fileinfo" ? (
      <GalleryFileInfo gallery={gallery} />
    ) : (
      renderExtensionTab(activeTab, id, onNavigate)
    );

  return (
    <div className="min-h-screen">
      <GalleryEditModal gallery={gallery} open={editing} onClose={() => setEditing(false)} />
      <CoverImageDialog
        open={coverOpen}
        title="Set Gallery Cover"
        entityType="gallery"
        entityId={gallery.id}
        currentImageUrl={gallery.coverPath}
        onUpload={(file) => galleries.uploadCoverImage(gallery.id, file)}
        onDelete={() => galleries.deleteCoverImage(gallery.id)}
        onClose={() => setCoverOpen(false)}
        onSuccess={() => {
          queryClient.invalidateQueries({ queryKey: ["gallery", gallery.id] });
          queryClient.invalidateQueries({ queryKey: ["galleries"] });
        }}
        aspectRatio="2/3"
        objectFit="contain"
      />
      <ConfirmDialog
        open={confirmDelete}
        title="Delete Gallery"
        message={`Delete "${galleryTitle}"? This cannot be undone.`}
        onConfirm={() => deleteMut.mutate()}
        onCancel={() => setConfirmDelete(false)}
      />

      <EntityHeroLayout
        entityType="gallery"
        entityId={gallery.id}
        backLabel={backLabel}
        onGoBack={goBack}
        imageUrl={gallery.coverPath}
        imageAlt={galleryTitle}
        imageFit="contain"
        imageContainerClassName="relative flex flex-shrink-0 items-center justify-center overflow-hidden rounded-xl border border-border bg-card shadow-xl shadow-black/35"
        imageClassName="h-auto w-auto max-h-96 max-w-[22rem] object-contain md:max-h-[34rem] md:max-w-[28rem]"
        imageFallbackClassName="h-96 w-72 items-center justify-center bg-card text-muted md:h-[34rem] md:w-[25rem]"
        onImageClick={canWriteGallery ? () => setCoverOpen(true) : undefined}
        imageFallback={<ImageIcon className="h-14 w-14" />}
        title={
          <FieldProvenanceHover fieldProvenance={gallery.fieldProvenance} fieldKey="title">
            {galleryTitle}
          </FieldProvenanceHover>
        }
        subtitle={
          <span className="inline-flex flex-wrap items-center gap-x-3 gap-y-1">
            {gallery.date ? (
              <FieldProvenanceHover fieldProvenance={gallery.fieldProvenance} fieldKey="date">
                <span>{formatDate(gallery.date)}</span>
              </FieldProvenanceHover>
            ) : null}
            {gallery.studioName && gallery.studioId ? (
              canReadStudios ? (
                <FieldProvenanceHover fieldProvenance={gallery.fieldProvenance} fieldKey="studio">
                  <button
                    onClick={() => onNavigate({ page: "studio", id: gallery.studioId })}
                    className="text-accent hover:underline"
                  >
                    {gallery.studioName}
                  </button>
                </FieldProvenanceHover>
              ) : (
                <FieldProvenanceHover fieldProvenance={gallery.fieldProvenance} fieldKey="studio">
                  <span>{gallery.studioName}</span>
                </FieldProvenanceHover>
              )
            ) : null}
            {gallery.photographer ? (
              <FieldProvenanceHover fieldProvenance={gallery.fieldProvenance} fieldKey="photographer">
                <span>Photographer: {gallery.photographer}</span>
              </FieldProvenanceHover>
            ) : null}
            {gallery.code ? (
              <FieldProvenanceHover fieldProvenance={gallery.fieldProvenance} fieldKey="code">
                <span>Code: {gallery.code}</span>
              </FieldProvenanceHover>
            ) : null}
          </span>
        }
        favorite={galleryFavorite}
        favoritePending={galleryFavoritePending}
        onFavoriteToggle={canEngageGallery ? () => setGalleryFavorite(!galleryFavorite) : undefined}
        organized={gallery.organized}
        organizedPending={galleryUpdateMut.isPending}
        onOrganizedToggle={canWriteGallery ? (organized) => galleryUpdateMut.mutate({ organized }) : undefined}
        description={
          gallery.details ? (
            <FieldProvenanceHover fieldProvenance={gallery.fieldProvenance} fieldKey="details" block>
              <NarrativeText>{gallery.details}</NarrativeText>
            </FieldProvenanceHover>
          ) : undefined
        }
        counts={[
          { key: "images", label: "Images", value: effectiveImageCount, icon: <ImageIcon className="h-4 w-4" /> },
          { key: "videos", label: "Videos", value: gallery.videoCount, icon: <Film className="h-4 w-4" /> },
          { key: "files", label: "Files", value: gallery.files.length, icon: <HardDrive className="h-4 w-4" /> },
          { key: "likes", label: "Likes", value: galleryLikeCount, icon: <ThumbsUp className="h-4 w-4" /> },
        ]}
        metaRow={
          <>
            <span title={`Created ${formatDate(gallery.createdAt)}`}>Updated {formatDate(gallery.updatedAt)}</span>
          </>
        }
        heroContent={
          <>
            <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
              <div className="shrink-0">
                <InteractiveRating
                  value={galleryRating}
                  onChange={(value) => setGalleryRating(value)}
                  readOnly={!canEngageGallery}
                />
              </div>
            </div>

            {canReadPerformers && gallery.performers.length > 0 ? (
              <div
                className={`mt-4 grid gap-3 ${gallery.performers.length > 1 ? "grid-cols-2 sm:grid-cols-3 lg:grid-cols-4" : "max-w-[220px]"}`}
              >
                {gallery.performers.map((performer) => (
                  <PerformerTile
                    key={performer.id}
                    performer={performer}
                    onClick={() => onNavigate({ page: "performer", id: performer.id })}
                    onNavigate={onNavigate}
                    referenceDate={gallery.date}
                  />
                ))}
              </div>
            ) : null}

            {gallery.urls.length > 0 ? (
              <FieldProvenanceHover fieldProvenance={gallery.fieldProvenance} fieldKey="urls" block className="mt-4">
                <div className="flex flex-wrap gap-2">
                  {gallery.urls.map((url, index) => (
                    <a
                      key={index}
                      href={url}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="inline-flex items-center gap-1.5 rounded-full border border-border bg-card px-3 py-1 text-xs text-accent hover:border-accent/60 hover:text-accent-hover"
                    >
                      <LinkIcon className="h-3 w-3" />
                      {(() => {
                        try {
                          return new URL(url).hostname.replace("www.", "");
                        } catch {
                          return url;
                        }
                      })()}
                    </a>
                  ))}
                </div>
              </FieldProvenanceHover>
            ) : null}

            {canReadTags && gallery.tags.length > 0 ? (
              <div className="mt-4 flex flex-wrap gap-1.5">
                {gallery.tags.map((tag) => (
                  <TagBadge
                    key={tag.id}
                    name={tag.name}
                    tag={tag}
                    provenance={resolveTagProvenance(tag, gallery.fieldProvenance)}
                    onClick={() => onNavigate({ page: "tag", id: tag.id })}
                  />
                ))}
              </div>
            ) : null}

            <CustomFieldsDisplay customFields={gallery.customFields} entityType="gallery" />
          </>
        }
        actions={
          <>
            <ExtensionSlot slot="gallery-detail-actions" context={{ gallery, onNavigate }} />
            {canWriteGallery ? (
              <button type="button" onClick={() => setEditing(true)} className={HERO_PRIMARY_ACTION_BUTTON_CLASS}>
                <Pencil className="h-3.5 w-3.5" /> Edit
              </button>
            ) : null}
            <div className="relative" ref={opsMenuRef}>
              <button
                type="button"
                onClick={() => setShowOpsMenu(!showOpsMenu)}
                aria-label="Open gallery operations"
                className={HERO_ACTION_BUTTON_CLASS}
                title="Operations"
              >
                <MoreVertical className="h-4 w-4" />
              </button>
              <FloatingActionMenu
                open={showOpsMenu}
                anchorRef={opsMenuRef}
                onClose={() => setShowOpsMenu(false)}
                className="min-w-[180px] py-1"
              >
                {canLibraryScan ? (
                  <button
                    onClick={() => {
                      rescanMut.mutate();
                      setShowOpsMenu(false);
                    }}
                    className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-foreground hover:bg-surface"
                  >
                    <RefreshCw className="h-3.5 w-3.5" /> Rescan
                  </button>
                ) : null}
                {canDeleteGallery ? <div className="my-1 border-t border-border" /> : null}
                {canDeleteGallery ? (
                  <button
                    onClick={() => {
                      setConfirmDelete(true);
                      setShowOpsMenu(false);
                    }}
                    className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-red-400 hover:bg-surface"
                  >
                    <Trash2 className="h-3.5 w-3.5" /> Delete
                  </button>
                ) : null}
              </FloatingActionMenu>
            </div>
          </>
        }
      >
        <EntityDetailTabs
          tabs={visibleGalleryTabs}
          activeTab={activeTab}
          onTabChange={(key) => setActiveTab(key as TabKey)}
          className="mb-4"
        />

        {activeContent}
        <ExtensionSlot slot="gallery-detail-main-bottom" context={{ gallery, onNavigate }} />
      </EntityHeroLayout>

      <ExtensionSlot slot="gallery-detail-bottom" context={{ gallery, onNavigate }} />

      {/* Add Images Dialog */}
      {showAddImages && canWriteGallery && (
        <AddImagesDialog
          existingImageIds={new Set(galleryImages?.items.map((i) => i.id) ?? [])}
          onAdd={(ids) => addImagesMut.mutate(ids)}
          onClose={() => setShowAddImages(false)}
          isPending={addImagesMut.isPending}
        />
      )}

      <Lightbox {...galleryLightbox.lightboxProps} canEngage={canEngageImages} canLike={canWriteImages} />
    </div>
  );
}

function GalleryVideosPanel({ galleryId, onNavigate }: { galleryId: number; onNavigate: (r: any) => void }) {
  const [zoomLevel, setZoomLevel] = useState(0);
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, availableDisplayModes } =
    useRelatedDetailListUrlState({
      stateKey: "videos",
      resetKey: "gallery-videos",
      entityType: "videos",
      builtInFilter: { page: 1, perPage: 24, direction: "desc" },
      defaultFilterKey: GALLERY_VIDEOS_DEFAULT_FILTER_KEY,
    });
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const queryPage = useCallback(
    (nextFilter: FindFilter) =>
      hasObjectFilter
        ? videos.findFiltered({
            findFilter: nextFilter,
            objectFilter: withRequiredMultiId(objectFilter as VideoFilterCriteria, "galleriesCriterion", galleryId),
          })
        : videos.find(nextFilter, { galleryId: String(galleryId) }),
    [galleryId, hasObjectFilter, objectFilter],
  );
  const {
    data,
    isLoading,
    loadError,
    retry,
    infinitePageSize,
    infiniteQuery,
    infiniteFilterKey,
    fetchAllIds,
    loadMore,
  } = useDetailListQuery<Video>({
    queryKey: ["gallery-videos", galleryId, objectFilter],
    filter,
    queryFn: queryPage,
  });
  const items = data?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({
    items,
    infinitePageSize,
    infiniteFilterKey,
    fetchAllIds,
    resetKeyParts: [objectFilter],
  });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar
      filter={filter}
      onFilterChange={setFilter}
      totalCount={data?.totalCount ?? 0}
      sortOptions={VIDEO_SORT_OPTIONS}
      zoomLevel={zoomLevel}
      onZoomChange={setZoomLevel}
      cardSizeEntityType="videos"
      showSearch
      selectedCount={selectedIds.size}
      onSelectAll={selectAll}
      selectAllPending={selectAllPending}
      onSelectAllMatching={selectShown}
      selectAllMatchingLabel="Select shown"
      onSelectNone={selectNone}
      selectionActions={
        <BulkSelectionActions
          entityType="videos"
          selectedIds={selectedIds}
          onDone={selectNone}
          videoItems={items}
          onNavigate={onNavigate}
          removeFromParent={{ type: "gallery", id: galleryId }}
        />
      }
      criteriaDefinitions={VIDEO_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      filterMode="videos"
      filterDefaultKey={GALLERY_VIDEOS_DEFAULT_FILTER_KEY}
      defaultFilterResolved
      allowInfinitePageSize
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={availableDisplayModes}
    />
  );

  if (loadError)
    return (
      <ListLoadError
        error={loadError}
        onRetry={() => {
          void retry();
        }}
        className="mt-3"
      />
    );
  if (isLoading) return <LoadingPanel icon={<Film className="h-10 w-10" />} message="Loading videos..." />;
  if (!data || items.length === 0)
    return (
      <>
        {toolbar}
        <EmptyPanel icon={<Film className="h-12 w-12" />} message="No videos for this gallery" />
      </>
    );

  return (
    <>
      {toolbar}
      <ContextualVideoListView
        items={items}
        filter={filter}
        totalCount={data.totalCount}
        queryPage={queryPage}
        displayMode={displayMode}
        zoomLevel={zoomLevel}
        selectedIds={selectedIds}
        selecting={selecting}
        onToggle={toggle}
        onNavigate={onNavigate}
        onVideoQuickView={setQuickViewId}
        infinitePageSize={infinitePageSize}
        hasNextPage={infiniteQuery.hasNextPage}
        isFetchingNextPage={infiniteQuery.isFetchingNextPage}
        loadMore={loadMore}
      />
      <DetailListPagination
        filter={filter}
        onFilterChange={setFilter}
        totalCount={data.totalCount}
        allowInfinitePageSize
      />
      {quickViewId !== null && (
        <QuickViewDialog type="video" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function GalleryImagesPanel({
  galleryId,
  filter,
  setFilter,
  objectFilter,
  setObjectFilter,
  displayMode,
  setDisplayMode,
  availableDisplayModes,
  onNavigate,
  galleryImages,
  loadError,
  onRetry,
  infinitePageSize,
  infiniteQuery,
  infiniteFilterKey,
  fetchAllIds,
  loadMore,
  onShowAddImages,
  onLightbox,
  imageZoom,
  setImageZoom,
  canWriteGallery,
}: {
  galleryId: number;
  filter: FindFilter;
  setFilter: (f: FindFilter) => void;
  objectFilter: Record<string, unknown>;
  setObjectFilter: (filter: Record<string, unknown>) => void;
  displayMode: ReturnType<typeof useRelatedDetailListUrlState>["displayMode"];
  setDisplayMode: ReturnType<typeof useRelatedDetailListUrlState>["setDisplayMode"];
  availableDisplayModes: ReturnType<typeof useRelatedDetailListUrlState>["availableDisplayModes"];
  onNavigate: (r: any) => void;
  galleryImages: { items: any[]; totalCount: number } | undefined;
  loadError: Error | null;
  onRetry: () => void;
  infinitePageSize: boolean;
  infiniteQuery: ReturnType<typeof useDetailListQuery<Image>>["infiniteQuery"];
  infiniteFilterKey: unknown;
  fetchAllIds: () => Promise<Array<Image["id"]>>;
  loadMore: () => void;
  onShowAddImages: () => void;
  onLightbox: (imageId: number) => void;
  imageZoom: number;
  setImageZoom: (z: number) => void;
  canWriteGallery: boolean;
}) {
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const items = galleryImages?.items ?? [];
  const { selectedIds, toggle, selectAll, selectAllPending, selectShown, selectNone } = useDetailListSelection({
    items,
    infinitePageSize,
    infiniteFilterKey,
    fetchAllIds,
    resetKeyParts: [objectFilter],
  });
  const selecting = selectedIds.size > 0;
  const toolbar = (
    <DetailListToolbar
      filter={filter}
      onFilterChange={setFilter}
      totalCount={galleryImages?.totalCount ?? 0}
      sortOptions={IMAGE_SORT_OPTIONS}
      zoomLevel={imageZoom}
      onZoomChange={setImageZoom}
      cardSizeEntityType="images"
      showSearch
      selectedCount={selectedIds.size}
      onSelectAll={selectAll}
      selectAllPending={selectAllPending}
      onSelectAllMatching={selectShown}
      selectAllMatchingLabel="Select shown"
      onSelectNone={selectNone}
      criteriaDefinitions={IMAGE_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      filterMode="images"
      filterDefaultKey={GALLERY_IMAGES_DEFAULT_FILTER_KEY}
      defaultFilterResolved
      allowInfinitePageSize
      displayMode={displayMode}
      onDisplayModeChange={setDisplayMode}
      availableDisplayModes={availableDisplayModes}
      selectionActions={
        <BulkSelectionActions
          entityType="images"
          selectedIds={selectedIds}
          onDone={selectNone}
          downloadItems={items}
          removeFromParent={{ type: "gallery", id: galleryId }}
        />
      }
    />
  );

  if (loadError) return <ListLoadError error={loadError} onRetry={onRetry} className="mt-3" />;

  if (!galleryImages || items.length === 0)
    return (
      <>
        {toolbar}
        {canWriteGallery ? (
          <div className="flex justify-end mb-3">
            <button
              onClick={onShowAddImages}
              className="flex items-center gap-1 px-2 py-1 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10 border border-border"
            >
              <Plus className="w-3 h-3" /> Add Images
            </button>
          </div>
        ) : null}
        <EmptyPanel icon={<ImageIcon className="h-12 w-12" />} message="No images in this gallery" />
      </>
    );

  return (
    <>
      {toolbar}
      {canWriteGallery ? (
        <div className="flex justify-end mb-2">
          <button
            onClick={onShowAddImages}
            className="flex items-center gap-1 px-2 py-1 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10 border border-border"
          >
            <Plus className="w-3 h-3" /> Add Images
          </button>
        </div>
      ) : null}
      <RelatedEntityListView
        entityType="images"
        items={items}
        displayMode={displayMode}
        zoomLevel={imageZoom}
        selectedIds={selectedIds}
        selecting={selecting}
        onToggle={toggle}
        onNavigate={onNavigate}
        onImageQuickView={setQuickViewId}
        onImagePreview={(image) => onLightbox(image.id)}
        onImageDetails={(image) => onNavigate({ page: "image", id: image.id })}
        infinitePageSize={infinitePageSize}
        hasNextPage={infiniteQuery.hasNextPage}
        isFetchingNextPage={infiniteQuery.isFetchingNextPage}
        loadMore={loadMore}
      />
      <DetailListPagination
        filter={filter}
        onFilterChange={setFilter}
        totalCount={galleryImages.totalCount}
        allowInfinitePageSize
      />
      {quickViewId !== null && (
        <QuickViewDialog type="image" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      )}
    </>
  );
}

function AddImagesDialog({
  existingImageIds,
  onAdd,
  onClose,
  isPending,
}: {
  existingImageIds: Set<number>;
  onAdd: (ids: number[]) => void;
  onClose: () => void;
  isPending: boolean;
}) {
  const [selectedImageIds, setSelectedImageIds] = useState<number[]>([]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70" onClick={onClose}>
      <div
        className="bg-card border border-border rounded-xl shadow-2xl w-full max-w-xl max-h-[80vh] flex flex-col"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-border">
          <h2 className="text-lg font-semibold text-foreground">Add Images to Gallery</h2>
          <div className="flex items-center gap-3">
            <span className="text-xs text-muted">{selectedImageIds.length} selected</span>
            <button
              onClick={() => onAdd(selectedImageIds)}
              disabled={selectedImageIds.length === 0 || isPending}
              className="px-3 py-1.5 rounded text-sm font-medium bg-accent hover:bg-accent-hover text-white disabled:opacity-50 flex items-center gap-2"
            >
              {isPending && <Loader2 className="w-3.5 h-3.5 animate-spin" />}
              Add {selectedImageIds.length > 0 ? selectedImageIds.length : ""}
            </button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto p-5">
          <EntityReferenceMultiSelector
            entityType="image"
            values={selectedImageIds}
            onChange={setSelectedImageIds}
            excludeIds={existingImageIds}
            disabled={isPending}
            placeholder="Search images..."
            emptyMessage="No images found"
            resultsClassName="rounded border border-border bg-surface"
            resultsMaxHeight={288}
          />
        </div>

        <div className="flex items-center justify-end px-5 py-3 border-t border-border">
          <button onClick={onClose} className="px-3 py-1.5 rounded text-sm text-secondary hover:text-foreground">
            Cancel
          </button>
        </div>
      </div>
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

function GalleryFileInfo({
  gallery,
}: {
  gallery: {
    folderPath?: string;
    files: {
      id: number;
      path: string;
      size: number;
      modTime: string;
      fingerprints: { type: string; value: string }[];
    }[];
  };
}) {
  const hasFolder = !!gallery.folderPath;
  const hasFiles = gallery.files.length > 0;
  const revealMutation = useMutation({ mutationFn: (fileId: number) => fileOps.reveal(fileId) });
  const canReveal =
    typeof window !== "undefined" && ["localhost", "127.0.0.1", "::1"].includes(window.location.hostname);

  if (!hasFolder && !hasFiles) {
    return <EmptyPanel icon={<HardDrive className="h-8 w-8" />} message="No file information available" />;
  }

  return (
    <div className="space-y-4">
      {hasFolder && (
        <div className="rounded-xl border border-border bg-card p-4">
          <h3 className="mb-3 text-sm font-semibold uppercase tracking-wide text-muted">Folder</h3>
          <dl className="space-y-2 text-sm">
            <div>
              <dt className="text-muted">Path</dt>
              <dd className="font-mono text-xs text-foreground break-all">{gallery.folderPath}</dd>
            </div>
          </dl>
        </div>
      )}
      {gallery.files.map((file) => (
        <div key={file.id} className="rounded-xl border border-border bg-card p-4">
          <div className="mb-3 flex items-center justify-between gap-3">
            <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">File</h3>
            {canReveal ? (
              <button
                type="button"
                onClick={() => revealMutation.mutate(file.id)}
                className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-xs text-secondary hover:border-accent hover:text-foreground"
              >
                <FolderOpen className="h-3.5 w-3.5" />
                Reveal
              </button>
            ) : null}
          </div>
          <dl className="space-y-2 text-sm">
            <div>
              <dt className="text-muted">Path</dt>
              <dd className="font-mono text-xs text-foreground break-all">{file.path}</dd>
            </div>
            <div>
              <dt className="text-muted">Size</dt>
              <dd className="text-foreground">{formatFileSize(file.size)}</dd>
            </div>
            <div>
              <dt className="text-muted">Modified</dt>
              <dd className="text-foreground">{formatDate(file.modTime)}</dd>
            </div>
            {file.fingerprints.length > 0 && (
              <div>
                <dt className="text-muted mb-1">Fingerprints</dt>
                {file.fingerprints.map((fp, i) => (
                  <dd key={i} className="text-foreground">
                    <span className="text-muted text-xs uppercase">{fp.type}:</span>{" "}
                    <span className="font-mono text-xs break-all">{fp.value}</span>
                  </dd>
                ))}
              </div>
            )}
          </dl>
        </div>
      ))}
    </div>
  );
}
