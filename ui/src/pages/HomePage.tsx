import {
  useState,
  useRef,
  useEffect,
  useLayoutEffect,
  useCallback,
  useMemo,
  useId,
  createContext,
  useContext,
  Children,
  isValidElement,
} from "react";
import type { CSSProperties } from "react";
import { useInfiniteQuery, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  videos,
  performers,
  studios,
  tags,
  galleries,
  groups,
  audios,
  texts,
  faces,
  segmentLibrary,
  segmentSpans,
  savedFilters,
  dashboards,
} from "../api/client";
import type {
  AffinityHostType,
  EntityEngagement,
  Group,
  SavedFilter,
  FindFilter,
  Dashboard,
  DashboardSummary,
  DashboardWidget,
  DashboardWidgetLayout,
  DashboardWidgetPresentation,
  DashboardWidgetWidth,
  ExtensionDashboardWidgetContribution,
  GroupItem,
  SegmentSpanSearchRequest,
} from "../api/types";
import {
  ChevronLeft,
  ChevronRight,
  Settings2,
  Plus,
  Trash2,
  GripVertical,
  Copy,
  Home,
  AlertTriangle,
  X,
  Check,
  RotateCcw,
  ZoomIn,
  ZoomOut,
} from "lucide-react";
import { createRouteLinkProps } from "../components/cardNavigation";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { useVideoQueueNavigation } from "../hooks/useVideoQueueNavigation";
import { readAuthenticatedUserHomePageContent } from "../utils/userUiPreferences";
import { withSeededRandomSort } from "../utils/seededRandomSort";
import {
  DashboardSkeleton,
  RECOMMENDATION_CARD_SHAPES,
  RecommendationCardSkeleton,
  RecommendationRowSkeleton,
  recommendationCardSkeletons,
  clampDashboardCardSize,
  clampDashboardPages,
  clampDashboardRows,
  dashboardCardWidthPx,
  dashboardCardWidthStyle,
  dashboardPagedTrackStyle,
  fitDashboardCards,
  DEFAULT_DASHBOARD_CARD_SIZE,
  MAX_DASHBOARD_CARD_SIZE,
  MAX_DASHBOARD_PAGES,
  MAX_DASHBOARD_ROWS,
  MIN_DASHBOARD_CARD_SIZE,
  MIN_DASHBOARD_PAGES,
  MIN_DASHBOARD_ROWS,
  type RecommendationCardShape,
} from "../components/DashboardSkeleton";
import { SortableList } from "../components/SortableList";
import { useExtensions } from "../extensions/ExtensionLoader";
import {
  AudioTile,
  FaceTile,
  GalleryTile,
  GroupTile,
  ImageTile,
  PerformerTile,
  SegmentTile,
  StudioTile,
  TagTile,
  TextTile,
  VideoCard,
} from "../components/EntityCards";
import { groupFeedItemKey, useGroupFeedEntities, type GroupFeedEntity } from "../components/GroupItemFeed";
import { useAuth } from "../auth/AuthContext";
import { canReadEntity } from "../auth/visibility";
import { resolveGroupFeedHost } from "../components/GroupItemFeed";
import { canAccessExtensionContribution } from "../extensions/extension-permissions";
import { ExtensionErrorBoundary } from "../components/ExtensionErrorBoundary";
import { emitLocationChange, historyStateForNewEntry, registerNavigationBlocker, type Route } from "../router/location";
import { buildSpanSearchRequest } from "./segments/useDerivedSpansQuery";
import { buildRawSegmentListOptions } from "./segments/useRawSegmentsQuery";
import { createDefaultRawSegmentFilter, readRawSegmentListFilter } from "./segments/rawSegmentFilter";
import { buildSpanTitle, buildRawSegmentTitle, formatSegmentCardEyebrow } from "./segments/segmentDisplayUtils";
import type { DerivedSpanItem, RawSegmentItem } from "./segments/types";
import {
  buildAppliedDerivedQuery,
  buildDerivedQueryDescriptor,
  readDerivedSpanQueryFilter,
} from "./segments/derivedQueryCriterion";
import { readMultiIdCriterionDepth, readMultiIdCriterionIds } from "./segments/segmentCriteriaDefinitions";
import { isApiNotFoundError } from "../utils/queryLoadState";
import {
  dashboardPageQueryKey,
  dashboardPrincipalKey,
  loadDashboardPage,
  savedFilterQueryKey,
} from "./dashboardPageQuery";

// ─── Types ───────────────────────────────────────────────────────────────────

type FilterMode =
  | "videos"
  | "performers"
  | "studios"
  | "tags"
  | "galleries"
  | "groups"
  | "audios"
  | "texts"
  | "segments"
  | "rawsegments";

interface CustomFilter {
  type: "custom";
  mode: FilterMode;
  sortBy: string;
  direction: "asc" | "desc";
  header: string;
}

interface SavedFilterRow {
  type: "saved";
  savedFilterId: number;
}

interface GroupRow {
  type: "group";
  groupId: number;
}

type FrontPageContent = CustomFilter | SavedFilterRow | GroupRow;

const CONTINUE_WATCHING_SOURCE_KEY = "continue-watching";

// Built-in dynamic groups are installation-wide singletons seeded before the API accepts requests,
// so their id can be resolved once and then referenced like any other group.
async function resolveBuiltInGroup(sourceKey: string): Promise<Group | null> {
  try {
    const matches = await groups.findFiltered({
      findFilter: { page: 1, perPage: 1 },
      // Users can create their own groups on a built-in source; only the system group is meant here.
      objectFilter: {
        querySourceKeyCriterion: { value: sourceKey, modifier: "EQUALS" },
        isBuiltInCriterion: { value: true },
      },
    });
    return matches.items[0] ?? null;
  } catch {
    // A principal that cannot read groups gets no row, as it would see nothing in the group anyway.
    return null;
  }
}

const DEFAULT_SORT_BY_MODE: Record<FilterMode, string> = {
  videos: "date",
  performers: "latest_video_date",
  studios: "latest_video_date",
  tags: "latest_video_date",
  galleries: "date",
  groups: "date",
  audios: "date",
  texts: "date",
  segments: "updated_at",
  rawsegments: "updated_at",
};

function normalizeFilterMode(mode: string | undefined): FilterMode | null {
  const normalized = mode?.toLowerCase();
  if (
    normalized === "videos" ||
    normalized === "performers" ||
    normalized === "studios" ||
    normalized === "tags" ||
    normalized === "galleries" ||
    normalized === "groups" ||
    normalized === "audios" ||
    normalized === "texts" ||
    normalized === "segments" ||
    normalized === "rawsegments"
  ) {
    return normalized;
  }
  return null;
}

function parseJsonObject<T extends object>(json: string | undefined): T | undefined {
  if (!json) return undefined;
  try {
    const parsed = JSON.parse(json);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as T) : undefined;
  } catch {
    return undefined;
  }
}

// ─── Default content (matches standard defaults) ───────────────────────

const DEFAULT_COLLECTION_CONTENT: FrontPageContent[] = [
  { type: "custom", mode: "videos", sortBy: "date", direction: "desc", header: "Recently Released Videos" },
  { type: "custom", mode: "studios", sortBy: "created_at", direction: "desc", header: "Recently Added Studios" },
  { type: "custom", mode: "groups", sortBy: "date", direction: "desc", header: "Recently Released Groups" },
  { type: "custom", mode: "performers", sortBy: "created_at", direction: "desc", header: "Recently Added Performers" },
  { type: "custom", mode: "galleries", sortBy: "date", direction: "desc", header: "Recently Released Galleries" },
];

// ─── Premade filter options (for adding new rows) ────────────────────────────

const PREMADE_FILTERS: CustomFilter[] = [
  { type: "custom", mode: "videos", sortBy: "date", direction: "desc", header: "Recently Released Videos" },
  { type: "custom", mode: "videos", sortBy: "created_at", direction: "desc", header: "Recently Added Videos" },
  { type: "custom", mode: "galleries", sortBy: "date", direction: "desc", header: "Recently Released Galleries" },
  { type: "custom", mode: "galleries", sortBy: "created_at", direction: "desc", header: "Recently Added Galleries" },
  { type: "custom", mode: "groups", sortBy: "date", direction: "desc", header: "Recently Released Groups" },
  { type: "custom", mode: "groups", sortBy: "created_at", direction: "desc", header: "Recently Added Groups" },
  { type: "custom", mode: "studios", sortBy: "created_at", direction: "desc", header: "Recently Added Studios" },
  { type: "custom", mode: "performers", sortBy: "created_at", direction: "desc", header: "Recently Added Performers" },
];

const STORAGE_KEY = "cove-front-page-content";
// One-time flag so we add the Continue Watching row to pre-existing layouts exactly once.
// After this, the user is free to remove it and it won't be re-added.
const CONTINUE_WATCHING_MIGRATION_KEY = "cove-front-page-continue-watching-migrated";
const FLOW_PRESENTATION: DashboardWidgetPresentation = "flow";

// True when the stored layout needs the Continue Watching group id to be converted, so the lookup
// is only paid for by layouts that predate the dynamic group widget.
function storedContentNeedsContinueWatching(): boolean {
  try {
    const stored = readAuthenticatedUserHomePageContent() ?? localStorage.getItem(STORAGE_KEY);
    if (!stored) return true;
    if (localStorage.getItem(CONTINUE_WATCHING_MIGRATION_KEY) !== "true") return true;
    return (JSON.parse(stored) as Array<{ type?: string }>).some((item) => item.type === "continueWatching");
  } catch {
    // An unreadable layout falls back to the defaults, which include the row.
    return true;
  }
}

function loadContent(continueWatchingGroupId: number | null): FrontPageContent[] {
  const continueWatchingRow: GroupRow[] =
    continueWatchingGroupId === null ? [] : [{ type: "group", groupId: continueWatchingGroupId }];
  try {
    // Prefer the user's account-stored layout (follows them across browsers); fall back to the
    // browser-local value for signed-out use and as a one-time migration source.
    const stored = readAuthenticatedUserHomePageContent() ?? localStorage.getItem(STORAGE_KEY);
    if (stored) {
      // Legacy layouts stored Continue Watching as its own row type; it is now a dynamic group row.
      // The group is dropped when it cannot be resolved, which is what an unreadable group renders.
      const parsed = JSON.parse(stored) as Array<FrontPageContent | { type: "continueWatching" }>;
      const content = parsed.flatMap<FrontPageContent>((item) =>
        item.type === "continueWatching" ? continueWatchingRow : [item],
      );
      // Migrate layouts saved before Continue Watching became a customizable row: it used to be
      // hardcoded at the top, so preserve that behavior by inserting it once.
      const migrated = localStorage.getItem(CONTINUE_WATCHING_MIGRATION_KEY) === "true";
      if (!migrated) {
        localStorage.setItem(CONTINUE_WATCHING_MIGRATION_KEY, "true");
        if (!content.some((item) => item.type === "group" && item.groupId === continueWatchingGroupId)) {
          return [...continueWatchingRow, ...content];
        }
      }
      return content;
    }
  } catch {
    /* ignore */
  }
  return [...continueWatchingRow, ...DEFAULT_COLLECTION_CONTENT];
}

