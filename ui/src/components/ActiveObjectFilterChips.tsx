import { useQuery } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { groups, performers, studios, tagGroups, tags } from "../api/client";
import { formatHumanDuration } from "../utils/durationFormat";
import type { CriterionDefinition, FilterDialogCustomSection } from "./FilterDialog";
import { getMultiIdModifierLabel } from "../utils/filterModifierLabels";
import type { MetadataServer, RatingSystemOptions } from "../api/types";
import { convertToRatingFormat, formatDisplayRating, normalizeRatingOptions, RatingStars, useRatingOptions } from "./Rating";
import { RESOLUTION_FILTER_OPTIONS } from "../utils/resolutionBuckets";
import { useOptionalAppConfig } from "../state/AppConfigContext";
import { CountryLabel } from "./Country";

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
  UNDER_PATH: "Under",
  NOT_UNDER_PATH: "Not Under",
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

function formatPercentChipValue(value: unknown): string {
  return typeof value === "number" && Number.isFinite(value) ? `${Number(value.toFixed(1))}%` : "";
}

function formatRatingChipScalar(value: unknown, options?: RatingSystemOptions): string {
  if (typeof value !== "number" || !Number.isFinite(value)) return formatChipScalar(value);
  const label = formatDisplayRating(value, options);
  if (label === null) return "0";
  if (normalizeRatingOptions(options).type !== "stars") return label;
  return `${label} ${label === "1" ? "star" : "stars"}`;
}

function formatOptionLabel(def: CriterionDefinition | undefined, value: unknown): string {
  const option = def?.options?.find((candidate) => candidate.value === String(value ?? ""));
  return option?.label ?? formatChipScalar(value);
}

function getMultiEnumOptionValues(def: CriterionDefinition, criterion: { value?: unknown; modifier?: string; _selectedValues?: string[] }): string[] {
  if (Array.isArray(criterion._selectedValues) && criterion._selectedValues.length > 0) {
    return criterion._selectedValues;
  }

  if (typeof criterion.value !== "string" || !criterion.value) return [];
  if (criterion.modifier !== "MATCHES_REGEX" && criterion.modifier !== "NOT_MATCHES_REGEX") {
    return [criterion.value];
  }

  const generatedPattern = criterion.value.match(/^\^\(\?:([\s\S]*)\)\$$/);
  if (!generatedPattern) return [];
  return generatedPattern[1]
    .split(/(?<!\\)\|/)
    .map((item) => item.replace(/\\([.*+?^${}()|[\]\\])/g, "$1"));
}

function formatCareerLength(value: unknown): string {
  if (typeof value !== "number" || !Number.isFinite(value)) return formatChipScalar(value);
  return `${value} ${value === 1 ? "year" : "years"}`;
}

function formatMetadataServiceLabel(endpoint: string, metadataServers: MetadataServer[]): string {
  if (!endpoint) return "Any metadata service";
  const configured = metadataServers.find((server) => server.endpoint.trim().toLowerCase() === endpoint.toLowerCase());
  if (!configured) return `${endpoint} (unconfigured)`;
  return configured.name?.trim() || configured.endpoint.trim();
}

export function formatRemoteIdFilterChipValue(
  value: unknown,
  endpointValue: unknown,
  metadataServers: MetadataServer[] = [],
): string {
  const valueCriterion = value && typeof value === "object" ? value as { value?: unknown; modifier?: string } : undefined;
  const endpointCriterion = endpointValue && typeof endpointValue === "object" ? endpointValue as { value?: unknown; modifier?: string } : undefined;
  const endpoint = typeof endpointCriterion?.value === "string" ? endpointCriterion.value.trim() : "";
  const service = formatMetadataServiceLabel(endpoint, metadataServers);
  const modifierKey = valueCriterion?.modifier ?? endpointCriterion?.modifier ?? "EQUALS";
  const modifier = CHIP_MODIFIER_LABELS[modifierKey] ?? modifierKey;

  if (modifierKey === "IS_NULL" || modifierKey === "NOT_NULL") return `${service} ${modifier}`;
  const remoteId = formatChipScalar(valueCriterion?.value);
  return remoteId ? `${service} ${modifier} ${remoteId}` : service;
}

