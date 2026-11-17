import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { segmentLibrary, segmentSpans } from "../api/client";
import type { CompilationPlayerItem } from "../components/CompilationPlayer";
import { SEGMENTS_PLAYBACK_PAGE_SIZE, SegmentsPlayPage } from "../pages/SegmentsPlayPage";
import type { SegmentsPlaybackSource } from "../router/location";

const playerProps = vi.hoisted(() => ({
  latest: undefined as undefined | PlayerProps,
}));

vi.mock("../api/client", () => ({
  segmentSpans: { search: vi.fn() },
  segmentLibrary: { findFiltered: vi.fn() },
}));
vi.mock("../components/CompilationPlayer", () => ({
  CompilationPlayer: (props: PlayerProps) => {
    playerProps.latest = props;
    return (
      <div data-testid="player">
        {props.items.length} loaded
        {props.notice}
      </div>
    );
  },
}));

type PlayerProps = {
  items: CompilationPlayerItem[];
  totalCount?: number;
  hasMore?: boolean;
  onNearEnd?: () => void;
  notice?: React.ReactNode;
  getPlaybackTracking?: (item: CompilationPlayerItem) => Record<string, unknown> | undefined;
};
vi.mock("../auth/AuthContext", () => ({ useAuth: () => ({ hasPermission: () => true }) }));
vi.mock("../hooks/useBackNavigation", () => ({
  useBackNavigation: () => ({ backLabel: "Back", goBack: vi.fn(), backAlternative: undefined }),
}));
vi.mock("../hooks/useDocumentTitle", () => ({ useDocumentTitle: () => {} }));
vi.mock("../pages/segments/useListedFaceNames", () => ({ useListedFaceNames: () => new Map() }));
vi.mock("../pages/segments/useDerivedOperandNameMaps", () => ({
  useDerivedOperandNameMaps: () => ({ tags: new Map(), performers: new Map(), faces: new Map() }),
}));

const returnRoute = { page: "segments" };

function spanResult(index: number) {
  return {
    videoId: 10 + index,
    videoTitle: `Video ${index}`,
    span: {
      spanKey: `span-${index}`,
      hostType: "video" as const,
      hostId: 10 + index,
      startSec: 60,
      endSec: 70,
      tagName: "Facial",
      collapsedToInstant: false,
      segmentIds: [index],
    },
    profileId: 2,
  };
}

function renderPage(source: SegmentsPlaybackSource) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <SegmentsPlayPage source={source} onNavigate={vi.fn()} />
    </QueryClientProvider>,
  );
}

