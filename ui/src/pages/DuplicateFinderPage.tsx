import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertTriangle,
  Check,
  ChevronDown,
  ChevronUp,
  Copy,
  Folder,
  Images as ImagesIcon,
  Loader2,
  Play,
  Search,
  Settings2,
  ShieldAlert,
  Trash2,
  Video as VideoIcon,
  X,
} from "lucide-react";
import { images, metadata, videos } from "../api/client";
import type {
  DuplicateCleanupOptions,
  DuplicateSearchGroup,
  DuplicateSearchRequest,
  ImageDuplicateGroup,
  Video,
} from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { canDeleteEntity } from "../auth/visibility";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { LibraryFolderTree } from "../components/LibraryFolderTree";
import { createRouteLinkProps } from "../components/cardNavigation";
import { formatDuration, formatFileSize, getResolutionLabel } from "../components/shared";

interface Props {
  onNavigate: (route: any) => void;
}
type MatchType = DuplicateSearchRequest["matchType"];
type MediaKind = "videos" | "images";
type FolderMode = "all" | "include" | "exclude";
type RankingMode = "balanced" | "custom";
interface Preferences {
  folderMode: FolderMode;
  folderPaths: string[];
  rankingMode: RankingMode;
  preferredCodecs: string[];
  keeperRules: string[];
  copyMetadata: boolean;
  overwriteMetadata: boolean;
  deleteGenerated: boolean;
  pageSize: number;
}

const PREFS_KEY = "cove_duplicate_manager_preferences_v1";
const DEFAULT_PREFS: Preferences = {
  folderMode: "all",
  folderPaths: [],
  rankingMode: "balanced",
  preferredCodecs: ["av1", "hevc", "h264", "vp9", "mpeg4"],
  keeperRules: ["resolution", "codec", "bitrate", "duration", "metadata", "oldest"],
  copyMetadata: true,
  overwriteMetadata: false,
  deleteGenerated: true,
  pageSize: 12,
};
const MATCH_OPTIONS: Array<{ value: MatchType; label: string; description: string }> = [
  { value: "fingerprint", label: "Exact fingerprint", description: "Groups byte-identical videos by MD5 or OSHash." },
  {
    value: "phash",
    label: "Visual pHash",
    description: "Finds visually similar videos within a distance and duration window.",
  },
  { value: "title", label: "Same title", description: "Groups videos with the same normalized title." },
  { value: "remoteId", label: "Same remote ID", description: "Groups videos sharing a scraper or metadata-server ID." },
];

function readPreferences(): Preferences {
  try {
    return { ...DEFAULT_PREFS, ...JSON.parse(localStorage.getItem(PREFS_KEY) ?? "{}") };
  } catch {
    return DEFAULT_PREFS;
  }
}
function getUrlState() {
  const p = new URLSearchParams(window.location.search);
  return {
    kind: p.get("kind") === "images" ? ("images" as const) : ("videos" as const),
    videoSearch: p.get("search"),
    imageSearch: p.get("imageSearch"),
  };
}
function replaceUrl(kind: MediaKind, searchId?: string | null) {
  const url = new URL(window.location.href);
  if (kind === "images") {
    url.searchParams.set("kind", "images");
    url.searchParams.delete("search");
    searchId ? url.searchParams.set("imageSearch", searchId) : url.searchParams.delete("imageSearch");
  } else {
    url.searchParams.delete("kind");
    url.searchParams.delete("imageSearch");
    searchId ? url.searchParams.set("search", searchId) : url.searchParams.delete("search");
  }
  window.history.replaceState(window.history.state, "", `${url.pathname}${url.search}${url.hash}`);
}

export function DuplicateFinderPage({ onNavigate }: Props) {
  const initial = useMemo(getUrlState, []);
  const [kind, setKind] = useState<MediaKind>(initial.kind);
  const [videoSearchId, setVideoSearchId] = useState<string | null>(initial.videoSearch);
  const [imageSearchId, setImageSearchId] = useState<string | null>(initial.imageSearch);
  const { hasPermission } = useAuth();
  const canReadVideos = hasPermission("videos.read");
  const canReadImages = hasPermission("images.read");
  useEffect(() => {
    if (kind === "videos" && !canReadVideos && canReadImages) setKind("images");
  }, [canReadImages, canReadVideos, kind]);
  const switchKind = (next: MediaKind) => {
    setKind(next);
    replaceUrl(next, next === "videos" ? videoSearchId : imageSearchId);
  };
  return (
    <div>
      <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <Copy className="h-6 w-6 text-accent" />
          <div>
            <h1 className="text-xl font-semibold text-foreground">Duplicate Manager</h1>
            <p className="text-sm text-muted">Review matches, choose keepers, and clean up safely.</p>
          </div>
        </div>
        <div className="flex rounded-lg border border-border bg-card p-1">
          {canReadVideos && (
            <KindButton
              active={kind === "videos"}
              onClick={() => switchKind("videos")}
              icon={<VideoIcon className="h-4 w-4" />}
              label="Videos"
            />
          )}
          {canReadImages && (
            <KindButton
              active={kind === "images"}
              onClick={() => switchKind("images")}
              icon={<ImagesIcon className="h-4 w-4" />}
              label="Images"
            />
          )}
        </div>
      </div>
      {kind === "videos" ? (
        <VideoManager searchId={videoSearchId} setSearchId={setVideoSearchId} onNavigate={onNavigate} />
      ) : (
        <ImageManager searchId={imageSearchId} setSearchId={setImageSearchId} onNavigate={onNavigate} />
      )}
    </div>
  );
}

