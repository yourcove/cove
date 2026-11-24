import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from "vitest";

vi.mock("../components/Rating", () => ({
  RatingBanner: () => null,
  RatingBadge: () => null,
}));

import { bookmarks } from "../api/client";
import type { BookmarkBatchRequest } from "../api/types";
import { BookmarkButton } from "../components/BookmarkButton";
import { GroupTile, ImageTile, VideoCard } from "../components/EntityCards";
import { fetchBookmarkState, invalidateBookmarkStates } from "../state/bookmarkStates";

const savedIds = new Set<string>();

function video(id: number) {
  return {
    id,
    title: `Video ${id}`,
    updatedAt: "2024-01-12T00:00:00Z",
    files: [],
    performers: [],
    groups: [],
    galleries: [],
    tags: [],
    studioName: null,
    studioId: null,
    resumeTime: 0,
    rating: null,
    likeCounter: 0,
    organized: false,
    details: null,
    date: null,
    playCount: 0,
  } as any;
}

function image(id: number) {
  return { id, title: `Image ${id}`, organized: false, urls: [], tags: [], performers: [], galleries: [] } as any;
}

function group(id: number) {
  return {
    id,
    name: `Group ${id}`,
    frontImagePath: null,
    tags: [],
    videoCount: 0,
    subGroupCount: 0,
    createdAt: "2026-05-01T00:00:00Z",
    updatedAt: "2026-05-01T00:00:00Z",
  } as any;
}

function cardButton(title: string) {
  const card = screen.getByText(title).closest(".group") as HTMLElement | null;
  if (!card) throw new Error(`No card for ${title}`);
  return within(card).getByRole("button", { name: /Save for Later/ });
}

let batchSpy: MockInstance<typeof bookmarks.batch>;

beforeEach(() => {
  savedIds.clear();
  vi.stubGlobal(
    "IntersectionObserver",
    class {
      observe() {}
      disconnect() {}
      unobserve() {}
    },
  );
  batchSpy = vi
    .spyOn(bookmarks, "batch")
    .mockImplementation(async ({ hostType, hostIds }: BookmarkBatchRequest) =>
      hostIds.map((hostId) => ({ hostType, hostId, saved: savedIds.has(`${hostType}:${hostId}`) })),
    );
  vi.spyOn(bookmarks, "toggle").mockImplementation(async ({ hostType, hostId, saved }) => {
    if (saved) savedIds.add(`${hostType}:${hostId}`);
    else savedIds.delete(`${hostType}:${hostId}`);
    return { hostType, hostId, saved };
  });
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

function renderWithClient(ui: React.ReactElement) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const result = render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
  return { ...result, queryClient };
}

describe("card Save for Later state", () => {
  it("shows saved cards without hover, using one batch request per host type", async () => {
    savedIds.add("video:2");
    savedIds.add("image:11");
    savedIds.add("group:21");

    renderWithClient(
      <>
        {[1, 2, 3].map((id) => (
          <VideoCard key={`v${id}`} video={video(id)} onClick={vi.fn()} />
        ))}
        {[10, 11].map((id) => (
          <ImageTile key={`i${id}`} image={image(id)} onClick={vi.fn()} />
        ))}
        {[20, 21].map((id) => (
          <GroupTile key={`g${id}`} group={group(id)} onClick={vi.fn()} />
        ))}
      </>,
    );

    await waitFor(() => expect(cardButton("Video 2")).toHaveAttribute("aria-pressed", "true"));
    expect(cardButton("Video 1")).toHaveAttribute("aria-pressed", "false");
    expect(cardButton("Video 3")).toHaveAttribute("aria-pressed", "false");
    await waitFor(() => expect(cardButton("Image 11")).toHaveAttribute("aria-pressed", "true"));
    expect(cardButton("Image 10")).toHaveAttribute("aria-pressed", "false");
    await waitFor(() => expect(cardButton("Group 21")).toHaveAttribute("aria-pressed", "true"));
    expect(cardButton("Group 20")).toHaveAttribute("aria-pressed", "false");

    expect(batchSpy).toHaveBeenCalledTimes(3);
    const requests = batchSpy.mock.calls.map(([request]) => request);
    expect(requests).toEqual(
      expect.arrayContaining([
        { hostType: "video", hostIds: [1, 2, 3] },
        { hostType: "image", hostIds: [10, 11] },
        { hostType: "group", hostIds: [20, 21] },
      ]),
    );
  });

  it("keeps a card in step with another button for the same item", async () => {
    renderWithClient(
      <>
        <VideoCard video={video(5)} onClick={vi.fn()} />
        <div data-testid="detail">
          <BookmarkButton hostType="video" hostId={5} />
        </div>
      </>,
    );

    await waitFor(() => expect(batchSpy).toHaveBeenCalledTimes(1));
    expect(batchSpy).toHaveBeenCalledWith({ hostType: "video", hostIds: [5] });
    const detailButton = within(screen.getByTestId("detail")).getByRole("button", { name: "Save for Later" });
    fireEvent.click(detailButton);

    await waitFor(() => expect(cardButton("Video 5")).toHaveAttribute("aria-pressed", "true"));
    expect(detailButton).toHaveAttribute("aria-pressed", "true");

    fireEvent.click(cardButton("Video 5"));
    await waitFor(() => expect(detailButton).toHaveAttribute("aria-pressed", "false"));
    expect(cardButton("Video 5")).toHaveAttribute("aria-pressed", "false");
  });

  it("refetches every cached state of a host type in one request when invalidated", async () => {
    const { queryClient } = renderWithClient(
      <>
        {[1, 2].map((id) => (
          <VideoCard key={id} video={video(id)} onClick={vi.fn()} />
        ))}
      </>,
    );
    await waitFor(() => expect(batchSpy).toHaveBeenCalledTimes(1));
    expect(cardButton("Video 1")).toHaveAttribute("aria-pressed", "false");

    await waitFor(() => expect(queryClient.isFetching()).toBe(0));

    savedIds.add("video:1");
    savedIds.add("video:2");
    await act(() => invalidateBookmarkStates(queryClient, "video"));

    await waitFor(() => expect(cardButton("Video 1")).toHaveAttribute("aria-pressed", "true"));
    expect(cardButton("Video 2")).toHaveAttribute("aria-pressed", "true");
    expect(batchSpy).toHaveBeenCalledTimes(2);
    expect(batchSpy).toHaveBeenLastCalledWith({ hostType: "video", hostIds: [1, 2] });
  });
});

describe("fetchBookmarkState", () => {
  it("rejects every waiting lookup when the batch request fails", async () => {
    batchSpy.mockRejectedValueOnce(new Error("offline"));

    const results = await Promise.allSettled([fetchBookmarkState("video", 1), fetchBookmarkState("video", 1)]);

    expect(results.map((result) => result.status)).toEqual(["rejected", "rejected"]);
    expect(batchSpy).toHaveBeenCalledTimes(1);
    expect(batchSpy).toHaveBeenCalledWith({ hostType: "video", hostIds: [1] });
  });

  it("splits very large pages into several requests", async () => {
    const ids = Array.from({ length: 501 }, (_, index) => index + 1);

    const states = await Promise.all(ids.map((id) => fetchBookmarkState("image", id)));

    expect(states).toHaveLength(501);
    expect(batchSpy).toHaveBeenCalledTimes(2);
    expect(batchSpy.mock.calls[0][0].hostIds).toHaveLength(500);
    expect(batchSpy.mock.calls[1][0].hostIds).toEqual([501]);
  });
});
