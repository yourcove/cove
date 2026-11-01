import { act, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    engagement: { hostId: 1, likeCount: 0, rating: null },
    rating: null,
    setRating: vi.fn(),
    ratingPending: false,
  }),
}));

vi.mock("../api/client", () => ({
  images: { incrementLike: vi.fn().mockResolvedValue(0) },
  playback: { recordIntervals: vi.fn().mockResolvedValue(undefined) },
}));

vi.mock("../utils/interactionTracking", () => ({
  createPlaybackSessionId: () => "session",
  trackInteraction: vi.fn(),
}));

import { Lightbox, type LightboxProps } from "../components/Lightbox";

const images = [
  { id: 1, src: "/one.jpg", title: "one.jpg" },
  { id: 2, src: "/two.jpg", title: "two.jpg" },
  { id: 3, src: "/three.jpg", title: "three.jpg" },
];

function renderLightbox(props: Partial<LightboxProps> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const baseProps: LightboxProps = { images, initialIndex: 0, open: true, onClose: vi.fn(), ...props };
  const view = render(
    <QueryClientProvider client={queryClient}>
      <Lightbox {...baseProps} />
    </QueryClientProvider>,
  );
  return {
    rerender: (next: Partial<LightboxProps>) =>
      view.rerender(
        <QueryClientProvider client={queryClient}>
          <Lightbox {...baseProps} {...next} />
        </QueryClientProvider>,
      ),
  };
}

describe("Lightbox open and index sync", () => {
  it("starts the slideshow when mounted open with autoplay", () => {
    renderLightbox({ autoPlay: true });

    expect(screen.getByRole("button", { name: "Pause slideshow" })).toBeInTheDocument();
  });

  it("follows initialIndex changes and restarts playback from autoplay when reopened", () => {
    const { rerender } = renderLightbox({ autoPlay: true });
    expect(screen.getByText("1 / 3")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Pause slideshow" }));
    expect(screen.getByRole("button", { name: "Play slideshow" })).toBeInTheDocument();

    rerender({ autoPlay: true, initialIndex: 2 });
    expect(screen.getByText("3 / 3")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Pause slideshow" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Pause slideshow" }));
    rerender({ autoPlay: true, initialIndex: 2, open: false });
    expect(screen.queryByText("3 / 3")).not.toBeInTheDocument();

    rerender({ autoPlay: true, initialIndex: 1, open: true });
    expect(screen.getByText("2 / 3")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Pause slideshow" })).toBeInTheDocument();
  });

  it("applies a changed slideshow delay while open", () => {
    const { rerender } = renderLightbox({ autoPlay: true, slideshowDelay: 5000 });
    fireEvent.click(screen.getByRole("button", { name: "Lightbox settings" }));
    expect(screen.getByText("5s")).toBeInTheDocument();

    rerender({ autoPlay: true, slideshowDelay: 8000 });
    expect(screen.getByText("8s")).toBeInTheDocument();
  });
});