function KindButton({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean;
  onClick: () => void;
  icon: ReactNode;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex items-center gap-2 rounded-md px-3 py-1.5 text-sm ${active ? "bg-accent text-white" : "text-secondary hover:text-foreground"}`}
    >
      {icon}
      {label}
    </button>
  );
}

function VideoManager({
  searchId,
  setSearchId,
  onNavigate,
}: {
  searchId: string | null;
  setSearchId: (id: string | null) => void;
  onNavigate: Props["onNavigate"];
}) {
  const [matchType, setMatchType] = useState<MatchType>("fingerprint");
  const [fingerprintAlgorithm, setFingerprintAlgorithm] = useState<"any" | "md5" | "oshash">("any");
  const [phashDistance, setPhashDistance] = useState(8);
  const [durationDiff, setDurationDiff] = useState(10);
  const [minimumDuration, setMinimumDuration] = useState(0);
  const [preferences, setPreferences] = useState(readPreferences);
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [showFolders, setShowFolders] = useState(false);
  const [filter, setFilter] = useState("");
  const [debouncedFilter, setDebouncedFilter] = useState("");
  const [keeperChoices, setKeeperChoices] = useState<Map<number, Set<number>>>(new Map());
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [compare, setCompare] = useState<{ left: Video; right: Video } | null>(null);
  const hydratedRef = useRef<string | null>(null);
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canRun = hasPermission("jobs.run");
  const canDelete = canDeleteEntity("video", hasPermission);
  const canDeleteFiles = hasPermission("videos.delete.file");
  const updatePreferences = (next: Partial<Preferences>) =>
    setPreferences((current) => {
      const value = { ...current, ...next };
      localStorage.setItem(PREFS_KEY, JSON.stringify(value));
      return value;
    });
  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedFilter(filter.trim()), 250);
    return () => window.clearTimeout(timer);
  }, [filter]);

  const searchQuery = useQuery({
    queryKey: ["duplicate-search", searchId],
    queryFn: () => videos.getDuplicateSearch(searchId!),
    enabled: searchId != null,
    refetchInterval: (q) => (["pending", "running"].includes(q.state.data?.status ?? "") ? 1_000 : false),
  });
  useEffect(() => {
    const s = searchQuery.data;
    if (!s || hydratedRef.current === s.id) return;
    setMatchType(s.matchType as MatchType);
    setFingerprintAlgorithm((s.fingerprintAlgorithm || "any") as typeof fingerprintAlgorithm);
    setPhashDistance(s.distance);
    setDurationDiff(s.durationDiff);
    setMinimumDuration(s.minimumDuration || 0);
    updatePreferences({
      folderMode: (s.folderMode || "all") as FolderMode,
      folderPaths: s.folderPaths || [],
      rankingMode: (s.rankingMode || "balanced") as RankingMode,
      preferredCodecs: s.preferredCodecs || DEFAULT_PREFS.preferredCodecs,
      keeperRules: s.keeperRules || DEFAULT_PREFS.keeperRules,
    });
    hydratedRef.current = s.id;
  }, [searchQuery.data]);
  const completed = searchQuery.data?.status === "completed";
  const groupsQuery = useInfiniteQuery({
    queryKey: ["duplicate-search-groups", searchId, debouncedFilter, preferences.pageSize],
    queryFn: ({ pageParam }) =>
      videos.getDuplicateSearchGroups(searchId!, pageParam, preferences.pageSize, debouncedFilter || undefined),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.hasMore ? last.page + 1 : undefined),
    enabled: searchId != null && completed,
  });
  const groups = useMemo(() => groupsQuery.data?.pages.flatMap((page) => page.items) ?? [], [groupsQuery.data]);
  useEffect(() => setKeeperChoices(new Map()), [searchId]);
  useEffect(() => {
    if (!groups.length) return;
    setKeeperChoices((current) => {
      const next = new Map(current);
      for (const group of groups) if (!next.has(group.id)) next.set(group.id, new Set(group.keepVideoIds));
      return next;
    });
  }, [groups]);

  const startMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () =>
      videos.startDuplicateSearch({
        matchType,
        fingerprintAlgorithm,
        distance: matchType === "phash" ? phashDistance : 0,
        durationDiff: matchType === "phash" ? durationDiff : null,
        minimumDuration,
        folderMode: preferences.folderMode,
        folderPaths: preferences.folderPaths,
        rankingMode: preferences.rankingMode,
        preferredCodecs: preferences.preferredCodecs,
        keeperRules: preferences.keeperRules,
      }),
    onSuccess: (result) => {
      replaceUrl("videos", result.searchId);
      setSearchId(result.searchId);
      queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
    },
  });
  const decisionMutation = useMutation({
    mutationFn: ({ groupId, keepVideoIds }: { groupId: number; keepVideoIds: number[] }) =>
      videos.updateDuplicateSearchDecision(searchId!, groupId, keepVideoIds),
    onMutate: ({ groupId, keepVideoIds }) =>
      setKeeperChoices((current) => new Map(current).set(groupId, new Set(keepVideoIds))),
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ["duplicate-search", searchId] });
      queryClient.invalidateQueries({ queryKey: ["duplicate-search-groups", searchId] });
    },
  });
  const deleteMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (options?: DuplicateCleanupOptions) => videos.deleteUnkeptDuplicates(searchId!, options),
    onSuccess: () => {
      setShowDeleteConfirm(false);
      queryClient.invalidateQueries({ queryKey: ["duplicate-search", searchId] });
      queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
    },
  });
  const toggleKeeper = (group: DuplicateSearchGroup, videoId: number) => {
    const current = keeperChoices.get(group.id) ?? new Set(group.keepVideoIds);
    if (current.has(videoId) && current.size === 1) return;
    const next = new Set(current);
    next.has(videoId) ? next.delete(videoId) : next.add(videoId);
    decisionMutation.mutate({ groupId: group.id, keepVideoIds: [...next] });
  };
  const search = searchQuery.data;
  const isRunning = search && ["pending", "running"].includes(search.status);
  const failed = search && ["failed", "cancelled", "interrupted"].includes(search.status);

  return (
    <>
      <section className="mb-5 rounded-lg border border-border bg-card p-4">
        <div className="grid gap-4 lg:grid-cols-[minmax(14rem,1fr)_minmax(9rem,.45fr)_auto] lg:items-end">
          <label className="text-xs font-medium text-secondary">
            Match type
            <select
              aria-label="Match type"
              value={matchType}
              onChange={(e) => setMatchType(e.target.value as MatchType)}
              className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground"
            >
              {MATCH_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
            <span className="mt-1 block text-xs font-normal text-muted">
              {MATCH_OPTIONS.find((option) => option.value === matchType)?.description}
            </span>
          </label>
          {matchType === "fingerprint" ? (
            <label className="text-xs font-medium text-secondary">
              Algorithm
              <select
                value={fingerprintAlgorithm}
                onChange={(e) => setFingerprintAlgorithm(e.target.value as typeof fingerprintAlgorithm)}
                className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground"
              >
                <option value="any">MD5 or OSHash</option>
                <option value="md5">MD5 only</option>
                <option value="oshash">OSHash only</option>
              </select>
            </label>
          ) : (
            <label className={`text-xs font-medium text-secondary ${matchType !== "phash" ? "opacity-50" : ""}`}>
              pHash distance
              <input
                type="number"
                min={0}
                max={64}
                disabled={matchType !== "phash"}
                value={phashDistance}
                onChange={(e) => setPhashDistance(Math.max(0, Math.min(64, Number(e.target.value) || 0)))}
                className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground disabled:opacity-50"
              />
            </label>
          )}
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={() => setShowFolders(true)}
              disabled={preferences.folderMode === "all"}
              className="inline-flex items-center gap-2 rounded-lg border border-border px-3 py-2 text-sm text-secondary disabled:opacity-50"
            >
              <Folder className="h-4 w-4" />
              {preferences.folderMode === "all"
                ? "All folders"
                : preferences.folderPaths.length
                  ? `${preferences.folderPaths.length} folders`
                  : "Choose folders"}
            </button>
            <button
              type="button"
              onClick={() => startMutation.mutate()}
              disabled={
                !canRun ||
                startMutation.isPending ||
                (preferences.folderMode !== "all" && !preferences.folderPaths.length)
              }
              className="inline-flex items-center gap-2 rounded bg-accent px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
            >
              {startMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
              {startMutation.isPending ? "Queueing…" : "Find Duplicates"}
            </button>
          </div>
        </div>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <span className="text-xs text-muted">Folder scope:</span>
          {(["all", "include", "exclude"] as FolderMode[]).map((mode) => (
            <button
              key={mode}
              type="button"
              onClick={() =>
                updatePreferences({ folderMode: mode, folderPaths: mode === "all" ? [] : preferences.folderPaths })
              }
              className={`rounded px-2 py-1 text-xs capitalize ${preferences.folderMode === mode ? "bg-accent/20 text-accent" : "text-secondary hover:bg-surface"}`}
            >
              {mode}
            </button>
          ))}
          <button
            type="button"
            onClick={() => setShowAdvanced((v) => !v)}
            className="ml-auto inline-flex items-center gap-1 text-xs text-secondary"
          >
            <Settings2 className="h-3.5 w-3.5" />
            Advanced {showAdvanced ? <ChevronUp className="h-3.5 w-3.5" /> : <ChevronDown className="h-3.5 w-3.5" />}
          </button>
        </div>
        {showAdvanced && (
          <AdvancedSettings
            matchType={matchType}
            durationDiff={durationDiff}
            setDurationDiff={setDurationDiff}
            minimumDuration={minimumDuration}
            setMinimumDuration={setMinimumDuration}
            preferences={preferences}
            updatePreferences={updatePreferences}
          />
        )}
      </section>
      {(startMutation.error || searchQuery.error || groupsQuery.error) && (
        <ErrorBanner error={startMutation.error ?? searchQuery.error ?? groupsQuery.error} />
      )}
      {searchQuery.isLoading && searchId && <StatusCard text="Loading duplicate search…" />}
      {isRunning && (
        <StatusCard text={`Searching ${search.candidateCount.toLocaleString()} videos in the background…`} />
      )}
      {failed && <ErrorBanner error={search.error || `Search ${search.status}. Start a new search to try again.`} />}
      {search && completed && (
        <>
          <div className="mb-4 flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card px-4 py-3">
            <p className="text-sm text-secondary">
              Found <strong className="text-foreground">{search.groupCount.toLocaleString()}</strong> groups containing{" "}
              <strong className="text-foreground">{search.videoCount.toLocaleString()}</strong> videos.
            </p>
            <label className="ml-auto flex min-w-64 items-center gap-2 rounded-lg border border-border bg-surface px-3 py-1.5">
              <Search className="h-4 w-4 text-muted" />
              <input
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder="Filter results"
                className="min-w-0 flex-1 bg-transparent text-sm text-foreground outline-none"
              />
            </label>
            {canDelete && search.unkeptVideoCount > 0 && (
              <button
                type="button"
                onClick={() => setShowDeleteConfirm(true)}
                disabled={Boolean(search.deletionJobId) || decisionMutation.isPending}
                className="inline-flex items-center gap-2 rounded bg-red-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50"
              >
                <Trash2 className="h-4 w-4" />
                {search.deletionJobId ? "Cleanup queued" : `Remove ${search.unkeptVideoCount.toLocaleString()}`}
              </button>
            )}
          </div>
          {search.groupCount === 0 && <EmptyState label="No video duplicates found" />}
          {groupsQuery.isLoading && search.groupCount > 0 && <StatusCard text="Loading duplicate groups…" />}
          <div className="space-y-4">
            {groups.map((group) => (
              <VideoGroupCard
                key={group.id}
                group={group}
                keepVideoIds={keeperChoices.get(group.id) ?? new Set(group.keepVideoIds)}
                pending={decisionMutation.isPending || Boolean(search.deletionJobId)}
                onToggle={(id) => toggleKeeper(group, id)}
                onSetKeepers={(keepVideoIds) => decisionMutation.mutate({ groupId: group.id, keepVideoIds })}
                onCompare={(left, right) => setCompare({ left, right })}
                onNavigate={onNavigate}
              />
            ))}
          </div>
          {groupsQuery.hasNextPage && (
            <LoadMore loading={groupsQuery.isFetchingNextPage} onClick={() => groupsQuery.fetchNextPage()} />
          )}
        </>
      )}
      {showFolders && (
        <FolderDialog
          mode={preferences.folderMode}
          selected={preferences.folderPaths}
          onClose={() => setShowFolders(false)}
          onApply={(folderPaths) => {
            updatePreferences({ folderPaths });
            setShowFolders(false);
          }}
        />
      )}
      {compare && <Comparator left={compare.left} right={compare.right} onClose={() => setCompare(null)} />}
      <ConfirmDialog
        open={showDeleteConfirm}
        title="Clean up unwanted duplicate videos"
        message={
          search
            ? `Queue removal of ${search.unkeptVideoCount.toLocaleString()} video records referencing ${formatFileSize(search.unkeptBytes)}. Metadata is merged inside the same database transaction as each deletion.`
            : ""
        }
        confirmLabel="Queue cleanup"
        onConfirm={(options) => deleteMutation.mutate(options)}
        onCancel={() => setShowDeleteConfirm(false)}
        isPending={deleteMutation.isPending}
        errorMessage={deleteMutation.error instanceof Error ? deleteMutation.error.message : null}
        showDeleteFile={canDeleteFiles}
        showDeleteGenerated
        showCopyMetadata
        defaultDeleteGenerated={preferences.deleteGenerated}
        defaultCopyMetadata={preferences.copyMetadata}
        defaultOverwriteMetadata={preferences.overwriteMetadata}
      />
    </>
  );
}

function AdvancedSettings({
  matchType,
  durationDiff,
  setDurationDiff,
  minimumDuration,
  setMinimumDuration,
  preferences,
  updatePreferences,
}: {
  matchType: MatchType;
  durationDiff: number;
  setDurationDiff: (n: number) => void;
  minimumDuration: number;
  setMinimumDuration: (n: number) => void;
  preferences: Preferences;
  updatePreferences: (p: Partial<Preferences>) => void;
}) {
  return (
    <div className="mt-4 grid gap-4 border-t border-border pt-4 md:grid-cols-2 xl:grid-cols-4">
      <label className="text-xs font-medium text-secondary">
        Minimum duration (seconds)
        <input
          type="number"
          min={0}
          value={minimumDuration}
          onChange={(e) => setMinimumDuration(Math.max(0, Number(e.target.value) || 0))}
          className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground"
        />
      </label>
      <label className={`text-xs font-medium text-secondary ${matchType !== "phash" ? "opacity-50" : ""}`}>
        Max duration delta (seconds)
        <input
          type="number"
          min={0}
          disabled={matchType !== "phash"}
          value={durationDiff}
          onChange={(e) => setDurationDiff(Math.max(0, Number(e.target.value) || 0))}
          className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground disabled:opacity-50"
        />
      </label>
      <label className="text-xs font-medium text-secondary">
        Keeper ranking
        <select
          value={preferences.rankingMode}
          onChange={(e) => updatePreferences({ rankingMode: e.target.value as RankingMode })}
          className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground"
        >
          <option value="balanced">Balanced quality</option>
          <option value="custom">Custom rule order</option>
        </select>
      </label>
      <label className="text-xs font-medium text-secondary">
        Preferred codecs
        <input
          value={preferences.preferredCodecs.join(", ")}
          onChange={(e) =>
            updatePreferences({
              preferredCodecs: e.target.value
                .split(",")
                .map((v) => v.trim())
                .filter(Boolean),
            })
          }
          className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground"
        />
      </label>
      {preferences.rankingMode === "custom" && (
        <label className="text-xs font-medium text-secondary md:col-span-2 xl:col-span-4">
          Keeper rules, first to last
          <input
            value={preferences.keeperRules.join(", ")}
            onChange={(e) =>
              updatePreferences({
                keeperRules: e.target.value
                  .split(",")
                  .map((v) => v.trim())
                  .filter(Boolean),
              })
            }
            className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground"
          />
        </label>
      )}
      <label className="flex items-start gap-2 text-sm text-secondary md:col-span-2">
        <input
          type="checkbox"
          checked={preferences.copyMetadata}
          onChange={(e) =>
            updatePreferences({
              copyMetadata: e.target.checked,
              overwriteMetadata: e.target.checked ? preferences.overwriteMetadata : false,
            })
          }
          className="mt-0.5 accent-accent"
        />
        <span>
          <strong className="block text-foreground">Copy missing metadata by default</strong>Merge relationships,
          markers, and engagement into the keeper.
        </span>
      </label>
      <label className="flex items-start gap-2 text-sm text-secondary">
        <input
          type="checkbox"
          checked={preferences.overwriteMetadata}
          disabled={!preferences.copyMetadata}
          onChange={(e) => updatePreferences({ overwriteMetadata: e.target.checked })}
          className="mt-0.5 accent-accent disabled:opacity-50"
        />
        <span>
          <strong className="block text-foreground">Overwrite conflicts</strong>Prefer removed records when both have
          values.
        </span>
      </label>
      <label className="flex items-start gap-2 text-sm text-secondary">
        <input
          type="checkbox"
          checked={preferences.deleteGenerated}
          onChange={(e) => updatePreferences({ deleteGenerated: e.target.checked })}
          className="mt-0.5 accent-accent"
        />
        <span>
          <strong className="block text-foreground">Delete generated files</strong>Clean thumbnails, previews, and
          sprites.
        </span>
      </label>
    </div>
  );
}

function VideoGroupCard({
  group,
  keepVideoIds,
  pending,
  onToggle,
  onSetKeepers,
  onCompare,
  onNavigate,
}: {
  group: DuplicateSearchGroup;
  keepVideoIds: Set<number>;
  pending: boolean;
  onToggle: (id: number) => void;
  onSetKeepers: (ids: number[]) => void;
  onCompare: (left: Video, right: Video) => void;
  onNavigate: Props["onNavigate"];
}) {
  const recommended = group.videos.find((video) => video.id === group.recommendedVideoId) ?? group.videos[0];
  const keepRecommended = () => onSetKeepers([recommended.id]);
  return (
    <section className="overflow-hidden rounded-lg border border-border">
      <header className="flex flex-wrap items-center gap-3 border-b border-border bg-card px-4 py-2">
        <strong className="text-sm text-foreground">Group {group.position + 1}</strong>
        <span className="text-sm text-muted">{group.videos.length} videos</span>
        {group.riskScore > 0 && (
          <span
            title={group.riskNotes.join("; ")}
            className="inline-flex items-center gap-1 rounded bg-amber-900/30 px-2 py-0.5 text-xs text-amber-300"
          >
            <ShieldAlert className="h-3.5 w-3.5" />
            risk {group.riskScore}
          </span>
        )}
        {group.recommendationReason && <span className="text-xs text-muted">{group.recommendationReason}</span>}
        <div className="ml-auto flex gap-2">
          <button
            type="button"
            disabled={pending}
            onClick={keepRecommended}
            className="rounded border border-border px-2 py-1 text-xs text-secondary"
          >
            Keep recommended
          </button>
          {group.videos.length > 1 && (
            <button
              type="button"
              onClick={() =>
                onCompare(
                  recommended,
                  group.videos.find((v) => v.id !== recommended.id)!,
                )
              }
              className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-xs text-secondary"
            >
              <Play className="h-3 w-3" />
              Compare
            </button>
          )}
        </div>
      </header>
      <div className="grid sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
        {group.videos.map((video) => (
          <VideoCard
            key={video.id}
            video={video}
            keep={keepVideoIds.has(video.id)}
            recommended={video.id === recommended.id}
            disableToggle={pending || (keepVideoIds.has(video.id) && keepVideoIds.size === 1)}
            onToggle={() => onToggle(video.id)}
            onNavigate={onNavigate}
          />
        ))}
      </div>
    </section>
  );
}

function VideoCard({
  video,
  keep,
  recommended,
  disableToggle,
  onToggle,
  onNavigate,
}: {
  video: Video;
  keep: boolean;
  recommended: boolean;
  disableToggle: boolean;
  onToggle: () => void;
  onNavigate: Props["onNavigate"];
}) {
  const [previewing, setPreviewing] = useState(false);
  const file = video.files[0];
  const route = { page: "video", id: video.id };
  const linkProps = createRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate(route));
  return (
    <article
      className={`relative border-b border-r border-border ${keep ? "bg-green-900/20 ring-2 ring-inset ring-green-500/50" : "bg-background"}`}
    >
      <button
        type="button"
        onClick={onToggle}
        disabled={disableToggle}
        className="absolute left-2 top-2 z-10"
        title={disableToggle ? "Every group needs a keeper" : keep ? "Mark for removal" : "Keep this video"}
      >
        <span
          className={`flex h-5 w-5 items-center justify-center rounded border-2 ${keep ? "border-green-500 bg-green-500" : "border-muted bg-black/50"}`}
        >
          {keep && <Check className="h-3 w-3 text-white" />}
        </span>
      </button>
      {recommended && (
        <span className="absolute right-2 top-2 z-10 rounded bg-accent px-2 py-0.5 text-[10px] font-medium text-white">
          Recommended keeper
        </span>
      )}
      <a
        {...linkProps}
        onMouseEnter={() => setPreviewing(true)}
        onMouseLeave={() => setPreviewing(false)}
        className="block aspect-video overflow-hidden bg-card"
      >
        {previewing ? (
          <video
            src={videos.previewUrl(video.id)}
            poster={videos.screenshotUrl(video.id)}
            autoPlay
            muted
            loop
            playsInline
            className="h-full w-full object-cover"
          />
        ) : (
          <img src={videos.screenshotUrl(video.id)} alt="" loading="lazy" className="h-full w-full object-cover" />
        )}
      </a>
      <div className="space-y-1 p-2">
        <a {...linkProps} className="block truncate text-xs font-medium text-foreground hover:text-accent">
          {video.title || file?.basename || `Video #${video.id}`}
        </a>
        {file && (
          <div className="flex flex-wrap gap-x-3 text-[10px] text-muted">
            <span>
              {file.width}×{file.height}
            </span>
            <span>{getResolutionLabel(file.width, file.height)}</span>
            <span>{formatDuration(file.duration)}</span>
            <span>{formatFileSize(file.size)}</span>
            <span>{file.videoCodec}</span>
            <span>{Math.round(file.bitRate / 1000)} kbps</span>
          </div>
        )}
        {file?.path && (
          <p className="truncate text-[10px] text-muted" title={file.path}>
            {file.path}
          </p>
        )}
      </div>
    </article>
  );
}

