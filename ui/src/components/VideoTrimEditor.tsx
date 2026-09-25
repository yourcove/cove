import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, Crosshair, Loader2, Scissors, Trash2, TriangleAlert, X } from "lucide-react";
import {
  videoConversion,
  videoCuts,
  type VideoConversionCodec,
  type VideoConversionOptions,
  type VideoCutItemPreview,
  type VideoCutPreview,
} from "../api/client";
import { CODECS, EFFORTS, EncoderHint, FRAME_RATE_CHOICES } from "./ConvertVideosDialog";
import { formatFileSize } from "./shared";
import {
  formatTimecode,
  keptSeconds,
  normalizeCutRanges,
  parseTimecode,
  removalAt,
  type CutRange,
} from "../utils/videoCut";

interface Props {
  videoId: number;
  /** The primary file the marks are placed on. A cut is refused if the primary file changes before it runs. */
  fileId: number;
  duration: number;
  currentTime: number;
  /** Removals to start with, e.g. from a link an extension opened. */
  initialRemove?: CutRange[];
  /** Whether the viewer may delete files from disk, which replacing the original does. */
  canReplaceOriginal: boolean;
  /** The file's frame rate, when known. Only lower rates are offered for an exact cut. */
  sourceFrameRate?: number | null;
  onSeek: (time: number) => void;
  onClose: () => void;
}

type Mode = "fast" | "exact";
type ReencodeCodec = Exclude<VideoConversionCodec, "copy">;

interface StoredChoices {
  mode: Mode;
  codec: ReencodeCodec;
  effort: VideoConversionOptions["effort"];
  /** Re-encode at this lower frame rate; null keeps the source's. */
  outputFrameRate: number | null;
}

const STORAGE_KEY = "cove.trim-video.options";
const DEFAULT_CHOICES: StoredChoices = { mode: "fast", codec: "hevc", effort: "highHardware", outputFrameRate: null };
const REENCODE_CODECS = CODECS.filter((option) => option.value !== "copy");

// Remembers the mode and encoder between videos. Replacing the original deletes a file, so like the
// convert dialog it is never carried over.
function loadChoices(): StoredChoices {
  try {
    const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "null") as Partial<StoredChoices> | null;
    if (!stored || typeof stored !== "object") return DEFAULT_CHOICES;
    return {
      mode: stored.mode === "exact" ? "exact" : "fast",
      codec: REENCODE_CODECS.some((option) => option.value === stored.codec)
        ? (stored.codec as ReencodeCodec)
        : DEFAULT_CHOICES.codec,
      effort: EFFORTS.some((option) => option.value === stored.effort)
        ? (stored.effort as StoredChoices["effort"])
        : DEFAULT_CHOICES.effort,
      outputFrameRate: FRAME_RATE_CHOICES.some((fps) => fps === stored.outputFrameRate)
        ? (stored.outputFrameRate as number)
        : null,
    };
  } catch {
    return DEFAULT_CHOICES;
  }
}

function saveChoices(choices: StoredChoices) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(choices));
  } catch {
    // Remembering the choices is a convenience only.
  }
}

/** Pixels either side of a removal's edge that grab the edge rather than start a new removal. */
const EDGE_GRAB_PX = 6;
/** Movement below this is a click, which seeks. */
const DRAG_THRESHOLD_PX = 3;

type Drag =
  | { kind: "new"; anchor: number; startX: number; moved: boolean }
  | { kind: "edge"; index: number; edge: "start" | "end"; startX: number; moved: boolean };

/**
 * Marks parts of a video to remove and cuts them out. The marks are drawn over a timeline under the
 * player: drag across it to mark a part, drag a part's edge to move it, click to seek. Everything
 * timed on the video (markers, clips, group ranges) moves with the cut when the original is replaced,
 * and the preview lists what happens to each before anything changes.
 */
