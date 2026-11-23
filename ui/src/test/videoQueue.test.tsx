import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";
import { VideoQueueProvider, useVideoQueue } from "../state/VideoQueueContext";

describe("video queues", () => {
  it("uses the configured autoplay default for a new queue", () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(() => useVideoQueue(), { wrapper });

    act(() =>
      result.current.setQueue([10, 20], 10, undefined, {
        autoplay: true,
      }),
    );

    expect(result.current.autoplay).toBe(true);
  });

  it("loads the next page at the queue boundary", async () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(() => useVideoQueue(), { wrapper });
    const loadNext = vi.fn().mockResolvedValue({ items: [{ id: 30 }, { id: 40 }], hasMore: false });

    act(() => result.current.setQueue([10, 20], 20, undefined, { startIndex: 0, totalCount: 4, loadNext }));
    let targetId: number | null = null;
    await act(async () => {
      targetId = await result.current.goNext();
    });

    expect(targetId).toBe(30);
    expect(result.current.currentId).toBe(30);
    expect(result.current.queue?.videoIds).toEqual([10, 20, 30, 40]);
    expect(loadNext).toHaveBeenCalledOnce();
  });

  it("reports the global position while lazily loading pages", async () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(() => useVideoQueue(), { wrapper });
    const loadPrevious = vi.fn().mockResolvedValue({ items: [{ id: 10 }, { id: 20 }], hasMore: false });

    act(() => result.current.setQueue([30, 40], 30, undefined, { startIndex: 2, totalCount: 4, loadPrevious }));
    expect(result.current.currentPosition).toBe(3);
    expect(result.current.queueLength).toBe(4);
    await act(async () => {
      await result.current.goPrevious();
    });

    expect(result.current.currentId).toBe(20);
    expect(result.current.currentPosition).toBe(2);
  });

  it("ignores a boundary response after the queue is replaced", async () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(() => useVideoQueue(), { wrapper });
    let resolveLoad!: (value: { items: { id: number }[]; hasMore: boolean }) => void;
    const loadNext = () =>
      new Promise<{ items: { id: number }[]; hasMore: boolean }>((resolve) => {
        resolveLoad = resolve;
      });

    act(() => result.current.setQueue([10, 20], 20, undefined, { startIndex: 0, totalCount: 4, loadNext }));
    let pending!: Promise<number | null>;
    act(() => {
      pending = result.current.goNext();
    });
    act(() => result.current.setQueue([90, 100], 90));
    resolveLoad({ items: [{ id: 30 }, { id: 40 }], hasMore: false });
    await act(async () => {
      await pending;
    });

    expect(await pending).toBeNull();
    expect(result.current.queue?.videoIds).toEqual([90, 100]);
    expect(result.current.currentId).toBe(90);
  });

  it("ignores a boundary response after local navigation changes", async () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(() => useVideoQueue(), { wrapper });
    let resolveLoad!: (value: { items: { id: number }[]; hasMore: boolean }) => void;
    const loadNext = () =>
      new Promise<{ items: { id: number }[]; hasMore: boolean }>((resolve) => {
        resolveLoad = resolve;
      });

    act(() => result.current.setQueue([10, 20], 20, undefined, { startIndex: 0, totalCount: 4, loadNext }));
    let pending!: Promise<number | null>;
    act(() => {
      pending = result.current.goNext();
    });
    act(() => {
      result.current.goToIndex(0);
    });
    resolveLoad({ items: [{ id: 30 }, { id: 40 }], hasMore: false });
    await act(async () => {
      await pending;
    });

    expect(await pending).toBeNull();
    expect(result.current.queue?.videoIds).toEqual([10, 20]);
    expect(result.current.currentId).toBe(10);
  });

  type Page = { items: { id: number }[]; hasMore: boolean };
  /** A loader whose first request stays pending until `release` is called; later requests answer at once. */
  const heldThenImmediate = (page: Page) => {
    let release!: () => void;
    const loader = vi
      .fn(async () => page)
      .mockImplementationOnce(
        () =>
          new Promise<Page>((resolve) => {
            release = () => resolve(page);
          }),
      );
    return { loader, release: () => release() };
  };

  it("loads the boundary again while a discarded load is still pending", async () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(() => useVideoQueue(), { wrapper });
    const { loader: loadNext, release } = heldThenImmediate({ items: [{ id: 30 }, { id: 40 }], hasMore: false });

    act(() => result.current.setQueue([10, 20], 20, undefined, { startIndex: 0, totalCount: 4, loadNext }));
    let discarded!: Promise<number | null>;
    act(() => {
      discarded = result.current.goNext();
    });
    act(() => {
      result.current.goToIndex(0);
    });
    act(() => {
      result.current.goToIndex(1);
    });
    let loaded: number | null = null;
    await act(async () => {
      loaded = await result.current.goNext();
    });

    expect(loaded).toBe(30);
    expect(loadNext).toHaveBeenCalledTimes(2);
    expect(result.current.currentId).toBe(30);

    release();
    await act(async () => {
      await discarded;
    });
    expect(await discarded).toBeNull();
    expect(result.current.queue?.videoIds).toEqual([10, 20, 30, 40]);
    expect(result.current.currentId).toBe(30);
  });

  it("loads a new queue's boundary while the replaced queue's load is still pending", async () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(() => useVideoQueue(), { wrapper });
    const { loader: oldLoadNext, release } = heldThenImmediate({ items: [{ id: 30 }], hasMore: false });
    const newLoadNext = vi.fn().mockResolvedValue({ items: [{ id: 110 }], hasMore: false });

    act(() => result.current.setQueue([10, 20], 20, undefined, { totalCount: 3, loadNext: oldLoadNext }));
    let discarded!: Promise<number | null>;
    act(() => {
      discarded = result.current.goNext();
    });
    act(() => result.current.setQueue([90, 100], 100, undefined, { totalCount: 3, loadNext: newLoadNext }));
    let loaded: number | null = null;
    await act(async () => {
      loaded = await result.current.goNext();
    });

    expect(loaded).toBe(110);
    expect(newLoadNext).toHaveBeenCalledOnce();

    release();
    await act(async () => {
      await discarded;
    });
    expect(await discarded).toBeNull();
    expect(result.current.queue?.videoIds).toEqual([90, 100, 110]);
    expect(result.current.currentId).toBe(110);
  });

  it("starts one boundary load at a time while the queue stays where it is", async () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(() => useVideoQueue(), { wrapper });
    const { loader: loadNext, release } = heldThenImmediate({ items: [{ id: 30 }], hasMore: false });

    act(() => result.current.setQueue([10, 20], 20, undefined, { totalCount: 3, loadNext }));
    let first!: Promise<number | null>;
    let second!: Promise<number | null>;
    act(() => {
      first = result.current.goNext();
    });
    act(() => {
      second = result.current.goNext();
    });
    release();
    await act(async () => {
      await Promise.all([first, second]);
    });

    expect(loadNext).toHaveBeenCalledOnce();
    expect(await first).toBe(30);
    expect(await second).toBeNull();
    expect(result.current.queue?.videoIds).toEqual([10, 20, 30]);
  });

  it("does not load the previous page while the next page is loading", async () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(() => useVideoQueue(), { wrapper });
    const { loader: loadNext, release } = heldThenImmediate({ items: [{ id: 30 }], hasMore: false });
    const loadPrevious = vi.fn().mockResolvedValue({ items: [{ id: 5 }], hasMore: false });

    act(() => result.current.setQueue([10], 10, undefined, { startIndex: 1, totalCount: 3, loadNext, loadPrevious }));
    let next!: Promise<number | null>;
    let previous!: Promise<number | null>;
    act(() => {
      next = result.current.goNext();
    });
    act(() => {
      previous = result.current.goPrevious();
    });
    release();
    await act(async () => {
      await Promise.all([next, previous]);
    });

    expect(loadPrevious).not.toHaveBeenCalled();
    expect(await previous).toBeNull();
    expect(result.current.queue?.videoIds).toEqual([10, 30]);
  });

  it.each(["resolves", "fails"] as const)(
    "keeps a retried load exclusive when the discarded load %s first",
    async (outcome) => {
      const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
      const { result } = renderHook(() => useVideoQueue(), { wrapper });
      const page: Page = { items: [{ id: 30 }, { id: 40 }], hasMore: false };
      let settleDiscarded!: () => void;
      let releaseRetry!: () => void;
      const loadNext = vi
        .fn(async () => page)
        .mockImplementationOnce(
          () =>
            new Promise<Page>((resolve, reject) => {
              settleDiscarded = () => (outcome === "resolves" ? resolve(page) : reject(new Error("request failed")));
            }),
        )
        .mockImplementationOnce(
          () =>
            new Promise<Page>((resolve) => {
              releaseRetry = () => resolve(page);
            }),
        );

      act(() => result.current.setQueue([10, 20], 20, undefined, { totalCount: 4, loadNext }));
      let discarded!: Promise<number | null>;
      act(() => {
        discarded = result.current.goNext();
      });
      act(() => {
        result.current.goToIndex(0);
      });
      act(() => {
        result.current.goToIndex(1);
      });
      let retry!: Promise<number | null>;
      act(() => {
        retry = result.current.goNext();
      });
      // The discarded load settles while the retry is still loading; it must not release the retry's claim.
      settleDiscarded();
      await act(async () => {
        await discarded;
      });
      let repeat!: Promise<number | null>;
      act(() => {
        repeat = result.current.goNext();
      });
      releaseRetry();
      await act(async () => {
        await Promise.all([retry, repeat]);
      });

      expect(await discarded).toBeNull();
      expect(await retry).toBe(30);
      expect(await repeat).toBeNull();
      expect(loadNext).toHaveBeenCalledTimes(2);
      expect(result.current.queue?.videoIds).toEqual([10, 20, 30, 40]);
    },
  );
});
