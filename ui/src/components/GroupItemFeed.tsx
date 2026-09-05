import { useInfiniteQuery, useQueries, useQuery } from "@tanstack/react-query";
import { AlertTriangle, Film, Image as ImageIcon, Layers, RotateCcw } from "lucide-react";
import { useMemo } from "react";
import {
  audios,
  entityImages,
  faces,
  galleries,
  groups,
  images,
  performers,
  segmentLibrary,
  studios,
  tags,
  texts,
  videos,
} from "../api/client";
import type {
  AffinityHostType,
  Audio,
  Face,
  Gallery,
  Group,
  GroupItem,
  Image,
  Performer,
  SegmentRecord,
  Studio,
  Tag,
  TextDocument,
  Video,
} from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canReadEntity, type EntityResource } from "../auth/visibility";
import { useEntityEngagement } from "../hooks/useEntityEngagement";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { getAudioDisplayTitle, getTextDisplayTitle } from "../utils/audioTextDisplay";
import { getGalleryDisplayTitle } from "../utils/galleryDisplay";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import {
  AudioTile,
  FaceTile,
  GalleryTile,
  GroupTile,
  PerformerTile,
  SegmentTile,
  StudioTile,
  TagTile,
  TextTile,
} from "./EntityCards";
import {
  FeedCardFrame,
  FeedIdentityBadge,
  FeedInlineRating,
  FeedMetadataPill,
  FeedPortraitMediaFrame,
  getFeedMediaStyle,
} from "./FeedCardFrame";
import { NarrativeText } from "./NarrativeText";
import { VirtualizedInfiniteList } from "./VirtualizedInfiniteList";
import { WallMediaCard } from "./WallMediaCard";

const DEFAULT_PAGE_SIZE = 10;

export interface GroupItemFeedProps {
  groupId: number;
  onNavigate: (route: { page: string; id?: number; [key: string]: unknown }) => void;
  pageSize?: number;
  showGroupHeader?: boolean;
}

type GroupFeedEntity =
  | { type: "video"; value: Video }
  | { type: "image"; value: Image }
  | { type: "audio"; value: Audio }
  | { type: "text"; value: TextDocument }
  | { type: "group"; value: Group }
  | { type: "performer"; value: Performer }
  | { type: "studio"; value: Studio }
  | { type: "tag"; value: Tag }
  | { type: "gallery"; value: Gallery }
  | { type: "face"; value: Face }
  | { type: "segment"; value: SegmentRecord };

type GroupFeedEntityState = { status: "loading" } | { status: "error" } | { status: "ready"; entity: GroupFeedEntity };

interface GroupFeedHost {
  resource: EntityResource;
  hostType: AffinityHostType;
  id: number;
  route: { page: string; id: number; seekTo?: number };
  kindLabel: string;
}

