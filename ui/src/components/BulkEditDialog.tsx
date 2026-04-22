import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { X, Plus, Minus } from "lucide-react";
import { InteractiveRating } from "./Rating";
import { tags as tagsApi, performers as performersApi, studios as studiosApi, groups as groupsApi } from "../api/client";
import type { BulkUpdateMode } from "../api/types";

// ===== Generic Bulk Edit Dialog =====

interface BulkEditField {
  key: string;
  label: string;
  type: "rating" | "number" | "bool" | "string" | "date" | "select" | "multiId";
  entityType?: "tags" | "performers" | "studios" | "groups";
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
    for (const f of fields) {
      if (enabledFields.has(f.key)) {
        result[f.key] = serializeBulkFieldValue(f, values[f.key]);
        if (f.type === "multiId") {
          result[getModeKey(f)] = values[getModeKey(f)] ?? "ADD";
        }
      }
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
            <input
              type="date"
              value={(value as string) ?? ""}
              onChange={(e) => onValueChange(e.target.value)}
              className="bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent"
            />
          )}
          {field.type === "select" && field.entityType === "studios" && (
            <StudioBulkSelect value={value as number | undefined} onValueChange={onValueChange} />
          )}
          {field.type === "select" && field.entityType !== "studios" && (
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
          {field.type === "multiId" && field.entityType && (
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

function StudioBulkSelect({
  value,
  onValueChange,
}: {
  value: number | undefined;
  onValueChange: (value: unknown) => void;
}) {
  const [searchText, setSearchText] = useState("");

  const { data: searchResults, isLoading } = useQuery({
    queryKey: ["bulk-edit", "studios", searchText],
    queryFn: async () => {
      const response = await studiosApi.find({
        q: searchText || undefined,
        perPage: 25,
        sort: "name",
        direction: "asc",
      });

      return response.items;
    },
    staleTime: 60000,
  });

  const selectedResult = searchResults?.find((studio) => studio.id === value);

  const { data: selectedStudio } = useQuery({
    queryKey: ["bulk-edit", "studio", value],
    queryFn: async () => studiosApi.get(value as number),
    enabled: typeof value === "number" && !selectedResult,
    staleTime: 60000,
  });

  const visibleResults = (searchResults ?? []).filter((studio) => studio.id !== value);
  const selectedLabel = selectedResult?.name ?? selectedStudio?.name;

  return (
    <div className="space-y-2">
      {selectedLabel && (
        <div className="flex flex-wrap gap-1">
          <span className="inline-flex items-center gap-1 rounded border border-border bg-card px-2 py-0.5 text-[10px] text-foreground">
            {selectedLabel}
            <button onClick={() => onValueChange(undefined)} className="hover:text-red-400" aria-label="Clear selected studio">
              <X className="h-2.5 w-2.5" />
            </button>
          </span>
        </div>
      )}

      <input
        type="text"
        value={searchText}
        onChange={(e) => setSearchText(e.target.value)}
        placeholder="Search studios..."
        className="w-full rounded border border-border bg-input px-2 py-1 text-xs text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
      />

      <div className="max-h-32 overflow-y-auto rounded border border-border bg-input">
        {isLoading ? (
          <div className="px-2 py-1 text-xs text-muted">Loading...</div>
        ) : visibleResults.length === 0 ? (
          <div className="px-2 py-1 text-xs text-muted">No studios found</div>
        ) : (
          visibleResults.map((studio) => (
            <button
              key={studio.id}
              onClick={() => {
                onValueChange(studio.id);
                setSearchText("");
              }}
              className="flex w-full items-center gap-1 px-2 py-1 text-left text-xs text-foreground hover:bg-card"
            >
              <Plus className="h-3 w-3" />
              {studio.name}
            </button>
          ))
        )}
      </div>
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
  entityType: "tags" | "performers" | "studios" | "groups";
  value: number[];
  mode: BulkUpdateMode;
  onValueChange: (v: unknown) => void;
  onModeChange: (m: BulkUpdateMode) => void;
}) {
  const [searchText, setSearchText] = useState("");

  const { data: entities } = useQuery({
    queryKey: [entityType, "all"],
    queryFn: async () => {
      switch (entityType) {
        case "tags": return (await tagsApi.find({ perPage: 1000, sort: "name", direction: "asc" })).items;
        case "performers": return (await performersApi.find({ perPage: 1000, sort: "name", direction: "asc" })).items;
        case "studios": return (await studiosApi.find({ perPage: 1000, sort: "name", direction: "asc" })).items;
        case "groups": return (await groupsApi.find({ perPage: 1000, sort: "name", direction: "asc" })).items;
        default: return [];
      }
    },
    staleTime: 60000,
  });

  const getName = (e: any) => e.name || e.title || `#${e.id}`;

  const filteredEntities = useMemo(() => {
    if (!entities) return [];
    const q = searchText.toLowerCase();
    return q ? entities.filter((e: any) => getName(e).toLowerCase().includes(q)) : entities;
  }, [entities, searchText]);

  const toggleId = (id: number) => {
    const next = value.includes(id) ? value.filter((i) => i !== id) : [...value, id];
    onValueChange(next);
  };

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

      {/* Selected */}
      {value.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {value.map((id) => {
            const entity = entities?.find((e: any) => e.id === id);
            return (
              <span key={id} className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[10px] bg-card text-foreground border border-border">
                {entity ? getName(entity) : `#${id}`}
                <button onClick={() => toggleId(id)} className="hover:text-red-400">
                  <X className="w-2.5 h-2.5" />
                </button>
              </span>
            );
          })}
        </div>
      )}

      {/* Search */}
      <input
        type="text"
        value={searchText}
        onChange={(e) => setSearchText(e.target.value)}
        placeholder={`Search ${entityType}...`}
        className="w-full bg-input border border-border rounded px-2 py-1 text-xs text-foreground focus:outline-none focus:border-accent placeholder:text-muted"
      />
      <div className="max-h-32 overflow-y-auto border border-border rounded bg-input">
        {filteredEntities.slice(0, 50).map((entity: any) => {
          const isSelected = value.includes(entity.id);
          return (
            <button
              key={entity.id}
              onClick={() => toggleId(entity.id)}
              className={`w-full text-left px-2 py-1 text-xs hover:bg-card flex items-center gap-1 ${isSelected ? "text-accent" : "text-foreground"}`}
            >
              {isSelected ? <Minus className="w-3 h-3" /> : <Plus className="w-3 h-3" />}
              {getName(entity)}
            </button>
          );
        })}
      </div>
    </div>
  );
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

export const SCENE_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "organized", label: "Organized", type: "bool" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "date", label: "Date", type: "date" },
  { key: "code", label: "Code", type: "string" },
  { key: "director", label: "Director", type: "string" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
  { key: "performerIds", label: "Performers", type: "multiId", entityType: "performers", modeKey: "performerMode" },
  {
    key: "groupIds",
    label: "Groups",
    type: "multiId",
    entityType: "groups",
    modeKey: "groupMode",
    serializeValue: (value) => ((value as number[] | undefined) ?? []).map((groupId) => ({ groupId, sceneIndex: 0 })),
  },
];

export const PERFORMER_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "favorite", label: "Favorite", type: "bool" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
];

export const GALLERY_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "organized", label: "Organized", type: "bool" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
  { key: "performerIds", label: "Performers", type: "multiId", entityType: "performers", modeKey: "performerMode" },
];

export const IMAGE_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "organized", label: "Organized", type: "bool" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
  { key: "performerIds", label: "Performers", type: "multiId", entityType: "performers", modeKey: "performerMode" },
];

export const TAG_BULK_FIELDS: BulkEditField[] = [
  { key: "favorite", label: "Favorite", type: "bool" },
  { key: "ignoreAutoTag", label: "Ignore Auto-Tag", type: "bool" },
];

export const STUDIO_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "favorite", label: "Favorite", type: "bool" },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
];

export const GROUP_BULK_FIELDS: BulkEditField[] = [
  { key: "rating", label: "Rating", type: "rating" },
  { key: "studioId", label: "Studio", type: "select", entityType: "studios", nullable: true },
  { key: "tagIds", label: "Tags", type: "multiId", entityType: "tags", modeKey: "tagMode" },
];
