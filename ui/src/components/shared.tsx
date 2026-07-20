import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { AlertTriangle, MoreVertical, SlidersHorizontal } from "lucide-react";
import type { FieldProvenance, Tag, TagProvenance } from "../api/types";
import { getFieldProvenanceEntries } from "./FieldProvenanceHover";
import { TagProvenanceHover } from "./TagProvenanceHover";
import { TagMediaHover, type TagMediaReference } from "./EntityMedia";

export { RatingBadge } from "./Rating";
export { CustomFieldsDisplay, CustomFieldsEditor } from "./CustomFields";
export { FieldProvenanceHover } from "./FieldProvenanceHover";
export { TagProvenanceHover } from "./TagProvenanceHover";
import { getResolutionBucketLabel } from "../utils/resolutionBuckets";

type TagBadgeData = Pick<Tag, "color" | "tagGroupColor"> & Partial<Pick<Tag, "id" | "name" | "imagePath" | "hasImage">>;
export function TagBadge({ name, tag, color, groupColor, onClick, provenance, reportable, onReportIncorrect, onAdjustThreshold }: { name: string; tag?: TagBadgeData; color?: string | null; groupColor?: string | null; onClick?: () => void; provenance?: TagProvenance[]; reportable?: boolean; onReportIncorrect?: () => void; onAdjustThreshold?: () => void }) {
  const interactive = Boolean(onClick);
  const hasMenu = Boolean(reportable && (onReportIncorrect || onAdjustThreshold));
  const resolvedGroupColor = normalizeTagColor(groupColor ?? tag?.tagGroupColor);
  const resolvedColor = normalizeTagColor(color ?? tag?.color ?? groupColor ?? tag?.tagGroupColor);
  const colorStyle = resolvedColor ? getTagColorStyle(resolvedColor) : undefined;
  const mediaTag = tag?.id ? { id: tag.id, name: tag.name ?? name, imagePath: tag.imagePath, hasImage: tag.hasImage } : undefined;

  const badgeContent = (
    <>
      {resolvedGroupColor ? (
        <span className="inline-flex h-3.5 w-3.5 items-center justify-center rounded-sm border border-current/30" title="Tag group">
          <span className="h-1.5 w-1.5 rounded-full" style={{ backgroundColor: resolvedGroupColor }} />
        </span>
      ) : null}
      <span>{name}</span>
    </>
  );
  const withMediaHover = (content: ReactNode) => mediaTag && !provenance?.length ? (
    <TagMediaHover tag={mediaTag}>
      {content}
    </TagMediaHover>
  ) : content;

  // Without a menu, the whole chip is a single button (or static span) as before.
  if (!hasMenu) {
    const badge = interactive ? (
      <button
        type="button"
        onClick={onClick}
        style={colorStyle}
        className="inline-flex min-h-9 items-center gap-1.5 rounded border border-border bg-card px-2.5 py-1 text-xs font-medium text-secondary transition hover:bg-card-hover hover:text-foreground sm:min-h-0 sm:px-2 sm:py-0.5"
      >
        {badgeContent}
      </button>
    ) : (
      <span
        style={colorStyle}
        className="inline-flex items-center gap-1.5 rounded border border-border bg-card px-2 py-0.5 text-xs font-medium text-secondary"
      >
        {badgeContent}
      </span>
    );

    const badgeWithProvenance = <TagProvenanceHover provenance={provenance} mediaTag={mediaTag}>{badge}</TagProvenanceHover>;
    return withMediaHover(badgeWithProvenance);
  }

  // With a menu, the chip box hosts the trigger inline (right edge) rather than a button-nested-in-button.
  // The menu routes the two intents apart: tuning how often a tag appears is a global threshold change,
  // while "this detection is wrong" is the rare per-video correction that deletes the AI's finding.
  const badgeWithMenu = (
    <TagBadgeWithMenu
      name={name}
      colorStyle={colorStyle}
      interactive={interactive}
      onClick={onClick}
      provenance={provenance}
      mediaTag={mediaTag}
      onReportIncorrect={onReportIncorrect}
      onAdjustThreshold={onAdjustThreshold}
    >
      {badgeContent}
    </TagBadgeWithMenu>
  );
  return withMediaHover(badgeWithMenu);
}

