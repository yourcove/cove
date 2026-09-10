import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Film, Loader2 } from "lucide-react";
import { entityImages, videos } from "../api/client";
import type { FilterExpression, FindFilter, Video, VideoFile, VideoFilterCriteria } from "../api/types";
import { DetailListToolbar } from "./DetailListToolbar";
import { VIDEO_CRITERIA } from "./filterCriteriaCatalogs";
import { VIDEO_SORT_OPTIONS } from "./videoSortOptions";
import { FILTER_EXPRESSION_STATE_KEY } from "../utils/filterExpressionTree";
import { formatDuration, formatFileSize } from "./shared";

const buttonClass =
  "rounded border border-border px-3 py-2 text-sm hover:bg-surface disabled:cursor-not-allowed disabled:opacity-50";
const titleOf = (video: Video) => video.title || video.files[0]?.basename || `Video ${video.id}`;

export function MoveVideoFileDialog({
  file,
  sourceVideoId,
  onClose,
  onMoved,
}: {
  file: VideoFile;
  sourceVideoId: number;
  onClose: () => void;
  onMoved?: () => void;
}) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const queryClient = useQueryClient();
  const [filter, setFilter] = useState<FindFilter>({ page: 1, perPage: 20, sort: "updated_at", direction: "desc" });
  const [objectFilter, setObjectFilter] = useState<Record<string, unknown>>({});
  const [selected, setSelected] = useState<Video | null>(null);
  const { [FILTER_EXPRESSION_STATE_KEY]: expression, ...criteria } = objectFilter;
  const results = useQuery({
    queryKey: ["videos", "move-file-candidates", filter, objectFilter],
    queryFn: () =>
      videos.findFiltered({
        findFilter: filter,
        objectFilter: criteria as VideoFilterCriteria,
        filterExpression: expression as FilterExpression<VideoFilterCriteria> | undefined,
      }),
  });
  const move = useMutation({
    mutationFn: () => videos.assignFile(selected!.id, file.id),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["video", sourceVideoId] }),
        queryClient.invalidateQueries({ queryKey: ["video", selected!.id] }),
        queryClient.invalidateQueries({ queryKey: ["videos"] }),
      ]);
      onMoved?.();
      onClose();
    },
  });
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const dialog = dialogRef.current!;
    dialog.showModal();
    return () => {
      dialog.close();
      previous?.focus();
    };
  }, []);

  return (
    <dialog
      ref={dialogRef}
      aria-modal="true"
      aria-labelledby="move-file-title"
      onCancel={(event) => {
        event.preventDefault();
        if (!move.isPending && !dialogRef.current?.querySelector('[role="dialog"]')) onClose();
      }}
      className="fixed inset-0 m-auto max-h-[92dvh] w-[min(96vw,72rem)] max-w-none overflow-hidden rounded-xl border border-border bg-background p-0 text-foreground shadow-2xl backdrop:bg-black/70"
    >
      <div className="flex max-h-[92dvh] flex-col">
        <header className="shrink-0 border-b border-border p-5">
          <h2 id="move-file-title" className="text-xl font-semibold">
            Move file to another video
          </h2>
          <div className="mt-3 rounded-lg border border-border bg-card p-3">
            <p className="text-xs text-secondary">File being moved</p>
            <p className="break-all font-medium">{file.basename || file.path.split(/[\\/]/).pop()}</p>
            <p className="mt-1 break-all text-xs text-muted">{file.path}</p>
            <p className="mt-1 text-xs text-secondary">
              {formatDuration(file.duration)} · {formatFileSize(file.size)} · {file.width}×{file.height}
            </p>
          </div>
          <p className="mt-3 text-sm text-secondary">
            Search or filter videos, then select one destination. The file stays in its current location on disk.
          </p>
        </header>
        <div className="min-h-0 overflow-y-auto p-5">
          <fieldset disabled={move.isPending}>
            <legend className="sr-only">Destination video</legend>
            <DetailListToolbar
              filter={filter}
              onFilterChange={setFilter}
              totalCount={results.data?.totalCount ?? 0}
              sortOptions={VIDEO_SORT_OPTIONS}
              showSearch
              criteriaDefinitions={VIDEO_CRITERIA}
              objectFilter={objectFilter}
              onObjectFilterChange={(value) => {
                setObjectFilter(value);
                setFilter((current) => ({ ...current, page: 1 }));
              }}
            />
            {results.isPending ? (
              <p role="status" className="py-6">
                Loading videos…
              </p>
            ) : results.isError ? (
              <div role="alert" className="py-6">
                <p>Could not load videos. {results.error.message}</p>
                <button className={buttonClass} onClick={() => void results.refetch()}>
                  Retry
                </button>
              </div>
            ) : results.data.items.length === 0 ? (
              <p className="py-6">No videos match your search.</p>
            ) : (
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
                {results.data.items.map((video) => {
                  const current = video.id === sourceVideoId;
                  const clip = video.parentVideoId != null;
                  return (
                    <label
                      key={video.id}
                      className={`overflow-hidden rounded-lg border bg-card ${current || clip ? "opacity-50" : "cursor-pointer hover:border-accent"} ${selected?.id === video.id ? "border-accent ring-2 ring-accent" : "border-border"}`}
                    >
                      <div className="relative aspect-video bg-surface">
                        <Film
                          aria-hidden="true"
                          className="absolute left-1/2 top-1/2 h-8 w-8 -translate-x-1/2 -translate-y-1/2 text-muted"
                        />
                        <img
                          src={entityImages.videoCoverUrl(video.id, video.updatedAt, 480)}
                          alt=""
                          loading="lazy"
                          className="relative h-full w-full object-cover"
                          onError={(event) => {
                            event.currentTarget.style.visibility = "hidden";
                          }}
                        />
                      </div>
                      <div className="flex items-start gap-2 p-3">
                        <input
                          type="radio"
                          name="move-video-destination"
                          disabled={current || clip}
                          checked={selected?.id === video.id}
                          onChange={() => {
                            setSelected(video);
                            move.reset();
                          }}
                          className="mt-1 accent-accent"
                        />
                        <div className="min-w-0">
                          <p className="break-words text-sm font-medium">{titleOf(video)}</p>
                          <p className="text-xs text-secondary">
                            {[video.date, video.studioName].filter(Boolean).join(" · ")}
                          </p>
                          {current ? (
                            <p className="text-xs text-secondary">Current video</p>
                          ) : clip ? (
                            <p className="text-xs text-secondary">Clip — select its parent video instead</p>
                          ) : null}
                        </div>
                      </div>
                    </label>
                  );
                })}
              </div>
            )}
          </fieldset>
        </div>
        <footer className="shrink-0 border-t border-border p-5">
          {move.isError && (
            <p role="alert" className="mb-3 text-sm text-red-400">
              Could not move file. {move.error.message}
            </p>
          )}
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="min-w-0 break-words text-sm" aria-live="polite">
              {selected ? `Destination: ${titleOf(selected)}` : "Select a destination video"}
            </p>
            <div className="flex gap-2">
              <button className={buttonClass} disabled={move.isPending} onClick={onClose}>
                Cancel
              </button>
              <button
                className={`${buttonClass} inline-flex items-center gap-2 bg-accent text-white`}
                disabled={!selected || move.isPending}
                onClick={() => move.mutate()}
              >
                {move.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
                {move.isPending ? "Moving…" : "Move file"}
              </button>
            </div>
          </div>
        </footer>
      </div>
    </dialog>
  );
}
