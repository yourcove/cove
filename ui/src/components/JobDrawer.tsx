import { useState, useEffect, useCallback, useRef } from "react";
import { createPortal } from "react-dom";
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { jobs } from "../api/client";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import type { JobInfo } from "../api/types";
import { X } from "lucide-react";
import { JobCard } from "./JobCard";

interface Props {
  open: boolean;
  onClose: () => void;
  onNavigate?: (r: any) => void;
}

export function isTerminalJob(job: JobInfo): boolean {
  return job.status === "completed" || job.status === "failed" || job.status === "cancelled";
}

export function collectUnseenTerminalJobs(jobsToInspect: readonly JobInfo[], seen: Set<string>): JobInfo[] {
  const unseen: JobInfo[] = [];
  for (const job of jobsToInspect) {
    if (!isTerminalJob(job) || seen.has(job.id)) continue;
    seen.add(job.id);
    unseen.push(job);
  }
  return unseen;
}

// Query roots holding settings, extension state or job state, which jobs do not change. Refetching them
// could also disturb a settings form mid-edit. Every other root is content a job may have changed.
const NON_CONTENT_QUERY_ROOTS = new Set([
  "admin",
  "auth",
  "custom-fields",
  "dashboard-page",
  "display-profiles",
  "ext-config",
  "extensions-list",
  "ffmpeg-capabilities",
  "filesystem-policy",
  "job",
  "jobs",
  "jobs-history",
  "library-folders",
  "logs",
  "plugins",
  "registry-categories",
  "registry-search",
  "registry-updates",
  "saved-filter",
  "saved-filters",
  "scrapers",
  "segment-display-profile",
  "segment-display-profiles",
  "settings",
  "setup",
  "system-config",
  "system-downloaders",
  "system-status",
  "video-conversion-encoders",
]);

export function isContentQueryKey(queryKey: readonly unknown[]): boolean {
  return !NON_CONTENT_QUERY_ROOTS.has(String(queryKey[0]));
}

// Job types that write files outside the library and change no content.
const JOB_TYPES_WITHOUT_CONTENT_CHANGES = new Set(["backup", "export"]);

/**
 * Any job can commit part of its work before it fails or is cancelled, and job types range from scans
 * and identify to plugin tasks, so every terminal outcome invalidates all content queries. Bulk
 * deletions also invalidate settings data: deleting a tag, for example, clears it from display rules.
 */
export function invalidateContentForTerminalJob(queryClient: QueryClient, job: JobInfo): boolean {
  if (!isTerminalJob(job) || JOB_TYPES_WITHOUT_CONTENT_CHANGES.has(job.type)) return false;
  if (job.type.endsWith("-bulk-delete")) {
    void queryClient.invalidateQueries();
  } else {
    void queryClient.invalidateQueries({ predicate: (query) => isContentQueryKey(query.queryKey) });
  }
  return true;
}

/** Replaces the job in the cached list, or appends it. Leaves an unloaded list for the first fetch. */
export function applyJobUpdate(list: JobInfo[] | undefined, job: JobInfo): JobInfo[] | undefined {
  if (!list) return list;
  const index = list.findIndex((existing) => existing.id === job.id);
  return index === -1 ? [...list, job] : list.map((existing, i) => (i === index ? job : existing));
}

export function jobHistoryPollingInterval(drawerOpen: boolean): number {
  return drawerOpen ? 3000 : 15000;
}

