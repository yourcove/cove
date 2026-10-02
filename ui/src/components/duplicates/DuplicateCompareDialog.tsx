import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from "react";
import {
  AlertTriangle,
  Check,
  Columns2,
  Film,
  Maximize2,
  Minimize2,
  Pause,
  Play,
  SplitSquareHorizontal,
  Volume2,
  VolumeX,
  ZoomIn,
} from "lucide-react";
import { videos as videosApi } from "../../api/client";
import { transcodeSource } from "../../utils/transcodeSource";
import type { Video } from "../../api/types";
import { formatDuration, formatFileSize } from "../shared";
import {
  clampView,
  detailShown,
  FIT_VIEW,
  formatDetailShown,
  nativeZoom,
  zoomAround,
  type Size,
  type View,
} from "./compareView";
import { DuplicateDialog } from "./DuplicateDialog";
import {
  copyKey,
  displayTitle,
  formatBitrate,
  formatCodec,
  isFileCopy,
  primaryFile,
  totalSize,
  type DuplicateCopy,
} from "./duplicateModel";

type CompareMode = "slider" | "side" | "frames";
type AudioSource = "a" | "b" | "none";

const DRIFT_TOLERANCE_SECONDS = 0.04;
const SEEK_DRIFT_SECONDS = 0.6;
const FRAME_POSITIONS = [0.05, 0.18, 0.31, 0.44, 0.57, 0.7, 0.83, 0.95];
const ZOOM_PRESETS = [2, 4];
const DIVIDER_GRAB_PX = 18;

type DragKind = "split" | "pan";

export function DuplicateCompareDialog({
  open,
  videos,
  keepVideoIds,
  initialPair,
  onClose,
  onKeepOnly,
}: {
  open: boolean;
  /** The group's members; in a files search each is the video narrowed to one of its files. */
  videos: DuplicateCopy[];
  /** Keys of the members to keep (see `copyKey`). */
  keepVideoIds: Set<number>;
  initialPair?: [number, number];
  onClose: () => void;
  /** Keeps just this member in the group; omitted when the group can no longer be changed. */
  onKeepOnly?: (memberKey: number) => void;
}) {
  const fileCopies = videos.some(isFileCopy);
  const [mode, setMode] = useState<CompareMode>("slider");
  const [pair, setPair] = useState<[number, number]>(() => initialPair ?? defaultPair(videos));
  const [pairResetKey, setPairResetKey] = useState({ open: false, initialPair, videos });
  if (pairResetKey.open !== open || pairResetKey.initialPair !== initialPair || pairResetKey.videos !== videos) {
    setPairResetKey({ open, initialPair, videos });
    if (open) setPair(initialPair ?? defaultPair(videos));
  }
  // Frame strips come from the video's generated thumbnails, which only ever show its primary file.
  if (fileCopies && mode === "frames") setMode("slider");
  const left = videos.find((video) => copyKey(video) === pair[0]) ?? videos[0];
  const right = videos.find((video) => copyKey(video) === pair[1]) ?? videos[1] ?? videos[0];

  return (
    <DuplicateDialog
      open={open}
      onClose={onClose}
      size="xl"
      title="Compare copies"
      subtitle="Drag the divider or play both copies in sync to spot cuts and watermarks. To judge sharpness, zoom to 1:1 — fitted to this window, a 4K copy is shrunk and its extra detail never reaches the screen."
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
            {fileCopies ? null : (
              <ModeButton
                active={mode === "frames"}
                onClick={() => setMode("frames")}
                icon={<Film className="h-4 w-4" />}
                label="Frames"
              />
            )}
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
            key={`${copyKey(left)}-${copyKey(right)}-${mode}`}
            mode={mode}
            left={left}
            right={right}
            keepVideoIds={keepVideoIds}
            onKeepOnly={onKeepOnly}
          />
        ) : null}
      </div>
    </DuplicateDialog>
  );
}

function defaultPair(videos: DuplicateCopy[]): [number, number] {
  const first = videos[0] ? copyKey(videos[0]) : 0;
  return [first, videos[1] ? copyKey(videos[1]) : first];
}

