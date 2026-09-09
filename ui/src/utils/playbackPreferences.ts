import { useMemo } from "react";
import { useAuth } from "../auth/AuthContext";
import type { UserPlaybackPreferences } from "../api/types";

export type ResolvedPlaybackPreferences = {
  skipSeconds: number;
};

export const DEFAULT_PLAYBACK_PREFERENCES: ResolvedPlaybackPreferences = {
  skipSeconds: 15,
};

export function normalizePlaybackPreferences(
  preferences: UserPlaybackPreferences | null | undefined,
): ResolvedPlaybackPreferences {
  const skipSeconds =
    typeof preferences?.skipSeconds === "number" && Number.isFinite(preferences.skipSeconds)
      ? Math.round(Math.min(300, Math.max(1, preferences.skipSeconds)))
      : DEFAULT_PLAYBACK_PREFERENCES.skipSeconds;

  return { skipSeconds };
}

export function usePlaybackPreferences(): ResolvedPlaybackPreferences {
  const { user } = useAuth();
  const skipSeconds = user?.uiPreferences?.playback?.skipSeconds;

  return useMemo(() => normalizePlaybackPreferences({ skipSeconds }), [skipSeconds]);
}
