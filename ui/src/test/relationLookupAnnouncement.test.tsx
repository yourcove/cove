import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useRelationLookupAnnouncement } from "../components/TaggerShared";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function Lookup({ id, answer }: { id: string; answer: () => Promise<object> }) {
  useQuery({ queryKey: ["tagger-resolve-relations", id], queryFn: answer, retry: false });
  return null;
}

function Announcer() {
  const announcement = useRelationLookupAnnouncement("tagger-resolve-relations", "videos");
  return <div data-testid="said">{announcement ? `${announcement.id}:${announcement.text}` : ""}</div>;
}

describe("useRelationLookupAnnouncement", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  let all: Record<string, ReturnType<typeof deferred<object>>> = {};
  beforeEach(() => {
    all = {};
  });
  const setUp = (lookups: Record<string, ReturnType<typeof deferred<object>>>) => {
    const client = new QueryClient();
    const view = (ids: string[]) => (
      <QueryClientProvider client={client}>
        <Announcer />
        {ids.map((id) => (
          <Lookup key={id} id={id} answer={() => all[id].promise} />
        ))}
      </QueryClientProvider>
    );
    // Lookups added later are read from the same record.
    Object.assign(all, lookups);
    const result = render(view(Object.keys(lookups)));
    return { rerender: (ids: string[]) => result.rerender(view(ids)), client };
  };
  const said = () => screen.getByTestId("said").textContent;
  const settle = async (ms = 1000) => act(async () => void (await vi.advanceTimersByTimeAsync(ms)));

  it("says once that checking finished when rows finish one after another", async () => {
    // The second row's search finishes, and its lookup starts, just after the first row's lookup answered.
    const lookups = { a: deferred<object>(), b: deferred<object>() };
    const { rerender } = setUp({ a: lookups.a });
    all.b = lookups.b;
    await settle(10);
    lookups.a.resolve({});
    await settle(100);
    rerender(["a", "b"]);
    await settle(10);
    lookups.b.resolve({});
    await settle();
    expect(said()).toBe("1:Finished checking your library.");
  });

  it("says nothing when the only waiting row goes away", async () => {
    const lookups = { a: deferred<object>() };
    const { rerender } = setUp(lookups);
    await settle(10);
    rerender([]);
    await settle();
    expect(said()).toBe("");
  });

  it("says that some videos could not be checked, and says it again when a retry fails again", async () => {
    const lookups = { a: deferred<object>() };
    const { client } = setUp(lookups);
    await settle(10);
    lookups.a.reject(new Error("failed"));
    await settle();
    expect(said()).toMatch(/^1:Couldn't check some videos/);

    lookups.a = all.a = deferred<object>();
    void client.refetchQueries({ queryKey: ["tagger-resolve-relations"] });
    await settle(10);
    lookups.a.reject(new Error("failed again"));
    await settle();
    expect(said()).toMatch(/^2:Couldn't check some videos/);
  });
});
