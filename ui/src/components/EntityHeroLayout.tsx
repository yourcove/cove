import { ArrowLeft, Check, ChevronLeft, ChevronRight, Heart, RefreshCw } from "lucide-react";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { EntityMedia, type EntityMediaRenderProps } from "./EntityMedia";

// Cove standard EntityHero action button styles. Use these for favorite/organized/edit/overflow
// so corner rounding, size, and border treatment stay consistent across all EntityHero pages.
export const HERO_ACTION_BUTTON_CLASS =
  "inline-flex h-10 w-10 items-center justify-center rounded-lg border border-border bg-card transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed";
export const HERO_PRIMARY_ACTION_BUTTON_CLASS =
  "inline-flex h-10 items-center gap-1.5 rounded-lg bg-accent px-3 text-sm text-white transition-colors hover:bg-accent-hover disabled:cursor-not-allowed";

export interface EntityHeroCount {
  key: string;
  label: string;
  value: ReactNode;
  icon?: ReactNode;
}

export interface EntityHeroLayoutProps {
  entityType: EntityMediaRenderProps["entityType"];
  entityId: number;
  backLabel: string;
  onGoBack: () => void;
  backgroundImageUrl?: string | null;
  backgroundImageAlt?: string;
  backgroundImageClassName?: string;
  backgroundOverlayClassName?: string;
  imageUrl?: string | null;
  imageAlt?: string;
  alternateImageUrl?: string | null;
  alternateImageAlt?: string;
  primaryImageLabel?: string;
  alternateImageLabel?: string;
  imageContainerClassName?: string;
  imageClassName?: string;
  imageFit?: EntityMediaRenderProps["fit"];
  imageFallbackClassName?: string;
  imageFallback?: ReactNode;
  onImageClick?: (imageSlot?: "primary" | "alternate") => void;
  imageActionTitle?: string;
  imageCarouselUrls?: string[];
  imageCarouselIndex?: number;
  onImageCarouselIndexChange?: (index: number) => void;
  title: ReactNode;
  subtitle?: ReactNode;
  sortName?: ReactNode;
  aliases?: ReactNode;
  description?: ReactNode;
  counts?: EntityHeroCount[];
  metaRow?: ReactNode;
  favorite?: boolean;
  favoritePending?: boolean;
  onFavoriteToggle?: () => void;
  organized?: boolean;
  organizedPending?: boolean;
  onOrganizedToggle?: (organized: boolean) => void;
  titleActions?: ReactNode;
  heroContent?: ReactNode;
  actions?: ReactNode;
  heroRowClassName?: string;
  contentClassName?: string;
  children?: ReactNode;
}

