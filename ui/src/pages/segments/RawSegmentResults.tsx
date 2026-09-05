import { FolderOpen } from "lucide-react";
import { VirtualizedEntityGrid } from "../../components/VirtualizedEntityLayouts";
import { SegmentTile } from "../../components/EntityCards";
import type { DisplayMode } from "../../components/ListPage";
import { CardSelectionToggle, RouteCardLinkOverlay } from "../../components/RouteCardLinkOverlay";
import {
  toggleOptionsFromEvent,
  type BoundMultiSelectToggleHandler,
  type MultiSelectToggleHandler,
} from "../../hooks/useMultiSelect";
import {
  buildRawSegmentTitle,
  formatDate,
  formatSegmentCardEyebrow,
  formatSourceLabel,
  formatSegmentDuration,
  formatSegmentRange,
  Pill,
  SegmentVideoPreview,
} from "./segmentDisplayUtils";
import { useSegmentListDensity, type SegmentListDensity } from "./segmentListDensity";
import type { RawSegmentItem } from "./types";

interface Props {
  displayMode: DisplayMode;
  items: RawSegmentItem[];
  canReadVideos: boolean;
  onNavigate: (route: any) => void;
  selectedIds: Set<string | number>;
  onToggle: MultiSelectToggleHandler<string | number>;
  selecting: boolean;
  infinitePageSize: boolean;
  hasNextPage?: boolean;
  isFetchingNextPage?: boolean;
  loadMore: () => void;
}

export function RawSegmentResults({
  displayMode,
  items,
  canReadVideos,
  onNavigate,
  selectedIds,
  onToggle,
  selecting,
  infinitePageSize,
  hasNextPage,
  isFetchingNextPage,
  loadMore,
}: Props) {
  const listDensity = useSegmentListDensity();

  if (displayMode === "grid") {
    return (
      <VirtualizedEntityGrid
        items={items}
        getItemKey={(item) => item.key}
        minCardWidth="var(--card-min-width, 220px)"
        estimateRowHeight={300}
        infinitePageSize={infinitePageSize}
        hasNextPage={hasNextPage}
        isFetchingNextPage={isFetchingNextPage}
        loadMore={loadMore}
        renderItem={(item) => (
          <SegmentTile
            segment={item}
            label={`Open raw segment ${buildRawSegmentTitle(item)}`}
            eyebrow={formatSegmentCardEyebrow(item.startSec, item.endSec)}
            onClick={(toggleOptions) =>
              selecting ? onToggle(item.id, toggleOptions) : onNavigate({ page: "segment", id: item.id })
            }
            selected={selectedIds.has(item.id)}
            onSelect={(toggleOptions) => onToggle(item.id, toggleOptions)}
            selecting={selecting}
            footer={
              <div className="flex items-center justify-between gap-2">
                <span>Updated {formatDate(item.updatedAt)}</span>
                {canReadVideos ? (
                  <button
                    type="button"
                    onClick={(event) => {
                      event.preventDefault();
                      event.stopPropagation();
                      onNavigate({ page: "video", id: item.hostId, seekTo: item.startSec });
                    }}
                    className="inline-flex items-center gap-1 text-accent hover:underline"
                  >
                    <FolderOpen className="h-3.5 w-3.5" />
                    Open video
                  </button>
                ) : null}
              </div>
            }
          />
        )}
      />
    );
  }

  return (
    <div className="overflow-hidden rounded-xl border border-border bg-card">
      <div className="hidden grid-cols-[minmax(0,1.3fr)_140px_minmax(0,1fr)_120px_120px] gap-3 border-b border-border bg-surface/70 px-4 py-2 text-[11px] font-medium uppercase tracking-wide text-muted lg:grid">
        <span>Segment</span>
        <span>Range</span>
        <span>Video</span>
        <span>Source</span>
        <span>Updated</span>
      </div>
      <div className="divide-y divide-border">
        {items.map((item) => (
          <RawSegmentListRow
            key={item.key}
            item={item}
            canReadVideos={canReadVideos}
            onNavigate={onNavigate}
            selected={selectedIds.has(item.id)}
            onToggle={(toggleOptions) => onToggle(item.id, toggleOptions)}
            selecting={selecting}
            density={listDensity}
          />
        ))}
      </div>
    </div>
  );
}

