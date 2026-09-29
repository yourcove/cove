import { ArrowRight, Heart, Play } from "lucide-react";
import { memo } from "react";
import type { PerformerPairingVideo } from "../../api/types";
import { sharedVideosRoute, type Pairing } from "../../utils/performerPairings";
import { PerformerGenderIcon } from "../EntityCards";
import {
  CareerStrip,
  CoStarPortrait,
  PairingVideoThumb,
  RouteLink,
  performerRoute,
  videoRoute,
  type PairingNavigate,
} from "./pairingParts";

// The count on the left already says how many videos a pairing has, so its actions stay quiet icon
// buttons with their full labels as tooltips.
const ROW_ACTION_CLASS =
  "inline-flex h-9 w-9 items-center justify-center rounded-lg border border-border text-secondary transition-colors hover:border-accent/60 hover:text-foreground";

interface RankedPairingsViewProps {
  performerId: number;
  /** The current page of the sorted list, already narrowed by every filter. */
  pairings: Pairing[];
  /** The Videos-list filter the pairings were counted with, carried into the links to their videos. */
  videoFilter: Record<string, unknown>;
  /** The rank of the first co-star on this page, so the list numbers on from the previous page. */
  firstRank: number;
  duoOnly: boolean;
  careerFirstYear: number | null;
  careerLastYear: number | null;
  selectedIds: ReadonlySet<number>;
  onToggleSelected: (coStarId: number) => void;
  onPlay: (videos: PerformerPairingVideo[]) => void;
  onNavigate: PairingNavigate;
}

function plural(count: number, one: string, many: string) {
  return `${count} ${count === 1 ? one : many}`;
}

function spanLabel(pairing: Pairing) {
  if (pairing.firstYear == null || pairing.lastYear == null) return "No dates";
  if (pairing.firstYear === pairing.lastYear) return `All in ${pairing.firstYear}`;
  const years = pairing.lastYear - pairing.firstYear;
  return `${pairing.firstYear} – ${pairing.lastYear} · ${plural(years, "year", "years")}`;
}

function castLabel(pairing: Pairing) {
  const parts: string[] = [];
  if (pairing.duoCount > 0) parts.push(`${pairing.duoCount} as a duo`);
  if (pairing.groupCount > 0) parts.push(`${pairing.groupCount} with others`);
  return parts.join(" · ");
}

function studiosLabel(pairing: Pairing, limit: number) {
  if (pairing.studios.length === 0) return null;
  const shown = pairing.studios.slice(0, limit).map((studio) => `${studio.name} ${studio.count}`);
  const rest = pairing.studios.length - limit;
  return rest > 0 ? `${shown.join(" · ")} · +${rest} more` : shown.join(" · ");
}

// How much of the co-star's own catalogue this pairing is, which tells a regular partner from a
// busy performer who happened to share a few videos.
function shareLabel(pairing: Pairing) {
  const total = pairing.coStar.videoCount ?? 0;
  if (total <= 0 || pairing.count > total) return null;
  return pairing.count === total
    ? `All ${plural(total, "video", "videos")} they appear in`
    : `${pairing.count} of the ${total} videos they appear in`;
}

