import { useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import * as signalR from "@microsoft/signalr";
import {
  ChevronDown,
  ChevronRight,
  ChevronUp,
  BookOpen,
  Check,
  Copy,
  Database,
  Download,
  FolderOpen,
  GripVertical,
  HardDrive,
  Info,
  Loader2,
  LogOut,
  MoreHorizontal,
  Monitor,
  Power,
  Plug,
  Plus,
  RefreshCw,
  SearchCode,
  Server,
  Shield,
  Trash2,
  PlayCircle,
  ScrollText,
  Upload,
  History,
  Search,
  Users,
  KeyRound,
  Keyboard,
  FileText,
  Layers,
  UserCog,
  X,
} from "lucide-react";
import { customFields, system, jobs, metadata, database, plugins as pluginsApi, logs as logsApi, tagGroups, auth as authApi, usersApi } from "../api/client";
import { recentChangelog } from "../data/changelog";
import type { ScanOptions, GenerateOptions, CleanGeneratedOptions, ExportOptions, LogEntry, UserRow } from "../api/client";
import type {
  JobInfo,
  Plugin,
  RatingSystemOptions,
  RatingStarPrecision,
  RatingSystemType,
  ScraperPreference,
  ScraperSummary,
  MetadataServer,
  CoveConfig,
  CovePathConfig,
  SystemStatus,
  CustomFieldDefinition,
  CustomFieldEntityType,
  CustomFieldType,
  DownloaderDescriptor,
  DownloaderPathOverrideConfig,
  DependencyInfo,
  ExtensionDependencyImpact,
  ExtensionTutorialTopic,
  IdentifyDefaultsConfig,
  MetadataServerValidationResult,
  TagGroup,
  UserTrackingPreferences,
} from "../api/types";
import { useExtensions } from "../extensions/ExtensionLoader";
import { getScraperSiteKey } from "../components/videoScrapeUtils";
import { useAppConfig } from "../state/AppConfigContext";
import { LOCATION_CHANGE_EVENT, buildCurrentUrl, navigateToUrl } from "../router/location";
import { DisplayProfilesSettingsPanel } from "./settings/DisplayProfilesSettingsPanel";
import { AiDataSettingsPanel } from "./settings/AiDataSettingsPanel";
import { SortableList } from "../components/SortableList";
import { JobCard } from "../components/JobCard";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { PaginationControls } from "../components/ListPage";
import { CheckboxLabel, CollapsibleSection, InfoPair, NumberField, SectionCard, SelectField, TaskCard, TextAreaField, TextField } from "../components/SettingsPrimitives";
import {
  DEFAULT_BATCH_DOWNLOAD_GENERATE_OPTIONS,
  formatBatchDownloadSummary,
  queueImportedUrlDownloads,
  type DownloadSelectionEntity,
} from "../utils/batchDownloads";
import { useAuth } from "../auth/AuthContext";
import { UsersTab, RolesTab, AuditTab, ContentRulesTab, ApiTokensTab, ShareLinksTab } from "./settings/AdminSections";
import { defaultRatingSystemOptions, normalizeRatingOptions } from "../components/Rating";
import { readStoredRatingOptionsOverride, writeStoredRatingOptionsOverride } from "../utils/ratingPreferences";
import { readAuthenticatedUserThemePreferences, supportsServerBackedUiPreferences, updateAuthenticatedUserUiPreferences } from "../utils/userUiPreferences";
import { writeStoredKeybindingOverrides } from "../hooks/useResolvedKeybindingOverrides";
import { KEYBINDING_GROUPS, keybindingDefault, normalizeShortcutEvent, normalizeShortcutSequence } from "../keyboard/keybindings";
import { openTutorialStoryboard } from "../components/TutorialStoryboardDialog";
import { customFieldDefinitionsQueryKey } from "../hooks/useCustomFieldDefinitions";

type LegacySettingsTab = "tasks" | "library" | "interface" | "user-settings" | "display-profiles" | "ai-data" | "security" | "metadata-providers" | "extensions" | "system" | "about";
type BuiltInSettingsTab =
  | LegacySettingsTab
  | "my-account"
  | "my-appearance-theme"
  | "my-theme"
  | "my-playback-viewers"
  | "my-lists-wall"
  | "keyboard-shortcuts"
  | "my-activity-history"
  | "library-paths-storage"
  | "library-scanning"
  | "library-custom-fields"
  | "library-display-profiles"
  | "operations-jobs"
  | "operations-scan-generate"
  | "operations-downloads"
  | "operations-duplicates"
  | "operations-maintenance"
  | "operations-backup-restore"
  | "operations-extension-tasks"
  | "data-sources-scrapers"
  | "data-sources-metadata-servers"
  | "data-sources-identify-batch-defaults"
  | "data-sources-downloader-paths"
  | "data-sources-ai-data"
  | "extensions-installed"
  | "extensions-registry"
  | "extensions-customizations"
  | "security-authentication"
  | "users"
  | "roles"
  | "content-rules"
  | "api-tokens"
  | "share-links"
  | "audit"
  | "server-host-network"
  | "server-ffmpeg-transcoding"
  | "system-info-about"
  | "system-info-runtime-status"
  | "logs";
type SettingsTab = BuiltInSettingsTab | string;
type SettingsTabDefinition = {
  key: SettingsTab;
  label: string;
  icon: typeof FolderOpen;
  order?: number;
  parentTabKey?: SettingsTab;
  description?: string;
  searchKeywords?: string[];
};
type BuiltInSettingsTabDefinition = SettingsTabDefinition & { key: BuiltInSettingsTab };
type SettingsTabGroupKey = "my-settings" | "library" | "operations" | "data-sources" | "extensions" | "security-access" | "server" | "system-info";
type SettingsTabGroupDefinition = { key: SettingsTabGroupKey; label: string; icon: typeof FolderOpen; tabs: BuiltInSettingsTab[] };

type ResolvedTrackingPreferences = {
  enabled: boolean;
  minViewSeconds: number;
  viewCompletionRatio: number;
  minImageDetailViewSeconds: number;
  minDerivedLikeSessionSeconds: number;
  sessionIdleTimeoutSec: number;
};

const defaultTrackingPreferences: ResolvedTrackingPreferences = {
  enabled: true,
  minViewSeconds: 30,
  viewCompletionRatio: 0.9,
  minImageDetailViewSeconds: 5,
  minDerivedLikeSessionSeconds: 60,
  sessionIdleTimeoutSec: 120,
};

function resolveTrackingPreferences(preferences?: UserTrackingPreferences | null): ResolvedTrackingPreferences {
  return {
    enabled: preferences?.enabled ?? defaultTrackingPreferences.enabled,
    minViewSeconds: preferences?.minViewSeconds ?? defaultTrackingPreferences.minViewSeconds,
    viewCompletionRatio: preferences?.viewCompletionRatio ?? defaultTrackingPreferences.viewCompletionRatio,
    minImageDetailViewSeconds: preferences?.minImageDetailViewSeconds ?? defaultTrackingPreferences.minImageDetailViewSeconds,
    minDerivedLikeSessionSeconds: preferences?.minDerivedLikeSessionSeconds ?? defaultTrackingPreferences.minDerivedLikeSessionSeconds,
    sessionIdleTimeoutSec: preferences?.sessionIdleTimeoutSec ?? defaultTrackingPreferences.sessionIdleTimeoutSec,
  };
}

const primaryTabs: BuiltInSettingsTabDefinition[] = [
  { key: "my-account", label: "Account", icon: UserCog },
  { key: "my-appearance-theme", label: "Interface", icon: Monitor },
  { key: "my-theme", label: "Theme", icon: Monitor },
  { key: "my-playback-viewers", label: "Playback & Viewers", icon: PlayCircle },
  { key: "my-lists-wall", label: "Lists & Cards", icon: Layers },
  { key: "keyboard-shortcuts", label: "Keyboard Shortcuts", icon: Keyboard },
  { key: "my-activity-history", label: "Activity & History", icon: History },
  { key: "library-paths-storage", label: "Paths & Storage", icon: FolderOpen },
  { key: "library-scanning", label: "Scanning & Assets", icon: SearchCode },
  { key: "library-custom-fields", label: "Custom Fields", icon: Layers },
  { key: "library-display-profiles", label: "Display Profiles", icon: Layers },
  { key: "operations-jobs", label: "Jobs", icon: PlayCircle },
  { key: "operations-scan-generate", label: "Scan & Generate", icon: RefreshCw },
  { key: "operations-downloads", label: "Downloads", icon: Download },
  { key: "operations-duplicates", label: "Duplicates", icon: Search },
  { key: "operations-maintenance", label: "Maintenance", icon: HardDrive },
  { key: "operations-backup-restore", label: "Backup & Restore", icon: Upload },
  { key: "operations-extension-tasks", label: "Extension Tasks", icon: Plug },
  { key: "data-sources-scrapers", label: "Scrapers", icon: SearchCode },
  { key: "data-sources-metadata-servers", label: "Metadata Servers", icon: Server },
  { key: "data-sources-identify-batch-defaults", label: "Identify & Batch Defaults", icon: FileText },
  { key: "data-sources-downloader-paths", label: "Downloader Paths", icon: Download },
  { key: "data-sources-ai-data", label: "AI Data", icon: Database },
  { key: "extensions-installed", label: "Installed Extensions", icon: Plug, order: 10 },
  { key: "extensions-registry", label: "Discover", icon: Search, order: 90 },
  { key: "extensions-customizations", label: "Customizations (CSS / JS)", icon: FileText, order: 100 },
  { key: "server-host-network", label: "Host & Network", icon: Server },
  { key: "server-ffmpeg-transcoding", label: "FFmpeg & Transcoding", icon: HardDrive },
  { key: "system-info-about", label: "About", icon: Info },
  { key: "system-info-runtime-status", label: "Runtime Status", icon: Server },
  { key: "logs", label: "Logs", icon: ScrollText },
];

const authTabs: BuiltInSettingsTabDefinition[] = [
  { key: "security-authentication", label: "Authentication", icon: Shield },
  { key: "users", label: "Users", icon: Users },
  { key: "roles", label: "Roles", icon: KeyRound },
  { key: "content-rules", label: "Content rules", icon: Shield },
  { key: "api-tokens", label: "API tokens", icon: KeyRound },
  { key: "share-links", label: "Share links", icon: Plug },
  { key: "audit", label: "Audit log", icon: FileText },
];

const tabs: BuiltInSettingsTabDefinition[] = [...primaryTabs, ...authTabs];
const tabByKey = new Map<BuiltInSettingsTab, BuiltInSettingsTabDefinition>(tabs.map((tab) => [tab.key, tab]));
const settingsTabGroups: SettingsTabGroupDefinition[] = [
  { key: "my-settings", label: "My Settings", icon: UserCog, tabs: ["my-account", "my-appearance-theme", "my-theme", "my-playback-viewers", "my-lists-wall", "keyboard-shortcuts", "my-activity-history"] },
  { key: "operations", label: "Operations", icon: PlayCircle, tabs: ["operations-jobs", "operations-scan-generate", "operations-downloads", "operations-duplicates", "operations-maintenance", "operations-backup-restore", "operations-extension-tasks"] },
  { key: "library", label: "Library", icon: FolderOpen, tabs: ["library-paths-storage", "library-scanning", "library-custom-fields", "library-display-profiles"] },
  { key: "data-sources", label: "Data Sources & Data", icon: SearchCode, tabs: ["data-sources-scrapers", "data-sources-metadata-servers", "data-sources-identify-batch-defaults", "data-sources-downloader-paths", "data-sources-ai-data"] },
  { key: "extensions", label: "Extensions", icon: Plug, tabs: ["extensions-installed", "extensions-registry", "extensions-customizations"] },
  { key: "security-access", label: "Security & Access", icon: Shield, tabs: authTabs.map((tab) => tab.key) },
  { key: "server", label: "Server", icon: Server, tabs: ["server-host-network", "server-ffmpeg-transcoding"] },
  { key: "system-info", label: "System Info", icon: Info, tabs: ["system-info-about", "system-info-runtime-status", "logs"] },
];
const settingsGroupKeyByTab = new Map<BuiltInSettingsTab, SettingsTabGroupKey>(
  settingsTabGroups.flatMap((group) => group.tabs.map((tab) => [tab, group.key] as const)),
);
const settingsTabCanonicalPaths: Partial<Record<BuiltInSettingsTab, string>> = {
  "my-account": "/settings/my/account",
  "my-appearance-theme": "/settings/my/interface",
  "my-theme": "/settings/my/theme",
  "my-playback-viewers": "/settings/my/playback-viewers",
  "my-lists-wall": "/settings/my/lists",
  "keyboard-shortcuts": "/settings/my/keyboard-shortcuts",
  "my-activity-history": "/settings/my/activity-history",
  "library-paths-storage": "/settings/library/paths-storage",
  "library-scanning": "/settings/library/scanning",
  "library-custom-fields": "/settings/library/custom-fields",
  "library-display-profiles": "/settings/library/display-profiles",
  "operations-jobs": "/settings/operations/jobs",
  "operations-scan-generate": "/settings/operations/scan-generate",
  "operations-downloads": "/settings/operations/downloads",
  "operations-duplicates": "/settings/operations/duplicates",
  "operations-maintenance": "/settings/operations/maintenance",
  "operations-backup-restore": "/settings/operations/backup-restore",
  "operations-extension-tasks": "/settings/operations/extension-tasks",
  "data-sources-scrapers": "/settings/data-sources/scrapers",
  "data-sources-metadata-servers": "/settings/data-sources/metadata-servers",
  "data-sources-identify-batch-defaults": "/settings/data-sources/identify-batch-defaults",
  "data-sources-downloader-paths": "/settings/data-sources/downloader-paths",
  "data-sources-ai-data": "/settings/data-sources/ai-data",
  "extensions-installed": "/settings/extensions/installed",
  "extensions-registry": "/settings/extensions/registry",
  "extensions-customizations": "/settings/extensions/customizations",
  "security-authentication": "/settings/security-access/authentication",
  users: "/settings/security-access/users",
  roles: "/settings/security-access/roles-permissions",
  "content-rules": "/settings/security-access/content-rules",
  "api-tokens": "/settings/security-access/api-tokens",
  "share-links": "/settings/security-access/share-links",
  audit: "/settings/security-access/audit-log",
  "server-host-network": "/settings/server/host-network",
  "server-ffmpeg-transcoding": "/settings/server/ffmpeg-transcoding",
  "system-info-about": "/settings/system-info/about",
  "system-info-runtime-status": "/settings/system-info/runtime-status",
  logs: "/settings/system-info/logs",
};
const settingsPathAliases: Partial<Record<string, SettingsTab>> = {
  tasks: "operations-jobs",
  library: "library-paths-storage",
  interface: "my-appearance-theme",
  "user-settings": "my-account",
  "display-profiles": "library-display-profiles",
  "ai-data": "data-sources-ai-data",
  security: "security-authentication",
  "metadata-providers": "data-sources-scrapers",
  extensions: "extensions-installed",
  system: "server-host-network",
  about: "system-info-about",
  my: "my-account",
  "my-settings": "my-account",
  "my/account": "my-account",
  "my/appearance-theme": "my-appearance-theme",
  "my/appearance": "my-appearance-theme",
  "my/interface": "my-appearance-theme",
  "my/theme": "my-theme",
  "my/playback-viewers": "my-playback-viewers",
  "my/lists-wall": "my-lists-wall",
  "my/lists": "my-lists-wall",
  "my/keyboard-shortcuts": "keyboard-shortcuts",
  "my/activity-history": "my-activity-history",
  "library/paths-storage": "library-paths-storage",
  "library/scanning": "library-scanning",
  "library/generated-assets": "library-scanning",
  "library/custom-fields": "library-custom-fields",
  "library/display-profiles": "library-display-profiles",
  operations: "operations-jobs",
  "operations/jobs": "operations-jobs",
  "operations/scan-generate": "operations-scan-generate",
  "operations/downloads": "operations-downloads",
  "operations/download-from-file": "operations-downloads",
  "operations/duplicates": "operations-duplicates",
  "operations/duplicate-finder": "operations-duplicates",
  "operations/maintenance": "operations-maintenance",
  "operations/backup-restore": "operations-backup-restore",
  "operations/extension-tasks": "operations-extension-tasks",
  "data-sources": "data-sources-scrapers",
  "data-sources/scrapers": "data-sources-scrapers",
  "data-sources/metadata-servers": "data-sources-metadata-servers",
  "data-sources/identify-batch-defaults": "data-sources-identify-batch-defaults",
  "data-sources/downloader-paths": "data-sources-downloader-paths",
  "data-sources/ai-data": "data-sources-ai-data",
  "extensions/installed": "extensions-installed",
  "extensions/registry": "extensions-registry",
  "extensions/discover": "extensions-registry",
  "extensions/customizations": "extensions-customizations",
  "extensions/custom-css": "extensions-customizations",
  "extensions/custom-javascript": "extensions-customizations",
  "security-access": "security-authentication",
  "security-access/authentication": "security-authentication",
  "security-access/users": "users",
  "security-access/roles-permissions": "roles",
  "security-access/content-rules": "content-rules",
  "security-access/api-tokens": "api-tokens",
  "security-access/share-links": "share-links",
  "security-access/audit-log": "audit",
  server: "server-host-network",
  "server/host-network": "server-host-network",
  "server/ffmpeg-transcoding": "server-ffmpeg-transcoding",
  "server/preview-generation": "library-scanning",
  "server/logging": "logs",
  "server/runtime-shutdown": "system-info-runtime-status",
  "system-info": "system-info-about",
  "system-info/about": "system-info-about",
  "system-info/runtime-status": "system-info-runtime-status",
  "system-info/changelog": "system-info-about",
  "system-info/logs": "logs",
  changelog: "system-info-about",
  "runtime-shutdown": "system-info-runtime-status",
  "server-runtime-shutdown": "system-info-runtime-status",
};
const tabDescriptions: Partial<Record<BuiltInSettingsTab, string>> = {
  "my-account": "Account details and sign-in controls.",
  "my-appearance-theme": "Language, title, favicon, navigation, rating presentation, and interface preferences.",
  "my-theme": "Theme, palette, layout, and visual effect preferences.",
  "my-playback-viewers": "Video player, previews, feed, vertical viewer, and lightbox preferences.",
  "my-lists-wall": "List display, wall behavior, and card media fit.",
  "keyboard-shortcuts": "Shortcut overrides and the full keyboard reference.",
  "my-activity-history": "Activity and engagement preferences for the current account.",
  "library-paths-storage": "Content roots and file extension handling.",
  "library-scanning": "Scan rules, generated asset paths, and preview generation defaults.",
  "library-custom-fields": "Typed metadata fields stored in the library database.",
  "library-display-profiles": "Manage resolved-span display profiles and the rules attached to each profile.",
  "operations-jobs": "Current queue and recent job history.",
  "operations-scan-generate": "Scan library roots and generate supporting media artifacts.",
  "operations-downloads": "Import a URL list from a text file and queue downloads.",
  "operations-duplicates": "Open duplicate detection and cleanup tools.",
  "operations-maintenance": "Clean orphaned records, generated files, imports, and database statistics.",
  "operations-backup-restore": "Database/config backup, restore, import/export, and wipe operations.",
  "operations-extension-tasks": "Run tasks provided by enabled extensions.",
  "data-sources-scrapers": "Legacy YAML scraper directories, scraper preferences, and discovered scrapers.",
  "data-sources-metadata-servers": "MetadataServer endpoint configuration and validation.",
  "data-sources-identify-batch-defaults": "Defaults for Identify and MetadataServer batch dialogs.",
  "data-sources-downloader-paths": "Downloader save-path overrides.",
  "data-sources-ai-data": "Inspect and safely purge AI-produced embeddings, detections, segments, tag sources, and face-owned data.",
  "extensions-installed": "Manage extensions loaded into this instance.",
  "extensions-registry": "Browse and install extensions from the official catalog or a URL package.",
  "extensions-customizations": "Inject custom CSS and JavaScript into the application.",
  "security-authentication": "Authentication requirements and anonymous share-link access.",
  users: "Manage local user accounts and their role assignments.",
  roles: "Define roles and the permissions they grant. Built-in roles are read-only.",
  "content-rules": "Restrict what each role can see or modify per entity kind. Deny rules override allow.",
  "api-tokens": "Long-lived personal access tokens. Scope is intersected with your own permissions.",
  "share-links": "Anonymous, time-limited, optionally password-gated read-only links.",
  audit: "Authentication, authorization, and admin action history.",
  "server-host-network": "Host, port, and runtime listener settings.",
  "server-ffmpeg-transcoding": "FFmpeg binaries, hardware acceleration, and transcode options.",
  "system-info-about": "Version, project information, and release history.",
  "system-info-runtime-status": "Effective runtime values and shutdown control.",
  logs: "Live application logs and server log output settings.",
};

const settingsSearchKeywords: Partial<Record<BuiltInSettingsTab, string[]>> = {
  "my-appearance-theme": ["appearance", "language", "title", "favicon", "navigation", "menu", "ratings", "rating system", "troubleshooting"],
  "my-theme": ["theme", "palette", "colors", "custom colors", "style", "layout", "visual effects"],
  "my-playback-viewers": ["autoplay", "resume", "preview clip", "feed", "vertical viewer", "lightbox", "slideshow", "ab loop"],
  "my-lists-wall": ["lists", "cards", "wall", "image fit", "video preview fit", "cover", "contain"],
  "library-scanning": ["scan", "scanning", "generated assets", "generated path", "cache path", "preview generation", "thumbnails", "md5"],
  "operations-scan-generate": ["scan", "generate", "covers", "thumbnails", "previews", "sprites", "phash", "md5"],
  "operations-downloads": ["download", "download from file", "url file", "batch download", "import urls"],
  "operations-duplicates": ["duplicates", "duplicate finder", "exact duplicate", "cleanup"],
  "operations-maintenance": ["clean", "clean generated", "orphaned", "optimize", "vacuum", "analyse"],
  "operations-backup-restore": ["backup", "restore", "export", "import", "config backup", "wipe", "danger zone"],
  "data-sources-downloader-paths": ["downloader", "save path", "path override", "site override"],
  "system-info-about": ["about", "version", "release history", "changelog", "setup tour"],
  "system-info-runtime-status": ["runtime", "status", "shutdown", "database", "config file", "app directory"],
  logs: ["logs", "tail", "server log level", "filter", "trace", "debug"],
};

const extensionSettingsTabIcons: Record<string, typeof FolderOpen> = {
  database: Database,
  download: Download,
  filetext: FileText,
  folderopen: FolderOpen,
  harddrive: HardDrive,
  history: History,
  info: Info,
  keyboard: Keyboard,
  keyround: KeyRound,
  layers: Layers,
  monitor: Monitor,
  playcircle: PlayCircle,
  plug: Plug,
  scrolltext: ScrollText,
  search: Search,
  searchcode: SearchCode,
  server: Server,
  shield: Shield,
  upload: Upload,
  usercog: UserCog,
  users: Users,
};

function resolveExtensionSettingsTabIcon(iconName?: string): typeof FolderOpen {
  if (!iconName) {
    return Plug;
  }

  const normalized = iconName.replace(/[^a-z0-9]/gi, "").toLowerCase();
  return extensionSettingsTabIcons[normalized] ?? Plug;
}

const SETTINGS_TAB_QUERY_KEY = "tab";
const SETTINGS_NAV_GROUPS_STORAGE_KEY = "cove-settings-nav-groups";
const TASK_SCAN_OPTIONS_KEY = "cove-settings-scan-options";
const TASK_GENERATE_OPTIONS_KEY = "cove-settings-generate-options";
const TASK_DOWNLOAD_IMPORT_OPTIONS_KEY = "cove-settings-download-import-options";
const TASK_DOWNLOAD_IMPORT_CACHE_KEY = "cove-settings-download-import-cache";
const KEYBINDING_CAPTURE_COMMIT_MS = 850;

const DEFAULT_SCAN_OPTIONS: ScanOptions = {
  scanGenerateCovers: true,
  scanGeneratePreviews: false,
  scanGenerateSprites: false,
  scanGeneratePhashes: false,
  scanGenerateMd5: false,
  scanGenerateThumbnails: false,
  scanGenerateImagePhashes: false,
  scanGenerateAudioPhashes: false,
  scanGenerateTextPhashes: false,
  rescan: false,
};

const DEFAULT_GENERATE_OPTIONS: GenerateOptions = {
  thumbnails: true,
  previews: false,
  sprites: false,
  markers: false,
  segmentThumbnails: false,
  segmentPreviews: false,
  phashes: false,
  md5: false,
  imageThumbnails: false,
  imagePhashes: false,
  galleryThumbnails: false,
  overwrite: false,
};

export function readSettingsTabFromUrl(extraAliases: Partial<Record<string, SettingsTab>> = {}): SettingsTab {
  const aliases = { ...settingsPathAliases, ...extraAliases };
  const pathParts = window.location.pathname.split("/").filter(Boolean);
  if (pathParts[0] === "settings") {
    const fullRouteKey = pathParts.slice(1).join("/").toLowerCase();
    if (fullRouteKey) {
      const exactTab = aliases[fullRouteKey];
      if (exactTab) {
        return exactTab;
      }

      // Preserve unknown settings paths so contributed tabs can resolve after
      // extensions finish loading instead of collapsing to a built-in fallback.
      return fullRouteKey;
    }

    for (let length = Math.min(pathParts.length - 1, 4); length >= 1; length--) {
      const routeKey = pathParts.slice(1, 1 + length).join("/").toLowerCase();
      const routeTab = aliases[routeKey];
      if (routeTab) {
        return routeTab;
      }
    }
  }

  const tab = new URLSearchParams(window.location.search).get(SETTINGS_TAB_QUERY_KEY)?.trim().toLowerCase();
  if (tab) {
    return aliases[tab] ?? tab;
  }

  return "library-paths-storage";
}

function readStoredSettingsGroupOpenState(): Partial<Record<SettingsTabGroupKey, boolean>> {
  try {
    const raw = localStorage.getItem(SETTINGS_NAV_GROUPS_STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw);
      if (parsed && typeof parsed === "object") {
        return parsed as Partial<Record<SettingsTabGroupKey, boolean>>;
      }
    }
  } catch {
    // Ignore invalid persisted state and fall back to defaults.
  }
  return {};
}

function createDefaultSettingsGroupOpenState(activeTab: SettingsTab): Record<SettingsTabGroupKey, boolean> {
  const activeGroup = settingsGroupKeyByTab.get(activeTab as BuiltInSettingsTab);
  const stored = readStoredSettingsGroupOpenState();
  return Object.fromEntries(
    settingsTabGroups.map((group) => {
      // Always reveal the group that contains the active tab; otherwise honor
      // the user's last-remembered open/closed state, falling back to the
      // first-run defaults for groups they have never toggled.
      const remembered = stored[group.key];
      const fallback = group.key === "my-settings" || group.key === "system-info";
      return [group.key, group.key === activeGroup || (remembered ?? fallback)];
    }),
  ) as Record<SettingsTabGroupKey, boolean>;
}

export function resolveVisibleSettingsTab(
  activeTab: SettingsTab,
  visibleTabs: Array<{ key: SettingsTab }>,
  fallback: SettingsTab = "about",
): SettingsTab {
  if (visibleTabs.some((tab) => tab.key === activeTab)) {
    return activeTab;
  }

  return visibleTabs[0]?.key ?? fallback;
}

function loadStoredTaskOptions<T extends object>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    if (!raw) {
      return fallback;
    }

    const parsed = JSON.parse(raw);
    if (parsed && typeof parsed === "object") {
      return { ...fallback, ...parsed } as T;
    }
  } catch {
    // Ignore invalid persisted state and fall back to defaults.
  }

  return fallback;
}

// Hierarchical folder picker for selective scan/generate. Top-level nodes are the configured library
// roots; expanding a node lazily fetches its subfolders from the server (which only ever returns
// folders at or below a library root), so the user can drill down but never select a folder outside
// their library. Selecting a folder targets that whole subtree.
function LibraryFolderPicker({
  roots,
  selected,
  onToggle,
  emptyHint,
}: {
  roots: string[];
  selected: string[];
  onToggle: (path: string, checked: boolean) => void;
  emptyHint: string;
}) {
  const selectedSet = useMemo(() => new Set(selected), [selected]);
  if (roots.length === 0) {
    return <p className="text-[11px] text-muted">{emptyHint}</p>;
  }
  return (
    <div className="max-h-72 space-y-0.5 overflow-auto rounded-lg border border-border/60 bg-surface/40 p-1.5">
      {roots.map((root) => (
        <LibraryFolderNode
          key={root}
          path={root}
          label={root}
          depth={0}
          hasChildren
          selectedSet={selectedSet}
          onToggle={onToggle}
        />
      ))}
    </div>
  );
}

function LibraryFolderNode({
  path,
  label,
  depth,
  hasChildren,
  selectedSet,
  onToggle,
}: {
  path: string;
  label: string;
  depth: number;
  hasChildren: boolean;
  selectedSet: Set<string>;
  onToggle: (path: string, checked: boolean) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const { data: children, isLoading } = useQuery({
    queryKey: ["library-folders", path],
    queryFn: () => metadata.libraryFolders(path),
    enabled: expanded && hasChildren,
  });
  const indent = depth * 16 + 4;
  return (
    <div>
      <div className="flex items-center gap-1.5 rounded px-1 py-0.5 hover:bg-surface/70" style={{ paddingLeft: indent }}>
        {hasChildren ? (
          <button
            type="button"
            onClick={() => setExpanded((value) => !value)}
            className="text-muted hover:text-foreground"
            aria-label={expanded ? "Collapse folder" : "Expand folder"}
          >
            {expanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
          </button>
        ) : (
          <span className="inline-block w-3.5" />
        )}
        <label className="flex min-w-0 cursor-pointer items-center gap-1.5">
          <input
            type="checkbox"
            checked={selectedSet.has(path)}
            onChange={(event) => onToggle(path, event.target.checked)}
            className="rounded border-border"
          />
          <span className="truncate text-xs text-foreground" title={path}>{label}</span>
        </label>
      </div>
      {expanded && hasChildren && (
        <div>
          {isLoading ? (
            <p className="text-[11px] text-muted" style={{ paddingLeft: indent + 38 }}>Loading…</p>
          ) : (children ?? []).length === 0 ? (
            <p className="text-[11px] text-muted" style={{ paddingLeft: indent + 38 }}>No subfolders</p>
          ) : (
            (children ?? []).map((child) => (
              <LibraryFolderNode
                key={child.path}
                path={child.path}
                label={child.name}
                depth={depth + 1}
                hasChildren={child.hasChildren}
                selectedSet={selectedSet}
                onToggle={onToggle}
              />
            ))
          )}
        </div>
      )}
    </div>
  );
}

type ExtensionDependencyCandidate = {
  id: string;
  name: string;
  version?: string;
  enabled?: boolean;
  kind?: string;
  source?: string;
  dependencies: Record<string, string>;
};

type PendingExtensionInstall = {
  extensionId: string;
  version: string;
  name?: string;
  dependencies: DependencyInfo[];
};

type PendingExtensionUninstall = {
  id: string;
  name: string;
  source: "native" | "legacy";
  dependents: ExtensionDependencyImpact[];
  confirmedDependents?: boolean;
};

function getTransitiveExtensionDependents<T extends ExtensionDependencyCandidate>(extensions: T[], extensionId: string): T[] {
  const dependentsByDependency = new Map<string, T[]>();
  const requestedId = extensionId.toLowerCase();

  for (const extension of extensions) {
    if (extension.id.toLowerCase() === requestedId) continue;
    for (const dependencyId of Object.keys(extension.dependencies ?? {})) {
      const key = dependencyId.toLowerCase();
      dependentsByDependency.set(key, [...(dependentsByDependency.get(key) ?? []), extension]);
    }
  }

  const result: T[] = [];
  const seen = new Set<string>([requestedId]);

  const visit = (dependencyId: string) => {
    const directDependents = [...(dependentsByDependency.get(dependencyId.toLowerCase()) ?? [])]
      .sort((left, right) => left.name.localeCompare(right.name));

    for (const dependent of directDependents) {
      const dependentKey = dependent.id.toLowerCase();
      if (seen.has(dependentKey)) continue;
      seen.add(dependentKey);
      visit(dependent.id);
      result.push(dependent);
    }
  };

  visit(extensionId);
  return result;
}

function toExtensionDependencyImpact(extension: ExtensionDependencyCandidate): ExtensionDependencyImpact {
  return {
    id: extension.id,
    name: extension.name,
    version: extension.version ?? "",
    enabled: extension.enabled ?? false,
    kind: extension.kind ?? "extension",
    source: extension.source ?? "unknown",
  };
}

function summarizeExtensionNames(extensions: Array<{ id: string; name?: string }>): string {
  const labels = extensions.map((extension) => extension.name || extension.id);
  if (labels.length <= 4) return labels.join(", ");
  return `${labels.slice(0, 4).join(", ")}, and ${labels.length - 4} more`;
}

function formatDependencyInstallMessage(pending: PendingExtensionInstall): string {
  const dependencySummary = summarizeExtensionNames(pending.dependencies);
  const extensionName = pending.name || pending.extensionId;
  return `Install ${extensionName} and its required dependencies: ${dependencySummary}?`;
}

function formatDependentUninstallMessage(target: PendingExtensionUninstall): string {
  if (target.source !== "native" || target.dependents.length === 0) {
    return `Uninstall ${target.name}?`;
  }

  const dependentSummary = summarizeExtensionNames(target.dependents);
  return `Uninstall ${target.name} and dependent extension${target.dependents.length === 1 ? "" : "s"}: ${dependentSummary}? If you cancel, nothing will be uninstalled.`;
}

const languageOptions = [
  { value: "en-US", label: "English (United States)" },
  { value: "en-GB", label: "English (United Kingdom)" },
  { value: "de-DE", label: "Deutsch" },
  { value: "fr-FR", label: "Francais" },
  { value: "es-ES", label: "Espanol" },
  { value: "it-IT", label: "Italiano" },
  { value: "ja-JP", label: "Japanese" },
  { value: "ko-KR", label: "Korean" },
  { value: "nl-NL", label: "Nederlands" },
  { value: "pl-PL", label: "Polski" },
  { value: "pt-BR", label: "Portugues (Brasil)" },
  { value: "ru-RU", label: "Russian" },
  { value: "sv-SE", label: "Svenska" },
  { value: "zh-CN", label: "Chinese (Simplified)" },
  { value: "zh-TW", label: "Chinese (Traditional)" },
];

const menuItems = [
  { value: "videos", label: "Videos" },
  { value: "segments", label: "Segments" },
  { value: "images", label: "Images" },
  { value: "faces", label: "Faces" },
  { value: "performers", label: "Performers" },
  { value: "galleries", label: "Galleries" },
  { value: "studios", label: "Studios" },
  { value: "tags", label: "Tags" },
  { value: "groups", label: "Groups" },
  { value: "audios", label: "Audios" },
  { value: "texts", label: "Texts" },
];

const ratingSystemOptions: { value: RatingSystemType; label: string }[] = [
  { value: "stars", label: "Stars" },
  { value: "decimal", label: "Decimal (0-10.0)" },
];

const starPrecisionOptions: { value: RatingStarPrecision; label: string }[] = [
  { value: "full", label: "Full stars" },
  { value: "half", label: "Half stars" },
  { value: "quarter", label: "Quarter stars" },
  { value: "tenth", label: "Tenth stars" },
];

function emptyPath(): CovePathConfig {
  return { path: "", excludeVideo: false, excludeImage: false, excludeAudio: false, excludeText: false };
}

function emptyDownloaderPathOverride(): DownloaderPathOverrideConfig {
  return { downloaderId: "", site: "", path: "" };
}

function emptyMetadataServer(): MetadataServer {
  return { name: "", endpoint: "", apiKey: "", maxRequestsPerMinute: 240 };
}

function defaultIdentifyDefaults(): IdentifyDefaultsConfig {
  return {
    createTags: true,
    createPerformers: true,
    createStudios: true,
    autoApplyMaxDurationDifferenceSeconds: undefined,
    autoApplyMaxPhashDistance: undefined,
  };
}

function defaultMetadataBatchDefaults() {
  return {
    refreshAlreadyTagged: false,
    createParentStudios: true,
    excludeFields: [] as string[],
  };
}

function defaultScraperPreferences(): ScraperPreference[] {
  return [];
}

const METADATA_BATCH_EXCLUDE_OPTIONS = [
  { id: "name", label: "Name" },
  { id: "description", label: "Description" },
  { id: "disambiguation", label: "Disambiguation" },
  { id: "gender", label: "Gender" },
  { id: "birthdate", label: "Birth date" },
  { id: "deathdate", label: "Death date" },
  { id: "country", label: "Country" },
  { id: "ethnicity", label: "Ethnicity" },
  { id: "eyecolor", label: "Eye color" },
  { id: "haircolor", label: "Hair color" },
  { id: "height", label: "Height" },
  { id: "measurements", label: "Measurements" },
  { id: "faketits", label: "Fake tits" },
  { id: "career", label: "Career dates" },
  { id: "tattoos", label: "Tattoos" },
  { id: "piercings", label: "Piercings" },
  { id: "aliases", label: "Aliases" },
  { id: "urls", label: "URLs" },
  { id: "image", label: "Image" },
  { id: "parent", label: "Parent studio" },
];

const customFieldEntityOptions: { value: CustomFieldEntityType; label: string }[] = [
  { value: "video", label: "Videos" },
  { value: "audio", label: "Audios" },
  { value: "text", label: "Texts" },
  { value: "performer", label: "Performers" },
  { value: "tag", label: "Tags" },
  { value: "studio", label: "Studios" },
  { value: "gallery", label: "Galleries" },
  { value: "image", label: "Images" },
  { value: "group", label: "Groups" },
  { value: "face", label: "Faces" },
];

const customFieldTypeOptions: { value: CustomFieldType; label: string }[] = [
  { value: "text", label: "Text" },
  { value: "number", label: "Number" },
  { value: "boolean", label: "Boolean" },
  { value: "date", label: "Date" },
  { value: "url", label: "URL" },
  { value: "enum", label: "Enum" },
];

function cloneConfig(config: CoveConfig): CoveConfig {
  return JSON.parse(JSON.stringify(config)) as CoveConfig;
}

function buildDebugReport(status: SystemStatus | null | undefined, draft: CoveConfig | null): string {
  const lines: string[] = [];
  lines.push("=== Cove Debug Info ===");
  lines.push(`Generated: ${new Date().toISOString()}`);

  lines.push("");
  lines.push("[Runtime Status]");
  if (status) {
    lines.push(`Version: ${status.version}`);
    lines.push(`Database: ${status.databasePath}`);
    if (status.configFile) lines.push(`Config file: ${status.configFile}`);
    if (status.appDir) lines.push(`App directory: ${status.appDir}`);
  } else {
    lines.push("(unavailable)");
  }

  lines.push("");
  lines.push("[System Information]");
  lines.push(`Browser: ${navigator.userAgent}`);
  lines.push(`Platform: ${navigator.platform}`);
  lines.push(`Screen resolution: ${screen.width}×${screen.height}`);
  lines.push(`Language: ${navigator.language}`);

  if (draft) {
    lines.push("");
    lines.push("[Config Summary]");
    lines.push(`Library paths: ${draft.covePaths.filter((path) => path.path.trim() !== "").length}`);
    lines.push(`Scraper directories: ${draft.scraping.scraperDirectories.filter(Boolean).length}`);
    lines.push(`Metadata Servers: ${draft.scraping.metadataServers.filter((box) => box.endpoint.trim() !== "").length}`);
    lines.push(`Rating system: ${draft.ui.ratingSystemOptions.type}`);
    lines.push(`Authentication: ${draft.security.enabled ? "enabled" : "disabled"}`);
  }

  return lines.join("\n");
}

function CopyDebugInfoButton({ getReport }: { getReport: () => string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(getReport());
          setCopied(true);
          setTimeout(() => setCopied(false), 2000);
        } catch {
          // Clipboard access can be denied; silently ignore.
        }
      }}
      className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-1.5 text-sm text-secondary transition-colors hover:border-accent hover:text-foreground"
      title="Copy system info and runtime status for debugging"
    >
      {copied ? <Check className="h-4 w-4 text-emerald-400" /> : <Copy className="h-4 w-4" />}
      {copied ? "Copied" : "Copy debug info"}
    </button>
  );
}

