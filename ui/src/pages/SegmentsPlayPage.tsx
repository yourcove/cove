import { useCallback, useMemo, useState } from "react";
import { useInfiniteQuery } from "@tanstack/react-query";
import { segmentLibrary, segmentSpans } from "../api/client";
import type { PlaybackTrackingTarget } from "../utils/interactionTracking";
import { useAuth } from "../auth/AuthContext";
import { canReadEntity } from "../auth/visibility";
import { CompilationPlayer, type CompilationPlayerItem } from "../components/CompilationPlayer";
import { DetailSkeleton } from "../components/DetailSkeleton";
import { useBackNavigation } from "../hooks/useBackNavigation";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import type { Route } from "../router/location";
import type {
  SegmentDerivedQueryDescriptor,
  SegmentFilteredQueryRequest,
  SegmentSpanSearchRequest,
} from "../api/types";
import { readRawSegmentIdsFromUrl, readSegmentsPageContentView } from "./segments/segmentCriteriaDefinitions";
import { readSegmentsListDefaults, useSegmentsListUrlState } from "./segments/segmentsListState";
import { useSegmentDisplayProfiles, useSegmentsQueryInputs } from "./segments/useSegmentsQueryInputs";
import { buildSegmentFilteredQuery } from "./segments/useRawSegmentsQuery";
import {
  buildRawSegmentTitle,
  buildSpanDisplayTitle,
  formatShownTitle,
  rawSegmentFaceName,
  rawSegmentPlaceholderFaceId,
  spanUnnamedFaceId,
} from "./segments/segmentDisplayUtils";
import type { DerivedSpanItem, RawSegmentItem } from "./segments/types";
import { useDerivedOperandNameMaps } from "./segments/useDerivedOperandNameMaps";
import { buildSpanSearchRequest, toDerivedSpanItem } from "./segments/useDerivedSpansQuery";
import { useListedFaceNames } from "./segments/useListedFaceNames";

/** Items fetched per request; playback asks for the next page a few items before it runs out. */
export const SEGMENTS_PLAYBACK_PAGE_SIZE = 100;
const SEGMENTS_PLAYBACK_SURFACE = "segmentsPlayback";

interface Props {
  onNavigate: (route: Route) => void;
}

/**
 * A Segments list to play: the request the list sends for its view, which playback pages through by changing
 * only the page, and the route back to that list.
 */
type SegmentsPlaybackSource = (
  | { view: "spans"; request: SegmentSpanSearchRequest; derivedQueryDescriptor?: SegmentDerivedQueryDescriptor }
  | { view: "raw"; request: SegmentFilteredQueryRequest }
) & { returnRoute: Route };

type PlaybackPage =
  | { view: "spans"; items: DerivedSpanItem[]; totalCount?: number; hasMore: boolean }
  | { view: "raw"; items: RawSegmentItem[]; totalCount?: number; hasMore: boolean };

/**
 * Plays a Segments list in the compilation player. The list comes from this page's URL, which takes the
 * Segments page's own parameters and reads them with the same defaults and hooks, so a playback link plays
 * what the Segments page lists for it, in the same order: filters, derived query, sort, seed and profile.
 */
