import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ListSearchControl } from "../components/ListSearchControl";

describe("ListSearchControl", () => {
  it("debounces query changes", async () => {
    const user = userEvent.setup();
    const onQueryChange = vi.fn();
    render(<ListSearchControl onQueryChange={onQueryChange} />);

    await user.type(screen.getByRole("textbox", { name: "Search list" }), "summer");

    expect(onQueryChange).not.toHaveBeenCalled();
    await waitFor(() => expect(onQueryChange).toHaveBeenCalledWith("summer", "debounce"));
  });

  it("submits immediately with Enter", async () => {
    const user = userEvent.setup();
    const onQueryChange = vi.fn();
    render(<ListSearchControl onQueryChange={onQueryChange} />);

    await user.type(screen.getByRole("textbox", { name: "Search list" }), "summer{Enter}");

    expect(onQueryChange).toHaveBeenCalledWith("summer", "submit");
  });

  it("provides the same clear action for populated searches", async () => {
    const user = userEvent.setup();
    const onQueryChange = vi.fn();
    render(<ListSearchControl query="summer" onQueryChange={onQueryChange} />);

    await user.click(screen.getByRole("button", { name: "Clear search" }));

    expect(screen.getByRole("textbox", { name: "Search list" })).toHaveValue("");
    expect(onQueryChange).toHaveBeenCalledWith(undefined, "clear");
  });

  it("synchronizes the input when the URL-backed query changes", () => {
    const onQueryChange = vi.fn();
    const { rerender } = render(<ListSearchControl query="summer" onQueryChange={onQueryChange} />);

    rerender(<ListSearchControl query="winter" onQueryChange={onQueryChange} />);

    expect(screen.getByRole("textbox", { name: "Search list" })).toHaveValue("winter");
  });

  it("preserves an in-progress space when the committed query is normalized", async () => {
    const user = userEvent.setup();
    const onQueryChange = vi.fn();
    const { rerender } = render(<ListSearchControl onQueryChange={onQueryChange} />);
    const input = screen.getByRole("textbox", { name: "Search list" });

    await user.type(input, "summer ");
    await waitFor(() => expect(onQueryChange).toHaveBeenCalledWith("summer", "debounce"));

    rerender(<ListSearchControl query="summer" onQueryChange={onQueryChange} />);

    expect(input).toHaveValue("summer ");
  });
});
