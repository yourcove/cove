import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { Dispatch, SetStateAction } from "react";
import { Building2, Check, FileAudio, FileText, Film, FolderOpen, Hash, Image as ImageIcon, Layers, Tag as TagIcon, User, Users, Volume2, VolumeX } from "lucide-react";
import { entityImages, galleries as galleryApi, images as imageApi, videos as videoApi } from "../api/client";
import type { AffinityHostType, Audio, EntityEngagement, Face, Gallery, Group, Image, Performer, Video, SegmentRecord, Studio, Tag, TagGraphNode, TextDocument } from "../api/types";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import type { Route } from "../router/location";
import { getAudioDisplayTitle, getTextDisplayTitle } from "../utils/audioTextDisplay";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import { getGalleryDisplayTitle } from "../utils/galleryDisplay";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { useWallColumns } from "../hooks/useWallColumns";
import { getEntityCardMinWidthPx } from "../hooks/useEntityCardSize";
import { useListPageCardSizeContext } from "./ListPageCardSizeContext";
import { formatDuration, formatFileSize, getResolutionLabel } from "./shared";
import type { DetailListDisplayMode } from "./DetailListToolbar";
import { getRelatedEntityDisplayModes, type RelatedEntityType } from "./relatedEntityDisplayModes";
export { getRelatedEntityDisplayModes, type RelatedEntityType } from "./relatedEntityDisplayModes";
import { AudioTile, FaceTile, GalleryTile, GroupTile, ImageTile, PerformerTile, VideoCard, SegmentTile, StudioTile, TagTile, TextTile } from "./EntityCards";
import { FeedCardFrame, FeedChipButton, FeedChipOverflowMenu, FeedIdentityBadge, FeedMetadataPill, FeedPortraitMediaFrame, getFeedMediaStyle } from "./FeedCardFrame";
import { CardSelectionToggle, RouteCardLinkOverlay } from "./RouteCardLinkOverlay";
import { VirtualizedInfiniteList } from "./VirtualizedInfiniteList";
import { VirtualizedEntityGrid, VirtualizedWallColumns, type InfiniteEntityLoadingState } from "./VirtualizedEntityLayouts";
import { WallMediaCard } from "./WallMediaCard";
import { VideoTagger } from "./VideoTagger";
import { PerformerTagger } from "./PerformerTagger";
import { StudioTagger } from "./StudioTagger";
import { TagTagger } from "./TagTagger";
import { ScraperEntityTagger } from "./ScraperEntityTagger";
import { TagGraphView } from "./TagGraphView";
import { toggleOptionsFromEvent, type MultiSelectToggleOptions } from "../hooks/useMultiSelect";

type RelatedEntityItem = Video | Image | Performer | Gallery | Studio | Tag | Group | Audio | TextDocument | SegmentRecord | Face;

const ENTITY_CARD_SIZE_TYPE: Partial<Record<RelatedEntityType, string>> = {
  videos: "videos",
  images: "images",
  performers: "performers",
  galleries: "galleries",
  studios: "studios",
  groups: "groups",
  audios: "audios",
  texts: "texts",
  segments: "segments",
  faces: "faces",
};

const ENTITY_LABELS: Record<RelatedEntityType, string> = {
  videos: "Video",
  images: "Image",
  performers: "Performer",
  galleries: "Gallery",
  studios: "Studio",
  tags: "Tag",
  groups: "Group",
  audios: "Audio",
  texts: "Text",
  segments: "Segment",
  faces: "Face",
};

// Rateable related-entity types → their AffinityHostType, so we can batch-load engagement (rating,
// favorite, play state) for the visible items and show each card's rating banner. Segments and faces
// are not rateable, so they're omitted (their engagement batch is skipped).
const RELATED_ENTITY_AFFINITY_HOST: Partial<Record<RelatedEntityType, AffinityHostType>> = {
  videos: "video",
  images: "image",
  performers: "performer",
  galleries: "gallery",
  studios: "studio",
  tags: "tag",
  groups: "group",
  audios: "audio",
  texts: "text",
};

export function useRelatedEntityDisplayMode(entityType: RelatedEntityType) {
  const availableDisplayModes = getRelatedEntityDisplayModes(entityType);
  const [displayMode, setDisplayModeState] = useState<DetailListDisplayMode>(availableDisplayModes[0]);
  const coercedDisplayMode = availableDisplayModes.includes(displayMode) ? displayMode : availableDisplayModes[0];
  const setDisplayMode = useCallback((nextMode: DetailListDisplayMode) => {
    setDisplayModeState(availableDisplayModes.includes(nextMode) ? nextMode : availableDisplayModes[0]);
  }, [availableDisplayModes]);

  return { displayMode: coercedDisplayMode, setDisplayMode, availableDisplayModes };
}

interface RelatedEntityListViewProps<TItem extends RelatedEntityItem> extends InfiniteEntityLoadingState {
  entityType: RelatedEntityType;
  items: TItem[];
  displayMode: DetailListDisplayMode;
  zoomLevel?: number;
  selectedIds?: Set<number>;
  selecting?: boolean;
  onToggle?: (id: number, options?: MultiSelectToggleOptions) => void;
  isSelectable?: (item: TItem) => boolean;
  onNavigate: (route: any) => void;
  onVideoQuickView?: (id: number) => void;
  onImageQuickView?: (id: number) => void;
  onImagePreview?: (image: Image, index: number) => void;
  onImageDetails?: (image: Image) => void;
  gap?: number;
  gapClassName?: string;
}

