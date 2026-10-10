import { describe, expect, it } from "vitest";
import {
  findGroupsNeedingColor,
  hueOfColor,
  LEGACY_DEFAULT_TAG_GROUP_COLOR,
  nextTagGroupColor,
  normalizeGroupColor,
  oklchToHex,
  suggestDistinctColors,
} from "../utils/tagGroupColors";

const group = (id: number, color: string | null, sortOrder = id * 10) => ({ id, color, sortOrder });

function hueGap(a: number, b: number) {
  const d = Math.abs(a - b) % 360;
  return Math.min(d, 360 - d);
}

describe("normalizeGroupColor", () => {
  it("lowercases valid colors and drops alpha", () => {
    expect(normalizeGroupColor(" #6EE7B7 ")).toBe("#6ee7b7");
    expect(normalizeGroupColor("#6ee7b780")).toBe("#6ee7b7");
  });

  it("rejects missing or malformed colors", () => {
    expect(normalizeGroupColor(null)).toBeNull();
    expect(normalizeGroupColor("")).toBeNull();
    expect(normalizeGroupColor("red")).toBeNull();
    expect(normalizeGroupColor("#abc")).toBeNull();
  });
});

describe("findGroupsNeedingColor", () => {
  it("selects groups with no color, the old default, or a shared color", () => {
    const groups = [
      group(1, LEGACY_DEFAULT_TAG_GROUP_COLOR),
      group(2, null),
      group(3, "#5F95CE"),
      group(4, "#5f95ce"),
      group(5, "#a43737"),
      group(6, "not a color"),
    ];

    expect([...findGroupsNeedingColor(groups)].sort((a, b) => a - b)).toEqual([1, 2, 3, 4, 6]);
  });

  it("leaves unique, deliberately chosen colors alone", () => {
    expect(findGroupsNeedingColor([group(1, "#a43737"), group(2, "#38587a")]).size).toBe(0);
  });
});

