import type { RatingStarPrecision, RatingSystemOptions, RatingSystemType } from "../api/types";
import { readAuthenticatedUserRatingOptions, updateAuthenticatedUserUiPreferences } from "./userUiPreferences";

export const RATING_OPTIONS_STORAGE_KEY = "cove-rating-options";
export const RATING_OPTIONS_CHANGE_EVENT = "cove-rating-options-changed";

function isRatingSystemType(value: unknown): value is RatingSystemType {
  return value === "stars" || value === "decimal";
}

function isRatingStarPrecision(value: unknown): value is RatingStarPrecision {
  return value === "full" || value === "half" || value === "quarter" || value === "tenth";
}

export function readStoredRatingOptionsOverride(): RatingSystemOptions | null {
  const userOverride = readAuthenticatedUserRatingOptions();
  if (userOverride) {
    return userOverride;
  }

  if (typeof window === "undefined") {
    return null;
  }

  try {
    const raw = window.localStorage.getItem(RATING_OPTIONS_STORAGE_KEY);
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw) as Partial<RatingSystemOptions>;
    if (!isRatingSystemType(parsed.type)) {
      return null;
    }

    return {
      type: parsed.type,
      starPrecision: isRatingStarPrecision(parsed.starPrecision) ? parsed.starPrecision : "full",
    };
  } catch {
    return null;
  }
}

export function writeStoredRatingOptionsOverride(options: RatingSystemOptions | null): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    if (options) {
      window.localStorage.setItem(RATING_OPTIONS_STORAGE_KEY, JSON.stringify(options));
    } else {
      window.localStorage.removeItem(RATING_OPTIONS_STORAGE_KEY);
    }
  } catch {
    // Ignore localStorage failures.
  }

  updateAuthenticatedUserUiPreferences((current) => ({
    ...(current ?? {}),
    ratingSystemOptions: options,
  }));

  window.dispatchEvent(new Event(RATING_OPTIONS_CHANGE_EVENT));
}
