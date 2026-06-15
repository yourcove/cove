import { useEffect, useState } from "react";
import { Loader2, CheckCircle, XCircle, Ban, Clock, Trash2, ChevronUp, ChevronDown } from "lucide-react";
import type { JobInfo } from "../api/types";

/** Format a millisecond duration as a compact human string (e.g. "45s", "3m 20s", "1h 15m"). */
export function formatJobDuration(ms: number): string {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return `${minutes}m ${seconds.toString().padStart(2, "0")}s`;
  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  return `${hours}h ${mins.toString().padStart(2, "0")}m`;
}

export function jobStatusIcon(status: JobInfo["status"]) {
  switch (status) {
    case "running": return <Loader2 className="w-4 h-4 text-accent animate-spin" />;
    case "completed": return <CheckCircle className="w-4 h-4 text-green-400" />;
    case "failed": return <XCircle className="w-4 h-4 text-red-400" />;
    case "cancelled": return <Ban className="w-4 h-4 text-secondary" />;
    default: return <Clock className="w-4 h-4 text-yellow-400" />;
  }
}

/**
 * Derives the live timing for a job (elapsed, progress %, and remaining ETA), ticking once a second
 * while the job is running. The ETA prefers the server's smoothed estimate (robust to bursty/no-op
 * jobs) counted down locally from the timestamp it was computed at, with a naive elapsed-rate fallback
 * only for servers that don't send one.
 */
