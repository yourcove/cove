import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DuplicateSearchInfo } from "../api/types";
import { DuplicateFinderPage } from "../pages/DuplicateFinderPage";

const mocks = vi.hoisted(() => ({
  getDuplicateSearch: vi.fn(),
  getDuplicateSearchGroups: vi.fn(),
}));

vi.mock("../api/client", () => ({
  videos: {
    getDuplicateSearch: mocks.getDuplicateSearch,
    getDuplicateSearchGroups: mocks.getDuplicateSearchGroups,
    startDuplicateSearch: vi.fn(),
    updateDuplicateSearchDecision: vi.fn(),
    deleteUnkeptDuplicates: vi.fn(),
  },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

const savedSearch: DuplicateSearchInfo = {
  id: "saved-title-search",
  jobId: "duplicate-search-job",
  matchType: "title",
  distance: 0,
  durationDiff: 10,
  status: "completed",
  candidateCount: 30_000,
  groupCount: 0,
  videoCount: 0,
  unkeptVideoCount: 0,
  unkeptFileCount: 0,
  unkeptBytes: 0,
  deletionJobId: null,
  createdAt: "2026-08-25T00:00:00Z",
  startedAt: "2026-08-25T00:00:01Z",
  completedAt: "2026-08-25T00:00:02Z",
  expiresAt: "2026-09-01T00:00:02Z",
};

describe("DuplicateFinderPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.history.replaceState({}, "", "/duplicates?search=saved-title-search");
    mocks.getDuplicateSearch.mockResolvedValue(savedSearch);
    mocks.getDuplicateSearchGroups.mockResolvedValue({
      items: [],
      totalCount: 0,
      page: 1,
      perPage: 10,
      hasMore: false,
    });
  });

  afterEach(() => {
    window.history.replaceState({}, "", "/");
  });

  it("restores the match type recorded by a saved search", async () => {
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DuplicateFinderPage onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    const matchType = screen.getByRole("combobox");
    await waitFor(() => expect(matchType).toHaveValue("title"));
    expect(screen.getByText("Groups videos with the same normalized title.")).toBeInTheDocument();
    expect(screen.getByRole("spinbutton", { name: "pHash distance" })).toHaveValue(8);
    expect(screen.getByRole("spinbutton", { name: "Max duration delta" })).toHaveValue(10);
    expect(mocks.getDuplicateSearch).toHaveBeenCalledWith("saved-title-search");
  });

  it("restores the parameters recorded by a saved visual search", async () => {
    mocks.getDuplicateSearch.mockResolvedValue({
      ...savedSearch,
      matchType: "phash",
      distance: 12,
      durationDiff: 45,
    });

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DuplicateFinderPage onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByRole("combobox")).toHaveValue("phash"));
    expect(screen.getByRole("spinbutton", { name: "pHash distance" })).toHaveValue(12);
    expect(screen.getByRole("spinbutton", { name: "Max duration delta" })).toHaveValue(45);
  });
});
