import { UserRound, Users } from "lucide-react";
import type { ReactNode } from "react";
import { entityImages } from "../../api/client";
import type { PerformerPairingVideo } from "../../api/types";
import type { Route } from "../../router/location";
import { pairingVideoTitle } from "../../utils/performerPairings";
import { createNestedRouteLinkProps } from "../cardNavigation";
import { CoverImage } from "../EntityCards";
import { formatDuration, getResolutionLabel } from "../shared";

export type PairingNavigate = (route: Route) => void;

export function performerRoute(performerId: number): Route {
  return { page: "performer", id: performerId };
}

export function videoRoute(videoId: number): Route {
  return { page: "video", id: videoId };
}

export function RouteLink({
  route,
  onNavigate,
  className,
  title,
  ariaLabel,
  children,
}: {
  route: Route;
  onNavigate: PairingNavigate;
  className?: string;
  title?: string;
  ariaLabel?: string;
  children: ReactNode;
}) {
  const link = createNestedRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate(route));
  return (
    <a {...link} className={className} title={title} aria-label={ariaLabel}>
      {children}
    </a>
  );
}

export function CoStarPortrait({ coStar, className }: { coStar: { imagePath?: string | null }; className: string }) {
  if (coStar.imagePath) {
    return <CoverImage src={coStar.imagePath} alt="" loading="lazy" className={`${className} bg-surface`} />;
  }
  return (
    <span className={`${className} flex items-center justify-center bg-surface text-muted`} aria-hidden="true">
      <UserRound className="h-1/3 w-1/3" />
    </span>
  );
}

export function PairingVideoThumb({
  video,
  onNavigate,
}: {
  video: PerformerPairingVideo;
  onNavigate: PairingNavigate;
}) {
  const title = pairingVideoTitle(video);
  const resolution = getResolutionLabel(video.width, video.height);
  const castSize = video.performerIds.length;
  const meta = [video.date, video.studioName].filter(Boolean).join(" · ");
  return (
    <RouteLink route={videoRoute(video.id)} onNavigate={onNavigate} className="group block min-w-0">
      <span className="relative block aspect-video overflow-hidden rounded-lg bg-surface ring-accent transition group-hover:ring-2">
        <CoverImage
          src={entityImages.videoCoverUrl(video.id, video.updatedAt, 480)}
          alt=""
          loading="lazy"
          className="h-full w-full"
        />
        {resolution ? (
          <span className="absolute left-1.5 top-1.5 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-bold text-white">
            {resolution}
          </span>
        ) : null}
        {castSize > 2 ? (
          <span
            className="absolute right-1.5 top-1.5 inline-flex items-center gap-1 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-bold text-white"
            title={`${castSize} people in this video`}
          >
            <Users aria-hidden="true" className="h-2.5 w-2.5" />
            {castSize}
          </span>
        ) : null}
        {video.duration > 0 ? (
          <span className="absolute bottom-1.5 right-1.5 rounded bg-black/70 px-1.5 py-0.5 text-[10px] font-semibold text-white">
            {formatDuration(video.duration)}
          </span>
        ) : null}
      </span>
      <span className="mt-1.5 block truncate text-sm font-semibold text-foreground group-hover:text-accent">
        {title}
      </span>
      <span className="block truncate text-xs text-muted">{meta || "\u00a0"}</span>
    </RouteLink>
  );
}

// Bars in a career strip. A longer career puts several years in each bar so the strip keeps its width.
const MAX_STRIP_BARS = 40;

/** Videos together per year across the performer's career, as a row of small bars. */
export function CareerStrip({
  yearCounts,
  firstYear,
  lastYear,
}: {
  yearCounts: ReadonlyMap<number, number>;
  firstYear: number | null;
  lastYear: number | null;
}) {
  if (firstYear == null || lastYear == null) return null;
  const yearsPerBar = Math.ceil((lastYear - firstYear + 1) / MAX_STRIP_BARS);
  const bars: Array<{ label: string; count: number }> = [];
  for (let start = firstYear; start <= lastYear; start += yearsPerBar) {
    const end = Math.min(lastYear, start + yearsPerBar - 1);
    let count = 0;
    for (let year = start; year <= end; year += 1) count += yearCounts.get(year) ?? 0;
    bars.push({ label: start === end ? String(start) : `${start}–${end}`, count });
  }
  const most = Math.max(1, ...bars.map((bar) => bar.count));
  return (
    <div className="flex items-end gap-2" title={`Videos together per year, ${firstYear} to ${lastYear}`}>
      <span className="text-[11px] leading-none text-muted">{firstYear}</span>
      <div className="flex h-6 min-w-0 flex-1 items-end gap-[2px]" aria-hidden="true">
        {bars.map((bar) => (
          <span
            key={bar.label}
            title={bar.count > 0 ? `${bar.label}: ${bar.count} ${bar.count === 1 ? "video" : "videos"}` : bar.label}
            className={`min-w-[2px] flex-1 rounded-sm ${bar.count > 0 ? "bg-accent" : "bg-border"}`}
            style={{ height: bar.count > 0 ? `${Math.max(30, Math.round((bar.count / most) * 100))}%` : "3px" }}
          />
        ))}
      </div>
      <span className="text-[11px] leading-none text-muted">{lastYear}</span>
    </div>
  );
}

export function FilterChip({
  pressed,
  onClick,
  children,
  title,
}: {
  pressed: boolean;
  onClick: () => void;
  children: ReactNode;
  title?: string;
}) {
  return (
    <button
      type="button"
      aria-pressed={pressed}
      title={title}
      onClick={onClick}
      className={`inline-flex min-h-8 items-center gap-1.5 rounded-full border px-3 text-xs transition-colors ${
        pressed
          ? "border-accent bg-accent/15 font-semibold text-accent"
          : "border-border text-secondary hover:border-accent/50 hover:text-foreground"
      }`}
    >
      {children}
    </button>
  );
}
