import { useEffect, useRef, useState } from "react";
import { Bookmark, Clapperboard } from "lucide-react";
import { videoAlignments, videos } from "../api/client";
import type { AlignmentAnchor, AlignmentAssessment, AlignmentPreview } from "../api/alignmentTypes";
import { WallMediaCard } from "./WallMediaCard";

const control = "rounded border border-border bg-input px-3 py-2 text-sm disabled:opacity-50";
const seconds = (n: number) => `${Math.floor(n / 60)}:${(n % 60).toFixed(2).padStart(5, "0")}`;

export function VideoAlignmentDialog({ videoId, targetFileId = 0, onClose, onApplied = () => {} }: {
  videoId: number;
  targetFileId?: number;
  onClose: () => void;
  onApplied?: () => void;
}) {
  const [assessment, setAssessment] = useState<AlignmentAssessment>();
  const [anchors, setAnchors] = useState<AlignmentAnchor[]>([]);
  const [preview, setPreview] = useState<AlignmentPreview>();
  const [deletions, setDeletions] = useState<Set<string>>(new Set());
  const [activeComparison, setActiveComparison] = useState("");
  const [busy, setBusy] = useState("Checking fingerprints and affected content…");
  const [error, setError] = useState("");
  const operation = useRef<AbortController | undefined>(undefined);

  useEffect(() => {
    const abort = new AbortController();
    operation.current = abort;
    videoAlignments.assess(videoId, targetFileId, abort.signal).then((value) => {
      setAssessment(value);
      setBusy("");
    }).catch((reason) => {
      if (!abort.signal.aborted) { setError(reason instanceof Error ? reason.message : String(reason)); setBusy(""); }
    });
    return () => operation.current?.abort();
  }, [targetFileId, videoId]);

  async function run(label: string, action: (signal: AbortSignal) => Promise<void>) {
    const abort = new AbortController();
    operation.current = abort;
    setBusy(label);
    setError("");
    try { await action(abort.signal); } catch (reason) { if (!abort.signal.aborted) setError(reason instanceof Error ? reason.message : String(reason)); }
    finally { if (operation.current === abort) { setBusy(""); operation.current = undefined; } }
  }

  async function apply(resolution: "direct" | "align" | "delete", signal: AbortSignal) {
    await videoAlignments.apply(videoId, {
      fileId: targetFileId,
      resolution,
      anchors: resolution === "align" ? anchors : undefined,
      deleteDependencies: resolution === "align" ? [...deletions].map((key) => { const [kind, id] = key.split(":"); return { kind, id: Number(id) }; }) : undefined,
      expectedPrimaryFileId: assessment!.sourceFileId,
    }, signal);
    onApplied();
    onClose();
  }

  const unresolved = preview?.dependencies.filter((dependency) => !dependency.mapped) ?? [];
  const allResolved = unresolved.every((dependency) => deletions.has(`${dependency.kind}:${dependency.id}`));
  const comparisons = preview ? [
    ...(preview.comparisons ?? []).filter((item) => item.kind === "segment").slice(0, 5),
    ...(preview.comparisons ?? []).filter((item) => item.kind === "clip").slice(0, 5),
  ] : [];
  useEffect(() => {
    const firstKey = comparisons[0] ? `${comparisons[0].kind}:${comparisons[0].id}` : "";
    if (!comparisons.some((item) => `${item.kind}:${item.id}` === activeComparison)) setActiveComparison(firstKey);
  }, [activeComparison, comparisons]);
  const comparisonCounts = preview?.comparisonCounts ?? {
    segment: preview?.dependencies.filter((item) => item.kind === "segment").length ?? 0,
    clip: preview?.dependencies.filter((item) => item.kind === "clip").length ?? 0,
  };
  const dialogIntroduction = !assessment
    ? "Checking whether this change affects timed content…"
    : preview
      ? "Compare the current and proposed timelines before applying any changes."
      : assessment.sourceFileId == null
        ? assessment.dependencyCount > 0
          ? "This video has no current primary file, but some timed content has saved timestamps. Without a previous primary timeline, Cove cannot match or adjust it automatically, so choose whether to remove it or cancel."
          : "This video has no current primary file and no timed content to adjust. The selected file can be set as primary directly."
      : assessment.equivalent
        ? "The selected file matches the current primary file, so existing timestamps can be kept."
        : assessment.dependencyCount === 0
          ? "The selected file does not match the current primary file, but this video has no timed content to adjust."
          : "The selected file does not match the current primary file. The affected timed content uses the current primary timeline, so choose how Cove should handle its timestamps.";
  async function approveAlignment(signal: AbortSignal) {
    const deletedClips = unresolved.filter((dependency) => dependency.kind === "clip" && deletions.has(`clip:${dependency.id}`)).length;
    if (deletedClips > 0 && !window.confirm(`This will permanently delete ${deletedClips} clip video${deletedClips === 1 ? "" : "s"} and any content nested under them. Continue?`)) return;
    await apply("align", signal);
  }
  const close = () => { operation.current?.abort(); onClose(); };
  return (
    <div className="fixed inset-0 z-[120] flex items-center justify-center bg-black/70 p-3">
      <div role="dialog" aria-modal="true" aria-labelledby="alignment-title" className="max-h-[95vh] w-full max-w-6xl overflow-y-auto rounded-xl border border-border bg-background p-5 text-foreground shadow-xl">
        <div className="flex items-start justify-between gap-4">
          <div><h2 id="alignment-title" className="text-xl font-semibold">Set primary video file</h2><p className="mt-1 max-w-3xl text-sm text-secondary">{dialogIntroduction}</p></div>
          <button className={control} onClick={close}>Cancel</button>
        </div>
        {assessment && (assessment.equivalent || assessment.dependencyCount === 0) && <div className="my-4 rounded border border-border bg-surface p-3 text-sm">
          <p>{assessment.sourceDuration == null ? `This video has no primary file. The selected file is ${seconds(assessment.targetDuration)} long.` : `Duration changes from ${seconds(assessment.sourceDuration)} to ${seconds(assessment.targetDuration)}.`}</p>
          <p>{assessment.dependencyCount === 0 ? "No timed content is affected." : "Affected timed content will keep the same timestamps."}</p>
          {assessment.equivalent && <p className="mt-1 text-green-400">The pHashes and durations match within tolerance, so timestamps can be kept.</p>}
        </div>}
        {assessment && (assessment.equivalent || assessment.dependencyCount === 0) && <button disabled={!!busy} className={`${control} bg-accent text-white`} onClick={() => void run("Setting primary file…", (signal) => apply("direct", signal))}>Set as primary</button>}
        {assessment && !assessment.equivalent && assessment.dependencyCount > 0 && !preview && <div className="mt-5 space-y-3">
          <p className="text-xs font-medium uppercase tracking-wide text-secondary">{assessment.sourceFileId == null || !assessment.canAlign ? "Decision required" : "Step 1 of 2 · Choose an approach"}</p>
          <p className="text-sm text-secondary">{assessment.sourceDuration == null ? `The selected file is ${seconds(assessment.targetDuration)} long. There is no current primary duration to compare.` : `Duration changes from ${seconds(assessment.sourceDuration)} to ${seconds(assessment.targetDuration)}.`}</p>
          <DependencyCountList counts={assessment.dependencyCounts} />
          <div className="grid gap-3 md:grid-cols-2">
            <section className="flex flex-col rounded-lg border border-border bg-surface p-4">
              <h3 className="font-medium">Use automated matching</h3>
              <p className="mt-2 flex-1 text-sm text-secondary">Compare samples from positions across both files, confirm one consistent timeline offset, adjust the related timestamps, and show side-by-side previews before anything changes.</p>
              {assessment.sourceFileId != null && assessment.canAlign ? <button disabled={!!busy} className="mt-4 rounded border border-accent bg-accent px-3 py-2 text-sm text-white hover:brightness-110 disabled:opacity-50" onClick={() => void run("Sampling both files and checking the timeline offset…", async (signal) => {
                const value = await videoAlignments.analyze(videoId, assessment.sourceFileId!, assessment.targetFileId, signal);
                if (value.anchors.length < 2) throw new Error(value.message);
                setAnchors(value.anchors);
                setBusy("Preparing side-by-side review…");
                const review = await videoAlignments.preview(videoId, { sourceFileId: assessment.sourceFileId!, targetFileId, sourceDuration: assessment.sourceDuration!, targetDuration: assessment.targetDuration, anchors: value.anchors }, signal);
                if (!Array.isArray(review.comparisons) || !review.comparisonCounts) throw new Error("The alignment review is unavailable. Refresh after the Cove server finishes restarting, then try again.");
                setPreview(review);
              })}>Use automated matching</button> : <p className="mt-4 text-sm text-amber-300">The current or selected file is unavailable, so Cove cannot find matching anchor points.</p>}
            </section>
            <section className="flex flex-col rounded-lg border border-border bg-surface p-4">
              <h3 className="font-medium">Remove all timed content</h3>
              <p className="mt-2 flex-1 text-sm text-secondary">Permanently remove all affected timed content and set the selected file as primary.{(assessment.dependencyCounts.clip ?? 0) > 0 ? " Clip videos and anything nested under them will also be deleted." : ""} This cannot be undone.</p>
              <button disabled={!!busy} className={`${control} mt-4 border-red-500/60 text-red-300 hover:bg-red-500/10`} onClick={() => { if (window.confirm("Permanently delete all affected timed content, including clip videos and anything nested under them, then set this file as primary?")) void run("Deleting timed content and setting primary file…", (signal) => apply("delete", signal)); }}>Remove all timed content</button>
            </section>
          </div>
        </div>}
        {preview && <div className="mt-4 space-y-4">
          <div className="rounded border border-border p-3">
            <p className="mb-1 text-xs font-medium uppercase tracking-wide text-secondary">Step 2 of 2 · Review and approve</p>
            <h3 className="font-medium">Review alignment</h3>
            <p className="mt-1 text-sm text-secondary">Showing {comparisons.filter((item) => item.kind === "segment").length} of {comparisonCounts.segment} segments and {comparisons.filter((item) => item.kind === "clip").length} of {comparisonCounts.clip} clips at their mapped positions.</p>
            <p className="mt-1 text-xs text-secondary">The visible comparison plays both timelines together so you can confirm their timing.</p>
            {comparisons.length > 0 ? <div className="mt-3 max-h-[55vh] space-y-4 overflow-y-auto pr-1">{comparisons.map((item) => {
              const dependency = preview.dependencies.find((candidate) => candidate.kind === item.kind && candidate.id === item.id);
              const sourceRange = reviewPlaybackRange(dependency?.startSec, dependency?.endSec, item.sourceSec, assessment?.sourceDuration);
              const targetRange = reviewPlaybackRange(dependency?.mapped?.startSec, dependency?.mapped?.endSec, item.targetSec, assessment?.targetDuration);
              const comparisonKey = `${item.kind}:${item.id}`;
              return <ComparisonRow key={comparisonKey} label={`Play ${item.kind} ${item.title || "Untitled"} comparison`} active={activeComparison === comparisonKey} onActivate={() => setActiveComparison(comparisonKey)}>
                <div className="mb-2 flex flex-wrap items-baseline justify-between gap-2"><h4 aria-label={`${item.kind === "segment" ? "Segment" : "Clip"}: ${item.title || "Untitled"}`} className="flex items-center gap-1.5 font-medium"><ComparisonKindIcon kind={item.kind} />{item.title || "Untitled"}</h4><span className="text-xs text-secondary">{seconds(item.sourceSec)} → {seconds(item.targetSec)}</span></div>
                <SynchronizedReviewPair
                  active={activeComparison === comparisonKey}
                  source={{ label: "Current primary", thumbnail: item.sourceThumbnail, videoSrc: videos.streamUrl(videoId, assessment!.sourceFileId!), ...sourceRange }}
                  target={{ label: "New primary", thumbnail: item.targetThumbnail, videoSrc: videos.streamUrl(videoId, targetFileId), ...targetRange }}
                />
              </ComparisonRow>;
            })}</div> : <p className="mt-3 text-sm text-secondary">There are no mapped segments or clips to compare visually.</p>}
          </div>
          {unresolved.length > 0 && <div className="rounded border border-amber-500/50 p-3"><p className="text-sm">Some timed content falls outside the matched sections. Select each item for permanent deletion before approving.</p><ul className="mt-2 max-h-56 overflow-y-auto text-sm">{unresolved.map((dependency) => { const key = `${dependency.kind}:${dependency.id}`; const deletionLabel = dependency.kind === "clip" ? "Delete clip video" : `Delete ${dependency.kind}`; return <li key={key} className="flex items-center gap-2 border-t border-border py-2"><span className="flex-1">{dependency.kind}: {dependency.title || "Untitled"} · {seconds(dependency.startSec)} · no counterpart found</span><label className="flex items-center gap-1"><input type="checkbox" checked={deletions.has(key)} onChange={(event) => setDeletions((current) => { const next = new Set(current); event.target.checked ? next.add(key) : next.delete(key); return next; })} /> {deletionLabel}</label></li>; })}</ul></div>}
          <button className={`${control} bg-accent text-white`} disabled={!!busy || !allResolved} onClick={() => void run("Applying alignment and setting primary file…", approveAlignment)}>Approve alignment and set primary</button>
        </div>}
        {busy && <div className="mt-4 flex items-center gap-3" role="status"><span>{busy}</span><button className={control} onClick={() => operation.current?.abort()}>Cancel operation</button></div>}
        {error && <p className="mt-4 text-sm text-red-400" role="alert">{error}</p>}
      </div>
    </div>
  );
}

