import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { system } from "../api/client";
import { authStore, hasPermission } from "../auth/authStore";
import type { CoveConfig, SystemStatus } from "../api/types";
import { normalizeShortcutSequence } from "../keyboard/keybindings";

const defaultMenuItems = ["videos", "audios", "texts", "images", "faces", "performers", "galleries", "studios", "tags", "groups"];
const defaultIdentifyDefaults = {
  createTags: true,
  createPerformers: true,
  createStudios: true,
};

const defaultScrapeApplyDefaults = {
  createMissingTags: false,
  createMissingPerformers: false,
  createMissingStudio: false,
  markOrganized: false,
  hydratePerformers: false,
};

function normalizeRatingSystemType(value: string | undefined) {
  return value?.toLowerCase() === "decimal" ? "decimal" : "stars";
}

function normalizeRatingStarPrecision(value: string | undefined) {
  switch (value?.toLowerCase()) {
    case "half":
      return "half";
    case "quarter":
      return "quarter";
    case "tenth":
      return "tenth";
    default:
      return "full";
  }
}

function normalizeKeybindingOverrides(overrides: Record<string, string> | null | undefined) {
  if (!overrides) {
    return {};
  }

  return Object.fromEntries(
    Object.entries(overrides)
      .map(([key, value]) => [key.trim(), normalizeShortcutSequence(value)] as const)
      .filter(([key, value]) => key.length > 0 && value.length > 0),
  );
}

function normalizeWallPreviewType(value: string | undefined) {
  return value === "image" ? "image" : "video";
}

function normalizeFeedVideoSource(value: string | undefined) {
  return value === "video" ? "video" : "preview";
}

function normalizeObjectFit(value: string | undefined) {
  return value === "contain" ? "contain" : "cover";
}

function clampNumber(value: number | undefined, min: number, max: number) {
  if (typeof value !== "number" || Number.isNaN(value)) {
    return min;
  }

  return Math.min(max, Math.max(min, value));
}

function normalizeConfig(config: CoveConfig, userKeybindingOverrides?: Record<string, string> | null): CoveConfig {
  const interfaceConfig = config.interface;
  const uiConfig = config.ui ?? ({} as CoveConfig["ui"]);
  const ratingOptions = uiConfig.ratingSystemOptions ?? { type: "stars", starPrecision: "full" };
  const identifyDefaults = config.scraping.identifyDefaults ?? defaultIdentifyDefaults;
  const scrapeApplyDefaults = config.scraping.scrapeApplyDefaults ?? defaultScrapeApplyDefaults;

  return {
    ...config,
    downloaderPathOverrides: (config.downloaderPathOverrides ?? []).map((overridePath) => ({
      downloaderId: overridePath.downloaderId?.trim() ?? "",
      site: overridePath.site?.trim() || undefined,
      path: overridePath.path?.trim() ?? "",
    })),
    interface: {
      ...interfaceConfig,
      menuItems: interfaceConfig.menuItems.length > 0 ? interfaceConfig.menuItems : defaultMenuItems,
    },
    ui: {
      ...uiConfig,
      troubleshootingModeEnabled: uiConfig.troubleshootingModeEnabled ?? false,
      autoplayOnListClick: uiConfig.autoplayOnListClick ?? false,
      maxLoopDuration: uiConfig.maxLoopDuration ?? 0,
      alwaysResumeOnPlayback: uiConfig.alwaysResumeOnPlayback ?? true,
      playerVideoStartPercent: clampNumber(uiConfig.playerVideoStartPercent, 0, 95),
      playerVideoStartMinDuration: Math.max(0, uiConfig.playerVideoStartMinDuration ?? 0),
      wallPreviewType: normalizeWallPreviewType(uiConfig.wallPreviewType),
      imageObjectFit: normalizeObjectFit(uiConfig.imageObjectFit),
      videoObjectFit: normalizeObjectFit(uiConfig.videoObjectFit),
      feedVideoSource: normalizeFeedVideoSource(uiConfig.feedVideoSource),
      feedVideoSound: uiConfig.feedVideoSound ?? false,
      feedVideoStartPercent: clampNumber(uiConfig.feedVideoStartPercent, 0, 95),
      feedVideoStartMinDuration: Math.max(0, uiConfig.feedVideoStartMinDuration ?? 0),
      keybindingOverrides: normalizeKeybindingOverrides(userKeybindingOverrides),
      ratingSystemOptions: {
        type: normalizeRatingSystemType(ratingOptions.type),
        starPrecision: normalizeRatingStarPrecision(ratingOptions.starPrecision),
      },
    },
    scraping: {
      ...config.scraping,
      metadataServers: config.scraping.metadataServers ?? [],
      scraperPreferences: (config.scraping.scraperPreferences ?? [])
        .map((preference) => ({
          entityType: preference.entityType?.trim().toLowerCase() || undefined,
          site: preference.site?.trim().toLowerCase() ?? "",
          scraperId: preference.scraperId?.trim() ?? "",
        }))
        .filter((preference, index, items) => {
          if (preference.site === "" || preference.scraperId === "") {
            return false;
          }

          return items.findIndex((candidate) => (candidate.entityType ?? "") === (preference.entityType ?? "") && candidate.site === preference.site) === index;
        }),
      identifyDefaults: {
        createTags: identifyDefaults.createTags ?? true,
        createPerformers: identifyDefaults.createPerformers ?? true,
        createStudios: identifyDefaults.createStudios ?? true,
        autoApplyMaxDurationDifferenceSeconds: identifyDefaults.autoApplyMaxDurationDifferenceSeconds,
        autoApplyMaxPhashDistance: identifyDefaults.autoApplyMaxPhashDistance,
      },
      scrapeApplyDefaults: {
        createMissingTags: scrapeApplyDefaults.createMissingTags ?? false,
        createMissingPerformers: scrapeApplyDefaults.createMissingPerformers ?? false,
        createMissingStudio: scrapeApplyDefaults.createMissingStudio ?? false,
        markOrganized: scrapeApplyDefaults.markOrganized ?? false,
        hydratePerformers: scrapeApplyDefaults.hydratePerformers ?? false,
      },
    },
  };
}

