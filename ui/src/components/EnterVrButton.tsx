import { useEffect, useRef, useState, type RefObject } from "react";
import { Glasses } from "lucide-react";
import { system } from "../api/client";
import { navigateToUrl } from "../router/location";
import { dropWallSession, lastVrList, offerWallSession } from "../vr/vrListRegistry";
import {
  claimHandedOffSession,
  getImmersiveVrSupport,
  hasHandedOffSession,
  immersiveVrSupportIfKnown,
  playVideoInSession,
  startImmersiveVideo,
  type ImmersiveSupport,
  type ImmersiveVideoSession,
  type PlaybackTransport,
  type VrDescriptor,
} from "../vr/immersiveVideo";

// Asked of the browser once per page; the answer does not change while the page lives.
let supportPromise: Promise<ImmersiveSupport> | null = null;

/** WebXR support, without needing a query client: the player is rendered in places that have none. */
function useImmersiveSupport(): ImmersiveSupport | undefined {
  const [support, setSupport] = useState<ImmersiveSupport | undefined>(immersiveVrSupportIfKnown);
  useEffect(() => {
    if (support) return;
    let cancelled = false;
    supportPromise ??= getImmersiveVrSupport();
    void supportPromise.then((result) => {
      if (!cancelled) setSupport(result);
    });
    return () => {
      cancelled = true;
    };
  }, [support]);
  return support;
}

