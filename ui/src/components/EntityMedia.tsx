import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { entityImages } from "../api/client";
import type { Tag } from "../api/types";
import { ExtensionComponentOverrideRenderer, useExtensions } from "../extensions/ExtensionLoader";

export const ENTITY_MEDIA_TARGET = "entity.media" as const;

export type EntityMediaSurface = "card" | "hero" | "list" | "picker" | "recommendation" | "dialog" | "hover";

export type EntityMediaFit = "cover" | "contain";

export interface EntityMediaRenderProps {
  entityType: string;
  entityId: number;
  surface: EntityMediaSurface;
  imageUrl?: string | null;
  alt: string;
  fit: EntityMediaFit;
  loading?: "eager" | "lazy";
  className?: string;
  renderDefault: () => ReactNode;
}

interface EntityMediaHoverProps extends Omit<EntityMediaRenderProps, "surface" | "className" | "renderDefault"> {
  children: ReactNode;
  wrapperClassName?: string;
  /** Host-owned details shown under the media, outside the entity.media extension boundary. */
  footer?: ReactNode;
}

interface EntityMediaPreviewProps extends Omit<EntityMediaRenderProps, "renderDefault"> {
  frameClassName?: string;
}

const DEFAULT_HOVER_ASPECT_RATIO = "4 / 3";

/** Override roots may declare their intrinsic frame shape for host-owned hover layout. */
function readDeclaredAspectRatio(container: HTMLElement | null) {
  const raw = container?.querySelector<HTMLElement>("[data-entity-media-aspect-ratio]")?.dataset.entityMediaAspectRatio;
  const match = raw?.match(/^\s*(\d+(?:\.\d+)?)\s*[:/]\s*(\d+(?:\.\d+)?)\s*$/);
  if (!match) return DEFAULT_HOVER_ASPECT_RATIO;

  const width = Number(match[1]);
  const height = Number(match[2]);
  return Number.isFinite(width) && Number.isFinite(height) && width > 0 && height > 0
    ? `${width} / ${height}`
    : DEFAULT_HOVER_ASPECT_RATIO;
}

function aspectRatioValue(aspectRatio: string) {
  const [width, height] = aspectRatio.split("/").map(Number);
  return width / height;
}

export type TagMediaReference = Pick<Tag, "id" | "name"> &
  Partial<Pick<Tag, "imagePath" | "hasImage" | "tagGroupName" | "tagGroupColor">>;

export function getTagMediaImageUrl(tag: TagMediaReference) {
  return tag.imagePath || (tag.hasImage ? entityImages.tagImageUrl(tag.id) : null);
}

/** Names the tag's group, with its color, inside a tag's hover popup. Renders nothing for an ungrouped tag. */
export function TagGroupCaption({ tag, className }: { tag: TagMediaReference; className?: string }) {
  const name = tag.tagGroupName?.trim();
  if (!name) return null;
  const color = tag.tagGroupColor?.trim();
  return (
    <span className={["flex min-w-0 items-center gap-1.5 text-xs", className ?? ""].filter(Boolean).join(" ")}>
      <span className="text-muted">Tag group</span>
      {color && /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(color) ? (
        <span className="h-2 w-2 flex-none rounded-full" style={{ backgroundColor: color }} />
      ) : null}
      <span className="min-w-0 truncate font-medium text-foreground">{name}</span>
    </span>
  );
}

/** Shared hover boundary for compact tag references across cards, feeds, and badges. */
export function TagMediaHover({
  tag,
  children,
  wrapperClassName,
}: {
  tag: TagMediaReference;
  children: ReactNode;
  wrapperClassName?: string;
}) {
  return (
    <EntityMediaHover
      entityType="tag"
      entityId={tag.id}
      imageUrl={getTagMediaImageUrl(tag)}
      alt={tag.name}
      fit="cover"
      loading="lazy"
      wrapperClassName={wrapperClassName}
      footer={tag.tagGroupName?.trim() ? <TagGroupCaption tag={tag} className="px-3 py-2" /> : undefined}
    >
      {children}
    </EntityMediaHover>
  );
}

/** Non-controller tag preview for composition inside an existing popup. */
export function TagMediaPreview({ tag, frameClassName }: { tag: TagMediaReference; frameClassName?: string }) {
  return (
    <EntityMediaPreview
      entityType="tag"
      entityId={tag.id}
      surface="hover"
      imageUrl={getTagMediaImageUrl(tag)}
      alt={tag.name}
      fit="cover"
      loading="lazy"
      className="h-full w-full"
      frameClassName={frameClassName}
    />
  );
}

/**
 * Stable extension boundary for an entity's primary visual media. The host keeps
 * navigation and card chrome outside this component while extensions may replace
 * or wrap only the native media renderer.
 */
export function EntityMedia({ renderDefault, ...componentProps }: EntityMediaRenderProps) {
  const resetKey = JSON.stringify([
    componentProps.entityType,
    componentProps.entityId,
    componentProps.surface,
    componentProps.imageUrl ?? null,
    componentProps.alt,
    componentProps.fit,
    componentProps.loading ?? null,
    componentProps.className ?? null,
  ]);

  return (
    <ExtensionComponentOverrideRenderer
      targetComponent={ENTITY_MEDIA_TARGET}
      componentProps={componentProps}
      renderDefault={renderDefault}
      resetKey={resetKey}
    />
  );
}

