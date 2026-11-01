import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Tag } from "../api/types";
import type { LightboxImage } from "../components/Lightbox";

const mockFindGalleries = vi.fn();
const mockNavigateToUrl = vi.fn();

vi.mock("../hooks/useEntityEngagement", () => ({
  useEntityEngagement: () => ({ engagement: undefined, rating: null, setRating: vi.fn(), ratingPending: false }),
}));

vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    galleries: { ...actual.galleries, find: (...args: unknown[]) => mockFindGalleries(...args) },
    playback: { recordIntervals: vi.fn().mockResolvedValue(undefined) },
  };
});

vi.mock("../router/location", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../router/location")>();
  return { ...actual, navigateToUrl: (...args: unknown[]) => mockNavigateToUrl(...args) };
});

vi.mock("../utils/interactionTracking", () => ({
  createPlaybackSessionId: () => "session",
  trackInteraction: vi.fn(),
}));

import { Lightbox } from "../components/Lightbox";

const taggedImage: LightboxImage = {
  id: 7,
  src: "/tagged.jpg",
  title: "tagged.jpg",
  references: {
    tags: [
      { id: 11, name: "Outdoor" },
      { id: 12, name: "Portrait" },
    ] as Tag[],
    performers: [],
    groups: [],
    galleryCount: 1,
  },
};
const plainImage: LightboxImage = { id: 8, src: "/plain.jpg", title: "plain.jpg" };

function renderLightbox(images: LightboxImage[], onClose = vi.fn()) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <Lightbox images={images} initialIndex={0} open onClose={onClose} />
    </QueryClientProvider>,
  );
  return { onClose };
}

async function loadImage(src: string) {
  const image = document.querySelector<HTMLImageElement>(`img[src="${src}"]`)!;
  await act(async () => {
    fireEvent.load(image);
  });
}

async function hover(element: HTMLElement) {
  fireEvent.mouseEnter(element.parentElement!);
  // Real timers: the popover's open delay and the lazily fetched gallery list must both settle.
  await act(() => new Promise((resolve) => setTimeout(resolve, 250)));
}

beforeEach(() => {
  vi.clearAllMocks();
  mockFindGalleries.mockResolvedValue({ items: [{ id: 21, title: "Holiday set" }], totalCount: 1 });
});

describe("Lightbox image references", () => {
  it("shows tag and gallery popovers for the current image in the bottom-right corner", async () => {
    renderLightbox([taggedImage]);
    await loadImage("/tagged.jpg");

    const tagsButton = screen.getByRole("button", { name: /^2$/ });
    expect(tagsButton).toHaveAttribute("title", "Tags");
    const galleriesButton = screen.getByTitle("Galleries");
    expect(galleriesButton).toHaveTextContent("1");
    expect(tagsButton.closest("[data-lightbox-engagement]")).toHaveClass("ml-auto");

    await hover(tagsButton);
    expect(screen.getByText("Outdoor")).toBeInTheDocument();
    expect(screen.getByText("Portrait")).toBeInTheDocument();

    await hover(galleriesButton);
    expect(await screen.findByText("Holiday set")).toBeInTheDocument();
    expect(mockFindGalleries).toHaveBeenCalledWith(expect.anything(), { imageId: 7 });
  });

  it("closes the lightbox before following a reference link", async () => {
    const { onClose } = renderLightbox([taggedImage]);
    await loadImage("/tagged.jpg");

    await hover(screen.getByTitle("Tags"));
    fireEvent.click(screen.getByText("Outdoor"));

    await waitFor(() => expect(mockNavigateToUrl).toHaveBeenCalled());
    expect(onClose.mock.invocationCallOrder[0]).toBeLessThan(mockNavigateToUrl.mock.invocationCallOrder[0]);
    expect(mockNavigateToUrl.mock.calls[0][0]).toContain("/tag/11");
  });

  it("keeps a wide popover inside the viewport and beside its trigger at the right edge", async () => {
    renderLightbox([taggedImage]);
    await loadImage("/tagged.jpg");

    const galleriesButton = screen.getByTitle("Galleries");
    const right = window.innerWidth - 16;
    const triggerRight = right - 2;
    vi.spyOn(galleriesButton.parentElement!, "getBoundingClientRect").mockReturnValue(
      DOMRect.fromRect({ x: triggerRight - 38, y: window.innerHeight - 40, width: 38, height: 28 }),
    );
    await hover(galleriesButton);

    const popover = (await screen.findByText("Holiday set")).closest<HTMLElement>("[style]")!;
    // Right-aligned with the trigger, so it grows leftward to whatever width its content needs.
    expect(popover.style.left).toBe("");
    expect(popover.style.right).toBe(`${window.innerWidth - triggerRight}px`);
  });

  it("keeps showing the displayed image's references until the next image has loaded", async () => {
    renderLightbox([taggedImage, plainImage]);
    await loadImage("/tagged.jpg");

    fireEvent.click(screen.getByRole("button", { name: "Next image" }));
    expect(screen.getByTitle("Tags")).toBeInTheDocument();

    await loadImage("/plain.jpg");
    expect(screen.queryByTitle("Tags")).not.toBeInTheDocument();
  });

  it("omits the reference bar when the image has no references", async () => {
    renderLightbox([plainImage]);
    await loadImage("/plain.jpg");

    expect(screen.queryByTitle("Tags")).not.toBeInTheDocument();
    expect(document.querySelector("[data-lightbox-references]")).toBeNull();
  });
});
