import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { GalleryScrubThumbnail, getGalleryScrubImageIndex } from "../components/GalleryScrubThumbnail";

const mocks = vi.hoisted(() => ({
  find: vi.fn(),
  thumbnailUrl: vi.fn((id: number) => `/thumbnail/${id}`),
  coverUrl: vi.fn(() => "/gallery-cover"),
}));

vi.mock("../api/client", () => ({
  galleries: { coverUrl: mocks.coverUrl },
  images: { find: mocks.find, thumbnailUrl: mocks.thumbnailUrl },
}));

const gallery = {
  id: 7,
  title: "Sample Gallery",
  imageCount: 101,
  updatedAt: "2026-09-04T00:00:00Z",
} as any;

function renderThumbnail(onClick = vi.fn()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={queryClient}>
      <button type="button" onClick={onClick}>
        <GalleryScrubThumbnail gallery={gallery} />
      </button>
    </QueryClientProvider>,
  );
  const scrubber = screen.getByTestId("gallery-scrub-thumbnail");
  vi.spyOn(scrubber, "getBoundingClientRect").mockReturnValue({ left: 10, width: 100 } as DOMRect);
  return { ...view, scrubber, onClick };
}

describe("GalleryScrubThumbnail", () => {
  beforeEach(() => {
    mocks.find.mockReset();
    mocks.thumbnailUrl.mockClear();
    mocks.coverUrl.mockClear();
    vi.stubGlobal(
      "matchMedia",
      vi.fn(() => ({ matches: true })),
    );
  });

  afterEach(() => vi.unstubAllGlobals());

  it("maps the pointer across every image in the gallery", () => {
    expect(getGalleryScrubImageIndex(0, 100, 101)).toBe(0);
    expect(getGalleryScrubImageIndex(50, 100, 101)).toBe(50);
    expect(getGalleryScrubImageIndex(100, 100, 101)).toBe(100);
    expect(
      new Set(Array.from({ length: 101 }, (_, position) => getGalleryScrubImageIndex(position, 100, 101))).size,
    ).toBe(101);
  });

  it("keeps the static cover on devices without precise hover", () => {
    vi.stubGlobal(
      "matchMedia",
      vi.fn(() => ({ matches: false })),
    );
    const { scrubber } = renderThumbnail();

    fireEvent.mouseMove(scrubber, { clientX: 60 });

    expect(mocks.find).not.toHaveBeenCalled();
  });

  it("loads a sized thumbnail for the sampled image and restores the cover on leave", async () => {
    mocks.find.mockResolvedValue({ items: [{ id: 55 }], totalCount: 101 });
    const { scrubber, container } = renderThumbnail();

    fireEvent.mouseMove(scrubber, { clientX: 60 });

    await waitFor(() =>
      expect(mocks.find).toHaveBeenCalledWith(
        { page: 51, perPage: 1, sort: "path", direction: "asc" },
        { galleryId: 7 },
      ),
    );
    await waitFor(() => expect(container.querySelector('img[src="/thumbnail/55"]')).toBeInTheDocument());
    const preview = container.querySelector('img[src="/thumbnail/55"]') as HTMLImageElement;
    fireEvent.load(preview);
    await waitFor(() => expect(container.querySelector('img[src="/thumbnail/55"]')).not.toHaveClass("opacity-0"));
    expect(container.querySelector('img[src="/gallery-cover"]')).not.toBeInTheDocument();
    expect(screen.getByText("51 / 101")).toBeInTheDocument();

    fireEvent.mouseLeave(scrubber);
    expect(container.querySelector('img[src="/thumbnail/55"]')).not.toBeInTheDocument();
    expect(container.querySelector('img[src="/gallery-cover"]')).toBeInTheDocument();
    expect(screen.queryByText("51 / 101")).not.toBeInTheDocument();
  });

  it("keeps the current preview visible until the next sampled thumbnail loads", async () => {
    mocks.find
      .mockResolvedValueOnce({ items: [{ id: 1 }], totalCount: 101 })
      .mockResolvedValueOnce({ items: [{ id: 101 }], totalCount: 101 });
    const { scrubber, container } = renderThumbnail();

    fireEvent.mouseMove(scrubber, { clientX: 10 });
    await waitFor(() => expect(container.querySelector('img[src="/thumbnail/1"]')).toBeInTheDocument());
    fireEvent.load(container.querySelector('img[src="/thumbnail/1"]')!);
    await waitFor(() => expect(container.querySelector('img[src="/thumbnail/1"]')).not.toHaveClass("opacity-0"));

    fireEvent.mouseMove(scrubber, { clientX: 110 });
    await waitFor(() => expect(container.querySelector('img[src="/thumbnail/101"]')).toHaveClass("opacity-0"));
    expect(container.querySelector('img[src="/thumbnail/1"]')).not.toHaveClass("opacity-0");

    fireEvent.load(container.querySelector('img[src="/thumbnail/101"]')!);
    await waitFor(() => expect(container.querySelector('img[src="/thumbnail/1"]')).not.toBeInTheDocument());
    expect(container.querySelector('img[src="/thumbnail/101"]')).not.toHaveClass("opacity-0");
  });

  it("keeps the last good preview when a replacement thumbnail fails", async () => {
    mocks.find
      .mockResolvedValueOnce({ items: [{ id: 1 }], totalCount: 101 })
      .mockResolvedValueOnce({ items: [{ id: 101 }], totalCount: 101 });
    const { scrubber, container } = renderThumbnail();

    fireEvent.mouseMove(scrubber, { clientX: 10 });
    await waitFor(() => expect(container.querySelector('img[src="/thumbnail/1"]')).toBeInTheDocument());
    fireEvent.load(container.querySelector('img[src="/thumbnail/1"]')!);
    fireEvent.mouseMove(scrubber, { clientX: 110 });
    await waitFor(() => expect(container.querySelector('img[src="/thumbnail/101"]')).toBeInTheDocument());

    fireEvent.error(container.querySelector('img[src="/thumbnail/101"]')!);

    expect(container.querySelector('img[src="/thumbnail/101"]')).not.toBeInTheDocument();
    expect(container.querySelector('img[src="/thumbnail/1"]')).not.toHaveClass("opacity-0");
  });

  it("ignores stale and failed previews without interfering with row navigation", async () => {
    let resolveFirst!: (value: any) => void;
    mocks.find
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveFirst = resolve;
          }),
      )
      .mockRejectedValueOnce(new Error("missing preview"));
    const { scrubber, container, onClick } = renderThumbnail();

    fireEvent.mouseMove(scrubber, { clientX: 10 });
    fireEvent.mouseMove(scrubber, { clientX: 110 });
    resolveFirst({ items: [{ id: 1 }], totalCount: 101 });

    await waitFor(() => expect(mocks.find).toHaveBeenCalledTimes(2));
    expect(container.querySelector('img[src="/thumbnail/1"]')).not.toBeInTheDocument();
    fireEvent.click(scrubber);
    expect(onClick).toHaveBeenCalledOnce();
  });
});