export function GroupItemFeed({
  groupId,
  onNavigate,
  pageSize = DEFAULT_PAGE_SIZE,
  showGroupHeader = true,
}: GroupItemFeedProps) {
  const { hasPermission, permissions, user } = useAuth();
  const normalizedGroupId = Number.isInteger(groupId) && groupId > 0 ? groupId : 0;
  const normalizedPageSize = Math.min(100, Math.max(1, Math.round(pageSize || DEFAULT_PAGE_SIZE)));
  const principalKey = useMemo(() => {
    const permissionKey = [...permissions].sort().join(",");
    const readGrantKey = [...(user?.readGrantedEntityKinds ?? [])].sort().join(",");
    return `${user?.kind ?? "anonymous"}:${user?.id ?? "none"}:${permissionKey}:${readGrantKey}`;
  }, [permissions, user?.id, user?.kind, user?.readGrantedEntityKinds]);
  const groupQuery = useQuery({
    queryKey: ["group-feed", principalKey, "group", normalizedGroupId],
    queryFn: () => groups.get(normalizedGroupId),
    enabled: normalizedGroupId > 0,
  });
  const itemsQuery = useInfiniteQuery({
    queryKey: ["group-feed", principalKey, "items", normalizedGroupId, normalizedPageSize],
    initialPageParam: 1,
    queryFn: ({ pageParam }) =>
      groups.items.page(normalizedGroupId, {
        page: Number(pageParam),
        perPage: normalizedPageSize,
        sort: "order",
        direction: "asc",
      }),
    getNextPageParam: (lastPage, pages) => {
      const loaded = pages.reduce((total, page) => total + page.items.length, 0);
      return loaded < lastPage.totalCount ? lastPage.page + 1 : undefined;
    },
    enabled: normalizedGroupId > 0,
  });
  const items = useMemo(() => itemsQuery.data?.pages.flatMap((page) => page.items) ?? [], [itemsQuery.data]);
  const entityStates = useGroupFeedEntities(items, hasPermission, principalKey);
  const totalCount = itemsQuery.data?.pages[0]?.totalCount ?? 0;

  if (normalizedGroupId === 0) {
    return <GroupFeedMessage>Select a group in this widget's settings.</GroupFeedMessage>;
  }

  if (groupQuery.error || itemsQuery.error) {
    return (
      <div
        role="alert"
        className="mx-auto flex min-h-44 w-full max-w-2xl flex-col items-center justify-center gap-3 rounded-xl border border-red-500/25 bg-red-500/5 p-6 text-center"
      >
        <AlertTriangle className="h-7 w-7 text-red-400" />
        <p className="font-medium text-foreground">This group feed could not be loaded.</p>
        <button
          type="button"
          onClick={() => {
            void groupQuery.refetch();
            void itemsQuery.refetch();
          }}
          className="rounded border border-border px-3 py-2 text-sm text-foreground hover:border-accent"
        >
          <RotateCcw className="mr-2 inline h-4 w-4" />
          Retry
        </button>
      </div>
    );
  }

  if (groupQuery.isLoading || itemsQuery.isLoading) {
    return (
      <div
        className="mx-auto min-h-64 w-full max-w-[64rem] animate-pulse rounded-xl bg-card/50"
        aria-label="Loading group feed"
      />
    );
  }

  return (
    <section className="min-w-0" data-group-item-feed={normalizedGroupId}>
      {showGroupHeader && groupQuery.data ? (
        <div className="mx-auto mb-4 flex w-full max-w-[64rem] flex-wrap items-end justify-between gap-3 px-3 sm:px-4">
          <div>
            <p className="text-xs font-semibold uppercase tracking-[0.16em] text-muted">Group feed</p>
            <h2 className="mt-1 text-xl font-semibold text-foreground">{groupQuery.data.name}</h2>
            <p className="mt-1 text-sm text-secondary">
              {totalCount} {totalCount === 1 ? "item" : "items"}
            </p>
          </div>
          <button
            type="button"
            onClick={() => onNavigate({ page: "group", id: normalizedGroupId })}
            className="rounded-md border border-border px-3 py-2 text-sm text-foreground hover:border-accent"
          >
            Open group
          </button>
        </div>
      ) : null}

      {items.length === 0 ? (
        <GroupFeedMessage>This group has no items yet.</GroupFeedMessage>
      ) : (
        <div className="mx-auto w-full max-w-[64rem] px-3 sm:px-4">
          <VirtualizedInfiniteList
            items={items}
            getItemKey={(item) => `${item.kind}:${item.id}:${item.orderIndex}`}
            estimateSize={760}
            overscan={2}
            hasNextPage={Boolean(itemsQuery.hasNextPage)}
            isFetchingNextPage={itemsQuery.isFetchingNextPage}
            loadMore={() => {
              void itemsQuery.fetchNextPage();
            }}
            itemClassName="pb-5 [touch-action:pan-y]"
            renderItem={({ item, isActive }) => (
              <GroupFeedItemCard
                item={item}
                entityState={entityStates.get(groupFeedItemKey(item))}
                active={isActive}
                principalKey={principalKey}
                onNavigate={onNavigate}
              />
            )}
          />
        </div>
      )}
    </section>
  );
}

