import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { PerformerTagger } from "../components/PerformerTagger";
import type { Performer } from "../api/types";

const mocks = vi.hoisted(() => ({
  listScrapers: vi.fn(),
  tagsFind: vi.fn(),
  previewScrape: vi.fn(),
  searchMetadataServer: vi.fn(),
  findMetadataServerByIds: vi.fn(),
  applyScraped: vi.fn(),
  importFromMetadataServer: vi.fn(),
  resolveRelations: vi.fn(),
  metadataServers: [] as Array<{ endpoint: string; name: string }>,
}));

vi.mock("../api/client", () => ({
  system: { listScrapers: mocks.listScrapers },
  tags: { find: mocks.tagsFind },
  scrapeAttempts: { resolveRelations: mocks.resolveRelations },
  performers: {
    previewScrape: mocks.previewScrape,
    searchMetadataServer: mocks.searchMetadataServer,
    findMetadataServerByIds: mocks.findMetadataServerByIds,
    applyScraped: mocks.applyScraped,
    importFromMetadataServer: mocks.importFromMetadataServer,
  },
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({
    config: {
      scraping: { metadataServers: mocks.metadataServers },
    },
  }),
}));

function renderTagger(performers: Performer[], mode: "bulk" | "detail" = "bulk") {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  const view = render(
    <QueryClientProvider client={queryClient}>
      <PerformerTagger performers={performers} mode={mode} />
    </QueryClientProvider>,
  );
  return { ...view, queryClient };
}

