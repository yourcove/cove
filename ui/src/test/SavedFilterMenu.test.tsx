import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { SavedFilterMenu } from "../components/SavedFilterMenu";

vi.mock("../api/client", () => ({
  savedFilters: {
    list: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    delete: vi.fn(),
  },
}));

describe("SavedFilterMenu", () => {
  it("closes when clicking outside the menu", async () => {
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
});
