import { fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { playback } from "../api/client";
import { trackInteraction } from "../utils/interactionTracking";

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

describe("Lightbox telemetry", () => {
  beforeEach(() => {
    vi.mocked(trackInteraction).mockClear();
    vi.mocked(playback.recordIntervals).mockClear();
  });

  it("reports the position and total shown when the lightbox closes after they change", () => {
    const { rerender } = renderLightbox({ totalCount: 10, positionOffset: 0 });

    rerender({ totalCount: 40, positionOffset: 20 });
    fireEvent.click(screen.getByRole("button", { name: "Close (Esc)" }));

    expect(trackInteraction).toHaveBeenLastCalledWith(
      expect.objectContaining({ kind: "closeLightbox", meta: expect.objectContaining({ index: 21, count: 40 }) }),
    );
  });

  it("reports a dwell session with the position it started on", () => {
    const { rerender } = renderLightbox({ totalCount: 10, positionOffset: 0 });

    rerender({ totalCount: 40, positionOffset: 20 });
    fireEvent.click(screen.getByRole("button", { name: "Next image" }));

    expect(playback.recordIntervals).toHaveBeenCalledTimes(1);
    expect(playback.recordIntervals).toHaveBeenCalledWith(
      expect.objectContaining({
        hostId: 1,
        state: "ended",
        context: expect.objectContaining({ index: 1, count: 10, source: "lightbox" }),
      }),
    );
  });
});
