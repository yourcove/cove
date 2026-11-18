import { useEffect, useMemo, useRef, useState } from "react";
import { LoadedImage } from "./LoadedImage";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, Fingerprint, ImagePlus, Search, Star, Trash2, UserCheck, UserX, X } from "lucide-react";
import { faces, performers } from "../api/client";
import type { Detection, Face, FaceAppearance, FaceHostTrack, FaceImageSource } from "../api/types";
import type { Route } from "../router/location";
import { createRouteLinkProps } from "./cardNavigation";
import { ConfirmDialog } from "./ConfirmDialog";
import { compareFaceSampleDetections } from "./faceComparisonImages";
import { formatDuration } from "./shared";

type AppearanceHost = Pick<FaceAppearance, "hostType" | "hostId" | "title">;

interface Props {
  face: Face;
  faceTitle: string;
  appearances: FaceAppearance[];
  detections: Detection[];
  canWrite: boolean;
  /** Whether an installed extension can move a face's appearances to another face. */
  canEditOccurrences: boolean;
  onNavigate: (r: any) => void;
  /** Called after the cover or the face's appearances changed, so the page can reload. */
  onChanged: (hostTypes: FaceAppearance["hostType"][]) => void;
  /** Called with what a move did. The page shows it, since a move can leave nothing here to show it next to. */
  onMoved: (outcomes: FaceMoveOutcome[]) => void;
}

/** What happened to the selected appearances of one video or image. */
export interface FaceMoveOutcome {
  host: AppearanceHost;
  targetFaceId?: number;
  createdNewFace?: boolean;
  linkedPerformerName?: string;
  /** Nothing was left to move: marking an earlier video not present had already taken these along. */
  alreadyMoved?: boolean;
  error?: string;
}

interface MoveRequest {
  host: AppearanceHost;
  groupKeys: string[];
  /** Every appearance of the face there is selected, so the face is not in that video or image at all. */
  wholeHost: boolean;
}

const ACTION_BUTTON_CLASS =
  "inline-flex items-center gap-1.5 rounded-lg border border-border px-2.5 py-1.5 text-xs text-foreground transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50";

const hostKeyOf = (host: Pick<FaceAppearance, "hostType" | "hostId">) => `${host.hostType}:${host.hostId}`;

const hostTracksQueryKey = (faceId: number, host: Pick<FaceAppearance, "hostType" | "hostId">) =>
  ["face", faceId, "host-tracks", host.hostType, host.hostId] as const;

/**
 * Whether the element has come near the viewport yet. A face can appear in hundreds of videos, and each section
 * asks the provider for its appearances and has frames cut for their pictures, so that waits until it is needed.
 */
function useHasBeenNearViewport<T extends Element>() {
  const ref = useRef<T>(null);
  const [seen, setSeen] = useState(() => typeof IntersectionObserver === "undefined");
  useEffect(() => {
    const element = ref.current;
    if (seen || !element) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) setSeen(true);
      },
      { rootMargin: "400px 0px" },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [seen]);
  return { ref, seen };
}

/** The state of a face's stored cover and where it came from. Shared with the page's carousel. */
export function useFaceImageSources(faceId: number, enabled = true) {
  const { data: imageSources } = useQuery({
    queryKey: ["face", faceId, "image-sources", "cover"],
    queryFn: () => faces.imageSources(faceId, []),
    enabled,
  });
  return { imageSources };
}

/** The server's own explanation from a failed request, without the transport wrapping. */
function apiErrorMessage(error: unknown) {
  const message = error instanceof Error ? error.message : String(error);
  const match = /^API Error \d+: ?([\s\S]*)$/.exec(message);
  if (!match) return message || "The request failed.";
  try {
    const parsed = JSON.parse(match[1]);
    return parsed?.error || parsed?.detail || parsed?.title || match[1];
  } catch {
    return match[1] || message;
  }
}

/**
 * Moves the selected appearances off the face, one video or image at a time, and optionally links the faces
 * they land on to a performer. Only faces created by the move are linked, after being combined into one:
 * appearances that joined a face which already existed leave that face alone.
 */