function TagBadgeWithMenu({ name, colorStyle, interactive, onClick, provenance, mediaTag, children, onReportIncorrect, onAdjustThreshold }: { name: string; colorStyle?: CSSProperties; interactive: boolean; onClick?: () => void; provenance?: TagProvenance[]; mediaTag?: TagMediaReference; children: ReactNode; onReportIncorrect?: () => void; onAdjustThreshold?: () => void }) {
  const chip = (
    <span
      style={colorStyle}
      className="inline-flex min-h-9 items-center gap-1 rounded border border-border bg-card py-1 pl-2.5 pr-1 text-xs font-medium text-secondary sm:min-h-0 sm:py-0.5 sm:pl-2"
    >
      {interactive ? (
        <button type="button" onClick={onClick} className="inline-flex items-center gap-1.5 transition hover:text-foreground">
          {children}
        </button>
      ) : (
        <span className="inline-flex items-center gap-1.5">{children}</span>
      )}
      <TagActionMenu
        name={name}
        onReportIncorrect={onReportIncorrect}
        onAdjustThreshold={onAdjustThreshold}
        triggerClassName="-my-0.5 inline-flex items-center rounded p-0.5 text-muted transition hover:text-foreground"
        iconClassName="h-3 w-3"
      />
    </span>
  );

  return <TagProvenanceHover provenance={provenance} mediaTag={mediaTag}>{chip}</TagProvenanceHover>;
}

