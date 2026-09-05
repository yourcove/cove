import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { GroupItemFeed } from "../components/GroupItemFeed";

const { authState, mocks } = vi.hoisted(() => ({
  authState: {
    user: {
      id: "7",
      kind: "user",
      readGrantedEntityKinds: [] as string[],
      uiPreferences: {} as { renderMarkdown?: boolean },
    },
    permissions: ["groups.read", "videos.read", "images.read"],
  },
  mocks: {
    getGroup: vi.fn(),
    pageItems: vi.fn(),
    getVideo: vi.fn(),
    getImage: vi.fn(),
    useEntityEngagement: vi.fn(),
  },
}));

vi.mock("../api/client", () => ({
  groups: { get: mocks.getGroup, items: { page: mocks.pageItems } },
  videos: {
    get: mocks.getVideo,
    streamUrl: (id: number) => `/videos/${id}/stream`,
    previewUrl: (id: number) => `/videos/${id}/preview`,
    previewStatusUrl: (id: number) => `/videos/${id}/preview/status`,
  },
  images: { get: mocks.getImage, thumbnailUrl: (id: number) => `/images/${id}/thumbnail` },
  audios: { get: vi.fn() },
  texts: { get: vi.fn() },
  performers: { get: vi.fn() },
  studios: { get: vi.fn() },
  tags: { get: vi.fn() },
  galleries: { get: vi.fn() },
  faces: { get: vi.fn() },
  segmentLibrary: { get: vi.fn() },
  entityImages: { videoCoverUrl: (id: number) => `/videos/${id}/cover` },
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ ...authState, hasPermission: () => true }),
}));

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: mocks.useEntityEngagement,
}));

vi.mock("../components/WallMediaCard", () => ({
  WallMediaCard: ({
    title,
    videoSrc,
    videoStartTimeSec,
    videoEndTimeSec,
    children,
  }: {
    title: string;
    videoSrc?: string;
    videoStartTimeSec?: number;
    videoEndTimeSec?: number;
    children?: React.ReactNode;
  }) => (
    <div data-video-src={videoSrc} data-video-start={videoStartTimeSec} data-video-end={videoEndTimeSec}>
      Media for {title}
      {children}
    </div>
  ),
}));

vi.mock("../components/VirtualizedInfiniteList", () => ({
  VirtualizedInfiniteList: ({
    items,
    renderItem,
  }: {
    items: unknown[];
    renderItem: (arg: { item: unknown; index: number; isActive: boolean }) => React.ReactNode;
  }) => (
    <div>
      {items.map((item, index) => (
        <div key={index}>{renderItem({ item, index, isActive: true })}</div>
      ))}
    </div>
  ),
}));

