import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ComponentProps } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Face, ResolvedSpan, Segment } from "../api/types";
import { VideoSegmentsPanel } from "../components/VideoSegmentsPanel";
import { EMPTY_SEGMENT_FILTER } from "../components/segmentFilter";

const { mockFaces } = vi.hoisted(() => ({
  mockFaces: { suggestions: vi.fn(), recordSuggestionDecision: vi.fn() },
}));

vi.mock("../api/client", () => ({
  videos: { segments: { create: vi.fn(), update: vi.fn(), delete: vi.fn() } },
  tagGroups: { list: vi.fn().mockResolvedValue([]) },
  faces: mockFaces,
}));

vi.mock("../components/EntityReferenceSelector", () => ({
  EntityReferenceSelector: () => null,
  EntityReferenceMultiSelector: () => null,
}));

vi.mock("../components/AddToGroupDialog", () => ({ AddToGroupDialog: () => null }));

type PanelProps = ComponentProps<typeof VideoSegmentsPanel>;

function raw(id: number, faceId: number, startSec: number): Segment {
  return {
    id,
    hostId: 7,
    startSec,
    endSec: startSec + 10,
    kind: "face",
    refId: faceId,
    refLabel: `face-00${faceId}`,
    title: `face-00${faceId}`,
    sourceKey: "ext:ai.faces",
  } as Segment;
}

function span(segment: Segment): ResolvedSpan {
  return {
    spanKey: `span-${segment.id}`,
    hostType: "video",
    hostId: 7,
    startSec: segment.startSec,
    endSec: segment.endSec ?? segment.startSec,
    kind: "face",
    sourceKey: "ext:ai.faces",
    tagName: segment.title,
    collapsedToInstant: false,
    segmentIds: [segment.id],
  } as ResolvedSpan;
}

function face(id: number, overrides: Partial<Face> = {}): Face {
  return { id, label: `face-00${id}`, primarySourceKey: `face-00${id}`, ignored: false, ...overrides } as Face;
}

const suggestion = { performerId: 40, performerName: "Suggested Person", confidence: 0.7 };
const rawSegments = [raw(1, 31, 0), raw(2, 32, 20), raw(3, 33, 40), raw(4, 34, 60)];
const faces = [
  face(31, { performerId: 7, performerName: "Linked Person" }),
  face(32, { performerId: 7, performerName: "Linked Person" }),
  face(33, { topSuggestion: suggestion }),
  face(34),
];

function renderPanel(overrides: Partial<PanelProps> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onNavigate = vi.fn();
  render(
    <QueryClientProvider client={queryClient}>
      <VideoSegmentsPanel
        videoId={7}
        spans={rawSegments.map(span)}
        rawSegments={rawSegments}
        facesById={new Map(faces.map((entry) => [entry.id, entry]))}
        loading={false}
        profiles={[]}
        onProfileChange={vi.fn()}
        filter={EMPTY_SEGMENT_FILTER}
        onFilterChange={vi.fn()}
        tagIdToGroupId={new Map()}
        canEdit={false}
        canDecideFaces
        onNavigate={onNavigate}
        {...overrides}
      />
    </QueryClientProvider>,
  );
  return { onNavigate };
}

afterEach(() => {
  vi.clearAllMocks();
});

