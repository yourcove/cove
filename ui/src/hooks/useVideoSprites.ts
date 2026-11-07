import { useEffect, useState, type CSSProperties } from "react";
import { serverAwareFetch } from "../state/serverAvailability";

export interface VideoSpriteEntry {
  start: number;
  end: number;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface VideoSprites {
  entries: VideoSpriteEntry[];
  imageUrl: string;
  /** Full sprite sheet size in source pixels, used to scale tiles to a display width. */
  sheetWidth: number;
  sheetHeight: number;
  /**
   * Cove captures each tile at, or at the keyframe just before, the middle of its interval. Stash, whose sprites
   * the Stash import copies over unchanged, captures each tile at the start of its interval.
   */
  capturedMidTile: boolean;
}

function parseVttTime(timeStr: string): number {
  const parts = timeStr.split(":");
  return parseInt(parts[0]) * 3600 + parseInt(parts[1]) * 60 + parseFloat(parts[2]);
}

export function parseSpriteVtt(text: string): VideoSpriteEntry[] {
  const entries: VideoSpriteEntry[] = [];
  const blocks = text.split(/\n\n+/);
  for (const block of blocks) {
    const lines = block.trim().split("\n");
    for (let i = 0; i < lines.length; i++) {
      const timeMatch = lines[i].match(/(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})/);
      if (timeMatch && lines[i + 1]) {
        const xywhMatch = lines[i + 1].match(/#xywh=(\d+),(\d+),(\d+),(\d+)/);
        if (xywhMatch) {
          entries.push({
            start: parseVttTime(timeMatch[1]),
            end: parseVttTime(timeMatch[2]),
            x: parseInt(xywhMatch[1]),
            y: parseInt(xywhMatch[2]),
            w: parseInt(xywhMatch[3]),
            h: parseInt(xywhMatch[4]),
          });
        }
      }
    }
  }
  return entries;
}

/**
 * Whether Cove made a sprite sheet. Cove names the image after the video id; Stash names it after a 16- or
 * 32-character file hash, and the Stash import copies its sprite files unchanged. A Stash hash can be all digits,
 * so only ids shorter than 16 digits count as Cove names.
 */
export function isCoveSpriteVtt(text: string): boolean {
  const imageName = text.match(/^\s*(\S+?)#xywh=/m)?.[1];
  return imageName != null && /^\d{1,15}_sprite\.jpg$/i.test(imageName);
}

/** The media time a tile's frame was captured at. */
export function spriteCaptureTime(sprites: VideoSprites, entry: VideoSpriteEntry): number {
  // Round the middle to milliseconds to keep float noise out of URLs; it stays well inside the tile. A start is
  // returned exactly, since the server matches it against the same tile.
  return sprites.capturedMidTile ? Math.round(((entry.start + entry.end) / 2) * 1000) / 1000 : entry.start;
}

/** Returns the index of the last tile that starts at or before `time`; earlier times use the first tile. */
export function findSpriteIndex(entries: VideoSpriteEntry[], time: number): number {
  for (let i = entries.length - 1; i > 0; i--) {
    if (time >= entries[i].start) return i;
  }
  return 0;
}

/** Background style that shows one sprite tile scaled to `width` pixels wide. */
export function spriteTileStyle(sprites: VideoSprites, entry: VideoSpriteEntry, width: number): CSSProperties {
  const scale = width / entry.w;
  return {
    width,
    height: Math.round(entry.h * scale),
    backgroundImage: `url(${sprites.imageUrl})`,
    backgroundPosition: `-${entry.x * scale}px -${entry.y * scale}px`,
    backgroundSize: `${sprites.sheetWidth * scale}px ${sprites.sheetHeight * scale}px`,
  };
}

/**
 * Loads the scrubber sprite sheet for a video. Returns null while loading, when the video has no
 * sprites, when the sprite list could not be fetched, or when `videoId` is null.
 */
export function useVideoSprites(videoId: number | null): VideoSprites | null {
  return useVideoSpritesLoad(videoId).sprites;
}

export type VideoSpritesLoad =
  | { status: "idle" | "loading" | "missing" | "failed"; sprites: null }
  | { status: "ready"; sprites: VideoSprites };

/**
 * Loads the scrubber sprite sheet for a video and reports how the load went. `missing` means the video has no
 * sprites (no list, or an empty one); `failed` means the list could not be fetched, which may be temporary.
 * The status is `idle` when `videoId` is null.
 */
export function useVideoSpritesLoad(videoId: number | null): VideoSpritesLoad {
  const [loaded, setLoaded] = useState<{ videoId: number; load: VideoSpritesLoad } | null>(null);

  useEffect(() => {
    if (videoId == null) return;
    let cancelled = false;
    const imageUrl = `/api/stream/video/${videoId}/sprite`;
    const settle = (load: VideoSpritesLoad) => {
      if (!cancelled) setLoaded({ videoId, load });
    };

    serverAwareFetch(`/api/stream/video/${videoId}/vtt/thumbs`)
      .then(async (response) => {
        if (response.status === 404) return settle({ status: "missing", sprites: null });
        if (!response.ok) return settle({ status: "failed", sprites: null });
        const text = await response.text();
        const entries = parseSpriteVtt(text);
        if (entries.length === 0) return settle({ status: "missing", sprites: null });
        settle({
          status: "ready",
          sprites: {
            entries,
            imageUrl,
            sheetWidth: Math.max(...entries.map((entry) => entry.x + entry.w)),
            sheetHeight: Math.max(...entries.map((entry) => entry.y + entry.h)),
            capturedMidTile: isCoveSpriteVtt(text),
          },
        });
      })
      .catch(() => settle({ status: "failed", sprites: null }));

    return () => {
      cancelled = true;
    };
  }, [videoId]);

  if (videoId == null) return { status: "idle", sprites: null };
  // Drop the previous video's sprites as soon as the video changes, before its own load finishes.
  if (loaded == null || loaded.videoId !== videoId) return { status: "loading", sprites: null };
  return loaded.load;
}