export async function moveFaceAppearances(
  faceId: number,
  requests: MoveRequest[],
  performer?: { id: number; name: string },
): Promise<FaceMoveOutcome[]> {
  const outcomes: FaceMoveOutcome[] = [];
  for (const request of requests) {
    const host = { hostType: request.host.hostType, hostId: request.host.hostId };
    try {
      let result;
      if (request.wholeHost) {
        result = await faces.markNotPresent(faceId, host);
      } else {
        try {
          result = await faces.split(faceId, { ...host, groupKeys: request.groupKeys });
        } catch (error) {
          // The selection turned out to be everything the face has there.
          if (!/mark it not present/i.test(apiErrorMessage(error))) throw error;
          result = await faces.markNotPresent(faceId, host);
        }
      }
      outcomes.push({
        host: request.host,
        targetFaceId: result.targetFaceId ?? undefined,
        createdNewFace: result.createdNewFace,
      });
    } catch (error) {
      const message = apiErrorMessage(error);
      outcomes.push(
        /not present on the specified host/i.test(message)
          ? { host: request.host, alreadyMoved: true }
          : { host: request.host, error: message },
      );
    }
  }

  if (performer) {
    const created = outcomes.filter((outcome) => outcome.createdNewFace && outcome.targetFaceId != null);
    const [keptFaceId, ...otherFaceIds] = [...new Set(created.map((outcome) => outcome.targetFaceId!))];
    const note = (faceIdToNote: number, change: (outcome: FaceMoveOutcome) => void) =>
      outcomes.filter((outcome) => outcome.targetFaceId === faceIdToNote).forEach(change);
    // Every new face is linked before any is combined: a merged face keeps its own detections and segments, and
    // those are named after the performer of the face they refer to.
    for (const createdFaceId of [keptFaceId, ...otherFaceIds].filter((id) => id != null)) {
      try {
        await faces.link(createdFaceId, { performerId: performer.id });
        note(createdFaceId, (outcome) => {
          outcome.linkedPerformerName = performer.name;
        });
      } catch (error) {
        note(createdFaceId, (outcome) => {
          outcome.error = `Moved, but linking failed: ${apiErrorMessage(error)}`;
        });
      }
    }
    // Naming one person for all of them says the new faces are the same person, so they become one face. A face
    // that already existed is someone's own cluster and is neither folded in nor relabelled.
    for (const otherFaceId of otherFaceIds) {
      try {
        await faces.mergeInto(otherFaceId, { targetFaceId: keptFaceId });
        note(otherFaceId, (outcome) => {
          outcome.targetFaceId = keptFaceId;
        });
      } catch (error) {
        note(otherFaceId, (outcome) => {
          outcome.error = `Moved, but it could not be combined with the other new face: ${apiErrorMessage(error)}`;
        });
      }
    }
  }
  return outcomes;
}

/**
 * A face's appearances, broken down per video or image into the separate stretches the face was tracked in. Each
 * shows the face as it looks there, so a face that mixes several people can be taken apart where it is seen:
 * select the appearances that are someone else and move them, optionally saying who they are.
 */
