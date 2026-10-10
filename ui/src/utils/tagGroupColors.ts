import type { TagGroup } from "../api/types";

/** The color the Tag Groups form used to fill in for every new group. */
export const LEGACY_DEFAULT_TAG_GROUP_COLOR = "#6ee7b7";

// Suggested colors share one OKLCH lightness and chroma so every group reads equally well on the
// dark card; only the hue changes. The hue sequence starts at the old default's green.
const SUGGESTED_LIGHTNESS = 0.78;
const SUGGESTED_CHROMA = 0.12;
const BASE_HUE = 158;
// Below this chroma a color is effectively grey and has no hue worth steering away from.
const MIN_HUE_CHROMA = 0.04;

type ColorGroup = Pick<TagGroup, "id" | "color" | "sortOrder">;

/** Returns `#rrggbb` (lowercase, alpha dropped) for a valid stored color, otherwise null. */
export function normalizeGroupColor(value?: string | null): string | null {
  const trimmed = value?.trim();
  if (!trimmed || !/^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(trimmed)) return null;
  return trimmed.slice(0, 7).toLowerCase();
}

/**
 * Groups that should get a new color by default: no valid color, still the old form default, or a
 * color another group also uses. A deliberately chosen, unique color is left alone.
 */
export function findGroupsNeedingColor(groups: ColorGroup[]): Set<number> {
  const counts = new Map<string, number>();
  for (const group of groups) {
    const color = normalizeGroupColor(group.color);
    if (color) counts.set(color, (counts.get(color) ?? 0) + 1);
  }
  const ids = new Set<number>();
  for (const group of groups) {
    const color = normalizeGroupColor(group.color);
    if (!color || color === LEGACY_DEFAULT_TAG_GROUP_COLOR || (counts.get(color) ?? 0) > 1) ids.add(group.id);
  }
  return ids;
}

/**
 * Proposes a color for each selected group. Selected groups get evenly spaced hues in sort order,
 * rotated as far as possible from the hues of the groups that keep their colors. `variant` reshuffles
 * which group gets which hue; variant 0 keeps sort order, so neighbouring groups get neighbouring hues.
 */
export function suggestDistinctColors(
  groups: ColorGroup[],
  selectedIds: ReadonlySet<number>,
  variant = 0,
): Map<number, string> {
  const selected = groups
    .filter((group) => selectedIds.has(group.id))
    .sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id);
  const fixedHues = groups
    .filter((group) => !selectedIds.has(group.id))
    .map((group) => hueOfColor(group.color))
    .filter((hue): hue is number => hue != null);

  const hues = spreadHues(selected.length, fixedHues);
  const order = shuffledIndexes(selected.length, variant);
  const result = new Map<number, string>();
  selected.forEach((group, index) => {
    result.set(group.id, oklchToHex(SUGGESTED_LIGHTNESS, SUGGESTED_CHROMA, hues[order[index]]));
  });
  return result;
}

/** The color a brand-new group should start with: the hue furthest from every existing group's. */
export function nextTagGroupColor(groups: ColorGroup[]): string {
  const fixedHues = groups.map((group) => hueOfColor(group.color)).filter((hue): hue is number => hue != null);
  return oklchToHex(SUGGESTED_LIGHTNESS, SUGGESTED_CHROMA, spreadHues(1, fixedHues)[0]);
}

/**
 * Picks `count` hues as far as possible from each other and from `fixedHues`. Three placements are
 * tried and the one with the largest smallest gap wins: evenly spaced (best when the wheel is free),
 * evenly spaced together with one kept hue (best when only a few groups keep their colors), and
 * filling the widest gaps (best when kept groups crowd one part of the wheel). The result is sorted
 * around the wheel from the base hue so consecutive groups get neighbouring hues.
 */
function spreadHues(count: number, fixedHues: number[]): number[] {
  if (count === 0) return [];
  const candidates = [
    evenlySpacedHues(count, fixedHues),
    ...fixedHues.map((anchor) => hueSlotsAround(anchor, count)),
    gapFilledHues(count, fixedHues),
  ];
  let best = candidates[0];
  let bestScore = smallestGap(best, fixedHues);
  for (const candidate of candidates.slice(1)) {
    const score = smallestGap(candidate, fixedHues);
    if (score > bestScore + 1e-9) {
      best = candidate;
      bestScore = score;
    }
  }
  return [...best].sort((a, b) => wheelPosition(a) - wheelPosition(b));
}

/** `count` hues evenly spaced with `anchor`, which takes one of the `count + 1` slots itself. */
function hueSlotsAround(anchor: number, count: number): number[] {
  const step = 360 / (count + 1);
  return Array.from({ length: count }, (_, k) => (anchor + (k + 1) * step) % 360);
}

function wheelPosition(hue: number) {
  return (((hue - BASE_HUE) % 360) + 360) % 360;
}

