import { useCallback, useEffect, useId, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, X } from "lucide-react";
import { InteractiveRating } from "./Rating";
import { IsoDateInput } from "./IsoDateInput";
import type { BulkUpdateMode, CustomFieldDefinition, CustomFieldEntityType } from "../api/types";
import { tagGroups } from "../api/client";
import { StudioSelector } from "./StudioSelector";
import { EntityReferenceMultiSelector, type EntityReferenceType } from "./EntityReferenceSelector";
import { CountrySelect } from "./Country";
import { ConfiguredFieldInput, normalizeConfiguredFieldValue } from "./CustomFields";
import { useCustomFieldDefinitions } from "../hooks/useCustomFieldDefinitions";

// ===== Generic Bulk Edit Dialog =====

export interface BulkEditField {
  key: string;
  label: string;
  type: "rating" | "number" | "bool" | "string" | "date" | "select" | "multiId" | "country";
  entityType?: "tags" | "performers" | "studios" | "groups" | "galleries" | "tagGroups";
  options?: { label: string; value: string | number }[];
  modeKey?: string;
  nullable?: boolean;
  serializeValue?: (value: unknown) => unknown;
}

interface BulkEditDialogProps {
  open: boolean;
  onClose: () => void;
  title: string;
  selectedCount: number;
  fields: BulkEditField[];
  onApply: (values: Record<string, unknown>) => void;
  isPending?: boolean;
  /**
   * When set, the dialog loads this entity's custom field definitions and offers a Custom fields section.
   * Only pass it for entities whose bulk endpoint accepts `customFields`, `customFieldMode`, and
   * `clearFields: ["customFields.<key>"]`.
   */
  customFieldEntityType?: CustomFieldEntityType;
}

/** Prefix shared by the request's `clearFields` entries and this dialog's internal state keys. */
const CUSTOM_FIELD_KEY_PREFIX = "customFields.";

function toCustomFieldStateKey(definitionKey: string) {
  return `${CUSTOM_FIELD_KEY_PREFIX}${definitionKey}`;
}