export function RelatedEntityListView<TItem extends RelatedEntityItem>({
  entityType,
  items,
  displayMode,
  zoomLevel,
  selectedIds,
  selecting = false,
  onToggle,
  isSelectable,
  onNavigate,
  onVideoQuickView,
  onImageQuickView,
  onImagePreview,
  onImageDetails,
  infinitePageSize,
  hasNextPage,
  isFetchingNextPage,
  loadMore,
  gap = 16,
  gapClassName = "gap-4",
}: RelatedEntityListViewProps<TItem>) {
  const effectiveDisplayMode = getRelatedEntityDisplayModes(entityType).includes(displayMode) ? displayMode : "grid";
  const listPageCardSize = useListPageCardSizeContext();
  const effectiveZoomLevel = zoomLevel ?? listPageCardSize?.zoomLevel ?? 1;
  const cardSizeEntityType = ENTITY_CARD_SIZE_TYPE[entityType];
  const minCardWidthPx = getEntityCardMinWidthPx(cardSizeEntityType, effectiveZoomLevel);
  const wallColumns = useWallColumns(items, getWallColumnCount(entityType));
  const itemIndexes = useMemo(() => new Map(items.map((item, index) => [item.id, index])), [items]);
  // Batch-load engagement for the materialized items so grid/wall tiles can show their rating banner
  // (and favorite/play state) wherever this shared view is used — every detail page and list view.
  const engagementHostType = RELATED_ENTITY_AFFINITY_HOST[entityType];
  const engagementIds = useMemo(
    () => (engagementHostType ? items.map((item) => item.id) : []),
    [engagementHostType, items],
  );
  const { engagementById } = useEntityEngagementBatch(engagementHostType ?? "video", engagementIds);
  const loadingState = { infinitePageSize, hasNextPage, isFetchingNextPage, loadMore };
  const appConfig = useOptionalAppConfig();
  const feedVideoSource = appConfig?.config?.ui.feedVideoSource ?? "preview";
  const feedVideoSound = appConfig?.config?.ui.feedVideoSound ?? false;
  const feedVideoStartPercent = appConfig?.config?.ui.feedVideoStartPercent ?? 0;
  const feedVideoStartMinDuration = appConfig?.config?.ui.feedVideoStartMinDuration ?? 0;
  const [verticalSoundEnabled, setVerticalSoundEnabled] = useState(feedVideoSound);
  const [feedAudioVideoId, setFeedAudioVideoId] = useState<number | null>(null);
  const renderItem = (item: TItem) => renderRelatedTile({ entityType, item, itemIndex: itemIndexes.get(item.id) ?? 0, engagement: engagementById.get(item.id), selectedIds, selecting, onToggle, onNavigate, onVideoQuickView, onImageQuickView, onImagePreview, onImageDetails });

  useEffect(() => {
    setVerticalSoundEnabled(feedVideoSound);
    if (!feedVideoSound) setFeedAudioVideoId(null);
  }, [feedVideoSound]);

  if (effectiveDisplayMode === "tagger") {
    return renderRelatedTagger({ entityType, items, selectedIds, selecting, onToggle, onNavigate });
  }

  if (effectiveDisplayMode === "graph" && entityType === "tags") {
    return <TagGraphView nodes={(items as Tag[]).map(toTagGraphNode)} links={[]} totalCount={items.length} onNavigate={onNavigate} selectedIds={selectedIds} onToggleSelect={onToggle} />;
  }

  if (effectiveDisplayMode === "list") {
    return <RelatedEntityListRows entityType={entityType} items={items} zoomLevel={effectiveZoomLevel} selectedIds={selectedIds} selecting={selecting} onToggle={onToggle} isSelectable={isSelectable} onNavigate={onNavigate} {...loadingState} />;
  }

  if (effectiveDisplayMode === "feed" && (entityType === "videos" || entityType === "images")) {
    return (
      <RelatedEntityFeed
        entityType={entityType}
        items={items as Array<Video | Image>}
        selectedIds={selectedIds}
        selecting={selecting}
        onToggle={onToggle}
        onNavigate={onNavigate}
        feedVideoSource={feedVideoSource}
        feedVideoSound={feedVideoSound}
        feedVideoStartPercent={feedVideoStartPercent}
        feedVideoStartMinDuration={feedVideoStartMinDuration}
        feedAudioVideoId={feedAudioVideoId}
        onFeedAudioVideoChange={setFeedAudioVideoId}
        {...loadingState}
      />
    );
  }

  if (effectiveDisplayMode === "vertical" && entityType === "videos") {
    return (
      <RelatedVideoVerticalViewer
        videos={items as Video[]}
        selectedIds={selectedIds}
        selecting={selecting}
        onToggle={onToggle}
        onNavigate={onNavigate}
        feedVideoSource={feedVideoSource}
        feedVideoStartPercent={feedVideoStartPercent}
        feedVideoStartMinDuration={feedVideoStartMinDuration}
        soundEnabled={verticalSoundEnabled}
        onToggleSound={() => setVerticalSoundEnabled((current) => !current)}
        {...loadingState}
      />
    );
  }

  if (effectiveDisplayMode === "wall") {
    return (
      <VirtualizedWallColumns
        columns={wallColumns}
        getItemKey={(item) => item.id}
        estimateItemHeight={getWallEstimateHeight(entityType)}
        {...loadingState}
        renderItem={(item) => renderRelatedWallTile({ entityType, item, selectedIds, selecting, onToggle, onNavigate }) ?? renderItem(item)}
      />
    );
  }

  return (
    <VirtualizedEntityGrid
      items={items}
      getItemKey={(item) => item.id}
      minCardWidth={`${minCardWidthPx}px`}
      virtualMinColumnWidth={minCardWidthPx}
      estimateRowHeight={getGridEstimateHeight(entityType)}
      gap={gap}
      gapClassName={gapClassName}
      {...loadingState}
      renderItem={(item) => renderItem(item)}
    />
  );
}

function renderRelatedTile<TItem extends RelatedEntityItem>({ entityType, item, itemIndex, engagement, selectedIds, selecting, onToggle, onNavigate, onVideoQuickView, onImageQuickView, onImagePreview, onImageDetails }: {
  entityType: RelatedEntityType;
  item: TItem;
  itemIndex: number;
  engagement?: EntityEngagement;
  selectedIds?: Set<number>;
  selecting: boolean;
  onToggle?: (id: number, options?: MultiSelectToggleOptions) => void;
  onNavigate: (route: any) => void;
  onVideoQuickView?: (id: number) => void;
  onImageQuickView?: (id: number) => void;
  onImagePreview?: (image: Image, index: number) => void;
  onImageDetails?: (image: Image) => void;
}) {
  const selected = selectedIds?.has(item.id) ?? false;
  const onSelect = onToggle ? (toggleOptions?: MultiSelectToggleOptions) => onToggle(item.id, toggleOptions) : undefined;
  const route = getRoute(entityType, item);
  const onClick = (toggleOptions?: MultiSelectToggleOptions) => selecting && onToggle ? onToggle(item.id, toggleOptions) : onNavigate(route);

  switch (entityType) {
    case "videos": {
      const video = item as Video;
      return <VideoCard video={video} engagement={engagement} onClick={onClick} onNavigate={onNavigate} onQuickView={onVideoQuickView ? () => onVideoQuickView(video.id) : undefined} selected={selected} onSelect={onSelect} selecting={selecting} />;
    }
    case "images": {
      const image = item as Image;
      return <ImageTile image={image} engagement={engagement} onClick={onClick} onNavigate={onNavigate} onPreview={onImagePreview ? () => selecting && onToggle ? onToggle(image.id) : onImagePreview(image, itemIndex) : undefined} onDetails={onImageDetails ? () => selecting && onToggle ? onToggle(image.id) : onImageDetails(image) : undefined} onQuickView={onImageQuickView ? () => onImageQuickView(image.id) : undefined} selected={selected} onSelect={onSelect} selecting={selecting} />;
    }
    case "performers": return <PerformerTile performer={item as Performer} engagement={engagement} onClick={onClick} onNavigate={onNavigate} selected={selected} onSelect={onSelect} selecting={selecting} />;
    case "galleries": return <GalleryTile gallery={item as Gallery} engagement={engagement} onClick={onClick} onNavigate={onNavigate} selected={selected} onSelect={onSelect} selecting={selecting} />;
    case "studios": return <StudioTile studio={item as Studio} engagement={engagement} onClick={onClick} onNavigate={onNavigate} selected={selected} onSelect={onSelect} selecting={selecting} />;
    case "tags": return <TagTile tag={item as Tag} engagement={engagement} onClick={onClick} onNavigate={onNavigate} selected={selected} onSelect={onSelect} selecting={selecting} />;
    case "groups": return <GroupTile group={item as Group} engagement={engagement} onClick={onClick} onNavigate={onNavigate} selected={selected} onSelect={onSelect} selecting={selecting} />;
    case "audios": return <AudioTile audio={item as Audio} engagement={engagement} onClick={onClick} onNavigate={onNavigate} selected={selected} onSelect={onSelect} selecting={selecting} />;
    case "texts": return <TextTile text={item as TextDocument} engagement={engagement} onClick={onClick} onNavigate={onNavigate} selected={selected} onSelect={onSelect} selecting={selecting} />;
    case "segments": return <SegmentTile segment={item as SegmentRecord} onClick={onClick} route={route} selected={selected} onSelect={onSelect} selecting={selecting} />;
    case "faces": return <FaceTile face={item as Face} onClick={onClick} selected={selected} onSelect={onSelect} selecting={selecting} />;
  }
}

export function RelatedEntityListRows<TItem extends RelatedEntityItem>({ entityType, items, zoomLevel, selectedIds, selecting, onToggle, isSelectable, onNavigate, infinitePageSize, hasNextPage, isFetchingNextPage, loadMore }: {
  entityType: RelatedEntityType;
  items: TItem[];
  zoomLevel?: number;
  selectedIds?: Set<number>;
  selecting: boolean;
  onToggle?: (id: number, options?: MultiSelectToggleOptions) => void;
  isSelectable?: (item: TItem) => boolean;
  onNavigate: (route: any) => void;
} & InfiniteEntityLoadingState) {
  const listPageCardSize = useListPageCardSizeContext();
  const density = getRelatedListDensity(zoomLevel ?? listPageCardSize?.zoomLevel ?? 1);
  const canSelect = isSelectable ?? (() => true);
  const renderRow = (item: TItem) => (
    <RelatedEntityListRow
      key={item.id}
      entityType={entityType}
      item={item}
      density={density}
      selected={selectedIds?.has(item.id) ?? false}
      selecting={selecting}
      onToggle={onToggle}
      selectable={canSelect(item)}
      onNavigate={onNavigate}
    />
  );

  if (infinitePageSize && items.length > 0) {
    return (
      <div className="mx-auto w-full max-w-7xl px-2">
        <VirtualizedInfiniteList
          items={items}
          getItemKey={(item) => item.id}
          estimateSize={density.estimateSize}
          overscan={6}
          hasNextPage={Boolean(hasNextPage)}
          isFetchingNextPage={Boolean(isFetchingNextPage)}
          loadMore={loadMore ?? noop}
          itemClassName={density.itemGapClassName}
          renderItem={({ item }) => renderRow(item)}
        />
      </div>
    );
  }

  return <div className={`mx-auto flex w-full max-w-7xl flex-col px-2 ${density.containerGapClassName}`}>{items.map(renderRow)}</div>;
}

