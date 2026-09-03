import { describe, expect, it } from "vitest";
import {
  AUDIO_CRITERIA,
  GALLERY_CRITERIA,
  GROUP_CRITERIA,
  IMAGE_CRITERIA,
  PERFORMER_CRITERIA,
  VIDEO_CRITERIA,
  STUDIO_CRITERIA,
  TAG_CRITERIA,
  TEXT_CRITERIA,
} from "../components/filterCriteriaCatalogs";
import type { CriterionDefinition } from "../components/filterCriteriaTypes";

const criteriaSets = [
  ["video", VIDEO_CRITERIA],
  ["performer", PERFORMER_CRITERIA],
  ["tag", TAG_CRITERIA],
  ["studio", STUDIO_CRITERIA],
  ["gallery", GALLERY_CRITERIA],
  ["image", IMAGE_CRITERIA],
  ["audio", AUDIO_CRITERIA],
  ["text", TEXT_CRITERIA],
  ["group", GROUP_CRITERIA],
  ["audio", AUDIO_CRITERIA],
  ["text", TEXT_CRITERIA],
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
  it("labels media performer tags as occurrence tags without changing their stable keys", () => {
    for (const criteria of [VIDEO_CRITERIA, GALLERY_CRITERIA, IMAGE_CRITERIA, AUDIO_CRITERIA, TEXT_CRITERIA]) {
      const criterion = criteria.find((item) => item.id === "performerTags");

      expect(criterion?.label).toBe("Performer Occurrence Tags");
      expect(criterion?.filterKey).toBe("performerTagsCriterion");
    }
  });

  it.each(criteriaSets)("%s criteria keep ids, labels, and filter keys unique", (entityName, criteria) => {
    expectUnique(criteria, "id", entityName);
    expectUnique(criteria, "label", entityName);
    expectUnique(criteria, "filterKey", entityName);
  });

  it.each([
    ["video", VIDEO_CRITERIA],
    ["image", IMAGE_CRITERIA],
    ["audio", AUDIO_CRITERIA],
    ["text", TEXT_CRITERIA],
    ["gallery", GALLERY_CRITERIA],
    ["group", GROUP_CRITERIA],
  ] as const)("%s exposes the shared Favorite boolean criterion", (_entityName, criteria) => {
    expect(criteria).toContainEqual(expect.objectContaining({
      id: "favorite",
      label: "Favorite",
      type: "bool",
      filterKey: "favoriteCriterion",
    }));
  });

  it("keeps video filter labels and modifiers aligned with the supported UI", () => {
    const videoCriteriaById = new Map(VIDEO_CRITERIA.map((criterion) => [criterion.id, criterion]));

    expect(videoCriteriaById.get("code")?.label).toBe("Studio Code");
    expect(videoCriteriaById.get("hash")?.filterKey).toBe("fingerprintCriterion");
    expect(videoCriteriaById.get("hash")?.options?.map((option) => option.value)).toEqual(["oshash", "md5", "phash"]);
    expect(videoCriteriaById.get("likeCounter")?.label).toBe("Likes");
    expect(videoCriteriaById.get("hasSegments")?.filterKey).toBe("hasSegmentsCriterion");
    expect(videoCriteriaById.has("isMissing")).toBe(false);
    expect(videoCriteriaById.has("interactive")).toBe(false);
    expect(videoCriteriaById.has("interactiveSpeed")).toBe(false);
    expect(videoCriteriaById.has("checksum")).toBe(false);
    expect(videoCriteriaById.has("hasMarkers")).toBe(false);
    expect(videoCriteriaById.get("playCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(videoCriteriaById.get("fileCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(videoCriteriaById.get("frameRate")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(videoCriteriaById.get("orientation")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS"]);
    expect(videoCriteriaById.get("studios")?.hierarchyToggleLabel).toBe("Include sub-studios");
  });

  it("offers related-entity filters instead of specialized performer-favorite filters", () => {
    for (const criteria of [VIDEO_CRITERIA, IMAGE_CRITERIA, GALLERY_CRITERIA, AUDIO_CRITERIA, TEXT_CRITERIA]) {
      expect(criteria).toContainEqual(expect.objectContaining({
        id: "relatedPerformers",
        label: "Related Performers",
        type: "related",
        entityType: "performers",
        filterKey: "performerFilterCriterion",
        category: "related",
      }));
      expect(criteria.some((criterion) => criterion.id === "performerFavorite")).toBe(false);
    }

    expect(PERFORMER_CRITERIA).toContainEqual(expect.objectContaining({
      id: "relatedVideos",
      label: "Related Videos",
      type: "related",
      entityType: "videos",
      filterKey: "videoFilterCriterion",
      category: "related",
    }));
  });

  it("does not expose unsupported performer path filtering", () => {
    expect(PERFORMER_CRITERIA.some((criterion) => criterion.id === "path")).toBe(false);
  });

  it("keeps performer count and timestamp modifiers aligned with non-null backend semantics", () => {
    const performerCriteriaById = new Map(PERFORMER_CRITERIA.map((criterion) => [criterion.id, criterion]));

    expect(performerCriteriaById.get("name")?.label).toBe("Name");
    expect(performerCriteriaById.get("gender")?.multiSelectOptions).toBe(true);
    expect(performerCriteriaById.get("studios")?.hierarchyToggleLabel).toBe("Include sub-studios");
    expect(performerCriteriaById.get("videoCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("audioCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("textCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("studioCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("imageCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("galleryCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.has("markerCount")).toBe(false);
    expect(performerCriteriaById.get("playCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("likeCounter")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("careerLength")?.label).toBe("Career Length");
    expect(performerCriteriaById.get("careerLength")?.type).toBe("careerLength");
    expect(performerCriteriaById.get("careerLength")?.modifiers).toBeUndefined();
    expect(performerCriteriaById.get("tagCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("createdAt")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("updatedAt")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(performerCriteriaById.get("remoteId")?.label).toBe("Remote ID");
    expect(performerCriteriaById.get("remoteId")?.type).toBe("remoteId");
  });

  it("keeps count-based tag, studio, gallery, image, and group criteria aligned with non-null backend semantics", () => {
    const tagCriteriaById = new Map(TAG_CRITERIA.map((criterion) => [criterion.id, criterion]));
    const studioCriteriaById = new Map(STUDIO_CRITERIA.map((criterion) => [criterion.id, criterion]));
    const galleryCriteriaById = new Map(GALLERY_CRITERIA.map((criterion) => [criterion.id, criterion]));
    const imageCriteriaById = new Map(IMAGE_CRITERIA.map((criterion) => [criterion.id, criterion]));
    const groupCriteriaById = new Map(GROUP_CRITERIA.map((criterion) => [criterion.id, criterion]));

    expect(tagCriteriaById.get("videoCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(tagCriteriaById.get("videoCount")?.auxiliaryToggleKey).toBe("videoCountIncludesChildren");
    expect(tagCriteriaById.has("markerCount")).toBe(false);
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
    expect(tagCriteriaById.get("remoteId")?.type).toBe("remoteId");

    expect(studioCriteriaById.get("childCount")?.label).toBe("Substudios Count");
    expect(studioCriteriaById.get("videoCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(studioCriteriaById.get("galleryCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(studioCriteriaById.get("groupCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);

    expect(galleryCriteriaById.get("hash")?.filterKey).toBe("fingerprintCriterion");
    expect(galleryCriteriaById.get("hash")?.options?.map((option) => option.value)).toEqual(["md5", "phash"]);
    expect(galleryCriteriaById.has("checksum")).toBe(false);
    expect(galleryCriteriaById.get("tagCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(galleryCriteriaById.get("studios")?.hierarchyToggleLabel).toBe("Include sub-studios");
    expect(imageCriteriaById.get("hash")?.filterKey).toBe("fingerprintCriterion");
    expect(imageCriteriaById.get("hash")?.options?.map((option) => option.value)).toEqual(["md5", "phash"]);
    expect(imageCriteriaById.has("checksum")).toBe(false);
    expect(imageCriteriaById.get("performerCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(imageCriteriaById.get("tagCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(imageCriteriaById.get("studios")?.hierarchyToggleLabel).toBe("Include sub-studios");
    expect(groupCriteriaById.get("videoCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(groupCriteriaById.get("tagCount")?.modifiers).toEqual(["EQUALS", "NOT_EQUALS", "GREATER_THAN", "LESS_THAN", "BETWEEN", "NOT_BETWEEN"]);
    expect(groupCriteriaById.get("studios")?.hierarchyToggleLabel).toBe("Include sub-studios");
  });
});
