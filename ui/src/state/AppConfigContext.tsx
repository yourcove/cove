import { createContext, useContext, useEffect, useMemo, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { system } from "../api/client";
import type { CoveConfig, SystemStatus } from "../api/types";

const defaultMenuItems = ["scenes", "images", "markers", "performers", "galleries", "studios", "tags", "groups"];
const defaultIdentifyDefaults = {
  createTags: true,
  createPerformers: true,
  createStudios: true,
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

function normalizeConfig(config: CoveConfig): CoveConfig {
  const interfaceConfig = config.interface;
  const uiConfig = config.ui ?? ({} as CoveConfig["ui"]);
  const ratingOptions = uiConfig.ratingSystemOptions ?? { type: "stars", starPrecision: "full" };
  const identifyDefaults = config.scraping.identifyDefaults ?? defaultIdentifyDefaults;

  return {
    ...config,
    interface: {
      ...interfaceConfig,
      menuItems: interfaceConfig.menuItems.length > 0 ? interfaceConfig.menuItems : defaultMenuItems,
    },
    ui: {
      ...uiConfig,
      ratingSystemOptions: {
        type: normalizeRatingSystemType(ratingOptions.type),
        starPrecision: normalizeRatingStarPrecision(ratingOptions.starPrecision),
      },
    },
    scraping: {
      ...config.scraping,
      metadataServers: config.scraping.metadataServers ?? [],
      identifyDefaults: {
        createTags: identifyDefaults.createTags ?? true,
        createPerformers: identifyDefaults.createPerformers ?? true,
        createStudios: identifyDefaults.createStudios ?? true,
        autoApplyMaxDurationDifferenceSeconds: identifyDefaults.autoApplyMaxDurationDifferenceSeconds,
        autoApplyMaxPhashDistance: identifyDefaults.autoApplyMaxPhashDistance,
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
  const configQuery = useQuery({
    queryKey: ["system-config"],
    queryFn: system.getConfig,
  });

  const config = useMemo(() => {
    if (!configQuery.data) {
      return undefined;
    }

    return normalizeConfig(configQuery.data);
  }, [configQuery.data]);

  const statusQuery = useQuery({
    queryKey: ["system-status"],
    queryFn: system.status,
  });

  useEffect(() => {
    document.title = config?.ui.title?.trim() || "Cove";
  }, [config?.ui.title]);

  useEffect(() => {
    document.documentElement.lang = config?.interface.language || "en-US";
  }, [config?.interface.language]);

  return (
    <AppConfigContext.Provider
      value={{
        config,
        status: statusQuery.data,
        configLoading: configQuery.isLoading,
        statusLoading: statusQuery.isLoading,
      }}
    >
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