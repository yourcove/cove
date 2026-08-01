import { useQuery } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useMemo } from "react";
import { groups, performers, studios, tagGroups, tags } from "../api/client";
import type { CriterionDefinition, FilterDialogCustomSection } from "./FilterDialog";

const CHIP_MODIFIER_LABELS: Record<string, string> = {
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
};

function formatChipScalar(value: unknown): string {
  if (typeof value === "boolean") return value ? "Yes" : "No";
  if (value == null) return "";
  if (typeof value === "object") {
    const candidate = value as { label?: string; name?: string; title?: string; value?: string | number };
    return candidate.label ?? candidate.name ?? candidate.title ?? String(candidate.value ?? "");
  }
  return String(value);
}

function formatChipEntityId(value: unknown, nameMap?: Map<number, string>): string {
  if (typeof value === "number") return nameMap?.get(value) ?? "Unavailable item";
  if (value && typeof value === "object") {
    const candidate = value as { id?: number | string; label?: string; name?: string; title?: string };
    if (candidate.label ?? candidate.name ?? candidate.title) return (candidate.label ?? candidate.name ?? candidate.title)!;
    if (candidate.id != null && typeof candidate.id === "number") return nameMap?.get(candidate.id) ?? "Unavailable item";
    return candidate.id != null ? "Unavailable item" : "";
  }
  return String(value ?? "");
}