describe("GroupItemFeed", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    authState.user = { id: "7", kind: "user", readGrantedEntityKinds: [], uiPreferences: {} };
    authState.permissions = ["groups.read", "videos.read", "images.read"];
    mocks.useEntityEngagement.mockReturnValue({
      engagement: undefined,
      rating: undefined,
      setRating: vi.fn(),
      ratingPending: false,
    });
    mocks.getGroup.mockResolvedValue({ id: 4, name: "Mixed group", kind: "static" });
    mocks.pageItems.mockResolvedValue({
      items: [
        { id: 11, groupId: 4, orderIndex: 0, kind: "video", videoId: 21, createdAt: "", updatedAt: "" },
        { id: 12, groupId: 4, orderIndex: 1, kind: "image", imageId: 22, createdAt: "", updatedAt: "" },
      ],
      totalCount: 2,
      page: 1,
      perPage: 10,
    });
    mocks.getVideo.mockResolvedValue({
      id: 21,
      title: "Video entry",
      files: [{ width: 1920, height: 1080, duration: 60 }],
      updatedAt: "",
      tags: [],
      performers: [],
    });
    mocks.getImage.mockResolvedValue({
      id: 22,
      title: "Image entry",
      files: [{ width: 1200, height: 800 }],
      updatedAt: "",
      tags: [],
      performers: [],
    });
  });

  it("renders an ordered mixed group as native feed cards and navigates to an item", async () => {
    const onNavigate = vi.fn();
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <GroupItemFeed groupId={4} onNavigate={onNavigate} />
      </QueryClientProvider>,
    );

    expect(await screen.findByRole("heading", { name: "Mixed group" })).toBeInTheDocument();
    expect(await screen.findByText("Video entry")).toBeInTheDocument();
    expect(await screen.findByText("Image entry")).toBeInTheDocument();
    expect(mocks.pageItems).toHaveBeenCalledWith(4, { page: 1, perPage: 10, sort: "order", direction: "asc" });
    expect(screen.getByRole("button", { name: "Image entry" })).toHaveClass("max-w-full", "[overflow-wrap:anywhere]");

    fireEvent.click(screen.getByRole("button", { name: "Video entry" }));
    expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 21 });
    await waitFor(() => expect(document.querySelectorAll("[data-feed-group-item]")).toHaveLength(2));
    expect(
      mocks.useEntityEngagement.mock.calls.some(
        (call) => call[2]?.enabled === true && String(call[2]?.queryScope).startsWith("user:7:"),
      ),
    ).toBe(true);
  });

  it("renders feed details as Markdown when the viewer enables it", async () => {
    authState.user.uiPreferences = { renderMarkdown: true };
    mocks.getVideo.mockResolvedValue({
      id: 21,
      title: "Video entry",
      details: "**Formatted feed details**",
      files: [{ width: 1920, height: 1080, duration: 60 }],
      updatedAt: "",
      tags: [],
      performers: [],
    });

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <GroupItemFeed groupId={4} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(await screen.findByText("Formatted feed details", { selector: "strong" })).toBeInTheDocument();
  });

  it("uses a new cache namespace when the authenticated principal changes", async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: 60_000 } } });
    const view = render(
      <QueryClientProvider client={client}>
        <GroupItemFeed groupId={4} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );
    expect(await screen.findByRole("heading", { name: "Mixed group" })).toBeInTheDocument();
    expect(mocks.pageItems).toHaveBeenCalledTimes(1);

    authState.user = { id: "8", kind: "user", readGrantedEntityKinds: [], uiPreferences: {} };
    view.rerender(
      <QueryClientProvider client={client}>
        <GroupItemFeed groupId={4} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(mocks.pageItems).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(mocks.getVideo).toHaveBeenCalledTimes(2));
  });

  it("does not expose cached group-item metadata or engagement when host hydration is denied", async () => {
    mocks.pageItems.mockResolvedValue({
      items: [
        {
          id: 11,
          groupId: 4,
          orderIndex: 0,
          kind: "video",
          videoId: 21,
          videoTitle: "Restricted video",
          title: "Restricted override",
          notes: "Restricted notes",
          createdAt: "",
          updatedAt: "",
        },
        {
          id: 12,
          groupId: 4,
          orderIndex: 1,
          kind: "image",
          imageId: 22,
          imageTitle: "Restricted image",
          createdAt: "",
          updatedAt: "",
        },
      ],
      totalCount: 2,
      page: 1,
      perPage: 10,
    });
    mocks.getVideo.mockRejectedValue(new Error("Forbidden"));
    mocks.getImage.mockRejectedValue(new Error("Forbidden"));

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <GroupItemFeed groupId={4} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(await screen.findAllByText("Unavailable item", { selector: "span" })).toHaveLength(4);
    expect(screen.queryByText(/Restricted/)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Restricted/ })).not.toBeInTheDocument();
    expect(mocks.useEntityEngagement.mock.calls.some((call) => call[2]?.enabled === true)).toBe(false);
  });

  it("uses the full stream and bounded playback times for video-range items", async () => {
    mocks.pageItems.mockResolvedValue({
      items: [
        {
          id: 11,
          groupId: 4,
          orderIndex: 0,
          kind: "videoRange",
          videoId: 21,
          startSec: 12,
          endSec: 20,
          createdAt: "",
          updatedAt: "",
        },
      ],
      totalCount: 1,
      page: 1,
      perPage: 10,
    });

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <GroupItemFeed groupId={4} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    const media = await screen.findByText("Media for Video entry");
    expect(media).toHaveAttribute("data-video-src", "/videos/21/stream");
    expect(media).toHaveAttribute("data-video-start", "12");
    expect(media).toHaveAttribute("data-video-end", "20");
  });
});
