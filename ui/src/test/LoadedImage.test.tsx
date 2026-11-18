import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { LoadedImage } from "../components/LoadedImage";

class FakePreload {
  static instances: FakePreload[] = [];
  src = "";
  crossOrigin: string | null = null;
  referrerPolicy = "";
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  constructor() {
    FakePreload.instances.push(this);
  }
}

function preloadOf(src: string) {
  const preload = [...FakePreload.instances].reverse().find((instance) => instance.src === src);
  if (!preload) throw new Error(`No preload for ${src}`);
  return preload;
}

describe("LoadedImage", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    FakePreload.instances = [];
  });

  it("stays invisible until the image has loaded", () => {
    render(<LoadedImage src="/cover.jpg" alt="Cover" style={{ objectFit: "cover" }} />);
    const image = screen.getByRole("img", { name: "Cover" });
    expect(image).not.toBeVisible();

    fireEvent.load(image);
    expect(image).toBeVisible();
    expect(image).toHaveStyle({ objectFit: "cover" });
  });

  it("never shows an image that failed to load", () => {
    render(<LoadedImage src="/missing.jpg" alt="Cover" />);
    const image = screen.getByRole("img", { name: "Cover" });

    fireEvent.error(image);
    expect(image).not.toBeVisible();
  });

  it("shows the fallback in place of an image that failed to load", () => {
    render(<LoadedImage src="/missing.jpg" alt="Cover" fallback={<span>No cover</span>} />);

    fireEvent.error(screen.getByRole("img", { name: "Cover" }));
    expect(screen.queryByRole("img", { name: "Cover" })).not.toBeInTheDocument();
    expect(screen.getByText("No cover")).toBeVisible();
  });

  it("keeps a shown picture up while its replacement loads, then swaps it in", () => {
    vi.stubGlobal("Image", FakePreload);
    const view = render(<LoadedImage src="/a.jpg" alt="Frame" />);
    const image = screen.getByRole("img", { name: "Frame" });
    fireEvent.load(image);

    view.rerender(<LoadedImage src="/b.jpg" alt="Frame" />);
    expect(image).toHaveAttribute("src", "/a.jpg");
    expect(image).toBeVisible();

    act(() => preloadOf("/b.jpg").onload?.());
    expect(image).toHaveAttribute("src", "/b.jpg");
    expect(image).toBeVisible();
  });

  it("swaps in only the latest of several quick replacements and cancels the others", () => {
    vi.stubGlobal("Image", FakePreload);
    const view = render(<LoadedImage src="/a.jpg" alt="Frame" />);
    const image = screen.getByRole("img", { name: "Frame" });
    fireEvent.load(image);

    view.rerender(<LoadedImage src="/b.jpg" alt="Frame" />);
    const skipped = preloadOf("/b.jpg");
    view.rerender(<LoadedImage src="/c.jpg" alt="Frame" />);
    expect(skipped.src).toBe("");
    expect(skipped.onload).toBeNull();

    act(() => preloadOf("/c.jpg").onload?.());
    expect(image).toHaveAttribute("src", "/c.jpg");
    expect(image).toBeVisible();
  });

  it("keeps the shown picture when the source returns to it before the replacement loads", () => {
    vi.stubGlobal("Image", FakePreload);
    const view = render(<LoadedImage src="/a.jpg" alt="Frame" />);
    const image = screen.getByRole("img", { name: "Frame" });
    fireEvent.load(image);

    view.rerender(<LoadedImage src="/b.jpg" alt="Frame" />);
    const abandoned = preloadOf("/b.jpg");
    view.rerender(<LoadedImage src="/a.jpg" alt="Frame" />);
    expect(abandoned.src).toBe("");
    expect(image).toHaveAttribute("src", "/a.jpg");
    expect(image).toBeVisible();
  });

  it("reports a replacement that fails to load and shows the fallback without ever showing it", () => {
    vi.stubGlobal("Image", FakePreload);
    const onError = vi.fn();
    const view = render(<LoadedImage src="/a.jpg" alt="Frame" onError={onError} fallback={<span>No frame</span>} />);
    fireEvent.load(screen.getByRole("img", { name: "Frame" }));

    view.rerender(<LoadedImage src="/missing.jpg" alt="Frame" onError={onError} fallback={<span>No frame</span>} />);
    act(() => preloadOf("/missing.jpg").onerror?.());
    const image = screen.getByRole("img", { name: "Frame" });
    expect(image).toHaveAttribute("src", "/missing.jpg");
    expect(image).not.toBeVisible();

    fireEvent.error(image);
    expect(onError).toHaveBeenCalledTimes(1);
    expect(screen.getByText("No frame")).toBeVisible();
  });

  it("retries a source that failed before once the source changes", () => {
    const view = render(<LoadedImage src="/a.jpg" alt="Cover" fallback={<span>No cover</span>} />);
    fireEvent.error(screen.getByRole("img", { name: "Cover" }));
    expect(screen.getByText("No cover")).toBeVisible();

    view.rerender(<LoadedImage src="/b.jpg" alt="Cover" fallback={<span>No cover</span>} />);
    expect(screen.getByRole("img", { name: "Cover" })).toHaveAttribute("src", "/b.jpg");
    expect(screen.getByRole("img", { name: "Cover" })).not.toBeVisible();
  });

  it("shows the fallback for an image without a source", () => {
    render(<LoadedImage src="" alt="Cover" fallback={<span>No cover</span>} />);
    expect(screen.getByText("No cover")).toBeVisible();
  });

  it("shows a picture the browser already has on its first frame", () => {
    const complete = vi.spyOn(HTMLImageElement.prototype, "complete", "get").mockReturnValue(true);
    const width = vi.spyOn(HTMLImageElement.prototype, "naturalWidth", "get").mockReturnValue(320);
    try {
      render(<LoadedImage src="/cached.jpg" alt="Cover" />);
      expect(screen.getByRole("img", { name: "Cover" })).toBeVisible();
    } finally {
      complete.mockRestore();
      width.mockRestore();
    }
  });

  it("is the only place the app renders an image element", () => {
    const sources = import.meta.glob<string>(["../**/*.tsx", "!../test/**"], {
      query: "?raw",
      import: "default",
      eager: true,
    });
    const offenders = Object.entries(sources)
      .filter(([path, source]) => !path.endsWith("/LoadedImage.tsx") && /<img\b/.test(source))
      .map(([path]) => path);
    expect(Object.keys(sources).length).toBeGreaterThan(50);
    expect(offenders).toEqual([]);
  });
});
