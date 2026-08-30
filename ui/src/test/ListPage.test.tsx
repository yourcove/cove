import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
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
  it("keeps the top-level wall size control mapped to wall columns", () => {
    const queryClient = new QueryClient();
    const onWallColumnCountChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            displayMode="wall"
            wallColumnCount={5}
            onWallColumnCountChange={onWallColumnCountChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    const slider = screen.getByRole("slider", { name: "Wall card size" });
    expect(slider).toHaveValue("5");
    expect(screen.getByText("5 cols")).toBeInTheDocument();

    fireEvent.change(slider, { target: { value: "8" } });

    expect(onWallColumnCountChange).toHaveBeenCalledWith(2);
  });

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

  it("opens and removes one parameter inside a related-performer filter group", async () => {
    const user = userEvent.setup();
    const onObjectFilterChange = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    queryClient.setQueryData(["saved-filters", "performers"], []);
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
              performerFilterCriterion: {
                findFilter: { q: "example" },
                objectFilter: {
                  favoriteCriterion: { value: true },
                  ratingCriterion: { value: 100, modifier: "EQUALS" },
                },
              },
            }}
            onObjectFilterChange={onObjectFilterChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    const group = screen.getByRole("group", { name: "Related Performers filters" });
    expect(group.querySelectorAll(".lucide-users")).toHaveLength(3);
    await user.click(screen.getByRole("button", { name: "Edit performer filter: Favorite" }));
    expect(screen.getByRole("dialog", { name: "Filters / Related Performers" })).toBeInTheDocument();
    expect(screen.getByRole("tabpanel", { name: "Favorite" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await user.click(screen.getByRole("button", { name: "Remove performer filter: Favorite" }));
    expect(onObjectFilterChange).toHaveBeenCalledWith({
      performerFilterCriterion: {
        findFilter: { q: "example" },
        objectFilter: { ratingCriterion: { value: 100, modifier: "EQUALS" } },
      },
    });
  });

  it("shows advanced group operators and each related-performer condition", () => {
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
              _filterExpression: {
                operator: "AND",
                children: [
                  { filter: { performerFilterCriterion: { objectFilter: { genderCriterion: { value: "^(?:Male)$", modifier: "MATCHES_REGEX", _selectedValues: ["Male"] } }, ageAtHostDateCriterion: { modifier: "BETWEEN", value: 20, value2: 30 } } } },
                  { filter: { performerFilterCriterion: { objectFilter: { genderCriterion: { value: "^(?:Female)$", modifier: "MATCHES_REGEX", _selectedValues: ["Female"] } }, ageAtHostDateCriterion: { modifier: "BETWEEN", value: 30, value2: 40 } } } },
                ],
              },
            }}
            onObjectFilterChange={vi.fn()}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    expect(document.querySelector('[data-filter-operator="AND"]')).toBeInTheDocument();
    expect(screen.getAllByLabelText("Related Performers condition")).toHaveLength(2);
    expect(screen.getByText("Male")).toBeInTheDocument();
    expect(screen.getByText("Between 20 and 30")).toBeInTheDocument();
    expect(screen.getByText("Female")).toBeInTheDocument();
    expect(screen.getByText("Between 30 and 40")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Edit filter: Advanced filter\. AND group: Related Performers, Age \(then\) Between 20 and 30, Gender Male; Related Performers, Age \(then\) Between 30 and 40, Gender Female/ })).toBeInTheDocument();
  });

  it("resolves entity names and exposes nested expression operators", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    queryClient.setQueryData(["tags", "all"], [{ id: 42, name: "Example Tag" }]);
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
              _filterExpression: {
                operator: "OR",
                children: [
                  { filter: { tagsCriterion: { modifier: "INCLUDES", value: [42] } } },
                  { group: { operator: "AND", children: [
                    { filter: { urlCriterion: { modifier: "INCLUDES", value: "foo" } } },
                    { filter: { urlCriterion: { modifier: "EXCLUDES", value: "bar" } } },
                  ] } },
                ],
              },
            }}
            onObjectFilterChange={vi.fn()}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    expect(await screen.findByText("Example Tag")).toBeInTheDocument();
    expect(document.querySelector('[data-filter-operator="OR"]')).toBeInTheDocument();
    expect(document.querySelector('[data-filter-operator="AND"]')).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /OR group: Tags Example Tag; AND group: URL Includes foo; URL Excludes bar/ })).toBeInTheDocument();
  });

  it("normalizes the legacy performer-favorite chip before editing or removing it", async () => {
    const user = userEvent.setup();
    const onObjectFilterChange = vi.fn();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    queryClient.setQueryData(["saved-filters", "performers"], []);
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
            objectFilter={{ performerFavoriteCriterion: { value: true } }}
            onObjectFilterChange={onObjectFilterChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    expect(screen.queryByText("performerFavoriteCriterion")).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Edit performer filter: Favorite" }));
    expect(screen.getByRole("tabpanel", { name: "Favorite" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await user.click(screen.getByRole("button", { name: "Remove performer filter: Favorite" }));
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
      "Transgender Male or Non-Binary",
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
    )).toBe("Male or RetiredValue");
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
    expect(tagsChip.textContent).toContain("Tag Two,\u00a0and Tag Three");
    expect(tagsChip.textContent).toContain("Excluded One\u00a0nor Excluded Two");
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

  it("uses relevance for a new search and restores the previous sort when cleared", async () => {
    vi.useFakeTimers();
    const queryClient = new QueryClient();
    const onFilterChange = vi.fn();
    const renderPage = (filter: { page: number; perPage: number; sort: string; direction: "asc" | "desc"; q?: string }) => (
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            pageKey="videos"
            filter={filter}
            onFilterChange={onFilterChange}
            totalCount={0}
            isLoading={false}
            sortOptions={[{ value: "date", label: "Date" }]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    const { rerender } = render(renderPage({ page: 3, perPage: 40, sort: "date", direction: "desc" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Search list" }), { target: { value: "needle" } });
    await vi.advanceTimersByTimeAsync(350);

    const searchedFilter = onFilterChange.mock.lastCall?.[0];
    expect(searchedFilter).toEqual(expect.objectContaining({ q: "needle", page: 1, sort: "relevance", direction: "desc" }));

    rerender(renderPage(searchedFilter));
    expect(screen.getByRole("combobox", { name: "Primary sort" })).toHaveValue("relevance");
    expect(screen.queryByRole("button", { name: /Sort (ascending|descending)/ })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Clear search" }));

    expect(onFilterChange.mock.lastCall?.[0]).toEqual(expect.objectContaining({ q: undefined, page: 1, sort: "date", direction: "desc" }));
    vi.useRealTimers();
  });

  it("restores a valid fallback when clearing a deep-linked relevance search", () => {
    const queryClient = new QueryClient();
    const onFilterChange = vi.fn();
    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            pageKey="videos"
            filter={{ page: 1, perPage: 40, q: "needle", sort: "relevance", direction: "desc" }}
            onFilterChange={onFilterChange}
            totalCount={0}
            isLoading={false}
            sortOptions={[{ value: "date", label: "Date" }]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    fireEvent.click(screen.getByRole("button", { name: "Clear search" }));

    expect(onFilterChange).toHaveBeenCalledWith(expect.objectContaining({ q: undefined, sort: "date", direction: "desc" }));
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

    await user.click(screen.getAllByText("Custom Fields").at(-1)!);
    await user.click(screen.getByRole("button", { name: /add custom field filter/i }));

    expect(screen.getByPlaceholderText("Search tags...").closest("label")?.className).toContain("min-w-0");
    expect(container.querySelector('[aria-label="Remove custom field filter"]')?.parentElement?.className).toContain("xl:grid-cols");
  });

  it("expands configured JSON paths into typed filter and sort targets", async () => {
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
        id: 2,
        key: "structured_metadata",
        label: "Structured Metadata",
        type: "json",
        entityTypes: ["video"],
        options: [],
        filterable: false,
        sortable: false,
        isMultiValue: false,
        jsonPaths: [
          {
            path: "/profile/score",
            label: "Score",
            type: "number",
            filterable: true,
            sortable: true,
          },
          {
            path: "/profile/filter-only",
            label: "Filter only",
            type: "text",
            filterable: true,
            sortable: false,
          },
          {
            path: "/profile/sort-only",
            label: "Sort only",
            type: "boolean",
            filterable: false,
            sortable: true,
          },
          {
            path: "/profile/disabled",
            label: "Disabled",
            type: "text",
            filterable: false,
            sortable: false,
          },
        ],
        displayOrder: 0,
      },
      {
        id: 3,
        key: "unindexed_metadata",
        label: "Unindexed Metadata",
        type: "json",
        entityTypes: ["video", "audio"],
        options: [],
        filterable: false,
        sortable: false,
        isMultiValue: false,
        jsonPaths: [],
        displayOrder: 10,
      },
    ]);
    const onObjectFilterChange = vi.fn();

    render(
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
            onObjectFilterChange={onObjectFilterChange}
            sortOptions={[]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    expect(screen.getByRole("option", { name: "Custom: Structured Metadata › Score" })).toHaveValue(
      "custom-json:number:structured_metadata:%2Fprofile%2Fscore",
    );
    expect(screen.getByRole("option", { name: "Custom: Structured Metadata › Sort only" })).toHaveValue(
      "custom-json:boolean:structured_metadata:%2Fprofile%2Fsort-only",
    );
    expect(screen.queryByRole("option", { name: "Custom: Structured Metadata › Filter only" })).not.toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Custom: Structured Metadata › Disabled" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Filters" }));
    await user.click(screen.getByText("Custom Fields"));
    await user.click(screen.getByRole("button", { name: /add custom field filter/i }));
    expect(screen.getByRole("option", { name: "Structured Metadata" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Unindexed Metadata" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Score" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Presence" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Filter only" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Sort only" })).not.toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Disabled" })).not.toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Field" })).toHaveValue("structured_metadata");
    expect(screen.getByRole("combobox", { name: "Target" })).toHaveValue("structured_metadata");
    expect(screen.getByRole("combobox", { name: "Match" })).toHaveValue("NOT_NULL");
    await user.selectOptions(
      screen.getByRole("combobox", { name: "Target" }),
      "structured_metadata:%2Fprofile%2Fscore",
    );
    await user.type(screen.getByRole("spinbutton", { name: "Value" }), "15");
    await user.click(screen.getByRole("button", { name: /add custom field filter/i }));
    await user.selectOptions(
      screen.getAllByRole("combobox", { name: "Target" })[1],
      "structured_metadata:%2Fprofile%2Ffilter-only",
    );
    await user.type(screen.getByRole("textbox", { name: "Value" }), "ready");
    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onObjectFilterChange).toHaveBeenCalledWith(expect.objectContaining({
      customFieldCriteria: [
        expect.objectContaining({
          key: "structured_metadata",
          jsonPath: "/profile/score",
          type: "number",
          value: "15",
        }),
        expect.objectContaining({
          key: "structured_metadata",
          jsonPath: "/profile/filter-only",
          type: "text",
          value: "ready",
        }),
      ],
    }));
  });

  it("filters for JSON custom field presence without configured paths", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(customFieldDefinitionsQueryKey("video"), [{
      id: 2,
      key: "structured_metadata",
      label: "Structured Metadata",
      type: "json",
      entityTypes: ["video"],
      options: [],
      filterable: false,
      sortable: false,
      isMultiValue: false,
      jsonPaths: [],
      displayOrder: 0,
    }]);
    const onObjectFilterChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            filterMode="videos"
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{}}
            onObjectFilterChange={onObjectFilterChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Filters" }));
    await user.click(screen.getByText("Custom Fields"));
    await user.click(screen.getByRole("button", { name: /add custom field filter/i }));
    expect(screen.getByRole("combobox", { name: "Field" })).toHaveValue("structured_metadata");
    expect(screen.getByRole("combobox", { name: "Match" })).toHaveValue("NOT_NULL");
    expect(screen.getByRole("option", { name: "Not Null" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Is Null" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Equals" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onObjectFilterChange).toHaveBeenCalledWith(expect.objectContaining({
      customFieldCriteria: [expect.objectContaining({
        key: "structured_metadata",
        type: "json",
        modifier: "NOT_NULL",
        jsonPath: undefined,
      })],
    }));
  });

  it("filters for long-text custom field presence without exposing content comparisons", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(customFieldDefinitionsQueryKey("video"), [{
      id: 3,
      key: "notes",
      label: "Notes",
      type: "longText",
      entityTypes: ["video"],
      options: [],
      filterable: false,
      sortable: false,
      isMultiValue: false,
      jsonPaths: [],
      displayOrder: 0,
    }]);
    const onObjectFilterChange = vi.fn();

    render(
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
            onObjectFilterChange={onObjectFilterChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Filters" }));
    await user.click(screen.getByText("Custom Fields"));
    await user.click(screen.getByRole("button", { name: /add custom field filter/i }));
    expect(screen.getByRole("combobox", { name: "Field" })).toHaveValue("notes");
    expect(screen.getByRole("combobox", { name: "Match" })).toHaveValue("NOT_NULL");
    expect(screen.getByRole("option", { name: "Not Null" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Is Null" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "Equals" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onObjectFilterChange).toHaveBeenCalledWith(expect.objectContaining({
      customFieldCriteria: [expect.objectContaining({
        key: "notes",
        type: "longText",
        modifier: "NOT_NULL",
      })],
    }));
  });

  it.each([
    {
      state: "revoked paths",
      definitions: [{
        id: 2,
        key: "structured_metadata",
        label: "Structured Metadata",
        type: "json" as const,
        entityTypes: ["video" as const],
        options: [],
        filterable: false,
        sortable: false,
        isMultiValue: false,
        jsonPaths: [{ path: "/profile/score", label: "Score", type: "number" as const, filterable: false, sortable: false }],
        displayOrder: 0,
      }],
      fieldDisabled: false,
      addDisabled: false,
    },
    {
      state: "deleted fields",
      definitions: [],
      fieldDisabled: true,
      addDisabled: true,
    },
  ])("keeps JSON filter and sort targets visible as unavailable for $state", async ({ definitions, fieldDisabled, addDisabled }) => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(customFieldDefinitionsQueryKey("video"), definitions);
    const staleSort = "custom-json:number:structured_metadata:%2Fprofile%2Fscore";

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40, sort: staleSort, direction: "asc" }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            filterMode="videos"
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{
              customFieldCriteria: [{
                key: "structured_metadata",
                jsonPath: "/profile/score",
                type: "number",
                modifier: "GREATER_THAN",
                value: "10",
              }],
            }}
            onObjectFilterChange={vi.fn()}
            sortOptions={[{ value: "updated_at", label: "Updated" }]}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    expect(screen.getByRole("combobox", { name: "Primary sort" })).toHaveValue(staleSort);
    expect(screen.getByRole("option", { name: "Unavailable custom sort: structured_metadata › /profile/score" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /Filters/ }));
    await user.click(screen.getAllByText("Custom Fields").at(-1)!);

    expect(screen.getByRole("combobox", { name: "Field" })).toHaveValue("structured_metadata");
    expect(screen.getByRole("option", { name: fieldDisabled ? "structured_metadata" : "Structured Metadata" })).toHaveProperty("disabled", fieldDisabled);
    expect(screen.getByRole("combobox", { name: "Target" })).toHaveValue("structured_metadata:%2Fprofile%2Fscore");
    expect(screen.getByRole("option", { name: "/profile/score (Unavailable)" })).toBeDisabled();
    expect(screen.getByRole("button", { name: /add custom field filter/i })).toHaveProperty("disabled", addDisabled);
  });

  it("applies the visible default when a boolean JSON filter is added", async () => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(customFieldDefinitionsQueryKey("video"), [
      {
        id: 3,
        key: "structured_metadata",
        label: "Structured Metadata",
        type: "json",
        entityTypes: ["video"],
        options: [],
        filterable: false,
        sortable: false,
        isMultiValue: false,
        jsonPaths: [{ path: "/reviewed", label: "Reviewed", type: "boolean", filterable: true, sortable: false }],
      },
    ]);
    const onObjectFilterChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            filterMode="videos"
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{}}
            onObjectFilterChange={onObjectFilterChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Filters" }));
    await user.click(screen.getByText("Custom Fields"));
    await user.click(screen.getByRole("button", { name: /add custom field filter/i }));
    await user.selectOptions(
      screen.getByRole("combobox", { name: "Target" }),
      "structured_metadata:%2Freviewed",
    );
    expect(screen.getByRole("combobox", { name: "Value" })).toHaveValue("true");
    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onObjectFilterChange).toHaveBeenCalledWith(expect.objectContaining({
      customFieldCriteria: [expect.objectContaining({
        key: "structured_metadata",
        jsonPath: "/reviewed",
        type: "boolean",
        value: "true",
      })],
    }));
  });

  it.each([
    ["empty", ""],
    ["whitespace-only", "   "],
  ])("submits an exact %s JSON text filter", async (_description, value) => {
    const user = userEvent.setup();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    queryClient.setQueryData(customFieldDefinitionsQueryKey("video"), [
      {
        id: 4,
        key: "structured_metadata",
        label: "Structured Metadata",
        type: "json",
        entityTypes: ["video"],
        options: [],
        filterable: false,
        sortable: false,
        isMultiValue: false,
        jsonPaths: [{ path: "/label", label: "Label", type: "text", filterable: true, sortable: false }],
      },
    ]);
    const onObjectFilterChange = vi.fn();

    render(
      <QueryClientProvider client={queryClient}>
        <RouteRegistryProvider>
          <ListPage
            title="Videos"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            filterMode="videos"
            criteriaDefinitions={VIDEO_CRITERIA}
            objectFilter={{}}
            onObjectFilterChange={onObjectFilterChange}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>,
    );

    await user.click(screen.getByRole("button", { name: "Filters" }));
    await user.click(screen.getByText("Custom Fields"));
    await user.click(screen.getByRole("button", { name: /add custom field filter/i }));
    await user.selectOptions(
      screen.getByRole("combobox", { name: "Target" }),
      "structured_metadata:%2Flabel",
    );
    if (value) await user.type(screen.getByRole("textbox", { name: "Value" }), value);
    await user.click(screen.getByRole("button", { name: "Apply" }));

    expect(onObjectFilterChange).toHaveBeenCalledWith(expect.objectContaining({
      customFieldCriteria: [expect.objectContaining({
        key: "structured_metadata",
        jsonPath: "/label",
        type: "text",
        value,
      })],
    }));
  });
});
