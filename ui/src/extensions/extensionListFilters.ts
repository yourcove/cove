import type { ExtensionFilterCriterion, ExtensionListFilterContribution } from "../api/types";
import type { CriterionDefinition, CriterionType } from "../components/filterCriteriaTypes";

const KEY_PREFIX = "extension-filter:";

export function extensionFilterKey(extensionId: string, filterId: string) {
  return `${KEY_PREFIX}${encodeURIComponent(extensionId)}:${encodeURIComponent(filterId)}`;
}

function parseExtensionFilterKey(key: string) {
  if (!key.startsWith(KEY_PREFIX)) return null;
  const separator = key.indexOf(":", KEY_PREFIX.length);
  if (separator < 0) return null;
  try {
    return {
      extensionId: decodeURIComponent(key.slice(KEY_PREFIX.length, separator)),
      filterId: decodeURIComponent(key.slice(separator + 1)),
    };
  } catch {
    return null;
  }
}

export function executableExtensionFilterKey(contribution: ExtensionListFilterContribution) {
  if (!contribution.filterId || contribution.entityType.trim().toLowerCase() !== "tags") return null;
  return extensionFilterKey(contribution.extensionId, contribution.filterId);
}

function toUiModifier(value: string) {
  return value
    .replace(/([a-z])([A-Z])/g, "$1_$2")
    .replaceAll("-", "_")
    .toUpperCase();
}

function toApiModifier(value: unknown) {
  return String(value ?? "equals")
    .toLowerCase()
    .replaceAll("_", "");
}

function isExtensionCriterion(value: unknown): value is ExtensionFilterCriterion {
  if (!value || typeof value !== "object") return false;
  const criterion = value as Record<string, unknown>;
  return (
    typeof criterion.extensionId === "string" &&
    criterion.extensionId.trim().length > 0 &&
    typeof criterion.filterId === "string" &&
    criterion.filterId.trim().length > 0 &&
    typeof criterion.modifier === "string" &&
    criterion.modifier.trim().length > 0 &&
    Object.hasOwn(criterion, "value")
  );
}

function malformedExtensionCriteria(filter: Record<string, unknown>) {
  return Array.isArray(filter.extensionCriteria)
    ? filter.extensionCriteria.filter((criterion) => !isExtensionCriterion(criterion))
    : [];
}

export function expandExtensionCriteria(filter: Record<string, unknown>) {
  const expanded = { ...filter };
  const criteria = Array.isArray(filter.extensionCriteria) ? filter.extensionCriteria : [];
  delete expanded.extensionCriteria;
  for (const criterion of criteria) {
    if (!isExtensionCriterion(criterion)) continue;
    expanded[extensionFilterKey(criterion.extensionId, criterion.filterId)] = {
      modifier: toUiModifier(criterion.modifier),
      value: criterion.value,
    };
  }
  const malformed = malformedExtensionCriteria(filter);
  if (malformed.length > 0) expanded.extensionCriteria = malformed;
  return expanded;
}

export function collapseExtensionCriteria(filter: Record<string, unknown>, preservedSource?: Record<string, unknown>) {
  const collapsed: Record<string, unknown> = {};
  const extensionCriteria: ExtensionFilterCriterion[] = [];
  for (const [key, value] of Object.entries(filter)) {
    const owned = parseExtensionFilterKey(key);
    if (!owned) {
      collapsed[key] = value;
      continue;
    }
    if (!value || typeof value !== "object") continue;
    const criterion = value as Record<string, unknown>;
    extensionCriteria.push({
      ...owned,
      modifier: toApiModifier(criterion.modifier),
      value: criterion.value,
    });
  }
  const preserved = malformedExtensionCriteria(preservedSource ?? filter);
  if (preserved.length > 0 || extensionCriteria.length > 0) {
    collapsed.extensionCriteria = [...preserved, ...extensionCriteria];
  }
  return collapsed;
}

function inferCriterionType(value: unknown): CriterionType {
  if (typeof value === "boolean") return "bool";
  if (typeof value === "number") return "number";
  return "string";
}

export function unavailableExtensionCriterionDefinitions(
  filter: Record<string, unknown>,
  contributions: ExtensionListFilterContribution[],
): CriterionDefinition[] {
  const declared = new Set(
    contributions.map(executableExtensionFilterKey).filter((key): key is string => key !== null),
  );
  const criteria = Array.isArray(filter.extensionCriteria) ? filter.extensionCriteria.filter(isExtensionCriterion) : [];
  return criteria
    .filter((criterion) => !declared.has(extensionFilterKey(criterion.extensionId, criterion.filterId)))
    .map((criterion) => ({
      id: extensionFilterKey(criterion.extensionId, criterion.filterId),
      filterKey: extensionFilterKey(criterion.extensionId, criterion.filterId),
      label: `Unavailable extension filter (${criterion.extensionId}/${criterion.filterId})`,
      type: inferCriterionType(criterion.value),
      supported: false,
      unsupportedReason: "Install or enable the owning extension to execute this saved filter.",
    }));
}
