import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ChevronDown,
  ChevronUp,
  Database,
  Download,
  FolderOpen,
  HardDrive,
  Info,
  Loader2,
  Monitor,
  Plug,
  Plus,
  RefreshCw,
  SearchCode,
  Server,
  Shield,
  Trash2,
  PlayCircle,
  Radio,
  ScrollText,
  Upload,
  Wrench,
  FileText,
  History,
  Search,
  X,
} from "lucide-react";
import { system, jobs, metadata, database, plugins as pluginsApi, dlna as dlnaApi, logs as logsApi } from "../api/client";
import type { ScanOptions, GenerateOptions, CleanGeneratedOptions, ExportOptions, LogEntry } from "../api/client";
import type {
  DlnaStatus,
  JobInfo,
  PackageSource,
  Plugin,
  RatingStarPrecision,
  RatingSystemType,
  ScraperSummary,
  MetadataServer,
  CoveConfig,
  CovePathConfig,
  MetadataServerValidationResult,
} from "../api/types";
import { useExtensions } from "../extensions/ExtensionLoader";
import { useAppConfig } from "../state/AppConfigContext";
import { LOCATION_CHANGE_EVENT, buildCurrentUrl, navigateToUrl } from "../router/location";

type SettingsTab = "tasks" | "library" | "interface" | "security" | "metadata-providers" | "dlna" | "extensions" | "logs" | "system" | "tools" | "changelog" | "about";

const tabs: { key: SettingsTab; label: string; icon: typeof FolderOpen }[] = [
  { key: "tasks", label: "Tasks", icon: PlayCircle },
  { key: "library", label: "Library", icon: FolderOpen },
  { key: "interface", label: "Interface", icon: Monitor },
  { key: "security", label: "Security", icon: Shield },
  { key: "metadata-providers", label: "Metadata Providers", icon: SearchCode },
  { key: "dlna", label: "Services (DLNA)", icon: Radio },
  { key: "extensions", label: "Extensions", icon: Plug },
  { key: "logs", label: "Logs", icon: ScrollText },
  { key: "system", label: "System", icon: Server },
  { key: "tools", label: "Tools", icon: Wrench },
  { key: "changelog", label: "Changelog", icon: History },
  { key: "about", label: "About", icon: Info },
];

const SETTINGS_TAB_QUERY_KEY = "tab";
const TASK_SCAN_OPTIONS_KEY = "cove-settings-scan-options";
const TASK_GENERATE_OPTIONS_KEY = "cove-settings-generate-options";

const DEFAULT_SCAN_OPTIONS: ScanOptions = {
  scanGenerateCovers: true,
  scanGeneratePreviews: false,
  scanGenerateSprites: false,
  scanGeneratePhashes: false,
  scanGenerateThumbnails: false,
  scanGenerateImagePhashes: false,
  rescan: false,
};

const DEFAULT_GENERATE_OPTIONS: GenerateOptions = {
  thumbnails: true,
  previews: false,
  sprites: false,
  markers: false,
  phashes: false,
  imageThumbnails: false,
  imagePhashes: false,
  overwrite: false,
};

function isSettingsTab(value: string | null): value is SettingsTab {
  return tabs.some((tab) => tab.key === value);
}

function readSettingsTabFromUrl(): SettingsTab {
  const tab = new URLSearchParams(window.location.search).get(SETTINGS_TAB_QUERY_KEY);
  return isSettingsTab(tab) ? tab : "library";
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
  { value: "scenes", label: "Scenes" },
  { value: "images", label: "Images" },
  { value: "performers", label: "Performers" },
  { value: "galleries", label: "Galleries" },
  { value: "studios", label: "Studios" },
  { value: "tags", label: "Tags" },
  { value: "groups", label: "Groups" },
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
  return { path: "", excludeVideo: false, excludeImage: false };
}

function emptyPackageSource(): PackageSource {
  return { name: "", url: "" };
}

function emptyMetadataServer(): MetadataServer {
  return { name: "", endpoint: "", apiKey: "", maxRequestsPerMinute: 240 };
}

function cloneConfig(config: CoveConfig): CoveConfig {
  return JSON.parse(JSON.stringify(config)) as CoveConfig;
}

function linesToList(value: string) {
  return value.split(/\r?\n/);
}

function listToLines(values: string[]) {
  return values.join("\n");
}

function normalizeConfig(config: CoveConfig): CoveConfig {
  return {
    ...config,
    covePaths: config.covePaths.filter((path) => path.path.trim() !== ""),
    videoExtensions: config.videoExtensions.map((value) => value.trim()).filter(Boolean),
    imageExtensions: config.imageExtensions.map((value) => value.trim()).filter(Boolean),
    galleryExtensions: config.galleryExtensions.map((value) => value.trim()).filter(Boolean),
    excludePatterns: config.excludePatterns.map((value) => value.trim()).filter(Boolean),
    excludeImagePatterns: config.excludeImagePatterns.map((value) => value.trim()).filter(Boolean),
    excludeGalleryPatterns: config.excludeGalleryPatterns.map((value) => value.trim()).filter(Boolean),
    galleryCoverRegex: config.galleryCoverRegex.trim(),
    interface: {
      ...config.interface,
      menuItems: config.interface.menuItems.filter(Boolean),
    },
    security: {
      ...config.security,
      username: config.security.username?.trim() || undefined,
      newPassword: config.security.newPassword?.trim() || undefined,
    },
    scraping: {
      scraperDirectories: config.scraping.scraperDirectories.map((value) => value.trim()).filter(Boolean),
      scraperPackageSources: config.scraping.scraperPackageSources
        .map((source) => ({ name: source.name.trim(), url: source.url.trim() }))
        .filter((source) => source.url !== ""),
      metadataServers: config.scraping.metadataServers
        .map((box) => ({
          name: box.name.trim(),
          endpoint: box.endpoint.trim(),
          apiKey: box.apiKey.trim(),
          maxRequestsPerMinute: box.maxRequestsPerMinute,
        }))
        .filter((box) => box.endpoint !== ""),
    },
  };
}

