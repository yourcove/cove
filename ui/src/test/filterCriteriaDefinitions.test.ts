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

  it("keeps scene filter labels and modifiers aligned with the supported UI", () => {
    const sceneCriteriaById = new Map(SCENE_CRITERIA.map((criterion) => [criterion.id, criterion]));

    expect(sceneCriteriaById.get("code")?.label).toBe("Studio Code");
    expect(sceneCriteriaById.get("oCounter")?.label).toBe("Favorites");
    expect(sceneCriteriaById.has("isMissing")).toBe(false);
    expect(sceneCriteriaById.has("interactive")).toBe(false);
    expect(sceneCriteriaById.has("interactiveSpeed")).toBe(false);
    expect(sceneCriteriaById.get("frameRate")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(sceneCriteriaById.get("orientation")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS"]);
  });

  it("does not expose unsupported performer path filtering", () => {
    expect(PERFORMER_CRITERIA.some((criterion) => criterion.id === "path")).toBe(false);
  });

  it("keeps performer count and timestamp modifiers aligned with non-null backend semantics", () => {
    const performerCriteriaById = new Map(PERFORMER_CRITERIA.map((criterion) => [criterion.id, criterion]));

    expect(performerCriteriaById.get("name")?.label).toBe("Name");
    expect(performerCriteriaById.get("gender")?.multiSelectOptions).toBe(true);
    expect(performerCriteriaById.get("studios")?.hierarchyToggleLabel).toBe("Include sub-studios");
    expect(performerCriteriaById.get("sceneCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"]);
    expect(performerCriteriaById.get("studioCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN", "IS_NULL", "NOT_NULL"]);
    expect(performerCriteriaById.get("imageCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("galleryCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("markerCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("playCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("oCounter")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("tagCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("createdAt")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("updatedAt")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("remoteId")?.label).toBe("Remote ID Provider");
  });
});