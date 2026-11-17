import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
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

    fireEvent.click(screen.getByRole("button", { name: "Show the 2 faces of Linked Person" }));
    expect(screen.getByText("face-0031")).toBeInTheDocument();
    expect(screen.getByText("face-0032")).toBeInTheDocument();
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

  it("offers no decisions without permission to change faces", () => {
    renderPanel({ canDecideFaces: false });
    fireEvent.click(screen.getByRole("button", { name: "Show the 1 face of Suggested Person" }));
    expect(screen.queryByRole("button", { name: "Accept" })).not.toBeInTheDocument();
  });
});
