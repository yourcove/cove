import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ResolvedSpan } from "../api/types";
import { SegmentTile, type SegmentTileItem } from "../components/EntityCards";
import { buildSpanTileSegment, buildSpanTitle } from "../pages/segments/segmentDisplayUtils";
import type { DerivedSpanItem } from "../pages/segments/types";

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

  it("keeps the video title as plain text while selecting", () => {
    const item = buildSpanItem({ kind: "tag", tagId: 5, tagName: "Dancing", sourceKey: "user" });
    renderTile(spanTile(item), { selecting: true, hostRoute: { page: "video", id: 7, seekTo: 10 } });

    expect(screen.getByText("Host Video")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /Host Video/ })).not.toBeInTheDocument();
  });
});
