import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { SortableList } from "../components/SortableList";

function renderList(onReorder = vi.fn()) {
  render(
    <SortableList
      items={["First", "Second", "Third"]}
      getKey={(item) => item}
      onReorder={onReorder}
      renderItem={(item, { dragHandleProps }) => (
        <div>
          <span {...dragHandleProps}>{item} handle</span>
          <span>{item}</span>
        </div>
      )}
    />,
  );
  return onReorder;
}

describe("SortableList", () => {
  it("reorders with pointer input initiated from the handle", () => {
    const onReorder = renderList();
    const firstHandle = screen.getAllByRole("button", { name: "Pick up item to reorder" })[0];
    const thirdRow = screen.getByText("Third").closest("[data-sortable-index]") as HTMLElement;
    Object.defineProperty(document, "elementFromPoint", { configurable: true, value: vi.fn(() => thirdRow) });

    fireEvent.pointerDown(firstHandle, { pointerId: 1, isPrimary: true, button: 0, clientX: 10, clientY: 10 });
    fireEvent.pointerMove(firstHandle, { pointerId: 1, isPrimary: true, clientX: 10, clientY: 80 });
    fireEvent.pointerUp(firstHandle, { pointerId: 1, isPrimary: true, button: 0, clientX: 10, clientY: 80 });

    expect(onReorder).toHaveBeenCalledWith(["Second", "Third", "First"]);
  });

  it("does not make the entire row natively draggable", () => {
    renderList();
    expect(screen.getByText("First").closest("[data-sortable-index]")).not.toHaveAttribute("draggable");
    expect(screen.getAllByRole("button", { name: "Pick up item to reorder" })[0]).toHaveStyle({
      display: "inline-flex",
      minWidth: "44px",
      minHeight: "44px",
      touchAction: "none",
    });
  });

  it("keeps keyboard reordering available from the handle", () => {
    const onReorder = renderList();
    const firstHandle = screen.getAllByRole("button", { name: "Pick up item to reorder" })[0];

    fireEvent.keyDown(firstHandle, { key: "Enter" });
    fireEvent.keyDown(firstHandle, { key: "ArrowDown" });

    expect(onReorder).toHaveBeenCalledWith(["Second", "First", "Third"]);
  });
});
