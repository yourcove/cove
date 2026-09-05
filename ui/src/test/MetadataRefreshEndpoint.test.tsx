import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Studio, Tag } from "../api/types";
import { StudioTagger } from "../components/StudioTagger";
import { TagTagger } from "../components/TagTagger";

const mocks = vi.hoisted(() => ({
  metadataServers: [
    { endpoint: "https://first.example/graphql", name: "First" },
    { endpoint: "https://second.example/graphql", name: "Second" },
  ],
  tagsFindByIds: vi.fn(),
  tagsImport: vi.fn(),
  studiosFindByIds: vi.fn(),
  studiosImport: vi.fn(),
}));

vi.mock("../api/client", () => ({
  tags: {
    findMetadataServerByIds: mocks.tagsFindByIds,
    importFromMetadataServer: mocks.tagsImport,
  },
  studios: {
    findMetadataServerByIds: mocks.studiosFindByIds,
    importFromMetadataServer: mocks.studiosImport,
  },
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({
    config: { scraping: { metadataServers: mocks.metadataServers } },
  }),
}));

function renderTagger(component: React.ReactNode) {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
      {component}
    </QueryClientProvider>,
  );
}

describe("metadata refresh import endpoints", () => {
  beforeEach(() => {
    mocks.tagsFindByIds.mockReset();
    mocks.tagsImport.mockReset().mockResolvedValue({});
    mocks.studiosFindByIds.mockReset();
    mocks.studiosImport.mockReset().mockResolvedValue({});
  });

  it("imports a tag through the endpoint that returned the refreshed result", async () => {
    const user = userEvent.setup();
    mocks.tagsFindByIds.mockResolvedValue([
      {
        endpoint: "https://second.example/graphql",
        metadataServerName: "Second",
        id: "second-tag",
        name: "Refreshed tag",
        aliases: [],
      },
    ]);
    const tag = {
      id: 11,
      name: "Local tag",
      favorite: false,
      organized: false,
      aliases: [],
      remoteIds: [
        { endpoint: "https://first.example/graphql", remoteId: "first-tag" },
        { endpoint: "https://second.example/graphql", remoteId: "second-tag" },
      ],
    } satisfies Tag & { remoteIds: Array<{ endpoint: string; remoteId: string }> };

    renderTagger(<TagTagger tags={[tag]} mode="detail" />);
    await user.click(screen.getByRole("button", { name: "Refresh from Second" }));
    await user.click(await screen.findByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(mocks.tagsImport).toHaveBeenCalledWith(11, {
        endpoint: "https://second.example/graphql",
        tagId: "second-tag",
      }),
    );
  });

  it("shows tag metadata imports that skipped conflicting remote claims as partial successes", async () => {
    const user = userEvent.setup();
    mocks.tagsFindByIds.mockResolvedValue([
      {
        endpoint: "https://second.example/graphql",
        metadataServerName: "Second",
        id: "second-tag",
        name: "Refreshed tag",
        aliases: ["Remote alias"],
      },
    ]);
    mocks.tagsImport.mockResolvedValue({
      importWarnings: ["Skipped a remote alias because it is already claimed by another tag."],
    });
    const tag = {
      id: 12,
      name: "Local tag",
      favorite: false,
      organized: false,
      aliases: [],
      remoteIds: [{ endpoint: "https://second.example/graphql", remoteId: "second-tag" }],
    } satisfies Tag & { remoteIds: Array<{ endpoint: string; remoteId: string }> };

    renderTagger(<TagTagger tags={[tag]} mode="detail" />);
    await user.click(screen.getByRole("button", { name: "Refresh from Second" }));
    await user.click(await screen.findByRole("button", { name: "Save" }));

    expect(await screen.findByText(/Saved with warnings: Skipped a remote alias/i)).toBeInTheDocument();
    expect(screen.getByText("Saved successfully")).toBeInTheDocument();
  });

  it("imports a studio through the endpoint that returned the refreshed result", async () => {
    const user = userEvent.setup();
    mocks.studiosFindByIds.mockResolvedValue([
      {
        endpoint: "https://second.example/graphql",
        serverName: "Second",
        id: "second-studio",
        name: "Refreshed studio",
        aliases: [],
        urls: [],
      },
    ]);
    const studio: Studio = {
      id: 21,
      name: "Local studio",
      favorite: false,
      organized: false,
      urls: [],
      aliases: [],
      tags: [],
      remoteIds: [
        { endpoint: "https://first.example/graphql", remoteId: "first-studio" },
        { endpoint: "https://second.example/graphql", remoteId: "second-studio" },
      ],
      videoCount: 0,
      imageCount: 0,
      galleryCount: 0,
      groupCount: 0,
      performerCount: 0,
      childStudioCount: 0,
      audioCount: 0,
      textCount: 0,
      createdAt: "2024-01-01T00:00:00Z",
      updatedAt: "2024-01-02T00:00:00Z",
    };

    renderTagger(<StudioTagger studios={[studio]} mode="detail" />);
    await user.click(screen.getByRole("button", { name: "Refresh from Second" }));
    await user.click(await screen.findByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(mocks.studiosImport).toHaveBeenCalledWith(
        21,
        expect.objectContaining({
          endpoint: "https://second.example/graphql",
          studioId: "second-studio",
        }),
      ),
    );
  });
});
