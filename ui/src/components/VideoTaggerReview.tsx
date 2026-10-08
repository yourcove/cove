import { useEffect, useId, useRef, useState } from "react";
import { Link2, Plus, X } from "lucide-react";
import type {
  MetadataServer,
  MetadataServerEntityCandidate,
  MetadataServerVideoMatch,
  Video,
  VideoCoverComparison,
} from "../api/types";
import { videos } from "../api/client";
import {
  EntityReferenceMultiSelector,
  EntityReferenceSelector,
  useEntityReferenceOptions,
  type EntityReferenceOption,
} from "./EntityReferenceSelector";
import { metadataServerLabel } from "./MetadataServerLinks";
import { relationKey, type ScrapeRelationActionMap } from "./ScrapeRelationChoices";
import type { CollectionMode } from "./videoScrapeUtils";
import {
  renderDiffValue,
  type DiffField,
  type DiffListMode,
  type DiffRecord,
  type DiffSelection,
} from "./MetadataDiff";

/**
 * Adapts the video tagger's decision state (per-field strategies, collection modes, per-item
 * exclusions, hand edits) to the generic review rows in `MetadataDiff`, so a scrape result is reviewed
 * with the same rows, pills and chips as a merge. The tagger's state model and apply request are
 * unchanged: this file only translates in both directions.
 */

export type TaggerFieldStrategy = "ignore" | "merge" | "overwrite";

export interface TaggerPerformerChoice {
  key: string;
  label: string;
  candidate: MetadataServerEntityCandidate;
}

/** Library items added through search, and current items taken off, beside the scrape. */
export interface TaggerRelationshipEdits {
  added: number[];
  removed: number[];
}

export type TaggerRelationshipKey = "tags" | "performers";

export interface TaggerReviewInput {
  video: Video;
  result: MetadataServerVideoMatch;
  /** The name of the source as it reads in a sentence, e.g. "StashDB" or "the scraper". */
  sourceName: string;
  metadataServers?: Pick<MetadataServer, "endpoint" | "name">[];
  fieldStrategies: Record<string, TaggerFieldStrategy>;
  imageReplace: boolean;
  /**
   * What the pixels say about the two covers, once the comparison has answered. Absent until then,
   * and for a video whose cover is an auto-generated frame, where the URLs are the only guide.
   */
  coverComparison?: VideoCoverComparison;
  collectionModes: Record<string, CollectionMode>;
  /**
   * The rows whose outcome the tagger cannot say yet (it is still asking the library which scraped items it
   * has), each with the note it shows instead; those rows cannot be changed meanwhile.
   */
  relationsWaiting?: Partial<Record<"studio" | TaggerRelationshipKey, string>>;
  showStudio: boolean;
  /** The scraped studio is not in the library. False while that is not known yet. */
  studioIsNew?: boolean;
  /** A new studio is created when chosen: the tagger creates missing studios, or the person chose to. */
  createStudio?: boolean;
  /** The library studio the scraped one resolved to, when its name differs (an alias, say). */
  studioMatchName?: string;
  /** Chooses to create a new studio and use it. */
  onCreateStudio?: () => void;
  showTags: boolean;
  showPerformers: boolean;
  /**
   * The tagger creates missing tags or performers by default, so a new one that is not chosen was taken
   * out on purpose (left out) rather than only offered. Off when absent, as for a new tagger config.
   */
  createMissingTags?: boolean;
  createMissingPerformers?: boolean;
  currentTagNames: string[];
  existingTagNames: string[];
  tagActions: ScrapeRelationActionMap;
  /** Which existing entity a scraped name resolved to, when it matched through an alias. */
  tagMatchInfo?: Record<string, string>;
  performerChoices: TaggerPerformerChoice[];
  currentPerformerChoiceKeys: string[];
  performerActions: ScrapeRelationActionMap;
  tagEdits?: TaggerRelationshipEdits;
  performerEdits?: TaggerRelationshipEdits;
  /**
   * Remembers a scraped tag name as an alias of a library tag, so it matches that tag from now on.
   * Without it the review offers no way to link a scraped tag that matched nothing.
   */
  onLinkTag?: (scrapedName: string, tag: EntityReferenceOption) => Promise<void>;
  /**
   * Sets a collection's mode directly, for a preset that selects exactly what is already selected
   * (nothing incoming to add, say): the selection cannot say which of the equal presets was meant.
   */
  onCollectionModeChange?: (field: string, mode: CollectionMode) => void;
}

const PRESET_OF_MODE: Record<CollectionMode, DiffListMode> = { merge: "combined", skip: "target", replace: "source" };
const MODE_OF_PRESET: Record<DiffListMode, CollectionMode> = { combined: "merge", target: "skip", source: "replace" };

/**
 * The list row's hooks for a tagger collection: the tagger's mode is the preset to highlight, and a
 * click on a preset the selection cannot express (it selects what another one does) sets the mode.
 */
