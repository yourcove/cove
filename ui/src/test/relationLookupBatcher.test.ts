import { describe, expect, it, vi } from "vitest";
import type { ResolveScrapeRelationsRequest, ResolveScrapeRelationsResult } from "../api/types";
import { createRelationLookupBatcher } from "../utils/relationLookupBatcher";

// Answers every requested name as a match on itself, the way the server echoes its inputs.
const echo = (request: ResolveScrapeRelationsRequest): ResolveScrapeRelationsResult => ({
  tags: request.tags.map((name) => ({ input: name, matchedName: name })),
  performers: request.performers.map((name) => ({ input: name, matchedName: name })),
  studios: (request.studios ?? []).map((name) => ({ input: name, matchedName: name })),
});

const BYTE_ORDER_MARK = String.fromCharCode(0xfeff);

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe("createRelationLookupBatcher", () => {
  it("sends lookups made together as one request and gives each caller only its own matches", async () => {
    const send = vi.fn(async (request: ResolveScrapeRelationsRequest) => echo(request));
    const lookup = createRelationLookupBatcher(send);

    const [first, second] = await Promise.all([
      lookup({ tags: ["Anal", "Outdoor"], performers: ["Jane"], studios: ["Studio A"] }),
      lookup({ tags: ["Outdoor", "Kissing"], performers: [], studios: [] }),
    ]);

    expect(send).toHaveBeenCalledOnce();
    expect(send.mock.calls[0][0]).toEqual({
      tags: ["Anal", "Outdoor", "Kissing"],
      performers: ["Jane"],
      studios: ["Studio A"],
    });
    expect(first).toEqual({
      tags: [
        { input: "Anal", matchedName: "Anal" },
        { input: "Outdoor", matchedName: "Outdoor" },
      ],
      performers: [{ input: "Jane", matchedName: "Jane" }],
      studios: [{ input: "Studio A", matchedName: "Studio A" }],
    });
    expect(second).toEqual({
      tags: [
        { input: "Outdoor", matchedName: "Outdoor" },
        { input: "Kissing", matchedName: "Kissing" },
      ],
      performers: [],
      studios: [],
    });
  });

  it("asks every spelling and gives each caller only the matches for its own spellings", async () => {
    // The server keeps a byte-order mark the browser's trim drops, so only the plain spelling matches; the
    // browser would call both one name, but the marked one must not borrow the plain one's match.
    const send = vi.fn(async (request: ResolveScrapeRelationsRequest) => ({
      tags: request.tags
        .filter((name) => name.trim().toLowerCase() === "anal" && !name.startsWith(BYTE_ORDER_MARK))
        .map((name) => ({ input: name, matchedName: "Anal" })),
      performers: [],
      studios: [],
    }));
    const lookup = createRelationLookupBatcher(send);

    const [withMark, plain, lower] = await Promise.all([
      lookup({ tags: [`${BYTE_ORDER_MARK}Anal`], performers: [], studios: [] }),
      lookup({ tags: ["Anal"], performers: [], studios: [] }),
      lookup({ tags: [" anal "], performers: [], studios: [] }),
    ]);

    expect(send.mock.calls[0][0].tags).toEqual([`${BYTE_ORDER_MARK}Anal`, "Anal", " anal "]);
    expect(withMark.tags).toEqual([]);
    expect(plain.tags).toEqual([{ input: "Anal", matchedName: "Anal" }]);
    expect(lower.tags).toEqual([{ input: " anal ", matchedName: "Anal" }]);
  });

  it("sends lookups made while a request is out without waiting for it", async () => {
    const hung = deferred<ResolveScrapeRelationsResult>();
    const send = vi.fn((request: ResolveScrapeRelationsRequest) =>
      send.mock.calls.length === 1 ? hung.promise : Promise.resolve(echo(request)),
    );
    const lookup = createRelationLookupBatcher(send);

    const first = lookup({ tags: ["A"], performers: [], studios: [] });
    await vi.waitFor(() => expect(send).toHaveBeenCalledOnce());
    const [second, third] = await Promise.all([
      lookup({ tags: ["B"], performers: [], studios: [] }),
      lookup({ tags: ["C"], performers: [], studios: [] }),
    ]);

    expect(send).toHaveBeenCalledTimes(2);
    expect(send.mock.calls[1][0].tags).toEqual(["B", "C"]);
    expect(second.tags).toEqual([{ input: "B", matchedName: "B" }]);
    expect(third.tags).toEqual([{ input: "C", matchedName: "C" }]);
    hung.resolve(echo(send.mock.calls[0][0]));
    expect((await first).tags).toEqual([{ input: "A", matchedName: "A" }]);
  });

  it("asks again for each caller on its own when a name conflict fails the shared request", async () => {
    // Two legacy performers sharing a name make the server refuse any request that names them.
    const conflict = new Error(
      'API Error 409: {"code":"ENTITY_NAME_CONFLICT","message":"Two performers are named Shared Name.","entityType":"performer"}',
    );
    const send = vi.fn(async (request: ResolveScrapeRelationsRequest) => {
      if (request.performers.includes("Shared Name")) throw conflict;
      return echo(request);
    });
    const lookup = createRelationLookupBatcher(send);

    const results = await Promise.allSettled([
      lookup({ tags: ["Fine"], performers: [], studios: [] }),
      lookup({ tags: [], performers: ["Shared Name"], studios: [] }),
      lookup({ tags: ["Also fine"], performers: [], studios: [] }),
    ]);

    expect(results).toEqual([
      { status: "fulfilled", value: { tags: [{ input: "Fine", matchedName: "Fine" }], performers: [], studios: [] } },
      { status: "rejected", reason: conflict },
      {
        status: "fulfilled",
        value: { tags: [{ input: "Also fine", matchedName: "Also fine" }], performers: [], studios: [] },
      },
    ]);
    expect(send).toHaveBeenCalledTimes(4);
  });

  it("fails every caller without asking again when the shared request fails for another reason", async () => {
    const failure = new Error("API Error 500: ");
    const send = vi.fn(async () => {
      throw failure;
    });
    const lookup = createRelationLookupBatcher(send);

    const results = await Promise.allSettled([
      lookup({ tags: ["A"], performers: [], studios: [] }),
      lookup({ tags: ["B"], performers: [], studios: [] }),
    ]);

    expect(results).toEqual([
      { status: "rejected", reason: failure },
      { status: "rejected", reason: failure },
    ]);
    expect(send).toHaveBeenCalledOnce();
  });

  it("rejects a lone caller with the request's own error without asking again", async () => {
    const failure = new Error("offline");
    const send = vi.fn(async () => {
      throw failure;
    });
    const lookup = createRelationLookupBatcher(send);

    await expect(lookup({ tags: ["A"], performers: [], studios: [] })).rejects.toBe(failure);
    expect(send).toHaveBeenCalledOnce();
  });

  it("leaves studios absent when the server predates studio lookups", async () => {
    const lookup = createRelationLookupBatcher(async (request) => ({
      tags: request.tags.map((name) => ({ input: name, matchedName: name })),
      performers: [],
    }));

    const result = await lookup({ tags: ["A"], performers: [], studios: ["Studio"] });

    expect(result).toEqual({ tags: [{ input: "A", matchedName: "A" }], performers: [], studios: undefined });
  });
});
