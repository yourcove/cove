import { useEffect, useState, type ReactNode } from "react";
import { useExtensions } from "../extensions/ExtensionLoader";
import { hasBootedLook } from "../theme/themeBoot";

// A theme is only known once /me and the manifest resolve, and the manifest needs a signed-in user,
// so a first sign-in on a new browser has nothing cached to pre-paint from. Hold the spinner the
// surrounding gates already use rather than render the app in the default palette and repaint it.
//
// Read once, at startup: a page the boot script already painted has nothing to wait for.
const bootedLookAtStartup = hasBootedLook();

// A manifest that never settles must not hold the app hostage; past this the default palette wins.
const THEME_SETTLE_TIMEOUT_MS = 2000;

export function ThemeGate({ children }: { children: ReactNode }) {
  const { loaded } = useExtensions();
  // A latch, not a derived value: `loaded` goes false again whenever the loader refetches, and
  // re-closing the gate would unmount the whole app below it and lose player and queue state.
  const [opened, setOpened] = useState(() => bootedLookAtStartup);
  if (loaded && !opened) setOpened(true);
  const waiting = !opened && !loaded;

  useEffect(() => {
    if (!waiting) return;
    const timer = window.setTimeout(() => setOpened(true), THEME_SETTLE_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [waiting]);

  if (waiting) {
    return (
      <div className="min-h-screen bg-background flex items-center justify-center">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-accent" />
      </div>
    );
  }

  return <>{children}</>;
}
