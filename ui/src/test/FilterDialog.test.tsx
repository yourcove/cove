import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { FilterDialog, PERFORMER_CRITERIA, SCENE_CRITERIA } from "../components/FilterDialog";

describe("FilterDialog", () => {
  it("resyncs its local edit state when the active filter changes outside the dialog", () => {
    const onApply = vi.fn();
    const onClose = vi.fn();

    const { rerender } = render(
      <FilterDialog
        open
        onClose={onClose}
        criteria={SCENE_CRITERIA}
        activeFilter={{ titleCriterion: { value: "Cloud Nine", modifier: "EQUALS" } }}
        onApply={onApply}
      />
    );

    expect(screen.getAllByText("Title")).toHaveLength(2);

    rerender(
      <FilterDialog
        open
        onClose={onClose}
        criteria={SCENE_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    expect(screen.getAllByText("Title")).toHaveLength(1);
  });

  it("applies multi-select performer gender filters as a regex-backed criterion", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={PERFORMER_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Gender"));
    fireEvent.click(screen.getByLabelText("Male"));
    fireEvent.click(screen.getByLabelText("Female"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      genderCriterion: expect.objectContaining({
        modifier: "MATCHES_REGEX",
        value: "^(?:Male|Female)$",
        _selectedValues: ["Male", "Female"],
      }),
    }));
  });
});