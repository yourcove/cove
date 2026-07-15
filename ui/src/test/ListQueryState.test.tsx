import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ListQueryState } from "../components/ListQueryState";
import { getLoadError, normalizeQueryError } from "../utils/queryLoadState";

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
    expect(screen.getByRole("alert")).toHaveTextContent("API Error 502: Bad Gateway");
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
});