describe("Lightbox settings", () => {
  const settingsButton = () => screen.getByRole("button", { name: "Lightbox settings" });
  const settingsPanel = () => screen.queryByRole("group", { name: "Lightbox settings" });
  const openSettings = () => fireEvent.click(settingsButton());

  it("keeps the slideshow delay in a settings panel rather than the toolbar", () => {
    renderLightbox({ slideshowDelay: 5000 });
    expect(screen.queryByRole("button", { name: "Increase slideshow delay" })).not.toBeInTheDocument();

    openSettings();
    expect(settingsButton()).toHaveAttribute("aria-expanded", "true");
    fireEvent.click(screen.getByRole("button", { name: "Increase slideshow delay" }));
    expect(settingsPanel()).toHaveTextContent("6s");
  });

  it("closes the panel on Escape before closing the lightbox, returning focus to its button", () => {
    const onClose = vi.fn();
    renderLightbox({ onClose });
    openSettings();
    screen.getByRole("button", { name: "Increase slideshow delay" }).focus();

    fireEvent.keyDown(window, { key: "Escape" });
    expect(settingsPanel()).not.toBeInTheDocument();
    expect(settingsButton()).toHaveFocus();
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.keyDown(window, { key: "Escape" });
    expect(onClose).toHaveBeenCalled();
  });

  it("names the panel it controls while open", () => {
    renderLightbox();
    openSettings();

    expect(settingsButton()).toHaveAttribute("aria-controls", settingsPanel()!.id);
  });

  it("dismisses the panel with a tap anywhere else, without acting on what lies underneath", () => {
    const onClose = vi.fn();
    renderLightbox({ onClose });
    openSettings();

    // The dismiss layer sits over the image and bottom controls (z-20) and under the raised top bar.
    const dismissLayer = document.querySelector<HTMLElement>("[data-lightbox-settings-dismiss]")!;
    expect(dismissLayer).toHaveClass("absolute", "inset-0", "z-30");
    // A sibling of the image stage in the viewer root, so it covers the whole viewer rather than one part.
    expect(dismissLayer.parentElement).toBe(screen.getByRole("img", { name: "one.jpg" }).parentElement!.parentElement);
    expect(settingsButton().closest(".absolute")).toHaveClass("z-40");

    fireEvent.click(dismissLayer);
    expect(settingsPanel()).not.toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByText("1 / 3")).toBeInTheDocument();
    expect(document.querySelector("[data-lightbox-settings-dismiss]")).toBeNull();
    expect(settingsButton().closest(".absolute")).toHaveClass("z-10");
  });

  it("closes the panel with a second press of its button", () => {
    renderLightbox();
    openSettings();

    openSettings();
    expect(settingsPanel()).not.toBeInTheDocument();
    expect(document.querySelector("[data-lightbox-settings-dismiss]")).toBeNull();
  });

  it("starts closed when the lightbox is reopened", () => {
    const { rerender } = renderLightbox();
    openSettings();

    rerender({ open: false });
    rerender({ open: true });
    expect(settingsPanel()).not.toBeInTheDocument();
  });

  it("leaves no dismiss layer behind when the queue shrinks to one image", () => {
    const { rerender } = renderLightbox({ initialIndex: 1 });
    openSettings();

    // A new start index replaces the queue with the images passed in.
    rerender({ images: [images[0]], initialIndex: 0 });
    expect(screen.queryByRole("button", { name: "Lightbox settings" })).not.toBeInTheDocument();
    expect(document.querySelector("[data-lightbox-settings-dismiss]")).toBeNull();
  });
});

describe("Lightbox full screen", () => {
  function setFullscreenEnabled(enabled: boolean | undefined) {
    Object.defineProperty(document, "fullscreenEnabled", { configurable: true, value: enabled });
  }

  afterEach(() => setFullscreenEnabled(undefined));

  it("offers full screen where the browser allows it for the viewer", () => {
    setFullscreenEnabled(true);
    renderLightbox();

    expect(screen.getByRole("button", { name: "Enter full screen" })).toBeInTheDocument();
  });

  it("hides full screen where only video can go full screen, as on iPhone", () => {
    setFullscreenEnabled(false);
    renderLightbox();

    expect(screen.queryByRole("button", { name: "Enter full screen" })).not.toBeInTheDocument();
  });
});

