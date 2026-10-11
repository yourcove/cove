import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { MediaScrapeDialog } from "../components/MediaScrapeDialog";

const api = vi.hoisted(() => ({
  listScrapers: vi.fn(),
  listAttempts: vi.fn(),
  resolveRelations: vi.fn(),
  apply: vi.fn(),
}));

vi.mock("../api/client", () => ({
  system: { listScrapers: api.listScrapers },
  scrapeAttempts: {
    list: api.listAttempts,
    create: vi.fn(),
    resolveRelations: api.resolveRelations,
    apply: api.apply,
  },
}));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { scraping: { scraperPreferences: [] } } }),
}));

const image = {
  id: 7,
  title: "",
  urls: ["https://site.example/image/7"],
  tags: [],
  performers: [],
  files: [{ basename: "image.jpg", path: "/library/image.jpg" }],
  organized: false,
};

const inLibrary = (...names: string[]) => ({
  tags: names.map((name) => ({ input: name, matchedName: name })),
  performers: [],
});

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

// Opens the dialog on an image whose latest scrape returned two tags, and returns its query client.
function renderDialog() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const dialog = (entity: object) => (
    <QueryClientProvider client={queryClient}>
      <MediaScrapeDialog open onClose={() => {}} entityType="image" entity={entity as never} />
    </QueryClientProvider>
  );
  const { rerender } = render(dialog(image));
  return { queryClient, rerender: (entity: object) => rerender(dialog(entity)) };
}

const tagsSection = () => screen.getByText("Tags", { selector: "div" }).closest("section")!;

describe("MediaScrapeDialog", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    api.listScrapers.mockResolvedValue([
      {
        id: "pack/site:image",
        name: "Site Scraper",
        entityType: "image",
        supportedScrapes: ["url"],
        urls: ["site.example/image/"],
        sourcePath: "",
      },
    ]);
    api.listAttempts.mockResolvedValue([
      {
        id: "attempt-1",
        scraperId: "pack/site:image",
        entityType: "image",
        entityId: 7,
        inputKind: "url",
        status: "Success",
        error: null,
        createdAt: "2026-10-01T10:00:00Z",
        candidateResultsJson: null,
        resultJson: JSON.stringify({ Title: "Scraped title", Tags: [{ Name: "Known" }, { Name: "Shared" }] }),
      },
    ]);
    api.resolveRelations.mockResolvedValue(inLibrary());
    api.apply.mockResolvedValue({ id: "attempt-1", status: "Applied" });
  });

  it("keeps the choices made when the library's answer changes", async () => {
    const { queryClient } = renderDialog();
    // Neither tag is in the library, so both are left out until chosen.
    await userEvent.click(await screen.findByRole("button", { name: "Shared: Excluded" }));
    await userEvent.selectOptions(within(tagsSection()).getByRole("combobox"), "replace");

    // "Known" was added to the library meanwhile, and the lookup is asked again.
    api.resolveRelations.mockResolvedValueOnce(inLibrary("Known"));
    await queryClient.invalidateQueries({ queryKey: ["scrape-dialog-resolve-relations"] });
    expect(await screen.findByRole("button", { name: "Known: Existing" })).toBeInTheDocument();

    expect(screen.getByRole("button", { name: "Shared: Will create" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Apply Selected Fields/ }));
    await waitFor(() => expect(api.apply).toHaveBeenCalledOnce());
    const request = api.apply.mock.calls[0][1];
    expect(request.collectionModes.tags).toBe("replace");
    expect(request.tagSelections).toEqual([
      { name: "Known", action: "include" },
      { name: "Shared", action: "create" },
    ]);
  });

  it("holds Apply and the tags until the library answers, so it never leaves out tags the library has", async () => {
    const held = heldLookup();
    renderDialog();

    const apply = await screen.findByRole("button", { name: /Checking library/ });
    expect(apply).toBeDisabled();
    expect(within(tagsSection()).getByText("Checking your library…")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Known:/ })).not.toBeInTheDocument();

    held.answer(inLibrary("Known", "Shared"));
    await userEvent.click(await screen.findByRole("button", { name: /Apply Selected Fields/ }));
    await waitFor(() => expect(api.apply).toHaveBeenCalledOnce());
    expect(api.apply.mock.calls[0][1].tagSelections).toEqual([
      { name: "Known", action: "include" },
      { name: "Shared", action: "include" },
    ]);
  });

  it("says when the library could not be checked and lets it be asked again", async () => {
    const held = heldLookup();
    renderDialog();
    await waitFor(() => expect(api.resolveRelations).toHaveBeenCalled());
    held.fail(new Error("lookup failed"));

    const failure = await screen.findByText("Couldn't check which of these are in your library.");
    expect(within(tagsSection()).getByText("Not checked against your library")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Apply Selected Fields/ })).toBeDisabled();

    api.resolveRelations.mockResolvedValueOnce(inLibrary("Known"));
    await userEvent.click(within(failure.parentElement!).getByRole("button", { name: "Retry" }));
    expect(await screen.findByRole("button", { name: "Known: Existing" })).toBeInTheDocument();
    expect(screen.queryByText("Couldn't check which of these are in your library.")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Apply Selected Fields/ })).toBeEnabled();
  });

  it("keeps the choices made when the page re-renders, building the entity afresh", async () => {
    const { rerender } = renderDialog();
    await userEvent.click(await screen.findByRole("button", { name: "Shared: Excluded" }));
    await userEvent.selectOptions(within(tagsSection()).getByRole("combobox"), "replace");

    // The detail pages pass the entity as a new object on every render.
    rerender({ ...image });

    expect(screen.getByRole("button", { name: "Shared: Will create" })).toBeInTheDocument();
    expect(within(tagsSection()).getByRole("combobox")).toHaveValue("replace");
    await userEvent.click(screen.getByRole("button", { name: /Apply Selected Fields/ }));
    await waitFor(() => expect(api.apply).toHaveBeenCalledOnce());
    expect(api.apply.mock.calls[0][1].collectionModes.tags).toBe("replace");
  });

  it("starts the choices over for another item", async () => {
    const { rerender } = renderDialog();
    await userEvent.click(await screen.findByRole("button", { name: "Shared: Excluded" }));
    expect(screen.getByRole("button", { name: "Shared: Will create" })).toBeInTheDocument();

    // The bulk actions show the dialog for whichever media item is selected.
    rerender({ ...image, id: 8 });

    expect(await screen.findByRole("button", { name: "Shared: Excluded" })).toBeInTheDocument();
  });

  it("keeps the failure line and focus on its Retry while asking again, then moves focus to Apply", async () => {
    const first = heldLookup();
    renderDialog();
    await waitFor(() => expect(api.resolveRelations).toHaveBeenCalled());
    first.fail(new Error("lookup failed"));
    const failure = await screen.findByText("Couldn't check which of these are in your library.");

    const again = heldLookup();
    const retry = within(failure.parentElement!).getByRole("button", { name: "Retry" });
    await userEvent.click(retry);

    expect(await screen.findByText("Checking your library again…")).toBeInTheDocument();
    expect(retry).toHaveFocus();
    again.answer(inLibrary("Known"));
    const apply = await screen.findByRole("button", { name: /Apply Selected Fields/ });
    await waitFor(() => expect(apply).toBeEnabled());
    await waitFor(() => expect(apply).toHaveFocus());
  });
});
