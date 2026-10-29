import { useMemo } from "react";
import { ExternalLink, FolderOpen } from "lucide-react";
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
  buildSpanTileSegment,
  buildSpanTitle,
  type DerivedOperandNameMaps,
  formatDate,
  formatDerivedQueryName,
  formatSegmentCardEyebrow,
  formatSegmentDuration,
  formatSegmentRange,
  Pill,
  SegmentVideoPreview,
} from "./segmentDisplayUtils";
import { useSegmentListDensity, type SegmentListDensity } from "./segmentListDensity";
import type { DerivedSpanItem } from "./types";
import { useDerivedOperandNameMaps } from "./useDerivedOperandNameMaps";

interface Props {
  displayMode: DisplayMode;
  items: DerivedSpanItem[];
  canReadVideos: boolean;
  onNavigate: (route: any) => void;
  onViewRawSegments: (segmentIds: number[]) => void;
  selectedIds: Set<string | number>;
  onToggle: MultiSelectToggleHandler<string | number>;
  selecting: boolean;
  infinitePageSize: boolean;
  hasNextPage?: boolean;
  isFetchingNextPage?: boolean;
  loadMore: () => void;
}

export function DerivedSpanResults({
  displayMode,
  items,
  canReadVideos,
  onNavigate,
  onViewRawSegments,
  selectedIds,
  onToggle,
  selecting,
  infinitePageSize,
  hasNextPage,
  isFetchingNextPage,
  loadMore,
}: Props) {
  const operands = useMemo(() => items.flatMap((item) => item.derivedQueryDescriptor?.operands ?? []), [items]);
  const nameMaps = useDerivedOperandNameMaps(operands);
  const listDensity = useSegmentListDensity();

  if (displayMode === "grid") {
    return (
      <VirtualizedEntityGrid
        items={items}
        getItemKey={(item) => item.key}
        minCardWidth="var(--card-min-width, 220px)"
        estimateRowHeight={320}
        infinitePageSize={infinitePageSize}
        hasNextPage={hasNextPage}
        isFetchingNextPage={isFetchingNextPage}
        loadMore={loadMore}
        renderItem={(item) => {
          const title =
            formatDerivedQueryName(item.derivedQueryDescriptor, nameMaps) ??
            buildSpanTitle(item.span, item.videoTitle, item.reference);
          const route = {
            page: "video-span",
            id: item.videoId,
            spanKey: item.span.spanKey,
            profileId: item.profileId,
            derivedQueryDescriptor: item.derivedQueryDescriptor,
          };
          const videoRoute = { page: "video", id: item.videoId, seekTo: item.span.startSec } as const;

          return (
            <SegmentTile
              segment={buildSpanTileSegment(item, title)}
              route={route}
              label={`Open segment ${title}`}
              eyebrow={formatSegmentCardEyebrow(item.span.startSec, item.span.endSec)}
              onClick={(toggleOptions) => (selecting ? onToggle(item.id, toggleOptions) : onNavigate(route))}
              selected={selectedIds.has(item.id)}
              onSelect={(toggleOptions) => onToggle(item.id, toggleOptions)}
              selecting={selecting}
              hostRoute={canReadVideos ? videoRoute : undefined}
              onOpenHost={() => onNavigate(videoRoute)}
            />
          );
        }}
      />
    );
  }

  return (
    <div className="overflow-hidden rounded-xl border border-border bg-card">
      <div className="hidden grid-cols-[minmax(0,1.4fr)_140px_minmax(0,1.1fr)_120px_120px] gap-3 border-b border-border bg-surface/70 px-4 py-2 text-[11px] font-medium uppercase tracking-wide text-muted lg:grid">
        <span>Span</span>
        <span>Range</span>
        <span>Video</span>
        <span>Source</span>
        <span>Updated</span>
      </div>
      <div className="divide-y divide-border">
        {items.map((item) => (
          <DerivedSpanListRow
            key={item.key}
            item={item}
            canReadVideos={canReadVideos}
            onNavigate={onNavigate}
            onViewRawSegments={onViewRawSegments}
            selected={selectedIds.has(item.id)}
            onToggle={(toggleOptions) => onToggle(item.id, toggleOptions)}
            selecting={selecting}
            nameMaps={nameMaps}
            density={listDensity}
          />
        ))}
      </div>
    </div>
  );
}

