import { useQuery } from "@tanstack/react-query";
import { entityNameConflicts, tagNameConflicts } from "../../api/client";
import type { CombinedNameConflictSummary, NameConflictEntityType } from "../../api/types";

export const TAG_NAME_CONFLICTS_PERMISSION = "tags.name-conflicts.manage";
export const ENTITY_NAME_CONFLICTS_PERMISSION = "entities.name-conflicts.manage";
export const tagNameConflictQueryKey = ["tag-name-conflicts"] as const;
export const tagNameConflictSummaryQueryKey = ["tag-name-conflicts", "summary"] as const;
export const entityNameConflictQueryKey = (entityType: NameConflictEntityType) => ["entity-name-conflicts", entityType] as const;

export function useTagNameConflictScan(enabled = true) {
  return useQuery({
    queryKey: tagNameConflictQueryKey,
    queryFn: tagNameConflicts.scan,
    enabled,
    staleTime: 30_000,
    refetchInterval: (query) => query.state.data?.unresolvedGroupCount ? 60_000 : false,
  });
}

export function useTagNameConflictSummary(enabled = true, includeEntityConflicts = true) {
  return useQuery({
    queryKey: [...tagNameConflictSummaryQueryKey, includeEntityConflicts ? "all" : "tags"],
    queryFn: async (): Promise<CombinedNameConflictSummary> => {
      const tags = await tagNameConflicts.summary();
      if (!includeEntityConflicts) {
        return {
          unresolvedGroupCount: tags.unresolvedGroupCount,
          tagUnresolvedGroupCount: tags.unresolvedGroupCount,
          performerUnresolvedGroupCount: 0,
          studioUnresolvedGroupCount: 0,
          scannedAtUtc: tags.scannedAtUtc,
        };
      }

      const entities = await entityNameConflicts.summary();
      return {
        unresolvedGroupCount: tags.unresolvedGroupCount + entities.unresolvedGroupCount,
        tagUnresolvedGroupCount: tags.unresolvedGroupCount,
        performerUnresolvedGroupCount: entities.performerUnresolvedGroupCount,
        studioUnresolvedGroupCount: entities.studioUnresolvedGroupCount,
        scannedAtUtc: tags.scannedAtUtc > entities.scannedAtUtc ? tags.scannedAtUtc : entities.scannedAtUtc,
      };
    },
    enabled,
    staleTime: 30_000,
    refetchInterval: (query) => query.state.data?.unresolvedGroupCount ? 60_000 : false,
  });
}

export function useEntityNameConflictScan(entityType: NameConflictEntityType, enabled = true) {
  return useQuery({
    queryKey: entityNameConflictQueryKey(entityType),
    queryFn: () => entityNameConflicts.scan(entityType),
    enabled,
    staleTime: 30_000,
    refetchInterval: (query) => query.state.data?.unresolvedGroupCount ? 60_000 : false,
  });
}
