import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AutocompleteDropdown } from "../components/AutocompleteDropdown";

function rect(left: number, top: number, width: number, height: number): DOMRect {
  return {
    x: left,
    y: top,
    left,
    top,
    right: left + width,
    bottom: top + height,
    width,
    height,
    toJSON: () => ({}),
  };
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("AutocompleteDropdown portal layout", () => {
  it("keeps a shared static portal positioned until its last dropdown releases it", () => {
    const portal = document.createElement("div");
    const firstAnchor = document.createElement("input");
    const secondAnchor = document.createElement("input");
    document.body.append(portal, firstAnchor, secondAnchor);
    const firstRef = { current: firstAnchor };
    const secondRef = { current: secondAnchor };

    const view = render(
      <>
        <AutocompleteDropdown key="first" anchorRef={firstRef} portalContainer={portal}>
          First
        </AutocompleteDropdown>
        <AutocompleteDropdown key="second" anchorRef={secondRef} portalContainer={portal}>
          Second
        </AutocompleteDropdown>
      </>,
    );
    expect(portal).toHaveStyle({ position: "relative" });

    view.rerender(
      <AutocompleteDropdown key="second" anchorRef={secondRef} portalContainer={portal}>
        Second
      </AutocompleteDropdown>,
    );
    expect(portal).toHaveStyle({ position: "relative" });

    view.unmount();
    expect(portal.style.position).toBe("");
    portal.remove();
    firstAnchor.remove();
    secondAnchor.remove();
  });

  it("uses document coordinates when choosing placement in a scrolled viewport", () => {
    vi.spyOn(window, "scrollX", "get").mockReturnValue(0);
    vi.spyOn(window, "scrollY", "get").mockReturnValue(500);
    const anchor = document.createElement("input");
    document.body.append(anchor);
    vi.spyOn(anchor, "getBoundingClientRect").mockReturnValue(rect(20, 700, 200, 40));

    render(
      <AutocompleteDropdown anchorRef={{ current: anchor }} maxHeight={160} data-testid="dropdown">
        Result
      </AutocompleteDropdown>,
    );

    expect(screen.getByTestId("dropdown")).toHaveStyle({
      left: "20px",
      top: "1196px",
      width: "200px",
      maxHeight: "160px",
      transform: "translateY(-100%)",
    });
    anchor.remove();
  });

  it("positions against the padding edge of a bordered portal", () => {
    const portal = document.createElement("div");
    const anchor = document.createElement("input");
    document.body.append(portal, anchor);
    vi.spyOn(portal, "getBoundingClientRect").mockReturnValue(rect(100, 200, 800, 600));
    Object.defineProperties(portal, {
      clientLeft: { configurable: true, value: 1 },
      clientTop: { configurable: true, value: 1 },
      clientHeight: { configurable: true, value: 598 },
    });
    vi.spyOn(anchor, "getBoundingClientRect").mockReturnValue(rect(120, 240, 200, 40));

    render(
      <AutocompleteDropdown anchorRef={{ current: anchor }} portalContainer={portal} data-testid="dropdown">
        Result
      </AutocompleteDropdown>,
    );

    expect(screen.getByTestId("dropdown")).toHaveStyle({
      left: "19px",
      top: "83px",
      width: "200px",
    });
    portal.remove();
    anchor.remove();
  });
});
