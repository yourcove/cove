import { useId, useMemo, useState, type ReactNode } from "react";
import { Check, ChevronDown, ChevronUp, Loader2, Plus, X } from "lucide-react";

/**
 * Generic two-sided review: the user compares a kept record against an incoming one and decides,
 * field by field, what the kept record ends up with. Used for entity merges (kept = target,
 * incoming = the entity being merged in) and designed for scrape / metadata-server updates
 * (kept = current entity, incoming = remote data). Pure controls: adapters own data loading, domain
 * rules, and persistence.
 *
 * `source` is the incoming side, `target` is the kept side. Selection values keep those names so
 * existing payload builders are unchanged.
 */
export type DiffSide = "source" | "target";
export type DiffSelection = Record<string, DiffSide | string[]>;
export interface DiffRecord {
  /** Column heading, e.g. "Kept video" or "From StashDB". */
  label: string;
  /** How the side reads inside a sentence ("StashDB", "the kept video"); defaults to the lowercased label. */
  sentenceLabel?: string;
  /** Missing keys are unavailable; null is an explicitly empty value. */
  values: Record<string, unknown>;
  /** Optional provenance label per field key, shown next to the value ("StashDB", "Edited by you"). */
  provenance?: Record<string, string | undefined>;
}

const sentenceLabel = (record: DiffRecord) => record.sentenceLabel ?? record.label.toLowerCase();
export interface DiffField {
  key: string;
  label: string;
  kind?: "scalar" | "list";
  readOnly?: boolean;
  alwaysVisible?: boolean;
  unavailableLabel?: string;
  render?: (value: unknown) => ReactNode;
  equal?: (left: unknown, right: unknown) => boolean;
  /** Treat a value as empty for the fill-empty default and the outcome label. */
  isEmpty?: (value: unknown) => boolean;
  itemKey?: (value: unknown) => string;
  itemLabel?: (value: unknown) => string;
  /** Chip content for one list item. Defaults to `render` / plain text. */
  renderItem?: (value: unknown) => ReactNode;
  /** An incoming item that does not exist in the library yet and will be created when included (amber chip). */
  itemIsNew?: (value: unknown) => boolean;
  additionalItems?: unknown[];
  renderListEditor?: (selected: string[], onChange: (selected: string[]) => void, disabled: boolean) => ReactNode;
  /**
   * Replaces the chip list entirely, for a relationship edited the way the entity's own edit form does
   * it: the app's selector with its chips, x buttons and search-to-add. Ids outside the compared sides
   * are new additions and count as added.
   */
  renderList?: (selected: string[], onChange: (selected: string[]) => void, disabled: boolean) => ReactNode;
  /** Only the Combine / Only kept / Only incoming presets, no per-item toggles (the writer takes a mode, not items). */
  modesOnly?: boolean;
  /** Items already on the kept side cannot be dropped one by one, only through the "Only incoming" preset. */
  lockKeptItems?: boolean;
  /**
   * New incoming items (see `itemIsNew`) join the Combine and Only incoming presets only once chosen,
   * so a preset never creates anything the person did not pick.
   */
  newItemsOnlyWhenChosen?: boolean;
  /**
   * New incoming items start out unchosen (the owner does not create missing items by default), so one
   * that is not selected is only offered: shown and counted as "not in your library", not as left out.
   * Without it, a new item that is not selected was taken out on purpose and reads as left out.
   */
  unchosenNewItemsOffered?: boolean;
  /**
   * The preset the owner is in, to highlight when several presets select the same items, and when the
   * selection has been changed item by item so that none of them matches it.
   */
  activeMode?: DiffListMode;
  /**
   * Called for every preset click, after its selection change, so the owner's mode is the one clicked
   * even when the selection cannot say so (it is already selected, another preset selects the same
   * items, or it reads as one item flipped). Without it a click on what is already selected does nothing.
   */
  onModeSelect?: (mode: DiffListMode) => void;
  /** A scalar whose incoming value names something the library does not have yet (a studio), created when chosen. */
  sourceIsNew?: boolean;
  /**
   * With `sourceIsNew`, the owner creates the value only when asked: the incoming side cannot be chosen
   * as it is, and this asks, as the "+" on a new list item does.
   */
  onCreateSource?: () => void;
  /**
   * The owner does not know this field's outcome yet (it is still checking something the field depends
   * on), so the field cannot be changed, is left out of the summary, and shows this note as its outcome.
   */
  waiting?: string;
}

