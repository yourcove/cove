import { useSyncExternalStore } from "react";
import { getServerAvailability, subscribeToServerAvailability } from "../state/serverAvailability";
import { getRequestFailureDetail } from "../utils/requestFailure";

interface ListLoadErrorProps {
  error: Error;
  onRetry?: () => void;
  title?: string;
  className?: string;
}

export function ListLoadError({
  error,
  onRetry,
  title = "Could not load items",
  className = "mx-1 mt-3",
}: ListLoadErrorProps) {
  const availability = useSyncExternalStore(
    subscribeToServerAvailability,
    getServerAvailability,
    getServerAvailability,
  );
  const detail = getRequestFailureDetail(error, availability);

  return (
    <div
      role="alert"
      className={`${className} rounded-xl border border-red-500/40 bg-red-500/10 px-4 py-6 text-center`}
    >
      <p className="font-medium text-red-100">{title}</p>
      <p className="mt-1 text-sm text-red-200/80">{detail}</p>
      {onRetry ? (
        <button
          type="button"
          onClick={onRetry}
          className="mt-4 rounded-lg border border-red-300/40 px-3 py-1.5 text-sm text-red-100 hover:bg-red-500/15"
        >
          Try again
        </button>
      ) : null}
    </div>
  );
}
