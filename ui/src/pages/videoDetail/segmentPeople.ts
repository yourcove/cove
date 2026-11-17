import type { Face, ResolvedSpan, Segment } from "../../api/types";
import { faceStoredName } from "../../utils/faceDisplay";
import { rawSegmentFaceId, resolveSpanName } from "../segments/segmentDisplayUtils";

/** Who, or what, a segment is about, for grouping a video's segments by person. */
export interface SegmentPerson {
  /** Segments with the same key are one group. */
  key: string;
  name: string;
  /** The person is only a face's suggested performer, not one it is linked to. */
  suggested: boolean;
  /** The face the segment shows, when it is a face segment. */
  faceId?: number;
}

/**
 * The person a segment belongs to. A face segment belongs to its face's linked performer, else the name the face was
 * given, so all of a person's faces in a video are one group; a face with neither belongs to its suggested performer,
 * kept apart from the confirmed one; and a face with none of these is a group of its own. A performer segment joins
 * the performer's face segments, and anything else is grouped by its name.
 */
export function spanPerson(
  span: Pick<ResolvedSpan, "tagId" | "tagName" | "kind" | "sourceKey" | "segmentIds">,
  rawSegmentsById: ReadonlyMap<number, Segment>,
  facesById?: ReadonlyMap<number, Face>,
): SegmentPerson {
  const first = (span.segmentIds ?? []).map((id) => rawSegmentsById.get(id)).find((segment) => segment != null);
  const faceId = first ? rawSegmentFaceId(first) : undefined;

  if (first && faceId != null) {
    const face = facesById?.get(faceId);
    const performerId = face?.performerId ?? first.performerId;
    const performerName = face?.performerName?.trim() || first.performerName?.trim();
    if (performerId != null && performerName) {
      return { key: personNameKey(performerName), name: performerName, suggested: false, faceId };
    }
    // A name the face was given counts before its suggestion, as everywhere else segments are named.
    const ownName = face ? faceStoredName({ ...face, performerId: undefined, performerName: undefined }) : undefined;
    if (ownName) return { key: personNameKey(ownName), name: ownName, suggested: false, faceId };
    const suggestion = face && !face.ignored && face.mergedIntoFaceId == null ? face.topSuggestion : undefined;
    const suggestedName = suggestion?.performerName?.trim();
    if (suggestion && suggestedName) {
      return { key: `suggested:${suggestion.performerId}`, name: suggestedName, suggested: true, faceId };
    }
    return { key: `face:${faceId}`, name: "Unnamed face", suggested: false, faceId };
  }

  if (first && span.tagId == null && first.kind?.toLowerCase() === "performer") {
    const name = first.performerName?.trim();
    if (name) return { key: personNameKey(name), name, suggested: false };
  }

  const shown = resolveSpanName(span, rawSegmentsById, facesById);
  return {
    key: shown.suggested ? `suggested-name:${shown.name}` : personNameKey(shown.name),
    name: shown.name,
    suggested: shown.suggested,
  };
}

// A linked performer, a face carrying the same name and a performer segment are one person, so the name is the key
// that brings them into one group.
function personNameKey(name: string) {
  return `name:${name.trim().toLowerCase()}`;
}

/** A face's own label for telling a person's faces apart: a name it was given, else its key, else its id. */
export function faceKeyLabel(face: Face | undefined, faceId: number) {
  if (!face) return `Face #${faceId}`;
  return (
    faceStoredName({ ...face, performerId: undefined, performerName: undefined }) ||
    face.label?.trim() ||
    `Face #${faceId}`
  );
}
