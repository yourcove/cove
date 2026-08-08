import { act, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../components/Rating", () => ({
  RatingBanner: () => null,
  RatingBadge: () => null,
}));

import { GalleryPreviewList, GalleryTile, GroupTile, ImageTile, PerformerTile, VideoCard, VideoCardPopovers } from "../components/EntityCards";
import { DetailsTab, FileInfoTab } from "../pages/VideoDetailPage";

const videoFile = {
  id: 10,
  basename: "alpha.mp4",
  path: "C:\\library\\alpha.mp4",
  size: 1_048_576,
  duration: 120,
  width: 1920,
  height: 1080,
  frameRate: 29.97,
  bitRate: 2_400_000,
  videoCodec: "H264",
  audioCodec: "AAC",
  fingerprints: [],
};

const baseVideo = {
  id: 42,
  title: "Sample Video",
  updatedAt: "2024-01-12T00:00:00Z",
  files: [videoFile],
  performers: [],
  groups: [],
  galleries: [],
  tags: [],
  studioName: null,
  studioId: null,
  resumeTime: 0,
  rating: null,
  likeCounter: 1,
  organized: false,
  details: null,
  date: null,
  playCount: 0,
};

const baseGallery = {
  id: 7,
  title: "Sample Gallery",
  date: null,
  details: null,
  photographer: null,
  organized: false,
  coverPath: undefined,
  studioId: null,
  studioName: null,
  urls: [],
  tags: [],
  performers: [],
  imageCount: 12,
  videoCount: 3,
  videoIds: [],
  folderPath: null,
  files: [],
  customFields: undefined,
  createdAt: "2024-01-11T00:00:00Z",
  updatedAt: "2024-01-12T00:00:00Z",
};

function renderWithQueryClient(ui: React.ReactElement) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

