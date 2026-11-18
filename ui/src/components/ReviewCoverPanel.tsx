import { useEffect, useId, useState, type ReactNode } from "react";
import { AlertTriangle, ArrowUpRight, Check, ChevronLeft, ChevronRight } from "lucide-react";
import type { DiffSide, ScalarStatus } from "./MetadataDiff";
import { LoadedImage } from "./LoadedImage";

/**
 * The cover of a scrape review, first and large. A conflict shows the current and the incoming cover
 * at the same size side by side with the one that will be used framed; a fill or an identical cover
 * shows one. When the source offers several images they are candidates to browse: only the one on
 * screen is loaded, its neighbours are fetched once it is usable, and the one on screen when the
 * person applies is the one that is stored.
 */
export function ReviewCoverPanel({
  status,
  chosen,
  currentUrl,
  candidates,
  activeIndex = 0,
  onActiveIndexChange,
  incomingLabel,
  onChoose,
  disabled = false,
  aspect = "video",
  subject = "Cover",
  note,
}: {
  status: ScalarStatus;
  chosen: DiffSide;
  currentUrl?: string | null;
  /** Incoming images in the source's order; one entry for a source with a single image. */
  candidates: string[];
  activeIndex?: number;
  onActiveIndexChange?: (index: number) => void;
  /** How the source reads in "Use …", e.g. "StashDB". */
  incomingLabel: string;
  onChoose: (side: DiffSide) => void;
  disabled?: boolean;
  aspect?: "video" | "portrait";
  subject?: string;
  /**
   * Replaces the "both have a value" sentence under a conflict, for a choice the caller can say
   * something more useful about — such as two covers that are the same picture at different sizes.
   */
  note?: string;
}) {
  const name = `${useId()}-cover`;
  const index = Math.min(Math.max(activeIndex, 0), Math.max(candidates.length - 1, 0));
  const incomingUrl = candidates[index] ?? "";
  const portrait = aspect === "portrait";
  // Contain, not cover: the point of the panel is to judge the whole image, so nothing may be cropped away.
  const imageClass = `${portrait ? "aspect-[2/3]" : "aspect-video"} w-full rounded-md bg-black object-contain`;
  // Two portraits fit side by side even on a phone; two landscape covers stack there.
  const singleWidth = portrait ? "w-44 sm:w-52" : "w-full sm:w-60";
  const optionWidth = portrait ? "w-1/2 sm:w-52" : "w-full sm:w-56";
  const browser =
    candidates.length > 1 && onActiveIndexChange ? (
      <CandidateBrowser
        index={index}
        count={candidates.length}
        disabled={disabled}
        onChange={onActiveIndexChange}
        subject={subject}
      />
    ) : null;
  const incoming = (
    <CandidateImage
      url={incomingUrl}
      neighbours={
        candidates.length > 1
          ? [
              candidates[(index - 1 + candidates.length) % candidates.length],
              candidates[(index + 1) % candidates.length],
            ]
          : []
      }
      alt={`${subject} from ${incomingLabel}`}
      className={imageClass}
      overlay={browser}
    />
  );

  if (status !== "conflict" && !incomingUrl && !currentUrl) return null;
  if (status !== "conflict") {
    const showsIncoming = chosen === "source" || !currentUrl;
    const statusNote =
      status === "identical"
        ? `same ${subject.toLowerCase()}`
        : status === "filled"
          ? "fills empty"
          : "keeping current";
    return (
      <div className={`flex shrink-0 flex-col gap-1.5 ${singleWidth}`}>
        {showsIncoming ? (
          incoming
        ) : (
          <CandidateImage
            url={currentUrl ?? ""}
            neighbours={[]}
            alt={`Current ${subject.toLowerCase()}`}
            className={imageClass}
          />
        )}
        <span className="inline-flex items-center gap-1 text-[11px] text-secondary">
          <Check className="h-3 w-3 text-green-400" />
          {subject} · {statusNote}
        </span>
      </div>
    );
  }

  const option = (side: DiffSide, image: ReactNode, label: string) => {
    const active = chosen === side;
    return (
      <label
        key={side}
        className={`flex min-w-0 cursor-pointer flex-col gap-1 rounded-lg border p-1 transition-colors ${optionWidth} ${
          active ? "border-accent bg-accent/10" : "border-transparent opacity-70 hover:opacity-100"
        } ${disabled ? "cursor-default" : ""}`}
      >
        {image}
        <span className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5 px-1 text-[11px]">
          <input
            type="radio"
            name={name}
            aria-label={label}
            checked={active}
            disabled={disabled}
            onChange={() => onChoose(side)}
            className="accent-accent"
          />
          <span className={active ? "text-foreground" : "text-secondary"}>{label}</span>
          {active ? (
            <span className="ml-auto rounded-full bg-accent/20 px-1.5 py-px text-[10px] font-semibold text-accent">
              Will be used
            </span>
          ) : null}
        </span>
      </label>
    );
  };
  return (
    <div role="radiogroup" aria-label={`${subject} choice`} className="flex w-full shrink-0 flex-col gap-1 sm:w-auto">
      <div className={`flex gap-2 ${portrait ? "flex-row" : "flex-col sm:flex-row"}`}>
        {option(
          "target",
          <CandidateImage
            url={currentUrl ?? ""}
            neighbours={[]}
            alt={`Current ${subject.toLowerCase()}`}
            className={imageClass}
          />,
          "Keep current",
        )}
        {option("source", incoming, `Use ${incomingLabel}`)}
      </div>
      <span className="inline-flex items-center gap-1 px-1 text-[11px] text-secondary">
        {note ? <ArrowUpRight className="h-3 w-3 text-accent" /> : <AlertTriangle className="h-3 w-3 text-amber-400" />}
        {subject} · {note ?? "both have a value"}
      </span>
    </div>
  );
}

