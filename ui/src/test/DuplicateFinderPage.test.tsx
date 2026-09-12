import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DuplicateSearchGroup, DuplicateSearchInfo, Video } from "../api/types";
import { phashDistance, rowTones, COMPARISON_ROWS } from "../components/duplicates/duplicateModel";
import { DuplicateFinderPage } from "../pages/DuplicateFinderPage";

const mocks = vi.hoisted(() => ({
  getDuplicateSearch: vi.fn(),
  getDuplicateSearchGroups: vi.fn(),
  listDuplicateSearches: vi.fn(),
  startDuplicateSearch: vi.fn(),
  updateDuplicateSearchDecision: vi.fn(),
  resolveDuplicateGroups: vi.fn(),
  ignoreDuplicateGroup: vi.fn(),
  registerKeyboardActions: vi.fn(),
}));

vi.mock("../api/client", () => ({
  videos: {
    getDuplicateSearch: mocks.getDuplicateSearch,
    getDuplicateSearchGroups: mocks.getDuplicateSearchGroups,
    listDuplicateSearches: mocks.listDuplicateSearches,
    startDuplicateSearch: mocks.startDuplicateSearch,
    updateDuplicateSearchDecision: mocks.updateDuplicateSearchDecision,
    resolveDuplicateGroups: mocks.resolveDuplicateGroups,
    ignoreDuplicateGroup: mocks.ignoreDuplicateGroup,
    restoreDuplicateGroup: vi.fn(),
    autoSelectDuplicateKeepers: vi.fn(),
    deleteDuplicateSearch: vi.fn(),
    screenshotUrl: vi.fn(() => "/test-video.jpg"),
    streamUrl: vi.fn(() => "/test-video.mp4"),
    transcodeUrl: vi.fn(() => "/test-video-transcode.mp4"),
  },
  jobs: { get: vi.fn(), cancel: vi.fn() },
  metadata: { libraryFolders: vi.fn(async () => []) },
  entityEngagement: { batch: vi.fn(async () => []) },
}));

vi.mock("../components/VideoPreviewThumbnail", () => ({
  VideoPreviewThumbnail: ({ children }: { children?: ReactNode }) => <div data-testid="preview">{children}</div>,
}));

vi.mock("../components/QuickViewDialog", () => ({ QuickViewDialog: () => null }));

