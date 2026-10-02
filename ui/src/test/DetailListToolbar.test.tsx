import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DetailListPagination, DetailListToolbar } from "../components/DetailListToolbar";
import type { FindFilter } from "../api/types";
import { VIDEO_CRITERIA } from "../components/filterCriteriaCatalogs";
import { customFieldDefinitionsQueryKey } from "../hooks/useCustomFieldDefinitions";
import { useRegisterKeyboardActionHandler } from "../hooks/useRegisterKeyboardActionHandler";

const appConfigMock = vi.hoisted(() => ({ optional: null as unknown }));

vi.mock("../state/AppConfigContext", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../state/AppConfigContext")>()),
  useOptionalAppConfig: () => appConfigMock.optional,
}));

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
    localStorage.setItem(
      "cove-default-filter-videos",
      JSON.stringify({
        findFilter: { page: 1, perPage: 24 },
        uiOptions: { displayMode: "list", zoomLevel: 5.25 },
      }),
    );
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
    localStorage.setItem(
      "cove-default-filter-videos",
      JSON.stringify({
        findFilter: { page: 1, perPage: 24 },
        uiOptions: { displayMode: "list", zoomLevel: 5.25 },
      }),
    );
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

  it("formats the visible range with the same digit grouping as the total", () => {
    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 810, perPage: 30 }}
        onFilterChange={vi.fn()}
        totalCount={24311}
        sortOptions={[{ value: "title", label: "Title" }]}
      />,
    );

    expect(
      screen.getByText(`${(24271).toLocaleString()}–${(24300).toLocaleString()} of ${(24311).toLocaleString()}`),
    ).toBeInTheDocument();
  });

  it("applies search text after a short delay without requiring Enter", async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 3, perPage: 24 }}
        onFilterChange={onFilterChange}
        totalCount={100}
        sortOptions={[{ value: "title", label: "Title" }]}
        showSearch
      />,
    );

    await user.type(screen.getByPlaceholderText("Search…"), "summer");

    await waitFor(() =>
      expect(onFilterChange).toHaveBeenCalledWith({
        page: 1,
        perPage: 24,
        q: "summer",
      }),
    );
  });

  it("allows the toolbar to fill the available detail-list width", () => {
    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24 }}
        onFilterChange={vi.fn()}
        totalCount={100}
        sortOptions={[{ value: "title", label: "Title" }]}
        showSearch
      />,
    );

    // The search sits inside the centered controls section, which sits inside the toolbar row.
    const toolbar = screen.getByPlaceholderText("Search…").closest("form")?.parentElement?.parentElement;
    expect(toolbar).toHaveClass("w-full");
    expect(toolbar).not.toHaveClass("max-w-7xl");
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

    expect(useRegisterKeyboardActionHandler).toHaveBeenCalledWith("list.filters", expect.any(Function), {
      enabled: true,
      surface: "list",
    });
  });

  it("renders matching pagination above and below a finite detail list", async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();
    const filter = { page: 1, perPage: 24, sort: "title", direction: "desc" as const };

    renderWithQueryClient(
      <>
        <DetailListToolbar
          filter={filter}
          onFilterChange={onFilterChange}
          totalCount={100}
          sortOptions={[{ value: "title", label: "Title" }]}
          allowInfinitePageSize
        />
        <div>Results</div>
        <DetailListPagination filter={filter} onFilterChange={onFilterChange} totalCount={100} allowInfinitePageSize />
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
    render(<DetailListPagination filter={{ page: 2, perPage: 24 }} onFilterChange={vi.fn()} totalCount={100} />);

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
    renderWithQueryClient(
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
    renderWithQueryClient(
      <DetailListPagination
        filter={{ page: 9999, perPage: 24, q: "example" }}
        onFilterChange={onFilterChange}
        totalCount={100}
      />,
    );

    await waitFor(() =>
      expect(onFilterChange).toHaveBeenCalledWith({
        page: 5,
        perPage: 24,
        q: "example",
      }),
    );
  });

  it("does not reset a deep page while its result count is unavailable", () => {
    const onFilterChange = vi.fn();
    render(<DetailListPagination filter={{ page: 12, perPage: 24 }} onFilterChange={onFilterChange} totalCount={0} />);

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
        criteriaDefinitions={[
          { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
        ]}
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
    const objectFilter = {
      _filterExpression: {
        operator: "AND",
        children: [
          {
            group: {
              operator: "OR",
              children: [
                { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
                { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
              ],
            },
          },
        ],
      },
    };

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
    await user.click(screen.getByRole("button", { name: "Edit Any group in Combine Filters" }));
    expect(screen.getByRole("heading", { name: "Combine Filters" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Close filters" }));
    await user.click(screen.getByRole("button", { name: "Filters, 2 active" }));
    expect(screen.getByRole("dialog", { name: "Filters" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Combine Filters" })).not.toBeInTheDocument();
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

    renderWithQueryClient(
      <DetailListToolbar
        filter={{ page: 1, perPage: 24, sort: "random", direction: "asc", seed: 2468 }}
        onFilterChange={onFilterChange}
        totalCount={10}
        sortOptions={[{ value: "random", label: "Random" }]}
      />,
    );

    await user.click(screen.getByTitle("Ascending"));

    expect(onFilterChange).toHaveBeenCalledWith(
      expect.objectContaining({ sort: "random", direction: "desc", seed: 2468 }),
    );
  });

  it("shows a shuffle button for random sort and replaces the seed", async () => {
    const user = userEvent.setup();
    const onFilterChange = vi.fn();
    vi.spyOn(Math, "random").mockReturnValue(0.5);

    renderWithQueryClient(
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
    renderWithQueryClient(
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

    renderWithQueryClient(
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
    renderWithQueryClient(
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
    localStorage.setItem(
      "cove-default-filter-galleries",
      JSON.stringify({
        findFilter: { page: 7, perPage: 40, sort: "title", direction: "asc", q: "summer" },
        objectFilter: { favorite: true },
        uiOptions: { displayMode: "list" },
      }),
    );
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

    await waitFor(() =>
      expect(onFilterChange).toHaveBeenCalledWith({
        page: 1,
        perPage: 40,
        sort: "title",
        direction: "asc",
        q: "summer",
      }),
    );
    expect(onObjectFilterChange).toHaveBeenCalledWith({ favorite: true });
    expect(onDisplayModeChange).toHaveBeenCalledWith("list");
  });

  it("does not reapply a saved default that URL-backed state resolved before mount", async () => {
    localStorage.setItem(
      "cove-default-filter-videos",
      JSON.stringify({
        findFilter: { page: 1, perPage: 40, sort: "random", direction: "asc" },
      }),
    );
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

  describe("custom field filtering", () => {
    const reviewStatusField = {
      id: 7,
      key: "review_status",
      label: "Review status",
      type: "text",
      entityTypes: ["video"],
      options: [],
      filterable: true,
      sortable: false,
      isMultiValue: false,
      displayOrder: 0,
      createdAt: "2026-09-17T00:00:00Z",
      updatedAt: "2026-09-17T00:00:00Z",
    };

    function renderWithCustomFields(ui: React.ReactNode) {
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      queryClient.setQueryData(customFieldDefinitionsQueryKey("video"), [reviewStatusField]);
      return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
    }

    it("offers the entity's custom fields in the filter dialog and applies the criteria", async () => {
      const user = userEvent.setup();
      const onObjectFilterChange = vi.fn();

      renderWithCustomFields(
        <DetailListToolbar
          filter={{ page: 3, perPage: 40 }}
          onFilterChange={vi.fn()}
          totalCount={0}
          sortOptions={[{ value: "title", label: "Title" }]}
          criteriaDefinitions={VIDEO_CRITERIA}
          customFieldEntityType="video"
          objectFilter={{}}
          onObjectFilterChange={onObjectFilterChange}
        />,
      );

      await user.click(screen.getByRole("button", { name: "Filters" }));
      await user.click(screen.getByText("Custom Fields"));
      await user.click(screen.getByRole("button", { name: /add custom field filter/i }));
      await user.type(screen.getByLabelText("Value"), "stale");
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onObjectFilterChange).toHaveBeenCalledWith({
        customFieldCriteria: [{ key: "review_status", type: "text", modifier: "EQUALS", value: "stale" }],
      });
    });

    it("hides custom fields from the dialog without an entity type", async () => {
      const user = userEvent.setup();

      renderWithCustomFields(
        <DetailListToolbar
          filter={{ page: 1, perPage: 40 }}
          onFilterChange={vi.fn()}
          totalCount={0}
          sortOptions={[{ value: "title", label: "Title" }]}
          criteriaDefinitions={VIDEO_CRITERIA}
          objectFilter={{}}
          onObjectFilterChange={vi.fn()}
        />,
      );

      await user.click(screen.getByRole("button", { name: "Filters" }));

      expect(screen.queryByText("Custom Fields")).not.toBeInTheDocument();
    });

    it("summarizes active custom field criteria as a removable chip", async () => {
      const user = userEvent.setup();
      const onObjectFilterChange = vi.fn();
      const onFilterChange = vi.fn();

      renderWithCustomFields(
        <DetailListToolbar
          filter={{ page: 3, perPage: 40 }}
          onFilterChange={onFilterChange}
          totalCount={0}
          sortOptions={[{ value: "title", label: "Title" }]}
          criteriaDefinitions={VIDEO_CRITERIA}
          customFieldEntityType="video"
          objectFilter={{
            customFieldCriteria: [{ key: "review_status", type: "text", modifier: "EQUALS", value: "stale" }],
          }}
          onObjectFilterChange={onObjectFilterChange}
        />,
      );

      expect(screen.getByText(/Review status Equals stale/)).toBeInTheDocument();
      await user.click(screen.getByRole("button", { name: "Remove filter: Custom Fields" }));

      expect(onObjectFilterChange).toHaveBeenCalledWith({});
      expect(onFilterChange).toHaveBeenCalledWith({ page: 1, perPage: 40 });
    });

    it("ignores cached definitions for all entities when no entity type is requested", async () => {
      const user = userEvent.setup();
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      queryClient.setQueryData(customFieldDefinitionsQueryKey(), [reviewStatusField]);

      render(
        <QueryClientProvider client={queryClient}>
          <DetailListToolbar
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            sortOptions={[{ value: "title", label: "Title" }]}
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{}}
            onObjectFilterChange={vi.fn()}
          />
        </QueryClientProvider>,
      );

      await user.click(screen.getByRole("button", { name: "Filters" }));

      expect(screen.queryByText("Custom Fields")).not.toBeInTheDocument();
    });

    it("opens the dialog on the custom field section when its chip is edited", async () => {
      const user = userEvent.setup();

      renderWithCustomFields(
        <DetailListToolbar
          filter={{ page: 1, perPage: 40 }}
          onFilterChange={vi.fn()}
          totalCount={0}
          sortOptions={[{ value: "title", label: "Title" }]}
          criteriaDefinitions={VIDEO_CRITERIA}
          customFieldEntityType="video"
          objectFilter={{
            customFieldCriteria: [{ key: "review_status", type: "text", modifier: "EQUALS", value: "stale" }],
          }}
          onObjectFilterChange={vi.fn()}
        />,
      );

      await user.click(screen.getByRole("button", { name: "Edit filter: Custom Fields" }));

      expect(screen.getByRole("tab", { name: "Custom Fields", selected: true })).toBeInTheDocument();
      expect(screen.getByLabelText("Value")).toHaveValue("stale");
    });

    it("derives the entity from the saved-filter mode of an embedded list", async () => {
      const user = userEvent.setup();
      const onObjectFilterChange = vi.fn();

      renderWithCustomFields(
        <DetailListToolbar
          filter={{ page: 1, perPage: 40 }}
          onFilterChange={vi.fn()}
          totalCount={0}
          sortOptions={[{ value: "title", label: "Title" }]}
          criteriaDefinitions={VIDEO_CRITERIA}
          filterMode="videos"
          objectFilter={{}}
          onObjectFilterChange={onObjectFilterChange}
        />,
      );

      await user.click(screen.getByRole("button", { name: "Filters" }));
      await user.click(screen.getByText("Custom Fields"));
      await user.click(screen.getByRole("button", { name: /add custom field filter/i }));
      await user.type(screen.getByLabelText("Value"), "stale");
      await user.click(screen.getByRole("button", { name: "Apply" }));

      expect(onObjectFilterChange).toHaveBeenCalledWith({
        customFieldCriteria: [{ key: "review_status", type: "text", modifier: "EQUALS", value: "stale" }],
      });
    });
  });

  describe("relevance sorting", () => {
    it("switches a searchable subview to relevance and restores the previous sort when cleared", () => {
      const onFilterChange = vi.fn();
      const renderToolbar = (filter: FindFilter) => (
        <DetailListToolbar
          filter={filter}
          onFilterChange={onFilterChange}
          totalCount={10}
          sortOptions={[{ value: "name", label: "Name" }]}
          filterMode="performers"
          showSearch
        />
      );

      const { rerender } = renderWithQueryClient(
        renderToolbar({ page: 3, perPage: 24, sort: "random", direction: "asc", seed: 7 }),
      );

      expect(
        within(screen.getByRole("combobox", { name: "Primary sort" })).queryByRole("option", { name: "Relevance" }),
      ).not.toBeInTheDocument();

      fireEvent.change(screen.getByRole("textbox", { name: "Search list" }), { target: { value: "needle" } });
      fireEvent.submit(screen.getByRole("textbox", { name: "Search list" }).closest("form")!);

      const searchedFilter = onFilterChange.mock.lastCall?.[0];
      expect(searchedFilter).toEqual({
        page: 1,
        perPage: 24,
        q: "needle",
        sort: "relevance",
        direction: "desc",
        sorts: undefined,
        seed: undefined,
      });

      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      rerender(<QueryClientProvider client={queryClient}>{renderToolbar(searchedFilter)}</QueryClientProvider>);

      expect(screen.getByRole("combobox", { name: "Primary sort" })).toHaveValue("relevance");
      expect(screen.queryByRole("button", { name: "Descending" })).not.toBeInTheDocument();

      fireEvent.click(screen.getByRole("button", { name: "Clear search" }));

      expect(onFilterChange.mock.lastCall?.[0]).toEqual({
        page: 1,
        perPage: 24,
        q: undefined,
        sort: "random",
        direction: "asc",
        sorts: undefined,
        seed: 7,
      });

      const restoredClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      rerender(
        <QueryClientProvider client={restoredClient}>
          {renderToolbar(onFilterChange.mock.lastCall?.[0])}
        </QueryClientProvider>,
      );

      expect(screen.getByRole("button", { name: "Ascending" })).toBeInTheDocument();
      expect(
        within(screen.getByRole("combobox", { name: "Primary sort" })).queryByRole("option", { name: "Relevance" }),
      ).not.toBeInTheDocument();
    });

    it("keeps the subview's sort for a new search when the library turns relevance sorting off", () => {
      appConfigMock.optional = { config: { ui: { sortSearchesByRelevance: false } } };
      const onFilterChange = vi.fn();
      try {
        renderWithQueryClient(
          <DetailListToolbar
            filter={{ page: 3, perPage: 24, sort: "name", direction: "asc" }}
            onFilterChange={onFilterChange}
            totalCount={10}
            sortOptions={[{ value: "name", label: "Name" }]}
            filterMode="performers"
            showSearch
          />,
        );

        fireEvent.change(screen.getByRole("textbox", { name: "Search list" }), { target: { value: "needle" } });
        fireEvent.submit(screen.getByRole("textbox", { name: "Search list" }).closest("form")!);

        expect(onFilterChange.mock.lastCall?.[0]).toEqual({
          page: 1,
          perPage: 24,
          q: "needle",
          sort: "name",
          direction: "asc",
        });
      } finally {
        appConfigMock.optional = null;
      }
    });

    it("pins descending and keeps the option listed when relevance is chosen from the dropdown", () => {
      const onFilterChange = vi.fn();

      renderWithQueryClient(
        <DetailListToolbar
          filter={{
            page: 2,
            perPage: 24,
            q: "needle",
            sort: "title",
            direction: "asc",
            sorts: [{ key: "title", direction: "asc" }],
          }}
          onFilterChange={onFilterChange}
          totalCount={10}
          sortOptions={[{ value: "title", label: "Title" }]}
          filterMode="videos"
          showSearch
        />,
      );

      fireEvent.change(screen.getByRole("combobox", { name: "Primary sort" }), { target: { value: "relevance" } });

      expect(onFilterChange).toHaveBeenCalledWith(
        expect.objectContaining({ sort: "relevance", direction: "desc", sorts: undefined, page: 1 }),
      );
    });

    it("keeps relevance selectable when it is the active sort without a query", () => {
      renderWithQueryClient(
        <DetailListToolbar
          filter={{ page: 1, perPage: 24, sort: "relevance", direction: "desc" }}
          onFilterChange={vi.fn()}
          totalCount={10}
          sortOptions={[{ value: "title", label: "Title" }]}
          filterMode="videos"
          showSearch
        />,
      );

      expect(screen.getByRole("combobox", { name: "Primary sort" })).toHaveValue("relevance");
      expect(screen.queryByRole("button", { name: "Descending" })).not.toBeInTheDocument();
    });

    it("restores a valid fallback sort when a deep-linked relevance search is cleared", () => {
      const onFilterChange = vi.fn();

      renderWithQueryClient(
        <DetailListToolbar
          filter={{ page: 1, perPage: 24, q: "needle", sort: "relevance", direction: "desc" }}
          onFilterChange={onFilterChange}
          totalCount={10}
          sortOptions={[{ value: "name", label: "Name" }]}
          filterMode="performers"
          showSearch
        />,
      );

      fireEvent.click(screen.getByRole("button", { name: "Clear search" }));

      expect(onFilterChange.mock.lastCall?.[0]).toEqual({
        page: 1,
        perPage: 24,
        q: undefined,
        sort: "name",
        direction: "desc",
        sorts: undefined,
        seed: undefined,
      });
    });

    it("leaves the caller's sort options untouched while sorting them for display", () => {
      const sortOptions = [
        { value: "title", label: "Title" },
        { value: "date", label: "Date" },
      ];

      renderWithQueryClient(
        <DetailListToolbar
          filter={{ page: 1, perPage: 24, sort: "title" }}
          onFilterChange={vi.fn()}
          totalCount={10}
          sortOptions={sortOptions}
          filterMode="videos"
          showSearch
        />,
      );

      expect(sortOptions.map((option) => option.value)).toEqual(["title", "date"]);
      expect(
        within(screen.getByRole("combobox", { name: "Primary sort" }))
          .getAllByRole("option")
          .map((option) => option.textContent),
      ).toEqual(["Date", "Title"]);
    });

    // A face subview's endpoints filter and sort in memory and would ignore sort=relevance, so the
    // list pages do not offer relevance for faces and neither may a face subview.
    it("leaves a subview whose entity has no relevance ordering on its existing sort", () => {
      const onFilterChange = vi.fn();

      renderWithQueryClient(
        <DetailListToolbar
          filter={{ page: 2, perPage: 24, sort: "similarity", direction: "desc" }}
          onFilterChange={onFilterChange}
          totalCount={10}
          sortOptions={[{ value: "similarity", label: "Similarity" }]}
          listEntityType="faces"
          showSearch
        />,
      );

      fireEvent.change(screen.getByRole("textbox", { name: "Search list" }), { target: { value: "needle" } });
      fireEvent.submit(screen.getByRole("textbox", { name: "Search list" }).closest("form")!);

      expect(onFilterChange.mock.lastCall?.[0]).toEqual({
        page: 1,
        perPage: 24,
        q: "needle",
        sort: "similarity",
        direction: "desc",
      });
      expect(
        within(screen.getByRole("combobox", { name: "Primary sort" })).queryByRole("option", { name: "Relevance" }),
      ).not.toBeInTheDocument();
    });
  });
});