/** The incoming side of a scalar can only be reached through its create action. */
export const sourceAwaitsCreate = (field: DiffField) => Boolean(field.sourceIsNew && field.onCreateSource);

/** An incoming item that is only offered while unchosen (see `unchosenNewItemsOffered`). */
export const isOfferedItem = (field: DiffField, value: unknown) =>
  Boolean(field.unchosenNewItemsOffered && field.itemIsNew?.(value));

export type DiffListMode = "combined" | "target" | "source";

export type ScalarStatus = "identical" | "filled" | "conflict" | "keptOnly" | "unavailable";
export type ListItemState = "both" | "kept" | "added" | "new" | "excluded" | "available";

export const isEmptyValue = (value: unknown) =>
  value == null || value === "" || (Array.isArray(value) && value.length === 0);

export const renderDiffValue = (value: unknown): ReactNode => {
  if (value == null || value === "") return <span className="text-muted italic">Empty</span>;
  if (typeof value === "boolean") return value ? "Yes" : "No";
  if (Array.isArray(value))
    return value.map((item) => (typeof item === "object" ? JSON.stringify(item) : String(item))).join(", ");
  return typeof value === "object" ? JSON.stringify(value, null, 2) : String(value);
};

export function diffListItems(field: DiffField, source: DiffRecord, target: DiffRecord) {
  const key = field.itemKey ?? ((value: unknown) => JSON.stringify(value));
  const sourceItems = new Map(((source.values[field.key] as unknown[]) ?? []).map((value) => [key(value), value]));
  const targetItems = new Map(((target.values[field.key] as unknown[]) ?? []).map((value) => [key(value), value]));
  const addedItems = new Map((field.additionalItems ?? []).map((value) => [key(value), value]));
  return [...new Set([...targetItems.keys(), ...sourceItems.keys(), ...addedItems.keys()])].map((id) => ({
    id,
    source: sourceItems.get(id),
    target: targetItems.get(id),
    result: targetItems.get(id) ?? sourceItems.get(id) ?? addedItems.get(id),
    inSource: sourceItems.has(id),
    inTarget: targetItems.has(id),
  }));
}

function scalarAvailability(field: DiffField, source: DiffRecord, target: DiffRecord) {
  const empty = field.isEmpty ?? isEmptyValue;
  const availableSource = Object.hasOwn(source.values, field.key);
  const availableTarget = Object.hasOwn(target.values, field.key);
  const sourceEmpty = !availableSource || empty(source.values[field.key]);
  const targetEmpty = !availableTarget || empty(target.values[field.key]);
  const equal =
    availableSource === availableTarget &&
    (field.equal ?? ((a, b) => JSON.stringify(a) === JSON.stringify(b)))(
      source.values[field.key],
      target.values[field.key],
    );
  return { availableSource, availableTarget, sourceEmpty, targetEmpty, equal };
}

export function scalarStatus(field: DiffField, source: DiffRecord, target: DiffRecord): ScalarStatus {
  const a = scalarAvailability(field, source, target);
  if (!a.availableSource && !a.availableTarget) return "unavailable";
  if (a.equal) return "identical";
  if (a.targetEmpty && !a.sourceEmpty) return "filled";
  if (a.sourceEmpty && !a.targetEmpty) return "keptOnly";
  return "conflict";
}

/**
 * Default choices: lists combine everything; empty kept fields are filled from the incoming side;
 * every other scalar keeps the kept record's value.
 */
