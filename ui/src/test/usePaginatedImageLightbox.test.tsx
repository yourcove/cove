import { act, renderHook } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { Image } from "../api/types";
import { usePaginatedImageLightbox } from "../hooks/usePaginatedImageLightbox";

const image = (id: number, title: string): Image => ({ id, title, files: [] }) as unknown as Image;

describe("usePaginatedImageLightbox", () => {
  it("opens the selected image and loads an adjacent page with the active filter", async () => {
    const queryPage = vi.fn().mockResolvedValue({ items: [image(3, "Third")], totalCount: 3 });
    const { result } = renderHook(() =>
      usePaginatedImageLightbox({
        items: [image(1, "First"), image(2, "Second")],
        filter: { page: 1, perPage: 2, sort: "title", direction: "asc", q: "needle" },
        totalCount: 3,
        infinitePageSize: false,
        queryPage,
        toLightboxImage: (item) => ({ id: item.id, src: `/image/${item.id}`, title: item.title }),
      }),
    );

    act(() => result.current.openImage(2));
    expect(result.current.lightboxProps.open).toBe(true);
    expect(result.current.lightboxProps.initialIndex).toBe(1);
    expect(result.current.lightboxProps.hasNext).toBe(true);

    let loaded: Awaited<ReturnType<NonNullable<typeof result.current.lightboxProps.loadNext>>> = [];
    await act(async () => {
      loaded = await result.current.lightboxProps.loadNext!();
    });
    expect(queryPage).toHaveBeenCalledWith({ page: 2, perPage: 2, sort: "title", direction: "asc", q: "needle" });
    expect(loaded).toEqual([{ id: 3, src: "/image/3", title: "Third" }]);
  });

  it("uses a selected-image scope without exposing remote boundaries", () => {
    const selected = [image(2, "Second"), image(4, "Fourth")];
    const { result } = renderHook(() =>
      usePaginatedImageLightbox({
        items: [image(1, "First"), ...selected],
        filter: { page: 2, perPage: 2 },
        totalCount: 8,
        infinitePageSize: false,
        queryPage: vi.fn(),
        toLightboxImage: (item) => ({ id: item.id, src: `/image/${item.id}` }),
      }),
    );

    act(() => result.current.openScope(selected));
    expect(result.current.lightboxProps.images.map((item) => item.id)).toEqual([2, 4]);
    expect(result.current.lightboxProps.autoPlay).toBe(true);
    expect(result.current.lightboxProps.hasPrevious).toBe(false);
    expect(result.current.lightboxProps.hasNext).toBe(false);
    expect(result.current.lightboxProps.wrap).toBe(true);
  });
});
