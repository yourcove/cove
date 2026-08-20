import { useQuery } from "@tanstack/react-query";
import { faces } from "../api/client";

/**
 * What the installed face providers support. Occurrence editing (separating a face's appearances,
 * marking it not present) is fulfilled by whichever extension registers an IFaceOccurrenceEditor, so
 * the UI asks the host rather than assuming any particular extension is present.
 *
 * Cached for the session: installing an extension requires a restart, so this cannot change under us.
 */
export function useFaceCapabilities(enabled = true) {
  const { data } = useQuery({
    queryKey: ["faces", "capabilities"],
    queryFn: () => faces.capabilities(),
    enabled,
    staleTime: Infinity,
    retry: false,
  });

  return {
    canEditOccurrences: data?.canEditOccurrences ?? false,
    canSuggest: data?.canSuggest ?? false,
  };
}
