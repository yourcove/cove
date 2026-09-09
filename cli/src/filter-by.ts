import { CliError } from "./errors";

export type FilterByResource = "videos" | "audios" | "images" | "galleries" | "tags" | "performers" | "studios" | "groups" | "texts";

const STRING_OPERATORS = [
  "equals",
  "not-equals",
  "includes",
  "excludes",
  "matches-regex",
  "not-matches-regex",
  "is-null",
  "not-null",
] as const;
const PATH_OPERATORS = [...STRING_OPERATORS, "under-path", "not-under-path"] as const;
const BASIC_STRING_OPERATORS = ["equals", "not-equals", "includes", "excludes", "is-null", "not-null"] as const;
const COLLECTION_OPERATORS = ["includes", "excludes", "is-null", "not-null"] as const;
const ENUM_OPERATORS = ["equals", "not-equals"] as const;
const EQUALS_ONLY_OPERATORS = ["equals"] as const;
const UNARY_OPERATORS = new Set<string>(["is-null", "not-null"]);

type FieldKind = "string" | "path" | "basic-string" | "collection" | "enum" | "equals-only";

const FILTER_FIELDS: Record<FilterByResource, Readonly<Record<string, FieldKind>>> = {
  videos: { title: "string", code: "string", details: "string", director: "string", path: "path", hash: "string", checksum: "string", url: "collection", "video-codec": "string", "audio-codec": "string", orientation: "equals-only", captions: "string" },
  audios: { title: "string", code: "string", details: "string", path: "path", format: "basic-string", "audio-codec": "basic-string", url: "string", "track-title": "string" },
  images: { title: "string", code: "string", details: "string", photographer: "string", path: "path", checksum: "string", url: "collection", orientation: "enum" },
  galleries: { title: "string", code: "string", details: "string", photographer: "string", path: "path", checksum: "string", url: "collection" },
  tags: { name: "string", "sort-name": "string", aliases: "collection", description: "string" },
  performers: { name: "string", gender: "string", ethnicity: "string", country: "string", url: "string", disambiguation: "string", details: "string", "eye-color": "string", "hair-color": "string", measurements: "string", "fake-tits": "string", circumcised: "enum", tattoo: "string", piercings: "string", aliases: "collection" },
  studios: { name: "string", details: "string", aliases: "collection", url: "collection" },
  groups: { name: "string", director: "string", synopsis: "string", kind: "enum", aliases: "string", url: "collection", "query-source-key": "string", "allowed-host-types": "basic-string" },
  texts: { title: "string", code: "string", details: "string", content: "string", path: "path", format: "basic-string", url: "string" },
};

const FILTER_FIELD_VALUES: Partial<Record<FilterByResource, Readonly<Record<string, readonly string[]>>>> = {
  videos: { orientation: ["landscape", "portrait", "square"] },
  images: { orientation: ["landscape", "portrait", "square"] },
  performers: { circumcised: ["cut", "uncut"] },
  groups: { kind: ["static", "dynamic"] },
};

function operatorsFor(resource: FilterByResource, field: string): readonly string[] {
  switch (FILTER_FIELDS[resource][field]) {
    case "path": return PATH_OPERATORS;
    case "basic-string": return BASIC_STRING_OPERATORS;
    case "collection": return COLLECTION_OPERATORS;
    case "enum": return ENUM_OPERATORS;
    case "equals-only": return EQUALS_ONLY_OPERATORS;
    default: return STRING_OPERATORS;
  }
}

function camelCase(value: string): string {
  return value.replace(/-([a-z])/g, (_match, letter: string) => letter.toUpperCase());
}

export function filterByCompletionValues(resource: FilterByResource): string[] {
  return Object.keys(FILTER_FIELDS[resource]).flatMap(field => operatorsFor(resource, field).flatMap(operator => {
    const values = FILTER_FIELD_VALUES[resource]?.[field];
    if (values) return values.map(value => `${field}:${operator}=${value}`);
    return `${field}:${operator}${UNARY_OPERATORS.has(operator) ? "" : "="}`;
  }));
}

export function filterByObjectFilter(expressions: readonly string[], resource: FilterByResource): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  const supportedFields = Object.keys(FILTER_FIELDS[resource]);

  for (const expression of expressions) {
    const colon = expression.indexOf(":");
    const equals = expression.indexOf("=", colon + 1);
    const field = (colon < 0 ? expression : expression.slice(0, colon)).trim().toLowerCase();
    const operator = (colon < 0 ? "" : expression.slice(colon + 1, equals < 0 ? undefined : equals)).trim().toLowerCase();
    const value = equals < 0 ? undefined : expression.slice(equals + 1);

    if (!field || !operator) throw invalidExpression();
    if (!supportedFields.includes(field)) {
      throw new CliError("INVALID_ARGUMENT", `--filter-by field “${field}” is not available for ${resource}.`, { details: { supportedFields } });
    }
    if (!operatorsFor(resource, field).includes(operator as never)) {
      throw new CliError("INVALID_ARGUMENT", `--filter-by operator “${operator}” is not valid for ${field}.`, { details: { supportedOperators: operatorsFor(resource, field) } });
    }

    const unary = UNARY_OPERATORS.has(operator);
    if ((unary && value !== undefined) || (!unary && (value === undefined || value.length === 0))) throw invalidExpression();

    let criterionValue = value ?? "";
    const supportedValues = FILTER_FIELD_VALUES[resource]?.[field];
    if (supportedValues) {
      criterionValue = criterionValue.trim().toLowerCase();
      if (!supportedValues.includes(criterionValue)) {
        throw new CliError("INVALID_ARGUMENT", `--filter-by value “${value}” is not valid for ${field}.`, { details: { supportedValues } });
      }
    }

    const criterion = `${camelCase(field)}Criterion`;
    if (criterion in result) throw new CliError("FILTER_CONFLICT", `Provide only one --filter-by expression for ${field}.`);
    result[criterion] = { value: criterionValue, modifier: camelCase(operator) };
  }

  return result;
}

function invalidExpression(): CliError {
  return new CliError("INVALID_ARGUMENT", "--filter-by must use field:operator=value, or field:is-null / field:not-null.");
}
