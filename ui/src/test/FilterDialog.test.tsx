import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { FilterDialog, PERFORMER_CRITERIA, SCENE_CRITERIA, TAG_CRITERIA } from "../components/FilterDialog";

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

  it("does not restore a removed criterion when the parent rerenders with the same active filter", () => {
    const onApply = vi.fn();
    const onClose = vi.fn();

    const { rerender } = render(
      <FilterDialog
        open
        onClose={onClose}
        criteria={SCENE_CRITERIA}
        activeFilter={{ createdAtCriterion: { value: "2026-04-22T12:00", modifier: "EQUALS" } }}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove Created At filter chip" }));
    expect(screen.queryByLabelText("Remove Created At filter chip")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Remove Created At filter row")).not.toBeInTheDocument();

    rerender(
      <FilterDialog
        open
        onClose={onClose}
        criteria={SCENE_CRITERIA}
        activeFilter={{ createdAtCriterion: { value: "2026-04-22T12:00", modifier: "EQUALS" } }}
        onApply={onApply}
      />
    );

    expect(screen.queryByLabelText("Remove Created At filter chip")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Remove Created At filter row")).not.toBeInTheDocument();
    expect(screen.getAllByText("Created At")).toHaveLength(1);
  });

  it("does not re-add an expanded timestamp criterion after removing it", () => {
    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={SCENE_CRITERIA}
        activeFilter={{ createdAtCriterion: { value: "2026-04-22T12:00", modifier: "EQUALS" } }}
        onApply={vi.fn()}
      />
    );

    fireEvent.click(screen.getAllByText("Created At")[1]);
    fireEvent.click(screen.getByRole("button", { name: "Remove Created At filter row" }));

    expect(screen.queryByLabelText("Remove Created At filter chip")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Remove Created At filter row")).not.toBeInTheDocument();
    expect(screen.getAllByText("Created At")).toHaveLength(1);
  });

  it("applies child-inclusive tag count toggles alongside the main criterion", () => {
    const onApply = vi.fn();

    render(
      <FilterDialog
        open
        onClose={vi.fn()}
        criteria={TAG_CRITERIA}
        activeFilter={{}}
        onApply={onApply}
      />
    );

    fireEvent.click(screen.getByText("Scene Count"));
    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "2" } });
    fireEvent.click(screen.getByLabelText("Count scenes from child tags"));
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      sceneCountCriterion: expect.objectContaining({
        modifier: "EQUALS",
        value: 2,
      }),
      sceneCountIncludesChildren: true,
    }));
  });

  it("renders the career length filter with a years/months unit selector", () => {
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

    fireEvent.click(screen.getByText("Career Length"));

    const unitSelect = screen.getByLabelText("Career length unit") as HTMLSelectElement;
    expect(unitSelect.value).toBe("years");

    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      careerLengthCriterion: expect.objectContaining({
        modifier: "EQUALS",
        value: 3,
      }),
    }));
  });

  it("converts career length entered in months into whole years before applying", () => {
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

    fireEvent.click(screen.getByText("Career Length"));
    fireEvent.change(screen.getByLabelText("Career length unit"), { target: { value: "months" } });
    fireEvent.change(screen.getByRole("spinbutton"), { target: { value: "30" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      careerLengthCriterion: expect.objectContaining({
        modifier: "EQUALS",
        value: 3,
      }),
    }));
  });
});