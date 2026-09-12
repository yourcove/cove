import { defaultRatingSystemOptions } from "../components/Rating";
import { useState } from "react";
import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { DateCriterion } from "../api/types";
import { CriterionEditor } from "../components/CriterionEditor";
import { DateEditor, TimestampEditor } from "../components/PrimitiveCriterionEditors";
import { isCriterionValueValid } from "../components/filterCriterionState";
import { formatFilterChipValue } from "../components/ActiveObjectFilterChips";
import { describeFilterExpressionCondition } from "../components/filterExpressionExplanation";
import type { CriterionDefinition } from "../components/filterCriteriaTypes";
import { readTimestampCriterion } from "../pages/segments/segmentCriteriaDefinitions";
import { parseDateFilterValue } from "../utils/relativeDate";

const definition: CriterionDefinition = { id: "date", label: "Date", type: "date", filterKey: "dateCriterion" };
const relative: DateCriterion = { value: "-1m", value2: "+7d", modifier: "BETWEEN" };

function Editor({ timestamp = false, initial = undefined as DateCriterion | undefined }) {
  const [value, setValue] = useState(initial);
  const Component = timestamp ? TimestampEditor : DateEditor;
  return (
    <>
      <Component
        value={value}
        onChange={(next) => setValue(next as DateCriterion)}
        modifiers={["EQUALS", "BETWEEN", "IS_NULL"]}
      />
      <output data-testid="value">{JSON.stringify(value)}</output>
    </>
  );
}