function DerivedSpanListRow({
  item,
  canReadVideos,
  onNavigate,
  onViewRawSegments,
  selected,
  onToggle,
  selecting,
  nameMaps,
  density,
}: {
  item: DerivedSpanItem;
  canReadVideos: boolean;
  onNavigate: (route: any) => void;
  onViewRawSegments: (segmentIds: number[]) => void;
  selected: boolean;
  onToggle: BoundMultiSelectToggleHandler<string | number>;
  selecting: boolean;
  nameMaps?: DerivedOperandNameMaps;
  density: SegmentListDensity;
}) {
  const derivedName = formatDerivedQueryName(item.derivedQueryDescriptor, nameMaps);
  const title = derivedName ?? buildSpanTitle(item.span, item.videoTitle, item.reference);
  const primaryRawSegmentId = item.span.segmentIds[0];
  const coverSegmentId = item.coverSegmentId ?? primaryRawSegmentId;

  return (
    <div
      onClick={selecting ? (event) => onToggle(toggleOptionsFromEvent(event)) : undefined}
      className={`video-card group relative cursor-pointer px-4 ${density.rowPaddingClassName} transition-colors ${selected ? "bg-accent/10" : "hover:bg-surface/40"}`}
    >
      <RouteCardLinkOverlay
        route={{
          page: "video-span",
          id: item.videoId,
          spanKey: item.span.spanKey,
          profileId: item.profileId,
          derivedQueryDescriptor: item.derivedQueryDescriptor,
        }}
        onClick={() =>
          onNavigate({
            page: "video-span",
            id: item.videoId,
            spanKey: item.span.spanKey,
            profileId: item.profileId,
            derivedQueryDescriptor: item.derivedQueryDescriptor,
          })
        }
        label={`Open span ${title}`}
        disabled={selecting}
        selectionSafeZone
      />
      <div className="flex items-start gap-3 lg:grid lg:grid-cols-[minmax(0,1.4fr)_140px_minmax(0,1.1fr)_120px_120px] lg:items-center">
        <div className="relative min-w-0 pl-8">
          <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onToggle} />
          <div className="flex items-start gap-3">
            {density.showPreview ? (
              <div
                className="hidden shrink-0 overflow-hidden rounded-lg bg-surface sm:block"
                style={{ height: density.previewHeight, width: density.previewWidth }}
              >
                <SegmentVideoPreview
                  hostId={item.videoId}
                  segmentId={coverSegmentId}
                  updatedAt={item.coverVersion ?? item.videoUpdatedAt}
                  startSec={item.span.startSec}
                  endSec={item.span.endSec}
                  title={title}
                  imgClassName="h-full w-full object-cover"
                />
              </div>
            ) : null}
            <div className="min-w-0">
              <div className="truncate text-sm font-medium text-foreground">{title}</div>
              <div className="mt-1 flex flex-wrap items-center gap-1.5 text-[11px] text-secondary">
                {item.span.kind && !derivedName ? <Pill>{item.span.kind}</Pill> : null}
                {item.reference?.faceSourceKey ? <Pill>{item.reference.faceSourceKey}</Pill> : null}
                <Pill>{formatSegmentDuration(item.span.startSec, item.span.endSec)}</Pill>
                <span>
                  {item.span.segmentIds.length} raw segment{item.span.segmentIds.length === 1 ? "" : "s"}
                </span>
              </div>
            </div>
          </div>
        </div>
        <div className="hidden text-xs text-secondary lg:block">
          {formatSegmentRange(item.span.startSec, item.span.endSec)}
        </div>
        <div className="min-w-0 text-xs text-secondary lg:text-sm">
          <div className="truncate text-foreground">{item.videoTitle}</div>
          <div className="mt-1 flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={(event) => {
                event.preventDefault();
                event.stopPropagation();
                onViewRawSegments(item.span.segmentIds);
              }}
              className="relative z-10 inline-flex items-center gap-1 text-accent hover:underline"
            >
              View raw segments ({item.span.segmentIds.length})
            </button>
            {primaryRawSegmentId != null ? (
              <button
                type="button"
                onClick={(event) => {
                  event.preventDefault();
                  event.stopPropagation();
                  onNavigate({ page: "segment", id: primaryRawSegmentId });
                }}
                className="relative z-10 inline-flex items-center gap-1 text-accent hover:underline"
              >
                <ExternalLink className="h-3.5 w-3.5" />
                Open raw
              </button>
            ) : null}
            {canReadVideos ? (
              <button
                type="button"
                onClick={(event) => {
                  event.preventDefault();
                  event.stopPropagation();
                  onNavigate({ page: "video", id: item.videoId, seekTo: item.span.startSec });
                }}
                className="relative z-10 mt-1 inline-flex items-center gap-1 text-accent hover:underline"
              >
                <FolderOpen className="h-3.5 w-3.5" />
                Open video
              </button>
            ) : null}
          </div>
        </div>
        <div className="hidden text-xs text-secondary lg:block">{item.span.sourceKey || "Derived"}</div>
        <div className="hidden text-xs text-secondary lg:block">{formatDate(item.videoUpdatedAt)}</div>
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-3 pl-8 text-[11px] text-secondary lg:hidden">
        <span>{formatSegmentRange(item.span.startSec, item.span.endSec)}</span>
        <span>{item.span.sourceKey || "Derived"}</span>
        <span>{formatDate(item.videoUpdatedAt)}</span>
      </div>
    </div>
  );
}