type MultiIdChipCriterion = {
  value?: unknown;
  excludes?: unknown[];
  modifier?: string;
  depth?: number;
  _names?: Record<string, string>;
};

function formatNaturalList(values: string[], conjunction: "and" | "or" | "nor"): string {
  if (values.length < 2) return values[0] ?? "";
  if (values.length === 2) return `${values[0]} ${conjunction} ${values[1]}`;
  return `${values.slice(0, -1).join(", ")}, ${conjunction} ${values.at(-1)}`;
}

function formatExcludedValues(values: string[]): string {
  if (values.length === 0) return "";
  if (values.length === 1) return `not ${values[0]}`;
  if (values.length === 2) return `neither ${formatNaturalList(values, "nor")}`;
  return `none of ${formatNaturalList(values, "or")}`;
}

function formatMultiIdSummary(includedValues: string[], excludedValues: string[], modifier: string | undefined, depth: number | undefined): string {
  const included = formatNaturalList(includedValues, modifier === "INCLUDES_ALL" ? "and" : "or");
  const excluded = formatExcludedValues(excludedValues);
  const selection = included && excluded ? `${included} but ${excluded}` : included || excluded;
  return [selection, depth === -1 ? "with sub-tags" : ""].filter(Boolean).join(" ");
}

export function formatFilterChipValue(def: CriterionDefinition | undefined, value: unknown, nameMap?: Map<number, string>, ratingOptions?: RatingSystemOptions): string {
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
    type?: string;
    _selectedValues?: string[];
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
      const formatValue = (clause.unit ?? "seconds") === "percent" ? formatPercentChipValue : formatHumanDuration;
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
    if (criterion.modifier === "IS_NULL" || criterion.modifier === "NOT_NULL") {
      return getMultiIdModifierLabel(criterion.modifier, def.entityType, modifier);
    }
    const includedValues = Array.isArray(criterion.value) ? criterion.value.map(resolveEntityName).filter(Boolean) : [];
    const excludedValues = Array.isArray(criterion.excludes) ? criterion.excludes.map(resolveEntityName).filter(Boolean) : [];
    if (criterion.modifier === "INCLUDES" || criterion.modifier === "INCLUDES_ALL") {
      return formatMultiIdSummary(includedValues, excludedValues, criterion.modifier, criterion.depth);
    }
    if (criterion.modifier === "EXCLUDES") {
      return formatMultiIdSummary([], [...includedValues, ...excludedValues], criterion.modifier, criterion.depth);
    }
    if (criterion.modifier === "EXCLUDES_ALL") {
      const values = [...includedValues, ...excludedValues];
      return [`not all of ${formatNaturalList(values, "and")}`, criterion.depth === -1 ? "with sub-tags" : ""].filter(Boolean).join(" ");
    }
    const included = includedValues.join(", ");
    return [included ? `${modifier} ${included}`.trim() : "", criterion.depth === -1 ? "with sub-tags" : ""].filter(Boolean).join(" · ");
  }

  if (def?.type === "hash") {
    const algorithm = formatOptionLabel(def, criterion.type);
    if (criterion.modifier === "IS_NULL" || criterion.modifier === "NOT_NULL") return `${algorithm} ${modifier}`.trim();
    const hashValue = formatChipScalar(criterion.value);
    return hashValue ? `${algorithm} ${modifier} ${hashValue}`.trim() : algorithm;
  }

  if (def?.type === "enum" && def.multiSelectOptions) {
    if (criterion.modifier === "IS_NULL") return "No Value";
    if (criterion.modifier === "NOT_NULL") return "Has Value";
    const values = getMultiEnumOptionValues(def, criterion);
    if (values.length > 0) {
      const labels = values.map((item) => formatOptionLabel(def, item));
      const prefix = criterion.modifier === "NOT_MATCHES_REGEX" ? "None of" : "Any of";
      return `${prefix} ${formatNaturalList(labels, "or")}`;
    }
  }

  if (criterion.modifier === "IS_NULL" || criterion.modifier === "NOT_NULL") return modifier;
  const formatValue = def?.type === "duration"
    ? formatHumanDuration
    : def?.type === "rating"
    ? (raw: unknown) => formatRatingChipScalar(raw, ratingOptions)
    : def?.type === "resolution"
    ? (raw: unknown) => RESOLUTION_FILTER_OPTIONS.find((option) => option.value === raw)?.label ?? formatChipScalar(raw)
    : def?.type === "careerLength"
    ? formatCareerLength
    : def?.type === "enum"
    ? (raw: unknown) => formatOptionLabel(def, raw)
    : formatChipScalar;
  const valueText = formatValue(criterion.value);
  const value2Text = formatValue(criterion.value2);
  if (criterion.modifier === "BETWEEN" || criterion.modifier === "NOT_BETWEEN") {
    return `${modifier} ${valueText} and ${value2Text}`.trim();
  }
  return valueText ? `${modifier} ${valueText}`.trim() : JSON.stringify(value);
}

