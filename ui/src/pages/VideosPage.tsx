import { useMemo, useState, useCallback, useEffect, useRef, lazy, Suspense } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { entityEngagement, entityImages, videos } from "../api/client";
import type { BoolCriterion, EntityEngagement, FindFilter, Group, Video, VideoCreate, VideoFilterCriteria, VideoListEntry } from "../api/types";
import { ListPage, type DisplayMode } from "../components/ListPage";
import { IsoDateInput } from "../components/IsoDateInput";
import { EntityCardGrid } from "../components/EntityCardGrid";
import { useListUrlState } from "../hooks/useListUrlState";
import { usePaginatedInfiniteQuery } from "../hooks/usePaginatedInfiniteQuery";
import { useVisualSimilarityApi } from "../hooks/useVisualSimilarityApi";
import { VideoTagger } from "../components/VideoTagger";
import { toggleOptionsFromEvent, useMultiSelect, type BoundMultiSelectToggleHandler, type MultiSelectToggleHandler, type MultiSelectToggleOptions } from "../hooks/useMultiSelect";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { CustomFieldsEditor, formatDuration, formatFileSize, getResolutionLabel, RatingBadge } from "../components/shared";
import { VIDEO_CRITERIA, type CriterionDefinition } from "../components/FilterDialog";
import { CreateModalActions, EditModal, Field, TextArea, TextInput } from "../components/EditModal";
import { Film, Eye, Loader2, Search, Play, Pause, Layers, Maximize2, Minimize2, Volume2, VolumeX, ThumbsUp, Heart, Shuffle } from "lucide-react";
import { useVideoQueue } from "../state/VideoQueueContext";
import { VideoSelectionActions } from "../components/VideoSelectionActions";
import { VideoCard } from "../components/EntityCards";
import { CardSelectionToggle, RouteCardLinkOverlay } from "../components/RouteCardLinkOverlay";
import { useAuth } from "../auth/AuthContext";
import { canReadEntity, canWriteEntity } from "../auth/visibility";
import { StringListEditor } from "../components/StringListEditor";
import { VIDEO_SORT_OPTIONS } from "../components/videoSortOptions";
import { useWallColumns } from "../hooks/useWallColumns";
import { useAppConfig } from "../state/AppConfigContext";
import { StudioSelector } from "../components/StudioSelector";
import { reshuffleRandomSort, withSeededRandomSort } from "../utils/seededRandomSort";
import { WallMediaCard, type WallMediaVideoControlsState } from "../components/WallMediaCard";
import { FeedActionPill, FeedCardFrame, FeedChipButton, FeedChipOverflowMenu, FeedIdentityBadge, FeedInlineRating, FeedMetadataPill, FeedPortraitMediaFrame, getFeedMediaStyle } from "../components/FeedCardFrame";
import { NarrativeText } from "../components/NarrativeText";
import { BookmarkButton } from "../components/BookmarkButton";
import { FileBackedCreateSource, type CreateSourceMode } from "../components/FileBackedCreateSource";
import { createFromUrlWithOptionalDownload, mergeUrlLists, NoDownloaderFoundError, type UrlDownloadMode } from "../utils/createFromUrlDownload";
import { useFileBackedCreatePreferences } from "../hooks/useFileBackedCreatePreferences";
import { VirtualizedInfiniteList } from "../components/VirtualizedInfiniteList";
import { VirtualizedEntityGrid, VirtualizedWallColumns } from "../components/VirtualizedEntityLayouts";
import { RelatedEntityListRow } from "../components/RelatedEntityListView";
import { EntityReferenceMultiSelector } from "../components/EntityReferenceSelector";
import { fetchAllMatchingIds } from "../utils/selectAllMatching";
import { resolveQueryLoadState } from "../utils/queryLoadState";
import { useVideoQueueNavigation } from "../hooks/useVideoQueueNavigation";
import { MediaAggregateMetadata } from "../components/MediaAggregateMetadata";

import { getDefaultFilter, resolveSavedDisplayMode } from "../components/SavedFilterMenu";
import { VIDEO_MULTI_SORT_KEYS } from "../components/entityMultiSortKeys";

const VideoDownloadDialog = lazy(() => import("../components/VideoDownloadDialog").then((module) => ({ default: module.VideoDownloadDialog })));
const QuickViewDialog = lazy(() => import("../components/QuickViewDialog").then((module) => ({ default: module.QuickViewDialog })));

const SEARCH_MODE_OPTIONS = [
  { value: "text", label: "Text", title: "Text search" },
  { value: "visual", label: "Visual", title: "Visual semantic search" },
];

const VISUAL_MATCH_SORT_OPTION = { value: "visual_match", label: "Visual Match" };
const INCLUDE_COMPILATIONS_FILTER_KEY = "includeCompilationGroups";
const IS_VR_FILTER_KEY = "isVrCriterion";
const VERTICAL_PORTRAIT_FILTER_KEY = "orientationCriterion";
const MOBILE_VIEWER_MEDIA_QUERY = "(max-width: 767px), (hover: none) and (pointer: coarse)";
const VIDEO_FILTER_CRITERIA: CriterionDefinition[] = [
  ...VIDEO_CRITERIA,
  { id: "includeCompilations", label: "Include Compilations", type: "bool", filterKey: INCLUDE_COMPILATIONS_FILTER_KEY },
];

function isMobileViewerViewport() {
  return typeof window !== "undefined"
    && typeof window.matchMedia === "function"
    && window.matchMedia(MOBILE_VIEWER_MEDIA_QUERY).matches;
}

function getBoolCriterionValue(value: unknown) {
  if (typeof value === "boolean") {
    return value;
  }

  const criterionValue = (value as BoolCriterion | undefined)?.value;
  return typeof criterionValue === "boolean" ? criterionValue : undefined;
}

function isIncludeCompilationGroupsEnabled(value: unknown) {
  return getBoolCriterionValue(value) === true;
}

interface Props {
  onNavigate: (r: any) => void;
}

