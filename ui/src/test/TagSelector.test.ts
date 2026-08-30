import { describe, expect, it } from "vitest";
import { groupTagsForSelector, type SelectableTag } from "../components/TagSelector";

describe("groupTagsForSelector", () => {
  it("orders groups by priority, tags by sort name, and ungrouped tags last", () => {
    const tags: SelectableTag[] = [
      tag(1, "Ungrouped"),
      tag(2, "Zulu", 10, "First", "Alpha"),
      tag(5, "Alpha", 10, "First", "Zulu"),
      tag(3, "Second", 20, "Second"),
      tag(4, "Maximum", Number.MAX_SAFE_INTEGER, "Maximum"),
    ];

    const groups = groupTagsForSelector(tags);

    expect(groups.map((group) => group.label)).toEqual(["First", "Second", "Maximum", "Ungrouped"]);
    expect(groups[0].tags.map((item) => item.id)).toEqual([2, 5]);
  });
});

function tag(id: number, name: string, sortOrder?: number, groupName?: string, sortName?: string): SelectableTag {
  return {
    id,
    name,
    sortName,
    color: null,
    tagGroupId: sortOrder == null ? null : groupName === "First" ? 100 : id,
    tagGroupName: groupName ?? null,
    tagGroupColor: null,
    tagGroupSortOrder: sortOrder ?? null,
  };
}
