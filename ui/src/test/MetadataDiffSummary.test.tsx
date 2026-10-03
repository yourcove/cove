import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { defaultDiffSelection, type DiffField, type DiffRecord, type DiffSelection } from "../components/MetadataDiff";
import { MetadataDiffSummary } from "../components/MetadataDiffSummary";

const fields: DiffField[] = [
  { key: "title", label: "Title" },
  { key: "code", label: "Code" },
  { key: "director", label: "Director" },
  { key: "date", label: "Date" },
  {
    key: "tags",
    label: "Tags",
    kind: "list",
    itemKey: (value) => (value as { id: string }).id,
    renderItem: (value) => (value as { id: string; label: string }).label,
    itemIsNew: (value) => Boolean((value as { isNew?: boolean }).isNew),
  },
];
const source: DiffRecord = {
  label: "From StashDB",
  sentenceLabel: "StashDB",
  values: {
    title: "Summer Sunshine",
    code: "D86221FA",
    director: "Andrej Lupin",
    date: "2021-09-05",
    tags: [
      { id: "poolside", label: "Poolside" },
      { id: "wet-look", label: "Wet Look", isNew: true },
      { id: "outdoors", label: "Outdoors" },
    ],
  },
};
const target: DiffRecord = {
  label: "Current",
  sentenceLabel: "current",
  values: {
    title: "Summer Sunshine",
    code: null,
    director: "Someone Else",
    date: "2021-09-05",
    tags: [{ id: "outdoors", label: "Outdoors" }],
  },
};

