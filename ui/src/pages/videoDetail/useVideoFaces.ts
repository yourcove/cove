import { useMemo } from "react";
import { useQueries } from "@tanstack/react-query";
import { faces } from "../../api/client";
import type { Detection, Face, Segment } from "../../api/types";
import { isFaceTimelineSegment } from "./timelineFaces";
import type { VideoFace } from "./VideoFacesList";

/**
 * The faces a video's detections and face segments show, each with how often and in how many separate runs it was
 * detected, and the same faces by id for naming them. The video page and the segment page load them the same way,
 * so a face is named alike on both.
 */
export function useVideoFaces(
  detections: Pick<Detection, "refId" | "refKind" | "groupKey">[],
  segments: Segment[],
  enabled: boolean,
): { videoFaces: VideoFace[]; facesById: ReadonlyMap<number, Face> } {
  const videoFaceIds = useMemo(() => {
    const ids = new Set<number>();
    for (const detection of detections) {
      if (detection.refId != null && detection.refKind?.toLowerCase() === "face") {
        ids.add(detection.refId);
      }
    }

    for (const segment of segments) {
      if (segment.refId != null && isFaceTimelineSegment(segment)) {
        ids.add(Number(segment.refId));
      }
    }

    return Array.from(ids);
  }, [detections, segments]);

  // Combining into the faces' data keeps one array while no face changes, so what is built from the faces is not
  // rebuilt on every render, such as each time the player's position updates.
  const loadedFaces = useQueries({
    queries: videoFaceIds.map((faceId) => ({
      queryKey: ["face", faceId],
      queryFn: () => faces.get(faceId),
      enabled,
    })),
    combine: loadedFaceData,
  });

  const videoFaces = useMemo(() => {
    const countsByFaceId = new Map<number, number>();
    // Detections carry the track (group key) they came from, so the number of distinct group keys is
    // how many separate appearances the face has in this video — and whether it can be split at all.
    const trackKeysByFaceId = new Map<number, Set<string>>();
    for (const detection of detections) {
      if (detection.refId != null && detection.refKind?.toLowerCase() === "face") {
        countsByFaceId.set(detection.refId, (countsByFaceId.get(detection.refId) ?? 0) + 1);
        if (detection.groupKey) {
          const keys = trackKeysByFaceId.get(detection.refId) ?? new Set<string>();
          keys.add(detection.groupKey.split(":span-")[0]);
          trackKeysByFaceId.set(detection.refId, keys);
        }
      }
    }

    return loadedFaces
      .filter((face): face is Face => face != null)
      .map((face) => ({
        face,
        detectionCount: countsByFaceId.get(face.id) ?? 0,
        trackCount: trackKeysByFaceId.get(face.id)?.size ?? 0,
      }))
      .sort((left, right) => right.detectionCount - left.detectionCount || left.face.id - right.face.id);
  }, [detections, loadedFaces]);

  const facesById = useMemo(() => new Map(videoFaces.map(({ face }) => [face.id, face] as const)), [videoFaces]);
  return { videoFaces, facesById };
}

// A stable function, so useQueries only combines again when a face's data changes.
function loadedFaceData(results: { data?: Face }[]) {
  return results.map((result) => result.data);
}
