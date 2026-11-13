import { describe, expect, it } from "vitest";
import { PERFORMER_CRITERIA } from "../components/filterCriteriaCatalogs";
import { PERFORMER_SORT_OPTIONS } from "../components/performerSortOptions";
import { visiblePerformerSortOptions, withoutSfwHiddenScrapedFields } from "../components/sfwPerformerFields";

describe("SFW performer fields", () => {
  it("marks exactly the six adult-oriented performer criteria as hidden in SFW mode", () => {
    const hidden = PERFORMER_CRITERIA.filter((criterion) => criterion.hiddenInSfwMode).map((criterion) => criterion.id);

    expect([...hidden].sort()).toEqual([
      "circumcised",
      "fakeTits",
      "measurements",
      "penisLength",
      "piercings",
      "tattoos",
    ]);
  });

  it("offers every performer sort option outside SFW mode", () => {
    expect(visiblePerformerSortOptions(PERFORMER_SORT_OPTIONS, false)).toBe(PERFORMER_SORT_OPTIONS);
  });

  it("removes the measurements sort in SFW mode", () => {
    const values = visiblePerformerSortOptions(PERFORMER_SORT_OPTIONS, true, "name").map((option) => option.value);

    expect(values).not.toContain("measurements");
    expect(values).toHaveLength(PERFORMER_SORT_OPTIONS.length - 1);
  });

  it("keeps the measurements sort listed in SFW mode while it is the sort in use", () => {
    expect(visiblePerformerSortOptions(PERFORMER_SORT_OPTIONS, true, "measurements")).toEqual(PERFORMER_SORT_OPTIONS);
  });
});

describe("withoutSfwHiddenScrapedFields", () => {
  const match = {
    id: "sample",
    scraped: { name: "Sample", ethnicity: "sample", measurements: "m", tattoos: "t", piercings: "p" },
  };

  it("leaves a tagger match untouched outside SFW mode", () => {
    expect(withoutSfwHiddenScrapedFields(match, false)).toBe(match);
  });

  it("drops the hidden scraped values in SFW mode and keeps the rest", () => {
    expect(withoutSfwHiddenScrapedFields(match, true)).toEqual({
      id: "sample",
      scraped: { name: "Sample", ethnicity: "sample" },
    });
    expect(match.scraped.tattoos).toBe("t");
  });
});
