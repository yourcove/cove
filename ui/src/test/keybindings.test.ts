import { describe, expect, it } from "vitest";
import { normalizeShortcutEvent, normalizeShortcutSequence } from "../keyboard/keybindings";

describe("keyboard shortcut normalization", () => {
  it("preserves Shift when it distinguishes alphabetic keys", () => {
    expect(normalizeShortcutEvent({ key: "j", ctrlKey: false, metaKey: false, altKey: false, shiftKey: false })).toBe("j");
    expect(normalizeShortcutEvent({ key: "J", ctrlKey: false, metaKey: false, altKey: false, shiftKey: true })).toBe("Shift+j");
    expect(normalizeShortcutSequence("Shift+j")).toBe("Shift+j");
    expect(normalizeShortcutEvent({ key: "Ö", ctrlKey: false, metaKey: false, altKey: false, shiftKey: true })).toBe("Shift+ö");
    expect(normalizeShortcutSequence("Shift+ö")).toBe("Shift+ö");
    expect(normalizeShortcutEvent({ key: "𐐀", ctrlKey: false, metaKey: false, altKey: false, shiftKey: true })).toBe("Shift+𐐨");
  });

  it("keeps produced punctuation logical instead of reconstructing physical Shift combinations", () => {
    expect(normalizeShortcutEvent({ key: "<", ctrlKey: false, metaKey: false, altKey: false, shiftKey: true })).toBe("<");
    expect(normalizeShortcutSequence("<")).toBe("<");
    expect(normalizeShortcutSequence("Shift+,")).toBe(",");
  });
});
