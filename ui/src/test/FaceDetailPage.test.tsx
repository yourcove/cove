import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FaceDetailRoute } from "../pages/FaceDetailPage";
import { SimilarReviewChoicesProvider } from "../components/similarReviewChoices";
import { navigateToUrl } from "../router/location";

const { mockFaces, mockPerformers, mockEntityEngagement, mockGoBack, mockAuth, noDeleteImpact } = vi.hoisted(() => ({
  noDeleteImpact: {
    detectionCount: 0,
    embeddingCount: 0,
    segmentCount: 0,
    hasCoverImage: false,
    releasedMergedFaceCount: 0,
  },
  mockFaces: {
    get: vi.fn(),
    similar: vi.fn(),
    appearances: vi.fn(),
    detections: vi.fn(),
    imageSources: async () => ({ cover: { hasStoredCover: false, sourceOnFace: false }, sources: [] }),
    deleteImpact: vi.fn(),
    update: vi.fn(),
    link: vi.fn(),
    batchLink: vi.fn(),
    recordSuggestionDecision: vi.fn(),
    setIgnored: vi.fn(),
    mergeInto: vi.fn(),
    delete: vi.fn(),
    list: vi.fn(),
    capabilities: vi.fn(),
    markNotPresent: vi.fn(),
    hostTracks: vi.fn(),
    split: vi.fn(),
    suggestions: vi.fn(),
  },
  mockPerformers: {
    find: vi.fn(),
  },
  mockEntityEngagement: {
    get: vi.fn(),
    setFavorite: vi.fn(),
    setRating: vi.fn(),
  },
  mockGoBack: vi.fn(),
  mockAuth: { canWrite: false },
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => {},
}));

vi.mock("../hooks/useResolvedKeybindingOverrides", () => ({
  useResolvedKeybindingOverrides: () => ({}),
}));

vi.mock("../api/client", () => ({
  faces: mockFaces,
  performers: mockPerformers,
  entityEngagement: mockEntityEngagement,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    user: { kind: "user" },
    hasPermission: (permission: string) => permission.endsWith(".read") || mockAuth.canWrite,
  }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({
    backLabel: "Back to faces",
    goBack: mockGoBack,
  }),
}));

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      // The app's client keeps data fresh for 30 seconds, so a tab that mounts does not refetch what the page has.
      queries: { retry: false, staleTime: 30_000 },
      mutations: { retry: false },
    },
  });
  const onNavigate = vi.fn();

  mockFaces.list.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 500 });

  // Rendered as App renders it: the review choices store above a route that gives each face its own page.
  const page = (faceId: number) => (
    <QueryClientProvider client={queryClient}>
      <SimilarReviewChoicesProvider>
        <FaceDetailRoute id={faceId} onNavigate={onNavigate} />
      </SimilarReviewChoicesProvider>
    </QueryClientProvider>
  );
  const { rerender, unmount } = render(page(7));

  return { onNavigate, unmount, queryClient, showFace: (faceId: number) => rerender(page(faceId)) };
}

