import { LayoutGrid, Sparkles } from "lucide-react";

const GENERATED_TITLE = "Generated cover: Generate made the frame at this segment's start";
const SPRITE_TITLE = "No generated cover yet: a scrub sprite tile stands in";

/**
 * Whether a segment's cover frame has been generated. Hidden when unknown, which includes segments
 * with a chosen cover.
 */
export function SegmentCoverBadge({ hasGeneratedCover }: { hasGeneratedCover?: boolean | null }) {
  if (hasGeneratedCover == null) return null;

  const Icon = hasGeneratedCover ? Sparkles : LayoutGrid;
  return (
    <span
      role="img"
      aria-label={hasGeneratedCover ? GENERATED_TITLE : SPRITE_TITLE}
      className={`inline-flex items-center gap-1 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-medium ${hasGeneratedCover ? "text-emerald-300" : "text-white/70"}`}
    >
      <Icon className="h-3 w-3" />
      {hasGeneratedCover ? "Generated" : "Sprite"}
    </span>
  );
}

/** The same status as a list pill, for rows without a cover overlay. */
export function SegmentCoverPill({ hasGeneratedCover }: { hasGeneratedCover?: boolean | null }) {
  if (hasGeneratedCover == null) return null;

  const Icon = hasGeneratedCover ? Sparkles : LayoutGrid;
  return (
    <span
      title={hasGeneratedCover ? GENERATED_TITLE : SPRITE_TITLE}
      className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 ${hasGeneratedCover ? "border-emerald-500/40 text-emerald-400" : "border-border text-muted"}`}
    >
      <Icon className="h-3 w-3" />
      {hasGeneratedCover ? "Generated cover" : "Sprite cover"}
    </span>
  );
}