describe("VideoSegmentsPanel groups by person", () => {
  it("puts a performer's faces in one group and lists them behind its face stack", () => {
    renderPanel();

    expect(screen.getByRole("button", { name: "Linked Person" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Suggested Person (suggested)" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Unnamed face" })).toBeInTheDocument();

    // Each row opens its segment from an always-visible button and no longer repeats that it is a face.
    expect(screen.getAllByRole("link", { name: "Open segment" })).toHaveLength(4);
    expect(screen.queryByText("face", { exact: true })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Show the 2 faces of Linked Person" }));
    expect(screen.getByText("face-0031")).toBeInTheDocument();
    expect(screen.getByText("face-0032")).toBeInTheDocument();
    // Faces and segments open from links, so they can be opened in a new tab.
    expect(screen.getAllByRole("link", { name: "Open face" }).map((link) => link.getAttribute("href"))).toEqual([
      "/face/31",
      "/face/32",
    ]);
    expect(screen.getAllByRole("link", { name: "Open segment" })[0]).toHaveAttribute("href", "/video/7/span/span-1");
  });

  it("accepts a suggested face with the reference link its match carries", async () => {
    mockFaces.suggestions.mockResolvedValue([
      {
        performerId: 40,
        performerName: "Suggested Person",
        confidence: 70,
        why: "",
        evidence: [],
        referenceEndpoint: "https://reference.example/graphql",
        referenceExternalId: "abc",
      },
    ]);
    mockFaces.recordSuggestionDecision.mockResolvedValue(face(33));
    renderPanel();

    fireEvent.click(screen.getByRole("button", { name: "Show the 1 face of Suggested Person" }));
    fireEvent.click(screen.getByRole("button", { name: "Accept" }));

    await waitFor(() =>
      expect(mockFaces.recordSuggestionDecision).toHaveBeenCalledWith(33, {
        performerId: 40,
        decision: "accept",
        referenceEndpoint: "https://reference.example/graphql",
        referenceExternalId: "abc",
        referenceUpdateMetadata: false,
      }),
    );
  });

  it("rejects a suggested face, and sends a disputed match to the face page", async () => {
    mockFaces.recordSuggestionDecision.mockResolvedValue(face(33));
    const { onNavigate } = renderPanel();
    fireEvent.click(screen.getByRole("button", { name: "Show the 1 face of Suggested Person" }));

    fireEvent.click(screen.getByRole("button", { name: "Not them" }));
    await waitFor(() =>
      expect(mockFaces.recordSuggestionDecision).toHaveBeenCalledWith(33, { performerId: 40, decision: "reject" }),
    );

    mockFaces.suggestions.mockResolvedValue([
      {
        performerId: 40,
        performerName: "Suggested Person",
        confidence: 70,
        why: "",
        evidence: [],
        conflictGroupId: "g",
      },
      { performerId: 41, performerName: "Other Person", confidence: 60, why: "", evidence: [], conflictGroupId: "g" },
    ]);
    mockFaces.recordSuggestionDecision.mockClear();
    fireEvent.click(screen.getByRole("button", { name: "Accept" }));
    await waitFor(() => expect(onNavigate).toHaveBeenCalledWith({ page: "face", id: 33 }));
    expect(mockFaces.recordSuggestionDecision).not.toHaveBeenCalled();
  });

  it("lists people first, then tags, each under its own heading", () => {
    const tagRaw = {
      ...raw(9, 0, 100),
      kind: "tag",
      refId: undefined,
      tagId: 5,
      tagName: "Kissing",
      title: "Kissing",
    } as Segment;
    const tagSpan = { ...span(tagRaw), kind: "tag", tagId: 5, tagName: "Kissing" } as ResolvedSpan;
    renderPanel({ spans: [tagSpan, ...rawSegments.map(span)], rawSegments: [...rawSegments, tagRaw] });

    const people = screen.getByRole("region", { name: /People/ });
    const tags = screen.getByRole("region", { name: /Tags/ });
    // The tag lasts longest, yet people still come first.
    expect(people.compareDocumentPosition(tags) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(tags).getByRole("button", { name: "Kissing" })).toBeInTheDocument();
    expect(within(people).queryByRole("button", { name: "Kissing" })).not.toBeInTheDocument();
    expect(screen.queryByRole("region", { name: /Other/ })).not.toBeInTheDocument();
  });

  it("lists tags in the order they first appear, as the timeline reads", () => {
    const tag = (id: number, tagId: number, name: string, startSec: number, endSec: number) => {
      const segment = {
        ...raw(id, 0, startSec),
        endSec,
        kind: "tag",
        refId: undefined,
        tagId,
        tagName: name,
        title: name,
      } as Segment;
      return { segment, span: { ...span(segment), kind: "tag", tagId, tagName: name } as ResolvedSpan };
    };
    // The longest tag starts last, and the shortest first.
    const late = tag(10, 1, "Late Long", 500, 900);
    const early = tag(11, 2, "Early Short", 5, 10);
    const middle = tag(12, 3, "Middle", 100, 200);
    renderPanel({
      spans: [late.span, early.span, middle.span],
      rawSegments: [late.segment, early.segment, middle.segment],
    });

    const tags = screen.getByRole("region", { name: /Tags/ });
    expect(
      within(tags)
        .getAllByRole("button", { expanded: true })
        .map((button) => button.textContent),
    ).toEqual(["Early Short", "Middle", "Late Long"]);
  });

  it("offers no decisions without permission to change faces", () => {
    renderPanel({ canDecideFaces: false });
    fireEvent.click(screen.getByRole("button", { name: "Show the 1 face of Suggested Person" }));
    expect(screen.queryByRole("button", { name: "Accept" })).not.toBeInTheDocument();
  });
});