function ImageManager({
  searchId,
  setSearchId,
  onNavigate,
}: {
  searchId: string | null;
  setSearchId: (id: string | null) => void;
  onNavigate: Props["onNavigate"];
}) {
  const [minimumMb, setMinimumMb] = useState(0);
  const [showConfirm, setShowConfirm] = useState(false);
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canRun = hasPermission("jobs.run");
  const canClean =
    hasPermission("images.write") && hasPermission("images.delete") && hasPermission("images.delete.file");
  const searchQuery = useQuery({
    queryKey: ["image-duplicate-search", searchId],
    queryFn: () => images.getDuplicateSearch(searchId!),
    enabled: Boolean(searchId),
    refetchInterval: (q) => (["pending", "running"].includes(q.state.data?.status ?? "") ? 1_000 : false),
  });
  const completed = searchQuery.data?.status === "completed";
  const groupsQuery = useInfiniteQuery({
    queryKey: ["image-duplicate-groups", searchId],
    queryFn: ({ pageParam }) => images.getDuplicateSearchGroups(searchId!, pageParam, 12),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.hasMore ? last.page + 1 : undefined),
    enabled: Boolean(searchId) && completed,
  });
  const groups = useMemo(() => groupsQuery.data?.pages.flatMap((page) => page.items) ?? [], [groupsQuery.data]);
  const startMutation = useMutation({
    mutationFn: () => images.startDuplicateSearch(Math.round(minimumMb * 1024 * 1024)),
    onSuccess: (result) => {
      replaceUrl("images", result.searchId);
      setSearchId(result.searchId);
      queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
    },
  });
  const keeperMutation = useMutation({
    mutationFn: ({ groupId, keeperFileId }: { groupId: number; keeperFileId: number }) =>
      images.updateDuplicateKeeper(searchId!, groupId, keeperFileId),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ["image-duplicate-groups", searchId] }),
  });
  const cleanupMutation = useMutation({
    mutationFn: () => images.cleanupDuplicates(searchId!, true, true),
    onSuccess: () => {
      setShowConfirm(false);
      queryClient.invalidateQueries({ queryKey: ["image-duplicate-search", searchId] });
      queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
    },
  });
  const search = searchQuery.data;
  return (
    <>
      <section className="mb-5 rounded-lg border border-border bg-card p-4">
        <div className="flex flex-wrap items-end gap-4">
          <label className="min-w-56 text-xs font-medium text-secondary">
            Minimum file size (MB)
            <input
              type="number"
              min={0}
              value={minimumMb}
              onChange={(e) => setMinimumMb(Math.max(0, Number(e.target.value) || 0))}
              className="mt-1 w-full rounded-lg border border-border bg-surface px-3 py-2 text-sm text-foreground"
            />
          </label>
          <div className="max-w-xl text-xs text-muted">
            <strong className="block text-secondary">Exact stored pHash only</strong>Image cleanup is deliberately
            conservative. Archive-backed files are protected.
          </div>
          <button
            type="button"
            onClick={() => startMutation.mutate()}
            disabled={!canRun || startMutation.isPending}
            className="ml-auto inline-flex items-center gap-2 rounded bg-accent px-4 py-2 text-sm font-medium text-white disabled:opacity-50"
          >
            {startMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Search className="h-4 w-4" />}
            Find Duplicates
          </button>
        </div>
      </section>
      {(startMutation.error || searchQuery.error || groupsQuery.error) && (
        <ErrorBanner error={startMutation.error ?? searchQuery.error ?? groupsQuery.error} />
      )}
      {search && ["pending", "running"].includes(search.status) && (
        <StatusCard text={`Checking ${search.candidateCount.toLocaleString()} image files…`} />
      )}
      {search && ["failed", "cancelled", "interrupted"].includes(search.status) && (
        <ErrorBanner error={search.error || `Search ${search.status}.`} />
      )}
      {search && completed && (
        <>
          <div className="mb-4 flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card px-4 py-3">
            <p className="text-sm text-secondary">
              Found <strong className="text-foreground">{search.groupCount.toLocaleString()}</strong> groups with{" "}
              <strong className="text-foreground">{formatFileSize(search.freeableBytes)}</strong> potentially
              recoverable.
            </p>
            {canClean && search.groupCount > 0 && (
              <button
                type="button"
                onClick={() => setShowConfirm(true)}
                disabled={Boolean(search.cleanupJobId)}
                className="ml-auto inline-flex items-center gap-2 rounded bg-red-600 px-3 py-2 text-sm font-medium text-white disabled:opacity-50"
              >
                <Trash2 className="h-4 w-4" />
                {search.cleanupJobId ? "Cleanup queued" : "Clean reviewed groups"}
              </button>
            )}
          </div>
          {search.groupCount === 0 && <EmptyState label="No exact image duplicates found" />}
          <div className="space-y-4">
            {groups.map((group) => (
              <ImageGroupCard
                key={group.id}
                group={group}
                pending={keeperMutation.isPending || Boolean(search.cleanupJobId)}
                onKeep={(keeperFileId) => keeperMutation.mutate({ groupId: group.id, keeperFileId })}
                onNavigate={onNavigate}
              />
            ))}
          </div>
          {groupsQuery.hasNextPage && (
            <LoadMore loading={groupsQuery.isFetchingNextPage} onClick={() => groupsQuery.fetchNextPage()} />
          )}
        </>
      )}
      <ConfirmDialog
        open={showConfirm}
        title="Permanently clean duplicate images"
        message={
          search
            ? `Delete reviewed duplicate files and merge their image metadata. Up to ${formatFileSize(search.freeableBytes)} may be recovered. Archive-backed files remain protected.`
            : ""
        }
        confirmLabel="Queue permanent cleanup"
        onConfirm={() => cleanupMutation.mutate()}
        onCancel={() => setShowConfirm(false)}
        isPending={cleanupMutation.isPending}
        errorMessage={cleanupMutation.error instanceof Error ? cleanupMutation.error.message : null}
      />
    </>
  );
}

