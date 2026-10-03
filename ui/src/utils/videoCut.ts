/** A span of a video's timeline, in seconds. */
export interface CutRange {
  start: number;
  end: number;
}

/** Removals shorter than this are dropped, matching what the server ignores. */
const MIN_RANGE_SECONDS = 0.05;

/**
 * Sorts removals, clamps them to the video and merges overlapping or touching ones, so what the editor
 * shows is exactly what would be cut.
 */
export function normalizeCutRanges(ranges: readonly CutRange[], duration: number): CutRange[] {
  const clamped = ranges
    .map((range) => ({
      start: Math.max(0, Math.min(range.start, range.end)),
      end: Math.min(duration > 0 ? duration : Number.POSITIVE_INFINITY, Math.max(range.start, range.end)),
    }))
    .filter((range) => range.end - range.start >= MIN_RANGE_SECONDS)
    .sort((a, b) => a.start - b.start);
  const merged: CutRange[] = [];
  for (const range of clamped) {
    const last = merged[merged.length - 1];
    if (last && range.start <= last.end) last.end = Math.max(last.end, range.end);
    else merged.push({ ...range });
  }
  return merged;
}

/** Seconds left after removing `ranges` (already normalized) from a video. */
export function keptSeconds(ranges: readonly CutRange[], duration: number): number {
  return Math.max(0, duration - ranges.reduce((total, range) => total + (range.end - range.start), 0));
}

/** The removal containing `time`, if any. */
export function removalAt(ranges: readonly CutRange[], time: number): CutRange | undefined {
  return ranges.find((range) => time >= range.start && time < range.end);
}

/**
 * Formats seconds as h:mm:ss.s or m:ss.s. Tenths are kept because a cut is placed more precisely than
 * the player's whole-second clock.
 */
export function formatTimecode(seconds: number): string {
  const safe = Math.max(0, Number.isFinite(seconds) ? seconds : 0);
  const tenths = Math.round(safe * 10);
  const h = Math.floor(tenths / 36000);
  const m = Math.floor((tenths % 36000) / 600);
  const s = Math.floor((tenths % 600) / 10);
  const t = tenths % 10;
  const tail = `${s.toString().padStart(2, "0")}.${t}`;
  return h > 0 ? `${h}:${m.toString().padStart(2, "0")}:${tail}` : `${m}:${tail}`;
}

/** Parses "1:02:03.5", "2:03", "123.4" or "123". Returns null for anything else. */
export function parseTimecode(text: string): number | null {
  const trimmed = text.trim();
  if (!/^\d+(:\d{1,2}){0,2}(\.\d+)?$/.test(trimmed)) return null;
  const parts = trimmed.split(":");
  let total = 0;
  for (const part of parts) total = total * 60 + Number(part);
  return Number.isFinite(total) ? total : null;
}

/**
 * The `cut` URL parameter: removals as `start-end` seconds, comma separated, e.g.
 * `?cut=0-12.5,300-310.25`. Extensions link to a video with it to open the trim editor with those
 * removals filled in for the user to review.
 */
export function formatCutParam(ranges: readonly CutRange[]): string {
  const round = (value: number) => String(Math.round(value * 1000) / 1000);
  return ranges.map((range) => `${round(range.start)}-${round(range.end)}`).join(",");
}

/** Parses the `cut` URL parameter. Returns undefined when it is missing or any part is malformed. */
export function parseCutParam(value: string | null | undefined): CutRange[] | undefined {
  if (!value) return undefined;
  const ranges: CutRange[] = [];
  for (const part of value.split(",")) {
    const match = /^\s*(\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)\s*$/.exec(part);
    if (!match) return undefined;
    const start = Number(match[1]);
    const end = Number(match[2]);
    if (!(end > start)) return undefined;
    ranges.push({ start, end });
  }
  return ranges.length > 0 ? ranges : undefined;
}