describe("suggestDistinctColors", () => {
  it("gives every selected group a different valid color and leaves the rest out", () => {
    const groups = Array.from({ length: 23 }, (_, i) => group(i + 1, LEGACY_DEFAULT_TAG_GROUP_COLOR));
    groups.push(group(99, "#a43737"));
    const selected = new Set(groups.filter((g) => g.id !== 99).map((g) => g.id));

    const result = suggestDistinctColors(groups, selected);

    expect(result.size).toBe(23);
    expect(result.has(99)).toBe(false);
    for (const color of result.values()) expect(color).toMatch(/^#[0-9a-f]{6}$/);
    expect(new Set(result.values()).size).toBe(23);
  });

  it("spaces hues as far as possible from each other and from groups that keep their color", () => {
    const fixedHue = hueOfColor("#a43737")!;
    const groups = [group(1, null), group(2, null), group(3, null), group(4, "#a43737")];

    const hues = [...suggestDistinctColors(groups, new Set([1, 2, 3])).values()].map((color) => hueOfColor(color)!);

    // Three new hues plus one kept hue fit a quarter turn apart. Allow a few degrees for gamut
    // mapping and hex rounding.
    const all = [...hues, fixedHue];
    for (let i = 0; i < all.length; i++) {
      for (let j = i + 1; j < all.length; j++) expect(hueGap(all[i], all[j])).toBeGreaterThan(85);
    }
  });

  it("spaces new hues evenly together with a single kept hue", () => {
    const fixedHue = hueOfColor("#a43737")!;
    const groups = [group(1, null), group(2, null), group(3, "#a43737")];

    const hues = [...suggestDistinctColors(groups, new Set([1, 2])).values()].map((color) => hueOfColor(color)!);

    // Two new hues and one kept hue can sit a third of a turn apart.
    const all = [...hues, fixedHue];
    for (let i = 0; i < all.length; i++) {
      for (let j = i + 1; j < all.length; j++) expect(hueGap(all[i], all[j])).toBeGreaterThan(115);
    }
  });

  it("spaces hues evenly around the wheel when no group keeps its color", () => {
    const groups = [group(1, null), group(2, null), group(3, null)];

    const hues = [...suggestDistinctColors(groups, new Set([1, 2, 3])).values()].map((color) => hueOfColor(color)!);

    for (let i = 0; i < hues.length; i++) {
      for (let j = i + 1; j < hues.length; j++) expect(hueGap(hues[i], hues[j])).toBeGreaterThan(115);
    }
  });

  it("steers around a cluster of kept groups that share one hue", () => {
    // Several kept groups that differ only in lightness share a hue; even spacing alone would put a
    // suggestion within ~13° of them.
    const blue = (lightness: number) => oklchToHex(lightness, 0.1, 251);
    const kept = [0.45, 0.55, 0.62, 0.7, 0.75].map((l, i) => group(100 + i, blue(l)));
    const recolored = Array.from({ length: 14 }, (_, i) => group(i + 1, LEGACY_DEFAULT_TAG_GROUP_COLOR));
    const groups = [...recolored, ...kept, group(200, "#3dc790")];

    const result = suggestDistinctColors(groups, new Set(recolored.map((g) => g.id)));

    const keptHues = [...kept, groups.at(-1)!].map((g) => hueOfColor(g.color)!);
    for (const color of result.values()) {
      for (const keptHue of keptHues) expect(hueGap(hueOfColor(color)!, keptHue)).toBeGreaterThan(15);
    }

    // Groups still get their hues in sort order: each one is further around the wheel than the last.
    const positions = recolored.map((g) => hueOfColor(result.get(g.id))!);
    const turns = positions.slice(1).map((hue, i) => (hue - positions[i] + 360) % 360);
    expect(turns.reduce((sum, turn) => sum + turn, 0)).toBeLessThan(360);
  });

  it("assigns hues in sort order by default and is deterministic per variant", () => {
    const groups = [group(1, null, 30), group(2, null, 10), group(3, null, 20), group(4, null, 40)];
    const selected = new Set([1, 2, 3, 4]);

    const first = suggestDistinctColors(groups, selected);
    expect(suggestDistinctColors(groups, selected)).toEqual(first);
    // Sort order 10, 20, 30, 40 is ids 2, 3, 1, 4; consecutive ones get consecutive hues.
    const hue = (id: number) => hueOfColor(first.get(id))!;
    expect(hueGap(hue(2), hue(3))).toBeCloseTo(90, -1);
    expect(hueGap(hue(3), hue(1))).toBeCloseTo(90, -1);

    const shuffled = suggestDistinctColors(groups, selected, 3);
    expect(suggestDistinctColors(groups, selected, 3)).toEqual(shuffled);
    expect(new Set(shuffled.values())).toEqual(new Set(first.values()));
    expect(shuffled).not.toEqual(first);
  });

  it("shuffles into many different orders", () => {
    const groups = [group(1, null), group(2, null), group(3, null), group(4, null)];
    const selected = new Set([1, 2, 3, 4]);

    const orders = new Set(
      Array.from({ length: 40 }, (_, variant) =>
        [...suggestDistinctColors(groups, selected, variant + 1).values()].join(","),
      ),
    );

    // 24 orders are possible for four groups.
    expect(orders.size).toBeGreaterThan(16);
  });

  it("returns nothing when no group is selected", () => {
    expect(suggestDistinctColors([group(1, null)], new Set()).size).toBe(0);
  });
});

describe("nextTagGroupColor", () => {
  it("picks the hue furthest from every existing group", () => {
    const red = oklchToHex(0.7, 0.12, 30);
    const green = oklchToHex(0.7, 0.12, 150);
    const blue = oklchToHex(0.7, 0.12, 270);

    const next = nextTagGroupColor([group(1, red), group(2, green), group(3, blue)]);

    const hue = hueOfColor(next)!;
    for (const existing of [30, 150, 270]) expect(hueGap(hue, existing)).toBeGreaterThan(55);
    expect(next).not.toBe(LEGACY_DEFAULT_TAG_GROUP_COLOR);
  });

  it("ignores grey and missing colors", () => {
    expect(nextTagGroupColor([group(1, "#808080"), group(2, null)])).toBe(nextTagGroupColor([]));
  });
});

describe("oklchToHex", () => {
  it("round-trips the hue for an in-gamut color", () => {
    expect(hueGap(hueOfColor(oklchToHex(0.78, 0.12, 200))!, 200)).toBeLessThan(2);
  });

  it("reduces chroma rather than clipping channels for out-of-gamut input, which would shift the hue", () => {
    const color = oklchToHex(0.78, 0.3, 260);

    expect(color).toMatch(/^#[0-9a-f]{6}$/);
    expect(hueGap(hueOfColor(color)!, 260)).toBeLessThan(2);
  });
});