describe("FaceDetailPage", () => {
  afterEach(() => {
    window.history.replaceState(null, "", "/");
    // Reset, not only clear: an implementation one test installs must not answer the next test's requests.
    vi.resetAllMocks();
    vi.restoreAllMocks();
    mockAuth.canWrite = false;
  });

  it("shows a retryable load error and recovers", async () => {
    mockFaces.get.mockRejectedValueOnce(new Error("API Error 502: upstream API Error 404")).mockResolvedValueOnce({
      id: 7,
      label: "Recovered face",
      ignored: false,
      detectionCount: 0,
      videoCount: 0,
      appearanceCount: 0,
      frameSampleCount: 0,
      imageCount: 0,
      createdAt: "2026-04-01T12:00:00Z",
      updatedAt: "2026-04-02T12:00:00Z",
    });
    mockFaces.similar.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 18 });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load face");
    expect(screen.queryByText("Face not found")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("heading", { name: "Recovered face" })).toBeInTheDocument();
  });

  it("keeps the not-found state for a genuine missing face", async () => {
    mockFaces.get.mockRejectedValue(new Error("API Error 404: Not Found"));
    mockFaces.similar.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 18 });
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();

    expect(await screen.findByText("Face not found")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("renders face metadata and similar faces", async () => {
    mockFaces.get.mockResolvedValue({
      id: 7,
      label: "Jane Cluster",
      performerId: 12,
      performerName: "Jane Doe",
      coverImageUrl: "/img/faces/7.jpg",
      ignored: false,
      mergedIntoFaceId: undefined,
      detectionCount: 18,
      videoCount: 4,
      appearanceCount: 6,
      frameSampleCount: 11,
      imageCount: 2,
      topSuggestion: undefined,
      primarySourceKey: "detector.facebox",
      createdAt: "2026-04-01T12:00:00Z",
      updatedAt: "2026-04-02T12:00:00Z",
    });
    mockFaces.similar.mockResolvedValue({
      items: [
        {
          id: 17,
          label: "Similar Jane",
          performerId: undefined,
          performerName: undefined,
          coverImageUrl: "/img/faces/17.jpg",
          ignored: false,
          mergedIntoFaceId: undefined,
          detectionCount: 9,
          videoCount: 3,
          imageCount: 1,
          primarySourceKey: "detector.facebox",
          createdAt: "2026-03-30T12:00:00Z",
          updatedAt: "2026-04-03T12:00:00Z",
          appearanceCount: 4,
          frameSampleCount: 7,
          distance: 0.1234,
        },
      ],
      totalCount: 20,
      page: 1,
      perPage: 18,
    });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    const { onNavigate } = renderPage();

    // A linked face shows the performer's name (faceDisplayName prefers performerName over label).
    expect(await screen.findByRole("heading", { name: "Jane Doe" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));

    expect(await screen.findByText("Nearest neighbors from the face embedding index.")).toBeInTheDocument();
    expect(await screen.findByText("Similar Jane")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Page 2" })).toHaveLength(2);
    expect(screen.getByRole("navigation", { name: "Pagination above results" })).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Pagination" })).toBeInTheDocument();
    expect(mockFaces.similar).toHaveBeenCalledWith(7, expect.objectContaining({ k: 250 }));
    expect(screen.getByRole("option", { name: "Random" })).toBeInTheDocument();
    expect(screen.getAllByRole("combobox").length).toBeGreaterThanOrEqual(2);

    // A similar face opens on its own Similar Faces tab, so a chain of similar faces stays one click apart.
    const similarLink = screen.getByRole("link", { name: "Open face Similar Jane" });
    expect(similarLink).toHaveAttribute("href", "/face/17?tab=similar");
    fireEvent.click(similarLink);
    expect(onNavigate).toHaveBeenCalledWith({ page: "face", id: 17, detailTab: "similar" });

    onNavigate.mockClear();
    fireEvent.click(screen.getByRole("button", { name: "List" }));
    fireEvent.click(await screen.findByRole("button", { name: /^Similar Jane/ }));
    expect(onNavigate).toHaveBeenCalledTimes(1);
    expect(onNavigate).toHaveBeenCalledWith({ page: "face", id: 17, detailTab: "similar" });
  });

  it("uses the back navigation hook", async () => {
    mockFaces.get.mockResolvedValue({
      id: 7,
      label: "Jane Cluster",
      performerId: undefined,
      performerName: undefined,
      coverImageUrl: undefined,
      ignored: false,
      mergedIntoFaceId: undefined,
      detectionCount: 3,
      videoCount: 1,
      appearanceCount: 1,
      frameSampleCount: 1,
      imageCount: 0,
      topSuggestion: undefined,
      primarySourceKey: undefined,
      createdAt: "2026-04-01T12:00:00Z",
      updatedAt: "2026-04-02T12:00:00Z",
    });
    mockFaces.similar.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 18 });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();
    await screen.findByRole("heading", { name: "Jane Cluster" });

    fireEvent.click(screen.getByRole("button", { name: "Back to faces" }));
    expect(mockGoBack).toHaveBeenCalled();
  });

  it("reviews the page's unlinked similar faces and links them in one batch, remembering skips", async () => {
    mockAuth.canWrite = true;
    mockFaces.get.mockResolvedValue(linkedFace);
    mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
    mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
    const reviewedPage = [
      similarFace({ id: 17, label: "Lookalike A" }),
      similarFace({ id: 18, label: "Other", performerId: 40, performerName: "Someone Else" }),
      similarFace({ id: 19, label: "Lookalike B" }),
      similarFace({ id: 20, label: "Lookalike C" }),
      similarFace({ id: 21, label: "Turned down", rejectedForSourcePerformer: true }),
      similarFace({ id: 22, label: "Ignored one", ignored: true }),
    ];
    // Answered by phase rather than call order: the page before the batch, then the page after it, whose
    // first refresh is held back to see the batch stay pending until the page shows the outcome.
    const afterBatch = {
      items: reviewedPage.map((item) =>
        item.id === 17 || item.id === 20
          ? { ...item, performerId: 12, performerName: "Jane Doe" }
          : item.id === 19
            ? { ...item, rejectedForSourcePerformer: true }
            : item,
      ),
      totalCount: 6,
      page: 1,
      perPage: 18,
    };
    let batchSent = false;
    let finishRefresh: () => void = () => {};
    const heldRefresh = new Promise((resolve) => {
      finishRefresh = () => resolve(afterBatch);
    });
    mockFaces.similar.mockImplementation(() =>
      batchSent ? heldRefresh : Promise.resolve({ items: reviewedPage, totalCount: 6, page: 1, perPage: 18 }),
    );
    mockFaces.batchLink.mockImplementation(async () => {
      batchSent = true;
      return { linked: [17, 20], rejected: [19], skipped: [], failed: [] };
    });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();
    await screen.findByRole("heading", { name: "Jane Doe" });
    fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));
    const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });

    // Every new unlinked face starts in the batch. A face already turned down for this performer stays out of it
    // until the reviewer links it after all, and a face linked to someone else or an ignored face is not offered.
    expect(within(review).getByRole("button", { name: "Link 3 faces to Jane Doe" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: /Other, skip it/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Turned down, skip it/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Ignored one, skip it/ })).not.toBeInTheDocument();
    expect(screen.getByText("Not Jane Doe")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Will link Lookalike B, skip it" }));
    expect(screen.getByRole("button", { name: "Skipped Lookalike B, link it after all" })).toBeInTheDocument();
    expect(within(review).getByText(/skipped as someone else/)).toBeInTheDocument();

    fireEvent.click(within(review).getByRole("button", { name: "Link 2 faces to Jane Doe" }));

    await waitFor(() =>
      expect(mockFaces.batchLink).toHaveBeenCalledWith({
        performerId: 12,
        faceIds: [17, 20],
        rejectFaceIds: [19],
        overrideRejectFaceIds: [],
      }),
    );
    expect(mockFaces.link).not.toHaveBeenCalled();

    // Until the refreshed page arrives the old one cannot be submitted again, which would link the skipped face.
    expect(await within(review).findByRole("button", { name: "Saving..." })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Skipped Lookalike B, link it after all" })).toBeDisabled();
    await act(async () => finishRefresh());
    await waitFor(() => expect(screen.getAllByText("Not Jane Doe")).toHaveLength(2));
    expect(mockFaces.batchLink).toHaveBeenCalledTimes(1);
    expect(within(review).getByRole("status")).toHaveTextContent(
      "Linked 2 faces to Jane Doe. Remembered 1 face as not Jane Doe.",
    );

    // Reviewed faces stay on the page they were on; hiding linked faces and faces marked as someone else brings
    // the next batch up.
    fireEvent.click(within(review).getByRole("button", { name: "Show faces left to review" }));
    await waitFor(() =>
      expect(mockFaces.similar).toHaveBeenLastCalledWith(
        7,
        expect.objectContaining({ linked: false, markedNotPerformer: false, page: 1 }),
      ),
    );
  });

  it("lets a face already marked as someone else be linked after all", async () => {
    mockAuth.canWrite = true;
    mockFaces.get.mockResolvedValue(linkedFace);
    mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
    mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
    mockFaces.similar.mockResolvedValue({
      items: [
        similarFace({ id: 17, label: "Lookalike A" }),
        similarFace({ id: 21, label: "Turned down", rejectedForSourcePerformer: true }),
      ],
      totalCount: 2,
      page: 1,
      perPage: 18,
    });
    mockFaces.batchLink.mockResolvedValue({ linked: [17, 21], rejected: [], skipped: [], failed: [] });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();
    await screen.findByRole("heading", { name: "Jane Doe" });
    fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));
    const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });

    // The earlier choice stands until it is changed: the face is left out of the batch.
    expect(within(review).getByRole("button", { name: "Link 1 face to Jane Doe" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Not Jane Doe Turned down, link it after all" }));
    expect(screen.getByRole("button", { name: "Will link Turned down, skip it" })).toBeInTheDocument();

    // Reset puts the saved choice back.
    fireEvent.click(within(review).getByRole("button", { name: "Reset choices" }));
    expect(screen.getByRole("button", { name: "Not Jane Doe Turned down, link it after all" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Not Jane Doe Turned down, link it after all" }));
    fireEvent.click(within(review).getByRole("button", { name: "Link 2 faces to Jane Doe" }));
    // It goes as an override of its mark, which the server needs to link it and to turn the reject into an
    // accept, and not as a new skip.
    await waitFor(() =>
      expect(mockFaces.batchLink).toHaveBeenCalledWith({
        performerId: 12,
        faceIds: [17],
        rejectFaceIds: [],
        overrideRejectFaceIds: [21],
      }),
    );
  });

  it("keeps each choice as made while the list refreshes, and keeps a failed link's choice for the next batch", async () => {
    mockAuth.canWrite = true;
    mockFaces.get.mockResolvedValue(linkedFace);
    mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
    mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);
    const page = (items: Record<string, unknown>[]) => ({
      items,
      totalCount: items.length,
      page: 1,
      perPage: 18,
    });
    const fresh = similarFace({ id: 17, label: "Fresh" });
    const skippedNow = similarFace({ id: 18, label: "Skipped now" });
    const savedReject = similarFace({ id: 19, label: "Saved reject", rejectedForSourcePerformer: true });
    // Answered by request and phase rather than call order: the first page, filtered to unlinked faces; the page
    // without that filter, held back, in which the face skipped now was meanwhile rejected elsewhere; and the page
    // after the batch.
    let batchSent = false;
    let finishRefresh: () => void = () => {};
    const heldUnfiltered = new Promise((resolve) => {
      finishRefresh = () =>
        resolve(page([fresh, similarFace({ ...skippedNow, rejectedForSourcePerformer: true }), savedReject]));
    });
    mockFaces.similar.mockImplementation((_faceId: number, options: { linked?: boolean }) =>
      batchSent
        ? Promise.resolve(page([savedReject]))
        : options.linked === false
          ? Promise.resolve(page([fresh, skippedNow, savedReject]))
          : heldUnfiltered,
    );
    mockFaces.batchLink.mockImplementation(async () => {
      batchSent = true;
      return { linked: [17], rejected: [], skipped: [], failed: [{ faceId: 19, error: "Host update failed." }] };
    });
    window.history.replaceState(
      null,
      "",
      `/face/7?tab=similar&filters=${encodeURIComponent(JSON.stringify({ linkedCriterion: { value: false } }))}`,
    );

    renderPage();
    await screen.findByRole("heading", { name: "Jane Doe" });
    const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
    fireEvent.click(screen.getByRole("button", { name: "Will link Skipped now, skip it" }));
    fireEvent.click(screen.getByRole("button", { name: "Not Jane Doe Saved reject, link it after all" }));
    expect(within(review).getByRole("button", { name: "Link 2 faces to Jane Doe" })).toBeEnabled();

    // The shortcut would hide the face chosen to link after all, so it waits for that choice to be linked or reset.
    expect(within(review).getByRole("button", { name: "Show faces left to review" })).toBeDisabled();
    expect(within(review).getByText(/Link or reset the 1 face you chose to link after all/)).toBeInTheDocument();

    // While the earlier list is still on screen nothing can be chosen or sent.
    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Linked" }));
    expect(within(review).getByRole("button", { name: "Link 2 faces to Jane Doe" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Will link Fresh, skip it" })).toBeDisabled();
    await act(async () => finishRefresh());

    // The face skipped now stays out although its saved state changed, and the saved reject stays in.
    expect(await screen.findByRole("button", { name: "Not Jane Doe Skipped now, link it after all" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Will link Saved reject, skip it" })).toBeInTheDocument();
    fireEvent.click(within(review).getByRole("button", { name: "Link 2 faces to Jane Doe" }));
    await waitFor(() =>
      expect(mockFaces.batchLink).toHaveBeenCalledWith({
        performerId: 12,
        faceIds: [17],
        rejectFaceIds: [],
        overrideRejectFaceIds: [19],
      }),
    );

    // The saved reject failed to link, so it is still offered as the reviewer chose it.
    expect(await within(review).findByText(/1 face could not be linked/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Will link Saved reject, skip it" })).toBeEnabled();
  });

  it("keeps the outcome of the batch that leaves no faces to review", async () => {
    mockAuth.canWrite = true;
    mockFaces.get.mockResolvedValue(linkedFace);
    mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
    mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);
    let batchSent = false;
    const remaining = [similarFace({ id: 17, label: "Last one" }), similarFace({ id: 18, label: "Someone else" })];
    mockFaces.similar.mockImplementation(async () => {
      const items = batchSent ? [] : remaining;
      return { items, totalCount: items.length, page: 1, perPage: 18 };
    });
    mockFaces.batchLink.mockImplementation(async () => {
      batchSent = true;
      return { linked: [17], rejected: [18], skipped: [], failed: [] };
    });
    window.history.replaceState(
      null,
      "",
      `/face/7?tab=similar&filters=${encodeURIComponent(
        JSON.stringify({ linkedCriterion: { value: false }, markedNotPerformerCriterion: { value: false } }),
      )}`,
    );

    renderPage();
    const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
    expect(mockFaces.similar).toHaveBeenCalledWith(
      7,
      expect.objectContaining({ linked: false, markedNotPerformer: false }),
    );
    // Already showing only faces left to review, so the shortcut has nothing to add.
    expect(within(review).queryByRole("button", { name: "Show faces left to review" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Will link Someone else, skip it" }));
    fireEvent.click(within(review).getByRole("button", { name: "Link 1 face to Jane Doe" }));

    expect(await screen.findByText("No faces are left to review for Jane Doe.")).toBeInTheDocument();
    expect(screen.queryByText("No faces on this page will be linked to Jane Doe.")).not.toBeInTheDocument();
    expect(
      within(screen.getByRole("region", { name: "Review similar faces for Jane Doe" })).getByRole("status"),
    ).toHaveTextContent("Linked 1 face to Jane Doe. Remembered 1 face as not Jane Doe.");
  });

  it("offers the shortcut on a list filtered to unlinked faces only, and sends the marked filter", async () => {
    mockAuth.canWrite = true;
    mockFaces.get.mockResolvedValue(linkedFace);
    mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
    mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);
    mockFaces.similar.mockResolvedValue({
      items: [similarFace({ id: 17, label: "Candidate" })],
      totalCount: 1,
      page: 1,
      perPage: 18,
    });
    window.history.replaceState(
      null,
      "",
      `/face/7?tab=similar&filters=${encodeURIComponent(JSON.stringify({ linkedCriterion: { value: false } }))}`,
    );

    renderPage();
    const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
    fireEvent.click(within(review).getByRole("button", { name: "Show faces left to review" }));
    await waitFor(() =>
      expect(mockFaces.similar).toHaveBeenLastCalledWith(
        7,
        expect.objectContaining({ linked: false, markedNotPerformer: false }),
      ),
    );
  });

  it("refreshes the similar list after a failed batch, since its skips may already be saved", async () => {
    mockAuth.canWrite = true;
    mockFaces.get.mockResolvedValue(linkedFace);
    mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
    mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);
    mockFaces.similar.mockResolvedValue({
      items: [similarFace({ id: 17, label: "Lookalike A" }), similarFace({ id: 18, label: "Lookalike B" })],
      totalCount: 2,
      page: 1,
      perPage: 18,
    });
    mockFaces.batchLink.mockRejectedValue(new Error("API Error 500: Internal Server Error"));

    renderPage();
    await screen.findByRole("heading", { name: "Jane Doe" });
    fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));
    const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
    fireEvent.click(screen.getByRole("button", { name: "Will link Lookalike B, skip it" }));
    const callsBefore = mockFaces.similar.mock.calls.length;

    fireEvent.click(within(review).getByRole("button", { name: "Link 1 face to Jane Doe" }));

    expect(await within(review).findByRole("alert")).toHaveTextContent("Internal Server Error");
    await waitFor(() => expect(mockFaces.similar.mock.calls.length).toBeGreaterThan(callsBefore));
  });

  it("offers no review for the similar faces of a face that is not linked yet", async () => {
    mockAuth.canWrite = true;
    mockFaces.get.mockResolvedValue({
      ...linkedFace,
      label: "Unlinked lead",
      performerId: undefined,
      performerName: undefined,
    });
    mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
    mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
    mockFaces.suggestions.mockResolvedValue([]);
    mockFaces.similar.mockResolvedValue({
      items: [similarFace({ id: 17, label: "Lookalike A" })],
      totalCount: 1,
      page: 1,
      perPage: 18,
    });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();
    await screen.findByRole("heading", { name: "Unlinked lead" });
    fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));
    await screen.findByText("Lookalike A");

    expect(screen.queryByRole("region", { name: /Review similar faces/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /skip it$/ })).not.toBeInTheDocument();
  });

  it("corrects a wrong appearance from the Appears In tab", async () => {
    mockAuth.canWrite = true;
    vi.spyOn(window, "confirm").mockReturnValue(true);
    mockFaces.get.mockResolvedValue(linkedFace);
    mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: true, canSuggest: false });
    mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
    mockFaces.similar.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 18 });
    mockFaces.detections.mockResolvedValue([]);
    mockFaces.appearances.mockResolvedValue({
      items: [
        {
          appearanceId: 91,
          hostType: "video",
          hostId: 5,
          title: "Wrong person video",
          thumbnailUrl: "/thumb/5.jpg",
          frameSampleCount: 4,
          retainedSpatialSampleCount: 2,
          segmentCount: 1,
        },
      ],
      totalCount: 1,
      page: 1,
      perPage: 24,
    });
    mockFaces.hostTracks.mockResolvedValue([
      { groupKey: "a", sampleCount: 2, detectionCount: 2, firstSeenSeconds: 12, suggestedGroup: 0 },
    ]);
    mockFaces.markNotPresent.mockResolvedValue({ targetFaceId: 44, createdNewFace: true });
    mockEntityEngagement.get.mockResolvedValue(null);

    renderPage();
    await screen.findByRole("heading", { name: "Jane Doe" });
    fireEvent.click(screen.getByRole("tab", { name: /Appears In/i }));

    // The only appearance in the video is someone else, so the face is not in that video at all.
    fireEvent.click(await screen.findByRole("button", { name: /Select the appearance in Wrong person video/ }));
    fireEvent.click(screen.getByRole("button", { name: /Not this person/ }));
    fireEvent.click(screen.getByRole("button", { name: "Move" }));
    await waitFor(() => expect(mockFaces.markNotPresent).toHaveBeenCalledWith(7, { hostType: "video", hostId: 5 }));
    expect(await screen.findByText(/moved to a/)).toBeInTheDocument();
  });

  describe("searches keep their results in place while the next keystroke loads", () => {
    function mockUnlinkedFace() {
      mockAuth.canWrite = true;
      mockFaces.get.mockResolvedValue({
        ...linkedFace,
        label: "Unlinked lead",
        performerId: undefined,
        performerName: undefined,
      });
      mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
      mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
      mockFaces.suggestions.mockResolvedValue([]);
      mockFaces.similar.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 18 });
      mockFaces.detections.mockResolvedValue([]);
      mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
      mockEntityEngagement.get.mockResolvedValue(null);
    }

    /**
     * Answers searches by their text, holding back the one for `heldQuery` until `finish` is called. Any
     * other call the page makes gets an empty page, so it cannot take a response meant for the search.
     */
    function answerSearches<T>(mock: ReturnType<typeof vi.fn>, answers: Record<string, T>, heldQuery: string) {
      const held = { finish: () => {} };
      const empty = { items: [], totalCount: 0, page: 1, perPage: 6 };
      mock.mockImplementation((options?: { q?: string }) => {
        const q = options?.q ?? "";
        if (q !== heldQuery) return Promise.resolve(answers[q] ?? empty);
        return new Promise((resolve) => {
          held.finish = () => resolve(answers[q]);
        });
      });
      return held;
    }

    const performerPage = (id: number, name: string) => ({ items: [{ id, name }], totalCount: 1, page: 1, perPage: 6 });

    it("in Link to performer", async () => {
      mockUnlinkedFace();
      renderPage();
      const search = answerSearches(
        mockPerformers.find,
        { Alex: performerPage(9, "Alexa Example"), Alexi: performerPage(10, "Alexis Example") },
        "Alexi",
      );
      await screen.findByRole("heading", { name: "Unlinked lead" });
      const input = screen.getByPlaceholderText("Search performers");
      fireEvent.change(input, { target: { value: "Alex" } });
      const firstResult = await screen.findByRole("button", { name: /Alexa Example/ });

      fireEvent.change(input, { target: { value: "Alexi" } });

      expect(screen.queryByText("Searching performers...")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: /Alexa Example/ })).toBe(firstResult);
      expect(firstResult).toBeDisabled();
      expect(firstResult.parentElement).toHaveAttribute("aria-busy", "true");

      search.finish();
      expect(await screen.findByRole("button", { name: /Alexis Example/ })).toBeEnabled();
      expect(screen.getByRole("button", { name: /Alexis Example/ }).parentElement).not.toHaveAttribute("aria-busy");
    });

    it("says it is searching, not that nothing matched, while text after an empty answer loads", async () => {
      mockUnlinkedFace();
      renderPage();
      const search = answerSearches(
        mockPerformers.find,
        { Zz: { items: [], totalCount: 0, page: 1, perPage: 6 }, Zza: performerPage(11, "Zza Example") },
        "Zza",
      );
      await screen.findByRole("heading", { name: "Unlinked lead" });
      const input = screen.getByPlaceholderText("Search performers");
      fireEvent.change(input, { target: { value: "Zz" } });
      expect(await screen.findByText("No performers matched that search.")).toBeInTheDocument();

      fireEvent.change(input, { target: { value: "Zza" } });

      expect(screen.queryByText("No performers matched that search.")).not.toBeInTheDocument();
      expect(screen.getByText("Searching performers...")).toBeInTheDocument();
      search.finish();
      expect(await screen.findByRole("button", { name: /Zza Example/ })).toBeEnabled();
    });

    it("in the merge target search", async () => {
      mockUnlinkedFace();
      renderPage();
      const target = (id: number, label: string) => ({
        items: [{ ...linkedFace, id, label, performerId: undefined, performerName: undefined }],
        totalCount: 1,
        page: 1,
        perPage: 6,
      });
      const search = answerSearches(
        mockFaces.list,
        { Target: target(30, "Target one"), "Target t": target(31, "Target two") },
        "Target t",
      );
      await screen.findByRole("heading", { name: "Unlinked lead" });
      fireEvent.click(screen.getByTitle("More actions"));
      fireEvent.click(screen.getByRole("button", { name: "Merge" }));
      const input = screen.getByPlaceholderText("Search by face label or linked performer");
      fireEvent.change(input, { target: { value: "Target" } });
      const firstResult = await screen.findByRole("button", { name: /Target one/ });

      fireEvent.change(input, { target: { value: "Target t" } });

      expect(screen.queryByText("Searching faces...")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: /Target one/ })).toBe(firstResult);
      expect(firstResult).toBeDisabled();

      search.finish();
      expect(await screen.findByRole("button", { name: /Target two/ })).toBeEnabled();
    });

    it("says a failed performer search failed, offers a retry, and holds Create Performer back meanwhile", async () => {
      mockUnlinkedFace();
      renderPage();
      let failing = true;
      mockPerformers.find.mockImplementation(({ q }: { q?: string }) =>
        q === "Alexis" && failing
          ? Promise.reject(new Error("API Error 502: Bad Gateway"))
          : Promise.resolve(
              q === "Alexis" ? performerPage(10, "Alexis Example") : { items: [], totalCount: 0, page: 1, perPage: 6 },
            ),
      );
      await screen.findByRole("heading", { name: "Unlinked lead" });
      const createPerformer = screen.getByRole("button", { name: "Create Performer" });
      expect(createPerformer).toBeEnabled();

      fireEvent.change(screen.getByPlaceholderText("Search performers"), { target: { value: "Alexis" } });

      // A failure is not "no matches": the performer may well exist, so creating one now could duplicate it.
      const alert = await screen.findByRole("alert");
      expect(alert).toHaveTextContent("Could not search performers");
      expect(screen.queryByText("No performers matched that search.")).not.toBeInTheDocument();
      expect(createPerformer).toBeDisabled();
      expect(createPerformer).toHaveAttribute("title", expect.stringMatching(/search failed/i));

      failing = false;
      fireEvent.click(within(alert).getByRole("button", { name: "Try again" }));
      expect(await screen.findByRole("button", { name: /Alexis Example/ })).toBeEnabled();
      expect(screen.queryByRole("alert")).not.toBeInTheDocument();
      expect(createPerformer).toBeEnabled();
    });

    it("says a failed merge target search failed and offers a retry", async () => {
      mockUnlinkedFace();
      renderPage();
      let failing = true;
      mockFaces.list.mockImplementation((options?: { q?: string }) =>
        options?.q === "Target" && failing
          ? Promise.reject(new Error("API Error 502: Bad Gateway"))
          : Promise.resolve({
              items:
                options?.q === "Target"
                  ? [{ ...linkedFace, id: 30, label: "Target one", performerId: undefined, performerName: undefined }]
                  : [],
              totalCount: 1,
              page: 1,
              perPage: 6,
            }),
      );
      await screen.findByRole("heading", { name: "Unlinked lead" });
      fireEvent.click(screen.getByTitle("More actions"));
      fireEvent.click(screen.getByRole("button", { name: "Merge" }));
      fireEvent.change(screen.getByPlaceholderText("Search by face label or linked performer"), {
        target: { value: "Target" },
      });

      const alert = await screen.findByRole("alert");
      expect(alert).toHaveTextContent("Could not search faces");
      expect(screen.queryByText("No merge targets matched that search.")).not.toBeInTheDocument();

      failing = false;
      fireEvent.click(within(alert).getByRole("button", { name: "Try again" }));
      expect(await screen.findByRole("button", { name: /Target one/ })).toBeEnabled();
    });
  });

  describe("list settings in the URL", () => {
    function mockLinkedFaceWithManySimilar() {
      mockFaces.get.mockResolvedValue(linkedFace);
      mockFaces.similar.mockResolvedValue({
        items: [similarFace({ label: "Similar Jane" })],
        totalCount: 40,
        page: 1,
        perPage: 18,
      });
      mockFaces.detections.mockResolvedValue([]);
      mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
      mockEntityEngagement.get.mockResolvedValue(null);
    }
    const urlParam = (key: string) => new URLSearchParams(window.location.search).get(key);

    it("records the Similar Faces page and search in the URL", async () => {
      window.history.replaceState(null, "", "/face/7?tab=similar");
      mockLinkedFaceWithManySimilar();

      renderPage();
      await screen.findByText("Similar Jane");
      fireEvent.click(screen.getAllByRole("button", { name: "Page 2" })[0]);
      await waitFor(() => expect(urlParam("page")).toBe("2"));
      fireEvent.change(screen.getByPlaceholderText("Search…"), { target: { value: "Jane" } });
      await waitFor(() => expect(urlParam("q")).toBe("Jane"));
      expect(urlParam("tab")).toBe("similar");
    });

    it("restores the Similar Faces list from a URL that carries its settings", async () => {
      window.history.replaceState(null, "", "/face/7?tab=similar&page=2&q=Jane&view=list");
      mockLinkedFaceWithManySimilar();

      renderPage();

      await waitFor(() =>
        expect(mockFaces.similar).toHaveBeenCalledWith(7, expect.objectContaining({ page: 2, q: "Jane" })),
      );
      expect(screen.getByPlaceholderText("Search…")).toHaveValue("Jane");
      // List view shows rows rather than tiles.
      expect(await screen.findByRole("button", { name: /^Similar Jane/ })).toBeInTheDocument();
    });

    // App gives each face its own page, but the lists must follow the URL even when the page is reused.
    it("starts another face's lists from their defaults", async () => {
      window.history.replaceState(null, "", "/face/7?tab=similar&page=2&q=Jane");
      mockLinkedFaceWithManySimilar();

      const { showFace } = renderPage();
      await waitFor(() =>
        expect(mockFaces.similar).toHaveBeenCalledWith(7, expect.objectContaining({ page: 2, q: "Jane" })),
      );

      act(() => {
        navigateToUrl("/face/8?tab=similar");
      });
      showFace(8);

      // Only the list asks for face 8's similar faces, at its defaults; the badge reads the same answer.
      await waitFor(() => expect(mockFaces.similar.mock.calls.filter(([faceId]) => faceId === 8)).toHaveLength(1));
      expect(mockFaces.similar.mock.calls.find(([faceId]) => faceId === 8)?.[1]).toEqual(
        expect.objectContaining({ page: 1, perPage: 18, q: undefined }),
      );
      expect(await screen.findByPlaceholderText("Search…")).toHaveValue("");
      expect(urlParam("page")).toBeNull();
    });

    it("brings a list's settings back when its tab is picked again", async () => {
      window.history.replaceState(null, "", "/face/7?tab=similar&page=2&q=Jane");
      mockLinkedFaceWithManySimilar();

      renderPage();
      await screen.findByText("Similar Jane");
      fireEvent.click(screen.getByRole("tab", { name: /Overview/i }));
      await waitFor(() => expect(urlParam("tab")).toBeNull());
      expect(urlParam("page")).toBeNull();

      fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));
      await waitFor(() => expect(urlParam("page")).toBe("2"));
      expect(urlParam("q")).toBe("Jane");
      expect(screen.getByPlaceholderText("Search…")).toHaveValue("Jane");
    });

    it("does not carry one tab's list settings into the other", async () => {
      window.history.replaceState(null, "", "/face/7?tab=similar&page=2&q=Jane");
      mockLinkedFaceWithManySimilar();

      renderPage();
      await screen.findByText("Similar Jane");
      fireEvent.click(screen.getByRole("tab", { name: /Appears In/i }));

      await waitFor(() => expect(urlParam("tab")).toBe("appearances"));
      expect(urlParam("page")).toBeNull();
      expect(urlParam("q")).toBeNull();
      // The face reports its appearance count, so the only appearances request is the list's own.
      await waitFor(() => expect(mockFaces.appearances).toHaveBeenCalledTimes(1));
      expect(mockFaces.appearances).toHaveBeenCalledWith(7, expect.objectContaining({ page: 1, q: undefined }));
    });

    it("writes Show faces left to review into the URL and goes back to the first page", async () => {
      mockAuth.canWrite = true;
      mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
      mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
      window.history.replaceState(null, "", "/face/7?tab=similar&page=2");
      mockLinkedFaceWithManySimilar();

      renderPage();
      const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
      fireEvent.click(within(review).getByRole("button", { name: "Show faces left to review" }));

      await waitFor(() =>
        expect(JSON.parse(urlParam("filters") ?? "{}")).toEqual({
          linkedCriterion: { value: false },
          markedNotPerformerCriterion: { value: false },
        }),
      );
      expect(urlParam("page")).toBeNull();
    });
  });

  describe("reviewing similar faces across tabs and faces", () => {
    function mockReviewableFace() {
      mockAuth.canWrite = true;
      mockFaces.get.mockImplementation(async (faceId: number) => ({ ...linkedFace, id: faceId }));
      mockFaces.capabilities.mockResolvedValue({ canEditOccurrences: false, canSuggest: false });
      mockFaces.deleteImpact.mockResolvedValue(noDeleteImpact);
      mockFaces.detections.mockResolvedValue([]);
      mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
      mockEntityEngagement.get.mockResolvedValue(null);
    }
    const page = (items: Record<string, unknown>[]) => ({
      items,
      totalCount: items.length,
      page: 1,
      perPage: 18,
    });

    /** A page with "Keep" and "Not them", and a batch that holds its answer until `finishBatch` is called. */
    function mockBatchFlow(afterBatch: () => Promise<unknown>) {
      const keep = similarFace({ id: 17, label: "Keep" });
      const notThem = similarFace({ id: 18, label: "Not them" });
      const state = { batchSent: false, finishBatch: () => {} };
      mockFaces.similar.mockImplementation(() =>
        state.batchSent ? afterBatch() : Promise.resolve(page([keep, notThem])),
      );
      mockFaces.batchLink.mockImplementation(
        () =>
          new Promise((resolve) => {
            state.finishBatch = () => {
              state.batchSent = true;
              resolve({ linked: [17], rejected: [18], skipped: [], failed: [] });
            };
          }),
      );
      return state;
    }
    const refreshedPage = () =>
      page([
        similarFace({ id: 17, label: "Keep", performerId: 12, performerName: "Jane Doe" }),
        similarFace({ id: 18, label: "Not them", rejectedForSourcePerformer: true }),
      ]);

    async function skipNotThemAndLink() {
      const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
      fireEvent.click(await screen.findByRole("button", { name: "Will link Not them, skip it" }));
      fireEvent.click(within(review).getByRole("button", { name: "Link 1 face to Jane Doe" }));
      await waitFor(() => expect(mockFaces.batchLink).toHaveBeenCalledTimes(1));
      return review;
    }

    it("keeps a skip as made on a page from before the batch, after leaving the tab while it saved", async () => {
      mockReviewableFace();
      const flow = mockBatchFlow(() => Promise.resolve(refreshedPage()));
      window.history.replaceState(null, "", "/face/7?tab=similar");

      renderPage();
      await skipNotThemAndLink();
      fireEvent.click(screen.getByRole("tab", { name: /Overview/i }));
      await act(async () => {
        flow.finishBatch();
        await new Promise((resolve) => setTimeout(resolve, 0));
      });
      fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));

      // Whichever page shows first, the skip still reads as made, and the list comes back refreshed.
      expect(
        await screen.findByRole("button", { name: /^(Skipped|Not Jane Doe) Not them, link it after all$/ }),
      ).toBeInTheDocument();
      expect(await screen.findByRole("button", { name: "Not Jane Doe Not them, link it after all" })).toBeEnabled();
      expect(mockFaces.batchLink).toHaveBeenCalledTimes(1);
    });

    it("says when the list could not be refreshed and offers to try again", async () => {
      mockReviewableFace();
      let refreshFails = true;
      const flow = mockBatchFlow(() =>
        refreshFails ? Promise.reject(new Error("API Error 504: Gateway Timeout")) : Promise.resolve(refreshedPage()),
      );
      window.history.replaceState(null, "", "/face/7?tab=similar");

      renderPage();
      const review = await skipNotThemAndLink();
      await act(async () => flow.finishBatch());

      const alert = await within(review).findByRole("alert");
      expect(alert).toHaveTextContent("could not be refreshed");
      // The page is the one from before the batch; the skip still reads as made.
      expect(screen.getByRole("button", { name: "Skipped Not them, link it after all" })).toBeInTheDocument();

      refreshFails = false;
      fireEvent.click(within(alert).getByRole("button", { name: "Try again" }));
      expect(await screen.findByRole("button", { name: "Not Jane Doe Not them, link it after all" })).toBeEnabled();
      expect(within(review).queryByRole("alert")).not.toBeInTheDocument();
    });

    it("keeps the review held when the reviewer leaves the face during a batch and comes back", async () => {
      mockReviewableFace();
      const flow = mockBatchFlow(() => Promise.resolve(refreshedPage()));
      window.history.replaceState(null, "", "/face/7?tab=similar");

      const { showFace } = renderPage();
      await skipNotThemAndLink();
      act(() => {
        navigateToUrl("/face/8?tab=similar");
      });
      showFace(8);
      await waitFor(() => expect(mockFaces.similar).toHaveBeenCalledWith(8, expect.anything()));
      act(() => {
        navigateToUrl("/face/7?tab=similar");
      });
      showFace(7);

      // The batch sent before leaving still holds the page, which shows the choices from the store.
      const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
      expect(within(review).getByRole("button", { name: "Saving..." })).toBeDisabled();
      expect(screen.getByRole("button", { name: "Skipped Not them, link it after all" })).toBeDisabled();

      await act(async () => flow.finishBatch());
      expect(await screen.findByRole("button", { name: "Not Jane Doe Not them, link it after all" })).toBeEnabled();
      expect(mockFaces.batchLink).toHaveBeenCalledTimes(1);
    });

    it("lets the faces a batch linked drop their choices, so a later mark on them counts again", async () => {
      mockReviewableFace();
      let batchSent = false;
      // After the batch linked it, the face was unlinked and marked as someone else again elsewhere.
      mockFaces.similar.mockImplementation(async () =>
        page([similarFace({ id: 19, label: "Saved reject", rejectedForSourcePerformer: true })]),
      );
      mockFaces.batchLink.mockImplementation(async () => {
        batchSent = true;
        return { linked: [19], rejected: [], skipped: [], failed: [] };
      });
      window.history.replaceState(null, "", "/face/7?tab=similar");

      renderPage();
      const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
      fireEvent.click(await screen.findByRole("button", { name: "Not Jane Doe Saved reject, link it after all" }));
      fireEvent.click(within(review).getByRole("button", { name: "Link 1 face to Jane Doe" }));
      await waitFor(() => expect(batchSent).toBe(true));

      // The choice to link it was carried out, so it no longer stands over the newer mark.
      expect(await within(review).findByRole("status")).toHaveTextContent("Linked 1 face");
      expect(screen.getByRole("button", { name: "Not Jane Doe Saved reject, link it after all" })).toBeEnabled();
    });

    // A list can show a face as an ordinary candidate after the reviewer marked it as someone else: a search
    // answered from before the batch, another face's list from before it, or a list from before the face was
    // linked to another performer. Whatever the list shows, the face goes only as an ordinary candidate, never as
    // an override, and the server leaves it alone; the review reports that.
    const markedSkip = { faceId: 18, reason: "Face is marked as not this performer." };

    it("sends a skipped face shown by a search answered from before the batch only as a candidate the server refuses", async () => {
      mockReviewableFace();
      const keep = similarFace({ id: 17, label: "Keep" });
      const notThem = similarFace({ id: 18, label: "Not them" });
      let answerSearch: () => void = () => {};
      const heldSearch = new Promise((resolve) => {
        answerSearch = () => resolve(page([keep, notThem]));
      });
      mockFaces.similar.mockImplementation((_faceId: number, options: { q?: string }) =>
        options.q ? heldSearch : Promise.resolve(page([keep, notThem])),
      );
      let finishBatch: () => void = () => {};
      mockFaces.batchLink
        .mockImplementationOnce(
          () =>
            new Promise((resolve) => {
              finishBatch = () => resolve({ linked: [17], rejected: [18], skipped: [], failed: [] });
            }),
        )
        .mockResolvedValue({ linked: [], rejected: [], skipped: [markedSkip], failed: [] });
      window.history.replaceState(null, "", "/face/7?tab=similar");

      renderPage();
      const review = await skipNotThemAndLink();
      fireEvent.change(screen.getByPlaceholderText("Search…"), { target: { value: "o" } });
      await waitFor(() => expect(mockFaces.similar).toHaveBeenCalledWith(7, expect.objectContaining({ q: "o" })));
      await act(async () => {
        finishBatch();
        await new Promise((resolve) => setTimeout(resolve, 0));
      });
      await act(async () => {
        answerSearch();
        await new Promise((resolve) => setTimeout(resolve, 0));
      });

      // The page answers from before the batch, but the skip still reads as made, so the face goes as a skip again.
      fireEvent.click(await within(review).findByRole("button", { name: /^(Link|Mark) / }));
      await waitFor(() => expect(mockFaces.batchLink).toHaveBeenCalledTimes(2));
      const second = mockFaces.batchLink.mock.calls[1][0];
      expect(second.faceIds).not.toContain(18);
      expect(second.overrideRejectFaceIds).not.toContain(18);
    });

    it("sends a face another face's old list still offers only as a candidate the server refuses", async () => {
      mockReviewableFace();
      mockFaces.suggestions.mockResolvedValue([]);
      let batchDone = false;
      let answerRefresh17: () => void = () => {};
      const heldRefresh17 = new Promise((resolve) => {
        answerRefresh17 = () =>
          resolve(page([similarFace({ id: 18, label: "Not them", rejectedForSourcePerformer: true })]));
      });
      mockFaces.similar.mockImplementation((faceId: number) => {
        if (faceId === 17)
          return batchDone ? heldRefresh17 : Promise.resolve(page([similarFace({ id: 18, label: "Not them" })]));
        return Promise.resolve(
          page([similarFace({ id: 17, label: "Keep" }), similarFace({ id: 18, label: "Not them" })]),
        );
      });
      mockFaces.batchLink
        .mockImplementationOnce(async () => {
          batchDone = true;
          return { linked: [17], rejected: [18], skipped: [], failed: [] };
        })
        .mockResolvedValue({ linked: [], rejected: [], skipped: [markedSkip], failed: [] });
      // Face 17\x27s list was looked at first and is cached from before the batch.
      window.history.replaceState(null, "", "/face/17?tab=similar");
      const { showFace } = renderPage();
      showFace(17);
      await screen.findByText("Not them");
      act(() => navigateToUrl("/face/7?tab=similar"));
      showFace(7);
      await skipNotThemAndLink();
      expect(await screen.findByRole("button", { name: "Skipped Not them, link it after all" })).toBeEnabled();

      act(() => navigateToUrl("/face/17?tab=similar"));
      showFace(17);
      const review17 = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
      // The batch marked every face's similar list out of date, so face 17's is fetched again on the way in.
      await waitFor(() => expect(mockFaces.similar.mock.calls.filter(([faceId]) => faceId === 17)).toHaveLength(2));
      fireEvent.click(within(review17).getByRole("button", { name: "Link 1 face to Jane Doe" }));

      await waitFor(() => expect(mockFaces.batchLink).toHaveBeenCalledTimes(2));
      expect(mockFaces.batchLink.mock.calls[1][0]).toEqual({
        performerId: 12,
        faceIds: [18],
        rejectFaceIds: [],
        overrideRejectFaceIds: [],
      });
      expect(await within(review17).findByRole("status")).toHaveTextContent(
        "Left 1 face unchanged: Face is marked as not this performer.",
      );
      await act(async () => answerRefresh17());
    });

    it("sends a face a list from before a performer change offers only as a candidate the server refuses", async () => {
      mockReviewableFace();
      let performer = 12;
      mockFaces.get.mockImplementation(async (faceId: number) =>
        performer === 12
          ? { ...linkedFace, id: faceId }
          : { ...linkedFace, id: faceId, performerId: 13, performerName: "Other Person" },
      );
      // Face 18 was marked as not Other Person earlier; the list for them never arrives.
      mockFaces.similar.mockImplementation(() =>
        performer === 12 ? Promise.resolve(page([similarFace({ id: 18, label: "Not them" })])) : new Promise(() => {}),
      );
      mockFaces.batchLink.mockResolvedValue({ linked: [], rejected: [], skipped: [markedSkip], failed: [] });
      window.history.replaceState(null, "", "/face/7?tab=similar");

      const { queryClient } = renderPage();
      await screen.findByRole("button", { name: "Will link Not them, skip it" });
      performer = 13;
      await act(async () => {
        void queryClient.invalidateQueries({ queryKey: ["face", 7] });
        await new Promise((resolve) => setTimeout(resolve, 0));
      });
      const review = await screen.findByRole("region", { name: "Review similar faces for Other Person" });
      fireEvent.click(within(review).getByRole("button", { name: "Link 1 face to Other Person" }));

      await waitFor(() =>
        expect(mockFaces.batchLink).toHaveBeenCalledWith({
          performerId: 13,
          faceIds: [18],
          rejectFaceIds: [],
          overrideRejectFaceIds: [],
        }),
      );
      expect(await within(review).findByRole("status")).toHaveTextContent(
        "Left 1 face unchanged: Face is marked as not this performer.",
      );
    });

    it("marks every face's similar list out of date after a batch, not only the reviewed face's", async () => {
      mockReviewableFace();
      mockFaces.similar.mockImplementation(async (faceId: number) =>
        faceId === 20 ? page([]) : page([similarFace({ id: 18, label: "Not them" })]),
      );
      mockFaces.batchLink.mockResolvedValue({ linked: [], rejected: [18], skipped: [], failed: [] });
      // Face 20, which the batch does not touch, was looked at first and its list is cached.
      window.history.replaceState(null, "", "/face/20?tab=similar");
      const { showFace } = renderPage();
      showFace(20);
      await waitFor(() => expect(mockFaces.similar).toHaveBeenCalledWith(20, expect.anything()));
      act(() => navigateToUrl("/face/7?tab=similar"));
      showFace(7);
      const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
      fireEvent.click(await screen.findByRole("button", { name: "Will link Not them, skip it" }));
      fireEvent.click(within(review).getByRole("button", { name: "Mark 1 face as not Jane Doe" }));
      expect(await within(review).findByRole("status")).toHaveTextContent("Remembered 1 face");

      act(() => navigateToUrl("/face/20?tab=similar"));
      showFace(20);
      await waitFor(() => expect(mockFaces.similar.mock.calls.filter(([faceId]) => faceId === 20)).toHaveLength(2));
    });

    it("holds every face's review while any face's batch is saving", async () => {
      mockReviewableFace();
      mockFaces.similar.mockImplementation(async (faceId: number) =>
        page([similarFace({ id: faceId === 20 ? 30 : 18, label: faceId === 20 ? "Other look-alike" : "Not them" })]),
      );
      let finishBatch: () => void = () => {};
      mockFaces.batchLink.mockImplementation(
        () =>
          new Promise((resolve) => {
            finishBatch = () => resolve({ linked: [], rejected: [18], skipped: [], failed: [] });
          }),
      );
      window.history.replaceState(null, "", "/face/7?tab=similar");

      const { showFace } = renderPage();
      const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
      fireEvent.click(await screen.findByRole("button", { name: "Will link Not them, skip it" }));
      fireEvent.click(within(review).getByRole("button", { name: "Mark 1 face as not Jane Doe" }));
      await waitFor(() => expect(mockFaces.batchLink).toHaveBeenCalledTimes(1));

      // Face 20, linked to the same performer, is opened while face 7's batch saves: two batches at once could
      // race, so its review waits and says why.
      act(() => navigateToUrl("/face/20?tab=similar"));
      showFace(20);
      const review20 = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
      expect(
        await within(review20).findByText("Another face's batch is saving. This review waits until it is done."),
      ).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Will link Other look-alike, skip it" })).toBeDisabled();
      expect(within(review20).getByRole("button", { name: "Link 1 face to Jane Doe" })).toBeDisabled();

      await act(async () => finishBatch());
      await waitFor(() =>
        expect(screen.getByRole("button", { name: "Will link Other look-alike, skip it" })).toBeEnabled(),
      );
      expect(within(review20).queryByText(/Another face's batch is saving/)).not.toBeInTheDocument();
    });

    it("keeps a review to the performer it was made for", async () => {
      mockReviewableFace();
      mockFaces.similar.mockResolvedValue(page([similarFace({ id: 18, label: "Not them" })]));
      window.history.replaceState(null, "", "/face/7?tab=similar");

      const { queryClient } = renderPage();
      fireEvent.click(await screen.findByRole("button", { name: "Will link Not them, skip it" }));

      // The face is linked to another performer meanwhile: the skip was about the first one.
      mockFaces.get.mockResolvedValue({ ...linkedFace, performerId: 13, performerName: "Other Person" });
      await act(async () => {
        await queryClient.invalidateQueries({ queryKey: ["face", 7], exact: true });
      });

      expect(await screen.findByRole("region", { name: "Review similar faces for Other Person" })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Will link Not them, skip it" })).toBeEnabled();
    });

    it("does not let a choice put back to the default outweigh a reject saved later", async () => {
      mockReviewableFace();
      let rejectedElsewhere = false;
      mockFaces.similar.mockImplementation(async () =>
        page([similarFace({ id: 18, label: "Not them", rejectedForSourcePerformer: rejectedElsewhere })]),
      );
      window.history.replaceState(null, "", "/face/7?tab=similar");

      const { queryClient } = renderPage();
      fireEvent.click(await screen.findByRole("button", { name: "Will link Not them, skip it" }));
      fireEvent.click(screen.getByRole("button", { name: "Skipped Not them, link it after all" }));

      // The face is then marked as someone else elsewhere; the reviewer's last word matched the old default only.
      rejectedElsewhere = true;
      await act(async () => {
        await queryClient.invalidateQueries({ queryKey: ["face", 7, "similar"] });
      });
      expect(await screen.findByRole("button", { name: "Not Jane Doe Not them, link it after all" })).toBeEnabled();
    });

    it("keeps a skip made on a face marked as someone else, even once that mark goes", async () => {
      mockReviewableFace();
      let stillMarked = true;
      mockFaces.similar.mockImplementation(async () =>
        page([similarFace({ id: 18, label: "Not them", rejectedForSourcePerformer: stillMarked })]),
      );
      window.history.replaceState(null, "", "/face/7?tab=similar");

      const { queryClient } = renderPage();
      fireEvent.click(await screen.findByRole("button", { name: "Not Jane Doe Not them, link it after all" }));
      fireEvent.click(screen.getByRole("button", { name: "Will link Not them, skip it" }));

      // The mark is then removed elsewhere; the reviewer's own word was to leave the face out.
      stillMarked = false;
      await act(async () => {
        await queryClient.invalidateQueries({ queryKey: ["face", 7, "similar"] });
      });
      expect(await screen.findByRole("button", { name: "Skipped Not them, link it after all" })).toBeEnabled();
    });

    it("shows no Similar Faces count, rather than 0, while no answer at the defaults exists", async () => {
      mockReviewableFace();
      mockFaces.similar.mockResolvedValue({ ...page([similarFace({ id: 17, label: "Keep" })]), totalCount: 40 });
      window.history.replaceState(null, "", "/face/7?tab=similar&page=2");

      renderPage();
      await screen.findByText("Keep");
      expect(screen.getByRole("tab", { name: /Similar Faces/i })).not.toHaveTextContent(/\d/);

      fireEvent.click(screen.getByRole("tab", { name: /Overview/i }));
      await waitFor(() => expect(screen.getByRole("tab", { name: /Similar Faces/i })).toHaveTextContent("40"));
    });

    it("asks for the appearance count only when the face does not report one", async () => {
      mockReviewableFace();
      mockFaces.similar.mockResolvedValue(page([]));
      mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 5, page: 1, perPage: 24 });

      const { unmount } = renderPage();
      await screen.findByRole("heading", { name: "Jane Doe" });
      expect(screen.getByRole("tab", { name: /Appears In/i })).toHaveTextContent("6");
      expect(mockFaces.appearances).not.toHaveBeenCalled();
      unmount();

      mockFaces.get.mockImplementation(async (faceId: number) => ({ ...linkedFace, id: faceId, appearanceCount: 0 }));
      renderPage();
      await screen.findByRole("heading", { name: "Jane Doe" });
      await waitFor(() => expect(screen.getByRole("tab", { name: /Appears In/i })).toHaveTextContent("5"));
      expect(mockFaces.appearances).toHaveBeenCalledTimes(1);
    });

    it("keeps a face's review choices when the reviewer leaves the face and comes back", async () => {
      mockReviewableFace();
      mockFaces.similar.mockImplementation(async (faceId: number) =>
        faceId === 7
          ? page([similarFace({ id: 17, label: "Keep" }), similarFace({ id: 18, label: "Not them" })])
          : page([]),
      );
      window.history.replaceState(null, "", "/face/7?tab=similar");

      const { showFace } = renderPage();
      fireEvent.click(await screen.findByRole("button", { name: "Will link Not them, skip it" }));

      act(() => {
        navigateToUrl("/face/8?tab=similar");
      });
      showFace(8);
      await waitFor(() => expect(mockFaces.similar).toHaveBeenCalledWith(8, expect.anything()));
      act(() => {
        navigateToUrl("/face/7?tab=similar");
      });
      showFace(7);

      expect(await screen.findByRole("button", { name: "Skipped Not them, link it after all" })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Will link Keep, skip it" })).toBeInTheDocument();
    });

    it("starts each face's lists from its own defaults, not from another face's last settings", async () => {
      mockReviewableFace();
      mockFaces.similar.mockResolvedValue(page([]));
      mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 60, page: 1, perPage: 24 });
      window.history.replaceState(null, "", "/face/7?tab=appearances&page=2");

      const { showFace } = renderPage();
      await waitFor(() => expect(mockFaces.appearances).toHaveBeenCalledWith(7, expect.objectContaining({ page: 2 })));
      // Leaving the tab keeps its settings for this face only.
      fireEvent.click(screen.getByRole("tab", { name: /Overview/i }));

      act(() => {
        navigateToUrl("/face/8");
      });
      showFace(8);
      await screen.findAllByRole("tab", { name: /Appears In/i });
      fireEvent.click(screen.getByRole("tab", { name: /Appears In/i }));

      await waitFor(() => expect(mockFaces.appearances).toHaveBeenCalledWith(8, expect.anything()));
      expect(
        mockFaces.appearances.mock.calls.filter(([faceId]) => faceId === 8).map(([, options]) => options.page),
      ).toEqual([1]);
    });

    it("asks for a face's similar faces once when the tab opens at its defaults, and once at another page size", async () => {
      mockReviewableFace();
      mockFaces.similar.mockResolvedValue(page([similarFace({ id: 17, label: "Keep" })]));
      window.history.replaceState(null, "", "/face/7?tab=similar");

      const { unmount } = renderPage();
      await screen.findByText("Keep");
      expect(mockFaces.similar).toHaveBeenCalledTimes(1);
      unmount();

      mockFaces.similar.mockClear();
      // List view: the endless grid is virtualized and needs a layout this test environment lacks.
      window.history.replaceState(null, "", "/face/7?tab=similar&perPage=infinite&view=list");
      renderPage();
      await screen.findByText("Keep");
      expect(mockFaces.similar).toHaveBeenCalledTimes(1);
      expect(mockFaces.similar).toHaveBeenCalledWith(7, expect.objectContaining({ perPage: 60 }));
    });

    it("shows Appears In list view by pages even when the link asks for endless scrolling", async () => {
      mockReviewableFace();
      mockFaces.similar.mockResolvedValue(page([]));
      mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 60, page: 1, perPage: 24 });
      window.history.replaceState(null, "", "/face/7?tab=appearances&perPage=infinite");

      renderPage();

      await waitFor(() =>
        expect(mockFaces.appearances).toHaveBeenCalledWith(7, expect.objectContaining({ perPage: 24 })),
      );
      expect(await screen.findAllByRole("button", { name: "Page 2" })).not.toHaveLength(0);
      expect(screen.queryByText(/NaN|Infinity/)).not.toBeInTheDocument();
    });
  });

  describe("active tab in the URL", () => {
    function mockLinkedFace() {
      mockFaces.get.mockResolvedValue(linkedFace);
      mockFaces.similar.mockResolvedValue({
        items: [similarFace({ label: "Similar Jane" })],
        totalCount: 1,
        page: 1,
        perPage: 18,
      });
      mockFaces.detections.mockResolvedValue([]);
      mockFaces.appearances.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 24 });
      mockEntityEngagement.get.mockResolvedValue(null);
    }

    it("opens the tab the URL names", async () => {
      window.history.replaceState(null, "", "/face/7?tab=similar");
      mockLinkedFace();

      renderPage();

      expect(await screen.findByText("Nearest neighbors from the face embedding index.")).toBeInTheDocument();
      expect(screen.getByRole("tab", { name: /Similar Faces/i })).toHaveAttribute("aria-selected", "true");
    });

    it("records the selected tab in the URL and drops it for Overview", async () => {
      window.history.replaceState(null, "", "/face/7");
      mockLinkedFace();

      renderPage();
      await screen.findByRole("heading", { name: "Jane Doe" });

      fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));
      expect(await screen.findByText("Nearest neighbors from the face embedding index.")).toBeInTheDocument();
      expect(new URLSearchParams(window.location.search).get("tab")).toBe("similar");

      fireEvent.click(screen.getByRole("tab", { name: /Overview/i }));
      await waitFor(() =>
        expect(screen.getByRole("tab", { name: /Overview/i })).toHaveAttribute("aria-selected", "true"),
      );
      expect(new URLSearchParams(window.location.search).has("tab")).toBe(false);
    });

    // App gives each face its own page, but the tab must follow the URL even when the page is reused.
    it("opens another face on the tab its own URL names", async () => {
      window.history.replaceState(null, "", "/face/7?tab=similar");
      mockLinkedFace();

      const { showFace } = renderPage();
      expect(await screen.findByText("Nearest neighbors from the face embedding index.")).toBeInTheDocument();

      act(() => {
        navigateToUrl("/face/8");
      });
      showFace(8);

      await waitFor(() =>
        expect(screen.getByRole("tab", { name: /Overview/i })).toHaveAttribute("aria-selected", "true"),
      );
      expect(screen.queryByText("Nearest neighbors from the face embedding index.")).not.toBeInTheDocument();
    });

    it("falls back to Overview for a tab the page does not have", async () => {
      window.history.replaceState(null, "", "/face/7?tab=bogus");
      mockLinkedFace();

      renderPage();
      await screen.findByRole("heading", { name: "Jane Doe" });

      expect(screen.getByRole("tab", { name: /Overview/i })).toHaveAttribute("aria-selected", "true");
      expect(screen.queryByText("Nearest neighbors from the face embedding index.")).not.toBeInTheDocument();
    });
  });
});

const linkedFace = {
  id: 7,
  label: "Dracuina",
  performerId: 12,
  performerName: "Jane Doe",
  ignored: false,
  detectionCount: 18,
  videoCount: 4,
  appearanceCount: 6,
  frameSampleCount: 11,
  imageCount: 2,
  createdAt: "2026-04-01T12:00:00Z",
  updatedAt: "2026-04-02T12:00:00Z",
};

function similarFace(overrides: Record<string, unknown>) {
  return {
    id: 17,
    ignored: false,
    detectionCount: 9,
    videoCount: 3,
    imageCount: 1,
    createdAt: "2026-03-30T12:00:00Z",
    updatedAt: "2026-04-03T12:00:00Z",
    appearanceCount: 4,
    frameSampleCount: 7,
    distance: 0.1234,
    ...overrides,
  };
}
