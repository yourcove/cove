import { describe, expect, it } from "vitest";

import { getEditableTagIds, getLockedTagIds, mergeTagIds } from "../utils/tags";

describe("tag edit helpers", () => {
  it("separates removable tags from derived locked tags", () => {
    const tags = [{ id: 1, canRemove: true }, { id: 2, canRemove: false }, { id: 3 }];

    expect(getEditableTagIds(tags)).toEqual([1, 3]);
    expect(getLockedTagIds(tags)).toEqual([2]);
  });

  it("merges selected and locked ids without invalid duplicates", () => {
    expect(mergeTagIds([1, 2], [2, 3], [0, -1, 4.5])).toEqual([1, 2, 3]);
  });
});
