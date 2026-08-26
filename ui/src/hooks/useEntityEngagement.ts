import { useCallback } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { entityEngagement } from "../api/client";
import type { AffinityHostType, EntityEngagement, EntityRatings } from "../api/types";

interface Options {
  enabled?: boolean;
  fallbackFavorite?: boolean;
  fallbackRating?: number;
  /** Keeps account-specific engagement out of another principal's cache namespace. */
  queryScope?: string;
}

export function useEntityEngagement(hostType: AffinityHostType, hostId: number, options?: Options) {
  const queryClient = useQueryClient();
  const queryKey = options?.queryScope
    ? ["engagement", options.queryScope, hostType, hostId] as const
    : ["engagement", hostType, hostId] as const;
  const batchQueryKey = ["engagement", hostType, "batch"] as const;
  const ratingsQueryKey = options?.queryScope
    ? ["engagement", options.queryScope, hostType, hostId, "ratings"] as const
    : ["engagement", hostType, hostId, "ratings"] as const;

  const { data } = useQuery({
    queryKey,
    queryFn: () => entityEngagement.get(hostType, hostId),
    enabled: options?.enabled ?? true,
  });

  const setFavoriteMutation = useMutation({
    mutationFn: (isFavorite: boolean) => entityEngagement.setFavorite(hostType, hostId, { isFavorite }),
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKey, (prev?: EntityEngagement) => mergePreservingResume(prev, updated));
      if (!options?.queryScope) {
        queryClient.setQueriesData({ queryKey: batchQueryKey }, (current) => syncBatchEngagement(current, updated));
      }
    },
  });

  const setRatingMutation = useMutation({
    mutationFn: (payload: { value: number | null; aspect?: string }) => entityEngagement.setRating(hostType, hostId, payload),
    onSuccess: (updated, variables) => {
      queryClient.setQueryData(queryKey, (prev?: EntityEngagement) => mergePreservingResume(prev, updated));
      if (!options?.queryScope) {
        queryClient.setQueriesData({ queryKey: batchQueryKey }, (current) => syncBatchEngagement(current, updated));
      }
      queryClient.setQueryData(ratingsQueryKey, (current: EntityRatings | undefined) => syncRatings(current, hostId, variables.aspect ?? "overall", variables.value));
    },
  });

  const setFavorite = useCallback((isFavorite: boolean) => {
    setFavoriteMutation.mutate(isFavorite);
  }, [setFavoriteMutation]);

  const setRating = useCallback((value: number | undefined, aspect = "overall") => {
    setRatingMutation.mutate({ value: value ?? null, aspect });
  }, [setRatingMutation]);

  return {
    engagement: data,
    favorite: data?.isFavorite ?? options?.fallbackFavorite ?? false,
    rating: data?.rating ?? options?.fallbackRating,
    setFavorite,
    setRating,
    favoritePending: setFavoriteMutation.isPending,
    ratingPending: setRatingMutation.isPending,
  };
}

function mergePreservingResume(prev: EntityEngagement | undefined, updated: EntityEngagement): EntityEngagement {
  // A rating/favorite change must not move the player's resume target. The server snapshot carries a
  // resumeTime (= last-persisted LastPositionSec) that lags your live playback position; writing it into the
  // engagement cache makes the detail page's player seek mid-watch. Keep the previously-known resumeTime —
  // the live player owns the playback position, not a rating action.
  return prev ? { ...updated, resumeTime: prev.resumeTime } : updated;
}

function syncBatchEngagement(current: unknown, updated: EntityEngagement) {
  if (!Array.isArray(current)) {
    return current;
  }

  const existingIndex = current.findIndex((entry) => isEntityEngagement(entry) && entry.hostId === updated.hostId);
  if (existingIndex < 0) {
    return current;
  }

  const next = [...current];
  next[existingIndex] = updated;
  return next;
}

function isEntityEngagement(value: unknown): value is EntityEngagement {
  return !!value && typeof value === "object" && typeof (value as EntityEngagement).hostId === "number";
}

function syncRatings(current: EntityRatings | undefined, hostId: number, aspect: string, value: number | null) {
  const nextRatings = { ...(current?.ratings ?? {}) };
  if (value == null) {
    delete nextRatings[aspect];
  } else {
    nextRatings[aspect] = value;
  }

  return {
    hostId,
    ratings: nextRatings,
  };
}
