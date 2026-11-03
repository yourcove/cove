import { ChevronDown } from "lucide-react";
import { useState } from "react";
import type { GenerateEstimate, GenerateOptions } from "../api/client";
import {
  EACH_SIZE_WORDS,
  formatCount,
  GENERATE_OPTIONS,
  RUN_SIZE_WORDS,
  WORK_SIZES,
  optionWork,
  runSize,
  segmentKindLabel,
  selectedSegmentCoverKinds,
  sizeEach,
  sizeRun,
  type GenerateOptionKey,
  type WorkSize,
} from "./generateOptionCatalog";

export interface GenerateOptionGroup {
  title: string;
  keys: GenerateOptionKey[];
}

const SIZE_TONES: Record<WorkSize, { text: string; fill: string; ring: string }> = {
  XS: { text: "text-emerald-400", fill: "bg-emerald-500/15", ring: "border-emerald-400" },
  S: { text: "text-sky-400", fill: "bg-sky-500/15", ring: "border-sky-400" },
  M: { text: "text-amber-400", fill: "bg-amber-500/15", ring: "border-amber-400" },
  L: { text: "text-orange-400", fill: "bg-orange-500/15", ring: "border-orange-400" },
  XL: { text: "text-rose-400", fill: "bg-rose-500/15", ring: "border-rose-400" },
};

/** A run's size, solid; outlined when the option is ticked so the ticked ones stand out. */
export function RunSizeChip({ size, strong = true }: { size: WorkSize; strong?: boolean }) {
  const tone = SIZE_TONES[size];
  return (
    <span
      className={`inline-block min-w-[2.25rem] rounded-md border px-1.5 py-0.5 text-center text-xs font-bold tracking-wide ${tone.fill} ${tone.text} ${strong ? tone.ring : "border-transparent"}`}
    >
      {size}
    </span>
  );
}

/** The work for one item, dashed so it does not read as the size of the run. */
function EachSizeChip({ size }: { size: WorkSize }) {
  return (
    <span
      className={`inline-block min-w-[2.25rem] rounded-md border border-dashed border-border px-1 py-0.5 text-center text-[11px] font-bold tracking-wide ${SIZE_TONES[size].text}`}
    >
      {size}
    </span>
  );
}

function StatusChip({ children }: { children: string }) {
  return (
    <span className="inline-block min-w-[2.25rem] rounded-md bg-surface px-1.5 py-0.5 text-center text-xs font-medium text-secondary">
      {children}
    </span>
  );
}

const COST_GRID = "grid w-[13rem] shrink-0 grid-cols-[2.25rem_1fr_1rem_2.75rem] items-center gap-1.5";

function RunCost({
  optionKey,
  estimate,
  options,
  checked,
  counting,
}: {
  optionKey: GenerateOptionKey;
  estimate: GenerateEstimate | undefined;
  options: GenerateOptions;
  checked: boolean;
  counting: boolean;
}) {
  if (!estimate) {
    const each = sizeEach(GENERATE_OPTIONS[optionKey].secondsEach);
    return (
      <span className={COST_GRID} role="img" aria-label={`${each} each${counting ? "; counting what is missing" : ""}`}>
        <EachSizeChip size={each} />
        <span className="col-span-3 text-right text-xs text-muted">{counting ? "counting…" : ""}</span>
      </span>
    );
  }

  if (optionKey === "segmentCovers" && options.segmentCoverKinds?.length === 0) {
    return (
      <span className={COST_GRID} role="img" aria-label="No segment kinds picked">
        <span className="col-span-4 text-right text-xs text-muted">Pick a kind</span>
      </span>
    );
  }
  const work = optionWork(optionKey, estimate, options);
  if (!work) return null;
  const unit = GENERATE_OPTIONS[optionKey].unit;
  const each = sizeEach(work.secondsEach);
  const run = sizeRun(work.seconds);
  const countText = formatCount(work.count, work.estimated);
  const label =
    work.count === 0
      ? work.total === 0
        ? "Nothing in scope"
        : work.estimated
          ? "Nearly all made"
          : "Already made"
      : `${each} each, ${work.estimated ? "about " : ""}${countText.replace("~", "")} ${work.count === 1 ? unit[0] : unit[1]} to make: ${run}, ${RUN_SIZE_WORDS[run]}`;
  return (
    <span className={COST_GRID} role="img" aria-label={label}>
      <EachSizeChip size={each} />
      <span className="text-right text-xs tabular-nums text-foreground">× {countText}</span>
      <span className="text-center text-xs text-muted" aria-hidden="true">
        →
      </span>
      {work.count > 0 ? (
        <RunSizeChip size={run} strong={checked} />
      ) : (
        <StatusChip>{work.total === 0 ? "None" : "Made"}</StatusChip>
      )}
    </span>
  );
}

