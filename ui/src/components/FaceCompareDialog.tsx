import { useEffect, useMemo, useRef, useState } from "react";
import { ChevronLeft, ChevronRight, ExternalLink, Fingerprint, GitMerge, Link2, SkipForward, XCircle } from "lucide-react";
import type { Face, FaceSuggestion, FaceSuggestionEvidence, FaceTopSuggestion } from "../api/types";
import { createRouteLinkProps } from "./cardNavigation";
import { EditModal } from "./EditModal";

type ComparableSuggestion = FaceSuggestion | FaceTopSuggestion;
type ConfirmOptions = { setPerformerImage?: boolean };

interface Props {
  open: boolean;
  face: Face | null;
  suggestion: ComparableSuggestion | null;
  faceImageUrls?: string[];
  disabled?: boolean;
  canReadPerformers: boolean;
  onClose: () => void;
  onConfirm: (suggestion: ComparableSuggestion, options?: ConfirmOptions) => void;
  onReject: (suggestion: ComparableSuggestion) => void;
  onNavigate: (route: any) => void;
  batchLabel?: string;
  onSkip?: () => void;
  // Other suggestions for the same face. When the opened suggestion shares a conflictGroupId with one or
  // more of these, the dialog offers a "use one / use the other / merge both" choice.
  siblingSuggestions?: FaceSuggestion[];
  // Called when the user chooses to merge the competing matches. The primary performer survives; the
  // others are folded into it as aliases/links. Ids mirror suggestion.performerId (real or reference).
  onMerge?: (primaryPerformerId: number, secondaryPerformerIds: number[]) => void;
}

