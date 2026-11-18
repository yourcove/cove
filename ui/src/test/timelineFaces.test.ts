import { describe, expect, it } from "vitest";
import type { Face, ResolvedSpan, Segment } from "../api/types";
import { buildFaceTimelineItems } from "../pages/videoDetail/timelineFaces";

function raw(id: number, startSec: number, endSec: number, faceId = 31): Segment {
  return {
    id,
    hostId: 1,
    startSec,
    endSec,
    kind: "face",
    refId: faceId,
    refLabel: `face-00${faceId}`,
    title: `face-00${faceId}`,
    sourceKey: "ext:ai.faces",
  } as Segment;
}

function span(spanKey: string, startSec: number, endSec: number, segmentIds: number[]) {
  return {
    spanKey,
    startSec,
    endSec,
    kind: "face",
    sourceKey: "ext:ai.faces",
    tagName: "face-0031",
    segmentIds,
  } as Pick<
    ResolvedSpan,
    "spanKey" | "startSec" | "endSec" | "tagId" | "tagName" | "kind" | "sourceKey" | "segmentIds"
  >;
}

const face = {
  id: 31,
  label: "face-0031",
  primarySourceKey: "face-0031",
  ignored: false,
  topSuggestion: { performerId: 40, performerName: "Suggested Person", confidence: 0.7 },
} as Face;

function build(options: Partial<Parameters<typeof buildFaceTimelineItems>[0]> = {}) {
  const rawSegments = [raw(1, 10, 12), raw(2, 15, 18), raw(3, 40, 45)];
  return buildFaceTimelineItems({
    spans: [span("joined", 10, 18, [1, 2]), span("alone", 40, 45, [3])],
    rawSegments,
    rawSegmentsById: new Map(rawSegments.map((segment) => [segment.id, segment])),
    detections: [],
    facesById: new Map([[31, face]]),
    includeFace: () => true,
    filterActive: false,
    ...options,
  });
}

describe("timeline face lanes", () => {
  it("draw one bar per segment of the chosen profile, named as everywhere else", () => {
    const items = build();
    expect(items.map((item) => [item.startSec, item.endSec])).toEqual([
      [10, 18],
      [40, 45],
    ]);
    expect(items[0]).toMatchObject({ label: "Suggested Person", suggested: true });
  });

  it("colour each bar as its person's group in the Segments tab, and name it after the person", () => {
    const items = build({ personColors: new Map([["suggested:40", "#123456"]]) });
    expect(items.map((item) => item.colorHint)).toEqual(["#123456", "#123456"]);
  });

  it("stand in with detection runs only for faces that have no face segments", () => {
    const items = build({
      detections: [
        { observedAtSec: 50, refKind: "face", refId: 31 },
        { observedAtSec: 60, refKind: "face", refId: 32 },
        { observedAtSec: 61, refKind: "face", refId: 32 },
      ],
    });
    // Face 31 has segments, so its detection adds nothing; face 32 has none, so its detections become one bar.
    expect(items.filter((item) => item.key.startsWith("face-32-")).map((item) => [item.startSec, item.endSec])).toEqual(
      [[60, 61]],
    );
    expect(items.some((item) => item.key.startsWith("face-31-"))).toBe(false);
  });

  it("leave out faces the filter excludes", () => {
    expect(build({ includeFace: (faceId) => faceId !== 31, filterActive: true })).toEqual([]);
  });
});
