import type { CSSProperties, MouseEvent, ReactNode } from "react";
import { InteractiveRating } from "./Rating";

export interface FeedMediaDimensions {
  width?: number | null;
  height?: number | null;
}

export function getFeedMediaStyle(media: FeedMediaDimensions | undefined): CSSProperties | undefined {
  if (!media?.width || !media.height || media.height <= media.width) {
    return undefined;
  }

  const ratio = media.width / media.height;
  return { maxWidth: `min(100%, ${80 * ratio}vh, 36rem)` };
}

interface FeedPortraitMediaFrameProps {
  title: string;
  backgroundSrc?: string | null;
  media: ReactNode;
  children?: ReactNode;
  className?: string;
}

export function FeedPortraitMediaFrame({ title, backgroundSrc, media, children, className }: FeedPortraitMediaFrameProps) {
  return (
    <div
      className={`relative h-[min(82dvh,58rem)] overflow-hidden rounded-2xl border border-border/70 bg-black/90 shadow-[0_18px_40px_rgba(0,0,0,0.35)] ${className ?? ""}`.trim()}
      title={title}
    >
      {backgroundSrc ? (
        <>
          <img src={backgroundSrc} alt="" aria-hidden="true" className="absolute inset-0 h-full w-full scale-110 object-cover opacity-45 blur-3xl" loading="lazy" />
          <div className="absolute inset-0 bg-black/35" />
        </>
      ) : null}
      <div className="relative z-0 h-full w-full p-1 sm:p-2">{media}</div>
      {children}
    </div>
  );
}

interface FeedCardFrameProps {
  dataAttribute?: Record<string, string | number>;
  selected?: boolean;
  identity?: ReactNode;
  header: ReactNode;
  headerActions?: ReactNode;
  media: ReactNode;
  title: ReactNode;
  details?: ReactNode;
  metadata?: ReactNode;
  chips?: ReactNode;
  onClick?: (event: MouseEvent<HTMLElement>) => void;
}

export function FeedCardFrame({ dataAttribute, selected, identity, header, headerActions, media, title, details, metadata, chips, onClick }: FeedCardFrameProps) {
  const attributeProps = dataAttribute ?? {};

  return (
    <article
      {...attributeProps}
      onClick={onClick}
      data-feed-card="true"
      className={`group border-b border-border/70 pb-5 transition-colors ${onClick ? "cursor-pointer" : ""} ${selected ? "bg-accent/5" : ""}`}
    >
      <div className="space-y-2 px-1 pb-2 pt-1 sm:px-2">
        <div className="flex items-start justify-between gap-3 text-xs text-muted">
          <div className="flex min-w-0 items-center gap-2">
            {identity ? <div className="min-w-0 shrink-0">{identity}</div> : null}
            <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 leading-tight">{header}</div>
          </div>
          {selected ? <span className="rounded-full border border-accent/40 bg-accent/10 px-2 py-1 text-[10px] font-semibold uppercase tracking-[0.16em] text-accent">Selected</span> : null}
        </div>

        <div className="space-y-1.5" data-feed-card-content="true">
          <div className="text-left text-base font-semibold leading-snug text-foreground sm:text-lg">{title}</div>
          {details ? <div className="text-sm leading-6 text-secondary">{details}</div> : null}
          {chips ? <div className="flex flex-wrap gap-1 overflow-visible text-[11px] text-muted">{chips}</div> : null}
        </div>
      </div>

      <div className="pb-2" data-feed-card-media="true">{media}</div>

      <div className="flex flex-wrap items-center gap-2 px-1 text-xs text-muted sm:px-2">
        {headerActions ? <div className="flex flex-wrap items-center gap-2 text-xs text-muted" data-feed-card-actions="true">{headerActions}</div> : null}
        {metadata ? <div className="flex flex-wrap gap-1.5 text-xs text-muted">{metadata}</div> : null}
      </div>
    </article>
  );
}

export function FeedIdentityBadge({ children }: { children: ReactNode }) {
  return <span className="inline-flex max-w-[11rem] items-center truncate rounded-full border border-border/70 bg-background/75 px-2.5 py-1 text-[11px] font-semibold text-foreground/90">{children}</span>;
}

export function FeedMetadataPill({ children }: { children: ReactNode }) {
  return <span className="rounded-full border border-border/70 bg-background/60 px-2 py-0.5 font-medium text-muted">{children}</span>;
}

export function FeedChipButton({ children, onClick }: { children: ReactNode; onClick: (event: MouseEvent<HTMLButtonElement>) => void }) {
  return (
    <button
      type="button"
      onClick={(event) => {
        event.stopPropagation();
        onClick(event);
      }}
      className="rounded-full border border-border/70 bg-background/50 px-2 py-0.5 font-medium text-secondary transition-colors hover:border-accent/50 hover:bg-background/80 hover:text-foreground"
    >
      {children}
    </button>
  );
}

export function FeedChipOverflowMenu({ children }: { children: ReactNode }) {
  return (
    <details
      className="group/chip-overflow relative inline-flex"
      onClick={(event) => event.stopPropagation()}
      onMouseDown={(event) => event.stopPropagation()}
    >
      <summary
        className="inline-flex h-[1.625rem] cursor-pointer list-none items-center rounded-full border border-border/70 bg-background/50 px-2.5 py-0.5 font-semibold text-secondary transition-colors hover:border-accent/50 hover:bg-background/80 hover:text-foreground [&::-webkit-details-marker]:hidden"
        aria-label="Show more tags"
        title="Show more tags"
      >
        ...
      </summary>
      <div className="absolute left-0 top-full z-30 mt-1 w-max max-w-[min(24rem,calc(100vw-2rem))] rounded-lg border border-border bg-surface p-2 text-[11px] shadow-xl">
        <div className="flex max-h-64 min-w-56 flex-col flex-nowrap gap-1 overflow-x-hidden overflow-y-scroll pr-1">
          {children}
        </div>
      </div>
    </details>
  );
}

export function FeedActionPill({ children }: { children: ReactNode }) {
  return <span className="inline-flex min-h-8 items-center gap-1.5 rounded-full bg-background/75 px-2.5 py-1 font-medium text-secondary">{children}</span>;
}

export function FeedInlineRating({ value, onChange, readOnly, pending }: { value?: number; onChange?: (value: number | undefined) => void; readOnly?: boolean; pending?: boolean }) {
  return (
    <span
      className="inline-flex min-h-8 items-center gap-2 rounded-full bg-background/75 px-2.5 py-1 font-medium text-secondary"
      onClick={(event) => event.stopPropagation()}
      onMouseDown={(event) => event.stopPropagation()}
    >
      <InteractiveRating value={value} onChange={onChange} readOnly={readOnly || pending} />
      {pending ? <span className="text-[10px] text-muted">Saving</span> : null}
    </span>
  );
}
