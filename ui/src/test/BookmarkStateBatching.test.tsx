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
import { useState } from "react";
import type { AuthContextValue } from "../auth/AuthContext";
import { AuthContext } from "../auth/optionalAuth";
import { fetchBookmarkState, invalidateBookmarkStates, usePrefetchBookmarkStates } from "../state/bookmarkStates";

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

function renderWithClient(ui: React.ReactElement, auth?: AuthContextValue) {
  // The app keeps query data fresh for 30 seconds; the remount checks depend on that.
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 30_000 }, mutations: { retry: false } },
  });
  const tree = <QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>;
  const result = render(auth ? <AuthContext.Provider value={auth}>{tree}</AuthContext.Provider> : tree);
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

// A list page prefetches every loaded ID, then mounts only the cards a virtualized list shows.
function VirtualizedPage({ ids }: { ids: number[] }) {
  usePrefetchBookmarkStates("video", ids);
  const [mounted, setMounted] = useState(1);
  return (
    <>
      <button type="button" onClick={() => setMounted((count) => count + 1)}>
        Scroll
      </button>
      {ids.slice(0, mounted).map((id) => (
        <VideoCard key={id} video={video(id)} onClick={vi.fn()} />
      ))}
    </>
  );
}

describe("list page Save for Later prefetch", () => {
  it("serves cards mounted later from the page's single request", async () => {
    savedIds.add("video:3");
    renderWithClient(<VirtualizedPage ids={[1, 2, 3, 4]} />);

    await waitFor(() => expect(batchSpy).toHaveBeenCalledTimes(1));
    expect(batchSpy).toHaveBeenCalledWith({ hostType: "video", hostIds: [1, 2, 3, 4] });

    for (let step = 0; step < 3; step += 1) {
      fireEvent.click(screen.getByRole("button", { name: "Scroll" }));
      await act(() => new Promise((resolve) => setTimeout(resolve, 5)));
    }

    expect(cardButton("Video 3")).toHaveAttribute("aria-pressed", "true");
    expect(cardButton("Video 4")).toHaveAttribute("aria-pressed", "false");
    expect(batchSpy).toHaveBeenCalledTimes(1);
  });

  it("shows a state changed on a detail page when the card mounts again, without another request", async () => {
    // The list and the detail page are never mounted together, as with real navigation.
    function App() {
      const [page, setPage] = useState<"list" | "detail">("list");
      return (
        <>
          <button type="button" onClick={() => setPage(page === "list" ? "detail" : "list")}>
            Navigate
          </button>
          {page === "list" ? (
            <VirtualizedPage ids={[8]} />
          ) : (
            <div data-testid="detail">
              <BookmarkButton hostType="video" hostId={8} />
            </div>
          )}
        </>
      );
    }
    renderWithClient(<App />);
    await waitFor(() => expect(batchSpy).toHaveBeenCalledTimes(1));
    expect(cardButton("Video 8")).toHaveAttribute("aria-pressed", "false");

    fireEvent.click(screen.getByRole("button", { name: "Navigate" }));
    fireEvent.click(within(screen.getByTestId("detail")).getByRole("button", { name: "Save for Later" }));
    await waitFor(() =>
      expect(within(screen.getByTestId("detail")).getByRole("button")).toHaveAttribute("aria-pressed", "true"),
    );

    fireEvent.click(screen.getByRole("button", { name: "Navigate" }));
    await act(() => new Promise((resolve) => setTimeout(resolve, 5)));
    expect(cardButton("Video 8")).toHaveAttribute("aria-pressed", "true");
    expect(batchSpy).toHaveBeenCalledTimes(1);
  });
});

describe("BookmarkButton outside a signed-in session", () => {
  it("does not ask for states as a share-link guest", async () => {
    const guest = { user: { kind: "shareLink" }, hasPermission: () => true } as unknown as AuthContextValue;
    renderWithClient(
      <>
        <VideoCard video={video(1)} onClick={vi.fn()} />
        <VirtualizedPage ids={[2, 3]} />
      </>,
      guest,
    );

    await act(() => new Promise((resolve) => setTimeout(resolve, 10)));
    expect(batchSpy).not.toHaveBeenCalled();
    expect(cardButton("Video 1")).toHaveAttribute("aria-pressed", "false");
  });
});

describe("BookmarkButton before its state loads", () => {
  it("resolves the state first, then toggles from it", async () => {
    savedIds.add("video:9");
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    batchSpy.mockImplementationOnce(async ({ hostType, hostIds }: BookmarkBatchRequest) => {
      await gate;
      return hostIds.map((hostId) => ({ hostType, hostId, saved: savedIds.has(`${hostType}:${hostId}`) }));
    });
    renderWithClient(<BookmarkButton hostType="video" hostId={9} />);

    fireEvent.click(screen.getByRole("button", { name: "Save for Later" }));
    release();

    await waitFor(() => expect(bookmarks.toggle).toHaveBeenCalledWith({ hostType: "video", hostId: 9, saved: false }));
    expect(await screen.findByRole("button", { name: "Save for Later" })).toHaveAttribute("aria-pressed", "false");
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