export function collectionPresetHooks(
  key: string,
  mode: CollectionMode,
  onCollectionModeChange?: (field: string, mode: CollectionMode) => void,
): Pick<DiffField, "activeMode" | "onModeSelect"> {
  return {
    activeMode: PRESET_OF_MODE[mode],
    onModeSelect: onCollectionModeChange
      ? (preset) => {
          if (MODE_OF_PRESET[preset] !== mode) onCollectionModeChange(key, MODE_OF_PRESET[preset]);
        }
      : undefined,
  };
}

/**
 * The incoming ids the presets select when new items join them only once chosen (see
 * `newItemsOnlyWhenChosen`): items the library has, and new ones in the previous selection.
 */
export function presetIncomingIds(
  incoming: { id: string; isNew: boolean }[],
  currentIds: string[],
  chosenBefore: Iterable<string>,
) {
  const chosen = new Set(chosenBefore);
  return incoming
    .filter((entry) => !entry.isNew || currentIds.includes(entry.id) || chosen.has(entry.id))
    .map((entry) => entry.id);
}

export interface TaggerReviewHandlers {
  onFieldStrategyChange?: (field: string, strategy: TaggerFieldStrategy) => void;
  onCollectionModeChange?: (field: string, mode: CollectionMode) => void;
  /** Every changed tag of one action, by name, in one call. */
  onToggleTag?: (names: string[]) => void;
  /** Every changed performer of one action, by choice key, in one call. */
  onTogglePerformer?: (choiceKeys: string[]) => void;
  /** The complete hand edits of one relationship after a change. */
  onRelationshipEditsChange?: (key: TaggerRelationshipKey, edits: TaggerRelationshipEdits) => void;
}

export interface ReviewItem {
  id: string;
  label: string;
  isNew: boolean;
  /** The scraped names behind this item that differ from its label, which is the library entity's. */
  scrapedAs?: string[];
  /** The library entity behind this item, when known: the selector edits it by this id. */
  localId?: number;
  /** On the current side (a matched scraped item is on both). */
  inTarget?: boolean;
  /** A current item the edit form would not let go either: derived or managed by an extension. */
  locked?: boolean;
}

const SCALAR_KEYS = ["title", "code", "details", "director", "date"] as const;
const HAND_EDITED_SOURCES = new Set(["user", "manual"]);
/** A selection id for a library item added through search, outside both compared sides. */
const LIBRARY_PREFIX = "library:";
export const libraryItemId = (id: number) => `${LIBRARY_PREFIX}${id}`;
const libraryIdOf = (selectionId: string) => {
  if (!selectionId.startsWith(LIBRARY_PREFIX)) return undefined;
  const id = Number(selectionId.slice(LIBRARY_PREFIX.length));
  return Number.isInteger(id) && id > 0 ? id : undefined;
};

/** True when the person set this field by hand, so a scrape should not silently overwrite it. */
export function isHandEdited(video: Video, fieldKey: string) {
  return (video.fieldProvenance ?? []).some(
    (entry) => entry.fieldKey === fieldKey && HAND_EDITED_SOURCES.has(entry.sourceKey.trim().toLowerCase()),
  );
}

const provenanceLabel = (sourceKey: string, servers: Pick<MetadataServer, "endpoint" | "name">[]) =>
  HAND_EDITED_SOURCES.has(sourceKey.trim().toLowerCase()) ? "Edited by you" : metadataServerLabel(sourceKey, servers);

const normalize = (value?: string | null) => (value ?? "").trim();
const byLabel = <T extends { label: string }>(items: T[]) =>
  [...items].sort((left, right) => left.label.localeCompare(right.label, undefined, { sensitivity: "base" }));

const item = (id: string, label: string, isNew = false, scrapedAs?: string[], localId?: number): ReviewItem => ({
  id,
  label,
  isNew,
  scrapedAs: scrapedAs?.length ? scrapedAs : undefined,
  localId,
});
const itemKey = (value: unknown) => (value as ReviewItem).id;
const itemLabel = (value: unknown) => (value as ReviewItem).label;
const itemIsNew = (value: unknown) => (value as ReviewItem).isNew;
const scrapedAsTitle = (entry: ReviewItem) =>
  entry.scrapedAs ? `Scraped as ${entry.scrapedAs.map((name) => `“${name}”`).join(", ")}` : undefined;
// The library name is the tag the video gets; the scraped spelling stays out of the way, on hover.
const renderItem = (value: unknown) => {
  const entry = value as ReviewItem;
  return entry.scrapedAs ? (
    <span title={scrapedAsTitle(entry)}>
      {entry.label}
      <span className="sr-only"> ({scrapedAsTitle(entry)})</span>
    </span>
  ) : (
    entry.label
  );
};

/** The ids a collection mode selects, given what each side has. */
export function idsForMode(mode: CollectionMode, current: string[], incoming: string[]) {
  if (mode === "skip") return current;
  if (mode === "replace") return incoming;
  return [...new Set([...current, ...incoming])];
}

export const sameSet = (left: Iterable<string>, right: Iterable<string>) => {
  const a = new Set(left);
  const b = new Set(right);
  return a.size === b.size && [...a].every((id) => b.has(id));
};

/**
 * The collection mode a selection expresses. The three presets are recognised by their exact id
 * sets, so a scraped item that is also current (one id on both sides) cannot mask them; anything else
 * is a per-item change that keeps the current mode.
 */