export function VideoTrimEditor({
  videoId,
  fileId,
  duration,
  currentTime,
  initialRemove,
  canReplaceOriginal,
  sourceFrameRate,
  onSeek,
  onClose,
}: Props) {
  const queryClient = useQueryClient();
  const [ranges, setRanges] = useState<CutRange[]>(() => normalizeCutRanges(initialRemove ?? [], duration));
  const [draft, setDraft] = useState<CutRange[] | null>(null);
  const [markIn, setMarkIn] = useState<number | null>(null);
  const [choices, setChoices] = useState<StoredChoices>(loadChoices);
  const [replaceOriginal, setReplaceOriginal] = useState(false);
  const [skipRemoved, setSkipRemoved] = useState(false);
  const [started, setStarted] = useState(false);
  const barRef = useRef<HTMLDivElement>(null);
  const dragRef = useRef<Drag | null>(null);

  const shown = draft ?? ranges;
  const exact = choices.mode === "exact";
  // Converting up would invent frames, so only rates below the source are offered, and a remembered rate
  // that would not lower this video's is not applied to it.
  const frameRates = FRAME_RATE_CHOICES.filter((fps) => sourceFrameRate == null || fps < sourceFrameRate - 0.01);
  const outputFrameRate = frameRates.some((fps) => fps === choices.outputFrameRate) ? choices.outputFrameRate : null;

  // A new set of removals from a link replaces whatever was being edited.
  const initialKey = JSON.stringify(initialRemove ?? null);
  const appliedKeyRef = useRef(initialKey);
  useEffect(() => {
    if (appliedKeyRef.current === initialKey) return;
    appliedKeyRef.current = initialKey;
    if (initialRemove) setRanges(normalizeCutRanges(initialRemove, duration));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- keyed on the serialized ranges
  }, [initialKey, duration]);

  // Previewing is cheap but reads keyframes from the file, so it waits for the marks to settle.
  const [settled, setSettled] = useState(ranges);
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(ranges), 350);
    return () => window.clearTimeout(timer);
  }, [ranges]);

  const previewQuery = useQuery({
    queryKey: ["video-cut-preview", videoId, fileId, settled, exact],
    queryFn: ({ signal }) => videoCuts.preview(videoId, settled, exact, signal),
    enabled: settled.length > 0 && keptSeconds(settled, duration) > 0,
    placeholderData: keepPreviousData,
    retry: false,
    staleTime: 60_000,
  });
  const preview = settled.length > 0 ? previewQuery.data : undefined;
  const previewCurrent = preview != null && settled === ranges && !previewQuery.isPlaceholderData;

  const encodersQuery = useQuery({
    queryKey: ["video-conversion-encoders"],
    queryFn: videoConversion.encoders,
    enabled: exact,
    staleTime: 5 * 60_000,
  });
  const cannotEncode =
    exact && encodersQuery.isSuccess && !encodersQuery.data.find((item) => item.codec === choices.codec)?.encoder;

  // Playing the result: jump over removed parts as playback reaches them.
  useEffect(() => {
    if (!skipRemoved) return;
    const removal = removalAt(ranges, currentTime);
    if (removal && removal.end < duration - 0.05) onSeek(removal.end);
  }, [skipRemoved, currentTime, ranges, duration, onSeek]);

  const startMut = useMutation({
    mutationFn: () =>
      videoCuts.start({
        videos: [{ videoId, fileId, remove: ranges }],
        codec: exact ? choices.codec : "copy",
        container: "source",
        effort: choices.effort,
        outputFrameRate: exact ? outputFrameRate : null,
        replaceOriginal,
      }),
    onSuccess: () => {
      saveChoices(choices);
      for (const key of ["jobs", "jobs-active", "jobs-history"]) queryClient.invalidateQueries({ queryKey: [key] });
      setStarted(true);
    },
  });

  const commit = (next: CutRange[]) => {
    setRanges(normalizeCutRanges(next, duration));
    setStarted(false);
  };

  const timeAt = (clientX: number) => {
    const rect = barRef.current?.getBoundingClientRect();
    if (!rect || rect.width <= 0) return 0;
    return Math.min(duration, Math.max(0, ((clientX - rect.left) / rect.width) * duration));
  };
  const pxPerSecond = () => (barRef.current?.getBoundingClientRect().width ?? 0) / Math.max(duration, 0.001);

  const onPointerDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    const time = timeAt(event.clientX);
    const grab = EDGE_GRAB_PX / Math.max(pxPerSecond(), 0.0001);
    let edge: Drag | null = null;
    ranges.forEach((range, index) => {
      if (edge) return;
      if (Math.abs(time - range.start) <= grab)
        edge = { kind: "edge", index, edge: "start", startX: event.clientX, moved: false };
      else if (Math.abs(time - range.end) <= grab)
        edge = { kind: "edge", index, edge: "end", startX: event.clientX, moved: false };
    });
    dragRef.current = edge ?? { kind: "new", anchor: time, startX: event.clientX, moved: false };
  };

  const onPointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const drag = dragRef.current;
    if (!drag) return;
    if (!drag.moved && Math.abs(event.clientX - drag.startX) < DRAG_THRESHOLD_PX) return;
    drag.moved = true;
    const time = timeAt(event.clientX);
    if (drag.kind === "new") {
      setDraft([...ranges, { start: Math.min(drag.anchor, time), end: Math.max(drag.anchor, time) }]);
    } else {
      setDraft(ranges.map((range, index) => (index === drag.index ? { ...range, [drag.edge]: time } : range)));
    }
  };

  const onPointerUp = (event: ReactPointerEvent<HTMLDivElement>) => {
    const drag = dragRef.current;
    dragRef.current = null;
    if (!drag) return;
    if (!drag.moved) {
      onSeek(timeAt(event.clientX));
    } else if (draft) {
      commit(draft);
    }
    setDraft(null);
  };

  const updateRange = (index: number, edge: "start" | "end", value: number) =>
    commit(ranges.map((range, i) => (i === index ? { ...range, [edge]: value } : range)));

  const markedKept = keptSeconds(ranges, duration);
  const removed = duration - markedKept;
  // A lossless cut starts each kept part on a keyframe at or before the mark, so a little more is kept.
  const extraKept = useMemo(() => extraKeptParts(preview, settled), [preview, settled]);
  const extraSeconds = extraKept.reduce((total, part) => total + (part.end - part.start), 0);
  const nothingLeft = ranges.length > 0 && markedKept <= 0.25;

  const pct = (time: number) => `${(Math.min(Math.max(time, 0), duration) / Math.max(duration, 0.001)) * 100}%`;

  return (
    <section
      aria-label="Trim video"
      className="max-h-[50vh] shrink-0 overflow-y-auto border-t border-white/10 bg-neutral-950 px-3 py-2 text-sm text-white"
    >
      <div className="mb-2 flex flex-wrap items-center gap-x-3 gap-y-1">
        <h2 className="flex items-center gap-1.5 font-semibold">
          <Scissors className="h-4 w-4 text-accent" /> Trim
        </h2>
        <span className="text-xs text-white/60">
          {ranges.length === 0
            ? "Drag across the bar to mark a part to remove, or use the buttons at the playhead."
            : `Keeps ${formatTimecode(markedKept)} of ${formatTimecode(duration)} · removes ${formatTimecode(removed)}`}
          {previewCurrent && preview.estimatedBytes < preview.sourceBytes
            ? ` · about ${formatFileSize(preview.sourceBytes - preview.estimatedBytes)} smaller`
            : null}
        </span>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close trim"
          className="ml-auto rounded p-1 text-white/60 hover:bg-white/10 hover:text-white"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <div
        ref={barRef}
        role="slider"
        aria-label="Trim timeline"
        aria-valuemin={0}
        aria-valuemax={duration}
        aria-valuenow={currentTime}
        tabIndex={0}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={() => {
          dragRef.current = null;
          setDraft(null);
        }}
        className="relative h-10 cursor-crosshair touch-none select-none overflow-hidden rounded border border-white/15 bg-white/[0.06]"
      >
        {shown.map((range, index) => (
          <div
            key={`${index}-${range.start}`}
            className="absolute inset-y-0 border-x-2 border-red-400 bg-red-500/35"
            style={{
              left: pct(Math.min(range.start, range.end)),
              width: pct(Math.abs(range.end - range.start)),
              backgroundImage: "repeating-linear-gradient(135deg, transparent 0 6px, rgba(0,0,0,0.25) 6px 12px)",
              cursor: "ew-resize",
            }}
          />
        ))}
        {!exact &&
          extraKept.map((part) => (
            <div
              key={`extra-${part.start}`}
              title="Kept by a lossless cut, which starts on the keyframe before your mark"
              className="absolute inset-y-0 bg-amber-400/60"
              style={{ left: pct(part.start), width: pct(part.end - part.start) }}
            />
          ))}
        {markIn != null ? (
          <div className="absolute inset-y-0 w-0.5 bg-sky-400" style={{ left: pct(markIn) }} title="Mark in" />
        ) : null}
        <div className="pointer-events-none absolute inset-y-0 w-0.5 bg-white" style={{ left: pct(currentTime) }} />
      </div>
      <div className="mt-0.5 flex justify-between text-[10px] text-white/40">
        <span>0:00.0</span>
        <span>{formatTimecode(currentTime)}</span>
        <span>{formatTimecode(duration)}</span>
      </div>

      <div className="mt-2 flex flex-wrap items-center gap-1.5">
        <ToolButton onClick={() => commit([...ranges, { start: 0, end: currentTime }])} disabled={currentTime <= 0}>
          Remove start → here
        </ToolButton>
        {markIn == null ? (
          <ToolButton onClick={() => setMarkIn(currentTime)}>Mark in at {formatTimecode(currentTime)}</ToolButton>
        ) : (
          <>
            <ToolButton
              onClick={() => {
                commit([...ranges, { start: markIn, end: currentTime }]);
                setMarkIn(null);
              }}
              disabled={Math.abs(currentTime - markIn) < 0.05}
              accent
            >
              Remove {formatTimecode(Math.min(markIn, currentTime))} → {formatTimecode(Math.max(markIn, currentTime))}
            </ToolButton>
            <ToolButton onClick={() => setMarkIn(null)}>Cancel mark</ToolButton>
          </>
        )}
        <ToolButton
          onClick={() => commit([...ranges, { start: currentTime, end: duration }])}
          disabled={currentTime >= duration}
        >
          Remove here → end
        </ToolButton>
        <label className="ml-auto flex cursor-pointer items-center gap-1.5 text-xs text-white/70">
          <input
            type="checkbox"
            checked={skipRemoved}
            onChange={() => setSkipRemoved((value) => !value)}
            className="h-3.5 w-3.5 accent-accent"
          />
          Skip removed parts while playing
        </label>
      </div>

      {ranges.length > 0 ? (
        <ul className="mt-2 space-y-1">
          {ranges.map((range, index) => (
            <li key={`${index}-${range.start}-${range.end}`} className="flex flex-wrap items-center gap-1.5 text-xs">
              <span className="w-16 text-white/50">Remove</span>
              <TimecodeInput
                label={`Removal ${index + 1} start`}
                value={range.start}
                onCommit={(value) => updateRange(index, "start", value)}
              />
              <span className="text-white/40">→</span>
              <TimecodeInput
                label={`Removal ${index + 1} end`}
                value={range.end}
                onCommit={(value) => updateRange(index, "end", value)}
              />
              <span className="text-white/40">({formatTimecode(range.end - range.start)})</span>
              <button
                type="button"
                onClick={() => onSeek(Math.max(0, range.start - 2))}
                aria-label={`Play from just before removal ${index + 1}`}
                className="rounded p-1 text-white/60 hover:bg-white/10 hover:text-white"
              >
                <Crosshair className="h-3.5 w-3.5" />
              </button>
              <button
                type="button"
                onClick={() => commit(ranges.filter((_, i) => i !== index))}
                aria-label={`Keep removal ${index + 1}'s footage`}
                className="rounded p-1 text-white/60 hover:bg-white/10 hover:text-red-300"
              >
                <Trash2 className="h-3.5 w-3.5" />
              </button>
            </li>
          ))}
        </ul>
      ) : null}

      {nothingLeft ? (
        <p role="alert" className="mt-2 text-xs text-red-400">
          That removes the whole video. Keep at least part of it.
        </p>
      ) : previewQuery.error && ranges.length > 0 ? (
        <p role="alert" className="mt-2 text-xs text-red-400">
          {(previewQuery.error as Error).message}
        </p>
      ) : null}
      {!exact && extraSeconds >= 0.05 && previewCurrent ? (
        <p className="mt-2 text-xs text-amber-300">
          A fast cut keeps {formatTimecode(extraSeconds)} more than marked (amber): each kept part has to start on a
          keyframe, which is at or just before your mark. Choose an exact cut to land on the marks.
        </p>
      ) : null}
      {previewCurrent ? <ItemOutcomes items={preview.items} replaceOriginal={replaceOriginal} /> : null}

      <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1.5 border-t border-white/10 pt-2">
        <fieldset className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <legend className="sr-only">Cut mode</legend>
          <ModeOption
            checked={!exact}
            onChange={() => setChoices((current) => ({ ...current, mode: "fast" }))}
            title="Fast (no re-encode)"
          />
          <ModeOption
            checked={exact}
            onChange={() => setChoices((current) => ({ ...current, mode: "exact" }))}
            title="Exact (re-encode)"
          />
        </fieldset>
        {exact ? (
          <>
            <select
              aria-label="Codec"
              value={choices.codec}
              onChange={(event) =>
                setChoices((current) => ({ ...current, codec: event.target.value as ReencodeCodec }))
              }
              className="rounded border border-white/15 bg-neutral-900 px-1.5 py-0.5 text-xs"
            >
              {REENCODE_CODECS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
            <select
              aria-label="Quality"
              value={choices.effort}
              onChange={(event) =>
                setChoices((current) => ({ ...current, effort: event.target.value as StoredChoices["effort"] }))
              }
              className="max-w-[16rem] rounded border border-white/15 bg-neutral-900 px-1.5 py-0.5 text-xs"
            >
              {EFFORTS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
            <select
              aria-label="Frame rate"
              value={outputFrameRate == null ? "" : String(outputFrameRate)}
              onChange={(event) =>
                setChoices((current) => ({
                  ...current,
                  outputFrameRate: event.target.value === "" ? null : Number(event.target.value),
                }))
              }
              className="rounded border border-white/15 bg-neutral-900 px-1.5 py-0.5 text-xs"
            >
              <option value="">Keep source frame rate</option>
              {frameRates.map((fps) => (
                <option key={fps} value={String(fps)}>
                  {fps} fps
                </option>
              ))}
            </select>
          </>
        ) : null}
        <div className="ml-auto flex items-center gap-3">
          {canReplaceOriginal ? (
            <label className="flex cursor-pointer items-center gap-1.5 text-xs text-orange-400">
              <input
                type="checkbox"
                checked={replaceOriginal}
                onChange={() => setReplaceOriginal((value) => !value)}
                className="h-3.5 w-3.5 accent-orange-500"
              />
              Replace the original
            </label>
          ) : null}
          <button
            type="button"
            onClick={() => startMut.mutate()}
            disabled={
              ranges.length === 0 || nothingLeft || startMut.isPending || started || cannotEncode || draft != null
            }
            className={`flex items-center gap-1.5 rounded-lg px-3 py-1 text-sm font-medium text-white disabled:opacity-50 ${
              replaceOriginal ? "bg-orange-600 hover:bg-orange-500" : "bg-accent hover:bg-accent-hover"
            }`}
          >
            {startMut.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Scissors className="h-4 w-4" />}
            {replaceOriginal ? "Cut and replace" : "Cut"}
          </button>
        </div>
      </div>
      <div className="mt-1 space-y-0.5 text-xs text-white/55">
        <p>
          {exact
            ? `Cuts exactly on the marks and re-encodes what is kept at the chosen quality, which shrinks it too. Slower.${
                outputFrameRate != null ? " A lower frame rate shrinks it further; motion becomes less smooth." : ""
              }`
            : "Takes seconds and loses no quality. Each kept part starts on the keyframe at or before its mark."}
        </p>
        {exact ? (
          <EncoderHint codec={choices.codec} encoders={encodersQuery.data} loading={encodersQuery.isLoading} />
        ) : null}
        <p className={replaceOriginal ? "text-orange-300/90" : undefined}>
          {replaceOriginal
            ? "The cut file is checked, becomes the video's file with markers and clips moved to match, and the original is deleted from disk."
            : canReplaceOriginal
              ? "The cut file is added next to the original, which is kept, so no space is saved until the original is replaced."
              : "The cut file is added next to the original. Replacing the original needs permission to delete files."}
        </p>
        {startMut.error ? (
          <p role="alert" className="text-red-400">
            {(startMut.error as Error).message}
          </p>
        ) : null}
        {started ? (
          <p className="flex items-center gap-1.5 text-green-400">
            <Check className="h-3.5 w-3.5" /> Cut queued. Follow it in Jobs.
          </p>
        ) : null}
      </div>
    </section>
  );
}

/** Parts a lossless cut keeps although they were marked for removal. */
function extraKeptParts(preview: VideoCutPreview | undefined, marked: readonly CutRange[]): CutRange[] {
  if (!preview || preview.exact) return [];
  const parts: CutRange[] = [];
  for (const kept of preview.kept) {
    for (const removal of marked) {
      const start = Math.max(kept.start, removal.start);
      const end = Math.min(kept.end, removal.end);
      if (end - start > 0.001) parts.push({ start, end });
    }
  }
  return parts;
}

const OUTCOME_ORDER: VideoCutItemPreview["outcome"][] = ["removed", "joined", "moved"];
const KIND_LABELS: Record<VideoCutItemPreview["kind"], string> = {
  segment: "marker",
  clip: "clip",
  detection: "detection",
  "group range": "group range",
};

function ItemOutcomes({ items, replaceOriginal }: { items: VideoCutItemPreview[]; replaceOriginal: boolean }) {
  const byOutcome = OUTCOME_ORDER.map((outcome) => ({
    outcome,
    items: items.filter((item) => item.outcome === outcome),
  })).filter((group) => group.items.length > 0);
  if (byOutcome.length === 0) return null;
  const describe = (item: VideoCutItemPreview) =>
    `${item.title || `${KIND_LABELS[item.kind]} ${item.id}`} (${formatTimecode(item.start)})`;
  return (
    <div className="mt-2 space-y-1 text-xs">
      {!replaceOriginal ? (
        <p className="text-white/50">
          Markers and clips stay on the original until it is replaced. If it were replaced:
        </p>
      ) : null}
      {byOutcome.map(({ outcome, items: group }) => (
        <p key={outcome} className={outcome === "removed" ? "flex items-start gap-1 text-red-300" : "text-white/70"}>
          {outcome === "removed" ? <TriangleAlert className="mt-0.5 h-3 w-3 shrink-0" /> : null}
          <span>
            {outcome === "removed"
              ? `${group.length} item${group.length === 1 ? " lies" : "s lie"} entirely inside removed parts and would be deleted: `
              : outcome === "joined"
                ? `${group.length} item${group.length === 1 ? " spans" : "s span"} a cut and would be joined across it: `
                : `${group.length} item${group.length === 1 ? "" : "s"} would move earlier to stay on the same footage`}
            {outcome === "moved" ? null : group.slice(0, 6).map(describe).join(", ")}
            {outcome !== "moved" && group.length > 6 ? `, and ${group.length - 6} more` : null}
          </span>
        </p>
      ))}
    </div>
  );
}

function ToolButton({
  onClick,
  disabled,
  accent,
  children,
}: {
  onClick: () => void;
  disabled?: boolean;
  accent?: boolean;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={`rounded border px-2 py-1 text-xs disabled:opacity-40 ${
        accent
          ? "border-accent bg-accent/20 text-white hover:bg-accent/30"
          : "border-white/15 bg-white/[0.04] text-white/85 hover:border-white/30 hover:bg-white/10"
      }`}
    >
      {children}
    </button>
  );
}

function ModeOption({ checked, onChange, title }: { checked: boolean; onChange: () => void; title: string }) {
  return (
    <label className="flex cursor-pointer items-center gap-1.5 text-xs text-white">
      <input
        type="radio"
        name="trim-mode"
        checked={checked}
        onChange={onChange}
        className="h-3.5 w-3.5 accent-accent"
      />
      {title}
    </label>
  );
}

function TimecodeInput({
  label,
  value,
  onCommit,
}: {
  label: string;
  value: number;
  onCommit: (value: number) => void;
}) {
  const [text, setText] = useState(formatTimecode(value));
  const [invalid, setInvalid] = useState(false);
  useEffect(() => {
    setText(formatTimecode(value));
    setInvalid(false);
  }, [value]);
  const commit = () => {
    const parsed = parseTimecode(text);
    if (parsed == null) {
      setInvalid(true);
      return;
    }
    setInvalid(false);
    if (Math.abs(parsed - value) > 0.001) onCommit(parsed);
    else setText(formatTimecode(value));
  };
  return (
    <input
      aria-label={label}
      aria-invalid={invalid}
      value={text}
      onChange={(event) => setText(event.target.value)}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === "Enter") commit();
        // The player listens for keys on the page; typing a time must not seek or pause it.
        event.stopPropagation();
      }}
      className={`w-24 rounded border bg-neutral-900 px-1.5 py-0.5 font-mono text-xs ${
        invalid ? "border-red-400" : "border-white/15"
      }`}
    />
  );
}
