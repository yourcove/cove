import { QueryClient, QueryClientProvider, focusManager, onlineManager } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { VideoTagger } from "../components/VideoTagger";

const mocks = vi.hoisted(() => ({
  findMetadataServerByIds: vi.fn(),
  importFromMetadataServer: vi.fn(),
  searchMetadataServer: vi.fn(),
  submitMetadataServerDraft: vi.fn(),
  listScrapers: vi.fn(),
  createScrapeAttempt: vi.fn(),
  resolveRelations: vi.fn(),
  applyScrapeAttempt: vi.fn(),
  addStudioAlias: vi.fn(),
  videoObjectFit: "cover" as "cover" | "contain",
}));

vi.mock("../api/client", () => ({
  entityImages: { videoCoverUrl: vi.fn(() => "/video-cover.jpg") },
  system: { listScrapers: mocks.listScrapers },
  studios: { addAlias: mocks.addStudioAlias },
  scrapeAttempts: {
    create: mocks.createScrapeAttempt,
    resolveRelations: mocks.resolveRelations,
    apply: mocks.applyScrapeAttempt,
  },
  videos: {
    previewUrl: vi.fn(() => "/video-preview.mp4"),
    screenshotUrl: vi.fn(() => "/video-cover.jpg"),
    findMetadataServerByIds: mocks.findMetadataServerByIds,
    importFromMetadataServer: mocks.importFromMetadataServer,
    searchMetadataServer: mocks.searchMetadataServer,
    submitMetadataServerDraft: mocks.submitMetadataServerDraft,
  },
}));

// The link panel's library search, which a test answers with one studio.
vi.mock("../components/EntityReferenceSelector", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../components/EntityReferenceSelector")>()),
  EntityReferenceSelector: ({
    onChange,
  }: {
    onChange: (id: number, option: { id: number; label: string }) => void;
  }) => (
    <button type="button" onClick={() => onChange(77, { id: 77, label: "Library Studio" })}>
      Pick Library Studio
    </button>
  ),
}));