/** A member's name: its file for a file copy (every copy shares the video's title), otherwise the video title. */
function memberName(video: DuplicateCopy) {
  return isFileCopy(video) ? (primaryFile(video)?.basename ?? displayTitle(video)) : displayTitle(video);
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
  videos: DuplicateCopy[];
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
          <option key={copyKey(video)} value={copyKey(video)}>
            {index + 1}. {memberName(video)}
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
  onKeepOnly,
}: {
  mode: Exclude<CompareMode, "frames">;
  left: DuplicateCopy;
  right: DuplicateCopy;
  keepVideoIds: Set<number>;
  onKeepOnly?: (memberKey: number) => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const secondStageRef = useRef<HTMLDivElement>(null);
  const leftRef = useRef<HTMLVideoElement>(null);
  const rightRef = useRef<HTMLVideoElement>(null);
  const [playing, setPlaying] = useState(false);
  const [time, setTime] = useState(0);
  const [offset, setOffset] = useState(0);
  const [audio, setAudio] = useState<AudioSource>("none");
  const [split, setSplit] = useState(50);
  const [dragging, setDragging] = useState<DragKind | null>(null);
  const [overDivider, setOverDivider] = useState(false);
  const [view, setView] = useState<View>(FIT_VIEW);
  const [stageSize, setStageSize] = useState<Size>({ width: 0, height: 0 });
  const [natural, setNatural] = useState<{ a?: Size; b?: Size }>(() => ({
    a: fileSize(primaryFile(left)),
    b: fileSize(primaryFile(right)),
  }));
  const [fullscreen, setFullscreen] = useState(false);
  const [transcoded, setTranscoded] = useState<{ a: boolean; b: boolean }>({ a: false, b: false });
  const [errors, setErrors] = useState<{ a: boolean; b: boolean }>({ a: false, b: false });
  const leftDuration = primaryFile(left)?.duration ?? 0;
  const rightDuration = primaryFile(right)?.duration ?? 0;
  const duration = Math.max(leftDuration, 0.1);
  const offsetRef = useRef(offset);
  useLayoutEffect(() => {
    offsetRef.current = offset;
  }, [offset]);
  // measureStage keeps this in step with stageSize, which it is the only writer of.
  const stageSizeRef = useRef(stageSize);
  const dragRef = useRef<{ kind: DragKind; pointerX: number; pointerY: number; view: View } | null>(null);
  const pixelRatio = typeof window === "undefined" ? 1 : window.devicePixelRatio || 1;

  const sources = useMemo(
    () => ({
      a: transcoded.a
        ? transcodeSource(left.id, undefined, 0, primaryFile(left)?.id).url
        : videosApi.streamUrl(left.id, primaryFile(left)?.id),
      b: transcoded.b
        ? transcodeSource(right.id, undefined, 0, primaryFile(right)?.id).url
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

  // Both copies always share one stage size (the slider overlays them; side by side uses equal columns),
  // so measuring the first stage is enough to place and clamp the zoomed picture for both.
  const measureStage = useCallback((): Size => {
    const stage = stageRef.current;
    if (!stage) return stageSizeRef.current;
    const rect = stage.getBoundingClientRect();
    const measured = { width: rect.width, height: rect.height };
    stageSizeRef.current = measured;
    setStageSize((current) =>
      current.width === measured.width && current.height === measured.height ? current : measured,
    );
    return measured;
  }, []);

  // ResizeObserver only reports while the page is rendering frames, so zoom and pan also measure the stage
  // themselves rather than trusting a size that may predate the last layout change.
  useEffect(() => {
    const stage = stageRef.current;
    if (!stage) return;
    measureStage();
    window.addEventListener("resize", measureStage);
    const observer = typeof ResizeObserver === "undefined" ? undefined : new ResizeObserver(() => measureStage());
    observer?.observe(stage);
    return () => {
      window.removeEventListener("resize", measureStage);
      observer?.disconnect();
    };
  }, [fullscreen, measureStage]);

  const [prevStageSize, setPrevStageSize] = useState(stageSize);
  if (stageSize !== prevStageSize) {
    setPrevStageSize(stageSize);
    setView((current) => clampView(current, stageSize));
  }

  // React attaches wheel listeners as passive, so preventing the dialog from scrolling needs a native listener.
  useEffect(() => {
    const targets = [stageRef.current, secondStageRef.current].filter((target): target is HTMLDivElement => !!target);
    const onWheel = (event: WheelEvent) => {
      const target = event.currentTarget as HTMLElement;
      event.preventDefault();
      const rect = target.getBoundingClientRect();
      const point = { x: event.clientX - rect.left - rect.width / 2, y: event.clientY - rect.top - rect.height / 2 };
      setView((current) => zoomAround(current, current.zoom * Math.exp(-event.deltaY * 0.0015), point, measureStage()));
    };
    targets.forEach((target) => target.addEventListener("wheel", onWheel, { passive: false }));
    return () => targets.forEach((target) => target.removeEventListener("wheel", onWheel));
  }, [fullscreen, measureStage]);

  useEffect(() => {
    const onChange = () => setFullscreen(document.fullscreenElement === containerRef.current);
    document.addEventListener("fullscreenchange", onChange);
    return () => document.removeEventListener("fullscreenchange", onChange);
  }, []);

  const canFullscreen = typeof document !== "undefined" && document.fullscreenEnabled === true;
  const toggleFullscreen = () => {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void containerRef.current?.requestFullscreen();
  };

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

  const nearDivider = (event: ReactPointerEvent<HTMLDivElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    return Math.abs(event.clientX - (rect.left + (rect.width * split) / 100)) <= DIVIDER_GRAB_PX;
  };

  const beginDrag = (event: ReactPointerEvent<HTMLDivElement>, kind: DragKind) => {
    if (event.button !== 0) return;
    event.currentTarget.setPointerCapture?.(event.pointerId);
    measureStage();
    dragRef.current = { kind, pointerX: event.clientX, pointerY: event.clientY, view };
    setDragging(kind);
    if (kind === "split") updateSplit(event);
  };

  const continueDrag = (event: ReactPointerEvent<HTMLDivElement>) => {
    const drag = dragRef.current;
    if (!drag) return false;
    if (drag.kind === "split") updateSplit(event);
    else
      setView(
        clampView(
          {
            ...drag.view,
            x: drag.view.x + event.clientX - drag.pointerX,
            y: drag.view.y + event.clientY - drag.pointerY,
          },
          stageSizeRef.current,
        ),
      );
    return true;
  };

  const endDrag = () => {
    dragRef.current = null;
    setDragging(null);
  };

  const zoomTo = (target: (stage: Size) => number) => {
    const stage = measureStage();
    setView((current) => zoomAround(current, target(stage), { x: 0, y: 0 }, stage));
  };
  const shownA = detailShown(natural.a, stageSize, view.zoom, pixelRatio);
  const shownB = detailShown(natural.b, stageSize, view.zoom, pixelRatio);
  // "1:1" gives the sharper copy one screen pixel per source pixel, the least zoom that shows all of its detail.
  const oneToOne = nativeZoom([natural.a, natural.b], stageSize, pixelRatio);
  const zoomed = view.zoom > 1.001;

  const layerStyle = {
    width: `${view.zoom * 100}%`,
    height: `${view.zoom * 100}%`,
    left: `calc(50% - ${view.zoom * 50}% + ${view.x}px)`,
    top: `calc(50% - ${view.zoom * 50}% + ${view.y}px)`,
  };
  const videoClass = "h-full w-full object-contain";
  const leftVideo = (
    <div className="absolute" style={layerStyle}>
      <video
        ref={leftRef}
        src={sources.a}
        muted={audio !== "a"}
        playsInline
        preload="auto"
        className={videoClass}
        onError={() => setErrors((current) => ({ ...current, a: true }))}
        onLoadedMetadata={(event) => {
          const { videoWidth, videoHeight } = event.currentTarget;
          if (videoWidth > 0 && videoHeight > 0)
            setNatural((current) => ({ ...current, a: { width: videoWidth, height: videoHeight } }));
        }}
        onLoadedData={() => setErrors((current) => ({ ...current, a: false }))}
        onEnded={() => setPlaying(false)}
      />
    </div>
  );
  const rightVideo = (
    <div className="absolute" style={layerStyle}>
      <video
        ref={rightRef}
        src={sources.b}
        muted={audio !== "b"}
        playsInline
        preload="auto"
        className={videoClass}
        onError={() => setErrors((current) => ({ ...current, b: true }))}
        onLoadedMetadata={(event) => {
          const { videoWidth, videoHeight } = event.currentTarget;
          if (videoWidth > 0 && videoHeight > 0)
            setNatural((current) => ({ ...current, b: { width: videoWidth, height: videoHeight } }));
        }}
        onLoadedData={() => {
          setErrors((current) => ({ ...current, b: false }));
          syncFollower(true);
        }}
      />
    </div>
  );

  const stageSizing = fullscreen ? "min-h-0 flex-1" : "mx-auto aspect-video max-h-[calc(92vh-18rem)] min-h-48";
  const panCursor = dragging === "pan" ? "cursor-grabbing" : zoomed ? "cursor-grab" : "";

  return (
    <div ref={containerRef} className={fullscreen ? "flex h-full flex-col gap-3 bg-black p-3" : "space-y-3"}>
      {mode === "slider" ? (
        <div
          ref={stageRef}
          data-testid="compare-stage"
          className={`relative w-full select-none overflow-hidden rounded-lg bg-black ${stageSizing} ${
            dragging === "split"
              ? "cursor-ew-resize"
              : dragging === "pan"
                ? "cursor-grabbing"
                : overDivider || !zoomed
                  ? "cursor-col-resize"
                  : "cursor-grab"
          }`}
          onPointerDown={(event) => beginDrag(event, nearDivider(event) || !zoomed ? "split" : "pan")}
          onPointerMove={(event) => {
            if (!continueDrag(event)) setOverDivider(nearDivider(event));
          }}
          onPointerUp={endDrag}
          onPointerCancel={endDrag}
          onPointerLeave={() => setOverDivider(false)}
          onDoubleClick={togglePlay}
        >
          <div className="absolute inset-0 overflow-hidden">{leftVideo}</div>
          {/* The clip sits on an untransformed wrapper so the divider stays where it is drawn at any zoom. */}
          <div className="absolute inset-0 overflow-hidden" style={{ clipPath: `inset(0 0 0 ${split}%)` }}>
            {rightVideo}
          </div>
          <div
            className="pointer-events-none absolute inset-y-0 z-10 w-0.5 bg-white/90 shadow-[0_0_8px_rgba(0,0,0,0.8)]"
            style={{ left: `${split}%` }}
          >
            <div className="absolute top-1/2 left-1/2 flex h-8 w-8 -translate-x-1/2 -translate-y-1/2 items-center justify-center rounded-full bg-white text-black shadow-lg">
              <SplitSquareHorizontal className="h-4 w-4" />
            </div>
          </div>
          <SideLabel
            side="A"
            video={left}
            keep={keepVideoIds.has(copyKey(left))}
            reencoded={transcoded.a}
            className="left-3"
          />
          <SideLabel
            side="B"
            video={right}
            keep={keepVideoIds.has(copyKey(right))}
            reencoded={transcoded.b}
            className="right-3"
          />
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
        <div className={`grid gap-3 md:grid-cols-2 ${fullscreen ? "min-h-0 flex-1" : ""}`}>
          {[
            {
              side: "A" as const,
              video: left,
              element: leftVideo,
              ref: stageRef,
              error: errors.a,
              isTranscoded: transcoded.a,
              transcode: () => setTranscoded((current) => ({ ...current, a: true })),
            },
            {
              side: "B" as const,
              video: right,
              element: rightVideo,
              ref: secondStageRef,
              error: errors.b,
              isTranscoded: transcoded.b,
              transcode: () => setTranscoded((current) => ({ ...current, b: true })),
            },
          ].map((entry) => (
            <div
              key={entry.side}
              ref={entry.ref}
              data-testid={entry.side === "A" ? "compare-stage" : undefined}
              className={`relative select-none overflow-hidden rounded-lg bg-black ${
                fullscreen ? "h-full" : "aspect-video max-h-[calc(92vh-18rem)] min-h-48"
              } ${panCursor}`}
              onPointerDown={(event) => {
                if (zoomed) beginDrag(event, "pan");
              }}
              onPointerMove={continueDrag}
              onPointerUp={endDrag}
              onPointerCancel={endDrag}
              onDoubleClick={togglePlay}
            >
              {entry.element}
              <SideLabel
                side={entry.side}
                video={entry.video}
                keep={keepVideoIds.has(copyKey(entry.video))}
                reencoded={entry.isTranscoded}
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

      <div className="flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card px-3 py-2 text-xs">
        <div className="flex items-center gap-1" role="group" aria-label="Zoom">
          <ZoomIn className="mr-1 h-3.5 w-3.5 text-secondary" />
          <ZoomButton label="Fit" active={!zoomed} onClick={() => setView(FIT_VIEW)} />
          <ZoomButton
            label="1:1"
            title="One screen pixel for every pixel of the sharper copy"
            active={zoomed && Math.abs(view.zoom - oneToOne) < 0.01}
            onClick={() => zoomTo((stage) => nativeZoom([natural.a, natural.b], stage, pixelRatio))}
          />
          {ZOOM_PRESETS.map((zoom) => (
            <ZoomButton
              key={zoom}
              label={`${zoom}×`}
              active={Math.abs(view.zoom - zoom) < 0.01}
              onClick={() => zoomTo(() => zoom)}
            />
          ))}
          <span className="ml-1 w-10 tabular-nums text-muted">{view.zoom.toFixed(1)}×</span>
        </div>
        <span
          className="text-secondary"
          title="How much of each copy's own resolution reaches your screen. Below 100% a copy is being shrunk, so zoom until the sharper copy shows full detail before judging quality."
          data-testid="detail-shown"
        >
          Detail on screen: A {formatDetailShown(shownA)} · B {formatDetailShown(shownB)}
        </span>
        <span className="text-muted">Scroll to zoom · drag to pan</span>
        <div className="ml-auto flex items-center gap-1.5">
          {onKeepOnly
            ? [
                { side: "A", video: left },
                { side: "B", video: right },
              ].map((entry) => {
                const soleKeeper = keepVideoIds.size === 1 && keepVideoIds.has(copyKey(entry.video));
                return (
                  <button
                    key={entry.side}
                    type="button"
                    disabled={soleKeeper}
                    onClick={() => onKeepOnly(copyKey(entry.video))}
                    className="inline-flex items-center gap-1 rounded-md border border-border bg-surface px-2 py-1 text-foreground hover:border-accent disabled:cursor-default disabled:border-emerald-700 disabled:text-emerald-300"
                  >
                    {soleKeeper ? <Check className="h-3.5 w-3.5" /> : null}
                    {soleKeeper ? `Keeping ${entry.side}` : `Keep only ${entry.side}`}
                  </button>
                );
              })
            : null}
          {canFullscreen ? (
            <button
              type="button"
              onClick={toggleFullscreen}
              className="inline-flex items-center gap-1 rounded-md border border-border bg-surface px-2 py-1 text-foreground hover:border-accent"
              aria-label={fullscreen ? "Exit full screen" : "Full screen"}
            >
              {fullscreen ? <Minimize2 className="h-3.5 w-3.5" /> : <Maximize2 className="h-3.5 w-3.5" />}
              {fullscreen ? "Exit full screen" : "Full screen"}
            </button>
          ) : null}
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

function fileSize(file: { width?: number; height?: number } | undefined): Size | undefined {
  return file?.width && file.height ? { width: file.width, height: file.height } : undefined;
}

function ZoomButton({
  label,
  title,
  active,
  onClick,
}: {
  label: string;
  title?: string;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      title={title}
      aria-pressed={active}
      onClick={onClick}
      className={`rounded border px-1.5 py-0.5 ${
        active
          ? "border-accent bg-accent text-white"
          : "border-border bg-surface hover:border-accent hover:text-foreground"
      }`}
    >
      {label}
    </button>
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

function SideLabel({
  side,
  video,
  keep,
  reencoded,
  className,
}: {
  side: string;
  video: DuplicateCopy;
  keep: boolean;
  reencoded: boolean;
  className: string;
}) {
  const file = primaryFile(video);
  const fileCopy = isFileCopy(video);
  return (
    <div
      className={`pointer-events-none absolute top-3 z-20 max-w-[45%] rounded-md bg-black/75 px-2 py-1 text-xs text-white shadow ${className}`}
    >
      <div className="flex items-center gap-1.5 font-semibold">
        <span className="rounded bg-white/20 px-1">{side}</span>
        <span className={keep ? "text-emerald-300" : "text-red-300"}>{keep ? "Keep" : "Remove"}</span>
        {video.isPrimaryFile ? <span className="font-normal text-white/70">· primary</span> : null}
      </div>
      {fileCopy && file ? <div className="mt-0.5 truncate text-white/90">{file.basename}</div> : null}
      {file ? (
        <div className="mt-0.5 truncate text-white/80">
          {file.width}×{file.height} · {formatCodec(file.videoCodec)} · {formatBitrate(file.bitRate)} ·{" "}
          {formatFileSize(totalSize(video))}
        </div>
      ) : null}
      {reencoded ? (
        <div className="mt-0.5 text-amber-200">
          Re-encoded for playback, so its compression is not the original&apos;s
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
              <div key={copyKey(video)}>
                <div className="mb-1 flex items-center gap-2 text-xs">
                  <span className="rounded bg-surface px-1.5 py-0.5 font-semibold text-secondary">{index + 1}</span>
                  <span className={keepVideoIds.has(copyKey(video)) ? "font-medium text-emerald-300" : "text-red-300"}>
                    {keepVideoIds.has(copyKey(video)) ? "Keep" : "Remove"}
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
