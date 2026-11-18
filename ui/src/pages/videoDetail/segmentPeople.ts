import type { Face, ResolvedSpan, Segment } from "../../api/types";
import { faceStoredName } from "../../utils/faceDisplay";
import { rawSegmentFaceId, resolveSpanName, type ShownName } from "../segments/segmentDisplayUtils";

/** Who, or what, a segment is about, for grouping a video's segments by person. */
export interface SegmentPerson {
  /** Segments with the same key are one group. */
  key: string;
  name: string;
  /** The person is only a face's suggested performer, not one it is linked to. */
  suggested: boolean;
  /** The face the segment shows, when it is a face segment. */
  faceId?: number;
  /** Which section of the Segments tab the group belongs in: people first, then tags, then anything else. */
  section: SegmentSection;
}

export type SegmentSection = "people" | "tags" | "other";

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

  if (first && faceId != null) return facePerson(faceId, facesById?.get(faceId), first);

  if (first && span.tagId == null && first.kind?.toLowerCase() === "performer") {
    const name = first.performerName?.trim();
    if (name) return { key: personNameKey(name), name, suggested: false, section: "people" };
  }

  const shown = resolveSpanName(span, rawSegmentsById, facesById);
  const isTag = span.tagId != null || first?.tagId != null;
  return {
    key: `${isTag ? "tag" : "other"}:${shown.name.trim().toLowerCase()}`,
    name: shown.name,
    suggested: shown.suggested,
    section: isTag ? "tags" : "other",
  };
}

/**
 * The person a face is: its linked performer, else the name it was given, else its suggested performer marked as
 * suggested, else an unnamed face of its own. A face segment can stand in for the face's performer while the face
 * itself has not loaded.
 */
export function facePerson(
  faceId: number,
  face: Face | undefined,
  segment?: Pick<Segment, "performerId" | "performerName">,
): SegmentPerson {
  const performerId = face?.performerId ?? segment?.performerId;
  const performerName = face?.performerName?.trim() || segment?.performerName?.trim();
  if (performerId != null && performerName) {
    return { key: personNameKey(performerName), name: performerName, suggested: false, faceId, section: "people" };
  }
  // A name the face was given counts before its suggestion, as everywhere else segments are named.
  const ownName = face ? faceStoredName({ ...face, performerId: undefined, performerName: undefined }) : undefined;
  if (ownName) return { key: personNameKey(ownName), name: ownName, suggested: false, faceId, section: "people" };
  const suggestion = face && !face.ignored && face.mergedIntoFaceId == null ? face.topSuggestion : undefined;
  const suggestedName = suggestion?.performerName?.trim();
  if (suggestion && suggestedName) {
    return {
      key: `suggested:${suggestion.performerId}`,
      name: suggestedName,
      suggested: true,
      faceId,
      section: "people",
    };
  }
  return { key: `face:${faceId}`, name: "Unnamed face", suggested: false, faceId, section: "people" };
}

/**
 * A segment's name in a list of the video's segments: a face segment is named after its person, as its group and its
 * timeline bar are, rather than after its own face's numbered name or placeholder key; anything else keeps the shared
 * segment naming.
 */
export function spanShownName(
  span: Pick<ResolvedSpan, "tagId" | "tagName" | "kind" | "sourceKey" | "segmentIds">,
  rawSegmentsById: ReadonlyMap<number, Segment>,
  facesById?: ReadonlyMap<number, Face>,
): ShownName {
  const person = spanPerson(span, rawSegmentsById, facesById);
  if (person.faceId != null) return { name: person.name, suggested: person.suggested };
  return resolveSpanName(span, rawSegmentsById, facesById);
}

// Muted colours that read on the dark timeline and as small dots beside the Segments tab's groups.
const PERSON_COLORS = ["#4d8569", "#5a7ca5", "#a07a3f", "#93658a", "#7d8842", "#4a807b", "#7963a1", "#a35d4e"];

/**
 * A colour for each person in a video, the same on the timeline's face lanes and in the Segments tab. People get
 * different colours in a fixed order, so two people only share one when a video has more people than colours.
 */
export function assignPersonColors(personKeys: Iterable<string>): ReadonlyMap<string, string> {
  const keys = Array.from(new Set(personKeys)).sort();
  return new Map(keys.map((key, index) => [key, PERSON_COLORS[index % PERSON_COLORS.length]] as const));
}

/** The people a video's face segments and face detections show, for colouring them. */
export function videoPersonKeys(
  spans: Pick<ResolvedSpan, "tagId" | "tagName" | "kind" | "sourceKey" | "segmentIds">[],
  rawSegmentsById: ReadonlyMap<number, Segment>,
  facesById: ReadonlyMap<number, Face>,
): string[] {
  const keys: string[] = [];
  for (const span of spans) {
    const person = spanPerson(span, rawSegmentsById, facesById);
    if (person.faceId != null) keys.push(person.key);
  }
  for (const [faceId, face] of facesById) keys.push(facePerson(faceId, face).key);
  return keys;
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
