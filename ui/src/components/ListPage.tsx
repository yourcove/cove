import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode, type RefObject } from "react";
import { LayoutGrid, List, Tags, Grid3X3, Share2, FolderTree, ZoomIn, ZoomOut, SlidersHorizontal, Plus, X, Rows3, MonitorPlay, Play, Pause } from "lucide-react";
import type { CriterionModifier, CustomFieldCriterion, CustomFieldDefinition, CustomFieldEntityType, CustomFieldType, ExtensionListFilterContribution, ExtensionListSortContribution, FindFilter } from "../api/types";
import { ExtensionSlot } from "../router/RouteRegistry";
import { getDefaultFilter, SavedFilterMenu } from "./SavedFilterMenu";
import { InfiniteScrollSentinel } from "./InfiniteScrollSentinel";
import { IsoDateInput } from "./IsoDateInput";
import { FilterDialog, FilterButton, type CriterionDefinition, type CriterionType, type EntityType, type FilterDialogCustomSection } from "./FilterDialog";
import { EntityReferenceSelector, getEntityReferenceLabel, isEntityReferenceType, parseEntityReferenceId } from "./EntityReferenceSelector";
import { useResolvedKeybindingOverrides } from "../hooks/useResolvedKeybindingOverrides";
import { useKeySequence } from "../hooks/useKeySequence";
import { resolveKeybinding } from "../keyboard/keybindings";
import { useAppConfig } from "../state/AppConfigContext";
import { useCustomFieldDefinitions } from "../hooks/useCustomFieldDefinitions";
import { clampEntityCardSizeLevel, getEntityCardMaxLevel, getEntityCardMinWidthPx, parseEntityCardSizeLevel, useEntityCardSize } from "../hooks/useEntityCardSize";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { withSeededRandomSort } from "../utils/seededRandomSort";
import { getSortClauses } from "../utils/sortClauses";
import { trackInteraction } from "../utils/interactionTracking";
import { toolbarIconButtonClass, toolbarSegmentClass, toolbarSelectClass } from "./listToolbarStyles";
import { PageSizeSelect } from "./PageSizeSelect";
import { ListPageCardSizeContext } from "./ListPageCardSizeContext";
import { useExtensions } from "../extensions/ExtensionLoader";
import { ActiveObjectFilterChips, countActiveObjectFilters } from "./ActiveObjectFilterChips";
import { collapseExtensionCriteria, executableExtensionFilterKey, expandExtensionCriteria, unavailableExtensionCriterionDefinitions } from "../extensions/extensionListFilters";
import { QueryState } from "./QueryState";
import { resolveQueryLoadState, type QueryLoadState } from "../utils/queryLoadState";
import { ListSearchControl, type ListSearchCommitSource } from "./ListSearchControl";
import { PaginationControls } from "./PaginationControls";
import { MultiSortControl } from "./MultiSortControl";
import { getWallColumnCountFromSizeLevel, getWallSizeLevelFromColumnCount, WallSizeControl } from "./WallSizeControl";

export type DisplayMode = "grid" | "list" | "wall" | "tagger" | "graph" | "byGroup" | "feed" | "vertical";

export interface ListPageProps {
  title: string;
  pageKey?: string;
  filter: FindFilter;
  onFilterChange: (f: FindFilter) => void;
  totalCount: number;
  isLoading?: boolean;
  error?: Error | null;
  onRetry?: () => void;
  loadState?: QueryLoadState<unknown>;
  children: ReactNode;
  sortOptions?: { value: string; label: string }[];
  multiSortKeys?: readonly string[];
  displayMode?: DisplayMode;
  onDisplayModeChange?: (mode: DisplayMode) => void;
  availableDisplayModes?: DisplayMode[];
  allowInfinitePageSize?: boolean;
  infinitePageSizeOnly?: boolean;
  maxPageSize?: number;
  perPageQueryKey?: string;
  selectedIds?: Set<string | number>;
  onSelectAll?: () => void;
  onSelectAllMatching?: () => void;
  onSelectNone?: () => void;
  onInvertSelection?: () => void;
  selectionActions?: ReactNode;
  selectionMetadata?: ReactNode;
  selectAllLabel?: string;
  selectAllPending?: boolean;
  selectAllMatchingLabel?: string;
  selectAllMatchingPending?: boolean;
  metadataByline?: ReactNode;
  onNew?: () => void;
  renderOperations?: () => ReactNode;
  filterMode?: string;
  savedFilterScope?: string;
  cardSizeEntityType?: string;
  manageDocumentTitle?: boolean;
  savedFilterUIOptions?: Record<string, unknown>;
  onApplySavedFilterUIOptions?: (options: Record<string, unknown>) => void;
  searchMode?: string;
  searchModes?: { value: string; label: string; title?: string }[];
  searchPlaceholder?: string;
  onSearchModeChange?: (mode: string) => void;
  // Advanced filtering
  criteriaDefinitions?: CriterionDefinition[];
  objectFilter?: Record<string, unknown>;
  onObjectFilterChange?: (filter: Record<string, unknown>) => void;
  wallColumnCount?: number;
  onWallColumnCountChange?: (count: number) => void;
  autoScrollContainerRef?: RefObject<HTMLElement | null>;
  infiniteScroll?: {
    hasNextPage?: boolean;
    isFetchingNextPage?: boolean;
    onLoadMore: () => void;
    loadedCount: number;
    totalCount: number;
  };
  showAutoScrollControls?: boolean;
  showPagingControls?: boolean;
  customFilterSections?: FilterDialogCustomSection[];
  showClearAllObjectFilters?: boolean;
  showCustomFilterDivider?: boolean;
}
const DEFAULT_ZOOM_LEVEL = 1;
const CUSTOM_FIELD_ENTITY_BY_FILTER_MODE: Record<string, CustomFieldEntityType> = {
  videos: "video",
  audios: "audio",
  texts: "text",
  performers: "performer",
  tags: "tag",
  studios: "studio",
  galleries: "gallery",
  images: "image",
  groups: "group",
  faces: "face",
};

const LIST_ENTITY_BY_FILTER_MODE: Record<string, string> = {
  videos: "video",
  audios: "audio",
  texts: "text",
  performers: "performer",
  tags: "tag",
  studios: "studio",
  galleries: "gallery",
  images: "image",
  groups: "group",
  faces: "face",
  segments: "segment",
};

const REFERENCE_ENTITY_TYPE_BY_EXTENSION_VALUE: Record<string, EntityType> = {
  tag: "tags",
  tags: "tags",
  taggroup: "tagGroups",
  taggroups: "tagGroups",
  tag_group: "tagGroups",
  performer: "performers",
  performers: "performers",
  studio: "studios",
  studios: "studios",
  group: "groups",
  groups: "groups",
  gallery: "galleries",
  galleries: "galleries",
  video: "videos",
  videos: "videos",
  face: "faces",
  faces: "faces",
};

type CustomFieldQueryDefinition = CustomFieldDefinition & {
  jsonPath?: string;
  unavailable?: boolean;
  fieldLabel?: string;
  targetLabel?: string;
};

