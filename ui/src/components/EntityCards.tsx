import { useCallback, useEffect, useRef, useState, type ImgHTMLAttributes, type KeyboardEvent, type MouseEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { useQuery } from "@tanstack/react-query";
import { videos, images, performers, galleries, studios, groups, audios, texts, entityImages, faces as facesApi } from "../api/client";
import type { AffinityHostType, Audio, AudioFilterCriteria, EntityEngagement, Face, FaceAppearance, FieldProvenance, Gallery, Group, GroupItem, GroupSummary, Image, PerformerSummary, Video, SegmentRecord, Studio, Tag as TagType, TextDocument, TextFilterCriteria } from "../api/types";
import { formatDate, FieldProvenanceHover, formatDuration, formatFileSize, getResolutionLabel } from "./shared";
import { RatingBanner, RatingBadge } from "./Rating";
import { BookOpenText, Building2, FileText, Fingerprint, FolderOpen, GripVertical, Headphones, Layers, Link2, Tag, User, Film, Box, Images as ImagesIcon, Heart, Eye, ThumbsUp, Mic2, MonitorPlay, PlayCircle, Merge } from "lucide-react";
import { createRouteLinkProps, createNestedRouteLinkProps } from "./cardNavigation";
import { CardSelectionToggle, RouteCardLinkOverlay } from "./RouteCardLinkOverlay";
import { ExtensionSlot, useHasExtensionSlot } from "../router/RouteRegistry";
import { getImageDisplayTitle } from "../utils/imageDisplay";
import { getGalleryDisplayTitle } from "../utils/galleryDisplay";
import { faceDisplayName } from "../utils/faceDisplay";
import { getAudioDisplayTitle, getTextDisplayTitle, pickPrimaryTextFile } from "../utils/audioTextDisplay";
import { BookmarkButton } from "./BookmarkButton";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { SegmentPreviewMedia } from "./SegmentPreviewMedia";
import { toggleOptionsFromEvent, type MultiSelectToggleOptions } from "../hooks/useMultiSelect";
import { EntityMedia, TagMediaHover } from "./EntityMedia";
import { VideoPreviewThumbnail } from "./VideoPreviewThumbnail";

function CoverImage({ className = "", ...props }: ImgHTMLAttributes<HTMLImageElement>) {
  const fitClass = useConfiguredImageFit() === "contain" ? "object-contain" : "object-cover";
  return <img {...props} className={`${className} ${fitClass}`.trim()} />;
}

function useConfiguredImageFit() {
  const appConfig = useOptionalAppConfig();
  return appConfig?.config?.ui.imageObjectFit === "contain" ? "contain" as const : "cover" as const;
}

function createNestedEntityNavigationHandlers<T extends HTMLAnchorElement>(route: { page: string; id: number }, onNavigate?: (route: any) => void) {
  return createNestedRouteLinkProps<T>(route, onNavigate ? () => onNavigate(route) : undefined);
}

interface EntityTileDragHandleProps {
  tabIndex: number;
  role: "button";
  "aria-label": string;
  "aria-pressed": boolean;
  onKeyDown: (event: KeyboardEvent<HTMLElement>) => void;
}

interface EntityTileFrameProps {
  route: { page: string; id: number };
  label: string;
  onClick: (options?: MultiSelectToggleOptions) => void;
  media: ReactNode;
  body: ReactNode;
  footer?: ReactNode;
  children?: ReactNode;
  selected?: boolean;
  onSelect?: (options?: MultiSelectToggleOptions) => void;
  selecting?: boolean;
  selectable?: boolean;
  mediaClassName?: string;
  bodyClassName?: string;
  extensionClassName?: string;
  extensionBeforeFooter?: boolean;
  className?: string;
  dragHandleProps?: EntityTileDragHandleProps;
  isDragging?: boolean;
  isOver?: boolean;
}

/**
 * Extension content in a card's content area, with the guardrails that keep a misbehaving extension from breaking
 * cards: it renders nothing when no extension registers (a null render collapses to zero height); each entry is
 * error-boundaried with a null fallback (a crash shows nothing, never a red box); and the box is bounded, clipped,
 * and isolated (`max-h`/`overflow-hidden`/`contain`/`isolate`) — a bad render stays inside its own box and cannot
 * resize the card or break the grid.
 */
export function CardExtensionSlot<TContext extends object>({ slot, context }: { slot: string; context: TContext }) {
  const has = useHasExtensionSlot(slot);
  if (!has) return null;
  return (
    <div
      className="card-extension relative isolate max-h-24 overflow-hidden"
      style={{ contain: "layout paint" }}
    >
      <ExtensionSlot slot={slot} context={context} fallback={null} entryClassName="min-w-0 max-w-full" />
    </div>
  );
}

export function EntityTileFrame({
  route,
  label,
  onClick,
  media,
  body,
  footer,
  children,
  selected,
  onSelect,
  selecting,
  selectable = true,
  mediaClassName = "aspect-video bg-gradient-to-br from-surface to-card",
  bodyClassName = "p-2.5",
  extensionClassName = "px-2 py-1.5",
  extensionBeforeFooter = false,
  className = "",
  dragHandleProps,
  isDragging,
  isOver,
}: EntityTileFrameProps) {
  return (
    <div
      onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined}
      className={`entity-card group relative flex h-full cursor-pointer flex-col overflow-hidden rounded-lg border bg-card text-left transition-colors ${selected ? "border-accent ring-2 ring-accent" : "border-border hover:border-accent/60"} ${isDragging ? "opacity-50" : ""} ${isOver ? "outline outline-2 outline-accent" : ""} ${className}`}
    >
      <RouteCardLinkOverlay route={route} onClick={onClick} label={label} disabled={selecting} selectionSafeZone={selectable && (selected !== undefined || selecting)} />
      <div className={`card-media relative flex shrink-0 items-center justify-center overflow-hidden ${mediaClassName}`}>
        {media}
        {selectable && (selected !== undefined || selecting) ? <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} /> : null}
      </div>
      <div className={`card-body flex flex-1 flex-col gap-1 border-t border-border/50 ${bodyClassName}`}>{body}</div>
      {children && extensionBeforeFooter ? <div className={`relative z-10 ${extensionClassName}`}>{children}</div> : null}
      {footer ? (
        <>
          <hr className="border-border/50 my-0" />
          <div className="relative z-10 flex min-h-[28px] flex-wrap items-center justify-center gap-1 rounded-b px-2 py-1.5 card-popovers">
            {footer}
          </div>
        </>
      ) : null}
      {children && !extensionBeforeFooter ? <div className={`relative z-10 ${extensionClassName}`}>{children}</div> : null}
      {dragHandleProps ? (
        <span
          {...dragHandleProps}
          onClick={(event) => event.stopPropagation()}
          className="absolute bottom-1.5 right-1.5 z-20 inline-flex h-7 w-7 cursor-grab items-center justify-center rounded bg-black/70 text-white transition-colors hover:bg-black/85 active:cursor-grabbing"
          title="Drag to reorder"
        >
          <GripVertical className="h-4 w-4" />
        </span>
      ) : null}
    </div>
  );
}

export function LikeCounter({ count }: { count: number }) {
  return (
    <span className="flex items-center gap-1 p-1 text-muted" title={`Likes: ${count}`}>
      <ThumbsUp className="h-3.5 w-3.5 fill-accent text-accent" />
      <span className="text-xs">{count}</span>
    </span>
  );
}

export function CardFavoriteButton(props: { hostType: AffinityHostType; hostId: number; favorite: boolean }) {
  if (!props.favorite) {
    return null;
  }

  return (
    <span className="inline-flex min-h-7 items-center justify-center p-1 text-red-400" title="Favorite" aria-label="Favorite">
      <Heart className="h-4 w-4 fill-current" />
    </span>
  );
}

export function PerformerPreviewGrid({ performers: performerItems, onNavigate }: { performers: Array<{ id: number; name: string; imagePath?: string | null }>; onNavigate?: (route: any) => void }) {
  return (
    <div className="grid grid-cols-2 gap-2">
      {performerItems.map((performer) => {
        const navigationHandlers = createNestedEntityNavigationHandlers<HTMLAnchorElement>({ page: "performer", id: performer.id }, onNavigate);

        return (
          <a
            key={performer.id}
            {...navigationHandlers}
            className="flex flex-col items-center gap-1.5 rounded p-1.5 text-center transition-colors hover:bg-card-hover group/perf"
          >
            <div className="w-20 h-28 rounded overflow-hidden bg-surface flex-shrink-0">
              {performer.imagePath ? (
                <CoverImage src={performer.imagePath} alt="" className="w-full h-full" loading="lazy" />
              ) : (
                <div className="w-full h-full flex items-center justify-center"><User className="w-8 h-8 text-muted" /></div>
              )}
            </div>
            <span className="text-xs text-accent group-hover/perf:underline truncate w-full font-medium">{performer.name}</span>
          </a>
        );
      })}
    </div>
  );
}

export function GalleryPreviewList({ galleries: galleryItems, onNavigate }: { galleries: Array<{ id: number; title?: string | null; date?: string | null; coverPath?: string | null }>; onNavigate?: (route: any) => void }) {
  return (
    <div className="space-y-1">
      {galleryItems.map((gallery) => {
        const navigationHandlers = createNestedEntityNavigationHandlers<HTMLAnchorElement>({ page: "gallery", id: gallery.id }, onNavigate);
        const galleryCoverSrc = gallery.coverPath ?? galleries.coverUrl(gallery.id);

        return (
          <a
            key={gallery.id}
            {...navigationHandlers}
            className="flex w-full items-center gap-2 rounded px-1.5 py-1 text-left transition-colors hover:bg-card-hover"
          >
            <div className="h-12 w-12 overflow-hidden rounded bg-surface flex-shrink-0">
              {galleryCoverSrc ? (
                <>
                  <CoverImage src={galleryCoverSrc} alt="" className="h-full w-full" loading="lazy" onError={(event) => { const image = event.currentTarget; image.style.display = "none"; const fallback = image.nextElementSibling as HTMLElement | null; if (fallback) fallback.style.display = "flex"; }} />
                  <div className="hidden h-full w-full items-center justify-center"><FolderOpen className="w-4 h-4 text-muted" /></div>
                </>
              ) : (
                <div className="flex h-full w-full items-center justify-center"><FolderOpen className="w-4 h-4 text-muted" /></div>
              )}
            </div>
            <div className="min-w-0 flex-1">
              <div className="truncate text-xs font-medium text-accent">{gallery.title || `Gallery ${gallery.id}`}</div>
              {gallery.date && <div className="truncate text-[10px] text-muted">{gallery.date}</div>}
            </div>
          </a>
        );
      })}
    </div>
  );
}

function EntityLinkIcon({ page, color }: { page: string; color?: string | null }) {
  if (page === "tag" && color) {
    return <span className="h-3 w-3 rounded-full border border-border" style={{ backgroundColor: color }} />;
  }

  const className = "h-3.5 w-3.5 shrink-0 text-muted";
  switch (page) {
    case "audio":
      return <Headphones className={className} />;
    case "gallery":
      return <ImagesIcon className={className} />;
    case "group":
      return <Layers className={className} />;
    case "image":
      return <ImagesIcon className={className} />;
    case "performer":
      return <User className={className} />;
    case "video":
      return <Film className={className} />;
    case "studio":
      return <Building2 className={className} />;
    case "tag":
      return <Tag className={className} />;
    case "text":
      return <FileText className={className} />;
    default:
      return <Link2 className={className} />;
  }
}

function EntityLinkList({ items, page, onNavigate }: { items: Array<{ id: number; label: string; color?: string | null }>; page: string; onNavigate?: (route: any) => void }) {
  return (
    <div className="space-y-1">
      {items.map((item) => {
        const navigationHandlers = createNestedEntityNavigationHandlers<HTMLAnchorElement>({ page, id: item.id }, onNavigate);
        return (
          <a key={`${page}-${item.id}`} {...navigationHandlers} className="flex items-center gap-2 rounded px-1.5 py-1 text-xs font-medium text-accent transition-colors hover:bg-card-hover hover:underline">
            <EntityLinkIcon page={page} color={item.color} />
            <span className="min-w-0 truncate">{item.label}</span>
          </a>
        );
      })}
    </div>
  );
}

function TagLinkList({ items, onNavigate }: { items: TagType[]; onNavigate?: (route: any) => void }) {
  return (
    <div className="space-y-1">
      {items.map((tag) => {
        const navigationHandlers = createNestedEntityNavigationHandlers<HTMLAnchorElement>({ page: "tag", id: tag.id }, onNavigate);
        return (
          <TagMediaHover key={`tag-${tag.id}`} tag={tag} wrapperClassName="block">
            <a {...navigationHandlers} className="flex items-center gap-2 rounded px-1.5 py-1 text-xs font-medium text-accent transition-colors hover:bg-card-hover hover:underline">
              <EntityLinkIcon page="tag" color={tag.color ?? tag.tagGroupColor} />
              <span className="min-w-0 truncate">{tag.name}</span>
            </a>
          </TagMediaHover>
        );
      })}
    </div>
  );
}

