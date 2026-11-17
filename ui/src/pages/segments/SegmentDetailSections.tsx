import { Fragment, type ReactNode } from "react";
import type { EntityEngagement, Video } from "../../api/types";
import { SuggestedNameMarker, VideoCard } from "../../components/EntityCards";

// The building blocks the raw segment page and the segment (span) page share, so both read the same way: a summary
// line under the title, the video, the face for a face segment, how the page relates to the other view, what is
// nearby, and technical details last.

export function DetailSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="space-y-3">
      {/* The page title is an h3 in the detail layout. */}
      <h4 className="text-xs font-semibold uppercase tracking-wide text-muted">{title}</h4>
      {children}
    </section>
  );
}

export function DetailRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline gap-3">
      <dt className="w-20 shrink-0 text-muted">{label}</dt>
      <dd className="min-w-0 flex-1 text-foreground">{children}</dd>
    </div>
  );
}

export function EntityLink({
  label,
  ariaLabel,
  icon,
  onClick,
}: {
  label: string;
  ariaLabel?: string;
  icon?: ReactNode;
  onClick?: () => void;
}) {
  if (!onClick) {
    return (
      <span className="inline-flex min-w-0 items-center gap-1.5">
        {icon}
        <span className="truncate">{label}</span>
      </span>
    );
  }
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={ariaLabel}
      className="inline-flex min-w-0 max-w-full items-center gap-1.5 text-left text-accent hover:underline"
    >
      {icon}
      <span className="truncate">{label}</span>
    </button>
  );
}

export function DetailNotice({ children }: { children: ReactNode }) {
  return (
    <p className="rounded-lg border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-200">{children}</p>
  );
}

/** One line of short facts under the title, such as a link to the tag, the source and the confidence. */
export function SummaryLine({ items }: { items: ReactNode[] }) {
  const shown = items.filter((item) => item != null && item !== false && item !== "");
  if (shown.length === 0) return null;
  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-secondary">
      {shown.map((item, index) => (
        <Fragment key={index}>
          {index > 0 ? <span aria-hidden>·</span> : null}
          <span className="inline-flex min-w-0 items-center">{item}</span>
        </Fragment>
      ))}
    </div>
  );
}

/** The video the segment is in, shown the same way on both pages. */
export function VideoSection({
  video,
  videoTitle,
  engagement,
  position,
  onOpen,
  onNavigate,
}: {
  video?: Video;
  videoTitle: string;
  engagement?: EntityEngagement;
  /** Where the segment sits among the video's segments, e.g. "13 of 14 raw segments in this video". */
  position?: string;
  onOpen?: () => void;
  onNavigate: (route: any) => void;
}) {
  return (
    <DetailSection title="Video">
      {video ? (
        <div className="max-w-sm">
          <VideoCard video={video} engagement={engagement} onClick={onOpen ?? (() => {})} onNavigate={onNavigate} />
        </div>
      ) : (
        <EntityLink label={videoTitle} onClick={onOpen} />
      )}
      {position ? <p className="text-sm text-secondary">{position}</p> : null}
    </DetailSection>
  );
}

export interface NearbyItem {
  key: string | number;
  title: string;
  suggested?: boolean;
  range: string;
  meta?: string;
  icon?: ReactNode;
  onClick: () => void;
}

export interface NearbyGroup {
  title: string;
  items: NearbyItem[];
}

/** Segments around this one in the video; groups with nothing in them are left out. */
export function NearbySection({ groups }: { groups: NearbyGroup[] }) {
  const shown = groups.filter((group) => group.items.length > 0);
  if (shown.length === 0) return null;
  return (
    <DetailSection title="Nearby">
      <div className="space-y-3">
        {shown.map((group) => (
          <div key={group.title}>
            <div className="mb-1 text-xs text-muted">{group.title}</div>
            <ul className="divide-y divide-border overflow-hidden rounded-lg border border-border">
              {group.items.map((item) => (
                <li key={item.key}>
                  <button
                    type="button"
                    onClick={item.onClick}
                    className="flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm transition-colors hover:bg-surface/60"
                  >
                    <span className="flex min-w-0 items-center gap-1.5">
                      {item.icon}
                      {item.suggested ? (
                        <>
                          <span aria-hidden className="truncate text-foreground">
                            {item.title}
                          </span>
                          <SuggestedNameMarker />
                          <span className="sr-only">{`${item.title} (suggested)`}</span>
                        </>
                      ) : (
                        <span className="truncate text-foreground">{item.title}</span>
                      )}
                    </span>
                    <span className="shrink-0 text-xs text-secondary">
                      {item.range}
                      {item.meta ? ` · ${item.meta}` : ""}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>
    </DetailSection>
  );
}

/** Values for troubleshooting, collapsed by default. */
export function TechnicalDetails({ entries }: { entries: Array<[string, string | undefined]> }) {
  const shown = entries.filter((entry): entry is [string, string] => !!entry[1]);
  if (shown.length === 0) return null;
  return (
    <details className="text-xs text-secondary">
      <summary className="cursor-pointer select-none text-muted hover:text-foreground">Technical details</summary>
      <dl className="mt-2 space-y-1">
        {shown.map(([label, value]) => (
          <div key={label} className="flex gap-3">
            <dt className="w-28 shrink-0 text-muted">{label}</dt>
            <dd className="min-w-0 break-all font-mono">{value}</dd>
          </div>
        ))}
      </dl>
    </details>
  );
}

/** A confidence between 0 and 1 as a whole percentage. */
export function formatConfidencePercent(confidence?: number | null) {
  return confidence == null ? undefined : `${Math.round(confidence * 100)}%`;
}

// A name with its "suggested" marker. The marker is hidden from screen readers, so a suggested name is read as one
// phrase ending in "(suggested)" instead.
export function SuggestedName({
  name,
  suggested,
  className,
  markerClassName,
}: {
  name: string;
  suggested: boolean;
  className?: string;
  markerClassName?: string;
}) {
  if (!suggested) return <span className={className}>{name}</span>;
  return (
    <>
      <span aria-hidden className={className}>
        {name}
      </span>
      <SuggestedNameMarker className={markerClassName} />
      <span className="sr-only">{`${name} (suggested)`}</span>
    </>
  );
}
