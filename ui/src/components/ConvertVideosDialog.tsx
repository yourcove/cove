import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, Cpu, Loader2, TriangleAlert, Zap } from "lucide-react";
import {
  videoConversion,
  type VideoConversionCodec,
  type VideoConversionEncoderInfo,
  type VideoConversionOptions,
} from "../api/client";
import { EditModal } from "./EditModal";

interface Props {
  open: boolean;
  onClose: () => void;
  videoIds: number[];
  /** Highest source frame rate in the selection, when known. Hides frame rates that would not lower it. */
  maxSourceFrameRate?: number | null;
  /** Whether the viewer may delete files from disk, which replacing originals does. */
  canReplaceOriginals: boolean;
  onStarted?: () => void;
  title?: string;
}

type Settings = Omit<VideoConversionOptions, "videoIds">;

const STORAGE_KEY = "cove.convert-videos.options";

const DEFAULT_SETTINGS: Settings = {
  codec: "hevc",
  container: "mp4",
  effort: "highHardware",
  outputFrameRate: null,
  convertMarginalSavings: false,
  convertEvenIfLarger: false,
  replaceOriginal: false,
};

// One ladder from most quality to most speed. Each rung is a quality every video is measured against, not
// a bitrate: Cove encodes short samples of each video, finds the smallest file that keeps that quality,
// and leaves a video alone when that would not save enough. Hardware entries are listed separately
// rather than as a modifier: a machine may not have a usable hardware encoder at all, and hardware is a
// different quality-per-bit trade rather than a speed setting.
export const EFFORTS: ReadonlyArray<{ value: Settings["effort"]; label: string; hint: string }> = [
  {
    value: "highHardware",
    label: "High quality (GPU)",
    hint: "Indistinguishable from the original on close inspection. Each video gets the smallest file that keeps it that way. Needs a supported GPU.",
  },
  {
    value: "highSoftware",
    label: "High quality (CPU)",
    hint: "The same quality with smaller files, but several times slower.",
  },
  {
    value: "balancedHardware",
    label: "Balanced — lower size, some detail lost (GPU)",
    hint: "Fine detail such as hair and freckles softens on close inspection; most of the picture holds up.",
  },
  {
    value: "balancedSoftware",
    label: "Balanced — lower size, some detail lost (CPU)",
    hint: "The Balanced quality on the CPU. Slower, smaller files.",
  },
];

// Only rates below the source are useful: converting up invents frames, costing size for nothing.
export const FRAME_RATE_CHOICES = [60, 30, 24] as const;

export const CODECS: ReadonlyArray<{ value: VideoConversionCodec; label: string; hint: string }> = [
  { value: "hevc", label: "HEVC (H.265)", hint: "About half the size of H.264 at the same quality." },
  { value: "h264", label: "H.264", hint: "Plays everywhere; larger files." },
  {
    value: "av1",
    label: "AV1",
    hint: "About the same size as HEVC at these quality levels, where AV1 gains least. Much slower without a GPU that encodes AV1.",
  },
  {
    value: "copy",
    label: "Keep codec (remux only)",
    hint: "Only changes the container. Takes seconds and loses no quality.",
  },
];

// Keeps the choices someone made last time, and falls back to the defaults when storage is unavailable.
/**
 * Restores the last-used choices, keeping only values this build still offers.
 *
 * These are persisted across sessions, so a stored choice can outlive the option that produced it: a
 * value renamed or retired in a later build would otherwise be replayed straight into the API, which
 * rejects the whole request. Anything unrecognised falls back to the default rather than being trusted.
 */
function loadSettings(): Settings {
  try {
    const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "null") as Partial<Settings> | null;
    if (!stored || typeof stored !== "object") return DEFAULT_SETTINGS;

    const oneOf = <T extends string>(value: unknown, allowed: readonly T[], fallback: T): T =>
      typeof value === "string" && (allowed as readonly string[]).includes(value) ? (value as T) : fallback;

    const frameRate =
      typeof stored.outputFrameRate === "number" &&
      Number.isFinite(stored.outputFrameRate) &&
      stored.outputFrameRate > 0
        ? stored.outputFrameRate
        : null;

    return {
      ...DEFAULT_SETTINGS,
      codec: oneOf(
        stored.codec,
        CODECS.map((option) => option.value),
        DEFAULT_SETTINGS.codec,
      ),
      container: oneOf(stored.container, ["mp4", "mkv"] as const, DEFAULT_SETTINGS.container),
      effort: oneOf(
        stored.effort,
        EFFORTS.map((option) => option.value),
        DEFAULT_SETTINGS.effort,
      ),
      outputFrameRate: frameRate,
      convertEvenIfLarger:
        typeof stored.convertEvenIfLarger === "boolean"
          ? stored.convertEvenIfLarger
          : DEFAULT_SETTINGS.convertEvenIfLarger,
      convertMarginalSavings:
        typeof stored.convertMarginalSavings === "boolean"
          ? stored.convertMarginalSavings
          : DEFAULT_SETTINGS.convertMarginalSavings,
      // Replacing originals deletes files, so it is never pre-selected from a previous session.
      replaceOriginal: false,
    };
  } catch {
    return DEFAULT_SETTINGS;
  }
}

