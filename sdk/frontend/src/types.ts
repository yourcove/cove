/**
 * @cove/extension-sdk — Types for the Cove extension system.
 *
 * These types mirror the host app's extension manifest contracts.
 * Extension authors should use these for type-safe development.
 */

// ── Entity types ─────────────────────────────────────────────────────────
export type EntityType = "video" | "performer" | "studio" | "tag" | "gallery" | "image" | "group" | "audio" | "text" | "face" | "segment";

// ── Criterion and custom-field types ─────────────────────────────────────
export type CriterionModifier =
  | "EQUALS" | "NOT_EQUALS" | "GREATER_THAN" | "LESS_THAN"
  | "INCLUDES" | "EXCLUDES" | "INCLUDES_ALL" | "EXCLUDES_ALL"
  | "IS_NULL" | "NOT_NULL" | "BETWEEN" | "NOT_BETWEEN"
  | "MATCHES_REGEX" | "NOT_MATCHES_REGEX";

export type ListCriterionType = "string" | "number" | "date" | "timestamp" | "duration" | "rating" | "multiId" | "enum" | "bool";

export type CustomFieldType =
  | "text" | "longText" | "number" | "boolean" | "date" | "timestamp"
  | "duration" | "percent" | "url" | "enum"
  | "tag" | "performer" | "studio" | "video" | "gallery" | "image" | "group";

// ── Common props passed to extension components ──────────────────────────
/** Props passed to extension components rendered in entity detail tabs. */
export interface EntityTabProps {
  entityId: number;
}

/** Props passed to extension components rendered in slots. */
export interface SlotProps<TContext = Record<string, unknown>> {
  context: TContext;
}

/** Stable extension slot rendered inside Cove's native entity cover editor. */
export const ENTITY_COVER_EDITOR_SLOT = "entity-cover-editor" as const;

/** Stable viewport-level slot for application-wide floating extension UI. */
export const APP_FLOATING_UI_SLOT = "app-floating-ui" as const;

/** Host-owned context supplied to entity cover editor contributions. */
export interface EntityCoverEditorContext {
  entityType: EntityType;
  entityId: number;
  coverKey: "primary" | "front" | "back";
  currentImageUrl?: string | null;
  canEdit: boolean;
}

/** Props passed to extension page components. */
export interface PageProps {
  onNavigate: (route: NavigateTarget) => void;
  params?: Record<string, string>;
}

/** Props passed to extension detail page components. */
export interface DetailPageProps {
  id: number;
  onNavigate: (route: NavigateTarget) => void;
}

/** Navigation target for onNavigate callback. */
export interface NavigateTarget {
  page: string;
  id?: number;
  [key: string]: unknown;
}

export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };

export type DashboardWidgetPresentation = "flow" | "canvas";

/** Props passed to a dashboard widget render component. */
export interface DashboardWidgetProps<TConfiguration extends JsonValue = JsonValue> {
  dashboardId: number;
  instanceId: string;
  configuration: TConfiguration;
  presentation: DashboardWidgetPresentation;
  onNavigate: (route: NavigateTarget) => void;
}

/** Props passed to an optional extension-owned dashboard widget editor. */
export interface DashboardWidgetEditorProps<TConfiguration extends JsonValue = JsonValue> {
  configuration: TConfiguration;
  presentation: DashboardWidgetPresentation;
  onChange: (configuration: TConfiguration) => void;
  onValidityChange: (valid: boolean, message?: string) => void;
}

/** Stable host component target for primary entity media overrides. */
export const ENTITY_MEDIA_TARGET = "entity.media" as const;

/** Host surface on which primary entity media is being rendered. */
export type EntityMediaSurface =
  | "card"
  | "hero"
  | "list"
  | "picker"
  | "recommendation"
  | "dialog"
  | "hover";

/** Object-fit behavior requested by the host media surface. */
export type EntityMediaFit = "cover" | "contain";

/** Props passed to an extension component overriding primary entity media. */
export interface EntityMediaRenderProps {
  entityType: string;
  entityId: number;
  surface: EntityMediaSurface;
  imageUrl?: string | null;
  alt: string;
  fit: EntityMediaFit;
  loading?: "eager" | "lazy";
  className?: string;
  /** Render the next lower-priority override, ending at Cove's native media. */
  renderDefault: () => React.ReactNode;
}

// ── Media-player contribution contracts ───────────────────────────────────────
/** Stable extension slot rendered with Cove's native media-player actions. */
export const MEDIA_PLAYER_ACTIONS_SLOT = "media-player-actions" as const;

/** Stable extension slot positioned over the displayed media content. */
export const MEDIA_PLAYER_OVERLAY_SLOT = "media-player-overlay" as const;

/** Host surface on which a video player extension contribution is rendered. */
export type MediaPlayerSurface = "detail" | "quick-view" | "compilation";

/** Container-relative rectangle occupied by the displayed video after letterboxing. */
export interface MediaPlayerContentRect {
  left: number;
  top: number;
  width: number;
  height: number;
}