vi.mock("../keyboard/KeyboardShortcutProvider", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../keyboard/KeyboardShortcutProvider")>()),
  useRegisterKeyboardActions: mocks.registerKeyboardActions,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

const completedSearch: DuplicateSearchInfo = {
  id: "saved-search",
  jobId: "duplicate-search-job",
  matchType: "phash",
  distance: 4,
  durationDiff: 5,
  includePaths: [],
  excludePaths: [],
  minimumDuration: 0,
  keeperRules: [{ type: "resolution" }],
  status: "completed",
  candidateCount: 30_000,
  groupCount: 1,
  videoCount: 2,
  counts: { unresolved: 1, queued: 0, resolved: 0, ignored: 0, failed: 0 },
  reclaimableBytes: 120_000_000,
  removableVideoCount: 1,
  removedBytes: 0,
  removedVideoCount: 0,
  resolutionJobId: null,
  createdAt: "2026-08-25T00:00:00Z",
  startedAt: "2026-08-25T00:00:01Z",
  completedAt: "2026-08-25T00:00:02Z",
  expiresAt: "2026-09-01T00:00:02Z",
};

function video(id: number, width: number, height: number, codec: string, bitRate: number): Video {
  return {
    id,
    title: `Candidate ${id}`,
    organized: false,
    urls: [],
    tags: [],
    performers: [],
    files: [
      {
        id: id * 10,
        path: `/library/candidate-${id}.mp4`,
        basename: `candidate-${id}.mp4`,
        format: "mp4",
        width,
        height,
        duration: 120,
        videoCodec: codec,
        audioCodec: "aac",
        frameRate: 30,
        bitRate,
        size: 120_000_000,
        fingerprints: [{ type: "phash", value: id === 1 ? "ff00ff00ff00ff00" : "ff00ff00ff00ff01" }],
      },
    ],
    primaryFileId: id * 10,
    groups: [],
    galleries: [],
    remoteIds: [],
    createdAt: "2026-08-25T00:00:00Z",
    updatedAt: "2026-08-25T00:00:00Z",
  };
}

const group: DuplicateSearchGroup = {
  id: 7,
  position: 0,
  status: "unresolved",
  videos: [video(1, 3840, 2160, "hevc", 8_000_000), video(2, 1920, 1080, "h264", 4_000_000)],
  keepVideoIds: [1],
  decisionSource: "auto",
  decisionRule: "resolution",
  resolutionAction: null,
  deleteFiles: false,
  error: null,
  resolvedAt: null,
  removedVideoCount: 0,
  removedBytes: 0,
  reclaimableBytes: 120_000_000,
};

const nextGroup: DuplicateSearchGroup = {
  ...group,
  id: 8,
  position: 1,
  videos: [video(3, 1920, 1080, "h264", 6_000_000), video(4, 1280, 720, "h264", 3_000_000)],
  keepVideoIds: [3],
};

const storage = new Map<string, string>();
const localStorageStub = {
  getItem: (key: string) => storage.get(key) ?? null,
  setItem: (key: string, value: string) => void storage.set(key, value),
  removeItem: (key: string) => void storage.delete(key),
  clear: () => storage.clear(),
};

function renderPage() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <DuplicateFinderPage onNavigate={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("DuplicateFinderPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    storage.clear();
    Object.defineProperty(window, "localStorage", { configurable: true, value: localStorageStub });
    mocks.listDuplicateSearches.mockResolvedValue([]);
    mocks.getDuplicateSearch.mockResolvedValue(completedSearch);
    mocks.getDuplicateSearchGroups.mockResolvedValue({ items: [group], totalCount: 1, page: 1, perPage: 10 });
    mocks.updateDuplicateSearchDecision.mockResolvedValue(undefined);
    mocks.resolveDuplicateGroups.mockResolvedValue({ queuedGroupCount: 1, jobId: "resolve-job" });
  });

  afterEach(() => {
    window.history.replaceState({}, "", "/");
  });

  it("starts a search with the remembered settings", async () => {
    window.history.replaceState({}, "", "/duplicates");
    mocks.startDuplicateSearch.mockResolvedValue({ searchId: "new-search", jobId: "job", candidateCount: 10 });
    renderPage();

    fireEvent.click(screen.getByRole("radio", { name: /Identical files/ }));
    fireEvent.click(screen.getByRole("button", { name: /Find duplicates/ }));

    await waitFor(() => expect(mocks.startDuplicateSearch).toHaveBeenCalled());
    expect(mocks.startDuplicateSearch.mock.calls[0][0]).toMatchObject({ matchType: "fingerprint", distance: 0 });
    expect(JSON.parse(window.localStorage.getItem("cove.duplicates.search.v2")!).matchType).toBe("fingerprint");
    await waitFor(() => expect(window.location.search).toContain("search=new-search"));
  });

  it("compares a saved group side by side and highlights the better copy", async () => {
    window.history.replaceState({}, "", "/duplicates?search=saved-search");
    renderPage();

    const card = await screen.findByText("Group 1");
    const article = card.closest("article")!;
    expect(within(article).getByText("Auto: highest resolution")).toBeInTheDocument();
    expect(within(article).getByText("Visual distance 1")).toBeInTheDocument();
    expect(within(article).getByText(/HEVC/)).toBeInTheDocument();
    expect(within(article).getByText("8.0 Mbps")).toBeInTheDocument();
    expect(within(article).getByText("Keeping this copy")).toBeInTheDocument();
    expect(mocks.getDuplicateSearchGroups).toHaveBeenCalledWith(
      "saved-search",
      expect.objectContaining({ status: "unresolved" }),
    );
  });

  it("switches the keeper with one click", async () => {
    window.history.replaceState({}, "", "/duplicates?search=saved-search");
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: /Keep this instead/ }));

    await waitFor(() => expect(mocks.updateDuplicateSearchDecision).toHaveBeenCalledWith("saved-search", 7, [2]));
  });

  it("asks once before resolving a group and remembers the choice", async () => {
    window.history.replaceState({}, "", "/duplicates?search=saved-search");
    renderPage();

    fireEvent.click(await screen.findByRole("button", { name: /Merge & remove 1/ }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByLabelText(/Don't ask again/));
    fireEvent.click(within(dialog).getByRole("button", { name: /Merge & remove 1 copy/ }));

    await waitFor(() =>
      expect(mocks.resolveDuplicateGroups).toHaveBeenCalledWith("saved-search", {
        groupIds: [7],
        action: "merge",
        deleteFiles: false,
        deleteGenerated: true,
      }),
    );
    expect(JSON.parse(window.localStorage.getItem("cove.duplicates.resolution.v2")!).confirmEachGroup).toBe(false);
  });

  it("scrolls the next group below the sticky navigation and duplicate controls after resolving", async () => {
    mocks.getDuplicateSearchGroups.mockResolvedValue({
      items: [group, nextGroup],
      totalCount: 2,
      page: 1,
      perPage: 10,
    });
    const navbar = document.createElement("nav");
    navbar.className = "cove-navbar";
    document.body.append(navbar);
    const scrollTo = vi.spyOn(window, "scrollTo").mockImplementation(() => undefined);
    const scrollY = vi.spyOn(window, "scrollY", "get").mockReturnValue(200);
    let computedStyle: ReturnType<typeof vi.spyOn> | undefined;

    try {
      window.history.replaceState({}, "", "/duplicates?search=saved-search");
      renderPage();

      const firstArticle = (await screen.findByText("Group 1")).closest("article")!;
      const nextArticle = screen.getByText("Group 2").closest("article")!;
      const controls = screen.getByPlaceholderText(/Filter by title/).parentElement!.parentElement!.parentElement!;
      expect(controls).toHaveClass("md:sticky", "md:top-12");
      nextArticle.scrollIntoView = vi.fn();
      vi.spyOn(navbar, "getBoundingClientRect").mockReturnValue({ height: 48 } as DOMRect);
      vi.spyOn(controls, "getBoundingClientRect").mockReturnValue({ height: 112 } as DOMRect);
      vi.spyOn(nextArticle, "getBoundingClientRect").mockReturnValue({ top: 500 } as DOMRect);

      fireEvent.click(within(firstArticle).getByRole("button", { name: /Merge & remove 1/ }));
      const dialog = await screen.findByRole("dialog");
      const confirm = within(dialog).getByRole("button", { name: /Merge & remove 1 copy/ });
      const realGetComputedStyle = window.getComputedStyle;
      computedStyle = vi.spyOn(window, "getComputedStyle").mockImplementation((element, pseudoElement) => {
        const style = realGetComputedStyle(element, pseudoElement);
        if (element === controls) Object.defineProperty(style, "position", { configurable: true, value: "sticky" });
        return style;
      });
      fireEvent.click(confirm);

      await waitFor(() => expect(scrollTo).toHaveBeenCalledWith({ top: 524, behavior: "smooth" }));
      expect(nextArticle.scrollIntoView).not.toHaveBeenCalled();
    } finally {
      computedStyle?.mockRestore();
      scrollY.mockRestore();
      scrollTo.mockRestore();
      navbar.remove();
    }
  });

  it("offsets keyboard navigation only for the navbar when the duplicate controls are not sticky", async () => {
    mocks.getDuplicateSearchGroups.mockResolvedValue({
      items: [group, nextGroup],
      totalCount: 2,
      page: 1,
      perPage: 10,
    });
    const navbar = document.createElement("nav");
    navbar.className = "cove-navbar";
    document.body.append(navbar);
    const scrollTo = vi.spyOn(window, "scrollTo").mockImplementation(() => undefined);
    const scrollY = vi.spyOn(window, "scrollY", "get").mockReturnValue(200);
    let computedStyle: ReturnType<typeof vi.spyOn> | undefined;

    try {
      window.history.replaceState({}, "", "/duplicates?search=saved-search");
      renderPage();

      const nextArticle = (await screen.findByText("Group 2")).closest("article")!;
      const controls = screen.getByPlaceholderText(/Filter by title/).parentElement!.parentElement!.parentElement!;
      vi.spyOn(navbar, "getBoundingClientRect").mockReturnValue({ height: 48 } as DOMRect);
      vi.spyOn(controls, "getBoundingClientRect").mockReturnValue({ height: 112 } as DOMRect);
      vi.spyOn(nextArticle, "getBoundingClientRect").mockReturnValue({ top: 500 } as DOMRect);
      const realGetComputedStyle = window.getComputedStyle;
      computedStyle = vi.spyOn(window, "getComputedStyle").mockImplementation((element, pseudoElement) => {
        const style = realGetComputedStyle(element, pseudoElement);
        if (element === controls) Object.defineProperty(style, "position", { configurable: true, value: "static" });
        return style;
      });
      const registrations = [...mocks.registerKeyboardActions.mock.calls]
        .reverse()
        .map(([value]) => value)
        .find((value) => value.some((entry: { id: string }) => entry.id === "duplicates.group.next"));
      const moveNext = registrations!.find((entry: { id: string }) => entry.id === "duplicates.group.next")!;

      act(() => moveNext.action({}));

      expect(scrollTo).toHaveBeenCalledWith({ top: 636, behavior: "smooth" });
      expect(nextArticle).toHaveClass("ring-1");
    } finally {
      computedStyle?.mockRestore();
      scrollY.mockRestore();
      scrollTo.mockRestore();
      navbar.remove();
    }
  });
});

describe("duplicate comparison model", () => {
  it("measures pHash distance", () => {
    expect(phashDistance("ff", "fe")).toBe(1);
    expect(phashDistance("ff", undefined)).toBeNull();
    expect(phashDistance("zz", "ff")).toBeNull();
  });

  it("marks the best value only when copies differ", () => {
    const resolution = COMPARISON_ROWS.find((row) => row.key === "resolution")!;
    const [big, small] = group.videos;
    expect(rowTones(resolution, [big, small], new Map()).tones).toEqual(["best", "worse"]);
    expect(rowTones(resolution, [big, big], new Map())).toEqual({ tones: ["same", "same"], allSame: true });
  });

  it("renders added dates in Cove's ISO date format", () => {
    const added = COMPARISON_ROWS.find((row) => row.key === "added")!;

    expect(added.render(group.videos[0], undefined)).toBe("2026-08-25");
  });
});