function reviewPlaybackRange(startSec: number | undefined, endSec: number | null | undefined, midpointSec: number, duration: number | null | undefined) {
  if (startSec != null && endSec != null && endSec > startSec + 0.25) return { startSec, endSec };
  const boundedDuration = duration != null && Number.isFinite(duration) ? duration : Number.POSITIVE_INFINITY;
  const fallbackStart = Math.max(0, midpointSec - 2);
  return { startSec: fallbackStart, endSec: Math.min(boundedDuration, Math.max(fallbackStart + 0.5, midpointSec + 2)) };
}

function ComparisonKindIcon({ kind }: { kind: "segment" | "clip" }) {
  const label = kind === "segment" ? "Segment" : "Clip video";
  return <span title={label} className="inline-flex text-secondary">{kind === "segment" ? <Bookmark className="h-4 w-4" aria-hidden="true" /> : <Clapperboard className="h-4 w-4" aria-hidden="true" />}</span>;
}

function DependencyCountList({ counts }: { counts: Record<string, number> }) {
  const labels: Record<string, [string, string]> = {
    segment: ["segment", "segments"],
    clip: ["clip video", "clip videos"],
    detection: ["detection", "detections"],
    "group range": ["group range", "group ranges"],
  };
  const entries = Object.entries(counts).filter(([, count]) => count > 0).map(([kind, count]) => {
    const label = labels[kind]?.[count === 1 ? 0 : 1] ?? `${kind}${count === 1 ? "" : "s"}`;
    return { kind, count, label };
  });
  return <ul aria-label="Affected timed content" className="flex flex-wrap gap-2">
    {entries.map(({ kind, count, label }) => <li key={kind} aria-label={`${count} ${label}`} className="inline-flex min-w-36 items-center gap-2 rounded border border-border bg-surface px-3 py-2 text-sm">
      {kind === "segment" || kind === "clip" ? <ComparisonKindIcon kind={kind} /> : null}
      <span className="font-semibold">{count}</span>
      <span className="text-secondary">{label}</span>
    </li>)}
  </ul>;
}

