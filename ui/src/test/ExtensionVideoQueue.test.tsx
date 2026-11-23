import { act, fireEvent, render, renderHook, screen } from "@testing-library/react";
import { useEffect, type ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
// Extensions reach the queue through the runtime barrel, as a namespace so the lookup also works on
// hosts that predate the hook.
import * as components from "../generated/extensions/runtime/v1/components";
import type { VideoQueueHandle, VideoQueueItem } from "../generated/extensions/runtime/v1/components";
import { VideoQueueProvider, useVideoQueue } from "../state/VideoQueueContext";

const QUEUE_STORAGE_KEY = "cove-video-queue";

const appConfig = vi.hoisted(() => ({ continuePlaylistDefault: false }));

vi.mock("../state/AppConfigContext", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../state/AppConfigContext")>()),
  useOptionalAppConfig: () => ({ config: { ui: { continuePlaylistDefault: appConfig.continuePlaylistDefault } } }),
}));

function withQueue({ children }: { children: ReactNode }) {
  return <VideoQueueProvider>{children}</VideoQueueProvider>;
}

/** The host's own view of the queue next to the one an extension gets. */
function useBothQueues() {
  return { host: useVideoQueue(), extension: components.useVideoQueue() as VideoQueueHandle };
}

function ExtensionQueueButton({ items }: { items: VideoQueueItem[] }) {
  const queue = components.useVideoQueue();
  if (!queue) return <span>no queue</span>;
  return (
    <div>
      <button
        type="button"
        onClick={() =>
          queue.setQueue(
            items.map((item) => item.id),
            items[1].id,
            items,
            { source: "sample:list" },
          )
        }
      >
        Open queue
      </button>
      <output>
        {queue.queue
          ? `${queue.queue.videoIds.join(",")} at ${queue.currentId} from ${queue.queue.source}`
          : "empty queue"}
      </output>
    </div>
  );
}

afterEach(() => {
  appConfig.continuePlaylistDefault = false;
  sessionStorage.clear();
});

