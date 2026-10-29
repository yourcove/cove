import { useMemo } from "react";
import { useQueries } from "@tanstack/react-query";
import { faces, performers, tags } from "../../api/client";
import { faceDisplayName } from "../../utils/faceDisplay";
import type { DerivedOperandNameMaps } from "./segmentDisplayUtils";

interface NamedOperand {
  tagIds?: number[];
  performerIds?: number[];
  faceIds?: number[];
}

// Looks up the tag, performer and face names a derived query's operands refer to.
export function useDerivedOperandNameMaps(operands: NamedOperand[]): DerivedOperandNameMaps {
  const { tagIds, performerIds, faceIds } = useMemo(() => {
    const tagSet = new Set<number>();
    const performerSet = new Set<number>();
    const faceSet = new Set<number>();
    for (const operand of operands) {
      operand.tagIds?.forEach((id) => tagSet.add(id));
      operand.performerIds?.forEach((id) => performerSet.add(id));
      operand.faceIds?.forEach((id) => faceSet.add(id));
    }
    return {
      tagIds: Array.from(tagSet),
      performerIds: Array.from(performerSet),
      faceIds: Array.from(faceSet),
    };
  }, [operands]);

  const tagQueries = useQueries({
    queries: tagIds.map((id) => ({ queryKey: ["tag", id], queryFn: () => tags.get(id), staleTime: 60_000 })),
  });
  const performerQueries = useQueries({
    queries: performerIds.map((id) => ({
      queryKey: ["performer", id],
      queryFn: () => performers.get(id),
      staleTime: 60_000,
    })),
  });
  const faceQueries = useQueries({
    queries: faceIds.map((id) => ({ queryKey: ["face", id], queryFn: () => faces.get(id), staleTime: 60_000 })),
  });

  // The query result arrays change identity on every render, so the maps are rebuilt only when a name does.
  const namesKey = JSON.stringify([
    tagQueries.map((query) => query.data?.name),
    performerQueries.map((query) => query.data?.name),
    faceQueries.map((query) => (query.data ? faceDisplayName(query.data) : undefined)),
  ]);
  return useMemo<DerivedOperandNameMaps>(() => {
    const tagNamesById = new Map<number, string>();
    tagIds.forEach((id, index) => {
      const tag = tagQueries[index]?.data;
      if (tag) tagNamesById.set(id, tag.name);
    });
    const performerNamesById = new Map<number, string>();
    performerIds.forEach((id, index) => {
      const performer = performerQueries[index]?.data;
      if (performer) performerNamesById.set(id, performer.name);
    });
    const faceLabelsById = new Map<number, string>();
    faceIds.forEach((id, index) => {
      const face = faceQueries[index]?.data;
      if (face) faceLabelsById.set(id, faceDisplayName(face));
    });
    return { tagNamesById, performerNamesById, faceLabelsById };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- namesKey stands in for the query results.
  }, [faceIds, namesKey, performerIds, tagIds]);
}
