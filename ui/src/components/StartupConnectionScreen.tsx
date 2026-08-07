import { Loader2, RefreshCw, ServerOff } from "lucide-react";

export function StartupConnectionScreen({ retrying, onRetry }: { retrying: boolean; onRetry: () => void }) {
  return (
    <main className="flex min-h-screen items-center justify-center bg-background px-6 text-foreground">
      <div className="w-full max-w-md rounded-xl border border-border bg-card p-8 text-center shadow-lg">
        <div className="mx-auto mb-5 flex h-14 w-14 items-center justify-center rounded-full bg-amber-500/15 text-amber-400">
          <ServerOff className="h-7 w-7" aria-hidden="true" />
        </div>
        <h1 className="text-xl font-semibold">Can’t connect to the Cove server</h1>
        <p className="mt-3 text-sm leading-6 text-muted-foreground">
          The Cove interface loaded, but the server is not responding. Check that the server is running and try again.
        </p>
        <button
          type="button"
          onClick={onRetry}
          disabled={retrying}
          className="mt-6 inline-flex items-center justify-center gap-2 rounded-md bg-accent px-4 py-2 text-sm font-semibold text-accent-foreground hover:opacity-90 disabled:cursor-wait disabled:opacity-60"
        >
          {retrying ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" /> : <RefreshCw className="h-4 w-4" aria-hidden="true" />}
          {retrying ? "Trying again…" : "Try again"}
        </button>
      </div>
    </main>
  );
}
