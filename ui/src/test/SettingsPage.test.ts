import { describe, expect, it } from "vitest";
import { mergeTaskSelectablePaths } from "../pages/SettingsPage";

describe("mergeTaskSelectablePaths", () => {
  it("defaults to all selectable paths when there is no stored selection", () => {
    expect(mergeTaskSelectablePaths(undefined, ["/library/a", "/library/b"], [])).toEqual([
      "/library/a",
      "/library/b",
    ]);
  });

  it("auto-selects newly added library roots without re-selecting previously deselected ones", () => {
    expect(
      mergeTaskSelectablePaths(["/library/a"], ["/library/a", "/library/b", "/library/c"], ["/library/a", "/library/b"])
    ).toEqual([
      "/library/a",
      "/library/c",
    ]);
  });

  it("prunes paths that are no longer selectable", () => {
    expect(
      mergeTaskSelectablePaths(["/library/a", "/library/b"], ["/library/b"], ["/library/a", "/library/b"])
    ).toEqual([
      "/library/b",
    ]);
  });
});