function linesToList(value: string) {
  return value.split(/\r?\n/);
}

function listToLines(values: string[]) {
  return values.join("\n");
}

function cloneCustomFieldDefinitions(definitions: CustomFieldDefinition[]) {
  return definitions.map((definition) => ({
    ...definition,
    entityTypes: [...definition.entityTypes],
    options: [...definition.options],
  }));
}

function canSyncCustomFieldDefinition(definition: CustomFieldDefinition) {
  return (definition.key.trim() !== "" || definition.label.trim() !== "") && definition.entityTypes.length > 0;
}

function normalizeCustomFieldDefinitionForSync(definition: CustomFieldDefinition, index: number): CustomFieldDefinition {
  return {
    id: definition.id,
    key: definition.key.trim(),
    label: definition.label.trim(),
    type: definition.type,
    entityTypes: [...definition.entityTypes],
    options: definition.options.map((option) => option.trim()).filter(Boolean),
    filterable: definition.filterable,
    sortable: definition.sortable,
    isMultiValue: definition.isMultiValue ?? false,
    displayOrder: definition.displayOrder ?? (index * 10),
  };
}

function mergeSavedCustomFieldDefinitions(savedDefinitions: CustomFieldDefinition[], draftSnapshot: CustomFieldDefinition[]) {
  const normalizedSavedDefinitions = cloneCustomFieldDefinitions(savedDefinitions);
  let savedIndex = 0;

  return draftSnapshot.flatMap((definition) => {
    if (definition.id == null && !canSyncCustomFieldDefinition(definition)) {
      return [{
        ...definition,
        entityTypes: [...definition.entityTypes],
        options: [...definition.options],
      }];
    }

    const savedDefinition = normalizedSavedDefinitions[savedIndex];
    if (!savedDefinition) {
      return [];
    }

    savedIndex += 1;
    return [savedDefinition];
  });
}

function normalizeConfig(config: CoveConfig): CoveConfig {
  return {
    ...config,
    covePaths: config.covePaths.filter((path) => path.path.trim() !== ""),
    downloaderPathOverrides: (config.downloaderPathOverrides ?? [])
      .map((overridePath) => ({
        downloaderId: overridePath.downloaderId.trim(),
        site: overridePath.site?.trim() || undefined,
        path: overridePath.path.trim(),
      }))
      .filter((overridePath, index, items) => {
        if (overridePath.downloaderId === "" || overridePath.path === "") {
          return false;
        }

        const overrideKey = `${overridePath.downloaderId.toLowerCase()}::${overridePath.site?.toLowerCase() ?? ""}`;
        return items.findIndex((candidate) => `${candidate.downloaderId.toLowerCase()}::${candidate.site?.toLowerCase() ?? ""}` === overrideKey) === index;
      }),
    videoExtensions: config.videoExtensions.map((value) => value.trim()).filter(Boolean),
    imageExtensions: config.imageExtensions.map((value) => value.trim()).filter(Boolean),
    galleryExtensions: config.galleryExtensions.map((value) => value.trim()).filter(Boolean),
    audioExtensions: (config.audioExtensions ?? []).map((value) => value.trim()).filter(Boolean),
    textExtensions: (config.textExtensions ?? []).map((value) => value.trim()).filter(Boolean),
    excludePatterns: config.excludePatterns.map((value) => value.trim()).filter(Boolean),
    excludeImagePatterns: config.excludeImagePatterns.map((value) => value.trim()).filter(Boolean),
    excludeGalleryPatterns: config.excludeGalleryPatterns.map((value) => value.trim()).filter(Boolean),
    galleryCoverRegex: config.galleryCoverRegex.trim(),
    interface: {
      ...config.interface,
      menuItems: config.interface.menuItems.filter(Boolean),
    },
    ui: {
      ...config.ui,
      playerVideoStartPercent: Math.min(95, Math.max(0, config.ui.playerVideoStartPercent ?? 0)),
      playerVideoStartMinDuration: Math.max(0, config.ui.playerVideoStartMinDuration ?? 0),
      imageObjectFit: config.ui.imageObjectFit === "contain" ? "contain" : "cover",
      videoObjectFit: config.ui.videoObjectFit === "contain" ? "contain" : "cover",
      feedVideoSource: config.ui.feedVideoSource === "video" ? "video" : "preview",
      feedVideoSound: config.ui.feedVideoSound ?? false,
      feedVideoStartPercent: Math.min(95, Math.max(0, config.ui.feedVideoStartPercent ?? 0)),
      feedVideoStartMinDuration: Math.max(0, config.ui.feedVideoStartMinDuration ?? 0),
      keybindingOverrides: Object.fromEntries(
        Object.entries(config.ui.keybindingOverrides ?? {})
          .map(([key, value]) => [key.trim(), value.trim()])
          .filter(([key, value]) => key !== "" && value !== "")
      ),
    },
    customFieldDefinitions: (config.customFieldDefinitions ?? [])
      .map((definition) => ({
        ...definition,
        key: definition.key.trim(),
        label: definition.label.trim() || definition.key.trim(),
        type: definition.type,
        entityTypes: definition.entityTypes.filter(Boolean),
        options: definition.options.map((option) => option.trim()).filter(Boolean),
        filterable: definition.filterable,
        sortable: definition.sortable,
      }))
      .filter((definition, index, items) => definition.key !== "" && items.findIndex((candidate) => candidate.key.toLowerCase() === definition.key.toLowerCase()) === index),
    security: {
      ...config.security,
      username: config.security.username?.trim() || undefined,
      newPassword: config.security.newPassword?.trim() || undefined,
    },
    scraping: {
      scraperDirectories: config.scraping.scraperDirectories.map((value) => value.trim()).filter(Boolean),
      metadataServers: config.scraping.metadataServers
        .map((box) => ({
          name: box.name.trim(),
          endpoint: box.endpoint.trim(),
          apiKey: box.apiKey.trim(),
          maxRequestsPerMinute: box.maxRequestsPerMinute,
        }))
        .filter((box) => box.endpoint !== ""),
      scraperPreferences: (config.scraping.scraperPreferences ?? [])
        .map((preference) => ({
          entityType: preference.entityType?.trim().toLowerCase() || undefined,
          site: preference.site.trim().toLowerCase(),
          scraperId: preference.scraperId.trim(),
        }))
        .filter((preference, index, items) => {
          if (preference.site === "" || preference.scraperId === "") {
            return false;
          }

          return items.findIndex((candidate) => (candidate.entityType ?? "") === (preference.entityType ?? "") && candidate.site === preference.site) === index;
        }),
      identifyDefaults: {
        ...defaultIdentifyDefaults(),
        ...config.scraping.identifyDefaults,
      },
      metadataBatchDefaults: {
        ...defaultMetadataBatchDefaults(),
        ...config.scraping.metadataBatchDefaults,
        excludeFields: (config.scraping.metadataBatchDefaults?.excludeFields ?? []).map((value) => value.trim()).filter(Boolean),
      },
    },
  };
}

