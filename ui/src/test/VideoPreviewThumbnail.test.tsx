import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoPreviewThumbnail } from "../components/VideoPreviewThumbnail";
import type { VideoSprites, VideoSpritesLoad } from "../hooks/useVideoSprites";

const mocks = vi.hoisted(() => ({
  videoCoverUrl: vi.fn(),
  previewUrl: vi.fn(),
  screenshotUrl: vi.fn(),
  useVideoSpritesLoad: vi.fn((_videoId: number | null): VideoSpritesLoad => ({ status: "idle", sprites: null })),
}));

vi.mock("../hooks/useVideoSprites", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../hooks/useVideoSprites")>()),
  useVideoSpritesLoad: (videoId: number | null) => mocks.useVideoSpritesLoad(videoId),
}));

type SpritesOutcome = VideoSprites | "loading" | "missing" | "failed";

/** What the sprite hook reports for video 42 once it is asked for: its sprite list, or how loading it went. */
function mockSprites(outcome: SpritesOutcome) {
  mocks.useVideoSpritesLoad.mockImplementation((videoId): VideoSpritesLoad => {
    if (videoId !== 42) return { status: "idle", sprites: null };
    if (typeof outcome === "string") return { status: outcome, sprites: null };
    return { status: "ready", sprites: outcome };
  });
}

vi.mock("../api/client", () => ({
  entityImages: { videoCoverUrl: mocks.videoCoverUrl },
  videos: {
    previewUrl: mocks.previewUrl,
    screenshotUrl: mocks.screenshotUrl,
  },
}));

const video = {
  id: 42,
  title: "Sample video",
  imagePath: "/covers/sample.jpg",
  updatedAt: "2026-08-07T00:00:00Z",
  clipStartSec: 35,
  clipEndSec: 95,
  files: [{ duration: 120, basename: "sample.mp4", path: "/library/sample.mp4" }],
  performers: [],
  tags: [],
  groups: [],
  galleries: [],
  urls: [],
  remoteIds: [],
} as any;

let observerCallback: IntersectionObserverCallback;
const observe = vi.fn();
const disconnect = vi.fn();