describe("SegmentsPlayPage", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    playerProps.latest = undefined;
  });

  it("plays spans in result order and loads the next page only when playback nears its end", async () => {
    vi.mocked(segmentSpans.search).mockImplementation(async (request) => ({
      items: [spanResult(request.page === 1 ? 1 : 2)],
      totalCount: 2,
      page: request.page ?? 1,
      perPage: SEGMENTS_PLAYBACK_PAGE_SIZE,
      hasMore: request.page === 1,
    }));
    renderPage({
      view: "spans",
      request: { profile: 2, sort: "random", seed: 99, page: 1, perPage: 24 },
      returnRoute,
    });

    expect(await screen.findByText("1 loaded")).toBeInTheDocument();
    expect(segmentSpans.search).toHaveBeenCalledTimes(1);
    expect(segmentSpans.search).toHaveBeenLastCalledWith(
      expect.objectContaining({ profile: 2, sort: "random", seed: 99, page: 1, perPage: SEGMENTS_PLAYBACK_PAGE_SIZE }),
    );
    expect(playerProps.latest?.totalCount).toBe(2);
    expect(playerProps.latest?.items[0]).toEqual(
      expect.objectContaining({
        videoId: 11,
        videoTitle: "Video 1",
        startSec: 60,
        endSec: 70,
        detailRoute: expect.objectContaining({ page: "video-span", id: 11, spanKey: "span-1", profileId: 2 }),
      }),
    );

    act(() => playerProps.latest?.onNearEnd?.());

    expect(await screen.findByText("2 loaded")).toBeInTheDocument();
    expect(segmentSpans.search).toHaveBeenLastCalledWith(expect.objectContaining({ seed: 99, page: 2 }));
    // The whole list is loaded, so there is no remaining total to report and nothing more to ask for.
    expect(playerProps.latest?.totalCount).toBeUndefined();
    act(() => playerProps.latest?.onNearEnd?.());
    await waitFor(() => expect(segmentSpans.search).toHaveBeenCalledTimes(2));
  });

  it("pages raw segments through the request's find filter and links each to its segment", async () => {
    vi.mocked(segmentLibrary.findFiltered).mockResolvedValue({
      items: [{ id: 501, hostType: "video", hostId: 10, hostTitle: "Source", startSec: 5, endSec: 9, tagName: "Kiss" }],
      totalCount: 1,
      page: 1,
      perPage: SEGMENTS_PLAYBACK_PAGE_SIZE,
    } as never);
    renderPage({
      view: "raw",
      request: {
        findFilter: { sort: "random", seed: 7, page: 3, perPage: 24 },
        objectFilter: { tagsCriterion: { value: [804], modifier: "INCLUDES_ALL" } },
      },
      returnRoute,
    });

    expect(await screen.findByText("1 loaded")).toBeInTheDocument();
    expect(segmentLibrary.findFiltered).toHaveBeenCalledWith({
      findFilter: { sort: "random", seed: 7, page: 1, perPage: SEGMENTS_PLAYBACK_PAGE_SIZE },
      objectFilter: { tagsCriterion: { value: [804], modifier: "INCLUDES_ALL" } },
    });
    expect(playerProps.latest?.items[0]).toEqual(
      expect.objectContaining({ segmentId: 501, videoId: 10, videoTitle: "Source", title: "Kiss", startSec: 5 }),
    );
  });

  it("asks the user to start from the Segments page when there is nothing to play", () => {
    const queryClient = new QueryClient();
    render(
      <QueryClientProvider client={queryClient}>
        <SegmentsPlayPage onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(screen.getByText("Nothing to play. Start playback from the Segments page.")).toBeInTheDocument();
    expect(segmentSpans.search).not.toHaveBeenCalled();
  });

  it("keeps playing what loaded when a later page fails, and retries only when asked", async () => {
    vi.mocked(segmentSpans.search).mockImplementation(async (request) => {
      if (request.page === 2) throw new Error("timeout on page 2");
      return { items: [spanResult(1)], totalCount: 2, page: 1, perPage: SEGMENTS_PLAYBACK_PAGE_SIZE, hasMore: true };
    });
    renderPage({ view: "spans", request: { profile: 2 }, returnRoute });

    expect(await screen.findByText("1 loaded")).toBeInTheDocument();
    act(() => playerProps.latest?.onNearEnd?.());

    expect(await screen.findByText(/More segments failed to load/)).toBeInTheDocument();
    expect(screen.getByTestId("player")).toHaveTextContent("1 loaded");
    // Playback asking again does not hammer the failing page.
    act(() => playerProps.latest?.onNearEnd?.());
    act(() => playerProps.latest?.onNearEnd?.());
    expect(segmentSpans.search).toHaveBeenCalledTimes(2);

    vi.mocked(segmentSpans.search).mockResolvedValue({
      items: [spanResult(2)],
      totalCount: 2,
      page: 2,
      perPage: SEGMENTS_PLAYBACK_PAGE_SIZE,
      hasMore: false,
    } as never);
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText("2 loaded")).toBeInTheDocument();
    expect(screen.queryByText(/More segments failed to load/)).not.toBeInTheDocument();
  });

  it("reports a list of unknown length as having more, without a made-up total", async () => {
    vi.mocked(segmentSpans.search).mockResolvedValue({
      items: [spanResult(1)],
      totalCount: -1,
      page: 1,
      perPage: SEGMENTS_PLAYBACK_PAGE_SIZE,
      hasMore: true,
    } as never);
    renderPage({ view: "spans", request: { profile: 2 }, returnRoute });

    expect(await screen.findByText("1 loaded")).toBeInTheDocument();
    expect(playerProps.latest?.hasMore).toBe(true);
    expect(playerProps.latest?.totalCount).toBeUndefined();
  });

  it("tracks raw segment plays as segment plays and spans like the span page, never as video page plays", async () => {
    vi.mocked(segmentLibrary.findFiltered).mockResolvedValue({
      items: [{ id: 501, hostType: "video", hostId: 10, hostTitle: "Source", startSec: 5, endSec: 9, tagName: "Kiss" }],
      totalCount: 1,
      page: 1,
      perPage: SEGMENTS_PLAYBACK_PAGE_SIZE,
    } as never);
    renderPage({ view: "raw", request: { findFilter: {} }, returnRoute });
    await screen.findByText("1 loaded");

    const item = playerProps.latest!.items[0];
    expect(playerProps.latest?.getPlaybackTracking?.(item)).toEqual(
      expect.objectContaining({
        hostType: "segment",
        hostId: 501,
        surface: "segmentsPlayback",
        parentHostType: "video",
        parentHostId: 10,
        segmentId: 501,
        clipStartSec: 5,
        clipEndSec: 9,
      }),
    );
    const span = {
      ...item,
      segmentId: undefined,
      detailRoute: { page: "video-span", id: 10, spanKey: "span-1" },
    };
    expect(playerProps.latest?.getPlaybackTracking?.(span)).toEqual(
      expect.objectContaining({
        hostType: "video",
        hostId: 10,
        surface: "segmentsPlayback",
        context: { spanKey: "span-1" },
      }),
    );
  });
});