function formatDurationChipSeconds(value: unknown): string {
  if (typeof value !== "number" || Number.isNaN(value)) return "";
  const totalSeconds = Math.max(0, Math.round(value));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${minutes}:${String(seconds).padStart(2, "0")}`;
}

function formatPercentChipValue(value: unknown): string {
  return typeof value === "number" && Number.isFinite(value) ? `${Number(value.toFixed(1))}%` : "";
}

export function formatFilterChipValue(def: CriterionDefinition | undefined, value: unknown, nameMap?: Map<number, string>): string {
  if (Array.isArray(value)) return value.map((item) => formatChipScalar(item)).join(", ");
  if (!value || typeof value !== "object") return String(value ?? "");

  const criterion = value as {
    value?: unknown;
    value2?: unknown;
    tagId?: number;
    unit?: string;
    clauses?: Array<{ tagId?: number; value?: unknown; value2?: unknown; modifier?: string; unit?: string }>;
    excludes?: unknown[];
    modifier?: string;
    depth?: number;
    _names?: Record<string, string>;
  };
  const modifier = criterion.modifier ? CHIP_MODIFIER_LABELS[criterion.modifier] ?? criterion.modifier : "";
  const resolveEntityName = (id: unknown): string => {
    if (typeof id === "number") return criterion._names?.[String(id)] ?? nameMap?.get(id) ?? "Unavailable item";
    return formatChipEntityId(id, nameMap);
  };

  if (def?.type === "tagDuration") {
    const clauses = Array.isArray(criterion.clauses) && criterion.clauses.length > 0 ? criterion.clauses : [criterion];
    const parts = clauses.map((clause) => {
      if (!clause.tagId || typeof clause.value !== "number") return "";
      const clauseModifier = clause.modifier ? CHIP_MODIFIER_LABELS[clause.modifier] ?? clause.modifier : "";
      const formatValue = (clause.unit ?? "seconds") === "percent" ? formatPercentChipValue : formatDurationChipSeconds;
      const valueText = formatValue(clause.value);
      const value2Text = formatValue(clause.value2);
      if (clause.modifier === "BETWEEN" || clause.modifier === "NOT_BETWEEN") {
        return `${resolveEntityName(clause.tagId)} ${clauseModifier} ${valueText} and ${value2Text}`.trim();
      }
      return `${resolveEntityName(clause.tagId)} ${clauseModifier} ${valueText}`.trim();
    }).filter(Boolean);
    return parts.join(" · ") || JSON.stringify(value);
  }

  if (def?.type === "multiId") {
    const included = Array.isArray(criterion.value) ? criterion.value.map(resolveEntityName).filter(Boolean).join(", ") : "";
    const excluded = Array.isArray(criterion.excludes) ? criterion.excludes.map(resolveEntityName).filter(Boolean).join(", ") : "";
    return [
      included ? `${modifier} ${included}`.trim() : "",
      excluded ? `Except ${excluded}` : "",
      criterion.depth === -1 ? "with sub-tags" : "",
    ].filter(Boolean).join(" · ");
  }

  if (criterion.modifier === "IS_NULL" || criterion.modifier === "NOT_NULL") return modifier;
  const valueText = formatChipScalar(criterion.value);
  const value2Text = formatChipScalar(criterion.value2);
  if (criterion.modifier === "BETWEEN" || criterion.modifier === "NOT_BETWEEN") {
    return `${modifier} ${valueText} and ${value2Text}`.trim();
  }
  return valueText ? `${modifier} ${valueText}`.trim() : JSON.stringify(value);
}

interface ActiveObjectFilterChipsProps {
  criteriaDefinitions: CriterionDefinition[];
  objectFilter: Record<string, unknown>;
  onRemove: (key: string) => void;
  onEdit: (key: string) => void;
  onClearAll?: () => void;
  customFilterSections?: FilterDialogCustomSection[];
  className?: string;
}

export function ActiveObjectFilterChips({ criteriaDefinitions, objectFilter, onRemove, onEdit, onClearAll, customFilterSections, className = "" }: ActiveObjectFilterChipsProps) {
  const activeEntityTypes = useMemo(() => {
    const types = new Set<string>();
    for (const key of Object.keys(objectFilter)) {
      const def = criteriaDefinitions.find((item) => item.id === key || item.filterKey === key || item.auxiliaryToggleKey === key);
      if ((def?.type === "multiId" || def?.type === "tagDuration") && def.entityType) types.add(def.entityType);
    }
    return types;
  }, [criteriaDefinitions, objectFilter]);

  const { data: tagEntities } = useQuery({ queryKey: ["tags", "all"], queryFn: async () => (await tags.find({ perPage: 5000, sort: "name", direction: "asc" }, { includeCounts: false })).items, staleTime: 60000, enabled: activeEntityTypes.has("tags") });
  const { data: performerEntities } = useQuery({ queryKey: ["performers", "all"], queryFn: async () => (await performers.find({ perPage: 5000, sort: "name", direction: "asc" })).items, staleTime: 60000, enabled: activeEntityTypes.has("performers") });
  const { data: studioEntities } = useQuery({ queryKey: ["studios", "all"], queryFn: async () => (await studios.find({ perPage: 5000, sort: "name", direction: "asc" })).items, staleTime: 60000, enabled: activeEntityTypes.has("studios") });
  const { data: groupEntities } = useQuery({ queryKey: ["groups", "all"], queryFn: async () => (await groups.find({ perPage: 5000, sort: "name", direction: "asc" })).items, staleTime: 60000, enabled: activeEntityTypes.has("groups") });
  const { data: tagGroupEntities } = useQuery({ queryKey: ["tag-groups"], queryFn: () => tagGroups.list(), staleTime: 60000, enabled: activeEntityTypes.has("tagGroups") });

  const entityNameMaps = useMemo(() => {
    const maps: Record<string, Map<number, string>> = {};
    const buildMap = (entities: any[] | undefined) => new Map((entities ?? []).map((entity) => [entity.id, entity.name || entity.title || "Untitled item"]));
    if (tagEntities) maps.tags = buildMap(tagEntities);
    if (performerEntities) maps.performers = buildMap(performerEntities);
    if (studioEntities) maps.studios = buildMap(studioEntities);
    if (groupEntities) maps.groups = buildMap(groupEntities);
    if (tagGroupEntities) maps.tagGroups = buildMap(tagGroupEntities);
    return maps;
  }, [groupEntities, performerEntities, studioEntities, tagEntities, tagGroupEntities]);

  if (Object.keys(objectFilter).length === 0) return null;

  return (
    <div className={`mx-1 mt-1 flex flex-wrap items-center gap-1.5 rounded-lg border border-border bg-surface/50 px-3 py-1.5 ${className}`}>
      {Object.entries(objectFilter).map(([key, value]) => {
        const customSection = customFilterSections?.find((section) => section.filterKey === key);
        const def = criteriaDefinitions.find((item) => item.id === key || item.filterKey === key || item.auxiliaryToggleKey === key);
        const isAuxiliaryToggle = def?.auxiliaryToggleKey === key;
        const label = customSection?.label ?? (isAuxiliaryToggle ? def.auxiliaryToggleLabel : undefined) ?? def?.label ?? key;
        const nameMap = def?.entityType ? entityNameMaps[def.entityType] : undefined;
        const displayValue = customSection?.summarize?.(value) ?? (isAuxiliaryToggle && typeof value === "boolean" ? (value ? "Yes" : "No") : formatFilterChipValue(def, value, nameMap));
        return (
          <div key={key} className="group flex items-center rounded-full border border-border bg-card text-xs text-foreground transition-colors hover:border-accent">
            <button type="button" onClick={() => onEdit(key)} className="flex min-h-6 items-center gap-1 py-0.5 pl-2.5" title={`Edit filter: ${label}`} aria-label={`Edit filter: ${label}`}>
              <span className="text-muted">{label}:</span>
              <span className="max-w-[200px] truncate">{displayValue}</span>
            </button>
            <button type="button" onClick={() => onRemove(key)} className="flex min-h-6 min-w-6 items-center justify-center py-0.5 pl-1 pr-2.5 text-muted hover:text-red-300" title={`Remove filter: ${label}`} aria-label={`Remove filter: ${label}`}>
              <X className="h-3 w-3 opacity-50 group-hover:opacity-100" />
            </button>
          </div>
        );
      })}
      {onClearAll ? <button type="button" onClick={onClearAll} className="text-xs text-muted hover:text-red-300">Clear all</button> : null}
    </div>
  );
}
