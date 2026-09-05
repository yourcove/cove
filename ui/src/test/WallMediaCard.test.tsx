import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WallMediaCard } from "../components/WallMediaCard";

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("WallMediaCard", () => {
  it("keeps the still image when the preview status request fails", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: false }));

    const { container } = render(
      <WallMediaCard
        title="Missing preview"
        imageSrc="/image.jpg"
        videoSrc="/missing.mp4"
        videoStatusSrc="/missing.mp4/status"
        useVideo
      />,
    );

    expect(screen.getByAltText("Missing preview")).toBeInTheDocument();
    await waitFor(() =>
      expect(fetch).toHaveBeenCalledWith("/missing.mp4/status", expect.objectContaining({ method: "GET" })),
    );
    expect(container.querySelector("video")).not.toBeInTheDocument();
  });

  it("does not mount a preview video when status reports unavailable", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({ available: false }) }));

    const { container } = render(
      <WallMediaCard
        title="Unavailable preview"
        imageSrc="/image.jpg"
        videoSrc="/preview.mp4"
        videoStatusSrc="/preview.mp4/status"
        useVideo
      />,
    );

    expect(screen.getByAltText("Unavailable preview")).toBeInTheDocument();
    await waitFor(() =>
      expect(fetch).toHaveBeenCalledWith("/preview.mp4/status", expect.objectContaining({ method: "GET" })),
    );
    expect(container.querySelector("video")).not.toBeInTheDocument();
  });

  it("uses the configured image source directly", () => {
    const { rerender } = render(<WallMediaCard title="Card image" imageSrc="/cover.jpg" />);

    const image = screen.getByAltText("Card image");
    expect(image).toHaveAttribute("src", "/cover.jpg");

    rerender(<WallMediaCard title="Card image" imageSrc="/next-cover.jpg" />);

    expect(screen.getByAltText("Card image")).toHaveAttribute("src", "/next-cover.jpg");
  });

  it("mounts the preview video after the preview exists", async () => {
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        private readonly callback: IntersectionObserverCallback;

        constructor(callback: IntersectionObserverCallback) {
          this.callback = callback;
        }

        observe(target: Element) {
          this.callback(
            [{ isIntersecting: true, intersectionRatio: 1, target } as IntersectionObserverEntry],
            this as unknown as IntersectionObserver,
          );
        }

        disconnect() {}
      },
    );

    const { container } = render(
      <WallMediaCard title="Available preview" imageSrc="/image.jpg" videoSrc="/preview.mp4" useVideo />,
    );

    await waitFor(() => expect(container.querySelector("video")).toBeInTheDocument());
    expect(container.querySelector("video")).toHaveAttribute("src", "/preview.mp4");
  });

  it("mounts video after a successful preview status response", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({ available: true }) }));
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        private readonly callback: IntersectionObserverCallback;

        constructor(callback: IntersectionObserverCallback) {
          this.callback = callback;
        }

        observe(target: Element) {
          this.callback(
            [{ isIntersecting: true, intersectionRatio: 1, target } as IntersectionObserverEntry],
            this as unknown as IntersectionObserver,
          );
        }

        disconnect() {}
      },
    );

    const { container } = render(
      <WallMediaCard
        title="Status preview"
        imageSrc="/image.jpg"
        videoSrc="/preview.mp4"
        videoStatusSrc="/preview.mp4/status"
        useVideo
      />,
    );

    await waitFor(() =>
      expect(fetch).toHaveBeenCalledWith("/preview.mp4/status", expect.objectContaining({ method: "GET" })),
    );
    await waitFor(() => expect(container.querySelector("video")).toBeInTheDocument());
  });

  it("observes and plays the video after async status mounts it", async () => {
    const play = vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);

    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({ available: true }) }));
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        private readonly callback: IntersectionObserverCallback;

        constructor(callback: IntersectionObserverCallback) {
          this.callback = callback;
        }

        observe(target: Element) {
          this.callback(
            [{ isIntersecting: true, intersectionRatio: 1, target } as IntersectionObserverEntry],
            this as unknown as IntersectionObserver,
          );
        }

        disconnect() {}
      },
    );

    const { container } = render(
      <WallMediaCard
        title="Async preview"
        imageSrc="/image.jpg"
        videoSrc="/preview.mp4"
        videoStatusSrc="/preview.mp4/status"
        useVideo
      />,
    );

    await waitFor(() => expect(container.querySelector("video")).toBeInTheDocument());
    await waitFor(() => expect(play).toHaveBeenCalled());
  });

  it("renders custom video controls after a feed video is available", async () => {
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        private readonly callback: IntersectionObserverCallback;

        constructor(callback: IntersectionObserverCallback) {
          this.callback = callback;
        }

        observe(target: Element) {
          this.callback(
            [{ isIntersecting: true, intersectionRatio: 1, target } as IntersectionObserverEntry],
            this as unknown as IntersectionObserver,
          );
        }

        disconnect() {}
      },
    );

    render(
      <WallMediaCard
        title="Available preview"
        imageSrc="/image.jpg"
        videoSrc="/preview.mp4"
        useVideo
        videoControls={() => <div>Custom feed controls</div>}
      />,
    );

    await waitFor(() => expect(screen.getByText("Custom feed controls")).toBeInTheDocument());
  });

  it("updates custom video controls with playback state", async () => {
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        private readonly callback: IntersectionObserverCallback;

        constructor(callback: IntersectionObserverCallback) {
          this.callback = callback;
        }

        observe(target: Element) {
          this.callback(
            [{ isIntersecting: true, intersectionRatio: 1, target } as IntersectionObserverEntry],
            this as unknown as IntersectionObserver,
          );
        }

        disconnect() {}
      },
    );

    const { container } = render(
      <WallMediaCard
        title="Available preview"
        imageSrc="/image.jpg"
        videoSrc="/preview.mp4"
        useVideo
        videoControls={(controls) => <div>{controls.isPlaying ? "Preview playing" : "Preview paused"}</div>}
      />,
    );

    await waitFor(() => expect(container.querySelector("video")).toBeInTheDocument());
    const video = container.querySelector("video")!;
    expect(screen.getByText("Preview paused")).toBeInTheDocument();

    fireEvent.play(video);
    expect(screen.getByText("Preview playing")).toBeInTheDocument();

    fireEvent.pause(video);
    expect(screen.getByText("Preview paused")).toBeInTheDocument();
  });

  it("restarts bounded playback at the configured start when it reaches the end", async () => {
    vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        private readonly callback: IntersectionObserverCallback;

        constructor(callback: IntersectionObserverCallback) {
          this.callback = callback;
        }

        observe(target: Element) {
          this.callback(
            [{ isIntersecting: true, intersectionRatio: 1, target } as IntersectionObserverEntry],
            this as unknown as IntersectionObserver,
          );
        }

        disconnect() {}
      },
    );

    const { container } = render(
      <WallMediaCard
        title="Bounded video"
        imageSrc="/image.jpg"
        videoSrc="/video.mp4"
        useVideo
        videoStartTimeSec={12}
        videoEndTimeSec={20}
      />,
    );

    await waitFor(() => expect(container.querySelector("video")).toBeInTheDocument());
    const video = container.querySelector("video")!;
    Object.defineProperty(video, "duration", { configurable: true, value: 60 });
    video.currentTime = 20.25;

    fireEvent.timeUpdate(video);

    expect(video.currentTime).toBe(12);
  });

  it("holds a zero-length range on its configured frame", async () => {
    vi.spyOn(HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    const pause = vi.spyOn(HTMLMediaElement.prototype, "pause").mockImplementation(() => {});
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        private readonly callback: IntersectionObserverCallback;

        constructor(callback: IntersectionObserverCallback) {
          this.callback = callback;
        }

        observe(target: Element) {
          this.callback(
            [{ isIntersecting: true, intersectionRatio: 1, target } as IntersectionObserverEntry],
            this as unknown as IntersectionObserver,
          );
        }

        disconnect() {}
      },
    );

    const { container } = render(
      <WallMediaCard
        title="Still range"
        imageSrc="/image.jpg"
        videoSrc="/video.mp4"
        useVideo
        videoStartTimeSec={12}
        videoEndTimeSec={12}
      />,
    );

    await waitFor(() => expect(container.querySelector("video")).toBeInTheDocument());
    const video = container.querySelector("video")!;
    Object.defineProperty(video, "duration", { configurable: true, value: 60 });
    video.currentTime = 12;
    pause.mockClear();

    fireEvent.timeUpdate(video);

    expect(video.currentTime).toBe(12);
    expect(pause).toHaveBeenCalledOnce();
  });
});
