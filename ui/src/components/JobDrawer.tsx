import { useState, useEffect, useCallback, useRef } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { jobs } from "../api/client";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import type { JobInfo } from "../api/types";
import { X, Loader2, CheckCircle, XCircle, Ban, Clock, Trash2 } from "lucide-react";

interface Props {
  open: boolean;
  onClose: () => void;
}

const statusIcon = (status: JobInfo["status"]) => {
  switch (status) {
    case "running": return <Loader2 className="w-4 h-4 text-accent animate-spin" />;
    case "completed": return <CheckCircle className="w-4 h-4 text-green-400" />;
    case "failed": return <XCircle className="w-4 h-4 text-red-400" />;
    case "cancelled": return <Ban className="w-4 h-4 text-secondary" />;
    default: return <Clock className="w-4 h-4 text-yellow-400" />;
  }
};

export function JobDrawer({ open, onClose }: Props) {
  const queryClient = useQueryClient();
  const [realtimeJobs, setRealtimeJobs] = useState<Map<string, JobInfo>>(new Map());
  const connectionRef = useRef<ReturnType<typeof HubConnectionBuilder.prototype.build> | null>(null);

  const { data: activeJobs } = useQuery({
    queryKey: ["jobs-active"],
    queryFn: jobs.list,
    refetchInterval: open ? 3000 : false,
  });

  const { data: jobHistory } = useQuery({
    queryKey: ["jobs-history"],
    queryFn: jobs.history,
    enabled: open,
  });

  // SignalR real-time updates
  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl("/hubs/jobs")
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on("JobUpdated", (job: JobInfo) => {
      setRealtimeJobs((prev) => {
        const next = new Map(prev);
        next.set(job.id, job);
        return next;
      });
      // Invalidate queries to stay in sync
      queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
      queryClient.invalidateQueries({ queryKey: ["jobs-history"] });
      // When a job completes, invalidate content queries
      if (job.status === "completed") {
        queryClient.invalidateQueries({ queryKey: ["scenes"] });
        queryClient.invalidateQueries({ queryKey: ["images"] });
        queryClient.invalidateQueries({ queryKey: ["galleries"] });
        queryClient.invalidateQueries({ queryKey: ["performers"] });
        queryClient.invalidateQueries({ queryKey: ["stats"] });
      }
    });

    connection.start().catch(() => {});
    connectionRef.current = connection;

    return () => {
      connection.stop();
    };
  }, [queryClient]);

  const handleCancel = useCallback(async (id: string) => {
    await jobs.cancel(id);
    queryClient.invalidateQueries({ queryKey: ["jobs-active"] });
    queryClient.invalidateQueries({ queryKey: ["jobs-history"] });
  }, [queryClient]);

  // Merge API jobs with real-time updates
  const mergedActive = activeJobs?.map((j) => realtimeJobs.get(j.id) ?? j) ?? [];
  // Also add any real-time jobs not in the API response
  for (const [id, job] of realtimeJobs) {
    if (
      (job.status === "running" || job.status === "pending") &&
      !mergedActive.find((j) => j.id === id)
    ) {
      mergedActive.push(job);
    }
  }

  // Clean up stale entries from realtimeJobs when the API no longer returns them
  useEffect(() => {
    if (!activeJobs) return;
    const activeIds = new Set(activeJobs.map((j) => j.id));
    setRealtimeJobs((prev) => {
      let changed = false;
      const next = new Map(prev);
      for (const [id] of next) {
        if (!activeIds.has(id)) {
          next.delete(id);
          changed = true;
        }
      }
      return changed ? next : prev;
    });
  }, [activeJobs]);

  const runningCount = mergedActive.filter((j) => j.status === "running" || j.status === "pending").length;

  if (!open) return null;

  return (
    <>
      {/* Backdrop */}
      <div className="fixed inset-0 bg-black/50 z-40" onClick={onClose} />

      {/* Drawer */}
      <div className="job-drawer fixed right-0 top-0 h-full w-96 bg-surface border-l border-border z-50 flex flex-col shadow-2xl">
        <div className="flex items-center justify-between px-4 py-3 border-b border-border">
          <h2 className="font-semibold text-foreground">
            Jobs {runningCount > 0 && <span className="text-accent text-sm ml-1">({runningCount} active)</span>}
          </h2>
          <button onClick={onClose} className="text-muted hover:text-foreground">
            <X className="w-5 h-5" />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto">
          {/* Active jobs */}
          {mergedActive.length > 0 && (
            <div className="p-4">
              <h3 className="text-xs font-semibold text-muted uppercase mb-2">Active</h3>
              <div className="space-y-2">
                {mergedActive.map((job) => (
                  <JobCard key={job.id} job={job} onCancel={handleCancel} />
                ))}
              </div>
            </div>
          )}

          {/* History */}
          {jobHistory && jobHistory.length > 0 && (
            <div className="p-4 border-t border-border">
              <h3 className="text-xs font-semibold text-muted uppercase mb-2">History</h3>
              <div className="space-y-2">
                {jobHistory.map((job) => (
                  <JobCard key={job.id} job={job} />
                ))}
              </div>
            </div>
          )}

          {mergedActive.length === 0 && (!jobHistory || jobHistory.length === 0) && (
            <div className="p-8 text-center text-muted text-sm">
              No jobs running or in history
            </div>
          )}
        </div>
      </div>
    </>
  );
}

