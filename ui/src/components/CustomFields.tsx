import { useMemo } from "react";
import type { CustomFieldDefinition, CustomFieldEntityType, CustomFieldType } from "../api/types";
import {
  EntityReferenceMultiSelector,
  EntityReferenceSelector,
  EntityReferenceValue,
  isEntityReferenceType,
  parseEntityReferenceIds,
} from "./EntityReferenceSelector";
import { useCustomFieldDefinitions } from "../hooks/useCustomFieldDefinitions";
import { formatDate } from "../utils/dateFormat";
import { IsoDateInput } from "./IsoDateInput";

export function CustomFieldsDisplay({
  customFields,
  entityType,
}: {
  customFields?: Record<string, unknown>;
  entityType?: CustomFieldEntityType;
}) {
  const definitionsQuery = useCustomFieldDefinitions(entityType, Boolean(entityType));
  const definitions = definitionsQuery.data ?? [];
  const entries = useMemo(() => getDisplayEntries(customFields, definitions), [customFields, definitions]);

  if (entries.length === 0) return null;

  return (
    <div className="bg-card rounded-xl p-4">
      <h3 className="text-sm font-semibold text-secondary mb-3">Custom Fields</h3>
      <div className="grid grid-cols-2 gap-2 text-sm">
        {entries.map(({ key, definition, value }) => {
          const label = definition?.label || key;
          const urlValue = definition?.type === "url" && typeof value === "string" ? value.trim() : "";

          return (
            <div key={key} className="flex flex-col">
              <span className="text-muted text-xs">{label}</span>
              {urlValue ? (
                <a href={urlValue} target="_blank" rel="noreferrer" className="text-accent hover:underline break-all">
                  {formatCustomFieldValue(value, definition?.type)}
                </a>
              ) : (
                <span className="text-foreground break-words">
                  <CustomFieldDisplayValue definition={definition} value={value} />
                </span>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

export function CustomFieldsEditor({
  value,
  onChange,
  entityType,
}: {
  value: Record<string, unknown>;
  onChange: (value: Record<string, unknown>) => void;
  entityType?: CustomFieldEntityType;
}) {
  const definitionsQuery = useCustomFieldDefinitions(entityType, Boolean(entityType));
  const definitions = definitionsQuery.data ?? [];

  const updateConfiguredField = (definition: CustomFieldDefinition, rawValue: unknown) => {
    const next = { ...value };
    const normalizedValue = normalizeConfiguredFieldValue(rawValue, definition);
    if (normalizedValue === undefined) {
      delete next[definition.key];
    } else {
      next[definition.key] = normalizedValue;
    }
    onChange(next);
  };

  if (definitionsQuery.isLoading) {
    return <div className="text-sm text-muted">Loading custom fields...</div>;
  }

  if (definitions.length === 0) {
    return <div className="text-sm text-muted">No custom fields are configured for this entity yet.</div>;
  }

  return (
    <div className="space-y-3">
      {definitions.map((definition) => {
        const currentValue = Object.prototype.hasOwnProperty.call(value, definition.key) ? value[definition.key] : undefined;
        return (
          <div key={definition.key} className="space-y-1">
            <label className="block text-xs font-medium text-secondary">
              {definition.label || definition.key}
              <span className="ml-2 text-[11px] font-normal text-muted">{definition.key}</span>
            </label>
            <ConfiguredFieldInput
              definition={definition}
              value={currentValue}
              onChange={(nextValue) => updateConfiguredField(definition, nextValue)}
            />
          </div>
        );
      })}
    </div>
  );
}

function ConfiguredFieldInput({
  definition,
  value,
  onChange,
}: {
  definition: CustomFieldDefinition;
  value: unknown;
  onChange: (value: unknown) => void;
}) {
  if (isEntityReferenceType(definition.type)) {
    const ids = parseEntityReferenceIds(value);
    if (definition.isMultiValue) {
      return (
        <EntityReferenceMultiSelector
          entityType={definition.type}
          values={ids}
          onChange={(nextIds) => onChange(nextIds)}
        />
      );
    }

    return (
      <EntityReferenceSelector
        entityType={definition.type}
        value={ids[0]}
        onChange={(nextId) => onChange(nextId)}
      />
    );
  }

  if (definition.isMultiValue && definition.type === "enum" && definition.options.length > 0) {
    const selectedOptions = Array.isArray(value)
      ? value.map((entry) => String(entry)).filter(Boolean)
      : typeof value === "string" && value.trim() !== ""
        ? [value.trim()]
        : [];

    return (
      <div className="flex flex-wrap gap-2 rounded border border-border bg-surface px-3 py-2">
        {definition.options.map((option) => {
          const selected = selectedOptions.includes(option);
          return (
            <label key={option} className={`inline-flex cursor-pointer items-center gap-2 rounded-full border px-3 py-1 text-sm ${selected ? "border-accent bg-accent/15 text-foreground" : "border-border text-secondary"}`}>
              <input
                type="checkbox"
                checked={selected}
                onChange={(event) => {
                  const nextValues = event.target.checked
                    ? [...selectedOptions, option]
                    : selectedOptions.filter((entry) => entry !== option);
                  onChange(nextValues);
                }}
                className="h-4 w-4 rounded border-border bg-input text-accent focus:ring-accent"
              />
              <span>{option}</span>
            </label>
          );
        })}
      </div>
    );
  }

  if (definition.isMultiValue && definition.type === "boolean") {
    const selectedValues = Array.isArray(value)
      ? value.filter((entry): entry is boolean => typeof entry === "boolean")
      : typeof value === "boolean"
        ? [value]
        : [];

    return (
      <div className="flex gap-2 rounded border border-border bg-surface px-3 py-2">
        {[true, false].map((option) => {
          const selected = selectedValues.includes(option);
          return (
            <button
              key={String(option)}
              type="button"
              onClick={() => onChange(selected ? selectedValues.filter((entry) => entry !== option) : [...selectedValues, option])}
              className={`rounded-full border px-3 py-1 text-sm ${selected ? "border-accent bg-accent/15 text-foreground" : "border-border text-secondary hover:border-accent/60 hover:text-foreground"}`}
            >
              {option ? "True" : "False"}
            </button>
          );
        })}
      </div>
    );
  }

  if (definition.type === "boolean") {
    return (
      <select
        value={serializeBooleanValue(value)}
        onChange={(event) => onChange(event.target.value)}
        className="w-full rounded border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      >
        <option value="">Unset</option>
        <option value="true">True</option>
        <option value="false">False</option>
      </select>
    );
  }

  if (definition.type === "enum" && definition.options.length > 0) {
    return (
      <select
        value={serializeScalarValue(value)}
        onChange={(event) => onChange(event.target.value)}
        className="w-full rounded border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      >
        <option value="">Unset</option>
        {definition.options.map((option) => (
          <option key={option} value={option}>{option}</option>
        ))}
      </select>
    );
  }

  if (definition.isMultiValue) {
    return (
      <textarea
        value={serializeMultiValue(value)}
        onChange={(event) => onChange(event.target.value)}
        rows={3}
        className="w-full rounded border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      />
    );
  }

  const inputType: Partial<Record<CustomFieldType, string>> = {
    text: "text",
    longText: "text",
    number: "number",
    boolean: "text",
    date: "text",
    timestamp: "text",
    url: "url",
    enum: "text",
    duration: "number",
    percent: "number",
  };

  const Input = definition.type === "date" || definition.type === "timestamp" ? IsoDateInput : "input";
  return (
    <Input
      {...(definition.type === "timestamp" ? { pickerType: "datetime-local" as const } : {})}
      type={inputType[definition.type] ?? "text"}
      value={serializeScalarValue(value)}
      onChange={(event) => onChange(event.target.value)}
      className="w-full rounded border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
    />
  );
}

function getDisplayEntries(
  customFields: Record<string, unknown> | undefined,
  definitions: CustomFieldDefinition[],
) {
  if (!customFields) {
    return [];
  }

  const definitionMap = new Map(definitions.map((definition) => [definition.key, definition]));
  const orderedKeys = definitions.map((definition) => definition.key);
  const extraKeys = Object.keys(customFields)
    .filter((key) => !definitionMap.has(key))
    .sort((left, right) => left.localeCompare(right));

  return [...orderedKeys, ...extraKeys]
    .filter((key) => Object.prototype.hasOwnProperty.call(customFields, key))
    .map((key) => ({ key, definition: definitionMap.get(key), value: customFields[key] }))
    .filter((entry) => entry.value !== undefined && entry.value !== null && !(typeof entry.value === "string" && entry.value.trim() === ""));
}

function CustomFieldDisplayValue({ definition, value }: { definition: CustomFieldDefinition | undefined; value: unknown }) {
  if (definition && isEntityReferenceType(definition.type)) {
    return <EntityReferenceValue entityType={definition.type} value={value} />;
  }

  return <>{formatCustomFieldValue(value, definition?.type)}</>;
}

function formatCustomFieldValue(value: unknown, type: CustomFieldType | undefined): string {
  if (value == null) {
    return "";
  }

  if (Array.isArray(value)) {
    return value.map((entry) => formatCustomFieldValue(entry, undefined)).filter(Boolean).join(", ");
  }

  switch (type) {
    case "boolean":
      return value === true ? "True" : value === false ? "False" : String(value);
    case "number": {
      const numericValue = typeof value === "number" ? value : Number(value);
      return Number.isFinite(numericValue) ? new Intl.NumberFormat().format(numericValue) : String(value);
    }
    case "date":
      return formatDateValue(value);
    default:
      return typeof value === "object" ? JSON.stringify(value) : String(value);
  }
}

function formatDateValue(value: unknown): string {
  if (typeof value !== "string" || !value) {
    return String(value ?? "");
  }

  const formatted = formatDate(value);
  return formatted === "Invalid Date" ? value : formatted;
}

function normalizeDefinedFieldValue(value: string, type: CustomFieldType): unknown {
  const values = value.split(/\r?\n/).map((entry) => entry.trim()).filter(Boolean);
  if (values.length > 1) {
    return values.map((entry) => normalizeDefinedFieldValue(entry, type)).filter((entry) => entry !== undefined);
  }

  switch (type) {
    case "boolean":
      if (value === "") return undefined;
      return value === "true";
    case "number": {
      if (value.trim() === "") return undefined;
      const numericValue = Number(value);
      return Number.isFinite(numericValue) ? numericValue : undefined;
    }
    case "duration":
    case "percent": {
      if (value.trim() === "") return undefined;
      const numericValue = Number(value);
      return Number.isFinite(numericValue) ? numericValue : undefined;
    }
    case "tag":
    case "performer":
    case "studio":
    case "video":
    case "gallery":
    case "image":
    case "group": {
      if (value.trim() === "") return undefined;
      const numericValue = Number(value);
      return Number.isInteger(numericValue) ? numericValue : undefined;
    }
    case "date":
    case "timestamp":
    case "enum":
    case "url": {
      const trimmedValue = value.trim();
      return trimmedValue === "" ? undefined : trimmedValue;
    }
    case "longText":
    case "text":
    default:
      return value.trim() === "" ? undefined : value;
  }
}

function serializeBooleanValue(value: unknown) {
  if (value === true) return "true";
  if (value === false) return "false";
  return "";
}

function serializeScalarValue(value: unknown) {
  if (value == null) return "";
  return typeof value === "string" ? value : String(value);
}

function serializeMultiValue(value: unknown) {
  if (Array.isArray(value)) {
    return value.map((entry) => serializeScalarValue(entry)).join("\n");
  }

  return serializeScalarValue(value);
}

function normalizeReferenceFieldValue(value: unknown, isMultiValue: boolean): unknown {
  const ids = parseEntityReferenceIds(value);
  if (isMultiValue) {
    return ids.length > 0 ? ids : undefined;
  }

  return ids[0];
}

function normalizeConfiguredFieldValue(rawValue: unknown, definition: CustomFieldDefinition): unknown {
  if (isEntityReferenceType(definition.type)) {
    return normalizeReferenceFieldValue(rawValue, definition.isMultiValue ?? false);
  }

  if (definition.isMultiValue && definition.type === "enum") {
    const values = (Array.isArray(rawValue) ? rawValue : [rawValue])
      .map((entry) => String(entry ?? "").trim())
      .filter(Boolean)
      .filter((entry, index, items) => items.indexOf(entry) === index);
    return values.length > 0 ? values : undefined;
  }

  if (definition.isMultiValue && definition.type === "boolean") {
    const values = (Array.isArray(rawValue) ? rawValue : [rawValue])
      .filter((entry): entry is boolean => typeof entry === "boolean");
    return values.length > 0 ? values : undefined;
  }

  return normalizeDefinedFieldValue(String(rawValue ?? ""), definition.type);
}
