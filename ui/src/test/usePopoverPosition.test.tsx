import { fireEvent, render, screen } from "@testing-library/react";
import { useEffect, useRef } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { FloatingActionMenu } from "../components/FloatingActionMenu";
import { TagActionMenu } from "../components/shared";
import { computePopoverPosition, type PopoverRect } from "../hooks/usePopoverPosition";

const viewport = { width: 1000, height: 800 };
const menu = { width: 220, height: 300 };

function anchorAt(left: number, top: number, size = 24): PopoverRect {
  return { left, top, right: left + size, bottom: top + size };
}

describe("computePopoverPosition", () => {
  it("opens below and lines up with the anchor's right edge when there is room", () => {
    const position = computePopoverPosition({ anchor: anchorAt(600, 100), popover: menu, viewport });

    expect(position).toMatchObject({ side: "below", align: "end", top: 128, left: 404 });
  });

  it("switches to the anchor's left edge when growing leftward would leave the viewport", () => {
    const position = computePopoverPosition({ anchor: anchorAt(20, 100), popover: menu, viewport });

    expect(position).toMatchObject({ align: "start", left: 20 });
  });

  it("switches to the anchor's right edge when a start-aligned menu would leave the viewport", () => {
    const position = computePopoverPosition({ anchor: anchorAt(900, 100), popover: menu, viewport, align: "start" });

    expect(position).toMatchObject({ align: "end", left: 704 });
  });

  it("clamps inside the padding when neither edge fits", () => {
    const narrow = { width: 360, height: 800 };
    const wide = { width: 300, height: 200 };

    expect(computePopoverPosition({ anchor: anchorAt(150, 100), popover: wide, viewport: narrow })).toMatchObject({
      align: "clamped",
      left: 8,
    });
    expect(
      computePopoverPosition({ anchor: anchorAt(150, 100), popover: wide, viewport: narrow, align: "start" }),
    ).toMatchObject({ align: "clamped", left: 52 });
  });

  it("caps the width and pins to the padding when the popover is wider than the viewport", () => {
    const position = computePopoverPosition({
      anchor: anchorAt(150, 100),
      popover: { width: 500, height: 200 },
      viewport: { width: 360, height: 800 },
    });

    expect(position).toMatchObject({ align: "clamped", left: 8, maxWidth: 344 });
  });

  it("flips above when only the space above fits", () => {
    const position = computePopoverPosition({ anchor: anchorAt(600, 600), popover: menu, viewport });

    expect(position).toMatchObject({ side: "above", top: 296, maxHeight: 588 });
  });

  it("stays below with a capped height when below is the roomier side but neither fits", () => {
    const position = computePopoverPosition({
      anchor: anchorAt(600, 200),
      popover: { width: 220, height: 900 },
      viewport,
    });

    expect(position).toMatchObject({ side: "below", top: 228, maxHeight: 564 });
  });

  it("goes above with a capped height when above is the roomier side but neither fits", () => {
    const position = computePopoverPosition({
      anchor: anchorAt(600, 500),
      popover: { width: 220, height: 900 },
      viewport,
    });

    expect(position).toMatchObject({ side: "above", top: 8, maxHeight: 488 });
  });
});