export function SegmentsPlayPage({ onNavigate }: Props) {
  const { source, unavailable } = useSegmentsPlaybackSource();
  const { backLabel, goBack, backAlternative } = useBackNavigation(
    source?.returnRoute ?? { page: "segments" },
    onNavigate,
  );
  const { hasPermission } = useAuth();
  const canReadFaces = canReadEntity("face", hasPermission);
  useDocumentTitle("Segment playback");

  const query = useInfiniteQuery({
    queryKey: ["segments-play", source ?? null],
    enabled: source != null,
    initialPageParam: 1,
    queryFn: ({ pageParam }) => fetchPlaybackPage(source!, pageParam),
    getNextPageParam: (lastPage, pages) => (lastPage.hasMore ? pages.length + 1 : undefined),
    staleTime: Infinity,
  });

  const spanItems = useMemo(
    () => query.data?.pages.flatMap((page) => (page.view === "spans" ? page.items : [])) ?? [],
    [query.data],
  );
  const rawItems = useMemo(
    () => query.data?.pages.flatMap((page) => (page.view === "raw" ? page.items : [])) ?? [],
    [query.data],
  );
  const nameMaps = useDerivedOperandNameMaps(
    useMemo(() => (source?.view === "spans" ? (source.derivedQueryDescriptor?.operands ?? []) : []), [source]),
  );
  const faceIds = useMemo(
    () => [
      ...spanItems.flatMap((item) => (item.kind === "derivedQuery" ? [] : (spanUnnamedFaceId(item.reference) ?? []))),
      ...rawItems.flatMap((item) => rawSegmentPlaceholderFaceId(item) ?? []),
    ],
    [rawItems, spanItems],
  );
  const listedFaceNames = useListedFaceNames(faceIds, canReadFaces);
  // The lookup hands back a new map on every render; keeping it while its names are unchanged keeps the
  // player's items stable, so the playing item is not re-seeked or its display timer restarted.
  const faceNamesKey = JSON.stringify([...listedFaceNames]);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- the key stands for the map's content
  const faceNames = useMemo(() => listedFaceNames, [faceNamesKey]);

  const items = useMemo<CompilationPlayerItem[]>(() => {
    if (source?.view === "spans") {
      return spanItems.flatMap((item, index) => {
        const { title, titleIsSuggestion } = buildSpanDisplayTitle(item, nameMaps, faceNames);
        const playerItem = spanPlayerItem(item, index, formatShownTitle(title, titleIsSuggestion));
        return playerItem ? [playerItem] : [];
      });
    }
    return rawItems.flatMap((item, index) => {
      const faceName = rawSegmentFaceName(item, faceNames);
      const title = formatShownTitle(faceName?.name ?? buildRawSegmentTitle(item), faceName?.suggested);
      const playerItem = rawPlayerItem(item, index, title);
      return playerItem ? [playerItem] : [];
    });
  }, [faceNames, nameMaps, rawItems, source?.view, spanItems]);

  const lastPage = query.data?.pages.at(-1);
  const totalCount = query.hasNextPage ? lastPage?.totalCount : undefined;
  const { fetchNextPage, hasNextPage, isFetchingNextPage, isFetchNextPageError } = query;
  // After a page fails, playback stops asking for it; the notice offers a retry instead of a request loop.
  const loadMore = useCallback(() => {
    if (hasNextPage && !isFetchingNextPage && !isFetchNextPageError) void fetchNextPage();
  }, [fetchNextPage, hasNextPage, isFetchingNextPage, isFetchNextPageError]);

  if (unavailable) {
    return (
      <div className="py-16 text-center text-secondary">
        <p>{unavailable}</p>
        <button
          type="button"
          onClick={() => onNavigate({ page: "segments" })}
          className="mt-4 rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent"
        >
          Open Segments
        </button>
      </div>
    );
  }

  if (!source || query.isLoading) {
    return (
      <div className="px-1 py-2">
        <DetailSkeleton />
      </div>
    );
  }

  // Only a list that never loaded is an error page; a later page failing keeps what is already playing.
  if (query.isError && !query.data) {
    return <div className="py-16 text-center text-danger">Segments failed to load: {query.error.message}</div>;
  }

  if (items.length === 0) {
    return <div className="py-16 text-center text-secondary">No playable segments match these filters.</div>;
  }

  return (
    <CompilationPlayer
      groupName={source.view === "raw" ? "Raw segments" : "Segments"}
      items={items}
      sourceKey={`segments:${JSON.stringify(source.request)}`}
      returnTarget={{ route: source.returnRoute, label: "Back to Segments" }}
      totalCount={totalCount}
      hasMore={hasNextPage}
      onNearEnd={loadMore}
      getPlaybackTracking={segmentPlaybackTracking}
      notice={
        isFetchNextPageError ? (
          <div className="rounded-lg border border-danger/40 bg-danger/10 px-3 py-2 text-sm text-danger">
            More segments failed to load.{" "}
            <button type="button" onClick={() => void fetchNextPage()} className="underline underline-offset-2">
              Retry
            </button>
          </div>
        ) : null
      }
      onNavigate={onNavigate}
      backLabel={backLabel}
      onGoBack={goBack}
      backAlternative={backAlternative}
    />
  );
}

