import { describe, expect, it } from "vitest";
import {
  GALLERY_CRITERIA,
  GROUP_CRITERIA,
  IMAGE_CRITERIA,
  PERFORMER_CRITERIA,
  SCENE_CRITERIA,
  STUDIO_CRITERIA,
  TAG_CRITERIA,
  type CriterionDefinition,
} from "../components/FilterDialog";

const criteriaSets = [
  ["scene", SCENE_CRITERIA],
  ["performer", PERFORMER_CRITERIA],
  ["tag", TAG_CRITERIA],
  ["studio", STUDIO_CRITERIA],
  ["gallery", GALLERY_CRITERIA],
  ["image", IMAGE_CRITERIA],
  ["group", GROUP_CRITERIA],
] as const;

function getDuplicates(values: string[]) {
  const seen = new Set<string>();
  const duplicates = new Set<string>();

  for (const value of values) {
    if (seen.has(value)) {
      duplicates.add(value);
      continue;
    }

    seen.add(value);
  }

  return [...duplicates].sort();
}

function expectUnique(criteria: CriterionDefinition[], key: keyof CriterionDefinition, entityName: string) {
  const duplicates = getDuplicates(criteria.map((criterion) => String(criterion[key])));
  expect(duplicates, `${entityName} criteria has duplicate ${String(key)} values`).toEqual([]);
}

describe("filter criteria definitions", () => {
  it.each(criteriaSets)("%s criteria keep ids, labels, and filter keys unique", (entityName, criteria) => {
    expectUnique(criteria, "id", entityName);
    expectUnique(criteria, "label", entityName);
    expectUnique(criteria, "filterKey", entityName);
  });
});