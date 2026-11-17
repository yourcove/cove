import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ResolvedSpanPlayPage } from "../pages/ResolvedSpanPlayPage";

// The overview shows the parent video as a card, which loads its preview when it scrolls into view.
vi.stubGlobal(
  "IntersectionObserver",
  vi.fn(function IntersectionObserver() {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  }),
);

const {
  mockVideos,
  mockSegmentDisplayProfiles,
  mockSegmentLibrary,
  mockGoBack,
  mockUiConfig,
  mockPlayerSeek,
  mockFaces,
  playerProps,
  mockHasPermission,
} = vi.hoisted(() => ({
  mockVideos: {
    get: vi.fn(),
    createSubVideo: vi.fn(),
    streamUrl: vi.fn((id: number) => `/video-${id}.mp4`),
    screenshotUrl: vi.fn((id: number) => `/video-${id}.jpg`),
    previewUrl: vi.fn(() => "/video-preview.mp4"),
    previewStatusUrl: vi.fn(() => "/video-preview-status"),
    segments: {
      spanDetail: vi.fn(),
      spans: vi.fn(),
    },
  },
  mockSegmentDisplayProfiles: {
    get: vi.fn(),
  },
  mockSegmentLibrary: {
    list: vi.fn(),
  },
  mockGoBack: vi.fn(),
  mockUiConfig: { autostartVideo: true },
  mockPlayerSeek: vi.fn(),
  mockFaces: { get: vi.fn(), findByIds: vi.fn(), suggestions: vi.fn(), recordSuggestionDecision: vi.fn() },
  mockHasPermission: vi.fn((_permission: string) => true),
  playerProps: vi.fn(),
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => {},
}));

vi.mock("../api/client", () => ({
  videos: mockVideos,
  segmentDisplayProfiles: mockSegmentDisplayProfiles,
  segmentLibrary: mockSegmentLibrary,
  faces: mockFaces,
  entityImages: {
    videoCoverUrl: vi.fn(() => "/video-cover.jpg"),
    studioImageUrl: vi.fn(() => "/studio-image.jpg"),
  },
  performers: { get: vi.fn() },
  tags: { get: vi.fn() },
}));

vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({
    backLabel: "Back to video",
    goBack: mockGoBack,
  }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({
    hasPermission: mockHasPermission,
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({
    config: { ui: mockUiConfig },
  }),
  useOptionalAppConfig: () => ({
    config: { ui: { ...mockUiConfig, keybindingOverrides: {}, videoObjectFit: "cover" } },
  }),
}));

vi.mock("../components/VideoPlayer", () => ({
  VideoPlayer: (props: Record<string, unknown>) => {
    playerProps(props);
    return <PlayerStub {...(props as any)} />;
  },
}));

function PlayerStub({
  clip,
  resumeTime,
  autostart,
  onSeekRegister,
  playbackTracking,
}: {
  clip: { start: number; end: number };
  resumeTime?: number;
  autostart?: boolean;
  onSeekRegister?: (seek: (time: number, forcePlay?: boolean) => void) => void;
  playbackTracking?: {
    clipStartSec?: number;
    clipEndSec?: number;
    context?: Record<string, unknown>;
  };
}) {
  onSeekRegister?.(mockPlayerSeek);
  return (
    <div>
      <div data-testid="resolved-span-player">
        Clip {clip.start}-{clip.end} @ {resumeTime}
      </div>
      <div data-testid="resolved-span-autostart">{String(autostart)}</div>
      <div data-testid="resolved-span-tracking">
        Tracking {playbackTracking?.clipStartSec}-{playbackTracking?.clipEndSec}{" "}
        {JSON.stringify(playbackTracking?.context)}
      </div>
    </div>
  );
}

function buildDetail() {
  return {
    videoId: 14,
    videoTitle: "Video Fourteen",
    profileId: 3,
    span: {
      spanKey: "tag-14",
      startSec: 5,
      endSec: 25,
      tagId: 8,
      tagName: "Action Sequence",
      kind: "tag",
      segmentIds: [71, 72],
      sourceKey: "tag:action",
      colorHint: "amber",
    },
    intervals: [
      { startSec: 5, endSec: 10 },
      { startSec: 20, endSec: 25 },
    ],
  };
}

