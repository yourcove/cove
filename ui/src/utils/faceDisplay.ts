import type { Face } from "../api/types";

type FaceNameFields = Pick<
  Face,
  | "id"
  | "label"
  | "primarySourceKey"
  | "performerId"
  | "performerName"
  | "performerFaceIndex"
  | "performerFaceCount"
  | "topSuggestion"
>;

/**
 * A face cluster's label is only a name when someone (or a reference match) put a name there. The
 * analysis extension stamps the cluster's own source key as a placeholder label ("face-0042") whenever
 * it has nothing better, so a label still equal to the source key carries no information and must not
 * win over a suggested performer.
 */
function storedLabel(face: FaceNameFields): string | undefined {
  const label = face.label?.trim();
  if (!label) return undefined;
  const sourceKey = face.primarySourceKey?.trim();
  return sourceKey && label.toLowerCase() === sourceKey.toLowerCase() ? undefined : label;
}

/**
 * The name this face carries on its own — the linked performer (disambiguated as "<performer> N" when
 * that performer has more than one linked face), else its label. Undefined while the cluster is still
 * anonymous, which callers that are themselves about a suggestion use so they don't echo that
 * suggestion back as the face's own name.
 */
export function faceStoredName(face: FaceNameFields): string | undefined {
  if (face.performerId && face.performerName) {
    return (face.performerFaceCount ?? 0) > 1 && (face.performerFaceIndex ?? 0) > 0
      ? `${face.performerName} ${face.performerFaceIndex}`
      : face.performerName;
  }
  return storedLabel(face);
}

/** The top suggested performer's name, when this face has a suggestion to fall back on. */
function suggestedName(face: FaceNameFields): string | undefined {
  return face.topSuggestion?.performerName?.trim() || undefined;
}

/**
 * Display name for a face cluster: its own name, else the top suggested performer — a face whose best
 * candidate is known reads better as that candidate than as an opaque cluster id — else "Face #id".
 */
export function faceDisplayName(face: FaceNameFields): string {
  return faceStoredName(face) ?? suggestedName(face) ?? `Face #${face.id}`;
}

/**
 * The name a face shows: its own name, else its top suggested performer flagged as a suggestion. Undefined when
 * it has neither.
 */
export function faceShownName(face: FaceNameFields): { name: string; suggested: boolean } | undefined {
  const stored = faceStoredName(face);
  if (stored) return { name: stored, suggested: false };
  const suggested = suggestedName(face);
  return suggested ? { name: suggested, suggested: true } : undefined;
}
