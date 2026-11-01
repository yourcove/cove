import { fireEvent, render, screen } from "@testing-library/react";
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