export function VideosPage({ onNavigate }: Props) {
  const defaultState = useMemo(() => {
    const savedFilter = getDefaultFilter("videos");
    return {
      filter: savedFilter?.findFilter ?? { page: 1, perPage: 40, sort: "date", direction: "desc" },
      objectFilter: savedFilter?.objectFilter ?? {},
      displayMode: resolveSavedDisplayMode(savedFilter?.uiOptions, ["grid", "list", "wall", "tagger", "feed", "vertical"] as const, "grid") as DisplayMode,
    };
  }, []);
  const visualSimilarity = useVisualSimilarityApi();
  const visualSimilarityAvailable = visualSimilarity != null;
  const { filter, setFilter, objectFilter, setObjectFilter, displayMode, setDisplayMode, searchMode, setSearchMode } = useListUrlState({
    resetKey: "videos",
    defaultFilter: defaultState.filter,
    defaultObjectFilter: defaultState.objectFilter,
    defaultDisplayMode: defaultState.displayMode,
    allowedDisplayModes: ["grid", "list", "wall", "tagger", "feed", "vertical"] as const,
    defaultSearchMode: "text",
    allowedSearchModes: visualSimilarityAvailable ? ["text", "visual"] : ["text"],
    allowInfinitePageSize: true,
  });
  const [showCreate, setShowCreate] = useState(false);
  const [selectAllMatchingPending, setSelectAllMatchingPending] = useState(false);
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const [wallColumnCount, setWallColumnCount] = useState(5);
  const [isMobileViewer, setIsMobileViewer] = useState(isMobileViewerViewport);
  const verticalViewerRef = useRef<HTMLDivElement>(null);
  const [verticalFullscreen, setVerticalFullscreen] = useState(false);
  const [verticalFullscreenDismissed, setVerticalFullscreenDismissed] = useState(false);
  const [verticalViewerTop, setVerticalViewerTop] = useState(0);
  const [verticalViewerHeight, setVerticalViewerHeight] = useState<number | null>(null);
  const [verticalSoundEnabled, setVerticalSoundEnabled] = useState(false);
  const [activeVerticalVideoId, setActiveVerticalVideoId] = useState<number | null>(null);
  const [verticalAutoScrollEnabled, setVerticalAutoScrollEnabled] = useState(false);
  const [verticalAutoScrollSeconds, setVerticalAutoScrollSeconds] = useState(8);
  const [verticalAutoScrollAwake, setVerticalAutoScrollAwake] = useState(true);
  const [feedAudioVideoId, setFeedAudioVideoId] = useState<number | null>(null);
  const lastPagedFilterRef = useRef<Pick<FindFilter, "page" | "perPage">>({ page: defaultState.filter.page ?? 1, perPage: defaultState.filter.perPage });
  const [downloadTarget, setDownloadTarget] = useState<Video | "new" | null>(null);
  const queryClient = useQueryClient();
  const { setQueue } = useVideoQueue();
  const { hasPermission, user } = useAuth();
  const { config } = useAppConfig();
  const canWriteVideo = canWriteEntity("video", hasPermission);
  const canEngageVideo = canReadEntity("video", hasPermission) && (user?.kind === "user" || user?.kind === "system");
  const feedVideoSource = config?.ui.feedVideoSource ?? "preview";
  const feedVideoSound = config?.ui.feedVideoSound ?? false;
  const defaultFeedVideoSound = feedVideoSound && !isMobileViewer;
  const feedVideoStartPercent = config?.ui.feedVideoStartPercent ?? 0;
  const feedVideoStartMinDuration = config?.ui.feedVideoStartMinDuration ?? 0;
  const continuePlaylistDefault = config?.ui.continuePlaylistDefault ?? false;
  const infiniteOnlyDisplayMode = displayMode === "feed" || displayMode === "vertical";
  const verticalItemHeight = verticalFullscreen
    ? (typeof window !== "undefined" ? window.innerHeight : 720)
    : (verticalViewerHeight ?? (typeof window !== "undefined" ? window.innerHeight : 720));

  useEffect(() => {
    if (typeof window.matchMedia !== "function") {
      setIsMobileViewer(false);
      return;
    }

    const mediaQuery = window.matchMedia(MOBILE_VIEWER_MEDIA_QUERY);
    const syncMobileViewer = () => setIsMobileViewer(mediaQuery.matches);
    syncMobileViewer();
    if (typeof mediaQuery.addEventListener === "function") {
      mediaQuery.addEventListener("change", syncMobileViewer);
      return () => mediaQuery.removeEventListener("change", syncMobileViewer);
    }

    mediaQuery.addListener(syncMobileViewer);
    return () => mediaQuery.removeListener(syncMobileViewer);
  }, []);

  useEffect(() => {
    if (displayMode !== "vertical") {
      setVerticalFullscreen(false);
      setVerticalFullscreenDismissed(false);
      setVerticalAutoScrollEnabled(false);
      setActiveVerticalVideoId(null);
      return;
    }

    const mediaQuery = window.matchMedia("(max-width: 767px)");
    const syncMobileFullscreen = () => {
      if (mediaQuery.matches && !verticalFullscreenDismissed) {
        setVerticalFullscreen(true);
      }
    };

    syncMobileFullscreen();
    mediaQuery.addEventListener("change", syncMobileFullscreen);
    return () => mediaQuery.removeEventListener("change", syncMobileFullscreen);
  }, [displayMode, verticalFullscreenDismissed]);

  useEffect(() => {
    if (displayMode === "vertical") {
      setVerticalSoundEnabled(defaultFeedVideoSound);
    }
  }, [defaultFeedVideoSound, displayMode]);

  useEffect(() => {
    if (displayMode !== "vertical" || verticalFullscreen) {
      setVerticalViewerTop(0);
      setVerticalViewerHeight(null);
      return;
    }

    const updateVerticalBounds = () => {
      const element = verticalViewerRef.current;
      if (!element) return;
      const top = Math.max(0, element.getBoundingClientRect().top);
      const height = Math.max(120, window.innerHeight - top);
      setVerticalViewerTop((current) => Math.abs(current - top) > 0.5 ? top : current);
      setVerticalViewerHeight((current) => current == null || Math.abs(current - height) > 0.5 ? height : current);
    };

    updateVerticalBounds();
    const frameId = window.requestAnimationFrame(updateVerticalBounds);
    window.addEventListener("resize", updateVerticalBounds);
    const resizeObserver = typeof ResizeObserver !== "undefined" ? new ResizeObserver(updateVerticalBounds) : null;
    if (resizeObserver) {
      resizeObserver.observe(document.body);
      if (verticalViewerRef.current?.parentElement) {
        resizeObserver.observe(verticalViewerRef.current.parentElement);
      }
    }

    return () => {
      window.cancelAnimationFrame(frameId);
      window.removeEventListener("resize", updateVerticalBounds);
      resizeObserver?.disconnect();
    };
  }, [displayMode, verticalFullscreen]);

  useEffect(() => {
    if (!verticalFullscreen) {
      return;
    }

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [verticalFullscreen]);

  useEffect(() => {
    if (displayMode !== "feed") {
      setFeedAudioVideoId(null);
    }
  }, [displayMode]);

  const wakeVerticalAutoScroll = useCallback(() => setVerticalAutoScrollAwake(true), []);

  useEffect(() => {
    if (displayMode !== "vertical" || !verticalAutoScrollAwake) {
      return;
    }

    const timeoutId = window.setTimeout(() => setVerticalAutoScrollAwake(false), verticalAutoScrollEnabled ? 2600 : 3600);
    return () => window.clearTimeout(timeoutId);
  }, [displayMode, verticalAutoScrollAwake, verticalAutoScrollEnabled, verticalAutoScrollSeconds]);

  const normalizedObjectFilter = useMemo(() => {
    const includeValue = objectFilter[INCLUDE_COMPILATIONS_FILTER_KEY];
    if (typeof includeValue !== "boolean") {
      return objectFilter;
    }

    return { ...objectFilter, [INCLUDE_COMPILATIONS_FILTER_KEY]: { value: includeValue } satisfies BoolCriterion };
  }, [objectFilter]);

  const backendObjectFilter = useMemo(() => Object.fromEntries(
    Object.entries(normalizedObjectFilter).filter(([key]) => key !== INCLUDE_COMPILATIONS_FILTER_KEY),
  ), [normalizedObjectFilter]);
  const hasObjectFilter = Object.keys(backendObjectFilter).length > 0;
  const compilationBlockingObjectFilter = useMemo(() => Object.fromEntries(
    Object.entries(backendObjectFilter).filter(([key, value]) => key !== IS_VR_FILTER_KEY || getBoolCriterionValue(value) !== false),
  ), [backendObjectFilter]);
  const hasCompilationBlockingObjectFilter = Object.keys(compilationBlockingObjectFilter).length > 0;
  const videoVrFilterValue = getBoolCriterionValue(backendObjectFilter[IS_VR_FILTER_KEY]);
  const compilationQueryExtra = useMemo(() => videoVrFilterValue === false ? { isVr: false } : undefined, [videoVrFilterValue]);
  const visualSearchActive = visualSimilarityAvailable && searchMode === "visual" && Boolean(filter.q?.trim());
  const infinitePageSize = filter.perPage === 0 || infiniteOnlyDisplayMode;
  const defaultInfiniteChunkSize = defaultState.filter.perPage && defaultState.filter.perPage > 0 ? defaultState.filter.perPage : 40;
  const infiniteChunkSize = displayMode === "vertical" ? 6 : displayMode === "feed" ? 10 : defaultInfiniteChunkSize;
  const infiniteFilterKey = useMemo(
    () => ({ ...filter, page: 1, perPage: infiniteChunkSize }),
    [filter, infiniteChunkSize],
  );

  useEffect(() => {
    if (!infiniteOnlyDisplayMode && filter.perPage !== 0) {
      lastPagedFilterRef.current = { page: filter.page ?? 1, perPage: filter.perPage };
    }
  }, [filter.page, filter.perPage, infiniteOnlyDisplayMode]);

  useEffect(() => {
    if (infiniteOnlyDisplayMode && filter.perPage !== 0) {
      setFilter({ ...filter, page: 1, perPage: 0 });
    }
  }, [filter, infiniteOnlyDisplayMode, setFilter]);
  const searchModeOptions = useMemo(() => visualSimilarityAvailable ? SEARCH_MODE_OPTIONS : SEARCH_MODE_OPTIONS.filter((mode) => mode.value === "text"), [visualSimilarityAvailable]);
  const sortOptions = useMemo(
    () => visualSimilarityAvailable && searchMode === "visual" ? [VISUAL_MATCH_SORT_OPTION, ...VIDEO_SORT_OPTIONS] : VIDEO_SORT_OPTIONS,
    [visualSimilarityAvailable, searchMode],
  );

  useEffect(() => {
    if (!visualSimilarityAvailable && searchMode === "visual") {
      setSearchMode("text");
      if (filter.sort === "visual_match") {
        setFilter({ ...filter, sort: defaultState.filter.sort, direction: defaultState.filter.direction ?? "desc", page: 1 });
      }
    }
  }, [defaultState.filter.direction, defaultState.filter.sort, filter, searchMode, setFilter, setSearchMode, visualSimilarityAvailable]);

  const handleSearchModeChange = useCallback((mode: string) => {
    if (mode === "visual" && !visualSimilarityAvailable) {
      return;
    }

    setSearchMode(mode);

    if (mode === "visual") {
      setFilter({ ...filter, sort: "visual_match", direction: "desc", sorts: undefined, page: 1 });
      return;
    }

    if (filter.sort === "visual_match") {
      setFilter({
        ...filter,
        sort: defaultState.filter.sort,
        direction: defaultState.filter.direction ?? "desc",
        page: 1,
      });
      return;
    }

    setFilter({ ...filter, page: 1 });
  }, [defaultState.filter.direction, defaultState.filter.sort, filter, setFilter, setSearchMode, visualSimilarityAvailable]);

  const handleDisplayModeChange = useCallback((mode: DisplayMode) => {
    const requiresInfinite = mode === "feed" || mode === "vertical";

    if (requiresInfinite && filter.perPage !== 0) {
      lastPagedFilterRef.current = { page: filter.page ?? 1, perPage: filter.perPage };
    }

    setDisplayMode(mode);

    if (mode === "vertical" && !objectFilter[VERTICAL_PORTRAIT_FILTER_KEY] && Object.keys(objectFilter).length === 0) {
      setObjectFilter({ [VERTICAL_PORTRAIT_FILTER_KEY]: { value: "portrait" } });
    }

    if (requiresInfinite && filter.perPage !== 0) {
      setFilter({ ...filter, page: 1, perPage: 0 });
      return;
    }

    if (!requiresInfinite && filter.perPage === 0) {
      const lastPagedFilter = lastPagedFilterRef.current;
      setFilter({ ...filter, page: lastPagedFilter.page ?? 1, perPage: lastPagedFilter.perPage ?? defaultState.filter.perPage });
    }
  }, [defaultState.filter.perPage, filter, objectFilter, setDisplayMode, setFilter, setObjectFilter]);

  const includeCompilationGroups = isIncludeCompilationGroupsEnabled(normalizedObjectFilter[INCLUDE_COMPILATIONS_FILTER_KEY]);
  const canShowCompilationGroups = !infinitePageSize && includeCompilationGroups && searchMode === "text" && !hasCompilationBlockingObjectFilter && (displayMode === "grid" || displayMode === "list");

  const aggregateFilter = useMemo(() => ({ q: filter.q, page: 1, perPage: 0 }), [filter.q]);
  const { data: filteredAggregate, isLoading: filteredAggregateLoading } = useQuery({
    queryKey: ["videos", "aggregate", aggregateFilter, backendObjectFilter],
    queryFn: () => videos.aggregate({
      findFilter: aggregateFilter,
      objectFilter: hasObjectFilter ? backendObjectFilter as VideoFilterCriteria : undefined,
    }),
    enabled: !visualSearchActive && !canShowCompilationGroups,
  });

  useEffect(() => {
    if (!visualSimilarityAvailable || searchMode !== "visual" || !filter.sorts || filter.sorts.length <= 1) {
      return;
    }

    setFilter({ ...filter, sort: "visual_match", direction: "desc", sorts: undefined, page: 1 });
  }, [filter, searchMode, setFilter, visualSimilarityAvailable]);

  useEffect(() => {
    if (!includeCompilationGroups || !filter.sorts || filter.sorts.length <= 1) {
      return;
    }

    setFilter({ ...filter, sorts: undefined, page: 1 });
  }, [filter, includeCompilationGroups, setFilter]);

  const { data, isLoading, error: pageError, refetch: refetchPage } = useQuery({
    queryKey: ["videos", filter, backendObjectFilter, searchMode],
    queryFn: () => {
      if (visualSearchActive) {
        return visualSimilarity.searchVideos({
          findFilter: filter,
          objectFilter: hasObjectFilter ? backendObjectFilter as VideoFilterCriteria : undefined,
        });
      }

      return hasObjectFilter
        ? videos.findFiltered({ findFilter: filter, objectFilter: backendObjectFilter as VideoFilterCriteria })
        : videos.find(filter);
    },
    enabled: !infinitePageSize && !canShowCompilationGroups,
  });

  const { data: unifiedData, isLoading: unifiedLoading, error: unifiedError, refetch: refetchUnified } = useQuery({
    queryKey: ["videos", "with-compilations", filter, compilationQueryExtra],
    queryFn: () => videos.findWithCompilations(
      filter.sorts && filter.sorts.length > 1 ? { ...filter, sorts: undefined } : filter,
      compilationQueryExtra,
    ),
    enabled: !infinitePageSize && canShowCompilationGroups,
  });

  const infiniteVideosQuery = usePaginatedInfiniteQuery<Video>({
    queryKey: ["videos", "infinite", infiniteFilterKey, backendObjectFilter, searchMode],
    enabled: infinitePageSize,
    chunkSize: infiniteChunkSize,
    queryFn: (page, perPage) => {
      const nextFilter = { ...filter, page, perPage };
      if (visualSearchActive) {
        return visualSimilarity.searchVideos({
          findFilter: nextFilter,
          objectFilter: hasObjectFilter ? backendObjectFilter as VideoFilterCriteria : undefined,
        });
      }

      return hasObjectFilter
        ? videos.findFiltered({ findFilter: nextFilter, objectFilter: backendObjectFilter as VideoFilterCriteria })
        : videos.find(nextFilter);
    },
  });

  const defaultListEntries: VideoListEntry[] = canShowCompilationGroups
    ? (unifiedData?.items ?? [])
    : (data?.items ?? []).map((video) => ({ kind: "video" as const, id: video.id, video }));
  const defaultItems = defaultListEntries.flatMap((entry) => entry.kind === "video" && entry.video ? [entry.video] : []);
  const items = infinitePageSize ? infiniteVideosQuery.items : defaultItems;
  const listEntries = infinitePageSize
    ? items.map((video) => ({ kind: "video" as const, id: video.id, video }))
    : defaultListEntries;
  const totalCount = infinitePageSize
    ? infiniteVideosQuery.totalCount
    : (canShowCompilationGroups ? unifiedData?.totalCount : data?.totalCount);
  const loading = infinitePageSize
    ? infiniteVideosQuery.isPending
    : (canShowCompilationGroups ? unifiedLoading : isLoading);
  const retryLoad = infinitePageSize
    ? infiniteVideosQuery.refetch
    : (canShowCompilationGroups ? refetchUnified : refetchPage);
  const activeQueryData = infinitePageSize
    ? infiniteVideosQuery.data
    : (canShowCompilationGroups ? unifiedData : data);
  const activeQueryError = infinitePageSize
    ? infiniteVideosQuery.error
    : (canShowCompilationGroups ? unifiedError : pageError);
  const videoLoadState = resolveQueryLoadState({
    data: activeQueryData === undefined ? undefined : { listEntries, items, totalCount: totalCount ?? 0 },
    isPending: loading,
    error: activeQueryError,
    isEmpty: (collection) => collection.listEntries.length === 0,
    retry: () => { void retryLoad(); },
  });
  const loadMoreVideos = useCallback(() => {
    if (infiniteVideosQuery.hasNextPage && !infiniteVideosQuery.isFetchingNextPage) {
      void infiniteVideosQuery.fetchNextPage();
    }
  }, [infiniteVideosQuery.fetchNextPage, infiniteVideosQuery.hasNextPage, infiniteVideosQuery.isFetchingNextPage]);

  useEffect(() => {
    if (displayMode !== "feed") {
      setFeedAudioVideoId(null);
      return;
    }
    if (!defaultFeedVideoSound) setFeedAudioVideoId(null);
  }, [defaultFeedVideoSound, displayMode]);

  useEffect(() => {
    if (displayMode !== "vertical") {
      setActiveVerticalVideoId(null);
    }
  }, [displayMode]);

  useEffect(() => {
    if (displayMode !== "vertical" || !verticalAutoScrollEnabled || activeVerticalVideoId == null) {
      return;
    }

    const timeoutId = window.setTimeout(() => {
      const root = verticalViewerRef.current;
      if (!root) return;
      const currentIndex = items.findIndex((video) => video.id === activeVerticalVideoId);
      const nextIndex = currentIndex >= 0 ? currentIndex + 1 : 0;
      if (nextIndex >= items.length) {
        setVerticalAutoScrollEnabled(false);
        return;
      }
      root.scrollTo({ top: nextIndex * verticalItemHeight, behavior: "smooth" });
    }, verticalAutoScrollSeconds * 1000);

    return () => window.clearTimeout(timeoutId);
  }, [activeVerticalVideoId, displayMode, items, verticalAutoScrollEnabled, verticalAutoScrollSeconds, verticalItemHeight]);
  const { engagementById } = useEntityEngagementBatch("video", items.map((item) => item.id));
  const wallColumns = useWallColumns(items, wallColumnCount, (video) => {
    const file = video.files[0];
    return file?.width && file.height ? file.height / file.width : 9 / 16;
  });
  const selectionResetKey = useMemo(() => JSON.stringify({ filter: infiniteFilterKey, objectFilter: backendObjectFilter, searchMode }), [backendObjectFilter, infiniteFilterKey, searchMode]);
  const { selectedIds, toggle, selectAll, selectIds, selectNone, invertSelection } = useMultiSelect(items, { preserveOnItemsChange: infinitePageSize, resetKey: selectionResetKey });
  const selecting = selectedIds.size > 0;
  const selectedIdList = useMemo(() => [...selectedIds].map(Number).sort((left, right) => left - right), [selectedIds]);
  const { data: selectedAggregate, isLoading: selectedAggregateLoading } = useQuery({
    queryKey: ["videos", "aggregate", "selection", selectedIdList],
    queryFn: () => videos.aggregate({ objectFilter: { ids: selectedIdList } }),
    enabled: selectedIdList.length > 0,
  });

  const queryVideoQueuePage = useCallback((nextFilter: FindFilter) => {
    if (visualSearchActive) {
      return visualSimilarity.searchVideos({
        findFilter: nextFilter,
        objectFilter: hasObjectFilter ? backendObjectFilter as VideoFilterCriteria : undefined,
      });
    }
    return hasObjectFilter
      ? videos.findFiltered({ findFilter: nextFilter, objectFilter: backendObjectFilter as VideoFilterCriteria })
      : videos.find(nextFilter);
  }, [backendObjectFilter, hasObjectFilter, visualSearchActive, visualSimilarity]);
  const { openVideo: navigateToVideo, navigateFromList: navigateFromVideoList } = useVideoQueueNavigation({
    items,
    filter,
    totalCount: totalCount ?? items.length,
    infinitePageSize,
    queryPage: queryVideoQueuePage,
    onNavigate,
  });

  const playRandomMutation = useMutation({
    mutationFn: async () => {
      const randomFilter = reshuffleRandomSort({ ...filter, page: 1, perPage: 1, sort: "random", direction: "asc" });
      const result = visualSearchActive && visualSimilarity
        ? await visualSimilarity.searchVideos({
          findFilter: randomFilter,
          objectFilter: hasObjectFilter ? backendObjectFilter as VideoFilterCriteria : undefined,
        })
        : hasObjectFilter
          ? await videos.findFiltered({ findFilter: randomFilter, objectFilter: backendObjectFilter as VideoFilterCriteria })
          : await videos.find(randomFilter);
      return result.items[0] ?? null;
    },
    onSuccess: (video) => {
      if (!video) {
        return;
      }

      setQueue([video.id], video.id, [{
        id: video.id,
        title: video.title || video.files[0]?.basename || `Video ${video.id}`,
        subtitle: video.studioName || video.date || undefined,
        imagePath: videos.screenshotUrl(video.id, video.updatedAt),
      }], { autoplay: continuePlaylistDefault });
      onNavigate({ page: "video", id: video.id });
    },
  });

  const handleSelectAllMatching = useCallback(async () => {
    setSelectAllMatchingPending(true);
    try {
      const ids = await fetchAllMatchingIds<Video>(filter, (nextFilter) => {
        if (visualSearchActive && visualSimilarity) {
          return visualSimilarity.searchVideos({
            findFilter: nextFilter,
            objectFilter: hasObjectFilter ? backendObjectFilter as VideoFilterCriteria : undefined,
          });
        }

        return hasObjectFilter
          ? videos.findFiltered({ findFilter: nextFilter, objectFilter: backendObjectFilter as VideoFilterCriteria })
          : videos.find(nextFilter);
      });
      selectIds(ids);
    } finally {
      setSelectAllMatchingPending(false);
    }
  }, [backendObjectFilter, filter, hasObjectFilter, selectIds, visualSearchActive, visualSimilarity]);

  // When sort changes to random, generate a new seed for reproducibility
  const handleFilterChange = useCallback((next: typeof filter) => {
    setFilter(withSeededRandomSort(filter, next));
  }, [filter, setFilter]);

  const verticalOverlayTop = verticalFullscreen ? 12 : Math.max(12, verticalViewerTop + 12);
  const verticalAutoScrollTop = verticalFullscreen
    ? (isMobileViewer ? "64%" : "50%")
    : verticalOverlayTop + (isMobileViewer ? 96 : 44);
  const verticalViewerStyle = verticalFullscreen ? undefined : { height: verticalViewerHeight != null ? `${verticalViewerHeight}px` : "calc(100dvh - 10rem)" };
  const verticalActiveIndex = useMemo(() => items.findIndex((video) => video.id === activeVerticalVideoId), [items, activeVerticalVideoId]);

  return (
    <>
    <VideoCreateModal open={showCreate} onClose={() => setShowCreate(false)} onCreated={(id) => onNavigate({ page: "video", id })} />
    <Suspense fallback={null}>
      {downloadTarget !== null ? (
        <VideoDownloadDialog
          open={downloadTarget !== null}
          video={downloadTarget !== "new" ? downloadTarget : undefined}
          onClose={() => setDownloadTarget(null)}
          onNavigate={onNavigate}
        />
      ) : null}
    </Suspense>
    <ListPage
      title="Videos"
      metadataByline={!visualSearchActive && !canShowCompilationGroups ? (
        <MediaAggregateMetadata duration={filteredAggregate?.duration} fileSize={filteredAggregate?.fileSize} loading={false} />
      ) : undefined}
      summaryLoading={!visualSearchActive && !canShowCompilationGroups && (loading || filteredAggregateLoading)}
      pageKey="videos"
      filterMode="videos"
      filter={filter}
      onFilterChange={handleFilterChange}
      totalCount={totalCount ?? 0}
      loadState={videoLoadState}
      searchMode={searchMode}
      searchModes={searchModeOptions}
      searchPlaceholder={visualSimilarityAvailable && searchMode === "visual" ? "Search visuals..." : "Search videos, tags, performers..."}
      onSearchModeChange={handleSearchModeChange}
      sortOptions={sortOptions}
      multiSortKeys={searchMode === "text" && !includeCompilationGroups ? VIDEO_MULTI_SORT_KEYS : undefined}
      displayMode={displayMode}
      onDisplayModeChange={handleDisplayModeChange}
      availableDisplayModes={["grid", "list", "wall", "tagger", "feed", "vertical"]}
      allowInfinitePageSize
      infinitePageSizeOnly={infiniteOnlyDisplayMode}
      criteriaDefinitions={VIDEO_FILTER_CRITERIA}
      objectFilter={normalizedObjectFilter}
      onObjectFilterChange={setObjectFilter}
      wallColumnCount={wallColumnCount}
      onWallColumnCountChange={setWallColumnCount}
      infiniteScroll={infinitePageSize ? {
        hasNextPage: Boolean(infiniteVideosQuery.hasNextPage),
        isFetchingNextPage: infiniteVideosQuery.isFetchingNextPage,
        onLoadMore: loadMoreVideos,
        loadedCount: infiniteVideosQuery.loadedThroughCount,
        totalCount: infiniteVideosQuery.totalCount,
      } : undefined}
      autoScrollContainerRef={displayMode === "vertical" ? verticalViewerRef : undefined}
      showAutoScrollControls={displayMode !== "vertical"}
      showPagingControls={!infinitePageSize}
      onSelectAll={infinitePageSize ? handleSelectAllMatching : selectAll}
      selectAllPending={infinitePageSize ? selectAllMatchingPending : false}
      onSelectAllMatching={infinitePageSize ? selectAll : undefined}
      selectAllMatchingLabel="Select shown"
      renderOperations={() => (
        <button
          type="button"
          onClick={() => playRandomMutation.mutate()}
          disabled={playRandomMutation.isPending || loading || (totalCount ?? 0) === 0}
          className="inline-flex min-h-10 items-center justify-center gap-1 rounded-lg border border-border bg-card/70 px-2.5 py-2 text-sm text-secondary transition-colors hover:border-accent/50 hover:text-accent disabled:cursor-not-allowed disabled:opacity-60 sm:min-h-0 sm:py-1 sm:text-xs"
          title="Play random"
          aria-label="Play random"
        >
          {playRandomMutation.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Shuffle className="h-3.5 w-3.5" />}
        </button>
      )}
      onNew={canWriteVideo ? () => setShowCreate(true) : undefined}
      selectedIds={selectedIds}
      selectionMetadata={<MediaAggregateMetadata duration={selectedAggregate?.duration} fileSize={selectedAggregate?.fileSize} loading={selectedAggregateLoading} />}
      onSelectNone={selectNone}
      onInvertSelection={invertSelection}
      selectionActions={
        <VideoSelectionActions
          items={items}
          selectedIds={selectedIds as Set<number>}
          onSelectNone={selectNone}
          onNavigate={onNavigate}
          storageKey="page-videos"
        />
      }
    >
      {displayMode === "vertical" && (
        <>
          <button
            type="button"
            onClick={() => {
              if (verticalFullscreen) {
                setVerticalFullscreen(false);
                setVerticalFullscreenDismissed(true);
              } else {
                setVerticalFullscreen(true);
                setVerticalFullscreenDismissed(false);
              }
            }}
            className={`fixed ${verticalFullscreen ? "left-3" : "right-3"} z-[95] rounded-full border border-white/15 bg-black/55 p-2 text-white shadow-lg backdrop-blur transition-colors hover:bg-black/75`}
            style={{ top: verticalOverlayTop }}
            aria-label={verticalFullscreen ? "Exit full screen" : "Enter full screen"}
            title={verticalFullscreen ? "Exit full screen" : "Enter full screen"}
          >
            {verticalFullscreen ? <Minimize2 className="h-4 w-4" /> : <Maximize2 className="h-4 w-4" />}
          </button>
          {infinitePageSize && (
            <div className="pointer-events-none fixed right-3 z-[94] sm:right-5" style={{ top: verticalAutoScrollTop, transform: verticalFullscreen ? "translateY(-50%)" : undefined }}>
              <div
                className="pointer-events-auto relative flex min-h-36 w-12 items-center justify-end"
                onPointerEnter={wakeVerticalAutoScroll}
                onPointerMove={wakeVerticalAutoScroll}
                onFocusCapture={wakeVerticalAutoScroll}
              >
                {!verticalAutoScrollAwake && <div className="absolute right-0 h-12 w-1.5 rounded-l-full bg-white/70 shadow-lg" aria-hidden="true" />}
                <div className={`flex flex-col items-center gap-2 rounded-xl border border-white/15 bg-black/60 px-2 py-2 text-white shadow-2xl backdrop-blur transition-all duration-300 ${verticalAutoScrollAwake ? "translate-x-0 opacity-100" : "pointer-events-none translate-x-2 opacity-0"}`}>
                  <button
                    type="button"
                    onClick={() => {
                      wakeVerticalAutoScroll();
                      setVerticalAutoScrollEnabled((current) => !current);
                    }}
                    className={`rounded-md border border-transparent p-1.5 transition-colors hover:bg-white/15 focus:outline-none focus:border-white/50 ${verticalAutoScrollEnabled ? "text-accent" : "text-white"}`}
                    aria-label={verticalAutoScrollEnabled ? "Pause vertical auto-scroll" : "Start vertical auto-scroll"}
                    title={verticalAutoScrollEnabled ? "Pause vertical auto-scroll" : "Start vertical auto-scroll"}
                  >
                    {verticalAutoScrollEnabled ? <Pause className="h-4 w-4" /> : <Play className="h-4 w-4" />}
                  </button>
                  <input
                    type="range"
                    min={3}
                    max={30}
                    step={1}
                    value={verticalAutoScrollSeconds}
                    onChange={(event) => {
                      wakeVerticalAutoScroll();
                      setVerticalAutoScrollSeconds(Number(event.target.value));
                    }}
                    className="h-24 w-1 accent-accent [writing-mode:vertical-lr]"
                    aria-label="Seconds before next vertical item"
                    title={`${verticalAutoScrollSeconds}s before next item`}
                  />
                  <span className="text-[10px] text-white/80 tabular-nums [writing-mode:vertical-lr]">{verticalAutoScrollSeconds}s/item</span>
                </div>
              </div>
            </div>
          )}
          <div
            ref={verticalViewerRef}
            style={verticalViewerStyle}
            className={verticalFullscreen
              ? "fixed inset-0 z-[80] h-[100dvh] snap-y snap-mandatory overflow-y-auto bg-black px-0 py-0"
              : "relative -mx-3 -mb-5 snap-y snap-mandatory overflow-y-auto bg-black px-0 py-0 sm:-mx-4 md:-mx-6"}
          >
            <VirtualizedInfiniteList
              items={items}
              getItemKey={(video) => video.id}
              estimateSize={verticalItemHeight}
              overscan={2}
              hasNextPage={Boolean(infiniteVideosQuery.hasNextPage)}
              isFetchingNextPage={infiniteVideosQuery.isFetchingNextPage}
              loadMore={loadMoreVideos}
              scrollElementRef={verticalViewerRef}
              onActiveIndexChange={(idx) => setActiveVerticalVideoId(idx == null ? null : items[idx]?.id ?? null)}
              itemClassName="snap-start"
              renderItem={({ item: video, index }) => (
                <VideoVerticalViewerCard
                  video={video}
                  useVideo={verticalActiveIndex < 0 ? index === 0 : Math.abs(index - verticalActiveIndex) <= 1}
                  feedVideoSource={feedVideoSource}
                  soundEnabled={verticalSoundEnabled && video.id === activeVerticalVideoId}
                  onToggleSound={() => setVerticalSoundEnabled((current) => !current)}
                  feedVideoStartPercent={feedVideoStartPercent}
                  feedVideoStartMinDuration={feedVideoStartMinDuration}
                  fullscreen={verticalFullscreen}
                  viewerHeight={verticalViewerHeight}
                  selected={selectedIds.has(video.id)}
                  selecting={selecting}
                  onSelect={(toggleOptions) => toggle(video.id, toggleOptions)}
                  onNavigate={navigateToVideo}
                />
              )}
            />
          </div>
        </>
      )}
      {displayMode === "feed" && (
        <div className="mx-auto w-full max-w-[64rem] px-3 sm:px-4">
          <VirtualizedInfiniteList
            items={items}
            getItemKey={(video) => video.id}
            estimateSize={760}
            overscan={2}
            adjustScrollOnItemSizeChange={!isMobileViewer}
            hasNextPage={Boolean(infiniteVideosQuery.hasNextPage)}
            isFetchingNextPage={infiniteVideosQuery.isFetchingNextPage}
            loadMore={loadMoreVideos}
            className={isMobileViewer ? "[overflow-anchor:none]" : undefined}
            itemClassName="pb-5 [touch-action:pan-y]"
            renderItem={({ item: video }) => (
              <VideoFeedCard
                video={video}
                useVideo={true}
                engagement={engagementById.get(video.id)}
                feedVideoSource={feedVideoSource}
                feedVideoStartPercent={feedVideoStartPercent}
                feedVideoStartMinDuration={feedVideoStartMinDuration}
                soundEnabled={feedAudioVideoId === video.id}
                onToggleSound={() => setFeedAudioVideoId((current) => current === video.id ? null : video.id)}
                onPlaybackEligibilityChange={defaultFeedVideoSound ? (eligible) => setFeedAudioVideoId((current) => eligible ? video.id : current === video.id ? null : current) : undefined}
                onNavigate={navigateFromVideoList}
                canEngage={canEngageVideo}
                selected={selectedIds.has(video.id)}
                selecting={selecting}
                onSelect={(toggleOptions) => toggle(video.id, toggleOptions)}
              />
            )}
          />
        </div>
      )}
      {displayMode === "grid" && (
        infinitePageSize ? (
          <VirtualizedEntityGrid
            items={items}
            getItemKey={(s) => s.id}
            minCardWidth="var(--card-min-width, 200px)"
            gap={12}
            estimateRowHeight={320}
            overscan={3}
            infinitePageSize={infinitePageSize}
            hasNextPage={infiniteVideosQuery.hasNextPage}
            isFetchingNextPage={infiniteVideosQuery.isFetchingNextPage}
            loadMore={loadMoreVideos}
            renderItem={(video) => (
              <VideoCard
                video={video}
                engagement={engagementById.get(video.id)}
                onClick={(toggleOptions) => selecting ? toggle(video.id, toggleOptions) : navigateToVideo(video.id)}
                onNavigate={onNavigate}
                selected={selectedIds.has(video.id)}
                onSelect={(toggleOptions) => toggle(video.id, toggleOptions)}
                selecting={selecting}
                onQuickView={() => setQuickViewId(video.id)}
              />
            )}
          />
        ) : (
          <EntityCardGrid minCardWidth="var(--card-min-width, 200px)">
            {listEntries.map((entry) => entry.kind === "compilation" && entry.group ? (
              <CompilationGroupCard key={`compilation-${entry.group.id}`} group={entry.group} onNavigate={onNavigate} />
            ) : entry.video ? (
              <VideoCard
                key={`video-${entry.video.id}`}
                video={entry.video}
                engagement={engagementById.get(entry.video.id)}
                onClick={(toggleOptions) => selecting ? toggle(entry.video!.id, toggleOptions) : navigateToVideo(entry.video!.id)}
                onNavigate={onNavigate}
                selected={selectedIds.has(entry.video.id)}
                onSelect={(toggleOptions) => toggle(entry.video!.id, toggleOptions)}
                selecting={selecting}
                onQuickView={() => setQuickViewId(entry.video!.id)}
              />
            ) : null)}
          </EntityCardGrid>
        )
      )}
      {displayMode === "list" && (
        <VideoListTable entries={listEntries} engagementById={engagementById} onNavigate={navigateFromVideoList} selectedIds={selectedIds} onToggle={toggle} selecting={selecting} />
      )}
      {displayMode === "wall" && (
        <VirtualizedWallColumns
          columns={wallColumns}
          getItemKey={(video) => video.id}
          infinitePageSize={infinitePageSize}
          hasNextPage={infiniteVideosQuery.hasNextPage}
          isFetchingNextPage={infiniteVideosQuery.isFetchingNextPage}
          loadMore={loadMoreVideos}
          estimateItemHeight={260}
          gap={4}
          className="flex gap-1 px-2"
          columnClassName="flex-1 flex flex-col gap-1 min-w-0"
          renderItem={(video) => (
                <VideoWallCard
                  video={video}
                  onClick={(toggleOptions) => selecting ? toggle(video.id, toggleOptions) : navigateToVideo(video.id)}
                  selected={selectedIds.has(video.id)}
                  selecting={selecting}
                  onSelect={(toggleOptions) => toggle(video.id, toggleOptions)}
                />
          )}
        />
      )}
      {displayMode === "tagger" && (
        <VideoTagger videos={items} onNavigate={navigateToVideo} selectedIds={selectedIds} selecting={selecting} onSelect={toggle} />
      )}
      {listEntries.length === 0 && (
        <div className="text-center py-20">
          <Film className="w-16 h-16 mx-auto mb-4 text-muted opacity-50" />
          <p className="text-secondary text-lg">No videos found</p>
          <p className="text-muted text-sm mt-1">Try scanning your library to discover content</p>
        </div>
      )}
    </ListPage>

    <Suspense fallback={null}>
      {quickViewId !== null ? (
        <QuickViewDialog type="video" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      ) : null}
    </Suspense>
    </>
  );
}

function VideoCreateModal({ open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (id: number) => void }) {
  const qc = useQueryClient();
  const [title, setTitle] = useState("");
  const [code, setCode] = useState("");
  const [date, setDate] = useState("");
  const [details, setDetails] = useState("");
  const [director, setDirector] = useState("");
  const [isVr, setIsVr] = useState(false);
  const [urls, setUrls] = useState<string[]>([""]);
  const [studioId, setStudioId] = useState<number | undefined>(undefined);
  const [customFields, setCustomFields] = useState<Record<string, unknown>>({});
  const [customFieldsValid, setCustomFieldsValid] = useState(true);
  const [createAnother, setCreateAnother] = useState(false);
  const [sourceMode, setSourceMode] = useState<CreateSourceMode>("metadata");
  const [filePath, setFilePath] = useState("");
  const [url, setUrl] = useState("");
  const { urlDownloadMode, setUrlDownloadMode, scrapeMetadata, setScrapeMetadata } = useFileBackedCreatePreferences("Video");
  const [noDownloaderFound, setNoDownloaderFound] = useState(false);
  const [selectedTagIds, setSelectedTagIds] = useState<number[]>([]);
  const [selectedPerformerIds, setSelectedPerformerIds] = useState<number[]>([]);
  const [selectedGalleryIds, setSelectedGalleryIds] = useState<number[]>([]);

  const resetForm = () => {
    setTitle("");
    setCode("");
    setDate("");
    setDetails("");
    setDirector("");
    setIsVr(false);
    setUrls([""]);
    setStudioId(undefined);
    setCustomFields({});
    setSourceMode("metadata");
    setFilePath("");
    setUrl("");
    setNoDownloaderFound(false);
    setSelectedTagIds([]);
    setSelectedPerformerIds([]);
    setSelectedGalleryIds([]);
  };

  const createMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (data: VideoCreate) => videos.create(data),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["videos"] });
      resetForm();
      if (createAnother) return;
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });

  const createFromFileMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({ path, data }: { path: string; data: VideoCreate }) => {
      const created = await videos.createFromFile({ filePath: path });
      return created?.id ? videos.update(created.id, data) : created;
    },
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["videos"] });
      resetForm();
      if (createAnother) return;
      onClose();
      if (created?.id) onCreated(created.id);
    },
  });

  const createFromUrlMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: ({ requestedUrl, data, downloadMode, scrapeMetadata }: { requestedUrl: string; data: VideoCreate; downloadMode: UrlDownloadMode; scrapeMetadata: boolean }) =>
      createFromUrlWithOptionalDownload({ requestedUrl, data, entity: "Video", downloadMode, scrapeMetadata, create: videos.create }),
    onSuccess: (created) => {
      qc.invalidateQueries({ queryKey: ["videos"] });
      qc.invalidateQueries({ queryKey: ["jobs"] });
      resetForm();
      if (createAnother) return;
      onClose();
      if (created?.id) onCreated(created.id);
    },
    onError: (err) => {
      if (err instanceof NoDownloaderFoundError) setNoDownloaderFound(true);
    },
  });

  const buildPayload = (extraUrls: string[] = []): VideoCreate => ({
    title: title || undefined,
    code: code || undefined,
    date: date || undefined,
    details: details || undefined,
    director: director || undefined,
    isVr,
    studioId,
    urls: mergeUrlLists(urls, extraUrls),
    tagIds: selectedTagIds,
    performerIds: selectedPerformerIds,
    galleryIds: selectedGalleryIds,
    customFields: Object.keys(customFields).length > 0 ? customFields : undefined,
  });

  const handleSourceModeChange = (mode: CreateSourceMode) => {
    setSourceMode(mode);
    setNoDownloaderFound(false);
  };

  const handleUrlChange = (value: string) => {
    setUrl(value);
    setNoDownloaderFound(false);
  };

  const handleCreateWithoutDownload = () => {
    const requestedUrl = url.trim();
    if (requestedUrl) createMut.mutate(buildPayload([requestedUrl]));
  };

  const handleSave = () => {
    if (sourceMode === "file") {
      const trimmedPath = filePath.trim();
      if (trimmedPath) createFromFileMut.mutate({ path: trimmedPath, data: buildPayload() });
      return;
    }

    if (sourceMode === "url") {
      const requestedUrl = url.trim();
      if (requestedUrl) createFromUrlMut.mutate({ requestedUrl, data: buildPayload(), downloadMode: urlDownloadMode, scrapeMetadata });
      return;
    }

    createMut.mutate(buildPayload());
  };

  const pending = createMut.isPending || createFromFileMut.isPending || createFromUrlMut.isPending;
  const error = (createMut.error ?? createFromFileMut.error ?? createFromUrlMut.error) as Error | null;

  return (
    <EditModal title="Create Video" open={open} onClose={onClose}>
      <FileBackedCreateSource
        mode={sourceMode}
        onModeChange={handleSourceModeChange}
        filePath={filePath}
        onFilePathChange={setFilePath}
        url={url}
        onUrlChange={handleUrlChange}
        urlDownloadMode={urlDownloadMode}
        onUrlDownloadModeChange={setUrlDownloadMode}
        scrapeMetadata={scrapeMetadata}
        onScrapeMetadataChange={setScrapeMetadata}
        noDownloaderFound={noDownloaderFound}
        onCreateWithoutDownload={handleCreateWithoutDownload}
        onDismissNoDownloader={() => setNoDownloaderFound(false)}
        modes={["metadata", "file", "url"]}
        filePlaceholder="C:\\Media\\video.mp4"
        urlPlaceholder="https://example.com/video"
      />

      <>
      <div className="grid grid-cols-2 gap-4">
        <Field label="Title">
          <TextInput value={title} onChange={setTitle} placeholder="Video title" />
        </Field>
        <Field label="Date">
          <IsoDateInput
            value={date}
            onChange={(e) => setDate(e.target.value)}
            className="w-full bg-card border border-border rounded px-3 py-2 text-sm text-foreground focus:outline-none focus:border-accent"
          />
        </Field>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <Field label="Studio Code">
          <TextInput value={code} onChange={setCode} placeholder="Studio code" />
        </Field>
        <Field label="Director">
          <TextInput value={director} onChange={setDirector} placeholder="Director" />
        </Field>
      </div>

      <Field label="Details">
        <TextArea value={details} onChange={setDetails} placeholder="Video description" rows={3} />
      </Field>

      <Field label="Studio">
        <StudioSelector value={studioId} onChange={setStudioId} />
      </Field>

      <Field label="URLs">
        <StringListEditor values={urls} onChange={setUrls} placeholder="https://..." addLabel="Add URL" inputType="url" />
      </Field>

      <div className="mb-2 flex flex-wrap items-center gap-4 text-sm">
        <label className="flex items-center gap-2">
          <input
            type="checkbox"
            checked={isVr}
            onChange={(e) => setIsVr(e.target.checked)}
            className="rounded bg-card border-border"
          />
          VR
        </label>
      </div>

      <Field label="Tags">
        <EntityReferenceMultiSelector entityType="tag" values={selectedTagIds} onChange={setSelectedTagIds} placeholder="Search tags..." />
      </Field>

      <Field label="Performers">
        <EntityReferenceMultiSelector entityType="performer" values={selectedPerformerIds} onChange={setSelectedPerformerIds} placeholder="Search performers..." />
      </Field>

      <Field label="Galleries">
        <EntityReferenceMultiSelector entityType="gallery" values={selectedGalleryIds} onChange={setSelectedGalleryIds} placeholder="Search galleries..." />
      </Field>

      <Field label="Custom Fields">
        <CustomFieldsEditor value={customFields} onChange={setCustomFields} onValidityChange={setCustomFieldsValid} entityType="video" />
      </Field>

      <CreateModalActions
        loading={pending}
        disabled={!customFieldsValid}
        onCancel={onClose}
        onSave={handleSave}
        createAnother={createAnother}
        onCreateAnotherChange={setCreateAnother}
      />
      </>
    </EditModal>
  );
}