export function defaultDiffSelection(fields: DiffField[], source: DiffRecord, target: DiffRecord): DiffSelection {
  return Object.fromEntries(
    fields.map((field) => {
      if (field.kind === "list") return [field.key, diffListItems(field, source, target).map((item) => item.id)];
      if (field.readOnly) return [field.key, Object.hasOwn(target.values, field.key) ? "target" : "source"];
      const status = scalarStatus(field, source, target);
      const a = scalarAvailability(field, source, target);
      if (status === "filled") return [field.key, "source"];
      return [field.key, a.availableTarget ? "target" : "source"];
    }),
  );
}

export interface DiffChange {
  kind: "filled" | "taken" | "conflictKept" | "listAdded" | "listRemoved" | "identical";
  text: string;
}

const plural = (count: number, label: string) => {
  const lower = label.toLowerCase();
  return `${count} ${count === 1 && lower.endsWith("s") && !lower.endsWith("ss") ? lower.slice(0, -1) : lower}`;
};

/** Plain-language summary of what the selection will do, for a result panel and footer. */
export function summarizeDiff(fields: DiffField[], source: DiffRecord, target: DiffRecord, value: DiffSelection) {
  const changes: DiffChange[] = [];
  const identical: string[] = [];
  let changeCount = 0;
  const incoming = sentenceLabel(source);
  const kept = sentenceLabel(target);
  const filled: string[] = [];
  const taken: string[] = [];
  const conflictKept: string[] = [];
  for (const field of fields) {
    // Whatever it would say now is a guess, and the field says so itself.
    if (field.waiting) continue;
    if (field.kind === "list") {
      const items = diffListItems(field, source, target);
      const selected = new Set(Array.isArray(value[field.key]) ? (value[field.key] as string[]) : []);
      const known = new Set(items.map((item) => item.id));
      const added =
        items.filter((item) => !item.inTarget && selected.has(item.id)).length +
        [...selected].filter((id) => !known.has(id)).length;
      const removed = items.filter((item) => item.inTarget && !selected.has(item.id)).length;
      // An item the library lacks that nobody chose to create was never on its way in, so it is not
      // counted as left out.
      const leftOut = items.filter(
        (item) => !item.inTarget && !selected.has(item.id) && !isOfferedItem(field, item.result),
      ).length;
      const created = items.filter(
        (item) => !item.inTarget && selected.has(item.id) && field.itemIsNew?.(item.result),
      ).length;
      if (added) {
        const text = `${plural(added, field.label)} added${created ? ` (${created} new)` : ""}`;
        changes.push({ kind: "listAdded", text });
        changeCount++;
      }
      if (removed) {
        changes.push({ kind: "listRemoved", text: `${plural(removed, field.label)} removed` });
        changeCount++;
      }
      if (leftOut) changes.push({ kind: "listRemoved", text: `${plural(leftOut, field.label)} left out` });
      if (!added && !removed && !leftOut && items.length) identical.push(field.label);
      continue;
    }
    const status = scalarStatus(field, source, target);
    if (status === "identical" || status === "unavailable" || field.readOnly) {
      identical.push(field.label);
      continue;
    }
    const chosen = value[field.key] === "source" ? "source" : "target";
    if (status === "filled" && chosen === "source") filled.push(field.label);
    else if (status === "conflict" && chosen === "source") taken.push(field.label);
    else if (status === "conflict") conflictKept.push(field.label);
    else if (status === "keptOnly" && chosen === "source") taken.push(field.label);
    else if (status === "keptOnly") identical.push(field.label);
  }
  const join = (items: string[]) => items.join(", ");
  if (filled.length) {
    changes.unshift({ kind: "filled", text: `${join(filled)} filled from ${incoming}` });
    changeCount += filled.length;
  }
  if (taken.length) {
    changes.push({ kind: "taken", text: `${join(taken)} replaced from ${incoming}` });
    changeCount += taken.length;
  }
  if (conflictKept.length)
    changes.push({ kind: "conflictKept", text: `${join(conflictKept)} kept from ${kept} (conflict)` });
  if (identical.length) changes.push({ kind: "identical", text: `${identical.length} unchanged` });
  return { changes, changeCount, identical };
}

