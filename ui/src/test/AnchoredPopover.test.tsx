import { act, fireEvent, render, screen } from "@testing-library/react";
import { useRef, useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AnchoredPopover } from "../components/duplicates/AnchoredPopover";

function Harness({ label, align, width = 320 }: { label?: string; align?: "start" | "end"; width?: number }) {
  const anchorRef = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(true);
  return (
    <>
      <button ref={anchorRef} type="button">
        Anchor
      </button>
      <button type="button">Elsewhere</button>
      <AnchoredPopover
        anchorRef={anchorRef}
        open={open}
        onClose={() => setOpen(false)}
        label={label}
        align={align}
        width={width}
      >
        <button type="button">Inside</button>
        <button type="button">Last inside</button>
      </AnchoredPopover>
    </>
  );
}

function placeAnchor(rect: Partial<DOMRect>) {
  vi.spyOn(HTMLButtonElement.prototype, "getBoundingClientRect").mockReturnValue({
    left: 100,
    right: 200,
    top: 100,
    bottom: 140,
    width: 100,
    height: 40,
    x: 100,
    y: 100,
    toJSON: () => ({}),
    ...rect,
  } as DOMRect);
}

function panel() {
  return screen.getByRole("button", { name: "Inside" }).parentElement as HTMLElement;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("AnchoredPopover", () => {
  it("lines up with the anchor's right edge by default and its left edge when asked", () => {
    placeAnchor({ left: 400, right: 500 });
    const { unmount } = render(<Harness width={200} />);
    expect(panel().style.left).toBe("300px");
    unmount();

    render(<Harness width={200} align="start" />);
    expect(panel().style.left).toBe("400px");
  });

  it("is never wider than the screen", () => {
    placeAnchor({});
    vi.spyOn(window, "innerWidth", "get").mockReturnValue(320);
    render(<Harness width={360} align="start" />);
    expect(panel().style.width).toBe("304px");
    expect(panel().style.left).toBe("8px");
  });

  it("opens above an anchor near the bottom of the screen, scrolling within the room there", () => {
    vi.spyOn(window, "innerHeight", "get").mockReturnValue(800);
    placeAnchor({ top: 700, bottom: 740 });
    render(<Harness />);
    expect(panel().style.bottom).toBe("104px");
    expect(panel().style.top).toBe("");
    expect(panel().style.maxHeight).toBe("688px");
    expect(panel().style.overflowY).toBe("auto");
  });

  it("stays a plain panel without a label", () => {
    placeAnchor({});
    render(<Harness />);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(panel()).not.toHaveFocus();
  });

  it("as a dialog, closes when focus leaves it and its anchor", () => {
    placeAnchor({});
    render(<Harness label="Faces" />);
    expect(screen.getByRole("dialog", { name: "Faces" })).toHaveFocus();

    act(() => screen.getByRole("button", { name: "Inside" }).focus());
    act(() => screen.getByRole("button", { name: "Anchor" }).focus());
    expect(screen.getByRole("dialog", { name: "Faces" })).toBeInTheDocument();

    act(() => screen.getByRole("button", { name: "Elsewhere" }).focus());
    expect(screen.queryByRole("dialog", { name: "Faces" })).not.toBeInTheDocument();
  });

  it("as a dialog, hands focus back to its anchor when Tab leaves either end", () => {
    placeAnchor({});
    const { unmount } = render(<Harness label="Faces" />);
    act(() => screen.getByRole("button", { name: "Last inside" }).focus());
    fireEvent.keyDown(screen.getByRole("button", { name: "Last inside" }), { key: "Tab" });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Anchor" })).toHaveFocus();
    unmount();

    render(<Harness label="Faces" />);
    act(() => screen.getByRole("button", { name: "Inside" }).focus());
    fireEvent.keyDown(screen.getByRole("button", { name: "Inside" }), { key: "Tab" });
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    fireEvent.keyDown(screen.getByRole("button", { name: "Inside" }), { key: "Tab", shiftKey: true });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Anchor" })).toHaveFocus();
  });

  it("without a label, stays open when focus moves elsewhere", () => {
    placeAnchor({});
    render(<Harness />);
    act(() => screen.getByRole("button", { name: "Elsewhere" }).focus());
    expect(screen.getByRole("button", { name: "Inside" })).toBeInTheDocument();
  });
});