function CompilationGroupCard({ group, onNavigate }: { group: Group; onNavigate: (r: any) => void }) {
  return (
    <div className="entity-card relative flex h-full cursor-pointer flex-col overflow-hidden rounded border border-border bg-card transition-colors hover:border-accent/60 group">
      <RouteCardLinkOverlay route={{ page: "compilation", id: group.id }} onClick={() => onNavigate({ page: "compilation", id: group.id })} label={`Play compilation ${group.name}`} selectionSafeZone />
      <div className="relative flex aspect-video items-center justify-center overflow-hidden bg-surface">
        <BookmarkButton
          hostType="group"
          hostId={group.id}
          compact
          deferUntilHover
          className="absolute left-1 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100"
        />
        {group.frontImagePath ? (
          <img src={group.frontImagePath} alt={group.name} className="h-full w-full object-cover" loading="lazy" />
        ) : (
          <Layers className="h-10 w-10 text-muted opacity-40" />
        )}
        <div className="absolute bottom-1 left-1 rounded bg-black/70 px-1.5 py-0.5 text-xs font-medium text-white">
          Compilation
        </div>
        <div className="absolute bottom-1 right-1 rounded bg-black/70 px-1.5 py-0.5 text-xs text-white">
          {group.videoCount} videos
        </div>
      </div>
      <div className="border-t border-border/50 px-2.5 py-2">
        <p className="line-clamp-2 text-sm font-semibold text-foreground group-hover:text-accent">{group.name}</p>
        {group.studioName ? <p className="mt-1 truncate text-xs text-muted">{group.studioName}</p> : null}
      </div>
    </div>
  );
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
    return {
      coverUrl,
      videoSrc: videos.streamUrl(video.id),
      videoStatusSrc: undefined,
    };
  }

  return {
    coverUrl,
    videoSrc: videos.previewUrl(video.id),
    videoStatusSrc: videos.previewStatusUrl(video.id),
  };
}

