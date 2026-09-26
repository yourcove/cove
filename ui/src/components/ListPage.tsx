import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
  type RefObject,
} from "react";
import { ViewInVrButton } from "./ViewInVrButton";
import type { VrListSource } from "../vr/vrListRegistry";
import {
  LayoutGrid,
  List,
  Tags,
  Grid3X3,
  Share2,
  FolderTree,
  ZoomIn,
  ZoomOut,
  Rows3,
  MonitorPlay,
  Play,
  Pause,
} from "lucide-react";
import type { ExtensionListFilterContribution, ExtensionListSortContribution, FindFilter } from "../api/types";
import { ExtensionSlot } from "../router/RouteRegistry";
import { getDefaultFilter, SavedFilterMenu } from "./SavedFilterMenu";
import { InfiniteScrollSentinel } from "./InfiniteScrollSentinel";
import { FilterDialog, type FilterDialogPreselection } from "./FilterDialog";
import { FilterButton } from "./FilterButton";
import type { CriterionDefinition, CriterionType, EntityType, FilterDialogCustomSection } from "./filterCriteriaTypes";
import { migrateLegacyPerformerFavoriteCriterion } from "./filterCriterionState";
import { useResolvedKeybindingOverrides } from "../hooks/useResolvedKeybindingOverrides";
import { useKeySequence } from "../hooks/useKeySequence";
import { resolveKeybinding } from "../keyboard/keybindings";
import { useCustomFieldDefinitions } from "../hooks/useCustomFieldDefinitions";
import {
  createCustomFieldQueryDefinitions,
  customFieldEntityTypeForFilterMode,
  useCustomFieldFilterSection,
} from "./CustomFieldFilterSection";
import {
  clampEntityCardSizeLevel,
  getEntityCardMaxLevel,
  getEntityCardMinWidthPx,
  parseEntityCardSizeLevel,
  useEntityCardSize,
} from "../hooks/useEntityCardSize";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { withSeededRandomSort } from "../utils/seededRandomSort";
import { getSortClauses } from "../utils/sortClauses";
import {
  normalizeListEntityType,
  resolveSearchQueryFilter,
  withRelevanceSortOption,
  type PreviousSearchSort,
} from "../utils/relevanceSort";
import { trackInteraction } from "../utils/interactionTracking";
import { toolbarIconButtonClass, toolbarSegmentClass } from "./listToolbarStyles";
import { PageSizeSelect } from "./PageSizeSelect";
import { ListPageCardSizeContext } from "./ListPageCardSizeContext";
import { useExtensions } from "../extensions/ExtensionLoader";
import {
  ActiveObjectFilterChips,
  countActiveObjectFilters,
  getFilterChipTargetKey,
  removeObjectFilterChipTarget,
} from "./ActiveObjectFilterChips";
import {
  collapseExtensionCriteria,
  executableExtensionFilterKey,
  expandExtensionCriteria,
  unavailableExtensionCriterionDefinitions,
} from "../extensions/extensionListFilters";
import { QueryState } from "./QueryState";
import { resolveQueryLoadState, type QueryLoadState } from "../utils/queryLoadState";
import { ListSearchControl, type ListSearchCommitSource } from "./ListSearchControl";
import { PaginationControls } from "./PaginationControls";
import { MultiSortControl } from "./MultiSortControl";
import { getWallColumnCountFromSizeLevel, getWallSizeLevelFromColumnCount, WallSizeControl } from "./WallSizeControl";

export type DisplayMode = "grid" | "list" | "wall" | "tagger" | "graph" | "byGroup" | "feed" | "vertical";

export interface ListPageProps {
  title: string;
  pageKey?: string;
  filter: FindFilter;
  onFilterChange: (f: FindFilter) => void;
  totalCount: number;
  isLoading?: boolean;
  summaryLoading?: boolean;
  error?: Error | null;
  onRetry?: () => void;
  loadState?: QueryLoadState<unknown>;
  children: ReactNode;
  sortOptions?: { value: string; label: string }[];
  multiSortKeys?: readonly string[];
  displayMode?: DisplayMode;
  onDisplayModeChange?: (mode: DisplayMode) => void;
  availableDisplayModes?: DisplayMode[];
  allowInfinitePageSize?: boolean;
  infinitePageSizeOnly?: boolean;
  maxPageSize?: number;
  perPageQueryKey?: string;
  selectedIds?: Set<string | number>;
  onSelectAll?: () => void;
  onSelectAllMatching?: () => void;
  onSelectNone?: () => void;
  onInvertSelection?: () => void;
  selectionActions?: ReactNode;
  selectionMetadata?: ReactNode;
  selectAllLabel?: string;
  selectAllPending?: boolean;
  selectAllMatchingLabel?: string;
  selectAllMatchingPending?: boolean;
  metadataByline?: ReactNode;
  onNew?: () => void;
  renderOperations?: () => ReactNode;
  /** When set, a "View in VR" button shows this list in a headset. */
  vrListSource?: VrListSource;
  onNavigate?: (route: any) => void;
  filterMode?: string;
  savedFilterScope?: string;
  cardSizeEntityType?: string;
  manageDocumentTitle?: boolean;
  savedFilterUIOptions?: Record<string, unknown>;
  onApplySavedFilterUIOptions?: (options: Record<string, unknown>) => void;
  searchMode?: string;
  searchModes?: { value: string; label: string; title?: string }[];
  searchPlaceholder?: string;
  onSearchModeChange?: (mode: string) => void;
  // Advanced filtering
  criteriaDefinitions?: CriterionDefinition[];
  objectFilter?: Record<string, unknown>;
  onObjectFilterChange?: (filter: Record<string, unknown>) => void;
  wallColumnCount?: number;
  onWallColumnCountChange?: (count: number) => void;
  autoScrollContainerRef?: RefObject<HTMLElement | null>;
  infiniteScroll?: {
    hasNextPage?: boolean;
    isFetchingNextPage?: boolean;
    onLoadMore: () => void;
    loadedCount: number;
    totalCount: number;
  };
  showAutoScrollControls?: boolean;
  showPagingControls?: boolean;
  customFilterSections?: FilterDialogCustomSection[];
  showClearAllObjectFilters?: boolean;
  supportsFilterExpressions?: boolean;
}
const DEFAULT_ZOOM_LEVEL = 1;
const REFERENCE_ENTITY_TYPE_BY_EXTENSION_VALUE: Record<string, EntityType> = {
  tag: "tags",
  tags: "tags",
  taggroup: "tagGroups",
  taggroups: "tagGroups",
  tag_group: "tagGroups",
  performer: "performers",
  performers: "performers",
  studio: "studios",
  studios: "studios",
  group: "groups",
  groups: "groups",
  gallery: "galleries",
  galleries: "galleries",
  video: "videos",
  videos: "videos",
  face: "faces",
  faces: "faces",
};

