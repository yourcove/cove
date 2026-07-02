import { auth } from "../api/client";
import { authStore } from "../auth/authStore";
import type { AuthUser } from "../auth/authStore";
import type { RatingSystemOptions, UserPlaybackPreferences, UserThemePreferences, UserTrackingPreferences, UserUiPreferences } from "../api/types";
import { normalizeShortcutSequence } from "../keyboard/keybindings";

const SAVE_DEBOUNCE_MS = 250;

let pendingSaveTimer: number | null = null;
let pendingPreferences: UserUiPreferences | null = null;
let pendingUserId: string | null = null;

function normalizeKeybindingOverrides(overrides: Record<string, string> | null | undefined): Record<string, string> | null {
  if (!overrides) {
    return null;
  }

  const normalized = Object.fromEntries(
    Object.entries(overrides)
      .map(([key, value]) => [key.trim(), normalizeShortcutSequence(value)] as const)
      .filter(([key, value]) => key.length > 0 && value.length > 0),
  );

  return Object.keys(normalized).length > 0 ? normalized : null;
}

function normalizeThemePreferences(theme: UserThemePreferences | null | undefined): UserThemePreferences | null {
  if (!theme) {
    return null;
  }

  const activeThemeId = theme.activeThemeId?.trim() || null;
  const activeComponentStyles = theme.activeComponentStyles?.map((style) => style.trim()).filter(Boolean) ?? null;
  const activeLayoutStyle = theme.activeLayoutStyle?.trim() || null;
  const customThemeColors = theme.customThemeColors
    ? Object.fromEntries(Object.entries(theme.customThemeColors).filter(([key, value]) => key.trim() && value.trim()))
    : null;
  const styleOptions = theme.styleOptions
    ? Object.fromEntries(
      Object.entries(theme.styleOptions)
        .map(([styleId, options]): [string, Record<string, string>] => [
          styleId,
          Object.fromEntries(Object.entries(options).filter(([key, value]) => key.trim() && value.trim())),
        ])
        .filter(([styleId, options]) => styleId.length > 0 && Object.keys(options).length > 0),
    )
    : null;

  if (!activeThemeId
    && (!activeComponentStyles || activeComponentStyles.length === 0)
    && !activeLayoutStyle
    && (!customThemeColors || Object.keys(customThemeColors).length === 0)
    && (!styleOptions || Object.keys(styleOptions).length === 0)) {
    return null;
  }

  return {
    activeThemeId,
    activeComponentStyles,
    activeLayoutStyle,
    customThemeColors,
    styleOptions,
  };
}

function normalizeRatingSystemOptions(options: RatingSystemOptions | null | undefined): RatingSystemOptions | null {
  if (!options) {
    return null;
  }

  const type = options.type === "decimal" ? "decimal" : options.type === "stars" ? "stars" : null;
  if (!type) {
    return null;
  }

  const starPrecision = options.starPrecision === "half"
    || options.starPrecision === "quarter"
    || options.starPrecision === "tenth"
    || options.starPrecision === "full"
    ? options.starPrecision
    : "full";

  return { type, starPrecision };
}

function clampNumber(value: number | null | undefined, min: number, max: number): number | null {
  if (typeof value !== "number" || Number.isNaN(value)) {
    return null;
  }

  return Math.min(max, Math.max(min, value));
}

function normalizeTrackingPreferences(
  tracking: UserTrackingPreferences | null | undefined,
  legacyTrackingEnabled?: unknown,
): UserTrackingPreferences | null {
  const enabled = typeof tracking?.enabled === "boolean"
    ? tracking.enabled
    : typeof legacyTrackingEnabled === "boolean"
      ? legacyTrackingEnabled
      : null;
  const minViewSeconds = clampNumber(tracking?.minViewSeconds, 0, 86_400);
  const viewCompletionRatio = clampNumber(tracking?.viewCompletionRatio, 0.01, 1);
  const minImageDetailViewSeconds = clampNumber(tracking?.minImageDetailViewSeconds, 0, 86_400);
  const minDerivedLikeSessionSeconds = clampNumber(tracking?.minDerivedLikeSessionSeconds, 0, 86_400);
  const sessionIdleTimeoutSec = clampNumber(tracking?.sessionIdleTimeoutSec, 10, 86_400);
  const dwellPositiveSec = clampNumber(tracking?.dwellPositiveSec, 1, 86_400);

  if (enabled == null
    && minViewSeconds == null
    && viewCompletionRatio == null
    && minImageDetailViewSeconds == null
    && minDerivedLikeSessionSeconds == null
    && sessionIdleTimeoutSec == null
    && dwellPositiveSec == null) {
    return null;
  }

  return {
    enabled,
    minViewSeconds,
    viewCompletionRatio,
    minImageDetailViewSeconds,
    minDerivedLikeSessionSeconds,
    sessionIdleTimeoutSec,
    dwellPositiveSec,
  };
}

function normalizePlaybackPreferences(preferences: UserPlaybackPreferences | null | undefined): UserPlaybackPreferences | null {
  const skipSeconds = clampNumber(preferences?.skipSeconds, 1, 300);
  return skipSeconds == null ? null : { skipSeconds: Math.round(skipSeconds) };
}