function createInstanceId() {
  return globalThis.crypto?.randomUUID?.() ?? `widget-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function getWidgetPresentation(widget: DashboardWidget): DashboardWidgetPresentation {
  return widget.presentation === "canvas" ? "canvas" : FLOW_PRESENTATION;
}

function getSupportedPresentations(definition?: ExtensionDashboardWidgetContribution): DashboardWidgetPresentation[] {
  const declared = definition?.supportedPresentations?.filter(
    (presentation, index, values): presentation is DashboardWidgetPresentation =>
      (presentation === "flow" || presentation === "canvas") && values.indexOf(presentation) === index,
  );
  return declared?.length ? declared : [FLOW_PRESENTATION];
}

function getDefaultPresentation(definition: ExtensionDashboardWidgetContribution): DashboardWidgetPresentation {
  const supported = getSupportedPresentations(definition);
  return definition.defaultPresentation && supported.includes(definition.defaultPresentation)
    ? definition.defaultPresentation
    : supported[0];
}

function definitionSupportsPresentation(
  definition: ExtensionDashboardWidgetContribution | undefined,
  presentation: DashboardWidgetPresentation,
) {
  return !!definition && getSupportedPresentations(definition).includes(presentation);
}

// ─── Widget Layout ──────────────────────────────────────────────────────────

const DASHBOARD_WIDGET_WIDTHS: DashboardWidgetWidth[] = [1, 2, 3, 4, 5];
const DASHBOARD_WIDGET_WIDTH_LABELS: Record<DashboardWidgetWidth, string> = {
  1: "Full",
  2: "Half",
  3: "Third",
  4: "Quarter",
  5: "Fifth",
};
/** The dashboard grid has this many columns so every supported width divides it evenly. */
const DASHBOARD_GRID_COLUMNS = 60;
/** A row fetches this many items per visible row of cards. */
const DASHBOARD_ROW_PAGE_SIZE = 25;
const MAX_DASHBOARD_ROW_PAGE_SIZE = 500;

interface ResolvedWidgetLayout {
  width: DashboardWidgetWidth;
  rows: number;
  cardSize: number;
  /** A cap on the pages shown, or undefined to show every loaded item. */
  pages: number | undefined;
  /**
   * Card-grid widgets that share a dashboard row, card size, and row count line their card rows up;
   * this names that group. Unset when the widget has nothing to line up with.
   */
  alignmentGroup?: string;
}

const DEFAULT_WIDGET_LAYOUT: ResolvedWidgetLayout = {
  width: 1,
  rows: MIN_DASHBOARD_ROWS,
  cardSize: DEFAULT_DASHBOARD_CARD_SIZE,
  pages: undefined,
};

function isDashboardWidgetWidth(value: unknown): value is DashboardWidgetWidth {
  return typeof value === "number" && (DASHBOARD_WIDGET_WIDTHS as number[]).includes(value);
}

function getWidgetLayout(widget: DashboardWidget): ResolvedWidgetLayout {
  const layout = widget.layout;
  return {
    width: getWidgetPresentation(widget) === "flow" && isDashboardWidgetWidth(layout?.width) ? layout.width : 1,
    rows: clampDashboardRows(layout?.rows),
    cardSize: clampDashboardCardSize(layout?.cardSize),
    pages: clampDashboardPages(layout?.pages),
  };
}

/** Card-grid widgets are the core collection rows; only they take rows and card size. */
function isCardGridWidget(widget: DashboardWidget) {
  return widgetToContent(widget) !== null;
}

/** How many items a row fetches per API page; the row keeps fetching pages as the user pages through it. */
function dashboardRowPageSize({ rows }: Pick<ResolvedWidgetLayout, "rows">) {
  return Math.min(MAX_DASHBOARD_ROW_PAGE_SIZE, DASHBOARD_ROW_PAGE_SIZE * clampDashboardRows(rows));
}

interface DashboardRowPage<TItem> {
  items: TItem[];
  /** A negative total means the source stopped counting; `hasMore` then says whether to keep paging. */
  totalCount?: number;
  hasMore?: boolean;
}

/**
 * Pages through a row's source so "all pages" really means every matching item: the shell asks for
 * the next API page as the user nears the end of what is loaded.
 */
function useDashboardRowPages<TItem>({
  queryKey,
  enabled = true,
  fetchPage,
}: {
  queryKey: unknown[];
  enabled?: boolean;
  fetchPage: (page: number) => Promise<DashboardRowPage<TItem>>;
}) {
  const query = useInfiniteQuery({
    queryKey,
    enabled,
    initialPageParam: 1,
    queryFn: ({ pageParam }) => fetchPage(pageParam),
    getNextPageParam: (last: DashboardRowPage<TItem>, all: DashboardRowPage<TItem>[]) => {
      if (last.items.length === 0) return undefined;
      if (typeof last.totalCount !== "number" || last.totalCount < 0) return last.hasMore ? all.length + 1 : undefined;
      const loaded = all.reduce((count, page) => count + page.items.length, 0);
      return loaded < last.totalCount ? all.length + 1 : undefined;
    },
  });
  const items = useMemo(() => query.data?.pages.flatMap((page) => page.items) ?? [], [query.data]);
  const { fetchNextPage } = query;
  const loadMore = useCallback(() => {
    void fetchNextPage();
  }, [fetchNextPage]);
  return {
    items,
    firstPage: query.data?.pages[0],
    totalCount: query.data?.pages[0]?.totalCount,
    isLoading: query.isLoading,
    isSuccess: query.isSuccess,
    isError: query.isError,
    error: query.error,
    refetch: query.refetch,
    hasMore: query.hasNextPage,
    loadMore,
    loadingMore: query.isFetchingNextPage,
  };
}

function widgetSpanStyle(layout: ResolvedWidgetLayout) {
  return { "--dashboard-widget-span": DASHBOARD_GRID_COLUMNS / layout.width } as CSSProperties;
}

const DashboardWidgetLayoutContext = createContext<ResolvedWidgetLayout>(DEFAULT_WIDGET_LAYOUT);

function useDashboardWidgetLayout() {
  return useContext(DashboardWidgetLayoutContext);
}

// ─── Row Alignment ──────────────────────────────────────────────────────────

/**
 * Card-grid widgets side by side on wide screens report their natural card-row heights here, and
 * every member of a group gets the row-by-row maximum back, so their rows line up the way rows
 * already do inside a single widget.
 */
interface DashboardRowAlignment {
  report: (id: string, group: string, heights: number[]) => void;
  remove: (id: string) => void;
  heightsFor: (group: string) => number[] | undefined;
}

const DashboardRowAlignmentContext = createContext<DashboardRowAlignment | null>(null);

/** Widgets only sit side by side from the md breakpoint up (see `.dashboard-widget-grid`). */
const SIDE_BY_SIDE_QUERY = "(min-width: 48rem)";

function sameHeights(a: number[], b: number[]) {
  return a.length === b.length && a.every((value, index) => value === b[index]);
}

function DashboardRowAlignmentProvider({ children }: { children: React.ReactNode }) {
  const [members, setMembers] = useState<Map<string, { group: string; heights: number[] }>>(() => new Map());
  const [sideBySide, setSideBySide] = useState(
    () =>
      typeof window !== "undefined" &&
      typeof window.matchMedia === "function" &&
      window.matchMedia(SIDE_BY_SIDE_QUERY).matches,
  );
  useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    const query = window.matchMedia(SIDE_BY_SIDE_QUERY);
    const update = () => setSideBySide(query.matches);
    update();
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);

  // Stable callbacks: widgets subscribe to these in effects, so they must not change when heights do.
  const report = useCallback(
    (id: string, group: string, heights: number[]) =>
      setMembers((current) => {
        const existing = current.get(id);
        if (existing && existing.group === group && sameHeights(existing.heights, heights)) return current;
        const next = new Map(current);
        next.set(id, { group, heights });
        return next;
      }),
    [],
  );
  const remove = useCallback(
    (id: string) =>
      setMembers((current) => {
        if (!current.has(id)) return current;
        const next = new Map(current);
        next.delete(id);
        return next;
      }),
    [],
  );
  const value = useMemo<DashboardRowAlignment>(
    () => ({
      report,
      remove,
      heightsFor: (group) => {
        if (!sideBySide) return undefined;
        const group_members = [...members.values()].filter((member) => member.group === group);
        if (group_members.length < 2) return undefined;
        const rows = Math.max(...group_members.map((member) => member.heights.length));
        return Array.from({ length: rows }, (_, row) =>
          Math.max(...group_members.map((member) => member.heights[row] ?? 0)),
        );
      },
    }),
    [members, remove, report, sideBySide],
  );

  return <DashboardRowAlignmentContext.Provider value={value}>{children}</DashboardRowAlignmentContext.Provider>;
}

/**
 * Which card-grid widgets share a dashboard row, following the grid's auto-placement: widgets fill a
 * row left to right and wrap when the next one does not fit.
 */
function dashboardAlignmentGroups(widgets: DashboardWidget[]) {
  const groups = new Map<string, string>();
  let row = 0;
  let column = 0;
  for (const widget of widgets) {
    const layout = getWidgetLayout(widget);
    const span = DASHBOARD_GRID_COLUMNS / layout.width;
    if (column + span > DASHBOARD_GRID_COLUMNS) {
      row += 1;
      column = 0;
    }
    if (isCardGridWidget(widget)) groups.set(widget.instanceId, `${row}:${layout.cardSize}:${layout.rows}`);
    column += span;
    if (column >= DASHBOARD_GRID_COLUMNS) {
      row += 1;
      column = 0;
    }
  }
  return groups;
}

function cloneJsonConfiguration(value: unknown): unknown {
  const ancestors = new WeakSet<object>();
  const validate = (candidate: unknown): boolean => {
    if (candidate === null || typeof candidate === "string" || typeof candidate === "boolean") return true;
    if (typeof candidate === "number") return Number.isFinite(candidate);
    if (typeof candidate !== "object") return false;
    if (ancestors.has(candidate)) return false;
    ancestors.add(candidate);
    const valid = Array.isArray(candidate)
      ? candidate.every(validate)
      : (Object.getPrototypeOf(candidate) === Object.prototype || Object.getPrototypeOf(candidate) === null) &&
        Object.values(candidate as Record<string, unknown>).every(validate);
    ancestors.delete(candidate);
    return valid;
  };

  if (!validate(value)) throw new Error("Widget configuration must be valid JSON data.");
  return JSON.parse(JSON.stringify(value));
}

// Widget labels are capped by the dashboard API; keep the entity id visible when a long name is cut.
function entityWidgetLabel(name: string | undefined, id: number, fallbackPrefix: string) {
  const normalizedName = name?.trim();
  if (!normalizedName) return `${fallbackPrefix} #${id}`;
  const identifyingSuffix = `… #${id}`;
  return normalizedName.length > 200
    ? `${normalizedName.slice(0, 200 - identifyingSuffix.length).trimEnd()}${identifyingSuffix}`
    : normalizedName;
}

function contentToWidget(content: FrontPageContent, sourceName?: string): DashboardWidget {
  if (content.type === "saved") {
    return {
      instanceId: createInstanceId(),
      owner: "cove.core",
      widgetKey: "collection",
      label: entityWidgetLabel(sourceName, content.savedFilterId, "Saved filter"),
      configuration: { source: "saved", savedFilterId: content.savedFilterId },
      presentation: FLOW_PRESENTATION,
    };
  }
  if (content.type === "group") {
    return {
      instanceId: createInstanceId(),
      owner: "cove.core",
      widgetKey: "collection",
      label: entityWidgetLabel(sourceName, content.groupId, "Group"),
      configuration: { source: "group", groupId: content.groupId },
      presentation: FLOW_PRESENTATION,
    };
  }
  return {
    instanceId: createInstanceId(),
    owner: "cove.core",
    widgetKey: "collection",
    label: content.header,
    configuration: {
      source: "premade",
      mode: content.mode,
      sortBy: content.sortBy,
      direction: content.direction,
      header: content.header,
    },
    presentation: FLOW_PRESENTATION,
  };
}

function widgetToContent(widget: DashboardWidget): FrontPageContent | null {
  if (widget.owner !== "cove.core") return null;
  if (widget.widgetKey !== "collection" || !widget.configuration || typeof widget.configuration !== "object")
    return null;
  const config = widget.configuration as Record<string, unknown>;
  if (config.source === "saved" && typeof config.savedFilterId === "number") {
    return { type: "saved", savedFilterId: config.savedFilterId };
  }
  if (config.source === "group" && typeof config.groupId === "number") {
    return { type: "group", groupId: config.groupId };
  }
  const mode = normalizeFilterMode(typeof config.mode === "string" ? config.mode : undefined);
  if (!mode || typeof config.sortBy !== "string" || (config.direction !== "asc" && config.direction !== "desc"))
    return null;
  return {
    type: "custom",
    mode,
    sortBy: config.sortBy,
    direction: config.direction,
    header: typeof config.header === "string" ? config.header : widget.label,
  };
}

function cardShapeForMode(mode: FilterMode): RecommendationCardShape {
  switch (mode) {
    case "performers":
      return RECOMMENDATION_CARD_SHAPES.performer;
    case "studios":
      return RECOMMENDATION_CARD_SHAPES.studio;
    case "tags":
      return RECOMMENDATION_CARD_SHAPES.tag;
    case "galleries":
      return RECOMMENDATION_CARD_SHAPES.gallery;
    case "groups":
      return RECOMMENDATION_CARD_SHAPES.group;
    case "audios":
      return RECOMMENDATION_CARD_SHAPES.audio;
    case "texts":
      return RECOMMENDATION_CARD_SHAPES.text;
    case "segments":
    case "rawsegments":
      return RECOMMENDATION_CARD_SHAPES.segment;
    default:
      return RECOMMENDATION_CARD_SHAPES.video;
  }
}

/**
 * The layout an account without a dashboard is bootstrapped from: the home-page rows it stored before
 * dashboards existed, with Continue Watching resolved only when that layout uses it.
 */
export async function buildLegacyDashboardWidgets(): Promise<DashboardWidget[]> {
  const continueWatching = storedContentNeedsContinueWatching()
    ? await resolveBuiltInGroup(CONTINUE_WATCHING_SOURCE_KEY)
    : null;
  return loadContent(continueWatching?.id ?? null).map((content) =>
    contentToWidget(
      content,
      content.type === "group" && content.groupId === continueWatching?.id ? continueWatching.name : undefined,
    ),
  );
}

// ─── Home Page Component ─────────────────────────────────────────────────────

interface Props {
  onNavigate: (r: any) => void;
  dashboardId?: number;
}

export function HomePage({ onNavigate, dashboardId }: Props) {
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const [editingDashboard, setEditingDashboard] = useState<{ id: number; selectName: boolean } | null>(null);
  const principalKey = dashboardPrincipalKey(user);
  const dashboardQuery = useQuery({
    queryKey: dashboardPageQueryKey(principalKey, dashboardId),
    queryFn: () => loadDashboardPage(queryClient, principalKey, dashboardId, buildLegacyDashboardWidgets),
  });

  useEffect(() => {
    if (dashboardQuery.data?.missingRequested) onNavigate({ page: "home" });
  }, [dashboardQuery.data?.missingRequested, onNavigate]);

  const [prevPrincipalKey, setPrevPrincipalKey] = useState(principalKey);
  if (principalKey !== prevPrincipalKey) {
    setPrevPrincipalKey(principalKey);
    setEditingDashboard(null);
  }

  // Leave the editor when a different dashboard loads than the one being edited.
  const loadedDashboardId = dashboardQuery.data?.dashboard.id;
  const [prevLoadedDashboardId, setPrevLoadedDashboardId] = useState(loadedDashboardId);
  if (loadedDashboardId !== prevLoadedDashboardId) {
    setPrevLoadedDashboardId(loadedDashboardId);
    if (loadedDashboardId != null && editingDashboard != null && editingDashboard.id !== loadedDashboardId) {
      setEditingDashboard(null);
    }
  }

  const refresh = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: ["dashboard-page"] });
  }, [queryClient]);

  if (dashboardQuery.isLoading) return <DashboardSkeleton />;
  if (dashboardQuery.error || !dashboardQuery.data) {
    return <DashboardLoadError error={dashboardQuery.error} onRetry={() => dashboardQuery.refetch()} />;
  }

  const { dashboard, list, readOnly } = dashboardQuery.data;
  if (editingDashboard?.id === dashboard.id) {
    return (
      <DashboardEditor
        key={`${principalKey}:${dashboard.id}`}
        dashboard={dashboard}
        dashboards={list}
        selectNameOnMount={editingDashboard.selectName}
        onNavigate={onNavigate}
        onCancel={() => setEditingDashboard(null)}
        onDeleted={async () => {
          await refresh();
          setEditingDashboard(null);
          onNavigate({ page: "home" });
        }}
        onSaved={async (saved) => {
          await refresh();
          setEditingDashboard(null);
          if (saved.isDefault) onNavigate({ page: "home" });
        }}
      />
    );
  }

  const presentation = dashboard.widgets.length === 1 ? getWidgetPresentation(dashboard.widgets[0]) : FLOW_PRESENTATION;
  const removeWidget = async (instanceId: string) => {
    const updateWithoutWidget = (current: Dashboard) =>
      dashboards.update(current.id, {
        name: current.name,
        expectedVersion: current.version,
        widgets: current.widgets.filter((candidate) => candidate.instanceId !== instanceId),
      });

    try {
      await updateWithoutWidget(dashboard);
    } catch (error) {
      if (!(error instanceof Error) || !error.message.includes("DASHBOARD_VERSION_CONFLICT")) throw error;
      const current = await dashboards.get(dashboard.id);
      if (current.widgets.some((candidate) => candidate.instanceId === instanceId)) {
        try {
          await updateWithoutWidget(current);
        } catch (retryError) {
          await refresh();
          throw retryError;
        }
      }
    }
    await refresh();
  };
  const alignmentGroups = dashboardAlignmentGroups(dashboard.widgets);
  return (
    <div className={presentation === "canvas" ? "space-y-3" : "space-y-5"} data-dashboard-presentation={presentation}>
      <DashboardHeader
        dashboard={dashboard}
        dashboards={list}
        onNavigate={onNavigate}
        onEdit={() => setEditingDashboard({ id: dashboard.id, selectName: false })}
        onCreated={async (created) => {
          await refresh();
          setEditingDashboard({ id: created.id, selectName: true });
          onNavigate({ page: "dashboard", id: created.id });
        }}
        readOnly={readOnly}
      />
      <DashboardRowAlignmentProvider>
        <div className={presentation === "canvas" ? "min-w-0" : "dashboard-widget-grid"}>
          {dashboard.widgets.map((widget) => (
            <DashboardWidgetHost
              key={widget.instanceId}
              dashboardId={dashboard.id}
              principalKey={principalKey}
              widget={widget}
              alignmentGroup={alignmentGroups.get(widget.instanceId)}
              onNavigate={onNavigate}
              onRemove={
                readOnly
                  ? undefined
                  : async () => {
                      await removeWidget(widget.instanceId);
                    }
              }
            />
          ))}
          {dashboard.widgets.length === 0 && readOnly ? (
            <div className="flex min-h-40 w-full items-center justify-center rounded-lg border border-dashed border-border bg-card/40 text-sm text-muted">
              No dashboard widgets are available.
            </div>
          ) : dashboard.widgets.length === 0 ? (
            <button
              onClick={() => setEditingDashboard({ id: dashboard.id, selectName: false })}
              className="flex min-h-40 w-full flex-col items-center justify-center gap-2 rounded-lg border border-dashed border-border bg-card/40 text-muted hover:border-accent/50 hover:text-accent"
            >
              <Plus className="h-6 w-6" />
              Add your first widget
            </button>
          ) : null}
        </div>
      </DashboardRowAlignmentProvider>
    </div>
  );
}

