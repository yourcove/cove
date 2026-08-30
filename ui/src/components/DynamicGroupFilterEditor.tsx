import { useMemo, useState } from "react";
import { ArrowDown, ArrowUp, SlidersHorizontal } from "lucide-react";
import type { FindFilter } from "../api/types";
import { AUDIO_CRITERIA, FilterDialog, IMAGE_CRITERIA, VIDEO_CRITERIA, TEXT_CRITERIA, type CriterionDefinition } from "./FilterDialog";
import { Field } from "./EditModal";

export const FILTER_DYNAMIC_SOURCE_KEY = "filter";

/** Source keys for system-managed dynamic groups that cannot be deleted. */
export const PROTECTED_BUILTIN_GROUP_SOURCE_KEYS = ["save-for-later", "watch-history", "continue-watching"];

export function isProtectedBuiltInGroup(querySourceKey?: string | null) {
  return !!querySourceKey && PROTECTED_BUILTIN_GROUP_SOURCE_KEYS.includes(querySourceKey);
}

const DEFAULT_FIND_FILTER: FindFilter = {
  page: 1,
  perPage: 40,
  sort: "updated_at",
  direction: "desc",
};

const VIDEO_SORT_OPTIONS = [
  { value: "updated_at", label: "Recently Updated" },
  { value: "created_at", label: "Recently Added" },
  { value: "date", label: "Date" },
  { value: "title", label: "Title" },
  { value: "rating", label: "Rating" },
  { value: "duration", label: "Duration" },
  { value: "path", label: "Path" },
];

const SORT_OPTIONS_BY_ENTITY: Record<string, { value: string; label: string }[]> = {
  video: VIDEO_SORT_OPTIONS,
  image: [
    { value: "updated_at", label: "Recently Updated" },
    { value: "created_at", label: "Recently Added" },
    { value: "date", label: "Date" },
    { value: "title", label: "Title" },
    { value: "rating", label: "Rating" },
    { value: "path", label: "Path" },
  ],
  audio: [
    { value: "updated_at", label: "Recently Updated" },
    { value: "created_at", label: "Recently Added" },
    { value: "date", label: "Date" },
    { value: "title", label: "Title" },
    { value: "duration", label: "Duration" },
    { value: "rating", label: "Rating" },
    { value: "file_size", label: "File Size" },
    { value: "file_mod_time", label: "File Modified" },
    { value: "file_count", label: "File Count" },
    { value: "path", label: "Path" },
    { value: "bitrate", label: "Bitrate" },
    { value: "track_count", label: "Track Count" },
    { value: "tag_count", label: "Tag Count" },
    { value: "performer_count", label: "Performer Count" },
  ],
  text: [
    { value: "updated_at", label: "Recently Updated" },
    { value: "created_at", label: "Recently Added" },
    { value: "date", label: "Date" },
    { value: "title", label: "Title" },
    { value: "words", label: "Word Count" },
    { value: "pages", label: "Page Count" },
    { value: "rating", label: "Rating" },
    { value: "file_size", label: "File Size" },
    { value: "file_mod_time", label: "File Modified" },
    { value: "file_count", label: "File Count" },
    { value: "path", label: "Path" },
    { value: "tag_count", label: "Tag Count" },
    { value: "performer_count", label: "Performer Count" },
  ],
  segment: [
    { value: "updated_at", label: "Recently Updated" },
    { value: "created_at", label: "Recently Added" },
    { value: "title", label: "Title" },
    { value: "start_sec", label: "Start Time" },
    { value: "end_sec", label: "End Time" },
    { value: "duration", label: "Duration" },
    { value: "confidence", label: "Confidence" },
    { value: "kind", label: "Kind" },
    { value: "source_key", label: "Source" },
    { value: "source_run_id", label: "Source Run" },
    { value: "tag_name", label: "Tag" },
    { value: "performer", label: "Performer" },
    { value: "ref", label: "Face/Reference" },
    { value: "video_title", label: "Video Title" },
    { value: "host_type", label: "Host Type" },
    { value: "host_id", label: "Host ID" },
  ],
};

const SEGMENT_NUMBER_MODIFIERS = ["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"] as const;