function GroupFeedMessage({ children }: { children: string }) {
  return (
    <div className="mx-auto flex min-h-40 w-full max-w-[64rem] items-center justify-center rounded-xl border border-dashed border-border bg-card/30 px-4 text-center text-sm text-muted">
      {children}
    </div>
  );
}

function useGroupFeedEntities(
  items: GroupItem[],
  hasPermission: (permission: string) => boolean,
  principalKey: string,
) {
  const queryItems = useMemo(
    () =>
      items.flatMap((item) => {
        const host = resolveGroupFeedHost(item);
        return host && canReadEntity(host.resource, hasPermission) ? [{ item, host }] : [];
      }),
    [hasPermission, items],
  );
  const queries = useQueries({
    queries: queryItems.map(({ item, host }) => createGroupFeedEntityQuery(item, host, principalKey)),
  });

  return useMemo(() => {
    const states = new Map<string, GroupFeedEntityState>();
    queryItems.forEach(({ item }, index) => {
      const query = queries[index];
      if (query?.isError) states.set(groupFeedItemKey(item), { status: "error" });
      else if (!query?.data) states.set(groupFeedItemKey(item), { status: "loading" });
      else states.set(groupFeedItemKey(item), { status: "ready", entity: query.data });
    });
    return states;
  }, [queries, queryItems]);
}

function createGroupFeedEntityQuery(item: GroupItem, host: GroupFeedHost, principalKey: string) {
  return {
    queryKey: ["group-feed", principalKey, "host", host.hostType, host.id, item.updatedAt],
    staleTime: 60_000,
    queryFn: async (): Promise<GroupFeedEntity> => {
      switch (host.resource) {
        case "video":
          return { type: "video", value: await videos.get(host.id) };
        case "image":
          return { type: "image", value: await images.get(host.id) };
        case "audio":
          return { type: "audio", value: await audios.get(host.id) };
        case "text":
          return { type: "text", value: await texts.get(host.id) };
        case "group":
          return { type: "group", value: await groups.get(host.id) };
        case "performer":
          return { type: "performer", value: await performers.get(host.id) };
        case "studio":
          return { type: "studio", value: await studios.get(host.id) };
        case "tag":
          return { type: "tag", value: await tags.get(host.id) };
        case "gallery":
          return { type: "gallery", value: await galleries.get(host.id) };
        case "face":
          return { type: "face", value: await faces.get(host.id) };
        case "segment": {
          const segment = await segmentLibrary.get(host.id);
          if (!segment) throw new Error("Segment not found");
          return { type: "segment", value: segment };
        }
      }
    },
  };
}

function GroupFeedItemCard({
  item,
  entityState,
  active,
  principalKey,
  onNavigate,
}: {
  item: GroupItem;
  entityState?: GroupFeedEntityState;
  active: boolean;
  principalKey: string;
  onNavigate: GroupItemFeedProps["onNavigate"];
}) {
  const { hasPermission, user } = useAuth();
  const host = resolveGroupFeedHost(item);
  const entity = entityState?.status === "ready" ? entityState.entity : undefined;
  const readable = !!host && !!entity && canReadEntity(host.resource, hasPermission);
  const canEngage = readable && (user?.kind === "user" || user?.kind === "system");
  const { engagement, rating, setRating, ratingPending } = useEntityEngagement(
    host?.hostType ?? "video",
    host?.id ?? 0,
    {
      enabled: canEngage,
      queryScope: principalKey,
    },
  );
  const loading = !!host && entityState?.status === "loading" && canReadEntity(host.resource, hasPermission);
  const title = readable ? getGroupFeedTitle(item, entity) : loading ? "Loading item…" : "Unavailable item";
  const details = readable
    ? item.notes || getGroupFeedDetails(entity)
    : loading
      ? undefined
      : "This item is not available to the current account.";
  const route = readable ? host.route : undefined;
  const open = () => {
    if (route) onNavigate(route);
  };

  return (
    <FeedCardFrame
      dataAttribute={{ "data-feed-group-item": "true" }}
      identity={<FeedIdentityBadge>{readable ? host.kindLabel : "Item"}</FeedIdentityBadge>}
      header={
        readable ? (
          <>
            <span>Item {item.orderIndex + 1}</span>
            {item.kind === "videoRange" && item.startSec != null ? (
              <span>Starts at {formatClock(item.startSec)}</span>
            ) : null}
          </>
        ) : (
          <span>{loading ? "Loading item" : "Unavailable item"}</span>
        )
      }
      headerActions={
        readable ? (
          <FeedInlineRating
            value={rating ?? engagement?.rating}
            onChange={setRating}
            readOnly={!canEngage}
            pending={ratingPending}
          />
        ) : undefined
      }
      media={renderGroupFeedMedia(item, entity, entityState, active, open, onNavigate, engagement)}
      title={
        route ? (
          <button
            type="button"
            onClick={open}
            className="max-w-full text-left text-base font-semibold text-foreground [overflow-wrap:anywhere] transition-colors hover:text-accent"
          >
            {title}
          </button>
        ) : (
          <span className="[overflow-wrap:anywhere]">{title}</span>
        )
      }
      details={details ? <NarrativeText className="line-clamp-4">{details}</NarrativeText> : undefined}
      metadata={
        readable ? (
          <>
            <FeedMetadataPill>{host.kindLabel}</FeedMetadataPill>
            {item.title ? <FeedMetadataPill>Custom title</FeedMetadataPill> : null}
          </>
        ) : undefined
      }
    />
  );
}

