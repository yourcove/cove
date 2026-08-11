import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ListQueryState } from "../components/ListQueryState";
import { getLoadError, normalizeQueryError, resolveQueryLoadState } from "../utils/queryLoadState";

describe("query load state", () => {
  it("only exposes an error before any data has loaded", () => {
    const error = new Error("API Error 502: Bad Gateway");

    expect(getLoadError(undefined, error)).toBe(error);
    expect(getLoadError([], error)).toBeNull();
    expect(getLoadError({ items: [] }, error)).toBeNull();
  });

  it("normalizes non-Error query failures", () => {
    expect(normalizeQueryError("Bad Gateway")?.message).toBe("Bad Gateway");
    expect(normalizeQueryError(null)).toBeNull();
  });

  it("derives pending, error, empty, and success without treating failures as empty", () => {
    const error = new Error("API request timed out");
    const retry = vi.fn();

    expect(resolveQueryLoadState({ data: undefined, isPending: true, error, isEmpty: () => true, retry })).toEqual({ status: "pending" });
    expect(resolveQueryLoadState({ data: undefined, isPending: false, error, isEmpty: () => true, retry })).toEqual({ status: "error", error, retry });
    expect(resolveQueryLoadState({ data: { items: [] }, isPending: false, error: null, isEmpty: (data) => data.items.length === 0 })).toEqual({ status: "empty", data: { items: [] } });
    expect(resolveQueryLoadState({ data: { items: [1] }, isPending: false, error: null, isEmpty: (data) => data.items.length === 0 })).toEqual({ status: "success", data: { items: [1] } });
    expect(resolveQueryLoadState({ data: { items: [1] }, isPending: false, error, isEmpty: (data) => data.items.length === 0 }).status).toBe("success");
  });
});

describe("ListQueryState", () => {
  it("renders loading, error, empty, and content states exclusively in that order", async () => {
    const user = userEvent.setup();
    const onRetry = vi.fn();
    const error = new Error("API Error 502: Bad Gateway");
    const states = {
      loading: <div>loading state</div>,
      empty: <div>empty state</div>,
      children: <div>content state</div>,
    };
    const { rerender } = render(
      <ListQueryState isLoading loadError={error} isEmpty loading={states.loading} empty={states.empty} onRetry={onRetry}>
        {states.children}
      </ListQueryState>,
    );

    expect(screen.getByText("loading state")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(screen.queryByText("empty state")).not.toBeInTheDocument();
    expect(screen.queryByText("content state")).not.toBeInTheDocument();

    rerender(
      <ListQueryState isLoading={false} loadError={error} isEmpty loading={states.loading} empty={states.empty} onRetry={onRetry}>
        {states.children}
      </ListQueryState>,
    );
    expect(screen.getByRole("alert")).toHaveTextContent("The server returned an error. Please try again.");
    expect(screen.getByRole("alert")).not.toHaveTextContent("API Error");
    expect(screen.queryByText("empty state")).not.toBeInTheDocument();
    expect(screen.queryByText("content state")).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Try again" }));
    expect(onRetry).toHaveBeenCalledOnce();

    rerender(
      <ListQueryState isLoading={false} loadError={null} isEmpty loading={states.loading} empty={states.empty} onRetry={onRetry}>
        {states.children}
      </ListQueryState>,
    );
    expect(screen.getByText("empty state")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(screen.queryByText("content state")).not.toBeInTheDocument();

    rerender(
      <ListQueryState isLoading={false} loadError={null} isEmpty={false} loading={states.loading} empty={states.empty} onRetry={onRetry}>
        {states.children}
      </ListQueryState>,
    );
    expect(screen.getByText("content state")).toBeInTheDocument();
    expect(screen.queryByText("empty state")).not.toBeInTheDocument();
  });

  it("keeps a persistent header mounted across query states", () => {
    const header = <input aria-label="List search" />;
    const { rerender } = render(
      <ListQueryState header={header} isLoading={false} loadError={null} isEmpty={false} loading={<div>loading</div>} empty={<div>empty</div>}>
        <div>content</div>
      </ListQueryState>,
    );
    const search = screen.getByRole("textbox", { name: "List search" });
    search.focus();

    rerender(
      <ListQueryState header={header} isLoading loadError={null} isEmpty={false} loading={<div>loading</div>} empty={<div>empty</div>}>
        <div>content</div>
      </ListQueryState>,
    );

    expect(screen.getByRole("textbox", { name: "List search" })).toBe(search);
    expect(search).toHaveFocus();
    expect(screen.getByText("loading")).toBeInTheDocument();
  });
});