function MultiIdFilterChipDisplay({ def, value, nameMap, fallback }: { def: CriterionDefinition; value: unknown; nameMap?: Map<number, string>; fallback: string }) {
  if (!value || typeof value !== "object") return <>{fallback}</>;
  const criterion = value as MultiIdChipCriterion;
  const isLegacyExcluded = criterion.modifier === "EXCLUDES" || criterion.modifier === "EXCLUDES_ALL";
  if (criterion.modifier !== "INCLUDES" && criterion.modifier !== "INCLUDES_ALL" && !isLegacyExcluded) return <>{fallback}</>;

  const resolveEntityName = (id: unknown): string => {
    if (typeof id === "number") return criterion._names?.[String(id)] ?? nameMap?.get(id) ?? "Unavailable item";
    return formatChipEntityId(id, nameMap);
  };
  const primaryValues = Array.isArray(criterion.value) ? criterion.value.map(resolveEntityName).filter(Boolean) : [];
  const explicitExcludedValues = Array.isArray(criterion.excludes) ? criterion.excludes.map(resolveEntityName).filter(Boolean) : [];
  const includedValues = isLegacyExcluded ? [] : primaryValues;
  const excludedValues = isLegacyExcluded ? [...primaryValues, ...explicitExcludedValues] : explicitExcludedValues;
  if (includedValues.length === 0 && excludedValues.length === 0) return <>{fallback}</>;

  const renderValues = (values: string[], kind: "included" | "excluded", conjunction: "and" | "or" | "nor") => values.map((name, index) => {
    let connector = "";
    if (index > 0) {
      connector = values.length === 2
        ? `\u00a0${conjunction} `
        : index === values.length - 1
          ? `,\u00a0${conjunction} `
          : ", ";
    }
    const colorClass = kind === "included" ? "text-green-300" : "text-red-300";
    return (
      <span key={`${kind}-${index}-${name}`}>
        {connector}
        <span
          data-filter-value-kind={kind}
          className={`inline font-medium ${colorClass}`}
          title={name}
        >
          {name}
        </span>
      </span>
    );
  });

  const excludedPrefix = criterion.modifier === "EXCLUDES_ALL"
    ? "not all of "
    : excludedValues.length === 1 ? "not " : excludedValues.length === 2 ? "neither " : "none of ";
  const excludedConjunction = criterion.modifier === "EXCLUDES_ALL" ? "and" : excludedValues.length === 2 ? "nor" : "or";
  return (
    <span className="inline-flex min-w-0 max-w-full flex-wrap items-center py-0.5 leading-5">
      {includedValues.length > 0 ? renderValues(includedValues, "included", criterion.modifier === "INCLUDES_ALL" ? "and" : "or") : null}
      {includedValues.length > 0 && excludedValues.length > 0 ? <span>&nbsp;but&nbsp;</span> : null}
      {excludedValues.length > 0 ? <span data-filter-excluded-prefix>{excludedPrefix.trim()} </span> : null}
      {excludedValues.length > 0 ? renderValues(excludedValues, "excluded", excludedConjunction) : null}
      {criterion.depth === -1 ? <span>&nbsp;with sub-tags</span> : null}
    </span>
  );
}

