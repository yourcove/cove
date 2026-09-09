import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../auth/AuthContext";
import type { DownloadEntityName, UrlDownloadMode } from "../utils/createFromUrlDownload";

interface FileBackedCreatePreferences {
  urlDownloadMode: UrlDownloadMode;
  scrapeMetadata: boolean;
}

const DEFAULT_PREFERENCES: FileBackedCreatePreferences = {
  urlDownloadMode: "now",
  scrapeMetadata: true,
};

function readPreferences(storageKey: string): FileBackedCreatePreferences {
  try {
    const raw = window.localStorage.getItem(storageKey);
    if (!raw) return DEFAULT_PREFERENCES;
    const parsed = JSON.parse(raw) as Partial<FileBackedCreatePreferences>;
    return {
      urlDownloadMode: parsed.urlDownloadMode === "later" ? "later" : "now",
      scrapeMetadata:
        typeof parsed.scrapeMetadata === "boolean" ? parsed.scrapeMetadata : DEFAULT_PREFERENCES.scrapeMetadata,
    };
  } catch {
    return DEFAULT_PREFERENCES;
  }
}

function writePreferences(storageKey: string, preferences: FileBackedCreatePreferences) {
  try {
    window.localStorage.setItem(storageKey, JSON.stringify(preferences));
  } catch {
    // Ignore storage failures; the in-memory state still updates for this session.
  }
}

export function useFileBackedCreatePreferences(entity: DownloadEntityName) {
  const { user } = useAuth();
  const userKey = user?.id ? `user-${user.id}` : "anonymous";
  const storageKey = `cove:file-backed-create:${userKey}:${entity.toLowerCase()}`;
  const [preferences, setPreferences] = useState<FileBackedCreatePreferences>(() => readPreferences(storageKey));

  useEffect(() => {
    setPreferences(readPreferences(storageKey));
  }, [storageKey]);

  const updatePreferences = useCallback(
    (updater: (current: FileBackedCreatePreferences) => FileBackedCreatePreferences) => {
      setPreferences((current) => {
        const next = updater(current);
        writePreferences(storageKey, next);
        return next;
      });
    },
    [storageKey],
  );

  const setUrlDownloadMode = useCallback(
    (urlDownloadMode: UrlDownloadMode) => {
      updatePreferences((current) => ({ ...current, urlDownloadMode }));
    },
    [updatePreferences],
  );

  const setScrapeMetadata = useCallback(
    (scrapeMetadata: boolean) => {
      updatePreferences((current) => ({ ...current, scrapeMetadata }));
    },
    [updatePreferences],
  );

  return {
    urlDownloadMode: preferences.urlDownloadMode,
    setUrlDownloadMode,
    scrapeMetadata: preferences.scrapeMetadata,
    setScrapeMetadata,
  };
}