describe("relative date filters", () => {
  it.each([false, true])("accepts offsets directly in the existing value input (timestamp=%s)", (timestamp) => {
    render(<Editor timestamp={timestamp} />);
    expect(screen.queryByLabelText("Date mode")).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Value"), { target: { value: "-1m" } });
    expect(JSON.parse(screen.getByTestId("value").textContent!)).toEqual({ value: "-1m", modifier: "EQUALS" });
    expect(screen.getByLabelText("Value")).toBeValid();
  });

  it("allows a relative expression in each Between bound", () => {
    render(<Editor initial={relative} />);
    expect(screen.getByLabelText("Minimum")).toHaveValue("-1m");
    expect(screen.getByLabelText("Maximum")).toHaveValue("+7d");
    expect(isCriterionValueValid(relative, definition)).toBe(true);
  });

  it("accepts combined offsets in largest-to-smallest order", () => {
    render(<Editor />);
    fireEvent.change(screen.getByLabelText("Value"), { target: { value: "-1y3m" } });
    expect(JSON.parse(screen.getByTestId("value").textContent!)).toEqual({ value: "-1y3m", modifier: "EQUALS" });
    expect(screen.getByLabelText("Value")).toBeValid();
    expect(formatFilterChipValue(definition, { value: "-1y3m", modifier: "EQUALS" })).toBe("1 year 3 months ago");
  });

  it("supports hour offsets for timestamps but not date-only criteria", () => {
    render(<Editor timestamp />);
    fireEvent.change(screen.getByLabelText("Value"), { target: { value: "-6h" } });
    expect(screen.getByLabelText("Value")).toBeValid();
    expect(
      formatFilterChipValue({ ...definition, type: "timestamp" }, { value: "-6h", modifier: "GREATER_THAN" }),
    ).toBe("Later than 6 hours ago");
    expect(isCriterionValueValid({ value: "-6h", modifier: "EQUALS" }, definition)).toBe(false);
  });

  it("rejects malformed unsigned combined offsets", () => {
    expect(isCriterionValueValid({ value: "1y+3m", modifier: "EQUALS" }, definition)).toBe(false);
    expect(isCriterionValueValid({ value: "1y-3m", modifier: "EQUALS" }, definition)).toBe(false);
  });

  it("does not classify compact absolute dates as relative expressions", () => {
    expect(isCriterionValueValid({ value: "1May2024", modifier: "EQUALS" }, definition)).toBe(true);
    expect(isCriterionValueValid({ value: "1Dec2024", modifier: "EQUALS" }, definition)).toBe(true);
  });

  it("accepts 0d as the present endpoint of a rolling range", () => {
    const rollingWeek: DateCriterion = { value: "-7d", value2: "0d", modifier: "BETWEEN" };
    expect(isCriterionValueValid(rollingWeek, definition)).toBe(true);
    expect(formatFilterChipValue(definition, rollingWeek)).toBe("Between 7 days ago and today");
    expect(formatFilterChipValue({ ...definition, type: "timestamp" }, rollingWeek)).toBe("Between 7 days ago and now");
  });

  it.each([
    { ...definition, customFieldKey: "custom_date" },
    { ...definition, filterKey: "extension-filter:example:date" },
  ])("keeps relative expressions unavailable for custom and extension criteria", (criterion) => {
    render(<CriterionEditor criterion={criterion} value={undefined} onChange={() => {}} />);
    const input = screen.getByLabelText("Value");
    fireEvent.change(input, { target: { value: "+7d" } });
    expect(input).toBeInvalid();
    expect(formatFilterChipValue(criterion, { value: "+7d", modifier: "EQUALS" })).toBe("= +7d");
  });

  it("summarizes relative expressions like other date values", () => {
    expect(formatFilterChipValue(definition, relative)).toBe("Between 1 month ago and 7 days from now");
    expect(
      describeFilterExpressionCondition({ dateCriterion: relative }, [definition], defaultRatingSystemOptions, []),
    ).toContain("-1m and +7d");
  });

  it.each([
    ["BETWEEN", "-21d", "-14d", "Between 14 and 21 days ago"],
    ["BETWEEN", "-14d", "-21d", "Between 14 and 21 days ago"],
    ["NOT_BETWEEN", "+1m", "+2m", "Not Between 1 and 2 months from now"],
  ])("compacts same-unit relative ranges", (modifier, value, value2, expected) => {
    expect(formatFilterChipValue(definition, { modifier, value, value2 })).toBe(expected);
  });

  it.each([
    ["-30d", "GREATER_THAN", "Later than 30 days ago"],
    ["+1w", "LESS_THAN", "Earlier than 1 week from now"],
    ["-1y", "EQUALS", "1 year ago"],
    ["+0d", "EQUALS", "today"],
  ])("formats %s as a human-readable date chip", (value, modifier, expected) => {
    expect(formatFilterChipValue(definition, { value, modifier })).toBe(expected);
  });

  it("uses now for a zero timestamp offset", () => {
    expect(formatFilterChipValue({ ...definition, type: "timestamp" }, { value: "-0m", modifier: "EQUALS" })).toBe(
      "now",
    );
  });

  it("keeps exact date chips unchanged when an unused relative second value remains", () => {
    expect(
      formatFilterChipValue(definition, {
        value: "2026-09-01",
        value2: "+7d",
        modifier: "GREATER_THAN",
      }),
    ).toBe("> 2026-09-01");
  });

  it("preserves relative segment timestamps for server evaluation", () => {
    expect(readTimestampCriterion(relative)).toEqual(relative);
  });

  it("resolves browser-side date and timestamp offsets from a supplied reference", () => {
    const reference = new Date("2024-03-31T12:34:56.789Z");

    expect(parseDateFilterValue("-1m", reference, true)).toBe(Date.parse("2024-02-29T00:00:00.000Z"));
    expect(parseDateFilterValue("+1y", reference)).toBe(Date.parse("2025-03-31T12:34:56.789Z"));
    expect(parseDateFilterValue("-7d", reference)).toBe(Date.parse("2024-03-24T12:34:56.789Z"));
    expect(parseDateFilterValue("-1y3m", reference, true)).toBe(Date.parse("2022-12-31T00:00:00.000Z"));
    expect(parseDateFilterValue("+1m2w3d", reference)).toBe(Date.parse("2024-05-17T12:34:56.789Z"));
    expect(parseDateFilterValue("-6h", reference)).toBe(Date.parse("2024-03-31T06:34:56.789Z"));
    expect(parseDateFilterValue("-1d6h", reference)).toBe(Date.parse("2024-03-30T06:34:56.789Z"));
    expect(parseDateFilterValue("-6h", reference, true)).toBeNaN();
    expect(parseDateFilterValue("0d", reference, true)).toBe(Date.parse("2024-03-31T00:00:00.000Z"));
    expect(parseDateFilterValue("0d", reference)).toBe(reference.getTime());
  });
});