function RatingFilterChipDisplay({ value, options, fallback }: { value: unknown; options: RatingSystemOptions; fallback: string }) {
  if (normalizeRatingOptions(options).type !== "stars" || !value || typeof value !== "object") return <>{fallback}</>;
  const criterion = value as { value?: unknown; value2?: unknown; modifier?: string };
  const modifier = criterion.modifier ? CHIP_MODIFIER_LABELS[criterion.modifier] ?? criterion.modifier : "";
  if (criterion.modifier === "IS_NULL" || criterion.modifier === "NOT_NULL") return <>{modifier}</>;

  const renderStars = (raw: unknown) => {
    if (typeof raw !== "number") return <span>{formatChipScalar(raw)}</span>;
    const displayValue = convertToRatingFormat(raw, options);
    return displayValue === null ? <span>0</span> : <RatingStars value={displayValue} sizeClass="h-3 w-3" />;
  };

  if (criterion.modifier === "BETWEEN" || criterion.modifier === "NOT_BETWEEN") {
    return <span className="inline-flex items-center gap-1">{modifier} {renderStars(criterion.value)} <span>and</span> {renderStars(criterion.value2)}</span>;
  }
  return <span className="inline-flex items-center gap-1">{modifier} {renderStars(criterion.value)}</span>;
}

function CountryFilterChipDisplay({ value, fallback }: { value: unknown; fallback: string }) {
  if (!value || typeof value !== "object") return <span title={fallback}>{fallback}</span>;
  const criterion = value as { value?: unknown; modifier?: string };
  if (criterion.modifier === "IS_NULL" || criterion.modifier === "NOT_NULL" || typeof criterion.value !== "string" || !criterion.value) return <span title={fallback}>{fallback}</span>;
  const modifier = criterion.modifier ? CHIP_MODIFIER_LABELS[criterion.modifier] ?? criterion.modifier : "";
  return <span className="inline-flex items-center gap-1">{modifier ? <span>{modifier}</span> : null}<CountryLabel value={criterion.value} /></span>;
}

interface ActiveObjectFilterChipsProps {
  criteriaDefinitions: CriterionDefinition[];
  objectFilter: Record<string, unknown>;
  onRemove: (key: string) => void;
  onEdit: (key: string) => void;
  onClearAll?: () => void;
  customFilterSections?: FilterDialogCustomSection[];
  className?: string;
  ariaLabel?: string;
  rovingKeyboardAccess?: boolean;
  onFocusFallback?: () => void;
  onFocusKey?: (key: string) => void;
}

function findCriterionDefinition(criteriaDefinitions: CriterionDefinition[], key: string) {
  return criteriaDefinitions.find((item) => item.id === key
    || item.filterKey === key
    || item.secondaryFilterKey === key
    || item.auxiliaryToggleKey === key);
}

export function countActiveObjectFilters(criteriaDefinitions: CriterionDefinition[], objectFilter: Record<string, unknown>): number {
  const processedKeys = new Set<string>();
  let count = 0;
  for (const key of Object.keys(objectFilter)) {
    if (processedKeys.has(key)) continue;
    const def = findCriterionDefinition(criteriaDefinitions, key);
    if (def?.type === "remoteId" && def.secondaryFilterKey) {
      processedKeys.add(def.filterKey);
      processedKeys.add(def.secondaryFilterKey);
    } else {
      processedKeys.add(key);
    }
    count += 1;
  }
  return count;
}