beforeEach(() => {
  vi.restoreAllMocks();
  vi.stubGlobal(
    "IntersectionObserver",
    class {
      observe() {}
      disconnect() {}
      unobserve() {}
    }
  );
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("VideoCard navigation", () => {
  it("shows the video placeholder when the cover image fails to load", () => {
    const { container } = render(<VideoCard video={baseVideo as any} onClick={vi.fn()} />);

    fireEvent.error(container.querySelector(".video-card-preview-image")!);

    expect(container.querySelector(".video-card-cover-fallback")).toBeVisible();
    expect(container.querySelector(".video-card-preview-image")).not.toBeInTheDocument();
  });

  it("renders the main video surface as a real link", () => {
    const onClick = vi.fn();
    render(<VideoCard video={baseVideo as any} onClick={onClick} />);

    expect(screen.getByRole("link", { name: /Open video Sample Video/i })).toHaveAttribute("href", "/video/42");
    expect(onClick).not.toHaveBeenCalled();
  });

  it("navigates in-place on a plain left click through the video link", () => {
    const onClick = vi.fn();
    render(<VideoCard video={baseVideo as any} onClick={onClick} />);

    fireEvent.click(screen.getByRole("link", { name: /Open video Sample Video/i }));

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it("lets modified clicks fall through to normal browser link behavior", () => {
    const onClick = vi.fn();
    render(<VideoCard video={baseVideo as any} onClick={onClick} />);

    fireEvent.click(screen.getByRole("link", { name: /Open video Sample Video/i }), { ctrlKey: true });

    expect(onClick).not.toHaveBeenCalled();
  });

  it("renders performer badges as real links without triggering video navigation on modified clicks", () => {
    const onClick = vi.fn();
    const onNavigate = vi.fn();

    render(
      <VideoCard
        video={{
          ...baseVideo,
          performers: [{ id: 7, name: "Alice Example", imagePath: null }],
        } as any}
        onClick={onClick}
        onNavigate={onNavigate}
      />
    );

    const performerLink = screen.getByRole("link", { name: /Alice Example/i });
    fireEvent.click(performerLink, { ctrlKey: true });

    expect(performerLink).toHaveAttribute("href", "/performer/7");
    expect(onClick).not.toHaveBeenCalled();
    expect(onNavigate).not.toHaveBeenCalled();
  });

  it("renders performer popover items as real links", () => {
    vi.useFakeTimers();

    render(
      <VideoCardPopovers
        video={{
          ...baseVideo,
          performers: [{ id: 9, name: "Popover Performer", imagePath: null }],
        } as any}
      />
    );

    fireEvent.mouseEnter(screen.getByTitle("Performers"));
    act(() => {
      vi.advanceTimersByTime(250);
    });

    expect(screen.getByRole("link", { name: /Popover Performer/i })).toHaveAttribute("href", "/performer/9");
  });

  it("navigates to gallery popover items when no navigation callback is provided", () => {
    window.history.replaceState(null, "", "/images");
    render(<GalleryPreviewList galleries={[{ id: 17, title: "Linked Gallery" }]} />);

    fireEvent.click(screen.getByRole("link", { name: "Linked Gallery" }));

    expect(window.location.pathname).toBe("/gallery/17");
    window.history.replaceState(null, "", "/");
  });

  it("does not create hover media for tag references without an image", () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);

    renderWithQueryClient(
      <VideoCardPopovers
        video={{
          ...baseVideo,
          tags: [
            { id: 9, name: "Featured", description: "List preview", videoCount: 12, hasImage: false },
            { id: 10, name: "Second tag", hasImage: true },
          ],
        } as any}
      />
    );

    fireEvent.mouseEnter(screen.getByTitle("Tags"));
    act(() => {
      vi.advanceTimersByTime(250);
    });
    fireEvent.mouseEnter(screen.getByRole("link", { name: "Featured" }));

    expect(screen.getByRole("link", { name: "Featured" })).toHaveAttribute("href", "/tag/9");
    expect(screen.getByRole("link", { name: "Featured" }).parentElement).toHaveClass("space-y-1");
    expect(screen.queryByRole("tooltip", { name: "Media for Featured" })).not.toBeInTheDocument();
    expect(screen.queryByRole("img", { name: "Featured" })).not.toBeInTheDocument();
    fireEvent.mouseEnter(screen.getByRole("link", { name: "Second tag" }));
    expect(screen.getByRole("img", { name: "Second tag" })).toHaveAttribute("src", "/api/tags/10/image?max=640");
    expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith("/api/tags/"))).toBe(false);
  });

  it("renders a likes counter instead of the legacy O badge", () => {
    render(<VideoCard video={baseVideo as any} engagement={{ hostId: 42, isFavorite: false, resumeTime: 0, playDuration: 0, playCount: 0, likeCount: 1, derivedLikeCount: 0, pageVisitCount: 0, completeCount: 0 }} onClick={vi.fn()} />);

    expect(screen.getByTitle("Likes: 1")).toBeInTheDocument();
    expect(screen.queryByText(/^O$/)).not.toBeInTheDocument();
  });

  it("does not render a favorite card control when the card is not favorited", () => {
    render(<VideoCard video={baseVideo as any} engagement={{ hostId: 42, isFavorite: false, resumeTime: 0, playDuration: 0, playCount: 0, likeCount: 0, derivedLikeCount: 0, pageVisitCount: 0, completeCount: 0 }} onClick={vi.fn()} />);

    expect(screen.queryByTitle("Favorite")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Favorite" })).not.toBeInTheDocument();
  });

  it("renders a non-interactive favorite indicator when the card is favorited", () => {
    render(<VideoCard video={baseVideo as any} engagement={{ hostId: 42, isFavorite: true, resumeTime: 0, playDuration: 0, playCount: 0, likeCount: 0, derivedLikeCount: 0, pageVisitCount: 0, completeCount: 0 }} onClick={vi.fn()} />);

    expect(screen.getByTitle("Favorite")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Favorite" })).not.toBeInTheDocument();
  });

  it("shows the hovered absolute timestamp above the video scrub preview bar", () => {
    const { container } = render(
      <VideoCard
        video={{
          ...baseVideo,
          clipStartSec: 35,
          clipEndSec: 95,
        } as any}
        onClick={vi.fn()}
      />
    );

    const scrubZone = container.querySelector(".cursor-ew-resize") as HTMLDivElement | null;
    expect(scrubZone).not.toBeNull();

    vi.spyOn(scrubZone!, "getBoundingClientRect").mockReturnValue({
      x: 0,
      y: 0,
      left: 0,
      top: 0,
      right: 100,
      bottom: 40,
      width: 100,
      height: 40,
      toJSON: () => ({}),
    } as DOMRect);

    fireEvent.mouseEnter(scrubZone!, { clientX: 50 });

    expect(screen.getByText("1:05")).toBeInTheDocument();

    fireEvent.mouseLeave(scrubZone!);

    expect(screen.queryByText("1:05")).not.toBeInTheDocument();
  });
});

describe("PerformerTile", () => {
  it("shows the performer fallback instead of an image when no image is present", () => {
    const { container } = render(
      <PerformerTile
        performer={{ id: 7, name: "No Photo Performer", tags: [] }}
        onClick={vi.fn()}
      />,
    );

    expect(screen.getByRole("link", { name: /Open performer No Photo Performer/i })).toHaveAttribute("href", "/performer/7");
    expect(container.querySelector("img")).not.toBeInTheDocument();
  });

  it("shows the performer likes count", () => {
    render(
      <PerformerTile
        performer={{ id: 7, name: "Liked Performer", tags: [], likeCount: 4 }}
        onClick={vi.fn()}
      />,
    );

    expect(screen.getByTitle("Likes: 4")).toBeInTheDocument();
  });
});

describe("GalleryTile", () => {
  it("shows the aggregate like count from gallery engagement", () => {
    const { container } = render(
      <GalleryTile
        gallery={baseGallery as any}
        engagement={{ likeCount: 6 } as any}
        onClick={vi.fn()}
      />,
    );

    expect(screen.getByTitle("Likes: 6")).toBeInTheDocument();
    expect(container.querySelector(".card-popovers")?.lastElementChild).toBe(screen.getByTitle("Likes: 6"));
  });

  it("uses media-enabled tag links in the shared reference popover", () => {
    vi.useFakeTimers();
    render(
      <GalleryTile
        gallery={{ ...baseGallery, tags: [{ id: 19, name: "Animated Gallery Tag", imagePath: "/gallery-tag.jpg" }] } as any}
        onClick={vi.fn()}
      />,
    );

    fireEvent.mouseEnter(screen.getByTitle("Tags"));
    act(() => vi.advanceTimersByTime(250));
    fireEvent.mouseEnter(screen.getByRole("link", { name: "Animated Gallery Tag" }));

    expect(screen.getByRole("tooltip", { name: "Media for Animated Gallery Tag" })).toContainElement(
      screen.getByRole("img", { name: "Animated Gallery Tag" }),
    );
  });

  it("shows image and video counts once in the footer popovers", () => {
    const { container } = render(<GalleryTile gallery={baseGallery as any} onClick={vi.fn()} />);

    const imagesButton = screen.getByTitle("Images");
    const videosButton = screen.getByTitle("Videos");

    expect(imagesButton).toHaveTextContent("12");
    expect(imagesButton.querySelector("svg")).toBeInTheDocument();
    expect(videosButton).toHaveTextContent("3");
    expect(videosButton.querySelector("svg")).toBeInTheDocument();
    expect(screen.queryByLabelText("12 images")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("3 videos")).not.toBeInTheDocument();
    expect(container.querySelector(".card-body")).not.toHaveTextContent("12 images");
  });

  it("uses a square media frame so gallery cards match image-card dimensions", () => {
    const { container } = render(<GalleryTile gallery={baseGallery as any} onClick={vi.fn()} />);

    expect(container.querySelector(".aspect-square")).toBeInTheDocument();
    expect(container.querySelector(".aspect-video")).not.toBeInTheDocument();
  });

  it("uses the effective gallery cover endpoint when no explicit cover path is present", () => {
    const { container } = render(<GalleryTile gallery={baseGallery as any} onClick={vi.fn()} />);

    expect((container.querySelector("img") as HTMLImageElement | null)?.getAttribute("src")).toContain("/api/galleries/7/cover");
  });

  it("shows the studio logo overlay and shared studio and performer popovers", () => {
    render(
      <GalleryTile
        gallery={{
          ...baseGallery,
          studioId: 9,
          studioName: "Studio Nine",
          performers: [{ id: 11, name: "Performer One", imagePath: null }],
        } as any}
        onClick={vi.fn()}
      />,
    );

    expect(screen.getByAltText("Studio Nine")).toHaveAttribute("src", expect.stringContaining("/api/studios/9/image"));
    expect(screen.getByTitle("Studio")).toBeInTheDocument();
    expect(screen.getByTitle("Performers")).toBeInTheDocument();
  });
});

describe("ImageTile", () => {
  it("uses media-enabled tag links in its tag popover", () => {
    vi.useFakeTimers();
    render(
      <ImageTile
        image={{
          id: 3,
          title: "Sample Image",
          organized: false,
          urls: [],
          tags: [{ id: 23, name: "Animated Image Tag", imagePath: "/image-tag.jpg" }],
          performers: [],
          galleryCount: 0,
          galleryIds: [],
          galleries: [],
          files: [],
          createdAt: "",
          updatedAt: "",
        } as any}
        onClick={vi.fn()}
      />,
    );

    fireEvent.mouseEnter(screen.getByTitle("Tags"));
    act(() => vi.advanceTimersByTime(250));
    fireEvent.mouseEnter(screen.getByRole("link", { name: "Animated Image Tag" }));

    expect(screen.getByRole("tooltip", { name: "Media for Animated Image Tag" })).toContainElement(
      screen.getByRole("img", { name: "Animated Image Tag" }),
    );
  });
});

describe("GroupTile", () => {
  it("uses hover popovers for dynamic mixed group counts", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({
      items: [
        { id: -1, groupId: 4, orderIndex: 0, kind: "video", videoId: 10, videoTitle: "Dynamic Video", hostType: "video", hostId: 10, title: "Dynamic Video", createdAt: "2026-05-01T00:00:00Z", updatedAt: "2026-05-01T00:00:00Z" },
        { id: -2, groupId: 4, orderIndex: 1, kind: "image", imageId: 20, imageTitle: "Group Image", hostType: "image", hostId: 20, title: "Group Image", createdAt: "2026-05-01T00:00:00Z", updatedAt: "2026-05-01T00:00:00Z" },
        { id: -3, groupId: 4, orderIndex: 2, kind: "audio", hostType: "audio", hostId: 30, title: "Group Audio", createdAt: "2026-05-01T00:00:00Z", updatedAt: "2026-05-01T00:00:00Z" },
        { id: -4, groupId: 4, orderIndex: 3, kind: "text", hostType: "text", hostId: 40, title: "Group Text", createdAt: "2026-05-01T00:00:00Z", updatedAt: "2026-05-01T00:00:00Z" },
        { id: -5, groupId: 4, orderIndex: 4, kind: "segment", hostType: "segment", hostId: 50, title: "Group Segment", createdAt: "2026-05-01T00:00:00Z", updatedAt: "2026-05-01T00:00:00Z" },
      ],
      totalCount: 5,
      page: 1,
      perPage: 0,
    }), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    renderWithQueryClient(
      <GroupTile
        group={{
          id: 4,
          name: "Mixed Dynamic Group",
          kind: "dynamic",
          frontImagePath: null,
          tags: [],
          videoCount: 1,
          imageCount: 1,
          audioCount: 1,
          textCount: 1,
          segmentCount: 1,
          subGroupCount: 0,
          itemCount: 5,
          createdAt: "2026-05-01T00:00:00Z",
          updatedAt: "2026-05-01T00:00:00Z",
        } as any}
        onClick={vi.fn()}
      />,
    );

    expect(screen.getByTitle("Videos").tagName).toBe("BUTTON");
    expect(screen.getByTitle("Images").tagName).toBe("BUTTON");
    expect(screen.getByTitle("Audios").tagName).toBe("BUTTON");
    expect(screen.getByTitle("Texts").tagName).toBe("BUTTON");
    expect(screen.getByTitle("Segments").tagName).toBe("BUTTON");

    fireEvent.mouseEnter(screen.getByTitle("Videos"));

    expect(await screen.findByText("Dynamic Video")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith("/api/groups/4/items/page?page=1&perPage=0&sort=order&direction=asc", expect.any(Object));
  });
});

describe("FileInfoTab", () => {
  it("renders every underlying video file", () => {
    renderWithQueryClient(
      <FileInfoTab
        files={[
          videoFile,
          {
            ...videoFile,
            id: 11,
            basename: "beta.mp4",
            path: "D:\\archive\\beta.mp4",
          },
        ] as any}
      />
    );

    expect(screen.getByText("C:\\library\\alpha.mp4")).toBeInTheDocument();
    expect(screen.getByText("D:\\archive\\beta.mp4")).toBeInTheDocument();
    expect(screen.getByText("File 1 of 2")).toBeInTheDocument();
    expect(screen.getByText("File 2 of 2")).toBeInTheDocument();
  });
});

describe("DetailsTab performers", () => {
  it("shows performer age at video date and uses a paired grid for multiple performers", () => {
    const video = {
      ...baseVideo,
      date: "2024-01-12",
      remoteIds: [],
      urls: [],
      customFields: undefined,
      performers: [
        { id: 7, name: "Alice Example", birthdate: "2000-01-10", imagePath: null },
        { id: 8, name: "Beth Example", birthdate: "1998-03-01", imagePath: null },
      ],
    };

    renderWithQueryClient(<DetailsTab video={video as any} onNavigate={vi.fn()} />);

    expect(screen.getByText("24 yrs old")).toBeInTheDocument();

    const performerGrid = screen.getByText("Performers").nextElementSibling as HTMLElement;
    expect(performerGrid.className).toContain("grid");
    expect(performerGrid.className).toContain("grid-cols-2");

    expect(screen.getByRole("link", { name: /Alice Example/i }).className).toContain("absolute inset-0");
    expect(screen.getByRole("link", { name: /Beth Example/i }).className).toContain("absolute inset-0");
  });

});

describe("DetailsTab tag hover", () => {
  it("renders supplied static media without fetching tag details", () => {
    const fetchMock = vi.fn<typeof fetch>(async () => new Response(JSON.stringify([]), { status: 200, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    const video = {
      ...baseVideo,
      remoteIds: [], urls: [], customFields: undefined,
      tags: [{ id: 9, name: "Featured", imagePath: "/tag.jpg", description: "Preview description", favorite: false, organized: true, aliases: [], videoCount: 12 }],
    };
    renderWithQueryClient(<DetailsTab video={video as any} onNavigate={vi.fn()} />);

    fireEvent.mouseEnter(screen.getByRole("button", { name: "Featured" }));

    expect(screen.getByRole("tooltip", { name: "Media for Featured" })).toContainElement(screen.getByRole("img", { name: "Featured" }));
    expect(screen.getByRole("img", { name: "Featured" })).toHaveAttribute("src", "/tag.jpg");
    expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith("/api/tags/"))).toBe(false);
  });
});