export function FaceAppearanceBreakdown({
  face,
  faceTitle,
  appearances,
  detections,
  canWrite,
  canEditOccurrences,
  onNavigate,
  onChanged,
  onMoved,
}: Props) {
  const queryClient = useQueryClient();
  const canMove = canWrite && canEditOccurrences;
  const [selection, setSelection] = useState<Record<string, { host: AppearanceHost; groupKeys: string[] }>>({});
  const [pendingMove, setPendingMove] = useState<{ performer?: { id: number; name: string } } | null>(null);
  const [choosingPerformer, setChoosingPerformer] = useState(false);
  const { imageSources } = useFaceImageSources(face.id);
  const cover = imageSources?.cover;

  // Kept with its video, so appearances ticked on another page of the list are still counted and moved.
  const selected = Object.values(selection).filter((item) => item.groupKeys.length > 0);
  const selectedCount = selected.reduce((total, item) => total + item.groupKeys.length, 0);

  const moveMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (performer?: { id: number; name: string }) => {
      const requests = selected.map(({ host, groupKeys }): MoveRequest => {
        const tracks = queryClient.getQueryData<FaceHostTrack[]>(hostTracksQueryKey(face.id, host));
        // Judged against the appearances as they are now, not by how many were ticked earlier.
        // An extension that reports no appearances here says nothing about whether these are all of them.
        const wholeHost =
          tracks != null && tracks.length > 0 && tracks.every((track) => groupKeys.includes(track.groupKey));
        return { host, groupKeys, wholeHost };
      });
      return moveFaceAppearances(face.id, requests, performer);
    },
    onSuccess: (result) => {
      onMoved(result);
      // What could not be moved stays ticked, to be tried again.
      const failed = new Set(
        result.filter((outcome) => outcome.error && outcome.targetFaceId == null).map((o) => hostKeyOf(o.host)),
      );
      setSelection((current) => Object.fromEntries(Object.entries(current).filter(([key]) => failed.has(key))));
      setPendingMove(null);
      setChoosingPerformer(false);
      if (result.some((outcome) => outcome.linkedPerformerName)) {
        queryClient.invalidateQueries({ queryKey: ["performers"] });
        queryClient.invalidateQueries({ queryKey: ["performer"] });
      }
      onChanged([...new Set(result.map((outcome) => outcome.host.hostType))]);
    },
  });

  const toggleTrack = (host: AppearanceHost, groupKey: string) =>
    setSelection((current) => {
      const key = hostKeyOf(host);
      const groupKeys = current[key]?.groupKeys ?? [];
      return {
        ...current,
        [key]: {
          host,
          groupKeys: groupKeys.includes(groupKey)
            ? groupKeys.filter((item) => item !== groupKey)
            : [...groupKeys, groupKey],
        },
      };
    });
  const setHostSelection = (host: AppearanceHost, groupKeys: string[]) =>
    setSelection((current) => ({ ...current, [hostKeyOf(host)]: { host, groupKeys } }));

  const selectionSummary = `${selectedCount} appearance${selectedCount === 1 ? "" : "s"} in ${selected.length} ${
    selected.length === 1 ? "place" : "places"
  }`;

  return (
    <div className="space-y-4">
      <FaceCoverRow
        face={face}
        cover={cover}
        coverSource={
          cover?.detectionId != null
            ? imageSources?.sources.find((source) => source.detectionId === cover.detectionId)
            : undefined
        }
        canWrite={canWrite}
        onNavigate={onNavigate}
        onChanged={() => onChanged([])}
      />

      {appearances.map((appearance) => (
        <HostSection
          key={hostKeyOf(appearance)}
          face={face}
          appearance={appearance}
          detections={detections}
          coverDetectionId={cover?.sourceOnFace ? cover.detectionId : null}
          canWrite={canWrite}
          canMove={canMove}
          useProviderTracks={canEditOccurrences}
          selected={selection[hostKeyOf(appearance)]?.groupKeys ?? []}
          onToggle={(groupKey) => toggleTrack(appearance, groupKey)}
          onSelect={(groupKeys) => setHostSelection(appearance, groupKeys)}
          onNavigate={onNavigate}
          onCoverChanged={() => onChanged([])}
        />
      ))}

      {canMove && selectedCount > 0 ? (
        <div
          className="sticky bottom-3 z-10 rounded-xl border border-accent/60 bg-card p-3 shadow-lg"
          role="region"
          aria-label="Selected appearances"
        >
          <div className="flex flex-wrap items-center gap-2">
            <p className="mr-auto text-sm text-foreground" aria-live="polite">
              {selectionSummary} selected
            </p>
            <button
              type="button"
              className={ACTION_BUTTON_CLASS}
              aria-expanded={choosingPerformer}
              onClick={() => setChoosingPerformer((open) => !open)}
            >
              <UserCheck className="h-3.5 w-3.5" />
              This is...
            </button>
            <button type="button" className={ACTION_BUTTON_CLASS} onClick={() => setPendingMove({})}>
              <UserX className="h-3.5 w-3.5" />
              Not this person
            </button>
            <button type="button" className={ACTION_BUTTON_CLASS} onClick={() => setSelection({})}>
              <X className="h-3.5 w-3.5" />
              Clear
            </button>
          </div>
          {choosingPerformer ? <PerformerChoice onChoose={(performer) => setPendingMove({ performer })} /> : null}
        </div>
      ) : null}

      <ConfirmDialog
        open={pendingMove != null}
        title={
          pendingMove?.performer
            ? `Move ${selectionSummary} to ${pendingMove.performer.name}?`
            : `Move ${selectionSummary} off ${faceTitle}?`
        }
        message={
          pendingMove?.performer
            ? `They are taken off ${faceTitle}. Those that become new faces are combined into one face linked to ${pendingMove.performer.name}. Those that match a face which already exists join it, and it is left as it is. Where every appearance in a video or image is selected, ${faceTitle} is kept from returning there, and appearances elsewhere that match them move too.`
            : `They are taken off ${faceTitle} and put on the face they match, or on a new face. Where every appearance in a video or image is selected, ${faceTitle} is kept from returning there, and appearances elsewhere that match them move too.`
        }
        confirmLabel="Move"
        destructive={false}
        isPending={moveMutation.isPending}
        errorMessage={moveMutation.error ? String(moveMutation.error.message ?? moveMutation.error) : null}
        onCancel={() => setPendingMove(null)}
        onConfirm={() => moveMutation.mutate(pendingMove?.performer)}
      />
    </div>
  );
}

