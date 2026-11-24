import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Bookmark, Loader2 } from "lucide-react";
import { bookmarks } from "../api/client";
import type { AffinityHostType } from "../api/types";
import { bookmarkStateQueryOptions, useCanBookmark } from "../state/bookmarkStates";

interface Props {
  hostType: AffinityHostType;
  hostId: number;
  className?: string;
  compact?: boolean;
}

export function BookmarkButton({ hostType, hostId, className = "", compact = false }: Props) {
  const [resolvingClick, setResolvingClick] = useState(false);
  const queryClient = useQueryClient();
  const { queryKey, queryFn } = bookmarkStateQueryOptions(hostType, hostId);
  const canBookmark = useCanBookmark();
  // Cards that mount together share one batch request. A cached state is refetched on mount only
  // once invalidated: virtualized lists remount rows while scrolling, and list pages refresh
  // their states with usePrefetchBookmarkStates, which costs one request per page.
  const { data } = useQuery({
    queryKey,
    queryFn,
    enabled: hostId > 0 && canBookmark,
    refetchOnMount: (query) => query.state.isInvalidated,
  });
  const hasLoadedState = data != null;
  const saved = data?.saved ?? false;
  const mutation = useMutation({
    mutationFn: (nextSaved: boolean) => bookmarks.toggle({ hostType, hostId, saved: nextSaved }),
    onMutate: async (nextSaved) => {
      await queryClient.cancelQueries({ queryKey });
      const previous = queryClient.getQueryData(queryKey);
      queryClient.setQueryData(queryKey, { hostType, hostId, saved: nextSaved });
      return { previous };
    },
    onError: (_error, _vars, context) => {
      queryClient.setQueryData(queryKey, context?.previous);
    },
    onSuccess: (state) => {
      queryClient.setQueryData(queryKey, state);
      // Save for Later is a dynamic group, so refresh every view that pages a group's items.
      queryClient.invalidateQueries({ queryKey: ["groups"] });
      queryClient.invalidateQueries({ queryKey: ["group-items"] });
      queryClient.invalidateQueries({ queryKey: ["group-mixed-items"] });
      queryClient.invalidateQueries({ queryKey: ["group-items-page-all"] });
      queryClient.invalidateQueries({ queryKey: ["group-items-popover"] });
      // Feed widgets also cache their host entities under group-feed; those don't change here.
      queryClient.invalidateQueries({
        queryKey: ["group-feed"],
        predicate: (query) => query.queryKey[2] === "group" || query.queryKey[2] === "items",
      });
      queryClient.invalidateQueries({ queryKey: ["front-page-group-items"] });
    },
  });
  const busy = mutation.isPending || resolvingClick;

  return (
    <button
      type="button"
      onClick={async (event) => {
        event.preventDefault();
        event.stopPropagation();
        if (busy || hostId <= 0) return;

        let currentSaved = saved;
        if (!hasLoadedState) {
          setResolvingClick(true);
          try {
            const state = await queryClient.fetchQuery({ queryKey, queryFn });
            currentSaved = state.saved;
          } finally {
            setResolvingClick(false);
          }
        }

        mutation.mutate(!currentSaved);
      }}
      disabled={busy}
      className={`inline-flex items-center justify-center rounded border transition-colors disabled:cursor-wait disabled:opacity-70 ${compact ? "h-7 w-7" : "h-8 w-8"} ${saved ? "border-accent bg-accent/15 text-accent" : "border-border bg-card/80 text-secondary hover:border-accent hover:text-foreground"} ${className}`}
      title={saved ? "Remove from Save for Later" : "Save for Later"}
      aria-label={saved ? "Remove from Save for Later" : "Save for Later"}
      aria-pressed={saved}
    >
      {busy ? (
        <Loader2 className={`${compact ? "h-3.5 w-3.5" : "h-4 w-4"} animate-spin`} />
      ) : (
        <Bookmark className={`${compact ? "h-3.5 w-3.5" : "h-4 w-4"} ${saved ? "fill-current" : ""}`} />
      )}
    </button>
  );
}
