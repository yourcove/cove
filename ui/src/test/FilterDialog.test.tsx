import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactElement } from "react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FilterButton, FilterDialog, RemoteIdFilterEditor, PERFORMER_CRITERIA, VIDEO_CRITERIA, TAG_CRITERIA, STUDIO_CRITERIA, type CriterionDefinition } from "../components/FilterDialog";
import { countActiveObjectFilters } from "../components/ActiveObjectFilterChips";
import type { CriterionModifier } from "../api/types";
import { writeStoredRatingOptionsOverride } from "../utils/ratingPreferences";
import { AppConfigProvider } from "../state/AppConfigContext";

const { performersFind, studiosFind, tagsFind, libraryFolders, savedFiltersList } = vi.hoisted(() => ({ performersFind: vi.fn(), studiosFind: vi.fn(), tagsFind: vi.fn(), libraryFolders: vi.fn(), savedFiltersList: vi.fn() }));
const scrollIntoViewDescriptor = Object.getOwnPropertyDescriptor(Element.prototype, "scrollIntoView");
const matchMediaDescriptor = Object.getOwnPropertyDescriptor(window, "matchMedia");

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    performers: { ...actual.performers, find: performersFind },
    studios: { ...actual.studios, find: studiosFind },
    tags: { ...actual.tags, find: tagsFind },
    metadata: { ...actual.metadata, libraryFolders },
    savedFilters: { ...actual.savedFilters, list: savedFiltersList },
  };
});

function renderWithQueryClient(ui: ReactElement, setup?: (client: QueryClient) => void) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  setup?.(client);
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

function startRootSubgroupCreation() {
  fireEvent.click(screen.getByRole("button", { name: "More actions for root group" }));
  fireEvent.click(screen.getByRole("button", { name: "Create subgroup" }));
}

