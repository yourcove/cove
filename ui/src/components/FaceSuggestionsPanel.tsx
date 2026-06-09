import { Fingerprint } from "lucide-react";
import type { Face, FaceSuggestion } from "../api/types";
import { SuggestionEvidenceText } from "./SuggestionEvidenceText";

interface Props {
  face?: Face;
  suggestions: FaceSuggestion[];
  isLoading: boolean;
  disabled: boolean;
  canReadPerformers: boolean;
  onAccept: (suggestion: FaceSuggestion) => void;
  onReject: (suggestion: FaceSuggestion) => void;
  onCompare?: (suggestion: FaceSuggestion) => void;
  onNavigate: (route: any) => void;
}

export function FaceSuggestionsPanel({
  suggestions,
  isLoading,
  disabled,
  canReadPerformers,
  onAccept,
  onReject,
  onCompare,
  onNavigate,
}: Props) {
  return (
    <div className="space-y-3 border-t border-border pt-4">
      <div>
        <div className="text-xs font-semibold uppercase tracking-wide text-muted">Suggested matches</div>
        <p className="mt-1 text-sm text-secondary">Extension-provided performer and reference matches ranked by confidence and supporting evidence.</p>
      </div>

      {isLoading ? (
        <p className="text-xs text-secondary">Loading face suggestions...</p>
      ) : suggestions.length === 0 ? (
        <p className="rounded-xl border border-dashed border-border px-3 py-4 text-sm text-secondary">No suggestions are available for this face yet.</p>
      ) : (
        <div className="space-y-3">
          {suggestions.map((suggestion) => {
            const isReferenceOnly = suggestion.performerId < 0;
            // A match sourced from a reference database, whether or not it already resolved to a local
            // performer (resolved ones have a positive id but still carry the external reference URL).
            const isReferenceMatch = isReferenceOnly || !!suggestion.externalUrl;

            return (
              <article key={suggestion.performerId} className="rounded-2xl border border-border bg-surface/60 p-4">
                <div className="flex gap-4">
                  {isReferenceOnly ? (
                    <div className="h-16 w-16 shrink-0 overflow-hidden rounded-2xl bg-surface/90 text-left">
                      {suggestion.coverImageUrl ? (
                        <img src={suggestion.coverImageUrl} alt={suggestion.performerName} className="h-full w-full object-cover object-top" loading="lazy" />
                      ) : (
                        <div className="flex h-full w-full items-center justify-center text-muted">
                          <Fingerprint className="h-6 w-6" />
                        </div>
                      )}
                    </div>
                  ) : (
                    <button
                      type="button"
                      onClick={() => canReadPerformers && onNavigate({ page: "performer", id: suggestion.performerId })}
                      className="h-16 w-16 shrink-0 overflow-hidden rounded-2xl bg-surface/90 text-left"
                      disabled={!canReadPerformers}
                      aria-label={`Open performer ${suggestion.performerName}`}
                    >
                      {suggestion.coverImageUrl ? (
                        <img src={suggestion.coverImageUrl} alt={suggestion.performerName} className="h-full w-full object-cover object-top" loading="lazy" />
                      ) : (
                        <div className="flex h-full w-full items-center justify-center text-muted">
                          <Fingerprint className="h-6 w-6" />
                        </div>
                      )}
                    </button>
                  )}

                  <div className="min-w-0 flex-1 space-y-3">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                      <div>
                        <div className="flex flex-wrap items-center gap-2">
                          {isReferenceOnly ? (
                            <span className="text-sm font-semibold text-foreground">{suggestion.performerName}</span>
                          ) : (
                            <button
                              type="button"
                              onClick={() => canReadPerformers && onNavigate({ page: "performer", id: suggestion.performerId })}
                              className={`text-left text-sm font-semibold ${canReadPerformers ? "text-accent hover:underline" : "text-foreground"}`}
                              disabled={!canReadPerformers}
                            >
                              {suggestion.performerName}
                            </button>
                          )}
                          {isReferenceMatch ? (
                            <span className="rounded-full border border-amber-500/30 bg-amber-500/10 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-amber-200">Reference DB</span>
                          ) : null}
                        </div>
                        <p className="mt-1 text-xs text-secondary">{suggestion.why}</p>
                      </div>
                      <div className="min-w-[124px] space-y-1">
                        <div className="flex items-center justify-between text-[11px] uppercase tracking-wide text-muted">
                          <span>Confidence</span>
                          <span>{formatPercent(suggestion.confidence)}%</span>
                        </div>
                        <div className="h-2 overflow-hidden rounded-full bg-surface">
                          <div
                            className={`h-full rounded-full ${confidenceBarClassName(suggestion.confidence)}`}
                            style={{ width: `${formatPercent(suggestion.confidence)}%` }}
                          />
                        </div>
                      </div>
                    </div>

                    <div className="space-y-2">
                      <div className="text-[11px] uppercase tracking-wide text-muted">Evidence</div>
                      {suggestion.evidence.length === 0 ? (
                        <SuggestionEvidenceText suggestion={suggestion} isReferenceOnly={isReferenceOnly} />
                      ) : (
                        <div className="flex flex-wrap gap-2">
                          {suggestion.evidence.slice(0, 5).map((evidence) => (
                            <button
                              key={`${suggestion.performerId}-${evidence.faceId}`}
                              type="button"
                              onClick={() => onNavigate({ page: "face", id: evidence.faceId })}
                              className="group relative h-11 w-11 overflow-hidden rounded-full border border-border bg-surface/80"
                              aria-label={`Open evidence face ${evidence.faceId}`}
                              title={`${formatPercent(evidence.similarity)}% similar`}
                            >
                              {evidence.thumbnailUrl ? (
                                <img src={evidence.thumbnailUrl} alt={`Face ${evidence.faceId}`} className="h-full w-full object-cover" loading="lazy" />
                              ) : (
                                <div className="flex h-full w-full items-center justify-center text-[10px] text-secondary">
                                  #{evidence.faceId}
                                </div>
                              )}
                            </button>
                          ))}
                        </div>
                      )}
                    </div>

                    <div className="flex flex-wrap gap-2">
                      {onCompare ? (
                        <button
                          type="button"
                          onClick={() => onCompare(suggestion)}
                          disabled={disabled}
                          className="rounded-lg border border-accent/60 bg-accent/10 px-3 py-2 text-sm text-accent transition-colors hover:bg-accent/20 disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          Compare
                        </button>
                      ) : null}
                      <button
                        type="button"
                        onClick={() => onAccept(suggestion)}
                        disabled={disabled}
                        className="rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white transition-colors hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {isReferenceOnly ? "Import as performer" : "Accept"}
                      </button>
                      <button
                        type="button"
                        onClick={() => onReject(suggestion)}
                        disabled={disabled}
                        className="rounded-lg border border-border px-3 py-2 text-sm text-foreground transition-colors hover:border-accent disabled:cursor-not-allowed disabled:opacity-50"
                      >
                        {isReferenceOnly ? "Dismiss" : "Reject"}
                      </button>
                    </div>
                </div>
                </div>
              </article>
            );
          })}
        </div>
      )}
    </div>
  );
}

function formatPercent(value: number) {
  const scaled = value <= 1 ? value * 100 : value;
  return Math.max(0, Math.min(100, Math.round(scaled)));
}

function confidenceBarClassName(value: number) {
  const percent = formatPercent(value);
  if (percent >= 75) {
    return "bg-emerald-500";
  }
  if (percent >= 50) {
    return "bg-amber-400";
  }
  return "bg-rose-500";
}