/** Cove's HTTPS listener, asked for only when a headset page landed on plain HTTP. */
function useHttpsStatus(enabled: boolean) {
  const [status, setStatus] = useState<{ enabled: boolean; port: number | null } | null>(null);
  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    system
      .httpsStatus()
      .then((result) => {
        if (!cancelled) setStatus({ enabled: result.enabled, port: result.port ?? null });
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [enabled]);
  return status;
}

/** The same page on Cove's HTTPS listener, for a headset that opened Cove over plain HTTP. */
export function secureUrlFor(location: Pick<Location, "hostname" | "pathname" | "search" | "hash">, port: number) {
  const host = location.hostname.includes(":") ? `[${location.hostname}]` : location.hostname;
  return `https://${host}:${port}${location.pathname}${location.search}${location.hash}`;
}

/**
 * Enters immersive playback of the player's own `<video>` element. Shown only for VR videos, and only
 * where the browser can start an immersive session. When WebXR is hidden because the page is not a
 * secure context and Cove's HTTPS listener is on, it links to the same page over HTTPS instead.
 *
 * When another page (a VR gallery) handed a live session to this one, the player takes it over as soon
 * as it mounts, so the headset goes straight from the gallery to the video while the browser shows the
 * video's page.
 */
export function EnterVrButton({
  videoRef,
  vr,
  title,
  transport,
}: {
  videoRef: RefObject<HTMLVideoElement | null>;
  vr: VrDescriptor;
  title?: string;
  /** The player's own playhead, so seeking and the timeline agree with it even on a transcode. */
  transport?: PlaybackTransport;
}) {
  const support = useImmersiveSupport();
  const insecure = support?.supported === false && support.reason === "insecure-context";
  const https = useHttpsStatus(insecure);
  const [session, setSession] = useState<ImmersiveVideoSession | null>(null);
  const [error, setError] = useState<string | null>(null);
  const claimedRef = useRef(false);
  // Whatever is playing in the headset for this button, so leaving the page can stop it.
  const liveRef = useRef<{ end: () => void } | null>(null);
  // The player rebuilds its transport as the stream changes; the headset always calls the latest one.
  const transportRef = useRef(transport);
  transportRef.current = transport;
  const playbackOptions = () => ({
    title,
    element: () => videoRef.current,
    transport: transportRef.current
      ? {
          currentTime: () => transportRef.current?.currentTime() ?? videoRef.current?.currentTime ?? 0,
          duration: () => transportRef.current?.duration() ?? videoRef.current?.duration ?? 0,
          seek: (seconds: number) => transportRef.current?.seek(seconds),
          isSeeking: () => transportRef.current?.isSeeking?.() ?? videoRef.current?.seeking ?? false,
        }
      : undefined,
  });

  useEffect(
    () => () => {
      // Leaving the page stops immersive playback: an own session ends, a borrowed one goes back to
      // its owner without dragging the browser along.
      liveRef.current?.end();
      liveRef.current = null;
    },
    [],
  );

  // Take over a session a gallery handed to this page. Runs once, on mount, before the support query
  // resolves: the session already exists, so support is a given.
  useEffect(() => {
    if (claimedRef.current || !hasHandedOffSession()) return;
    const video = videoRef.current;
    if (!video) return;
    const claimed = claimHandedOffSession();
    if (!claimed) return;
    claimedRef.current = true;
    let active = true;
    playVideoInSession(claimed.session, video, vr, {
      ...playbackOptions(),
      presenter: claimed.presenter,
      onExit: () => {
        liveRef.current = null;
        if (active) setSession(null);
        claimed.returnToOwner("exit");
      },
    })
      .then((playback) => {
        if (!active) {
          playback.stop();
          claimed.returnToOwner("abandoned");
          return;
        }
        liveRef.current = {
          end: () => {
            playback.stop();
            claimed.returnToOwner("abandoned");
          },
        };
        setSession({
          mode: playback.mode,
          end: async () => {
            liveRef.current = null;
            playback.stop();
            setSession(null);
            claimed.returnToOwner("exit");
          },
        });
      })
      .catch((reason: unknown) => {
        setError(reason instanceof Error ? reason.message : String(reason));
        claimed.returnToOwner("abandoned");
      });
    return () => {
      active = false;
    };
    // The claim must happen exactly once per mount; later prop changes do not re-run it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (insecure && https?.enabled && https.port) {
    return (
      <a
        href={secureUrlFor(window.location, https.port)}
        className="hidden shrink-0 p-1 hover:text-accent md:inline-flex"
        title="VR playback needs HTTPS. Open this page on Cove's secure address."
        aria-label="Open over HTTPS for VR"
      >
        <Glasses className="h-4 w-4 opacity-60" />
      </a>
    );
  }
  if (!support?.supported && !session) return null;

  const enter = async () => {
    const video = videoRef.current;
    if (!video) return;
    if (session) {
      await session.end();
      return;
    }
    setError(null);
    try {
      const started = await startImmersiveVideo(video, vr, {
        ...playbackOptions(),
        onEnd: () => {
          liveRef.current = null;
          setSession(null);
        },
        // Back goes to the list this video was opened from, with that list showing in the headset.
        onBack: (xrSession) => {
          const list = lastVrList();
          if (!list) return false;
          offerWallSession(xrSession, list.url);
          // If that page never shows the wall (it is gone, say), the session must not linger.
          window.setTimeout(() => {
            if (dropWallSession(xrSession)) void (xrSession as { end(): Promise<void> }).end().catch(() => {});
          }, 10_000);
          navigateToUrl(list.url);
          return true;
        },
      });
      liveRef.current = { end: () => void started.end() };
      setSession(started);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    }
  };

  return (
    <span className="inline-flex shrink-0 items-center gap-1">
      <button
        type="button"
        onClick={() => void enter()}
        className={`shrink-0 p-1 hover:text-accent ${session ? "text-accent" : ""} ${error ? "text-red-400" : ""}`}
        title={error ? `Could not enter VR: ${error}` : session ? "Exit VR" : "Enter VR"}
        aria-label={session ? "Exit VR" : "Enter VR"}
        aria-pressed={session != null}
      >
        <Glasses className="h-4 w-4" />
      </button>
      {error ? (
        <span className="hidden max-w-[18rem] truncate text-xs text-red-400 md:inline" title={error}>
          {error}
        </span>
      ) : null}
    </span>
  );
}