export function modeForSelection(
  selected: Iterable<string>,
  current: string[],
  incoming: string[],
  previous: CollectionMode,
): CollectionMode {
  const combined = [...new Set([...current, ...incoming])];
  const matching = (
    [
      ["merge", combined],
      ["skip", current],
      ["replace", incoming],
    ] as const
  )
    .filter(([, ids]) => sameSet(selected, ids))
    .map(([mode]) => mode);
  // Presets that select the same items cannot be told apart by the selection, so the mode stays as it
  // is when it is one of them; an explicit click on another one arrives through `onModeSelect`.
  if (matching.includes(previous)) return previous;
  if (matching.length > 0) return matching[0];
  return previous === "skip" ? "merge" : previous;
}

function tagItems(input: TaggerReviewInput) {
  const localTags = new Map(input.video.tags.map((tag) => [relationKey(tag.name), tag]));
  const localIds = new Map([...localTags].map(([key, tag]) => [key, tag.id]));
  const current = byLabel(
    input.currentTagNames.map((name) => {
      const tag = localTags.get(relationKey(name));
      return {
        ...item(relationKey(name), name, false, undefined, tag?.id),
        inTarget: true,
        locked: tag?.canRemove === false || tag?.isDerived === true,
      };
    }),
  );
  const currentIds = new Set(current.map((tag) => tag.id));
  const existing = new Set(input.existingTagNames.map(relationKey));
  const candidates = new Map(input.result.tagCandidates.map((candidate) => [relationKey(candidate.name), candidate]));
  // A scraped name that resolved through an alias is shown as the library tag it lands on, so the
  // names landing on one tag are one item, and one landing on a current tag is that current item.
  const groups = new Map<string, { label: string; names: string[] }>();
  for (const name of input.result.tagNames) {
    const label = input.tagMatchInfo?.[relationKey(name)] ?? name;
    const id = relationKey(label);
    const group = groups.get(id);
    // Two spellings of one name are one tag; toggling both would flip it twice.
    if (group?.names.some((known) => relationKey(known) === relationKey(name))) continue;
    if (group) group.names.push(name);
    else groups.set(id, { label, names: [name] });
  }
  // A tag the video has, which the scrape also returned under another name, says so on hover too.
  const currentWithScrapedAs = current.map((tag) => {
    const scrapedAs = groups.get(tag.id)?.names.filter((name) => relationKey(name) !== tag.id);
    return scrapedAs?.length ? { ...tag, scrapedAs } : tag;
  });
  const incoming = byLabel(
    [...groups].map(([id, { label, names }]) => {
      const localId = names.map((name) => candidates.get(relationKey(name))?.localId).find((value) => value != null);
      return item(
        id,
        label,
        !currentIds.has(id) && !names.some((name) => existing.has(relationKey(name))),
        names.filter((name) => relationKey(name) !== id),
        localId ?? localIds.get(id),
      );
    }),
  );
  const nameIncluded = (name: string) => input.tagActions[relationKey(name)] !== "exclude";
  // A name that lands on a tag the video has is that tag, which only a hand removal takes off; an
  // exclusion made while the name still stood on its own (before an alias matched it) means nothing here.
  // The video tagger's actions already say so; the review does not rely on its caller for it.
  const included = [...groups]
    .filter(([id, group]) => currentIds.has(id) || group.names.some(nameIncluded))
    .map(([id]) => id);
  // Flipping an item flips only the names behind it that are not already on its new side.
  const namesToToggle = (id: string) => {
    const names = groups.get(id)?.names ?? [];
    const wasIncluded = names.some(nameIncluded);
    return names.filter((name) => nameIncluded(name) === wasIncluded);
  };
  return { current: currentWithScrapedAs, incoming, included, namesToToggle };
}

const performerIdentity = (performer: { name: string; disambiguation?: string | null }) =>
  performer.disambiguation?.trim() ? `${performer.name} (${performer.disambiguation.trim()})` : performer.name;

function performerItems(input: TaggerReviewInput) {
  const matched = new Set(input.currentPerformerChoiceKeys.map(relationKey));
  const byIdentity = new Map(
    input.video.performers.map((performer) => [relationKey(performerIdentity(performer)), performer.id]),
  );
  const choices = input.performerChoices.map((choice) => ({
    ...item(
      relationKey(choice.key),
      choice.label,
      !choice.candidate.existsLocally,
      undefined,
      choice.candidate.localId ?? byIdentity.get(relationKey(performerIdentity(choice.candidate))),
    ),
    choiceKey: choice.key,
    candidate: choice.candidate,
  }));
  const matchedChoices = choices.filter((choice) => matched.has(choice.id));
  // A current performer is represented by its scraped counterpart when one matched it, whether the
  // match came through a local id or through the same name.
  const linkedIds = new Set(matchedChoices.map((choice) => choice.localId).filter((id) => id != null));
  const linkedNames = new Set(matchedChoices.map((choice) => relationKey(performerIdentity(choice.candidate))));
  const current = byLabel([
    ...matchedChoices.map((choice) => ({
      ...item(choice.id, choice.label, false, undefined, choice.localId),
      inTarget: true,
    })),
    ...input.video.performers
      .filter(
        (performer) => !linkedIds.has(performer.id) && !linkedNames.has(relationKey(performerIdentity(performer))),
      )
      .map((performer) => ({
        ...item(`local:${performer.id}`, performerIdentity(performer), false, undefined, performer.id),
        inTarget: true,
      })),
  ]);
  const included = choices
    .filter((choice) => input.performerActions[choice.id] !== "exclude")
    .map((choice) => choice.id);
  return {
    current,
    incoming: byLabel(choices),
    included,
    choiceKeys: new Map(choices.map((choice) => [choice.id, choice.choiceKey])),
  };
}

