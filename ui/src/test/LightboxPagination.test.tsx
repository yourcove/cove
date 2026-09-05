import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import { Lightbox } from "../components/Lightbox";
import { extendLightboxPageBounds } from "../utils/lightboxPagination";

vi.mock("../api/client", () => ({
  entityEngagement: { get: vi.fn().mockResolvedValue(undefined) },
  images: { incrementLike: vi.fn().mockResolvedValue(0) },
  playback: { recordIntervals: vi.fn().mockResolvedValue(undefined) },
}));

vi.mock("../utils/interactionTracking", () => ({
  createPlaybackSessionId: () => "test-session",
  trackInteraction: vi.fn(),
}));

vi.mock("../components/Rating", () => ({
  InteractiveRating: () => null,
}));

describe("Lightbox pagination", () => {
  it("tracks both loaded boundaries when navigation reverses direction", () => {
    let bounds = { first: 5, last: 5 };
    bounds = extendLightboxPageBounds(bounds, 6, "next");
    bounds = extendLightboxPageBounds(bounds, 4, "previous");
    bounds = extendLightboxPageBounds(bounds, 7, "next");

    expect(bounds).toEqual({ first: 4, last: 7 });
  });

  it("loads and advances to the next page at the queue boundary", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const loadNext = vi.fn().mockResolvedValue([
      {
        id: 2,
        src: "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==",
        title: "Page two image",
      },
    ]);

    render(
      <QueryClientProvider client={queryClient}>
        <Lightbox
          images={[
            {
              id: 1,
              src: "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==",
              title: "Page one image",
            },
          ]}
          initialIndex={0}
          open
          onClose={() => {}}
          hasNext
          loadNext={loadNext}
        />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Next image" }));

    await waitFor(() => expect(screen.getByAltText("Page two image")).toBeInTheDocument());
    expect(loadNext).toHaveBeenCalledOnce();
  });
});
