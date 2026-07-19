import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { StrictMode, useEffect, useLayoutEffect } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  ExtensionSlot,
  RouteRegistryProvider,
  useRouteRegistry,
  type SlotEntry,
} from "../router/RouteRegistry";

type Release = () => void;
type AcquireInteractionMode = () => Release;

interface TestSlotContext {
  currentTime: number;
  crash?: boolean;
  acquireInteractionMode?: AcquireInteractionMode;
}

interface EntryContextResource {
  context: Partial<TestSlotContext>;
  mount?: () => void | Release;
}

function RegisteredSlot({ entry }: { entry?: SlotEntry<TestSlotContext> }) {
  const { registerSlot } = useRouteRegistry();

  useLayoutEffect(() => {
    if (!entry) return;
    return registerSlot(entry);
  }, [entry, registerSlot]);

  return null;
}

function RegisteredSlots({ entries }: { entries: SlotEntry<TestSlotContext>[] }) {
  return (
    <>
      {entries.map((entry) => (
        <RegisteredSlot key={`${entry.extensionId}:${entry.id}`} entry={entry} />
      ))}
    </>
  );
}

function SlotHarness({
  entry,
  context,
  contextResetKey,
  createEntryContext,
  wrapperClassName,
}: {
  entry?: SlotEntry<TestSlotContext>;
  context: TestSlotContext;
  contextResetKey?: unknown;
  createEntryContext?: (entry: SlotEntry<TestSlotContext>) => EntryContextResource;
  wrapperClassName?: string;
}) {
  return (
    <RouteRegistryProvider>
      <RegisteredSlot entry={entry} />
      <ExtensionSlot
        slot="media-player-overlay"
        context={context}
        contextResetKey={contextResetKey}
        createEntryContext={createEntryContext}
        wrapperClassName={wrapperClassName}
      />
    </RouteRegistryProvider>
  );
}

function createOwnerLeaseManager() {
  const activeOwners = new WeakSet<object>();
  const ownerReleases = new WeakMap<object, Set<Release>>();
  const cleanupCounts = new WeakMap<object, number>();
  let activeLeaseCount = 0;

  const createEntryContext = vi.fn((entry: SlotEntry<TestSlotContext>): EntryContextResource => {
    const acquireInteractionMode: AcquireInteractionMode = () => {
      if (!activeOwners.has(entry)) return () => {};

      let released = false;
      activeLeaseCount += 1;
      const release = () => {
        if (released) return;
        released = true;
        activeLeaseCount -= 1;
        ownerReleases.get(entry)?.delete(release);
      };
      const releases = ownerReleases.get(entry) ?? new Set<Release>();
      releases.add(release);
      ownerReleases.set(entry, releases);
      return release;
    };

    return {
      context: { acquireInteractionMode },
      mount: () => {
        activeOwners.add(entry);
        return () => {
          activeOwners.delete(entry);
          cleanupCounts.set(entry, (cleanupCounts.get(entry) ?? 0) + 1);
          for (const release of [...(ownerReleases.get(entry) ?? [])]) release();
        };
      },
    };
  });

  return {
    createEntryContext,
    get activeLeaseCount() {
      return activeLeaseCount;
    },
    cleanupCount(entry: SlotEntry<TestSlotContext>) {
      return cleanupCounts.get(entry) ?? 0;
    },
  };
}

