import { useMutation, useQueryClient } from "@tanstack/react-query";
import { faces } from "../../api/client";
import { readReferenceLinkInfo } from "../../components/FaceCompareDialog";
import { hasConflictingMatch } from "../../utils/faceSuggestions";

export type FaceSuggestionChoice = "accept" | "reject";

/**
 * Accepts or rejects a face's suggested performer from a video's page, as the faces list does: accepting records the
 * reference link the match carries, and a match that another reference source disputes, or one that would import a
 * new performer, is reviewed on the face page instead. The face and the video refresh afterwards.
 */
export function useFaceSuggestionDecision(videoId: number, onNavigate: (route: any) => void) {
  const queryClient = useQueryClient();
  const mutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: async ({
      faceId,
      performerId,
      decision,
    }: {
      faceId: number;
      performerId: number;
      decision: FaceSuggestionChoice;
    }): Promise<"done" | "review"> => {
      if (decision === "reject") {
        await faces.recordSuggestionDecision(faceId, { performerId, decision });
        return "done";
      }
      const suggestions = await faces.suggestions(faceId);
      const match = suggestions.find((suggestion) => suggestion.performerId === performerId);
      if (!match || match.performerId <= 0 || hasConflictingMatch(match, suggestions)) return "review";
      await faces.recordSuggestionDecision(faceId, { performerId, decision, ...readReferenceLinkInfo(match) });
      return "done";
    },
    onSuccess: (result, { faceId }) => {
      if (result === "review") {
        onNavigate({ page: "face", id: faceId });
        return;
      }
      queryClient.invalidateQueries({ queryKey: ["face", faceId] });
      queryClient.invalidateQueries({ queryKey: ["faces"] });
      queryClient.invalidateQueries({ queryKey: ["video", videoId] });
    },
  });

  return {
    decide: (faceId: number, performerId: number, decision: FaceSuggestionChoice) =>
      mutation.mutate({ faceId, performerId, decision }),
    decidingFaceId: mutation.isPending ? mutation.variables?.faceId : undefined,
    decisionError: mutation.error ? { faceId: mutation.variables?.faceId, error: mutation.error } : undefined,
  };
}
