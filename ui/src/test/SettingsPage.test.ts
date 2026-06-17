import { describe, expect, it } from "vitest";
import { readSettingsTabFromUrl, resolveVisibleSettingsTab } from "../pages/SettingsPage";
import { isLimitedPrimarySettingsTabVisible } from "../pages/settings/tabVisibility";

describe("resolveVisibleSettingsTab", () => {
  it("falls back to the first visible tab when the requested tab is hidden", () => {
    expect(
      resolveVisibleSettingsTab("library", [
        { key: "my-appearance-theme" },
        { key: "my-theme" },
        { key: "system-info-about" },
      ])
    ).toBe("my-appearance-theme");
  });

  it("keeps the requested tab when it is visible", () => {
    expect(
      resolveVisibleSettingsTab("system-info-about", [
        { key: "my-appearance-theme" },
        { key: "system-info-about" },
      ])
    ).toBe("system-info-about");
  });
});

describe("readSettingsTabFromUrl", () => {
  it("preserves nested extension settings paths before contributed aliases are loaded", () => {
    window.history.replaceState({}, "", "/settings/extensions/docs");

    expect(readSettingsTabFromUrl()).toBe("extensions/docs");
  });

  it("keeps nested contributed child settings paths instead of collapsing to the built-in extensions tab", () => {
    window.history.replaceState({}, "", "/settings/extensions/docs/topic");

    expect(readSettingsTabFromUrl()).toBe("extensions/docs/topic");
  });

  it("preserves single-segment extension settings paths before contributed aliases are loaded", () => {
    window.history.replaceState({}, "", "/settings/docs");

    expect(readSettingsTabFromUrl()).toBe("docs");
  });

  it("resolves single-segment extension settings aliases after contributions load", () => {
    window.history.replaceState({}, "", "/settings/docs");

    expect(readSettingsTabFromUrl({ docs: "extensions/docs" })).toBe("extensions/docs");
  });
});

describe("isLimitedPrimarySettingsTabVisible", () => {
  it("keeps personal and system info tabs visible for limited users", () => {
    expect(isLimitedPrimarySettingsTabVisible("my-account", false)).toBe(true);
    expect(isLimitedPrimarySettingsTabVisible("my-appearance-theme", false)).toBe(true);
    expect(isLimitedPrimarySettingsTabVisible("my-theme", false)).toBe(true);
    expect(isLimitedPrimarySettingsTabVisible("system-info-about", false)).toBe(true);
    expect(isLimitedPrimarySettingsTabVisible("library-paths-storage", false)).toBe(false);
  });

  it("keeps display profiles gated by segment access", () => {
    expect(isLimitedPrimarySettingsTabVisible("library-display-profiles", false)).toBe(false);
    expect(isLimitedPrimarySettingsTabVisible("library-display-profiles", true)).toBe(true);
  });
});