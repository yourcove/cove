import { Check, Minus, Plus } from "lucide-react";
import type { ScrapeCollectionItemAction, ScrapeCollectionItemSelection } from "../api/types";

export type ScrapeRelationActionMap = Record<string, ScrapeCollectionItemAction>;

export function relationKey(name: string) {
  return name.trim().toLowerCase();
}

// relationKey(scraped name) -> the existing entity's primary name. Differs from the scraped name when
// the match was via an alias; surfaced as the ScrapeRelationChoices tooltip so a match is never a mystery.
export function buildMatchInfo(matches?: { input: string; matchedName: string }[]): Record<string, string> {
  const info: Record<string, string> = {};
  for (const match of matches ?? []) {
    info[relationKey(match.input)] = match.matchedName;
  }
  return info;
}

export function buildRelationActionMap(
  scrapedNames: string[],
  currentNames: string[],
  existingNames: string[],
  createMissing: boolean,
): ScrapeRelationActionMap {
  const current = new Set(currentNames.map(relationKey));
  const existing = new Set(existingNames.map(relationKey));
  const actions: ScrapeRelationActionMap = {};

  for (const name of scrapedNames) {
    const key = relationKey(name);
    if (!key) continue;
    actions[key] = current.has(key) || existing.has(key) ? "include" : createMissing ? "create" : "exclude";
  }

  return actions;
}

export function buildRelationSelectionPayload(names: string[], actions: ScrapeRelationActionMap): ScrapeCollectionItemSelection[] {
  return names
    .map((name) => ({ name, action: actions[relationKey(name)] ?? "exclude" }))
    .filter((selection, index, items) => items.findIndex((candidate) => relationKey(candidate.name) === relationKey(selection.name)) === index);
}

export function countAppliedRelationSelections(names: string[], actions: ScrapeRelationActionMap, collectionMode?: string) {
  if (collectionMode === "skip") return 0;
  return names.filter((name) => (actions[relationKey(name)] ?? "exclude") !== "exclude").length;
}

export function ScrapeRelationChoices({
  names,
  currentNames,
  existingNames,
  displayNames,
  matchInfo,
  actions,
  onActionChange,
  disabled = false,
}: {
  names: string[];
  currentNames: string[];
  existingNames: string[];
  // relationKey(choice key) -> user-facing label. This lets callers use a stable identity key when
  // multiple entities share the same display name.
  displayNames?: Record<string, string>;
  // relationKey(scraped name) -> the existing entity's primary name. When it differs from the
  // scraped name, the match was via an alias; surfaced in the tooltip so a match is never a mystery.
  matchInfo?: Record<string, string>;
  actions: ScrapeRelationActionMap;
  onActionChange: (name: string, action: ScrapeCollectionItemAction) => void;
  disabled?: boolean;
}) {
  const current = new Set(currentNames.map(relationKey));
  const existing = new Set(existingNames.map(relationKey));

  return (
    <div className="mt-2 flex flex-wrap gap-1.5">
      {names.map((name) => {
        const key = relationKey(name);
        const displayName = displayNames?.[key] ?? name;
        const action = actions[key] ?? "exclude";
        const isCurrent = current.has(key);
        const existsLocally = isCurrent || existing.has(key);
        const nextAction = action === "exclude" ? existsLocally ? "include" : "create" : "exclude";
        // Show the matched primary name only when it differs from the scraped name (alias match).
        const matchedName = matchInfo?.[key];
        const aliasMatch = matchedName != null && relationKey(matchedName) !== key ? matchedName : null;
        const label = action === "exclude"
          ? "Excluded"
          : action === "create"
            ? "Will create"
            : isCurrent
              ? "Current"
              : existsLocally
                ? "Existing"
                : "Include only";
        const title = action === "exclude"
          ? `${displayName} is excluded`
          : action === "create"
            ? `${displayName} has no existing match — a new entry will be created`
            : isCurrent
              ? `${displayName} is already linked`
              : aliasMatch
                ? `${displayName} matches existing "${aliasMatch}" (alias) — no new entry created`
                : existsLocally
                  ? `${displayName} matches an existing entry — no new entry created`
                  : `${displayName} will be included`;

        return (
          <button
            type="button"
            key={key || name}
            disabled={disabled}
            onClick={() => onActionChange(name, nextAction)}
            title={title}
            aria-label={`${displayName}: ${label}`}
            className={`inline-flex max-w-full items-center gap-1 rounded border px-2 py-1 text-[11px] transition-colors ${getRelationChipClass(action, existsLocally, disabled)}`}
          >
            <RelationActionIcon action={action} />
            <span className="truncate">{displayName}</span>
          </button>
        );
      })}
    </div>
  );
}

function RelationActionIcon({ action }: { action: ScrapeCollectionItemAction }) {
  const Icon = action === "include" ? Check : action === "create" ? Plus : Minus;
  return <Icon className="h-2.5 w-2.5 shrink-0" />;
}

function getRelationChipClass(action: ScrapeCollectionItemAction, existsLocally: boolean, disabled: boolean) {
  if (disabled) {
    return "cursor-not-allowed border-border bg-surface text-muted opacity-55";
  }

  if (action === "exclude") {
    return "border-border bg-surface text-muted line-through opacity-70 hover:opacity-100";
  }

  if (action === "create" || !existsLocally) {
    return "border-amber-600/20 bg-amber-600/10 text-amber-300 hover:border-amber-500/40";
  }

  return "border-green-600/20 bg-green-600/10 text-green-300 hover:border-green-500/40";
}
