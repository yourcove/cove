import { useEffect, useMemo, useRef, useState } from "react";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Check, Copy, Loader2, Search, Trash2 } from "lucide-react";
import { videos } from "../api/client";
import type { DeleteEntityOptions, DuplicateSearchGroup, DuplicateSearchRequest, Video } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity } from "../auth/visibility";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { createRouteLinkProps } from "../components/cardNavigation";
import { formatDuration, formatFileSize, getResolutionLabel } from "../components/shared";

interface Props {
  onNavigate: (route: any) => void;
}

type DuplicateMatchType = DuplicateSearchRequest["matchType"];

const GROUP_PAGE_SIZE = 10;

const MATCH_OPTIONS: Array<{ value: DuplicateMatchType; label: string; description: string }> = [
  { value: "fingerprint", label: "Exact file fingerprint", description: "Groups videos that share an MD5 or OSHash." },
  { value: "phash", label: "Similar visual pHash", description: "Finds visually similar videos within a pHash distance and duration window." },
  { value: "title", label: "Same title", description: "Groups videos with the same normalized title." },
  { value: "remoteId", label: "Same remote ID", description: "Groups videos that share a scraper or metadata-server ID." },
];

function getSearchIdFromUrl() {
  return new URLSearchParams(window.location.search).get("search");
}

function replaceSearchIdInUrl(searchId: string | null) {
  const url = new URL(window.location.href);
  if (searchId) url.searchParams.set("search", searchId);
  else url.searchParams.delete("search");
  window.history.replaceState(window.history.state, "", `${url.pathname}${url.search}${url.hash}`);
}