export function SettingsPage() {
  const { config, status, configLoading, statusLoading } = useAppConfig();
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = useState<SettingsTab>(() => readSettingsTabFromUrl());
  const [draft, setDraft] = useState<CoveConfig | null>(null);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const initializedRef = useRef(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const savingRef = useRef(false);
  const [metadataServerValidation, setMetadataServerValidation] = useState<Record<string, MetadataServerValidationResult>>({});

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
    if (nextDraft.scraping.scraperPackageSources.length === 0) {
      nextDraft.scraping.scraperPackageSources = [emptyPackageSource()];
    }
    if (nextDraft.scraping.scraperDirectories.length === 0) {
      nextDraft.scraping.scraperDirectories = [""];
    }
    if (!nextDraft.ui.ratingSystemOptions) {
      nextDraft.ui.ratingSystemOptions = { type: "stars", starPrecision: "full" };
    }

    setDraft(nextDraft);
  }, [config]);

  useEffect(() => {
    const handleLocationChange = () => setActiveTab(readSettingsTabFromUrl());
    window.addEventListener("popstate", handleLocationChange);
    window.addEventListener(LOCATION_CHANGE_EVENT, handleLocationChange);

    return () => {
      window.removeEventListener("popstate", handleLocationChange);
      window.removeEventListener(LOCATION_CHANGE_EVENT, handleLocationChange);
    };
  }, []);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    if (activeTab === "library") {
      params.delete(SETTINGS_TAB_QUERY_KEY);
    } else {
      params.set(SETTINGS_TAB_QUERY_KEY, activeTab);
    }

    navigateToUrl(buildCurrentUrl(window.location.pathname, params), { replace: true });
  }, [activeTab]);

  const saveMutation = useMutation({
    mutationFn: (nextConfig: CoveConfig) => system.saveConfig(nextConfig),
    onSuccess: (savedConfig) => {
      savingRef.current = true;
      queryClient.setQueryData(["system-config"], savedConfig);
      queryClient.invalidateQueries({ queryKey: ["system-scrapers"] });
      setSaved(true);
      setError(null);
      setTimeout(() => setSaved(false), 2000);
    },
    onError: (err: Error) => setError(err.message),
  });

  const { data: scrapers = [], isLoading: scrapersLoading } = useQuery({
    queryKey: ["system-scrapers"],
    queryFn: system.listScrapers,
    enabled: activeTab === "metadata-providers",
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

  // Debounced auto-save: triggers 800ms after draft changes
  useEffect(() => {
    if (!draft) return;
    // Skip the first render when draft is initialized from config
    if (!initializedRef.current) {
      initializedRef.current = true;
      return;
    }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      saveMutation.mutate(normalizeConfig(draft));
    }, 800);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [draft]); // eslint-disable-line react-hooks/exhaustive-deps

  if (configLoading || !draft) {
    return (
      <div className="flex h-64 items-center justify-center">
        <Loader2 className="h-6 w-6 animate-spin text-muted" />
      </div>
    );
  }

  const updateDraft = (updater: (current: CoveConfig) => CoveConfig) => {
    setDraft((current) => (current ? updater(current) : current));
  };

  return (
    <div className="grid gap-6 lg:grid-cols-[240px_minmax(0,1fr)]">
      <aside className="h-fit rounded-2xl border border-border bg-surface p-2 lg:sticky lg:top-16">
        <div className="mb-2 px-3 py-2">
          <h1 className="text-lg font-semibold text-foreground">Settings</h1>
          <p className="mt-1 text-sm text-secondary">Stock Cove-style categories, backed by the rewrite config.</p>
        </div>
        <nav className="space-y-1">
          {tabs.map(({ key, label, icon: Icon }) => (
            <button
              key={key}
              onClick={() => setActiveTab(key)}
              className={`flex w-full items-center gap-2 rounded-xl px-3 py-2 text-left text-sm transition-colors ${
                activeTab === key
                  ? "bg-card text-foreground shadow-[inset_0_0_0_1px_var(--color-border)]"
                  : "text-secondary hover:bg-card hover:text-foreground"
              }`}
            >
              <Icon className="h-4 w-4" />
              <span>{label}</span>
            </button>
          ))}
        </nav>
      </aside>

      <div className="space-y-5">
        <section className="rounded-2xl border border-border bg-surface p-5 shadow-lg shadow-black/20">
          <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
            <div>
              <h2 className="text-xl font-semibold text-foreground">{tabs.find((tab) => tab.key === activeTab)?.label}</h2>
              <p className="mt-1 text-sm text-secondary">
                {activeTab === "tasks" && "Scan, generate, and maintenance operations."}
                {activeTab === "library" && "Content locations, generated assets, and scan rules."}
                {activeTab === "interface" && "Language, custom title, navigation, and rating presentation."}
                {activeTab === "security" && "Authentication and session settings. Password changes are persisted immediately."}
                {activeTab === "metadata-providers" && "Scraper directories, package source URLs, configured MetadataServer endpoints, and discovered Cove-compatible scrapers."}
                {activeTab === "dlna" && "DLNA media server for streaming to compatible devices on your local network."}
                {activeTab === "extensions" && "Manage extensions, themes, and settings."}
                {activeTab === "system" && "Host, port, and task concurrency. Server changes take effect after restart."}
                {activeTab === "tools" && "Utility tools for working with your library."}
                {activeTab === "changelog" && "Release history and version information."}
                {activeTab === "about" && "Runtime status and effective config locations."}
              </p>
            </div>
            <div className="flex flex-wrap items-center gap-3">
              {error && <span className="text-sm text-red-300">{error}</span>}
              {saveMutation.isPending && (
                <span className="inline-flex items-center gap-1.5 text-sm text-muted">
                  <Loader2 className="h-3.5 w-3.5 animate-spin" /> Saving…
                </span>
              )}
              {saved && !saveMutation.isPending && (
                <span className="text-sm text-emerald-300">Saved</span>
              )}
            </div>
          </div>
        </section>

        {activeTab === "tasks" && <TasksPanel />}

        {activeTab === "library" && (
          <>
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
                        placeholder="D:\\Media\\Scenes"
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

            <SectionCard title="Generated Assets" description="Control where generated and cached media artifacts are written.">
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
              </div>
            </SectionCard>

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
          </>
        )}

        {activeTab === "interface" && (
          <>
            <SectionCard title="Basic Interface" description="Persisted UI preferences used across the app shell.">
              <div className="grid gap-4 md:grid-cols-2">
                <SelectField
                  label="Language"
                  value={draft.interface.language ?? "en-US"}
                  onChange={(value) => updateDraft((current) => ({ ...current, interface: { ...current.interface, language: value } }))}
                  options={languageOptions}
                />
                <TextField
                  label="Custom title"
                  value={draft.ui.title ?? ""}
                  onChange={(value) => updateDraft((current) => ({ ...current, ui: { ...current.ui, title: value || undefined } }))}
                  placeholder="Cove"
                />
              </div>
            </SectionCard>

            <SectionCard title="Navigation" description="These menu items are reflected in the rewrite navbar immediately after save.">
              <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
                {menuItems.map((item) => (
                  <CheckboxLabel
                    key={item.value}
                    label={item.label}
                    checked={draft.interface.menuItems.includes(item.value)}
                    onChange={(checked) =>
                      updateDraft((current) => ({
                        ...current,
                        interface: {
                          ...current.interface,
                          menuItems: checked
                            ? [...new Set([...current.interface.menuItems, item.value])]
                            : current.interface.menuItems.filter((value) => value !== item.value),
                        },
                      }))
                    }
                  />
                ))}
              </div>
            </SectionCard>

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

            <SectionCard title="Scene Player" description="Playback behavior for the built-in video player.">
              <div className="space-y-3">
                <CheckboxLabel
                  label="Autostart video"
                  checked={draft.ui.autostartVideo}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, autostartVideo: checked } }))}
                />
                <CheckboxLabel
                  label="Autostart video on play selected"
                  checked={draft.ui.autostartVideoOnPlaySelected}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, autostartVideoOnPlaySelected: checked } }))}
                />
                <CheckboxLabel
                  label="Continue playlist default"
                  checked={draft.ui.continuePlaylistDefault}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, continuePlaylistDefault: checked } }))}
                />
                <CheckboxLabel
                  label="Show A-B loop controls"
                  checked={draft.ui.showAbLoopControls}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, showAbLoopControls: checked } }))}
                />
                <CheckboxLabel
                  label="Track activity"
                  checked={draft.ui.trackActivity}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, trackActivity: checked } }))}
                />
              </div>
            </SectionCard>

            <SectionCard title="Preview" description="Preview generation and playback settings.">
              <div className="space-y-4">
                <CheckboxLabel
                  label="Sound on preview"
                  checked={draft.ui.soundOnPreview}
                  onChange={(checked) => updateDraft((d) => ({ ...d, ui: { ...d.ui, soundOnPreview: checked } }))}
                />
                <div className="grid gap-4 md:grid-cols-2">
                  <NumberField
                    label="Preview segment duration (seconds)"
                    value={draft.ui.previewSegmentDuration}
                    min={0}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, previewSegmentDuration: value ?? d.ui.previewSegmentDuration } }))}
                  />
                  <NumberField
                    label="Preview segments"
                    value={draft.ui.previewSegments}
                    min={0}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, previewSegments: value ?? d.ui.previewSegments } }))}
                  />
                  <TextField
                    label="Preview exclude start"
                    value={draft.ui.previewExcludeStart}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, previewExcludeStart: value } }))}
                  />
                  <TextField
                    label="Preview exclude end"
                    value={draft.ui.previewExcludeEnd}
                    onChange={(value) => updateDraft((d) => ({ ...d, ui: { ...d.ui, previewExcludeEnd: value } }))}
                  />
                </div>
              </div>
            </SectionCard>

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
              </div>
            </SectionCard>

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

            <ThemeSelector />
          </>
        )}

        {activeTab === "security" && (
          <>
            <SectionCard title="Authentication" description="These values persist to config immediately. Enabling or disabling auth may still require a restart for middleware changes.">
              <div className="space-y-4">
                <CheckboxLabel
                  label="Require authentication"
                  checked={draft.security.enabled}
                  onChange={(checked) => updateDraft((current) => ({ ...current, security: { ...current.security, enabled: checked } }))}
                />
                <div className="grid gap-4 md:grid-cols-2">
                  <TextField
                    label="Username"
                    value={draft.security.username ?? ""}
                    onChange={(value) => updateDraft((current) => ({ ...current, security: { ...current.security, username: value || undefined } }))}
                    placeholder="cove"
                  />
                  <NumberField
                    label="Maximum session age (minutes)"
                    value={draft.security.maxSessionAgeMinutes}
                    min={1}
                    onChange={(value) =>
                      updateDraft((current) => ({
                        ...current,
                        security: {
                          ...current.security,
                          maxSessionAgeMinutes: value ?? current.security.maxSessionAgeMinutes,
                        },
                      }))
                    }
                  />
                </div>
                <TextField
                  label="New password"
                  type="password"
                  value={draft.security.newPassword ?? ""}
                  onChange={(value) => updateDraft((current) => ({ ...current, security: { ...current.security, newPassword: value || undefined } }))}
                  placeholder="Leave blank to keep the current password"
                />
              </div>
            </SectionCard>
          </>
        )}

        {activeTab === "metadata-providers" && (
          <>
            <SectionCard title="Scraper Directories" description="Directories are scanned recursively for Cove-compatible YAML scraper definitions.">
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

            <SectionCard title="Package Sources" description="Source URLs are stored now so scraper package installation can layer on top of the same config later.">
              <div className="space-y-3">
                {draft.scraping.scraperPackageSources.map((source, index) => (
                  <div key={index} className="grid gap-3 rounded-xl border border-border bg-card p-3 lg:grid-cols-[1fr_2fr_auto]">
                    <TextField
                      label="Source name"
                      value={source.name}
                      onChange={(value) =>
                        updateDraft((current) => ({
                          ...current,
                          scraping: {
                            ...current.scraping,
                            scraperPackageSources: current.scraping.scraperPackageSources.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, name: value } : item,
                            ),
                          },
                        }))
                      }
                      placeholder="Official"
                    />
                    <TextField
                      label="Source URL"
                      value={source.url}
                      onChange={(value) =>
                        updateDraft((current) => ({
                          ...current,
                          scraping: {
                            ...current.scraping,
                            scraperPackageSources: current.scraping.scraperPackageSources.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, url: value } : item,
                            ),
                          },
                        }))
                      }
                      placeholder="https://example.com/packages.yaml"
                    />
                    <div className="flex items-end">
                      <button
                        onClick={() =>
                          updateDraft((current) => ({
                            ...current,
                            scraping: {
                              ...current.scraping,
                              scraperPackageSources:
                                current.scraping.scraperPackageSources.length > 1
                                  ? current.scraping.scraperPackageSources.filter((_, itemIndex) => itemIndex !== index)
                                  : [emptyPackageSource()],
                            },
                          }))
                        }
                        className="inline-flex items-center gap-1 rounded-lg border border-border px-2 py-2 text-xs text-red-300 hover:border-red-500 hover:text-red-200"
                      >
                        <Trash2 className="h-3.5 w-3.5" /> Remove
                      </button>
                    </div>
                  </div>
                ))}
                <button
                  onClick={() =>
                    updateDraft((current) => ({
                      ...current,
                      scraping: {
                        ...current.scraping,
                        scraperPackageSources: [...current.scraping.scraperPackageSources, emptyPackageSource()],
                      },
                    }))
                  }
                  className="inline-flex items-center gap-2 rounded-xl border border-dashed border-border px-3 py-2 text-sm text-secondary hover:text-foreground"
                >
                  <Plus className="h-4 w-4" /> Add package source
                </button>
              </div>
            </SectionCard>

            <SectionCard title="Metadata Server Instances" description="Configure remote metadata-server GraphQL endpoints, validate credentials, and use them from performer detail pages.">
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
              ) : scrapers.length === 0 ? (
                <div className="rounded-xl border border-dashed border-border p-4 text-sm text-secondary">
                  No scraper definitions were found in the configured directories.
                </div>
              ) : (
                <div className="space-y-4">
                  {Object.entries(groupedScrapers).map(([entityType, entityScrapers]) => (
                    <ScraperTable key={entityType} entityType={entityType} scrapers={entityScrapers} />
                  ))}
                </div>
              )}
            </SectionCard>
          </>
        )}

        {activeTab === "system" && (
          <>
            <SectionCard title="Server" description="Host and port are persisted immediately but require a restart to rebind the listener.">
              <div className="grid gap-4 md:grid-cols-3">
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
                <NumberField
                  label="Max parallel tasks (-1 = all CPU threads)"
                  value={draft.maxParallelTasks}
                  min={-1}
                  max={128}
                  onChange={(value) => updateDraft((current) => ({ ...current, maxParallelTasks: value ?? current.maxParallelTasks }))}
                />
              </div>
            </SectionCard>

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

            <SectionCard title="Preview Generation" description="Settings for preview video generation during scanning.">
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
                  checked={draft.previewAudio === "true"}
                  onChange={(checked) => updateDraft((d) => ({ ...d, previewAudio: checked ? "true" : "false" }))}
                />
              </div>
            </SectionCard>

            <SectionCard title="Logging" description="Log level and output configuration. Changes take effect after restart.">
              <div className="space-y-4">
                <div className="grid gap-4 md:grid-cols-2">
                  <SelectField
                    label="Log level"
                    value={draft.logLevel}
                    onChange={(value) => updateDraft((d) => ({ ...d, logLevel: value }))}
                    options={[
                      { value: "Trace", label: "Trace" },
                      { value: "Debug", label: "Debug" },
                      { value: "Info", label: "Info" },
                      { value: "Warning", label: "Warning" },
                      { value: "Error", label: "Error" },
                    ]}
                  />
                  <TextField
                    label="Log file"
                    value={draft.logFile ?? ""}
                    onChange={(value) => updateDraft((d) => ({ ...d, logFile: value || undefined }))}
                    placeholder="Leave blank for no file logging"
                  />
                </div>
                <CheckboxLabel
                  label="Log to stdout"
                  checked={draft.logOut}
                  onChange={(checked) => updateDraft((d) => ({ ...d, logOut: checked }))}
                />
                <CheckboxLabel
                  label="Log access requests"
                  checked={draft.logAccess}
                  onChange={(checked) => updateDraft((d) => ({ ...d, logAccess: checked }))}
                />
              </div>
            </SectionCard>
          </>
        )}

        {activeTab === "extensions" && <ExtensionsPanel />}

        {activeTab === "dlna" && <DlnaPanel />}

        {activeTab === "logs" && <LogsPanel />}

        {activeTab === "tools" && (
          <>
            <SectionCard title="Developer Tools" description="API playground and utilities.">
              <div className="space-y-3">
                <a
                  href="/api/graphql"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="flex items-center gap-3 px-4 py-3 rounded-lg bg-surface hover:bg-surface/80 border border-border transition group"
                >
                  <div className="w-10 h-10 rounded bg-accent/20 flex items-center justify-center"><FileText className="w-5 h-5 text-accent" /></div>
                  <div>
                    <div className="text-sm font-medium text-foreground group-hover:text-accent">API Documentation</div>
                    <div className="text-xs text-muted">Browse the REST API endpoints</div>
                  </div>
                </a>
              </div>
            </SectionCard>
            <SectionCard title="Scene Tools" description="Utilities for managing scene files.">
              <div className="space-y-3">
                <button
                  onClick={() => (window.location.hash = "#/scenes?mode=duplicates")}
                  className="w-full flex items-center gap-3 px-4 py-3 rounded-lg bg-surface hover:bg-surface/80 border border-border transition group text-left"
                >
                  <div className="w-10 h-10 rounded bg-yellow-500/20 flex items-center justify-center"><Search className="w-5 h-5 text-yellow-400" /></div>
                  <div>
                    <div className="text-sm font-medium text-foreground group-hover:text-accent">Scene Duplicate Checker</div>
                    <div className="text-xs text-muted">Find and manage duplicate scenes by file fingerprint</div>
                  </div>
                </button>
              </div>
            </SectionCard>
          </>
        )}

        {activeTab === "changelog" && (
          <>
            <SectionCard title="Changelog" description="What's new in this version.">
              <div className="space-y-6">
                <div className="border-l-2 border-accent pl-4">
                  <h3 className="text-lg font-semibold text-foreground">v0.0.1 — Cove</h3>
                  <p className="text-xs text-muted mt-1">A modern media library organizer</p>
                  <ul className="mt-3 space-y-2 text-sm text-secondary">
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> New React 19 frontend with Tailwind CSS</li>
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> .NET 10 backend with PostgreSQL + pgvector</li>
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> Extension system with theme support</li>
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> Real-time job tracking via SignalR</li>
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> Custom fields on all entity types</li>
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> Full MetadataServer integration for scene tagger</li>
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> Video filters (brightness, contrast, saturation)</li>
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> Gallery and image management</li>
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> Performer, studio, tag, and group management</li>
                    <li className="flex items-start gap-2"><span className="text-emerald-400 mt-0.5">•</span> Scene markers with scrubber integration</li>
                  </ul>
                </div>
              </div>
            </SectionCard>
          </>
        )}

        {activeTab === "about" && (
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
                </div>
              </div>
            </SectionCard>

            <SectionCard title="Runtime Status" description="Effective values reported by the running backend instance.">
              {statusLoading && !status ? (
                <div className="flex items-center gap-2 text-sm text-secondary">
                  <Loader2 className="h-4 w-4 animate-spin" /> Loading status...
                </div>
              ) : status ? (
                <dl className="grid gap-4 md:grid-cols-2">
                  <InfoPair label="Version" value={status.version} />
                  <InfoPair label="Database" value={status.databasePath} />
                  <InfoPair label="Config file" value={status.configFile} />
                  <InfoPair label="App directory" value={status.appDir} />
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

            <SectionCard title="Current Config Summary" description="High-level values from the effective client-side config object.">
              <dl className="grid gap-4 md:grid-cols-2">
                <InfoPair label="Library paths" value={String(draft.covePaths.filter((path) => path.path.trim() !== "").length)} />
                <InfoPair label="Scraper directories" value={String(draft.scraping.scraperDirectories.filter(Boolean).length)} />
                <InfoPair label="Metadata Servers" value={String(draft.scraping.metadataServers.filter((box) => box.endpoint.trim() !== "").length)} />
                <InfoPair label="Rating system" value={draft.ui.ratingSystemOptions.type} />
                <InfoPair label="Authentication" value={draft.security.enabled ? "enabled" : "disabled"} />
              </dl>
            </SectionCard>

            <SectionCard title="Keyboard Shortcuts" description="Press ? anywhere to view the full shortcut reference.">
              <div className="grid gap-3 md:grid-cols-2 text-sm">
                <div className="flex justify-between"><span className="text-secondary">Global navigation</span><span className="font-mono text-xs bg-card px-2 py-0.5 rounded">g</span></div>
                <div className="flex justify-between"><span className="text-secondary">Theater mode (scenes)</span><span className="font-mono text-xs bg-card px-2 py-0.5 rounded">,</span></div>
                <div className="flex justify-between"><span className="text-secondary">Show all shortcuts</span><span className="font-mono text-xs bg-card px-2 py-0.5 rounded">?</span></div>
                <div className="flex justify-between"><span className="text-secondary">Search / filter</span><span className="font-mono text-xs bg-card px-2 py-0.5 rounded">/</span></div>
              </div>
            </SectionCard>
          </>
        )}
      </div>
    </div>
  );
}

