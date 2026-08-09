import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { X } from "lucide-react";
import { InteractiveRating } from "./Rating";
import { IsoDateInput } from "./IsoDateInput";
import type { BulkUpdateMode } from "../api/types";
import { tagGroups } from "../api/client";
import { StudioSelector } from "./StudioSelector";
import { EntityReferenceMultiSelector, type EntityReferenceType } from "./EntityReferenceSelector";

// ===== Generic Bulk Edit Dialog =====

export interface BulkEditField {
  key: string;
  label: string;
  type: "rating" | "number" | "bool" | "string" | "date" | "select" | "multiId";
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
}

export function BulkEditDialog({ open, onClose, title, selectedCount, fields, onApply, isPending }: BulkEditDialogProps) {
  const [values, setValues] = useState<Record<string, unknown>>({});
  const [enabledFields, setEnabledFields] = useState<Set<string>>(new Set());

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

  const handleApply = () => {
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
    if (clearFields.length > 0) {
      result.clearFields = clearFields;
    }
    onApply(result);
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-surface border border-border rounded-lg shadow-xl w-full max-w-md max-h-[80vh] flex flex-col" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between px-4 py-3 border-b border-border">
          <h2 className="text-sm font-semibold text-foreground">
            {title} <span className="text-muted font-normal">({selectedCount} selected)</span>
          </h2>
          <button onClick={onClose} className="p-1 hover:bg-card rounded text-muted hover:text-foreground">
            <X className="w-4 h-4" />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto px-4 py-3 space-y-3">
          {fields.map((field) => (
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

        <div className="flex items-center justify-end gap-2 px-4 py-3 border-t border-border">
          <button onClick={onClose} className="px-3 py-1 rounded text-xs text-secondary hover:text-foreground border border-border">
            Cancel
          </button>
          <button
            onClick={handleApply}
            disabled={isPending || enabledFields.size === 0}
            className="px-4 py-1 rounded text-xs font-medium bg-accent hover:bg-accent-hover text-white disabled:opacity-50"
          >
            {isPending ? "Applying..." : "Apply"}
          </button>
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
  return (
    <div>
      <label className="flex items-center gap-2 cursor-pointer">
        <input
          type="checkbox"
          checked={enabled}
          onChange={onToggle}
          className="w-3.5 h-3.5 rounded border-border accent-accent"
        />
        <span className={`text-xs font-medium ${enabled ? "text-foreground" : "text-muted"}`}>
          {field.label}
        </span>
      </label>
      {enabled && (
        <div className="ml-6 mt-1">
          {field.type === "rating" && (
            <div className="rounded border border-border bg-input px-3 py-2">
              <InteractiveRating value={value as number | undefined} onChange={(nextValue) => onValueChange(nextValue || undefined)} />
            </div>
          )}
          {field.type === "number" && (
            <input
              type="number"
              value={(value as number) ?? ""}
              onChange={(e) => onValueChange(e.target.value ? Number(e.target.value) : undefined)}
              className="w-24 bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
            />
          )}
          {field.type === "bool" && (
            <div className="flex gap-2">
              <button
                onClick={() => onValueChange(true)}
                className={`px-3 py-1 rounded text-xs border ${value === true ? "bg-accent text-white border-accent" : "border-border text-secondary"}`}
              >
                True
              </button>
              <button
                onClick={() => onValueChange(false)}
                className={`px-3 py-1 rounded text-xs border ${value === false ? "bg-accent text-white border-accent" : "border-border text-secondary"}`}
              >
                False
              </button>
            </div>
          )}
          {field.type === "string" && (
            <input
              type="text"
              value={(value as string) ?? ""}
              onChange={(e) => onValueChange(e.target.value)}
              className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
            />
          )}
          {field.type === "date" && (
            <IsoDateInput
              value={(value as string) ?? ""}
              onChange={(e) => onValueChange(e.target.value)}
              className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
            />
          )}
          {field.type === "select" && field.entityType === "studios" && (
            <div className="space-y-2">
              <StudioSelector value={value as number | undefined} onChange={(nextValue) => onValueChange(nextValue)} />
              {field.nullable && (
                <button
                  type="button"
                  onClick={() => onValueChange(undefined)}
                  className={`inline-flex items-center gap-1 rounded border px-2 py-1 text-xs ${value == null ? "border-accent bg-accent/10 text-accent" : "border-border text-secondary hover:text-foreground"}`}
                >
                  <X className="h-3 w-3" />
                  Clear value
                </button>
              )}
            </div>
          )}
          {field.type === "select" && field.entityType === "tagGroups" && (
            <TagGroupBulkSelect value={value as number | undefined} nullable={field.nullable} onValueChange={onValueChange} />
          )}
          {field.type === "select" && field.entityType !== "studios" && field.entityType !== "tagGroups" && (
            <select
              value={String(value ?? "")}
              onChange={(e) => {
                if (!e.target.value) {
                  onValueChange(undefined);
                  return;
                }

                const selectedOption = field.options?.find((option) => String(option.value) === e.target.value);
                onValueChange(selectedOption?.value ?? e.target.value);
              }}
              className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
            >
              <option value="">Select...</option>
              {field.options?.map((o) => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </select>
          )}
          {field.type === "multiId" && isMultiIdEntityType(field.entityType) && (
            <MultiIdBulkEditor
              entityType={field.entityType}
              value={(value as number[]) ?? []}
              mode={mode}
              onValueChange={onValueChange}
              onModeChange={onModeChange}
            />
          )}
        </div>
      )}
    </div>
  );
}

function TagGroupBulkSelect({ value, nullable, onValueChange }: { value?: number; nullable?: boolean; onValueChange: (v: unknown) => void }) {
  const { data: groups = [], isLoading } = useQuery({ queryKey: ["tag-groups"], queryFn: tagGroups.list });

  return (
    <div className="space-y-2">
      <select
        value={String(value ?? "")}
        onChange={(event) => onValueChange(event.target.value ? Number(event.target.value) : undefined)}
        className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
      >
        <option value="">{isLoading ? "Loading tag groups..." : "Select tag group..."}</option>
        {groups.map((group) => (
          <option key={group.id} value={group.id}>{group.name}</option>
        ))}
      </select>
      {nullable && (
        <button
          type="button"
          onClick={() => onValueChange(undefined)}
          className={`inline-flex items-center gap-1 rounded border px-2 py-1 text-xs ${value == null ? "border-accent bg-accent/10 text-accent" : "border-border text-secondary hover:text-foreground"}`}
        >
          <X className="h-3 w-3" />
          Clear value
        </button>
      )}
    </div>
  );
}

function MultiIdBulkEditor({
  entityType,
  value,
  mode,
  onValueChange,
  onModeChange,
}: {
  entityType: "tags" | "performers" | "studios" | "groups" | "galleries";
  value: number[];
  mode: BulkUpdateMode;
  onValueChange: (v: unknown) => void;
  onModeChange: (m: BulkUpdateMode) => void;
}) {
  return (
    <div className="space-y-2">
      {/* Mode selector */}
      <div className="flex gap-1">
        {(["SET", "ADD", "REMOVE"] as BulkUpdateMode[]).map((m) => (
          <button
            key={m}
            onClick={() => onModeChange(m)}
            className={`px-2 py-0.5 rounded text-[10px] border ${
              m === mode ? "bg-accent text-white border-accent" : "border-border text-secondary"
            }`}
          >
            {BULK_MODE_LABELS[m]}
          </button>
        ))}
      </div>

      <EntityReferenceMultiSelector
        entityType={toReferenceEntityType(entityType)}
        values={value}
        onChange={onValueChange as (values: number[]) => void}
        placeholder={`Search ${entityType}...`}
        inputClassName="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
        resultsMaxHeight={128}
      />
    </div>
  );
}

function toReferenceEntityType(entityType: "tags" | "performers" | "studios" | "groups" | "galleries"): EntityReferenceType {
  switch (entityType) {
    case "tags": return "tag";
    case "performers": return "performer";
    case "studios": return "studio";
    case "groups": return "group";
    case "galleries": return "gallery";
  }
}

function isMultiIdEntityType(entityType: BulkEditField["entityType"]): entityType is "tags" | "performers" | "studios" | "groups" | "galleries" {
  return entityType === "tags" || entityType === "performers" || entityType === "studios" || entityType === "groups" || entityType === "galleries";
}

const BULK_MODE_LABELS: Record<BulkUpdateMode, string> = {
  SET: "Overwrite",
  ADD: "Add",
  REMOVE: "Remove",
};

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
    options: ["Female", "Male", "TransgenderFemale", "TransgenderMale", "Intersex", "NonBinary"].map((value) => ({ value, label: value.replace(/([a-z])([A-Z])/g, "$1 $2") })),
  },
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