function useEntityMediaPreview(componentProps: Omit<EntityMediaRenderProps, "renderDefault">) {
  const { getComponentOverrides } = useExtensions();
  const [failedImageUrl, setFailedImageUrl] = useState<string | null>(null);
  const staticImageUrl = componentProps.imageUrl || null;
  const hasStaticImage = Boolean(staticImageUrl) && failedImageUrl !== staticImageUrl;
  const enabled = hasStaticImage || getComponentOverrides(ENTITY_MEDIA_TARGET).length > 0;
  const renderDefault = () =>
    staticImageUrl && failedImageUrl !== staticImageUrl ? (
      <img
        src={staticImageUrl}
        alt={componentProps.alt}
        className={`h-full w-full ${componentProps.fit === "contain" ? "object-contain" : "object-cover"}`}
        loading={componentProps.loading}
        onError={() => setFailedImageUrl(staticImageUrl)}
      />
    ) : null;

  return {
    enabled,
    render: () => (enabled ? <EntityMedia {...componentProps} renderDefault={renderDefault} /> : null),
  };
}

/** Renders entity media without adding hover/focus ownership or portal positioning. */
export function EntityMediaPreview({ frameClassName, ...componentProps }: EntityMediaPreviewProps) {
  const preview = useEntityMediaPreview(componentProps);
  if (!preview.enabled) return null;
  return <div className={["empty:hidden", frameClassName ?? ""].filter(Boolean).join(" ")}>{preview.render()}</div>;
}

/**
 * Optional hover surface for entity references. The host owns positioning,
 * containment, and the supplied static image; an entity.media override may
 * replace that image and owns any additional data fetching. With neither
 * static media nor an active override this returns the reference unchanged.
 */
export function EntityMediaHover({
  children,
  wrapperClassName = "inline-flex",
  footer,
  ...mediaProps
}: EntityMediaHoverProps) {
  const anchorRef = useRef<HTMLSpanElement>(null);
  const tooltipRef = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState({ left: 8, top: 8 });
  const [aspectRatio, setAspectRatio] = useState(DEFAULT_HOVER_ASPECT_RATIO);
  const preview = useEntityMediaPreview({ ...mediaProps, surface: "hover", className: "h-full w-full" });

  useLayoutEffect(() => {
    if (!preview.enabled || !open) return;

    const tooltip = tooltipRef.current;
    const updateAspectRatio = () => {
      const next = readDeclaredAspectRatio(tooltip);
      setAspectRatio((current) => (current === next ? current : next));
    };

    updateAspectRatio();
    if (!tooltip) return;

    const observer = new MutationObserver(updateAspectRatio);
    observer.observe(tooltip, {
      attributes: true,
      attributeFilter: ["data-entity-media-aspect-ratio"],
      childList: true,
      subtree: true,
    });
    return () => observer.disconnect();
  }, [preview.enabled, open]);

  useLayoutEffect(() => {
    if (!preview.enabled || !open) return;

    const place = () => {
      const anchor = anchorRef.current?.getBoundingClientRect();
      if (!anchor) return;
      const rect = tooltipRef.current?.getBoundingClientRect();
      const width = rect?.width || 288;
      // Measure when rendered, since a footer adds height under the media frame.
      const height = rect?.height || width / aspectRatioValue(aspectRatio);
      const margin = 8;
      const left = Math.min(Math.max(margin, anchor.left), window.innerWidth - width - margin);
      const top =
        anchor.top - height - margin >= margin
          ? anchor.top - height - margin
          : Math.min(anchor.bottom + margin, window.innerHeight - height - margin);
      const next = { left, top: Math.max(margin, top) };
      // Keep the same state when nothing moved, so scroll and resize events don't re-render needlessly.
      setPosition((current) => (current.left === next.left && current.top === next.top ? current : next));
    };

    place();
    // Media can appear, change size or fail after the popup opens (an override may still be loading);
    // re-place so a popup that grew doesn't cover the reference.
    const resizeObserver =
      typeof ResizeObserver !== "undefined" && tooltipRef.current ? new ResizeObserver(place) : null;
    if (tooltipRef.current) resizeObserver?.observe(tooltipRef.current);
    window.addEventListener("resize", place);
    window.addEventListener("scroll", place, true);
    return () => {
      resizeObserver?.disconnect();
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [aspectRatio, preview.enabled, open]);

  if (!preview.enabled) return <>{children}</>;

  return (
    <span
      ref={anchorRef}
      className={`relative ${wrapperClassName}`}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
      onFocus={() => setOpen(true)}
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setOpen(false);
      }}
    >
      {children}
      {open && typeof document !== "undefined"
        ? createPortal(
            <div
              ref={tooltipRef}
              role="tooltip"
              aria-label={`Media for ${mediaProps.alt}`}
              // Hidden while the media frame is empty, so a footer alone never shows as a popup.
              className="pointer-events-none fixed z-[10000] block w-72 overflow-hidden rounded-xl border border-border bg-surface/95 shadow-2xl has-[>[data-media-frame]:empty]:hidden"
              style={position}
            >
              {/* The frame clips the media to its aspect ratio, so an override can't push into the footer. */}
              <div data-media-frame="" className="w-full overflow-hidden" style={{ aspectRatio }}>
                {preview.render()}
              </div>
              {footer ? <div className="border-t border-border/70">{footer}</div> : null}
            </div>,
            document.body,
          )
        : null}
    </span>
  );
}