export function useJobTiming(job: JobInfo) {
  const [now, setNow] = useState(Date.now());

  useEffect(() => {
    if (job.status !== "running") return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [job.status]);

  const progressPct = Math.round((job.progress ?? 0) * 100);
  const startedMs = new Date(job.startedAt).getTime();
  const elapsedMs = (job.completedAt ? new Date(job.completedAt).getTime() : now) - startedMs;

  let etaMs: number | null = null;
  if (job.status === "running" && typeof job.etaSeconds === "number" && job.etaSeconds >= 0) {
    const computedAt = job.updatedAt ? new Date(job.updatedAt).getTime() : now;
    etaMs = Math.max(0, job.etaSeconds * 1000 - Math.max(0, now - computedAt));
  } else if (job.status === "running" && job.progress >= 0.01 && elapsedMs > 4000) {
    etaMs = (1.0 - job.progress) / (job.progress / elapsedMs);
  }

  return { now, progressPct, elapsedMs, etaMs };
}

/** Human summary of how a finished job's units resolved, or null if there's nothing to show. */
function finishedCounts(job: JobInfo): string | null {
  if (job.summary) return job.summary;
  const succeeded = job.unitsSucceeded ?? 0;
  const failed = job.unitsFailed ?? 0;
  const skipped = job.unitsSkipped ?? 0;
  if (succeeded + failed + skipped === 0) return null;
  const parts: string[] = [];
  if (succeeded) parts.push(`${succeeded} succeeded`);
  if (failed) parts.push(`${failed} failed`);
  if (skipped) parts.push(`${skipped} skipped`);
  return parts.join(", ");
}

const STATUS_BADGE_CLASS: Record<JobInfo["status"], string> = {
  running: "bg-green-600/20 text-green-300",
  pending: "bg-yellow-600/20 text-yellow-300",
  completed: "bg-card text-muted",
  failed: "bg-red-600/20 text-red-300",
  cancelled: "bg-card text-muted",
};

export interface JobCardProps {
  job: JobInfo;
  /** "drawer" = compact card with a leading status icon; "panel" = settings card with a status badge and queue controls. */
  variant?: "drawer" | "panel";
  onCancel?: (id: string) => void;
  onMoveUp?: () => void;
  onMoveDown?: () => void;
}

/**
 * Single source of truth for rendering a job (running, queued, or finished) across the jobs drawer and
 * the Settings jobs panel. Finished jobs show their start time, total duration, finish time, a unit
 * summary, and any error.
 */
export function JobCard({ job, variant = "drawer", onCancel, onMoveUp, onMoveDown }: JobCardProps) {
  const { progressPct, elapsedMs, etaMs } = useJobTiming(job);
  const isPanel = variant === "panel";
  const isFinished = job.status !== "running" && job.status !== "pending";
  const counts = isFinished ? finishedCounts(job) : null;

  const containerClass = isPanel
    ? "flex items-start justify-between gap-2 rounded-xl border border-border bg-card p-3"
    : "job-drawer-card rounded-lg border border-border p-3 text-foreground";

  return (
    <div className={containerClass}>
      <div className="flex items-start gap-2 min-w-0 flex-1">
        {!isPanel && <span className="mt-0.5 flex-shrink-0">{jobStatusIcon(job.status)}</span>}
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            {isPanel && <span className="flex-shrink-0">{jobStatusIcon(job.status)}</span>}
            <span className="truncate text-sm font-medium text-foreground">{job.description}</span>
            {isPanel && (
              <span className={`text-xs px-1.5 py-0.5 rounded flex-shrink-0 ${STATUS_BADGE_CLASS[job.status]}`}>
                {job.status}
              </span>
            )}
          </div>

          {job.subTask && <p className="text-xs text-muted mt-0.5 line-clamp-3">{job.subTask}</p>}

          {job.status === "running" && job.progress != null && job.progress >= 0 && (
            <div className="mt-2">
              <div className="h-1.5 w-full rounded-full bg-input overflow-hidden">
                <div className="h-full rounded-full bg-accent transition-all duration-300" style={{ width: `${Math.min(progressPct, 100)}%` }} />
              </div>
              <div className="flex items-center justify-between mt-1 gap-2">
                <span className="text-xs text-muted">
                  {progressPct}% · {formatJobDuration(elapsedMs)} elapsed
                </span>
                {etaMs != null && (
                  <span className="text-xs text-muted whitespace-nowrap">
                    {etaMs < 1500 ? "finishing…" : `~${formatJobDuration(etaMs)} remaining`}
                  </span>
                )}
              </div>
            </div>
          )}

          {job.error && <p className="text-xs text-red-400 mt-1 break-words">{job.error}</p>}

          {isFinished && job.completedAt && (
            <div className="mt-1 space-y-0.5">
              <p className="text-xs text-muted">
                Started {new Date(job.startedAt).toLocaleString()}
                {" · "}
                Took {formatJobDuration(new Date(job.completedAt).getTime() - new Date(job.startedAt).getTime())}
                {" · "}
                Finished {new Date(job.completedAt).toLocaleTimeString()}
              </p>
              {counts && <p className="text-xs text-muted">{counts}</p>}
            </div>
          )}
        </div>
      </div>

      <div className="ml-2 flex flex-shrink-0 items-center gap-1">
        {isPanel && job.status === "pending" && (
          <>
            <button
              type="button"
              onClick={onMoveUp}
              disabled={!onMoveUp}
              title="Move queued job up"
              className="rounded p-1 text-muted hover:bg-card-hover hover:text-foreground disabled:cursor-not-allowed disabled:opacity-30"
            >
              <ChevronUp className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={onMoveDown}
              disabled={!onMoveDown}
              title="Move queued job down"
              className="rounded p-1 text-muted hover:bg-card-hover hover:text-foreground disabled:cursor-not-allowed disabled:opacity-30"
            >
              <ChevronDown className="h-4 w-4" />
            </button>
          </>
        )}
        {onCancel && (job.status === "running" || job.status === "pending") && (
          isPanel ? (
            <button type="button" onClick={() => onCancel(job.id)} className="text-xs text-muted hover:text-red-300">
              Cancel
            </button>
          ) : (
            <button type="button" onClick={() => onCancel(job.id)} title="Cancel job" className="text-muted hover:text-red-400 flex-shrink-0">
              <Trash2 className="w-3.5 h-3.5" />
            </button>
          )
        )}
      </div>
    </div>
  );
}
