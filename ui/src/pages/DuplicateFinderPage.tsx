import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { videos } from "../api/client";
import type { Video } from "../api/types";
import { formatDuration, formatFileSize, getResolutionLabel } from "../components/shared";
import { Copy, Trash2, Loader2, Search, AlertTriangle, Check } from "lucide-react";
import { createRouteLinkProps } from "../components/cardNavigation";
import { ConfirmDialog } from "../components/ConfirmDialog";

interface Props {
  onNavigate: (r: any) => void;
}

type DuplicateMatchType = "fingerprint" | "phash" | "title" | "remoteId";

const MATCH_OPTIONS: Array<{ value: DuplicateMatchType; label: string; description: string }> = [
  { value: "fingerprint", label: "Exact file fingerprint", description: "Groups videos that share an MD5 or OSHash." },
  { value: "phash", label: "Similar visual pHash", description: "Finds visually similar videos within a pHash distance." },
  { value: "title", label: "Same title", description: "Groups videos with the same normalized title." },
  { value: "remoteId", label: "Same remote ID", description: "Groups videos that share a scraper or metadata-server ID." },
];

export function DuplicateFinderPage({ onNavigate }: Props) {
  const [matchType, setMatchType] = useState<DuplicateMatchType>("fingerprint");
  const [phashDistance, setPhashDistance] = useState(8);
  const [durationDiff, setDurationDiff] = useState(10);
  const [groups, setGroups] = useState<Video[][] | null>(null);
  const [selectedPerGroup, setSelectedPerGroup] = useState<Map<number, Set<number>>>(new Map());
  const [pendingDelete, setPendingDelete] = useState<{
    groupIdx: number;
    ids: number[];
    keepCount: number;
    fileCount: number;
    totalBytes: number;
  } | null>(null);
  const queryClient = useQueryClient();

  const findMut = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: () => videos.findDuplicates({
      matchType,
      distance: matchType === "phash" ? phashDistance : 0,
      durationDiff: matchType === "phash" && durationDiff >= 0 ? durationDiff : undefined,
    }),
    onSuccess: (data) => {
      setGroups(data);
      setSelectedPerGroup(new Map(data.map((group, index) => {
        const keeper = chooseRecommendedKeeper(group);
        return [index, keeper ? new Set([keeper.id]) : new Set<number>()];
      })));
    },
  });

  const deleteMut = useMutation({
    mutationFn: ({ ids, options }: { ids: number[]; options?: { deleteFile?: boolean; deleteGenerated?: boolean } }) => videos.bulkDelete(ids, options),
    onSuccess: () => {
      setPendingDelete(null);
      queryClient.invalidateQueries({ queryKey: ["videos"] });
      findMut.mutate();
    },
  });

  const toggleSelected = (groupIdx: number, videoId: number) => {
    setSelectedPerGroup((prev) => {
      const next = new Map(prev);
      const set = new Set(next.get(groupIdx) ?? []);
      if (set.has(videoId)) set.delete(videoId);
      else set.add(videoId);
      next.set(groupIdx, set);
      return next;
    });
  };

  const keepSelected = (groupIdx: number) => {
    if (!groups) return;
    const group = groups[groupIdx];
    const kept = selectedPerGroup.get(groupIdx) ?? new Set();
    const toDelete = group.filter((s) => !kept.has(s.id)).map((s) => s.id);
    if (toDelete.length === 0) return;
    const deleteVideos = group.filter((video) => toDelete.includes(video.id));
    setPendingDelete({
      groupIdx,
      ids: toDelete,
      keepCount: kept.size,
      fileCount: deleteVideos.reduce((total, video) => total + video.files.length, 0),
      totalBytes: deleteVideos.reduce((total, video) => total + video.files.reduce((sum, file) => sum + (file.size ?? 0), 0), 0),
    });
  };

  return (
    <>
    <div>
      {/* Header */}
      <div className="flex items-center gap-3 mb-6">
        <Copy className="w-6 h-6 text-accent" />
        <h1 className="text-xl font-semibold text-foreground">Duplicate Finder</h1>
      </div>

      {/* Controls */}
      <div className="mb-6 rounded-lg border border-border bg-card p-4">
        <div className="grid gap-4 lg:grid-cols-[minmax(16rem,1fr)_minmax(10rem,0.45fr)_minmax(10rem,0.45fr)_auto] lg:items-end">
        <div>
          <label className="block text-xs font-medium text-secondary mb-1">
            Match type
          </label>
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
            max={64}
            value={phashDistance}
            disabled={matchType !== "phash"}
            onChange={(event) => setPhashDistance(Math.max(0, Math.min(64, Number(event.target.value) || 0)))}
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
          onClick={() => findMut.mutate()}
          disabled={findMut.isPending}
          className="flex items-center gap-2 px-4 py-2 rounded text-sm font-medium bg-accent hover:bg-accent-hover text-white disabled:opacity-50"
        >
          {findMut.isPending ? (
            <Loader2 className="w-4 h-4 animate-spin" />
          ) : (
            <Search className="w-4 h-4" />
          )}
          {findMut.isPending ? "Searching..." : "Find Duplicates"}
        </button>
        </div>
      </div>

      {/* Error */}
      {findMut.isError && (
        <div className="flex items-center gap-2 p-3 mb-4 bg-red-900/20 border border-red-800 rounded text-red-300 text-sm">
          <AlertTriangle className="w-4 h-4 shrink-0" />
          {(findMut.error as Error).message}
        </div>
      )}

      {/* Results summary */}
      {groups !== null && (
        <div className="mb-4 text-sm text-secondary">
          Found <span className="font-semibold text-foreground">{groups.length}</span> duplicate group{groups.length !== 1 ? "s" : ""}
          {groups.length > 0 && (
            <span>
              {" "}containing{" "}
              <span className="font-semibold text-foreground">
                {groups.reduce((n, g) => n + g.length, 0)}
              </span>{" "}
              total videos
            </span>
          )}
        </div>
      )}

      {/* No results */}
      {groups !== null && groups.length === 0 && (
        <div className="text-center py-16">
          <Check className="w-12 h-12 mx-auto mb-3 text-green-400" />
          <p className="text-secondary">No duplicates found</p>
          <p className="mt-1 text-sm text-muted">Try a visual pHash search if exact file hashes are unavailable for this library.</p>
        </div>
      )}

      {/* Duplicate groups */}
      {groups && groups.length > 0 && (
        <div className="space-y-4">
          {groups.map((group, gi) => {
            const selected = selectedPerGroup.get(gi) ?? new Set();
            return (
              <div key={gi} className="border border-border rounded-lg overflow-hidden">
                {/* Group header */}
                <div className="flex items-center justify-between px-4 py-2 bg-card border-b border-border">
                  <span className="text-sm font-medium text-foreground">
                    Group {gi + 1} — {group.length} videos
                  </span>
                  <div className="flex items-center gap-2">
                    <span className="text-xs text-muted">
                      {selected.size > 0
                        ? `${selected.size} selected to keep`
                        : "Select videos to keep"}
                    </span>
                    <button
                      onClick={() => keepSelected(gi)}
                      disabled={selected.size === 0 || selected.size === group.length || deleteMut.isPending}
                      className="flex items-center gap-1 px-2 py-1 text-xs rounded bg-red-600 hover:bg-red-500 text-white disabled:opacity-30 disabled:cursor-not-allowed"
                    >
                      <Trash2 className="w-3 h-3" />
                      Delete Others
                    </button>
                  </div>
                </div>

                {/* Video cards */}
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-0">
                  {group.map((video) => {
                    const file = video.files[0];
                    const isSelected = selected.has(video.id);
                    const linkProps = createRouteLinkProps<HTMLAnchorElement>({ page: "video", id: video.id }, () => onNavigate({ page: "video", id: video.id }));
                    return (
                      <div
                        key={video.id}
                        className={`relative border-r border-b border-border last:border-r-0 ${
                          isSelected ? "bg-green-900/20 ring-inset ring-2 ring-green-500/50" : "bg-background"
                        }`}
                      >
                        {/* Selection overlay */}
                        <button
                          onClick={() => toggleSelected(gi, video.id)}
                          className="absolute top-2 left-2 z-10"
                        >
                          <div
                            className={`w-5 h-5 rounded border-2 flex items-center justify-center ${
                              isSelected
                                ? "bg-green-500 border-green-500"
                                : "border-muted bg-black/40"
                            }`}
                          >
                            {isSelected && <Check className="w-3 h-3 text-white" />}
                          </div>
                        </button>

                        {/* Thumbnail */}
                        <a
                          {...linkProps}
                          className="aspect-video bg-card cursor-pointer block w-full overflow-hidden"
                        >
                          {/** Use an empty alt when there is no uploaded cover so Firefox doesn't render the title inside broken images. */}
                          <img
                            src={videos.screenshotUrl(video.id)}
                            alt={video.imagePath ? video.title || "" : ""}
                            className="w-full h-full object-cover"
                            loading="lazy"
                            onError={(e) => {
                              (e.target as HTMLImageElement).style.display = "none";
                            }}
                          />
                        </a>

                        {/* Details */}
                        <div className="p-2 space-y-1">
                          <a
                            {...linkProps}
                            className="block max-w-full truncate text-left text-xs font-medium text-foreground hover:text-accent"
                          >
                            {video.title || file?.basename || `Video #${video.id}`}
                          </a>
                          {file && (
                            <div className="flex flex-wrap gap-x-3 gap-y-0.5 text-[10px] text-muted">
                              <span>{file.width}×{file.height}</span>
                              <span>{getResolutionLabel(file.width, file.height)}</span>
                              <span>{formatDuration(file.duration)}</span>
                              <span>{formatFileSize(file.size)}</span>
                              <span>{file.videoCodec}</span>
                              <span>{Math.round(file.bitRate / 1000)} kbps</span>
                            </div>
                          )}
                          {file?.path && (
                            <p className="text-[9px] text-muted truncate" title={file.path}>
                              {file.path}
                            </p>
                          )}
                        </div>
                      </div>
                    );
                  })}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
    <ConfirmDialog
      open={pendingDelete != null}
      title="Delete duplicate videos"
      message={pendingDelete
        ? `Delete ${pendingDelete.ids.length} duplicate video record(s) from group ${pendingDelete.groupIdx + 1} and keep ${pendingDelete.keepCount}. This affects ${pendingDelete.fileCount} file record(s) totaling ${formatFileSize(pendingDelete.totalBytes)}. Physical files are only deleted if you enable the checkbox below.`
        : ""}
      confirmLabel={deleteMut.isPending ? "Deleting..." : "Delete duplicates"}
      onConfirm={(options) => {
        if (!pendingDelete || deleteMut.isPending) return;
        deleteMut.mutate({ ids: pendingDelete.ids, options });
      }}
      onCancel={() => setPendingDelete(null)}
      showDeleteFile
      showDeleteGenerated
    />
    </>
  );
}

function chooseRecommendedKeeper(group: Video[]) {
  return [...group].sort((left, right) => scoreVideo(right) - scoreVideo(left) || left.id - right.id)[0];
}

function scoreVideo(video: Video) {
  const file = video.files[0];
  if (!file) return 0;
  return (file.width ?? 0) * (file.height ?? 0) + Math.min(file.size ?? 0, 100_000_000_000) / 100_000;
}
