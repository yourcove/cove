import { describe, expect, it } from "vitest";
import { extensionKeyboardScopeMatches } from "../extensions/ExtensionKeyboardActions";

describe("extension keyboard action scopes", () => {
  it("enforces page and entity type scopes", () => {
    expect(extensionKeyboardScopeMatches(
      { surface: "detail", page: "video", entityType: "video" },
      { page: "video", id: 12 },
    )).toBe(true);
    expect(extensionKeyboardScopeMatches(
      { surface: "detail", entityType: "video" },
      { page: "performer", id: 12 },
    )).toBe(false);
  });

  it("requires tab-scoped actions to register from the mounted tab", () => {
    expect(extensionKeyboardScopeMatches(
      { surface: "local", page: "video", tab: "details" },
      { page: "video", id: 12 },
    )).toBe(false);
  });
});
