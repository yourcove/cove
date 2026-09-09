import { useMemo } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { entityEngagement } from "../api/client";
import type { AffinityHostType, EntityEngagement } from "../api/types";

export function useEntityEngagementBatch(hostType: AffinityHostType, hostIds: number[]) {
  const queryClient = useQueryClient();
  const idsKey = hostIds.join(",");
  const normalizedHostIds = useMemo(() => [...new Set(hostIds)].sort((left, right) => left - right), [idsKey]);

  const { data, isLoading } = useQuery({
    queryKey: ["engagement", hostType, "batch", normalizedHostIds],
    queryFn: async () => {
      const results = await entityEngagement.batch({ hostType, hostIds: normalizedHostIds });
      for (const engagement of results) {
        queryClient.setQueryData(["engagement", hostType, engagement.hostId], engagement);
      }
      return results;
    },
    enabled: normalizedHostIds.length > 0,
    staleTime: 30000,
  });

  const engagementById = useMemo(() => {
    const map = new Map<number, EntityEngagement>();
    for (const engagement of data ?? []) {
      map.set(engagement.hostId, engagement);
    }
    return map;
  }, [data]);

  return {
    engagementById,
    isLoading,
  };
}