export function RelatedEntityListRow<TItem extends RelatedEntityItem>({ entityType, item, density, selected, selecting, onToggle, selectable = true, onNavigate }: {
  entityType: RelatedEntityType;
  item: TItem;
  density?: RelatedListDensity;
  selected?: boolean;
  selecting?: boolean;
  onToggle?: (id: number, options?: MultiSelectToggleOptions) => void;
  selectable?: boolean;
  onNavigate: (route: any) => void;
}) {
  const listPageCardSize = useListPageCardSizeContext();
  const resolvedDensity = density ?? getRelatedListDensity(listPageCardSize?.zoomLevel ?? 1);
  const route = getRoute(entityType, item);
  const onClick = (toggleOptions?: MultiSelectToggleOptions) => selecting && selectable && onToggle ? onToggle(item.id, toggleOptions) : onNavigate(route);
  const stats = getRelatedStats(entityType, item);
  const title = getRelatedTitle(entityType, item);
  const subtitle = getRelatedSubtitle(entityType, item);
  const description = resolvedDensity.showDescription ? getRelatedDescription(entityType, item) : undefined;

  return (
    <article
      className={`group flex w-full items-stretch ${resolvedDensity.rowGapClassName} rounded-lg border border-border/70 bg-card/70 ${resolvedDensity.rowPaddingClassName} text-left shadow-sm shadow-black/10 transition-colors hover:border-accent/45 hover:bg-card ${selected ? "border-accent/70 bg-accent/10 ring-1 ring-accent/35" : ""}`}
      style={{ minHeight: resolvedDensity.minHeight }}
      onClick={(event) => onClick(toggleOptionsFromEvent(event))}
    >
      {onToggle && selectable ? (
        <button
          type="button"
          onClick={(event) => { event.stopPropagation(); onToggle(item.id, toggleOptionsFromEvent(event)); }}
          className={`mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded border text-[10px] transition-colors ${selected ? "border-accent bg-accent text-white" : selecting ? "border-accent/60 text-transparent hover:text-accent" : "border-border text-transparent hover:border-accent hover:text-accent"}`}
          aria-label={selected ? `Deselect ${ENTITY_LABELS[entityType].toLowerCase()}` : `Select ${ENTITY_LABELS[entityType].toLowerCase()}`}
        >
          {selected ? <Check className="h-3 w-3" /> : null}
        </button>
      ) : null}

      {resolvedDensity.showImage ? <RelatedThumbnail entityType={entityType} item={item} density={resolvedDensity} /> : null}

      <button type="button" onClick={(event) => { event.stopPropagation(); onClick(toggleOptionsFromEvent(event)); }} className="flex min-w-0 flex-1 flex-col justify-center text-left">
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
          <h3 className={`min-w-0 truncate font-semibold text-foreground transition-colors group-hover:text-accent ${resolvedDensity.titleClassName}`}>{title}</h3>
          {resolvedDensity.showBadges ? getRelatedStatusBadge(entityType, item) : null}
        </div>
        {resolvedDensity.showSubtitle ? <p className="mt-1 truncate text-xs text-secondary">{subtitle}</p> : null}
        {description ? <p className="mt-1 line-clamp-2 text-xs leading-5 text-muted">{description}</p> : null}
        {resolvedDensity.showStats && stats.length > 0 ? (
          <div className="mt-2 flex flex-wrap gap-1.5 text-[11px] text-muted">
            {stats.map((stat) => <span key={stat} className="rounded-full border border-border/70 bg-background/55 px-2 py-0.5">{stat}</span>)}
          </div>
        ) : null}
      </button>
    </article>
  );
}

function renderRelatedTagger<TItem extends RelatedEntityItem>({ entityType, items, selectedIds, selecting, onToggle, onNavigate }: {
  entityType: RelatedEntityType;
  items: TItem[];
  selectedIds?: Set<number>;
  selecting: boolean;
  onToggle?: (id: number, options?: MultiSelectToggleOptions) => void;
  onNavigate: (route: any) => void;
}) {
  switch (entityType) {
    case "videos": return <VideoTagger videos={items as Video[]} selectedIds={selectedIds} selecting={selecting} onSelect={onToggle} onNavigate={(videoId) => onNavigate({ page: "video", id: videoId })} />;
    case "performers": return <PerformerTagger performers={items as Performer[]} selectedIds={selectedIds} selecting={selecting} onSelect={onToggle} onNavigate={(performerId) => onNavigate({ page: "performer", id: performerId })} />;
    case "studios": return <StudioTagger studios={items as Studio[]} selectedIds={selectedIds} selecting={selecting} onSelect={onToggle} />;
    case "tags": return <TagTagger tags={items as Tag[]} selectedIds={selectedIds} selecting={selecting} onSelect={onToggle} />;
    case "images":
    case "galleries":
    case "groups":
    case "audios":
    case "texts":
      return (
        <ScraperEntityTagger
          entityType={getScraperEntityType(entityType)}
          label={ENTITY_LABELS[entityType]}
          items={items as Array<Image | Gallery | Group | Audio | TextDocument>}
          selectedIds={selectedIds}
          selecting={selecting}
          onSelect={onToggle}
          getTitle={(item) => getRelatedTitle(entityType, item as RelatedEntityItem)}
          getImageUrl={(item) => getRelatedImageUrl(entityType, item as RelatedEntityItem)}
          getRoute={(item) => getRoute(entityType, item as RelatedEntityItem)}
          queryKey={`related-${entityType}`}
        />
      );
    default:
      return null;
  }
}

function getScraperEntityType(entityType: RelatedEntityType) {
  if (entityType === "audios") return "audio";
  if (entityType === "texts") return "text";
  if (entityType === "images") return "image";
  if (entityType === "galleries") return "gallery";
  return "group";
}

function getRoute(entityType: RelatedEntityType, item: RelatedEntityItem): Route {
  const page = entityType === "videos" ? "video"
    : entityType === "images" ? "image"
      : entityType === "performers" ? "performer"
        : entityType === "galleries" ? "gallery"
          : entityType === "studios" ? "studio"
            : entityType === "tags" ? "tag"
              : entityType === "groups" ? "group"
                : entityType === "audios" ? "audio"
                  : entityType === "texts" ? "text"
                    : entityType === "segments" ? "segment"
                      : "face";
  return { page, id: item.id };
}

function getRelatedTitle(entityType: RelatedEntityType, item: RelatedEntityItem) {
  switch (entityType) {
    case "videos": {
      const video = item as Video;
      return video.title || video.files?.[0]?.basename || `Video ${video.id}`;
    }
    case "images": return getImageDisplayTitle(item as Image);
    case "performers": return (item as Performer).name || `Performer ${item.id}`;
    case "galleries": return (item as Gallery).title || `Gallery ${item.id}`;
    case "studios": return (item as Studio).name || `Studio ${item.id}`;
    case "tags": return (item as Tag).name || `Tag ${item.id}`;
    case "groups": return (item as Group).name || `Group ${item.id}`;
    case "audios": return getAudioDisplayTitle(item as Audio);
    case "texts": return getTextDisplayTitle(item as TextDocument);
    case "segments": {
      const segment = item as SegmentRecord;
      return segment.title || segment.tagName || segment.refLabel || `Segment ${segment.id}`;
    }
    case "faces": {
      const face = item as Face;
      return face.label || face.performerName || `Face ${face.id}`;
    }
  }
}