export function SettingsPage() {
  const { config, status, configLoading, statusLoading } = useAppConfig();
  const { authEnabled, user, hasPermission } = useAuth();
  const {
    getSettingsPanelsForTab,
    resolveComponent,
    settingsTabs: contributedSettingsTabs,
    loaded: extensionsLoaded,
    manifest: extensionManifest,
  } = useExtensions();
  // Built-in nav pages plus any extension-contributed nav pages, so extension pages
  // (e.g. "AI Dome") are reorderable/toggleable here just like built-in pages.
  const navMenuItems = [
    ...menuItems,
    ...(extensionManifest?.pages ?? [])
      .filter((page) => page.showInNav && !menuItems.some((item) => item.value === page.route))
      .map((page) => ({ value: page.route, label: page.label })),
  ];
  const canWriteSystemSettings = hasPermission("system.settings.write");
  const canShutdownSystem = hasPermission("system.shutdown");
  const canReadSegments = hasPermission("segments.read");
  const canWriteSegments = hasPermission("segments.write");
  const canReadJobs = hasPermission("jobs.read");
  const libraryExtensionsPanels = getSettingsPanelsForTab("library", "extensions");
  const libraryStandalonePanels = getSettingsPanelsForTab("library");
  const extensionSettingsPathAliases = useMemo<Partial<Record<string, SettingsTab>>>(() => {
    return Object.fromEntries(
      contributedSettingsTabs.flatMap((tab) => {
        const resolvedKey = tab.key.toLowerCase();
        const shorthandAlias = resolvedKey.startsWith("extensions/")
          ? resolvedKey.slice("extensions/".length)
          : undefined;
        return [resolvedKey, shorthandAlias, ...(tab.aliases ?? []).map((alias) => alias.toLowerCase())]
          .filter((alias): alias is string => Boolean(alias))
          .map((alias) => [alias, resolvedKey] as const);
      }),
    );
  }, [contributedSettingsTabs]);
  const extensionSettingsTabs = useMemo<SettingsTabDefinition[]>(() => {
    return contributedSettingsTabs
      .map((tab) => ({
        key: tab.key.toLowerCase(),
        label: tab.label,
        icon: resolveExtensionSettingsTabIcon(tab.icon),
        order: tab.order,
        parentTabKey: tab.parentTabKey?.toLowerCase(),
        description: tab.description,
        searchKeywords: tab.searchKeywords ?? [],
      }))
      .sort((left, right) => (left.order ?? 100) - (right.order ?? 100) || left.label.localeCompare(right.label));
  }, [contributedSettingsTabs]);
  const extensionSettingsTabByKey = useMemo(
    () => new Map(extensionSettingsTabs.map((tab) => [tab.key, tab] as const)),
    [extensionSettingsTabs],
  );
  const allTabs = useMemo(() => [...tabs, ...extensionSettingsTabs], [extensionSettingsTabs]);
  const allTabByKey = useMemo(
    () => new Map(allTabs.map((tab) => [tab.key, tab] as const)),
    [allTabs],
  );
  const resolvedSettingsGroupKeyByTab = useMemo(() => {
    const nextMap = new Map<SettingsTab, SettingsTabGroupKey>(settingsGroupKeyByTab);
    extensionSettingsTabs.forEach((tab) => nextMap.set(tab.key, "extensions"));
    return nextMap;
  }, [extensionSettingsTabs]);
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = useState<SettingsTab>(() => readSettingsTabFromUrl());
  const [settingsSearch, setSettingsSearch] = useState("");
  const [settingsNavOpen, setSettingsNavOpen] = useState(false);
  const [openSettingsGroups, setOpenSettingsGroups] = useState<Record<SettingsTabGroupKey, boolean>>(() =>
    createDefaultSettingsGroupOpenState(readSettingsTabFromUrl()),
  );
  const [draftState, setDraft] = useState<CoveConfig | null>(null);
  const [customFieldDraftState, setCustomFieldDraft] = useState<CustomFieldDefinition[] | null>(null);
  const [capturingKeybindingId, setCapturingKeybindingId] = useState<string | null>(null);
  const [capturedKeybindingParts, setCapturedKeybindingParts] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const initializedRef = useRef(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const captureCommitTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const capturingKeybindingIdRef = useRef<string | null>(null);
  const capturedKeybindingPartsRef = useRef<string[]>([]);
  const savingRef = useRef(false);
  const [metadataServerValidation, setMetadataServerValidation] = useState<Record<string, MetadataServerValidationResult>>({});

  const { data: loadedCustomFieldDefinitions = [], isLoading: customFieldDefinitionsLoading } = useQuery({
    queryKey: customFieldDefinitionsQueryKey(),
    queryFn: () => customFields.list(),
    enabled: canWriteSystemSettings,
  });

  const securityUsersQ = useQuery<UserRow[]>({
    queryKey: ["admin", "users", "security"],
    queryFn: usersApi.list,
    enabled: activeTab === "security-authentication" && hasPermission("users.read"),
  });

  const revokeSessionsMutation = useMutation({
    mutationFn: authApi.revokeSessions,
    onSuccess: () => window.dispatchEvent(new CustomEvent("cove-auth-required")),
    onError: (err: Error) => setError(err.message),
  });

  const shutdownMutation = useMutation({
    mutationFn: system.shutdown,
    onError: (err: Error) => setError(err.message),
  });

  const { data: availableScrapers = [] } = useQuery({
    queryKey: ["system-scrapers"],
    queryFn: system.listScrapers,
    enabled: canWriteSystemSettings && activeTab === "data-sources-scrapers",
  });

  const { data: availableDownloaders = [] } = useQuery({
    queryKey: ["system-downloaders"],
    queryFn: system.listDownloaders,
    enabled: canWriteSystemSettings && activeTab === "data-sources-downloader-paths",
  });

  const { data: availablePluginTasks = [] } = useQuery({
    queryKey: ["plugins", "tasks"],
    queryFn: pluginsApi.getTasks,
    enabled: canWriteSystemSettings,
  });

  const scraperPreferenceGroups = useMemo(() => {
    const groups = new Map<string, ScraperSummary[]>();

    for (const scraper of availableScrapers) {
      const entityType = scraper.entityType.toLowerCase();
      const sites = new Set([
        ...scraper.urls.map((pattern) => getScraperSiteKey(pattern)),
        ...(scraper.preferenceSites ?? []).map((site) => getScraperSiteKey(site)),
      ].filter((site) => site && site !== "*"));
      for (const site of sites) {
        const groupKey = `${entityType}\u001f${site}`;
        const siteScrapers = groups.get(groupKey) ?? [];
        if (!siteScrapers.some((candidate) => candidate.id === scraper.id)) {
          siteScrapers.push(scraper);
        }
        groups.set(groupKey, siteScrapers);
      }
    }

    return [...groups.entries()]
      .map(([groupKey, scrapers]) => {
        const [entityType, site] = groupKey.split("\u001f", 2);
        return {
          entityType,
          site,
          scrapers: [...scrapers].sort((left, right) => left.name.localeCompare(right.name)),
        };
      })
      .filter((group) => group.scrapers.length > 1)
      .sort((left, right) => left.entityType.localeCompare(right.entityType) || left.site.localeCompare(right.site));
  }, [availableScrapers]);

  const updateScraperPreference = (entityType: string, site: string, scraperId: string) => {
    updateDraft((current) => ({
      ...current,
      scraping: {
        ...current.scraping,
        scraperPreferences: scraperId
          ? [
              ...current.scraping.scraperPreferences.filter((preference) =>
                preference.site !== site || ((preference.entityType ?? "").toLowerCase() !== entityType && (preference.entityType ?? "") !== ""),
              ),
              { entityType, site, scraperId },
            ]
          : current.scraping.scraperPreferences.filter((preference) =>
              preference.site !== site || ((preference.entityType ?? "").toLowerCase() !== entityType && (preference.entityType ?? "") !== ""),
            ),
      },
    }));
  };

  const getSelectedScraperPreferenceId = (entityType: string, site: string) => {
    return draftState?.scraping.scraperPreferences.find((preference) =>
      preference.site === site && (preference.entityType?.toLowerCase() ?? "") === entityType,
    )?.scraperId
      ?? draftState?.scraping.scraperPreferences.find((preference) => preference.site === site && !preference.entityType)?.scraperId
      ?? "";
  };

  useEffect(() => {
    if (!config) {
      return;
    }
    // Skip re-init when config changed due to our own save
    if (savingRef.current) {
      savingRef.current = false;
      return;
    }

    const nextDraft = cloneConfig(config);
    if (nextDraft.covePaths.length === 0) {
      nextDraft.covePaths = [emptyPath()];
    }
    nextDraft.downloaderPathOverrides = nextDraft.downloaderPathOverrides ?? [];
    if (nextDraft.scraping.scraperDirectories.length === 0) {
      nextDraft.scraping.scraperDirectories = [""];
    }
    if (!nextDraft.scraping.identifyDefaults) {
      nextDraft.scraping.identifyDefaults = defaultIdentifyDefaults();
    }
    nextDraft.scraping.scraperPreferences = nextDraft.scraping.scraperPreferences ?? defaultScraperPreferences();
    nextDraft.scraping.metadataBatchDefaults = {
      ...defaultMetadataBatchDefaults(),
      ...nextDraft.scraping.metadataBatchDefaults,
      excludeFields: nextDraft.scraping.metadataBatchDefaults?.excludeFields ?? [],
    };
    if (!nextDraft.ui.ratingSystemOptions) {
      nextDraft.ui.ratingSystemOptions = { type: "stars", starPrecision: "full" };
    }
    nextDraft.ui.playerVideoStartPercent = Math.min(95, Math.max(0, nextDraft.ui.playerVideoStartPercent ?? 0));
    nextDraft.ui.playerVideoStartMinDuration = Math.max(0, nextDraft.ui.playerVideoStartMinDuration ?? 0);
    nextDraft.ui.feedVideoSound = nextDraft.ui.feedVideoSound ?? false;
    nextDraft.ui.feedVideoStartPercent = Math.min(95, Math.max(0, nextDraft.ui.feedVideoStartPercent ?? 0));
    nextDraft.ui.feedVideoStartMinDuration = Math.max(0, nextDraft.ui.feedVideoStartMinDuration ?? 0);

    setDraft(nextDraft);
  }, [config]);

  useEffect(() => {
    if (!canWriteSystemSettings || customFieldDefinitionsLoading) {
      return;
    }

    setCustomFieldDraft((current) => current ?? cloneCustomFieldDefinitions(loadedCustomFieldDefinitions));
  }, [canWriteSystemSettings, customFieldDefinitionsLoading, loadedCustomFieldDefinitions]);

  useEffect(() => {
    const handleLocationChange = () => setActiveTab(readSettingsTabFromUrl(extensionSettingsPathAliases));
    window.addEventListener("popstate", handleLocationChange);
    window.addEventListener(LOCATION_CHANGE_EVENT, handleLocationChange);

    return () => {
      window.removeEventListener("popstate", handleLocationChange);
      window.removeEventListener(LOCATION_CHANGE_EVENT, handleLocationChange);
    };
  }, [extensionSettingsPathAliases]);

  useEffect(() => {
    if (!extensionsLoaded) {
      return;
    }

    setActiveTab((current) => {
      const nextTab = readSettingsTabFromUrl(extensionSettingsPathAliases);
      return current === nextTab ? current : nextTab;
    });
  }, [extensionSettingsPathAliases, extensionsLoaded]);

  useEffect(() => () => {
    if (captureCommitTimerRef.current) {
      clearTimeout(captureCommitTimerRef.current);
    }
  }, []);

  useEffect(() => {
    if (!extensionsLoaded && !tabByKey.has(activeTab as BuiltInSettingsTab)) {
      return;
    }

    const params = new URLSearchParams(window.location.search);
    params.delete(SETTINGS_TAB_QUERY_KEY);
    const pathname = settingsTabCanonicalPaths[activeTab as BuiltInSettingsTab] ?? `/settings/${activeTab}`;

    navigateToUrl(buildCurrentUrl(pathname, params), { replace: true });
  }, [activeTab, extensionsLoaded]);

  const saveMutation = useMutation({
    mutationFn: (nextConfig: CoveConfig) => system.saveConfig(nextConfig),
    onSuccess: (savedConfig) => {
      savingRef.current = true;
      queryClient.setQueriesData({ queryKey: ["system-config"] }, savedConfig);
      queryClient.invalidateQueries({ queryKey: ["system-config"] });
      queryClient.invalidateQueries({ queryKey: ["system-scrapers"] });
      setError(null);
    },
    onError: (err: Error) => setError(err.message),
  });

  const syncCustomFieldsMutation = useMutation({
    mutationFn: ({ definitions }: { definitions: CustomFieldDefinition[]; draftSnapshot: CustomFieldDefinition[] }) => customFields.replaceAll(definitions),
    onSuccess: (savedDefinitions, variables) => {
      setCustomFieldDraft(mergeSavedCustomFieldDefinitions(savedDefinitions, variables.draftSnapshot));
      queryClient.setQueryData(customFieldDefinitionsQueryKey(), savedDefinitions);
      queryClient.invalidateQueries({ queryKey: ["custom-fields"] });
      setError(null);
    },
    onError: (err: Error) => setError(err.message),
  });

  const uploadFaviconMutation = useMutation({
    mutationFn: system.uploadFavicon,
    onSuccess: (result) => {
      updateDraft((current) => ({ ...current, ui: { ...current.ui, faviconPath: result.path } }));
      setError(null);
    },
    onError: (err: Error) => setError(err.message),
  });

  const { data: scrapers = [], isLoading: scrapersLoading, error: scrapersError } = useQuery({
    queryKey: ["system-scrapers"],
    queryFn: system.listScrapers,
    enabled: canWriteSystemSettings && activeTab === "data-sources-scrapers",
  });

  const reloadScrapersMutation = useMutation({
    mutationFn: system.reloadScrapers,
    onSuccess: (nextScrapers) => {
      queryClient.setQueryData(["system-scrapers"], nextScrapers);
    },
  });

  const validateMetadataServerMutation = useMutation({
    mutationFn: ({ index, metadataServer }: { index: number; metadataServer: MetadataServer }) => system.validateMetadataServer(metadataServer),
    onSuccess: (result, variables) => {
      setMetadataServerValidation((current) => ({ ...current, [String(variables.index)]: result }));
    },
    onError: (err: Error, variables) => {
      setMetadataServerValidation((current) => ({
        ...current,
        [String(variables.index)]: { valid: false, status: err.message },
      }));
    },
  });

  const groupedScrapers = useMemo(() => {
    return scrapers.reduce<Record<string, ScraperSummary[]>>((acc, scraper) => {
      if (!acc[scraper.entityType]) {
        acc[scraper.entityType] = [];
      }
      acc[scraper.entityType].push(scraper);
      return acc;
    }, {});
  }, [scrapers]);

  const visibleAuthTabs = useMemo(() => {
    return authTabs.filter((tab) => {
      switch (tab.key) {
        case "security-authentication":
          return hasPermission("system.settings.write");
        case "users":
          return hasPermission("users.read");
        case "roles":
        case "content-rules":
          return hasPermission("roles.read");
        case "api-tokens":
          return authEnabled && !!user && hasPermission("apitokens.write");
        case "share-links":
          return authEnabled && !!user && hasPermission("sharelinks.write");
        case "audit":
          return hasPermission("audit.read");
        default:
          return true;
      }
    });
  }, [authEnabled, hasPermission, user]);

  const visiblePrimaryTabs = useMemo(() => {
    return primaryTabs.filter((tab) => {
      switch (tab.key) {
        case "my-account":
        case "my-appearance-theme":
        case "my-theme":
        case "keyboard-shortcuts":
        case "system-info-about":
        case "system-info-runtime-status":
          return true;
        case "my-playback-viewers":
        case "my-lists-wall":
        case "my-activity-history":
          return canWriteSystemSettings;
        case "logs":
          return canWriteSystemSettings;
        case "library-display-profiles":
          return canReadSegments;
        case "operations-jobs":
          return canWriteSystemSettings || canReadJobs;
        case "operations-extension-tasks":
          return canWriteSystemSettings && availablePluginTasks.length > 0;
        default:
          return canWriteSystemSettings;
      }
    });
  }, [availablePluginTasks.length, canReadJobs, canReadSegments, canWriteSystemSettings]);

  const visibleExtensionSettingsTabs = useMemo(
    () => canWriteSystemSettings ? extensionSettingsTabs : [],
    [canWriteSystemSettings, extensionSettingsTabs],
  );

  const visibleTabs = useMemo(
    () => [...visiblePrimaryTabs, ...visibleExtensionSettingsTabs, ...visibleAuthTabs],
    [visibleAuthTabs, visibleExtensionSettingsTabs, visiblePrimaryTabs],
  );
  const visibleSettingsGroups = useMemo(() => {
    const visibleTabKeys = new Set(visibleTabs.map((tab) => tab.key));

    return settingsTabGroups
      .map((group) => {
        const builtInGroupTabs = group.tabs
          .filter((tabKey) => visibleTabKeys.has(tabKey))
          .map((tabKey) => tabByKey.get(tabKey))
          .filter((tab): tab is BuiltInSettingsTabDefinition => !!tab);

        const mergedTabs = group.key === "extensions"
          ? [...builtInGroupTabs, ...visibleExtensionSettingsTabs]
            .sort((left, right) => (left.order ?? 100) - (right.order ?? 100) || left.label.localeCompare(right.label))
          : builtInGroupTabs;

        return {
          ...group,
          tabs: mergedTabs,
        };
      })
      .filter((group) => group.tabs.length > 0);
  }, [visibleExtensionSettingsTabs, visibleTabs]);

  const settingsSearchResults = useMemo(() => {
    const query = settingsSearch.trim().toLowerCase();
    if (!query) return [];

    return visibleTabs
      .map((tab) => {
        const groupKey = resolvedSettingsGroupKeyByTab.get(tab.key);
        const groupLabel = settingsTabGroups.find((group) => group.key === groupKey)?.label ?? "Settings";
        const searchable = [
          groupLabel,
          tab.label,
          tab.description ?? tabDescriptions[tab.key as BuiltInSettingsTab],
          ...(tab.searchKeywords ?? settingsSearchKeywords[tab.key as BuiltInSettingsTab] ?? []),
        ].filter(Boolean).join(" ").toLowerCase();
        return searchable.includes(query) ? { tab, groupLabel } : null;
      })
      .filter((result): result is { tab: SettingsTabDefinition; groupLabel: string } => !!result)
      .slice(0, 8);
  }, [resolvedSettingsGroupKeyByTab, settingsSearch, visibleTabs]);

  useEffect(() => {
    const activeGroup = resolvedSettingsGroupKeyByTab.get(activeTab);
    if (activeGroup) {
      setOpenSettingsGroups((current) => ({ ...current, [activeGroup]: true }));
    }
  }, [activeTab, resolvedSettingsGroupKeyByTab]);

  // Remember the user's last open/closed state for the settings nav groups.
  useEffect(() => {
    try {
      localStorage.setItem(SETTINGS_NAV_GROUPS_STORAGE_KEY, JSON.stringify(openSettingsGroups));
    } catch {
      // Ignore storage failures (e.g. private mode / quota).
    }
  }, [openSettingsGroups]);

  useEffect(() => {
    if (!extensionsLoaded && !tabByKey.has(activeTab as BuiltInSettingsTab)) {
      return;
    }

    const nextTab = resolveVisibleSettingsTab(activeTab, visibleTabs, canWriteSystemSettings ? "library-paths-storage" : "system-info-about");
    if (nextTab !== activeTab) {
      setActiveTab(nextTab);
    }
  }, [activeTab, canWriteSystemSettings, extensionsLoaded, visibleTabs]);

  // Debounced auto-save: triggers 800ms after draft changes
  useEffect(() => {
    if (!draftState || !canWriteSystemSettings) return;
    // Skip the first render when draft is initialized from config
    if (!initializedRef.current) {
      initializedRef.current = true;
      return;
    }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      saveMutation.mutate(normalizeConfig(draftState));
    }, 800);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [draftState, canWriteSystemSettings]); // eslint-disable-line react-hooks/exhaustive-deps

  if (configLoading || (canWriteSystemSettings && !draftState)) {
    return (
      <div className="flex h-64 items-center justify-center">
        <Loader2 className="h-6 w-6 animate-spin text-muted" />
      </div>
    );
  }

  const updateDraft = (updater: (current: CoveConfig) => CoveConfig) => {
    setDraft((current) => (current ? updater(current) : current));
  };

  const clearKeybindingCaptureTimer = () => {
    if (captureCommitTimerRef.current) {
      clearTimeout(captureCommitTimerRef.current);
      captureCommitTimerRef.current = null;
    }
  };

  const stopKeybindingCapture = () => {
    clearKeybindingCaptureTimer();
    capturingKeybindingIdRef.current = null;
    capturedKeybindingPartsRef.current = [];
    setCapturingKeybindingId(null);
    setCapturedKeybindingParts([]);
  };

  const startKeybindingCapture = (id: string) => {
    clearKeybindingCaptureTimer();
    capturingKeybindingIdRef.current = id;
    capturedKeybindingPartsRef.current = [];
    setCapturingKeybindingId(id);
    setCapturedKeybindingParts([]);
  };

  const persistKeybindingOverrides = (overrides: Record<string, string>) => {
    const normalizedOverrides = Object.fromEntries(
      Object.entries(overrides)
        .map(([key, value]) => [key.trim(), normalizeShortcutSequence(value)] as const)
        .filter(([key, value]) => key.length > 0 && value.length > 0),
    );

    const persistedToAccount = updateAuthenticatedUserUiPreferences((current) => ({
      ...(current ?? {}),
      keybindingOverrides: Object.keys(normalizedOverrides).length > 0 ? normalizedOverrides : null,
    }));

    if (!persistedToAccount) {
      writeStoredKeybindingOverrides(normalizedOverrides);
    }
  };

  const updateKeybindingOverride = (id: string, value: string) => {
    updateDraft((current) => {
      const nextOverrides = { ...(current.ui.keybindingOverrides ?? {}) };
      const normalized = normalizeShortcutSequence(value);
      const defaultShortcut = normalizeShortcutSequence(keybindingDefault(id));
      if (!normalized || normalized === defaultShortcut) {
        delete nextOverrides[id];
      } else {
        nextOverrides[id] = normalized;
      }
      persistKeybindingOverrides(nextOverrides);
      return { ...current, ui: { ...current.ui, keybindingOverrides: nextOverrides } };
    });
  };

  const commitCapturedKeybindingOverride = (id: string, parts: string[]) => {
    const shortcut = normalizeShortcutSequence(parts.join(" "));
    if (shortcut) {
      updateKeybindingOverride(id, shortcut);
    }
    stopKeybindingCapture();
  };

  const scheduleCapturedKeybindingCommit = (id: string, parts: string[]) => {
    clearKeybindingCaptureTimer();
    captureCommitTimerRef.current = setTimeout(() => {
      if (capturingKeybindingIdRef.current === id) {
        commitCapturedKeybindingOverride(id, parts);
      }
    }, KEYBINDING_CAPTURE_COMMIT_MS);
  };

  const captureKeybindingOverride = (id: string, event: ReactKeyboardEvent<HTMLElement>) => {
    if (capturingKeybindingId !== id) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();

    if (event.key === "Escape") {
      stopKeybindingCapture();
      return;
    }

    if (event.key === "Enter") {
      if (capturedKeybindingPartsRef.current.length > 0) {
        commitCapturedKeybindingOverride(id, capturedKeybindingPartsRef.current);
      } else {
        stopKeybindingCapture();
      }
      return;
    }

    if (event.key === "Backspace" || event.key === "Delete") {
      clearKeybindingCaptureTimer();
      capturedKeybindingPartsRef.current = [];
      setCapturedKeybindingParts([]);
      return;
    }

    const shortcut = normalizeShortcutEvent(event);
    if (!shortcut) {
      return;
    }

    const nextParts = [...capturedKeybindingPartsRef.current, shortcut].slice(0, 2);
    capturedKeybindingPartsRef.current = nextParts;
    setCapturedKeybindingParts(nextParts);
    if (nextParts.length >= 2) {
      commitCapturedKeybindingOverride(id, nextParts);
    } else {
      scheduleCapturedKeybindingCommit(id, nextParts);
    }
  };

  const renderExtensionSettingsPanels = (
    tabKey: SettingsTab,
    emptyTitle: string,
    emptyDescription: string,
  ) => {
    const panels = getSettingsPanelsForTab(tabKey);

    if (panels.length === 0) {
      return (
        <SectionCard title={emptyTitle} description={emptyDescription}>
          <p className="text-sm text-secondary">No installed AI extension contributes settings to this page yet.</p>
        </SectionCard>
      );
    }

    return panels.map((panel) => {
      const Component = resolveComponent(panel.componentName);
      if (!Component) return null;
      return (
        <SectionCard
          key={panel.id}
          title={panel.label}
          description={`Settings provided by ${panel.extensionId}.`}
        >
          <Component />
        </SectionCard>
      );
    });
  };

  const commitCustomFieldDraft = (definitions: CustomFieldDefinition[] | null = customFieldDraftState) => {
    if (!definitions || !canWriteSystemSettings) {
      return;
    }

    if (definitions.some((definition) => definition.id != null && !canSyncCustomFieldDefinition(definition))) {
      return;
    }

    const nextDefinitions = definitions
      .filter((definition) => definition.id != null || canSyncCustomFieldDefinition(definition))
      .map((definition, index) => normalizeCustomFieldDefinitionForSync(definition, index));

    syncCustomFieldsMutation.mutate({
      definitions: nextDefinitions,
      draftSnapshot: cloneCustomFieldDefinitions(definitions),
    });
  };

  const updateCustomFieldDefinition = (
    index: number,
    updater: (definition: CustomFieldDefinition) => CustomFieldDefinition,
    options?: { commit?: boolean },
  ) => {
    let nextDefinitions: CustomFieldDefinition[] | null = null;
    setCustomFieldDraft((current) => {
      if (!current) return current;
      const definitions = [...current];
      const existing = definitions[index];
      if (!existing) return current;
      definitions[index] = updater(existing);
      nextDefinitions = definitions;
      return definitions;
    });

    if (options?.commit && nextDefinitions) {
      commitCustomFieldDraft(nextDefinitions);
    }
  };

  const addCustomFieldDefinition = () => {
    setCustomFieldDraft((current) => ([
      ...(current ?? []),
      {
        key: "",
        label: "",
        type: "text",
        entityTypes: ["video"],
        options: [],
        filterable: true,
        sortable: false,
        isMultiValue: false,
      },
    ]));
  };

  const removeCustomFieldDefinition = (index: number) => {
    let nextDefinitions: CustomFieldDefinition[] | null = null;
    setCustomFieldDraft((current) => {
      nextDefinitions = current?.filter((_, candidateIndex) => candidateIndex !== index) ?? null;
      return nextDefinitions;
    });

    commitCustomFieldDraft(nextDefinitions);
  };

  const toggleCustomFieldEntity = (index: number, entityType: CustomFieldEntityType) => {
    updateCustomFieldDefinition(index, (definition) => {
      const currentTypes = definition.entityTypes ?? [];
      const nextTypes = currentTypes.includes(entityType)
        ? currentTypes.filter((candidate) => candidate !== entityType)
        : [...currentTypes, entityType];
      return { ...definition, entityTypes: nextTypes };
    }, { commit: true });
  };

  const draft = draftState as CoveConfig;
  const customFieldDraft = customFieldDraftState ?? [];
  const hasInvalidPersistedCustomFields = customFieldDraft.some((definition) => definition.id != null && !canSyncCustomFieldDefinition(definition));
  const resolvedActiveTab = resolveVisibleSettingsTab(activeTab, visibleTabs, canWriteSystemSettings ? "library-paths-storage" : "system-info-about");
  const activeTabMeta = visibleTabs.find((tab) => tab.key === resolvedActiveTab) ?? allTabByKey.get(resolvedActiveTab);
  const activeExtensionSettingsTab = extensionSettingsTabByKey.get(resolvedActiveTab);
  const activeTabDescription = resolvedActiveTab === "my-appearance-theme" && !canWriteSystemSettings
    ? "Rating and personal display preferences stored in this browser or account."
    : resolvedActiveTab === "my-theme" && !canWriteSystemSettings
    ? "Theme preferences stored in this browser or account."
    : activeExtensionSettingsTab?.description ?? tabDescriptions[resolvedActiveTab as BuiltInSettingsTab] ?? "Settings and runtime controls.";
  const selectSettingsTab = (key: SettingsTab) => {
    setActiveTab(key);
    setSettingsNavOpen(false);
  };

  return (
    <div className="grid min-w-0 gap-4 lg:gap-6 lg:grid-cols-[240px_minmax(0,1fr)]">
      <aside className="min-w-0 rounded-2xl border border-border bg-surface p-2 lg:sticky lg:top-16 lg:max-h-[calc(100vh-5rem)] lg:overflow-y-auto">
        <button
          type="button"
          onClick={() => setSettingsNavOpen((open) => !open)}
          className="flex w-full items-center justify-between gap-3 rounded-xl px-3 py-2 text-left lg:hidden"
        >
          <span className="min-w-0">
            <span className="block text-sm font-semibold text-foreground">Settings</span>
            <span className="block truncate text-xs text-secondary">{activeTabMeta?.label}</span>
          </span>
          {settingsNavOpen ? <ChevronUp className="h-4 w-4 shrink-0 text-secondary" /> : <ChevronDown className="h-4 w-4 shrink-0 text-secondary" />}
        </button>
        <div className="mb-2 hidden px-3 py-2 lg:block">
          <h1 className="text-lg font-semibold text-foreground">Settings</h1>
        </div>
        <div className={[settingsNavOpen ? "block" : "hidden", "lg:block"].join(" ")}>
        <div className="mb-3 px-2">
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
            <input
              type="search"
              value={settingsSearch}
              onChange={(event) => setSettingsSearch(event.target.value)}
              placeholder="Search settings"
              aria-label="Search settings"
              className="w-full rounded-xl border border-border bg-card py-2 pl-9 pr-3 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
            />
          </div>
          {settingsSearch.trim() ? (
            <div className="mt-2 space-y-1 rounded-xl border border-border bg-background p-1">
              {settingsSearchResults.length > 0 ? (
                settingsSearchResults.map(({ tab, groupLabel }) => (
                  <button
                    key={tab.key}
                    type="button"
                    onClick={() => {
                      selectSettingsTab(tab.key);
                      setSettingsSearch("");
                    }}
                    className="flex w-full flex-col rounded-lg px-3 py-2 text-left text-sm transition-colors hover:bg-card hover:text-foreground"
                  >
                    <span className="font-medium text-foreground">{tab.label}</span>
                    <span className="text-xs text-muted">{groupLabel}</span>
                  </button>
                ))
              ) : (
                <p className="px-3 py-2 text-xs text-muted">No settings found.</p>
              )}
            </div>
          ) : null}
        </div>
        <nav className="space-y-1">
          {visibleSettingsGroups.map(({ key: groupKey, label, icon: GroupIcon, tabs: groupTabs }) => {
            const isOpen = openSettingsGroups[groupKey] ?? false;
            const rootTabs = groupTabs.filter((tab) => !tab.parentTabKey || !groupTabs.some((candidate) => candidate.key === tab.parentTabKey));
            return (
              <div key={groupKey} className="pt-1 first:pt-0">
                <button
                  onClick={() => setOpenSettingsGroups((current) => ({ ...current, [groupKey]: !isOpen }))}
                  className="flex w-full items-center gap-2 rounded-xl px-3 py-2 text-left text-sm text-secondary transition-colors hover:bg-card hover:text-foreground"
                >
                  <GroupIcon className="h-4 w-4" />
                  <span className="flex-1">{label}</span>
                  {isOpen ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
                </button>
                {isOpen ? (
                  <div className="mt-1 space-y-1 border-l border-border/60 pl-3 ml-3">
                    {rootTabs.map(({ key, label, icon: Icon }) => {
                        const childTabs = groupTabs.filter((tab) => tab.parentTabKey === key);
                        const isParentActive = childTabs.some((tab) => tab.key === resolvedActiveTab);

                        return (
                          <div key={key}>
                            <button
                              onClick={() => selectSettingsTab(key)}
                              className={`flex w-full items-center gap-2 rounded-xl px-3 py-2 text-left text-sm transition-colors ${
                                resolvedActiveTab === key || isParentActive
                                  ? "bg-card text-foreground shadow-[inset_0_0_0_1px_var(--color-border)]"
                                  : "text-secondary hover:bg-card hover:text-foreground"
                              }`}
                            >
                              <Icon className="h-4 w-4" />
                              <span>{label}</span>
                            </button>
                            {childTabs.length > 0 ? (
                              <div className="mt-1 ml-6 space-y-1 border-l border-border/60 pl-3">
                                {childTabs.map(({ key: childKey, label: childLabel, icon: ChildIcon }) => (
                                  <button
                                    key={childKey}
                                    onClick={() => selectSettingsTab(childKey)}
                                    className={`flex w-full items-center gap-2 rounded-xl px-3 py-2 text-left text-sm transition-colors ${
                                      resolvedActiveTab === childKey
                                        ? "bg-card text-foreground shadow-[inset_0_0_0_1px_var(--color-border)]"
                                        : "text-secondary hover:bg-card hover:text-foreground"
                                    }`}
                                  >
                                    <ChildIcon className="h-4 w-4" />
                                    <span>{childLabel}</span>
                                  </button>
                                ))}
                              </div>
                            ) : null}
                          </div>
                        );
                      })}
                  </div>
                ) : null}
              </div>
            );
          })}
        </nav>
        </div>
      </aside>

      <div className="min-w-0 space-y-5">
        <section className="min-w-0 rounded-2xl border border-border bg-surface p-4 shadow-lg shadow-black/20 sm:p-5">
          <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
            <div>
              <h2 className="text-xl font-semibold text-foreground">{activeTabMeta?.label}</h2>
              <p className="mt-1 text-sm text-secondary">{activeTabDescription}</p>
            </div>
            <div className="flex flex-wrap items-center gap-3">
              {error && <span className="text-sm text-red-300">{error}</span>}

            </div>
          </div>
        </section>

        {["operations-jobs", "operations-scan-generate", "operations-downloads", "operations-duplicates", "operations-maintenance", "operations-backup-restore", "operations-extension-tasks"].includes(resolvedActiveTab) && (
          <TasksPanel
            activeTab={resolvedActiveTab}
            midSlot={resolvedActiveTab === "operations-jobs" && canWriteSystemSettings ? (
              <SectionCard title="Job Limits" description="Control how many background jobs Cove can run at the same time.">
                <NumberField
                  label="Max parallel tasks (-1 = all CPU threads)"
                  value={draft.maxParallelTasks}
                  min={-1}
                  max={128}
                  onChange={(value) => updateDraft((current) => ({ ...current, maxParallelTasks: value ?? current.maxParallelTasks }))}
                />
              </SectionCard>
            ) : null}
          />
        )}

        {(["library-paths-storage", "library-scanning", "data-sources-downloader-paths"] as SettingsTab[]).includes(resolvedActiveTab) && (
          <>
            {resolvedActiveTab === "library-paths-storage" && (
            <SectionCard title="Library Paths" description="Add the content roots the scanner should process.">
              <div className="space-y-3">
                {draft.covePaths.map((path, index) => (
                  <div key={index} className="rounded-xl border border-border bg-card p-3">
                    <div className="flex flex-col gap-3 xl:flex-row xl:items-center">
                      <input
                        type="text"
                        value={path.path}
                        onChange={(event) =>
                          updateDraft((current) => ({
                            ...current,
                            covePaths: current.covePaths.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, path: event.target.value } : item,
                            ),
                          }))
                        }
                        placeholder="D:\\Media\\Videos"
                        className="flex-1 rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                      />
                      <div className="flex flex-wrap items-center gap-4">
                        <CheckboxLabel
                          label="Exclude videos"
                          checked={path.excludeVideo}
                          onChange={(checked) =>
                            updateDraft((current) => ({
                              ...current,
                              covePaths: current.covePaths.map((item, itemIndex) =>
                                itemIndex === index ? { ...item, excludeVideo: checked } : item,
                              ),
                            }))
                          }
                        />
                        <CheckboxLabel
                          label="Exclude images"
                          checked={path.excludeImage}
                          onChange={(checked) =>
                            updateDraft((current) => ({
                              ...current,
                              covePaths: current.covePaths.map((item, itemIndex) =>
                                itemIndex === index ? { ...item, excludeImage: checked } : item,
                              ),
                            }))
                          }
                        />
                        <CheckboxLabel
                          label="Exclude audio"
                          checked={path.excludeAudio}
                          onChange={(checked) =>
                            updateDraft((current) => ({
                              ...current,
                              covePaths: current.covePaths.map((item, itemIndex) =>
                                itemIndex === index ? { ...item, excludeAudio: checked } : item,
                              ),
                            }))
                          }
                        />
                        <CheckboxLabel
                          label="Exclude texts"
                          checked={path.excludeText}
                          onChange={(checked) =>
                            updateDraft((current) => ({
                              ...current,
                              covePaths: current.covePaths.map((item, itemIndex) =>
                                itemIndex === index ? { ...item, excludeText: checked } : item,
                              ),
                            }))
                          }
                        />
                        <button
                          onClick={() =>
                            updateDraft((current) => ({
                              ...current,
                              covePaths:
                                current.covePaths.length > 1
                                  ? current.covePaths.filter((_, itemIndex) => itemIndex !== index)
                                  : [emptyPath()],
                            }))
                          }
                          className="inline-flex items-center gap-1 rounded-lg border border-border px-2 py-1 text-xs text-red-300 hover:border-red-500 hover:text-red-200"
                        >
                          <Trash2 className="h-3.5 w-3.5" /> Remove
                        </button>
                      </div>
                    </div>
                  </div>
                ))}
                <button
                  onClick={() => updateDraft((current) => ({ ...current, covePaths: [...current.covePaths, emptyPath()] }))}
                  className="inline-flex items-center gap-2 rounded-xl border border-dashed border-border px-3 py-2 text-sm text-secondary hover:text-foreground"
                >
                  <Plus className="h-4 w-4" /> Add path
                </button>
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "library-scanning" && (
            <>
            <SectionCard title="Generated Asset Paths" description="Control where generated and cached media artifacts are written.">
              <div className="grid gap-4 md:grid-cols-2">
                <TextField
                  label="Generated path"
                  value={draft.generatedPath ?? ""}
                  onChange={(value) => updateDraft((current) => ({ ...current, generatedPath: value || undefined }))}
                  placeholder="D:\\Cove\\generated"
                />
                <TextField
                  label="Cache path"
                  value={draft.cachePath ?? ""}
                  onChange={(value) => updateDraft((current) => ({ ...current, cachePath: value || undefined }))}
                  placeholder="D:\\Cove\\cache"
                />
              </div>
            </SectionCard>

            <SectionCard title="Preview Generation" description="Server-side settings used when Cove creates preview clips.">
              <div className="space-y-4">
                <SelectField
                  label="Preview preset"
                  value={draft.previewPreset}
                  onChange={(value) => updateDraft((d) => ({ ...d, previewPreset: value }))}
                  options={[
                    { value: "ultrafast", label: "Ultrafast" },
                    { value: "veryfast", label: "Very Fast" },
                    { value: "fast", label: "Fast" },
                    { value: "medium", label: "Medium" },
                    { value: "slow", label: "Slow" },
                    { value: "slower", label: "Slower" },
                    { value: "veryslow", label: "Very Slow" },
                  ]}
                />
                <CheckboxLabel
                  label="Include audio in previews"
                  description="Keep the audio track in generated preview files when the source has audio."
                  checked={draft.previewAudio === "true"}
                  onChange={(checked) => updateDraft((d) => ({ ...d, previewAudio: checked ? "true" : "false" }))}
                />
                <div className="grid gap-4 md:grid-cols-2">
                  <NumberField
                    label="Preview clip length (seconds)"
                    description="Duration of each source slice used when building a generated preview clip."
                    value={draft.ui.previewSegmentDuration}
                    min={0}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, previewSegmentDuration: value ?? d.ui.previewSegmentDuration } }))}
                  />
                  <NumberField
                    label="Preview slices per clip"
                    description="How many slices Cove stitches together for each generated preview clip."
                    value={draft.ui.previewSegments}
                    min={0}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, previewSegments: value ?? d.ui.previewSegments } }))}
                  />
                  <TextField
                    label="Skip from start"
                    description="Seconds or percent to avoid at the beginning of source videos when choosing preview slices."
                    value={draft.ui.previewExcludeStart}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, previewExcludeStart: value } }))}
                    placeholder="0 or 10%"
                  />
                  <TextField
                    label="Skip from end"
                    description="Seconds or percent to avoid at the end of source videos when choosing preview slices."
                    value={draft.ui.previewExcludeEnd}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, previewExcludeEnd: value } }))}
                    placeholder="0 or 10%"
                  />
                </div>
              </div>
            </SectionCard>
            </>
            )}

            {resolvedActiveTab === "data-sources-downloader-paths" && (
            <>
            <SectionCard title="Downloader Limits" description="Control how many downloader imports may run at the same time.">
              <NumberField
                label="Max concurrent downloads"
                value={draft.maxConcurrentDownloads}
                min={1}
                max={16}
                onChange={(value) => updateDraft((current) => ({ ...current, maxConcurrentDownloads: value ?? current.maxConcurrentDownloads }))}
              />
            </SectionCard>

            <SectionCard title="Downloader Paths" description="Override where downloader imports land for a specific downloader or for a downloader/site combination.">
              <div className="space-y-3">
                {draft.downloaderPathOverrides.length === 0 ? (
                  <div className="rounded-xl border border-dashed border-border bg-card/40 px-4 py-3 text-sm text-secondary">
                    No downloader path overrides are configured yet.
                  </div>
                ) : null}

                {draft.downloaderPathOverrides.map((overridePath, index) => (
                  <div key={`${overridePath.downloaderId || "override"}-${index}`} className="rounded-xl border border-border bg-card p-3">
                    <div className="grid gap-3 xl:grid-cols-[minmax(0,1fr)_minmax(0,0.8fr)_minmax(0,1.2fr)_auto] xl:items-end">
                      <label className="block text-sm">
                        <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">Downloader</span>
                        <select
                          value={overridePath.downloaderId}
                          onChange={(event) =>
                            updateDraft((current) => ({
                              ...current,
                              downloaderPathOverrides: current.downloaderPathOverrides.map((item, itemIndex) =>
                                itemIndex === index ? { ...item, downloaderId: event.target.value } : item,
                              ),
                            }))
                          }
                          className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground outline-none"
                        >
                          <option value="">Select downloader</option>
                          {availableDownloaders.map((downloader) => (
                            <option key={downloader.id} value={downloader.id}>
                              {downloader.name} ({downloader.id})
                            </option>
                          ))}
                        </select>
                      </label>
                      <TextField
                        label="Site override (optional)"
                        value={overridePath.site ?? ""}
                        onChange={(value) =>
                          updateDraft((current) => ({
                            ...current,
                            downloaderPathOverrides: current.downloaderPathOverrides.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, site: value || undefined } : item,
                            ),
                          }))
                        }
                        placeholder="example.com"
                      />
                      <TextField
                        label="Save path"
                        value={overridePath.path}
                        onChange={(value) =>
                          updateDraft((current) => ({
                            ...current,
                            downloaderPathOverrides: current.downloaderPathOverrides.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, path: value } : item,
                            ),
                          }))
                        }
                        placeholder="D:\\Media\\Downloader\\Example"
                      />
                      <button
                        onClick={() =>
                          updateDraft((current) => ({
                            ...current,
                            downloaderPathOverrides: current.downloaderPathOverrides.filter((_, itemIndex) => itemIndex !== index),
                          }))
                        }
                        className="inline-flex items-center gap-1 rounded-lg border border-border px-2 py-2 text-xs text-red-300 hover:border-red-500 hover:text-red-200"
                      >
                        <Trash2 className="h-3.5 w-3.5" /> Remove
                      </button>
                    </div>
                    <p className="mt-2 text-xs text-secondary">
                      Choose a downloader first. Leave the optional site field blank to use this path for every site handled by that downloader, or add a host like example.com to override only that site.
                    </p>
                  </div>
                ))}

                <button
                  onClick={() => updateDraft((current) => ({ ...current, downloaderPathOverrides: [...current.downloaderPathOverrides, emptyDownloaderPathOverride()] }))}
                  className="inline-flex items-center gap-2 rounded-xl border border-dashed border-border px-3 py-2 text-sm text-secondary hover:text-foreground"
                >
                  <Plus className="h-4 w-4" /> Add downloader path override
                </button>
              </div>
            </SectionCard>
            </>
            )}

            {resolvedActiveTab === "library-paths-storage" && (
            <SectionCard title="Extensions" description="One extension per line. These values are persisted directly into the backend config.">
              <div className="grid gap-4 lg:grid-cols-3">
                <TextAreaField
                  label="Video extensions"
                  value={listToLines(draft.videoExtensions)}
                  onChange={(value) => updateDraft((current) => ({ ...current, videoExtensions: linesToList(value) }))}
                  rows={7}
                />
                <TextAreaField
                  label="Image extensions"
                  value={listToLines(draft.imageExtensions)}
                  onChange={(value) => updateDraft((current) => ({ ...current, imageExtensions: linesToList(value) }))}
                  rows={7}
                />
                <TextAreaField
                  label="Gallery extensions"
                  value={listToLines(draft.galleryExtensions)}
                  onChange={(value) => updateDraft((current) => ({ ...current, galleryExtensions: linesToList(value) }))}
                  rows={7}
                />
                <TextAreaField
                  label="Audio extensions"
                  value={listToLines(draft.audioExtensions)}
                  onChange={(value) => updateDraft((current) => ({ ...current, audioExtensions: linesToList(value) }))}
                  rows={7}
                />
                <TextAreaField
                  label="Text extensions"
                  value={listToLines(draft.textExtensions)}
                  onChange={(value) => updateDraft((current) => ({ ...current, textExtensions: linesToList(value) }))}
                  rows={7}
                />
              </div>
              {libraryExtensionsPanels.length > 0 && (
                <div className="mt-6 space-y-4 border-t border-border/70 pt-4">
                  {libraryExtensionsPanels.map((panel) => {
                    const Component = resolveComponent(panel.componentName);
                    if (!Component) return null;
                    return (
                      <div key={panel.id} className="space-y-2">
                        <div>
                          <h3 className="text-sm font-medium text-foreground">{panel.label}</h3>
                          <p className="text-xs text-muted">Provided by the {panel.extensionId} extension.</p>
                        </div>
                        <Component />
                      </div>
                    );
                  })}
                </div>
              )}
            </SectionCard>
            )}

            {resolvedActiveTab === "library-scanning" && (
            <SectionCard title="Scan Rules" description="Hashing and exclude patterns applied during scan operations.">
              <div className="space-y-4">
                <CheckboxLabel
                  label="Calculate MD5 checksums during scan"
                  checked={draft.calculateMd5}
                  onChange={(checked) => updateDraft((current) => ({ ...current, calculateMd5: checked }))}
                />
                <TextAreaField
                  label="Exclude patterns"
                  value={listToLines(draft.excludePatterns)}
                  onChange={(value) => updateDraft((current) => ({ ...current, excludePatterns: linesToList(value) }))}
                  rows={5}
                  placeholder="**/._*&#10;**/.DS_Store"
                />
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "library-scanning" && (
            <SectionCard title="Library Behavior" description="Additional library options aligned with Cove's library settings.">
              <div className="space-y-4">
                <div className="grid gap-4 lg:grid-cols-2">
                  <TextAreaField
                    label="Excluded image patterns"
                    value={listToLines(draft.excludeImagePatterns)}
                    onChange={(value) => updateDraft((current) => ({ ...current, excludeImagePatterns: linesToList(value) }))}
                    rows={4}
                  />
                  <TextAreaField
                    label="Excluded gallery patterns"
                    value={listToLines(draft.excludeGalleryPatterns)}
                    onChange={(value) => updateDraft((current) => ({ ...current, excludeGalleryPatterns: linesToList(value) }))}
                    rows={4}
                  />
                </div>

                <div className="grid gap-3 md:grid-cols-2">
                  <CheckboxLabel
                    label="Create galleries from folders"
                    checked={draft.createGalleriesFromFolders}
                    onChange={(checked) => updateDraft((current) => ({ ...current, createGalleriesFromFolders: checked }))}
                  />
                  <CheckboxLabel
                    label="Write image thumbnails"
                    checked={draft.writeImageThumbnails}
                    onChange={(checked) => updateDraft((current) => ({ ...current, writeImageThumbnails: checked }))}
                  />
                  <CheckboxLabel
                    label="Create image clips from videos"
                    checked={draft.createImageClipsFromVideos}
                    onChange={(checked) => updateDraft((current) => ({ ...current, createImageClipsFromVideos: checked }))}
                  />
                  <CheckboxLabel
                    label="Delete file default"
                    checked={draft.ui.deleteFileDefault}
                    onChange={(checked) => updateDraft((current) => ({ ...current, ui: { ...current.ui, deleteFileDefault: checked } }))}
                  />
                  <CheckboxLabel
                    label="Delete generated default"
                    checked={draft.deleteGeneratedDefault}
                    onChange={(checked) => updateDraft((current) => ({ ...current, deleteGeneratedDefault: checked }))}
                  />
                </div>

                <TextField
                  label="Gallery cover regex"
                  value={draft.galleryCoverRegex}
                  onChange={(value) => updateDraft((current) => ({ ...current, galleryCoverRegex: value }))}
                  placeholder="(poster|cover|folder|board)\\.[^\\.]+$"
                />
              </div>
            </SectionCard>
            )}
            {resolvedActiveTab === "library-paths-storage" && libraryStandalonePanels.map((panel) => {
              const Component = resolveComponent(panel.componentName);
              if (!Component) return null;
              return (
                <SectionCard key={panel.id} title={panel.label} description={`Provided by the ${panel.extensionId} extension.`}>
                  <Component />
                </SectionCard>
              );
            })}
          </>
        )}

        {(["my-appearance-theme", "my-theme", "my-playback-viewers", "my-lists-wall", "library-custom-fields", "extensions-customizations"] as SettingsTab[]).includes(resolvedActiveTab) && (
          canWriteSystemSettings ? (
            <>
            {resolvedActiveTab === "my-appearance-theme" && (
            <SectionCard title="Basic Interface" description="Persisted UI preferences used across the app shell.">
              <div className="grid gap-4 md:grid-cols-2">
                <SelectField
                  label="Language"
                  description="Default interface language for the app shell."
                  value={draft.interface.language ?? "en-US"}
                  onChange={(value) => updateDraft((current) => ({ ...current, interface: { ...current.interface, language: value } }))}
                  options={languageOptions}
                />
                <TextField
                  label="Custom title"
                  description="Browser title shown for this Cove instance."
                  value={draft.ui.title ?? ""}
                  onChange={(value) => updateDraft((current) => ({ ...current, ui: { ...current.ui, title: value || undefined } }))}
                  placeholder="Cove"
                />
                <TextField
                  label="Favicon path"
                  description="Path or uploaded asset used as the browser tab icon."
                  value={draft.ui.faviconPath ?? ""}
                  onChange={(value) => updateDraft((current) => ({ ...current, ui: { ...current.ui, faviconPath: value || undefined } }))}
                  placeholder="/favicon.ico"
                />
                <div className="space-y-1">
                  <span className="block text-xs font-medium text-secondary">Favicon upload</span>
                  <label className="inline-flex cursor-pointer items-center gap-2 rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground hover:border-accent hover:text-accent">
                    {uploadFaviconMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Upload className="h-4 w-4" />}
                    <span>{uploadFaviconMutation.isPending ? "Uploading" : "Choose file"}</span>
                    <input
                      type="file"
                      accept=".ico,image/png,image/jpeg,image/webp,image/svg+xml"
                      className="hidden"
                      disabled={uploadFaviconMutation.isPending}
                      onChange={(event) => {
                        const file = event.currentTarget.files?.[0];
                        event.currentTarget.value = "";
                        if (file) uploadFaviconMutation.mutate(file);
                      }}
                    />
                  </label>
                </div>
                <CheckboxLabel
                  label="Troubleshooting mode"
                  description="Temporarily enables more verbose diagnostics and disables custom CSS/JS injection."
                  checked={draft.ui.troubleshootingModeEnabled}
                  onChange={(checked) => updateDraft((current) => ({
                    ...current,
                    logLevel: checked ? "Debug" : current.logLevel,
                    ui: {
                      ...current.ui,
                      troubleshootingModeEnabled: checked,
                      enableCSSCustomization: checked ? false : current.ui.enableCSSCustomization,
                      enableJSCustomization: checked ? false : current.ui.enableJSCustomization,
                    },
                  }))}
                />
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "my-appearance-theme" && (
            <SectionCard title="Navigation" description="Drag to reorder, toggle to show/hide. Changes apply immediately after save.">
              <div className="space-y-4">
                <NavReorderList
                  allItems={navMenuItems}
                  enabledItems={draft.interface.menuItems}
                  onChange={(items) =>
                    updateDraft((current) => ({
                      ...current,
                      interface: { ...current.interface, menuItems: items },
                    }))
                  }
                />
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "library-custom-fields" && (
            <SectionCard title="Custom Fields" description="Define typed metadata fields for entities that need extra structured values.">
              <div className="space-y-4">
                {hasInvalidPersistedCustomFields ? (
                  <div className="rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-sm text-amber-100">
                    Existing custom fields need a key or label and at least one entity selected before they can be saved.
                  </div>
                ) : null}
                {customFieldDefinitionsLoading && customFieldDraftState == null ? (
                  <div className="inline-flex items-center gap-2 text-sm text-secondary">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Loading custom fields...
                  </div>
                ) : null}
                {customFieldDraft.map((definition, index) => (
                  <div key={`custom-field-${index}`} className="rounded-lg border border-border bg-card p-4">
                    <div className="mb-4 flex items-center justify-between gap-3">
                      <div className="min-w-0">
                        <div className="truncate text-sm font-medium text-foreground">{definition.label || definition.key || "New custom field"}</div>
                        <div className="truncate text-xs text-muted">{definition.key || "Unsaved key"}</div>
                      </div>
                      <button
                        type="button"
                        onClick={() => removeCustomFieldDefinition(index)}
                        aria-label="Remove custom field definition"
                        className="rounded-lg border border-border p-2 text-muted hover:border-red-400 hover:text-red-300"
                      >
                        <Trash2 className="h-4 w-4" />
                      </button>
                    </div>
                    <div className="grid gap-4 md:grid-cols-2">
                      <TextField
                        label="Key"
                        value={definition.key}
                        onChange={(value) => updateCustomFieldDefinition(index, (current) => ({ ...current, key: value }))}
                        onBlur={() => commitCustomFieldDraft()}
                        placeholder="source_id"
                      />
                      <TextField
                        label="Label"
                        value={definition.label}
                        onChange={(value) => updateCustomFieldDefinition(index, (current) => ({ ...current, label: value }))}
                        onBlur={() => commitCustomFieldDraft()}
                        placeholder="Source ID"
                      />
                      <SelectField
                        label="Type"
                        value={definition.type}
                        onChange={(value) => updateCustomFieldDefinition(index, (current) => ({ ...current, type: value as CustomFieldType }))}
                        onBlur={() => commitCustomFieldDraft()}
                        options={customFieldTypeOptions}
                      />
                      <div className="space-y-2">
                        <span className="block text-xs font-medium uppercase tracking-wide text-muted">Behavior</span>
                        <div className="flex flex-wrap gap-3 rounded-xl border border-border bg-background px-3 py-2">
                          <CheckboxLabel
                            label="Filterable"
                            checked={definition.filterable}
                            onChange={(checked) => updateCustomFieldDefinition(index, (current) => ({ ...current, filterable: checked }), { commit: true })}
                          />
                          <CheckboxLabel
                            label="Sortable"
                            checked={definition.sortable}
                            onChange={(checked) => updateCustomFieldDefinition(index, (current) => ({ ...current, sortable: checked }), { commit: true })}
                          />
                        </div>
                      </div>
                    </div>
                    <div className="mt-4 space-y-2">
                      <span className="block text-xs font-medium uppercase tracking-wide text-muted">Entities</span>
                      <div className="flex flex-wrap gap-3 rounded-xl border border-border bg-background px-3 py-2">
                        {customFieldEntityOptions.map((option) => (
                          <CheckboxLabel
                            key={option.value}
                            label={option.label}
                            checked={(definition.entityTypes ?? []).includes(option.value)}
                            onChange={() => toggleCustomFieldEntity(index, option.value)}
                          />
                        ))}
                      </div>
                    </div>
                    {definition.type === "enum" ? (
                      <div className="mt-4">
                        <TextAreaField
                          label="Options"
                          value={listToLines(definition.options ?? [])}
                          onChange={(value) => updateCustomFieldDefinition(index, (current) => ({ ...current, options: linesToList(value) }))}
                          onBlur={() => commitCustomFieldDraft()}
                          rows={3}
                          placeholder="One option per line"
                        />
                      </div>
                    ) : null}
                  </div>
                ))}
                <button
                  type="button"
                  onClick={addCustomFieldDefinition}
                  className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:border-accent hover:text-foreground"
                >
                  <Plus className="h-4 w-4" />
                  Add field
                </button>
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "my-appearance-theme" && (
            <SectionCard title="Ratings" description="Stored ratings remain 1-100 internally. This changes how they are displayed and edited in the UI.">
              <div className="grid gap-4 md:grid-cols-2">
                <SelectField
                  label="Rating system"
                  value={draft.ui.ratingSystemOptions.type}
                  onChange={(value) =>
                    updateDraft((current) => ({
                      ...current,
                      ui: {
                        ...current.ui,
                        ratingSystemOptions: {
                          ...current.ui.ratingSystemOptions,
                          type: value as RatingSystemType,
                        },
                      },
                    }))
                  }
                  options={ratingSystemOptions}
                />
                {draft.ui.ratingSystemOptions.type === "stars" && (
                  <SelectField
                    label="Star precision"
                    value={draft.ui.ratingSystemOptions.starPrecision}
                    onChange={(value) =>
                      updateDraft((current) => ({
                        ...current,
                        ui: {
                          ...current.ui,
                          ratingSystemOptions: {
                            ...current.ui.ratingSystemOptions,
                            starPrecision: value as RatingStarPrecision,
                          },
                        },
                      }))
                    }
                    options={starPrecisionOptions}
                  />
                )}
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "my-playback-viewers" && (
            <SectionCard title="Video Player" description="Playback behavior for the built-in video player.">
              <div className="space-y-3">
                <CheckboxLabel
                  label="Auto-play videos when opened"
                  description="Start playback automatically when you open a video detail page."
                  checked={draft.ui.autostartVideo}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, autostartVideo: checked } }))}
                />
                <CheckboxLabel
                  label="Auto-play when opened with Play Selected"
                  description="Honor the auto-play setting when launching via the Play Selected action from a list."
                  checked={draft.ui.autostartVideoOnPlaySelected}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, autostartVideoOnPlaySelected: checked } }))}
                />
                <CheckboxLabel
                  label="Auto-play when clicking a video in a list"
                  description="Start playback immediately when you click a video row in a list view."
                  checked={draft.ui.autoplayOnListClick}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, autoplayOnListClick: checked } }))}
                />
                <CheckboxLabel
                  label="Always resume from last position"
                  description="If you've watched part of a video, resume from where you left off instead of starting at 0."
                  checked={draft.ui.alwaysResumeOnPlayback}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, alwaysResumeOnPlayback: checked } }))}
                />
                <div className="grid gap-4 md:grid-cols-2">
                  <NumberField
                    label="Default player start (%)"
                    value={draft.ui.playerVideoStartPercent ?? 0}
                    min={0}
                    max={95}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, playerVideoStartPercent: Math.min(95, Math.max(0, value ?? 0)) } }))}
                  />
                  <NumberField
                    label="Use default start only for videos longer than (seconds)"
                    value={draft.ui.playerVideoStartMinDuration ?? 0}
                    min={0}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, playerVideoStartMinDuration: Math.max(0, value ?? 0) } }))}
                  />
                </div>
                <CheckboxLabel
                  label="Auto-advance to the next item in a list"
                  description="When a video finishes, automatically play the next item in the active list or playlist."
                  checked={draft.ui.continuePlaylistDefault}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, continuePlaylistDefault: checked } }))}
                />
                <CheckboxLabel
                  label="Show A-B loop controls in the player"
                  description="Adds the A-B loop buttons to the player toolbar for repeating a selected range."
                  checked={draft.ui.showAbLoopControls}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, showAbLoopControls: checked } }))}
                />
                <NumberField
                  label="Maximum A-B loop length (seconds)"
                  description="Hard cap on how long an A-B loop can run before it stops. 0 = no cap."
                  value={draft.ui.maxLoopDuration}
                  min={0}
                  onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, maxLoopDuration: value ?? 0 } }))}
                />
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "my-playback-viewers" && (
            <SectionCard title="List Previews" description="Playback behavior for generated preview clips in list-style browsing surfaces.">
              <div className="space-y-3">
                <CheckboxLabel
                  label="Play audio in preview clips"
                  description="When a generated preview clip is played inline, allow its audio track by default."
                  checked={draft.ui.soundOnPreview}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, soundOnPreview: checked } }))}
                />
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "my-lists-wall" && (
            <SectionCard title="Wall" description="Wall view display options.">
              <div className="space-y-4">
                <CheckboxLabel
                  label="Wall show title"
                  checked={draft.ui.wallShowTitle}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, wallShowTitle: checked } }))}
                />
                <SelectField
                  label="Wall playback"
                  value={String(draft.ui.wallPlayback)}
                  onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, wallPlayback: Number(value) } }))}
                  options={[
                    { value: "0", label: "Audio" },
                    { value: "1", label: "Silent" },
                  ]}
                />
                <SelectField
                  label="Wall preview type"
                  description="Media source used for wall tiles when Cove has multiple preview formats."
                  value={draft.ui.wallPreviewType ?? "video"}
                  onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, wallPreviewType: value } }))}
                  options={[
                    { value: "video", label: "Video" },
                    { value: "webp", label: "Animated WebP" },
                    { value: "image", label: "Static Image" },
                  ]}
                />
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "my-lists-wall" && (
            <SectionCard title="Card Media Fit" description="How still images and generated video previews fill cards across list and wall views.">
              <div className="grid gap-4 md:grid-cols-2">
                <SelectField
                  label="Image card fit"
                  description="Cover fills each card by cropping; contain keeps the full image visible with empty space when needed."
                  value={draft.ui.imageObjectFit ?? "cover"}
                  onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, imageObjectFit: value } }))}
                  options={[
                    { value: "cover", label: "Cover" },
                    { value: "contain", label: "Contain" },
                  ]}
                />
                <SelectField
                  label="Video preview card fit"
                  description="Cover crops generated video previews to fill cards; contain keeps the whole frame visible."
                  value={draft.ui.videoObjectFit ?? "cover"}
                  onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, videoObjectFit: value } }))}
                  options={[
                    { value: "cover", label: "Cover" },
                    { value: "contain", label: "Contain" },
                  ]}
                />
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "my-playback-viewers" && (
            <SectionCard title="Feed & Vertical Viewer" description="Choose what autoplays in the video feed-style views.">
              <div className="space-y-4">
                <SelectField
                  label="Playback source"
                  value={draft.ui.feedVideoSource ?? "preview"}
                  onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, feedVideoSource: value } }))}
                  options={[
                    { value: "preview", label: "Generated preview clip" },
                    { value: "video", label: "Full video" },
                  ]}
                />
                <CheckboxLabel
                  label="Play sound by default in Feed and Vertical Viewer"
                  checked={draft.ui.feedVideoSound ?? false}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, feedVideoSound: checked } }))}
                />
                <div className="grid gap-4 md:grid-cols-2">
                  <NumberField
                    label="Full video start (%)"
                    value={draft.ui.feedVideoStartPercent ?? 0}
                    min={0}
                    max={95}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, feedVideoStartPercent: Math.min(95, Math.max(0, value ?? 0)) } }))}
                  />
                  <NumberField
                    label="Use start % only for videos longer than (seconds)"
                    value={draft.ui.feedVideoStartMinDuration ?? 0}
                    min={0}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, feedVideoStartMinDuration: Math.max(0, value ?? 0) } }))}
                  />
                </div>
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "my-playback-viewers" && (
            <SectionCard title="Lightbox" description="Lightbox and slideshow behavior.">
              <div className="space-y-4">
                <CheckboxLabel
                  label="Delete file default"
                  checked={draft.ui.deleteFileDefault}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, deleteFileDefault: checked } }))}
                />
                <NumberField
                  label="Slideshow delay (ms)"
                  value={draft.ui.slideshowDelay}
                  min={500}
                  onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, slideshowDelay: value ?? d.ui.slideshowDelay } }))}
                />
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "extensions-customizations" && (
            <>
            <SectionCard title="Custom CSS" description="Inject custom CSS into the application.">
              <div className="space-y-4">
                <CheckboxLabel
                  label="Enable CSS customization"
                  checked={draft.ui.enableCSSCustomization}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, enableCSSCustomization: checked } }))}
                />
                {draft.ui.enableCSSCustomization && (
                  <TextAreaField
                    label="Custom CSS"
                    value={draft.ui.customCss ?? ""}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, customCss: value || undefined } }))}
                    rows={8}
                    placeholder="/* Enter custom CSS here */"
                  />
                )}
              </div>
            </SectionCard>

            <SectionCard title="Custom JavaScript" description="Inject custom JavaScript into the application.">
              <div className="space-y-4">
                <CheckboxLabel
                  label="Enable JavaScript customization"
                  checked={draft.ui.enableJSCustomization}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, enableJSCustomization: checked } }))}
                />
                {draft.ui.enableJSCustomization && (
                  <TextAreaField
                    label="Custom JavaScript"
                    value={draft.ui.customJs ?? ""}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, customJs: value || undefined } }))}
                    rows={8}
                    placeholder="// Enter custom JavaScript here"
                  />
                )}
              </div>
            </SectionCard>
            </>
            )}

            {resolvedActiveTab === "my-theme" && <ThemeSelector />}
            </>
          ) : (
            resolvedActiveTab === "my-theme"
              ? <ThemeSelector />
              : <LocalInterfacePanel serverRatingOptions={draftState?.ui.ratingSystemOptions} />
          )
        )}

        {resolvedActiveTab === "keyboard-shortcuts" && (
          <>
            {canWriteSystemSettings ? (
              <SectionCard title="Keyboard Shortcuts" description="Override the registered global and list-page shortcut keys.">
                <div className="space-y-5">
                  {KEYBINDING_GROUPS.map((group) => (
                    <div key={group.group} className="space-y-3">
                      <div className="text-xs font-semibold uppercase tracking-wide text-muted">{group.group}</div>
                      <div className="grid gap-3 md:grid-cols-2">
                        {group.definitions.map((definition) => {
                          const defaultShortcut = normalizeShortcutSequence(definition.keys);
                          const value = normalizeShortcutSequence(draft.ui.keybindingOverrides?.[definition.id] ?? defaultShortcut);
                          const isCapturing = capturingKeybindingId === definition.id;
                          const isCustomized = value !== defaultShortcut;
                          const capturedValue = isCapturing ? capturedKeybindingParts.join(" ") : "";

                          return (
                            <div key={definition.id} className="rounded-xl border border-border bg-card p-3">
                              <div className="flex items-start justify-between gap-3">
                                <div>
                                  <div className="text-sm font-medium text-foreground">{definition.label}</div>
                                  <div className="mt-1 text-xs text-secondary">Default: {defaultShortcut}</div>
                                </div>
                                {isCustomized ? (
                                  <button
                                    type="button"
                                    onClick={() => updateKeybindingOverride(definition.id, "")}
                                    className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-border text-muted hover:border-accent hover:text-accent"
                                    aria-label={`Reset ${definition.label}`}
                                  >
                                    <X className="h-4 w-4" />
                                  </button>
                                ) : null}
                              </div>
                              <div className="mt-3 flex flex-col gap-2 sm:flex-row">
                                <input
                                  value={value}
                                  onChange={(event) => updateKeybindingOverride(definition.id, event.target.value)}
                                  placeholder={definition.keys}
                                  className="min-w-0 flex-1 rounded-lg border border-border bg-surface px-3 py-2 font-mono text-sm text-foreground focus:border-accent focus:outline-none"
                                  aria-label={`${definition.label} shortcut`}
                                />
                                <button
                                  type="button"
                                  onClick={() => startKeybindingCapture(definition.id)}
                                  onKeyDown={(event) => captureKeybindingOverride(definition.id, event)}
                                  className={`inline-flex items-center justify-center gap-2 rounded-lg border px-3 py-2 text-sm font-medium transition ${
                                    isCapturing
                                      ? "border-accent bg-accent/15 text-accent"
                                      : "border-border text-secondary hover:border-accent hover:text-accent"
                                  }`}
                                >
                                  <Keyboard className="h-4 w-4" />
                                  {isCapturing ? (capturedValue || "Recording") : "Record"}
                                </button>
                              </div>
                            </div>
                          );
                        })}
                      </div>
                    </div>
                  ))}
                  <button
                    type="button"
                    onClick={() => updateDraft((current) => ({ ...current, ui: { ...current.ui, keybindingOverrides: {} } }))}
                    className="rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:border-accent hover:text-foreground"
                  >
                    Reset shortcuts
                  </button>
                </div>
              </SectionCard>
            ) : (
              <SectionCard title="Keyboard Shortcuts" description="Current shortcut reference.">
                <div className="space-y-5">
                  {KEYBINDING_GROUPS.map((group) => (
                    <div key={group.group} className="space-y-3">
                      <div className="text-xs font-semibold uppercase tracking-wide text-muted">{group.group}</div>
                      <div className="grid gap-3 md:grid-cols-2">
                        {group.definitions.map((definition) => (
                          <div key={definition.id} className="rounded-lg border border-border bg-card px-3 py-2">
                            <div className="text-sm font-medium text-foreground">{definition.label}</div>
                            <div className="mt-1 text-xs text-secondary">{definition.keys}</div>
                          </div>
                        ))}
                      </div>
                    </div>
                  ))}
                </div>
              </SectionCard>
            )}
          </>
        )}

        {(["my-account", "my-activity-history", "my-lists-wall"] as SettingsTab[]).includes(resolvedActiveTab) && <UserSettingsPanel activeTab={resolvedActiveTab} />}

        {resolvedActiveTab === "library-display-profiles" && canReadSegments && (
          <DisplayProfilesSettingsPanel canWrite={canWriteSegments} />
        )}

        {resolvedActiveTab === "data-sources-ai-data" && <AiDataSettingsPanel />}

        {resolvedActiveTab === "security-authentication" && (
          <>
            <SectionCard title="Authentication" description="These values persist to config immediately.">
              <div className="space-y-4">
                <CheckboxLabel
                  label="Authentication required"
                  checked={draft.security.enabled}
                  onChange={(checked) => updateDraft((current) => ({ ...current, security: { ...current.security, enabled: checked } }))}
                />
                {!draft.security.enabled ? (
                  <div className="rounded-lg border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-sm text-amber-200">
                    Anyone with network access to this Cove can use it. The outside-IP failsafe is still active: if a
                    request arrives from a public/untrusted address while authentication is disabled, Cove
                    automatically re-enables authentication to protect your data. To intentionally run with
                    authentication off behind a trusted reverse proxy, add your hostname under Trusted hosts below
                    <strong> before</strong> disabling authentication.
                  </div>
                ) : null}
                <div className="rounded-lg border border-border bg-background px-3 py-3">
                  <div className="font-medium text-foreground">Trusted hosts</div>
                  <div className="mt-1 text-sm text-secondary">
                    Hostnames listed here are treated as trusted even when authentication is disabled, so the
                    outside-IP failsafe will not re-enable authentication for requests to them. Use this only if you
                    intentionally run Cove with authentication disabled behind a trusted reverse proxy (e.g. an nginx
                    ingress) on a custom domain. Configure this <strong>before</strong> turning authentication off. Use
                    an exact hostname (<code>cove.example.com</code>), a wildcard (<code>*.example.com</code>), or
                    <code>*</code> to trust any host. Leave empty unless you know you need this.
                  </div>
                  <div className="mt-3 space-y-2">
                        {(draft.security.trustedHosts ?? []).map((host, index) => (
                          <div key={index} className="flex flex-col gap-2 md:flex-row md:items-center">
                            <input
                              type="text"
                              value={host}
                              placeholder="cove.example.com"
                              onChange={(event) =>
                                updateDraft((current) => ({
                                  ...current,
                                  security: {
                                    ...current.security,
                                    trustedHosts: (current.security.trustedHosts ?? []).map((item, itemIndex) =>
                                      itemIndex === index ? event.target.value : item,
                                    ),
                                  },
                                }))
                              }
                              className="flex-1 rounded-lg border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                            />
                            <button
                              type="button"
                              onClick={() =>
                                updateDraft((current) => ({
                                  ...current,
                                  security: {
                                    ...current.security,
                                    trustedHosts: (current.security.trustedHosts ?? []).filter(
                                      (_, itemIndex) => itemIndex !== index,
                                    ),
                                  },
                                }))
                              }
                              className="inline-flex justify-center rounded-lg border border-red-500/50 px-3 py-2 text-sm font-medium text-red-300 hover:bg-red-500/10"
                            >
                              Remove
                            </button>
                          </div>
                        ))}
                        <button
                          type="button"
                          onClick={() =>
                            updateDraft((current) => ({
                              ...current,
                              security: {
                                ...current.security,
                                trustedHosts: [...(current.security.trustedHosts ?? []), ""],
                              },
                            }))
                          }
                          className="inline-flex justify-center rounded-lg border border-border px-3 py-2 text-sm font-medium text-foreground hover:border-accent hover:text-accent"
                        >
                          Add trusted host
                        </button>
                      </div>
                    </div>
                <CheckboxLabel
                  label="Allow anonymous share links"
                  checked={draft.security.allowAnonymousShareLinks}
                  onChange={(checked) => updateDraft((current) => ({ ...current, security: { ...current.security, allowAnonymousShareLinks: checked } }))}
                />
                {draft.security.enabled ? (
                  <div className="flex flex-col gap-3 rounded-lg border border-border bg-background px-3 py-3 text-sm md:flex-row md:items-center md:justify-between">
                    <div>
                      <div className="font-medium text-foreground">Owner user</div>
                      <div className="text-secondary">
                        {securityUsersQ.data?.find((item) => item.roles.includes("Owner") || item.isSystem)?.username ?? "Not loaded"}
                      </div>
                    </div>
                    <button
                      type="button"
                      onClick={() => {
                        setActiveTab("users");
                        navigateToUrl("/settings/security-access/users", { state: { page: "settings" } });
                      }}
                      className="inline-flex justify-center rounded-lg border border-border px-3 py-2 text-sm font-medium text-foreground hover:border-accent hover:text-accent"
                    >
                      User management
                    </button>
                  </div>
                ) : null}
                <div className="flex flex-col gap-2 border-t border-border pt-4 md:flex-row md:items-center md:justify-between">
                  <div className="text-sm text-secondary">Refresh tokens remain rotatable and revocable.</div>
                  <button
                    type="button"
                    disabled={!authEnabled || !user || revokeSessionsMutation.isPending}
                    onClick={() => revokeSessionsMutation.mutate()}
                    className="inline-flex justify-center rounded-lg border border-red-500/50 px-3 py-2 text-sm font-medium text-red-300 hover:bg-red-500/10 disabled:opacity-50"
                  >
                    {revokeSessionsMutation.isPending ? "Revoking..." : "Revoke all sessions"}
                  </button>
                </div>
              </div>
            </SectionCard>
          </>
        )}

        {(["data-sources-scrapers", "data-sources-metadata-servers", "data-sources-identify-batch-defaults"] as SettingsTab[]).includes(resolvedActiveTab) && (
          <>
            {resolvedActiveTab === "data-sources-scrapers" && (
            <>
            <SectionCard title="Legacy YAML Scraper Directories" description="New scrapers ship as extensions. This list is scanned only for legacy YAML scraper definitions that have not been packaged yet.">
              <div className="space-y-3">
                {draft.scraping.scraperDirectories.map((directory, index) => (
                  <div key={index} className="flex flex-col gap-2 md:flex-row md:items-center">
                    <input
                      type="text"
                      value={directory}
                      onChange={(event) =>
                        updateDraft((current) => ({
                          ...current,
                          scraping: {
                            ...current.scraping,
                            scraperDirectories: current.scraping.scraperDirectories.map((item, itemIndex) =>
                              itemIndex === index ? event.target.value : item,
                            ),
                          },
                        }))
                      }
                      placeholder="C:\\Users\\you\\AppData\\Local\\cove\\scrapers"
                      className="flex-1 rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                    />
                    <button
                      onClick={() =>
                        updateDraft((current) => ({
                          ...current,
                          scraping: {
                            ...current.scraping,
                            scraperDirectories:
                              current.scraping.scraperDirectories.length > 1
                                ? current.scraping.scraperDirectories.filter((_, itemIndex) => itemIndex !== index)
                                : [""],
                          },
                        }))
                      }
                      className="inline-flex items-center gap-1 rounded-lg border border-border px-2 py-2 text-xs text-red-300 hover:border-red-500 hover:text-red-200"
                    >
                      <Trash2 className="h-3.5 w-3.5" /> Remove
                    </button>
                  </div>
                ))}
                <button
                  onClick={() =>
                    updateDraft((current) => ({
                      ...current,
                      scraping: {
                        ...current.scraping,
                        scraperDirectories: [...current.scraping.scraperDirectories, ""],
                      },
                    }))
                  }
                  className="inline-flex items-center gap-2 rounded-xl border border-dashed border-border px-3 py-2 text-sm text-secondary hover:text-foreground"
                >
                  <Plus className="h-4 w-4" /> Add scraper directory
                </button>
              </div>
            </SectionCard>

            <SectionCard
              title="Preferred Scrapers"
              description="Pick the default scraper Cove should surface first for each entity type and site."
            >
              {scraperPreferenceGroups.length === 0 ? (
                <div className="rounded-xl border border-dashed border-border p-4 text-sm text-secondary">
                  No scraper ownership conflicts need a preferred scraper.
                </div>
              ) : (
                <div className="space-y-3">
                  {scraperPreferenceGroups.map((group) => {
                    const selectedScraperId = getSelectedScraperPreferenceId(group.entityType, group.site);

                    return (
                      <div key={`${group.entityType}-${group.site}`} className="grid gap-3 rounded-xl border border-border bg-card p-3 md:grid-cols-[minmax(0,1fr)_minmax(0,1.5fr)]">
                        <div>
                          <div className="text-sm font-medium capitalize text-foreground">{group.entityType} · {group.site}</div>
                          <p className="mt-1 text-xs text-secondary">
                            Applies when a {group.entityType} URL resolves to this host.
                          </p>
                        </div>
                        <div>
                          <label className="block text-xs font-medium uppercase tracking-[0.14em] text-muted">Preferred scraper</label>
                          <select
                            value={selectedScraperId}
                            onChange={(event) => updateScraperPreference(group.entityType, group.site, event.target.value)}
                            className="mt-2 w-full rounded-xl border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                          >
                            <option value="">No preference</option>
                            {group.scrapers.map((scraper) => (
                              <option key={scraper.id} value={scraper.id}>
                                {scraper.name}
                              </option>
                            ))}
                          </select>
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </SectionCard>
            </>
            )}

            {resolvedActiveTab === "data-sources-metadata-servers" && (
            <SectionCard title="Metadata Server Instances" description="Configure remote metadata-server GraphQL endpoints, validate credentials, and use them from entity detail pages.">
              <div className="space-y-3">
                {draft.scraping.metadataServers.length === 0 && (
                  <div className="rounded-xl border border-dashed border-border p-4 text-sm text-secondary">
                    No Metadata Server instances configured yet.
                  </div>
                )}

                {draft.scraping.metadataServers.map((metadataServer, index) => {
                  const validation = metadataServerValidation[String(index)];

                  return (
                    <div key={index} className="rounded-xl border border-border bg-card p-3">
                      <div className="grid gap-3 xl:grid-cols-[minmax(0,1fr)_minmax(0,2fr)_minmax(0,2fr)_160px_auto_auto]">
                        <TextField
                          label="Name"
                          value={metadataServer.name}
                          onChange={(value) =>
                            updateDraft((current) => ({
                              ...current,
                              scraping: {
                                ...current.scraping,
                                metadataServers: current.scraping.metadataServers.map((item, itemIndex) =>
                                  itemIndex === index ? { ...item, name: value } : item,
                                ),
                              },
                            }))
                          }
                          placeholder="Server name"
                        />
                        <TextField
                          label="Endpoint"
                          value={metadataServer.endpoint}
                          onChange={(value) =>
                            updateDraft((current) => ({
                              ...current,
                              scraping: {
                                ...current.scraping,
                                metadataServers: current.scraping.metadataServers.map((item, itemIndex) =>
                                  itemIndex === index ? { ...item, endpoint: value } : item,
                                ),
                              },
                            }))
                          }
                          placeholder="https://example.com/graphql"
                        />
                        <TextField
                          label="API key"
                          type="password"
                          value={metadataServer.apiKey}
                          onChange={(value) =>
                            updateDraft((current) => ({
                              ...current,
                              scraping: {
                                ...current.scraping,
                                metadataServers: current.scraping.metadataServers.map((item, itemIndex) =>
                                  itemIndex === index ? { ...item, apiKey: value } : item,
                                ),
                              },
                            }))
                          }
                          placeholder="Paste API key"
                        />
                        <NumberField
                          label="Max req/min"
                          value={metadataServer.maxRequestsPerMinute}
                          min={1}
                          onChange={(value) =>
                            updateDraft((current) => ({
                              ...current,
                              scraping: {
                                ...current.scraping,
                                metadataServers: current.scraping.metadataServers.map((item, itemIndex) =>
                                  itemIndex === index
                                    ? { ...item, maxRequestsPerMinute: value ?? item.maxRequestsPerMinute }
                                    : item,
                                ),
                              },
                            }))
                          }
                        />
                        <div className="flex items-end">
                          <button
                            onClick={() => validateMetadataServerMutation.mutate({ index, metadataServer })}
                            disabled={validateMetadataServerMutation.isPending || !metadataServer.endpoint.trim()}
                            className="inline-flex items-center gap-2 rounded-xl border border-border px-3 py-2 text-sm text-foreground hover:border-accent hover:text-accent disabled:opacity-60"
                          >
                            {validateMetadataServerMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                            Validate
                          </button>
                        </div>
                        <div className="flex items-end">
                          <button
                            onClick={() =>
                              updateDraft((current) => ({
                                ...current,
                                scraping: {
                                  ...current.scraping,
                                  metadataServers: current.scraping.metadataServers.filter((_, itemIndex) => itemIndex !== index),
                                },
                              }))
                            }
                            className="inline-flex items-center gap-1 rounded-lg border border-border px-2 py-2 text-xs text-red-300 hover:border-red-500 hover:text-red-200"
                          >
                            <Trash2 className="h-3.5 w-3.5" /> Remove
                          </button>
                        </div>
                      </div>
                      {validation && (
                        <p className={`mt-3 text-sm ${validation.valid ? "text-emerald-300" : "text-red-300"}`}>
                          {validation.status}
                        </p>
                      )}
                    </div>
                  );
                })}

                <button
                  onClick={() =>
                    updateDraft((current) => ({
                      ...current,
                      scraping: {
                        ...current.scraping,
                        metadataServers: [...current.scraping.metadataServers, emptyMetadataServer()],
                      },
                    }))
                  }
                  className="inline-flex items-center gap-2 rounded-xl border border-dashed border-border px-3 py-2 text-sm text-secondary hover:text-foreground"
                >
                  <Plus className="h-4 w-4" /> Add MetadataServer instance
                </button>
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "data-sources-identify-batch-defaults" && (
            <>
            <SectionCard title="Default Batch Options" description="Defaults used to prefill MetadataServer batch-tag dialogs.">
              <div className="space-y-4">
                <div className="rounded-xl border border-border bg-card px-3 py-2 text-sm text-secondary">
                  Default scraper choices are managed in the Scrapers tab above.
                </div>

                <SelectField
                  label="Existing linked entities"
                  value={draft.scraping.metadataBatchDefaults.refreshAlreadyTagged ? "overwrite" : "keep"}
                  onChange={(value) =>
                    updateDraft((current) => ({
                      ...current,
                      scraping: {
                        ...current.scraping,
                        metadataBatchDefaults: {
                          ...current.scraping.metadataBatchDefaults,
                          refreshAlreadyTagged: value === "overwrite",
                        },
                      },
                    }))
                  }
                  options={[
                    { value: "keep", label: "Keep existing MetadataServer links" },
                    { value: "overwrite", label: "Refresh already-linked entities" },
                  ]}
                />

                <CheckboxLabel
                  label="Create parent studios"
                  description="When batch metadata references a studio parent that does not exist locally, create it automatically."
                  checked={draft.scraping.metadataBatchDefaults.createParentStudios}
                  onChange={(checked) =>
                    updateDraft((current) => ({
                      ...current,
                      scraping: {
                        ...current.scraping,
                        metadataBatchDefaults: {
                          ...current.scraping.metadataBatchDefaults,
                          createParentStudios: checked,
                        },
                      },
                    }))
                  }
                />

                <div className="space-y-2">
                  <div>
                    <div className="text-xs font-medium uppercase tracking-wide text-muted">Apply by default</div>
                    <p className="mt-1 text-xs text-secondary">
                      Checked fields are enabled when a MetadataServer batch dialog opens. Clear a field to preserve existing local values by default.
                    </p>
                  </div>
                  <div className="grid gap-2 rounded-xl border border-border bg-card p-3 sm:grid-cols-2 lg:grid-cols-3">
                    {METADATA_BATCH_EXCLUDE_OPTIONS.map((option) => (
                      <CheckboxLabel
                        key={option.id}
                        label={option.label}
                        checked={!draft.scraping.metadataBatchDefaults.excludeFields.includes(option.id)}
                        onChange={(checked) =>
                          updateDraft((current) => {
                            const excludeFields = new Set(current.scraping.metadataBatchDefaults.excludeFields);
                            if (checked) {
                              excludeFields.delete(option.id);
                            } else {
                              excludeFields.add(option.id);
                            }

                            return {
                              ...current,
                              scraping: {
                                ...current.scraping,
                                metadataBatchDefaults: {
                                  ...current.scraping.metadataBatchDefaults,
                                  excludeFields: METADATA_BATCH_EXCLUDE_OPTIONS
                                    .map((candidate) => candidate.id)
                                    .filter((id) => excludeFields.has(id)),
                                },
                              },
                            };
                          })
                        }
                      />
                    ))}
                  </div>
                </div>
              </div>
            </SectionCard>

            <SectionCard title="Identify Defaults" description="Defaults used when opening Identify. Leave thresholds blank to disable that auto-apply requirement.">
              <div className="space-y-4">
                <div className="grid gap-4 md:grid-cols-2">
                  <NumberField
                    label="Max auto-apply duration difference (seconds)"
                    value={draft.scraping.identifyDefaults.autoApplyMaxDurationDifferenceSeconds}
                    min={0}
                    onChange={(value) =>
                      updateDraft((current) => ({
                        ...current,
                        scraping: {
                          ...current.scraping,
                          identifyDefaults: {
                            ...current.scraping.identifyDefaults,
                            autoApplyMaxDurationDifferenceSeconds: value,
                          },
                        },
                      }))
                    }
                  />
                  <NumberField
                    label="Max auto-apply pHash distance"
                    value={draft.scraping.identifyDefaults.autoApplyMaxPhashDistance}
                    min={0}
                    onChange={(value) =>
                      updateDraft((current) => ({
                        ...current,
                        scraping: {
                          ...current.scraping,
                          identifyDefaults: {
                            ...current.scraping.identifyDefaults,
                            autoApplyMaxPhashDistance: value,
                          },
                        },
                      }))
                    }
                  />
                </div>
                <div className="border-t border-border pt-3 space-y-3">
                  <CheckboxLabel
                    label="Allow Identify to create new performers"
                    checked={draft.scraping.identifyDefaults.createPerformers}
                    onChange={(checked) =>
                      updateDraft((current) => ({
                        ...current,
                        scraping: {
                          ...current.scraping,
                          identifyDefaults: {
                            ...current.scraping.identifyDefaults,
                            createPerformers: checked,
                          },
                        },
                      }))
                    }
                  />
                  <CheckboxLabel
                    label="Allow Identify to create new studios"
                    checked={draft.scraping.identifyDefaults.createStudios}
                    onChange={(checked) =>
                      updateDraft((current) => ({
                        ...current,
                        scraping: {
                          ...current.scraping,
                          identifyDefaults: {
                            ...current.scraping.identifyDefaults,
                            createStudios: checked,
                          },
                        },
                      }))
                    }
                  />
                  <CheckboxLabel
                    label="Allow Identify to create new tags"
                    checked={draft.scraping.identifyDefaults.createTags}
                    onChange={(checked) =>
                      updateDraft((current) => ({
                        ...current,
                        scraping: {
                          ...current.scraping,
                          identifyDefaults: {
                            ...current.scraping.identifyDefaults,
                            createTags: checked,
                          },
                        },
                      }))
                    }
                  />
                </div>
                <div className="flex items-start gap-2 rounded-xl border border-border bg-card px-3 py-2 text-xs text-secondary">
                  <Info className="h-4 w-4 text-accent mt-0.5 flex-shrink-0" />
                  <p>
                    Duration and pHash thresholds are applied before Identify auto-saves a match. Set either field to <strong>0</strong> to require an exact match, or leave it blank to ignore that signal.
                  </p>
                </div>
              </div>
            </SectionCard>
            </>
            )}

            {resolvedActiveTab === "data-sources-scrapers" && (
            <SectionCard title="Discovered Scrapers" description="Scraper definitions are loaded from the configured directories using the same YAML field names Cove expects.">
              <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
                <p className="text-sm text-secondary">Reload after changing directories or adding new scraper files.</p>
                <button
                  onClick={() => reloadScrapersMutation.mutate()}
                  disabled={reloadScrapersMutation.isPending}
                  className="inline-flex items-center gap-2 rounded-xl border border-border px-3 py-2 text-sm text-foreground hover:border-accent hover:text-accent disabled:opacity-60"
                >
                  {reloadScrapersMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
                  Reload scrapers
                </button>
              </div>

              {scrapersLoading ? (
                <div className="flex items-center gap-2 text-sm text-secondary">
                  <Loader2 className="h-4 w-4 animate-spin" /> Loading scrapers...
                </div>
              ) : scrapersError ? (
                <div className="rounded-xl border border-red-500/40 bg-red-500/10 p-4 text-sm text-red-200">
                  Failed to load scrapers: {scrapersError instanceof Error ? scrapersError.message : "Unknown error"}
                </div>
              ) : scrapers.length === 0 ? (
                <div className="rounded-xl border border-dashed border-border p-4 text-sm text-secondary">
                  No YAML or extension scraper definitions are currently loaded.
                </div>
              ) : (
                <div className="space-y-4">
                  {Object.entries(groupedScrapers).map(([entityType, entityScrapers]) => (
                    <ScraperTable key={entityType} entityType={entityType} scrapers={entityScrapers} />
                  ))}
                </div>
              )}
            </SectionCard>
            )}
          </>
        )}

        {resolvedActiveTab === "users" && <UsersTab />}
        {resolvedActiveTab === "roles" && <RolesTab />}
        {resolvedActiveTab === "content-rules" && <ContentRulesTab />}
        {resolvedActiveTab === "api-tokens" && <ApiTokensTab />}
        {resolvedActiveTab === "share-links" && <ShareLinksTab />}
        {resolvedActiveTab === "audit" && <AuditTab />}

        {(["server-host-network", "server-ffmpeg-transcoding"] as SettingsTab[]).includes(resolvedActiveTab) && (
          <>
            {resolvedActiveTab === "server-host-network" && (
            <SectionCard title="Server" description="Host and port are persisted immediately but require a restart to rebind the listener.">
              <div className="grid gap-4 md:grid-cols-2">
                <TextField
                  label="Host"
                  value={draft.host}
                  onChange={(value) => updateDraft((current) => ({ ...current, host: value }))}
                />
                <NumberField
                  label="Port"
                  value={draft.port}
                  min={1}
                  onChange={(value) => updateDraft((current) => ({ ...current, port: value ?? current.port }))}
                />
              </div>
              <div className="mt-4">
                <SelectField
                  label="Frame extraction"
                  value={draft.frameExtractionMode === "managed" ? "managed" : "external"}
                  onChange={(value) => updateDraft((d) => ({ ...d, frameExtractionMode: value }))}
                  options={[
                    { value: "external", label: "External (ffmpeg CLI)" },
                    { value: "managed", label: "Managed (in-process)" },
                  ]}
                />
                <p className="mt-1 text-xs text-secondary">
                  How Cove extracts frames for thumbnails, sprites, and phashes. <span className="font-medium">External</span> spawns the ffmpeg CLI — most compatible and crash-isolated. <span className="font-medium">Managed</span> decodes in-process for much higher throughput.
                  <span className="text-red-300 font-medium"> Warning:</span> managed mode can fatally crash the process on some systems (e.g. missing native drivers, or rare malformed files); switch back to external if you hit instability.
                </p>
              </div>
              <div className="mt-4">
                <CheckboxLabel
                  label="Enable hardware acceleration (managed mode)"
                  checked={draft.enableFfmpegHwAccel}
                  onChange={(checked) => updateDraft((current) => ({ ...current, enableFfmpegHwAccel: checked }))}
                />
                <p className="mt-1 text-xs text-secondary">
                  When using managed frame extraction, attempt hardware-accelerated decoding for phash and sprite generation.
                  <span className="text-red-300 font-medium"> Warning:</span> In some Docker environments, this may cause a fatal process crash due to missing native drivers.
                </p>
              </div>
            </SectionCard>
            )}

            {resolvedActiveTab === "server-ffmpeg-transcoding" && (
            <>
            <SectionCard title="FFmpeg" description="Paths to FFmpeg and FFprobe binaries. Leave blank to use system PATH.">
              <div className="grid gap-4 md:grid-cols-2">
                <TextField
                  label="FFmpeg path"
                  value={draft.ffmpegPath ?? ""}
                  onChange={(value) => updateDraft((d) => ({ ...d, ffmpegPath: value || undefined }))}
                  placeholder="C:\\ffmpeg\\bin\\ffmpeg.exe"
                />
                <TextField
                  label="FFprobe path"
                  value={draft.ffprobePath ?? ""}
                  onChange={(value) => updateDraft((d) => ({ ...d, ffprobePath: value || undefined }))}
                  placeholder="C:\\ffmpeg\\bin\\ffprobe.exe"
                />
              </div>
            </SectionCard>

            <SectionCard title="Transcoding" description="Hardware acceleration and transcode size limits. 0 means original resolution.">
              <div className="space-y-4">
                <div className="grid gap-4 md:grid-cols-2">
                  <NumberField
                    label="Max transcode size"
                    value={draft.maxTranscodeSize}
                    min={0}
                    onChange={(value) => updateDraft((d) => ({ ...d, maxTranscodeSize: value ?? d.maxTranscodeSize }))}
                  />
                  <NumberField
                    label="Max streaming transcode size"
                    value={draft.maxStreamingTranscodeSize}
                    min={0}
                    onChange={(value) => updateDraft((d) => ({ ...d, maxStreamingTranscodeSize: value ?? d.maxStreamingTranscodeSize }))}
                  />
                </div>
                <SelectField
                  label="Hardware acceleration"
                  value={draft.transcodeHardwareAcceleration}
                  onChange={(value) => updateDraft((d) => ({ ...d, transcodeHardwareAcceleration: value }))}
                  options={[
                    { value: "none", label: "None" },
                    { value: "nvenc", label: "NVENC" },
                    { value: "vaapi", label: "VAAPI" },
                    { value: "qsv", label: "QSV" },
                  ]}
                />
                <div className="grid gap-4 md:grid-cols-2">
                  <TextField
                    label="Transcode input args"
                    value={draft.transcodeInputArgs ?? ""}
                    onChange={(value) => updateDraft((d) => ({ ...d, transcodeInputArgs: value || undefined }))}
                  />
                  <TextField
                    label="Transcode output args"
                    value={draft.transcodeOutputArgs ?? ""}
                    onChange={(value) => updateDraft((d) => ({ ...d, transcodeOutputArgs: value || undefined }))}
                  />
                  <TextField
                    label="Live transcode input args"
                    value={draft.liveTranscodeInputArgs ?? ""}
                    onChange={(value) => updateDraft((d) => ({ ...d, liveTranscodeInputArgs: value || undefined }))}
                  />
                  <TextField
                    label="Live transcode output args"
                    value={draft.liveTranscodeOutputArgs ?? ""}
                    onChange={(value) => updateDraft((d) => ({ ...d, liveTranscodeOutputArgs: value || undefined }))}
                  />
                </div>
              </div>
            </SectionCard>
            </>
            )}
          </>
        )}

        {resolvedActiveTab === "extensions-installed" && <ExtensionsPanel mode="installed" />}
        {activeExtensionSettingsTab && (
          <>
            {renderExtensionSettingsPanels(
              activeExtensionSettingsTab.key,
              activeExtensionSettingsTab.label,
              activeExtensionSettingsTab.description ?? `Settings provided by installed extensions for ${activeExtensionSettingsTab.label}.`,
            )}
          </>
        )}
        {resolvedActiveTab === "extensions-registry" && <ExtensionsPanel mode="registry" />}

        {resolvedActiveTab === "logs" && <LogsPanel />}
        {resolvedActiveTab === "system-info-about" && (
          <>
            <SectionCard title="About Cove" description="An organizer for your media library.">
              <div className="flex items-start gap-6">
                <div className="w-16 h-16 rounded-xl bg-accent/20 flex items-center justify-center shrink-0">
                  <span className="text-3xl font-bold text-accent">S</span>
                </div>
                <div className="space-y-2">
                  <h2 className="text-2xl font-bold text-foreground">Cove</h2>
                  {status && <p className="text-sm text-secondary">Version {status.version}</p>}
                  <p className="text-sm text-muted max-w-lg">
                    A self-hosted media organizer and video streaming app. Organize, tag, and browse your media library with ease.
                  </p>
                  <div className="flex gap-3 pt-1">
                    <a href="https://github.com/yourcove/cove" target="_blank" rel="noopener noreferrer" className="text-xs text-accent hover:underline">GitHub</a>
                    <a href="https://docs.cove.app" target="_blank" rel="noopener noreferrer" className="text-xs text-accent hover:underline">Documentation</a>
                    <a href="https://discord.gg/EzM8764YVr" target="_blank" rel="noopener noreferrer" className="text-xs text-accent hover:underline">Discord</a>
                  </div>
                  <button
                    type="button"
                    onClick={() => openTutorialStoryboard("getting-started")}
                    className="inline-flex w-fit items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary transition-colors hover:border-accent hover:text-foreground"
                  >
                    <BookOpen className="h-4 w-4" />
                    Replay setup tour
                  </button>
                </div>
              </div>
            </SectionCard>

            <SectionCard title="Release History" description="Recent releases and what changed.">
              <div className="space-y-6">
                {recentChangelog(3).map((entry) => (
                  <div key={entry.version} className="border-l-2 border-accent pl-4">
                    <h3 className="text-lg font-semibold text-foreground">v{entry.version}</h3>
                    <p className="text-xs text-muted mt-1">
                      {entry.date}{entry.summary ? ` — ${entry.summary}` : ""}
                    </p>
                    <ul className="mt-3 space-y-2 text-sm text-secondary">
                      {entry.highlights.map((highlight, index) => (
                        <li key={index} className="flex items-start gap-2">
                          <span className="text-emerald-400 mt-0.5">•</span> {highlight}
                        </li>
                      ))}
                    </ul>
                  </div>
                ))}
              </div>
            </SectionCard>
          </>
        )}

        {resolvedActiveTab === "system-info-runtime-status" && (
          <>
            {canShutdownSystem ? (
              <SectionCard title="Shutdown" description="Stop the current Cove server process after pending requests complete.">
                <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                  <div>
                    <h3 className="text-sm font-medium text-foreground">Shutdown server</h3>
                    <p className="mt-1 text-sm text-secondary">The browser will lose connection until Cove is started again.</p>
                  </div>
                  <button
                    type="button"
                    onClick={() => {
                      if (window.confirm("Shut down the Cove server?")) {
                        shutdownMutation.mutate();
                      }
                    }}
                    disabled={shutdownMutation.isPending}
                    className="inline-flex items-center justify-center gap-2 rounded-xl bg-red-600 px-3 py-2 text-sm font-medium text-white transition hover:bg-red-500 disabled:cursor-not-allowed disabled:opacity-60"
                  >
                    {shutdownMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Power className="h-4 w-4" />}
                    Shutdown
                  </button>
                </div>
              </SectionCard>
            ) : null}

            <SectionCard
              title="Runtime Status"
              description="Effective values reported by the running backend instance."
              actions={<CopyDebugInfoButton getReport={() => buildDebugReport(status, draftState)} />}
            >
              {statusLoading && !status ? (
                <div className="flex items-center gap-2 text-sm text-secondary">
                  <Loader2 className="h-4 w-4 animate-spin" /> Loading status...
                </div>
              ) : status ? (
                <dl className="grid gap-4 md:grid-cols-2">
                  <InfoPair label="Version" value={status.version} />
                  <InfoPair label="Database" value={status.databasePath} />
                  {status.configFile ? <InfoPair label="Config file" value={status.configFile} /> : null}
                  {status.appDir ? <InfoPair label="App directory" value={status.appDir} /> : null}
                </dl>
              ) : (
                <div className="text-sm text-secondary">Runtime status is unavailable.</div>
              )}
            </SectionCard>

            <SectionCard title="System Information" description="Browser and environment details.">
              <dl className="grid gap-4 md:grid-cols-2">
                <InfoPair label="Browser" value={navigator.userAgent.split(/[()]/)[1] || navigator.userAgent.substring(0, 60)} />
                <InfoPair label="Platform" value={navigator.platform} />
                <InfoPair label="Screen resolution" value={`${screen.width}×${screen.height}`} />
                <InfoPair label="Language" value={navigator.language} />
              </dl>
            </SectionCard>

            {draftState ? (
              <SectionCard title="Current Config Summary" description="High-level values from the effective client-side config object.">
                <dl className="grid gap-4 md:grid-cols-2">
                  <InfoPair label="Library paths" value={String(draftState.covePaths.filter((path) => path.path.trim() !== "").length)} />
                  <InfoPair label="Scraper directories" value={String(draftState.scraping.scraperDirectories.filter(Boolean).length)} />
                  <InfoPair label="Metadata Servers" value={String(draftState.scraping.metadataServers.filter((box) => box.endpoint.trim() !== "").length)} />
                  <InfoPair label="Rating system" value={draftState.ui.ratingSystemOptions.type} />
                  <InfoPair label="Authentication" value={draftState.security.enabled ? "enabled" : "disabled"} />
                </dl>
              </SectionCard>
            ) : (
              <SectionCard title="Current Config Summary" description="High-level values from the effective client-side config object.">
                <div className="text-sm text-secondary">Config summary requires system read access.</div>
              </SectionCard>
            )}
          </>
        )}
      </div>
    </div>
  );
}

function LocalInterfacePanel({
  serverRatingOptions,
}: {
  serverRatingOptions?: Partial<RatingSystemOptions> | null;
}) {
  const { authEnabled, user } = useAuth();
  const accountBackedPreferences = supportsServerBackedUiPreferences(user);
  const sharedProfilePreferences = accountBackedPreferences && !authEnabled;
  const [localRatingOverride, setLocalRatingOverride] = useState<RatingSystemOptions | null>(() => readStoredRatingOptionsOverride());
  const [trackingPreferences, setTrackingPreferences] = useState<ResolvedTrackingPreferences>(() => resolveTrackingPreferences(user?.uiPreferences?.tracking));

  useEffect(() => {
    setLocalRatingOverride(readStoredRatingOptionsOverride());
  }, [serverRatingOptions, user]);

  useEffect(() => {
    setTrackingPreferences(resolveTrackingPreferences(user?.uiPreferences?.tracking));
  }, [user]);

  const effectiveRatingOptions = localRatingOverride ?? normalizeRatingOptions(serverRatingOptions ?? defaultRatingSystemOptions);

  const updateRatingOptions = (nextOptions: RatingSystemOptions | null) => {
    writeStoredRatingOptionsOverride(nextOptions);
    setLocalRatingOverride(nextOptions);
  };

  const updateTrackingPreferences = (patch: Partial<ResolvedTrackingPreferences>) => {
    const nextTracking = {
      ...defaultTrackingPreferences,
      ...trackingPreferences,
      ...patch,
    };
    setTrackingPreferences(nextTracking);
    updateAuthenticatedUserUiPreferences((current) => ({
      ...(current ?? {}),
      tracking: nextTracking,
    }));
  };

  return (
    <>
      <SectionCard
        title={sharedProfilePreferences ? "Shared Appearance" : accountBackedPreferences ? "Personal Appearance" : "Local Appearance"}
        description={sharedProfilePreferences
          ? "These preferences are stored in Cove's shared built-in profile, so they carry across browsers and devices while authentication is disabled."
          : accountBackedPreferences
            ? "These preferences follow your signed-in account across browsers. When signed out, the browser-local values are still used as a fallback."
            : "These preferences are stored in this browser and do not change the server configuration."}
      >
        <div className="space-y-4">
          <div className="flex flex-col gap-3 rounded-xl border border-border bg-card px-4 py-3 md:flex-row md:items-center md:justify-between">
            <p className="text-sm text-secondary">Choose whether ratings follow the system default or your personal display preference.</p>
            <button
              onClick={() => updateRatingOptions(null)}
              disabled={!localRatingOverride}
              className="inline-flex items-center justify-center rounded-xl border border-border px-3 py-2 text-sm text-foreground hover:border-accent hover:text-accent disabled:cursor-not-allowed disabled:opacity-60"
            >
              Use system default
            </button>
          </div>

          <div className="grid gap-4 md:grid-cols-2">
            <SelectField
              label="Rating system"
              value={effectiveRatingOptions.type}
              onChange={(value) =>
                updateRatingOptions(normalizeRatingOptions({
                  ...effectiveRatingOptions,
                  type: value as RatingSystemType,
                  starPrecision: value === "decimal" ? "full" : effectiveRatingOptions.starPrecision,
                }))
              }
              options={ratingSystemOptions}
            />
            {effectiveRatingOptions.type === "stars" ? (
              <SelectField
                label="Star precision"
                value={effectiveRatingOptions.starPrecision}
                onChange={(value) =>
                  updateRatingOptions(normalizeRatingOptions({
                    ...effectiveRatingOptions,
                    starPrecision: value as RatingStarPrecision,
                  }))
                }
                options={starPrecisionOptions}
              />
            ) : (
              <div className="rounded-xl border border-border bg-card px-4 py-3 text-sm text-secondary">
                Decimal ratings always use 0.1 steps.
              </div>
            )}
          </div>
        </div>
      </SectionCard>
    </>
  );
}

function UserSettingsPanel({ activeTab }: { activeTab: SettingsTab }) {
  const { authEnabled, user, logout } = useAuth();
  const accountBackedPreferences = supportsServerBackedUiPreferences(user);
  const sharedProfilePreferences = accountBackedPreferences && !authEnabled;
  const [trackingPreferences, setTrackingPreferences] = useState<ResolvedTrackingPreferences>(() => resolveTrackingPreferences(user?.uiPreferences?.tracking));
  const [logoutPending, setLogoutPending] = useState(false);

  const handleLogout = async () => {
    setLogoutPending(true);
    try {
      await logout();
      navigateToUrl("/login", { replace: true });
    } finally {
      setLogoutPending(false);
    }
  };

  useEffect(() => {
    setTrackingPreferences(resolveTrackingPreferences(user?.uiPreferences?.tracking));
  }, [user]);

  const updateTrackingPreferences = (patch: Partial<ResolvedTrackingPreferences>) => {
    const nextTracking = {
      ...defaultTrackingPreferences,
      ...trackingPreferences,
      ...patch,
    };
    setTrackingPreferences(nextTracking);
    updateAuthenticatedUserUiPreferences((current) => ({
      ...(current ?? {}),
      tracking: nextTracking,
    }));
  };

  if (!accountBackedPreferences) {
    return (
      <SectionCard
        title="User Settings"
        description="Sign in or use the shared built-in profile to store engagement preferences outside this browser."
      >
        <p className="text-sm text-secondary">No account-backed user settings are available in the current session.</p>
      </SectionCard>
    );
  }

  return (
    <div className="space-y-5">
      {activeTab === "my-account" && (
      <SectionCard title="Account" description="Current sign-in controls for this browser session.">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h3 className="text-sm font-medium text-foreground">{user?.username ?? "Current user"}</h3>
            <p className="mt-1 text-sm text-secondary">End this session and return to the sign-in screen.</p>
          </div>
          <button
            type="button"
            onClick={handleLogout}
            disabled={logoutPending}
            className="inline-flex items-center justify-center gap-2 rounded-xl border border-border bg-card px-3 py-2 text-sm font-medium text-foreground transition hover:border-accent hover:text-accent disabled:cursor-not-allowed disabled:opacity-60"
          >
            {logoutPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <LogOut className="h-4 w-4" />}
            Logout
          </button>
        </div>
      </SectionCard>
      )}

      {activeTab === "my-activity-history" && (
      <SectionCard
        title={sharedProfilePreferences ? "Shared Engagement" : "Personal Engagement"}
        description={sharedProfilePreferences
          ? "These preferences are stored in Cove's shared built-in profile and control activity recording for the current profile."
          : "These preferences follow your signed-in account and control activity recording for your own profile."}
      >
        <div className="space-y-4">
          <CheckboxLabel
            label="Enable engagement history"
            checked={trackingPreferences.enabled ?? defaultTrackingPreferences.enabled}
            onChange={(checked) => updateTrackingPreferences({ enabled: checked })}
          />
          <div className="grid gap-4 md:grid-cols-2">
            <NumberField
              label="Minimum video view seconds"
              value={trackingPreferences.minViewSeconds ?? defaultTrackingPreferences.minViewSeconds}
              min={0}
              onChange={(value) => updateTrackingPreferences({ minViewSeconds: value ?? defaultTrackingPreferences.minViewSeconds })}
            />
            <NumberField
              label="Video completion ratio"
              value={trackingPreferences.viewCompletionRatio ?? defaultTrackingPreferences.viewCompletionRatio}
              min={0.01}
              max={1}
              onChange={(value) => updateTrackingPreferences({ viewCompletionRatio: value ?? defaultTrackingPreferences.viewCompletionRatio })}
            />
            <NumberField
              label="Minimum image view seconds"
              value={trackingPreferences.minImageDetailViewSeconds ?? defaultTrackingPreferences.minImageDetailViewSeconds}
              min={0}
              onChange={(value) => updateTrackingPreferences({ minImageDetailViewSeconds: value ?? defaultTrackingPreferences.minImageDetailViewSeconds })}
            />
            <NumberField
              label="Minimum session length for derived likes"
              value={trackingPreferences.minDerivedLikeSessionSeconds ?? defaultTrackingPreferences.minDerivedLikeSessionSeconds}
              min={0}
              onChange={(value) => updateTrackingPreferences({ minDerivedLikeSessionSeconds: value ?? defaultTrackingPreferences.minDerivedLikeSessionSeconds })}
            />
            <NumberField
              label="Session idle timeout seconds"
              value={trackingPreferences.sessionIdleTimeoutSec ?? defaultTrackingPreferences.sessionIdleTimeoutSec}
              min={10}
              onChange={(value) => updateTrackingPreferences({ sessionIdleTimeoutSec: value ?? defaultTrackingPreferences.sessionIdleTimeoutSec })}
            />
          </div>
        </div>
      </SectionCard>
      )}
    </div>
  );
}
function LogsPanel() {
  const { config } = useAppConfig();
  const { hasPermission } = useAuth();
  const canWriteSystemSettings = hasPermission("system.settings.write");
  const [clientFilter, setClientFilter] = useState("");
  const [serverLogLevel, setServerLogLevel] = useState(config?.logLevel ?? "Info");
  const [tailEntries, setTailEntries] = useState<LogEntry[]>([]);
  const [streamError, setStreamError] = useState<string | null>(null);
  const { data: initialLogEntries, isLoading } = useQuery({
    queryKey: ["logs", "tail"],
    queryFn: () => logsApi.recent(undefined, 200),
  });

  const setLogLevelMutation = useMutation({
    mutationFn: system.setLogLevel,
    onSuccess: (result) => setServerLogLevel(result.level),
    onError: (error: Error) => setStreamError(error.message),
  });

  useEffect(() => {
    if (config?.logLevel) {
      setServerLogLevel(config.logLevel);
    }
  }, [config?.logLevel]);

  useEffect(() => {
    if (initialLogEntries) {
      setTailEntries(initialLogEntries.slice(-200));
    }
  }, [initialLogEntries]);

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/logs")
      .withAutomaticReconnect()
      .build();

    connection.on("LogReceived", (entry: LogEntry) => {
      setTailEntries((current) => [...current, entry].slice(-200));
    });

    connection.start()
      .then(() => setStreamError(null))
      .catch((error) => setStreamError(error instanceof Error ? error.message : "Failed to connect to log stream."));

    return () => {
      void connection.stop();
    };
  }, []);

  const filteredLogEntries = clientFilter
    ? tailEntries.filter((entry) => normalizeLogLevel(entry.level) === clientFilter)
    : tailEntries;

  const levelColor = (level: string) => {
    switch (level.toLowerCase()) {
      case "error": case "critical": return "text-red-400";
      case "warning": return "text-yellow-400";
      case "debug": return "text-accent";
      case "trace": return "text-secondary";
      default: return "text-secondary";
    }
  };

  return (
    <SectionCard title="Logs" description="Live log tail from the server.">
      <div className="mb-4 grid gap-3 md:grid-cols-2 md:items-end">
        <SelectField
          label="Filter"
          description="Filter the log rows shown in this browser without changing what the server records."
          value={clientFilter}
          onChange={setClientFilter}
          options={[
            { value: "", label: "All" },
            { value: "Verbose", label: "Trace" },
            { value: "Debug", label: "Debug" },
            { value: "Information", label: "Info" },
            { value: "Warning", label: "Warning" },
            { value: "Error", label: "Error" },
            { value: "Fatal", label: "Critical" },
          ]}
        />
        <SelectField
          label="Server log level"
          description="Change the live server log verbosity and persist the selected level to config."
          value={serverLogLevel}
          onChange={(value) => {
            setServerLogLevel(value);
            setLogLevelMutation.mutate(value);
          }}
          options={[
            { value: "Trace", label: "Trace" },
            { value: "Debug", label: "Debug" },
            { value: "Info", label: "Info" },
            { value: "Warning", label: "Warning" },
            { value: "Error", label: "Error" },
            { value: "Critical", label: "Critical" },
          ]}
          disabled={!canWriteSystemSettings || setLogLevelMutation.isPending}
        />
      </div>
      {streamError ? <p className="mb-3 text-sm text-red-300">{streamError}</p> : null}
      {isLoading ? (
        <div className="flex items-center gap-2 text-sm text-secondary">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading logs...
        </div>
      ) : filteredLogEntries.length > 0 ? (
        <div className="max-h-[600px] overflow-y-auto rounded border border-border bg-background font-mono text-xs">
          {filteredLogEntries.map((entry, i) => (
            <div key={i} className="grid grid-cols-[auto_auto_minmax(0,1fr)] items-start gap-3 border-b border-border/50 px-3 py-1.5 hover:bg-surface">
              <span className="whitespace-nowrap text-muted">{entry.timestamp}</span>
              <span className={`whitespace-nowrap font-semibold ${levelColor(entry.level)}`}>{entry.level}</span>
              <span className="min-w-0 break-all text-foreground">{entry.message}</span>
            </div>
          ))}
        </div>
      ) : (
        <p className="text-sm text-muted">No log entries found.</p>
      )}
    </SectionCard>
  );
}

function normalizeLogLevel(level: string) {
  switch (level.toLowerCase()) {
    case "trace":
    case "verbose":
      return "Verbose";
    case "debug":
      return "Debug";
    case "info":
    case "information":
      return "Information";
    case "warning":
    case "warn":
      return "Warning";
    case "error":
      return "Error";
    case "critical":
    case "fatal":
      return "Fatal";
    default:
      return level;
  }
}

function TasksPanel({ activeTab, midSlot }: { activeTab: SettingsTab; midSlot?: React.ReactNode }) {
  const queryClient = useQueryClient();
  const { data: activeJobs, refetch: refetchJobs } = useQuery({
    queryKey: ["jobs"],
    queryFn: () => jobs.list(),
    refetchInterval: 2000,
  });
  const { data: recentJobs } = useQuery({
    queryKey: ["jobs-history"],
    queryFn: () => jobs.history(),
    refetchInterval: 2000,
  });

  // ---- Job Queue ----
  const pendingJobs = activeJobs?.filter((job) => job.status === "pending") ?? [];
  const moveQueuedJob = (job: JobInfo, direction: "up" | "down") => {
    const index = pendingJobs.findIndex((item) => item.id === job.id);
    if (index < 0) return;

    const beforeJobId = direction === "up"
      ? pendingJobs[index - 1]?.id
      : pendingJobs[index + 2]?.id ?? null;

    void jobs.reorder(job.id, beforeJobId).then(() => refetchJobs());
  };

  const jobQueue = activeJobs && activeJobs.length > 0 ? (
    <SectionCard title="Job Queue" description="Currently running or queued jobs.">
      <div className="space-y-2">
        {activeJobs.map((job) => {
          const pendingIndex = pendingJobs.findIndex((item) => item.id === job.id);
          return (
            <JobCard
              key={job.id}
              job={job}
              variant="panel"
              onCancel={(id) => jobs.cancel(id).then(() => refetchJobs())}
              onMoveUp={job.status === "pending" && pendingIndex > 0 ? () => moveQueuedJob(job, "up") : undefined}
              onMoveDown={job.status === "pending" && pendingIndex >= 0 && pendingIndex < pendingJobs.length - 1 ? () => moveQueuedJob(job, "down") : undefined}
            />
          );
        })}
      </div>
    </SectionCard>
  ) : null;

  const jobHistory = recentJobs && recentJobs.length > 0 ? (
    <SectionCard title="Recent Jobs" description={`Recently completed, failed, or cancelled jobs (${recentJobs.length}).`}>
      <div className="max-h-[32rem] space-y-2 overflow-y-auto pr-1">
        {recentJobs.map((job) => (
          <JobCard key={job.id} job={job} variant="panel" />
        ))}
      </div>
    </SectionCard>
  ) : null;

  return (
    <>
      {activeTab === "operations-jobs" && jobQueue}
      {activeTab === "operations-jobs" && midSlot}
      {activeTab === "operations-jobs" && jobHistory}
      {activeTab === "operations-jobs" && !jobQueue && !jobHistory ? (
        <SectionCard title="Jobs" description="Currently running, queued, and recent jobs.">
          <p className="text-sm text-secondary">No jobs are running or recently completed.</p>
        </SectionCard>
      ) : null}
      {activeTab === "operations-scan-generate" && <LibraryTasksSection refetchJobs={refetchJobs} mode="scan-generate" />}
      {activeTab === "operations-downloads" && <LibraryTasksSection refetchJobs={refetchJobs} mode="downloads" />}
      {activeTab === "operations-duplicates" && <LibraryTasksSection refetchJobs={refetchJobs} mode="duplicates" />}
      {activeTab === "operations-maintenance" && <DataManagementSection refetchJobs={refetchJobs} mode="maintenance" />}
      {activeTab === "operations-backup-restore" && <DataManagementSection refetchJobs={refetchJobs} mode="backup" />}
      {activeTab === "operations-extension-tasks" && <ExtensionTasksSection refetchJobs={refetchJobs} />}
    </>
  );
}


// ---- Library Tasks ----
type LibraryTaskSectionMode = "scan-generate" | "downloads" | "duplicates";

function LibraryTasksSection({ refetchJobs, mode }: { refetchJobs: () => void; mode: LibraryTaskSectionMode }) {
  const { config } = useAppConfig();
  const selectablePaths = useMemo(
    () => (config?.covePaths ?? []).map((path) => path.path.trim()).filter(Boolean),
    [config?.covePaths],
  );
  const [showScanOpts, setShowScanOpts] = useState(false);
  const [scanOpts, setScanOpts] = useState<ScanOptions>(() => loadStoredTaskOptions(TASK_SCAN_OPTIONS_KEY, DEFAULT_SCAN_OPTIONS));

  const [showGenOpts, setShowGenOpts] = useState(false);
  const [genOpts, setGenOpts] = useState<GenerateOptions>(() => loadStoredTaskOptions(TASK_GENERATE_OPTIONS_KEY, DEFAULT_GENERATE_OPTIONS));
  const [showDownloadImportOpts, setShowDownloadImportOpts] = useState(false);
  const [downloadImportEntity, setDownloadImportEntity] = useState<DownloadSelectionEntity>(() => {
    const stored = loadStoredTaskOptions(TASK_DOWNLOAD_IMPORT_OPTIONS_KEY, { entity: "Video" as DownloadSelectionEntity });
    return stored.entity as DownloadSelectionEntity;
  });
  const [downloadImportFile, setDownloadImportFile] = useState<File | null>(null);
  const [downloadImportAutoApplyMetadata, setDownloadImportAutoApplyMetadata] = useState(() => {
    const stored = loadStoredTaskOptions(TASK_DOWNLOAD_IMPORT_OPTIONS_KEY, { scrapeVideos: false });
    return !!stored.scrapeVideos;
  });
  const [downloadImportAllowDuplicateDownloads, setDownloadImportAllowDuplicateDownloads] = useState(() => {
    const stored = loadStoredTaskOptions(TASK_DOWNLOAD_IMPORT_OPTIONS_KEY, { allowDuplicateDownloads: false });
    return !!stored.allowDuplicateDownloads;
  });
  const [downloadImportGenerateOpts, setDownloadImportGenerateOpts] = useState<GenerateOptions>(() => {
    const stored = loadStoredTaskOptions(TASK_DOWNLOAD_IMPORT_OPTIONS_KEY, { generate: DEFAULT_BATCH_DOWNLOAD_GENERATE_OPTIONS });
    return { ...DEFAULT_BATCH_DOWNLOAD_GENERATE_OPTIONS, ...(stored.generate ?? {}) };
  });
  const [downloadImportCachedUrls, setDownloadImportCachedUrls] = useState<string[]>(() => {
    const stored = loadStoredTaskOptions(TASK_DOWNLOAD_IMPORT_CACHE_KEY, { urls: [] as string[] });
    return Array.isArray(stored.urls) ? stored.urls.filter((value: unknown): value is string => typeof value === "string") : [];
  });
  const [downloadImportCachedFileName, setDownloadImportCachedFileName] = useState(() => {
    const stored = loadStoredTaskOptions(TASK_DOWNLOAD_IMPORT_CACHE_KEY, { fileName: "" });
    return typeof stored.fileName === "string" ? stored.fileName : "";
  });
  const [downloadImportStatus, setDownloadImportStatus] = useState<{ type: "success" | "error"; text: string } | null>(null);

  useEffect(() => {
    localStorage.setItem(TASK_SCAN_OPTIONS_KEY, JSON.stringify(scanOpts));
  }, [scanOpts]);

  useEffect(() => {
    localStorage.setItem(TASK_GENERATE_OPTIONS_KEY, JSON.stringify(genOpts));
  }, [genOpts]);

  useEffect(() => {
    localStorage.setItem(TASK_DOWNLOAD_IMPORT_OPTIONS_KEY, JSON.stringify({
      entity: downloadImportEntity,
      scrapeVideos: downloadImportAutoApplyMetadata,
      allowDuplicateDownloads: downloadImportAllowDuplicateDownloads,
      generate: downloadImportGenerateOpts,
    }));
  }, [downloadImportAllowDuplicateDownloads, downloadImportAutoApplyMetadata, downloadImportEntity, downloadImportGenerateOpts]);

  useEffect(() => {
    localStorage.setItem(TASK_DOWNLOAD_IMPORT_CACHE_KEY, JSON.stringify({
      fileName: downloadImportCachedFileName,
      urls: downloadImportCachedUrls,
    }));
  }, [downloadImportCachedFileName, downloadImportCachedUrls]);

  // Selected folder paths (library roots and/or drilled-down subfolders). Empty means "everything".
  const scanSelectedPaths = scanOpts.paths ?? [];
  const effectiveScanOpts = useMemo<ScanOptions>(
    () => ({ ...scanOpts, paths: (scanOpts.paths?.length ?? 0) > 0 ? scanOpts.paths : undefined }),
    [scanOpts],
  );

  const toggleScanPath = (path: string, checked: boolean) => {
    setScanOpts((current) => {
      const next = new Set(current.paths ?? []);
      if (checked) next.add(path); else next.delete(path);
      return { ...current, paths: Array.from(next) };
    });
  };

  const genSelectedPaths = genOpts.paths ?? [];
  const effectiveGenOpts = useMemo<GenerateOptions>(
    () => ({ ...genOpts, paths: (genOpts.paths?.length ?? 0) > 0 ? genOpts.paths : undefined }),
    [genOpts],
  );

  const toggleGenPath = (path: string, checked: boolean) => {
    setGenOpts((current) => {
      const next = new Set(current.paths ?? []);
      if (checked) next.add(path); else next.delete(path);
      return { ...current, paths: Array.from(next) };
    });
  };

  const scanMut = useMutation({ mutationFn: () => metadata.scan(effectiveScanOpts), onSuccess: () => refetchJobs() });
  const genMut = useMutation({ mutationFn: () => metadata.generate(effectiveGenOpts), onSuccess: () => refetchJobs() });
  const downloadImportMut = useMutation({
    mutationFn: async () => {
      let urls: string[];
      if (downloadImportFile) {
        urls = linesToList(await downloadImportFile.text()).map((value) => value.trim()).filter(Boolean);
        setDownloadImportCachedUrls(urls);
        setDownloadImportCachedFileName(downloadImportFile.name);
      } else {
        urls = downloadImportCachedUrls;
      }

      if (urls.length === 0) {
        throw new Error("Choose a text file with one URL per line, or reuse the last imported URLs.");
      }

      return queueImportedUrlDownloads(downloadImportEntity, urls, {
        scrapeVideos: downloadImportAutoApplyMetadata,
        allowDuplicateDownloads: downloadImportAllowDuplicateDownloads,
        generate: downloadImportGenerateOpts,
      });
    },
    onSuccess: (result) => {
      refetchJobs();
      const summary = formatBatchDownloadSummary(downloadImportEntity.toLowerCase(), result);
      setDownloadImportStatus({
        type: result.queuedCount > 0 ? "success" : "error",
        text: summary,
      });
    },
    onError: (error: Error) => {
      setDownloadImportStatus({
        type: "error",
        text: error.message || "Failed to queue the URL imports.",
      });
    },
  });

  const sectionMeta: Record<LibraryTaskSectionMode, { title: string; description: string }> = {
    "scan-generate": {
      title: "Scan & Generate",
      description: "Scan library roots and generate supporting files such as thumbnails, previews, sprites, hashes, and checksums.",
    },
    downloads: {
      title: "Downloads",
      description: "Read one URL per line from a text file and queue a backend batch download job.",
    },
    duplicates: {
      title: "Duplicates",
      description: "Open duplicate detection to compare exact duplicate video files and choose what to remove.",
    },
  };

  return (
    <>
    <SectionCard title={sectionMeta[mode].title} description={sectionMeta[mode].description}>
      <div className="space-y-4">
        {mode === "scan-generate" && (
        <>
        {/* Scan */}
        <TaskCard
          label="Scan"
          description="Scan library paths for new content and update metadata."
          onRun={() => scanMut.mutate()}
          isPending={scanMut.isPending}
          expandable
          expanded={showScanOpts}
          onToggleExpand={() => setShowScanOpts(!showScanOpts)}
        >
          <div className="space-y-3 pt-3 border-t border-border/50">
            <p className="text-xs text-muted font-medium uppercase tracking-wide">Video options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Thumbnails / screenshots" checked={!!scanOpts.scanGenerateCovers} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateCovers: c })} />
              <CheckboxLabel label="Video previews" checked={!!scanOpts.scanGeneratePreviews} onChange={(c) => setScanOpts({ ...scanOpts, scanGeneratePreviews: c })} />
              <CheckboxLabel label="Sprite sheets" checked={!!scanOpts.scanGenerateSprites} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateSprites: c })} />
              <CheckboxLabel label="Perceptual hashes (phash)" checked={!!scanOpts.scanGeneratePhashes} onChange={(c) => setScanOpts({ ...scanOpts, scanGeneratePhashes: c })} />
              <CheckboxLabel label="MD5 checksums" checked={!!scanOpts.scanGenerateMd5} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateMd5: c })} />
            </div>
            <p className="text-xs text-muted font-medium uppercase tracking-wide pt-2">Image options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Image thumbnails" checked={!!scanOpts.scanGenerateThumbnails} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateThumbnails: c })} />
              <CheckboxLabel label="Image phashes" checked={!!scanOpts.scanGenerateImagePhashes} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateImagePhashes: c })} />
            </div>
            <p className="text-xs text-muted font-medium uppercase tracking-wide pt-2">Audio options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Audio perceptual hashes" checked={!!scanOpts.scanGenerateAudioPhashes} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateAudioPhashes: c })} />
            </div>
            <p className="text-xs text-muted font-medium uppercase tracking-wide pt-2">Text options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Text perceptual hashes" checked={!!scanOpts.scanGenerateTextPhashes} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateTextPhashes: c })} />
            </div>
            <div className="pt-2">
              <CheckboxLabel label="Force rescan (ignore mtime)" checked={!!scanOpts.rescan} onChange={(c) => setScanOpts({ ...scanOpts, rescan: c })} />
            </div>
            {selectablePaths.length > 0 && (
              <div className="space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <p className="text-xs font-medium text-foreground">Selective scan</p>
                    <p className="text-[11px] text-muted">Pick folders to scan, or leave everything unselected to scan the whole library. Expand a library path to drill into a specific subfolder.</p>
                  </div>
                  {scanSelectedPaths.length > 0 && (
                    <button
                      type="button"
                      onClick={() => setScanOpts({ ...scanOpts, paths: [] })}
                      className="text-[11px] text-accent hover:text-accent-hover"
                    >
                      Clear
                    </button>
                  )}
                </div>
                <LibraryFolderPicker
                  roots={selectablePaths}
                  selected={scanSelectedPaths}
                  onToggle={toggleScanPath}
                  emptyHint="No library paths configured."
                />
              </div>
            )}
          </div>
        </TaskCard>

        {/* Generate */}
        <TaskCard
          label="Generate"
          description="Generate thumbnails, previews, sprites, segments, perceptual hashes, and MD5 checksums."
          onRun={() => genMut.mutate()}
          isPending={genMut.isPending}
          expandable
          expanded={showGenOpts}
          onToggleExpand={() => setShowGenOpts(!showGenOpts)}
        >
          <div className="space-y-3 pt-3 border-t border-border/50">
            <p className="text-xs text-muted font-medium uppercase tracking-wide">Video options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Thumbnails / screenshots" checked={!!genOpts.thumbnails} onChange={(c) => setGenOpts({ ...genOpts, thumbnails: c })} />
              <CheckboxLabel label="Video previews" checked={!!genOpts.previews} onChange={(c) => setGenOpts({ ...genOpts, previews: c })} />
              <CheckboxLabel label="Sprite sheets" checked={!!genOpts.sprites} onChange={(c) => setGenOpts({ ...genOpts, sprites: c })} />
              <CheckboxLabel
                label="Segment thumbnails"
                checked={!!genOpts.segmentThumbnails}
                onChange={(c) => setGenOpts({ ...genOpts, segmentThumbnails: c, segmentPreviews: c ? genOpts.segmentPreviews : false, markers: false })}
              />
              <CheckboxLabel
                label="Animated segment previews"
                checked={!!genOpts.segmentPreviews}
                onChange={(c) => setGenOpts({ ...genOpts, segmentThumbnails: c ? true : genOpts.segmentThumbnails, segmentPreviews: c, markers: false })}
              />
              <CheckboxLabel label="Perceptual hashes (phash)" checked={!!genOpts.phashes} onChange={(c) => setGenOpts({ ...genOpts, phashes: c })} />
              <CheckboxLabel label="MD5 checksums" checked={!!genOpts.md5} onChange={(c) => setGenOpts({ ...genOpts, md5: c })} />
            </div>
            <p className="text-xs text-muted font-medium uppercase tracking-wide pt-2">Image options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Image thumbnails" checked={!!genOpts.imageThumbnails} onChange={(c) => setGenOpts({ ...genOpts, imageThumbnails: c })} />
              <CheckboxLabel label="Image phashes" checked={!!genOpts.imagePhashes} onChange={(c) => setGenOpts({ ...genOpts, imagePhashes: c })} />
            </div>
            <p className="text-xs text-muted font-medium uppercase tracking-wide pt-2">Gallery options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Gallery cover thumbnails" checked={!!genOpts.galleryThumbnails} onChange={(c) => setGenOpts({ ...genOpts, galleryThumbnails: c })} />
            </div>
            <p className="text-xs text-muted font-medium uppercase tracking-wide pt-2">Audio options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Audio perceptual hashes" checked={!!genOpts.audioPhashes} onChange={(c) => setGenOpts({ ...genOpts, audioPhashes: c })} />
            </div>
            <p className="text-xs text-muted font-medium uppercase tracking-wide pt-2">Text options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Text perceptual hashes" checked={!!genOpts.textPhashes} onChange={(c) => setGenOpts({ ...genOpts, textPhashes: c })} />
            </div>
            <div className="pt-2">
              <CheckboxLabel label="Overwrite existing generated files" checked={!!genOpts.overwrite} onChange={(c) => setGenOpts({ ...genOpts, overwrite: c })} />
            </div>
            {selectablePaths.length > 0 && (
              <div className="space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <p className="text-xs font-medium text-foreground">Selective generate</p>
                    <p className="text-[11px] text-muted">Pick folders to generate for, or leave everything unselected to cover the whole library. Expand a library path to drill into a specific subfolder.</p>
                  </div>
                  {genSelectedPaths.length > 0 && (
                    <button
                      type="button"
                      onClick={() => setGenOpts({ ...genOpts, paths: [] })}
                      className="text-[11px] text-accent hover:text-accent-hover"
                    >
                      Clear
                    </button>
                  )}
                </div>
                <LibraryFolderPicker
                  roots={selectablePaths}
                  selected={genSelectedPaths}
                  onToggle={toggleGenPath}
                  emptyHint="No library paths configured."
                />
              </div>
            )}
          </div>
        </TaskCard>
        </>
        )}

        {mode === "duplicates" && (
        <TaskCard
          label="Duplicate Finder"
          description="Find exact duplicate video files and choose which records or files to remove."
          onRun={() => navigateToUrl("/duplicates", { state: { page: "duplicates" } })}
          isPending={false}
          runLabel="Open"
        />
        )}

        {mode === "downloads" && (
        <TaskCard
          label="Download From File"
          description="Read one URL per line from a text file and queue one backend batch job to resolve, create, and download them."
          onRun={() => downloadImportMut.mutate()}
          isPending={downloadImportMut.isPending}
          expandable
          expanded={showDownloadImportOpts}
          onToggleExpand={() => setShowDownloadImportOpts(!showDownloadImportOpts)}
          statusMessage={downloadImportStatus}
        >
          <div className="space-y-3 pt-3 border-t border-border/50">
            <SelectField
              label="Entity type"
              description="Choose what kind of library item each imported URL should create or download."
              value={downloadImportEntity}
              onChange={(value) => {
                setDownloadImportEntity(value as DownloadSelectionEntity);
                setDownloadImportStatus(null);
              }}
              options={[
                { value: "Video", label: "Videos" },
                { value: "Image", label: "Images" },
                { value: "Gallery", label: "Galleries" },
              ]}
            />
            <label className="block text-sm">
              <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">URL file</span>
              <input
                type="file"
                accept=".txt,text/plain"
                onChange={(event) => {
                  setDownloadImportFile(event.target.files?.[0] ?? null);
                  setDownloadImportStatus(null);
                }}
                className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground file:mr-3 file:rounded-lg file:border-0 file:bg-accent/10 file:px-3 file:py-1.5 file:text-xs file:font-medium file:text-accent"
              />
            </label>
            {downloadImportEntity === "Video" ? (
              <CheckboxLabel
                label="Auto-apply video metadata after download"
                description="After video downloads are queued, also queue metadata matching for the imported video URLs."
                checked={downloadImportAutoApplyMetadata}
                onChange={(checked) => {
                  setDownloadImportAutoApplyMetadata(checked);
                  setDownloadImportStatus(null);
                }}
              />
            ) : null}
            <CheckboxLabel
              label="Allow duplicate downloads for this batch"
              description="Permit URLs that Cove would otherwise skip as duplicates in the current batch."
              checked={downloadImportAllowDuplicateDownloads}
              onChange={(checked) => {
                setDownloadImportAllowDuplicateDownloads(checked);
                setDownloadImportStatus(null);
              }}
            />
            <div className="space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
              <div>
                <p className="text-xs font-medium text-foreground">Generate after download</p>
                <p className="text-[11px] text-muted">Queue a follow-up generate scan after the batch download finishes.</p>
              </div>
              <div className="grid gap-2 sm:grid-cols-2">
                <CheckboxLabel label="Covers" checked={!!downloadImportGenerateOpts.thumbnails} onChange={(checked) => { setDownloadImportGenerateOpts((current) => ({ ...current, thumbnails: checked })); setDownloadImportStatus(null); }} />
                <CheckboxLabel label="Previews" checked={!!downloadImportGenerateOpts.previews} onChange={(checked) => { setDownloadImportGenerateOpts((current) => ({ ...current, previews: checked })); setDownloadImportStatus(null); }} />
                <CheckboxLabel label="Sprites" checked={!!downloadImportGenerateOpts.sprites} onChange={(checked) => { setDownloadImportGenerateOpts((current) => ({ ...current, sprites: checked })); setDownloadImportStatus(null); }} />
                <CheckboxLabel label="Video perceptual hashes" checked={!!downloadImportGenerateOpts.phashes} onChange={(checked) => { setDownloadImportGenerateOpts((current) => ({ ...current, phashes: checked })); setDownloadImportStatus(null); }} />
                <CheckboxLabel label="MD5 checksums" checked={!!downloadImportGenerateOpts.md5} onChange={(checked) => { setDownloadImportGenerateOpts((current) => ({ ...current, md5: checked })); setDownloadImportStatus(null); }} />
                <CheckboxLabel label="Image thumbnails" checked={!!downloadImportGenerateOpts.imageThumbnails} onChange={(checked) => { setDownloadImportGenerateOpts((current) => ({ ...current, imageThumbnails: checked })); setDownloadImportStatus(null); }} />
                <CheckboxLabel label="Image perceptual hashes" checked={!!downloadImportGenerateOpts.imagePhashes} onChange={(checked) => { setDownloadImportGenerateOpts((current) => ({ ...current, imagePhashes: checked })); setDownloadImportStatus(null); }} />
                <CheckboxLabel label="Overwrite generated files" checked={!!downloadImportGenerateOpts.overwrite} onChange={(checked) => { setDownloadImportGenerateOpts((current) => ({ ...current, overwrite: checked })); setDownloadImportStatus(null); }} />
              </div>
            </div>
            <div className="rounded-xl border border-border/60 bg-surface/60 p-3 text-[11px] text-muted">
              <p>Use a plain text file with one URL per line.</p>
              <p className="mt-1">Cove now queues the batch job immediately and remembers the last imported URLs and options for the next run.</p>
              {downloadImportFile ? <p className="mt-1 text-secondary">Selected file: {downloadImportFile.name}</p> : null}
              {!downloadImportFile && downloadImportCachedFileName ? <p className="mt-1 text-secondary">Reusing cached URLs from: {downloadImportCachedFileName}</p> : null}
            </div>
          </div>
        </TaskCard>
        )}
      </div>
    </SectionCard>
    </>
  );
}

