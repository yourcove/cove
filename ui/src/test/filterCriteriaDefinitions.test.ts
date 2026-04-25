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
    expect(sceneCriteriaById.get("hash")?.filterKey).toBe("fingerprintCriterion");
    expect(sceneCriteriaById.get("hash")?.options?.map((option) => option.value)).toEqual(["oshash", "md5", "phash"]);
    expect(sceneCriteriaById.get("oCounter")?.label).toBe("Favorites");
    expect(sceneCriteriaById.has("isMissing")).toBe(false);
    expect(sceneCriteriaById.has("interactive")).toBe(false);
    expect(sceneCriteriaById.has("interactiveSpeed")).toBe(false);
    expect(sceneCriteriaById.has("checksum")).toBe(false);
    expect(sceneCriteriaById.get("playCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(sceneCriteriaById.get("fileCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
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
    expect(performerCriteriaById.get("sceneCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("studioCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("imageCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("galleryCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("markerCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("playCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("oCounter")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("tagCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("createdAt")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("updatedAt")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("remoteId")?.label).toBe("Remote ID");
    expect(performerCriteriaById.get("remoteIdProvider")?.label).toBe("Remote ID Provider");
  });

  it("keeps count-based tag, studio, gallery, image, and group criteria aligned with non-null backend semantics", () => {
    const tagCriteriaById = new Map(TAG_CRITERIA.map((criterion) => [criterion.id, criterion]));
    const studioCriteriaById = new Map(STUDIO_CRITERIA.map((criterion) => [criterion.id, criterion]));
    const galleryCriteriaById = new Map(GALLERY_CRITERIA.map((criterion) => [criterion.id, criterion]));
    const imageCriteriaById = new Map(IMAGE_CRITERIA.map((criterion) => [criterion.id, criterion]));
    const groupCriteriaById = new Map(GROUP_CRITERIA.map((criterion) => [criterion.id, criterion]));

    expect(tagCriteriaById.get("sceneCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(tagCriteriaById.get("sceneCount")?.auxiliaryToggleKey).toBe("sceneCountIncludesChildren");
    expect(tagCriteriaById.get("markerCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(tagCriteriaById.get("markerCount")?.auxiliaryToggleKey).toBe("markerCountIncludesChildren");
    expect(tagCriteriaById.get("performerCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(tagCriteriaById.get("performerCount")?.auxiliaryToggleKey).toBe("performerCountIncludesChildren");
    expect(tagCriteriaById.get("children")?.label).toBe("Sub-Tags");
    expect(tagCriteriaById.get("imageCount")?.auxiliaryToggleKey).toBe("imageCountIncludesChildren");
    expect(tagCriteriaById.get("galleryCount")?.auxiliaryToggleKey).toBe("galleryCountIncludesChildren");
    expect(tagCriteriaById.get("studioCount")?.auxiliaryToggleKey).toBe("studioCountIncludesChildren");
    expect(tagCriteriaById.get("groupCount")?.auxiliaryToggleKey).toBe("groupCountIncludesChildren");
    expect(tagCriteriaById.get("childCount")?.label).toBe("Sub-Tag Count");
    expect(tagCriteriaById.get("childCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(tagCriteriaById.get("remoteId")?.label).toBe("Remote ID");
    expect(tagCriteriaById.get("remoteIdProvider")?.label).toBe("Remote ID Provider");

    expect(studioCriteriaById.get("childCount")?.label).toBe("Substudios Count");
    expect(studioCriteriaById.get("sceneCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(studioCriteriaById.get("galleryCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(studioCriteriaById.get("groupCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);

    expect(galleryCriteriaById.get("hash")?.filterKey).toBe("fingerprintCriterion");
    expect(galleryCriteriaById.get("hash")?.options?.map((option) => option.value)).toEqual(["md5", "phash"]);
    expect(galleryCriteriaById.has("checksum")).toBe(false);
    expect(galleryCriteriaById.get("tagCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(imageCriteriaById.get("hash")?.filterKey).toBe("fingerprintCriterion");
    expect(imageCriteriaById.get("hash")?.options?.map((option) => option.value)).toEqual(["md5", "phash"]);
    expect(imageCriteriaById.has("checksum")).toBe(false);
    expect(imageCriteriaById.get("performerCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(imageCriteriaById.get("tagCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(groupCriteriaById.get("sceneCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(groupCriteriaById.get("tagCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
  });
});