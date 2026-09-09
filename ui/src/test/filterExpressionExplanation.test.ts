import { describe, expect, it } from "vitest";
import { defaultRatingSystemOptions } from "../components/Rating";
import { describeFilterExpressionCondition } from "../components/filterExpressionExplanation";
import type { CriterionDefinition } from "../components/filterCriteriaTypes";

describe("filter expression explanations", () => {
  it("describes primitive criterion modifiers in plain language", () => {
    const criterion: CriterionDefinition = {
      id: "title",
      label: "Title",
      type: "string",
      filterKey: "titleCriterion",
    };

    expect(
      describeFilterExpressionCondition(
        {
          titleCriterion: { value: "Alpha", modifier: "INCLUDES" },
        },
        [criterion],
        defaultRatingSystemOptions,
        [],
      ),
    ).toBe("Title includes Alpha");
  });

  it("describes nested related criteria with their quantifier", () => {
    const nestedCriterion: CriterionDefinition = {
      id: "name",
      label: "Name",
      type: "string",
      filterKey: "nameCriterion",
    };
    const criterion: CriterionDefinition = {
      id: "relatedPerformers",
      label: "Related Performers",
      type: "related",
      entityType: "performers",
      filterKey: "performerFilterCriterion",
      relatedCriteria: () => [nestedCriterion],
    };

    expect(
      describeFilterExpressionCondition(
        {
          performerFilterCriterion: {
            mode: "every",
            objectFilter: {
              nameCriterion: { value: "Alpha", modifier: "EQUALS" },
            },
          },
        },
        [criterion],
        defaultRatingSystemOptions,
        [],
      ),
    ).toBe("Every performer matches all of the following — Name is Alpha");
  });
});