// ---- Data Management ----
function DataManagementSection({ refetchJobs, mode }: { refetchJobs: () => void; mode: "maintenance" | "backup" }) {
  const queryClient = useQueryClient();
  const [cleanDryRun, setCleanDryRun] = useState(false);
  const [showCleanGenOpts, setShowCleanGenOpts] = useState(false);
  const [cleanGenOpts, setCleanGenOpts] = useState<CleanGeneratedOptions>({
    screenshots: true,
    sprites: true,
    transcodes: true,
    markers: true,
    imageThumbnails: true,
    dryRun: false,
  });

  const [showExportOpts, setShowExportOpts] = useState(false);
  const [exportOpts, setExportOpts] = useState<ExportOptions>({
    includeVideos: true,
    includePerformers: true,
    includeStudios: true,
    includeTags: true,
    includeGalleries: true,
    includeGroups: true,
  });

  const cleanMut = useMutation({ mutationFn: () => metadata.clean({ dryRun: cleanDryRun }), onSuccess: () => refetchJobs() });
  const cleanGenMut = useMutation({ mutationFn: () => metadata.cleanGenerated(cleanGenOpts), onSuccess: () => refetchJobs() });
  const exportMut = useMutation({ mutationFn: () => metadata.export(exportOpts), onSuccess: () => refetchJobs() });
  const [importFilePath, setImportFilePath] = useState("");
  const [importOverwrite, setImportOverwrite] = useState(false);
  const [showImportOpts, setShowImportOpts] = useState(false);
  const [restoreBackupPath, setRestoreBackupPath] = useState("");
  const [restoreConfirmed, setRestoreConfirmed] = useState(false);
  const importMut = useMutation({
    mutationFn: () => metadata.import({ filePath: importFilePath, duplicateHandling: importOverwrite }),
    onSuccess: () => refetchJobs(),
  });
  const latestBackupQuery = useQuery({
    queryKey: ["settings", "latest-backup"],
    queryFn: () => database.latestBackup(),
    retry: false,
  });
  const backupMut = useMutation({
    mutationFn: () => database.backup(),
    onSuccess: async (result) => {
      setRestoreBackupPath(result.backupPath);
      await queryClient.invalidateQueries({ queryKey: ["settings", "latest-backup"] });
      refetchJobs();
    },
  });
  const restoreMut = useMutation({
    mutationFn: async () => {
      const backupPath = restoreBackupPath.trim();
      if (!backupPath) {
        throw new Error("Backup path is required.");
      }
      if (!restoreConfirmed) {
        throw new Error("Confirm that restoring will replace the current database first.");
      }
      return database.restore(backupPath);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries();
      window.location.reload();
    },
  });
  const optimizeMut = useMutation({ mutationFn: () => database.optimize(), onSuccess: () => refetchJobs() });
  const [wipeConfirm1, setWipeConfirm1] = useState(false);
  const [wipeConfirm2, setWipeConfirm2] = useState(false);
  const [lastWipeConfigBackup, setLastWipeConfigBackup] = useState<string | null>(null);
  const wipeMut = useMutation({
    mutationFn: async () => {
      // Backend now wipes BOTH the database AND the on-disk config file (and snapshots
      // both first), so we no longer need to clear covePaths from here.
      return database.wipe();
    },
    onSuccess: (result) => {
      setLastWipeConfigBackup(result.configBackupPath);
      sessionStorage.removeItem("cove-setup-dismissed");
      window.location.reload();
    },
  });

  const [configRestorePath, setConfigRestorePath] = useState("");
  const [configRestoreConfirmed, setConfigRestoreConfirmed] = useState(false);
  const latestConfigBackupQuery = useQuery({
    queryKey: ["settings", "latest-config-backup"],
    queryFn: () => database.latestConfigBackup(),
    retry: false,
  });
  const configBackupMut = useMutation({
    mutationFn: () => database.backupConfig(),
    onSuccess: async (result) => {
      setConfigRestorePath(result.backupPath);
      await queryClient.invalidateQueries({ queryKey: ["settings", "latest-config-backup"] });
    },
  });
  const configRestoreMut = useMutation({
    mutationFn: async () => {
      const path = configRestorePath.trim();
      if (!path) throw new Error("Config backup path is required.");
      if (!configRestoreConfirmed) throw new Error("Confirm that restoring will replace the current config first.");
      return database.restoreConfig(path);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries();
      window.location.reload();
    },
  });

  const backupStatus = backupMut.isSuccess
    ? { type: "success" as const, text: `Backup saved to ${backupMut.data?.backupPath ?? "disk"}` }
    : backupMut.isError
    ? { type: "error" as const, text: `Backup failed: ${backupMut.error instanceof Error ? backupMut.error.message : "Unknown error"}` }
    : null;
  const optimizeStatus = optimizeMut.isSuccess
    ? { type: "success" as const, text: "Database optimized successfully" }
    : optimizeMut.isError
    ? { type: "error" as const, text: `Optimize failed: ${optimizeMut.error instanceof Error ? optimizeMut.error.message : "Unknown error"}` }
    : null;
  const restoreStatus = restoreMut.isSuccess
    ? {
        type: "success" as const,
        text: restoreMut.data?.preRestoreBackupPath
          ? `Restore completed from ${restoreBackupPath}. Pre-restore backup saved to ${restoreMut.data.preRestoreBackupPath}. Reloading...`
          : `Restore completed from ${restoreBackupPath}. Reloading...`,
      }
    : restoreMut.isError
    ? { type: "error" as const, text: `Restore failed: ${restoreMut.error instanceof Error ? restoreMut.error.message : "Unknown error"}` }
    : null;

  return (
    <SectionCard
      title={mode === "maintenance" ? "Maintenance" : "Backup & Restore"}
      description={mode === "maintenance" ? "Clean orphaned data, generated files, and optimize the database." : "Export/import metadata, manage database or config backups, and wipe after snapshots are created."}
    >
      <div className="space-y-4">
        {mode === "maintenance" && (
        <>
        {/* Clean */}
        <TaskCard
          label="Clean"
          description="Find and remove database entries for files that no longer exist on disk."
          onRun={() => cleanMut.mutate()}
          isPending={cleanMut.isPending}
        >
          <div className="pt-3 border-t border-border/50">
            <CheckboxLabel label="Dry run (report only, don't delete)" checked={cleanDryRun} onChange={setCleanDryRun} />
          </div>
        </TaskCard>

        {/* Clean Generated Files */}
        <TaskCard
          label="Clean Generated Files"
          description="Remove generated files (screenshots, sprites, transcodes, etc.) that are no longer needed."
          onRun={() => cleanGenMut.mutate()}
          isPending={cleanGenMut.isPending}
          expandable
          expanded={showCleanGenOpts}
          onToggleExpand={() => setShowCleanGenOpts(!showCleanGenOpts)}
        >
          <div className="grid gap-2 sm:grid-cols-2 pt-3 border-t border-border/50">
            <CheckboxLabel label="Screenshots" checked={!!cleanGenOpts.screenshots} onChange={(c) => setCleanGenOpts({ ...cleanGenOpts, screenshots: c })} />
            <CheckboxLabel label="Sprites" checked={!!cleanGenOpts.sprites} onChange={(c) => setCleanGenOpts({ ...cleanGenOpts, sprites: c })} />
            <CheckboxLabel label="Transcodes" checked={!!cleanGenOpts.transcodes} onChange={(c) => setCleanGenOpts({ ...cleanGenOpts, transcodes: c })} />
            <CheckboxLabel label="Segments" checked={!!cleanGenOpts.markers} onChange={(c) => setCleanGenOpts({ ...cleanGenOpts, markers: c })} />
            <CheckboxLabel label="Image thumbnails" checked={!!cleanGenOpts.imageThumbnails} onChange={(c) => setCleanGenOpts({ ...cleanGenOpts, imageThumbnails: c })} />
            <CheckboxLabel label="Dry run" checked={!!cleanGenOpts.dryRun} onChange={(c) => setCleanGenOpts({ ...cleanGenOpts, dryRun: c })} />
          </div>
        </TaskCard>
        </>
        )}

        {mode === "backup" && (
        <>
        {/* Export */}
        <TaskCard
          label="Full Export"
          description="Export database content to JSON metadata files."
          onRun={() => exportMut.mutate()}
          isPending={exportMut.isPending}
          expandable
          expanded={showExportOpts}
          onToggleExpand={() => setShowExportOpts(!showExportOpts)}
        >
          <div className="grid gap-2 sm:grid-cols-2 pt-3 border-t border-border/50">
            <CheckboxLabel label="Videos" checked={!!exportOpts.includeVideos} onChange={(c) => setExportOpts({ ...exportOpts, includeVideos: c })} />
            <CheckboxLabel label="Performers" checked={!!exportOpts.includePerformers} onChange={(c) => setExportOpts({ ...exportOpts, includePerformers: c })} />
            <CheckboxLabel label="Studios" checked={!!exportOpts.includeStudios} onChange={(c) => setExportOpts({ ...exportOpts, includeStudios: c })} />
            <CheckboxLabel label="Tags" checked={!!exportOpts.includeTags} onChange={(c) => setExportOpts({ ...exportOpts, includeTags: c })} />
            <CheckboxLabel label="Galleries" checked={!!exportOpts.includeGalleries} onChange={(c) => setExportOpts({ ...exportOpts, includeGalleries: c })} />
            <CheckboxLabel label="Groups" checked={!!exportOpts.includeGroups} onChange={(c) => setExportOpts({ ...exportOpts, includeGroups: c })} />
          </div>
        </TaskCard>

        {/* Import */}
        <TaskCard
          label="Import"
          description="Import metadata from a previously exported JSON file."
          onRun={() => importMut.mutate()}
          isPending={importMut.isPending}
          expandable
          expanded={showImportOpts}
          onToggleExpand={() => setShowImportOpts(!showImportOpts)}
        >
          <div className="space-y-3 pt-3 border-t border-border/50">
            <div>
              <label className="block text-xs text-secondary mb-1">Export file path</label>
              <input
                type="text"
                value={importFilePath}
                onChange={(e) => setImportFilePath(e.target.value)}
                placeholder="/path/to/cove-export.json"
                className="w-full rounded border border-border bg-surface px-3 py-1.5 text-sm text-foreground"
              />
            </div>
            <CheckboxLabel label="Overwrite existing entries" checked={importOverwrite} onChange={setImportOverwrite} />
          </div>
        </TaskCard>
        </>
        )}
        {/* Database Operations */}
        <div className="grid gap-3 sm:grid-cols-2">
          {mode === "backup" && (
          <>
          <TaskCard
            label="Backup Database"
            description="Create a pg_dump backup of the PostgreSQL database."
            onRun={() => backupMut.mutate()}
            isPending={backupMut.isPending}
            statusMessage={backupStatus}
          />
          <TaskCard
            label="Restore Backup"
            description="Restore the database from a backup file. This replaces the current database contents and reloads Cove."
            onRun={() => restoreMut.mutate()}
            isPending={restoreMut.isPending}
            statusMessage={restoreStatus}
          >
            <div className="space-y-3 pt-3 border-t border-border/50">
              <div>
                <div className="mb-1 flex items-center justify-between gap-3">
                  <label className="block text-xs text-secondary">Backup file path</label>
                  {latestBackupQuery.data && (
                    <button
                      type="button"
                      onClick={() => setRestoreBackupPath(latestBackupQuery.data ?? "")}
                      className="text-xs text-accent hover:text-accent-hover"
                    >
                      Use latest backup
                    </button>
                  )}
                </div>
                <input
                  type="text"
                  value={restoreBackupPath}
                  onChange={(e) => setRestoreBackupPath(e.target.value)}
                  placeholder="/path/to/cove_backup.sql"
                  className="w-full rounded border border-border bg-surface px-3 py-1.5 text-sm text-foreground"
                />
                {latestBackupQuery.data && (
                  <p className="mt-2 text-xs text-secondary">Latest backup: {latestBackupQuery.data}</p>
                )}
                {latestBackupQuery.isLoading && (
                  <p className="mt-2 text-xs text-secondary">Checking for the latest backup…</p>
                )}
              </div>
              <CheckboxLabel
                label="I understand this will replace the current database with the selected backup"
                checked={restoreConfirmed}
                onChange={setRestoreConfirmed}
              />
            </div>
          </TaskCard>
          </>
          )}
          {mode === "maintenance" && (
          <TaskCard
            label="Optimise Database"
            description="Run VACUUM ANALYSE to reclaim space and update query planner statistics."
            onRun={() => optimizeMut.mutate()}
            isPending={optimizeMut.isPending}
            statusMessage={optimizeStatus}
          />
          )}
        </div>

        {/* Config Backup / Restore */}
        {mode === "backup" && (
        <div className="grid gap-3 sm:grid-cols-2">
          <TaskCard
            label="Backup Config"
            description="Snapshot cove-config.json (library paths, downloader overrides, scraper preferences, UI settings, etc.) to the backups folder."
            onRun={() => configBackupMut.mutate()}
            isPending={configBackupMut.isPending}
            statusMessage={
              configBackupMut.isSuccess
                ? { type: "success" as const, text: `Config saved to ${configBackupMut.data?.backupPath ?? "disk"}` }
                : configBackupMut.isError
                ? { type: "error" as const, text: `Config backup failed: ${configBackupMut.error instanceof Error ? configBackupMut.error.message : "Unknown error"}` }
                : null
            }
          />
          <TaskCard
            label="Restore Config"
            description="Replace the current cove-config.json with a previously saved snapshot. Reloads Cove on success."
            onRun={() => configRestoreMut.mutate()}
            isPending={configRestoreMut.isPending}
            statusMessage={
              configRestoreMut.isSuccess
                ? { type: "success" as const, text: `Config restored from ${configRestorePath}. Reloading...` }
                : configRestoreMut.isError
                ? { type: "error" as const, text: `Config restore failed: ${configRestoreMut.error instanceof Error ? configRestoreMut.error.message : "Unknown error"}` }
                : null
            }
          >
            <div className="space-y-3 pt-3 border-t border-border/50">
              <div>
                <div className="mb-1 flex items-center justify-between gap-3">
                  <label className="block text-xs text-secondary">Config backup file path</label>
                  {latestConfigBackupQuery.data && (
                    <button
                      type="button"
                      onClick={() => setConfigRestorePath(latestConfigBackupQuery.data ?? "")}
                      className="text-xs text-accent hover:text-accent-hover"
                    >
                      Use latest backup
                    </button>
                  )}
                </div>
                <input
                  type="text"
                  value={configRestorePath}
                  onChange={(e) => setConfigRestorePath(e.target.value)}
                  placeholder="/path/to/cove_config_*.json"
                  className="w-full rounded border border-border bg-surface px-3 py-1.5 text-sm text-foreground"
                />
                {latestConfigBackupQuery.data && (
                  <p className="mt-2 text-xs text-secondary">Latest config backup: {latestConfigBackupQuery.data}</p>
                )}
              </div>
              <CheckboxLabel
                label="I understand this will replace the current config with the selected backup"
                checked={configRestoreConfirmed}
                onChange={setConfigRestoreConfirmed}
              />
            </div>
          </TaskCard>
        </div>
        )}

        {/* Wipe Database — danger zone */}
        {mode === "backup" && (
        <div className="border border-red-900/50 rounded-lg p-4 bg-red-950/20">
          <h4 className="text-sm font-semibold text-red-400 mb-1">Danger Zone</h4>
          <p className="text-xs text-secondary mb-3">
            Permanently deletes all videos, performers, tags, studios, galleries, and groups from the database <strong>and</strong> resets your saved configuration (cove-config.json) to factory defaults so the setup wizard reappears. A snapshot of both the database and the config is taken first and saved to the backups folder, so you can restore them later from this Backup & Restore page.
          </p>
          {lastWipeConfigBackup && (
            <p className="text-xs text-amber-300 mb-3">Last config snapshot from a wipe: {lastWipeConfigBackup}</p>
          )}
          {!wipeConfirm1 && (
            <button
              onClick={() => setWipeConfirm1(true)}
              className="px-3 py-1.5 text-sm bg-red-900/40 hover:bg-red-900/70 text-red-400 hover:text-red-300 rounded border border-red-800/50 transition-colors"
            >
              Wipe Database & Config…
            </button>
          )}
          {wipeConfirm1 && !wipeConfirm2 && (
            <div className="space-y-2">
              <p className="text-sm text-red-300 font-medium">Are you sure? This will delete ALL your data <strong>and</strong> reset your saved configuration. Snapshots of both will be saved to the backups folder.</p>
              <div className="flex gap-2">
                <button
                  onClick={() => setWipeConfirm2(true)}
                  className="px-3 py-1.5 text-sm bg-red-700 hover:bg-red-600 text-white rounded transition-colors"
                >
                  Yes, continue
                </button>
                <button
                  onClick={() => setWipeConfirm1(false)}
                  className="px-3 py-1.5 text-sm text-secondary hover:text-foreground rounded transition-colors"
                >
                  Cancel
                </button>
              </div>
            </div>
          )}
          {wipeConfirm2 && (
            <div className="space-y-2">
              <p className="text-sm text-red-300 font-medium">Final confirmation: type WIPE to confirm permanent deletion.</p>
              <WipeConfirmInput
                onConfirm={() => wipeMut.mutate()}
                onCancel={() => { setWipeConfirm1(false); setWipeConfirm2(false); }}
                isPending={wipeMut.isPending}
                error={wipeMut.isError ? (wipeMut.error instanceof Error ? wipeMut.error.message : "Wipe failed") : null}
              />
            </div>
          )}
        </div>
        )}
      </div>
    </SectionCard>
  );
}

