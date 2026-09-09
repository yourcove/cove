import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { TagProvenanceHover } from "../components/TagProvenanceHover";
import type { TagProvenance } from "../api/types";

const provenance: TagProvenance[] = [
  { sourceKey: "ext:ai.tagging", appliedAt: "2026-06-10T17:17:29Z", confidence: 1 } as TagProvenance,
];

describe("TagProvenanceHover", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  const popupCount = () => screen.queryAllByText("Tag Sources").filter((el) => !el.closest(".sr-only")).length;

  it("opens only after the hover-intent delay", () => {
    render(
      <TagProvenanceHover provenance={provenance}>
        <span>Chip</span>
      </TagProvenanceHover>,
    );

    fireEvent.mouseEnter(screen.getByText("Chip").parentElement!);
    expect(popupCount()).toBe(0);

    act(() => vi.advanceTimersByTime(500));
    expect(popupCount()).toBe(1);
  });

  it("does not open when the cursor sweeps through quickly", () => {
    render(
      <TagProvenanceHover provenance={provenance}>
        <span>Chip</span>
      </TagProvenanceHover>,
    );
    const wrapper = screen.getByText("Chip").parentElement!;

    fireEvent.mouseEnter(wrapper);
    act(() => vi.advanceTimersByTime(100));
    fireEvent.mouseLeave(wrapper);
    act(() => vi.advanceTimersByTime(1000));

    expect(popupCount()).toBe(0);
  });

  it("shows at most one popup at a time across chips", () => {
    render(
      <>
        <TagProvenanceHover provenance={provenance}>
          <span>First</span>
        </TagProvenanceHover>
        <TagProvenanceHover provenance={provenance}>
          <span>Second</span>
        </TagProvenanceHover>
      </>,
    );

    fireEvent.mouseEnter(screen.getByText("First").parentElement!);
    act(() => vi.advanceTimersByTime(500));
    expect(popupCount()).toBe(1);

    fireEvent.mouseEnter(screen.getByText("Second").parentElement!);
    act(() => vi.advanceTimersByTime(500));
    expect(popupCount()).toBe(1);
  });

  it("dismisses the popup on mousedown so click-opened menus are not covered", () => {
    render(
      <TagProvenanceHover provenance={provenance}>
        <span>Chip</span>
      </TagProvenanceHover>,
    );
    const wrapper = screen.getByText("Chip").parentElement!;

    fireEvent.mouseEnter(wrapper);
    act(() => vi.advanceTimersByTime(500));
    expect(popupCount()).toBe(1);

    fireEvent.mouseDown(wrapper);
    expect(popupCount()).toBe(0);
  });

  it("does not reopen via the focus that follows a pointer press", () => {
    render(
      <TagProvenanceHover provenance={provenance}>
        <button type="button">Chip</button>
      </TagProvenanceHover>,
    );
    const button = screen.getByRole("button", { name: "Chip" });
    const wrapper = button.parentElement!;

    fireEvent.mouseEnter(wrapper);
    act(() => vi.advanceTimersByTime(500));
    expect(popupCount()).toBe(1);

    // A pointer press delivers mousedown then focus synchronously; the popup must stay dismissed.
    fireEvent.mouseDown(button);
    fireEvent.focus(button);
    expect(popupCount()).toBe(0);

    // A later keyboard focus (no preceding mousedown) still opens it for accessibility.
    act(() => vi.advanceTimersByTime(10));
    fireEvent.blur(button);
    fireEvent.focus(button);
    expect(popupCount()).toBe(1);
  });

  it("stays open when the popup itself is pressed (scrollbar drags)", () => {
    render(
      <TagProvenanceHover provenance={provenance}>
        <span>Chip</span>
      </TagProvenanceHover>,
    );
    const wrapper = screen.getByText("Chip").parentElement!;

    fireEvent.mouseEnter(wrapper);
    act(() => vi.advanceTimersByTime(500));
    expect(popupCount()).toBe(1);

    const popup = screen.getAllByText("Tag Sources").find((el) => !el.closest(".sr-only"))!.parentElement!;
    fireEvent.mouseDown(popup);
    expect(popupCount()).toBe(1);
  });

  it("stays open when a click originates inside the portalled popup", () => {
    render(
      <TagProvenanceHover provenance={provenance}>
        <span>Chip</span>
      </TagProvenanceHover>,
    );
    const wrapper = screen.getByText("Chip").parentElement!;

    fireEvent.mouseEnter(wrapper);
    act(() => vi.advanceTimersByTime(500));
    const popup = screen.getAllByText("Tag Sources").find((el) => !el.closest(".sr-only"))!.parentElement!;

    fireEvent.click(popup);

    expect(popupCount()).toBe(1);
  });

  it("measures the popup and places it above a chip near the viewport bottom", () => {
    vi.spyOn(window, "innerWidth", "get").mockReturnValue(1000);
    vi.spyOn(window, "innerHeight", "get").mockReturnValue(600);
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      if (this.classList.contains("cursor-help")) {
        return {
          x: 400,
          y: 550,
          left: 400,
          top: 550,
          right: 500,
          bottom: 580,
          width: 100,
          height: 30,
          toJSON: () => ({}),
        } as DOMRect;
      }
      if (this.classList.contains("fixed") && this.textContent?.includes("Tag Sources")) {
        return {
          x: 212,
          y: 0,
          left: 212,
          top: 0,
          right: 500,
          bottom: 300,
          width: 288,
          height: 300,
          toJSON: () => ({}),
        } as DOMRect;
      }
      return { x: 0, y: 0, left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0, toJSON: () => ({}) } as DOMRect;
    });
    render(
      <TagProvenanceHover provenance={provenance}>
        <span>Chip</span>
      </TagProvenanceHover>,
    );

    fireEvent.focus(screen.getByText("Chip").parentElement!);

    const popup = screen.getAllByText("Tag Sources").find((el) => !el.closest(".sr-only"))!.parentElement!;
    expect(popup).toHaveStyle({ left: "212px", top: "242px" });
  });
});
