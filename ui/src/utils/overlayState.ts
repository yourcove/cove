// Tracks how many full-screen overlays (e.g. the image lightbox) are currently open.
//
// Global keyboard shortcuts (list pagination, app navigation) are registered as window-level
// keydown listeners and fire in registration order, so a modal that opens later cannot rely on
// stopPropagation to silence them. Instead those shortcut handlers consult this counter and bail
// while an overlay owns the keyboard — e.g. lightbox arrow keys must not also page the list behind it.

let openOverlayCount = 0;

/** Mark an overlay as open. Returns a release function that is safe to call at most once. */
export function pushOverlay(): () => void {
  openOverlayCount += 1;
  let released = false;
  return () => {
    if (released) return;
    released = true;
    openOverlayCount = Math.max(0, openOverlayCount - 1);
  };
}

/** True while at least one keyboard-owning overlay (e.g. the lightbox) is open. */
export function isOverlayOpen(): boolean {
  return openOverlayCount > 0;
}
