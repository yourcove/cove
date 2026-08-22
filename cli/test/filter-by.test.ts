import { describe, expect, test } from "bun:test";
import { mergeObjectFilters } from "../src/entity-list";
import { filterByCompletionValues, filterByObjectFilter, type FilterByResource } from "../src/filter-by";

describe("filter-by", () => {
  test("explicit criteria replace legacy key casing", () => {
    expect(mergeObjectFilters(
      { TagsCriterion: { value: [1], modifier: "includes" }, titleCriterion: { value: "saved", modifier: "equals" } },
      { tagsCriterion: { value: [2], modifier: "includes" } },
    )).toEqual({ titleCriterion: { value: "saved", modifier: "equals" }, tagsCriterion: { value: [2], modifier: "includes" } });
  });

  test("translates text, path, and null expressions to Cove criteria", () => {
    expect(filterByObjectFilter([
      "title:excludes=[cyoa]",
      "details:includes=a=b",
      "path:under-path=/library",
      "captions:not-null",
    ], "videos")).toEqual({
      titleCriterion: { value: "[cyoa]", modifier: "excludes" },
      detailsCriterion: { value: "a=b", modifier: "includes" },
      pathCriterion: { value: "/library", modifier: "underPath" },
      captionsCriterion: { value: "", modifier: "notNull" },
    });
  });

  test("publishes command-specific field and operator completion prefixes", () => {
    const videos = filterByCompletionValues("videos");
    expect(videos).toContain("title:excludes=");
    expect(videos).toContain("path:under-path=");
    expect(videos).toContain("title:not-null");
    expect(videos).not.toContain("remote-id:equals=");
    expect(videos).toContain("orientation:equals=landscape");
    expect(videos).not.toContain("name:excludes=");
    expect(filterByCompletionValues("performers")).toContain("name:excludes=");
  });

  test("rejects malformed, unsupported, and duplicate criteria", () => {
    expect(() => filterByObjectFilter(["title:excludes"], "videos")).toThrow("field:operator=value");
    expect(() => filterByObjectFilter(["title:under-path=value"], "videos")).toThrow("not valid for title");
    expect(() => filterByObjectFilter(["format:matches-regex=mp3"], "audios")).toThrow("not valid for format");
    expect(() => filterByObjectFilter(["name:equals=value"], "videos")).toThrow("not available for videos");
    expect(() => filterByObjectFilter(["orientation:equals=diagonal"], "videos")).toThrow("not valid for orientation");
    expect(filterByObjectFilter(["orientation:equals=Portrait"], "videos")).toEqual({ orientationCriterion: { value: "portrait", modifier: "equals" } });
    expect(() => filterByObjectFilter(["title:equals=one", "title:excludes=two"], "videos")).toThrow("only one --filter-by");
  });

  test("rejects unsupported enum values before they can broaden a server query", () => {
    const cases: Array<[FilterByResource, string]> = [
      ["videos", "orientation:equals=diagonal"],
      ["images", "orientation:not-equals=diagonal"],
      ["performers", "circumcised:equals=unknown"],
      ["groups", "kind:not-equals=unknown"],
    ];

    for (const [resource, expression] of cases) {
      expect(() => filterByObjectFilter([expression], resource)).toThrow("not valid for");
    }
  });
});
