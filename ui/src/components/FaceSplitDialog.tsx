import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Check, Fingerprint, Loader2, Scissors, UserX } from "lucide-react";
import { faces } from "../api/client";
import type { FaceHostTrack } from "../api/types";
import { formatDuration } from "./shared";
import { EditModal } from "./EditModal";

interface Props {
  open: boolean;
  faceId: number | null;
  faceTitle: string;
  hostType: "video" | "image";
  hostId: number;
  onClose: () => void;
  /** Called after a successful split so the caller can refresh its own face/detection queries. */
  onSplit?: (targetFaceId: number) => void;
  /** Offers "mark not present" as the better tool when every appearance is the wrong person. */
  onMarkNotPresent?: () => void;
}

/**
 * Pulls a second performer out of a face, within one video or image.
 *
 * A face can hold several separate tracked appearances on the same host. When two people who look alike
 * were merged into one face, some of those appearances are one person and the rest are the other — and
 * the whole-host "not present" action is too blunt to fix it.
 *
 * The provider groups the appearances by who it thinks they are, so the usual interaction is confirming
 * a proposed split rather than sorting clips by eye. Individual appearances stay togglable for when the
 * grouping gets it wrong.
 */
export function FaceSplitDialog({
  open,
  faceId,
  faceTitle,
  hostType,
  hostId,
  onClose,
  onSplit,
  onMarkNotPresent,
}: Props) {
  const queryClient = useQueryClient();
  const [selectedGroupKeys, setSelectedGroupKeys] = useState<string[]>([]);
  const [touched, setTouched] = useState(false);

  const {
    data: tracks,
    isLoading,
    error,
    refetch,
  } = useQuery({
    queryKey: ["face", faceId, "host-tracks", hostType, hostId],
    queryFn: () => faces.hostTracks(faceId!, { hostType, hostId }),
    enabled: open && faceId != null,
  });

  const splitMut = useMutation({
    mutationFn: () => faces.split(faceId!, { hostType, hostId, groupKeys: selectedGroupKeys }),
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: ["face"] });
      queryClient.invalidateQueries({ queryKey: [hostType, String(hostId)] });
      onSplit?.(result.targetFaceId);
      onClose();
    },
  });

  const available = useMemo(() => tracks ?? [], [tracks]);

  // The provider's proposed people, biggest first. Group 0 is the one the face mostly is; everything
  // after it is what a split would peel away.
  const groups = useMemo(() => {
    const byGroup = new Map<number, FaceHostTrack[]>();
    for (const track of available) {
      const key = track.suggestedGroup ?? 0;
      byGroup.set(key, [...(byGroup.get(key) ?? []), track]);
    }
    return [...byGroup.entries()].sort(([a], [b]) => a - b).map(([index, items]) => ({ index, items }));
  }, [available]);

  const hasProposal = groups.length > 1;

  // Reopening on another face must not carry the previous selection over. When the provider found more
  // than one person, start with its answer already filled in: everything outside the dominant group.
  useEffect(() => {
    setTouched(false);
    setSelectedGroupKeys([]);
    splitMut.reset();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [faceId, open]);

  useEffect(() => {
    if (touched || available.length === 0) return;
    setSelectedGroupKeys(
      groups.length > 1
        ? groups.filter((group) => group.index !== 0).flatMap((group) => group.items.map((item) => item.groupKey))
        : [],
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [groups, available.length, touched]);

  const selectedCount = selectedGroupKeys.length;
  const allSelected = available.length > 0 && selectedCount === available.length;
  const canSplit = selectedCount > 0 && !allSelected && !splitMut.isPending;

  const toggle = (groupKey: string) => {
    setTouched(true);
    setSelectedGroupKeys((current) =>
      current.includes(groupKey) ? current.filter((key) => key !== groupKey) : [...current, groupKey],
    );
  };

  const toggleGroup = (items: FaceHostTrack[]) => {
    setTouched(true);
    const keys = items.map((item) => item.groupKey);
    const allOn = keys.every((key) => selectedGroupKeys.includes(key));
    setSelectedGroupKeys((current) =>
      allOn ? current.filter((key) => !keys.includes(key)) : [...new Set([...current, ...keys])],
    );
  };

  const hostNoun = hostType === "video" ? "video" : "image";

  return (
    <EditModal
      title={`Separate people in "${faceTitle}"`}
      open={open}
      onClose={onClose}
      maxWidthClassName="sm:max-w-3xl"
    >
      <div className="space-y-4">
        <p className="text-sm text-secondary">
          {hasProposal
            ? `This face looks like ${groups.length} different people in this ${hostNoun}. The ones below are pre-selected as the odd ones out — check them, then separate them onto their own face.`
            : available.length > 1
              ? `This face appears ${available.length} times in this ${hostNoun} and they all look like the same person. Select any that are actually someone else.`
              : `Select the appearances that are a different person.`}
        </p>

        {error ? (
          <div role="alert" className="rounded-xl border border-red-500/40 bg-red-500/10 px-4 py-5 text-center">
            <p className="font-medium text-red-100">Could not load appearances</p>
            {/* The server's own reason, not a generic "try again" — this talks to a provider extension,
                so "what went wrong, and where" is the useful thing to show. */}
            <p className="mt-1 break-words text-sm text-red-200/80">{describeError(error)}</p>
            <button
              type="button"
              onClick={() => {
                void refetch();
              }}
              className="mt-4 rounded-lg border border-red-300/40 px-3 py-1.5 text-sm text-red-100 hover:bg-red-500/15"
            >
              Try again
            </button>
          </div>
        ) : isLoading ? (
          <div className="flex items-center justify-center gap-2 py-12 text-sm text-muted">
            <Loader2 className="h-4 w-4 animate-spin" />
            Loading appearances…
          </div>
        ) : available.length < 2 ? (
          <div className="space-y-3 rounded-2xl border border-border bg-card/40 p-5 text-center">
            <Fingerprint className="mx-auto h-8 w-8 text-muted" />
            <p className="text-sm text-secondary">
              This face appears only once in this {hostNoun}, so there is nothing to separate. If it is the wrong person
              here, mark it not present instead.
            </p>
            {onMarkNotPresent ? (
              <button
                type="button"
                onClick={() => {
                  onClose();
                  onMarkNotPresent();
                }}
                className="inline-flex items-center gap-2 rounded-lg border border-border px-4 py-2 text-sm text-foreground transition-colors hover:border-accent"
              >
                <UserX className="h-4 w-4" />
                Mark not present in this {hostNoun}
              </button>
            ) : null}
          </div>
        ) : (
          <div className="space-y-4">
            {groups.map((group) => {
              const keys = group.items.map((item) => item.groupKey);
              const allOn = keys.every((key) => selectedGroupKeys.includes(key));
              return (
                <section key={group.index} className="space-y-2">
                  {hasProposal ? (
                    <div className="flex items-center justify-between gap-3">
                      <h6 className="text-sm font-medium text-foreground">
                        {group.index === 0 ? "Mostly this person" : `Someone else (${group.index})`}
                        <span className="ml-2 text-xs font-normal text-muted">
                          {group.items.length} appearance{group.items.length === 1 ? "" : "s"}
                        </span>
                      </h6>
                      <button
                        type="button"
                        onClick={() => toggleGroup(group.items)}
                        className="rounded-lg border border-border px-2.5 py-1 text-xs text-secondary transition-colors hover:border-accent hover:text-accent"
                      >
                        {allOn ? "Deselect all" : "Select all"}
                      </button>
                    </div>
                  ) : null}
                  <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                    {group.items.map((track) => (
                      <TrackCard
                        key={track.groupKey}
                        track={track}
                        selected={selectedGroupKeys.includes(track.groupKey)}
                        onToggle={() => toggle(track.groupKey)}
                      />
                    ))}
                  </div>
                </section>
              );
            })}
          </div>
        )}

        {allSelected ? (
          <div className="flex items-start gap-2 rounded-xl border border-amber-500/30 bg-amber-500/5 px-3 py-2 text-sm text-amber-200">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
            <span>
              Every appearance is selected, which would leave this face with nothing here. Deselect at least one, or
              mark the face not present in this {hostNoun} instead.
            </span>
          </div>
        ) : null}

        {splitMut.error ? (
          <div className="flex items-start gap-2 rounded-xl border border-red-500/30 bg-red-500/5 px-3 py-2 text-sm text-red-200">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
            <span className="break-words">{describeError(splitMut.error)}</span>
          </div>
        ) : null}

        <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-4">
          <span className="text-xs text-muted">
            {selectedCount > 0
              ? `${selectedCount} of ${available.length} appearance${available.length === 1 ? "" : "s"} selected`
              : "Nothing selected yet"}
          </span>
          <div className="flex flex-wrap justify-end gap-2">
            <button
              type="button"
              onClick={onClose}
              disabled={splitMut.isPending}
              className="inline-flex items-center gap-2 rounded-lg border border-border px-4 py-2 text-sm text-foreground transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={() => splitMut.mutate()}
              disabled={!canSplit}
              className="inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
            >
              {splitMut.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Scissors className="h-4 w-4" />}
              {selectedCount > 1 ? `Separate ${selectedCount} appearances` : "Separate appearance"}
            </button>
          </div>
        </div>
      </div>
    </EditModal>
  );
}

/**
 * The API client throws `API Error <status>: <body>`, where the body is either a ProblemDetails document
 * or a plain error object. Pull the human-readable part out and keep the status, so a failure names its
 * own cause instead of the generic "the server might be down".
 */
function describeError(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error ?? "");
  const match = /^API Error (\d+): ?([\s\S]*)$/.exec(message);
  if (!match) {
    return message || "The request failed.";
  }

  const [, status, body] = match;
  let detail = body.trim();
  try {
    const parsed = JSON.parse(detail);
    detail = parsed?.detail || parsed?.error || parsed?.title || detail;
  } catch {
    // Not JSON — show the raw body.
  }

  if (status === "401" || status === "403") {
    return "You do not have permission to view this face's appearances.";
  }
  if (status === "501") {
    return "No installed extension provides face occurrence editing.";
  }
  if (status === "404") {
    return "That face no longer exists.";
  }
  return detail ? `${detail} (HTTP ${status})` : `The request failed (HTTP ${status}).`;
}

function TrackCard({ track, selected, onToggle }: { track: FaceHostTrack; selected: boolean; onToggle: () => void }) {
  // Tight crop: the whole point here is telling one face apart from the others in the same shot, and
  // the default portrait context (1.8x the box) routinely pulls a neighbour's face into frame.
  const thumbnailUrl =
    track.representativeDetectionId != null
      ? faces.detectionCropUrl(track.representativeDetectionId, 320, 1.15)
      : undefined;
  const start = track.firstSeenSeconds;
  const end = track.lastSeenSeconds;
  const spanSeconds = start != null && end != null ? Math.max(0, end - start) : null;
  const when = start != null ? formatDuration(start) : null;
  const howLong = spanSeconds != null && spanSeconds >= 1 ? `${Math.round(spanSeconds)}s on screen` : null;

  return (
    <button
      type="button"
      onClick={onToggle}
      aria-pressed={selected}
      className={`group relative overflow-hidden rounded-2xl border text-left transition-colors ${
        selected ? "border-accent bg-accent/10" : "border-border bg-card/50 hover:border-accent/50"
      }`}
    >
      <div className="relative aspect-square bg-surface/70">
        {thumbnailUrl ? (
          <img src={thumbnailUrl} alt="" className="h-full w-full object-cover" loading="lazy" />
        ) : (
          <div className="flex h-full w-full items-center justify-center text-muted">
            <Fingerprint className="h-8 w-8" />
          </div>
        )}
        <span
          className={`absolute right-2 top-2 inline-flex h-6 w-6 items-center justify-center rounded-full border transition-colors ${
            selected
              ? "border-accent bg-accent text-white"
              : "border-white/30 bg-black/50 text-transparent group-hover:text-white/60"
          }`}
        >
          <Check className="h-3.5 w-3.5" />
        </span>
      </div>
      <div className="space-y-1 p-3">
        {/* When and for how long — the two things that identify a clip to a person watching. "Samples"
            was an internal pipeline count that matched nothing else shown in the app. */}
        <div className="text-sm font-medium text-foreground">{when ? `At ${when}` : "In this image"}</div>
        <div className="text-xs text-secondary">
          {[howLong, `${track.detectionCount} detection${track.detectionCount === 1 ? "" : "s"}`]
            .filter(Boolean)
            .join(" · ")}
        </div>
      </div>
    </button>
  );
}