function ComparisonRow({ active, label, onActivate, children }: { active: boolean; label: string; onActivate: () => void; children: React.ReactNode }) {
  const row = useRef<HTMLDivElement>(null);
  const onActivateRef = useRef(onActivate);
  useEffect(() => { onActivateRef.current = onActivate; }, [onActivate]);
  useEffect(() => {
    const element = row.current;
    if (!element || typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver(([entry]) => {
      if (entry.isIntersecting && entry.intersectionRatio >= 0.6) onActivateRef.current();
    }, { threshold: [0.6] });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  return <div ref={row} role="button" tabIndex={0} aria-label={label} aria-pressed={active} className={`rounded border bg-surface p-3 ${active ? "border-accent/60" : "border-border"}`} onClick={onActivate} onKeyDown={(event) => {
    if (event.key === "Enter" || event.key === " ") { event.preventDefault(); onActivate(); }
  }}>{children}</div>;
}

type ReviewFrameProps = {
  active: boolean;
  label: string;
  thumbnail: string | null;
  videoSrc: string;
  startSec: number;
  endSec: number;
  onVideoElementChange?: (element: HTMLVideoElement | null) => void;
  autoPlayVideo?: boolean;
};

function SynchronizedReviewPair({ active, source, target }: { active: boolean; source: Omit<ReviewFrameProps, "active">; target: Omit<ReviewFrameProps, "active"> }) {
  const [sourceVideo, setSourceVideo] = useState<HTMLVideoElement | null>(null);
  const [targetVideo, setTargetVideo] = useState<HTMLVideoElement | null>(null);
  useEffect(() => {
    if (!active || !sourceVideo || !targetVideo) return;
    let started = false;
    let starting = false;
    let cancelled = false;
    let playRetries = 0;
    let retryTimer: number | undefined;
    const cancelPendingSeeks = new Set<() => void>();
    const pairDuration = Math.max(0.5, Math.min(source.endSec - source.startSec, target.endSec - target.startSec));
    const seekTo = (video: HTMLVideoElement, time: number) => new Promise<void>((resolve) => {
      let finished = false;
      const finish = () => {
        if (finished) return;
        finished = true;
        video.removeEventListener("seeked", finish);
        cancelPendingSeeks.delete(finish);
        resolve();
      };
      cancelPendingSeeks.add(finish);
      video.addEventListener("seeked", finish, { once: true });
      video.currentTime = time;
      queueMicrotask(() => { if (!video.seeking) finish(); });
    });
    const beginTogether = () => {
      if (cancelled || started || starting || sourceVideo.readyState < 2 || targetVideo.readyState < 2) return;
      starting = true;
      sourceVideo.pause();
      targetVideo.pause();
      void Promise.all([seekTo(sourceVideo, source.startSec), seekTo(targetVideo, target.startSec)]).then(async () => {
        if (cancelled) return;
        const results = await Promise.allSettled([sourceVideo.play(), targetVideo.play()]);
        if (cancelled) return;
        starting = false;
        if (results.every((result) => result.status === "fulfilled")) { started = true; playRetries = 0; return; }
        sourceVideo.pause();
        targetVideo.pause();
        if (playRetries++ < 3) retryTimer = window.setTimeout(beginTogether, 250);
      });
    };
    const restartTogether = () => {
      if (starting) return;
      started = false;
      beginTogether();
    };
    const synchronize = () => {
      const sourceElapsed = sourceVideo.currentTime - source.startSec;
      if (sourceElapsed >= pairDuration) { restartTogether(); return; }
      const targetElapsed = targetVideo.currentTime - target.startSec;
      if (Math.abs(sourceElapsed - targetElapsed) > 0.075) targetVideo.currentTime = target.startSec + sourceElapsed;
    };
    sourceVideo.addEventListener("canplay", beginTogether);
    targetVideo.addEventListener("canplay", beginTogether);
    sourceVideo.addEventListener("timeupdate", synchronize);
    beginTogether();
    return () => {
      cancelled = true;
      window.clearTimeout(retryTimer);
      cancelPendingSeeks.forEach((cancel) => cancel());
      sourceVideo.removeEventListener("canplay", beginTogether);
      targetVideo.removeEventListener("canplay", beginTogether);
      sourceVideo.removeEventListener("timeupdate", synchronize);
      sourceVideo.pause();
      targetVideo.pause();
    };
  }, [active, source.endSec, source.startSec, sourceVideo, target.endSec, target.startSec, targetVideo]);
  return <div className="grid grid-cols-2 gap-3">
    <ReviewFrame active={active} onVideoElementChange={setSourceVideo} autoPlayVideo={false} {...source} />
    <ReviewFrame active={active} onVideoElementChange={setTargetVideo} autoPlayVideo={false} {...target} />
  </div>;
}

function ReviewFrame({ active, label, thumbnail, videoSrc, startSec, endSec, onVideoElementChange, autoPlayVideo }: ReviewFrameProps) {
  const poster = thumbnail ? `data:image/jpeg;base64,${thumbnail}` : null;
  return <figure>
    <figcaption className="mb-1 text-xs text-secondary">{label}</figcaption>
    <WallMediaCard
      title={`${label} comparison preview`}
      imageSrc={poster}
      imageAlt={`${label} comparison frame`}
      videoSrc={videoSrc}
      videoStartTimeSec={startSec}
      videoEndTimeSec={autoPlayVideo === false ? undefined : endSec}
      videoLoadRootMargin="0px"
      videoPlayThreshold={0.5}
      useVideo={active}
      autoPlayVideo={autoPlayVideo}
      onVideoElementChange={onVideoElementChange}
      muted
      chromeless
      trackingEnabled={false}
      aspectRatio="16 / 9"
      imageClassName="object-contain"
      videoClassName="object-contain"
      className="overflow-hidden rounded bg-black"
      fallback={<span className="text-xs text-secondary">Preview unavailable</span>}
    />
  </figure>;
}
