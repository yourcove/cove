import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it } from "vitest";
import { useListUrlState } from "../hooks/useListUrlState";

function ListStateProbe() {
  const { filter, setFilter, displayMode, setDisplayMode } = useListUrlState({
    resetKey: "videos",
    defaultFilter: { page: 1, perPage: 40 },
    defaultDisplayMode: "grid" as const,
    allowedDisplayModes: ["grid", "list", "wall", "feed", "vertical"] as const,
    allowInfinitePageSize: true,
  });

  return (
    <div>
      <div data-testid="display-mode">{displayMode}</div>
      <div data-testid="per-page">{filter.perPage}</div>
      <button type="button" onClick={() => setDisplayMode("feed")}>Feed</button>
      <button type="button" onClick={() => setFilter({ ...filter, perPage: 60 })}>Sixty</button>
    </div>
  );
}

function DefaultSearchProbe() {
  const { filter } = useListUrlState({
    resetKey: "videos",
    defaultFilter: { q: "default search", page: 1, perPage: 40 },
    defaultDisplayMode: "grid" as const,
    allowedDisplayModes: ["grid"] as const,
  });

  return <div data-testid="search-query">{filter.q ?? "undefined"}</div>;
}

function SavedDisplayDefaultProbe() {
  const { displayMode } = useListUrlState({
    resetKey: "videos",
    defaultFilter: { page: 1, perPage: 40 },
    defaultDisplayMode: "list" as const,
    allowedDisplayModes: ["grid", "list"] as const,
  });

  return <div data-testid="display-mode">{displayMode}</div>;
}

function MultiSortProbe() {
  const { filter, setFilter } = useListUrlState({
    resetKey: "videos",
    defaultFilter: { page: 1, perPage: 40, sort: "date", direction: "desc" },
    defaultDisplayMode: "grid" as const,
    allowedDisplayModes: ["grid"] as const,
  });

  return (
    <div>
      <div data-testid="sort-clauses">{JSON.stringify(filter.sorts ?? [])}</div>
      <button
        type="button"
        onClick={() => setFilter({
          ...filter,
          sort: "studio",
          direction: "asc",
          sorts: [
            { key: "studio", direction: "asc" },
            { key: "date", direction: "desc" },
          ],
        })}
      >
        Group by studio
      </button>
      <button
        type="button"
        onClick={() => setFilter({
          ...filter,
          direction: filter.direction === "desc" ? "asc" : "desc",
          sorts: undefined,
        })}
      >
        Toggle direction
      </button>
    </div>
  );
}

describe("useListUrlState", () => {
  beforeEach(() => {
    window.history.replaceState(null, "", "/videos?view=wall&perPage=infinite&page=2&viewMode=vertical");
  });

  it("reads display mode and persists infinite page size through perPage", async () => {
    const user = userEvent.setup();

    render(<ListStateProbe />);

    expect(screen.getByTestId("display-mode")).toHaveTextContent("wall");
    expect(screen.getByTestId("per-page")).toHaveTextContent("0");

    await user.click(screen.getByRole("button", { name: "Feed" }));

    await waitFor(() => {
      expect(screen.getByTestId("display-mode")).toHaveTextContent("feed");
      expect(window.location.search).toContain("view=feed");
      expect(window.location.search).toContain("perPage=infinite");
      expect(window.location.search).not.toContain("viewMode");
      expect(window.location.search).toContain("page=2");
    });

    await user.click(screen.getByRole("button", { name: "Sixty" }));

    await waitFor(() => {
      expect(screen.getByTestId("per-page")).toHaveTextContent("60");
      expect(window.location.search).toContain("perPage=60");
    });
  });

  it("keeps an explicitly cleared search from restoring the default", async () => {
    window.history.replaceState(null, "", "/videos?q=");

    render(<DefaultSearchProbe />);

    await waitFor(() => {
      expect(screen.getByTestId("search-query")).toBeEmptyDOMElement();
      expect(window.location.search).toBe("?q=");
    });
  });

  it("preserves an explicit display override relative to a saved display default", async () => {
    window.history.replaceState(null, "", "/videos?view=grid");

    const firstMount = render(<SavedDisplayDefaultProbe />);

    await waitFor(() => {
      expect(screen.getByTestId("display-mode")).toHaveTextContent("grid");
      expect(window.location.search).toBe("?view=grid");
    });

    firstMount.unmount();
    render(<SavedDisplayDefaultProbe />);

    expect(screen.getByTestId("display-mode")).toHaveTextContent("grid");
    expect(window.location.search).toBe("?view=grid");
  });

  it("round-trips ordered sort clauses through a compact URL parameter", async () => {
    window.history.replaceState(null, "", "/videos?sorts=studio%3Aasc%2Cdate%3Adesc");
    const user = userEvent.setup();

    render(<MultiSortProbe />);

    expect(screen.getByTestId("sort-clauses")).toHaveTextContent(
      JSON.stringify([
        { key: "studio", direction: "asc" },
        { key: "date", direction: "desc" },
      ]),
    );

    window.history.replaceState(null, "", "/videos");
    await user.click(screen.getByRole("button", { name: "Group by studio" }));

    await waitFor(() => {
      expect(decodeURIComponent(window.location.search)).toContain("sorts=studio:asc,date:desc");
      expect(window.location.search).not.toContain("sort=studio");
      expect(window.location.search).not.toContain("direction=asc");
    });
  });

  it("keeps a scalar direction change after URL synchronization settles", async () => {
    window.history.replaceState(null, "", "/videos");
    const user = userEvent.setup();

    render(<MultiSortProbe />);
    await user.click(screen.getByRole("button", { name: "Toggle direction" }));

    await waitFor(() => {
      expect(window.location.search).toBe("?direction=asc");
    });

    await new Promise((resolve) => window.setTimeout(resolve, 50));
    expect(window.location.search).toBe("?direction=asc");
  });
});
