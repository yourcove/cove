import type { FaceSuggestion } from "../api/types";

interface Props {
  suggestion: Pick<FaceSuggestion, "why" | "externalUrl" | "localPerformerId" | "performerId">;
  isReferenceOnly: boolean;
}

// The textual evidence block shown beneath a suggestion: a plain-language summary of where the match
// came from, the individual evidence lines (split out of `why`), and a link to the source record when
// the match originates from a reference database. Shared by the face detail suggestions panel and the
// compare dialog so both surfaces show identical evidence.
export function SuggestionEvidenceText({ suggestion, isReferenceOnly }: Props) {
  const evidenceLines = splitEvidenceLines(suggestion.why);
  const hasReferenceSignal = !!suggestion.externalUrl || suggestion.localPerformerId != null || isReferenceOnly;

  if (!hasReferenceSignal && evidenceLines.length === 0) {
    return <p className="text-xs text-secondary">This suggestion did not include local face thumbnails.</p>;
  }

  return (
    <div className="space-y-1 text-xs text-secondary">
      {hasReferenceSignal ? (
        <p>{isReferenceOnly ? "External reference match. Import it to create a local performer link." : "Reference match resolved to this local performer."}</p>
      ) : null}
      {evidenceLines.map((line) => (
        <p key={line}>{line}</p>
      ))}
      {suggestion.externalUrl ? (
        <a href={suggestion.externalUrl} target="_blank" rel="noreferrer" className="inline-flex text-accent hover:underline">
          Open reference record
        </a>
      ) : null}
    </div>
  );
}

export function splitEvidenceLines(value: string) {
  return (value || "")
    .split(/;\s*/)
    .map((line) => line.trim())
    .filter(Boolean)
    .slice(0, 4);
}