function CandidateBrowser({
  index,
  count,
  disabled,
  onChange,
  subject,
}: {
  index: number;
  count: number;
  disabled: boolean;
  onChange: (index: number) => void;
  subject: string;
}) {
  const step = (delta: number) => onChange((index + delta + count) % count);
  const buttonClass =
    "pointer-events-auto inline-flex h-6 w-6 items-center justify-center rounded-full bg-black/60 text-white hover:bg-black/80 disabled:opacity-50";
  return (
    <span className="pointer-events-none absolute inset-x-1 bottom-1 flex items-center justify-between">
      <button
        type="button"
        disabled={disabled}
        onClick={() => step(-1)}
        aria-label={`Previous ${subject.toLowerCase()}`}
        className={buttonClass}
      >
        <ChevronLeft className="h-3.5 w-3.5" />
      </button>
      <span aria-live="polite" className="rounded-full bg-black/60 px-2 py-px text-[10px] font-semibold text-white">
        {index + 1} / {count}
      </span>
      <button
        type="button"
        disabled={disabled}
        onClick={() => step(1)}
        aria-label={`Next ${subject.toLowerCase()}`}
        className={buttonClass}
      >
        <ChevronRight className="h-3.5 w-3.5" />
      </button>
    </span>
  );
}

/** One image that gives way to a same-size placeholder when it cannot load, and warms its neighbours once it has. */
function CandidateImage({
  url,
  neighbours,
  alt,
  className,
  overlay = null,
}: {
  url: string;
  neighbours: (string | undefined)[];
  alt: string;
  className: string;
  overlay?: ReactNode;
}) {
  const [failed, setFailed] = useState<string | null>(null);
  const [loaded, setLoaded] = useState<string | null>(null);
  const neighbourKey = neighbours.filter(Boolean).join("\n");
  useEffect(() => {
    if (loaded !== url || !neighbourKey) return;
    for (const neighbour of neighbourKey.split("\n")) new Image().src = neighbour;
  }, [loaded, url, neighbourKey]);
  return (
    <span className="relative block">
      {!url || failed === url ? (
        <span
          className={`${className} flex items-center justify-center text-[11px] text-muted`}
          data-testid="cover-placeholder"
        >
          {url ? "Could not load" : "No cover"}
        </span>
      ) : (
        <LoadedImage
          key={url}
          src={url}
          alt={alt}
          className={className}
          loading="eager"
          decoding="async"
          onLoad={() => setLoaded(url)}
          onError={() => setFailed(url)}
        />
      )}
      {overlay}
    </span>
  );
}
