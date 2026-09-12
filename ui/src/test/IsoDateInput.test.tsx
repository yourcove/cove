import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { IsoDateInput, isValidPartialIsoDate, isValidRelativeDate } from "../components/IsoDateInput";

describe("IsoDateInput", () => {
  it.each(["", "2026", "2026-05", "2026-05-01"])("accepts partial ISO date %s", (value) => {
    expect(isValidPartialIsoDate(value)).toBe(true);
  });

  it.each(["2026-13", "2026-02-30", "26", "not-a-date"])("rejects invalid partial ISO date %s", (value) => {
    expect(isValidPartialIsoDate(value)).toBe(false);
  });

  it.each(["-7d", "+2w", "-1m", "+1y", "-1y3m", "+1m2w3d", "+0d", "0d", "-12M"])(
    "accepts relative date %s",
    (value) => {
      expect(isValidRelativeDate(value)).toBe(true);
    },
  );

  it("allows hour offsets only for timestamp inputs", () => {
    expect(isValidRelativeDate("-6h")).toBe(false);
    expect(isValidRelativeDate("-6h", true)).toBe(true);
    expect(isValidRelativeDate("-1d6h", true)).toBe(true);
  });

  it.each([
    "7d",
    "1y3m",
    "-1m1y",
    "-1y2y",
    "-1y+3m",
    "1y+3m",
    "1y-3m",
    "0w",
    "0m",
    "0y",
    "+d",
    "+1.5d",
    "+1h",
    "+2147483648d",
    "tomorrow",
  ])("rejects invalid relative date %s", (value) => {
    expect(isValidRelativeDate(value)).toBe(false);
  });

  it("shows the ISO value and provides a native calendar picker", () => {
    const onChange = vi.fn();
    const { container } = render(<IsoDateInput value="2026-05-01" onChange={onChange} />);

    expect(screen.getByDisplayValue("2026-05-01")).toHaveAttribute("placeholder", "yyyy-MM-dd");
    expect(screen.getByRole("button", { name: "Choose date" })).toBeInTheDocument();

    const picker = container.querySelector('input[type="date"]');
    expect(picker).not.toBeNull();
    fireEvent.change(picker!, { target: { value: "2026-06-02" } });
    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange.mock.calls[0][0].target.value).toBe("2026-06-02");
  });
});
