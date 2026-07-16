import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ResolvedSpanPlayPage } from "../pages/ResolvedSpanPlayPage";

const { mockVideos, mockSegmentDisplayProfiles, mockSegmentLibrary, mockGoBack, mockUiConfig } = vi.hoisted(() => ({
  mockVideos: {
    get: vi.fn(),
    createSubVideo: vi.fn(),
    streamUrl: vi.fn((id: number) => `/video-${id}.mp4`),
    screenshotUrl: vi.fn((id: number) => `/video-${id}.jpg`),
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
}));

vi.mock("../hooks/useDocumentTitle", () => ({
  useDocumentTitle: () => {},
}));

vi.mock("../api/client", () => ({
  videos: mockVideos,
  segmentDisplayProfiles: mockSegmentDisplayProfiles,
  segmentLibrary: mockSegmentLibrary,
  faces: { get: vi.fn() },
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
    hasPermission: () => true,
  }),
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({
    config: { ui: mockUiConfig },
  }),
}));

vi.mock("../components/VideoPlayer", () => ({
  VideoPlayer: ({ clip, resumeTime, autostart, onEnded }: { clip: { start: number; end: number }; resumeTime?: number; autostart?: boolean; onEnded?: () => void }) => (
    <div>
      <div data-testid="resolved-span-player">Clip {clip.start}-{clip.end} @ {resumeTime}</div>
      <div data-testid="resolved-span-autostart">{String(autostart)}</div>
      <button type="button" onClick={() => onEnded?.()}>End clip</button>
    </div>
  ),
}));

function buildDetail() {
  return {
    videoId: 14,
    videoTitle: "Video Fourteen",
    profileId: 3,
    span: {
      spanKey: "tag-14",
      startSec: 5,
      endSec: 25,
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
      <ResolvedSpanPlayPage
        videoId={14}
        spanKey="tag-14"
        onNavigate={vi.fn()}
        {...props}
      />
    </QueryClientProvider>,
  );
}

describe("ResolvedSpanPlayPage", () => {
  afterEach(() => {
    vi.clearAllMocks();
    mockUiConfig.autostartVideo = true;
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

    expect(await screen.findByText("Clip 5-10 @ 5")).toBeInTheDocument();
    expect(screen.getByTestId("resolved-span-autostart")).toHaveTextContent("true");
    expect(screen.queryByTestId("media-detail-layout-media-frame")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: /intervals/i }));
    expect(await screen.findByText("Interval 1")).toBeInTheDocument();
    expect(screen.getByText("Interval 2")).toBeInTheDocument();
  });

  it("auto-advances between intervals", async () => {
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

    expect(await screen.findByText("Clip 5-10 @ 5")).toBeInTheDocument();

    fireEvent.click(screen.getByText("End clip"));
    expect(await screen.findByText("Clip 20-25 @ 20")).toBeInTheDocument();
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
      expect(mockVideos.createSubVideo).toHaveBeenCalledWith(14, expect.objectContaining({
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
      }));
    });
    await waitFor(() => {
      expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 777 });
    });
  });
});