function SegmentKindChips({
  estimate,
  options,
  onChange,
}: {
  estimate: GenerateEstimate;
  options: GenerateOptions;
  onChange: (kinds: string[]) => void;
}) {
  const available = estimate.segmentCovers ?? [];
  if (available.length === 0) return null;
  const availableKinds = available.map((entry) => entry.kind);
  const chosen = selectedSegmentCoverKinds(options, availableKinds);
  const selected = new Set(chosen);
  // Kinds chosen earlier but absent from this scope stay chosen.
  const elsewhere = chosen.filter((kind) => !availableKinds.includes(kind));
  return (
    <div role="group" aria-label="Segment kinds to make covers for" className="ml-7 flex flex-wrap items-center gap-2">
      <span className="text-xs text-secondary" aria-hidden="true">
        For
      </span>
      {available.map((entry) => {
        const pressed = selected.has(entry.kind);
        const count = options.overwrite ? formatCount(entry.total) : formatCount(entry.missing, entry.estimated);
        return (
          <button
            key={entry.kind}
            type="button"
            aria-pressed={pressed}
            onClick={() => {
              const next = new Set(selected);
              if (pressed) next.delete(entry.kind);
              else next.add(entry.kind);
              onChange([...elsewhere, ...availableKinds.filter((kind) => next.has(kind))]);
            }}
            className={`inline-flex min-h-9 items-center gap-1.5 rounded-lg border px-3 text-sm ${
              pressed
                ? "border-accent bg-accent/15 text-foreground"
                : "border-border bg-card text-secondary hover:text-foreground"
            }`}
          >
            {segmentKindLabel(entry.kind)}
            <span className="text-xs text-muted">{count}</span>
          </button>
        );
      })}
    </div>
  );
}

/**
 * Generate's options as rows: what each makes, a Details panel saying where Cove uses it, and its size. With
 * `estimate` (Generate), a row shows the work for one item × how many this run would make → the run's size.
 * Without it (Scan, which only learns what is new while it runs), a row shows the work for one item alone.
 */
