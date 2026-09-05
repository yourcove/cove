import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Bookmark, Loader2 } from "lucide-react";
import { bookmarks } from "../api/client";
import type { AffinityHostType } from "../api/types";

interface Props {
  hostType: AffinityHostType;
  hostId: number;
  className?: string;
  compact?: boolean;
  deferUntilHover?: boolean;
  initialSaved?: boolean;
}

export function BookmarkButton({
  hostType,
  hostId,
  className = "",
  compact = false,
  deferUntilHover = false,
  initialSaved,
}: Props) {
  const [activated, setActivated] = useState(!deferUntilHover);
  const [resolvingClick, setResolvingClick] = useState(false);
  let queryClient;
  try {
    queryClient = useQueryClient();
  } catch {
    return (
      <button
        type="button"
        className={`inline-flex items-center justify-center rounded border border-border bg-card/80 text-secondary ${compact ? "h-7 w-7" : "h-8 w-8"} ${className}`}
        title="Save for Later"
        aria-label="Save for Later"
        disabled
      >
        <Bookmark className={compact ? "h-3.5 w-3.5" : "h-4 w-4"} />
      </button>
    );
  }
  const queryKey = ["bookmark-state", hostType, hostId];
  const fetchBookmarkState = () => bookmarks.batch({ hostType, hostIds: [hostId] });
  const { data } = useQuery({
    queryKey,
    queryFn: fetchBookmarkState,
    enabled: hostId > 0 && activated,
    initialData: initialSaved === undefined ? undefined : [{ hostType, hostId, saved: initialSaved }],
  });
  const hasLoadedState = data?.[0] != null;
  const saved = data?.[0]?.saved ?? false;
  const mutation = useMutation({
    mutationFn: (nextSaved: boolean) => bookmarks.toggle({ hostType, hostId, saved: nextSaved }),
    onMutate: async (nextSaved) => {
      await queryClient.cancelQueries({ queryKey });
      const previous = queryClient.getQueryData(queryKey);
      queryClient.setQueryData(queryKey, [{ hostType, hostId, saved: nextSaved }]);
      return { previous };
    },
    onError: (_error, _vars, context) => {
      queryClient.setQueryData(queryKey, context?.previous);
    },
    onSuccess: (state) => {
      queryClient.setQueryData(queryKey, [state]);
      queryClient.invalidateQueries({ queryKey: ["bookmarks"] });
      queryClient.invalidateQueries({ queryKey: ["groups"] });
      queryClient.invalidateQueries({ queryKey: ["group-items"] });
      queryClient.invalidateQueries({ queryKey: ["group-items-page"] });
      queryClient.invalidateQueries({ queryKey: ["front-page-continue-watching"] });
    },
  });
  const busy = mutation.isPending || resolvingClick;

  return (
    <button
      type="button"
      onMouseEnter={() => setActivated(true)}
      onFocus={() => setActivated(true)}
      onClick={async (event) => {
        event.preventDefault();
        event.stopPropagation();
        setActivated(true);
        if (busy || hostId <= 0) return;

        let currentSaved = saved;
        if (!hasLoadedState) {
          setResolvingClick(true);
          try {
            const state = await queryClient.fetchQuery({ queryKey, queryFn: fetchBookmarkState });
            currentSaved = state?.[0]?.saved ?? false;
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
