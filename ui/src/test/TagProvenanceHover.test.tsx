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
  });

  const popupCount = () => screen.queryAllByText("Tag Sources").filter((el) => !el.closest(".sr-only")).length;

  it("opens only after the hover-intent delay", () => {
    render(<TagProvenanceHover provenance={provenance}><span>Chip</span></TagProvenanceHover>);

    fireEvent.mouseEnter(screen.getByText("Chip").parentElement!);
    expect(popupCount()).toBe(0);

    act(() => vi.advanceTimersByTime(500));
    expect(popupCount()).toBe(1);
  });

  it("does not open when the cursor sweeps through quickly", () => {
    render(<TagProvenanceHover provenance={provenance}><span>Chip</span></TagProvenanceHover>);
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
        <TagProvenanceHover provenance={provenance}><span>First</span></TagProvenanceHover>
        <TagProvenanceHover provenance={provenance}><span>Second</span></TagProvenanceHover>
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
    render(<TagProvenanceHover provenance={provenance}><span>Chip</span></TagProvenanceHover>);
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
    render(<TagProvenanceHover provenance={provenance}><span>Chip</span></TagProvenanceHover>);
    const wrapper = screen.getByText("Chip").parentElement!;

    fireEvent.mouseEnter(wrapper);
    act(() => vi.advanceTimersByTime(500));
    expect(popupCount()).toBe(1);

    const popup = screen.getAllByText("Tag Sources").find((el) => !el.closest(".sr-only"))!.parentElement!;
    fireEvent.mouseDown(popup);
    expect(popupCount()).toBe(1);
  });
});
