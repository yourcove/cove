import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Face } from "../api/types";
import { VideoFacesList, type VideoFace } from "../pages/videoDetail/VideoFacesList";

const { mockFaces } = vi.hoisted(() => ({
  mockFaces: { suggestions: vi.fn(), recordSuggestionDecision: vi.fn() },
}));

vi.mock("../api/client", () => ({ faces: mockFaces }));

function face(id: number, overrides: Partial<Face> = {}): Face {
  return { id, label: `face-00${id}`, primarySourceKey: `face-00${id}`, ignored: false, ...overrides } as Face;
}

const suggestion = { performerId: 40, performerName: "Suggested Person", confidence: 0.7 };
const videoFaces: VideoFace[] = [
  // A performer seen through three faces, numbered across the whole library.
  {
    face: face(1, { performerId: 7, performerName: "Linked Person", performerFaceIndex: 1, performerFaceCount: 240 }),
    detectionCount: 20,
  },
  {
    face: face(2, { performerId: 7, performerName: "Linked Person", performerFaceIndex: 236, performerFaceCount: 240 }),
    detectionCount: 18,
  },
  {
    face: face(3, { performerId: 7, performerName: "Linked Person", performerFaceIndex: 237, performerFaceCount: 240 }),
    detectionCount: 16,
  },
  { face: face(4, { topSuggestion: suggestion }), detectionCount: 6 },
  { face: face(5, { performerId: 8, performerName: "Solo Person" }), detectionCount: 4 },
];

function renderList(overrides: Partial<Parameters<typeof VideoFacesList>[0]> = {}) {
  const onNavigate = vi.fn();
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <VideoFacesList videoId={9} videoFaces={videoFaces} canDecideFaces onNavigate={onNavigate} {...overrides} />
    </QueryClientProvider>,
  );
  return { onNavigate };
}

afterEach(() => {
  vi.clearAllMocks();
});

describe("VideoFacesList", () => {
  it("shows one card per person, without the library-wide face numbers", () => {
    renderList();

    expect(screen.getByRole("button", { name: /Linked Person/ })).toHaveTextContent(/54 detections.*3 faces/);
    expect(screen.queryByText(/Linked Person \d/)).not.toBeInTheDocument();
    // A person seen through one face is a link to that face.
    expect(screen.getByRole("link", { name: /Solo Person/ })).toHaveAttribute("href", "/face/5");
    expect(screen.getByRole("button", { name: /Suggested Person \(suggested\)/ })).toBeInTheDocument();
  });

  it("lists a person's faces with links that can open in a new tab", () => {
    renderList();

    fireEvent.click(screen.getByRole("button", { name: /Linked Person/ }));
    expect(screen.getByText("face-001")).toBeInTheDocument();
    expect(screen.getAllByRole("link", { name: "Open face" }).map((link) => link.getAttribute("href"))).toEqual([
      "/face/1",
      "/face/2",
      "/face/3",
    ]);
  });

  it("accepts a suggested face from its card", async () => {
    mockFaces.suggestions.mockResolvedValue([
      { performerId: 40, performerName: "Suggested Person", confidence: 70, why: "", evidence: [] },
    ]);
    mockFaces.recordSuggestionDecision.mockResolvedValue(face(4));
    renderList();

    fireEvent.click(screen.getByRole("button", { name: /Suggested Person \(suggested\)/ }));
    fireEvent.click(screen.getByRole("button", { name: "Accept" }));

    await waitFor(() =>
      expect(mockFaces.recordSuggestionDecision).toHaveBeenCalledWith(4, { performerId: 40, decision: "accept" }),
    );
  });

  it("sends a suggestion that would create a new performer to the face page for review", () => {
    const { onNavigate } = renderList({
      videoFaces: [{ face: face(6, { topSuggestion: { ...suggestion, performerId: 0 } }), detectionCount: 3 }],
    });

    fireEvent.click(screen.getByRole("button", { name: /Suggested Person \(suggested\)/ }));
    expect(screen.queryByRole("button", { name: "Accept" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Review" }));
    expect(onNavigate).toHaveBeenCalledWith({ page: "face", id: 6 });
  });
});
