import { relationKey, scrapedAsTitle, type ScrapeRelationActionMap } from "./ScrapeRelationChoices";
import type { CollectionMode } from "./videoScrapeUtils";
import { collectionPresetHooks, idsForMode, modeForSelection, presetIncomingIds, sameSet } from "./VideoTaggerReview";
import { type DiffField, type DiffRecord, type DiffSelection, type WaitingNoteState } from "./MetadataDiff";

/**
 * Adapts the performer tagger's decision state (per-field strategies, collection modes, per-tag
 * actions) to the generic review rows, so a performer scrape is reviewed the same way a video scrape
 * and a merge are. The tagger's state model and its apply requests are unchanged: this file only
 * translates in both directions.
 */

export type PerformerFieldStrategy = "ignore" | "merge" | "overwrite";
export type PerformerTagAction = "include" | "create" | "exclude";

export interface PerformerReviewScalar {
  key: string;
  label: string;
  current?: string | number | null;
  scraped: string | number;
}

export interface PerformerReviewInput {
  /** The name of the source as it reads in a sentence, e.g. "StashDB" or "the scraper". */
  sourceName: string;
  /** Only the fields the source has a value for. */
  scalars: PerformerReviewScalar[];
  fieldStrategies: Record<string, PerformerFieldStrategy>;
  collectionModes: Record<string, CollectionMode>;
  currentImageUrl?: string | null;
  /** The incoming image on screen; the cover panel browses the rest. */
  incomingImageUrl?: string;
  urls: { current: string[]; incoming: string[] };
  aliases: { current: string[]; incoming: string[] };
  tags: {
    current: string[];
    incoming: string[];
    existing: string[];
    actions: ScrapeRelationActionMap;
    /**
     * The library tag each matched scraped name lands on, keyed by the scraped name's relation key (the
     * server's answer, which also matches aliases). A name landing on another name is shown as that tag.
     */
    matches?: Record<string, string>;
  };
  /**
   * Whether the tagger creates missing tags by default. When it does not, a new tag that is not chosen
   * is only offered ("not in your library"); when it does, one not chosen was left out on purpose.
   */
  createMissingTags?: boolean;
  /**
   * Set while the tagger is still asking the server which scraped tags are in the library: the tags row
   * cannot be changed and says this instead of an outcome, since which tags are new is not known yet.
   */
  tagsWaiting?: WaitingNoteState;
  /**
   * Sets a collection's mode directly, for a preset that selects exactly what another one does: the
   * selection cannot say which of them was meant.
   */
  onCollectionModeChange?: (field: string, mode: CollectionMode) => void;
}

export interface PerformerReviewHandlers {
  onFieldStrategyChange: (field: string, strategy: PerformerFieldStrategy) => void;
  onCollectionModeChange: (field: string, mode: CollectionMode) => void;
  /** Every changed tag of one action, keyed by relation key, in one call. */
  onTagActionsChange: (actions: Record<string, PerformerTagAction>) => void;
}

interface ReviewItem {
  id: string;
  label: string;
  isNew: boolean;
  /** Scraped spellings that land on this library tag under another name. */
  scrapedAs?: string[];
  /** The scrape also returned the label itself, besides the names in `scrapedAs`. */
  scrapedAsLabelToo?: boolean;
}

const itemKey = (value: unknown) => (value as ReviewItem).id;
const itemLabel = (value: unknown) => (value as ReviewItem).label;
const renderItem = (value: unknown) => {
  const entry = value as ReviewItem;
  if (!entry.scrapedAs) return entry.label;
  const title = scrapedAsTitle(entry.scrapedAs, entry.scrapedAsLabelToo);
  return (
    <span title={title}>
      {entry.label}
      <span className="sr-only"> ({title})</span>
    </span>
  );
};
const itemIsNew = (value: unknown) => (value as ReviewItem).isNew;
const plainItems = (values: string[]): ReviewItem[] =>
  [...new Set(values.map((value) => value.trim()).filter(Boolean))].map((value) => ({
    id: value,
    label: value,
    isNew: false,
  }));