function FaceCoverRow({
  face,
  cover,
  coverSource,
  canWrite,
  onNavigate,
  onChanged,
}: {
  face: Face;
  cover?: { hasStoredCover: boolean; detectionId?: number | null; sourceOnFace: boolean };
  coverSource?: FaceImageSource;
  canWrite: boolean;
  onNavigate: (r: any) => void;
  onChanged: () => void;
}) {
  const fileInputRef = useRef<HTMLInputElement>(null);
  const meta = { suppressGlobalError: true };
  const removeMutation = useMutation({ meta, mutationFn: () => faces.deleteCover(face.id), onSuccess: onChanged });
  const uploadMutation = useMutation({
    meta,
    mutationFn: (file: File) => faces.uploadCover(face.id, file),
    onSuccess: onChanged,
  });
  const [confirmingRemoval, setConfirmingRemoval] = useState(false);
  const error = removeMutation.error ?? uploadMutation.error;
  if (!cover || (!cover.hasStoredCover && !canWrite)) {
    return null;
  }

  return (
    <section className="rounded-xl border border-border bg-card/70 p-3" aria-label="Cover">
      <div className="flex flex-wrap items-center gap-3">
        {cover.hasStoredCover && face.coverImageUrl ? (
          <LoadedImage src={face.coverImageUrl} alt="Cover" className="h-12 w-12 shrink-0 rounded-lg object-cover" />
        ) : null}
        <div className="mr-auto min-w-0 text-sm">
          <span className="font-semibold text-foreground">Cover</span>{" "}
          {!cover.hasStoredCover ? (
            <span className="text-secondary">
              None stored, so the best current detection is shown. Choose one from the appearances below.
            </span>
          ) : coverSource ? (
            <>
              <span className="text-secondary">from </span>
              <SourceLink source={coverSource} onNavigate={onNavigate} inline />
            </>
          ) : cover.detectionId != null ? (
            <span className="text-secondary">
              {cover.sourceOnFace
                ? "taken from a video or image you cannot open."
                : "taken from an appearance that is no longer part of this face."}
            </span>
          ) : (
            <span className="text-secondary">
              source not recorded, so it may not show anyone this face still contains.
            </span>
          )}
        </div>
        {canWrite ? (
          <>
            <input
              ref={fileInputRef}
              type="file"
              accept="image/*"
              className="hidden"
              aria-label="Upload cover image"
              onChange={(event) => {
                const file = event.target.files?.[0];
                event.target.value = "";
                if (file) {
                  removeMutation.reset();
                  uploadMutation.mutate(file);
                }
              }}
            />
            <button
              type="button"
              className={ACTION_BUTTON_CLASS}
              disabled={uploadMutation.isPending || removeMutation.isPending}
              onClick={() => fileInputRef.current?.click()}
            >
              <ImagePlus className="h-3.5 w-3.5" />
              {uploadMutation.isPending ? "Uploading..." : "Upload cover"}
            </button>
            {cover.hasStoredCover ? (
              <button
                type="button"
                className={ACTION_BUTTON_CLASS}
                disabled={uploadMutation.isPending || removeMutation.isPending}
                title="Remove the stored cover and show the best current detection instead"
                onClick={() => setConfirmingRemoval(true)}
              >
                <Trash2 className="h-3.5 w-3.5" />
                {removeMutation.isPending ? "Removing..." : "Remove cover"}
              </button>
            ) : null}
          </>
        ) : null}
      </div>
      <ConfirmDialog
        open={confirmingRemoval}
        title="Remove this cover?"
        message="The stored cover image is deleted and the face is shown by its best current detection. An uploaded image cannot be brought back."
        confirmLabel="Remove cover"
        onCancel={() => setConfirmingRemoval(false)}
        onConfirm={() => {
          setConfirmingRemoval(false);
          uploadMutation.reset();
          removeMutation.mutate();
        }}
      />
      {error ? (
        <p
          className="mt-2 rounded-lg border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm text-red-200"
          role="alert"
        >
          {String(error.message ?? "The cover could not be changed.")}
        </p>
      ) : null}
    </section>
  );
}

