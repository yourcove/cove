import { useMemo } from "react";
import { useSfwMode } from "../state/AppConfigContext";
import { PERFORMER_SORT_OPTIONS } from "./performerSortOptions";

/** Performer sort keys that order by a field SFW mode hides. */
const SFW_HIDDEN_PERFORMER_SORTS: ReadonlySet<string> = new Set(["measurements"]);

/**
 * Removes the performer sort options SFW mode hides. The sort in use stays listed, so a list already
 * sorted by a hidden field still says so and can be changed.
 */
export function visiblePerformerSortOptions<T extends { value: string }>(
  options: T[],
  sfwMode: boolean,
  activeSort?: string,
): T[] {
  if (!sfwMode) return options;
  return options.filter((option) => option.value === activeSort || !SFW_HIDDEN_PERFORMER_SORTS.has(option.value));
}

/** The performer sort options to offer in the current content mode. */
export function usePerformerSortOptions(activeSort?: string) {
  const sfwMode = useSfwMode();
  return useMemo(() => visiblePerformerSortOptions(PERFORMER_SORT_OPTIONS, sfwMode, activeSort), [activeSort, sfwMode]);
}

/** Field ids of the metadata-server batch dialog that SFW mode hides and always keeps at their current value. */
export const SFW_HIDDEN_METADATA_BATCH_FIELDS: readonly string[] = ["measurements", "faketits", "tattoos", "piercings"];

/**
 * Field strategies that make a metadata-server performer import leave the fields SFW mode hides alone.
 * The server fetches the performer itself and fills any field without a strategy, so the import has to
 * name them. The keys are the server's, and are case-sensitive.
 */
export const SFW_IGNORED_METADATA_IMPORT_STRATEGIES = {
  measurements: "ignore",
  fakeTits: "ignore",
  tattoos: "ignore",
  piercings: "ignore",
} as const;

/**
 * Drops the values SFW mode hides from a scraper's performer tagger match, so the tagger neither shows
 * nor applies them. Metadata-server matches carry no such values: their import is covered by
 * {@link SFW_IGNORED_METADATA_IMPORT_STRATEGIES}.
 */
export function withoutSfwHiddenScrapedFields<
  T extends { scraped?: { measurements?: string; tattoos?: string; piercings?: string } },
>(match: T, sfwMode: boolean): T {
  if (!sfwMode || !match.scraped) return match;
  return { ...match, scraped: { ...match.scraped, measurements: undefined, tattoos: undefined, piercings: undefined } };
}
