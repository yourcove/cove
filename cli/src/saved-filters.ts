import type { CoveClient } from "./client";
import { CliError } from "./errors";
import type { CoveRecord, FindFilter, ListQueryOptions, MeResponse, SavedFilter } from "./types";

export interface DefaultSavedFilter {
  mode: string;
  findFilter?: string;
  objectFilter?: string;
  uiOptions?: string;
}

const MODIFIERS: Record<string, string> = {
  EQUALS: "equals",
  NOT_EQUALS: "notEquals",
  GREATER_THAN: "greaterThan",
  LESS_THAN: "lessThan",
  INCLUDES: "includes",
  EXCLUDES: "excludes",
  INCLUDES_ALL: "includesAll",
  EXCLUDES_ALL: "excludesAll",
  IS_NULL: "isNull",
  NOT_NULL: "notNull",
  BETWEEN: "between",
  NOT_BETWEEN: "notBetween",
  MATCHES_REGEX: "matchesRegex",
  NOT_MATCHES_REGEX: "notMatchesRegex",
  UNDER_PATH: "underPath",
  NOT_UNDER_PATH: "notUnderPath",
};

function isRecord(value: unknown): value is CoveRecord {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

function isSavedFilter(value: unknown): value is SavedFilter {
  const candidate = value as Partial<SavedFilter> | undefined;
  return !!candidate && typeof candidate.id === "number" && typeof candidate.mode === "string" && typeof candidate.name === "string";
}

export async function listSavedFilters(client: CoveClient, mode?: string): Promise<SavedFilter[]> {
  const filters = await client.get<unknown>(`savedfilters${mode ? `?mode=${encodeURIComponent(mode)}` : ""}`);
  if (!Array.isArray(filters) || !filters.every(isSavedFilter)) {
    throw new CliError("INVALID_RESPONSE", "Cove returned an invalid saved-filter list response.");
  }
  return filters;
}

export async function defaultSavedFilters(client: CoveClient): Promise<DefaultSavedFilter[]> {
  const me = await client.get<MeResponse>("auth/me");
  const defaults = me?.user?.uiPreferences?.defaultFilters;
  if (!defaults || typeof defaults !== "object" || Array.isArray(defaults)) return [];
  return Object.entries(defaults).flatMap(([mode, raw]) => {
    if (typeof raw !== "string") return [];
    try {
      const parsed: unknown = JSON.parse(raw);
      if (!isRecord(parsed)) return [];
      const findFilter = isRecord(parsed.findFilter) ? JSON.stringify(parsed.findFilter) : undefined;
      const objectFilter = isRecord(parsed.objectFilter) ? JSON.stringify(parsed.objectFilter) : undefined;
      const uiOptions = isRecord(parsed.uiOptions) ? JSON.stringify(parsed.uiOptions) : undefined;
      return [{ mode, findFilter, objectFilter, uiOptions }];
    } catch {
      return [];
    }
  }).sort((left, right) => left.mode.localeCompare(right.mode));
}

export async function defaultSavedFilter(client: CoveClient, mode: string): Promise<DefaultSavedFilter | undefined> {
  const key = mode.trim().toLowerCase();
  return (await defaultSavedFilters(client)).find(filter => filter.mode.toLowerCase() === key);
}

export async function resolveSavedFilter(client: CoveClient, reference: string, mode?: string): Promise<SavedFilter> {
  if (/^\d+$/.test(reference)) {
    const filter = await client.get<unknown>(`savedfilters/${reference}`);
    if (!isSavedFilter(filter)) throw new CliError("INVALID_RESPONSE", "Cove returned an invalid saved-filter response.");
    if (mode && filter.mode.toLowerCase() !== mode.toLowerCase()) throw new CliError("SAVED_FILTER_MODE_MISMATCH", `Saved filter ${filter.id} belongs to “${filter.mode}”, not ${mode}.`);
    return filter;
  }
  const filters = await listSavedFilters(client, mode);
  const exact = filters.filter(filter => filter.name.toLowerCase() === reference.toLowerCase());
  if (exact.length === 0) throw new CliError("SAVED_FILTER_NOT_FOUND", `No saved filter exactly matches “${reference}”.`, { details: { candidates: filters.slice(0, 10).map(filter => ({ id: filter.id, mode: filter.mode, name: filter.name })) } });
  if (exact.length > 1) throw new CliError("SAVED_FILTER_AMBIGUOUS", `More than one saved filter exactly matches “${reference}”. ${mode ? "Use an ID." : "Use an ID or specify a mode."}`, { details: { candidates: exact.map(filter => ({ id: filter.id, mode: filter.mode, name: filter.name })) } });
  return exact[0]!;
}

export interface SavedFilterQuery {
  q?: string;
  sorts: NonNullable<ListQueryOptions["sorts"]>;
  seed?: number;
  objectFilter: Record<string, unknown>;
}

const ENTITY_CRITERIA: Record<string, { label: string; path: string }> = {
  performerscriterion: { label: "Performers", path: "performers" },
  tagscriterion: { label: "Tags", path: "tags" },
  studioscriterion: { label: "Studios", path: "studios" },
  studiocriterion: { label: "Studio", path: "studios" },
  groupscriterion: { label: "Groups", path: "groups" },
  groupcriterion: { label: "Group", path: "groups" },
  galleriescriterion: { label: "Galleries", path: "galleries" },
  gallerycriterion: { label: "Gallery", path: "galleries" },
  videoscriterion: { label: "Videos", path: "videos" },
  videocriterion: { label: "Video", path: "videos" },
  performertagscriterion: { label: "Performer Tags", path: "tags" },
  childrencriterion: { label: "Sub-Tags", path: "tags" },
  taggroupscriterion: { label: "Tag Group", path: "taggroups" },
};

export async function savedFilterSummary(client: CoveClient, saved: SavedFilterQuery, label = "Default filter", mode?: string): Promise<string> {
  const parts = [label];
  if (saved.q) parts.push(`Search: “${saved.q}”`);
  if (saved.sorts.length) parts.push(`Sort: ${saved.sorts.map(sort => `${humanizeKey(sort.key)} ${sort.direction === "asc" ? "ascending" : "descending"}`).join(", then ")}`);
  const processed = new Set<string>();
  for (const [key, value] of Object.entries(saved.objectFilter)) {
    if (processed.has(key.toLowerCase())) continue;
    const normalizedKey = key.toLowerCase();
    if (normalizedKey === "remoteidvaluecriterion" || normalizedKey === "remoteidcriterion") {
      processed.add("remoteidvaluecriterion");
      processed.add("remoteidcriterion");
      parts.push(`Remote ID: ${formatRemoteIdCriterion(saved.objectFilter.remoteIdValueCriterion, saved.objectFilter.remoteIdCriterion)}`);
      continue;
    }
    if (normalizedKey === "tagdurationcriterion") {
      parts.push(`Tag Duration: ${await formatTagDurationCriterion(client, value)}`);
      continue;
    }
    const entity = normalizedKey === "parentscriterion"
      ? { label: mode === "studios" ? "Parent Studios" : "Parent Tags", path: mode === "studios" ? "studios" : "tags" }
      : ENTITY_CRITERIA[normalizedKey];
    parts.push(entity ? `${entity.label}: ${await formatEntityCriterion(client, entity.path, value)}` : `${humanizeKey(key)}: ${formatCriterionValue(value)}`);
  }
  return parts.join(" · ");
}

async function formatEntityCriterion(client: CoveClient, path: string, value: unknown): Promise<string> {
  if (!isRecord(value)) return formatCriterionValue(value);
  const selectedIds = criterionIds(value.value);
  const requiredIds = criterionIds(value.requiredIds);
  const excludedIds = [...criterionIds(value.excludes), ...criterionIds(value.excludedIds)];
  const names = isRecord(value._names) ? value._names : {};
  const resolve = async (id: number) => {
    const savedName = names[String(id)];
    if (typeof savedName === "string" && savedName.trim()) return savedName;
    try {
      const entity = await client.get<unknown>(`${path}/${id}`);
      if (isRecord(entity)) {
        const name = entity.name ?? entity.title;
        if (typeof name === "string" && name.trim()) return name;
      }
    } catch { /* Keep summaries best-effort; the filter itself remains authoritative. */ }
    return `#${id}`;
  };
  const selected = await Promise.all([...new Set(selectedIds)].map(resolve));
  const required = await Promise.all([...new Set(requiredIds)].map(resolve));
  const excluded = await Promise.all([...new Set(excludedIds)].map(resolve));
  const modifier = typeof value.modifier === "string" ? value.modifier.toLowerCase() : "includes";
  const legacyExcluded = modifier === "excludes" || modifier === "excludesall";
  const includedValues = legacyExcluded ? [] : selected;
  const excludedValues = legacyExcluded ? [...selected, ...excluded] : excluded;
  const selectedText = naturalList(includedValues, modifier === "includesall" ? "and" : "or");
  const requiredText = naturalList(required, "and");
  const includedText = selectedText && requiredText ? `${selectedText}; requires ${requiredText}` : selectedText || requiredText;
  const excludedText = modifier === "excludesall"
    ? `not all of ${naturalList(excludedValues, "and")}`
    : excludedValues.length === 1 ? `not ${excludedValues[0]}` : excludedValues.length === 2 ? `neither ${naturalList(excludedValues, "nor")}` : excludedValues.length > 2 ? `none of ${naturalList(excludedValues, "or")}` : "";
  const selection = includedText && excludedText ? `${includedText} but ${excludedText}` : includedText || excludedText || formatCriterionValue(value);
  return value.depth === -1 ? `${selection} with sub-tags` : selection;
}

async function formatTagDurationCriterion(client: CoveClient, value: unknown): Promise<string> {
  if (!isRecord(value)) return formatCriterionValue(value);
  const clauses = Array.isArray(value.clauses) ? value.clauses.filter(isRecord) : [value];
  const formatted = await Promise.all(clauses.map(async clause => {
    if (typeof clause.tagId !== "number") return formatCriterionValue(clause);
    const name = await formatEntityCriterion(client, "tags", { value: [clause.tagId], modifier: "includes", _names: value._names });
    const unit = clause.unit === "percent" ? "%" : "s";
    const modifier = typeof clause.modifier === "string" ? clause.modifier.toLowerCase() : "equals";
    if ((modifier === "between" || modifier === "notbetween") && clause.value2 !== undefined) return `${name} ${modifierLabel(modifier)}${clause.value}${unit} and ${clause.value2}${unit}`;
    return `${name} ${modifierLabel(modifier)}${clause.value}${unit}`;
  }));
  return formatted.join(" · ");
}

function formatRemoteIdCriterion(value: unknown, endpoint: unknown): string {
  const valueCriterion = isRecord(value) ? value : {};
  const endpointCriterion = isRecord(endpoint) ? endpoint : {};
  const service = typeof endpointCriterion.value === "string" && endpointCriterion.value ? endpointCriterion.value : "Any metadata service";
  const modifier = typeof valueCriterion.modifier === "string" ? valueCriterion.modifier.toLowerCase() : typeof endpointCriterion.modifier === "string" ? endpointCriterion.modifier.toLowerCase() : "equals";
  if (modifier === "isnull") return `${service} Is Null`;
  if (modifier === "notnull") return `${service} Not Null`;
  return valueCriterion.value === undefined ? service : `${service} ${modifierLabel(modifier)}${valueCriterion.value}`.trim();
}

function criterionIds(value: unknown): number[] {
  if (!Array.isArray(value)) return typeof value === "number" ? [value] : [];
  return value.flatMap(item => typeof item === "number" ? [item] : isRecord(item) && typeof item.id === "number" ? [item.id] : []);
}

function naturalList(values: string[], conjunction: string): string {
  if (values.length < 2) return values[0] ?? "";
  if (values.length === 2) return `${values[0]} ${conjunction} ${values[1]}`;
  return `${values.slice(0, -1).join(", ")}, ${conjunction} ${values.at(-1)}`;
}

function humanizeKey(key: string): string {
  const words = key.replace(/criterion$/i, "").replace(/[_-]+/g, " ").replace(/([a-z0-9])([A-Z])/g, "$1 $2").trim();
  return words.replace(/\b\w/g, letter => letter.toUpperCase());
}

function formatCriterionValue(value: unknown): string {
  if (isRecord(value)) {
    const modifier = typeof value.modifier === "string" ? value.modifier.toLowerCase() : "";
    if (modifier === "isnull") return "Is Null";
    if (modifier === "notnull") return "Not Null";
    if (typeof value.value === "boolean") return value.value ? "Yes" : "No";
    if ((modifier === "between" || modifier === "notbetween") && value.value2 !== undefined) return `${modifierLabel(modifier)}${String(value.value)} and ${String(value.value2)}`.trim();
    if (value.value !== undefined) return `${modifierLabel(modifier)}${String(value.value)}`.trim();
  }
  if (typeof value === "boolean") return value ? "Yes" : "No";
  return String(value ?? "");
}

function modifierLabel(modifier: string): string {
  return ({ equals: "", notequals: "≠ ", greaterthan: "> ", lessthan: "< ", includes: "Includes ", excludes: "Excludes ", between: "Between ", notbetween: "Not Between " } as Record<string, string>)[modifier] ?? (modifier ? `${humanizeKey(modifier)} ` : "");
}

export function queryForSavedFilter(savedFilter: Pick<SavedFilter, "mode" | "findFilter" | "objectFilter">, expectedMode: string): SavedFilterQuery {
  const mode = savedFilter.mode.toLowerCase();
  if (mode !== expectedMode.toLowerCase()) throw new CliError("SAVED_FILTER_MODE_MISMATCH", `Saved filter belongs to “${savedFilter.mode}”, not ${expectedMode}.`);
  const findFilter = parseObject(savedFilter.findFilter, "findFilter") as FindFilter;
  const rawObjectFilter = parseObject(savedFilter.objectFilter, "objectFilter");
  const compilationGroups = Object.entries(rawObjectFilter).find(([key]) => key.toLowerCase() === "includecompilationgroups");
  if (mode === "videos" && criterionBoolean(compilationGroups?.[1]) === true) {
    throw new CliError("UNSUPPORTED_SAVED_FILTER", "This saved filter includes compilation groups, but this command returns videos only.");
  }
  const backendObjectFilter = mode === "videos"
    ? Object.fromEntries(Object.entries(rawObjectFilter).filter(([key]) => key.toLowerCase() !== "includecompilationgroups"))
    : rawObjectFilter;
  const q = findFilter.q ?? undefined;
  const sort = findFilter.sort ?? undefined;
  const rawDirection = findFilter.direction ?? undefined;
  const direction = typeof rawDirection === "string" ? rawDirection.toLowerCase() : rawDirection;
  const seed = findFilter.seed ?? undefined;
  const rawCompoundSorts = findFilter.sorts ?? undefined;
  if (q !== undefined && typeof q !== "string") throw invalidFindFilter();
  if (sort !== undefined && typeof sort !== "string") throw invalidFindFilter();
  if (direction !== undefined && direction !== "asc" && direction !== "desc") throw invalidFindFilter();
  if (seed !== undefined && (!Number.isInteger(seed) || seed < -2_147_483_648 || seed > 2_147_483_647)) throw invalidFindFilter();
  if (rawCompoundSorts !== undefined && (!Array.isArray(rawCompoundSorts) || !rawCompoundSorts.every(sortClause => isRecord(sortClause) && typeof sortClause.key === "string" && typeof sortClause.direction === "string" && ["asc", "desc"].includes(sortClause.direction.toLowerCase())))) {
    throw new CliError("INVALID_SAVED_FILTER", "The saved filter contains an invalid compound sort configuration.");
  }
  const compoundSorts = rawCompoundSorts?.map(sortClause => ({ key: sortClause.key, direction: sortClause.direction.toLowerCase() as "asc" | "desc" }));
  const sorts = compoundSorts?.length
    ? compoundSorts
    : [{ key: sort ?? savedFilterDefaultSort(mode), direction: (direction ?? "desc") as "asc" | "desc" }];
  if (sorts.some(sort => sort.key.toLowerCase() === "visual_match")) throw new CliError("UNSUPPORTED_SAVED_FILTER", "Visual-similarity saved filters cannot be run through a standard REST query.");
  const random = sorts[0]?.key.toLowerCase() === "random";
  return {
    ...(q === undefined ? {} : { q }),
    sorts,
    ...(random ? { seed: seed ?? Math.floor(Math.random() * 2_147_483_647) } : {}),
    objectFilter: normalizeCriteria(backendObjectFilter) as Record<string, unknown>,
  };
}

export function savedFilterDefaultSort(mode: string): string {
  if (["performers", "studios", "tags"].includes(mode)) return "latest_video_date";
  if (["videos", "audios", "images", "galleries", "groups", "texts"].includes(mode)) return "date";
  return "updated_at";
}

function invalidFindFilter(): CliError {
  return new CliError("INVALID_SAVED_FILTER", "The saved filter contains an invalid findFilter configuration.");
}

function parseObject(value: string | null | undefined, field: string): CoveRecord {
  if (!value) return {};
  try {
    const parsed: unknown = JSON.parse(value);
    if (!isRecord(parsed)) throw new Error("not an object");
    return parsed;
  } catch {
    throw new CliError("INVALID_SAVED_FILTER", `The saved filter contains invalid ${field} JSON.`);
  }
}

function normalizeCriteria(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(normalizeCriteria);
  if (!isRecord(value)) return value;
  return Object.fromEntries(Object.entries(value).map(([key, entry]) => [
    key,
    key === "modifier" && typeof entry === "string" ? MODIFIERS[entry] ?? entry : normalizeCriteria(entry),
  ]));
}

function criterionBoolean(value: unknown): boolean | undefined {
  if (typeof value === "boolean") return value;
  if (isRecord(value) && typeof value.value === "boolean") return value.value;
  return undefined;
}