function HostSection({
  face,
  appearance,
  detections,
  coverDetectionId,
  canWrite,
  canMove,
  useProviderTracks,
  selected,
  onToggle,
  onSelect,
  onNavigate,
  onCoverChanged,
}: {
  face: Face;
  appearance: FaceAppearance;
  detections: Detection[];
  coverDetectionId?: number | null;
  canWrite: boolean;
  canMove: boolean;
  useProviderTracks: boolean;
  selected: string[];
  onToggle: (groupKey: string) => void;
  onSelect: (groupKeys: string[]) => void;
  onNavigate: (r: any) => void;
  onCoverChanged: () => void;
}) {
  const { ref: sectionRef, seen } = useHasBeenNearViewport<HTMLElement>();
  const {
    data: providerTracks,
    isLoading,
    error,
  } = useQuery({
    queryKey: hostTracksQueryKey(face.id, appearance),
    queryFn: () => faces.hostTracks(face.id, { hostType: appearance.hostType, hostId: appearance.hostId }),
    enabled: useProviderTracks && seen,
  });
  // Without an extension that reports tracked appearances, the face's own detections are grouped instead.
  const fallbackTracks = useMemo(
    () => (seen ? tracksFromDetections(detections, appearance) : []),
    [appearance, detections, seen],
  );
  // The provider reports an appearance that came with a merged face without its picture; the face's own
  // detections still have one.
  const fallbackDetectionByGroupKey = useMemo(
    () => new Map(fallbackTracks.map((track) => [track.groupKey, track.representativeDetectionId])),
    [fallbackTracks],
  );
  const tracks = useMemo(
    () =>
      // A provider that reports nothing for a place the face is listed in still leaves its detections to show.
      (useProviderTracks && (providerTracks?.length ?? 0) > 0
        ? providerTracks!.map((track) => ({
            ...track,
            representativeDetectionId:
              track.representativeDetectionId ?? fallbackDetectionByGroupKey.get(track.groupKey),
          }))
        : useProviderTracks && providerTracks == null
          ? []
          : [...fallbackTracks]
      ).sort(
        (left, right) =>
          (left.representativeFrameSeconds ?? left.firstSeenSeconds ?? 0) -
          (right.representativeFrameSeconds ?? right.firstSeenSeconds ?? 0),
      ),
    [fallbackDetectionByGroupKey, fallbackTracks, providerTracks, useProviderTracks],
  );
  // A long section starts as a sample: each picture of a video appearance is a frame decoded from the video.
  const [showAll, setShowAll] = useState(false);
  const visibleTracks = useMemo(
    () => (showAll ? tracks : spreadSample(tracks, COLLAPSED_APPEARANCE_LIMIT)),
    [showAll, tracks],
  );
  const hiddenCount = tracks.length - visibleTracks.length;
  // The endpoint describes at most 50 detections at once; later appearances link to the video at their time.
  const detectionIds = useMemo(
    () =>
      visibleTracks
        .map((track) => track.representativeDetectionId)
        .filter((id): id is number => id != null)
        .slice(0, 50),
    [visibleTracks],
  );
  const { data: sources } = useQuery({
    queryKey: ["face", face.id, "image-sources", detectionIds],
    queryFn: () => faces.imageSources(face.id, detectionIds),
    enabled: detectionIds.length > 0,
    placeholderData: keepPreviousData,
  });
  const sourceByDetection = useMemo(
    () => new Map((sources?.sources ?? []).map((source) => [source.detectionId, source])),
    [sources],
  );
  const chooseCoverMutation = useMutation({
    meta: { suppressGlobalError: true },
    mutationFn: (detectionId: number) => faces.setCoverFromDetection(face.id, detectionId),
    onSuccess: onCoverChanged,
  });

  const hostNoun = appearance.hostType === "image" ? "image" : "video";
  const title = appearance.title || `${hostNoun} #${appearance.hostId}`;
  const hostRoute = { page: appearance.hostType, id: appearance.hostId };
  const allSelected = tracks.length > 0 && selected.length >= tracks.length;

  return (
    <section ref={sectionRef} className="rounded-xl border border-border bg-card/60 p-3" aria-label={title}>
      <div className="flex flex-wrap items-center gap-3">
        <div className="h-12 w-20 shrink-0 overflow-hidden rounded bg-surface">
          {appearance.thumbnailUrl ? (
            <LoadedImage src={appearance.thumbnailUrl} alt="" className="h-full w-full object-cover" loading="lazy" />
          ) : null}
        </div>
        <div className="mr-auto min-w-0">
          <a
            {...createRouteLinkProps<HTMLAnchorElement>(hostRoute as Route, () => onNavigate(hostRoute))}
            className="block truncate text-sm font-medium text-accent hover:underline"
          >
            {title}
          </a>
          <p className="mt-0.5 text-xs text-muted">
            {hostNoun}
            {tracks.length > 0 ? ` · ${tracks.length} appearance${tracks.length === 1 ? "" : "s"}` : ""} ·{" "}
            {appearance.frameSampleCount} samples
          </p>
        </div>
        {canMove && tracks.length > 0 ? (
          <button
            type="button"
            className={ACTION_BUTTON_CLASS}
            onClick={() => onSelect(allSelected ? [] : tracks.map((track) => track.groupKey))}
          >
            {allSelected ? "Select none" : hiddenCount > 0 ? `Select all ${tracks.length}` : "Select all"}
          </button>
        ) : null}
      </div>

      {isLoading ? <p className="mt-3 text-sm text-secondary">Loading appearances...</p> : null}
      {error ? (
        <p className="mt-3 text-sm text-secondary" role="alert">
          The appearances in this {hostNoun} could not be loaded.
        </p>
      ) : null}
      {tracks.length > 0 ? (
        <ul className="mt-3 grid grid-cols-[repeat(auto-fill,minmax(7.5rem,1fr))] gap-2">
          {visibleTracks.map((track) => {
            const detectionId = track.representativeDetectionId;
            const source = detectionId != null ? sourceByDetection.get(detectionId) : undefined;
            const seconds = track.representativeFrameSeconds ?? track.firstSeenSeconds;
            const when = appearance.hostType === "video" && seconds != null ? formatDuration(seconds) : null;
            const described = when ? `${title} at ${when}` : title;
            const isSelected = selected.includes(track.groupKey);
            const isCover = detectionId != null && detectionId === coverDetectionId;
            const image =
              detectionId != null ? (
                // Tight crop: the point is telling one face from the others in the same shot.
                <LoadedImage
                  src={faces.detectionCropUrl(detectionId, 320, 1.15)}
                  alt={`Face in ${described}`}
                  className="h-full w-full object-cover"
                  loading="lazy"
                />
              ) : (
                <div className="flex h-full w-full items-center justify-center text-muted">
                  <Fingerprint className="h-8 w-8" />
                </div>
              );
            return (
              <li
                key={track.groupKey}
                className={`overflow-hidden rounded-xl border ${
                  isSelected ? "border-accent bg-accent/10" : "border-border bg-surface/60"
                }`}
              >
                {canMove ? (
                  <button
                    type="button"
                    onClick={() => onToggle(track.groupKey)}
                    aria-pressed={isSelected}
                    aria-label={`Select the appearance in ${described}`}
                    className="group relative block aspect-square w-full bg-surface/70"
                  >
                    {image}
                    <span
                      className={`absolute right-1.5 top-1.5 inline-flex h-6 w-6 items-center justify-center rounded-full border ${
                        isSelected
                          ? "border-accent bg-accent text-white"
                          : "border-white/30 bg-black/50 text-transparent group-hover:text-white/60 group-focus-visible:text-white/60"
                      }`}
                    >
                      <Check className="h-3.5 w-3.5" />
                    </span>
                  </button>
                ) : (
                  <div className="aspect-square w-full bg-surface/70">{image}</div>
                )}
                <div className="flex items-center justify-between gap-1 px-2 py-1.5 text-xs">
                  {source ? (
                    <SourceLink source={source} onNavigate={onNavigate} label={when ?? "Open"} />
                  ) : when ? (
                    <a
                      {...createRouteLinkProps<HTMLAnchorElement>({ ...hostRoute, seekTo: seconds } as Route, () =>
                        onNavigate({ ...hostRoute, seekTo: seconds }),
                      )}
                      className="text-accent hover:underline"
                    >
                      {when}
                    </a>
                  ) : (
                    <span />
                  )}
                  {isCover ? (
                    <span className="font-semibold uppercase tracking-wide text-accent">Cover</span>
                  ) : canWrite && detectionId != null ? (
                    <button
                      type="button"
                      className="text-secondary hover:text-accent disabled:opacity-50"
                      disabled={chooseCoverMutation.isPending}
                      onClick={() => chooseCoverMutation.mutate(detectionId)}
                      aria-label={`Use as cover: ${described}`}
                      title="Use as cover"
                    >
                      <Star className="h-3.5 w-3.5" />
                    </button>
                  ) : null}
                </div>
              </li>
            );
          })}
        </ul>
      ) : null}
      {tracks.length > COLLAPSED_APPEARANCE_LIMIT ? (
        <div className="mt-3 flex flex-wrap items-center gap-3 text-xs text-muted">
          {hiddenCount > 0 ? (
            <span>
              Showing {visibleTracks.length} of {tracks.length} appearances, spread across the {hostNoun}.
            </span>
          ) : null}
          <button
            type="button"
            className={ACTION_BUTTON_CLASS}
            aria-expanded={showAll}
            onClick={() => setShowAll((value) => !value)}
          >
            {showAll ? "Show fewer" : `Show all ${tracks.length}`}
          </button>
        </div>
      ) : null}
      {chooseCoverMutation.error ? (
        <p
          className="mt-2 rounded-lg border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm text-red-200"
          role="alert"
        >
          {String(chooseCoverMutation.error.message ?? "The cover could not be changed.")}
        </p>
      ) : null}
    </section>
  );
}