function buildVideo(overrides: Record<string, unknown> = {}) {
  return {
    id: 14,
    title: "Video Fourteen",
    code: "SC-14",
    details: "Resolved parent video",
    director: "Director Span",
    date: "2026-05-01",
    organized: true,
    studioId: 22,
    urls: ["https://example.test/video-14"],
    tags: [{ id: 8, name: "Action" }],
    performers: [{ id: 31, name: "Performer Span" }],
    galleries: [{ id: 45, title: "Gallery Forty Five", date: "2026-04-10" }],
    groups: [{ id: 55, name: "Span Group", videoIndex: 2 }],
    customFields: { energy: "high" },
    files: [{ id: 1, format: "mp4", duration: 120, captions: [] }],
    ...overrides,
  };
}

function renderPage(props?: Partial<React.ComponentProps<typeof ResolvedSpanPlayPage>>) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  render(
    <QueryClientProvider client={queryClient}>
      <ResolvedSpanPlayPage videoId={14} spanKey="tag-14" onNavigate={vi.fn()} {...props} />
    </QueryClientProvider>,
  );
}

describe("ResolvedSpanPlayPage", () => {
  afterEach(() => {
    vi.clearAllMocks();
    mockUiConfig.autostartVideo = true;
    mockHasPermission.mockImplementation(() => true);
    mockSegmentLibrary.list.mockReset();
    mockFaces.get.mockReset();
  });

  it("shows a retryable load error and recovers", async () => {
    mockVideos.segments.spanDetail
      .mockRejectedValueOnce(new Error("API Error 502: upstream API Error 404"))
      .mockResolvedValueOnce(buildDetail());
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockVideos.get.mockResolvedValue(buildVideo());
    mockSegmentDisplayProfiles.get.mockResolvedValue({ id: 3, name: "Default Profile" });
    mockSegmentLibrary.list.mockResolvedValue({ items: [] });

    renderPage();

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Could not load segment");
    expect(screen.queryByText("Segment not found")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByText("Clip 5-25 @ 5")).toBeInTheDocument();
  });

  it("keeps the not-found state for a genuinely missing segment", async () => {
    mockVideos.segments.spanDetail.mockRejectedValue(new Error("API Error 404: Not Found"));

    renderPage();

    expect(await screen.findByText("Segment not found")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("renders VideoPlayer clip props and interval details", async () => {
    mockVideos.segments.spanDetail.mockResolvedValue(buildDetail());
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockVideos.get.mockResolvedValue(buildVideo());
    mockSegmentDisplayProfiles.get.mockResolvedValue({ id: 3, name: "Default Profile" });
    mockSegmentLibrary.list.mockResolvedValue({
      items: [
        { id: 71, startSec: 5, endSec: 10, title: "Segment One" },
        { id: 72, startSec: 20, endSec: 25, title: "Segment Two" },
      ],
    });

    renderPage();

    expect(await screen.findByText("Clip 5-25 @ 5")).toBeInTheDocument();
    expect(screen.getByTestId("resolved-span-tracking")).toHaveTextContent('Tracking 5-25 {"spanKey":"tag-14"}');
    expect(screen.getByTestId("resolved-span-autostart")).toHaveTextContent("true");
    expect(screen.queryByTestId("media-detail-layout-media-frame")).not.toBeInTheDocument();

    // The raw segments it joins are listed on the overview, each playable in place.
    expect(screen.queryByRole("tab", { name: /intervals/i })).not.toBeInTheDocument();
    expect(await screen.findByRole("button", { name: /^0:05 - 0:10/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^0:20 - 0:25/ })).toBeInTheDocument();
    expect(screen.getByText("Segment · 2 raw segments")).toBeInTheDocument();
  });

  it("seeks within one continuous clip when an interval is selected", async () => {
    mockVideos.segments.spanDetail.mockResolvedValue(buildDetail());
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockVideos.get.mockResolvedValue(buildVideo());
    mockSegmentDisplayProfiles.get.mockResolvedValue({ id: 3, name: "Default Profile" });
    mockSegmentLibrary.list.mockResolvedValue({
      items: [
        { id: 71, startSec: 5, endSec: 10, title: "Segment One" },
        { id: 72, startSec: 20, endSec: 25, title: "Segment Two" },
      ],
    });

    renderPage();

    expect(await screen.findByText("Clip 5-25 @ 5")).toBeInTheDocument();

    fireEvent.click(await screen.findByRole("button", { name: "Play from 0:20" }));
    expect(screen.getByText("Clip 5-25 @ 20")).toBeInTheDocument();
    expect(mockPlayerSeek).toHaveBeenCalledWith(20, false);
  });

  it("leaves resolved spans paused when autoplay is disabled", async () => {
    mockUiConfig.autostartVideo = false;
    mockVideos.segments.spanDetail.mockResolvedValue(buildDetail());
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockVideos.get.mockResolvedValue(buildVideo());
    mockSegmentDisplayProfiles.get.mockResolvedValue({ id: 3, name: "Default Profile" });
    mockSegmentLibrary.list.mockResolvedValue({ items: [] });

    renderPage();

    expect(await screen.findByTestId("resolved-span-autostart")).toHaveTextContent("false");
  });

  it("describes derived intersection spans without union-only copy", async () => {
    const detail = buildDetail();
    detail.span.spanKey = "dq-intersection-5000-25000";
    detail.span.tagName = "Intersection";

    mockVideos.segments.spanDetail.mockResolvedValue(detail);
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockVideos.get.mockResolvedValue(buildVideo());
    mockSegmentDisplayProfiles.get.mockResolvedValue({ id: 3, name: "Default Profile" });

    renderPage({
      spanKey: detail.span.spanKey,
      profileId: 3,
      derivedQueryDescriptor: {
        operator: "intersection",
        operands: [],
      },
    });

    expect((await screen.findAllByText("Intersection")).length).toBeGreaterThan(0);
    expect(screen.getByText("Derived")).toBeInTheDocument();
    expect(screen.queryByText(/Union progress/i)).not.toBeInTheDocument();
  });

  it("creates a metadata-preserving video from the resolved span", async () => {
    mockVideos.segments.spanDetail.mockResolvedValue(buildDetail());
    mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
    mockVideos.get.mockResolvedValue(buildVideo());
    mockVideos.createSubVideo.mockResolvedValue({ id: 777 });
    mockSegmentDisplayProfiles.get.mockResolvedValue({ id: 3, name: "Default Profile" });
    mockSegmentLibrary.list.mockResolvedValue({
      items: [
        { id: 71, startSec: 5, endSec: 10, title: "Segment One" },
        { id: 72, startSec: 20, endSec: 25, title: "Segment Two" },
      ],
    });

    const onNavigate = vi.fn();
    renderPage({ onNavigate });

    fireEvent.click(await screen.findByTitle("Operations"));
    fireEvent.click(await screen.findByRole("button", { name: /make video/i }));

    await waitFor(() => {
      expect(mockVideos.createSubVideo).toHaveBeenCalledWith(
        14,
        expect.objectContaining({
          title: "Action Sequence",
          code: "SC-14",
          details: "Resolved parent video",
          director: "Director Span",
          date: "2026-05-01",
          organized: true,
          studioId: 22,
          urls: ["https://example.test/video-14"],
          tagIds: [8],
          performerIds: [31],
          galleryIds: [45],
          groups: [{ groupId: 55, videoIndex: 2 }],
          customFields: { energy: "high" },
          parentVideoId: 14,
          clipStartSec: 5,
          clipEndSec: 25,
        }),
      );
    });
    await waitFor(() => {
      expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 777 });
    });
  });

  describe("overview", () => {
    function buildFaceDetail() {
      return {
        videoId: 14,
        videoTitle: "Video Fourteen",
        profileId: 3,
        span: {
          spanKey: "face-14",
          startSec: 5,
          endSec: 25,
          tagName: "face-0031",
          kind: "face",
          segmentIds: [71, 72],
          sourceKey: "ext:ai.faces",
        },
        intervals: [
          { startSec: 5, endSec: 10 },
          { startSec: 20, endSec: 25 },
        ],
      };
    }

    function rawFaceSegment(id: number, startSec: number, endSec: number) {
      return {
        id,
        hostType: "video",
        hostId: 14,
        startSec,
        endSec,
        kind: "face",
        refId: 31,
        refLabel: "face-0031",
        title: "face-0031",
        sourceKey: "ext:ai.faces",
        confidence: 0.8,
      };
    }

    function mockFaceSpan() {
      mockVideos.segments.spanDetail.mockResolvedValue(buildFaceDetail());
      mockVideos.segments.spans.mockResolvedValue({ profileId: 3, spans: [] });
      mockVideos.get.mockResolvedValue(buildVideo());
      mockSegmentDisplayProfiles.get.mockResolvedValue({ id: 3, name: "Default Profile" });
      mockFaces.get.mockResolvedValue({
        id: 31,
        label: "face-0031",
        primarySourceKey: "face-0031",
        ignored: false,
        topSuggestion: { performerId: 40, performerName: "Suggested Person", confidence: 0.7 },
      });
      mockFaces.suggestions.mockResolvedValue([
        { performerId: 40, performerName: "Suggested Person", confidence: 70, why: "Looks alike", evidence: [] },
      ]);
    }

    it("names a face span by its face's suggestion and shows the face box before its raw segments load", async () => {
      mockFaceSpan();
      let releaseSegments: (value: unknown) => void = () => {};
      mockSegmentLibrary.list.mockReturnValue(new Promise((resolve) => (releaseSegments = resolve)));

      renderPage({ spanKey: "face-14" });

      // The player mounts while the raw segments are still loading, and reads its X-ray default only then.
      await waitFor(() =>
        expect(playerProps).toHaveBeenCalledWith(expect.objectContaining({ faceOverlayDefault: true })),
      );
      releaseSegments({ items: [rawFaceSegment(71, 5, 10), rawFaceSegment(72, 20, 25)] });

      expect(await screen.findByRole("heading", { name: "Suggested Person (suggested)" })).toBeInTheDocument();
      expect(screen.getByText("Segment · 2 raw segments")).toBeInTheDocument();
      // An untagged span's stored title is not offered as a tag.
      expect(screen.getByRole("button", { name: "face-0031" })).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Open tag" })).not.toBeInTheDocument();
      expect(await screen.findByRole("button", { name: "Accept" })).toBeInTheDocument();
    });

    it("names nearby face segments by their face, and uses the segment's first raw segment for its own face", async () => {
      mockFaceSpan();
      const detail = buildFaceDetail();
      // The span's first raw segment (72) is a different face from the earliest-starting one (71).
      detail.span.segmentIds = [72, 71];
      mockVideos.segments.spanDetail.mockResolvedValue(detail);
      mockVideos.segments.spans.mockResolvedValue({
        profileId: 3,
        spans: [
          {
            spanKey: "face-later",
            hostId: 14,
            startSec: 40,
            endSec: 60,
            kind: "face",
            tagName: "face-0099",
            segmentIds: [90],
            collapsedToInstant: false,
          },
        ],
      });
      mockSegmentLibrary.list.mockImplementation(async ({ ids }: { ids: string }) => ({
        items:
          ids === "90"
            ? [{ ...rawFaceSegment(90, 40, 60), refId: 99, refLabel: "face-0099", title: "face-0099" }]
            : [
                rawFaceSegment(71, 5, 10),
                { ...rawFaceSegment(72, 20, 25), refId: 32, refLabel: "face-0032", title: "face-0032" },
              ],
      }));
      mockFaces.get.mockImplementation(async (id: number) => ({
        id,
        label: `face-00${id}`,
        primarySourceKey: `face-00${id}`,
        ignored: false,
      }));
      mockFaces.findByIds.mockResolvedValue({
        items: [
          {
            id: 99,
            label: "face-0099",
            primarySourceKey: "face-0099",
            ignored: false,
            performerId: 5,
            performerName: "Later Person",
          },
        ],
        totalCount: 1,
      });

      renderPage({ spanKey: "face-14" });

      expect(await screen.findByText("Later Person")).toBeInTheDocument();
      await waitFor(() => expect(mockFaces.get).toHaveBeenCalledWith(32));
      expect(mockFaces.get).not.toHaveBeenCalledWith(31);
    });

    it("does not load or link the video without permission to read videos", async () => {
      mockHasPermission.mockImplementation((permission: string) => !permission.startsWith("videos"));
      mockFaceSpan();
      mockSegmentLibrary.list.mockResolvedValue({ items: [rawFaceSegment(71, 5, 10)] });

      renderPage({ spanKey: "face-14" });

      expect(await screen.findByText(/permissions do not allow video playback/)).toBeInTheDocument();
      expect(mockVideos.get).not.toHaveBeenCalled();
      expect(screen.queryByTitle("Open parent video")).not.toBeInTheDocument();
    });

    it("keeps the page and player in place while another segment of the same video loads", async () => {
      mockFaceSpan();
      mockSegmentLibrary.list.mockResolvedValue({ items: [rawFaceSegment(71, 5, 10), rawFaceSegment(72, 20, 25)] });
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      const page = (spanKey: string) => (
        <QueryClientProvider client={queryClient}>
          <ResolvedSpanPlayPage videoId={14} spanKey={spanKey} onNavigate={vi.fn()} />
        </QueryClientProvider>
      );
      const { rerender } = render(page("face-14"));
      const player = await screen.findByTestId("resolved-span-player");

      let releaseNext: (value: unknown) => void = () => {};
      mockVideos.segments.spanDetail.mockReturnValue(new Promise((resolve) => (releaseNext = resolve)));
      rerender(page("face-15"));

      // While the next segment loads, the current one stays on screen with the same player.
      expect(screen.getByTestId("resolved-span-player")).toBe(player);
      expect(player).toHaveTextContent("Clip 5-25");

      const next = buildFaceDetail();
      next.span = { ...next.span, spanKey: "face-15", startSec: 40, endSec: 60 };
      releaseNext(next);
      await waitFor(() => expect(screen.getByTestId("resolved-span-player")).toHaveTextContent("Clip 40-60 @ 40"));
      expect(screen.getByTestId("resolved-span-player")).toBe(player);
    });

    it("highlights the raw segment under the playhead", async () => {
      mockFaceSpan();
      mockSegmentLibrary.list.mockResolvedValue({ items: [rawFaceSegment(71, 5, 10), rawFaceSegment(72, 20, 25)] });

      renderPage({ spanKey: "face-14" });

      await screen.findByRole("button", { name: "Play from 0:20" });
      const { onTimeUpdate } = playerProps.mock.lastCall![0] as { onTimeUpdate: (time: number) => void };
      act(() => onTimeUpdate(21));

      const rows = screen.getAllByRole("listitem").filter((row) => row.querySelector("[aria-label^='Play from']"));
      expect(rows.map((row) => row.getAttribute("aria-current"))).toEqual([null, "true"]);
    });

    it("offers to retry when the raw segments fail to load", async () => {
      mockFaceSpan();
      mockSegmentLibrary.list.mockRejectedValueOnce(new Error("API Error 500: boom")).mockResolvedValueOnce({
        items: [rawFaceSegment(71, 5, 10)],
      });

      renderPage({ spanKey: "face-14" });

      expect(await screen.findByText("Could not load raw segments")).toBeInTheDocument();
      fireEvent.click(screen.getByRole("button", { name: "Try again" }));
      expect(await screen.findByRole("button", { name: "Play from 0:05" })).toBeInTheDocument();
    });
  });
});
