import { Plus, Settings2 } from "lucide-react";

/**
 * The outer dimensions of a dashboard recommendation card. Real cards and their loading skeletons
 * share these, so a row keeps its height when data replaces the skeleton.
 */
export interface RecommendationCardShape {
  width: string;
  /** Aspect ratio of the cover image box. */
  media: string;
  /** Minimum height of the text block under the cover; it reserves an optional secondary line. */
  body: string;
  /** Whether the card ends with a stats row. */
  stats: boolean;
}

// py-1.5 plus a text-sm title line, and for two lines a text-xs secondary line.
const ONE_LINE_BODY = "min-h-[2rem]";
const TWO_LINE_BODY = "min-h-[3rem]";

/** pb-1.5 plus one text-xs line. */
export const RECOMMENDATION_CARD_STATS_ROW = "min-h-[1.375rem]";

export const RECOMMENDATION_CARD_SHAPES = {
  video: { width: "w-[200px]", media: "aspect-video", body: TWO_LINE_BODY, stats: true },
  performer: { width: "w-[160px]", media: "aspect-[2/3]", body: TWO_LINE_BODY, stats: false },
  studio: { width: "w-[200px]", media: "aspect-video", body: ONE_LINE_BODY, stats: false },
  tag: { width: "w-[160px]", media: "aspect-video", body: ONE_LINE_BODY, stats: false },
  gallery: { width: "w-[200px]", media: "aspect-video", body: TWO_LINE_BODY, stats: false },
  group: { width: "w-[160px]", media: "aspect-[2/3]", body: TWO_LINE_BODY, stats: false },
  audio: { width: "w-[200px]", media: "aspect-video", body: TWO_LINE_BODY, stats: false },
  text: { width: "w-[200px]", media: "aspect-video", body: TWO_LINE_BODY, stats: false },
  segment: { width: "w-[220px]", media: "aspect-video", body: TWO_LINE_BODY, stats: false },
  groupItem: { width: "w-[220px]", media: "aspect-video", body: ONE_LINE_BODY, stats: false },
} satisfies Record<string, RecommendationCardShape>;

/** Enough placeholders to overflow a wide row, so its carousel page dots appear while loading too. */
const SKELETON_CARD_COUNT = 10;

const PULSE = "motion-safe:animate-pulse";

export function RecommendationCardSkeleton({ shape }: { shape: RecommendationCardShape }) {
  return (
    <div
      aria-hidden="true"
      data-card-skeleton=""
      className={`${shape.width} flex-shrink-0 overflow-hidden rounded border border-border bg-card`}
      // Snap like the real cards do, so the carousel settles at the same scroll offset.
      style={{ scrollSnapAlign: "start" }}
    >
      <div className={`${shape.media} bg-surface ${PULSE}`} />
      <div className={`px-2 py-1.5 ${shape.body}`}>
        <div className="flex h-5 items-center">
          <div className={`h-3 w-3/4 rounded bg-surface ${PULSE}`} />
        </div>
      </div>
      {shape.stats ? <div className={`px-2 pb-1.5 ${RECOMMENDATION_CARD_STATS_ROW}`} /> : null}
    </div>
  );
}

export function recommendationCardSkeletons(shape: RecommendationCardShape) {
  return Array.from({ length: SKELETON_CARD_COUNT }, (_, index) => (
    <RecommendationCardSkeleton key={index} shape={shape} />
  ));
}

/**
 * A recommendation row whose source has not resolved yet, so neither its title nor its items are
 * known. It mirrors the row header, cards, and page dots of a loaded row.
 */
export function RecommendationRowSkeleton({
  shape = RECOMMENDATION_CARD_SHAPES.video,
}: {
  shape?: RecommendationCardShape;
}) {
  return (
    <div className="recommendation-row" aria-busy="true" data-row-skeleton="">
      <div className="mb-2 flex items-center justify-between px-1">
        <div className="flex h-6 items-center">
          <div className={`h-4 w-40 rounded bg-card ${PULSE}`} />
        </div>
        <span
          aria-hidden="true"
          className="invisible inline-flex min-h-9 items-center px-2 text-sm sm:min-h-0 sm:px-0 sm:text-xs"
        >
          View All
        </span>
      </div>
      <div className="flex gap-2 overflow-x-auto scrollbar-hide px-1" style={{ scrollSnapType: "x mandatory" }}>
        {recommendationCardSkeletons(shape)}
      </div>
      <div aria-hidden="true" className="mt-2 h-8 sm:h-1" />
    </div>
  );
}

/** The home dashboard before its layout is known: the switcher, its actions, and a few rows. */
export function DashboardSkeleton() {
  return (
    <div className="space-y-5" role="status" aria-busy="true" data-dashboard-skeleton="">
      <span className="sr-only">Loading dashboard</span>
      <div className="flex items-center justify-between gap-3">
        <ol aria-hidden="true" className="flex min-w-0 flex-1 items-center py-1 text-lg sm:text-xl">
          <li className="block py-1">
            <span className={`inline-block h-[0.8em] w-24 rounded bg-card align-middle ${PULSE}`} />
          </li>
        </ol>
        <div aria-hidden="true" className="flex shrink-0 gap-2">
          <span className="inline-block rounded-md border border-border px-2.5 py-2 text-sm sm:px-3">
            <span className="invisible">
              <Plus className="inline h-4 w-4 sm:mr-1" />
              <span className="sr-only sm:not-sr-only">New Dashboard</span>
            </span>
          </span>
          <span className="inline-block rounded-md border border-border bg-card px-2.5 py-2 text-sm sm:px-3">
            <span className="invisible">
              <Settings2 className="inline h-4 w-4 sm:mr-1" />
              <span className="sr-only sm:not-sr-only">Customize</span>
            </span>
          </span>
        </div>
      </div>
      <div className="space-y-5">
        <RecommendationRowSkeleton />
        <RecommendationRowSkeleton />
        <RecommendationRowSkeleton />
      </div>
    </div>
  );
}