function getVideoFeedVideoStartTime(video: Video, feedVideoSource: string, startPercent: number, minDuration: number) {
  if (feedVideoSource !== "video" || startPercent <= 0) {
    return 0;
  }

  const duration = getVideoDisplayDuration(video);
  if (duration <= Math.max(0, minDuration)) {
    return 0;
  }

  return duration * (Math.min(95, Math.max(0, startPercent)) / 100);
}

/* ── Video List Table ── */

function VideoListTable({ entries, onNavigate, selectedIds, onToggle, selecting }: { entries: VideoListEntry[]; engagementById: ReadonlyMap<number, EntityEngagement>; onNavigate: (r: any) => void; selectedIds?: Set<number>; onToggle?: MultiSelectToggleHandler; selecting?: boolean }) {
  return (
    <div className="mx-auto flex w-full max-w-7xl flex-col gap-2 px-2">
      {entries.map((entry) => {
        if (entry.kind === "compilation" && entry.group) {
          const group = entry.group;
          return <CompilationListRow key={`compilation-${group.id}`} group={group} onNavigate={onNavigate} />;
        }
        if (!entry.video) return null;
        return <RelatedEntityListRow key={`video-${entry.video.id}`} entityType="videos" item={entry.video} selected={selectedIds?.has(entry.video.id) ?? false} selecting={selecting} onToggle={onToggle} onNavigate={onNavigate} />;
      })}
    </div>
  );
}