const SEGMENT_CRITERIA: CriterionDefinition[] = [
  { id: "videoTitle", label: "Video Title", type: "string", filterKey: "videoTitleCriterion" },
  { id: "videos", label: "Videos", type: "multiId", entityType: "videos", filterKey: "videosCriterion" },
  { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
  { id: "hostType", label: "Host Type", type: "enum", filterKey: "hostTypeCriterion", modifiers: ["EQUALS", "NOT_EQUALS"], options: [{ value: "video", label: "Video" }, { value: "image", label: "Image" }, { value: "audio", label: "Audio" }] },
  { id: "sourceCategory", label: "Source Category", type: "enum", filterKey: "sourceCategoryCriterion", modifiers: ["EQUALS", "NOT_EQUALS"], options: [{ value: "extensions", label: "Extensions" }, { value: "user", label: "User-created" }] },
  { id: "kind", label: "Kind", type: "string", filterKey: "kindCriterion" },
  { id: "sourceKey", label: "Source", type: "string", filterKey: "sourceKeyCriterion" },
  { id: "sourceRunId", label: "Source Run", type: "string", filterKey: "sourceRunIdCriterion" },
  { id: "colorHint", label: "Color Hint", type: "string", filterKey: "colorHintCriterion" },
  { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
  { id: "performers", label: "Performers", type: "multiId", entityType: "performers", filterKey: "performersCriterion" },
  { id: "faces", label: "Faces", type: "multiId", entityType: "faces", filterKey: "facesCriterion" },
  { id: "hasImage", label: "Has Image", type: "bool", filterKey: "hasImageCriterion" },
  { id: "hasPayload", label: "Has Payload", type: "bool", filterKey: "hasPayloadCriterion" },
  { id: "startSec", label: "Start Time", type: "duration", filterKey: "startSecCriterion", modifiers: [...SEGMENT_NUMBER_MODIFIERS] },
  { id: "endSec", label: "End Time", type: "duration", filterKey: "endSecCriterion", modifiers: [...SEGMENT_NUMBER_MODIFIERS] },
  { id: "duration", label: "Duration", type: "duration", filterKey: "durationCriterion", modifiers: [...SEGMENT_NUMBER_MODIFIERS] },
  { id: "confidence", label: "Confidence", type: "number", filterKey: "confidenceCriterion", modifiers: [...SEGMENT_NUMBER_MODIFIERS] },
  { id: "createdAt", label: "Created At", type: "timestamp", filterKey: "createdAtCriterion" },
  { id: "updatedAt", label: "Updated At", type: "timestamp", filterKey: "updatedAtCriterion" },
];

const ENTITY_OPTIONS = [
  { value: "video", label: "Videos" },
  { value: "image", label: "Images" },
  { value: "audio", label: "Audios" },
  { value: "text", label: "Texts" },
  { value: "segment", label: "Segments" },
] as const;

type DynamicEntityType = typeof ENTITY_OPTIONS[number]["value"];

interface DynamicGroupFilterQuery {
  entityType?: string;
  entityTypes?: string[];
  findFilter?: FindFilter;
  findFilters?: Record<string, FindFilter>;
  objectFilter?: Record<string, unknown>;
  objectFilters?: Record<string, Record<string, unknown>>;
}

interface ParsedDynamicGroupFilterQuery {
  entityTypes: DynamicEntityType[];
  findFilters: Record<string, FindFilter>;
  objectFilters: Record<string, Record<string, unknown>>;
}

interface DynamicGroupFilterEditorProps {
  queryJson?: string | null;
  onChange: (queryJson: string) => void;
}

export function defaultDynamicGroupFilterQueryJson() {
  return serializeDynamicGroupFilterQuery(["video"], { video: DEFAULT_FIND_FILTER }, {});
}

export function parseDynamicGroupFilterQuery(queryJson?: string | null): ParsedDynamicGroupFilterQuery {
  if (!queryJson) {
    return { entityTypes: ["video"], findFilters: { video: DEFAULT_FIND_FILTER }, objectFilters: {} };
  }

  try {
    const parsed = JSON.parse(queryJson) as DynamicGroupFilterQuery;
    const entityTypes = normalizeEntityTypes(parsed.entityTypes?.length ? parsed.entityTypes : [parsed.entityType ?? "video"]);
    return {
      entityTypes,
      findFilters: normalizeFindFilters(parsed, entityTypes),
      objectFilters: normalizeObjectFilters(parsed, entityTypes),
    };
  } catch {
    return { entityTypes: ["video"], findFilters: { video: DEFAULT_FIND_FILTER }, objectFilters: {} };
  }
}

export function serializeDynamicGroupFilterQuery(entityTypes: string[], findFilters: Record<string, FindFilter>, objectFilters: Record<string, Record<string, unknown>>) {
  const normalizedEntityTypes = normalizeEntityTypes(entityTypes);
  const cleanedFindFilters = Object.fromEntries(normalizedEntityTypes.map((entityType) => {
    const cleanedFindFilter = normalizeFindFilter(findFilters[entityType], entityType);
    if (!cleanedFindFilter.q) {
      delete cleanedFindFilter.q;
    }
    return [entityType, cleanedFindFilter] as const;
  }));

  const cleanedObjectFilters = Object.fromEntries(
    Object.entries(objectFilters)
      .map(([entityType, filter]) => [normalizeEntityType(entityType), filter] as const)
      .filter(([entityType, filter]) => normalizedEntityTypes.includes(entityType) && Object.keys(filter).length > 0),
  );

  return JSON.stringify({
    entityTypes: normalizedEntityTypes,
    findFilters: cleanedFindFilters,
    objectFilters: Object.keys(cleanedObjectFilters).length > 0 ? cleanedObjectFilters : undefined,
  });
}

export function DynamicGroupFilterEditor({ queryJson, onChange }: DynamicGroupFilterEditorProps) {
  const [filterOpenFor, setFilterOpenFor] = useState<DynamicEntityType | null>(null);
  const query = useMemo(() => parseDynamicGroupFilterQuery(queryJson), [queryJson]);
  const entityTypes = query.entityTypes;
  const findFilters = query.findFilters;
  const objectFilters = query.objectFilters;
  const openCriteriaDefinitions = filterOpenFor ? getCriteriaDefinitions(filterOpenFor) : null;

  const updateEntityTypes = (nextEntityTypes: string[]) => {
    const normalizedEntityTypes = normalizeEntityTypes(nextEntityTypes);
    const nextFindFilters = Object.fromEntries(normalizedEntityTypes.map((entityType) => [entityType, findFilters[entityType] ?? normalizeFindFilter(undefined, entityType)] as const));
    const nextFilters = Object.fromEntries(Object.entries(objectFilters).filter(([entityType]) => normalizedEntityTypes.includes(normalizeEntityType(entityType))));
    onChange(serializeDynamicGroupFilterQuery(normalizedEntityTypes, nextFindFilters, nextFilters));
  };
  const updateFindFilter = (entityType: DynamicEntityType, next: FindFilter) => onChange(serializeDynamicGroupFilterQuery(entityTypes, { ...findFilters, [entityType]: next }, objectFilters));
  const updateObjectFilter = (entityType: DynamicEntityType, next: Record<string, unknown>) => onChange(serializeDynamicGroupFilterQuery(entityTypes, findFilters, { ...objectFilters, [entityType]: next }));

  return (
    <div className="rounded-lg border border-border bg-card/60 p-3">
      <div>
        <Field label="Entity types">
          <div className="flex min-h-10 flex-wrap gap-1.5">
            {ENTITY_OPTIONS.map((option) => {
              const selected = entityTypes.includes(option.value);
              return (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => updateEntityTypes(selected ? entityTypes.filter((entityType) => entityType !== option.value) : [...entityTypes, option.value])}
                  className={`rounded border px-2.5 py-1.5 text-sm transition-colors ${selected ? "border-accent bg-accent text-white" : "border-border bg-input text-secondary hover:text-foreground"}`}
                  aria-pressed={selected}
                >
                  {option.label}
                </button>
              );
            })}
          </div>
        </Field>
      </div>
      <div className="mt-3 space-y-3">
        {entityTypes.map((entityType) => {
          const criteriaDefinitions = getCriteriaDefinitions(entityType);
          const findFilter = findFilters[entityType] ?? normalizeFindFilter(undefined, entityType);
          const sortOptions = getSortOptions(entityType);
          const activeCriteriaCount = Object.keys(objectFilters[entityType] ?? {}).length;
          const label = ENTITY_OPTIONS.find((option) => option.value === entityType)?.label ?? entityType;
          return (
            <div key={entityType} className="rounded border border-border bg-surface/45 p-3">
              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-muted">{label}</div>
              <div className="grid gap-3 md:grid-cols-[1fr_minmax(180px,220px)_auto_auto] md:items-end">
                <Field label="Search">
                  <input
                    type="text"
                    value={findFilter.q ?? ""}
                    onChange={(event) => updateFindFilter(entityType, { ...findFilter, q: event.target.value || undefined, page: 1 })}
                    placeholder={`${label} keyword search`}
                    className="w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none"
                  />
                </Field>
                <Field label="Sort">
                  <select
                    value={findFilter.sort ?? DEFAULT_FIND_FILTER.sort}
                    onChange={(event) => updateFindFilter(entityType, { ...findFilter, sort: event.target.value, page: 1 })}
                    className="w-full rounded border border-border bg-input px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none"
                  >
                    {sortOptions.map((option) => (
                      <option key={option.value} value={option.value}>{option.label}</option>
                    ))}
                  </select>
                </Field>
                <button
                  type="button"
                  onClick={() => updateFindFilter(entityType, { ...findFilter, direction: findFilter.direction === "asc" ? "desc" : "asc", page: 1 })}
                  className="inline-flex h-10 items-center justify-center rounded border border-border bg-input px-3 text-secondary transition-colors hover:text-foreground"
                  title={findFilter.direction === "asc" ? "Ascending" : "Descending"}
                >
                  {findFilter.direction === "desc" ? <ArrowDown className="h-4 w-4" /> : <ArrowUp className="h-4 w-4" />}
                </button>
                {criteriaDefinitions ? (
                  <button
                    type="button"
                    onClick={() => setFilterOpenFor(entityType)}
                    className="inline-flex h-10 items-center justify-center gap-2 rounded border border-border bg-input px-3 text-sm text-foreground transition-colors hover:border-accent"
                  >
                    <SlidersHorizontal className="h-4 w-4" />
                    Filters
                    {activeCriteriaCount > 0 ? <span className="rounded-full bg-accent px-1.5 py-0.5 text-[10px] font-bold text-white">{activeCriteriaCount}</span> : null}
                  </button>
                ) : null}
              </div>
            </div>
          );
        })}
      </div>
      {filterOpenFor && openCriteriaDefinitions ? (
        <FilterDialog
          open={filterOpenFor != null}
          onClose={() => setFilterOpenFor(null)}
          criteria={openCriteriaDefinitions}
          activeFilter={objectFilters[filterOpenFor] ?? {}}
          supportsFilterExpressions={filterOpenFor === "video"}
          subjectLabel={`${filterOpenFor}s`}
          onApply={(next) => updateObjectFilter(filterOpenFor, next)}
        />
      ) : null}
    </div>
  );
}

function normalizeEntityTypes(entityTypes: string[]): DynamicEntityType[] {
  const values = entityTypes
    .map(normalizeEntityType)
    .filter((entityType, index, all) => all.indexOf(entityType) === index);
  return values.length > 0 ? values : ["video"];
}

function normalizeEntityType(entityType?: string | null): DynamicEntityType {
  const normalized = (entityType || "video").trim().toLowerCase();
  const singular = normalized.endsWith("s") ? normalized.slice(0, -1) : normalized;
  return ENTITY_OPTIONS.find((option) => option.value === singular)?.value ?? "video";
}

function normalizeFindFilters(parsed: DynamicGroupFilterQuery, entityTypes: DynamicEntityType[]) {
  return Object.fromEntries(entityTypes.map((entityType) => {
    const findFilter = parsed.findFilters?.[entityType] ?? parsed.findFilters?.[`${entityType}s`] ?? parsed.findFilter;
    return [entityType, normalizeFindFilter(findFilter, entityType)] as const;
  }));
}

function normalizeFindFilter(findFilter: FindFilter | undefined, entityType: DynamicEntityType) {
  const sortOptions = getSortOptions(entityType);
  const sort = sortOptions.some((option) => option.value === findFilter?.sort) ? findFilter?.sort : sortOptions[0].value;
  return { ...DEFAULT_FIND_FILTER, ...(findFilter ?? {}), sort, page: 1 };
}

function normalizeObjectFilters(parsed: DynamicGroupFilterQuery, entityTypes: DynamicEntityType[]) {
  const objectFilters: Record<string, Record<string, unknown>> = {};
  if (parsed.objectFilters && typeof parsed.objectFilters === "object") {
    for (const [entityType, filter] of Object.entries(parsed.objectFilters)) {
      const normalizedEntityType = normalizeEntityType(entityType);
      if (entityTypes.includes(normalizedEntityType) && filter && typeof filter === "object") {
        objectFilters[normalizedEntityType] = filter;
      }
    }
  }

  if (parsed.objectFilter && typeof parsed.objectFilter === "object") {
    const legacyEntityType = normalizeEntityType(parsed.entityType);
    if (entityTypes.includes(legacyEntityType)) {
      objectFilters[legacyEntityType] = parsed.objectFilter;
    }
  }

  return objectFilters;
}

function getSortOptions(entityType: DynamicEntityType) {
  return SORT_OPTIONS_BY_ENTITY[entityType] ?? SORT_OPTIONS_BY_ENTITY.video;
}

function getCriteriaDefinitions(entityType: DynamicEntityType): CriterionDefinition[] | null {
  switch (entityType) {
    case "video": return VIDEO_CRITERIA;
    case "image": return IMAGE_CRITERIA;
    case "audio": return AUDIO_CRITERIA;
    case "text": return TEXT_CRITERIA;
    case "segment": return SEGMENT_CRITERIA;
    default: return null;
  }
}
