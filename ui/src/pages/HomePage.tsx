import { useState, useRef, useEffect, useCallback, useMemo, useId } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
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
  Audio,
  EntityEngagement,
  Video,
  Performer,
  Studio,
  Tag,
  Gallery,
  Group,
  SavedFilter,
  FindFilter,
  Dashboard,
  DashboardSummary,
  DashboardWidget,
  DashboardWidgetPresentation,
  ExtensionDashboardWidgetContribution,
  TextDocument,
} from "../api/types";
import { formatDuration, formatFileSize, getResolutionLabel, RatingBadge } from "../components/shared";
import { RatingBanner } from "../components/Rating";
import {
  ChevronLeft,
  ChevronRight,
  Settings2,
  Plus,
  Trash2,
  Film,
  User,
  Building2,
  Tag as TagIcon,
  Images,
  Clapperboard,
  GripVertical,
  Headphones,
  Layers,
  Copy,
  Home,
  AlertTriangle,
  X,
  Check,
  RotateCcw,
  FileText,
} from "lucide-react";
import { createRouteLinkProps } from "../components/cardNavigation";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { readAuthenticatedUserHomePageContent } from "../utils/userUiPreferences";
import { getGalleryDisplayTitle } from "../utils/galleryDisplay";
import { withSeededRandomSort } from "../utils/seededRandomSort";
import { VideoCoverImage } from "../components/VideoCoverImage";
import { SortableList } from "../components/SortableList";
import { useExtensions } from "../extensions/ExtensionLoader";
import { useAuth } from "../auth/AuthContext";
import { canAccessExtensionContribution } from "../extensions/extension-permissions";
import { ExtensionErrorBoundary } from "../components/ExtensionErrorBoundary";
import { emitLocationChange, registerNavigationBlocker } from "../router/location";
import { buildSpanSearchRequest } from "./segments/useDerivedSpansQuery";
import { buildRawSegmentListOptions } from "./segments/useRawSegmentsQuery";
import { createDefaultRawSegmentFilter, readRawSegmentListFilter } from "./segments/rawSegmentFilter";
import {
  buildSpanTitle,
  buildRawSegmentTitle,
  formatSegmentCardEyebrow,
  SegmentVideoPreview,
} from "./segments/segmentDisplayUtils";
import type { DerivedSpanItem, RawSegmentItem } from "./segments/types";
import {
  buildAppliedDerivedQuery,
  buildDerivedQueryDescriptor,
  readDerivedSpanQueryFilter,
} from "./segments/derivedQueryCriterion";
import { readMultiIdCriterionDepth, readMultiIdCriterionIds } from "./segments/segmentCriteriaDefinitions";

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

interface ContinueWatchingRowConfig {
  type: "continueWatching";
}

type FrontPageContent = CustomFilter | SavedFilterRow | ContinueWatchingRowConfig;

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

