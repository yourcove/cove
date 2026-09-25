import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Glasses } from "lucide-react";
import { getImmersiveVrSupport, immersiveVrSupportIfKnown } from "../vr/immersiveVideo";
import { claimWallSession, hasPendingWallSession, registerVrList, type VrListSource } from "../vr/vrListRegistry";
import type { VrWall, WallState } from "../vr/VrWall";

const loadWall = () => import("../vr/VrWall");

/**
 * "View in VR" for a video list: shows the list's current page as a wall of cards in the headset. The
 * wall outlives the page, so opening a video in it takes the browser to the video's page and coming
 * back returns the browser here; a video started from its own page also comes back here when the
 * viewer presses back, with the wall showing.
 */
export function ViewInVrButton({ source, onNavigate }: { source: VrListSource; onNavigate: (route: any) => void }) {
  const support = useQuery({
    queryKey: ["webxr-support"],
    queryFn: getImmersiveVrSupport,
    staleTime: Infinity,
    initialData: immersiveVrSupportIfKnown,
  });
  const [wall, setWall] = useState<VrWall | null>(null);
  const [state, setState] = useState<WallState>("browsing");
  const [error, setError] = useState<string | null>(null);
  // The wall calls back long after this render; it must reach the latest list and navigator.
  const sourceRef = useRef(source);
  const navigateRef = useRef(onNavigate);
  useEffect(() => {
    sourceRef.current = source;
    navigateRef.current = onNavigate;
  });

  const hooks = () => ({
    navigate: (target: { page: string; id?: number }) => navigateRef.current(target),
    onEnd: () => {
      setWall(null);
      setState("browsing");
    },
    onError: setError,
    onStateChange: setState,
  });

  // This list is the one the headset would come back to.
  useEffect(() => {
    registerVrList(source);
  }, [source]);

  // Pick up a wall that is already showing (we came back from a video), or a session a video page
  // left for this list to show the wall in.
  useEffect(() => {
    // Without WebXR there can be no wall to pick up, and three.js need not load at all.
    if (immersiveVrSupportIfKnown() && !hasPendingWallSession()) return;
    let cancelled = false;
    void loadWall().then(async ({ getActiveWall, VrWall }) => {
      if (cancelled) return;
      const url = `${window.location.pathname}${window.location.search}`;
      const active = getActiveWall();
      if (active) {
        active.attachPage(hooks(), url);
        active.setSource(sourceRef.current, url);
        setWall(active);
        setState(active.state);
        return;
      }
      const session = claimWallSession(url);
      if (!session) return;
      const next = new VrWall(sourceRef.current, url, hooks());
      setWall(next);
      try {
        await next.start(session);
      } catch (reason) {
        setWall(null);
        setError(reason instanceof Error ? reason.message : String(reason));
      }
    });
    return () => {
      cancelled = true;
    };
    // Once per mount: the wall is a page-level object, not a render-time one.
  }, []);

  // The browser's list moved (page, sort, filter): the wall follows.
  useEffect(() => {
    if (wall && wall.state === "browsing")
      wall.setSource(source, `${window.location.pathname}${window.location.search}`);
  }, [source, wall]);

  // Without a headset the button has no job; it appears only where a session can be started.
  if (!support.data?.supported && !wall) return null;

  const enter = async () => {
    setError(null);
    if (wall) {
      await wall.end();
      return;
    }
    const { VrWall } = await loadWall();
    const url = `${window.location.pathname}${window.location.search}`;
    const next = new VrWall(sourceRef.current, url, hooks());
    setWall(next);
    try {
      await next.start();
    } catch (reason) {
      setWall(null);
      setError(reason instanceof Error ? reason.message : String(reason));
    }
  };

  const label = wall ? (state === "watching" ? "Watching in VR" : "Leave VR") : "View in VR";
  return (
    <button
      type="button"
      onClick={() => void enter()}
      className={`inline-flex min-h-10 items-center justify-center gap-1 rounded-lg border px-2.5 py-2 text-sm transition-colors sm:min-h-0 sm:py-1 sm:text-xs ${
        wall
          ? "border-accent bg-accent/15 text-accent"
          : "border-border bg-card/70 text-secondary hover:border-accent/50 hover:text-accent"
      } ${error ? "border-red-500/60" : ""}`}
      title={error ? `VR: ${error}` : wall ? "Leave VR" : "Show this list in the headset"}
      aria-label={label}
      aria-pressed={wall != null}
    >
      <Glasses className="h-3.5 w-3.5" />
      <span className="hidden lg:inline">{label}</span>
    </button>
  );
}