async function fetchPlaybackPage(source: SegmentsPlaybackSource, page: number): Promise<PlaybackPage> {
  const perPage = SEGMENTS_PLAYBACK_PAGE_SIZE;
  if (source.view === "spans") {
    const response = await segmentSpans.search({ ...source.request, page, perPage });
    // A page served by early termination reports -1 and says whether more follow.
    const knownTotal = response.totalCount >= 0 ? response.totalCount : undefined;
    return {
      view: "spans",
      items: response.items.map((item) =>
        toDerivedSpanItem(item, source.request.derivedQuery, source.derivedQueryDescriptor),
      ),
      totalCount: knownTotal,
      hasMore: response.hasMore ?? (knownTotal != null && page * perPage < knownTotal),
    };
  }

  const response = await segmentLibrary.findFiltered({
    ...source.request,
    findFilter: { ...source.request.findFilter, page, perPage },
  });
  return {
    view: "raw",
    items: response.items.map((item) => ({
      ...item,
      key: `segment:${item.id}`,
      videoId: item.hostId,
      videoTitle: item.hostTitle?.trim() || `Video #${item.hostId}`,
    })),
    totalCount: response.totalCount,
    hasMore: page * perPage < response.totalCount,
  };
}

/**
 * Plays from this page are segment plays, not plays of the video page: a raw segment is tracked like on its
 * segment page and a span like on its span page, so neither moves the video's own resume position.
 */
function segmentPlaybackTracking(item: CompilationPlayerItem): PlaybackTrackingTarget {
  const clip = { clipStartSec: item.startSec, clipEndSec: item.endSec ?? null };
  if (item.segmentId != null) {
    return {
      hostType: "segment",
      hostId: item.segmentId,
      surface: SEGMENTS_PLAYBACK_SURFACE,
      scopeKey: `segment:${item.segmentId}`,
      ...(item.videoId != null ? { parentHostType: "video", parentHostId: item.videoId } : {}),
      itemHostType: item.videoId != null ? "video" : item.audioId != null ? "audio" : "image",
      itemHostId: item.videoId ?? item.audioId ?? item.imageId ?? undefined,
      segmentId: item.segmentId,
      ...clip,
    };
  }
  const spanKey = item.detailRoute?.spanKey;
  return {
    hostType: "video",
    hostId: item.videoId ?? item.hostId,
    surface: SEGMENTS_PLAYBACK_SURFACE,
    scopeKey: `video:${item.videoId}:span:${spanKey}`,
    itemHostType: "video",
    itemHostId: item.videoId ?? undefined,
    ...clip,
    context: { spanKey },
  };
}

/** A resolved span plays its whole range on its video; its row links to the span's own page. */
function spanPlayerItem(item: DerivedSpanItem, index: number, title: string): CompilationPlayerItem | null {
  if (item.span.hostType !== "video") return null;
  return {
    // Positions are unique within one playback and stable while more pages load.
    groupItemId: index + 1,
    hostType: "segment",
    hostId: item.span.hostId,
    videoId: item.videoId,
    videoTitle: item.videoTitle,
    src: `/api/stream/video/${item.videoId}`,
    startSec: item.span.startSec,
    endSec: item.span.endSec,
    durationSec: Math.max(0, item.span.endSec - item.span.startSec),
    title,
    hasVideoTrack: false,
    detailRoute: {
      page: "video-span",
      id: item.videoId,
      spanKey: item.span.spanKey,
      profileId: item.profileId,
      derivedQueryDescriptor: item.derivedQueryDescriptor,
    },
  };
}