function renderGroupFeedMedia(
  item: GroupItem,
  entity: GroupFeedEntity | undefined,
  state: GroupFeedEntityState | undefined,
  active: boolean,
  open: () => void,
  onNavigate: GroupItemFeedProps["onNavigate"],
  engagement: ReturnType<typeof useEntityEngagement>["engagement"],
) {
  if (state?.status === "loading") return <div className="aspect-video animate-pulse rounded-2xl bg-card" />;
  if (!entity || state?.status === "error") {
    return (
      <div className="flex aspect-video items-center justify-center rounded-2xl border border-border bg-card/60 text-muted">
        <Layers className="h-12 w-12" />
      </div>
    );
  }

  if (entity.type === "video")
    return <GroupVideoFeedMedia item={item} video={entity.value} active={active} onOpen={open} />;
  if (entity.type === "image") return <GroupImageFeedMedia item={item} image={entity.value} onOpen={open} />;

  return (
    <div className="mx-auto w-full max-w-xl px-2 py-1">{renderEntityTile(entity, engagement, onNavigate, open)}</div>
  );
}

function GroupVideoFeedMedia({
  item,
  video,
  active,
  onOpen,
}: {
  item: GroupItem;
  video: Video;
  active: boolean;
  onOpen: () => void;
}) {
  const appConfig = useOptionalAppConfig();
  const source = appConfig?.config?.ui.feedVideoSource === "video" ? "video" : "preview";
  const configuredStartPercent = appConfig?.config?.ui.feedVideoStartPercent ?? 0;
  const configuredMinimumDuration = appConfig?.config?.ui.feedVideoStartMinDuration ?? 0;
  const file = video.files[0];
  const title = item.title || video.title || file?.basename || `Video ${video.id}`;
  const coverUrl = entityImages.videoCoverUrl(video.id, video.updatedAt, 1280);
  const isRange = item.kind === "videoRange" && item.startSec != null;
  const useFullVideo = source === "video" || isRange;
  const videoSrc = useFullVideo ? videos.streamUrl(video.id) : videos.previewUrl(video.id);
  const videoStatusSrc = useFullVideo ? undefined : videos.previewStatusUrl(video.id);
  const duration =
    item.kind === "videoRange" && item.startSec != null && item.endSec != null
      ? Math.max(0, item.endSec - item.startSec)
      : (file?.duration ?? 0);
  const startTime =
    item.kind === "videoRange" && item.startSec != null
      ? item.startSec
      : useFullVideo && configuredStartPercent > 0 && duration > configuredMinimumDuration
        ? duration * (Math.min(95, Math.max(0, configuredStartPercent)) / 100)
        : 0;
  const mediaStyle = getFeedMediaStyle(file);
  const media = (
    <WallMediaCard
      title={title}
      imageSrc={coverUrl}
      imageAlt={video.imagePath ? title : ""}
      videoSrc={videoSrc}
      videoStatusSrc={videoStatusSrc}
      useVideo={active}
      muted
      videoStartTimeSec={startTime}
      videoEndTimeSec={isRange ? (item.endSec ?? undefined) : undefined}
      videoPlayThreshold={0.5}
      playbackTracking={{
        hostType: "video",
        hostId: video.id,
        surface: "feed",
        scopeKey: `group-feed:${item.groupId}:${item.id}`,
        groupItemId: item.id > 0 ? item.id : undefined,
        clipStartSec: isRange ? item.startSec : undefined,
        clipEndSec: isRange ? (item.endSec ?? null) : undefined,
      }}
      aspectRatio={file?.width && file.height ? `${file.width} / ${file.height}` : "16 / 9"}
      fillMedia={Boolean(mediaStyle)}
      chromeless={Boolean(mediaStyle)}
      imageClassName={mediaStyle ? "object-contain" : "object-cover"}
      videoClassName={mediaStyle ? "object-contain" : undefined}
      style={mediaStyle}
      className={
        mediaStyle
          ? "h-full w-full bg-transparent"
          : "overflow-hidden rounded-2xl border border-border/70 bg-black/95 shadow-[0_18px_40px_rgba(0,0,0,0.35)]"
      }
    />
  );
  const clickable = (
    <button
      type="button"
      onClick={onOpen}
      className="block w-full cursor-pointer text-left"
      aria-label={`Open ${title}`}
    >
      {media}
    </button>
  );
  return mediaStyle ? <FeedPortraitMediaFrame title={title} backgroundSrc={coverUrl} media={clickable} /> : clickable;
}

