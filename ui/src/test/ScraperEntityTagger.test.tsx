import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ResolveScrapeRelationsRequest } from "../api/types";
import { ScraperEntityTagger } from "../components/ScraperEntityTagger";

const api = vi.hoisted(() => ({
  listScrapers: vi.fn(),
  createAttempt: vi.fn(),
  resolveRelations: vi.fn(),
}));

vi.mock("../api/client", () => ({
  system: { listScrapers: api.listScrapers },
  scrapeAttempts: { create: api.createAttempt, resolveRelations: api.resolveRelations, apply: vi.fn() },
}));

const galleries = [
  { id: 1, title: "First gallery", urls: [], tags: [], performers: [] },
  { id: 2, title: "Second gallery", urls: [], tags: [], performers: [] },
];

describe("ScraperEntityTagger", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.listScrapers.mockResolvedValue([
      {
        id: "pack/site:gallery",
        name: "Site Scraper",
        entityType: "gallery",
        supportedScrapes: ["name"],
        urls: [],
        sourcePath: "",
      },
    ]);
    api.createAttempt.mockImplementation(async ({ entityId }: { entityId: number }) => ({
      id: entityId,
      status: "success",
      resultJson: JSON.stringify({ Title: `Scraped ${entityId}`, Tags: [`Tag ${entityId}`, "Shared"] }),
    }));
    api.resolveRelations.mockImplementation(async (request: ResolveScrapeRelationsRequest) => ({
      tags: request.tags.map((name) => ({ input: name, matchedName: name })),
      performers: [],
    }));
  });

  it("asks the library about every row's names in one request", async () => {
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ScraperEntityTagger
          entityType="gallery"
          label="Gallery"
          items={galleries as never}
          getTitle={(gallery) => gallery.title ?? ""}
          queryKey="galleries"
        />
      </QueryClientProvider>,
    );

    await userEvent.click(await screen.findByRole("button", { name: /Search all/ }));

    await waitFor(() => expect(api.createAttempt).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(api.resolveRelations).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(api.resolveRelations).toHaveBeenCalledOnce();
    expect([...api.resolveRelations.mock.calls[0][0].tags].sort()).toEqual(["Shared", "Tag 1", "Tag 2"]);
  });
});