const DEFAULT_CONTENT: FrontPageContent[] = [
  { type: "continueWatching" },
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

function loadContent(): FrontPageContent[] {
  try {
    // Prefer the user's account-stored layout (follows them across browsers); fall back to the
    // browser-local value for signed-out use and as a one-time migration source.
    const stored = readAuthenticatedUserHomePageContent() ?? localStorage.getItem(STORAGE_KEY);
    if (stored) {
      const content = JSON.parse(stored) as FrontPageContent[];
      // Migrate layouts saved before Continue Watching became a customizable row: it used to be
      // hardcoded at the top, so preserve that behavior by inserting it once.
      const migrated = localStorage.getItem(CONTINUE_WATCHING_MIGRATION_KEY) === "true";
      if (!migrated) {
        localStorage.setItem(CONTINUE_WATCHING_MIGRATION_KEY, "true");
        if (!content.some((item) => item.type === "continueWatching")) {
          return [{ type: "continueWatching" }, ...content];
        }
      }
      return content;
    }
  } catch {
    /* ignore */
  }
  return DEFAULT_CONTENT;
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

function contentToWidget(content: FrontPageContent): DashboardWidget {
  if (content.type === "continueWatching") {
    return {
      instanceId: createInstanceId(),
      owner: "cove.core",
      widgetKey: "continue-watching",
      label: "Continue Watching",
      configuration: {},
      presentation: FLOW_PRESENTATION,
    };
  }
  if (content.type === "saved") {
    return {
      instanceId: createInstanceId(),
      owner: "cove.core",
      widgetKey: "collection",
      label: "Saved filter",
      configuration: { source: "saved", savedFilterId: content.savedFilterId },
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
  if (widget.widgetKey === "continue-watching") return { type: "continueWatching" };
  if (widget.widgetKey !== "collection" || !widget.configuration || typeof widget.configuration !== "object")
    return null;
  const config = widget.configuration as Record<string, unknown>;
  if (config.source === "saved" && typeof config.savedFilterId === "number") {
    return { type: "saved", savedFilterId: config.savedFilterId };
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

// ─── Home Page Component ─────────────────────────────────────────────────────

interface Props {
  onNavigate: (r: any) => void;
  dashboardId?: number;
}

export function HomePage({ onNavigate, dashboardId }: Props) {
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const [editingDashboard, setEditingDashboard] = useState<{ id: number; selectName: boolean } | null>(null);
  const principalKey = user ? `${user.kind}:${user.id}` : "anonymous";
  const legacyWidgets = useMemo(() => loadContent().map(contentToWidget), [principalKey]);
  const dashboardQuery = useQuery({
    queryKey: ["dashboard-page", principalKey, dashboardId ?? "default"],
    queryFn: async () => {
      try {
        await dashboards.bootstrap(legacyWidgets);
        const list = await dashboards.list();
        const requested =
          dashboardId == null
            ? (list.find((item) => item.isDefault) ?? list[0])
            : list.find((item) => item.id === dashboardId);
        const fallback = list.find((item) => item.isDefault) ?? list[0];
        if (!fallback) throw new Error("No dashboard is available.");
        return {
          list,
          dashboard: await dashboards.get((requested ?? fallback).id),
          missingRequested: dashboardId != null && !requested,
          readOnly: false,
        };
      } catch (error) {
        // Anonymous and share-link principals have no personal storage. Preserve their existing
        // home experience as a local, read-only standard dashboard.
        if (!(error instanceof Error) || !error.message.includes("API Error 401")) throw error;
        const standard: Dashboard = {
          id: 0,
          name: "Standard",
          isDefault: true,
          version: 1,
          createdAt: "",
          updatedAt: "",
          widgets: legacyWidgets,
        };
        return { list: [standard], dashboard: standard, missingRequested: dashboardId != null, readOnly: true };
      }
    },
  });

  useEffect(() => {
    if (dashboardQuery.data?.missingRequested) onNavigate({ page: "home" });
  }, [dashboardQuery.data?.missingRequested, onNavigate]);

  useEffect(() => setEditingDashboard(null), [principalKey]);

  const loadedDashboardId = dashboardQuery.data?.dashboard.id;
  useEffect(() => {
    if (loadedDashboardId != null) {
      setEditingDashboard((current) => (current == null || current.id === loadedDashboardId ? current : null));
    }
  }, [loadedDashboardId]);

  const refresh = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: ["dashboard-page"] });
  }, [queryClient]);

  if (dashboardQuery.isLoading) {
    return (
      <div className="flex min-h-[35vh] items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
      </div>
    );
  }
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
      <div className={presentation === "canvas" ? "min-w-0" : "space-y-5"}>
        {dashboard.widgets.map((widget) => (
          <DashboardWidgetHost
            key={widget.instanceId}
            dashboardId={dashboard.id}
            principalKey={principalKey}
            widget={widget}
            onNavigate={onNavigate}
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
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <h1 className="text-2xl font-semibold text-foreground">Cove</h1>
          <select
            aria-label="Dashboard"
            value={dashboard.id}
            disabled={readOnly || creating}
            onChange={(event) => {
              const selected = items.find((item) => item.id === Number(event.target.value));
              onNavigate(selected?.isDefault ? { page: "home" } : { page: "dashboard", id: selected?.id });
            }}
            className="min-w-40 rounded-md border border-border bg-card px-3 py-2 text-sm text-foreground"
          >
            {items.map((item) => (
              <option key={item.id} value={item.id}>
                {item.name}
              </option>
            ))}
          </select>
        </div>
        {!readOnly ? (
          <div className="flex gap-2">
            <button
              disabled={creating}
              onClick={createDashboard}
              className="rounded-md border border-accent/60 px-3 py-2 text-sm text-accent hover:bg-accent/10 disabled:opacity-50"
            >
              <Plus className="mr-1 inline h-4 w-4" />
              {creating ? "Creating…" : "New Dashboard"}
            </button>
            <button
              disabled={creating}
              onClick={onEdit}
              className="rounded-md border border-border bg-card px-3 py-2 text-sm text-foreground hover:border-accent/60 disabled:opacity-50"
            >
              <Settings2 className="mr-1 inline h-4 w-4" />
              Customize
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

function DashboardWidgetHost({
  dashboardId,
  principalKey,
  widget,
  onNavigate,
  editing = false,
}: {
  dashboardId: number;
  principalKey: string;
  widget: DashboardWidget;
  onNavigate: (route: any) => void;
  editing?: boolean;
}) {
  const content = widgetToContent(widget);
  if (content) {
    return (
      <div style={{ containerType: "inline-size" }}>
        {content.type === "continueWatching" ? (
          <ContinueWatchingRow principalKey={principalKey} onNavigate={onNavigate} />
        ) : (
          <RecommendationRow principalKey={principalKey} content={content} onNavigate={onNavigate} editing={editing} />
        )}
      </div>
    );
  }

  return <ExtensionDashboardWidgetHost dashboardId={dashboardId} widget={widget} onNavigate={onNavigate} />;
}

function ExtensionDashboardWidgetHost({
  dashboardId,
  widget,
  onNavigate,
}: {
  dashboardId: number;
  widget: DashboardWidget;
  onNavigate: (route: any) => void;
}) {
  const { manifest, resolveComponent, getExtensionRevision } = useExtensions();
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
  if (!definition || !Component) return <UnavailableWidget widget={widget} />;

  return (
    <div
      style={{ containerType: "inline-size" }}
      className={`dashboard-widget-container dashboard-widget-${presentation}`}
      data-widget-presentation={presentation}
    >
      <ExtensionErrorBoundary
        extensionId={definition.extensionId}
        resetKey={`${widget.instanceId}:${getExtensionRevision(definition.extensionId)}`}
        fallback={<UnavailableWidget widget={widget} failed />}
      >
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
  const principalKey = user ? `${user.kind}:${user.id}` : "anonymous";
  const { data: allSavedFilters } = useQuery({
    queryKey: ["saved-filters-all", "dashboard", user ? `${user.kind}:${user.id}` : "anonymous"],
    queryFn: () => savedFilters.list(),
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
      window.history.pushState(editorHistoryState.current, "", editorUrl.current);
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

  useEffect(() => {
    if (!busy) return;
    setShowCatalog(false);
    setConfiguringId(null);
  }, [busy]);

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
  if (widget.owner === "cove.core") return widget.widgetKey !== "continue-watching";
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
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

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
      onCloseRef.current();
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
  extensionDefinitions,
  disabled,
  onAdd,
  onClose,
}: {
  currentWidgets: DashboardWidget[];
  savedFilters: SavedFilter[];
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
  const builtInItems = [
    ...(!currentWidgets.some((widget) => widget.owner === "cove.core" && widget.widgetKey === "continue-watching")
      ? [
          {
            key: "continue-watching",
            label: "Continue Watching",
            description: hasCanvasWidget ? flowConflictDescription : "Resume unfinished media.",
            disabled: disabled || hasCanvasWidget,
            onClick: () => onAdd(contentToWidget({ type: "continueWatching" })),
          },
        ]
      : []),
    ...PREMADE_FILTERS.map((filter) => ({
      key: `${filter.mode}:${filter.sortBy}:${filter.header}`,
      label: filter.header,
      description: hasCanvasWidget ? flowConflictDescription : `Collection · ${filter.mode}`,
      disabled: disabled || hasCanvasWidget,
      onClick: () => addPremade(filter),
    })),
  ].filter((item) => matchesSearch(item.label, item.description));
  const savedFilterItems = supportedSavedFilters
    .map((filter) => ({
      key: `saved:${filter.id}`,
      label: filter.name,
      description: hasCanvasWidget ? flowConflictDescription : `Saved filter · ${filter.mode}`,
      disabled: disabled || hasCanvasWidget,
      onClick: () => onAdd(contentToWidget({ type: "saved", savedFilterId: filter.id })),
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
  const hasMatches = builtInItems.length + savedFilterItems.length + extensionItems.length > 0;
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

function ContinueWatchingRow({ principalKey, onNavigate }: { principalKey: string; onNavigate: (r: any) => void }) {
  const groupQuery = useQuery({
    queryKey: ["front-page-continue-watching-group", principalKey],
    queryFn: () => groups.find({ page: 1, perPage: 100, sort: "name", direction: "asc" }),
  });
  const groupData = groupQuery.data;
  const continueGroup = groupData?.items.find((group) => group.querySourceKey === "continue-watching");
  const itemQuery = useQuery({
    queryKey: ["front-page-continue-watching", principalKey, continueGroup?.id],
    queryFn: () => groups.items.page(continueGroup!.id, { page: 1, perPage: 12 }),
    enabled: !!continueGroup,
  });
  if (groupQuery.isError)
    return (
      <WidgetLoadError
        label="Continue Watching"
        error={groupQuery.error}
        onRetry={() => {
          void groupQuery.refetch();
        }}
      />
    );
  if (itemQuery.isError)
    return (
      <WidgetLoadError
        label="Continue Watching"
        error={itemQuery.error}
        onRetry={() => {
          void itemQuery.refetch();
        }}
      />
    );
  const { data: itemPage, isLoading } = itemQuery;
  const playableItems = itemPage?.items ?? [];
  if (!isLoading && playableItems.length === 0) return null;

  return (
    <RecommendationRowShell
      header="Continue Watching"
      viewAllPage="group"
      viewAllId={continueGroup!.id}
      onNavigate={onNavigate}
      loading={isLoading}
      count={playableItems.length}
    >
      {playableItems.map((item) => (
        <ContinueWatchingCard key={`${item.groupId}-${item.id}`} item={item} onNavigate={onNavigate} />
      ))}
    </RecommendationRowShell>
  );
}

function ContinueWatchingCard({
  item,
  onNavigate,
}: {
  item: {
    hostType?: string;
    hostId?: number;
    videoId?: number | null;
    videoTitle?: string;
    title?: string;
    startSec?: number;
  };
  onNavigate: (r: any) => void;
}) {
  const hostType = item.hostType ?? "video";
  const hostId = item.hostId ?? item.videoId ?? 0;
  const videoId = item.videoId ?? (hostType === "video" ? hostId : 0);
  const title = item.title || item.videoTitle || "Untitled";
  const route =
    hostType === "audio"
      ? { page: "audio", id: hostId }
      : hostType === "segment"
        ? { page: "segment", id: hostId }
        : // Only pass an explicit seekTo when we actually have a position (segments carry startSec).
          // Continue-watching video items have no startSec, so omit it and let VideoDetailPage resume
          // from the engagement resumeTime — passing seekTo: 0 would force playback back to the start.
          item.startSec && item.startSec > 0
          ? { page: "video", id: videoId, seekTo: item.startSec }
          : { page: "video", id: videoId };
  const linkProps = createRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate(route));
  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[220px] cursor-pointer overflow-hidden rounded border border-border bg-card transition-colors hover:border-accent/50"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-black">
        {videoId > 0 ? (
          <VideoCoverImage
            src={`/api/stream/video/${videoId}/screenshot`}
            alt={title}
            className="h-full w-full object-cover"
            fallbackClassName="video-recommendation-cover-fallback"
            loading="lazy"
          />
        ) : (
          <div className="flex h-full w-full items-center justify-center text-accent">
            {hostType === "audio" ? <Headphones className="h-10 w-10" /> : <Layers className="h-10 w-10" />}
          </div>
        )}
        <div className="absolute bottom-1 left-1 rounded bg-black/70 px-1.5 py-0.5 text-xs text-white">Resume</div>
      </div>
      <div className="px-2 py-1.5">
        <p className="truncate text-sm font-medium text-foreground">{title}</p>
      </div>
    </a>
  );
}

// ─── Recommendation Row (dispatcher) ────────────────────────────────────────

function RecommendationRow({
  principalKey,
  content,
  onNavigate,
  editing = false,
}: {
  principalKey: string;
  content: FrontPageContent;
  onNavigate: (r: any) => void;
  editing?: boolean;
}) {
  if (content.type === "continueWatching") {
    return <ContinueWatchingRow principalKey={principalKey} onNavigate={onNavigate} />;
  }
  if (content.type === "saved") {
    return (
      <SavedFilterRecommendationRow
        principalKey={principalKey}
        savedFilterId={content.savedFilterId}
        onNavigate={onNavigate}
        editing={editing}
      />
    );
  }
  return <CustomFilterRecommendationRow filter={content} onNavigate={onNavigate} />;
}

// ─── Custom Filter Row ──────────────────────────────────────────────────────

function CustomFilterRecommendationRow({ filter, onNavigate }: { filter: CustomFilter; onNavigate: (r: any) => void }) {
  const findFilter = useMemo(
    () => withSeededRandomSort({}, { perPage: 25, sort: filter.sortBy, direction: filter.direction }),
    [filter],
  );

  const fetchFn = useMemo((): (() => Promise<any>) => {
    switch (filter.mode) {
      case "videos":
        return () => videos.find(findFilter);
      case "performers":
        return () => performers.find(findFilter);
      case "studios":
        return () => studios.find(findFilter);
      case "tags":
        return () => tags.find(findFilter);
      case "galleries":
        return () => galleries.find(findFilter);
      case "groups":
        return () => groups.find(findFilter);
      default:
        return () => Promise.resolve({ items: [], totalCount: 0 });
    }
  }, [filter.mode, findFilter]);

  const query = useQuery<any>({
    queryKey: ["front-page", filter.mode, findFilter],
    queryFn: fetchFn,
  });

  const { data, isLoading } = query;
  const items = data?.items ?? [];
  const engagementHostType = getRecommendationEngagementHostType(filter.mode);
  const { engagementById } = useEntityEngagementBatch(
    engagementHostType ?? "video",
    engagementHostType ? items.map((item: any) => item.id) : [],
  );
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
      count={items.length}
    >
      {items.map((item: any) => (
        <EntityCard
          key={item.id}
          item={item}
          engagement={engagementById.get(item.id)}
          mode={filter.mode}
          onNavigate={onNavigate}
        />
      ))}
    </RecommendationRowShell>
  );
}

// ─── Saved Filter Row ───────────────────────────────────────────────────────

function SavedFilterRecommendationRow({
  principalKey,
  savedFilterId,
  onNavigate,
  editing = false,
}: {
  principalKey: string;
  savedFilterId: number;
  onNavigate: (r: any) => void;
  editing?: boolean;
}) {
  const filterQuery = useQuery({
    queryKey: ["saved-filter", principalKey, savedFilterId],
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
  const findFilter = useMemo((): FindFilter | undefined => {
    if (!mode) return undefined;
    return withSeededRandomSort(
      {},
      {
        ...parsedFilter,
        page: 1,
        perPage: 25,
        sort: parsedFilter.sort ?? DEFAULT_SORT_BY_MODE[mode],
        direction: parsedFilter.direction ?? "desc",
      },
    );
  }, [mode, parsedFilter]);

  const fetchFn = useMemo((): (() => Promise<any>) => {
    if (!mode) return () => Promise.resolve({ items: [], totalCount: 0 });
    const fetchMap: Record<string, () => Promise<any>> = {
      videos: hasObjectFilter
        ? () => videos.findFiltered({ findFilter, objectFilter: parsedObjectFilter })
        : () => videos.find(findFilter),
      performers: hasObjectFilter
        ? () => performers.findFiltered({ findFilter, objectFilter: parsedObjectFilter })
        : () => performers.find(findFilter),
      studios: hasObjectFilter
        ? () => studios.findFiltered({ findFilter, objectFilter: parsedObjectFilter })
        : () => studios.find(findFilter),
      tags: hasObjectFilter
        ? () => tags.findFiltered({ findFilter, objectFilter: parsedObjectFilter })
        : () => tags.find(findFilter),
      galleries: hasObjectFilter
        ? () => galleries.findFiltered({ findFilter, objectFilter: parsedObjectFilter })
        : () => galleries.find(findFilter),
      groups: hasObjectFilter
        ? () => groups.findFiltered({ findFilter, objectFilter: parsedObjectFilter })
        : () => groups.find(findFilter),
      audios: hasObjectFilter
        ? () => audios.findFiltered({ findFilter, objectFilter: parsedObjectFilter })
        : () => audios.find(findFilter),
      texts: hasObjectFilter
        ? () => texts.findFiltered({ findFilter, objectFilter: parsedObjectFilter })
        : () => texts.find(findFilter),
      segments: async () => {
        if (segmentProfileId == null) return { items: [], totalCount: 0 };
        const criteria = readRawSegmentListFilter(parsedObjectFilter ?? {});
        const videoTagIds = readMultiIdCriterionIds(parsedObjectFilter?.videoTagsCriterion);
        const videoTagDepth = readMultiIdCriterionDepth(parsedObjectFilter?.videoTagsCriterion);
        const rawTagDepth = readMultiIdCriterionDepth(parsedObjectFilter?.rawTagsCriterion);
        const derivedFilter = readDerivedSpanQueryFilter(parsedObjectFilter?.derivedSpanQuery);
        const performerIds = Array.from(new Set(derivedFilter.operands.flatMap((operand) => operand.performerIds)));
        const performerFaceEntries = await Promise.all(
          performerIds.map(async (performerId) => {
            const response = await faces.list({ performerId, merged: false, page: 1, perPage: 200 });
            return [performerId, response.items.map((face) => face.id)] as const;
          }),
        );
        const appliedQuery = buildAppliedDerivedQuery(derivedFilter, new Map(performerFaceEntries));
        const derivedQueryDescriptor = buildDerivedQueryDescriptor(derivedFilter);
        const response = await segmentSpans.search(
          buildSpanSearchRequest({
            activeProfileId: segmentProfileId,
            pageNumber: 1,
            perPage: 25,
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
          }),
        );
        return {
          ...response,
          items: response.items.map<DerivedSpanItem>((item) => ({
            id: `${item.videoId}:${item.span.spanKey}`,
            key: `${item.videoId}:${item.span.spanKey}`,
            kind: derivedQueryDescriptor ? "derivedQuery" : "profile",
            videoId: item.videoId,
            videoTitle: item.videoTitle ?? `Video #${item.videoId}`,
            videoUpdatedAt: item.videoUpdatedAt,
            span: item.span,
            profileId: item.profileId,
            derivedQuery: appliedQuery ?? undefined,
            derivedQueryDescriptor,
          })),
        };
      },
      rawsegments: async () => {
        const criteria = readRawSegmentListFilter(parsedObjectFilter ?? {});
        const videoTagIds = readMultiIdCriterionIds(parsedObjectFilter?.videoTagsCriterion);
        const videoTagDepth = readMultiIdCriterionDepth(parsedObjectFilter?.videoTagsCriterion);
        const rawTagDepth = readMultiIdCriterionDepth(parsedObjectFilter?.rawTagsCriterion);
        const response = await segmentLibrary.list(
          buildRawSegmentListOptions({
            pageNumber: 1,
            perPage: 25,
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
    return fetchMap[mode] ?? (() => Promise.resolve({ items: [], totalCount: 0 }));
  }, [mode, findFilter, parsedObjectFilter, hasObjectFilter, segmentProfileId]);

  const itemQuery = useQuery<any>({
    queryKey: ["front-page-saved", principalKey, savedFilterId, mode, findFilter, parsedObjectFilter, segmentProfileId],
    queryFn: fetchFn,
    enabled: !!mode,
  });

  const { data, isLoading } = itemQuery;
  const items = (data as any)?.items ?? [];
  const engagementHostType = getRecommendationEngagementHostType(mode ?? undefined);
  const { engagementById } = useEntityEngagementBatch(
    engagementHostType ?? "video",
    engagementHostType ? items.map((item: any) => item.id) : [],
  );
  if (filterQuery.isError)
    return (
      <WidgetLoadError
        label="Saved filter"
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
  if (!filter || !mode) return null;
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
      count={items.length}
    >
      {items.map((item: any) => (
        <EntityCard
          key={item.id}
          item={item}
          engagement={engagementById.get(item.id)}
          mode={mode!}
          onNavigate={onNavigate}
        />
      ))}
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
  count,
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
  count: number;
  children: React.ReactNode;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [canScrollLeft, setCanScrollLeft] = useState(false);
  const [canScrollRight, setCanScrollRight] = useState(false);
  const [currentPage, setCurrentPage] = useState(0);
  const [pageDestinations, setPageDestinations] = useState([0]);

  const updateScrollState = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
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
  }, [updateScrollState, count]);

  const scroll = (dir: "left" | "right") => {
    const el = scrollRef.current;
    if (!el) return;
    const nextPage = Math.max(0, Math.min(pageDestinations.length - 1, currentPage + (dir === "left" ? -1 : 1)));
    el.scrollTo({ left: pageDestinations[nextPage], behavior: "smooth" });
  };

  return (
    <div className="recommendation-row">
      {/* Header */}
      <div className="flex items-center justify-between mb-2 px-1">
        <h2 className="text-base font-semibold text-foreground">{header}</h2>
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
          className="inline-flex min-h-9 items-center rounded-md px-2 text-sm text-muted hover:text-accent sm:min-h-0 sm:px-0 sm:text-xs"
        >
          View All
        </button>
      </div>

      {/* Scrollable cards */}
      <div className="relative group/row">
        {/* Left arrow */}
        {canScrollLeft && (
          <button
            onClick={() => scroll("left")}
            aria-label={`Previous ${header} page`}
            className="absolute left-0 top-0 bottom-0 z-20 w-8 flex items-center justify-center bg-gradient-to-r from-background/90 to-transparent opacity-0 group-hover/row:opacity-100 focus:opacity-100 transition-opacity"
          >
            <ChevronLeft className="w-6 h-6 text-white" />
          </button>
        )}

        <div
          ref={scrollRef}
          className="flex gap-2 overflow-x-auto scrollbar-hide scroll-smooth px-1"
          style={{ scrollSnapType: "x mandatory" }}
        >
          {loading
            ? Array.from({ length: 6 }).map((_, i) => (
                <div key={i} className="flex-shrink-0 w-[200px] aspect-video bg-card rounded animate-pulse" />
              ))
            : children}
        </div>

        {/* Right arrow */}
        {canScrollRight && (
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
      {pageDestinations.length > 1 && (
        <div className="mx-auto flex max-w-full justify-start gap-1.5 overflow-x-auto px-1 mt-2 scrollbar-hide sm:justify-center sm:overflow-visible">
          {pageDestinations.map((destination, i) => (
            <button
              key={destination}
              onClick={() => {
                const el = scrollRef.current;
                if (el) el.scrollTo({ left: destination, behavior: "smooth" });
              }}
              className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full sm:h-1 sm:w-6"
              aria-label={`Go to carousel page ${i + 1}`}
            >
              <span
                className={`h-1.5 w-6 rounded-full transition-colors sm:h-full sm:w-full ${i === currentPage ? "bg-foreground" : "bg-muted/40"}`}
              />
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

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

// ─── Entity Card (renders appropriate card based on mode) ───────────────────

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
  switch (mode) {
    case "videos":
      return <VideoRecommendationCard video={item} engagement={engagement} onNavigate={onNavigate} />;
    case "performers":
      return <PerformerRecommendationCard performer={item} engagement={engagement} onNavigate={onNavigate} />;
    case "studios":
      return <StudioRecommendationCard studio={item} engagement={engagement} onNavigate={onNavigate} />;
    case "tags":
      return <TagRecommendationCard tag={item} onNavigate={onNavigate} />;
    case "galleries":
      return <GalleryRecommendationCard gallery={item} engagement={engagement} onNavigate={onNavigate} />;
    case "groups":
      return <GroupRecommendationCard group={item} engagement={engagement} onNavigate={onNavigate} />;
    case "audios":
      return <AudioRecommendationCard audio={item} engagement={engagement} onNavigate={onNavigate} />;
    case "texts":
      return <TextRecommendationCard text={item} engagement={engagement} onNavigate={onNavigate} />;
    case "segments":
      return <DerivedSegmentRecommendationCard item={item} onNavigate={onNavigate} />;
    case "rawsegments":
      return <RawSegmentRecommendationCard item={item} engagement={engagement} onNavigate={onNavigate} />;
    default:
      return null;
  }
}

function AudioRecommendationCard({
  audio,
  engagement,
  onNavigate,
}: {
  audio: Audio;
  engagement?: EntityEngagement;
  onNavigate: (r: any) => void;
}) {
  const title = audio.title?.trim() || audio.files?.[0]?.basename || "Untitled audio";
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "audio", id: audio.id }, () =>
    onNavigate({ page: "audio", id: audio.id }),
  );
  return (
    <a
      {...linkProps}
      className="flex w-[200px] flex-shrink-0 flex-col overflow-hidden rounded border border-border bg-card hover:border-accent/50"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative flex aspect-video items-center justify-center bg-surface">
        {audio.imagePath ? (
          <img src={audio.imagePath} alt={title} className="h-full w-full object-cover" loading="lazy" />
        ) : (
          <Headphones className="h-10 w-10 text-muted" />
        )}
        <RatingBanner rating={engagement?.rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="truncate text-sm font-medium text-foreground">{title}</p>
        {audio.date ? <p className="text-xs text-muted">{audio.date}</p> : null}
      </div>
    </a>
  );
}

function TextRecommendationCard({
  text,
  engagement,
  onNavigate,
}: {
  text: TextDocument;
  engagement?: EntityEngagement;
  onNavigate: (r: any) => void;
}) {
  const title = text.title?.trim() || text.files?.[0]?.basename || "Untitled text";
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "text", id: text.id }, () =>
    onNavigate({ page: "text", id: text.id }),
  );
  return (
    <a
      {...linkProps}
      className="flex w-[200px] flex-shrink-0 flex-col overflow-hidden rounded border border-border bg-card hover:border-accent/50"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative flex aspect-video items-center justify-center bg-surface">
        {text.imagePath ? (
          <img src={text.imagePath} alt={title} className="h-full w-full object-cover" loading="lazy" />
        ) : (
          <FileText className="h-10 w-10 text-muted" />
        )}
        <RatingBanner rating={engagement?.rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="truncate text-sm font-medium text-foreground">{title}</p>
        {text.date ? <p className="text-xs text-muted">{text.date}</p> : null}
      </div>
    </a>
  );
}

function DerivedSegmentRecommendationCard({
  item,
  onNavigate,
}: {
  item: DerivedSpanItem;
  onNavigate: (r: any) => void;
}) {
  const title = buildSpanTitle(item.span, item.videoTitle);
  const primarySegmentId = item.span.segmentIds[0];
  const route = {
    page: "video-span",
    id: item.videoId,
    spanKey: item.span.spanKey,
    profileId: item.profileId,
    derivedQueryDescriptor: item.derivedQueryDescriptor,
  };
  const linkProps = createRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate(route));
  return (
    <a
      {...linkProps}
      className="flex w-[220px] flex-shrink-0 flex-col overflow-hidden rounded border border-border bg-card hover:border-accent/50"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="aspect-video bg-black">
        <SegmentVideoPreview
          hostId={item.videoId}
          segmentId={primarySegmentId}
          updatedAt={item.videoUpdatedAt}
          startSec={item.span.startSec}
          endSec={item.span.endSec}
          title={title}
          imgClassName="h-full w-full object-cover"
        />
      </div>
      <div className="px-2 py-1.5">
        <p className="truncate text-sm font-medium text-foreground">{title}</p>
        <p className="text-xs text-muted">{formatSegmentCardEyebrow(item.span.startSec, item.span.endSec)}</p>
      </div>
    </a>
  );
}

function RawSegmentRecommendationCard({
  item,
  engagement,
  onNavigate,
}: {
  item: RawSegmentItem;
  engagement?: EntityEngagement;
  onNavigate: (r: any) => void;
}) {
  const title = buildRawSegmentTitle(item);
  const route = { page: "segment", id: item.id };
  const linkProps = createRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate(route));
  return (
    <a
      {...linkProps}
      className="flex w-[220px] flex-shrink-0 flex-col overflow-hidden rounded border border-border bg-card hover:border-accent/50"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-black">
        <SegmentVideoPreview
          hostId={item.hostId}
          segmentId={item.id}
          updatedAt={item.updatedAt}
          startSec={item.startSec}
          endSec={item.endSec}
          title={title}
          imgClassName="h-full w-full object-cover"
        />
        <RatingBanner rating={engagement?.rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="truncate text-sm font-medium text-foreground">{title}</p>
        <p className="text-xs text-muted">{formatSegmentCardEyebrow(item.startSec, item.endSec)}</p>
      </div>
    </a>
  );
}

// ─── Video Card ─────────────────────────────────────────────────────────────

function VideoRecommendationCard({
  video,
  engagement,
  onNavigate,
}: {
  video: Video;
  engagement?: EntityEngagement;
  onNavigate: (r: any) => void;
}) {
  const file = video.files[0];
  const duration = file?.duration ?? 0;
  const resLabel = file ? getResolutionLabel(file.width, file.height) : null;
  const screenshotUrl = videos.screenshotUrl(video.id);
  const screenshotAlt = video.imagePath ? video.title || "" : "";
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "video", id: video.id }, () =>
    onNavigate({ page: "video", id: video.id }),
  );
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[200px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-black">
        <VideoCoverImage
          src={screenshotUrl}
          alt={screenshotAlt}
          className="h-full w-full object-cover"
          fallbackClassName="video-recommendation-cover-fallback"
          loading="lazy"
        />
        {/* Resolution + duration overlay */}
        <div className="absolute bottom-0 right-0 flex items-center gap-0.5 p-1 text-xs text-white">
          {resLabel && <span className="bg-black/70 px-1 py-0.5 rounded font-bold">{resLabel}</span>}
          {duration > 0 && <span className="bg-black/70 px-1 py-0.5 rounded">{formatDuration(duration)}</span>}
        </div>
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">
          {video.title || file?.basename || "Untitled"}
        </p>
        {video.date && <p className="text-xs text-muted">{video.date}</p>}
      </div>
      {/* Bottom stats */}
      <div className="flex items-center gap-2 px-2 pb-1.5 text-xs text-muted">
        {video.tags.length > 0 && (
          <span className="flex items-center gap-0.5">
            <TagIcon className="w-2.5 h-2.5" />
            {video.tags.length}
          </span>
        )}
        {video.performers.length > 0 && (
          <span className="flex items-center gap-0.5">
            <User className="w-2.5 h-2.5" />
            {video.performers.length}
          </span>
        )}
      </div>
    </a>
  );
}

// ─── Performer Card ─────────────────────────────────────────────────────────

function PerformerRecommendationCard({
  performer,
  engagement,
  onNavigate,
}: {
  performer: Performer;
  engagement?: EntityEngagement;
  onNavigate: (r: any) => void;
}) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "performer", id: performer.id }, () =>
    onNavigate({ page: "performer", id: performer.id }),
  );
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[160px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-[2/3] bg-surface">
        {performer.imagePath ? (
          <img src={performer.imagePath} alt={performer.name} className="w-full h-full object-cover" loading="lazy" />
        ) : (
          <div className="w-full h-full flex items-center justify-center">
            <User className="w-10 h-10 text-muted" />
          </div>
        )}
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">{performer.name}</p>
        {performer.disambiguation && <p className="text-xs text-muted truncate">{performer.disambiguation}</p>}
      </div>
    </a>
  );
}

// ─── Studio Card ────────────────────────────────────────────────────────────

function StudioRecommendationCard({
  studio,
  engagement,
  onNavigate,
}: {
  studio: Studio;
  engagement?: EntityEngagement;
  onNavigate: (r: any) => void;
}) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "studio", id: studio.id }, () =>
    onNavigate({ page: "studio", id: studio.id }),
  );
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[200px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-surface flex items-center justify-center p-4">
        {studio.imagePath ? (
          <img src={studio.imagePath} alt={studio.name} className="h-full w-full object-contain" loading="lazy" />
        ) : (
          <Building2 className="w-10 h-10 text-muted" />
        )}
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">{studio.name}</p>
      </div>
    </a>
  );
}

// ─── Tag Card ───────────────────────────────────────────────────────────────

function TagRecommendationCard({ tag, onNavigate }: { tag: Tag; onNavigate: (r: any) => void }) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "tag", id: tag.id }, () =>
    onNavigate({ page: "tag", id: tag.id }),
  );

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[160px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-surface flex items-center justify-center">
        {tag.imagePath ? (
          <img src={tag.imagePath} alt={tag.name} className="max-w-full max-h-full object-contain" loading="lazy" />
        ) : (
          <TagIcon className="w-8 h-8 text-muted" />
        )}
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">{tag.name}</p>
      </div>
    </a>
  );
}

// ─── Gallery Card ───────────────────────────────────────────────────────────

function GalleryRecommendationCard({
  gallery,
  engagement,
  onNavigate,
}: {
  gallery: Gallery;
  engagement?: EntityEngagement;
  onNavigate: (r: any) => void;
}) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "gallery", id: gallery.id }, () =>
    onNavigate({ page: "gallery", id: gallery.id }),
  );
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[200px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-video bg-surface flex items-center justify-center">
        {gallery.coverPath ? (
          <img
            src={`/api/galleries/${gallery.id}/cover`}
            alt={gallery.title || ""}
            className="w-full h-full object-cover"
            loading="lazy"
          />
        ) : (
          <Images className="w-8 h-8 text-muted" />
        )}
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">
          {getGalleryDisplayTitle(gallery)}
        </p>
        {gallery.date && <p className="text-xs text-muted">{gallery.date}</p>}
      </div>
    </a>
  );
}

// ─── Group Card ─────────────────────────────────────────────────────────────

function GroupRecommendationCard({
  group,
  engagement,
  onNavigate,
}: {
  group: Group;
  engagement?: EntityEngagement;
  onNavigate: (r: any) => void;
}) {
  const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "group", id: group.id }, () =>
    onNavigate({ page: "group", id: group.id }),
  );
  const rating = engagement?.rating;

  return (
    <a
      {...linkProps}
      className="flex-shrink-0 w-[160px] cursor-pointer group rounded overflow-hidden bg-card border border-border hover:border-accent/50 transition-colors"
      style={{ scrollSnapAlign: "start" }}
    >
      <div className="relative aspect-[2/3] bg-surface flex items-center justify-center">
        {group.frontImagePath ? (
          <img src={group.frontImagePath} alt={group.name} className="w-full h-full object-cover" loading="lazy" />
        ) : (
          <Clapperboard className="w-8 h-8 text-muted" />
        )}
        <RatingBanner rating={rating} />
      </div>
      <div className="px-2 py-1.5">
        <p className="text-sm font-medium text-foreground truncate group-hover:text-accent">{group.name}</p>
        {group.date && <p className="text-xs text-muted">{group.date}</p>}
      </div>
    </a>
  );
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