function ImageGroupCard({
  group,
  pending,
  onKeep,
  onNavigate,
}: {
  group: ImageDuplicateGroup;
  pending: boolean;
  onKeep: (id: number) => void;
  onNavigate: Props["onNavigate"];
}) {
  return (
    <section className="overflow-hidden rounded-lg border border-border">
      <header className="flex items-center gap-3 border-b border-border bg-card px-4 py-2">
        <strong className="text-sm text-foreground">{group.files.length} exact copies</strong>
        <span className="text-xs text-muted">{formatFileSize(group.freeableBytes)} recoverable</span>
      </header>
      <div className="grid sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
        {group.files.map((file) => {
          const keep = file.id === group.keeperFileId;
          const route = { page: "image", id: file.imageId };
          const linkProps = createRouteLinkProps<HTMLAnchorElement>(route, () => onNavigate(route));
          return (
            <article
              key={file.id}
              className={`relative border-b border-r border-border ${keep ? "bg-green-900/20 ring-2 ring-inset ring-green-500/50" : "bg-background"}`}
            >
              <button
                type="button"
                disabled={pending || file.protected}
                onClick={() => onKeep(file.id)}
                className="absolute left-2 top-2 z-10"
                title={file.protected ? "Archive-backed files are protected" : "Keep this file"}
              >
                <span
                  className={`flex h-5 w-5 items-center justify-center rounded border-2 ${keep ? "border-green-500 bg-green-500" : "border-muted bg-black/50"}`}
                >
                  {keep && <Check className="h-3 w-3 text-white" />}
                </span>
              </button>
              {file.protected && (
                <span className="absolute right-2 top-2 z-10 rounded bg-amber-800 px-2 py-0.5 text-[10px] text-white">
                  archive · protected
                </span>
              )}
              <a {...linkProps} className="block aspect-square bg-card">
                <img
                  src={images.thumbnailUrl(file.imageId, 500)}
                  alt=""
                  loading="lazy"
                  className="h-full w-full object-contain"
                />
              </a>
              <div className="p-2 text-xs">
                <strong className="text-foreground">
                  {file.width}×{file.height}
                </strong>
                <span className="ml-2 text-muted">{formatFileSize(file.size)}</span>
                <p className="mt-1 truncate text-[10px] text-muted" title={file.path}>
                  {file.path || file.basename}
                </p>
              </div>
            </article>
          );
        })}
      </div>
    </section>
  );
}