/** A raw segment plays on the media that hosts it, like a segment item of a group. */
function rawPlayerItem(item: RawSegmentItem, index: number, title: string): CompilationPlayerItem | null {
  const base = {
    groupItemId: index + 1,
    hostType: "segment",
    hostId: item.id,
    segmentId: item.id,
    startSec: item.startSec,
    endSec: item.endSec ?? null,
    durationSec: item.endSec != null ? Math.max(0, item.endSec - item.startSec) : undefined,
    title,
    hasVideoTrack: false,
  };
  switch (item.hostType) {
    case "video":
      return {
        ...base,
        videoId: item.hostId,
        videoTitle: item.videoTitle,
        src: `/api/stream/video/${item.hostId}`,
      };
    case "audio":
      // The player streams audio by its id; the manifest's src is only read for images and texts.
      return { ...base, audioId: item.hostId, src: "" };
    case "image":
      return { ...base, imageId: item.hostId, src: "" };
    default:
      return null;
  }
}

/** The list this page's URL describes, once everything it needs to be requested has loaded. */
function useSegmentsPlaybackSource(): { source?: SegmentsPlaybackSource; unavailable?: string } {
  // The view and raw-id narrowing belong to the link that opened this page, like the rest of its URL.
  const [view] = useState(readSegmentsPageContentView);
  const [rawSegmentIds] = useState(readRawSegmentIdsFromUrl);
  const isRawView = view === "raw";
  const defaults = useMemo(() => readSegmentsListDefaults(view), [view]);
  const { filter, objectFilter } = useSegmentsListUrlState("segments-play", defaults);
  const { profilesQuery, availableProfiles } = useSegmentDisplayProfiles(!isRawView);
  // The Segments page's own fallback: the linked profile while it exists, otherwise the default one.
  const activeProfileId = availableProfiles.some((profile) => profile.id === defaults.profileId)
    ? defaults.profileId
    : (availableProfiles.find((profile) => profile.isDefault)?.id ?? availableProfiles[0]?.id);
  const inputs = useSegmentsQueryInputs({ filter, objectFilter, isRawView, activeProfileId });

  const source = useMemo<SegmentsPlaybackSource | undefined>(() => {
    const returnRoute: Route = {
      page: "segments",
      listFilter: filter,
      listObjectFilter: Object.keys(objectFilter).length > 0 ? objectFilter : undefined,
      segmentsView: isRawView ? "raw" : undefined,
      profileId: isRawView ? undefined : activeProfileId,
      rawSegmentIds: isRawView && rawSegmentIds.length > 0 ? rawSegmentIds : undefined,
    };
    const listInputs = {
      pageNumber: 1,
      perPage: SEGMENTS_PLAYBACK_PAGE_SIZE,
      q: inputs.q,
      videoTitle: inputs.videoTitle,
      videoTagIds: inputs.videoTagIds,
      videoTagDepth: inputs.videoTagDepth,
      sort: inputs.sort,
      direction: inputs.direction,
      seed: inputs.seed,
      includeVideoIds: inputs.videoSelection.includeIds,
      excludeVideoIds: inputs.videoSelection.excludeIds,
      rawFilter: inputs.combinedRawSegmentFilter,
    };
    if (isRawView) {
      return { view: "raw", request: buildSegmentFilteredQuery({ ...listInputs, rawSegmentIds }), returnRoute };
    }
    if (!inputs.derivedQueryEnabled || activeProfileId == null) return undefined;
    return {
      view: "spans",
      request: buildSpanSearchRequest({ ...listInputs, activeProfileId, appliedQuery: inputs.appliedQuery }),
      derivedQueryDescriptor: inputs.appliedQuery != null ? inputs.derivedQueryDescriptor : undefined,
      returnRoute,
    };
  }, [activeProfileId, filter, inputs, isRawView, objectFilter, rawSegmentIds]);

  if (!isRawView && profilesQuery.isSuccess && availableProfiles.length === 0) {
    return { unavailable: "No segment display profile is available to play these segments with." };
  }
  return { source };
}
