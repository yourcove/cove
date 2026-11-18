import { useState, type ReactNode } from "react";
import type { Tag } from "../api/types";
import { Check, Film, Heart } from "lucide-react";
import { entityImages } from "../api/client";
import type { EntityEngagement, SavedFilterUIOptions, Video, VideoFile } from "../api/types";
import { formatDate, formatDateTime } from "../utils/dateFormat";
import { formatDuration, formatFileSize, getResolutionLabel, RatingBadge, TagBadge } from "./shared";
import { LoadedImage } from "./LoadedImage";

export const VIDEO_LIST_COLUMNS_UI_OPTION_KEY = "listColumns";

const MAX_INLINE_PERFORMERS = 3;
const MAX_INLINE_TAGS = 4;

/** Minimum width reserved for each fluid (unsized) column before the table starts to scroll horizontally. */
export const VIDEO_LIST_FLUID_COLUMN_MIN_WIDTH_PX = 160;
export const VIDEO_LIST_SELECTION_COLUMN_WIDTH_PX = 40;
export const VIDEO_LIST_CELL_PADDING_PX = 24;

export interface VideoListCellContext {
  onNavigate: (route: any) => void;
  thumbnailHeightPx: number;
  /** A row's tags for rendering; a list entry's tag ids are resolved through the tag lookup. */
  tagsOf: (video: Video) => Tag[];
}

export interface VideoListColumnDefinition {
  id: VideoListColumnId;
  label: string;
  /**
   * Fixed column width in pixels. Columns without a width are fluid and share the remaining table width equally
   * (see VIDEO_LIST_FLUID_COLUMN_MIN_WIDTH_PX). The thumbnail column sizes itself from the current zoom level.
   */
  widthPx?: number;
  /** Extra classes applied to each body cell. */
  cellClassName?: string;
  align?: "left" | "right" | "center";
  render: (video: Video, engagement: EntityEngagement | undefined, context: VideoListCellContext) => ReactNode;
}

export type VideoListColumnId =
  | "thumbnail"
  | "title"
  | "studio"
  | "date"
  | "productionDate"
  | "duration"
  | "resolution"
  | "fileSize"
  | "frameRate"
  | "bitRate"
  | "codec"
  | "performers"
  | "tags"
  | "rating"
  | "playCount"
  | "likeCount"
  | "favorite"
  | "lastPlayedAt"
  | "code"
  | "director"
  | "filename"
  | "path"
  | "fileCount"
  | "groups"
  | "galleries"
  | "organized"
  | "isVr"
  | "createdAt"
  | "updatedAt";

export const DEFAULT_VIDEO_LIST_COLUMN_IDS: readonly VideoListColumnId[] = [
  "thumbnail",
  "title",
  "studio",
  "date",
  "duration",
  "resolution",
  "performers",
  "tags",
  "rating",
];

export function getVideoPrimaryFile(video: Video): VideoFile | undefined {
  if (video.primaryFileId != null) {
    const primary = video.files.find((file) => file.id === video.primaryFileId);
    if (primary) return primary;
  }
  return video.files[0];
}

export function getVideoDisplayDuration(video: Video) {
  if (typeof video.clipStartSec === "number" && typeof video.clipEndSec === "number") {
    return Math.max(0, video.clipEndSec - video.clipStartSec);
  }

  return getVideoPrimaryFile(video)?.duration ?? 0;
}

export function getVideoListTitle(video: Video) {
  return video.title || getVideoPrimaryFile(video)?.basename || `Video ${video.id}`;
}

function stopRowClick(event: { stopPropagation: () => void }) {
  event.stopPropagation();
}

function InlineLinkButton({ label, onClick, className }: { label: string; onClick: () => void; className?: string }) {
  return (
    <button
      type="button"
      onClick={(event) => {
        stopRowClick(event);
        onClick();
      }}
      className={`truncate text-left text-secondary transition-colors hover:text-accent ${className ?? ""}`}
    >
      {label}
    </button>
  );
}

function OverflowCount({ count }: { count: number }) {
  if (count <= 0) return null;
  return <span className="text-xs text-muted">+{count}</span>;
}