/**
 * The tags row's items. Scraped names are grouped by the library tag they land on, as the video tagger
 * does, so a name matched by alias reads as that tag and folds into it when the performer has it. A group
 * is chosen while any of its scraped names is, and toggling it toggles all of them, since the apply sends
 * scraped names.
 */
function tagItems(input: PerformerReviewInput) {
  const currentIds = new Set(input.tags.current.map(relationKey));
  const existing = new Set(input.tags.existing.map(relationKey));
  const matches = input.tags.matches ?? {};
  const dedupe = (names: string[]) => [...new Map(names.map((name) => [relationKey(name), name])).entries()];
  const current = dedupe(input.tags.current).map(([id, label]) => ({ id, label, isNew: false }));
  const currentLabels = new Map(current.map((tag) => [tag.id, tag.label]));
  const groups = new Map<string, { label: string; members: string[]; scrapedAs: string[] }>();
  for (const [key, name] of dedupe(input.tags.incoming)) {
    const library = matches[key];
    const id = relationKey(library ?? name);
    const group = groups.get(id) ?? { label: currentLabels.get(id) ?? library ?? name, members: [], scrapedAs: [] };
    group.members.push(key);
    if (id !== key) group.scrapedAs.push(name);
    groups.set(id, group);
  }
  const incoming: ReviewItem[] = [...groups].map(([id, group]) => ({
    id,
    label: group.label,
    isNew: !currentIds.has(id) && !group.members.some((key) => existing.has(key)),
    scrapedAs: group.scrapedAs.length > 0 ? group.scrapedAs : undefined,
    scrapedAsLabelToo: group.scrapedAs.length > 0 && group.scrapedAs.length < group.members.length,
  }));
  const included = [...groups]
    .filter(([, group]) => group.members.some((key) => input.tags.actions[key] !== "exclude"))
    .map(([id]) => id);
  const membersOf = (id: string) => groups.get(id)?.members ?? [id];
  return { current, incoming, included, existing, membersOf };
}

export function buildPerformerReview(input: PerformerReviewInput) {
  const fields: DiffField[] = [];
  const sourceValues: Record<string, unknown> = {};
  const targetValues: Record<string, unknown> = {};
  const selection: DiffSelection = {};

  for (const scalar of input.scalars) {
    fields.push({
      key: scalar.key,
      label: scalar.label,
      equal: (left, right) => String(left ?? "").trim() === String(right ?? "").trim(),
    });
    sourceValues[scalar.key] = scalar.scraped;
    targetValues[scalar.key] = scalar.current ?? null;
    selection[scalar.key] = input.fieldStrategies[scalar.key] === "overwrite" ? "source" : "target";
  }
  if (input.incomingImageUrl) {
    fields.push({
      key: "image",
      label: "Image",
      alwaysVisible: true,
      render: (value) => (
        <img src={String(value)} alt="Performer image" className="max-h-56 w-full rounded object-contain" />
      ),
    });
    sourceValues.image = input.incomingImageUrl;
    targetValues.image = input.currentImageUrl || null;
    selection.image = input.fieldStrategies.image === "overwrite" ? "source" : "target";
  }
  // With a collection switched off, its chips are not the way back in: the presets are.
  const listField = (key: string, label: string, mode: CollectionMode, modesOnly: boolean): DiffField => ({
    key,
    label,
    kind: "list",
    itemKey,
    itemLabel,
    renderItem,
    itemIsNew,
    modesOnly: modesOnly || mode === "skip",
    lockKeptItems: true,
    ...collectionPresetHooks(key, mode, input.onCollectionModeChange),
  });
  const plainList = (key: "urls" | "aliases", label: string) => {
    const side = input[key];
    if (side.incoming.length === 0) return;
    const mode = input.collectionModes[key] ?? "merge";
    const current = plainItems(side.current);
    const incoming = plainItems(side.incoming);
    fields.push(listField(key, label, mode, true));
    sourceValues[key] = incoming;
    targetValues[key] = current;
    selection[key] = idsForMode(
      mode,
      current.map((entry) => entry.id),
      incoming.map((entry) => entry.id),
    );
  };
  plainList("urls", "URLs");
  plainList("aliases", "Aliases");
  if (input.tags.incoming.length > 0) {
    const tags = tagItems(input);
    const mode = input.collectionModes.tags ?? "merge";
    fields.push({
      ...listField("tags", "Tags", mode, false),
      // A preset is a mode, not a way to create every new tag at once.
      newItemsOnlyWhenChosen: true,
      unchosenNewItemsOffered: input.createMissingTags === false,
      waiting: input.tagsWaiting,
    });
    sourceValues.tags = tags.incoming;
    targetValues.tags = tags.current;
    selection.tags = idsForMode(
      mode,
      tags.current.map((tag) => tag.id),
      tags.included,
    );
  }

  const source: DiffRecord = {
    label: `From ${input.sourceName}`,
    sentenceLabel: input.sourceName,
    values: sourceValues,
  };
  const target: DiffRecord = { label: "Current", sentenceLabel: "current", values: targetValues };
  return { fields, source, target, selection };
}