function normalizeCriterionType(value: string | undefined): CriterionType {
  const normalized = (value ?? "string").trim().toLowerCase();
  switch (normalized) {
    case "bool":
    case "boolean":
      return "bool";
    case "int":
    case "integer":
    case "number":
      return "number";
    case "date":
      return "date";
    case "datetime":
    case "timestamp":
      return "timestamp";
    case "duration":
      return "duration";
    case "percent":
      return "number";
    case "rating":
      return "rating";
    case "multiid":
    case "reference":
      return "multiId";
    case "enum":
      return "enum";
    default:
      return "string";
  }
}

function normalizeReferenceEntityType(value?: string): EntityType | undefined {
  if (!value) return undefined;
  return REFERENCE_ENTITY_TYPE_BY_EXTENSION_VALUE[value.trim().toLowerCase()];
}

function createExtensionCriterionDefinition(contribution: ExtensionListFilterContribution): CriterionDefinition | null {
  const criterionType = normalizeCriterionType(contribution.criterionType || contribution.customFieldType);
  const executableFilterKey = executableExtensionFilterKey(contribution);
  if (contribution.filterId && !executableFilterKey) return null;
  const filterKey =
    executableFilterKey ||
    contribution.filterKey ||
    (contribution.customFieldKey ? `extension:${contribution.extensionId}:${contribution.id}` : undefined);
  if (!filterKey) return null;

  return {
    id: `extension:${contribution.extensionId}:${contribution.id}`,
    label: contribution.label,
    type: criterionType,
    entityType: normalizeReferenceEntityType(contribution.entityReferenceType),
    filterKey,
    customFieldKey: contribution.customFieldKey,
    customFieldType: contribution.customFieldType,
    modifiers: contribution.modifiers,
    options: contribution.options,
  };
}

function createExtensionSortOption(contribution: ExtensionListSortContribution) {
  const value =
    contribution.sortKey ||
    (contribution.customFieldKey
      ? `custom:${contribution.customFieldType || "text"}:${contribution.customFieldKey}`
      : undefined);
  return value ? { value, label: contribution.label } : null;
}

function createUnavailableCustomSortOption(key: string) {
  const parts = key.split(":");
  if (parts[0] === "custom-json" && parts.length === 4) {
    let path = parts[3];
    try {
      path = decodeURIComponent(path);
    } catch {
      // Keep the encoded token visible when it is malformed.
    }
    return { value: key, label: `Unavailable custom sort: ${parts[2]} › ${path}` };
  }
  return { value: key, label: `Unavailable custom sort: ${parts.at(-1) ?? key}` };
}

