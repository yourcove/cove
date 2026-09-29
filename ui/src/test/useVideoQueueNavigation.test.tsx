import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Video } from "../api/types";
import { VideoQueueProvider, useVideoQueue } from "../state/VideoQueueContext";
import { useVideoQueueNavigation } from "../hooks/useVideoQueueNavigation";

vi.mock("../state/AppConfigContext", () => ({
  useOptionalAppConfig: () => ({ config: { ui: { continuePlaylistDefault: true } } }),
}));

const video = (id: number, title: string): Video =>
  ({
    id,
    title,
    updatedAt: "2026-01-01T00:00:00Z",
    files: [],
  }) as unknown as Video;

describe("useVideoQueueNavigation", () => {
  beforeEach(() => sessionStorage.clear());

  it("builds a queue from the active page and loads the next page with the same filter", async () => {
    const onNavigate = vi.fn();
    const queryPage = vi.fn().mockResolvedValue({ items: [video(30, "Third")], totalCount: 3 });
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(
      () => ({
        navigation: useVideoQueueNavigation({
          items: [video(10, "First"), video(20, "Second")],
          filter: { page: 1, perPage: 2, sort: "title", direction: "asc", q: "needle" },
          totalCount: 3,
          infinitePageSize: false,
          queryPage,
          onNavigate,
        }),
        queue: useVideoQueue(),
      }),
      { wrapper },
    );

    act(() => result.current.navigation.openVideo(20));
    expect(onNavigate).toHaveBeenCalledWith({ page: "video", id: 20 });
    expect(result.current.queue.currentPosition).toBe(2);
    expect(result.current.queue.queueLength).toBe(3);
    expect(result.current.queue.autoplay).toBe(true);

    await act(async () => {
      await result.current.queue.goNext();
    });
    expect(queryPage).toHaveBeenCalledWith({ page: 2, perPage: 2, sort: "title", direction: "asc", q: "needle" });
    expect(result.current.queue.currentId).toBe(30);
  });

  // The Videos list can open a video before its separately loaded total arrives.
  it("follows a full page into the next one while the list total is still unknown", async () => {
    const queryPage = vi
      .fn()
      .mockResolvedValueOnce({ items: [video(30, "Third"), video(40, "Fourth")], totalCount: 5 })
      .mockResolvedValueOnce({ items: [video(50, "Fifth")], totalCount: 5 });
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(
      () => ({
        navigation: useVideoQueueNavigation({
          items: [video(10, "First"), video(20, "Second")],
          filter: { page: 1, perPage: 2 },
          totalCount: undefined,
          infinitePageSize: false,
          queryPage,
          onNavigate: vi.fn(),
        }),
        queue: useVideoQueue(),
      }),
      { wrapper },
    );

    act(() => result.current.navigation.openVideo(20));
    await act(async () => {
      await result.current.queue.goNext();
    });

    expect(result.current.queue.currentId).toBe(30);
    expect(result.current.queue.queueLength).toBe(5);
  });

  it("counts the queue through the opened page while the list total is still unknown", () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(
      () => ({
        navigation: useVideoQueueNavigation({
          items: [video(50, "Fifth"), video(60, "Sixth")],
          filter: { page: 3, perPage: 2 },
          totalCount: undefined,
          infinitePageSize: false,
          queryPage: vi.fn(),
          onNavigate: vi.fn(),
        }),
        queue: useVideoQueue(),
      }),
      { wrapper },
    );

    act(() => result.current.navigation.openVideo(50));

    expect(result.current.queue.currentPosition).toBe(5);
    expect(result.current.queue.queueLength).toBe(6);
  });

  it("never counts fewer videos than the queue already holds", () => {
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(
      () => ({
        navigation: useVideoQueueNavigation({
          items: [video(50, "Fifth"), video(60, "Sixth")],
          filter: { page: 3, perPage: 2 },
          totalCount: 4,
          infinitePageSize: false,
          queryPage: vi.fn(),
          onNavigate: vi.fn(),
        }),
        queue: useVideoQueue(),
      }),
      { wrapper },
    );

    act(() => result.current.navigation.openVideo(60));

    expect(result.current.queue.currentPosition).toBe(6);
    expect(result.current.queue.queueLength).toBe(6);
  });

  it("ends a queue opened without a total when the next page is empty", async () => {
    const queryPage = vi.fn().mockResolvedValue({ items: [], totalCount: 2 });
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(
      () => ({
        navigation: useVideoQueueNavigation({
          items: [video(10, "First"), video(20, "Second")],
          filter: { page: 1, perPage: 2 },
          totalCount: undefined,
          infinitePageSize: false,
          queryPage,
          onNavigate: vi.fn(),
        }),
        queue: useVideoQueue(),
      }),
      { wrapper },
    );

    act(() => result.current.navigation.openVideo(20));
    await act(async () => {
      await result.current.queue.goNext();
    });
    await act(async () => {
      await result.current.queue.goNext();
    });

    expect(result.current.queue.currentId).toBe(20);
    expect(queryPage).toHaveBeenCalledTimes(1);
  });

  it("does not look past a short page while the list total is still unknown", async () => {
    const queryPage = vi.fn();
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(
      () => ({
        navigation: useVideoQueueNavigation({
          items: [video(10, "First")],
          filter: { page: 1, perPage: 2 },
          totalCount: undefined,
          infinitePageSize: false,
          queryPage,
          onNavigate: vi.fn(),
        }),
        queue: useVideoQueue(),
      }),
      { wrapper },
    );

    act(() => result.current.navigation.openVideo(10));
    await act(async () => {
      await result.current.queue.goNext();
    });

    expect(queryPage).not.toHaveBeenCalled();
  });

  it("intercepts only video routes", () => {
    const onNavigate = vi.fn();
    const wrapper = ({ children }: { children: ReactNode }) => <VideoQueueProvider>{children}</VideoQueueProvider>;
    const { result } = renderHook(
      () =>
        useVideoQueueNavigation({
          items: [video(10, "First")],
          filter: { page: 1, perPage: 20 },
          totalCount: 1,
          infinitePageSize: false,
          queryPage: vi.fn(),
          onNavigate,
        }),
      { wrapper },
    );

    act(() => result.current.navigateFromList({ page: "performer", id: 5 }));
    expect(onNavigate).toHaveBeenCalledWith({ page: "performer", id: 5 });
  });
});