/**
 * Translates a changed selection back into the tagger's callbacks. Scalars and the image flip a field
 * strategy; a list first checks whether the selection is one of the presets (which sets the
 * collection mode) and then reports, in one call, every incoming-only tag whose inclusion changed.
 */
export function applyPerformerSelectionChange(
  input: PerformerReviewInput,
  previous: DiffSelection,
  next: DiffSelection,
  handlers: PerformerReviewHandlers,
) {
  for (const key of [...input.scalars.map((scalar) => scalar.key), "image"]) {
    if (next[key] !== previous[key] && next[key] != null)
      handlers.onFieldStrategyChange(key, next[key] === "source" ? "overwrite" : "ignore");
  }

  const listChange = (
    key: "urls" | "aliases" | "tags",
    currentIds: string[],
    incomingIds: string[],
    onToggle?: (changed: string[], chosen: Set<string>) => void,
    presetIds = incomingIds,
  ) => {
    const selected = next[key];
    const before = (previous[key] as string[] | undefined) ?? [];
    if (!Array.isArray(selected) || sameSet(selected, before)) return;
    const previousMode = input.collectionModes[key] ?? "merge";
    const chosen = new Set(selected);
    const wasSelected = new Set(before);
    const currentSet = new Set(currentIds);
    const changed = incomingIds.filter((id) => !currentSet.has(id) && chosen.has(id) !== wasSelected.has(id));
    const currentChanged = currentIds.some((id) => chosen.has(id) !== wasSelected.has(id));
    // One incoming chip flipped is a per-item choice even when the result happens to look like a
    // preset (dropping the last incoming item leaves exactly the current side).
    const singleChip = Boolean(onToggle) && changed.length === 1 && !currentChanged && previousMode !== "skip";
    const mode = singleChip ? previousMode : modeForSelection(chosen, currentIds, presetIds, previousMode);
    if (mode !== previousMode) handlers.onCollectionModeChange(key, mode);
    // Switched off, or just switched back on: the incoming side is taken as a whole and exclusions
    // stay as they were.
    if (mode === "skip" || previousMode === "skip" || !onToggle || changed.length === 0) return;
    onToggle(changed, chosen);
  };

  const ids = (values: string[]) => plainItems(values).map((entry) => entry.id);
  listChange("urls", ids(input.urls.current), ids(input.urls.incoming));
  listChange("aliases", ids(input.aliases.current), ids(input.aliases.incoming));
  const tags = tagItems(input);
  listChange(
    "tags",
    tags.current.map((tag) => tag.id),
    tags.incoming.map((tag) => tag.id),
    (changed, chosen) =>
      handlers.onTagActionsChange(
        Object.fromEntries(
          changed.flatMap((id) =>
            tags
              .membersOf(id)
              .map((key) => [key, chosen.has(id) ? (tags.existing.has(key) ? "include" : "create") : "exclude"]),
          ),
        ),
      ),
    // The presets offer a new tag only once it has been chosen (the list row does the same).
    presetIncomingIds(
      tags.incoming,
      tags.current.map((tag) => tag.id),
      (previous.tags as string[] | undefined) ?? [],
    ),
  );
}
