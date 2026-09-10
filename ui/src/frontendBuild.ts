// Identity is opaque: any difference, including a rollback, requires a reload.
export function startFrontendBuildMonitor(buildId: string, onUpdate: () => void) {
  let pending: Promise<void> | undefined;
  let outdated = false;
  let stopped = false;
  let controller: AbortController | undefined;

  const check = (): Promise<void> => {
    if (stopped || outdated) return Promise.resolve();
    if (pending) return pending;
    controller = new AbortController();
    const timeout = window.setTimeout(() => controller?.abort(), 10_000);
    pending = (async () => {
      try {
        const response = await fetch("/api/system/frontend-build", {
          cache: "no-store",
          credentials: "omit",
          signal: controller!.signal,
        });
        if (!response.ok) return;
        const data: unknown = await response.json();
        if (
          !stopped &&
          data !== null &&
          typeof data === "object" &&
          "buildId" in data &&
          typeof data.buildId === "string" &&
          /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(data.buildId) &&
          data.buildId !== buildId
        ) {
          outdated = true;
          onUpdate();
        }
      } catch {
        // Offline, restarting, or invalid response: retry on the next trigger.
      } finally {
        window.clearTimeout(timeout);
        pending = undefined;
      }
    })();
    return pending;
  };
  const trigger = () => {
    void check();
  };
  const whenVisible = () => {
    if (document.visibilityState === "visible") trigger();
  };
  document.addEventListener("visibilitychange", whenVisible);
  window.addEventListener("online", trigger);
  window.addEventListener("pageshow", trigger);
  // Do not preventDefault: normal chunk error recovery remains available unless
  // the independent check confirms a mismatch and blocks the app.
  window.addEventListener("vite:preloadError", trigger);
  const interval = window.setInterval(whenVisible, 60_000);
  trigger();
  return {
    check,
    stop() {
      stopped = true;
      controller?.abort();
      window.clearInterval(interval);
      document.removeEventListener("visibilitychange", whenVisible);
      window.removeEventListener("online", trigger);
      window.removeEventListener("pageshow", trigger);
      window.removeEventListener("vite:preloadError", trigger);
    },
  };
}
