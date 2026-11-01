import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";

const mockSetRating = vi.fn();
const mockIncrementLike = vi.fn();
// Whether a rating save is in flight, as the engagement hook reports it.
const mockRatingState = { pending: false };

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({
    engagement: { hostId: 152376, likeCount: 1, rating: 40 },
    rating: 40,
    setRating: mockSetRating,
    ratingPending: mockRatingState.pending,
  }),
}));

vi.mock("../components/Rating", () => ({
  InteractiveRating: vi.fn(
    ({
      onChange,
      readOnly,
      disabled,
    }: {
      onChange: (value: number) => void;
      readOnly?: boolean;
      disabled?: boolean;
    }) => (
      <button type="button" disabled={readOnly || disabled} onClick={() => onChange(60)}>
        Rate image 60
      </button>
    ),
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
  mockRatingState.pending = false;
  mockIncrementLike.mockResolvedValue(2);
});

describe("Lightbox image engagement", () => {
  it("shows rating and like controls in the bottom-right overlay", async () => {
    renderLightbox();

    fireEvent.click(screen.getByRole("button", { name: "Rate image 60" }));
    expect(mockSetRating).toHaveBeenCalledWith(60);

    fireEvent.click(screen.getByRole("button", { name: "Like image (1 likes)" }));
    await waitFor(() => expect(mockIncrementLike).toHaveBeenCalledWith(152376));
    expect(await screen.findByRole("button", { name: "Like image (2 likes)" })).toBeInTheDocument();

    // Stars, like and references share one panel at the bottom right, with the stars first in the DOM to
    // match the row on wider screens; phones lift the like row above the stars.
    const ratingRow = screen.getByRole("button", { name: "Rate image 60" }).closest("[data-lightbox-rating]");
    const panel = ratingRow?.closest("[data-lightbox-engagement]");
    expect(panel).toHaveClass("ml-auto");
    expect(panel?.parentElement).toHaveClass("absolute", "bottom-[max(1rem,env(safe-area-inset-bottom))]", "inset-x-4");
    expect(panel?.firstElementChild).toBe(ratingRow);
    const likeRow = screen.getByRole("button", { name: "Like image (2 likes)" }).parentElement;
    expect(likeRow?.parentElement).toBe(panel);
    expect(likeRow).toHaveClass("max-sm:order-first");
  });

  it("asks for compact stars so the overlay fits a phone", async () => {
    const { InteractiveRating } = await import("../components/Rating");
    renderLightbox();

    expect(vi.mocked(InteractiveRating).mock.lastCall?.[0]).toMatchObject({ compact: true });
  });

  it("shows the image name at the bottom left, beside the engagement panel", () => {
    renderLightbox();

    const name = screen.getByText("001.jpg");
    const panel = screen.getByRole("button", { name: "Rate image 60" }).closest("[data-lightbox-engagement]");
    expect(name.parentElement).toBe(panel?.parentElement);
    expect(name.parentElement?.firstElementChild).toBe(name);
    expect(name).toHaveClass("truncate");
  });

  it("keeps the stars interactive-looking and shows the chosen rating while it saves", async () => {
    const { InteractiveRating } = await import("../components/Rating");
    mockSetRating.mockImplementation(() => {
      mockRatingState.pending = true;
    });
    renderLightbox();

    fireEvent.click(screen.getByRole("button", { name: "Rate image 60" }));

    // Switching to the read-only layout would flash the old value and an "Unrated" label.
    expect(vi.mocked(InteractiveRating).mock.lastCall?.[0]).toMatchObject({
      value: 60,
      readOnly: false,
      disabled: true,
    });
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