function saveSettings(settings: Settings) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...settings, replaceOriginal: false }));
  } catch {
    // Remembering the choices is a convenience only.
  }
}

export function EncoderHint({
  codec,
  encoders,
  loading,
}: {
  codec: VideoConversionCodec;
  encoders?: VideoConversionEncoderInfo[];
  loading: boolean;
}) {
  if (codec === "copy") return null;
  if (loading) {
    return (
      <p className="flex items-center gap-1.5 text-xs text-muted">
        <Loader2 className="h-3 w-3 animate-spin" /> Checking which encoders this machine can use…
      </p>
    );
  }
  const info = encoders?.find((item) => item.codec === codec);
  if (!info) return null;
  if (!info.encoder) {
    return (
      <p className="flex items-center gap-1.5 text-xs text-red-400">
        <TriangleAlert className="h-3 w-3" /> This ffmpeg build cannot encode {codec.toUpperCase()}.
      </p>
    );
  }
  return info.hardware ? (
    <p className="flex items-center gap-1.5 text-xs text-green-400">
      <Zap className="h-3 w-3" /> GPU encoding with {info.encoder}
    </p>
  ) : (
    <p className="flex items-center gap-1.5 text-xs text-secondary">
      <Cpu className="h-3 w-3" /> CPU encoding with {info.encoder} (no usable GPU encoder; slower)
    </p>
  );
}

const selectClass =
  "w-full bg-input border border-border rounded px-2 py-1.5 text-sm text-foreground focus:outline-none focus:border-accent disabled:opacity-50";