function getRelatedSubtitle(entityType: RelatedEntityType, item: RelatedEntityItem) {
  switch (entityType) {
    case "videos": {
      const video = item as Video;
      const file = video.files?.[0];
      const duration = typeof video.clipStartSec === "number" && typeof video.clipEndSec === "number" ? Math.max(0, video.clipEndSec - video.clipStartSec) : file?.duration;
      return [video.studioName, file ? getResolutionLabel(file.width, file.height) : null, duration != null ? formatDuration(duration) : null].filter(Boolean).join(" · ") || "Video";
    }
    case "images": {
      const image = item as Image;
      const file = image.files?.[0];
      return [image.studioName, image.photographer, file ? getResolutionLabel(file.width, file.height) : null].filter(Boolean).join(" · ") || "Image";
    }
    case "performers": {
      const performer = item as Performer;
      return [performer.disambiguation, performer.gender, performer.country].filter(Boolean).join(" · ") || "Performer";
    }
    case "galleries": {
      const gallery = item as Gallery;
      return [gallery.studioName, gallery.date, gallery.imageCount != null ? `${gallery.imageCount} images` : null].filter(Boolean).join(" · ") || "Gallery";
    }
    case "studios": {
      const studio = item as Studio;
      return [studio.parentName, studio.videoCount != null ? `${studio.videoCount} videos` : null].filter(Boolean).join(" · ") || "Studio";
    }
    case "tags": return (item as Tag).tagGroupName || "";
    case "groups": {
      const group = item as Group;
      return [group.kind, group.itemCount != null ? `${group.itemCount} items` : null].filter(Boolean).join(" · ") || "Group";
    }
    case "audios": {
      const audio = item as Audio;
      return [audio.studioName, audio.date, audio.maxDuration != null ? formatDuration(audio.maxDuration) : null].filter(Boolean).join(" · ") || "Audio";
    }
    case "texts": {
      const text = item as TextDocument;
      return [text.studioName, text.date, text.maxWordCount != null ? `${text.maxWordCount.toLocaleString()} words` : null].filter(Boolean).join(" · ") || "Text";
    }
    case "segments": {
      const segment = item as SegmentRecord;
      return [segment.hostTitle, formatDuration(Math.max(0, (segment.endSec ?? segment.startSec) - segment.startSec)), segment.confidence != null ? `${Math.round(segment.confidence * 100)}%` : null].filter(Boolean).join(" · ") || "Segment";
    }
    case "faces": {
      const face = item as Face;
      return [`${face.appearanceCount} appearances`, `${face.videoCount} videos`, `${face.imageCount} images`].join(" · ");
    }
  }
}

function getRelatedImageUrl(entityType: RelatedEntityType, item: RelatedEntityItem) {
  switch (entityType) {
    case "images": return imageApi.thumbnailUrl((item as Image).id, 320);
    case "galleries": return (item as Gallery).coverPath ?? undefined;
    case "groups": return (item as Group).frontImagePath ?? (item as Group).backImagePath ?? undefined;
    case "audios": return (item as Audio).imagePath ?? undefined;
    case "texts": return (item as TextDocument).imagePath ?? undefined;
    default: return undefined;
  }
}

function getGridEstimateHeight(entityType: RelatedEntityType) {
  if (entityType === "audios" || entityType === "texts") return 220;
  if (entityType === "images" || entityType === "performers") return 260;
  if (entityType === "galleries" || entityType === "studios" || entityType === "groups" || entityType === "segments") return 280;
  if (entityType === "faces") return 360;
  return 320;
}

function getWallEstimateHeight(entityType: RelatedEntityType) {
  if (entityType === "images") return 240;
  if (entityType === "performers") return 300;
  if (entityType === "galleries") return 260;
  return getGridEstimateHeight(entityType);
}

function getWallColumnCount(entityType: RelatedEntityType) {
  if (entityType === "images" || entityType === "performers") return 6;
  return 5;
}

function toTagGraphNode(tag: Tag): TagGraphNode {
  return {
    id: tag.id,
    name: tag.name,
    favorite: tag.favorite,
    description: tag.description,
    imagePath: tag.imagePath,
    tagGroupId: tag.tagGroupId ?? undefined,
    tagGroupName: tag.tagGroupName ?? undefined,
    tagGroupColor: tag.tagGroupColor ?? undefined,
    parentIds: [],
    childIds: [],
    totalUsageCount: (tag.videoCount ?? 0) + (tag.segmentCount ?? 0) + (tag.imageCount ?? 0) + (tag.galleryCount ?? 0) + (tag.groupCount ?? 0) + (tag.performerCount ?? 0) + (tag.studioCount ?? 0),
    videoCount: tag.videoCount ?? 0,
    segmentCount: tag.segmentCount ?? 0,
    imageCount: tag.imageCount ?? 0,
    galleryCount: tag.galleryCount ?? 0,
    groupCount: tag.groupCount ?? 0,
    performerCount: tag.performerCount ?? 0,
    studioCount: tag.studioCount ?? 0,
  };
}

function renderRelatedWallTile<TItem extends RelatedEntityItem>({ entityType, item, selectedIds, selecting, onToggle, onNavigate }: {
  entityType: RelatedEntityType;
  item: TItem;
  selectedIds?: Set<number>;
  selecting: boolean;
  onToggle?: (id: number, options?: MultiSelectToggleOptions) => void;
  onNavigate: (route: any) => void;
}) {
  const selected = selectedIds?.has(item.id) ?? false;
  const onSelect = onToggle ? (toggleOptions?: MultiSelectToggleOptions) => onToggle(item.id, toggleOptions) : undefined;
  const onClick = (toggleOptions?: MultiSelectToggleOptions) => selecting && onToggle ? onToggle(item.id, toggleOptions) : onNavigate(getRoute(entityType, item));

  switch (entityType) {
    case "videos": return <RelatedVideoWallCard video={item as Video} selected={selected} selecting={selecting} onSelect={onSelect} onClick={onClick} />;
    case "images": return <RelatedImageWallCard image={item as Image} selected={selected} selecting={selecting} onSelect={onSelect} onClick={onClick} />;
    case "performers": return <RelatedPortraitWallCard entityType="performers" item={item as Performer} selected={selected} selecting={selecting} onSelect={onSelect} onClick={onClick} />;
    case "galleries": return <RelatedGalleryWallCard gallery={item as Gallery} selected={selected} selecting={selecting} onSelect={onSelect} onClick={onClick} />;
    default: return null;
  }
}

function RelatedVideoWallCard({ video, selected, selecting, onSelect, onClick }: { video: Video; selected?: boolean; selecting?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; onClick: (options?: MultiSelectToggleOptions) => void }) {
  const file = video.files[0];
  const title = video.title || file?.basename || `Video ${video.id}`;
  const coverUrl = entityImages.videoCoverUrl(video.id, video.updatedAt, 1280);
  const coverAlt = video.imagePath ? title : "";
  const appConfig = useOptionalAppConfig();
  const wallPreviewType = appConfig?.config?.ui.wallPreviewType ?? "video";
  const showTitle = appConfig?.config?.ui.wallShowTitle ?? true;
  const duration = getVideoDisplayDuration(video);

  return (
    <WallMediaCard
      title={title}
      imageSrc={coverUrl}
      imageAlt={coverAlt}
      videoSrc={videoApi.previewUrl(video.id)}
      videoStatusSrc={videoApi.previewStatusUrl(video.id)}
      useVideo={wallPreviewType === "video" || wallPreviewType === "webp"}
      muted
      aspectRatio={file?.width && file.height ? `${file.width} / ${file.height}` : "16 / 9"}
      imageClassName="object-cover"
      onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined}
      className={`group ${selected ? "border-accent ring-1 ring-accent/60" : ""}`.trim()}
    >
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <RouteCardLinkOverlay route={{ page: "video", id: video.id }} onClick={onClick} label={`Open video ${title}`} disabled={selecting} selectionSafeZone />
      <div className={`absolute inset-0 bg-gradient-to-t from-black/65 via-transparent to-transparent transition-opacity ${showTitle ? "opacity-0 group-hover:opacity-100" : "opacity-0"}`} />
      {showTitle ? <div className="absolute inset-x-0 bottom-0 p-2 opacity-0 transition-opacity group-hover:opacity-100"><p className="truncate text-xs font-medium text-white">{title}</p></div> : null}
      {duration > 0 ? <span className="absolute right-1 top-1 rounded bg-black/70 px-1 text-xs text-white">{formatDuration(duration)}</span> : null}
    </WallMediaCard>
  );
}