function requireAcquire(context: TestSlotContext) {
  if (!context.acquireInteractionMode) {
    throw new Error("Expected an owner-bound acquireInteractionMode function");
  }
  return context.acquireInteractionMode;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("ExtensionSlot registration lifecycles", () => {
  it("recovers a failed contribution only when the host context reset key changes", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const entry: SlotEntry<TestSlotContext> = {
      id: "recovering-overlay",
      extensionId: "animated-previews",
      slot: "media-player-overlay",
      render: (context) => {
        if (context.crash) throw new Error("item A is unsupported");
        return <div>overlay for time {context.currentTime}</div>;
      },
    };

    const view = render(
      <SlotHarness
        entry={entry}
        context={{ currentTime: 4, crash: true }}
        contextResetKey="item-a"
      />,
    );
    expect(await screen.findByText("Extension error (animated-previews)")).toBeInTheDocument();

    view.rerender(
      <SlotHarness
        entry={entry}
        context={{ currentTime: 18, crash: false }}
        contextResetKey="item-a"
      />,
    );
    expect(screen.getByText("Extension error (animated-previews)")).toBeInTheDocument();
    expect(screen.queryByText("overlay for time 18")).not.toBeInTheDocument();

    view.rerender(
      <SlotHarness
        entry={entry}
        context={{ currentTime: 18, crash: false }}
        contextResetKey="item-b"
      />,
    );
    expect(await screen.findByText("overlay for time 18")).toBeInTheDocument();
  });

  it("keeps same-id contributions isolated by extension owner", async () => {
    const alpha: SlotEntry<TestSlotContext> = {
      id: "player-tool",
      extensionId: "extension.alpha",
      slot: "media-player-overlay",
      render: () => <div>alpha overlay</div>,
    };
    const beta: SlotEntry<TestSlotContext> = {
      id: "player-tool",
      extensionId: "extension.beta",
      slot: "media-player-overlay",
      render: () => <div>beta overlay</div>,
    };

    const view = render(
      <RouteRegistryProvider>
        <RegisteredSlots entries={[alpha, beta]} />
        <ExtensionSlot slot="media-player-overlay" context={{ currentTime: 0 }} />
      </RouteRegistryProvider>,
    );

    expect(await screen.findByText("alpha overlay")).toBeInTheDocument();
    expect(screen.getByText("beta overlay")).toBeInTheDocument();

    view.rerender(
      <RouteRegistryProvider>
        <RegisteredSlots entries={[beta]} />
        <ExtensionSlot slot="media-player-overlay" context={{ currentTime: 0 }} />
      </RouteRegistryProvider>,
    );

    await waitFor(() => expect(screen.queryByText("alpha overlay")).not.toBeInTheDocument());
    expect(screen.getByText("beta overlay")).toBeInTheDocument();
  });

  it("preserves extension ownership, keeps entry context stable, and applies a host-selected wrapper class", async () => {
    const acquiredFunctions: AcquireInteractionMode[] = [];
    const entry: SlotEntry<TestSlotContext> = {
      id: "crop-overlay",
      extensionId: "animated-previews",
      slot: "media-player-overlay",
      render: (context) => {
        acquiredFunctions.push(requireAcquire(context));
        return <div data-testid="crop-overlay">time:{context.currentTime}</div>;
      },
    };
    const createEntryContext = vi.fn((registeredEntry: SlotEntry<TestSlotContext>): EntryContextResource => {
      expect(registeredEntry).toBe(entry);
      expect(registeredEntry.extensionId).toBe("animated-previews");
      return { context: { acquireInteractionMode: () => () => {} } };
    });

    const view = render(
      <SlotHarness
        entry={entry}
        context={{ currentTime: 4 }}
        createEntryContext={createEntryContext}
        wrapperClassName="contents player-overlay-contribution"
      />,
    );

    expect(await screen.findByText("time:4")).toBeInTheDocument();
    expect(screen.getByTestId("crop-overlay").parentElement).toHaveClass("contents", "player-overlay-contribution");
    const firstAcquire = acquiredFunctions.at(-1);

    view.rerender(
      <SlotHarness
        entry={entry}
        context={{ currentTime: 18 }}
        createEntryContext={createEntryContext}
        wrapperClassName="contents player-overlay-contribution"
      />,
    );

    expect(await screen.findByText("time:18")).toBeInTheDocument();
    expect(acquiredFunctions.at(-1)).toBe(firstAcquire);
    expect(createEntryContext).toHaveBeenCalledTimes(1);
  });

  it("runs owner cleanup on unregister and rejects acquisition through stale captured context", async () => {
    const manager = createOwnerLeaseManager();
    let staleAcquire: AcquireInteractionMode | undefined;
    let release: Release | undefined;
    const entry: SlotEntry<TestSlotContext> = {
      id: "crop-overlay",
      extensionId: "animated-previews",
      slot: "media-player-overlay",
      render: (context) => {
        staleAcquire = requireAcquire(context);
        return <button onClick={() => { release = staleAcquire?.(); }}>Acquire crop mode</button>;
      },
    };

    const view = render(
      <SlotHarness
        entry={entry}
        context={{ currentTime: 0 }}
        createEntryContext={manager.createEntryContext}
      />,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Acquire crop mode" }));
    expect(manager.activeLeaseCount).toBe(1);

    view.rerender(
      <SlotHarness
        context={{ currentTime: 0 }}
        createEntryContext={manager.createEntryContext}
      />,
    );

    await waitFor(() => expect(screen.queryByRole("button", { name: "Acquire crop mode" })).not.toBeInTheDocument());
    expect(manager.activeLeaseCount).toBe(0);
    expect(manager.cleanupCount(entry)).toBe(1);

    const staleRelease = staleAcquire?.();
    expect(manager.activeLeaseCount).toBe(0);
    staleRelease?.();
    release?.();
    expect(manager.activeLeaseCount).toBe(0);
  });

  it("cleans the old registration before activating a same-id upgrade", async () => {
    const manager = createOwnerLeaseManager();
    let oldAcquire: AcquireInteractionMode | undefined;
    let newAcquire: AcquireInteractionMode | undefined;

    function LeakyContribution({
      label,
      acquireInteractionMode,
      capture,
    }: {
      label: string;
      acquireInteractionMode: AcquireInteractionMode;
      capture: (acquire: AcquireInteractionMode) => void;
    }) {
      useEffect(() => {
        capture(acquireInteractionMode);
        acquireInteractionMode();
      }, [acquireInteractionMode, capture]);
      return <div>{label}</div>;
    }

    const captureOld = (acquire: AcquireInteractionMode) => { oldAcquire = acquire; };
    const captureNew = (acquire: AcquireInteractionMode) => { newAcquire = acquire; };
    const oldEntry: SlotEntry<TestSlotContext> = {
      id: "crop-overlay",
      extensionId: "animated-previews",
      slot: "media-player-overlay",
      resetKey: 1,
      render: (context) => (
        <LeakyContribution label="old crop tool" acquireInteractionMode={requireAcquire(context)} capture={captureOld} />
      ),
    };
    const newEntry: SlotEntry<TestSlotContext> = {
      id: "crop-overlay",
      extensionId: "animated-previews",
      slot: "media-player-overlay",
      resetKey: 2,
      render: (context) => (
        <LeakyContribution label="new crop tool" acquireInteractionMode={requireAcquire(context)} capture={captureNew} />
      ),
    };

    const view = render(
      <SlotHarness
        entry={oldEntry}
        context={{ currentTime: 0 }}
        createEntryContext={manager.createEntryContext}
      />,
    );
    await waitFor(() => expect(manager.activeLeaseCount).toBe(1));
    expect(await screen.findByText("old crop tool")).toBeInTheDocument();

    view.rerender(
      <SlotHarness
        entry={newEntry}
        context={{ currentTime: 0 }}
        createEntryContext={manager.createEntryContext}
      />,
    );

    expect(await screen.findByText("new crop tool")).toBeInTheDocument();
    await waitFor(() => expect(manager.activeLeaseCount).toBe(1));
    expect(manager.cleanupCount(oldEntry)).toBe(1);
    expect(manager.cleanupCount(newEntry)).toBe(0);

    const oldRelease = oldAcquire?.();
    expect(manager.activeLeaseCount).toBe(1);
    oldRelease?.();

    const newRelease = newAcquire?.();
    expect(manager.activeLeaseCount).toBe(2);
    newRelease?.();
    expect(manager.activeLeaseCount).toBe(1);
  });

  it("cleans an active owner when its contribution crashes", async () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const manager = createOwnerLeaseManager();
    let staleAcquire: AcquireInteractionMode | undefined;

    function CrashingContribution({ context }: { context: TestSlotContext }) {
      const acquire = requireAcquire(context);
      useEffect(() => {
        staleAcquire = acquire;
        acquire();
      }, [acquire]);
      if (context.crash) throw new Error("crop tool crashed");
      return <div>crop editor active</div>;
    }

    const entry: SlotEntry<TestSlotContext> = {
      id: "crop-overlay",
      extensionId: "animated-previews",
      slot: "media-player-overlay",
      render: (context) => <CrashingContribution context={context} />,
    };

    const view = render(
      <SlotHarness
        entry={entry}
        context={{ currentTime: 0, crash: false }}
        createEntryContext={manager.createEntryContext}
      />,
    );
    expect(await screen.findByText("crop editor active")).toBeInTheDocument();
    await waitFor(() => expect(manager.activeLeaseCount).toBe(1));

    view.rerender(
      <SlotHarness
        entry={entry}
        context={{ currentTime: 0, crash: true }}
        createEntryContext={manager.createEntryContext}
      />,
    );

    expect(await screen.findByText("Extension error (animated-previews)")).toBeInTheDocument();
    await waitFor(() => expect(manager.activeLeaseCount).toBe(0));
    expect(manager.cleanupCount(entry)).toBe(1);
    staleAcquire?.();
    expect(manager.activeLeaseCount).toBe(0);
    expect(consoleError).toHaveBeenCalled();
  });

  it("reactivates an entry resource after StrictMode's lifecycle replay and cleans it on unmount", async () => {
    const manager = createOwnerLeaseManager();
    const entry: SlotEntry<TestSlotContext> = {
      id: "strict-overlay",
      extensionId: "animated-previews",
      slot: "media-player-overlay",
      render: (context) => (
        <button onClick={() => requireAcquire(context)()}>Acquire strict crop mode</button>
      ),
    };

    const view = render(
      <StrictMode>
        <SlotHarness
          entry={entry}
          context={{ currentTime: 0 }}
          createEntryContext={manager.createEntryContext}
        />
      </StrictMode>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Acquire strict crop mode" }));
    expect(manager.activeLeaseCount).toBe(1);
    expect(manager.cleanupCount(entry)).toBeGreaterThanOrEqual(1);

    view.unmount();
    expect(manager.activeLeaseCount).toBe(0);
    expect(manager.cleanupCount(entry)).toBeGreaterThanOrEqual(2);
  });
});
