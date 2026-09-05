import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoTagger } from "../components/VideoTagger";

const mocks = vi.hoisted(() => ({
  findMetadataServerByIds: vi.fn(),
  importFromMetadataServer: vi.fn(),
  searchMetadataServer: vi.fn(),
  videoObjectFit: "cover" as "cover" | "contain",
}));

vi.mock("../api/client", () => ({
  entityImages: { videoCoverUrl: vi.fn(() => "/video-cover.jpg") },
  system: { listScrapers: vi.fn().mockResolvedValue([]) },
  scrapeAttempts: { resolveRelations: vi.fn() },
  videos: {
    previewUrl: vi.fn(() => "/video-preview.mp4"),
    screenshotUrl: vi.fn(() => "/video-cover.jpg"),
    findMetadataServerByIds: mocks.findMetadataServerByIds,
    importFromMetadataServer: mocks.importFromMetadataServer,
    searchMetadataServer: mocks.searchMetadataServer,
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
      ui: { videoObjectFit: mocks.videoObjectFit },
    },
  }),
}));

describe("VideoTagger", () => {
  beforeEach(() => {
    localStorage.clear();
    vi.stubGlobal(
      "IntersectionObserver",
      class {
        observe() {}
        disconnect() {}
        unobserve() {}
      },
    );
    mocks.findMetadataServerByIds.mockReset();
    mocks.importFromMetadataServer.mockReset();
    mocks.searchMetadataServer.mockReset();
    mocks.videoObjectFit = "cover";
    mocks.importFromMetadataServer.mockResolvedValue({});
    mocks.searchMetadataServer.mockResolvedValue([]);
    mocks.findMetadataServerByIds.mockResolvedValue([
      {
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
      },
    ]);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it.each(["cover", "contain"] as const)("renders the shared preview and scrub controls using %s fit", (fit) => {
    mocks.videoObjectFit = fit;
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "Local video",
      files: [{ duration: 60, basename: "video.mp4", path: "/library/video.mp4" }],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    } as any;

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} mode="detail" />
      </QueryClientProvider>,
    );

    const thumbnailLink = screen.getByTitle("Open video Local video");
    expect(thumbnailLink.querySelector(".video-card-preview-image")).toHaveStyle({ objectFit: fit });
    expect(thumbnailLink.querySelector(".video-card-preview-video")).toHaveAttribute("src", "/video-preview.mp4");
    expect(thumbnailLink.querySelector(".video-card-preview-video")).toHaveStyle({ objectFit: fit });
    expect(thumbnailLink.querySelector(".cursor-ew-resize")).toBeInTheDocument();
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

  it("shows skipped related tag claims as a partial-success warning", async () => {
    mocks.importFromMetadataServer.mockResolvedValue({
      importWarnings: ["Skipped remote alias because it is already claimed by another tag."],
    });
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

    await userEvent.click(screen.getByRole("button", { name: "Refresh from First provider" }));
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));

    expect(await screen.findByText(/Saved with warnings: Skipped remote alias/i)).toBeInTheDocument();
    expect(screen.getByText("Saved successfully")).toBeInTheDocument();
  });

  it("can override Scrape All with fingerprint-only matching", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [
      { id: 123, title: "First local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
      { id: 456, title: "Second local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Choose scrape strategy" }));
    await userEvent.click(screen.getByRole("button", { name: /Fingerprint only/ }));

    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(2));
    expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
      123,
      "First local video",
      "https://first.example/graphql",
      "fingerprint",
    );
    expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
      456,
      "Second local video",
      "https://first.example/graphql",
      "fingerprint",
    );
  });

  it("saves and uses a default bulk match strategy", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "Local video",
      files: [],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    } as any;

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByTitle("Tagger settings"));
    await userEvent.selectOptions(screen.getByLabelText("Default bulk match strategy"), "remote-id");
    await userEvent.click(screen.getByRole("button", { name: "Save default" }));
    await userEvent.click(screen.getByRole("button", { name: "Scrape All" }));

    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledOnce());
    expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
      123,
      "Local video",
      "https://first.example/graphql",
      "remote-id",
    );
    expect(JSON.parse(localStorage.getItem("cove-tagger-config") ?? "{}").bulkMatchStrategy).toBe("remote-id");
  });

  it("uses text only for the row search field", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "Local video",
      files: [],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    } as any;

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} mode="detail" />
      </QueryClientProvider>,
    );

    await userEvent.type(screen.getByRole("textbox"), "{enter}");

    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledOnce());
    expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
      123,
      "Local video",
      "https://first.example/graphql",
      undefined,
    );
  });

  it("rehydrates the saved bulk strategy and keeps the fingerprint row action strict", async () => {
    localStorage.setItem("cove-tagger-config", JSON.stringify({ bulkMatchStrategy: "remote-id-fingerprint" }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "Local video",
      files: [],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    } as any;

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Scrape All" }));
    await waitFor(() =>
      expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
        123,
        "Local video",
        "https://first.example/graphql",
        "remote-id-fingerprint",
      ),
    );

    mocks.searchMetadataServer.mockClear();
    await userEvent.click(screen.getByTitle("Search by fingerprint only"));
    await waitFor(() =>
      expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
        123,
        undefined,
        "https://first.example/graphql",
        "fingerprint",
      ),
    );
  });
});
