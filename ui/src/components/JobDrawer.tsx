import { useState, useEffect, useCallback, useRef } from "react";
import { createPortal } from "react-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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
        queryClient.invalidateQueries({ queryKey: ["videos"] });
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
            <div className="p-8 text-center text-muted text-sm">
              No jobs running or in history
            </div>
          )}
        </div>
          </div>
        </>
      ) : null}
    </>,
    document.body,
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

