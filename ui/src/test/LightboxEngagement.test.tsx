import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";

const mockSetRating = vi.fn();
const mockIncrementLike = vi.fn();

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    engagement: { hostId: 152376, likeCount: 1, rating: 40 },
    rating: 40,
    setRating: mockSetRating,
    ratingPending: false,
  }),
}));

vi.mock("../components/Rating", () => ({
  InteractiveRating: ({ onChange, readOnly }: { onChange: (value: number) => void; readOnly?: boolean }) => (
    <button type="button" disabled={readOnly} onClick={() => onChange(60)}>Rate image 60</button>
  ),
}));

vi.mock("../api/client", () => ({
  images: { incrementLike: (...args: unknown[]) => mockIncrementLike(...args) },
  playback: { recordIntervals: vi.fn().mockResolvedValue(undefined) },
}));

vi.mock("../utils/interactionTracking", () => ({
  createPlaybackSessionId: () => "session",
  trackInteraction: vi.fn(),
}));

import { Lightbox } from "../components/Lightbox";

function renderLightbox(canEngage = true, canLike = canEngage) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <Lightbox
        images={[{ id: 152376, src: "/image.jpg", title: "001.jpg" }]}
        initialIndex={0}
        open
        onClose={vi.fn()}
        canEngage={canEngage}
        canLike={canLike}
      />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mockIncrementLike.mockResolvedValue(2);
});

describe("Lightbox image engagement", () => {
  it("shows rating and like controls in the bottom-left overlay", async () => {
    renderLightbox();

    fireEvent.click(screen.getByRole("button", { name: "Rate image 60" }));
    expect(mockSetRating).toHaveBeenCalledWith(60);

    fireEvent.click(screen.getByRole("button", { name: "Like image (1 likes)" }));
    await waitFor(() => expect(mockIncrementLike).toHaveBeenCalledWith(152376));
    expect(await screen.findByRole("button", { name: "Like image (2 likes)" })).toBeInTheDocument();

    const controls = screen.getByRole("button", { name: "Rate image 60" }).parentElement;
    expect(controls).toHaveClass("absolute", "bottom-[max(1rem,env(safe-area-inset-bottom))]", "left-4");
  });

  it("prevents engagement mutations without permission", () => {
    renderLightbox(false);

    expect(screen.getByRole("button", { name: "Rate image 60" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Like image (1 likes)" })).toBeDisabled();
  });

  it("allows ratings but prevents likes without image write permission", () => {
    renderLightbox(true, false);

    expect(screen.getByRole("button", { name: "Rate image 60" })).toBeEnabled();
    expect(screen.getByRole("button", { name: "Like image (1 likes)" })).toBeDisabled();
  });
});
