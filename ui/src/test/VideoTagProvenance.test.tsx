import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";

import { DetailsTab } from "../pages/VideoDetailPage";

describe("Video tag provenance", () => {
  it("passes tag provenance through to the shared badge surface", () => {
    const video = {
      id: 17,
      title: "Provenance video",
      updatedAt: "2026-05-01T00:00:00Z",
      files: [],
      performers: [],
      groups: [],
      galleries: [],
      studioName: null,
      studioId: null,
      resumeTime: 0,
      rating: null,
      likeCounter: 0,
      organized: false,
      details: null,
      date: null,
      playCount: 0,
      remoteIds: [],
      urls: [],
      customFields: undefined,
      tags: [
        {
          id: 99,
          name: "AI Tagged",
          provenance: [
            {
              sourceKey: "ext:ai.tagging",
              sourceRunId: "run-17",
              modelKey: "tagger-v1",
              confidence: 0.91,
              appliedAt: "2026-05-01T00:00:00Z",
            },
          ],
        },
      ],
    };

    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <DetailsTab video={video as any} onNavigate={vi.fn()} />
      </QueryClientProvider>,
    );

    expect(screen.getByText("AI Tagged")).toBeInTheDocument();
    expect(screen.getByText("Ai.Tagging")).toBeInTheDocument();
    expect(screen.getByText("Model tagger-v1")).toBeInTheDocument();
    expect(screen.getByText("Run run-17")).toBeInTheDocument();
    expect(screen.getByText("91%")).toBeInTheDocument();
  });
});
