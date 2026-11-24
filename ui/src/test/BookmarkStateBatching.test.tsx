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
import {
  fetchBookmarkState,
  invalidateBookmarkStates,
  usePrefetchBookmarkStateRefs,
  usePrefetchBookmarkStates,
  useResetBookmarkStatesOnUserChange,
  type BookmarkHostRef,
} from "../state/bookmarkStates";

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

describe("list page Save for Later prefetch over time", () => {
  it("asks only for the new page when an infinite list grows, even after earlier states went stale", async () => {
    const { rerender, queryClient } = renderWithClient(<VirtualizedPage ids={[1, 2]} />);
    await waitFor(() => expect(batchSpy).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));

    const now = Date.now();
    vi.spyOn(Date, "now").mockReturnValue(now + 60_000);
    rerender(
      <QueryClientProvider client={queryClient}>
        <VirtualizedPage ids={[1, 2, 3, 4]} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(batchSpy).toHaveBeenCalledTimes(2));
    expect(batchSpy).toHaveBeenLastCalledWith({ hostType: "video", hostIds: [3, 4] });
  });

  it("refetches a state invalidated while its card was unmounted when the card returns", async () => {
    function App() {
      const [selecting, setSelecting] = useState(false);
      return (
        <>
          <button type="button" onClick={() => setSelecting(!selecting)}>
            Toggle selection
          </button>
          <VideoCard video={video(6)} onClick={vi.fn()} selecting={selecting} />
        </>
      );
    }
    const { queryClient } = renderWithClient(<App />);
    await waitFor(() => expect(batchSpy).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(queryClient.isFetching()).toBe(0));

    // Cards hide their button in selection mode, so a bulk change lands while it is unmounted.
    fireEvent.click(screen.getByRole("button", { name: "Toggle selection" }));
    expect(screen.queryByRole("button", { name: /Save for Later/ })).not.toBeInTheDocument();
    savedIds.add("video:6");
    await act(() => invalidateBookmarkStates(queryClient, "video"));
    expect(batchSpy).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole("button", { name: "Toggle selection" }));
    await waitFor(() => expect(cardButton("Video 6")).toHaveAttribute("aria-pressed", "true"));
    expect(batchSpy).toHaveBeenCalledTimes(2);
  });
});

describe("mixed group item prefetch", () => {
  function MixedItems({ refs }: { refs: BookmarkHostRef[] }) {
    usePrefetchBookmarkStateRefs(refs);
    return null;
  }

  it("asks once per host type and skips types whose cards have no button", async () => {
    renderWithClient(
      <MixedItems
        refs={[
          { hostType: "video", hostId: 1 },
          { hostType: "audio", hostId: 2 },
          { hostType: "video", hostId: 3 },
          { hostType: "performer", hostId: 4 },
          { hostType: "group", hostId: 5 },
        ]}
      />,
    );

    await waitFor(() => expect(batchSpy).toHaveBeenCalledTimes(3));
    expect(batchSpy.mock.calls.map(([request]) => request)).toEqual(
      expect.arrayContaining([
        { hostType: "video", hostIds: [1, 3] },
        { hostType: "audio", hostIds: [2] },
        { hostType: "group", hostIds: [5] },
      ]),
    );
  });
});

describe("BookmarkButton and the session", () => {
  function session(user: { id: string; kind: string }) {
    return { user, hasPermission: () => true } as unknown as AuthContextValue;
  }

  it.each([
    ["a share-link guest", session({ id: "0f8e2c", kind: "shareLink" })],
    ["an auth-disabled install without an owner", session({ id: "system", kind: "system" })],
  ])("does not ask for states as %s", async (_label, auth) => {
    renderWithClient(
      <>
        <VideoCard video={video(1)} onClick={vi.fn()} />
        <VirtualizedPage ids={[2, 3]} />
      </>,
      auth,
    );

    await act(() => new Promise((resolve) => setTimeout(resolve, 10)));
    expect(batchSpy).not.toHaveBeenCalled();
    expect(cardButton("Video 1")).toHaveAttribute("aria-pressed", "false");
  });

  it.each([
    ["a signed-in user", session({ id: "3", kind: "user" })],
    ["the owner of an auth-disabled install", session({ id: "1", kind: "system" })],
  ])("shows saved states for %s", async (_label, auth) => {
    savedIds.add("video:1");
    renderWithClient(<VideoCard video={video(1)} onClick={vi.fn()} />, auth);

    await waitFor(() => expect(cardButton("Video 1")).toHaveAttribute("aria-pressed", "true"));
    expect(batchSpy).toHaveBeenCalledTimes(1);
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
    await waitFor(() => expect(batchSpy).toHaveBeenCalledTimes(1));
    expect(bookmarks.toggle).not.toHaveBeenCalled();
    release();

    await waitFor(() => expect(bookmarks.toggle).toHaveBeenCalledWith({ hostType: "video", hostId: 9, saved: false }));
    expect(await screen.findByRole("button", { name: "Save for Later" })).toHaveAttribute("aria-pressed", "false");
  });
});

describe("useResetBookmarkStatesOnUserChange", () => {
  function Session({ userId }: { userId: string | null | undefined }) {
    useResetBookmarkStatesOnUserChange(userId);
    return null;
  }

  it("keeps states while the session loads and drops them when the user signs out or changes", () => {
    const { rerender, queryClient } = renderWithClient(<Session userId={undefined} />);
    const seed = () =>
      queryClient.setQueryData(["bookmark-state", "video", 1], { hostType: "video", hostId: 1, saved: true });
    const rerenderWith = (userId: string | null) =>
      rerender(
        <QueryClientProvider client={queryClient}>
          <Session userId={userId} />
        </QueryClientProvider>,
      );
    const cached = () => queryClient.getQueryData(["bookmark-state", "video", 1]);

    seed();
    rerenderWith("3");
    expect(cached()).toBeDefined();

    rerenderWith(null);
    expect(cached()).toBeUndefined();

    rerenderWith("3");
    seed();
    rerenderWith("4");
    expect(cached()).toBeUndefined();
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