interface AppConfigContextValue {
  config?: CoveConfig;
  status?: SystemStatus;
  configLoading: boolean;
  statusLoading: boolean;
}

const AppConfigContext = createContext<AppConfigContextValue | null>(null);

export function AppConfigProvider({ children }: { children: ReactNode }) {
  const [authUser, setAuthUser] = useState(() => authStore.getUser());

  useEffect(() => authStore.subscribe(() => setAuthUser(authStore.getUser())), []);

  const statusQuery = useQuery({
    queryKey: ["system-status"],
    queryFn: system.status,
  });

  const canReadSystemConfig = !!statusQuery.data
    && (!statusQuery.data.authEnabled || hasPermission(authUser?.permissions, "system.read", authUser?.readGrantedEntityKinds));

  const configQuery = useQuery({
    queryKey: ["system-config", statusQuery.data?.authEnabled ? authUser?.id ?? "anonymous" : "auth-disabled"],
    queryFn: system.getConfig,
    enabled: canReadSystemConfig,
    retry: false,
  });

  const config = useMemo(() => {
    if (!configQuery.data) {
      return undefined;
    }

    return normalizeConfig(configQuery.data, authUser?.uiPreferences?.keybindingOverrides);
  }, [authUser?.uiPreferences?.keybindingOverrides, configQuery.data]);
  useEffect(() => {
    const appTitle = config?.ui.title?.trim() || "Cove";
    const pageTitle = document.body.dataset.covePageTitle?.trim();
    document.title = pageTitle ? `${pageTitle} | ${appTitle}` : appTitle;
  }, [config?.ui.title]);

  useEffect(() => {
    let link = document.querySelector<HTMLLinkElement>('link[rel="icon"]');
    if (!link) {
      link = document.createElement("link");
      link.rel = "icon";
      document.head.appendChild(link);
    }

    link.href = config?.ui.faviconPath?.trim() || "/favicon.svg";
  }, [config?.ui.faviconPath]);

  useEffect(() => {
    document.documentElement.lang = config?.interface.language || "en-US";
  }, [config?.interface.language]);

  useEffect(() => {
    const existing = document.getElementById("cove-custom-css");
    if (existing) existing.remove();

    const customCss = config?.ui.customCss?.trim();
    if (config?.ui.troubleshootingModeEnabled || !customCss) {
      return;
    }

    const style = document.createElement("style");
    style.id = "cove-custom-css";
    style.textContent = customCss;
    document.head.appendChild(style);

    return () => { style.remove(); };
  }, [config?.ui.customCss, config?.ui.troubleshootingModeEnabled]);

  useEffect(() => {
    const existing = document.getElementById("cove-custom-js");
    if (existing) existing.remove();

    const customJs = config?.ui.customJs?.trim();
    if (config?.ui.troubleshootingModeEnabled || !customJs) {
      return;
    }

    const script = document.createElement("script");
    script.id = "cove-custom-js";
    script.textContent = customJs;
    document.body.appendChild(script);

    return () => { script.remove(); };
  }, [config?.ui.customJs, config?.ui.troubleshootingModeEnabled]);

  return (
    <AppConfigContext.Provider
      value={{
        config,
        status: statusQuery.data,
        configLoading: configQuery.isLoading,
        statusLoading: statusQuery.isLoading,
      }}
    >
      {config?.ui.troubleshootingModeEnabled ? (
        <div className="sticky top-0 z-[60] border-b border-yellow-500/40 bg-yellow-500/15 px-4 py-2 text-center text-xs font-medium text-yellow-100">
          Troubleshooting mode is enabled. Extensions and custom UI assets should be treated as disabled while diagnosing issues.
        </div>
      ) : null}
      {children}
    </AppConfigContext.Provider>
  );
}

export function useAppConfig() {
  const context = useContext(AppConfigContext);
  if (!context) {
    throw new Error("useAppConfig must be used within an AppConfigProvider");
  }

  return context;
}

export function useOptionalAppConfig() {
  return useContext(AppConfigContext);
}

