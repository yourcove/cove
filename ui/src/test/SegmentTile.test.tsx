import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ResolvedSpan } from "../api/types";
import { SegmentTile, type SegmentTileItem } from "../components/EntityCards";
import { RawSegmentListRow } from "../pages/segments/RawSegmentResults";
import {
  buildSpanTileSegment,
  buildSpanTitle,
  formatDerivedQueryName,
  hasDerivedQueryNames,
  rawSegmentFaceRoute,
  spanFaceRoute,
} from "../pages/segments/segmentDisplayUtils";

import type { DerivedSpanItem, RawSegmentItem } from "../pages/segments/types";

vi.mock("../components/Rating", () => ({
  RatingBanner: () => null,
  RatingBadge: () => null,
}));

function renderTile(segment: SegmentTileItem, props: Partial<Parameters<typeof SegmentTile>[0]> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <SegmentTile segment={segment} onClick={vi.fn()} {...props} />
    </QueryClientProvider>,
  );
}

function buildSpanItem(span: Partial<ResolvedSpan>, reference?: DerivedSpanItem["reference"]): DerivedSpanItem {
  return {
    id: "7:span",
    key: "7:span",
    kind: "profile",
    videoId: 7,
    videoTitle: "Host Video",
    span: {
      spanKey: "span",
      hostType: "video",
      hostId: 7,
      startSec: 10,
      endSec: 20,
      collapsedToInstant: false,
      segmentIds: [3],
      ...span,
    } as ResolvedSpan,
    reference,
  };
}

function spanTile(item: DerivedSpanItem) {
  return buildSpanTileSegment(item, buildSpanTitle(item.span, item.videoTitle, item.reference));
}

