import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ListPageCardSizeContext } from "../components/ListPageCardSizeContext";
import { VirtualizedEntityGrid } from "../components/VirtualizedEntityLayouts";

const virtualizedInfiniteListSpy = vi.fn();

vi.mock("../components/VirtualizedInfiniteList", () => ({
  VirtualizedInfiniteList: (props: unknown) => {
    virtualizedInfiniteListSpy(props);
    return <div data-testid="virtualized-grid" />;
  },
}));

vi.mock("../components/EntityCardGrid", () => ({
  EntityCardGrid: ({ children }: { children: React.ReactNode }) => <div data-testid="entity-card-grid">{children}</div>,
}));

describe("VirtualizedEntityGrid", () => {
  beforeEach(() => {
    virtualizedInfiniteListSpy.mockReset();
  });

  it("uses the shared ListPage card width for infinite grids when no explicit virtual width is provided", () => {
    render(
      <ListPageCardSizeContext.Provider value={{ cardMinWidthPx: 360, zoomLevel: 2 }}>
        <VirtualizedEntityGrid
          items={[{ id: 1 }]}
          getItemKey={(item) => item.id}
          renderItem={(item) => <div>{item.id}</div>}
          infinitePageSize
          hasNextPage={false}
          isFetchingNextPage={false}
          loadMore={vi.fn()}
        />
      </ListPageCardSizeContext.Provider>,
    );

    expect(screen.getByTestId("virtualized-grid")).toBeInTheDocument();
    expect(virtualizedInfiniteListSpy).toHaveBeenCalledWith(expect.objectContaining({ minColumnWidth: 360 }));
  });

  it("prefers an explicit virtual width over the shared ListPage card width", () => {
    render(
      <ListPageCardSizeContext.Provider value={{ cardMinWidthPx: 360, zoomLevel: 2 }}>
        <VirtualizedEntityGrid
          items={[{ id: 1 }]}
          getItemKey={(item) => item.id}
          renderItem={(item) => <div>{item.id}</div>}
          infinitePageSize
          virtualMinColumnWidth={220}
          hasNextPage={false}
          isFetchingNextPage={false}
          loadMore={vi.fn()}
        />
      </ListPageCardSizeContext.Provider>,
    );

    expect(virtualizedInfiniteListSpy).toHaveBeenCalledWith(expect.objectContaining({ minColumnWidth: 220 }));
  });

  it("falls back to a numeric minCardWidth when no shared ListPage width is available", () => {
    render(
      <VirtualizedEntityGrid
        items={[{ id: 1 }]}
        getItemKey={(item) => item.id}
        renderItem={(item) => <div>{item.id}</div>}
        minCardWidth="320px"
        infinitePageSize
        hasNextPage={false}
        isFetchingNextPage={false}
        loadMore={vi.fn()}
      />,
    );

    expect(virtualizedInfiniteListSpy).toHaveBeenCalledWith(expect.objectContaining({ minColumnWidth: 320 }));
  });

  it("falls back to the CSS variable px fallback when no shared ListPage width is available", () => {
    render(
      <VirtualizedEntityGrid
        items={[{ id: 1 }]}
        getItemKey={(item) => item.id}
        renderItem={(item) => <div>{item.id}</div>}
        minCardWidth="var(--card-min-width, 280px)"
        infinitePageSize
        hasNextPage={false}
        isFetchingNextPage={false}
        loadMore={vi.fn()}
      />,
    );

    expect(virtualizedInfiniteListSpy).toHaveBeenCalledWith(expect.objectContaining({ minColumnWidth: 280 }));
  });
});