export function ConvertVideosDialog({
  open,
  onClose,
  videoIds,
  canReplaceOriginals,
  onStarted,
  title,
  maxSourceFrameRate,
}: Props) {
  const queryClient = useQueryClient();
  const [settings, setSettings] = useState<Settings>(loadSettings);
  const [submitted, setSubmitted] = useState(false);

  const encodersQuery = useQuery({
    queryKey: ["video-conversion-encoders"],
    queryFn: videoConversion.encoders,
    enabled: open,
    staleTime: 5 * 60_000,
  });

  const startMut = useMutation({
    mutationFn: () => videoConversion.start({ ...settings, videoIds }),
    onSuccess: () => {
      saveSettings(settings);
      for (const key of ["jobs", "jobs-active", "jobs-history"]) queryClient.invalidateQueries({ queryKey: [key] });
      setSubmitted(true);
      onStarted?.();
    },
  });

  if (!open) return null;

  const update = <K extends keyof Settings>(key: K, value: Settings[K]) =>
    setSettings((current) => ({ ...current, [key]: value }));

  const reencodes = settings.codec !== "copy";
  const selectedEncoder = encodersQuery.data?.find((item) => item.codec === settings.codec);
  const cannotEncode = reencodes && encodersQuery.isSuccess && !selectedEncoder?.encoder;
  const count = videoIds.length;
  const heading = title ?? `Convert ${count} video${count === 1 ? "" : "s"}`;

  return (
    <EditModal open={open} onClose={onClose} title={heading} maxWidthClassName="sm:max-w-lg">
      <div className="space-y-4">
        <p className="text-sm text-secondary">
          Each video's primary file is converted into a new file next to it, which is added to the same video. Files
          already in the chosen format are skipped.
        </p>

        <div className="space-y-1.5">
          <label className="text-xs font-semibold uppercase tracking-wider text-muted" htmlFor="convert-codec">
            Video codec
          </label>
          <select
            id="convert-codec"
            value={settings.codec}
            onChange={(event) => update("codec", event.target.value as VideoConversionCodec)}
            className={selectClass}
          >
            {CODECS.map((codec) => (
              <option key={codec.value} value={codec.value}>
                {codec.label}
              </option>
            ))}
          </select>
          <p className="text-xs text-muted">{CODECS.find((codec) => codec.value === settings.codec)?.hint}</p>
          <EncoderHint codec={settings.codec} encoders={encodersQuery.data} loading={encodersQuery.isLoading} />
        </div>

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          <div className="space-y-1.5">
            <label className="text-xs font-semibold uppercase tracking-wider text-muted" htmlFor="convert-container">
              Container
            </label>
            <select
              id="convert-container"
              value={settings.container}
              onChange={(event) => update("container", event.target.value as Settings["container"])}
              className={selectClass}
            >
              <option value="mp4">MP4</option>
              <option value="mkv">MKV</option>
            </select>
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <label className="text-xs font-semibold uppercase tracking-wider text-muted" htmlFor="convert-effort">
              Quality
            </label>
            <select
              id="convert-effort"
              value={settings.effort}
              disabled={!reencodes}
              onChange={(event) => update("effort", event.target.value as Settings["effort"])}
              className={selectClass}
            >
              {EFFORTS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
            <p className="text-xs text-muted">{EFFORTS.find((o) => o.value === settings.effort)?.hint}</p>
          </div>
          <div className="space-y-1.5 sm:col-span-2">
            <label className="text-xs font-semibold uppercase tracking-wider text-muted" htmlFor="convert-fps">
              Frame rate
            </label>
            <select
              id="convert-fps"
              value={settings.outputFrameRate == null ? "" : String(settings.outputFrameRate)}
              disabled={!reencodes}
              onChange={(event) =>
                update("outputFrameRate", event.target.value === "" ? null : Number(event.target.value))
              }
              className={selectClass}
            >
              <option value="">Keep source frame rate</option>
              {FRAME_RATE_CHOICES.filter((fps) => maxSourceFrameRate == null || fps < maxSourceFrameRate - 0.01).map(
                (fps) => (
                  <option key={fps} value={String(fps)}>
                    {fps} fps
                  </option>
                ),
              )}
            </select>
            <p className="text-xs text-muted">
              Lowering the frame rate shrinks the file without touching per-frame detail. Motion becomes less smooth.
            </p>
          </div>
        </div>
        {settings.container === "mp4" && (
          <p className="text-xs text-muted">
            MP4 can't hold some tracks: audio it can't store is re-encoded to AAC, and image-based subtitles are
            dropped. Choose MKV to keep everything.
          </p>
        )}

        <div className="space-y-2 border-t border-border pt-4">
          {reencodes && (
            <>
              <label className="flex cursor-pointer items-start gap-3">
                <input
                  type="checkbox"
                  checked={settings.convertEvenIfLarger ?? false}
                  onChange={() => update("convertEvenIfLarger", !settings.convertEvenIfLarger)}
                  className="mt-0.5 h-4 w-4 rounded border-border accent-accent"
                />
                <span className="text-sm text-foreground">
                  Convert even when the result would be larger
                  <span className="block text-xs text-muted">
                    A video already below the bitrate its resolution can use has no space to reclaim, so re-encoding it
                    would only grow the file and lose quality.
                  </span>
                </span>
              </label>
              <label className="flex cursor-pointer items-start gap-3">
                <input
                  type="checkbox"
                  checked={settings.convertMarginalSavings ?? false}
                  onChange={() => update("convertMarginalSavings", !settings.convertMarginalSavings)}
                  className="mt-0.5 h-4 w-4 rounded border-border accent-accent"
                />
                <span className="text-sm text-foreground">
                  Convert even when it would save very little
                  <span className="block text-xs text-muted">
                    Videos already close to the bitrate their resolution can use are skipped by default, since
                    re-encoding them costs time and a little quality for almost no space.
                  </span>
                </span>
              </label>
            </>
          )}
          {canReplaceOriginals && (
            <label className="flex cursor-pointer items-start gap-3">
              <input
                type="checkbox"
                checked={settings.replaceOriginal}
                onChange={() => update("replaceOriginal", !settings.replaceOriginal)}
                className="mt-0.5 h-4 w-4 rounded border-border accent-orange-500"
              />
              <span className="text-sm">
                <span className="text-orange-400">Replace the originals</span>
                <span className="block text-xs text-muted">
                  Each converted file is fully decoded to check it isn't corrupt, and checked to be the same footage
                  (length and appearance). Only then does it become the primary file, keeping covers, previews, sprites
                  and markers, and the original is deleted from disk. If any check fails, the original is kept.
                </span>
              </span>
            </label>
          )}
        </div>

        {startMut.error && (
          <p role="alert" className="text-sm text-red-400">
            {(startMut.error as Error).message}
          </p>
        )}
      </div>

      <div className="mt-5 flex items-center justify-end gap-2 border-t border-border pt-4">
        {submitted ? (
          <>
            <div className="mr-auto flex items-center gap-2 text-sm text-green-400">
              <Check className="h-4 w-4" />
              Conversion job queued
            </div>
            <button
              onClick={() => {
                setSubmitted(false);
                onClose();
              }}
              className="rounded-lg px-4 py-2 text-sm text-secondary hover:bg-surface hover:text-foreground"
            >
              Close
            </button>
          </>
        ) : (
          <>
            <button
              onClick={onClose}
              className="rounded-lg px-4 py-2 text-sm text-secondary hover:bg-surface hover:text-foreground"
            >
              Cancel
            </button>
            <button
              onClick={() => startMut.mutate()}
              disabled={startMut.isPending || cannotEncode || count === 0}
              className={`flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium text-white disabled:opacity-50 ${
                settings.replaceOriginal ? "bg-orange-600 hover:bg-orange-500" : "bg-accent hover:bg-accent-hover"
              }`}
            >
              {startMut.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
              {settings.replaceOriginal ? "Convert and replace" : "Convert"}
            </button>
          </>
        )}
      </div>
    </EditModal>
  );
}
