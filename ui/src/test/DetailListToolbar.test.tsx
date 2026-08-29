import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DetailListPagination, DetailListToolbar } from "../components/DetailListToolbar";
import { VIDEO_CRITERIA } from "../components/FilterDialog";
import { useRegisterKeyboardActionHandler } from "../hooks/useRegisterKeyboardActionHandler";

vi.mock("../hooks/useRegisterKeyboardActionHandler", () => ({
  useRegisterKeyboardActionHandler: vi.fn(),
}));

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
  it("applies the default saved filter's zoom to an embedded list", async () => {
    localStorage.setItem("cove-default-filter-videos", JSON.stringify({
      findFilter: { page: 1, perPage: 24 },
      uiOptions: { displayMode: "list", zoomLevel: 5.25 },
    }));
    const onZoomChange = vi.fn();
    const onDisplayModeChange = vi.fn();

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={0}
        sortOptions={[{ value: "title", label: "Title" }]}
        zoomLevel={1}
        onZoomChange={onZoomChange}
        cardSizeEntityType="videos"
        displayMode="grid"
        onDisplayModeChange={onDisplayModeChange}
        availableDisplayModes={["grid", "list"]}
        filterMode="videos"
      />,
    );

    await waitFor(() => expect(onZoomChange).toHaveBeenCalledWith(5.25));
    expect(onDisplayModeChange).toHaveBeenCalledWith("list");
    expect(localStorage.getItem("cove.cardSize.video")).toBe("5.25");
  });

  it("applies default zoom when the embedded list filter was resolved from the URL", async () => {
    localStorage.setItem("cove-default-filter-videos", JSON.stringify({
      findFilter: { page: 1, perPage: 24 },
      uiOptions: { displayMode: "list", zoomLevel: 5.25 },
    }));
    const onFilterChange = vi.fn();
    const onZoomChange = vi.fn();
    const onDisplayModeChange = vi.fn();

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 3, perPage: 48 }}
        onFilterChange={onFilterChange}
        totalCount={0}
        sortOptions={[{ value: "title", label: "Title" }]}
        zoomLevel={1}
        onZoomChange={onZoomChange}
        cardSizeEntityType="videos"
        displayMode="grid"
        onDisplayModeChange={onDisplayModeChange}
        availableDisplayModes={["grid", "list"]}
        filterMode="videos"
        defaultFilterResolved
      />,
    );

    await waitFor(() => expect(onZoomChange).toHaveBeenCalledWith(5.25));
    expect(localStorage.getItem("cove.cardSize.video")).toBe("5.25");
    expect(onFilterChange).not.toHaveBeenCalled();
    expect(onDisplayModeChange).not.toHaveBeenCalled();
  });

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

  it("registers the filter action when filtering is available", () => {
    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={10}
        sortOptions={[{ value: "title", label: "Title" }]}
        criteriaDefinitions={[{ id: "title", label: "Title", type: "string", filterKey: "titleCriterion" }]}
        objectFilter={{}}
        onObjectFilterChange={vi.fn()}
      />,
    );

    expect(useRegisterKeyboardActionHandler).toHaveBeenCalledWith(
      "list.filters",
      expect.any(Function),
      { enabled: true, surface: "list" },
    );
  });

  it("renders matching pagination above and below a finite detail list", async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();
    const filter = { page: 1, perPage: 24, sort: "title", direction: "desc" as const };

    render(
      <>
        <DetailListToolbar
          filter={filter}
          onFilterChange={onFilterChange}
          totalCount={100}
          sortOptions={[{ value: "title", label: "Title" }]}
          allowInfinitePageSize
        />
        <div>Results</div>
        <DetailListPagination
          filter={filter}
          onFilterChange={onFilterChange}
          totalCount={100}
          allowInfinitePageSize
        />
      </>,
    );

    const pageTwoButtons = screen.getAllByRole("button", { name: "Page 2" });
    expect(pageTwoButtons).toHaveLength(2);

    await user.click(pageTwoButtons[1]);

    expect(onFilterChange).toHaveBeenCalledWith({
      page: 2,
      perPage: 24,
      sort: "title",
      direction: "desc",
    });
  });

  it("labels native pagination controls and identifies the current page", () => {
    render(
      <DetailListPagination
        filter={{ page: 2, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={100}
      />,
    );

    expect(screen.getByRole("button", { name: "First page" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Previous page" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Next page" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Last page" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Page 2" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("navigation", { name: "Pagination" })).toBeInTheDocument();
    for (const button of screen.getAllByRole("button")) {
      expect(button).toHaveAttribute("type", "button");
    }
  });

  it("supports distinct navigation landmarks for multiple pagers", () => {
    const filter = { page: 2, perPage: 24 };
    render(
      <>
        <DetailListPagination
          filter={filter}
          onFilterChange={vi.fn()}
          totalCount={100}
          ariaLabel="Results pagination above list"
        />
        <DetailListPagination
          filter={filter}
          onFilterChange={vi.fn()}
          totalCount={100}
          ariaLabel="Results pagination below list"
        />
      </>,
    );

    expect(screen.getByRole("navigation", { name: "Results pagination above list" })).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Results pagination below list" })).toBeInTheDocument();
  });

  it("does not render detail pagination for infinite or single-page lists", () => {
    const { rerender } = render(
      <DetailListPagination
        filter={{ page: 1, perPage: 0 }}
        onFilterChange={vi.fn()}
        totalCount={100}
        allowInfinitePageSize
      />,
    );

    expect(screen.queryByRole("button")).not.toBeInTheDocument();

    rerender(
      <DetailListPagination
        filter={{ page: 1, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={24}
        allowInfinitePageSize
      />,
    );

    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("corrects an out-of-range page when used without the toolbar", async () => {
    const onFilterChange = vi.fn();
    render(
      <DetailListPagination
        filter={{ page: 9999, perPage: 24, q: "example" }}
        onFilterChange={onFilterChange}
        totalCount={100}
      />,
    );

    await waitFor(() => expect(onFilterChange).toHaveBeenCalledWith({
      page: 5,
      perPage: 24,
      q: "example",
    }));
  });

  it("does not reset a deep page while its result count is unavailable", () => {
    const onFilterChange = vi.fn();
    render(
      <DetailListPagination
        filter={{ page: 12, perPage: 24 }}
        onFilterChange={onFilterChange}
        totalCount={0}
      />,
    );

    expect(onFilterChange).not.toHaveBeenCalled();
  });

  it("opens an applied object-filter parameter for editing and only removes it from the remove button", async () => {
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

    const chip = screen.getByRole("button", { name: "Edit filter: Tags" });
    expect(chip.parentElement).toHaveClass("min-h-[26px]", "max-w-full", "text-xs");
    expect(chip).toHaveTextContent("Tags:");
    expect(chip).toHaveTextContent("Tags:Facial");
    expect(chip.parentElement?.parentElement).toHaveClass("mb-2");
    onFilterChange.mockClear();

    await user.click(chip);

    expect(screen.getByRole("dialog", { name: "Filters" })).toBeInTheDocument();
    expect(screen.getByRole("tabpanel", { name: "Tags" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Includes All" })).toBeInTheDocument();
    expect(onObjectFilterChange).not.toHaveBeenCalled();
    expect(onFilterChange).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await user.click(screen.getByRole("button", { name: "Remove filter: Tags" }));

    expect(onObjectFilterChange).toHaveBeenCalledWith({});
    expect(onFilterChange).toHaveBeenCalledWith({ page: 1, perPage: 24 });
  });

  it("routes nested expression operators and leaves to their matching filter views", async () => {
    const user = userEvent.setup();
    const objectFilter = { _filterExpression: { operator: "AND", children: [
      { group: { operator: "OR", children: [
        { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
        { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
      ] } },
    ] } };

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={10}
        sortOptions={[{ value: "title", label: "Title" }]}
        criteriaDefinitions={VIDEO_CRITERIA}
        objectFilter={objectFilter}
        onObjectFilterChange={vi.fn()}
      />,
    );

    await user.click(screen.getByRole("button", { name: "Edit filter: Date < 2000-01-01" }));
    expect(screen.getByRole("complementary", { name: "Filter criteria" })).toBeInTheDocument();
    const second = screen.getByRole("group", { name: "Date condition 2" });
    await waitFor(() => expect(within(second).getByRole("button", { name: "<" })).toHaveFocus());

    await user.click(screen.getByRole("button", { name: "Close filters" }));
    await user.click(screen.getByRole("button", { name: "Edit OR group in advanced filters" }));
    expect(screen.getByRole("heading", { name: "Advanced filter" })).toBeInTheDocument();
  });

  it("normalizes a legacy performer-favorite chip before editing or removing it", async () => {
    const user = userEvent.setup();
    const onObjectFilterChange = vi.fn();

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={10}
        sortOptions={[{ value: "title", label: "Title" }]}
        criteriaDefinitions={VIDEO_CRITERIA}
        objectFilter={{ performerFavoriteCriterion: { value: true } }}
        onObjectFilterChange={onObjectFilterChange}
      />,
    );

    await user.click(screen.getByRole("button", { name: "Edit performer filter: Favorite" }));
    expect(screen.getByRole("tabpanel", { name: "Favorite" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await user.click(screen.getByRole("button", { name: "Remove performer filter: Favorite" }));
    expect(onObjectFilterChange).toHaveBeenCalledWith({});
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

  it("uses wall size levels for an embedded wall list", async () => {
    const user = userEvent.setup();
    const onZoomChange = vi.fn();
    localStorage.setItem("cove.cardSize.video", "5");

    render(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={10}
        sortOptions={[{ value: "title", label: "Title" }]}
        zoomLevel={5}
        onZoomChange={onZoomChange}
        cardSizeEntityType="videos"
        displayMode="wall"
      />,
    );

    const slider = screen.getByRole("slider", { name: "Wall card size" });
    expect(slider).toHaveAttribute("min", "2");
    expect(slider).toHaveAttribute("max", "8");
    expect(slider).toHaveAttribute("step", "1");
    expect(screen.getByText("5 cols")).toBeInTheDocument();

    await user.click(slider);
    fireEvent.change(slider, { target: { value: "8" } });

    expect(onZoomChange).toHaveBeenCalledWith(8);
    expect(localStorage.getItem("cove.cardSize.video")).toBe("8");
  });

  it("hides the size slider for embedded modes without card sizing", () => {
    render(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={10}
        sortOptions={[{ value: "title", label: "Title" }]}
        zoomLevel={5}
        onZoomChange={vi.fn()}
        displayMode="tagger"
      />,
    );

    expect(screen.queryByRole("slider")).not.toBeInTheDocument();
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