function FolderDialog({
  mode,
  selected,
  onClose,
  onApply,
}: {
  mode: FolderMode;
  selected: string[];
  onClose: () => void;
  onApply: (paths: string[]) => void;
}) {
  const [paths, setPaths] = useState(selected);
  const roots = useQuery({
    queryKey: ["library-folders", "duplicate-manager-roots"],
    queryFn: () => metadata.libraryFolders(undefined, true),
  });
  return (
    <Modal title={`${mode === "exclude" ? "Exclude" : "Include"} folders`} onClose={onClose}>
      <p className="mb-3 text-sm text-secondary">
        Select configured library folders. Paths are stored only in this browser.
      </p>
      {roots.isLoading ? (
        <StatusCard text="Loading folders…" />
      ) : roots.error ? (
        <ErrorBanner error={roots.error} />
      ) : (
        <LibraryFolderTree
          roots={roots.data ?? []}
          selected={paths}
          onToggle={(path, checked) =>
            setPaths((current) =>
              checked ? [...new Set([...current, path])] : current.filter((value) => value !== path),
            )
          }
          emptyHint="No library folders are configured."
        />
      )}
      <div className="mt-4 flex justify-end gap-2">
        <button type="button" onClick={onClose} className="px-3 py-2 text-sm text-secondary">
          Cancel
        </button>
        <button
          type="button"
          onClick={() => onApply(paths)}
          disabled={!paths.length}
          className="rounded bg-accent px-3 py-2 text-sm text-white disabled:opacity-50"
        >
          Apply folders
        </button>
      </div>
    </Modal>
  );
}