describe("SegmentTile", () => {
  it("names a tag span once, below the thumbnail behind a tag icon, followed by the video", () => {
    const item = buildSpanItem({ kind: "tag", tagId: 5, tagName: "Dancing", sourceKey: "user" });
    const { container } = renderTile(spanTile(item));

    expect(screen.getAllByText("Dancing")).toHaveLength(1);
    expect(screen.getByRole("img", { name: "Tag" })).toBeInTheDocument();
    expect(container.querySelector(".card-body")).toHaveTextContent("Dancing");
    expect(container.querySelector(".card-media")).not.toHaveTextContent("Dancing");
    expect(screen.getByText("Host Video")).toBeInTheDocument();
    expect(screen.getByText("User")).toBeInTheDocument();
    expect(container).not.toHaveTextContent("Tag:");
  });

  it("shows a span's confidence after its source", () => {
    const item = {
      ...buildSpanItem({ kind: "tag", tagId: 5, tagName: "Dancing", sourceKey: "ext:ai.tagging" }),
      confidence: 0.914,
    };
    renderTile(spanTile(item));

    expect(screen.getByText("AI Tagging · 91% confidence")).toBeInTheDocument();
  });

  it("shows an unnamed face by its placeholder key rather than as a tag", () => {
    const item = buildSpanItem(
      { kind: "face", tagName: "stale title", sourceKey: "ext:ai.faces" },
      { faceId: 31, faceLabel: "face-0031", faceSourceKey: "face-0031" },
    );
    const { container } = renderTile(spanTile(item));

    expect(screen.getByText("Unnamed face")).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Face" })).toBeInTheDocument();
    expect(screen.getByText("face-0031 · AI Faces")).toBeInTheDocument();
    expect(container).not.toHaveTextContent("stale title");
    expect(container).not.toHaveTextContent("Tag:");
  });

  it("names a face linked to a performer after the performer", () => {
    const item = buildSpanItem(
      { kind: "face", tagName: "stale title", sourceKey: "ext:ai.faces" },
      {
        faceId: 32,
        faceLabel: "face-0032",
        faceSourceKey: "face-0032",
        performerId: 4,
        performerName: "Linked Performer",
      },
    );
    renderTile(spanTile(item));

    expect(screen.getByText("Linked Performer")).toBeInTheDocument();
    expect(screen.getByText("face-0032 · AI Faces")).toBeInTheDocument();
  });

  it("names a performer span after its performer", () => {
    const item = buildSpanItem(
      { kind: "performer", tagName: "stale title", sourceKey: "user" },
      { performerId: 4, performerName: "Direct Performer" },
    );
    const { container } = renderTile(spanTile(item));

    expect(screen.getByText("Direct Performer")).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Performer" })).toBeInTheDocument();
    expect(container).not.toHaveTextContent("stale title");
  });

  it("falls back to a face span's stored title, without calling it a tag, when it has no reference", () => {
    const item = buildSpanItem({ kind: "face", tagName: "face-0031", sourceKey: "ext:ai.faces" });
    const { container } = renderTile(spanTile(item));

    expect(screen.getByText("face-0031")).toBeInTheDocument();
    expect(screen.getByText("AI Faces")).toBeInTheDocument();
    expect(container).not.toHaveTextContent("Tag:");
  });

  it("labels a derived span by its operator", () => {
    const item = buildSpanItem({ kind: "union", sourceKey: "derived", segmentIds: [3, 4] });
    const { container } = renderTile(spanTile(item));

    expect(screen.getByText("union")).toBeInTheDocument();
    expect(screen.getByText("derived")).toBeInTheDocument();
    expect(container.querySelector(".card-body [role=img]")).toBeNull();
  });

  it("matches the face kind regardless of case", () => {
    const item = buildSpanItem(
      { kind: "FACE", sourceKey: "ext:ai.faces" },
      { faceId: 31, faceLabel: "face-0031", faceSourceKey: "face-0031" },
    );
    renderTile(spanTile(item));

    expect(screen.getByRole("img", { name: "Face" })).toBeInTheDocument();
  });

  it("keeps a raw segment's tag in the details when its title differs", () => {
    renderTile({
      id: 3,
      hostType: "video",
      hostId: 7,
      startSec: 10,
      endSec: 20,
      kind: "tag",
      title: "Custom Title",
      tagName: "Dancing",
      sourceKey: "user",
      confidence: 0.91,
      hostTitle: "Host Video",
    });

    expect(screen.getByText("Custom Title")).toBeInTheDocument();
    expect(screen.queryByRole("img", { name: "Tag" })).not.toBeInTheDocument();
    expect(screen.getByText("Tag: Dancing · User · 91% confidence")).toBeInTheDocument();
  });

  it("links the video title to the video, separately from the card", () => {
    const onClick = vi.fn();
    const onOpenHost = vi.fn();
    const item = buildSpanItem({ kind: "tag", tagId: 5, tagName: "Dancing", sourceKey: "user" });
    renderTile(spanTile(item), {
      onClick,
      onOpenHost,
      hostRoute: { page: "video", id: 7, seekTo: 10 },
    });

    const videoLink = screen.getByRole("link", { name: "Open video Host Video" });
    expect(videoLink).toHaveAttribute("href", "/video/7?t=10");
    expect(videoLink).toHaveTextContent("Host Video");

    // A modified click is left to the browser so the video can open in a new tab.
    expect(fireEvent.click(videoLink, { ctrlKey: true })).toBe(true);
    expect(onOpenHost).not.toHaveBeenCalled();

    expect(fireEvent.click(videoLink)).toBe(false);
    expect(onOpenHost).toHaveBeenCalledTimes(1);
    expect(onClick).not.toHaveBeenCalled();
  });

  it("links a face span's name to the face, separately from the card", () => {
    const onClick = vi.fn();
    const onOpenFace = vi.fn();
    const item = buildSpanItem(
      { kind: "face", sourceKey: "ext:ai.faces" },
      { faceId: 32, faceLabel: "face-0032", faceSourceKey: "face-0032", performerId: 4, performerName: "Linked" },
    );
    const faceRoute = spanFaceRoute(item);
    renderTile(spanTile(item), { onClick, onOpenFace, faceRoute });

    const faceLink = screen.getByRole("link", { name: "Open face Linked" });
    expect(faceLink).toHaveAttribute("href", "/face/32");
    expect(fireEvent.click(faceLink)).toBe(false);
    expect(onOpenFace).toHaveBeenCalledTimes(1);
    expect(onClick).not.toHaveBeenCalled();
  });

  it("keeps the face name as plain text while selecting", () => {
    const item = buildSpanItem({ kind: "face" }, { faceId: 32, faceLabel: "face-0032" });
    renderTile(spanTile(item), { selecting: true, faceRoute: spanFaceRoute(item) });

    expect(screen.queryByRole("link", { name: /Open face/ })).not.toBeInTheDocument();
  });

  it("keeps the video title as plain text while selecting", () => {
    const item = buildSpanItem({ kind: "tag", tagId: 5, tagName: "Dancing", sourceKey: "user" });
    renderTile(spanTile(item), { selecting: true, hostRoute: { page: "video", id: 7, seekTo: 10 } });

    expect(screen.getByText("Host Video")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /Host Video/ })).not.toBeInTheDocument();
  });
});