describe("FilterDialog", () => {
  beforeEach(() => {
    localStorage.clear();
    tagsFind.mockResolvedValue({ items: [] });
    libraryFolders.mockResolvedValue([]);
    savedFiltersList.mockResolvedValue([]);
  });

  it("uses a saved performer filter as a related video condition", async () => {
    savedFiltersList.mockResolvedValue([
      {
        id: 7,
        mode: "performers",
        name: "Favorite performers",
        findFilter: "{}",
        objectFilter: JSON.stringify({ favoriteCriterion: { value: true } }),
      },
    ]);
    const onApply = vi.fn();

    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Related Performers"));
    await screen.findByRole("option", { name: "Favorite performers" });
    fireEvent.change(screen.getByLabelText("Saved performer filter"), { target: { value: "7" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      performerFilterCriterion: {
        objectFilter: { favoriteCriterion: { value: true } },
        _savedFilterName: "Favorite performers",
      },
    });
    expect(savedFiltersList).toHaveBeenCalledWith("performers");
  });

  it("builds an ad hoc related performer filter in the same dialog", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Related Performers"));
    expect(screen.getByRole("button", { name: "Back to filters" })).toBeInTheDocument();
    expect(screen.getByLabelText("Performers").querySelector(".lucide-users")).toBeInTheDocument();
    expect(screen.queryByLabelText("Add another performer condition")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Text search" }));
    fireEvent.change(screen.getByLabelText("Search related performers"), { target: { value: "Bianca" } });
    fireEvent.click(screen.getByRole("tab", { name: "Favorite" }));
    fireEvent.click(screen.getByRole("button", { name: "True" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      performerFilterCriterion: {
        findFilter: { q: "Bianca" },
        objectFilter: { favoriteCriterion: { value: true } },
      },
    });
  });

  it("adds another related performer condition without opening Combine Filters", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          performerCountCriterion: { modifier: "EQUALS", value: 2 },
          performerFilterCriterion: {
            objectFilter: { genderCriterion: { modifier: "MATCHES_REGEX", value: "^(?:Male)$", _selectedValues: ["Male"] } },
            ageAtHostDateCriterion: { modifier: "BETWEEN", value: 18, value2: 25 },
          },
        }}
        onApply={onApply}
        preselectCriterion="relatedPerformers"
        supportsFilterExpressions
      />,
    );

    const addPerformerCondition = screen.getByRole("button", { name: "Add another performer condition" });
    expect(screen.getByRole("group", { name: "Filter composition actions" })).toContainElement(addPerformerCondition);
    expect(screen.getByRole("button", { name: "Close filters" }).parentElement).not.toContainElement(addPerformerCondition);
    fireEvent.click(addPerformerCondition);
    expect(screen.getByRole("heading", { name: "Add Related Performers condition" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Combine Filters" })).not.toBeInTheDocument();

    expect(screen.getByRole("button", { name: "Apply" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Back to filters" }));
    expect(screen.getByRole("button", { name: "Edit performer filter: Gender" })).toHaveTextContent("Male");
    await waitFor(() => expect(screen.getByRole("button", { name: "Add another performer condition" })).toHaveFocus());
    fireEvent.click(screen.getByRole("button", { name: "Add another performer condition" }));

    fireEvent.click(screen.getByRole("tab", { name: "Gender" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Female" }));
    fireEvent.click(screen.getByRole("tab", { name: "Age (then)" }));
    fireEvent.click(screen.getByRole("button", { name: "Between" }));
    fireEvent.change(screen.getByRole("spinbutton", { name: "Minimum" }), { target: { value: "30" } });
    fireEvent.change(screen.getByRole("spinbutton", { name: "Maximum" }), { target: { value: "40" } });
    expect(screen.queryByRole("button", { name: "Save condition" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Done" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Filters / Related Performers" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add another performer condition" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByText("Related Performers"));
    expect(screen.getByRole("heading", { name: "Filters / Related Performers" })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Filter composition actions" })).toContainElement(
      screen.getByRole("button", { name: "Add another performer condition" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Remove performer filter: Gender" }));
    expect(screen.queryByRole("button", { name: "Edit performer filter: Gender" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Gender" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "Male" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    const performerConditions = screen.getAllByRole("button", { name: "Edit filter: Related Performers" });
    expect(performerConditions).toHaveLength(2);
    fireEvent.click(performerConditions[0]);
    expect(screen.getByRole("heading", { name: "Edit Related Performers condition" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Edit performer filter: Gender" }));
    expect(screen.getByRole("checkbox", { name: "Male" })).toBeChecked();
    fireEvent.click(screen.getByRole("button", { name: "Cancel condition" }));
    await waitFor(() => expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument());
    expect(screen.queryByRole("heading", { name: "Filters / Related Performers" })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Edit filter: Related Performers" })[0]).toHaveFocus());
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
          { filter: {
            performerFilterCriterion: {
              objectFilter: { genderCriterion: { modifier: "MATCHES_REGEX", value: "^(?:Male)$", _selectedValues: ["Male"] } },
              ageAtHostDateCriterion: { modifier: "BETWEEN", value: 18, value2: 25 },
            },
          } },
          { filter: {
            performerFilterCriterion: {
              objectFilter: { genderCriterion: { modifier: "MATCHES_REGEX", value: "^(?:Female)$", _selectedValues: ["Female"] } },
              ageAtHostDateCriterion: { modifier: "BETWEEN", value: 30, value2: 40 },
            },
          } },
        ],
      },
    });
  });

  it("shows the repeat action only for the currently open criterion", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          resolutionCriterion: { modifier: "EQUALS", value: 2160 },
          performerFilterCriterion: { ageAtHostDateCriterion: { modifier: "EQUALS", value: 18 } },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );

    expect(screen.queryByRole("button", { name: "Add another performer condition" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Resolution" }));
    expect(screen.getByRole("button", { name: "Add another Resolution" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add another performer condition" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    expect(screen.queryByRole("button", { name: "Add another Resolution" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add another performer condition" })).not.toBeInTheDocument();
  });

  it("uses Apply to return from related filters before applying the full filter", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();
    const onClose = vi.fn();
    renderWithQueryClient(
      <FilterDialog open onClose={onClose} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());
    await user.click(screen.getByText("Related Performers"));
    await user.click(screen.getByRole("tab", { name: "Text search" }));
    await user.type(screen.getByLabelText("Search related performers"), "sample");

    const chips = screen.getByRole("toolbar", { name: "Related Performers selected filters" });
    expect(within(chips).getByRole("button", { name: "Edit performer filter: Text search" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Done" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Apply" })).toHaveAttribute("aria-keyshortcuts", "Control+Enter Meta+Enter");

    await user.click(screen.getByRole("button", { name: "Apply" }));
    expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument();
    expect(onApply).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();

    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Enter", ctrlKey: true });
    expect(onApply).toHaveBeenCalledWith({ performerFilterCriterion: { findFilter: { q: "sample" } } });
    expect(onClose).toHaveBeenCalled();
  });

  it("returns focus to related filter search after removing the final local chip", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ performerFilterCriterion: { findFilter: { q: "sample" } } }}
        onApply={vi.fn()}
      />,
    );

    await waitFor(() => expect(screen.getByLabelText("Search performer filter criteria")).toHaveFocus());
    const chips = screen.getByRole("toolbar", { name: "Related Performers selected filters" });
    await user.click(within(chips).getByRole("button", { name: "Remove performer filter: Text search" }));

    await waitFor(() => expect(screen.queryByRole("toolbar", { name: "Related Performers selected filters" })).not.toBeInTheDocument());
    expect(screen.getByLabelText("Search performer filter criteria")).toHaveFocus();
  });

  it("focuses criteria search when entering a related workspace", async () => {
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} />,
    );

    fireEvent.click(screen.getByText("Related Performers"));

    await waitFor(() => expect(screen.getByLabelText("Search performer filter criteria")).toHaveFocus());
  });

  it("navigates related filter criteria with vertical arrow keys", () => {
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} />,
    );

    fireEvent.click(screen.getByText("Related Performers"));
    const search = screen.getByLabelText("Search performer filter criteria");
    search.focus();
    fireEvent.keyDown(search, { key: "ArrowDown" });
    expect(screen.getByRole("tab", { name: "Text search" })).toHaveFocus();

    fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    expect(screen.getByRole("tab", { name: "Age (now)" })).toHaveFocus();
    fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    expect(screen.getByRole("tab", { name: "Age (then)" })).toHaveFocus();
    fireEvent.keyDown(document.activeElement!, { key: "ArrowUp" });
    expect(screen.getByRole("tab", { name: "Age (now)" })).toHaveFocus();

    fireEvent.keyDown(document.activeElement!, { key: "End" });
    expect(screen.getByRole("tab", { name: "Weight" })).toHaveFocus();
    fireEvent.keyDown(document.activeElement!, { key: "Home" });
    expect(screen.getByRole("tab", { name: "Text search" })).toHaveFocus();
    fireEvent.keyDown(document.activeElement!, { key: "ArrowUp" });
    expect(search).toHaveFocus();
  });

  it("opens at the root when the first active filter is a related filter", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          performerFilterCriterion: {
            mode: "every",
            conditionOperator: "or",
            ageAtHostDateCriterion: { modifier: "BETWEEN", value: 18, value2: 20 },
            objectFilter: { favoriteCriterion: { value: true } },
          },
        }}
        onApply={vi.fn()}
        openAtRoot
      />,
    );

    expect(screen.getByRole("dialog", { name: "Filters" })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Related Performers filters" })).toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: "Filters / Related Performers" })).not.toBeInTheDocument();
  });

  it("returns focus to related criteria search after leaving a mobile-style editor", async () => {
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} />,
    );

    fireEvent.click(screen.getByText("Related Performers"));
    fireEvent.click(screen.getByRole("tab", { name: "Favorite" }));
    fireEvent.click(screen.getByRole("button", { name: "Back to related filter criteria" }));

    await waitFor(() => expect(screen.getByLabelText("Search performer filter criteria")).toHaveFocus());
  });

  it("opens the related-existence chip in a dedicated editor", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ performerFilterCriterion: { _matchAll: true } }}
        onApply={vi.fn()}
        preselectCriterion={{ criterionId: "relatedPerformers", relatedFacet: "existence" }}
      />,
    );

    const matchAny = screen.getByRole("button", { name: "Match any related performer" });
    expect(screen.getByRole("tabpanel", { name: "Any performer" })).toContainElement(matchAny);
    expect(matchAny).toHaveAttribute("aria-pressed", "true");
    await waitFor(() => expect(matchAny).toHaveFocus());
  });

  it("builds a performer filter from related five-star videos", async () => {
    writeStoredRatingOptionsOverride({ type: "stars", starPrecision: "full" });
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={PERFORMER_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Related Videos"));
    expect(screen.getByLabelText("Videos").querySelector(".lucide-film")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Rating" }));
    fireEvent.click(screen.getByRole("button", { name: "Set rating to 5" }));
    fireEvent.click(screen.getByRole("button", { name: "Back to filters" }));
    const group = screen.getByRole("group", { name: "Related Videos filters" });
    expect(group.querySelectorAll(".lucide-film")).toHaveLength(1);
    expect(within(group).getByRole("button", { name: "Edit video filter: Rating" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      videoFilterCriterion: {
        objectFilter: { ratingCriterion: { value: 100, modifier: "EQUALS" } },
      },
    });
  });

  it.each([
    ["every", "Every performer matches"],
    ["none", "No performer matches"],
  ] as const)("stores the %s related-performer mode from the relationship dropdown", (mode, label) => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Related Performers"));
    const relationshipMode = screen.getByRole("combobox", { name: "Relationship match mode" });
    expect(relationshipMode).toHaveValue("atLeastOne");
    fireEvent.change(relationshipMode, { target: { value: mode } });
    expect(screen.getByRole("option", { name: label })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Favorite" }));
    fireEvent.click(screen.getByRole("button", { name: "True" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      performerFilterCriterion: {
        mode,
        objectFilter: { favoriteCriterion: { value: true } },
      },
    });
  });

  it("preserves the relationship mode when matching any related performer", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ performerFilterCriterion: { _matchAll: true } }}
        onApply={onApply}
        preselectCriterion={{ criterionId: "relatedPerformers", relatedFacet: "existence" }}
      />,
    );

    fireEvent.change(screen.getByRole("combobox", { name: "Relationship match mode" }), { target: { value: "every" } });
    fireEvent.click(screen.getByRole("button", { name: "Match any related performer" }));
    fireEvent.click(screen.getByRole("button", { name: "Match any related performer" }));
    expect(screen.getByRole("combobox", { name: "Relationship match mode" })).toHaveValue("every");
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ performerFilterCriterion: { mode: "every", _matchAll: true } });
  });

  it("matches any related-performer condition within the selected relationship mode", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Related Performers"));
    fireEvent.change(screen.getByRole("combobox", { name: "Relationship match mode" }), { target: { value: "every" } });
    fireEvent.click(screen.getByRole("tab", { name: "Age (then)" }));
    fireEvent.click(screen.getByRole("button", { name: "Between" }));
    const values = screen.getAllByRole("spinbutton");
    fireEvent.change(values[0], { target: { value: "18" } });
    fireEvent.change(values[1], { target: { value: "20" } });
    fireEvent.click(screen.getByRole("tab", { name: "Favorite" }));
    fireEvent.click(screen.getByRole("button", { name: "True" }));
    fireEvent.change(screen.getByRole("combobox", { name: "Related condition operator" }), { target: { value: "or" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      performerFilterCriterion: {
        mode: "every",
        conditionOperator: "or",
        ageAtHostDateCriterion: { modifier: "BETWEEN", value: 18, value2: 20 },
        objectFilter: { favoriteCriterion: { value: true } },
      },
    });
  });

  it("builds a performer filter from related favorite videos", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={PERFORMER_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Related Videos"));
    fireEvent.click(screen.getByRole("tab", { name: "Favorite" }));
    fireEvent.click(screen.getByRole("button", { name: "True" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      videoFilterCriterion: {
        objectFilter: { favoriteCriterion: { value: true } },
      },
    });
  });

  it("keeps multiple related conditions together while moving between the workspace and parent filters", () => {
    writeStoredRatingOptionsOverride({ type: "stars", starPrecision: "full" });
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Related Performers"));
    expect(screen.getByText("Filters / Related Performers")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Favorite" }));
    fireEvent.click(screen.getByRole("button", { name: "True" }));
    fireEvent.click(screen.getByRole("tab", { name: "Rating" }));
    fireEvent.click(screen.getByRole("button", { name: "Set rating to 5" }));

    fireEvent.click(screen.getByRole("button", { name: "Back to filters" }));
    fireEvent.click(screen.getByRole("tab", { name: /Related Performers/ }));
    expect(screen.getByRole("tab", { name: "Favorite" })).toHaveAttribute("data-active", "true");
    expect(screen.getByRole("tab", { name: "Rating" })).toHaveAttribute("data-active", "true");
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      performerFilterCriterion: {
        objectFilter: {
          favoriteCriterion: { value: true },
          ratingCriterion: { value: 100, modifier: "EQUALS" },
        },
      },
    });
  });

  it("renders related parameters as one icon-prefixed group and removes only the chosen condition", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          performerFilterCriterion: {
            findFilter: { q: "example" },
            objectFilter: {
              favoriteCriterion: { value: true },
              ratingCriterion: { value: 100, modifier: "EQUALS" },
            },
          },
        }}
        onApply={onApply}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Back to filters" }));
    await waitFor(() => expect(screen.getByLabelText("Search filter criteria")).toHaveFocus());
    const group = screen.getByRole("group", { name: "Related Performers filters" });
    expect(group).toHaveTextContent("At least one performer matching all");
    expect(group.querySelectorAll(".lucide-users")).toHaveLength(3);
    const editFavorite = within(group).getByRole("button", { name: "Edit performer filter: Favorite" });
    const removeFavorite = within(group).getByRole("button", { name: "Remove performer filter: Favorite" });
    expect(editFavorite).toHaveAttribute("tabindex", "-1");
    expect(removeFavorite).toHaveAttribute("tabindex", "-1");
    expect(within(group).getByRole("button", { name: "Edit performer filter: Rating" })).toBeInTheDocument();
    removeFavorite.focus();
    await user.keyboard("{Enter}");
    await waitFor(() => expect(within(group).getByRole("button", { name: "Edit filter: Related Performers" })).toHaveFocus());
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      performerFilterCriterion: {
        findFilter: { q: "example" },
        objectFilter: { ratingCriterion: { value: 100, modifier: "EQUALS" } },
      },
    });
  });

  it("shows unsupported saved-filter fields as removable read-only aggregates", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          performerFilterCriterion: {
            objectFilter: {
              customFieldCriteria: [
                { key: "example", type: "text", modifier: "EQUALS", value: "one" },
                { key: "example-two", type: "text", modifier: "EQUALS", value: "two" },
              ],
            },
          },
        }}
        onApply={onApply}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Back to filters" }));
    await waitFor(() => expect(screen.getByLabelText("Search filter criteria")).toHaveFocus());
    const group = screen.getByRole("group", { name: "Related Performers filters" });
    expect(group).toHaveTextContent("Custom field conditions:2 conditions");
    expect(within(group).queryByRole("button", { name: "Edit performer filter: Custom field conditions" })).not.toBeInTheDocument();
    const removeAggregate = within(group).getByRole("button", { name: "Remove performer filter: Custom field conditions" });
    removeAggregate.focus();
    await user.keyboard("{Enter}");
    await waitFor(() => expect(screen.queryByRole("group", { name: "Related Performers filters" })).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getByLabelText("Search filter criteria")).toHaveFocus());
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({});
  });

  it("keeps focus in the visible editor when removing the last related condition", async () => {
    const user = userEvent.setup();
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      value: vi.fn((query: string) => ({
        matches: query === "(max-width: 767px)",
        media: query,
        onchange: null,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    });
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          performerFilterCriterion: {
            objectFilter: { favoriteCriterion: { value: true } },
          },
        }}
        onApply={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Back to filters" }));
    await waitFor(() => expect(screen.getByLabelText("Search filter criteria")).toHaveFocus());
    fireEvent.click(screen.getByRole("tab", { name: "Favorite" }));
    const editor = screen.getByRole("tabpanel", { name: "Favorite" });
    const trueButton = within(editor).getByRole("button", { name: "True" });
    await waitFor(() => expect(trueButton).toHaveFocus());

    const group = screen.getByRole("group", { name: "Related Performers filters" });
    const removeFavorite = within(group).getByRole("button", { name: "Remove performer filter: Favorite" });
    removeFavorite.focus();
    await user.keyboard("{Enter}");

    await waitFor(() => expect(screen.queryByRole("group", { name: "Related Performers filters" })).not.toBeInTheDocument());
    expect(trueButton).toHaveFocus();
  });

  it.each([
    [true, false],
    [false, true],
  ])("migrates a legacy performer-favorite value of %s without losing its semantics", (legacyValue, exclude) => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ performerFavoriteCriterion: { value: legacyValue } }}
        onApply={onApply}
      />,
    );

    expect(screen.getByRole("dialog", { name: "Filters / Related Performers" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Relationship match mode" })).toHaveValue(exclude ? "none" : "atLeastOne");
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      performerFilterCriterion: {
        objectFilter: { favoriteCriterion: { value: true } },
        ...(exclude ? { exclude: true } : {}),
      },
    });
  });

  it("browses configured folders and applies a folder-aware path criterion", async () => {
    libraryFolders.mockImplementation((path?: string) => Promise.resolve(path
      ? [{ name: "Nested", path: "/library/Root/Nested", hasChildren: false }]
      : [{ name: "/library/Root", path: "/library/Root", hasChildren: true }]));
    const onApply = vi.fn();

    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} preselectCriterion="path" />,
    );

    expect(await screen.findByRole("radio", { name: "/library/Root" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Expand folder /library/Root" }));
    await userEvent.click(await screen.findByRole("radio", { name: "Nested" }));
    await userEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(libraryFolders).toHaveBeenNthCalledWith(1, undefined, false);
    expect(libraryFolders).toHaveBeenNthCalledWith(2, "/library/Root", false);
    expect(onApply).toHaveBeenCalledWith({
      pathCriterion: { value: "/library/Root/Nested", modifier: "UNDER_PATH" },
    });
  });

  it("shows loading instead of a cached folder error while a recovery request is in progress", async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const childQueryKey = ["library-folders", "/library/Root", false];
    client.setQueryData(childQueryKey, [{ name: "Stale", path: "/library/Root/Stale", hasChildren: false }]);
    const cachedQuery = client.getQueryCache().find({ queryKey: childQueryKey });
    cachedQuery?.setState({ ...cachedQuery.state, status: "error", error: new Error("temporary failure") });

    let resolveChildren!: (value: Array<{ name: string; path: string; hasChildren: boolean }>) => void;
    const childrenRequest = new Promise<Array<{ name: string; path: string; hasChildren: boolean }>>((resolve) => {
      resolveChildren = resolve;
    });
    libraryFolders.mockImplementation((path?: string) => path
      ? childrenRequest
      : Promise.resolve([{ name: "/library/Root", path: "/library/Root", hasChildren: true }]));

    render(
      <QueryClientProvider client={client}>
        <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} preselectCriterion="path" />
      </QueryClientProvider>,
    );

    await userEvent.click(await screen.findByRole("button", { name: "Expand folder /library/Root" }));

    expect(screen.getByText("Loading…")).toBeInTheDocument();
    expect(screen.queryByText("Unable to list subfolders")).not.toBeInTheDocument();
    resolveChildren([{ name: "Nested", path: "/library/Root/Nested", hasChildren: false }]);
    expect(await screen.findByRole("radio", { name: "Nested" })).toBeInTheDocument();
  });

  it("shows loading instead of a cached root-folder error while recovery is in progress", async () => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const rootQueryKey = ["library-folders", "roots", false];
    client.setQueryData(rootQueryKey, [{ name: "Stale", path: "/library/Stale", hasChildren: false }]);
    const cachedQuery = client.getQueryCache().find({ queryKey: rootQueryKey });
    cachedQuery?.setState({ ...cachedQuery.state, status: "error", error: new Error("temporary failure") });

    let resolveRoots!: (value: Array<{ name: string; path: string; hasChildren: boolean }>) => void;
    libraryFolders.mockReturnValue(new Promise<Array<{ name: string; path: string; hasChildren: boolean }>>((resolve) => {
      resolveRoots = resolve;
    }));

    render(
      <QueryClientProvider client={client}>
        <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} preselectCriterion="path" />
      </QueryClientProvider>,
    );

    expect(screen.getByText("Loading library folders…")).toBeInTheDocument();
    expect(screen.queryByText("Folder browsing is unavailable. You can still enter a path manually.")).not.toBeInTheDocument();
    resolveRoots([{ name: "/library/Root", path: "/library/Root", hasChildren: false }]);
    expect(await screen.findByRole("radio", { name: "/library/Root" })).toBeInTheDocument();
  });

  afterEach(() => {
    if (scrollIntoViewDescriptor) Object.defineProperty(Element.prototype, "scrollIntoView", scrollIntoViewDescriptor);
    else Reflect.deleteProperty(Element.prototype, "scrollIntoView");
    if (matchMediaDescriptor) Object.defineProperty(window, "matchMedia", matchMediaDescriptor);
    else Reflect.deleteProperty(window, "matchMedia");
  });

  it("keeps the toolbar trigger at the compact toolbar scale", () => {
    render(<FilterButton activeCount={2} onClick={vi.fn()} />);

    const trigger = screen.getByRole("button", { name: "Filters, 2 active" });
    expect(trigger).toHaveClass("px-2", "py-1", "text-xs");
    expect(trigger).not.toHaveClass("min-h-11");
  });

  const metadataServiceModifiers: CriterionModifier[] = [
    "EQUALS",
    "NOT_EQUALS",
    "INCLUDES",
    "EXCLUDES",
    "MATCHES_REGEX",
    "NOT_MATCHES_REGEX",
    "IS_NULL",
    "NOT_NULL",
  ];

  it("renders configured metadata service names with endpoint fallback labels", () => {
    render(
      <RemoteIdFilterEditor
        onChange={vi.fn()}
        modifiers={metadataServiceModifiers}
        metadataServers={[
          { endpoint: "https://named.example/graphql", name: "Named Service", apiKey: "", maxRequestsPerMinute: 0 },
          { endpoint: "https://fallback.example/graphql", name: "", apiKey: "", maxRequestsPerMinute: 0 },
        ]}
      />,
    );

    expect(screen.getByRole("option", { name: "Named Service" })).toHaveValue("https://named.example/graphql");
    expect(screen.getByRole("option", { name: "https://fallback.example/graphql" })).toHaveValue("https://fallback.example/graphql");
  });

  it("shows an explicit no-services state", () => {
    render(<RemoteIdFilterEditor onChange={vi.fn()} modifiers={metadataServiceModifiers} metadataServers={[]} />);

    expect(screen.getByRole("combobox", { name: "Metadata Service" })).toBeEnabled();
    expect(screen.getByRole("option", { name: "Any metadata service" })).toBeInTheDocument();
  });

  it("keeps a legacy unconfigured endpoint selected", () => {
    render(
      <RemoteIdFilterEditor
        value={{ value: "", endpoint: "https://legacy.example/graphql", modifier: "NOT_NULL" }}
        onChange={vi.fn()}
        modifiers={metadataServiceModifiers}
        metadataServers={[]}
      />,
    );

    expect(screen.getByRole("combobox", { name: "Metadata Service" })).toHaveValue("https://legacy.example/graphql");
    expect(screen.getByRole("option", { name: "https://legacy.example/graphql (unconfigured)" })).toBeInTheDocument();
  });

  it("uses the configured label for a saved endpoint with different casing", () => {
    render(
      <RemoteIdFilterEditor
        value={{ value: "remote-123", endpoint: "HTTPS://SERVICE.EXAMPLE/GRAPHQL", modifier: "EQUALS" }}
        onChange={vi.fn()}
        modifiers={metadataServiceModifiers}
        metadataServers={[
          { endpoint: "https://service.example/graphql", name: "Configured Service", apiKey: "", maxRequestsPerMinute: 0 },
        ]}
      />,
    );

    expect(screen.getByRole("combobox", { name: "Metadata Service" })).toHaveValue("HTTPS://SERVICE.EXAMPLE/GRAPHQL");
    expect(screen.getByRole("option", { name: "Configured Service" })).toHaveValue("HTTPS://SERVICE.EXAMPLE/GRAPHQL");
    expect(screen.queryByText(/unconfigured/i)).not.toBeInTheDocument();
  });

  it("emits a paired endpoint and value payload", () => {
    const onApply = vi.fn();
    render(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Remote ID"));
    fireEvent.change(screen.getByLabelText("Remote ID value"), { target: { value: "remote-123" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      remoteIdValueCriterion: { value: "remote-123", modifier: "EQUALS" },
    });
  });

  it("applies the video segment-presence criterion", () => {
    const onApply = vi.fn();
    render(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    fireEvent.click(screen.getByText("Has Segments"));
    fireEvent.click(screen.getByRole("button", { name: "True" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      hasSegmentsCriterion: { value: true },
    });
  });

  it("selects the announced whole-star rating from the keyboard", () => {
    writeStoredRatingOptionsOverride({ type: "stars", starPrecision: "half" });
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} preselectCriterion="rating" />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Set rating to 3" }), { detail: 0 });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ ratingCriterion: { value: 60, modifier: "EQUALS" } });
  });

  it("keeps filter pin controls in the criteria list and persists pinned criteria", async () => {
    const criteria: CriterionDefinition[] = [
      { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
    ];
    render(<FilterDialog open onClose={vi.fn()} criteria={criteria} activeFilter={{}} onApply={vi.fn()} />);
    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());

    const titleTab = screen.getByRole("tab", { name: "Title" });
    const pinButton = screen.getByRole("button", { name: "Pin Title" });
    expect(titleTab.parentElement).toContainElement(pinButton);
    expect(screen.queryByText("Configure this filter")).not.toBeInTheDocument();
    expect(screen.queryByText("Active filter")).not.toBeInTheDocument();
    expect(pinButton.querySelector(".lucide-pin")).toBeInTheDocument();
    expect(pinButton.querySelector(".lucide-pin-off")).not.toBeInTheDocument();

    fireEvent.click(pinButton);

    const unpinButton = screen.getByRole("button", { name: "Unpin Title" });
    expect(unpinButton).toHaveClass("text-muted", "hover:text-foreground");
    expect(unpinButton).not.toHaveClass("text-accent");
    expect(unpinButton.querySelector(".lucide-pin")).toHaveClass("group-hover:hidden", "group-focus-visible:hidden");
    expect(unpinButton.querySelector(".lucide-pin-off")).toHaveClass("hidden", "group-hover:block", "group-focus-visible:block");
    expect(localStorage.getItem("filter-pinned")).toBe(JSON.stringify(["title"]));
    expect(screen.queryByRole("tabpanel", { name: "Title" })).not.toBeInTheDocument();
  });

  it("keeps pin controls out of the tab order while allowing arrow-key access from the selected criterion", async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const criteria: CriterionDefinition[] = [
      { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
      { id: "rating", label: "Rating", type: "number", filterKey: "ratingCriterion" },
    ];
    render(<FilterDialog open onClose={onClose} criteria={criteria} activeFilter={{}} onApply={vi.fn()} />);
    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());

    expect(screen.getByRole("button", { name: "Pin Title" })).toHaveAttribute("tabindex", "-1");
    expect(screen.getByRole("button", { name: "Pin Rating" })).toHaveAttribute("tabindex", "-1");
    await user.click(screen.getByRole("tab", { name: "Title" }));

    const titleTab = screen.getByRole("tab", { name: "Title" });
    const pinButton = screen.getByRole("button", { name: "Pin Title" });
    expect(titleTab.parentElement).toHaveClass("border-accent", "bg-accent/15");
    expect(titleTab).toHaveClass("focus-visible:outline-none", "focus-visible:ring-accent");
    expect(pinButton).toHaveAttribute("tabindex", "-1");
    expect(screen.getByRole("button", { name: "Pin Rating" })).toHaveAttribute("tabindex", "-1");
    expect(pinButton).toHaveClass("md:opacity-0", "md:hover:opacity-100", "md:focus-visible:opacity-100", "border-accent/40");
    expect(pinButton).not.toHaveClass("md:group-hover:opacity-100");
    expect(pinButton).not.toHaveClass("md:group-focus-within:opacity-100");

    await waitFor(() => expect(screen.getByRole("button", { name: "=", pressed: true })).toHaveFocus());
    await user.keyboard("{Shift>}{Tab}{/Shift}");
    expect(titleTab).toHaveFocus();
    await user.keyboard("{ArrowRight}");
    expect(pinButton).toHaveFocus();
    await user.keyboard("{Escape}");
    expect(titleTab).toHaveFocus();
    expect(onClose).not.toHaveBeenCalled();

    await user.keyboard("{ArrowRight}");
    await user.keyboard(" ");
    await waitFor(() => expect(screen.getByRole("tab", { name: "Title" })).toHaveFocus());
    expect(screen.getByRole("button", { name: "Unpin Title" })).toHaveAttribute("tabindex", "-1");
  });

  it("moves sideways through string match parameters with arrow keys", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} preselectCriterion="details" />,
    );

    const equals = screen.getByRole("button", { name: "=", pressed: true });
    await waitFor(() => expect(equals).toHaveFocus());

    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("button", { name: "≠", pressed: true })).toHaveFocus();

    await user.keyboard("{ArrowLeft}");
    expect(screen.getByRole("button", { name: "=", pressed: true })).toHaveFocus();

    await user.keyboard("{ArrowLeft}");
    expect(screen.getByRole("button", { name: "Not Null", pressed: true })).toHaveFocus();
  });

  it("shows active draft values as removable chips inside the dialog", () => {
    const onApply = vi.fn();
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          titleCriterion: { value: "example", modifier: "EQUALS" },
          organizedCriterion: { value: true },
        }}
        onApply={onApply}
      />,
    );
    const selectedFilters = screen.getByRole("toolbar", { name: "Selected filters" });
    expect(selectedFilters).toHaveClass("max-h-[min(12rem,35dvh)]", "overflow-y-auto");
    expect(selectedFilters).toHaveTextContent("Title:= example");
    expect(selectedFilters).toHaveTextContent("Organized:Yes");
    expect(screen.queryByRole("button", { name: "Clear criterion" })).not.toBeInTheDocument();
    expect(screen.getAllByRole("tab").filter((tab) => tab.querySelector(".lucide-check"))).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Title" }));

    expect(screen.queryByRole("button", { name: "Edit filter: Title" })).not.toBeInTheDocument();
    expect(screen.getByRole("tabpanel", { name: "Title" })).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "Value" })).toHaveValue("");

    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({ organizedCriterion: { value: true } });
  });

  it("opens a draft criterion editor from an in-dialog filter chip", () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          titleCriterion: { value: "example", modifier: "EQUALS" },
          organizedCriterion: { value: false },
        }}
        onApply={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Edit filter: Organized" }));

    expect(screen.getByRole("tabpanel", { name: "Organized" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "False", pressed: true })).toBeInTheDocument();
  });

  it("uses one roving tab stop for active filter groups and preserves focus after removal", async () => {
    const user = userEvent.setup();
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          titleCriterion: { value: "example", modifier: "EQUALS" },
          organizedCriterion: { value: true },
        }}
        onApply={vi.fn()}
      />,
    );
    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());

    const title = screen.getByRole("button", { name: "Edit filter: Title" });
    const organized = screen.getByRole("button", { name: "Edit filter: Organized" });
    expect(screen.getByRole("toolbar", { name: "Selected filters" })).toHaveClass("[&_button:focus-visible]:bg-accent/25", "[&_button:focus-visible]:ring-inset");
    expect(title).toHaveAttribute("tabindex", "0");
    expect(organized).toHaveAttribute("tabindex", "-1");
    expect(screen.getByRole("button", { name: "Remove filter: Title" })).toHaveAttribute("tabindex", "-1");

    title.focus();
    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("button", { name: "Remove filter: Title" })).toHaveFocus();
    await user.keyboard("{ArrowRight}");
    expect(organized).toHaveFocus();
    await user.keyboard("{Delete}");

    expect(screen.queryByRole("button", { name: "Edit filter: Organized" })).not.toBeInTheDocument();
    expect(title).toHaveFocus();

    await user.keyboard("{Delete}");
    await waitFor(() => expect(screen.getByRole("tab", { name: "Title" })).toHaveFocus());
    expect(screen.queryByRole("toolbar", { name: "Selected filters" })).not.toBeInTheDocument();
  });

  it("uses arrow keys to navigate every part of a related filter chip", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ performerFilterCriterion: { findFilter: { q: "sample" } } }}
        onApply={vi.fn()}
        openAtRoot
      />,
    );

    const toolbar = screen.getByRole("toolbar", { name: "Selected filters" });
    const editGroup = within(toolbar).getByRole("button", { name: "Edit filter: Related Performers" });
    const removeGroup = within(toolbar).getByRole("button", { name: "Remove filter: Related Performers" });
    const editSearch = within(toolbar).getByRole("button", { name: "Edit performer filter: Text search" });
    const removeSearch = within(toolbar).getByRole("button", { name: "Remove performer filter: Text search" });
    expect(within(toolbar).getAllByRole("button").filter((button) => button.tabIndex === 0)).toEqual([editGroup]);
    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());

    editGroup.focus();
    await user.keyboard("{ArrowRight}");
    expect(removeGroup).toHaveFocus();
    await user.keyboard("{ArrowRight}");
    expect(editSearch).toHaveFocus();
    await user.keyboard("{ArrowRight}");
    expect(removeSearch).toHaveFocus();
    expect(within(toolbar).getAllByRole("button").filter((button) => button.tabIndex === 0)).toEqual([removeSearch]);

    const criterionSearch = screen.getByRole("searchbox", { name: "Search filter criteria" });
    criterionSearch.focus();
    await user.type(criterionSearch, "title");
    expect(within(toolbar).getAllByRole("button").filter((button) => button.tabIndex === 0)).toEqual([removeSearch]);

    removeSearch.focus();
    await user.keyboard("{ArrowLeft}");
    expect(editSearch).toHaveFocus();

    await user.keyboard("{ArrowDown}");
    expect(criterionSearch).toHaveFocus();
    await user.keyboard("{ArrowUp}");
    expect(editSearch).toHaveFocus();
  });

  it("places Clear all after selected filters and returns focus to criterion search", async () => {
    const user = userEvent.setup();
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          titleCriterion: { value: "example", modifier: "EQUALS" },
          organizedCriterion: { value: true },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );
    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());
    const selectedFilters = screen.getByRole("toolbar", { name: "Selected filters" });
    const clearAll = within(selectedFilters).getByRole("button", { name: "Clear all" });
    const combineFilters = screen.getByRole("button", { name: "Combine Filters" });
    expect(clearAll).toHaveAttribute("tabindex", "-1");
    expect(screen.getAllByRole("button", { name: /Clear all/i })).toHaveLength(1);

    const organized = screen.getByRole("button", { name: "Edit filter: Organized" });
    organized.focus();
    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("button", { name: "Remove filter: Organized" })).toHaveFocus();
    await user.keyboard("{ArrowRight}");
    expect(clearAll).toHaveFocus();
    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("button", { name: "Edit filter: Title" })).toHaveFocus();
    expect(combineFilters).not.toHaveFocus();

    await user.click(clearAll);
    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());
    expect(screen.queryByRole("toolbar", { name: "Selected filters" })).not.toBeInTheDocument();
    expect(screen.queryByRole("tabpanel")).not.toBeInTheDocument();
  });

  it("formats rating filter chips with the preferred star presentation", async () => {
    writeStoredRatingOptionsOverride({ type: "stars", starPrecision: "full" });
    renderWithQueryClient(
      <AppConfigProvider><FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ ratingCriterion: { value: 100, modifier: "LESS_THAN" } }}
        onApply={vi.fn()}
      /></AppConfigProvider>,
    );
    const chip = screen.getByRole("button", { name: "Edit filter: Rating" });
    expect(chip).toHaveAttribute("title", "Rating: < 5 stars");
    expect(chip).toHaveTextContent("Rating:<");
    expect(chip.querySelectorAll("[data-rating-stars]")).toHaveLength(1);
  });

  it("formats rating filter ranges with the preferred decimal presentation", async () => {
    writeStoredRatingOptionsOverride({ type: "decimal", starPrecision: "full" });
    renderWithQueryClient(
      <AppConfigProvider><FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ ratingCriterion: { value: 40, value2: 100, modifier: "BETWEEN" } }}
        onApply={vi.fn()}
      /></AppConfigProvider>,
    );
    const chip = screen.getByRole("button", { name: "Edit filter: Rating" });
    expect(chip).toHaveAttribute("title", "Rating: Between 4.0 and 10.0");
    expect(chip).toHaveTextContent("Between 4.0 and 10.0");
    expect(chip.querySelector("[data-rating-stars]")).not.toBeInTheDocument();
  });

  it("preserves filter-group focus when removing the last entity-backed filter", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          tagsCriterion: { value: [1], modifier: "INCLUDES_ALL", _names: { "1": "Selected tag" } },
          titleCriterion: { value: "example", modifier: "EQUALS" },
        }}
        onApply={vi.fn()}
      />,
    );
    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());

    const tags = screen.getByRole("button", { name: "Edit filter: Tags" });
    tags.focus();
    await user.keyboard("{Delete}");

    await waitFor(() => expect(screen.getByRole("button", { name: "Edit filter: Title" })).toHaveFocus());
  });

  it("returns focus to criterion search when the removed filter row is hidden", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ tagsCriterion: { value: [1], modifier: "INCLUDES_ALL", _names: { "1": "Selected tag" } } }}
        onApply={vi.fn()}
        preselectCriterion="tags"
      />,
    );
    const search = screen.getByRole("searchbox", { name: "Search filter criteria" });
    await waitFor(() => expect(screen.getByRole("combobox", { name: "Search tags" })).toHaveFocus());
    await user.type(search, "Title");
    const tags = screen.getByRole("button", { name: "Edit filter: Tags" });
    tags.focus();
    await user.keyboard("{Delete}");

    await waitFor(() => expect(search).toHaveFocus());
    expect(screen.queryByRole("toolbar", { name: "Selected filters" })).not.toBeInTheDocument();
  });

  it("clears criterion search when opening an editor from a draft filter chip", () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ titleCriterion: { value: "example", modifier: "EQUALS" } }}
        onApply={vi.fn()}
      />,
    );

    const searchInput = screen.getByRole("searchbox", { name: "Search filter criteria" });
    fireEvent.change(searchInput, { target: { value: "rating" } });
    expect(screen.queryByRole("tab", { name: "Title" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Edit filter: Title" }));

    expect(searchInput).toHaveValue("");
    expect(screen.getByRole("tab", { name: "Title", selected: true })).toBeInTheDocument();
    expect(screen.getByRole("tabpanel", { name: "Title" })).toBeInTheDocument();
  });

  it("exposes a modal dialog and supports keyboard criterion selection", async () => {
    const user = userEvent.setup();
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={[
          { id: "title", label: "Title", type: "string", filterKey: "titleCriterion" },
          { id: "rating", label: "Rating", type: "number", filterKey: "ratingCriterion" },
        ]}
        activeFilter={{}}
        onApply={vi.fn()}
      />,
    );

    expect(screen.getByRole("dialog", { name: "Filters" })).toHaveAttribute("aria-modal", "true");
    const search = screen.getByRole("searchbox", { name: "Search filter criteria" });
    await waitFor(() => expect(search).toHaveFocus());

    await user.keyboard("{ArrowDown}");
    expect(screen.getAllByRole("tab")[0]).toHaveFocus();
    await user.keyboard("{ArrowUp}");
    expect(search).toHaveFocus();

    await user.keyboard("{ArrowDown}{Enter}");
    expect(screen.getByRole("tabpanel", { name: "Rating" })).toBeInTheDocument();
    expect(screen.getByRole("spinbutton", { name: "Value" })).toBeInTheDocument();
  });

  it("prioritizes an exact criterion match and focuses its primary editor control", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} />,
    );

    await user.type(screen.getByRole("searchbox", { name: "Search filter criteria" }), "Tags");
    await user.keyboard("{ArrowDown}{Enter}");

    expect(screen.getByRole("tabpanel", { name: "Tags" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("combobox", { name: "Search tags" })).toHaveFocus());
  });

  it("shows a human-readable duration beside the clock input", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ durationCriterion: { value: 600, modifier: "LESS_THAN" } }}
        onApply={vi.fn()}
      />,
    );

    await user.click(screen.getByRole("tab", { name: /^Duration/ }));
    const input = screen.getByRole("textbox", { name: "Value" });
    expect(input).toHaveValue("10:00");
    expect(screen.getByText("10 min")).toBeInTheDocument();
    expect(input).toHaveAccessibleDescription("10 min");

    await user.clear(input);
    await user.type(input, "1:30");
    expect(screen.getByText("1 min 30 sec")).toBeInTheDocument();
    expect(input).toHaveAccessibleDescription("1 min 30 sec");
  });

  it("selects a multi-ID search result without tabbing through result actions and applies with Ctrl+Enter", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    tagsFind.mockResolvedValue({ items: [
      { id: 1, name: "Blowjob", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
      { id: 2, name: "Double Blowjob", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
      { id: 3, name: "Unselected", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
    ] });
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    await user.type(screen.getByRole("searchbox", { name: "Search filter criteria" }), "Tags");
    await user.keyboard("{ArrowDown}{Enter}");
    const tagSearch = screen.getByRole("combobox", { name: "Search tags" });
    await user.type(tagSearch, "blowjob");
    await waitFor(() => expect(screen.getByRole("option", { name: "Blowjob" })).toBeInTheDocument());
    const result = screen.getByRole("option", { name: "Blowjob" });
    expect(result.children[0]).toBe(screen.getByRole("button", { name: "Include Blowjob" }));
    expect(result.children[1]).toHaveTextContent("Blowjob");
    expect(result.children[2]).toBe(screen.getByRole("button", { name: "Exclude Blowjob" }));

    await user.keyboard("{ArrowDown}{Enter}");
    expect(tagSearch).toHaveFocus();
    expect(tagSearch).toHaveValue("");
    expect(scrollIntoView).toHaveBeenCalledWith({ block: "nearest" });
    expect(screen.getAllByTitle("Include")[0]).toHaveAttribute("tabindex", "-1");
    const results = screen.getByRole("listbox", { name: "tags results" });
    const selectedTag = screen.getByRole("button", { name: "Remove Blowjob" }).parentElement!;
    const matchMode = screen.getByRole("group", { name: "Match mode" });
    expect(matchMode.closest("fieldset")).toBeNull();
    expect(screen.getByText("Match")).toBeInTheDocument();
    expect(matchMode.compareDocumentPosition(results) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(results.compareDocumentPosition(selectedTag) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(Array.from(matchMode.querySelectorAll("button"), (button) => button.textContent)).toEqual([
      "Includes All", "Includes", "None", "Any",
    ]);
    expect(screen.getByRole("button", { name: "Includes All", pressed: true })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Include sub-tags" })).toBeInTheDocument();
    await user.type(tagSearch, "double blowjob");
    await waitFor(() => expect(screen.getByRole("option", { name: "Double Blowjob" })).toBeInTheDocument());
    await user.keyboard("{Shift>}{Enter}{/Shift}");
    expect(tagSearch).toHaveValue("");

    await user.type(tagSearch, "unselected");
    await user.keyboard("{Control>}{Enter}{/Control}");
    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      tagsCriterion: expect.objectContaining({ value: [1], excludes: [2] }),
    }));
  });

  it.each([
    { criterion: "tags", searchName: "Search tags", mockFind: tagsFind },
    { criterion: "performers", searchName: "Search performers", mockFind: performersFind },
    { criterion: "studios", searchName: "Search studios", mockFind: studiosFind },
  ])("places $criterion match modes above entity selection", async ({ criterion, searchName, mockFind }) => {
    mockFind.mockResolvedValue({ items: [] });
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} preselectCriterion={criterion} />,
    );

    const matchMode = screen.getByRole("group", { name: "Match mode" });
    const search = await screen.findByRole("combobox", { name: searchName });
    expect(matchMode.compareDocumentPosition(search) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it.each([
    { criterion: "tags", optionName: "Clickable tag", mockFind: tagsFind, entity: { id: 1, name: "Clickable tag", tagGroupId: null, tagGroupName: null, tagGroupColor: null } },
    { criterion: "performers", optionName: "Clickable performer", mockFind: performersFind, entity: { id: 2, name: "Clickable performer" } },
  ])("includes $criterion by clicking the row while keeping exclude isolated", async ({ criterion, optionName, mockFind, entity }) => {
    const user = userEvent.setup();
    mockFind.mockResolvedValue({ items: [entity] });
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} preselectCriterion={criterion} />,
    );

    const option = await screen.findByRole("option", { name: optionName });
    await user.click(within(option).getByText(optionName));
    expect(screen.getByRole("button", { name: `Remove ${optionName}` })).toBeInTheDocument();

    await user.click(within(option).getByRole("button", { name: `Exclude ${optionName}` }));
    expect(within(screen.getByRole("group", { name: `Excluded ${criterion}` })).getByRole("button", { name: `Remove ${optionName}` })).toBeInTheDocument();
  });

  it.each([
    { criterion: "tags", mockFind: tagsFind, makeEntity: (id: number, name: string) => ({ id, name, tagGroupId: null, tagGroupName: null, tagGroupColor: null }) },
    { criterion: "performers", mockFind: performersFind, makeEntity: (id: number, name: string) => ({ id, name }) },
  ])("clears the $criterion search after row, plus, and minus selection", async ({ criterion, mockFind, makeEntity }) => {
    const user = userEvent.setup();
    mockFind.mockResolvedValue({ items: [makeEntity(1, "Row choice"), makeEntity(2, "Plus choice"), makeEntity(3, "Minus choice")] });
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} preselectCriterion={criterion} />,
    );

    const search = screen.getByRole("combobox", { name: `Search ${criterion}` });
    await user.type(search, "Row choice");
    const rowOption = await screen.findByRole("option", { name: "Row choice" });
    await user.click(within(rowOption).getByText("Row choice"));
    expect(search).toHaveValue("");

    await user.type(search, "Plus choice");
    const plusOption = await screen.findByRole("option", { name: "Plus choice" });
    await user.click(within(plusOption).getByRole("button", { name: "Include Plus choice" }));
    expect(search).toHaveValue("");

    await user.type(search, "Minus choice");
    const minusOption = await screen.findByRole("option", { name: "Minus choice" });
    await user.click(within(minusOption).getByRole("button", { name: "Exclude Minus choice" }));
    expect(search).toHaveValue("");
  });

  it("skips grouped results and uses one roving tab stop for selected values", async () => {
    const user = userEvent.setup();
    tagsFind.mockResolvedValue({ items: [
      { id: 1, name: "First tag", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
      { id: 2, name: "Middle tag", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
      { id: 3, name: "Last tag", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
    ] });
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          tagsCriterion: {
            value: [1, 2, 3],
            modifier: "INCLUDES_ALL",
            _names: { "1": "First tag", "2": "Middle tag", "3": "Last tag" },
          },
        }}
        onApply={vi.fn()}
        preselectCriterion="tags"
      />,
    );

    const first = await screen.findByRole("button", { name: "Remove First tag" });
    await waitFor(() => expect(screen.getByRole("combobox", { name: "Search tags" })).toHaveFocus());
    const middle = screen.getByRole("button", { name: "Remove Middle tag" });
    const last = screen.getByRole("button", { name: "Remove Last tag" });
    expect(screen.queryByRole("button", { name: /Ungrouped/ })).not.toBeInTheDocument();
    expect(screen.getByText("Ungrouped")).toBeInTheDocument();
    expect(screen.getByRole("listbox", { name: "tags results" })).toHaveAttribute("tabindex", "-1");
    expect(first).toHaveAttribute("tabindex", "0");
    expect(middle).toHaveAttribute("tabindex", "-1");
    expect(last).toHaveAttribute("tabindex", "-1");

    const search = screen.getByRole("combobox", { name: "Search tags" });
    await user.tab();
    expect(first).toHaveFocus();
    await user.keyboard("{ArrowRight}");
    expect(middle).toHaveFocus();
    await user.keyboard("{Delete}");

    expect(screen.queryByRole("button", { name: "Remove Middle tag" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Last tag" })).toHaveFocus();
    expect(screen.getByText("Removed Middle tag. 2 selected.", { selector: "[role='status']" })).toBeInTheDocument();

    await user.keyboard("{Delete}");
    expect(screen.getByRole("button", { name: "Remove First tag" })).toHaveFocus();
    expect(screen.queryByRole("button", { name: "Remove Last tag" })).not.toBeInTheDocument();
    await user.keyboard("{Delete}");
    expect(search).toHaveFocus();
    expect(screen.queryByRole("button", { name: "Remove First tag" })).not.toBeInTheDocument();
  });

  it("navigates tag results in their rendered group order", async () => {
    const user = userEvent.setup();
    Element.prototype.scrollIntoView = vi.fn();
    tagsFind.mockResolvedValue({ items: [
      { id: 1, name: "Alpha ungrouped", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
      { id: 2, name: "Beta grouped", tagGroupId: 10, tagGroupName: "Featured", tagGroupColor: null },
    ] });
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} preselectCriterion="tags" />,
    );

    const search = screen.getByRole("combobox", { name: "Search tags" });
    await waitFor(() => expect(screen.getByRole("option", { name: "Beta grouped" })).toBeInTheDocument());
    await user.keyboard("{ArrowDown}");
    expect(search).toHaveAttribute("aria-activedescendant", "multi-id-result-tags-2");
    await user.keyboard("{Enter}");
    expect(screen.getByRole("button", { name: "Remove Beta grouped" })).toBeInTheDocument();
  });

  it("keeps tag results visually stable while a typed search is loading", async () => {
    const user = userEvent.setup();
    let resolveSearch!: (value: { items: Array<{ id: number; name: string; tagGroupId: null; tagGroupName: null; tagGroupColor: null }> }) => void;
    const searchRequest = new Promise<{ items: Array<{ id: number; name: string; tagGroupId: null; tagGroupName: null; tagGroupColor: null }> }>((resolve) => {
      resolveSearch = resolve;
    });
    tagsFind.mockImplementation(({ q }: { q?: string }) => q
      ? searchRequest
      : Promise.resolve({ items: [{ id: 1, name: "Initial tag", tagGroupId: null, tagGroupName: null, tagGroupColor: null }] }));
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} preselectCriterion="tags" />,
    );

    await screen.findByRole("option", { name: "Initial tag" });
    await user.type(screen.getByRole("combobox", { name: "Search tags" }), "new");

    await waitFor(() => expect(screen.getByRole("listbox", { name: "tags results" })).toHaveAttribute("aria-busy", "true"));
    expect(screen.getByRole("option", { name: "Initial tag" })).not.toHaveClass("opacity-50");
    const search = screen.getByRole("combobox", { name: "Search tags" });
    await user.keyboard("{ArrowDown}");
    expect(search).not.toHaveAttribute("aria-activedescendant");
    await user.keyboard("{Enter}");
    expect(screen.queryByRole("button", { name: "Remove Initial tag" })).not.toBeInTheDocument();

    resolveSearch({ items: [{ id: 2, name: "New tag", tagGroupId: null, tagGroupName: null, tagGroupColor: null }] });
    expect(await screen.findByRole("option", { name: "New tag" })).toBeInTheDocument();
  });

  it("caps keyboard navigation at the last rendered result", async () => {
    const user = userEvent.setup();
    Element.prototype.scrollIntoView = vi.fn();
    tagsFind.mockResolvedValue({ items: Array.from({ length: 51 }, (_, index) => ({
      id: index + 1,
      name: `Tag ${String(index + 1).padStart(2, "0")}`,
      tagGroupId: null,
      tagGroupName: null,
      tagGroupColor: null,
    })) });
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} preselectCriterion="tags" />,
    );

    const search = screen.getByRole("combobox", { name: "Search tags" });
    await user.type(search, "Tag");
    await waitFor(() => expect(screen.getByRole("option", { name: "Tag 50" })).toBeInTheDocument());
    expect(screen.queryByRole("option", { name: "Tag 51" })).not.toBeInTheDocument();
    for (let index = 0; index < 55; index += 1) await user.keyboard("{ArrowDown}");
    expect(search).toHaveAttribute("aria-activedescendant", "multi-id-result-tags-50");
    await user.keyboard("{Enter}");
    expect(screen.getByRole("button", { name: "Remove Tag 50" })).toBeInTheDocument();
  });

  it.each([
    {
      criterion: "Performer Occurrence Tags",
      searchName: "Search tags",
      findMock: tagsFind,
      entities: [
        { id: 1, name: "Tag One", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
        { id: 2, name: "Tag Two", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
      ],
      firstOption: "Tag One",
      secondOption: "Tag Two",
    },
    {
      criterion: "Performers",
      searchName: "Search performers",
      findMock: performersFind,
      entities: [{ id: 11, name: "Performer One" }, { id: 12, name: "Performer Two" }],
      firstOption: "Performer One",
      secondOption: "Performer Two",
    },
    {
      criterion: "Studios",
      searchName: "Search studios",
      findMock: studiosFind,
      entities: [{ id: 21, name: "Studio One" }, { id: 22, name: "Studio Two" }],
      firstOption: "Studio One",
      secondOption: "Studio Two",
    },
  ])("clears the $criterion search after keyboard include and exclude", async ({ criterion, searchName, findMock, entities, firstOption, secondOption }) => {
    const user = userEvent.setup();
    findMock.mockResolvedValue({ items: entities });
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} />,
    );

    await user.click(screen.getByRole("tab", { name: criterion }));
    const search = screen.getByRole("combobox", { name: searchName });
    await user.type(search, "one");
    await waitFor(() => expect(screen.getByRole("option", { name: firstOption })).toBeInTheDocument());
    await user.keyboard("{Enter}");
    expect(search).toHaveValue("");

    await user.type(search, "two");
    await waitFor(() => expect(screen.getByRole("option", { name: secondOption })).toBeInTheDocument());
    await user.keyboard("{Shift>}{Enter}{/Shift}");
    expect(search).toHaveValue("");
  });

  it("confirms before a null match mode clears multi-ID selections", async () => {
    const user = userEvent.setup();
    const onApply = vi.fn();
    tagsFind.mockResolvedValue({ items: [
      { id: 1, name: "Selected Tag", tagGroupId: null, tagGroupName: null, tagGroupColor: null },
    ] });
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />,
    );

    await user.click(screen.getByRole("tab", { name: "Tags" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Include Selected Tag" })).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: "Include Selected Tag" }));
    await user.click(screen.getByRole("button", { name: "None" }));

    const confirmation = screen.getByRole("dialog", { name: "Clear selected tags?" });
    expect(screen.getByRole("button", { name: "Remove Selected Tag" })).toBeInTheDocument();
    await waitFor(() => expect(within(confirmation).getByRole("button", { name: "Cancel" })).toHaveFocus());
    await user.keyboard("{Control>}{Enter}{/Control}");
    expect(onApply).not.toHaveBeenCalled();
    expect(screen.getByRole("dialog", { name: "Clear selected tags?" })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "Clear selected tags?" })).not.toBeInTheDocument();
    expect(screen.getByRole("dialog", { name: "Filters" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Includes All", pressed: true })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Selected Tag" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "None" }));
    await user.click(screen.getByRole("button", { name: "Clear selection" }));
    expect(screen.getByRole("button", { name: "None", pressed: true })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove Selected Tag" })).not.toBeInTheDocument();
  });

  it("discards canceled drafts before the next open", async () => {
    const user = userEvent.setup();
    const props = {
      onClose: vi.fn(),
      criteria: [{ id: "title", label: "Title", type: "string", filterKey: "titleCriterion" }] satisfies CriterionDefinition[],
      activeFilter: {},
      onApply: vi.fn(),
    };
    const { rerender } = render(<FilterDialog open {...props} />);

    await user.click(screen.getByRole("tab", { name: /Title/ }));
    await user.type(screen.getByRole("textbox", { name: "Value" }), "temporary");
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    rerender(<FilterDialog open={false} {...props} />);
    rerender(<FilterDialog open {...props} />);
    await user.click(screen.getByRole("tab", { name: /Title/ }));

    expect(screen.getByRole("textbox", { name: "Value" })).toHaveValue("");
  });

  it("scrolls a preselected criterion into view when the dialog opens", async () => {
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ titleCriterion: { value: "example", modifier: "EQUALS" } }}
        onApply={vi.fn()}
        preselectCriterion="title"
      />,
    );

    await waitFor(() => expect(scrollIntoView).toHaveBeenCalledWith({ block: "center", inline: "nearest" }));
    expect(screen.getByRole("textbox", { name: "Value" })).toHaveValue("example");
  });

  it("clears a stale criterion search before scrolling to a preselected criterion", async () => {
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    const props = {
      onClose: vi.fn(),
      criteria: VIDEO_CRITERIA,
      activeFilter: { titleCriterion: { value: "example", modifier: "EQUALS" } },
      onApply: vi.fn(),
    };
    const { rerender } = render(<FilterDialog open {...props} />);

    fireEvent.change(screen.getByRole("searchbox", { name: "Search filter criteria" }), { target: { value: "Audio Codec" } });
    rerender(<FilterDialog open={false} {...props} />);
    rerender(<FilterDialog open preselectCriterion="title" {...props} />);

    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveValue(""));
    expect(screen.getByRole("textbox", { name: "Value" })).toHaveValue("example");
    expect(scrollIntoView).toHaveBeenCalledWith({ block: "center", inline: "nearest" });
  });

  it.each(metadataServiceModifiers)("preserves selected metadata services for %s", (modifier) => {
    const onApply = vi.fn();
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ remoteIdCriterion: { value: "https://service.example/graphql", modifier } }}
        onApply={onApply}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({
      remoteIdCriterion: { value: "https://service.example/graphql", modifier },
    });
  });

  it.each([[VIDEO_CRITERIA], [PERFORMER_CRITERIA], [STUDIO_CRITERIA], [TAG_CRITERIA]])(
    "preserves legacy blank-value null filters",
    (criteria) => {
      const onApply = vi.fn();
      render(
        <FilterDialog
          open
          onClose={vi.fn()}
          criteria={criteria}
          activeFilter={{ remoteIdCriterion: { value: "   ", modifier: "IS_NULL" } }}
          onApply={onApply}
        />,
      );

      fireEvent.click(screen.getByRole("button", { name: "Apply" }));
      expect(onApply).toHaveBeenCalledWith({
        remoteIdCriterion: { value: "   ", modifier: "IS_NULL" },
      });
    },
  );

  it("keeps performer suggestions visible while a search refreshes", async () => {
    const onApply = vi.fn();
    let resolveSearch: ((value: { items: { id: number; name: string }[] }) => void) | undefined;
    performersFind.mockImplementation(({ q }: { q?: string }) => {
      if (!q) return Promise.resolve({ items: [{ id: 1, name: "Existing performer" }] });
      return new Promise((resolve) => { resolveSearch = resolve; });
    });

    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Performers"));
    expect(await screen.findByText("Existing performer")).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("Search performers..."), { target: { value: "Eva" } });
    await waitFor(() => expect(performersFind).toHaveBeenCalledWith(expect.objectContaining({ q: "Eva" })));

    expect(screen.getByText("Existing performer")).toBeInTheDocument();
    expect(screen.getByTitle("Include")).toBeDisabled();
    expect(screen.getByRole("listbox", { name: "performers results" })).toHaveAttribute("aria-busy", "true");
    fireEvent.click(screen.getByTitle("Include"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenLastCalledWith({});

    resolveSearch?.({ items: [{ id: 2, name: "Matching performer" }] });
    expect(await screen.findByText("Matching performer")).toBeInTheDocument();
  });

  it("resyncs its local edit state when the active filter changes outside the dialog", () => {
    const onApply = vi.fn();
    const onClose = vi.fn();

    const { rerender } = render(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ titleCriterion: { value: "Cloud Nine", modifier: "EQUALS" } }}
        onApply={onApply}
      />
    );

    expect(screen.getByRole("tab", { name: /^Title$/ })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("textbox", { name: "Value" })).toHaveValue("Cloud Nine");

    rerender(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    expect(screen.getByRole("textbox", { name: "Value" })).toHaveValue("");
  });

  it("applies multi-select performer gender filters as a regex-backed criterion", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={PERFORMER_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Gender"));
    fireEvent.click(screen.getByLabelText("Male"));
    fireEvent.click(screen.getByLabelText("Female"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      genderCriterion: expect.objectContaining({
        modifier: "MATCHES_REGEX",
        value: "^(?:Male|Female)$",
        _selectedValues: ["Male", "Female"],
      }),
    }));
  });

  it("does not restore a removed criterion when the parent rerenders with the same active filter", () => {
    const onApply = vi.fn();
    const onClose = vi.fn();

    const { rerender } = render(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ createdAtCriterion: { value: "2026-04-22T12:00", modifier: "EQUALS" } }}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Created At" }));
    expect(screen.queryByRole("button", { name: "Edit filter: Created At" })).not.toBeInTheDocument();

    rerender(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ createdAtCriterion: { value: "2026-04-22T12:00", modifier: "EQUALS" } }}
        onApply={onApply}
      />
    );

    expect(screen.queryByRole("button", { name: "Edit filter: Created At" })).not.toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /Created At/ })).toBeInTheDocument();
  });

  it("does not re-add an expanded timestamp criterion after removing it", () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ createdAtCriterion: { value: "2026-04-22T12:00", modifier: "EQUALS" } }}
        onApply={vi.fn()}
      />
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Created At" }));

    expect(screen.queryByRole("button", { name: "Edit filter: Created At" })).not.toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /Created At/ })).toBeInTheDocument();
  });

  it("uses full-width labeled controls for date and timestamp values", () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={vi.fn()}
        preselectCriterion="date"
      />
    );

    const dateValue = screen.getByLabelText("Value");
    expect(dateValue).toHaveClass("min-h-11", "w-full");

    fireEvent.click(screen.getByRole("button", { name: "Between" }));
    expect(screen.getByLabelText("Minimum")).toHaveClass("min-h-11", "w-full");
    expect(screen.getByLabelText("Maximum")).toHaveClass("min-h-11", "w-full");

    fireEvent.click(screen.getByRole("tab", { name: "Created At" }));

    expect(screen.getByLabelText("Value")).toHaveClass("min-h-11", "w-full");
  });

  it("uses a full-size labeled control for hash values", () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={vi.fn()}
        preselectCriterion="hash"
      />
    );

    expect(screen.getByRole("textbox", { name: "Value" })).toHaveClass("min-h-11", "w-full");
  });

  it("applies child-inclusive tag count toggles alongside the main criterion", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={TAG_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Video Count"));
    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "2" } });
    fireEvent.click(screen.getByLabelText("Count videos from child tags"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      videoCountCriterion: expect.objectContaining({
        modifier: "EQUALS",
        value: 2,
      }),
      videoCountIncludesChildren: true,
    }));
  });

  it("renders the career length filter with a years/months unit selector", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={PERFORMER_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Career Length"));

    const unitSelect = screen.getByLabelText("Career length unit") as HTMLSelectElement;
    expect(unitSelect.value).toBe("years");

    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      careerLengthCriterion: expect.objectContaining({
        modifier: "EQUALS",
        value: 3,
      }),
    }));
  });

  it("converts career length entered in months into whole years before applying", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={PERFORMER_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Career Length"));
    fireEvent.change(screen.getByLabelText("Career length unit"), { target: { value: "months" } });
    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "30" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      careerLengthCriterion: expect.objectContaining({
        modifier: "EQUALS",
        value: 3,
      }),
    }));
  });

  it("does not apply a tag duration filter until a threshold is entered", () => {
    const onApply = vi.fn();

    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />,
      (client) => {
        client.setQueryData(["tags", "all"], [{ id: 1, name: "Action", tagGroupName: "Acts" }]);
      }
    );

    fireEvent.click(screen.getByText("Tag Duration"));
    fireEvent.change(screen.getByPlaceholderText("Search tags"), { target: { value: "Action" } });
    fireEvent.click(screen.getByRole("option", { name: "Action" }));

    expect(screen.queryByRole("button", { name: "Remove Tag Duration filter chip" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({});
  });

  it("uses time and percent controls for tag duration filters without context mode choices", () => {
    const onApply = vi.fn();

    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />,
      (client) => {
        client.setQueryData(["tags", "all"], [{ id: 1, name: "Action", tagGroupName: "Acts" }]);
      }
    );

    fireEvent.click(screen.getByText("Tag Duration"));
    expect(screen.queryByRole("option", { name: "Any" })).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("Search tags"), { target: { value: "Action" } });
    fireEvent.click(screen.getByRole("option", { name: "Action" }));
    fireEvent.change(screen.getByLabelText("Tag duration time"), { target: { value: "1:30" } });
    fireEvent.blur(screen.getByLabelText("Tag duration time"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenLastCalledWith(expect.objectContaining({
      tagDurationCriterion: expect.objectContaining({
        clauses: [expect.objectContaining({
          tagId: 1,
          unit: "seconds",
          value: 90,
        })],
      }),
    }));

    fireEvent.change(screen.getByLabelText("Tag duration time"), { target: { value: "0.5" } });
    fireEvent.blur(screen.getByLabelText("Tag duration time"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenLastCalledWith(expect.objectContaining({
      tagDurationCriterion: expect.objectContaining({
        clauses: [expect.objectContaining({
          tagId: 1,
          unit: "seconds",
          value: 0.5,
        })],
      }),
    }));

    fireEvent.change(screen.getByLabelText("Tag duration unit"), { target: { value: "percent" } });
    fireEvent.change(screen.getByLabelText("Tag duration percent"), { target: { value: "25" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenLastCalledWith(expect.objectContaining({
      tagDurationCriterion: expect.objectContaining({
        clauses: [expect.objectContaining({
          tagId: 1,
          unit: "percent",
          value: 25,
        })],
      }),
    }));
  });

  it("allows multiple tag duration clauses in one filter", () => {
    const onApply = vi.fn();

    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />,
      (client) => {
        client.setQueryData(["tags", "all"], [
          { id: 1, name: "Action", tagGroupName: "Acts" },
          { id: 2, name: "Mood", tagGroupName: "Qualities" },
        ]);
      }
    );

    fireEvent.click(screen.getByText("Tag Duration"));
    fireEvent.change(screen.getByPlaceholderText("Search tags"), { target: { value: "Action" } });
    fireEvent.click(screen.getByRole("option", { name: "Action" }));
    fireEvent.change(screen.getByLabelText("Tag duration time"), { target: { value: "0:30" } });
    fireEvent.blur(screen.getByLabelText("Tag duration time"));
    fireEvent.click(screen.getByRole("button", { name: "Add tag duration" }));

    const searchInputs = screen.getAllByPlaceholderText("Search tags");
    fireEvent.change(searchInputs[1], { target: { value: "Mood" } });
    fireEvent.click(screen.getByRole("option", { name: "Mood" }));
    fireEvent.change(screen.getAllByLabelText("Tag duration unit")[1], { target: { value: "percent" } });
    fireEvent.change(screen.getByLabelText("Tag duration percent"), { target: { value: "10" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      tagDurationCriterion: expect.objectContaining({
        clauses: [
          expect.objectContaining({ tagId: 1, unit: "seconds", value: 30 }),
          expect.objectContaining({ tagId: 2, unit: "percent", value: 10 }),
        ],
      }),
    }));
  });

  it("adds a second instance of the same criterion as an AND expression", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ urlCriterion: { modifier: "INCLUDES", value: "foo" } }}
        onApply={onApply}
        preselectCriterion="url"
        supportsFilterExpressions
      />,
    );

    expect(screen.queryByRole("region", { name: "Active" })).not.toBeInTheDocument();
    const activeUrlTab = screen.getByRole("tab", { name: "URL" });
    expect(activeUrlTab.querySelector("span")).toHaveClass("text-accent");
    expect(activeUrlTab).toHaveAccessibleDescription("Active filter");
    expect(activeUrlTab.querySelector(".lucide-check")).not.toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Filter composition actions" })).toContainElement(
      screen.getByRole("button", { name: "Add another URL" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Add another URL" }));
    const second = screen.getByRole("group", { name: "URL condition 2" });
    const removeSecond = within(second).getByRole("button", { name: "Remove URL condition 2" });
    expect(removeSecond).toHaveClass("absolute");
    expect(removeSecond).toHaveTextContent("");
    expect(screen.queryByRole("combobox", { name: "Filter condition" })).not.toBeInTheDocument();
    await waitFor(() => expect(within(second).getByRole("button", { pressed: true })).toHaveFocus());
    fireEvent.click(within(second).getByRole("button", { name: "Excludes" }));
    fireEvent.change(within(second).getByLabelText("Value"), { target: { value: "bar" } });
    expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "URL condition 1" })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "URL condition 2" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit filter: URL. URL Includes foo" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit filter: URL. URL Excludes bar" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          { filter: { urlCriterion: { modifier: "INCLUDES", value: "foo" } } },
          { filter: { urlCriterion: { modifier: "EXCLUDES", value: "bar" } } },
        ],
      },
    });
  });

  it("marks criteria nested anywhere in a complex expression as active", () => {
    const activeFilter = {
      _filterExpression: {
        operator: "AND" as const,
        children: [
          { filter: { tagsCriterion: { modifier: "INCLUDES_ALL", value: [1] } } },
          { group: { operator: "OR" as const, children: [
            { filter: { resolutionCriterion: { modifier: "EQUALS", value: 2160 } } },
            { group: { operator: "AND" as const, children: [
              { filter: { performerFilterCriterion: { ageAtHostDateCriterion: { modifier: "EQUALS", value: 18 } } } },
            ] } },
          ] } },
          { filter: { tagsCriterion: { modifier: "INCLUDES_ALL", value: [2] } } },
        ],
      },
    };
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={activeFilter}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );

    for (const name of ["Tags", "Resolution", "Related Performers"]) {
      const tab = screen.getByRole("tab", { name });
      expect(tab).toHaveAccessibleDescription("Active filter");
      expect(tab.querySelector("span")).toHaveClass("text-accent");
    }
    expect(screen.getByLabelText("4 active filters")).toHaveTextContent("4");
    expect(countActiveObjectFilters(VIDEO_CRITERIA, activeFilter)).toBe(4);
  });

  it("stacks repeated date conditions in the normal criterion panel", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } }}
        onApply={onApply}
        preselectCriterion="date"
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Add another Date" }));
    expect(screen.getByRole("complementary", { name: "Filter criteria" })).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Filter condition" })).not.toBeInTheDocument();
    const first = screen.getByRole("group", { name: "Date condition 1" });
    const second = screen.getByRole("group", { name: "Date condition 2" });
    fireEvent.click(within(second).getByRole("button", { name: ">" }));
    fireEvent.change(within(second).getByLabelText("Value"), { target: { value: "2020-01-01" } });
    expect(within(first).getByDisplayValue("2000-01-01")).toBeInTheDocument();
    expect(within(second).getByDisplayValue("2020-01-01")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
        ],
      },
    });
  });

  it("keeps a single applied duration in the plain editor with a discoverable add action", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { filter: { durationCriterion: { modifier: "EQUALS", value: 600 } } },
        ] } }}
        onApply={vi.fn()}
        preselectCriterion="duration"
        supportsFilterExpressions
      />,
    );

    expect(screen.queryByRole("group", { name: "Duration condition 1" })).not.toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "Value" })).toHaveValue("10:00");
    expect(screen.getByRole("button", { name: "Add another Duration" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Combine Filters" })).not.toBeInTheDocument();
  });

  it("stacks repeated OR conditions in the normal criterion panel without changing the operator", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "OR", children: [
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
        ] } }}
        onApply={onApply}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("tab", { name: "Date" }));
    const first = screen.getByRole("group", { name: "Date condition 1" });
    const second = screen.getByRole("group", { name: "Date condition 2" });
    expect(within(first).getByDisplayValue("2020-01-01")).toBeInTheDocument();
    expect(within(second).getByDisplayValue("2000-01-01")).toBeInTheDocument();
    fireEvent.change(within(second).getByLabelText("Value"), { target: { value: "1999-01-01" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ _filterExpression: { operator: "OR", children: [
      { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
      { filter: { dateCriterion: { modifier: "LESS_THAN", value: "1999-01-01" } } },
    ] } });
  });

  it("adds another repeated condition to the active OR group", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "OR", children: [
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
        ] } }}
        onApply={onApply}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("tab", { name: "Date" }));
    fireEvent.click(screen.getByRole("button", { name: "Add another Date" }));
    const third = screen.getByRole("group", { name: "Date condition 3" });
    fireEvent.click(within(third).getByRole("button", { name: ">" }));
    fireEvent.change(within(third).getByLabelText("Value"), { target: { value: "2025-01-01" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ _filterExpression: { operator: "OR", children: [
      { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
      { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
      { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2025-01-01", value2: undefined } } },
    ] } });
  });

  it("does not expose an incomplete repeated-condition placeholder in the filter summary", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "OR", children: [
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("tab", { name: "Date" }));
    fireEvent.click(screen.getByRole("button", { name: "Add another Date" }));

    const toolbar = screen.getByRole("toolbar", { name: "Selected filters" });
    expect(within(toolbar).queryByText(/_criterion/i)).not.toBeInTheDocument();
    expect(within(toolbar).getByRole("button", { name: "Edit filter: Date > 2020-01-01" })).toBeInTheDocument();
    expect(within(toolbar).getByRole("button", { name: "Edit filter: Date < 2000-01-01" })).toBeInTheDocument();
  });

  it("shows an interactive OR chip group inside the dialog", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "OR", children: [
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );

    const operatorChip = screen.getByRole("button", { name: "Edit Any group in Combine Filters" });
    expect(operatorChip).toHaveProperty("tabIndex", 0);
    fireEvent.click(operatorChip);
    expect(screen.getByRole("heading", { name: "Combine Filters" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Any", pressed: true })).toHaveFocus());
    fireEvent.click(screen.getByRole("button", { name: "Back to simple filters" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Edit Any group in Combine Filters" })).toHaveFocus());

    fireEvent.click(screen.getByRole("button", { name: "Edit filter: Date < 2000-01-01" }));
    const second = screen.getByRole("group", { name: "Date condition 2" });
    await waitFor(() => expect(within(second).getByRole("button", { name: "<" })).toHaveFocus());
  });

  it("keeps expression chips and ordinary filters in one roving group after rerenders and removals", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: { operator: "AND", children: [
            { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
          ] },
          organizedCriterion: { value: true },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    const toolbar = screen.getByRole("toolbar", { name: "Selected filters" });
    const combineFilters = screen.getByRole("button", { name: "Combine Filters" });
    combineFilters.focus();
    await user.type(screen.getByRole("searchbox", { name: "Search filter criteria" }), "title");
    expect(within(toolbar).getAllByRole("button").filter((button) => button.tabIndex === 0)).toHaveLength(1);

    const removeExpression = within(toolbar).getByRole("button", { name: "Remove filter: Date" });
    removeExpression.focus();
    await user.keyboard("{Enter}");
    await waitFor(() => expect(within(toolbar).getByRole("button", { name: "Remove filter: Organized" })).toHaveFocus());
    expect(within(toolbar).getAllByRole("button").filter((button) => button.tabIndex === 0)).toEqual([
      within(toolbar).getByRole("button", { name: "Remove filter: Organized" }),
    ]);
  });

  it("uses the shared structured chips for simple expression conditions", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
          { filter: { performerFilterCriterion: {
            ageAtHostDateCriterion: { modifier: "BETWEEN", value: 18, value2: 25 }, objectFilter: {
            genderCriterion: { value: "^(?:Male)$", modifier: "MATCHES_REGEX", _selectedValues: ["Male"] },
          } } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    const toolbar = screen.getByRole("toolbar", { name: "Selected filters" });
    expect(within(toolbar).getByRole("button", { name: "Edit filter: Performer Count. Performer Count = 2" })).toHaveTextContent("Performer Count:= 2");
    const ageChip = within(toolbar).getByRole("button", { name: "Edit performer filter: Age (then) Between 18 and 25" });
    expect(ageChip).toHaveClass("border");
    expect(within(toolbar).getByRole("button", { name: "Edit performer filter: Gender Male" })).toHaveClass("border");
    expect(within(toolbar).queryByRole("button", { name: /At least one performer —/ })).not.toBeInTheDocument();
    fireEvent.click(ageChip);
    expect(screen.getByRole("dialog", { name: "Edit Related Performers condition" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Age (then)" })).toBeInTheDocument();
    fireEvent.keyDown(document, { key: "Escape" });
    await waitFor(() => expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument());
    expect(screen.queryByRole("heading", { name: "Filters / Related Performers" })).not.toBeInTheDocument();
    const returnedAgeChip = within(screen.getByRole("toolbar", { name: "Selected filters" })).getByRole("button", { name: "Edit performer filter: Age (then) Between 18 and 25" });
    await waitFor(() => expect(returnedAgeChip).toHaveFocus());

    fireEvent.click(returnedAgeChip);
    fireEvent.click(screen.getByRole("button", { name: "Back to filters" }));
    await waitFor(() => expect(within(screen.getByRole("toolbar", { name: "Selected filters" })).getByRole("button", { name: "Edit performer filter: Age (then) Between 18 and 25" })).toHaveFocus());

    fireEvent.click(within(screen.getByRole("toolbar", { name: "Selected filters" })).getByRole("button", { name: "Edit performer filter: Age (then) Between 18 and 25" }));
    fireEvent.click(screen.getByRole("button", { name: "Save condition" }));
    await waitFor(() => expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument());
    expect(screen.queryByRole("heading", { name: "Filters / Related Performers" })).not.toBeInTheDocument();
    await waitFor(() => expect(within(screen.getByRole("toolbar", { name: "Selected filters" })).getByRole("button", { name: "Edit performer filter: Age (then) Between 18 and 25" })).toHaveFocus());

    const returnedToolbar = screen.getByRole("toolbar", { name: "Selected filters" });
    fireEvent.click(within(returnedToolbar).getByRole("button", { name: "Remove filter: Related Performers" }));
    expect(within(returnedToolbar).queryByRole("button", { name: "Edit filter: Related Performers" })).not.toBeInTheDocument();
    expect(within(returnedToolbar).getByRole("button", { name: "Edit filter: Performer Count. Performer Count = 2" })).toBeInTheDocument();
  });

  it.each([
    ["search", { findFilter: { q: "sample" } }, "Edit performer filter: Text search sample"],
    ["existence", { _matchAll: true }, "Edit performer filter: Any performer"],
  ])("restores focus to the related performer %s chip", async (_facet, relatedValue, chipName) => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { filter: { performerFilterCriterion: relatedValue } },
          { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    const chip = within(screen.getByRole("toolbar", { name: "Selected filters" })).getByRole("button", { name: chipName });
    fireEvent.click(chip);
    fireEvent.keyDown(document, { key: "Escape" });

    await waitFor(() => expect(within(screen.getByRole("toolbar", { name: "Selected filters" })).getByRole("button", { name: chipName })).toHaveFocus());
  });

  it("keeps focus in the unified group when its final ordinary filter is removed", async () => {
    const user = userEvent.setup();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: { operator: "AND", children: [
            { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
          ] },
          titleCriterion: { value: "example", modifier: "EQUALS" },
          organizedCriterion: { value: true },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    const toolbar = screen.getByRole("toolbar", { name: "Selected filters" });
    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());
    const removeOrganized = within(toolbar).getByRole("button", { name: "Remove filter: Organized" });
    removeOrganized.focus();
    await user.keyboard("{Enter}");
    await waitFor(() => expect(within(toolbar).getByRole("button", { name: "Edit filter: Title" })).toHaveFocus());

    await user.keyboard("{Delete}");
    await waitFor(() => expect(within(toolbar).getByRole("button", { name: "Remove filter: Date" })).toHaveFocus());
    expect(within(toolbar).getAllByRole("button").filter((button) => button.tabIndex === 0)).toEqual([
      within(toolbar).getByRole("button", { name: "Remove filter: Date" }),
    ]);
  });

  it("makes complex-expression actions explicit and restores focus after editing", async () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [{
              group: {
                operator: "OR",
                children: [
                  { filter: { titleCriterion: { modifier: "INCLUDES", value: "one" } } },
                  { filter: { titleCriterion: { modifier: "INCLUDES", value: "two" } } },
                ],
              },
            }],
          },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    const editExpression = screen.getByRole("button", { name: "Combine Filters" });
    expect(editExpression).toHaveAttribute("title", "Combine Filters");
    expect(editExpression).toHaveTextContent("");
    const clearAll = screen.getByRole("button", { name: "Clear all" });
    expect(clearAll).toHaveTextContent("Clear all");
    fireEvent.click(editExpression);
    expect(screen.getByRole("heading", { name: "Combine Filters" })).toBeInTheDocument();
    fireEvent.keyDown(document, { key: "Escape" });
    await waitFor(() => expect(screen.getByRole("button", { name: "Combine Filters" })).toHaveFocus());

    fireEvent.click(screen.getByRole("button", { name: "Clear all" }));
    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());
    expect(screen.queryByRole("toolbar", { name: "Selected filters" })).not.toBeInTheDocument();
  });

  it("removes nested combined-filter conditions from their own chips", async () => {
    const onApply = vi.fn();
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              {
                group: {
                  operator: "OR",
                  children: [
                    { filter: { titleCriterion: { modifier: "INCLUDES", value: "one" } } },
                    { filter: { titleCriterion: { modifier: "INCLUDES", value: "two" } } },
                  ],
                },
              },
              { filter: { resolutionCriterion: { modifier: "EQUALS", value: 2160 } } },
            ],
          },
        }}
        onApply={onApply}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    expect(screen.queryByRole("button", { name: "Remove filter: Combine Filters" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove filter: Title Includes one" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove filter: Title Includes two" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove filter: Resolution = 4K" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Title Includes one" }));
    expect(screen.queryByRole("button", { name: "Edit filter: Title Includes one" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit filter: Title Includes two" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit Any group in Combine Filters" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Remove filter: Title Includes two" })).toHaveFocus());

    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          { group: { operator: "OR", children: [{ filter: { titleCriterion: { modifier: "INCLUDES", value: "two" } } }] } },
          { filter: { resolutionCriterion: { modifier: "EQUALS", value: 2160 } } },
        ],
      },
    });
  });

  it("opens a nested related-condition editor directly from a combined-filter chip", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "OR", children: [
          { filter: { performerFilterCriterion: { ageAtHostDateCriterion: { modifier: "EQUALS", value: 18 } } } },
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "sample" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    const ageChip = screen.getByRole("button", { name: "Edit performer filter: Age (then) = 18" });
    expect(ageChip).not.toHaveClass("border");
    fireEvent.click(ageChip);

    expect(screen.getByRole("dialog", { name: "Edit Related Performers condition" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Age (then)" })).toBeInTheDocument();
  });

  it("removes a condition through a flattened None presentation path", () => {
    const onApply = vi.fn();
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "NOT", children: [{ group: { operator: "OR", children: [
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "one" } } },
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "two" } } },
        ] } }] } }}
        onApply={onApply}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Title Includes one" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ _filterExpression: { operator: "NOT", children: [
      { filter: { titleCriterion: { modifier: "INCLUDES", value: "two" } } },
    ] } });
  });

  it("prunes an empty nested group after removing its final condition chip", () => {
    const onApply = vi.fn();
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { group: { operator: "OR", children: [{ filter: { titleCriterion: { modifier: "INCLUDES", value: "one" } } }] } },
              { filter: { resolutionCriterion: { modifier: "EQUALS", value: 2160 } } },
            ],
          },
        }}
        onApply={onApply}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Title Includes one" }));
    expect(screen.queryByRole("button", { name: "Edit Any group in Combine Filters" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Edit filter: Resolution/ })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [{ filter: { resolutionCriterion: { modifier: "EQUALS", value: 2160 } } }],
      },
    });
  });

  it("returns focus to filter search after removing the final combined-filter condition", async () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "NOT",
            children: [{ filter: { titleCriterion: { modifier: "INCLUDES", value: "one" } } }],
          },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Title Includes one" }));

    await waitFor(() => expect(screen.getByRole("searchbox", { name: "Search filter criteria" })).toHaveFocus());
    expect(screen.queryByRole("toolbar", { name: "Selected filters" })).not.toBeInTheDocument();
  });

  it("returns focus to a remaining ordinary filter after removing the final combined condition", async () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: { operator: "NOT", children: [{ filter: { titleCriterion: { modifier: "INCLUDES", value: "one" } } }] },
          organizedCriterion: { value: true },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
        openAtRoot
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Title Includes one" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Edit filter: Organized" })).toHaveFocus());
  });

  it("opens an Advanced OR leaf in the repeated criterion stack and returns to the organizer", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "OR", children: [
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
        ] } }}
        onApply={onApply}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: /Edit condition 2: Date/ }));
    const second = screen.getByRole("group", { name: "Date condition 2" });
    expect(screen.getByRole("group", { name: "Date condition 1" })).toBeInTheDocument();
    await waitFor(() => expect(within(second).getByRole("button", { name: "<" })).toHaveFocus());
    fireEvent.click(screen.getByRole("button", { name: "Back to Combine Filters" }));
    expect(screen.getByRole("heading", { name: "Combine Filters" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ _filterExpression: { operator: "OR", children: [
      { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
      { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
    ] } });
  });

  it("opens a targeted OR leaf directly in the repeated criterion stack", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "OR", children: [
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialExpressionPath={[1]}
      />,
    );

    expect(screen.getByRole("group", { name: "Date condition 1" })).toBeInTheDocument();
    const second = screen.getByRole("group", { name: "Date condition 2" });
    await waitFor(() => expect(within(second).getByRole("button", { name: "<" })).toHaveFocus());
  });

  it("adds a repeated simple criterion as an implicit root AND without opening Combine Filters", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" },
          _filterExpression: {
            operator: "OR",
            children: [
              { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
              { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
            ],
          },
        }}
        onApply={onApply}
        preselectCriterion="date"
        supportsFilterExpressions
      />,
    );

    expect(screen.queryByRole("group", { name: "Date condition 1" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add another Date" }));
    expect(screen.getByRole("complementary", { name: "Filter criteria" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Combine Filters" })).not.toBeInTheDocument();
    const newCondition = screen.getByRole("group", { name: "Date condition 2" });
    fireEvent.click(within(newCondition).getByRole("button", { name: ">" }));
    fireEvent.change(within(newCondition).getByLabelText("Value"), { target: { value: "2020-01-01" } });
    expect(screen.getByRole("group", { name: "Date condition 1" })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Date condition 2" })).toBeInTheDocument();
    expect(within(screen.getByRole("toolbar", { name: "Selected filters" })).getByRole("button", { name: "Edit All group in Combine Filters" })).toBeInTheDocument();
    expect(within(screen.getByRole("toolbar", { name: "Selected filters" })).getByRole("button", { name: "Edit filter: Date < 2000-01-01" })).toHaveClass(
      "relative",
      "focus-visible:after:absolute",
      "focus-visible:after:inset-0",
      "focus-visible:after:ring-2",
      "focus-visible:after:ring-inset",
      "focus-visible:after:ring-accent",
      "focus-visible:after:content-['']",
    );
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          { group: { operator: "OR", children: [
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
            { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
          ] } },
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
        ],
      },
    });
  });

  it("shows a new draft when Advanced adds a criterion that already exists", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    fireEvent.click(screen.getByRole("button", { name: "Add condition" }));
    fireEvent.click(screen.getByRole("tab", { name: "Date" }));

    expect(screen.getByRole("heading", { name: "Add Date condition" })).toBeInTheDocument();
    expect(screen.queryByRole("group", { name: "Date condition 1" })).not.toBeInTheDocument();
    expect(screen.getByLabelText("Value")).toHaveValue("");
    expect(screen.getByRole("button", { name: "Save condition" })).toBeDisabled();
  });

  it("preserves an auxiliary toggle while editing a repeated criterion", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={TAG_CRITERIA}
        activeFilter={{ videoCountCriterion: { modifier: "EQUALS", value: 2 }, videoCountIncludesChildren: true }}
        onApply={onApply}
        preselectCriterion="videoCount"
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Add another Video Count" }));
    const first = screen.getByRole("group", { name: "Video Count condition 1" });
    const second = screen.getByRole("group", { name: "Video Count condition 2" });
    fireEvent.change(within(second).getByRole("spinbutton"), { target: { value: "4" } });
    fireEvent.change(within(first).getByRole("spinbutton"), { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ _filterExpression: { operator: "AND", children: [
      { filter: { videoCountCriterion: { modifier: "EQUALS", value: 3 }, videoCountIncludesChildren: true } },
      { filter: { videoCountCriterion: { modifier: "EQUALS", value: 4 } } },
    ] } });
  });

  it("removes an incomplete repeated condition inline", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ urlCriterion: { modifier: "INCLUDES", value: "foo" } }}
        onApply={onApply}
        preselectCriterion="url"
        supportsFilterExpressions
      />,
    );

    expect(screen.getByLabelText("1 active filters")).toHaveTextContent("1");
    fireEvent.click(screen.getByRole("button", { name: "Add another URL" }));
    expect(screen.getByLabelText("1 active filters")).toHaveTextContent("1");
    fireEvent.click(screen.getByRole("button", { name: "Remove URL condition 2" }));
    expect(screen.queryByRole("group", { name: "URL condition 1" })).not.toBeInTheDocument();
    expect(screen.getByDisplayValue("foo")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: { operator: "AND", children: [
        { filter: { urlCriterion: { modifier: "INCLUDES", value: "foo" } } },
      ] },
    });
  });

  it("keeps a flat AND expression in the simple filtering workflow", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
              { filter: { directorCriterion: { modifier: "EXCLUDES", value: "bar" } } },
            ],
          },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );

    expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Advanced" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Combine Filters" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Edit filter: Title. Title Includes foo" }));
    expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument();
    expect(screen.queryByRole("group", { name: "Title condition 1" })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "=" })).toHaveFocus());
  });

  it("uses the ordinary stack when editing a flat condition from the organizer", async () => {
    const onClose = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
              { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
            ],
          },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    fireEvent.click(screen.getByRole("button", { name: /Edit condition 2: Date/ }));
    expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument();
    expect(screen.getByRole("complementary", { name: "Filter criteria" })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Date condition 1" })).toBeInTheDocument();
    const second = screen.getByRole("group", { name: "Date condition 2" });
    expect(screen.queryByRole("button", { name: "Save condition" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Cancel condition" })).not.toBeInTheDocument();
    fireEvent.change(within(second).getByLabelText("Value"), { target: { value: "2021-01-01" } });

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    expect(screen.getByRole("button", { name: /Edit condition 2: Date is greater than 2021-01-01/ })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Back to simple filters" }));
    expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onClose).toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Edit filter: Date. Date > 2020-01-01" })).toBeInTheDocument();
  });

  it("shows ordinary sidebar edits when returning from a flat stack to Advanced", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    fireEvent.click(screen.getByRole("button", { name: /Edit condition 1: Date/ }));
    fireEvent.click(screen.getByRole("tab", { name: "Director" }));
    fireEvent.change(screen.getByLabelText("Value"), { target: { value: "bar" } });
    fireEvent.click(screen.getByRole("button", { name: "Back to Combine Filters" }));

    expect(screen.getByRole("button", { name: /Edit condition 3: Director is bar/ })).toBeInTheDocument();
  });

  it("offers combining only after two simple filters are active", () => {
    renderWithQueryClient(
      <FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={vi.fn()} supportsFilterExpressions />,
    );

    expect(screen.queryByRole("button", { name: "Combine Filters" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Title" }));
    fireEvent.change(screen.getByLabelText("Value"), { target: { value: "foo" } });
    expect(screen.queryByRole("button", { name: "Combine Filters" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("tab", { name: "Director" }));
    fireEvent.change(screen.getByLabelText("Value"), { target: { value: "bar" } });
    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));

    expect(screen.getByRole("heading", { name: "Combine Filters" })).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /Edit condition/ })).toHaveLength(2);
  });

  it("adds a simple condition to the root of an existing OR expression with AND", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "OR",
            children: [
              { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
              { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
            ],
          },
        }}
        onApply={onApply}
        supportsFilterExpressions
        preselectCriterion="performerCount"
      />,
    );

    fireEvent.change(screen.getByRole("spinbutton", { name: "Value" }), { target: { value: "2" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          {
            group: {
              operator: "OR",
              children: [
                { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
                { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
              ],
            },
          },
          { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
        ],
      },
    });
  });

  it("presents filter expressions as readable rules with contextual structural actions", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        subjectLabel="videos"
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
              { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
              { filter: { performerFilterCriterion: {
                ageAtHostDateCriterion: { modifier: "BETWEEN", value: 18, value2: 25 },
                objectFilter: {
                  genderCriterion: { modifier: "MATCHES_REGEX", value: "^(?:Male)$", _selectedValues: ["Male"] },
                },
              } } },
            ],
          },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    expect(screen.getByText("Find videos where")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "All", pressed: true })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Any", pressed: false })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit condition 1: Title includes foo" })).toHaveTextContent("Title:Includes foo");
    expect(screen.getByRole("button", { name: "Remove filter: Title" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Edit condition 3: At least one performer/ })).toHaveTextContent("At least one performer matching all");
    expect(screen.getByRole("button", { name: "Edit performer filter: Age (then)" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit performer filter: Gender" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove filter: Related Performers" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Edit performer filter: Gender" }));
    expect(screen.getByRole("tabpanel", { name: "Gender" })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: "Male" })).toBeChecked();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByRole("heading", { name: "Edit Related Performers condition" })).toBeInTheDocument();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByRole("heading", { name: "Combine Filters" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove performer filter: Gender" }));
    expect(screen.queryByRole("button", { name: "Edit performer filter: Gender" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit performer filter: Age (then)" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove filter: Title" }));
    expect(screen.queryByRole("button", { name: "Remove filter: Title" })).not.toBeInTheDocument();
    const nextCondition = screen.getByRole("button", { name: "Edit condition 1: Director includes bar" });
    await waitFor(() => expect(nextCondition).toHaveFocus());
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Move condition 1" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Explain this search" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove condition 1" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "More actions for condition 1" }));
    expect(screen.getByRole("button", { name: "Edit" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove" })).toBeInTheDocument();
    fireEvent.keyDown(screen.getByRole("button", { name: "Remove" }), { key: "Escape" });
    expect(screen.queryByRole("button", { name: "Remove" })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "More actions for condition 1" })).toHaveFocus());
  });

  it("groups selected sibling conditions and navigates advanced views with Escape", async () => {
    const onApply = vi.fn();
    const onClose = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={onClose}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
              { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
              { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
            ],
          },
        }}
        onApply={onApply}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));

    startRootSubgroupCreation();
    fireEvent.click(screen.getByRole("button", { name: "Select condition 1 for grouping" }));
    fireEvent.click(screen.getByRole("button", { name: "Cancel grouping" }));
    expect(screen.getByRole("button", { name: /Edit condition 1: Title/ })).toBeInTheDocument();

    startRootSubgroupCreation();
    fireEvent.click(screen.getByRole("button", { name: "Select condition 1 for grouping" }));
    fireEvent.click(screen.getByRole("button", { name: "Select condition 2 for grouping" }));
    fireEvent.click(screen.getByRole("button", { name: "Group selected as Any" }));

    expect(screen.getByRole("region", { name: "Any group" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("region", { name: "Any group" })).toHaveFocus());

    fireEvent.click(within(screen.getByRole("region", { name: "Any group" })).getByRole("button", { name: "More actions for group" }));
    fireEvent.click(screen.getByRole("button", { name: "Dissolve group" }));
    startRootSubgroupCreation();
    fireEvent.click(screen.getByRole("button", { name: "Select condition 1 for grouping" }));
    fireEvent.click(screen.getByRole("button", { name: "Select condition 2 for grouping" }));
    fireEvent.click(screen.getByRole("button", { name: "Group selected as Any" }));

    fireEvent.click(screen.getByRole("button", { name: /Edit condition 1: Title/ }));
    expect(screen.getByRole("heading", { name: "Edit Title condition" })).toBeInTheDocument();
    expect(screen.getByRole("complementary", { name: "Filter criteria" })).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Filter condition" })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Includes", pressed: true })).toHaveFocus());

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByRole("heading", { name: "Combine Filters" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: /Edit condition 1: Title/ })).toHaveFocus());
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByRole("heading", { name: "Filters" })).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Edit All group in Combine Filters" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit Any group in Combine Filters" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Edit filter: Title/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Edit filter: Director/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Edit filter: Performer Count/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Clear all" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Combine Filters" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Open Combine Filters" })).not.toBeInTheDocument();

    const nestedOperatorChip = screen.getByRole("button", { name: "Edit Any group in Combine Filters" });
    fireEvent.click(nestedOperatorChip);
    await waitFor(() => expect(within(screen.getByRole("region", { name: "Any group" })).getByRole("button", { name: "Any", pressed: true })).toHaveFocus());
    fireEvent.keyDown(document, { key: "Escape" });
    await waitFor(() => expect(screen.getByRole("button", { name: "Edit Any group in Combine Filters" })).toHaveFocus());

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          {
            group: {
              operator: "OR",
              children: [
                { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
                { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
              ],
            },
          },
          { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
        ],
      },
    });
  });

  it("groups one selected condition as semantic None and serializes unary NOT", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
              { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
            ],
          },
        }}
        onApply={onApply}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    startRootSubgroupCreation();
    fireEvent.click(screen.getByRole("button", { name: "Select condition 2 for grouping" }));
    fireEvent.click(screen.getByRole("button", { name: "Group selected as None" }));

    expect(screen.getByRole("region", { name: "None group" })).toBeInTheDocument();
    expect(within(screen.getByRole("region", { name: "None group" })).getByRole("button", { name: "Add condition" })).toBeInTheDocument();

    fireEvent.click(within(screen.getByRole("region", { name: "None group" })).getByRole("button", { name: "More actions for group" }));
    fireEvent.click(screen.getByRole("button", { name: "Dissolve group" }));
    await waitFor(() => expect(screen.queryByRole("region", { name: "None group" })).not.toBeInTheDocument());
    startRootSubgroupCreation();
    fireEvent.click(screen.getByRole("button", { name: "Select condition 2 for grouping" }));
    fireEvent.click(screen.getByRole("button", { name: "Group selected as None" }));
    expect(screen.getByRole("button", { name: "None", pressed: true })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
          { group: { operator: "NOT", children: [
            { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
          ] } },
        ],
      },
    });
  });

  it("serializes a multi-condition None group as unary NOT around OR", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
          { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
        ] } }}
        onApply={onApply}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    fireEvent.click(screen.getByRole("button", { name: "None", pressed: false }));
    expect(screen.getByRole("button", { name: "None", pressed: true })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ _filterExpression: { operator: "NOT", children: [
      { group: { operator: "OR", children: [
        { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
        { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
      ] } },
    ] } });
  });

  it("keeps non-equivalent compound negation visibly enclosed as Exclude", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "NOT", children: [
          { group: { operator: "AND", children: [
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
            { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
          ] } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    expect(screen.getByRole("region", { name: "Exclude group" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "All group" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Exclude group" }).querySelector(":scope > div:first-child button[aria-label='Add condition']")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "More actions for root group" })).not.toBeInTheDocument();
  });

  it("keeps operators out of group action menus", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { group: { operator: "NOT", children: [
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
          ] } },
          { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    const noneGroup = screen.getByRole("region", { name: "None group" });
    fireEvent.click(within(noneGroup).getByRole("button", { name: "More actions for group" }));
    const actions = within(noneGroup).getByRole("group", { name: "Group actions" });
    expect(within(actions).getByRole("button", { name: "Create subgroup" })).toBeInTheDocument();
    expect(within(actions).queryByRole("button", { name: "All" })).not.toBeInTheDocument();
    expect(within(actions).queryByRole("button", { name: "Any" })).not.toBeInTheDocument();
    expect(within(actions).queryByRole("button", { name: "Exclude" })).not.toBeInTheDocument();
  });

  it("moves a nested condition to the outer group through the accessible destination menu", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { group: { operator: "OR", children: [
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
            { filter: { resolutionCriterion: { modifier: "EQUALS", value: 2160 } } },
          ] } },
          { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
        ] } }}
        onApply={onApply}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    const anyGroup = screen.getByRole("region", { name: "Any group" });
    fireEvent.click(within(anyGroup).getByRole("button", { name: "More actions for condition 2" }));
    fireEvent.click(screen.getByRole("button", { name: "Move to…" }));
    fireEvent.click(screen.getByRole("button", { name: "Outermost All group" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Move condition 2" })).toHaveFocus());
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({ _filterExpression: { operator: "AND", children: [
      { group: { operator: "OR", children: [
        { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
      ] } },
      { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
      { filter: { resolutionCriterion: { modifier: "EQUALS", value: 2160 } } },
    ] } });
  });

  it("moves a nested condition with keyboard pickup and drop", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { group: { operator: "OR", children: [
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
            { filter: { resolutionCriterion: { modifier: "EQUALS", value: 2160 } } },
          ] } },
          { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    const handle = within(screen.getByRole("region", { name: "Any group" })).getByRole("button", { name: "Move condition 2" });
    handle.focus();
    fireEvent.keyDown(handle, { key: "Enter" });
    expect(screen.getByRole("button", { name: "Drop condition in Outermost All group" })).toHaveFocus();
    fireEvent.keyDown(screen.getByRole("button", { name: "Drop condition in Outermost All group" }), { key: "Enter" });

    await waitFor(() => expect(screen.getByRole("button", { name: "Move condition 2" })).toHaveFocus());
    expect(screen.getByText("Condition moved to Outermost All group.")).toBeInTheDocument();
  });

  it("moves a nested condition by dropping its handle on another group", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { group: { operator: "OR", children: [
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
            { filter: { resolutionCriterion: { modifier: "EQUALS", value: 2160 } } },
          ] } },
          { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    const handle = within(screen.getByRole("region", { name: "Any group" })).getByRole("button", { name: "Move condition 2" });
    const root = screen.getByRole("region", { name: "All group" });
    const dataTransfer = { effectAllowed: "none", dropEffect: "none", setData: vi.fn() };
    fireEvent.dragStart(handle, { dataTransfer });
    fireEvent.dragOver(root, { dataTransfer });
    fireEvent.drop(root, { dataTransfer });

    await waitFor(() => expect(screen.getByText("Condition moved to Outermost All group.")).toBeInTheDocument());
    expect(within(screen.getByRole("region", { name: "Any group" })).queryByText("Resolution")).not.toBeInTheDocument();
    expect(within(root).getByRole("button", { name: /Edit condition 2: Resolution/ })).toBeInTheDocument();
  });

  it("keeps draft-only expression conditions visible in Combine Filters", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { filter: { _criterionId: "title" } },
          { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    expect(screen.getByRole("button", { name: "Edit condition 1: Title is not set" })).toBeInTheDocument();
  });

  it("presents a unary NOT as an editable semantic None group", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "NOT", children: [
          { filter: { performerFilterCriterion: {
            objectFilter: { genderCriterion: { modifier: "MATCHES_REGEX", value: "^(?:Male)$", _selectedValues: ["Male"] } },
          } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    const noneGroup = screen.getByRole("region", { name: "None group" });
    expect(within(noneGroup).getByRole("button", { name: "None", pressed: true })).toBeInTheDocument();
    expect(within(noneGroup).getByRole("button", { name: "Add condition" })).toBeInTheDocument();
  });

  it("adds a direct condition by wrapping a root NOT in an implicit AND", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "NOT", children: [
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
        ] } }}
        onApply={onApply}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("tab", { name: "Date" }));
    expect(screen.queryByRole("group", { name: "Date condition 1" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add another Date" }));
    const newCondition = screen.getByRole("group", { name: "Date condition 1" });
    expect(within(newCondition).getByRole("button", { name: "Remove Date condition 1" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add another Date" })).toBeDisabled();
    await waitFor(() => expect(within(newCondition).getByRole("button", { pressed: true })).toHaveFocus());
    fireEvent.click(within(newCondition).getByRole("button", { name: ">" }));
    fireEvent.change(within(newCondition).getByLabelText("Value"), { target: { value: "2020-01-01" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ _filterExpression: { operator: "AND", children: [
      { group: { operator: "NOT", children: [
        { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
      ] } },
      { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
    ] } });
  });

  it("adds a direct condition at the root while preserving a nested NOT", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
          { group: { operator: "NOT", children: [
            { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
          ] } },
        ] } }}
        onApply={onApply}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Edit filter: Date < 2000-01-01" }));
    fireEvent.click(screen.getByRole("button", { name: "Add another Date" }));
    const newCondition = screen.getByRole("group", { name: "Date condition 1" });
    expect(within(newCondition).getByRole("button", { name: "Remove Date condition 1" })).toBeInTheDocument();
    await waitFor(() => expect(within(newCondition).getByRole("button", { pressed: true })).toHaveFocus());
    fireEvent.click(within(newCondition).getByRole("button", { name: ">" }));
    fireEvent.change(within(newCondition).getByLabelText("Value"), { target: { value: "2020-01-01" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
          { group: { operator: "NOT", children: [
            { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
          ] } },
          { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
        ],
      },
    });
  });

  it.each([
    {
      name: "root NOT",
      activeFilter: { _filterExpression: { operator: "NOT", children: [
        { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
      ] } },
      openNested: false,
    },
    {
      name: "nested NOT",
      activeFilter: { _filterExpression: { operator: "AND", children: [
        { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
        { group: { operator: "NOT", children: [
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
        ] } },
      ] } },
      openNested: true,
    },
  ])("restores the $name editor after removing an inline addition", async ({ activeFilter, openNested }) => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={activeFilter}
        onApply={vi.fn()}
        supportsFilterExpressions
        preselectCriterion={openNested ? undefined : "date"}
      />,
    );

    if (openNested) fireEvent.click(screen.getByRole("button", { name: "Edit filter: Date < 2000-01-01" }));
    fireEvent.click(screen.getByRole("button", { name: "Add another Date" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove Date condition 1" }));

    expect(screen.queryByRole("group", { name: "Date condition 1" })).not.toBeInTheDocument();
    expect(screen.getByDisplayValue("2000-01-01")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add another Date" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "<" })).toHaveFocus());
  });

  it("rebases inline-addition restoration after removing an earlier sibling", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { filter: { dateCriterion: { modifier: "LESS_THAN", value: "1990-01-01" } } },
          { group: { operator: "NOT", children: [
            { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
          ] } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Edit filter: Date < 2000-01-01" }));
    fireEvent.click(screen.getByRole("button", { name: "Add another Date" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove Date condition 1" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove Date condition 1" }));

    expect(screen.queryByRole("group", { name: "Date condition 1" })).not.toBeInTheDocument();
    expect(screen.getByDisplayValue("2000-01-01")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add another Date" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "<" })).toHaveFocus());
  });

  it("flattens canonical NOT of OR as a semantic None group", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "NOT", children: [
          { group: { operator: "OR", children: [
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
            { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
          ] } },
        ] } }}
        onApply={onApply}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    const noneGroup = screen.getByRole("region", { name: "None group" });
    expect(within(noneGroup).getByRole("button", { name: "None", pressed: true })).toBeInTheDocument();
    expect(within(noneGroup).getByRole("button", { name: /Edit condition 1: Title/ })).toBeInTheDocument();
    expect(within(noneGroup).getByRole("button", { name: /Edit condition 2: Director/ })).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Any group" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({ _filterExpression: { operator: "NOT", children: [
      { group: { operator: "OR", children: [
        { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
        { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
      ] } },
    ] } });
  });

  it("supports keyboard-operated grouping and contextual group actions", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
              { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
              { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
            ],
          },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    screen.getByRole("button", { name: "More actions for root group" }).focus();
    startRootSubgroupCreation();
    const first = screen.getByRole("button", { name: "Select condition 1 for grouping" });
    await waitFor(() => expect(first).toHaveFocus());
    fireEvent.click(first);
    screen.getByRole("button", { name: "Select condition 2 for grouping" }).focus();
    fireEvent.click(screen.getByRole("button", { name: "Select condition 2 for grouping" }));
    fireEvent.click(screen.getByRole("button", { name: "Group selected as Any" }));

    const nestedGroup = screen.getByRole("region", { name: "Any group" });
    await waitFor(() => expect(nestedGroup).toHaveFocus());
    fireEvent.click(within(nestedGroup).getByRole("button", { name: "More actions for group" }));
    fireEvent.click(screen.getByRole("button", { name: "Dissolve group" }));
    await waitFor(() => expect(screen.queryByRole("region", { name: "Any group" })).not.toBeInTheDocument());
    await waitFor(() => expect(screen.getByRole("button", { name: /Edit condition 1: Title/ })).toHaveFocus());
  });

  it("restores focus to the first displayed child after dissolving a reordered group", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "AND", children: [
          { group: { operator: "OR", children: [
            { group: { operator: "NOT", children: [{ filter: { directorCriterion: { modifier: "INCLUDES", value: "blocked" } } }] } },
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "visible first" } } },
          ] } },
          { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    const anyGroup = screen.getByRole("region", { name: "Any group" });
    fireEvent.click(anyGroup.querySelector<HTMLButtonElement>(":scope > div:first-child button[aria-label='More actions for group']")!);
    fireEvent.click(screen.getByRole("button", { name: "Dissolve group" }));

    await waitFor(() => expect(screen.getByRole("button", { name: /Edit condition 1: Title includes visible first/ })).toHaveFocus());
  });

  it("renders nested and related filter logic directly as readable rules", () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        subjectLabel="videos"
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { group: { operator: "OR", children: [
                { filter: { dateCriterion: { modifier: "GREATER_THAN", value: "2020-01-01" } } },
                { filter: { dateCriterion: { modifier: "LESS_THAN", value: "2000-01-01" } } },
              ] } },
              { filter: { performerFilterCriterion: {
                mode: "every",
                conditionOperator: "or",
                ageAtHostDateCriterion: { modifier: "BETWEEN", value: 18, value2: 20 },
                objectFilter: { favoriteCriterion: { value: true } },
              } } },
              { filter: { tagsCriterion: {
                modifier: "INCLUDES",
                value: [1],
                excludes: [2],
                _names: { "1": "Included tag", "2": "Excluded tag" },
              } } },
              { filter: {
                remoteIdValueCriterion: { modifier: "EQUALS", value: "remote-123" },
                remoteIdCriterion: { modifier: "EQUALS", value: "https://metadata.example" },
              } },
            ],
          },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    expect(screen.getByText("Find videos where")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit condition 1: Date is greater than 2020-01-01" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Edit condition 2: Date is less than 2000-01-01" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Edit condition 1: Every performer/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Edit condition 2: Tags:/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Edit condition 3: Remote ID:/ })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Explain this search" })).not.toBeInTheDocument();

    const rootMatchAny = screen.getAllByRole("button", { name: "Any", pressed: false })[0];
    fireEvent.click(rootMatchAny);
    expect(rootMatchAny).toHaveAttribute("aria-pressed", "true");
  });

  it("presents mixed groups in logical order without rewriting the expression", async () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        subjectLabel="videos"
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { group: { operator: "NOT", children: [{ filter: { directorCriterion: { modifier: "INCLUDES", value: "blocked" } } }] } },
              { group: { operator: "OR", children: [
                { filter: { titleCriterion: { modifier: "INCLUDES", value: "first" } } },
                { group: { operator: "NOT", children: [{ filter: { titleCriterion: { modifier: "INCLUDES", value: "excluded" } } }] } },
                { group: { operator: "AND", children: [
                  { filter: { urlCriterion: { modifier: "INCLUDES", value: "one" } } },
                  { filter: { urlCriterion: { modifier: "INCLUDES", value: "two" } } },
                ] } },
                { filter: { titleCriterion: { modifier: "INCLUDES", value: "second" } } },
              ] } },
              { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
            ],
          },
        }}
        onApply={onApply}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    const root = screen.getAllByRole("region", { name: "All group" }).find((region) => !region.hasAttribute("data-expression-node-path"))!;
    const rootChildren = root.querySelector<HTMLElement>(":scope > [data-testid='expression-group-children']")!;
    expect(rootChildren.children[0]).toHaveTextContent("Performer Count");
    expect(rootChildren.children[1].firstElementChild).toHaveAttribute("aria-label", "Any group");
    expect(rootChildren.children[2].firstElementChild).toHaveAttribute("aria-label", "None group");

    const anyGroup = within(root).getByRole("region", { name: "Any group" });
    const anyChildren = anyGroup.querySelector<HTMLElement>(":scope > [data-testid='expression-group-children']")!;
    expect(anyChildren.children[0].firstElementChild).toHaveAttribute("aria-label", "All group");
    expect(anyChildren.children[1]).toHaveTextContent("first");
    expect(anyChildren.children[2]).toHaveTextContent("second");
    expect(anyChildren.children[3].firstElementChild).toHaveAttribute("aria-label", "None group");

    expect(root.querySelector("[data-expression-return-focus='add-']")).toHaveTextContent("");
    expect(within(screen.getAllByRole("region", { name: "None group" })[0]).getByRole("button", { name: "Add condition" })).toBeInTheDocument();

    fireEvent.click(within(root).getByRole("button", { name: "More actions for root group" }));
    fireEvent.click(screen.getByRole("button", { name: "Create subgroup" }));
    await waitFor(() => expect(within(root).getByRole("button", { name: "Select condition 1 for grouping" })).toHaveFocus());

    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          { group: { operator: "NOT", children: [{ filter: { directorCriterion: { modifier: "INCLUDES", value: "blocked" } } }] } },
          { group: { operator: "OR", children: [
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "first" } } },
            { group: { operator: "NOT", children: [{ filter: { titleCriterion: { modifier: "INCLUDES", value: "excluded" } } }] } },
            { group: { operator: "AND", children: [
              { filter: { urlCriterion: { modifier: "INCLUDES", value: "one" } } },
              { filter: { urlCriterion: { modifier: "INCLUDES", value: "two" } } },
            ] } },
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "second" } } },
          ] } },
          { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
        ],
      },
    });
  });

  it("preserves mixed simple criteria when editing a complex expression", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "OR",
            children: [
              { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
              { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
            ],
          },
          performerCountCriterion: { modifier: "EQUALS", value: 2 },
        }}
        onApply={onApply}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: /Edit condition 1: Title/ }));
    fireEvent.change(screen.getByLabelText("Value"), { target: { value: "baz" } });
    fireEvent.click(screen.getByRole("button", { name: "Save condition" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      _filterExpression: {
        operator: "AND",
        children: [
          { group: { operator: "OR", children: [
            { filter: { titleCriterion: { modifier: "INCLUDES", value: "baz" } } },
            { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
          ] } },
          { filter: { performerCountCriterion: { modifier: "EQUALS", value: 2 } } },
        ],
      },
    });
  });

  it("keeps focus inside the organizer when removing conditions", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          _filterExpression: {
            operator: "AND",
            children: [
              { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
              { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
            ],
          },
        }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));

    fireEvent.click(screen.getByRole("button", { name: "More actions for condition 1" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(screen.getByRole("button", { name: /Edit condition 1: Director/ })).toHaveFocus());
    fireEvent.click(screen.getByRole("button", { name: "More actions for condition 1" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Add condition" })).toHaveFocus());
  });

  it("offers age on video date inside a related performer condition", () => {
    const onApply = vi.fn();
    renderWithQueryClient(<FilterDialog open onClose={vi.fn()} criteria={VIDEO_CRITERIA} activeFilter={{}} onApply={onApply} />);

    fireEvent.click(screen.getByText("Related Performers"));
    fireEvent.click(screen.getByRole("tab", { name: "Age (then)" }));
    fireEvent.click(screen.getByRole("button", { name: "Between" }));
    const values = screen.getAllByRole("spinbutton");
    fireEvent.change(values[0], { target: { value: "20" } });
    fireEvent.change(values[1], { target: { value: "30" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      performerFilterCriterion: {
        ageAtHostDateCriterion: { modifier: "BETWEEN", value: 20, value2: 30 },
      },
    });
  });

  it("returns from a nested related facet before leaving an Advanced condition", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "OR", children: [
          { filter: { performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: true } } } } },
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: /Edit condition 1: At least one performer/ }));
    expect(screen.queryByRole("region", { name: "Selected filters" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Open Combine Filters" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Favorite" }));
    expect(screen.getByRole("tabpanel", { name: "Favorite" })).toBeInTheDocument();

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByRole("heading", { name: "Edit Related Performers condition" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText("Saved performer filter")).toHaveFocus());

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByRole("heading", { name: "Combine Filters" })).toBeInTheDocument();
  });

  it("removes chips from a related expression-condition draft", async () => {
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{ _filterExpression: { operator: "OR", children: [
          { filter: { performerFilterCriterion: { objectFilter: { favoriteCriterion: { value: true } } } } },
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
        ] } }}
        onApply={vi.fn()}
        supportsFilterExpressions
        initialView="advanced"
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: /Edit condition 1: At least one performer/ }));
    const draftChips = screen.getByRole("toolbar", { name: "Related Performers selected filters" });
    fireEvent.click(within(draftChips).getByRole("button", { name: "Remove performer filter: Favorite" }));

    await waitFor(() => expect(screen.queryByRole("toolbar", { name: "Related Performers selected filters" })).not.toBeInTheDocument());
    expect(screen.getByRole("heading", { name: "Edit filter condition" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save condition" })).toBeDisabled();
  });

  it("preserves page-specific filter keys when combining simple filters", () => {
    const onApply = vi.fn();
    renderWithQueryClient(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={VIDEO_CRITERIA}
        activeFilter={{
          includeCompilationGroups: { value: true },
          titleCriterion: { modifier: "INCLUDES", value: "foo" },
          directorCriterion: { modifier: "INCLUDES", value: "bar" },
        }}
        onApply={onApply}
        supportsFilterExpressions
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Combine Filters" }));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      includeCompilationGroups: { value: true },
      _filterExpression: {
        operator: "AND",
        children: [
          { filter: { titleCriterion: { modifier: "INCLUDES", value: "foo" } } },
          { filter: { directorCriterion: { modifier: "INCLUDES", value: "bar" } } },
        ],
      },
    });
  });
});
