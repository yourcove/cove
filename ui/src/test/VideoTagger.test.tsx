import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { VideoTagger } from "../components/VideoTagger";

const mocks = vi.hoisted(() => ({
  findMetadataServerByIds: vi.fn(),
  importFromMetadataServer: vi.fn(),
}));

vi.mock("../api/client", () => ({
  system: { listScrapers: vi.fn().mockResolvedValue([]) },
  scrapeAttempts: { resolveRelations: vi.fn() },
  videos: {
    screenshotUrl: vi.fn(() => "/video-cover.jpg"),
    findMetadataServerByIds: mocks.findMetadataServerByIds,
    importFromMetadataServer: mocks.importFromMetadataServer,
  },
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({
    config: {
      scraping: {
        metadataServers: [
          { name: "First provider", endpoint: "https://first.example/graphql" },
          { name: "Second provider", endpoint: "https://second.example/graphql" },
        ],
      },
    },
  }),
}));

describe("VideoTagger", () => {
  beforeEach(() => {
    localStorage.clear();
    mocks.findMetadataServerByIds.mockReset();
    mocks.importFromMetadataServer.mockReset();
    mocks.importFromMetadataServer.mockResolvedValue({});
    mocks.findMetadataServerByIds.mockResolvedValue([{
      id: "first-video-id",
      endpoint: "https://first.example/graphql",
      metadataServerName: "First provider",
      title: "First provider result",
      code: null,
      details: null,
      director: null,
      date: null,
      duration: 60,
      urls: [],
      images: [],
      studioName: null,
      studioCandidate: null,
      performerNames: [],
      performerCandidates: [],
      tagNames: [],
      tagCandidates: [],
      fingerprints: [],
      fingerprintAlgorithms: [],
    }]);
  });

  it("clears results when the metadata provider changes", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "Local video",
      files: [{ duration: 60, basename: "video.mp4", path: "/library/video.mp4" }],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [{ endpoint: "https://first.example/graphql", remoteId: "first-video-id" }],
    } as any;

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} mode="detail" />
      </QueryClientProvider>,
    );

    await userEvent.click(await screen.findByRole("button", { name: "Refresh from First provider" }));
    expect((await screen.findAllByText("First provider result")).length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "Save" })).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByRole("combobox"), "metadata-server:https://second.example/graphql");

    await waitFor(() => expect(screen.queryAllByText("First provider result")).toHaveLength(0));
    expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
  });

  it("imports a result through the provider that returned it", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "Local video",
      files: [{ duration: 60, basename: "video.mp4", path: "/library/video.mp4" }],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [{ endpoint: "https://first.example/graphql", remoteId: "first-video-id" }],
    } as any;

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} mode="detail" />
      </QueryClientProvider>,
    );

    await userEvent.selectOptions(screen.getByRole("combobox"), "metadata-server:https://second.example/graphql");
    await userEvent.click(screen.getByRole("button", { name: "Refresh from First provider" }));
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    expect(mocks.importFromMetadataServer).toHaveBeenCalledWith(
      123,
      expect.objectContaining({
        endpoint: "https://first.example/graphql",
        videoId: "first-video-id",
      }),
    );
  });
});