export function GenerateOptionList({
  idPrefix,
  groups,
  isChecked,
  onCheckedChange,
  sizing,
  options = {},
  estimate,
  estimatePending = false,
  onSegmentCoverKindsChange,
}: {
  idPrefix: string;
  groups: GenerateOptionGroup[];
  isChecked: (key: GenerateOptionKey) => boolean;
  onCheckedChange: (key: GenerateOptionKey, checked: boolean) => void;
  /** "run" sizes each row from the estimate; "each" shows the work for one item. */
  sizing: "run" | "each";
  options?: GenerateOptions;
  estimate?: GenerateEstimate;
  /** The estimate is still being counted. */
  estimatePending?: boolean;
  onSegmentCoverKindsChange?: (kinds: string[]) => void;
}) {
  const [open, setOpen] = useState<Partial<Record<GenerateOptionKey, boolean>>>({});

  return (
    <div className="space-y-3">
      {groups.map((group) => (
        <div key={group.title} className="space-y-0.5">
          <div className="flex items-baseline gap-3 px-2.5 pb-1">
            <p className="flex-1 text-xs font-medium uppercase tracking-wide text-muted">{group.title}</p>
            {sizing === "run" ? (
              <p className={`${COST_GRID} text-[11px] text-muted`} aria-hidden="true">
                <span className="text-center">Each</span>
                <span className="text-right">to make</span>
                <span />
                <span className="text-center">Run</span>
              </p>
            ) : (
              <p className="text-[11px] text-muted">Each</p>
            )}
          </div>
          {group.keys.map((key) => {
            const info = GENERATE_OPTIONS[key];
            const checked = isChecked(key);
            const expanded = !!open[key];
            const id = `${idPrefix}-${key}`;
            return (
              <div
                key={key}
                className={`space-y-2 rounded-lg px-2.5 py-2 ${checked || expanded ? "bg-surface/70" : ""}`}
              >
                <div className="flex flex-wrap items-start gap-x-3 gap-y-2">
                  <input
                    id={id}
                    type="checkbox"
                    checked={checked}
                    onChange={(event) => onCheckedChange(key, event.target.checked)}
                    className="mt-0.5 h-4 w-4 shrink-0 rounded border-border bg-card text-accent focus:ring-0"
                  />
                  <div className="min-w-0 flex-1 basis-48">
                    <label htmlFor={id} className="text-sm font-medium text-foreground">
                      {info.label}
                    </label>
                    <p className="text-xs leading-relaxed text-secondary">{info.summary}</p>
                  </div>
                  <div className="ml-auto flex items-center gap-2">
                    <button
                      type="button"
                      aria-expanded={expanded}
                      aria-controls={expanded ? `${id}-details` : undefined}
                      aria-label={`Details: ${info.label}`}
                      onClick={() => setOpen((current) => ({ ...current, [key]: !expanded }))}
                      className="inline-flex min-h-8 items-center gap-1 rounded-md px-2 text-xs text-secondary hover:text-foreground"
                    >
                      Details
                      <ChevronDown className={`h-3.5 w-3.5 transition-transform ${expanded ? "rotate-180" : ""}`} />
                    </button>
                    {sizing === "run" ? (
                      <RunCost
                        optionKey={key}
                        estimate={estimate}
                        options={options}
                        checked={checked}
                        counting={estimatePending}
                      />
                    ) : (
                      <span role="img" aria-label={`${sizeEach(info.secondsEach)} for each new file`}>
                        <EachSizeChip size={sizeEach(info.secondsEach)} />
                      </span>
                    )}
                  </div>
                </div>
                {key === "segmentCovers" && checked && estimate && onSegmentCoverKindsChange ? (
                  <SegmentKindChips estimate={estimate} options={options} onChange={onSegmentCoverKindsChange} />
                ) : null}
                {expanded ? (
                  <dl
                    id={`${id}-details`}
                    className="ml-7 grid grid-cols-[6.5rem_minmax(0,1fr)] gap-x-3 gap-y-1.5 rounded-lg bg-card px-3 py-2.5 text-xs leading-relaxed"
                  >
                    <dt className="font-medium text-muted">What it is</dt>
                    <dd className="text-foreground">{info.what}</dd>
                    <dt className="font-medium text-muted">Used for</dt>
                    <dd className="text-foreground">{info.usedFor}</dd>
                    <dt className="font-medium text-muted">Without it</dt>
                    <dd className="text-foreground">{info.without}</dd>
                  </dl>
                ) : null}
              </div>
            );
          })}
        </div>
      ))}
    </div>
  );
}

/** The ticked options' sizes added up, beside what the letters mean. */
export function GenerateRunSummary({
  keys,
  options,
  estimate,
  estimatePending = false,
}: {
  keys: GenerateOptionKey[];
  options: GenerateOptions;
  estimate: GenerateEstimate | undefined;
  estimatePending?: boolean;
}) {
  const size = estimate ? runSize(keys, estimate, options) : null;
  return (
    <div className="space-y-2 px-2.5">
      <p className="flex items-center gap-2 text-sm text-foreground">
        This run
        {size ? (
          <>
            <RunSizeChip size={size} />
            <span className="text-xs text-secondary">{RUN_SIZE_WORDS[size]}</span>
          </>
        ) : (
          <span className="text-xs text-muted">{estimatePending ? "counting what is missing…" : "not counted"}</span>
        )}
      </p>
      <SizeLegend sizing="run" />
    </div>
  );
}

export function SizeLegend({ sizing }: { sizing: "run" | "each" }) {
  const each = (
    <>
      {(["XS", "S", "M", "L"] as WorkSize[]).map((size, index) => (
        <span key={size}>
          {index > 0 ? " · " : ""}
          <b className="text-secondary">{size}</b> {EACH_SIZE_WORDS[size]}
        </span>
      ))}
    </>
  );
  return (
    <dl className="grid grid-cols-[2.5rem_minmax(0,1fr)] gap-x-2 gap-y-1 text-[11px] text-muted">
      <dt className="font-medium text-secondary">Each</dt>
      <dd>work for one item: {each}</dd>
      {sizing === "run" ? (
        <>
          <dt className="font-medium text-secondary">Run</dt>
          <dd>
            each × how many to make:{" "}
            {WORK_SIZES.map((size, index) => (
              <span key={size}>
                {index > 0 ? " · " : ""}
                <b className="text-secondary">{size}</b> {RUN_SIZE_WORDS[size]}
              </span>
            ))}
          </dd>
        </>
      ) : null}
    </dl>
  );
}