export function BulkEditDialog({
  open,
  onClose,
  title,
  selectedCount,
  fields,
  onApply,
  isPending,
  customFieldEntityType,
}: BulkEditDialogProps) {
  const [values, setValues] = useState<Record<string, unknown>>({});
  const [enabledFields, setEnabledFields] = useState<Set<string>>(new Set());
  const [clearedCustomFields, setClearedCustomFields] = useState<Set<string>>(new Set());
  const [customFieldMode, setCustomFieldMode] = useState<BulkUpdateMode>("ADD");
  const [invalidJsonKeys, setInvalidJsonKeys] = useState<Set<string>>(new Set());
  const titleId = useId();
  const customFieldDefinitionsQuery = useCustomFieldDefinitions(customFieldEntityType, Boolean(customFieldEntityType));
  const customFieldDefinitions = customFieldEntityType ? (customFieldDefinitionsQuery.data ?? []) : [];

  const dropCustomFieldDraft = (definition: CustomFieldDefinition) => {
    setValues((current) => {
      const next = { ...current };
      delete next[toCustomFieldStateKey(definition.key)];
      return next;
    });
    setInvalidJsonKeys((current) => withoutKey(current, definition.key));
  };

  const toggleCustomField = (definition: CustomFieldDefinition) => {
    const stateKey = toCustomFieldStateKey(definition.key);
    if (enabledFields.has(stateKey)) {
      setEnabledFields((current) => withoutKey(current, stateKey));
      setClearedCustomFields((current) => withoutKey(current, definition.key));
      dropCustomFieldDraft(definition);
    } else {
      setEnabledFields((current) => new Set(current).add(stateKey));
    }
  };

  const toggleCustomFieldCleared = (definition: CustomFieldDefinition) => {
    if (clearedCustomFields.has(definition.key)) {
      setClearedCustomFields((current) => withoutKey(current, definition.key));
    } else {
      setClearedCustomFields((current) => new Set(current).add(definition.key));
      dropCustomFieldDraft(definition);
    }
  };

  const updateJsonValidity = useCallback((key: string, isValid: boolean) => {
    setInvalidJsonKeys((current) => {
      if (isValid) return withoutKey(current, key);
      return current.has(key) ? current : new Set(current).add(key);
    });
  }, []);

  const hasInvalidCustomFieldJson = customFieldDefinitions.some(
    (definition) => enabledFields.has(toCustomFieldStateKey(definition.key)) && invalidJsonKeys.has(definition.key),
  );

  const toggleField = (field: BulkEditField) => {
    setEnabledFields((prev) => {
      const next = new Set(prev);
      if (next.has(field.key)) {
        next.delete(field.key);
        setValues((currentValues) => {
          const nextValues = { ...currentValues };
          delete nextValues[field.key];
          delete nextValues[getModeKey(field)];
          return nextValues;
        });
      } else {
        next.add(field.key);
      }
      return next;
    });
  };

  const updateValue = (key: string, val: unknown) => {
    setValues((prev) => ({ ...prev, [key]: val }));
  };

  const buildPayload = () => {
    const result: Record<string, unknown> = {};
    const clearFields: string[] = [];
    for (const f of fields) {
      if (enabledFields.has(f.key)) {
        const serializedValue = serializeBulkFieldValue(f, values[f.key]);
        if (f.nullable && (serializedValue == null || serializedValue === "")) {
          result[f.key] = null;
          clearFields.push(f.key);
        } else {
          result[f.key] = serializedValue;
        }
        if (f.type === "multiId") {
          result[getModeKey(f)] = values[getModeKey(f)] ?? "ADD";
        }
      }
    }
    const customFields: Record<string, unknown> = {};
    for (const definition of customFieldDefinitions) {
      const stateKey = toCustomFieldStateKey(definition.key);
      if (!enabledFields.has(stateKey)) continue;
      if (clearedCustomFields.has(definition.key)) {
        clearFields.push(stateKey);
        continue;
      }
      const normalizedValue = normalizeConfiguredFieldValue(values[stateKey], definition);
      if (normalizedValue === undefined) continue;
      customFields[definition.key] = normalizedValue;
    }
    if (Object.keys(customFields).length > 0) {
      result.customFields = customFields;
      result.customFieldMode = customFieldMode;
    }
    if (clearFields.length > 0) {
      result.clearFields = clearFields;
    }
    return result;
  };

  useEffect(() => {
    if (!open) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && !event.defaultPrevented) onClose();
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [open, onClose]);

  if (!open) return null;

  const payload = buildPayload();
  // A ticked field with nothing entered contributes nothing; do not send a request that changes nothing.
  const hasChanges = Object.values(payload).some((value) => value !== undefined);
  const changedCount =
    fields.filter((field) => enabledFields.has(field.key) && payload[field.key] !== undefined).length +
    Object.keys((payload.customFields as Record<string, unknown> | undefined) ?? {}).length +
    clearedCustomFields.size;
  const sections = BULK_SECTIONS.map((section) => ({
    section,
    fields: fields.filter((field) => getBulkSection(field) === section),
  })).filter(({ fields: sectionFields }) => sectionFields.length > 0);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 md:p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="flex h-[100dvh] w-full flex-col overflow-hidden border-border bg-surface shadow-2xl md:h-auto md:max-h-[min(88dvh,52rem)] md:max-w-2xl md:rounded-2xl md:border"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex min-h-16 items-center justify-between gap-3 border-b border-border px-4 pt-[env(safe-area-inset-top)] md:px-6 md:pt-0">
          <div className="flex min-w-0 items-center gap-2">
            <h2 id={titleId} className="truncate text-lg font-semibold text-foreground">
              {title}
            </h2>
            <span className="shrink-0 rounded-full bg-card px-2 py-0.5 text-xs font-medium text-secondary">
              {selectedCount} selected
            </span>
          </div>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="inline-flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-muted hover:bg-card hover:text-foreground"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="flex-1 space-y-6 overflow-y-auto overscroll-contain px-4 py-5 md:px-6">
          <p className="text-sm text-muted">
            Tick a field to change it on every selected item. Unticked fields stay as they are.
          </p>
          {sections.map(({ section, fields: sectionFields }) => (
            <div key={section} className="space-y-1.5">
              <h3 className={SECTION_HEADING_CLASS}>{section}</h3>
              {sectionFields.map((field) => (
                <BulkFieldEditor
                  key={field.key}
                  field={field}
                  enabled={enabledFields.has(field.key)}
                  onToggle={() => toggleField(field)}
                  value={values[field.key]}
                  mode={(values[getModeKey(field)] as BulkUpdateMode) ?? "ADD"}
                  onValueChange={(v) => updateValue(field.key, v)}
                  onModeChange={(m) => updateValue(getModeKey(field), m)}
                />
              ))}
            </div>
          ))}
          {customFieldDefinitions.length > 0 && (
            <CustomFieldsBulkSection
              definitions={customFieldDefinitions}
              mode={customFieldMode}
              onModeChange={setCustomFieldMode}
              enabledFields={enabledFields}
              clearedFields={clearedCustomFields}
              values={values}
              onToggle={toggleCustomField}
              onToggleCleared={toggleCustomFieldCleared}
              onValueChange={(definition, nextValue) => updateValue(toCustomFieldStateKey(definition.key), nextValue)}
              onJsonValidityChange={updateJsonValidity}
            />
          )}
        </div>

        <div className="flex min-h-[4.75rem] items-center gap-3 border-t border-border px-4 pb-[env(safe-area-inset-bottom)] md:px-6 md:pb-0">
          <span className="min-w-0 text-sm text-muted">
            {changedCount === 0 || !hasChanges
              ? "Nothing to change yet"
              : `Changes ${changedCount} ${changedCount === 1 ? "field" : "fields"} on ${selectedCount} selected ${selectedCount === 1 ? "item" : "items"}`}
          </span>
          <div className="ml-auto flex shrink-0 gap-2">
            <button
              type="button"
              onClick={onClose}
              className="min-h-11 rounded-lg border border-border px-4 text-sm text-secondary hover:bg-card hover:text-foreground"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={() => onApply(payload)}
              disabled={isPending || !hasChanges || hasInvalidCustomFieldJson}
              className="min-h-11 rounded-lg bg-accent px-5 text-sm font-bold text-white hover:bg-accent-hover disabled:opacity-50"
            >
              {isPending ? "Applying..." : "Apply"}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

function BulkFieldEditor({
  field,
  enabled,
  onToggle,
  value,
  mode,
  onValueChange,
  onModeChange,
}: {
  field: BulkEditField;
  enabled: boolean;
  onToggle: () => void;
  value: unknown;
  mode: BulkUpdateMode;
  onValueChange: (v: unknown) => void;
  onModeChange: (m: BulkUpdateMode) => void;
}) {
  const clearButton = field.nullable && field.type === "select" && field.entityType !== undefined && (
    <ClearValueButton label={field.label} clearing={value == null} onClick={() => onValueChange(undefined)} />
  );

  return (
    <BulkFieldShell label={field.label} enabled={enabled} onToggle={onToggle} headerAction={clearButton}>
      {field.type === "rating" && (
        <div className="flex min-h-11 items-center rounded-lg border border-border bg-input px-3 py-2">
          <InteractiveRating
            value={value as number | undefined}
            onChange={(nextValue) => onValueChange(nextValue || undefined)}
          />
        </div>
      )}
      {field.type === "number" && (
        <input
          type="number"
          aria-label={field.label}
          value={(value as number) ?? ""}
          onChange={(e) => onValueChange(e.target.value ? Number(e.target.value) : undefined)}
          className={`${INPUT_CLASS} md:w-40`}
        />
      )}
      {field.type === "bool" && (
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            aria-pressed={value === true}
            onClick={() => onValueChange(true)}
            className={pillClass(value === true)}
          >
            True
          </button>
          <button
            type="button"
            aria-pressed={value === false}
            onClick={() => onValueChange(false)}
            className={pillClass(value === false)}
          >
            False
          </button>
        </div>
      )}
      {field.type === "string" && (
        <input
          type="text"
          aria-label={field.label}
          value={(value as string) ?? ""}
          onChange={(e) => onValueChange(e.target.value)}
          className={INPUT_CLASS}
        />
      )}
      {field.type === "date" && (
        <IsoDateInput
          aria-label={field.label}
          value={(value as string) ?? ""}
          onChange={(e) => onValueChange(e.target.value)}
          className={INPUT_CLASS}
        />
      )}
      {field.type === "country" && <CountrySelect value={(value as string) ?? ""} onChange={onValueChange} />}
      {field.type === "select" && field.entityType === "studios" && (
        <StudioSelector
          value={value as number | undefined}
          onChange={(nextValue) => onValueChange(nextValue)}
          inputClassName={INPUT_CLASS}
        />
      )}
      {field.type === "select" && field.entityType === "tagGroups" && (
        <TagGroupBulkSelect label={field.label} value={value as number | undefined} onValueChange={onValueChange} />
      )}
      {field.type === "select" && field.entityType !== "studios" && field.entityType !== "tagGroups" && (
        <select
          aria-label={field.label}
          value={String(value ?? "")}
          onChange={(e) => {
            if (!e.target.value) {
              onValueChange(undefined);
              return;
            }

            const selectedOption = field.options?.find((option) => String(option.value) === e.target.value);
            onValueChange(selectedOption?.value ?? e.target.value);
          }}
          className={INPUT_CLASS}
        >
          <option value="">Select...</option>
          {field.options?.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      )}
      {field.type === "multiId" && isMultiIdEntityType(field.entityType) && (
        <MultiIdBulkEditor
          entityType={field.entityType}
          value={(value as number[]) ?? []}
          mode={mode}
          onValueChange={onValueChange}
          label={field.label}
          onModeChange={onModeChange}
        />
      )}
    </BulkFieldShell>
  );
}

/**
 * Renders a field as a quiet row while unticked and as a card while ticked. The checkbox stays at the same place in
 * the tree in both states, so ticking it with the keyboard does not remount it and drop focus.
 */
function BulkFieldShell({
  label,
  detail,
  enabled,
  onToggle,
  headerAction,
  children,
}: {
  label: string;
  detail?: string;
  enabled: boolean;
  onToggle: () => void;
  headerAction?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <div
      className={
        enabled ? FIELD_CARD_CLASS : "rounded-lg px-3 text-secondary hover:bg-card-hover hover:text-foreground"
      }
    >
      <div className={`flex items-center gap-3 ${enabled ? "min-h-10" : "min-h-11"}`}>
        <label
          className={`flex min-w-0 flex-1 cursor-pointer items-center gap-3 self-stretch text-sm ${enabled ? "font-medium text-foreground" : ""}`}
        >
          <input type="checkbox" checked={enabled} onChange={onToggle} className={CHECKBOX_CLASS} />
          {label}
          {detail && <span className="truncate text-xs font-normal text-muted">{detail}</span>}
        </label>
        {enabled ? (
          headerAction
        ) : (
          <span aria-hidden="true" className="shrink-0 text-xs text-muted">
            Not changed
          </span>
        )}
      </div>
      {enabled && children}
    </div>
  );
}

/** With nothing picked, a ticked nullable field clears the value, so that state is shown as a status, not a button. */
function ClearValueButton({ label, clearing, onClick }: { label: string; clearing: boolean; onClick: () => void }) {
  if (clearing) {
    return (
      <span className="inline-flex min-h-8 shrink-0 items-center gap-1.5 rounded-lg border border-accent/50 bg-accent/10 px-2.5 text-xs text-accent">
        <X className="h-3 w-3" />
        Clearing value
      </span>
    );
  }
  return (
    <button
      type="button"
      aria-label={`Clear value for ${label}`}
      onClick={onClick}
      className="inline-flex min-h-8 shrink-0 items-center gap-1.5 rounded-lg border border-transparent px-2.5 text-xs text-muted hover:bg-card-hover hover:text-foreground"
    >
      <X className="h-3 w-3" />
      Clear value
    </button>
  );
}

function CustomFieldsBulkSection({
  definitions,
  mode,
  onModeChange,
  enabledFields,
  clearedFields,
  values,
  onToggle,
  onToggleCleared,
  onValueChange,
  onJsonValidityChange,
}: {
  definitions: CustomFieldDefinition[];
  mode: BulkUpdateMode;
  onModeChange: (mode: BulkUpdateMode) => void;
  enabledFields: Set<string>;
  clearedFields: Set<string>;
  values: Record<string, unknown>;
  onToggle: (definition: CustomFieldDefinition) => void;
  onToggleCleared: (definition: CustomFieldDefinition) => void;
  onValueChange: (definition: CustomFieldDefinition, value: unknown) => void;
  onJsonValidityChange: (key: string, isValid: boolean) => void;
}) {
  const modeRadioName = useId();
  const bodyId = useId();
  const [expanded, setExpanded] = useState(false);
  const tickedCount = definitions.filter((definition) =>
    enabledFields.has(toCustomFieldStateKey(definition.key)),
  ).length;
  // Collapsed by default so a long definition list does not bury the standard fields. Ticking a field opens it,
  // and the header keeps showing how many are ticked while it is collapsed.
  const open = expanded;
  const valuedDefinitions = definitions.filter(
    (definition) => enabledFields.has(toCustomFieldStateKey(definition.key)) && !clearedFields.has(definition.key),
  );
  // The mode only travels with values, so it is offered only while some ticked field is not being cleared.
  const showMode = valuedDefinitions.length > 0;
  const anyMultiValue = valuedDefinitions.some((definition) => definition.isMultiValue);

  return (
    <section role="group" aria-label="Custom fields" className="space-y-1.5">
      <button
        type="button"
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={() => setExpanded((current) => !current)}
        className="flex min-h-11 w-full items-center gap-2 rounded-lg px-1 text-left text-muted hover:text-foreground"
      >
        {open ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
        <span className="text-[11px] font-semibold uppercase tracking-wider">Custom fields</span>
        <span className="ml-auto text-xs">
          {tickedCount > 0 ? `${tickedCount} of ${definitions.length} selected` : `${definitions.length} fields`}
        </span>
      </button>
      {open && (
        <div id={bodyId} className="space-y-1.5">
          {showMode && (
            <div className="space-y-1.5 px-1 pb-1">
              <fieldset className="flex flex-wrap items-center gap-2">
                <legend className="sr-only">Custom field mode</legend>
                <span aria-hidden="true" className="mr-1 text-sm font-medium text-secondary">
                  Mode
                </span>
                {BULK_MODE_ORDER.map((candidate) => (
                  <label
                    key={candidate}
                    className={`${pillClass(candidate === mode)} cursor-pointer has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-accent has-[:focus-visible]:ring-offset-1 has-[:focus-visible]:ring-offset-surface`}
                  >
                    <input
                      type="radio"
                      name={modeRadioName}
                      value={candidate}
                      checked={candidate === mode}
                      onChange={() => onModeChange(candidate)}
                      className="sr-only"
                    />
                    {BULK_MODE_LABELS[candidate]}
                  </label>
                ))}
              </fieldset>
              <p className="text-xs text-muted">
                {mode === "SET"
                  ? "Overwrite replaces the current value on every selected item."
                  : mode === "ADD"
                    ? anyMultiValue
                      ? "Add appends new entries to multi-value fields and overwrites single-value fields."
                      : "Add overwrites single-value fields with the entered value."
                    : anyMultiValue
                      ? "Remove drops matching entries from multi-value fields and clears single-value fields whose value matches."
                      : "Remove clears single-value fields whose current value matches the entered value."}
              </p>
            </div>
          )}
          {definitions.map((definition) => {
            const stateKey = toCustomFieldStateKey(definition.key);
            const enabled = enabledFields.has(stateKey);
            const cleared = clearedFields.has(definition.key);
            const label = definition.label || definition.key;
            // The accessible name starts with the visible text so voice control can target the button by what it shows.
            const clearText = cleared ? "Clearing value on every selected item" : "Clear value";
            return (
              <BulkFieldShell
                key={definition.key}
                label={label}
                detail={definition.key}
                enabled={enabled}
                onToggle={() => {
                  if (!enabled) setExpanded(true);
                  onToggle(definition);
                }}
                headerAction={
                  <button
                    type="button"
                    aria-pressed={cleared}
                    aria-label={`${clearText} for ${label}`}
                    onClick={() => onToggleCleared(definition)}
                    className={`inline-flex min-h-8 shrink-0 items-center gap-1.5 rounded-lg border px-2.5 text-xs ${
                      cleared
                        ? "border-accent/50 bg-accent/10 text-accent"
                        : "border-transparent text-muted hover:bg-card-hover hover:text-foreground"
                    }`}
                  >
                    <X className="h-3 w-3" />
                    {cleared ? "Clearing value" : "Clear value"}
                  </button>
                }
              >
                {cleared ? (
                  <p className="flex min-h-11 items-center rounded-lg border border-border bg-input px-3 text-sm italic text-muted">
                    Removed from every selected item
                  </p>
                ) : (
                  <ConfiguredFieldInput
                    definition={definition}
                    value={values[stateKey]}
                    onChange={(nextValue) => onValueChange(definition, nextValue)}
                    onJsonValidityChange={onJsonValidityChange}
                    ariaLabel={label}
                  />
                )}
              </BulkFieldShell>
            );
          })}
        </div>
      )}
    </section>
  );
}

function TagGroupBulkSelect({
  label,
  value,
  onValueChange,
}: {
  label: string;
  value?: number;
  onValueChange: (v: unknown) => void;
}) {
  const { data: groups = [], isLoading } = useQuery({ queryKey: ["tag-groups"], queryFn: tagGroups.list });

  return (
    <select
      aria-label={label}
      value={String(value ?? "")}
      onChange={(event) => onValueChange(event.target.value ? Number(event.target.value) : undefined)}
      className={INPUT_CLASS}
    >
      <option value="">{isLoading ? "Loading tag groups..." : "Select tag group..."}</option>
      {groups.map((group) => (
        <option key={group.id} value={group.id}>
          {group.name}
        </option>
      ))}
    </select>
  );
}

function MultiIdBulkEditor({
  entityType,
  label,
  value,
  mode,
  onValueChange,
  onModeChange,
}: {
  entityType: "tags" | "performers" | "studios" | "groups" | "galleries";
  label: string;
  value: number[];
  mode: BulkUpdateMode;
  onValueChange: (v: unknown) => void;
  onModeChange: (m: BulkUpdateMode) => void;
}) {
  return (
    <div className="space-y-2.5">
      <div className="flex flex-wrap items-center gap-2" role="group" aria-label={`${label} mode`}>
        <span aria-hidden="true" className="mr-1 text-sm font-medium text-secondary">
          Mode
        </span>
        {BULK_MODE_ORDER.map((m) => (
          <button
            key={m}
            type="button"
            aria-pressed={m === mode}
            onClick={() => onModeChange(m)}
            className={pillClass(m === mode)}
          >
            {BULK_MODE_LABELS[m]}
          </button>
        ))}
        <span className="text-xs text-muted">{MULTI_ID_MODE_HINTS[mode](entityType)}</span>
      </div>

      <EntityReferenceMultiSelector
        entityType={toReferenceEntityType(entityType)}
        values={value}
        onChange={onValueChange as (values: number[]) => void}
        placeholder={`Search ${entityType}...`}
        inputClassName={INPUT_CLASS}
        resultsMaxHeight={160}
      />
    </div>
  );
}

function toReferenceEntityType(
  entityType: "tags" | "performers" | "studios" | "groups" | "galleries",
): EntityReferenceType {
  switch (entityType) {
    case "tags":
      return "tag";
    case "performers":
      return "performer";
    case "studios":
      return "studio";
    case "groups":
      return "group";
    case "galleries":
      return "gallery";
  }
}

function isMultiIdEntityType(
  entityType: BulkEditField["entityType"],
): entityType is "tags" | "performers" | "studios" | "groups" | "galleries" {
  return (
    entityType === "tags" ||
    entityType === "performers" ||
    entityType === "studios" ||
    entityType === "groups" ||
    entityType === "galleries"
  );
}

const BULK_MODE_LABELS: Record<BulkUpdateMode, string> = {
  SET: "Overwrite",
  ADD: "Add",
  REMOVE: "Remove",
};

/** Least destructive first, matching the default mode. */
const BULK_MODE_ORDER: BulkUpdateMode[] = ["ADD", "REMOVE", "SET"];

const MULTI_ID_MODE_HINTS: Record<BulkUpdateMode, (entityType: string) => string> = {
  ADD: (entityType) => `Keeps existing ${entityType}`,
  REMOVE: (entityType) => `Removes only these ${entityType}`,
  SET: (entityType) => `Replaces all ${entityType}`,
};

// Sizing and colours follow the filter dialog so the two read as one app.
const INPUT_CLASS =
  "min-h-11 w-full rounded-lg border border-border bg-input px-3 py-2 text-base text-foreground placeholder:text-muted focus:border-accent focus:outline-none md:text-sm";
const CHECKBOX_CLASS = "h-4 w-4 shrink-0 cursor-pointer rounded border-border accent-accent";
const FIELD_CARD_CLASS = "space-y-2.5 rounded-xl border border-border bg-card px-3 pb-3 pt-1";
const SECTION_HEADING_CLASS = "px-1 text-[11px] font-semibold uppercase tracking-wider text-muted";

function pillClass(active: boolean) {
  return `min-h-9 rounded-lg border px-3 py-1.5 text-sm ${
    active
      ? "border-accent bg-accent text-white"
      : "border-border text-secondary hover:border-accent/50 hover:text-foreground"
  }`;
}

const BULK_SECTIONS = ["Status", "Details", "Relations"] as const;

function getBulkSection(field: BulkEditField): (typeof BULK_SECTIONS)[number] {
  if (field.type === "rating" || field.type === "bool") return "Status";
  if (field.type === "multiId") return "Relations";
  return "Details";
}

function withoutKey(set: Set<string>, key: string) {
  if (!set.has(key)) return set;
  const next = new Set(set);
  next.delete(key);
  return next;
}

function getModeKey(field: BulkEditField) {
  return field.modeKey ?? `${field.key}Mode`;
}

function serializeBulkFieldValue(field: BulkEditField, value: unknown) {
  if (field.serializeValue) {
    return field.serializeValue(value);
  }

  if (field.type === "multiId") {
    return value ?? [];
  }

  if (field.type === "string" || field.type === "date") {
    return value ?? "";
  }

  return value;
}

// ===== Pre-configured bulk edit field sets =====

export const VIDEO_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "organized", label: "Organized", type: "bool" },
  { key: "isVr", label: "VR", type: "bool" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "date", label: "Date", type: "date" },
  { key: "code", label: "Studio Code", type: "string" },
  { key: "director", label: "Director", type: "string" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
  { key: "performerIds", label: "Performers", type: "multiId", entityType: "performers", modeKey: "performerMode" },
  {
    key: "groupIds",
    label: "Groups",
    type: "multiId",
    entityType: "groups",
    modeKey: "groupMode",
    serializeValue: (value) => ((value as number[] | undefined) ?? []).map((groupId) => ({ groupId, videoIndex: 0 })),
  },
];

export const PERFORMER_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "favorite", label: "Favorite", type: "bool" },
  {
    key: "gender",
    label: "Gender",
    type: "select",
    options: ["Female", "Male", "TransgenderFemale", "TransgenderMale", "Intersex", "NonBinary"].map((value) => ({
      value,
      label: value.replace(/([a-z])([A-Z])/g, "$1 $2"),
    })),
  },
  { key: "country", label: "Country", type: "country", nullable: true },
  { key: "details", label: "Details", type: "string" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
];

export const GALLERY_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "organized", label: "Organized", type: "bool" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "date", label: "Date", type: "date" },
  { key: "code", label: "Studio Code", type: "string" },
  { key: "photographer", label: "Photographer", type: "string" },
  { key: "details", label: "Details", type: "string" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
  { key: "performerIds", label: "Performers", type: "multiId", entityType: "performers", modeKey: "performerMode" },
];

