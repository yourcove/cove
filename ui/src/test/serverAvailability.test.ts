import { afterEach, describe, expect, it, vi } from "vitest";
import {
  beginServerReconnect,
  getServerAvailability,
  reportServerConnectionFailure,
  reportServerResponse,
  resetServerAvailabilityForTests,
  runServerProbe,
  serverAwareFetch,
  subscribeToServerAvailability,
} from "../state/serverAvailability";

describe("server availability", () => {
  afterEach(() => {
    vi.useRealTimers();
    resetServerAvailabilityForTests();
    vi.unstubAllGlobals();
  });

  it("treats gateway failures as server unavailability", () => {
    reportServerResponse(new Response("", { status: 502 }));

    expect(getServerAvailability()).toBe("unavailable");
  });

  it("keeps the server available when its status endpoint responds after one gateway failure", async () => {
    let resolveStatus!: (response: Response) => void;
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      if (input === "/api/system/status") {
        return new Promise<Response>((resolve) => {
          resolveStatus = resolve;
        });
      }
      return Promise.resolve(new Response("", { status: 502 }));
    });
    vi.stubGlobal("fetch", fetchMock);
    const transitions: string[] = [];
    const unsubscribe = subscribeToServerAvailability(() => transitions.push(getServerAvailability()));

    await expect(serverAwareFetch("/api/test")).resolves.toMatchObject({ status: 502 });
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(getServerAvailability()).toBe("available");
    expect(transitions).toEqual([]);

    resolveStatus(new Response("", { status: 200 }));
    await Promise.resolve();
    await Promise.resolve();

    expect(getServerAvailability()).toBe("available");
    expect(transitions).toEqual([]);
    unsubscribe();
  });

  it("marks the server unavailable when a gateway failure is confirmed by its status endpoint", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response("", { status: 502 }));
    vi.stubGlobal("fetch", fetchMock);

    await expect(serverAwareFetch("/api/test")).resolves.toMatchObject({ status: 502 });
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(getServerAvailability()).toBe("unavailable");
  });

  it("does not treat a reachable server's 503 response as an outage", () => {
    reportServerResponse(new Response("", { status: 503 }));

    expect(getServerAvailability()).toBe("available");
  });

  it("keeps the server available when its status endpoint responds after one connection failure", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      if (input === "/api/system/status") return Promise.resolve(new Response("", { status: 200 }));
      return Promise.reject(new TypeError("Failed to fetch"));
    });
    vi.stubGlobal("fetch", fetchMock);

    await expect(serverAwareFetch("/api/test")).rejects.toThrow("Failed to fetch");
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(getServerAvailability()).toBe("available");
  });

  it("marks the server unavailable when connection failure confirmation also fails", async () => {
    const fetchMock = vi.fn().mockRejectedValue(new TypeError("Failed to fetch"));
    vi.stubGlobal("fetch", fetchMock);

    await expect(serverAwareFetch("/api/test")).rejects.toThrow("Failed to fetch");
    await vi.waitFor(() => expect(getServerAvailability()).toBe("unavailable"));
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("deduplicates status checks for concurrent connection failures", async () => {
    let resolveStatus!: (response: Response) => void;
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      if (input === "/api/system/status") {
        return new Promise<Response>((resolve) => {
          resolveStatus = resolve;
        });
      }
      return Promise.reject(new TypeError("Failed to fetch"));
    });
    vi.stubGlobal("fetch", fetchMock);

    await expect(Promise.all([serverAwareFetch("/api/one"), serverAwareFetch("/api/two")])).rejects.toThrow(
      "Failed to fetch",
    );
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));

    resolveStatus(new Response("", { status: 200 }));
    await vi.waitFor(() => expect(getServerAvailability()).toBe("available"));
  });

  it("does not let an older failed status check overwrite newer successful evidence", async () => {
    let rejectStatus!: (error: Error) => void;
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      if (input === "/api/system/status") {
        return new Promise<Response>((_resolve, reject) => {
          rejectStatus = reject;
        });
      }
      return Promise.reject(new TypeError("Failed to fetch"));
    });
    vi.stubGlobal("fetch", fetchMock);

    await expect(serverAwareFetch("/api/test")).rejects.toThrow("Failed to fetch");
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    reportServerResponse(new Response("", { status: 200 }));

    rejectStatus(new TypeError("Failed to fetch"));
    await vi.waitFor(() => expect(getServerAvailability()).toBe("available"));
  });

  it("starts a fresh status check when a new failure follows successful evidence", async () => {
    const probes: Array<{
      resolve: (response: Response) => void;
      reject: (error: Error) => void;
    }> = [];
    const fetchMock = vi.fn(
      () =>
        new Promise<Response>((resolve, reject) => {
          probes.push({ resolve, reject });
        }),
    );
    vi.stubGlobal("fetch", fetchMock);

    reportServerConnectionFailure();
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledOnce());
    reportServerResponse(new Response("", { status: 200 }));

    reportServerConnectionFailure();
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));

    probes[0].reject(new TypeError("Old status check failed"));
    await Promise.resolve();
    expect(getServerAvailability()).toBe("available");

    probes[1].resolve(new Response("", { status: 200 }));
    await vi.waitFor(() => expect(getServerAvailability()).toBe("available"));
  });

  it("times out an API request and marks the server unavailable when status confirmation times out", async () => {
    vi.useFakeTimers();
    vi.stubGlobal(
      "fetch",
      vi.fn(
        (_input: RequestInfo | URL, init?: RequestInit) =>
          new Promise<Response>((_resolve, reject) => {
            init?.signal?.addEventListener("abort", () => reject(init.signal?.reason));
          }),
      ),
    );

    const request = serverAwareFetch("/api/test", { timeoutMs: 100 });
    const rejection = expect(request).rejects.toMatchObject({
      name: "TimeoutError",
      message: "API request timed out after 100 ms.",
    });
    await vi.advanceTimersByTimeAsync(100);

    await rejection;
    await vi.advanceTimersByTimeAsync(4_000);
    expect(getServerAvailability()).toBe("unavailable");
  });

  it("does not report caller cancellation as a server outage", async () => {
    const controller = new AbortController();
    vi.stubGlobal(
      "fetch",
      vi.fn(
        (_input: RequestInfo | URL, init?: RequestInit) =>
          new Promise<Response>((_resolve, reject) => {
            init?.signal?.addEventListener("abort", () => reject(init.signal?.reason));
          }),
      ),
    );

    const request = serverAwareFetch("/api/test", { signal: controller.signal });
    controller.abort();

    await expect(request).rejects.toMatchObject({ name: "AbortError" });
    expect(getServerAvailability()).toBe("available");
  });

  it("recognizes caller cancellation with a custom abort reason", async () => {
    const controller = new AbortController();
    const reason = new Error("navigation changed");
    vi.stubGlobal(
      "fetch",
      vi.fn(
        (_input: RequestInfo | URL, init?: RequestInit) =>
          new Promise<Response>((_resolve, reject) => {
            init?.signal?.addEventListener("abort", () => reject(init.signal?.reason));
          }),
      ),
    );

    const request = serverAwareFetch(new Request("http://localhost/api/test", { signal: controller.signal }));
    controller.abort(reason);

    await expect(request).rejects.toBe(reason);
    expect(getServerAvailability()).toBe("available");
  });

  it("keeps caller cancellation attached after response headers arrive", async () => {
    const controller = new AbortController();
    let fetchSignal: AbortSignal | null | undefined;
    vi.stubGlobal(
      "fetch",
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        fetchSignal = init?.signal;
        return new Response(
          new ReadableStream({
            start(bodyController) {
              init?.signal?.addEventListener("abort", () => bodyController.error(init.signal?.reason), { once: true });
            },
          }),
          { status: 200 },
        );
      }),
    );

    const response = await serverAwareFetch("/api/test", { signal: controller.signal, timeoutMs: null });
    const reason = new Error("navigation changed");
    const bodyRejection = expect(response.text()).rejects.toBe(reason);
    controller.abort(reason);

    expect(fetchSignal?.aborted).toBe(true);
    await bodyRejection;
    expect(getServerAvailability()).toBe("available");
  });

  it("keeps the request timeout active while the response body is pending", async () => {
    vi.useFakeTimers();
    let fetchSignal: AbortSignal | null | undefined;
    vi.stubGlobal(
      "fetch",
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        fetchSignal = init?.signal;
        return new Response(
          new ReadableStream({
            start(bodyController) {
              init?.signal?.addEventListener("abort", () => bodyController.error(init.signal?.reason), { once: true });
            },
          }),
          { status: 200 },
        );
      }),
    );

    const response = await serverAwareFetch("/api/test", { timeoutMs: 100 });
    const bodyRejection = expect(response.text()).rejects.toMatchObject({
      name: "TimeoutError",
      message: "API request timed out after 100 ms.",
    });
    await vi.advanceTimersByTimeAsync(100);

    expect(fetchSignal?.aborted).toBe(true);
    expect(fetchSignal?.reason).toMatchObject({
      name: "TimeoutError",
      message: "API request timed out after 100 ms.",
    });
    await bodyRejection;
  });

  it("allows long-running requests to opt out of the timeout", async () => {
    vi.useFakeTimers();
    let resolveFetch!: (response: Response) => void;
    vi.stubGlobal(
      "fetch",
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            resolveFetch = resolve;
          }),
      ),
    );

    const request = serverAwareFetch("/api/database/migrate", { timeoutMs: null });
    await vi.advanceTimersByTimeAsync(60 * 60_000);
    expect(getServerAvailability()).toBe("available");

    resolveFetch(new Response("", { status: 200 }));
    await expect(request).resolves.toMatchObject({ status: 200 });
  });

  it("shows reconnecting while an explicit probe is pending", async () => {
    let resolveFetch!: (response: Response) => void;
    vi.stubGlobal(
      "fetch",
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            resolveFetch = resolve;
          }),
      ),
    );

    const probe = runServerProbe();
    expect(getServerAvailability()).toBe("reconnecting");

    resolveFetch(new Response("", { status: 200 }));
    await probe;
    expect(getServerAvailability()).toBe("available");
  });

  it("keeps the unavailable state stable during a background probe", async () => {
    let resolveFetch!: (response: Response) => void;
    vi.stubGlobal(
      "fetch",
      vi.fn(
        () =>
          new Promise<Response>((resolve) => {
            resolveFetch = resolve;
          }),
      ),
    );
    reportServerResponse(new Response("", { status: 502 }));

    const probe = runServerProbe({ showReconnecting: false });
    expect(getServerAvailability()).toBe("unavailable");

    resolveFetch(new Response("", { status: 502 }));
    await probe;
    expect(getServerAvailability()).toBe("unavailable");
  });

  it("returns to unavailable when a reconnect attempt fails", () => {
    reportServerResponse(new Response("", { status: 502 }));
    beginServerReconnect();

    expect(getServerAvailability()).toBe("reconnecting");

    reportServerResponse(new Response("", { status: 504 }));
    expect(getServerAvailability()).toBe("unavailable");
  });
});
