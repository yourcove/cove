import { QueryClient } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import type { JobInfo } from "../api/types";
import {
  collectUnseenTerminalJobs,
  invalidateContentForTerminalJob,
  jobHistoryPollingInterval,
} from "../components/JobDrawer";

function job(status: JobInfo["status"], id: string = status): JobInfo {
  return {
    id,
    type: "image-bulk-delete",
    description: "Deleting images",
    status,
    progress: 0.5,
    startedAt: new Date().toISOString(),
    completedAt: status === "running" ? undefined : new Date().toISOString(),
  };
}

describe("bulk deletion job cache invalidation", () => {
  it.each(["completed", "failed", "cancelled"] as const)("invalidates content for a %s bulk deletion", (status) => {
    const queryClient = new QueryClient();
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");

    expect(invalidateContentForTerminalJob(queryClient, job(status))).toBe(true);
    expect(invalidate).toHaveBeenCalledWith();
  });

  it("detects a terminal job first observed by history polling after reconnect", () => {
    const seen = new Set<string>();
    const cancelled = job("cancelled", "missed-cancel");

    expect(collectUnseenTerminalJobs([job("running"), cancelled], seen)).toEqual([cancelled]);
    expect(collectUnseenTerminalJobs([cancelled], seen)).toEqual([]);
  });

  it("keeps a low-frequency history fallback while the drawer is closed", () => {
    expect(jobHistoryPollingInterval(false)).toBe(15000);
    expect(jobHistoryPollingInterval(true)).toBe(3000);
  });
});
