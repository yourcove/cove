import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
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
        currentUIOptions={{ view: "list" }}
        onApplyFilter={vi.fn()}
      />
    </QueryClientProvider>,
  );
}

afterEach(() => {
  vi.clearAllMocks();
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
      uiOptions: JSON.stringify({ view: "list" }),
    }));
    await waitFor(() => expect(screen.queryByText("Saved Filters")).not.toBeInTheDocument());
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