// Shared hero layout used by entity-style detail pages (Tags, Studios, Performers,
// Galleries, Faces). Mirrors the existing Tag/Studio/Performer detail page header
// (cover image left, title + counts top, scrollable content area below).
export function EntityHeroLayout({
  entityType,
  entityId,
  backLabel,
  onGoBack,
  backgroundImageUrl,
  backgroundImageAlt,
  backgroundImageClassName,
  backgroundOverlayClassName,
  imageUrl,
  imageAlt,
  alternateImageUrl,
  alternateImageAlt,
  primaryImageLabel = "front cover",
  alternateImageLabel = "back cover",
  imageContainerClassName,
  imageClassName,
  imageFit = "cover",
  imageFallbackClassName,
  imageFallback,
  onImageClick,
  imageActionTitle = "Change cover",
  imageCarouselUrls,
  imageCarouselIndex = 0,
  onImageCarouselIndexChange,
  title,
  subtitle,
  sortName,
  aliases,
  description,
  counts = [],
  metaRow,
  favorite,
  favoritePending = false,
  onFavoriteToggle,
  organized,
  organizedPending = false,
  onOrganizedToggle,
  titleActions,
  heroContent,
  actions,
  heroRowClassName,
  contentClassName,
  children,
}: EntityHeroLayoutProps) {
  const resolvedImageContainerClassName =
    imageContainerClassName ??
    "relative flex h-48 w-48 flex-shrink-0 items-center justify-center overflow-hidden rounded-xl border border-border bg-card shadow-xl shadow-black/35 md:h-56 md:w-56";
  const resolvedImageClassName = imageClassName ?? "h-full w-full object-cover";
  const resolvedFallbackClassName =
    imageFallbackClassName ?? "h-full w-full items-center justify-center bg-card text-muted";
  const resolvedHeroRowClassName = heroRowClassName ?? "flex flex-col gap-6 md:flex-row md:items-start";
  const resolvedContentClassName =
    contentClassName ?? "w-full px-4 py-6 [&>[data-entity-detail-tabs]:first-child]:-mt-6";
  const carouselUrls = useMemo(
    () =>
      Array.from(
        new Set(
          (imageCarouselUrls ?? []).filter((url): url is string => typeof url === "string" && url.trim().length > 0),
        ),
      ),
    [imageCarouselUrls],
  );
  const hasCarousel = carouselUrls.length > 1 && onImageCarouselIndexChange != null;
  const boundedCarouselIndex =
    carouselUrls.length > 0 ? Math.min(Math.max(imageCarouselIndex, 0), carouselUrls.length - 1) : 0;
  const resolvedImageUrl = carouselUrls[boundedCarouselIndex] ?? imageUrl;
  const hasAlternateImage = !hasCarousel && Boolean(alternateImageUrl);
  const [showAlternateImage, setShowAlternateImage] = useState(false);
  const displayedImageSlot = hasAlternateImage && showAlternateImage ? "alternate" : "primary";
  const displayedImageUrl = displayedImageSlot === "alternate" ? alternateImageUrl : resolvedImageUrl;
  const displayedImageAlt = displayedImageSlot === "alternate" ? (alternateImageAlt ?? imageAlt) : imageAlt;
  const displayedOrganized = organized;

  useEffect(() => {
    if (!hasAlternateImage && showAlternateImage) {
      setShowAlternateImage(false);
    }
  }, [hasAlternateImage, showAlternateImage]);

  const favoriteTitle = favorite ? "Remove favorite" : "Favorite";
  const heroActionClassName = HERO_ACTION_BUTTON_CLASS;
  const favoriteAction =
    typeof favorite === "boolean" ? (
      onFavoriteToggle ? (
        <button
          type="button"
          onClick={onFavoriteToggle}
          disabled={favoritePending}
          aria-pressed={favorite}
          title={favoriteTitle}
          className={`${heroActionClassName} ${favorite ? "text-red-400" : "text-accent"}`}
        >
          <Heart className={`h-4 w-4 ${favorite ? "fill-current" : ""}`} />
        </button>
      ) : (
        <span
          title={favoriteTitle}
          className={`inline-flex h-10 w-10 items-center justify-center rounded-lg border border-border bg-card ${favorite ? "text-red-400" : "text-accent"}`}
        >
          <Heart className={`h-4 w-4 ${favorite ? "fill-current" : ""}`} />
        </span>
      )
    ) : null;
  const organizedTitle = displayedOrganized ? "Mark unorganized" : "Mark organized";
  const organizedAction =
    typeof organized === "boolean" ? (
      onOrganizedToggle ? (
        <button
          type="button"
          onClick={() => {
            onOrganizedToggle(!organized);
          }}
          disabled={organizedPending}
          aria-pressed={displayedOrganized}
          title={organizedTitle}
          className={`${heroActionClassName} ${displayedOrganized ? "text-emerald-400" : "text-secondary"}`}
        >
          <Check className="h-4 w-4" />
        </button>
      ) : organized ? (
        <span
          title="Organized"
          className="inline-flex h-10 w-10 items-center justify-center rounded-lg border border-border bg-card text-emerald-400"
        >
          <Check className="h-4 w-4" />
        </span>
      ) : null
    ) : null;
  const hasHeaderActions = Boolean(organizedAction || favoriteAction || actions);
  const imageContent = (
    <>
      <EntityMedia
        entityType={entityType}
        entityId={entityId}
        surface="hero"
        imageUrl={displayedImageUrl}
        alt={displayedImageAlt ?? ""}
        fit={imageFit}
        loading="eager"
        className={resolvedImageClassName}
        renderDefault={() => (
          <>
            {displayedImageUrl ? (
              <img
                src={displayedImageUrl}
                alt={displayedImageAlt ?? ""}
                className={resolvedImageClassName}
                loading="eager"
                onLoad={(event) => {
                  event.currentTarget.style.display = "";
                  const fallback = event.currentTarget.nextElementSibling as HTMLElement | null;
                  if (fallback) fallback.style.display = "none";
                }}
                onError={(e) => {
                  (e.target as HTMLImageElement).style.display = "none";
                  const fallback = (e.target as HTMLImageElement).nextElementSibling as HTMLElement | null;
                  if (fallback) fallback.style.display = "flex";
                }}
              />
            ) : null}
            <div className={[resolvedFallbackClassName, displayedImageUrl ? "hidden" : "flex"].join(" ")}>
              {imageFallback}
            </div>
          </>
        )}
      />
      {hasCarousel ? (
        <>
          <button
            type="button"
            onClick={(event) => {
              event.preventDefault();
              event.stopPropagation();
              onImageCarouselIndexChange((boundedCarouselIndex - 1 + carouselUrls.length) % carouselUrls.length);
            }}
            className="absolute left-2 top-1/2 z-20 inline-flex h-9 w-9 -translate-y-1/2 items-center justify-center rounded-lg border border-white/15 bg-black/65 text-white shadow-lg transition hover:bg-black/80 focus:outline-none focus:ring-2 focus:ring-accent"
            aria-label="Previous image"
            title="Previous image"
          >
            <ChevronLeft className="h-4 w-4" />
          </button>
          <button
            type="button"
            onClick={(event) => {
              event.preventDefault();
              event.stopPropagation();
              onImageCarouselIndexChange((boundedCarouselIndex + 1) % carouselUrls.length);
            }}
            className="absolute right-2 top-1/2 z-20 inline-flex h-9 w-9 -translate-y-1/2 items-center justify-center rounded-lg border border-white/15 bg-black/65 text-white shadow-lg transition hover:bg-black/80 focus:outline-none focus:ring-2 focus:ring-accent"
            aria-label="Next image"
            title="Next image"
          >
            <ChevronRight className="h-4 w-4" />
          </button>
          <span className="pointer-events-none absolute bottom-2 right-2 z-20 rounded bg-black/65 px-2 py-0.5 text-[11px] font-medium text-white">
            {boundedCarouselIndex + 1}/{carouselUrls.length}
          </span>
        </>
      ) : null}
      {hasAlternateImage ? (
        <button
          type="button"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            setShowAlternateImage((current) => !current);
          }}
          className="absolute bottom-2 right-2 z-30 inline-flex h-10 w-10 items-center justify-center rounded-full border border-white/15 bg-black/70 text-white shadow-lg transition hover:bg-black/85 focus:outline-none focus:ring-2 focus:ring-accent"
          aria-label={showAlternateImage ? `Show ${primaryImageLabel}` : `Show ${alternateImageLabel}`}
          title={showAlternateImage ? `Show ${primaryImageLabel}` : `Show ${alternateImageLabel}`}
        >
          <RefreshCw className="h-5 w-5" />
        </button>
      ) : null}
      {onImageClick ? (
        <span
          className={`pointer-events-none absolute inset-x-3 ${hasAlternateImage ? "bottom-14" : "bottom-3"} rounded-lg bg-black/70 px-3 py-2 text-center text-xs font-medium text-white opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100`}
        >
          {imageActionTitle}
        </span>
      ) : null}
    </>
  );

  return (
    <div className="min-h-screen">
      <div className="relative overflow-hidden detail-hero-gradient">
        {backgroundImageUrl ? (
          <>
            <img
              src={backgroundImageUrl}
              alt={backgroundImageAlt ?? ""}
              className={
                backgroundImageClassName ?? "absolute inset-0 h-full w-full scale-110 object-cover opacity-10 blur-md"
              }
              onError={(event) => {
                (event.target as HTMLImageElement).style.display = "none";
              }}
            />
            <div
              className={
                backgroundOverlayClassName ??
                "absolute inset-0 bg-gradient-to-t from-background via-background/70 to-transparent"
              }
            />
          </>
        ) : null}

        <div className="relative mx-auto max-w-7xl px-4 py-8">
          <div className="mb-5 flex items-center justify-between gap-4">
            <button
              type="button"
              onClick={onGoBack}
              className="flex items-center gap-1 text-sm text-secondary hover:text-foreground"
            >
              <ArrowLeft className="h-4 w-4" /> {backLabel}
            </button>
            {hasHeaderActions ? (
              <div className="flex items-center gap-2">
                {organizedAction}
                {favoriteAction}
                {actions}
              </div>
            ) : null}
          </div>

          <div className={resolvedHeroRowClassName}>
            {onImageClick && !hasCarousel && !hasAlternateImage ? (
              <button
                type="button"
                onClick={() => onImageClick(displayedImageSlot)}
                title={imageActionTitle}
                className={`${resolvedImageContainerClassName} group focus:outline-none focus:ring-2 focus:ring-accent`}
              >
                {imageContent}
              </button>
            ) : (
              <div
                className={`${resolvedImageContainerClassName} ${onImageClick ? "group focus-within:ring-2 focus-within:ring-accent" : ""}`}
              >
                {onImageClick ? (
                  <button
                    type="button"
                    onClick={() => onImageClick(displayedImageSlot)}
                    title={imageActionTitle}
                    aria-label={imageActionTitle}
                    className="absolute inset-0 z-10 focus:outline-none"
                  />
                ) : null}
                {imageContent}
              </div>
            )}

            <div className="min-w-0 flex-1">
              <div className="mb-2 flex items-start gap-4">
                <div className="min-w-0 flex-1">
                  <h1 className="truncate text-2xl font-bold text-foreground sm:text-3xl">{title}</h1>
                  {subtitle ? <div className="mt-1 text-sm text-secondary">{subtitle}</div> : null}
                  {sortName ? <p className="mt-1 text-sm text-muted">Sort name: {sortName}</p> : null}
                  {aliases ? <p className="mt-1 text-sm text-secondary">Also known as: {aliases}</p> : null}
                </div>
                {titleActions ? <div className="flex items-center gap-2">{titleActions}</div> : null}
              </div>

              {description ? (
                <div className="max-w-4xl whitespace-pre-wrap text-sm leading-6 text-secondary">{description}</div>
              ) : null}

              {counts.length > 0 ? (
                <div className="mt-4 flex flex-wrap gap-3">
                  {counts.map((c) => (
                    <div
                      key={c.key}
                      className="flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-2"
                    >
                      {c.icon ? <span className="text-accent">{c.icon}</span> : null}
                      <div>
                        <div className="text-lg font-semibold text-foreground">{c.value}</div>
                        <div className="text-xs text-muted">{c.label}</div>
                      </div>
                    </div>
                  ))}
                </div>
              ) : null}

              {metaRow ? (
                <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-muted">{metaRow}</div>
              ) : null}
              {heroContent ? <div className="mt-4">{heroContent}</div> : null}
            </div>
          </div>
        </div>
        <div aria-hidden="true" className="pointer-events-none absolute inset-x-4 bottom-0 border-b border-border" />
      </div>

      <div className={resolvedContentClassName}>{children}</div>
    </div>
  );
}
