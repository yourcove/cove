import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { PaginationControls } from "../components/PaginationControls";

function renderSlots(page: number, totalPages: number) {
  const view = render(<PaginationControls page={page} totalPages={totalPages} goTo={vi.fn()} />);
  const slots = Array.from(
    view.container.querySelectorAll('button[aria-label^="Page "], span[aria-hidden="true"]'),
  ).map((element) => element.textContent);
  view.unmount();
  return slots;
}

describe("PaginationControls", () => {
  it("renders the same number of page slots on every page so the arrows never move", () => {
    const totalPages = 14;
    for (let page = 1; page <= totalPages; page += 1) {
      expect(renderSlots(page, totalPages), `page ${page}`).toHaveLength(7);
    }
  });

  it("keeps the neighbouring pages reachable at the start, middle and end", () => {
    expect(renderSlots(4, 14)).toEqual(["1", "2", "3", "4", "5", "…", "14"]);
    expect(renderSlots(7, 14)).toEqual(["1", "…", "6", "7", "8", "…", "14"]);
    expect(renderSlots(11, 14)).toEqual(["1", "…", "10", "11", "12", "13", "14"]);
  });

  it("shows every page without ellipses for short lists", () => {
    expect(renderSlots(2, 5)).toEqual(["1", "2", "3", "4", "5"]);
    render(<PaginationControls page={2} totalPages={5} goTo={vi.fn()} />);
    expect(screen.getByRole("button", { name: "Page 2" })).toHaveAttribute("aria-current", "page");
  });
});
