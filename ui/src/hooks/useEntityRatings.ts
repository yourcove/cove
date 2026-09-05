import { useQuery } from "@tanstack/react-query";
import { entityEngagement } from "../api/client";
import type { AffinityHostType } from "../api/types";

interface Options {
  enabled?: boolean;
}

export function useEntityRatings(hostType: AffinityHostType, hostId: number, options?: Options) {
  const { data, isLoading } = useQuery({
    queryKey: ["engagement", hostType, hostId, "ratings"],
    queryFn: () => entityEngagement.getRatings(hostType, hostId),
    enabled: options?.enabled ?? true,
  });

  return {
    ratings: data?.ratings ?? {},
    isLoading,
  };
}
