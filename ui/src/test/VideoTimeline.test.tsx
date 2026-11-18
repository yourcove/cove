import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Face, ResolvedSpan, Segment } from "../api/types";
import { EMPTY_SEGMENT_FILTER } from "../components/segmentFilter";
import { VideoScrubber } from "../pages/videoDetail/VideoTimeline";

function span(spanKey: string, tagName: string, startSec: number, endSec: number, segmentId: number) {
  return { spanKey, startSec, endSec, tagId: segmentId, tagName, kind: "tag", segmentIds: [segmentId] } as Pick<
    ResolvedSpan,
    "spanKey" | "startSec" | "endSec" | "tagId" | "tagName" | "kind" | "segmentIds"
  >;
}

const rawSegments = [
  { id: 1, startSec: 10, endSec: 20, kind: "tag", tagId: 1, title: "Kissing" },
  { id: 2, startSec: 40, endSec: 50, kind: "tag", tagId: 2, title: "Dancing" },
] as Segment[];

function renderTimeline(props: Partial<Parameters<typeof VideoScrubber>[0]> = {}) {
  return render(
    <VideoScrubber
      sprites={null}
      duration={100}
      spans={[span("kiss", "Kissing", 10, 20, 1), span("dance", "Dancing", 40, 50, 2)]}
      rawSegments={rawSegments}
      detections={[]}
      facesById={new Map()}
      personColors={new Map()}
      {...props}
    />,
  );
}

describe("the video timeline on a segment's page", () => {
  it("marks the segment's stretch of the video and outlines its bar", () => {
    renderTimeline({ highlight: { startSec: 10, endSec: 20 }, activeSpanKey: "kiss", onOpenSpan: vi.fn() });
    expect(screen.getByTestId("timeline-highlight")).toHaveStyle({ left: "10%", width: "10%" });
    const own = screen.getByRole("button", { name: "Kissing" });
    expect(own).toHaveAttribute("aria-current", "true");
    expect(own.style.boxShadow).toContain("0 0 0 2px");
    expect(screen.getByRole("button", { name: "Dancing" }).style.boxShadow).not.toContain("0 0 0 2px");
  });

  it("opens another segment from its bar, and seeks from the segment's own bar", () => {
    const onOpenSpan = vi.fn();
    const onSeek = vi.fn();
    renderTimeline({ activeSpanKey: "kiss", onOpenSpan, onSeek });
    fireEvent.click(screen.getByRole("button", { name: "Dancing" }));
    expect(onOpenSpan).toHaveBeenCalledWith("dance");
    fireEvent.click(screen.getByRole("button", { name: "Kissing" }));
    expect(onSeek).toHaveBeenCalledWith(10);
    expect(onOpenSpan).toHaveBeenCalledTimes(1);
  });

  it("seeks from every bar on the video page, where no segment is open", () => {
    const onSeek = vi.fn();
    renderTimeline({ onSeek });
    fireEvent.click(screen.getByRole("button", { name: "Dancing" }));
    expect(onSeek).toHaveBeenCalledWith(40);
    expect(screen.queryByTestId("timeline-highlight")).toBeNull();
  });

  it("shows the face lanes without being asked when the video's faces have names", () => {
    const faceSegment = { id: 3, startSec: 60, endSec: 70, kind: "face", refId: 31, title: "face-0031" } as Segment;
    const face = { id: 31, label: "face-0031", performerId: 7, performerName: "Alice", ignored: false } as Face;
    renderTimeline({
      spans: [{ spanKey: "alice", startSec: 60, endSec: 70, kind: "face", tagName: "face-0031", segmentIds: [3] }],
      rawSegments: [...rawSegments, faceSegment],
      facesById: new Map([[31, face]]),
    });
    expect(screen.getByText("Faces")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Alice" })).toBeInTheDocument();
  });
});

describe("the video timeline's detections", () => {
  const detection = (id: number, observedAtSec: number, overrides: Record<string, unknown> = {}) => ({
    id,
    observedAtSec,
    class: "face",
    score: 0.9,
    refKind: "face",
    refId: 31,
    ...overrides,
  });

  afterEach(() => {
    localStorage.clear();
  });

  it("draws detections linked to a face only as the person's bars, and keeps unlinked ones as dots", () => {
    const face = { id: 31, label: "face-0031", performerId: 7, performerName: "Alice", ignored: false } as Face;
    renderTimeline({
      spans: [],
      rawSegments: [],
      detections: [detection(1, 60), detection(2, 61), detection(3, 80, { refKind: null, refId: null })],
      facesById: new Map([[31, face]]),
    });

    expect(screen.getByRole("button", { name: "Alice (1:00 - 1:01)" })).toBeInTheDocument();
    expect(screen.getByText("Detections")).toBeInTheDocument();
    expect(screen.getAllByTitle(/face \(90%\)/).map((dot) => dot.title)).toEqual(["face (90%) at 1:20"]);
  });

  it("hides the Detections row while a segment filter is applied", () => {
    renderTimeline({
      detections: [detection(4, 30, { class: "logo", refKind: null, refId: null })],
      filter: { ...EMPTY_SEGMENT_FILTER, tagIds: [1] },
    });
    expect(screen.queryByText("Detections")).not.toBeInTheDocument();
  });

  it("keeps other detections in a row of their own, which Collapse hides", () => {
    const onSeek = vi.fn();
    renderTimeline({
      spans: [],
      rawSegments: [],
      detections: [detection(4, 30, { class: "logo", refKind: null, refId: null })],
      onSeek,
    });

    expect(screen.getByText("Detections")).toBeInTheDocument();
    fireEvent.click(screen.getByTitle("logo (90%) at 0:30"));
    expect(onSeek).toHaveBeenCalledWith(30);

    fireEvent.click(screen.getByRole("button", { name: "Collapse" }));
    expect(screen.queryByText("Detections")).not.toBeInTheDocument();
    expect(screen.queryByTitle("logo (90%) at 0:30")).not.toBeInTheDocument();
  });
});
