import { ZoomIn, ZoomOut } from "lucide-react";
import type { CSSProperties } from "react";

export const MIN_WALL_SIZE_LEVEL = 2;
export const MAX_WALL_SIZE_LEVEL = 8;

export function clampWallSizeLevel(value: number) {
  if (!Number.isFinite(value)) return MIN_WALL_SIZE_LEVEL;
  return Math.min(MAX_WALL_SIZE_LEVEL, Math.max(MIN_WALL_SIZE_LEVEL, Math.round(value)));
}

export function getWallColumnCountFromSizeLevel(sizeLevel: number) {
  return 10 - clampWallSizeLevel(sizeLevel);
}

export function getWallSizeLevelFromColumnCount(columnCount: number) {
  return clampWallSizeLevel(10 - columnCount);
}

interface WallSizeControlProps {
  sizeLevel: number;
  onChange: (sizeLevel: number) => void;
}

export function WallSizeControl({ sizeLevel, onChange }: WallSizeControlProps) {
  const effectiveSizeLevel = clampWallSizeLevel(sizeLevel);
  const columnCount = getWallColumnCountFromSizeLevel(effectiveSizeLevel);

  return (
    <div className="hidden items-center gap-1 pl-1 md:flex">
      <ZoomOut className="w-3 h-3 text-muted" />
      <input
        type="range"
        min={MIN_WALL_SIZE_LEVEL}
        max={MAX_WALL_SIZE_LEVEL}
        step={1}
        value={effectiveSizeLevel}
        onChange={(event) => onChange(clampWallSizeLevel(Number(event.target.value)))}
        style={
          {
            "--range-fill": `${((effectiveSizeLevel - MIN_WALL_SIZE_LEVEL) / (MAX_WALL_SIZE_LEVEL - MIN_WALL_SIZE_LEVEL)) * 100}%`,
          } as CSSProperties
        }
        className="themed-range-input h-1 w-16 cursor-pointer sm:w-20"
        aria-label="Wall card size"
        title={`Wall card size: ${effectiveSizeLevel}`}
      />
      <ZoomIn className="w-3 h-3 text-muted" />
      <span className="min-w-[2.25rem] text-[10px] text-muted">{columnCount} cols</span>
    </div>
  );
}