// ---- Extension Tasks ----
function ExtensionTasksSection({ refetchJobs }: { refetchJobs: () => void }) {
  const { data: pluginList } = useQuery({ queryKey: ["plugins"], queryFn: pluginsApi.list });
  const runTaskMut = useMutation({
    mutationFn: pluginsApi.runTask,
    onSuccess: () => refetchJobs(),
  });

  const enabledWithTasks = pluginList?.filter((p) => p.enabled && p.tasks.length > 0) ?? [];

  if (enabledWithTasks.length === 0) {
    return (
      <SectionCard title="Extension Tasks" description="No installed extensions currently expose runnable tasks.">
        <button
          type="button"
          onClick={() => navigateToUrl("/settings/extensions/installed", { state: { page: "settings" } })}
          className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary hover:border-accent hover:text-foreground"
        >
          <Plug className="h-4 w-4" />
          Installed extensions
        </button>
      </SectionCard>
    );
  }

  return (
    <SectionCard title="Extension Tasks" description="Run tasks provided by enabled extensions.">
      <div className="space-y-4">
        {enabledWithTasks.map((ext) => (
          <div key={ext.id} className="rounded-xl border border-border bg-card overflow-hidden">
            <div className="px-4 py-2.5 border-b border-border bg-black/10 flex items-center gap-2">
              <Plug className="h-3.5 w-3.5 text-muted" />
              <span className="text-sm font-medium text-foreground">{ext.name}</span>
              <span className="text-xs text-muted">v{ext.version}</span>
            </div>
            <div className="divide-y divide-border/50">
              {ext.tasks.map((task) => (
                <div key={task.name} className="flex items-center justify-between px-4 py-3">
                  <div>
                    <h4 className="text-sm font-medium text-foreground">{task.description || task.name}</h4>
                  </div>
                  <button
                    onClick={() => runTaskMut.mutate({ pluginId: ext.id, taskName: task.name })}
                    disabled={runTaskMut.isPending}
                    className="inline-flex items-center gap-1.5 rounded-lg bg-accent px-3 py-1.5 text-xs font-medium text-white hover:bg-accent-hover disabled:opacity-60"
                  >
                    {runTaskMut.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <PlayCircle className="h-3.5 w-3.5" />}
                    Run
                  </button>
                </div>
              ))}
            </div>
          </div>
        ))}
      </div>
    </SectionCard>
  );
}

