import { describe, expect, it } from "vitest";
import {
  flattenKeyboardPreset,
  getKeyboardSequenceContinuations,
  resolveKeyboardDispatch,
  resolveKeyboardPreset,
  validateKeyboardPreset,
  type KeyboardActionDefinition,
  type KeyboardShortcutPreset,
} from "../keyboard/registry";
import { keepResolvableKeyboardPresets } from "../keyboard/KeyboardShortcutProvider";

const actions: KeyboardActionDefinition[] = [
  { id: "global.help", group: "Global", label: "Help", defaultBindings: ["?"] },
  { id: "list.filters", group: "Lists", label: "Filters", defaultBindings: ["f"] },
  { id: "detail.favorite", group: "Details", label: "Favorite", defaultBindings: ["o"] },
];

const cove: KeyboardShortcutPreset = {
  schemaVersion: 1,
  id: "example:cove",
  name: "Example Cove",
  unmappedActions: "action-defaults",
  bindings: {},
};

describe("keyboard preset registry", () => {
  it("resolves presets by data rather than special preset ids", () => {
    const duplicate: KeyboardShortcutPreset = { ...cove, id: "user:any-name", name: "Copy" };

    expect(resolveKeyboardPreset(duplicate, [duplicate], actions).bindings).toEqual({
      "global.help": ["?"],
      "list.filters": ["f"],
      "detail.favorite": ["o"],
    });
  });

  it("supports inheritance and explicit unbinding", () => {
    const child: KeyboardShortcutPreset = {
      schemaVersion: 1,
      id: "extension:example:filters-only",
      name: "Filters only",
      basePresetId: cove.id,
      unmappedActions: "unbound",
      bindings: {
        "global.help": [],
        "list.filters": ["Mod+F", "g f"],
      },
    };

    expect(resolveKeyboardPreset(child, [cove, child], actions).bindings).toEqual({
      "global.help": [],
      "list.filters": ["Ctrl+f", "g f"],
      "detail.favorite": ["o"],
    });
  });

  it("flattens a contributed preset into a self-contained personal document", () => {
    const child: KeyboardShortcutPreset = {
      schemaVersion: 1,
      id: "extension:example:alternate",
      name: "Alternate",
      basePresetId: cove.id,
      unmappedActions: "unbound",
      bindings: { "detail.favorite": ["f"] },
      provenance: { source: "extension", providerId: "example" },
    };

    const flattened = flattenKeyboardPreset(child, [cove, child], actions, {
      id: "user:copy",
      name: "My alternate",
    });

    expect(flattened).toMatchObject({
      id: "user:copy",
      name: "My alternate",
      unmappedActions: "unbound",
      bindings: {
        "global.help": ["?"],
        "list.filters": ["f"],
        "detail.favorite": ["f"],
      },
      provenance: {
        source: "personal",
        originalPresetId: child.id,
      },
    });
    expect(flattened.basePresetId).toBeUndefined();
  });

  it("rejects inheritance cycles and sequences longer than three strokes", () => {
    const a: KeyboardShortcutPreset = {
      schemaVersion: 1,
      id: "a",
      name: "A",
      basePresetId: "b",
      unmappedActions: "unbound",
      bindings: {},
    };
    const b: KeyboardShortcutPreset = { ...a, id: "b", name: "B", basePresetId: "a" };

    expect(() => resolveKeyboardPreset(a, [a, b], actions)).toThrow(/cycle/i);
    expect(validateKeyboardPreset({
      ...cove,
      bindings: { "list.filters": ["g f x y"] },
    }).errors).toContainEqual(expect.stringMatching(/three strokes/i));
  });

  it("prefers the most specific active surface", () => {
    const resolution = resolveKeyboardDispatch([
      { actionId: "global", sequence: "f", priority: 0, value: "global" },
      { actionId: "detail", sequence: "f", priority: 20, value: "detail" },
    ], "f");

    expect(resolution).toMatchObject({ kind: "action", candidate: { value: "detail" } });
  });

  it("blocks peer and prefix conflicts", () => {
    expect(resolveKeyboardDispatch([
      { actionId: "one", sequence: "g", priority: 10, value: 1 },
      { actionId: "two", sequence: "g g", priority: 10, value: 2 },
    ], "g")).toEqual({ kind: "conflict", actionIds: ["one", "two"] });
  });

  it("allows several chords to share a prefix", () => {
    const candidates = [
      { actionId: "home", sequence: "g h", priority: 0, value: 1 },
      { actionId: "videos", sequence: "g s", priority: 0, value: 2 },
      { actionId: "faces", sequence: "g f", priority: 0, value: 3 },
    ];
    expect(resolveKeyboardDispatch(candidates, "g")).toEqual({ kind: "prefix" });
    expect(getKeyboardSequenceContinuations(candidates, "g")).toEqual(["h", "s", "f"]);
  });

  it("isolates contributed presets with missing bases", () => {
    const broken: KeyboardShortcutPreset = {
      ...cove,
      id: "extension:sample:broken",
      name: "Broken",
      basePresetId: "extension:missing:base",
    };

    expect(keepResolvableKeyboardPresets([cove, broken], actions).map((preset) => preset.id)).toEqual([cove.id]);
  });
});