function normalizeUiPreferences(preferences: UserUiPreferences | null | undefined): UserUiPreferences | null {
  const theme = normalizeThemePreferences(preferences?.theme);
  const ratingSystemOptions = normalizeRatingSystemOptions(preferences?.ratingSystemOptions);
  const legacyTrackingEnabledKey = "record" + "PlaybackHistory";
  const legacyTrackingEnabled = preferences && typeof preferences === "object"
    ? (preferences as Record<string, unknown>)[legacyTrackingEnabledKey]
    : undefined;
  const tracking = normalizeTrackingPreferences(preferences?.tracking, legacyTrackingEnabled);
  const includeCompilationGroups = preferences?.videos?.includeCompilationGroups;
  const videos = typeof includeCompilationGroups === "boolean"
    ? {
        ...(typeof includeCompilationGroups === "boolean" ? { includeCompilationGroups } : {}),
      }
    : null;
  const playback = normalizePlaybackPreferences(preferences?.playback);
  const keybindingOverrides = normalizeKeybindingOverrides(preferences?.keybindingOverrides);
  const homePageContent = preferences?.homePageContent?.trim() ? preferences.homePageContent : null;
  const defaultFilters = normalizeDefaultFilters(preferences?.defaultFilters);
  if (!theme && !ratingSystemOptions && !tracking && !videos && !playback && !keybindingOverrides && !homePageContent && !defaultFilters) {
    return null;
  }

  return {
    theme,
    ratingSystemOptions,
    tracking,
    videos,
    playback,
    keybindingOverrides,
    homePageContent,
    defaultFilters,
  };
}

function normalizeDefaultFilters(defaultFilters: Record<string, string> | null | undefined): Record<string, string> | null {
  if (!defaultFilters) {
    return null;
  }

  const normalized = Object.fromEntries(
    Object.entries(defaultFilters)
      .map(([key, value]) => [key.trim().toLowerCase(), typeof value === "string" ? value.trim() : ""] as const)
      .filter(([key, value]) => key.length > 0 && value.length > 0),
  );

  return Object.keys(normalized).length > 0 ? normalized : null;
}

export function supportsServerBackedUiPreferences(user: AuthUser | null | undefined): user is AuthUser & { kind: "user" | "system" } {
  return user?.kind === "user" || user?.kind === "system";
}

export function readAuthenticatedUserThemePreferences(): UserThemePreferences | null {
  const user = authStore.getUser();
  if (!supportsServerBackedUiPreferences(user)) {
    return null;
  }

  return normalizeThemePreferences(user.uiPreferences?.theme);
}

export function readAuthenticatedUserRatingOptions(): RatingSystemOptions | null {
  const user = authStore.getUser();
  if (!supportsServerBackedUiPreferences(user)) {
    return null;
  }

  return normalizeRatingSystemOptions(user.uiPreferences?.ratingSystemOptions);
}

/** The user's server-stored home page content JSON, or null when not signed in / unset. */
export function readAuthenticatedUserHomePageContent(): string | null {
  const user = authStore.getUser();
  if (!supportsServerBackedUiPreferences(user)) {
    return null;
  }

  return user.uiPreferences?.homePageContent?.trim() ? user.uiPreferences.homePageContent : null;
}

/** The user's server-stored default saved filter (opaque JSON) for a list mode, or null. */
export function readAuthenticatedUserDefaultFilter(mode: string): string | null {
  const user = authStore.getUser();
  if (!supportsServerBackedUiPreferences(user)) {
    return null;
  }

  const value = user.uiPreferences?.defaultFilters?.[mode.trim().toLowerCase()];
  return typeof value === "string" && value.trim() ? value : null;
}

export function updateAuthenticatedUserUiPreferences(
  updater: (current: UserUiPreferences | null) => UserUiPreferences | null,
): boolean {
  const user = authStore.getUser();
  if (!supportsServerBackedUiPreferences(user)) {
    return false;
  }

  const nextPreferences = normalizeUiPreferences(updater(normalizeUiPreferences(user.uiPreferences)));
  authStore.setUser({
    ...user,
    uiPreferences: nextPreferences,
  });

  pendingPreferences = nextPreferences;
  pendingUserId = user.id;
  if (pendingSaveTimer != null) {
    window.clearTimeout(pendingSaveTimer);
  }

  pendingSaveTimer = window.setTimeout(async () => {
    const targetUserId = pendingUserId;
    const snapshot = pendingPreferences;
    pendingSaveTimer = null;
    pendingPreferences = null;
    pendingUserId = null;

    const currentUser = authStore.getUser();
    if (!targetUserId || !supportsServerBackedUiPreferences(currentUser) || currentUser.id !== targetUserId) {
      return;
    }

    try {
      const savedPreferences = await auth.updateUiPreferences(snapshot);
      const refreshedUser = authStore.getUser();
      if (supportsServerBackedUiPreferences(refreshedUser) && refreshedUser.id === targetUserId) {
        authStore.setUser({
          ...refreshedUser,
          uiPreferences: normalizeUiPreferences(savedPreferences),
        });
      }
    } catch (error) {
      console.warn("Failed to persist user UI preferences", error);
    }
  }, SAVE_DEBOUNCE_MS);

  return true;
}