function formatDuration(ms: number): string {
  const totalSeconds = Math.max(0, Math.floor(ms / 1000));
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return `${minutes}m ${seconds.toString().padStart(2, "0")}s`;
  const hours = Math.floor(minutes / 60);
  const mins = minutes % 60;
  return `${hours}h ${mins.toString().padStart(2, "0")}m`;
}

function JobCard({ job, onCancel }: { job: JobInfo; onCancel?: (id: string) => void }) {
  const progressPct = Math.round(job.progress * 100);
  const [now, setNow] = useState(Date.now());
  const progressHistory = useRef<{ time: number; progress: number }[]>([]);
  const maxProgress = useRef(0);

  // Tick every second for elapsed/ETA display while running
  useEffect(() => {
    if (job.status !== "running") return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [job.status]);

  // Track progress history for rolling window ETA (clamp to never go backwards)
  useEffect(() => {
    if (job.status === "running" && job.progress > 0) {
      const hist = progressHistory.current;
      const now = Date.now();
      // Clamp: if progress goes backwards (phase transitions), reset the window
      if (job.progress < maxProgress.current - 0.01) {
        hist.length = 0;
      }
      maxProgress.current = Math.max(maxProgress.current, job.progress);
      hist.push({ time: now, progress: maxProgress.current });
      // Keep last 60 seconds for better averaging on slow jobs
      const cutoff = now - 60000;
      while (hist.length > 0 && hist[0].time < cutoff) hist.shift();
    }
  }, [job.progress, job.status]);

  const elapsedMs = now - new Date(job.startedAt).getTime();

  // Rolling window ETA: use rate from last 60s of progress updates
  // Falls back to overall rate (elapsed-based) when rolling window has insufficient data
  let etaMs: number | null = null;
  const hist = progressHistory.current;
  if (job.progress >= 0.01) {
    if (hist.length >= 2) {
      const first = hist[0];
      const last = hist[hist.length - 1];
      const dt = last.time - first.time;
      const dp = last.progress - first.progress;
      if (dt > 1000 && dp > 0) {
        const rate = dp / dt;
        etaMs = (1.0 - last.progress) / rate;
      }
    }
    // Fallback: overall elapsed rate when rolling window yields nothing
    if (etaMs == null && elapsedMs > 2000) {
      const overallRate = job.progress / elapsedMs;
      etaMs = (1.0 - job.progress) / overallRate;
    }
  }

  return (
    <div className="bg-card rounded-lg p-3">
      <div className="flex items-start justify-between gap-2">
        <div className="flex items-center gap-2 flex-1 min-w-0">
          {statusIcon(job.status)}
          <div className="min-w-0 flex-1">
            <p className="text-sm font-medium truncate">{job.description}</p>
            {job.subTask && (
              <p className="text-xs text-muted truncate mt-0.5">{job.subTask}</p>
            )}
          </div>
        </div>
        {(job.status === "running" || job.status === "pending") && onCancel && (
          <button onClick={() => onCancel(job.id)} className="text-muted hover:text-red-400 flex-shrink-0">
            <Trash2 className="w-3.5 h-3.5" />
          </button>
        )}
      </div>

      {job.status === "running" && (
        <div className="mt-2">
          <div className="h-1.5 bg-input rounded-full overflow-hidden">
            <div
              className="h-full bg-accent rounded-full transition-all duration-300"
              style={{ width: `${progressPct}%` }}
            />
          </div>
          <div className="flex items-center justify-between mt-1">
            <p className="text-xs text-muted">
              {progressPct}% · {formatDuration(elapsedMs)} elapsed
            </p>
            {etaMs != null && (
              <p className="text-xs text-muted">
                ~{formatDuration(etaMs)} remaining
              </p>
            )}
          </div>
        </div>
      )}

      {job.error && (
        <p className="text-xs text-red-400 mt-1 truncate">{job.error}</p>
      )}

      {job.completedAt && (
        <p className="text-xs text-muted mt-1">
          Completed in {formatDuration(new Date(job.completedAt).getTime() - new Date(job.startedAt).getTime())} · {new Date(job.completedAt).toLocaleTimeString()}
        </p>
      )}
    </div>
  );
}

// Export a hook for the navbar badge
export function useJobCount() {
  const [count, setCount] = useState(0);

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl("/hubs/jobs")
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build();

    let activeIds = new Set<string>();

    connection.on("JobUpdated", (job: JobInfo) => {
      if (job.status === "running" || job.status === "pending") {
        activeIds.add(job.id);
      } else {
        activeIds.delete(job.id);
      }
      setCount(activeIds.size);
    });

    // Also poll once on mount
    jobs.list().then((list) => {
      activeIds = new Set(list.filter((j) => j.status === "running" || j.status === "pending").map((j) => j.id));
      setCount(activeIds.size);
    }).catch(() => {});

    connection.start().catch(() => {});

    return () => { connection.stop(); };
  }, []);

  return count;
}