describe("extension video queue", () => {
  it("lets an extension component read and replace the queue in place", () => {
    render(
      <ExtensionQueueButton items={[{ id: 10, title: "First" }, { id: 20, subtitle: "Untriaged" }, { id: 30 }]} />,
      { wrapper: withQueue },
    );

    expect(screen.getByRole("status").textContent).toBe("empty queue");
    fireEvent.click(screen.getByRole("button", { name: "Open queue" }));

    expect(screen.getByRole("status").textContent).toBe("10,20,30 at 20 from sample:list");
  });

  it("returns null outside the queue provider", () => {
    render(<ExtensionQueueButton items={[]} />);

    expect(screen.getByText("no queue")).toBeTruthy();
  });

  it("exposes only the documented members", () => {
    const { result } = renderHook(() => components.useVideoQueue(), { wrapper: withQueue });
    act(() => result.current!.setQueue([10, 20], 10, [{ id: 10 }], { source: "sample:list" }));

    expect(Object.keys(result.current!).sort()).toEqual([
      "autoplay",
      "clearQueue",
      "currentId",
      "queue",
      "setQueue",
      "updateItems",
    ]);
    expect(Object.keys(result.current!.queue!).sort()).toEqual(["source", "videoIds"]);
  });

  it("shows the queue an extension opened to the host, and clears it", () => {
    const { result } = renderHook(useBothQueues, { wrapper: withQueue });

    act(() =>
      result.current.extension.setQueue([10, 20], 20, [{ id: 20, title: "Second", subtitle: "Untriaged" }], {
        autoplay: true,
      }),
    );

    expect(result.current.host.currentId).toBe(20);
    expect(result.current.host.currentPosition).toBe(2);
    expect(result.current.host.autoplay).toBe(true);
    expect(result.current.host.queueItems).toEqual([{ id: 10 }, { id: 20, title: "Second", subtitle: "Untriaged" }]);

    act(() => result.current.extension.clearQueue());

    expect(result.current.host.queue).toBeNull();
    expect(result.current.extension.queue).toBeNull();
    expect(result.current.extension.currentId).toBeNull();
  });

  it("clears the queue when an extension sets an empty one", () => {
    const { result } = renderHook(useBothQueues, { wrapper: withQueue });
    act(() => result.current.extension.setQueue([10], 10));

    act(() => result.current.extension.setQueue([], 10));

    expect(result.current.host.queue).toBeNull();
  });

  it("keeps the identity of its functions while the queue changes", () => {
    const { result } = renderHook(useBothQueues, { wrapper: withQueue });
    const { setQueue, updateItems, clearQueue } = result.current.extension;

    act(() => result.current.extension.setQueue([10, 20], 10, undefined, { autoplay: true }));
    act(() => result.current.host.goToIndex(1));
    act(() => result.current.host.toggleAutoplay());

    expect(result.current.extension.currentId).toBe(20);
    expect(result.current.extension.setQueue).toBe(setQueue);
    expect(result.current.extension.updateItems).toBe(updateItems);
    expect(result.current.extension.clearQueue).toBe(clearQueue);
  });

  it("keeps the identity of setQueue when the auto-advance setting changes, and applies the new setting", () => {
    const { result, rerender } = renderHook(useBothQueues, { wrapper: withQueue });
    const { setQueue } = result.current.extension;

    appConfig.continuePlaylistDefault = true;
    rerender();

    expect(result.current.extension.setQueue).toBe(setQueue);
    act(() => setQueue([10, 20], 10));
    expect(result.current.extension.autoplay).toBe(true);
  });

  it("does not reopen a queue kept in step by an effect when the position changes", () => {
    const ids = [10, 20, 30];
    const opened = vi.fn();
    function KeepQueue() {
      const setQueue = components.useVideoQueue()?.setQueue;
      useEffect(() => {
        opened();
        setQueue?.(ids, ids[0]);
      }, [setQueue]);
      return null;
    }
    const { result } = renderHook(() => useVideoQueue(), {
      wrapper: ({ children }: { children: ReactNode }) => (
        <VideoQueueProvider>
          <KeepQueue />
          {children}
        </VideoQueueProvider>
      ),
    });

    act(() => {
      result.current.goToIndex(2);
    });

    expect(result.current.currentId).toBe(30);
    expect(opened).toHaveBeenCalledOnce();
  });

  it("updates queued items in place, keeping the position and the pages still to load", async () => {
    const { result } = renderHook(useBothQueues, { wrapper: withQueue });
    let finishLoading: (page: { items: VideoQueueItem[]; hasMore: boolean }) => void = () => {};
    const loadNext = vi.fn(
      () => new Promise<{ items: VideoQueueItem[]; hasMore: boolean }>((r) => (finishLoading = r)),
    );
    act(() =>
      result.current.host.setQueue(
        [10, 20],
        20,
        [
          { id: 10, title: "First", subtitle: "Untriaged", imagePath: "/first.jpg" },
          { id: 20, title: "Second", subtitle: "Untriaged", imagePath: "/second.jpg" },
        ],
        { startIndex: 40, totalCount: 43, autoplay: true, loadNext },
      ),
    );
    const videoIds = result.current.extension.queue!.videoIds;

    // The next page is already loading when the items change; it must still be applied.
    let next: Promise<number | null> = Promise.resolve(null);
    act(() => {
      next = result.current.host.goNext();
    });
    act(() =>
      result.current.extension.updateItems([
        { id: 20, subtitle: "Import" },
        { id: 10, imagePath: null },
        { id: 99, title: "Not queued" },
      ]),
    );

    // A field left out keeps its value and null clears it; a video that is not queued is ignored.
    expect(result.current.host.queueItems).toEqual([
      { id: 10, title: "First", subtitle: "Untriaged", imagePath: null },
      { id: 20, title: "Second", subtitle: "Import", imagePath: "/second.jpg" },
    ]);
    expect(result.current.extension.queue!.videoIds).toBe(videoIds);
    expect(result.current.host.currentId).toBe(20);
    expect(result.current.host.currentPosition).toBe(42);
    expect(result.current.host.queueLength).toBe(43);
    expect(result.current.host.autoplay).toBe(true);

    await act(async () => {
      finishLoading({ items: [{ id: 30 }], hasMore: false });
      await next;
    });
    expect(result.current.host.currentId).toBe(30);
    expect(result.current.host.queueItems[1]).toEqual({
      id: 20,
      title: "Second",
      subtitle: "Import",
      imagePath: "/second.jpg",
    });
  });

  it("leaves the queue untouched when an update changes nothing", () => {
    const { result } = renderHook(useBothQueues, { wrapper: withQueue });
    act(() => result.current.extension.setQueue([10, 20], 10, [{ id: 10, title: "First" }]));
    const queue = result.current.host.queue;

    act(() => result.current.extension.updateItems([{ id: 10, title: "First" }, { id: 20 }, { id: 99, title: "x" }]));

    expect(result.current.host.queue).toBe(queue);
  });

  it("keeps an item update made just before the host moves to another video", () => {
    const { result } = renderHook(useBothQueues, { wrapper: withQueue });
    act(() => result.current.extension.setQueue([10, 20], 10, [{ id: 20, subtitle: "Untriaged" }]));

    act(() => {
      result.current.extension.updateItems([{ id: 20, subtitle: "Import" }]);
      result.current.host.goToIndex(1);
    });

    expect(result.current.host.currentId).toBe(20);
    expect(result.current.host.queueItems[1]).toEqual({ id: 20, subtitle: "Import" });
  });

  it("takes only well-formed ids, item fields and options, and copies what it is given", () => {
    const { result } = renderHook(useBothQueues, { wrapper: withQueue });
    const ids = [10, 20];
    const loadNext = vi.fn();

    act(() =>
      result.current.extension.setQueue(
        ids,
        20,
        [{ id: 10, title: { text: "First" }, subtitle: "Untriaged", extra: true }, null, { id: "20" }] as never,
        { source: 5, autoplay: "yes", loadNext, startIndex: 40, totalCount: 90 } as never,
      ),
    );
    ids.push(30);

    expect(result.current.host.queue!.videoIds).toEqual([10, 20]);
    expect(result.current.host.queueItems).toEqual([{ id: 10, subtitle: "Untriaged" }, { id: 20 }]);
    expect(result.current.extension.queue!.source).toBeUndefined();
    expect(result.current.host.autoplay).toBe(false);
    expect(result.current.host.currentPosition).toBe(2);
    expect(result.current.host.queueLength).toBe(2);
    expect(result.current.host.hasNext).toBe(false);
  });

  it("labels a queue with its source until another queue replaces it", () => {
    const { result } = renderHook(useBothQueues, { wrapper: withQueue });

    act(() => result.current.extension.setQueue([10, 20], 10, undefined, { source: "sample:list" }));
    expect(result.current.extension.queue).toEqual({ videoIds: [10, 20], source: "sample:list" });

    act(() => result.current.host.goToIndex(1));
    expect(result.current.extension.queue!.source).toBe("sample:list");

    act(() => result.current.host.setQueue([10, 20], 10));
    expect(result.current.extension.queue).toEqual({ videoIds: [10, 20], source: undefined });
  });

  it("restores the source with the queue after a reload", () => {
    const first = renderHook(() => components.useVideoQueue(), { wrapper: withQueue });
    act(() => first.result.current!.setQueue([10, 20], 20, undefined, { source: "sample:list" }));
    expect(JSON.parse(sessionStorage.getItem(QUEUE_STORAGE_KEY)!).source).toBe("sample:list");
    first.unmount();

    const restored = renderHook(() => components.useVideoQueue(), { wrapper: withQueue });

    expect(restored.result.current!.queue).toEqual({ videoIds: [10, 20], source: "sample:list" });
    expect(restored.result.current!.currentId).toBe(20);
  });

  it("starts a queue with the user's auto-advance setting unless the opener chooses", () => {
    appConfig.continuePlaylistDefault = true;
    const { result } = renderHook(() => components.useVideoQueue(), { wrapper: withQueue });

    act(() => result.current!.setQueue([10, 20], 10));
    expect(result.current!.autoplay).toBe(true);

    act(() => result.current!.setQueue([10, 20], 10, undefined, { autoplay: false }));
    expect(result.current!.autoplay).toBe(false);

    // As for the queues Cove opens itself, a new queue does not inherit the last one's choice.
    act(() => result.current!.setQueue([30, 40], 30));
    expect(result.current!.autoplay).toBe(true);
  });
});
