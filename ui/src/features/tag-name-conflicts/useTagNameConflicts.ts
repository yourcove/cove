import { useQuery } from "@tanstack/react-query";
import { tagNameConflicts } from "../../api/client";

export const TAG_NAME_CONFLICTS_PERMISSION = "tags.name-conflicts.manage";
export const tagNameConflictQueryKey = ["tag-name-conflicts"] as const;
export const tagNameConflictSummaryQueryKey = ["tag-name-conflicts", "summary"] as const;

export function useTagNameConflictScan(enabled = true) {
  return useQuery({
    queryKey: tagNameConflictQueryKey,
    queryFn: tagNameConflicts.scan,
    enabled,
    staleTime: 30_000,
    refetchInterval: (query) => query.state.data?.unresolvedGroupCount ? 60_000 : false,
  });
}

export function useTagNameConflictSummary(enabled = true) {
  return useQuery({
    queryKey: tagNameConflictSummaryQueryKey,
    queryFn: tagNameConflicts.summary,
    enabled,
    staleTime: 30_000,
    refetchInterval: (query) => query.state.data?.unresolvedGroupCount ? 60_000 : false,
  });
}