function LogsPanel() {
  const [logLevel, setLogLevel] = useState("");
  const { data: logEntries, isLoading, refetch } = useQuery({
    queryKey: ["logs", logLevel],
    queryFn: () => logsApi.recent(logLevel || undefined, 200),
    refetchInterval: 5000,
  });

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
    <SectionCard title="Logs" description="Recent log entries from the server.">
      <div className="flex items-center gap-3 mb-4">
        <label className="text-sm text-secondary">Log Level</label>
        <select
          value={logLevel}
          onChange={(e) => setLogLevel(e.target.value)}
          className="rounded border border-border bg-surface px-3 py-1.5 text-sm text-foreground"
        >
          <option value="">All</option>
          <option value="Trace">Trace</option>
          <option value="Debug">Debug</option>
          <option value="Information">Info</option>
          <option value="Warning">Warning</option>
          <option value="Error">Error</option>
        </select>
        <button onClick={() => refetch()} className="flex items-center gap-1 rounded border border-border bg-surface px-3 py-1.5 text-sm text-secondary hover:text-foreground">
          <RefreshCw className="h-3.5 w-3.5" /> Refresh
        </button>
      </div>
      {isLoading ? (
        <div className="flex items-center gap-2 text-sm text-secondary">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading logs...
        </div>
      ) : logEntries && logEntries.length > 0 ? (
        <div className="max-h-[600px] overflow-y-auto rounded border border-border bg-background font-mono text-xs">
          {logEntries.map((entry, i) => (
            <div key={i} className="flex gap-3 border-b border-border/50 px-3 py-1.5 hover:bg-surface">
              <span className="shrink-0 text-muted">{entry.timestamp}</span>
              <span className={`shrink-0 w-14 font-semibold ${levelColor(entry.level)}`}>{entry.level}</span>
              <span className="text-foreground break-all">{entry.message}</span>
            </div>
          ))}
        </div>
      ) : (
        <p className="text-sm text-muted">No log entries found.</p>
      )}
    </SectionCard>
  );
}

