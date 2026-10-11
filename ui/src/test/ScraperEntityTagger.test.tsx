import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ResolveScrapeRelationsRequest } from "../api/types";
import { ScraperEntityTagger } from "../components/ScraperEntityTagger";

const api = vi.hoisted(() => ({
  listScrapers: vi.fn(),
  createAttempt: vi.fn(),
  resolveRelations: vi.fn(),
  apply: vi.fn(),
}));

vi.mock("../api/client", () => ({
  system: { listScrapers: api.listScrapers },
  scrapeAttempts: { create: api.createAttempt, resolveRelations: api.resolveRelations, apply: api.apply },
}));

const galleries = [
  { id: 1, title: "First gallery", urls: [], tags: [], performers: [] },
  { id: 2, title: "Second gallery", urls: [], tags: [], performers: [] },
];

function renderTagger(items: object[] = galleries) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const tagger = (rows: object[]) => (
    <QueryClientProvider client={queryClient}>
      <ScraperEntityTagger
        entityType="gallery"
        label="Gallery"
        items={rows as never}
        getTitle={(gallery) => gallery.title ?? ""}
        queryKey="galleries"
      />
    </QueryClientProvider>
  );
  const { rerender } = render(tagger(items));
  return { queryClient, rerender: (rows: object[]) => rerender(tagger(rows)) };
}

// A lookup the test answers, or fails, when it chooses.
function heldLookup() {
  const held = {} as { answer: (result: object) => void; fail: (error: Error) => void };
  const answer = new Promise((resolve, reject) => {
    held.answer = resolve;
    held.fail = reject;
  });
  // A test may fail it before the lookup asks; that is not an unhandled rejection.
  answer.catch(() => {});
  api.resolveRelations.mockImplementationOnce(() => answer);
  return held;
}

const inLibrary = (...names: string[]) => ({
  tags: names.map((name) => ({ input: name, matchedName: name })),
  performers: [],
});

describe("ScraperEntityTagger", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
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
    api.apply.mockResolvedValue({ id: 1, status: "Applied" });
  });

  it("asks the library about every row's names in one request", async () => {
    renderTagger();

    await userEvent.click(await screen.findByRole("button", { name: /Search all/ }));

    await waitFor(() => expect(api.createAttempt).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(api.resolveRelations).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(api.resolveRelations).toHaveBeenCalledOnce();
    expect([...api.resolveRelations.mock.calls[0][0].tags].sort()).toEqual(["Shared", "Tag 1", "Tag 2"]);
  });

  it("keeps the choices made in a row when the library's answer changes", async () => {
    api.resolveRelations.mockResolvedValueOnce(inLibrary());
    const { queryClient } = renderTagger([galleries[0]]);
    await userEvent.click(await screen.findByRole("button", { name: "Search" }));
    // Neither tag is in the library, so both are left out until chosen.
    await userEvent.click(await screen.findByRole("button", { name: "Shared: Excluded" }));
    expect(screen.getByRole("button", { name: "Shared: Will create" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Merge current + scraped" }));

    // "Tag 1" was added to the library meanwhile, and the lookup is asked again.
    api.resolveRelations.mockResolvedValueOnce(inLibrary("Tag 1"));
    await queryClient.invalidateQueries({ queryKey: ["scraper-tagger-resolve-relations"] });

    expect(await screen.findByRole("button", { name: "Tag 1: Existing" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Shared: Will create" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(api.apply).toHaveBeenCalledOnce());
    const request = api.apply.mock.calls[0][1];
    expect(request.collectionModes.tags).toBe("merge");
    expect(request.tagSelections).toEqual([
      { name: "Tag 1", action: "include" },
      { name: "Shared", action: "create" },
    ]);
  });

  it("holds Save and the tags until the library answers, so it never leaves out tags the library has", async () => {
    const held = heldLookup();
    renderTagger([galleries[0]]);
    await userEvent.click(await screen.findByRole("button", { name: "Search" }));

    const save = await screen.findByRole("button", { name: /Checking library/ });
    expect(save).toBeDisabled();
    expect(screen.getByText("Checking your library…")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Tag 1:/ })).not.toBeInTheDocument();

    held.answer(inLibrary("Tag 1", "Shared"));
    await userEvent.click(await screen.findByRole("button", { name: "Save" }));
    await waitFor(() => expect(api.apply).toHaveBeenCalledOnce());
    expect(api.apply.mock.calls[0][1].tagSelections).toEqual([
      { name: "Tag 1", action: "include" },
      { name: "Shared", action: "include" },
    ]);
  });

  it("says when the library could not be checked and lets the row ask again", async () => {
    const held = heldLookup();
    renderTagger([galleries[0]]);
    await userEvent.click(await screen.findByRole("button", { name: "Search" }));
    await waitFor(() => expect(api.resolveRelations).toHaveBeenCalled());
    held.fail(new Error("lookup failed"));

    const failure = await screen.findByText("Couldn't check which of these are in your library.");
    expect(screen.getByText("Not checked against your library")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Save/ })).toBeDisabled();

    api.resolveRelations.mockResolvedValueOnce(inLibrary("Tag 1"));
    await userEvent.click(within(failure.parentElement!).getByRole("button", { name: "Retry" }));
    expect(await screen.findByRole("button", { name: "Tag 1: Existing" })).toBeInTheDocument();
    expect(screen.queryByText("Couldn't check which of these are in your library.")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
  });

  it("keeps a row's choices when its item changes elsewhere meanwhile", async () => {
    api.resolveRelations.mockResolvedValue(inLibrary());
    const { rerender } = renderTagger([galleries[0]]);
    await userEvent.click(await screen.findByRole("button", { name: "Search" }));
    await userEvent.click(await screen.findByRole("button", { name: "Shared: Excluded" }));
    await userEvent.click(screen.getByRole("button", { name: "Merge current + scraped" }));

    // The gallery got "Tag 1" on its own page meanwhile.
    rerender([{ ...galleries[0], tags: [{ id: 5, name: "Tag 1" }] }]);

    expect(await screen.findByRole("button", { name: "Tag 1: Current" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Shared: Will create" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(api.apply).toHaveBeenCalledOnce());
    expect(api.apply.mock.calls[0][1].collectionModes.tags).toBe("merge");
  });

  it("starts a row's choices over when it is searched again", async () => {
    api.resolveRelations.mockResolvedValue(inLibrary());
    renderTagger([galleries[0]]);
    await userEvent.click(await screen.findByRole("button", { name: "Search" }));
    await userEvent.click(await screen.findByRole("button", { name: "Shared: Excluded" }));
    expect(screen.getByRole("button", { name: "Shared: Will create" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByRole("button", { name: "Shared: Excluded" })).toBeInTheDocument();
  });

  it("keeps the failure line and focus on its Retry while asking again, then moves focus to Save", async () => {
    const first = heldLookup();
    renderTagger([galleries[0]]);
    await userEvent.click(await screen.findByRole("button", { name: "Search" }));
    first.fail(new Error("lookup failed"));
    const failure = await screen.findByText("Couldn't check which of these are in your library.");

    const again = heldLookup();
    const retry = within(failure.parentElement!).getByRole("button", { name: "Retry" });
    await userEvent.click(retry);

    expect(await screen.findByText("Checking your library again…")).toBeInTheDocument();
    expect(retry).toHaveFocus();
    again.answer(inLibrary("Tag 1"));
    const save = await screen.findByRole("button", { name: "Save" });
    await waitFor(() => expect(save).toHaveFocus());
  });
});