describe("VideoPreviewThumbnail", () => {
  beforeEach(() => {
    mocks.videoCoverUrl.mockReset().mockReturnValue("/cover/42");
    mocks.previewUrl.mockReset().mockReturnValue("/preview/42");
    mocks.screenshotUrl.mockReset().mockImplementation((_id, _updatedAt, seconds) => `/screenshot/42/${seconds}`);
    mocks.useVideoSpritesLoad.mockReset();
    mockSprites("loading");
    observe.mockReset();
    disconnect.mockReset();
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        constructor(callback: IntersectionObserverCallback) {
          observerCallback = callback;
        }
        observe = observe;
        disconnect = disconnect;
        unobserve() {}
      },
    );
    vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue();
    vi.spyOn(HTMLMediaElement.prototype, "pause").mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it.each(["cover", "contain"] as const)("uses Cove's cover and generated-preview endpoints with %s fit", (fit) => {
    const { container } = render(<VideoPreviewThumbnail video={video} fit={fit} surface="list" coverWidth={640} />);

    expect(mocks.videoCoverUrl).toHaveBeenCalledWith(42, video.updatedAt, 640);
    expect(mocks.previewUrl).toHaveBeenCalledWith(42);
    expect(container.querySelector(".video-card-preview-image")).toHaveAttribute("src", "/cover/42");
    expect(container.querySelector(".video-card-preview-image")).toHaveStyle({ objectFit: fit });
    expect(container.querySelector(".video-card-preview-video")).toHaveAttribute("src", "/preview/42");
    expect(container.querySelector(".video-card-preview-video")).toHaveStyle({ objectFit: fit });
  });

  it("plays the generated preview while visible and pauses it when hidden", () => {
    const play = vi.mocked(HTMLMediaElement.prototype.play);
    const pause = vi.mocked(HTMLMediaElement.prototype.pause);
    const { container, unmount } = render(<VideoPreviewThumbnail video={video} fit="cover" />);
    const preview = container.querySelector("video");

    expect(observe).toHaveBeenCalledWith(preview);

    act(() => observerCallback([{ intersectionRatio: 1 } as IntersectionObserverEntry], {} as IntersectionObserver));
    expect(play).toHaveBeenCalledOnce();

    act(() => observerCallback([{ intersectionRatio: 0 } as IntersectionObserverEntry], {} as IntersectionObserver));
    expect(pause).toHaveBeenCalledOnce();

    unmount();
    expect(disconnect).toHaveBeenCalledOnce();
  });

  it("shows a clip-aware absolute timestamp, screenshot, and progress while scrubbing", () => {
    // One tile per second, so the shown time is the hovered second.
    mockSprites({
      entries: Array.from({ length: 120 }, (_, i) => ({ start: i, end: i + 1, x: 0, y: 0, w: 160, h: 90 })),
      imageUrl: "/api/stream/video/42/sprite",
      sheetWidth: 160,
      sheetHeight: 90,
      capturedMidTile: false,
    });
    const { container } = render(<VideoPreviewThumbnail video={video} fit="cover" />);
    const scrubZone = container.querySelector(".cursor-ew-resize") as HTMLDivElement;
    vi.spyOn(scrubZone, "getBoundingClientRect").mockReturnValue({
      left: 0,
      width: 100,
    } as DOMRect);

    fireEvent.mouseEnter(scrubZone, { clientX: 50 });
    fireEvent.mouseMove(scrubZone, { clientX: 50 });

    expect(mocks.screenshotUrl).toHaveBeenCalledWith(42, video.updatedAt, 65);
    expect(screen.getByText("1:05")).toBeInTheDocument();
    expect(container.querySelector('img[src="/screenshot/42/65"]')).toBeInTheDocument();
    expect(scrubZone.querySelector(".bg-accent")).toHaveStyle({ width: "50%" });

    fireEvent.mouseLeave(scrubZone);
    expect(screen.queryByText("1:05")).not.toBeInTheDocument();
    expect(container.querySelector('img[src="/screenshot/42/65"]')).not.toBeInTheDocument();
  });

  // Tiles every 25 seconds over the 120 second source video; the clip covers 35-95 seconds.
  const tiledSprites = {
    entries: [0, 25, 50, 75, 100].map((start, i) => ({ start, end: start + 25, x: i * 160, y: 0, w: 160, h: 90 })),
    imageUrl: "/api/stream/video/42/sprite",
    sheetWidth: 800,
    sheetHeight: 90,
    capturedMidTile: false,
  };

  function renderScrubber(onScrubClick = vi.fn(), sprites: SpritesOutcome = tiledSprites, scrubVideo = video) {
    mockSprites(sprites);
    const { container, rerender } = render(
      <VideoPreviewThumbnail video={scrubVideo} fit="cover" onScrubClick={onScrubClick} />,
    );
    const scrubZone = container.querySelector(".cursor-ew-resize") as HTMLDivElement;
    vi.spyOn(scrubZone, "getBoundingClientRect").mockReturnValue({ left: 0, width: 100 } as DOMRect);
    const rerenderScrubber = () =>
      rerender(<VideoPreviewThumbnail video={scrubVideo} fit="cover" onScrubClick={onScrubClick} />);
    return { container, scrubZone, onScrubClick, rerenderScrubber };
  }

  it("loads the sprite list only once the strip is first hovered", () => {
    const { scrubZone } = renderScrubber();
    expect(mocks.useVideoSpritesLoad).not.toHaveBeenCalledWith(42);

    fireEvent.mouseEnter(scrubZone, { clientX: 10 });

    expect(mocks.useVideoSpritesLoad).toHaveBeenLastCalledWith(42);
  });

  it("snaps the preview and the click to the start of a Stash-captured tile", () => {
    const { container, scrubZone, onScrubClick } = renderScrubber();
    fireEvent.mouseEnter(scrubZone, { clientX: 10 });

    // 50% along the clip is 65 seconds, inside the tile captured at 50 seconds.
    fireEvent.mouseMove(scrubZone, { clientX: 50 });
    expect(screen.getByText("0:50")).toBeInTheDocument();
    expect(container.querySelector('img[src="/screenshot/42/50"]')).toBeInTheDocument();

    fireEvent.click(scrubZone, { clientX: 50 });
    expect(onScrubClick).toHaveBeenCalledWith(50);
  });

  it("snaps to the middle of tiles that Cove captured mid-interval", () => {
    const { scrubZone, onScrubClick } = renderScrubber(vi.fn(), { ...tiledSprites, capturedMidTile: true });
    fireEvent.mouseEnter(scrubZone, { clientX: 10 });

    // 65 seconds is in the 50-75 second tile, which Cove captured at 62.5 seconds.
    fireEvent.click(scrubZone, { clientX: 50 });

    expect(onScrubClick).toHaveBeenCalledWith(62.5);
  });

  it("keeps a mid-tile time short of the end of a clip that ends inside the tile", () => {
    const shortClip = { ...video, clipEndSec: 80 };
    const { scrubZone, onScrubClick } = renderScrubber(vi.fn(), { ...tiledSprites, capturedMidTile: true }, shortClip);
    fireEvent.mouseEnter(scrubZone, { clientX: 10 });

    // The 75-100 second tile was captured at 87.5 seconds, after the clip ends at 80.
    fireEvent.click(scrubZone, { clientX: 99 });

    expect(onScrubClick).toHaveBeenCalledWith(79);
  });

  it("keeps a snapped time inside the clip when its tile starts before the clip", () => {
    const { scrubZone, onScrubClick } = renderScrubber();
    fireEvent.mouseEnter(scrubZone, { clientX: 10 });

    // 5% along the clip is 38 seconds, in the tile from 25 seconds, before the clip starts at 35.
    fireEvent.click(scrubZone, { clientX: 5 });

    expect(onScrubClick).toHaveBeenCalledWith(35);
  });

  it("updates the shown time when the sprite list arrives while the pointer rests", () => {
    const { container, scrubZone, onScrubClick, rerenderScrubber } = renderScrubber(vi.fn(), "loading");
    fireEvent.mouseEnter(scrubZone, { clientX: 50 });
    expect(screen.getByText("1:05")).toBeInTheDocument();
    // No frame is shown until the sprite list confirms there is one to show.
    expect(container.querySelector("img.z-\\[7\\]")).not.toBeInTheDocument();

    mockSprites(tiledSprites);
    rerenderScrubber();

    expect(screen.getByText("0:50")).toBeInTheDocument();
    expect(container.querySelector('img[src="/screenshot/42/50"]')).toBeInTheDocument();
    fireEvent.click(scrubZone, { clientX: 50 });
    expect(onScrubClick).toHaveBeenCalledWith(50);
  });

  it("uses the hovered time when the sprite list's cue times do not cover the video", () => {
    // Some Stash sprite lists have every cue at 0:00, or end long before the video does.
    const zeroCues = {
      ...tiledSprites,
      entries: tiledSprites.entries.map((entry) => ({ ...entry, start: 0, end: 0 })),
    };
    const { scrubZone, onScrubClick } = renderScrubber(vi.fn(), zeroCues);
    fireEvent.mouseEnter(scrubZone, { clientX: 50 });
    expect(screen.getByText("1:05")).toBeInTheDocument();
    fireEvent.click(scrubZone, { clientX: 50 });
    expect(onScrubClick).toHaveBeenLastCalledWith(65);
  });

  it("uses the hovered time past the end of a sprite list that stops early", () => {
    const earlyEnd = { ...tiledSprites, entries: tiledSprites.entries.slice(0, 2) };
    const { scrubZone, onScrubClick } = renderScrubber(vi.fn(), earlyEnd);
    fireEvent.mouseEnter(scrubZone, { clientX: 10 });

    // 75% along the clip is 80 seconds, long after the last tile ends at 50 seconds.
    fireEvent.click(scrubZone, { clientX: 75 });

    expect(onScrubClick).toHaveBeenCalledWith(80);
  });

  it("uses the hovered time for a click before the sprite list arrives", () => {
    const { scrubZone, onScrubClick } = renderScrubber(vi.fn(), "loading");

    fireEvent.mouseEnter(scrubZone, { clientX: 50 });
    fireEvent.click(scrubZone, { clientX: 50 });

    expect(onScrubClick).toHaveBeenCalledWith(65);
  });

  it("drops the scrub surface once the video turns out to have no sprites", () => {
    const { container, scrubZone, rerenderScrubber } = renderScrubber(vi.fn(), "loading");
    fireEvent.mouseEnter(scrubZone, { clientX: 50 });

    mockSprites("missing");
    rerenderScrubber();

    expect(container.querySelector(".cursor-ew-resize")).not.toBeInTheDocument();
    expect(container.querySelector("img.z-\\[7\\]")).not.toBeInTheDocument();
    expect(screen.queryByText("1:05")).not.toBeInTheDocument();
    expect(mocks.screenshotUrl).not.toHaveBeenCalled();
  });

  it("lets a click on a card without sprites open the card", () => {
    mockSprites("missing");
    const onClick = vi.fn((event: { preventDefault(): void }) => event.preventDefault());
    const { container } = render(
      <a href="/video/42" onClick={onClick}>
        <VideoPreviewThumbnail video={video} fit="cover" onScrubClick={vi.fn()} />
      </a>,
    );
    fireEvent.mouseEnter(container.querySelector(".cursor-ew-resize")!, { clientX: 50 });

    expect(container.querySelector(".cursor-ew-resize")).not.toBeInTheDocument();
    fireEvent.click(container.querySelector(".video-card-preview")!);
    expect(onClick).toHaveBeenCalledOnce();
  });

  it("keeps scrubbing with frames from the screenshot endpoint when the sprite list could not be fetched", () => {
    const { container, scrubZone, onScrubClick } = renderScrubber(vi.fn(), "failed");
    fireEvent.mouseEnter(scrubZone, { clientX: 50 });

    expect(container.querySelector(".cursor-ew-resize")).toBeInTheDocument();
    expect(screen.getByText("1:05")).toBeInTheDocument();
    expect(container.querySelector('img[src="/screenshot/42/65"]')).toBeInTheDocument();
    fireEvent.click(scrubZone, { clientX: 50 });
    expect(onScrubClick).toHaveBeenCalledWith(65);
  });

  it("stops showing frames once one fails to load", () => {
    const { container, scrubZone } = renderScrubber();
    fireEvent.mouseEnter(scrubZone, { clientX: 50 });
    fireEvent.error(container.querySelector('img[src="/screenshot/42/50"]')!);

    fireEvent.mouseMove(scrubZone, { clientX: 90 });

    expect(container.querySelector("img.z-\\[7\\]")).not.toBeInTheDocument();
    expect(screen.getByText("1:15")).toBeInTheDocument();
  });

  it("forgets the hover when scrubbing is turned off under the pointer", () => {
    mockSprites(tiledSprites);
    const { container, rerender } = render(<VideoPreviewThumbnail video={video} fit="cover" />);
    const scrubZone = container.querySelector(".cursor-ew-resize") as HTMLDivElement;
    vi.spyOn(scrubZone, "getBoundingClientRect").mockReturnValue({ left: 0, width: 100 } as DOMRect);
    fireEvent.mouseEnter(scrubZone, { clientX: 50 });
    expect(container.querySelector('img[src="/screenshot/42/50"]')).toBeInTheDocument();

    rerender(<VideoPreviewThumbnail video={video} fit="cover" enableScrubbing={false} />);
    expect(container.querySelector("img.z-\\[7\\]")).not.toBeInTheDocument();

    rerender(<VideoPreviewThumbnail video={video} fit="cover" />);
    expect(container.querySelector("img.z-\\[7\\]")).not.toBeInTheDocument();
    expect(screen.queryByText("0:50")).not.toBeInTheDocument();
  });

  it("keeps scrub clicks from activating the surrounding navigation", () => {
    const onClick = vi.fn();
    const { container } = render(
      <a href="/video/42" onClick={onClick}>
        <VideoPreviewThumbnail video={video} fit="cover" />
      </a>,
    );

    fireEvent.click(container.querySelector(".cursor-ew-resize")!);
    expect(onClick).not.toHaveBeenCalled();
  });

  it("leaves out the scrub surface on devices without precise hover", () => {
    const matchMedia = vi.fn(() => ({ matches: false }));
    vi.stubGlobal("matchMedia", matchMedia);

    const { container } = render(<VideoPreviewThumbnail video={video} fit="cover" />);

    expect(matchMedia).toHaveBeenCalledWith("(hover: hover) and (pointer: fine)");
    expect(container.querySelector(".cursor-ew-resize")).not.toBeInTheDocument();
  });

  it("can disable the scrub surface for selection mode", () => {
    const { container } = render(<VideoPreviewThumbnail video={video} fit="cover" enableScrubbing={false} />);

    expect(container.querySelector(".cursor-ew-resize")).not.toBeInTheDocument();
  });

  it("shows the standard video fallback when the cover cannot load", () => {
    const { container } = render(<VideoPreviewThumbnail video={video} fit="cover" />);

    fireEvent.error(container.querySelector(".video-card-preview-image")!);

    expect(container.querySelector(".video-card-cover-fallback")).toBeVisible();
    expect(container.querySelector(".video-card-preview-image")).not.toBeInTheDocument();
  });
});
