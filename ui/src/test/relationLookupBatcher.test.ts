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

  it("asks every spelling, and (a known limitation) shares a match between spellings the browser calls one name", async () => {
    // The server keeps a byte-order mark the browser's trim drops, so only the plain spelling matches.
    // Each caller is still given the matches for its names as the browser keys them, which is how the row
    // reads them, so the marked spelling is shown as matched although the server would not match it.
    const send = vi.fn(async (request: ResolveScrapeRelationsRequest) => ({
      tags: request.tags.filter((name) => name === "Anal").map((name) => ({ input: name, matchedName: name })),
      performers: [],
      studios: [],
    }));
    const lookup = createRelationLookupBatcher(send);

    const [withMark, plain] = await Promise.all([
      lookup({ tags: [`${BYTE_ORDER_MARK}Anal`], performers: [], studios: [] }),
      lookup({ tags: ["Anal"], performers: [], studios: [] }),
    ]);

    expect(send.mock.calls[0][0].tags).toEqual([`${BYTE_ORDER_MARK}Anal`, "Anal"]);
    expect(plain.tags).toEqual([{ input: "Anal", matchedName: "Anal" }]);
    expect(withMark.tags).toEqual([{ input: "Anal", matchedName: "Anal" }]);
  });

  it("holds lookups made while a request is out and sends them together when it answers", async () => {
    const answers = [deferred<ResolveScrapeRelationsResult>(), deferred<ResolveScrapeRelationsResult>()];
    const send = vi.fn((_request: ResolveScrapeRelationsRequest) => answers[send.mock.calls.length - 1].promise);
    const lookup = createRelationLookupBatcher(send);

    const first = lookup({ tags: ["A"], performers: [], studios: [] });
    await vi.waitFor(() => expect(send).toHaveBeenCalledOnce());
    const second = lookup({ tags: ["B"], performers: [], studios: [] });
    const third = lookup({ tags: ["C"], performers: [], studios: [] });
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(send).toHaveBeenCalledOnce();

    answers[0].resolve(echo(send.mock.calls[0][0]));
    expect((await first).tags).toEqual([{ input: "A", matchedName: "A" }]);
    await vi.waitFor(() => expect(send).toHaveBeenCalledTimes(2));
    expect(send.mock.calls[1][0].tags).toEqual(["B", "C"]);
    answers[1].resolve(echo(send.mock.calls[1][0]));
    expect((await second).tags).toEqual([{ input: "B", matchedName: "B" }]);
    expect((await third).tags).toEqual([{ input: "C", matchedName: "C" }]);
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

  it("sends lookups that waited behind a failed request once its callers have asked again", async () => {
    const conflict = new Error('API Error 409: {"code":"ENTITY_NAME_CONFLICT","message":"Conflict."}');
    const first = deferred<ResolveScrapeRelationsResult>();
    const send = vi.fn((request: ResolveScrapeRelationsRequest) => {
      if (send.mock.calls.length === 1) return first.promise;
      return request.performers.includes("Shared Name") ? Promise.reject(conflict) : Promise.resolve(echo(request));
    });
    const lookup = createRelationLookupBatcher(send);

    const fine = lookup({ tags: ["A"], performers: [], studios: [] });
    const failing = lookup({ tags: [], performers: ["Shared Name"], studios: [] });
    await vi.waitFor(() => expect(send).toHaveBeenCalledOnce());
    const waiting = lookup({ tags: ["B"], performers: [], studios: [] });
    first.reject(conflict);

    expect((await fine).tags).toEqual([{ input: "A", matchedName: "A" }]);
    await expect(failing).rejects.toBe(conflict);
    expect((await waiting).tags).toEqual([{ input: "B", matchedName: "B" }]);
    expect(send.mock.calls.at(-1)?.[0].tags).toEqual(["B"]);
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
