import { LOCATION_CHANGE_EVENT } from "../router/location";

const RESTORE_INTERVAL_MS = 100;
const RESTORE_TIMEOUT_MS = 8000;
/** Ticks the offset must hold before restoring stops, so a page that resets its scroll on mount loses. */
const SETTLED_TICKS = 3;
/** Scroll input from the user, and leaving the page, both end a restore. */
const STOP_EVENTS = ["wheel", "touchstart", "keydown", "pointerdown", "popstate"] as const;

let cancelActiveRestore: (() => void) | undefined;

/**
 * Scroll the window to `targetY` on a page whose content is still loading. The offset is re-applied
 * until it holds: reaching the bottom of an infinite list brings its sentinel into view and loads the
 * next page, which makes room to continue. Scroll input from the user, navigating away, or
 * {@link RESTORE_TIMEOUT_MS} passing stops it. Call after the navigation to the page has happened.
 */
export function restoreWindowScroll(targetY: number): () => void {
  cancelActiveRestore?.();

  const target = Math.max(0, Math.round(targetY));
  let settledTicks = 0;
  let stopped = false;

  const stop = () => {
    if (stopped) return;
    stopped = true;
    window.clearInterval(interval);
    window.clearTimeout(timeout);
    for (const eventName of STOP_EVENTS) window.removeEventListener(eventName, stop, true);
    window.removeEventListener(LOCATION_CHANGE_EVENT, stopOnNewPage);
    if (cancelActiveRestore === stop) cancelActiveRestore = undefined;
  };

  const apply = () => {
    if (Math.abs(window.scrollY - target) > 1) {
      settledTicks = 0;
      window.scrollTo(0, target);
      return;
    }

    settledTicks += 1;
    if (settledTicks >= SETTLED_TICKS) stop();
  };

  // A page fixing up its own URL replaces the entry; only a move to another entry leaves the page.
  const stopOnNewPage = (event: Event) => {
    if (!(event instanceof CustomEvent && event.detail?.replace === true)) stop();
  };

  const interval = window.setInterval(apply, RESTORE_INTERVAL_MS);
  const timeout = window.setTimeout(stop, RESTORE_TIMEOUT_MS);
  for (const eventName of STOP_EVENTS) {
    window.addEventListener(eventName, stop, { capture: true, passive: true });
  }
  window.addEventListener(LOCATION_CHANGE_EVENT, stopOnNewPage);
  cancelActiveRestore = stop;
  apply();
  return stop;
}