const optionClass = (chosen: boolean, interactive: boolean) =>
  `flex min-w-0 items-start gap-2.5 rounded-lg border px-3 py-2.5 text-left transition-colors ${
    chosen ? "border-accent bg-accent/10" : "border-border bg-card"
  } ${interactive ? "cursor-pointer hover:border-accent/60" : "cursor-default opacity-60"}`;

function StatusPill({
  status,
  chosen,
  incoming,
  kept,
}: {
  status: ScalarStatus;
  chosen: DiffSide;
  incoming: string;
  kept: string;
}) {
  if (status === "identical" || status === "unavailable")
    return (
      <span className="rounded-full border border-border px-2 py-0.5 text-[11px] font-semibold text-muted">
        Identical
      </span>
    );
  if (
    status === "filled" ||
    (status === "keptOnly" && chosen === "source") ||
    (status === "conflict" && chosen === "source")
  )
    return (
      <span className="inline-flex items-center gap-1 rounded-full border border-green-400/40 bg-green-400/10 px-2 py-0.5 text-[11px] font-semibold text-green-400">
        <Plus className="h-3 w-3" />
        {status === "conflict" ? `Replaced from ${incoming}` : `Filled from ${incoming}`}
      </span>
    );
  if (status === "conflict")
    return (
      <span className="rounded-full border border-amber-400/40 bg-amber-400/10 px-2 py-0.5 text-[11px] font-semibold text-amber-400">
        Conflict · keeping {kept}
      </span>
    );
  return <span className="rounded-full bg-card-hover px-2 py-0.5 text-[11px] font-semibold text-secondary">Kept</span>;
}