function CompilationListRow({ group, onNavigate }: { group: Group; onNavigate: (r: any) => void }) {
  return (
    <article
      className="group flex min-h-[5.75rem] w-full cursor-pointer items-stretch gap-3 rounded-lg border border-border/70 bg-card/70 p-2 text-left shadow-sm shadow-black/10 transition-colors hover:border-accent/45 hover:bg-card"
      onClick={() => onNavigate({ page: "compilation", id: group.id })}
    >
      <div className="relative flex h-20 w-28 shrink-0 items-center justify-center overflow-hidden rounded-md border border-border/70 bg-surface/80 text-muted">
        <Layers className="h-7 w-7" />
      </div>
      <button type="button" onClick={(event) => { event.stopPropagation(); onNavigate({ page: "compilation", id: group.id }); }} className="flex min-w-0 flex-1 flex-col justify-center text-left">
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
          <h3 className="min-w-0 truncate text-sm font-semibold text-foreground transition-colors group-hover:text-accent sm:text-[15px]">{group.name}</h3>
          <span className="rounded-full border border-accent/30 bg-accent/10 px-2 py-0.5 text-[10px] font-semibold uppercase text-accent">Compilation</span>
        </div>
        <p className="mt-1 truncate text-xs text-secondary">{[group.studioName, group.date].filter(Boolean).join(" · ") || "Compilation"}</p>
        <div className="mt-2 flex flex-wrap gap-1.5 text-[11px] text-muted">
          <span className="rounded-full border border-border/70 bg-background/55 px-2 py-0.5">{group.videoCount} videos</span>
        </div>
      </button>
    </article>
  );
}

