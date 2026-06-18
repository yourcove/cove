import type { Face } from "../api/types";

type FaceNameFields = Pick<
  Face,
  "id" | "label" | "performerId" | "performerName" | "performerFaceIndex" | "performerFaceCount"
>;

/**
 * Display name for a face cluster: the linked performer's name — disambiguated as "<performer> N"
 * when that performer has more than one linked face — else the face's own label, else "Face #id".
 */
export function faceDisplayName(face: FaceNameFields): string {
  if (face.performerId && face.performerName) {
    return (face.performerFaceCount ?? 0) > 1 && (face.performerFaceIndex ?? 0) > 0
      ? `${face.performerName} ${face.performerFaceIndex}`
      : face.performerName;
  }
  return face.label?.trim() || `Face #${face.id}`;
}
