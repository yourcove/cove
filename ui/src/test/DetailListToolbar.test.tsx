import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { DetailListToolbar } from "../components/DetailListToolbar";

describe("DetailListToolbar", () => {
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
});