/* ── Video Wall Card ── */

function VideoWallCard({ video, onClick, selected, selecting, onSelect }: { video: Video; onClick: BoundMultiSelectToggleHandler; selected?: boolean; selecting?: boolean; onSelect?: BoundMultiSelectToggleHandler }) {
  const file = video.files[0];
  const coverUrl = entityImages.videoCoverUrl(video.id, video.updatedAt, 1280);
  const previewUrl = videos.previewUrl(video.id);
  const previewStatusUrl = videos.previewStatusUrl(video.id);
  const aspectRatio = file?.width && file.height ? `${file.width} / ${file.height}` : "16 / 9";
  const title = video.title || file?.basename || "Untitled";
  const coverAlt = video.imagePath ? title : "";
  const duration = getVideoDisplayDuration(video);
  const { config } = useAppConfig();
  const wallPreviewType = config?.ui.wallPreviewType ?? "video";
  const showTitle = config?.ui.wallShowTitle ?? true;

  return (
    <WallMediaCard
      title={title}
      imageSrc={coverUrl}
      imageAlt={coverAlt}
      videoSrc={previewUrl}
      videoStatusSrc={previewStatusUrl}
      useVideo={wallPreviewType === "video" || wallPreviewType === "webp"}
      // Browsers generally block autoplay with audio, so wall previews stay muted to animate reliably.
      muted
      aspectRatio={aspectRatio}
      imageClassName="object-cover"
      onClick={selecting ? (event) => onClick(toggleOptionsFromEvent(event)) : undefined}
      className="group"
    >
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <RouteCardLinkOverlay route={{ page: "video", id: video.id }} onClick={onClick} label={`Open video ${title}`} disabled={selecting} selectionSafeZone />
      <div className={`absolute inset-0 bg-gradient-to-t from-black/60 via-transparent to-transparent transition-opacity ${showTitle ? "opacity-0 group-hover:opacity-100" : "opacity-0"}`} />
      {showTitle ? <div className="absolute bottom-0 left-0 right-0 p-1.5 opacity-0 group-hover:opacity-100 transition-opacity">
          <p className="text-xs text-white font-medium truncate">
            {title}
          </p>
      </div> : null}
      {duration > 0 && (
        <span className="absolute top-1 right-1 text-xs text-white bg-black/70 px-1 rounded">
          {formatDuration(duration)}
        </span>
      )}
    </WallMediaCard>
  );
}