export function FaceCompareDialog({
  open,
  face,
  suggestion,
  faceImageUrls,
  disabled = false,
  canReadPerformers,
  onClose,
  onConfirm,
  onReject,
  onNavigate,
  batchLabel,
  onSkip,
  siblingSuggestions,
  onMerge,
}: Props) {
  // The competing matches for this face (the opened suggestion plus any siblings sharing its conflict
  // group). Empty unless a merge handler is wired and there is a genuine conflict.
  const conflictCandidates = useMemo<ComparableSuggestion[]>(() => {
    const groupId = readConflictGroupId(suggestion);
    if (!onMerge || !groupId || !suggestion) {
      return [];
    }
    const siblings = (siblingSuggestions ?? []).filter((item) => item.conflictGroupId === groupId);
    const all = [suggestion, ...siblings];
    const seen = new Set<number>();
    return all.filter((item) => {
      if (seen.has(item.performerId)) return false;
      seen.add(item.performerId);
      return true;
    });
  }, [onMerge, siblingSuggestions, suggestion]);

  const isConflict = conflictCandidates.length >= 2;

  const [selectedPerformerId, setSelectedPerformerId] = useState<number | null>(suggestion?.performerId ?? null);
  const [setPerformerImage, setSetPerformerImage] = useState(false);
  const [faceImageIndex, setFaceImageIndex] = useState(0);
  const [suggestionImageIndex, setSuggestionImageIndex] = useState(0);

  // The candidate currently shown in the right-hand pane. With no conflict this is just the opened
  // suggestion; with a conflict it follows the radio selection.
  const active = useMemo<ComparableSuggestion | null>(() => {
    if (!suggestion) return null;
    if (!isConflict) return suggestion;
    return conflictCandidates.find((item) => item.performerId === selectedPerformerId) ?? conflictCandidates[0];
  }, [conflictCandidates, isConflict, selectedPerformerId, suggestion]);

  const canSetPerformerImage = useMemo(() => {
    if (!face || !active) {
      return false;
    }
    const localPerformerId = readLocalPerformerId(active);
    return localPerformerId != null
      && !!face.coverImageUrl
      && active.localPerformerHasImage === false
      && active.localPerformerIsLocalOnly === true
      // When linking this reference match will refresh the performer from a metadata server, the
      // performer's image comes from there — setting it from the face crop is irrelevant.
      && !readReferenceWillRefreshFromMetadata(active);
  }, [face, active]);

  const evidence = useMemo(() => active ? readEvidence(active).slice(0, 5) : [], [active]);
  const faceImages = useMemo(
    () => buildCarouselImageUrls(face?.coverImageUrl, faceImageUrls ?? []),
    [face?.coverImageUrl, faceImageUrls],
  );
  const suggestionImages = useMemo(
    () => buildCarouselImageUrls(active?.coverImageUrl, evidence.map((item) => item.thumbnailUrl), true),
    [evidence, active?.coverImageUrl],
  );

  useEffect(() => {
    setSelectedPerformerId(suggestion?.performerId ?? null);
  }, [face?.id, open, suggestion?.performerId]);

  useEffect(() => {
    setSetPerformerImage(canSetPerformerImage);
  }, [canSetPerformerImage, face?.id, open, active?.performerId]);

  useEffect(() => {
    setSuggestionImageIndex(0);
  }, [active?.performerId, open]);

  useEffect(() => {
    setFaceImageIndex(0);
  }, [face?.id, open]);

  if (!open || !face || !suggestion || !active) {
    return null;
  }

  const faceTitle = face.label?.trim() || face.performerName || "Unidentified face";
  const localPerformerId = readLocalPerformerId(active);
  const referenceOnly = localPerformerId == null && active.performerId < 0;
  // Sourced from a reference database, whether or not it already resolved to a local performer.
  const isReferenceMatch = active.performerId < 0 || !!active.externalUrl;
  const why = readWhy(active);

  const faceLinkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "face", id: face.id }, () => {
    onClose();
    onNavigate({ page: "face", id: face.id });
  });

  const performerLinkProps = localPerformerId != null && canReadPerformers
    ? createRouteLinkProps<HTMLAnchorElement>({ page: "performer", id: localPerformerId }, () => {
      onClose();
      onNavigate({ page: "performer", id: localPerformerId });
    })
    : null;

  const secondaryPerformerIds = conflictCandidates
    .filter((item) => item.performerId !== active.performerId)
    .map((item) => item.performerId);

  return (
    <EditModal title={batchLabel ? `Compare suggestion - ${batchLabel}` : "Compare suggestion"} open={open} onClose={onClose} maxWidthClassName="sm:max-w-4xl">
      <div className="space-y-5 py-5">
        <div className="grid gap-4 lg:grid-cols-2">
          <ComparePane
            eyebrow="Face in question"
            title={faceTitle}
            imageUrls={faceImages}
            imageIndex={faceImageIndex}
            onImageIndexChange={setFaceImageIndex}
            fallbackLabel="Unidentified face"
            footer={(
              <div className="space-y-2 text-xs text-secondary">
                <div>{face.appearanceCount ?? 0} appearance{(face.appearanceCount ?? 0) === 1 ? "" : "s"}</div>
                <div>{face.videoCount} video{face.videoCount === 1 ? "" : "s"} and {face.imageCount} image{face.imageCount === 1 ? "" : "s"}</div>
                <a {...faceLinkProps} className="inline-flex items-center gap-1 text-accent hover:underline">
                  Open face page
                </a>
              </div>
            )}
          />

          <ComparePane
            eyebrow={referenceOnly ? "Reference suggestion" : "Suggested performer"}
            title={active.performerName}
            titleBadge={isReferenceMatch ? (
              <span className="rounded-full border border-amber-500/30 bg-amber-500/10 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-amber-200">Reference DB</span>
            ) : undefined}
            imageUrls={suggestionImages}
            imageIndex={suggestionImageIndex}
            onImageIndexChange={setSuggestionImageIndex}
            fallbackLabel={active.performerName}
            alignTop
            footer={(
              <div className="space-y-2 text-xs text-secondary">
                <div>{formatPercent(active.confidence)}% confidence</div>
                {why ? <p>{why}</p> : <p>Review the side-by-side cover images before confirming the link.</p>}
                <div className="flex flex-wrap gap-2 pt-1">
                  {performerLinkProps ? (
                    <a {...performerLinkProps} className="inline-flex items-center gap-1 rounded-lg border border-border px-3 py-1.5 text-foreground transition-colors hover:border-accent hover:text-accent">
                      <Link2 className="h-3.5 w-3.5" />
                      Open performer
                    </a>
                  ) : null}
                  {active.externalUrl ? (
                    <a href={active.externalUrl} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 rounded-lg border border-border px-3 py-1.5 text-foreground transition-colors hover:border-accent hover:text-accent">
                      <ExternalLink className="h-3.5 w-3.5" />
                      Open external
                    </a>
                  ) : null}
                </div>
              </div>
            )}
          />
        </div>

        {isConflict ? (
          <ConflictChooser
            candidates={conflictCandidates}
            selectedPerformerId={active.performerId}
            onSelect={setSelectedPerformerId}
          />
        ) : null}

        <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-4">
          {canSetPerformerImage ? (
            <label className="flex items-center gap-2 rounded-lg border border-border bg-card/40 px-3 py-2 text-sm text-secondary">
              <input
                type="checkbox"
                checked={setPerformerImage}
                onChange={(event) => setSetPerformerImage(event.target.checked)}
                className="rounded border-border bg-surface accent-accent"
              />
              Use face image for this local performer
            </label>
          ) : <span />}
          <div className="flex flex-wrap justify-end gap-2">
            {onSkip ? (
              <button
                type="button"
                onClick={onSkip}
                disabled={disabled}
                className="inline-flex items-center gap-2 rounded-lg border border-border px-4 py-2 text-sm text-foreground transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
              >
                <SkipForward className="h-4 w-4" />
                Skip
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => onReject(active)}
              disabled={disabled}
              className="inline-flex items-center gap-2 rounded-lg border border-border px-4 py-2 text-sm text-foreground transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
            >
              <XCircle className="h-4 w-4" />
              Reject
            </button>
            {isConflict && onMerge ? (
              <button
                type="button"
                onClick={() => onMerge(active.performerId, secondaryPerformerIds)}
                disabled={disabled}
                className="inline-flex items-center gap-2 rounded-lg border border-accent/60 bg-accent/10 px-4 py-2 text-sm font-medium text-accent transition-colors hover:bg-accent/20 disabled:cursor-not-allowed disabled:opacity-50"
                title={`Merge all ${conflictCandidates.length} matches, keeping ${active.performerName} as the primary`}
              >
                <GitMerge className="h-4 w-4" />
                Merge into {truncate(active.performerName)}
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => onConfirm(active, { setPerformerImage: canSetPerformerImage && setPerformerImage })}
              disabled={disabled}
              className="inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
            >
              {isConflict ? `Use ${truncate(active.performerName)}` : referenceOnly ? "Import performer" : "Confirm link"}
            </button>
          </div>
        </div>
      </div>
    </EditModal>
  );
}

function ConflictChooser({
  candidates,
  selectedPerformerId,
  onSelect,
}: {
  candidates: ComparableSuggestion[];
  selectedPerformerId: number;
  onSelect: (performerId: number) => void;
}) {
  return (
    <section className="space-y-3 rounded-2xl border border-amber-500/30 bg-amber-500/5 p-4">
      <div>
        <div className="text-xs font-semibold uppercase tracking-wide text-amber-200">Possible duplicate — {candidates.length} sources</div>
        <p className="mt-1 text-sm text-secondary">
          This face matched more than one performer. Pick the one to use, or merge them into a single performer (the selected one becomes the primary and the others are added as aliases).
        </p>
      </div>
      <div className="space-y-2">
        {candidates.map((candidate) => {
          const isReferenceMatch = candidate.performerId < 0 || !!candidate.externalUrl;
          const selected = candidate.performerId === selectedPerformerId;
          return (
            <label
              key={candidate.performerId}
              className={`flex cursor-pointer items-center gap-3 rounded-xl border px-3 py-2 transition-colors ${selected ? "border-accent bg-accent/10" : "border-border bg-surface/60 hover:border-accent/50"}`}
            >
              <input
                type="radio"
                name="conflict-primary"
                checked={selected}
                onChange={() => onSelect(candidate.performerId)}
                className="accent-accent"
              />
              <div className="h-10 w-10 shrink-0 overflow-hidden rounded-lg bg-surface/80">
                {candidate.coverImageUrl ? (
                  <img src={candidate.coverImageUrl} alt={candidate.performerName} className="h-full w-full object-cover object-top" loading="lazy" />
                ) : (
                  <div className="flex h-full w-full items-center justify-center text-muted">
                    <Fingerprint className="h-5 w-5" />
                  </div>
                )}
              </div>
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <span className="truncate text-sm font-semibold text-foreground">{candidate.performerName}</span>
                  {isReferenceMatch ? (
                    <span className="rounded-full border border-amber-500/30 bg-amber-500/10 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-amber-200">Reference DB</span>
                  ) : null}
                  {selected ? (
                    <span className="rounded-full border border-accent/40 bg-accent/10 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-accent">Primary</span>
                  ) : null}
                </div>
                <div className="text-xs text-secondary">{formatPercent(candidate.confidence)}% confidence</div>
              </div>
            </label>
          );
        })}
      </div>
    </section>
  );
}

function ComparePane({
  eyebrow,
  title,
  titleBadge,
  imageUrls,
  imageIndex,
  onImageIndexChange,
  fallbackLabel,
  footer,
  alignTop = false,
}: {
  eyebrow: string;
  title: string;
  titleBadge?: React.ReactNode;
  imageUrls: string[];
  imageIndex: number;
  onImageIndexChange: (value: number) => void;
  fallbackLabel: string;
  footer: React.ReactNode;
  // Anchor the cover crop to the top so a portrait performer image never loses the head.
  alignTop?: boolean;
}) {
  const activeIndex = imageUrls.length === 0 ? 0 : Math.min(imageIndex, imageUrls.length - 1);
  const imageUrl = imageUrls[activeIndex];
  const canPage = imageUrls.length > 1;

  return (
    <section className="overflow-hidden rounded-2xl border border-border bg-card/50">
      <div className="relative h-[clamp(18rem,48vh,32rem)] bg-surface/70">
        {imageUrl ? (
          <CompareImage src={imageUrl} alt={title} alignTop={alignTop} />
        ) : (
          <div className="flex h-full w-full items-center justify-center text-muted">
            <Fingerprint className="h-12 w-12" />
          </div>
        )}
        {canPage ? (
          <>
            <button
              type="button"
              onClick={() => onImageIndexChange(activeIndex === 0 ? imageUrls.length - 1 : activeIndex - 1)}
              className="absolute left-2 top-1/2 inline-flex h-9 w-9 -translate-y-1/2 items-center justify-center rounded-full border border-white/20 bg-black/55 text-white transition-colors hover:bg-black/75"
              title="Previous image"
              aria-label="Previous image"
            >
              <ChevronLeft className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={() => onImageIndexChange(activeIndex === imageUrls.length - 1 ? 0 : activeIndex + 1)}
              className="absolute right-2 top-1/2 inline-flex h-9 w-9 -translate-y-1/2 items-center justify-center rounded-full border border-white/20 bg-black/55 text-white transition-colors hover:bg-black/75"
              title="Next image"
              aria-label="Next image"
            >
              <ChevronRight className="h-4 w-4" />
            </button>
            <div className="absolute bottom-2 right-2 rounded-full bg-black/60 px-2 py-0.5 text-[11px] font-medium text-white">
              {activeIndex + 1}/{imageUrls.length}
            </div>
          </>
        ) : null}
      </div>
      <div className="space-y-3 p-4">
        <div>
          <div className="text-[11px] font-semibold uppercase tracking-wide text-muted">{eyebrow}</div>
          <div className="mt-1 flex flex-wrap items-center gap-2">
            <span className="text-base font-semibold text-foreground">{title || fallbackLabel}</span>
            {titleBadge}
          </div>
        </div>
        {footer}
      </div>
    </section>
  );
}

function CompareImage({ src, alt, alignTop }: { src: string; alt: string; alignTop?: boolean }) {
  const wrapperRef = useRef<HTMLDivElement>(null);
  const [imageAspect, setImageAspect] = useState<number | null>(null);
  const [frameAspect, setFrameAspect] = useState<number | null>(null);

  useEffect(() => {
    const wrapper = wrapperRef.current;
    if (!wrapper) return;

    const updateFrameAspect = () => {
      const rect = wrapper.getBoundingClientRect();
      setFrameAspect(rect.width > 0 && rect.height > 0 ? rect.width / rect.height : null);
    };

    updateFrameAspect();
    const observer = new ResizeObserver(updateFrameAspect);
    observer.observe(wrapper);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    setImageAspect(null);
  }, [src]);

  const shouldContain = imageAspect != null && frameAspect != null
    ? imageAspect > frameAspect * 1.12 || imageAspect < frameAspect / 1.12
    : false;

  return (
    <div ref={wrapperRef} className="h-full w-full">
      <img
        src={src}
        alt={alt}
        className={`h-full w-full ${shouldContain ? "object-contain" : "object-cover"} ${!shouldContain && alignTop ? "object-top" : ""}`}
        loading="lazy"
        onLoad={(event) => {
          const image = event.currentTarget;
          setImageAspect(image.naturalWidth > 0 && image.naturalHeight > 0 ? image.naturalWidth / image.naturalHeight : null);
        }}
      />
    </div>
  );
}

function readEvidence(suggestion: ComparableSuggestion): FaceSuggestionEvidence[] {
  return "evidence" in suggestion ? suggestion.evidence : [];
}

function readWhy(suggestion: ComparableSuggestion) {
  return "why" in suggestion ? suggestion.why : undefined;
}

function readConflictGroupId(suggestion: ComparableSuggestion | null) {
  return suggestion && "conflictGroupId" in suggestion ? suggestion.conflictGroupId : undefined;
}

function readReferenceWillRefreshFromMetadata(suggestion: ComparableSuggestion) {
  return "referenceWillRefreshFromMetadata" in suggestion ? suggestion.referenceWillRefreshFromMetadata === true : false;
}

// Reference (metadata-server) link info to send back on accept, so the host can record the remote id on
// the linked performer and scrape it when enabled. Empty for non-reference suggestions (or the cached
// top-suggestion projection, which does not carry the endpoint).
export function readReferenceLinkInfo(suggestion: FaceSuggestion | FaceTopSuggestion):
  { referenceEndpoint?: string; referenceExternalId?: string; referenceUpdateMetadata?: boolean } {
  if (!("referenceEndpoint" in suggestion) || !suggestion.referenceEndpoint || !suggestion.referenceExternalId) {
    return {};
  }
  return {
    referenceEndpoint: suggestion.referenceEndpoint,
    referenceExternalId: suggestion.referenceExternalId,
    referenceUpdateMetadata: readReferenceWillRefreshFromMetadata(suggestion),
  };
}

function readLocalPerformerId(suggestion: ComparableSuggestion) {
  return suggestion.localPerformerId ?? (suggestion.performerId > 0 ? suggestion.performerId : undefined);
}

function buildCarouselImageUrls(primaryUrl: string | undefined, samples: Array<string | undefined>, skipFirstSampleWithPrimary = false) {
  const usableSamples = primaryUrl && skipFirstSampleWithPrimary ? samples.slice(1) : samples;
  return uniqueImageUrls([primaryUrl, ...usableSamples]).slice(0, 3);
}

function uniqueImageUrls(values: Array<string | undefined>) {
  return values
    .map((value) => value?.trim())
    .filter((value): value is string => !!value)
    .filter((value, index, all) => all.indexOf(value) === index);
}

function formatPercent(value: number) {
  const scaled = value <= 1 ? value * 100 : value;
  return Math.max(0, Math.min(100, Math.round(scaled)));
}

function truncate(value: string, max = 24) {
  return value.length > max ? `${value.slice(0, max - 1)}…` : value;
}
