import { useCallback, useMemo, useRef, useState } from "react";
import { ChevronDown, Loader2, Scissors, UserX } from "lucide-react";
import type { Face } from "../../api/types";
import { SuggestedNameMarker } from "../../components/EntityCards";
import { createNestedRouteLinkProps } from "../../components/cardNavigation";
import { AnchoredPopover } from "../../components/duplicates/AnchoredPopover";
import { FaceCrop } from "./FaceCrops";
import { SuggestionDecision } from "./SuggestionDecision";
import { faceKeyLabel, facePerson } from "./segmentPeople";
import { useFaceSuggestionDecision } from "./useFaceSuggestionDecision";

export interface VideoFace {
  face: Face;
  detectionCount: number;
  trackCount?: number;
}

interface PersonCard {
  key: string;
  name: string;
  suggested: boolean;
  // The performer the card names, linked or suggested; none for a face known only by a name of its own, or unnamed.
  performerId?: number;
  detectionCount: number;
  faces: VideoFace[];
}

const noPerformers: ReadonlyArray<{ id: number; name: string }> = [];

type FaceLink = (faceId: number) => ReturnType<typeof createNestedRouteLinkProps<HTMLAnchorElement>>;

const cardClassName =
  "flex w-full items-center gap-3 rounded-xl border bg-card/70 px-3 py-2 text-left transition-colors hover:border-accent";

/**
 * The people a video's faces show, one card per person as the Segments tab groups them, rather than one card per
 * face: detection splits a person into several faces, and a performer's faces are numbered across the whole library.
 * A card with several faces, or a suggested one, opens a popover listing its faces, each with its own actions, so
 * every card keeps the same width.
 *
 * Linked people come first, in the order of the video's performers above the list, then suggested people in that same
 * order, then unnamed faces; people the video's performers do not account for follow in each group by detections.
 */
export function VideoFacesList({
  videoId,
  videoFaces,
  performers = noPerformers,
  personColors,
  canDecideFaces = false,
  onMarkFaceNotPresent,
  markingFaceId,
  onSplitFace,
  onNavigate,
}: {
  videoId: number;
  videoFaces: VideoFace[];
  /** The video's performers, in the order their cards show. */
  performers?: ReadonlyArray<{ id: number; name: string }>;
  personColors?: ReadonlyMap<string, string>;
  canDecideFaces?: boolean;
  onMarkFaceNotPresent?: (faceId: number) => void;
  markingFaceId?: number;
  onSplitFace?: (face: Face) => void;
  onNavigate: (route: any) => void;
}) {
  const decision = useFaceSuggestionDecision(videoId, onNavigate);
  const faceLink: FaceLink = (faceId) =>
    createNestedRouteLinkProps<HTMLAnchorElement>({ page: "face", id: faceId }, () =>
      onNavigate({ page: "face", id: faceId }),
    );

  const people = useMemo(() => {
    const byKey = new Map<string, PersonCard>();
    for (const entry of videoFaces) {
      const person = facePerson(entry.face.id, entry.face);
      const card = byKey.get(person.key) ?? {
        key: person.key,
        name: person.name,
        suggested: person.suggested,
        performerId: person.suggested ? entry.face.topSuggestion?.performerId : (entry.face.performerId ?? undefined),
        detectionCount: 0,
        faces: [],
      };
      card.detectionCount += entry.detectionCount;
      card.faces.push(entry);
      byKey.set(person.key, card);
    }
    const cards = Array.from(byKey.values());
    const rankOf = (card: PersonCard) => {
      const name = card.name.trim().toLowerCase();
      const index = performers.findIndex(
        (performer) =>
          (card.performerId != null && performer.id === card.performerId) ||
          performer.name.trim().toLowerCase() === name,
      );
      return index < 0 ? performers.length : index;
    };
    const ranks = new Map(cards.map((card) => [card.key, rankOf(card)]));
    const group = (card: PersonCard) => (card.suggested ? 1 : card.key.startsWith("face:") ? 2 : 0);
    return cards.sort(
      (left, right) =>
        group(left) - group(right) ||
        ranks.get(left.key)! - ranks.get(right.key)! ||
        right.detectionCount - left.detectionCount,
    );
  }, [performers, videoFaces]);

  return (
    <div className="flex flex-wrap gap-2">
      {people.map((person) => (
        <PersonEntry
          key={person.key}
          person={person}
          color={personColors?.get(person.key)}
          canDecideFaces={canDecideFaces}
          decision={decision}
          faceLink={faceLink}
          markingFaceId={markingFaceId}
          onMarkFaceNotPresent={onMarkFaceNotPresent}
          onSplitFace={onSplitFace}
          onNavigate={onNavigate}
        />
      ))}
    </div>
  );
}