function VideoFeedCard({ video, engagement, feedVideoSource, useVideo, soundEnabled, onToggleSound, onPlaybackEligibilityChange, feedVideoStartPercent, feedVideoStartMinDuration, onNavigate, canEngage, selected, selecting, onSelect }: { video: Video; engagement?: EntityEngagement; feedVideoSource: string; useVideo: boolean; soundEnabled: boolean; onToggleSound: () => void; onPlaybackEligibilityChange?: (eligible: boolean) => void; feedVideoStartPercent: number; feedVideoStartMinDuration: number; onNavigate: (route: any) => void; canEngage: boolean; selected?: boolean; selecting?: boolean; onSelect?: BoundMultiSelectToggleHandler }) {
  const file = video.files[0];
  const { coverUrl, videoSrc, videoStatusSrc } = getVideoFeedMedia(video, feedVideoSource);
  const title = video.title || file?.basename || `Video ${video.id}`;
  const coverAlt = video.imagePath ? title : "";
  const duration = getVideoDisplayDuration(video);
  const aspectRatio = file?.width && file.height ? `${file.width} / ${file.height}` : "16 / 9";
  const mediaStyle = getFeedMediaStyle(file);
  const mediaIsPortrait = Boolean(mediaStyle);
  const videoStartTimeSec = getVideoFeedVideoStartTime(video, feedVideoSource, feedVideoStartPercent, feedVideoStartMinDuration);
  const visitCount = engagement?.pageVisitCount ?? 0;
  const likeCount = engagement?.likeCount ?? 0;
  const queryClient = useQueryClient();
  const ratingMut = useMutation({
    mutationFn: (value: number | undefined) => entityEngagement.setRating("video", video.id, { value: value ?? null, aspect: "overall" }),
    onSuccess: (nextEngagement) => {
      queryClient.setQueryData(["engagement", "video", video.id], nextEngagement);
      queryClient.invalidateQueries({ queryKey: ["engagement", "video", "batch"] });
    },
  });
  const ratingValue = ratingMut.data?.rating ?? engagement?.rating;
  const visibleTags = video.tags.slice(0, 4);
  const hiddenTags = video.tags.slice(4);
  const renderVideoControls = (controls: WallMediaVideoControlsState) => (
    <VideoFeedVideoControls controls={controls} soundEnabled={soundEnabled} onToggleSound={onToggleSound} />
  );
  const openOrSelect = (toggleOptions?: MultiSelectToggleOptions) => selecting ? onSelect?.(toggleOptions) : onNavigate({ page: "video", id: video.id });

  const mediaOverlay = (
    <>
      <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
      <RouteCardLinkOverlay route={{ page: "video", id: video.id }} onClick={openOrSelect} label={`Open video ${title}`} disabled={selecting} selectionSafeZone />
      {!selecting && (
        <BookmarkButton
          hostType="video"
          hostId={video.id}
          compact
          deferUntilHover
          className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100"
        />
      )}
    </>
  );

  return (
    <FeedCardFrame
      dataAttribute={{ "data-feed-video-id": video.id }}
      selected={selected}
      onClick={selecting ? (event) => openOrSelect(toggleOptionsFromEvent(event)) : undefined}
      identity={video.studioName ? <FeedIdentityBadge>{video.studioName}</FeedIdentityBadge> : undefined}
      header={(
        <>
          {video.date ? <span>{video.date}</span> : null}
          {duration > 0 ? <span>{formatDuration(duration)}</span> : null}
        </>
      )}
      headerActions={(
        <>
          <FeedInlineRating value={ratingValue} onChange={(value) => ratingMut.mutate(value)} readOnly={!canEngage} pending={ratingMut.isPending} />
          <FeedActionPill>
            <ThumbsUp className={["h-3.5 w-3.5", likeCount > 0 ? "fill-accent text-accent" : ""].join(" ")} />
            {likeCount}
          </FeedActionPill>
          {engagement?.isFavorite ? (
            <FeedActionPill>
              <Heart className="h-3.5 w-3.5 fill-current text-red-400" />
              Favorite
            </FeedActionPill>
          ) : null}
          <FeedActionPill>
            <Eye className="h-3.5 w-3.5" />
            {visitCount}
          </FeedActionPill>
        </>
      )}
      media={(
        mediaIsPortrait ? (
          <FeedPortraitMediaFrame
            title={title}
            backgroundSrc={coverUrl}
            className="cursor-pointer"
            media={(
              <WallMediaCard
                title={title}
                imageSrc={coverUrl}
                imageAlt={coverAlt}
                videoSrc={videoSrc}
                videoStatusSrc={videoStatusSrc}
                useVideo={useVideo}
                muted={!soundEnabled}
                videoStartTimeSec={videoStartTimeSec}
                videoPlayThreshold={0.5}
                onVideoPlayEligibilityChange={onPlaybackEligibilityChange}
                playbackTracking={{ hostType: "video", hostId: video.id, surface: "feed", scopeKey: `video-feed:${video.id}` }}
                fillMedia
                chromeless
                imageClassName="object-contain"
                videoClassName="object-contain"
                className="h-full w-full bg-transparent"
                videoControls={renderVideoControls}
              />
            )}
          >
            {mediaOverlay}
          </FeedPortraitMediaFrame>
        ) : (
          <WallMediaCard
            title={title}
            imageSrc={coverUrl}
            imageAlt={coverAlt}
            videoSrc={videoSrc}
            videoStatusSrc={videoStatusSrc}
            useVideo={useVideo}
            muted={!soundEnabled}
            videoStartTimeSec={videoStartTimeSec}
            videoPlayThreshold={0.5}
            onVideoPlayEligibilityChange={onPlaybackEligibilityChange}
            playbackTracking={{ hostType: "video", hostId: video.id, surface: "feed", scopeKey: `video-feed:${video.id}` }}
            aspectRatio={aspectRatio}
            imageClassName="object-cover"
            style={mediaStyle}
            className="overflow-hidden rounded-2xl border border-border/70 bg-black/95 shadow-[0_18px_40px_rgba(0,0,0,0.35)] hover:border-border/70"
            videoControls={renderVideoControls}
          >
            {mediaOverlay}
          </WallMediaCard>
        )
      )}
      title={(
        <button
          type="button"
          onClick={(event) => {
            event.stopPropagation();
            openOrSelect(toggleOptionsFromEvent(event));
          }}
          className="text-left text-base font-semibold text-foreground transition-colors hover:text-accent"
        >
          {title}
        </button>
      )}
      details={video.details ? <NarrativeText className="line-clamp-4">{video.details}</NarrativeText> : undefined}
      metadata={(video.organized || video.galleries.length > 0) ? (
        <>
          {video.organized ? <FeedMetadataPill>Organized</FeedMetadataPill> : null}
          {video.galleries.length > 0 ? <FeedMetadataPill>{video.galleries.length} galleries</FeedMetadataPill> : null}
        </>
      ) : undefined}
      chips={(
        <>
          {video.performers.slice(0, 4).map((performer) => (
            <FeedChipButton
              key={performer.id}
              onClick={(event) => selecting ? onSelect?.(toggleOptionsFromEvent(event)) : onNavigate({ page: "performer", id: performer.id })}
            >
              {performer.name}
            </FeedChipButton>
          ))}
          {visibleTags.map((tag) => (
            <FeedChipButton
              key={tag.id}
              onClick={(event) => selecting ? onSelect?.(toggleOptionsFromEvent(event)) : onNavigate({ page: "tag", id: tag.id })}
            >
              #{tag.name}
            </FeedChipButton>
          ))}
          {hiddenTags.length > 0 ? (
            <FeedChipOverflowMenu>
              {hiddenTags.map((tag) => (
                <FeedChipButton
                  key={tag.id}
                  onClick={(event) => selecting ? onSelect?.(toggleOptionsFromEvent(event)) : onNavigate({ page: "tag", id: tag.id })}
                >
                  #{tag.name}
                </FeedChipButton>
              ))}
            </FeedChipOverflowMenu>
          ) : null}
        </>
      )}
    />
  );
}

