import { useEffect, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { MoreVertical, Trash2 } from "lucide-react";
import { formatDateTime } from "../utils/dateFormat";

interface Props {
  likeHistory?: string[];
  loading?: boolean;
  canAddHistoricalLike: boolean;
  onAddHistoricalLike: (at: string) => Promise<unknown>;
  onDeleteLike: (at: string) => Promise<unknown>;
}

function currentLocalDateTime() {
  const now = new Date();
  now.setMinutes(now.getMinutes() - now.getTimezoneOffset());
  return now.toISOString().slice(0, 16);
}

export function LikeHistorySection({
  likeHistory,
  loading,
  canAddHistoricalLike,
  onAddHistoricalLike,
  onDeleteLike,
}: Props) {
  const [menuOpen, setMenuOpen] = useState(false);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [historicalAt, setHistoricalAt] = useState("");
  const menuRef = useRef<HTMLDivElement>(null);
  const addMutation = useMutation({
    mutationFn: () => onAddHistoricalLike(new Date(historicalAt).toISOString()),
    onSuccess: () => {
      setDialogOpen(false);
      setHistoricalAt("");
    },
  });
  const deleteMutation = useMutation({ mutationFn: (at: string) => onDeleteLike(at) });

  useEffect(() => {
    if (!menuOpen) return;
    const handleClickOutside = (event: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) setMenuOpen(false);
    };
    document.addEventListener("mousedown", handleClickOutside);
    return () => document.removeEventListener("mousedown", handleClickOutside);
  }, [menuOpen]);

  return (
    <section>
      <div className="mb-2 flex items-center justify-between">
        <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">Like History</h3>
        {canAddHistoricalLike ? (
          <div ref={menuRef} className="relative">
            <button
              type="button"
              aria-label="Like history actions"
              aria-expanded={menuOpen}
              onClick={() => setMenuOpen((open) => !open)}
              className="rounded p-1 text-secondary hover:bg-card-hover hover:text-foreground"
            >
              <MoreVertical className="h-4 w-4" />
            </button>
            {menuOpen ? (
              <div className="absolute right-0 z-20 mt-1 w-44 overflow-hidden rounded-md border border-border bg-card shadow-lg">
                <button
                  type="button"
                  className="w-full px-3 py-2 text-left text-sm text-foreground hover:bg-card-hover"
                  onClick={() => {
                    setHistoricalAt(currentLocalDateTime());
                    setMenuOpen(false);
                    setDialogOpen(true);
                  }}
                >
                  Add historical like
                </button>
              </div>
            ) : null}
          </div>
        ) : null}
      </div>
      {loading ? (
        <div className="border-t border-border pt-2 text-xs text-muted">Loading like history...</div>
      ) : likeHistory?.length ? (
        <div className="max-h-40 space-y-0.5 overflow-y-auto border-t border-border pt-2">
          {likeHistory.map((date, index) => (
            <div key={`${date}-${index}`} className="flex items-center justify-between gap-2 text-xs text-secondary">
              <span>{formatDateTime(date)}</span>
              {canAddHistoricalLike ? (
                <button
                  type="button"
                  aria-label={`Delete like from ${formatDateTime(date)}`}
                  title="Delete like"
                  disabled={deleteMutation.isPending}
                  onClick={() => deleteMutation.mutate(date)}
                  className="rounded p-1 text-muted hover:bg-card-hover hover:text-danger disabled:opacity-50"
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              ) : null}
            </div>
          ))}
          {deleteMutation.isError ? <div className="pt-1 text-xs text-danger">Could not delete the like.</div> : null}
        </div>
      ) : (
        <div className="border-t border-border pt-2 text-xs text-muted">No likes recorded yet.</div>
      )}

      {dialogOpen ? (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
          role="presentation"
          onMouseDown={() => setDialogOpen(false)}
        >
          <form
            role="dialog"
            aria-modal="true"
            aria-labelledby="historical-like-title"
            className="w-full max-w-sm rounded-lg border border-border bg-card p-4 shadow-xl"
            onMouseDown={(event) => event.stopPropagation()}
            onSubmit={(event) => {
              event.preventDefault();
              addMutation.mutate();
            }}
          >
            <h3 id="historical-like-title" className="text-base font-semibold text-foreground">
              Add historical like
            </h3>
            <label className="mt-4 block text-sm text-secondary">
              Date and time
              <input
                type="datetime-local"
                required
                max={currentLocalDateTime()}
                value={historicalAt}
                onChange={(event) => setHistoricalAt(event.target.value)}
                className="mt-1 w-full rounded-md border border-border bg-surface px-3 py-2 text-foreground"
              />
            </label>
            {addMutation.isError ? (
              <p className="mt-2 text-xs text-danger">Could not add the historical like.</p>
            ) : null}
            <div className="mt-4 flex justify-end gap-2">
              <button
                type="button"
                className="rounded border border-border bg-card px-3 py-1.5 text-sm text-secondary hover:bg-card-hover hover:text-foreground"
                onClick={() => setDialogOpen(false)}
              >
                Cancel
              </button>
              <button
                type="submit"
                disabled={!historicalAt || addMutation.isPending}
                className="rounded bg-accent px-3 py-1.5 text-sm text-white disabled:opacity-50"
              >
                {addMutation.isPending ? "Adding…" : "Add like"}
              </button>
            </div>
          </form>
        </div>
      ) : null}
    </section>
  );
}
