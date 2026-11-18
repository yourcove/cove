/**
 * Accept or reject a face's suggested performer, as the Segments tab and the faces list both offer it. A suggestion
 * that would create a new performer is reviewed on the face page instead.
 */
export function SuggestionDecision({
  performerId,
  pending,
  onDecide,
  onReview,
}: {
  performerId: number;
  pending: boolean;
  onDecide: (performerId: number, decision: "accept" | "reject") => void;
  onReview: () => void;
}) {
  if (performerId <= 0) {
    return (
      <button type="button" onClick={onReview} className="shrink-0 text-accent hover:underline">
        Review
      </button>
    );
  }
  return (
    <>
      <button
        type="button"
        onClick={() => onDecide(performerId, "accept")}
        disabled={pending}
        className="shrink-0 rounded bg-accent px-2 py-0.5 font-medium text-white hover:bg-accent-hover disabled:opacity-50"
      >
        Accept
      </button>
      <button
        type="button"
        onClick={() => onDecide(performerId, "reject")}
        disabled={pending}
        className="shrink-0 rounded border border-border px-2 py-0.5 text-foreground hover:border-accent disabled:opacity-50"
      >
        Not them
      </button>
    </>
  );
}