export const IMAGE_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "organized", label: "Organized", type: "bool" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "date", label: "Date", type: "date" },
  { key: "code", label: "Studio Code", type: "string" },
  { key: "photographer", label: "Photographer", type: "string" },
  { key: "details", label: "Details", type: "string" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
  { key: "performerIds", label: "Performers", type: "multiId", entityType: "performers", modeKey: "performerMode" },
  { key: "galleryIds", label: "Galleries", type: "multiId", entityType: "galleries", modeKey: "galleryMode" },
];

export const AUDIO_BULK_FIELDS: BulkEditField[] = [
  { key: "organized", label: "Organized", type: "bool" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "date", label: "Date", type: "date" },
  { key: "code", label: "Studio Code", type: "string" },
  { key: "details", label: "Details", type: "string" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
  { key: "performerIds", label: "Performers", type: "multiId", entityType: "performers", modeKey: "performerMode" },
];

export const TEXT_BULK_FIELDS: BulkEditField[] = [
  { key: "organized", label: "Organized", type: "bool" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "date", label: "Date", type: "date" },
  { key: "code", label: "Studio Code", type: "string" },
  { key: "details", label: "Details", type: "string" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
  { key: "performerIds", label: "Performers", type: "multiId", entityType: "performers", modeKey: "performerMode" },
];

export const TAG_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "description", label: "Description", type: "string" },
  { key: "color", label: "Badge Color", type: "string" },
  { key: "tagGroupId", label: "Tag Group", type: "select", entityType: "tagGroups", nullable: true },
  { key: "minOccurrenceSec", label: "Min Seconds", type: "number" },
  { key: "minOccurrencePercent", label: "Min Percent", type: "number" },
  { key: "organized", label: "Organized", type: "bool" },
  { key: "favorite", label: "Favorite", type: "bool" },
  { key: "parentIds", label: "Parent Tags", type: "multiId", entityType: "tags", modeKey: "parentMode" },
  { key: "childIds", label: "Child Tags", type: "multiId", entityType: "tags", modeKey: "childMode" },
];

export const STUDIO_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "favorite", label: "Favorite", type: "bool" },
  { key: "details", label: "Details", type: "string" },
  { key: "organized", label: "Organized", type: "bool" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
];

export const GROUP_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "date", label: "Date", type: "date" },
  { key: "director", label: "Director", type: "string" },
  { key: "description", label: "Description", type: "string" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
];
