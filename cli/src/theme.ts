import type { CoveClient } from "./client";
import { DEFAULT_ACCENT, terminalRgb } from "./ui";

type ThemeClient = Pick<CoveClient, "get">;

function record(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function supportedColor(value: unknown): string | undefined {
  if (typeof value !== "string" || !terminalRgb(value)) return undefined;
  return value.trim();
}

export function themeAccent(me: unknown, manifest?: unknown): string {
  const user = record(record(me)?.user);
  const preferences = record(user?.uiPreferences);
  const theme = record(preferences?.theme);
  const activeThemeId = typeof theme?.activeThemeId === "string" ? theme.activeThemeId : "default";
  if (activeThemeId === "custom") {
    return supportedColor(record(theme?.customThemeColors)?.["--color-accent"]) ?? DEFAULT_ACCENT;
  }

  const themes = record(manifest)?.themes;
  if (!Array.isArray(themes)) return DEFAULT_ACCENT;
  const activeTheme = themes.map(record).find(candidate => candidate?.id === activeThemeId);
  return supportedColor(record(activeTheme?.cssVariables)?.["--color-accent"]) ?? DEFAULT_ACCENT;
}

export async function fetchThemeAccent(client: ThemeClient): Promise<string> {
  const [me, manifest] = await Promise.allSettled([
    client.get<unknown>("auth/me"),
    client.get<unknown>("extensions/manifest"),
  ]);
  return themeAccent(me.status === "fulfilled" ? me.value : undefined, manifest.status === "fulfilled" ? manifest.value : undefined);
}
