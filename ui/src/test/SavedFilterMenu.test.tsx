import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { savedFilters } from "../api/client";
import { SavedFilterMenu } from "../components/SavedFilterMenu";

vi.mock("../api/client", () => ({
  savedFilters: {
    list: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    delete: vi.fn(),
  },
}));

function renderMenu() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <SavedFilterMenu
        mode="videos"
        currentFilter={{ page: 2, sort: "title", direction: "asc" }}
        currentObjectFilter={{ favorite: true }}
        currentUIOptions={{ displayMode: "list", zoomLevel: 5.25 }}
        onApplyFilter={vi.fn()}
      />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.clearAllMocks();
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe("SavedFilterMenu", () => {
  it("closes when clicking outside the menu", async () => {
    vi.mocked(savedFilters.list).mockResolvedValue([]);
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <div>
          <button type="button">Outside</button>
          <SavedFilterMenu
            mode="videos"
            currentFilter={{ page: 1, perPage: 40 }}
            onApplyFilter={vi.fn()}
          />
        </div>
      </QueryClientProvider>,
    );

    await user.click(screen.getByTitle("Saved filters"));
    expect(screen.getByText("Saved Filters")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Save current filter" }));
    expect(screen.getByPlaceholderText("Filter name...")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Outside" }));

    expect(screen.queryByText("Saved Filters")).not.toBeInTheDocument();
  });

  it("exposes its open state and closes on Escape", async () => {
    vi.mocked(savedFilters.list).mockResolvedValue([]);
    const user = userEvent.setup();
    renderMenu();

    const trigger = screen.getByTitle("Saved filters");
    expect(trigger).toHaveAttribute("aria-expanded", "false");

    await user.click(trigger);
    expect(trigger).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("dialog", { name: "Saved filters" })).toBeInTheDocument();

    await user.keyboard("{Escape}");

    expect(trigger).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("dialog", { name: "Saved filters" })).not.toBeInTheDocument();
  });

  it("reveals a truncated saved-filter name on hover and keyboard focus", async () => {
    const longName = "A saved filter name that cannot fit in the available width";
    vi.mocked(savedFilters.list).mockResolvedValue([
      { id: 1, mode: "videos", name: longName, findFilter: "{}" },
    ]);
    const user = userEvent.setup();
    renderMenu();

    await user.click(screen.getByTitle("Saved filters"));
    const filterButton = await screen.findByRole("button", { name: longName });
    Object.defineProperties(filterButton, {
      clientWidth: { configurable: true, value: 120 },
      scrollWidth: { configurable: true, value: 320 },
    });

    await user.hover(filterButton);
    const hoverTooltip = screen.getByRole("tooltip", { name: longName });
    expect(filterButton).toHaveAttribute("aria-describedby", hoverTooltip.id);

    await user.unhover(filterButton);
    await user.hover(hoverTooltip);
    expect(screen.getByRole("tooltip", { name: longName })).toBeInTheDocument();
    await user.unhover(hoverTooltip);
    await waitFor(() => expect(screen.queryByRole("tooltip", { name: longName })).not.toBeInTheDocument());

    await user.tab();
    expect(filterButton).toHaveFocus();
    expect(screen.getByRole("tooltip", { name: longName })).toBeInTheDocument();
  });

  it("does not add a tooltip when the saved-filter name fits", async () => {
    vi.mocked(savedFilters.list).mockResolvedValue([
      { id: 1, mode: "videos", name: "Short name", findFilter: "{}" },
    ]);
    const user = userEvent.setup();
    renderMenu();

    await user.click(screen.getByTitle("Saved filters"));
    const filterButton = await screen.findByRole("button", { name: "Short name" });
    Object.defineProperties(filterButton, {
      clientWidth: { configurable: true, value: 120 },
      scrollWidth: { configurable: true, value: 80 },
    });

    await user.hover(filterButton);
    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
    expect(filterButton).not.toHaveAttribute("aria-describedby");
  });

  it("stays open when a mobile viewport shift moves the trigger offscreen while naming a filter", async () => {
    const visualViewport = Object.assign(new EventTarget(), {
      offsetTop: 0,
      offsetLeft: 0,
      width: 390,
      height: 400,
    });
    vi.stubGlobal("visualViewport", visualViewport);
    vi.mocked(savedFilters.list).mockResolvedValue([]);
    const user = userEvent.setup();
    renderMenu();

    const trigger = screen.getByTitle("Saved filters");
    await user.click(trigger);
    await user.click(screen.getByRole("button", { name: "Save current filter" }));
    const input = screen.getByPlaceholderText("Filter name...");
    expect(input).toHaveFocus();

    vi.spyOn(trigger, "getBoundingClientRect").mockReturnValue({
      x: 0, y: -100, top: -100, bottom: -76, left: 0, right: 48, width: 48, height: 24,
      toJSON: () => ({}),
    });
    fireEvent(visualViewport as unknown as Window, new Event("resize"));

    const dialog = await screen.findByRole("dialog", { name: "Saved filters" });
    await waitFor(() => expect(dialog).toHaveStyle({ top: "8px" }));
    expect(dialog).toHaveStyle({ maxHeight: "384px", maxWidth: "374px", minWidth: "224px" });
    expect(input).toHaveFocus();
  });

  it("updates a saved filter from the current state", async () => {
    vi.mocked(savedFilters.list).mockResolvedValue([
      { id: 2, mode: "videos", name: "alpha", findFilter: "{}" },
      { id: 1, mode: "videos", name: "Zulu", findFilter: "{}" },
    ]);
    vi.mocked(savedFilters.update).mockResolvedValue({
      id: 2,
      mode: "videos",
      name: "alpha",
      findFilter: "{}",
    });
    const user = userEvent.setup();
    renderMenu();

    await user.click(screen.getByTitle("Saved filters"));
    await screen.findByRole("button", { name: "alpha" });
    await user.click(screen.getByRole("button", { name: 'Update saved filter "alpha"' }));

    await waitFor(() => expect(savedFilters.update).toHaveBeenCalledWith(2, {
      findFilter: JSON.stringify({ page: 2, sort: "title", direction: "asc" }),
      objectFilter: JSON.stringify({ favorite: true }),
      uiOptions: JSON.stringify({ displayMode: "list", zoomLevel: 5.25 }),
    }));
    await waitFor(() => expect(screen.queryByText("Saved Filters")).not.toBeInTheDocument());
  });

  it("applies saved display and zoom options", async () => {
    vi.mocked(savedFilters.list).mockResolvedValue([
      {
        id: 1,
        mode: "videos",
        name: "Large wall",
        findFilter: "{}",
        uiOptions: JSON.stringify({ displayMode: "wall", zoomLevel: 5.25 }),
      },
    ]);
    const onApplyUIOptions = vi.fn();
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <SavedFilterMenu
          mode="videos"
          currentFilter={{ page: 1 }}
          onApplyFilter={vi.fn()}
          onApplyUIOptions={onApplyUIOptions}
        />
      </QueryClientProvider>,
    );

    await user.click(screen.getByTitle("Saved filters"));
    await user.click(await screen.findByRole("button", { name: "Large wall" }));

    expect(onApplyUIOptions).toHaveBeenCalledWith({ displayMode: "wall", zoomLevel: 5.25 });
  });

  it("keeps the menu open and reports an update failure", async () => {
    vi.mocked(savedFilters.list).mockResolvedValue([
      { id: 2, mode: "videos", name: "Favorites", findFilter: "{}" },
    ]);
    vi.mocked(savedFilters.update).mockRejectedValue(new Error("Conflict"));
    const user = userEvent.setup();
    renderMenu();

    await user.click(screen.getByTitle("Saved filters"));
    await user.click(await screen.findByRole("button", { name: 'Update saved filter "Favorites"' }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Could not update this saved filter.");
    expect(screen.getByText("Saved Filters")).toBeInTheDocument();
  });

  it("does not create a duplicate name ignoring case and whitespace", async () => {
    vi.mocked(savedFilters.list).mockResolvedValue([
      { id: 1, mode: "videos", name: "Favorites", findFilter: "{}" },
    ]);
    const user = userEvent.setup();
    renderMenu();

    await user.click(screen.getByTitle("Saved filters"));
    await user.click(await screen.findByRole("button", { name: "Save current filter" }));
    await user.type(screen.getByPlaceholderText("Filter name..."), " favorites ");

    expect(screen.getByText("A saved filter with this name already exists.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create saved filter" })).toBeDisabled();
    expect(savedFilters.create).not.toHaveBeenCalled();
  });
});
