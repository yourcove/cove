import { useMemo, useState } from "react";
import { ChevronDown, ChevronUp, Loader2, Scissors, UserX } from "lucide-react";
import type { Face } from "../../api/types";
import { SuggestedNameMarker } from "../../components/EntityCards";
import { createNestedRouteLinkProps } from "../../components/cardNavigation";
import { FaceCrop, FaceStack } from "./FaceCrops";
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
  detectionCount: number;
  faces: VideoFace[];
}

/**
 * The people a video's faces show, one card per person as the Segments tab groups them, rather than one card per
 * face: detection splits a person into several faces, and a performer's faces are numbered across the whole library.
 * A card with several faces, or a suggested one, lists its faces, each with its own actions.
 */
const cardClassName =
  "flex w-full items-center gap-3 rounded-xl border border-border bg-card/70 px-3 py-2 text-left transition-colors hover:border-accent";

export function VideoFacesList({
  videoId,
  videoFaces,
  personColors,
  canDecideFaces = false,
  onMarkFaceNotPresent,
  markingFaceId,
  onSplitFace,
  onNavigate,
}: {
  videoId: number;
  videoFaces: VideoFace[];
  personColors?: ReadonlyMap<string, string>;
  canDecideFaces?: boolean;
  onMarkFaceNotPresent?: (faceId: number) => void;
  markingFaceId?: number;
  onSplitFace?: (face: Face) => void;
  onNavigate: (route: any) => void;
}) {
  const decision = useFaceSuggestionDecision(videoId, onNavigate);
  const faceLink = (faceId: number) =>
    createNestedRouteLinkProps<HTMLAnchorElement>({ page: "face", id: faceId }, () =>
      onNavigate({ page: "face", id: faceId }),
    );
  const [openKeys, setOpenKeys] = useState<Set<string>>(new Set());

  const people = useMemo(() => {
    const byKey = new Map<string, PersonCard>();
    for (const entry of videoFaces) {
      const person = facePerson(entry.face.id, entry.face);
      const card = byKey.get(person.key) ?? {
        key: person.key,
        name: person.name,
        suggested: person.suggested,
        detectionCount: 0,
        faces: [],
      };
      card.detectionCount += entry.detectionCount;
      card.faces.push(entry);
      byKey.set(person.key, card);
    }
    return Array.from(byKey.values()).sort((left, right) => right.detectionCount - left.detectionCount);
  }, [videoFaces]);

  const toggle = (key: string) =>
    setOpenKeys((current) => {
      const next = new Set(current);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  return (
    <div className="flex flex-wrap gap-2">
      {people.map((person) => {
        const listsFaces = person.faces.length > 1 || person.suggested;
        const open = listsFaces && openKeys.has(person.key);
        const single = person.faces[0];
        const color = personColors?.get(person.key);
        const cardContent = (
          <>
            {person.faces.length > 1 ? (
              <FaceStack
                faces={person.faces.map((entry) => ({ faceId: entry.face.id, face: entry.face }))}
                size="h-10 w-10"
              />
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
              <div className="mt-1 flex items-center gap-1 text-xs text-secondary">
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
                  open ? (
                    <ChevronUp aria-hidden className="h-3 w-3 shrink-0" />
                  ) : (
                    <ChevronDown aria-hidden className="h-3 w-3 shrink-0" />
                  )
                ) : null}
              </div>
            </div>
          </>
        );
        return (
          <div
            key={person.key}
            // A person with several faces, or with their faces listed, takes the full width.
            className={
              open || person.faces.length > 1
                ? "basis-full"
                : "min-w-[180px] flex-1 sm:flex-none sm:basis-[calc(50%-0.25rem)]"
            }
          >
            <div className="group relative flex items-stretch">
              {listsFaces ? (
                <button type="button" onClick={() => toggle(person.key)} aria-expanded={open} className={cardClassName}>
                  {cardContent}
                </button>
              ) : (
                // A person seen through one face opens that face, as a link that can open in a new tab.
                <a {...faceLink(single.face.id)} className={cardClassName}>
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
            {open ? (
              <ul className="mt-1 space-y-1 rounded-xl border border-border bg-surface/30 p-2">
                {person.faces.map((entry) => {
                  const suggestion = person.suggested ? entry.face.topSuggestion : undefined;
                  const label = faceKeyLabel(entry.face, entry.face.id);
                  return (
                    <li key={entry.face.id} className="flex flex-wrap items-center gap-2 text-xs">
                      <FaceCrop face={entry.face} className="h-8 w-8" />
                      <span className="min-w-0 flex-1 truncate text-foreground">{label}</span>
                      <span className="shrink-0 text-secondary">
                        {entry.detectionCount} detection{entry.detectionCount === 1 ? "" : "s"}
                      </span>
                      {suggestion && canDecideFaces ? (
                        <SuggestionDecision
                          performerId={suggestion.performerId}
                          pending={decision.decidingFaceId === entry.face.id}
                          onDecide={(performerId, choice) => decision.decide(entry.face.id, performerId, choice)}
                          onReview={() => onNavigate({ page: "face", id: entry.face.id })}
                        />
                      ) : null}
                      <a {...faceLink(entry.face.id)} className="shrink-0 text-accent hover:underline">
                        Open face
                      </a>
                      <FaceActions
                        entry={entry}
                        title={label}
                        className="flex"
                        marking={markingFaceId === entry.face.id}
                        onMarkFaceNotPresent={onMarkFaceNotPresent}
                        onSplitFace={onSplitFace}
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
            ) : null}
          </div>
        );
      })}
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
