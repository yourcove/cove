import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ListPage } from "../components/ListPage";
import { formatFilterChipValue, formatRemoteIdFilterChipValue } from "../components/ActiveObjectFilterChips";
import { TAG_CRITERIA, VIDEO_CRITERIA, type CriterionDefinition } from "../components/FilterDialog";
import { getEntityCardMinWidthPx } from "../hooks/useEntityCardSize";
import { customFieldDefinitionsQueryKey } from "../hooks/useCustomFieldDefinitions";
import { RouteRegistryProvider } from "../router/RouteRegistry";

const appConfigMock = vi.hoisted(() => ({ optional: undefined as any }));

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: { keybindingOverrides: {} } } }),
  useOptionalAppConfig: () => appConfigMock.optional,
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: null }),
}));

const storage = new Map<string, string>();

beforeEach(() => {
  appConfigMock.optional = undefined;
  storage.clear();
  window.history.replaceState(null, "", "/");
  Object.defineProperty(window, "localStorage", {
    configurable: true,
    value: {
      getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => {
        storage.set(key, value);
      },
      removeItem: (key: string) => {
        storage.delete(key);
      },
    },
  });
});

describe("ListPage active filter chips", () => {
  it("does not present a pending collection as empty", () => {
    const queryClient = new QueryClient();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            loadState={{ status: "pending" }}
          >
            <div>empty collection content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.getByRole("status", { name: "Loading Videos" })).toBeInTheDocument();
    expect(screen.getByText("Loading…")).toBeInTheDocument();
    expect(screen.queryByText("0 items")).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Videos" })).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "Search list" })).toBeInTheDocument();
    expect(screen.queryByText("empty collection content")).not.toBeInTheDocument();
  });

  it("keeps the focused search control mounted while collection results become pending", () => {
    const queryClient = new QueryClient();
    const renderListPage = (loadState: { status: "success"; data: unknown } | { status: "pending" }) => (
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={81}
            loadState={loadState}
          >
            <div>collection content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    const { rerender } = render(renderListPage({ status: "success", data: {} }));
    const search = screen.getByRole("textbox", { name: "Search list" });
    search.focus();

    rerender(renderListPage({ status: "pending" }));

    expect(screen.getByRole("textbox", { name: "Search list" })).toBe(search);
    expect(search).toHaveFocus();
    expect(screen.getByRole("status", { name: "Loading Videos" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Next page" })).not.toBeInTheDocument();
    expect(screen.queryByText("collection content")).not.toBeInTheDocument();
  });

  it("returns to the last valid page when refreshed results remove the current page", async () => {
    const queryClient = new QueryClient();
    const onFilterChange = vi.fn();
    const renderListPage = (totalCount: number) => (
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 2, perPage: 40 }}
            onFilterChange={onFilterChange}
            totalCount={totalCount}
            isLoading={false}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    const { rerender } = render(renderListPage(41));

    expect(onFilterChange).not.toHaveBeenCalled();

    rerender(renderListPage(40));

    await waitFor(() => expect(onFilterChange).toHaveBeenCalledWith({ page: 1, perPage: 40 }));
  });

  it.each([
    ["configured service names", { value: "HTTPS://SERVICE.EXAMPLE/GRAPHQL", modifier: "EQUALS" }, [{ endpoint: "https://service.example/graphql", name: "Named Service" }], "Named Service = remote-123"],
    ["any-service labels", undefined, [], "Any metadata service = remote-123"],
    ["unconfigured endpoint fallbacks", { value: "https://retired.example/graphql", modifier: "EQUALS" }, [], "https://retired.example/graphql (unconfigured) = remote-123"],
  ] as const)("formats Remote ID with %s", (_caseName, endpointCriterion, metadataServers, expected) => {
    expect(formatRemoteIdFilterChipValue(
      { value: "remote-123", modifier: "EQUALS" },
      endpointCriterion,
      metadataServers.map((server) => ({ ...server, apiKey: "", maxRequestsPerMinute: 0 })),
    )).toBe(expected);
  });

  it("groups Remote ID value and metadata service into one removable chip", async () => {
    const user = userEvent.setup();
    const onObjectFilterChange = vi.fn();
    appConfigMock.optional = {
      config: {
        ui: { ratingSystemOptions: { type: "stars", starPrecision: "full" } },
        scraping: {
          metadataServers: [{ endpoint: "https://service.example/graphql", name: "Named Service", apiKey: "", maxRequestsPerMinute: 0 }],
        },
      },
    };

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{
              remoteIdValueCriterion: { value: "remote-123", modifier: "EQUALS" },
              remoteIdCriterion: { value: "https://service.example/graphql", modifier: "EQUALS" },
            }}
            onObjectFilterChange={onObjectFilterChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    const chip = screen.getByRole("button", { name: "Edit filter: Remote ID" });
    expect(chip).toHaveTextContent("Remote ID:Named Service = remote-123");
    expect(screen.getAllByRole("button", { name: "Edit filter: Remote ID" })).toHaveLength(1);
    expect(screen.getByRole("button", { name: "Filters, 1 active" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Remove filter: Remote ID" }));
    expect(onObjectFilterChange).toHaveBeenCalledWith({});
  });

  it.each([
    [
      "hash algorithms",
      { id: "hash", label: "Hash", type: "hash", filterKey: "fingerprintCriterion", options: [{ value: "phash", label: "pHash" }] },
      { type: "phash", value: "abc123", modifier: "EQUALS" },
      "pHash = abc123",
    ],
    [
      "resolution labels",
      { id: "resolution", label: "Resolution", type: "resolution", filterKey: "resolutionCriterion" },
      { value: 2160, modifier: "GREATER_THAN" },
      "> 4K",
    ],
    [
      "single enum labels",
      { id: "orientation", label: "Orientation", type: "enum", filterKey: "orientationCriterion", options: [{ value: "landscape", label: "Landscape" }] },
      { value: "landscape", modifier: "EQUALS" },
      "= Landscape",
    ],
    [
      "multi-enum labels",
      { id: "gender", label: "Gender", type: "enum", filterKey: "genderCriterion", multiSelectOptions: true, options: [{ value: "TransgenderMale", label: "Transgender Male" }, { value: "NonBinary", label: "Non-Binary" }] },
      { value: "^(?:TransgenderMale|NonBinary)$", modifier: "MATCHES_REGEX" },
      "Any of Transgender Male or Non-Binary",
    ],
    [
      "career length units",
      { id: "careerLength", label: "Career Length", type: "careerLength", filterKey: "careerLengthCriterion" },
      { value: 2, modifier: "GREATER_THAN" },
      "> 2 years",
    ],
  ] as const)("uses picker-facing text for %s", (_caseName, definition, criterion, expected) => {
    expect(formatFilterChipValue(definition as CriterionDefinition, criterion)).toBe(expected);
  });

  it("uses natural exclusion grammar for multi-enum labels", () => {
    expect(formatFilterChipValue(
      {
        id: "gender",
        label: "Gender",
        type: "enum",
        filterKey: "genderCriterion",
        multiSelectOptions: true,
        options: [{ value: "Male", label: "Male" }, { value: "Female", label: "Female" }],
      },
      { value: "^(?:Male|Female)$", modifier: "NOT_MATCHES_REGEX", _selectedValues: ["Male", "Female"] },
    )).toBe("None of Male or Female");
  });

  it("preserves removed multi-enum values in the summary", () => {
    expect(formatFilterChipValue(
      {
        id: "gender",
        label: "Gender",
        type: "enum",
        filterKey: "genderCriterion",
        multiSelectOptions: true,
        options: [{ value: "Male", label: "Male" }],
      },
      { value: "^(?:Male|RetiredValue)$", modifier: "MATCHES_REGEX", _selectedValues: ["Male", "RetiredValue"] },
    )).toBe("Any of Male or RetiredValue");
  });

  it("opens and removes a legacy endpoint-only Remote ID filter", async () => {
    const user = userEvent.setup();
    const onObjectFilterChange = vi.fn();
    appConfigMock.optional = {
      config: {
        ui: { ratingSystemOptions: { type: "stars", starPrecision: "full" } },
        scraping: { metadataServers: [] },
      },
    };

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{ remoteIdCriterion: { value: "https://retired.example/graphql", modifier: "NOT_NULL" } }}
            onObjectFilterChange={onObjectFilterChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Edit filter: Remote ID" }));
    expect(screen.getByRole("tabpanel", { name: "Remote ID" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Metadata Service" })).toHaveValue("https://retired.example/graphql");

    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await user.click(screen.getByRole("button", { name: "Remove filter: Remote ID" }));
    expect(onObjectFilterChange).toHaveBeenCalledWith({});
  });

  it.each([
    ["single included value", { value: [1], modifier: "INCLUDES_ALL" }, "Tag One"],
    ["included alternatives", { value: [1, 2], modifier: "INCLUDES" }, "Tag One or Tag Two"],
    ["single excluded value", { value: [], excludes: [3], modifier: "INCLUDES_ALL" }, "not Tag Three"],
    ["three excluded values", { value: [], excludes: [1, 2, 3], modifier: "INCLUDES_ALL" }, "none of Tag One, Tag Two, or Tag Three"],
    ["legacy excluded values", { value: [1, 2], modifier: "EXCLUDES" }, "neither Tag One nor Tag Two"],
    ["legacy exclude-all values", { value: [1, 2], modifier: "EXCLUDES_ALL" }, "not all of Tag One and Tag Two"],
  ])("formats natural multi-value grammar for a %s", (_caseName, criterion, expected) => {
    expect(formatFilterChipValue(
      { id: "tags", label: "Tags", type: "multiId", entityType: "tags", filterKey: "tagsCriterion" },
      criterion,
      new Map([[1, "Tag One"], [2, "Tag Two"], [3, "Tag Three"]]),
    )).toBe(expected);
  });

  it("shows a load error instead of interpreting it as an empty collection", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient();
    const onRetry = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            loadState={{ status: "error", error: new Error("Request failed: 502 Bad Gateway"), retry: onRetry }}
          >
            <div>empty collection content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.getByText("Could not load Videos")).toBeInTheDocument();
    expect(screen.getByText("Cove couldn’t complete the request. Please try again.")).toBeInTheDocument();
    expect(screen.queryByText("Request failed: 502 Bad Gateway")).not.toBeInTheDocument();
    expect(screen.queryByText("empty collection content")).not.toBeInTheDocument();
    expect(screen.getByText("Unavailable")).toBeInTheDocument();
    expect(screen.queryByText("0 items")).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Videos" })).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "Search list" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Try again" }));
    expect(onRetry).toHaveBeenCalledOnce();
  });

  it("formats criterion chips with human labels and modifiers", () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    queryClient.setQueryData(["tags", "all"], [
      { id: 1, name: "Tag One" },
      { id: 2, name: "Tag Two" },
      { id: 3, name: "Tag Three" },
      { id: 8, name: "Excluded One" },
      { id: 9, name: "Excluded Two" },
    ]);
    queryClient.setQueryData(["performers", "all"], [
      { id: 4, name: "Performer One" },
      { id: 5, name: "Performer Two" },
      { id: 6, name: "Performer Three" },
    ]);
    queryClient.setQueryData(["studios", "all"], [{ id: 7, name: "Studio One" }]);

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{
              ratingCriterion: { value: 80, modifier: "GREATER_THAN" },
              tagsCriterion: { value: [1, 2, 3], excludes: [8, 9], modifier: "INCLUDES_ALL", depth: -1 },
              performersCriterion: { value: [4, 5, 6], modifier: "INCLUDES" },
              studiosCriterion: { value: [7], modifier: "EXCLUDES" },
              durationCriterion: { value: 600, modifier: "LESS_THAN" },
            }}
            onObjectFilterChange={vi.fn()}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    const ratingChip = screen.getByRole("button", { name: "Edit filter: Rating" });
    expect(ratingChip).toHaveTextContent("Rating:>");
    expect(ratingChip).toHaveAttribute("title", "Rating: > 4 stars");
    expect(ratingChip.querySelectorAll("[data-rating-stars]")).toHaveLength(1);

    const tagsChip = screen.getByRole("button", { name: "Edit filter: Tags" });
    expect(tagsChip).toHaveTextContent("Tags:");
    expect(tagsChip).toHaveTextContent("Tag One, Tag Two, and Tag Three but neither Excluded One nor Excluded Two with sub-tags");
    expect(tagsChip).toHaveAttribute("title", "Tags: Tag One, Tag Two, and Tag Three but neither Excluded One nor Excluded Two with sub-tags");
    expect(tagsChip.querySelectorAll('[data-filter-value-kind="included"]')).toHaveLength(3);
    expect(tagsChip.querySelectorAll('[data-filter-value-kind="excluded"]')).toHaveLength(2);
    expect(tagsChip.querySelector('[data-filter-value-kind="included"]')).toHaveClass("text-green-300");
    expect(tagsChip.querySelector('[data-filter-value-kind="excluded"]')).toHaveClass("text-red-300");
    expect(tagsChip.querySelector('[data-filter-value-kind="included"]')).not.toHaveClass("border", "rounded", "bg-green-900/40");
    expect(tagsChip.querySelector('[data-filter-value-kind="excluded"]')).not.toHaveClass("border", "rounded", "bg-red-900/40");
    expect(tagsChip.querySelector('[data-filter-excluded-prefix]')?.textContent).toBe("neither ");
    expect(screen.getByRole("button", { name: "Edit filter: Performers" })).toHaveTextContent("Performer One, Performer Two, or Performer Three");
    const studiosChip = screen.getByRole("button", { name: "Edit filter: Studios" });
    expect(studiosChip).toHaveTextContent("Studios:not Studio One");
    expect(studiosChip.querySelector('[data-filter-value-kind="excluded"]')).toHaveClass("text-red-300");
    expect(screen.getByRole("button", { name: "Edit filter: Duration" })).toHaveTextContent("< 10 min");
  });

  it("renders empty and non-empty entity relations as None and Any", () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(["tags", "all"], []);
    queryClient.setQueryData(["performers", "all"], []);
    queryClient.setQueryData(["studios", "all"], []);

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{
              tagsCriterion: { modifier: "IS_NULL" },
              performersCriterion: { modifier: "NOT_NULL" },
              studiosCriterion: { modifier: "IS_NULL" },
            }}
            onObjectFilterChange={vi.fn()}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    expect(screen.getByRole("button", { name: "Edit filter: Tags" })).toHaveTextContent("Tags:None");
    expect(screen.getByRole("button", { name: "Edit filter: Performers" })).toHaveTextContent("Performers:Any");
    expect(screen.getByRole("button", { name: "Edit filter: Studios" })).toHaveTextContent("Studios:None");
  });

  it("opens the filter dialog at the clicked applied criterion without removing it", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const onFilterChange = vi.fn();
    const onObjectFilterChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 3, perPage: 40 }}
            onFilterChange={onFilterChange}
            totalCount={120}
            isLoading={false}
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{ titleCriterion: { value: "example", modifier: "EQUALS" } }}
            onObjectFilterChange={onObjectFilterChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    await user.click(screen.getByRole("button", { name: "Edit filter: Title" }));

    expect(screen.getByRole("dialog", { name: "Filters" })).toBeInTheDocument();
    expect(screen.getByRole("tabpanel", { name: "Title" })).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "Value" })).toHaveValue("example");
    expect(onObjectFilterChange).not.toHaveBeenCalled();
    expect(onFilterChange).not.toHaveBeenCalled();
  });

  it("uses horizontal arrows for match modes without paging behind the filter dialog", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(["multi-id-selector", "tags", ""], []);
    const onFilterChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 2, perPage: 40 }}
            onFilterChange={onFilterChange}
            totalCount={120}
            isLoading={false}
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{}}
            onObjectFilterChange={vi.fn()}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Filters" }));
    await user.click(screen.getByRole("tab", { name: "Tags" }));
    const includesAll = screen.getByRole("button", { name: "Includes All" });
    includesAll.focus();
    onFilterChange.mockClear();

    await user.keyboard("{ArrowRight}");

    expect(screen.getByRole("button", { name: "Includes", pressed: true })).toHaveFocus();
    expect(onFilterChange).not.toHaveBeenCalled();

    await user.keyboard("{ArrowLeft}");

    expect(screen.getByRole("button", { name: "Includes All", pressed: true })).toHaveFocus();
    expect(onFilterChange).not.toHaveBeenCalled();
  });

  it("opens an auxiliary filter chip at its owning criterion", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Tags"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            criteriaDefinitions={TAG_CRITERIA}
            objectFilter={{ videoCountIncludesChildren: true }}
            onObjectFilterChange={vi.fn()}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    const chip = screen.getByRole("button", { name: "Edit filter: Count videos from child tags" });
    expect(chip).toHaveTextContent("Yes");
    await user.click(chip);

    expect(screen.getByText("Count videos from child tags")).toBeInTheDocument();
  });

  it("formats tag duration chips with tag names and time values", () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    queryClient.setQueryData(["tags", "all"], [
      { id: 1, name: "Tag One" },
    ]);

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{
              tagDurationCriterion: { clauses: [{ tagId: 1, value: 90, modifier: "GREATER_THAN", unit: "seconds" }] },
            }}
            onObjectFilterChange={vi.fn()}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.getByRole("button", { name: "Edit filter: Tag Duration" })).toHaveTextContent("Tag Duration:");
    expect(screen.getByRole("button", { name: "Edit filter: Tag Duration" })).toHaveTextContent("Tag One");
    expect(screen.getByRole("button", { name: "Edit filter: Tag Duration" })).toHaveTextContent("> 1 min 30 sec");
  });

  it("sorts sort options alphabetically in the toolbar", () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            sortOptions={[
              { value: "updated_at", label: "Updated At" },
              { value: "title", label: "Title" },
              { value: "bitrate", label: "Bitrate" },
            ]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    const [sortSelect] = screen.getAllByRole("combobox");
    expect(Array.from((sortSelect as HTMLSelectElement).options).map((option) => option.text)).toEqual([
      "Bitrate",
      "Title",
      "Updated At",
    ]);
  });

  it("restores saved per-page and zoom preferences for a page key", async () => {
    localStorage.setItem("cove-list-prefs-videos", JSON.stringify({ perPage: 120, zoomLevel: 2 }));
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    const onFilterChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            pageKey="videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={onFilterChange}
            totalCount={0}
            isLoading={false}
            displayMode="grid"
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    await waitFor(() => {
      expect(onFilterChange).toHaveBeenCalledWith(expect.objectContaining({ page: 1, perPage: 120 }));
    });

    expect(screen.getByRole("slider")).toHaveValue("2");
  });

  it("applies saved-filter display and zoom options from the page default", async () => {
    localStorage.setItem("cove-default-filter-videos", JSON.stringify({
      findFilter: { page: 1, perPage: 40 },
      uiOptions: { displayMode: "list", zoomLevel: 5.25 },
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const onDisplayModeChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            pageKey="videos"
            filterMode="videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            displayMode="grid"
            onDisplayModeChange={onDisplayModeChange}
            availableDisplayModes={["grid", "list"]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    await waitFor(() => expect(screen.getByRole("slider")).toHaveValue("5.25"));
    expect(onDisplayModeChange).toHaveBeenCalledWith("list");
    expect(localStorage.getItem("cove.cardSize.video")).toBe("5.25");
  });

  it("applies each saved default zoom when a reused page changes filter modes", async () => {
    localStorage.setItem("cove-default-filter-segments", JSON.stringify({
      findFilter: { page: 1, perPage: 40 },
      uiOptions: { zoomLevel: 2.75 },
    }));
    localStorage.setItem("cove-default-filter-rawsegments", JSON.stringify({
      findFilter: { page: 1, perPage: 40 },
      uiOptions: { zoomLevel: 8 },
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const commonProps = {
      title: "Segments",
      pageKey: "segments",
      filter: { page: 1, perPage: 40 },
      onFilterChange: vi.fn(),
      totalCount: 0,
      isLoading: false,
      displayMode: "grid" as const,
    };

    const { rerender } = render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage {...commonProps} filterMode="segments"><div>content</div></ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByRole("slider")).toHaveValue("2.75"));

    rerender(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage {...commonProps} filterMode="rawsegments"><div>content</div></ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByRole("slider")).toHaveValue("8"));
    expect(localStorage.getItem("cove.cardSize.rawsegments")).toBe("8");
  });

  it("supports an extension saved-filter scope independently from entity filtering and card sizing", async () => {
    localStorage.setItem("cove-default-filter-ext:com.example.tools:missing-videos", JSON.stringify({
      findFilter: { page: 1, perPage: 40 },
      uiOptions: { zoomLevel: 2.5 },
    }));

    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RouteRegistryProvider>
          <ListPage
            title="Missing Videos"
            pageKey="complete-the-cove-missing-videos"
            savedFilterScope="ext:com.example.tools:missing-videos"
            cardSizeEntityType="video"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            displayMode="grid"
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    expect(screen.getByTitle("Saved filters")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Filters/ })).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("slider")).toHaveValue("2.5"));
    expect(localStorage.getItem("cove.cardSize.video")).toBe("2.5");
  });

  it("ignores invalid saved-filter UI options", async () => {
    localStorage.setItem("cove.cardSize.video", "2");
    localStorage.setItem("cove-default-filter-videos", JSON.stringify({
      findFilter: { page: 1, perPage: 40 },
      uiOptions: { displayMode: "vertical", zoomLevel: "large" },
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const onDisplayModeChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            pageKey="videos"
            filterMode="videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            displayMode="grid"
            onDisplayModeChange={onDisplayModeChange}
            availableDisplayModes={["grid", "list"]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    await waitFor(() => expect(screen.getByRole("slider")).toHaveValue("2"));
    expect(onDisplayModeChange).not.toHaveBeenCalled();
    expect(localStorage.getItem("cove.cardSize.video")).toBe("2");
  });

  it("clamps a saved-filter zoom level to the entity card-size range", async () => {
    localStorage.setItem("cove-default-filter-videos", JSON.stringify({
      findFilter: { page: 1, perPage: 40 },
      uiOptions: { zoomLevel: 99 },
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            pageKey="videos"
            filterMode="videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            displayMode="grid"
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    await waitFor(() => expect(screen.getByRole("slider")).toHaveValue("8"));
    expect(localStorage.getItem("cove.cardSize.video")).toBe("8");
  });

  it("keeps an explicit URL display mode while applying the default zoom", async () => {
    window.history.replaceState(null, "", "/videos?view=grid");
    localStorage.setItem("cove-default-filter-videos", JSON.stringify({
      findFilter: { page: 1, perPage: 40 },
      uiOptions: { displayMode: "list", zoomLevel: 5.25 },
    }));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const onDisplayModeChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            pageKey="videos"
            filterMode="videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            displayMode="grid"
            onDisplayModeChange={onDisplayModeChange}
            availableDisplayModes={["grid", "list"]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    await waitFor(() => expect(screen.getByRole("slider")).toHaveValue("5.25"));
    expect(onDisplayModeChange).not.toHaveBeenCalled();
  });

  it("allows the global images card-size slider to grow larger than the default max", () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Images"
            pageKey="images"
            filterMode="images"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            displayMode="grid"
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.getByRole("slider")).toHaveAttribute("max", "8");
  });

  it("keeps gallery card sizes on the same width scale as image cards", () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Galleries"
            pageKey="galleries"
            filterMode="galleries"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            displayMode="grid"
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.getByRole("slider")).toHaveAttribute("max", "8");
    expect(getEntityCardMinWidthPx("images", 0)).toBe(225);
    expect(getEntityCardMinWidthPx("galleries", 0)).toBe(225);
    expect(getEntityCardMinWidthPx("images", 8)).toBe(625);
    expect(getEntityCardMinWidthPx("galleries", 8)).toBe(625);
    expect(getEntityCardMinWidthPx("images", 1)).toBe(getEntityCardMinWidthPx("galleries", 1));
  });

  it("uses the same 225-625 global width scale for other entities", () => {
    expect(getEntityCardMinWidthPx("videos", 0)).toBe(225);
    expect(getEntityCardMinWidthPx("performers", 8)).toBe(625);
    expect(getEntityCardMinWidthPx("audios", 8)).toBe(625);
  });

  it("preserves the random seed when toggling list sort direction", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    const onFilterChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40, sort: "random", direction: "desc", seed: 12345 }}
            onFilterChange={onFilterChange}
            totalCount={0}
            isLoading={false}
            sortOptions={[{ value: "random", label: "Random" }]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    const directionButton = screen.getByRole("button", { name: "Sort descending" });
    const shuffleButton = screen.getByRole("button", { name: "Shuffle" });
    expect(directionButton.compareDocumentPosition(shuffleButton) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);

    await user.click(directionButton);

    expect(onFilterChange).toHaveBeenCalledWith(expect.objectContaining({ sort: "random", direction: "asc", seed: 12345 }));
  });

  it("keeps one sort compact and progressively reveals ordered additional sorts", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    const onFilterChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40, sort: "studio", direction: "asc" }}
            onFilterChange={onFilterChange}
            totalCount={0}
            isLoading={false}
            sortOptions={[
              { value: "date", label: "Date" },
              { value: "studio", label: "Studio" },
              { value: "title", label: "Title" },
            ]}
            multiSortKeys={["date", "studio", "title"]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.queryByRole("dialog", { name: "Sort order" })).not.toBeInTheDocument();
    const primaryDirectionButton = screen.getByRole("button", { name: "Sort ascending" });
    const addSortButton = screen.getByRole("button", { name: "Add another sort" });
    expect(primaryDirectionButton.compareDocumentPosition(addSortButton) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);

    await user.click(addSortButton);
    expect(screen.getByRole("dialog", { name: "Sort order" })).toBeInTheDocument();
    expect(screen.queryByText("Earlier fields take priority.")).not.toBeInTheDocument();
    expect(screen.getByText("1.")).toBeInTheDocument();
    const directionButton = screen.getByRole("button", { name: "Studio ascending" });
    const moveEarlierButton = screen.getByRole("button", { name: "Move Studio earlier" });
    expect(directionButton).not.toHaveTextContent(/Asc|Desc/);
    expect(moveEarlierButton.compareDocumentPosition(directionButton) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);

    await user.click(screen.getByRole("button", { name: "Add sort" }));

    expect(onFilterChange).toHaveBeenCalledWith(expect.objectContaining({
      page: 1,
      sort: "studio",
      direction: "asc",
      sorts: [
        { key: "studio", direction: "asc" },
        { key: "date", direction: "desc" },
      ],
    }));
  });

  it("lets compact sort editors change a clause's priority directly", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    const onFilterChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{
              page: 1,
              perPage: 40,
              sort: "studio",
              direction: "asc",
              sorts: [
                { key: "studio", direction: "asc" },
                { key: "date", direction: "desc" },
                { key: "title", direction: "asc" },
              ],
            }}
            onFilterChange={onFilterChange}
            totalCount={0}
            isLoading={false}
            sortOptions={[
              { value: "date", label: "Date" },
              { value: "studio", label: "Studio" },
              { value: "title", label: "Title" },
            ]}
            multiSortKeys={["date", "studio", "title"]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.queryByRole("combobox", { name: "Primary sort" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Sort ascending" })).not.toBeInTheDocument();
    const summaryButton = screen.getByRole("button", {
      name: "Edit sort order: 1. Studio ascending; 2. Date descending; 3. Title ascending",
    });
    expect(summaryButton).toHaveTextContent("1. Studio");
    expect(summaryButton).toHaveTextContent("2. Date");
    expect(summaryButton).toHaveTextContent("+1");
    expect(summaryButton).not.toHaveTextContent("Title");

    await user.click(summaryButton);
    await user.selectOptions(screen.getByRole("combobox", { name: "Priority for Date" }), "0");

    expect(onFilterChange).toHaveBeenCalledWith(expect.objectContaining({
      sort: "date",
      direction: "desc",
      sorts: [
        { key: "date", direction: "desc" },
        { key: "studio", direction: "asc" },
        { key: "title", direction: "asc" },
      ],
    }));
  });

  it("shows the scalar sort when compound sorting is unavailable", () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{
              page: 1,
              perPage: 40,
              sort: "visual_match",
              direction: "desc",
              sorts: [
                { key: "studio", direction: "asc" },
                { key: "date", direction: "desc" },
              ],
            }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            sortOptions={[
              { value: "visual_match", label: "Visual Match" },
              { value: "date", label: "Date" },
              { value: "studio", label: "Studio" },
            ]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.getByRole("combobox", { name: "Primary sort" })).toHaveValue("visual_match");
    expect(screen.queryByRole("button", { name: /Edit sort order:/ })).not.toBeInTheDocument();
  });

  it("shows a shuffle button for random sort and replaces the seed", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    const onFilterChange = vi.fn();
    vi.spyOn(Math, "random").mockReturnValue(0.5);

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 3, perPage: 40, sort: "random", direction: "desc", seed: 12345 }}
            onFilterChange={onFilterChange}
            totalCount={0}
            isLoading={false}
            sortOptions={[{ value: "random", label: "Random" }]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    await user.click(screen.getByTitle("Shuffle"));

    expect(onFilterChange).toHaveBeenCalledWith(expect.objectContaining({ sort: "random", page: 1, seed: 1073741823 }));
  });

  it("uses the wide filter dialog layout for custom reference field filters", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });
    queryClient.setQueryData(customFieldDefinitionsQueryKey("video"), [
      {
        id: 1,
        key: "testlabel",
        label: "testlabel",
        type: "tag",
        entityTypes: ["video"],
        options: [],
        filterable: true,
        sortable: false,
        isMultiValue: false,
        displayOrder: 0,
        createdAt: "2026-05-09T00:00:00Z",
        updatedAt: "2026-05-09T00:00:00Z",
      },
    ]);

    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            filterMode="videos"
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{}}
            onObjectFilterChange={vi.fn()}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    await user.click(screen.getByRole("button", { name: "Filters" }));
    const dialogShell = Array.from(container.querySelectorAll("div"))
      .find((element) => element.className.includes("md:w-[min(94vw,72rem)]"));
    expect(dialogShell).toBeTruthy();

    await user.click(screen.getByText("Custom Fields"));
    await user.click(screen.getByRole("button", { name: /add custom field filter/i }));

    expect(screen.getByPlaceholderText("Search tags...").closest("label")?.className).toContain("min-w-0");
    expect(container.querySelector('[aria-label="Remove custom field filter"]')?.parentElement?.className).toContain("xl:grid-cols");
  });
});
