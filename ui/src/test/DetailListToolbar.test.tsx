import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DetailListToolbar } from "../components/DetailListToolbar";

vi.mock("../api/client", () => ({
  savedFilters: {
    list: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    delete: vi.fn(),
  },
  tags: {
    find: vi.fn().mockResolvedValue({ items: [] }),
  },
  performers: { find: vi.fn().mockResolvedValue({ items: [] }) },
  studios: { find: vi.fn().mockResolvedValue({ items: [] }) },
  groups: { find: vi.fn().mockResolvedValue({ items: [] }) },
  tagGroups: { list: vi.fn().mockResolvedValue([]) },
}));

function renderWithQueryClient(ui: React.ReactNode) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
}

afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

describe("DetailListToolbar", () => {
  it("applies search text after a short delay without requiring Enter", async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();

    render(
      <DetailListToolbar
        filter={{ page: 3, perPage: 24 }}
        onFilterChange={onFilterChange}
        totalCount={100}
        sortOptions={[{ value: "title", label: "Title" }]}
        showSearch
      />,
    );

    await user.type(screen.getByPlaceholderText("Search…"), "summer");

    await waitFor(() => expect(onFilterChange).toHaveBeenCalledWith({
      page: 1,
      perPage: 24,
      q: "summer",
    }));
  });

  it("shows and removes applied object-filter parameters", async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();
    const onObjectFilterChange = vi.fn();

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 3, perPage: 24 }}
        onFilterChange={onFilterChange}
        totalCount={10}
        sortOptions={[{ value: "title", label: "Title" }]}
        criteriaDefinitions={[{ id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" }]}
        objectFilter={{
          tagsCriterion: {
            value: [804],
            _names: { "804": "Facial" },
            modifier: "INCLUDES_ALL",
          },
        }}
        onObjectFilterChange={onObjectFilterChange}
      />,
    );

    const chip = screen.getByRole("button", { name: /tags:/i });
    expect(chip).toHaveTextContent("Tags:");
    expect(chip).toHaveTextContent("Includes All Facial");
    expect(chip.parentElement).toHaveClass("mb-2");

    await user.click(chip);

    expect(onObjectFilterChange).toHaveBeenCalledWith({});
    expect(onFilterChange).toHaveBeenCalledWith({ page: 1, perPage: 24 });
  });

  it("clears all applied object-filter parameters", async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();
    const onObjectFilterChange = vi.fn();

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 4, perPage: 40 }}
        onFilterChange={onFilterChange}
        totalCount={10}
        sortOptions={[{ value: "title", label: "Title" }]}
        criteriaDefinitions={[
          { id: "rating", label: "Rating", type: "number", filterKey: "ratingCriterion" },
          { id: "favorite", label: "Favorite", type: "bool", filterKey: "favoriteCriterion" },
        ]}
        objectFilter={{
          ratingCriterion: { value: 80, modifier: "GREATER_THAN" },
          favoriteCriterion: true,
        }}
        onObjectFilterChange={onObjectFilterChange}
      />,
    );

    await user.click(screen.getByRole("button", { name: "Clear all" }));

    expect(onObjectFilterChange).toHaveBeenCalledWith({});
    expect(onFilterChange).toHaveBeenCalledWith({ page: 1, perPage: 40 });
  });

  it("preserves the random seed when toggling sort direction", async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();

    render(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24, sort: "random", direction: "asc", seed: 2468 }}
        onFilterChange={onFilterChange}
        totalCount={10}
        sortOptions={[{ value: "random", label: "Random" }]}
      />,
    );

    await user.click(screen.getByTitle("Ascending"));

    expect(onFilterChange).toHaveBeenCalledWith(expect.objectContaining({ sort: "random", direction: "desc", seed: 2468 }));
  });

  it("shows a shuffle button for random sort and replaces the seed", async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();
    vi.spyOn(Math, "random").mockReturnValue(0.5);

    render(
      <DetailListToolbar
        filter={{ page: 3, perPage: 24, sort: "random", direction: "asc", seed: 2468 }}
        onFilterChange={onFilterChange}
        totalCount={10}
        sortOptions={[{ value: "random", label: "Random" }]}
      />,
    );

    await user.click(screen.getByTitle("Shuffle"));

    expect(onFilterChange).toHaveBeenCalledWith(expect.objectContaining({ sort: "random", page: 1, seed: 1073741823 }));
  });

  it("uses the expanded image slider max for image detail lists", () => {
    render(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={10}
        sortOptions={[{ value: "title", label: "Title" }]}
        zoomLevel={1}
        onZoomChange={vi.fn()}
        cardSizeEntityType="images"
      />,
    );

    expect(screen.getByRole("slider")).toHaveAttribute("max", "8");
  });

  it("applies the complete saved default for an embedded list", async () => {
    localStorage.setItem("cove-default-filter-galleries", JSON.stringify({
      findFilter: { page: 7, perPage: 40, sort: "title", direction: "asc", q: "summer" },
      objectFilter: { favorite: true },
      uiOptions: { displayMode: "list" },
    }));
    const onFilterChange = vi.fn();
    const onObjectFilterChange = vi.fn();
    const onDisplayModeChange = vi.fn();

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 3, perPage: 18, direction: "desc" }}
        onFilterChange={onFilterChange}
        totalCount={100}
        sortOptions={[{ value: "title", label: "Title" }]}
        filterMode="galleries"
        objectFilter={{}}
        onObjectFilterChange={onObjectFilterChange}
        displayMode="grid"
        onDisplayModeChange={onDisplayModeChange}
        availableDisplayModes={["grid", "list"]}
      />,
    );

    await waitFor(() => expect(onFilterChange).toHaveBeenCalledWith({
      page: 1,
      perPage: 40,
      sort: "title",
      direction: "asc",
      q: "summer",
    }));
    expect(onObjectFilterChange).toHaveBeenCalledWith({ favorite: true });
    expect(onDisplayModeChange).toHaveBeenCalledWith("list");
  });

  it("does not reapply a saved default that URL-backed state resolved before mount", async () => {
    localStorage.setItem("cove-default-filter-videos", JSON.stringify({
      findFilter: { page: 1, perPage: 40, sort: "random", direction: "asc" },
    }));
    const onFilterChange = vi.fn();

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 1, perPage: 40, sort: "random", direction: "asc", seed: 2468 }}
        onFilterChange={onFilterChange}
        totalCount={100}
        sortOptions={[{ value: "random", label: "Random" }]}
        filterMode="videos"
        defaultFilterResolved
      />,
    );

    await waitFor(() => expect(onFilterChange).not.toHaveBeenCalled());
  });
});
