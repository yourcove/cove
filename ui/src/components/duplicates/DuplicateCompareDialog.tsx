import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from "react";
import { AlertTriangle, Columns2, Film, Pause, Play, SplitSquareHorizontal, Volume2, VolumeX } from "lucide-react";
import { videos as videosApi } from "../../api/client";
import type { Video } from "../../api/types";
import { formatDuration, formatFileSize } from "../shared";
import { DuplicateDialog } from "./DuplicateDialog";
import { displayTitle, formatBitrate, formatCodec, primaryFile, totalSize } from "./duplicateModel";

type CompareMode = "slider" | "side" | "frames";
type AudioSource = "a" | "b" | "none";

const DRIFT_TOLERANCE_SECONDS = 0.04;
const SEEK_DRIFT_SECONDS = 0.6;
const FRAME_POSITIONS = [0.05, 0.18, 0.31, 0.44, 0.57, 0.7, 0.83, 0.95];

export function DuplicateCompareDialog({
  open,
  videos,
  keepVideoIds,
  initialPair,
  onClose,
}: {
  open: boolean;
  videos: Video[];
  keepVideoIds: Set<number>;
  initialPair?: [number, number];
  onClose: () => void;
}) {
  const [mode, setMode] = useState<CompareMode>("slider");
  const [pair, setPair] = useState<[number, number]>(() => initialPair ?? defaultPair(videos));
  useEffect(() => {
    if (open) setPair(initialPair ?? defaultPair(videos));
  }, [open, initialPair, videos]);
  const left = videos.find((video) => video.id === pair[0]) ?? videos[0];
  const right = videos.find((video) => video.id === pair[1]) ?? videos[1] ?? videos[0];

  return (
    <DuplicateDialog
      open={open}
      onClose={onClose}
      size="xl"
      title="Compare copies"
      subtitle="Drag the divider or play both copies in sync to spot differences in quality, cuts and watermarks."
    >
      <div className="space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex rounded-lg border border-border bg-card p-1" role="tablist" aria-label="Comparison mode">
            <ModeButton
              active={mode === "slider"}
              onClick={() => setMode("slider")}
              icon={<SplitSquareHorizontal className="h-4 w-4" />}
              label="Slider"
            />
            <ModeButton
              active={mode === "side"}
              onClick={() => setMode("side")}
              icon={<Columns2 className="h-4 w-4" />}
              label="Side by side"
            />
            <ModeButton
              active={mode === "frames"}
              onClick={() => setMode("frames")}
              icon={<Film className="h-4 w-4" />}
              label="Frames"
            />
          </div>
          {mode !== "frames" && videos.length > 2 ? (
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <PairSelect
                label="A"
                value={pair[0]}
                videos={videos}
                onChange={(id) => setPair([id, pair[1] === id ? pair[0] : pair[1]])}
              />
              <PairSelect
                label="B"
                value={pair[1]}
                videos={videos}
                onChange={(id) => setPair([pair[0] === id ? pair[1] : pair[0], id])}
              />
            </div>
          ) : null}
        </div>

        {mode === "frames" ? (
          <FrameStrips videos={videos} keepVideoIds={keepVideoIds} />
        ) : left && right ? (
          <SyncedComparison
            key={`${left.id}-${right.id}-${mode}`}
            mode={mode}
            left={left}
            right={right}
            keepVideoIds={keepVideoIds}
          />
        ) : null}
      </div>
    </DuplicateDialog>
  );
}

function defaultPair(videos: Video[]): [number, number] {
  return [videos[0]?.id ?? 0, videos[1]?.id ?? videos[0]?.id ?? 0];
}