function DashboardLoadError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  return (
    <div className="mx-auto flex min-h-[35vh] max-w-lg flex-col items-center justify-center gap-3 text-center">
      <AlertTriangle className="h-8 w-8 text-yellow-400" />
      <h1 className="text-lg font-semibold text-foreground">Dashboard unavailable</h1>
      <p className="text-sm text-muted">
        {error instanceof Error ? error.message : "The dashboard could not be loaded."}
      </p>
      <button
        onClick={onRetry}
        className="rounded border border-border px-3 py-2 text-sm text-foreground hover:border-accent"
      >
        <RotateCcw className="mr-2 inline h-4 w-4" />
        Retry
      </button>
    </div>
  );
}

function DashboardHeader({
  dashboard,
  dashboards: items,
  onNavigate,
  onEdit,
  onCreated,
  readOnly = false,
}: {
  dashboard: Dashboard;
  dashboards: DashboardSummary[];
  onNavigate: (route: any) => void;
  onEdit: () => void;
  onCreated: (dashboard: Dashboard) => Promise<void>;
  readOnly?: boolean;
}) {
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const createDashboard = async () => {
    const existingNames = new Set(items.map((item) => item.name.trim().toUpperCase()));
    let name = "New Dashboard";
    for (let suffix = 2; existingNames.has(name.toUpperCase()); suffix += 1) {
      name = `New Dashboard ${suffix}`;
    }
    setCreating(true);
    setCreateError(null);
    try {
      await onCreated(await dashboards.create(name));
    } catch (caught) {
      setCreateError(caught instanceof Error ? caught.message : "Dashboard creation failed.");
    } finally {
      setCreating(false);
    }
  };
  return (
    <div className="space-y-2">
      <header className="flex items-center justify-between gap-3">
        <h1 className="sr-only">{dashboard.name}</h1>
        <DashboardTabs current={dashboard.id} dashboards={items} onNavigate={onNavigate} disabled={creating} />
        {!readOnly ? (
          <div className="flex shrink-0 gap-2">
            <button
              disabled={creating}
              onClick={createDashboard}
              title="New Dashboard"
              className="rounded-md border border-accent/60 px-2.5 py-2 text-sm text-accent hover:bg-accent/10 disabled:opacity-50 sm:px-3"
            >
              <Plus className="inline h-4 w-4 sm:mr-1" />
              <span className="sr-only sm:not-sr-only">{creating ? "Creating…" : "New Dashboard"}</span>
            </button>
            <button
              disabled={creating}
              onClick={onEdit}
              title="Customize"
              className="rounded-md border border-border bg-card px-2.5 py-2 text-sm text-foreground hover:border-accent/60 disabled:opacity-50 sm:px-3"
            >
              <Settings2 className="inline h-4 w-4 sm:mr-1" />
              <span className="sr-only sm:not-sr-only">Customize</span>
            </button>
          </div>
        ) : null}
      </header>
      {createError ? (
        <div role="alert" className="rounded border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-300">
          {createError}
        </div>
      ) : null}
    </div>
  );
}

/**
 * Inline dashboard switcher: names separated by dots, the current one emphasized. Wide screens
 * wrap the names; narrow screens keep one swipeable line with the current name scrolled into view.
 */
