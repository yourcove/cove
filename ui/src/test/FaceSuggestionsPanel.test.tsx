import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { FaceSuggestionsPanel } from "../components/FaceSuggestionsPanel";
import type { Face, FaceSuggestion } from "../api/types";

const testFace: Face = {
  id: 1,
  ignored: false,
  detectionCount: 0,
  appearanceCount: 0,
  frameSampleCount: 0,
  videoCount: 0,
  imageCount: 0,
  createdAt: "2026-01-01T00:00:00Z",
  updatedAt: "2026-01-01T00:00:00Z",
};

describe("FaceSuggestionsPanel", () => {
  it("renders the empty state when there are no suggestions", () => {
    render(
      <FaceSuggestionsPanel
        face={testFace}
        suggestions={[]}
        isLoading={false}
        disabled={false}
        canReadPerformers
        onAccept={vi.fn()}
        onReject={vi.fn()}
        onNavigate={vi.fn()}
      />,
    );

    expect(screen.getByText("Suggested matches")).toBeInTheDocument();
    expect(screen.getByText("No suggestions are available for this face yet.")).toBeInTheDocument();
  });

  it("renders populated suggestions and forwards accept/reject actions", () => {
    const onAccept = vi.fn();
    const onReject = vi.fn();
    const onNavigate = vi.fn();
    const suggestions: FaceSuggestion[] = [
      {
        performerId: 12,
        performerName: "Jane Doe",
        coverImageUrl: "/img/performers/12.jpg",
        confidence: 0.92,
        why: "Two high-similarity face matches from the same source.",
        evidence: [
          { faceId: 51, thumbnailUrl: "/img/faces/51.jpg", similarity: 0.94 },
          { faceId: 52, thumbnailUrl: "/img/faces/52.jpg", similarity: 0.88 },
        ],
      },
    ];

    render(
      <FaceSuggestionsPanel
        face={testFace}
        suggestions={suggestions}
        isLoading={false}
        disabled={false}
        canReadPerformers
        onAccept={onAccept}
        onReject={onReject}
        onNavigate={onNavigate}
      />,
    );

    expect(screen.getByText("Jane Doe")).toBeInTheDocument();
    expect(screen.getByText("Two high-similarity face matches from the same source.")).toBeInTheDocument();
    expect(screen.getByText("92%")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Accept" }));
    expect(onAccept).toHaveBeenCalledWith(suggestions[0]);

    fireEvent.click(screen.getByRole("button", { name: "Reject" }));
    expect(onReject).toHaveBeenCalledWith(suggestions[0]);

    fireEvent.click(screen.getByRole("button", { name: "Open evidence face 51" }));
    expect(onNavigate).toHaveBeenCalledWith({ page: "face", id: 51 });
  });

  it("renders reference-only suggestions with import and dismiss actions", () => {
    const onAccept = vi.fn();
    const onReject = vi.fn();
    const suggestion: FaceSuggestion = {
      performerId: -7,
      performerName: "Reference Jane",
      coverImageUrl: undefined,
      confidence: 0.81,
      why: "Nearest imported reference identity.",
      evidence: [],
    };

    render(
      <FaceSuggestionsPanel
        face={testFace}
        suggestions={[suggestion]}
        isLoading={false}
        disabled={false}
        canReadPerformers
        onAccept={onAccept}
        onReject={onReject}
        onNavigate={vi.fn()}
      />,
    );

    expect(screen.getByText("Reference DB")).toBeInTheDocument();
    expect(
      screen.getByText("External reference match. Import it to create a local performer link."),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Import as performer" }));
    expect(onAccept).toHaveBeenCalledWith(suggestion);

    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(onReject).toHaveBeenCalledWith(suggestion);
  });
});