function VideoFeedVideoControls({ controls, soundEnabled, onToggleSound }: { controls: WallMediaVideoControlsState; soundEnabled: boolean; onToggleSound: () => void }) {
  const seekValue = Math.round(controls.progressPercent * 10);

  return (
    <>
      <button
        type="button"
        onClick={(event) => {
          event.preventDefault();
          event.stopPropagation();
          onToggleSound();
        }}
        className="absolute bottom-14 right-3 z-20 flex h-10 w-10 items-center justify-center rounded-full bg-black/45 text-white shadow transition-colors hover:bg-black/70"
        aria-label={soundEnabled ? "Mute this feed item" : "Unmute this feed item"}
        title={soundEnabled ? "Mute this feed item" : "Unmute this feed item"}
      >
        {soundEnabled ? <Volume2 className="h-5 w-5" /> : <VolumeX className="h-5 w-5" />}
      </button>
      <div className="pointer-events-none absolute inset-x-3 bottom-3 z-20 flex items-center gap-2 rounded-full bg-black/45 px-2.5 py-1.5 text-white shadow-lg">
        <button
          type="button"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            controls.togglePlayback();
          }}
          className="pointer-events-auto flex h-7 w-7 items-center justify-center rounded-full text-white/90 transition-colors hover:bg-white/15 hover:text-white"
          aria-label={controls.isPlaying ? "Pause feed video" : "Play feed video"}
          title={controls.isPlaying ? "Pause" : "Play"}
        >
          {controls.isPlaying ? <Pause className="h-4 w-4" /> : <Play className="h-4 w-4" />}
        </button>
        <input
          type="range"
          min={0}
          max={1000}
          step={1}
          value={seekValue}
          onChange={(event) => controls.seekToPercent(Number(event.target.value) / 1000)}
          onClick={(event) => event.stopPropagation()}
          onMouseDown={(event) => event.stopPropagation()}
          className="pointer-events-auto h-1 min-w-0 flex-1 cursor-pointer accent-white"
          aria-label="Seek feed video"
          title="Seek"
        />
        <span className="min-w-[2.4rem] text-right text-[11px] tabular-nums text-white/90">
          {formatDuration(controls.currentTime || 0)}
        </span>
        <button
          type="button"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            controls.toggleFullscreen();
          }}
          className="pointer-events-auto flex h-7 w-7 items-center justify-center rounded-full text-white/90 transition-colors hover:bg-white/15 hover:text-white"
          aria-label={controls.isFullscreen ? "Exit fullscreen" : "Enter fullscreen"}
          title={controls.isFullscreen ? "Exit fullscreen" : "Enter fullscreen"}
        >
          {controls.isFullscreen ? <Minimize2 className="h-4 w-4" /> : <Maximize2 className="h-4 w-4" />}
        </button>
      </div>
    </>
  );
}

function VideoVerticalViewerCard({ video, feedVideoSource, useVideo, soundEnabled, onToggleSound, feedVideoStartPercent, feedVideoStartMinDuration, fullscreen, viewerHeight, onNavigate, selected, selecting, onSelect }: { video: Video; feedVideoSource: string; useVideo: boolean; soundEnabled: boolean; onToggleSound: () => void; feedVideoStartPercent: number; feedVideoStartMinDuration: number; fullscreen: boolean; viewerHeight: number | null; onNavigate: (videoId: number) => void; selected?: boolean; selecting?: boolean; onSelect?: BoundMultiSelectToggleHandler }) {
  const file = video.files[0];
  const { coverUrl, videoSrc, videoStatusSrc } = getVideoFeedMedia(video, feedVideoSource);
  const title = video.title || file?.basename || `Video ${video.id}`;
  const coverAlt = video.imagePath ? title : "";
  const duration = getVideoDisplayDuration(video);
  const videoStartTimeSec = getVideoFeedVideoStartTime(video, feedVideoSource, feedVideoStartPercent, feedVideoStartMinDuration);
  const availableViewerHeight = viewerHeight != null ? Math.max(120, viewerHeight) : null;
  const openOrSelect = (toggleOptions?: MultiSelectToggleOptions) => selecting ? onSelect?.(toggleOptions) : onNavigate(video.id);

  return (
    <article data-vertical-video-id={video.id} className={`flex h-full min-h-0 snap-start snap-always items-center justify-center ${fullscreen ? "px-0 py-0" : "px-2 py-0 sm:px-4"}`}>
      <WallMediaCard
        title={title}
        imageSrc={coverUrl}
        imageAlt={coverAlt}
        videoSrc={videoSrc}
        videoStatusSrc={videoStatusSrc}
        useVideo={useVideo}
        muted={!soundEnabled}
        videoStartTimeSec={videoStartTimeSec}
        videoPlayThreshold={0.72}
        playbackTracking={{ hostType: "video", hostId: video.id, surface: "vertical", scopeKey: `video-vertical:${video.id}` }}
        aspectRatio="9 / 16"
        imageClassName="object-cover"
        fillMedia={fullscreen}
        onClick={selecting ? (event) => openOrSelect(toggleOptionsFromEvent(event)) : undefined}
        style={fullscreen
          ? { width: "min(100vw, 56.25dvh)", height: "100dvh" }
          : { width: availableViewerHeight != null ? `min(calc(100vw - 1rem), ${Math.round(availableViewerHeight * 0.5625)}px)` : "min(calc(100vw - 1rem), calc((100dvh - 10rem) * 0.5625))" }}
        className={`group mx-auto overflow-hidden bg-card shadow-2xl transition-colors ${fullscreen ? "rounded-none border-0" : "rounded-[1.5rem] sm:rounded-[1.75rem]"} ${selected ? "border-accent ring-1 ring-accent/60" : "border-border hover:border-accent/50"}`}
      >
        <button
          type="button"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            onToggleSound();
          }}
          className="absolute right-2 top-2 z-20 rounded-full border border-white/15 bg-black/60 p-2 text-white shadow transition-colors hover:bg-black/80"
          aria-label={soundEnabled ? "Mute Vertical Viewer" : "Unmute Vertical Viewer"}
          title={soundEnabled ? "Mute Vertical Viewer" : "Unmute Vertical Viewer"}
        >
          {soundEnabled ? <Volume2 className="h-4 w-4" /> : <VolumeX className="h-4 w-4" />}
        </button>
        <CardSelectionToggle selected={selected} selecting={selecting} onToggle={onSelect} />
        <RouteCardLinkOverlay route={{ page: "video", id: video.id }} onClick={openOrSelect} label={`Open video ${title}`} disabled={selecting} selectionSafeZone />
        {!selecting && (
          <BookmarkButton
            hostType="video"
            hostId={video.id}
            compact
            deferUntilHover
            className="absolute left-9 top-1 z-10 border-white/20 bg-black/60 text-white opacity-0 shadow transition-opacity hover:bg-black/80 group-hover:opacity-100 focus:opacity-100"
          />
        )}
        {duration > 0 ? <span className="absolute right-2 top-12 rounded bg-black/65 px-2 py-0.5 text-xs text-white">{formatDuration(duration)}</span> : null}
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