const PairingRow = memo(function PairingRow({
  performerId,
  pairing,
  duoOnly,
  videoFilter,
  selected,
  onToggleSelected,
  onPlay,
  onNavigate,
  careerFirstYear,
  careerLastYear,
}: {
  performerId: number;
  pairing: Pairing;
  duoOnly: boolean;
  videoFilter: Record<string, unknown>;
  selected: boolean;
  onToggleSelected: (coStarId: number) => void;
  onPlay: (videos: PerformerPairingVideo[]) => void;
  onNavigate: PairingNavigate;
  careerFirstYear: number | null;
  careerLastYear: number | null;
}) {
  const { coStar } = pairing;
  const studios = studiosLabel(pairing, 3);
  const share = shareLabel(pairing);
  const single = pairing.count === 1;
  const playLabel = single ? "Play" : "Play all";
  const viewLabel = single ? "Open video" : `View all ${pairing.count} videos`;
  return (
    <article
      className={`rounded-xl border bg-card p-4 transition-colors ${selected ? "border-accent" : "border-border hover:border-accent/50"}`}
    >
      <div className="flex flex-col gap-5 lg:grid lg:grid-cols-[8.5rem_minmax(0,17rem)_minmax(0,1fr)] lg:gap-6">
        <div className="relative w-28 lg:w-auto">
          <RouteLink
            route={performerRoute(coStar.id)}
            onNavigate={onNavigate}
            ariaLabel={coStar.name}
            className="block"
          >
            <CoStarPortrait coStar={coStar} className="aspect-[3/4] w-full rounded-xl" />
          </RouteLink>
          <label className="absolute left-2 top-2 flex h-7 w-7 cursor-pointer items-center justify-center rounded-md bg-black/60">
            <input
              type="checkbox"
              checked={selected}
              onChange={() => onToggleSelected(coStar.id)}
              aria-label={`Add ${coStar.name} to the lineup`}
              className="h-4 w-4 cursor-pointer accent-[var(--color-accent)]"
            />
          </label>
          {coStar.favorite ? (
            <Heart
              aria-label="Favorite"
              className="absolute right-2 top-2 h-4 w-4 fill-red-500 text-red-500 drop-shadow-md"
            />
          ) : null}
        </div>
        <div className="flex min-w-0 flex-col gap-2.5">
          <div className="flex min-w-0 items-center gap-2">
            <RouteLink
              route={performerRoute(coStar.id)}
              onNavigate={onNavigate}
              className="truncate text-lg font-bold text-foreground hover:text-accent"
            >
              {coStar.name}
            </RouteLink>
            <PerformerGenderIcon gender={coStar.gender ?? undefined} />
          </div>
          <div className="flex items-center gap-3">
            <span className="text-5xl font-extrabold leading-none tracking-tight text-foreground tabular-nums">
              {pairing.count}
            </span>
            <span className="flex flex-col">
              <span className="text-sm font-semibold text-foreground">
                {single ? "video together" : "videos together"}
              </span>
              <span className="text-xs text-muted">{spanLabel(pairing)}</span>
            </span>
          </div>
          <CareerStrip yearCounts={pairing.yearCounts} firstYear={careerFirstYear} lastYear={careerLastYear} />
          <p className="text-sm text-secondary">{castLabel(pairing)}</p>
          {studios ? (
            <p
              className="truncate text-sm text-muted"
              title={pairing.studios.map((studio) => `${studio.name} ${studio.count}`).join(", ")}
            >
              {studios}
            </p>
          ) : null}
          {share ? <p className="text-xs text-muted">{share}</p> : null}
        </div>
        <div className="flex min-w-0 flex-col gap-3">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[11px] font-semibold uppercase tracking-wider text-muted">
              {single ? "Together in" : "Latest together"}
            </span>
            <span className="flex-1" />
            <button
              type="button"
              onClick={() => onPlay(pairing.videos)}
              title={playLabel}
              aria-label={playLabel}
              className={ROW_ACTION_CLASS}
            >
              <Play aria-hidden="true" className="h-4 w-4 fill-current" />
            </button>
            <RouteLink
              route={
                single
                  ? videoRoute(pairing.videos[0].id)
                  : sharedVideosRoute(performerId, [coStar.id], { duoOnly, videoFilter })
              }
              onNavigate={onNavigate}
              title={viewLabel}
              ariaLabel={viewLabel}
              className={ROW_ACTION_CLASS}
            >
              <ArrowRight aria-hidden="true" className="h-4 w-4" />
            </RouteLink>
          </div>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-5">
            {pairing.videos.slice(0, 5).map((video) => (
              <PairingVideoThumb key={video.id} video={video} onNavigate={onNavigate} />
            ))}
          </div>
        </div>
      </div>
    </article>
  );
});

export function RankedPairingsView({
  performerId,
  pairings,
  videoFilter,
  firstRank,
  duoOnly,
  careerFirstYear,
  careerLastYear,
  selectedIds,
  onToggleSelected,
  onPlay,
  onNavigate,
}: RankedPairingsViewProps) {
  return (
    <ol aria-label="Co-stars" start={firstRank} className="flex flex-col gap-3">
      {pairings.map((pairing) => (
        <li key={pairing.coStar.id}>
          <PairingRow
            performerId={performerId}
            pairing={pairing}
            duoOnly={duoOnly}
            videoFilter={videoFilter}
            selected={selectedIds.has(pairing.coStar.id)}
            onToggleSelected={onToggleSelected}
            onPlay={onPlay}
            onNavigate={onNavigate}
            careerFirstYear={careerFirstYear}
            careerLastYear={careerLastYear}
          />
        </li>
      ))}
    </ol>
  );
}
