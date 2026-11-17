import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { SegmentDetailPage } from "../pages/SegmentDetailPage";

vi.stubGlobal(
  "IntersectionObserver",
  vi.fn(function IntersectionObserver() {
    return {
      observe: vi.fn(),
      unobserve: vi.fn(),
      disconnect: vi.fn(),
    };
  }),
);

const {
  mockEntityImages,
  mockVideos,
  mockSegmentLibrary,
  mockTags,
  mockFaces,
  mockGoBack,
  videoPlayerMock,
  mockHasPermission,
} = vi.hoisted(() => ({
  mockHasPermission: vi.fn((_permission: string) => true),
  mockEntityImages: {
    segmentCoverUrl: vi.fn(() => "/segment-cover.jpg"),
    videoCoverUrl: vi.fn(() => "/video-cover.jpg"),
    studioImageUrl: vi.fn(() => "/studio-image.jpg"),
    uploadSegmentCoverImage: vi.fn(),
    deleteSegmentCoverImage: vi.fn(),
    setSegmentCoverFromFrame: vi.fn(),
  },
  mockVideos: {
    get: vi.fn(),
    createSubVideo: vi.fn(),
    streamUrl: vi.fn(() => "/stream/video.mp4"),
    screenshotUrl: vi.fn(() => "/video.jpg"),
    previewUrl: vi.fn(() => "/video-preview.mp4"),
    previewStatusUrl: vi.fn(() => "/video-preview-status"),
    segments: {
      list: vi.fn(),
      spans: vi.fn(),
      update: vi.fn(),
      delete: vi.fn(),
    },
  },
  mockSegmentLibrary: {
    get: vi.fn(),
  },
  mockTags: {
    find: vi.fn(),
  },
  mockFaces: {
    get: vi.fn(),
    findByIds: vi.fn(),
    suggestions: vi.fn(),
    recordSuggestionDecision: vi.fn(),
  },
  mockGoBack: vi.fn(),
  videoPlayerMock: vi.fn(),
}));

vi.mock("../api/client", () => ({
  entityImages: mockEntityImages,
  videos: mockVideos,
  segmentLibrary: mockSegmentLibrary,
  tags: mockTags,
  faces: mockFaces,
}));

vi.mock("../components/VideoPlayer", () => ({
  VideoPlayer: (props: Record<string, unknown>) => {
    videoPlayerMock(props);
    return <div data-testid="segment-video-player">Video Player</div>;
  },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    hasPermission: mockHasPermission,
  }),
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({
    backLabel: "Back to segments",
    goBack: mockGoBack,
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: { keybindingOverrides: {} } } }),
  useOptionalAppConfig: () => ({ config: { ui: { keybindingOverrides: {}, videoObjectFit: "cover" } } }),
}));

function buildSegment(overrides: Record<string, unknown> = {}) {
  return {
    id: 7,
    hostId: 99,
    hostType: "video",
    hostTitle: "Video 99",
    title: "Episode Intro",
    kind: "intro",
    sourceKey: "detector.segment",
    sourceRunId: "run-1",
    colorHint: "#ffaa00",
    startSec: 12,
    endSec: 21,
    confidence: 0.91,
    tagId: 5,
    tagName: "Opening",
    refId: 55,
    payload: { score: 0.91 },
    createdAt: "2026-05-01T12:00:00Z",
    updatedAt: "2026-05-01T13:00:00Z",
    ...overrides,
  };
}

function buildVideo(overrides: Record<string, unknown> = {}) {
  return {
    id: 99,
    title: "Video 99",
    code: "SC-99",
    details: "Parent video details",
    director: "Director Example",
    date: "2026-05-01",
    organized: true,
    studioId: 12,
    studioName: "Studio 12",
    urls: ["https://example.test/video-99"],
    tags: [
      { id: 5, name: "Opening" },
      { id: 6, name: "Action" },
    ],
    performers: [{ id: 11, name: "Performer One" }],
    galleries: [{ id: 15, title: "Gallery Fifteen", date: "2026-04-15" }],
    groups: [{ id: 17, name: "Highlights", videoIndex: 3 }],
    customFields: { mood: "intense" },
    files: [{ format: "mp4", duration: 120, captions: [] }],
    ...overrides,
  };
}

