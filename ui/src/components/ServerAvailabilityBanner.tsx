import { useEffect, useRef, useSyncExternalStore } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { Loader2, RefreshCw, WifiOff } from "lucide-react";
import {
  getServerAvailability,
  runServerProbe,
  subscribeToServerAvailability,
} from "../state/serverAvailability";

const RECONNECT_INTERVAL_MS = 5_000;

export function ServerAvailabilityBanner() {
  const queryClient = useQueryClient();
  const availability = useSyncExternalStore(
    subscribeToServerAvailability,
    getServerAvailability,
    getServerAvailability,
  );
  const previousAvailability = useRef(availability);

  useEffect(() => {
    const recovered = previousAvailability.current !== "available" && availability === "available";
    previousAvailability.current = availability;
    if (recovered) {
      void queryClient.invalidateQueries({ refetchType: "active" });
    }
  }, [availability, queryClient]);

  useEffect(() => {
    if (availability !== "unavailable") return;
    const timer = window.setInterval(() => {
      void runServerProbe({ showReconnecting: false });
    }, RECONNECT_INTERVAL_MS);
    return () => window.clearInterval(timer);
  }, [availability]);

  if (availability === "available") return null;

  const reconnecting = availability === "reconnecting";
  return (
    <div
      role="status"
      aria-live="assertive"
      className="fixed inset-x-0 top-0 z-[100] flex min-h-11 items-center justify-center gap-3 border-b border-amber-400/40 bg-amber-950 px-4 py-2 text-center text-sm text-amber-50 shadow-lg"
    >
      {reconnecting ? (
        <Loader2 className="h-4 w-4 shrink-0 animate-spin" aria-hidden="true" />
      ) : (
        <WifiOff className="h-4 w-4 shrink-0" aria-hidden="true" />
      )}
      <span>
        {reconnecting
          ? "Reconnecting to the Cove server…"
          : "Cove can’t reach the server. Some information and actions may be unavailable."}
      </span>
      <button
        type="button"
        onClick={() => void runServerProbe()}
        disabled={reconnecting}
        className="inline-flex shrink-0 items-center gap-1.5 rounded-md border border-amber-200/40 px-2.5 py-1 text-xs font-semibold hover:bg-amber-100/10 disabled:cursor-wait disabled:opacity-60"
      >
        <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
        Try now
      </button>
    </div>
  );
}