describe("MetadataDiffSummary", () => {
  it("shows fills as facts, conflicts as an inline choice and folds unchanged fields into one line", () => {
    const selection = defaultDiffSelection(fields, source, target);
    render(
      <MetadataDiffSummary fields={fields} source={source} target={target} value={selection} onChange={vi.fn()} />,
    );

    const code = screen.getByText("Code").closest("[data-tone]")!;
    expect(code).toHaveAttribute("data-tone", "ok");
    expect(within(code as HTMLElement).getByText("D86221FA")).toBeInTheDocument();
    expect(within(code as HTMLElement).getByText("fills empty")).toBeInTheDocument();

    const director = screen.getByText("Director").closest("[data-tone]")!;
    expect(director).toHaveAttribute("data-tone", "warn");
    const group = within(director as HTMLElement).getByRole("radiogroup", { name: "Director choice" });
    expect(within(group).getByRole("radio", { name: "Keep current" })).toBeChecked();
    expect(within(group).getByRole("radio", { name: "Use StashDB" })).not.toBeChecked();

    const tags = screen.getByText("Tags").closest("[data-tone]")!;
    expect(
      within(tags as HTMLElement)
        .getByText("Poolside")
        .closest("[data-state]"),
    ).toHaveAttribute("data-state", "added");
    expect(
      within(tags as HTMLElement)
        .getByText("Wet Look")
        .closest("[data-state]"),
    ).toHaveAttribute("data-state", "new");
    expect(within(tags as HTMLElement).queryByText("Outdoors")).not.toBeInTheDocument();
    expect(within(tags as HTMLElement).getByText("and 1 already present")).toBeInTheDocument();
    expect(within(tags as HTMLElement).getByText("2 added (1 new)")).toBeInTheDocument();

    const unchanged = screen.getByText("Unchanged").closest("[data-tone]")!;
    expect(within(unchanged as HTMLElement).getByText("Title · Date")).toBeInTheDocument();
  });

  it("flips a conflict to the incoming side through the inline choice", async () => {
    const selection = defaultDiffSelection(fields, source, target);
    const onChange = vi.fn();
    render(
      <MetadataDiffSummary fields={fields} source={source} target={target} value={selection} onChange={onChange} />,
    );

    await userEvent.click(screen.getByRole("radio", { name: "Use StashDB" }));
    expect(onChange).toHaveBeenCalledWith({ ...selection, director: "source" });
  });

  it("reads a taken conflict and a dropped list item back as facts", () => {
    const selection: DiffSelection = {
      ...defaultDiffSelection(fields, source, target),
      director: "source",
      tags: ["poolside"],
    };
    render(
      <MetadataDiffSummary fields={fields} source={source} target={target} value={selection} onChange={vi.fn()} />,
    );

    const director = screen.getByText("Director").closest("[data-tone]")!;
    expect(director).toHaveAttribute("data-tone", "ok");
    expect(within(director as HTMLElement).getByText("replaces current")).toBeInTheDocument();

    const tags = screen.getByText("Tags").closest("[data-tone]")!;
    expect(
      within(tags as HTMLElement)
        .getByText("Outdoors")
        .closest("[data-state]"),
    ).toHaveAttribute("data-state", "removed");
    expect(within(tags as HTMLElement).getByText("1 added · 1 removed · 1 not in your library")).toBeInTheDocument();
  });

  it("offers the way back on a landed value and a Keep empty choice when a fill is declined", async () => {
    const onChange = vi.fn();
    const landed: DiffSelection = { ...defaultDiffSelection(fields, source, target), director: "source" };
    const { rerender } = render(
      <MetadataDiffSummary fields={fields} source={source} target={target} value={landed} onChange={onChange} />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Director: keep current instead" }));
    expect(onChange).toHaveBeenLastCalledWith({ ...landed, director: "target" });

    const declined: DiffSelection = { ...defaultDiffSelection(fields, source, target), code: "target" };
    rerender(
      <MetadataDiffSummary fields={fields} source={source} target={target} value={declined} onChange={onChange} />,
    );
    const code = screen.getByText("Code").closest("[data-tone]")!;
    expect(code).toHaveAttribute("data-tone", "warn");
    expect(within(code as HTMLElement).getByRole("radio", { name: "Keep empty" })).toBeChecked();
    expect(within(code as HTMLElement).getByText("keeping empty")).toBeInTheDocument();
  });

  it("shows provenance on a kept conflict and treats a cleared field as a landed change", () => {
    const provenanced: DiffRecord = { ...target, provenance: { director: "Edited by you" } };
    const cleared: DiffRecord = { ...source, values: { ...source.values, director: null } };
    const selection: DiffSelection = { ...defaultDiffSelection(fields, source, provenanced) };
    const { rerender } = render(
      <MetadataDiffSummary fields={fields} source={source} target={provenanced} value={selection} onChange={vi.fn()} />,
    );
    expect(screen.getByText("Edited by you · both have a value · keeping current")).toBeInTheDocument();

    rerender(
      <MetadataDiffSummary
        fields={fields}
        source={cleared}
        target={target}
        value={{ ...selection, director: "source" }}
        onChange={vi.fn()}
      />,
    );
    const director = screen.getByText("Director").closest("[data-tone]")!;
    expect(director).toHaveAttribute("data-tone", "ok");
    expect(within(director as HTMLElement).getByText("cleared")).toBeInTheDocument();
  });

  it("reports left-out incoming items the way the footer sentence does and skips empty lists", () => {
    const withEmpty: DiffField[] = [
      ...fields,
      { key: "urls", label: "URLs", kind: "list", itemKey: (value) => String(value) },
    ];
    const selection: DiffSelection = { ...defaultDiffSelection(withEmpty, source, target), tags: ["outdoors"] };
    render(
      <MetadataDiffSummary
        fields={withEmpty}
        source={{ ...source, values: { ...source.values, urls: [] } }}
        target={{ ...target, values: { ...target.values, urls: [] } }}
        value={selection}
        onChange={vi.fn()}
      />,
    );
    const tags = screen.getByText("Tags").closest("[data-tone]")!;
    expect(tags).toHaveAttribute("data-tone", "same");
    // A new item nobody chose to create was never on its way in, so it is listed apart rather than as left out.
    expect(within(tags as HTMLElement).getByText("1 left out · 1 not in your library")).toBeInTheDocument();
    expect(
      within(tags as HTMLElement)
        .getByText("Wet Look")
        .closest("[data-state]"),
    ).toHaveAttribute("data-state", "not-in-library");
    expect(
      within(tags as HTMLElement)
        .getByText("Poolside")
        .closest("[data-state]"),
    ).toHaveAttribute("data-state", "left-out");
    expect(screen.getByText("Unchanged").closest("[data-tone]")).not.toHaveTextContent("URLs");
    expect(screen.queryByText("URLs")).not.toBeInTheDocument();
  });
});