function RawSegmentListRow({
  item,
  canReadVideos,
  onNavigate,
  selected,
  onToggle,
  selecting,
  density,
}: {
  item: RawSegmentItem;
  canReadVideos: boolean;
  onNavigate: (route: any) => void;
  selected: boolean;
  onToggle: BoundMultiSelectToggleHandler<string | number>;
  selecting: boolean;
  density: SegmentListDensity;
}) {
  const title = buildRawSegmentTitle(item);

  return (
    <div
      onClick={selecting ? (event) => onToggle(toggleOptionsFromEvent(event)) : undefined}
      className={`video-card group relative cursor-pointer px-4 ${density.rowPaddingClassName} transition-colors ${selected ? "bg-accent/10" : "hover:bg-surface/40"}`}
    >
      <RouteCardLinkOverlay
        route={{ page: "segment", id: item.id }}
        onClick={() => onNavigate({ page: "segment", id: item.id })}
        label={`Open raw segment ${title}`}
        disabled={selecting}
        selectionSafeZone
      />
      <div className="flex items-start gap-3 lg:grid lg:grid-cols-[minmax(0,1.3fr)_140px_minmax(0,1fr)_120px_120px] lg:items-center">
        <div className="relative min-w-0 pl-8">
          <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onToggle} />
          <div className="flex items-start gap-3">
            {density.showPreview ? (
              <div
                className="hidden shrink-0 overflow-hidden rounded-lg bg-surface sm:block"
                style={{ height: density.previewHeight, width: density.previewWidth }}
              >
                <SegmentVideoPreview
                  hostId={item.hostId}
                  segmentId={item.id}
                  updatedAt={item.updatedAt}
                  startSec={item.startSec}
                  endSec={item.endSec}
                  title={title}
                  imgClassName="h-full w-full object-cover"
                />
              </div>
            ) : null}
            <div className="min-w-0">
              <div className="truncate text-sm font-medium text-foreground">{title}</div>
              <div className="mt-1 flex flex-wrap items-center gap-1.5 text-[11px] text-secondary">
                {item.tagName ? <Pill>{item.tagName}</Pill> : null}
                {item.kind ? <Pill>{item.kind}</Pill> : null}
                {item.performerName || item.refLabel ? <Pill>{item.performerName || item.refLabel}</Pill> : null}
                {item.confidence != null ? <Pill>{Math.round(item.confidence * 100)}%</Pill> : null}
              </div>
            </div>
          </div>
        </div>
        <div className="hidden text-xs text-secondary lg:block">{formatSegmentRange(item.startSec, item.endSec)}</div>
        <div className="min-w-0 text-xs text-secondary lg:text-sm">
          <div className="truncate text-foreground">{item.videoTitle}</div>
          {canReadVideos ? (
            <div className="mt-1 flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={(event) => {
                  event.preventDefault();
                  event.stopPropagation();
                  onNavigate({ page: "video", id: item.hostId, seekTo: item.startSec });
                }}
                className="relative z-10 inline-flex items-center gap-1 text-accent hover:underline"
              >
                <FolderOpen className="h-3.5 w-3.5" />
                Open video
              </button>
            </div>
          ) : null}
        </div>
        <div className="hidden text-xs text-secondary lg:block">{formatSourceLabel(item.sourceKey)}</div>
        <div className="hidden text-xs text-secondary lg:block">{formatDate(item.updatedAt)}</div>
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-3 pl-8 text-[11px] text-secondary lg:hidden">
        <span>{formatSegmentRange(item.startSec, item.endSec)}</span>
        <span>{formatSourceLabel(item.sourceKey)}</span>
        <span>{formatDate(item.updatedAt)}</span>
      </div>
    </div>
  );
}