function PersonEntry({
  person,
  color,
  canDecideFaces,
  decision,
  faceLink,
  markingFaceId,
  onMarkFaceNotPresent,
  onSplitFace,
  onNavigate,
}: {
  person: PersonCard;
  color?: string;
  canDecideFaces: boolean;
  decision: ReturnType<typeof useFaceSuggestionDecision>;
  faceLink: FaceLink;
  markingFaceId?: number;
  onMarkFaceNotPresent?: (faceId: number) => void;
  onSplitFace?: (face: Face) => void;
  onNavigate: (route: any) => void;
}) {
  const buttonRef = useRef<HTMLButtonElement>(null);
  const [popoverOpen, setOpen] = useState(false);
  const close = useCallback(() => setOpen(false), []);
  const listsFaces = person.faces.length > 1 || person.suggested;
  // A person can lose all but one face while their popover is open, leaving a plain link with nothing to list; the
  // popover closes then rather than reopening by itself should they gain a face again.
  if (popoverOpen && !listsFaces) setOpen(false);
  const open = popoverOpen && listsFaces;
  const single = person.faces[0];
  const cardContent = (
    <>
      {person.faces.length > 1 ? (
        // The person's leading face, with a second one tucked into its corner to show there are more.
        <span aria-hidden className="relative shrink-0">
          <FaceCrop face={single.face} className="h-14 w-14 !rounded-lg" />
          <FaceCrop face={person.faces[1].face} className="absolute -bottom-1 -right-1 h-7 w-7 ring-2 ring-card" />
        </span>
      ) : (
        <FaceCrop face={single.face} className="h-14 w-14 !rounded-lg" />
      )}
      <div className="min-w-0 flex-1">
        {/* The name wraps rather than shrinking to a few letters beside the "suggested" marker. */}
        <div className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-0.5 text-sm font-medium text-foreground">
          {color ? (
            // The person's colour on the timeline's face lanes and in the Segments tab.
            <span aria-hidden className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ backgroundColor: color }} />
          ) : null}
          {person.suggested ? (
            <>
              <span aria-hidden className="break-words">
                {person.name}
              </span>
              <SuggestedNameMarker />
              <span className="sr-only">{`${person.name} (suggested)`}</span>
            </>
          ) : (
            <span className="break-words">{person.name}</span>
          )}
        </div>
        <div className="mt-1 flex flex-wrap items-center gap-x-1 text-xs text-secondary">
          <span>
            {person.detectionCount} detection{person.detectionCount === 1 ? "" : "s"}
          </span>
          {person.faces.length > 1 ? (
            <>
              <span aria-hidden>·</span>
              <span>{person.faces.length} faces</span>
            </>
          ) : null}
          {listsFaces ? (
            <ChevronDown aria-hidden className={`h-3 w-3 shrink-0 transition-transform ${open ? "rotate-180" : ""}`} />
          ) : null}
        </div>
      </div>
    </>
  );

  return (
    <div className="min-w-[180px] flex-1 sm:flex-none sm:basis-[calc(50%-0.25rem)]">
      <div className="group relative flex h-full items-stretch">
        {listsFaces ? (
          <button
            ref={buttonRef}
            type="button"
            onClick={() => setOpen((current) => !current)}
            aria-expanded={open}
            aria-haspopup="dialog"
            className={`${cardClassName} ${open ? "border-accent" : "border-border"}`}
          >
            {cardContent}
          </button>
        ) : (
          // A person seen through one face opens that face, as a link that can open in a new tab.
          <a {...faceLink(single.face.id)} className={`${cardClassName} border-border`}>
            {cardContent}
          </a>
        )}
        {!listsFaces ? (
          <FaceActions
            entry={single}
            title={person.name}
            // On a wide screen with a mouse the actions appear on hover or keyboard focus; on a narrow or touch screen they show.
            className={`absolute right-1 top-1 flex rounded-md bg-surface/80 transition-opacity ${markingFaceId === single.face.id ? "" : "md:pointer-fine:opacity-0 md:pointer-fine:group-hover:opacity-100 md:pointer-fine:focus-within:opacity-100"}`}
            marking={markingFaceId === single.face.id}
            onMarkFaceNotPresent={onMarkFaceNotPresent}
            onSplitFace={onSplitFace}
          />
        ) : null}
      </div>
      {listsFaces ? (
        <AnchoredPopover
          anchorRef={buttonRef}
          open={open}
          onClose={close}
          width={360}
          align="start"
          label={`Faces of ${person.name}${person.suggested ? " (suggested)" : ""}`}
        >
          <ul className="space-y-1 p-2">
            {person.faces.map((entry) => {
              const suggestion = person.suggested ? entry.face.topSuggestion : undefined;
              const label = faceKeyLabel(entry.face, entry.face.id);
              const detections = `${entry.detectionCount} detection${entry.detectionCount === 1 ? "" : "s"}`;
              return (
                <li key={entry.face.id} className="flex flex-wrap items-center gap-2 text-xs">
                  {/* The face's crop and name open the face, as a link that can open in a new tab. */}
                  <a
                    {...faceLink(entry.face.id)}
                    className="flex min-w-0 flex-1 items-center gap-2 rounded-md text-foreground hover:text-accent hover:underline"
                  >
                    <FaceCrop face={entry.face} className="h-8 w-8" />
                    <span className="min-w-0 truncate">{label}</span>
                  </a>
                  <span className="shrink-0 tabular-nums text-secondary" title={detections}>
                    <span aria-hidden>{entry.detectionCount}×</span>
                    <span className="sr-only">{detections}</span>
                  </span>
                  {suggestion && canDecideFaces ? (
                    <SuggestionDecision
                      performerId={suggestion.performerId}
                      pending={decision.decidingFaceId === entry.face.id}
                      onDecide={(performerId, choice) => decision.decide(entry.face.id, performerId, choice)}
                      onReview={() => onNavigate({ page: "face", id: entry.face.id })}
                    />
                  ) : null}
                  <FaceActions
                    entry={entry}
                    title={label}
                    className="flex"
                    marking={markingFaceId === entry.face.id}
                    onMarkFaceNotPresent={onMarkFaceNotPresent}
                    // The split dialog opens over the page, so the list closes first rather than covering it.
                    onSplitFace={
                      onSplitFace
                        ? (face) => {
                            setOpen(false);
                            onSplitFace(face);
                          }
                        : undefined
                    }
                  />
                  {decision.decisionError?.faceId === entry.face.id ? (
                    <span role="alert" className="basis-full text-red-400">
                      {decision.decisionError.error.message}
                    </span>
                  ) : null}
                </li>
              );
            })}
          </ul>
        </AnchoredPopover>
      ) : null}
    </div>
  );
}