const urlItems = (input: TaggerReviewInput) => ({
  current: input.video.urls.map((url) => item(url, url)),
  incoming: input.result.urls.map((url) => item(url, url)),
});

/** The selection of a relationship: the mode's ids, minus hand removals, plus library additions. */
function relationshipSelection(
  mode: CollectionMode,
  current: ReviewItem[],
  included: string[],
  edits: TaggerRelationshipEdits | undefined,
) {
  const removed = new Set(edits?.removed ?? []);
  const ids = idsForMode(
    mode,
    current.map((entry) => entry.id),
    included,
  ).filter((id) => {
    const localId = [...current].find((entry) => entry.id === id)?.localId;
    return localId == null || !removed.has(localId);
  });
  return [...ids, ...(edits?.added ?? []).map(libraryItemId)];
}

/**
 * Maps the selector's id list back to a selection. The selector owns the current side and the
 * search additions; scraped items keep their own chips, so an id that matches a scraped item selects
 * that item while the incoming side is shown and is a plain library addition while it is switched off.
 */
export function selectorChange(items: ReviewItem[], selected: string[], ids: number[], incomingHidden: boolean) {
  const current = items.filter((entry) => entry.inTarget && entry.localId != null);
  const incomingByLocalId = new Map(
    items.filter((entry) => !entry.inTarget && entry.localId != null).map((entry) => [entry.localId!, entry.id]),
  );
  const next = new Set(selected.filter((id) => libraryIdOf(id) == null && !current.some((entry) => entry.id === id)));
  for (const entry of current) if (ids.includes(entry.localId!)) next.add(entry.id);
  for (const id of ids) {
    if (current.some((entry) => entry.localId === id)) continue;
    const incomingId = incomingHidden ? undefined : incomingByLocalId.get(id);
    if (incomingId != null) next.add(incomingId);
    else next.add(libraryItemId(id));
  }
  return [...next];
}

/**
 * The relationship row as the video's edit form shows it: the app's selector with chips, x buttons
 * and search-to-add for the current items and anything added through search, and chip strips for
 * the scraped items: those the library has (green, struck through when left out), and apart from
 * them those it does not (amber when they will be created, plain with + and, for tags, a link action
 * when not). While the collection is switched off the scraped strips are hidden.
 */
