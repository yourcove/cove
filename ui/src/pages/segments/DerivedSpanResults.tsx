import { useMemo } from "react";
import { ExternalLink, Fingerprint, FolderOpen } from "lucide-react";
import { VirtualizedEntityGrid } from "../../components/VirtualizedEntityLayouts";
import { SegmentTile, SuggestedNameMarker } from "../../components/EntityCards";
import type { DisplayMode } from "../../components/ListPage";
import { CardSelectionToggle, RouteCardLinkOverlay } from "../../components/RouteCardLinkOverlay";
import {
  toggleOptionsFromEvent,
  type BoundMultiSelectToggleHandler,
  type MultiSelectToggleHandler,
} from "../../hooks/useMultiSelect";
import {
  buildSpanDisplayTitle,
  buildSpanTileSegment,
  type DerivedOperandNameMaps,
  formatDate,
  formatShownTitle,
  formatSegmentCardEyebrow,
  formatSegmentDuration,
  formatSegmentRange,
  Pill,
  SegmentVideoPreview,
  spanFaceRoute,
  spanUnnamedFaceId,
} from "./segmentDisplayUtils";
import { useSegmentListDensity, type SegmentListDensity } from "./segmentListDensity";
import type { DerivedSpanItem } from "./types";
import { useDerivedOperandNameMaps } from "./useDerivedOperandNameMaps";
import { useListedFaceNames, type ListedFaceName } from "./useListedFaceNames";

interface Props {
  displayMode: DisplayMode;
  items: DerivedSpanItem[];
  canReadVideos: boolean;
  canReadFaces: boolean;
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
  canReadFaces,
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
  const unnamedFaceIds = useMemo(
    () => items.flatMap((item) => (item.kind === "derivedQuery" ? [] : (spanUnnamedFaceId(item.reference) ?? []))),
    [items],
  );
  const faceNames = useListedFaceNames(unnamedFaceIds, canReadFaces);
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
          const { title, titleIsSuggestion } = buildSpanDisplayTitle(item, nameMaps, faceNames);
          const route = {
            page: "video-span",
            id: item.videoId,
            spanKey: item.span.spanKey,
            profileId: item.profileId,
            derivedQueryDescriptor: item.derivedQueryDescriptor,
          };
          const videoRoute = { page: "video", id: item.videoId, seekTo: item.span.startSec } as const;
          const faceRoute = canReadFaces ? spanFaceRoute(item) : undefined;

          return (
            <SegmentTile
              segment={{ ...buildSpanTileSegment(item, title), titleIsSuggestion }}
              route={route}
              label={`Open segment ${formatShownTitle(title, titleIsSuggestion)}`}
              eyebrow={formatSegmentCardEyebrow(item.span.startSec, item.span.endSec)}
              onClick={(toggleOptions) => (selecting ? onToggle(item.id, toggleOptions) : onNavigate(route))}
              selected={selectedIds.has(item.id)}
              onSelect={(toggleOptions) => onToggle(item.id, toggleOptions)}
              selecting={selecting}
              hostRoute={canReadVideos ? videoRoute : undefined}
              onOpenHost={() => onNavigate(videoRoute)}
              faceRoute={faceRoute}
              onOpenFace={() => faceRoute && onNavigate(faceRoute)}
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
            canReadFaces={canReadFaces}
            onNavigate={onNavigate}
            onViewRawSegments={onViewRawSegments}
            selected={selectedIds.has(item.id)}
            onToggle={(toggleOptions) => onToggle(item.id, toggleOptions)}
            selecting={selecting}
            nameMaps={nameMaps}
            faceNames={faceNames}
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
  canReadFaces,
  onNavigate,
  onViewRawSegments,
  selected,
  onToggle,
  selecting,
  nameMaps,
  faceNames,
  density,
}: {
  item: DerivedSpanItem;
  canReadVideos: boolean;
  canReadFaces: boolean;
  onNavigate: (route: any) => void;
  onViewRawSegments: (segmentIds: number[]) => void;
  selected: boolean;
  onToggle: BoundMultiSelectToggleHandler<string | number>;
  selecting: boolean;
  nameMaps?: DerivedOperandNameMaps;
  faceNames: ReadonlyMap<number, ListedFaceName>;
  density: SegmentListDensity;
}) {
  const { title, derivedName, titleIsSuggestion } = buildSpanDisplayTitle(item, nameMaps, faceNames);
  const primaryRawSegmentId = item.span.segmentIds[0];
  const coverSegmentId = item.coverSegmentId ?? primaryRawSegmentId;
  const faceRoute = canReadFaces ? spanFaceRoute(item) : undefined;

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
        label={`Open span ${formatShownTitle(title, titleIsSuggestion)}`}
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
                  title={formatShownTitle(title, titleIsSuggestion)}
                  imgClassName="h-full w-full object-cover"
                />
              </div>
            ) : null}
            <div className="min-w-0">
              <div className="flex min-w-0 items-center gap-1.5">
                <div className="truncate text-sm font-medium text-foreground">{title}</div>
                {titleIsSuggestion ? <SuggestedNameMarker /> : null}
              </div>
              <div className="mt-1 flex flex-wrap items-center gap-1.5 text-[11px] text-secondary">
                {item.span.kind && !derivedName ? <Pill>{item.span.kind}</Pill> : null}
                {item.reference?.faceSourceKey ? <Pill>{item.reference.faceSourceKey}</Pill> : null}
                <Pill>{formatSegmentDuration(item.span.startSec, item.span.endSec)}</Pill>
                {item.confidence != null ? <Pill>{Math.round(item.confidence * 100)}% confidence</Pill> : null}
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
            {faceRoute ? (
              <button
                type="button"
                onClick={(event) => {
                  event.preventDefault();
                  event.stopPropagation();
                  onNavigate(faceRoute);
                }}
                className="relative z-10 inline-flex items-center gap-1 text-accent hover:underline"
              >
                <Fingerprint className="h-3.5 w-3.5" />
                Open face
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