/** Smallest distance from a new hue to any other hue. Kept groups that already share a hue don't count. */
function smallestGap(newHues: number[], fixedHues: number[]) {
  let smallest = Number.POSITIVE_INFINITY;
  newHues.forEach((hue, i) => {
    for (const other of [...newHues.slice(i + 1), ...fixedHues]) smallest = Math.min(smallest, hueDistance(hue, other));
  });
  return smallest;
}

function gapFilledHues(count: number, fixedHues: number[]): number[] {
  const taken = [...fixedHues];
  const added: number[] = [];
  for (let k = 0; k < count; k++) {
    let hue = BASE_HUE;
    if (taken.length > 0) {
      const sorted = [...taken].map(wheelPosition).sort((a, b) => a - b);
      let widest = -1;
      for (let i = 0; i < sorted.length; i++) {
        const start = sorted[i];
        const end = i + 1 < sorted.length ? sorted[i + 1] : sorted[0] + 360;
        if (end - start > widest + 1e-9) {
          widest = end - start;
          hue = (BASE_HUE + start + widest / 2) % 360;
        }
      }
    }
    taken.push(hue);
    added.push(hue);
  }
  return added;
}

function evenlySpacedHues(count: number, fixedHues: number[]): number[] {
  if (count === 0) return [];
  const step = 360 / count;
  let bestOffset = 0;
  let bestScore = -1;
  // Try 1° offsets (finer when there are many groups) and keep the one furthest from fixed hues.
  const tries = Math.max(1, Math.ceil(step));
  for (let i = 0; i < tries; i++) {
    const offset = (i * step) / tries;
    let score = Number.POSITIVE_INFINITY;
    for (let k = 0; k < count; k++) {
      const hue = BASE_HUE + offset + k * step;
      for (const fixed of fixedHues) score = Math.min(score, hueDistance(hue, fixed));
    }
    if (score > bestScore + 1e-9) {
      bestScore = score;
      bestOffset = offset;
    }
  }
  return Array.from({ length: count }, (_, k) => (BASE_HUE + bestOffset + k * step) % 360);
}

function hueDistance(a: number, b: number) {
  const d = Math.abs((((a - b) % 360) + 360) % 360);
  return Math.min(d, 360 - d);
}

function shuffledIndexes(count: number, variant: number): number[] {
  const order = Array.from({ length: count }, (_, i) => i);
  if (variant === 0) return order;
  // Deterministic Fisher–Yates driven by a small LCG, so the same variant always gives the same result.
  let seed = (variant * 2654435761) >>> 0;
  for (let i = count - 1; i > 0; i--) {
    seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
    // Use the high bits; an LCG's low bits cycle quickly and would reach only a few orders.
    const j = Math.floor((seed / 2 ** 32) * (i + 1));
    [order[i], order[j]] = [order[j], order[i]];
  }
  return order;
}

/** OKLCH hue in degrees, or null for an invalid or near-grey color. */
export function hueOfColor(value?: string | null): number | null {
  const hex = normalizeGroupColor(value);
  if (!hex) return null;
  const [r, g, b] = [1, 3, 5].map((i) => srgbToLinear(Number.parseInt(hex.slice(i, i + 2), 16) / 255));
  const l = Math.cbrt(0.4122214708 * r + 0.5363026654 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  const labA = 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s;
  const labB = 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s;
  if (Math.hypot(labA, labB) < MIN_HUE_CHROMA) return null;
  return ((Math.atan2(labB, labA) * 180) / Math.PI + 360) % 360;
}

/** Converts OKLCH to sRGB hex, lowering chroma until the color fits the sRGB gamut. */
export function oklchToHex(lightness: number, chroma: number, hue: number): string {
  const rad = (hue * Math.PI) / 180;
  for (let c = chroma; c >= 0; c -= 0.005) {
    const rgb = oklabToLinearSrgb(lightness, c * Math.cos(rad), c * Math.sin(rad));
    if (rgb.every((channel) => channel >= -1e-4 && channel <= 1 + 1e-4)) {
      return `#${rgb.map((channel) => toHexByte(linearToSrgb(channel))).join("")}`;
    }
  }
  const grey = oklabToLinearSrgb(lightness, 0, 0);
  return `#${grey.map((channel) => toHexByte(linearToSrgb(channel))).join("")}`;
}

function oklabToLinearSrgb(lightness: number, a: number, b: number): [number, number, number] {
  const l = (lightness + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m = (lightness - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s = (lightness - 0.0894841775 * a - 1.291485548 * b) ** 3;
  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];
}

function srgbToLinear(channel: number) {
  return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
}

function linearToSrgb(channel: number) {
  const clamped = Math.min(1, Math.max(0, channel));
  return clamped <= 0.0031308 ? 12.92 * clamped : 1.055 * clamped ** (1 / 2.4) - 0.055;
}

function toHexByte(channel: number) {
  return Math.round(channel * 255)
    .toString(16)
    .padStart(2, "0");
}
