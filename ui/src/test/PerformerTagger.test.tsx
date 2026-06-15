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
  applyScraped: vi.fn(),
  importFromMetadataServer: vi.fn(),
}));

vi.mock("../api/client", () => ({
  system: { listScrapers: mocks.listScrapers },
  tags: { find: mocks.tagsFind },
  performers: {
    previewScrape: mocks.previewScrape,
    searchMetadataServer: mocks.searchMetadataServer,
    applyScraped: mocks.applyScraped,
    importFromMetadataServer: mocks.importFromMetadataServer,
  },
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({
    config: {
      scraping: { metadataServers: [] },
    },
  }),
}));

function renderTagger(performers: Performer[]) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <PerformerTagger performers={performers} />
    </QueryClientProvider>,
  );
}

describe("PerformerTagger", () => {
  beforeEach(() => {
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
    mocks.previewScrape.mockRejectedValue(new Error('API Error 404: {"error":"Scrape returned no performer metadata."}'));
    mocks.searchMetadataServer.mockResolvedValue([]);
    mocks.applyScraped.mockResolvedValue({});
    mocks.importFromMetadataServer.mockResolvedValue({});
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

    await waitFor(() => expect(mocks.previewScrape).toHaveBeenCalledWith(1, {
      scraperId: "performer-scraper",
      inputKind: "name",
      name: "Missing Performer",
      url: undefined,
    }));
    expect(await screen.findByText("No performer metadata was found for this search.")).toBeInTheDocument();
    expect(screen.queryByText(/API Error 404/i)).not.toBeInTheDocument();
  });
});

