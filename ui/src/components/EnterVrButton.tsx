import { useState, type RefObject } from "react";
import { useQuery } from "@tanstack/react-query";
import { Glasses } from "lucide-react";
import { system } from "../api/client";
import {
  getImmersiveVrSupport,
  startImmersiveVideo,
  type ImmersiveVideoSession,
  type VrDescriptor,
} from "../vr/immersiveVideo";

/** The same page on Cove's HTTPS listener, for a headset that opened Cove over plain HTTP. */
export function secureUrlFor(location: Pick<Location, "hostname" | "pathname" | "search" | "hash">, port: number) {
  const host = location.hostname.includes(":") ? `[${location.hostname}]` : location.hostname;
  return `https://${host}:${port}${location.pathname}${location.search}${location.hash}`;
}

/**
 * Enters immersive playback of the player's own `<video>` element. Shown only for VR videos, and only
 * where the browser can start an immersive session. When WebXR is hidden because the page is not a
 * secure context and Cove's HTTPS listener is on, it links to the same page over HTTPS instead.
 */
export function EnterVrButton({ videoRef, vr }: { videoRef: RefObject<HTMLVideoElement | null>; vr: VrDescriptor }) {
  const support = useQuery({ queryKey: ["webxr-support"], queryFn: getImmersiveVrSupport, staleTime: Infinity });
  const insecure = support.data?.supported === false && support.data.reason === "insecure-context";
  const https = useQuery({
    queryKey: ["https-status"],
    queryFn: system.httpsStatus,
    enabled: insecure,
    staleTime: 60_000,
  });
  const [session, setSession] = useState<ImmersiveVideoSession | null>(null);
  const [error, setError] = useState<string | null>(null);

  if (insecure && https.data?.enabled && https.data.port) {
    return (
      <a
        href={secureUrlFor(window.location, https.data.port)}
        className="hidden shrink-0 p-1 hover:text-accent md:inline-flex"
        title="VR playback needs HTTPS. Open this page on Cove's secure address."
        aria-label="Open over HTTPS for VR"
      >
        <Glasses className="h-4 w-4 opacity-60" />
      </a>
    );
  }
  if (!support.data?.supported) return null;

  const enter = async () => {
    const video = videoRef.current;
    if (!video) return;
    if (session) {
      await session.end();
      return;
    }
    setError(null);
    try {
      const started = await startImmersiveVideo(video, vr, { onEnd: () => setSession(null) });
      setSession(started);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    }
  };

  return (
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
  );
}