vi.mock("../state/AppConfigContext", () => ({
  useOptionalAppConfig: () => undefined,
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

/**
 * The Apply all summary, found by what it says rather than by a role any region could claim. A run
 * with failures is an alert and a cancelled run with none is a status, so both are accepted here and
 * the tests that care about the difference assert the role themselves.
 */
async function findApplyAllSummary(): Promise<HTMLElement> {
  const text = await screen.findByText(/^(Cancelled\. )?Applied \d+/);
  const container = text.closest("[role=alert],[role=status]");
  if (!(container instanceof HTMLElement)) throw new Error("Apply all summary is not in a live region");
  return container;
}

function matchFor(videoId: number) {
  return {
    id: `result-for-${videoId}`,
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
  };
}

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
    mocks.submitMetadataServerDraft.mockReset();
    mocks.listScrapers.mockReset();
    mocks.createScrapeAttempt.mockReset();
    mocks.resolveRelations.mockReset();
    mocks.applyScrapeAttempt.mockReset();
    mocks.videoObjectFit = "cover";
    mocks.importFromMetadataServer.mockResolvedValue({});
    mocks.searchMetadataServer.mockResolvedValue([]);
    mocks.listScrapers.mockResolvedValue([]);
    mocks.resolveRelations.mockResolvedValue({ tags: [], performers: [], studios: [] });
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
    expect(screen.getByRole("button", { name: /^Apply \d+ changes?$/ })).toBeInTheDocument();
    // The compact facts open by default; the full side-by-side rows sit behind Adjust.
    expect(screen.queryByText(/Empty fields are filled from/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Adjust…" }));
    expect(screen.getByText(/Empty fields are filled from/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Done adjusting" }));
    expect(screen.queryByText(/Empty fields are filled from/)).not.toBeInTheDocument();

    await userEvent.selectOptions(
      screen.getAllByRole("combobox").filter((element) => element.tagName === "SELECT")[0],
      "metadata-server:https://second.example/graphql",
    );

    await waitFor(() => expect(screen.queryAllByText("First provider result")).toHaveLength(0));
    expect(screen.queryByRole("button", { name: /^Apply/ })).not.toBeInTheDocument();
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

    await userEvent.selectOptions(
      screen.getAllByRole("combobox").filter((element) => element.tagName === "SELECT")[0],
      "metadata-server:https://second.example/graphql",
    );
    await userEvent.click(screen.getByRole("button", { name: "Refresh from First provider" }));
    await userEvent.click(await screen.findByRole("button", { name: /^Apply/ }));

    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    expect(mocks.importFromMetadataServer).toHaveBeenCalledWith(
      123,
      expect.objectContaining({
        endpoint: "https://first.example/graphql",
        videoId: "first-video-id",
      }),
    );
  });

  it.each([
    { create: false, setStudio: false, studioOverride: undefined },
    {
      create: true,
      setStudio: true,
      studioOverride: { remoteId: "remote-studio", name: "Remote Studio", action: "create" },
    },
  ])("imports a provider studio the library lacks only once chosen (Create: $create)", async (expected) => {
    mocks.findMetadataServerByIds.mockResolvedValue([
      {
        ...matchFor(123),
        id: "first-video-id",
        studioName: "Remote Studio",
        studioCandidate: { remoteId: "remote-studio", name: "Remote Studio", existsLocally: false },
      },
    ]);
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
    await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
    const create = await screen.findByRole("button", { name: "Studio: create “Remote Studio” and use it" });
    if (expected.create) await userEvent.click(create);
    await userEvent.click(await screen.findByRole("button", { name: /^Apply/ }));

    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    const request = mocks.importFromMetadataServer.mock.calls[0][1];
    expect(request.setStudio).toBe(expected.setStudio);
    expect(request.onlyExistingStudio).toBe(true);
    expect(request.studioOverride).toEqual(expected.studioOverride);
  });

  it("imports a provider studio linked to a library one for this video as that studio", async () => {
    mocks.findMetadataServerByIds.mockResolvedValue([
      {
        ...matchFor(123),
        id: "first-video-id",
        studioName: "Remote Studio",
        studioCandidate: { remoteId: "remote-studio", name: "Remote Studio", existsLocally: false },
      },
    ]);
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
    await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
    await userEvent.click(
      await screen.findByRole("button", { name: "Studio: link “Remote Studio” to one in your library" }),
    );
    await userEvent.click(screen.getByRole("button", { name: "Pick Library Studio" }));
    await userEvent.click(screen.getByLabelText(/Remember “Remote Studio” as an alias/));
    await userEvent.click(screen.getByRole("button", { name: "Link" }));
    await userEvent.click(await screen.findByRole("button", { name: /^Apply/ }));

    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    const request = mocks.importFromMetadataServer.mock.calls[0][1];
    expect(request.setStudio).toBe(true);
    expect(request.studioOverride).toEqual({
      remoteId: "remote-studio",
      name: "Remote Studio",
      action: "existing",
      localId: 77,
    });
  });

  it("shows a metadata-server match under the library name it lands on and folds it into the tag the video has", async () => {
    // The search matched "Big Tits" to the library tag "Big Breasts" (by alias or remote id), and the
    // studio to one linked under another name.
    mocks.findMetadataServerByIds.mockResolvedValue([
      {
        ...matchFor(123),
        id: "first-video-id",
        studioName: "Fixture Studio",
        studioCandidate: {
          remoteId: "remote-studio",
          name: "Fixture Studio",
          existsLocally: true,
          localId: 9,
          localName: "Fixture Studios Inc",
        },
        tagNames: ["Big Tits"],
        tagCandidates: [
          { remoteId: "t1", name: "Big Tits", existsLocally: true, localId: 5, localName: "Big Breasts" },
        ],
      },
    ]);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "Local video",
      files: [{ duration: 60, basename: "video.mp4", path: "/library/video.mp4" }],
      performers: [],
      tags: [{ id: 5, name: "Big Breasts" }],
      urls: [],
      remoteIds: [{ endpoint: "https://first.example/graphql", remoteId: "first-video-id" }],
    } as any;
    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} mode="detail" />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Refresh from First provider" }));
    await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));

    // One chip, the tag the video has, and nothing added.
    expect(screen.queryByText("Big Tits")).not.toBeInTheDocument();
    expect(within(screen.getByRole("group", { name: "Tags" })).getAllByText("Big Breasts")).toHaveLength(1);
    expect(screen.queryByText(/tags? added/)).not.toBeInTheDocument();
    expect(screen.getAllByText("Fixture Studios Inc").length).toBeGreaterThan(0);
    // The search answered for its tag, so nothing is asked about it.
    expect(mocks.resolveRelations).not.toHaveBeenCalled();
  });

  it("asks again about a provider studio its search did not find, so one created since is set", async () => {
    // The search found no "Remote Studio"; another row has created it since, and the lookup now has it.
    mocks.findMetadataServerByIds.mockResolvedValue([
      {
        ...matchFor(123),
        id: "first-video-id",
        studioName: "Remote Studio",
        studioCandidate: { remoteId: "remote-studio", name: "Remote Studio", existsLocally: false },
      },
    ]);
    mocks.resolveRelations.mockResolvedValue({
      tags: [],
      performers: [],
      studios: [{ input: "Remote Studio", matchedName: "Remote Studio" }],
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
    await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());
    expect(mocks.resolveRelations.mock.calls[0][0].studios).toEqual(["Remote Studio"]);
    const apply = await screen.findByRole("button", { name: /^Apply/ });
    await waitFor(() => expect(apply).toBeEnabled());
    expect(screen.queryByRole("button", { name: /Studio: create/ })).not.toBeInTheDocument();
    await userEvent.click(apply);

    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    const request = mocks.importFromMetadataServer.mock.calls[0][1];
    expect(request.setStudio).toBe(true);
    expect(request.studioOverride).toBeUndefined();
  });

  it("asks only about the selected result, so a result that needs nothing is not held by another", async () => {
    // The first result's search matched everything; the second has a tag only the lookup can place,
    // and that lookup never answers.
    mocks.findMetadataServerByIds.mockResolvedValue([
      { ...matchFor(123), id: "first-video-id" },
      {
        ...matchFor(123),
        id: "second-video-id",
        title: "Second result",
        tagNames: ["Unmatched"],
        tagCandidates: [{ remoteId: "t9", name: "Unmatched", existsLocally: false }],
      },
    ]);
    mocks.resolveRelations.mockImplementation(() => new Promise(() => {}));
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
    const apply = await screen.findByRole("button", { name: /^Apply/ });
    expect(apply).toBeEnabled();
    expect(mocks.resolveRelations).not.toHaveBeenCalled();

    // Choosing the second result asks about its names, and that result waits for the answer.
    const more = screen.queryByRole("button", { name: /other match/i });
    if (more) await userEvent.click(more);
    await userEvent.click(await screen.findByRole("button", { name: "Use Second result" }));
    await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalledOnce());
    expect(mocks.resolveRelations.mock.calls[0][0].tags).toEqual(["Unmatched"]);
    expect(await screen.findByRole("button", { name: /Checking library/ })).toBeDisabled();
  });

  it("treats a provider studio the lookup finds under an alias as in the library, as the import does", async () => {
    mocks.findMetadataServerByIds.mockResolvedValue([
      {
        ...matchFor(123),
        id: "first-video-id",
        studioName: "Remote Studio",
        studioCandidate: { remoteId: "remote-studio", name: "Remote Studio", existsLocally: false },
      },
    ]);
    mocks.resolveRelations.mockResolvedValue({
      tags: [],
      performers: [],
      studios: [{ input: "Remote Studio", matchedName: "Remote Studio Inc" }],
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
    const apply = await screen.findByRole("button", { name: /^Apply/ });
    await waitFor(() => expect(apply).toBeEnabled());
    expect(screen.queryByRole("button", { name: /Studio: create/ })).not.toBeInTheDocument();
    expect(screen.getAllByText("Remote Studio Inc").length).toBeGreaterThan(0);
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
    await userEvent.click(await screen.findByRole("button", { name: /^Apply/ }));

    expect(await screen.findByText(/Saved with warnings: Skipped remote alias/i)).toBeInTheDocument();
    expect(screen.getByText("Saved successfully")).toBeInTheDocument();
  });

  it("can override Search all with fingerprint-only matching", async () => {
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

    await userEvent.click(screen.getByRole("button", { name: "Choose search strategy" }));
    await userEvent.click(screen.getByRole("button", { name: /Fingerprint only/ }));

    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(2));
    expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
      123,
      "First local video",
      "https://first.example/graphql",
      "fingerprint",
      expect.any(AbortSignal),
    );
    expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
      456,
      "Second local video",
      "https://first.example/graphql",
      "fingerprint",
      expect.any(AbortSignal),
    );
  });

  it("drops performers whose gender the settings exclude from the preview and the import", async () => {
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
    mocks.searchMetadataServer.mockResolvedValue([
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
        performerNames: ["Kept Performer", "Excluded Performer"],
        performerCandidates: [
          { remoteId: "p-female", name: "Kept Performer", existsLocally: false, gender: "FEMALE" },
          { remoteId: "p-male", name: "Excluded Performer", existsLocally: false, gender: "MALE" },
        ],
        tagNames: [],
        tagCandidates: [],
        fingerprints: [],
        fingerprintAlgorithms: [],
      },
    ]);

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    expect(await screen.findByText("Excluded Performer")).toBeInTheDocument();

    // Unchecking the gender takes effect on the match already on screen; no second search is needed.
    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Male" }));
    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));

    expect(screen.queryByText("Excluded Performer")).not.toBeInTheDocument();
    expect(screen.getByText("Kept Performer")).toBeInTheDocument();

    await userEvent.click(await screen.findByRole("button", { name: "Apply all (1)" }));
    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());

    const [, request] = mocks.importFromMetadataServer.mock.calls[0];
    expect(request.performerGenders).toEqual(expect.arrayContaining(["Female", "Unknown"]));
    expect(request.performerGenders).not.toContain("Male");
    expect(request.performerOverrides ?? []).not.toContainEqual(expect.objectContaining({ remoteId: "p-male" }));
  });

  it("keeps every performer out when no gender is checked, and says so in the request", async () => {
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
    mocks.searchMetadataServer.mockResolvedValue([
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
        performerNames: ["Excluded Performer"],
        performerCandidates: [
          { remoteId: "p-female", name: "Excluded Performer", existsLocally: false, gender: "FEMALE" },
        ],
        tagNames: [],
        tagCandidates: [],
        fingerprints: [],
        fingerprintAlgorithms: [],
      },
    ]);

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    expect(await screen.findByText("Excluded Performer")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));
    for (const gender of [
      "Female",
      "Male",
      "Transgender Female",
      "Transgender Male",
      "Intersex",
      "Non-Binary",
      "Unknown",
    ]) {
      await userEvent.click(screen.getByRole("checkbox", { name: gender }));
    }
    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));

    expect(screen.queryByText("Excluded Performer")).not.toBeInTheDocument();

    await userEvent.click(await screen.findByRole("button", { name: "Apply all (1)" }));
    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    // An empty list must reach the backend as "no gender allowed"; omitting it would mean the opposite.
    expect(mocks.importFromMetadataServer.mock.calls[0][1].performerGenders).toEqual([]);
  });

  it("adds Unknown to a config saved before the option existed, and then leaves the choice alone", async () => {
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
    // A config from before the setting worked: one gender unchecked, and no record of Unknown either way.
    localStorage.setItem(
      "cove-tagger-config",
      JSON.stringify({ performerGenders: ["Female", "Transgender Female", "Intersex", "Non-Binary"] }),
    );

    const first = render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));
    expect(screen.getByRole("checkbox", { name: "Unknown" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Male" })).not.toBeChecked();

    // Unchecking it is a real choice, so the upgrade must not hand it back on the next visit.
    await userEvent.click(screen.getByRole("checkbox", { name: "Unknown" }));
    first.unmount();

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));
    expect(screen.getByRole("checkbox", { name: "Unknown" })).not.toBeChecked();
  });

  it.each([
    ["the migration has already run", 2],
    ["the config is newer than the migration", 99],
  ])("leaves a deliberately empty gender selection alone once %s", async (_case, configVersion) => {
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
    // Emptied on purpose after the setting started working, so it must survive rather than be read as a
    // legacy config and filled back in.
    localStorage.setItem("cove-tagger-config", JSON.stringify({ configVersion, performerGenders: [] }));

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));
    for (const gender of ["Female", "Male", "Unknown"]) {
      expect(screen.getByRole("checkbox", { name: gender })).not.toBeChecked();
    }
  });

  it("shows a gender checked when the saved config spells it differently", async () => {
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
    localStorage.setItem(
      "cove-tagger-config",
      JSON.stringify({ configVersion: 2, performerGenders: ["FEMALE", "Non Binary"] }),
    );

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));
    // The filter compares by key, so the boxes must agree with it rather than with the exact strings.
    expect(screen.getByRole("checkbox", { name: "Female" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Non-Binary" })).toBeChecked();
    expect(screen.getByRole("checkbox", { name: "Male" })).not.toBeChecked();

    await userEvent.click(screen.getByRole("checkbox", { name: "Female" }));
    expect(screen.getByRole("checkbox", { name: "Female" })).not.toBeChecked();
  });

  it("reads a legacy config with no gender checked as the filter it actually was: none", async () => {
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
    // Saved while the setting did nothing, so the user saw every performer whatever the boxes said.
    localStorage.setItem("cove-tagger-config", JSON.stringify({ performerGenders: [] }));

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));
    for (const gender of ["Female", "Male", "Unknown"]) {
      expect(screen.getByRole("checkbox", { name: gender })).toBeChecked();
    }
  });

  it("sends no gender filter while every performer gender is checked", async () => {
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
    mocks.searchMetadataServer.mockResolvedValue([
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
        // A gender the server did not state must survive the default settings.
        performerNames: ["Kept Performer"],
        performerCandidates: [{ remoteId: "p-unknown", name: "Kept Performer", existsLocally: false }],
        tagNames: [],
        tagCandidates: [],
        fingerprints: [],
        fingerprintAlgorithms: [],
      },
    ]);

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    expect(await screen.findByText("Kept Performer")).toBeInTheDocument();

    await userEvent.click(await screen.findByRole("button", { name: "Apply all (1)" }));
    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    expect(mocks.importFromMetadataServer.mock.calls[0][1].performerGenders).toBeUndefined();
  });

  it("applies all only to the videos that have a match", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [
      { id: 123, title: "First local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
      { id: 456, title: "Second local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;
    // Only the first video matches; the second returns nothing and so must be left alone.
    mocks.searchMetadataServer.mockImplementation((videoId: number) =>
      Promise.resolve(
        videoId === 123
          ? [
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
            ]
          : [],
      ),
    );

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(2));

    const applyAll = await screen.findByRole("button", { name: "Apply all (1)" });
    await userEvent.click(applyAll);

    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    expect(mocks.importFromMetadataServer).toHaveBeenCalledWith(123, expect.anything());
  });

  it("shares the library lookup between rows instead of sending one per row at once", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [301, 302, 303].map((id) => ({
      id,
      title: `Local video ${id}`,
      files: [],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    })) as any;
    mocks.searchMetadataServer.mockImplementation((videoId: number) =>
      Promise.resolve([
        {
          ...matchFor(videoId),
          tagNames: [`Tag ${videoId}`],
          tagCandidates: [{ remoteId: `tag-${videoId}`, name: `Tag ${videoId}`, existsLocally: false }],
        },
      ]),
    );
    // The first lookup stays out until released, so any row asking meanwhile shows up as a second request.
    let releaseFirst!: () => void;
    mocks.resolveRelations.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          releaseFirst = () => resolve({ tags: [], performers: [], studios: [] });
        }),
    );

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(3));
    await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(mocks.resolveRelations).toHaveBeenCalledOnce();

    releaseFirst();
    await waitFor(() => {
      const asked = mocks.resolveRelations.mock.calls.flatMap(([request]) => request.tags);
      expect(asked.sort()).toEqual(["Tag 301", "Tag 302", "Tag 303"]);
    });
    expect(mocks.resolveRelations.mock.calls.length).toBeLessThanOrEqual(2);
  });

  it("asks the library again for a re-searched row without sending another row with the same names back to checking", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [401, 402].map((id) => ({
      id,
      title: `Local video ${id}`,
      files: [],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    })) as any;
    // Both rows ask about the same name, so they share one lookup.
    mocks.searchMetadataServer.mockImplementation((videoId: number) =>
      Promise.resolve([
        {
          ...matchFor(videoId),
          tagNames: ["Shared Tag"],
          tagCandidates: [{ remoteId: "shared-tag", name: "Shared Tag", existsLocally: false }],
        },
      ]),
    );
    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.getAllByRole("button", { name: /^Apply \d+ changes?$/ })).toHaveLength(2));
    await waitFor(() =>
      screen.getAllByRole("button", { name: /^Apply \d+ changes?$/ }).forEach((button) => expect(button).toBeEnabled()),
    );
    expect(mocks.resolveRelations).toHaveBeenCalledOnce();

    let release!: () => void;
    mocks.resolveRelations.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          release = () =>
            resolve({ tags: [{ input: "Shared Tag", matchedName: "Library Tag" }], performers: [], studios: [] });
        }),
    );
    await userEvent.click(screen.getAllByRole("button", { name: "Search for this text" })[1]);
    await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalledTimes(2));
    // The re-searched row waits for the new answer; the other keeps the one it shows.
    expect(screen.getByRole("button", { name: "Checking library…" })).toBeDisabled();
    expect(screen.getByRole("button", { name: /^Apply \d+ changes?$/ })).toBeEnabled();
    release();
    await waitFor(() => expect(screen.getAllByRole("button", { name: /^Apply \d+ changes?$/ })).toHaveLength(2));
    expect(mocks.resolveRelations).toHaveBeenCalledTimes(2);
  });

  it("reports a failed save on the row it failed for, and saves the rest of the batch", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [
      { id: 123, title: "First local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
      { id: 456, title: "Second local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;
    mocks.searchMetadataServer.mockImplementation((videoId: number) => Promise.resolve([matchFor(videoId)]));
    // The first video's import loses a race for a related entity the way a concurrent import does;
    // the second must still save, and the failure must not vanish. The rejection takes the shape the
    // API client actually throws, so the reason has to be unpacked from the body rather than dumped.
    mocks.importFromMetadataServer.mockImplementation((videoId: number) =>
      videoId === 123
        ? Promise.reject(
            new Error(
              'API Error 409: {"code":"RELATED_ENTITY_NAME_CONFLICT","message":"A studio with name \\"Shared studio\\" already exists. Studio names must be unique.","entityType":"studio"}',
            ),
          )
        : Promise.resolve({}),
    );

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(2));

    await userEvent.click(await screen.findByRole("button", { name: "Apply all (2)" }));

    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledTimes(2));
    // The batch says what it did overall, so a partial failure is not read as a clean run.
    const summary = await findApplyAllSummary();
    expect(summary).toHaveTextContent("Applied 1, failed 1.");
    // The reason is the server's own sentence, not the raw response body.
    expect(summary).not.toHaveTextContent("RELATED_ENTITY_NAME_CONFLICT");
    // And it reaches the failing row itself, not only the summary.
    const reasons = screen.getAllByText(
      /A studio with name "Shared studio" already exists\. Studio names must be unique\./,
    );
    expect(reasons.some((element) => !summary.contains(element))).toBe(true);
    // The batch carried on: the video that did not fail reports success.
    expect(await screen.findByText("Saved successfully")).toBeTruthy();
  });

  it("narrows the batch summary to the failures still outstanding after a partial retry", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [
      { id: 301, title: "First local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
      { id: 302, title: "Second local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;
    mocks.searchMetadataServer.mockImplementation((videoId: number) => Promise.resolve([matchFor(videoId)]));
    mocks.importFromMetadataServer.mockImplementation((videoId: number) =>
      Promise.reject(new Error(`API Error 409: {"message":"Studio ${videoId} already exists."}`)),
    );

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(2));
    await userEvent.click(await screen.findByRole("button", { name: "Apply all (2)" }));

    const summary = await findApplyAllSummary();
    expect(summary).toHaveTextContent("Applied 0, failed 2.");
    expect(summary).toHaveAttribute("role", "alert");

    // Only the first row is retried successfully, so the summary must shed that row's count and its
    // reason rather than keep describing the batch as it was when it ended.
    mocks.importFromMetadataServer.mockImplementation((videoId: number) =>
      videoId === 301
        ? Promise.resolve({})
        : Promise.reject(new Error(`API Error 409: {"message":"Studio ${videoId} already exists."}`)),
    );
    const rowApplyButtons = await screen.findAllByRole("button", { name: /^Apply \d+ changes?$/ });
    await userEvent.click(rowApplyButtons[0]);

    await waitFor(async () => expect(await findApplyAllSummary()).toHaveTextContent("Applied 1, failed 1."));
    const narrowed = await findApplyAllSummary();
    expect(narrowed).toHaveTextContent("Studio 302 already exists.");
    expect(narrowed).not.toHaveTextContent("Studio 301 already exists.");
  });

  it("retires the batch summary once the rows that failed have been applied", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [
      { id: 123, title: "First local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;
    mocks.searchMetadataServer.mockResolvedValue([matchFor(123)]);
    mocks.importFromMetadataServer.mockRejectedValueOnce(new Error('API Error 409: {"message":"Boom."}'));

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledOnce());
    await userEvent.click(await screen.findByRole("button", { name: "Apply all (1)" }));
    expect(await findApplyAllSummary()).toHaveTextContent("Applied 0, failed 1.");

    // Retrying the row on its own succeeds, which makes the summary describe a problem that is gone.
    // Apply all is disabled once nothing is left to apply, so nothing else could clear it.
    mocks.importFromMetadataServer.mockResolvedValue({});
    await userEvent.click(await screen.findByRole("button", { name: /^Apply \d+ changes?$/ }));

    await waitFor(() => expect(screen.queryByText(/^Applied \d+/)).toBeNull());
  });

  it("says a cancelled batch was cancelled and accounts for the rows it never started", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    // More videos than the batch runs at once, so cancelling leaves some never started.
    const videos = Array.from({ length: 8 }, (_, index) => ({
      id: 100 + index,
      title: `Local video ${index}`,
      files: [],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    })) as any;
    mocks.searchMetadataServer.mockImplementation((videoId: number) => Promise.resolve([matchFor(videoId)]));
    // Hold every import open so the batch is still draining when Cancel is pressed.
    let releaseImports: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      releaseImports = resolve;
    });
    mocks.importFromMetadataServer.mockImplementation(() => gate.then(() => ({})));

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(8));
    await userEvent.click(await screen.findByRole("button", { name: "Apply all (8)" }));
    // Only the concurrency limit is in flight; the rest have not been started.
    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledTimes(5));

    await userEvent.click(await screen.findByRole("button", { name: /cancel/i }));
    releaseImports();

    const summary = await findApplyAllSummary();
    expect(summary).toHaveTextContent("Cancelled.");
    // The three rows the cancellation stopped are named rather than dropped from the arithmetic.
    expect(summary).toHaveTextContent("not attempted 3");
    // Cancelling never interrupts an import already sent, so those five still count as applied.
    expect(mocks.importFromMetadataServer).toHaveBeenCalledTimes(5);
  });

  it("keeps a cancelled Search all in charge until it drains, so a second one cannot orphan it", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = Array.from({ length: 8 }, (_, index) => ({
      id: 100 + index,
      title: `Local video ${index}`,
      files: [],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    })) as any;
    // Hold every search open so the batch is still draining when Cancel is pressed.
    let releaseSearches: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      releaseSearches = resolve;
    });
    mocks.searchMetadataServer.mockImplementation(() => gate.then(() => []));

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(5));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));

    // The five searches already sent are still out, so the batch still owns the toolbar: there is no
    // Search all to press that would start a second batch over the same rows.
    expect(screen.getByRole("button", { name: "Cancel" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Search all" })).not.toBeInTheDocument();
    // The searches already sent are told to stop, so the wait for them is as short as the server allows.
    expect(mocks.searchMetadataServer.mock.calls.every((call) => (call[4] as AbortSignal).aborted)).toBe(true);

    releaseSearches();
    await screen.findByRole("button", { name: "Search all" });
    // Cancelling stopped the three rows that had not started.
    expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(5);
  });

  it("returns the rows a cancelled Search all abandoned to idle, without an error", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = Array.from({ length: 8 }, (_, index) => ({
      id: 100 + index,
      title: `Local video ${index}`,
      files: [],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    })) as any;
    // Like fetch: an aborted request rejects straight away with an AbortError.
    mocks.searchMetadataServer.mockImplementation(
      (_id: number, _term: string, _endpoint: string, _strategy: string, signal: AbortSignal) =>
        new Promise((_, reject) =>
          signal.addEventListener("abort", () => reject(new DOMException("The operation was aborted.", "AbortError"))),
        ),
    );

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(5));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));

    await screen.findByRole("button", { name: "Search all" });
    // No row reports a failure, whatever the formatter would have called an abort.
    expect(document.querySelectorAll("p.text-red-400")).toHaveLength(0);
    expect(screen.queryByText(/no (results|matches)/i)).not.toBeInTheDocument();
    // Every row can be searched again.
    for (const button of screen.getAllByRole("button", { name: "Search for this text" })) expect(button).toBeEnabled();
  });

  it("caps the reasons a batch summary names when every row fails differently", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = Array.from({ length: 5 }, (_, index) => ({
      id: 200 + index,
      title: `Local video ${index}`,
      files: [],
      performers: [],
      tags: [],
      urls: [],
      remoteIds: [],
    })) as any;
    mocks.searchMetadataServer.mockImplementation((videoId: number) => Promise.resolve([matchFor(videoId)]));
    // A name conflict quotes the entity it collided with, so a batch can fail for as many reasons as rows.
    mocks.importFromMetadataServer.mockImplementation((videoId: number) =>
      Promise.reject(new Error(`API Error 409: {"message":"Studio ${videoId} already exists."}`)),
    );

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(5));
    await userEvent.click(await screen.findByRole("button", { name: "Apply all (5)" }));

    const summary = await findApplyAllSummary();
    expect(summary).toHaveTextContent("Applied 0, failed 5.");
    expect(summary).toHaveTextContent("And 2 other reasons.");
  });

  it("lets the batch summary be dismissed", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [
      { id: 123, title: "First local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;
    mocks.searchMetadataServer.mockResolvedValue([matchFor(123)]);
    mocks.importFromMetadataServer.mockRejectedValue(new Error('API Error 409: {"message":"Boom."}'));

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledOnce());
    await userEvent.click(await screen.findByRole("button", { name: "Apply all (1)" }));
    expect(await findApplyAllSummary()).toBeTruthy();

    await userEvent.click(screen.getByRole("button", { name: "Dismiss apply summary" }));
    expect(screen.queryByText(/^Applied \d+/)).toBeNull();
    // The button that had focus is gone, so focus lands on the list rather than dropping to the page.
    expect(screen.getByRole("region", { name: "Videos" })).toHaveFocus();
    // The row keeps its own reason: dismissing the summary is not dismissing the failure.
    expect(screen.getByText(/Boom\./)).toBeTruthy();
  });

  it("says nothing about the batch when every save succeeds", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [
      { id: 123, title: "First local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;
    mocks.searchMetadataServer.mockResolvedValue([matchFor(123)]);

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledOnce());
    await userEvent.click(await screen.findByRole("button", { name: "Apply all (1)" }));

    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    expect(await screen.findByText("Saved successfully")).toBeTruthy();
    expect(screen.queryByText(/^Applied \d+/)).toBeNull();
  });

  it("keeps a dismissed video off the list and out of Apply all until it is restored", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const videos = [
      { id: 123, title: "First local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
      { id: 456, title: "Second local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;
    // Both videos match, so Apply all would take both until one is dismissed.
    mocks.searchMetadataServer.mockImplementation((videoId: number) =>
      Promise.resolve([
        {
          id: `remote-${videoId}`,
          endpoint: "https://first.example/graphql",
          metadataServerName: "First provider",
          title: `Result for ${videoId}`,
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
      ]),
    );

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledTimes(2));
    expect(await screen.findByRole("button", { name: "Apply all (2)" })).toBeInTheDocument();

    await userEvent.click(screen.getAllByRole("button", { name: "Dismiss video" })[0]);

    expect(screen.queryByText("First local video")).not.toBeInTheDocument();
    expect(screen.getByText("Second local video")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Apply all (1)" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Apply all (1)" }));
    await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
    expect(mocks.importFromMetadataServer).toHaveBeenCalledWith(456, expect.anything());

    await userEvent.click(screen.getByRole("button", { name: "Restore 1 dismissed" }));
    expect(screen.getByText("First local video")).toBeInTheDocument();
  });

  it("starts each visit showing unmatched videos and never stores the toggle", async () => {
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

    const first = render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Hide Unmatched" }));
    expect(screen.getByRole("button", { name: "Show Unmatched" })).toBeInTheDocument();
    // Other tagger settings are stored; this one must stay out of the persisted config.
    expect(JSON.parse(localStorage.getItem("cove-tagger-config") ?? "{}")).not.toHaveProperty("showUnmatched");

    // A remount stands in for the page reload: the toggle is back to showing unmatched videos.
    first.unmount();
    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} />
      </QueryClientProvider>,
    );
    expect(screen.getByRole("button", { name: "Hide Unmatched" })).toBeInTheDocument();
  });

  it("shows unmatched videos again when the list's page or filters change", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const firstPage = [
      { id: 123, title: "First page video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
      { id: 124, title: "Another first page video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;
    const secondPage = [
      { id: 456, title: "Second page video", files: [], performers: [], tags: [], urls: [], remoteIds: [] },
    ] as any;
    const renderTagger = (videos: any[], resetKey: string) => (
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={videos} resetKey={resetKey} />
      </QueryClientProvider>
    );

    const { rerender } = render(renderTagger(firstPage, "page-1"));
    await userEvent.click(screen.getByRole("button", { name: "Hide Unmatched" }));
    expect(screen.queryByText("First page video")).not.toBeInTheDocument();

    // The same list refetched keeps the toggle where the user left it, even when an apply made the
    // filter drop a video and the ids changed.
    rerender(renderTagger(firstPage.slice(1), "page-1"));
    expect(screen.getByRole("button", { name: "Show Unmatched" })).toBeInTheDocument();

    rerender(renderTagger(secondPage, "page-2"));
    expect(screen.getByRole("button", { name: "Hide Unmatched" })).toBeInTheDocument();
    expect(screen.getByText("Second page video")).toBeInTheDocument();
  });

  it("disables Apply all until something matches", async () => {
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

    expect(screen.getByRole("button", { name: "Apply all" })).toBeDisabled();
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

    await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));
    await userEvent.selectOptions(screen.getByLabelText("Default bulk match strategy"), "remote-id");
    await userEvent.click(screen.getByRole("button", { name: "Save default" }));
    await userEvent.click(screen.getByRole("button", { name: "Search all" }));

    await waitFor(() => expect(mocks.searchMetadataServer).toHaveBeenCalledOnce());
    expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
      123,
      "Local video",
      "https://first.example/graphql",
      "remote-id",
      expect.any(AbortSignal),
    );
    expect(JSON.parse(localStorage.getItem("cove-tagger-config") ?? "{}").bulkMatchStrategy).toBe("remote-id");
  });

  it("closes the toolbar menu on an outside click and on Escape", async () => {
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
    // The overflow menu is gone; the bulk search strategy picker is the toolbar's remaining menu.
    const menu = screen.getByRole("button", { name: "Choose search strategy" }).closest("details")!;
    await userEvent.click(screen.getByRole("button", { name: "Choose search strategy" }));
    expect(menu.open).toBe(true);
    await userEvent.click(document.body);
    expect(menu.open).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Choose search strategy" }));
    expect(menu.open).toBe(true);
    await userEvent.keyboard("{Escape}");
    expect(menu.open).toBe(false);
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
      "text",
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

    await userEvent.click(screen.getByRole("button", { name: "Search all" }));
    await waitFor(() =>
      expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
        123,
        "Local video",
        "https://first.example/graphql",
        "remote-id-fingerprint",
        expect.any(AbortSignal),
      ),
    );

    // Both of the row's search modes sit beside the query box rather than in the overflow menu, and
    // neither takes the saved bulk strategy: one searches by text alone, the other by content alone.
    mocks.searchMetadataServer.mockClear();
    await userEvent.click(screen.getByRole("button", { name: "Identify by file content" }));
    await waitFor(() =>
      expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
        123,
        undefined,
        "https://first.example/graphql",
        "fingerprint",
      ),
    );

    mocks.searchMetadataServer.mockClear();
    await userEvent.click(screen.getByRole("button", { name: "Search for this text" }));
    await waitFor(() =>
      expect(mocks.searchMetadataServer).toHaveBeenCalledWith(
        123,
        "Local video",
        "https://first.example/graphql",
        "text",
        undefined,
      ),
    );

    await userEvent.click(screen.getByRole("button", { name: "More actions" }));
    expect(screen.getByRole("button", { name: "Submit fingerprints" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Submit as draft" })).toBeInTheDocument();
    expect(screen.queryByText("Search by fingerprint only")).not.toBeInTheDocument();
  });

  it("closes the draft tab and shows the reason when the draft is rejected", async () => {
    const tab = { closed: false, opener: {}, close: vi.fn(), location: { replace: vi.fn() }, document: {} };
    const openSpy = vi.spyOn(window, "open").mockReturnValue(tab as unknown as Window);
    mocks.submitMetadataServerDraft.mockRejectedValue(new Error("Draft rejected by server"));
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

    try {
      render(
        <QueryClientProvider client={queryClient}>
          <VideoTagger videos={[video]} />
        </QueryClientProvider>,
      );

      await userEvent.click(screen.getByRole("button", { name: "More actions" }));
      await userEvent.click(screen.getByRole("button", { name: "Submit as draft" }));

      expect(await screen.findByText("Draft rejected by server")).toBeInTheDocument();
      expect(tab.close).toHaveBeenCalledOnce();
      expect(tab.location.replace).not.toHaveBeenCalled();
      expect(screen.queryByRole("link", { name: "Open draft" })).not.toBeInTheDocument();
    } finally {
      openSpy.mockRestore();
    }
  });

  it("opens a submitted draft in a new tab and links to it from the row", async () => {
    const tab = { closed: false, opener: {}, close: vi.fn(), location: { replace: vi.fn() }, document: {} };
    const openSpy = vi.spyOn(window, "open").mockReturnValue(tab as unknown as Window);
    mocks.submitMetadataServerDraft.mockResolvedValue({ draftId: "draft-1" });
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

    try {
      render(
        <QueryClientProvider client={queryClient}>
          <VideoTagger videos={[video]} />
        </QueryClientProvider>,
      );

      await userEvent.click(screen.getByRole("button", { name: "More actions" }));
      await userEvent.click(screen.getByRole("button", { name: "Submit as draft" }));

      expect(openSpy).toHaveBeenCalledWith("", "_blank");
      expect(await screen.findByRole("link", { name: "Open draft" })).toHaveAttribute(
        "href",
        "https://first.example/drafts/draft-1",
      );
      expect(mocks.submitMetadataServerDraft).toHaveBeenCalledWith(123, "https://first.example/graphql");
      expect(tab.location.replace).toHaveBeenCalledWith("https://first.example/drafts/draft-1");
    } finally {
      openSpy.mockRestore();
    }
  });

  it("offers tags, performers and studio from a YAML scraper's object-shaped result", async () => {
    mocks.listScrapers.mockResolvedValue([
      {
        id: "pack/site:video",
        name: "Site Scraper",
        entityType: "video",
        supportedScrapes: ["url"],
        urls: ["site.example/watch/"],
        sourcePath: "",
      },
    ]);
    mocks.createScrapeAttempt.mockResolvedValue({
      id: "attempt-1",
      scraperId: "pack/site:video",
      entityType: "video",
      entityId: 123,
      inputKind: "url",
      status: "Success",
      error: null,
      candidateResultsJson: null,
      resultJson: JSON.stringify({
        Title: "Scraped title",
        URL: "https://site.example/watch/1",
        Tags: [
          { Name: "Countdown", URL: "https://site.example/tag/countdown" },
          { Name: "Edging" },
          { Name: "countdown" },
        ],
        Performers: [{ Name: "Scraped Performer", URL: "https://site.example/model/1" }],
        Studio: [{ Name: "Scraped Studio", URL: "https://site.example/store/1" }],
      }),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "Local video",
      files: [{ duration: 60, basename: "video.mp4", path: "/library/video.mp4" }],
      performers: [],
      tags: [],
      urls: ["https://site.example/watch/1"],
      remoteIds: [],
    } as any;

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} mode="detail" />
      </QueryClientProvider>,
    );

    await screen.findByRole("option", { name: "Site Scraper (Scraper)" });
    await userEvent.selectOptions(screen.getByRole("combobox"), "scraper:pack/site:video");
    // Only a metadata server can identify a file by its content.
    expect(screen.queryByRole("button", { name: "Identify by file content" })).not.toBeInTheDocument();
    await userEvent.type(screen.getByPlaceholderText("Video URL..."), "{Enter}");

    await waitFor(() => expect(mocks.createScrapeAttempt).toHaveBeenCalledOnce());
    // The compact review lists every scraped tag and performer as a chip, deduplicated by name; one the
    // library does not have is not added, since the tagger creates missing items only when asked to; and
    // the studio as a field that fills the empty current one.
    expect((await screen.findByText("Countdown")).closest("[data-state]")).toHaveAttribute(
      "data-state",
      "not-in-library",
    );
    expect(screen.getByText("Edging").closest("[data-state]")).toHaveAttribute("data-state", "not-in-library");
    expect(screen.queryByText("countdown")).not.toBeInTheDocument();
    expect(screen.getByText("Scraped Performer").closest("[data-state]")).toHaveAttribute(
      "data-state",
      "not-in-library",
    );
    // The studio is new too, so it is offered for creation rather than presented as a fill.
    const studio = screen.getByText("Studio").closest("[data-tone]")!;
    expect(within(studio as HTMLElement).getByText("Scraped Studio")).toBeInTheDocument();
    expect(within(studio as HTMLElement).getByText("not in your library")).toBeInTheDocument();
    expect(mocks.resolveRelations).toHaveBeenCalledWith(expect.objectContaining({ studios: ["Scraped Studio"] }));
  });

  // Scrapes a video whose result names a studio, leaving the review open for the test to act on.
  async function scrapeWithStudio(studioName = "Scraped Studio") {
    mocks.listScrapers.mockResolvedValue([
      {
        id: "pack/site:video",
        name: "Site Scraper",
        entityType: "video",
        supportedScrapes: ["url"],
        urls: ["site.example/watch/"],
        sourcePath: "",
      },
    ]);
    mocks.createScrapeAttempt.mockResolvedValue({
      id: "attempt-1",
      scraperId: "pack/site:video",
      entityType: "video",
      entityId: 123,
      inputKind: "url",
      status: "Success",
      error: null,
      candidateResultsJson: null,
      resultJson: JSON.stringify({ Title: "Scraped title", Studio: studioName }),
    });
    mocks.applyScrapeAttempt.mockResolvedValue({ id: "attempt-1", status: "Applied" });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "",
      files: [{ duration: 60, basename: "video.mp4", path: "/library/video.mp4" }],
      performers: [],
      tags: [],
      urls: ["https://site.example/watch/1"],
      remoteIds: [],
    } as any;
    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} mode="detail" />
      </QueryClientProvider>,
    );
    await screen.findByRole("option", { name: "Site Scraper (Scraper)" });
    await userEvent.selectOptions(screen.getByRole("combobox"), "scraper:pack/site:video");
    await userEvent.type(screen.getByPlaceholderText("Video URL..."), "{Enter}");
    await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());
  }

  const applyRow = async () => {
    await userEvent.click(await screen.findByRole("button", { name: /^Apply/ }));
    await waitFor(() => expect(mocks.applyScrapeAttempt).toHaveBeenCalledOnce());
    return mocks.applyScrapeAttempt.mock.calls[0][1];
  };

  // Create sits in the full rows only, as the decisions on new tags and performers do.
  async function createStudioInAdjust(studioName: string) {
    await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
    await userEvent.click(await screen.findByRole("button", { name: `Studio: create “${studioName}” and use it` }));
    await userEvent.click(screen.getByRole("button", { name: "Done adjusting" }));
  }

  it("does not create a studio the library lacks unless the person chooses Create", async () => {
    await scrapeWithStudio();
    expect(await screen.findByText("not in your library")).toBeInTheDocument();
    // The compact row only reads the studio as new; creating it is decided in Adjust….
    expect(screen.queryByRole("button", { name: /^Studio: create/ })).not.toBeInTheDocument();
    const request = await applyRow();
    expect(request.createMissingStudio).toBe(false);
    // The studio is left alone rather than "replaced" with nothing, so the attempt records no studio.
    expect(request.collectionModes.studio).toBe("skip");
  });

  it("does not carry a Create choice over to another studio a new search returns", async () => {
    await scrapeWithStudio();
    await createStudioInAdjust("Scraped Studio");
    mocks.createScrapeAttempt.mockResolvedValue({
      ...(await mocks.createScrapeAttempt.mock.results[0].value),
      id: "attempt-2",
      resultJson: JSON.stringify({ Title: "Scraped title", Studio: "Other Studio" }),
    });
    await userEvent.type(screen.getByPlaceholderText("Video URL..."), "{Enter}");
    await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
    expect(await screen.findByRole("button", { name: "Studio: create “Other Studio” and use it" })).toBeInTheDocument();
    const request = await applyRow();
    expect(request.createMissingStudio).toBe(false);
  });

  it("reviews a studio whose name is also an object member", async () => {
    await scrapeWithStudio("constructor");
    await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
    expect(await screen.findByRole("button", { name: "Studio: create “constructor” and use it" })).toBeInTheDocument();
  });

  it("creates a studio the library lacks once the person chooses Create", async () => {
    await scrapeWithStudio();
    await createStudioInAdjust("Scraped Studio");
    expect(await screen.findByText("fills empty · new, will be created")).toBeInTheDocument();
    const request = await applyRow();
    expect(request.createMissingStudio).toBe(true);
    expect(request.collectionModes.studio).toBe("replace");
  });

  it("shows a scraped studio the library has under its library name", async () => {
    mocks.resolveRelations.mockResolvedValue({
      tags: [],
      performers: [],
      studios: [{ input: "Scraped Studio", matchedName: "Library Studio" }],
    });
    await scrapeWithStudio();
    const studio = (await screen.findByText("Library Studio")).closest("[data-tone]")!;
    expect(studio).toHaveAttribute("data-tone", "ok");
    expect(within(studio as HTMLElement).queryByText("not in your library")).not.toBeInTheDocument();
    const request = await applyRow();
    expect(request.createMissingStudio).toBe(false);
  });

  it("asks the library again when the row is searched again, so a studio aliased since is matched", async () => {
    await scrapeWithStudio();
    expect(await screen.findByText("not in your library")).toBeInTheDocument();
    // The studio gets the scraped name as an alias elsewhere; the same search returns the same names.
    mocks.resolveRelations.mockResolvedValue({
      tags: [],
      performers: [],
      studios: [{ input: "Scraped Studio", matchedName: "Library Studio" }],
    });
    await userEvent.type(screen.getByPlaceholderText("Video URL..."), "{Enter}");
    expect(await screen.findByText("Library Studio")).toBeInTheDocument();
    expect(screen.queryByText("not in your library")).not.toBeInTheDocument();
    expect(mocks.resolveRelations).toHaveBeenCalledTimes(2);
  });

  it("asks the library again for another result chosen after a re-search, whose answer predates it", async () => {
    const result = (id: string, title: string, tag: string) => ({
      ...matchFor(123),
      id,
      title,
      tagNames: [tag],
      tagCandidates: [{ remoteId: tag, name: tag, existsLocally: false }],
    });
    mocks.findMetadataServerByIds.mockImplementation(() =>
      Promise.resolve([
        result("first-video-id", "First result", "Tag A"),
        result("second-video-id", "Second result", "Tag B"),
      ]),
    );
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
    const choose = async (name: string) => {
      const more = screen.queryByRole("button", { name: /other match/i });
      if (more) await userEvent.click(more);
      await userEvent.click(await screen.findByRole("button", { name }));
      await waitFor(() => expect(screen.getByRole("button", { name: /^Apply/ })).toBeEnabled());
    };
    const refresh = async () => {
      await userEvent.click(screen.getByRole("button", { name: "Refresh from First provider" }));
      await waitFor(() => expect(screen.getByRole("button", { name: /^Apply/ })).toBeEnabled());
    };
    // Both results answered once, then the row is searched again on the first.
    await refresh();
    await choose("Use Second result");
    await choose("Use First result");
    expect(mocks.resolveRelations).toHaveBeenCalledTimes(2);
    await new Promise((resolve) => setTimeout(resolve, 5));
    await refresh();
    expect(mocks.resolveRelations).toHaveBeenCalledTimes(3);
    // The second result's answer is from before this search too, so choosing it asks again.
    await choose("Use Second result");
    expect(mocks.resolveRelations).toHaveBeenCalledTimes(4);
  });

  it("does not show a re-search's check as a retry because an earlier lookup once failed", async () => {
    mocks.resolveRelations.mockRejectedValueOnce(new Error("lookup failed"));
    await scrapeWithStudio();
    const failure = await screen.findByText("Couldn't check which of these are in your library.");
    await userEvent.click(within(failure.parentElement!).getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(screen.getByRole("button", { name: /^Apply/ })).toBeEnabled());
    await new Promise((resolve) => setTimeout(resolve, 5));
    let answer!: () => void;
    mocks.resolveRelations.mockImplementationOnce(
      () => new Promise((resolve) => (answer = () => resolve({ tags: [], performers: [], studios: [] }))),
    );
    await userEvent.type(screen.getByPlaceholderText("Video URL..."), "{Enter}");
    await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalledTimes(3));
    expect(screen.queryByText("Checking your library again…")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Checking library…" })).toBeDisabled();
    answer();
    await waitFor(() => expect(screen.getByRole("button", { name: /^Apply/ })).toBeEnabled());
  });

  it("asks the library again on window focus once its answer is old, but not after a failure", async () => {
    await scrapeWithStudio();
    expect(await screen.findByText("not in your library")).toBeInTheDocument();
    const refocus = () => {
      focusManager.setFocused(false);
      focusManager.setFocused(true);
    };
    try {
      // Thirty seconds on, the answer is old, and focusing the window asks again.
      vi.spyOn(Date, "now").mockReturnValue(Date.now() + 60_000);
      mocks.resolveRelations.mockResolvedValue({
        tags: [],
        performers: [],
        studios: [{ input: "Scraped Studio", matchedName: "Library Studio" }],
      });
      refocus();
      expect(await screen.findByText("Library Studio")).toBeInTheDocument();
      expect(mocks.resolveRelations).toHaveBeenCalledTimes(2);
    } finally {
      vi.mocked(Date.now).mockRestore();
      focusManager.setFocused(undefined);
    }
  });

  it("does not put a failed lookup back to checking when the window is focused", async () => {
    mocks.resolveRelations.mockRejectedValue(new Error("lookup failed"));
    await scrapeWithStudio();
    expect(await screen.findByText("Couldn't check which of these are in your library.")).toBeInTheDocument();
    try {
      focusManager.setFocused(false);
      focusManager.setFocused(true);
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(mocks.resolveRelations).toHaveBeenCalledOnce();
    } finally {
      focusManager.setFocused(undefined);
    }
  });

  async function linkStudio(rememberAlias: boolean) {
    await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
    await userEvent.click(
      await screen.findByRole("button", { name: "Studio: link “Scraped Studio” to one in your library" }),
    );
    await userEvent.click(screen.getByRole("button", { name: "Pick Library Studio" }));
    if (!rememberAlias) await userEvent.click(screen.getByLabelText(/Remember “Scraped Studio” as an alias/));
    await userEvent.click(screen.getByRole("button", { name: "Link" }));
  }

  it("uses a library studio linked for this video alone, without saving an alias", async () => {
    await scrapeWithStudio();
    await linkStudio(false);
    expect(mocks.addStudioAlias).not.toHaveBeenCalled();
    // The row now reads the library studio as the one the video gets, with the scraped name on hover.
    expect(await screen.findByTitle("Scraped as “Scraped Studio”")).toHaveTextContent("Library Studio");
    await userEvent.click(screen.getByRole("button", { name: "Done adjusting" }));
    const request = await applyRow();
    expect(request.linkedStudioId).toBe(77);
    expect(request.collectionModes.studio).toBe("replace");
    expect(request.createMissingStudio).toBe(false);
  });

  it("hands focus back to the studio's Link when its panel is cancelled", async () => {
    await scrapeWithStudio();
    await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
    await userEvent.click(
      await screen.findByRole("button", { name: "Studio: link “Scraped Studio” to one in your library" }),
    );
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Studio: link “Scraped Studio” to one in your library" }),
      ).toHaveFocus(),
    );
  });

  it("does not carry a studio linked for this video over to another studio a new search returns", async () => {
    await scrapeWithStudio();
    await linkStudio(false);
    mocks.createScrapeAttempt.mockResolvedValue({
      ...(await mocks.createScrapeAttempt.mock.results[0].value),
      id: "attempt-2",
      resultJson: JSON.stringify({ Title: "Scraped title", Studio: "Other Studio" }),
    });
    await userEvent.type(screen.getByPlaceholderText("Video URL..."), "{Enter}");
    expect(await screen.findByText("Other Studio")).toBeInTheDocument();
    const request = await applyRow();
    expect(request.linkedStudioId).toBeUndefined();
    expect(request.collectionModes.studio).toBe("skip");
  });

  it("remembers the scraped studio as an alias, and the library lookup then matches it", async () => {
    mocks.addStudioAlias.mockImplementation(async () => {
      mocks.resolveRelations.mockResolvedValue({
        tags: [],
        performers: [],
        studios: [{ input: "Scraped Studio", matchedName: "Library Studio" }],
      });
      return {};
    });
    await scrapeWithStudio();
    await linkStudio(true);
    expect(mocks.addStudioAlias).toHaveBeenCalledWith(77, "Scraped Studio");
    expect(await screen.findByTitle("Scraped as “Scraped Studio”")).toHaveTextContent("Library Studio");
    await userEvent.click(screen.getByRole("button", { name: "Done adjusting" }));
    const request = await applyRow();
    // Matched by its new alias, so the apply looks it up as any other.
    expect(request.linkedStudioId).toBeUndefined();
    expect(request.collectionModes.studio).toBe("replace");
  });

  it("keeps a saved choice to create missing studios", async () => {
    localStorage.setItem("cove-tagger-config", JSON.stringify({ onlyExistingStudio: false }));
    await scrapeWithStudio();
    expect(await screen.findByText("fills empty · new, will be created")).toBeInTheDocument();
    const request = await applyRow();
    expect(request.createMissingStudio).toBe(true);
  });

  // Scrapes a video with a URL scraper that returns two tags and a performer, then applies the row.
  async function scrapeAndApply() {
    mocks.listScrapers.mockResolvedValue([
      {
        id: "pack/site:video",
        name: "Site Scraper",
        entityType: "video",
        supportedScrapes: ["url"],
        urls: ["site.example/watch/"],
        sourcePath: "",
      },
    ]);
    mocks.createScrapeAttempt.mockResolvedValue({
      id: "attempt-1",
      scraperId: "pack/site:video",
      entityType: "video",
      entityId: 123,
      inputKind: "url",
      status: "Success",
      error: null,
      candidateResultsJson: null,
      resultJson: JSON.stringify({
        Title: "Scraped title",
        Tags: [{ Name: "Countdown" }, { Name: "Edging" }],
        Performers: [{ Name: "Scraped Performer" }],
      }),
    });
    mocks.applyScrapeAttempt.mockResolvedValue({ id: "attempt-1", status: "Applied" });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const video = {
      id: 123,
      title: "",
      files: [{ duration: 60, basename: "video.mp4", path: "/library/video.mp4" }],
      performers: [],
      tags: [],
      urls: ["https://site.example/watch/1"],
      remoteIds: [],
    } as any;

    render(
      <QueryClientProvider client={queryClient}>
        <VideoTagger videos={[video]} mode="detail" />
      </QueryClientProvider>,
    );

    await screen.findByRole("option", { name: "Site Scraper (Scraper)" });
    await userEvent.selectOptions(screen.getByRole("combobox"), "scraper:pack/site:video");
    await userEvent.type(screen.getByPlaceholderText("Video URL..."), "{Enter}");
    const countdown = (await screen.findByText("Countdown")).closest("[data-state]");
    await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());
    await userEvent.click(await screen.findByRole("button", { name: /^Apply/ }));
    await waitFor(() => expect(mocks.applyScrapeAttempt).toHaveBeenCalledOnce());
    return { countdown, request: mocks.applyScrapeAttempt.mock.calls[0][1] };
  }

  it("keeps a saved choice to create missing tags and performers", async () => {
    localStorage.setItem(
      "cove-tagger-config",
      JSON.stringify({ onlyExistingTags: false, onlyExistingPerformers: false }),
    );
    const { countdown, request } = await scrapeAndApply();
    expect(countdown).toHaveAttribute("data-state", "new");
    expect(request.createMissingTags).toBe(true);
    expect(request.tagSelections).toEqual([
      { name: "Countdown", action: "create" },
      { name: "Edging", action: "create" },
    ]);
    expect(request.performerSelections).toEqual([{ name: "Scraped Performer", action: "create" }]);
  });

  it("leaves out a new name once the library lookup has answered that it does not have it", async () => {
    // The default lookup answers that the library has none of the scraped names.
    const { request } = await scrapeAndApply();
    expect(request.tagSelections).toEqual([
      { name: "Countdown", action: "exclude" },
      { name: "Edging", action: "exclude" },
    ]);
    expect(request.performerSelections).toEqual([{ name: "Scraped Performer", action: "exclude" }]);
  });

  describe("before the library lookup has answered", () => {
    const noMatches = { tags: [], performers: [], studios: [] };

    // A lookup the test answers, or fails, when it chooses.
    function heldLookup() {
      const held = {} as { answer: (result: object) => void; fail: (error: Error) => void };
      mocks.resolveRelations.mockImplementationOnce(
        () =>
          new Promise((resolve, reject) => {
            held.answer = resolve;
            held.fail = reject;
          }),
      );
      return held;
    }

    // Scrapes one video with a URL scraper, leaving its review open for the test to act on.
    async function scrape(result: object, videoTags: { id: number; name: string }[] = []) {
      mocks.listScrapers.mockResolvedValue([
        {
          id: "pack/site:video",
          name: "Site Scraper",
          entityType: "video",
          supportedScrapes: ["url"],
          urls: ["site.example/watch/"],
          sourcePath: "",
        },
      ]);
      mocks.createScrapeAttempt.mockResolvedValue({
        id: "attempt-1",
        scraperId: "pack/site:video",
        entityType: "video",
        entityId: 123,
        inputKind: "url",
        status: "Success",
        error: null,
        candidateResultsJson: null,
        resultJson: JSON.stringify(result),
      });
      mocks.applyScrapeAttempt.mockResolvedValue({ id: "attempt-1", status: "Applied" });
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      const video = {
        id: 123,
        title: "",
        files: [{ duration: 60, basename: "video.mp4", path: "/library/video.mp4" }],
        performers: [],
        tags: videoTags,
        urls: ["https://site.example/watch/1"],
        remoteIds: [],
      } as any;
      render(
        <QueryClientProvider client={queryClient}>
          <VideoTagger videos={[video]} mode="detail" />
        </QueryClientProvider>,
      );
      await screen.findByRole("option", { name: "Site Scraper (Scraper)" });
      await userEvent.selectOptions(screen.getByRole("combobox"), "scraper:pack/site:video");
      await userEvent.type(screen.getByPlaceholderText("Video URL..."), "{Enter}");
      await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());
      return queryClient;
    }

    const fullResult = {
      Title: "Scraped title",
      Studio: "Scraped Studio",
      Tags: [{ Name: "Countdown" }, { Name: "Edging" }],
      Performers: [{ Name: "Scraped Performer" }],
    };

    it("leaves tags alone once Set tags is unchecked, whatever was chosen in the row before", async () => {
      await scrape(fullResult);
      await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
      await userEvent.click(await screen.findByRole("button", { name: "Use source Tags" }));
      await userEvent.click(screen.getByRole("button", { name: "Tagger settings" }));
      await userEvent.click(screen.getByRole("checkbox", { name: "Set tags" }));
      expect(screen.queryByRole("group", { name: "Tags" })).not.toBeInTheDocument();

      await userEvent.click(screen.getByRole("button", { name: /^Apply/ }));
      await waitFor(() => expect(mocks.applyScrapeAttempt).toHaveBeenCalledOnce());
      const request = mocks.applyScrapeAttempt.mock.calls[0][1];
      expect(request.collectionModes.tags).toBe("skip");
      expect(request.addedTagIds).toBeUndefined();
      expect(request.removedTagIds).toBeUndefined();
    });

    it("holds the tags, performers, studio and Apply until it answers, and leaves the other fields usable", async () => {
      const lookup = heldLookup();
      await scrape(fullResult);

      expect(await screen.findByRole("button", { name: /Checking library/ })).toBeDisabled();
      for (const label of ["Tags", "Performers", "Studio"]) {
        const row = screen.getByText(label, { selector: "span" }).closest("[data-tone]") as HTMLElement;
        expect(within(row).getByText("Checking your library…")).toBeInTheDocument();
      }
      // Nothing is called new on a guess before the library has said.
      expect(screen.queryByText(/not in your library/)).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Title: keep empty instead" })).toBeEnabled();

      lookup.answer({ ...noMatches, tags: [{ input: "Countdown", matchedName: "Countdown" }] });
      await userEvent.click(await screen.findByRole("button", { name: /^Apply/ }));
      await waitFor(() => expect(mocks.applyScrapeAttempt).toHaveBeenCalledOnce());
      expect(mocks.applyScrapeAttempt.mock.calls[0][1].tagSelections).toEqual([
        { name: "Countdown", action: "include" },
        { name: "Edging", action: "exclude" },
      ]);
    });

    it("says when it failed and offers a retry instead of staying disabled", async () => {
      mocks.resolveRelations.mockRejectedValueOnce(new Error("lookup failed"));
      await scrape(fullResult);

      const failure = await screen.findByText("Couldn't check which of these are in your library.");
      expect(screen.getByRole("button", { name: /^Apply/ })).toBeDisabled();
      // The page says it once, from a region that was there before it had anything to say.
      await waitFor(() =>
        expect(
          screen.getByText(/Couldn't check some videos against your library/).closest("[role=status]"),
        ).not.toBeNull(),
      );

      const answer = heldLookup();
      const retry = within(failure).getByRole("button", { name: "Retry" });
      await userEvent.click(retry);
      // Asked again: the line and its button stay, so keyboard focus is not dropped.
      expect(await screen.findByText("Checking your library again…")).toBeInTheDocument();
      expect(retry).toHaveFocus();
      expect(retry).toHaveAttribute("aria-disabled", "true");

      answer.answer(noMatches);
      const apply = await screen.findByRole("button", { name: /^Apply/ });
      await waitFor(() => expect(apply).toBeEnabled());
      expect(screen.queryByText("Couldn't check which of these are in your library.")).not.toBeInTheDocument();
      await waitFor(() => expect(apply).toHaveFocus());
      expect((await screen.findByText("Finished checking your library.")).closest("[role=status]")).not.toBeNull();
    });

    it("leaves focus on a Retry that fails again, and does not move it when the lookup later answers", async () => {
      mocks.resolveRelations.mockRejectedValueOnce(new Error("lookup failed"));
      const queryClient = await scrape(fullResult);
      const failure = await screen.findByText("Couldn't check which of these are in your library.");
      const retry = within(failure).getByRole("button", { name: "Retry" });

      mocks.resolveRelations.mockRejectedValueOnce(new Error("lookup failed again"));
      await userEvent.click(retry);
      await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalledTimes(2));
      await waitFor(() => expect(retry).not.toHaveAttribute("aria-disabled"));
      expect(retry).toHaveFocus();

      // The answer arrives some other way (another row's Retry, a reconnect) while focus is elsewhere.
      retry.blur();
      mocks.resolveRelations.mockResolvedValueOnce(noMatches);
      await queryClient.refetchQueries({ queryKey: ["tagger-resolve-relations"] });
      await waitFor(() => expect(screen.getByRole("button", { name: /^Apply/ })).toBeEnabled());
      expect(screen.getByRole("button", { name: /^Apply/ })).not.toHaveFocus();
    });

    it("fails a retry made offline rather than leaving it checking, so Retry is offered again", async () => {
      mocks.resolveRelations.mockRejectedValueOnce(new Error("lookup failed"));
      await scrape(fullResult);
      const failure = await screen.findByText("Couldn't check which of these are in your library.");
      onlineManager.setOnline(false);
      try {
        mocks.resolveRelations.mockRejectedValueOnce(new TypeError("Failed to fetch"));
        await userEvent.click(within(failure).getByRole("button", { name: "Retry" }));
        await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalledTimes(2));
        expect(await screen.findByText("Couldn't check which of these are in your library.")).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Retry" })).not.toHaveAttribute("aria-disabled");
      } finally {
        onlineManager.setOnline(true);
      }
    });

    it("calls no scraped item new or matched in the full rows while it waits, and keeps them disabled", async () => {
      heldLookup();
      await scrape(fullResult);
      await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
      expect(screen.queryByText("Not in your library")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Add Tags: Countdown" })).toBeDisabled();
      expect(screen.getByRole("button", { name: "Add Performers: Scraped Performer" })).toBeDisabled();
      expect(screen.queryByRole("button", { name: /^Link Tags/ })).not.toBeInTheDocument();
    });

    it("keeps a tag the video has when an excluded scraped name later turns out to be its alias", async () => {
      localStorage.setItem("cove-tagger-config", JSON.stringify({ onlyExistingTags: false }));
      const queryClient = await scrape({ Title: "Scraped title", Tags: [{ Name: "Tit Tease" }] }, [
        { id: 2, name: "Tit Worship" },
      ]);
      await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
      await userEvent.click(await screen.findByRole("button", { name: "Remove Tags: Tit Tease" }));
      await userEvent.click(screen.getByRole("button", { name: "Use source Tags" }));

      // Another row links the name, so this row's lookup now lands it on the tag the video has.
      mocks.resolveRelations.mockResolvedValue({
        ...noMatches,
        tags: [{ input: "Tit Tease", matchedName: "Tit Worship" }],
      });
      await queryClient.invalidateQueries({ queryKey: ["tagger-resolve-relations"] });
      await waitFor(() =>
        expect(screen.queryByRole("button", { name: "Remove Tags: Tit Tease" })).not.toBeInTheDocument(),
      );
      // The exclusion was made on a separate new name; on the tag the video has it no longer means anything.
      expect(screen.getByRole("button", { name: "Remove Tit Worship" })).toBeInTheDocument();

      await userEvent.click(screen.getByRole("button", { name: /^Apply/ }));
      await waitFor(() => expect(mocks.applyScrapeAttempt).toHaveBeenCalledOnce());
      const request = mocks.applyScrapeAttempt.mock.calls[0][1];
      expect(request.collectionModes.tags).toBe("replace");
      expect(request.tagSelections).toEqual([{ name: "Tit Tease", action: "include" }]);
      expect(request.removedTagIds).toBeUndefined();
    });

    describe("Apply all", () => {
      // A metadata-server match with a tag its search did not find, which the row asks the library about.
      const withUnmatchedTag = () =>
        mocks.searchMetadataServer.mockResolvedValue([
          {
            ...matchFor(123),
            tagNames: ["Unmatched"],
            tagCandidates: [{ remoteId: "tag-1", name: "Unmatched", existsLocally: false }],
          },
        ]);

      async function searchAndApplyAll() {
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const video = { id: 123, title: "Local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] };
        const view = render(
          <QueryClientProvider client={queryClient}>
            <VideoTagger videos={[video] as any} />
          </QueryClientProvider>,
        );
        await userEvent.click(screen.getByRole("button", { name: "Search all" }));
        await userEvent.click(await screen.findByRole("button", { name: "Apply all (1)" }));
        // The video leaves the page (the list changes), taking its row with it.
        const removeRow = () =>
          view.rerender(
            <QueryClientProvider client={queryClient}>
              <VideoTagger videos={[]} />
            </QueryClientProvider>,
          );
        return { removeRow };
      }

      it("waits for a row's lookup and applies with its answer", async () => {
        withUnmatchedTag();
        const lookup = heldLookup();
        await searchAndApplyAll();
        await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());
        await new Promise((resolve) => setTimeout(resolve, 20));
        expect(mocks.importFromMetadataServer).not.toHaveBeenCalled();

        lookup.answer({ ...noMatches, tags: [{ input: "Unmatched", matchedName: "Library Tag" }] });
        await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
        expect(mocks.importFromMetadataServer.mock.calls[0][1].excludedTagNames).toBeUndefined();
      });

      it("applies a ready row while more rows than it has slots still wait for their lookups", async () => {
        // Videos 1 to 5 each have a tag to ask the library about, and their lookups never answer; video 6
        // needs no lookup. Waiting rows must not hold the five slots the ready one needs.
        mocks.searchMetadataServer.mockImplementation(async (videoId: number) => [
          videoId === 6
            ? matchFor(videoId)
            : {
                ...matchFor(videoId),
                tagNames: [`Unmatched ${videoId}`],
                tagCandidates: [{ remoteId: `tag-${videoId}`, name: `Unmatched ${videoId}`, existsLocally: false }],
              },
        ]);
        mocks.resolveRelations.mockImplementation(() => new Promise(() => {}));
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        const videos = [1, 2, 3, 4, 5, 6].map((id) => ({
          id,
          title: `Local video ${id}`,
          files: [],
          performers: [],
          tags: [],
          urls: [],
          remoteIds: [],
        }));
        render(
          <QueryClientProvider client={queryClient}>
            <VideoTagger videos={videos as any} />
          </QueryClientProvider>,
        );
        await userEvent.click(screen.getByRole("button", { name: "Search all" }));
        await userEvent.click(await screen.findByRole("button", { name: "Apply all (6)" }));

        await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
        expect(mocks.importFromMetadataServer.mock.calls[0][0]).toBe(6);
      });

      it("counts a row whose lookup failed as failed, with the reason", async () => {
        withUnmatchedTag();
        const lookup = heldLookup();
        await searchAndApplyAll();
        await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());
        lookup.fail(new Error("lookup failed"));

        const summary = await findApplyAllSummary();
        expect(summary).toHaveTextContent(/^Applied 0, failed 1\. The library check failed/);
        expect(mocks.importFromMetadataServer).not.toHaveBeenCalled();
      });

      it("sends nothing for a waiting row once the batch is cancelled, and counts it as not attempted", async () => {
        withUnmatchedTag();
        const lookup = heldLookup();
        await searchAndApplyAll();
        await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());
        await userEvent.click(await screen.findByRole("button", { name: /cancel/i }));

        const summary = await findApplyAllSummary();
        expect(summary).toHaveTextContent("Cancelled. Applied 0, not attempted 1.");
        lookup.answer(noMatches);
        await new Promise((resolve) => setTimeout(resolve, 20));
        expect(mocks.importFromMetadataServer).not.toHaveBeenCalled();
      });

      it("drops a waiting row's apply when the row goes away, without marking the row failed", async () => {
        withUnmatchedTag();
        heldLookup();
        const { removeRow } = await searchAndApplyAll();
        await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());
        removeRow();

        // Its result went away before its turn, as for a row that disappears: skipped, not "not attempted",
        // which is for rows a cancellation stopped.
        const summary = await findApplyAllSummary();
        expect(summary).toHaveTextContent("Applied 0, skipped 1.");
        expect(summary).not.toHaveTextContent(/failed/);
        expect(mocks.importFromMetadataServer).not.toHaveBeenCalled();
      });

      it("locks a row's search and result choice while Apply all applies it", async () => {
        mocks.searchMetadataServer.mockResolvedValue([
          {
            ...matchFor(123),
            tagNames: ["Unmatched"],
            tagCandidates: [{ remoteId: "tag-1", name: "Unmatched", existsLocally: false }],
          },
          { ...matchFor(123), id: "second-result", title: "Second result" },
        ]);
        const lookup = heldLookup();
        await searchAndApplyAll();
        await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalled());

        // Waiting for its library check inside Apply all: what was shown at the click is what is applied.
        expect(screen.getByRole("button", { name: "Search for this text" })).toBeDisabled();
        const more = screen.queryByRole("button", { name: /other match/i });
        if (more) await userEvent.click(more);
        const second = screen.getByRole("button", { name: "Use Second result" });
        expect(second).toHaveAttribute("aria-disabled", "true");
        await userEvent.click(second);
        // Still not the selected one.
        expect(screen.getByRole("button", { name: "Use Second result" })).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Dismiss video" })).toBeDisabled();

        lookup.answer(noMatches);
        await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
        expect(mocks.importFromMetadataServer.mock.calls[0][1].videoId).toBe(matchFor(123).id);
        await waitFor(() => expect(screen.getByRole("button", { name: "Search for this text" })).toBeEnabled());
      });

      it("counts a row that goes away while its failed lookup is asked once more as skipped", async () => {
        withUnmatchedTag();
        mocks.resolveRelations.mockRejectedValueOnce(new Error("lookup failed"));
        heldLookup();
        const { removeRow } = await searchAndApplyAll();
        await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalledTimes(2));
        removeRow();

        const summary = await findApplyAllSummary();
        expect(summary).toHaveTextContent("Applied 0, skipped 1.");
        expect(mocks.importFromMetadataServer).not.toHaveBeenCalled();
      });

      it("asks a lookup that had already failed once more before applying", async () => {
        withUnmatchedTag();
        mocks.resolveRelations
          .mockRejectedValueOnce(new Error("lookup failed"))
          .mockResolvedValueOnce({ ...noMatches, tags: [{ input: "Unmatched", matchedName: "Library Tag" }] });
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
          <QueryClientProvider client={queryClient}>
            <VideoTagger
              videos={
                [{ id: 123, title: "Local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] }] as any
              }
            />
          </QueryClientProvider>,
        );
        await userEvent.click(screen.getByRole("button", { name: "Search all" }));
        await screen.findByText("Couldn't check which of these are in your library.");
        await userEvent.click(screen.getByRole("button", { name: "Apply all (1)" }));

        await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
        // Asked again, and applied only after that answer (the apply's own refresh asks once more afterwards).
        expect(mocks.resolveRelations.mock.invocationCallOrder[1]).toBeLessThan(
          mocks.importFromMetadataServer.mock.invocationCallOrder[0],
        );
        expect(mocks.importFromMetadataServer.mock.calls[0][1].excludedTagNames).toBeUndefined();
      });

      it("holds only the tags of a metadata-server row, whose search already placed its performers", async () => {
        mocks.searchMetadataServer.mockResolvedValue([
          {
            ...matchFor(123),
            tagNames: ["Unmatched"],
            tagCandidates: [{ remoteId: "tag-1", name: "Unmatched", existsLocally: false }],
            performerNames: ["Known Performer"],
            performerCandidates: [{ remoteId: "p-1", name: "Known Performer", existsLocally: true, localId: 9 }],
          },
        ]);
        heldLookup();
        const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        render(
          <QueryClientProvider client={queryClient}>
            <VideoTagger
              videos={
                [{ id: 123, title: "Local video", files: [], performers: [], tags: [], urls: [], remoteIds: [] }] as any
              }
            />
          </QueryClientProvider>,
        );
        await userEvent.click(screen.getByRole("button", { name: "Search all" }));
        const tags = (await screen.findByText("Tags", { selector: "span" })).closest("[data-tone]") as HTMLElement;
        expect(within(tags).getByText("Checking your library…")).toBeInTheDocument();
        const performers = screen.getByText("Performers", { selector: "span" }).closest("[data-tone]") as HTMLElement;
        expect(within(performers).queryByText("Checking your library…")).not.toBeInTheDocument();
        expect(within(performers).getByText("Known Performer")).toBeInTheDocument();
      });

      it("does not wait for a row that asks the library nothing", async () => {
        mocks.searchMetadataServer.mockResolvedValue([matchFor(123)]);
        await searchAndApplyAll();
        await waitFor(() => expect(mocks.importFromMetadataServer).toHaveBeenCalledOnce());
        expect(mocks.resolveRelations).not.toHaveBeenCalled();
      });
    });
  });
});