describe("face routes", () => {
  it("open the face a span or a raw face segment names, and nothing else", () => {
    expect(spanFaceRoute(buildSpanItem({ kind: "face" }, { faceId: 31 }))).toEqual({ page: "face", id: 31 });
    expect(spanFaceRoute(buildSpanItem({ kind: "performer" }, { performerId: 4 }))).toBeUndefined();

    const raw = (segment: Partial<RawSegmentItem>) => segment as RawSegmentItem;
    expect(rawSegmentFaceRoute(raw({ kind: "Face", refId: 31 }))).toEqual({ page: "face", id: 31 });
    expect(rawSegmentFaceRoute(raw({ kind: "performer", refId: 4 }))).toBeUndefined();
    expect(rawSegmentFaceRoute(raw({ kind: "face" }))).toBeUndefined();
  });
});

describe("formatDerivedQueryName", () => {
  const names = {
    tagNamesById: new Map([
      [1, "Dancing"],
      [2, "Singing"],
      [3, "Kissing"],
    ]),
    performerNamesById: new Map([[9, "Alex"]]),
  };

  it("names each operator in words", () => {
    expect(formatDerivedQueryName({ operator: "union", operands: [{ tagIds: [1] }, { tagIds: [2] }] }, names)).toBe(
      "Dancing or Singing",
    );
    expect(
      formatDerivedQueryName({ operator: "intersection", operands: [{ tagIds: [3] }, { performerIds: [9] }] }, names),
    ).toBe("Kissing with Alex");
    expect(
      formatDerivedQueryName({ operator: "difference", operands: [{ tagIds: [1] }, { performerIds: [9] }] }, names),
    ).toBe("Dancing without Alex");
  });

  it("lists further operands and groups an operand's alternatives", () => {
    expect(
      formatDerivedQueryName(
        { operator: "intersection", operands: [{ tagIds: [3] }, { performerIds: [9] }, { tagIds: [1, 2] }] },
        names,
      ),
    ).toBe("Kissing with Alex and (Dancing or Singing)");
  });

  it("qualifies an operand by source and confidence, and names a kind-only operand", () => {
    expect(
      formatDerivedQueryName(
        {
          operator: "difference",
          operands: [{ tagIds: [1], sourceKey: "ext:ai.tagging", minConfidence: 0.8 }, { kind: "face" }],
        },
        names,
      ),
    ).toBe("Dancing (AI Tagging, 80%+) without any face");
  });

  it("leaves out the kind the editor sets alongside the names", () => {
    expect(
      formatDerivedQueryName(
        {
          operator: "union",
          operands: [
            { kind: "tag", tagIds: [1] },
            { kind: "tag", tagIds: [2] },
          ],
        },
        names,
      ),
    ).toBe("Dancing or Singing");
    expect(
      formatDerivedQueryName(
        {
          operator: "intersection",
          operands: [
            { kind: "tag", tagIds: [3] },
            { kind: "performer", performerIds: [9] },
          ],
        },
        names,
      ),
    ).toBe("Kissing with Alex");
  });

  it("joins three operands and names a single or missing one", () => {
    const three = [{ tagIds: [1] }, { tagIds: [2] }, { tagIds: [3] }];
    expect(formatDerivedQueryName({ operator: "union", operands: three }, names)).toBe("Dancing or Singing or Kissing");
    expect(formatDerivedQueryName({ operator: "difference", operands: three }, names)).toBe(
      "Dancing without Singing or Kissing",
    );
    expect(formatDerivedQueryName({ operator: "difference", operands: [{ tagIds: [1] }] }, names)).toBe("Dancing");
    expect(formatDerivedQueryName({ operator: "union", operands: [] }, names)).toBeUndefined();
    expect(formatDerivedQueryName({ operator: "union", operands: [{ sourceKey: "user" }, { kind: "face" }] })).toBe(
      "Any segment (User) or any face",
    );
  });

  it("reports whether every named operand has loaded", () => {
    expect(hasDerivedQueryNames([{ tagIds: [1] }, { performerIds: [9] }], names)).toBe(true);
    expect(hasDerivedQueryNames([{ tagIds: [1] }, { tagIds: [7] }], names)).toBe(false);
    expect(hasDerivedQueryNames([{ kind: "face" }], undefined)).toBe(true);
  });

  it("falls back to ids before names load", () => {
    expect(formatDerivedQueryName({ operator: "union", operands: [{ tagIds: [7] }, { faceIds: [4] }] })).toBe(
      "Tag #7 or Face #4",
    );
  });
});

