import { useState, useMemo, useCallback, useEffect, useRef, lazy, Suspense } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { entityEngagement, images } from "../api/client";
import type { DeleteEntityOptions, EntityEngagement, FindFilter, Image, ImageFilterCriteria } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { toggleOptionsFromEvent, useMultiSelect, type BoundMultiSelectToggleHandler, type MultiSelectToggleOptions } from "../hooks/useMultiSelect";
import { useListUrlState } from "../hooks/useListUrlState";
import { useInfiniteListData } from "../hooks/useInfiniteListData";
import { useVisualSimilarityApi } from "../hooks/useVisualSimilarityApi";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { ImageIcon, Trash2, Loader2, Edit, FolderOpen, Play, Search, ThumbsUp, Eye, Heart } from "lucide-react";
import { IMAGE_CRITERIA } from "../components/FilterDialog";
import { BulkEditDialog, IMAGE_BULK_FIELDS } from "../components/BulkEditDialog";
import { ImageTile } from "../components/EntityCards";
import type { LightboxImage } from "../components/Lightbox";
import { extendLightboxPageBounds } from "../utils/lightboxPagination";
import { getDefaultFilter } from "../components/SavedFilterMenu";
import { CardSelectionToggle, RouteCardLinkOverlay } from "../components/RouteCardLinkOverlay";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity, canReadEntity, canWriteEntity } from "../auth/visibility";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import { useWallColumns } from "../hooks/useWallColumns";
import { ExtensionSelectionActions } from "../components/ExtensionSelectionActions";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { withSeededRandomSort } from "../utils/seededRandomSort";
import { WallMediaCard } from "../components/WallMediaCard";
import { FeedActionPill, FeedCardFrame, FeedChipButton, FeedChipOverflowMenu, FeedIdentityBadge, FeedInlineRating, FeedMetadataPill, FeedPortraitMediaFrame, getFeedMediaStyle } from "../components/FeedCardFrame";
import { BookmarkButton } from "../components/BookmarkButton";
import { ScraperEntityTagger } from "../components/ScraperEntityTagger";
import { RelatedEntityListView } from "../components/RelatedEntityListView";
import { VirtualizedInfiniteList } from "../components/VirtualizedInfiniteList";
import { VirtualizedEntityGrid, VirtualizedWallColumns } from "../components/VirtualizedEntityLayouts";
import { useAppConfig } from "../state/AppConfigContext";
import { IMAGE_SORT_OPTIONS } from "../components/imageSortOptions";

const Lightbox = lazy(() => import("../components/Lightbox").then((module) => ({ default: module.Lightbox })));
const ImageCreateModal = lazy(() => import("./ImageEditModal").then((module) => ({ default: module.ImageCreateModal })));
const QuickViewDialog = lazy(() => import("../components/QuickViewDialog").then((module) => ({ default: module.QuickViewDialog })));

const SEARCH_MODE_OPTIONS = [
  { value: "text", label: "Text", title: "Text search" },
  { value: "visual", label: "Visual", title: "Visual semantic search" },
];

const VISUAL_MATCH_SORT_OPTION = { value: "visual_match", label: "Visual Match" };

interface Props {
  onNavigate: (r: any) => void;
}