function RelationshipEditor({
  entityType,
  label,
  placeholder,
  items,
  selected,
  onChange,
  disabled,
  incomingHidden,
  waiting = false,
  onLinkTag,
}: {
  entityType: "tag" | "performer";
  label: string;
  placeholder: string;
  items: ReviewItem[];
  selected: string[];
  onChange: (selected: string[]) => void;
  disabled: boolean;
  incomingHidden: boolean;
  /** The library has not said yet which scraped items it has, so none is called new or matched. */
  waiting?: boolean;
  onLinkTag?: TaggerReviewInput["onLinkTag"];
}) {
  const chosen = new Set(selected);
  const current = items.filter((entry) => entry.inTarget && entry.localId != null);
  const libraryIds = selected
    .map(libraryIdOf)
    .filter((id): id is number => id != null && !current.some((entry) => entry.localId === id));
  const seedOptions: EntityReferenceOption[] = current.map((entry) => ({ id: entry.localId!, label: entry.label }));
  const labelOf = new Map(seedOptions.map((option) => [option.id, option.label]));
  // The selector draws the current items; what the search added is drawn apart as additions, the way
  // added scraped items are, so it is not mistaken for something the video already has.
  const addedOptions = useEntityReferenceOptions(entityType, libraryIds);
  const values = [...new Set(current.filter((entry) => chosen.has(entry.id)).map((entry) => entry.localId!))].sort(
    (left, right) =>
      (labelOf.get(left) ?? "￿").localeCompare(labelOf.get(right) ?? "￿", undefined, { sensitivity: "base" }),
  );
  const lockedIds = current.filter((entry) => entry.locked).map((entry) => entry.localId!);
  const valueTitles = Object.fromEntries(
    current.flatMap((entry) => {
      const title = scrapedAsTitle(entry);
      return title ? [[entry.localId!, title]] : [];
    }),
  );
  const scraped = incomingHidden ? [] : items.filter((entry) => !entry.inTarget);
  // What the library already knows comes first; what it does not is kept apart, so a new item is
  // always a visible decision whether or not the tagger creates missing items by default.
  // While the library has not said, the scraped items stand together, with nothing called new or matched.
  const matched = waiting ? scraped : scraped.filter((entry) => !entry.isNew);
  const unknown = waiting ? [] : scraped.filter((entry) => entry.isNew);
  const [linking, setLinking] = useState<ReviewItem | null>(null);
  const linkIdPrefix = useId();
  const rowRef = useRef<HTMLDivElement>(null);
  // Focus goes back to the chip's link button; once a remembered alias has moved the chip out of the
  // strip there is no such button, and the tag search is the natural next step.
  const closeLink = (entry: ReviewItem) => {
    setLinking(null);
    requestAnimationFrame(() =>
      (document.getElementById(`${linkIdPrefix}-link-${entry.id}`) ?? rowRef.current?.querySelector("input"))?.focus(),
    );
  };
  const toggle = (entry: ReviewItem) =>
    onChange(chosen.has(entry.id) ? selected.filter((id) => id !== entry.id) : [...selected, entry.id]);
  const chip = (entry: ReviewItem) => {
    const included = chosen.has(entry.id);
    const isNew = entry.isNew && !waiting;
    const state = waiting ? "available" : !included ? (isNew ? "available" : "excluded") : isNew ? "new" : "added";
    const chipClass = {
      new: "border-amber-400/50 bg-card text-amber-300",
      added: "border-green-400/50 bg-card text-green-300",
      available: "border-border bg-transparent text-muted",
      excluded: "border-dashed border-border bg-transparent text-muted line-through",
    }[state];
    return (
      <span
        key={entry.id}
        data-state={state}
        title={state === "new" ? "Not in your library yet; will be created" : undefined}
        className={`inline-flex max-w-full items-center gap-1.5 rounded border py-0.5 pl-2 pr-1 text-xs ${chipClass}`}
      >
        {included && !waiting ? <Plus className="h-3 w-3 shrink-0" /> : null}
        <span className="min-w-0 truncate">{renderItem(entry)}</span>
        {state === "new" ? <span className="sr-only"> (new, will be created)</span> : null}
        <button
          type="button"
          disabled={disabled}
          onClick={() => toggle(entry)}
          aria-label={`${included ? "Remove" : isNew ? "Create and add" : "Add"} ${label}: ${entry.label}`}
          title={!included && isNew ? `Create “${entry.label}” and add it` : undefined}
          className="inline-flex h-4.5 w-4.5 shrink-0 items-center justify-center rounded-full bg-white/10 hover:bg-white/20"
        >
          {included ? <X className="h-2.5 w-2.5" /> : <Plus className="h-2.5 w-2.5" />}
        </button>
        {isNew && onLinkTag ? (
          <button
            type="button"
            id={`${linkIdPrefix}-link-${entry.id}`}
            disabled={disabled}
            onClick={() => setLinking(entry)}
            aria-label={`Link ${label}: ${entry.label} to a library tag`}
            title={`Use a tag you already have for “${entry.label}”`}
            className="inline-flex h-4.5 w-4.5 shrink-0 items-center justify-center rounded-full bg-white/10 hover:bg-white/20"
          >
            <Link2 className="h-2.5 w-2.5" />
          </button>
        ) : null}
      </span>
    );
  };
  const addedChip = (id: number) => {
    const name = addedOptions.get(id)?.label ?? `Loading ${entityType}…`;
    return (
      <span
        key={`library-${id}`}
        data-state="added"
        title="Added from your library"
        className="inline-flex max-w-full items-center gap-1.5 rounded border border-green-400/50 bg-card py-0.5 pl-2 pr-1 text-xs text-green-300"
      >
        <Plus className="h-3 w-3 shrink-0" />
        <span className="min-w-0 truncate">{name}</span>
        <button
          type="button"
          disabled={disabled}
          onClick={() => onChange(selected.filter((selectedId) => selectedId !== libraryItemId(id)))}
          aria-label={`Remove ${label}: ${name}`}
          className="inline-flex h-4.5 w-4.5 shrink-0 items-center justify-center rounded-full bg-white/10 hover:bg-white/20"
        >
          <X className="h-2.5 w-2.5" />
        </button>
      </span>
    );
  };
  return (
    <div ref={rowRef} className="flex flex-col gap-2">
      {matched.length || libraryIds.length ? (
        <div className="flex flex-wrap gap-1.5">
          {matched.map(chip)}
          {libraryIds.map(addedChip)}
        </div>
      ) : null}
      {unknown.length ? (
        <div role="group" aria-labelledby={`${linkIdPrefix}-heading`} className="flex flex-col gap-1">
          <span id={`${linkIdPrefix}-heading`} className="text-[11px] text-muted">
            Not in your library
          </span>
          <div className="flex flex-wrap gap-1.5">{unknown.map(chip)}</div>
        </div>
      ) : null}
      {linking && onLinkTag && unknown.some((entry) => entry.id === linking.id) ? (
        <TagLinkPanel
          scrapedName={linking.label}
          disabled={disabled}
          onCancel={() => closeLink(linking)}
          onLink={async (tag, rememberAlias) => {
            if (rememberAlias) await onLinkTag(linking.label, tag);
            // Just this video: the scraped name stays out and the library tag goes in, the way the
            // search below adds one, so a tag already on either side is selected rather than repeated.
            else
              onChange(
                selectorChange(
                  items,
                  selected.filter((id) => id !== linking.id),
                  [...values, ...libraryIds, tag.id],
                  incomingHidden,
                ),
              );
            closeLink(linking);
          }}
        />
      ) : null}
      <EntityReferenceMultiSelector
        entityType={entityType}
        values={values}
        excludeIds={libraryIds}
        lockedIds={lockedIds}
        valueTitles={valueTitles}
        onChange={(ids) => onChange(selectorChange(items, selected, [...ids, ...libraryIds], incomingHidden))}
        placeholder={placeholder}
        seedOptions={seedOptions}
        disabled={disabled}
      />
    </div>
  );
}

