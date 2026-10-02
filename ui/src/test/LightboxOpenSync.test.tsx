import { fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";

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
    expect(screen.getByText("5s")).toBeInTheDocument();

    rerender({ autoPlay: true, slideshowDelay: 8000 });
    expect(screen.getByText("8s")).toBeInTheDocument();
  });
});
