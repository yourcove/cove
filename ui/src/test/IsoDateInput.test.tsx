import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { IsoDateInput, isValidPartialIsoDate } from "../components/IsoDateInput";

describe("IsoDateInput", () => {
  it.each(["", "2026", "2026-05", "2026-05-01"])("accepts partial ISO date %s", (value) => {
    expect(isValidPartialIsoDate(value)).toBe(true);
  });

  it.each(["2026-13", "2026-02-30", "26", "not-a-date"])("rejects invalid partial ISO date %s", (value) => {
    expect(isValidPartialIsoDate(value)).toBe(false);
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