export function ActiveObjectFilterChips(props: ActiveObjectFilterChipsProps) {
  const { criteriaDefinitions, objectFilter } = props;
  const activeEntityTypes = useMemo(() => {
    const types = new Set<string>();
    for (const key of Object.keys(objectFilter)) {
      const def = findCriterionDefinition(criteriaDefinitions, key);
      if ((def?.type === "multiId" || def?.type === "tagDuration") && def.entityType) types.add(def.entityType);
    }
    return types;
  }, [criteriaDefinitions, objectFilter]);
  const hasRemoteIdFilter = Object.keys(objectFilter).some((key) => findCriterionDefinition(criteriaDefinitions, key)?.type === "remoteId");

  if (Object.keys(objectFilter).length === 0) return null;

  if (hasRemoteIdFilter) {
    return <ActiveObjectFilterChipsWithMetadata {...props} activeEntityTypes={activeEntityTypes} />;
  }

  if (activeEntityTypes.size === 0) {
    return <ActiveObjectFilterChipsContent {...props} entityNameMaps={{}} metadataServers={[]} />;
  }

  return <ActiveObjectFilterChipsWithEntityNames {...props} activeEntityTypes={activeEntityTypes} metadataServers={[]} />;
}

function ActiveObjectFilterChipsWithMetadata(props: ActiveObjectFilterChipsProps & { activeEntityTypes: Set<string> }) {
  const appConfig = useOptionalAppConfig();
  const metadataServers = appConfig?.config?.scraping?.metadataServers ?? [];
  if (props.activeEntityTypes.size === 0) {
    return <ActiveObjectFilterChipsContent {...props} entityNameMaps={{}} metadataServers={metadataServers} />;
  }

  return <ActiveObjectFilterChipsWithEntityNames {...props} metadataServers={metadataServers} />;
}

function ActiveObjectFilterChipsWithEntityNames(props: ActiveObjectFilterChipsProps & { activeEntityTypes: Set<string>; metadataServers: MetadataServer[] }) {
  const { activeEntityTypes } = props;
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

  return <ActiveObjectFilterChipsContent {...props} entityNameMaps={entityNameMaps} />;
}

