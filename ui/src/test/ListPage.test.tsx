import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ListPage } from "../components/ListPage";
import { SCENE_CRITERIA } from "../components/FilterDialog";
import { RouteRegistryProvider } from "../router/RouteRegistry";

const storage = new Map<string, string>();

beforeEach(() => {
  storage.clear();
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
            title="Scenes"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            criteriaDefinitions={SCENE_CRITERIA}
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

    expect(screen.getByRole("button", { name: /rating:/i })).toHaveTextContent("Rating:");
    expect(screen.getByRole("button", { name: /rating:/i })).toHaveTextContent("> 80");

    expect(screen.getByRole("button", { name: /tags:/i })).toHaveTextContent("Tags:");
    expect(screen.getByRole("button", { name: /tags:/i })).toHaveTextContent("Includes All Tag One, Tag Two");
    expect(screen.getByRole("button", { name: /tags:/i })).toHaveTextContent("with sub-tags");
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
            title="Scenes"
            filter={{ page: 1, perPage: 40 }}
            onFilterChange={vi.fn()}
            totalCount={0}
            isLoading={false}
            sortOptions={[
              { value: "updated_at", label: "Updated Date" },
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
      "Updated Date",
    ]);
  });

  it("restores saved per-page and zoom preferences for a page key", async () => {
    localStorage.setItem("cove-list-prefs-scenes", JSON.stringify({ perPage: 120, zoomLevel: 2 }));
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
            title="Scenes"
            pageKey="scenes"
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
});