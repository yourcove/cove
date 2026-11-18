import { describe, expect, it } from "vitest";
import type { Face, ResolvedSpan, Segment } from "../api/types";
import { resolveRawSegmentName, resolveSpanName, segmentFaceName } from "../pages/segments/segmentDisplayUtils";

function face(overrides: Partial<Face> = {}): Face {
  return {
    id: 31,
    label: "face-0031",
    primarySourceKey: "face-0031",
    ignored: false,
    topSuggestion: { performerId: 40, performerName: "Suggested Person", confidence: 0.7 },
    ...overrides,
  } as Face;
}

function rawFaceSegment(overrides: Partial<Segment> = {}): Segment {
  return {
    id: 7,
    hostId: 1,
    startSec: 10,
    endSec: 20,
    kind: "face",
    refId: 31,
    refLabel: "face-0031",
    title: "face-0031",
    sourceKey: "ext:ai.faces",
    ...overrides,
  } as Segment;
}

function span(overrides: Partial<ResolvedSpan> = {}): ResolvedSpan {
  return {
    spanKey: "s",
    hostType: "video",
    hostId: 1,
    startSec: 10,
    endSec: 20,
    kind: "face",
    sourceKey: "ext:ai.faces",
    tagName: "face-0031",
    collapsedToInstant: false,
    segmentIds: [7],
    ...overrides,
  } as ResolvedSpan;
}

describe("segment naming shared by every view", () => {
  it("names a face by its own name, else its suggestion marked as one, and never after an old suggestion", () => {
    expect(segmentFaceName(face({ performerId: 4, performerName: "Linked" }))).toEqual({
      name: "Linked",
      suggested: false,
    });
    expect(segmentFaceName(face())).toEqual({ name: "Suggested Person", suggested: true });
    expect(segmentFaceName(face({ ignored: true }))).toBeUndefined();
    expect(segmentFaceName(face({ mergedIntoFaceId: 99, label: "Kept Name" }))).toEqual({
      name: "Kept Name",
      suggested: false,
    });
  });

  it("names a raw face segment titled with a placeholder key after its face", () => {
    const faces = new Map([[31, face()]]);
    expect(resolveRawSegmentName(rawFaceSegment(), faces)).toEqual({ name: "Suggested Person", suggested: true });
    // Without the face at hand, the segment's own fields still name it.
    expect(resolveRawSegmentName(rawFaceSegment())).toEqual({ name: "face-0031", suggested: false });
    // A title someone typed wins, and a title holding a source's raw data does not count as one.
    expect(resolveRawSegmentName(rawFaceSegment({ title: "Kitchen" }), faces).name).toBe("Kitchen");
    expect(
      resolveRawSegmentName({ ...rawFaceSegment(), kind: "tag", tagName: "Kissing", title: '{"probabilities":1}' })
        .name,
    ).toBe("Kissing");
  });

  it("names a span by its tag, else by the raw segment it starts with rather than its stored title", () => {
    const raw = new Map([[7, rawFaceSegment()]]);
    const faces = new Map([[31, face()]]);
    expect(resolveSpanName(span(), raw, faces)).toEqual({ name: "Suggested Person", suggested: true });
    expect(resolveSpanName(span({ tagId: 5, tagName: "Kissing", kind: "tag" }), raw, faces)).toEqual({
      name: "Kissing",
      suggested: false,
    });
    // With none of its raw segments loaded, the stored title is the last resort.
    expect(resolveSpanName(span(), new Map(), faces).name).toBe("face-0031");
  });
});