function VideoListThumbnail({ video, heightPx }: { video: Video; heightPx: number }) {
  const [failed, setFailed] = useState(false);
  return (
    <div
      className="flex shrink-0 items-center justify-center overflow-hidden rounded-md border border-border/70 bg-surface/80 text-muted"
      style={{ height: heightPx, width: Math.round((heightPx * 16) / 9) }}
    >
      {failed ? (
        <Film className="h-5 w-5" aria-hidden="true" />
      ) : (
        <LoadedImage
          src={entityImages.videoCoverUrl(video.id, video.updatedAt, 320)}
          alt=""
          loading="lazy"
          className="h-full w-full object-cover"
          onError={() => setFailed(true)}
        />
      )}
    </div>
  );
}

function BooleanCell({ value, label }: { value: boolean | undefined; label: string }) {
  return value ? <Check className="mx-auto h-4 w-4 text-accent" aria-label={label} /> : null;
}

const NUMERIC_CELL = "whitespace-nowrap tabular-nums text-secondary";
const TEXT_CELL = "text-secondary";
const DATE_CELL = "whitespace-nowrap text-secondary";

export const VIDEO_LIST_COLUMNS: readonly VideoListColumnDefinition[] = [
  {
    id: "thumbnail",
    label: "Thumbnail",
    cellClassName: "py-1",
    render: (video, _engagement, context) => <VideoListThumbnail video={video} heightPx={context.thumbnailHeightPx} />,
  },
  {
    id: "title",
    label: "Title",
    render: (video) => {
      const title = getVideoListTitle(video);
      // A real button keeps the row reachable by keyboard; the click bubbles to the row, which opens the video or
      // toggles selection depending on the current selection state.
      return (
        <button
          type="button"
          className="block w-full truncate text-left font-medium text-foreground transition-colors hover:text-accent"
          title={title}
        >
          {title}
        </button>
      );
    },
  },
  {
    id: "studio",
    label: "Studio",
    widthPx: 160,
    cellClassName: TEXT_CELL,
    render: (video, _engagement, context) => {
      if (!video.studioName) return null;
      if (video.studioId == null) return <span className="truncate">{video.studioName}</span>;
      const studioId = video.studioId;
      return (
        <InlineLinkButton
          label={video.studioName}
          onClick={() => context.onNavigate({ page: "studio", id: studioId })}
          className="max-w-full"
        />
      );
    },
  },
  {
    id: "date",
    label: "Date",
    widthPx: 104,
    cellClassName: DATE_CELL,
    render: (video) => (video.date ? formatDate(video.date) : null),
  },
  {
    id: "productionDate",
    label: "Production Date",
    widthPx: 120,
    cellClassName: DATE_CELL,
    render: (video) => (video.productionDate ? formatDate(video.productionDate) : null),
  },
  {
    id: "duration",
    label: "Duration",
    widthPx: 96,
    align: "right",
    cellClassName: NUMERIC_CELL,
    render: (video) => formatDuration(getVideoDisplayDuration(video)),
  },
  {
    id: "resolution",
    label: "Resolution",
    widthPx: 104,
    align: "right",
    cellClassName: NUMERIC_CELL,
    render: (video) => {
      const file = getVideoPrimaryFile(video);
      if (!file?.width || !file.height) return null;
      return getResolutionLabel(file.width, file.height) ?? `${file.width}x${file.height}`;
    },
  },
  {
    id: "fileSize",
    label: "Size",
    widthPx: 96,
    align: "right",
    cellClassName: NUMERIC_CELL,
    render: (video) => {
      const size = getVideoPrimaryFile(video)?.size;
      return size ? formatFileSize(size) : null;
    },
  },
  {
    id: "frameRate",
    label: "FPS",
    widthPx: 72,
    align: "right",
    cellClassName: NUMERIC_CELL,
    render: (video) => {
      const frameRate = getVideoPrimaryFile(video)?.frameRate;
      return frameRate ? Math.round(frameRate * 100) / 100 : null;
    },
  },
  {
    id: "bitRate",
    label: "Bitrate",
    widthPx: 104,
    align: "right",
    cellClassName: NUMERIC_CELL,
    render: (video) => {
      const bitRate = getVideoPrimaryFile(video)?.bitRate;
      return bitRate ? `${Math.round(bitRate / 1000)} kbps` : null;
    },
  },
  {
    id: "codec",
    label: "Codec",
    widthPx: 128,
    cellClassName: `whitespace-nowrap ${TEXT_CELL}`,
    render: (video) => {
      const file = getVideoPrimaryFile(video);
      return [file?.videoCodec, file?.audioCodec].filter(Boolean).join(" / ") || null;
    },
  },
  {
    id: "performers",
    label: "Performers",
    widthPx: 160,
    render: (video, _engagement, context) => {
      if (video.performers.length === 0) return null;
      const shown = video.performers.slice(0, MAX_INLINE_PERFORMERS);
      return (
        <span className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5">
          {shown.map((performer, index) => (
            <span key={performer.id} className="inline-flex max-w-full items-center">
              <InlineLinkButton
                label={performer.name}
                onClick={() => context.onNavigate({ page: "performer", id: performer.id })}
              />
              {index < shown.length - 1 ? <span className="text-muted">,</span> : null}
            </span>
          ))}
          <OverflowCount count={video.performers.length - shown.length} />
        </span>
      );
    },
  },
  {
    id: "tags",
    label: "Tags",
    widthPx: 208,
    render: (video, _engagement, context) => {
      const tags = context.tagsOf(video);
      if (tags.length === 0) return null;
      const shown = tags.slice(0, MAX_INLINE_TAGS);
      return (
        <span className="flex flex-wrap items-center gap-1 whitespace-nowrap" onClick={stopRowClick}>
          {shown.map((tag) => (
            <TagBadge
              key={tag.id}
              name={tag.name}
              tag={tag}
              onClick={() => context.onNavigate({ page: "tag", id: tag.id })}
            />
          ))}
          <OverflowCount count={tags.length - shown.length} />
        </span>
      );
    },
  },
  {
    id: "rating",
    label: "Rating",
    widthPx: 80,
    align: "center",
    render: (_video, engagement) =>
      engagement?.rating != null ? (
        <span className="inline-flex justify-center">
          <RatingBadge rating={engagement.rating} />
        </span>
      ) : null,
  },
  {
    id: "playCount",
    label: "Plays",
    widthPx: 72,
    align: "right",
    cellClassName: NUMERIC_CELL,
    render: (_video, engagement) => engagement?.playCount ?? 0,
  },
  {
    id: "likeCount",
    label: "Likes",
    widthPx: 72,
    align: "right",
    cellClassName: NUMERIC_CELL,
    render: (_video, engagement) => engagement?.likeCount ?? 0,
  },
  {
    id: "favorite",
    label: "Favorite",
    widthPx: 80,
    align: "center",
    render: (_video, engagement) =>
      engagement?.isFavorite ? (
        <Heart className="mx-auto h-4 w-4 fill-red-500 text-red-500" aria-label="Favorite" />
      ) : null,
  },
  {
    id: "lastPlayedAt",
    label: "Last played",
    widthPx: 168,
    cellClassName: DATE_CELL,
    render: (_video, engagement) => (engagement?.lastPlayedAt ? formatDateTime(engagement.lastPlayedAt) : null),
  },
  {
    id: "code",
    label: "Code",
    widthPx: 112,
    cellClassName: `whitespace-nowrap ${TEXT_CELL}`,
    render: (video) => video.code ?? null,
  },
  {
    id: "director",
    label: "Director",
    widthPx: 160,
    cellClassName: `truncate ${TEXT_CELL}`,
    render: (video) => video.director ?? null,
  },
  {
    id: "filename",
    label: "Filename",
    cellClassName: TEXT_CELL,
    render: (video) => {
      const basename = getVideoPrimaryFile(video)?.basename;
      return basename ? (
        <span className="block truncate" title={basename}>
          {basename}
        </span>
      ) : null;
    },
  },
  {
    id: "path",
    label: "Path",
    cellClassName: "font-mono text-xs text-muted",
    render: (video) => {
      const path = getVideoPrimaryFile(video)?.path;
      return path ? (
        <span className="block truncate" title={path}>
          {path}
        </span>
      ) : null;
    },
  },
  {
    id: "fileCount",
    label: "Files",
    widthPx: 64,
    align: "right",
    cellClassName: NUMERIC_CELL,
    render: (video) => video.files.length,
  },
  {
    id: "groups",
    label: "Groups",
    cellClassName: TEXT_CELL,
    render: (video) => {
      if (video.groups.length === 0) return null;
      const label = video.groups.map((group) => group.name).join(", ");
      return (
        <span className="block truncate" title={label}>
          {label}
        </span>
      );
    },
  },
  {
    id: "galleries",
    label: "Galleries",
    widthPx: 88,
    align: "right",
    cellClassName: NUMERIC_CELL,
    render: (video) => video.galleries.length,
  },
  {
    id: "organized",
    label: "Organized",
    widthPx: 96,
    align: "center",
    render: (video) => <BooleanCell value={video.organized} label="Organized" />,
  },
  {
    id: "isVr",
    label: "VR",
    widthPx: 56,
    align: "center",
    render: (video) => <BooleanCell value={video.isVr} label="VR" />,
  },
  {
    id: "createdAt",
    label: "Created",
    widthPx: 168,
    cellClassName: DATE_CELL,
    render: (video) => formatDateTime(video.createdAt),
  },
  {
    id: "updatedAt",
    label: "Updated",
    widthPx: 168,
    cellClassName: DATE_CELL,
    render: (video) => formatDateTime(video.updatedAt),
  },
];