describe("Lightbox swipe", () => {
  const WIDTH = 375;
  const GAP = 24;
  const settle = () => act(() => new Promise((resolve) => setTimeout(resolve, 250)));

  function stageOf(image: HTMLElement) {
    const stage = image.parentElement!;
    Object.defineProperty(stage, "clientWidth", { configurable: true, value: WIDTH });
    return stage;
  }

  async function showFirstImage() {
    const image = screen.getByRole("img", { name: "one.jpg" });
    await act(async () => {
      fireEvent.load(image);
    });
    return { image, stage: stageOf(image) };
  }

  const at = (x: number, y = 300, pointerType = "touch") => ({
    pointerId: 1,
    pointerType,
    isPrimary: true,
    clientX: x,
    clientY: y,
  });

  function swipe(target: Element, dx: number, dy = 0, pointerType = "touch") {
    fireEvent.pointerDown(target, at(200, 300, pointerType));
    fireEvent.pointerMove(target, at(200 + dx / 2, 300 + dy / 2, pointerType));
    fireEvent.pointerMove(target, at(200 + dx, 300 + dy, pointerType));
    fireEvent.pointerUp(target, at(200 + dx, 300 + dy, pointerType));
    fireEvent.click(target, at(200 + dx, 300 + dy, pointerType));
  }

  const incoming = () => document.querySelector<HTMLImageElement>("img[data-lightbox-incoming]");

  it("pulls the next image in from the right edge as the finger drags left", async () => {
    renderLightbox();
    const { image, stage } = await showFirstImage();

    fireEvent.pointerDown(stage, at(300));
    fireEvent.pointerMove(stage, at(200));

    expect(image).toHaveStyle({ transform: "scale(1) translate(-100px, 0px)", transition: "none" });
    expect(incoming()).toHaveAttribute("src", "/two.jpg");
    expect(incoming()).toHaveStyle({ transform: `scale(1) translate(${-100 + WIDTH + GAP}px, 0px)` });

    // Reversing direction brings the previous image in from the left instead.
    fireEvent.pointerMove(stage, at(340));
    expect(incoming()).toHaveAttribute("src", "/three.jpg");
    expect(incoming()).toHaveStyle({ transform: `scale(1) translate(${40 - WIDTH - GAP}px, 0px)` });
  });

  it("keeps the incoming image's element as the new image, so a loaded one shows at once", async () => {
    const onClose = vi.fn();
    renderLightbox({ onClose });
    const { image, stage } = await showFirstImage();

    swipe(stage, -120);
    // Both images complete the slide: the current one leaves by a full width, the next lands at 0.
    expect(image).toHaveStyle({ transform: `scale(1) translate(${-(WIDTH + GAP)}px, 0px)` });
    const next = incoming()!;
    expect(next).toHaveStyle({ transform: "scale(1) translate(0px, 0px)" });
    expect(screen.getByText("1 / 3")).toBeInTheDocument();
    // It loaded while sliding in, before it became the current image.
    await act(async () => {
      fireEvent.load(next);
    });

    await settle();
    expect(screen.getByText("2 / 3")).toBeInTheDocument();
    // The same element, not a fresh one that would have to decode again, is now the shown image.
    expect(screen.getByRole("img", { name: "two.jpg" })).toBe(next);
    expect(next).toHaveAttribute("data-lightbox-shown");
    expect(next).toHaveStyle({ opacity: "1", transform: "scale(1) translate(0px, 0px)" });
    expect(image).not.toBeInTheDocument();
    // Releasing a swipe on the backdrop must not also close the viewer.
    expect(onClose).not.toHaveBeenCalled();
  });

  it("hides the image it left until a still-loading incoming image arrives", async () => {
    renderLightbox();
    const { image, stage } = await showFirstImage();

    swipe(stage, -120);
    const next = incoming()!;
    await settle();
    expect(screen.getByText("2 / 3")).toBeInTheDocument();
    expect(image).toHaveStyle({ opacity: "0" });

    await act(async () => {
      fireEvent.load(next);
    });
    expect(screen.getByRole("img", { name: "two.jpg" })).toBe(next);
    expect(next).toHaveStyle({ opacity: "1" });
    expect(image).not.toBeInTheDocument();
  });

  it("goes to the previous image on a swipe right", async () => {
    renderLightbox();
    const { stage } = await showFirstImage();

    swipe(stage, 120);
    await settle();
    expect(screen.getByText("3 / 3")).toBeInTheDocument();
  });

  it("springs back after a short swipe and slides the incoming image back out", async () => {
    renderLightbox();
    const { image, stage } = await showFirstImage();

    swipe(stage, -30);
    expect(image).toHaveStyle({ transform: "scale(1) translate(0px, 0px)" });
    expect(incoming()).toHaveStyle({ transform: `scale(1) translate(${WIDTH + GAP}px, 0px)` });

    await settle();
    expect(incoming()).toBeNull();
    expect(screen.getByText("1 / 3")).toBeInTheDocument();
  });

  it("resists at the end of a list that does not wrap, with nothing to pull in", async () => {
    renderLightbox({ wrap: false });
    const { image, stage } = await showFirstImage();

    fireEvent.pointerDown(stage, at(200));
    fireEvent.pointerMove(stage, at(300));
    expect(image).toHaveStyle({ transform: "scale(1) translate(30px, 0px)" });
    expect(incoming()).toBeNull();

    fireEvent.pointerUp(stage, at(300));
    await settle();
    expect(screen.getByText("1 / 3")).toBeInTheDocument();
  });

  it("shows the load error, rather than a frozen stage, when the incoming image fails", async () => {
    renderLightbox();
    const { stage } = await showFirstImage();

    fireEvent.pointerDown(stage, at(300));
    fireEvent.pointerMove(stage, at(150));
    fireEvent.error(incoming()!);
    fireEvent.pointerUp(stage, at(150));
    await settle();

    expect(screen.getByText("2 / 3")).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("Unable to load image");
    // The viewer still answers: the next swipe turns the page again.
    swipe(stage, -120);
    await settle();
    expect(screen.getByText("3 / 3")).toBeInTheDocument();
  });

  it("does not turn twice when a key moves on during the slide, and shows where it landed", async () => {
    renderLightbox();
    const { stage } = await showFirstImage();

    swipe(stage, -120);
    const next = incoming()!;
    await act(async () => {
      fireEvent.load(next);
    });
    fireEvent.keyDown(window, { key: "ArrowRight" });
    expect(screen.getByText("2 / 3")).toBeInTheDocument();

    await settle();
    expect(screen.getByText("2 / 3")).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "two.jpg" })).toHaveStyle({ opacity: "1" });
  });

  it("loads the next page once however many times the boundary is swiped while it loads", async () => {
    let finishLoad!: (images: { id: number; src: string; title: string }[]) => void;
    const loadNext = vi.fn(
      () => new Promise<{ id: number; src: string; title: string }[]>((resolve) => (finishLoad = resolve)),
    );
    renderLightbox({ initialIndex: 2, wrap: false, hasNext: true, loadNext });
    const image = screen.getByRole("img", { name: "three.jpg" });
    await act(async () => {
      fireEvent.load(image);
    });
    const stage = stageOf(image);

    swipe(stage, -120);
    swipe(stage, -120);
    await settle();
    expect(loadNext).toHaveBeenCalledTimes(1);

    await act(async () => finishLoad([{ id: 4, src: "/four.jpg", title: "four.jpg" }]));
    expect(screen.getByText("4 / 4")).toBeInTheDocument();
  });

  it("does not let a peek's load result stand in for a fresh element of the same image", async () => {
    renderLightbox();
    const { image, stage } = await showFirstImage();

    // A short swipe pulls the next image in far enough to load, then springs back and drops it.
    fireEvent.pointerDown(stage, at(200));
    fireEvent.pointerMove(stage, at(170));
    const peeked = incoming()!;
    await act(async () => {
      fireEvent.load(peeked);
    });
    fireEvent.pointerUp(stage, at(170));
    await settle();
    expect(peeked).not.toBeInTheDocument();

    // Arrow navigation keeps the shown image until the next one's own element has loaded.
    fireEvent.keyDown(window, { key: "ArrowRight" });
    expect(screen.getByText("2 / 3")).toBeInTheDocument();
    expect(image).toHaveAttribute("data-lightbox-shown");
    const fresh = screen.getByRole("img", { name: "two.jpg" });
    expect(fresh).not.toHaveAttribute("data-lightbox-shown");

    await act(async () => {
      fireEvent.load(fresh);
    });
    expect(fresh).toHaveAttribute("data-lightbox-shown");
  });

  it("does not report a peek's load failure for a fresh element of the same image", async () => {
    renderLightbox();
    const { stage } = await showFirstImage();

    fireEvent.pointerDown(stage, at(200));
    fireEvent.pointerMove(stage, at(170));
    fireEvent.error(incoming()!);
    fireEvent.pointerUp(stage, at(170));
    await settle();

    fireEvent.keyDown(window, { key: "ArrowRight" });
    expect(screen.getByText("2 / 3")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("shows the loading spinner while a swipe waits on an incoming image that is still loading", async () => {
    renderLightbox();
    const { stage } = await showFirstImage();

    swipe(stage, -120);
    await settle();
    expect(screen.getByText("2 / 3")).toBeInTheDocument();
    expect(stage.querySelector(".animate-spin")).not.toBeNull();
  });

  it("ignores mostly vertical and mouse drags, and cancelled swipes", async () => {
    renderLightbox();
    const { stage } = await showFirstImage();

    swipe(stage, -80, 160);
    swipe(stage, -200, 0, "mouse");
    fireEvent.pointerDown(stage, at(200));
    fireEvent.pointerMove(stage, at(60));
    fireEvent.pointerCancel(stage, at(60));
    await settle();
    expect(screen.getByText("1 / 3")).toBeInTheDocument();
  });
});