function DashboardTabs({
  current,
  dashboards: items,
  onNavigate,
  disabled,
}: {
  current: number;
  dashboards: DashboardSummary[];
  onNavigate: (route: any) => void;
  disabled: boolean;
}) {
  const scroller = useRef<HTMLElement>(null);
  const currentLink = useRef<HTMLAnchorElement>(null);
  useEffect(() => {
    const container = scroller.current;
    const link = currentLink.current;
    if (!container || !link) return;
    // Keep the scroll-padding clear so the current name never sits under the edge fade.
    const padding = parseFloat(getComputedStyle(container).scrollPaddingLeft) || 0;
    // The nav is `relative`, so it is the offsetParent that offsetLeft measures from.
    const start = link.offsetLeft - padding;
    const end = link.offsetLeft + link.offsetWidth + padding;
    // Too tight to clear both fades: centre the name so they overlap it evenly.
    if (end - start > container.clientWidth) container.scrollLeft = (start + end - container.clientWidth) / 2;
    else if (start < container.scrollLeft) container.scrollLeft = start;
    else if (end > container.scrollLeft + container.clientWidth) container.scrollLeft = end - container.clientWidth;
  }, [current, items]);

  const [fade, setFade] = useState({ start: false, end: false });
  useEffect(() => {
    const container = scroller.current;
    if (!container) return;
    const update = () => {
      const start = container.scrollLeft > 1;
      const end = container.scrollLeft + container.clientWidth < container.scrollWidth - 1;
      setFade((previous) => (previous.start === start && previous.end === end ? previous : { start, end }));
    };
    update();
    container.addEventListener("scroll", update, { passive: true });
    const resizeObserver = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(update);
    // Watch the names too: their widths can change (fonts loading) without the nav resizing.
    for (const element of [container, ...container.querySelectorAll("li")]) resizeObserver?.observe(element);
    return () => {
      container.removeEventListener("scroll", update);
      resizeObserver?.disconnect();
    };
  }, [items]);

  return (
    <nav
      ref={scroller}
      aria-label="Dashboards"
      data-fade-start={fade.start || undefined}
      data-fade-end={fade.end || undefined}
      className="dashboard-tabs-fade scrollbar-hide relative min-w-0 flex-1 overflow-x-auto sm:overflow-visible"
    >
      <ol className="flex items-center whitespace-nowrap py-1 text-lg sm:flex-wrap sm:text-xl">
        {items.map((item, index) => {
          const isCurrent = item.id === current;
          const route: Route = item.isDefault ? { page: "home" } : { page: "dashboard", id: item.id };
          return (
            <li key={item.id} className="flex items-center">
              {index > 0 ? (
                <span aria-hidden="true" className="px-2 text-muted sm:px-2.5">
                  ·
                </span>
              ) : null}
              <a
                ref={isCurrent ? currentLink : undefined}
                aria-current={isCurrent ? "page" : undefined}
                aria-disabled={disabled && !isCurrent ? true : undefined}
                title={item.name}
                {...createRouteLinkProps(route, () => {
                  if (!disabled && !isCurrent) onNavigate(route);
                })}
                className={
                  isCurrent
                    ? "max-w-[16rem] truncate py-1 font-semibold text-foreground"
                    : `max-w-[16rem] truncate py-1 text-muted transition-colors hover:text-foreground ${disabled ? "pointer-events-none opacity-50" : ""}`
                }
              >
                {item.name}
              </a>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

function DashboardWidgetHost({
  dashboardId,
  principalKey,
  widget,
  alignmentGroup,
  onNavigate,
  onRemove,
  editing = false,
}: {
  dashboardId: number;
  principalKey: string;
  widget: DashboardWidget;
  alignmentGroup?: string;
  onNavigate: (route: any) => void;
  onRemove?: () => Promise<void>;
  editing?: boolean;
}) {
  const content = widgetToContent(widget);
  const layout = useMemo(
    () => ({ ...getWidgetLayout(widget), alignmentGroup }),
    // oxlint-disable-next-line react-hooks/exhaustive-deps -- the layout derives from widget.layout and presentation only
    [widget.layout, widget.presentation, alignmentGroup],
  );
  if (content) {
    return (
      <DashboardWidgetLayoutContext.Provider value={layout}>
        <div style={{ containerType: "inline-size", ...widgetSpanStyle(layout) }}>
          <RecommendationRow
            principalKey={principalKey}
            content={content}
            widgetLabel={widget.label}
            onNavigate={onNavigate}
            onRemove={onRemove}
            editing={editing}
          />
        </div>
      </DashboardWidgetLayoutContext.Provider>
    );
  }

  return (
    <ExtensionDashboardWidgetHost dashboardId={dashboardId} widget={widget} layout={layout} onNavigate={onNavigate} />
  );
}

function ExtensionDashboardWidgetHost({
  dashboardId,
  widget,
  layout,
  onNavigate,
}: {
  dashboardId: number;
  widget: DashboardWidget;
  layout: ResolvedWidgetLayout;
  onNavigate: (route: any) => void;
}) {
  const { manifest, extensionsSettling, resolveComponent, getExtensionRevision } = useExtensions();
  const { hasPermission } = useAuth();
  const safeConfiguration = useMemo(() => cloneJsonConfiguration(widget.configuration), [widget.configuration]);

  const definition = manifest?.dashboardWidgets?.find(
    (item) => item.extensionId === widget.owner && item.id === widget.widgetKey,
  );
  const presentation = getWidgetPresentation(widget);
  const Component =
    definition &&
    definitionSupportsPresentation(definition, presentation) &&
    canAccessExtensionContribution(definition, hasPermission)
      ? resolveComponent(definition.extensionId, definition.componentName)
      : undefined;
  if (!definition || !Component) {
    // Extension modules can still be importing after the dashboard renders; only call a widget
    // unavailable once they have settled.
    if (extensionsSettling) {
      return (
        <div
          role="status"
          aria-busy="true"
          style={widgetSpanStyle(layout)}
          className="min-h-24 rounded-lg bg-card motion-safe:animate-pulse"
        >
          <span className="sr-only">Loading widget</span>
        </div>
      );
    }
    return <UnavailableWidget widget={widget} />;
  }

  return (
    <div
      style={{ containerType: "inline-size", ...widgetSpanStyle(layout) }}
      className={`dashboard-widget-container dashboard-widget-${presentation}`}
      data-widget-presentation={presentation}
    >
      <ExtensionErrorBoundary
        extensionId={definition.extensionId}
        resetKey={`${widget.instanceId}:${getExtensionRevision(definition.extensionId)}`}
        fallback={<UnavailableWidget widget={widget} failed />}
      >
        {/* oxlint-disable-next-line react/static-components -- resolved from the extension component registry, which returns a stable reference */}
        <Component
          dashboardId={dashboardId}
          instanceId={widget.instanceId}
          configuration={safeConfiguration}
          presentation={presentation}
          onNavigate={onNavigate}
        />
      </ExtensionErrorBoundary>
    </div>
  );
}

function UnavailableWidget({ widget, failed = false }: { widget: DashboardWidget; failed?: boolean }) {
  return (
    <div className="flex min-h-24 items-center gap-3 rounded-lg border border-yellow-500/25 bg-yellow-500/5 px-4 py-3">
      <AlertTriangle className="h-5 w-5 shrink-0 text-yellow-400" />
      <div>
        <p className="font-medium text-foreground">{widget.label}</p>
        <p className="text-xs text-muted">
          {failed ? "This widget failed to render." : "Its extension is unavailable or you no longer have access."}{" "}
          Configuration has been preserved.
        </p>
      </div>
    </div>
  );
}

function WidgetLoadError({ label, error, onRetry }: { label: string; error: unknown; onRetry: () => void }) {
  return (
    <div
      role="alert"
      className="flex min-h-24 flex-wrap items-center justify-between gap-3 rounded-lg border border-red-500/30 bg-red-500/10 px-4 py-3"
    >
      <div>
        <p className="font-medium text-foreground">{label} could not be loaded</p>
        <p className="text-xs text-muted">{error instanceof Error ? error.message : "The widget request failed."}</p>
      </div>
      <button
        type="button"
        onClick={onRetry}
        aria-label={`Retry ${label}`}
        className="rounded border border-red-400/40 px-3 py-2 text-sm text-red-300 hover:bg-red-500/10"
      >
        <RotateCcw className="mr-1 inline h-4 w-4" />
        Retry
      </button>
    </div>
  );
}

function MissingSourceWidget({
  label,
  reason,
  onRemove,
}: {
  label: string;
  reason: string;
  onRemove?: () => Promise<void>;
}) {
  const [removing, setRemoving] = useState(false);
  const [removeFailed, setRemoveFailed] = useState(false);

  const remove = async () => {
    if (!onRemove || removing) return;
    setRemoving(true);
    setRemoveFailed(false);
    try {
      await onRemove();
    } catch {
      setRemoveFailed(true);
      setRemoving(false);
    }
  };

  return (
    <div
      role="alert"
      className="flex min-h-24 flex-wrap items-center justify-between gap-3 rounded-lg border border-red-500/30 bg-red-500/10 px-4 py-3"
    >
      <div>
        <p className="font-medium text-foreground">{label} is unavailable</p>
        <p className="text-xs text-muted">{reason}</p>
        {removeFailed ? <p className="mt-1 text-xs text-red-300">The widget could not be removed. Try again.</p> : null}
      </div>
      {onRemove ? (
        <button
          type="button"
          disabled={removing}
          onClick={() => void remove()}
          aria-label={`Remove ${label}`}
          className="rounded border border-red-400/40 px-3 py-2 text-sm text-red-300 hover:bg-red-500/10 disabled:opacity-50"
        >
          <Trash2 className="mr-1 inline h-4 w-4" />
          {removing ? "Removing…" : "Remove"}
        </button>
      ) : null}
    </div>
  );
}

function DashboardEditor({
  dashboard,
  dashboards: dashboardList,
  selectNameOnMount = false,
  onNavigate,
  onCancel,
  onDeleted,
  onSaved,
}: {
  dashboard: Dashboard;
  dashboards: DashboardSummary[];
  selectNameOnMount?: boolean;
  onNavigate: (route: any) => void;
  onCancel: () => void;
  onDeleted: () => Promise<void>;
  onSaved: (dashboard: Dashboard) => Promise<void>;
}) {
  const { manifest } = useExtensions();
  const { hasPermission, user } = useAuth();
  const [draft, setDraft] = useState<Dashboard>(() => ({
    ...dashboard,
    widgets: dashboard.widgets.map((widget) => ({ ...widget })),
  }));
  const [showCatalog, setShowCatalog] = useState(false);
  const [configuringId, setConfiguringId] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [operation, setOperation] = useState<"duplicate" | "delete" | "default" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const allowNavigation = useRef(false);
  const pendingScrollWidgetId = useRef<string | null>(null);
  const widgetElements = useRef(new Map<string, HTMLElement>());
  const editorToolbar = useRef<HTMLElement | null>(null);
  const dashboardNameInput = useRef<HTMLInputElement | null>(null);
  const editorUrl = useRef(`${window.location.pathname}${window.location.search}`);
  const editorHistoryState = useRef(window.history.state);
  const dirty =
    JSON.stringify({ name: draft.name, widgets: draft.widgets }) !==
    JSON.stringify({ name: dashboard.name, widgets: dashboard.widgets });
  const busy = saving || operation !== null;
  const principalKey = dashboardPrincipalKey(user);
  const { data: allSavedFilters } = useQuery({
    queryKey: ["saved-filters", "all", principalKey],
    queryFn: () => savedFilters.list(),
  });
  const { data: dynamicGroups } = useQuery({
    queryKey: ["dynamic-groups-all", "dashboard", principalKey],
    queryFn: () => groups.find({ page: 1, perPage: 1000, sort: "name", direction: "asc" }, { kind: "dynamic" }),
    enabled: showCatalog,
  });
  const definitions = (manifest?.dashboardWidgets ?? []).filter((definition) =>
    canAccessExtensionContribution(definition, hasPermission),
  );

  useEffect(() => {
    if (!selectNameOnMount) return;
    dashboardNameInput.current?.focus();
    dashboardNameInput.current?.select();
  }, [selectNameOnMount]);

  useEffect(() => {
    const confirmNavigation = () =>
      allowNavigation.current || !dirty || window.confirm("Discard your unsaved dashboard changes?");
    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      if (!dirty) return;
      event.preventDefault();
      event.returnValue = "";
    };
    const handlePopState = () => {
      if (confirmNavigation()) return;
      window.history.pushState(historyStateForNewEntry(editorHistoryState.current), "", editorUrl.current);
      emitLocationChange();
    };
    const unregisterBlocker = registerNavigationBlocker(confirmNavigation);
    window.addEventListener("beforeunload", handleBeforeUnload);
    window.addEventListener("popstate", handlePopState);
    return () => {
      unregisterBlocker();
      window.removeEventListener("beforeunload", handleBeforeUnload);
      window.removeEventListener("popstate", handlePopState);
    };
  }, [dirty]);

  // Close the catalog and widget settings once an operation starts.
  const [prevBusy, setPrevBusy] = useState(busy);
  if (busy !== prevBusy) {
    setPrevBusy(busy);
    if (busy) {
      setShowCatalog(false);
      setConfiguringId(null);
    }
  }

  useEffect(() => {
    const instanceId = pendingScrollWidgetId.current;
    if (!instanceId) return;
    const element = widgetElements.current.get(instanceId);
    if (!element) {
      if (!draft.widgets.some((widget) => widget.instanceId === instanceId)) pendingScrollWidgetId.current = null;
      return;
    }
    pendingScrollWidgetId.current = null;
    let revealFrame: number | undefined;
    const revealWidget = () => {
      if (revealFrame !== undefined) window.cancelAnimationFrame(revealFrame);
      revealFrame = window.requestAnimationFrame(() => {
        revealFrame = undefined;
        const delta = getWidgetRevealScrollDelta(
          element.getBoundingClientRect(),
          editorToolbar.current?.getBoundingClientRect().bottom ?? 0,
          window.innerHeight,
        );
        if (Math.abs(delta) > 1) window.scrollBy({ top: delta, behavior: "smooth" });
      });
    };
    const resizeObserver = new ResizeObserver(revealWidget);
    resizeObserver.observe(element);
    revealWidget();
    let observerTimer: number | undefined;
    const stopObserving = () => {
      if (revealFrame !== undefined) {
        window.cancelAnimationFrame(revealFrame);
        revealFrame = undefined;
      }
      resizeObserver.disconnect();
      if (observerTimer !== undefined) window.clearTimeout(observerTimer);
      window.removeEventListener("wheel", stopObserving);
      window.removeEventListener("touchstart", stopObserving);
      window.removeEventListener("pointerdown", stopObserving);
      window.removeEventListener("keydown", stopOnScrollKey);
    };
    const stopOnScrollKey = (event: KeyboardEvent) => {
      if (["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " "].includes(event.key)) stopObserving();
    };
    window.addEventListener("wheel", stopObserving, { passive: true });
    window.addEventListener("touchstart", stopObserving, { passive: true });
    window.addEventListener("pointerdown", stopObserving, { passive: true });
    window.addEventListener("keydown", stopOnScrollKey);
    observerTimer = window.setTimeout(stopObserving, 3000);
    return stopObserving;
  }, [draft.widgets]);

  const confirmDiscard = () => !dirty || window.confirm("Discard your unsaved dashboard changes?");
  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const saved = await dashboards.update(draft.id, {
        name: draft.name,
        expectedVersion: draft.version,
        widgets: draft.widgets,
      });
      allowNavigation.current = true;
      await onSaved(saved);
    } catch (caught) {
      allowNavigation.current = false;
      const message = caught instanceof Error ? caught.message : "Dashboard save failed.";
      if (
        message.includes("DASHBOARD_VERSION_CONFLICT") &&
        window.confirm("This dashboard changed elsewhere. Overwrite it with this draft?")
      ) {
        try {
          const current = await dashboards.get(draft.id);
          const overwritten = await dashboards.update(draft.id, {
            name: draft.name,
            expectedVersion: current.version,
            widgets: draft.widgets,
          });
          allowNavigation.current = true;
          await onSaved(overwritten);
          return;
        } catch (overwriteError) {
          allowNavigation.current = false;
          setError(overwriteError instanceof Error ? overwriteError.message : "Dashboard overwrite failed.");
        }
      } else {
        setError(message);
      }
    } finally {
      setSaving(false);
    }
  };

  const addWidget = (widget: DashboardWidget) => {
    if (busy) return;
    pendingScrollWidgetId.current = widget.instanceId;
    setDraft((current) => ({ ...current, widgets: [...current.widgets, widget] }));
    setShowCatalog(false);
  };
  const duplicateWidget = (widget: DashboardWidget) => {
    if (busy || !canDuplicateWidget(widget, definitions)) return;
    const copy = { ...widget, instanceId: createInstanceId(), configuration: structuredClone(widget.configuration) };
    setDraft((current) => ({ ...current, widgets: [...current.widgets, copy] }));
  };
  const removeWidget = (instanceId: string) => {
    if (busy) return;
    setDraft((current) => ({
      ...current,
      widgets: current.widgets.filter((widget) => widget.instanceId !== instanceId),
    }));
  };
  const setWidgetPresentation = (instanceId: string, presentation: DashboardWidgetPresentation) => {
    if (busy || (presentation === "canvas" && draft.widgets.length !== 1)) return;
    setDraft((current) => ({
      ...current,
      widgets: current.widgets.map((widget) =>
        widget.instanceId === instanceId ? { ...widget, presentation } : widget,
      ),
    }));
  };
  const setWidgetLayout = (instanceId: string, patch: DashboardWidgetLayout) => {
    if (busy) return;
    setDraft((current) => ({
      ...current,
      widgets: current.widgets.map((widget) =>
        widget.instanceId === instanceId ? { ...widget, layout: { ...widget.layout, ...patch } } : widget,
      ),
    }));
  };

  const duplicateDashboard = async () => {
    if (!confirmDiscard()) return;
    const name = window.prompt("Name for the duplicate", `${dashboard.name} Copy`)?.trim();
    if (!name) return;
    setOperation("duplicate");
    setError(null);
    try {
      const created = await dashboards.duplicate(dashboard.id, name);
      allowNavigation.current = true;
      onNavigate({ page: "dashboard", id: created.id });
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Dashboard duplication failed.");
    } finally {
      setOperation(null);
    }
  };
  const deleteDashboard = async () => {
    if (dashboardList.length <= 1) {
      setError("The last dashboard cannot be deleted.");
      return;
    }
    if (!window.confirm(`Delete dashboard “${dashboard.name}”?`)) return;
    setOperation("delete");
    setError(null);
    try {
      await dashboards.delete(dashboard.id);
      allowNavigation.current = true;
      await onDeleted();
    } catch (caught) {
      allowNavigation.current = false;
      setError(caught instanceof Error ? caught.message : "Dashboard deletion failed.");
    } finally {
      setOperation(null);
    }
  };
  const setDefault = async () => {
    if (!confirmDiscard()) return;
    setOperation("default");
    setError(null);
    try {
      const updated = await dashboards.setDefault(dashboard.id);
      allowNavigation.current = true;
      await onSaved(updated);
    } catch (caught) {
      allowNavigation.current = false;
      setError(caught instanceof Error ? caught.message : "Could not set the default dashboard.");
    } finally {
      setOperation(null);
    }
  };

  const configuredWidget = configuringId
    ? draft.widgets.find((widget) => widget.instanceId === configuringId)
    : undefined;
  return (
    <div className="space-y-4">
      <header
        ref={editorToolbar}
        className="sticky top-14 z-30 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-card p-3 shadow-lg"
      >
        <div className="flex min-w-0 items-center gap-3">
          <span className="rounded bg-accent/15 px-2 py-1 text-xs font-medium text-accent">Editing Dashboard</span>
          <input
            ref={dashboardNameInput}
            aria-label="Dashboard name"
            disabled={busy}
            value={draft.name}
            maxLength={100}
            onChange={(event) => setDraft((current) => ({ ...current, name: event.target.value }))}
            onKeyDown={(event) => {
              if (event.key !== "Enter" || event.nativeEvent.isComposing || busy || !draft.name.trim()) return;
              event.preventDefault();
              void save();
            }}
            className="min-w-0 rounded border border-border bg-input px-3 py-2 font-medium text-foreground"
          />
        </div>
        <div className="flex flex-wrap gap-2">
          {!dashboard.isDefault ? (
            <button
              disabled={busy}
              onClick={setDefault}
              className="rounded border border-border px-3 py-2 text-sm text-foreground hover:border-accent disabled:opacity-50"
            >
              <Home className="mr-1 inline h-4 w-4" />
              {operation === "default" ? "Setting…" : "Set Default"}
            </button>
          ) : null}
          <button
            disabled={busy}
            onClick={duplicateDashboard}
            className="rounded border border-border px-3 py-2 text-sm text-foreground hover:border-accent disabled:opacity-50"
          >
            <Copy className="mr-1 inline h-4 w-4" />
            {operation === "duplicate" ? "Duplicating…" : "Duplicate"}
          </button>
          <button
            disabled={busy}
            onClick={deleteDashboard}
            className="rounded border border-red-500/30 px-3 py-2 text-sm text-red-400 hover:bg-red-500/10 disabled:opacity-50"
          >
            <Trash2 className="mr-1 inline h-4 w-4" />
            {operation === "delete" ? "Deleting…" : "Delete"}
          </button>
          <button
            disabled={busy}
            onClick={() => {
              if (confirmDiscard()) onCancel();
            }}
            className="rounded px-3 py-2 text-sm text-muted hover:text-foreground disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            disabled={busy || !draft.name.trim()}
            onClick={save}
            className="rounded bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-50"
          >
            <Check className="mr-1 inline h-4 w-4" />
            {saving ? "Saving…" : "Done"}
          </button>
        </div>
      </header>
      <button
        disabled={busy}
        onClick={() => setShowCatalog(true)}
        className="flex w-full items-center justify-center gap-2 rounded-lg border border-dashed border-border py-4 text-sm text-accent hover:border-accent/60 hover:bg-accent/5 disabled:opacity-50"
      >
        <Plus className="h-4 w-4" />
        Add Widget
      </button>
      {error ? (
        <div role="alert" className="rounded border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-300">
          {error}
        </div>
      ) : null}

      <SortableList
        items={draft.widgets}
        getKey={(widget) => widget.instanceId}
        onReorder={(widgets) => {
          if (!busy) setDraft((current) => ({ ...current, widgets }));
        }}
        className="space-y-3 pb-6"
        renderItem={(widget, { dragHandleProps, isDragging, isOver }) => (
          <section
            ref={(element) => {
              if (element) widgetElements.current.set(widget.instanceId, element);
              else widgetElements.current.delete(widget.instanceId);
            }}
            className={`rounded-lg border bg-card/30 transition-colors ${isDragging || isOver ? "border-accent" : "border-border"}`}
          >
            <div className="flex flex-col gap-2 border-b border-border px-3 py-2 sm:flex-row sm:items-center">
              <div className="flex min-w-0 items-center gap-2 sm:flex-1">
                <span
                  {...(busy ? {} : dragHandleProps)}
                  aria-disabled={busy}
                  className={
                    busy ? "cursor-not-allowed text-muted opacity-50" : "cursor-grab text-muted active:cursor-grabbing"
                  }
                >
                  <GripVertical className="h-4 w-4" />
                </span>
                <span className="min-w-0 flex-1 truncate text-sm font-medium text-foreground">{widget.label}</span>
              </div>
              <div className="flex flex-wrap items-center gap-1 sm:ml-auto sm:justify-end">
                <WidgetLayoutControls
                  widget={widget}
                  disabled={busy}
                  onChange={(patch) => setWidgetLayout(widget.instanceId, patch)}
                />
                <WidgetPresentationControl
                  widget={widget}
                  definition={definitions.find(
                    (item) => item.extensionId === widget.owner && item.id === widget.widgetKey,
                  )}
                  dashboardWidgetCount={draft.widgets.length}
                  disabled={busy}
                  onChange={(presentation) => setWidgetPresentation(widget.instanceId, presentation)}
                />
                <button
                  disabled={busy}
                  onClick={() => setConfiguringId(widget.instanceId)}
                  className="px-2 py-1 text-xs text-muted hover:text-accent disabled:opacity-50"
                >
                  <Settings2 className="mr-1 inline h-3.5 w-3.5" />
                  Configure
                </button>
                {canDuplicateWidget(widget, definitions) ? (
                  <button
                    disabled={busy}
                    onClick={() => duplicateWidget(widget)}
                    className="px-2 py-1 text-xs text-muted hover:text-accent disabled:opacity-50"
                  >
                    <Copy className="mr-1 inline h-3.5 w-3.5" />
                    Duplicate
                  </button>
                ) : null}
                <button
                  disabled={busy}
                  onClick={() => removeWidget(widget.instanceId)}
                  className="px-2 py-1 text-xs text-red-400 hover:text-red-300 disabled:opacity-50"
                >
                  <Trash2 className="mr-1 inline h-3.5 w-3.5" />
                  Remove
                </button>
              </div>
            </div>
            <div className="p-3">
              <DashboardWidgetHost
                dashboardId={dashboard.id}
                principalKey={principalKey}
                widget={widget}
                onNavigate={onNavigate}
                editing
              />
            </div>
          </section>
        )}
      />

      {showCatalog ? (
        <WidgetCatalog
          currentWidgets={draft.widgets}
          savedFilters={allSavedFilters ?? []}
          dynamicGroups={dynamicGroups?.items ?? []}
          extensionDefinitions={definitions}
          disabled={busy}
          onAdd={addWidget}
          onClose={() => setShowCatalog(false)}
        />
      ) : null}
      {configuredWidget ? (
        <WidgetConfigurationDialog
          widget={configuredWidget}
          definition={definitions.find(
            (item) => item.extensionId === configuredWidget.owner && item.id === configuredWidget.widgetKey,
          )}
          disabled={busy}
          onSave={(configuration, label) => {
            if (busy) return;
            setDraft((current) => ({
              ...current,
              widgets: current.widgets.map((widget) =>
                widget.instanceId === configuredWidget.instanceId
                  ? { ...widget, configuration, label: label ?? widget.label }
                  : widget,
              ),
            }));
            setConfiguringId(null);
          }}
          onClose={() => setConfiguringId(null)}
        />
      ) : null}
    </div>
  );
}

function canDuplicateWidget(widget: DashboardWidget, definitions: ExtensionDashboardWidgetContribution[]) {
  if (getWidgetPresentation(widget) === "canvas") return false;
  if (widget.owner === "cove.core") return true;
  const definition = definitions.find((item) => item.extensionId === widget.owner && item.id === widget.widgetKey);
  return definition !== undefined && definition.allowMultiple !== false;
}

export function getWidgetRevealScrollDelta(
  rect: Pick<DOMRect, "top" | "bottom" | "height">,
  toolbarBottom: number,
  viewportHeight: number,
) {
  const revealTop = toolbarBottom + 4;
  const revealBottom = viewportHeight - 16;
  if (rect.height > revealBottom - revealTop) return rect.top - revealTop;
  if (rect.top < revealTop) return rect.top - revealTop;
  if (rect.bottom > revealBottom) return rect.bottom - revealBottom;
  return 0;
}

function WidgetLayoutControls({
  widget,
  disabled,
  onChange,
}: {
  widget: DashboardWidget;
  disabled: boolean;
  onChange: (patch: DashboardWidgetLayout) => void;
}) {
  if (getWidgetPresentation(widget) !== "flow") return null;
  const layout = getWidgetLayout(widget);
  const cardGrid = isCardGridWidget(widget);
  return (
    <>
      <label className="flex items-center gap-1 text-xs text-muted">
        Width
        <select
          aria-label={`Width for ${widget.label}`}
          value={layout.width}
          disabled={disabled}
          onChange={(event) => onChange({ width: Number(event.target.value) as DashboardWidgetWidth })}
          className="rounded border border-border bg-input px-2 py-1 text-xs text-foreground"
        >
          {DASHBOARD_WIDGET_WIDTHS.map((width) => (
            <option key={width} value={width}>
              {DASHBOARD_WIDGET_WIDTH_LABELS[width]}
            </option>
          ))}
        </select>
      </label>
      {cardGrid ? (
        <>
          <label className="flex items-center gap-1 text-xs text-muted">
            Rows
            <input
              type="number"
              aria-label={`Rows for ${widget.label}`}
              min={MIN_DASHBOARD_ROWS}
              max={MAX_DASHBOARD_ROWS}
              step={1}
              value={layout.rows}
              disabled={disabled}
              onChange={(event) => {
                const next = Number(event.target.value);
                if (Number.isFinite(next)) onChange({ rows: clampDashboardRows(next) });
              }}
              className="w-14 rounded border border-border bg-input px-2 py-1 text-xs text-foreground"
            />
          </label>
          <label className="flex items-center gap-1 text-xs text-muted">
            Pages
            <select
              aria-label={`Pages for ${widget.label}`}
              value={layout.pages ?? ""}
              disabled={disabled}
              onChange={(event) =>
                onChange({
                  pages: event.target.value === "" ? undefined : clampDashboardPages(Number(event.target.value)),
                })
              }
              className="rounded border border-border bg-input px-2 py-1 text-xs text-foreground"
            >
              <option value="">All</option>
              {Array.from({ length: MAX_DASHBOARD_PAGES - MIN_DASHBOARD_PAGES + 1 }, (_, index) => {
                const count = MIN_DASHBOARD_PAGES + index;
                return (
                  <option key={count} value={count}>
                    {count}
                  </option>
                );
              })}
            </select>
          </label>
          <label className="flex items-center gap-1 text-xs text-muted" title={`Card size: ${layout.cardSize}`}>
            <ZoomOut className="h-3 w-3" aria-hidden="true" />
            <input
              type="range"
              aria-label={`Card size for ${widget.label}`}
              min={MIN_DASHBOARD_CARD_SIZE}
              max={MAX_DASHBOARD_CARD_SIZE}
              step={1}
              value={layout.cardSize}
              disabled={disabled}
              onChange={(event) => onChange({ cardSize: clampDashboardCardSize(Number(event.target.value)) })}
              style={
                {
                  "--range-fill": `${((layout.cardSize - MIN_DASHBOARD_CARD_SIZE) / (MAX_DASHBOARD_CARD_SIZE - MIN_DASHBOARD_CARD_SIZE)) * 100}%`,
                } as CSSProperties
              }
              className="themed-range-input h-1 w-16 cursor-pointer sm:w-20"
            />
            <ZoomIn className="h-3 w-3" aria-hidden="true" />
          </label>
        </>
      ) : null}
    </>
  );
}

function WidgetPresentationControl({
  widget,
  definition,
  dashboardWidgetCount,
  disabled,
  onChange,
}: {
  widget: DashboardWidget;
  definition?: ExtensionDashboardWidgetContribution;
  dashboardWidgetCount: number;
  disabled: boolean;
  onChange: (presentation: DashboardWidgetPresentation) => void;
}) {
  const presentation = getWidgetPresentation(widget);
  const supported = widget.owner === "cove.core" ? [FLOW_PRESENTATION] : getSupportedPresentations(definition);
  const presentationSupported = supported.includes(presentation);
  if (!definition && widget.owner !== "cove.core") {
    return presentation === "canvas" ? (
      <span className="rounded bg-accent/10 px-2 py-1 text-[10px] font-semibold uppercase tracking-wide text-accent">
        Canvas
      </span>
    ) : null;
  }
  if (supported.length < 2 && presentationSupported) {
    return presentation === "canvas" ? (
      <span className="rounded bg-accent/10 px-2 py-1 text-[10px] font-semibold uppercase tracking-wide text-accent">
        Canvas
      </span>
    ) : null;
  }
  return (
    <label className="flex items-center gap-1 text-xs text-muted">
      Presentation
      <select
        aria-label={`Presentation for ${widget.label}`}
        value={presentationSupported ? presentation : "unsupported"}
        disabled={disabled}
        onChange={(event) => onChange(event.target.value as DashboardWidgetPresentation)}
        className="rounded border border-border bg-input px-2 py-1 text-xs text-foreground"
      >
        {!presentationSupported ? (
          <option value="unsupported" disabled>
            Unsupported {presentation === "canvas" ? "Canvas" : "Flow"}
          </option>
        ) : null}
        {supported.map((option) => (
          <option key={option} value={option} disabled={option === "canvas" && dashboardWidgetCount !== 1}>
            {option === "canvas" ? "Canvas" : "Flow"}
          </option>
        ))}
      </select>
    </label>
  );
}

function useDashboardDialog<T extends HTMLElement>(onClose: () => void) {
  const dialogRef = useRef<T>(null);

  useEffect(() => {
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const focusTimer = window.setTimeout(() => {
      const initialFocus = dialogRef.current?.querySelector<HTMLElement>("[data-dialog-initial-focus]");
      (initialFocus ?? dialogRef.current)?.focus();
    }, 0);
    return () => {
      window.clearTimeout(focusTimer);
      previousFocus?.focus({ preventScroll: true });
    };
  }, []);

  const onKeyDown = (event: React.KeyboardEvent<HTMLElement>) => {
    event.stopPropagation();
    if (event.key === "Escape") {
      event.preventDefault();
      onClose();
      return;
    }
    if (event.key !== "Tab" || !dialogRef.current) return;
    const focusable = Array.from(
      dialogRef.current.querySelectorAll<HTMLElement>(
        "a[href]:not([hidden]), button:not(:disabled):not([hidden]), input:not(:disabled):not([type='hidden']):not([hidden]), select:not(:disabled):not([hidden]), textarea:not(:disabled):not([hidden]), [tabindex]:not([tabindex='-1']):not([hidden])",
      ),
    );
    if (focusable.length === 0) {
      event.preventDefault();
      dialogRef.current.focus();
      return;
    }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  };

  return { dialogRef, onKeyDown };
}

function WidgetCatalog({
  currentWidgets,
  savedFilters: filters,
  dynamicGroups,
  extensionDefinitions,
  disabled,
  onAdd,
  onClose,
}: {
  currentWidgets: DashboardWidget[];
  savedFilters: SavedFilter[];
  dynamicGroups: Group[];
  extensionDefinitions: ExtensionDashboardWidgetContribution[];
  disabled: boolean;
  onAdd: (widget: DashboardWidget) => void;
  onClose: () => void;
}) {
  const titleId = useId();
  const { dialogRef, onKeyDown } = useDashboardDialog<HTMLElement>(onClose);
  const searchRef = useRef<HTMLInputElement>(null);
  const [search, setSearch] = useState("");
  const addPremade = (filter: CustomFilter) => onAdd(contentToWidget(filter));
  const hasCanvasWidget = currentWidgets.some((widget) => getWidgetPresentation(widget) === "canvas");
  const supportedSavedFilters = filters.filter((filter) => normalizeFilterMode(filter.mode));
  const normalizedSearch = search.trim().toLocaleLowerCase();
  const matchesSearch = (label: string, description: string) =>
    !normalizedSearch || `${label} ${description}`.toLocaleLowerCase().includes(normalizedSearch);
  const flowConflictDescription = "Remove the Canvas widget before adding Flow content.";
  const builtInItems = PREMADE_FILTERS.map((filter) => ({
    key: `${filter.mode}:${filter.sortBy}:${filter.header}`,
    label: filter.header,
    description: hasCanvasWidget ? flowConflictDescription : `Collection · ${filter.mode}`,
    disabled: disabled || hasCanvasWidget,
    onClick: () => addPremade(filter),
  })).filter((item) => matchesSearch(item.label, item.description));
  const savedFilterItems = supportedSavedFilters
    .map((filter) => ({
      key: `saved:${filter.id}`,
      label: filter.name,
      description: hasCanvasWidget ? flowConflictDescription : `Saved filter · ${filter.mode}`,
      disabled: disabled || hasCanvasWidget,
      onClick: () => onAdd(contentToWidget({ type: "saved", savedFilterId: filter.id }, filter.name)),
    }))
    .filter((item) => matchesSearch(item.label, item.description));
  const dynamicGroupItems = dynamicGroups
    .map((group) => ({
      key: `group:${group.id}`,
      label: group.name,
      description: hasCanvasWidget ? flowConflictDescription : "Dynamic group",
      disabled: disabled || hasCanvasWidget,
      onClick: () => onAdd(contentToWidget({ type: "group", groupId: group.id }, group.name)),
    }))
    .filter((item) => matchesSearch(item.label, item.description));
  const extensionItems = extensionDefinitions
    .map((definition) => {
      const alreadyAdded =
        !definition.allowMultiple &&
        currentWidgets.some((widget) => widget.owner === definition.extensionId && widget.widgetKey === definition.id);
      const defaultPresentation = getDefaultPresentation(definition);
      const supportedPresentations = getSupportedPresentations(definition);
      const presentation =
        currentWidgets.length > 0 &&
        !hasCanvasWidget &&
        defaultPresentation === "canvas" &&
        supportedPresentations.includes("flow")
          ? FLOW_PRESENTATION
          : defaultPresentation;
      const canvasConflict = presentation === "canvas" && currentWidgets.length > 0;
      const flowConflict = presentation === "flow" && hasCanvasWidget;
      const conflictDescription = canvasConflict
        ? "Canvas widgets need an empty dashboard. Create or empty a dashboard first."
        : flowConflict
          ? flowConflictDescription
          : undefined;
      return {
        key: `${definition.extensionId}:${definition.id}`,
        label: definition.label,
        description: conflictDescription ?? definition.description ?? definition.extensionId,
        disabled: disabled || alreadyAdded || canvasConflict || flowConflict,
        onClick: () =>
          onAdd({
            instanceId: createInstanceId(),
            owner: definition.extensionId,
            widgetKey: definition.id,
            label: definition.label,
            configuration: structuredClone(definition.defaultConfiguration ?? {}),
            presentation,
          }),
      };
    })
    .filter((item) => matchesSearch(item.label, item.description));
  const hasMatches =
    builtInItems.length + savedFilterItems.length + dynamicGroupItems.length + extensionItems.length > 0;
  const onCatalogKeyDown = (event: React.KeyboardEvent<HTMLElement>) => {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
    const items = Array.from(
      dialogRef.current?.querySelectorAll<HTMLButtonElement>("button[data-widget-catalog-item]:not(:disabled)") ?? [],
    );
    if (items.length === 0) return;
    if (event.currentTarget === searchRef.current) {
      if (event.key === "ArrowDown") {
        event.preventDefault();
        items[0].focus();
      }
      return;
    }
    const index = items.indexOf(event.currentTarget as HTMLButtonElement);
    if (index < 0) return;
    event.preventDefault();
    if (event.key === "ArrowUp" && index === 0) searchRef.current?.focus();
    else items[Math.max(0, Math.min(items.length - 1, index + (event.key === "ArrowDown" ? 1 : -1)))]?.focus();
  };
  return (
    <div className="fixed inset-0 z-50 flex items-stretch justify-end bg-black/70" onClick={onClose}>
      <aside
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onKeyDown={onKeyDown}
        className="h-full w-full max-w-md overflow-y-auto border-l border-border bg-surface p-5 shadow-xl"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="sticky -top-5 z-10 -mx-5 mb-5 border-b border-border bg-surface px-5 pb-4 pt-5">
          <div className="mb-4 flex items-center justify-between">
            <h2 id={titleId} className="text-lg font-semibold text-foreground">
              Add Widget
            </h2>
            <button onClick={onClose} aria-label="Close">
              <X className="h-5 w-5 text-muted" />
            </button>
          </div>
          <input
            ref={searchRef}
            data-dialog-initial-focus
            type="search"
            aria-label="Search widgets"
            placeholder="Search widgets…"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            onKeyDown={onCatalogKeyDown}
            className="block w-full rounded-md border border-border bg-input px-3 py-2 text-sm text-foreground placeholder:text-muted"
          />
        </div>
        {builtInItems.length ? (
          <CatalogSection title="Built-in" items={builtInItems} onItemKeyDown={onCatalogKeyDown} />
        ) : null}
        {savedFilterItems.length ? (
          <CatalogSection title="Saved Filters" items={savedFilterItems} onItemKeyDown={onCatalogKeyDown} />
        ) : null}
        {dynamicGroupItems.length ? (
          <CatalogSection title="Dynamic Groups" items={dynamicGroupItems} onItemKeyDown={onCatalogKeyDown} />
        ) : null}
        {extensionItems.length ? (
          <CatalogSection title="Extensions" items={extensionItems} onItemKeyDown={onCatalogKeyDown} />
        ) : null}
        {!hasMatches ? (
          <p className="rounded-lg border border-dashed border-border px-4 py-8 text-center text-sm text-muted">
            No widgets match “{search.trim()}”.
          </p>
        ) : null}
      </aside>
    </div>
  );
}

function CatalogSection({
  title,
  items,
  onItemKeyDown,
}: {
  title: string;
  items: Array<{ key: string; label: string; description: string; disabled: boolean; onClick: () => void }>;
  onItemKeyDown: (event: React.KeyboardEvent<HTMLElement>) => void;
}) {
  return (
    <section className="mb-6" aria-labelledby={`widget-catalog-${title.toLocaleLowerCase().replaceAll(" ", "-")}`}>
      <h3
        id={`widget-catalog-${title.toLocaleLowerCase().replaceAll(" ", "-")}`}
        className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted"
      >
        {title}
      </h3>
      <div className="space-y-2">
        {items.map((item) => (
          <CatalogButton
            key={item.key}
            label={item.label}
            description={item.description}
            disabled={item.disabled}
            onClick={item.onClick}
            onKeyDown={onItemKeyDown}
          />
        ))}
      </div>
    </section>
  );
}

function CatalogButton({
  label,
  description,
  disabled,
  onClick,
  onKeyDown,
}: {
  label: string;
  description: string;
  disabled?: boolean;
  onClick: () => void;
  onKeyDown: (event: React.KeyboardEvent<HTMLElement>) => void;
}) {
  return (
    <button
      data-widget-catalog-item
      disabled={disabled}
      onClick={onClick}
      onKeyDown={onKeyDown}
      className="block w-full rounded-lg border border-border bg-card p-3 text-left hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-40"
    >
      <span className="block text-sm font-medium text-foreground">{label}</span>
      <span className="mt-1 block text-xs text-muted">{description}</span>
    </button>
  );
}

function WidgetConfigurationDialog({
  widget,
  definition,
  disabled,
  onSave,
  onClose,
}: {
  widget: DashboardWidget;
  definition?: ExtensionDashboardWidgetContribution;
  disabled: boolean;
  onSave: (configuration: unknown, label?: string) => void;
  onClose: () => void;
}) {
  const titleId = useId();
  const { dialogRef, onKeyDown } = useDashboardDialog<HTMLDivElement>(onClose);
  const { resolveComponent, getExtensionRevision } = useExtensions();
  const [configuration, setConfiguration] = useState(() => structuredClone(widget.configuration));
  const [valid, setValid] = useState(true);
  const [validationMessage, setValidationMessage] = useState<string | undefined>();
  const [configurationError, setConfigurationError] = useState<string | undefined>();
  const Editor = definition?.editorComponentName
    ? resolveComponent(definition.extensionId, definition.editorComponentName)
    : undefined;
  const coreContent = widgetToContent({ ...widget, configuration });
  const editorConfiguration = useMemo(() => cloneJsonConfiguration(configuration), [configuration]);

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center bg-black/75 p-4" onClick={onClose}>
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        onKeyDown={onKeyDown}
        className="w-full max-w-lg rounded-lg border border-border bg-surface p-5 shadow-xl"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="mb-4 flex items-center justify-between">
          <h2 id={titleId} className="text-lg font-semibold text-foreground">
            Configure {widget.label}
          </h2>
          <button data-dialog-initial-focus onClick={onClose} aria-label="Close">
            <X className="h-5 w-5 text-muted" />
          </button>
        </div>
        <fieldset disabled={disabled} className="contents">
          {widget.owner === "cove.core" && coreContent?.type === "custom" ? (
            <div className="space-y-3">
              <label className="block text-sm text-secondary">
                Title
                <input
                  value={coreContent.header}
                  onChange={(event) => setConfiguration({ ...(configuration as object), header: event.target.value })}
                  className="mt-1 block w-full rounded border border-border bg-input px-3 py-2 text-foreground"
                />
              </label>
              <label className="block text-sm text-secondary">
                Direction
                <select
                  value={coreContent.direction}
                  onChange={(event) =>
                    setConfiguration({ ...(configuration as object), direction: event.target.value })
                  }
                  className="mt-1 block w-full rounded border border-border bg-input px-3 py-2 text-foreground"
                >
                  <option value="desc">Descending</option>
                  <option value="asc">Ascending</option>
                </select>
              </label>
            </div>
          ) : Editor && definition ? (
            <ExtensionErrorBoundary
              extensionId={definition.extensionId}
              resetKey={getExtensionRevision(definition.extensionId)}
              fallback={<UnavailableWidget widget={widget} failed />}
            >
              {/* oxlint-disable-next-line react/static-components -- resolved from the extension component registry, which returns a stable reference */}
              <Editor
                configuration={editorConfiguration}
                presentation={getWidgetPresentation(widget)}
                onChange={(nextConfiguration: unknown) => {
                  try {
                    setConfiguration(cloneJsonConfiguration(nextConfiguration));
                    setConfigurationError(undefined);
                  } catch (caught) {
                    setConfigurationError(
                      caught instanceof Error ? caught.message : "Widget configuration must be valid JSON data.",
                    );
                  }
                }}
                onValidityChange={(nextValid: boolean, message?: string) => {
                  setValid(nextValid);
                  setValidationMessage(message);
                }}
              />
            </ExtensionErrorBoundary>
          ) : (
            <p className="text-sm text-muted">
              This widget has no additional settings. Its saved configuration will remain unchanged.
            </p>
          )}
          {validationMessage ? (
            <p className={`mt-3 text-sm ${valid ? "text-muted" : "text-red-400"}`}>{validationMessage}</p>
          ) : null}
          {configurationError ? (
            <p role="alert" className="mt-3 text-sm text-red-400">
              {configurationError}
            </p>
          ) : null}
          <div className="mt-5 flex justify-end gap-2">
            <button onClick={onClose} className="px-3 py-2 text-sm text-muted">
              Cancel
            </button>
            <button
              disabled={disabled || !valid || !!configurationError}
              onClick={() => onSave(configuration, coreContent?.type === "custom" ? coreContent.header : undefined)}
              className="rounded bg-accent px-4 py-2 text-sm text-white disabled:opacity-50"
            >
              Save
            </button>
          </div>
        </fieldset>
      </div>
    </div>
  );
}

// ─── Recommendation Row (dispatcher) ────────────────────────────────────────

function RecommendationRow({
  principalKey,
  content,
  widgetLabel,
  onNavigate,
  onRemove,
  editing = false,
}: {
  principalKey: string;
  content: FrontPageContent;
  widgetLabel?: string;
  onNavigate: (r: any) => void;
  onRemove?: () => Promise<void>;
  editing?: boolean;
}) {
  if (content.type === "saved") {
    return (
      <SavedFilterRecommendationRow
        principalKey={principalKey}
        savedFilterId={content.savedFilterId}
        fallbackLabel={widgetLabel}
        onNavigate={onNavigate}
        onRemove={onRemove}
        editing={editing}
      />
    );
  }
  if (content.type === "group") {
    return (
      <GroupRecommendationRow
        principalKey={principalKey}
        source={content}
        fallbackLabel={widgetLabel}
        onNavigate={onNavigate}
        onRemove={onRemove}
        editing={editing}
      />
    );
  }
  return <CustomFilterRecommendationRow filter={content} onNavigate={onNavigate} />;
}

// ─── Custom Filter Row ──────────────────────────────────────────────────────

function CustomFilterRecommendationRow({ filter, onNavigate }: { filter: CustomFilter; onNavigate: (r: any) => void }) {
  const perPage = dashboardRowPageSize(useDashboardWidgetLayout());
  const findFilter = useMemo(
    () => withSeededRandomSort({}, { perPage, sort: filter.sortBy, direction: filter.direction }),
    [filter, perPage],
  );

  const fetchFn = useMemo((): ((page: number) => Promise<any>) => {
    switch (filter.mode) {
      case "videos":
        return (page: number) => videos.find({ ...findFilter, page });
      case "performers":
        return (page: number) => performers.find({ ...findFilter, page });
      case "studios":
        return (page: number) => studios.find({ ...findFilter, page });
      case "tags":
        return (page: number) => tags.find({ ...findFilter, page });
      case "galleries":
        return (page: number) => galleries.find({ ...findFilter, page });
      case "groups":
        return (page: number) => groups.find({ ...findFilter, page });
      default:
        return async () => ({ items: [], totalCount: 0 });
    }
  }, [filter.mode, findFilter]);

  const query = useDashboardRowPages<any>({
    queryKey: ["front-page", filter.mode, findFilter],
    fetchPage: fetchFn,
  });

  const { items, isLoading } = query;
  const engagementHostType = getRecommendationEngagementHostType(filter.mode);
  const { engagementById } = useEntityEngagementBatch(
    engagementHostType ?? "video",
    engagementHostType ? items.map((item: any) => item.id) : [],
  );
  const queryVideoPage = useCallback((page: FindFilter) => videos.find(page), []);
  const { navigateFromList } = useVideoQueueNavigation({
    items: filter.mode === "videos" ? items : [],
    // The queue continues after everything the row has loaded so far, however many pages that is.
    filter: { ...findFilter, page: 1, perPage: items.length || perPage },
    totalCount: query.totalCount,
    infinitePageSize: false,
    queryPage: queryVideoPage,
    onNavigate,
  });
  if (query.isError)
    return (
      <WidgetLoadError
        label={filter.header}
        error={query.error}
        onRetry={() => {
          void query.refetch();
        }}
      />
    );
  if (!isLoading && items.length === 0) return null;

  return (
    <RecommendationRowShell
      header={filter.header}
      viewAllPage={filter.mode}
      viewAllFilter={{ q: "", page: 1, sort: filter.sortBy, direction: filter.direction }}
      viewAllObjectFilter={{}}
      onNavigate={onNavigate}
      loading={isLoading}
      cardShape={cardShapeForMode(filter.mode)}
      count={items.length}
      totalCount={query.totalCount}
      hasMore={query.hasMore}
      loadMore={query.loadMore}
      loadingMore={query.loadingMore}
    >
      {items.map((item: any) => (
        <EntityCard
          key={item.id}
          item={item}
          engagement={engagementById.get(item.id)}
          mode={filter.mode}
          onNavigate={navigateFromList}
        />
      ))}
    </RecommendationRowShell>
  );
}

// ─── Saved Filter Row ───────────────────────────────────────────────────────

function SavedFilterRecommendationRow({
  principalKey,
  savedFilterId,
  fallbackLabel,
  onNavigate,
  onRemove,
  editing = false,
}: {
  principalKey: string;
  savedFilterId: number;
  fallbackLabel?: string;
  onNavigate: (r: any) => void;
  onRemove?: () => Promise<void>;
  editing?: boolean;
}) {
  const normalizedFallbackLabel = fallbackLabel?.trim();
  const errorLabel =
    normalizedFallbackLabel && normalizedFallbackLabel !== "Saved filter"
      ? normalizedFallbackLabel
      : `Saved filter #${savedFilterId}`;
  const queryClient = useQueryClient();
  const filterQuery = useQuery({
    queryKey: savedFilterQueryKey(principalKey, savedFilterId),
    queryFn: () => savedFilters.get(savedFilterId),
  });
  const filter = filterQuery.data;

  const mode = normalizeFilterMode(filter?.mode);
  const parsedFilter = useMemo(() => parseJsonObject<FindFilter>(filter?.findFilter) ?? {}, [filter?.findFilter]);
  const parsedObjectFilter = useMemo(
    () => parseJsonObject<Record<string, unknown>>(filter?.objectFilter),
    [filter?.objectFilter],
  );
  const parsedUIOptions = useMemo(
    () => parseJsonObject<Record<string, unknown>>(filter?.uiOptions),
    [filter?.uiOptions],
  );
  const hasObjectFilter = !!parsedObjectFilter && Object.keys(parsedObjectFilter).length > 0;
  const segmentProfileId =
    typeof parsedUIOptions?.profileId === "number" &&
    Number.isInteger(parsedUIOptions.profileId) &&
    parsedUIOptions.profileId > 0
      ? parsedUIOptions.profileId
      : undefined;
  const perPage = dashboardRowPageSize(useDashboardWidgetLayout());
  const findFilter = useMemo((): FindFilter | undefined => {
    if (!mode) return undefined;
    return withSeededRandomSort(
      {},
      {
        ...parsedFilter,
        page: 1,
        perPage,
        sort: parsedFilter.sort ?? DEFAULT_SORT_BY_MODE[mode],
        direction: parsedFilter.direction ?? "desc",
      },
    );
  }, [mode, parsedFilter, perPage]);

  const queryVideoPage = useCallback(
    (page: FindFilter) =>
      hasObjectFilter ? videos.findFiltered({ findFilter: page, objectFilter: parsedObjectFilter }) : videos.find(page),
    [hasObjectFilter, parsedObjectFilter],
  );

  const fetchFn = useMemo((): ((page: number) => Promise<any>) => {
    if (!mode) return async () => ({ items: [], totalCount: 0 });
    const fetchMap: Record<string, (page: number) => Promise<any>> = {
      videos: (page: number) => queryVideoPage({ ...findFilter!, page }),
      performers: hasObjectFilter
        ? (page: number) =>
            performers.findFiltered({ findFilter: { ...findFilter, page }, objectFilter: parsedObjectFilter })
        : (page: number) => performers.find({ ...findFilter, page }),
      studios: hasObjectFilter
        ? (page: number) =>
            studios.findFiltered({ findFilter: { ...findFilter, page }, objectFilter: parsedObjectFilter })
        : (page: number) => studios.find({ ...findFilter, page }),
      tags: hasObjectFilter
        ? (page: number) => tags.findFiltered({ findFilter: { ...findFilter, page }, objectFilter: parsedObjectFilter })
        : (page: number) => tags.find({ ...findFilter, page }),
      galleries: hasObjectFilter
        ? (page: number) =>
            galleries.findFiltered({ findFilter: { ...findFilter, page }, objectFilter: parsedObjectFilter })
        : (page: number) => galleries.find({ ...findFilter, page }),
      groups: hasObjectFilter
        ? (page: number) =>
            groups.findFiltered({ findFilter: { ...findFilter, page }, objectFilter: parsedObjectFilter })
        : (page: number) => groups.find({ ...findFilter, page }),
      audios: hasObjectFilter
        ? (page: number) =>
            audios.findFiltered({ findFilter: { ...findFilter, page }, objectFilter: parsedObjectFilter })
        : (page: number) => audios.find({ ...findFilter, page }),
      texts: hasObjectFilter
        ? (page: number) =>
            texts.findFiltered({ findFilter: { ...findFilter, page }, objectFilter: parsedObjectFilter })
        : (page: number) => texts.find({ ...findFilter, page }),
      segments: async (page: number) => {
        if (segmentProfileId == null) return { items: [], totalCount: 0 };
        const criteria = readRawSegmentListFilter(parsedObjectFilter ?? {});
        const videoTagIds = readMultiIdCriterionIds(parsedObjectFilter?.videoTagsCriterion);
        const videoTagDepth = readMultiIdCriterionDepth(parsedObjectFilter?.videoTagsCriterion);
        const rawTagDepth = readMultiIdCriterionDepth(parsedObjectFilter?.rawTagsCriterion);
        const derivedFilter = readDerivedSpanQueryFilter(parsedObjectFilter?.derivedSpanQuery);
        const performerIds = Array.from(new Set(derivedFilter.operands.flatMap((operand) => operand.performerIds)));
        const performerFaceEntries = await Promise.all(
          // The row's pages share these lookups instead of repeating them for every page.
          performerIds.map(async (performerId) => {
            const response = await queryClient.fetchQuery({
              queryKey: ["front-page-performer-faces", principalKey, performerId],
              queryFn: () => faces.list({ performerId, merged: false, page: 1, perPage: 200 }),
              staleTime: 60_000,
            });
            return [performerId, response.items.map((face) => face.id)] as const;
          }),
        );
        const appliedQuery = buildAppliedDerivedQuery(derivedFilter, new Map(performerFaceEntries));
        const derivedQueryDescriptor = buildDerivedQueryDescriptor(derivedFilter);
        const spanRequest = buildSpanSearchRequest({
          activeProfileId: segmentProfileId,
          pageNumber: page,
          perPage,
          q: findFilter?.q?.trim() ?? "",
          videoTitle: criteria.videoTitle ?? "",
          videoTagIds,
          videoTagDepth,
          sort: findFilter?.sort ?? DEFAULT_SORT_BY_MODE.segments,
          direction: findFilter?.direction ?? "desc",
          seed: findFilter?.seed,
          includeVideoIds: criteria.videoIds,
          excludeVideoIds: criteria.excludeVideoIds,
          appliedQuery,
          rawFilter: { ...createDefaultRawSegmentFilter(), ...criteria, tagDepth: rawTagDepth },
        });
        const response = await segmentSpans.search(spanRequest);
        return {
          ...response,
          // A search that stopped early leaves the total to the separate, cached count below.
          spanRequest: response.totalCount < 0 ? spanRequest : undefined,
          items: response.items.map<DerivedSpanItem>((item) => ({
            id: `${item.videoId}:${item.span.spanKey}`,
            key: `${item.videoId}:${item.span.spanKey}`,
            kind: derivedQueryDescriptor ? "derivedQuery" : "profile",
            videoId: item.videoId,
            videoTitle: item.videoTitle ?? `Video #${item.videoId}`,
            videoUpdatedAt: item.videoUpdatedAt,
            coverSegmentId: item.coverSegmentId,
            coverVersion: item.coverVersion,
            span: item.span,
            profileId: item.profileId,
            derivedQuery: appliedQuery ?? undefined,
            derivedQueryDescriptor,
          })),
        };
      },
      rawsegments: async (page: number) => {
        const criteria = readRawSegmentListFilter(parsedObjectFilter ?? {});
        const videoTagIds = readMultiIdCriterionIds(parsedObjectFilter?.videoTagsCriterion);
        const videoTagDepth = readMultiIdCriterionDepth(parsedObjectFilter?.videoTagsCriterion);
        const rawTagDepth = readMultiIdCriterionDepth(parsedObjectFilter?.rawTagsCriterion);
        const response = await segmentLibrary.list(
          buildRawSegmentListOptions({
            pageNumber: page,
            perPage,
            q: findFilter?.q?.trim() ?? "",
            videoTitle: criteria.videoTitle ?? "",
            videoTagIds,
            videoTagDepth,
            sort: findFilter?.sort ?? DEFAULT_SORT_BY_MODE.rawsegments,
            direction: findFilter?.direction ?? "desc",
            seed: findFilter?.seed,
            includeVideoIds: criteria.videoIds,
            excludeVideoIds: criteria.excludeVideoIds,
            rawSegmentIds: [],
            rawFilter: { ...createDefaultRawSegmentFilter(), ...criteria, tagDepth: rawTagDepth },
            includeAggregate: false,
          }),
        );
        return {
          ...response,
          items: response.items.map<RawSegmentItem>((item) => ({
            ...item,
            key: `segment:${item.id}`,
            videoId: item.hostId,
            videoTitle: item.hostTitle?.trim() || `Video #${item.hostId}`,
          })),
        };
      },
    };
    return fetchMap[mode] ?? (async () => ({ items: [], totalCount: 0 }));
  }, [
    mode,
    findFilter,
    parsedObjectFilter,
    hasObjectFilter,
    segmentProfileId,
    perPage,
    queryVideoPage,
    queryClient,
    principalKey,
  ]);

  const itemQuery = useDashboardRowPages<any>({
    queryKey: ["front-page-saved", principalKey, savedFilterId, mode, findFilter, parsedObjectFilter, segmentProfileId],
    fetchPage: fetchFn,
    enabled: !!mode,
  });
  const { items, isLoading } = itemQuery;
  // An exact span total means resolving every span in the filter's scope; a row pages by "has more"
  // and goes without it rather than pay that on every dashboard load.
  const uncountedSpanRequest: SegmentSpanSearchRequest | undefined = (itemQuery.firstPage as any)?.spanRequest;
  const totalCount: number | undefined = uncountedSpanRequest ? undefined : itemQuery.totalCount;
  const engagementHostType = getRecommendationEngagementHostType(mode ?? undefined);
  const { engagementById } = useEntityEngagementBatch(
    engagementHostType ?? "video",
    engagementHostType ? items.map((item: any) => item.id) : [],
  );
  const { navigateFromList } = useVideoQueueNavigation({
    items: mode === "videos" ? items : [],
    filter: { ...findFilter, page: 1, perPage: items.length || perPage },
    totalCount: itemQuery.totalCount,
    infinitePageSize: false,
    queryPage: queryVideoPage,
    onNavigate,
  });
  if (isApiNotFoundError(filterQuery.error)) {
    return <MissingSourceWidget label={errorLabel} reason="The saved filter was deleted." onRemove={onRemove} />;
  }
  if (filterQuery.isError)
    return (
      <WidgetLoadError
        label={errorLabel}
        error={filterQuery.error}
        onRetry={() => {
          void filterQuery.refetch();
        }}
      />
    );
  if (itemQuery.isError)
    return (
      <WidgetLoadError
        label={filter?.name ?? "Saved filter"}
        error={itemQuery.error}
        onRetry={() => {
          void itemQuery.refetch();
        }}
      />
    );
  // The filter's entity type is unknown until it loads; most dashboard rows are videos.
  if (!filter) return <RecommendationRowSkeleton />;
  if (!mode) return null;
  if (itemQuery.isSuccess && items.length === 0) {
    if (!editing) return null;
    return (
      <div className="px-1 py-2">
        <h2 className="text-base font-semibold text-foreground">{filter.name}</h2>
        <p className="mt-2 text-sm text-muted">No matching entities.</p>
      </div>
    );
  }

  return (
    <RecommendationRowShell
      header={filter.name}
      viewAllPage={mode === "segments" || mode === "rawsegments" ? "segments" : (mode ?? "videos")}
      viewAllFilter={{
        ...parsedFilter,
        q: parsedFilter.q ?? "",
        page: 1,
        sort: parsedFilter.sort ?? DEFAULT_SORT_BY_MODE[mode],
        direction: parsedFilter.direction ?? "desc",
        ...(findFilter?.seed != null ? { seed: findFilter.seed } : {}),
      }}
      viewAllObjectFilter={parsedObjectFilter ?? {}}
      viewAllView={typeof parsedUIOptions?.displayMode === "string" ? parsedUIOptions.displayMode : undefined}
      viewAllProfileId={mode === "segments" ? segmentProfileId : undefined}
      viewAllSegmentsView={mode === "rawsegments" ? "raw" : undefined}
      onNavigate={onNavigate}
      loading={isLoading}
      cardShape={cardShapeForMode(mode)}
      count={items.length}
      totalCount={totalCount}
      hasMore={itemQuery.hasMore}
      loadMore={itemQuery.loadMore}
      loadingMore={itemQuery.loadingMore}
    >
      {items.map((item: any) => (
        <EntityCard
          key={item.id}
          item={item}
          engagement={engagementById.get(item.id)}
          mode={mode!}
          onNavigate={navigateFromList}
        />
      ))}
    </RecommendationRowShell>
  );
}

// ─── Group Row ──────────────────────────────────────────────────────────────

function GroupRecommendationRow({
  principalKey,
  source,
  fallbackLabel,
  onNavigate,
  onRemove,
  editing = false,
}: {
  principalKey: string;
  source: GroupRow;
  fallbackLabel?: string;
  onNavigate: (r: any) => void;
  onRemove?: () => Promise<void>;
  editing?: boolean;
}) {
  const { hasPermission } = useAuth();
  const errorLabel = fallbackLabel?.trim() || `Group #${source.groupId}`;
  const groupQuery = useQuery({
    queryKey: ["front-page-group", principalKey, source.groupId],
    queryFn: () => groups.get(source.groupId),
  });
  const groupId = groupQuery.data?.id;
  const perPage = dashboardRowPageSize(useDashboardWidgetLayout());
  const itemQuery = useDashboardRowPages<GroupItem>({
    queryKey: ["front-page-group-items", principalKey, groupId, perPage],
    fetchPage: (page) => groups.items.page(groupId!, { page, perPage }),
    enabled: groupId !== undefined,
  });
  const items = useMemo(
    () =>
      itemQuery.items.filter((item) => {
        const host = resolveGroupFeedHost(item);
        return !!host && canReadEntity(host.resource, hasPermission);
      }),
    [hasPermission, itemQuery.items],
  );
  // The same per-item hydration the group feed uses, so the row renders the shared entity tiles.
  const entities = useGroupFeedEntities(items, hasPermission, principalKey);
  if (isApiNotFoundError(groupQuery.error)) {
    return (
      <MissingSourceWidget
        label={errorLabel}
        reason="The group was deleted or is not visible to you."
        onRemove={onRemove}
      />
    );
  }
  if (groupQuery.isError)
    return (
      <WidgetLoadError
        label={errorLabel}
        error={groupQuery.error}
        onRetry={() => {
          void groupQuery.refetch();
        }}
      />
    );
  const group = groupQuery.data;
  if (itemQuery.isError)
    return (
      <WidgetLoadError
        label={group?.name ?? errorLabel}
        error={itemQuery.error}
        onRetry={() => {
          void itemQuery.refetch();
        }}
      />
    );
  if (!group) return <RecommendationRowSkeleton shape={RECOMMENDATION_CARD_SHAPES.groupItem} />;
  if (itemQuery.isSuccess && items.length === 0) {
    if (!editing) return null;
    return (
      <div className="px-1 py-2">
        <h2 className="text-base font-semibold text-foreground">{group.name}</h2>
        <p className="mt-2 text-sm text-muted">This group has no items.</p>
      </div>
    );
  }

  return (
    <RecommendationRowShell
      header={group.name}
      viewAllPage="group"
      viewAllId={groupId}
      onNavigate={onNavigate}
      loading={itemQuery.isLoading}
      cardShape={RECOMMENDATION_CARD_SHAPES.groupItem}
      count={items.length}
      totalCount={itemQuery.totalCount}
      totalNoun={["item", "items"]}
      hasMore={itemQuery.hasMore}
      loadMore={itemQuery.loadMore}
      loadingMore={itemQuery.loadingMore}
    >
      {items.map((item) => {
        const host = resolveGroupFeedHost(item)!;
        const state = entities.get(groupFeedItemKey(item));
        if (state?.status === "error") return null;
        if (!state || state.status === "loading") {
          return (
            <RecommendationCardSkeleton key={groupFeedItemKey(item)} shape={RECOMMENDATION_CARD_SHAPES.groupItem} />
          );
        }
        return (
          <GroupFeedEntityTile
            key={groupFeedItemKey(item)}
            entity={state.entity}
            title={item.title}
            onOpen={() => onNavigate(host.route)}
            onNavigate={onNavigate}
          />
        );
      })}
    </RecommendationRowShell>
  );
}

// ─── Recommendation Row Shell (horizontal carousel) ─────────────────────────

function RecommendationRowShell({
  header,
  viewAllPage,
  viewAllId,
  viewAllFilter,
  viewAllObjectFilter,
  viewAllView,
  viewAllProfileId,
  viewAllSegmentsView,
  onNavigate,
  loading,
  cardShape,
  count,
  totalCount,
  totalNoun = ["match", "matches"],
  hasMore = false,
  loadMore,
  loadingMore = false,
  children,
}: {
  header: string;
  viewAllPage: string;
  viewAllId?: number;
  viewAllFilter?: FindFilter;
  viewAllObjectFilter?: Record<string, unknown>;
  viewAllView?: string;
  viewAllProfileId?: number;
  viewAllSegmentsView?: "raw";
  onNavigate: (r: any) => void;
  loading: boolean;
  cardShape: RecommendationCardShape;
  count: number;
  /**
   * Every match of the row's query, not just the cards it shows; it drives the page count past what is
   * loaded and shows beside the title. A negative total means the source went uncounted.
   */
  totalCount?: number;
  /** What the total counts, singular then plural. */
  totalNoun?: readonly [string, string];
  hasMore?: boolean;
  loadMore?: () => void;
  loadingMore?: boolean;
  children: React.ReactNode;
}) {
  const { rows, cardSize, pages, alignmentGroup } = useDashboardWidgetLayout();
  // The cards on screen are a lower bound, in case the total was counted before they were added.
  const shownTotalCount = totalCount != null && totalCount >= 0 ? Math.max(totalCount, count) : undefined;
  const totalLabel =
    shownTotalCount != null
      ? `${shownTotalCount.toLocaleString()} ${totalNoun[shownTotalCount === 1 ? 0 : 1]}`
      : undefined;
  const alignment = useContext(DashboardRowAlignmentContext);
  const reportAlignment = alignment?.report;
  const removeAlignment = alignment?.remove;
  const alignmentId = useId();
  // One row scrolls and swipes like a carousel. More rows page whole columns at a time instead, so
  // no card is ever cut off at the edge.
  const paged = rows > 1;
  const scrollRef = useRef<HTMLDivElement>(null);
  const [canScrollLeft, setCanScrollLeft] = useState(false);
  const [canScrollRight, setCanScrollRight] = useState(false);
  const [currentPage, setCurrentPage] = useState(0);
  const [pageDestinations, setPageDestinations] = useState([0]);
  const [trackWidth, setTrackWidth] = useState(0);
  const [requestedPage, setRequestedPage] = useState(0);

  const updateScrollState = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    setTrackWidth(el.clientWidth);
    setCanScrollLeft(el.scrollLeft > 5);
    setCanScrollRight(el.scrollLeft + el.clientWidth < el.scrollWidth - 5);
    const destinations = getCarouselPageDestinations(el.scrollWidth, el.clientWidth);
    setPageDestinations(destinations);
    setCurrentPage(
      destinations.reduce(
        (nearestIndex, destination, index) =>
          Math.abs(destination - el.scrollLeft) < Math.abs(destinations[nearestIndex] - el.scrollLeft)
            ? index
            : nearestIndex,
        0,
      ),
    );
  }, []);

  useEffect(() => {
    updateScrollState();
    const el = scrollRef.current;
    if (el) {
      el.addEventListener("scroll", updateScrollState);
      const resizeObserver = new ResizeObserver(updateScrollState);
      resizeObserver.observe(el);
      return () => {
        el.removeEventListener("scroll", updateScrollState);
        resizeObserver.disconnect();
      };
    }
  }, [updateScrollState, count, rows, cardSize]);

  const { columns, cardWidthPx } = fitDashboardCards(trackWidth, dashboardCardWidthPx(cardSize));
  const perPage = columns * rows;
  // A page cap trims the items the widget shows, in both the carousel and the paged grid.
  // Tiles fill whatever cell they are given, so each one gets a fixed-width cell in the track.
  const childArray = useMemo(() => {
    const all = Children.toArray(children).map((child) => (
      <div
        key={isValidElement(child) && child.key !== null ? child.key : undefined}
        className="w-[var(--dashboard-card-width)] flex-shrink-0"
        style={{ scrollSnapAlign: "start" }}
      >
        {child}
      </div>
    ));
    return pages === undefined ? all : all.slice(0, pages * perPage);
  }, [children, pages, perPage]);
  const loadedCount = childArray.length;
  const knownTotal = Math.max(loadedCount, totalCount ?? 0);
  const itemLimit = pages === undefined ? knownTotal : Math.min(knownTotal, pages * perPage);
  const pagedTotal = loading ? perPage : Math.max(1, itemLimit);
  const pagedPageCount = Math.max(1, Math.ceil(pagedTotal / perPage));
  const pagedPage = Math.min(requestedPage, pagedPageCount - 1);
  // The last page backs up so it stays full, overlapping the previous page, instead of showing a
  // lone partial column.
  const pagedStart = Math.min(pagedPage * perPage, Math.max(0, childArray.length - perPage));
  const pagedItems = loading
    ? recommendationCardSkeletons(cardShape, rows).slice(0, perPage)
    : childArray.slice(pagedStart, pagedStart + perPage);

  const pageCount = paged ? pagedPageCount : pageDestinations.length;
  const activePage = paged ? pagedPage : currentPage;

  // Line card rows up with the other card-grid widgets in this dashboard row: measure this widget's
  // natural row heights (with any alignment lifted), report them, and apply the group maximum.
  const aligned = alignmentGroup && alignment ? alignment.heightsFor(alignmentGroup) : undefined;
  const alignedRows = aligned && paged ? aligned.map((height) => `${height}px`).join(" ") : undefined;
  const alignedHeight = aligned && !paged ? `${aligned[0]}px` : undefined;
  const alignedTrackStyle: CSSProperties = paged ? { gridTemplateRows: alignedRows } : { height: alignedHeight };
  const naturalRows = paged ? `repeat(${rows}, auto)` : "";
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (!el || !reportAlignment || !alignmentGroup || loading) return;
    if (paged) el.style.gridTemplateRows = naturalRows;
    else el.style.height = "";
    const heights = Array.from({ length: paged ? rows : 1 }, () => 0);
    Array.from(el.children).forEach((cell, index) => {
      const row = paged ? index % rows : 0;
      heights[row] = Math.max(heights[row], cell.getBoundingClientRect().height);
    });
    reportAlignment(alignmentId, alignmentGroup, heights.map(Math.ceil));
    if (paged) el.style.gridTemplateRows = alignedRows ?? naturalRows;
    else el.style.height = alignedHeight ?? "";
    // oxlint-disable-next-line react-hooks/exhaustive-deps -- re-measure only when the widget's own content or size changes
  }, [reportAlignment, alignmentGroup, alignmentId, childArray, columns, loading, paged, rows, trackWidth]);
  useEffect(() => () => removeAlignment?.(alignmentId), [removeAlignment, alignmentId]);

  // Fetch the next API page once the user is within a page of the end of what is loaded.
  useEffect(() => {
    if (!hasMore || loadingMore || !loadMore || loading) return;
    if (pages !== undefined && loadedCount >= pages * perPage) return;
    const needed = paged ? (pagedPage + 2) * perPage : (currentPage + 2) * columns;
    if (needed > loadedCount) loadMore();
  }, [columns, currentPage, hasMore, loadMore, loadedCount, loading, loadingMore, paged, pagedPage, pages, perPage]);
  const hasPrevious = paged ? pagedPage > 0 : canScrollLeft;
  const hasNext = paged ? pagedPage < pagedPageCount - 1 : canScrollRight;

  const goToPage = (page: number) => {
    if (paged) {
      setRequestedPage(Math.max(0, Math.min(pagedPageCount - 1, page)));
      return;
    }
    const el = scrollRef.current;
    if (!el) return;
    el.scrollTo({
      left: pageDestinations[Math.max(0, Math.min(pageDestinations.length - 1, page))],
      behavior: "smooth",
    });
  };
  const scroll = (dir: "left" | "right") => goToPage(activePage + (dir === "left" ? -1 : 1));

  return (
    <div className="recommendation-row">
      {/* Header */}
      <div className="flex items-center justify-between gap-3 mb-2 px-1">
        <div className="flex min-w-0 items-baseline gap-2">
          <h2 className="min-w-0 truncate text-base font-semibold text-foreground">{header}</h2>
          {shownTotalCount != null && (
            <span className="shrink-0 text-xs text-muted tabular-nums" title={totalLabel}>
              {shownTotalCount.toLocaleString()}
              <span className="sr-only"> {totalNoun[shownTotalCount === 1 ? 0 : 1]}</span>
            </span>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-3">
          {paged && pageCount > 1 ? (
            <div className="flex items-center gap-0.5 text-xs text-muted" aria-label={`${header} pages`}>
              <button
                onClick={() => scroll("left")}
                disabled={!hasPrevious}
                aria-label={`Previous ${header} page`}
                className="flex h-9 w-9 items-center justify-center rounded-md hover:text-accent disabled:opacity-30 disabled:hover:text-muted sm:h-6 sm:w-6"
              >
                <ChevronLeft className="h-4 w-4" />
              </button>
              <span className="tabular-nums">
                {activePage + 1} / {pageCount}
              </span>
              <button
                onClick={() => scroll("right")}
                disabled={!hasNext}
                aria-label={`Next ${header} page`}
                className="flex h-9 w-9 items-center justify-center rounded-md hover:text-accent disabled:opacity-30 disabled:hover:text-muted sm:h-6 sm:w-6"
              >
                <ChevronRight className="h-4 w-4" />
              </button>
            </div>
          ) : null}
          <button
            onClick={() =>
              onNavigate({
                page: viewAllPage,
                ...(viewAllId !== undefined ? { id: viewAllId } : {}),
                ...(viewAllFilter ? { listFilter: viewAllFilter } : {}),
                ...(viewAllObjectFilter !== undefined ? { listObjectFilter: viewAllObjectFilter } : {}),
                ...(viewAllView ? { listView: viewAllView } : {}),
                ...(viewAllProfileId ? { profileId: viewAllProfileId } : {}),
                ...(viewAllSegmentsView ? { segmentsView: viewAllSegmentsView } : {}),
              })
            }
            className="inline-flex min-h-9 shrink-0 items-center whitespace-nowrap rounded-md px-2 text-sm text-muted hover:text-accent sm:min-h-0 sm:px-0 sm:text-xs"
          >
            View All
          </button>
        </div>
      </div>

      {/* Scrollable cards */}
      <div className="relative group/row">
        {/* Left arrow */}
        {!paged && hasPrevious && (
          <button
            onClick={() => scroll("left")}
            aria-label={`Previous ${header} page`}
            className="absolute left-0 top-0 bottom-0 z-20 w-8 flex items-center justify-center bg-gradient-to-r from-background/90 to-transparent opacity-0 group-hover/row:opacity-100 focus:opacity-100 transition-opacity"
          >
            <ChevronLeft className="w-6 h-6 text-white" />
          </button>
        )}

        {paged ? (
          <div
            ref={scrollRef}
            className="gap-2 overflow-hidden px-1"
            style={{ ...dashboardPagedTrackStyle(rows), ...dashboardCardWidthStyle(cardWidthPx), ...alignedTrackStyle }}
            data-dashboard-rows={rows}
            data-dashboard-page={pagedPage}
            aria-busy={loading || undefined}
          >
            {pagedItems}
          </div>
        ) : (
          <div
            ref={scrollRef}
            className="flex gap-2 overflow-x-auto scrollbar-hide scroll-smooth px-1"
            style={{ scrollSnapType: "x mandatory", ...dashboardCardWidthStyle(cardWidthPx), ...alignedTrackStyle }}
            data-dashboard-rows={rows}
            aria-busy={loading || undefined}
          >
            {loading ? recommendationCardSkeletons(cardShape) : childArray}
          </div>
        )}

        {/* Right arrow */}
        {!paged && hasNext && (
          <button
            onClick={() => scroll("right")}
            aria-label={`Next ${header} page`}
            className="absolute right-0 top-0 bottom-0 z-20 w-8 flex items-center justify-center bg-gradient-to-l from-background/90 to-transparent opacity-0 group-hover/row:opacity-100 focus:opacity-100 transition-opacity"
          >
            <ChevronRight className="w-6 h-6 text-white" />
          </button>
        )}
      </div>

      {/* Page dots */}
      {pageCount > 1 && pageCount <= MAX_CAROUSEL_DOTS && (
        <div className="mx-auto flex max-w-full justify-start gap-1.5 overflow-x-auto px-1 mt-2 scrollbar-hide sm:justify-center sm:overflow-visible">
          {Array.from({ length: pageCount }, (_, i) => (
            <button
              key={paged ? i : pageDestinations[i]}
              onClick={() => goToPage(i)}
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full sm:h-1 sm:w-6"
              aria-label={`Go to carousel page ${i + 1}`}
            >
              <span
                className={`h-1.5 w-6 rounded-full transition-colors sm:h-full sm:w-full ${i === activePage ? "bg-foreground" : "bg-muted/40"}`}
              />
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** Past this many pages the dots stop being useful; the arrows and header pager still work. */
const MAX_CAROUSEL_DOTS = 20;

export function getCarouselPageDestinations(scrollWidth: number, clientWidth: number) {
  if (clientWidth <= 0 || scrollWidth <= clientWidth) return [0];
  const maxScroll = scrollWidth - clientWidth;
  const destinations = Array.from(
    { length: Math.floor(maxScroll / clientWidth) + 1 },
    (_, index) => index * clientWidth,
  );
  if (maxScroll - destinations[destinations.length - 1] > 5) destinations.push(maxScroll);
  return destinations;
}

// ─── Entity Cards (the shared tiles every list page uses) ───────────────────

function EntityCard({
  item,
  engagement,
  mode,
  onNavigate,
}: {
  item: any;
  engagement?: EntityEngagement;
  mode: FilterMode;
  onNavigate: (r: any) => void;
}) {
  const open = (page: string) => () => onNavigate({ page, id: item.id });
  switch (mode) {
    case "videos":
      return <VideoCard video={item} engagement={engagement} onClick={open("video")} onNavigate={onNavigate} />;
    case "performers":
      return (
        <PerformerTile performer={item} engagement={engagement} onClick={open("performer")} onNavigate={onNavigate} />
      );
    case "studios":
      return <StudioTile studio={item} engagement={engagement} onClick={open("studio")} onNavigate={onNavigate} />;
    case "tags":
      return <TagTile tag={item} engagement={engagement} onClick={open("tag")} onNavigate={onNavigate} />;
    case "galleries":
      return <GalleryTile gallery={item} engagement={engagement} onClick={open("gallery")} onNavigate={onNavigate} />;
    case "groups":
      return <GroupTile group={item} engagement={engagement} onClick={open("group")} onNavigate={onNavigate} />;
    case "audios":
      return <AudioTile audio={item} engagement={engagement} onClick={open("audio")} onNavigate={onNavigate} />;
    case "texts":
      return <TextTile text={item} engagement={engagement} onClick={open("text")} onNavigate={onNavigate} />;
    case "segments":
      return <DerivedSegmentCard item={item} onNavigate={onNavigate} />;
    case "rawsegments": {
      const raw = item as RawSegmentItem;
      const route = { page: "segment", id: raw.id };
      return (
        <SegmentTile
          segment={raw}
          route={route}
          label={`Open raw segment ${buildRawSegmentTitle(raw)}`}
          eyebrow={formatSegmentCardEyebrow(raw.startSec, raw.endSec)}
          onClick={() => onNavigate(route)}
        />
      );
    }
    default:
      return null;
  }
}

function DerivedSegmentCard({ item, onNavigate }: { item: DerivedSpanItem; onNavigate: (r: any) => void }) {
  const title = buildSpanTitle(item.span, item.videoTitle);
  const route = {
    page: "video-span",
    id: item.videoId,
    spanKey: item.span.spanKey,
    profileId: item.profileId,
    derivedQueryDescriptor: item.derivedQueryDescriptor,
  };
  return (
    <SegmentTile
      segment={{
        id: item.coverSegmentId ?? item.span.segmentIds[0] ?? item.id,
        hostType: "video",
        hostId: item.videoId,
        startSec: item.span.startSec,
        endSec: item.span.endSec,
        tagName: item.span.tagName,
        kind: item.span.kind,
        sourceKey: item.span.sourceKey,
        title,
        updatedAt: item.coverVersion ?? item.videoUpdatedAt,
        hostTitle: item.videoTitle,
      }}
      route={route}
      label={`Open segment ${title}`}
      eyebrow={formatSegmentCardEyebrow(item.span.startSec, item.span.endSec)}
      onClick={() => onNavigate(route)}
    />
  );
}

/** A group item's hydrated host entity rendered with the same tile the list pages use for that type. */
function GroupFeedEntityTile({
  entity,
  title,
  onOpen,
  onNavigate,
}: {
  entity: GroupFeedEntity;
  /** The group item's own title, which overrides the entity's. */
  title?: string;
  onOpen: () => void;
  onNavigate: (r: any) => void;
}) {
  const override = title?.trim();
  switch (entity.type) {
    case "video":
      return (
        <VideoCard
          video={override ? { ...entity.value, title: override } : entity.value}
          onClick={onOpen}
          onNavigate={onNavigate}
        />
      );
    case "image":
      return (
        <ImageTile
          image={override ? { ...entity.value, title: override } : entity.value}
          onClick={onOpen}
          onNavigate={onNavigate}
        />
      );
    case "audio":
      return (
        <AudioTile
          audio={override ? { ...entity.value, title: override } : entity.value}
          onClick={onOpen}
          onNavigate={onNavigate}
        />
      );
    case "text":
      return (
        <TextTile
          text={override ? { ...entity.value, title: override } : entity.value}
          onClick={onOpen}
          onNavigate={onNavigate}
        />
      );
    case "group":
      return <GroupTile group={entity.value} onClick={onOpen} onNavigate={onNavigate} />;
    case "performer":
      return <PerformerTile performer={entity.value} onClick={onOpen} onNavigate={onNavigate} />;
    case "studio":
      return <StudioTile studio={entity.value} onClick={onOpen} onNavigate={onNavigate} />;
    case "tag":
      return <TagTile tag={entity.value} onClick={onOpen} onNavigate={onNavigate} />;
    case "gallery":
      return <GalleryTile gallery={entity.value} onClick={onOpen} onNavigate={onNavigate} />;
    case "face":
      return <FaceTile face={entity.value} onClick={onOpen} />;
    case "segment":
      return <SegmentTile segment={entity.value} onClick={onOpen} />;
  }
}

function getRecommendationEngagementHostType(mode: FilterMode | undefined): AffinityHostType | null {
  switch (mode) {
    case "videos":
      return "video";
    case "performers":
      return "performer";
    case "studios":
      return "studio";
    case "galleries":
      return "gallery";
    case "groups":
      return "group";
    case "audios":
      return "audio";
    case "texts":
      return "text";
    case "rawsegments":
      return "segment";
    case "segments":
      return null;
    default:
      return null;
  }
}
