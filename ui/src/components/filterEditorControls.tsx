import { useEffect, useId, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from "react";
import type { CriterionModifier } from "../api/types";
import { formatHumanDuration } from "../utils/durationFormat";
import { RESOLUTION_FILTER_OPTIONS } from "../utils/resolutionBuckets";

export const MODIFIER_LABELS: Record<CriterionModifier, string> = {
  EQUALS: "=",
  NOT_EQUALS: "≠",
  GREATER_THAN: ">",
  LESS_THAN: "<",
  INCLUDES: "Includes",
  EXCLUDES: "Excludes",
  INCLUDES_ALL: "Includes All",
  EXCLUDES_ALL: "Excludes All",
  IS_NULL: "Is Null",
  NOT_NULL: "Not Null",
  BETWEEN: "Between",
  NOT_BETWEEN: "Not Between",
  MATCHES_REGEX: "Regex",
  NOT_MATCHES_REGEX: "Not Regex",
  UNDER_PATH: "Under",
  NOT_UNDER_PATH: "Not Under",
};

export function LabeledControl({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block space-y-1.5 text-sm font-medium text-secondary">
      <span>{label}</span>
      {children}
    </label>
  );
}

export function ModifierSelector({
  modifiers,
  selected,
  onSelect,
}: {
  modifiers: CriterionModifier[];
  selected: CriterionModifier;
  onSelect: (m: CriterionModifier) => void;
}) {
  const handleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    const currentButton = (event.target as HTMLElement).closest<HTMLButtonElement>("button[data-modifier]");
    if (!currentButton) return;
    const currentModifier = currentButton.dataset.modifier as CriterionModifier;
    const currentIndex = modifiers.indexOf(currentModifier);
    if (currentIndex < 0) return;

    event.preventDefault();
    event.stopPropagation();
    const direction = event.key === "ArrowRight" ? 1 : -1;
    const nextModifier = modifiers[(currentIndex + direction + modifiers.length) % modifiers.length];
    event.currentTarget.querySelector<HTMLButtonElement>(`button[data-modifier="${nextModifier}"]`)?.focus();
    onSelect(nextModifier);
  };

  return (
    <div className="space-y-2" role="group" aria-label="Match">
      <div className="text-sm font-medium text-secondary">Match</div>
      <div className="flex flex-wrap gap-2" onKeyDown={handleKeyDown}>
        {modifiers.map((m) => (
          <button
            type="button"
            key={m}
            data-modifier={m}
            aria-pressed={m === selected}
            aria-keyshortcuts="ArrowLeft ArrowRight"
            tabIndex={m === selected ? 0 : -1}
            onClick={() => onSelect(m)}
            className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${
              m === selected
                ? "bg-accent text-white border-accent"
                : "border-border text-secondary hover:text-foreground hover:border-accent/50"
            }`}
          >
            {MODIFIER_LABELS[m]}
          </button>
        ))}
      </div>
    </div>
  );
}

function formatDurationInputValue(value?: number) {
  if (value == null) {
    return "";
  }

  const h = Math.floor((value ?? 0) / 3600);
  const m = Math.floor(((value ?? 0) % 3600) / 60);
  const s = (value ?? 0) % 60;
  return h > 0
    ? `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`
    : `${m}:${String(s).padStart(2, "0")}`;
}

function parseDurationInputValue(value: string) {
  const trimmed = value.trim();
  if (trimmed === "") return undefined;
  const parts = trimmed.split(":").map(Number);
  if (parts.some((part) => !Number.isFinite(part))) return undefined;
  const seconds =
    parts.length === 3
      ? parts[0] * 3600 + parts[1] * 60 + parts[2]
      : parts.length === 2
        ? parts[0] * 60 + parts[1]
        : parts[0];
  return seconds >= 0 ? seconds : undefined;
}

export function DurationInput({
  value,
  onChange,
  ariaLabel,
}: {
  value?: number;
  onChange: (v: number | undefined) => void;
  ariaLabel?: string;
}) {
  const [inputText, setInputText] = useState(() => formatDurationInputValue(value));
  const descriptionId = useId();

  useEffect(() => {
    setInputText(formatDurationInputValue(value));
  }, [value]);

  const commit = (rawValue: string) => {
    const parsed = parseDurationInputValue(rawValue);
    setInputText(formatDurationInputValue(parsed));
    onChange(parsed);
  };

  const humanValue = formatHumanDuration(parseDurationInputValue(inputText));

  return (
    <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
      <input
        type="text"
        value={inputText}
        onChange={(event) => setInputText(event.target.value)}
        onBlur={(event) => commit(event.target.value)}
        placeholder="H:MM:SS"
        aria-label={ariaLabel}
        aria-describedby={humanValue ? descriptionId : undefined}
        className="min-h-11 w-28 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
      />
      {humanValue ? (
        <span id={descriptionId} aria-live="polite" className="text-xs font-normal text-muted">
          {humanValue}
        </span>
      ) : null}
    </span>
  );
}

export function PercentInput({
  value,
  onChange,
  ariaLabel,
}: {
  value?: number;
  onChange: (v: number | undefined) => void;
  ariaLabel?: string;
}) {
  return (
    <label className="relative inline-flex w-24 items-center">
      <input
        type="number"
        min={0}
        max={100}
        step={0.1}
        value={value ?? ""}
        onChange={(event) => onChange(event.target.value === "" ? undefined : Number(event.target.value))}
        aria-label={ariaLabel}
        className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 pr-8 text-base text-foreground outline-none focus:border-accent md:text-sm"
      />
      <span className="pointer-events-none absolute right-2 text-xs text-muted">%</span>
    </label>
  );
}

// CareerLengthInput stores its value as integer years (the backend's unit). The
// user can optionally enter a value in months and it will be converted to years
// (rounded to the nearest year, minimum 1 if any months were entered).
export function CareerLengthInput({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  const [unit, setUnit] = useState<"years" | "months">("years");
  const display = unit === "years" ? value : value * 12;

  const handleAmountChange = (amount: number) => {
    if (unit === "years") {
      onChange(amount);
    } else {
      // Convert months to years: round to nearest, but if any months entered round up to at least 1.
      const years = Math.round(amount / 12);
      onChange(amount > 0 && years === 0 ? 1 : years);
    }
  };

  return (
    <div className="flex items-center gap-1">
      <input
        type="number"
        min={0}
        value={display === 0 ? "" : display}
        onChange={(e) => handleAmountChange(e.target.value === "" ? 0 : Number(e.target.value))}
        className="min-h-11 w-24 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
      />
      <select
        value={unit}
        onChange={(e) => setUnit(e.target.value as "years" | "months")}
        aria-label="Career length unit"
        className="min-h-11 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
      >
        <option value="years">Years</option>
        <option value="months">Months</option>
      </select>
    </div>
  );
}

export function ResolutionSelect({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(Number(e.target.value))}
      className="min-h-11 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
    >
      {RESOLUTION_FILTER_OPTIONS.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}