// ---- Wipe Confirm Input ----
function WipeConfirmInput({ onConfirm, onCancel, isPending, error }: { onConfirm: () => void; onCancel: () => void; isPending: boolean; error: string | null }) {
  const [value, setValue] = useState("");
  return (
    <div className="flex flex-col gap-2">
      <input
        type="text"
        value={value}
        onChange={(e) => setValue(e.target.value)}
        placeholder="Type WIPE"
        className="w-48 bg-card border border-red-800 rounded px-3 py-1.5 text-sm text-foreground focus:outline-none focus:border-red-500"
      />
      {error && <p className="text-xs text-red-400">{error}</p>}
      <div className="flex gap-2">
        <button
          onClick={onConfirm}
          disabled={value !== "WIPE" || isPending}
          className="px-3 py-1.5 text-sm bg-red-700 hover:bg-red-600 text-white rounded transition-colors disabled:opacity-50 disabled:cursor-not-allowed flex items-center gap-1.5"
        >
          {isPending && <span className="animate-spin inline-block w-3 h-3 border border-white border-t-transparent rounded-full" />}
          Permanently Wipe Database
        </button>
        <button
          onClick={onCancel}
          className="px-3 py-1.5 text-sm text-secondary hover:text-foreground rounded transition-colors"
        >
          Cancel
        </button>
      </div>
    </div>
  );
}

// ===== Color + Alpha helpers for custom theme colors =====
/** Parse a CSS color value into hex + alpha. Handles hex, rgba, and named colors. */
function parseColorAlpha(raw: string): { hex: string; alpha: number } {
  raw = raw.trim();
  // #rrggbbaa or #rgba
  if (raw.startsWith("#")) {
    if (raw.length === 9) {
      const alphaHex = raw.slice(7, 9);
      return { hex: raw.slice(0, 7), alpha: parseInt(alphaHex, 16) / 255 };
    }
    if (raw.length === 5) {
      const a = raw[4];
      return { hex: `#${raw[1]}${raw[1]}${raw[2]}${raw[2]}${raw[3]}${raw[3]}`, alpha: parseInt(a + a, 16) / 255 };
    }
    return { hex: raw.length >= 7 ? raw.slice(0, 7) : raw, alpha: 1 };
  }
  // rgba(r, g, b, a)
  const rgbaMatch = raw.match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+))?\s*\)/);
  if (rgbaMatch) {
    const r = Number(rgbaMatch[1]);
    const g = Number(rgbaMatch[2]);
    const b = Number(rgbaMatch[3]);
    const a = rgbaMatch[4] !== undefined ? Number(rgbaMatch[4]) : 1;
    const hex = `#${r.toString(16).padStart(2, "0")}${g.toString(16).padStart(2, "0")}${b.toString(16).padStart(2, "0")}`;
    return { hex, alpha: a };
  }
  return { hex: "#202b33", alpha: 1 };
}

