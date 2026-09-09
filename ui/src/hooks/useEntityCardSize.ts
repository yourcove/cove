import { useCallback, useEffect, useMemo, useState } from "react";

const MIN_CARD_SIZE_LEVEL = 0;
const MAX_CARD_SIZE_LEVEL = 8;
const DEFAULT_CARD_SIZE_LEVEL = 1;

interface EntityCardSizeProfile {
  baseWidthPx: number;
  stepPx: number;
  maxLevel?: number;
}

const GLOBAL_CARD_SIZE_PROFILE: EntityCardSizeProfile = { baseWidthPx: 225, stepPx: 50, maxLevel: MAX_CARD_SIZE_LEVEL };
const DEFAULT_CARD_SIZE_PROFILE: EntityCardSizeProfile = GLOBAL_CARD_SIZE_PROFILE;

const ENTITY_CARD_SIZE_PROFILES: Record<string, EntityCardSizeProfile> = {
  video: GLOBAL_CARD_SIZE_PROFILE,
  image: GLOBAL_CARD_SIZE_PROFILE,
  gallery: GLOBAL_CARD_SIZE_PROFILE,
  performer: GLOBAL_CARD_SIZE_PROFILE,
  studio: GLOBAL_CARD_SIZE_PROFILE,
  tag: GLOBAL_CARD_SIZE_PROFILE,
  group: GLOBAL_CARD_SIZE_PROFILE,
  audio: GLOBAL_CARD_SIZE_PROFILE,
  text: GLOBAL_CARD_SIZE_PROFILE,
  face: GLOBAL_CARD_SIZE_PROFILE,
  segment: GLOBAL_CARD_SIZE_PROFILE,
};

const ENTITY_ALIASES: Record<string, string> = {
  videos: "video",
  video: "video",
  images: "image",
  image: "image",
  galleries: "gallery",
  gallery: "gallery",
  performers: "performer",
  performer: "performer",
  studios: "studio",
  studio: "studio",
  tags: "tag",
  tag: "tag",
  groups: "group",
  group: "group",
  audios: "audio",
  audio: "audio",
  texts: "text",
  text: "text",
  faces: "face",
  face: "face",
  segments: "segment",
  segment: "segment",
};

export function clampCardSizeLevel(value: number) {
  return Math.min(MAX_CARD_SIZE_LEVEL, Math.max(MIN_CARD_SIZE_LEVEL, value));
}

function getEntityCardSizeProfile(entityType?: string) {
  const normalized = normalizeEntityType(entityType);
  return normalized ? (ENTITY_CARD_SIZE_PROFILES[normalized] ?? DEFAULT_CARD_SIZE_PROFILE) : DEFAULT_CARD_SIZE_PROFILE;
}

export function getEntityCardMaxLevel(entityType?: string) {
  return getEntityCardSizeProfile(entityType).maxLevel ?? MAX_CARD_SIZE_LEVEL;
}

export function clampEntityCardSizeLevel(entityType: string | undefined, value: number) {
  return Math.min(getEntityCardMaxLevel(entityType), Math.max(MIN_CARD_SIZE_LEVEL, value));
}

export function parseEntityCardSizeLevel(entityType: string | undefined, value: unknown) {
  return typeof value === "number" && Number.isFinite(value) ? clampEntityCardSizeLevel(entityType, value) : undefined;
}

export function getEntityCardMinWidthPx(entityType: string | undefined, level: number) {
  const profile = getEntityCardSizeProfile(entityType);
  return Math.round(profile.baseWidthPx + clampEntityCardSizeLevel(entityType, level) * profile.stepPx);
}

function normalizeEntityType(entityType?: string) {
  if (!entityType) return undefined;
  const normalized = entityType.trim().toLowerCase();
  return ENTITY_ALIASES[normalized] ?? normalized.replace(/[^a-z0-9_.-]/g, "-");
}

function readLegacyZoomLevel(legacyPageKey?: string) {
  if (!legacyPageKey) return undefined;
  try {
    const raw = localStorage.getItem(`cove-list-prefs-${legacyPageKey}`);
    if (!raw) return undefined;
    const parsed = JSON.parse(raw) as { zoomLevel?: number };
    return typeof parsed.zoomLevel === "number" ? Math.max(MIN_CARD_SIZE_LEVEL, parsed.zoomLevel) : undefined;
  } catch {
    return undefined;
  }
}

function readEntityCardSize(entityType?: string, legacyPageKey?: string, defaultValue = DEFAULT_CARD_SIZE_LEVEL) {
  const normalized = normalizeEntityType(entityType);
  if (typeof window === "undefined" || !normalized) {
    return clampEntityCardSizeLevel(entityType, defaultValue);
  }

  try {
    const key = `cove.cardSize.${normalized}`;
    const raw = localStorage.getItem(key);
    if (raw != null) {
      const parsed = Number(raw);
      return Number.isFinite(parsed)
        ? clampEntityCardSizeLevel(normalized, parsed)
        : clampEntityCardSizeLevel(normalized, defaultValue);
    }

    const legacyValue = readLegacyZoomLevel(legacyPageKey);
    if (legacyValue != null) {
      localStorage.setItem(key, String(legacyValue));
      return clampEntityCardSizeLevel(normalized, legacyValue);
    }
  } catch {
    return clampEntityCardSizeLevel(normalized, defaultValue);
  }

  return clampEntityCardSizeLevel(normalized, defaultValue);
}

export function useEntityCardSize(entityType?: string, legacyPageKey?: string, defaultValue = DEFAULT_CARD_SIZE_LEVEL) {
  const normalizedEntityType = useMemo(() => normalizeEntityType(entityType), [entityType]);
  const [level, setLevelState] = useState(() => readEntityCardSize(normalizedEntityType, legacyPageKey, defaultValue));

  useEffect(() => {
    setLevelState(readEntityCardSize(normalizedEntityType, legacyPageKey, defaultValue));
  }, [defaultValue, legacyPageKey, normalizedEntityType]);

  const setLevel = useCallback(
    (value: number | ((current: number) => number)) => {
      setLevelState((current) => {
        const nextValue = typeof value === "function" ? value(current) : value;
        const next = clampEntityCardSizeLevel(normalizedEntityType, nextValue);
        if (typeof window !== "undefined" && normalizedEntityType) {
          try {
            localStorage.setItem(`cove.cardSize.${normalizedEntityType}`, String(next));
          } catch {
            // Ignore storage write failures.
          }
        }
        return next;
      });
    },
    [normalizedEntityType],
  );

  return [level, setLevel] as const;
}