export function EntityReferencePopovers({
  performers: performerItems = [],
  tags: tagItems = [],
  groups: groupItems = [],
  studio,
  onNavigate,
  className = "",
}: {
  performers?: PerformerSummary[];
  tags?: TagType[];
  groups?: GroupSummary[];
  studio?: { id?: number | null; name?: string | null } | null;
  onNavigate?: (route: any) => void;
  className?: string;
}) {
  const studioName = studio?.name?.trim();
  const studioId = studio?.id ?? null;
  const groupLinks = groupItems.map((group) => ({ id: group.id, label: group.name }));

  if (!studioName && performerItems.length === 0 && tagItems.length === 0 && groupLinks.length === 0) {
    return null;
  }

  return (
    <div className={`relative z-[2] flex flex-wrap items-center gap-1 ${className}`} data-entity-reference-popovers>
      {studioName ? (
        <PopoverButton icon={<Building2 className="h-3.5 w-3.5" />} count={1} title="Studio" preferBelow>
          {studioId ? (
            <EntityLinkList items={[{ id: studioId, label: studioName }]} page="studio" onNavigate={onNavigate} />
          ) : (
            <div className="px-1 text-xs text-foreground">{studioName}</div>
          )}
        </PopoverButton>
      ) : null}
      {performerItems.length > 0 ? (
        <PopoverButton icon={<User className="h-3.5 w-3.5" />} count={performerItems.length} title="Performers" wide preferBelow>
          <PerformerPreviewGrid performers={performerItems} onNavigate={onNavigate} />
        </PopoverButton>
      ) : null}
      {tagItems.length > 0 ? (
        <PopoverButton icon={<Tag className="h-3.5 w-3.5" />} count={tagItems.length} title="Tags" preferBelow>
          <TagLinkList items={tagItems} onNavigate={onNavigate} />
        </PopoverButton>
      ) : null}
      {groupLinks.length > 0 ? (
        <PopoverButton icon={<Layers className="h-3.5 w-3.5" />} count={groupLinks.length} title="Groups" preferBelow>
          <EntityLinkList items={groupLinks} page="group" onNavigate={onNavigate} />
        </PopoverButton>
      ) : null}
    </div>
  );
}

function StudioCardOverlay({ studioId, studioName, selecting, onNavigate }: { studioId?: number | null; studioName?: string | null; selecting?: boolean; onNavigate?: (route: any) => void }) {
  if (!studioId || !studioName || selecting) return null;
  const navigationHandlers = createNestedEntityNavigationHandlers<HTMLAnchorElement>({ page: "studio", id: studioId }, onNavigate);
  return (
    <div data-studio-overlay className="absolute right-0 top-0 z-[5] p-1">
      <a {...navigationHandlers} aria-label={studioName} className="block">
        <img
          src={entityImages.studioImageUrl(studioId)}
          alt={studioName}
          className="h-8 w-auto max-w-[120px] object-contain drop-shadow-md"
          onError={(event) => {
            const image = event.currentTarget;
            image.style.display = "none";
            const fallback = image.nextElementSibling as HTMLElement | null;
            if (fallback) fallback.style.display = "";
          }}
        />
        <span className="rounded bg-black/60 px-1.5 py-0.5 text-xs font-medium text-white" style={{ display: "none" }}>{studioName}</span>
      </a>
    </div>
  );
}

function MediaCardPerformerBadges({ performerItems, onNavigate }: { performerItems: PerformerSummary[]; onNavigate?: (route: any) => void }) {
  if (performerItems.length === 0) return null;
  return (
    <div className="relative z-10 flex flex-wrap items-center gap-1.5 overflow-hidden">
      {performerItems.slice(0, 4).map((performer) => (
        <PerformerBadge key={performer.id} performer={performer} navigationHandlers={createNestedEntityNavigationHandlers<HTMLAnchorElement>({ page: "performer", id: performer.id }, onNavigate)} />
      ))}
      {performerItems.length > 4 ? <span className="text-[10px] text-muted">+{performerItems.length - 4}</span> : null}
    </div>
  );
}

function AudioTextCardPopovers({ hostType, hostId, performers: performerItems, tags: tagItems, groups: groupItems, engagement, organized, onNavigate }: { hostType: "audio" | "text"; hostId: number; performers: PerformerSummary[]; tags: TagType[]; groups: GroupSummary[]; engagement?: EntityEngagement; organized?: boolean; onNavigate?: (route: any) => void }) {
  const likeCount = engagement?.likeCount ?? 0;
  const hasFavorite = engagement?.isFavorite === true;
  const hasPopovers = performerItems.length > 0 || tagItems.length > 0 || groupItems.length > 0 || likeCount > 0 || hasFavorite || organized;
  return (
    <>
      <hr className="my-0 border-border/50" />
      <div className="card-popovers relative z-10 flex min-h-[28px] flex-wrap items-center justify-center gap-1 rounded-b px-2 py-1.5">
        {!hasPopovers ? <span className="select-none text-[10px] text-muted/30">&nbsp;</span> : null}
        {performerItems.length > 0 ? <PopoverButton icon={<User className="h-3.5 w-3.5" />} count={performerItems.length} title="Performers" wide preferBelow><PerformerPreviewGrid performers={performerItems} onNavigate={onNavigate} /></PopoverButton> : null}
        {tagItems.length > 0 ? <PopoverButton icon={<Tag className="h-3.5 w-3.5" />} count={tagItems.length} title="Tags" preferBelow><TagLinkList items={tagItems} onNavigate={onNavigate} /></PopoverButton> : null}
        {likeCount > 0 ? <LikeCounter count={likeCount} /> : null}
        {hasFavorite ? <CardFavoriteButton hostType={hostType} hostId={hostId} favorite /> : null}
        {groupItems.length > 0 ? <PopoverButton icon={<Layers className="h-3.5 w-3.5" />} count={groupItems.length} title="Groups" preferBelow><EntityLinkList items={groupItems.map((group) => ({ id: group.id, label: group.name }))} page="group" onNavigate={onNavigate} /></PopoverButton> : null}
        {organized ? <span className="p-1 text-muted" title="Organized"><Box className="h-3.5 w-3.5" /></span> : null}
      </div>
    </>
  );
}

// ===== PopoverButton (shared hover popover) =====

export function PopoverButton({ icon, count, title, children, wide, preferBelow }: { icon: React.ReactNode; count: number; title: string; children?: React.ReactNode; wide?: boolean; preferBelow?: boolean }) {
  const [open, setOpen] = useState(false);
  const buttonRef = useRef<HTMLDivElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const enterTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const leaveTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const [popoverStyle, setPopoverStyle] = useState<React.CSSProperties>({});

  const handleMouseEnter = useCallback(() => {
    clearTimeout(leaveTimer.current);
    enterTimer.current = setTimeout(() => {
      if (buttonRef.current) {
        const rect = buttonRef.current.getBoundingClientRect();
        const spaceBelow = window.innerHeight - rect.bottom;
        const showBelow = preferBelow ? (spaceBelow > 100) : (rect.top < 220);
        const style: React.CSSProperties = { position: "fixed", zIndex: 9999 };
        if (showBelow) { style.top = rect.bottom + 4; } else { style.bottom = window.innerHeight - rect.top + 4; }
        const centerX = rect.left + rect.width / 2;
        const popWidth = wide ? 300 : 220;
        let left = centerX - popWidth / 2;
        if (left < 8) left = 8;
        if (left + popWidth > window.innerWidth - 8) left = window.innerWidth - 8 - popWidth;
        style.left = left;
        setPopoverStyle(style);
      }
      setOpen(true);
    }, 200);
  }, [preferBelow, wide]);

  const handleMouseLeave = useCallback(() => {
    clearTimeout(enterTimer.current);
    leaveTimer.current = setTimeout(() => setOpen(false), 200);
  }, []);

  useEffect(() => () => { clearTimeout(enterTimer.current); clearTimeout(leaveTimer.current); }, []);

  return (
    <div className="relative" ref={buttonRef} onMouseEnter={handleMouseEnter} onMouseLeave={handleMouseLeave}>
      <button
        className="flex items-center gap-1 px-1.5 py-1 text-secondary hover:text-accent rounded text-xs transition-colors"
        title={title}
        onClick={(e) => e.stopPropagation()}
        onMouseDown={(e) => e.stopPropagation()}
        onAuxClick={(e) => e.stopPropagation()}
      >
        {icon}
        <span className="font-medium">{count}</span>
      </button>
      {open && children && createPortal(
        <div
          ref={popoverRef}
          style={popoverStyle}
          className={`bg-surface border border-border rounded-lg shadow-2xl shadow-black/40 p-2.5 ${wide ? "min-w-[280px] max-w-[360px]" : "min-w-[180px] max-w-[min(280px,calc(100vw-1rem))]"} max-h-[320px] overflow-y-auto`}
          onClick={(e) => e.stopPropagation()}
          onMouseEnter={() => { clearTimeout(leaveTimer.current); }}
          onMouseLeave={handleMouseLeave}
        >
          <div className="text-xs uppercase tracking-wider text-muted font-semibold mb-1.5 px-1">{title}</div>
          {children}
        </div>,
        document.body
      )}
    </div>
  );
}

// ===== Lazy video list popover content =====

