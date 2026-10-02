import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DownloaderMatch, ScrapeAttempt, ScraperSummary } from "../api/types";
import { IdentifyDialog } from "../components/IdentifyDialog";
import { MediaScrapeDialog } from "../components/MediaScrapeDialog";
import { VideoDownloadDialog } from "../components/VideoDownloadDialog";

const api = vi.hoisted(() => ({
  matchDownloaders: vi.fn(),
  preflightDownload: vi.fn(),
  startDownload: vi.fn(),
  listScrapers: vi.fn(),
  listAttempts: vi.fn(),
  resolveRelations: vi.fn(),
}));

vi.mock("../api/client", () => ({
  system: {
    matchDownloaders: api.matchDownloaders,
    preflightDownload: api.preflightDownload,
    startDownload: api.startDownload,
    listScrapers: api.listScrapers,
  },
  videos: { create: vi.fn() },
  metadata: { identify: vi.fn() },
  scrapeAttempts: {
    list: api.listAttempts,
    resolveRelations: api.resolveRelations,
    create: vi.fn(),
    apply: vi.fn(),
  },
}));

// No loaded config: the dialogs must cope with their `?? []` fallbacks without looping.
vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: undefined }),
}));

function renderWithClient(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const result = render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
  return {
    ...result,
    rerender: (next: ReactNode) => result.rerender(<QueryClientProvider client={client}>{next}</QueryClientProvider>),
  };
}

function downloaderMatch(downloaderId: string, qualityIds: string[]): DownloaderMatch {
  return {
    downloaderId,
    downloaderName: `Downloader ${downloaderId}`,
    supportedEntity: "Video",
    normalizedUrl: `https://example.com/watch/${downloaderId}`,
    label: `Match ${downloaderId}`,
    qualityOptions: qualityIds.map((id) => ({ id, label: `Quality ${id}` })),
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
});

afterEach(() => {
  cleanup();
});

describe("VideoDownloadDialog", () => {
  const video = { id: 7, title: "Existing video", urls: ["https://example.com/watch/source"], files: [] };

  it("keeps the quality valid for the selected downloader and queues the one shown", async () => {
    api.matchDownloaders.mockResolvedValue([
      downloaderMatch("alpha", ["1080p", "720p"]),
      downloaderMatch("beta", ["720p", "480p"]),
    ]);
    api.preflightDownload.mockResolvedValue({ isDuplicate: false });
    api.startDownload.mockResolvedValue({});

    renderWithClient(<VideoDownloadDialog open onClose={vi.fn()} onNavigate={vi.fn()} video={video} />);

    expect(screen.getByPlaceholderText("https://example.com/watch/...")).toHaveValue(video.urls[0]);
    fireEvent.click(screen.getByRole("button", { name: /Find Downloaders/ }));

    const quality = await screen.findByRole("combobox");
    expect(quality).toHaveValue("1080p");

    // Switching to a downloader without the chosen quality falls back to its first option...
    fireEvent.click(screen.getByRole("radio", { name: /Match beta/ }));
    expect(screen.getByRole("combobox")).toHaveValue("720p");

    // ...and a quality both downloaders offer carries over when switching back.
    fireEvent.click(screen.getByRole("radio", { name: /Match alpha/ }));
    expect(screen.getByRole("combobox")).toHaveValue("720p");

    fireEvent.click(screen.getByRole("button", { name: /Queue Download/ }));
    await waitFor(() => expect(api.startDownload).toHaveBeenCalledTimes(1));
    expect(api.startDownload).toHaveBeenCalledWith(
      expect.objectContaining({ downloaderId: "alpha", qualityId: "720p" }),
    );
  });

  it("resets the form each time it opens", async () => {
    api.matchDownloaders.mockResolvedValue([downloaderMatch("alpha", ["1080p"])]);

    const { rerender } = renderWithClient(
      <VideoDownloadDialog open onClose={vi.fn()} onNavigate={vi.fn()} video={video} />,
    );
    fireEvent.change(screen.getByPlaceholderText("https://example.com/watch/..."), {
      target: { value: "https://example.com/watch/edited" },
    });
    fireEvent.click(screen.getByRole("button", { name: /Find Downloaders/ }));
    expect(await screen.findByRole("radio", { name: /Match alpha/ })).toBeChecked();

    rerender(<VideoDownloadDialog open={false} onClose={vi.fn()} onNavigate={vi.fn()} video={video} />);
    rerender(<VideoDownloadDialog open onClose={vi.fn()} onNavigate={vi.fn()} video={video} />);

    expect(screen.getByPlaceholderText("https://example.com/watch/...")).toHaveValue(video.urls[0]);
    expect(screen.queryByRole("radio")).toBeNull();
    expect(screen.getByText("Match the URL to choose a video downloader.")).toBeInTheDocument();
  });
});

describe("MediaScrapeDialog", () => {
  const entity = {
    id: 3,
    title: "Harbour at dusk",
    urls: [],
    tags: [],
    performers: [],
    files: [{ basename: "harbour.jpg", path: "/library/harbour.jpg" }],
    organized: false,
  };

  it("selects the preferred scraper, a supported input kind and the latest attempt", async () => {
    const scraper: ScraperSummary = {
      id: "name-only",
      name: "Name only scraper",
      entityType: "Image",
      supportedScrapes: ["name"],
      urls: [],
      sourcePath: "/scrapers/name-only.yml",
    };
    const attempts: ScrapeAttempt[] = [
      {
        id: "latest",
        scraperId: "name-only",
        entityType: "image",
        entityId: entity.id,
        inputKind: "name",
        status: "Failure",
        error: "The site refused the request.",
        createdAt: "2026-09-01T00:00:00Z",
      },
      {
        id: "older",
        scraperId: "name-only",
        entityType: "image",
        entityId: entity.id,
        inputKind: "name",
        status: "Failure",
        error: "An older failure.",
        createdAt: "2026-08-01T00:00:00Z",
      },
    ];
    api.listScrapers.mockResolvedValue([scraper]);
    api.listAttempts.mockResolvedValue(attempts);

    renderWithClient(<MediaScrapeDialog open onClose={vi.fn()} entityType="image" entity={entity} />);

    expect(await screen.findAllByText("The site refused the request.")).not.toHaveLength(0);
    expect(screen.queryAllByText("An older failure.")).toHaveLength(0);
    expect(await screen.findByRole("option", { name: "Name only scraper" })).toBeInTheDocument();
    expect(screen.getAllByRole("combobox")[0]).toHaveValue("name-only");
    expect(screen.getByPlaceholderText("Search text")).toHaveValue("Harbour at dusk");

    fireEvent.click(screen.getAllByRole("button", { name: /name-only/ })[1]);
    expect(await screen.findAllByText("An older failure.")).not.toHaveLength(0);
    expect(screen.queryAllByText("The site refused the request.")).toHaveLength(0);
  });
});

describe("IdentifyDialog", () => {
  it("builds its sources once the scrapers load and keeps the user's changes", async () => {
    api.listScrapers.mockResolvedValue([
      {
        id: "url-scraper",
        name: "URL scraper",
        entityType: "Video",
        supportedScrapes: ["url"],
        urls: [],
        sourcePath: "/scrapers/url.yml",
      } satisfies ScraperSummary,
    ]);

    renderWithClient(<IdentifyDialog open onClose={vi.fn()} videoIds={[1]} />);

    const source = await screen.findByRole("checkbox", { name: /URL scraper/ });
    expect(source).toBeChecked();
    fireEvent.click(source);
    expect(screen.getByRole("checkbox", { name: /URL scraper/ })).not.toBeChecked();
  });
});
