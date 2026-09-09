import { describe, expect, it } from "vitest";
import {
  logFilterLevelOptions,
  isUnverifiedExtensionInstallSource,
  isValidQueryableJsonPointer,
  normalizeCustomFieldDefinitionForSync,
  removeCustomFieldDefinitionSnapshot,
  readSettingsTabFromUrl,
  resolveVisibleSettingsTab,
  serverLogLevelOptions,
  updateCustomFieldDefinitionSnapshot,
} from "../pages/SettingsPage";
import type { CustomFieldDefinition } from "../api/types";
import { isLimitedPrimarySettingsTabVisible } from "../pages/settings/tabVisibility";

describe("resolveVisibleSettingsTab", () => {
  it("falls back to the first visible tab when the requested tab is hidden", () => {
    expect(
      resolveVisibleSettingsTab("library", [
        { key: "my-appearance-theme" },
        { key: "my-theme" },
        { key: "system-info-about" },
      ]),
    ).toBe("my-appearance-theme");
  });

  it("keeps the requested tab when it is visible", () => {
    expect(
      resolveVisibleSettingsTab("system-info-about", [{ key: "my-appearance-theme" }, { key: "system-info-about" }]),
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

describe("log level option ordering", () => {
  it("orders server levels from Critical down to Trace", () => {
    expect(serverLogLevelOptions.map((option) => option.label)).toEqual([
      "Critical",
      "Error",
      "Warning",
      "Info",
      "Debug",
      "Trace (15 minutes)",
    ]);
  });

  it("keeps All first and orders filter levels from Critical down to Trace", () => {
    expect(logFilterLevelOptions.map((option) => option.label)).toEqual([
      "All",
      "Critical",
      "Error",
      "Warning",
      "Info",
      "Debug",
      "Trace",
    ]);
  });
});

describe("extension installation trust badges", () => {
  it("marks direct URL and ZIP uploads as unverified", () => {
    expect(isUnverifiedExtensionInstallSource("url")).toBe(true);
    expect(isUnverifiedExtensionInstallSource("upload")).toBe(true);
    expect(isUnverifiedExtensionInstallSource("registry")).toBe(false);
  });
});

describe("isValidQueryableJsonPointer", () => {
  it("accepts nested, array, and escaped pointer segments", () => {
    expect(isValidQueryableJsonPointer("/profile/score")).toBe(true);
    expect(isValidQueryableJsonPointer("/items/0/value")).toBe(true);
    expect(isValidQueryableJsonPointer("/escaped~1property/tilde~0property")).toBe(true);
    expect(isValidQueryableJsonPointer("/property ")).toBe(true);
  });

  it("rejects root, malformed escapes, and overly deep pointers", () => {
    expect(isValidQueryableJsonPointer("")).toBe(false);
    expect(isValidQueryableJsonPointer("profile.score")).toBe(false);
    expect(isValidQueryableJsonPointer(" /profile/score")).toBe(false);
    expect(isValidQueryableJsonPointer("/invalid~2escape")).toBe(false);
    expect(isValidQueryableJsonPointer(`/${Array.from({ length: 33 }, () => "item").join("/")}`)).toBe(false);
  });
});

describe("custom field draft snapshots", () => {
  it("normalizes long text as a single non-queryable value", () => {
    const normalized = normalizeCustomFieldDefinitionForSync(
      {
        key: "notes",
        label: "Notes",
        type: "longText",
        entityTypes: ["performer"],
        options: [],
        filterable: true,
        sortable: true,
        isMultiValue: true,
        jsonPaths: [{ path: "/ignored", label: "Ignored", type: "text", filterable: true, sortable: true }],
      },
      0,
    );

    expect(normalized).toEqual(
      expect.objectContaining({
        filterable: false,
        sortable: false,
        isMultiValue: false,
        jsonPaths: [],
      }),
    );
  });

  it("composes rapid JSON path toggles and removal from the latest synchronous snapshot", () => {
    const initial: CustomFieldDefinition[] = [
      {
        id: 1,
        key: "structured_metadata",
        label: "Structured metadata",
        type: "json",
        entityTypes: ["video"],
        options: [],
        filterable: false,
        sortable: false,
        jsonPaths: [{ path: "/score", label: "Score", type: "number", filterable: false, sortable: false }],
      },
    ];

    const filterable = updateCustomFieldDefinitionSnapshot(initial, 0, (definition) => ({
      ...definition,
      jsonPaths: definition.jsonPaths?.map((path) => ({ ...path, filterable: true })),
    }))!;
    const filterableAndSortable = updateCustomFieldDefinitionSnapshot(filterable, 0, (definition) => ({
      ...definition,
      jsonPaths: definition.jsonPaths?.map((path) => ({ ...path, sortable: true })),
    }))!;

    expect(filterableAndSortable[0].jsonPaths).toEqual([
      expect.objectContaining({ path: "/score", filterable: true, sortable: true }),
    ]);
    expect(removeCustomFieldDefinitionSnapshot(filterableAndSortable, 0)).toEqual([]);
  });
});