/** How many appearances a video or image shows before the rest are asked for. */
const COLLAPSED_APPEARANCE_LIMIT = 12;

/** Up to `limit` items in their order, evenly spread from the first to the last. */
function spreadSample<T>(items: T[], limit: number) {
  if (items.length <= limit) return items;
  return Array.from({ length: limit }, (_, index) => items[Math.round((index * (items.length - 1)) / (limit - 1))]);
}

function PerformerChoice({ onChoose }: { onChoose: (performer: { id: number; name: string }) => void }) {
  const [search, setSearch] = useState("");
  const term = search.trim();
  const { data, isLoading, isPlaceholderData } = useQuery({
    queryKey: ["performers", "face-appearance-move", term],
    queryFn: () => performers.find({ q: term, page: 1, perPage: 6 }),
    enabled: term.length >= 2,
    // Keep the last results on screen while the next keystroke searches, instead of flashing to a loading
    // line on every key; they are disabled meanwhile because they answer the previous text.
    placeholderData: keepPreviousData,
  });
  const matches = data?.items ?? [];

  return (
    <div className="mt-3 space-y-2">
      <div className="relative">
        <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted" />
        <input
          type="text"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder="Who is this? Search performers"
          aria-label="Search performers"
          autoFocus
          className="w-full rounded-lg border border-border bg-input py-2 pl-9 pr-3 text-sm text-foreground outline-none focus:border-accent"
        />
      </div>
      {/* An empty earlier answer is not kept: "no matches" must not stand for text not searched yet. */}
      {term.length < 2 ? null : isLoading || (isPlaceholderData && matches.length === 0) ? (
        <p className="text-sm text-secondary">Searching performers...</p>
      ) : matches.length === 0 ? (
        <p className="text-sm text-secondary">No performers matched that search.</p>
      ) : (
        <div className="flex flex-wrap gap-2" aria-busy={isPlaceholderData || undefined}>
          {matches.map((performer) => (
            <button
              key={performer.id}
              type="button"
              disabled={isPlaceholderData}
              className={ACTION_BUTTON_CLASS}
              onClick={() => onChoose({ id: performer.id, name: performer.name })}
            >
              {performer.name}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export function MoveOutcomeList({
  outcomes,
  onNavigate,
  onDismiss,
}: {
  outcomes: FaceMoveOutcome[];
  onNavigate: (r: any) => void;
  onDismiss: () => void;
}) {
  const sectionRef = useRef<HTMLElement>(null);
  // The move is confirmed from the bottom of a long list; bring its result into view.
  useEffect(() => {
    sectionRef.current?.scrollIntoView?.({ block: "nearest" });
  }, [outcomes]);
  return (
    <section
      ref={sectionRef}
      className="rounded-xl border border-border bg-card/70 p-3"
      aria-label="Moved appearances"
      role="status"
    >
      <div className="flex items-start justify-between gap-3">
        <h3 className="text-sm font-semibold text-foreground">Moved appearances</h3>
        <button type="button" className={ACTION_BUTTON_CLASS} onClick={onDismiss}>
          Dismiss
        </button>
      </div>
      <ul className="mt-2 space-y-1 text-sm text-secondary">
        {outcomes.map((outcome) => {
          const route = { page: "face", id: outcome.targetFaceId };
          return (
            <li key={hostKeyOf(outcome.host)}>
              <span className="text-foreground">
                {outcome.host.title || `${outcome.host.hostType} #${outcome.host.hostId}`}
              </span>
              {": "}
              {outcome.targetFaceId != null ? (
                <>
                  {outcome.createdNewFace ? "moved to a " : "joined the "}
                  <a
                    {...createRouteLinkProps<HTMLAnchorElement>(route as Route, () => onNavigate(route))}
                    className="text-accent hover:underline"
                  >
                    {outcome.createdNewFace ? "new face" : "existing face"}
                  </a>
                  {outcome.linkedPerformerName
                    ? `, linked to ${outcome.linkedPerformerName}`
                    : outcome.createdNewFace
                      ? ""
                      : ", which was left as it is"}
                  {outcome.error ? `. ${outcome.error}` : "."}
                </>
              ) : outcome.alreadyMoved ? (
                "already moved along with the appearances of another video."
              ) : (
                <span className="text-red-200">{outcome.error ?? "nothing was moved."}</span>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

function SourceLink({
  source,
  onNavigate,
  label,
  inline = false,
}: {
  source: FaceImageSource;
  onNavigate: (r: any) => void;
  label?: string;
  inline?: boolean;
}) {
  const hasTime = source.hostType === "video" && source.observedAtSec != null;
  const route =
    source.segmentId != null
      ? { page: "segment", id: source.segmentId }
      : hasTime
        ? { page: "video", id: source.hostId, seekTo: source.observedAtSec }
        : { page: source.hostType, id: source.hostId };
  const described = hasTime ? `${source.hostTitle} at ${formatDuration(source.observedAtSec ?? 0)}` : source.hostTitle;
  return (
    <a
      {...createRouteLinkProps<HTMLAnchorElement>(route as Route, () => onNavigate(route))}
      className={`${inline ? "" : "truncate "}text-accent hover:underline`}
      title={source.segmentId != null ? "Open this face segment" : `Open this ${source.hostType}`}
      aria-label={label ? `Open ${described}` : undefined}
    >
      {label ?? described}
    </a>
  );
}

/** One entry per tracked run of the face on a host, built from its detections when no extension reports tracks. */
function tracksFromDetections(detections: Detection[], host: Pick<FaceAppearance, "hostType" | "hostId">) {
  const tracks = new Map<string, FaceHostTrack>();
  const onHost = detections.filter(
    (detection) => String(detection.hostType).toLowerCase() === host.hostType && detection.hostId === host.hostId,
  );
  // Ranked best first, so the first detection seen for a track is its representative.
  const ranked = [...onHost].sort(compareFaceSampleDetections);
  for (const detection of ranked) {
    const groupKey = detection.groupKey?.replace(/:span-\d+$/, "") || `detection-${detection.id}`;
    const seconds = detection.observedAtSec ?? undefined;
    const track = tracks.get(groupKey);
    if (!track) {
      tracks.set(groupKey, {
        groupKey,
        firstSeenSeconds: seconds,
        lastSeenSeconds: seconds,
        sampleCount: 1,
        detectionCount: 1,
        representativeFrameSeconds: seconds,
        representativeDetectionId: detection.id,
        suggestedGroup: 0,
      });
      continue;
    }
    track.detectionCount += 1;
    track.sampleCount += 1;
    if (seconds != null) {
      track.firstSeenSeconds = Math.min(track.firstSeenSeconds ?? seconds, seconds);
      track.lastSeenSeconds = Math.max(track.lastSeenSeconds ?? seconds, seconds);
    }
  }
  return [...tracks.values()];
}
