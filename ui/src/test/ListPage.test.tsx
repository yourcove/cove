import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ListPage } from "../components/ListPage";
import { SCENE_CRITERIA } from "../components/FilterDialog";
import { RouteRegistryProvider } from "../router/RouteRegistry";

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
});