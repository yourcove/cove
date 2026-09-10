import { afterEach, describe, expect, it, vi } from "vitest";
import { database, extensions, scrapeAttempts, videoAlignments } from "../api/client";
import { resetServerAvailabilityForTests } from "../state/serverAvailability";

function pendingJsonResponse() {
  let resolve!: (response: Response) => void;
  let signal: AbortSignal | null | undefined;
  vi.stubGlobal(
    "fetch",
    vi.fn((_input: RequestInfo | URL, init?: RequestInit) => {
      signal = init?.signal;
      return new Promise<Response>((resolveFetch) => {
        resolve = resolveFetch;
      });
    }),
  );
  return {
    get signal() {
      return signal;
    },
    finish() {
      resolve(new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }));
    },
  };
}

describe("API client timeout policies", () => {
  it("lets ad hoc alignment finish beyond 15 seconds and preserves cancellation", async () => {
    vi.useFakeTimers();
    const fetchRequest = pendingJsonResponse();
    const controller = new AbortController();
    const request = videoAlignments.analyze(1, 2, 3, controller.signal);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fetchRequest.signal?.aborted).toBe(false);
    controller.abort();
    expect(fetchRequest.signal?.aborted).toBe(true);
    fetchRequest.finish();
    await request;
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    resetServerAvailabilityForTests();
  });

  it("gives synchronous scraper work more than the default timeout", async () => {
    vi.useFakeTimers();
    const fetchRequest = pendingJsonResponse();

    const request = scrapeAttempts.create({} as Parameters<typeof scrapeAttempts.create>[0]);
    await vi.advanceTimersByTimeAsync(15_000);

    expect(fetchRequest.signal?.aborted).toBe(false);
    fetchRequest.finish();
    await request;
  });

  it("does not time out database maintenance", async () => {
    vi.useFakeTimers();
    const fetchRequest = pendingJsonResponse();

    const request = database.migrate();
    await vi.advanceTimersByTimeAsync(60 * 60_000);

    expect(fetchRequest.signal?.aborted).toBe(false);
    fetchRequest.finish();
    await request;
  });

  it("does not time out extension installation", async () => {
    vi.useFakeTimers();
    const fetchRequest = pendingJsonResponse();

    const request = extensions.registryInstall("extension", "1.0.0");
    await vi.advanceTimersByTimeAsync(60 * 60_000);

    expect(fetchRequest.signal?.aborted).toBe(false);
    fetchRequest.finish();
    await request;
  });
});