function Comparator({ left, right, onClose }: { left: Video; right: Video; onClose: () => void }) {
  const leftRef = useRef<HTMLVideoElement>(null);
  const rightRef = useRef<HTMLVideoElement>(null);
  const [wipe, setWipe] = useState(50);
  const toggle = async () => {
    const a = leftRef.current,
      b = rightRef.current;
    if (!a || !b) return;
    if (a.paused) {
      b.currentTime = a.currentTime;
      await Promise.allSettled([a.play(), b.play()]);
    } else {
      a.pause();
      b.pause();
    }
  };
  const sync = () => {
    if (
      leftRef.current &&
      rightRef.current &&
      Math.abs(leftRef.current.currentTime - rightRef.current.currentTime) > 0.25
    )
      rightRef.current.currentTime = leftRef.current.currentTime;
  };
  return (
    <Modal title="A/B video comparator" onClose={onClose} wide>
      <div className="relative aspect-video overflow-hidden rounded-lg bg-black" onClick={() => void toggle()}>
        <video
          ref={leftRef}
          src={videos.streamUrl(left.id)}
          muted
          playsInline
          preload="metadata"
          onTimeUpdate={sync}
          className="h-full w-full object-contain"
        />
        <div className="absolute inset-0" style={{ clipPath: `inset(0 0 0 ${wipe}%)` }}>
          <video
            ref={rightRef}
            src={videos.streamUrl(right.id)}
            muted
            playsInline
            preload="metadata"
            className="h-full w-full object-contain"
          />
        </div>
        <div className="pointer-events-none absolute inset-y-0 w-0.5 bg-white" style={{ left: `${wipe}%` }} />
      </div>
      <input
        aria-label="Comparison wipe"
        type="range"
        min={0}
        max={100}
        value={wipe}
        onChange={(e) => setWipe(Number(e.target.value))}
        className="mt-3 w-full"
      />
      <div className="mt-3 grid grid-cols-2 gap-4 text-xs text-secondary">
        <VideoSummary label="A" video={left} />
        <VideoSummary label="B" video={right} />
      </div>
      <p className="mt-3 text-center text-xs text-muted">
        Click to play or pause both streams. Drag the slider to compare the same frame.
      </p>
    </Modal>
  );
}
function VideoSummary({ label, video }: { label: string; video: Video }) {
  const file = video.files[0];
  return (
    <div>
      <strong className="text-foreground">
        {label}: {video.title || file?.basename || `Video #${video.id}`}
      </strong>
      {file && (
        <p>
          {file.width}×{file.height} · {file.videoCodec} · {Math.round(file.bitRate / 1000)} kbps ·{" "}
          {formatFileSize(file.size)}
        </p>
      )}
    </div>
  );
}
function Modal({
  title,
  onClose,
  wide,
  children,
}: {
  title: string;
  onClose: () => void;
  wide?: boolean;
  children: ReactNode;
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <button aria-label="Close dialog" className="absolute inset-0 bg-black/70" onClick={onClose} />
      <div
        role="dialog"
        aria-modal="true"
        className={`relative max-h-[92vh] w-full overflow-auto rounded-xl border border-border bg-surface p-5 shadow-2xl ${wide ? "max-w-5xl" : "max-w-xl"}`}
      >
        <div className="mb-4 flex items-center justify-between">
          <h2 className="text-lg font-semibold text-foreground">{title}</h2>
          <button type="button" onClick={onClose} className="rounded p-1 text-muted">
            <X className="h-5 w-5" />
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}
function ErrorBanner({ error }: { error: unknown }) {
  return (
    <div className="mb-4 flex items-center gap-2 rounded border border-red-800 bg-red-900/20 p-3 text-sm text-red-300">
      <AlertTriangle className="h-4 w-4 shrink-0" />
      {error instanceof Error ? error.message : String(error || "The request failed.")}
    </div>
  );
}
function StatusCard({ text }: { text: string }) {
  return (
    <div className="mb-4 flex items-center gap-2 rounded-lg border border-border bg-card p-4 text-sm text-secondary">
      <Loader2 className="h-4 w-4 animate-spin text-accent" />
      {text}
    </div>
  );
}
function EmptyState({ label }: { label: string }) {
  return (
    <div className="py-16 text-center">
      <Check className="mx-auto mb-3 h-12 w-12 text-green-400" />
      <p className="text-secondary">{label}</p>
    </div>
  );
}
function LoadMore({ loading, onClick }: { loading: boolean; onClick: () => void }) {
  return (
    <div className="flex justify-center py-4">
      <button
        type="button"
        onClick={onClick}
        disabled={loading}
        className="inline-flex items-center gap-2 rounded-lg border border-border px-4 py-2 text-sm text-foreground disabled:opacity-50"
      >
        {loading && <Loader2 className="h-4 w-4 animate-spin" />}
        {loading ? "Loading…" : "Load more"}
      </button>
    </div>
  );
}