function renderPageWithUnmount() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const onNavigate = vi.fn();
  const { unmount } = render(
    <QueryClientProvider client={queryClient}>
      <SegmentDetailPage id={7} onNavigate={onNavigate} />
    </QueryClientProvider>,
  );
  return { onNavigate, unmount };
}

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  const onNavigate = vi.fn();

  render(
    <QueryClientProvider client={queryClient}>
      <SegmentDetailPage id={7} onNavigate={onNavigate} />
    </QueryClientProvider>,
  );

  return { onNavigate };
}

describe("SegmentDetailPage", () => {
  afterEach(() => {
    vi.clearAllMocks();
    mockHasPermission.mockImplementation(() => true);
  });

  it("shows a retryable load error and recovers", async () => {
    mockSegmentLibrary.get
      .mockRejectedValueOnce(new Error("API Error 502: upstream API Error 404"))
      .mockResolvedValueOnce(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([buildSegment()]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockTags.find.mockResolvedValue({ items: [] });

    renderPage();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load segment");
    expect(screen.queryByText("Segment not found")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("heading", { name: "Episode Intro" })).toBeInTheDocument();
  });

  it("keeps the not-found state for a genuinely missing segment", async () => {
    mockSegmentLibrary.get.mockRejectedValue(new Error("API Error 404: Not Found"));

    renderPage();

    expect(await screen.findByText("Segment not found")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("shows the video, its segment and nearby raw segments in one overview", async () => {
    mockSegmentLibrary.get.mockResolvedValue(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([
      buildSegment({ id: 6, title: "Cold Open", startSec: 5, endSec: 10, tagName: "Teaser" }),
      buildSegment(),
      buildSegment({ id: 8, title: "Action Beat", startSec: 22, endSec: 34, tagName: "Action" }),
    ]);
    mockVideos.segments.spans.mockResolvedValue({
      profileId: 3,
      spans: [
        {
          spanKey: "focus",
          startSec: 10,
          endSec: 28,
          tagName: "Opening Stretch",
          kind: "highlight",
          segmentIds: [7, 8],
        },
      ],
    });
    mockTags.find.mockResolvedValue({ items: [] });

    renderPage();

    expect(await screen.findByRole("heading", { name: "Episode Intro" })).toBeInTheDocument();
    expect(screen.getByTestId("media-detail-layout-media")).toBeInTheDocument();
    expect(await screen.findByTestId("segment-video-player")).toBeInTheDocument();
    expect(screen.queryByTestId("media-detail-layout-media-frame")).not.toBeInTheDocument();
    expect(videoPlayerMock).toHaveBeenCalledWith(
      expect.not.objectContaining({
        extensionSurface: expect.anything(),
      }),
    );

    const tabs = screen.getByRole("tablist", { name: /detail tabs/i });
    // What used to be a separate Context tab is part of the overview.
    expect(within(tabs).queryByRole("tab", { name: /context/i })).not.toBeInTheDocument();
    expect(await screen.findByText("Before")).toBeInTheDocument();
    expect(screen.getAllByText("Cold Open").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Action Beat").length).toBeGreaterThan(0);
    expect(screen.getByText("2 of 3 raw segments in this video")).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Open segment 0:10 - 0:28" })).toBeInTheDocument();
    expect(screen.getByText("joins 2 raw segments")).toBeInTheDocument();
  });

  it("does not list intersecting segments as adjacent segments", async () => {
    mockSegmentLibrary.get.mockResolvedValue(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([
      buildSegment({ id: 5, title: "Earlier Segment", startSec: 5, endSec: 10, tagName: "Teaser" }),
      buildSegment({ id: 6, title: "Earlier Overlap", startSec: 10, endSec: 15, tagName: "Transition" }),
      buildSegment(),
      buildSegment({ id: 8, title: "Same Range", tagName: "Action" }),
      buildSegment({ id: 9, title: "Later Segment", startSec: 22, endSec: 34, tagName: "Closing" }),
    ]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockTags.find.mockResolvedValue({ items: [] });

    renderPage();

    const previousSection = (await screen.findByText("Before")).parentElement!;
    const nextSection = screen.getByText("After").parentElement!;
    const intersectingSection = screen.getByText("Overlapping").parentElement!;
    expect(within(previousSection).getByText("Earlier Segment")).toBeInTheDocument();
    expect(within(previousSection).queryByText("Earlier Overlap")).not.toBeInTheDocument();
    expect(within(nextSection).queryByText("Same Range")).not.toBeInTheDocument();
    expect(within(nextSection).getByText("Later Segment")).toBeInTheDocument();
    expect(within(intersectingSection).getByText("Earlier Overlap")).toBeInTheDocument();
    expect(within(intersectingSection).getByText("Same Range")).toBeInTheDocument();
  });

  it("supports keyboard shortcuts for edit and adjacent navigation", async () => {
    mockSegmentLibrary.get.mockResolvedValue(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([
      buildSegment({ id: 6, title: "Cold Open", startSec: 5, endSec: 10 }),
      buildSegment(),
      buildSegment({ id: 8, title: "Action Beat", startSec: 22, endSec: 34 }),
    ]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockTags.find.mockResolvedValue({ items: [] });

    const { onNavigate } = renderPage();

    await screen.findByRole("heading", { name: "Episode Intro" });

    fireEvent.keyDown(window, { key: "e" });
    expect(await screen.findByRole("heading", { name: "Edit Raw Segment" })).toBeInTheDocument();
    expect(screen.getByLabelText("Title")).toHaveFocus();

    fireEvent.keyDown(window, { key: "]" });
    expect(onNavigate).toHaveBeenCalledWith({ page: "segment", id: 8 });

    fireEvent.keyDown(window, { key: "s" });
    expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 99, seekTo: 12 });
  });

  it("shows Save in the accent color only while the segment has unsaved changes", async () => {
    mockSegmentLibrary.get.mockResolvedValue(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([buildSegment()]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockTags.find.mockResolvedValue({ items: [] });

    renderPage();

    await screen.findByRole("heading", { name: "Episode Intro" });
    fireEvent.keyDown(window, { key: "e" });
    await screen.findByRole("heading", { name: "Edit Raw Segment" });

    const save = screen.getByRole("button", { name: "Save" });
    expect(save).not.toHaveClass("bg-accent");
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Renamed Intro" } });
    expect(save).toHaveClass("bg-accent");
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Episode Intro" } });
    expect(save).not.toHaveClass("bg-accent");
  });

  it("shows stored times to the hundredth and saves them unchanged when only other fields are edited", async () => {
    const segment = buildSegment({ startSec: 1734.025, endSec: 1754.458 });
    mockSegmentLibrary.get.mockResolvedValue(segment);
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([segment]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockVideos.segments.update.mockResolvedValue(segment);
    mockTags.find.mockResolvedValue({ items: [] });

    renderPage();

    await screen.findByRole("heading", { name: "Episode Intro" });
    fireEvent.keyDown(window, { key: "e" });
    await screen.findByRole("heading", { name: "Edit Raw Segment" });

    expect(screen.getByLabelText(/^Start/)).toHaveValue("28:54.03");
    expect(screen.getByLabelText(/^End/)).toHaveValue("29:14.46");
    const save = screen.getByRole("button", { name: "Save" });
    expect(save).not.toHaveClass("bg-accent");

    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Renamed Intro" } });
    fireEvent.click(save);

    await waitFor(() => {
      expect(mockVideos.segments.update).toHaveBeenCalledWith(
        99,
        7,
        expect.objectContaining({ title: "Renamed Intro", startSec: 1734.025, endSec: 1754.458 }),
      );
    });
  });

  it("saves an edited start time as typed and clears the end when its input is emptied", async () => {
    const segment = buildSegment({ startSec: 1734.025, endSec: 1754.458 });
    mockSegmentLibrary.get.mockResolvedValue(segment);
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.segments.list.mockResolvedValue([segment]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockVideos.segments.update.mockResolvedValue(segment);
    mockTags.find.mockResolvedValue({ items: [] });

    renderPage();

    await screen.findByRole("heading", { name: "Episode Intro" });
    fireEvent.keyDown(window, { key: "e" });
    await screen.findByRole("heading", { name: "Edit Raw Segment" });

    fireEvent.change(screen.getByLabelText(/^Start/), { target: { value: "1:02:03.5" } });
    fireEvent.blur(screen.getByLabelText(/^Start/));
    fireEvent.change(screen.getByLabelText(/^End/), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(mockVideos.segments.update).toHaveBeenCalledWith(
        99,
        7,
        expect.objectContaining({ startSec: 3723.5, endSec: undefined }),
      );
    });
  });

  it("creates a metadata-preserving sub-video from the current segment", async () => {
    mockSegmentLibrary.get.mockResolvedValue(buildSegment());
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.createSubVideo.mockResolvedValue({ id: 1234 });
    mockVideos.segments.list.mockResolvedValue([buildSegment()]);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockTags.find.mockResolvedValue({ items: [] });

    const { onNavigate } = renderPage();

    fireEvent.click(await screen.findByTitle("Operations"));
    const makeVideoButton = await screen.findByRole("button", { name: /make video/i });
    await waitFor(() => {
      expect(makeVideoButton).toBeEnabled();
    });
    fireEvent.click(makeVideoButton);

    await waitFor(() => {
      expect(mockVideos.createSubVideo).toHaveBeenCalledWith(
        99,
        expect.objectContaining({
          title: "Episode Intro",
          code: "SC-99",
          details: "Parent video details",
          director: "Director Example",
          date: "2026-05-01",
          organized: true,
          studioId: 12,
          urls: ["https://example.test/video-99"],
          tagIds: [5, 6],
          performerIds: [11],
          galleryIds: [15],
          groups: [{ groupId: 17, videoIndex: 3 }],
          customFields: { mood: "intense" },
          parentVideoId: 99,
          clipStartSec: 12,
          clipEndSec: 21,
        }),
      );
    });
    await waitFor(() => {
      expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 1234 });
    });
  });

  describe("overview", () => {
    function buildFaceSegment(overrides: Record<string, unknown> = {}) {
      return buildSegment({
        title: "face-0031",
        kind: "face",
        sourceKey: "ext:ai.faces",
        tagId: undefined,
        tagName: undefined,
        refId: 31,
        refLabel: "face-0031",
        confidence: 0.73,
        payload: { refKey: "face-0031", bestTimeSec: "15", modelKey: "face_detections" },
        ...overrides,
      });
    }

    function buildFace(overrides: Record<string, unknown> = {}) {
      return {
        id: 31,
        label: "face-0031",
        primarySourceKey: "face-0031",
        ignored: false,
        coverImageUrl: "/face-31.jpg",
        topSuggestion: { performerId: 40, performerName: "Suggested Person", confidence: 0.71 },
        ...overrides,
      };
    }

    function mockVideoContext(segment: Record<string, unknown>, spans: unknown[] = []) {
      mockSegmentLibrary.get.mockResolvedValue(segment);
      mockVideos.get.mockResolvedValue(buildVideo());
      mockVideos.segments.list.mockResolvedValue([segment]);
      mockVideos.segments.spans.mockResolvedValue({ profileId: 3, profileVersion: 1, spans });
      mockFaces.findByIds.mockResolvedValue({ items: [], totalCount: 0 });
    }

    it("names an unnamed face by its suggested performer and accepts the suggestion from the page", async () => {
      mockVideoContext(buildFaceSegment(), [
        { spanKey: "joined", hostId: 99, startSec: 10, endSec: 30, segmentIds: [7, 8, 9], collapsedToInstant: false },
      ]);
      mockFaces.get.mockResolvedValue(buildFace());
      mockFaces.suggestions.mockResolvedValue([
        { performerId: 40, performerName: "Suggested Person", confidence: 71, why: "Looks alike", evidence: [] },
      ]);
      mockFaces.recordSuggestionDecision.mockResolvedValue(
        buildFace({ performerId: 40, performerName: "Suggested Person" }),
      );

      const { onNavigate } = renderPage();

      // The marker is hidden from screen readers, which hear the suggestion in the heading's name instead.
      expect(await screen.findByRole("heading", { name: "Suggested Person (suggested)" })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "face-0031" })).toBeInTheDocument();
      expect(screen.getByText("Not linked")).toBeInTheDocument();
      expect(screen.getByText("Looks alike")).toBeInTheDocument();
      expect(screen.getByText("AI Faces")).toBeInTheDocument();

      // The X-ray box is on for a face segment and names the face as the heading does.
      await waitFor(() =>
        expect(videoPlayerMock).toHaveBeenLastCalledWith(
          expect.objectContaining({
            faceOverlayDefault: true,
            faces: [expect.objectContaining({ id: 31, displayName: "Suggested Person (suggested)" })],
          }),
        ),
      );

      fireEvent.click(await screen.findByRole("button", { name: "Open segment 0:10 - 0:30" }));
      expect(onNavigate).toHaveBeenCalledWith({ page: "video-span", id: 99, spanKey: "joined", profileId: 3 });

      // Showing the best frame stops on it rather than playing past it.
      const playerProps = videoPlayerMock.mock.lastCall![0] as {
        onSeekRegister: (seek: (time: number, forcePlay?: boolean) => void) => void;
        onPlaybackControlRegister: (controls: Record<string, () => void>) => void;
      };
      const seek = vi.fn();
      const pause = vi.fn();
      playerProps.onSeekRegister(seek);
      playerProps.onPlaybackControlRegister({ pause, play: vi.fn(), toggle: vi.fn(), seekBy: vi.fn() });
      fireEvent.click(screen.getByRole("button", { name: "Show the best frame at 0:15" }));
      expect(pause).toHaveBeenCalled();
      expect(seek).toHaveBeenCalledWith(15, false);

      fireEvent.click(screen.getByRole("button", { name: "Accept" }));
      await waitFor(() =>
        expect(mockFaces.recordSuggestionDecision).toHaveBeenCalledWith(31, { performerId: 40, decision: "accept" }),
      );
    });

    it("names a face linked since detection by its performer, once", async () => {
      mockVideoContext(buildFaceSegment({ title: "face-0048", performerId: 40, performerName: "Linked Person" }));
      mockFaces.get.mockResolvedValue(buildFace({ performerId: 40, performerName: "Linked Person" }));

      renderPage();

      expect(await screen.findByRole("heading", { name: /Linked Person/ })).toBeInTheDocument();
      expect(screen.getByRole("heading", { name: /Linked Person/ })).not.toHaveTextContent("suggested");
      // The rows link to the performer and face without repeating the name the heading shows.
      expect(await screen.findByRole("button", { name: "Open performer" })).toBeInTheDocument();
      expect(screen.queryByText("Accept")).not.toBeInTheDocument();
      expect(mockFaces.suggestions).not.toHaveBeenCalled();
    });

    it("rejects a suggestion from the page", async () => {
      mockVideoContext(buildFaceSegment());
      mockFaces.get.mockResolvedValue(buildFace());
      mockFaces.suggestions.mockResolvedValue([
        { performerId: 40, performerName: "Suggested Person", confidence: 71, why: "Looks alike", evidence: [] },
      ]);
      mockFaces.recordSuggestionDecision.mockResolvedValue(buildFace());

      renderPage();

      fireEvent.click(await screen.findByRole("button", { name: "Not them" }));
      await waitFor(() =>
        expect(mockFaces.recordSuggestionDecision).toHaveBeenCalledWith(31, { performerId: 40, decision: "reject" }),
      );
    });

    it("accepts a reference match to a library performer with its reference link, but not a reference-only one", async () => {
      const resolved = {
        performerId: 40,
        performerName: "Suggested Person",
        confidence: 71,
        why: "Matches a reference performer",
        evidence: [],
        externalUrl: "https://reference.example/performers/abc",
        referenceEndpoint: "https://reference.example/graphql",
        referenceExternalId: "abc",
        referenceWillRefreshFromMetadata: true,
      };
      mockVideoContext(buildFaceSegment());
      mockFaces.get.mockResolvedValue(buildFace());
      mockFaces.suggestions.mockResolvedValue([resolved]);
      mockFaces.recordSuggestionDecision.mockResolvedValue(buildFace());

      const { unmount } = renderPageWithUnmount();

      fireEvent.click(await screen.findByRole("button", { name: "Accept" }));
      await waitFor(() =>
        expect(mockFaces.recordSuggestionDecision).toHaveBeenCalledWith(31, {
          performerId: 40,
          decision: "accept",
          referenceEndpoint: "https://reference.example/graphql",
          referenceExternalId: "abc",
          referenceUpdateMetadata: true,
        }),
      );
      unmount();

      mockFaces.suggestions.mockResolvedValue([{ ...resolved, performerId: -1 }]);
      renderPage();
      expect(await screen.findByRole("button", { name: "Review on face page" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Accept" })).not.toBeInTheDocument();
    });

    it("does not name an ignored face after its old suggestion", async () => {
      mockVideoContext(buildFaceSegment());
      mockFaces.get.mockResolvedValue(buildFace({ ignored: true }));

      renderPage();

      expect(await screen.findByText(/This face is ignored/)).toBeInTheDocument();
      expect(screen.getByRole("heading", { name: "face-0031" })).toBeInTheDocument();
      expect(mockFaces.suggestions).not.toHaveBeenCalled();
      await waitFor(() =>
        expect(videoPlayerMock).toHaveBeenLastCalledWith(
          expect.objectContaining({ faces: [expect.objectContaining({ id: 31, displayName: undefined })] }),
        ),
      );
    });

    it("names rows it cannot link instead of offering to open them", async () => {
      mockHasPermission.mockImplementation((permission: string) => !permission.startsWith("performers"));
      mockVideoContext(buildFaceSegment({ title: "face-0048", performerId: 40, performerName: "Linked Person" }));
      mockFaces.get.mockResolvedValue(buildFace({ performerId: 40, performerName: "Linked Person" }));

      renderPage();

      expect(await screen.findByRole("heading", { name: "Linked Person" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Open performer" })).not.toBeInTheDocument();
      expect(screen.getAllByText("Linked Person").length).toBeGreaterThan(1);
    });

    it("keeps X-ray at the viewer's default for a segment that is not a face, and says it is a raw segment", async () => {
      mockVideoContext(buildSegment({ title: "Opening", kind: "tag", tagName: "Opening" }));

      renderPage();

      await waitFor(() =>
        expect(videoPlayerMock).toHaveBeenLastCalledWith(expect.objectContaining({ faceOverlayDefault: false })),
      );
      expect(screen.getByText("Raw segment")).toBeInTheDocument();
    });

    it("says when the Segments view leaves a raw segment out, and when it shows it on its own", async () => {
      mockVideoContext(buildSegment({ title: "Opening", kind: "tag", tagName: "Opening" }));

      const { unmount } = renderPageWithUnmount();
      expect(await screen.findByText(/the display profile leaves this raw segment out/)).toBeInTheDocument();
      unmount();

      mockVideos.segments.spans.mockResolvedValue({
        profileId: 3,
        profileVersion: 1,
        spans: [{ spanKey: "alone", hostId: 99, startSec: 12, endSec: 21, segmentIds: [7], collapsedToInstant: false }],
      });
      renderPage();
      expect(await screen.findByText("shown on its own")).toBeInTheDocument();
    });

    it("offers to retry nearby raw segments that failed to load", async () => {
      mockVideoContext(buildSegment({ title: "Opening", kind: "tag", tagName: "Opening" }));
      mockVideos.segments.list.mockReset();
      mockVideos.segments.list
        .mockRejectedValueOnce(new Error("API Error 500: boom"))
        .mockResolvedValueOnce([buildSegment(), buildSegment({ id: 9, title: "Later", startSec: 30, endSec: 40 })]);

      renderPage();

      expect(await screen.findByText("Could not load nearby raw segments")).toBeInTheDocument();
      fireEvent.click(screen.getByRole("button", { name: "Try again" }));
      expect(await screen.findByText("After")).toBeInTheDocument();
    });

    it("does not read a video's segments for a raw segment on an image", async () => {
      mockSegmentLibrary.get.mockResolvedValue(buildSegment({ hostType: "image", kind: "tag", title: "Opening" }));

      renderPage();

      expect(await screen.findByRole("heading", { name: "Opening" })).toBeInTheDocument();
      expect(mockVideos.segments.list).not.toHaveBeenCalled();
      expect(mockVideos.segments.spans).not.toHaveBeenCalled();
    });

    it("keeps the page and player in place while the next raw segment loads", async () => {
      mockVideoContext(buildSegment({ title: "Opening", kind: "tag", tagName: "Opening" }));
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      const page = (segmentId: number) => (
        <QueryClientProvider client={queryClient}>
          <SegmentDetailPage id={segmentId} onNavigate={vi.fn()} />
        </QueryClientProvider>
      );
      const { rerender } = render(page(7));
      const player = await screen.findByTestId("segment-video-player");

      let releaseNext: (value: unknown) => void = () => {};
      mockSegmentLibrary.get.mockReturnValue(new Promise((resolve) => (releaseNext = resolve)));
      rerender(page(8));

      expect(screen.getByTestId("segment-video-player")).toBe(player);
      expect(screen.getByRole("heading", { name: "Opening" })).toBeInTheDocument();

      releaseNext(buildSegment({ id: 8, title: "Closing", kind: "tag", tagName: "Closing", startSec: 30, endSec: 40 }));
      expect(await screen.findByRole("heading", { name: "Closing" })).toBeInTheDocument();
      expect(screen.getByTestId("segment-video-player")).toBe(player);
    });

    it("marks a move as loading and closes the previous segment's dialogs", async () => {
      mockVideoContext(buildSegment({ title: "Opening", kind: "tag", tagName: "Opening" }));
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      const page = (segmentId: number) => (
        <QueryClientProvider client={queryClient}>
          <SegmentDetailPage id={segmentId} onNavigate={vi.fn()} />
        </QueryClientProvider>
      );
      const { rerender } = render(page(7));
      await screen.findByRole("heading", { name: "Opening" });
      expect(screen.getByRole("status")).toHaveTextContent("");

      fireEvent.click(screen.getByTitle("Operations"));
      fireEvent.click(await screen.findByRole("button", { name: /Delete/ }));
      expect(await screen.findByText(/Delete raw segment #7\?/)).toBeInTheDocument();

      let releaseNext: (value: unknown) => void = () => {};
      mockSegmentLibrary.get.mockReturnValue(new Promise((resolve) => (releaseNext = resolve)));
      rerender(page(8));
      expect(screen.getByRole("status")).toHaveTextContent("Loading");

      releaseNext(buildSegment({ id: 8, title: "Closing", kind: "tag", tagName: "Closing", startSec: 30, endSec: 40 }));
      expect(await screen.findByRole("heading", { name: "Closing" })).toBeInTheDocument();
      expect(screen.getByRole("status")).toHaveTextContent("");
      expect(screen.queryByText(/Delete raw segment/)).not.toBeInTheDocument();
    });

    it("refreshes the edited segment when its save finishes after moving to another one", async () => {
      const edited = buildSegment({ title: "Opening", kind: "tag", tagName: "Opening" });
      mockVideoContext(edited);
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      const invalidate = vi.spyOn(queryClient, "invalidateQueries");
      const page = (segmentId: number) => (
        <QueryClientProvider client={queryClient}>
          <SegmentDetailPage id={segmentId} onNavigate={vi.fn()} />
        </QueryClientProvider>
      );
      const { rerender } = render(page(7));
      await screen.findByRole("heading", { name: "Opening" });
      fireEvent.keyDown(window, { key: "e" });
      fireEvent.change(await screen.findByLabelText("Title"), { target: { value: "Renamed" } });
      let finishSave: (value: unknown) => void = () => {};
      mockVideos.segments.update.mockReturnValue(new Promise((resolve) => (finishSave = resolve)));
      fireEvent.click(screen.getByRole("button", { name: "Save" }));

      mockSegmentLibrary.get.mockResolvedValue(
        buildSegment({ id: 8, title: "Closing", kind: "tag", tagName: "Closing" }),
      );
      rerender(page(8));
      await screen.findByRole("heading", { name: "Closing" });
      invalidate.mockClear();

      finishSave(edited);
      await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ["segment", 7] }));
      expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ["segment", 8] });
    });

    it("shows a tag segment's tag once, with its icon", async () => {
      mockVideoContext(buildSegment({ title: "Opening", kind: "tag", tagName: "Opening" }));

      const { onNavigate } = renderPage();

      const heading = await screen.findByRole("heading", { name: /Opening/ });
      expect(heading.querySelector("svg.lucide-tag")).not.toBeNull();
      expect(screen.getAllByText("Opening")).toHaveLength(1);
      // The tag is opened from the header; the summary line does not repeat the heading.
      expect(screen.queryByRole("button", { name: "Open tag" })).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Open video Video 99 at this segment" })).toBeInTheDocument();
      fireEvent.click(screen.getByRole("button", { name: "Open tag Opening" }));
      expect(onNavigate).toHaveBeenCalledWith({ page: "tag", id: 5 });
      expect(mockFaces.get).not.toHaveBeenCalled();
    });
  });
});
