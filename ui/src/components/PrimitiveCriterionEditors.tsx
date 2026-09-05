import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Star } from "lucide-react";
import { metadata } from "../api/client";
import type {
  BoolCriterion,
  CriterionModifier,
  DateCriterion,
  FingerprintCriterion,
  IntCriterion,
  MetadataServer,
  StringCriterion,
  TimestampCriterion,
} from "../api/types";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { IsoDateInput } from "./IsoDateInput";
import { CountrySelect } from "./Country";
import { LibraryFolderTree } from "./LibraryFolderTree";
import {
  convertFromRatingFormat,
  convertToRatingFormat,
  getRatingMax,
  getRatingPrecision,
  getRatingStep,
  useRatingOptions,
} from "./Rating";
import { NULL_VALUE_MODIFIERS } from "./filterCriterionState";
import type { CriterionType } from "./filterCriteriaTypes";
import {
  CareerLengthInput,
  DurationInput,
  LabeledControl,
  ModifierSelector,
  ResolutionSelect,
} from "./filterEditorControls";

export function BoolEditor({ value, onChange }: { value?: BoolCriterion; onChange: (v: unknown) => void }) {
  return (
    <div className="space-y-2" role="group" aria-label="Value">
      <div className="text-sm font-medium text-secondary">Value</div>
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          aria-pressed={value?.value === true}
          onClick={() => onChange({ value: true })}
          className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${value?.value === true ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
        >
          True
        </button>
        <button
          type="button"
          aria-pressed={value?.value === false}
          onClick={() => onChange({ value: false })}
          className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${value?.value === false ? "bg-accent text-white border-accent" : "border-border text-secondary hover:text-foreground"}`}
        >
          False
        </button>
      </div>
    </div>
  );
}

// ===== Number Editor =====

export function NumberEditor({
  value,
  onChange,
  type,
  modifiers,
  defaultModifier,
  min,
  max,
  step,
  hint,
  auxiliaryToggleLabel,
  auxiliaryToggleChecked,
  onAuxiliaryToggleChange,
}: {
  value?: IntCriterion;
  onChange: (v: unknown) => void;
  type: CriterionType;
  modifiers: CriterionModifier[];
  defaultModifier?: CriterionModifier;
  min?: number;
  max?: number;
  step?: number;
  hint?: string;
  auxiliaryToggleLabel?: string;
  auxiliaryToggleChecked?: boolean;
  onAuxiliaryToggleChange?: (checked: boolean) => void;
}) {
  // A criterion that narrows `modifiers` must be able to start on one it actually offers — otherwise the Match
  // control shows nothing selected and the saved criterion carries a modifier that isn't in the list.
  const modifier = value?.modifier ?? defaultModifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";
  // Both bounds known ⇒ the value lives on a range, so offer a slider alongside the box.
  const bounded = min != null && max != null && max > min;
  const sliderStep = step ?? (bounded ? Math.max((max! - min!) / 100, 0.001) : undefined);
  const fallback = bounded ? (min! + max!) / 2 : 0;

  const update = (patch: Partial<IntCriterion>) => {
    onChange({ modifier, ...(bounded ? { value: value?.value ?? fallback } : {}), ...value, ...patch });
  };

  const numberInput = (current: number | undefined, onPick: (v: number | undefined) => void, label: string) => (
    <div className={bounded ? "flex items-center gap-3" : undefined}>
      {bounded && (
        <input
          aria-label={`${label} slider`}
          type="range"
          min={min}
          max={max}
          step={sliderStep}
          value={current ?? fallback}
          onChange={(e) => onPick(Number(e.target.value))}
          className="h-2 flex-1 accent-accent"
        />
      )}
      <input
        aria-label={label}
        type="number"
        min={min}
        max={max}
        step={sliderStep}
        value={current ?? ""}
        onChange={(e) => onPick(e.target.value === "" ? undefined : Number(e.target.value))}
        className={`min-h-11 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm ${bounded ? "w-24 tabular-nums" : "w-full"}`}
      />
    </div>
  );

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      {!isNull && (
        <div className="grid gap-3 sm:grid-cols-2">
          {type === "duration" ? (
            <LabeledControl label={isBetween ? "Minimum" : "Value"}>
              <DurationInput
                value={value?.value}
                onChange={(v) => update({ value: v })}
                ariaLabel={isBetween ? "Minimum" : "Value"}
              />
            </LabeledControl>
          ) : type === "resolution" ? (
            <LabeledControl label="Value">
              <ResolutionSelect value={value?.value ?? 0} onChange={(v) => update({ value: v })} />
            </LabeledControl>
          ) : type === "careerLength" ? (
            <LabeledControl label={isBetween ? "Minimum" : "Value"}>
              <CareerLengthInput value={value?.value ?? 0} onChange={(v) => update({ value: v })} />
            </LabeledControl>
          ) : (
            <LabeledControl label={isBetween ? "Minimum" : "Value"}>
              {numberInput(value?.value, (v) => update({ value: v }), isBetween ? "Minimum" : "Value")}
            </LabeledControl>
          )}
          {isBetween && (
            <div>
              {type === "duration" ? (
                <LabeledControl label="Maximum">
                  <DurationInput value={value?.value2} onChange={(v) => update({ value2: v })} ariaLabel="Maximum" />
                </LabeledControl>
              ) : type === "careerLength" ? (
                <LabeledControl label="Maximum">
                  <CareerLengthInput value={value?.value2 ?? 0} onChange={(v) => update({ value2: v })} />
                </LabeledControl>
              ) : (
                <LabeledControl label="Maximum">
                  {numberInput(value?.value2, (v) => update({ value2: v }), "Maximum")}
                </LabeledControl>
              )}
            </div>
          )}
        </div>
      )}
      {hint && <div className="text-xs text-muted">{hint}</div>}
      {auxiliaryToggleLabel && onAuxiliaryToggleChange && (
        <label className="flex min-h-9 items-center gap-2 text-sm text-secondary">
          <input
            type="checkbox"
            checked={Boolean(auxiliaryToggleChecked)}
            onChange={(event) => onAuxiliaryToggleChange(event.target.checked)}
            className="h-5 w-5 rounded border-border bg-input text-accent focus:ring-accent"
          />
          <span>{auxiliaryToggleLabel}</span>
        </label>
      )}
    </div>
  );
}

function RatingStarInput({
  displayValue,
  onChangeDisplay,
  step,
}: {
  displayValue: number;
  onChangeDisplay: (v: number) => void;
  step: number;
}) {
  const [hoverValue, setHoverValue] = useState<number | null>(null);
  const activeValue = hoverValue ?? displayValue;

  return (
    <div className="flex items-center gap-0.5" onMouseLeave={() => setHoverValue(null)}>
      {[1, 2, 3, 4, 5].map((star) => (
        <button
          key={star}
          type="button"
          aria-label={`Set rating to ${star}`}
          onMouseMove={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
            const segments = Math.max(1, Math.ceil(ratio / step));
            const frac = Math.min(1, Number((segments * step).toFixed(2)));
            setHoverValue(star - 1 + frac);
          }}
          onMouseLeave={() => setHoverValue(null)}
          onClick={(e) => {
            const next =
              e.detail === 0
                ? star
                : (() => {
                    const rect = e.currentTarget.getBoundingClientRect();
                    const ratio = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
                    const segments = Math.max(1, Math.ceil(ratio / step));
                    const frac = Math.min(1, Number((segments * step).toFixed(2)));
                    return star - 1 + frac;
                  })();
            onChangeDisplay(next === displayValue ? 0 : next);
          }}
          className="relative inline-flex h-9 w-9 items-center justify-center rounded-lg text-accent transition-transform hover:scale-105 focus:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          <Star className="h-7 w-7 text-muted" />
          <span
            className="absolute left-1 top-1 h-7 overflow-hidden"
            style={{ width: `${Math.max(0, Math.min(1, activeValue - (star - 1))) * 1.75}rem` }}
          >
            <Star className="h-7 w-7 fill-current text-accent" />
          </span>
        </button>
      ))}
      {hoverValue != null && (
        <span className="text-xs text-secondary ml-1">{hoverValue.toFixed(step < 1 ? 1 : 0)}</span>
      )}
    </div>
  );
}

function RatingFilterInput({ rawValue, onChangeRaw }: { rawValue: number; onChangeRaw: (v: number) => void }) {
  const options = useRatingOptions();
  const displayValue = convertToRatingFormat(rawValue || undefined, options) ?? 0;
  const max = getRatingMax(options);
  const step = getRatingStep(options);

  const setDisplay = (v: number) => {
    const clamped = Math.min(max, Math.max(0, Number(v.toFixed(2))));
    onChangeRaw(convertFromRatingFormat(clamped, options));
  };

  if (options.type === "stars") {
    return (
      <div className="flex items-center gap-2">
        <RatingStarInput
          displayValue={displayValue}
          onChangeDisplay={setDisplay}
          step={getRatingPrecision(options.starPrecision)}
        />
      </div>
    );
  }

  // Decimal mode
  return (
    <input
      type="number"
      value={displayValue || ""}
      min={0}
      max={max}
      step={step}
      onChange={(e) => {
        const v = Number(e.target.value);
        if (Number.isFinite(v)) setDisplay(v);
      }}
      className="min-h-11 w-28 rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
    />
  );
}

export function RatingFilterEditor({
  value,
  onChange,
  modifiers,
}: {
  value?: IntCriterion;
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  const update = (patch: Partial<IntCriterion>) => {
    onChange({ value: value?.value ?? 0, modifier, ...value, ...patch });
  };

  return (
    <div className="space-y-2">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={(m) => update({ modifier: m })} />
      {!isNull && (
        <div className="space-y-2">
          <RatingFilterInput rawValue={value?.value ?? 0} onChangeRaw={(v) => update({ value: v })} />
          {isBetween && (
            <>
              <span className="text-xs text-muted">and</span>
              <RatingFilterInput rawValue={value?.value2 ?? 0} onChangeRaw={(v) => update({ value2: v })} />
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ===== String Editor =====

export function StringEditor({
  value,
  onChange,
  modifiers,
}: {
  value?: StringCriterion;
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })}
      />
      {!isNull && (
        <LabeledControl label="Value">
          <input
            aria-label="Value"
            type="text"
            value={value?.value ?? ""}
            onChange={(e) => onChange({ value: e.target.value, modifier })}
            className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            placeholder="Enter a value"
          />
        </LabeledControl>
      )}
    </div>
  );
}

export function CountryEditor({
  value,
  onChange,
  modifiers,
}: {
  value?: StringCriterion;
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";
  const useSelector = modifier === "EQUALS" || modifier === "NOT_EQUALS";

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(nextModifier) => onChange({ value: value?.value ?? "", modifier: nextModifier })}
      />
      {!isNull ? (
        <LabeledControl label="Value">
          {useSelector ? (
            <CountrySelect value={value?.value ?? ""} onChange={(country) => onChange({ value: country, modifier })} />
          ) : (
            <input
              aria-label="Value"
              type="text"
              value={value?.value ?? ""}
              onChange={(event) => onChange({ value: event.target.value, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              placeholder="Enter a stored code or custom value"
            />
          )}
        </LabeledControl>
      ) : null}
    </div>
  );
}

export function PathEditor({
  value,
  onChange,
  modifiers,
}: {
  value?: StringCriterion;
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
}) {
  const modifier = value?.modifier ?? "UNDER_PATH";
  const isNull = NULL_VALUE_MODIFIERS.has(modifier);
  const rootsQuery = useQuery({
    queryKey: ["library-folders", "roots", false],
    queryFn: () => metadata.libraryFolders(undefined, false),
    retry: false,
  });

  const updateModifier = (nextModifier: CriterionModifier) => {
    onChange({ value: value?.value ?? "", modifier: nextModifier });
  };

  const selectFolder = (path: string, checked: boolean) => {
    if (!checked) return;
    onChange({
      value: path,
      modifier: modifier === "NOT_UNDER_PATH" ? "NOT_UNDER_PATH" : "UNDER_PATH",
    });
  };

  return (
    <div className="space-y-4">
      <ModifierSelector modifiers={modifiers} selected={modifier} onSelect={updateModifier} />
      {!isNull ? (
        <>
          <div className="space-y-2">
            <div>
              <div className="text-sm font-medium text-secondary">Browse library folders</div>
              <p className="text-xs text-muted">Choose a folder to match it and all of its descendants.</p>
            </div>
            {rootsQuery.isLoading || (rootsQuery.isFetching && rootsQuery.isError) ? (
              <p className="text-xs text-muted">Loading library folders…</p>
            ) : rootsQuery.isError ? (
              <p className="text-xs text-muted">Folder browsing is unavailable. You can still enter a path manually.</p>
            ) : (
              <LibraryFolderTree
                roots={rootsQuery.data ?? []}
                selected={value?.value ? [value.value] : []}
                onToggle={selectFolder}
                selectionMode="single"
                probeChildren={false}
                emptyHint="No library folders are configured."
              />
            )}
          </div>
          <LabeledControl label="Path">
            <input
              aria-label="Path"
              type="text"
              value={value?.value ?? ""}
              onChange={(event) => onChange({ value: event.target.value, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              placeholder="Enter a file or folder path"
            />
          </LabeledControl>
        </>
      ) : null}
    </div>
  );
}

export function RemoteIdFilterEditor({
  value,
  onChange,
  modifiers,
  metadataServers,
}: {
  value?: StringCriterion & { endpoint?: string };
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
  metadataServers?: MetadataServer[];
}) {
  const appConfig = useOptionalAppConfig();
  const modifier = value?.modifier ?? "EQUALS";
  const selectedEndpoint = value?.endpoint?.trim() ?? "";
  const isNull = NULL_VALUE_MODIFIERS.has(modifier);
  const configuredServers = metadataServers ?? appConfig?.config?.scraping?.metadataServers ?? [];
  const options = useMemo(() => {
    const endpoints = new Set<string>();
    const configured = configuredServers.flatMap((server) => {
      const endpoint = server.endpoint.trim();
      const normalizedEndpoint = endpoint.toLowerCase();
      if (!endpoint || endpoints.has(normalizedEndpoint)) return [];
      endpoints.add(normalizedEndpoint);
      const optionValue = selectedEndpoint.toLowerCase() === normalizedEndpoint ? selectedEndpoint : endpoint;
      return [{ value: optionValue, label: server.name?.trim() || endpoint }];
    });

    if (selectedEndpoint && !endpoints.has(selectedEndpoint.toLowerCase())) {
      configured.push({ value: selectedEndpoint, label: `${selectedEndpoint} (unconfigured)` });
    }

    return configured;
  }, [configuredServers, selectedEndpoint]);

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(nextModifier) =>
          onChange({ value: value?.value ?? "", endpoint: selectedEndpoint, modifier: nextModifier })
        }
      />
      <select
        aria-label="Metadata Service"
        value={selectedEndpoint}
        onChange={(event) => onChange({ value: value?.value ?? "", endpoint: event.target.value, modifier })}
        className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none disabled:opacity-60 md:text-sm"
      >
        <option value="">Any metadata service</option>
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
      {!isNull && (
        <input
          type="text"
          aria-label="Remote ID value"
          value={value?.value ?? ""}
          onChange={(event) => onChange({ value: event.target.value, endpoint: selectedEndpoint, modifier })}
          className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
          placeholder="Value..."
        />
      )}
    </div>
  );
}

export function HashEditor({
  value,
  onChange,
  modifiers,
  options,
}: {
  value?: FingerprintCriterion;
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
  options: { value: string; label: string }[];
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const hashType = value?.type ?? options[0]?.value ?? "md5";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <select
        value={hashType}
        onChange={(event) => onChange({ type: event.target.value, value: value?.value ?? "", modifier })}
        className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(nextModifier) => onChange({ type: hashType, value: value?.value ?? "", modifier: nextModifier })}
      />
      {!isNull && (
        <LabeledControl label="Value">
          <input
            type="text"
            aria-label="Value"
            value={value?.value ?? ""}
            onChange={(event) => onChange({ type: hashType, value: event.target.value, modifier })}
            className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm"
            placeholder="Hash value..."
          />
        </LabeledControl>
      )}
    </div>
  );
}

// ===== Enum Editor =====

export function EnumEditor({
  value,
  onChange,
  options,
  modifiers,
}: {
  value?: StringCriterion;
  onChange: (v: unknown) => void;
  options: { value: string; label: string }[];
  modifiers: CriterionModifier[];
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })}
      />
      {!isNull && (
        <select
          value={value?.value ?? ""}
          onChange={(e) => onChange({ value: e.target.value, modifier })}
          className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
        >
          <option value="">Select...</option>
          {options.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          ))}
        </select>
      )}
    </div>
  );
}

export function MultiEnumEditor({
  value,
  onChange,
  options,
}: {
  value?: StringCriterion;
  onChange: (v: unknown) => void;
  options: { value: string; label: string }[];
}) {
  const selectionMode =
    value?.modifier === "NOT_MATCHES_REGEX"
      ? "exclude"
      : value?.modifier === "IS_NULL"
        ? "isNull"
        : value?.modifier === "NOT_NULL"
          ? "notNull"
          : "include";
  const selectedValues = useMemo(() => {
    const storedValues = (value as { _selectedValues?: string[] } | undefined)?._selectedValues;
    if (Array.isArray(storedValues) && storedValues.length > 0) {
      return options.filter((option) => storedValues.includes(option.value)).map((option) => option.value);
    }

    if (!value?.value) {
      return [];
    }

    if (value.modifier === "MATCHES_REGEX" || value.modifier === "NOT_MATCHES_REGEX") {
      try {
        const regex = new RegExp(value.value, "i");
        return options.filter((option) => regex.test(option.value)).map((option) => option.value);
      } catch {
        return [];
      }
    }

    return options.some((option) => option.value === value.value) ? [value.value] : [];
  }, [options, value]);

  const buildCriterion = (nextSelectedValues: string[], nextMode: "include" | "exclude" | "isNull" | "notNull") => {
    if (nextMode === "isNull") {
      onChange({ value: "", modifier: "IS_NULL", _selectedValues: nextSelectedValues });
      return;
    }

    if (nextMode === "notNull") {
      onChange({ value: "", modifier: "NOT_NULL", _selectedValues: nextSelectedValues });
      return;
    }

    const escapedValues = nextSelectedValues.map((selectedValue) =>
      selectedValue.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"),
    );
    onChange({
      value: escapedValues.length > 0 ? `^(?:${escapedValues.join("|")})$` : "",
      modifier: nextMode === "exclude" ? "NOT_MATCHES_REGEX" : "MATCHES_REGEX",
      _selectedValues: nextSelectedValues,
    });
  };

  const toggleValue = (optionValue: string) => {
    const nextSelectedValues = selectedValues.includes(optionValue)
      ? selectedValues.filter((selectedValue) => selectedValue !== optionValue)
      : [...selectedValues, optionValue];
    buildCriterion(nextSelectedValues, selectionMode);
  };

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2" role="group" aria-label="Match mode">
        {(
          [
            ["include", "Any Of"],
            ["exclude", "None Of"],
            ["isNull", "No Value"],
            ["notNull", "Has Value"],
          ] as const
        ).map(([mode, label]) => (
          <button
            key={mode}
            onClick={() => buildCriterion(selectedValues, mode)}
            className={`min-h-9 rounded-lg border px-3 py-1.5 text-sm ${
              selectionMode === mode
                ? "bg-accent text-white border-accent"
                : "border-border text-secondary hover:text-foreground hover:border-accent/50"
            }`}
          >
            {label}
          </button>
        ))}
      </div>
      {(selectionMode === "include" || selectionMode === "exclude") && (
        <div className="grid gap-1 sm:grid-cols-2">
          {options.map((option) => {
            const checked = selectedValues.includes(option.value);

            return (
              <label
                key={option.value}
                className="flex min-h-9 items-center gap-2 rounded-lg border border-border bg-input px-3 py-1.5 text-sm text-foreground"
              >
                <input
                  type="checkbox"
                  checked={checked}
                  onChange={() => toggleValue(option.value)}
                  className="h-5 w-5 accent-accent"
                />
                <span>{option.label}</span>
              </label>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ===== Date Editor =====

export function DateEditor({
  value,
  onChange,
  modifiers,
}: {
  value?: DateCriterion;
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(m) => onChange({ value: value?.value ?? "", modifier: m })}
      />
      {!isNull && (
        <div className={`grid gap-3 ${isBetween ? "sm:grid-cols-2" : ""}`}>
          <LabeledControl label={isBetween ? "Minimum" : "Value"}>
            <IsoDateInput
              aria-label={isBetween ? "Minimum" : "Value"}
              value={value?.value ?? ""}
              onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            />
          </LabeledControl>
          {isBetween && (
            <LabeledControl label="Maximum">
              <IsoDateInput
                aria-label="Maximum"
                value={value?.value2 ?? ""}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              />
            </LabeledControl>
          )}
        </div>
      )}
    </div>
  );
}

// ===== Timestamp Editor =====

function getDefaultLocalTimestampValue() {
  const date = new Date();
  date.setHours(12, 0, 0, 0);

  const pad = (part: number) => String(part).padStart(2, "0");

  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

export function TimestampEditor({
  value,
  onChange,
  modifiers,
}: {
  value?: TimestampCriterion;
  onChange: (v: unknown) => void;
  modifiers: CriterionModifier[];
}) {
  const modifier = value?.modifier ?? "EQUALS";
  const isBetween = modifier === "BETWEEN" || modifier === "NOT_BETWEEN";
  const isNull = modifier === "IS_NULL" || modifier === "NOT_NULL";
  const ensureTimestampValue = (current?: string) =>
    current && current.length > 0 ? current : getDefaultLocalTimestampValue();

  return (
    <div className="space-y-2">
      <ModifierSelector
        modifiers={modifiers}
        selected={modifier}
        onSelect={(m) => {
          const nextIsNull = m === "IS_NULL" || m === "NOT_NULL";
          const nextIsBetween = m === "BETWEEN" || m === "NOT_BETWEEN";
          onChange({
            value: nextIsNull ? (value?.value ?? "") : ensureTimestampValue(value?.value),
            value2: nextIsBetween ? ensureTimestampValue(value?.value2) : undefined,
            modifier: m,
          });
        }}
      />
      {!isNull && (
        <div className={`grid gap-3 ${isBetween ? "sm:grid-cols-2" : ""}`}>
          <LabeledControl label={isBetween ? "Minimum" : "Value"}>
            <IsoDateInput
              aria-label={isBetween ? "Minimum" : "Value"}
              pickerType="datetime-local"
              value={value?.value ?? ensureTimestampValue(value?.value)}
              onChange={(e) => onChange({ value: e.target.value, value2: value?.value2, modifier })}
              className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
            />
          </LabeledControl>
          {isBetween && (
            <LabeledControl label="Maximum">
              <IsoDateInput
                aria-label="Maximum"
                pickerType="datetime-local"
                value={value?.value2 ?? ensureTimestampValue(value?.value2)}
                onChange={(e) => onChange({ value: value?.value, value2: e.target.value, modifier })}
                className="min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground focus:border-accent focus:outline-none md:text-sm"
              />
            </LabeledControl>
          )}
        </div>
      )}
    </div>
  );
}
