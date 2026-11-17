import { useEffect, useMemo } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { faces } from "../../api/client";
import type { Face, FaceSuggestion, SegmentRecord } from "../../api/types";
import { readReferenceLinkInfo } from "../../components/FaceCompareDialog";
import type { FaceOverlayInfo } from "../../components/VideoPlayer";
import { faceShownName, faceStoredName } from "../../utils/faceDisplay";
import { formatShownTitle, rawSegmentFaceId, rawSegmentPlaceholderFaceId } from "./segmentDisplayUtils";

type SuggestionDecision = "accept" | "reject";

/**
 * The face a raw face segment names, how to name the segment after it, and its suggested performers with a way to
 * decide on them. Used by the raw segment page and, through one of its raw segments, by the segment (span) page.
 */
export function useSegmentFace(segment: SegmentRecord | undefined, canReadFaces: boolean) {
  const queryClient = useQueryClient();
  const faceId = segment ? rawSegmentFaceId(segment) : undefined;

  const { data: loadedFace, isError: faceQueryFailed } = useQuery({
    queryKey: ["face", faceId],
    queryFn: () => faces.get(faceId!),
    enabled: faceId != null && canReadFaces,
    // Moving between segments of the same face keeps it shown; another face replaces it once it loads.
    placeholderData: keepPreviousData,
  });
  // A face kept from the previous segment is only used while it is still the face this segment names.
  const face = loadedFace?.id === faceId ? loadedFace : undefined;
  // Suggestions are only offered for a face nobody has linked to a performer, ignored or merged away.
  const faceNeedsMatch = !!face && face.performerId == null && !face.ignored && face.mergedIntoFaceId == null;
  const { data: suggestionData } = useQuery({
    queryKey: ["face", faceId, "suggestions"],
    queryFn: () => faces.suggestions(faceId!),
    enabled: faceId != null && canReadFaces && faceNeedsMatch,
  });
  const suggestions = useMemo(() => (faceNeedsMatch ? (suggestionData ?? []) : []), [faceNeedsMatch, suggestionData]);

  const decision = useMutation({
    // The suggestion card shows the error itself.
    meta: { suppressGlobalError: true },
    mutationFn: ({
      faceId: decidedFaceId,
      suggestion,
      decision: choice,
    }: {
      faceId: number;
      segmentId: number;
      suggestion: FaceSuggestion;
      decision: SuggestionDecision;
    }) =>
      faces.recordSuggestionDecision(decidedFaceId, {
        performerId: suggestion.performerId,
        decision: choice,
        // Accepting a reference-database match records which remote performer it is, as the faces list does.
        ...(choice === "accept" ? readReferenceLinkInfo(suggestion) : {}),
      }),
    onSuccess: (_face, { faceId: decidedFaceId, segmentId }) => {
      queryClient.invalidateQueries({ queryKey: ["face", decidedFaceId] });
      queryClient.invalidateQueries({ queryKey: ["faces"] });
      queryClient.invalidateQueries({ queryKey: ["segment", segmentId] });
    },
  });
  // A decision's outcome belongs to the segment it was made on, not the next one opened in the same page.
  const resetDecision = decision.reset;
  const segmentId = segment?.id;
  useEffect(() => {
    resetDecision();
  }, [segmentId, resetDecision]);

  // A segment still titled with its face's placeholder key is named after the face: its own name, else its top
  // suggested performer flagged as a suggestion. An ignored or merged face keeps only a name it was given.
  const shownName =
    segment && face && rawSegmentPlaceholderFaceId(segment) != null
      ? faceNeedsMatch
        ? faceShownName(face)
        : storedNameAsShown(face)
      : undefined;
  const overlayName = shownName ? formatShownTitle(shownName.name, shownName.suggested) : undefined;
  const playerFaces = useMemo<FaceOverlayInfo[]>(
    () => (face ? [{ ...face, displayName: overlayName }] : []),
    [face, overlayName],
  );

  const decide = (suggestion: FaceSuggestion, choice: SuggestionDecision) => {
    if (faceId == null || segment == null) return;
    decision.mutate({ faceId, segmentId: segment.id, suggestion, decision: choice });
  };

  return {
    faceId,
    face,
    // Loading until this segment's own face arrives, not while the previous one is still shown.
    faceLoading: faceId != null && canReadFaces && !face && !faceQueryFailed,
    suggestions,
    shownName,
    playerFaces,
    decide,
    decisionPending: decision.isPending,
    decisionError: decision.error as Error | null,
  };
}

function storedNameAsShown(face: Face) {
  const name = faceStoredName(face);
  return name ? { name, suggested: false } : undefined;
}
