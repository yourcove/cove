import type { FaceSuggestion } from "../api/types";

// A suggestion is "conflicting" when it shares a conflict group with another suggestion that points
// at a different performer — i.e. two reference (SAIE) packs disagree about who this face is. Only
// then should accepting open the compare dialog so the user can choose between / merge them. Every
// other accept just links directly.
export function hasConflictingMatch(suggestion: FaceSuggestion, allSuggestions: readonly FaceSuggestion[]) {
  const groupId = suggestion.conflictGroupId;
  if (!groupId) return false;
  return allSuggestions.some(
    (other) =>
      other !== suggestion && other.conflictGroupId === groupId && other.performerId !== suggestion.performerId,
  );
}
