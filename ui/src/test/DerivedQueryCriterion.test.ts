import { describe, expect, it } from "vitest";
import { buildAppliedDerivedQuery, readDerivedSpanQueryFilter } from "../pages/segments/derivedQueryCriterion";

describe("readDerivedSpanQueryFilter", () => {
  it("reads an operand that names tags, performers or faces without a kind as that kind", () => {
    const filter = readDerivedSpanQueryFilter({
      operator: "union",
      operands: [{ tagIds: [1] }, { performerIds: [2] }, { faceIds: [3] }],
    });

    expect(filter.operands.map((operand) => operand.kind)).toEqual(["tag", "performer", "face"]);
  });

  it("keeps a stored kind, and leaves an operand naming several kinds of entity alone", () => {
    const filter = readDerivedSpanQueryFilter({
      operator: "union",
      operands: [{ kind: "Tag", tagIds: [1] }, { tagIds: [1], faceIds: [3] }, { sourceKey: "user" }],
    });

    expect(filter.operands.map((operand) => operand.kind)).toEqual(["Tag", undefined, undefined]);
  });
});

describe("buildAppliedDerivedQuery", () => {
  it("matches a performer through its faces' face segments", () => {
    const filter = readDerivedSpanQueryFilter({
      operator: "intersection",
      operands: [
        { kind: "tag", tagIds: [1] },
        { kind: "performer", performerIds: [2] },
      ],
    });

    const query = buildAppliedDerivedQuery(filter, new Map([[2, [30, 31]]]));

    expect(query?.operands).toEqual([
      { kind: "tag", tagIds: [1] },
      { kind: "face", refIds: [30, 31] },
    ]);
  });

  it("sends a link-only performer operand the same way", () => {
    const filter = readDerivedSpanQueryFilter({ operator: "union", operands: [{ performerIds: [2] }] });

    expect(buildAppliedDerivedQuery(filter, new Map([[2, [30]]]))?.operands).toEqual([{ kind: "face", refIds: [30] }]);
  });

  it("keeps a performer operand with no performer picked on performer segments", () => {
    const filter = readDerivedSpanQueryFilter({ operator: "union", operands: [{ kind: "performer" }] });

    expect(buildAppliedDerivedQuery(filter, new Map())?.operands).toEqual([{ kind: "performer" }]);
  });

  it("matches nothing for a picked performer without linked faces, whatever the case of its kind", () => {
    const filter = readDerivedSpanQueryFilter({
      operator: "union",
      operands: [{ kind: "Performer", performerIds: [2] }],
    });

    expect(buildAppliedDerivedQuery(filter, new Map([[2, []]]))?.operands).toEqual([{ kind: "face", refIds: [-1] }]);
  });
});
