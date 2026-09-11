import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  Clock,
  Copy,
  GitMerge,
  History,
  Keyboard,
  Loader2,
  Plus,
  RotateCcw,
  Search,
  Sparkles,
  Trash2,
  X,
} from "lucide-react";
import { jobs as jobsApi, videos } from "../api/client";
import type {
  DuplicateGroupFilter,
  DuplicateGroupSort,
  DuplicateKeeperRule,
  DuplicateSearchGroup,
  DuplicateSearchInfo,
} from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity } from "../auth/visibility";
import { PaginationControls } from "../components/PaginationControls";
import { QuickViewDialog } from "../components/QuickViewDialog";
import { formatFileSize } from "../components/shared";
import { AnchoredPopover } from "../components/duplicates/AnchoredPopover";
import { DuplicateCompareDialog } from "../components/duplicates/DuplicateCompareDialog";
import { DuplicateDialog } from "../components/duplicates/DuplicateDialog";
import { DuplicateGroupCard } from "../components/duplicates/DuplicateGroupCard";
import { DuplicateResolveDialog, type ResolveSummary } from "../components/duplicates/DuplicateResolveDialog";
import { DuplicateSearchSetup } from "../components/duplicates/DuplicateSearchSetup";
import { KeeperRulesEditor, describeRules } from "../components/duplicates/KeeperRulesEditor";
import {
  GROUP_FILTERS,
  GROUP_SORTS,
  PAGE_SIZES,
  describeSearch,
  readResolutionPreferences,
  readSearchPreferences,
  totalSize,
  writeResolutionPreferences,
  writeSearchPreferences,
  type ResolutionPreferences,
  type SearchPreferences,
} from "../components/duplicates/duplicateModel";
import { useEntityEngagementBatch } from "../hooks/useEntityEngagementBatch";
import { navigateToUrl } from "../router/location";
import {
  useOptionalKeyboardShortcuts,
  useRegisterKeyboardActions,
  type KeyboardActionRegistration,
} from "../keyboard/KeyboardShortcutProvider";
import { COVE_KEYBOARD_ACTIONS } from "../keyboard/catalog";

interface Props {
  onNavigate: (route: any) => void;
}

interface UrlState {
  search: string | null;
  status: DuplicateGroupFilter;
  sort: DuplicateGroupSort;
  page: number;
  q: string;
}

function readUrlState(): UrlState {
  const params = new URLSearchParams(window.location.search);
  const status = params.get("status") as DuplicateGroupFilter | null;
  const sort = params.get("sort") as DuplicateGroupSort | null;
  return {
    search: params.get("search"),
    status: status && GROUP_FILTERS.some((filter) => filter.value === status) ? status : "unresolved",
    sort: sort && GROUP_SORTS.some((option) => option.value === sort) ? sort : "position",
    page: Math.max(1, Number(params.get("page")) || 1),
    q: params.get("q") ?? "",
  };
}

function writeUrlState(state: UrlState) {
  const url = new URL(window.location.href);
  const set = (key: string, value: string | null, fallback?: string) => {
    if (value == null || value === "" || value === fallback) url.searchParams.delete(key);
    else url.searchParams.set(key, value);
  };
  set("search", state.search);
  set("status", state.search ? state.status : null, "unresolved");
  set("sort", state.search ? state.sort : null, "position");
  set("page", state.search ? String(state.page) : null, "1");
  set("q", state.search ? state.q : null);
  window.history.replaceState(window.history.state, "", `${url.pathname}${url.search}${url.hash}`);
}

type Notice = { tone: "info" | "success" | "error"; message: string; undo?: () => void };
type ResolveTarget = { scope: "all" } | { scope: "group"; groupId: number };