function RelatedImageWallCard({ image, selected, selecting, onSelect, onClick }: { image: Image; selected?: boolean; selecting?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; onClick: (options?: MultiSelectToggleOptions) => void }) {
  const title = getImageDisplayTitle(image);
  const file = image.files[0];

  return (
    <WallMediaCard
      title={title}
      imageSrc={imageApi.thumbnailUrl(image.id)}
      aspectRatio={file?.width && file.height ? `${file.width} / ${file.height}` : "1 / 1"}
      onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined}
      className={`group ${selected ? "border-accent ring-1 ring-accent/60" : ""}`.trim()}
    >
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <RouteCardLinkOverlay route={{ page: "image", id: image.id }} onClick={onClick} label={`Open image ${title}`} disabled={selecting} selectionSafeZone />
    </WallMediaCard>
  );
}

function RelatedPortraitWallCard({ entityType, item, selected, selecting, onSelect, onClick }: { entityType: RelatedEntityType; item: RelatedEntityItem; selected?: boolean; selecting?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; onClick: (options?: MultiSelectToggleOptions) => void }) {
  const title = getRelatedTitle(entityType, item);
  const imageSrc = getRelatedThumbnailUrl(entityType, item, 640);

  return (
    <WallMediaCard title={title} imageSrc={imageSrc} aspectRatio="2 / 3" onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined} className={`group ${selected ? "border-accent ring-1 ring-accent/60" : ""}`.trim()} fallback={<User className="h-12 w-12 text-muted" />}>
      <RouteCardLinkOverlay route={getRoute(entityType, item)} onClick={onClick} label={`Open ${title}`} disabled={selecting} selectionSafeZone />
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <div className="selection-safe-zone absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/85 to-transparent p-2 text-xs font-medium text-white">{title}</div>
    </WallMediaCard>
  );
}

function RelatedGalleryWallCard({ gallery, selected, selecting, onSelect, onClick }: { gallery: Gallery; selected?: boolean; selecting?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; onClick: (options?: MultiSelectToggleOptions) => void }) {
  const title = getGalleryDisplayTitle(gallery);
  const imageSrc = gallery.coverPath ?? galleryApi.coverUrl(gallery.id, gallery.updatedAt, 960);

  return (
    <WallMediaCard title={title} imageSrc={imageSrc} aspectRatio="1 / 1" onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined} className={`group ${selected ? "border-accent ring-1 ring-accent/60" : ""}`.trim()} fallback={<FolderOpen className="h-10 w-10 text-muted" />}>
      <RouteCardLinkOverlay route={{ page: "gallery", id: gallery.id }} onClick={onClick} label={`Open gallery ${title}`} disabled={selecting} selectionSafeZone />
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/85 to-transparent p-2 text-xs font-medium text-white">
        <p className="truncate">{title}</p>
        <p className="mt-0.5 text-[11px] text-white/75">{[gallery.imageCount ? `${gallery.imageCount} images` : null, gallery.videoCount ? `${gallery.videoCount} videos` : null].filter(Boolean).join(" · ")}</p>
      </div>
    </WallMediaCard>
  );
}

function RelatedEntityFeed({ entityType, items, selectedIds, selecting, onToggle, onNavigate, feedVideoSource, feedVideoSound, feedVideoStartPercent, feedVideoStartMinDuration, feedAudioVideoId, onFeedAudioVideoChange, infinitePageSize, hasNextPage, isFetchingNextPage, loadMore }: {
  entityType: "videos" | "images";
  items: Array<Video | Image>;
  selectedIds?: Set<number>;
  selecting: boolean;
  onToggle?: (id: number, options?: MultiSelectToggleOptions) => void;
  onNavigate: (route: any) => void;
  feedVideoSource: string;
  feedVideoSound: boolean;
  feedVideoStartPercent: number;
  feedVideoStartMinDuration: number;
  feedAudioVideoId: number | null;
  onFeedAudioVideoChange: Dispatch<SetStateAction<number | null>>;
} & InfiniteEntityLoadingState) {
  const renderFeedItem = (item: Video | Image) => entityType === "videos"
    ? <RelatedVideoFeedCard video={item as Video} selected={selectedIds?.has(item.id) ?? false} selecting={selecting} onSelect={onToggle ? (toggleOptions?: MultiSelectToggleOptions) => onToggle(item.id, toggleOptions) : undefined} onNavigate={onNavigate} feedVideoSource={feedVideoSource} feedVideoStartPercent={feedVideoStartPercent} feedVideoStartMinDuration={feedVideoStartMinDuration} soundEnabled={feedAudioVideoId === item.id} onPlaybackEligibilityChange={feedVideoSound ? (eligible) => onFeedAudioVideoChange((current) => eligible ? item.id : current === item.id ? null : current) : undefined} />
    : <RelatedImageFeedCard image={item as Image} selected={selectedIds?.has(item.id) ?? false} selecting={selecting} onSelect={onToggle ? (toggleOptions?: MultiSelectToggleOptions) => onToggle(item.id, toggleOptions) : undefined} onNavigate={onNavigate} />;

  if (infinitePageSize && items.length > 0) {
    return (
      <div className="mx-auto w-full max-w-[64rem] px-3 sm:px-4">
        <VirtualizedInfiniteList
          items={items}
          getItemKey={(item) => item.id}
          estimateSize={760}
          overscan={2}
          hasNextPage={Boolean(hasNextPage)}
          isFetchingNextPage={Boolean(isFetchingNextPage)}
          loadMore={loadMore ?? noop}
          itemClassName="pb-5"
          renderItem={({ item }) => renderFeedItem(item)}
        />
      </div>
    );
  }

  return <div className="mx-auto w-full max-w-[64rem] space-y-5 px-3 sm:px-4">{items.map((item) => <div key={item.id}>{renderFeedItem(item)}</div>)}</div>;
}

function RelatedVideoFeedCard({ video, selected, selecting, onSelect, onNavigate, feedVideoSource, feedVideoStartPercent, feedVideoStartMinDuration, soundEnabled, onPlaybackEligibilityChange }: { video: Video; selected?: boolean; selecting?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; onNavigate: (route: any) => void; feedVideoSource: string; feedVideoStartPercent: number; feedVideoStartMinDuration: number; soundEnabled: boolean; onPlaybackEligibilityChange?: (eligible: boolean) => void }) {
  const file = video.files[0];
  const title = video.title || file?.basename || `Video ${video.id}`;
  const coverAlt = video.imagePath ? title : "";
  const duration = getVideoDisplayDuration(video);
  const { coverUrl, videoSrc, videoStatusSrc } = getVideoFeedMedia(video, feedVideoSource);
  const mediaStyle = getFeedMediaStyle(file);
  const videoStartTimeSec = getVideoFeedVideoStartTime(video, feedVideoSource, feedVideoStartPercent, feedVideoStartMinDuration);
  const openOrSelect = (toggleOptions?: MultiSelectToggleOptions) => selecting ? onSelect?.(toggleOptions) : onNavigate({ page: "video", id: video.id });
  const mediaOverlay = (
    <>
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <RouteCardLinkOverlay route={{ page: "video", id: video.id }} onClick={openOrSelect} label={`Open video ${title}`} disabled={selecting} selectionSafeZone />
    </>
  );

  return (
    <FeedCardFrame
      dataAttribute={{ "data-feed-video-id": video.id }}
      selected={selected}
      onClick={selecting ? (event) => openOrSelect(toggleOptionsFromEvent(event)) : undefined}
      identity={video.studioName ? <FeedIdentityBadge>{video.studioName}</FeedIdentityBadge> : undefined}
      header={<>{video.date ? <span>{video.date}</span> : null}{duration > 0 ? <span>{formatDuration(duration)}</span> : null}</>}
      media={mediaStyle ? (
        <FeedPortraitMediaFrame title={title} backgroundSrc={coverUrl} className="cursor-pointer" media={<WallMediaCard title={title} imageSrc={coverUrl} imageAlt={coverAlt} videoSrc={videoSrc} videoStatusSrc={videoStatusSrc} useVideo muted={!soundEnabled} videoStartTimeSec={videoStartTimeSec} videoPlayThreshold={0.5} onVideoPlayEligibilityChange={onPlaybackEligibilityChange} fillMedia chromeless imageClassName="object-contain" videoClassName="object-contain" className="h-full w-full bg-transparent" />}>{mediaOverlay}</FeedPortraitMediaFrame>
      ) : (
        <WallMediaCard title={title} imageSrc={coverUrl} imageAlt={coverAlt} videoSrc={videoSrc} videoStatusSrc={videoStatusSrc} useVideo muted={!soundEnabled} videoStartTimeSec={videoStartTimeSec} videoPlayThreshold={0.5} onVideoPlayEligibilityChange={onPlaybackEligibilityChange} playbackTracking={{ hostType: "video", hostId: video.id, surface: "feed", scopeKey: `related-video-feed:${video.id}` }} aspectRatio={file?.width && file.height ? `${file.width} / ${file.height}` : "16 / 9"} imageClassName="object-cover" style={mediaStyle} className="overflow-hidden rounded-2xl border border-border/70 bg-black/95 shadow-[0_18px_40px_rgba(0,0,0,0.35)] hover:border-border/70">{mediaOverlay}</WallMediaCard>
      )}
      title={<button type="button" onClick={(event) => { event.stopPropagation(); openOrSelect(toggleOptionsFromEvent(event)); }} className="text-left text-base font-semibold text-foreground transition-colors hover:text-accent">{title}</button>}
      details={video.details ? <p className="line-clamp-4">{video.details}</p> : undefined}
      metadata={(video.organized || video.galleries.length > 0) ? <>{video.organized ? <FeedMetadataPill>Organized</FeedMetadataPill> : null}{video.galleries.length > 0 ? <FeedMetadataPill>{video.galleries.length} galleries</FeedMetadataPill> : null}</> : undefined}
      chips={<RelatedFeedChips performers={video.performers} tags={video.tags} selecting={selecting} onSelect={onSelect} onNavigate={onNavigate} />}
    />
  );
}

