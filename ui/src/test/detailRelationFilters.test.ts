import { describe, expect, it } from "vitest";
import { withRequiredMultiId, withRequiredSingleId } from "../utils/detailRelationFilters";

describe("detail relation filters", () => {
  it("adds recursive depth to required multi-id relations", () => {
    expect(withRequiredMultiId({} as { studiosCriterion?: unknown }, "studiosCriterion", 7, -1)).toEqual({
      studiosCriterion: { value: [7], modifier: "INCLUDES", depth: -1 },
    });
  });

  it("adds recursive depth to required single-id relations", () => {
    expect(withRequiredSingleId({} as { studiosCriterion?: unknown }, "studiosCriterion", 7, -1)).toEqual({
      studiosCriterion: { value: [7], modifier: "INCLUDES", depth: -1 },
    });
  });
});
