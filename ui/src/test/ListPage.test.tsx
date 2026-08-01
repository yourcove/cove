import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ListPage } from "../components/ListPage";
import { TAG_CRITERIA, VIDEO_CRITERIA } from "../components/FilterDialog";
import { getEntityCardMinWidthPx } from "../hooks/useEntityCardSize";
import { customFieldDefinitionsQueryKey } from "../hooks/useCustomFieldDefinitions";
import { RouteRegistryProvider } from "../router/RouteRegistry";

vi.mock("../state/AppConfigContext", () => ({
  useAppConfig: () => ({ config: { ui: { keybindingOverrides: {} } } }),
}));

vi.mock("../auth/AuthContext", () => ({
  useAuth: () => ({ user: null }),
}));

const storage = new Map<string, string>();

beforeEach(() => {
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
            isLoading={false}
            error={new Error("Request failed: 502 Bad Gateway")}
            onRetry={onRetry}
          >
            <div>empty collection content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.getByText("Could not load Videos")).toBeInTheDocument();
    expect(screen.getByText("Request failed: 502 Bad Gateway")).toBeInTheDocument();
    expect(screen.queryByText("empty collection content")).not.toBeInTheDocument();
    expect(screen.queryByText("0 items")).not.toBeInTheDocument();

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
              ratingCriterion: { value: 80, modifier: "GREATER_THAN" },
              tagsCriterion: { value: [1, 2], modifier: "INCLUDES_ALL", depth: -1 },
            }}
            onObjectFilterChange={vi.fn()}
          >
            <div>content</div>
          </ListPage>
        </RouteRegistryProvider>
      </QueryClientProvider>
    );

    expect(screen.getByRole("button", { name: "Edit filter: Rating" })).toHaveTextContent("Rating:");
    expect(screen.getByRole("button", { name: "Edit filter: Rating" })).toHaveTextContent("> 80");

    expect(screen.getByRole("button", { name: "Edit filter: Tags" })).toHaveTextContent("Tags:");
    expect(screen.getByRole("button", { name: "Edit filter: Tags" })).toHaveTextContent("Includes All Tag One, Tag Two");
    expect(screen.getByRole("button", { name: "Edit filter: Tags" })).toHaveTextContent("with sub-tags");
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
            totalCount={0}
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

    expect(screen.getByRole("heading", { name: "Edit Filter" })).toBeInTheDocument();
    expect(screen.getByPlaceholderText("Value...")).toHaveValue("example");
    expect(onObjectFilterChange).not.toHaveBeenCalled();
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
    expect(screen.getByRole("button", { name: "Edit filter: Tag Duration" })).toHaveTextContent("> 1:30");
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

    await user.click(screen.getByTitle("Sort descending"));

    expect(onFilterChange).toHaveBeenCalledWith(expect.objectContaining({ sort: "random", direction: "asc", seed: 12345 }));
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

    await user.click(screen.getByRole("button", { name: "Filter" }));
    const dialogShell = Array.from(container.querySelectorAll("div"))
      .find((element) => element.className.includes("sm:w-[min(92vw,56rem)]"));
    expect(dialogShell).toBeTruthy();

    await user.click(screen.getByText("Custom Fields"));
    await user.click(screen.getByRole("button", { name: /add custom field filter/i }));

    expect(screen.getByPlaceholderText("Search tags...").closest("label")?.className).toContain("min-w-0");
    expect(container.querySelector('[aria-label="Remove custom field filter"]')?.parentElement?.className).toContain("xl:grid-cols");
  });
});