function RelatedImageFeedCard({ image, selected, selecting, onSelect, onNavigate }: { image: Image; selected?: boolean; selecting?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; onNavigate: (route: any) => void }) {
  const title = getImageDisplayTitle(image);
  const file = image.files[0];
  const imageSrc = imageApi.thumbnailUrl(image.id, 1280);
  const mediaStyle = getFeedMediaStyle(file);
  const openOrSelect = (toggleOptions?: MultiSelectToggleOptions) => selecting ? onSelect?.(toggleOptions) : onNavigate({ page: "image", id: image.id });
  const mediaOverlay = (
    <>
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <RouteCardLinkOverlay route={{ page: "image", id: image.id }} onClick={openOrSelect} label={`Open image ${title}`} disabled={selecting} selectionSafeZone />
    </>
  );

  return (
    <FeedCardFrame
      dataAttribute={{ "data-feed-image-id": image.id }}
      selected={selected}
      onClick={selecting ? (event) => openOrSelect(toggleOptionsFromEvent(event)) : undefined}
      identity={image.studioName ? <FeedIdentityBadge>{image.studioName}</FeedIdentityBadge> : undefined}
      header={<>{image.date ? <span>{image.date}</span> : null}{image.photographer ? <span>{image.photographer}</span> : null}</>}
      media={mediaStyle ? (
        <FeedPortraitMediaFrame title={title} backgroundSrc={imageSrc} className="cursor-pointer" media={<WallMediaCard title={title} imageSrc={imageSrc} fillMedia chromeless imageClassName="object-contain" className="h-full w-full bg-transparent" />}>{mediaOverlay}</FeedPortraitMediaFrame>
      ) : (
        <WallMediaCard title={title} imageSrc={imageSrc} aspectRatio={file?.width && file.height ? `${file.width} / ${file.height}` : "1 / 1"} style={mediaStyle} className="overflow-hidden rounded-2xl border border-border/70 bg-black/95 shadow-[0_18px_40px_rgba(0,0,0,0.35)] hover:border-border/70">{mediaOverlay}</WallMediaCard>
      )}
      title={<button type="button" onClick={(event) => { event.stopPropagation(); openOrSelect(toggleOptionsFromEvent(event)); }} className="text-left text-base font-semibold text-foreground transition-colors hover:text-accent">{title}</button>}
      details={image.details ? <p className="line-clamp-4">{image.details}</p> : undefined}
      metadata={(image.organized || image.galleries.length > 0) ? <>{image.organized ? <FeedMetadataPill>Organized</FeedMetadataPill> : null}{image.galleries.length > 0 ? <FeedMetadataPill>{image.galleries.length} galleries</FeedMetadataPill> : null}</> : undefined}
      chips={<RelatedFeedChips performers={image.performers} tags={image.tags} selecting={selecting} onSelect={onSelect} onNavigate={onNavigate} />}
    />
  );
}