export const VIDEO_LIST_COLUMN_BY_ID: ReadonlyMap<VideoListColumnId, VideoListColumnDefinition> = new Map(
  VIDEO_LIST_COLUMNS.map((column) => [column.id, column]),
);

export const VIDEO_LIST_COLUMN_IDS: readonly VideoListColumnId[] = VIDEO_LIST_COLUMNS.map((column) => column.id);

function isVideoListColumnId(value: string): value is VideoListColumnId {
  return VIDEO_LIST_COLUMN_BY_ID.has(value as VideoListColumnId);
}

/**
 * Reads a column id list from untrusted saved-filter JSON. Unknown ids and duplicates are dropped;
 * anything that is not a non-empty array of known ids yields `undefined` so callers fall back to defaults.
 */
export function normalizeVideoListColumns(value: unknown): VideoListColumnId[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const seen = new Set<VideoListColumnId>();
  const result: VideoListColumnId[] = [];
  for (const entry of value) {
    if (typeof entry !== "string") continue;
    const id = entry.trim();
    if (!isVideoListColumnId(id) || seen.has(id)) continue;
    seen.add(id);
    result.push(id);
  }
  return result.length > 0 ? result : undefined;
}

export function resolveVideoListColumns(uiOptions?: SavedFilterUIOptions | Record<string, unknown> | null) {
  return normalizeVideoListColumns(uiOptions?.[VIDEO_LIST_COLUMNS_UI_OPTION_KEY]) ?? [...DEFAULT_VIDEO_LIST_COLUMN_IDS];
}