export function ListPage({
  title,
  pageKey,
  filter,
  onFilterChange,
  totalCount,
  isLoading = false,
  summaryLoading = false,
  error,
  onRetry,
  loadState,
  children,
  sortOptions,
  multiSortKeys,
  displayMode,
  onDisplayModeChange,
  availableDisplayModes,
  allowInfinitePageSize = false,
  infinitePageSizeOnly = false,
  maxPageSize,
  perPageQueryKey,
  selectedIds,
  onSelectAll,
  onSelectAllMatching,
  onSelectNone,
  onInvertSelection,
  selectionActions,
  selectionMetadata,
  selectAllLabel = "Select all",
  selectAllPending = false,
  selectAllMatchingLabel = "Select all matching",
  selectAllMatchingPending = false,
  metadataByline,
  onNew,
  renderOperations,
  vrListSource,
  onNavigate,
  filterMode,
  savedFilterScope,
  cardSizeEntityType: requestedCardSizeEntityType,
  manageDocumentTitle = true,
  savedFilterUIOptions,
  onApplySavedFilterUIOptions,
  searchMode,
  searchModes,
  searchPlaceholder,
  onSearchModeChange,
  criteriaDefinitions,
  objectFilter,
  onObjectFilterChange,
  wallColumnCount,
  onWallColumnCountChange,
  autoScrollContainerRef,
  infiniteScroll,
  showAutoScrollControls = true,
  showPagingControls = true,
  customFilterSections,
  showClearAllObjectFilters = true,
  supportsFilterExpressions = false,
}: ListPageProps) {
  const [filterDialogOpen, setFilterDialogOpen] = useState(false);
  const [filterDialogPreselect, setFilterDialogPreselect] = useState<FilterDialogPreselection | undefined>();
  const [filterDialogInitialView, setFilterDialogInitialView] = useState<"simple" | "advanced">("simple");
  const [filterDialogExpressionPath, setFilterDialogExpressionPath] = useState<number[] | undefined>();
  const [filterDialogOpenAtRoot, setFilterDialogOpenAtRoot] = useState(false);
  const cardSizeEntityType = requestedCardSizeEntityType ?? filterMode ?? pageKey;
  const resolvedSavedFilterScope = savedFilterScope ?? filterMode;
  const [zoomLevel, setZoomLevel] = useEntityCardSize(cardSizeEntityType, pageKey, DEFAULT_ZOOM_LEVEL);
  const cardSizeMaxLevel = getEntityCardMaxLevel(cardSizeEntityType);
  const cardMinWidthPx = getEntityCardMinWidthPx(cardSizeEntityType, zoomLevel);
  const [autoScrollEnabled, setAutoScrollEnabled] = useState(false);
  const [autoScrollSpeed, setAutoScrollSpeed] = useState(120);
  const [autoScrollControlsAwake, setAutoScrollControlsAwake] = useState(true);
  const defaultUIOptionsModeRef = useRef<string | undefined>(undefined);
  const restoredPrefsRef = useRef(false);
  const { getListFiltersForEntity, getListSortsForEntity } = useExtensions();
  const keybindingOverrides = useResolvedKeybindingOverrides();
  const customFieldEntityType = customFieldEntityTypeForFilterMode(filterMode);
  const listEntityType = normalizeListEntityType(filterMode ?? pageKey);
  const extensionCriteriaDefinitions = useMemo(
    () =>
      getListFiltersForEntity(listEntityType)
        .map(createExtensionCriterionDefinition)
        .filter((item): item is CriterionDefinition => item != null),
    [getListFiltersForEntity, listEntityType],
  );
  const extensionFilterContributions = useMemo(
    () => getListFiltersForEntity(listEntityType),
    [getListFiltersForEntity, listEntityType],
  );
  const unavailableExtensionCriteria = useMemo(
    () => unavailableExtensionCriterionDefinitions(objectFilter ?? {}, extensionFilterContributions),
    [extensionFilterContributions, objectFilter],
  );

  const applySavedFilterUIOptions = useCallback(
    (options: Record<string, unknown>, applyDisplayMode = true) => {
      const nextDisplayMode =
        typeof options.displayMode === "string" ? (options.displayMode as DisplayMode) : undefined;
      if (
        applyDisplayMode &&
        nextDisplayMode &&
        onDisplayModeChange &&
        (!availableDisplayModes || availableDisplayModes.includes(nextDisplayMode))
      ) {
        onDisplayModeChange(nextDisplayMode);
      }
      const nextZoomLevel = parseEntityCardSizeLevel(cardSizeEntityType, options.zoomLevel);
      if (nextZoomLevel != null) setZoomLevel(nextZoomLevel);
      onApplySavedFilterUIOptions?.(options);
    },
    [availableDisplayModes, cardSizeEntityType, onApplySavedFilterUIOptions, onDisplayModeChange, setZoomLevel],
  );

  useEffect(() => {
    if (!resolvedSavedFilterScope || defaultUIOptionsModeRef.current === resolvedSavedFilterScope) return;
    defaultUIOptionsModeRef.current = resolvedSavedFilterScope;
    const options = getDefaultFilter(resolvedSavedFilterScope)?.uiOptions;
    if (!options) return;
    const explicitDisplayMode = new URLSearchParams(window.location.search).get("view");
    const hasSupportedExplicitDisplayMode =
      explicitDisplayMode != null &&
      (!availableDisplayModes || availableDisplayModes.includes(explicitDisplayMode as DisplayMode));
    applySavedFilterUIOptions(options, !hasSupportedExplicitDisplayMode);
  }, [applySavedFilterUIOptions, availableDisplayModes, resolvedSavedFilterScope]);
  const mergedCriteriaDefinitions = useMemo(() => {
    const merged = [...(criteriaDefinitions ?? []), ...extensionCriteriaDefinitions, ...unavailableExtensionCriteria];
    return merged.length > 0 ? merged : undefined;
  }, [criteriaDefinitions, extensionCriteriaDefinitions, unavailableExtensionCriteria]);
  const editorObjectFilter = useMemo(
    () =>
      migrateLegacyPerformerFavoriteCriterion(
        expandExtensionCriteria(objectFilter ?? {}),
        mergedCriteriaDefinitions ?? [],
      ),
    [mergedCriteriaDefinitions, objectFilter],
  );
  const extensionSortOptions = useMemo(
    () =>
      getListSortsForEntity(listEntityType)
        .map(createExtensionSortOption)
        .filter((item): item is { value: string; label: string } => item != null),
    [getListSortsForEntity, listEntityType],
  );
  const { data: customFieldDefinitions = [] } = useCustomFieldDefinitions(
    customFieldEntityType,
    Boolean(customFieldEntityType),
  );
  const generatedCustomFieldSection = useCustomFieldFilterSection(customFieldEntityType, objectFilter);
  const mergedCustomFilterSections = useMemo(
    () =>
      generatedCustomFieldSection
        ? [...(customFilterSections ?? []), generatedCustomFieldSection]
        : customFilterSections,
    [customFilterSections, generatedCustomFieldSection],
  );

  const perPage = filter.perPage ?? 25;
  const previousSearchSortRef = useRef<PreviousSearchSort | null>(null);
  const infinitePageSize = allowInfinitePageSize && (perPage === 0 || infinitePageSizeOnly);
  const page = filter.page ?? 1;
  const resolvedLoadState =
    loadState ??
    resolveQueryLoadState({
      data: isLoading || error ? undefined : true,
      isPending: isLoading,
      error,
      isEmpty: () => false,
      retry: onRetry,
    });
  // Callers report a zero result count while a page change loads. Remember the last settled count of
  // the current list so the item range, byline and top pager stay in place across the reload instead
  // of flickering on every page change; only the range for the requested page changes immediately.
  // A changed filter is a different list, so it shows the loading label until its own count arrives.
  const listIdentity = useMemo(
    () => JSON.stringify([{ ...filter, page: undefined }, objectFilter]),
    [filter, objectFilter],
  );
  const [settledCount, setSettledCount] = useState({ listIdentity, totalCount });
  if (
    resolvedLoadState.status !== "pending" &&
    (settledCount.listIdentity !== listIdentity || !Object.is(settledCount.totalCount, totalCount))
  ) {
    setSettledCount({ listIdentity, totalCount });
  }
  const settledTotalPages = Math.ceil(
    settledCount.totalCount / (infinitePageSize ? Math.max(settledCount.totalCount, 1) : perPage),
  );
  const reloading =
    resolvedLoadState.status === "pending" &&
    settledCount.listIdentity === listIdentity &&
    settledCount.totalCount > 0 &&
    page <= settledTotalPages;
  const shownTotalCount = reloading ? settledCount.totalCount : totalCount;
  // The count and the byline summarise the same list, so reveal them together behind one loading label
  // instead of letting whichever request settles first appear beside the other's indicator.
  const summaryPending = (resolvedLoadState.status === "pending" && !reloading) || summaryLoading;
  // Keep the last successfully committed results on screen while that reload is pending, so a page
  // change swaps the old items for the new ones instead of collapsing to a spinner in between, which
  // also removed the scrollbar and shifted the layout. The children are rendered as a fragment, so
  // the synthetic success state never hands its undefined data to a render callback.
  const settledChildrenRef = useRef<ReactNode>(children);
  useEffect(() => {
    if (resolvedLoadState.status === "success" || resolvedLoadState.status === "empty") {
      settledChildrenRef.current = children;
    }
  });
  const resultsState: QueryLoadState<unknown> = reloading ? { status: "success", data: undefined } : resolvedLoadState;
  // oxlint-disable-next-line react/refs -- intentionally shows the last committed results while a reload is pending
  const resultsChildren = reloading ? settledChildrenRef.current : children;
  const effectivePerPage = infinitePageSize ? Math.max(shownTotalCount, 1) : perPage;
  const totalPages = Math.max(1, Math.ceil(shownTotalCount / effectivePerPage));
  const start = shownTotalCount > 0 ? (infinitePageSize ? 1 : (page - 1) * effectivePerPage + 1) : 0;
  const end = infinitePageSize ? shownTotalCount : Math.min(page * effectivePerPage, shownTotalCount);
  const sortedSortOptions = useMemo(() => {
    const customSortOptions = createCustomFieldQueryDefinitions(customFieldDefinitions, "sortable").map(
      (definition) => ({
        value: definition.jsonPath
          ? `custom-json:${definition.type}:${definition.key}:${encodeURIComponent(definition.jsonPath)}`
          : `custom:${definition.type}:${definition.key}`,
        label: `Custom: ${definition.label || definition.key}`,
      }),
    );
    const mergedOptions = withRelevanceSortOption(
      [...(sortOptions ?? []), ...extensionSortOptions, ...customSortOptions],
      { listEntityType, filter },
    );
    const knownValues = new Set(mergedOptions.map((option) => option.value));
    const unavailableCustomSortOptions = getSortClauses(filter)
      .filter(
        (clause) =>
          (clause.key.startsWith("custom:") || clause.key.startsWith("custom-json:")) && !knownValues.has(clause.key),
      )
      .map((clause) => createUnavailableCustomSortOption(clause.key));
    mergedOptions.push(...unavailableCustomSortOptions);
    return mergedOptions.length > 0
      ? mergedOptions.sort((left, right) => left.label.localeCompare(right.label))
      : undefined;
  }, [customFieldDefinitions, extensionSortOptions, filter, listEntityType, sortOptions]);
  const slotContext = { pageKey, title, filter, onFilterChange, totalCount, isLoading };
  const selecting = selectedIds && selectedIds.size > 0;
  const showSelectionBar = Boolean(selectedIds && selecting);
  const showInfiniteAutoScrollControls = infinitePageSize && showAutoScrollControls;
  const contentOwnsInfiniteLoading =
    infinitePageSize &&
    (displayMode === "grid" || displayMode === "wall" || displayMode === "feed" || displayMode === "vertical");
  const wakeAutoScrollControls = useCallback(() => setAutoScrollControlsAwake(true), []);

  if ((!infinitePageSize || !showAutoScrollControls) && autoScrollEnabled) {
    setAutoScrollEnabled(false);
  }

  useEffect(() => {
    if (!showInfiniteAutoScrollControls || !autoScrollControlsAwake) {
      return;
    }

    const timeoutId = window.setTimeout(() => setAutoScrollControlsAwake(false), autoScrollEnabled ? 2600 : 3600);
    return () => window.clearTimeout(timeoutId);
  }, [autoScrollControlsAwake, autoScrollEnabled, autoScrollSpeed, showInfiniteAutoScrollControls]);

  useEffect(() => {
    if (!showInfiniteAutoScrollControls || !autoScrollEnabled) {
      return;
    }

    const container = autoScrollContainerRef?.current;
    const previousSnapType = container?.style.scrollSnapType;
    let previousTime = performance.now();
    let pendingDistance = 0;
    let frameId = 0;

    if (container) {
      // oxlint-disable-next-line react/immutability -- the effect owns this DOM style and restores it on cleanup
      container.style.scrollSnapType = "none";
    }

    const step = (currentTime: number) => {
      const deltaSeconds = Math.min(0.05, Math.max(0, (currentTime - previousTime) / 1000));
      previousTime = currentTime;
      pendingDistance += autoScrollSpeed * deltaSeconds;
      const distance = Math.floor(pendingDistance);

      if (distance < 1) {
        frameId = window.requestAnimationFrame(step);
        return;
      }

      pendingDistance -= distance;

      if (container) {
        const maxScrollTop = Math.max(0, container.scrollHeight - container.clientHeight);
        if (container.scrollTop < maxScrollTop - 1) {
          container.scrollTop = Math.min(maxScrollTop, container.scrollTop + distance);
        }
      } else {
        const scrollingElement = document.scrollingElement ?? document.documentElement;
        const maxScrollTop = Math.max(0, scrollingElement.scrollHeight - window.innerHeight);
        if (window.scrollY < maxScrollTop - 1) {
          window.scrollTo({ top: Math.min(maxScrollTop, window.scrollY + distance), behavior: "auto" });
        }
      }

      frameId = window.requestAnimationFrame(step);
    };

    frameId = window.requestAnimationFrame(step);

    return () => {
      window.cancelAnimationFrame(frameId);
      if (container) {
        container.style.scrollSnapType = previousSnapType ?? "";
      }
    };
  }, [autoScrollContainerRef, autoScrollEnabled, autoScrollSpeed, showInfiniteAutoScrollControls]);

  useEffect(() => {
    if (!pageKey || restoredPrefsRef.current) {
      return;
    }

    restoredPrefsRef.current = true;

    try {
      const raw = localStorage.getItem(`cove-list-prefs-${pageKey}`);
      if (!raw) {
        return;
      }

      const parsed = JSON.parse(raw) as { perPage?: number; wallColumnCount?: number };

      if (typeof parsed.wallColumnCount === "number" && onWallColumnCountChange) {
        onWallColumnCountChange(Math.min(12, Math.max(2, parsed.wallColumnCount)));
      }

      const hasPerPageOverride = new URLSearchParams(window.location.search).has(perPageQueryKey ?? "perPage");
      const persistedPerPageAllowed =
        typeof parsed.perPage === "number" &&
        (parsed.perPage > 0 || (allowInfinitePageSize && parsed.perPage === 0)) &&
        (maxPageSize == null || parsed.perPage <= maxPageSize);
      if (!hasPerPageOverride && persistedPerPageAllowed && parsed.perPage !== perPage) {
        onFilterChange({ ...filter, perPage: parsed.perPage, page: 1 });
      }
    } catch {
      // Ignore invalid persisted list preferences.
    }
  }, [
    allowInfinitePageSize,
    filter,
    maxPageSize,
    onFilterChange,
    onWallColumnCountChange,
    pageKey,
    perPage,
    perPageQueryKey,
  ]);

  useEffect(() => {
    if (!pageKey) {
      return;
    }

    localStorage.setItem(`cove-list-prefs-${pageKey}`, JSON.stringify({ perPage, wallColumnCount }));
  }, [pageKey, perPage, wallColumnCount]);

  const handleSearchChange = useCallback(
    (query: string | undefined, source: ListSearchCommitSource) => {
      if (pageKey && query && source !== "clear") {
        trackInteraction({
          hostType: "collection",
          kind: "searchQuery",
          meta: {
            pageKey,
            query,
            source: "listPageToolbar",
            activeFilterCount: Object.keys(objectFilter ?? {}).length,
          },
        });
      }

      const resolved = resolveSearchQueryFilter({
        filter,
        query,
        listEntityType,
        sortOptions,
        previousSearchSort: previousSearchSortRef.current,
      });
      previousSearchSortRef.current = resolved.previousSearchSort;
      onFilterChange(resolved.filter);
    },
    [filter, listEntityType, objectFilter, onFilterChange, pageKey, sortOptions],
  );

  const goTo = useCallback(
    (p: number) => {
      // An unavailable result count is not a one-page collection.
      if (resolvedLoadState.status === "pending" || resolvedLoadState.status === "error") return;
      onFilterChange({ ...filter, page: Math.max(1, Math.min(totalPages, p)) });
    },
    [filter, onFilterChange, resolvedLoadState.status, totalPages],
  );

  // List-page keyboard shortcuts
  const listBindings = useMemo(
    () => [
      // "/" focuses search
      {
        id: "list.search",
        keys: resolveKeybinding(keybindingOverrides, "list.search", "/"),
        surface: "list" as const,
        action: () => {
          document.querySelector<HTMLInputElement>("input[data-list-search='true']")?.focus();
        },
      },
      // View switching
      ...(onDisplayModeChange && availableDisplayModes
        ? [
            ...(availableDisplayModes.includes("grid")
              ? [
                  {
                    id: "list.view.grid",
                    keys: "v g",
                    surface: "list" as const,
                    action: () => onDisplayModeChange("grid"),
                  },
                ]
              : []),
            ...(availableDisplayModes.includes("list")
              ? [
                  {
                    id: "list.view.list",
                    keys: "v l",
                    surface: "list" as const,
                    action: () => onDisplayModeChange("list"),
                  },
                ]
              : []),
            ...(availableDisplayModes.includes("wall")
              ? [
                  {
                    id: "list.view.wall",
                    keys: "v w",
                    surface: "list" as const,
                    action: () => onDisplayModeChange("wall"),
                  },
                ]
              : []),
            ...(availableDisplayModes.includes("tagger")
              ? [
                  {
                    id: "list.view.tagger",
                    keys: "v t",
                    surface: "list" as const,
                    action: () => onDisplayModeChange("tagger"),
                  },
                ]
              : []),
            ...(availableDisplayModes.includes("graph")
              ? [
                  {
                    id: "list.view.graph",
                    keys: "v h",
                    surface: "list" as const,
                    action: () => onDisplayModeChange("graph"),
                  },
                ]
              : []),
            ...(availableDisplayModes.includes("byGroup")
              ? [
                  {
                    id: "list.view.group",
                    keys: "v b",
                    surface: "list" as const,
                    action: () => onDisplayModeChange("byGroup"),
                  },
                ]
              : []),
            ...(availableDisplayModes.includes("feed")
              ? [
                  {
                    id: "list.view.feed",
                    keys: "v f",
                    surface: "list" as const,
                    action: () => onDisplayModeChange("feed"),
                  },
                ]
              : []),
            ...(availableDisplayModes.includes("vertical")
              ? [
                  {
                    id: "list.view.vertical",
                    keys: "v k",
                    surface: "list" as const,
                    action: () => onDisplayModeChange("vertical"),
                  },
                ]
              : []),
          ]
        : []),
      // Selection
      ...(onSelectAll ? [{ id: "list.select.all", keys: "s a", surface: "list" as const, action: onSelectAll }] : []),
      ...(onSelectNone
        ? [{ id: "list.select.none", keys: "s n", surface: "list" as const, action: onSelectNone }]
        : []),
      ...(onInvertSelection
        ? [{ id: "list.select.invert", keys: "s i", surface: "list" as const, action: onInvertSelection }]
        : []),
      // Pagination
      ...(showPagingControls
        ? [
            { id: "list.page.previous", keys: "ArrowLeft", surface: "list" as const, action: () => goTo(page - 1) },
            { id: "list.page.next", keys: "ArrowRight", surface: "list" as const, action: () => goTo(page + 1) },
            {
              id: "list.page.back10",
              keys: "Shift+ArrowLeft",
              surface: "list" as const,
              action: () => goTo(page - 10),
            },
            {
              id: "list.page.forward10",
              keys: "Shift+ArrowRight",
              surface: "list" as const,
              action: () => goTo(page + 10),
            },
            { id: "list.page.first", keys: "Ctrl+Home", surface: "list" as const, action: () => goTo(1) },
            { id: "list.page.last", keys: "Ctrl+End", surface: "list" as const, action: () => goTo(totalPages) },
          ]
        : []),
      // Filter dialog
      ...(mergedCriteriaDefinitions && onObjectFilterChange
        ? [
            {
              id: "list.filters",
              keys: "",
              surface: "list" as const,
              action: () => {
                setFilterDialogPreselect(undefined);
                setFilterDialogExpressionPath(undefined);
                setFilterDialogInitialView("simple");
                setFilterDialogOpenAtRoot(true);
                setFilterDialogOpen(true);
              },
            },
          ]
        : []),
      // Zoom
      {
        id: "list.zoom.in",
        keys: "+",
        surface: "list" as const,
        action: () => setZoomLevel((v) => clampEntityCardSizeLevel(cardSizeEntityType, v + 0.25)),
      },
      {
        id: "list.zoom.out",
        keys: "-",
        surface: "list" as const,
        action: () => setZoomLevel((v) => clampEntityCardSizeLevel(cardSizeEntityType, v - 0.25)),
      },
    ],
    [
      availableDisplayModes,
      cardSizeEntityType,
      goTo,
      keybindingOverrides,
      mergedCriteriaDefinitions,
      onDisplayModeChange,
      onInvertSelection,
      onObjectFilterChange,
      onSelectAll,
      onSelectNone,
      page,
      setZoomLevel,
      showPagingControls,
      totalPages,
    ],
  );

  useKeySequence(listBindings);

  useDocumentTitle(title, manageDocumentTitle);

  useEffect(() => {
    if (
      infinitePageSize ||
      resolvedLoadState.status === "pending" ||
      resolvedLoadState.status === "error" ||
      page <= totalPages
    ) {
      return;
    }

    onFilterChange({ ...filter, page: totalPages });
  }, [filter, infinitePageSize, onFilterChange, page, resolvedLoadState.status, totalPages]);

  return (
    <div className="list-page space-y-0">
      {/* Toolbar - matches standard FilteredListToolbar. From the lg breakpoint it is three sections:
          the title section keeps a reserved minimum width so a changing item range never moves the
          controls, the controls section wraps internally, and the operations stay right-aligned.
          Between sm and lg the title takes its own row above the controls; below sm the controls
          wrapper is display: contents so the phone layout is unchanged. */}
      <div className="list-page-toolbar mx-1 mt-1 flex flex-wrap items-center gap-2 rounded-xl border border-border bg-surface/90 px-3 py-3 shadow-sm shadow-black/20 sm:px-2.5 sm:py-2 lg:flex-nowrap">
        {/* Title + count + byline */}
        <div className="list-page-title-group flex min-w-0 basis-full flex-wrap items-center gap-x-2 gap-y-0.5 pr-2 lg:flex-1 lg:basis-0 lg:min-w-[12rem]">
          <h1 className="text-sm font-semibold text-foreground whitespace-nowrap">{title}</h1>
          <span className="text-xs text-muted hidden sm:inline">
            {summaryPending
              ? "Loading…"
              : resolvedLoadState.status === "error"
                ? "Unavailable"
                : shownTotalCount > 0
                  ? `${start}-${end} of ${shownTotalCount.toLocaleString()}`
                  : "0 items"}
          </span>
          <span className="text-xs text-muted sm:hidden">
            {summaryPending
              ? "…"
              : resolvedLoadState.status === "error"
                ? "—"
                : shownTotalCount > 0
                  ? shownTotalCount.toLocaleString()
                  : "0"}
          </span>
          {!summaryPending && metadataByline}
        </div>

        {/* Controls */}
        <div className="list-page-controls flex min-w-0 flex-1 basis-0 flex-wrap items-center gap-2 max-sm:contents lg:flex-initial">
          {/* Search */}
          <ListSearchControl
            query={filter.q}
            onQueryChange={handleSearchChange}
            placeholder={searchPlaceholder}
            searchMode={searchMode}
            searchModes={searchModes}
            onSearchModeChange={onSearchModeChange}
            className={`list-page-search ${searchModes && searchModes.length > 1 ? "sm:w-[22rem]" : "sm:w-[18rem]"}`}
          />

          {/* Sort */}
          {sortedSortOptions && (
            <MultiSortControl
              filter={filter}
              onFilterChange={onFilterChange}
              options={sortedSortOptions}
              multiSortKeys={multiSortKeys}
            />
          )}

          {/* Saved filters */}
          {resolvedSavedFilterScope && (
            <SavedFilterMenu
              mode={resolvedSavedFilterScope}
              currentFilter={filter}
              currentObjectFilter={objectFilter}
              currentUIOptions={{ ...savedFilterUIOptions, displayMode, zoomLevel }}
              onApplyFilter={(nextFilter) => onFilterChange(withSeededRandomSort(filter, nextFilter))}
              onApplyObjectFilter={onObjectFilterChange}
              onApplyUIOptions={applySavedFilterUIOptions}
            />
          )}

          {/* Filter button */}
          {mergedCriteriaDefinitions && onObjectFilterChange && (
            <FilterButton
              activeCount={countActiveObjectFilters(mergedCriteriaDefinitions, editorObjectFilter)}
              onClick={() => {
                setFilterDialogPreselect(undefined);
                setFilterDialogExpressionPath(undefined);
                setFilterDialogInitialView("simple");
                setFilterDialogOpenAtRoot(true);
                setFilterDialogOpen(true);
              }}
            />
          )}

          {/* Display mode */}
          {onDisplayModeChange && availableDisplayModes && (
            <div className={`${toolbarSegmentClass} gap-0.5`}>
              {availableDisplayModes.includes("grid") && (
                <button
                  onClick={() => onDisplayModeChange("grid")}
                  className={`${toolbarIconButtonClass} ${displayMode === "grid" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                  title="Grid"
                >
                  <LayoutGrid className="w-3.5 h-3.5" />
                </button>
              )}
              {availableDisplayModes.includes("list") && (
                <button
                  onClick={() => onDisplayModeChange("list")}
                  className={`${toolbarIconButtonClass} ${displayMode === "list" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                  title="List"
                >
                  <List className="w-3.5 h-3.5" />
                </button>
              )}
              {availableDisplayModes.includes("wall") && (
                <button
                  onClick={() => onDisplayModeChange("wall")}
                  className={`${toolbarIconButtonClass} ${displayMode === "wall" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                  title="Wall"
                >
                  <Grid3X3 className="w-3.5 h-3.5" />
                </button>
              )}
              {availableDisplayModes.includes("tagger") && (
                <button
                  onClick={() => onDisplayModeChange("tagger")}
                  className={`${toolbarIconButtonClass} ${displayMode === "tagger" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                  title="Tagger"
                >
                  <Tags className="w-3.5 h-3.5" />
                </button>
              )}
              {availableDisplayModes.includes("graph") && (
                <button
                  onClick={() => onDisplayModeChange("graph")}
                  className={`${toolbarIconButtonClass} ${displayMode === "graph" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                  title="Graph/Tree"
                >
                  <Share2 className="w-3.5 h-3.5" />
                </button>
              )}
              {availableDisplayModes.includes("byGroup") && (
                <button
                  onClick={() => onDisplayModeChange("byGroup")}
                  className={`${toolbarIconButtonClass} ${displayMode === "byGroup" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                  title="By Group"
                >
                  <FolderTree className="w-3.5 h-3.5" />
                </button>
              )}
              {availableDisplayModes.includes("feed") && (
                <button
                  onClick={() => onDisplayModeChange("feed")}
                  className={`${toolbarIconButtonClass} ${displayMode === "feed" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                  title="Feed"
                >
                  <Rows3 className="w-3.5 h-3.5" />
                </button>
              )}
              {availableDisplayModes.includes("vertical") && (
                <button
                  onClick={() => onDisplayModeChange("vertical")}
                  className={`${toolbarIconButtonClass} ${displayMode === "vertical" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                  title="Vertical Viewer"
                >
                  <MonitorPlay className="w-3.5 h-3.5" />
                </button>
              )}
            </div>
          )}

          {/* Per page */}
          <div className={toolbarSegmentClass}>
            <PageSizeSelect
              perPage={perPage}
              allowInfinite={allowInfinitePageSize}
              infinitePageSize={infinitePageSize}
              infinitePageSizeOnly={infinitePageSizeOnly}
              maxPageSize={maxPageSize}
              onChange={(nextPerPage) =>
                onFilterChange({
                  ...filter,
                  perPage: maxPageSize == null ? nextPerPage : Math.min(nextPerPage, maxPageSize),
                  page: 1,
                })
              }
            />

            {/* Zoom slider (standard card size slider) */}
            {(displayMode === "grid" || displayMode === "list") && (
              <div className="hidden items-center gap-1 pl-1 md:flex">
                <ZoomOut className="w-3 h-3 text-muted" />
                <input
                  type="range"
                  min={0}
                  max={cardSizeMaxLevel}
                  step={0.25}
                  value={zoomLevel}
                  onChange={(e) => setZoomLevel(clampEntityCardSizeLevel(cardSizeEntityType, Number(e.target.value)))}
                  style={
                    { "--range-fill": `${(zoomLevel / Math.max(0.25, cardSizeMaxLevel)) * 100}%` } as CSSProperties
                  }
                  className="themed-range-input h-1 w-16 cursor-pointer sm:w-20"
                  title={`Card size: ${getEntityCardMinWidthPx(cardSizeEntityType, zoomLevel)}px`}
                />
                <ZoomIn className="w-3 h-3 text-muted" />
              </div>
            )}

            {displayMode === "wall" && wallColumnCount != null && onWallColumnCountChange && (
              <WallSizeControl
                sizeLevel={getWallSizeLevelFromColumnCount(wallColumnCount)}
                onChange={(sizeLevel) => onWallColumnCountChange(getWallColumnCountFromSizeLevel(sizeLevel))}
              />
            )}
          </div>
        </div>

        {/* Operations */}
        <div className="list-page-operations ml-auto flex flex-wrap items-center justify-end gap-2 sm:ml-0 lg:flex-1 lg:basis-0 lg:min-w-fit">
          {vrListSource && onNavigate ? <ViewInVrButton source={vrListSource} onNavigate={onNavigate} /> : null}
          {renderOperations?.()}
          <ExtensionSlot slot="list-page-toolbar-end" context={slotContext} />
          {pageKey && <ExtensionSlot slot={`${pageKey}-list-toolbar-end`} context={slotContext} />}
          {onNew && (
            <button
              onClick={onNew}
              className="inline-flex min-h-10 items-center rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white hover:bg-accent-hover sm:min-h-0 sm:py-1 sm:text-xs"
            >
              + New
            </button>
          )}
        </div>
      </div>

      {showInfiniteAutoScrollControls && (
        <div className="pointer-events-none fixed right-3 top-1/2 z-[90] -translate-y-1/2 sm:right-5 sm:top-[24%]">
          <div
            className="pointer-events-auto relative flex min-h-36 w-12 items-center justify-end"
            onPointerEnter={wakeAutoScrollControls}
            onPointerMove={wakeAutoScrollControls}
            onFocusCapture={wakeAutoScrollControls}
          >
            {!autoScrollControlsAwake && (
              <div className="absolute right-0 h-12 w-1.5 rounded-l-full bg-accent/70 shadow-lg" aria-hidden="true" />
            )}
            <div
              className={`flex flex-col items-center gap-2 rounded-xl border border-border bg-card/95 px-2 py-2 shadow-2xl backdrop-blur transition-all duration-300 ${autoScrollControlsAwake ? "translate-x-0 opacity-100" : "pointer-events-none translate-x-2 opacity-0"}`}
            >
              <button
                type="button"
                onClick={() => {
                  wakeAutoScrollControls();
                  setAutoScrollEnabled((current) => !current);
                }}
                className={`${toolbarIconButtonClass} ${autoScrollEnabled ? "bg-background/60 text-accent shadow-sm" : ""}`}
                aria-label={autoScrollEnabled ? "Pause auto-scroll" : "Start auto-scroll"}
                title={autoScrollEnabled ? "Pause auto-scroll" : "Start auto-scroll"}
              >
                {autoScrollEnabled ? <Pause className="h-4 w-4" /> : <Play className="h-4 w-4" />}
              </button>
              <input
                type="range"
                min={10}
                max={360}
                step={10}
                value={autoScrollSpeed}
                onChange={(event) => {
                  wakeAutoScrollControls();
                  setAutoScrollSpeed(Number(event.target.value));
                }}
                className="h-24 w-1 accent-accent [writing-mode:vertical-lr]"
                aria-label="Floating auto-scroll speed"
                title={`Auto-scroll speed: ${autoScrollSpeed}px/s`}
              />
              <span className="text-[10px] text-muted tabular-nums [writing-mode:vertical-lr]">
                {autoScrollSpeed}px/s
              </span>
            </div>
          </div>
        </div>
      )}

      {/* Active filter tags (criterion badges) */}
      {objectFilter &&
        onObjectFilterChange &&
        mergedCriteriaDefinitions &&
        Object.keys(editorObjectFilter).length > 0 && (
          <ActiveObjectFilterChips
            criteriaDefinitions={mergedCriteriaDefinitions}
            objectFilter={editorObjectFilter}
            customFilterSections={mergedCustomFilterSections}
            onEdit={(target) => {
              const key = getFilterChipTargetKey(target);
              setFilterDialogExpressionPath(target.kind === "expression" ? target.path : undefined);
              const criterion = mergedCriteriaDefinitions.find(
                (item) =>
                  item.id === key ||
                  item.filterKey === key ||
                  item.secondaryFilterKey === key ||
                  item.auxiliaryToggleKey === key,
              );
              const customSection =
                target.kind === "root"
                  ? mergedCustomFilterSections?.find((section) => section.filterKey === key)
                  : undefined;
              setFilterDialogPreselect(
                target.kind === "expression"
                  ? target.criterionId
                    ? {
                        criterionId: target.criterionId,
                        relatedFacet: target.relatedFacet ?? (target.nestedCriterionId ? "criterion" : "mode"),
                        nestedCriterionId: target.nestedCriterionId,
                      }
                    : undefined
                  : target.kind === "related"
                    ? {
                        criterionId: criterion?.id ?? key,
                        relatedFacet: target.facet,
                        nestedCriterionId: target.nestedCriterionId,
                      }
                    : (customSection?.id ?? criterion?.id ?? key),
              );
              setFilterDialogInitialView(
                key === "_filterExpression" && target.kind !== "expression" ? "advanced" : "simple",
              );
              setFilterDialogOpenAtRoot(false);
              setFilterDialogOpen(true);
            }}
            onRemove={(target) => {
              const key = getFilterChipTargetKey(target);
              if (pageKey) {
                trackInteraction({
                  hostType: "collection",
                  kind: "filterClear",
                  meta: { pageKey, source: "filterChip", criteriaKeys: [key] },
                });
              }
              const next = removeObjectFilterChipTarget(editorObjectFilter, mergedCriteriaDefinitions, target);
              onObjectFilterChange(collapseExtensionCriteria(next, objectFilter));
              onFilterChange({ ...filter, page: 1 });
            }}
            onClearAll={
              showClearAllObjectFilters
                ? () => {
                    if (pageKey) {
                      trackInteraction({
                        hostType: "collection",
                        kind: "filterClear",
                        meta: { pageKey, source: "filterChip", clearedAll: true },
                      });
                    }
                    onObjectFilterChange({});
                    onFilterChange({ ...filter, page: 1 });
                  }
                : undefined
            }
          />
        )}

      {/* A full-width `<pageKey>-list-row` slot below the toolbar — separate from the toolbar-end slots, which
          sit in the right-aligned operations group and cannot host a row. Empty (no extension) renders nothing. */}
      {pageKey && <ExtensionSlot slot={`${pageKey}-list-row`} context={slotContext} />}

      {/* Selection bar */}
      {showSelectionBar && (
        <div className="grid grid-cols-1 items-center gap-2 bg-card/80 border border-border rounded-lg px-3 py-1.5 mx-1 mt-1 sm:grid-cols-[1fr_auto_1fr]">
          <div className="flex min-w-0 flex-wrap items-center gap-x-2 text-xs text-secondary">
            <span>{selectedIds!.size} selected</span>
            {selectionMetadata}
          </div>
          <div className="flex flex-wrap items-center justify-center gap-3">
            {onSelectAll && (
              <button
                onClick={onSelectAll}
                disabled={selectAllPending}
                className="text-xs text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-60"
              >
                {selectAllPending ? "Selecting..." : selectAllLabel}
              </button>
            )}
            {onSelectAllMatching && (
              <button
                onClick={onSelectAllMatching}
                disabled={selectAllMatchingPending}
                className="text-xs text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-60"
              >
                {selectAllMatchingPending ? "Selecting..." : selectAllMatchingLabel}
              </button>
            )}
            {onInvertSelection && (
              <button onClick={onInvertSelection} className="text-xs text-secondary hover:text-foreground">
                Invert
              </button>
            )}
            {onSelectNone && (
              <button onClick={onSelectNone} className="text-xs text-secondary hover:text-foreground">
                Deselect all
              </button>
            )}
            {selectionActions}
          </div>
          <div className="hidden sm:block" aria-hidden="true" />
        </div>
      )}

      {/* Top pager: rendered outside the load gate so it survives page changes */}
      {showPagingControls && resolvedLoadState.status !== "error" && totalPages > 1 && (
        <div className="mx-1 mt-1 flex flex-wrap items-center justify-center gap-1 py-1">
          <PaginationControls page={page} totalPages={totalPages} goTo={goTo} />
        </div>
      )}

      {/* Results */}
      <QueryState
        state={resultsState}
        loading={
          <div role="status" aria-label={`Loading ${title}`} className="flex h-64 items-center justify-center">
            <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
          </div>
        }
        errorTitle={`Could not load ${title}`}
        errorClassName="mx-1 mt-3"
      >
        <>
          <ListPageCardSizeContext.Provider value={{ cardMinWidthPx, zoomLevel }}>
            <div
              aria-busy={reloading || undefined}
              className="list-page-content pt-3"
              style={{ "--card-min-width": `${cardMinWidthPx}px` } as React.CSSProperties}
            >
              {reloading && <span role="status" aria-label={`Loading ${title} page ${page}`} className="sr-only" />}
              {resultsChildren}
              {infinitePageSize && infiniteScroll && !contentOwnsInfiniteLoading && (
                <InfiniteScrollSentinel
                  hasMore={Boolean(infiniteScroll.hasNextPage)}
                  isLoading={Boolean(infiniteScroll.isFetchingNextPage)}
                  onLoadMore={infiniteScroll.onLoadMore}
                  loadedCount={infiniteScroll.loadedCount}
                  totalCount={infiniteScroll.totalCount}
                />
              )}
            </div>
          </ListPageCardSizeContext.Provider>
          {showPagingControls && totalPages > 1 && (
            <div className="flex flex-wrap items-center justify-center gap-1 py-4">
              <PaginationControls page={page} totalPages={totalPages} goTo={goTo} />
            </div>
          )}
        </>
      </QueryState>

      {/* Filter Dialog */}
      {mergedCriteriaDefinitions && onObjectFilterChange && (
        <FilterDialog
          open={filterDialogOpen}
          onClose={() => {
            setFilterDialogOpen(false);
            setFilterDialogPreselect(undefined);
            setFilterDialogInitialView("simple");
            setFilterDialogExpressionPath(undefined);
            setFilterDialogOpenAtRoot(false);
          }}
          criteria={mergedCriteriaDefinitions}
          activeFilter={editorObjectFilter}
          customSections={mergedCustomFilterSections}
          supportsFilterExpressions={supportsFilterExpressions}
          subjectLabel={title.toLowerCase()}
          onApply={(f) => {
            if (pageKey) {
              trackInteraction({
                hostType: "collection",
                kind: Object.keys(f).length === 0 ? "filterClear" : "filterApply",
                meta: {
                  pageKey,
                  source: "filterDialog",
                  criteriaKeys: Object.keys(f),
                },
              });
            }
            onObjectFilterChange(collapseExtensionCriteria(f, objectFilter));
            onFilterChange({ ...filter, page: 1 });
          }}
          preselectCriterion={filterDialogPreselect}
          initialView={filterDialogInitialView}
          initialExpressionPath={filterDialogExpressionPath}
          openAtRoot={filterDialogOpenAtRoot}
        />
      )}
    </div>
  );
}