function GroupImageFeedMedia({ item, image, onOpen }: { item: GroupItem; image: Image; onOpen: () => void }) {
  const file = image.files[0];
  const title = item.title || getImageDisplayTitle(image);
  const imageSrc = images.thumbnailUrl(image.id, 1280);
  const mediaStyle = getFeedMediaStyle(file);
  const media = (
    <WallMediaCard
      title={title}
      imageSrc={imageSrc}
      aspectRatio={file?.width && file.height ? `${file.width} / ${file.height}` : "1 / 1"}
      fillMedia={Boolean(mediaStyle)}
      chromeless={Boolean(mediaStyle)}
      imageClassName={mediaStyle ? "object-contain" : "object-cover"}
      style={mediaStyle}
      className={
        mediaStyle
          ? "h-full w-full bg-transparent"
          : "overflow-hidden rounded-2xl border border-border/70 bg-black/95 shadow-[0_18px_40px_rgba(0,0,0,0.35)]"
      }
    />
  );
  const clickable = (
    <button
      type="button"
      onClick={onOpen}
      className="block w-full cursor-pointer text-left"
      aria-label={`Open ${title}`}
    >
      {media}
    </button>
  );
  return mediaStyle ? <FeedPortraitMediaFrame title={title} backgroundSrc={imageSrc} media={clickable} /> : clickable;
}