export function isDefaultVideoListColumns(columnIds: readonly VideoListColumnId[]) {
  return (
    columnIds.length === DEFAULT_VIDEO_LIST_COLUMN_IDS.length &&
    columnIds.every((id, index) => id === DEFAULT_VIDEO_LIST_COLUMN_IDS[index])
  );
}

export const VIDEO_LIST_COLUMN_WIDTHS_UI_OPTION_KEY = "listColumnWidths";
export const VIDEO_LIST_MIN_COLUMN_WIDTH_PX = 48;
export const VIDEO_LIST_MAX_COLUMN_WIDTH_PX = 1600;

/** User-resized column widths in pixels, keyed by column id. Columns without an entry use their built-in sizing. */
export type VideoListColumnWidths = Partial<Record<VideoListColumnId, number>>;

export function clampVideoListColumnWidth(width: number) {
  return Math.min(VIDEO_LIST_MAX_COLUMN_WIDTH_PX, Math.max(VIDEO_LIST_MIN_COLUMN_WIDTH_PX, Math.round(width)));
}

/**
 * Reads user column widths from untrusted saved-filter JSON. Unknown ids and non-finite values are dropped and
 * widths are clamped; anything that is not an object with at least one valid entry yields `undefined`.
 */
export function normalizeVideoListColumnWidths(value: unknown): VideoListColumnWidths | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  const result: VideoListColumnWidths = {};
  let count = 0;
  for (const [key, width] of Object.entries(value as Record<string, unknown>)) {
    if (!isVideoListColumnId(key) || typeof width !== "number" || !Number.isFinite(width)) continue;
    result[key] = clampVideoListColumnWidth(width);
    count += 1;
  }
  return count > 0 ? result : undefined;
}

export function resolveVideoListColumnWidths(
  uiOptions?: SavedFilterUIOptions | Record<string, unknown> | null,
): VideoListColumnWidths {
  return normalizeVideoListColumnWidths(uiOptions?.[VIDEO_LIST_COLUMN_WIDTHS_UI_OPTION_KEY]) ?? {};
}

export function hasVideoListColumnWidths(widths: VideoListColumnWidths) {
  return Object.keys(widths).length > 0;
}
