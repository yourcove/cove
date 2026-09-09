export const LIMITED_PRIMARY_SETTINGS_TAB_KEYS = new Set([
  "my-account",
  "my-appearance-theme",
  "my-theme",
  "keyboard-shortcuts",
  "system-info-about",
  "system-info-runtime-status",
]);

export function isLimitedPrimarySettingsTabVisible(tabKey: string, canReadSegments: boolean): boolean {
  return LIMITED_PRIMARY_SETTINGS_TAB_KEYS.has(tabKey) || (tabKey === "library-display-profiles" && canReadSegments);
}