function TasksPanel() {
  const queryClient = useQueryClient();
  const { data: activeJobs, refetch: refetchJobs } = useQuery({
    queryKey: ["jobs"],
    queryFn: () => jobs.list(),
    refetchInterval: 2000,
  });

  // ---- Job Queue ----
  const jobQueue = activeJobs && activeJobs.length > 0 ? (
    <SectionCard title="Job Queue" description="Currently running or queued jobs.">
      <div className="space-y-2">
        {activeJobs.map((job) => (
          <JobQueueCard key={job.id} job={job} onCancel={() => jobs.cancel(job.id).then(() => refetchJobs())} />
        ))}
      </div>
    </SectionCard>
  ) : null;

  return (
    <>
      {jobQueue}
      <LibraryTasksSection refetchJobs={refetchJobs} />
      <DataManagementSection refetchJobs={refetchJobs} />
      <ExtensionTasksSection refetchJobs={refetchJobs} />
    </>
  );
}

function formatJobDuration(ms: number): string {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return `${minutes}m ${seconds.toString().padStart(2, "0")}s`;
  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  return `${hours}h ${mins.toString().padStart(2, "0")}m`;
}

function JobQueueCard({ job, onCancel }: { job: JobInfo; onCancel: () => void }) {
  const [now, setNow] = useState(Date.now());
  const progressHistory = useRef<{ time: number; progress: number }[]>([]);

  useEffect(() => {
    if (job.status !== "running") return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [job.status]);

  // Track progress history for rolling window ETA
  useEffect(() => {
    if (job.status === "running" && job.progress > 0) {
      const hist = progressHistory.current;
      const now = Date.now();
      hist.push({ time: now, progress: job.progress });
      // Keep last 30 seconds
      const cutoff = now - 30000;
      while (hist.length > 0 && hist[0].time < cutoff) hist.shift();
    }
  }, [job.progress, job.status]);

  const progressPct = Math.round((job.progress ?? 0) * 100);
  const elapsedMs = now - new Date(job.startedAt).getTime();

  // Rolling window ETA: use rate from last 30s of progress updates
  let etaMs: number | null = null;
  const hist = progressHistory.current;
  if (hist.length >= 2 && job.progress >= 0.01) {
    const first = hist[0];
    const last = hist[hist.length - 1];
    const dt = last.time - first.time;
    const dp = last.progress - first.progress;
    if (dt > 1000 && dp > 0) {
      const rate = dp / dt; // progress per ms
      etaMs = (1.0 - last.progress) / rate;
    }
  }

  return (
    <div className="flex items-center justify-between rounded-xl border border-border bg-card p-3">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2">
          <span className="text-sm font-medium text-foreground">{job.description}</span>
          <span className={`text-xs px-1.5 py-0.5 rounded ${
            job.status === "running" ? "bg-green-600/20 text-green-300" :
            job.status === "pending" ? "bg-yellow-600/20 text-yellow-300" :
            "bg-card text-muted"
          }`}>
            {job.status}
          </span>
        </div>
        {job.subTask && (
          <p className="text-xs text-muted mt-1 truncate">{job.subTask}</p>
        )}
        {job.status === "running" && job.progress != null && job.progress >= 0 && (
          <>
            <div className="mt-2 h-2 w-full rounded-full bg-surface overflow-hidden">
              <div className="h-full rounded-full bg-accent transition-all" style={{ width: `${Math.min(progressPct, 100)}%` }} />
            </div>
            <div className="flex items-center justify-between mt-1">
              <span className="text-xs text-muted">
                {progressPct}% · {formatJobDuration(elapsedMs)} elapsed
              </span>
              {etaMs != null && (
                <span className="text-xs text-muted">
                  ~{formatJobDuration(etaMs)} remaining
                </span>
              )}
            </div>
          </>
        )}
      </div>
      <button
        onClick={onCancel}
        className="ml-3 text-xs text-muted hover:text-red-300 flex-shrink-0"
      >
        Cancel
      </button>
    </div>
  );
}

// ---- Library Tasks ----
function LibraryTasksSection({ refetchJobs }: { refetchJobs: () => void }) {
  const { config } = useAppConfig();
  const selectablePaths = useMemo(
    () => (config?.covePaths ?? []).map((path) => path.path.trim()).filter(Boolean),
    [config?.covePaths],
  );
  const [showScanOpts, setShowScanOpts] = useState(false);
  const [scanOpts, setScanOpts] = useState<ScanOptions>(() => loadStoredTaskOptions(TASK_SCAN_OPTIONS_KEY, DEFAULT_SCAN_OPTIONS));

  const [showGenOpts, setShowGenOpts] = useState(false);
  const [genOpts, setGenOpts] = useState<GenerateOptions>(() => loadStoredTaskOptions(TASK_GENERATE_OPTIONS_KEY, DEFAULT_GENERATE_OPTIONS));

  useEffect(() => {
    if (selectablePaths.length === 0) {
      return;
    }

    setScanOpts((current) => {
      const currentPaths = current.paths?.filter((path) => selectablePaths.includes(path));
      if (currentPaths && currentPaths.length > 0) {
        return currentPaths.length === current.paths?.length ? current : { ...current, paths: currentPaths };
      }

      return { ...current, paths: selectablePaths };
    });
  }, [selectablePaths]);

  useEffect(() => {
    localStorage.setItem(TASK_SCAN_OPTIONS_KEY, JSON.stringify(scanOpts));
  }, [scanOpts]);

  useEffect(() => {
    localStorage.setItem(TASK_GENERATE_OPTIONS_KEY, JSON.stringify(genOpts));
  }, [genOpts]);

  // Initialize generate paths like scan
  useEffect(() => {
    if (selectablePaths.length === 0) return;
    setGenOpts((current) => {
      const currentPaths = current.paths?.filter((path: string) => selectablePaths.includes(path));
      if (currentPaths && currentPaths.length > 0) {
        return currentPaths.length === current.paths?.length ? current : { ...current, paths: currentPaths };
      }
      return { ...current, paths: selectablePaths };
    });
  }, [selectablePaths]);

  const effectiveScanOpts = useMemo<ScanOptions>(() => {
    const selectedPaths = scanOpts.paths?.filter((path) => selectablePaths.includes(path)) ?? [];
    return {
      ...scanOpts,
      paths: selectablePaths.length === 0
        ? undefined
        : selectedPaths.length === selectablePaths.length
          ? undefined
          : selectedPaths,
    };
  }, [scanOpts, selectablePaths]);

  const allScanPathsSelected = selectablePaths.length > 0
    && (scanOpts.paths?.length ?? 0) === selectablePaths.length;

  const toggleScanPath = (path: string, checked: boolean) => {
    setScanOpts((current) => {
      const selectedPaths = current.paths?.filter((value) => selectablePaths.includes(value)) ?? selectablePaths;
      const nextPaths = checked
        ? [...new Set([...selectedPaths, path])]
        : selectedPaths.filter((value) => value !== path);
      return { ...current, paths: nextPaths };
    });
  };

  const effectiveGenOpts = useMemo<GenerateOptions>(() => {
    const selectedPaths = genOpts.paths?.filter((path: string) => selectablePaths.includes(path)) ?? [];
    return {
      ...genOpts,
      paths: selectablePaths.length === 0
        ? undefined
        : selectedPaths.length === selectablePaths.length
          ? undefined
          : selectedPaths,
    };
  }, [genOpts, selectablePaths]);

  const allGenPathsSelected = selectablePaths.length > 0
    && (genOpts.paths?.length ?? 0) === selectablePaths.length;

  const toggleGenPath = (path: string, checked: boolean) => {
    setGenOpts((current) => {
      const selectedPaths = current.paths?.filter((value: string) => selectablePaths.includes(value)) ?? selectablePaths;
      const nextPaths = checked
        ? [...new Set([...selectedPaths, path])]
        : selectedPaths.filter((value: string) => value !== path);
      return { ...current, paths: nextPaths };
    });
  };

  const scanMut = useMutation({ mutationFn: () => metadata.scan(effectiveScanOpts), onSuccess: () => refetchJobs() });
  const genMut = useMutation({ mutationFn: () => metadata.generate(effectiveGenOpts), onSuccess: () => refetchJobs() });
  const autoTagMut = useMutation({ mutationFn: () => metadata.autoTag(), onSuccess: () => refetchJobs() });

  return (
    <SectionCard title="Library Tasks" description="Scan for new content, generate supporting files, and auto-tag your library.">
      <div className="space-y-4">
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
          <div className="grid gap-2 sm:grid-cols-2 pt-3 border-t border-border/50">
            <CheckboxLabel label="Generate covers" checked={!!scanOpts.scanGenerateCovers} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateCovers: c })} />
            <CheckboxLabel label="Generate previews" checked={!!scanOpts.scanGeneratePreviews} onChange={(c) => setScanOpts({ ...scanOpts, scanGeneratePreviews: c })} />
            <CheckboxLabel label="Generate sprites" checked={!!scanOpts.scanGenerateSprites} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateSprites: c })} />
            <CheckboxLabel label="Generate perceptual hashes" checked={!!scanOpts.scanGeneratePhashes} onChange={(c) => setScanOpts({ ...scanOpts, scanGeneratePhashes: c })} />
            <CheckboxLabel label="Generate image thumbnails" checked={!!scanOpts.scanGenerateThumbnails} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateThumbnails: c })} />
            <CheckboxLabel label="Generate image phashes" checked={!!scanOpts.scanGenerateImagePhashes} onChange={(c) => setScanOpts({ ...scanOpts, scanGenerateImagePhashes: c })} />
            <CheckboxLabel label="Force rescan (ignore mtime)" checked={!!scanOpts.rescan} onChange={(c) => setScanOpts({ ...scanOpts, rescan: c })} />
            {selectablePaths.length > 0 && (
              <div className="sm:col-span-2 space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <p className="text-xs font-medium text-foreground">Selective scan</p>
                    <p className="text-[11px] text-muted">Choose specific library roots to scan, or leave them all selected for a full scan.</p>
                  </div>
                  <button
                    type="button"
                    onClick={() => setScanOpts({ ...scanOpts, paths: allScanPathsSelected ? [] : selectablePaths })}
                    className="text-[11px] text-accent hover:text-accent-hover"
                  >
                    {allScanPathsSelected ? "Clear" : "Select all"}
                  </button>
                </div>
                <div className="space-y-1.5">
                  {selectablePaths.map((path) => (
                    <CheckboxLabel
                      key={path}
                      label={path}
                      checked={scanOpts.paths?.includes(path) ?? false}
                      onChange={(checked) => toggleScanPath(path, checked)}
                    />
                  ))}
                </div>
              </div>
            )}
          </div>
        </TaskCard>

        {/* Auto Tag */}
        <TaskCard
          label="Auto Tag"
          description="Automatically tag content based on filenames and path patterns."
          onRun={() => autoTagMut.mutate()}
          isPending={autoTagMut.isPending}
        />

        {/* Generate */}
        <TaskCard
          label="Generate"
          description="Generate thumbnails, previews, sprites, markers, and perceptual hashes."
          onRun={() => genMut.mutate()}
          isPending={genMut.isPending}
          expandable
          expanded={showGenOpts}
          onToggleExpand={() => setShowGenOpts(!showGenOpts)}
        >
          <div className="space-y-3 pt-3 border-t border-border/50">
            <p className="text-xs text-muted font-medium uppercase tracking-wide">Scene options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Thumbnails / screenshots" checked={!!genOpts.thumbnails} onChange={(c) => setGenOpts({ ...genOpts, thumbnails: c })} />
              <CheckboxLabel label="Video previews" checked={!!genOpts.previews} onChange={(c) => setGenOpts({ ...genOpts, previews: c })} />
              <CheckboxLabel label="Sprite sheets" checked={!!genOpts.sprites} onChange={(c) => setGenOpts({ ...genOpts, sprites: c })} />
              <CheckboxLabel label="Marker previews" checked={!!genOpts.markers} onChange={(c) => setGenOpts({ ...genOpts, markers: c })} />
              <CheckboxLabel label="Perceptual hashes (phash)" checked={!!genOpts.phashes} onChange={(c) => setGenOpts({ ...genOpts, phashes: c })} />
            </div>
            <p className="text-xs text-muted font-medium uppercase tracking-wide pt-2">Image options</p>
            <div className="grid gap-2 sm:grid-cols-2">
              <CheckboxLabel label="Image thumbnails" checked={!!genOpts.imageThumbnails} onChange={(c) => setGenOpts({ ...genOpts, imageThumbnails: c })} />
              <CheckboxLabel label="Image phashes" checked={!!genOpts.imagePhashes} onChange={(c) => setGenOpts({ ...genOpts, imagePhashes: c })} />
            </div>
            <div className="pt-2">
              <CheckboxLabel label="Overwrite existing generated files" checked={!!genOpts.overwrite} onChange={(c) => setGenOpts({ ...genOpts, overwrite: c })} />
            </div>
            {selectablePaths.length > 0 && (
              <div className="space-y-2 rounded-xl border border-border/60 bg-surface/60 p-3">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <p className="text-xs font-medium text-foreground">Selective generate</p>
                    <p className="text-[11px] text-muted">Choose specific library roots to generate for, or leave them all selected.</p>
                  </div>
                  <button
                    type="button"
                    onClick={() => setGenOpts({ ...genOpts, paths: allGenPathsSelected ? [] : selectablePaths })}
                    className="text-[11px] text-accent hover:text-accent-hover"
                  >
                    {allGenPathsSelected ? "Clear" : "Select all"}
                  </button>
                </div>
                <div className="space-y-1.5">
                  {selectablePaths.map((path) => (
                    <CheckboxLabel
                      key={path}
                      label={path}
                      checked={genOpts.paths?.includes(path) ?? false}
                      onChange={(checked) => toggleGenPath(path, checked)}
                    />
                  ))}
                </div>
              </div>
            )}
          </div>
        </TaskCard>
      </div>
    </SectionCard>
  );
}

