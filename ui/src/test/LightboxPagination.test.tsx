import { useState } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import type { FindFilter, Image, PaginatedResponse } from "../api/types";
import { Lightbox } from "../components/Lightbox";
import { usePaginatedImageLightbox } from "../hooks/usePaginatedImageLightbox";
import { extendLightboxPageBounds } from "../utils/lightboxPagination";

vi.mock("../api/client", () => ({
  // The API answers null for an image nobody has engaged with yet.
  entityEngagement: { get: vi.fn().mockResolvedValue(null) },
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

const image = (id: number): Image => ({ id, title: `Image ${id}`, files: [] }) as unknown as Image;

function PaginatedLightboxHarness({
  queryPage,
}: {
  queryPage: (filter: FindFilter) => Promise<PaginatedResponse<Image>>;
}) {
  const pageTwoImages = Array.from({ length: 60 }, (_, index) => image(index + 61));
  const lightbox = usePaginatedImageLightbox({
    items: pageTwoImages,
    filter: { page: 2, perPage: 60 },
    totalCount: 131,
    infinitePageSize: false,
    queryPage,
    toLightboxImage: (item) => ({ id: item.id, src: `/image/${item.id}`, title: item.title }),
  });

  return (
    <>
      <button onClick={() => lightbox.openImage(61)}>Open lightbox</button>
      <Lightbox {...lightbox.lightboxProps} />
    </>
  );
}

// Stands in for an infinite list: holds a growing prefix of the 131 results and extends it one
// 30-result page at a time, the way the list's own infinite query does.
function useFakeInfiniteList(initialCount: number) {
  const [loadedCount, setLoadedCount] = useState(initialCount);
  const all = Array.from({ length: 131 }, (_, index) => image(index + 1));
  const items = all.slice(0, loadedCount);
  const fetchMoreItems = async () => {
    const next = Math.min(131, loadedCount + 30);
    setLoadedCount(next);
    return all.slice(0, next);
  };

  return { items, fetchMoreItems };
}

function InfiniteLightboxHarness({ loadedCount, openAt }: { loadedCount: number; openAt: number }) {
  const list = useFakeInfiniteList(loadedCount);
  const lightbox = usePaginatedImageLightbox({
    items: list.items,
    filter: { page: 1, perPage: 0 },
    totalCount: 131,
    infinitePageSize: true,
    queryPage: vi.fn(),
    toLightboxImage: (item) => ({ id: item.id, src: `/image/${item.id}`, title: item.title }),
    fetchMoreItems: list.fetchMoreItems,
  });

  return (
    <>
      <button onClick={() => lightbox.openImage(openAt)}>Open lightbox</button>
      <Lightbox {...lightbox.lightboxProps} />
    </>
  );
}

describe("Lightbox pagination", () => {
  it("shows the known result count when only part of the queue is loaded", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <Lightbox
          images={[
            {
              id: 1,
              src: "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==",
              title: "First loaded image",
            },
          ]}
          initialIndex={0}
          open
          onClose={() => {}}
          totalCount={131}
        />
      </QueryClientProvider>,
    );

    expect(screen.getByText("1 / 131")).toBeInTheDocument();
  });

  it("keeps the absolute position when loading and reversing into a previous page", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const queryPage = vi.fn().mockResolvedValue({
      items: Array.from({ length: 60 }, (_, index) => image(index + 1)),
      totalCount: 131,
    });

    render(
      <QueryClientProvider client={queryClient}>
        <PaginatedLightboxHarness queryPage={queryPage} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Open lightbox" }));
    expect(screen.getByText("61 / 131")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Previous image" }));
    await waitFor(() => expect(screen.getByText("60 / 131")).toBeInTheDocument());

    await userEvent.click(screen.getByRole("button", { name: "Next image" }));
    expect(screen.getByText("61 / 131")).toBeInTheDocument();
    expect(queryPage).toHaveBeenCalledWith({ page: 1, perPage: 60 });
  });

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

  it("navigates past the loaded results of an infinite list instead of wrapping", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <InfiniteLightboxHarness loadedCount={60} openAt={60} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Open lightbox" }));
    expect(screen.getByText("60 / 131")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Next image" }));

    await waitFor(() => expect(screen.getByText("61 / 131")).toBeInTheDocument());
  });

  it("reaches the final result of an infinite list and only then wraps", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <InfiniteLightboxHarness loadedCount={120} openAt={120} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Open lightbox" }));
    const next = screen.getByRole("button", { name: "Next image" });

    for (let position = 121; position <= 131; position += 1) {
      await userEvent.click(next);
      await waitFor(() => expect(screen.getByText(`${position} / 131`)).toBeInTheDocument());
    }

    // Every result is now in the queue, so the end of it is the end of the list and wrapping resumes.
    await userEvent.click(next);
    await waitFor(() => expect(screen.getByText("1 / 131")).toBeInTheDocument());
  });

  it("does not wrap backwards out of a partially loaded infinite list", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <InfiniteLightboxHarness loadedCount={60} openAt={1} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Open lightbox" }));
    expect(screen.getByText("1 / 131")).toBeInTheDocument();

    // Wrapping back to the last queued result would report it as result 60 of 131, which it is not.
    expect(screen.getByRole("button", { name: "Previous image" })).toBeDisabled();

    // The keyboard path is not covered by the disabled attribute, so it has to hold the line itself.
    await userEvent.keyboard("{ArrowLeft}");
    expect(screen.getByText("1 / 131")).toBeInTheDocument();
  });
});
