import { useId, useState, type ReactNode } from "react";
import { AlertTriangle, Check, Minus, Plus, Undo2 } from "lucide-react";
import {
  diffListItems,
  renderDiffValue,
  scalarStatus,
  CreateSourceButton,
  sourceAwaitsCreate,
  type DiffField,
  type DiffRecord,
  type DiffSelection,
  type DiffSide,
} from "./MetadataDiff";

/**
 * The compact reading of a `MetadataDiff` selection: one line per field that changes, a check mark
 * when the incoming value simply lands, an inline two-way choice when the kept record already has a
 * different value, and every unchanged field folded into a single line. Classification follows
 * `summarizeDiff` so this view and the footer sentence never disagree. The full side-by-side rows
 * stay available for anything this view does not let the person do (per-item chips, hand edits).
 */
export function MetadataDiffSummary({
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
  const incoming = source.sentenceLabel ?? source.label.toLowerCase();
  const kept = target.sentenceLabel ?? target.label.toLowerCase();
  const rows: ReactNode[] = [];
  const unchanged: string[] = [];

  for (const field of fields) {
    if (field.kind === "list") {
      const items = diffListItems(field, source, target);
      if (items.length === 0) continue;
      const selected = Array.isArray(value[field.key]) ? (value[field.key] as string[]) : [];
      const chosen = new Set(selected);
      const known = new Set(items.map((item) => item.id));
      const added = items.filter((item) => !item.inTarget && chosen.has(item.id));
      const extra = selected.filter((id) => !known.has(id));
      const removed = items.filter((item) => item.inTarget && !chosen.has(item.id));
      const isNew = (item: (typeof items)[number]) => field.itemIsNew?.(item.result) ?? false;
      // An item the library lacks that nobody chose to create was never on its way in, so it is not
      // "left out"; it is listed apart, the way the review row lists it.
      const notAdded = items.filter((item) => !item.inTarget && !chosen.has(item.id));
      const leftOut = notAdded.filter((item) => !isNew(item));
      const notInLibrary = notAdded.filter(isNew);
      const present = items.filter((item) => item.inTarget && chosen.has(item.id)).length;
      const changes = added.length + extra.length + removed.length;
      if (!changes && !notAdded.length) {
        unchanged.push(field.label);
        continue;
      }
      const renderItem = field.renderItem ?? field.render ?? renderDiffValue;
      const created = added.filter(isNew).length;
      const counts = [
        added.length + extra.length
          ? `${added.length + extra.length} added${created ? ` (${created} new)` : ""}`
          : null,
        removed.length ? `${removed.length} removed` : null,
        leftOut.length ? `${leftOut.length} left out` : null,
        notInLibrary.length ? `${notInLibrary.length} not in your library` : null,
      ].filter(Boolean);
      rows.push(
        <SummaryRow key={field.key} tone={changes ? "ok" : "same"} label={field.label} how={counts.join(" · ")}>
          <span className="flex flex-wrap items-center gap-1">
            {added.map((item) => (
              <Chip key={item.id} state={isNew(item) ? "new" : "added"}>
                {renderItem(item.result)}
              </Chip>
            ))}
            {extra.length ? <Chip state="library">{extra.length} from your library</Chip> : null}
            {removed.map((item) => (
              <Chip key={item.id} state="removed">
                {renderItem(item.result)}
              </Chip>
            ))}
            {leftOut.map((item) => (
              <Chip key={item.id} state="left-out">
                {renderItem(item.result)}
              </Chip>
            ))}
            {notInLibrary.map((item) => (
              <Chip key={item.id} state="not-in-library">
                {renderItem(item.result)}
              </Chip>
            ))}
            {present ? <span className="text-[11px] text-muted">and {present} already present</span> : null}
          </span>
        </SummaryRow>,
      );
      continue;
    }

    const status = scalarStatus(field, source, target);
    const chosen: DiffSide = value[field.key] === "source" ? "source" : "target";
    if (
      field.readOnly ||
      status === "identical" ||
      status === "unavailable" ||
      (status === "keptOnly" && chosen === "target")
    ) {
      unchanged.push(field.label);
      continue;
    }
    const render = field.render ?? renderDiffValue;
    const provenance = target.provenance?.[field.key];
    const keepLabel = status === "conflict" || status === "keptOnly" ? `Keep ${kept}` : "Keep empty";
    // An incoming value the library lacks is only offered, the way a new list item is: it takes the
    // explicit create action to land.
    if (sourceAwaitsCreate(field)) {
      rows.push(
        <SummaryRow
          key={field.key}
          tone="warn"
          label={field.label}
          how={[provenance, status === "conflict" ? `not in your library · keeping ${kept}` : "not in your library"]
            .filter(Boolean)
            .join(" · ")}
          action={<CreateSourceButton field={field} value={source.values[field.key]} disabled={disabled} />}
        >
          <span className="text-muted">
            <ScalarValue value={source.values[field.key]} render={render} />
          </span>
        </SummaryRow>,
      );
      continue;
    }
    if (chosen === "source") {
      // The incoming value lands; the way back is one click, so a mis-click never needs the full rows.
      rows.push(
        <SummaryRow
          key={field.key}
          tone="ok"
          label={field.label}
          how={
            (status === "filled" ? "fills empty" : status === "conflict" ? `replaces ${kept}` : "cleared") +
            (field.sourceIsNew ? " · new, will be created" : "")
          }
          action={
            <button
              type="button"
              disabled={disabled}
              onClick={() => onChange({ ...value, [field.key]: "target" })}
              aria-label={`${field.label}: ${keepLabel.toLowerCase()} instead`}
              className="inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px] text-muted hover:bg-card-hover hover:text-foreground disabled:opacity-60"
            >
              <Undo2 className="h-3 w-3" />
              {keepLabel}
            </button>
          }
        >
          <ScalarValue value={source.values[field.key]} render={render} />
        </SummaryRow>,
      );
      continue;
    }
    // The kept side stays although the incoming side has something: the one decision worth a choice.
    const name = `${instanceId}-${field.key}`;
    rows.push(
      <SummaryRow
        key={field.key}
        tone="warn"
        label={field.label}
        how={[provenance, status === "conflict" ? `both have a value · keeping ${kept}` : "keeping empty"]
          .filter(Boolean)
          .join(" · ")}
      >
        <span role="radiogroup" aria-label={`${field.label} choice`} className="flex flex-wrap items-center gap-1.5">
          <ChoiceButton
            name={name}
            checked
            disabled={disabled}
            label={keepLabel}
            onChoose={() => onChange({ ...value, [field.key]: "target" })}
          >
            {status === "conflict" ? render(target.values[field.key]) : null}
          </ChoiceButton>
          <ChoiceButton
            name={name}
            checked={false}
            disabled={disabled}
            label={`Use ${incoming}`}
            onChoose={() => onChange({ ...value, [field.key]: "source" })}
          >
            {render(source.values[field.key])}
          </ChoiceButton>
        </span>
      </SummaryRow>,
    );
  }

  return (
    <div className="flex flex-col divide-y divide-border/60">
      {rows}
      {unchanged.length ? (
        <SummaryRow tone="same" label="Unchanged">
          <span className="text-[11px] text-muted">{unchanged.join(" · ")}</span>
        </SummaryRow>
      ) : null}
    </div>
  );
}

function SummaryRow({
  tone,
  label,
  how,
  action,
  children,
}: {
  tone: "ok" | "warn" | "same";
  label: string;
  how?: string;
  action?: ReactNode;
  children: ReactNode;
}) {
  const icon =
    tone === "ok" ? (
      <Check className="h-3 w-3" />
    ) : tone === "warn" ? (
      <AlertTriangle className="h-3 w-3" />
    ) : (
      <Minus className="h-3 w-3" />
    );
  const iconClass =
    tone === "ok"
      ? "bg-green-400/15 text-green-400"
      : tone === "warn"
        ? "bg-amber-400/15 text-amber-400"
        : "bg-border/60 text-muted";
  return (
    <div
      data-tone={tone}
      className="grid grid-cols-[18px_minmax(0,1fr)] items-center gap-x-2.5 gap-y-1 px-3 py-1.5 text-[13px] md:grid-cols-[18px_88px_minmax(0,1fr)_auto]"
    >
      <span className={`inline-flex h-[18px] w-[18px] items-center justify-center rounded-full ${iconClass}`}>
        {icon}
      </span>
      <span className={`text-xs ${tone === "same" ? "text-muted" : "text-secondary"}`}>{label}</span>
      <span className="col-start-2 min-w-0 md:col-start-3">{children}</span>
      {how || action ? (
        <span className="col-start-2 flex flex-wrap items-center gap-x-2 text-[11px] text-muted md:col-start-4 md:justify-end">
          {how ? <span className="whitespace-nowrap">{how}</span> : null}
          {action}
        </span>
      ) : null}
    </div>
  );
}

type ChipState = "added" | "new" | "library" | "removed" | "left-out" | "not-in-library";

function Chip({ state, children }: { state: ChipState; children: ReactNode }) {
  const className: Record<ChipState, string> = {
    added: "border-green-400/50 text-green-300",
    new: "border-amber-400/50 text-amber-300",
    library: "border-green-400/50 text-green-300",
    removed: "border-dashed border-border text-muted line-through",
    "left-out": "border-dashed border-border text-muted line-through",
    "not-in-library": "border-border text-muted",
  };
  const added = state === "added" || state === "new" || state === "library";
  return (
    <span
      data-state={state}
      title={
        state === "new"
          ? "Not in your library yet; will be created"
          : state === "left-out"
            ? "Left out of this update"
            : state === "not-in-library"
              ? "Not in your library; not added"
              : undefined
      }
      className={`inline-flex max-w-full items-center gap-1 rounded-full border px-2 py-px text-[11px] ${className[state]}`}
    >
      {added ? <Plus className="h-2.5 w-2.5 shrink-0" /> : null}
      <span className="min-w-0 truncate">{children}</span>
    </span>
  );
}

const LONG_TEXT = 220;

function ScalarValue({ value, render }: { value: unknown; render: (value: unknown) => ReactNode }) {
  const [expanded, setExpanded] = useState(false);
  const long = typeof value === "string" && value.length > LONG_TEXT;
  return (
    <span className="flex min-w-0 flex-col items-start gap-0.5">
      <span
        className={`min-w-0 whitespace-pre-wrap break-words [&_img]:max-h-14 [&_img]:w-auto [&_img]:rounded ${long && !expanded ? "line-clamp-2" : ""}`}
      >
        {render(value)}
      </span>
      {long ? (
        <button
          type="button"
          onClick={() => setExpanded((current) => !current)}
          className="text-[11px] text-accent hover:underline"
        >
          {expanded ? "Show less" : "Show all"}
        </button>
      ) : null}
    </span>
  );
}

function ChoiceButton({
  name,
  checked,
  disabled,
  label,
  onChoose,
  children,
}: {
  name: string;
  checked: boolean;
  disabled: boolean;
  label: string;
  onChoose: () => void;
  children: ReactNode;
}) {
  return (
    <label
      className={`inline-flex max-w-full cursor-pointer items-center gap-2 rounded-md border py-1 pl-1.5 pr-2.5 text-xs transition-colors ${
        checked ? "border-accent bg-accent/10" : "border-border bg-surface hover:border-accent/60"
      } ${disabled ? "cursor-default opacity-60" : ""}`}
    >
      <input
        type="radio"
        name={name}
        checked={checked}
        disabled={disabled}
        onChange={onChoose}
        aria-label={label}
        className="shrink-0 accent-accent"
      />
      {children != null ? (
        <span className="line-clamp-1 max-w-[14rem] min-w-0 break-all text-secondary [&_img]:h-7 [&_img]:w-12 [&_img]:rounded-sm [&_img]:object-cover">
          {children}
        </span>
      ) : null}
      <span className="whitespace-nowrap">{label}</span>
    </label>
  );
}