const CUSTOM_FIELD_MODIFIER_LABELS: Record<CriterionModifier, string> = {
  EQUALS: "Equals",
  NOT_EQUALS: "Does Not Equal",
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

const TEXT_CUSTOM_FIELD_MODIFIERS: CriterionModifier[] = ["EQUALS", "NOT_EQUALS", "INCLUDES", "EXCLUDES", "IS_NULL", "NOT_NULL"];
const ORDERED_CUSTOM_FIELD_MODIFIERS: CriterionModifier[] = ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"];
const BOOLEAN_CUSTOM_FIELD_MODIFIERS: CriterionModifier[] = ["EQUALS", "NOT_EQUALS", "IS_NULL", "NOT_NULL"];
const REFERENCE_CUSTOM_FIELD_MODIFIERS: CriterionModifier[] = ["INCLUDES", "EXCLUDES", "IS_NULL", "NOT_NULL"];
const PRESENCE_CUSTOM_FIELD_MODIFIERS: CriterionModifier[] = ["NOT_NULL", "IS_NULL"];

function getDefaultCustomFieldModifier(type: CustomFieldType): CriterionModifier {
  if (type === "json" || type === "longText") return "NOT_NULL";
  return isEntityReferenceType(type) ? "INCLUDES" : "EQUALS";
}

function getDefaultCustomFieldValue(type: CustomFieldType) {
  return type === "boolean" ? "true" : "";
}

function normalizeCustomFieldCriteria(value: unknown): CustomFieldCriterion[] {
  return Array.isArray(value) ? value.filter((item): item is CustomFieldCriterion => Boolean(item && typeof item === "object")) : [];
}

function isCustomFieldCriterionActive(value: CustomFieldCriterion | undefined) {
  if (!value?.key) return false;
  const modifier = value.modifier ?? "EQUALS";
  if (modifier === "IS_NULL" || modifier === "NOT_NULL") return true;
  if (value.jsonPath && value.type === "text") {
    if (modifier === "EQUALS" || modifier === "NOT_EQUALS") return true;
    return String(value.value ?? "").length > 0;
  }
  if (modifier === "BETWEEN" || modifier === "NOT_BETWEEN") {
    return String(value.value ?? "").trim() !== "" && String(value.value2 ?? "").trim() !== "";
  }
  return String(value.value ?? "").trim() !== "";
}

function getCustomFieldModifiers(type: CustomFieldType) {
  switch (type) {
    case "json":
    case "longText":
      return PRESENCE_CUSTOM_FIELD_MODIFIERS;
    case "number":
    case "date":
    case "timestamp":
    case "duration":
    case "percent":
      return ORDERED_CUSTOM_FIELD_MODIFIERS;
    case "boolean":
      return BOOLEAN_CUSTOM_FIELD_MODIFIERS;
    case "tag":
    case "performer":
    case "studio":
    case "video":
    case "gallery":
    case "image":
    case "group":
      return REFERENCE_CUSTOM_FIELD_MODIFIERS;
    default:
      return TEXT_CUSTOM_FIELD_MODIFIERS;
  }
}

function normalizeListEntityType(entityType?: string) {
  const normalized = (entityType ?? "").trim().toLowerCase();
  const singular = normalized.endsWith("s") ? normalized.slice(0, -1) : normalized;
  return LIST_ENTITY_BY_FILTER_MODE[normalized] ?? singular;
}

function normalizeCriterionType(value: string | undefined): CriterionType {
  const normalized = (value ?? "string").trim().toLowerCase();
  switch (normalized) {
    case "bool":
    case "boolean":
      return "bool";
    case "int":
    case "integer":
    case "number":
      return "number";
    case "date":
      return "date";
    case "datetime":
    case "timestamp":
      return "timestamp";
    case "duration":
      return "duration";
    case "percent":
      return "number";
    case "rating":
      return "rating";
    case "multiid":
    case "reference":
      return "multiId";
    case "enum":
      return "enum";
    default:
      return "string";
  }
}

function normalizeReferenceEntityType(value?: string): EntityType | undefined {
  if (!value) return undefined;
  return REFERENCE_ENTITY_TYPE_BY_EXTENSION_VALUE[value.trim().toLowerCase()];
}

function createExtensionCriterionDefinition(contribution: ExtensionListFilterContribution): CriterionDefinition | null {
  const criterionType = normalizeCriterionType(contribution.criterionType || contribution.customFieldType);
  const executableFilterKey = executableExtensionFilterKey(contribution);
  if (contribution.filterId && !executableFilterKey) return null;
  const filterKey = executableFilterKey
    || contribution.filterKey
    || (contribution.customFieldKey ? `extension:${contribution.extensionId}:${contribution.id}` : undefined);
  if (!filterKey) return null;

  return {
    id: `extension:${contribution.extensionId}:${contribution.id}`,
    label: contribution.label,
    type: criterionType,
    entityType: normalizeReferenceEntityType(contribution.entityReferenceType),
    filterKey,
    customFieldKey: contribution.customFieldKey,
    customFieldType: contribution.customFieldType,
    modifiers: contribution.modifiers,
    options: contribution.options,
  };
}

function createExtensionSortOption(contribution: ExtensionListSortContribution) {
  const value = contribution.sortKey || (contribution.customFieldKey ? `custom:${contribution.customFieldType || "text"}:${contribution.customFieldKey}` : undefined);
  return value ? { value, label: contribution.label } : null;
}

function formatCustomFieldCriterionValue(
  definition: CustomFieldQueryDefinition | undefined,
  criterion: CustomFieldCriterion,
  valueKey: "value" | "value2",
) {
  const rawValue = criterion[valueKey];
  if (definition?.jsonPath && definition.type === "text") {
    return JSON.stringify(String(rawValue ?? ""));
  }
  if (String(rawValue ?? "").trim() === "") {
    return "";
  }

  if (definition && isEntityReferenceType(definition.type)) {
    const displayValue = valueKey === "value2" ? criterion.displayValue2 : criterion.displayValue;
    return displayValue || `Selected ${getEntityReferenceLabel(definition.type).singular}`;
  }

  return String(rawValue);
}

function customFieldQueryDefinitionId(definition: CustomFieldQueryDefinition) {
  return definition.jsonPath ? `${definition.key}:${encodeURIComponent(definition.jsonPath)}` : definition.key;
}

function findCustomFieldQueryDefinition(
  definitions: CustomFieldQueryDefinition[],
  criterion: CustomFieldCriterion,
) {
  return definitions.find((candidate) => candidate.key === criterion.key
    && (candidate.jsonPath ?? undefined) === (criterion.jsonPath ?? undefined));
}

function createCustomFieldQueryDefinitions(
  definitions: CustomFieldDefinition[],
  capability: "filterable" | "sortable",
): CustomFieldQueryDefinition[] {
  return definitions.flatMap((definition) => {
    if (definition.type === "longText") {
      return capability === "filterable" ? [{
        ...definition,
        fieldLabel: definition.label || definition.key,
        targetLabel: "Presence",
        filterable: true,
      }] : [];
    }

    if (definition.type !== "json") {
      return definition[capability] ? [definition] : [];
    }

    const pathDefinitions = (definition.jsonPaths ?? [])
      .filter((jsonPath) => jsonPath[capability])
      .map((jsonPath) => ({
        ...definition,
        label: `${definition.label || definition.key} › ${jsonPath.label || jsonPath.path}`,
        fieldLabel: definition.label || definition.key,
        targetLabel: jsonPath.label || jsonPath.path,
        type: jsonPath.type,
        options: [],
        filterable: jsonPath.filterable,
        sortable: jsonPath.sortable,
        jsonPath: jsonPath.path,
    }));

    if (capability === "sortable") return pathDefinitions;
    return [{
      ...definition,
      fieldLabel: definition.label || definition.key,
      targetLabel: "Presence",
      filterable: true,
    }, ...pathDefinitions];
  });
}

function createUnavailableCustomFieldQueryDefinition(criterion: CustomFieldCriterion): CustomFieldQueryDefinition {
  const pathLabel = criterion.jsonPath ? ` › ${criterion.jsonPath}` : "";
  return {
    key: criterion.key,
    label: `${criterion.key}${pathLabel} (Unavailable)`,
    type: criterion.type ?? "text",
    entityTypes: [],
    options: [],
    filterable: false,
    sortable: false,
    isMultiValue: false,
    jsonPaths: [],
    jsonPath: criterion.jsonPath,
    unavailable: true,
    fieldLabel: criterion.key,
    targetLabel: criterion.jsonPath ? `${criterion.jsonPath} (Unavailable)` : "Unavailable",
  };
}

function createUnavailableCustomSortOption(key: string) {
  const parts = key.split(":");
  if (parts[0] === "custom-json" && parts.length === 4) {
    let path = parts[3];
    try {
      path = decodeURIComponent(path);
    } catch {
      // Keep the encoded token visible when it is malformed.
    }
    return { value: key, label: `Unavailable custom sort: ${parts[2]} › ${path}` };
  }
  return { value: key, label: `Unavailable custom sort: ${parts.at(-1) ?? key}` };
}

function createCustomFieldFilterSection(definitions: CustomFieldQueryDefinition[]): FilterDialogCustomSection {
  const normalizeCriterion = (criterion: CustomFieldCriterion): CustomFieldCriterion => {
    const definition = findCustomFieldQueryDefinition(definitions, criterion);
    if (!definition) return criterion;
    const availableModifiers = getCustomFieldModifiers(definition.type);
    const defaultModifier = getDefaultCustomFieldModifier(definition.type);
    const modifier = availableModifiers.includes(criterion.modifier ?? defaultModifier) ? (criterion.modifier ?? defaultModifier) : defaultModifier;
    return { ...criterion, type: definition.type, jsonPath: definition.jsonPath, modifier };
  };

  return {
    id: "custom-fields",
    label: "Custom Fields",
    filterKey: "customFieldCriteria",
    defaultValue: [] satisfies CustomFieldCriterion[],
    isActive: (value) => normalizeCustomFieldCriteria(value).map(normalizeCriterion).some(isCustomFieldCriterionActive),
    shouldKeepDraft: (value) => normalizeCustomFieldCriteria(value).some((criterion) => Boolean(criterion.key)),
    sanitize: (value) => normalizeCustomFieldCriteria(value).map(normalizeCriterion).filter(isCustomFieldCriterionActive),
    summarize: (value) => {
      const activeCriteria = normalizeCustomFieldCriteria(value).map(normalizeCriterion).filter(isCustomFieldCriterionActive);
      if (activeCriteria.length === 0) return "";
      return activeCriteria.map((criterion) => {
        const definition = findCustomFieldQueryDefinition(definitions, criterion);
        const label = definition?.label || criterion.key;
        const modifier = CUSTOM_FIELD_MODIFIER_LABELS[criterion.modifier ?? "EQUALS"];
        if (criterion.modifier === "IS_NULL" || criterion.modifier === "NOT_NULL") {
          return `${label} ${modifier}`;
        }

        if (criterion.modifier === "BETWEEN" || criterion.modifier === "NOT_BETWEEN") {
          return `${label} ${modifier} ${formatCustomFieldCriterionValue(definition, criterion, "value")} and ${formatCustomFieldCriterionValue(definition, criterion, "value2")}`;
        }

        return `${label} ${modifier} ${formatCustomFieldCriterionValue(definition, criterion, "value")}`;
      }).join(", ");
    },
    renderEditor: (value, onChange) => (
      <CustomFieldCriteriaEditor
        definitions={definitions}
        value={normalizeCustomFieldCriteria(value)}
        onChange={onChange}
      />
    ),
  };
}

function CustomFieldCriteriaEditor({
  definitions,
  value,
  onChange,
}: {
  definitions: CustomFieldQueryDefinition[];
  value: CustomFieldCriterion[];
  onChange: (value: CustomFieldCriterion[]) => void;
}) {
  const firstDefinition = definitions.find((definition) => !definition.unavailable);
  const fieldDefinitions = definitions.filter((definition, index) =>
    definitions.findIndex((candidate) => candidate.key === definition.key) === index
  );
  const rows = value.length > 0 ? value : [];
  const setRow = (index: number, nextCriterion: CustomFieldCriterion) => {
    onChange(rows.map((criterion, candidateIndex) => candidateIndex === index ? nextCriterion : criterion));
  };
  const removeRow = (index: number) => onChange(rows.filter((_, candidateIndex) => candidateIndex !== index));
  const addRow = () => {
    if (!firstDefinition) return;
    onChange([...rows, { key: firstDefinition.key, jsonPath: firstDefinition.jsonPath, type: firstDefinition.type, value: getDefaultCustomFieldValue(firstDefinition.type), modifier: getDefaultCustomFieldModifier(firstDefinition.type) }]);
  };

  return (
    <div className="space-y-2">
      {rows.map((criterion, index) => {
        const definition = findCustomFieldQueryDefinition(definitions, criterion) ?? firstDefinition;
        if (!definition) return null;
        const availableModifiers = getCustomFieldModifiers(definition.type);
        const defaultModifier = getDefaultCustomFieldModifier(definition.type);
        const modifier = availableModifiers.includes(criterion.modifier ?? defaultModifier) ? (criterion.modifier ?? defaultModifier) : defaultModifier;
        const valueDisabled = modifier === "IS_NULL" || modifier === "NOT_NULL";
        const targetDefinitions = definitions.filter((candidate) => candidate.key === definition.key);

        return (
          <div key={`${criterion.key}-${index}`} className="min-w-0 rounded border border-border bg-background p-3">
            <div className="grid min-w-0 gap-3 md:grid-cols-[minmax(10rem,1fr)_minmax(9rem,0.75fr)] xl:grid-cols-[minmax(12rem,1.1fr)_minmax(9rem,0.6fr)_minmax(18rem,2fr)_auto] xl:items-start">
              <div className="min-w-0 space-y-2">
                <label className="block min-w-0 text-xs text-muted">
                  Field
                  <select
                    value={definition.key}
                    onChange={(event) => {
                      const nextDefinition = definitions.find((candidate) => candidate.key === event.target.value && !candidate.unavailable) ?? definition;
                      setRow(index, { key: nextDefinition.key, jsonPath: nextDefinition.jsonPath, type: nextDefinition.type, value: getDefaultCustomFieldValue(nextDefinition.type), modifier: getDefaultCustomFieldModifier(nextDefinition.type) });
                    }}
                    className="mt-1 w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                  >
                    {fieldDefinitions.map((option) => (
                      <option key={option.key} value={option.key} disabled={option.unavailable}>{option.fieldLabel || option.label || option.key}</option>
                    ))}
                  </select>
                </label>
                {targetDefinitions.length > 1 || definition.jsonPath ? (
                  <label className="block min-w-0 text-xs text-muted">
                    Target
                    <select
                      value={customFieldQueryDefinitionId(definition)}
                      onChange={(event) => {
                        const nextDefinition = targetDefinitions.find((candidate) => customFieldQueryDefinitionId(candidate) === event.target.value) ?? definition;
                        setRow(index, { key: nextDefinition.key, jsonPath: nextDefinition.jsonPath, type: nextDefinition.type, value: getDefaultCustomFieldValue(nextDefinition.type), modifier: getDefaultCustomFieldModifier(nextDefinition.type) });
                      }}
                      className="mt-1 w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                    >
                      {targetDefinitions.map((option) => (
                        <option key={customFieldQueryDefinitionId(option)} value={customFieldQueryDefinitionId(option)} disabled={option.unavailable}>{option.targetLabel || option.label || option.key}</option>
                      ))}
                    </select>
                  </label>
                ) : null}
              </div>
              <label className="block min-w-0 text-xs text-muted">
                Match
                <select
                  value={modifier}
                  onChange={(event) => setRow(index, { ...criterion, modifier: event.target.value as CriterionModifier })}
                  className="mt-1 w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                >
                  {availableModifiers.map((option) => (
                    <option key={option} value={option}>{CUSTOM_FIELD_MODIFIER_LABELS[option]}</option>
                  ))}
                </select>
              </label>
              <div className={`min-w-0 ${modifier === "BETWEEN" || modifier === "NOT_BETWEEN" ? "grid gap-2 sm:grid-cols-2" : ""}`}>
                <CustomFieldValueInput
                  definition={definition}
                  disabled={valueDisabled}
                  value={criterion.value ?? ""}
                  onChange={(nextValue, displayValue) => setRow(index, { ...criterion, modifier, type: definition.type, jsonPath: definition.jsonPath, value: nextValue, displayValue })}
                />
                {modifier === "BETWEEN" || modifier === "NOT_BETWEEN" ? (
                  <CustomFieldValueInput
                    definition={definition}
                    disabled={valueDisabled}
                    label="And"
                    value={criterion.value2 ?? ""}
                    onChange={(nextValue, displayValue) => setRow(index, { ...criterion, modifier, type: definition.type, jsonPath: definition.jsonPath, value2: nextValue, displayValue2: displayValue })}
                  />
                ) : null}
              </div>
              <button
                type="button"
                onClick={() => removeRow(index)}
                aria-label="Remove custom field filter"
                className="justify-self-start rounded border border-border p-2 text-muted hover:border-red-400 hover:text-red-300 xl:mt-6"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </div>
          </div>
        );
      })}
      <button
        type="button"
        onClick={addRow}
        disabled={!firstDefinition}
        className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-xs text-secondary hover:border-accent hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
      >
        <Plus className="h-3.5 w-3.5" />
        Add custom field filter
      </button>
    </div>
  );
}

function CustomFieldValueInput({
  definition,
  disabled,
  label = "Value",
  value,
  onChange,
}: {
  definition: CustomFieldDefinition;
  disabled: boolean;
  label?: string;
  value: string;
  onChange: (value: string, displayValue?: string) => void;
}) {
  if (isEntityReferenceType(definition.type)) {
    const selectedId = parseEntityReferenceId(value);
    const labels = getEntityReferenceLabel(definition.type);
    return (
      <label className="block min-w-0 text-xs text-muted">
        {label}
        <div className="mt-1 min-w-0">
          <EntityReferenceSelector
            entityType={definition.type}
            value={selectedId}
            disabled={disabled}
            placeholder={`Search ${labels.plural}...`}
            inputClassName="w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground placeholder:text-muted disabled:opacity-50 focus:border-accent focus:outline-none"
            onChange={(nextId, option) => onChange(nextId == null ? "" : String(nextId), option?.label)}
          />
        </div>
      </label>
    );
  }

  if (definition.type === "boolean") {
    return (
      <label className="block text-xs text-muted">
        {label}
        <select
          disabled={disabled}
          value={value || "true"}
          onChange={(event) => onChange(event.target.value)}
          className="mt-1 w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground disabled:opacity-50 focus:border-accent focus:outline-none"
        >
          <option value="true">True</option>
          <option value="false">False</option>
        </select>
      </label>
    );
  }

  if (definition.type === "enum" && definition.options.length > 0) {
    return (
      <label className="block text-xs text-muted">
        {label}
        <select
          disabled={disabled}
          value={value}
          onChange={(event) => onChange(event.target.value)}
          className="mt-1 w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground disabled:opacity-50 focus:border-accent focus:outline-none"
        >
          <option value="">Select</option>
          {definition.options.map((option) => (
            <option key={option} value={option}>{option}</option>
          ))}
        </select>
      </label>
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
    <label className="block text-xs text-muted">
      {label}
      <Input
        {...(definition.type === "timestamp" ? { pickerType: "datetime-local" as const } : {})}
        type={inputType[definition.type] ?? "text"}
        disabled={disabled}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        className="mt-1 w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground disabled:opacity-50 focus:border-accent focus:outline-none"
      />
    </label>
  );
}

export function ListPage({
  title,
  pageKey,
  filter,
  onFilterChange,
  totalCount,
  isLoading = false,
  error,
  onRetry,
  loadState,
  children,
  sortOptions,
  multiSortKeys,
  displayMode,
  onDisplayModeChange,
  availableDisplayModes,
  allowInfinitePageSize = false,
  infinitePageSizeOnly = false,
  maxPageSize,
  perPageQueryKey,
  selectedIds,
  onSelectAll,
  onSelectAllMatching,
  onSelectNone,
  onInvertSelection,
  selectionActions,
  selectionMetadata,
  selectAllLabel = "Select all",
  selectAllPending = false,
  selectAllMatchingLabel = "Select all matching",
  selectAllMatchingPending = false,
  metadataByline,
  onNew,
  renderOperations,
  filterMode,
  savedFilterScope,
  cardSizeEntityType: requestedCardSizeEntityType,
  manageDocumentTitle = true,
  savedFilterUIOptions,
  onApplySavedFilterUIOptions,
  searchMode,
  searchModes,
  searchPlaceholder,
  onSearchModeChange,
  criteriaDefinitions,
  objectFilter,
  onObjectFilterChange,
  wallColumnCount,
  onWallColumnCountChange,
  autoScrollContainerRef,
  infiniteScroll,
  showAutoScrollControls = true,
  showPagingControls = true,
  customFilterSections,
  showClearAllObjectFilters = true,
  showCustomFilterDivider = true,
}: ListPageProps) {
  const [filterDialogOpen, setFilterDialogOpen] = useState(false);
  const [filterDialogPreselect, setFilterDialogPreselect] = useState<string | undefined>();
  const cardSizeEntityType = requestedCardSizeEntityType ?? filterMode ?? pageKey;
  const resolvedSavedFilterScope = savedFilterScope ?? filterMode;
  const [zoomLevel, setZoomLevel] = useEntityCardSize(cardSizeEntityType, pageKey, DEFAULT_ZOOM_LEVEL);
  const cardSizeMaxLevel = getEntityCardMaxLevel(cardSizeEntityType);
  const cardMinWidthPx = getEntityCardMinWidthPx(cardSizeEntityType, zoomLevel);
  const [autoScrollEnabled, setAutoScrollEnabled] = useState(false);
  const [autoScrollSpeed, setAutoScrollSpeed] = useState(120);
  const [autoScrollControlsAwake, setAutoScrollControlsAwake] = useState(true);
  const defaultUIOptionsModeRef = useRef<string | undefined>(undefined);
  const restoredPrefsRef = useRef(false);
  const { config } = useAppConfig();
  const { getListFiltersForEntity, getListSortsForEntity } = useExtensions();
  const keybindingOverrides = useResolvedKeybindingOverrides();
  const customFieldEntityType = filterMode ? CUSTOM_FIELD_ENTITY_BY_FILTER_MODE[filterMode] : undefined;
  const listEntityType = normalizeListEntityType(filterMode ?? pageKey);
  const extensionCriteriaDefinitions = useMemo(
    () => getListFiltersForEntity(listEntityType).map(createExtensionCriterionDefinition).filter((item): item is CriterionDefinition => item != null),
    [getListFiltersForEntity, listEntityType]
  );
  const extensionFilterContributions = useMemo(() => getListFiltersForEntity(listEntityType), [getListFiltersForEntity, listEntityType]);
  const unavailableExtensionCriteria = useMemo(
    () => unavailableExtensionCriterionDefinitions(objectFilter ?? {}, extensionFilterContributions),
    [extensionFilterContributions, objectFilter],
  );

  const applySavedFilterUIOptions = useCallback((options: Record<string, unknown>, applyDisplayMode = true) => {
    const nextDisplayMode = typeof options.displayMode === "string" ? options.displayMode as DisplayMode : undefined;
    if (applyDisplayMode && nextDisplayMode && onDisplayModeChange && (!availableDisplayModes || availableDisplayModes.includes(nextDisplayMode))) {
      onDisplayModeChange(nextDisplayMode);
    }
    const nextZoomLevel = parseEntityCardSizeLevel(cardSizeEntityType, options.zoomLevel);
    if (nextZoomLevel != null) setZoomLevel(nextZoomLevel);
    onApplySavedFilterUIOptions?.(options);
  }, [availableDisplayModes, cardSizeEntityType, onApplySavedFilterUIOptions, onDisplayModeChange, setZoomLevel]);

  useEffect(() => {
    if (!resolvedSavedFilterScope || defaultUIOptionsModeRef.current === resolvedSavedFilterScope) return;
    defaultUIOptionsModeRef.current = resolvedSavedFilterScope;
    const options = getDefaultFilter(resolvedSavedFilterScope)?.uiOptions;
    if (!options) return;
    const explicitDisplayMode = new URLSearchParams(window.location.search).get("view");
    const hasSupportedExplicitDisplayMode = explicitDisplayMode != null
      && (!availableDisplayModes || availableDisplayModes.includes(explicitDisplayMode as DisplayMode));
    applySavedFilterUIOptions(options, !hasSupportedExplicitDisplayMode);
  }, [applySavedFilterUIOptions, availableDisplayModes, resolvedSavedFilterScope]);
  const mergedCriteriaDefinitions = useMemo(() => {
    const merged = [...(criteriaDefinitions ?? []), ...extensionCriteriaDefinitions, ...unavailableExtensionCriteria];
    return merged.length > 0 ? merged : undefined;
  }, [criteriaDefinitions, extensionCriteriaDefinitions, unavailableExtensionCriteria]);
  const editorObjectFilter = useMemo(() => expandExtensionCriteria(objectFilter ?? {}), [objectFilter]);
  const extensionSortOptions = useMemo(
    () => getListSortsForEntity(listEntityType).map(createExtensionSortOption).filter((item): item is { value: string; label: string } => item != null),
    [getListSortsForEntity, listEntityType]
  );
  const { data: customFieldDefinitions = [] } = useCustomFieldDefinitions(customFieldEntityType, Boolean(customFieldEntityType));
  const generatedCustomFieldSection = useMemo(() => {
    const definitions = createCustomFieldQueryDefinitions(customFieldDefinitions, "filterable");
    const unavailableDefinitions = normalizeCustomFieldCriteria(objectFilter?.customFieldCriteria)
      .filter((criterion) => Boolean(criterion.key) && !findCustomFieldQueryDefinition(definitions, criterion))
      .filter((criterion, index, criteria) => criteria.findIndex((candidate) =>
        candidate.key === criterion.key && (candidate.jsonPath ?? undefined) === (criterion.jsonPath ?? undefined)
      ) === index)
      .map(createUnavailableCustomFieldQueryDefinition);
    const editorDefinitions = [...definitions, ...unavailableDefinitions];

    return editorDefinitions.length > 0 ? createCustomFieldFilterSection(editorDefinitions) : undefined;
  }, [customFieldDefinitions, objectFilter]);
  const mergedCustomFilterSections = useMemo(
    () => generatedCustomFieldSection ? [...(customFilterSections ?? []), generatedCustomFieldSection] : customFilterSections,
    [customFilterSections, generatedCustomFieldSection]
  );

  const perPage = filter.perPage ?? 25;
  const infinitePageSize = allowInfinitePageSize && (perPage === 0 || infinitePageSizeOnly);
  const page = filter.page ?? 1;
  const effectivePerPage = infinitePageSize ? Math.max(totalCount, 1) : perPage;
  const totalPages = Math.max(1, Math.ceil(totalCount / effectivePerPage));
  const start = totalCount > 0 ? (infinitePageSize ? 1 : (page - 1) * effectivePerPage + 1) : 0;
  const end = infinitePageSize ? totalCount : Math.min(page * effectivePerPage, totalCount);
  const sortedSortOptions = useMemo(() => {
    const customSortOptions = createCustomFieldQueryDefinitions(customFieldDefinitions, "sortable")
      .map((definition) => ({
        value: definition.jsonPath
          ? `custom-json:${definition.type}:${definition.key}:${encodeURIComponent(definition.jsonPath)}`
          : `custom:${definition.type}:${definition.key}`,
        label: `Custom: ${definition.label || definition.key}`,
      }));
    const mergedOptions = [...(sortOptions ?? []), ...extensionSortOptions, ...customSortOptions];
    const knownValues = new Set(mergedOptions.map((option) => option.value));
    const unavailableCustomSortOptions = getSortClauses(filter)
      .filter((clause) => (clause.key.startsWith("custom:") || clause.key.startsWith("custom-json:")) && !knownValues.has(clause.key))
      .map((clause) => createUnavailableCustomSortOption(clause.key));
    mergedOptions.push(...unavailableCustomSortOptions);
    return mergedOptions.length > 0 ? mergedOptions.sort((left, right) => left.label.localeCompare(right.label)) : undefined;
  }, [customFieldDefinitions, extensionSortOptions, filter, sortOptions]);
  const slotContext = { pageKey, title, filter, onFilterChange, totalCount, isLoading };
  const selecting = selectedIds && selectedIds.size > 0;
  const showSelectionBar = Boolean(selectedIds && selecting);
  const showInfiniteAutoScrollControls = infinitePageSize && showAutoScrollControls;
  const contentOwnsInfiniteLoading = infinitePageSize && (displayMode === "grid" || displayMode === "wall" || displayMode === "feed" || displayMode === "vertical");
  const wakeAutoScrollControls = useCallback(() => setAutoScrollControlsAwake(true), []);

  useEffect(() => {
    if ((!infinitePageSize || !showAutoScrollControls) && autoScrollEnabled) {
      setAutoScrollEnabled(false);
    }
  }, [autoScrollEnabled, infinitePageSize, showAutoScrollControls]);

  useEffect(() => {
    if (!showInfiniteAutoScrollControls || !autoScrollControlsAwake) {
      return;
    }

    const timeoutId = window.setTimeout(() => setAutoScrollControlsAwake(false), autoScrollEnabled ? 2600 : 3600);
    return () => window.clearTimeout(timeoutId);
  }, [autoScrollControlsAwake, autoScrollEnabled, autoScrollSpeed, showInfiniteAutoScrollControls]);

  useEffect(() => {
    if (!showInfiniteAutoScrollControls || !autoScrollEnabled) {
      return;
    }

    const container = autoScrollContainerRef?.current;
    const previousSnapType = container?.style.scrollSnapType;
    let previousTime = performance.now();
    let pendingDistance = 0;
    let frameId = 0;

    if (container) {
      container.style.scrollSnapType = "none";
    }

    const step = (currentTime: number) => {
      const deltaSeconds = Math.min(0.05, Math.max(0, (currentTime - previousTime) / 1000));
      previousTime = currentTime;
      pendingDistance += autoScrollSpeed * deltaSeconds;
      const distance = Math.floor(pendingDistance);

      if (distance < 1) {
        frameId = window.requestAnimationFrame(step);
        return;
      }

      pendingDistance -= distance;

      if (container) {
        const maxScrollTop = Math.max(0, container.scrollHeight - container.clientHeight);
        if (container.scrollTop < maxScrollTop - 1) {
          container.scrollTop = Math.min(maxScrollTop, container.scrollTop + distance);
        }
      } else {
        const scrollingElement = document.scrollingElement ?? document.documentElement;
        const maxScrollTop = Math.max(0, scrollingElement.scrollHeight - window.innerHeight);
        if (window.scrollY < maxScrollTop - 1) {
          window.scrollTo({ top: Math.min(maxScrollTop, window.scrollY + distance), behavior: "auto" });
        }
      }

      frameId = window.requestAnimationFrame(step);
    };

    frameId = window.requestAnimationFrame(step);

    return () => {
      window.cancelAnimationFrame(frameId);
      if (container) {
        container.style.scrollSnapType = previousSnapType ?? "";
      }
    };
  }, [autoScrollContainerRef, autoScrollEnabled, autoScrollSpeed, showInfiniteAutoScrollControls]);

  useEffect(() => {
    if (!pageKey || restoredPrefsRef.current) {
      return;
    }

    restoredPrefsRef.current = true;

    try {
      const raw = localStorage.getItem(`cove-list-prefs-${pageKey}`);
      if (!raw) {
        return;
      }

      const parsed = JSON.parse(raw) as { perPage?: number; wallColumnCount?: number };

      if (typeof parsed.wallColumnCount === "number" && onWallColumnCountChange) {
        onWallColumnCountChange(Math.min(12, Math.max(2, parsed.wallColumnCount)));
      }

      const hasPerPageOverride = new URLSearchParams(window.location.search).has(perPageQueryKey ?? "perPage");
      const persistedPerPageAllowed = typeof parsed.perPage === "number"
        && (parsed.perPage > 0 || (allowInfinitePageSize && parsed.perPage === 0))
        && (maxPageSize == null || parsed.perPage <= maxPageSize);
      if (!hasPerPageOverride && persistedPerPageAllowed && parsed.perPage !== perPage) {
        onFilterChange({ ...filter, perPage: parsed.perPage, page: 1 });
      }
    } catch {
      // Ignore invalid persisted list preferences.
    }
  }, [allowInfinitePageSize, filter, maxPageSize, onFilterChange, onWallColumnCountChange, pageKey, perPage, perPageQueryKey]);

  useEffect(() => {
    if (!pageKey) {
      return;
    }

    localStorage.setItem(
      `cove-list-prefs-${pageKey}`,
      JSON.stringify({ perPage, wallColumnCount })
    );
  }, [pageKey, perPage, wallColumnCount]);

  const handleSearchChange = useCallback((query: string | undefined, source: ListSearchCommitSource) => {
    if (pageKey && query && source !== "clear") {
      trackInteraction({
        hostType: "collection",
        kind: "searchQuery",
        meta: {
          pageKey,
          query,
          source: "listPageToolbar",
          activeFilterCount: Object.keys(objectFilter ?? {}).length,
        },
      });
    }

    onFilterChange({ ...filter, q: query, page: 1 });
  }, [filter, objectFilter, onFilterChange, pageKey]);

  const goTo = useCallback(
    (p: number) => onFilterChange({ ...filter, page: Math.max(1, Math.min(totalPages, p)) }),
    [filter, onFilterChange, totalPages]
  );

  // List-page keyboard shortcuts
  const listBindings = useMemo(() => [
    // "/" focuses search
    { id: "list.search", keys: resolveKeybinding(keybindingOverrides, "list.search", "/"), surface: "list" as const, action: () => { document.querySelector<HTMLInputElement>("input[data-list-search='true']")?.focus(); } },
    // View switching
    ...(onDisplayModeChange && availableDisplayModes ? [
      ...(availableDisplayModes.includes("grid") ? [{ id: "list.view.grid", keys: "v g", surface: "list" as const, action: () => onDisplayModeChange("grid") }] : []),
      ...(availableDisplayModes.includes("list") ? [{ id: "list.view.list", keys: "v l", surface: "list" as const, action: () => onDisplayModeChange("list") }] : []),
      ...(availableDisplayModes.includes("wall") ? [{ id: "list.view.wall", keys: "v w", surface: "list" as const, action: () => onDisplayModeChange("wall") }] : []),
      ...(availableDisplayModes.includes("tagger") ? [{ id: "list.view.tagger", keys: "v t", surface: "list" as const, action: () => onDisplayModeChange("tagger") }] : []),
      ...(availableDisplayModes.includes("graph") ? [{ id: "list.view.graph", keys: "v h", surface: "list" as const, action: () => onDisplayModeChange("graph") }] : []),
      ...(availableDisplayModes.includes("byGroup") ? [{ id: "list.view.group", keys: "v b", surface: "list" as const, action: () => onDisplayModeChange("byGroup") }] : []),
      ...(availableDisplayModes.includes("feed") ? [{ id: "list.view.feed", keys: "v f", surface: "list" as const, action: () => onDisplayModeChange("feed") }] : []),
      ...(availableDisplayModes.includes("vertical") ? [{ id: "list.view.vertical", keys: "v k", surface: "list" as const, action: () => onDisplayModeChange("vertical") }] : []),
    ] : []),
    // Selection
    ...(onSelectAll ? [{ id: "list.select.all", keys: "s a", surface: "list" as const, action: onSelectAll }] : []),
    ...(onSelectNone ? [{ id: "list.select.none", keys: "s n", surface: "list" as const, action: onSelectNone }] : []),
    ...(onInvertSelection ? [{ id: "list.select.invert", keys: "s i", surface: "list" as const, action: onInvertSelection }] : []),
    // Pagination
    ...(showPagingControls ? [
      { id: "list.page.previous", keys: "ArrowLeft", surface: "list" as const, action: () => goTo(page - 1) },
      { id: "list.page.next", keys: "ArrowRight", surface: "list" as const, action: () => goTo(page + 1) },
      { id: "list.page.back10", keys: "Shift+ArrowLeft", surface: "list" as const, action: () => goTo(page - 10) },
      { id: "list.page.forward10", keys: "Shift+ArrowRight", surface: "list" as const, action: () => goTo(page + 10) },
      { id: "list.page.first", keys: "Ctrl+Home", surface: "list" as const, action: () => goTo(1) },
      { id: "list.page.last", keys: "Ctrl+End", surface: "list" as const, action: () => goTo(totalPages) },
    ] : []),
    // Filter dialog
    ...(mergedCriteriaDefinitions && onObjectFilterChange ? [{ id: "list.filters", keys: "", surface: "list" as const, action: () => setFilterDialogOpen(true) }] : []),
    // Zoom
    { id: "list.zoom.in", keys: "+", surface: "list" as const, action: () => setZoomLevel((v) => clampEntityCardSizeLevel(cardSizeEntityType, v + 0.25)) },
    { id: "list.zoom.out", keys: "-", surface: "list" as const, action: () => setZoomLevel((v) => clampEntityCardSizeLevel(cardSizeEntityType, v - 0.25)) },
  ], [availableDisplayModes, cardSizeEntityType, goTo, keybindingOverrides, mergedCriteriaDefinitions, onDisplayModeChange, onInvertSelection, onObjectFilterChange, onSelectAll, onSelectNone, page, setZoomLevel, showPagingControls, totalPages]);

  useKeySequence(listBindings);

  useDocumentTitle(title, manageDocumentTitle);

  const resolvedLoadState = loadState ?? resolveQueryLoadState({
    data: isLoading || error ? undefined : true,
    isPending: isLoading,
    error,
    isEmpty: () => false,
    retry: onRetry,
  });

  useEffect(() => {
    if (infinitePageSize || resolvedLoadState.status === "pending" || resolvedLoadState.status === "error" || page <= totalPages) {
      return;
    }

    onFilterChange({ ...filter, page: totalPages });
  }, [filter, infinitePageSize, onFilterChange, page, resolvedLoadState.status, totalPages]);

  return (
    <div className="list-page space-y-0">
      {/* Toolbar - matches standard FilteredListToolbar */}
      <div className="list-page-toolbar mx-1 mt-1 flex flex-wrap items-center gap-2 rounded-xl border border-border bg-surface/90 px-3 py-3 shadow-sm shadow-black/20 sm:px-2.5 sm:py-2">
        {/* Title + count + byline */}
        <div className="list-page-title-group mr-auto flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 pr-2">
          <h1 className="text-sm font-semibold text-foreground whitespace-nowrap">{title}</h1>
          <span className="text-xs text-muted hidden sm:inline">
            {resolvedLoadState.status === "pending"
              ? "Loading…"
              : resolvedLoadState.status === "error"
                ? "Unavailable"
                : totalCount > 0 ? `${start}-${end} of ${totalCount.toLocaleString()}` : "0 items"}
          </span>
          <span className="text-xs text-muted sm:hidden">
            {resolvedLoadState.status === "pending"
              ? "…"
              : resolvedLoadState.status === "error"
                ? "—"
                : totalCount > 0 ? totalCount.toLocaleString() : "0"}
          </span>
          {metadataByline}
        </div>

        {/* Search */}
        <ListSearchControl
          query={filter.q}
          onQueryChange={handleSearchChange}
          placeholder={searchPlaceholder}
          searchMode={searchMode}
          searchModes={searchModes}
          onSearchModeChange={onSearchModeChange}
          className={`list-page-search ${searchModes && searchModes.length > 1 ? "sm:w-[22rem]" : "sm:w-[18rem]"}`}
        />

        {/* Sort */}
        {sortedSortOptions && (
          <MultiSortControl
            filter={filter}
            onFilterChange={onFilterChange}
            options={sortedSortOptions}
            multiSortKeys={multiSortKeys}
          />
        )}

        {/* Saved filters */}
        {resolvedSavedFilterScope && (
          <SavedFilterMenu
            mode={resolvedSavedFilterScope}
            currentFilter={filter}
            currentObjectFilter={objectFilter}
            currentUIOptions={{ ...savedFilterUIOptions, displayMode, zoomLevel }}
            onApplyFilter={(nextFilter) => onFilterChange(withSeededRandomSort(filter, nextFilter))}
            onApplyObjectFilter={onObjectFilterChange}
            onApplyUIOptions={applySavedFilterUIOptions}
          />
        )}

        {/* Advanced filter button */}
        {mergedCriteriaDefinitions && onObjectFilterChange && (
          <FilterButton
            activeCount={countActiveObjectFilters(mergedCriteriaDefinitions, editorObjectFilter)}
            onClick={() => setFilterDialogOpen(true)}
          />
        )}

        {/* Display mode */}
        {onDisplayModeChange && availableDisplayModes && (
          <div className={`${toolbarSegmentClass} gap-0.5`}>
            {availableDisplayModes.includes("grid") && (
              <button
                onClick={() => onDisplayModeChange("grid")}
                className={`${toolbarIconButtonClass} ${displayMode === "grid" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                title="Grid"
              >
                <LayoutGrid className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("list") && (
              <button
                onClick={() => onDisplayModeChange("list")}
                className={`${toolbarIconButtonClass} ${displayMode === "list" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                title="List"
              >
                <List className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("wall") && (
              <button
                onClick={() => onDisplayModeChange("wall")}
                className={`${toolbarIconButtonClass} ${displayMode === "wall" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                title="Wall"
              >
                <Grid3X3 className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("tagger") && (
              <button
                onClick={() => onDisplayModeChange("tagger")}
                className={`${toolbarIconButtonClass} ${displayMode === "tagger" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                title="Tagger"
              >
                <Tags className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("graph") && (
              <button
                onClick={() => onDisplayModeChange("graph")}
                className={`${toolbarIconButtonClass} ${displayMode === "graph" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                title="Graph/Tree"
              >
                <Share2 className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("byGroup") && (
              <button
                onClick={() => onDisplayModeChange("byGroup")}
                className={`${toolbarIconButtonClass} ${displayMode === "byGroup" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                title="By Group"
              >
                <FolderTree className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("feed") && (
              <button
                onClick={() => onDisplayModeChange("feed")}
                className={`${toolbarIconButtonClass} ${displayMode === "feed" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                title="Feed"
              >
                <Rows3 className="w-3.5 h-3.5" />
              </button>
            )}
            {availableDisplayModes.includes("vertical") && (
              <button
                onClick={() => onDisplayModeChange("vertical")}
                className={`${toolbarIconButtonClass} ${displayMode === "vertical" ? "bg-background/60 text-accent shadow-sm" : ""}`}
                title="Vertical Viewer"
              >
                <MonitorPlay className="w-3.5 h-3.5" />
              </button>
            )}
          </div>
        )}

        {/* Per page */}
        <div className={toolbarSegmentClass}>
          <PageSizeSelect
            perPage={perPage}
            allowInfinite={allowInfinitePageSize}
            infinitePageSize={infinitePageSize}
            infinitePageSizeOnly={infinitePageSizeOnly}
            maxPageSize={maxPageSize}
            onChange={(nextPerPage) => onFilterChange({ ...filter, perPage: maxPageSize == null ? nextPerPage : Math.min(nextPerPage, maxPageSize), page: 1 })}
          />

          {/* Zoom slider (standard card size slider) */}
          {(displayMode === "grid" || displayMode === "list") && (
            <div className="hidden items-center gap-1 pl-1 md:flex">
              <ZoomOut className="w-3 h-3 text-muted" />
              <input
                type="range"
                min={0}
                max={cardSizeMaxLevel}
                step={0.25}
                value={zoomLevel}
                onChange={(e) => setZoomLevel(clampEntityCardSizeLevel(cardSizeEntityType, Number(e.target.value)))}
                style={{ "--range-fill": `${(zoomLevel / Math.max(0.25, cardSizeMaxLevel)) * 100}%` } as CSSProperties}
                className="themed-range-input h-1 w-16 cursor-pointer sm:w-20"
                title={`Card size: ${getEntityCardMinWidthPx(cardSizeEntityType, zoomLevel)}px`}
              />
              <ZoomIn className="w-3 h-3 text-muted" />
            </div>
          )}

          {displayMode === "wall" && wallColumnCount != null && onWallColumnCountChange && (
            <WallSizeControl
              sizeLevel={getWallSizeLevelFromColumnCount(wallColumnCount)}
              onChange={(sizeLevel) => onWallColumnCountChange(getWallColumnCountFromSizeLevel(sizeLevel))}
            />
          )}
        </div>

        {/* Operations */}
        <div className="list-page-operations ml-auto flex flex-wrap items-center justify-end gap-2">
          {renderOperations?.()}
          <ExtensionSlot slot="list-page-toolbar-end" context={slotContext} />
          {pageKey && <ExtensionSlot slot={`${pageKey}-list-toolbar-end`} context={slotContext} />}
          {onNew && (
            <button
              onClick={onNew}
              className="inline-flex min-h-10 items-center rounded-lg bg-accent px-3 py-2 text-sm font-medium text-white hover:bg-accent-hover sm:min-h-0 sm:py-1 sm:text-xs"
            >
              + New
            </button>
          )}
        </div>
      </div>

      {showInfiniteAutoScrollControls && (
        <div className="pointer-events-none fixed right-3 top-1/2 z-[90] -translate-y-1/2 sm:right-5 sm:top-[24%]">
          <div
            className="pointer-events-auto relative flex min-h-36 w-12 items-center justify-end"
            onPointerEnter={wakeAutoScrollControls}
            onPointerMove={wakeAutoScrollControls}
            onFocusCapture={wakeAutoScrollControls}
          >
            {!autoScrollControlsAwake && <div className="absolute right-0 h-12 w-1.5 rounded-l-full bg-accent/70 shadow-lg" aria-hidden="true" />}
            <div className={`flex flex-col items-center gap-2 rounded-xl border border-border bg-card/95 px-2 py-2 shadow-2xl backdrop-blur transition-all duration-300 ${autoScrollControlsAwake ? "translate-x-0 opacity-100" : "pointer-events-none translate-x-2 opacity-0"}`}>
            <button
              type="button"
              onClick={() => {
                wakeAutoScrollControls();
                setAutoScrollEnabled((current) => !current);
              }}
              className={`${toolbarIconButtonClass} ${autoScrollEnabled ? "bg-background/60 text-accent shadow-sm" : ""}`}
              aria-label={autoScrollEnabled ? "Pause auto-scroll" : "Start auto-scroll"}
              title={autoScrollEnabled ? "Pause auto-scroll" : "Start auto-scroll"}
            >
              {autoScrollEnabled ? <Pause className="h-4 w-4" /> : <Play className="h-4 w-4" />}
            </button>
            <input
              type="range"
              min={10}
              max={360}
              step={10}
              value={autoScrollSpeed}
              onChange={(event) => {
                wakeAutoScrollControls();
                setAutoScrollSpeed(Number(event.target.value));
              }}
              className="h-24 w-1 accent-accent [writing-mode:vertical-lr]"
              aria-label="Floating auto-scroll speed"
              title={`Auto-scroll speed: ${autoScrollSpeed}px/s`}
            />
            <span className="text-[10px] text-muted tabular-nums [writing-mode:vertical-lr]">{autoScrollSpeed}px/s</span>
            </div>
          </div>
        </div>
      )}

      {/* Active filter tags (criterion badges) */}
      {objectFilter && onObjectFilterChange && mergedCriteriaDefinitions && Object.keys(editorObjectFilter).length > 0 && (
        <ActiveObjectFilterChips
          criteriaDefinitions={mergedCriteriaDefinitions}
          objectFilter={editorObjectFilter}
          customFilterSections={mergedCustomFilterSections}
          onEdit={(key) => {
            const criterion = mergedCriteriaDefinitions.find((item) => item.id === key || item.filterKey === key || item.secondaryFilterKey === key || item.auxiliaryToggleKey === key);
            const customSection = mergedCustomFilterSections?.find((section) => section.filterKey === key);
            setFilterDialogPreselect(customSection?.id ?? criterion?.id ?? key);
            setFilterDialogOpen(true);
          }}
          onRemove={(key) => {
            if (pageKey) {
              trackInteraction({ hostType: "collection", kind: "filterClear", meta: { pageKey, source: "filterChip", criteriaKeys: [key] } });
            }
            const next = { ...editorObjectFilter };
            const criterion = mergedCriteriaDefinitions.find((item) => item.id === key
              || item.filterKey === key
              || item.secondaryFilterKey === key
              || item.auxiliaryToggleKey === key);
            if (criterion && criterion.auxiliaryToggleKey !== key) {
              delete next[criterion.filterKey];
              if (criterion.secondaryFilterKey) delete next[criterion.secondaryFilterKey];
            } else {
              delete next[key];
            }
            onObjectFilterChange(collapseExtensionCriteria(next, objectFilter));
            onFilterChange({ ...filter, page: 1 });
          }}
          onClearAll={showClearAllObjectFilters ? () => {
            if (pageKey) {
              trackInteraction({ hostType: "collection", kind: "filterClear", meta: { pageKey, source: "filterChip", clearedAll: true } });
            }
            onObjectFilterChange({});
            onFilterChange({ ...filter, page: 1 });
          } : undefined}
        />
      )}

      {/* A full-width `<pageKey>-list-row` slot below the toolbar — separate from the toolbar-end slots, which
          sit in the right-aligned operations group and cannot host a row. Empty (no extension) renders nothing. */}
      {pageKey && <ExtensionSlot slot={`${pageKey}-list-row`} context={slotContext} />}

      {/* Selection bar */}
      {showSelectionBar && (
        <div className="grid grid-cols-1 items-center gap-2 bg-card/80 border border-border rounded-lg px-3 py-1.5 mx-1 mt-1 sm:grid-cols-[1fr_auto_1fr]">
          <div className="flex min-w-0 flex-wrap items-center gap-x-2 text-xs text-secondary">
            <span>{selectedIds!.size} selected</span>
            {selectionMetadata}
          </div>
          <div className="flex flex-wrap items-center justify-center gap-3">
            {onSelectAll && <button onClick={onSelectAll} disabled={selectAllPending} className="text-xs text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-60">{selectAllPending ? "Selecting..." : selectAllLabel}</button>}
            {onSelectAllMatching && (
              <button onClick={onSelectAllMatching} disabled={selectAllMatchingPending} className="text-xs text-accent hover:underline disabled:cursor-not-allowed disabled:opacity-60">
                {selectAllMatchingPending ? "Selecting..." : selectAllMatchingLabel}
              </button>
            )}
            {onInvertSelection && <button onClick={onInvertSelection} className="text-xs text-secondary hover:text-foreground">Invert</button>}
            {onSelectNone && <button onClick={onSelectNone} className="text-xs text-secondary hover:text-foreground">Deselect all</button>}
            {selectionActions}
          </div>
          <div className="hidden sm:block" aria-hidden="true" />
        </div>
      )}

      {/* Results */}
      <QueryState
        state={resolvedLoadState}
        loading={(
          <div role="status" aria-label={`Loading ${title}`} className="flex h-64 items-center justify-center">
            <div className="h-8 w-8 animate-spin rounded-full border-b-2 border-accent" />
          </div>
        )}
        errorTitle={`Could not load ${title}`}
        errorClassName="mx-1 mt-3"
      >
        <>
          {showPagingControls && totalPages > 1 && (
            <div className="mx-1 mt-1 flex flex-wrap items-center justify-center gap-1 py-1">
              <PaginationControls page={page} totalPages={totalPages} goTo={goTo} />
            </div>
          )}
          <ListPageCardSizeContext.Provider value={{ cardMinWidthPx, zoomLevel }}>
            <div className="list-page-content pt-3" style={{ "--card-min-width": `${cardMinWidthPx}px` } as React.CSSProperties}>
              {children}
              {infinitePageSize && infiniteScroll && !contentOwnsInfiniteLoading && (
                <InfiniteScrollSentinel
                  hasMore={Boolean(infiniteScroll.hasNextPage)}
                  isLoading={Boolean(infiniteScroll.isFetchingNextPage)}
                  onLoadMore={infiniteScroll.onLoadMore}
                  loadedCount={infiniteScroll.loadedCount}
                  totalCount={infiniteScroll.totalCount}
                />
              )}
            </div>
          </ListPageCardSizeContext.Provider>
          {showPagingControls && totalPages > 1 && (
            <div className="flex flex-wrap items-center justify-center gap-1 py-4">
              <PaginationControls page={page} totalPages={totalPages} goTo={goTo} />
            </div>
          )}
        </>
      </QueryState>

      {/* Filter Dialog */}
      {mergedCriteriaDefinitions && onObjectFilterChange && (
        <FilterDialog
          open={filterDialogOpen}
          onClose={() => { setFilterDialogOpen(false); setFilterDialogPreselect(undefined); }}
          criteria={mergedCriteriaDefinitions}
          activeFilter={editorObjectFilter}
          customSections={mergedCustomFilterSections}
          showCustomSectionDivider={showCustomFilterDivider}
          onApply={(f) => {
            if (pageKey) {
              trackInteraction({
                hostType: "collection",
                kind: Object.keys(f).length === 0 ? "filterClear" : "filterApply",
                meta: {
                  pageKey,
                  source: "filterDialog",
                  criteriaKeys: Object.keys(f),
                },
              });
            }
            onObjectFilterChange(collapseExtensionCriteria(f, objectFilter));
            onFilterChange({ ...filter, page: 1 });
          }}
          preselectCriterion={filterDialogPreselect}
        />
      )}
    </div>
  );
}
