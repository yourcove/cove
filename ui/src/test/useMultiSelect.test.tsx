import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { useState } from "react";
import { useMultiSelect } from "../hooks/useMultiSelect";

function MultiSelectProbe() {
  const [items, setItems] = useState([{ id: 1 }, { id: 2 }, { id: 3 }, { id: 4 }]);
  const [resetKey, setResetKey] = useState("initial");
  const { selectedIds, toggle } = useMultiSelect(items, { preserveOnItemsChange: true, resetKey, isSelectable: (item) => item.id !== 3 });

  return (
    <div>
      <div data-testid="selected">{[...selectedIds].join(",")}</div>
      <button type="button" onClick={() => toggle(1)}>Toggle first</button>
      <button type="button" onClick={() => toggle(4, { range: true })}>Range to fourth</button>
      <button type="button" onClick={() => toggle(2, { range: true })}>Range to second</button>
      <button type="button" onClick={() => toggle(1, { range: true })}>Range to first</button>
      <button type="button" onClick={() => toggle(4, { range: true, orderedIds: [1, 4] })}>Visible range to fourth</button>
      <button type="button" onClick={() => setItems((current) => [...current, { id: 5 }])}>Append</button>
      <button type="button" onClick={() => setItems([{ id: 9 }])}>Replace</button>
      <button type="button" onClick={() => setResetKey("changed")}>Reset query</button>
    </div>
  );
}

function LegacyPreserveProbe() {
  const [items, setItems] = useState([{ id: 1 }]);
  const { selectedIds, toggle } = useMultiSelect(items, { preserveOnAppend: true });

  return (
    <div>
      <div data-testid="selected">{[...selectedIds].join(",")}</div>
      <button type="button" onClick={() => toggle(1)}>Toggle first</button>
      <button type="button" onClick={() => setItems((current) => [...current, { id: 2 }])}>Append</button>
    </div>
  );
}

function IdSelectableProbe() {
  const items = [{ id: 1 }, { id: 2 }, { id: 3 }, { id: 4 }];
  const { selectedIds, toggle, selectAll, selectIds, invertSelection } = useMultiSelect(items, {
    isSelectable: (item) => item.id !== 2,
    isSelectableId: (id) => id !== 4,
  });

  return (
    <div>
      <div data-testid="selected">{[...selectedIds].join(",")}</div>
      <button type="button" onClick={() => selectIds([1, 2, 3, 4, 5])}>Select ids</button>
      <button type="button" onClick={() => selectAll()}>Select all</button>
      <button type="button" onClick={() => invertSelection()}>Invert</button>
      <button type="button" onClick={() => toggle(2)}>Toggle item-blocked</button>
      <button type="button" onClick={() => toggle(4)}>Toggle id-blocked</button>
      <button type="button" onClick={() => toggle(5)}>Toggle unloaded</button>
      <button type="button" onClick={() => toggle(1)}>Toggle first</button>
      <button type="button" onClick={() => toggle(5, { range: true, orderedIds: [1, 2, 3, 4, 5] })}>Range to unloaded</button>
    </div>
  );
}

describe("useMultiSelect", () => {
  it("selects a contiguous visible range from the last toggled item and skips unselectable items", async () => {
    const user = userEvent.setup();

    render(<MultiSelectProbe />);

    await user.click(screen.getByRole("button", { name: "Toggle first" }));
    await user.click(screen.getByRole("button", { name: "Range to fourth" }));

    expect(screen.getByTestId("selected")).toHaveTextContent("1,2,4");
  });

  it("keeps the selected range when the range target is already selected", async () => {
    const user = userEvent.setup();

    render(<MultiSelectProbe />);

    await user.click(screen.getByRole("button", { name: "Toggle first" }));
    await user.click(screen.getByRole("button", { name: "Range to fourth" }));
    await user.click(screen.getByRole("button", { name: "Range to second" }));

    expect(screen.getByTestId("selected")).toHaveTextContent("1,2,4");
  });

  it("uses the provided visible order for range selection", async () => {
    const user = userEvent.setup();

    render(<MultiSelectProbe />);

    await user.click(screen.getByRole("button", { name: "Toggle first" }));
    await user.click(screen.getByRole("button", { name: "Visible range to fourth" }));

    expect(screen.getByTestId("selected")).toHaveTextContent("1,4");
  });

  it("preserves selection for infinite list window changes until the query resets", async () => {
    const user = userEvent.setup();

    render(<MultiSelectProbe />);

    await user.click(screen.getByRole("button", { name: "Toggle first" }));
    expect(screen.getByTestId("selected")).toHaveTextContent("1");

    await user.click(screen.getByRole("button", { name: "Append" }));

    await waitFor(() => {
      expect(screen.getByTestId("selected")).toHaveTextContent("1");
    });

    await user.click(screen.getByRole("button", { name: "Replace" }));

    await waitFor(() => {
      expect(screen.getByTestId("selected")).toHaveTextContent("1");
    });

    await user.click(screen.getByRole("button", { name: "Reset query" }));

    await waitFor(() => {
      expect(screen.getByTestId("selected")).toBeEmptyDOMElement();
    });
  });

  it("accepts the deprecated preserveOnAppend alias while callers migrate", async () => {
    const user = userEvent.setup();

    render(<LegacyPreserveProbe />);

    await user.click(screen.getByRole("button", { name: "Toggle first" }));
    await user.click(screen.getByRole("button", { name: "Append" }));

    await waitFor(() => {
      expect(screen.getByTestId("selected")).toHaveTextContent("1");
    });
  });

  it("filters explicit ids with both item-level and id-level selectability", async () => {
    const user = userEvent.setup();

    render(<IdSelectableProbe />);

    await user.click(screen.getByRole("button", { name: "Select ids" }));

    expect(screen.getByTestId("selected")).toHaveTextContent("1,3,5");
  });

  it("uses id-level selectability for bulk helpers", async () => {
    const user = userEvent.setup();

    render(<IdSelectableProbe />);

    await user.click(screen.getByRole("button", { name: "Select all" }));
    expect(screen.getByTestId("selected")).toHaveTextContent("1,3");

    await user.click(screen.getByRole("button", { name: "Invert" }));
    expect(screen.getByTestId("selected")).toBeEmptyDOMElement();
  });

  it("uses id-level selectability for toggle targets and ranges", async () => {
    const user = userEvent.setup();

    render(<IdSelectableProbe />);

    await user.click(screen.getByRole("button", { name: "Toggle item-blocked" }));
    await user.click(screen.getByRole("button", { name: "Toggle id-blocked" }));
    expect(screen.getByTestId("selected")).toBeEmptyDOMElement();

    await user.click(screen.getByRole("button", { name: "Toggle unloaded" }));
    expect(screen.getByTestId("selected")).toHaveTextContent("5");

    await user.click(screen.getByRole("button", { name: "Toggle first" }));
    await user.click(screen.getByRole("button", { name: "Range to unloaded" }));
    expect(screen.getByTestId("selected")).toHaveTextContent("5,1,3");
  });
});
