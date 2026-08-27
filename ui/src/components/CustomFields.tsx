import { useCallback, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { Braces, ChevronDown, ChevronRight, X } from "lucide-react";
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
            <div key={key} className={`flex flex-col ${definition?.type === "json" || definition?.type === "longText" ? "col-span-2" : ""}`}>
              <span className="text-muted text-xs">{label}</span>
              {urlValue ? (
                <a href={urlValue} target="_blank" rel="noreferrer" className="text-accent hover:underline break-all">
                  {formatCustomFieldValue(value, definition?.type)}
                </a>
              ) : (
                <div className={`text-foreground break-words ${definition?.type === "longText" ? "whitespace-pre-wrap" : ""}`}>
                  <CustomFieldDisplayValue definition={definition} value={value} />
                </div>
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
  onValidityChange,
  entityType,
}: {
  value: Record<string, unknown>;
  onChange: (value: Record<string, unknown>) => void;
  onValidityChange?: (isValid: boolean) => void;
  entityType?: CustomFieldEntityType;
}) {
  const definitionsQuery = useCustomFieldDefinitions(entityType, Boolean(entityType));
  const definitions = definitionsQuery.data ?? [];
  const [invalidJsonKeys, setInvalidJsonKeys] = useState<Set<string>>(() => new Set());
  const jsonDefinitionKeys = useMemo(
    () => new Set(definitions.filter((definition) => definition.type === "json").map((definition) => definition.key)),
    [definitions],
  );

  const updateJsonValidity = useCallback((key: string, isValid: boolean) => {
    setInvalidJsonKeys((current) => {
      const next = new Set(current);
      if (isValid) {
        next.delete(key);
      } else {
        next.add(key);
      }
      return next.size === current.size && [...next].every((candidate) => current.has(candidate)) ? current : next;
    });
  }, []);

  useEffect(() => {
    setInvalidJsonKeys((current) => {
      const next = new Set([...current].filter((key) => jsonDefinitionKeys.has(key)));
      return next.size === current.size ? current : next;
    });
  }, [jsonDefinitionKeys]);

  useEffect(() => {
    onValidityChange?.(invalidJsonKeys.size === 0);
  }, [invalidJsonKeys, onValidityChange]);

  useEffect(() => () => onValidityChange?.(true), [onValidityChange]);

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
              onJsonValidityChange={updateJsonValidity}
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
  onJsonValidityChange,
}: {
  definition: CustomFieldDefinition;
  value: unknown;
  onChange: (value: unknown) => void;
  onJsonValidityChange: (key: string, isValid: boolean) => void;
}) {
  if (definition.type === "json") {
    return <JsonFieldInput definition={definition} value={value} onChange={onChange} onValidityChange={onJsonValidityChange} />;
  }

  if (definition.type === "longText") {
    return (
      <textarea
        aria-label={definition.label || definition.key}
        value={serializeScalarValue(value)}
        onChange={(event) => onChange(event.target.value)}
        rows={6}
        className="w-full rounded border border-border bg-surface px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
      />
    );
  }

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

function JsonFieldInput({
  definition,
  value,
  onChange,
  onValidityChange,
}: {
  definition: CustomFieldDefinition;
  value: unknown;
  onChange: (value: unknown) => void;
  onValidityChange: (key: string, isValid: boolean) => void;
}) {
  const serializedValue = useMemo(() => serializeJsonValue(value), [value]);
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState(serializedValue);
  const [error, setError] = useState<string | null>(null);
  const label = definition.label || definition.key;

  useEffect(() => {
    if (open) return;
    setDraft(serializedValue);
    setError(null);
    onValidityChange(definition.key, true);
  }, [definition.key, onValidityChange, open, serializedValue]);

  useEffect(() => () => onValidityChange(definition.key, true), [definition.key, onValidityChange]);

  const openEditor = () => {
    setDraft(serializedValue);
    setError(null);
    onValidityChange(definition.key, true);
    setOpen(true);
  };

  const cancelEditing = useCallback(() => {
    setOpen(false);
    setDraft(serializedValue);
    setError(null);
    onValidityChange(definition.key, true);
  }, [definition.key, onValidityChange, serializedValue]);

  const updateDraft = (nextDraft: string) => {
    setDraft(nextDraft);
    const nextError = getJsonDraftError(nextDraft);
    setError(nextError);
    onValidityChange(definition.key, nextError == null);
  };

  const formatDraft = () => {
    if (draft.trim() === "") return;
    try {
      const parsed = JSON.parse(draft) as unknown;
      const numberError = getJsonNumberError(parsed);
      if (numberError) {
        setError(numberError);
        onValidityChange(definition.key, false);
        return;
      }
      setDraft(serializeJsonValue(parsed));
      setError(null);
      onValidityChange(definition.key, true);
    } catch {
      setError("Enter valid JSON before saving this value.");
      onValidityChange(definition.key, false);
    }
  };

  const applyDraft = () => {
    if (draft.trim() === "") {
      onChange(undefined);
      setOpen(false);
      setError(null);
      onValidityChange(definition.key, true);
      return;
    }

    try {
      const parsed = JSON.parse(draft) as unknown;
      const numberError = getJsonNumberError(parsed);
      if (numberError) {
        setError(numberError);
        onValidityChange(definition.key, false);
        return;
      }
      onChange(parsed);
      setOpen(false);
      setError(null);
      onValidityChange(definition.key, true);
    } catch {
      setError("Enter valid JSON before saving this value.");
      onValidityChange(definition.key, false);
    }
  };

  return (
    <>
      <JsonContentPreview
        label={label}
        value={value}
        editable
        onOpen={openEditor}
      />
      {open ? (
        <JsonFieldDialog
          label={label}
          draft={draft}
          editable
          error={error}
          onDraftChange={updateDraft}
          onFormat={formatDraft}
          onApply={applyDraft}
          onClose={cancelEditing}
        />
      ) : null}
    </>
  );
}

function JsonContentPreview({
  label,
  value,
  editable,
  onOpen,
}: {
  label: string;
  value: unknown;
  editable: boolean;
  onOpen: () => void;
}) {
  const hasContent = value !== undefined && value !== null;
  const action = editable ? (hasContent ? "Edit" : "Add") : "View";

  return (
    <button
      type="button"
      aria-label={`${action} ${label} JSON`}
      onClick={onOpen}
      className="flex w-full items-center justify-between gap-3 rounded-lg border border-border bg-background px-3 py-2 text-left transition hover:border-accent/70 hover:bg-card-hover focus:outline-none focus:ring-2 focus:ring-accent/60"
    >
      <span className="flex min-w-0 items-center gap-2">
        <Braces className="h-4 w-4 shrink-0 text-accent" />
        <span className="truncate text-sm text-secondary">{getJsonContentSummary(value)}</span>
      </span>
      <span className="shrink-0 text-xs font-medium text-accent">{action} JSON</span>
    </button>
  );
}

function JsonFieldDialog({
  label,
  draft,
  editable,
  error = null,
  onDraftChange,
  onFormat,
  onApply,
  onClose,
}: {
  label: string;
  draft: string;
  editable: boolean;
  error?: string | null;
  onDraftChange?: (value: string) => void;
  onFormat?: () => void;
  onApply?: () => void;
  onClose: () => void;
}) {
  const titleId = useId();
  const errorId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const editorHighlightRef = useRef<HTMLPreElement>(null);
  const title = label;

  useEffect(() => {
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    const focusTimer = window.setTimeout(() => {
      dialogRef.current?.querySelector<HTMLElement>("[data-json-dialog-initial-focus]")?.focus();
    }, 0);
    return () => {
      window.clearTimeout(focusTimer);
      document.body.style.overflow = previousOverflow;
      previousFocus?.focus();
    };
  }, []);

  const handleKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    event.stopPropagation();
    if (event.key === "Escape") {
      event.preventDefault();
      onClose();
      return;
    }

    if (event.key !== "Tab" || !dialogRef.current) return;
    const focusable = Array.from(dialogRef.current.querySelectorAll<HTMLElement>(
      "button:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])",
    ));
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  };

  return createPortal(
    <div
      className="fixed inset-0 z-[120] flex items-center justify-center bg-black/70 p-3 sm:p-6"
      onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onKeyDown={handleKeyDown}
        className="flex h-[90vh] w-full max-w-4xl flex-col overflow-hidden rounded-xl border border-border bg-surface shadow-2xl"
      >
        <div className="flex items-center justify-between gap-4 border-b border-border px-4 py-3 sm:px-5">
          <div className="flex min-w-0 items-center gap-2">
            <Braces className="h-5 w-5 shrink-0 text-accent" />
            <h2 id={titleId} className="truncate text-lg font-semibold text-foreground">{title}</h2>
          </div>
          <button
            type="button"
            aria-label={editable ? "Close JSON editor" : "Close JSON viewer"}
            onClick={onClose}
            data-json-dialog-initial-focus={!editable ? true : undefined}
            className="rounded-lg p-1.5 text-muted transition hover:bg-card-hover hover:text-foreground"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="min-h-0 flex-1 overflow-auto bg-background p-4 sm:p-5">
          {editable ? (
            <div className="relative h-full min-h-0 w-full">
              <pre
                ref={editorHighlightRef}
                aria-hidden="true"
                data-json-editor-highlight
                className="pointer-events-none absolute inset-0 h-full overflow-auto whitespace-pre bg-background font-mono text-sm leading-relaxed text-foreground"
              >
                <JsonHighlightedText text={draft} />
              </pre>
              <textarea
                data-json-dialog-initial-focus
                aria-label={`${label} JSON`}
                aria-invalid={Boolean(error)}
                aria-describedby={error ? errorId : undefined}
                value={draft}
                onChange={(event) => onDraftChange?.(event.target.value)}
                onScroll={(event) => {
                  if (!editorHighlightRef.current) return;
                  editorHighlightRef.current.scrollTop = event.currentTarget.scrollTop;
                  editorHighlightRef.current.scrollLeft = event.currentTarget.scrollLeft;
                }}
                spellCheck={false}
                placeholder={'{\n  "key": "value"\n}'}
                className="relative z-10 h-full min-h-0 w-full resize-none whitespace-pre overflow-auto border-0 bg-transparent p-0 font-mono text-sm leading-relaxed text-transparent caret-foreground selection:bg-accent/40 selection:text-transparent placeholder:text-muted focus:outline-none focus:ring-0"
              />
            </div>
          ) : (
            <JsonSyntaxTree label={label} value={parseJsonForDisplay(draft)} />
          )}
        </div>

        <div className="border-t border-border px-4 py-3 sm:px-5">
          {editable ? (
            <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
              <div className="min-w-0">
                {error ? (
                  <span id={errorId} role="alert" className="text-xs text-red-300">{error}</span>
                ) : (
                  <span className="text-xs text-muted">Objects, arrays, strings, booleans, and finite numbers are supported. Numbers use JavaScript precision.</span>
                )}
              </div>
              <div className="flex shrink-0 items-center justify-end gap-2">
                <button
                  type="button"
                  onClick={onFormat}
                  disabled={draft.trim() === "" || Boolean(error)}
                  aria-label={`Format ${label} JSON`}
                  className="rounded border border-border px-3 py-2 text-sm text-secondary transition hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
                >
                  Format JSON
                </button>
                <button
                  type="button"
                  onClick={onClose}
                  aria-label="Cancel JSON editing"
                  className="px-3 py-2 text-sm text-secondary transition hover:text-foreground"
                >
                  Cancel
                </button>
                <button
                  type="button"
                  onClick={onApply}
                  disabled={Boolean(error)}
                  className="rounded-md bg-accent px-4 py-2 text-sm font-medium text-white transition hover:bg-accent-hover disabled:cursor-not-allowed disabled:opacity-50"
                >
                  Apply JSON
                </button>
              </div>
            </div>
          ) : (
            <div className="flex justify-end">
              <button
                type="button"
                onClick={onClose}
                className="rounded-md bg-accent px-4 py-2 text-sm font-medium text-white transition hover:bg-accent-hover"
              >
                Close
              </button>
            </div>
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}

const DEFAULT_JSON_EXPANDED_LEVELS = 3;
const MAX_JSON_AUTO_EXPANDED_ENTRIES = 200;

function JsonSyntaxTree({ label, value }: { label: string; value: unknown }) {
  return (
    <div
      aria-label={`${label} JSON value`}
      className="bg-background font-mono text-sm leading-relaxed text-foreground"
    >
      <JsonSyntaxNode value={value} path="$" depth={0} />
    </div>
  );
}

function JsonSyntaxNode({
  value,
  path,
  depth,
  propertyName,
  trailingComma = false,
}: {
  value: unknown;
  path: string;
  depth: number;
  propertyName?: string;
  trailingComma?: boolean;
}) {
  const isContainer = value !== null && typeof value === "object";
  const entries = isContainer ? Object.entries(value) : [];
  const [expanded, setExpanded] = useState(depth < DEFAULT_JSON_EXPANDED_LEVELS && entries.length <= MAX_JSON_AUTO_EXPANDED_ENTRIES);
  const indentation = { paddingLeft: `${depth * 1.25}rem` };
  const propertyPrefix = propertyName === undefined ? null : (
    <>
      <span data-json-token="key" className="text-sky-300">{JSON.stringify(propertyName)}</span>
      <span className="text-secondary">: </span>
    </>
  );

  if (!isContainer) {
    return (
      <div className="whitespace-pre-wrap break-words" style={indentation}>
        {propertyPrefix}
        <JsonPrimitiveValue value={value} />
        {trailingComma ? <span className="text-secondary">,</span> : null}
      </div>
    );
  }

  const isArray = Array.isArray(value);
  const opening = isArray ? "[" : "{";
  const closing = isArray ? "]" : "}";
  const itemLabel = isArray ? `${entries.length} ${entries.length === 1 ? "item" : "items"}` : `${entries.length} ${entries.length === 1 ? "property" : "properties"}`;
  const pathLabel = path === "$" ? "JSON root" : path;

  if (entries.length === 0) {
    return (
      <div className="whitespace-pre-wrap break-words" style={indentation}>
        {propertyPrefix}
        <span className="text-secondary">{opening}{closing}{trailingComma ? "," : ""}</span>
      </div>
    );
  }

  return (
    <>
      <div className="flex min-w-0 items-start" style={indentation}>
        <button
          type="button"
          aria-label={`${expanded ? "Collapse" : "Expand"} ${pathLabel}`}
          aria-expanded={expanded}
          onClick={() => setExpanded((current) => !current)}
          className="-ml-5 mt-0.5 inline-flex min-w-0 items-start rounded text-left hover:bg-card-hover focus:outline-none focus:ring-1 focus:ring-accent"
        >
          {expanded ? <ChevronDown className="mr-0.5 h-4 w-4 shrink-0 text-muted" /> : <ChevronRight className="mr-0.5 h-4 w-4 shrink-0 text-muted" />}
          <span className="min-w-0 whitespace-pre-wrap break-words">
            {propertyPrefix}
            <span className="text-secondary">{expanded ? opening : `${opening}…${closing}`}</span>
            {!expanded ? <span className="ml-2 text-xs text-muted">{itemLabel}</span> : null}
            {!expanded && trailingComma ? <span className="text-secondary">,</span> : null}
          </span>
        </button>
      </div>
      {expanded ? (
        <>
          {entries.map(([key, childValue], index) => (
            <JsonSyntaxNode
              key={key}
              value={childValue}
              path={isArray ? `${path}[${key}]` : appendJsonPath(path, key)}
              depth={depth + 1}
              propertyName={isArray ? undefined : key}
              trailingComma={index < entries.length - 1}
            />
          ))}
          <div className="text-secondary" style={indentation}>{closing}{trailingComma ? "," : ""}</div>
        </>
      ) : null}
    </>
  );
}

function JsonPrimitiveValue({ value }: { value: unknown }) {
  if (value === null) return <span data-json-token="null" className="text-rose-300">null</span>;
  if (typeof value === "string") return <span data-json-token="string" className="text-emerald-300">{JSON.stringify(value)}</span>;
  if (typeof value === "number") return <span data-json-token="number" className="text-amber-300">{String(value)}</span>;
  if (typeof value === "boolean") return <span data-json-token="boolean" className="text-violet-300">{String(value)}</span>;
  return <span className="text-secondary">{JSON.stringify(value) ?? String(value)}</span>;
}

const JSON_TOKEN_PATTERN = /"(?:\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4})|[^"\\\u0000-\u001F])*"|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?|\b(?:true|false|null)\b/g;
const MAX_JSON_HIGHLIGHT_CHARACTERS = 100_000;
const MAX_JSON_HIGHLIGHT_TOKENS = 2_000;

function JsonHighlightedText({ text }: { text: string }) {
  if (text.length > MAX_JSON_HIGHLIGHT_CHARACTERS) return text;

  const parts: ReactNode[] = [];
  let cursor = 0;
  let tokenCount = 0;

  for (const match of text.matchAll(JSON_TOKEN_PATTERN)) {
    tokenCount += 1;
    if (tokenCount > MAX_JSON_HIGHLIGHT_TOKENS) return text;
    const index = match.index;
    if (index > cursor) parts.push(text.slice(cursor, index));

    const token = match[0];
    const isString = token.startsWith('"');
    const isKey = isString && /^\s*:/.test(text.slice(index + token.length));
    const kind = isKey ? "key" : isString ? "string" : token === "null" ? "null" : token === "true" || token === "false" ? "boolean" : "number";
    const color = kind === "key" ? "text-sky-300" : kind === "string" ? "text-emerald-300" : kind === "number" ? "text-amber-300" : kind === "boolean" ? "text-violet-300" : "text-rose-300";
    parts.push(<span key={index} data-json-token={kind} className={color}>{token}</span>);
    cursor = index + token.length;
  }

  if (cursor < text.length) parts.push(text.slice(cursor));
  return parts;
}

function appendJsonPath(path: string, key: string): string {
  return /^[A-Za-z_$][\w$]*$/.test(key) ? `${path}.${key}` : `${path}[${JSON.stringify(key)}]`;
}

function parseJsonForDisplay(draft: string): unknown {
  try {
    return JSON.parse(draft) as unknown;
  } catch {
    return draft;
  }
}

function getJsonDraftError(draft: string): string | null {
  if (draft.trim() === "") return null;
  try {
    return getJsonNumberError(JSON.parse(draft) as unknown);
  } catch {
    return "Enter valid JSON before saving this value.";
  }
}

function getJsonContentSummary(value: unknown): string {
  if (value === undefined || value === null) return "No JSON content";
  if (Array.isArray(value)) return `JSON array · ${value.length} ${value.length === 1 ? "item" : "items"}`;
  if (typeof value === "object") {
    const count = Object.keys(value).length;
    return `JSON object · ${count} ${count === 1 ? "property" : "properties"}`;
  }
  if (typeof value === "string") return `JSON string · ${value.length} ${value.length === 1 ? "character" : "characters"}`;
  return `JSON ${typeof value}`;
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
  if (definition?.type === "json") {
    return <JsonFieldDisplayValue definition={definition} value={value} />;
  }

  if (definition && isEntityReferenceType(definition.type)) {
    return <EntityReferenceValue entityType={definition.type} value={value} />;
  }

  return <>{formatCustomFieldValue(value, definition?.type)}</>;
}

function JsonFieldDisplayValue({ definition, value }: { definition: CustomFieldDefinition; value: unknown }) {
  const [open, setOpen] = useState(false);
  const label = definition.label || definition.key;
  const closeViewer = useCallback(() => setOpen(false), []);

  return (
    <>
      <JsonContentPreview label={label} value={value} editable={false} onOpen={() => setOpen(true)} />
      {open ? (
        <JsonFieldDialog
          label={label}
          draft={serializeJsonValue(value)}
          editable={false}
          onClose={closeViewer}
        />
      ) : null}
    </>
  );
}

function serializeJsonValue(value: unknown): string {
  if (value === undefined) return "";

  try {
    return JSON.stringify(value, null, 2) ?? "";
  } catch {
    return String(value ?? "");
  }
}

function getJsonNumberError(value: unknown): string | null {
  if (typeof value === "number") {
    if (!Number.isFinite(value)) {
      return "JSON numbers must be finite.";
    }
    if (Number.isInteger(value) && !Number.isSafeInteger(value)) {
      return "JSON integers must be between -9,007,199,254,740,991 and 9,007,199,254,740,991.";
    }
    return null;
  }

  if (Array.isArray(value)) {
    for (const entry of value) {
      const error = getJsonNumberError(entry);
      if (error) return error;
    }
    return null;
  }

  if (value && typeof value === "object") {
    for (const entry of Object.values(value as Record<string, unknown>)) {
      const error = getJsonNumberError(entry);
      if (error) return error;
    }
  }

  return null;
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
  if (type === "longText") {
    return value.trim() === "" ? undefined : value;
  }

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
  if (definition.type === "json") {
    return rawValue === null ? undefined : rawValue;
  }

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