function renderEntityTile(
  entity: GroupFeedEntity,
  engagement: ReturnType<typeof useEntityEngagement>["engagement"],
  onNavigate: GroupItemFeedProps["onNavigate"],
  onOpen: () => void,
) {
  const normalizedEngagement = engagement ?? undefined;
  switch (entity.type) {
    case "audio":
      return (
        <AudioTile audio={entity.value} engagement={normalizedEngagement} onClick={onOpen} onNavigate={onNavigate} />
      );
    case "text":
      return (
        <TextTile text={entity.value} engagement={normalizedEngagement} onClick={onOpen} onNavigate={onNavigate} />
      );
    case "group":
      return (
        <GroupTile group={entity.value} engagement={normalizedEngagement} onClick={onOpen} onNavigate={onNavigate} />
      );
    case "performer":
      return (
        <PerformerTile
          performer={entity.value}
          engagement={normalizedEngagement}
          onClick={onOpen}
          onNavigate={onNavigate}
        />
      );
    case "studio":
      return (
        <StudioTile studio={entity.value} engagement={normalizedEngagement} onClick={onOpen} onNavigate={onNavigate} />
      );
    case "tag":
      return <TagTile tag={entity.value} engagement={normalizedEngagement} onClick={onOpen} onNavigate={onNavigate} />;
    case "gallery":
      return (
        <GalleryTile
          gallery={entity.value}
          engagement={normalizedEngagement}
          onClick={onOpen}
          onNavigate={onNavigate}
        />
      );
    case "face":
      return <FaceTile face={entity.value} onClick={onOpen} />;
    case "segment":
      return <SegmentTile segment={entity.value} onClick={onOpen} />;
    case "video":
      return (
        <div className="flex aspect-video items-center justify-center rounded-xl bg-card">
          <Film className="h-10 w-10 text-muted" />
        </div>
      );
    case "image":
      return (
        <div className="flex aspect-video items-center justify-center rounded-xl bg-card">
          <ImageIcon className="h-10 w-10 text-muted" />
        </div>
      );
  }
}

function resolveGroupFeedHost(item: GroupItem): GroupFeedHost | null {
  const kind = String(item.kind).toLowerCase();
  const candidate = kind === "videorange" ? "video" : String(item.hostType || kind).toLowerCase();
  const resource = isEntityResource(candidate) ? candidate : null;
  if (!resource) return null;
  const id =
    resource === "video"
      ? (item.videoId ?? item.hostId)
      : resource === "image"
        ? (item.imageId ?? item.hostId)
        : resource === "group"
          ? (item.childGroupId ?? item.hostId)
          : item.hostId;
  if (!id || id <= 0) return null;
  const route = {
    page: resource,
    id,
    ...(resource === "video" && item.startSec && item.startSec > 0 ? { seekTo: item.startSec } : {}),
  };
  return { resource, hostType: resource, id, route, kindLabel: ENTITY_KIND_LABELS[resource] };
}

function isEntityResource(value: string): value is EntityResource {
  return Object.hasOwn(ENTITY_KIND_LABELS, value);
}

const ENTITY_KIND_LABELS: Record<EntityResource, string> = {
  video: "Video",
  image: "Image",
  audio: "Audio",
  text: "Text",
  group: "Group",
  performer: "Performer",
  studio: "Studio",
  tag: "Tag",
  gallery: "Gallery",
  face: "Face",
  segment: "Segment",
};

function getGroupFeedTitle(item: GroupItem, entity?: GroupFeedEntity) {
  if (item.title) return item.title;
  if (!entity) return item.videoTitle || item.imageTitle || item.childGroupName || "Loading item…";
  switch (entity.type) {
    case "video":
      return entity.value.title || entity.value.files[0]?.basename || `Video ${entity.value.id}`;
    case "image":
      return getImageDisplayTitle(entity.value);
    case "audio":
      return getAudioDisplayTitle(entity.value);
    case "text":
      return getTextDisplayTitle(entity.value);
    case "group":
      return entity.value.name;
    case "performer":
      return entity.value.name;
    case "studio":
      return entity.value.name;
    case "tag":
      return entity.value.name;
    case "gallery":
      return getGalleryDisplayTitle(entity.value);
    case "face":
      return entity.value.label || `Face ${entity.value.id}`;
    case "segment":
      return entity.value.title || `Segment ${entity.value.id}`;
  }
}

function getGroupFeedDetails(entity?: GroupFeedEntity) {
  if (!entity) return undefined;
  const value = entity.value as { details?: string | null; description?: string | null };
  return value.details || value.description || undefined;
}

function groupFeedItemKey(item: GroupItem) {
  return `${item.kind}:${item.id}:${item.orderIndex}`;
}

function formatClock(seconds: number) {
  const value = Math.max(0, Math.round(seconds));
  const hours = Math.floor(value / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  const remaining = value % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remaining).padStart(2, "0")}`
    : `${minutes}:${String(remaining).padStart(2, "0")}`;
}