export function DuplicateFinderPage({ onNavigate }: Props) {
  const [matchType, setMatchType] = useState<DuplicateMatchType>("fingerprint");
  const [phashDistance, setPhashDistance] = useState(8);
  const [durationDiff, setDurationDiff] = useState(10);
  const [searchId, setSearchId] = useState<string | null>(getSearchIdFromUrl);
  const [keeperChoices, setKeeperChoices] = useState<Map<number, Set<number>>>(new Map());
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const hydratedSearchIdRef = useRef<string | null>(null);
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canRunDuplicateSearch = hasPermission("jobs.run");
  const canDeleteVideos = canDeleteEntity("video", hasPermission);
  const canDeleteVideoFiles = hasPermission("videos.delete.file");

  const searchQuery = useQuery({
    queryKey: ["duplicate-search", searchId],
    queryFn: () => videos.getDuplicateSearch(searchId!),
    enabled: searchId != null,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return status === "pending" || status === "running" ? 1_000 : false;
    },
  });

  useEffect(() => {
    const persistedSearch = searchQuery.data;
    if (!persistedSearch || persistedSearch.id !== searchId || hydratedSearchIdRef.current === searchId) return;

    const persistedMatchType = MATCH_OPTIONS.find((option) => option.value === persistedSearch.matchType)?.value;
    if (persistedMatchType) setMatchType(persistedMatchType);
    if (persistedMatchType === "phash") {
      setPhashDistance(Math.max(0, Math.min(16, persistedSearch.distance)));
      setDurationDiff(Math.max(0, persistedSearch.durationDiff));
    }
    hydratedSearchIdRef.current = searchId;
  }, [searchId, searchQuery.data]);

  const completed = searchQuery.data?.status === "completed";
  const groupsQuery = useInfiniteQuery({
    queryKey: ["duplicate-search-groups", searchId],
    queryFn: ({ pageParam }) => videos.getDuplicateSearchGroups(searchId!, pageParam, GROUP_PAGE_SIZE),
    initialPageParam: 1,
    getNextPageParam: (lastPage) => lastPage.hasMore ? lastPage.page + 1 : undefined,
    enabled: searchId != null && completed,
  });

  const groups = useMemo(
    () => groupsQuery.data?.pages.flatMap((page) => page.items) ?? [],
    [groupsQuery.data],
  );

  useEffect(() => {
    setKeeperChoices(new Map());
  }, [searchId]);

  useEffect(() => {
    if (groups.length === 0) return;
    setKeeperChoices((current) => {
      const next = new Map(current);
      let changed = false;
      for (const group of groups) {
        if (next.has(group.id)) continue;
        next.set(group.id, new Set(group.keepVideoIds));
        changed = true;
      }
      return changed ? next : current;
    });
  }, [groups]);

  const startMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => videos.startDuplicateSearch({
      matchType,
      distance: matchType === "phash" ? phashDistance : 0,
      durationDiff: matchType === "phash" ? durationDiff : null,
    }),
    onSuccess: (result) => {
      replaceSearchIdInUrl(result.searchId);
      setSearchId(result.searchId);
      queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
      queryClient.invalidateQueries({ queryKey: ["jobs-history"] });
    },
  });

  const decisionMutation = useMutation({
    mutationFn: ({ groupId, keepVideoIds }: { groupId: number; keepVideoIds: number[] }) =>
      videos.updateDuplicateSearchDecision(searchId!, groupId, keepVideoIds),
    onMutate: ({ groupId, keepVideoIds }) => {
      const previous = keeperChoices.get(groupId);
      setKeeperChoices((current) => {
        const next = new Map(current);
        next.set(groupId, new Set(keepVideoIds));
        return next;
      });
      return { groupId, previous };
    },
    onError: (_error, _variables, context) => {
      if (!context) return;
      setKeeperChoices((current) => {
        const next = new Map(current);
        if (context.previous) next.set(context.groupId, context.previous);
        else next.delete(context.groupId);
        return next;
      });
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ["duplicate-search", searchId] });
      queryClient.invalidateQueries({ queryKey: ["duplicate-search-groups", searchId] });
    },
  });

  const deleteMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (options?: DeleteEntityOptions) => videos.deleteUnkeptDuplicates(searchId!, options),
    onSuccess: () => {
      setShowDeleteConfirm(false);
      queryClient.invalidateQueries({ queryKey: ["duplicate-search", searchId] });
      queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
      queryClient.invalidateQueries({ queryKey: ["jobs-history"] });
    },
  });

  const toggleKeeper = (group: DuplicateSearchGroup, videoId: number) => {
    const current = keeperChoices.get(group.id) ?? new Set(group.keepVideoIds);
    if (current.has(videoId) && current.size === 1) return;
    const next = new Set(current);
    if (next.has(videoId)) next.delete(videoId);
    else next.add(videoId);
    decisionMutation.mutate({ groupId: group.id, keepVideoIds: [...next] });
  };

  const search = searchQuery.data;
  const resultError = searchQuery.error ?? groupsQuery.error;
  const isRunning = search?.status === "pending" || search?.status === "running";
  const terminalFailure = search?.status === "failed" || search?.status === "cancelled" || search?.status === "interrupted";

  return (
    <>
      <div>
        <div className="mb-6 flex items-center gap-3">
          <Copy className="h-6 w-6 text-accent" />
          <h1 className="text-xl font-semibold text-foreground">Duplicate Finder</h1>
        </div>

        <div className="mb-6 rounded-lg border border-border bg-card p-4">
          <div className="grid gap-4 lg:grid-cols-[minmax(16rem,1fr)_minmax(10rem,0.45fr)_minmax(10rem,0.45fr)_auto] lg:items-end">
            <div>
              <label className="mb-1 block text-xs font-medium text-secondary">Match type</label>
              <select
                value={matchType}
                onChange={(event) => setMatchType(event.target.value as DuplicateMatchType)}
                className="w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
              >
                {MATCH_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
              </select>
              <p className="mt-1 text-xs text-muted">{MATCH_OPTIONS.find((option) => option.value === matchType)?.description}</p>
            </div>
            <label className={`block text-xs font-medium text-secondary ${matchType === "phash" ? "" : "opacity-50"}`}>
              pHash distance
              <input
                type="number"
                min={0}
                max={16}
                value={phashDistance}
                disabled={matchType !== "phash"}
                onChange={(event) => setPhashDistance(Math.max(0, Math.min(16, Number(event.target.value) || 0)))}
                className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground disabled:cursor-not-allowed disabled:opacity-50 focus:border-accent focus:outline-none"
              />
            </label>
            <label className={`block text-xs font-medium text-secondary ${matchType === "phash" ? "" : "opacity-50"}`}>
              Max duration delta
              <input
                type="number"
                min={0}
                value={durationDiff}
                disabled={matchType !== "phash"}
                onChange={(event) => setDurationDiff(Math.max(0, Number(event.target.value) || 0))}
                className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground disabled:cursor-not-allowed disabled:opacity-50 focus:border-accent focus:outline-none"
              />
            </label>
            <button
              type="button"
              onClick={() => startMutation.mutate()}
              disabled={startMutation.isPending || !canRunDuplicateSearch}
              title={canRunDuplicateSearch ? undefined : "You do not have permission to run jobs"}
              className="flex items-center justify-center gap-2 rounded bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-50"
            >
              {startMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              {startMutation.isPending ? "Queueing…" : searchId ? "Start New Search" : "Find Duplicates"}
            </button>
          </div>
        </div>

        {(startMutation.error || resultError) && <ErrorBanner error={startMutation.error ?? resultError} />}

        {searchQuery.isLoading && searchId && (
          <div className="flex items-center gap-2 rounded-lg border border-border bg-card p-4 text-sm text-secondary">
            <Loader2 className="h-4 w-4 animate-spin text-accent" />
            Loading duplicate search…
          </div>
        )}

        {search && isRunning && (
          <div className="rounded-lg border border-border bg-card p-5">
            <div className="flex items-center gap-3">
              <Loader2 className="h-5 w-5 animate-spin text-accent" />
              <div>
                <p className="font-medium text-foreground">Searching {search.candidateCount.toLocaleString()} videos</p>
                <p className="text-sm text-muted">This runs as a background job. You can leave this page and open the results from Jobs when it finishes.</p>
              </div>
            </div>
          </div>
        )}

        {search && terminalFailure && (
          <div className="flex items-start gap-2 rounded-lg border border-red-800 bg-red-900/20 p-4 text-sm text-red-300">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
            <div>
              <p className="font-medium capitalize">Search {search.status}</p>
              <p>{search.error || (search.status === "interrupted" ? "The server restarted while this search was running. Start a new search to try again." : "Start a new search to try again.")}</p>
            </div>
          </div>
        )}

        {search && completed && (
          <>
            <div className="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-card px-4 py-3">
              <div className="text-sm text-secondary">
                Found <strong className="text-foreground">{search.groupCount.toLocaleString()}</strong> duplicate group{search.groupCount === 1 ? "" : "s"} containing <strong className="text-foreground">{search.videoCount.toLocaleString()}</strong> videos.
              </div>
              {canDeleteVideos && search.unkeptVideoCount > 0 && (
                <button
                  type="button"
                  onClick={() => setShowDeleteConfirm(true)}
                  disabled={Boolean(search.deletionJobId) || deleteMutation.isPending || decisionMutation.isPending}
                  className="inline-flex items-center gap-2 rounded bg-red-600 px-3 py-2 text-sm font-medium text-white hover:bg-red-500 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <Trash2 className="h-4 w-4" />
                  {search.deletionJobId ? "Deletion queued" : `Remove ${search.unkeptVideoCount.toLocaleString()} unwanted`}
                </button>
              )}
            </div>

            {decisionMutation.isError && <ErrorBanner error={decisionMutation.error} />}

            {search.groupCount === 0 && (
              <div className="py-16 text-center">
                <Check className="mx-auto mb-3 h-12 w-12 text-green-400" />
                <p className="text-secondary">No duplicates found</p>
                <p className="mt-1 text-sm text-muted">Try a visual pHash search if exact file hashes are unavailable for this library.</p>
              </div>
            )}

            {groupsQuery.isLoading && search.groupCount > 0 && (
              <div className="flex items-center justify-center gap-2 py-12 text-sm text-secondary">
                <Loader2 className="h-4 w-4 animate-spin text-accent" />
                Loading duplicate groups…
              </div>
            )}

            {groups.length > 0 && (
              <div className="space-y-4">
                {groups.map((group) => (
                  <DuplicateGroupCard
                    key={group.id}
                    group={group}
                    keepVideoIds={keeperChoices.get(group.id) ?? new Set(group.keepVideoIds)}
                    decisionPending={decisionMutation.isPending || Boolean(search.deletionJobId)}
                    onToggleKeeper={(videoId) => toggleKeeper(group, videoId)}
                    onNavigate={onNavigate}
                  />
                ))}
                {groupsQuery.hasNextPage && (
                  <div className="flex justify-center py-2">
                    <button
                      type="button"
                      onClick={() => groupsQuery.fetchNextPage()}
                      disabled={groupsQuery.isFetchingNextPage}
                      className="inline-flex items-center gap-2 rounded-lg border border-border px-4 py-2 text-sm text-foreground hover:border-accent disabled:opacity-50"
                    >
                      {groupsQuery.isFetchingNextPage && <Loader2 className="h-4 w-4 animate-spin" />}
                      {groupsQuery.isFetchingNextPage ? "Loading…" : `Load more (${groups.length.toLocaleString()} of ${search.groupCount.toLocaleString()})`}
                    </button>
                  </div>
                )}
              </div>
            )}
          </>
        )}
      </div>

      <ConfirmDialog
        open={showDeleteConfirm}
        title="Delete unwanted duplicate videos"
        message={search
          ? `Queue deletion of ${search.unkeptVideoCount.toLocaleString()} video record(s). They reference ${search.unkeptFileCount.toLocaleString()} file record(s) totaling ${formatFileSize(search.unkeptBytes)}. Your keeper choices are saved with this search.`
          : ""}
        confirmLabel="Queue deletion"
        onConfirm={(options) => deleteMutation.mutate(options)}
        onCancel={() => setShowDeleteConfirm(false)}
        isPending={deleteMutation.isPending}
        errorMessage={deleteMutation.error instanceof Error ? deleteMutation.error.message : null}
        showDeleteFile={canDeleteVideoFiles}
        showDeleteGenerated
      />
    </>
  );
}

function ErrorBanner({ error }: { error: unknown }) {
  return (
    <div className="mb-4 flex items-center gap-2 rounded border border-red-800 bg-red-900/20 p-3 text-sm text-red-300">
      <AlertTriangle className="h-4 w-4 shrink-0" />
      {error instanceof Error ? error.message : "The request failed."}
    </div>
  );
}

function DuplicateGroupCard({
  group,
  keepVideoIds,
  decisionPending,
  onToggleKeeper,
  onNavigate,
}: {
  group: DuplicateSearchGroup;
  keepVideoIds: Set<number>;
  decisionPending: boolean;
  onToggleKeeper: (videoId: number) => void;
  onNavigate: Props["onNavigate"];
}) {
  return (
    <div className="overflow-hidden rounded-lg border border-border">
      <div className="flex items-center justify-between gap-3 border-b border-border bg-card px-4 py-2">
        <span className="text-sm font-medium text-foreground">Group {group.position + 1} — {group.videos.length} videos</span>
        <span className="text-xs text-muted">{keepVideoIds.size} selected to keep</span>
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
        {group.videos.map((video) => (
          <DuplicateVideoCard
            key={video.id}
            video={video}
            keep={keepVideoIds.has(video.id)}
            disableToggle={decisionPending || (keepVideoIds.has(video.id) && keepVideoIds.size === 1)}
            onToggle={() => onToggleKeeper(video.id)}
            onNavigate={onNavigate}
          />
        ))}
      </div>
    </div>
  );
}

function DuplicateVideoCard({
  video,
  keep,
  disableToggle,
  onToggle,
  onNavigate,
}: {
  video: Video;
  keep: boolean;
  disableToggle: boolean;
  onToggle: () => void;
  onNavigate: Props["onNavigate"];
}) {
  const file = video.files[0];
  const route = { page: "video", id: video.id };
  const linkProps = createRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate(route));
  return (
    <div className={`relative border-b border-r border-border ${keep ? "bg-green-900/20 ring-2 ring-inset ring-green-500/50" : "bg-background"}`}>
      <button
        type="button"
        onClick={onToggle}
        disabled={disableToggle}
        title={disableToggle && keep ? "Every group must keep at least one video" : keep ? "Do not keep this video" : "Keep this video"}
        className="absolute left-2 top-2 z-10 disabled:cursor-not-allowed"
      >
        <span className={`flex h-5 w-5 items-center justify-center rounded border-2 ${keep ? "border-green-500 bg-green-500" : "border-muted bg-black/40"}`}>
          {keep && <Check className="h-3 w-3 text-white" />}
        </span>
      </button>
      <a {...linkProps} className="block aspect-video w-full cursor-pointer overflow-hidden bg-card">
        <img
          src={videos.screenshotUrl(video.id)}
          alt={video.imagePath ? video.title || "" : ""}
          className="h-full w-full object-cover"
          loading="lazy"
          onError={(event) => { event.currentTarget.style.display = "none"; }}
        />
      </a>
      <div className="space-y-1 p-2">
        <a {...linkProps} className="block max-w-full truncate text-xs font-medium text-foreground hover:text-accent">
          {video.title || file?.basename || `Video #${video.id}`}
        </a>
        {file && (
          <div className="flex flex-wrap gap-x-3 gap-y-0.5 text-[10px] text-muted">
            <span>{file.width}×{file.height}</span>
            <span>{getResolutionLabel(file.width, file.height)}</span>
            <span>{formatDuration(file.duration)}</span>
            <span>{formatFileSize(file.size)}</span>
          </div>
        )}
        {file?.path && <p className="truncate text-[9px] text-muted" title={file.path}>{file.path}</p>}
      </div>
    </div>
  );
}
