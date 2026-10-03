import type { ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useFirstScreenPrefetch } from "../App";
import { buildRoutePath } from "../router/location";

const mocks = vi.hoisted(() => ({
  jobsList: vi.fn(async () => []),
  jobsHistory: vi.fn(async () => []),
  prefetchDashboardPage: vi.fn(),
}));

vi.mock("../api/client", async (importOriginal) => {
  const original = await importOriginal<typeof import("../api/client")>();
  return { ...original, jobs: { ...original.jobs, list: mocks.jobsList, history: mocks.jobsHistory } };
});

vi.mock("../pages/dashboardPageQuery", async (importOriginal) => {
  const original = await importOriginal<typeof import("../pages/dashboardPageQuery")>();
  return { ...original, prefetchDashboardPage: mocks.prefetchDashboardPage };
});

type Principal = { id: string; kind: "user" } | null;

function renderPrefetch(initial: { user: Principal; loading: boolean }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return renderHook(({ user, loading }) => useFirstScreenPrefetch(user, loading), { initialProps: initial, wrapper });
}

describe("useFirstScreenPrefetch", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.history.replaceState(null, "", "/");
  });
  afterEach(cleanup);

  it("prefetches nothing without a signed-in principal", () => {
    const { rerender } = renderPrefetch({ user: null, loading: true });
    rerender({ user: null, loading: false });

    expect(mocks.jobsList).not.toHaveBeenCalled();
    expect(mocks.prefetchDashboardPage).not.toHaveBeenCalled();
  });

  it("waits for the session to settle before prefetching", () => {
    const { rerender } = renderPrefetch({ user: { id: "7", kind: "user" }, loading: true });
    expect(mocks.prefetchDashboardPage).not.toHaveBeenCalled();

    rerender({ user: { id: "7", kind: "user" }, loading: false });
    expect(mocks.prefetchDashboardPage).toHaveBeenCalledOnce();
  });

  it("prefetches once per sign-in, and again for the next principal after signing out", () => {
    const { rerender } = renderPrefetch({ user: { id: "7", kind: "user" }, loading: false });
    rerender({ user: { id: "7", kind: "user" }, loading: false });

    expect(mocks.jobsList).toHaveBeenCalledOnce();
    expect(mocks.jobsHistory).toHaveBeenCalledOnce();
    expect(mocks.prefetchDashboardPage).toHaveBeenCalledOnce();
    expect(mocks.prefetchDashboardPage).toHaveBeenLastCalledWith(expect.any(QueryClient), "user:7", undefined);

    rerender({ user: null, loading: false });
    rerender({ user: { id: "8", kind: "user" }, loading: false });

    expect(mocks.prefetchDashboardPage).toHaveBeenCalledTimes(2);
    expect(mocks.prefetchDashboardPage).toHaveBeenLastCalledWith(expect.any(QueryClient), "user:8", undefined);
  });

  it("prefetches the dashboard a dashboard route or the manual shows, and none for other pages", () => {
    window.history.replaceState(null, "", buildRoutePath({ page: "dashboard", id: 3 }));
    renderPrefetch({ user: { id: "7", kind: "user" }, loading: false }).unmount();
    expect(mocks.prefetchDashboardPage).toHaveBeenLastCalledWith(expect.any(QueryClient), "user:7", 3);

    window.history.replaceState(null, "", buildRoutePath({ page: "manual" }));
    renderPrefetch({ user: { id: "7", kind: "user" }, loading: false }).unmount();
    expect(mocks.prefetchDashboardPage).toHaveBeenLastCalledWith(expect.any(QueryClient), "user:7", undefined);
    expect(mocks.prefetchDashboardPage).toHaveBeenCalledTimes(2);

    window.history.replaceState(null, "", buildRoutePath({ page: "videos" }));
    renderPrefetch({ user: { id: "7", kind: "user" }, loading: false });
    expect(mocks.prefetchDashboardPage).toHaveBeenCalledTimes(2);
    expect(mocks.jobsList).toHaveBeenCalledTimes(3);
  });
});