function RelatedFeedChips({ performers, tags, selecting, onSelect, onNavigate }: { performers: Array<{ id: number; name: string }>; tags: Tag[]; selecting?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; onNavigate: (route: any) => void }) {
  const visibleTags = tags.slice(0, 4);
  const hiddenTags = tags.slice(4);
  return (
    <>
      {performers.slice(0, 4).map((performer) => <FeedChipButton key={`performer-${performer.id}`} onClick={(event) => selecting ? onSelect?.(toggleOptionsFromEvent(event)) : onNavigate({ page: "performer", id: performer.id })}>{performer.name}</FeedChipButton>)}
      {visibleTags.map((tag) => <FeedChipButton key={`tag-${tag.id}`} onClick={(event) => selecting ? onSelect?.(toggleOptionsFromEvent(event)) : onNavigate({ page: "tag", id: tag.id })}>#{tag.name}</FeedChipButton>)}
      {hiddenTags.length > 0 ? <FeedChipOverflowMenu>{hiddenTags.map((tag) => <FeedChipButton key={tag.id} onClick={(event) => selecting ? onSelect?.(toggleOptionsFromEvent(event)) : onNavigate({ page: "tag", id: tag.id })}>#{tag.name}</FeedChipButton>)}</FeedChipOverflowMenu> : null}
    </>
  );
}

function RelatedVideoVerticalViewer({ videos, selectedIds, selecting, onToggle, onNavigate, feedVideoSource, feedVideoStartPercent, feedVideoStartMinDuration, soundEnabled, onToggleSound, hasNextPage, isFetchingNextPage, loadMore }: {
  videos: Video[];
  selectedIds?: Set<number>;
  selecting: boolean;
  onToggle?: (id: number, options?: MultiSelectToggleOptions) => void;
  onNavigate: (route: any) => void;
  feedVideoSource: string;
  feedVideoStartPercent: number;
  feedVideoStartMinDuration: number;
  soundEnabled: boolean;
  onToggleSound: () => void;
} & InfiniteEntityLoadingState) {
  const viewerRef = useRef<HTMLDivElement | null>(null);
  const [viewerHeight, setViewerHeight] = useState<number | null>(null);
  const [activeVideoId, setActiveVideoId] = useState<number | null>(videos[0]?.id ?? null);
  const activeIndex = videos.findIndex((video) => video.id === activeVideoId);
  const itemHeight = Math.max(420, viewerHeight ?? 720);

  useEffect(() => {
    if (videos.length === 0) {
      setActiveVideoId(null);
      return;
    }
    if (!videos.some((video) => video.id === activeVideoId)) {
      setActiveVideoId(videos[0].id);
    }
  }, [activeVideoId, videos]);

  useEffect(() => {
    const updateViewerHeight = () => {
      if (typeof window === "undefined") return;
      setViewerHeight(Math.floor(Math.max(640, window.innerHeight - 48)));
    };

    updateViewerHeight();
    window.addEventListener("resize", updateViewerHeight);
    const observer = typeof ResizeObserver !== "undefined" ? new ResizeObserver(updateViewerHeight) : null;
    if (observer && viewerRef.current) observer.observe(viewerRef.current);
    return () => {
      window.removeEventListener("resize", updateViewerHeight);
      observer?.disconnect();
    };
  }, []);

  if (videos.length === 0) return null;

  return (
    <div
      ref={viewerRef}
      style={{ height: viewerHeight != null ? `${viewerHeight}px` : "calc(100dvh - 10rem)" }}
      className="relative -mx-2 snap-y snap-mandatory overflow-y-auto bg-black px-0 py-0 sm:-mx-3 md:-mx-4"
    >
      <VirtualizedInfiniteList
        items={videos}
        getItemKey={(video) => video.id}
        estimateSize={itemHeight}
        overscan={2}
        hasNextPage={Boolean(hasNextPage)}
        isFetchingNextPage={Boolean(isFetchingNextPage)}
        loadMore={loadMore ?? noop}
        scrollElementRef={viewerRef}
        onActiveIndexChange={(idx) => setActiveVideoId(idx == null ? null : videos[idx]?.id ?? null)}
        itemClassName="snap-start"
        renderItem={({ item: video, index }) => (
          <RelatedVideoVerticalCard
            video={video}
            selected={selectedIds?.has(video.id) ?? false}
            selecting={selecting}
            onSelect={onToggle ? (toggleOptions?: MultiSelectToggleOptions) => onToggle(video.id, toggleOptions) : undefined}
            onNavigate={onNavigate}
            feedVideoSource={feedVideoSource}
            feedVideoStartPercent={feedVideoStartPercent}
            feedVideoStartMinDuration={feedVideoStartMinDuration}
            useVideo={activeIndex < 0 ? index === 0 : Math.abs(index - activeIndex) <= 1}
            soundEnabled={soundEnabled && video.id === activeVideoId}
            onToggleSound={onToggleSound}
            viewerHeight={viewerHeight}
          />
        )}
      />
    </div>
  );
}

function RelatedVideoVerticalCard({ video, selected, selecting, onSelect, onNavigate, feedVideoSource, feedVideoStartPercent, feedVideoStartMinDuration, useVideo, soundEnabled, onToggleSound, viewerHeight }: { video: Video; selected?: boolean; selecting?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; onNavigate: (route: any) => void; feedVideoSource: string; feedVideoStartPercent: number; feedVideoStartMinDuration: number; useVideo: boolean; soundEnabled: boolean; onToggleSound: () => void; viewerHeight: number | null }) {
  const file = video.files[0];
  const title = video.title || file?.basename || `Video ${video.id}`;
  const duration = getVideoDisplayDuration(video);
  const { coverUrl, videoSrc, videoStatusSrc } = getVideoFeedMedia(video, feedVideoSource);
  const videoStartTimeSec = getVideoFeedVideoStartTime(video, feedVideoSource, feedVideoStartPercent, feedVideoStartMinDuration);
  const openOrSelect = (toggleOptions?: MultiSelectToggleOptions) => selecting ? onSelect?.(toggleOptions) : onNavigate({ page: "video", id: video.id });
  const availableViewerHeight = viewerHeight != null ? Math.max(120, viewerHeight) : null;

  return (
    <article data-vertical-video-id={video.id} className="flex h-full min-h-0 snap-start snap-always items-center justify-center px-2 py-0 sm:px-4">
      <WallMediaCard
        title={title}
        imageSrc={coverUrl}
        videoSrc={videoSrc}
        videoStatusSrc={videoStatusSrc}
        useVideo={useVideo}
        muted={!soundEnabled}
        videoStartTimeSec={videoStartTimeSec}
        videoPlayThreshold={0.72}
        playbackTracking={{ hostType: "video", hostId: video.id, surface: "vertical", scopeKey: `related-video-vertical:${video.id}` }}
        aspectRatio="9 / 16"
        imageClassName="object-cover"
        onClick={selecting ? (event) => openOrSelect(toggleOptionsFromEvent(event)) : undefined}
        style={{ width: availableViewerHeight != null ? `min(calc(100vw - 1rem), ${Math.round(availableViewerHeight * 0.5625)}px)` : "min(calc(100vw - 1rem), calc((100dvh - 10rem) * 0.5625))" }}
        className={`group mx-auto overflow-hidden rounded-[1.5rem] bg-card shadow-2xl transition-colors sm:rounded-[1.75rem] ${selected ? "border-accent ring-1 ring-accent/60" : "border-border hover:border-accent/50"}`}
      >
        <button
          type="button"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            onToggleSound();
          }}
          className="absolute right-2 top-2 z-20 rounded-full border border-white/15 bg-black/60 p-2 text-white shadow transition-colors hover:bg-black/80"
          aria-label={soundEnabled ? "Mute vertical viewer" : "Unmute vertical viewer"}
          title={soundEnabled ? "Mute vertical viewer" : "Unmute vertical viewer"}
        >
          {soundEnabled ? <Volume2 className="h-4 w-4" /> : <VolumeX className="h-4 w-4" />}
        </button>
        <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
        <RouteCardLinkOverlay route={{ page: "video", id: video.id }} onClick={openOrSelect} label={`Open video ${title}`} disabled={selecting} selectionSafeZone />
        {duration > 0 ? <span className="absolute right-2 top-2 rounded bg-black/65 px-2 py-0.5 text-xs text-white">{formatDuration(duration)}</span> : null}
        <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/95 via-black/45 to-transparent p-4 pt-14 text-white">
          <div className="flex flex-wrap items-center gap-2 text-[11px] text-white/75">
            {video.studioName ? <span>{video.studioName}</span> : null}
            {video.date ? <span>{video.date}</span> : null}
            <span>{feedVideoSource === "video" ? "Full video" : "Preview clip"}</span>
          </div>
          <p className="mt-1 line-clamp-2 text-base font-semibold leading-tight sm:text-lg">{title}</p>
          <div className="mt-2 flex flex-wrap gap-1.5 text-xs text-white/85">
            {video.performers.slice(0, 3).map((performer) => <span key={performer.id}>@{performer.name}</span>)}
            {video.tags.slice(0, 3).map((tag) => <span key={tag.id}>#{tag.name}</span>)}
          </div>
        </div>
      </WallMediaCard>
    </article>
  );
}

interface RelatedListDensity {
  minHeight: number;
  estimateSize: number;
  thumbnailHeight: number;
  showImage: boolean;
  showSubtitle: boolean;
  showStats: boolean;
  showDescription: boolean;
  showBadges: boolean;
  titleClassName: string;
  rowGapClassName: string;
  rowPaddingClassName: string;
  itemGapClassName: string;
  containerGapClassName: string;
}

function getRelatedListDensity(level: number): RelatedListDensity {
  const normalizedLevel = Number.isFinite(level) ? Math.max(0, Math.min(8, level)) : 1;

  if (normalizedLevel <= 0.25) {
    return {
      minHeight: 42,
      estimateSize: 48,
      thumbnailHeight: 0,
      showImage: false,
      showSubtitle: false,
      showStats: false,
      showDescription: false,
      showBadges: false,
      titleClassName: "text-sm",
      rowGapClassName: "gap-2",
      rowPaddingClassName: "px-2 py-1.5",
      itemGapClassName: "pb-1",
      containerGapClassName: "gap-1",
    };
  }

  if (normalizedLevel <= 0.75) {
    return {
      minHeight: 58,
      estimateSize: 64,
      thumbnailHeight: 0,
      showImage: false,
      showSubtitle: true,
      showStats: false,
      showDescription: false,
      showBadges: true,
      titleClassName: "text-sm",
      rowGapClassName: "gap-2.5",
      rowPaddingClassName: "p-2",
      itemGapClassName: "pb-1.5",
      containerGapClassName: "gap-1.5",
    };
  }

  const thumbnailHeight = Math.round(62 + normalizedLevel * 8);
  const minHeight = thumbnailHeight + 18;

  return {
    minHeight,
    estimateSize: minHeight + 10,
    thumbnailHeight,
    showImage: true,
    showSubtitle: true,
    showStats: normalizedLevel >= 1,
    showDescription: normalizedLevel >= 1,
    showBadges: true,
    titleClassName: "text-sm sm:text-[15px]",
    rowGapClassName: "gap-3",
    rowPaddingClassName: "p-2",
    itemGapClassName: "pb-2",
    containerGapClassName: "gap-2",
  };
}

function RelatedThumbnail({ entityType, item, density }: { entityType: RelatedEntityType; item: RelatedEntityItem; density: RelatedListDensity }) {
  const src = getRelatedThumbnailUrl(entityType, item, 320);
  const Icon = getRelatedThumbnailIcon(entityType);
  const portrait = entityType === "performers";
  const height = density.thumbnailHeight;
  const width = portrait ? Math.round(height * 0.68) : Math.round(height * 1.42);

  return (
    <div className="relative shrink-0 overflow-hidden rounded-md border border-border/70 bg-surface/80" style={{ height, width }}>
      {src ? (
        <>
          <img src={src} alt="" className="h-full w-full object-cover" loading="lazy" onError={(event) => { const image = event.currentTarget; image.style.display = "none"; const fallback = image.nextElementSibling as HTMLElement | null; if (fallback) fallback.style.display = "flex"; }} />
          <div className="hidden h-full w-full items-center justify-center text-muted"><Icon className="h-7 w-7" /></div>
        </>
      ) : (
        <div className="flex h-full w-full items-center justify-center text-muted"><Icon className="h-7 w-7" /></div>
      )}
    </div>
  );
}

function getRelatedThumbnailUrl(entityType: RelatedEntityType, item: RelatedEntityItem, max: number) {
  switch (entityType) {
    case "videos": return entityImages.videoCoverUrl((item as Video).id, (item as Video).updatedAt, Math.max(max, 960));
    case "images": return imageApi.thumbnailUrl((item as Image).id, max);
    case "performers": return (item as Performer).imagePath;
    case "galleries": return (item as Gallery).coverPath ?? galleryApi.coverUrl((item as Gallery).id, (item as Gallery).updatedAt, Math.max(max, 640));
    case "studios": return (item as Studio).imagePath;
    case "tags": return (item as Tag).imagePath;
    case "groups": return (item as Group).frontImagePath ?? (item as Group).backImagePath;
    case "audios": return (item as Audio).imagePath ?? undefined;
    case "texts": return (item as TextDocument).imagePath ?? undefined;
    case "segments": return entityImages.segmentCoverUrl((item as SegmentRecord).id, (item as SegmentRecord).updatedAt, max);
    case "faces": return (item as Face).coverImageUrl;
  }
}

function getRelatedThumbnailIcon(entityType: RelatedEntityType) {
  switch (entityType) {
    case "videos": return Film;
    case "images": return ImageIcon;
    case "performers": return User;
    case "galleries": return FolderOpen;
    case "studios": return Building2;
    case "tags": return TagIcon;
    case "groups": return Layers;
    case "audios": return FileAudio;
    case "texts": return FileText;
    case "segments": return Hash;
    case "faces": return Users;
  }
}

function getRelatedDescription(entityType: RelatedEntityType, item: RelatedEntityItem) {
  switch (entityType) {
    case "videos": return (item as Video).details;
    case "images": return (item as Image).details;
    case "performers": return (item as Performer).details ?? (item as Performer).aliases.slice(0, 4).join(", ");
    case "galleries": return (item as Gallery).details;
    case "studios": return (item as Studio).details;
    case "tags": return (item as Tag).description;
    case "groups": return (item as Group).description;
    case "audios": return (item as Audio).details;
    case "texts": return (item as TextDocument).details ?? (item as TextDocument).files[0]?.excerptText ?? undefined;
    default: return undefined;
  }
}

function getRelatedStatusBadge(entityType: RelatedEntityType, item: RelatedEntityItem) {
  const favorite = entityType === "performers" ? (item as Performer).favorite
    : entityType === "studios" ? (item as Studio).favorite
      : entityType === "tags" ? (item as Tag).favorite
        : false;
  const organized = entityType === "videos" ? (item as Video).organized
    : entityType === "images" ? (item as Image).organized
      : entityType === "galleries" ? (item as Gallery).organized
        : entityType === "audios" ? (item as Audio).organized
          : entityType === "texts" ? (item as TextDocument).organized
            : false;

  if (favorite) return <span className="rounded-full border border-red-500/30 bg-red-500/10 px-2 py-0.5 text-[10px] font-semibold uppercase text-red-300">Favorite</span>;
  if (organized) return <span className="rounded-full border border-emerald-500/25 bg-emerald-500/10 px-2 py-0.5 text-[10px] font-semibold uppercase text-emerald-300">Organized</span>;
  return null;
}

function getRelatedStats(entityType: RelatedEntityType, item: RelatedEntityItem) {
  switch (entityType) {
    case "videos": {
      const video = item as Video;
      const file = video.files[0];
      return [video.date, file ? getResolutionLabel(file.width, file.height) : null, file?.size ? formatFileSize(file.size) : null, video.performers.length ? `${video.performers.length} performers` : null, video.tags.length ? `${video.tags.length} tags` : null].filter(Boolean) as string[];
    }
    case "images": {
      const image = item as Image;
      const file = image.files[0];
      return [image.date, file ? getResolutionLabel(file.width, file.height) : null, file?.size ? formatFileSize(file.size) : null, image.galleryCount ? `${image.galleryCount} galleries` : null, image.tags.length ? `${image.tags.length} tags` : null].filter(Boolean) as string[];
    }
    case "performers": {
      const performer = item as Performer;
      return [performer.videoCount ? `${performer.videoCount} videos` : null, performer.imageCount ? `${performer.imageCount} images` : null, performer.galleryCount ? `${performer.galleryCount} galleries` : null, performer.groupCount ? `${performer.groupCount} groups` : null].filter(Boolean) as string[];
    }
    case "galleries": {
      const gallery = item as Gallery;
      return [gallery.date, gallery.imageCount ? `${gallery.imageCount} images` : null, gallery.videoCount ? `${gallery.videoCount} videos` : null, gallery.performers.length ? `${gallery.performers.length} performers` : null].filter(Boolean) as string[];
    }
    case "studios": {
      const studio = item as Studio;
      return [studio.videoCount ? `${studio.videoCount} videos` : null, studio.imageCount ? `${studio.imageCount} images` : null, studio.galleryCount ? `${studio.galleryCount} galleries` : null, studio.childStudioCount ? `${studio.childStudioCount} child studios` : null].filter(Boolean) as string[];
    }
    case "tags": {
      const tag = item as Tag;
      return [tag.tagGroupName, tag.videoCount ? `${tag.videoCount} videos` : null, tag.imageCount ? `${tag.imageCount} images` : null, tag.performerCount ? `${tag.performerCount} performers` : null].filter(Boolean) as string[];
    }
    case "groups": {
      const group = item as Group;
      return [group.kind, group.itemCount ? `${group.itemCount} items` : null, group.videoCount ? `${group.videoCount} videos` : null, group.imageCount ? `${group.imageCount} images` : null].filter(Boolean) as string[];
    }
    case "audios": {
      const audio = item as Audio;
      return [audio.date, audio.maxDuration ? formatDuration(audio.maxDuration) : null, audio.fileCount ? `${audio.fileCount} files` : null, audio.performers.length ? `${audio.performers.length} performers` : null].filter(Boolean) as string[];
    }
    case "texts": {
      const text = item as TextDocument;
      return [text.date, text.maxWordCount ? `${text.maxWordCount.toLocaleString()} words` : null, text.maxPageCount ? `${text.maxPageCount.toLocaleString()} pages` : null, text.fileCount ? `${text.fileCount} files` : null].filter(Boolean) as string[];
    }
    case "segments": {
      const segment = item as SegmentRecord;
      return [segment.hostType, formatDuration(Math.max(0, (segment.endSec ?? segment.startSec) - segment.startSec)), segment.confidence != null ? `${Math.round(segment.confidence * 100)}%` : null].filter(Boolean) as string[];
    }
    case "faces": {
      const face = item as Face;
      return [`${face.appearanceCount} appearances`, `${face.detectionCount} detections`, `${face.videoCount} videos`, `${face.imageCount} images`];
    }
  }
}

function getVideoDisplayDuration(video: Video) {
  if (typeof video.clipStartSec === "number" && typeof video.clipEndSec === "number") {
    return Math.max(0, video.clipEndSec - video.clipStartSec);
  }

  return video.files[0]?.duration ?? 0;
}

function getVideoFeedMedia(video: Video, feedVideoSource: string) {
  const coverUrl = entityImages.videoCoverUrl(video.id, video.updatedAt, 1280);

  if (feedVideoSource === "video") {
    return { coverUrl, videoSrc: videoApi.streamUrl(video.id), videoStatusSrc: undefined };
  }

  return { coverUrl, videoSrc: videoApi.previewUrl(video.id), videoStatusSrc: videoApi.previewStatusUrl(video.id) };
}

function getVideoFeedVideoStartTime(video: Video, feedVideoSource: string, startPercent: number, minDuration: number) {
  if (feedVideoSource !== "video" || startPercent <= 0) return 0;
  const duration = getVideoDisplayDuration(video);
  if (duration <= Math.max(0, minDuration)) return 0;
  return duration * (Math.min(95, Math.max(0, startPercent)) / 100);
}

function noop() {}
