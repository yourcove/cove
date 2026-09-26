import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import { createElement, type ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import { jobs } from "../api/client";
import type { JobInfo } from "../api/types";
import {
  applyJobUpdate,
  collectUnseenTerminalJobs,
  invalidateContentForTerminalJob,
  jobHistoryPollingInterval,
  useJobCount,
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

  it("applies a job event to the cached list without waiting for a fetch", () => {
    const running = job("running", "a");
    expect(applyJobUpdate(undefined, running)).toBeUndefined();
    expect(applyJobUpdate([running], job("completed", "a"))).toEqual([job("completed", "a")]);
    expect(applyJobUpdate([running], job("pending", "b"))).toEqual([running, job("pending", "b")]);
  });

  it("counts running and pending jobs from the shared job list for the navbar badge", async () => {
    const list = vi.spyOn(jobs, "list").mockResolvedValue([job("running", "a"), job("completed", "b")]);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client: queryClient }, children);

    const { result } = renderHook(() => useJobCount(), { wrapper });
    await waitFor(() => expect(result.current).toBe(1));

    act(() => {
      queryClient.setQueryData<JobInfo[]>(["jobs"], (cached) => applyJobUpdate(cached, job("pending", "c")));
    });
    await waitFor(() => expect(result.current).toBe(2));
    list.mockRestore();
  });
});
