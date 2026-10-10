import { useState } from "react";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import {
  MetadataDiff,
  defaultDiffSelection,
  scalarStatus,
  summarizeDiff,
  type DiffField,
  type DiffRecord,
} from "../components/MetadataDiff";

const fields: DiffField[] = [
  { key: "title", label: "Title" },
  { key: "description", label: "Description" },
  { key: "empty", label: "Empty field" },
  { key: "missing", label: "Missing field" },
  { key: "same", label: "Same field" },
  { key: "fill", label: "Fillable field" },
  { key: "tags", label: "Tags", kind: "list", itemKey: (item) => String(item).toLowerCase() },
  { key: "cover", label: "Cover", render: (value) => <img alt="Custom cover" src={String(value)} /> },
];
// `source` is the incoming side, `target` the kept side.
const source: DiffRecord = {
  label: "Scraped",
  values: {
    title: "Scraped title",
    description: "Scraped description",
    empty: null,
    same: "Same",
    fill: "Filled in",
    tags: ["Shared", "New"],
    cover: "/source.png",
  },
  provenance: { title: "StashDB" },
};
const target: DiffRecord = {
  label: "Library",
  values: {
    title: "Library title",
    description: "Library description",
    empty: "Populated",
    missing: "Present",
    same: "Same",
    fill: null,
    tags: ["shared", "Existing"],
    cover: "/target.png",
  },
};
function Harness() {
  const [value, onChange] = useState(() => defaultDiffSelection(fields, source, target));
  return <MetadataDiff {...{ fields, source, target, value, onChange }} />;
}
describe("MetadataDiff", () => {
  it("keeps conflicts, fills empty fields from the incoming side, and lets each field be chosen", () => {
    render(<Harness />);
    expect(screen.getByLabelText("Title from target")).toBeChecked();
    expect(screen.getByLabelText("Fillable field from source")).toBeChecked();
    expect(screen.getByLabelText("Empty field from target")).toBeChecked();
    fireEvent.click(screen.getByLabelText("Title from source"));
    fireEvent.click(screen.getByLabelText("Empty field from source"));
    expect(screen.getByLabelText("Title from source")).toBeChecked();
    expect(screen.getByLabelText("Description from target")).toBeChecked();
    expect(screen.getByLabelText("Empty field from source")).toBeChecked();
    expect(screen.getByLabelText("Missing field from source")).toBeDisabled();
    expect(within(screen.getByRole("group", { name: "Title" })).getByText("StashDB")).toBeInTheDocument();
    expect(scalarStatus(fields[0], source, target)).toBe("conflict");
    expect(scalarStatus(fields[5], source, target)).toBe("filled");
    expect(scalarStatus(fields[4], source, target)).toBe("identical");
  });
  it("labels the outcome of every visible row", () => {
    render(<Harness />);
    expect(
      within(screen.getByRole("group", { name: "Title" })).getAllByText("Conflict · keeping library"),
    ).not.toHaveLength(0);
    expect(
      within(screen.getByRole("group", { name: "Fillable field" })).getAllByText("Filled from scraped"),
    ).not.toHaveLength(0);
    fireEvent.click(screen.getByLabelText("Title from source"));
    expect(within(screen.getByRole("group", { name: "Title" })).getAllByText("Replaced from scraped")).not.toHaveLength(
      0,
    );
  });
  it("shows list items as chips and allows leaving out shared and unique items", () => {
    render(<Harness />);
    expect(screen.getByRole("button", { name: "Remove Tags: shared" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: new" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: existing" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove Tags: shared" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove Tags: new" }));
    expect(screen.getByRole("button", { name: "Add Tags: shared" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: existing" })).toBeInTheDocument();
    const group = screen.getByRole("group", { name: "Tags" });
    expect(group.querySelectorAll('[data-state="excluded"]')).toHaveLength(2);
    expect(group.querySelectorAll('[data-state="kept"]')).toHaveLength(1);
  });
  it("applies combined, kept-only and incoming-only presets", () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: "Use source Tags" }));
    expect(screen.getByRole("button", { name: "Remove Tags: shared" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: new" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add Tags: existing" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Use target Tags" }));
    expect(screen.getByRole("button", { name: "Add Tags: new" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove Tags: existing" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Use combined Tags" }));
    expect(screen.getByRole("button", { name: "Remove Tags: new" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Use combined Tags" })).toHaveAttribute("aria-pressed", "true");
  });
  it("collapses identical fields and keeps always-visible rows", () => {
    render(<Harness />);
    expect(screen.queryByRole("group", { name: "Same field" })).not.toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Cover" })).toBeInTheDocument();
    expect(screen.getAllByAltText("Custom cover")).toHaveLength(2);
    fireEvent.click(screen.getByRole("button", { name: /1 identical/ }));
    expect(screen.getByRole("group", { name: "Same field" })).toBeInTheDocument();
  });
  it("supports sentence labels, will-create chips, mode-only lists and locked kept items", () => {
    const applyFields: DiffField[] = [
      {
        key: "tags",
        label: "Tags",
        kind: "list",
        itemKey: (item) => String(item).toLowerCase(),
        itemIsNew: (item) => item === "Brand new",
        lockKeptItems: true,
      },
      { key: "urls", label: "URLs", kind: "list", itemKey: (item) => String(item), modesOnly: true },
    ];
    const incoming: DiffRecord = {
      label: "From StashDB",
      sentenceLabel: "StashDB",
      values: { tags: ["Shared", "Brand new"], urls: ["https://a"] },
    };
    const current: DiffRecord = {
      label: "Current",
      sentenceLabel: "current",
      values: { tags: ["shared", "Existing"], urls: ["https://b"] },
    };
    function ApplyHarness() {
      const [value, onChange] = useState(() => defaultDiffSelection(applyFields, incoming, current));
      return <MetadataDiff {...{ fields: applyFields, source: incoming, target: current, value, onChange }} />;
    }
    render(<ApplyHarness />);
    const tags = screen.getByRole("group", { name: "Tags" });
    expect(tags.querySelector('[data-state="new"]')).toHaveTextContent("Brand new");
    expect(within(tags).getByText(/1 added \(1 new\)/)).toBeInTheDocument();
    expect(within(tags).queryByRole("button", { name: "Remove Tags: existing" })).not.toBeInTheDocument();
    expect(within(tags).getByRole("button", { name: "Remove Tags: brand new" })).toBeInTheDocument();
    expect(within(tags).getByRole("button", { name: "Use source Tags" })).toHaveTextContent("Only StashDB");
    expect(within(tags).getByRole("button", { name: "Use target Tags" })).toHaveTextContent("Only current");
    const urls = screen.getByRole("group", { name: "URLs" });
    expect(within(urls).queryByRole("button", { name: /Remove URLs/ })).not.toBeInTheDocument();
    expect(within(urls).getByRole("button", { name: "Use combined URLs" })).toBeInTheDocument();
    const summary = summarizeDiff(applyFields, incoming, current, defaultDiffSelection(applyFields, incoming, current));
    expect(summary.changes.map((change) => change.text)).toEqual(["1 tag added (1 new)", "1 url added"]);
  });

  describe("a list changed item by item", () => {
    const tagField = (overrides: Partial<DiffField> = {}): DiffField => ({
      key: "tags",
      label: "Tags",
      kind: "list",
      itemKey: (item) => String(item).toLowerCase(),
      ...overrides,
    });
    // "Shared" and "New" come in and "shared" and "Existing" are kept; "shared" alone matches no preset.
    const renderCustomised = (field: DiffField) =>
      render(
        <MetadataDiff
          fields={[field]}
          source={source}
          target={target}
          value={{ tags: ["shared"] }}
          onChange={() => {}}
        />,
      );
    const pressed = (name: string) => screen.getByRole("button", { name }).getAttribute("aria-pressed");

    it("presses the owner's mode, saying that clicking it undoes the changes", () => {
      renderCustomised(tagField({ activeMode: "source" }));
      expect(pressed("Use source Tags")).toBe("true");
      expect(pressed("Use combined Tags")).toBe("false");
      expect(screen.getByRole("button", { name: "Use source Tags" })).toHaveAttribute(
        "title",
        "Only scraped, with your changes. Click to undo them.",
      );
    });

    it("presses the owner's mode even when the changes happen to equal another preset", () => {
      // Every incoming item taken off one by one leaves exactly the kept side, which is "Only current",
      // but the owner is still combining.
      render(
        <MetadataDiff
          fields={[tagField({ activeMode: "combined" })]}
          source={source}
          target={target}
          value={{ tags: ["shared", "existing"] }}
          onChange={() => {}}
        />,
      );
      expect(pressed("Use combined Tags")).toBe("true");
      expect(pressed("Use target Tags")).toBe("false");
      expect(screen.getByRole("button", { name: "Use combined Tags" })).toHaveAttribute(
        "title",
        "Combine, with your changes. Click to undo them.",
      );
    });

    it("presses nothing for an owner without a mode", () => {
      renderCustomised(tagField());
      for (const name of ["Use combined Tags", "Use target Tags", "Use source Tags"]) {
        expect(pressed(name)).toBe("false");
        expect(screen.getByRole("button", { name })).not.toHaveAttribute("title");
      }
    });
  });

  it("holds a waiting field, leaves the others usable, and keeps it out of the summary", () => {
    const waiting = fields.map((field) =>
      field.key === "tags" || field.key === "description" ? { ...field, waiting: { text: "Checking…" } } : field,
    );
    const value = defaultDiffSelection(waiting, source, target);
    render(<MetadataDiff fields={waiting} source={source} target={target} value={value} onChange={() => {}} />);
    for (const label of ["Tags", "Description"]) {
      const row = screen.getByRole("group", { name: label });
      expect(row).toHaveAttribute("aria-busy", "true");
      expect(within(row).getAllByText("Checking…").length).toBeGreaterThan(0);
    }
    expect(screen.getByRole("button", { name: "Remove Tags: new" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Use combined Tags" })).toBeDisabled();
    expect(screen.getByLabelText("Description from source")).toBeDisabled();
    expect(screen.getByLabelText("Title from source")).toBeEnabled();
    const texts = summarizeDiff(waiting, source, target, value).changes.map((change) => change.text);
    expect(texts.join(" ")).not.toMatch(/tag|Description/i);
  });

  it("does not read a field whose check failed as still in progress", () => {
    const failed = fields.map((field) =>
      field.key === "tags" ? { ...field, waiting: { text: "Not checked", failed: true } } : field,
    );
    const value = defaultDiffSelection(failed, source, target);
    render(<MetadataDiff fields={failed} source={source} target={target} value={value} onChange={() => {}} />);
    const row = screen.getByRole("group", { name: "Tags" });
    expect(row).not.toHaveAttribute("aria-busy");
    expect(row.querySelector(".animate-spin")).toBeNull();
    expect(within(row).getByText("Not checked")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Use combined Tags" })).toBeDisabled();
  });

  it("summarises what the selection will do", () => {
    const value = defaultDiffSelection(fields, source, target);
    const summary = summarizeDiff(fields, source, target, value);
    expect(summary.changeCount).toBe(2);
    expect(summary.changes.map((change) => change.text)).toEqual([
      "Fillable field filled from scraped",
      "1 tag added",
      "Title, Description, Cover kept from library (conflict)",
      "3 unchanged",
    ]);
  });
});
