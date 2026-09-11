import { act, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import { Lightbox } from "../components/Lightbox";

vi.mock("../api/client", () => ({
  entityEngagement: { get: vi.fn().mockResolvedValue(undefined) },
  images: { incrementLike: vi.fn() },
  playback: { recordIntervals: vi.fn().mockResolvedValue(undefined) },
}));
vi.mock("../utils/interactionTracking", () => ({
  createPlaybackSessionId: () => "test-session",
  trackInteraction: vi.fn(),
}));
vi.mock("../components/Rating", () => ({ InteractiveRating: () => null }));

function setup() {
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Lightbox
        images={[1, 2, 3].map((id) => ({ id, src: `/${id}.jpg`, title: `Image ${id}` }))}
        initialIndex={0}
        open
        onClose={() => {}}
      />
    </QueryClientProvider>,
  );
}
async function load(image: HTMLElement) {
  await act(async () => {
    fireEvent.load(image);
  });
}

describe("Lightbox image loading", () => {
  it("keeps the previous image mounted and visible until the next image is decoded", async () => {
    setup();
    const first = screen.getByAltText("Image 1");
    await load(first);
    fireEvent.keyDown(document, { key: "ArrowRight" });
    expect(first).toBeInTheDocument();
    expect(first).toHaveStyle({ opacity: "1" });
    const second = screen.getByAltText("Image 2");
    let decoded!: () => void;
    Object.defineProperty(second, "decode", {
      value: () =>
        new Promise<void>((resolve) => {
          decoded = resolve;
        }),
    });
    await load(second);
    expect(first).toHaveStyle({ opacity: "1" });
    expect(second).toHaveStyle({ opacity: "0" });
    await act(async () => {
      decoded();
    });
    expect(first).not.toBeInTheDocument();
    expect(second).toHaveStyle({ opacity: "1" });
    expect(screen.getByAltText("Image 2")).toBe(second);
  });

  it("ignores a late decode after rapid navigation", async () => {
    setup();
    await load(screen.getByAltText("Image 1"));
    fireEvent.keyDown(document, { key: "ArrowRight" });
    const second = screen.getByAltText("Image 2");
    let decoded!: () => void;
    Object.defineProperty(second, "decode", {
      value: () =>
        new Promise<void>((resolve) => {
          decoded = resolve;
        }),
    });
    await load(second);
    fireEvent.keyDown(document, { key: "ArrowRight" });
    const third = screen.getByAltText("Image 3");
    await load(third);
    await act(async () => {
      decoded();
    });
    expect(third).toHaveStyle({ opacity: "1" });
    expect(screen.queryByAltText("Image 2")).not.toBeInTheDocument();
  });

  it("keeps the last image visible on failure and allows navigation to recover", async () => {
    setup();
    const first = screen.getByAltText("Image 1");
    await load(first);
    fireEvent.click(screen.getByRole("button", { name: "Next image" }));
    fireEvent.error(screen.getByAltText("Image 2"));
    expect(first).toHaveStyle({ opacity: "1" });
    expect(screen.getByRole("alert")).toHaveTextContent("Unable to load image");
    fireEvent.click(screen.getByRole("button", { name: "Next image" }));
    await load(screen.getByAltText("Image 3"));
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(screen.getByAltText("Image 3")).toHaveStyle({ opacity: "1" });
  });
});