// ---- Data Management ----
function DataManagementSection({ refetchJobs }: { refetchJobs: () => void }) {
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
    includeScenes: true,
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
  const importMut = useMutation({
    mutationFn: () => metadata.import({ filePath: importFilePath, duplicateHandling: importOverwrite }),
    onSuccess: () => refetchJobs(),
  });
  const backupMut = useMutation({ mutationFn: () => database.backup(), onSuccess: () => refetchJobs() });
  const optimizeMut = useMutation({ mutationFn: () => database.optimize(), onSuccess: () => refetchJobs() });

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

  return (
    <SectionCard title="Data Management" description="Clean orphaned data, manage generated files, export, and database operations.">
      <div className="space-y-4">
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
            <CheckboxLabel label="Markers" checked={!!cleanGenOpts.markers} onChange={(c) => setCleanGenOpts({ ...cleanGenOpts, markers: c })} />
            <CheckboxLabel label="Image thumbnails" checked={!!cleanGenOpts.imageThumbnails} onChange={(c) => setCleanGenOpts({ ...cleanGenOpts, imageThumbnails: c })} />
            <CheckboxLabel label="Dry run" checked={!!cleanGenOpts.dryRun} onChange={(c) => setCleanGenOpts({ ...cleanGenOpts, dryRun: c })} />
          </div>
        </TaskCard>

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
            <CheckboxLabel label="Scenes" checked={!!exportOpts.includeScenes} onChange={(c) => setExportOpts({ ...exportOpts, includeScenes: c })} />
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

        {/* Database Operations */}
        <div className="grid gap-3 sm:grid-cols-2">
          <TaskCard
            label="Backup Database"
            description="Create a pg_dump backup of the PostgreSQL database."
            onRun={() => backupMut.mutate()}
            isPending={backupMut.isPending}
            statusMessage={backupStatus}
          />
          <TaskCard
            label="Optimise Database"
            description="Run VACUUM ANALYSE to reclaim space and update query planner statistics."
            onRun={() => optimizeMut.mutate()}
            isPending={optimizeMut.isPending}
            statusMessage={optimizeStatus}
          />
        </div>
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

  if (enabledWithTasks.length === 0) return null;

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
                    <h4 className="text-sm font-medium text-foreground">{task.name}</h4>
                    {task.description && <p className="text-xs text-secondary mt-0.5">{task.description}</p>}
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

// ---- Task Card (reusable) ----
function TaskCard({
  label,
  description,
  onRun,
  isPending,
  expandable,
  expanded,
  onToggleExpand,
  statusMessage,
  children,
}: {
  label: string;
  description: string;
  onRun: () => void;
  isPending: boolean;
  expandable?: boolean;
  expanded?: boolean;
  onToggleExpand?: () => void;
  statusMessage?: { type: "success" | "error"; text: string } | null;
  children?: React.ReactNode;
}) {
  return (
    <div className="rounded-xl border border-border bg-card p-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2 min-w-0 flex-1">
          {expandable && onToggleExpand && (
            <button onClick={onToggleExpand} className="text-muted hover:text-foreground flex-shrink-0">
              {expanded ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
            </button>
          )}
          <div>
            <h4 className="text-sm font-medium text-foreground">{label}</h4>
            <p className="text-xs text-secondary mt-0.5">{description}</p>
          </div>
        </div>
        <button
          onClick={onRun}
          disabled={isPending}
          className="inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60 flex-shrink-0 ml-3"
        >
          {isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <PlayCircle className="h-4 w-4" />}
          Run
        </button>
      </div>
      {statusMessage && (
        <p className={`text-xs mt-2 ${statusMessage.type === "success" ? "text-green-400" : "text-red-400"}`}>
          {statusMessage.text}
        </p>
      )}
      {/* Always show children (e.g. Clean dry-run checkbox), or show only when expanded */}
      {children && (!expandable || expanded) && (
        <div className="mt-3">{children}</div>
      )}
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

function ThemeSelector() {
  const {
    availableThemes, activeThemeId, setActiveTheme,
    availableComponentStyles, activeComponentStyles, toggleComponentStyle,
    availableLayoutStyles, activeLayoutStyle, setActiveLayoutStyle,
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

  // Style option configs stored in localStorage
  const [styleOptions, setStyleOptionsState] = useState<Record<string, Record<string, string>>>(() => {
    try {
      const raw = JSON.parse(localStorage.getItem("cove-style-options") ?? "{}");
      // Migrate old gradient settings to unified dropdowns
      if (raw.gradient) {
        const g = raw.gradient;
        if (g.animated === "on" && g.speed) { g.animated = g.speed; }
        else if (g.animated === "on") { g.animated = "medium"; }
        delete g.speed;
        if (g.cards === "on" && g.cardstrength) { g.cards = g.cardstrength; }
        else if (g.cards === "on") { g.cards = "medium"; }
        else if (g.cards === "off") { /* keep off */ }
        delete g.cardstrength;
        if (g.bgstrength && !g.background) { g.background = g.bgstrength; }
        delete g.bgstrength;
        raw.gradient = g;
      }
      // Migrate discrete string values to numeric (continuous sliders)
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
        for (const [key, val] of Object.entries(opts as Record<string, string>)) {
          const migVal = discreteToNumeric[styleId]?.[key]?.[val];
          if (migVal) {
            (raw as Record<string, Record<string, string>>)[styleId][key] = migVal;
            migrated = true;
          }
        }
      }
      if (migrated) {
        localStorage.setItem("cove-style-options", JSON.stringify(raw));
      }
      return raw;
    } catch { return {}; }
  });
  const setStyleOption = (styleId: string, optionKey: string, value: string) => {
    const updated = { ...styleOptions, [styleId]: { ...styleOptions[styleId], [optionKey]: value } };
    setStyleOptionsState(updated);
    localStorage.setItem("cove-style-options", JSON.stringify(updated));
    // Apply to document as data attribute for CSS targeting
    document.documentElement.dataset[`style${styleId.charAt(0).toUpperCase()}${styleId.slice(1)}${optionKey.charAt(0).toUpperCase()}${optionKey.slice(1)}`] = value;
    // Set CSS custom property for range-type configs
    const cfg = styleConfigs[styleId]?.find(c => c.key === optionKey);
    if (cfg && "cssVar" in cfg) {
      document.documentElement.style.setProperty(cfg.cssVar, value);
    }
  };

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
  }, []);

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
      { key: "carddir", label: "Card Direction", options: [{ value: "vertical", label: "Vertical" }, { value: "horizontal", label: "Horizontal" }, { value: "diagonal", label: "Diagonal" }] },
      { key: "bgdir", label: "Background Direction", options: [{ value: "vertical", label: "Vertical" }, { value: "horizontal", label: "Horizontal" }, { value: "diagonal", label: "Diagonal" }] },
      { key: "surfacedir", label: "Surface Direction", options: [{ value: "vertical", label: "Vertical" }, { value: "horizontal", label: "Horizontal" }, { value: "diagonal", label: "Diagonal" }] },
      { key: "scenepause", label: "Pause on Scene Player", options: [{ value: "on", label: "On (recommended)" }, { value: "off", label: "Off" }] },
    ],
    glass: [
      { key: "cardblur", label: "Card Blur", type: "range", cssVar: "--sv-card-blur", min: 0, max: 100, defaultValue: 27 },
      { key: "surfaceblur", label: "Surface Blur", type: "range", cssVar: "--sv-surface-blur", min: 0, max: 100, defaultValue: 50 },
      { key: "opacity", label: "Surface Opacity", type: "range", cssVar: "--sv-surface-opacity", min: 0, max: 100, defaultValue: 40 },
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
    <SectionCard title="Theme & Appearance" description="Customize colors, styles, layout, and effects.">
      <div className="space-y-3">
        {/* --- Color Palette --- */}
        <CollapsibleSection title="Color Palette" subtitle={activeThemeId ? availableThemes.find((t) => t.id === activeThemeId)?.name ?? activeThemeId : "Default"} expanded={expandedSections.has("palette")} onToggle={() => toggleSection("palette")}>
          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            {/* Extension themes */}
            {availableThemes.map((theme) => (
              <button
                key={theme.id}
                onClick={() => setActiveTheme(theme.id)}
                className={`rounded-xl border p-4 text-left transition-colors ${
                  activeThemeId === theme.id
                    ? "border-accent bg-accent/10"
                    : "border-border bg-card hover:border-accent/50"
                }`}
              >
                <div className="text-sm font-medium text-foreground">{theme.name}</div>
                {theme.description && (
                  <div className="text-xs text-secondary mt-1">{theme.description}</div>
                )}
                {theme.cssVariables && (
                  <div className="flex gap-1 mt-2">
                    {Object.entries(theme.cssVariables).slice(0, 3).map(([key, val]) => (
                      <div key={key} className="w-5 h-5 rounded border border-white/10" style={{ background: val }} />
                    ))}
                  </div>
                )}
              </button>
            ))}

            {/* Custom theme */}
            <div
              className={`rounded-xl border transition-colors ${
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
                  <div className="flex gap-1 mt-2">
                    <div className="w-5 h-5 rounded border border-white/10" style={{ background: "linear-gradient(135deg, #ff6b6b, #4ecdc4, #45b7d1)" }} />
                  </div>
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
                                  className="w-full h-1.5 accent-accent cursor-pointer"
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
              {availableLayoutStyles.map((layout) => (
                <button
                  key={layout.id}
                  onClick={() => setActiveLayoutStyle(layout.id)}
                  className={`rounded-xl border p-4 text-left transition-colors ${
                    activeLayoutStyle === layout.id
                      ? "border-accent bg-accent/10"
                      : "border-border bg-card hover:border-accent/50"
                  }`}
                >
                  <div className="text-sm font-medium text-foreground">{layout.name}</div>
                  {layout.description && (
                    <div className="text-xs text-secondary mt-1">{layout.description}</div>
                  )}
                </button>
              ))}
            </div>
          </CollapsibleSection>
        )}
      </div>
    </SectionCard>
  );
}

function CollapsibleSection({ title, subtitle, expanded, onToggle, children }: { title: string; subtitle?: string; expanded: boolean; onToggle: () => void; children: React.ReactNode }) {
  return (
    <div className="border border-border rounded-xl overflow-hidden">
      <button onClick={onToggle} className="w-full flex items-center justify-between px-4 py-3 bg-card hover:bg-card-hover transition-colors text-left">
        <div className="min-w-0">
          <span className="text-sm font-medium text-foreground">{title}</span>
          {subtitle && <span className="text-xs text-muted ml-2">({subtitle})</span>}
        </div>
        {expanded ? <ChevronUp className="w-4 h-4 text-muted shrink-0" /> : <ChevronDown className="w-4 h-4 text-muted shrink-0" />}
      </button>
      {expanded && <div className="px-4 py-3 border-t border-border">{children}</div>}
    </div>
  );
}

function SectionCard({ title, description, children }: { title: string; description: string; children: React.ReactNode }) {
  return (
    <section className="rounded-2xl border border-border bg-surface p-5 shadow-[0_12px_30px_-20px_rgba(0,0,0,0.7)]">
      <div className="mb-4">
        <h3 className="text-base font-semibold text-foreground">{title}</h3>
        <p className="mt-1 text-sm text-secondary">{description}</p>
      </div>
      {children}
    </section>
  );
}

function TextField({
  label,
  value,
  onChange,
  placeholder,
  type = "text",
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  type?: string;
}) {
  return (
    <label className="block text-sm">
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">{label}</span>
      <input
        type={type}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      />
    </label>
  );
}

function NumberField({
  label,
  value,
  onChange,
  min,
  max,
}: {
  label: string;
  value?: number;
  onChange: (value: number | undefined) => void;
  min?: number;
  max?: number;
}) {
  return (
    <label className="block text-sm">
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">{label}</span>
      <input
        type="number"
        value={value ?? ""}
        min={min}
        max={max}
        onChange={(event) => onChange(event.target.value ? Number(event.target.value) : undefined)}
        className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      />
    </label>
  );
}

function TextAreaField({
  label,
  value,
  onChange,
  rows,
  placeholder,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  rows: number;
  placeholder?: string;
}) {
  return (
    <label className="block text-sm">
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">{label}</span>
      <textarea
        value={value}
        onChange={(event) => onChange(event.target.value)}
        rows={rows}
        placeholder={placeholder}
        className="w-full rounded-xl border border-border bg-card px-3 py-2 font-mono text-sm text-foreground focus:border-accent focus:outline-none"
      />
    </label>
  );
}

function SelectField({
  label,
  value,
  onChange,
  options,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  options: { value: string; label: string }[];
}) {
  return (
    <label className="block text-sm">
      <span className="mb-1 block text-xs font-medium uppercase tracking-wide text-muted">{label}</span>
      <select
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    </label>
  );
}

function CheckboxLabel({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <label className="flex items-center gap-2 text-sm text-secondary">
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="h-4 w-4 rounded border-border bg-card text-accent focus:ring-0"
      />
      <span>{label}</span>
    </label>
  );
}

function InfoPair({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-border bg-card p-3">
      <dt className="text-xs font-medium uppercase tracking-wide text-muted">{label}</dt>
      <dd className="mt-1 break-all text-sm text-foreground">{value}</dd>
    </div>
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

// ===== DLNA Panel =====
function DlnaPanel() {
  const queryClient = useQueryClient();
  const [newIp, setNewIp] = useState("");

  const { data: status, isLoading } = useQuery({
    queryKey: ["dlna-status"],
    queryFn: () => dlnaApi.status(),
  });

  const enableMut = useMutation({
    mutationFn: (durationMinutes?: number) => dlnaApi.enable(durationMinutes),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["dlna-status"] }),
  });

  const disableMut = useMutation({
    mutationFn: () => dlnaApi.disable(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["dlna-status"] }),
  });

  const allowIpMut = useMutation({
    mutationFn: (ip: string) => dlnaApi.allowIp(ip),
    onSuccess: () => { setNewIp(""); queryClient.invalidateQueries({ queryKey: ["dlna-status"] }); },
  });

  const removeIpMut = useMutation({
    mutationFn: (ip: string) => dlnaApi.removeIp(ip),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["dlna-status"] }),
  });

  if (isLoading || !status) {
    return (
      <SectionCard title="DLNA Server" description="Loading...">
        <Loader2 className="w-5 h-5 animate-spin text-muted" />
      </SectionCard>
    );
  }

  return (
    <>
      <SectionCard title="DLNA Server" description="Enable or disable the DLNA media server for streaming to compatible devices.">
        <div className="space-y-4">
          <div className="flex items-center gap-4">
            <div className={`w-3 h-3 rounded-full ${status.running ? "bg-green-500" : "bg-gray-500"}`} />
            <span className="text-sm font-medium text-foreground">{status.running ? "Running" : "Stopped"}</span>
            {status.untilDisabled && (
              <span className="text-xs text-muted">
                (enabled until {new Date(status.untilDisabled).toLocaleTimeString()})
              </span>
            )}
          </div>
          <div className="flex flex-wrap gap-2">
            {!status.running ? (
              <>
                <button onClick={() => enableMut.mutate(undefined)} className="px-3 py-1.5 text-sm bg-green-600 hover:bg-green-500 text-white rounded">Enable</button>
                <button onClick={() => enableMut.mutate(120)} className="px-3 py-1.5 text-sm bg-card border border-border text-secondary hover:text-foreground rounded">Enable for 2 hours</button>
                <button onClick={() => enableMut.mutate(1440)} className="px-3 py-1.5 text-sm bg-card border border-border text-secondary hover:text-foreground rounded">Enable for 24 hours</button>
              </>
            ) : (
              <button onClick={() => disableMut.mutate()} className="px-3 py-1.5 text-sm bg-red-600 hover:bg-red-500 text-white rounded">Disable</button>
            )}
          </div>
        </div>
      </SectionCard>

      <SectionCard title="Allowed IP Addresses" description="Configure which IP addresses are allowed to access the DLNA server.">
        <div className="space-y-3">
          <div className="flex gap-2">
            <input
              type="text"
              value={newIp}
              onChange={(e) => setNewIp(e.target.value)}
              placeholder="IP address (e.g. 192.168.1.100)"
              className="flex-1 bg-input border border-border rounded px-3 py-1.5 text-sm text-foreground"
              onKeyDown={(e) => { if (e.key === "Enter" && newIp.trim()) allowIpMut.mutate(newIp.trim()); }}
            />
            <button
              onClick={() => newIp.trim() && allowIpMut.mutate(newIp.trim())}
              className="px-3 py-1.5 text-sm bg-accent hover:bg-accent-hover text-white rounded flex items-center gap-1"
            >
              <Plus className="w-3.5 h-3.5" /> Add
            </button>
          </div>
          {status.allowedIps.length === 0 ? (
            <p className="text-xs text-muted">No IP addresses allowed. All devices can connect when the server is running.</p>
          ) : (
            <div className="space-y-1">
              {status.allowedIps.map((ip) => (
                <div key={ip} className="flex items-center justify-between bg-card border border-border rounded px-3 py-2 text-sm">
                  <span className="text-foreground font-mono">{ip}</span>
                  <button onClick={() => removeIpMut.mutate(ip)} className="text-muted hover:text-red-400"><Trash2 className="w-3.5 h-3.5" /></button>
                </div>
              ))}
            </div>
          )}
        </div>
      </SectionCard>

      {status.recentIps.length > 0 && (
        <SectionCard title="Recent Connections" description="IP addresses that have recently connected to the DLNA server.">
          <div className="space-y-1">
            {status.recentIps.map((ip) => (
              <div key={ip} className="flex items-center justify-between bg-card border border-border rounded px-3 py-2 text-sm">
                <span className="text-foreground font-mono">{ip}</span>
                <button
                  onClick={() => allowIpMut.mutate(ip)}
                  className="text-xs text-accent hover:underline"
                >
                  Allow
                </button>
              </div>
            ))}
          </div>
        </SectionCard>
      )}
    </>
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
                className="flex-1 bg-card border border-border rounded px-2 py-1 text-sm focus:border-accent outline-none"
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
function ExtensionsPanel() {
  const { availableThemes, activeThemeId, setActiveTheme, settingsPanels, resolveComponent } = useExtensions();
  const queryClient = useQueryClient();
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [categoryFilter, setCategoryFilter] = useState<string>("all");
  const [searchQuery, setSearchQuery] = useState("");

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

  const runJobMut = useMutation({
    mutationFn: (args: { id: string; jobId: string }) =>
      import("../api/client").then(m => m.extensions.runJob(args.id, args.jobId)),
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
    categories: string[];
    source: "native" | "legacy";
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
        categories: ext.categories,
        source: "native",
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
        categories: [],
        source: "legacy",
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
      {/* Installed Extensions */}
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
                      {ext.source === "legacy" && <> · <span className="text-yellow-500">Python extension</span></>}
                    </div>

                    {/* Capability badges */}
                    <div className="flex gap-1.5 flex-wrap">
                      {ext.hasUI && <ExtBadge label="UI" />}
                      {ext.hasApi && <ExtBadge label="API" />}
                      {ext.hasState && <ExtBadge label="Stateful" />}
                      {ext.hasJobs && <ExtBadge label="Jobs" />}
                      {ext.hasEvents && <ExtBadge label="Events" />}
                    </div>

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

      {/* Extension-contributed settings panels */}
      {settingsPanels.length > 0 &&
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
      <FindAndInstallExtensions />
    </>
  );
}

// ===== Find and Install Extensions =====
function FindAndInstallExtensions() {
  const queryClient = useQueryClient();
  const [searchQuery, setSearchQuery] = useState("");
  const [category, setCategory] = useState<string>("");
  const [selectedExtension, setSelectedExtension] = useState<import("../api/types").RegistryExtensionDetail | null>(null);

  const { data: searchResults, isLoading: searching, refetch: doSearch } = useQuery({
    queryKey: ["registry-search", searchQuery, category],
    queryFn: () => import("../api/client").then(m =>
      m.extensions.registrySearch({ q: searchQuery || undefined, category: category || undefined, pageSize: 50 })
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
    mutationFn: (args: { extensionId: string; version: string }) =>
      import("../api/client").then(m => m.extensions.registryInstall(args.extensionId, args.version)),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["extensions-list"] });
      queryClient.invalidateQueries({ queryKey: ["registry-search"] });
      queryClient.invalidateQueries({ queryKey: ["registry-updates"] });
    },
  });

  const uninstallMut = useMutation({
    mutationFn: (extensionId: string) =>
      import("../api/client").then(m => m.extensions.registryUninstall(extensionId)),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["extensions-list"] });
      queryClient.invalidateQueries({ queryKey: ["registry-search"] });
    },
  });

  const installedIds = new Set((installedList ?? []).map(e => e.id));
  const updateMap = new Map((updates ?? []).map(u => [u.extensionId, u]));

  const viewDetail = async (id: string) => {
    const detail = await import("../api/client").then(m => m.extensions.registryGetExtension(id));
    setSelectedExtension(detail);
  };

  return (
    <SectionCard title="Find and Install Extensions" description="Browse and install extensions from the official Cove extension registry.">
      {/* Updates banner */}
      {updates && updates.length > 0 && (
        <div className="mb-4 p-3 bg-yellow-600/10 border border-yellow-600/30 rounded-lg">
          <div className="text-sm font-medium text-yellow-400 mb-1">Updates Available</div>
          <div className="space-y-1">
            {updates.map(u => (
              <div key={u.extensionId} className="flex items-center justify-between text-xs">
                <span className="text-secondary">{u.extensionId}: v{u.currentVersion} → v{u.latestVersion}</span>
                <button
                  onClick={() => installMut.mutate({ extensionId: u.extensionId, version: u.latestVersion })}
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

      {/* Search and filter */}
      <div className="flex items-center gap-3 mb-4">
        <div className="relative flex-1">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-muted" />
          <input
            type="text"
            placeholder="Search the extension registry..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="w-full pl-8 pr-3 py-1.5 text-sm bg-card border border-border rounded focus:outline-none focus:border-accent"
          />
        </div>
        {registryCategories && registryCategories.length > 0 && (
          <select
            value={category}
            onChange={(e) => setCategory(e.target.value)}
            className="px-3 py-1.5 text-sm bg-card border border-border rounded focus:outline-none focus:border-accent"
          >
            <option value="">All Categories</option>
            {registryCategories.map(c => (
              <option key={c} value={c}>{c}</option>
            ))}
          </select>
        )}
      </div>

      {/* Extension detail modal */}
      {selectedExtension && (
        <div className="mb-4 p-4 bg-surface rounded-lg border border-border">
          <div className="flex items-start justify-between mb-3">
            <div>
              <h3 className="text-lg font-semibold">{selectedExtension.name}</h3>
              <div className="text-xs text-muted mt-0.5">
                v{selectedExtension.version}
                {selectedExtension.author && <> · by {selectedExtension.author}</>}
              </div>
            </div>
            <button
              onClick={() => setSelectedExtension(null)}
              className="text-secondary hover:text-foreground"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
          {selectedExtension.description && (
            <p className="text-sm text-secondary mb-3">{selectedExtension.description}</p>
          )}
          {selectedExtension.categories.length > 0 && (
            <div className="flex gap-1 mb-3 flex-wrap">
              {selectedExtension.categories.map(c => (
                <span key={c} className="text-[10px] px-1.5 py-0.5 rounded bg-surface text-secondary border border-border/50">{c}</span>
              ))}
            </div>
          )}
          {selectedExtension.readme && (
            <div className="text-xs text-secondary bg-card rounded p-3 mb-3 max-h-48 overflow-y-auto whitespace-pre-wrap">
              {selectedExtension.readme}
            </div>
          )}
          <div className="flex gap-2">
            {!installedIds.has(selectedExtension.id) ? (
              <button
                onClick={() => installMut.mutate({ extensionId: selectedExtension.id, version: selectedExtension.version })}
                disabled={installMut.isPending}
                className="px-4 py-1.5 text-sm bg-accent hover:bg-accent-hover text-white rounded disabled:opacity-50 flex items-center gap-1.5"
              >
                {installMut.isPending ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Download className="w-3.5 h-3.5" />}
                Install v{selectedExtension.version}
              </button>
            ) : (
              <button
                onClick={() => uninstallMut.mutate(selectedExtension.id)}
                disabled={uninstallMut.isPending}
                className="px-4 py-1.5 text-sm bg-card border border-border text-muted hover:text-red-400 hover:border-red-500 rounded disabled:opacity-50 flex items-center gap-1.5"
              >
                {uninstallMut.isPending ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Trash2 className="w-3.5 h-3.5" />}
                Uninstall
              </button>
            )}
          </div>
        </div>
      )}

      {/* Results grid */}
      {searching ? (
        <div className="flex items-center justify-center py-8">
          <Loader2 className="w-5 h-5 animate-spin text-muted" />
        </div>
      ) : !searchResults || searchResults.items.length === 0 ? (
        <div className="text-sm text-muted text-center py-6">
          {searchQuery ? "No extensions found matching your search." : "No extensions available in the registry yet."}
        </div>
      ) : (
        <div className="space-y-2">
          {searchResults.items.map((ext) => {
            const isInstalled = installedIds.has(ext.id);
            const update = updateMap.get(ext.id);
            return (
              <div
                key={ext.id}
                className="flex items-center justify-between bg-card border border-border rounded-xl px-4 py-3 cursor-pointer hover:bg-card-hover/30 transition-colors"
                onClick={() => viewDetail(ext.id)}
              >
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2 flex-wrap">
                    <span className="text-sm font-medium text-foreground">{ext.name}</span>
                    <span className="text-xs text-muted">v{ext.version}</span>
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
                <div className="flex gap-2 ml-3 flex-shrink-0">
                  {!isInstalled ? (
                    <button
                      onClick={(e) => { e.stopPropagation(); installMut.mutate({ extensionId: ext.id, version: ext.version }); }}
                      disabled={installMut.isPending}
                      className="px-3 py-1.5 text-xs bg-accent hover:bg-accent-hover text-white rounded disabled:opacity-50 flex items-center gap-1"
                    >
                      {installMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <Download className="w-3 h-3" />}
                      Install
                    </button>
                  ) : update ? (
                    <button
                      onClick={(e) => { e.stopPropagation(); installMut.mutate({ extensionId: ext.id, version: update.latestVersion }); }}
                      disabled={installMut.isPending}
                      className="px-3 py-1.5 text-xs bg-yellow-600 hover:bg-yellow-500 text-white rounded disabled:opacity-50 flex items-center gap-1"
                    >
                      {installMut.isPending ? <Loader2 className="w-3 h-3 animate-spin" /> : <RefreshCw className="w-3 h-3" />}
                      Update
                    </button>
                  ) : (
                    <button
                      onClick={(e) => { e.stopPropagation(); uninstallMut.mutate(ext.id); }}
                      disabled={uninstallMut.isPending}
                      className="px-3 py-1.5 text-xs bg-card border border-border text-muted hover:text-red-400 hover:border-red-500 rounded disabled:opacity-50 flex items-center gap-1"
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