export function MetadataDiff({
  fields,
  source,
  target,
  value,
  onChange,
  disabled = false,
}: {
  fields: DiffField[];
  source: DiffRecord;
  target: DiffRecord;
  value: DiffSelection;
  onChange: (value: DiffSelection) => void;
  disabled?: boolean;
}) {
  const instanceId = useId();
  const [showIdentical, setShowIdentical] = useState(false);
  const rows = useMemo(
    () =>
      fields.map((field) => {
        if (field.kind === "list") {
          const items = diffListItems(field, source, target);
          const identical = items.length > 0 && items.every((item) => item.inSource && item.inTarget);
          return { field, kind: "list" as const, items, identical: identical || items.length === 0 };
        }
        const status = scalarStatus(field, source, target);
        return {
          field,
          kind: "scalar" as const,
          status,
          identical: status === "identical" || status === "unavailable",
        };
      }),
    [fields, source, target],
  );
  const identicalRows = rows.filter(
    (row) => row.identical && !row.field.alwaysVisible && !row.field.renderListEditor && !row.field.renderList,
  );
  const visibleRows = rows.filter((row) => !identicalRows.includes(row));
  const differenceCount = visibleRows.filter((row) => row.kind === "scalar").length;
  const listCount = visibleRows.filter((row) => row.kind === "list").length;
  const incoming = sentenceLabel(source);
  const kept = sentenceLabel(target);

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <div className="flex flex-wrap items-center gap-1.5 text-xs">
          <span className="rounded-full border border-border px-2.5 py-1 font-semibold text-secondary">
            Differences <span className="text-amber-400">{differenceCount}</span>
          </span>
          <span className="rounded-full border border-border px-2.5 py-1 font-semibold text-secondary">
            Lists <span className="text-green-400">{listCount}</span>
          </span>
        </div>
        <p className="text-xs text-muted">
          Empty fields are filled from the {incoming} by default. Real conflicts keep the {kept}'s value.
        </p>
      </div>

      <div className="hidden grid-cols-[150px_minmax(0,1fr)_minmax(0,1fr)_190px] gap-3 pb-1 text-[11px] font-bold uppercase tracking-wider text-muted md:grid">
        <span>Field</span>
        <span className="text-secondary">{source.label}</span>
        <span className="text-accent">{target.label}</span>
        <span className="text-right">Outcome</span>
      </div>

      <div className="flex flex-col divide-y divide-border border-t border-border">
        {visibleRows.map((row) =>
          row.kind === "list" ? (
            <ListRow
              key={row.field.key}
              field={row.field}
              items={row.items}
              selected={Array.isArray(value[row.field.key]) ? (value[row.field.key] as string[]) : []}
              sourceLabel={sentenceLabel(source)}
              targetLabel={sentenceLabel(target)}
              disabled={disabled || Boolean(row.field.waiting)}
              onChange={(selected) => onChange({ ...value, [row.field.key]: selected })}
            />
          ) : (
            <ScalarRow
              key={row.field.key}
              field={row.field}
              status={row.status}
              source={source}
              target={target}
              chosen={value[row.field.key] === "source" ? "source" : "target"}
              name={`${instanceId}-${row.field.key}`}
              disabled={disabled || Boolean(row.field.waiting)}
              onChange={(side) => onChange({ ...value, [row.field.key]: side })}
            />
          ),
        )}
        {identicalRows.length > 0 && (
          <div className="flex flex-col">
            <button
              type="button"
              aria-expanded={showIdentical}
              onClick={() => setShowIdentical((current) => !current)}
              className="flex flex-wrap items-center gap-3 py-3 text-left text-sm hover:text-foreground"
            >
              <span className="font-semibold text-secondary md:w-[150px]">{identicalRows.length} identical</span>
              <span className="min-w-0 flex-1 truncate text-xs text-muted">
                {identicalRows.map((row) => row.field.label).join(" · ")}
              </span>
              <span className="inline-flex items-center gap-1 text-xs text-secondary">
                {showIdentical ? "Hide" : "Show"}
                {showIdentical ? <ChevronUp className="h-3.5 w-3.5" /> : <ChevronDown className="h-3.5 w-3.5" />}
              </span>
            </button>
            {showIdentical && (
              <div className="flex flex-col divide-y divide-border border-t border-border">
                {identicalRows.map((row) =>
                  row.kind === "list" ? (
                    <ListRow
                      key={row.field.key}
                      field={row.field}
                      items={row.items}
                      selected={Array.isArray(value[row.field.key]) ? (value[row.field.key] as string[]) : []}
                      sourceLabel={sentenceLabel(source)}
                      targetLabel={sentenceLabel(target)}
                      disabled={disabled || Boolean(row.field.waiting)}
                      onChange={(selected) => onChange({ ...value, [row.field.key]: selected })}
                    />
                  ) : (
                    <ScalarRow
                      key={row.field.key}
                      field={row.field}
                      status={row.status}
                      source={source}
                      target={target}
                      chosen={value[row.field.key] === "source" ? "source" : "target"}
                      name={`${instanceId}-${row.field.key}`}
                      disabled={disabled || Boolean(row.field.waiting)}
                      onChange={(side) => onChange({ ...value, [row.field.key]: side })}
                    />
                  ),
                )}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function ScalarRow({
  field,
  status,
  source,
  target,
  chosen,
  name,
  disabled,
  onChange,
}: {
  field: DiffField;
  status: ScalarStatus;
  source: DiffRecord;
  target: DiffRecord;
  chosen: DiffSide;
  name: string;
  disabled: boolean;
  onChange: (side: DiffSide) => void;
}) {
  const render = field.render ?? renderDiffValue;
  const awaitsCreate = sourceAwaitsCreate(field);
  const option = (side: DiffSide, record: DiffRecord) => {
    const available = Object.hasOwn(record.values, field.key);
    const interactive = available && !disabled && !field.readOnly && !(side === "source" && awaitsCreate);
    const provenance = record.provenance?.[field.key];
    return (
      <label className={optionClass(chosen === side, interactive)}>
        <input
          type="radio"
          name={name}
          aria-label={`${field.label} from ${side}`}
          checked={chosen === side}
          disabled={!interactive}
          onChange={() => onChange(side)}
          className="mt-0.5 shrink-0 accent-accent"
        />
        <span className="flex min-w-0 flex-1 flex-col gap-0.5">
          <span className="flex items-center gap-1.5 text-[11px] text-secondary">
            {record.label}
            {provenance ? (
              <span className="rounded-full border border-border px-1.5 text-[10px] font-semibold text-muted">
                {provenance}
              </span>
            ) : null}
          </span>
          {available ? (
            <ClampedValue value={record.values[field.key]} render={render} />
          ) : (
            <span className="text-sm text-muted">{field.unavailableLabel ?? "Unavailable"}</span>
          )}
        </span>
      </label>
    );
  };
  // Nothing lands while the incoming value waits to be created, whatever the plain comparison says.
  const pill = field.waiting ? (
    <WaitingNote text={field.waiting} />
  ) : awaitsCreate ? (
    <span className="rounded-full border border-border px-2 py-0.5 text-[11px] font-semibold text-muted">
      Not in your library
    </span>
  ) : (
    <StatusPill status={status} chosen={chosen} incoming={sentenceLabel(source)} kept={sentenceLabel(target)} />
  );
  return (
    <fieldset
      disabled={disabled}
      aria-busy={field.waiting ? true : undefined}
      className="grid grid-cols-1 gap-2 py-3 md:grid-cols-[150px_minmax(0,1fr)_minmax(0,1fr)_190px] md:gap-3"
    >
      <legend className="sr-only">{field.label}</legend>
      <div className="flex items-center justify-between gap-2 md:block md:pt-2.5">
        <span className="text-sm font-semibold">{field.label}</span>
        <span className="md:hidden">{pill}</span>
      </div>
      {field.sourceIsNew ? (
        <div className="flex min-w-0 flex-col gap-1.5">
          {option("source", source)}
          {awaitsCreate ? (
            <span className="flex flex-wrap items-center gap-2 text-[11px] text-muted">
              Not in your library
              <CreateSourceButton field={field} value={source.values[field.key]} disabled={disabled} />
            </span>
          ) : chosen === "source" ? (
            <span className="text-[11px] text-amber-300">Not in your library yet; will be created</span>
          ) : (
            <span className="text-[11px] text-muted">Not in your library</span>
          )}
        </div>
      ) : (
        option("source", source)
      )}
      {option("target", target)}
      <div className="hidden items-start justify-end md:flex md:pt-2.5">{pill}</div>
    </fieldset>
  );
}

/** The explicit way to an incoming value the library does not have yet (see `onCreateSource`). */
export function CreateSourceButton({
  field,
  value,
  disabled,
}: {
  field: DiffField;
  value: unknown;
  disabled: boolean;
}) {
  const name = String(value ?? "");
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={field.onCreateSource}
      aria-label={`${field.label}: create “${name}” and use it`}
      title={`Create “${name}” and use it`}
      className="inline-flex items-center gap-1 rounded border border-border px-1.5 py-0.5 text-[11px] text-foreground hover:border-accent/60 disabled:opacity-60"
    >
      <Plus className="h-3 w-3" />
      Create
    </button>
  );
}

const LONG_TEXT = 220;

function ClampedValue({ value, render }: { value: unknown; render: (value: unknown) => ReactNode }) {
  const [expanded, setExpanded] = useState(false);
  const long = typeof value === "string" && value.length > LONG_TEXT;
  return (
    <span className="flex min-w-0 flex-col items-start gap-1">
      <span className={`min-w-0 whitespace-pre-wrap break-words text-sm ${long && !expanded ? "line-clamp-4" : ""}`}>
        {render(value)}
      </span>
      {long ? (
        <button
          type="button"
          onClick={(event) => {
            event.preventDefault();
            setExpanded((current) => !current);
          }}
          className="text-xs text-accent hover:underline"
        >
          {expanded ? "Show less" : "Show all"}
        </button>
      ) : null}
    </span>
  );
}

function ListRow({
  field,
  items,
  selected,
  onChange,
  sourceLabel,
  targetLabel,
  disabled,
}: {
  field: DiffField;
  items: ReturnType<typeof diffListItems>;
  selected: string[];
  onChange: (selected: string[]) => void;
  sourceLabel: string;
  targetLabel: string;
  disabled: boolean;
}) {
  const renderItem = field.renderItem ?? field.render ?? renderDiffValue;
  const label = (item: (typeof items)[number]) => field.itemLabel?.(item.result) ?? item.id;
  // Ids chosen through the row editor that neither side had (a tag or URL added in the review) are
  // shown as added chips too.
  const shown = [
    ...items,
    ...selected
      .filter((id) => !items.some((item) => item.id === id))
      .map((id) => ({
        id,
        source: undefined,
        target: undefined,
        result: id as unknown,
        inSource: false,
        inTarget: false,
      })),
  ];
  const isNewIncoming = (item: (typeof shown)[number]) => !item.inTarget && Boolean(field.itemIsNew?.(item.result));
  const isOffered = (item: (typeof shown)[number]) => !item.inTarget && isOfferedItem(field, item.result);
  const inPresets = (item: (typeof items)[number]) =>
    !field.newItemsOnlyWhenChosen || !isNewIncoming(item) || selected.includes(item.id);
  const modes: { key: DiffListMode; label: string; ids: string[] }[] = [
    {
      key: "combined",
      label: "Combine",
      ids: items.filter((item) => item.inTarget || (item.inSource && inPresets(item))).map((item) => item.id),
    },
    {
      key: "target",
      label: `Only ${targetLabel}`,
      ids: items.filter((item) => item.inTarget).map((item) => item.id),
    },
    {
      key: "source",
      label: `Only ${sourceLabel}`,
      ids: items.filter((item) => item.inSource && inPresets(item)).map((item) => item.id),
    },
  ];
  const matchesSelection = (ids: string[]) =>
    selected.length === ids.length && ids.every((id) => selected.includes(id));
  // An owner that states its mode has that preset pressed, whatever the selection looks like: two presets
  // can select the same items (nothing incoming to add, say), and items changed one by one can match no
  // preset or happen to match another one (every incoming item taken off leaves exactly the kept side).
  // When the selection differs from the mode's own, the mode is "customised". Without a stated mode, the
  // matching preset is pressed.
  const matching = modes.filter((mode) => matchesSelection(mode.ids));
  const ownerMode = modes.find((mode) => mode.key === field.activeMode);
  const customised = ownerMode ? !matchesSelection(ownerMode.ids) : matching.length === 0;
  const activeMode = ownerMode ? ownerMode.key : matching[0]?.key;
  // The selection a preset makes cannot always say which preset was meant: another one can select the
  // same items, or the change can read as a single chip flipped. An owner that tracks its mode hears the
  // click itself, after the selection so that its mode is the one that stands.
  const selectPreset = (mode: (typeof modes)[number]) => {
    if (!(field.onModeSelect && matchesSelection(mode.ids))) onChange(mode.ids);
    field.onModeSelect?.(mode.key);
  };
  const kept = shown.filter((item) => item.inTarget && selected.includes(item.id)).length;
  const added = shown.filter((item) => !item.inTarget && selected.includes(item.id)).length;
  const notInLibrary = shown.filter((item) => !selected.includes(item.id) && isOffered(item)).length;
  const leftOut = shown.filter((item) => !selected.includes(item.id)).length - notInLibrary;
  const addedNew = shown.filter((item) => selected.includes(item.id) && isNewIncoming(item)).length;
  const counts = [
    kept ? `${kept} kept` : null,
    added ? (
      <span key="added" className="text-green-400">
        {added} added{addedNew ? ` (${addedNew} new)` : ""}
      </span>
    ) : null,
    leftOut ? (
      <span key="out" className="text-muted">
        {leftOut} left out
      </span>
    ) : null,
    notInLibrary ? (
      <span key="unknown" className="text-muted">
        {notInLibrary} not in your library
      </span>
    ) : null,
  ].filter(Boolean);
  const stateOf = (item: (typeof items)[number]): ListItemState =>
    !selected.includes(item.id)
      ? isOffered(item)
        ? "available"
        : "excluded"
      : item.inTarget && item.inSource
        ? "both"
        : item.inTarget
          ? "kept"
          : field.itemIsNew?.(item.result)
            ? "new"
            : "added";
  // The same chip as the entity edit forms use, with the review state on its border.
  const chipClass: Record<ListItemState, string> = {
    both: "border-border bg-card text-foreground",
    kept: "border-border bg-card text-foreground",
    added: "border-green-400/50 bg-card text-green-300",
    new: "border-amber-400/50 bg-card text-amber-300",
    excluded: "border-dashed border-border bg-transparent text-muted line-through",
    // Not in the library and not chosen: never on its way in, so not struck through as left out.
    available: "border-border bg-transparent text-muted",
  };
  return (
    <fieldset
      disabled={disabled}
      aria-busy={field.waiting ? true : undefined}
      className="grid grid-cols-1 gap-2 py-3 md:grid-cols-[150px_minmax(0,1fr)_190px] md:gap-3"
    >
      <legend className="sr-only">{field.label}</legend>
      <div className="flex flex-col gap-0.5">
        <span className="text-sm font-semibold">{field.label}</span>
        <span className="text-[11px] text-secondary">
          {field.waiting ? (
            <WaitingNote text={field.waiting} />
          ) : counts.length ? (
            counts.map((count, index) => (
              <span key={index}>
                {index > 0 ? " · " : ""}
                {count}
              </span>
            ))
          ) : (
            "No values"
          )}
        </span>
      </div>
      <div className="flex min-w-0 flex-col gap-2">
        {field.renderList ? (
          field.renderList(selected, onChange, disabled)
        ) : (
          <div className="flex flex-wrap gap-1.5">
            {shown.map((item) => {
              const state = stateOf(item);
              const included = state !== "excluded" && state !== "available";
              const toggle = () => {
                if (!disabled) onChange(included ? selected.filter((id) => id !== item.id) : [...selected, item.id]);
              };
              const togglable = !field.modesOnly && !(field.lockKeptItems && item.inTarget);
              return (
                <span
                  key={item.id}
                  data-state={state}
                  title={state === "new" ? "Not in your library yet; will be created" : undefined}
                  className={`inline-flex max-w-full items-center gap-1.5 rounded border py-0.5 ${togglable ? "pl-2 pr-1" : "px-2"} text-xs ${chipClass[state]}`}
                >
                  {state === "added" || state === "new" ? <Plus className="h-3 w-3 shrink-0" /> : null}
                  <span className="min-w-0 truncate">{renderItem(item.result)}</span>
                  {togglable ? (
                    <button
                      type="button"
                      disabled={disabled}
                      onClick={toggle}
                      aria-label={`${included ? "Remove" : "Add"} ${field.label}: ${label(item)}`}
                      className="inline-flex h-4.5 w-4.5 shrink-0 items-center justify-center rounded-full bg-white/10 hover:bg-white/20"
                    >
                      {included ? <X className="h-2.5 w-2.5" /> : <Plus className="h-2.5 w-2.5" />}
                    </button>
                  ) : null}
                </span>
              );
            })}
          </div>
        )}
        {field.renderListEditor?.(selected, onChange, disabled)}
      </div>
      <div className="flex items-start md:justify-end">
        <div
          role="group"
          aria-label={`${field.label} mode`}
          className="inline-flex overflow-hidden rounded-lg border border-border"
        >
          {modes.map((mode) => {
            const active = mode.key === activeMode;
            return (
              <button
                key={mode.key}
                type="button"
                aria-label={`Use ${mode.key} ${field.label}`}
                aria-pressed={active}
                title={active && customised ? `${mode.label}, with your changes. Click to undo them.` : undefined}
                onClick={() => selectPreset(mode)}
                className={`px-2.5 py-1 text-[11px] font-semibold ${active ? "bg-accent/15 text-accent" : "text-secondary hover:text-foreground"}`}
              >
                {active && <Check className="mr-1 inline h-3 w-3" />}
                {mode.label}
              </button>
            );
          })}
        </div>
      </div>
    </fieldset>
  );
}

/** A field's outcome while its owner is still finding it out. */
export function WaitingNote({ text }: { text: string }) {
  return (
    <span className="inline-flex items-center gap-1 text-[11px] text-muted">
      <Loader2 className="h-3 w-3 shrink-0 animate-spin" aria-hidden="true" />
      {text}
    </span>
  );
}