export function JobDrawer({ open, onClose }: Props) {
  const queryClient = useQueryClient();
  const [realtimeJobs, setRealtimeJobs] = useState<Map<string, JobInfo>>(new Map());
  const connectionRef = useRef<ReturnType<typeof HubConnectionBuilder.prototype.build> | null>(null);
  const observedTerminalJobsRef = useRef(new Set<string>());

  const { data: activeJobs } = useQuery({
    queryKey: ["jobs"],
    queryFn: jobs.list,
    refetchInterval: open ? 3000 : false,
  });

  const { data: jobHistory } = useQuery({
    queryKey: ["jobs-history"],
    queryFn: jobs.history,
    // The drawer stays mounted while closed. Keep a low-frequency fallback running so a terminal
    // SignalR update missed during reconnect or browser suspension cannot leave deleted content cached.
    refetchInterval: jobHistoryPollingInterval(open),
  });

  const observeTerminalJob = useCallback(
    (job: JobInfo) => {
      for (const unseen of collectUnseenTerminalJobs([job], observedTerminalJobsRef.current)) {
        invalidateContentForTerminalJob(queryClient, unseen);
      }
    },
    [queryClient],
  );

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
      // Apply the event to the cached job list so the badge and queue update at once. Updates arrive up
      // to ten times a second per job, so the follow-up refetch must not cancel one already in flight,
      // or no refetch would ever finish while jobs run.
      queryClient.setQueryData<JobInfo[]>(["jobs"], (list) => applyJobUpdate(list, job));
      queryClient.invalidateQueries({ queryKey: ["jobs"] }, { cancelRefetch: false });
      queryClient.invalidateQueries({ queryKey: ["jobs-history"] }, { cancelRefetch: false });
      observeTerminalJob(job);
    });

    connection.start().catch(() => {});
    connectionRef.current = connection;

    return () => {
      connection.stop();
    };
  }, [observeTerminalJob, queryClient]);

  // Poll history as a fallback for terminal SignalR messages missed during a reconnect. The first
  // response only records the baseline: those jobs ended before this page loaded its data.
  const historyBaselineRecordedRef = useRef(false);
  useEffect(() => {
    if (!jobHistory) return;
    const unseen = collectUnseenTerminalJobs(jobHistory, observedTerminalJobsRef.current);
    if (!historyBaselineRecordedRef.current) {
      historyBaselineRecordedRef.current = true;
      return;
    }
    for (const job of unseen) invalidateContentForTerminalJob(queryClient, job);
  }, [jobHistory, queryClient]);

  const handleCancel = useCallback(
    async (id: string) => {
      await jobs.cancel(id);
      queryClient.invalidateQueries({ queryKey: ["jobs"] });
      queryClient.invalidateQueries({ queryKey: ["jobs-history"] });
    },
    [queryClient],
  );

  // Clean up stale entries from realtimeJobs when the API no longer returns them
  const [prevActiveJobs, setPrevActiveJobs] = useState(activeJobs);
  if (activeJobs !== prevActiveJobs) {
    setPrevActiveJobs(activeJobs);
    if (activeJobs) {
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
    }
  }

  // Merge API jobs with real-time updates
  const mergedActive = activeJobs?.map((j) => realtimeJobs.get(j.id) ?? j) ?? [];
  // Also add any real-time jobs not in the API response
  for (const [id, job] of realtimeJobs) {
    if ((job.status === "running" || job.status === "pending") && !mergedActive.find((j) => j.id === id)) {
      mergedActive.push(job);
    }
  }

  const runningCount = mergedActive.filter((j) => j.status === "running" || j.status === "pending").length;

  if (typeof document === "undefined") return null;

  if (!open) return null;

  return createPortal(
    <>
      {open ? (
        <>
          {/* Backdrop */}
          <div className="fixed inset-0 bg-black/50 z-40" onClick={onClose} />

          {/* Drawer */}
          <div className="job-drawer fixed inset-y-0 right-0 z-50 flex w-96 flex-col border-l border-border bg-surface text-foreground shadow-2xl">
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
                <div className="p-8 text-center text-muted text-sm">No jobs running or in history</div>
              )}
            </div>
          </div>
        </>
      ) : null}
    </>,
    document.body,
  );
}

// Export a hook for the navbar badge. It shares the drawer's active-job query; the drawer stays mounted
// next to the badge and invalidates that query on every SignalR job update.
export function useJobCount() {
  const { data } = useQuery({ queryKey: ["jobs"], queryFn: jobs.list });
  return data?.filter((job) => job.status === "running" || job.status === "pending").length ?? 0;
}