/**
 * Links a scraped tag that matched nothing to a tag the library already has. Remembering the scraped
 * name as an alias changes the library tag, not just this video, so it takes effect at once and every
 * later scrape matches it; without that, the library tag is added to this video alone.
 */
function TagLinkPanel({
  scrapedName,
  disabled,
  onLink,
  onCancel,
}: {
  scrapedName: string;
  disabled: boolean;
  onLink: (tag: EntityReferenceOption, rememberAlias: boolean) => Promise<void>;
  onCancel: () => void;
}) {
  const inputId = useId();
  const [tag, setTag] = useState<EntityReferenceOption | null>(null);
  const [rememberAlias, setRememberAlias] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => document.getElementById(inputId)?.focus(), [inputId]);
  const link = async () => {
    if (!tag) return;
    setBusy(true);
    setError(null);
    try {
      await onLink(tag, rememberAlias);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Cove couldn’t save the alias. Please try again.");
      setBusy(false);
    }
  };
  return (
    <div
      role="group"
      aria-label={`Link “${scrapedName}”`}
      // The tag search handles Escape itself while its results are open.
      onKeyDown={(event) => {
        if (event.key === "Escape" && !busy) onCancel();
      }}
      className="flex flex-col gap-2 rounded border border-border p-2"
    >
      <label htmlFor={inputId} className="text-xs text-foreground">
        Use a tag you already have for “{scrapedName}”
      </label>
      <EntityReferenceSelector
        entityType="tag"
        value={tag?.id}
        onChange={(_, option) => setTag(option ?? null)}
        inputId={inputId}
        placeholder="Search tags..."
        allowCreate={false}
        creatable={false}
        disabled={disabled || busy}
      />
      <label className="flex items-center gap-2 text-xs text-foreground">
        <input
          type="checkbox"
          checked={rememberAlias}
          onChange={(event) => setRememberAlias(event.target.checked)}
          disabled={disabled || busy}
          aria-describedby={rememberAlias ? `${inputId}-note` : undefined}
          className="rounded border-border"
        />
        Remember “{scrapedName}” as an alias{tag ? ` of ${tag.label}` : ""}
      </label>
      {rememberAlias ? (
        <p id={`${inputId}-note`} className="text-[11px] text-muted">
          The alias is saved to the tag right away, even if you don’t apply.
        </p>
      ) : null}
      {error ? (
        <p role="alert" className="text-[11px] text-red-400">
          {error}
        </p>
      ) : null}
      <div className="flex gap-2">
        <button
          type="button"
          onClick={link}
          disabled={!tag || disabled || busy}
          className="rounded bg-accent px-2 py-1 text-xs text-white disabled:opacity-50"
        >
          {busy ? "Linking…" : "Link"}
        </button>
        <button
          type="button"
          onClick={onCancel}
          disabled={busy}
          className="rounded border border-border px-2 py-1 text-xs text-foreground"
        >
          Cancel
        </button>
      </div>
    </div>
  );
}