describe("FloatingActionMenu placement", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  function stubLayout(anchor: DOMRect) {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      return this.dataset.testid === "anchor" ? anchor : ({ top: 0, left: 0, right: 0, bottom: 0 } as DOMRect);
    });
    vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockImplementation(function (this: HTMLElement) {
      return this.getAttribute("role") === "menu" ? 220 : 0;
    });
    vi.spyOn(HTMLElement.prototype, "offsetHeight", "get").mockImplementation(function (this: HTMLElement) {
      return this.getAttribute("role") === "menu" ? 300 : 0;
    });
    vi.spyOn(document.documentElement, "clientWidth", "get").mockReturnValue(1000);
    vi.spyOn(document.documentElement, "clientHeight", "get").mockReturnValue(800);
  }

  function Harness() {
    const anchorRef = useRef<HTMLDivElement>(null);
    return (
      <>
        <div ref={anchorRef} data-testid="anchor" />
        <FloatingActionMenu open anchorRef={anchorRef} onClose={() => {}}>
          <button type="button">Rescan</button>
        </FloatingActionMenu>
      </>
    );
  }

  it("keeps a menu opened from a trigger near the left edge inside the viewport", () => {
    stubLayout({ left: 20, top: 100, right: 44, bottom: 124 } as DOMRect);
    render(<Harness />);

    const menuElement = screen.getByRole("menu");
    expect(menuElement.style.left).toBe("20px");
    expect(menuElement.style.top).toBe("128px");
    expect(menuElement.style.opacity).toBe("");
  });

  it("keeps the right-aligned placement when there is room", () => {
    stubLayout({ left: 600, top: 100, right: 624, bottom: 124 } as DOMRect);
    render(<Harness />);

    expect(screen.getByRole("menu").style.left).toBe("404px");
  });
});

describe("FloatingActionMenu while unmeasured", () => {
  it("keeps its items focusable so a mount effect can move focus into the menu", () => {
    let menuVisibilityAtMount = "";
    function FocusFirst() {
      const ref = useRef<HTMLButtonElement>(null);
      useEffect(() => {
        // A visibility:hidden ancestor makes browsers ignore focus(); jsdom does not, so check the style.
        menuVisibilityAtMount = getComputedStyle(ref.current!.closest('[role="menu"]')!).visibility;
        ref.current?.focus();
      }, []);
      return (
        <button ref={ref} type="button">
          First
        </button>
      );
    }
    function Harness() {
      const anchorRef = useRef<HTMLDivElement>(null);
      return (
        <>
          <div ref={anchorRef} />
          <FloatingActionMenu open anchorRef={anchorRef} onClose={() => {}}>
            <FocusFirst />
          </FloatingActionMenu>
        </>
      );
    }

    render(<Harness />);

    expect(menuVisibilityAtMount).not.toBe("hidden");
    expect(screen.getByRole("button", { name: "First" })).toHaveFocus();
  });
});

describe("FloatingActionMenu following its anchor", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("re-places the menu when the page scrolls", () => {
    let anchorTop = 100;
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      return (
        this.dataset.testid === "anchor"
          ? { left: 600, right: 624, top: anchorTop, bottom: anchorTop + 24 }
          : { top: 0, left: 0, right: 0, bottom: 0 }
      ) as DOMRect;
    });
    function Harness() {
      const anchorRef = useRef<HTMLDivElement>(null);
      return (
        <>
          <div ref={anchorRef} data-testid="anchor" />
          <FloatingActionMenu open anchorRef={anchorRef} onClose={() => {}}>
            <button type="button">Rescan</button>
          </FloatingActionMenu>
        </>
      );
    }

    render(<Harness />);
    expect(screen.getByRole("menu").style.top).toBe("128px");

    anchorTop = 40;
    fireEvent.scroll(window);
    expect(screen.getByRole("menu").style.top).toBe("68px");
  });
});

describe("TagActionMenu", () => {
  it("closes on page scroll instead of leaving its invisible backdrop behind", () => {
    render(<TagActionMenu name="Kissing" onReportIncorrect={() => {}} />);

    fireEvent.click(screen.getByRole("button", { name: "More actions for Kissing" }));
    expect(screen.getByText("This detection is wrong")).toBeInTheDocument();

    // Scrolling inside a height-capped menu keeps it open; scrolling the page closes it.
    fireEvent.scroll(screen.getByText("This detection is wrong").closest(".fixed.w-64")!);
    expect(screen.getByText("This detection is wrong")).toBeInTheDocument();

    fireEvent.scroll(window);
    expect(screen.queryByText("This detection is wrong")).not.toBeInTheDocument();
  });
});