export function DuplicateFinderPage({ onNavigate }: Props) {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canRun = hasPermission("jobs.run");
  const canCancelJobs = hasPermission("jobs.cancel");
  const canReadFiles = hasPermission("files.read");
  const canResolve = canDeleteEntity("video", hasPermission);
  const canDeleteFiles = hasPermission("videos.delete.file");
  const canWrite = hasPermission("videos.write");

  const [url, setUrl] = useState<UrlState>(readUrlState);
  const updateUrl = useCallback((patch: Partial<UrlState>) => {
    setUrl((current) => {
      const next = { ...current, ...patch };
      writeUrlState(next);
      return next;
    });
  }, []);
  const searchId = url.search;

  const [preferences, setPreferencesState] = useState<SearchPreferences>(readSearchPreferences);
  const setPreferences = (next: SearchPreferences) => {
    setPreferencesState(next);
    writeSearchPreferences(next);
  };
  const [resolution, setResolutionState] = useState<ResolutionPreferences>(readResolutionPreferences);
  const setResolution = (next: ResolutionPreferences) => {
    setResolutionState(next);
    writeResolutionPreferences(next);
  };

  const [setupOpen, setSetupOpen] = useState(!searchId);
  const [queryDraft, setQueryDraft] = useState(url.q);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [focusedIndex, setFocusedIndex] = useState(0);
  const [compare, setCompare] = useState<{ groupId: number; pair?: [number, number] } | null>(null);
  const [quickViewId, setQuickViewId] = useState<number | null>(null);
  const [resolveTarget, setResolveTarget] = useState<ResolveTarget | null>(null);
  const [autoSelectOpen, setAutoSelectOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const [keeperOverrides, setKeeperOverrides] = useState<Map<number, Set<number>>>(new Map());
  const [statusOverrides, setStatusOverrides] = useState<Map<number, DuplicateSearchGroup["status"]>>(new Map());
  const [pinned, setPinned] = useState<
    Map<number, { group: DuplicateSearchGroup; index: number; intent: "resolve" | "ignore" }>
  >(new Map());
  const groupRefs = useRef(new Map<number, HTMLElement>());

  useEffect(() => {
    const timer = window.setTimeout(() => {
      if (queryDraft !== url.q) updateUrl({ q: queryDraft, page: 1 });
    }, 300);
    return () => window.clearTimeout(timer);
  }, [queryDraft, url.q, updateUrl]);

  useEffect(() => {
    if (!notice || notice.tone === "error") return;
    const timer = window.setTimeout(() => setNotice(null), notice.undo ? 8000 : 5000);
    return () => window.clearTimeout(timer);
  }, [notice]);

  // Local decisions, pinned cards and focus belong to one view of one search.
  useEffect(() => {
    setKeeperOverrides(new Map());
    setStatusOverrides(new Map());
    setPinned(new Map());
    setFocusedIndex(0);
  }, [searchId, url.status, url.sort, url.page, url.q, preferences.pageSize]);

  const searchQuery = useQuery({
    queryKey: ["duplicate-search", searchId],
    queryFn: () => videos.getDuplicateSearch(searchId!),
    enabled: searchId != null,
    refetchInterval: (query) => {
      const data = query.state.data;
      if (!data) return false;
      if (data.status === "pending" || data.status === "running") return 1000;
      return data.counts.queued > 0 || data.resolutionJobId ? 1500 : false;
    },
  });
  const search = searchQuery.data;
  const isRunning = search?.status === "pending" || search?.status === "running";
  const completed = search?.status === "completed";

  const jobQuery = useQuery({
    queryKey: ["job", search?.jobId],
    queryFn: () => jobsApi.get(search!.jobId!),
    enabled: Boolean(isRunning && search?.jobId),
    refetchInterval: 1000,
    retry: false,
  });

  const recentQuery = useQuery({
    queryKey: ["duplicate-searches"],
    queryFn: () => videos.listDuplicateSearches(8),
  });

  const groupQueryKey = [
    "duplicate-search-groups",
    searchId,
    url.status,
    url.sort,
    url.page,
    preferences.pageSize,
    url.q,
  ];
  const groupsQuery = useQuery({
    queryKey: groupQueryKey,
    queryFn: () =>
      videos.getDuplicateSearchGroups(searchId!, {
        page: url.page,
        perPage: preferences.pageSize,
        status: url.status,
        sort: url.sort,
        q: url.q,
      }),
    enabled: searchId != null && completed,
    placeholderData: keepPreviousData,
    refetchInterval: search && search.counts.queued > 0 ? 2000 : false,
  });

  const serverGroups = groupsQuery.data?.items ?? [];
  // Groups handled in this view stay where they were, so the list does not jump while reviewing. Once one
  // leaves the current filter its real state (queued, resolved, failed) is followed by id.
  const pinnedIds = useMemo(
    () => [...pinned.keys()].filter((id) => !serverGroups.some((group) => group.id === id)),
    [pinned, serverGroups],
  );
  const pinnedQuery = useQuery({
    queryKey: ["duplicate-search-groups", searchId, "pinned", pinnedIds],
    queryFn: () => videos.getDuplicateSearchGroups(searchId!, { page: 1, perPage: 50, ids: pinnedIds }),
    enabled: searchId != null && pinnedIds.length > 0,
    placeholderData: keepPreviousData,
    refetchInterval: (query) =>
      query.state.data?.items.some((group) => group.status === "queued" || group.status === "processing")
        ? 1500
        : false,
  });
  const displayedGroups = useMemo(() => {
    const merged = serverGroups.map((group) => {
      const status = statusOverrides.get(group.id);
      return status && (group.status === "unresolved" || group.status === "failed") ? { ...group, status } : group;
    });
    const followed = new Map((pinnedQuery.data?.items ?? []).map((group) => [group.id, group]));
    const missing = [...pinned.values()]
      .filter((entry) => !merged.some((group) => group.id === entry.group.id))
      .sort((left, right) => left.index - right.index);
    for (const entry of missing) {
      const current = followed.get(entry.group.id);
      const status = statusOverrides.get(entry.group.id);
      const shown: DuplicateSearchGroup =
        current && (current.status !== "unresolved" || !status)
          ? current
          : // Until the group's own state arrives, show what the reviewer just asked for.
            { ...entry.group, status: status ?? (entry.intent === "ignore" ? "ignored" : "queued") };
      merged.splice(Math.min(entry.index, merged.length), 0, shown);
    }
    return merged;
  }, [serverGroups, pinned, statusOverrides, pinnedQuery.data]);

  const keepersFor = useCallback(
    (group: DuplicateSearchGroup) => keeperOverrides.get(group.id) ?? new Set(group.keepVideoIds),
    [keeperOverrides],
  );

  const pageVideoIds = useMemo(
    () => displayedGroups.flatMap((group) => group.videos.map((video) => video.id)),
    [displayedGroups],
  );
  const { engagementById } = useEntityEngagementBatch("video", pageVideoIds);

  const invalidateSearch = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ["duplicate-search", searchId] });
    void queryClient.invalidateQueries({ queryKey: ["duplicate-search-groups", searchId] });
    void queryClient.invalidateQueries({ queryKey: ["duplicate-searches"] });
  }, [queryClient, searchId]);

  const openSearch = (id: string | null) => {
    updateUrl({ search: id, status: "unresolved", sort: "position", page: 1, q: "" });
    setQueryDraft("");
    setSetupOpen(id == null);
  };

  const startMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () =>
      videos.startDuplicateSearch({
        matchType: preferences.matchType,
        distance: preferences.matchType === "phash" ? preferences.distance : 0,
        durationDiff: preferences.matchType === "phash" ? preferences.durationDiff : null,
        includePaths: canReadFiles ? preferences.includePaths : [],
        excludePaths: canReadFiles ? preferences.excludePaths : [],
        minimumDuration: preferences.minimumDuration,
        keeperRules: preferences.keeperRules.filter((rule) => canReadFiles || rule.type !== "path"),
      }),
    onSuccess: (result) => {
      openSearch(result.searchId);
      void queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
      void queryClient.invalidateQueries({ queryKey: ["duplicate-searches"] });
    },
  });

  const cancelMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (jobId: string) => jobsApi.cancel(jobId),
    onSettled: invalidateSearch,
  });

  const discardMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (id: string) => videos.deleteDuplicateSearch(id),
    onSuccess: (_result, id) => {
      if (id === searchId) openSearch(null);
      void queryClient.invalidateQueries({ queryKey: ["duplicate-searches"] });
    },
    onError: (error) => setNotice({ tone: "error", message: errorMessage(error) }),
  });

  const decisionMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: ({ groupId, keepVideoIds }: { groupId: number; keepVideoIds: number[]; previous?: Set<number> }) =>
      videos.updateDuplicateSearchDecision(searchId!, groupId, keepVideoIds),
    onMutate: ({ groupId, keepVideoIds }) => {
      setKeeperOverrides((current) => new Map(current).set(groupId, new Set(keepVideoIds)));
    },
    onError: (error, { groupId, previous }) => {
      setKeeperOverrides((current) => {
        const next = new Map(current);
        if (previous) next.set(groupId, previous);
        else next.delete(groupId);
        return next;
      });
      setNotice({ tone: "error", message: errorMessage(error) });
    },
    onSuccess: () => {
      // Refresh the groups too, so the card reports the choice as the reviewer's own.
      void queryClient.invalidateQueries({ queryKey: ["duplicate-search", searchId] });
      void queryClient.invalidateQueries({ queryKey: ["duplicate-search-groups", searchId] });
    },
  });

  const setKeepers = (group: DuplicateSearchGroup, keepVideoIds: number[]) => {
    if (keepVideoIds.length === 0) return;
    decisionMutation.mutate({ groupId: group.id, keepVideoIds, previous: keeperOverrides.get(group.id) });
  };

  const pin = (group: DuplicateSearchGroup, intent: "resolve" | "ignore") => {
    const index = displayedGroups.findIndex((item) => item.id === group.id);
    setPinned((current) => new Map(current).set(group.id, { group, index: Math.max(0, index), intent }));
  };

  const resolveMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: ({ groupIds }: { groupIds: number[] | null }) =>
      videos.resolveDuplicateGroups(searchId!, {
        groupIds,
        action: canWrite ? resolution.action : "remove",
        deleteFiles: canDeleteFiles && resolution.deleteFiles,
        deleteGenerated: resolution.deleteGenerated,
      }),
    onMutate: ({ groupIds }) => {
      if (!groupIds) return;
      setStatusOverrides((current) => {
        const next = new Map(current);
        for (const id of groupIds) next.set(id, "queued");
        return next;
      });
    },
    onSuccess: async (result, { groupIds }) => {
      setResolveTarget(null);
      if (!groupIds) {
        setNotice({
          tone: "success",
          message: `Queued ${result.queuedGroupCount.toLocaleString()} groups. They resolve in the background — keep working.`,
        });
      }
      void queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["duplicate-search", searchId] }),
        queryClient.invalidateQueries({ queryKey: ["duplicate-search-groups", searchId] }),
      ]);
      // The server now reports each group's real state, including a failure, so the optimistic one can go.
      setStatusOverrides((current) => {
        const next = new Map(current);
        for (const id of groupIds ?? []) next.delete(id);
        return next;
      });
      void queryClient.invalidateQueries({ queryKey: ["duplicate-searches"] });
    },
    onError: (error, { groupIds }) => {
      setStatusOverrides((current) => {
        const next = new Map(current);
        for (const id of groupIds ?? []) next.delete(id);
        return next;
      });
      setPinned((current) => {
        const next = new Map(current);
        for (const id of groupIds ?? []) next.delete(id);
        return next;
      });
      if (!resolveTarget) setNotice({ tone: "error", message: errorMessage(error) });
    },
  });

  const ignoreMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: ({ group, restore }: { group: DuplicateSearchGroup; restore: boolean }) =>
      restore ? videos.restoreDuplicateGroup(searchId!, group.id) : videos.ignoreDuplicateGroup(searchId!, group.id),
    onMutate: ({ group, restore }) => {
      if (!restore) {
        pin(group, "ignore");
        setStatusOverrides((current) => new Map(current).set(group.id, "ignored"));
      }
    },
    onSuccess: async (_result, { group, restore }) => {
      if (!restore) {
        setNotice({
          tone: "info",
          message: `Group ${group.position + 1} marked as not duplicates. Future searches will skip these videos as a pair.`,
          undo: () => ignoreMutation.mutate({ group, restore: true }),
        });
        invalidateSearch();
        return;
      }
      setNotice({ tone: "info", message: `Group ${group.position + 1} is back in review.` });
      // Keep the pinned row until the refreshed list includes the group again, so it never blinks out.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["duplicate-search", searchId] }),
        queryClient.invalidateQueries({ queryKey: ["duplicate-search-groups", searchId] }),
      ]);
      setPinned((current) => {
        const next = new Map(current);
        next.delete(group.id);
        return next;
      });
      setStatusOverrides((current) => {
        const next = new Map(current);
        next.delete(group.id);
        return next;
      });
    },
    onError: (error, { group, restore }) => {
      if (!restore) {
        setPinned((current) => {
          const next = new Map(current);
          next.delete(group.id);
          return next;
        });
        setStatusOverrides((current) => {
          const next = new Map(current);
          next.delete(group.id);
          return next;
        });
      }
      setNotice({ tone: "error", message: errorMessage(error) });
    },
  });

  const autoSelectMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: ({ rules, overwriteManual }: { rules: DuplicateKeeperRule[]; overwriteManual: boolean }) =>
      videos.autoSelectDuplicateKeepers(searchId!, rules, { overwriteManual }),
    onSuccess: (result) => {
      setAutoSelectOpen(false);
      setKeeperOverrides(new Map());
      setNotice({
        tone: "success",
        message:
          result.changedGroupCount === 0
            ? `Checked ${result.updatedGroupCount.toLocaleString()} groups — every keeper already matched your rules.`
            : `Changed the keeper in ${result.changedGroupCount.toLocaleString()} of ${result.updatedGroupCount.toLocaleString()} groups.`,
      });
      invalidateSearch();
    },
  });

  const advanceFocusFrom = (groupId: number) => {
    const index = displayedGroups.findIndex((group) => group.id === groupId);
    const next = displayedGroups.findIndex(
      (group, position) => position > index && (group.status === "unresolved" || group.status === "failed"),
    );
    if (next >= 0) {
      setFocusedIndex(next);
      window.setTimeout(
        () => groupRefs.current.get(displayedGroups[next].id)?.scrollIntoView({ behavior: "smooth", block: "start" }),
        50,
      );
    }
  };

  const resolveGroup = (group: DuplicateSearchGroup, confirmed = false) => {
    if (!canResolve) return;
    const keepers = keepersFor(group);
    if (keepers.size === group.videos.length) return;
    if (resolution.confirmEachGroup && !confirmed) {
      setResolveTarget({ scope: "group", groupId: group.id });
      return;
    }
    setResolveTarget(null);
    pin(group, "resolve");
    resolveMutation.mutate({ groupIds: [group.id] });
    advanceFocusFrom(group.id);
  };

  const ignoreGroup = (group: DuplicateSearchGroup) => {
    if (!canWrite) return;
    ignoreMutation.mutate({ group, restore: false });
    advanceFocusFrom(group.id);
  };

  const focusedGroup = displayedGroups[Math.min(focusedIndex, Math.max(0, displayedGroups.length - 1))];
  const reviewing = Boolean(
    completed &&
    displayedGroups.length > 0 &&
    !compare &&
    quickViewId == null &&
    !resolveTarget &&
    !autoSelectOpen &&
    !shortcutsOpen,
  );
  const moveFocus = (offset: number) => {
    if (displayedGroups.length === 0) return;
    const next = Math.max(0, Math.min(displayedGroups.length - 1, focusedIndex + offset));
    setFocusedIndex(next);
    groupRefs.current.get(displayedGroups[next].id)?.scrollIntoView({ behavior: "smooth", block: "start" });
  };
  const keyboardActions = useMemo<KeyboardActionRegistration[]>(() => {
    const reviewable = focusedGroup && (focusedGroup.status === "unresolved" || focusedGroup.status === "failed");
    const keepNumber = (position: number) => () => {
      const video = focusedGroup?.videos[position];
      if (video && reviewable) setKeepers(focusedGroup, [video.id]);
    };
    return [
      { id: "duplicates.group.next", action: () => moveFocus(1) },
      { id: "duplicates.group.previous", action: () => moveFocus(-1) },
      { id: "duplicates.keep.1", action: keepNumber(0) },
      { id: "duplicates.keep.2", action: keepNumber(1) },
      { id: "duplicates.keep.3", action: keepNumber(2) },
      { id: "duplicates.keep.4", action: keepNumber(3) },
      { id: "duplicates.group.resolve", action: () => reviewable && resolveGroup(focusedGroup) },
      { id: "duplicates.group.ignore", action: () => reviewable && ignoreGroup(focusedGroup) },
      { id: "duplicates.group.compare", action: () => focusedGroup && setCompare({ groupId: focusedGroup.id }) },
    ].map((registration) => ({ ...registration, surface: "page" as const, enabled: reviewing }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusedGroup, focusedIndex, reviewing, displayedGroups, resolution, keeperOverrides]);
  useRegisterKeyboardActions(keyboardActions);

  const totalCount = groupsQuery.data?.totalCount ?? 0;
  const totalPages = Math.max(1, Math.ceil(totalCount / preferences.pageSize));
  const compareGroup = compare ? displayedGroups.find((group) => group.id === compare.groupId) : undefined;
  const targetGroup =
    resolveTarget?.scope === "group" ? displayedGroups.find((group) => group.id === resolveTarget.groupId) : undefined;
  const resolveSummary: ResolveSummary = useMemo(() => {
    if (resolveTarget?.scope === "group" && targetGroup) {
      const keepers = keepersFor(targetGroup);
      const removed = targetGroup.videos.filter((video) => !keepers.has(video.id));
      return {
        groupCount: 1,
        videoCount: removed.length,
        bytes: removed.reduce((sum, video) => sum + totalSize(video), 0),
      };
    }
    return {
      groupCount: (search?.counts.unresolved ?? 0) + (search?.counts.failed ?? 0),
      videoCount: search?.removableVideoCount ?? 0,
      bytes: search?.reclaimableBytes ?? 0,
    };
  }, [resolveTarget, targetGroup, search, keepersFor]);

  return (
    <div className="mx-auto max-w-[1800px] space-y-5">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <div className="rounded-xl bg-accent/15 p-2.5 text-accent">
            <Copy className="h-6 w-6" />
          </div>
          <div>
            <h1 className="text-xl font-semibold text-foreground">Duplicate Finder</h1>
            <p className="text-sm text-muted">
              Find copies of the same video, keep the best one, and reclaim space safely.
            </p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <RecentSearchesMenu searches={recentQuery.data ?? []} currentId={searchId} onOpen={(id) => openSearch(id)} />
          {searchId && !setupOpen ? (
            <button
              type="button"
              onClick={() => setSetupOpen(true)}
              className="inline-flex items-center gap-1.5 rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white hover:bg-accent-hover"
            >
              <Plus className="h-4 w-4" />
              New search
            </button>
          ) : null}
        </div>
      </header>

      {setupOpen ? (
        <DuplicateSearchSetup
          preferences={preferences}
          onChange={setPreferences}
          onStart={() => startMutation.mutate()}
          onCancel={searchId ? () => setSetupOpen(false) : undefined}
          isStarting={startMutation.isPending}
          canRun={canRun}
          canReadFiles={canReadFiles}
          error={startMutation.error ? errorMessage(startMutation.error) : null}
        />
      ) : null}

      {!searchId && (recentQuery.data?.length ?? 0) > 0 ? (
        <RecentSearchList
          searches={recentQuery.data!}
          onOpen={openSearch}
          onDiscard={(id) => discardMutation.mutate(id)}
        />
      ) : null}

      {searchId && searchQuery.isLoading ? (
        <div className="flex items-center gap-2 rounded-xl border border-border bg-card p-5 text-sm text-secondary">
          <Loader2 className="h-4 w-4 animate-spin text-accent" /> Loading search…
        </div>
      ) : null}

      {searchId && searchQuery.isError ? (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-border bg-card p-5">
          <div className="text-sm text-secondary">
            This search is no longer available. Results are kept for 7 days after their last change.
          </div>
          <button
            type="button"
            onClick={() => openSearch(null)}
            className="rounded-md border border-border px-3 py-1.5 text-sm text-foreground hover:border-accent"
          >
            Start a new search
          </button>
        </div>
      ) : null}

      {search ? (
        <SearchSummaryBar
          search={search}
          canReadFiles={canReadFiles}
          onDiscard={() => discardMutation.mutate(search.id)}
        />
      ) : null}

      {search && isRunning ? (
        <SearchProgress
          search={search}
          progress={jobQuery.data?.progress}
          subTask={jobQuery.data?.subTask}
          canCancel={canCancelJobs && Boolean(search.jobId)}
          cancelling={cancelMutation.isPending}
          onCancel={() => search.jobId && cancelMutation.mutate(search.jobId)}
        />
      ) : null}

      {search && (search.status === "failed" || search.status === "cancelled" || search.status === "interrupted") ? (
        <div className="flex flex-wrap items-start justify-between gap-3 rounded-xl border border-red-900/70 bg-red-950/30 p-5">
          <div className="flex items-start gap-3">
            <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-red-300" />
            <div>
              <p className="font-medium capitalize text-red-200">Search {search.status}</p>
              <p className="text-sm text-red-200/80">
                {search.error ||
                  (search.status === "interrupted"
                    ? "Cove restarted while this search was running."
                    : "The search stopped before it finished.")}
              </p>
            </div>
          </div>
          <button
            type="button"
            onClick={() => setSetupOpen(true)}
            className="inline-flex items-center gap-1.5 rounded-md border border-red-800 px-3 py-1.5 text-sm text-red-100 hover:bg-red-900/40"
          >
            <RotateCcw className="h-4 w-4" /> Adjust and try again
          </button>
        </div>
      ) : null}

      {search && completed ? (
        <>
          <StatTiles search={search} deleteFiles={canDeleteFiles && resolution.deleteFiles} />

          {search.groupCount === 0 ? (
            <EmptyResults search={search} onAdjust={() => setSetupOpen(true)} />
          ) : (
            <>
              <div className="md:sticky md:top-0 z-20 -mx-1 space-y-3 rounded-xl border border-border bg-surface/95 px-3 py-3 shadow-lg backdrop-blur supports-[backdrop-filter]:bg-surface/80">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div className="flex flex-wrap gap-1 rounded-lg bg-card p-1" role="tablist" aria-label="Group status">
                    {GROUP_FILTERS.map((filter) => {
                      const count =
                        filter.value === "unresolved"
                          ? search.counts.unresolved + search.counts.failed
                          : filter.value === "queued"
                            ? search.counts.queued
                            : filter.value === "resolved"
                              ? search.counts.resolved
                              : search.counts.ignored;
                      if (filter.value === "queued" && count === 0 && url.status !== "queued") return null;
                      return (
                        <button
                          key={filter.value}
                          type="button"
                          role="tab"
                          aria-selected={url.status === filter.value}
                          onClick={() => updateUrl({ status: filter.value, page: 1 })}
                          className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm transition-colors ${
                            url.status === filter.value
                              ? "bg-surface text-foreground shadow"
                              : "text-secondary hover:text-foreground"
                          }`}
                        >
                          {filter.value === "queued" && count > 0 ? (
                            <Loader2 className="h-3.5 w-3.5 animate-spin text-accent" />
                          ) : null}
                          {filter.label}
                          <span
                            className={`rounded-full px-1.5 text-xs tabular-nums ${url.status === filter.value ? "bg-accent/20 text-accent" : "bg-surface text-muted"}`}
                          >
                            {count.toLocaleString()}
                          </span>
                        </button>
                      );
                    })}
                  </div>
                  <div className="flex flex-wrap items-center gap-2">
                    {canResolve && url.status === "unresolved" ? (
                      <>
                        <button
                          type="button"
                          onClick={() => setAutoSelectOpen(true)}
                          className="inline-flex items-center gap-1.5 rounded-md border border-border bg-card px-3 py-1.5 text-sm text-secondary hover:border-accent/60 hover:text-foreground"
                          title="Re-pick keepers in every group using rules"
                        >
                          <Sparkles className="h-4 w-4 text-amber-300" />
                          Auto-select keepers
                        </button>
                        <button
                          type="button"
                          onClick={() => setResolveTarget({ scope: "all" })}
                          disabled={search.removableVideoCount === 0}
                          className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-semibold text-white shadow-sm disabled:cursor-not-allowed disabled:opacity-40 ${
                            canDeleteFiles && resolution.deleteFiles
                              ? "bg-red-600 hover:bg-red-500"
                              : "bg-accent hover:bg-accent-hover"
                          }`}
                        >
                          {canWrite && resolution.action === "merge" ? (
                            <GitMerge className="h-4 w-4" />
                          ) : (
                            <Trash2 className="h-4 w-4" />
                          )}
                          Resolve all {(search.counts.unresolved + search.counts.failed).toLocaleString()}
                        </button>
                      </>
                    ) : null}
                  </div>
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  <label className="relative min-w-[14rem] flex-1">
                    <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
                    <input
                      value={queryDraft}
                      onChange={(event) => setQueryDraft(event.target.value)}
                      placeholder="Filter by title, file, studio, performer or tag"
                      className="w-full rounded-md border border-border bg-card py-1.5 pl-8 pr-8 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
                    />
                    {queryDraft ? (
                      <button
                        type="button"
                        aria-label="Clear filter"
                        onClick={() => setQueryDraft("")}
                        className="absolute right-2 top-1/2 -translate-y-1/2 text-muted hover:text-foreground"
                      >
                        <X className="h-4 w-4" />
                      </button>
                    ) : null}
                  </label>
                  <select
                    value={url.sort}
                    onChange={(event) => updateUrl({ sort: event.target.value as DuplicateGroupSort, page: 1 })}
                    aria-label="Sort groups"
                    className="rounded-md border border-border bg-card px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
                  >
                    {GROUP_SORTS.map((option) => (
                      <option key={option.value} value={option.value}>
                        {option.label}
                      </option>
                    ))}
                  </select>
                  <select
                    value={preferences.pageSize}
                    onChange={(event) => {
                      setPreferences({ ...preferences, pageSize: Number(event.target.value) });
                      updateUrl({ page: 1 });
                    }}
                    aria-label="Groups per page"
                    className="rounded-md border border-border bg-card px-2 py-1.5 text-sm text-foreground focus:border-accent focus:outline-none"
                  >
                    {PAGE_SIZES.map((size) => (
                      <option key={size} value={size}>
                        {size} per page
                      </option>
                    ))}
                  </select>
                  <label className="inline-flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm text-secondary hover:text-foreground">
                    <input
                      type="checkbox"
                      checked={preferences.showIdenticalRows}
                      onChange={(event) => setPreferences({ ...preferences, showIdenticalRows: event.target.checked })}
                      className="accent-accent"
                    />
                    Show identical details
                  </label>
                  {canResolve ? (
                    <ResolutionOptionsSummary
                      resolution={resolution}
                      canDeleteFiles={canDeleteFiles}
                      canMerge={canWrite}
                      onChange={setResolution}
                    />
                  ) : null}
                  <button
                    type="button"
                    onClick={() => setShortcutsOpen(true)}
                    className="rounded-md p-1.5 text-muted hover:text-foreground"
                    aria-label="Keyboard shortcuts"
                    title="Keyboard shortcuts"
                  >
                    <Keyboard className="h-4 w-4" />
                  </button>
                </div>
              </div>

              {notice ? <NoticeBar notice={notice} onDismiss={() => setNotice(null)} /> : null}

              {groupsQuery.isError ? (
                <div className="rounded-xl border border-red-900/70 bg-red-950/30 p-4 text-sm text-red-200">
                  {errorMessage(groupsQuery.error)}
                </div>
              ) : null}

              {groupsQuery.isLoading ? (
                <div className="flex items-center justify-center gap-2 py-16 text-sm text-secondary">
                  <Loader2 className="h-4 w-4 animate-spin text-accent" /> Loading groups…
                </div>
              ) : displayedGroups.length === 0 ? (
                <FilterEmptyState status={url.status} query={url.q} onClearQuery={() => setQueryDraft("")} />
              ) : (
                <div className={`space-y-4 transition-opacity ${groupsQuery.isPlaceholderData ? "opacity-60" : ""}`}>
                  {displayedGroups.map((group, index) => (
                    <DuplicateGroupCard
                      key={group.id}
                      ref={(element) => {
                        if (element) groupRefs.current.set(group.id, element);
                        else groupRefs.current.delete(group.id);
                      }}
                      group={group}
                      keepVideoIds={keepersFor(group)}
                      engagement={engagementById}
                      resolution={{
                        ...resolution,
                        action: canWrite ? resolution.action : "remove",
                        deleteFiles: canDeleteFiles && resolution.deleteFiles,
                      }}
                      focused={index === focusedIndex}
                      busy={decisionMutation.isPending && decisionMutation.variables?.groupId === group.id}
                      showIdenticalRows={preferences.showIdenticalRows}
                      canResolve={canResolve}
                      canIgnore={canWrite}
                      onFocus={() => setFocusedIndex(index)}
                      onKeepOnly={(videoId) => setKeepers(group, [videoId])}
                      onToggleKeep={(videoId) => {
                        const next = new Set(keepersFor(group));
                        if (next.has(videoId)) next.delete(videoId);
                        else next.add(videoId);
                        setKeepers(group, [...next]);
                      }}
                      onResolve={() => resolveGroup(group)}
                      onIgnore={() => ignoreGroup(group)}
                      onRestore={() => ignoreMutation.mutate({ group, restore: true })}
                      onCompare={(pair) => setCompare({ groupId: group.id, pair })}
                      onQuickView={setQuickViewId}
                      onNavigate={onNavigate}
                    />
                  ))}
                </div>
              )}

              {totalPages > 1 ? (
                <nav className="flex items-center justify-center gap-1 py-2" aria-label="Group pages">
                  <PaginationControls
                    page={url.page}
                    totalPages={totalPages}
                    goTo={(page) => {
                      updateUrl({ page });
                      window.scrollTo({ top: 0, behavior: "smooth" });
                    }}
                  />
                </nav>
              ) : null}
            </>
          )}
        </>
      ) : null}

      {compareGroup ? (
        <DuplicateCompareDialog
          open
          videos={compareGroup.videos}
          keepVideoIds={keepersFor(compareGroup)}
          initialPair={compare?.pair}
          onClose={() => setCompare(null)}
        />
      ) : null}

      {quickViewId != null ? (
        <QuickViewDialog type="video" id={quickViewId} onClose={() => setQuickViewId(null)} onNavigate={onNavigate} />
      ) : null}

      <DuplicateResolveDialog
        open={resolveTarget != null}
        scope={resolveTarget?.scope ?? "all"}
        summary={resolveSummary}
        preferences={resolution}
        canDeleteFiles={canDeleteFiles}
        canMerge={canWrite}
        isPending={resolveMutation.isPending}
        error={resolveMutation.error && resolveTarget ? errorMessage(resolveMutation.error) : null}
        onChange={setResolution}
        onClose={() => {
          setResolveTarget(null);
          resolveMutation.reset();
        }}
        onConfirm={() => {
          if (resolveTarget?.scope === "group" && targetGroup) resolveGroup(targetGroup, true);
          else resolveMutation.mutate({ groupIds: null });
        }}
      />

      <AutoSelectDialog
        open={autoSelectOpen}
        initialRules={search?.keeperRules ?? preferences.keeperRules}
        canReadFiles={canReadFiles}
        isPending={autoSelectMutation.isPending}
        error={autoSelectMutation.error ? errorMessage(autoSelectMutation.error) : null}
        onClose={() => setAutoSelectOpen(false)}
        onApply={(rules, overwriteManual) => {
          setPreferences({ ...preferences, keeperRules: rules });
          autoSelectMutation.mutate({ rules, overwriteManual });
        }}
      />

      <ShortcutsDialog open={shortcutsOpen} onClose={() => setShortcutsOpen(false)} />
    </div>
  );
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : "The request failed.";
}

function relativeTime(value: string | null | undefined) {
  if (!value) return "";
  const seconds = Math.round((Date.now() - new Date(value).getTime()) / 1000);
  if (seconds < 60) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.round(hours / 24);
  return `${days} day${days === 1 ? "" : "s"} ago`;
}

function SearchSummaryBar({
  search,
  canReadFiles,
  onDiscard,
}: {
  search: DuplicateSearchInfo;
  canReadFiles: boolean;
  onDiscard: () => void;
}) {
  const scopeParts = [
    search.includePaths.length
      ? `in ${search.includePaths.length} folder${search.includePaths.length === 1 ? "" : "s"}`
      : "whole library",
    search.excludePaths.length ? `excluding ${search.excludePaths.length}` : null,
    search.minimumDuration > 0 ? `≥ ${search.minimumDuration}s` : null,
  ].filter(Boolean);
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span className="font-medium text-secondary">{describeSearch(search)}</span>
        <span
          title={
            canReadFiles
              ? [...search.includePaths, ...search.excludePaths.map((path) => `not ${path}`)].join("\n")
              : undefined
          }
        >
          {scopeParts.join(" · ")}
        </span>
        <span className="inline-flex items-center gap-1">
          <Clock className="h-3.5 w-3.5" />
          {relativeTime(search.completedAt ?? search.createdAt)}
        </span>
        <span>{search.candidateCount.toLocaleString()} videos compared</span>
        <span>Keeper rules: {describeRules(search.keeperRules)}</span>
      </div>
      {search.status !== "pending" && search.status !== "running" && !search.resolutionJobId ? (
        <button type="button" onClick={onDiscard} className="text-xs text-muted hover:text-red-300">
          Discard search
        </button>
      ) : null}
    </div>
  );
}

function SearchProgress({
  search,
  progress,
  subTask,
  canCancel,
  cancelling,
  onCancel,
}: {
  search: DuplicateSearchInfo;
  progress?: number;
  subTask?: string | null;
  canCancel: boolean;
  cancelling: boolean;
  onCancel: () => void;
}) {
  const percent = Math.round(Math.min(1, Math.max(0, progress ?? 0)) * 100);
  return (
    <section className="rounded-xl border border-border bg-card p-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <Loader2 className="h-6 w-6 animate-spin text-accent" />
          <div>
            <p className="font-medium text-foreground">
              {search.status === "pending" ? "Waiting to start…" : subTask || "Searching for duplicates…"}
            </p>
            <p className="text-sm text-muted">
              {search.candidateCount > 0 ? `${search.candidateCount.toLocaleString()} videos in scope. ` : ""}
              You can leave this page — the search keeps running and results wait here for 7 days.
            </p>
          </div>
        </div>
        {canCancel ? (
          <button
            type="button"
            onClick={onCancel}
            disabled={cancelling}
            className="rounded-md border border-border px-3 py-1.5 text-sm text-secondary hover:border-red-700 hover:text-red-300 disabled:opacity-50"
          >
            Cancel search
          </button>
        ) : null}
      </div>
      <div className="mt-4 h-2 overflow-hidden rounded-full bg-surface">
        <div
          className={`h-full rounded-full bg-accent transition-[width] duration-500 ${search.status === "pending" ? "w-1/3 animate-pulse" : ""}`}
          style={search.status === "pending" ? undefined : { width: `${Math.max(2, percent)}%` }}
        />
      </div>
      {search.status === "running" ? (
        <p className="mt-1.5 text-right text-xs tabular-nums text-muted">{percent}%</p>
      ) : null}
    </section>
  );
}

function StatTiles({ search, deleteFiles }: { search: DuplicateSearchInfo; deleteFiles: boolean }) {
  const tiles = [
    {
      label: "Groups to review",
      value: (search.counts.unresolved + search.counts.failed).toLocaleString(),
      detail: `${search.groupCount.toLocaleString()} found · ${search.videoCount.toLocaleString()} videos`,
    },
    {
      label: "Marked for removal",
      value: search.removableVideoCount.toLocaleString(),
      detail:
        search.counts.failed > 0
          ? `${search.counts.failed} group${search.counts.failed === 1 ? "" : "s"} need attention`
          : "copies across unreviewed groups",
      tone: search.counts.failed > 0 ? "warn" : undefined,
    },
    {
      label: deleteFiles ? "Space you can free" : "Space in duplicate copies",
      value: formatFileSize(search.reclaimableBytes),
      detail: deleteFiles ? "with files deleted from disk" : "turn on file deletion to reclaim it",
    },
    {
      label: "Resolved so far",
      value: search.counts.resolved.toLocaleString(),
      detail:
        search.removedVideoCount > 0
          ? `${search.removedVideoCount.toLocaleString()} ${search.removedVideoCount === 1 ? "copy" : "copies"} removed${search.removedBytes > 0 ? ` · ${formatFileSize(search.removedBytes)}` : ""}`
          : search.counts.ignored > 0
            ? `${search.counts.ignored} marked not duplicates`
            : "nothing removed yet",
      tone: search.counts.resolved > 0 ? "good" : undefined,
    },
  ];
  return (
    <div className="grid grid-cols-2 gap-3 xl:grid-cols-4">
      {tiles.map((tile) => (
        <div key={tile.label} className="min-w-0 rounded-xl border border-border bg-card px-3 py-2.5 sm:px-4 sm:py-3">
          <div className="text-xs font-medium uppercase tracking-wide text-muted">{tile.label}</div>
          <div
            className={`mt-1 text-xl font-semibold tabular-nums sm:text-2xl ${
              tile.tone === "good" ? "text-emerald-300" : tile.tone === "warn" ? "text-amber-300" : "text-foreground"
            }`}
          >
            {tile.value}
          </div>
          <div className="mt-0.5 text-xs text-muted">{tile.detail}</div>
        </div>
      ))}
    </div>
  );
}

function ResolutionOptionsSummary({
  resolution,
  canDeleteFiles,
  canMerge,
  onChange,
}: {
  resolution: ResolutionPreferences;
  canDeleteFiles: boolean;
  canMerge: boolean;
  onChange: (next: ResolutionPreferences) => void;
}) {
  const [open, setOpen] = useState(false);
  const anchorRef = useRef<HTMLButtonElement>(null);
  const close = useCallback(() => setOpen(false), []);
  const merge = canMerge && resolution.action === "merge";
  const deleteFiles = canDeleteFiles && resolution.deleteFiles;
  return (
    <div className="ml-auto">
      <button
        ref={anchorRef}
        type="button"
        onClick={() => setOpen((value) => !value)}
        aria-expanded={open}
        className={`inline-flex items-center gap-2 rounded-md border px-2.5 py-1.5 text-sm ${
          deleteFiles
            ? "border-red-800 bg-red-950/40 text-red-200"
            : "border-border bg-card text-secondary hover:text-foreground"
        }`}
      >
        {merge ? <GitMerge className="h-4 w-4" /> : <Trash2 className="h-4 w-4" />}
        {merge ? "Merge metadata" : "Remove only"} · {deleteFiles ? "delete files" : "keep files on disk"}
        <ChevronDown className="h-3.5 w-3.5" />
      </button>
      <AnchoredPopover anchorRef={anchorRef} open={open} onClose={close}>
        <div className="space-y-2 p-3 text-sm">
          <div className="font-medium text-foreground">When resolving a group</div>
          <label className={`flex items-start gap-2 ${canMerge ? "cursor-pointer" : "opacity-50"}`}>
            <input
              type="radio"
              disabled={!canMerge}
              checked={merge}
              onChange={() => onChange({ ...resolution, action: "merge" })}
              className="mt-1 accent-accent"
            />
            <span>
              <span className="block text-foreground">Merge metadata into the keeper</span>
              <span className="block text-xs text-muted">Tags, performers, ratings, plays and markers carry over.</span>
            </span>
          </label>
          <label className="flex cursor-pointer items-start gap-2">
            <input
              type="radio"
              checked={!merge}
              onChange={() => onChange({ ...resolution, action: "remove" })}
              className="mt-1 accent-accent"
            />
            <span>
              <span className="block text-foreground">Only remove the other copies</span>
            </span>
          </label>
          <div className="border-t border-border pt-2">
            <label className={`flex items-center gap-2 ${canDeleteFiles ? "cursor-pointer" : "opacity-50"}`}>
              <input
                type="checkbox"
                disabled={!canDeleteFiles}
                checked={deleteFiles}
                onChange={(event) => onChange({ ...resolution, deleteFiles: event.target.checked })}
                className="accent-red-500"
              />
              <span className={deleteFiles ? "text-red-200" : "text-foreground"}>Delete files from disk</span>
            </label>
            <label className="mt-1.5 flex cursor-pointer items-center gap-2">
              <input
                type="checkbox"
                checked={resolution.deleteGenerated}
                onChange={(event) => onChange({ ...resolution, deleteGenerated: event.target.checked })}
                className="accent-accent"
              />
              <span className="text-foreground">Delete generated previews</span>
            </label>
            <label className="mt-1.5 flex cursor-pointer items-center gap-2">
              <input
                type="checkbox"
                checked={resolution.confirmEachGroup}
                onChange={(event) => onChange({ ...resolution, confirmEachGroup: event.target.checked })}
                className="accent-accent"
              />
              <span className="text-foreground">Confirm before resolving each group</span>
            </label>
          </div>
        </div>
      </AnchoredPopover>
    </div>
  );
}

function NoticeBar({ notice, onDismiss }: { notice: Notice; onDismiss: () => void }) {
  const tone =
    notice.tone === "error"
      ? "border-red-900/70 bg-red-950/40 text-red-200"
      : notice.tone === "success"
        ? "border-emerald-900/70 bg-emerald-950/30 text-emerald-200"
        : "border-border bg-card text-secondary";
  return (
    <div
      role="status"
      className={`flex items-center justify-between gap-3 rounded-lg border px-4 py-2 text-sm ${tone}`}
    >
      <span className="flex items-center gap-2">
        {notice.tone === "error" ? (
          <AlertTriangle className="h-4 w-4" />
        ) : notice.tone === "success" ? (
          <CheckCircle2 className="h-4 w-4" />
        ) : null}
        {notice.message}
      </span>
      <span className="flex items-center gap-2">
        {notice.undo ? (
          <button
            type="button"
            onClick={() => {
              notice.undo?.();
              onDismiss();
            }}
            className="rounded-md border border-current/30 px-2 py-0.5 text-xs font-medium hover:bg-white/5"
          >
            Undo
          </button>
        ) : null}
        <button type="button" onClick={onDismiss} aria-label="Dismiss" className="opacity-70 hover:opacity-100">
          <X className="h-4 w-4" />
        </button>
      </span>
    </div>
  );
}

function EmptyResults({ search, onAdjust }: { search: DuplicateSearchInfo; onAdjust: () => void }) {
  const tip =
    search.matchType === "fingerprint"
      ? "Identical-file matching only catches byte-for-byte copies. Try “Looks the same” to find re-encodes."
      : search.matchType === "phash" && search.distance < 8
        ? "Try a lower accuracy or a larger duration tolerance to catch more variations."
        : search.matchType === "phash"
          ? "Videos need visual fingerprints (pHash) — generate them from Settings → Tasks if many are missing."
          : "Try matching by how videos look instead.";
  return (
    <div className="rounded-xl border border-border bg-card px-6 py-14 text-center">
      <CheckCircle2 className="mx-auto h-12 w-12 text-emerald-400" />
      <p className="mt-3 text-lg font-medium text-foreground">No duplicates found</p>
      <p className="mx-auto mt-1 max-w-lg text-sm text-muted">{tip}</p>
      <button
        type="button"
        onClick={onAdjust}
        className="mt-4 rounded-md border border-border px-3 py-1.5 text-sm text-secondary hover:border-accent hover:text-foreground"
      >
        Adjust search
      </button>
    </div>
  );
}

function FilterEmptyState({
  status,
  query,
  onClearQuery,
}: {
  status: DuplicateGroupFilter;
  query: string;
  onClearQuery: () => void;
}) {
  const messages: Record<DuplicateGroupFilter, string> = {
    unresolved: "Everything here has been reviewed. Nice work!",
    queued: "Nothing is being resolved right now.",
    resolved: "No groups have been resolved yet.",
    ignored: "No groups are marked as not duplicates.",
    failed: "No groups failed.",
    all: "No groups.",
  };
  return (
    <div className="rounded-xl border border-dashed border-border px-6 py-12 text-center">
      {status === "unresolved" && !query ? <CheckCircle2 className="mx-auto mb-3 h-10 w-10 text-emerald-400" /> : null}
      <p className="text-secondary">{query ? `No groups match “${query}”.` : messages[status]}</p>
      {query ? (
        <button type="button" onClick={onClearQuery} className="mt-3 text-sm text-accent hover:underline">
          Clear filter
        </button>
      ) : null}
    </div>
  );
}

function RecentSearchesMenu({
  searches,
  currentId,
  onOpen,
}: {
  searches: Awaited<ReturnType<typeof videos.listDuplicateSearches>>;
  currentId: string | null;
  onOpen: (id: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const anchorRef = useRef<HTMLButtonElement>(null);
  const close = useCallback(() => setOpen(false), []);
  if (!currentId || searches.length === 0) return null;
  return (
    <div>
      <button
        ref={anchorRef}
        type="button"
        onClick={() => setOpen((value) => !value)}
        aria-expanded={open}
        className="inline-flex items-center gap-1.5 rounded-lg border border-border bg-card px-3 py-2 text-sm text-secondary hover:text-foreground"
      >
        <History className="h-4 w-4" />
        Recent
        <ChevronDown className="h-3.5 w-3.5" />
      </button>
      <AnchoredPopover anchorRef={anchorRef} open={open} onClose={close}>
        <div>
          {searches.map((item) => (
            <button
              key={item.id}
              type="button"
              onClick={() => {
                setOpen(false);
                onOpen(item.id);
              }}
              className={`block w-full px-3 py-2 text-left hover:bg-card ${item.id === currentId ? "bg-card" : ""}`}
            >
              <div className="text-sm text-foreground">{describeSearch({ ...item })}</div>
              <div className="text-xs text-muted">
                {relativeTime(item.createdAt)} · {searchStatusText(item)}
              </div>
            </button>
          ))}
        </div>
      </AnchoredPopover>
    </div>
  );
}

function searchStatusText(item: { status: string; groupCount: number; unresolvedCount: number }) {
  if (item.status !== "completed") return item.status;
  if (item.groupCount === 0) return "no duplicates";
  return item.unresolvedCount > 0
    ? `${item.unresolvedCount.toLocaleString()} of ${item.groupCount.toLocaleString()} groups left`
    : "all reviewed";
}

function RecentSearchList({
  searches,
  onOpen,
  onDiscard,
}: {
  searches: Awaited<ReturnType<typeof videos.listDuplicateSearches>>;
  onOpen: (id: string) => void;
  onDiscard: (id: string) => void;
}) {
  return (
    <section className="rounded-xl border border-border bg-card">
      <h2 className="flex items-center gap-2 border-b border-border px-5 py-3 text-sm font-semibold text-foreground">
        <History className="h-4 w-4 text-muted" />
        Pick up where you left off
      </h2>
      <ul className="divide-y divide-border">
        {searches.map((item) => (
          <li key={item.id} className="flex flex-wrap items-center justify-between gap-3 px-5 py-3">
            <button type="button" onClick={() => onOpen(item.id)} className="min-w-0 flex-1 text-left">
              <div className="text-sm font-medium text-foreground hover:text-accent">
                {describeSearch({ ...item })}
                {item.scoped ? <span className="ml-2 text-xs font-normal text-muted">scoped</span> : null}
              </div>
              <div className="text-xs text-muted">
                {relativeTime(item.createdAt)} · {searchStatusText(item)}
              </div>
            </button>
            <div className="flex items-center gap-2">
              {item.status === "completed" && item.unresolvedCount > 0 ? (
                <div className="hidden w-32 sm:block">
                  <div className="h-1.5 overflow-hidden rounded-full bg-surface">
                    <div
                      className="h-full rounded-full bg-emerald-500"
                      style={{
                        width: `${Math.round(((item.groupCount - item.unresolvedCount) / Math.max(1, item.groupCount)) * 100)}%`,
                      }}
                    />
                  </div>
                </div>
              ) : null}
              <button
                type="button"
                onClick={() => onOpen(item.id)}
                className="rounded-md border border-border px-3 py-1 text-sm text-secondary hover:border-accent hover:text-foreground"
              >
                {item.status === "completed" && item.unresolvedCount > 0 ? "Continue" : "Open"}
              </button>
              <button
                type="button"
                onClick={() => onDiscard(item.id)}
                aria-label="Discard search"
                title="Discard search"
                className="rounded-md p-1.5 text-muted hover:text-red-300"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

function AutoSelectDialog({
  open,
  initialRules,
  canReadFiles,
  isPending,
  error,
  onClose,
  onApply,
}: {
  open: boolean;
  initialRules: DuplicateKeeperRule[];
  canReadFiles: boolean;
  isPending: boolean;
  error: string | null;
  onClose: () => void;
  onApply: (rules: DuplicateKeeperRule[], overwriteManual: boolean) => void;
}) {
  const [rules, setRules] = useState(initialRules);
  const [overwriteManual, setOverwriteManual] = useState(false);
  useEffect(() => {
    if (open) {
      setRules(initialRules);
      setOverwriteManual(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  return (
    <DuplicateDialog
      open={open}
      onClose={onClose}
      dismissible={!isPending}
      title="Auto-select keepers"
      subtitle="Re-pick the copy to keep in every group waiting for review. Rules run top to bottom."
      footer={
        <>
          {error ? <span className="mr-auto text-sm text-red-300">{error}</span> : null}
          <button
            type="button"
            onClick={onClose}
            disabled={isPending}
            className="px-3 py-2 text-sm text-secondary hover:text-foreground"
          >
            Cancel
          </button>
          <button
            type="button"
            data-autofocus
            disabled={isPending}
            onClick={() => onApply(rules, overwriteManual)}
            className="inline-flex items-center gap-2 rounded-md bg-accent px-4 py-2 text-sm font-semibold text-white hover:bg-accent-hover disabled:opacity-50"
          >
            {isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Sparkles className="h-4 w-4" />}
            Apply to all groups
          </button>
        </>
      }
    >
      <KeeperRulesEditor rules={rules} onChange={setRules} canReadFiles={canReadFiles} />
      <label className="mt-4 flex cursor-pointer items-center gap-2 text-sm text-secondary">
        <input
          type="checkbox"
          checked={overwriteManual}
          onChange={(event) => setOverwriteManual(event.target.checked)}
          className="accent-accent"
        />
        Also replace keepers I picked by hand
      </label>
    </DuplicateDialog>
  );
}

const SHORTCUT_ROWS: Array<{ ids: string[]; label: string }> = [
  { ids: ["duplicates.group.next", "duplicates.group.previous"], label: "Next / previous group" },
  {
    ids: ["duplicates.keep.1", "duplicates.keep.2", "duplicates.keep.3", "duplicates.keep.4"],
    label: "Keep only copy 1–4",
  },
  { ids: ["duplicates.group.resolve"], label: "Resolve the group" },
  { ids: ["duplicates.group.ignore"], label: "Mark as not duplicates" },
  { ids: ["duplicates.group.compare"], label: "Compare copies" },
];

function ShortcutsDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const shortcuts = useOptionalKeyboardShortcuts();
  // Show the keys that actually work for this person: a personal preset copied before these actions
  // existed leaves them unbound until they are assigned.
  const keysFor = (id: string) =>
    shortcuts?.effectiveBindings[id] ?? COVE_KEYBOARD_ACTIONS.find((action) => action.id === id)?.defaultBindings ?? [];
  const rows = SHORTCUT_ROWS.map((row) => ({ ...row, keys: row.ids.map((id) => keysFor(id)[0]).filter(Boolean) }));
  const anyUnbound = rows.some((row) => row.keys.length < row.ids.length);
  return (
    <DuplicateDialog open={open} onClose={onClose} size="sm" title="Keyboard shortcuts">
      <dl className="space-y-2 text-sm">
        {rows.map((row) => (
          <div key={row.label} className="flex items-center justify-between gap-4">
            <dt className="text-secondary">{row.label}</dt>
            <dd className="flex gap-1">
              {row.keys.length > 0 ? (
                row.keys.map((key) => (
                  <kbd
                    key={key}
                    className="rounded border border-border bg-card px-2 py-0.5 font-mono text-xs text-foreground"
                  >
                    {key}
                  </kbd>
                ))
              ) : (
                <span className="text-xs text-muted">Not assigned</span>
              )}
            </dd>
          </div>
        ))}
      </dl>
      {anyUnbound ? (
        <p className="mt-4 rounded-md border border-amber-900/60 bg-amber-950/30 px-3 py-2 text-xs text-amber-200">
          Your keyboard preset was created before some of these shortcuts existed, so they are not assigned yet.
        </p>
      ) : null}
      <button
        type="button"
        onClick={() => {
          onClose();
          navigateToUrl("/settings/my/keyboard-shortcuts", { state: { page: "settings" } });
        }}
        className="mt-4 text-sm text-accent hover:underline"
      >
        Customize in Settings → Keyboard Shortcuts
      </button>
    </DuplicateDialog>
  );
}
