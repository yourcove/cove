import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ScrapeRelationChoices, type ScrapeRelationActionMap } from "../components/ScrapeRelationChoices";

describe("ScrapeRelationChoices", () => {
  it("renders compact clickable relation chips", () => {
    const actions: ScrapeRelationActionMap = {
      current: "include",
      new: "create",
      skipped: "exclude",
    };
    const onActionChange = vi.fn();

    render(
      <ScrapeRelationChoices
        names={["Current", "New", "Skipped"]}
        currentNames={["Current"]}
        existingNames={[]}
        actions={actions}
        onActionChange={onActionChange}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Current: Current" }));
    fireEvent.click(screen.getByRole("button", { name: "New: Will create" }));
    fireEvent.click(screen.getByRole("button", { name: "Skipped: Excluded" }));

    expect(onActionChange).toHaveBeenNthCalledWith(1, "Current", "exclude");
    expect(onActionChange).toHaveBeenNthCalledWith(2, "New", "exclude");
    expect(onActionChange).toHaveBeenNthCalledWith(3, "Skipped", "create");
  });

  it("surfaces the matched primary name in the tooltip for alias matches", () => {
    render(
      <ScrapeRelationChoices
        names={["Myra Moans"]}
        currentNames={[]}
        existingNames={["Myra Moans"]}
        matchInfo={{ "myra moans": "Jane Doe" }}
        actions={{ "myra moans": "include" }}
        onActionChange={vi.fn()}
      />,
    );

    const chip = screen.getByRole("button", { name: "Myra Moans: Existing" });
    expect(chip.getAttribute("title")).toContain("Jane Doe");
  });
});
