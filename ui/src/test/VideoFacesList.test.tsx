import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
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

  it("lists a person's faces in a popover, each named by a link that can open in a new tab", () => {
    renderList();

    const card = screen.getByRole("button", { name: /Linked Person/ });
    fireEvent.click(card);
    const popover = screen.getByRole("dialog", { name: "Faces of Linked Person" });
    expect(card).toHaveAttribute("aria-expanded", "true");
    expect(popover).toHaveFocus();
    expect(
      within(popover)
        .getAllByRole("link")
        .map((link) => [link.textContent, link.getAttribute("href")]),
    ).toEqual([
      ["face-001", "/face/1"],
      ["face-002", "/face/2"],
      ["face-003", "/face/3"],
    ]);
    expect(within(popover).getByTitle("18 detections")).toHaveTextContent("18×");

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(card).toHaveFocus();
  });

  it("closes a person's popover once they are down to one face, and keeps it closed", () => {
    const twoFaces = videoFaces.slice(0, 2);
    const onNavigate = vi.fn();
    const client = new QueryClient();
    const list = (faces: VideoFace[]) => (
      <QueryClientProvider client={client}>
        <VideoFacesList videoId={9} videoFaces={faces} onNavigate={onNavigate} />
      </QueryClientProvider>
    );
    const { rerender } = render(list(twoFaces));

    fireEvent.click(screen.getByRole("button", { name: /Linked Person/ }));
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    rerender(list(twoFaces.slice(0, 1)));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    rerender(list(twoFaces));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("orders linked people as the video's performers, then suggested people the same way, then unnamed faces", () => {
    renderList({
      performers: [
        { id: 8, name: "Solo Person" },
        { id: 40, name: "Suggested Person" },
        { id: 7, name: "Linked Person" },
      ],
      videoFaces: [
        { face: face(10), detectionCount: 500 },
        {
          face: face(11, { topSuggestion: { ...suggestion, performerId: 41, performerName: "Stranger" } }),
          detectionCount: 300,
        },
        {
          face: face(12, { topSuggestion: { ...suggestion, performerId: 7, performerName: "Linked Person" } }),
          detectionCount: 100,
        },
        { face: face(4, { topSuggestion: suggestion }), detectionCount: 2 },
        { face: face(13, { performerId: 9, performerName: "Uncredited Person" }), detectionCount: 200 },
        ...videoFaces.slice(0, 3),
        { face: face(5, { performerId: 8, performerName: "Solo Person" }), detectionCount: 1 },
      ],
    });

    // Each card names its person once; a suggested one marks it as such.
    const cardNames = Array.from(document.querySelectorAll(".break-words"), (name) =>
      name.closest("a, button")?.querySelector(".sr-only") ? `${name.textContent} (suggested)` : name.textContent,
    );
    expect(cardNames).toEqual([
      "Solo Person",
      "Linked Person",
      "Uncredited Person",
      "Suggested Person (suggested)",
      "Linked Person (suggested)",
      "Stranger (suggested)",
      "Unnamed face",
    ]);
  });

  it("closes a person's popover before opening the split dialog over the page", () => {
    const onSplitFace = vi.fn();
    renderList({
      onSplitFace,
      videoFaces: videoFaces.slice(0, 2).map((entry, index) => (index === 0 ? { ...entry, trackCount: 2 } : entry)),
    });

    fireEvent.click(screen.getByRole("button", { name: /Linked Person/ }));
    fireEvent.click(screen.getByRole("button", { name: "Separate people in this face" }));
    expect(onSplitFace).toHaveBeenCalledWith(videoFaces[0].face);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("closes a person's popover on a click outside it", () => {
    renderList();

    fireEvent.click(screen.getByRole("button", { name: /Linked Person/ }));
    fireEvent.mouseDown(document.body);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
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