function ActiveObjectFilterChipsContent({
  criteriaDefinitions,
  objectFilter,
  onRemove,
  onEdit,
  onClearAll,
  customFilterSections,
  className = "",
  ariaLabel = "Applied filters",
  entityNameMaps,
  rovingKeyboardAccess = false,
  onFocusFallback,
  onFocusKey,
  metadataServers,
}: ActiveObjectFilterChipsProps & { entityNameMaps: Record<string, Map<number, string>>; metadataServers: MetadataServer[] }) {
  const ratingOptions = useRatingOptions();
  const logicalEntries = useMemo(() => {
    const processedKeys = new Set<string>();
    const entries: Array<{
      key: string;
      value: unknown;
      endpointValue?: unknown;
      customSection?: FilterDialogCustomSection;
      def?: CriterionDefinition;
    }> = [];

    for (const [objectKey, value] of Object.entries(objectFilter)) {
      if (processedKeys.has(objectKey)) continue;
      const customSection = customFilterSections?.find((section) => section.filterKey === objectKey);
      const def = findCriterionDefinition(criteriaDefinitions, objectKey);
      if (def?.type === "remoteId" && def.secondaryFilterKey) {
        const primaryKey = def.filterKey;
        const secondaryKey = def.secondaryFilterKey;
        processedKeys.add(primaryKey);
        processedKeys.add(secondaryKey);
        entries.push({
          key: Object.hasOwn(objectFilter, primaryKey) ? primaryKey : secondaryKey,
          value: objectFilter[primaryKey],
          endpointValue: objectFilter[secondaryKey],
          def,
        });
        continue;
      }

      processedKeys.add(objectKey);
      entries.push({ key: objectKey, value, customSection, def });
    }

    return entries;
  }, [criteriaDefinitions, customFilterSections, objectFilter]);
  const keys = logicalEntries.map((entry) => entry.key);
  const keysSignature = keys.join("\u0000");
  const [focusedKey, setFocusedKey] = useState<string | null>(() => keys[0] ?? null);
  const [announcement, setAnnouncement] = useState("");
  const buttonRefs = useRef(new Map<string, HTMLButtonElement>());
  const clearAllRef = useRef<HTMLButtonElement>(null);
  const pendingRemovalRef = useRef<{ key: string | null; label: string } | null>(null);
  const instructionsId = useId();

  useEffect(() => {
    if (focusedKey && keys.includes(focusedKey)) return;
    setFocusedKey(keys[0] ?? null);
  }, [focusedKey, keysSignature]);

  useEffect(() => {
    const pending = pendingRemovalRef.current;
    if (!pending) return;
    pendingRemovalRef.current = null;
    setAnnouncement(`Removed ${pending.label} filter. ${keys.length} selected.`);
    if (pending.key) buttonRefs.current.get(pending.key)?.focus();
    else onFocusFallback?.();
  }, [keysSignature, onFocusFallback]);

  const focusKey = (key: string) => {
    setFocusedKey(key);
    buttonRefs.current.get(key)?.focus();
  };

  const handleEditKeyDown = (event: KeyboardEvent<HTMLButtonElement>, key: string, label: string) => {
    const index = keys.indexOf(key);
    if (onClearAll && ((event.key === "ArrowRight" && index === keys.length - 1) || (event.key === "ArrowLeft" && index === 0) || event.key === "End")) {
      event.preventDefault();
      clearAllRef.current?.focus();
      return;
    }
    let nextIndex: number | undefined;
    if (event.key === "ArrowRight") nextIndex = (index + 1) % keys.length;
    if (event.key === "ArrowLeft") nextIndex = (index - 1 + keys.length) % keys.length;
    if (event.key === "Home") nextIndex = 0;
    if (event.key === "End") nextIndex = keys.length - 1;
    if (nextIndex !== undefined) {
      event.preventDefault();
      focusKey(keys[nextIndex]);
      return;
    }
    if (event.key === "Delete" || event.key === "Backspace") {
      event.preventDefault();
      const nextKey = keys[index + 1] ?? keys[index - 1] ?? null;
      if (!nextKey) {
        onRemove(key);
        window.setTimeout(() => onFocusFallback?.(), 0);
        return;
      }
      pendingRemovalRef.current = { key: nextKey, label };
      setFocusedKey(nextKey);
      onRemove(key);
      window.setTimeout(() => onFocusKey?.(nextKey), 0);
    }
  };

  const removeFilter = (key: string, label: string) => {
    const index = keys.indexOf(key);
    const nextKey = keys[index + 1] ?? keys[index - 1] ?? null;
    if (!nextKey) {
      onRemove(key);
      window.setTimeout(() => onFocusFallback?.(), 0);
      return;
    }
    pendingRemovalRef.current = { key: nextKey, label };
    setFocusedKey(nextKey);
    onRemove(key);
    window.setTimeout(() => onFocusKey?.(nextKey), 0);
  };

  return (
    <div className={`mx-1 mt-2 flex flex-wrap items-center gap-1 rounded-lg border border-border bg-surface/50 p-1 ${className}`} role={rovingKeyboardAccess ? "toolbar" : "region"} aria-label={ariaLabel} aria-orientation={rovingKeyboardAccess ? "horizontal" : undefined} aria-describedby={rovingKeyboardAccess ? instructionsId : undefined}>
      {rovingKeyboardAccess ? <span id={instructionsId} className="sr-only">Use Left and Right Arrow to review filters, Enter to edit, and Delete or Backspace to remove.</span> : null}
      {logicalEntries.map(({ key, value, endpointValue, customSection, def }) => {
        const isAuxiliaryToggle = def?.auxiliaryToggleKey === key;
        const label = customSection?.label ?? (isAuxiliaryToggle ? def.auxiliaryToggleLabel : undefined) ?? def?.label ?? key;
        const nameMap = def?.entityType ? entityNameMaps[def.entityType] : undefined;
        const displayValue = def?.type === "remoteId"
          ? formatRemoteIdFilterChipValue(value, endpointValue, metadataServers)
          : customSection?.summarize?.(value) ?? (isAuxiliaryToggle && typeof value === "boolean" ? (value ? "Yes" : "No") : formatFilterChipValue(def, value, nameMap, ratingOptions));
        const displayContent = !customSection && def?.type === "rating"
          ? <RatingFilterChipDisplay value={value} options={ratingOptions} fallback={displayValue} />
          : !customSection && def?.type === "country"
            ? <CountryFilterChipDisplay value={value} fallback={displayValue} />
          : !customSection && def?.type === "multiId"
            ? <MultiIdFilterChipDisplay def={def} value={value} nameMap={nameMap} fallback={displayValue} />
            : displayValue;
        return (
          <div key={key} className="group flex min-h-[26px] max-w-full items-stretch overflow-hidden rounded-md border border-border bg-card text-xs text-foreground transition-colors hover:border-accent">
            <button
              ref={(element) => { if (element) buttonRefs.current.set(key, element); else buttonRefs.current.delete(key); }}
              type="button"
              onClick={() => onEdit(key)}
              onFocus={rovingKeyboardAccess ? () => setFocusedKey(key) : undefined}
              onKeyDown={rovingKeyboardAccess ? (event) => handleEditKeyDown(event, key, label) : undefined}
              tabIndex={rovingKeyboardAccess ? (focusedKey === key || (!focusedKey && key === keys[0]) ? 0 : -1) : undefined}
              aria-keyshortcuts={rovingKeyboardAccess ? "ArrowLeft ArrowRight Home End Delete Backspace" : undefined}
              data-active-filter-key={key}
              className="flex min-w-0 max-w-full flex-wrap items-center gap-1 px-2 text-left"
              title={def?.type === "country" ? undefined : `${label}: ${displayValue}`}
              aria-label={`Edit filter: ${label}`}
            >
              <span className="text-muted">{label}:</span>
              <span className="flex min-w-0 max-w-full flex-wrap items-center">{displayContent}</span>
            </button>
            <button type="button" tabIndex={rovingKeyboardAccess ? -1 : undefined} onClick={() => rovingKeyboardAccess ? removeFilter(key, label) : onRemove(key)} className="flex w-7 items-center justify-center border-l border-border text-muted hover:bg-red-500/10 hover:text-red-300" title={`Remove filter: ${label}`} aria-label={`Remove filter: ${label}`}>
              <X className="h-3 w-3" />
            </button>
          </div>
        );
      })}
      {onClearAll ? (
        <button
          ref={clearAllRef}
          type="button"
          tabIndex={rovingKeyboardAccess ? -1 : undefined}
          onClick={onClearAll}
          onKeyDown={rovingKeyboardAccess ? (event) => {
            if (event.key === "ArrowRight" || event.key === "Home") {
              event.preventDefault();
              focusKey(keys[0]);
            } else if (event.key === "ArrowLeft") {
              event.preventDefault();
              focusKey(keys[keys.length - 1]);
            }
          } : undefined}
          aria-keyshortcuts={rovingKeyboardAccess ? "ArrowLeft ArrowRight Home" : undefined}
          className="h-[26px] rounded-md px-2 text-xs font-medium text-muted hover:bg-red-500/10 hover:text-red-300"
        >
          Clear all
        </button>
      ) : null}
      <span className="sr-only" role="status" aria-live="polite">{announcement}</span>
    </div>
  );
}