export function VideosPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["videos-popover", filter],
    queryFn: () => videos.find({ perPage: 10, sort: "date", direction: "desc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">LoadingÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No videos</p>;
  return (
    <div className="space-y-1">
      {items.map((s) => (
        <div key={s.id} className="flex items-center gap-2 px-1 py-0.5 rounded hover:bg-card">
          <img src={videos.screenshotUrl(s.id, s.updatedAt)} alt="" className="w-12 h-7 rounded object-cover flex-shrink-0 bg-surface" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} />
          <span className="text-[11px] text-foreground truncate">{s.title || "Untitled"}</span>
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy image list popover content =====

export function ImagesPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["images-popover", filter],
    queryFn: () => images.find({ perPage: 10, sort: "created_at", direction: "desc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">LoadingÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No images</p>;
  return (
    <div className="grid grid-cols-3 gap-1">
      {items.map((img) => (
        <div key={img.id} className="aspect-square rounded overflow-hidden bg-surface">
          <CoverImage src={images.thumbnailUrl(img.id)} alt="" className="w-full h-full" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} />
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="col-span-3 text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy audio list popover content =====

export function AudiosPopoverContent({ filter }: { filter: AudioFilterCriteria }) {
  const { data, isLoading } = useQuery({
    queryKey: ["audios-popover", filter],
    queryFn: () => audios.findFiltered({ findFilter: { perPage: 10, sort: "created_at", direction: "desc" }, objectFilter: filter }),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">Loading...</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No audio</p>;
  return (
    <div className="space-y-1">
      {items.map((audio) => (
        <div key={audio.id} className="flex items-center gap-2 px-1 py-0.5 rounded hover:bg-card">
          <Headphones className="h-3.5 w-3.5 flex-shrink-0 text-muted" />
          <span className="truncate text-[11px] text-foreground">{getAudioDisplayTitle(audio)}</span>
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy text list popover content =====

export function TextsPopoverContent({ filter }: { filter: TextFilterCriteria }) {
  const { data, isLoading } = useQuery({
    queryKey: ["texts-popover", filter],
    queryFn: () => texts.findFiltered({ findFilter: { perPage: 10, sort: "created_at", direction: "desc" }, objectFilter: filter }),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">Loading...</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No texts</p>;
  return (
    <div className="space-y-1">
      {items.map((text) => (
        <div key={text.id} className="flex items-center gap-2 px-1 py-0.5 rounded hover:bg-card">
          <FileText className="h-3.5 w-3.5 flex-shrink-0 text-muted" />
          <span className="truncate text-[11px] text-foreground">{getTextDisplayTitle(text)}</span>
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy performer list popover content =====

export function PerformersPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["performers-popover", filter],
    queryFn: () => performers.find({ perPage: 10, sort: "name", direction: "asc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">LoadingÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No performers</p>;
  return <PerformerPreviewGrid performers={items} />;
}

// ===== Lazy gallery list popover content =====

export function GalleriesPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["galleries-popover", filter],
    queryFn: () => galleries.find({ perPage: 10, sort: "title", direction: "asc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">LoadingÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No galleries</p>;
  return <GalleryPreviewList galleries={items} />;
}

// ===== Lazy studio list popover content =====

export function StudiosPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["studios-popover", filter],
    queryFn: () => studios.find({ perPage: 10, sort: "name", direction: "asc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">LoadingÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No studios</p>;
  return (
    <div className="space-y-1">
      {items.map((s) => {
        const navigationHandlers = createNestedEntityNavigationHandlers<HTMLAnchorElement>({ page: "studio", id: s.id });

        return (
          <a
            key={s.id}
            {...navigationHandlers}
            className="flex items-center gap-2 px-1 py-0.5 rounded hover:bg-card"
          >
            {s.imagePath ? <img src={s.imagePath} alt="" className="w-10 h-7 rounded object-contain flex-shrink-0 bg-surface" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} /> : <Building2 className="w-4 h-4 text-muted flex-shrink-0" />}
            <span className="text-[11px] text-accent hover:underline truncate">{s.name}</span>
          </a>
        );
      })}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== Lazy group list popover content =====

export function GroupsPopoverContent({ filter }: { filter: Record<string, string | number> }) {
  const { data, isLoading } = useQuery({
    queryKey: ["groups-popover", filter],
    queryFn: () => groups.find({ perPage: 10, sort: "name", direction: "asc" }, filter),
  });
  if (isLoading) return <p className="text-[11px] text-muted px-1">LoadingÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</p>;
  const items = data?.items ?? [];
  if (items.length === 0) return <p className="text-[11px] text-muted px-1">No groups</p>;
  return (
    <div className="space-y-1">
      {items.map((g) => (
        <div key={g.id} className="flex items-center gap-2 px-1 py-0.5 rounded hover:bg-card">
          {g.frontImagePath ? <CoverImage src={g.frontImagePath} alt="" className="w-7 h-10 rounded flex-shrink-0 bg-surface" loading="lazy" onError={(e) => { (e.target as HTMLImageElement).style.display = "none"; }} /> : <Layers className="w-4 h-4 text-muted flex-shrink-0" />}
          <span className="text-[11px] text-foreground truncate">{g.name}</span>
        </div>
      ))}
      {(data?.totalCount ?? 0) > 10 && (
        <p className="text-[10px] text-muted px-1 pt-0.5">+ {(data!.totalCount) - 10} more</p>
      )}
    </div>
  );
}

// ===== VideoCardPopovers =====

export function VideoCardPopovers({ video, engagement, onNavigate }: { video: Video; engagement?: EntityEngagement; onNavigate?: (r: any) => void }) {
  const likeCount = engagement?.likeCount ?? 0;
  const hasFavorite = engagement?.isFavorite === true;
  const hasPopovers =
    video.tags.length > 0 || video.performers.length > 0 || video.groups.length > 0 ||
    video.galleries.length > 0 || likeCount > 0 || hasFavorite || video.organized;
  return (
    <>
      <hr className="border-border/50 my-0" />
      <div className="relative z-10 flex flex-wrap items-center justify-center gap-1 px-2 py-1.5 rounded-b card-popovers min-h-[28px]">
        {!hasPopovers && <span className="text-[10px] text-muted/30 select-none">&nbsp;</span>}
        {video.performers.length > 0 && (
          <PopoverButton icon={<User className="w-3.5 h-3.5" />} count={video.performers.length} title="Performers" wide preferBelow>
            <PerformerPreviewGrid performers={video.performers} onNavigate={onNavigate} />
          </PopoverButton>
        )}
        {video.tags.length > 0 && (
          <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={video.tags.length} title="Tags" preferBelow>
            <TagLinkList items={video.tags} onNavigate={onNavigate} />
          </PopoverButton>
        )}
        {likeCount > 0 && (
          <LikeCounter count={likeCount} />
        )}
        {hasFavorite ? (
          <CardFavoriteButton hostType="video" hostId={video.id} favorite={engagement?.isFavorite ?? false} />
        ) : null}
        {video.groups.length > 0 && (
          <PopoverButton icon={<Layers className="w-3.5 h-3.5" />} count={video.groups.length} title="Groups" preferBelow>
            <EntityLinkList items={video.groups.map((group: any) => ({ id: group.id, label: group.name }))} page="group" onNavigate={onNavigate} />
          </PopoverButton>
        )}
        {video.galleries.length > 0 && (
          <PopoverButton icon={<ImagesIcon className="w-3.5 h-3.5" />} count={video.galleries.length} title="Galleries" preferBelow>
            <EntityLinkList items={video.galleries.map((gallery: any) => ({ id: gallery.id, label: gallery.title || "Untitled" }))} page="gallery" onNavigate={onNavigate} />
          </PopoverButton>
        )}
        {video.organized && (
          <span className="p-1 text-muted" title="Organized"><Box className="w-3.5 h-3.5" /></span>
        )}
      </div>
    </>
  );
}

// ===== PerformerBadge (hover popover with performer image) =====

function PerformerBadge({
  performer,
  navigationHandlers,
}: {
  performer: { id: number; name: string; imagePath?: string | null };
  navigationHandlers: ReturnType<typeof createNestedRouteLinkProps<HTMLAnchorElement>>;
}) {
  const badgeRef = useRef<HTMLAnchorElement>(null);
  const [hover, setHover] = useState(false);
  const [style, setStyle] = useState<React.CSSProperties>({});
  const enterTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const leaveTimer = useRef<ReturnType<typeof setTimeout>>(undefined);

  const onEnter = useCallback(() => {
    clearTimeout(leaveTimer.current);
    enterTimer.current = setTimeout(() => {
      if (badgeRef.current) {
        const rect = badgeRef.current.getBoundingClientRect();
        const s: React.CSSProperties = { position: "fixed", zIndex: 9999 };
        const spaceBelow = window.innerHeight - rect.bottom;
        if (spaceBelow > 180) { s.top = rect.bottom + 4; } else { s.bottom = window.innerHeight - rect.top + 4; }
        let left = rect.left + rect.width / 2 - 64;
        if (left < 8) left = 8;
        if (left + 128 > window.innerWidth - 8) left = window.innerWidth - 136;
        s.left = left;
        setStyle(s);
      }
      setHover(true);
    }, 300);
  }, []);

  const onLeave = useCallback(() => {
    clearTimeout(enterTimer.current);
    leaveTimer.current = setTimeout(() => setHover(false), 200);
  }, []);

  useEffect(() => () => { clearTimeout(enterTimer.current); clearTimeout(leaveTimer.current); }, []);

  return (
    <>
      <a ref={badgeRef} {...navigationHandlers} onMouseEnter={onEnter} onMouseLeave={onLeave}
        className="performer-badge flex items-center gap-1 rounded-full border border-border bg-surface px-1.5 py-0.5 min-w-0 hover:border-accent/50 transition-colors">
        {performer.imagePath ? (
          <CoverImage src={performer.imagePath} alt="" className="h-4 w-4 rounded-full flex-shrink-0" loading="lazy" />
        ) : (
          <User className="h-3.5 w-3.5 text-muted flex-shrink-0" />
        )}
        <span className="max-w-[80px] truncate text-[10px] text-secondary hover:text-accent">{performer.name}</span>
      </a>
      {hover && createPortal(
        <div style={style}
          className="bg-surface border border-border rounded-lg shadow-2xl shadow-black/40 p-2 w-[128px]"
          onClick={(e) => e.stopPropagation()}
          onMouseEnter={() => clearTimeout(leaveTimer.current)}
          onMouseLeave={onLeave}
        >
          <div className="w-full aspect-[2/3] rounded overflow-hidden bg-card mb-1.5">
            {performer.imagePath ? (
              <CoverImage src={performer.imagePath} alt="" className="w-full h-full" loading="lazy" />
            ) : (
              <div className="w-full h-full flex items-center justify-center"><User className="w-8 h-8 text-muted" /></div>
            )}
          </div>
          <p className="text-xs text-foreground font-medium text-center truncate">{performer.name}</p>
        </div>,
        document.body
      )}
    </>
  );
}

// ===== PerformerBadgeRow (reusable wrap of small performer badges for hero/detail headers) =====

export function PerformerBadgeRow({
  performers,
  onNavigate,
  max = 12,
  className = "",
}: {
  performers: Array<{ id: number; name: string; imagePath?: string | null }>;
  onNavigate?: (route: any) => void;
  max?: number;
  className?: string;
}) {
  if (!performers.length) return null;
  const shown = performers.slice(0, max);
  return (
    <div className={`flex flex-wrap items-center gap-1.5 ${className}`}>
      {shown.map((performer) => {
        const navigationHandlers = createNestedEntityNavigationHandlers<HTMLAnchorElement>({ page: "performer", id: performer.id }, onNavigate);
        return <PerformerBadge key={performer.id} performer={performer} navigationHandlers={navigationHandlers} />;
      })}
      {performers.length > max && <span className="text-[10px] text-muted">+{performers.length - max}</span>}
    </div>
  );
}


// ===== EntityRefBadge (face-list style reference badge with image thumbnail) =====

export function EntityRefBadge({
  imageUrl,
  label,
  sublabel,
  icon,
  route,
  onNavigate,
}: {
  imageUrl?: string | null;
  label: string;
  sublabel?: ReactNode;
  icon: ReactNode;
  route: { page: string; id: number };
  onNavigate?: (route: any) => void;
}) {
  const [failed, setFailed] = useState(false);
  const showImage = Boolean(imageUrl) && !failed;
  const navigationHandlers = createRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate?.(route));
  return (
    <a
      {...navigationHandlers}
      className="flex items-center gap-2.5 rounded-lg border border-border bg-card px-2 py-1.5 min-w-0 transition-colors hover:border-accent/60"
    >
      <div className="flex h-11 w-11 shrink-0 items-center justify-center overflow-hidden rounded-md bg-surface text-muted">
        {showImage ? (
          <CoverImage src={imageUrl ?? undefined} alt="" className="h-full w-full" loading="lazy" onError={() => setFailed(true)} />
        ) : (
          icon
        )}
      </div>
      <div className="min-w-0">
        <div className="truncate text-sm font-medium text-foreground">{label}</div>
        {sublabel ? <div className="mt-0.5 truncate text-xs text-secondary">{sublabel}</div> : null}
      </div>
    </a>
  );
}

// ===== StudioHeaderImage (shared studio logo shown above detail titles) =====
export function StudioHeaderImage({ studioId, studioName, onNavigate }: { studioId?: number | null; studioName?: string | null; onNavigate?: (route: any) => void }) {
  if (!studioId) return null;
  return (
    <button type="button" onClick={() => onNavigate?.({ page: "studio", id: studioId })} className="block" title={studioName || "Studio"}>
      <img
        src={entityImages.studioImageUrl(studioId)}
        alt={studioName || "Studio"}
        className="h-20 w-auto max-w-full object-contain"
        onError={(event) => { (event.target as HTMLImageElement).style.display = "none"; }}
      />
    </button>
  );
}

// ===== MediaStudioSubtitle (shared date + studio link subtitle for detail pages) =====
export function MediaStudioSubtitle({ date, studioId, studioName, fieldProvenance, onNavigate, canReadStudio = true, extra }: { date?: string | null; studioId?: number | null; studioName?: string | null; fieldProvenance?: FieldProvenance[]; onNavigate?: (route: any) => void; canReadStudio?: boolean; extra?: ReactNode }) {
  const hasStudio = Boolean(studioName && studioId);
  if (!date && !hasStudio && !extra) return null;
  return (
    <div className="flex flex-wrap items-center gap-3 text-sm text-secondary">
      {date ? <FieldProvenanceHover fieldProvenance={fieldProvenance} fieldKey="date"><span>{formatDate(date)}</span></FieldProvenanceHover> : null}
      {hasStudio ? (
        canReadStudio ? (
          <FieldProvenanceHover fieldProvenance={fieldProvenance} fieldKey="studio">
            <button type="button" onClick={() => onNavigate?.({ page: "studio", id: studioId! })} className="font-medium text-accent hover:underline">{studioName}</button>
          </FieldProvenanceHover>
        ) : (
          <FieldProvenanceHover fieldProvenance={fieldProvenance} fieldKey="studio"><span>{studioName}</span></FieldProvenanceHover>
        )
      ) : null}
      {extra}
    </div>
  );
}

// ===== VideoCard (redesigned - cleaner, performer badges, 2-line title) =====
export function VideoCard({ video, engagement, onClick, selected, onSelect, onNavigate, selecting, onQuickView, bookmarkInitiallySaved }: { video: Video; engagement?: EntityEngagement; onClick: (options?: MultiSelectToggleOptions) => void; selected?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; selecting?: boolean; onNavigate?: (r: any) => void; onQuickView?: () => void; bookmarkInitiallySaved?: boolean }) {
  const appConfig = useOptionalAppConfig();
  const file = video.files[0];
  const clipDuration = typeof video.clipStartSec === "number" && typeof video.clipEndSec === "number"
    ? Math.max(0, video.clipEndSec - video.clipStartSec)
    : undefined;
  const duration = clipDuration ?? file?.duration ?? 0;
  const resLabel = file ? getResolutionLabel(file.width, file.height) : null;
  const visibleResumeTime = typeof video.clipStartSec === "number" && typeof engagement?.resumeTime === "number"
    ? Math.max(0, engagement.resumeTime - video.clipStartSec)
    : engagement?.resumeTime;
  const progressPercent = duration > 0 && visibleResumeTime ? Math.min(100, (visibleResumeTime / duration) * 100) : 0;
  const cardTitle = video.title || file?.basename || "Untitled";
  const videoPreviewObjectFit = appConfig?.config?.ui.videoObjectFit === "contain" ? "contain" : "cover";

  return (
    <div onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined} className={`video-card relative cursor-pointer group rounded border bg-card overflow-hidden flex flex-col h-full ${selected ? "ring-2 ring-accent border-accent" : "border-border"}`}>
      <RouteCardLinkOverlay route={{ page: "video", id: video.id }} onClick={onClick} label={`Open video ${cardTitle}`} disabled={selecting} selectionSafeZone={selected !== undefined || selecting} />
      <VideoPreviewThumbnail video={video} fit={videoPreviewObjectFit} enableScrubbing={!selecting}>
        {(selected !== undefined || selecting) && <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />}
        {!selecting && (
          <BookmarkButton
            hostType="video"
            hostId={video.id}
            compact
            deferUntilHover
            initialSaved={bookmarkInitiallySaved}
            className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100"
          />
        )}
        <StudioCardOverlay studioId={video.studioId} studioName={video.studioName} selecting={selecting} onNavigate={onNavigate} />
        {(duration > 0 || resLabel) && (
          <div className="video-specs-overlay absolute bottom-0 right-0 flex items-center gap-0.5 px-1.5 py-1 text-xs text-white z-[5] transition-opacity">
            {file && <span className="bg-black/70 px-1 py-0.5 rounded extra-video-info hidden">{formatFileSize(file.size)}</span>}
            {resLabel && <span className="bg-black/70 px-1 py-0.5 rounded font-black uppercase">{resLabel}</span>}
            {duration > 0 && <span className="bg-black/70 px-1 py-0.5 rounded">{formatDuration(duration)}</span>}
          </div>
        )}
        {onQuickView && (
          <button
            onClick={(e) => { e.stopPropagation(); onQuickView(); }}
            className="absolute bottom-1 left-1 z-10 opacity-0 group-hover:opacity-100 transition-opacity p-1 rounded bg-black/60 text-white hover:bg-black/80"
            title="Quick View"
          >
            <Eye className="w-3.5 h-3.5" />
          </button>
        )}
        {progressPercent > 0 && (
          <div className="absolute bottom-0 left-0 right-0 h-[3px] bg-black/40 z-[6]"><div className="h-full bg-accent" style={{ width: `${progressPercent}%` }} /></div>
        )}
        <RatingBanner rating={engagement?.rating} />
      </VideoPreviewThumbnail>
      <div className="card-body px-2.5 pt-2 pb-2 border-t border-border/50 flex-1 flex flex-col gap-1.5 min-h-0">
        <div>
          <p className="card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent transition-colors leading-snug" title={cardTitle}>
            {cardTitle}
          </p>
          <div className="mt-1 flex items-center gap-2 text-[11px] text-muted">
            {video.date && <span>{video.date}</span>}
            {video.studioName && <span className="truncate">{video.studioName}</span>}
          </div>
        </div>
        <MediaCardPerformerBadges performerItems={video.performers} onNavigate={onNavigate} />
        {video.details && <p className="text-xs text-secondary line-clamp-2 leading-snug">{video.details}</p>}
      </div>
      <CardExtensionSlot slot="video-card-content" context={{ video }} />
      <VideoCardPopovers video={video} engagement={engagement} onNavigate={onNavigate} />
    </div>
  );
}

// ===== VideoTile =====

interface VideoTileProps {
  video: Video;
  onClick: () => void;
}

export function VideoTile({ video, onClick }: VideoTileProps) {
  const file = video.files[0];
  const clipDuration = typeof video.clipStartSec === "number" && typeof video.clipEndSec === "number"
    ? Math.max(0, video.clipEndSec - video.clipStartSec)
    : undefined;
  const duration = clipDuration ?? file?.duration ?? 0;
  const resLabel = file ? getResolutionLabel(file.width, file.height) : null;
  const coverUrl = entityImages.videoCoverUrl(video.id, video.updatedAt, 960);
  const coverAlt = video.imagePath ? video.title || "" : "";
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "video", id: video.id }, onClick);

  return (
    <a {...linkProps} className="group text-left">
      <div className="relative aspect-video overflow-hidden rounded-lg border border-border bg-card shadow-md shadow-black/30">
        <EntityMedia
          entityType="video"
          entityId={video.id}
          surface="card"
          imageUrl={coverUrl}
          alt={coverAlt}
          fit="cover"
          loading="lazy"
          className="h-full w-full object-cover"
          renderDefault={() => (
            <img
              src={coverUrl}
              alt={coverAlt}
              className="h-full w-full object-cover"
              loading="lazy"
            />
          )}
        />
        {duration > 0 && <span className="absolute bottom-1.5 right-1.5 rounded bg-black/75 px-1.5 py-0.5 text-[11px] text-white">{formatDuration(duration)}</span>}
        {resLabel && <span className="absolute top-1.5 right-1.5 rounded bg-black/75 px-1.5 py-0.5 text-[10px] font-bold uppercase text-accent">{resLabel}</span>}
        <RatingBanner rating={undefined} />
      </div>
      <div className="pt-2">
        <p className="card-title font-medium text-foreground line-clamp-2 group-hover:text-accent">{video.title || "Untitled"}</p>
        <p className="mt-0.5 truncate text-xs text-secondary">{video.date || video.studioName || ""}</p>
      </div>
    </a>
  );
}

// ===== PerformerTile =====

interface PerformerTileEntity {
  id: number;
  name: string;
  imagePath?: string | null;
  country?: string;
  birthdate?: string;
  favorite?: boolean;
  tags?: Array<{ id: number; name: string }>;
  videoCount?: number;
  imageCount?: number;
  galleryCount?: number;
  audioCount?: number;
  textCount?: number;
  groupCount?: number;
  likeCount?: number;
}

interface PerformerTileProps {
  performer: PerformerTileEntity;
  onClick: (options?: MultiSelectToggleOptions) => void;
  onNavigate?: (r: any) => void;
  children?: ReactNode;
  selected?: boolean;
  onSelect?: (options?: MultiSelectToggleOptions) => void;
  selecting?: boolean;
}

export function PerformerTile({ performer, engagement, onClick, onNavigate, children, selected, onSelect, selecting }: PerformerTileProps & { engagement?: EntityEngagement }) {
  const imageFit = useConfiguredImageFit();
  const videoCount = performer.videoCount ?? 0;
  const imageCount = performer.imageCount ?? 0;
  const galleryCount = performer.galleryCount ?? 0;
  const audioCount = performer.audioCount ?? 0;
  const textCount = performer.textCount ?? 0;
  const groupCount = performer.groupCount ?? 0;
  const likeCount = performer.likeCount ?? 0;
  const performerImageUrl = performer.imagePath || null;
  const hasFooter = (performer.tags?.length ?? 0) > 0 || videoCount > 0 || imageCount > 0 || galleryCount > 0 || audioCount > 0 || textCount > 0 || groupCount > 0 || likeCount > 0;

  return (
    <EntityTileFrame
      route={{ page: "performer", id: performer.id }}
      label={`Open performer ${performer.name}`}
      onClick={onClick}
      selected={selected}
      onSelect={onSelect}
      selecting={selecting}
      mediaClassName="aspect-[2/3] bg-gradient-to-b from-card to-surface"
      bodyClassName="p-2.5"
      media={(
        <>
          <EntityMedia
            entityType="performer"
            entityId={performer.id}
            surface="card"
            imageUrl={performerImageUrl}
            alt={performer.name}
            fit={imageFit}
            loading="lazy"
            className="h-full w-full"
            renderDefault={() => performerImageUrl ? (
              <>
                <CoverImage src={performerImageUrl} alt={performer.name} className="h-full w-full" loading="lazy" onError={(event) => { const image = event.currentTarget; image.style.display = "none"; const fallback = image.nextElementSibling as HTMLElement | null; if (fallback) fallback.style.display = "flex"; }} />
                <div className="hidden h-full w-full items-center justify-center"><User className="h-12 w-12 text-muted" /></div>
              </>
            ) : (
              <div className="flex h-full w-full items-center justify-center"><User className="h-12 w-12 text-muted" /></div>
            )}
          />
          <RatingBanner rating={engagement?.rating} />
          {performer.favorite ? <Heart className="absolute right-1.5 top-1.5 z-[5] h-4 w-4 fill-red-500 text-red-500 drop-shadow-md" /> : null}
        </>
      )}
      body={(
        <>
          <p className="card-title line-clamp-2 font-semibold text-foreground group-hover:text-accent">{performer.name}</p>
          {(performer.country || performer.birthdate) ? (
            <div className="flex items-center gap-2 text-[11px] text-muted">
              {performer.country ? <span>{performer.country}</span> : null}
              {performer.birthdate ? <span>{performer.birthdate}</span> : null}
            </div>
          ) : null}
        </>
      )}
      footer={hasFooter ? (
        <>
            {performer.tags && performer.tags.length > 0 && (
              <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={performer.tags.length} title="Tags" preferBelow>
                <div className="flex flex-wrap gap-1">
                  {performer.tags.map((t: any) => {
                    const navigationHandlers = createNestedEntityNavigationHandlers<HTMLAnchorElement>({ page: "tag", id: t.id }, onNavigate);

                    return (
                    <a key={t.id} {...navigationHandlers}
                      className="text-[11px] text-accent hover:underline cursor-pointer px-1.5 py-0.5 rounded bg-card border border-border hover:border-accent/40 transition-colors whitespace-nowrap">
                      {t.name}
                    </a>
                  );})}
                </div>
              </PopoverButton>
            )}
            {videoCount > 0 && (
              <PopoverButton icon={<Film className="w-3.5 h-3.5" />} count={videoCount} title="Videos" wide preferBelow>
                <VideosPopoverContent filter={{ performerIds: String(performer.id) }} />
              </PopoverButton>
            )}
            {imageCount > 0 && (
              <PopoverButton icon={<ImagesIcon className="w-3.5 h-3.5" />} count={imageCount} title="Images" wide preferBelow>
                <ImagesPopoverContent filter={{ performerIds: String(performer.id) }} />
              </PopoverButton>
            )}
            {galleryCount > 0 && (
              <PopoverButton icon={<FolderOpen className="w-3.5 h-3.5" />} count={galleryCount} title="Galleries" wide preferBelow>
                <GalleriesPopoverContent filter={{ performerIds: String(performer.id) }} />
              </PopoverButton>
            )}
            {audioCount > 0 && (
              <PopoverButton icon={<Headphones className="w-3.5 h-3.5" />} count={audioCount} title="Audio" wide preferBelow>
                <AudiosPopoverContent filter={{ performersCriterion: { modifier: "INCLUDES", value: [performer.id] } }} />
              </PopoverButton>
            )}
            {textCount > 0 && (
              <PopoverButton icon={<FileText className="w-3.5 h-3.5" />} count={textCount} title="Texts" wide preferBelow>
                <TextsPopoverContent filter={{ performersCriterion: { modifier: "INCLUDES", value: [performer.id] } }} />
              </PopoverButton>
            )}
            {groupCount > 0 ? <span className="flex items-center gap-0.5 text-xs text-muted px-1" title="Groups"><Layers className="w-3 h-3" /> {groupCount}</span> : null}
            {likeCount > 0 ? <LikeCounter count={likeCount} /> : null}
        </>
      ) : null}
      extensionClassName="px-2 py-2"
    >
      {children}
    </EntityTileFrame>
  );
}

// ===== StudioTile =====

interface StudioTileProps {
  studio: Studio;
  onClick: (options?: MultiSelectToggleOptions) => void;
  onNavigate?: (r: any) => void;
  children?: ReactNode;
  selected?: boolean;
  onSelect?: (options?: MultiSelectToggleOptions) => void;
  selecting?: boolean;
}

export function StudioTile({ studio, engagement, onClick, onNavigate, children, selected, onSelect, selecting }: StudioTileProps & { engagement?: EntityEngagement }) {
  const hasFooter = studio.tags.length > 0 || studio.videoCount > 0 || studio.performerCount > 0 || studio.imageCount > 0 || studio.galleryCount > 0 || studio.groupCount > 0 || studio.childStudioCount > 0 || studio.audioCount > 0 || studio.textCount > 0;

  return (
    <EntityTileFrame
      route={{ page: "studio", id: studio.id }}
      label={`Open studio ${studio.name}`}
      onClick={onClick}
      selected={selected}
      onSelect={onSelect}
      selecting={selecting}
      media={(
        <>
          <EntityMedia
            entityType="studio"
            entityId={studio.id}
            surface="card"
            imageUrl={studio.imagePath ?? null}
            alt={studio.name}
            fit="contain"
            loading="lazy"
            className="box-border h-full w-full p-4"
            renderDefault={() => studio.imagePath ? (
              <>
                <img
                  src={studio.imagePath}
                  alt={studio.name}
                  className="box-border h-full w-full object-contain p-4"
                  loading="lazy"
                  onError={(event) => {
                    const image = event.currentTarget;
                    image.style.display = "none";
                    const fallback = image.nextElementSibling as HTMLElement | null;
                    if (fallback) fallback.style.display = "flex";
                  }}
                />
                <div className="hidden h-full w-full items-center justify-center">
                  <Building2 className="h-10 w-10 text-muted" />
                </div>
              </>
            ) : (
              <Building2 className="h-10 w-10 text-muted" />
            )}
          />
          <RatingBanner rating={engagement?.rating} />
          {studio.favorite ? <Heart className="absolute right-1.5 top-1.5 z-[5] h-4 w-4 fill-red-500 text-red-500 drop-shadow-md" /> : null}
        </>
      )}
      body={(
        <>
          <p className="card-title line-clamp-2 font-semibold text-foreground group-hover:text-accent">{studio.name}</p>
          {studio.parentName ? <p className="truncate text-xs text-secondary">{studio.parentName}</p> : null}
        </>
      )}
      footer={hasFooter ? (
        <>
            {studio.tags.length > 0 ? (
              <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={studio.tags.length} title="Tags" preferBelow>
                <EntityLinkList items={studio.tags.map((tag) => ({ id: tag.id, label: tag.name, color: tag.color ?? tag.tagGroupColor }))} page="tag" onNavigate={onNavigate} />
              </PopoverButton>
            ) : null}
            {studio.videoCount > 0 && (
              <PopoverButton icon={<Film className="w-3.5 h-3.5" />} count={studio.videoCount} title="Videos" wide preferBelow>
                <VideosPopoverContent filter={{ studioId: studio.id }} />
              </PopoverButton>
            )}
            {studio.performerCount > 0 && (
              <PopoverButton icon={<User className="w-3.5 h-3.5" />} count={studio.performerCount} title="Performers" wide preferBelow>
                <PerformersPopoverContent filter={{ studioId: studio.id }} />
              </PopoverButton>
            )}
            {studio.imageCount > 0 && (
              <PopoverButton icon={<ImagesIcon className="w-3.5 h-3.5" />} count={studio.imageCount} title="Images" wide preferBelow>
                <ImagesPopoverContent filter={{ studioId: studio.id }} />
              </PopoverButton>
            )}
            {studio.galleryCount > 0 && (
              <PopoverButton icon={<FolderOpen className="w-3.5 h-3.5" />} count={studio.galleryCount} title="Galleries" wide preferBelow>
                <GalleriesPopoverContent filter={{ studioId: studio.id }} />
              </PopoverButton>
            )}
            {studio.groupCount > 0 && (
              <PopoverButton icon={<Layers className="w-3.5 h-3.5" />} count={studio.groupCount} title="Groups" wide preferBelow>
                <GroupsPopoverContent filter={{ studioId: studio.id }} />
              </PopoverButton>
            )}
            {studio.childStudioCount > 0 && (
              <PopoverButton icon={<Building2 className="w-3.5 h-3.5" />} count={studio.childStudioCount} title="Sub-studios" wide preferBelow>
                <StudiosPopoverContent filter={{ parentId: studio.id }} />
              </PopoverButton>
            )}
            {studio.audioCount > 0 && (
              <PopoverButton icon={<Headphones className="w-3.5 h-3.5" />} count={studio.audioCount} title="Audios" wide preferBelow>
                <AudiosPopoverContent filter={{ studiosCriterion: { modifier: "INCLUDES", value: [studio.id] } }} />
              </PopoverButton>
            )}
            {studio.textCount > 0 && (
              <PopoverButton icon={<FileText className="w-3.5 h-3.5" />} count={studio.textCount} title="Texts" wide preferBelow>
                <TextsPopoverContent filter={{ studiosCriterion: { modifier: "INCLUDES", value: [studio.id] } }} />
              </PopoverButton>
            )}
        </>
      ) : null}
    >
      {children}
    </EntityTileFrame>
  );
}

// ===== ImageTile =====

interface ImageTileProps {
  image: Image;
  onClick: (options?: MultiSelectToggleOptions) => void;
  onPreview?: (options?: MultiSelectToggleOptions) => void;
  onDetails?: (options?: MultiSelectToggleOptions) => void;
  onNavigate?: (r: any) => void;
  onQuickView?: () => void;
  selected?: boolean;
  onSelect?: (options?: MultiSelectToggleOptions) => void;
  selecting?: boolean;
  bookmarkInitiallySaved?: boolean;
}

export function ImageTile({ image, engagement, onClick, onPreview, onDetails, onNavigate, onQuickView, selected, onSelect, selecting, bookmarkInitiallySaved }: ImageTileProps & { engagement?: EntityEngagement }) {
  const imageFit = useConfiguredImageFit();
  const likeCount = engagement?.likeCount ?? 0;
  const hasFavorite = engagement?.isFavorite === true;
  const imageGroups = image.groups ?? [];
  const hasFooter = (image.tags?.length ?? 0) > 0 || (image.performers?.length ?? 0) > 0 || (image.galleries?.length ?? 0) > 0 || imageGroups.length > 0 || likeCount > 0 || hasFavorite || image.organized;
  const displayTitle = getImageDisplayTitle(image);
  const imageUrl = images.thumbnailUrl(image.id);
  const detailsClick = onDetails ?? onClick;
  const previewClick = onPreview ?? detailsClick;
  return (
    <div onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined} className={`entity-card group relative cursor-pointer overflow-hidden rounded-lg border bg-card text-left shadow-md shadow-black/20 flex flex-col h-full transition-colors ${selected ? "ring-2 ring-accent border-accent" : "border-border hover:border-accent/60"}`}>
      {!onPreview ? <RouteCardLinkOverlay route={{ page: "image", id: image.id }} onClick={detailsClick} label={`Open image ${displayTitle}`} disabled={selecting} selectionSafeZone={selected !== undefined || selecting} /> : null}
      <div className="card-media aspect-square overflow-hidden bg-surface relative" onClick={selecting ? undefined : () => previewClick()}>
        <EntityMedia
          entityType="image"
          entityId={image.id}
          surface="card"
          imageUrl={imageUrl}
          alt={displayTitle}
          fit={imageFit}
          loading="lazy"
          className="h-full w-full"
          renderDefault={() => <CoverImage src={imageUrl} alt={displayTitle} className="h-full w-full" loading="lazy" />}
        />
        <RatingBanner rating={engagement?.rating} />
        {(selected !== undefined || selecting) && <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />}
        {!selecting && (
          <BookmarkButton
            hostType="image"
            hostId={image.id}
            compact
            deferUntilHover
            initialSaved={bookmarkInitiallySaved}
            className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100"
          />
        )}
        {image.studioName && (
          <div className="absolute top-1 right-1 text-[10px] bg-black/70 px-1 py-0.5 rounded text-white truncate max-w-[80%]">{image.studioName}</div>
        )}
        {!selecting && onQuickView && (
          <button
            onClick={(e) => { e.stopPropagation(); onQuickView(); }}
            className="absolute bottom-1 left-1 z-10 opacity-0 group-hover:opacity-100 transition-opacity p-1 rounded bg-black/60 text-white hover:bg-black/80"
            title="Quick View"
          >
            <Eye className="w-3.5 h-3.5" />
          </button>
        )}
      </div>
      <div className="card-body border-t border-border/50 p-2 flex-1 flex flex-col gap-1">
        {!selecting && onPreview ? (
          <a {...createRouteLinkProps<HTMLAnchorElement>({ page: "image", id: image.id }, detailsClick)} className="relative z-10 card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent">
            {displayTitle}
          </a>
        ) : (
          <p className="card-title font-semibold text-foreground line-clamp-2 group-hover:text-accent">{displayTitle}</p>
        )}
      </div>
      {hasFooter && (
        <>
          <hr className="border-border/50 my-0" />
          <div className="relative z-10 flex flex-wrap items-center justify-center gap-1 px-2 py-1.5 rounded-b card-popovers min-h-[28px]">
            {(image.tags?.length ?? 0) > 0 && (
              <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={image.tags.length} title="Tags" preferBelow>
                <TagLinkList items={image.tags} onNavigate={onNavigate} />
              </PopoverButton>
            )}
            {(image.performers?.length ?? 0) > 0 && (
              <PopoverButton icon={<User className="w-3.5 h-3.5" />} count={image.performers.length} title="Performers" wide preferBelow>
                <PerformerPreviewGrid performers={image.performers} onNavigate={onNavigate} />
              </PopoverButton>
            )}
            {image.galleryCount > 0 && (
              <PopoverButton icon={<FolderOpen className="w-3.5 h-3.5" />} count={image.galleryCount} title="Galleries" wide preferBelow>
                <GalleriesPopoverContent filter={{ imageId: image.id }} />
              </PopoverButton>
            )}
            {imageGroups.length > 0 ? (
              <PopoverButton icon={<Layers className="w-3.5 h-3.5" />} count={imageGroups.length} title="Groups" preferBelow>
                <EntityLinkList items={imageGroups.map((group) => ({ id: group.id, label: group.name }))} page="group" onNavigate={onNavigate} />
              </PopoverButton>
            ) : null}
            {likeCount > 0 && (
              <LikeCounter count={likeCount} />
            )}
            {hasFavorite ? (
              <CardFavoriteButton hostType="image" hostId={image.id} favorite={engagement?.isFavorite ?? false} />
            ) : null}
            {image.organized && (
              <span className="p-1 text-muted" title="Organized"><Box className="w-3.5 h-3.5" /></span>
            )}
          </div>
        </>
      )}
    </div>
  );
}

// ===== GalleryTile =====

interface GalleryTileProps {
  gallery: Gallery;
  onClick: (options?: MultiSelectToggleOptions) => void;
  onNavigate?: (r: any) => void;
  selected?: boolean;
  onSelect?: (options?: MultiSelectToggleOptions) => void;
  selecting?: boolean;
  bookmarkInitiallySaved?: boolean;
}

export function GalleryTile({ gallery, engagement, onClick, onNavigate, selected, onSelect, selecting, bookmarkInitiallySaved }: GalleryTileProps & { engagement?: EntityEngagement }) {
  const imageFit = useConfiguredImageFit();
  const likeCount = engagement?.likeCount ?? 0;
  const hasFooter = likeCount > 0 || gallery.imageCount > 0 || gallery.videoCount > 0 || gallery.tags.length > 0 || gallery.performers.length > 0 || Boolean(gallery.studioName) || gallery.organized;
  const title = getGalleryDisplayTitle(gallery);
  const galleryCoverSrc = gallery.coverPath ?? galleries.coverUrl(gallery.id, gallery.updatedAt, 960);

  return (
    <EntityTileFrame
      route={{ page: "gallery", id: gallery.id }}
      label={`Open gallery ${title}`}
      onClick={onClick}
      selected={selected}
      onSelect={onSelect}
      selecting={selecting}
      mediaClassName="aspect-square bg-surface"
      bodyClassName="p-2"
      media={(
        <>
          <EntityMedia
            entityType="gallery"
            entityId={gallery.id}
            surface="card"
            imageUrl={galleryCoverSrc ?? null}
            alt={title}
            fit={imageFit}
            loading="lazy"
            className="h-full w-full"
            renderDefault={() => galleryCoverSrc ? (
              <>
                <CoverImage src={galleryCoverSrc} alt={title} className="h-full w-full" loading="lazy" onError={(event) => { const image = event.currentTarget; image.style.display = "none"; const fallback = image.nextElementSibling as HTMLElement | null; if (fallback) fallback.style.display = "flex"; }} />
                <div className="hidden h-full w-full items-center justify-center"><FolderOpen className="h-10 w-10 text-muted" /></div>
              </>
            ) : (
              <FolderOpen className="h-10 w-10 text-muted" />
            )}
          />
          <RatingBanner rating={engagement?.rating} />
          {!selecting ? (
            <BookmarkButton
              hostType="gallery"
              hostId={gallery.id}
              compact
              deferUntilHover
              initialSaved={bookmarkInitiallySaved}
              className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100"
            />
          ) : null}
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
        </>
      )}
      body={(
        <p className="card-title line-clamp-2 font-semibold text-foreground group-hover:text-accent">{title}</p>
      )}
      footer={hasFooter ? (
        <>
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
              studio={gallery.studioName ? { id: gallery.studioId, name: gallery.studioName } : null}
              performers={gallery.performers}
              tags={gallery.tags}
              onNavigate={onNavigate}
            />
            {gallery.organized ? <span className="p-1 text-muted" title="Organized"><Box className="w-3.5 h-3.5" /></span> : null}
            {likeCount > 0 ? <LikeCounter count={likeCount} /> : null}
        </>
      ) : null}
    />
  );
}

type GroupPreviewKind = "video" | "image" | "audio" | "text" | "segment";

const GROUP_ITEMS_POPOVER_LIMIT = 10;

const GROUP_PREVIEW_EMPTY_LABELS: Record<GroupPreviewKind, string> = {
  video: "No videos",
  image: "No images",
  audio: "No audio",
  text: "No texts",
  segment: "No segments",
};

function groupItemMatchesPreviewKind(item: GroupItem, kind: GroupPreviewKind) {
  if (kind === "video") {
    return item.kind === "video" || item.kind === "videoRange" || item.hostType === "video";
  }

  return item.kind === kind || item.hostType === kind;
}

function getGroupItemPreviewId(item: GroupItem, kind: GroupPreviewKind) {
  switch (kind) {
    case "video":
      return item.videoId ?? (item.hostType === "video" ? item.hostId : undefined);
    case "image":
      return item.imageId ?? (item.hostType === "image" ? item.hostId : undefined);
    case "audio":
      return item.hostType === "audio" ? item.hostId : undefined;
    case "text":
      return item.hostType === "text" ? item.hostId : undefined;
    case "segment":
      return item.hostType === "segment" ? item.hostId : undefined;
  }
}

function getGroupItemPreviewTitle(item: GroupItem, kind: GroupPreviewKind) {
  const explicitTitle = item.title?.trim() || item.videoTitle?.trim() || item.imageTitle?.trim() || item.childGroupName?.trim();
  if (explicitTitle) return explicitTitle;

  const id = getGroupItemPreviewId(item, kind);
  const label = kind === "audio" ? "Audio" : kind[0].toUpperCase() + kind.slice(1);
  return id ? `${label} ${id}` : `${label} ${item.orderIndex + 1}`;
}

function getGroupItemPreviewRoute(item: GroupItem, kind: GroupPreviewKind) {
  const id = getGroupItemPreviewId(item, kind);
  if (!id) return null;
  return { page: kind, id };
}

function GroupItemPreviewMedia({ item, kind }: { item: GroupItem; kind: GroupPreviewKind }) {
  const previewId = getGroupItemPreviewId(item, kind);
  if (kind === "video" && previewId) {
    return <img src={videos.screenshotUrl(previewId, item.updatedAt)} alt="" className="h-9 w-14 flex-shrink-0 rounded bg-surface object-cover" loading="lazy" onError={(event) => { (event.currentTarget as HTMLImageElement).style.display = "none"; }} />;
  }

  if (kind === "image" && previewId) {
    return <CoverImage src={images.thumbnailUrl(previewId)} alt="" className="h-10 w-10 flex-shrink-0 rounded bg-surface" loading="lazy" onError={(event) => { (event.currentTarget as HTMLImageElement).style.display = "none"; }} />;
  }

  const iconClassName = "h-4 w-4 flex-shrink-0 text-muted";
  if (kind === "audio") return <Headphones className={iconClassName} />;
  if (kind === "text") return <FileText className={iconClassName} />;
  if (kind === "segment") return <Merge className={iconClassName} />;
  return <Film className={iconClassName} />;
}

function GroupItemPreviewRow({ item, kind, onNavigate }: { item: GroupItem; kind: GroupPreviewKind; onNavigate?: (route: any) => void }) {
  const route = getGroupItemPreviewRoute(item, kind);
  const title = getGroupItemPreviewTitle(item, kind);
  const rangeLabel = typeof item.startSec === "number"
    ? `${formatDuration(item.startSec)}${typeof item.endSec === "number" ? ` - ${formatDuration(item.endSec)}` : ""}`
    : null;
  const content = (
    <>
      <GroupItemPreviewMedia item={item} kind={kind} />
      <span className="min-w-0 flex-1 truncate text-[11px] font-medium text-foreground">{title}</span>
      {rangeLabel ? <span className="shrink-0 text-[10px] text-muted">{rangeLabel}</span> : null}
    </>
  );

  if (route) {
    const navigationHandlers = createNestedEntityNavigationHandlers<HTMLAnchorElement>(route, onNavigate);
    return (
      <a {...navigationHandlers} className="flex w-full items-center gap-2 rounded px-1.5 py-1 text-left transition-colors hover:bg-card-hover">
        {content}
      </a>
    );
  }

  return <div className="flex items-center gap-2 rounded px-1.5 py-1">{content}</div>;
}

export function GroupItemsPopoverContent({ groupId, kind, totalCount, onNavigate }: { groupId: number; kind: GroupPreviewKind; totalCount?: number; onNavigate?: (route: any) => void }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ["group-items-popover", groupId, kind],
    queryFn: () => groups.items.page(groupId, { page: 1, perPage: 0, sort: "order", direction: "asc" }),
    staleTime: 30_000,
  });

  if (isLoading) return <p className="px-1 text-[11px] text-muted">Loading...</p>;
  if (isError) return <p className="px-1 text-[11px] text-muted">Could not load preview</p>;

  const matchingItems = (data?.items ?? []).filter((item) => groupItemMatchesPreviewKind(item, kind));
  if (matchingItems.length === 0) return <p className="px-1 text-[11px] text-muted">{GROUP_PREVIEW_EMPTY_LABELS[kind]}</p>;

  const previewItems = matchingItems.slice(0, GROUP_ITEMS_POPOVER_LIMIT);
  const resolvedTotal = Math.max(totalCount ?? 0, matchingItems.length);
  const moreCount = Math.max(0, resolvedTotal - previewItems.length);

  return (
    <div className="space-y-1">
      {previewItems.map((item) => <GroupItemPreviewRow key={`${item.kind}-${item.id}-${item.orderIndex}`} item={item} kind={kind} onNavigate={onNavigate} />)}
      {moreCount > 0 ? <p className="px-1 pt-0.5 text-[10px] text-muted">+ {moreCount} more</p> : null}
    </div>
  );
}

// ===== GroupTile =====

interface GroupTileProps {
  group: Group;
  onClick: (options?: MultiSelectToggleOptions) => void;
  onNavigate?: (r: any) => void;
  selected?: boolean;
  onSelect?: (options?: MultiSelectToggleOptions) => void;
  selecting?: boolean;
  selectable?: boolean;
  bookmarkInitiallySaved?: boolean;
  dragHandleProps?: EntityTileDragHandleProps;
  isDragging?: boolean;
  isOver?: boolean;
}

export function GroupTile({ group, engagement, onClick, onNavigate, selected, onSelect, selecting, selectable, bookmarkInitiallySaved, dragHandleProps, isDragging, isOver }: GroupTileProps & { engagement?: EntityEngagement }) {
  const imageFit = useConfiguredImageFit();
  const previewCountItems: Array<{ key: string; kind: GroupPreviewKind; title: string; count: number; icon: ReactNode }> = [
    { key: "image", kind: "image" as const, title: "Images", count: group.imageCount ?? 0, icon: <ImagesIcon className="w-3.5 h-3.5" /> },
    { key: "audio", kind: "audio" as const, title: "Audios", count: group.audioCount ?? 0, icon: <Headphones className="w-3.5 h-3.5" /> },
    { key: "text", kind: "text" as const, title: "Texts", count: group.textCount ?? 0, icon: <FileText className="w-3.5 h-3.5" /> },
    { key: "segments", kind: "segment" as const, title: "Segments", count: group.segmentCount ?? 0, icon: <Merge className="w-3.5 h-3.5" /> },
  ].filter((item) => item.count > 0);
  const countItems = [
    { key: "gallery", title: "Galleries", count: group.galleryCount ?? 0, icon: <FolderOpen className="w-3.5 h-3.5" /> },
    { key: "subgroups", title: "Subgroups", count: group.subGroupCount ?? 0, icon: <Layers className="w-3.5 h-3.5" /> },
    { key: "performer", title: "Performers", count: group.performerCount ?? 0, icon: <User className="w-3.5 h-3.5" /> },
    { key: "studio", title: "Studios", count: group.studioCount ?? 0, icon: <Building2 className="w-3.5 h-3.5" /> },
    { key: "tagItems", title: "Tag Items", count: group.tagItemCount ?? 0, icon: <Tag className="w-3.5 h-3.5" /> },
    { key: "faces", title: "Faces", count: group.faceCount ?? 0, icon: <Fingerprint className="w-3.5 h-3.5" /> },
  ].filter((item) => item.count > 0);
  const hasUncategorizedItems = group.kind === "dynamic" && (group.itemCount ?? 0) > 0 && group.videoCount === 0 && previewCountItems.length === 0 && countItems.length === 0;
  const hasFooter = (group.tags?.length ?? 0) > 0 || group.videoCount > 0 || previewCountItems.length > 0 || countItems.length > 0 || hasUncategorizedItems;

  return (
    <EntityTileFrame
      route={{ page: "group", id: group.id }}
      label={`Open group ${group.name}`}
      onClick={onClick}
      selected={selected}
      onSelect={onSelect}
      selecting={selecting}
      selectable={selectable}
      dragHandleProps={dragHandleProps}
      isDragging={isDragging}
      isOver={isOver}
      media={(
        <>
          <EntityMedia
            entityType="group"
            entityId={group.id}
            surface="card"
            imageUrl={group.frontImagePath ?? null}
            alt={group.name}
            fit={imageFit}
            loading="lazy"
            className="h-full w-full"
            renderDefault={() => group.frontImagePath ? (
              <>
                <CoverImage src={group.frontImagePath} alt={group.name} className="h-full w-full" loading="lazy" onError={(event) => { const image = event.currentTarget; image.style.display = "none"; const fallback = image.nextElementSibling as HTMLElement | null; if (fallback) fallback.style.display = "flex"; }} />
                <div className="hidden h-full w-full items-center justify-center"><Layers className="h-10 w-10 text-muted" /></div>
              </>
            ) : (
              <Layers className="h-10 w-10 text-muted" />
            )}
          />
          <RatingBanner rating={engagement?.rating} />
          {!selecting ? (
            <BookmarkButton
              hostType="group"
              hostId={group.id}
              compact
              deferUntilHover
              initialSaved={bookmarkInitiallySaved}
              className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100"
            />
          ) : null}
          {group.kind === "dynamic" ? <span className="absolute bottom-1 left-1 rounded bg-accent/90 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-white">Dynamic</span> : null}
        </>
      )}
      body={(
        <>
          <p className="card-title line-clamp-2 font-semibold text-foreground group-hover:text-accent">{group.name}</p>
          {(group.date || group.studioName) ? <p className="truncate text-xs text-secondary">{group.date || group.studioName}</p> : null}
        </>
      )}
      footer={hasFooter ? (
        <>
            {group.videoCount > 0 ? (
              group.kind === "dynamic" ? (
                <PopoverButton icon={<Film className="w-3.5 h-3.5" />} count={group.videoCount} title="Videos" wide preferBelow>
                  <GroupItemsPopoverContent groupId={group.id} kind="video" totalCount={group.videoCount} onNavigate={onNavigate} />
                </PopoverButton>
              ) : (
                <PopoverButton icon={<Film className="w-3.5 h-3.5" />} count={group.videoCount} title="Videos" wide preferBelow>
                  <VideosPopoverContent filter={{ groupId: group.id }} />
                </PopoverButton>
              )
            ) : null}
            {previewCountItems.map((item) => (
              <PopoverButton key={item.key} icon={item.icon} count={item.count} title={item.title} wide preferBelow>
                <GroupItemsPopoverContent groupId={group.id} kind={item.kind} totalCount={item.count} onNavigate={onNavigate} />
              </PopoverButton>
            ))}
            {countItems.map((item) => <CountPill key={item.key} icon={item.icon} count={item.count} title={item.title} />)}
            {hasUncategorizedItems ? <CountPill icon={<Layers className="w-3.5 h-3.5" />} count={group.itemCount ?? 0} title="Items" /> : null}
            {(group.tags?.length ?? 0) > 0 ? (
              <PopoverButton icon={<Tag className="w-3.5 h-3.5" />} count={group.tags.length} title="Tags" preferBelow>
                <EntityLinkList items={group.tags.map((tag) => ({ id: tag.id, label: tag.name, color: tag.color ?? tag.tagGroupColor }))} page="tag" onNavigate={onNavigate} />
              </PopoverButton>
            ) : null}
        </>
      ) : null}
    />
  );
}

function CountPill({ icon, count, title }: { icon: ReactNode; count: number; title: string }) {
  return (
    <span className="inline-flex items-center gap-1 rounded px-1.5 py-1 text-xs text-muted" title={`${title}: ${count}`}>
      {icon}
      <span>{count}</span>
    </span>
  );
}

interface AudioTileProps {
  audio: Audio;
  engagement?: EntityEngagement;
  onClick: (options?: MultiSelectToggleOptions) => void;
  onNavigate?: (route: any) => void;
  selected?: boolean;
  onSelect?: (options?: MultiSelectToggleOptions) => void;
  selecting?: boolean;
}

export function AudioTile({ audio, engagement, selected, onSelect, selecting, onClick, onNavigate }: AudioTileProps) {
  const imageFit = useConfiguredImageFit();
  const title = getAudioDisplayTitle(audio);
  const duration = audio.maxDuration > 0 ? formatDuration(audio.maxDuration) : null;
  const audioRef = useRef<HTMLAudioElement>(null);
  const hoverTimerRef = useRef<number | null>(null);
  const canPreview = !selecting && !selected;

  const stopPreview = useCallback(() => {
    if (hoverTimerRef.current !== null) {
      window.clearTimeout(hoverTimerRef.current);
      hoverTimerRef.current = null;
    }
    const element = audioRef.current;
    if (!element) return;
    element.pause();
    element.currentTime = 0;
  }, []);

  const schedulePreview = (event: MouseEvent<HTMLElement>) => {
    if (!canPreview || (event.target as HTMLElement).closest("[data-audio-preview-ignore]")) return;
    if (hoverTimerRef.current !== null) window.clearTimeout(hoverTimerRef.current);
    hoverTimerRef.current = window.setTimeout(() => {
      hoverTimerRef.current = null;
      const element = audioRef.current;
      if (!element) return;
      element.currentTime = 0;
      element.volume = 0.35;
      element.play().catch(() => {});
    }, 1000);
  };

  useEffect(() => {
    if (!canPreview) stopPreview();
    return () => {
      if (hoverTimerRef.current !== null) window.clearTimeout(hoverTimerRef.current);
    };
  }, [canPreview, stopPreview]);

  return (
    <article onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined} onMouseEnter={schedulePreview} onMouseLeave={stopPreview} className={`entity-card group relative flex h-full cursor-pointer flex-col overflow-hidden rounded-lg border bg-card text-left transition-colors ${selected ? "border-accent ring-2 ring-accent" : "border-border hover:border-accent/60"}`}>
      <RouteCardLinkOverlay route={{ page: "audio", id: audio.id }} onClick={onClick} label={`Open ${title}`} disabled={selecting} selectionSafeZone={selected !== undefined || selecting} />
      <div className="card-media relative flex aspect-video items-center justify-center overflow-hidden bg-surface">
        <EntityMedia
          entityType="audio"
          entityId={audio.id}
          surface="card"
          imageUrl={audio.imagePath ?? null}
          alt={title}
          fit={imageFit}
          loading="lazy"
          className="h-full w-full"
          renderDefault={() => audio.imagePath ? (
            <CoverImage src={audio.imagePath} alt={title} className="h-full w-full" loading="lazy" />
          ) : (
            <Headphones className="h-12 w-12 text-muted opacity-50" />
          )}
        />
        <audio ref={audioRef} src={audios.streamUrl(audio.id)} preload="none" />
        {(selected !== undefined || selecting) ? <div data-audio-preview-ignore onMouseEnter={stopPreview}><CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} /></div> : null}
        {!selecting ? <BookmarkButton hostType="audio" hostId={audio.id} compact deferUntilHover className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100" /> : null}
        <div data-audio-preview-ignore onMouseEnter={stopPreview}>
          <StudioCardOverlay studioId={audio.studioId} studioName={audio.studioName} selecting={selecting} onNavigate={onNavigate} />
        </div>
        {audio.hasVideoFiles ? (
          <span className="absolute bottom-1 left-1 z-[5] inline-flex items-center gap-1 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-medium text-white"><MonitorPlay className="h-3 w-3" />Video</span>
        ) : null}
        {duration ? <span className="absolute bottom-1 right-1 z-[5] rounded bg-black/70 px-1.5 py-0.5 text-xs text-white">{duration}</span> : null}
        <RatingBanner rating={engagement?.rating} />
      </div>
      <div className="card-body flex min-h-0 flex-1 flex-col gap-1.5 border-t border-border/50 px-2.5 pb-2 pt-2">
        <div>
          <h2 className="card-title line-clamp-2 font-semibold leading-snug text-foreground transition-colors group-hover:text-accent" title={title}>{title}</h2>
          <div className="mt-1 flex items-center gap-2 text-[11px] text-muted">
            {audio.date ? <span>{audio.date}</span> : null}
            {audio.studioName ? <span className="truncate">{audio.studioName}</span> : null}
          </div>
        </div>
        <div data-audio-preview-ignore onMouseEnter={stopPreview}><MediaCardPerformerBadges performerItems={audio.performers} onNavigate={onNavigate} /></div>
        {audio.details ? <p className="line-clamp-2 text-xs leading-snug text-secondary">{audio.details}</p> : null}
        <div className="flex flex-wrap gap-1.5 text-[11px] text-muted">
          {engagement?.playCount ? <span className="inline-flex items-center gap-1 rounded border border-border/80 px-1.5 py-0.5"><PlayCircle className="h-3 w-3" />{engagement.playCount} play{engagement.playCount === 1 ? "" : "s"}</span> : null}
          {audio.tracks.length > 0 ? <span className="inline-flex items-center gap-1 rounded border border-border/80 px-1.5 py-0.5"><Mic2 className="h-3 w-3" />{audio.tracks.length} track{audio.tracks.length === 1 ? "" : "s"}</span> : null}
        </div>
      </div>
      <div data-audio-preview-ignore onMouseEnter={stopPreview}><AudioTextCardPopovers hostType="audio" hostId={audio.id} performers={audio.performers} tags={audio.tags} groups={audio.groups} engagement={engagement} organized={audio.organized} onNavigate={onNavigate} /></div>
    </article>
  );
}

interface TextTileProps {
  text: TextDocument;
  engagement?: EntityEngagement;
  onClick: (options?: MultiSelectToggleOptions) => void;
  onNavigate?: (route: any) => void;
  selected?: boolean;
  onSelect?: (options?: MultiSelectToggleOptions) => void;
  selecting?: boolean;
}

export function TextTile({ text, engagement, selected, onSelect, selecting, onClick, onNavigate }: TextTileProps) {
  const imageFit = useConfiguredImageFit();
  const title = getTextDisplayTitle(text);
  const primaryFile = pickPrimaryTextFile(text);
  const preview = primaryFile?.excerptText?.trim() || text.details?.trim() || "Open the document to read the extracted content and file details.";

  return (
    <article onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined} className={`entity-card group relative flex h-full cursor-pointer flex-col overflow-hidden rounded-lg border bg-card text-left transition-colors ${selected ? "border-accent ring-2 ring-accent" : "border-border hover:border-accent/60"}`}>
      <RouteCardLinkOverlay route={{ page: "text", id: text.id }} onClick={onClick} label={`Open ${title}`} disabled={selecting} selectionSafeZone={selected !== undefined || selecting} />
      <div className="card-media relative flex aspect-video items-center justify-center overflow-hidden bg-surface">
        <EntityMedia
          entityType="text"
          entityId={text.id}
          surface="card"
          imageUrl={text.imagePath ?? null}
          alt={title}
          fit={imageFit}
          loading="lazy"
          className="h-full w-full"
          renderDefault={() => text.imagePath ? <CoverImage src={text.imagePath} alt={title} className="h-full w-full" loading="lazy" /> : <FileText className="h-12 w-12 text-muted opacity-50" />}
        />
        {(selected !== undefined || selecting) ? <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} /> : null}
        {!selecting ? <BookmarkButton hostType="text" hostId={text.id} compact deferUntilHover className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100" /> : null}
        <StudioCardOverlay studioId={text.studioId} studioName={text.studioName} selecting={selecting} onNavigate={onNavigate} />
        {text.maxWordCount ? <span className="absolute bottom-1 right-1 z-[5] rounded bg-black/70 px-1.5 py-0.5 text-xs text-white">{Intl.NumberFormat().format(text.maxWordCount)} words</span> : null}
        <RatingBanner rating={engagement?.rating} />
      </div>
      <div className="card-body flex min-h-0 flex-1 flex-col gap-1.5 border-t border-border/50 px-2.5 pb-2 pt-2">
        <div>
          <h2 className="card-title line-clamp-2 font-semibold leading-snug text-foreground transition-colors group-hover:text-accent" title={title}>{title}</h2>
          <div className="mt-1 flex items-center gap-2 text-[11px] text-muted">
            {text.date ? <span>{text.date}</span> : null}
            {text.studioName ? <span className="truncate">{text.studioName}</span> : null}
          </div>
        </div>
        <MediaCardPerformerBadges performerItems={text.performers} onNavigate={onNavigate} />
        <p className="line-clamp-2 text-xs leading-snug text-secondary">{preview}</p>
        {text.maxPageCount ? <div className="flex flex-wrap gap-1.5 text-[11px] text-muted"><span className="inline-flex items-center gap-1 rounded border border-border/80 px-1.5 py-0.5"><BookOpenText className="h-3 w-3" />{text.maxPageCount} page{text.maxPageCount === 1 ? "" : "s"}</span></div> : null}
      </div>
      <AudioTextCardPopovers hostType="text" hostId={text.id} performers={text.performers} tags={text.tags} groups={text.groups} engagement={engagement} organized={text.organized} onNavigate={onNavigate} />
    </article>
  );
}

export function TagTile({ tag, engagement, onClick, onNavigate, children, selected, onSelect, selecting }: { tag: TagType; engagement?: EntityEngagement; onClick: (options?: MultiSelectToggleOptions) => void; onNavigate?: (r: any) => void; children?: ReactNode; selected?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; selecting?: boolean }) {
  const imageFit = useConfiguredImageFit();
  const favorite = engagement?.isFavorite ?? tag.favorite;
  const hasFooter = Boolean(tag.videoCount || tag.segmentCount || tag.imageCount || tag.galleryCount || tag.groupCount || tag.performerCount || tag.studioCount || tag.audioCount || tag.textCount);

  return (
    <EntityTileFrame
      route={{ page: "tag", id: tag.id }}
      label={`Open tag ${tag.name}`}
      onClick={onClick}
      selected={selected}
      onSelect={onSelect}
      selecting={selecting}
      media={(
        <>
          <RatingBanner rating={engagement?.rating} />
          {favorite ? <Heart className="absolute right-2 top-2 z-10 h-4 w-4 fill-red-500 text-red-500 drop-shadow" /> : null}
          <EntityMedia
            entityType="tag"
            entityId={tag.id}
            surface="card"
            imageUrl={tag.imagePath ?? null}
            alt={tag.name}
            fit={imageFit}
            loading="lazy"
            className="h-full w-full"
            renderDefault={() => tag.imagePath ? (
              <>
                <CoverImage src={tag.imagePath} alt={tag.name} className="h-full w-full" loading="lazy" onError={(event) => { const image = event.currentTarget; image.style.display = "none"; const fallback = image.nextElementSibling as HTMLElement | null; if (fallback) fallback.style.display = "flex"; }} />
                <div className="hidden h-full w-full items-center justify-center"><Tag className="h-10 w-10 text-muted" /></div>
              </>
            ) : (
              <Tag className="h-10 w-10 text-muted" />
            )}
          />
        </>
      )}
      body={(
        <>
          <h3 className="card-title truncate text-sm font-semibold text-foreground group-hover:text-accent">{tag.name}</h3>
          {tag.tagGroupName ? <div className="inline-flex max-w-full items-center gap-1.5 rounded-full border border-border bg-surface px-2 py-0.5 text-[10px] text-secondary"><span className="h-2 w-2 rounded-full border border-border" style={{ backgroundColor: tag.tagGroupColor ?? "transparent" }} /><span className="truncate">{tag.tagGroupName}</span></div> : null}
          {tag.description ? <p className="line-clamp-1 text-xs text-secondary">{tag.description}</p> : null}
        </>
      )}
      footer={hasFooter ? (
        <>
          {tag.videoCount != null && tag.videoCount > 0 ? <PopoverButton icon={<Film className="w-3 h-3" />} count={tag.videoCount} title="Videos" wide preferBelow><VideosPopoverContent filter={{ tagIds: String(tag.id) }} /></PopoverButton> : null}
          {tag.imageCount != null && tag.imageCount > 0 ? <PopoverButton icon={<ImagesIcon className="w-3.5 h-3.5" />} count={tag.imageCount} title="Images" wide preferBelow><ImagesPopoverContent filter={{ tagIds: String(tag.id) }} /></PopoverButton> : null}
          {tag.audioCount != null && tag.audioCount > 0 ? <PopoverButton icon={<Headphones className="w-3.5 h-3.5" />} count={tag.audioCount} title="Audios" wide preferBelow><AudiosPopoverContent filter={{ tagsCriterion: { modifier: "INCLUDES", value: [tag.id] } }} /></PopoverButton> : null}
          {tag.textCount != null && tag.textCount > 0 ? <PopoverButton icon={<FileText className="w-3.5 h-3.5" />} count={tag.textCount} title="Texts" wide preferBelow><TextsPopoverContent filter={{ tagsCriterion: { modifier: "INCLUDES", value: [tag.id] } }} /></PopoverButton> : null}
          {tag.galleryCount != null && tag.galleryCount > 0 ? <PopoverButton icon={<FolderOpen className="w-3 h-3" />} count={tag.galleryCount} title="Galleries" wide preferBelow><GalleriesPopoverContent filter={{ tagIds: String(tag.id) }} /></PopoverButton> : null}
          {tag.groupCount != null && tag.groupCount > 0 ? <PopoverButton icon={<Layers className="w-3 h-3" />} count={tag.groupCount} title="Groups" wide preferBelow><GroupsPopoverContent filter={{ tagIds: String(tag.id) }} /></PopoverButton> : null}
          {tag.segmentCount != null && tag.segmentCount > 0 ? <span className="flex items-center gap-0.5 text-xs text-muted" title="Segments"><Layers className="w-3 h-3" /> {tag.segmentCount}</span> : null}
          {tag.performerCount != null && tag.performerCount > 0 ? <PopoverButton icon={<User className="w-3 h-3" />} count={tag.performerCount} title="Performers" wide preferBelow><PerformersPopoverContent filter={{ tagIds: String(tag.id) }} /></PopoverButton> : null}
          {tag.studioCount != null && tag.studioCount > 0 ? <PopoverButton icon={<Building2 className="w-3 h-3" />} count={tag.studioCount} title="Studios" wide preferBelow><StudiosPopoverContent filter={{ tagIds: String(tag.id) }} /></PopoverButton> : null}
        </>
      ) : null}
    >
      {children}
    </EntityTileFrame>
  );
}

export function FaceTile({ face, onClick, selected, onSelect, selecting, children }: { face: Face; onClick: (options?: MultiSelectToggleOptions) => void; selected?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; selecting?: boolean; children?: React.ReactNode }) {
  const title = faceDisplayName(face);

  return (
    <EntityTileFrame
      route={{ page: "face", id: face.id }}
      label={`Open face ${title}`}
      onClick={onClick}
      selected={selected}
      onSelect={onSelect}
      selecting={selecting}
      mediaClassName="aspect-square bg-surface/80"
      bodyClassName="p-2.5"
      extensionClassName="border-t border-border/50 p-2.5"
      extensionBeforeFooter
      media={(
        <>
          <EntityMedia
            entityType="face"
            entityId={face.id}
            surface="card"
            imageUrl={face.coverImageUrl ?? null}
            alt={title}
            fit="cover"
            loading="lazy"
            className="h-full w-full object-cover transition-transform duration-300 group-hover:scale-[1.02]"
            renderDefault={() => face.coverImageUrl ? (
              <img src={face.coverImageUrl} alt={title} className="h-full w-full object-cover transition-transform duration-300 group-hover:scale-[1.02]" loading="lazy" />
            ) : (
              <div className="flex h-full w-full items-center justify-center bg-surface text-muted"><Fingerprint className="h-12 w-12" /></div>
            )}
          />
          <div className="absolute inset-x-0 bottom-0 flex flex-wrap gap-1 bg-gradient-to-t from-black/80 via-black/35 to-transparent p-2.5">
            {face.performerId ? <span className="inline-flex items-center gap-1 rounded-full bg-black/65 px-2 py-0.5 text-[11px] text-white"><Link2 className="h-3 w-3" />Linked</span> : null}
          </div>
        </>
      )}
      body={(
        <>
          <h3 className="card-title truncate text-sm font-semibold text-foreground group-hover:text-accent">{title}</h3>
          <div className="truncate text-xs text-secondary">{face.performerId && face.performerName ? `Updated ${formatDate(face.updatedAt)}` : face.performerName || `Updated ${formatDate(face.updatedAt)}`}</div>
        </>
      )}
      footer={(
        <>
          <CountPill icon={<Eye className="w-3.5 h-3.5" />} count={face.detectionCount} title="Detections" />
          {face.videoCount > 0 ? (
            <PopoverButton icon={<Film className="w-3.5 h-3.5" />} count={face.videoCount} title="Videos" wide preferBelow>
              <FaceAppearancesPopoverContent faceId={face.id} hostType="video" />
            </PopoverButton>
          ) : null}
          {face.imageCount > 0 ? (
            <PopoverButton icon={<ImagesIcon className="w-3.5 h-3.5" />} count={face.imageCount} title="Images" wide preferBelow>
              <FaceAppearancesPopoverContent faceId={face.id} hostType="image" />
            </PopoverButton>
          ) : null}
        </>
      )}
    >
      {children}
    </EntityTileFrame>
  );
}

function FaceAppearancesPopoverContent({ faceId, hostType }: { faceId: number; hostType: "video" | "image" }) {
  const { data, isLoading } = useQuery({
    queryKey: ["face-card-appearances-popover", faceId, hostType],
    queryFn: () => facesApi.appearances(faceId, { sort: "last_seen", direction: "desc", perPage: 24 }),
    staleTime: 60_000,
  });

  if (isLoading) {
    return <p className="px-1 text-[11px] text-muted">Loading...</p>;
  }

  const items = (data?.items ?? []).filter((item) => item.hostType === hostType).slice(0, 8);
  if (items.length === 0) {
    return <p className="px-1 text-[11px] text-muted">No {hostType === "video" ? "videos" : "images"}</p>;
  }

  return hostType === "image" ? (
    <div className="grid grid-cols-4 gap-1">
      {items.map((item) => (
        <div key={item.appearanceId} className="aspect-square overflow-hidden rounded bg-surface">
          <CoverImage src={item.thumbnailUrl} alt="" className="h-full w-full" loading="lazy" onError={(event) => { (event.currentTarget as HTMLImageElement).style.display = "none"; }} />
        </div>
      ))}
    </div>
  ) : (
    <div className="space-y-1">
      {items.map((item) => (
        <div key={item.appearanceId} className="flex items-center gap-2 rounded px-1 py-0.5 hover:bg-card">
          <img src={item.thumbnailUrl} alt="" className="h-7 w-12 flex-shrink-0 rounded bg-surface object-cover" loading="lazy" onError={(event) => { (event.currentTarget as HTMLImageElement).style.display = "none"; }} />
          <span className="truncate text-[11px] text-foreground">{item.title || `Video #${item.hostId}`}</span>
        </div>
      ))}
    </div>
  );
}

export function FaceAppearanceTile({ appearance, onClick }: { appearance: FaceAppearance; onClick: () => void }) {
  const hostLabel = appearance.title || `${appearance.hostType === "image" ? "Image" : "Video"} #${appearance.hostId}`;
  const Icon = appearance.hostType === "image" ? ImagesIcon : Film;

  return (
    <EntityTileFrame
      route={{ page: appearance.hostType, id: appearance.hostId }}
      label={`Open ${hostLabel}`}
      onClick={onClick}
      mediaClassName={appearance.hostType === "image" ? "aspect-square bg-surface/80" : "aspect-video bg-surface/80"}
      media={(
        <>
          <div className="absolute inset-0 flex items-center justify-center text-muted">
            <Icon className="h-10 w-10" />
          </div>
          <img
            src={appearance.thumbnailUrl}
            alt={hostLabel}
            className="relative h-full w-full object-cover transition-transform duration-300 group-hover:scale-[1.02]"
            loading="lazy"
            onError={(event) => { (event.currentTarget as HTMLImageElement).style.display = "none"; }}
          />
        </>
      )}
      body={(
        <>
          <div className="flex flex-wrap items-center gap-1.5 text-[11px] text-secondary">
            <span className="inline-flex items-center gap-1 rounded border border-border bg-surface/70 px-1.5 py-0.5">
              <Icon className="h-3 w-3" />
              {appearance.hostType}
            </span>
            {appearance.hostType === "video" ? <span>{formatFaceAppearanceTimeRange(appearance)}</span> : <span>Image appearance</span>}
          </div>
          <h3 className="card-title truncate text-sm font-semibold text-foreground group-hover:text-accent">{hostLabel}</h3>
          {appearance.topConfidence != null ? <div className="text-xs text-secondary">{Math.round(appearance.topConfidence * 100)}% confidence</div> : null}
        </>
      )}
      footer={(
        <>
          <CountPill icon={<Film className="w-3.5 h-3.5" />} count={appearance.frameSampleCount} title="Frames" />
          <CountPill icon={<Eye className="w-3.5 h-3.5" />} count={appearance.retainedSpatialSampleCount} title="Samples" />
          <CountPill icon={<Layers className="w-3.5 h-3.5" />} count={appearance.segmentCount} title="Segments" />
        </>
      )}
    />
  );
}

function formatFaceAppearanceTimeRange(appearance: FaceAppearance) {
  const start = appearance.firstSeenAtSec == null ? null : formatFaceAppearanceTime(appearance.firstSeenAtSec);
  const end = appearance.lastSeenAtSec == null ? null : formatFaceAppearanceTime(appearance.lastSeenAtSec);
  return start && end && start !== end ? `${start} - ${end}` : start ?? end ?? "Video appearance";
}

function formatFaceAppearanceTime(totalSeconds: number) {
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = Math.floor(totalSeconds % 60);
  return hours > 0
    ? `${hours}:${minutes.toString().padStart(2, "0")}:${seconds.toString().padStart(2, "0")}`
    : `${minutes}:${seconds.toString().padStart(2, "0")}`;
}

interface SegmentTileItem {
  id: number | string;
  hostType: string;
  hostId: number;
  startSec: number;
  endSec?: number;
  tagName?: string;
  kind?: string;
  sourceKey?: string;
  sourceRunId?: string;
  confidence?: number;
  title?: string;
  updatedAt?: string;
  hostTitle?: string;
  refLabel?: string;
  performerName?: string;
}

export function SegmentTile({ segment, route, label, eyebrow, footer, onClick, selected, onSelect, selecting }: { segment: SegmentTileItem; route?: any; label?: string; eyebrow?: string; footer?: ReactNode; onClick: (options?: MultiSelectToggleOptions) => void; selected?: boolean; onSelect?: (options?: MultiSelectToggleOptions) => void; selecting?: boolean }) {
  const sourceLabel = formatSegmentSourceLabel(segment.sourceKey);
  const refLabel = segment.performerName || segment.refLabel;
  const title = segment.title || segment.tagName || refLabel || segment.kind || sourceLabel;
  const cardRoute = route ?? { page: "segment", id: segment.id };
  const rangeLabel = formatSegmentRangeLabel(segment.startSec, segment.endSec);
  const confidenceLabel = segment.confidence == null ? null : `${Math.round(segment.confidence * 100)}%`;

  return (
    <article onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined} className={`entity-card video-card group relative overflow-hidden rounded border bg-card transition-all ${selected ? "border-accent ring-2 ring-accent" : "border-border hover:border-accent/60"}`}>
      <RouteCardLinkOverlay route={cardRoute} onClick={onClick} label={label ?? `Open segment ${title}`} disabled={selecting} selectionSafeZone={selected !== undefined || selecting} />
      <div className="card-media relative aspect-video w-full overflow-hidden bg-surface/70">
        {(selected !== undefined || selecting) ? <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} /> : null}
        {segment.hostType === "video" ? <SegmentHoverPreview hostId={segment.hostId} segmentId={typeof segment.id === "number" ? segment.id : undefined} updatedAt={segment.updatedAt} startSec={segment.startSec} endSec={segment.endSec} title={title} className="h-full w-full object-cover transition-transform duration-300 group-hover:scale-105" /> : <div className="flex h-full w-full items-center justify-center bg-surface text-muted"><Layers className="h-10 w-10" /></div>}
        <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/85 via-black/35 to-transparent p-3 text-white">
          <div className="flex items-end justify-between gap-3">
            <div className="line-clamp-2 min-w-0 text-sm font-semibold">{title}</div>
            <div className="shrink-0 rounded bg-black/70 px-1.5 py-0.5 text-xs font-medium text-white/85">{eyebrow ?? rangeLabel}</div>
          </div>
        </div>
      </div>
      <div className="card-body border-t border-border bg-card p-3">
        <div className="line-clamp-2 text-sm font-medium text-foreground">{title}</div>
        <div className="truncate text-xs text-secondary">{segment.hostTitle || `${segment.hostType} #${segment.hostId}`}</div>
      </div>
      <div className="card-popovers relative z-10 flex flex-wrap items-center gap-1.5 border-t border-border px-3 py-2 text-[11px] text-secondary">
        {segment.tagName ? <SegmentInfoChip label="Tag" value={segment.tagName} /> : null}
        {segment.kind ? <SegmentInfoChip label="Kind" value={segment.kind} /> : null}
        {refLabel ? <SegmentInfoChip label="Ref" value={refLabel} /> : null}
        {segment.sourceKey ? <SegmentInfoChip label="Provider" value={sourceLabel} /> : null}
        {confidenceLabel ? <SegmentInfoChip label="Confidence" value={confidenceLabel} /> : null}
      </div>
      {footer ? <div className="relative z-10 border-t border-border px-3 py-2 text-xs text-secondary">{footer}</div> : null}
    </article>
  );
}

function SegmentInfoChip({ label, value }: { label: string; value: string }) {
  return (
    <span className="inline-flex min-w-0 items-center gap-1 rounded border border-border bg-surface/60 px-1.5 py-0.5">
      <span className="shrink-0 text-muted">{label}:</span>
      <span className="truncate text-foreground">{value}</span>
    </span>
  );
}

function formatSegmentRangeLabel(startSec: number, endSec?: number) {
  const start = formatDuration(startSec);
  return endSec == null ? start : `${start} - ${formatDuration(endSec)}`;
}

function formatSegmentSourceLabel(sourceKey?: string) {
  if (!sourceKey) {
    return "Unknown source";
  }

  if (sourceKey === "user") {
    return "User";
  }

  return sourceKey.startsWith("ext:")
    ? sourceKey.slice(4).split(/[._-]+/).filter(Boolean).map((part) => part.charAt(0).toUpperCase() + part.slice(1)).join(" ")
    : sourceKey;
}

function SegmentHoverPreview({ hostId, segmentId, updatedAt, startSec, endSec, title, className }: { hostId: number; segmentId?: number; updatedAt?: string; startSec: number; endSec?: number; title: string; className: string }) {
  return <SegmentPreviewMedia hostId={hostId} segmentId={segmentId} updatedAt={updatedAt} startSec={startSec} endSec={endSec} title={title} className={className} />;
}
