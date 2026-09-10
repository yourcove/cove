import { useEffect, useState } from "react";
import { useExtensions } from "./ExtensionLoader";
import type { ExtensionRuntimeFailure } from "./ExtensionRuntimeReconciler";
import { navigateToUrl } from "../router/location";

export function ExtensionLoadNotice() {
  const { loadFailures } = useExtensions();
  const [dismissed, setDismissed] = useState<string | null>(null);
  useEffect(() => {
    if (loadFailures.length === 0) setDismissed(null);
  }, [loadFailures.length]);
  const signature = JSON.stringify(loadFailures.map((failure) => [String(failure.extensionId), failure.message]));
  if (loadFailures.length === 0 || dismissed === signature) return null;

  return (
    <div
      role="alert"
      className="fixed bottom-4 left-4 right-4 z-[19000] rounded-lg border border-yellow-500/40 bg-card p-4 shadow-xl sm:left-auto sm:max-w-md"
    >
      <p className="font-semibold">
        {loadFailures.length} extension{loadFailures.length === 1 ? "" : "s"}
        {loadFailures.some((failure) => failure.phase === "cleanup")
          ? " reported a UI problem."
          : " couldn’t load its UI."}
      </p>
      <p className="mt-1 text-sm text-secondary">Other extensions can still run.</p>
      <div className="mt-3 flex items-center justify-between gap-4">
        <a
          href="/settings/extensions/installed"
          className="text-sm text-accent underline"
          onClick={(event) => {
            if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
            event.preventDefault();
            navigateToUrl("/settings/extensions/installed");
          }}
        >
          View extension status
        </a>
        <button
          type="button"
          className="text-sm text-secondary"
          onClick={() => setDismissed(signature)}
          aria-label="Dismiss extension loading notice"
        >
          Dismiss
        </button>
      </div>
    </div>
  );
}

export function ExtensionLoadFailureDetails({
  failure,
  retry,
}: {
  failure: ExtensionRuntimeFailure;
  retry: () => Promise<void>;
}) {
  const [retrying, setRetrying] = useState(false);
  const [retryError, setRetryError] = useState<string | null>(null);
  const message =
    failure.phase === "dependency"
      ? "A required extension could not load. Restore it, then retry."
      : failure.phase === "cleanup"
        ? "This extension could not finish unloading. Its UI has been removed. Reload Cove to clear any remaining browser state."
        : /does not provide an export named/.test(failure.message)
          ? "This extension requires a component unavailable in this version of Cove. Check for compatible updates."
          : "This extension’s UI could not start. Retry, check for updates, or disable the extension.";

  return (
    <div className="border-t border-yellow-500/30 bg-yellow-500/5 px-4 py-3 text-sm">
      <p className="font-medium text-yellow-500">
        {failure.phase === "dependency"
          ? "UI blocked by dependency"
          : failure.phase === "cleanup"
            ? "UI cleanup failed"
            : "UI failed to load"}
      </p>
      <p className="mt-1 text-secondary">{message}</p>
      <details className="mt-2">
        <summary className="cursor-pointer text-secondary">Technical details</summary>
        <pre className="mt-2 whitespace-pre-wrap break-all text-xs text-secondary">{failure.message}</pre>
      </details>
      {retryError && (
        <p role="alert" className="mt-2 text-red-400">
          {retryError}
        </p>
      )}
      <button
        type="button"
        disabled={retrying}
        className="mt-2 rounded bg-accent px-3 py-1 text-white disabled:opacity-50"
        onClick={async () => {
          if (failure.phase === "cleanup") {
            window.location.reload();
            return;
          }
          setRetrying(true);
          setRetryError(null);
          try {
            await retry();
          } catch {
            setRetryError("Couldn’t retry extension loading. Try again.");
          } finally {
            setRetrying(false);
          }
        }}
      >
        {failure.phase === "cleanup" ? "Reload Cove" : retrying ? "Retrying…" : "Retry failed extensions"}
      </button>
    </div>
  );
}