export function buildTaggerReview(input: TaggerReviewInput) {
  const { video, result } = input;
  const servers = input.metadataServers ?? [];
  const fields: DiffField[] = [];
  const sourceValues: Record<string, unknown> = {};
  const targetValues: Record<string, unknown> = {};
  const selection: DiffSelection = {};
  const provenance = Object.fromEntries(
    (video.fieldProvenance ?? []).map((entry) => [entry.fieldKey, provenanceLabel(entry.sourceKey, servers)]),
  );

  for (const key of SCALAR_KEYS) {
    if (!normalize(result[key])) continue;
    fields.push({ key, label: key === "details" ? "Details" : key[0].toUpperCase() + key.slice(1) });
    sourceValues[key] = result[key];
    targetValues[key] = video[key] ?? null;
    selection[key] = input.fieldStrategies[key] === "overwrite" ? "source" : "target";
  }
  if (result.imageUrl) {
    // Two covers only ever differ by URL here, so the URLs cannot say whether they are the same
    // picture. Where the comparison has answered that they are, and the incoming one is no larger,
    // the cover is not a decision at all and reads as unchanged beside every other untouched field.
    //
    // Not, however, once the person has chosen to take the incoming cover. The comparison answers
    // only after a download, so that choice can be made while the panel still shows both; calling it
    // unchanged afterwards would both misreport a write and leave no control to change it back.
    const sameCover = input.coverComparison?.verdict === "same" && !input.imageReplace;
    fields.push({
      key: "image",
      label: "Cover",
      alwaysVisible: !sameCover,
      equal: sameCover ? () => true : undefined,
      render: (value) => (
        <img src={String(value)} alt="Video cover" className="max-h-40 w-full rounded object-contain" />
      ),
    });
    sourceValues.image = result.imageUrl;
    targetValues.image = video.imagePath || videos.screenshotUrl(video.id, video.updatedAt);
    selection.image = input.imageReplace ? "source" : "target";
  }
  if (result.studioName && input.showStudio) {
    const scrapedStudio = result.studioName;
    const matchName = input.studioMatchName;
    // A new studio that will not be created is not set by the server, so the row cannot say it is.
    const awaitsCreate = Boolean(input.studioIsNew && !input.createStudio);
    fields.push({
      key: "studio",
      label: "Studio",
      waiting: input.relationsWaiting?.studio,
      sourceIsNew: input.studioIsNew,
      onCreateSource: awaitsCreate ? input.onCreateStudio : undefined,
      // The studio the video gets is the library's; the scraped spelling stays on hover.
      render:
        matchName && relationKey(matchName) !== relationKey(scrapedStudio)
          ? (value) =>
              value === matchName ? (
                <span title={`Scraped as “${scrapedStudio}”`}>
                  {matchName}
                  <span className="sr-only"> (scraped as “{scrapedStudio}”)</span>
                </span>
              ) : (
                renderDiffValue(value)
              )
          : undefined,
    });
    sourceValues.studio = matchName ?? scrapedStudio;
    targetValues.studio = video.studioName ?? null;
    selection.studio = input.collectionModes.studio === "replace" && !awaitsCreate ? "source" : "target";
  }
  // With a collection switched off, its chips are not the way back in: the presets are.
  const listField = (key: string, label: string, mode: CollectionMode, modesOnly = false): DiffField => ({
    key,
    label,
    kind: "list",
    itemKey,
    itemLabel,
    renderItem,
    itemIsNew,
    modesOnly: modesOnly || mode === "skip",
    lockKeptItems: true,
    // A preset is a mode, not a way to create every new item at once.
    newItemsOnlyWhenChosen: true,
    ...collectionPresetHooks(key, mode, input.onCollectionModeChange),
  });
  if (result.urls.length > 0) {
    const urls = urlItems(input);
    const mode = input.collectionModes.urls ?? "merge";
    fields.push(listField("urls", "URLs", mode, true));
    sourceValues.urls = urls.incoming;
    targetValues.urls = urls.current;
    selection.urls = idsForMode(
      mode,
      urls.current.map((url) => url.id),
      urls.incoming.map((url) => url.id),
    );
  }
  // Tags and performers are edited the way the video's edit form edits them, so a library item can
  // be added and a current one taken off beside the scrape; the amber chips stay for new items.
  const relationship = (
    key: TaggerRelationshipKey,
    label: string,
    entityType: "tag" | "performer",
    placeholder: string,
    current: ReviewItem[],
    incoming: ReviewItem[],
    included: string[],
    edits: TaggerRelationshipEdits | undefined,
  ) => {
    const mode = input.collectionModes[key] ?? "merge";
    const field = listField(key, label, mode);
    field.lockKeptItems = false;
    field.waiting = input.relationsWaiting?.[key];
    field.unchosenNewItemsOffered = !(key === "tags" ? input.createMissingTags : input.createMissingPerformers);
    field.renderList = (selected, onChange, disabled) => (
      <RelationshipEditor
        entityType={entityType}
        label={label}
        placeholder={placeholder}
        items={[...current, ...incoming.filter((entry) => !current.some((known) => known.id === entry.id))]}
        selected={selected}
        onChange={onChange}
        disabled={disabled}
        incomingHidden={mode === "skip"}
        waiting={Boolean(field.waiting)}
        onLinkTag={entityType === "tag" ? input.onLinkTag : undefined}
      />
    );
    fields.push(field);
    sourceValues[key] = incoming;
    targetValues[key] = current;
    selection[key] = relationshipSelection(mode, current, included, edits);
  };
  // Always present when the collection is on, as in the edit form: there is something to add even
  // when neither side has an item yet.
  if (input.showPerformers) {
    const performers = performerItems(input);
    relationship(
      "performers",
      "Performers",
      "performer",
      "Search performers...",
      performers.current,
      performers.incoming,
      performers.included,
      input.performerEdits,
    );
  }
  if (input.showTags) {
    const tags = tagItems(input);
    relationship("tags", "Tags", "tag", "Search tags...", tags.current, tags.incoming, tags.included, input.tagEdits);
  }

  const source: DiffRecord = {
    label: `From ${input.sourceName}`,
    sentenceLabel: input.sourceName,
    values: sourceValues,
  };
  const target: DiffRecord = { label: "Current", sentenceLabel: "current", values: targetValues, provenance };
  return { fields, source, target, selection };
}

/**
 * Translates a changed selection back into the tagger's callbacks. Scalars flip a field strategy;
 * a list first checks whether the selection is one of the presets (which sets the collection mode)
 * and then toggles, in one call, every incoming-only item whose inclusion changed. Library ids
 * outside both sides and current items taken off are hand edits and go to their own callback.
 */