function ModeButton({
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
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm transition-colors ${
        active ? "bg-accent text-white" : "text-secondary hover:text-foreground"
      }`}
    >
      {icon}
      {label}
    </button>
  );
}

function PairSelect({
  label,
  value,
  videos,
  onChange,
}: {
  label: string;
  value: number;
  videos: Video[];
  onChange: (id: number) => void;
}) {
  return (
    <label className="inline-flex items-center gap-1.5">
      <span className="rounded bg-surface px-1.5 py-0.5 text-xs font-semibold text-secondary">{label}</span>
      <select
        value={value}
        onChange={(event) => onChange(Number(event.target.value))}
        className="max-w-[16rem] rounded-md border border-border bg-surface px-2 py-1 text-sm text-foreground focus:border-accent focus:outline-none"
      >
        {videos.map((video, index) => (
          <option key={video.id} value={video.id}>
            {index + 1}. {displayTitle(video)}
          </option>
        ))}
      </select>
    </label>
  );
}

function SyncedComparison({
  mode,
  left,
  right,
  keepVideoIds,
}: {
  mode: Exclude<CompareMode, "frames">;
  left: Video;
  right: Video;
  keepVideoIds: Set<number>;
}) {
  const leftRef = useRef<HTMLVideoElement>(null);
  const rightRef = useRef<HTMLVideoElement>(null);
  const [playing, setPlaying] = useState(false);
  const [time, setTime] = useState(0);
  const [offset, setOffset] = useState(0);
  const [audio, setAudio] = useState<AudioSource>("none");
  const [split, setSplit] = useState(50);
  const [dragging, setDragging] = useState(false);
  const [transcoded, setTranscoded] = useState<{ a: boolean; b: boolean }>({ a: false, b: false });
  const [errors, setErrors] = useState<{ a: boolean; b: boolean }>({ a: false, b: false });
  const leftDuration = primaryFile(left)?.duration ?? 0;
  const rightDuration = primaryFile(right)?.duration ?? 0;
  const duration = Math.max(leftDuration, 0.1);
  const offsetRef = useRef(offset);
  offsetRef.current = offset;

  const sources = useMemo(
    () => ({
      a: transcoded.a
        ? videosApi.transcodeUrl(left.id, undefined, 0, primaryFile(left)?.id)
        : videosApi.streamUrl(left.id, primaryFile(left)?.id),
      b: transcoded.b
        ? videosApi.transcodeUrl(right.id, undefined, 0, primaryFile(right)?.id)
        : videosApi.streamUrl(right.id, primaryFile(right)?.id),
    }),
    [left, right, transcoded],
  );

  // Seeking large files is slow, so a follower that is re-seeked on every small drift never catches up.
  // Small drift is absorbed by nudging B's playback rate; only a large jump (or an explicit seek) seeks.
  const syncFollower = useCallback((force = false) => {
    const master = leftRef.current;
    const follower = rightRef.current;
    if (!master || !follower) return;
    const target = Math.max(0, master.currentTime + offsetRef.current);
    const drift = follower.currentTime - target;
    if (force || Math.abs(drift) > SEEK_DRIFT_SECONDS) {
      if (force || !follower.seeking) follower.currentTime = target;
      follower.playbackRate = master.playbackRate;
    } else if (Math.abs(drift) > DRIFT_TOLERANCE_SECONDS) {
      follower.playbackRate = master.playbackRate * (drift > 0 ? 0.93 : 1.07);
    } else {
      follower.playbackRate = master.playbackRate;
    }
  }, []);

  useEffect(() => {
    if (!playing) return;
    // An interval rather than animation frames keeps the copies aligned even while the page is in the background.
    const timer = window.setInterval(() => {
      const master = leftRef.current;
      if (!master) return;
      setTime(master.currentTime);
      syncFollower();
    }, 100);
    return () => window.clearInterval(timer);
  }, [playing, syncFollower]);

  useEffect(() => {
    syncFollower(true);
  }, [offset, syncFollower]);

  useEffect(() => {
    if (leftRef.current) leftRef.current.muted = audio !== "a";
    if (rightRef.current) rightRef.current.muted = audio !== "b";
  }, [audio]);

  useEffect(() => {
    const master = leftRef.current;
    const follower = rightRef.current;
    return () => {
      master?.pause();
      follower?.pause();
    };
  }, []);

  const togglePlay = async () => {
    const master = leftRef.current;
    const follower = rightRef.current;
    if (!master || !follower) return;
    if (playing) {
      master.pause();
      follower.pause();
      setPlaying(false);
      syncFollower(true);
      return;
    }
    syncFollower(true);
    try {
      await Promise.all([master.play(), follower.play()]);
      setPlaying(true);
    } catch {
      master.pause();
      follower.pause();
      setPlaying(false);
    }
  };

  const seek = (seconds: number) => {
    const master = leftRef.current;
    if (!master) return;
    master.currentTime = Math.max(0, Math.min(duration, seconds));
    setTime(master.currentTime);
    syncFollower(true);
  };

  const updateSplit = (event: ReactPointerEvent<HTMLDivElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    setSplit(Math.min(100, Math.max(0, ((event.clientX - rect.left) / Math.max(1, rect.width)) * 100)));
  };

  const videoClass = "absolute inset-0 h-full w-full object-contain";
  const leftVideo = (
    <video
      ref={leftRef}
      src={sources.a}
      muted={audio !== "a"}
      playsInline
      preload="auto"
      className={videoClass}
      onError={() => setErrors((current) => ({ ...current, a: true }))}
      onLoadedData={() => setErrors((current) => ({ ...current, a: false }))}
      onEnded={() => setPlaying(false)}
    />
  );
  const rightVideo = (
    <video
      ref={rightRef}
      src={sources.b}
      muted={audio !== "b"}
      playsInline
      preload="auto"
      className={videoClass}
      style={mode === "slider" ? { clipPath: `inset(0 0 0 ${split}%)` } : undefined}
      onError={() => setErrors((current) => ({ ...current, b: true }))}
      onLoadedData={() => {
        setErrors((current) => ({ ...current, b: false }));
        syncFollower(true);
      }}
    />
  );

  return (
    <div className="space-y-3">
      {mode === "slider" ? (
        <div
          className={`relative mx-auto aspect-video max-h-[calc(92vh-15rem)] w-full select-none overflow-hidden rounded-lg bg-black ${dragging ? "cursor-ew-resize" : "cursor-col-resize"}`}
          onPointerDown={(event) => {
            event.currentTarget.setPointerCapture(event.pointerId);
            setDragging(true);
            updateSplit(event);
          }}
          onPointerMove={(event) => {
            if (dragging) updateSplit(event);
          }}
          onPointerUp={() => setDragging(false)}
          onDoubleClick={togglePlay}
        >
          {leftVideo}
          {rightVideo}
          <div
            className="pointer-events-none absolute inset-y-0 z-10 w-0.5 bg-white/90 shadow-[0_0_8px_rgba(0,0,0,0.8)]"
            style={{ left: `${split}%` }}
          >
            <div className="absolute top-1/2 left-1/2 flex h-8 w-8 -translate-x-1/2 -translate-y-1/2 items-center justify-center rounded-full bg-white text-black shadow-lg">
              <SplitSquareHorizontal className="h-4 w-4" />
            </div>
          </div>
          <SideLabel side="A" video={left} keep={keepVideoIds.has(left.id)} className="left-3" />
          <SideLabel side="B" video={right} keep={keepVideoIds.has(right.id)} className="right-3" />
          <PlaybackError
            visible={errors.a}
            side="A"
            video={left}
            className="left-3"
            onTranscode={() => setTranscoded((current) => ({ ...current, a: true }))}
            transcoded={transcoded.a}
          />
          <PlaybackError
            visible={errors.b}
            side="B"
            video={right}
            className="right-3"
            onTranscode={() => setTranscoded((current) => ({ ...current, b: true }))}
            transcoded={transcoded.b}
          />
        </div>
      ) : (
        <div className="grid gap-3 md:grid-cols-2">
          {[
            {
              side: "A" as const,
              video: left,
              element: leftVideo,
              error: errors.a,
              isTranscoded: transcoded.a,
              transcode: () => setTranscoded((current) => ({ ...current, a: true })),
            },
            {
              side: "B" as const,
              video: right,
              element: rightVideo,
              error: errors.b,
              isTranscoded: transcoded.b,
              transcode: () => setTranscoded((current) => ({ ...current, b: true })),
            },
          ].map((entry) => (
            <div
              key={entry.side}
              className="relative aspect-video max-h-[calc(92vh-15rem)] overflow-hidden rounded-lg bg-black"
              onDoubleClick={togglePlay}
            >
              {entry.element}
              <SideLabel
                side={entry.side}
                video={entry.video}
                keep={keepVideoIds.has(entry.video.id)}
                className="left-3"
              />
              <PlaybackError
                visible={entry.error}
                side={entry.side}
                video={entry.video}
                className="left-3"
                onTranscode={entry.transcode}
                transcoded={entry.isTranscoded}
              />
            </div>
          ))}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card px-3 py-2">
        <button
          type="button"
          onClick={togglePlay}
          className="flex h-9 w-9 items-center justify-center rounded-full bg-accent text-white hover:bg-accent-hover"
          aria-label={playing ? "Pause both" : "Play both"}
        >
          {playing ? <Pause className="h-4 w-4" /> : <Play className="h-4 w-4 translate-x-px" />}
        </button>
        <span className="w-24 text-sm tabular-nums text-secondary">
          {formatDuration(time)} / {formatDuration(duration)}
        </span>
        <input
          type="range"
          min={0}
          max={duration}
          step={0.1}
          value={Math.min(time, duration)}
          onChange={(event) => seek(Number(event.target.value))}
          className="min-w-[10rem] flex-1 accent-accent"
          aria-label="Seek both copies"
        />
        <div
          className="flex items-center gap-1 text-xs text-secondary"
          title="Shift B's timeline to line up copies that were trimmed differently"
        >
          <span className="mr-1">B offset</span>
          {[-1, -0.1].map((step) => (
            <NudgeButton
              key={step}
              label={`${step}s`}
              onClick={() => setOffset((value) => Math.round((value + step) * 10) / 10)}
            />
          ))}
          <span className="w-12 text-center tabular-nums text-foreground">
            {offset > 0 ? "+" : ""}
            {offset.toFixed(1)}s
          </span>
          {[0.1, 1].map((step) => (
            <NudgeButton
              key={step}
              label={`+${step}s`}
              onClick={() => setOffset((value) => Math.round((value + step) * 10) / 10)}
            />
          ))}
          {offset !== 0 ? <NudgeButton label="Reset" onClick={() => setOffset(0)} /> : null}
        </div>
        <div className="flex items-center gap-1 text-xs" role="radiogroup" aria-label="Audio">
          {(["none", "a", "b"] as AudioSource[]).map((source) => (
            <button
              key={source}
              type="button"
              role="radio"
              aria-checked={audio === source}
              onClick={() => setAudio(source)}
              className={`inline-flex items-center gap-1 rounded-md px-2 py-1 ${audio === source ? "bg-surface text-foreground" : "text-muted hover:text-foreground"}`}
            >
              {source === "none" ? <VolumeX className="h-3.5 w-3.5" /> : <Volume2 className="h-3.5 w-3.5" />}
              {source === "none" ? "Mute" : source.toUpperCase()}
            </button>
          ))}
        </div>
      </div>
      {Math.abs(leftDuration - rightDuration) >= 1 ? (
        <p className="text-xs text-amber-300">
          The copies differ in length by {Math.round(Math.abs(leftDuration - rightDuration))} seconds. Use the B offset
          to line up the same moment in both.
        </p>
      ) : null}
    </div>
  );
}

function NudgeButton({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="rounded border border-border bg-surface px-1.5 py-0.5 hover:border-accent hover:text-foreground"
    >
      {label}
    </button>
  );
}

function SideLabel({ side, video, keep, className }: { side: string; video: Video; keep: boolean; className: string }) {
  const file = primaryFile(video);
  return (
    <div
      className={`pointer-events-none absolute top-3 z-20 max-w-[45%] rounded-md bg-black/75 px-2 py-1 text-xs text-white shadow ${className}`}
    >
      <div className="flex items-center gap-1.5 font-semibold">
        <span className="rounded bg-white/20 px-1">{side}</span>
        <span className={keep ? "text-emerald-300" : "text-red-300"}>{keep ? "Keep" : "Remove"}</span>
      </div>
      {file ? (
        <div className="mt-0.5 truncate text-white/80">
          {file.width}×{file.height} · {formatCodec(file.videoCodec)} · {formatBitrate(file.bitRate)} ·{" "}
          {formatFileSize(totalSize(video))}
        </div>
      ) : null}
    </div>
  );
}

function PlaybackError({
  visible,
  side,
  video,
  className,
  onTranscode,
  transcoded,
}: {
  visible: boolean;
  side: string;
  video: Video;
  className: string;
  onTranscode: () => void;
  transcoded: boolean;
}) {
  if (!visible) return null;
  const file = primaryFile(video);
  return (
    <div
      className={`absolute bottom-3 z-20 max-w-[45%] rounded-md border border-red-800 bg-red-950/90 px-3 py-2 text-xs text-red-100 ${className}`}
    >
      <div className="flex items-center gap-1.5 font-medium">
        <AlertTriangle className="h-3.5 w-3.5" />
        {side} can’t play {transcoded ? "even when transcoded" : "directly in this browser"}
      </div>
      {!transcoded ? (
        <>
          <div className="mt-0.5 text-red-200/80">
            {file ? `${file.format.toUpperCase()} · ${formatCodec(file.videoCodec)}` : "Unknown format"}
          </div>
          <button
            type="button"
            onClick={onTranscode}
            className="mt-1.5 rounded bg-red-800 px-2 py-0.5 text-white hover:bg-red-700"
          >
            Transcode for comparison
          </button>
        </>
      ) : null}
    </div>
  );
}

function FrameStrips({ videos, keepVideoIds }: { videos: Video[]; keepVideoIds: Set<number> }) {
  const [alignment, setAlignment] = useState<"relative" | "absolute">("relative");
  const shortest = Math.min(
    ...videos.map((video) => primaryFile(video)?.duration ?? 0).filter((duration) => duration > 0),
  );
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
        <p className="text-secondary">
          The same moments from every copy, lined up so cuts, intros and watermarks stand out.
        </p>
        <div className="flex rounded-lg border border-border bg-card p-0.5 text-xs">
          <button
            type="button"
            onClick={() => setAlignment("relative")}
            className={`rounded-md px-2 py-1 ${alignment === "relative" ? "bg-surface text-foreground" : "text-muted"}`}
            title="Frames at the same percentage of each copy's length"
          >
            Same position
          </button>
          <button
            type="button"
            onClick={() => setAlignment("absolute")}
            className={`rounded-md px-2 py-1 ${alignment === "absolute" ? "bg-surface text-foreground" : "text-muted"}`}
            title="Frames at the same timestamp in every copy"
          >
            Same timestamp
          </button>
        </div>
      </div>
      <div className="overflow-x-auto">
        <div className="min-w-[56rem] space-y-3">
          {videos.map((video, index) => {
            const duration = primaryFile(video)?.duration ?? 0;
            return (
              <div key={video.id}>
                <div className="mb-1 flex items-center gap-2 text-xs">
                  <span className="rounded bg-surface px-1.5 py-0.5 font-semibold text-secondary">{index + 1}</span>
                  <span className={keepVideoIds.has(video.id) ? "font-medium text-emerald-300" : "text-red-300"}>
                    {keepVideoIds.has(video.id) ? "Keep" : "Remove"}
                  </span>
                  <span className="truncate text-secondary">{displayTitle(video)}</span>
                  <span className="text-muted">{formatDuration(duration)}</span>
                </div>
                <div className="grid grid-cols-8 gap-1">
                  {FRAME_POSITIONS.map((position) => {
                    const seconds =
                      alignment === "relative"
                        ? duration * position
                        : (Number.isFinite(shortest) ? shortest : duration) * position;
                    return (
                      <figure key={position} className="relative aspect-video overflow-hidden rounded bg-black">
                        {duration > 0 ? (
                          <img
                            src={videosApi.screenshotUrl(
                              video.id,
                              video.updatedAt,
                              Math.max(0, Math.min(duration - 0.5, seconds)),
                            )}
                            alt=""
                            loading="lazy"
                            className="h-full w-full object-cover"
                          />
                        ) : null}
                        <figcaption className="absolute bottom-0.5 right-0.5 rounded bg-black/70 px-1 text-[10px] tabular-nums text-white">
                          {formatDuration(seconds)}
                        </figcaption>
                      </figure>
                    );
                  })}
                </div>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
