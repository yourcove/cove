import { startFrontendBuildMonitor } from "../frontendBuild";

const a = "11111111-1111-4111-8111-111111111111";
const b = "22222222-2222-4222-8222-222222222222";
let monitor: ReturnType<typeof startFrontendBuildMonitor>;
const response = (buildId: unknown) => ({ ok: true, json: async () => ({ buildId }) });

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response(a)));
  vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible");
});
afterEach(() => {
  monitor?.stop();
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

it.each([
  [a, b],
  [b, a],
])("detects a different build, including rollbacks (%s → %s)", async (current, next) => {
  vi.mocked(fetch).mockResolvedValue(response(next) as Response);
  const update = vi.fn();
  monitor = startFrontendBuildMonitor(current, update);
  await monitor.check();
  await monitor.check();
  expect(update).toHaveBeenCalledTimes(1);
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(fetch).toHaveBeenCalledWith(
    "/api/system/frontend-build",
    expect.objectContaining({ cache: "no-store", credentials: "omit" }),
  );
});

it.each([undefined, null, "", "invalid", 12])("ignores invalid IDs (%s) and retries", async (invalid) => {
  vi.mocked(fetch).mockResolvedValueOnce(response(invalid) as Response);
  const update = vi.fn();
  monitor = startFrontendBuildMonitor(a, update);
  await monitor.check();
  expect(update).not.toHaveBeenCalled();
  vi.mocked(fetch).mockResolvedValueOnce(response(b) as Response);
  await monitor.check();
  expect(update).toHaveBeenCalledOnce();
});

it("retries offline, server errors, and malformed JSON without claiming an update", async () => {
  const update = vi.fn();
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("offline"));
  monitor = startFrontendBuildMonitor(a, update);
  await monitor.check();
  vi.mocked(fetch).mockResolvedValueOnce({ ok: false } as Response);
  await monitor.check();
  vi.mocked(fetch).mockResolvedValueOnce({
    ok: true,
    json: async () => {
      throw new SyntaxError();
    },
  } as unknown as Response);
  await monitor.check();
  expect(update).not.toHaveBeenCalled();
  vi.mocked(fetch).mockResolvedValueOnce(response(b) as Response);
  window.dispatchEvent(new Event("online"));
  await monitor.check();
  expect(update).toHaveBeenCalledOnce();
});

it("deduplicates triggers while a request is pending", async () => {
  let resolve!: (value: Response) => void;
  vi.mocked(fetch).mockReturnValue(
    new Promise((done) => {
      resolve = done;
    }),
  );
  monitor = startFrontendBuildMonitor(a, vi.fn());
  window.dispatchEvent(new Event("online"));
  window.dispatchEvent(new Event("pageshow"));
  document.dispatchEvent(new Event("visibilitychange"));
  expect(fetch).toHaveBeenCalledTimes(1);
  resolve(response(a) as Response);
  await monitor.check();
  await monitor.check();
  expect(fetch).toHaveBeenCalledTimes(2);
});

it("polls only while visible and checks on visibility and history restoration", async () => {
  monitor = startFrontendBuildMonitor(a, vi.fn());
  await monitor.check();
  await vi.advanceTimersByTimeAsync(60_000);
  expect(fetch).toHaveBeenCalledTimes(2);
  vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
  await vi.advanceTimersByTimeAsync(120_000);
  expect(fetch).toHaveBeenCalledTimes(2);
  vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible");
  document.dispatchEvent(new Event("visibilitychange"));
  await monitor.check();
  window.dispatchEvent(new PageTransitionEvent("pageshow", { persisted: true }));
  await monitor.check();
  expect(fetch).toHaveBeenCalledTimes(4);
});

it("checks chunk errors without suppressing ordinary error recovery", async () => {
  const update = vi.fn();
  monitor = startFrontendBuildMonitor(a, update);
  await monitor.check();
  const error = new Event("vite:preloadError", { cancelable: true });
  window.dispatchEvent(error);
  await monitor.check();
  expect(error.defaultPrevented).toBe(false);
  expect(update).not.toHaveBeenCalled();
  vi.mocked(fetch).mockResolvedValueOnce(response(b) as Response);
  window.dispatchEvent(error);
  await monitor.check();
  expect(update).toHaveBeenCalledOnce();
});

it("times out stalled requests so a later trigger can retry", async () => {
  vi.mocked(fetch).mockImplementationOnce(
    (_url, options) =>
      new Promise((_resolve, reject) => {
        options!.signal!.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
      }),
  );
  monitor = startFrontendBuildMonitor(a, vi.fn());
  await vi.advanceTimersByTimeAsync(10_000);
  await monitor.check();
  expect(fetch).toHaveBeenCalledTimes(2);
});
