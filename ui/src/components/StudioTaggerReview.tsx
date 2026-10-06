import { collectionPresetHooks, idsForMode, modeForSelection, sameSet } from "./VideoTaggerReview";
import type { CollectionMode } from "./videoScrapeUtils";
import { type DiffField, type DiffRecord, type DiffSelection } from "./MetadataDiff";

/**
 * Adapts the studio tagger's decision state (per-field strategies, collection modes) to the generic
 * review rows, so a studio match is reviewed the same way a video scrape, a performer scrape and a
 * merge are. The tagger's state model and its import requests are unchanged: this file only
 * translates in both directions. A studio has no relations to create, so unlike the performer
 * adapter its lists are plain strings and every list is preset-only.
 */

export type StudioFieldStrategy = "ignore" | "merge" | "overwrite";

export interface StudioReviewScalar {
  key: string;
  label: string;
  current?: string | number | null;
  scraped: string | number;
}

export interface StudioReviewInput {
  /** The name of the source as it reads in a sentence, e.g. "StashDB". */
  sourceName: string;
  /** Only the fields the source has a value for. */
  scalars: StudioReviewScalar[];
  fieldStrategies: Record<string, StudioFieldStrategy>;
  collectionModes: Record<string, CollectionMode>;
  currentImageUrl?: string | null;
  /** The source's logo, when it has one. */
  incomingImageUrl?: string;
  urls: { current: string[]; incoming: string[] };
  aliases: { current: string[]; incoming: string[] };
  /**
   * Sets a collection's mode directly, for a preset that selects exactly what another one does: the
   * selection cannot say which of them was meant.
   */
  onCollectionModeChange?: (field: string, mode: CollectionMode) => void;
}

export interface StudioReviewHandlers {
  onFieldStrategyChange: (field: string, strategy: StudioFieldStrategy) => void;
  onCollectionModeChange: (field: string, mode: CollectionMode) => void;
}

interface ReviewItem {
  id: string;
  label: string;
}

const itemKey = (value: unknown) => (value as ReviewItem).id;
const itemLabel = (value: unknown) => (value as ReviewItem).label;
const renderItem = (value: unknown) => (value as ReviewItem).label;
const plainItems = (values: string[]): ReviewItem[] =>
  [...new Set(values.map((value) => value.trim()).filter(Boolean))].map((value) => ({ id: value, label: value }));

const listKeys = ["urls", "aliases"] as const;

export function buildStudioReview(input: StudioReviewInput) {
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
      label: "Logo",
      alwaysVisible: true,
      render: (value) => (
        <img src={String(value)} alt="Studio logo" className="max-h-32 w-full rounded object-contain" />
      ),
    });
    sourceValues.image = input.incomingImageUrl;
    targetValues.image = input.currentImageUrl || null;
    selection.image = input.fieldStrategies.image === "overwrite" ? "source" : "target";
  }
  for (const key of listKeys) {
    const side = input[key];
    if (side.incoming.length === 0) continue;
    const mode = input.collectionModes[key] ?? "merge";
    const current = plainItems(side.current);
    const incoming = plainItems(side.incoming);
    fields.push({
      key,
      label: key === "urls" ? "URLs" : "Aliases",
      kind: "list",
      itemKey,
      itemLabel,
      renderItem,
      // A studio list offers the presets only: there is no per-item action behind a chip.
      modesOnly: true,
      lockKeptItems: true,
      ...collectionPresetHooks(key, mode, input.onCollectionModeChange),
    });
    sourceValues[key] = incoming;
    targetValues[key] = current;
    selection[key] = idsForMode(
      mode,
      current.map((entry) => entry.id),
      incoming.map((entry) => entry.id),
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
 * Translates a changed selection back into the tagger's callbacks. Scalars and the logo flip a field
 * strategy; a list maps back to the collection mode its selection matches.
 */
export function applyStudioSelectionChange(
  input: StudioReviewInput,
  previous: DiffSelection,
  next: DiffSelection,
  handlers: StudioReviewHandlers,
) {
  for (const key of [...input.scalars.map((scalar) => scalar.key), "image"]) {
    if (next[key] !== previous[key] && next[key] != null)
      handlers.onFieldStrategyChange(key, next[key] === "source" ? "overwrite" : "ignore");
  }

  for (const key of listKeys) {
    const selected = next[key];
    const before = (previous[key] as string[] | undefined) ?? [];
    if (!Array.isArray(selected) || sameSet(selected, before)) continue;
    const previousMode = input.collectionModes[key] ?? "merge";
    const mode = modeForSelection(
      new Set(selected),
      plainItems(input[key].current).map((entry) => entry.id),
      plainItems(input[key].incoming).map((entry) => entry.id),
      previousMode,
    );
    if (mode !== previousMode) handlers.onCollectionModeChange(key, mode);
  }
}