describe("PerformerTagger", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.metadataServers.splice(0);
    mocks.listScrapers.mockResolvedValue([
      {
        id: "performer-scraper",
        name: "Performer Scraper",
        entityType: "performer",
        supportedScrapes: ["name"],
        urls: [],
        sourcePath: "",
      },
    ]);
    mocks.tagsFind.mockResolvedValue({ items: [] });
    mocks.previewScrape.mockRejectedValue(
      new Error('API Error 404: {"error":"Scrape returned no performer metadata."}'),
    );
    mocks.searchMetadataServer.mockResolvedValue([]);
    mocks.findMetadataServerByIds.mockResolvedValue([]);
    mocks.applyScraped.mockResolvedValue({});
    mocks.importFromMetadataServer.mockResolvedValue({});
    mocks.resolveRelations.mockResolvedValue({ tags: [], performers: [], studios: [] });
    window.localStorage.clear();
  });

  it("shows a friendly empty result message for scraper 404 responses", async () => {
    const user = userEvent.setup();
    const performer: Performer = {
      id: 1,
      name: "Missing Performer",
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    renderTagger([performer]);

    await user.click(await screen.findByRole("button", { name: /^Search$/i }));

    await waitFor(() =>
      expect(mocks.previewScrape).toHaveBeenCalledWith(1, {
        scraperId: "performer-scraper",
        inputKind: "name",
        name: "Missing Performer",
        url: undefined,
      }),
    );
    expect(await screen.findByText("No performer metadata was found for this search.")).toBeInTheDocument();
    expect(screen.queryByText(/API Error 404/i)).not.toBeInTheDocument();
  });

  it("imports a remote refresh through the endpoint that returned the match", async () => {
    const user = userEvent.setup();
    mocks.metadataServers.push(
      { endpoint: "https://first.example/graphql", name: "First" },
      { endpoint: "https://second.example/graphql", name: "Second" },
    );
    mocks.findMetadataServerByIds.mockResolvedValue([
      {
        endpoint: "https://second.example/graphql",
        id: "second-remote",
        name: "Same Name",
        heightCm: 165,
        imageUrl: "https://cdn.example/a.jpg",
        imageUrls: ["https://cdn.example/a.jpg", "https://cdn.example/b.jpg"],
        aliases: [],
        urls: [],
        deleted: false,
      },
    ]);
    const performer: Performer = {
      id: 1,
      name: "Same Name",
      heightCm: 160,
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [
        { endpoint: "https://first.example/graphql", remoteId: "first-remote" },
        { endpoint: "https://second.example/graphql", remoteId: "second-remote" },
      ],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    renderTagger([performer], "detail");
    await user.click(await screen.findByRole("button", { name: "Refresh from Second" }));
    // The source's images are candidates: the one on screen when applying is the one imported.
    expect(await screen.findByText("1 / 2")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Next image" }));
    expect(screen.getByText("2 / 2")).toBeInTheDocument();
    // A new lookup starts from the first image again: the position belongs to the match it was chosen for.
    await user.click(screen.getByRole("button", { name: "Refresh from Second" }));
    expect(await screen.findByText("1 / 2")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Next image" }));
    expect(screen.getByText("2 / 2")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /^Apply/ }));

    await waitFor(() =>
      expect(mocks.importFromMetadataServer).toHaveBeenCalledWith(
        1,
        expect.objectContaining({
          endpoint: "https://second.example/graphql",
          performerId: "second-remote",
          imageUrl: "https://cdn.example/b.jpg",
        }),
      ),
    );
  });

  describe("which scraped tags are in the library", () => {
    const performer: Performer = {
      id: 7,
      name: "Jane Doe",
      favorite: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };
    beforeEach(() => {
      // Missing tags are not created, so only a tag the library has is applied by default.
      window.localStorage.setItem("cove.performerTaggerConfig", JSON.stringify({ createMissingTags: false }));
      mocks.previewScrape.mockResolvedValue({
        scraped: { name: "Jane Doe", tagNames: ["Big Tits", "Brand New"] },
        inputKind: "name",
      });
    });
    const search = async () => {
      const view = renderTagger([performer]);
      await userEvent.click(await screen.findByRole("button", { name: /^Search$/i }));
      return view;
    };

    it("asks the server, so a tag it matches by alias is applied without being created", async () => {
      mocks.resolveRelations.mockResolvedValue({
        tags: [{ input: "Big Tits", matchedName: "Big Breasts" }],
        performers: [],
        studios: [],
      });
      await search();
      await waitFor(() =>
        expect(mocks.resolveRelations).toHaveBeenCalledWith({
          tags: ["Big Tits", "Brand New"],
          performers: [],
          studios: [],
        }),
      );
      expect(mocks.tagsFind).not.toHaveBeenCalled();

      await userEvent.click(await screen.findByRole("button", { name: /^Apply/ }));
      await waitFor(() => expect(mocks.applyScraped).toHaveBeenCalledOnce());
      const request = mocks.applyScraped.mock.calls[0][1];
      expect(request.scraped.tagNames).toEqual(["Big Tits"]);
      expect(request.createMissingTags).toBe(false);
    });

    it("follows the library for tags the person has not chosen, after choosing another", async () => {
      mocks.previewScrape.mockResolvedValue({
        scraped: { name: "Jane Doe", tagNames: ["Tattoos", "Brand New"] },
        inputKind: "name",
      });
      mocks.resolveRelations.mockResolvedValue({
        tags: [{ input: "Tattoos", matchedName: "Tattoos" }],
        performers: [],
        studios: [],
      });
      const { queryClient } = await search();
      await userEvent.click(await screen.findByRole("button", { name: "Adjust…" }));
      await userEvent.click(await screen.findByRole("button", { name: "Remove Tags: Tattoos" }));

      // Another row's apply creates "Brand New", and this row asks again.
      mocks.resolveRelations.mockResolvedValue({
        tags: [
          { input: "Tattoos", matchedName: "Tattoos" },
          { input: "Brand New", matchedName: "Brand New" },
        ],
        performers: [],
        studios: [],
      });
      await queryClient.invalidateQueries({ queryKey: ["performer-tagger-resolve-relations"] });

      // Now in the library and never chosen against, so it is included; Tattoos stays left out.
      expect(await screen.findByRole("button", { name: "Remove Tags: Brand New" })).toBeInTheDocument();
      await userEvent.click(screen.getByRole("button", { name: /^Apply/ }));
      await waitFor(() => expect(mocks.applyScraped).toHaveBeenCalledOnce());
      expect(mocks.applyScraped.mock.calls[0][1].scraped.tagNames).toEqual(["Brand New"]);
    });

    it("asks the server again when the row is searched again, so a tag aliased since is matched", async () => {
      mocks.resolveRelations.mockResolvedValue({ tags: [], performers: [], studios: [] });
      await search();
      await waitFor(() => expect(screen.getByRole("button", { name: /^Apply/ })).toBeEnabled());
      mocks.resolveRelations.mockResolvedValue({
        tags: [{ input: "Big Tits", matchedName: "Big Breasts" }],
        performers: [],
        studios: [],
      });
      await userEvent.click(screen.getByRole("button", { name: /^Search$/i }));
      await waitFor(() => expect(mocks.resolveRelations).toHaveBeenCalledTimes(2));
      await waitFor(() => expect(screen.getByRole("button", { name: /^Apply/ })).toBeEnabled());
      await userEvent.click(screen.getByRole("button", { name: /^Apply/ }));
      await waitFor(() => expect(mocks.applyScraped).toHaveBeenCalledOnce());
      expect(mocks.applyScraped.mock.calls[0][1].scraped.tagNames).toContain("Big Tits");
    });

    it("holds Apply until the server answers, and offers Retry when it fails", async () => {
      let fail!: (error: Error) => void;
      mocks.resolveRelations.mockImplementationOnce(() => new Promise((_, reject) => (fail = reject)));
      await search();
      const apply = await screen.findByRole("button", { name: /Checking library/ });
      expect(apply).toBeDisabled();

      fail(new Error("lookup failed"));
      expect(await screen.findByText("Couldn't check which of these are in your library.")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: /^Apply/ })).toBeDisabled();
      // The tags row says it was not checked rather than still checking, and the page says so once.
      expect(screen.getAllByText("Not checked against your library").length).toBeGreaterThan(0);
      expect(screen.queryByText("Checking your library…")).not.toBeInTheDocument();
      expect(
        (await screen.findByText(/Couldn't check some performers against your library/)).closest("[role=status]"),
      ).not.toBeNull();

      await userEvent.click(screen.getByRole("button", { name: "Retry" }));
      await waitFor(() => expect(screen.getByRole("button", { name: /^Apply/ })).toBeEnabled());
      expect(mocks.applyScraped).not.toHaveBeenCalled();
    });
  });
});