describe("derived span tiles", () => {
  it("are named after their query without an operator label or the derived source", () => {
    const item = {
      ...buildSpanItem({ kind: "union", sourceKey: "derived", segmentIds: [3, 4] }),
      kind: "derivedQuery" as const,
      derivedQueryDescriptor: { operator: "union" as const, operands: [{ tagIds: [1] }, { tagIds: [2] }] },
    };
    const title = formatDerivedQueryName(item.derivedQueryDescriptor, {
      tagNamesById: new Map([
        [1, "Dancing"],
        [2, "Singing"],
      ]),
    })!;
    const { container } = renderTile(buildSpanTileSegment(item, title));

    expect(screen.getByText("Dancing or Singing")).toBeInTheDocument();
    expect(container).not.toHaveTextContent(/union/i);
    expect(container).not.toHaveTextContent("derived");
  });
});

describe("RawSegmentListRow", () => {
  const density = {
    rowPaddingClassName: "py-1.5",
    previewHeight: 0,
    previewWidth: 0,
    showPreview: false,
    showSecondaryDetails: false,
  };
  const faceSegment = {
    id: 3,
    key: "3",
    videoId: 7,
    videoTitle: "Host Video",
    hostType: "video",
    hostId: 7,
    startSec: 10,
    endSec: 20,
    kind: "face",
    refId: 31,
    sourceKey: "ext:ai.faces",
  } as RawSegmentItem;

  it("offers the face on its own when the video cannot be read", () => {
    const onNavigate = vi.fn();
    render(
      <RawSegmentListRow
        item={faceSegment}
        canReadVideos={false}
        canReadFaces
        onNavigate={onNavigate}
        selected={false}
        onToggle={vi.fn()}
        selecting={false}
        density={density}
      />,
    );

    expect(screen.queryByRole("button", { name: "Open video" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Open face" }));
    expect(onNavigate).toHaveBeenCalledWith({ page: "face", id: 31 });
  });

  it("leaves the face out without permission to read faces", () => {
    render(
      <RawSegmentListRow
        item={faceSegment}
        canReadVideos
        canReadFaces={false}
        onNavigate={vi.fn()}
        selected={false}
        onToggle={vi.fn()}
        selecting={false}
        density={density}
      />,
    );

    expect(screen.getByRole("button", { name: "Open video" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Open face" })).not.toBeInTheDocument();
  });
});
