import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoPreviewThumbnail } from "../components/VideoPreviewThumbnail";

const mocks = vi.hoisted(() => ({
  videoCoverUrl: vi.fn(),
  previewUrl: vi.fn(),
  screenshotUrl: vi.fn(),
}));

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
    observe.mockReset();
    disconnect.mockReset();
    vi.stubGlobal("IntersectionObserver", class {
      constructor(callback: IntersectionObserverCallback) {
        observerCallback = callback;
      }
      observe = observe;
      disconnect = disconnect;
      unobserve() {}
    });
    vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue();
    vi.spyOn(HTMLMediaElement.prototype, "pause").mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it.each(["cover", "contain"] as const)("uses Cove's cover and generated-preview endpoints with %s fit", (fit) => {
    const { container } = render(
      <VideoPreviewThumbnail video={video} fit={fit} surface="list" coverWidth={640} />,
    );

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
    const { container } = render(<VideoPreviewThumbnail video={video} fit="cover" />);
    const scrubZone = container.querySelector(".cursor-ew-resize") as HTMLDivElement;
    vi.spyOn(scrubZone, "getBoundingClientRect").mockReturnValue({
      left: 0,
      width: 100,
    } as DOMRect);

    fireEvent.mouseMove(scrubZone, { clientX: 50 });

    expect(mocks.screenshotUrl).toHaveBeenCalledWith(42, video.updatedAt, 65);
    expect(screen.getByText("1:05")).toBeInTheDocument();
    expect(container.querySelector('img[src="/screenshot/42/65"]')).toBeInTheDocument();
    expect(scrubZone.querySelector(".bg-accent")).toHaveStyle({ width: "50%" });

    fireEvent.mouseLeave(scrubZone);
    expect(screen.queryByText("1:05")).not.toBeInTheDocument();
    expect(container.querySelector('img[src="/screenshot/42/65"]')).not.toBeInTheDocument();
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

  it("can disable the scrub surface for selection mode", () => {
    const { container } = render(
      <VideoPreviewThumbnail video={video} fit="cover" enableScrubbing={false} />,
    );

    expect(container.querySelector(".cursor-ew-resize")).not.toBeInTheDocument();
  });

  it("shows the standard video fallback when the cover cannot load", () => {
    const { container } = render(<VideoPreviewThumbnail video={video} fit="cover" />);

    fireEvent.error(container.querySelector(".video-card-preview-image")!);

    expect(container.querySelector(".video-card-cover-fallback")).toBeVisible();
    expect(container.querySelector(".video-card-preview-image")).not.toBeInTheDocument();
  });
});
