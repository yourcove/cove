import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FaceDetailPage } from "../pages/FaceDetailPage";
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
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  const onNavigate = vi.fn();

  mockFaces.list.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 500 });

  const page = (faceId: number) => (
    <QueryClientProvider client={queryClient}>
      <FaceDetailPage id={faceId} onNavigate={onNavigate} />
    </QueryClientProvider>
  );
  const { rerender } = render(page(7));

  return { onNavigate, showFace: (faceId: number) => rerender(page(faceId)) };
}

describe("FaceDetailPage", () => {
  afterEach(() => {
    window.history.replaceState(null, "", "/");
    vi.clearAllMocks();
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
    mockFaces.similar.mockResolvedValueOnce({ items: reviewedPage, totalCount: 6, page: 1, perPage: 18 });
    // The refresh after the batch is held back, to see the batch stay pending until the page shows the outcome.
    let finishRefresh: () => void = () => {};
    mockFaces.similar.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finishRefresh = () =>
            resolve({
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
            });
        }),
    );
    mockFaces.similar.mockResolvedValue({ items: [], totalCount: 0, page: 1, perPage: 18 });
    mockFaces.batchLink.mockResolvedValue({ linked: [17, 20], rejected: [19], skipped: [], failed: [] });
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
      expect(mockFaces.batchLink).toHaveBeenCalledWith({ performerId: 12, faceIds: [17, 20], rejectFaceIds: [19] }),
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

    // Reviewed faces stay on the page they were on; narrowing to unlinked faces brings the next batch up.
    fireEvent.click(within(review).getByRole("button", { name: "Show unlinked only" }));
    await waitFor(() =>
      expect(mockFaces.similar).toHaveBeenLastCalledWith(7, expect.objectContaining({ linked: false, page: 1 })),
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
    // Linking it is enough: the backend drops the earlier reject, so it is not sent as a new skip.
    await waitFor(() =>
      expect(mockFaces.batchLink).toHaveBeenCalledWith({ performerId: 12, faceIds: [17, 21], rejectFaceIds: [] }),
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
    const page = (items: ReturnType<typeof similarFace>[]) => ({
      items,
      totalCount: items.length,
      page: 1,
      perPage: 18,
    });
    const fresh = similarFace({ id: 17, label: "Fresh" });
    const skippedNow = similarFace({ id: 18, label: "Skipped now" });
    const savedReject = similarFace({ id: 19, label: "Saved reject", rejectedForSourcePerformer: true });
    // The first page, then the unlinked-only page in which the face skipped now was meanwhile rejected
    // elsewhere, and finally the page after the batch.
    let finishRefresh: () => void = () => {};
    mockFaces.similar
      .mockResolvedValueOnce(page([fresh, skippedNow, savedReject]))
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finishRefresh = () =>
              resolve(page([fresh, similarFace({ ...skippedNow, rejectedForSourcePerformer: true }), savedReject]));
          }),
      )
      .mockResolvedValue(page([savedReject]));
    mockFaces.batchLink.mockResolvedValue({
      linked: [17],
      rejected: [],
      skipped: [],
      failed: [{ faceId: 19, error: "Host update failed." }],
    });

    renderPage();
    await screen.findByRole("heading", { name: "Jane Doe" });
    fireEvent.click(screen.getByRole("tab", { name: /Similar Faces/i }));
    const review = await screen.findByRole("region", { name: "Review similar faces for Jane Doe" });
    fireEvent.click(screen.getByRole("button", { name: "Will link Skipped now, skip it" }));
    fireEvent.click(screen.getByRole("button", { name: "Not Jane Doe Saved reject, link it after all" }));
    expect(within(review).getByRole("button", { name: "Link 2 faces to Jane Doe" })).toBeEnabled();

    // While the earlier list is still on screen nothing can be chosen or sent.
    fireEvent.click(within(review).getByRole("button", { name: "Show unlinked only" }));
    expect(within(review).getByRole("button", { name: "Link 2 faces to Jane Doe" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Will link Fresh, skip it" })).toBeDisabled();
    await act(async () => finishRefresh());

    // The face skipped now stays out although its saved state changed, and the saved reject stays in.
    expect(await screen.findByRole("button", { name: "Not Jane Doe Skipped now, link it after all" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Will link Saved reject, skip it" })).toBeInTheDocument();
    fireEvent.click(within(review).getByRole("button", { name: "Link 2 faces to Jane Doe" }));
    await waitFor(() =>
      expect(mockFaces.batchLink).toHaveBeenCalledWith({ performerId: 12, faceIds: [17, 19], rejectFaceIds: [] }),
    );

    // The saved reject failed to link, so it is still offered as the reviewer chose it.
    expect(await within(review).findByText(/1 face could not be linked/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Will link Saved reject, skip it" })).toBeEnabled();
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

    // App reuses the page across faces, so the tab follows the new face's URL rather than the old face.
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
