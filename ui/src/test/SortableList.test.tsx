import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { SortableList } from "../components/SortableList";

function renderList(onReorder = vi.fn()) {
  render(
    <div>
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
      />
    </div>,
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

it("finds the outer drop target through nested sortable rows", () => {
  const onReorder = vi.fn();
  render(
    <SortableList
      items={["First", "Second"]}
      getKey={(item) => item}
      onReorder={onReorder}
      renderItem={(item, { dragHandleProps }) => (
        <div>
          <button {...dragHandleProps}>{item}</button>
          <SortableList
            items={[item + " step"]}
            getKey={(step) => step}
            onReorder={vi.fn()}
            renderItem={(step) => <span>{step}</span>}
          />
        </div>
      )}
    />,
  );
  const handle = screen.getAllByRole("button")[0];
  Object.defineProperty(document, "elementFromPoint", {
    configurable: true,
    value: () => screen.getByText("Second step"),
  });
  fireEvent.pointerDown(handle, { pointerId: 1, isPrimary: true, button: 0, clientX: 10, clientY: 100 });
  fireEvent.pointerMove(handle, { pointerId: 1, isPrimary: true, clientX: 10, clientY: 200 });
  fireEvent.pointerUp(handle, { pointerId: 1, isPrimary: true, button: 0 });
  expect(onReorder).toHaveBeenCalledWith(["Second", "First"]);
});

it("scrolls the surrounding dialog at its edge during a pointer drag", () => {
  const onReorder = renderList();
  const list = screen.getByRole("list");
  const container = list.parentElement!;
  container.style.overflowY = "auto";
  Object.defineProperties(container, { scrollHeight: { value: 1000 }, clientHeight: { value: 300 } });
  container.getBoundingClientRect = () => ({ top: 100, bottom: 400 }) as DOMRect;
  container.scrollBy = vi.fn();
  const handle = screen.getAllByRole("button")[0];
  Object.defineProperty(document, "elementFromPoint", { configurable: true, value: () => list.firstElementChild });
  fireEvent.pointerDown(handle, { pointerId: 1, isPrimary: true, button: 0, clientX: 10, clientY: 200 });
  fireEvent.pointerMove(handle, { pointerId: 1, isPrimary: true, clientX: 10, clientY: 385 });
  expect(container.scrollBy).toHaveBeenCalledWith({ top: 12, behavior: "auto" });
  fireEvent.pointerCancel(handle, { pointerId: 1 });
  expect(onReorder).not.toHaveBeenCalled();
});
