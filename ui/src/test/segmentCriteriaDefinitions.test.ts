import { describe, expect, it } from "vitest";
import { SEGMENT_CRITERIA } from "../pages/segments/segmentCriteriaDefinitions";

describe("segment criteria definitions", () => {
  it("keeps parent video tags distinct from raw segment tags", () => {
    expect(SEGMENT_CRITERIA).toContainEqual(expect.objectContaining({
      id: "videoTags",
      label: "Video Tags",
      entityType: "tags",
      filterKey: "videoTagsCriterion",
    }));
  });
});
