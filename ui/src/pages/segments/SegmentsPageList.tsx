import { Bookmark } from "lucide-react";
import type { DisplayMode } from "../../components/ListPage";
import { DerivedSpanResults } from "./DerivedSpanResults";
import { RawSegmentResults } from "./RawSegmentResults";
import type { AppliedDerivedQuery, DerivedSpanItem, RawSegmentItem } from "./types";
import type { MultiSelectToggleHandler } from "../../hooks/useMultiSelect";

interface Props {
  displayMode: DisplayMode;
  isRawView: boolean;
  rawItems: RawSegmentItem[];
  spanItems: DerivedSpanItem[];
  rawSegmentIds: number[];
  appliedQuery: AppliedDerivedQuery | null;
  isLoading: boolean;
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

export function SegmentsPageList({
  displayMode,
  isRawView,
  rawItems,
  spanItems,
  rawSegmentIds,
  appliedQuery,
  isLoading,
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
  const items = isRawView ? rawItems : spanItems;

  return (
    <>
      {isRawView ? (
        <RawSegmentResults
          displayMode={displayMode}
          items={rawItems}
          canReadVideos={canReadVideos}
          onNavigate={onNavigate}
          selectedIds={selectedIds}
          onToggle={onToggle}
          selecting={selecting}
          infinitePageSize={infinitePageSize}
          hasNextPage={hasNextPage}
          isFetchingNextPage={isFetchingNextPage}
          loadMore={loadMore}
        />
      ) : (
        <DerivedSpanResults
          displayMode={displayMode}
          items={spanItems}
          canReadVideos={canReadVideos}
          onNavigate={onNavigate}
          onViewRawSegments={onViewRawSegments}
          selectedIds={selectedIds}
          onToggle={onToggle}
          selecting={selecting}
          infinitePageSize={infinitePageSize}
          hasNextPage={hasNextPage}
          isFetchingNextPage={isFetchingNextPage}
          loadMore={loadMore}
        />
      )}

      {items.length === 0 && !isLoading ? (
        <div className="py-16 text-center text-secondary">
          <Bookmark className="mx-auto mb-3 h-12 w-12 text-muted opacity-50" />
          <p>
            {isRawView
              ? rawSegmentIds.length > 0
                ? "No raw segments matched the selected segment contents."
                : "No raw segments found for this scope."
              : appliedQuery != null
                ? "No segments matched the current query."
                : "No segments found for this profile and scope."}
          </p>
        </div>
      ) : null}
    </>
  );
}