export function ImagesPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("images");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "date", direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: "grid" as DisplayMode,
    };
  }, []);
  const visualSimilarity = useVisualSimilarityApi();
  const visualSimilarityAvailable = visualSimilarity != null;
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, searchMode, setSearchMode } = useListUrlState({
    resetKey: "images",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "wall", "tagger", "feed"] as const,
    defaultSearchMode: "text",
    allowedSearchModes: visualSimilarityAvailable ? ["text", "visual"] : ["text"],
    allowInfinitePageSize: true,
  });
  const [showCreate, setShowCreate] = useState(false);
  const [showBulkEdit, setShowBulkEdit] = useState(false);
  const [lightboxOpen, setLightboxOpen] = useState(false);
  const [lightboxIndex, setLightboxIndex] = useState(0);
  const [lightboxAutoPlay, setLightboxAutoPlay] = useState(false);
  const [lightboxScopeIds, setLightboxScopeIds] = useState<Set<number> | null>(null);
  const [lightboxPageBounds, setLightboxPageBounds] = useState(() => ({ first: filter.page ?? 1, last: filter.page ?? 1 }));
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const [wallColumnCount, setWallColumnCount] = useState(6);
  const lastPagedFilterRef = useRef<Pick<FindFilter, "page" | "perPage">>({ page: defaultState.filter.page ?? 1, perPage: defaultState.filter.perPage });
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const queryClient = useQueryClient();
  const { config } = useAppConfig();
  const { hasPermission, user } = useAuth();
  const canWriteImage = canWriteEntity("image", hasPermission);
  const canDeleteImage = canDeleteEntity("image", hasPermission);
  const canEngageImage = canReadEntity("image", hasPermission) && (user?.kind === "user" || user?.kind === "system");

  const hasObjectFilter = Object.keys(objectFilter).length > 0;
  const visualSearchActive = visualSimilarityAvailable && searchMode === "visual" && Boolean(filter.q?.trim());
  const infinitePageSize = filter.perPage === 0 || displayMode === "feed";
  const defaultInfiniteChunkSize = defaultState.filter.perPage && defaultState.filter.perPage > 0 ? defaultState.filter.perPage : 40;
  const infiniteChunkSize = displayMode === "feed" ? 8 : defaultInfiniteChunkSize;
  const searchModeOptions = useMemo(() => visualSimilarityAvailable ? SEARCH_MODE_OPTIONS : SEARCH_MODE_OPTIONS.filter((mode) => mode.value === "text"), [visualSimilarityAvailable]);
  const sortOptions = useMemo(
    () => visualSimilarityAvailable && searchMode === "visual" ? [VISUAL_MATCH_SORT_OPTION, ...IMAGE_SORT_OPTIONS] : IMAGE_SORT_OPTIONS,
    [visualSimilarityAvailable, searchMode],
  );

  useEffect(() => {
    if (!visualSimilarityAvailable && searchMode === "visual") {
      setSearchMode("text");
      if (filter.sort === "visual_match") {
        setFilter({ ...filter, sort: defaultState.filter.sort, direction: defaultState.filter.direction ?? "desc", page: 1 });
      }
    }
  }, [defaultState.filter.direction, defaultState.filter.sort, filter, searchMode, setFilter, setSearchMode, visualSimilarityAvailable]);

  const handleSearchModeChange = useCallback((mode: string) => {
    if (mode === "visual" && !visualSimilarityAvailable) {
      return;
    }

    setSearchMode(mode);

    if (mode === "visual") {
      setFilter({ ...filter, sort: "visual_match", direction: "desc", page: 1 });
      return;
    }

    if (filter.sort === "visual_match") {
      setFilter({
        ...filter,
        sort: defaultState.filter.sort,
        direction: defaultState.filter.direction ?? "desc",
        page: 1,
      });
      return;
    }

    setFilter({ ...filter, page: 1 });
  }, [defaultState.filter.direction, defaultState.filter.sort, filter, setFilter, setSearchMode, visualSimilarityAvailable]);

  useEffect(() => {
    if (displayMode !== "feed" && filter.perPage !== 0) {
      lastPagedFilterRef.current = { page: filter.page ?? 1, perPage: filter.perPage };
    }
  }, [displayMode, filter.page, filter.perPage]);

  const handleDisplayModeChange = useCallback((mode: DisplayMode) => {
    const requiresInfinite = mode === "feed";

    if (requiresInfinite && filter.perPage !== 0) {
      lastPagedFilterRef.current = { page: filter.page ?? 1, perPage: filter.perPage };
    }

    setDisplayMode(mode);

    if (requiresInfinite && filter.perPage !== 0) {
      setFilter({ ...filter, page: 1, perPage: 0 });
      return;
    }

    if (!requiresInfinite && filter.perPage === 0) {
      const lastPagedFilter = lastPagedFilterRef.current;
      setFilter({ ...filter, page: lastPagedFilter.page ?? 1, perPage: lastPagedFilter.perPage ?? defaultState.filter.perPage });
    }
  }, [defaultState.filter.perPage, filter, setDisplayMode, setFilter]);

  const queryImagesPage = useCallback((nextFilter: FindFilter) => {
    if (visualSearchActive) {
      return visualSimilarity.searchImages({
        findFilter: nextFilter,
        objectFilter: hasObjectFilter ? objectFilter as ImageFilterCriteria : undefined,
      });
    }
    return hasObjectFilter
      ? images.findFiltered({ findFilter: nextFilter, objectFilter: objectFilter as ImageFilterCriteria })
      : images.find(nextFilter);
  }, [hasObjectFilter, objectFilter, visualSearchActive, visualSimilarity]);

  const listData = useInfiniteListData<Image>({
    queryKey: ["images", objectFilter, searchMode],
    filter,
    chunkSize: infiniteChunkSize,
    queryPage: queryImagesPage,
  });

  const items = listData.items;
  const totalCount = listData.totalCount;
  const loading = listData.isLoading;
  const { engagementById } = useEntityEngagementBatch("image", items.map((item) => item.id));
  const estimateImageWallHeight = useCallback((image: Image) => {
    const file = image.files[0];
    return file?.width && file.height ? file.height / file.width : 1;
  }, []);
  const wallColumnOptions = useMemo(() => ({ stable: infinitePageSize, getKey: (image: Image) => image.id }), [infinitePageSize]);
  const wallColumns = useWallColumns(items, wallColumnCount, estimateImageWallHeight, wallColumnOptions);
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: listData.infiniteFilterKey, objectFilter, searchMode }), [listData.infiniteFilterKey, objectFilter, searchMode]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(items, { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const selectedVisibleImages = useMemo(() => items.filter((item) => selectedIds.has(item.id)), [items, selectedIds]);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);

  const lightboxSourceItems = useMemo(
    () => lightboxScopeIds ? items.filter((item) => lightboxScopeIds.has(item.id)) : items,
    [items, lightboxScopeIds],
  );
  const lightboxImages: LightboxImage[] = useMemo(
    () => lightboxSourceItems.map((img) => ({
      id: img.id,
      src: images.imageUrl(img.id),
      title: getImageDisplayTitle(img),
      interactionSource: "imagesPage",
      interactionMeta: { pageKey: "images" },
    })),
    [lightboxSourceItems],
  );

  const closeLightbox = useCallback(() => {
    setLightboxOpen(false);
    setLightboxAutoPlay(false);
    setLightboxScopeIds(null);
  }, []);

  const openListLightbox = useCallback((imageId: number) => {
    setLightboxScopeIds(null);
    const page = filter.page ?? 1;
    setLightboxPageBounds({ first: page, last: page });
    setLightboxAutoPlay(false);
    setLightboxIndex(Math.max(0, items.findIndex((item) => item.id === imageId)));
    setLightboxOpen(true);
  }, [filter.page, items]);

  const loadLightboxPage = useCallback(async (page: number, direction: "previous" | "next") => {
    const response = await queryImagesPage({ ...filter, page });
    setLightboxPageBounds((bounds) => extendLightboxPageBounds(bounds, page, direction));
    return response.items.map((img) => ({ id: img.id, src: images.imageUrl(img.id), title: getImageDisplayTitle(img), interactionSource: "imagesPage", interactionMeta: { pageKey: "images" } }));
  }, [filter, queryImagesPage]);

  const playSelectedImages = useCallback(() => {
    if (selectedVisibleImages.length === 0) {
      return;
    }

    setLightboxScopeIds(new Set(selectedVisibleImages.map((image) => image.id)));
    setLightboxIndex(0);
    setLightboxAutoPlay(selectedVisibleImages.length > 1);
    setLightboxOpen(true);
  }, [selectedVisibleImages]);

  const handleFilterChange = useCallback((next: typeof filter) => {
    setFilter(withSeededRandomSort(filter, next));
  }, [filter, setFilter]);

  const handleSelectAllMatching = useCallback(async () => {
    setSelectAllMatchingPending(true);
    try {
      selectIds(await listData.fetchAllIds());
    } finally {
      setSelectAllMatchingPending(false);
    }
  }, [listData, selectIds]);

  const bulkDeleteMut = useMutation<void, Error, DeleteEntityOptions | undefined>({
    mutationFn: async (options) => {
      await images.bulkDelete([...selectedIds], options);
    },
    onSuccess: () => { setShowDeleteConfirm(false); selectNone(); queryClient.invalidateQueries({ queryKey: ["images"] }); },
  });

  const bulkEditMut = useMutation({
    mutationFn: (values: Record<string, unknown>) =>
      images.bulkUpdate({ ids: [...selectedIds], ...values } as any),
    onSuccess: () => {
      setShowBulkEdit(false);
      selectNone();
      queryClient.invalidateQueries({ queryKey: ["images"] });
    },
  });

  return (
    <>
    <Suspense fallback={null}>
      {showCreate ? <ImageCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "image", id })} /> : null}
    </Suspense>
    <ListPage
      title="Images"
      pageKey="images"
      filterMode="images"
      filter={filter}
      onFilterChange={handleFilterChange}
      totalCount={totalCount}
      isLoading={loading}
      error={listData.loadError}
      onRetry={() => { void listData.refetch(); }}
      searchMode={searchMode}
      searchModes={searchModeOptions}
      searchPlaceholder={visualSimilarityAvailable && searchMode === "visual" ? "Search visuals..." : "Search images, tags, performers..."}
      onSearchModeChange={handleSearchModeChange}
      sortOptions={sortOptions}
      displayMode={displayMode}
      onDisplayModeChange={handleDisplayModeChange}
      availableDisplayModes={["grid", "list", "wall", "tagger", "feed"]}
      allowInfinitePageSize
      onNew={canWriteImage ? () => setShowCreate(true) : undefined}
      criteriaDefinitions={IMAGE_CRITERIA}
      objectFilter={objectFilter}
      onObjectFilterChange={setObjectFilter}
      wallColumnCount={wallColumnCount}
      onWallColumnCountChange={setWallColumnCount}
      showPagingControls={!infinitePageSize}
      infiniteScroll={listData.infiniteScroll}
      onSelectAll={infinitePageSize ? handleSelectAllMatching : selectAll}
      selectAllPending={infinitePageSize ? selectAllMatchingPending : false}
      onSelectAllMatching={infinitePageSize ? selectAll : undefined}
      selectAllMatchingLabel="Select shown"

      selectedIds={selectedIds}
      onSelectNone={selectNone}
      onInvertSelection={invertSelection}
      selectionActions={
        <>
          {selectedVisibleImages.length > 1 ? (
            <button
              onClick={playSelectedImages}
              className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10"
            >
              <Play className="w-3 h-3" />
              Play
            </button>
          ) : null}
          {canWriteImage && (
            <button
              onClick={() => setShowBulkEdit(true)}
              className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-accent hover:text-accent-hover hover:bg-accent/10"
            >
              <Edit className="w-3 h-3" />
              Edit
            </button>
          )}
          <ExtensionSelectionActions entityType="image" selectedIds={selectedIds} />
          {canDeleteImage && (
            <button
              onClick={() => setShowDeleteConfirm(true)}
              disabled={bulkDeleteMut.isPending}
              className="flex items-center gap-1 px-2 py-0.5 rounded text-xs text-red-400 hover:text-red-300 hover:bg-red-900/20"
            >
              {bulkDeleteMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
              Delete
            </button>
          )}
        </>
      }
    >
      <ConfirmDialog
        open={showDeleteConfirm}
        title={`Delete ${selectedIds.size} image${selectedIds.size === 1 ? "" : "s"}`}
        message={`Delete ${selectedIds.size} selected image${selectedIds.size === 1 ? "" : "s"}? This cannot be undone.`}
        confirmLabel={bulkDeleteMut.isPending ? "Deleting..." : "Delete"}
        onConfirm={(options) => bulkDeleteMut.mutate(options)}
        onCancel={() => { bulkDeleteMut.reset(); setShowDeleteConfirm(false); }}
        isPending={bulkDeleteMut.isPending}
        errorMessage={bulkDeleteMut.error?.message ?? null}
        showDeleteFile
        showDeleteGenerated
      />
      {displayMode === "feed" ? (
        <div className="mx-auto w-full max-w-[64rem] px-3 sm:px-4">
          <VirtualizedInfiniteList
            items={items}
            getItemKey={(image) => image.id}
            estimateSize={760}
            overscan={2}
            hasNextPage={Boolean(listData.infiniteQuery.hasNextPage)}
            isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
            loadMore={listData.loadMore}
            itemClassName="pb-5"
            renderItem={({ item: img }) => (
            <ImageFeedCard
              image={img}
              engagement={engagementById.get(img.id)}
              onNavigate={onNavigate}
              canEngage={canEngageImage}
              selected={selectedIds.has(img.id)}
              onSelect={(toggleOptions) => toggle(img.id, toggleOptions)}
              selecting={selecting}
            />
            )}
          />
        </div>
      ) : displayMode === "tagger" ? (
        <ScraperEntityTagger
          entityType="image"
          label="Image"
          items={items}
          selectedIds={selectedIds}
          selecting={selecting}
          onSelect={toggle}
          getTitle={getImageDisplayTitle}
          getImageUrl={(image) => images.thumbnailUrl(image.id, 320)}
          getRoute={(image) => ({ page: "image", id: image.id })}
          queryKey="images"
        />
      ) : displayMode === "list" ? (
        <RelatedEntityListView
          entityType="images"
          items={items}
          displayMode="list"
          selectedIds={selectedIds}
          selecting={selecting}
          onToggle={toggle}
          onNavigate={onNavigate}
          infinitePageSize={infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
        />
      ) : displayMode === "grid" ? (
        <VirtualizedEntityGrid
          items={items}
          getItemKey={(image) => image.id}
          minCardWidth="var(--card-min-width, 140px)"
          estimateRowHeight={260}
          infinitePageSize={infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
          renderItem={(img) => (
            <ImageTile
              image={img}
              engagement={engagementById.get(img.id)}
              onClick={(toggleOptions) => {
                if (selecting) { toggle(img.id, toggleOptions); return; }
                onNavigate({ page: "image", id: img.id });
              }}
              onPreview={(toggleOptions) => {
                if (selecting) { toggle(img.id, toggleOptions); return; }
                openListLightbox(img.id);
              }}
              onDetails={() => {
                if (selecting) { toggle(img.id); return; }
                onNavigate({ page: "image", id: img.id });
              }}
              onNavigate={onNavigate}
              selected={selectedIds.has(img.id)}
              onSelect={(toggleOptions) => toggle(img.id, toggleOptions)}
              selecting={selecting}
              onQuickView={() => setQuickViewId(img.id)}
            />
          )}
        />
      ) : (
        <VirtualizedWallColumns
          columns={wallColumns}
          getItemKey={(image) => image.id}
          infinitePageSize={infinitePageSize}
          hasNextPage={listData.infiniteQuery.hasNextPage}
          isFetchingNextPage={listData.infiniteQuery.isFetchingNextPage}
          loadMore={listData.loadMore}
          estimateItemHeight={320}
          gap={8}
          renderItem={(img) => (
            <ImageWallCard image={img} onClick={(toggleOptions) => selecting ? toggle(img.id, toggleOptions) : onNavigate({ page: "image", id: img.id })} selected={selectedIds.has(img.id)} selecting={selecting} onSelect={(toggleOptions) => toggle(img.id, toggleOptions)} />
          )}
        />
      )}
      {items.length === 0 && (
        <div className="text-center text-secondary py-16">
          <ImageIcon className="w-12 h-12 mx-auto mb-3 opacity-50" />
          <p>No images found</p>
        </div>
      )}
    </ListPage>
    <BulkEditDialog
      open={showBulkEdit}
      onClose={() => setShowBulkEdit(false)}
      title="Edit Images"
      selectedCount={selectedIds.size}
      fields={IMAGE_BULK_FIELDS}
      onApply={(values) => bulkEditMut.mutate(values)}
      isPending={bulkEditMut.isPending}
    />
    <Suspense fallback={null}>
      {lightboxOpen ? (
        <Lightbox
          images={lightboxImages}
          initialIndex={lightboxIndex}
          open={lightboxOpen}
          onClose={closeLightbox}
          slideshowDelay={config?.ui.slideshowDelay}
          autoPlay={lightboxAutoPlay}
          canEngage={canEngageImage}
          hasPrevious={!lightboxScopeIds && !infinitePageSize && lightboxPageBounds.first > 1}
          hasNext={!lightboxScopeIds && !infinitePageSize && lightboxPageBounds.last * (filter.perPage ?? 40) < totalCount}
          loadPrevious={() => loadLightboxPage(lightboxPageBounds.first - 1, "previous")}
          loadNext={() => loadLightboxPage(lightboxPageBounds.last + 1, "next")}
          wrap={lightboxScopeIds !== null || infinitePageSize}
        />
      ) : null}
      {quickViewId !== null ? (
        <QuickViewDialog type="image" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      ) : null}
    </Suspense>
    </>
  );
}

function ImageFeedCard({ image, engagement, onNavigate, canEngage, selected, onSelect, selecting }: { image: Image; engagement?: EntityEngagement; onNavigate: (route: any) => void; canEngage: boolean; selected?: boolean; onSelect?: BoundMultiSelectToggleHandler; selecting?: boolean }) {
  const displayTitle = getImageDisplayTitle(image);
  const file = image.files[0];
  const aspectRatio = file?.width && file.height ? `${file.width} / ${file.height}` : "1 / 1";
  const imageSrc = images.thumbnailUrl(image.id, 1280);
  const mediaStyle = getFeedMediaStyle(file);
  const mediaIsPortrait = Boolean(mediaStyle);
  const likeCount = engagement?.likeCount ?? 0;
  const visitCount = engagement?.pageVisitCount ?? 0;
  const visibleTags = image.tags.slice(0, 4);
  const hiddenTags = image.tags.slice(4);
  const queryClient = useQueryClient();
  const ratingMut = useMutation({
    mutationFn: (value: number | undefined) => entityEngagement.setRating("image", image.id, { value: value ?? null, aspect: "overall" }),
    onSuccess: (nextEngagement) => {
      queryClient.setQueryData(["engagement", "image", image.id], nextEngagement);
      queryClient.invalidateQueries({ queryKey: ["engagement", "image", "batch"] });
    },
  });
  const ratingValue = ratingMut.data?.rating ?? engagement?.rating;
  const openOrSelect = (toggleOptions?: MultiSelectToggleOptions) => {
    if (selecting) {
      onSelect?.(toggleOptions);
      return;
    }

    onNavigate({ page: "image", id: image.id });
  };

  const mediaOverlay = (
    <>
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <RouteCardLinkOverlay route={{ page: "image", id: image.id }} onClick={openOrSelect} label={`Open image ${displayTitle}`} disabled={selecting} selectionSafeZone />
      {!selecting && (
        <BookmarkButton
          hostType="image"
          hostId={image.id}
          compact
          deferUntilHover
          className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100"
        />
      )}
    </>
  );

  return (
    <FeedCardFrame
      dataAttribute={{ "data-feed-image-id": image.id }}
      selected={selected}
      onClick={selecting ? (event) => openOrSelect(toggleOptionsFromEvent(event)) : undefined}
      identity={image.studioName ? <FeedIdentityBadge>{image.studioName}</FeedIdentityBadge> : undefined}
      header={(
        <>
          {image.date ? <span>{image.date}</span> : null}
          {image.photographer ? <span>{image.photographer}</span> : null}
        </>
      )}
      headerActions={(
        <>
          <FeedInlineRating value={ratingValue} onChange={(value) => ratingMut.mutate(value)} readOnly={!canEngage} pending={ratingMut.isPending} />
          <FeedActionPill>
            <ThumbsUp className={["h-3.5 w-3.5", likeCount > 0 ? "fill-accent text-accent" : ""].join(" ")} />
            {likeCount}
          </FeedActionPill>
          {engagement?.isFavorite ? (
            <FeedActionPill>
              <Heart className="h-3.5 w-3.5 fill-current text-red-400" />
              Favorite
            </FeedActionPill>
          ) : null}
          <FeedActionPill>
            <Eye className="h-3.5 w-3.5" />
            {visitCount}
          </FeedActionPill>
          {image.galleryCount > 0 ? (
            <FeedActionPill>
              <FolderOpen className="h-3.5 w-3.5" />
              {image.galleryCount}
            </FeedActionPill>
          ) : null}
        </>
      )}
      media={(
        mediaIsPortrait ? (
          <FeedPortraitMediaFrame
            title={displayTitle}
            backgroundSrc={imageSrc}
            className="cursor-pointer"
            media={(
              <WallMediaCard
                title={displayTitle}
                imageSrc={imageSrc}
                fillMedia
                chromeless
                imageClassName="object-contain"
                className="h-full w-full bg-transparent"
              />
            )}
          >
            {mediaOverlay}
          </FeedPortraitMediaFrame>
        ) : (
          <WallMediaCard
            title={displayTitle}
            imageSrc={imageSrc}
            aspectRatio={aspectRatio}
            style={mediaStyle}
            className="overflow-hidden rounded-2xl border border-border/70 bg-black/95 shadow-[0_18px_40px_rgba(0,0,0,0.35)] hover:border-border/70"
          >
            {mediaOverlay}
          </WallMediaCard>
        )
      )}
      title={(
        <button
          type="button"
          onClick={(event) => { event.stopPropagation(); openOrSelect(toggleOptionsFromEvent(event)); }}
          className="text-left text-base font-semibold text-foreground transition-colors hover:text-accent"
        >
          {displayTitle}
        </button>
      )}
      details={image.details ? <p className="line-clamp-4">{image.details}</p> : undefined}
      metadata={(image.organized || image.galleries.length > 0) ? (
        <>
          {image.organized ? <FeedMetadataPill>Organized</FeedMetadataPill> : null}
          {image.galleries.length > 0 ? <FeedMetadataPill>{image.galleries.length} galleries</FeedMetadataPill> : null}
        </>
      ) : undefined}
      chips={(
        <>
          {image.performers.slice(0, 4).map((performer) => (
            <FeedChipButton
              key={performer.id}
              onClick={(event) => selecting ? onSelect?.(toggleOptionsFromEvent(event)) : onNavigate({ page: "performer", id: performer.id })}
            >
              {performer.name}
            </FeedChipButton>
          ))}
          {visibleTags.map((tag) => (
            <FeedChipButton
              key={tag.id}
              onClick={(event) => selecting ? onSelect?.(toggleOptionsFromEvent(event)) : onNavigate({ page: "tag", id: tag.id })}
            >
              #{tag.name}
            </FeedChipButton>
          ))}
          {hiddenTags.length > 0 ? (
            <FeedChipOverflowMenu>
              {hiddenTags.map((tag) => (
                <FeedChipButton
                  key={tag.id}
                  onClick={(event) => selecting ? onSelect?.(toggleOptionsFromEvent(event)) : onNavigate({ page: "tag", id: tag.id })}
                >
                  #{tag.name}
                </FeedChipButton>
              ))}
            </FeedChipOverflowMenu>
          ) : null}
        </>
      )}
    />
  );
}

function ImageWallCard({ image, onClick, selected, selecting, onSelect }: { image: Image; onClick: BoundMultiSelectToggleHandler; selected?: boolean; selecting?: boolean; onSelect?: BoundMultiSelectToggleHandler }) {
  const displayTitle = getImageDisplayTitle(image);
  const file = image.files[0];
  const aspectRatio = file?.width && file.height ? `${file.width} / ${file.height}` : "1 / 1";

  return (
    <WallMediaCard
      title={displayTitle}
      imageSrc={images.thumbnailUrl(image.id)}
      aspectRatio={aspectRatio}
      onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined}
      className={`group ${selected ? "border-accent ring-1 ring-accent/60" : ""}`.trim()}
    >
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <RouteCardLinkOverlay route={{ page: "image", id: image.id }} onClick={onClick} label={`Open image ${displayTitle}`} disabled={selecting} selectionSafeZone />
      {!selecting && (
        <BookmarkButton
          hostType="image"
          hostId={image.id}
          compact
          deferUntilHover
          className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100"
        />
      )}
    </WallMediaCard>
  );
}
