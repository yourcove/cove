import { describe, expect, it } from "vitest";
import type { Face, ResolvedSpan, Segment } from "../api/types";
import { assignPersonColors, spanPerson, spanShownName } from "../pages/videoDetail/segmentPeople";

function raw(id: number, overrides: Partial<Segment> = {}): Segment {
  return {
    id,
    hostId: 1,
    startSec: 0,
    endSec: 5,
    kind: "face",
    refId: id,
    title: `face-00${id}`,
    refLabel: `face-00${id}`,
    sourceKey: "ext:ai.faces",
    ...overrides,
  } as Segment;
}

function face(id: number, overrides: Partial<Face> = {}): Face {
  return { id, label: `face-00${id}`, primarySourceKey: `face-00${id}`, ignored: false, ...overrides } as Face;
}

function personOf(segment: Segment, faces: Face[]) {
  const span = { kind: segment.kind, sourceKey: segment.sourceKey, segmentIds: [segment.id] } as Pick<
    ResolvedSpan,
    "tagId" | "tagName" | "kind" | "sourceKey" | "segmentIds"
  >;
  return spanPerson(span, new Map([[segment.id, segment]]), new Map(faces.map((entry) => [entry.id, entry])));
}

describe("grouping a video's segments by person", () => {
  it("puts every face of a performer, and the faces carrying their name, in one group", () => {
    const linkedA = personOf(raw(1), [face(1, { performerId: 7, performerName: "Person A" })]);
    const linkedB = personOf(raw(2), [face(2, { performerId: 7, performerName: "Person A" })]);
    const namedOnly = personOf(raw(3), [face(3, { label: "Person A" })]);
    expect(linkedA).toMatchObject({ name: "Person A", suggested: false, faceId: 1 });
    expect(linkedB.key).toBe(linkedA.key);
    expect(namedOnly.key).toBe(linkedA.key);
  });

  it("keeps a suggested performer apart from the confirmed one, and an unknown face on its own", () => {
    const suggestion = { performerId: 7, performerName: "Person A", confidence: 0.7 };
    const confirmed = personOf(raw(1), [face(1, { performerId: 7, performerName: "Person A" })]);
    const suggestedA = personOf(raw(2), [face(2, { topSuggestion: suggestion })]);
    const suggestedB = personOf(raw(3), [face(3, { topSuggestion: suggestion })]);
    expect(suggestedA).toMatchObject({ name: "Person A", suggested: true });
    expect(suggestedA.key).not.toBe(confirmed.key);
    expect(suggestedB.key).toBe(suggestedA.key);
    // An ignored face is not grouped under its old suggestion.
    expect(personOf(raw(4), [face(4, { topSuggestion: suggestion, ignored: true })])).toMatchObject({
      name: "Unnamed face",
      key: "face:4",
    });
  });

  it("joins a performer segment to the same performer's face segments", () => {
    const fromFace = personOf(raw(1), [face(1, { performerId: 7, performerName: "Person A" })]);
    const fromPerformer = personOf(
      raw(9, { kind: "performer", refId: 7, performerId: 7, performerName: "Person A", title: undefined }),
      [],
    );
    expect(fromPerformer.key).toBe(fromFace.key);
  });
});

describe("person colours", () => {
  it("give each person in a video a different colour, the same for the same person", () => {
    const colors = assignPersonColors(["name:b", "name:a", "suggested:1", "name:a"]);
    expect(colors.size).toBe(3);
    expect(new Set(colors.values()).size).toBe(3);
    expect(assignPersonColors(["name:a", "name:b", "suggested:1"]).get("name:a")).toBe(colors.get("name:a"));
  });
});

describe("a segment's name in the video's segment lists", () => {
  function nameOf(segment: Segment, faces: Face[]) {
    const span = { kind: segment.kind, sourceKey: segment.sourceKey, segmentIds: [segment.id] } as Pick<
      ResolvedSpan,
      "tagId" | "tagName" | "kind" | "sourceKey" | "segmentIds"
    >;
    return spanShownName(span, new Map([[segment.id, segment]]), new Map(faces.map((entry) => [entry.id, entry])));
  }

  it("names a face segment after its person, as its group does, not after its placeholder or numbered face", () => {
    expect(nameOf(raw(31), [face(31)])).toEqual({ name: "Unnamed face", suggested: false });
    expect(
      nameOf(raw(32), [
        face(32, { performerId: 7, performerName: "Alice", performerFaceIndex: 2, performerFaceCount: 3 }),
      ]),
    ).toEqual({ name: "Alice", suggested: false });
  });

  it("keeps the shared naming for a tag segment", () => {
    expect(
      nameOf(raw(40, { kind: "tag", tagId: 5, refId: undefined, title: "Kissing", sourceKey: "manual" }), []),
    ).toEqual({
      name: "Kissing",
      suggested: false,
    });
  });
});
