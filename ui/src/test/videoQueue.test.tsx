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
});