// The "⋯" trigger + dropdown for a derived ("locked") tag chip, shared by the read-only Details chip
// and the Edit-tab tag selector. It deliberately routes the two intents apart: tuning how often a tag
// appears is a global threshold change, while "this detection is wrong" is the rare per-video
// correction that deletes the AI's finding. Renders nothing if neither action is available.
export function TagActionMenu({ name, onReportIncorrect, onAdjustThreshold, triggerClassName, iconClassName }: { name: string; onReportIncorrect?: () => void; onAdjustThreshold?: () => void; triggerClassName?: string; iconClassName?: string }) {
  const [open, setOpen] = useState(false);
  const [coords, setCoords] = useState<{ left: number; top: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  // Position the menu against the viewport from the trigger's rect, clamped so it never spills off any
  // edge (a chip near the left or bottom would otherwise push an absolutely-positioned menu off-screen).
  const place = () => {
    const rect = triggerRef.current?.getBoundingClientRect();
    if (!rect) return;
    const margin = 8;
    const width = 256; // w-64
    const height = menuRef.current?.offsetHeight ?? 96;
    const left = Math.min(Math.max(margin, rect.right - width), window.innerWidth - width - margin);
    const top = rect.bottom + 4 + height > window.innerHeight - margin
      ? Math.max(margin, rect.top - height - 4)
      : rect.bottom + 4;
    setCoords({ left, top });
  };

  // Measure once the menu is in the DOM (so height is known), then re-clamp.
  useLayoutEffect(() => {
    if (open) place();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  // The fixed-positioned menu can't follow the trigger, so dismiss it on scroll/resize instead.
  useEffect(() => {
    if (!open) return;
    const close = () => setOpen(false);
    window.addEventListener("scroll", close, true);
    window.addEventListener("resize", close);
    return () => {
      window.removeEventListener("scroll", close, true);
      window.removeEventListener("resize", close);
    };
  }, [open]);

  if (!onReportIncorrect && !onAdjustThreshold) return null;

  return (
    <span className="inline-flex">
      <button
        ref={triggerRef}
        type="button"
        aria-label={`More actions for ${name}`}
        title="More actions"
        onClick={(e) => { e.stopPropagation(); e.preventDefault(); setOpen((value) => !value); }}
        className={triggerClassName ?? "rounded p-0.5 text-muted transition hover:text-foreground"}
      >
        <MoreVertical className={iconClassName ?? "h-3.5 w-3.5"} />
      </button>
      {open && coords
        ? createPortal(
            <>
              <div className="fixed inset-0 z-[60]" onClick={() => setOpen(false)} onContextMenu={(e) => { e.preventDefault(); setOpen(false); }} />
              <div ref={menuRef} className="fixed z-[61] w-64 rounded-md border border-border bg-surface p-1 shadow-xl" style={{ left: coords.left, top: coords.top }}>
                {onAdjustThreshold ? (
                  <button
                    type="button"
                    onClick={() => { setOpen(false); onAdjustThreshold(); }}
                    className="flex w-full items-start gap-2 rounded px-2 py-1.5 text-left text-xs text-foreground transition hover:bg-card"
                  >
                    <SlidersHorizontal className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted" />
                    <span>
                      <span className="block font-medium">Adjust when this tag appears</span>
                      <span className="block text-muted">Changes the tag's threshold — affects all videos</span>
                    </span>
                  </button>
                ) : null}
                {onReportIncorrect ? (
                  <button
                    type="button"
                    onClick={() => { setOpen(false); onReportIncorrect(); }}
                    className="flex w-full items-start gap-2 rounded px-2 py-1.5 text-left text-xs text-red-300 transition hover:bg-card"
                  >
                    <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                    <span>
                      <span className="block font-medium">This detection is wrong</span>
                      <span className="block text-red-300/70">Removes the AI's detection from this video</span>
                    </span>
                  </button>
                ) : null}
              </div>
            </>,
            document.body,
          )
        : null}
    </span>
  );
}

function fieldProvenanceValueContainsTag(value: unknown, tagName: string) {
  if (typeof value === "string") {
    return value === tagName || value.includes(tagName);
  }

  if (Array.isArray(value)) {
    return value.some((item) => {
      if (typeof item === "string") {
        return item === tagName || item.includes(tagName);
      }
      if (item && typeof item === "object") {
        return ["name", "tagName", "value", "label"].some((key) => {
          const candidate = (item as Record<string, unknown>)[key];
          return typeof candidate === "string" && (candidate === tagName || candidate.includes(tagName));
        });
      }
      return false;
    });
  }

  if (value && typeof value === "object") {
    return ["name", "tagName", "value", "label"].some((key) => {
      const candidate = (value as Record<string, unknown>)[key];
      return typeof candidate === "string" && (candidate === tagName || candidate.includes(tagName));
    });
  }

  return false;
}

export function resolveTagProvenance(tag: Pick<Tag, "name" | "provenance">, fieldProvenance?: FieldProvenance[], fieldKey: string | string[] = "tags") {
  if (tag.provenance?.length) {
    return tag.provenance;
  }

  const fallback = getFieldProvenanceEntries(fieldProvenance, fieldKey)
    .filter((entry) => fieldProvenanceValueContainsTag(entry.value, tag.name))
    .map((entry) => ({
      sourceKey: entry.sourceKey,
      sourceRunId: entry.sourceRunId,
      modelKey: entry.modelKey,
      confidence: entry.confidence,
      appliedAt: entry.createdAt,
    } satisfies TagProvenance));

  return fallback.length > 0 ? fallback : tag.provenance;
}

export function buildTagProvenanceById(tags: Array<Pick<Tag, "id" | "name" | "provenance">>, fieldProvenance?: FieldProvenance[], fieldKey: string | string[] = "tags") {
  return Object.fromEntries(tags.map((tag) => [tag.id, resolveTagProvenance(tag, fieldProvenance, fieldKey)])) as Record<number, TagProvenance[] | undefined>;
}

export function ProvenanceBadge({ name, provenance, onClick, sourceLabel = "Source", children }: { name: string; provenance?: TagProvenance[]; onClick?: () => void; sourceLabel?: string; children?: ReactNode }) {
  const interactive = Boolean(onClick);

  const badgeContent = (
    <>
      <span>{children ?? name}</span>
    </>
  );

  const badge = interactive ? (
    <button
      type="button"
      onClick={onClick}
      className="inline-flex min-h-9 items-center gap-1.5 rounded border border-border bg-card px-2.5 py-1 text-xs font-medium text-secondary transition hover:bg-card-hover hover:text-foreground sm:min-h-0 sm:px-2 sm:py-0.5"
    >
      {badgeContent}
    </button>
  ) : (
    <span className="inline-flex items-center gap-1.5 rounded border border-border bg-card px-2 py-0.5 text-xs font-medium text-secondary">
      {badgeContent}
    </span>
  );

  return <TagProvenanceHover provenance={provenance} sourceLabel={sourceLabel}>{badge}</TagProvenanceHover>;
}

function normalizeTagColor(value?: string | null) {
  const trimmed = value?.trim();
  if (!trimmed || !/^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(trimmed)) {
    return null;
  }
  return trimmed;
}

function getTagColorStyle(color: string): CSSProperties {
  return {
    borderColor: hexToRgba(color, 0.58),
    backgroundColor: hexToRgba(color, 0.14),
    color: hexToRgba(color, 0.96),
  };
}

function hexToRgba(hex: string, alpha: number) {
  const value = hex.slice(1, 7);
  const r = Number.parseInt(value.slice(0, 2), 16);
  const g = Number.parseInt(value.slice(2, 4), 16);
  const b = Number.parseInt(value.slice(4, 6), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

export function formatDuration(seconds: number): string {
  if (!seconds || seconds <= 0) return "0:00";
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  if (h > 0) return `${h}:${m.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}`;
  return `${m}:${s.toString().padStart(2, "0")}`;
}

export function formatFileSize(bytes: number): string {
  if (bytes === 0) return "0 B";
  const k = 1024;
  const sizes = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + " " + sizes[i];
}

export { formatDate } from "../utils/dateFormat";

export function getResolutionLabel(width: number, height: number): string | null {
  return getResolutionBucketLabel(width, height);
}
