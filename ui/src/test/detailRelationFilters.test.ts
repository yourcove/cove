import { describe, expect, it } from "vitest";
import { withRequiredMultiId, withRequiredSingleId } from "../utils/detailRelationFilters";

describe("detail relation filters", () => {
  it("adds recursive depth to required multi-id relations", () => {
    expect(withRequiredMultiId({} as { studiosCriterion?: unknown }, "studiosCriterion", 7, -1)).toEqual({
      studiosCriterion: { value: [], modifier: "INCLUDES", requiredIds: [7], requiredIdsDepth: -1 },
    });
  });

  it("adds recursive depth to required single-id relations", () => {
    expect(withRequiredSingleId({} as { studiosCriterion?: unknown }, "studiosCriterion", 7, -1)).toEqual({
      studiosCriterion: { value: [], modifier: "INCLUDES", requiredIds: [7], requiredIdsDepth: -1 },
    });
  });

  it("preserves modifier-only null criteria while adding the parent constraint", () => {
    expect(withRequiredMultiId(
      { tagsCriterion: { modifier: "IS_NULL" } } as { tagsCriterion?: unknown },
      "tagsCriterion",
      7,
    )).toEqual({
      tagsCriterion: { value: [], modifier: "IS_NULL", requiredIds: [7] },
    });
  });
});
