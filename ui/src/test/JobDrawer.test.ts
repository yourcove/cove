import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, renderHook, waitFor } from "@testing-library/react";
import { createElement, type ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("@microsoft/signalr", () => {
  const connection = { on: vi.fn(), start: vi.fn(() => Promise.resolve()), stop: vi.fn() };
  class HubConnectionBuilder {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return connection;
    }
  }
  return { HubConnectionBuilder, LogLevel: { Warning: 3 } };
});
import { jobs } from "../api/client";
import type { JobInfo } from "../api/types";
import {
  applyJobUpdate,
  collectUnseenTerminalJobs,
  invalidateContentForTerminalJob,
  isContentQueryKey,
  JobDrawer,
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

function cachedQueryClient() {
  const queryClient = new QueryClient();
  for (const key of [["tags"], ["studios"], ["tag-videos", 1], ["video", 2], ["stats"], ["system-config"], ["jobs"]]) {
    queryClient.setQueryData(key, []);
  }
  return queryClient;
}

function invalidatedRoots(queryClient: QueryClient) {
  return queryClient
    .getQueryCache()
    .getAll()
    .filter((query) => query.state.isInvalidated)
    .map((query) => query.queryKey[0]);
}

describe("job cache invalidation", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it.each(["completed", "failed", "cancelled"] as const)(
    "invalidates every content query when a job is %s",
    (status) => {
      for (const type of ["scan", "identify", "plugin:com.example.task"]) {
        const queryClient = cachedQueryClient();

        expect(invalidateContentForTerminalJob(queryClient, { ...job(status), type })).toBe(true);
        expect(invalidatedRoots(queryClient)).toEqual(["tags", "studios", "tag-videos", "video", "stats"]);
      }
    },
  );

  it.each(["completed", "failed", "cancelled"] as const)(
    "invalidates everything when a bulk deletion is %s",
    (status) => {
      const queryClient = cachedQueryClient();

      expect(invalidateContentForTerminalJob(queryClient, { ...job(status), type: "tag-bulk-delete" })).toBe(true);
      expect(invalidatedRoots(queryClient)).toEqual([
        "tags",
        "studios",
        "tag-videos",
        "video",
        "stats",
        "system-config",
        "jobs",
      ]);
    },
  );

  it.each(["backup", "export"])("leaves the cache alone when a %s job ends", (type) => {
    const queryClient = cachedQueryClient();

    expect(invalidateContentForTerminalJob(queryClient, { ...job("completed"), type })).toBe(false);
    expect(invalidatedRoots(queryClient)).toEqual([]);
  });

  it.each(["pending", "running"] as const)("leaves the cache alone while a job is %s", (status) => {
    const queryClient = cachedQueryClient();

    expect(invalidateContentForTerminalJob(queryClient, job(status))).toBe(false);
    expect(invalidatedRoots(queryClient)).toEqual([]);
  });

  it("treats settings, extension and job state as non-content", () => {
    expect(isContentQueryKey(["tag-studios", 3])).toBe(true);
    expect(isContentQueryKey(["system-config"])).toBe(false);
    expect(isContentQueryKey(["extensions-list"])).toBe(false);
    expect(isContentQueryKey(["jobs-history"])).toBe(false);
  });

  it("does not invalidate content for jobs that ended before the drawer loaded history", async () => {
    const earlier = job("completed", "earlier");
    const later = job("failed", "later");
    vi.spyOn(jobs, "list").mockResolvedValue([]);
    const history = vi.spyOn(jobs, "history").mockResolvedValue([earlier]);
    const queryClient = cachedQueryClient();

    render(
      createElement(
        QueryClientProvider,
        { client: queryClient },
        createElement(JobDrawer, { open: false, onClose: vi.fn() }),
      ),
    );
    await waitFor(() => expect(history).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(queryClient.getQueryData(["jobs-history"])).toEqual([earlier]));
    // Let the drawer's history effect run before checking that it invalidated nothing.
    await act(async () => {});
    expect(invalidatedRoots(queryClient)).not.toContain("tags");

    history.mockResolvedValue([later, earlier]);
    await act(() => queryClient.refetchQueries({ queryKey: ["jobs-history"] }));
    await waitFor(() => expect(invalidatedRoots(queryClient)).toContain("tags"));
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
    // Built once each: two calls a millisecond apart would stamp different times.
    const completed = job("completed", "a");
    const pending = job("pending", "b");
    expect(applyJobUpdate([running], completed)).toEqual([completed]);
    expect(applyJobUpdate([running], pending)).toEqual([running, pending]);
  });

  it("counts running and pending jobs from the shared job list for the navbar badge", async () => {
    vi.spyOn(jobs, "list").mockResolvedValue([job("running", "a"), job("completed", "b")]);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client: queryClient }, children);

    const { result } = renderHook(() => useJobCount(), { wrapper });
    await waitFor(() => expect(result.current).toBe(1));

    act(() => {
      queryClient.setQueryData<JobInfo[]>(["jobs"], (cached) => applyJobUpdate(cached, job("pending", "c")));
    });
    await waitFor(() => expect(result.current).toBe(2));
  });
});