// Separating people out of a face, and marking a face as not present in this video.
function FaceActions({
  entry,
  title,
  className,
  marking,
  onMarkFaceNotPresent,
  onSplitFace,
}: {
  entry: VideoFace;
  title: string;
  className: string;
  marking: boolean;
  onMarkFaceNotPresent?: (faceId: number) => void;
  onSplitFace?: (face: Face) => void;
}) {
  const trackCount = entry.trackCount ?? 0;
  // Only offer the split where there is something to split: the face has to have been detected as more than one
  // separate run in this video.
  const canSplit = !!onSplitFace && trackCount > 1;
  if (!canSplit && !onMarkFaceNotPresent) return null;
  return (
    <div className={`gap-1 ${className}`}>
      {canSplit ? (
        <button
          type="button"
          title={`Detected as ${trackCount} separate appearances — separate a different person out of this face`}
          aria-label="Separate people in this face"
          onClick={() => onSplitFace(entry.face)}
          className="rounded-md p-1 text-muted transition-colors hover:text-accent pointer-coarse:p-1.5"
        >
          <Scissors className="h-3.5 w-3.5" />
        </button>
      ) : null}
      {onMarkFaceNotPresent ? (
        <button
          type="button"
          title="This face is not actually present in this video"
          aria-label="Mark face not present in this video"
          disabled={marking}
          onClick={() => {
            if (
              window.confirm(
                `Mark "${title}" as NOT present in this video?\n\nIts occurrences here (and other videos that match them) will be split off into the correct face.`,
              )
            ) {
              onMarkFaceNotPresent(entry.face.id);
            }
          }}
          className="rounded-md p-1 text-muted transition-colors hover:text-red-300 disabled:cursor-not-allowed pointer-coarse:p-1.5"
        >
          {marking ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <UserX className="h-3.5 w-3.5" />}
        </button>
      ) : null}
    </div>
  );
}