export function applyTaggerSelectionChange(
  input: TaggerReviewInput,
  previous: DiffSelection,
  next: DiffSelection,
  handlers: TaggerReviewHandlers,
) {
  for (const key of SCALAR_KEYS) {
    if (next[key] !== previous[key] && next[key] != null)
      handlers.onFieldStrategyChange?.(key, next[key] === "source" ? "overwrite" : "ignore");
  }
  if (next.image !== previous.image && next.image != null)
    handlers.onFieldStrategyChange?.("image", next.image === "source" ? "overwrite" : "ignore");
  if (next.studio !== previous.studio && next.studio != null)
    handlers.onCollectionModeChange?.("studio", next.studio === "source" ? "replace" : "skip");

  const listChange = (
    key: "urls" | TaggerRelationshipKey,
    current: ReviewItem[],
    incoming: ReviewItem[],
    onToggle?: (ids: string[]) => void,
    edits?: TaggerRelationshipEdits,
  ) => {
    const rawSelected = next[key];
    if (!Array.isArray(rawSelected) || sameSet(rawSelected, (previous[key] as string[] | undefined) ?? [])) return;
    const currentIds = current.map((entry) => entry.id);
    const incomingIds = incoming.map((entry) => entry.id);
    // The presets offer a new item only once it has been chosen (the list row does the same), so they
    // are recognised by those ids; every incoming id can still be toggled one by one.
    const presetIds = presetIncomingIds(incoming, currentIds, (previous[key] as string[] | undefined) ?? []);
    const sideIds = rawSelected.filter((id) => libraryIdOf(id) == null);
    // A preset names the whole sides; anything else that drops a current item is a hand removal,
    // which does not touch the collection mode.
    // Taking the last current-only item off looks exactly like "Only incoming" and comes out the same,
    // so it reads as that preset.
    const isPreset =
      sameSet(sideIds, currentIds) ||
      sameSet(sideIds, presetIds) ||
      sameSet(sideIds, [...new Set([...currentIds, ...presetIds])]);
    if (key !== "urls") {
      const added = rawSelected.map(libraryIdOf).filter((id): id is number => id != null);
      // "Only <source>" drops the current items it was not given anyway, so taking one of those off
      // there is not a hand removal; one the scrape also returned would stay, so it is.
      const replacing = (input.collectionModes[key] ?? "merge") === "replace";
      const removed = isPreset
        ? []
        : current
            .filter((entry) => entry.localId != null && !sideIds.includes(entry.id))
            .filter((entry) => !replacing || incomingIds.includes(entry.id))
            .map((entry) => entry.localId!);
      const nextEdits = { added: [...new Set(added)], removed: [...new Set(removed)] };
      const same = (left: number[], right: number[]) => sameSet(left.map(String), right.map(String));
      if (!same(nextEdits.added, edits?.added ?? []) || !same(nextEdits.removed, edits?.removed ?? []))
        handlers.onRelationshipEditsChange?.(key, nextEdits);
    }
    const selected = isPreset ? sideIds : [...new Set([...sideIds, ...currentIds])];
    const wasSelected = new Set([
      ...((previous[key] as string[] | undefined) ?? []).filter((id) => libraryIdOf(id) == null),
      ...(isPreset ? [] : currentIds),
    ]);
    if (sameSet(selected, wasSelected)) return;
    const chosen = new Set(selected);
    const previousMode = input.collectionModes[key] ?? "merge";
    const currentSet = new Set(currentIds);
    const changed = incomingIds.filter((id) => !currentSet.has(id) && chosen.has(id) !== wasSelected.has(id));
    const currentChanged = currentIds.some((id) => chosen.has(id) !== wasSelected.has(id));
    // One incoming chip flipped is a per-item choice even when the result happens to look like a
    // preset (dropping the last incoming item leaves exactly the current side).
    const singleChip = Boolean(onToggle) && changed.length === 1 && !currentChanged && previousMode !== "skip";
    const mode = singleChip ? previousMode : modeForSelection(chosen, currentIds, presetIds, previousMode);
    if (mode !== previousMode) handlers.onCollectionModeChange?.(key, mode);
    // Switched off, or just switched back on: the incoming side is taken as a whole and exclusions
    // stay as they were.
    if (mode === "skip" || previousMode === "skip" || !onToggle) return;
    if (changed.length > 0) onToggle(changed);
  };

  const urls = urlItems(input);
  listChange("urls", urls.current, urls.incoming);
  const tags = tagItems(input);
  listChange(
    "tags",
    tags.current,
    tags.incoming,
    (ids) => handlers.onToggleTag?.(ids.flatMap(tags.namesToToggle)),
    input.tagEdits,
  );
  const performers = performerItems(input);
  listChange(
    "performers",
    performers.current,
    performers.incoming,
    (ids) =>
      handlers.onTogglePerformer?.(
        ids.map((id) => performers.choiceKeys.get(id)).filter((key): key is string => Boolean(key)),
      ),
    input.performerEdits,
  );
}
