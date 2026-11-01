import { act, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    engagement: { hostId: 152376, likeCount: 0, rating: null },
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

import { Lightbox } from "../components/Lightbox";
import { trackInteraction } from "../utils/interactionTracking";

function renderLightbox() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <Lightbox
        images={[{ id: 152376, src: "/image.jpg", title: "001.jpg" }]}
        initialIndex={0}
        open
        onClose={vi.fn()}
      />
    </QueryClientProvider>,
  );
}

function zoomControl(name: string) {
  return screen.getByRole("button", { name });
}

function iconClassesOf(name: string) {
  const icon = zoomControl(name).querySelector("svg");
  expect(icon).not.toBeNull();
  return Array.from(icon!.classList).filter((className) => className.startsWith("lucide-"));
}

describe("Lightbox zoom controls", () => {
  it("gives reset zoom its own icon so it is not mistaken for zoom out", () => {
    renderLightbox();

    expect(iconClassesOf("Zoom out")).toContain("lucide-zoom-out");
    expect(iconClassesOf("Zoom in")).toContain("lucide-zoom-in");
    expect(iconClassesOf("Reset zoom")).toContain("lucide-rotate-ccw");
  });

  it("labels every zoom control on hover", () => {
    renderLightbox();

    expect(zoomControl("Zoom out")).toHaveAttribute("title", "Zoom out");
    expect(zoomControl("Zoom in")).toHaveAttribute("title", "Zoom in");
    expect(zoomControl("Reset zoom")).toHaveAttribute("title", "Reset zoom");
  });

  it("returns the image to its unzoomed scale when reset zoom is used", () => {
    renderLightbox();
    const image = screen.getByRole("img", { name: "001.jpg" });

    fireEvent.click(zoomControl("Zoom in"));
    expect(image).toHaveStyle({ transform: "scale(1.5) translate(0px, 0px)" });

    fireEvent.click(zoomControl("Reset zoom"));
    expect(image).toHaveStyle({ transform: "scale(1) translate(0px, 0px)" });
  });

  it("keeps the zoom buttons off narrow viewports, where pinching zooms the image", () => {
    renderLightbox();

    for (const name of ["Zoom out", "Zoom in", "Reset zoom"]) {
      expect(zoomControl(name)).toHaveClass("max-sm:hidden");
    }
    expect(zoomControl("Close (Esc)")).not.toHaveClass("max-sm:hidden");
  });

  it("lets the image take the pinch gestures the rest of Cove blocks", () => {
    renderLightbox();
    const image = screen.getByRole("img", { name: "001.jpg" });

    expect(image).toHaveStyle({ touchAction: "none" });
    expect(image.parentElement).toHaveStyle({ touchAction: "none" });
  });
});

describe("Lightbox pinch zoom", () => {
  // The stage sits at (20, 60) and lays the image out at (30, 40) inside it, 300x200, so the image's
  // untransformed centre is at (200, 200) on screen.
  const CENTRE = { x: 200, y: 200 };

  async function renderShownImage() {
    renderLightbox();
    const image = screen.getByRole("img", { name: "001.jpg" });
    await act(async () => {
      fireEvent.load(image);
    });
    expect(image).toHaveAttribute("data-lightbox-shown");
    const stage = image.parentElement!;
    vi.spyOn(stage, "getBoundingClientRect").mockReturnValue(new DOMRect(20, 60, 400, 400));
    for (const [property, value] of Object.entries({
      offsetLeft: 30,
      offsetTop: 40,
      offsetWidth: 300,
      offsetHeight: 200,
    })) {
      Object.defineProperty(image, property, { configurable: true, value });
    }
    vi.mocked(trackInteraction).mockClear();
    return image;
  }

  // Two fingers on a horizontal line through the image centre, given as offsets from that centre.
  function pinch(target: Element, from: [number, number], to: [number, number]) {
    const at = (pointerId: number, x: number) => ({
      pointerId,
      pointerType: "touch",
      isPrimary: pointerId === 1,
      clientX: CENTRE.x + x,
      clientY: CENTRE.y,
    });
    fireEvent.pointerDown(target, at(1, from[0]));
    fireEvent.pointerDown(target, at(2, from[1]));
    fireEvent.pointerMove(target, at(1, to[0]));
    fireEvent.pointerMove(target, at(2, to[1]));
  }

  function release(target: Element, ...pointerIds: number[]) {
    for (const pointerId of pointerIds) fireEvent.pointerUp(target, { pointerId, pointerType: "touch" });
  }

  function tap(target: Element) {
    const point = { pointerId: 3, pointerType: "touch", isPrimary: true, clientX: CENTRE.x, clientY: CENTRE.y };
    fireEvent.pointerDown(target, point);
    fireEvent.pointerUp(target, point);
    fireEvent.click(target, point);
  }

  it("scales the image by how far the fingers spread", async () => {
    const image = await renderShownImage();

    pinch(image, [-50, 50], [-100, 100]);

    expect(image).toHaveStyle({ transform: "scale(2) translate(0px, 0px)" });
  });

  it("keeps the point between the fingers under the fingers", async () => {
    const image = await renderShownImage();

    // Doubling around 150px right of the image centre moves the image 150px left.
    pinch(image, [100, 200], [50, 250]);

    expect(image).toHaveStyle({ transform: "scale(2) translate(-75px, 0px)" });
  });

  it("does not shrink the image below its fitted size", async () => {
    const image = await renderShownImage();

    pinch(image, [100, 200], [140, 160]);

    expect(image).toHaveStyle({ transform: "scale(1) translate(0px, 0px)" });
  });

  it("does not toggle zoom when a pinch ends, but a later tap still does", async () => {
    const image = await renderShownImage();

    pinch(image, [-50, 50], [-100, 100]);
    release(image, 1, 2);
    fireEvent.click(image);
    expect(image).toHaveStyle({ transform: "scale(2) translate(0px, 0px)" });

    tap(image);
    expect(image).toHaveStyle({ transform: "scale(1) translate(0px, 0px)" });
  });

  it("pans with the finger left on the image after a pinch", async () => {
    const image = await renderShownImage();

    pinch(image, [-50, 50], [-100, 100]);
    release(image, 2);
    fireEvent.pointerMove(image, {
      pointerId: 1,
      pointerType: "touch",
      clientX: CENTRE.x - 60,
      clientY: CENTRE.y + 20,
    });

    expect(image).toHaveStyle({ transform: "scale(2) translate(20px, 10px)" });
  });

  it("forgets a finger whose release was lost when a new gesture starts", async () => {
    const image = await renderShownImage();

    // Finger 7 never reports its release; the next primary finger must not pair with it as a pinch.
    fireEvent.pointerDown(image, { pointerId: 7, pointerType: "touch", isPrimary: true, clientX: 0, clientY: 0 });
    tap(image);

    expect(image).toHaveStyle({ transform: "scale(2) translate(0px, 0px)" });
  });

  it("reports a pinch only when it changed the zoom", async () => {
    const image = await renderShownImage();

    pinch(image, [-50, 50], [-48, 48]);
    release(image, 1, 2);
    expect(trackInteraction).not.toHaveBeenCalled();

    pinch(image, [-50, 50], [-100, 100]);
    release(image, 1, 2);
    expect(trackInteraction).toHaveBeenCalledTimes(1);
    expect(trackInteraction).toHaveBeenCalledWith(
      expect.objectContaining({ kind: "zoom", meta: expect.objectContaining({ action: "pinch", zoom: 2 }) }),
    );
  });
});
