import { CircleAlert, X } from "lucide-react";
import { useEffect, useSyncExternalStore } from "react";
import { dismissMutationFailure, getMutationFailure, subscribeToMutationFailure } from "../state/mutationFailure";
import { getServerAvailability, subscribeToServerAvailability } from "../state/serverAvailability";
import { getRequestFailureDetail } from "../utils/requestFailure";

const AUTO_DISMISS_MS = 10_000;

export function MutationFailureNotice() {
  const failure = useSyncExternalStore(subscribeToMutationFailure, getMutationFailure, getMutationFailure);
  const availability = useSyncExternalStore(
    subscribeToServerAvailability,
    getServerAvailability,
    getServerAvailability,
  );

  useEffect(() => {
    if (!failure) return;
    const timer = window.setTimeout(() => dismissMutationFailure(failure.id), AUTO_DISMISS_MS);
    return () => window.clearTimeout(timer);
  }, [failure]);

  if (!failure) return null;

  return (
    <div
      role="alert"
      className="fixed bottom-4 left-4 right-4 z-[20000] flex items-start gap-3 rounded-lg border border-red-400/40 bg-red-950 px-4 py-3 text-red-50 shadow-xl sm:left-auto sm:max-w-md"
    >
      <CircleAlert className="mt-0.5 h-5 w-5 shrink-0" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <p className="font-semibold">Couldn’t complete the action</p>
        <p className="mt-0.5 text-sm text-red-100">{getRequestFailureDetail(failure.error, availability)}</p>
      </div>
      <button
        type="button"
        onClick={() => dismissMutationFailure(failure.id)}
        className="rounded p-1 text-red-100 hover:bg-red-100/10 hover:text-white"
        aria-label="Dismiss action error"
      >
        <X className="h-4 w-4" aria-hidden="true" />
      </button>
    </div>
  );
}
