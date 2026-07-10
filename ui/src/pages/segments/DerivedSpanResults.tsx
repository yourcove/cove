import { useMemo } from "react";
import { useQueries } from "@tanstack/react-query";
import { ExternalLink, FolderOpen } from "lucide-react";
import { faces, performers, tags } from "../../api/client";
import { VirtualizedEntityGrid } from "../../components/VirtualizedEntityLayouts";
import { SegmentTile } from "../../components/EntityCards";
import type { DisplayMode } from "../../components/ListPage";
import { CardSelectionToggle, RouteCardLinkOverlay } from "../../components/RouteCardLinkOverlay";
import { toggleOptionsFromEvent, type BoundMultiSelectToggleHandler, type MultiSelectToggleHandler } from "../../hooks/useMultiSelect";
import {
  buildSpanTitle,
  type DerivedOperandNameMaps,
  formatDate,
  formatDerivedOperandSummary,
  formatSegmentCardEyebrow,
  formatSegmentDuration,
  formatSegmentRange,
  Pill,
  SegmentVideoPreview,
} from "./segmentDisplayUtils";
import { useSegmentListDensity, type SegmentListDensity } from "./segmentListDensity";
import type { DerivedSpanItem } from "./types";

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
  const { tagIds, performerIds, faceIds } = useMemo(() => {
    const tagSet = new Set<number>();
    const performerSet = new Set<number>();
    const faceSet = new Set<number>();
    for (const item of items) {
      const descriptor = item.derivedQueryDescriptor;
      if (!descriptor) continue;
      for (const operand of descriptor.operands) {
        operand.tagIds?.forEach((id) => tagSet.add(id));
        operand.performerIds?.forEach((id) => performerSet.add(id));
        operand.faceIds?.forEach((id) => faceSet.add(id));
      }
    }
    return {
      tagIds: Array.from(tagSet),
      performerIds: Array.from(performerSet),
      faceIds: Array.from(faceSet),
    };
  }, [items]);

  const tagQueries = useQueries({
    queries: tagIds.map((id) => ({ queryKey: ["tag", id], queryFn: () => tags.get(id), staleTime: 60_000 })),
  });
  const performerQueries = useQueries({
    queries: performerIds.map((id) => ({ queryKey: ["performer", id], queryFn: () => performers.get(id), staleTime: 60_000 })),
  });
  const faceQueries = useQueries({
    queries: faceIds.map((id) => ({ queryKey: ["face", id], queryFn: () => faces.get(id), staleTime: 60_000 })),
  });

  const nameMaps = useMemo<DerivedOperandNameMaps>(() => {
    const tagNamesById = new Map<number, string>();
    tagIds.forEach((id, index) => {
      const tag = tagQueries[index]?.data;
      if (tag) tagNamesById.set(id, tag.name);
    });
    const performerNamesById = new Map<number, string>();
    performerIds.forEach((id, index) => {
      const performer = performerQueries[index]?.data;
      if (performer) performerNamesById.set(id, performer.name);
    });
    const faceLabelsById = new Map<number, string>();
    faceIds.forEach((id, index) => {
      const face = faceQueries[index]?.data;
      if (face) faceLabelsById.set(id, face.label?.trim() || face.performerName?.trim() || `Face #${id}`);
    });
    return { tagNamesById, performerNamesById, faceLabelsById };
  }, [faceIds, faceQueries, performerIds, performerQueries, tagIds, tagQueries]);
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
          const title = buildSpanTitle(item.span, item.videoTitle);
          const route = { page: "video-span", id: item.videoId, spanKey: item.span.spanKey, profileId: item.profileId, derivedQueryDescriptor: item.derivedQueryDescriptor };
          const operandSummary = formatDerivedOperandSummary(item, nameMaps);
          const primaryRawSegmentId = item.span.segmentIds[0];

          return (
            <SegmentTile
              segment={{
                id: primaryRawSegmentId ?? item.id,
                hostType: "video",
                hostId: item.videoId,
                startSec: item.span.startSec,
                endSec: item.span.endSec,
                tagName: item.span.tagName,
                kind: item.span.kind,
                sourceKey: item.span.sourceKey,
                title,
                updatedAt: item.videoUpdatedAt,
                hostTitle: item.videoTitle,
              }}
              route={route}
              label={`Open segment ${title}`}
              eyebrow={formatSegmentCardEyebrow(item.span.startSec, item.span.endSec)}
              onClick={(toggleOptions) => (selecting ? onToggle(item.id, toggleOptions) : onNavigate(route))}
              selected={selectedIds.has(item.id)}
              onSelect={(toggleOptions) => onToggle(item.id, toggleOptions)}
              selecting={selecting}
              footer={(
                <div className="space-y-1.5">
                  {operandSummary ? <div className="line-clamp-2 text-foreground">{operandSummary}</div> : null}
                  <div className="flex items-center justify-between gap-2">
                    <span>Updated {formatDate(item.videoUpdatedAt)}</span>
                  <div className="flex items-center gap-2">
                    {canReadVideos ? (
                      <button
                        type="button"
                        onClick={(event) => {
                          event.preventDefault();
                          event.stopPropagation();
                          onNavigate({ page: "video", id: item.videoId, seekTo: item.span.startSec });
                        }}
                        className="inline-flex items-center gap-1 text-accent hover:underline"
                      >
                        <FolderOpen className="h-3.5 w-3.5" />
                        Open video
                      </button>
                    ) : null}
                  </div>
                  </div>
                </div>
              )}
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
  const title = buildSpanTitle(item.span, item.videoTitle);
  const primaryRawSegmentId = item.span.segmentIds[0];
  const operandSummary = formatDerivedOperandSummary(item, nameMaps);

  return (
    <div onClick={selecting ? (event) => onToggle(toggleOptionsFromEvent(event)) : undefined} className={`video-card group relative cursor-pointer px-4 ${density.rowPaddingClassName} transition-colors ${selected ? "bg-accent/10" : "hover:bg-surface/40"}`}>
      <RouteCardLinkOverlay route={{ page: "video-span", id: item.videoId, spanKey: item.span.spanKey, profileId: item.profileId, derivedQueryDescriptor: item.derivedQueryDescriptor }} onClick={() => onNavigate({ page: "video-span", id: item.videoId, spanKey: item.span.spanKey, profileId: item.profileId, derivedQueryDescriptor: item.derivedQueryDescriptor })} label={`Open span ${title}`} disabled={selecting} selectionSafeZone />
      <div className="flex items-start gap-3 lg:grid lg:grid-cols-[minmax(0,1.4fr)_140px_minmax(0,1.1fr)_120px_120px] lg:items-center">
        <div className="relative min-w-0 pl-8">
          <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onToggle} />
          <div className="flex items-start gap-3">
            {density.showPreview ? <div className="hidden shrink-0 overflow-hidden rounded-lg bg-surface sm:block" style={{ height: density.previewHeight, width: density.previewWidth }}>
              <SegmentVideoPreview hostId={item.videoId} segmentId={primaryRawSegmentId} updatedAt={item.videoUpdatedAt} startSec={item.span.startSec} endSec={item.span.endSec} title={title} imgClassName="h-full w-full object-cover" />
            </div> : null}
            <div className="min-w-0">
              <div className="truncate text-sm font-medium text-foreground">{title}</div>
              <div className="mt-1 flex flex-wrap items-center gap-1.5 text-[11px] text-secondary">
                {item.span.tagName ? <Pill>{item.span.tagName}</Pill> : null}
                {item.span.kind ? <Pill>{item.span.kind}</Pill> : null}
                <Pill>{formatSegmentDuration(item.span.startSec, item.span.endSec)}</Pill>
                <span>{item.span.segmentIds.length} raw segment{item.span.segmentIds.length === 1 ? "" : "s"}</span>
              </div>
              {density.showSecondaryDetails && operandSummary ? <div className="mt-1 line-clamp-2 text-xs text-secondary">{operandSummary}</div> : null}
            </div>
          </div>
        </div>
        <div className="hidden text-xs text-secondary lg:block">{formatSegmentRange(item.span.startSec, item.span.endSec)}</div>
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
