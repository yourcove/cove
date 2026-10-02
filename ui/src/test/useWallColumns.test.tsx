import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { useWallColumns } from "../hooks/useWallColumns";

type Item = { id: number; height: number };

const item = (id: number, height = 1): Item => ({ id, height });
const estimateHeight = (entry: Item) => entry.height;
const stableOptions = { stable: true, getKey: (entry: Item) => entry.id };
const ids = (columns: Item[][]) => columns.map((column) => column.map((entry) => entry.id));

const originalInnerWidth = window.innerWidth;

function setViewportWidth(width: number) {
  Object.defineProperty(window, "innerWidth", { configurable: true, value: width });
}

afterEach(() => {
  setViewportWidth(originalInnerWidth);
});

describe("useWallColumns", () => {
  it("places each item in the shortest column", () => {
    setViewportWidth(1280);
    const items = [item(1, 3), item(2, 1), item(3, 1), item(4, 1)];

    const { result } = renderHook(() => useWallColumns(items, 3, estimateHeight));

    expect(ids(result.current)).toEqual([[1], [2, 4], [3]]);
  });

  it("keeps stable items in their columns when more items load", () => {
    setViewportWidth(1280);
    const { result, rerender } = renderHook(({ items }) => useWallColumns(items, 2, estimateHeight, stableOptions), {
      initialProps: { items: [item(1, 1), item(2, 1)] },
    });
    expect(ids(result.current)).toEqual([[1], [2]]);

    // Rebalancing from scratch would move item 2 next to the tall item 3; stable columns keep it put.
    rerender({ items: [item(3, 5), item(1, 1), item(2, 1), item(4, 1)] });

    expect(ids(result.current)).toEqual([
      [3, 1],
      [2, 4],
    ]);
  });

  it("forgets removed stable items so the space is reused", () => {
    setViewportWidth(1280);
    const { result, rerender } = renderHook(({ items }) => useWallColumns(items, 2, estimateHeight, stableOptions), {
      initialProps: { items: [item(1, 4), item(2, 1), item(3, 1)] },
    });
    expect(ids(result.current)).toEqual([[1], [2, 3]]);

    rerender({ items: [item(2, 1), item(3, 1)] });
    rerender({ items: [item(2, 1), item(3, 1), item(5, 1)] });

    expect(ids(result.current)).toEqual([[5], [2, 3]]);
  });

  it("reassigns stable items from scratch when the column count changes", () => {
    setViewportWidth(1280);
    const items = [item(1, 1), item(2, 1), item(3, 1)];
    const { result } = renderHook(() => useWallColumns(items, 3, estimateHeight, stableOptions));
    expect(ids(result.current)).toEqual([[1], [2], [3]]);

    setViewportWidth(360);
    act(() => {
      window.dispatchEvent(new Event("resize"));
    });

    expect(ids(result.current)).toEqual([[1, 3], [2]]);
  });

  it("starts stable assignments fresh after a non-stable render", () => {
    setViewportWidth(1280);
    const { result, rerender } = renderHook(({ items, options }) => useWallColumns(items, 2, estimateHeight, options), {
      initialProps: { items: [item(1, 1), item(2, 1)], options: stableOptions as typeof stableOptions | undefined },
    });
    expect(ids(result.current)).toEqual([[1], [2]]);

    rerender({ items: [item(3, 5), item(2, 1)], options: undefined });
    rerender({ items: [item(3, 5), item(2, 1), item(1, 1)], options: stableOptions });

    expect(ids(result.current)).toEqual([[3], [2, 1]]);
  });
});