/** Native player behavior temporarily suspended by an interactive extension tool. */
export interface MediaPlayerInteractionModeOptions {
  hideNativeControls?: boolean;
  pauseTracking?: boolean;
  pausePlayback?: boolean;
}

/** Live playback state and safe controls passed directly to media-player slot components. */
export interface MediaPlayerExtensionContext {
  hostType: "video";
  hostId: number;
  surface: MediaPlayerSurface;
  currentTime: number;
  duration: number;
  playing: boolean;
  playbackRate?: number;
  intrinsicWidth: number;
  intrinsicHeight: number;
  contentRect: MediaPlayerContentRect;
  play(): Promise<void>;
  pause(): void;
  seek(seconds: number): void;
  setPlaybackRate?(rate: number): void;
  acquireInteractionMode(options?: MediaPlayerInteractionModeOptions): () => void;
}

// ── Filter types ─────────────────────────────────────────────────────────────────────────
export interface FindFilter {
  page?: number;
  perPage?: number;
  sort?: string;
  direction?: "asc" | "desc";
  query?: string;
}

// ── Host list contribution contracts ─────────────────────────────────────
export interface ListFilterOption {
  value: string;
  label: string;
}

export interface ListFilterContribution {
  id: string;
  entityType: EntityType;
  label: string;
  criterionType: ListCriterionType | CustomFieldType;
  extensionId: string;
  /** Namespaced backend predicate resolved by this contribution's owning extension. */
  filterId?: string;
  filterKey?: string;
  customFieldKey?: string;
  customFieldType?: CustomFieldType;
  entityReferenceType?: EntityType;
  modifiers?: CriterionModifier[];
  options?: ListFilterOption[];
  order?: number;
}

export interface ListSortContribution {
  id: string;
  entityType: EntityType;
  label: string;
  extensionId: string;
  sortKey?: string;
  customFieldKey?: string;
  customFieldType?: CustomFieldType;
  order?: number;
}

export interface UIManifestListContributions {
  listFilters?: ListFilterContribution[];
  listSorts?: ListSortContribution[];
}

export type KeyboardShortcutSurface = "global" | "page" | "list" | "detail" | "player" | "viewer" | "overlay" | "local";

export interface KeyboardActionScope {
  surface: KeyboardShortcutSurface;
  page?: string;
  entityType?: EntityType | string;
  tab?: string;
}

export interface KeyboardActionContribution {
  id: string;
  label: string;
  extensionId: string;
  defaultBindings: string[];
  scopes: KeyboardActionScope[];
  description?: string;
  group?: string;
  handlerName?: string;
  apiEndpoint?: string;
  order?: number;
  repeatable?: boolean;
  allowInEditable?: boolean;
  requiredPermission?: string;
}

export interface KeyboardShortcutPresetContribution {
  schemaVersion: 1;
  id: string;
  name: string;
  extensionId: string;
  unmappedActions: "action-defaults" | "unbound";
  bindings: Record<string, string[]>;
  description?: string;
  author?: string;
  version?: string;
  basePresetId?: string;
  order?: number;
}

export interface DashboardWidgetContribution {
  id: string;
  label: string;
  extensionId: string;
  componentName: string;
  editorComponentName?: string;
  description?: string;
  icon?: string;
  defaultConfiguration?: JsonValue;
  allowMultiple?: boolean;
  order?: number;
  requiredPermission?: string;
  requiredPermissions?: string[];
  requiredPermissionMode?: "all" | "any";
  supportedPresentations?: DashboardWidgetPresentation[];
  defaultPresentation?: DashboardWidgetPresentation;
}

// ── Extension registration contract ──────────────────────────────────────

/** Manifest action passed to a bundle-provided action handler. */
export interface ExtensionAction {
  id: string;
  label: string;
  extensionId: string;
  actionType: string;
  entityTypes: string[];
  icon?: string;
  apiEndpoint?: string;
  handlerName?: string;
  order: number;
  pages?: string[];
  suppressSuccessAlert?: boolean;
  requiredPermission?: string;
}

/** Runtime handler for an extension action contribution. */
export type ExtensionActionHandler = (
  action: ExtensionAction,
  payload: Record<string, unknown>,
) => unknown | Promise<unknown>;

/** The default export expected from an extension's JS bundle. */
export interface ExtensionModule {
  /** Map of component name → React component. */
  components?: Record<string, React.FC<any>>;
  /** Map of action handler name → runtime handler. */
  actionHandlers?: Record<string, ExtensionActionHandler>;
  /** Legacy alias for actionHandlers. */
  handlers?: Record<string, ExtensionActionHandler>;
  /** Optional lifecycle hook called after the extension is loaded. May run again after a completed unload cycle. */
  onLoad?: () => void | Promise<void>;
  /** Optional cleanup hook called before unload. Implementations should be idempotent within a load cycle. */
  onUnload?: () => void | Promise<void>;
}