/** Build a CSS color from hex + alpha. Returns rgba() if alpha < 1, otherwise hex. */
function buildColorWithAlpha(hex: string, alpha: number): string {
  if (alpha >= 1) return hex;
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha.toFixed(2)})`;
}

function getThemePreviewColor(cssVariables: Record<string, string> | undefined, key: string, fallback: string) {
  return cssVariables?.[`--${key}`] ?? cssVariables?.[`--color-${key}`] ?? fallback;
}

function ThemePalettePreview({ cssVariables }: { cssVariables?: Record<string, string> }) {
  const background = getThemePreviewColor(cssVariables, "background", "#0b1220");
  const card = getThemePreviewColor(cssVariables, "card", "#152033");
  const surface = getThemePreviewColor(cssVariables, "surface", card);
  const accent = getThemePreviewColor(cssVariables, "accent", "#2f80ed");
  const foreground = getThemePreviewColor(cssVariables, "foreground", "#f8fafc");
  const secondary = getThemePreviewColor(cssVariables, "secondary", foreground);

  return (
    <div className="mt-3 overflow-hidden rounded-xl border border-black/10" style={{ background }}>
      <div className="flex items-center justify-between gap-2 border-b border-black/10 px-3 py-2" style={{ background: surface }}>
        <div className="flex items-center gap-1.5">
          {[background, surface, card, accent, foreground].map((color, index) => (
            <span key={`${color}-${index}`} className="h-4 w-4 rounded-full border border-black/15" style={{ background: color }} />
          ))}
        </div>
        <div className="h-2 w-14 rounded-full" style={{ background: secondary, opacity: 0.55 }} />
      </div>
      <div className="grid grid-cols-[3.75rem_minmax(0,1fr)] gap-2 p-3">
        <div className="space-y-1.5 rounded-lg p-2" style={{ background: card }}>
          {[0.85, 0.6, 0.38].map((opacity, index) => (
            <div key={index} className="h-1.5 rounded-full" style={{ background: foreground, opacity }} />
          ))}
        </div>
        <div className="space-y-2">
          <div className="h-9 rounded-lg" style={{ background: accent }} />
          <div className="grid grid-cols-3 gap-1.5">
            {[surface, card, accent].map((color, index) => (
              <div key={`${color}-${index}`} className="h-6 rounded-md" style={{ background: color, opacity: index === 2 ? 0.75 : 1 }} />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

function ThemeSelector() {
  const { user } = useAuth();
  const {
    availableThemes, activeThemeId, setActiveTheme,
    availableComponentStyles, activeComponentStyles, toggleComponentStyle,
    availableLayoutStyles, activeLayoutStyles, activeLayoutStyle, toggleLayoutStyle,
    customThemeColors, setCustomThemeColors,
  } = useExtensions();

  const SECTIONS_STORAGE_KEY = "cove-theme-sections";
  const [expandedSections, setExpandedSections] = useState<Set<string>>(() => {
    try {
      const stored = JSON.parse(localStorage.getItem(SECTIONS_STORAGE_KEY) ?? "null");
      return stored ? new Set(stored) : new Set(["palette"]);
    } catch { return new Set(["palette"]); }
  });
  const toggleSection = (key: string) => {
    setExpandedSections((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      localStorage.setItem(SECTIONS_STORAGE_KEY, JSON.stringify([...next]));
      return next;
    });
  };

  const readPersistedStyleOptions = () => {
    try {
      const source = supportsServerBackedUiPreferences(user)
        ? (readAuthenticatedUserThemePreferences()?.styleOptions ?? {})
        : JSON.parse(localStorage.getItem("cove-style-options") ?? "{}");
      const raw = JSON.parse(JSON.stringify(source)) as Record<string, Record<string, string>>;

      if (raw.gradient) {
        const g = raw.gradient;
        if (g.animated === "on" && g.speed) { g.animated = g.speed; }
        else if (g.animated === "on") { g.animated = "medium"; }
        delete g.speed;
        if (g.cards === "on" && g.cardstrength) { g.cards = g.cardstrength; }
        else if (g.cards === "on") { g.cards = "medium"; }
        delete g.cardstrength;
        if (g.bgstrength && !g.background) { g.background = g.bgstrength; }
        delete g.bgstrength;
        raw.gradient = g;
      }

      const discreteToNumeric: Record<string, Record<string, Record<string, string>>> = {
        gradient: {
          animated: { off: "0", slow: "25", medium: "55", fast: "85" },
          background: { off: "0", subtle: "25", medium: "45", strong: "75" },
          cards: { off: "0", subtle: "25", medium: "50", strong: "75" },
        },
        glass: {
          cardblur: { off: "0", light: "27", full: "83" },
          surfaceblur: { low: "25", medium: "50", high: "75" },
          opacity: { light: "25", medium: "40", heavy: "65" },
          cardopacity: { light: "25", medium: "40", heavy: "65" },
          buttonopacity: { light: "35", medium: "55", heavy: "75" },
        },
        animated: {
          hover: { off: "0", subtle: "33", on: "67" },
        },
        theme: {
          bgspeed: { off: "0", slow: "25", medium: "55", fast: "85" },
        },
      };

      let migrated = false;
      for (const [styleId, opts] of Object.entries(raw)) {
        for (const [key, val] of Object.entries(opts)) {
          const migratedValue = discreteToNumeric[styleId]?.[key]?.[val];
          if (migratedValue) {
            raw[styleId][key] = migratedValue;
            migrated = true;
          }
        }
      }

      if (migrated) {
        localStorage.setItem("cove-style-options", JSON.stringify(raw));
      }

      return raw;
    } catch {
      return {};
    }
  };

  // Style option configs stored in localStorage
  const [styleOptions, setStyleOptionsState] = useState<Record<string, Record<string, string>>>(() => {
    return readPersistedStyleOptions();
  });
  const setStyleOption = (styleId: string, optionKey: string, value: string) => {
    const updated = { ...styleOptions, [styleId]: { ...styleOptions[styleId], [optionKey]: value } };
    setStyleOptionsState(updated);
    localStorage.setItem("cove-style-options", JSON.stringify(updated));
    updateAuthenticatedUserUiPreferences((current) => ({
      ...(current ?? {}),
      theme: {
        ...(current?.theme ?? {}),
        styleOptions: updated,
      },
    }));
    // Apply to document as data attribute for CSS targeting
    document.documentElement.dataset[`style${styleId.charAt(0).toUpperCase()}${styleId.slice(1)}${optionKey.charAt(0).toUpperCase()}${optionKey.slice(1)}`] = value;
    // Set CSS custom property for range-type configs
    const cfg = styleConfigs[styleId]?.find(c => c.key === optionKey);
    if (cfg && "cssVar" in cfg) {
      document.documentElement.style.setProperty(cfg.cssVar, value);
    }
  };

  useEffect(() => {
    setStyleOptionsState(readPersistedStyleOptions());
  }, [user]);

  // Apply style options on mount (and clean up old migrated attributes)
  useEffect(() => {
    // Remove old attribute names from pre-migration settings
    delete document.documentElement.dataset.styleGradientSpeed;
    delete document.documentElement.dataset.styleGradientCardstrength;
    delete document.documentElement.dataset.styleGradientBgstrength;
    for (const [styleId, opts] of Object.entries(styleOptions)) {
      for (const [key, val] of Object.entries(opts)) {
        document.documentElement.dataset[`style${styleId.charAt(0).toUpperCase()}${styleId.slice(1)}${key.charAt(0).toUpperCase()}${key.slice(1)}`] = val;
        // Set CSS custom property for range-type configs
        const cfg = styleConfigs[styleId]?.find(c => c.key === key);
        if (cfg && "cssVar" in cfg) {
          document.documentElement.style.setProperty(cfg.cssVar, val);
        }
      }
    }
  }, [styleOptions]);

  // Style-specific configuration definitions
  // "range" type: continuous slider with CSS custom property. "select" (no type): dropdown.
  type RangeConfig = { key: string; label: string; type: "range"; cssVar: string; min: number; max: number; defaultValue: number };
  type SelectConfig = { key: string; label: string; options: { value: string; label: string }[] };
  type StyleConfig = RangeConfig | SelectConfig;
  const styleConfigs: Record<string, StyleConfig[]> = {
    gradient: [
      { key: "animated", label: "Animation Speed", type: "range", cssVar: "--sv-anim-speed", min: 0, max: 100, defaultValue: 55 },
      { key: "background", label: "Background Intensity", type: "range", cssVar: "--sv-bg-intensity", min: 0, max: 100, defaultValue: 45 },
      { key: "cards", label: "Card Gradient", type: "range", cssVar: "--sv-card-gradient", min: 0, max: 100, defaultValue: 50 },
      { key: "carddir", label: "Card Direction", options: [{ value: "diagonal", label: "Diagonal" }, { value: "vertical", label: "Vertical" }, { value: "horizontal", label: "Horizontal" }] },
      { key: "bgdir", label: "Background Direction", options: [{ value: "diagonal", label: "Diagonal" }, { value: "vertical", label: "Vertical" }, { value: "horizontal", label: "Horizontal" }] },
      { key: "surfacedir", label: "Surface Direction", options: [{ value: "diagonal", label: "Diagonal" }, { value: "vertical", label: "Vertical" }, { value: "horizontal", label: "Horizontal" }] },
      { key: "videopause", label: "Pause on Video Player", options: [{ value: "on", label: "On (recommended)" }, { value: "off", label: "Off" }] },
    ],
    glass: [
      { key: "cardblur", label: "Card Blur", type: "range", cssVar: "--sv-card-blur", min: 0, max: 100, defaultValue: 27 },
      { key: "surfaceblur", label: "Surface Blur", type: "range", cssVar: "--sv-surface-blur", min: 0, max: 100, defaultValue: 50 },
      { key: "opacity", label: "Surface Opacity", type: "range", cssVar: "--sv-surface-opacity", min: 0, max: 100, defaultValue: 40 },
      { key: "cardopacity", label: "Card Opacity", type: "range", cssVar: "--sv-card-opacity", min: 0, max: 100, defaultValue: 40 },
      { key: "buttonopacity", label: "Button Opacity", type: "range", cssVar: "--sv-button-opacity", min: 0, max: 100, defaultValue: 55 },
    ],
    animated: [
      { key: "hover", label: "Card Hover Glow", type: "range", cssVar: "--sv-hover-glow", min: 0, max: 100, defaultValue: 67 },
      { key: "shimmer", label: "Navbar Shimmer", options: [{ value: "on", label: "On" }, { value: "off", label: "Off" }] },
      { key: "entrance", label: "Card Entrance", options: [{ value: "on", label: "On" }, { value: "off", label: "Off" }] },
      { key: "surfaceshimmer", label: "Surface Shimmer", options: [{ value: "on", label: "On" }, { value: "off", label: "Off" }] },
      { key: "buttonglow", label: "Button Glow", options: [{ value: "on", label: "On" }, { value: "off", label: "Off" }] },
    ],
    theme: [
      { key: "bgspeed", label: "Background Animation Speed", type: "range", cssVar: "--sv-bg-anim-speed", min: 0, max: 100, defaultValue: 55 },
    ],
  };

  // Track which cards have their config expanded
  const CONFIGS_STORAGE_KEY = "cove-theme-configs";
  const [expandedConfigs, setExpandedConfigs] = useState<Set<string>>(() => {
    try {
      const stored = JSON.parse(localStorage.getItem(CONFIGS_STORAGE_KEY) ?? "null");
      return stored ? new Set(stored) : new Set();
    } catch { return new Set(); }
  });
  const toggleConfig = (key: string) => {
    setExpandedConfigs((prev) => {
      const n = new Set(prev);
      n.has(key) ? n.delete(key) : n.add(key);
      localStorage.setItem(CONFIGS_STORAGE_KEY, JSON.stringify([...n]));
      return n;
    });
  };

  const colorVarNames = [
    { key: "--color-background", label: "Background" },
    { key: "--color-nav", label: "Navigation" },
    { key: "--color-card", label: "Card" },
    { key: "--color-card-hover", label: "Card Hover" },
    { key: "--color-surface", label: "Surface" },
    { key: "--color-border", label: "Border" },
    { key: "--color-accent", label: "Accent" },
    { key: "--color-accent-hover", label: "Accent Hover" },
    { key: "--color-foreground", label: "Text" },
    { key: "--color-secondary", label: "Text Secondary" },
    { key: "--color-muted", label: "Text Muted" },
    { key: "--color-nav-active", label: "Nav Active" },
  ];

  return (
    <SectionCard title="Theme" description="Customize colors, styles, layout, and effects.">
      <div className="space-y-3">
        {/* --- Color Palette --- */}
        <CollapsibleSection title="Color Palette" subtitle={activeThemeId ? availableThemes.find((t) => t.id === activeThemeId)?.name ?? activeThemeId : "Default"} expanded={expandedSections.has("palette")} onToggle={() => toggleSection("palette")}>
          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            {/* Extension themes */}
            {availableThemes.map((theme) => (
              <button
                key={theme.id}
                onClick={() => setActiveTheme(theme.id)}
                className={`theme-option-card rounded-xl border p-4 text-left transition-colors ${
                  activeThemeId === theme.id
                    ? "border-accent bg-accent/10"
                    : "border-border bg-card hover:border-accent/50"
                }`}
              >
                <div className="text-sm font-medium text-foreground">{theme.name}</div>
                {theme.description && (
                  <div className="text-xs text-secondary mt-1">{theme.description}</div>
                )}
                <ThemePalettePreview cssVariables={theme.cssVariables} />
              </button>
            ))}

            {/* Custom theme */}
            <div
              className={`theme-option-card rounded-xl border transition-colors ${
                activeThemeId === "custom"
                  ? "border-accent bg-accent/10"
                  : "border-border bg-card hover:border-accent/50"
              }`}
            >
              <div className="flex items-center">
                <button
                  onClick={() => setActiveTheme("custom")}
                  className="flex-1 p-4 text-left"
                >
                  <div className="text-sm font-medium text-foreground">Custom</div>
                  <div className="text-xs text-secondary mt-1">Pick your own colors</div>
                  <ThemePalettePreview cssVariables={customThemeColors} />
                </button>
                {activeThemeId === "custom" && (
                  <button
                    onClick={(e) => { e.stopPropagation(); toggleConfig("custom"); }}
                    className="p-2 mr-2 rounded-lg hover:bg-card-hover text-muted"
                    title="Configure colors"
                  >
                    {expandedConfigs.has("custom") ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                  </button>
                )}
              </div>
              {activeThemeId === "custom" && expandedConfigs.has("custom") && (
                <div className="px-4 pb-3 pt-2 border-t border-border/50 space-y-2">
                  <div className="grid gap-3 md:grid-cols-2">
                    {colorVarNames.map(({ key, label }) => {
                      const rawValue = customThemeColors[key] || getComputedStyle(document.documentElement).getPropertyValue(key).trim() || "#202b33";
                      const { hex, alpha } = parseColorAlpha(rawValue);
                      return (
                        <div key={key} className="flex items-center gap-2">
                          <input
                            type="color"
                            value={hex}
                            onChange={(e) => setCustomThemeColors({ ...customThemeColors, [key]: buildColorWithAlpha(e.target.value, alpha) })}
                            className="w-7 h-7 rounded cursor-pointer border border-border bg-transparent p-0 shrink-0"
                          />
                          <div className="flex-1 min-w-0">
                            <span className="text-[11px] text-secondary block">{label}</span>
                            <div className="flex items-center gap-1 mt-0.5">
                              <input
                                type="range"
                                min="0"
                                max="100"
                                value={Math.round(alpha * 100)}
                                onChange={(e) => setCustomThemeColors({ ...customThemeColors, [key]: buildColorWithAlpha(hex, Number(e.target.value) / 100) })}
                                className="w-full h-1 rounded-full appearance-none bg-border cursor-pointer accent-accent"
                                title={`Opacity: ${Math.round(alpha * 100)}%`}
                              />
                              <span className="text-[10px] text-muted w-7 text-right shrink-0">{Math.round(alpha * 100)}%</span>
                            </div>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </div>
              )}
            </div>

            {availableThemes.length === 0 && (
              <p className="text-sm text-muted col-span-full">
                No additional themes available. Install theme extensions to add more options.
              </p>
            )}
          </div>
        </CollapsibleSection>

        {/* --- Style --- */}
        {availableComponentStyles.length > 0 && (
          <CollapsibleSection title="Style" subtitle={[...activeComponentStyles].join(", ") || "None"} expanded={expandedSections.has("component-style")} onToggle={() => toggleSection("component-style")}>
            <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
              {availableComponentStyles.map((style) => {
                const isActive = activeComponentStyles.has(style.id);
                const configs = styleConfigs[style.id];
                const hasConfigs = isActive && configs && configs.length > 0;
                const configExpanded = expandedConfigs.has(`style-${style.id}`);
                return (
                  <div
                    key={style.id}
                    className={`settings-style-card rounded-xl border transition-colors ${
                      isActive
                        ? "active border-accent bg-accent/10"
                        : "border-border bg-card hover:border-accent/50"
                    }`}
                  >
                    <div className="flex items-center">
                      <button
                        onClick={() => toggleComponentStyle(style.id)}
                        className="flex-1 p-4 text-left"
                      >
                        <div className="flex items-center gap-2">
                          <div className={`w-4 h-4 rounded border-2 flex items-center justify-center shrink-0 ${isActive ? "border-accent bg-accent" : "border-border"}`}>
                            {isActive && <svg className="w-3 h-3 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={3} d="M5 13l4 4L19 7" /></svg>}
                          </div>
                          <div className="text-sm font-medium text-foreground">{style.name}</div>
                        </div>
                        {style.description && (
                          <div className="text-xs text-secondary mt-1 ml-6">{style.description}</div>
                        )}
                      </button>
                      {hasConfigs && (
                        <button
                          onClick={(e) => { e.stopPropagation(); toggleConfig(`style-${style.id}`); }}
                          className="p-2 mr-2 rounded-lg hover:bg-card-hover text-muted"
                          title="Configure"
                        >
                          {configExpanded ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                        </button>
                      )}
                    </div>
                    {hasConfigs && configExpanded && (
                      <div className="px-4 pb-3 pt-2 border-t border-border/50 space-y-3">
                        {configs.map((cfg) => {
                          if ("cssVar" in cfg) {
                            // Continuous range slider
                            const raw = styleOptions[style.id]?.[cfg.key];
                            const numValue = raw != null && raw !== "" ? Number(raw) : cfg.defaultValue;
                            return (
                              <div key={cfg.key}>
                                <div className="flex items-center justify-between mb-1">
                                  <label className="text-xs text-secondary">{cfg.label}</label>
                                  <span className="text-xs text-foreground font-medium tabular-nums">{numValue === 0 ? "Off" : `${numValue}%`}</span>
                                </div>
                                <input
                                  type="range"
                                  min={cfg.min}
                                  max={cfg.max}
                                  step={1}
                                  value={numValue}
                                  onChange={(e) => setStyleOption(style.id, cfg.key, e.target.value)}
                                  onClick={(e) => e.stopPropagation()}
                                  style={{ "--range-fill": `${((numValue - cfg.min) / Math.max(1, cfg.max - cfg.min)) * 100}%` } as CSSProperties}
                                  className="themed-range-input settings-range-input w-full cursor-pointer"
                                />
                                <div className="flex justify-between mt-0.5">
                                  <span className="text-[9px] text-muted">Off</span>
                                  <span className="text-[9px] text-muted">Max</span>
                                </div>
                              </div>
                            );
                          }

                          const currentValue = styleOptions[style.id]?.[cfg.key] ?? cfg.options[0].value;
                          return (
                            <div key={cfg.key} className="flex items-center gap-2">
                              <label className="text-xs text-secondary shrink-0">{cfg.label}</label>
                              <select
                                value={currentValue}
                                onChange={(e) => setStyleOption(style.id, cfg.key, e.target.value)}
                                className="text-xs rounded border border-border bg-input px-2 py-1 text-foreground focus:border-accent focus:outline-none cursor-pointer"
                                onClick={(e) => e.stopPropagation()}
                              >
                                {cfg.options.map((o) => (
                                  <option key={o.value} value={o.value}>{o.label}</option>
                                ))}
                              </select>
                            </div>
                          );
                        })}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </CollapsibleSection>
        )}

        {/* --- Layout --- */}
        {availableLayoutStyles.length > 0 && (
          <CollapsibleSection title="Layout" subtitle={activeLayoutStyle || "Default"} expanded={expandedSections.has("layout")} onToggle={() => toggleSection("layout")}>
            <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
              {availableLayoutStyles.map((layout) => {
                const isActive = activeLayoutStyles.has(layout.id);
                return (
                  <button
                    key={layout.id}
                    onClick={() => toggleLayoutStyle(layout.id)}
                    className={`layout-option-card rounded-xl border p-4 text-left transition-colors ${
                      isActive
                        ? "border-accent bg-accent/10"
                        : "border-border bg-card hover:border-accent/50"
                    }`}
                  >
                    <div className="flex items-center gap-2">
                      <div className={`w-4 h-4 rounded border-2 flex items-center justify-center shrink-0 ${isActive ? "border-accent bg-accent" : "border-border"}`}>
                        {isActive && <svg className="w-3 h-3 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={3} d="M5 13l4 4L19 7" /></svg>}
                      </div>
                      <div className="text-sm font-medium text-foreground">{layout.name}</div>
                    </div>
                    {layout.description && (
                      <div className="text-xs text-secondary mt-1 ml-6">{layout.description}</div>
                    )}
                  </button>
                );
              })}
            </div>
          </CollapsibleSection>
        )}
      </div>
    </SectionCard>
  );
}

function NavReorderList({
  allItems,
  enabledItems,
  onChange,
}: {
  allItems: { value: string; label: string }[];
  enabledItems: string[];
  onChange: (items: string[]) => void;
}) {
  // Build ordered list: enabled items first (in their order), then unchecked items
  const enabledSet = new Set(enabledItems);
  const ordered = [
    ...enabledItems.map((v) => allItems.find((i) => i.value === v)).filter(Boolean) as typeof allItems,
    ...allItems.filter((i) => !enabledSet.has(i.value)),
  ];

  const handleToggle = (value: string, checked: boolean) => {
    if (checked) {
      onChange([...enabledItems, value]);
    } else {
      onChange(enabledItems.filter((v) => v !== value));
    }
  };

  return (
    <SortableList
      items={ordered}
      getKey={(item) => item.value}
      onReorder={(nextItems) => {
        onChange(nextItems.filter((item) => enabledSet.has(item.value)).map((item) => item.value));
      }}
      className="space-y-1"
      renderItem={(item, { dragHandleProps, isDragging, isOver }) => {
        const isEnabled = enabledSet.has(item.value);
        return (
          <div
            className={`flex items-center gap-3 rounded-lg border px-3 py-2 transition-colors cursor-grab active:cursor-grabbing select-none ${
              isDragging ? "opacity-40 border-accent" : isOver ? "border-accent bg-accent/5" : "border-border bg-card"
            }`}
          >
            <span {...dragHandleProps} className="inline-flex shrink-0 items-center text-muted">
              <GripVertical className="h-4 w-4" />
            </span>
            <input
              type="checkbox"
              checked={isEnabled}
              onChange={(e) => handleToggle(item.value, e.target.checked)}
              className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
            />
            <span className={`text-sm ${isEnabled ? "text-foreground" : "text-muted"}`}>{item.label}</span>
          </div>
        );
      }}
    />
  );
}

function ScraperTable({ entityType, scrapers }: { entityType: string; scrapers: ScraperSummary[] }) {
  return (
    <div className="overflow-hidden rounded-xl border border-border bg-card">
      <div className="border-b border-border px-4 py-3 text-sm font-semibold capitalize text-foreground">
        {entityType} scrapers <span className="text-muted">({scrapers.length})</span>
      </div>
      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-border text-sm">
          <thead className="bg-black/10 text-left text-xs uppercase tracking-wide text-muted">
            <tr>
              <th className="px-4 py-3">Name</th>
              <th className="px-4 py-3">Supported types</th>
              <th className="px-4 py-3">Supported URLs</th>
              <th className="px-4 py-3">Source</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-border/70">
            {scrapers.map((scraper) => (
              <tr key={scraper.id}>
                <td className="px-4 py-3 font-medium text-foreground">{scraper.name}</td>
                <td className="px-4 py-3 text-secondary">{scraper.supportedScrapes.join(", ")}</td>
                <td className="px-4 py-3 text-secondary">
                  {scraper.urls.length > 0 ? scraper.urls.join(", ") : <span className="text-muted">No URL matchers</span>}
                </td>
                <td className="px-4 py-3 text-xs text-muted">{scraper.sourcePath}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ===== Extension Settings Form (legacy Python extensions with config) =====
function ExtensionSettingsForm({ extensionId, schema }: { extensionId: string; schema: import("../api/types").PluginSettingSchema[] }) {
  const queryClient = useQueryClient();
  const { data: configValues, isLoading } = useQuery({
    queryKey: ["ext-config", extensionId],
    queryFn: () => pluginsApi.getConfig(extensionId),
  });
  const [localValues, setLocalValues] = useState<Record<string, unknown>>({});
  const [initialized, setInitialized] = useState(false);

  if (configValues && !initialized) {
    setLocalValues(configValues);
    setInitialized(true);
  }

  const saveMut = useMutation({
    mutationFn: (values: Record<string, unknown>) => pluginsApi.setConfig(extensionId, values),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["ext-config", extensionId] }),
  });

  const updateValue = (name: string, value: unknown) => {
    setLocalValues((prev) => ({ ...prev, [name]: value }));
  };

  const isDirty = JSON.stringify(localValues) !== JSON.stringify(configValues ?? {});

  if (isLoading) return <Loader2 className="w-4 h-4 animate-spin text-secondary" />;

  return (
    <div>
      <div className="text-xs font-medium text-secondary mb-2">Settings</div>
      <div className="space-y-2">
        {schema.map((s) => (
          <div key={s.name} className="flex items-center gap-3 bg-surface/50 rounded px-3 py-2">
            <label className="text-sm min-w-[140px] shrink-0">
              {s.displayName || s.name}
              {s.description && <div className="text-xs text-muted mt-0.5">{s.description}</div>}
            </label>
            {s.type === "BOOLEAN" ? (
              <button
                onClick={() => updateValue(s.name, !localValues[s.name])}
                className={`px-3 py-1 text-xs rounded font-medium transition-colors ${
                  localValues[s.name]
                    ? "bg-green-600/20 text-green-400 hover:bg-green-600/30"
                    : "bg-card/30 text-secondary hover:bg-card-hover/40"
                }`}
              >
                {localValues[s.name] ? "On" : "Off"}
              </button>
            ) : s.type === "NUMBER" ? (
              <input
                type="number"
                value={(localValues[s.name] as number) ?? ""}
                onChange={(e) => updateValue(s.name, e.target.value ? Number(e.target.value) : null)}
                className="themed-number-input settings-number-input flex-1 bg-card border border-border rounded px-2 py-1 text-sm focus:border-accent outline-none"
              />
            ) : (
              <input
                type="text"
                value={(localValues[s.name] as string) ?? ""}
                onChange={(e) => updateValue(s.name, e.target.value || null)}
                className="flex-1 bg-card border border-border rounded px-2 py-1 text-sm focus:border-accent outline-none"
              />
            )}
          </div>
        ))}
      </div>
      {isDirty && (
        <div className="flex justify-end mt-2 gap-2">
          <button
            onClick={() => { setLocalValues(configValues ?? {}); }}
            className="px-3 py-1 text-xs bg-card hover:bg-card-hover rounded transition-colors"
          >
            Reset
          </button>
          <button
            onClick={() => saveMut.mutate(localValues)}
            disabled={saveMut.isPending}
            className="px-3 py-1 text-xs bg-accent hover:bg-accent-hover rounded transition-colors disabled:opacity-50"
          >
            {saveMut.isPending ? "Saving..." : "Save Settings"}
          </button>
        </div>
      )}
    </div>
  );
}

// ===== Extensions Panel — unified view of all extensions =====
function ExtensionsPanel({ mode }: { mode: "installed" | "registry" }) {
  const { availableThemes, activeThemeId, setActiveTheme, getSettingsPanelsForTab, resolveComponent, manifest, refreshManifest } = useExtensions();
  const settingsPanels = getSettingsPanelsForTab("extensions");
  const queryClient = useQueryClient();
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [categoryFilter, setCategoryFilter] = useState<string>("all");
  const [searchQuery, setSearchQuery] = useState("");
  const [extensionToUninstall, setExtensionToUninstall] = useState<PendingExtensionUninstall | null>(null);
  const [pendingDependencyInstall, setPendingDependencyInstall] = useState<PendingExtensionInstall | null>(null);
  // Just-installed extension that ships a setup guide, shown as a post-install CTA (we don't auto-open it).
  const [justInstalledSetup, setJustInstalledSetup] = useState<{ name: string; topicId: string } | null>(null);

  // Map an extension id to its setup-guide topic id (topics flagged kind === "setup" in the manifest).
  const setupTopicByExtension = useMemo(() => {
    const map = new Map<string, string>();
    for (const topic of manifest?.tutorialTopics ?? []) {
      if (topic.kind === "setup" && topic.extensionId && !map.has(topic.extensionId)) {
        map.set(topic.extensionId, topic.id);
      }
    }
    return map;
  }, [manifest]);

  // .NET extensions from the extension manager
  const { data: extList } = useQuery({
    queryKey: ["extensions-list"],
    queryFn: () => import("../api/client").then(m => m.extensions.list()),
  });

  // Legacy Python extensions (from /api/plugins)
  const { data: legacyList } = useQuery({
    queryKey: ["plugins"],
    queryFn: pluginsApi.list,
  });

  const settingsMut = useMutation({
    mutationFn: pluginsApi.saveSettings,
    onMutate: async (vars: { enabledMap: Record<string, boolean> }) => {
      await queryClient.cancelQueries({ queryKey: ["plugins"] });
      const prev = queryClient.getQueryData<typeof legacyList>(["plugins"]);
      if (prev) {
        queryClient.setQueryData(["plugins"], prev.map((p) => {
          const override = vars.enabledMap[p.id];
          return override !== undefined ? { ...p, enabled: override } : p;
        }));
      }
      return { prev };
    },
    onError: (_err, _vars, ctx) => {
      if (ctx?.prev) queryClient.setQueryData(["plugins"], ctx.prev);
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: ["plugins"] }),
  });

  const enableMut = useMutation({
    mutationFn: (args: { id: string; enable: boolean }) =>
      import("../api/client").then(m => args.enable ? m.extensions.enable(args.id) : m.extensions.disable(args.id)),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["extensions-list"] }),
  });

  const { data: registryUpdates } = useQuery({
    queryKey: ["registry-updates"],
    queryFn: () => import("../api/client").then(m => m.extensions.registryCheckUpdates()),
  });

  const upgradeMut = useMutation({
    mutationFn: (args: { id: string; version: string; name?: string; installDependencies?: boolean }) =>
      import("../api/client").then(m => m.extensions.registryInstall(args.id, args.version, args.installDependencies ?? false)),
    onSuccess: (data, variables) => {
      if (data.requiresDependencies && data.missingDependencies?.length) {
        setPendingDependencyInstall({
          extensionId: variables.id,
          version: variables.version,
          name: data.extension?.name ?? variables.name,
          dependencies: data.missingDependencies,
        });
        return;
      }

      setPendingDependencyInstall(null);
      queryClient.invalidateQueries({ queryKey: ["extensions-list"] });
      queryClient.invalidateQueries({ queryKey: ["registry-search"] });
      queryClient.invalidateQueries({ queryKey: ["registry-updates"] });

      // Pull the freshened manifest so a newly installed extension's setup guide becomes
      // available, then offer it as a CTA rather than opening the manual automatically.
      void refreshManifest().then((fresh) => {
        const setupTopic = fresh?.tutorialTopics?.find(
          (topic) => topic.kind === "setup" && topic.extensionId === variables.id,
        );
        if (setupTopic) {
          setJustInstalledSetup({ name: data.extension?.name ?? variables.name ?? variables.id, topicId: setupTopic.id });
        }
      });
    },
  });

  const runJobMut = useMutation({
    mutationFn: (args: { id: string; jobId: string }) =>
      import("../api/client").then(m => m.extensions.runJob(args.id, args.jobId)),
  });

  const uninstallMut = useMutation<unknown, Error, PendingExtensionUninstall>({
    mutationFn: (ext) =>
      import("../api/client").then(m => m.extensions.registryUninstall(ext.id, ext.confirmedDependents || ext.dependents.length > 0)),
    onSuccess: (data, variables) => {
      const result = data as { requiresDependents?: boolean; dependents?: ExtensionDependencyImpact[] } | undefined;
      if (variables.source === "native" && result?.requiresDependents && Array.isArray(result.dependents)) {
        setExtensionToUninstall({ ...variables, dependents: result.dependents });
        return;
      }

      setExtensionToUninstall(null);
      queryClient.invalidateQueries({ queryKey: ["extensions-list"] });
      queryClient.invalidateQueries({ queryKey: ["plugins"] });
      queryClient.invalidateQueries({ queryKey: ["registry-search"] });
      queryClient.invalidateQueries({ queryKey: ["registry-updates"] });
    },
  });

  // Merge all extensions into a unified list
  type UnifiedExtension = {
    id: string;
    name: string;
    version: string;
    description?: string;
    author?: string;
    url?: string;
    enabled: boolean;
    kind: string;
    categories: string[];
    dependencies: Record<string, string>;
    source: "native" | "legacy";
    installSource?: string;
    hasUI: boolean;
    hasApi: boolean;
    hasJobs: boolean;
    hasState: boolean;
    hasEvents: boolean;
    jobs: { id: string; name: string; description?: string }[];
    legacyTasks?: import("../api/types").PluginTask[];
    legacySettings?: import("../api/types").PluginSettingSchema[];
  };

  const allExtensions: UnifiedExtension[] = useMemo(() => {
    const list: UnifiedExtension[] = [];

    // .NET extensions
    for (const ext of extList ?? []) {
      list.push({
        id: ext.id,
        name: ext.name,
        version: ext.version,
        description: ext.description,
        author: ext.author,
        url: ext.url,
        enabled: ext.enabled,
        kind: ext.kind ?? "extension",
        categories: ext.categories,
        dependencies: ext.dependencies,
        source: "native",
        installSource: ext.source,
        hasUI: ext.hasUI,
        hasApi: ext.hasApi,
        hasJobs: ext.hasJobs,
        hasState: ext.hasState,
        hasEvents: ext.hasEvents,
        jobs: ext.jobs,
      });
    }

    // Legacy Python extensions
    for (const p of legacyList ?? []) {
      // Don't duplicate if already in .NET list
      if (list.some(e => e.id === p.id)) continue;
      list.push({
        id: p.id,
        name: p.name,
        version: p.version,
        description: p.description,
        enabled: p.enabled,
        url: p.url,
        kind: "extension",
        categories: [],
        dependencies: {},
        source: "legacy",
        installSource: "legacy",
        hasUI: false,
        hasApi: false,
        hasJobs: p.tasks.length > 0,
        hasState: false,
        hasEvents: false,
        jobs: [],
        legacyTasks: p.tasks,
        legacySettings: p.settings,
      });
    }

    return list.sort((a, b) => a.name.localeCompare(b.name));
  }, [extList, legacyList]);

  const nativeExtensions = useMemo(
    () => allExtensions.filter((extension) => extension.source === "native"),
    [allExtensions],
  );

  const getNativeDependents = (extensionId: string): ExtensionDependencyImpact[] =>
    getTransitiveExtensionDependents(nativeExtensions, extensionId).map(toExtensionDependencyImpact);

  const installedUpdateMap = useMemo(
    () => new Map((registryUpdates ?? []).map(update => [update.extensionId, update])),
    [registryUpdates],
  );

  // Derive categories from loaded extensions
  const allCategories = useMemo(() => {
    const cats = new Set<string>();
    for (const ext of allExtensions) {
      for (const c of ext.categories) cats.add(c);
    }
    return Array.from(cats).sort();
  }, [allExtensions]);

  // Filter
  const filtered = useMemo(() => {
    let list = allExtensions;
    if (categoryFilter !== "all") {
      list = list.filter(e => e.categories.some(c => c.toLowerCase() === categoryFilter.toLowerCase()));
    }
    if (searchQuery.trim()) {
      const q = searchQuery.trim().toLowerCase();
      list = list.filter(e =>
        e.name.toLowerCase().includes(q) ||
        (e.description?.toLowerCase().includes(q)) ||
        e.id.toLowerCase().includes(q)
      );
    }
    return list;
  }, [allExtensions, categoryFilter, searchQuery]);

  const toggleEnable = (ext: UnifiedExtension) => {
    if (ext.source === "legacy") {
      settingsMut.mutate({ enabledMap: { [ext.id]: !ext.enabled } });
    } else {
      enableMut.mutate({ id: ext.id, enable: !ext.enabled });
    }
  };

  return (
    <>
      {justInstalledSetup && (
        <div className="mb-4 flex items-center justify-between gap-3 rounded border border-accent/30 bg-accent/10 px-4 py-3">
          <div className="flex items-center gap-2 text-sm text-foreground">
            <BookOpen className="h-4 w-4 shrink-0 text-accent" />
            <span><span className="font-medium">{justInstalledSetup.name}</span> is installed. View its setup guide to finish getting it ready.</span>
          </div>
          <div className="flex items-center gap-2 shrink-0">
            <button
              type="button"
              onClick={() => {
                openTutorialStoryboard({ topicId: justInstalledSetup.topicId });
                setJustInstalledSetup(null);
              }}
              className="px-3 py-1 text-xs rounded font-medium bg-accent text-background hover:bg-accent/90"
            >
              View setup guide
            </button>
            <button
              type="button"
              onClick={() => setJustInstalledSetup(null)}
              className="rounded px-2 py-1 text-xs text-secondary hover:bg-card-hover/40"
              aria-label="Dismiss"
            >
              <X className="h-3.5 w-3.5" />
            </button>
          </div>
        </div>
      )}
      <ConfirmDialog
        open={extensionToUninstall != null}
        title="Uninstall Extension"
        message={extensionToUninstall ? formatDependentUninstallMessage(extensionToUninstall) : "Uninstall this extension?"}
        confirmLabel={extensionToUninstall?.source === "native" && extensionToUninstall.dependents.length > 0 ? "Uninstall All" : "Uninstall"}
        destructive
        isPending={uninstallMut.isPending}
        errorMessage={uninstallMut.error instanceof Error ? uninstallMut.error.message : undefined}
        onConfirm={() => {
          if (extensionToUninstall) {
            uninstallMut.mutate({
              ...extensionToUninstall,
              confirmedDependents: extensionToUninstall.source === "native" && extensionToUninstall.dependents.length > 0,
            });
          }
        }}
        onCancel={() => {
          if (uninstallMut.isPending) return;
          uninstallMut.reset();
          setExtensionToUninstall(null);
        }}
      />

      <ConfirmDialog
        open={pendingDependencyInstall != null}
        title="Install Dependencies"
        message={pendingDependencyInstall ? formatDependencyInstallMessage(pendingDependencyInstall) : "Install required dependencies?"}
        confirmLabel="Install All"
        destructive={false}
        isPending={upgradeMut.isPending}
        errorMessage={upgradeMut.error instanceof Error ? upgradeMut.error.message : undefined}
        onConfirm={() => {
          if (pendingDependencyInstall) {
            upgradeMut.mutate({
              id: pendingDependencyInstall.extensionId,
              version: pendingDependencyInstall.version,
              name: pendingDependencyInstall.name,
              installDependencies: true,
            });
          }
        }}
        onCancel={() => {
          if (upgradeMut.isPending) return;
          upgradeMut.reset();
          setPendingDependencyInstall(null);
        }}
      />

      {mode === "installed" && (
      <SectionCard title="Installed Extensions" description="Manage extensions loaded into this instance.">
        {/* Search and filter bar */}
        <div className="flex items-center gap-3 mb-4">
          <div className="relative flex-1">
            <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-muted" />
            <input
              type="text"
              placeholder="Search extensions..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              className="w-full pl-8 pr-3 py-1.5 text-sm bg-card border border-border rounded focus:outline-none focus:border-accent"
            />
          </div>
          {allCategories.length > 0 && (
            <select
              value={categoryFilter}
              onChange={(e) => setCategoryFilter(e.target.value)}
              className="px-3 py-1.5 text-sm bg-card border border-border rounded focus:outline-none focus:border-accent"
            >
              <option value="all">All Categories</option>
              {allCategories.map(c => (
                <option key={c} value={c}>{c}</option>
              ))}
            </select>
          )}
          <span className="text-sm text-secondary whitespace-nowrap">
            {filtered.length} extension{filtered.length !== 1 ? "s" : ""}
          </span>
        </div>

        {filtered.length === 0 && (
          <div className="text-sm text-muted py-6 text-center">
            {searchQuery || categoryFilter !== "all" ? "No extensions match your filter." : "No extensions installed."}
          </div>
        )}

        <div className="space-y-2">
          {filtered.map((ext) => {
            const isExpanded = expandedId === ext.id;
            const isBundle = ext.kind === "bundle";
            const update = installedUpdateMap.get(ext.id);
            return (
              <div key={ext.id} className="bg-card/50 rounded-lg border border-border/50 overflow-hidden">
                <div
                  className="flex items-center justify-between px-4 py-3 cursor-pointer hover:bg-card-hover/30 transition-colors"
                  onClick={() => setExpandedId(isExpanded ? null : ext.id)}
                >
                  <div className="flex items-center gap-3 min-w-0">
                    <div className={`w-2 h-2 rounded-full shrink-0 ${ext.enabled ? "bg-green-400" : "bg-gray-500"}`} />
                    <div className="min-w-0">
                      <div className="font-medium text-sm flex items-center gap-2 flex-wrap">
                        {ext.name}
                        <span className="text-xs text-muted">v{ext.version}</span>
                        {update && (
                          <span className="text-[10px] px-1.5 py-0.5 rounded bg-yellow-600/20 text-yellow-400 border border-yellow-600/30">
                            Upgrade: v{update.latestVersion}
                          </span>
                        )}
                        {isBundle && (
                          <span className="text-[10px] px-1.5 py-0.5 rounded bg-sky-500/15 text-sky-300 border border-sky-500/25">
                            Bundle
                          </span>
                        )}
                        {ext.installSource === "url" && (
                          <span className="text-[10px] px-1.5 py-0.5 rounded bg-yellow-500/15 text-yellow-300 border border-yellow-500/25">
                            Unverified
                          </span>
                        )}
                        {ext.author && <span className="text-xs text-muted">by {ext.author}</span>}
                      </div>
                      {ext.description && (
                        <div className="text-xs text-secondary truncate">{ext.description}</div>
                      )}
                      {ext.categories.length > 0 && (
                        <div className="flex gap-1 mt-1 flex-wrap">
                          {ext.categories.map(c => (
                            <span key={c} className="text-[10px] px-1.5 py-0.5 rounded bg-surface text-secondary border border-border/50">{c}</span>
                          ))}
                        </div>
                      )}
                    </div>
                  </div>
                  <div className="flex items-center gap-3 shrink-0">
                    {update && ext.source !== "legacy" && (
                      <button
                        onClick={(e) => {
                          e.stopPropagation();
                          upgradeMut.mutate({ id: ext.id, version: update.latestVersion, name: ext.name });
                        }}
                        disabled={upgradeMut.isPending}
                        className="px-3 py-1 text-xs rounded font-medium bg-yellow-600 text-white hover:bg-yellow-500 disabled:opacity-50 flex items-center gap-1"
                      >
                        {upgradeMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <RefreshCw className="w-3 h-3" />}
                        Upgrade
                      </button>
                    )}
                    {setupTopicByExtension.has(ext.id) && (
                      <button
                        type="button"
                        onClick={(e) => {
                          e.stopPropagation();
                          const topicId = setupTopicByExtension.get(ext.id);
                          if (topicId) openTutorialStoryboard({ topicId });
                        }}
                        className="inline-flex items-center gap-1 rounded px-2 py-1 text-xs font-medium text-accent transition-colors hover:bg-accent/10"
                        title="Open this extension's setup guide"
                      >
                        <BookOpen className="h-3.5 w-3.5" />
                        Setup guide
                      </button>
                    )}
                    {isBundle ? (
                      <span className="px-3 py-1 text-xs rounded font-medium bg-sky-500/15 text-sky-300 border border-sky-500/25">
                        Bundle
                      </span>
                    ) : (
                      <button
                        onClick={(e) => { e.stopPropagation(); toggleEnable(ext); }}
                        className={`px-3 py-1 text-xs rounded font-medium transition-colors ${
                          ext.enabled
                            ? "bg-green-600/20 text-green-400 hover:bg-green-600/30"
                            : "bg-card/30 text-secondary hover:bg-card-hover/40"
                        }`}
                      >
                        {ext.enabled ? "Enabled" : "Disabled"}
                      </button>
                    )}
                    {ext.source === "native" && (
                      <button
                        type="button"
                        onClick={(e) => {
                          e.stopPropagation();
                          uninstallMut.reset();
                          setExtensionToUninstall({
                            id: ext.id,
                            name: ext.name,
                            source: ext.source,
                            dependents: getNativeDependents(ext.id),
                          });
                        }}
                        disabled={uninstallMut.isPending}
                        className="inline-flex items-center gap-1 rounded px-2 py-1 text-xs font-medium text-red-300 transition-colors hover:bg-red-500/10 hover:text-red-200 disabled:opacity-50"
                        title="Uninstall extension"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                        Uninstall
                      </button>
                    )}
                    <span className="text-secondary text-xs">{isExpanded ? "▲" : "▼"}</span>
                  </div>
                </div>

                {isExpanded && (
                  <div className="px-4 pb-4 border-t border-border/50 pt-3 space-y-3">
                    <div className="text-xs text-muted">
                      <span className="font-medium">ID:</span> {ext.id}
                      {ext.url && (
                        <> · <a href={ext.url} target="_blank" rel="noopener noreferrer" className="text-accent hover:underline">{ext.url}</a></>
                      )}
                      {isBundle && <> · <span className="text-sky-300">Bundle package</span></>}
                      {ext.installSource === "url" && <> · <span className="text-yellow-300">Installed from URL</span></>}
                      {ext.source === "legacy" && <> · <span className="text-yellow-500">Python extension</span></>}
                    </div>

                    {update && ext.source !== "legacy" && (
                      <div className="flex items-center justify-between gap-3 rounded border border-yellow-600/30 bg-yellow-600/10 px-3 py-2">
                        <div className="text-xs text-yellow-100">
                          Update available: v{ext.version} to v{update.latestVersion}
                        </div>
                        <button
                          onClick={() => upgradeMut.mutate({ id: ext.id, version: update.latestVersion, name: ext.name })}
                          disabled={upgradeMut.isPending}
                          className="px-3 py-1 text-xs rounded font-medium bg-yellow-600 text-white hover:bg-yellow-500 disabled:opacity-50 flex items-center gap-1"
                        >
                          {upgradeMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <RefreshCw className="w-3 h-3" />}
                          Upgrade
                        </button>
                      </div>
                    )}

                    {/* Capability badges */}
                    <div className="flex gap-1.5 flex-wrap">
                      {isBundle && <ExtBadge label="Bundle" />}
                      {ext.hasUI && <ExtBadge label="UI" />}
                      {ext.hasApi && <ExtBadge label="API" />}
                      {ext.hasState && <ExtBadge label="Stateful" />}
                      {ext.hasJobs && <ExtBadge label="Jobs" />}
                      {ext.hasEvents && <ExtBadge label="Events" />}
                    </div>

                    {Object.keys(ext.dependencies).length > 0 && (
                      <div>
                        <div className="text-xs font-medium text-secondary mb-2">
                          {isBundle ? "Included Extensions" : "Dependencies"}
                        </div>
                        <div className="space-y-1.5">
                          {Object.entries(ext.dependencies).map(([depId, constraint]) => (
                            <div key={depId} className="flex items-center justify-between bg-surface/50 rounded px-3 py-2 gap-3">
                              <div className="text-sm font-medium truncate">{depId}</div>
                              <div className="text-xs text-muted shrink-0">{constraint}</div>
                            </div>
                          ))}
                        </div>
                      </div>
                    )}

                    {/* Jobs (only shown if extension has them) */}
                    {ext.jobs.length > 0 && (
                      <div>
                        <div className="text-xs font-medium text-secondary mb-2">Jobs</div>
                        <div className="space-y-1.5">
                          {ext.jobs.map(job => (
                            <div key={job.id} className="flex items-center justify-between bg-surface/50 rounded px-3 py-2">
                              <div>
                                <div className="text-sm font-medium">{job.name}</div>
                                {job.description && <div className="text-xs text-muted">{job.description}</div>}
                              </div>
                              <button
                                onClick={() => runJobMut.mutate({ id: ext.id, jobId: job.id })}
                                disabled={runJobMut.isPending}
                                className="px-2 py-1 text-xs bg-accent hover:bg-accent-hover rounded transition-colors disabled:opacity-50"
                              >
                                Run
                              </button>
                            </div>
                          ))}
                        </div>
                      </div>
                    )}

                    {/* Legacy tasks (only for Python extensions that have them) */}
                    {ext.legacyTasks && ext.legacyTasks.length > 0 && (
                      <div>
                        <div className="text-xs font-medium text-secondary mb-2">Tasks</div>
                        <div className="space-y-1.5">
                          {ext.legacyTasks.map(task => (
                            <div key={task.name} className="flex items-center justify-between bg-surface/50 rounded px-3 py-2">
                              <div>
                                <div className="text-sm font-medium">{task.name}</div>
                                {task.description && <div className="text-xs text-muted">{task.description}</div>}
                              </div>
                            </div>
                          ))}
                        </div>
                      </div>
                    )}

                    {/* Legacy settings */}
                    {ext.legacySettings && ext.legacySettings.length > 0 && (
                      <ExtensionSettingsForm extensionId={ext.id} schema={ext.legacySettings} />
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </SectionCard>
      )}

      {/* Extension-contributed settings panels */}
      {mode === "installed" && settingsPanels.length > 0 &&
        settingsPanels.map((panel) => {
          const Component = resolveComponent(panel.componentName);
          if (!Component) return null;
          return (
            <SectionCard
              key={panel.id}
              title={panel.label}
              description={`Settings provided by the ${panel.extensionId} extension.`}
            >
              <Component />
            </SectionCard>
          );
        })}

      {/* Find and Install Extensions */}
      {mode === "registry" && <FindAndInstallExtensions />}
    </>
  );
}

// ===== Find and Install Extensions =====
function FindAndInstallExtensions() {
  const queryClient = useQueryClient();
  const { manifest, refreshManifest } = useExtensions();
  const [searchQuery, setSearchQuery] = useState("");
  const [category, setCategory] = useState<string>("");
  const [registryType, setRegistryType] = useState<string>("");
  const [page, setPage] = useState(1);
  const [selectedExtension, setSelectedExtension] = useState<import("../api/types").RegistryExtensionDetail | null>(null);
  const [selectedVersion, setSelectedVersion] = useState<string>("");
  const [pendingDeps, setPendingDeps] = useState<import("../api/types").DependencyInfo[] | null>(null);
  const [pendingDependencyInstall, setPendingDependencyInstall] = useState<PendingExtensionInstall | null>(null);
  const [extensionToUninstall, setExtensionToUninstall] = useState<PendingExtensionUninstall | null>(null);
  const [showMoreActions, setShowMoreActions] = useState(false);
  const [showUrlInstallForm, setShowUrlInstallForm] = useState(false);
  const [urlInstallUrl, setUrlInstallUrl] = useState("");
  const [confirmUrlInstall, setConfirmUrlInstall] = useState(false);
  const [installError, setInstallError] = useState<string | null>(null);
  // Just-installed extension that ships a setup guide, surfaced as a post-install CTA (we don't auto-open it).
  const [justInstalledSetup, setJustInstalledSetup] = useState<{ name: string; topicId: string } | null>(null);

  const REGISTRY_PAGE_SIZE = 20;

  // Map an extension id to its setup-guide topic id (topics flagged kind === "setup" in the manifest).
  const findSetupTopicId = (topics: ExtensionTutorialTopic[] | undefined, extensionId: string) =>
    topics?.find((topic) => topic.kind === "setup" && topic.extensionId === extensionId)?.id;

  // Resolve a friendly display name for the setup CTA. The owning extension may be a dependency that
  // was just installed, so fall back to a fresh extensions list lookup before showing the raw id.
  const resolveExtensionName = async (extensionId: string, knownName?: string): Promise<string> => {
    if (knownName) return knownName;
    try {
      const list = await import("../api/client").then((m) => m.extensions.list());
      return list.find((ext) => ext.id === extensionId)?.name ?? extensionId;
    } catch {
      return extensionId;
    }
  };

  // Reset to the first page whenever the search/filters change.
  useEffect(() => {
    setPage(1);
  }, [searchQuery, category, registryType]);

  const { data: searchResults, isLoading: searching, refetch: doSearch } = useQuery({
    queryKey: ["registry-search", searchQuery, category, registryType, page],
    queryFn: () => import("../api/client").then(m =>
      m.extensions.registrySearch({ q: searchQuery || undefined, category: category || undefined, type: registryType || undefined, page, pageSize: REGISTRY_PAGE_SIZE })
    ),
    enabled: true,
  });

  const { data: registryCategories } = useQuery({
    queryKey: ["registry-categories"],
    queryFn: () => import("../api/client").then(m => m.extensions.registryGetCategories()),
  });

  const { data: updates } = useQuery({
    queryKey: ["registry-updates"],
    queryFn: () => import("../api/client").then(m => m.extensions.registryCheckUpdates()),
  });

  const { data: installedList } = useQuery({
    queryKey: ["extensions-list"],
    queryFn: () => import("../api/client").then(m => m.extensions.list()),
  });

  const installMut = useMutation({
    mutationFn: (args: { extensionId: string; version: string; name?: string; installDependencies?: boolean }) =>
      import("../api/client").then(m => m.extensions.registryInstall(args.extensionId, args.version, args.installDependencies)),
    onSuccess: (data, variables) => {
      setInstallError(null);
      if (data.requiresDependencies && data.missingDependencies?.length) {
        setPendingDeps(data.missingDependencies);
        setPendingDependencyInstall({
          extensionId: variables.extensionId,
          version: variables.version,
          name: data.extension?.name ?? variables.name,
          dependencies: data.missingDependencies,
        });
        return;
      }
      setPendingDeps(null);
      setPendingDependencyInstall(null);
      queryClient.invalidateQueries({ queryKey: ["extensions-list"] });
      queryClient.invalidateQueries({ queryKey: ["registry-search"] });
      queryClient.invalidateQueries({ queryKey: ["registry-updates"] });

      // Pull the freshened manifest so a newly installed extension's setup guide becomes
      // available, then offer it as a CTA rather than opening the manual automatically. The
      // setup guide can belong to an auto-installed dependency (e.g. installing AI Tagging pulls
      // in AI Core, which is the one that ships the setup guide), so scan the requested extension
      // and every dependency that was installed alongside it.
      void refreshManifest().then((fresh) => {
        const candidateIds = [variables.extensionId, ...(data.installedDependencies ?? [])];
        const match = candidateIds
          .map((id) => ({ id, topicId: findSetupTopicId(fresh?.tutorialTopics, id) }))
          .find((candidate) => candidate.topicId);
        if (match?.topicId) {
          void resolveExtensionName(match.id, match.id === variables.extensionId ? data.extension?.name ?? variables.name : undefined)
            .then((name) => setJustInstalledSetup({ name, topicId: match.topicId! }));
        }
      });
    },
    onError: (error) => setInstallError(error instanceof Error ? error.message : "Extension install failed."),
  });

  const urlInstallMut = useMutation({
    mutationFn: () => import("../api/client").then(m =>
      m.extensions.installFromUrl(urlInstallUrl.trim(), true)
    ),
    onSuccess: (data) => {
      setInstallError(null);
      setConfirmUrlInstall(false);
      setShowUrlInstallForm(false);
      setUrlInstallUrl("");
      queryClient.invalidateQueries({ queryKey: ["extensions-list"] });
      queryClient.invalidateQueries({ queryKey: ["registry-search"] });
      queryClient.invalidateQueries({ queryKey: ["registry-updates"] });

      const installedId = data?.extensionId;
      if (installedId) {
        void refreshManifest().then((fresh) => {
          const setupTopicId = findSetupTopicId(fresh?.tutorialTopics, installedId);
          if (setupTopicId) {
            void resolveExtensionName(installedId).then((name) => setJustInstalledSetup({ name, topicId: setupTopicId }));
          }
        });
      }
    },
    onError: (error) => setInstallError(error instanceof Error ? error.message : "Extension install failed."),
  });

  const uninstallMut = useMutation({
    mutationFn: (target: PendingExtensionUninstall) =>
      import("../api/client").then(m => m.extensions.registryUninstall(target.id, target.confirmedDependents || target.dependents.length > 0)),
    onSuccess: (data, variables) => {
      if (data.requiresDependents && data.dependents?.length) {
        setExtensionToUninstall({ ...variables, dependents: data.dependents });
        return;
      }

      setExtensionToUninstall(null);
      queryClient.invalidateQueries({ queryKey: ["extensions-list"] });
      queryClient.invalidateQueries({ queryKey: ["registry-search"] });
    },
    onError: (error) => setInstallError(error instanceof Error ? error.message : "Extension uninstall failed."),
  });

  const installedMap = new Map((installedList ?? []).map(e => [e.id, e]));
  const installedIds = new Set(installedMap.keys());
  const getInstalledDependents = (extensionId: string): ExtensionDependencyImpact[] =>
    getTransitiveExtensionDependents(installedList ?? [], extensionId).map(toExtensionDependencyImpact);
  const updateMap = new Map((updates ?? []).map(u => [u.extensionId, u]));
  const registryItems = searchResults?.items ?? [];
  const totalPages = searchResults ? Math.max(1, Math.ceil(searchResults.totalCount / (searchResults.pageSize || REGISTRY_PAGE_SIZE))) : 1;

  const viewDetail = async (id: string) => {
    const detail = await import("../api/client").then(m => m.extensions.registryGetExtension(id));
    setSelectedExtension(detail);
    setSelectedVersion(detail.version);
    setPendingDeps(null);
  };

  const selectedInstalledVersion = selectedExtension ? installedMap.get(selectedExtension.id)?.version : undefined;
  const selectedRequestedVersion = selectedExtension ? (selectedVersion || selectedExtension.version) : "";

  return (
    <SectionCard title="Find and Install Extensions" description="Browse and install extensions from the official Cove extension registry.">
      {justInstalledSetup && (
        <div className="mb-4 flex items-center justify-between gap-3 rounded border border-accent/30 bg-accent/10 px-4 py-3">
          <div className="flex items-center gap-2 text-sm text-foreground">
            <BookOpen className="h-4 w-4 shrink-0 text-accent" />
            <span><span className="font-medium">{justInstalledSetup.name}</span> is installed. View its setup guide to finish getting it ready.</span>
          </div>
          <div className="flex items-center gap-2 shrink-0">
            <button
              type="button"
              onClick={() => {
                openTutorialStoryboard({ topicId: justInstalledSetup.topicId });
                setJustInstalledSetup(null);
              }}
              className="px-3 py-1 text-xs rounded font-medium bg-accent text-background hover:bg-accent/90"
            >
              View setup guide
            </button>
            <button
              type="button"
              onClick={() => setJustInstalledSetup(null)}
              className="rounded px-2 py-1 text-xs text-secondary hover:bg-card-hover/40"
              aria-label="Dismiss"
            >
              <X className="h-3.5 w-3.5" />
            </button>
          </div>
        </div>
      )}
      {/* Updates banner */}
      {updates && updates.length > 0 && (
        <div className="mb-4 p-3 bg-yellow-600/10 border border-yellow-600/30 rounded-lg">
          <div className="text-sm font-medium text-yellow-400 mb-1">Updates Available</div>
          <div className="space-y-1">
            {updates.map(u => (
              <div key={u.extensionId} className="flex items-center justify-between text-xs">
                <span className="text-secondary">{u.extensionId}: v{u.currentVersion} → v{u.latestVersion}</span>
                <button
                  onClick={() => installMut.mutate({ extensionId: u.extensionId, version: u.latestVersion, name: u.extensionId })}
                  disabled={installMut.isPending}
                  className="px-2 py-0.5 bg-yellow-600 hover:bg-yellow-500 text-white rounded text-xs disabled:opacity-50"
                >
                  Update
                </button>
              </div>
            ))}
          </div>
        </div>
      )}

      {installError && !confirmUrlInstall && !selectedExtension && (
        <div className="mb-4 rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200">
          {installError}
        </div>
      )}

      <ConfirmDialog
        open={confirmUrlInstall}
        title="Install Unverified Extension"
        message="Extensions installed from a URL are unsafe unless you trust the author and this exact package. Cove cannot verify this source."
        confirmLabel="Install"
        destructive
        isPending={urlInstallMut.isPending}
        errorMessage={installError}
        onConfirm={() => urlInstallMut.mutate()}
        onCancel={() => {
          if (urlInstallMut.isPending) return;
          setConfirmUrlInstall(false);
          setInstallError(null);
        }}
      />

      <ConfirmDialog
        open={pendingDependencyInstall != null}
        title="Install Dependencies"
        message={pendingDependencyInstall ? formatDependencyInstallMessage(pendingDependencyInstall) : "Install required dependencies?"}
        confirmLabel="Install All"
        destructive={false}
        isPending={installMut.isPending}
        errorMessage={installError}
        onConfirm={() => {
          if (pendingDependencyInstall) {
            installMut.mutate({
              extensionId: pendingDependencyInstall.extensionId,
              version: pendingDependencyInstall.version,
              name: pendingDependencyInstall.name,
              installDependencies: true,
            });
          }
        }}
        onCancel={() => {
          if (installMut.isPending) return;
          installMut.reset();
          setPendingDeps(null);
          setPendingDependencyInstall(null);
          setInstallError(null);
        }}
      />

      <ConfirmDialog
        open={extensionToUninstall != null}
        title="Uninstall Extension"
        message={extensionToUninstall ? formatDependentUninstallMessage(extensionToUninstall) : "Uninstall this extension?"}
        confirmLabel={extensionToUninstall?.dependents.length ? "Uninstall All" : "Uninstall"}
        destructive
        isPending={uninstallMut.isPending}
        errorMessage={installError}
        onConfirm={() => {
          if (extensionToUninstall) {
            uninstallMut.mutate({
              ...extensionToUninstall,
              confirmedDependents: extensionToUninstall.dependents.length > 0,
            });
          }
        }}
        onCancel={() => {
          if (uninstallMut.isPending) return;
          uninstallMut.reset();
          setExtensionToUninstall(null);
          setInstallError(null);
        }}
      />

      {/* Search and filter */}
      <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center">
        <div className="relative flex-1">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-muted" />
          <input
            type="text"
            placeholder="Search the extension registry..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="min-h-10 w-full rounded border border-border bg-card py-2 pl-8 pr-3 text-sm focus:border-accent focus:outline-none sm:min-h-0 sm:py-1.5"
          />
        </div>
        <select
          value={registryType}
          onChange={(e) => setRegistryType(e.target.value)}
          className="min-h-10 rounded border border-border bg-card px-3 py-2 text-sm focus:border-accent focus:outline-none sm:min-h-0 sm:w-auto sm:py-1.5"
          title="Filter by extension type"
        >
          <option value="">All Types</option>
          <option value="extension">Extensions</option>
          <option value="scraper">Scrapers</option>
          <option value="downloader">Downloaders</option>
        </select>
        {registryCategories && registryCategories.length > 0 && (
          <select
            value={category}
            onChange={(e) => setCategory(e.target.value)}
            className="min-h-10 rounded border border-border bg-card px-3 py-2 text-sm focus:border-accent focus:outline-none sm:min-h-0 sm:w-auto sm:py-1.5"
          >
            <option value="">All Categories</option>
            {registryCategories.map(c => (
              <option key={c} value={c}>{c}</option>
            ))}
          </select>
        )}
        <div className="relative">
          <button
            type="button"
            title="More extension actions"
            onClick={() => setShowMoreActions((open) => !open)}
            className="inline-flex h-10 w-10 items-center justify-center rounded border border-border bg-card text-secondary hover:text-foreground sm:h-8 sm:w-8"
          >
            <MoreHorizontal className="h-4 w-4" />
          </button>
          {showMoreActions && (
            <div className="absolute right-0 z-20 mt-1 w-44 rounded border border-border bg-surface p-1 shadow-lg">
              <button
                type="button"
                onClick={() => {
                  setShowMoreActions(false);
                  setShowUrlInstallForm(true);
                  setInstallError(null);
                }}
                className="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-xs text-secondary hover:bg-card hover:text-foreground"
              >
                <Shield className="h-3.5 w-3.5" />
                Install from URL...
              </button>
            </div>
          )}
        </div>
      </div>

      {showUrlInstallForm && (
        <form
          className="mb-4 rounded border border-border bg-card/60 p-3"
          onSubmit={(event) => {
            event.preventDefault();
            setInstallError(null);
            if (urlInstallUrl.trim()) setConfirmUrlInstall(true);
          }}
        >
          <div className="mb-2 flex items-center justify-between gap-2">
            <div className="flex items-center gap-2 text-sm font-medium text-secondary">
              <Shield className="h-4 w-4" /> Install from URL
            </div>
            <button
              type="button"
              onClick={() => {
                setShowUrlInstallForm(false);
                setUrlInstallUrl("");
                setInstallError(null);
              }}
              disabled={urlInstallMut.isPending}
              className="text-secondary hover:text-foreground disabled:opacity-50"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
          <div className="grid gap-2 md:grid-cols-[minmax(0,1fr)_auto]">
            <input
              type="url"
              value={urlInstallUrl}
              onChange={(e) => setUrlInstallUrl(e.target.value)}
              placeholder="https://example.com/extension.zip"
              className="min-h-10 min-w-0 rounded border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none sm:min-h-0 sm:py-1.5"
            />
            <button
              type="submit"
              disabled={!urlInstallUrl.trim() || urlInstallMut.isPending}
              className="inline-flex min-h-10 items-center justify-center gap-1 rounded bg-card-hover px-3 py-2 text-sm font-medium text-foreground hover:bg-accent hover:text-white disabled:cursor-not-allowed disabled:opacity-50 sm:min-h-0 sm:py-1.5 sm:text-xs"
            >
              {urlInstallMut.isPending ? <Loader2 className="h-3 w-3 animate-spin" /> : <Download className="h-3 w-3" />}
              Install
            </button>
          </div>
        </form>
      )}

      {/* Extension detail modal */}
      {selectedExtension && (
        <div className="mb-4 p-4 bg-surface rounded-lg border border-border">
          <div className="flex items-start justify-between mb-3">
            <div>
              <h3 className="text-lg font-semibold flex items-center gap-2 flex-wrap">
                <span>{selectedExtension.name}</span>
                {selectedExtension.kind === "bundle" && (
                  <span className="text-[10px] px-1.5 py-0.5 rounded bg-sky-500/15 text-sky-300 border border-sky-500/25">
                    Bundle
                  </span>
                )}
              </h3>
              <div className="text-xs text-muted mt-0.5">
                v{selectedExtension.version}
                {selectedExtension.author && <> · by {selectedExtension.author}</>}
              </div>
            </div>
            <button
              onClick={() => { setSelectedExtension(null); setPendingDeps(null); }}
              className="text-secondary hover:text-foreground"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
          {selectedExtension.description && (
            <p className="text-sm text-secondary mb-3">{selectedExtension.description}</p>
          )}
          {selectedExtension.kind === "bundle" && Object.keys(selectedExtension.dependencies).length > 0 && (
            <div className="mb-3 p-2 bg-sky-500/10 border border-sky-500/20 rounded text-xs text-sky-100">
              Installs {Object.keys(selectedExtension.dependencies).length} bundled extension{Object.keys(selectedExtension.dependencies).length !== 1 ? "s" : ""} in one step.
            </div>
          )}
          {selectedExtension.categories.length > 0 && (
            <div className="flex gap-1 mb-3 flex-wrap">
              {selectedExtension.categories.map(c => (
                <span key={c} className="text-[10px] px-1.5 py-0.5 rounded bg-surface text-secondary border border-border/50">{c}</span>
              ))}
            </div>
          )}

          {/* Version picker */}
          {selectedExtension.versions.length > 1 && (
            <div className="mb-3">
              <label className="block text-xs font-medium text-muted mb-1">Version</label>
              <select
                value={selectedVersion}
                onChange={(e) => setSelectedVersion(e.target.value)}
                className="px-2 py-1 text-sm bg-card border border-border rounded focus:outline-none focus:border-accent"
              >
                {selectedExtension.versions.map(v => (
                  <option key={v.version} value={v.version}>
                    v{v.version}{v.releasedAt ? ` — ${new Date(v.releasedAt).toLocaleDateString()}` : ""}
                  </option>
                ))}
              </select>
            </div>
          )}

          {/* Dependencies */}
          {Object.keys(selectedExtension.dependencies).length > 0 && (
            <div className="mb-3 p-2 bg-card rounded border border-border/50">
              <div className="text-xs font-medium text-muted mb-1">
                {selectedExtension.kind === "bundle" ? "Included Extensions" : "Dependencies"}
              </div>
              <div className="space-y-0.5">
                {Object.entries(selectedExtension.dependencies).map(([depId, constraint]) => {
                  const isDepInstalled = installedIds.has(depId);
                  return (
                    <div key={depId} className="flex items-center gap-2 text-xs">
                      <span className={isDepInstalled ? "text-green-400" : "text-yellow-400"}>
                        {isDepInstalled ? "✓" : "○"}
                      </span>
                      <span className="text-secondary">{depId}</span>
                      <span className="text-muted">{constraint}</span>
                    </div>
                  );
                })}
              </div>
            </div>
          )}

          {/* Dependency resolution prompt */}
          {pendingDeps && pendingDeps.length > 0 && (
            <div className="mb-3 p-3 bg-yellow-600/10 border border-yellow-600/30 rounded-lg">
              <div className="text-sm font-medium text-yellow-400 mb-2">Missing Dependencies</div>
              <div className="space-y-1 mb-3">
                {pendingDeps.map(dep => (
                  <div key={dep.id} className="flex items-center gap-2 text-xs">
                    <span className={dep.available ? "text-green-400" : "text-red-400"}>
                      {dep.available ? "↓" : "✗"}
                    </span>
                    <span className="text-secondary">{dep.name || dep.id}</span>
                    <span className="text-muted">{dep.versionConstraint}</span>
                    {!dep.available && <span className="text-red-400">(not available in registry)</span>}
                  </div>
                ))}
              </div>
              <div className="flex gap-2">
                <button
                  onClick={() => installMut.mutate({
                    extensionId: selectedExtension.id,
                    version: selectedVersion || selectedExtension.version,
                    name: selectedExtension.name,
                    installDependencies: true,
                  })}
                  disabled={installMut.isPending || pendingDeps.some(d => !d.available)}
                  className="px-3 py-1 text-xs bg-accent hover:bg-accent-hover text-white rounded disabled:opacity-50"
                >
                  {installMut.isPending ? "Installing..." : "Install All"}
                </button>
                <button
                  onClick={() => setPendingDeps(null)}
                  className="px-3 py-1 text-xs bg-card border border-border text-secondary rounded hover:text-foreground"
                >
                  Cancel
                </button>
              </div>
            </div>
          )}

          {selectedExtension.readme && (
            <div className="text-xs text-secondary bg-card rounded p-3 mb-3 max-h-48 overflow-y-auto whitespace-pre-wrap">
              {selectedExtension.readme}
            </div>
          )}
          {installError && !pendingDeps && (
            <div className="mb-3 rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200">
              {installError}
            </div>
          )}
          <div className="flex flex-wrap gap-2">
            <button
              onClick={() => installMut.mutate({ extensionId: selectedExtension.id, version: selectedRequestedVersion, name: selectedExtension.name })}
              disabled={installMut.isPending}
                className="flex min-h-10 items-center gap-1.5 rounded bg-accent px-4 py-2 text-sm text-white hover:bg-accent-hover disabled:opacity-50 sm:min-h-0 sm:py-1.5"
            >
              {installMut.isPending ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Download className="w-3.5 h-3.5" />}
              {!selectedInstalledVersion
                ? selectedExtension.kind === "bundle" ? "Install Bundle" : `Install v${selectedRequestedVersion}`
                : selectedInstalledVersion === selectedRequestedVersion ? `Reinstall v${selectedRequestedVersion}` : `Install v${selectedRequestedVersion}`}
            </button>
            {selectedInstalledVersion ? (
              <button
                onClick={() => setExtensionToUninstall({
                  id: selectedExtension.id,
                  name: selectedExtension.name,
                  source: "native",
                  dependents: getInstalledDependents(selectedExtension.id),
                })}
                disabled={uninstallMut.isPending}
                className="flex min-h-10 items-center gap-1.5 rounded border border-border bg-card px-4 py-2 text-sm text-muted hover:border-red-500 hover:text-red-400 disabled:opacity-50 sm:min-h-0 sm:py-1.5"
              >
                {uninstallMut.isPending ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Trash2 className="w-3.5 h-3.5" />}
                {selectedExtension.kind === "bundle" ? "Uninstall Bundle" : "Uninstall"}
              </button>
            ) : null}
          </div>
        </div>
      )}

      {/* Results grid */}
      {searching ? (
        <div className="flex items-center justify-center py-8">
          <Loader2 className="w-5 h-5 animate-spin text-muted" />
        </div>
      ) : !searchResults || registryItems.length === 0 ? (
        <div className="text-sm text-muted text-center py-6">
          {searchQuery ? "No extensions found matching your search." : "No extensions available in the registry yet."}
        </div>
      ) : (
        <div className="space-y-2">
          {registryItems.map((ext) => {
            const isInstalled = installedIds.has(ext.id);
            const update = updateMap.get(ext.id);
            return (
              <div
                key={ext.id}
                className="flex cursor-pointer flex-col gap-3 rounded-xl border border-border bg-card px-4 py-3 transition-colors hover:bg-card-hover/30 sm:flex-row sm:items-center sm:justify-between"
                onClick={() => viewDetail(ext.id)}
              >
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2 flex-wrap">
                    <span className="text-sm font-medium text-foreground">{ext.name}</span>
                    <span className="text-xs text-muted">v{ext.version}</span>
                    {ext.kind === "bundle" && (
                      <span className="text-[10px] px-1.5 py-0.5 rounded bg-sky-500/15 text-sky-300 border border-sky-500/25">
                        Bundle
                      </span>
                    )}
                    {ext.author && <span className="text-xs text-muted">by {ext.author}</span>}
                    {isInstalled && (
                      <span className="text-xs px-1.5 py-0.5 rounded bg-green-600/20 text-green-400">Installed</span>
                    )}
                    {update && (
                      <span className="text-xs px-1.5 py-0.5 rounded bg-yellow-600/20 text-yellow-400">
                        Update: v{update.latestVersion}
                      </span>
                    )}
                  </div>
                  {ext.description && <p className="text-xs text-secondary mt-0.5 truncate">{ext.description}</p>}
                  {ext.categories.length > 0 && (
                    <div className="flex gap-1 mt-1 flex-wrap">
                      {ext.categories.map(c => (
                        <span key={c} className="text-[10px] px-1.5 py-0.5 rounded bg-surface text-secondary border border-border/50">{c}</span>
                      ))}
                    </div>
                  )}
                </div>
                <div className="flex w-full flex-wrap gap-2 sm:ml-3 sm:w-auto sm:flex-shrink-0">
                  {!isInstalled ? (
                    <button
                      onClick={(e) => { e.stopPropagation(); installMut.mutate({ extensionId: ext.id, version: ext.version, name: ext.name }); }}
                      disabled={installMut.isPending}
                      className="flex min-h-10 items-center gap-1 rounded bg-accent px-3 py-2 text-sm text-white hover:bg-accent-hover disabled:opacity-50 sm:min-h-0 sm:py-1.5 sm:text-xs"
                    >
                      {installMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Download className="w-3 h-3" />}
                      Install
                    </button>
                  ) : update ? (
                    <button
                      onClick={(e) => { e.stopPropagation(); installMut.mutate({ extensionId: ext.id, version: update.latestVersion, name: ext.name }); }}
                      disabled={installMut.isPending}
                      className="flex min-h-10 items-center gap-1 rounded bg-yellow-600 px-3 py-2 text-sm text-white hover:bg-yellow-500 disabled:opacity-50 sm:min-h-0 sm:py-1.5 sm:text-xs"
                    >
                      {installMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <RefreshCw className="w-3 h-3" />}
                      Update
                    </button>
                  ) : (
                    <button
                      onClick={(e) => {
                        e.stopPropagation();
                        setExtensionToUninstall({
                          id: ext.id,
                          name: ext.name,
                          source: "native",
                          dependents: getInstalledDependents(ext.id),
                        });
                      }}
                      disabled={uninstallMut.isPending}
                      className="flex min-h-10 items-center gap-1 rounded border border-border bg-card px-3 py-2 text-sm text-muted hover:border-red-500 hover:text-red-400 disabled:opacity-50 sm:min-h-0 sm:py-1.5 sm:text-xs"
                    >
                      {uninstallMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Trash2 className="w-3 h-3" />}
                      Uninstall
                    </button>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}

      {!searching && searchResults && registryItems.length > 0 && totalPages > 1 && (
        <div className="mt-4 flex flex-wrap items-center justify-center gap-1">
          <PaginationControls page={page} totalPages={totalPages} goTo={(p) => setPage(Math.min(Math.max(1, p), totalPages))} />
        </div>
      )}
    </SectionCard>
  );
}

function ExtBadge({ label }: { label: string }) {
  return (
    <span className="text-[10px] px-1.5 py-0.5 rounded bg-accent/15 text-accent border border-accent/25">
      {label}
    </span>
  );
}



